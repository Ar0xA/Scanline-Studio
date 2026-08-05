using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Milestone-audit Phase 3 SHOULD finding (spec/14-roadmap.md): <see cref="AnalogFmSstvEncoder"/> used
/// to never validate an image's dimensions against the mode being encoded before starting -- a
/// too-small image threw <see cref="IndexOutOfRangeException"/> from deep inside a scanline encoder,
/// after the header and part of a line had already been yielded (no clean error state); a too-large one
/// silently cropped with no signal at all. This file proves the new guard rejects both cases up front,
/// before any samples are produced, and leaves correctly-sized images unaffected.
/// </summary>
public class AnalogFmSstvEncoderInputValidationTests
{
    [Fact]
    public void EncodeAsync_ImageSmallerThanMode_ThrowsImmediately_BeforeAnyEnumeration()
    {
        var mode = SstvModeRegistry.Robot72; // 320x240
        var tooSmall = new ArrayImageSource(160, 120, new Rgb24[160 * 120]);
        var encoder = new AnalogFmSstvEncoder(11025);

        // Deliberately calling EncodeAsync itself, not enumerating the result -- proves the guard
        // fires synchronously at the call site (the whole point of the EncodeAsync/EncodeAsyncCore
        // split), not merely on first MoveNextAsync.
        var ex = Assert.Throws<ArgumentException>(() => encoder.EncodeAsync(mode, tooSmall));
        Assert.Contains("160x120", ex.Message);
        Assert.Contains("320x240", ex.Message);
    }

    [Fact]
    public void EncodeAsync_ImageMatchesWidthButNotHeight_ThrowsImmediately()
    {
        // Both existing throw tests vary width AND height together -- this isolates the
        // `image.Height != mode.ImageHeight` half of the guard's `||` on its own (code-review finding).
        var mode = SstvModeRegistry.Robot72; // 320x240
        var wrongHeight = new ArrayImageSource(320, 120, new Rgb24[320 * 120]);
        var encoder = new AnalogFmSstvEncoder(11025);

        var ex = Assert.Throws<ArgumentException>(() => encoder.EncodeAsync(mode, wrongHeight));
        Assert.Contains("320x120", ex.Message);
        Assert.Contains("320x240", ex.Message);
    }

    [Fact]
    public void EncodeAsync_ImageLargerThanMode_ThrowsImmediately_BeforeAnyEnumeration()
    {
        var mode = SstvModeRegistry.Robot72; // 320x240
        var tooLarge = new ArrayImageSource(640, 480, new Rgb24[640 * 480]);
        var encoder = new AnalogFmSstvEncoder(11025);

        var ex = Assert.Throws<ArgumentException>(() => encoder.EncodeAsync(mode, tooLarge));
        Assert.Contains("640x480", ex.Message);
        Assert.Contains("320x240", ex.Message);
    }

    [Fact]
    public async Task EncodeAsync_CorrectlySizedImage_DoesNotThrow_AndProducesSamples()
    {
        var mode = SstvModeRegistry.Robot72;
        var correctlySized = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, new Rgb24[mode.ImageWidth * mode.ImageHeight]);
        var encoder = new AnalogFmSstvEncoder(11025);

        var sampleCount = 0;
        await foreach (var _ in encoder.EncodeAsync(mode, correctlySized))
        {
            sampleCount++;
            if (sampleCount > 100)
            {
                break; // don't need the whole (multi-second) transmission, just proof it started
            }
        }

        Assert.True(sampleCount > 0);
    }
}
