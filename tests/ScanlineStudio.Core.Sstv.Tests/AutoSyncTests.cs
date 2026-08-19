using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Auto Sync -- the automatic, drift-triggered version of the manual ReSync button (legacy's
/// <c>sys.m_AutoSync</c>, <c>TMmsstv::AutoStopJob</c>, <c>Main.cpp:3884-4035</c>). See
/// <see cref="AnalogFmSstvDecoder"/>'s own <c>TryAutoSync</c>/<c>ComputeAutoSyncPosition</c>/
/// <c>ResetAutoSyncDetectionState</c> doc comments for the full design and the two rounds of
/// plan-readiness review this went through (round 1's most severe finding: a wrong anchor base would
/// have caused continuous spurious auto-resyncs on a perfectly-synced signal -- the regression test
/// for that is below and is the most important one in this file).
/// </summary>
public class AutoSyncTests
{
    [Fact]
    public void PerfectlySyncedSignal_NeverTriggersAutoSync_AndComputesA_NearZeroPosition()
    {
        // The round-1 plan-review regression: using the wrong anchor base (ApplySlantTracking's own
        // segment-offset-relative `relative`, instead of the OFP-relative ComputeAutoSyncPosition)
        // would have produced a large, constant, silent bias.
        //
        // MUST be a mode whose sync segment is NOT first in the line (Scottie family), AND must assert
        // on LastComputedAutoSyncPositionForTests directly, not just the trigger count -- both
        // corrections found via this test's own revert-fix-confirm-fail check. Robot36's own sync
        // segment happens to be first in the line, so _syncSegmentOffsetSamples and
        // ComputeSyncPeakOffsetSamples coincide for it -- a Robot36-only version of this test passed
        // even with the wrong base reintroduced. Separately, a CONSTANT wrong bias (every line offset
        // the same way) reads as a stable, self-consistent cluster to TryAutoSync's own clustering
        // check -- nothing ever looks like a "jump" if every reading agrees with every other reading,
        // just uniformly offset from true zero -- so AutoSyncTriggerCountForTests alone stayed 0 with
        // Scottie AND the wrong base reintroduced too. Only a direct assertion on the computed
        // position's own magnitude actually catches this class of bug.
        var mode = SstvModeRegistry.ScottieS1;
        var samples = EncodeRealTransmission(mode, out _);
        var decoder = new AnalogFmSstvDecoder(11025);

        var lineCount = 0;
        decoder.LineDecoded += _ => lineCount++;
        const int chunkSize = 256;
        var offset = 0;
        for (; offset < samples.Length && lineCount < 10; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
        }

        Assert.True(lineCount >= 10, "Never decoded enough lines -- test setup problem.");
        Assert.Equal(0, decoder.AutoSyncTriggerCountForTests);

        Assert.NotNull(decoder.LastComputedAutoSyncPositionForTests);
        Assert.True(Math.Abs(decoder.LastComputedAutoSyncPositionForTests!.Value) < 20,
            $"OFP-relative Auto Sync position {decoder.LastComputedAutoSyncPositionForTests} is too large for a clean signal -- suggests the wrong anchor base.");
    }

    [Fact]
    public void SuddenPositionJump_EventuallyTriggersAutoSync()
    {
        // Negative control for the test above: confirms the 0-trigger result there isn't just because
        // Auto Sync can never fire at all in this test harness.
        //
        // NOT a continuous clock-rate mismatch -- empirically confirmed (during this feature's own
        // development) that smooth linear drift never organically triggers Auto Sync in this port:
        // SlantTracker's own already-shipped regression-based correction absorbs continuous drift
        // smoothly enough that consecutive ComputeAutoSyncPosition readings stay clustered together,
        // never producing the "small step, but a real jump from the stable reference" pattern Auto
        // Sync's own branches look for. On reflection this matches legacy's real design intent too:
        // Auto Slant (continuous, regression-based) and Auto Sync (sudden-jump, cluster-based) are
        // different mechanisms for different failure modes, sharing one legacy function but not one
        // job. A splice of extra silent samples mid-stream instead simulates a real sync
        // glitch/dropout -- a SUDDEN, then-persistent position shift, which is what Auto Sync exists
        // to catch.
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode, out _);
        var decoder = new AnalogFmSstvDecoder(11025);

        // Splice well past both the 8-line warmup and a realistic point for a stable n>=4 reference to
        // have already formed on this clean signal (15% in is comfortably past either for Robot36's
        // own per-line sample count). The inserted silence is a real discontinuity in the raw sample
        // stream, not a synthetic pokes of internal state -- exercises the real hook, not a shortcut.
        var spliceIndex = samples.Length * 15 / 100;
        // Empirically determined (during this feature's own development, see this test's own doc
        // comment): must clear the clustering check's own threshold (14*mult once 16 real observations
        // exist, Main.cpp:3892) or the jump silently reads as "still the same cluster" instead of a
        // genuine scattered/new-position event -- a 60-sample splice measured well under that
        // threshold and never triggered; 150 comfortably clears it for this mode/rate.
        const int spliceSamples = 150;
        var spliced = new float[samples.Length + spliceSamples];
        Array.Copy(samples, 0, spliced, 0, spliceIndex);
        Array.Copy(samples, spliceIndex, spliced, spliceIndex + spliceSamples, samples.Length - spliceIndex);

        decoder.PushSamples(spliced);

        Assert.True(decoder.AutoSyncTriggerCountForTests > 0,
            $"Never observed an automatic trigger after a deliberate {spliceSamples}-sample splice -- test setup problem, or Auto Sync genuinely never fires.");
    }

    [Fact]
    public void SuddenPositionJump_ViaBulkSinglePush_ActuallyDrainsTheTriggeredSkip_NotJustZeroesItAtEndOfImage()
    {
        // D0-audit round-4 finding: DrainPendingSkip() only ran at the top of PushSamplesCore before
        // this fix -- correct for a streaming caller (any chunk shorter than one line, since the
        // NEXT call's own top-of-method drain runs before the next line can even complete), but a
        // bulk caller pushing a WHOLE transmission in one PushSamples call (this test's own
        // scenario, and a real path: WAV/file decode, GoldenVectorTests, and the sibling test above)
        // never lets a second PushSamplesCore call arrive to drain a skip TriggerAutoSync requests
        // mid-decode -- so the skip sat pending, unapplied, until EndOfImage's ResetReSyncState()
        // silently zeroed it, while ApplySyncCorrection's OTHER side-effects
        // (_slantCorrectionsDisabledForRestOfImage, the Auto-Sync cooldown) still applied regardless
        // -- paying the suppression cost without the realignment it exists to buy.
        //
        // Same splice-based trigger technique as the sibling test above, but this one observes
        // PendingSkipSamplesForTests on every LineDecoded event, not just the trigger count.
        // RaiseSubscribers(LineDecoded, ...) fires BEFORE this SAME loop iteration's own
        // ApplySlantTracking()/DrainPendingSkip() call (verified: LineDecoded is raised at line
        // ~2643, ApplySlantTracking at ~2691, both inside TryProcessBuffer's one per-line loop
        // iteration) -- so a skip a line's OWN trigger sets is only ever OBSERVABLE, if it were left
        // undrained, starting from the NEXT line's LineDecoded event, never that same line's own.
        // With the fix, DrainPendingSkip() runs in the SAME iteration the trigger sets the skip, so
        // it is always back to 0 again before the next line's LineDecoded fires -- this should NEVER
        // observe a nonzero value on any line, despite the trigger definitely having fired (asserted
        // below). Without the fix, the skip set by the trigger's own line stays nonzero across every
        // subsequent LineDecoded observation until EndOfImage finally zeroes it, unapplied, at the
        // very end.
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode, out _);
        var decoder = new AnalogFmSstvDecoder(11025);

        var spliceIndex = samples.Length * 15 / 100;
        const int spliceSamples = 150;
        var spliced = new float[samples.Length + spliceSamples];
        Array.Copy(samples, 0, spliced, 0, spliceIndex);
        Array.Copy(samples, spliceIndex, spliced, spliceIndex + spliceSamples, samples.Length - spliceIndex);

        var pendingSkipAfterEachLine = new List<int>();
        decoder.LineDecoded += _ => pendingSkipAfterEachLine.Add(decoder.PendingSkipSamplesForTests);

        decoder.PushSamples(spliced);

        Assert.True(decoder.AutoSyncTriggerCountForTests > 0,
            $"Never observed an automatic trigger after a deliberate {spliceSamples}-sample splice -- test setup problem, not what this test targets.");
        Assert.DoesNotContain(pendingSkipAfterEachLine, v => v > 0);
    }

    [Fact]
    public void AutoSyncEnabledFalse_NeverTriggers_ButBookkeepingStillRuns()
    {
        // Same splice scenario the test above proves DOES trigger when enabled -- a real end-to-end
        // A/B, matching SyncRestartEnabled's own established test convention (MidReceptionRestartTests.cs).
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode, out _);
        var decoder = new AnalogFmSstvDecoder(11025, autoSyncEnabled: false);

        var spliceIndex = samples.Length * 15 / 100;
        const int spliceSamples = 150;
        var spliced = new float[samples.Length + spliceSamples];
        Array.Copy(samples, 0, spliced, 0, spliceIndex);
        Array.Copy(samples, spliceIndex, spliced, spliceIndex + spliceSamples, samples.Length - spliceIndex);

        decoder.PushSamples(spliced);

        Assert.Equal(0, decoder.AutoSyncTriggerCountForTests);
        // Bookkeeping (ring buffer / observation count / cooldown decrement) is NOT gated by the
        // setting -- round-2 plan-review finding: legacy's own outer gate (Main.cpp:3886) is
        // constant-true in this port (no Auto Slant toggle exists), so only the two trigger branches
        // themselves read AutoSyncEnabled. A naive "disabled = skip the whole feature" implementation
        // would leave this at 0 too.
        Assert.True(decoder.AutoSyncObservationCountForTests > 8,
            "Bookkeeping (observation count) must keep advancing even while AutoSyncEnabled=false.");
    }

    [Fact]
    public void Branch1Threshold_SwitchesBetween5xAnd2xTheBaseMultiplier_BasedOnAutoSlantEnabled()
    {
        // Regression test for a real legacy-parity bug an auditor plan-review caught before this
        // shipped: Main.cpp:3910/:3917 use `(KRSA->Checked ? 5 : 2) * m_Mult` for branch 1's own
        // threshold, not a constant -- this port previously hardcoded the `5` side unconditionally,
        // correct only before an Auto Slant toggle existed to ever make the `2` side reachable.
        //
        // Verified via LastBranch1ThresholdForTests directly rather than an empirically-tuned
        // real-audio splice scenario: the threshold moves BOTH of branch 1's own comparisons in
        // opposite directions at once (a looser "small step" ceiling but a stricter "large jump"
        // floor, or vice versa), so a splice sized to trigger at one threshold and not the other would
        // be fragile and non-obvious to maintain -- a direct property read is the more precise,
        // more robust check for this specific fix.
        // branch 1's own threshold is only ever computed while `n < 4` -- CountAutoSyncCluster's own
        // "how many of the last 16 readings cluster near the current position" count, per
        // PerfectlySyncedSignal_NeverTriggersAutoSync's own doc comment a CLEAN signal stays clustered
        // (n>=4) almost immediately after warmup and essentially never revisits n<4 -- so this reuses
        // SuddenPositionJump_EventuallyTriggersAutoSync's own splice technique (a real discontinuity in
        // the raw sample stream) purely to scatter the cluster at least once, not to provoke a trigger.
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode, out _);
        var spliceIndex = samples.Length * 15 / 100;
        const int spliceSamples = 150;
        var spliced = new float[samples.Length + spliceSamples];
        Array.Copy(samples, 0, spliced, 0, spliceIndex);
        Array.Copy(samples, spliceIndex, spliced, spliceIndex + spliceSamples, samples.Length - spliceIndex);

        var enabledDecoder = new AnalogFmSstvDecoder(11025, autoSlantEnabled: true);
        enabledDecoder.PushSamples(spliced);
        var thresholdWhenEnabled = enabledDecoder.LastBranch1ThresholdForTests;

        var disabledDecoder = new AnalogFmSstvDecoder(11025, autoSlantEnabled: false);
        disabledDecoder.PushSamples(spliced);
        var thresholdWhenDisabled = disabledDecoder.LastBranch1ThresholdForTests;

        Assert.NotNull(thresholdWhenEnabled);
        Assert.NotNull(thresholdWhenDisabled);
        // Exact 5:2 ratio, both derived from the SAME base multiplier (same mode/sample rate for both
        // decoders) -- cross-multiplied to avoid integer-division rounding rather than dividing either
        // side directly.
        Assert.Equal(thresholdWhenEnabled!.Value * 2, thresholdWhenDisabled!.Value * 5);
        // Sanity: the two values must actually differ, ruling out a no-op fix that always picks one
        // branch of the ternary regardless of the flag.
        Assert.NotEqual(thresholdWhenEnabled, thresholdWhenDisabled);
    }

    [Fact]
    public async Task Avt_NeverRunsAutoSyncBookkeepingAtAll()
    {
        // AVT exclusion is free via InitializeSlant nulling _slantTracker for AVT (ApplySlantTracking
        // returns immediately, before ever reaching TryAutoSync) -- confirmed directly here rather
        // than just assumed from the hook's placement.
        var mode = SstvModeRegistry.Avt;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(180, 90, 40));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        var decoder = new AnalogFmSstvDecoder(11025);

        var encoder = new AnalogFmSstvEncoder(11025);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        decoder.PushSamples(samples.ToArray());

        Assert.Equal(0, decoder.AutoSyncObservationCountForTests);
        Assert.Equal(0, decoder.AutoSyncTriggerCountForTests);
    }

    [Fact]
    public void ManualReSync_ResetsAutoSyncObservationCount_ButNotViaAutoSyncItself()
    {
        // PerformReSync's own new addition (round-1 plan-review finding: KRFSClick's real
        // m_AutoStopACnt=0, Main.cpp:14015, previously undocumented as a real gap since Auto Sync
        // didn't exist in this port yet -- now a real, observable reset). Deliberately distinct from
        // Auto Sync's own triggers, which do NOT reset this counter (TryAutoSync's own doc comment).
        var mode = SstvModeRegistry.Robot36;
        const int declaredSampleRate = 11025;
        const int trueSampleRate = (int)(declaredSampleRate * 1.01);
        var samples = EncodeRealTransmissionAtRate(mode, trueSampleRate, out _);
        // RX buffer subsystem Phase 6d: explicit RxBufferMode.Off, not the bare (RxBufferMode.On
        // default) constructor an earlier version of this test used. This test is about ManualReSync's
        // own AutoSyncObservationCount reset, not RX buffer/replay -- but the default constructor's
        // RxBufferMode.On is no longer inert now that Phase 6d wires automatic replay to it: this
        // scenario's own real 1% clock mismatch reliably commits an Auto-Slant correction within the
        // first several lines, and PerformReplay's own reset-then-rebuild (on the first replay pass of
        // an image) resets AutoSyncObservationCountForTests right back toward 0 -- exactly the counter
        // this test's own setup assertion checks -- before this test ever gets to read it. Off keeps
        // this test's own scope narrow and unaffected by a feature it isn't testing.
        var decoder = new AnalogFmSstvDecoder(declaredSampleRate, rxBufferMode: RxBufferMode.Off);

        const int chunkSize = 256;
        var offset = 0;
        var lineCount = 0;
        decoder.LineDecoded += _ => lineCount++;
        for (; offset < samples.Length && lineCount < 10; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
        }

        Assert.True(decoder.AutoSyncObservationCountForTests > 8, "Test setup problem -- never reached Auto Sync's own warmup.");

        decoder.RequestReSync();
        decoder.PushSamples(samples.AsMemory(offset, Math.Min(64, samples.Length - offset)));

        Assert.Equal(0, decoder.AutoSyncObservationCountForTests);
    }

    private static float[] EncodeRealTransmission(SstvModeDefinition mode, out ArrayImageSource sourceImage)
        => EncodeRealTransmissionAtRate(mode, 11025, out sourceImage);

    private static float[] EncodeRealTransmissionAtRate(SstvModeDefinition mode, int encodeSampleRate, out ArrayImageSource sourceImage)
    {
        var image = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
        sourceImage = image;

        var encoder = new AnalogFmSstvEncoder(encodeSampleRate);
        var samples = new List<float>();
        var enumerator = encoder.EncodeAsync(mode, image).GetAsyncEnumerator();
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

    private static ArrayImageSource CreateGradientTestImage(int width, int height)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = new Rgb24(
                    R: (byte)(x * 255 / Math.Max(1, width - 1)),
                    G: (byte)(y * 255 / Math.Max(1, height - 1)),
                    B: 128);
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }
}
