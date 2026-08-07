using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Imaging.Tests;

internal sealed class FakeSstvDecoder : ISstvDecoder
{
    public event Action<DecodedImageUpdate>? LineDecoded;

    public event Action<SstvModeDefinition>? ModeDetected;

    public event Action<SstvModeDefinition>? DecodeRestarted;

    public void ResetAgc()
    {
    }

    public void PushSamples(ReadOnlyMemory<float> samples)
    {
    }

    public void RaiseLineDecoded(DecodedImageUpdate update) => LineDecoded?.Invoke(update);

    public void RaiseModeDetected(SstvModeDefinition mode) => ModeDetected?.Invoke(mode);

    public void RaiseDecodeRestarted(SstvModeDefinition abandonedMode) => DecodeRestarted?.Invoke(abandonedMode);
}
