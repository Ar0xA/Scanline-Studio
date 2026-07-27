using Yoniq.Abstractions.Imaging;

namespace Yoniq.Abstractions.Sstv;

public interface ISstvEncoder
{
    int SampleRate { get; }

    IAsyncEnumerable<float> EncodeAsync(SstvModeDefinition mode, IImageSource image, CancellationToken ct = default);
}
