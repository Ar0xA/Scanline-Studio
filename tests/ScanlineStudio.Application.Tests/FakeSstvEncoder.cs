using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Application.Tests;

internal sealed class FakeSstvEncoder : ISstvEncoder
{
    public int SampleRate { get; init; } = 11025;

    public float[] SamplesToYield { get; init; } = [0.1f, 0.2f, 0.3f];

    public async IAsyncEnumerable<float> EncodeAsync(SstvModeDefinition mode, IImageSource image, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var sample in SamplesToYield)
        {
            ct.ThrowIfCancellationRequested();
            yield return sample;
            await Task.Yield();
        }
    }
}
