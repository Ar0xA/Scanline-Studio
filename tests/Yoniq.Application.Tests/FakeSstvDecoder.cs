using Yoniq.Abstractions.Sstv;

namespace Yoniq.Application.Tests;

internal sealed class FakeSstvDecoder : ISstvDecoder
{
    public List<ReadOnlyMemory<float>> PushedSamples { get; } = [];

    public Exception? ThrowOnPush { get; set; }

    public event Action<DecodedImageUpdate>? LineDecoded;

    public event Action<SstvModeDefinition>? ModeDetected;

    public event Action<SstvModeDefinition>? DecodeRestarted;

    public void PushSamples(ReadOnlyMemory<float> samples)
    {
        PushedSamples.Add(samples);
        if (ThrowOnPush is not null)
        {
            throw ThrowOnPush;
        }
    }

    public void RaiseModeDetected(SstvModeDefinition mode) => ModeDetected?.Invoke(mode);

    public void RaiseLineDecoded(DecodedImageUpdate update) => LineDecoded?.Invoke(update);

    public void RaiseDecodeRestarted(SstvModeDefinition abandonedMode) => DecodeRestarted?.Invoke(abandonedMode);
}
