using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Legacy's LinePD/LineMP/LineMN call <c>GetRY(Y, RY[x], BY[x], Pixels[x][m_wLine])</c> during the
/// Y(odd) pass and only afterwards do <c>mp->m_wLine++</c> (`Main.cpp:6694`/`:6703`, `:6741`/`:6750`,
/// `:6811`/`:6820`) -- the ONE transmitted chroma pair belongs to the ODD (first) row; the even row
/// contributes luma only. Round-trip and the smooth-gradient golden TX fixtures both fail to catch a
/// swap here: the decoder applies the same chroma pair to both output rows either way, so encoder and
/// decoder would agree with each other while both disagreed with legacy -- the Scottie precedent
/// (CLAUDE.md section 4). Hence a direct TX-side assertion.
/// </summary>
public class YCbCrLinePairedScanlineEncoderTests
{
    [Fact]
    public void ChromaComesFromTheOddRow_LumaFromBothRows()
    {
        var mode = SstvModeRegistry.Pd90;
        var oddRow = new Rgb24(200, 30, 30);
        var evenRow = new Rgb24(30, 30, 200);
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        for (var x = 0; x < mode.ImageWidth; x++)
        {
            pixels[x] = oddRow;
            pixels[mode.ImageWidth + x] = evenRow;
        }

        var image = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);
        var segments = new YCbCrLinePairedScanlineEncoder()
            .GenerateLine(mode, image, lineIndex: 0)
            .ToList();

        // Pd90's LineSegments: sync, porch, Y1(320px), RY(320px), BY(320px), Y2(320px).
        var y1 = segments[2].FrequencyHz;
        var ry = segments[2 + mode.ImageWidth].FrequencyHz;
        var by = segments[2 + (mode.ImageWidth * 2)].FrequencyHz;
        var y2 = segments[2 + (mode.ImageWidth * 3)].FrequencyHz;

        var (oddY, oddRy, oddBy) = YCbCr.FromRgb(oddRow.R, oddRow.G, oddRow.B);
        var (evenY, evenRy, evenBy) = YCbCr.FromRgb(evenRow.R, evenRow.G, evenRow.B);

        double Freq(double v) => YCbCr.ColorToFreq(v, mode.LuminanceMinHz, mode.LuminanceMaxHz);

        Assert.Equal(Freq(oddY), y1);
        Assert.Equal(Freq(oddRy), ry);   // NOT evenRy -- the whole point of this test
        Assert.Equal(Freq(oddBy), by);   // NOT evenBy
        Assert.Equal(Freq(evenY), y2);

        // Guard the guard: these two rows really are chroma-distinguishable, so a swap would fail
        // the asserts above rather than passing vacuously.
        Assert.NotEqual(Freq(oddRy), Freq(evenRy));
        Assert.NotEqual(Freq(oddBy), Freq(evenBy));
    }
}
