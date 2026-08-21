namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Isolated tests for <see cref="SyncEnvelopeDetector"/>'s <c>bandwidthHz</c> parameter, added to
/// support the VIS-bit tone-race detectors (1080Hz/1320Hz at 80Hz bandwidth, `sstv.cpp:1446/1448`)
/// this port's <c>VisLockStateMachine</c> piece needs -- every existing instance (AFC, Slant,
/// <c>m_sint2</c>/<c>m_sint3</c>) stays on the default 100Hz bandwidth (`sstv.cpp:1447/1449/1450`),
/// so the default-value behavior must not change.
/// </summary>
public class SyncEnvelopeDetectorTests
{
    private const int SampleRate = 44100;

    [Fact]
    public void NarrowBandwidthDetector_RespondsMoreStronglyToOnFrequencyTone()
    {
        // Both instances are tuned to the SAME 1080Hz center -- this compares one filter's on-freq
        // vs off-freq response, which is the test's real intent. (A prior version of this test
        // accidentally used a correctly-tuned but confusingly-named second variable here; renamed,
        // not retuned -- retuning it to 1320Hz would instead compare two DIFFERENT filters' each-own
        // on-frequency response to each other, which isn't what "on-frequency tone" means and isn't
        // what NarrowBandwidth_IsMoreSelectiveThanTheDefault below tests either.)
        var onFrequencyDetector = new SyncEnvelopeDetector(SampleRate, 1080.0, bandwidthHz: 80.0);
        var offFrequencyDetector = new SyncEnvelopeDetector(SampleRate, 1080.0, bandwidthHz: 80.0);

        var onFrequencyEnvelope = FeedTone(onFrequencyDetector, 1080.0, durationMs: 30);
        var offFrequencyEnvelope = FeedTone(offFrequencyDetector, 1320.0, durationMs: 30);

        Assert.True(onFrequencyEnvelope > offFrequencyEnvelope,
            $"On-frequency envelope {onFrequencyEnvelope:F4} should exceed off-frequency envelope {offFrequencyEnvelope:F4}.");
    }

    [Fact]
    public void NarrowBandwidth_IsMoreSelectiveThanTheDefault()
    {
        // Closes a coverage gap flagged by Tier A Batch 6 chunk 6a (docs/functional-audit-playbook.md):
        // the test above constructed BOTH detectors at 80Hz, so it never actually exercised the
        // bandwidthHz parameter's own effect (an 80-vs-100 swap in the constructor call would pass
        // it unchanged). sstv.cpp:1446/1448 use 80Hz (m_iir11/m_iir13) vs sstv.cpp:1447/1449/1450's
        // 100Hz (m_iir12/m_iir19/m_iirfsk) -- pin the actual consequence of that difference.
        // CIIRTANK's a0 scales with bw (fir.cpp:56), so absolute gains differ; only the off/on ratio
        // is a fair comparison between the two bandwidths.
        const double offsetHz = 120.0;
        var narrowOn = FeedTone(new SyncEnvelopeDetector(SampleRate, 1080.0, bandwidthHz: 80.0), 1080.0, durationMs: 200);
        var narrowOff = FeedTone(new SyncEnvelopeDetector(SampleRate, 1080.0, bandwidthHz: 80.0), 1080.0 + offsetHz, durationMs: 200);
        var wideOn = FeedTone(new SyncEnvelopeDetector(SampleRate, 1080.0, bandwidthHz: 100.0), 1080.0, durationMs: 200);
        var wideOff = FeedTone(new SyncEnvelopeDetector(SampleRate, 1080.0, bandwidthHz: 100.0), 1080.0 + offsetHz, durationMs: 200);

        Assert.True(narrowOff / narrowOn < wideOff / wideOn,
            $"80Hz BW should reject {offsetHz}Hz off-tune harder than 100Hz: narrow={narrowOff / narrowOn:F5}, wide={wideOff / wideOn:F5}");
    }

    [Fact]
    public void ToneRaceDetectors_DiscriminateBetween1080And1320Hz()
    {
        var detector1080 = new SyncEnvelopeDetector(SampleRate, 1080.0, bandwidthHz: 80.0);
        var detector1320 = new SyncEnvelopeDetector(SampleRate, 1320.0, bandwidthHz: 80.0);

        var d11 = FeedTone(detector1080, 1080.0, durationMs: 30);
        var d13 = FeedTone(detector1320, 1080.0, durationMs: 30);

        Assert.True(d11 > d13, $"1080Hz detector ({d11:F4}) should read higher than 1320Hz detector ({d13:F4}) for a 1080Hz tone.");
    }

    [Fact]
    public void DefaultBandwidth_MatchesExplicit100HzConstruction()
    {
        var implicitDetector = new SyncEnvelopeDetector(SampleRate, 1200.0);
        var explicitDetector = new SyncEnvelopeDetector(SampleRate, 1200.0, bandwidthHz: 100.0);

        var random = new Random(7);
        for (var i = 0; i < 200; i++)
        {
            var sample = random.NextDouble() * 2 - 1;
            Assert.Equal(explicitDetector.ProcessSample(sample), implicitDetector.ProcessSample(sample), precision: 12);
        }
    }

    [Fact]
    public void Retune_ActuallyMovesTheResonator_NotJustTheObservationHook()
    {
        // Closes a coverage gap flagged by Tier A Batch 6 chunk 6a: no test pinned that Retune has
        // any DSP effect at all -- AfcTests.cs only reads AppliedCenterFrequencyHzForTests, a value
        // Retune assigns on its own line, independent of the actual _resonator.SetFreq call. Deleting
        // that SetFreq call, or inverting only its sign, previously survived the entire suite -- the
        // exact bug class this file's own Retune caller already shipped once (sign inversion,
        // AnalogFmSstvDecoder.cs's AFC correction call site).
        const double offsetHz = 60.0;

        var retuned = new SyncEnvelopeDetector(SampleRate, 1200.0);
        retuned.Retune(offsetHz);
        var nominal = new SyncEnvelopeDetector(SampleRate, 1200.0);
        var mirrored = new SyncEnvelopeDetector(SampleRate, 1200.0);
        mirrored.Retune(offsetHz);

        var retunedAtNewCentre = FeedTone(retuned, 1200.0 + offsetHz, durationMs: 200);
        var nominalAtNewCentre = FeedTone(nominal, 1200.0 + offsetHz, durationMs: 200);
        var retunedAtMirrorTone = FeedTone(mirrored, 1200.0 - offsetHz, durationMs: 200);

        Assert.True(retunedAtNewCentre > nominalAtNewCentre * 1.2,
            $"retune had no effect on the filter: retuned={retunedAtNewCentre:F5}, nominal={nominalAtNewCentre:F5}");
        Assert.True(retunedAtNewCentre > retunedAtMirrorTone * 1.2,
            $"retune direction looks sign-inverted: +offset={retunedAtNewCentre:F5}, -offset={retunedAtMirrorTone:F5}");
        Assert.Equal(1260.0, retuned.AppliedCenterFrequencyHzForTests, tolerance: 1e-9);
    }

    [Fact]
    public void Retune_IsAbsoluteFromTheConstructedCentre_NotCumulative()
    {
        // Legacy's InitTone always computes 1200+dfq from nominal (sstv.cpp:1699), never compounding
        // a prior retune's own offset into the next one.
        var detector = new SyncEnvelopeDetector(SampleRate, 1200.0);
        detector.Retune(40.0);
        detector.Retune(-15.0);
        Assert.Equal(1185.0, detector.AppliedCenterFrequencyHzForTests, tolerance: 1e-9);
    }

    [Fact]
    public void Retune_DoesNotResetFilterState()
    {
        // Legacy's InitTone (sstv.cpp:1695-1705) only calls SetFreq; m_iir12/m_lpf12's z-state
        // persists across retunes (never Clear()'d). Retune(0.0) recomputes identical coefficients,
        // so a state reset is the ONLY thing that could make the retuned instance's subsequent output
        // diverge from an untouched twin fed the identical input.
        var retuned = new SyncEnvelopeDetector(SampleRate, 1200.0);
        var untouched = new SyncEnvelopeDetector(SampleRate, 1200.0);
        FeedTone(retuned, 1200.0, durationMs: 100);
        FeedTone(untouched, 1200.0, durationMs: 100);

        retuned.Retune(0.0);
        for (var i = 0; i < 64; i++)
        {
            Assert.Equal(untouched.ProcessSample(0.0), retuned.ProcessSample(0.0), precision: 12);
        }
    }

    private static double FeedTone(SyncEnvelopeDetector detector, double frequencyHz, double durationMs)
    {
        var sampleCount = (int)(durationMs / 1000.0 * SampleRate);
        double envelope = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = Math.Sin(2 * Math.PI * frequencyHz * i / SampleRate);
            envelope = detector.ProcessSample(sample);
        }

        return envelope;
    }
}
