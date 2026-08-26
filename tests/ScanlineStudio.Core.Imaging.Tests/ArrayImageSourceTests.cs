using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Imaging.Tests;

public sealed class ArrayImageSourceTests
{
    [Fact]
    public void CopyFrom_CopiesEveryPixel()
    {
        var pixels = new Rgb24[6];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Rgb24((byte)i, (byte)(i * 2), (byte)(i * 3));
        }

        var source = new MutableTestImageSource(3, 2, pixels);

        var copy = ArrayImageSource.CopyFrom(source);

        Assert.Equal(source.Width, copy.Width);
        Assert.Equal(source.Height, copy.Height);
        for (var y = 0; y < source.Height; y++)
        {
            Assert.True(source.GetScanline(y).SequenceEqual(copy.GetScanline(y)));
        }
    }

    [Fact]
    public void CopyFrom_IsIndependentOfLaterMutationToTheSource()
    {
        // The whole reason this method exists (see its own doc comment): ISstvDecoder.LineDecoded
        // hands out a LIVE ALIAS of the decoder's own mutable pixel buffer -- a caller that holds
        // onto the source reference instead of copying sees torn/stale data once the decoder mutates
        // it further. This proves the copy is genuinely independent, not just correct at copy time.
        var pixels = new Rgb24[] { new(1, 1, 1), new(2, 2, 2) };
        var source = new MutableTestImageSource(2, 1, pixels);

        var copy = ArrayImageSource.CopyFrom(source);
        pixels[0] = new Rgb24(99, 99, 99);

        Assert.Equal(new Rgb24(1, 1, 1), copy.GetScanline(0)[0]);
        Assert.Equal(new Rgb24(99, 99, 99), source.GetScanline(0)[0]);
    }
}
