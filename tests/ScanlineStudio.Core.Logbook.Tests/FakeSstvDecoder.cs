using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Logbook.Tests;

internal sealed class FakeSstvDecoder : ISstvDecoder
{
    public int SampleRate => SstvSampleRate.Default;

    public event Action<DecodedImageUpdate>? LineDecoded;

    public event Action<SstvModeDefinition>? ModeDetected;

    public event Action<FskStationIdDecodedInfo>? StationIdDecoded;

    public event Action<SstvModeDefinition>? DecodeRestarted;

    public long ReceptionSequence { get; private set; }

    public void ResetAgc()
    {
    }

    public void RequestReSync()
    {
    }

    public void RequestNotch(bool enabled, double? frequencyHz)
    {
    }

    public void RequestPllTuning(double vcoGain, int loopOrder, double loopCutoffHz, int outputOrder, double outputCutoffHz)
    {
    }

    public void RequestZeroCrossingTuning(ZeroCrossingSmoothingMode smoothingMode, int outputOrder, double outputCutoffHz, double smoothingFrequencyHz)
    {
    }

    public void ArmScopeCapture(int size)
    {
    }

    public double[]? TryGetScopeCaptureChannel0() => null;

    public double[]? TryGetScopeCaptureChannel1() => null;

    public void ForceMode(SstvModeDefinition mode)
    {
    }

    public void SetModeLock(SstvModeDefinition? mode)
    {
    }

    public void RequestAbandonReception()
    {
    }

    public void RequestCorrectSlant()
    {
    }

    public double? SlantPpm => null;

    public SstvSyncSource SyncSource => SstvSyncSource.Idle;

    public int? SyncOffsetSamples => null;

    public double SignalPeakLevel => 0.0;

    public bool IsLevelOverdriven => false;

    public bool RxBufferDegraded => false;

    public double? SyncFrequencyCorrectionHz => null;

    public int BufferedSampleCount => 0;

    public bool AutoSlantEnabled { get; set; } = true;

    public bool AutoSyncEnabled { get; set; } = true;

    public bool AutoStopEnabled { get; set; }

    public bool SyncRestartEnabled { get; set; } = true;

    public int SenseLevel { get; set; } = 1;

    public RxBpfPreset RxBpfPreset => RxBpfPreset.Wide;

    public bool StationIdDecodeEnabled { get; set; }

    public void PushSamples(ReadOnlyMemory<float> samples)
    {
    }

    public void RaiseLineDecoded(DecodedImageUpdate update) => LineDecoded?.Invoke(update);

    public void RaiseModeDetected(SstvModeDefinition mode)
    {
        // ISstvDecoder.ReceptionSequence: bumped before the raise, matching the real
        // implementations' contract (see that property's own doc comment) -- first value is 1, 0
        // means "unset."
        ReceptionSequence++;
        ModeDetected?.Invoke(mode);
    }

    public void RaiseStationIdDecoded(FskStationIdDecodedInfo info) => StationIdDecoded?.Invoke(info);

    public void RaiseDecodeRestarted(SstvModeDefinition abandonedMode) => DecodeRestarted?.Invoke(abandonedMode);
}
