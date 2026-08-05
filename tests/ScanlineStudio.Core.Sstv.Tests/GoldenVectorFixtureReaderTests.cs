namespace ScanlineStudio.Core.Sstv.Tests;

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
                    row[x] == new ScanlineStudio.Abstractions.Imaging.Rgb24(255, 255, 255),
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

    // Task #7 (spec/14-roadmap.md): six new fixtures added in one batch (scottie-s1, robot72, pd90,
    // rm8, mn110, avt) -- one shared sample-count/rate sanity check per file rather than repeating
    // the full per-mode derivation the two original fixtures' own tests above carry (already
    // covered there); real per-mode DSP-correctness checks live in GoldenVectorTests.cs instead.
    [Theory]
    [InlineData("scottie-s1.mmv", 1258395)]
    [InlineData("robot72.mmv", 843090)]
    [InlineData("pd90.mmv", 1040815)]
    [InlineData("rm8.mmv", 139365)]
    [InlineData("mn110.mmv", 1248220)]
    [InlineData("avt.mmv", 1120290)]
    public void MmvFile_ReadsNewTask7Fixture_With11025HzAndPlausibleSampleCount(string fileName, int expectedSampleCount)
    {
        var (samples, sampleRate) = MmvFile.Read(Path.Combine(FixtureDir, fileName));

        Assert.Equal(11025, sampleRate);
        Assert.Equal(expectedSampleCount, samples.Length);
        Assert.All(samples, s => Assert.InRange(s, -1.0f, 1.0f));
        Assert.Contains(samples, s => Math.Abs(s) > 0.1f);
    }

    // Confirmed during Task #7 scoping (independent PIL inspection, cross-checked against
    // CSSTVSET::GetPictureSize, sstv.cpp:638-653): AVT/Robot72/RM8 all have hp=240 (same as
    // Robot36), so their RX save is also the full 320x256 shared canvas. Robot72/RM8's margin is
    // pure white, matching Robot36's own convention -- but AVT's is NOT: its rows 240-255 hold
    // near-black content (not blank fill), a genuine, unexplained difference from the other three
    // hp=240 modes' own captures. Documented here rather than asserted as an invariant, since it
    // contradicts the pattern the sibling BmpFile_ReadsRobot36Rx test already established.
    [Fact]
    public void BmpFile_ReadsAvtRx_With256TallCanvas_ButMarginIsNotPureWhite()
    {
        var image = BmpFile.Read(Path.Combine(FixtureDir, "avt_RX.bmp"));

        Assert.Equal(320, image.Width);
        Assert.Equal(256, image.Height);

        var sawNonWhiteMargin = false;
        for (var y = 240; y < 256 && !sawNonWhiteMargin; y++)
        {
            var row = image.GetScanline(y);
            for (var x = 0; x < image.Width; x++)
            {
                if (row[x] != new ScanlineStudio.Abstractions.Imaging.Rgb24(255, 255, 255))
                {
                    sawNonWhiteMargin = true;
                    break;
                }
            }
        }

        Assert.True(sawNonWhiteMargin, "Expected avt_RX.bmp's margin to differ from Robot36's pure-white convention (see this test's own comment) -- if this now fails, the margin fill behavior may have changed and this test/comment need re-checking, not just re-asserting.");
    }
}
