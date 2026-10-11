using System.Reflection;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>Characterization tests for <see cref="RestartableSstvDecoder"/>'s settings mirroring: every
/// persistent decoder setting must reach the LIVE inner decoder after an inner-decoder swap, on both the
/// periodic idle swap path and the reconfiguration swap path. Each case asserts the full snapshot of the
/// live inner decoder, so a setting that leaks into a sibling field is caught, not just a dropped one.
/// The surface guard fails when a new setting or settings-like interface member appears without a row.</summary>
public sealed class DecoderSettingsSwapSurvivalTests
{
    private const int SwapRate = 22050;
    private const int QueuedRate = 16000;
    private const long TestWarningThreshold = 100;
    private const long TestCriticalThreshold = 1000;

    /// <summary>The internal ctor's defaults, with SNR off (the wrapper's own default).</summary>
    private static readonly ResolvedSstvDecoderSettings Baseline = new(
        AfcEnabled: true,
        SyncRestartEnabled: true,
        AutoSyncEnabled: true,
        AutoStopEnabled: false,
        AutoSlantEnabled: true,
        SenseLevel: 1,
        DemodType: DemodType.Hilbert,
        RxBpfPreset: RxBpfPreset.Wide,
        RxBufferMode: RxBufferMode.On,
        PllVcoGain: 1.0,
        PllLoopOrder: 1,
        PllLoopCutoffHz: 1500,
        PllOutputOrder: 3,
        PllOutputCutoffHz: 900,
        ZeroCrossingSmoothingMode: ZeroCrossingSmoothingMode.Iir,
        ZeroCrossingOutputOrder: 3,
        ZeroCrossingOutputCutoffHz: 900,
        ZeroCrossingSmoothingFrequencyHz: 2200,
        SnrMeasurementEnabled: false);

    /// <summary>One-shot commands: nothing to survive a swap, so no row covers them.</summary>
    private static readonly string[] OneShotMembers =
    [
        nameof(ISstvDecoder.ResetAgc),
        nameof(ISstvDecoder.RequestReSync),
        nameof(ISstvDecoder.ArmScopeCapture),
        nameof(ISstvDecoder.ForceMode),
        nameof(ISstvDecoder.RequestAbandonReception),
        nameof(ISstvDecoder.RequestCorrectSlant),
        nameof(ISstvDecoder.PushSamples),
        nameof(ISstvDecoderReconfiguration.ApplyPendingReconfigurationNow),
        nameof(ISstvDecoder.TryGetScopeCaptureChannel0),
        nameof(ISstvDecoder.TryGetScopeCaptureChannel1),
    ];

    private sealed record DecoderSnapshot(
        ResolvedSstvDecoderSettings Settings,
        bool StationIdDecodeEnabled,
        bool NotchEnabled,
        double? NotchFrequencyHz,
        string? LockedModeId,
        int SampleRate);

    private sealed record SurvivalRow(
        string Name,
        string[] Covers,
        Action<RestartableSstvDecoder> Apply,
        Func<DecoderSnapshot, DecoderSnapshot> Expect,
        int? QueuedSampleRate = null);

    private static readonly SurvivalRow[] SurvivalRows =
    [
        new("Afc", [nameof(ResolvedSstvDecoderSettings.AfcEnabled)],
            d => d.AfcEnabled = false,
            s => s with { Settings = s.Settings with { AfcEnabled = false } }),
        new("SyncRestart", [nameof(ResolvedSstvDecoderSettings.SyncRestartEnabled)],
            d => d.SyncRestartEnabled = false,
            s => s with { Settings = s.Settings with { SyncRestartEnabled = false } }),
        new("AutoSync", [nameof(ResolvedSstvDecoderSettings.AutoSyncEnabled)],
            d => d.AutoSyncEnabled = false,
            s => s with { Settings = s.Settings with { AutoSyncEnabled = false } }),
        new("AutoStop", [nameof(ResolvedSstvDecoderSettings.AutoStopEnabled)],
            d => d.AutoStopEnabled = true,
            s => s with { Settings = s.Settings with { AutoStopEnabled = true } }),
        new("AutoSlant", [nameof(ResolvedSstvDecoderSettings.AutoSlantEnabled)],
            d => d.AutoSlantEnabled = false,
            s => s with { Settings = s.Settings with { AutoSlantEnabled = false } }),
        new("SenseLevel", [nameof(ResolvedSstvDecoderSettings.SenseLevel)],
            d => d.SenseLevel = 3,
            s => s with { Settings = s.Settings with { SenseLevel = 3 } }),
        new("SenseLevelOutOfRange", [nameof(ResolvedSstvDecoderSettings.SenseLevel)],
            d => d.SenseLevel = 9,
            s => s with { Settings = s.Settings with { SenseLevel = 0 } }),
        new("SnrMeasurement", [nameof(ResolvedSstvDecoderSettings.SnrMeasurementEnabled)],
            d => d.SnrMeasurementEnabled = true,
            s => s with { Settings = s.Settings with { SnrMeasurementEnabled = true } }),
        new("StationIdDecode", [nameof(ISstvDecoder.StationIdDecodeEnabled)],
            d => d.StationIdDecodeEnabled = true,
            s => s with { StationIdDecodeEnabled = true }),
        new("Notch", [nameof(ISstvDecoder.RequestNotch)],
            d => d.RequestNotch(true, 1800),
            s => s with { NotchEnabled = true, NotchFrequencyHz = 1800 }),
        new("ModeLock", [nameof(ISstvDecoder.SetModeLock)],
            d => d.SetModeLock(SstvModeRegistry.Robot36),
            s => s with { LockedModeId = SstvModeRegistry.Robot36.Id }),
        new("PllTuning",
            [
                nameof(ISstvDecoder.RequestPllTuning),
                nameof(ResolvedSstvDecoderSettings.PllVcoGain),
                nameof(ResolvedSstvDecoderSettings.PllLoopOrder),
                nameof(ResolvedSstvDecoderSettings.PllLoopCutoffHz),
                nameof(ResolvedSstvDecoderSettings.PllOutputOrder),
                nameof(ResolvedSstvDecoderSettings.PllOutputCutoffHz),
            ],
            d => d.RequestPllTuning(2.5, 2, 1700, 4, 1100),
            s => s with
            {
                Settings = s.Settings with
                {
                    PllVcoGain = 2.5,
                    PllLoopOrder = 2,
                    PllLoopCutoffHz = 1700,
                    PllOutputOrder = 4,
                    PllOutputCutoffHz = 1100,
                },
            }),
        new("ZeroCrossingTuning",
            [
                nameof(ISstvDecoder.RequestZeroCrossingTuning),
                nameof(ResolvedSstvDecoderSettings.ZeroCrossingSmoothingMode),
                nameof(ResolvedSstvDecoderSettings.ZeroCrossingOutputOrder),
                nameof(ResolvedSstvDecoderSettings.ZeroCrossingOutputCutoffHz),
                nameof(ResolvedSstvDecoderSettings.ZeroCrossingSmoothingFrequencyHz),
            ],
            d => d.RequestZeroCrossingTuning(ZeroCrossingSmoothingMode.Fir, 5, 1000, 2500),
            s => s with
            {
                Settings = s.Settings with
                {
                    ZeroCrossingSmoothingMode = ZeroCrossingSmoothingMode.Fir,
                    ZeroCrossingOutputOrder = 5,
                    ZeroCrossingOutputCutoffHz = 1000,
                    ZeroCrossingSmoothingFrequencyHz = 2500,
                },
            }),
        new("Reconfiguration",
            [
                nameof(ISstvDecoderReconfiguration.RequestReconfiguration),
                nameof(ResolvedSstvDecoderSettings.DemodType),
                nameof(ResolvedSstvDecoderSettings.RxBufferMode),
                nameof(ResolvedSstvDecoderSettings.RxBpfPreset),
            ],
            // RxBufferMode.Off, so no scratch files are created.
            d => d.RequestReconfiguration(RxBpfPreset.Narrow, DemodType.Pll, RxBufferMode.Off),
            s => s with
            {
                Settings = s.Settings with
                {
                    RxBpfPreset = RxBpfPreset.Narrow,
                    DemodType = DemodType.Pll,
                    RxBufferMode = RxBufferMode.Off,
                },
            }),
        new("RxBpfPreset", [nameof(ISstvDecoderReconfiguration.RequestRxBpfPreset)],
            d => d.RequestRxBpfPreset(RxBpfPreset.VeryNarrow),
            s => s with { Settings = s.Settings with { RxBpfPreset = RxBpfPreset.VeryNarrow } }),
        // A queued rate is drained only by ApplyPendingReconfigurationNow, never by a push-driven swap.
        new("SampleRate", [nameof(ISstvDecoderReconfiguration.RequestSampleRate)],
            d => d.RequestSampleRate(QueuedRate),
            s => s,
            QueuedSampleRate: QueuedRate),
    ];

    public static TheoryData<string, int> SurvivalCases()
    {
        var data = new TheoryData<string, int>();
        foreach (var row in SurvivalRows)
        {
            data.Add(row.Name, 1);
            data.Add(row.Name, 2);
        }

        return data;
    }

    [Fact]
    public void SurfaceGuard_EverySettingsMemberIsCoveredByExactlyTheRowTable()
    {
        var target = new HashSet<string>(StringComparer.Ordinal);
        foreach (var iface in new[] { typeof(ISstvDecoder), typeof(ISstvDecoderReconfiguration) })
        {
            foreach (var property in iface.GetProperties().Where(p => p.CanWrite))
            {
                target.Add(property.Name);
            }

            foreach (var method in iface.GetMethods().Where(m => !m.IsSpecialName))
            {
                target.Add(method.Name);
            }
        }

        foreach (var property in typeof(ResolvedSstvDecoderSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            target.Add(property.Name);
        }

        target.ExceptWith(OneShotMembers);

        var covered = new HashSet<string>(SurvivalRows.SelectMany(r => r.Covers), StringComparer.Ordinal);

        Assert.Equal(target.Order(StringComparer.Ordinal), covered.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(SurvivalCases))]
    public void Setting_SurvivesInnerDecoderSwap_WithFullSnapshot(string rowName, int swapPath)
    {
        var row = SurvivalRows.Single(r => r.Name == rowName);
        using var decoder = new RestartableSstvDecoder(
            afcEnabled: true,
            warningThresholdSamples: TestWarningThreshold,
            criticalThresholdSamples: TestCriticalThreshold);

        row.Apply(decoder);
        var restartsBefore = decoder.RestartCountForTests;

        int expectedRate;
        if (swapPath == 1)
        {
            ForcePeriodicIdleSwap(decoder);
            expectedRate = SstvSampleRate.Default;
        }
        else
        {
            expectedRate = row.QueuedSampleRate ?? SwapRate;
            if (row.QueuedSampleRate is null)
            {
                decoder.RequestSampleRate(SwapRate);
            }

            ForceReconfigurationSwap(decoder);
        }

        Assert.True(decoder.RestartCountForTests > restartsBefore);
        var expected = row.Expect(new DecoderSnapshot(Baseline, false, false, null, null, expectedRate));
        Assert.Equal(expected, ReadSnapshot(decoder));
    }

    /// <summary>Every int/double differs from every other same-typed field, so a transposed pair of
    /// constructor arguments shows up in the snapshot. The bools sit at their defaults; each ctor row
    /// flips exactly one.</summary>
    private static readonly ResolvedSstvDecoderSettings CtorBase = new(
        AfcEnabled: true,
        SyncRestartEnabled: true,
        AutoSyncEnabled: true,
        AutoStopEnabled: false,
        AutoSlantEnabled: true,
        SenseLevel: 3,
        DemodType: DemodType.Pll,
        RxBpfPreset: RxBpfPreset.Narrow,
        RxBufferMode: RxBufferMode.Off,
        PllVcoGain: 2.5,
        PllLoopOrder: 2,
        PllLoopCutoffHz: 1700,
        PllOutputOrder: 4,
        PllOutputCutoffHz: 1100,
        ZeroCrossingSmoothingMode: ZeroCrossingSmoothingMode.Fir,
        ZeroCrossingOutputOrder: 5,
        ZeroCrossingOutputCutoffHz: 1000,
        ZeroCrossingSmoothingFrequencyHz: 2500,
        SnrMeasurementEnabled: false);

    private static readonly (string Name, ResolvedSstvDecoderSettings Settings, ResolvedSstvDecoderSettings Expected)[] CtorRows =
    [
        ("Base", CtorBase, CtorBase),
        ("AfcOff", CtorBase with { AfcEnabled = false }, CtorBase with { AfcEnabled = false }),
        ("SyncRestartOff", CtorBase with { SyncRestartEnabled = false }, CtorBase with { SyncRestartEnabled = false }),
        ("AutoSyncOff", CtorBase with { AutoSyncEnabled = false }, CtorBase with { AutoSyncEnabled = false }),
        ("AutoStopOn", CtorBase with { AutoStopEnabled = true }, CtorBase with { AutoStopEnabled = true }),
        ("AutoSlantOff", CtorBase with { AutoSlantEnabled = false }, CtorBase with { AutoSlantEnabled = false }),
        ("SnrOn", CtorBase with { SnrMeasurementEnabled = true }, CtorBase with { SnrMeasurementEnabled = true }),
        ("SenseLevelOutOfRange", CtorBase with { SenseLevel = 9 }, CtorBase with { SenseLevel = 0 }),
    ];

    public static TheoryData<string, string> CtorCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var row in CtorRows)
        {
            data.Add(row.Name, "Public");
            data.Add(row.Name, "Core");
        }

        return data;
    }

    /// <summary>The public settings ctor has production thresholds, so it is swapped by path 2; the core
    /// internal ctor gets the 100/1000 test thresholds and is swapped by path 1.</summary>
    [Theory]
    [MemberData(nameof(CtorCases))]
    public void SettingsCtor_SeedsEveryField_AndSurvivesSwap(string rowName, string ctorKind)
    {
        var row = CtorRows.Single(r => r.Name == rowName);
        using var decoder = ctorKind == "Public"
            ? new RestartableSstvDecoder(row.Settings)
            : new RestartableSstvDecoder(row.Settings, TestWarningThreshold, TestCriticalThreshold);

        Assert.Equal(row.Expected.SenseLevel, decoder.SenseLevel);
        Assert.Equal(row.Expected.SnrMeasurementEnabled, decoder.SnrMeasurementEnabled);

        int expectedRate;
        if (ctorKind == "Public")
        {
            decoder.RequestSampleRate(SwapRate);
            ForceReconfigurationSwap(decoder);
            expectedRate = SwapRate;
        }
        else
        {
            ForcePeriodicIdleSwap(decoder);
            expectedRate = SstvSampleRate.Default;
        }

        Assert.True(decoder.RestartCountForTests > 0);
        Assert.Equal(new DecoderSnapshot(row.Expected, false, false, null, null, expectedRate), ReadSnapshot(decoder));
    }

    /// <summary>Path 1: with the 100/1000 test thresholds, the third 50-sample push of silence crosses
    /// the warning threshold while idle and swaps; the swap's chunk then drains the fresh inner's latches.</summary>
    private static void ForcePeriodicIdleSwap(RestartableSstvDecoder decoder)
    {
        for (var i = 0; i < 3; i++)
        {
            decoder.PushSamples(new float[50]);
        }
    }

    /// <summary>Path 2: drains the queued rate, then one push so latched requests reach applied state.</summary>
    private static void ForceReconfigurationSwap(RestartableSstvDecoder decoder)
    {
        Assert.Equal(SwapResult.Committed, decoder.ApplyPendingReconfigurationNow());
        decoder.PushSamples(new float[64]);
    }

    private static DecoderSnapshot ReadSnapshot(RestartableSstvDecoder decoder)
    {
        var pll = decoder.InnerPllTuningForTests;
        var zc = decoder.InnerZeroCrossingTuningForTests;
        var settings = new ResolvedSstvDecoderSettings(
            AfcEnabled: decoder.InnerAfcEnabledForTests,
            SyncRestartEnabled: decoder.InnerSyncRestartEnabledForTests,
            AutoSyncEnabled: decoder.InnerAutoSyncEnabledForTests,
            AutoStopEnabled: decoder.InnerAutoStopEnabledForTests,
            AutoSlantEnabled: decoder.InnerAutoSlantEnabledForTests,
            SenseLevel: decoder.InnerSenseLevelForTests,
            DemodType: decoder.InnerDemodTypeForTests,
            RxBpfPreset: decoder.InnerRxBpfPresetForTests,
            RxBufferMode: decoder.InnerRxBufferModeForTests,
            PllVcoGain: pll.VcoGain,
            PllLoopOrder: pll.LoopOrder,
            PllLoopCutoffHz: pll.LoopCutoffHz,
            PllOutputOrder: pll.OutputOrder,
            PllOutputCutoffHz: pll.OutputCutoffHz,
            ZeroCrossingSmoothingMode: zc.SmoothingMode,
            ZeroCrossingOutputOrder: zc.OutputOrder,
            ZeroCrossingOutputCutoffHz: zc.OutputCutoffHz,
            ZeroCrossingSmoothingFrequencyHz: zc.SmoothingFrequencyHz,
            SnrMeasurementEnabled: decoder.InnerSnrMeasurementEnabledForTests);
        return new DecoderSnapshot(
            settings,
            decoder.InnerStationIdDecodeEnabledForTests,
            decoder.InnerNotchEnabledForTests,
            decoder.InnerNotchFrequencyForTests,
            decoder.InnerLockedModeForTests?.Id,
            decoder.SampleRate);
    }
}
