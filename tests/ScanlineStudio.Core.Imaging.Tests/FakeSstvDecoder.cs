using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Imaging.Tests;

internal sealed class FakeSstvDecoder : ISstvDecoder
{
    public int SampleRate => SstvSampleRate.Default;

    public event Action<DecodedImageUpdate>? LineDecoded;

    public event Action<SstvModeDefinition>? ModeDetected;

    public event Action<FskStationIdDecodedInfo>? StationIdDecoded;

    public event Action<SstvModeDefinition>? DecodeRestarted;

    public void ResetAgc()
    {
    }

    public void RequestReSync()
    {
    }

    public void ForceMode(SstvModeDefinition mode)
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

    public double? SyncFrequencyCorrectionHz => null;

    public int BufferedSampleCount => 0;

    public bool AutoSlantEnabled => true;

    public int SenseLevel => 1;

    public RxBpfPreset RxBpfPreset => RxBpfPreset.Wide;

    public bool StationIdDecodeEnabled { get; set; }

    public void PushSamples(ReadOnlyMemory<float> samples)
    {
    }

    public void RaiseLineDecoded(DecodedImageUpdate update) => LineDecoded?.Invoke(update);

    public void RaiseModeDetected(SstvModeDefinition mode) => ModeDetected?.Invoke(mode);

    public void RaiseStationIdDecoded(FskStationIdDecodedInfo info) => StationIdDecoded?.Invoke(info);

    public void RaiseDecodeRestarted(SstvModeDefinition abandonedMode) => DecodeRestarted?.Invoke(abandonedMode);
}
