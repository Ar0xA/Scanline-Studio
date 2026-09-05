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
    public void RequestNotch_SurvivesAPeriodicSwap()
    {
        // Un-stub-RX-tab Piece A code-review finding: RequestNotch mirrors StationIdDecodeEnabled's
        // own live-settable shape (not RequestReSync's fire-and-forget-only one) -- same non-vacuous
        // proof requirement as StationIdDecodeEnabled_SetLiveAfterConstruction_AlsoSurvivesAPeriodicSwap
        // above: assert the LIVE inner decoder's own state post-swap via InnerNotchEnabledForTests/
        // InnerNotchFrequencyForTests, not just that the wrapper remembers what it was told.
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 100, criticalThresholdSamples: 1000);
        Assert.False(decoder.InnerNotchEnabledForTests);

        decoder.RequestNotch(true, 1750.0);
        decoder.PushSamples(new float[1]); // RequestNotch only queues -- ApplyPendingNotchRequest drains it inside PushSamplesCore
        Assert.True(decoder.InnerNotchEnabledForTests);
        Assert.Equal(1750.0, decoder.InnerNotchFrequencyForTests);

        for (var i = 0; i < 3; i++)
        {
            decoder.PushSamples(new float[50]); // idle silence -- crosses warningThresholdSamples=100 by the 3rd call
        }

        Assert.Equal(1, decoder.RestartCountForTests); // sanity: the swap this test targets actually happened
        Assert.True(decoder.InnerNotchEnabledForTests);
        Assert.Equal(1750.0, decoder.InnerNotchFrequencyForTests);
    }

    [Fact]
    public void RequestPllTuning_SurvivesAPeriodicSwap()
    {
        // Options stub backlog item 1 (docs/plans/options-stub-item1-pll-tuning-plan.md), round-2
        // plan-review correction: unlike RequestNotch above, the re-seed on rebuild is via
        // CreateInner's own AnalogFmSstvDecoder ctor arguments (real ctor params exist for PLL
        // tuning, unlike notch), and it must be UNCONDITIONAL (no off-state to gate on, unlike
        // notch's own `if (_notchEnabled)`). Same non-vacuous proof requirement as RequestNotch's own
        // test above -- assert the LIVE inner decoder's own tuning post-swap via
        // InnerPllTuningForTests, not just that the wrapper remembers what it was told.
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 100, criticalThresholdSamples: 1000);
        var initialTuning = decoder.InnerPllTuningForTests;
        Assert.Equal(1.0, initialTuning.VcoGain); // legacy default, confirms the re-seed test below is a real change, not a no-op

        decoder.RequestPllTuning(vcoGain: 2.5, loopOrder: 6, loopCutoffHz: 1300, outputOrder: 8, outputCutoffHz: 850);
        decoder.PushSamples(new float[1]); // RequestPllTuning only queues -- ApplyPendingPllTuningRequest drains it inside PushSamplesCore
        var afterRequest = decoder.InnerPllTuningForTests;
        Assert.Equal(2.5, afterRequest.VcoGain);
        Assert.Equal(6, afterRequest.LoopOrder);
        Assert.Equal(1300, afterRequest.LoopCutoffHz);
        Assert.Equal(8, afterRequest.OutputOrder);
        Assert.Equal(850, afterRequest.OutputCutoffHz);

        for (var i = 0; i < 3; i++)
        {
            decoder.PushSamples(new float[50]); // idle silence -- crosses warningThresholdSamples=100 by the 3rd call
        }

        Assert.Equal(1, decoder.RestartCountForTests); // sanity: the swap this test targets actually happened
        var afterSwap = decoder.InnerPllTuningForTests;
        Assert.Equal(2.5, afterSwap.VcoGain);
        Assert.Equal(6, afterSwap.LoopOrder);
        Assert.Equal(1300, afterSwap.LoopCutoffHz);
        Assert.Equal(8, afterSwap.OutputOrder);
        Assert.Equal(850, afterSwap.OutputCutoffHz);
    }

    [Fact]
    public void RequestZeroCrossingTuning_SurvivesAPeriodicSwap()
    {
        // Options stub backlog item 2 (docs/plans/options-stub-item2-zerocrossing-tuning-plan.md) --
        // same unconditional store-forward-and-re-seed shape as RequestPllTuning_SurvivesAPeriodicSwap
        // above, mutation-tested by hand the same way.
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 100, criticalThresholdSamples: 1000);
        var initialTuning = decoder.InnerZeroCrossingTuningForTests;
        Assert.Equal(ZeroCrossingSmoothingMode.Iir, initialTuning.SmoothingMode); // legacy default, confirms the re-seed test below is a real change, not a no-op

        decoder.RequestZeroCrossingTuning(ZeroCrossingSmoothingMode.Fir, outputOrder: 8, outputCutoffHz: 850, smoothingFrequencyHz: 2600);
        decoder.PushSamples(new float[1]); // RequestZeroCrossingTuning only queues -- ApplyPendingZeroCrossingTuningRequest drains it inside PushSamplesCore
        var afterRequest = decoder.InnerZeroCrossingTuningForTests;
        Assert.Equal(ZeroCrossingSmoothingMode.Fir, afterRequest.SmoothingMode);
        Assert.Equal(8, afterRequest.OutputOrder);
        Assert.Equal(850, afterRequest.OutputCutoffHz);
        Assert.Equal(2600, afterRequest.SmoothingFrequencyHz);

        for (var i = 0; i < 3; i++)
        {
            decoder.PushSamples(new float[50]); // idle silence -- crosses warningThresholdSamples=100 by the 3rd call
        }

        Assert.Equal(1, decoder.RestartCountForTests); // sanity: the swap this test targets actually happened
        var afterSwap = decoder.InnerZeroCrossingTuningForTests;
        Assert.Equal(ZeroCrossingSmoothingMode.Fir, afterSwap.SmoothingMode);
        Assert.Equal(8, afterSwap.OutputOrder);
        Assert.Equal(850, afterSwap.OutputCutoffHz);
        Assert.Equal(2600, afterSwap.SmoothingFrequencyHz);
    }

    [Fact]
    public void SenseLevel_ConstructorValue_SurvivesAPeriodicSwap()
    {
        // User-reported (2026-08-27, "Squelch level" live control): SenseLevel is deliberately
        // LIVE-settable now, same shape as StationIdDecodeEnabled -- same non-vacuous proof
        // requirement as that property's own swap-survival tests above: InnerSenseLevelForTests/
        // InnerVisLockThresholdsForTests read the LIVE inner decoder's own state post-swap, not just
        // the wrapper's stored field.
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 100, criticalThresholdSamples: 1000, senseLevel: 3);
        Assert.Equal(3, decoder.SenseLevel);
        Assert.Equal(3, decoder.InnerSenseLevelForTests);

        for (var i = 0; i < 3; i++)
        {
            decoder.PushSamples(new float[50]); // idle silence -- crosses warningThresholdSamples=100 by the 3rd call
        }

        Assert.Equal(1, decoder.RestartCountForTests); // sanity: the swap this test targets actually happened
        Assert.Equal(3, decoder.SenseLevel);
        Assert.Equal(3, decoder.InnerSenseLevelForTests);
        var expected = AnalogFmSstvDecoder.SenseLevelPresets[3];
        Assert.Equal((expected.SLvl, expected.SLvl2), decoder.InnerVisLockThresholdsForTests);
    }

    [Fact]
    public void SenseLevel_SetLiveAfterConstruction_AlsoSurvivesAPeriodicSwap()
    {
        // Same finding as SenseLevel_ConstructorValue_SurvivesAPeriodicSwap above, but for the
        // property SETTER path -- proves the setter updates the wrapper's STORED field, not just the
        // current inner instance directly (the only way a later swap could know to re-apply it). An
        // earlier draft of this feature forwarded straight to the inner decoder with no wrapper-level
        // storage, which this exact test would have caught: the value would have silently reverted
        // to the constructor default (1) at the swap below.
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 100, criticalThresholdSamples: 1000, senseLevel: 1);
        Assert.Equal(1, decoder.SenseLevel);

        decoder.SenseLevel = 3;
        Assert.Equal(3, decoder.SenseLevel); // wrapper's own stored field, immediate

        for (var i = 0; i < 3; i++)
        {
            decoder.PushSamples(new float[50]); // 1st call also drains the deferred inner request; 3rd crosses warningThresholdSamples=100
        }

        Assert.Equal(1, decoder.RestartCountForTests);
        Assert.Equal(3, decoder.SenseLevel);
        Assert.Equal(3, decoder.InnerSenseLevelForTests);
        var expected = AnalogFmSstvDecoder.SenseLevelPresets[3];
        Assert.Equal((expected.SLvl, expected.SLvl2), decoder.InnerVisLockThresholdsForTests);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(99)]
    public void SenseLevel_ConstructorOutOfRangeValue_ClampsToZero(int outOfRangeValue)
    {
        // Auditor round-2 finding: SstvDecoderSettings.Resolve() does NOT actually clamp an
        // out-of-range SenseLevel despite its own doc comment claiming it does (a pre-existing bug
        // that became load-bearing once this wrapper's OWN getter became authoritative, reading its
        // stored field instead of forwarding to _inner). This wrapper must clamp itself in the
        // constructor -- dropping that clamp leaves the rest of this suite green (nothing else
        // exercises an out-of-range constructor value), so it needs its own direct test.
        var decoder = new RestartableSstvDecoder(afcEnabled: true, senseLevel: outOfRangeValue);

        Assert.Equal(0, decoder.SenseLevel);
        Assert.Equal(0, decoder.InnerSenseLevelForTests);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void SenseLevel_SetterOutOfRangeValue_ClampsToZero(int outOfRangeValue)
    {
        // Same reasoning as SenseLevel_ConstructorOutOfRangeValue_ClampsToZero above, for the
        // setter path -- the production caller (RxImagePaneViewModel.OnSenseLevelChanged) already
        // guards against this, but this wrapper's own defensive clamp is a separate layer that
        // needs its own direct proof, not just an implication from the VM-layer test.
        var decoder = new RestartableSstvDecoder(afcEnabled: true, senseLevel: 1);

        decoder.SenseLevel = outOfRangeValue;
        decoder.PushSamples(new float[8]); // drains AnalogFmSstvDecoder's own deferred SenseLevel request

        Assert.Equal(0, decoder.SenseLevel);
        Assert.Equal(0, decoder.InnerSenseLevelForTests);
    }

    [Fact]
    public void ArmScopeCapture_Channel0_SurvivesAPeriodicSwap()
    {
        // Un-stub-RX-tab Piece B: unlike RequestNotch above, there is no wrapper-level "re-seed on
        // rebuild" step to prove here -- the state that must survive a restart is the capture's OWN
        // fill progress, which lives on the SHARED ScopeCaptureBuffer instances CreateInner hands to
        // every fresh inner decoder (see RestartableSstvDecoder's own _scopeCaptureChannel0 doc
        // comment). If a future change ever passed CreateInner a FRESH buffer instead of the shared
        // instance, post-restart writes would land on an orphaned buffer this wrapper's own
        // TryGetScopeCaptureChannel0 never reads -- the capture would stall forever instead of
        // completing, which is exactly what this test would catch (a bounded loop that fails to
        // observe completion), not something a "restart happened" assertion alone could catch.
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 20_000, criticalThresholdSamples: 1_000_000);
        decoder.ArmScopeCapture(200_000); // large enough to still be incomplete once a restart lands
        decoder.PushSamples(new float[1]); // drains the arm request

        var sawRestartBeforeCompletion = false;
        for (var i = 0; i < 30 && decoder.TryGetScopeCaptureChannel0() is null; i++)
        {
            // The restart-threshold check uses TotalSamplesReceived as of the START of each call (see
            // RestartableSstvDecoder's own PushSamplesCore), so which push number actually crosses
            // warningThresholdSamples isn't pinned to an exact iteration here -- only that it happens
            // at some point before this capture completes, which is what this test needs to prove.
            decoder.PushSamples(new float[25_000]);
            if (decoder.RestartCountForTests > 0)
            {
                sawRestartBeforeCompletion = true;
            }
        }

        var channel0 = decoder.TryGetScopeCaptureChannel0();
        Assert.NotNull(channel0);
        Assert.Equal(200_000, channel0!.Length);
        Assert.True(sawRestartBeforeCompletion, "Test setup problem -- no restart happened before the capture completed, so this test never actually exercised cross-restart persistence.");
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
        // `using`: round-8 D2-audit nit fix -- RxBufferMode.Extended constructs a disk-backed
        // RxDiskLineStagingBuffer; without it, this instance's own scratch files leak past this
        // test's own end (same fix its sibling Swap_DisposesTheOutgoingInnerDecodersScratchFiles below
        // already carries).
        using var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 100, criticalThresholdSamples: 1000, rxBufferMode: RxBufferMode.Extended);
        Assert.Equal(RxBufferMode.Extended, decoder.InnerRxBufferModeForTests);

        for (var i = 0; i < 3; i++)
        {
            decoder.PushSamples(new float[50]); // idle silence -- crosses warningThresholdSamples=100 by the 3rd call
        }

        Assert.Equal(1, decoder.RestartCountForTests); // sanity: the swap this test targets actually happened
        Assert.Equal(RxBufferMode.Extended, decoder.InnerRxBufferModeForTests);
    }

    [Fact]
    public void AfcSyncRestartAutoSyncAutoStopSenseLevel_ConstructorValues_SurviveAPeriodicSwap()
    {
        // Round-8 D2-audit finding: CreateInner() forwards afcEnabled/syncRestartEnabled/
        // autoSyncEnabled/autoStopEnabled/senseLevel like every other constructor-injected toggle
        // above, but none of the five had an Inner*ForTests accessor -- no other test in this file
        // ever pins any of the five to a non-default value (round-10 correction: dropping the
        // round-9 fix's own enumeration of specific call sites here, since it named the wrong test
        // and miscounted -- exactly the citation-drift failure class this codebase's D6 audit chunk
        // is documented elsewhere as being prone to; the load-bearing claim needs no site list, only
        // that none exists), so dropping any one of these five arguments from CreateInner would
        // leave the whole suite green while silently reverting a non-default user setting to the C#
        // parameter default on every periodic restart.
        // Same InnerXForTests pattern as DemodType/RxBpfPreset/RxBufferMode above; all five are
        // exercised together since they share the same "restart-only, no live read-back" shape.
        // Non-default on all five so no assertion can pass vacuously against a parameter default
        // (afcEnabled/syncRestartEnabled/autoSyncEnabled default true, autoStopEnabled defaults false,
        // senseLevel defaults 1). `using`: round-9 nit fix for consistency with this file's other
        // RestartableSstvDecoder tests -- harmless either way here since the default RxBufferMode.On
        // gives the inner decoder a RAM staging buffer whose Dispose() is a documented no-op.
        using var decoder = new RestartableSstvDecoder(
            afcEnabled: false,
            warningThresholdSamples: 100,
            criticalThresholdSamples: 1000,
            syncRestartEnabled: false,
            autoSyncEnabled: false,
            autoStopEnabled: true,
            senseLevel: 3);
        Assert.False(decoder.InnerAfcEnabledForTests);
        Assert.False(decoder.InnerSyncRestartEnabledForTests);
        Assert.False(decoder.InnerAutoSyncEnabledForTests);
        Assert.True(decoder.InnerAutoStopEnabledForTests);
        Assert.Equal(3, decoder.InnerSenseLevelForTests);

        for (var i = 0; i < 3; i++)
        {
            decoder.PushSamples(new float[50]); // idle silence -- crosses warningThresholdSamples=100 by the 3rd call
        }

        Assert.Equal(1, decoder.RestartCountForTests); // sanity: the swap this test targets actually happened
        Assert.False(decoder.InnerAfcEnabledForTests);
        Assert.False(decoder.InnerSyncRestartEnabledForTests);
        Assert.False(decoder.InnerAutoSyncEnabledForTests);
        Assert.True(decoder.InnerAutoStopEnabledForTests);
        Assert.Equal(3, decoder.InnerSenseLevelForTests);
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
    public async Task Swap_DisposesOutgoingInstanceAfterReleasingTheLock_PushDrivenPath()
    {
        // T0-7 regression test (production_audit.md): before this fix, Swap() disposed the outgoing
        // instance INSIDE lock (_gate) -- every UI-facing property getter (SignalPeakLevel/SlantPpm/
        // SyncSource/BufferedSampleCount/etc.) blocks on that same lock, so a slow disposal (up to
        // ~10s worst case, RxDiskLineStagingBuffer's own bounded drain) froze every one of them.
        //
        // Critical design point: the property read below MUST happen from a DIFFERENT thread than
        // the one driving the swap. lock (_gate) is Monitor, which is RE-ENTRANT on the same thread
        // -- a same-thread read would succeed instantly even against the UNFIXED code (re-entering
        // your own already-held lock always succeeds), proving nothing. This is exactly the
        // "shared-gate test passes against unfixed code" failure class this project has already been
        // burned by (feedback_deterministic_gates_not_shared_race).
        //
        // Auditor code-review correction: the hook's own bound and the property-read assertion's
        // bound are DELIBERATELY ASYMMETRIC (30s vs 2s), not both 5s. A plausible future regression
        // -- moving the whole `try { hook; dispose } catch {}` block back inside the lock -- would
        // swallow the hook's own timeout assertion, collapsing detection to a coin flip between two
        // near-simultaneous 5s deadlines (Task.Delay's own coarse timer resolution actively biases
        // toward a FALSE PASS in that race). A 30s-vs-2s gap makes any mutation that holds _gate
        // across the hook block the read past 2s deterministically, regardless of exception
        // swallowing. try/finally around the body releases releaseDispose even on assertion failure,
        // so a failing run doesn't also stall `using var decoder`'s own Dispose() (which takes _gate)
        // for the full 30s.
        var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispose = new ManualResetEventSlim(initialState: false);
        using var decoder = new RestartableSstvDecoder(
            afcEnabled: true, warningThresholdSamples: 100, criticalThresholdSamples: 1000, rxBufferMode: RxBufferMode.Extended,
            outgoingDisposeStartingForTests: () =>
            {
                disposeStarted.TrySetResult();
                Assert.True(releaseDispose.Wait(TimeSpan.FromSeconds(30)), "releaseDispose was never signaled -- see this test's own comment.");
            });

        var worker = Task.Run(() =>
        {
            for (var i = 0; i < 3; i++)
            {
                decoder.PushSamples(new float[50]); // idle silence -- crosses warningThresholdSamples=100 by the 3rd call
            }
        });

        try
        {
            await disposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The load-bearing assertion: a property read from ANOTHER thread completes promptly
            // while disposal is artificially held open -- proving _gate is already free at this
            // point. Pre-fix (dispose still inside the lock), this read would block past the 2s
            // bound below instead.
            var propertyRead = Task.Run(() => _ = decoder.BufferedSampleCount);
            var propertyReadCompleted = await Task.WhenAny(propertyRead, Task.Delay(TimeSpan.FromSeconds(2))) == propertyRead;
            Assert.True(propertyReadCompleted, "a UI-facing property read should not block while the outgoing instance's disposal is in flight.");
        }
        finally
        {
            releaseDispose.Set();
        }

        await worker.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, decoder.RestartCountForTests); // sanity: the swap this test targets actually happened
    }

    [Fact]
    public async Task Swap_DisposesOutgoingInstanceAfterReleasingTheLock_ApplyPendingReconfigurationNowPath()
    {
        // T0-7 regression test, second call site: same proof as the push-driven test above, but for
        // ApplyPendingReconfigurationNow's own Swap() call -- the fix's caller-side dispose placement
        // differs between the two call sites (this one stays inside ApplyPendingReconfigurationNow's
        // own _pushActive hold, per this method's own doc comment), so both need independent coverage.
        // Same asymmetric-bound reasoning as the push-driven test above (30s hook vs 2s assertion,
        // try/finally around the body) -- see that test's own comment for why.
        var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispose = new ManualResetEventSlim(initialState: false);
        using var decoder = new RestartableSstvDecoder(
            afcEnabled: true, warningThresholdSamples: 100, criticalThresholdSamples: 1000, rxBufferMode: RxBufferMode.Extended,
            outgoingDisposeStartingForTests: () =>
            {
                disposeStarted.TrySetResult();
                Assert.True(releaseDispose.Wait(TimeSpan.FromSeconds(30)), "releaseDispose was never signaled -- see this test's own comment.");
            });
        decoder.RequestReconfiguration(RxBpfPreset.Narrow, DemodType.Pll, RxBufferMode.Extended);

        var worker = Task.Run(() => decoder.ApplyPendingReconfigurationNow());

        SwapResult result;
        try
        {
            await disposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var propertyRead = Task.Run(() => _ = decoder.BufferedSampleCount);
            var propertyReadCompleted = await Task.WhenAny(propertyRead, Task.Delay(TimeSpan.FromSeconds(2))) == propertyRead;
            Assert.True(propertyReadCompleted, "a UI-facing property read should not block while the outgoing instance's disposal is in flight.");
        }
        finally
        {
            releaseDispose.Set();
        }

        result = await worker.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(SwapResult.Committed, result);
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
    public async Task RxBufferDegraded_ForwardsFromInnerStagingBuffer_OnceAWriteFailureLatches()
    {
        // T0-6: proves RestartableSstvDecoder.RxBufferDegraded genuinely forwards the underlying
        // disk staging buffer's write-failure latch, not just a stale default. The latch is async
        // (the background consumer task observes the corrupted stream on its own next write), so
        // this polls with a bounded wait rather than asserting synchronously. Drives the inner
        // RxDiskLineStagingBuffer directly (same pattern as Dispose_DisposesTheCurrentInnerDecoder_
        // AndIsIdempotent above) rather than through PushSamples, since the property under test only
        // depends on the staging buffer's own latch, not on a real decoded line.
        using var decoder = new RestartableSstvDecoder(rxBufferMode: RxBufferMode.Extended);
        var inner = (RxDiskLineStagingBuffer)decoder.InnerRxLineStagingBufferForTests!;

        Assert.False(decoder.RxBufferDegraded);

        Assert.True(inner.TryAppendLine([1.0], [2.0]));
        _ = inner.DemodulatedAt(0); // forces a drain -- line 1 is now guaranteed flushed
        inner.CorruptWriteStreamForTests();
        inner.TryAppendLine([3.0], [4.0]); // the background consumer's next write now throws

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!decoder.RxBufferDegraded && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(decoder.RxBufferDegraded, "RestartableSstvDecoder.RxBufferDegraded should forward the inner staging buffer's latched write failure.");
    }

    [Fact]
    public void PushSamples_ThrowsObjectDisposedException_AfterDispose()
    {
        // D2 round 3 correction: the round-2 version of this test used the PUBLIC ctor (12h/13h-sample
        // thresholds), so a single 16-sample post-dispose push could never reach the critical/warning
        // branch that calls Swap() -- it just fell through to the ALREADY-disposed inner's own
        // ObjectDisposedException (AnalogFmSstvDecoder's round-1 guard), passing even with THIS
        // wrapper's own round-2 guard fully reverted. That test didn't pin the regression it claimed
        // to. Fixed by using the internal short-threshold ctor and priming the inner's own sample count
        // above the critical threshold BEFORE disposal, so the post-dispose call genuinely lands in the
        // Swap()-calling branch if this wrapper's own guard is missing -- and asserting RestartCountForTests
        // never moves, which is the actual leaked-Swap() signature this test exists to catch.
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1, criticalThresholdSamples: 1);
        decoder.PushSamples(new float[16]); // inner's TotalSamplesReceived now >= criticalThresholdSamples
        var restartCountBeforeDispose = decoder.RestartCountForTests;
        decoder.Dispose();

        Assert.Throws<ObjectDisposedException>(() => decoder.PushSamples(new float[16]));
        Assert.Equal(restartCountBeforeDispose, decoder.RestartCountForTests); // Swap() must never have run
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
    public void StationIdDecoded_AcrossBackToBackTransmissions_StampsEachWithItsOwnReceptionSequence()
    {
        // fsk_cwid.md A1: FskStationIdDecodedInfo.ReceptionSequence must identify WHICH reception a
        // decoded station ID belongs to -- this is the one property persistence/correlation (A2) and
        // the RX pane's stale guard (A5) both depend on. The ordering assumption this pins (fsk_cwid.md's
        // own words): "the FSK ID is transmitted after the image (Main.cpp:7016-7019), and the scan
        // loop processes samples in order with TryNarrowFskScan called before the header scan in the
        // same push (AnalogFmSstvDecoder.cs:3759), so the station-ID raise for reception A should
        // always precede B's ModeDetected" -- exercised end to end (real encode -> real decode -> the
        // wrapper's own forwarded/stamped event), not asserted from source reading alone.
        const int sampleRate = 11025;
        var mode = SstvModeRegistry.Robot36;
        var stationIdA = new StationIdTransmitOptions { FskIdEnabled = true, Callsign = "W1AW" };
        var stationIdB = new StationIdTransmitOptions { FskIdEnabled = true, Callsign = "K2ABC" };
        var samplesA = EncodeRealTransmission(mode, out _, stationIdA);
        var samplesB = EncodeRealTransmission(mode, out _, stationIdB);
        var combined = samplesA.Concat(samplesB).Concat(new float[sampleRate * 2]).ToArray();

        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: long.MaxValue, criticalThresholdSamples: long.MaxValue, stationIdDecodeEnabled: true);
        var modeSequences = new List<long>();
        var stationIdEvents = new List<FskStationIdDecodedInfo>();
        decoder.ModeDetected += _ => modeSequences.Add(decoder.ReceptionSequence);
        decoder.StationIdDecoded += info => stationIdEvents.Add(info);

        decoder.PushSamples(combined);

        Assert.Equal(2, modeSequences.Count);
        Assert.Equal(2, stationIdEvents.Count);
        Assert.Equal("W1AW", stationIdEvents[0].Callsign);
        Assert.Equal("K2ABC", stationIdEvents[1].Callsign);
        // The core assertion: each station ID is stamped with ITS OWN reception's sequence, not
        // swapped and not both carrying the same (e.g. final) value.
        Assert.Equal(modeSequences[0], stationIdEvents[0].ReceptionSequence);
        Assert.Equal(modeSequences[1], stationIdEvents[1].ReceptionSequence);
        Assert.NotEqual(stationIdEvents[0].ReceptionSequence, stationIdEvents[1].ReceptionSequence);
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
        // InnerReplayPassCountForTests passthrough (added after auditor code-review round 1 flagged the
        // original LineDecoded-count discriminator as weaker than it needed to be -- it would also pass
        // if the decoder restarted mid-run and decoded extra lines with no replay at all; buffered-replay
        // fix, §15 item 2, retired the original InnerRxBufferBaseTransmissionLineForTests oracle this
        // used, since that field stays 0 forever now) for the SAME proof-positive precision the direct
        // AnalogFmSstvDecoder tests use: this counter only ever increments inside PerformReplay's own
        // tail, on a genuinely completed pass.
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
        Assert.True(decoder.InnerReplayPassCountForTests > 0, "PerformReplay never ran on the live inner decoder -- RequestCorrectSlant() may not have reached it.");
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
    public void PushSamples_CriticalSwap_RaisesMaintenanceEvents_WhenReplacementPushThrows()
    {
        var creationCount = 0;
        AnalogFmSstvDecoder CreateDecoder(int sampleRate)
        {
            var inner = new AnalogFmSstvDecoder(sampleRate);
            creationCount++;
            if (creationCount == 2)
            {
                inner.Dispose();
            }

            return inner;
        }

        using var decoder = new RestartableSstvDecoder(
            afcEnabled: true,
            warningThresholdSamples: 1,
            criticalThresholdSamples: 1,
            decoderFactoryForTests: CreateDecoder);
        decoder.PushSamples(new float[16]);

        var criticalCount = 0;
        var restartedCount = 0;
        decoder.RestartCriticallyOverdue += () => criticalCount++;
        decoder.Restarted += () => restartedCount++;

        Assert.Throws<ObjectDisposedException>(() => decoder.PushSamples(new float[1]));
        Assert.Equal(1, decoder.RestartCountForTests);
        Assert.Equal(1, criticalCount);
        Assert.Equal(1, restartedCount);
    }

    [Fact]
    public void PushSamples_CriticalHandlerThrows_StillAttemptsRestartedNotification()
    {
        using var decoder = new RestartableSstvDecoder(
            afcEnabled: true,
            warningThresholdSamples: 1,
            criticalThresholdSamples: 1);
        decoder.PushSamples(new float[1]);

        var restartedCount = 0;
        decoder.RestartCriticallyOverdue += () => throw new InvalidOperationException("Injected critical-handler failure.");
        decoder.Restarted += () => restartedCount++;

        Assert.Throws<InvalidOperationException>(() => decoder.PushSamples(new float[1]));
        Assert.Equal(1, decoder.RestartCountForTests);
        Assert.Equal(1, restartedCount);
    }

    [Fact]
    public void PushSamples_WhenReplacementConstructionThrows_KeepsOutgoingInnerObservable()
    {
        var outgoing = new AnalogFmSstvDecoder();
        var creationCount = 0;
        AnalogFmSstvDecoder CreateDecoder(int sampleRate)
        {
            creationCount++;
            return creationCount == 1
                ? outgoing
                : throw new IOException("Injected replacement-construction failure.");
        }

        using var decoder = new RestartableSstvDecoder(
            afcEnabled: true,
            warningThresholdSamples: 1,
            criticalThresholdSamples: 1,
            decoderFactoryForTests: CreateDecoder);
        decoder.PushSamples(new float[16]);

        SstvModeDefinition? detectedMode = null;
        decoder.ModeDetected += mode => detectedMode = mode;

        Assert.Throws<IOException>(() => decoder.PushSamples(new float[1]));
        Assert.Equal(0, decoder.RestartCountForTests);

        decoder.ForceMode(SstvModeRegistry.Avt);
        outgoing.PushSamples(new float[64]);
        Assert.Equal(SstvModeRegistry.Avt.Id, detectedMode?.Id);
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

        var thresholds = RestartableSstvDecoder.ComputeDefaultThresholds(11025);
        Assert.Equal(RestartableSstvDecoder.DefaultWarningThresholdSamples, thresholds.WarningThresholdSamples);
        Assert.Equal(RestartableSstvDecoder.DefaultCriticalThresholdSamples, thresholds.CriticalThresholdSamples);
    }

    [Fact]
    public void ProductionThresholds_AtMaximumRate_StayInsideProjectionReserve()
    {
        var thresholds = RestartableSstvDecoder.ComputeDefaultThresholds(SstvSampleRate.Maximum);

        Assert.True(thresholds.WarningThresholdSamples < thresholds.CriticalThresholdSamples);
        Assert.True(thresholds.CriticalThresholdSamples <= thresholds.MaximumSafeSampleIndex);
        Assert.Equal(int.MaxValue - thresholds.ProjectionReserveSamples, thresholds.MaximumSafeSampleIndex);
        Assert.True(thresholds.CriticalThresholdSamples < 13L * 3600L * SstvSampleRate.Maximum);
    }

    [Theory]
    [InlineData(5000, 216000000, 234000000, 2128804399, 18679248)]
    [InlineData(11025, 476280000, 515970000, 2106295903, 41187744)]
    [InlineData(48500, 1791694999, 1966294999, 1966294999, 181188648)]
    public void ProductionThresholds_HaveExactExpectedValues(
        int sampleRate,
        long expectedWarning,
        long expectedCritical,
        int expectedMaximumSafeIndex,
        long expectedProjectionReserve)
    {
        var thresholds = RestartableSstvDecoder.ComputeDefaultThresholds(sampleRate);

        Assert.Equal(expectedWarning, thresholds.WarningThresholdSamples);
        Assert.Equal(expectedCritical, thresholds.CriticalThresholdSamples);
        Assert.Equal(expectedMaximumSafeIndex, thresholds.MaximumSafeSampleIndex);
        Assert.Equal(expectedProjectionReserve, thresholds.ProjectionReserveSamples);
    }

    [Fact]
    public void ComposedForwardProjection_IsInsideReserve_AtEverySupportedIntegerRate()
    {
        for (var sampleRate = SstvSampleRate.Minimum; sampleRate <= SstvSampleRate.Maximum; sampleRate++)
        {
            var thresholds = RestartableSstvDecoder.ComputeDefaultThresholds(sampleRate);
            var composedProjection = RestartableSstvDecoder.ComputeMaximumComposedProjectionSamples(sampleRate);
            Assert.True(
                composedProjection < thresholds.ProjectionReserveSamples,
                $"Composed projection {composedProjection} must fit reserve {thresholds.ProjectionReserveSamples} at {sampleRate} Hz.");
        }
    }

    // Frozen review values, deliberately not recomputed from the production constants: this test
    // must fail if any named forward term is accidentally deleted from the composed horizon.
    [Theory]
    [InlineData(5000, 3077204)]
    [InlineData(11025, 6782821)]
    [InlineData(48500, 29831430)]
    public void ComposedForwardProjection_HasIndependentlyPinnedEndpointValues(int sampleRate, long expectedSamples) =>
        Assert.Equal(expectedSamples, RestartableSstvDecoder.ComputeMaximumComposedProjectionSamples(sampleRate));

    [Fact]
    public void SampleRate_SurvivesPeriodicSwap()
    {
        using var decoder = new RestartableSstvDecoder(
            afcEnabled: true,
            warningThresholdSamples: 1,
            criticalThresholdSamples: 1,
            sampleRate: 22050);

        decoder.PushSamples(new float[1]);
        decoder.PushSamples(new float[1]);

        Assert.Equal(1, decoder.RestartCountForTests);
        Assert.Equal(22050, decoder.SampleRate);
    }

    [Fact]
    public void Constructor_RejectsInitialFactoryRateMismatch_AndDisposesCandidate()
    {
        var candidate = new AnalogFmSstvDecoder(SstvSampleRate.Default);

        Assert.Throws<InvalidOperationException>(() => new RestartableSstvDecoder(
            afcEnabled: true,
            warningThresholdSamples: 10,
            criticalThresholdSamples: 20,
            sampleRate: 22050,
            decoderFactoryForTests: _ => candidate));

        Assert.Throws<ObjectDisposedException>(() => candidate.PushSamples(new float[1]));
    }

    [Fact]
    public void CapacityReplacementRateMismatch_KeepsOutgoingInstalledAndDisposesCandidate()
    {
        var outgoing = new AnalogFmSstvDecoder(SstvSampleRate.Default);
        var mismatch = new AnalogFmSstvDecoder(22050);
        var creationCount = 0;
        using var decoder = new RestartableSstvDecoder(
            afcEnabled: true,
            warningThresholdSamples: long.MaxValue,
            criticalThresholdSamples: long.MaxValue,
            maximumSafeSampleIndex: 10,
            decoderFactoryForTests: _ => creationCount++ == 0 ? outgoing : mismatch);

        SstvModeDefinition? detectedMode = null;
        decoder.ModeDetected += mode => detectedMode = mode;

        decoder.PushSamples(new float[10]);
        Assert.Throws<InvalidOperationException>(() => decoder.PushSamples(new float[1]));
        Assert.Equal(0, decoder.RestartCountForTests);
        Assert.Throws<ObjectDisposedException>(() => mismatch.PushSamples(new float[1]));

        decoder.ForceMode(SstvModeRegistry.Avt);
        outgoing.PushSamples(new float[64]);
        Assert.False(outgoing.IsIdle);
        Assert.Equal(SstvModeRegistry.Avt.Id, detectedMode?.Id);
    }

    [Fact]
    public void CapacityPreflight_AcceptsEquality_ThenSwapsBeforeOneMoreSample()
    {
        using var decoder = new RestartableSstvDecoder(
            afcEnabled: true,
            warningThresholdSamples: long.MaxValue,
            criticalThresholdSamples: long.MaxValue,
            maximumSafeSampleIndex: 10);
        var events = new List<string>();
        decoder.RestartCriticallyOverdue += () => events.Add("critical");
        decoder.Restarted += () => events.Add("restarted");

        decoder.PushSamples(new float[10]);
        Assert.Equal(0, decoder.RestartCountForTests);

        decoder.PushSamples(new float[1]);

        Assert.Equal(1, decoder.RestartCountForTests);
        Assert.Equal(["critical", "restarted"], events);
    }

    [Fact]
    public void CapacityPreflight_RejectsSingleOversizedChunkBeforeSwapOrEvents()
    {
        using var decoder = new RestartableSstvDecoder(
            afcEnabled: true,
            warningThresholdSamples: long.MaxValue,
            criticalThresholdSamples: long.MaxValue,
            maximumSafeSampleIndex: 10);
        var eventCount = 0;
        decoder.RestartCriticallyOverdue += () => eventCount++;
        decoder.Restarted += () => eventCount++;

        Assert.Throws<ArgumentOutOfRangeException>(() => decoder.PushSamples(new float[11]));
        Assert.Equal(0, decoder.RestartCountForTests);
        Assert.Equal(0, eventCount);
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
