using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Isolated tests for <see cref="SyncIntervalTracker"/> (porting legacy's <c>CSYNCINT</c>), checked
/// against synthetic peak sequences before this class is wired into any of the three usage sites
/// legacy has for it (<c>m_sint1</c>/<c>m_sint2</c>/<c>m_sint3</c>) — that wiring is separately
/// scoped, later work.
/// </summary>
public class SyncIntervalTrackerTests
{
    private const int SampleRate = 44100;

    [Fact]
    public void PeriodicPeaksMatchingModeInterval_EventuallyMatch()
    {
        var candidates = SstvModeRegistry.GetSyncIntervalCandidates(SampleRate);
        var tracker = new SyncIntervalTracker(SampleRate, isNarrow: false, candidates);

        var mode = SstvModeRegistry.ScottieS1; // default group, depth 5 -- needs 3 consecutive matches
        var intervalSamples = (int)Math.Round(mode.LineDurationMs / 1000.0 * SampleRate);

        var matched = FeedPeriodicPeaks(tracker, intervalSamples, lineCount: 6);

        Assert.Equal(mode.Id, matched?.Id);
    }

    [Fact]
    public void TooFewConsecutivePeaks_NeverMatches()
    {
        var candidates = SstvModeRegistry.GetSyncIntervalCandidates(SampleRate);
        var tracker = new SyncIntervalTracker(SampleRate, isNarrow: false, candidates);

        var mode = SstvModeRegistry.ScottieS1;
        var intervalSamples = (int)Math.Round(mode.LineDurationMs / 1000.0 * SampleRate);

        // Depth-5 (default group) needs 3 consecutive matches; only feed 2.
        var matched = FeedPeriodicPeaks(tracker, intervalSamples, lineCount: 2);

        Assert.Null(matched);
    }

    [Fact]
    public void NonPeriodicPeaks_NeverMatch()
    {
        var candidates = SstvModeRegistry.GetSyncIntervalCandidates(SampleRate);
        var tracker = new SyncIntervalTracker(SampleRate, isNarrow: false, candidates);

        var random = new Random(42);
        SstvModeDefinition? matched = null;
        for (var line = 0; line < 20 && matched is null; line++)
        {
            // Random interval each "line", nowhere near consistent -- should never build up enough
            // consecutive matches for any candidate.
            var interval = random.Next(2000, 40000);
            matched = FeedOnePeak(tracker, interval);
        }

        Assert.Null(matched);
    }

    [Fact]
    public void DoubleInterval_MatchesViaSubharmonicCheck()
    {
        // Legacy checks the *measured* interval AND its 1/2, 1/3 subharmonics (sstv.cpp:1342) --
        // i.e. it divides down, not up. So this handles the case where every *other* peak was
        // missed (measured interval = 2x the mode's true interval), not a faster-repeating mode --
        // an earlier version of this test fed half the interval and expected a match, backwards from
        // what the division direction actually supports; caught by the test failing against the
        // (correct) implementation, not an implementation bug.
        var candidates = SstvModeRegistry.GetSyncIntervalCandidates(SampleRate);
        var tracker = new SyncIntervalTracker(SampleRate, isNarrow: false, candidates);

        var mode = SstvModeRegistry.ScottieS1;
        var doubleIntervalSamples = (int)Math.Round(mode.LineDurationMs / 1000.0 * SampleRate * 2.0);

        var matched = FeedPeriodicPeaks(tracker, doubleIntervalSamples, lineCount: 6);

        Assert.Equal(mode.Id, matched?.Id);
    }

    [Fact]
    public void Sc260AndSc2120_NeverMatchEvenWithPerfectInterval()
    {
        // SyncCheckSub unconditionally excludes SC2-60/120 (sstv.cpp:1296-1298) -- this mechanism
        // must never recognize them, no matter how clean the input.
        var candidates = SstvModeRegistry.GetSyncIntervalCandidates(SampleRate);

        foreach (var modeId in new[] { "sc2-60", "sc2-120" })
        {
            var tracker = new SyncIntervalTracker(SampleRate, isNarrow: false, candidates);
            var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);
            var intervalSamples = (int)Math.Round(mode.LineDurationMs / 1000.0 * SampleRate);

            var matched = FeedPeriodicPeaks(tracker, intervalSamples, lineCount: 10);

            Assert.Null(matched);
        }
    }

    [Fact]
    public void NarrowTracker_OnlyMatchesNarrowFamilyModes()
    {
        // A tracker running in narrow mode should never match a normal-band mode's interval, even
        // if the timing happens to line up, and vice versa (GetSyncIntervalMatchDepth's band gating).
        // Robot36 (150ms), not Scottie S1 (428.22ms): an earlier version of this test used Scottie
        // S1, whose duration happens to fall within 3ms of MC110's (428.5ms) -- a coincidental
        // collision in the test's own chosen data, not an implementation bug, caught by the test
        // failing against the (correct) implementation.
        var candidates = SstvModeRegistry.GetSyncIntervalCandidates(SampleRate);
        var narrowTracker = new SyncIntervalTracker(SampleRate, isNarrow: true, candidates);

        var normalMode = SstvModeRegistry.Robot36;
        var intervalSamples = (int)Math.Round(normalMode.LineDurationMs / 1000.0 * SampleRate);

        var matched = FeedPeriodicPeaks(narrowTracker, intervalSamples, lineCount: 10);

        Assert.Null(matched);
    }

    [Fact]
    public void NarrowTracker_MatchesNarrowFamilyMode()
    {
        var candidates = SstvModeRegistry.GetSyncIntervalCandidates(SampleRate);
        var tracker = new SyncIntervalTracker(SampleRate, isNarrow: true, candidates);

        var mode = SstvModeRegistry.Mn73; // narrow group, depth 3 -- needs 5 consecutive matches
        var intervalSamples = (int)Math.Round(mode.LineDurationMs / 1000.0 * SampleRate);

        var matched = FeedPeriodicPeaks(tracker, intervalSamples, lineCount: 8);

        Assert.Equal(mode.Id, matched?.Id);
    }

    // Closes a coverage gap flagged by Tier A Batch 5 chunk 5b (docs/functional-audit-playbook.md):
    // GetSyncIntervalMatchDepth's 4-group + narrow-gating table (sstv.cpp:1290-1325) had no direct
    // per-mode test -- only Robot 36 and generic band-gating were covered indirectly.
    public static IEnumerable<object?[]> AllModesExceptAvtWithExpectedMatchDepth()
    {
        int? Depth(SstvModeDefinition mode, bool isNarrow)
        {
            if (mode == SstvModeRegistry.Sc260 || mode == SstvModeRegistry.Sc2120)
            {
                return null;
            }

            if (mode == SstvModeRegistry.R24 || mode == SstvModeRegistry.Robot36 || mode == SstvModeRegistry.MartinM2
                || mode == SstvModeRegistry.Pd50 || mode == SstvModeRegistry.Pd240)
            {
                return isNarrow ? null : 4;
            }

            if (mode == SstvModeRegistry.Rm8 || mode == SstvModeRegistry.Rm12)
            {
                return isNarrow ? null : 0;
            }

            if (mode == SstvModeRegistry.Mn73 || mode == SstvModeRegistry.Mn110 || mode == SstvModeRegistry.Mn140
                || mode == SstvModeRegistry.Mc110 || mode == SstvModeRegistry.Mc140 || mode == SstvModeRegistry.Mc180)
            {
                return isNarrow ? 3 : null;
            }

            return isNarrow ? null : 5;
        }

        foreach (var mode in SstvModeRegistry.All.Where(m => m != SstvModeRegistry.Avt))
        {
            yield return new object?[] { mode, false, Depth(mode, false) };
            yield return new object?[] { mode, true, Depth(mode, true) };
        }
    }

    [Theory]
    [MemberData(nameof(AllModesExceptAvtWithExpectedMatchDepth))]
    public void GetSyncIntervalMatchDepth_MatchesLegacySyncCheckSubGrouping(SstvModeDefinition mode, bool isNarrow, int? expectedDepth)
    {
        Assert.Equal(expectedDepth, SstvModeRegistry.GetSyncIntervalMatchDepth(mode, isNarrow));
    }

    private static SstvModeDefinition? FeedPeriodicPeaks(SyncIntervalTracker tracker, int intervalSamples, int lineCount)
    {
        SstvModeDefinition? matched = null;
        for (var line = 0; line < lineCount && matched is null; line++)
        {
            matched = FeedOnePeak(tracker, intervalSamples);
        }

        return matched;
    }

    private static SstvModeDefinition? FeedOnePeak(SyncIntervalTracker tracker, int intervalSamples)
    {
        var peakAt = intervalSamples / 2;
        for (var i = 0; i < intervalSamples; i++)
        {
            tracker.Increment();
            if (i == peakAt)
            {
                tracker.UpdateMax(1000);
            }
        }

        return tracker.TryStart();
    }
}
