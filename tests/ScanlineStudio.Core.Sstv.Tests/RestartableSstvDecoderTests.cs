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
    public void DemodType_ConstructorValue_SurvivesAPeriodicSwap()
    {
        // Demod-type subsystem Phase 3 auditor code-review finding: DemodType has no public
        // wrapper-level getter to assert against (it follows SenseLevel's "restart-only, no
        // read-back" shape, not StationIdDecodeEnabled's live-settable/exposed one) -- so the only
        // non-vacuous way to prove it survives CreateInner's periodic rebuild is to read the LIVE
        // inner decoder's own value directly, same InnerXForTests pattern already established for
        // StationIdDecodeEnabled above. Without this, a dropped `demodType` argument in CreateInner
        // would silently revert a PLL/ZeroCrossing user to Hilbert after every ~12h restart, with no
        // test able to catch it.
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 100, criticalThresholdSamples: 1000, demodType: DemodType.Pll);
        Assert.Equal(DemodType.Pll, decoder.InnerDemodTypeForTests);

        for (var i = 0; i < 3; i++)
        {
            decoder.PushSamples(new float[50]); // idle silence -- crosses warningThresholdSamples=100 by the 3rd call
        }

        Assert.Equal(1, decoder.RestartCountForTests); // sanity: the swap this test targets actually happened
        Assert.Equal(DemodType.Pll, decoder.InnerDemodTypeForTests);
    }

    [Fact]
    public void RxBpfPreset_ConstructorValue_SurvivesAPeriodicSwap()
    {
        // RX BPF subsystem Phase 3 -- same reasoning/shape as DemodType_ConstructorValue_SurvivesA
        // PeriodicSwap above: RxBpfPreset has no public wrapper-level getter either, so the only
        // non-vacuous way to prove it survives CreateInner's periodic rebuild is to read the LIVE
        // inner decoder's own value directly. Without this, a dropped `rxBpfPreset` argument in
        // CreateInner would silently revert a Narrow/VeryNarrow/Off user to Wide after every ~12h
        // restart, with no test able to catch it. Uses Narrow (not Off) so the assertion can't pass
        // vacuously against the parameter's own Wide default.
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 100, criticalThresholdSamples: 1000, rxBpfPreset: RxBpfPreset.Narrow);
        Assert.Equal(RxBpfPreset.Narrow, decoder.InnerRxBpfPresetForTests);

        for (var i = 0; i < 3; i++)
        {
            decoder.PushSamples(new float[50]); // idle silence -- crosses warningThresholdSamples=100 by the 3rd call
        }

        Assert.Equal(1, decoder.RestartCountForTests); // sanity: the swap this test targets actually happened
        Assert.Equal(RxBpfPreset.Narrow, decoder.InnerRxBpfPresetForTests);
    }

    [Fact]
    public void RxBufferMode_ConstructorValue_SurvivesAPeriodicSwap()
    {
        // RX buffer subsystem Phase 2 -- same reasoning/shape as DemodType/RxBpfPreset's own sibling
        // tests above. Uses Extended (not Off) so the assertion can't pass vacuously against the
        // parameter's own On default.
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 100, criticalThresholdSamples: 1000, rxBufferMode: RxBufferMode.Extended);
        Assert.Equal(RxBufferMode.Extended, decoder.InnerRxBufferModeForTests);

        for (var i = 0; i < 3; i++)
        {
            decoder.PushSamples(new float[50]); // idle silence -- crosses warningThresholdSamples=100 by the 3rd call
        }

        Assert.Equal(1, decoder.RestartCountForTests); // sanity: the swap this test targets actually happened
        Assert.Equal(RxBufferMode.Extended, decoder.InnerRxBufferModeForTests);
    }

    [Fact]
    public void Swap_DisposesTheOutgoingInnerDecodersScratchFiles()
    {
        // RX buffer subsystem Phase 7 (disposal-chain sub-piece): without Swap() disposing the
        // outgoing instance, RxBufferMode.Extended would leak two scratch files + a live background
        // writer task PER RESTART -- a real, unbounded production leak (round-2 plan-review's own
        // finding, flagged before Phase 7 started). Same swap-forcing recipe as
        // RxBufferMode_ConstructorValue_SurvivesAPeriodicSwap above. `using`: code-review nit fix --
        // RestartableSstvDecoder is IDisposable as of this sub-piece; without it, the NEW current
        // instance's own scratch files (asserted live below) would leak past this test's own end.
        using var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 100, criticalThresholdSamples: 1000, rxBufferMode: RxBufferMode.Extended);
        var outgoing = (RxDiskLineStagingBuffer)decoder.InnerRxLineStagingBufferForTests!;
        var demodPath = outgoing.DemodPathForTests;
        var syncPath = outgoing.SyncPathForTests;
        Assert.True(File.Exists(demodPath), "Test setup problem: the outgoing instance's scratch file should exist before the swap.");
        Assert.True(File.Exists(syncPath), "Test setup problem: the outgoing instance's scratch file should exist before the swap.");

        for (var i = 0; i < 3; i++)
        {
            decoder.PushSamples(new float[50]); // idle silence -- crosses warningThresholdSamples=100 by the 3rd call
        }

        Assert.Equal(1, decoder.RestartCountForTests); // sanity: the swap this test targets actually happened
        Assert.False(File.Exists(demodPath), "Outgoing instance's scratch file should have been deleted by Swap()'s own disposal.");
        Assert.False(File.Exists(syncPath), "Outgoing instance's scratch file should have been deleted by Swap()'s own disposal.");

        // The NEW current instance has its own, different, still-live scratch files -- disposal
        // targets only the outgoing instance, not every Extended-mode buffer that ever existed.
        var current = (RxDiskLineStagingBuffer)decoder.InnerRxLineStagingBufferForTests!;
        Assert.NotSame(outgoing, current);
        Assert.True(File.Exists(current.DemodPathForTests));
        Assert.True(File.Exists(current.SyncPathForTests));
    }

    [Fact]
    public void Dispose_DisposesTheCurrentInnerDecoder_AndIsIdempotent()
    {
        var decoder = new RestartableSstvDecoder(rxBufferMode: RxBufferMode.Extended);
        var inner = (RxDiskLineStagingBuffer)decoder.InnerRxLineStagingBufferForTests!;
        var demodPath = inner.DemodPathForTests;
        Assert.True(File.Exists(demodPath), "Test setup problem: the scratch file should exist before Dispose().");

        decoder.Dispose();
        Assert.False(File.Exists(demodPath));

        // Round-2 plan-review finding: this class is a container-created DI singleton, so the DI
        // container can dispose it at host shutdown IN ADDITION to SstvSessionService's own explicit
        // call, in unspecified order -- must not throw on a second call.
        var exception = Record.Exception(decoder.Dispose);
        Assert.Null(exception);
    }

    [Fact]
    public void StationIdDecoded_ForwardsFromTheCurrentInner()
    {
        // CW-ID/FSK station-ID subsystem Phase 5 (RestartableSstvDecoder.StationIdDecoded's own doc
        // comment: forwarding the event was explicitly left for this phase, Phase 4 only wired the
        // enable FLAG). Uses the real production type (Program.cs registers RestartableSstvDecoder,
        // not AnalogFmSstvDecoder directly) end to end: real encode -> real decode -> the wrapper's
        // own forwarded event, not AnalogFmSstvDecoder's event checked in isolation.
        const int sampleRate = 11025;
        var mode = SstvModeRegistry.Robot36;
        var stationId = new StationIdTransmitOptions { FskIdEnabled = true, Callsign = "W1AW" };
        var samples = EncodeRealTransmission(mode, out _, stationId);

        // Same general pre-lock-scan-gate property Phase 4's own wiring tests documented
        // (AnalogFmSstvEncoderStationIdWiringTests' PadPastFixedWindowCeiling) -- not specific to
        // this wrapper class.
        var padded = samples.Concat(new float[sampleRate * 2]).ToArray();

        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: long.MaxValue, criticalThresholdSamples: long.MaxValue, stationIdDecodeEnabled: true);
        var stationIdEvents = new List<FskStationIdDecodedInfo>();
        decoder.StationIdDecoded += info => stationIdEvents.Add(info);

        decoder.PushSamples(padded);

        Assert.Single(stationIdEvents);
        Assert.Equal("W1AW", stationIdEvents[0].Callsign);
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
    public async Task RequestCorrectSlant_ForwardsToTheCurrentInner_AndFiresARealReplay()
    {
        // RX buffer subsystem Phase 8c: proves RequestCorrectSlant() reaches the real inner
        // AnalogFmSstvDecoder through the wrapper, not just that the wrapper method compiles. Uses the
        // new InnerRxBufferBaseTransmissionLineForTests passthrough (added after auditor code-review
        // round 1 flagged the original LineDecoded-count discriminator as weaker than it needed to be
        // -- it would also pass if the decoder restarted mid-run and decoded extra lines with no
        // replay at all) for the SAME proof-positive precision the direct AnalogFmSstvDecoder tests
        // use: that field only ever moves off 0 inside PerformReplay's own tail.
        //
        // autoSlantEnabled: false (same technique as AutoSlantEnabledFalse_ActuallyPropagates... above)
        // means the CONTINUOUS automatic tracker never commits on its own, so any replay observed here
        // can only be this manual request's own doing. Same ~453ppm mismatch at the wrapper's fixed
        // 11025 rate that test's own positive control already established as reliable.
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

        var sampleArray = samples.ToArray();

        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: long.MaxValue, criticalThresholdSamples: long.MaxValue, autoSlantEnabled: false, rxBufferMode: RxBufferMode.On);
        var decodedLineCount = 0;
        decoder.LineDecoded += _ => decodedLineCount++;

        const int chunkSize = 256;
        var offset = 0;
        var requested = false;
        while (offset < sampleArray.Length)
        {
            var length = Math.Min(chunkSize, sampleArray.Length - offset);
            decoder.PushSamples(sampleArray.AsMemory(offset, length));
            offset += length;

            if (!requested && decodedLineCount >= 16)
            {
                decoder.RequestCorrectSlant();
                requested = true;
            }
        }

        Assert.True(requested, "Test setup problem: never decoded 16 lines to request against.");
        Assert.True(decoder.InnerRxBufferBaseTransmissionLineForTests > 0, "PerformReplay never ran on the live inner decoder -- RequestCorrectSlant() may not have reached it.");
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

    private static float[] EncodeRealTransmission(SstvModeDefinition mode, out ArrayImageSource sourceImage, StationIdTransmitOptions? stationId = null)
    {
        var image = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
        sourceImage = image;

        var encoder = new AnalogFmSstvEncoder(11025);
        var samples = new List<float>();
        var enumerator = encoder.EncodeAsync(mode, image, stationId).GetAsyncEnumerator();
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
