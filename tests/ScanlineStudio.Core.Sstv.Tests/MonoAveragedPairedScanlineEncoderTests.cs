using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

public class MonoAveragedPairedScanlineEncoderTests
{
    [Fact]
    public void GenerateLine_AveragesBothSourceRows_WithLegacysIntegerDivision()
    {
        // TMmsstv::LineRM (Main.cpp:6785-6800) reads row N into Y[x], does its own m_wLine++, reads
        // row N+1 into YY, then `YY = (YY + Y[x]) / 2` -- INTEGER division of two already-truncated,
        // already-clamped GetRY luminances. BOTH rows contribute, unlike YCbCrLinePaired (where one
        // row's chroma is reused for both) -- verified against LineRM directly, not inferred from the
        // other paired family's shape. Alternating black/white rows make the average provably equal
        // to NEITHER row's own value, so a "reads only one row" regression fails loudly here even
        // though the gradient-based round-trip/golden-vector fixtures can't see it (~0.5 level).
        var mode = SstvModeRegistry.Rm8;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        for (var y = 0; y < mode.ImageHeight; y++)
        {
            var v = (byte)(y % 2 == 0 ? 0 : 255);
            for (var x = 0; x < mode.ImageWidth; x++)
            {
                pixels[(y * mode.ImageWidth) + x] = new Rgb24(v, v, v);
            }
        }

        var encoder = new MonoAveragedPairedScanlineEncoder();
        var segments = encoder
            .GenerateLine(mode, new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels), lineIndex: 0)
            .ToList();

        var (yBlack, _, _) = YCbCr.FromRgb(0, 0, 0);
        var (yWhite, _, _) = YCbCr.FromRgb(255, 255, 255);
        var expectedHz = YCbCr.ColorToFreq(Math.Floor((yBlack + yWhite) / 2.0), mode.LuminanceMinHz, mode.LuminanceMaxHz);

        // 6ms sync + 2ms porch, then one write per pixel (Main.cpp:6789-6790's ts / ts/3.0).
        Assert.Equal(2 + mode.ImageWidth, segments.Count);
        Assert.All(segments.Skip(2), s => Assert.Equal(expectedHz, s.FrequencyHz));

        // ...and it really is an average, not either row reused.
        Assert.NotEqual(YCbCr.ColorToFreq(yBlack, mode.LuminanceMinHz, mode.LuminanceMaxHz), expectedHz);
        Assert.NotEqual(YCbCr.ColorToFreq(yWhite, mode.LuminanceMinHz, mode.LuminanceMaxHz), expectedHz);
    }

    [Fact]
    public void RowsPerTransmissionLine_IsTwo_MatchingLegacysDoubleIncrement()
    {
        // LineRM's own internal `mp->m_wLine++` (Main.cpp:6795) PLUS the outer TX dispatch loop's
        // unconditional `mp->m_wLine++` (Main.cpp:7190) -- two image rows per transmission line.
        Assert.Equal(2, new MonoAveragedPairedScanlineEncoder().RowsPerTransmissionLine);
    }
}
