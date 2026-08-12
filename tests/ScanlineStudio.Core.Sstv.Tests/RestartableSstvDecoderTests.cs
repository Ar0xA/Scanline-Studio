using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Ultracode audit finding #34 fix: <see cref="RestartableSstvDecoder"/> periodically discards and
/// reconstructs the whole <see cref="AnalogFmSstvDecoder"/> object graph instead of widening every
/// affected field. See that class's own doc comment for the full state machine.
/// </summary>
public class RestartableSstvDecoderTests
{
    [Fact]
    public void AutoSlantEnabled_ForwardsTheConstructorValue()
    {
        // Auditor plan-review finding: no existing test in this file covers ANY of the sibling
        // constructor-injected toggles' own forwarding (every other call site here passes
        // afcEnabled: true as an incidental fixed value while testing maintenance-threshold behavior)
        // -- this is the first such test for any of these flags, not mirroring an existing pattern.
        // Reads the WRAPPER's own stored value (RestartableSstvDecoder.AutoSlantEnabled), not a
        // decode-driven inner-decoder property -- see that property's own doc comment for why no
        // _gate is needed to read it.
        Assert.True(new RestartableSstvDecoder(autoSlantEnabled: true).AutoSlantEnabled);
        Assert.False(new RestartableSstvDecoder(autoSlantEnabled: false).AutoSlantEnabled);
    }

    [Fact]
    public async Task AutoSlantEnabledFalse_ActuallyPropagatesToTheInnerDecodersOwnGate_NotJustTheWrapperField()
    {
        // Auditor-caught gap: AutoSlantEnabled_ForwardsTheConstructorValue above only proves the
        // WRAPPER's own field readback -- nothing proved CreateInner() actually forwards
        // autoSlantEnabled: into the real AnalogFmSstvDecoder it constructs (the production path,
        // since Program.cs registers THIS wrapper type, not AnalogFmSstvDecoder directly). Dropping
        // that one constructor argument in CreateInner() would leave the whole suite green otherwise.
        // Same 1.0005x-mismatch technique as SlantTests.cs's own
        // AnalogFmSstvDecoder_AutoSlantDisabled_SlantPpmNeverMovesOffZero_ButBookkeepingStillAdvances
        // (that test's 44100 rate yields ~499ppm; this test is pinned to the wrapper's own fixed
        // 11025 rate instead, yielding ~453ppm -- see the positive control below for why that
        // difference matters here specifically),
        // driven through the wrapper instead of the inner decoder directly.
        var mode = SstvModeRegistry.Robot36;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(230, 230, 230));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        const int declaredSampleRate = RestartableSstvDecoder.ProductionSampleRate;
        const double trueSampleRate = declaredSampleRate * 1.0005;

        var encoder = new AnalogFmSstvEncoder((int)trueSampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        // Positive control (auditor round 2 finding): the `false` assertion below is only load-bearing
        // if this exact scenario WOULD commit a correction with the flag on -- CreateInner() pins the
        // wrapper to the 11025 default (no sampleRate argument forwarded), a rate/ppm combination
        // ((int)(11025*1.0005)=11030, ~453ppm) none of SlantTests.cs's own commit scenarios actually
        // exercise (they all use 44100). Without this control, a scenario that never commits at 11025
        // regardless of the flag would make the `false` assertion pass vacuously even if
        // autoSlantEnabled: were silently dropped from CreateInner() entirely.
        var enabledDecoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: long.MaxValue, criticalThresholdSamples: long.MaxValue, autoSlantEnabled: true);
        var observedNonZeroSlantPpmWhenEnabled = false;
        enabledDecoder.LineDecoded += _ =>
        {
            if (enabledDecoder.SlantPpm is not (null or 0.0))
            {
                observedNonZeroSlantPpmWhenEnabled = true;
            }
        };
        enabledDecoder.PushSamples(samples.ToArray());
        Assert.True(observedNonZeroSlantPpmWhenEnabled,
            "Positive control failed -- this scenario never commits a correction at 11025Hz even with the flag on, so the disabled case below would pass vacuously.");

        var disabledDecoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: long.MaxValue, criticalThresholdSamples: long.MaxValue, autoSlantEnabled: false);
        var observedNonZeroSlantPpmWhenDisabled = false;
        disabledDecoder.LineDecoded += _ =>
        {
            if (disabledDecoder.SlantPpm is not (null or 0.0))
            {
                observedNonZeroSlantPpmWhenDisabled = true;
            }
        };
        disabledDecoder.PushSamples(samples.ToArray());

        Assert.False(observedNonZeroSlantPpmWhenDisabled);
    }

    [Fact]
    public void StationIdDecodeEnabled_ConstructorValue_SurvivesAPeriodicSwap()
    {
        // Auditor code-review finding on CW-ID/FSK Phase 4: unlike every OTHER toggle in this class
        // (AutoSlantEnabled etc., all restart-only/readonly), StationIdDecodeEnabled is deliberately
        // LIVE-settable (see ISstvDecoder.StationIdDecodeEnabled's own doc comment) -- a naive
        // implementation could easily forward a set value to the CURRENT inner without also storing
        // it for CreateInner() to re-apply on the next periodic rebuild, silently reverting to the
        // constructor default (false) the next time this class swaps its inner decoder.
        //
        // Round-2 finding: asserting only the public StationIdDecodeEnabled getter here would be
        // VACUOUS -- that getter reads the wrapper's own stored field, which Swap()/CreateInner()
        // could stop applying entirely and this assertion would still pass. InnerStationIdDecodeEnabledForTests
        // reads the LIVE inner decoder's own property instead, so this actually proves the value
        // reached the fresh post-swap instance, not just that the wrapper remembers what it was told.
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 100, criticalThresholdSamples: 1000, stationIdDecodeEnabled: true);
        Assert.True(decoder.StationIdDecodeEnabled);
        Assert.True(decoder.InnerStationIdDecodeEnabledForTests);

        for (var i = 0; i < 3; i++)
        {
            decoder.PushSamples(new float[50]); // idle silence -- crosses warningThresholdSamples=100 by the 3rd call
        }

        Assert.Equal(1, decoder.RestartCountForTests); // sanity: the swap this test targets actually happened
        Assert.True(decoder.StationIdDecodeEnabled);
        Assert.True(decoder.InnerStationIdDecodeEnabledForTests);
    }

    [Fact]
    public void StationIdDecodeEnabled_SetLiveAfterConstruction_AlsoSurvivesAPeriodicSwap()
    {
        // Same finding as StationIdDecodeEnabled_ConstructorValue_SurvivesAPeriodicSwap above, but for
        // the property SETTER path specifically -- proves the setter updates the STORED field
        // (_stationIdDecodeEnabled), not just the current inner instance directly, which is the only
        // way a later swap could know to re-apply it. Same round-2 non-vacuous-assertion fix applied
        // here too.
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 100, criticalThresholdSamples: 1000);
        Assert.False(decoder.StationIdDecodeEnabled);
        Assert.False(decoder.InnerStationIdDecodeEnabledForTests);

        decoder.StationIdDecodeEnabled = true;
        Assert.True(decoder.StationIdDecodeEnabled);
        Assert.True(decoder.InnerStationIdDecodeEnabledForTests);

        for (var i = 0; i < 3; i++)
        {
            decoder.PushSamples(new float[50]);
        }

        Assert.Equal(1, decoder.RestartCountForTests);
        Assert.True(decoder.StationIdDecodeEnabled);
        Assert.True(decoder.InnerStationIdDecodeEnabledForTests);
    }

    [Fact]
    public void ForceMode_ForwardsToTheCurrentInner()
    {
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: long.MaxValue, criticalThresholdSamples: long.MaxValue);
        SstvModeDefinition? detectedMode = null;
        decoder.ModeDetected += m => detectedMode = m;

        decoder.ForceMode(SstvModeRegistry.Avt);
        decoder.PushSamples(new float[64]);

        Assert.NotNull(detectedMode);
        Assert.Equal(SstvModeRegistry.Avt.Id, detectedMode!.Id);
    }

    [Fact]
    public void ForceMode_ForwardsTelemetryPropertiesToTheCurrentInner()
    {
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: long.MaxValue, criticalThresholdSamples: long.MaxValue);

        Assert.Equal(0.0, decoder.SignalPeakLevel);
        Assert.False(decoder.IsLevelOverdriven);
        Assert.Equal(0, decoder.BufferedSampleCount);
        Assert.Null(decoder.SyncFrequencyCorrectionHz);

        decoder.ForceMode(SstvModeRegistry.Robot36);
        decoder.PushSamples(new float[64]);

        Assert.True(decoder.BufferedSampleCount > 0);
    }

    [Fact]
    public void PushSamples_AfterASwap_TelemetryPropertiesReflectTheFreshInnerPlusTheTriggeringChunk()
    {
        // Round-2 plan-review correction: unlike SlantPpm/SyncOffsetSamples, a restart swap does NOT
        // reliably reset the 3 AGC-backed properties to a fixed "empty" value -- PushSamples forwards
        // the SAME chunk that triggered the swap to the fresh inner, and pre-lock scanning alone
        // drives BufferedSampleCount/SignalPeakLevel/IsLevelOverdriven with no lock required. Using
        // idle silence as the triggering chunk (never locks, near-zero amplitude) so the AGC-backed
        // properties land at their "nothing happened yet" values for THIS specific chunk shape --
        // not asserting that as a general post-swap guarantee, only for silence.
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 100, criticalThresholdSamples: 1000);

        for (var i = 0; i < 3; i++)
        {
            decoder.PushSamples(new float[50]); // idle silence -- crosses warningThresholdSamples=100 by the 3rd call
        }

        Assert.Equal(1, decoder.RestartCountForTests); // sanity: the swap this test targets actually happened

        // SyncFrequencyCorrectionHz IS state-gated (needs a fresh _mode lock) -- reliably null, since
        // silence can never establish one.
        Assert.Null(decoder.SyncFrequencyCorrectionHz);

        // The AGC-backed properties reflect the fresh inner plus the small silent triggering chunk:
        // silence never drives CurMax up, so these read as if freshly constructed for THIS chunk
        // shape specifically (not a general post-swap guarantee -- see this test's own comment above).
        Assert.Equal(0.0, decoder.SignalPeakLevel);
        Assert.False(decoder.IsLevelOverdriven);

        // The one property this round-2 correction was actually about (code-level audit finding: an
        // earlier version of this test omitted it) -- NOT 0, the fresh inner's BufferedSampleCount
        // already reflects the 50-sample chunk PushSamples forwarded to it as part of this very swap.
        Assert.Equal(50, decoder.BufferedSampleCount);
    }

    [Fact]
    public void PushSamples_IdlePastWarningThreshold_SwapsInner_AndEventForwardingSurvives()
    {
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 100, criticalThresholdSamples: 1000);
        var restartedCount = 0;
        decoder.Restarted += () => restartedCount++;

        // The state machine evaluates n/IsIdle BEFORE forwarding the current chunk (settled state as
        // of the end of the PREVIOUS call) -- so crossing the threshold is only detected on a call
        // AFTER the one whose forwarding actually crossed it. Push idle silence in a loop of small
        // chunks (not one single 200-sample push) so a later call's pre-check can observe what an
        // earlier call's forwarding already crossed.
        for (var i = 0; i < 3; i++)
        {
            decoder.PushSamples(new float[50]);
        }

        Assert.Equal(1, decoder.RestartCountForTests);
        // Coverage gap closed post-final-review: nothing previously subscribed to Restarted, so a
        // regression dropping that raise from the normal-swap branch would have stayed green here --
        // and in production would silently mean MaintenanceWarningCleared never fires, since Restarted
        // is its only source signal (see SstvSessionService.OnDecoderRestarted).
        Assert.Equal(1, restartedCount);

        // Event forwarding must survive the swap: a subscriber attached to the WRAPPER (after the
        // swap above already happened) must still receive events from whichever inner instance is
        // now current -- exercises that CreateInner() correctly rewires each fresh inner's own
        // LineDecoded/ModeDetected/DecodeRestarted to the wrapper's stable forwarders, not just that
        // a swap happened.
        SstvModeDefinition? detectedMode = null;
        decoder.ModeDetected += m => detectedMode = m;

        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode, out _);
        decoder.PushSamples(samples);

        Assert.NotNull(detectedMode);
        Assert.Equal(mode.Id, detectedMode!.Id);
    }

    [Fact]
    public void PushSamples_ChunkedRealTransmission_ObservesNonIdle_AndNeverSwapsWhenBothThresholdsAreUnreachable()
    {
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode, out _);

        // Both thresholds set beyond this transmission's own length -- neither can ever fire during
        // this test, isolating "chunked delivery correctly reports non-idle mid-decode and never
        // swaps prematurely" from the separate (correctly swap-triggering) threshold-crossing tests.
        var warningThreshold = (long)samples.Length * 100;
        var criticalThreshold = (long)samples.Length * 200;
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: warningThreshold, criticalThresholdSamples: criticalThreshold);

        var observedNonIdle = false;
        const int chunkSize = 256; // deliberately small and not aligned to any header/line boundary
        for (var offset = 0; offset < samples.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));

            if (!decoder.IsIdleForTests)
            {
                observedNonIdle = true;
            }
        }

        // This is the direct regression test for the vacuous-pass shape a prior incident in this
        // codebase already hit once: a single bulk push would never present a non-idle state at a
        // wrapper-level PushSamples entry, making "no swap happened" trivially true without proving
        // anything. Chunking guarantees at least one call lands while genuinely mid-decode.
        Assert.True(observedNonIdle, "Chunked delivery of a real transmission never observed a non-idle decoder -- this test proves nothing without that.");
        Assert.Equal(0, decoder.RestartCountForTests);
    }

    [Fact]
    public void PushSamples_PastCriticalThresholdWhileNeverIdle_SwapsUnconditionally_AndSelfClears()
    {
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode, out _);

        // Critical threshold set to land mid-transmission -- the decoder is guaranteed non-idle
        // (mode locked, mid-image) at that point, since the header alone is a small fraction of a
        // full transmission's length.
        var criticalThreshold = samples.Length / 2;
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: criticalThreshold / 2, criticalThresholdSamples: criticalThreshold);

        var criticalFired = 0;
        decoder.RestartCriticallyOverdue += () => criticalFired++;
        var restartedCount = 0;
        decoder.Restarted += () => restartedCount++;

        const int chunkSize = 256;
        var crossedCritical = false;
        for (var offset = 0; offset < samples.Length && !crossedCritical; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));

            if (decoder.RestartCountForTests >= 1)
            {
                crossedCritical = true;
            }
        }

        Assert.Equal(1, decoder.RestartCountForTests);
        Assert.Equal(1, criticalFired);
        // Same coverage-gap closure as the normal-swap test above, but for the UNCONDITIONAL branch --
        // a regression dropping Restarted specifically from the critical path (while leaving it in the
        // normal-swap path) would otherwise only be caught here.
        Assert.Equal(1, restartedCount);

        // Regression test for the round-2 wedge bug: pushing another full critical-threshold's worth
        // of samples on the NOW-SWAPPED instance must not immediately re-fire -- the fresh inner
        // starts its own TotalSamplesReceived back near 0, so it takes a full new threshold's worth
        // of samples to cross again, not zero.
        decoder.PushSamples(new float[10]);
        Assert.Equal(1, decoder.RestartCountForTests);
        Assert.Equal(1, criticalFired);
    }

    [Fact]
    public void PushSamples_PastWarningThresholdWhileNeverIdle_RaisesRestartOverdueExactlyOnce()
    {
        var mode = SstvModeRegistry.Robot36;
        var samples = EncodeRealTransmission(mode, out _);

        var warningThreshold = samples.Length / 8;
        // Critical threshold set past this transmission's own length so it never fires here --
        // isolates the warning-only path.
        var criticalThreshold = (long)samples.Length * 100;
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: warningThreshold, criticalThresholdSamples: criticalThreshold);

        var warningFired = 0;
        decoder.RestartOverdue += () => warningFired++;

        // Deliberately stop at 3/4 of the transmission, well before the image finishes decoding and
        // the decoder naturally returns to idle -- reaching idle again while already past
        // warningThreshold would correctly trigger a normal step-2 swap, which is expected behavior
        // elsewhere but would defeat THIS test's isolation of the "never idle" warning-only path.
        const int chunkSize = 256;
        var stopAt = samples.Length * 3 / 4;
        for (var offset = 0; offset < stopAt; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, stopAt - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
        }

        Assert.Equal(1, warningFired);
        Assert.Equal(0, decoder.RestartCountForTests);
    }

    [Fact]
    public void ProductionThresholds_AreSizedAt12And13Hours_At11025Hz()
    {
        Assert.Equal(12L * 3600 * 11025, RestartableSstvDecoder.DefaultWarningThresholdSamples);
        Assert.Equal(13L * 3600 * 11025, RestartableSstvDecoder.DefaultCriticalThresholdSamples);
    }

    [Fact]
    public void PushSamples_And_ResetAgc_PassThrough_PreAndPostSwap()
    {
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 50, criticalThresholdSamples: 500);

        decoder.ResetAgc(); // pre-swap: must not throw
        decoder.PushSamples(new float[10]);

        // See the lazy-detection note in the idle-swap test above: crossing the threshold is only
        // observed on a call AFTER the one whose forwarding crossed it, so loop rather than push once.
        for (var i = 0; i < 5; i++)
        {
            decoder.PushSamples(new float[20]);
        }

        Assert.Equal(1, decoder.RestartCountForTests);
        decoder.ResetAgc(); // post-swap: must not throw
        decoder.PushSamples(new float[10]);
    }

    private static float[] EncodeRealTransmission(SstvModeDefinition mode, out ArrayImageSource sourceImage)
    {
        var image = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
        sourceImage = image;

        var encoder = new AnalogFmSstvEncoder(11025);
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
