using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Measures horizontal registration directly, which
/// <see cref="EdgeContaminationSizingProbe"/> could only measure by proxy.
///
/// <para><b>Why this exists.</b> Plan-review round 2 and the `yoniq-principal` round after it both
/// rejected sizing an edge repair from the contamination table, for the same reason: that table
/// cannot separate three different causes that look alike in it — a constant read-index bias, filter
/// smear, and per-line drift. Two results in it are unexplainable as smear. The MC family's
/// contaminated depth cannot be fitted by any single sample count while the filter's reach is a fixed
/// sample count. And the LEADING edge has zero guard by construction, so smear should dirty it in
/// every mode, yet 34 of 43 read perfectly clean.</para>
///
/// <para><b>What a vertical edge settles.</b> A half-black, half-white source puts one sharp
/// transition at a known column. Where that transition lands after decoding is the read index's
/// alignment, measured directly and in samples, with no threshold and no averaging over rows to hide
/// it. The three candidate causes give three different answers:</para>
///
/// <list type="bullet">
/// <item>Offset constant across rows AND similar in SAMPLES across modes — a constant anchor
/// residual. One decode-side constant would correct it.</item>
/// <item>Offset grows with row index — drift. Neither an anchor constant nor an edge repair helps,
/// and the encoder's per-line rounding is the first place to look.</item>
/// <item>Offset similar in MILLISECONDS but not samples — a residual in legacy's own empirical
/// per-mode sync offset, which the port reproduces exactly.</item>
/// </list>
///
/// <para><b>The port's anchor is legacy-identical</b> (`argmax - OFP + htap/4`, verified against
/// `Main.cpp:3777-3795`). So a non-zero offset here is NOT a port defect. It would be a
/// `CLAUDE.md` §0a improvement candidate, and it would need the full evidence bar.</para>
///
/// <para>This is a measurement. It changes no shipping code.</para>
/// </summary>
public sealed class HorizontalRegistrationProbe
{
    private const int SampleRate = 44100;
    private const int EdgeRows = 8;

    [Fact]
    public async Task DecodedVerticalEdgePosition_SeparatesAnchorBiasFromSmearAndDrift()
    {
        var rows = new List<string>
        {
            "| mode | pitch | first 8 rows (px) | last 8 rows (px) | drift | offset (samples) | offset (ms) |",
            "|---|---|---|---|---|---|---|",
        };

        foreach (var mode in SstvModeRegistry.All.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var edgeColumn = mode.ImageWidth / 2;
            var source = CreateVerticalEdgeImage(mode.ImageWidth, mode.ImageHeight, edgeColumn);
            var decoded = await DecodeThroughTheRealChainAsync(mode, source);
            if (decoded is null)
            {
                rows.Add($"| {mode.Id} | | DECODE FAILED | | | | |");
                continue;
            }

            var usableRows = Math.Min(mode.ImageHeight, decoded.Height);
            var offsets = new List<double>();
            for (var y = 0; y < usableRows; y++)
            {
                var found = FindEdgeColumn(decoded.GetScanline(y), mode.ImageWidth);
                if (found is not null)
                {
                    offsets.Add(found.Value - edgeColumn);
                }
            }

            if (offsets.Count < EdgeRows * 2)
            {
                rows.Add($"| {mode.Id} | | only {offsets.Count} rows had a detectable edge | | | | |");
                continue;
            }

            var first = offsets.Take(EdgeRows).Average();
            var last = offsets.TakeLast(EdgeRows).Average();
            var overall = offsets.Average();

            var lastScan = mode.LineSegments.OfType<ScanSegment>().Last();
            var pitch = lastScan.DurationMs / mode.ImageWidth / 1000.0 * SampleRate;

            rows.Add(
                $"| {mode.Id} | {pitch:F1} | {first:F2} | {last:F2} | {last - first:F2} | "
                + $"{overall * pitch:F1} | {overall * pitch / SampleRate * 1000.0:F3} |");
        }

        Assert.Fail(
            "Horizontal registration, real chain at " + SampleRate + ", half-black/half-white source.\n"
            + "A positive offset means the decoded edge lands to the RIGHT of where it was sent, i.e.\n"
            + "the read indices are EARLY. Negative means the reads are LATE, which is the direction\n"
            + "that would explain a clean leading edge and a dirty trailing one.\n\n"
            + string.Join("\n", rows));
    }

    /// <summary>
    /// First column whose luma crosses the midpoint. Sub-pixel interpolation between the two
    /// straddling columns, because a whole-pixel answer cannot distinguish a 0.4-pixel bias from none.
    /// </summary>
    private static double? FindEdgeColumn(ReadOnlySpan<Rgb24> line, int width)
    {
        for (var x = 1; x < width; x++)
        {
            var previous = Luma(line[x - 1]);
            var current = Luma(line[x]);
            if (previous < 127.5 && current >= 127.5)
            {
                var span = current - previous;
                return span <= 0 ? x : x - 1 + ((127.5 - previous) / span);
            }
        }

        return null;
    }

    private static double Luma(Rgb24 pixel) => (pixel.R + pixel.G + pixel.B) / 3.0;

    private static async Task<IImageSource?> DecodeThroughTheRealChainAsync(
        SstvModeDefinition mode,
        IImageSource source)
    {
        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, source))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        SstvModeDefinition? detected = null;
        IImageSource? decodedImage = null;
        decoder.ModeDetected += m => detected = m;
        decoder.LineDecoded += update => decodedImage = update.Image;
        decoder.PushSamples(samples.ToArray());

        return detected?.Id == mode.Id ? decodedImage : null;
    }

    private static ArrayImageSource CreateVerticalEdgeImage(int width, int height, int edgeColumn)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var level = (byte)(x < edgeColumn ? 0 : 255);
                pixels[(y * width) + x] = new Rgb24(level, level, level);
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }
}
