using Yoniq.Abstractions.Imaging;

namespace Yoniq.Abstractions.Sstv;

public sealed record DecodedImageUpdate(int Line, IImageSource Image);

public interface ISstvDecoder
{
    void PushSamples(ReadOnlyMemory<float> samples);

    event Action<DecodedImageUpdate>? LineDecoded;

    event Action<SstvModeDefinition>? ModeDetected;
}
