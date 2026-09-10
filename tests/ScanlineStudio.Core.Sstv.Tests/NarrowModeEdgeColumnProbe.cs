using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// `BACKLOG.md` item D2 — the MN/MC right-edge stripe, the only confirmed VISIBLE decode defect.
/// `docs/known-decode-defects.md` §4 measures the last two pixel columns of every MN and MC decode
/// carrying several times the mid-image error, and says the first step is to decode a clean,
/// noise-free signal and see whether the artefact survives.
///
/// <para><b>Ideal transport is cleaner than any noise-free WAV</b>, so this uses
/// <see cref="IdealTransport"/> rather than encoding audio. That answers a strictly stronger question
/// in one run: if the stripe survives with no modulation, filtering or demodulation in the path at
/// all, it cannot be a noise-sensitivity or demodulator effect — it is a timing or geometry fault in
/// the scanline codecs. If it vanishes, the fault is in the DSP chain and §4's frequency-plan lead is
/// pointing at the right place for the wrong reason.</para>
///
/// <para><b>MN140 against MP140 is the controlled pair.</b> §4 established they are structurally
/// identical — both `YCbCrLinePaired`, 320x256, 9.0 ms sync, 1.0 ms porch, four 270.0 ms segments —
/// differing only in sync 1900 against 1200 Hz, porch 2044 against 1500 Hz, and `LuminanceMin/MaxHz`
/// 2044-2300 against the default 1500-2300. MP140 is clean and MN140 is not, so the defect follows
/// the narrow frequency plan and nothing else.</para>
///
/// <para><b>Reading the numbers.</b> A 256 Hz band is 3.1x more level-sensitive per Hz than an
/// 800 Hz one, so MN is expected to show a HIGHER mid-image error than MP for the same underlying
/// frequency error. That factor is the baseline to judge the edge columns against — an edge-to-mid
/// ratio near MP's own is just band sensitivity, and a much larger one is the real defect.</para>
/// </summary>
public sealed class NarrowModeEdgeColumnProbe
{
    // The controlled pair first, then the two modes §4 actually measured, then a wide-band control.
    private static readonly string[] ProbeModeIds = ["mn140", "mp140", "mn73", "mc180", "martin-m1"];

    [RequiresPhotographicSourceFact]
    public void EdgeColumnError_UnderIdealTransport_ShowsWhetherTheStripeIsTimingOrDsp()
    {
        var stem = Environment.GetEnvironmentVariable("SCANLINE_SOURCE_BMP")!;

        var lines = new List<string>
        {
            "| mode | band Hz | mid-image | col -2 | col -1 | col -1 / mid |",
            "|---|---|---|---|---|---|",
        };

        foreach (var modeId in ProbeModeIds)
        {
            var mode = SstvModeRegistry.All.SingleOrDefault(m => m.Id == modeId);
            if (mode is null)
            {
                lines.Add($"| {modeId} | (not in registry) | | | | |");
                continue;
            }

            var sourcePath = $"{stem}-{mode.ImageWidth}x{mode.ImageHeight}.bmp";
            if (!File.Exists(sourcePath))
            {
                lines.Add($"| {modeId} | (no {mode.ImageWidth}x{mode.ImageHeight} source) | | | | |");
                continue;
            }

            var source = BmpFile.Read(sourcePath);
            var decoded = IdealTransport.Decode(mode, source);
            var columns = MeasurePerColumnAbsoluteDelta(source, decoded, mode.ImageHeight);

            // Mid-image baseline: the middle half of the width, so neither edge contaminates it.
            var midStart = mode.ImageWidth / 4;
            var midEnd = mode.ImageWidth * 3 / 4;
            var mid = columns[midStart..midEnd].Average();
            var band = mode.LuminanceMaxHz - mode.LuminanceMinHz;

            lines.Add(
                $"| {modeId} | {band:F0} | {mid:F1} | {columns[^2]:F1} | {columns[^1]:F1} | {columns[^1] / mid:F1}x |");
        }

        // The probe's output IS the deliverable. There is no threshold to assert, because the shape
        // of the table is what decides whether D2 is a codec-timing fault or a DSP one.
        Assert.Fail(string.Join(Environment.NewLine, lines));
    }

    private static double[] MeasurePerColumnAbsoluteDelta(IImageSource source, IImageSource decoded, int height)
    {
        var totals = new double[source.Width];

        for (var y = 0; y < height; y++)
        {
            var sourceLine = source.GetScanline(y);
            var decodedLine = decoded.GetScanline(y);
            for (var x = 0; x < source.Width; x++)
            {
                totals[x] += Math.Abs(decodedLine[x].R - sourceLine[x].R)
                    + Math.Abs(decodedLine[x].G - sourceLine[x].G)
                    + Math.Abs(decodedLine[x].B - sourceLine[x].B);
            }
        }

        for (var x = 0; x < totals.Length; x++)
        {
            totals[x] /= height * 3;
        }

        return totals;
    }
}
