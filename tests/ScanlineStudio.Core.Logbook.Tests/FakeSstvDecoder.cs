using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Logbook.Tests;

internal sealed class FakeSstvDecoder : ISstvDecoder
{
    public event Action<DecodedImageUpdate>? LineDecoded;

    public event Action<SstvModeDefinition>? ModeDetected;

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

    public double? SlantPpm => null;

    public int? SyncOffsetSamples => null;

    public double SignalPeakLevel => 0.0;

    public bool IsLevelOverdriven => false;

    public double? SyncFrequencyCorrectionHz => null;

    public int BufferedSampleCount => 0;

    public bool AutoSlantEnabled => true;

    public void PushSamples(ReadOnlyMemory<float> samples)
    {
    }

    public void RaiseLineDecoded(DecodedImageUpdate update) => LineDecoded?.Invoke(update);

    public void RaiseModeDetected(SstvModeDefinition mode) => ModeDetected?.Invoke(mode);

    public void RaiseDecodeRestarted(SstvModeDefinition abandonedMode) => DecodeRestarted?.Invoke(abandonedMode);
}
