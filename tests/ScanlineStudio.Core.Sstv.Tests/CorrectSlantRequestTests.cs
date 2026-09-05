using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// RX buffer subsystem Phase 8, sub-piece (c) -- tests for <see cref="ISstvDecoder.RequestCorrectSlant"/>'s
/// own deferred-request plumbing (the drain point inside <c>TryProcessBuffer</c>'s per-line loop, the
/// stale-request clear at a fresh lock, subsuming <c>_pendingReplayRequested</c>, and
/// <see cref="RestartableSstvDecoder"/>'s forwarding) -- driven through REAL encoded audio, not the
/// synthetic staging-buffer injection <c>CorrectSlantTests.cs</c> uses for the algorithm itself
/// (<see cref="AnalogFmSstvDecoder.TryCorrectSlant"/> is already proven correct there; the drain can
/// only be exercised by actually running <c>TryProcessBuffer</c>'s real per-line loop, which the
/// synthetic-injection technique deliberately bypasses).
/// </summary>
public class CorrectSlantRequestTests
{
    [Fact]
    public void RequestCorrectSlant_NoModeLockedYet_IsASafeNoOp()
    {
        // Guard-clause coverage, mirroring ReplayEngineTests' own PerformReplay_NoModeLockedYet_IsASafeNoOp:
        // the drain site lives inside TryProcessBuffer's per-line loop, which never runs at all before
        // any mode locks -- so a request made before that point just sits, unconsumed, harmlessly.
        var decoder = new AnalogFmSstvDecoder(11025, rxBufferMode: RxBufferMode.On);
        decoder.RequestCorrectSlant();
        decoder.PushSamples(new float[64]); // idle silence -- never locks
        Assert.Null(decoder.ModeForTests);
    }

    [Fact]
    public void RequestCorrectSlant_WithAutoSlantDisabled_FiresARealCorrectionOnlyTheRequestCouldHaveCaused()
    {
        // Isolation strategy: with autoSlantEnabled: false, the CONTINUOUS per-line tracker never
        // commits a correction on its own (AnalogFmSstvDecoder.cs's own ProcessSlantTrackingSample
        // routes `!_autoSlantEnabled` into ProcessLineHistoryOnly, which never writes
        // _effectiveSamplesPerLine -- see that call site's own doc comment) -- so under a real clock
        // mismatch, _effectiveSamplesPerLine can ONLY move via this test's own RequestCorrectSlant()
        // call reaching the new drain point and a real TryCorrectSlant() commit. This is the same
        // 500ppm mismatch technique ReplayEngineTests' own automatic-trigger tests use, just with the
        // automatic path structurally disabled instead of merely suppressed.
        var mode = SstvModeRegistry.Robot36;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(230, 230, 230));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        const int declaredSampleRate = 44100;
        const int trueSampleRate = (int)(declaredSampleRate * 1.0005);
        var samples = Encode(mode, sourceImage, trueSampleRate);

        var decoder = new AnalogFmSstvDecoder(declaredSampleRate, autoSlantEnabled: false, rxBufferMode: RxBufferMode.On);

        const int chunkSize = 256;
        var offset = 0;
        var requested = false;

        // Stops shortly after the request lands, deliberately NOT decoding the whole transmission --
        // once the image completes, EndOfImage resets _mode (and therefore SlantPpm, which reads
        // through it) back to null regardless of whether locking/decoding/correcting succeeded, which
        // would make the SlantPpm assertion below fail for a reason having nothing to do with the fix
        // under test (the exact mistake an earlier version of a sibling test in this file made -- see
        // RequestCorrectSlant_StaleRequestFromAbandonedImage_IsClearedAtTheNextFreshLock's own history).
        // Buffered-replay fix (§15 item 2): swapped from the old "_rxBufferBaseTransmissionLine == 0"
        // oracle (now always true regardless of whether replay ran, since the staging buffer is never
        // truncated/re-based) to ReplayPassCountForTests.
        while (offset < samples.Length && (!requested || decoder.ReplayPassCountForTests == 0))
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
            offset += length;

            // Request as soon as the entry gate's own 16-line minimum could plausibly be met, then stop
            // requesting -- a single request is enough to prove the drain fires; requesting every chunk
            // would just mean "eventually," not "because of this one call."
            if (!requested && decoder.RxLineStagingBufferForTests is { LineCount: >= 16 })
            {
                decoder.RequestCorrectSlant();
                requested = true;
            }
        }

        Assert.True(requested, "Test setup problem: never staged 16 lines to request against.");
        var nominalLineWidth = mode.LineDurationMs / 1000.0 * declaredSampleRate;
        Assert.NotEqual(nominalLineWidth, decoder.EffectiveSamplesPerLineForTests);
        Assert.True(decoder.ReplayPassCountForTests > 0, "PerformReplay never ran.");

        // Auditor code-review finding, Phase 8c round 1 (blocker, fixed): a manual commit that only
        // writes _effectiveSamplesPerLine, without also syncing the tracker's own rate pair, leaves
        // SlantPpm (public, ISstvDecoder.SlantPpm's own backing read) reporting the PRE-manual rate
        // forever -- and, worse, leaves the tracker's own internal baseline stale for its NEXT
        // automatic commit to silently revert this correction against (see SlantTests.cs's own
        // SlantTracker_AdoptCorrectedRate_FirstNaturalCorrectionUsesTheAdoptedBaseline for that half).
        // Batch 2 chunk 2b round-1 fix: the manual path now calls SlantTracker.RestoreRateWithoutReset
        // (not AdoptCorrectedRate) so a subsequent revert doesn't also wipe the tracker's baseline/
        // history -- see RestoreRateWithoutReset's own doc comment. This assertion directly proves the
        // sync happened: SlantPpm must reflect the SAME corrected rate _effectiveSamplesPerLine now
        // holds, derived independently via the mode's own LineDurationMs (not by re-reading
        // _effectiveSamplesPerLine's own value back at itself, which would trivially match even if the
        // rate sync were never called).
        var committedSampleRate = decoder.EffectiveSamplesPerLineForTests / (mode.LineDurationMs / 1000.0);
        var expectedPpm = (committedSampleRate - declaredSampleRate) * 1_000_000.0 / declaredSampleRate;
        Assert.NotNull(decoder.SlantPpm);
        Assert.Equal(expectedPpm, decoder.SlantPpm!.Value, precision: 6);
    }

    // RequestCorrectSlant_WithAutoSlantEnabled_TheNextAutomaticCommitDoesNotRevertIt (a real-audio
    // integration attempt at this exact regression) was removed after round-2 auditor code-review:
    // its reference ppm was captured from the FIRST post-request LineDecoded, which fires before the
    // manual drain runs in that same iteration -- so it measured the PRE-manual (automatic-only) rate,
    // not the post-manual one, making the whole assertion pass unconditionally regardless of whether
    // AdoptCorrectedRate ran at all. It also had no positive control that the manual commit actually
    // landed, and no guarantee a further automatic commit even follows a good manual one (residual
    // drift after a real correction routinely stays under every ladder threshold, so there may be
    // nothing to revert). A deterministic, fast unit-level equivalent that directly exercises both
    // field writes SlantTracker.AdoptCorrectedRate makes (_currentSampleRate/_nominalSamplesPerLine --
    // not its trailing Reset() call, which a virgin tracker can't discriminate) now lives in
    // SlantTests.cs (SlantTracker_AdoptCorrectedRate_FirstNaturalCorrectionUsesTheAdoptedBaseline)
    // instead of fighting real-audio commit timing for the same proof.

    [Fact]
    public void RequestCorrectSlant_SuppressAutomaticReplayForTests_DoesNotGateTheManualPath()
    {
        // Design point 1's own explicit decision: SuppressAutomaticReplayForTests exists to isolate the
        // AUTOMATIC tracker's own replay behavior for testing, not to gate every replay path -- the
        // manual Correct-Slant request must still fire PerformReplay with it set. Auto Slant stays
        // ENABLED here (unlike the test above) specifically so this scenario could ALSO be satisfied by
        // the automatic commit trigger alone if the suppression flag were (wrongly) gating that path
        // too instead of just its own PerformReplay call -- the discriminator is that suppression is
        // set, so if replay only ever happened through the (still-open) automatic commit trigger's own
        // gate, this would still pass for the wrong reason. What actually proves the manual path: a
        // request issued, then AutoSlant's own automatic PerformReplay call suppressed for the REST of
        // the run, with a correction still landing.
        var mode = SstvModeRegistry.Robot36;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(230, 230, 230));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        const int declaredSampleRate = 44100;
        const int trueSampleRate = (int)(declaredSampleRate * 1.0005);
        var samples = Encode(mode, sourceImage, trueSampleRate);

        var decoder = new AnalogFmSstvDecoder(declaredSampleRate, rxBufferMode: RxBufferMode.On)
        {
            SuppressAutomaticReplayForTests = true,
        };

        const int chunkSize = 256;
        var offset = 0;
        var requested = false;

        while (offset < samples.Length)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
            offset += length;

            if (!requested && decoder.RxLineStagingBufferForTests is { LineCount: >= 16 })
            {
                decoder.RequestCorrectSlant();
                requested = true;
            }
        }

        Assert.True(requested, "Test setup problem: never staged 16 lines to request against.");
        // Buffered-replay fix (§15 item 2): swapped from the old "_rxBufferBaseTransmissionLine" oracle
        // (now always 0, since the staging buffer is never truncated/re-based) to ReplayPassCountForTests.
        Assert.True(decoder.ReplayPassCountForTests > 0, "The manual request's own PerformReplay call should not be gated by SuppressAutomaticReplayForTests.");
    }

    [Fact]
    public void RequestCorrectSlant_StaleRequestFromAbandonedImage_IsClearedAtTheNextFreshLock()
    {
        // Mirrors _pendingReplayRequested's own established stale-request test shape: a request made
        // and never drained (no mode ever locked, matching the no-op test above) must not survive into
        // a LATER, unrelated lock -- InitializeSlant clears _correctSlantRequested unconditionally at
        // every fresh lock, same site and same reasoning as _pendingReplayRequested's own clear.
        //
        // Auditor code-review finding, Phase 8c round 1: an earlier version of this test proved this
        // indirectly (push a whole clean transmission, assert no replay happened) -- but that assertion
        // would ALSO pass with InitializeSlant's own stale-clear deleted entirely, because the per-line
        // drain unconditionally clears _correctSlantRequested itself before checking any guard, and on
        // a fresh lock the entry gate's own cumulative-line-count check (< 16) rejects the very first
        // line regardless. That made the whole test vacuous with respect to the thing it was named for.
        // This version checks CorrectSlantRequestedForTests directly, immediately after the fresh lock
        // and before any per-line drain could possibly run -- using the same ForceMode +
        // InitializeSlantForTests two-step CorrectSlantTests.cs's own isolated fixtures established
        // (ForceMode alone does not reach InitializeSlant for a non-AVT mode; see that hook's own doc
        // comment), so this is a true unit-level proof, not an inference from a larger integration.
        var decoder = new AnalogFmSstvDecoder(11025, rxBufferMode: RxBufferMode.On);
        decoder.RequestCorrectSlant();
        Assert.True(decoder.CorrectSlantRequestedForTests, "Test setup problem: the request never actually landed.");

        decoder.ForceMode(SstvModeRegistry.Robot36);
        decoder.PushSamples(new float[1]); // drains ForceMode -- sets _mode, but NOT _effectiveSamplesPerLine/staging buffer
        decoder.InitializeSlantForTests(SstvModeRegistry.Robot36); // the fresh-lock reset under test

        Assert.False(decoder.CorrectSlantRequestedForTests, "InitializeSlant's own stale-request clear did not run -- a request from before this lock would incorrectly survive into it.");
    }

    private static float[] Encode(SstvModeDefinition mode, IImageSource sourceImage, int sampleRate)
    {
        var encoder = new AnalogFmSstvEncoder(sampleRate);
        var samples = new List<float>();
        var enumerator = encoder.EncodeAsync(mode, sourceImage).GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                samples.Add(enumerator.Current);
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return samples.ToArray();
    }
}
