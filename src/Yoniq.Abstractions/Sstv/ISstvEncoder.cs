using Yoniq.Abstractions.Imaging;

namespace Yoniq.Abstractions.Sstv;

public interface ISstvEncoder
{
    int SampleRate { get; }

    /// <summary>
    /// Encodes <paramref name="image"/> as <paramref name="mode"/>'s SSTV audio. Implementations must
    /// reject an <paramref name="image"/> whose dimensions don't exactly match
    /// <c>mode.ImageWidth</c>/<c>mode.ImageHeight</c> with an <see cref="ArgumentException"/> thrown
    /// synchronously from this call (not deferred to enumeration) -- callers must not rely on a
    /// mismatched image being silently cropped or padded.
    /// </summary>
    IAsyncEnumerable<float> EncodeAsync(SstvModeDefinition mode, IImageSource image, CancellationToken ct = default);
}
