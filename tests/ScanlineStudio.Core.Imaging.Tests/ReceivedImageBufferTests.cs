using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Imaging.Tests;

public sealed class ReceivedImageBufferTests
{
    private static readonly SstvModeDefinition TestMode = new(
        Id: "test",
        DisplayName: "Test",
        VisCode: 0,
        ImageWidth: 2,
        ImageHeight: 2,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments: []);

    [Fact]
    public void OnLineDecoded_CopiesWholeImage_NotJustTheReportedRow()
    {
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder);
        var pixels = new Rgb24[4];
        pixels[0] = new Rgb24(1, 1, 1);
        pixels[1] = new Rgb24(2, 2, 2);
        pixels[2] = new Rgb24(3, 3, 3); // row 1 -- already filled even though this update reports row 0,
        pixels[3] = new Rgb24(4, 4, 4); // simulating a paired-mode decoder (RowsPerTransmissionLine = 2).
        var liveImage = new MutableTestImageSource(2, 2, pixels);

        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, liveImage));

        var current = buffer.Current;
        Assert.Equal(new Rgb24(1, 1, 1), current.GetScanline(0)[0]);
        Assert.Equal(new Rgb24(2, 2, 2), current.GetScanline(0)[1]);
        Assert.Equal(new Rgb24(3, 3, 3), current.GetScanline(1)[0]);
        Assert.Equal(new Rgb24(4, 4, 4), current.GetScanline(1)[1]);
    }

    [Fact]
    public void Current_ReturnsSnapshot_UnaffectedByLaterMutationOfTheLiveBuffer()
    {
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder);
        var pixels = new Rgb24[4];
        pixels[0] = new Rgb24(1, 1, 1);
        var liveImage = new MutableTestImageSource(2, 2, pixels);
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, liveImage));

        var snapshot = buffer.Current;
        pixels[0] = new Rgb24(99, 99, 99); // decoder "keeps writing" into the same live array

        Assert.Equal(new Rgb24(1, 1, 1), snapshot.GetScanline(0)[0]);
    }

    [Fact]
    public void OnDecodeRestarted_ResetsToAnEmptyImage()
    {
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder);
        var pixels = new Rgb24[4];
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, new MutableTestImageSource(2, 2, pixels)));

        decoder.RaiseDecodeRestarted(TestMode);

        Assert.Equal(1, buffer.Current.Width);
        Assert.Equal(1, buffer.Current.Height);
    }

    [Fact]
    public void Updated_FiresOnBothLineDecodedAndDecodeRestarted()
    {
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder);
        var updateCount = 0;
        buffer.Updated += () => updateCount++;

        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, new MutableTestImageSource(1, 1, new Rgb24[1])));
        decoder.RaiseDecodeRestarted(TestMode);

        Assert.Equal(2, updateCount);
    }

    [Fact]
    public void Progress_FreshBuffer_IsNull()
    {
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder);

        Assert.Null(buffer.Progress);
    }

    [Fact]
    public void Progress_AfterModeDetected_IsZero()
    {
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder);

        decoder.RaiseModeDetected(TestMode);

        Assert.Equal(0.0, buffer.Progress);
    }

    [Fact]
    public void Progress_FirstLineDecoded_StepNotYetLearned_FallsBackToLineOverHeight()
    {
        // 10-row image, single-row-per-event family (step unknowable from just one event).
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder);
        decoder.RaiseModeDetected(TestMode);
        var image = new MutableTestImageSource(1, 10, new Rgb24[10]);

        decoder.RaiseLineDecoded(new DecodedImageUpdate(3, image));

        Assert.Equal(3.0 / 10.0, buffer.Progress);
    }

    [Fact]
    public void Progress_SecondLineDecoded_StepLearned_UsesLinePlusStep()
    {
        // Single-row-per-event family: step = 1.
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder);
        decoder.RaiseModeDetected(TestMode);
        var image = new MutableTestImageSource(1, 10, new Rgb24[10]);
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, image));

        decoder.RaiseLineDecoded(new DecodedImageUpdate(1, image));

        Assert.Equal(2.0 / 10.0, buffer.Progress);
    }

    [Fact]
    public void Progress_PairedLineFamily_StepLearnedAsTwo_ComputesCorrectly()
    {
        // PD/MP/RM8/RM12-shaped: Line advances by 2 per event.
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder);
        decoder.RaiseModeDetected(TestMode);
        var image = new MutableTestImageSource(1, 10, new Rgb24[10]);
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, image));

        decoder.RaiseLineDecoded(new DecodedImageUpdate(2, image));

        Assert.Equal(4.0 / 10.0, buffer.Progress);
    }

    [Fact]
    public void Progress_FinalCompletingEvent_SnapsToExactlyOne()
    {
        // 10-row image, step-1 family: events at 0,1,2,...,9. The Line=9 event (Line+step=10>=10)
        // must read exactly 1.0, not 10.0/10.0's own asymptotic near-miss for paired families --
        // this pins the snap-to-1.0 behavior directly, not just that it happens to equal 1.0 here.
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder);
        decoder.RaiseModeDetected(TestMode);
        var image = new MutableTestImageSource(1, 10, new Rgb24[10]);
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, image));
        decoder.RaiseLineDecoded(new DecodedImageUpdate(1, image));

        decoder.RaiseLineDecoded(new DecodedImageUpdate(9, image));

        Assert.Equal(1.0, buffer.Progress);
    }

    [Fact]
    public void Progress_DecodeRestarted_ResetsToNull()
    {
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder);
        decoder.RaiseModeDetected(TestMode);
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, new MutableTestImageSource(1, 10, new Rgb24[10])));

        decoder.RaiseDecodeRestarted(TestMode);

        Assert.Null(buffer.Progress);
    }

    [Fact]
    public async Task SaveAsync_WritesARealPngFileMatchingCurrentPixels()
    {
        var decoder = new FakeSstvDecoder();
        var buffer = new ReceivedImageBuffer(decoder);
        var pixels = new[] { new Rgb24(10, 20, 30) };
        decoder.RaiseLineDecoded(new DecodedImageUpdate(0, new MutableTestImageSource(1, 1, pixels)));
        var path = Path.Combine(Path.GetTempPath(), $"yoniq-received-image-test-{Guid.NewGuid()}.png");

        try
        {
            await buffer.SaveAsync(path);

            using var loaded = await SixLabors.ImageSharp.Image.LoadAsync<SixLabors.ImageSharp.PixelFormats.Rgb24>(path);
            var pixel = loaded[0, 0];
            Assert.Equal(10, pixel.R);
            Assert.Equal(20, pixel.G);
            Assert.Equal(30, pixel.B);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
