namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Isolated correctness checks for <see cref="BmpFile"/>/<see cref="MmvFile"/> themselves, before
/// anything else in this suite builds on them -- confirming the exact facts independently verified
/// against the fixtures during scoping (row orientation, Robot 36's canvas-vs-picture-height split,
/// `.mmv` header/sample-rate decoding) rather than assuming the readers are correct because they
/// compile.
/// </summary>
public class GoldenVectorFixtureReaderTests
{
    private const string FixtureDir = "Fixtures/GoldenVectors";

    [Fact]
    public void BmpFile_ReadsMartinM1Source_WithExpectedDimensionsAndGradient()
    {
        var image = BmpFile.Read(Path.Combine(FixtureDir, "martin-m1.bmp"));

        Assert.Equal(320, image.Width);
        Assert.Equal(256, image.Height);

        // Top-left corner of the gradient formula (R=0, G=0, B=128) -- confirms row 0 is the visual
        // top after the bottom-up BMP flip, not the raw file's first stored row.
        var topLeft = image.GetScanline(0)[0];
        Assert.Equal(0, topLeft.R);
        Assert.Equal(0, topLeft.G);
        Assert.Equal(128, topLeft.B);

        // Bottom-right corner: R and G both near 255.
        var bottomRight = image.GetScanline(255)[319];
        Assert.True(bottomRight.R > 250, $"Expected R near 255 at bottom-right, got {bottomRight.R}.");
        Assert.True(bottomRight.G > 250, $"Expected G near 255 at bottom-right, got {bottomRight.G}.");
    }

    [Fact]
    public void BmpFile_ReadsRobot36Rx_With256TallCanvas_TopPictureBottomWhiteMargin()
    {
        // Confirmed during scoping (independent PIL inspection): legacy's RX save is the full
        // 320x256 shared canvas, not the 240-line picture -- rows 0-239 are real decoded content,
        // rows 240-255 are pure white (255,255,255) fill left over from the unused canvas margin.
        var image = BmpFile.Read(Path.Combine(FixtureDir, "robot36_RX.bmp"));

        Assert.Equal(320, image.Width);
        Assert.Equal(256, image.Height);

        var lastContentRow = image.GetScanline(239);
        Assert.False(
            lastContentRow[0].R == 255 && lastContentRow[0].G == 255 && lastContentRow[0].B == 255,
            "Row 239 (last real picture row) should not be blank white.");

        for (var y = 240; y < 256; y++)
        {
            var row = image.GetScanline(y);
            for (var x = 0; x < image.Width; x++)
            {
                Assert.True(
                    row[x] == new Yoniq.Abstractions.Imaging.Rgb24(255, 255, 255),
                    $"Expected pure white margin fill at (x={x}, y={y}), got {row[x]}.");
            }
        }
    }

    [Fact]
    public void MmvFile_ReadsRobot36_With11025HzAndPlausibleSampleCount()
    {
        var (samples, sampleRate) = MmvFile.Read(Path.Combine(FixtureDir, "robot36.mmv"));

        Assert.Equal(11025, sampleRate);

        // Confirmed via direct byte inspection: 446870 samples. Trimmed from the original 78.58s
        // capture (866304 samples) to the TX region plus a 1.0s safety margin on each side, per the
        // user's explicit request to remove incidental room/mic audio -- see the fixture directory's
        // own README.md for exact trim provenance.
        Assert.Equal(446870, samples.Length);

        // Real captured audio should be well within [-1, 1] and not all-zero.
        Assert.All(samples, s => Assert.InRange(s, -1.0f, 1.0f));
        Assert.Contains(samples, s => Math.Abs(s) > 0.1f);
    }

    [Fact]
    public void MmvFile_ReadsMartinM1_With11025HzAndPlausibleSampleCount()
    {
        var (samples, sampleRate) = MmvFile.Read(Path.Combine(FixtureDir, "martin-m1.mmv"));

        Assert.Equal(11025, sampleRate);
        // Trimmed from the original 147.86s capture (1630208 samples) the same way as robot36.mmv
        // above -- see that test's comment.
        Assert.Equal(1309600, samples.Length);
        Assert.All(samples, s => Assert.InRange(s, -1.0f, 1.0f));
        Assert.Contains(samples, s => Math.Abs(s) > 0.1f);
    }
}
