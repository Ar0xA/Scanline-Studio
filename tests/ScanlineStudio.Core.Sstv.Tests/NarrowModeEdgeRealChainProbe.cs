using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// `BACKLOG.md` item D2, second step. <see cref="NarrowModeEdgeColumnProbe"/> already showed the
/// MN/MC right-edge stripe does NOT survive ideal transport, which moved the defect out of the
/// scanline codecs and into the DSP chain. `docs/known-decode-defects.md` §4's surviving lead is that
/// MN's sync sits 144 Hz below its picture band where MP's sits 300 Hz below, so the approaching
/// NEXT line's sync transient reaches the picture band early — and the right edge is where early
/// lands.
///
/// <para><b>This probe runs the REAL chain</b> — real encoder, real
/// <see cref="AnalogFmSstvDecoder"/>, no added noise — because the lead is about modulation, filtering
/// and demodulation, which ideal transport deletes.</para>
///
/// <para><b>Flat grey is the discriminating source.</b> With no image content there is nothing for a
/// pixel-geometry or content-dependent error to act on, so any right-edge excursion left on a flat
/// picture is the line boundary itself. If MN140 shows one on flat content and MP140 does not, §4's
/// mechanism is confirmed as a line-boundary DSP effect and the next question is how wide it is. If
/// BOTH are flat at the edge, the stripe needs image content and §4's mechanism is wrong.</para>
///
/// <para><b>The per-row breakdown answers a second question in the same run.</b> Under §4's
/// mechanism the error is caused by the FOLLOWING line's sync, so the last decoded row — the one with
/// no following picture line — is the one row that should be clean. A last row as bad as the rest
/// refutes the mechanism directly.</para>
///
/// <para>This is a measurement, not a fix. It changes no shipping code.</para>
/// </summary>
public sealed class NarrowModeEdgeRealChainProbe
{
    // The controlled pair from §4, plus mn73 to tie back to the recorded 4.7x and a wide-band control.
    private static readonly string[] ProbeModeIds = ["mn140", "mp140", "mn73", "martin-m1"];

    // Sizing sweep for the D2 fix: how many TRAILING pixel columns does the next line's sync pre-echo
    // actually corrupt, per mode? Narrow and wide, several line durations and pixel pitches.
    private static readonly string[] SizingModeIds =
    [
        "mn73", "mn110", "mn140", "mc110", "mc140", "mc180",
        "mp73", "mp115", "mp140", "martin-m1", "martin-m2", "scottie-s1", "sc2-180",
        "robot-72", "pd120", "pd180", "p3",
    ];

    private const byte FlatLevel = 128;

    [RequiresPhotographicSourceFact]
    public async Task EdgeColumnError_OnTheRealChain_ShowsWhetherTheStripeNeedsImageContent()
    {
        var stem = Environment.GetEnvironmentVariable("SCANLINE_SOURCE_BMP")!;

        var columnLines = new List<string>
        {
            "| mode | source | band Hz | mid | -6 | -5 | -4 | -3 | -2 | -1 | -1/mid |",
            "|---|---|---|---|---|---|---|---|---|---|---|",
        };

        var valueLines = new List<string>
        {
            "| mode | last-col mean | exact 0 | exact 255 | even rows mean | odd rows mean |",
            "|---|---|---|---|---|---|",
        };

        var rowLines = new List<string>
        {
            "| mode | source | edge err, rows 0..n-2 | edge err, LAST row | last / rest |",
            "|---|---|---|---|---|",
        };

        foreach (var modeId in ProbeModeIds)
        {
            var mode = SstvModeRegistry.All.SingleOrDefault(m => m.Id == modeId);
            if (mode is null)
            {
                columnLines.Add($"| {modeId} | | (not in registry) | | | | |");
                continue;
            }

            var sourcePath = $"{stem}-{mode.ImageWidth}x{mode.ImageHeight}.bmp";
            var sources = new List<(string Name, IImageSource Image)>
            {
                ("flat", CreateFlatImage(mode.ImageWidth, mode.ImageHeight)),
            };

            if (File.Exists(sourcePath))
            {
                sources.Add(("photo", BmpFile.Read(sourcePath)));
            }

            foreach (var (name, source) in sources)
            {
                var decoded = await DecodeThroughTheRealChainAsync(mode, source);
                if (decoded is null)
                {
                    columnLines.Add($"| {modeId} | {name} | | DECODE FAILED | | | |");
                    continue;
                }

                var columns = MeasurePerColumnDelta(source, decoded, mode.ImageWidth, mode.ImageHeight);

                // Middle half of the width, so neither edge contaminates the baseline.
                var mid = columns[(mode.ImageWidth / 4)..(mode.ImageWidth * 3 / 4)].Average();
                var band = mode.LuminanceMaxHz - mode.LuminanceMinHz;
                var ratio = mid > 0 ? columns[^1] / mid : double.NaN;

                columnLines.Add(
                    $"| {modeId} | {name} | {band:F0} | {mid:F1} | {columns[^6]:F1} | {columns[^5]:F1} | " +
                    $"{columns[^4]:F1} | {columns[^3]:F1} | {columns[^2]:F1} | {columns[^1]:F1} | " +
                    $"**{ratio:F1}x** |");

                var (restOfRows, lastRow) = MeasureEdgeErrorByRow(source, decoded, mode.ImageWidth, mode.ImageHeight);
                var rowRatio = restOfRows > 0 ? lastRow / restOfRows : double.NaN;
                rowLines.Add($"| {modeId} | {name} | {restOfRows:F1} | {lastRow:F1} | **{rowRatio:F2}x** |");

                if (name == "flat")
                {
                    valueLines.Add(DescribeLastColumnValues(modeId, decoded, mode.ImageWidth, mode.ImageHeight));
                }
            }
        }

        var sizing = new List<string>
        {
            "| mode | narrow | px pitch (samples) | corrupted trailing columns | worst error |",
            "|---|---|---|---|---|",
        };

        foreach (var modeId in SizingModeIds)
        {
            var mode = SstvModeRegistry.All.SingleOrDefault(m => m.Id == modeId);
            if (mode is null)
            {
                sizing.Add($"| {modeId} | | (not in registry) | | |");
                continue;
            }

            var flat = CreateFlatImage(mode.ImageWidth, mode.ImageHeight);
            var decoded = await DecodeThroughTheRealChainAsync(mode, flat);
            if (decoded is null)
            {
                sizing.Add($"| {modeId} | | DECODE FAILED | | |");
                continue;
            }

            var columns = MeasurePerColumnDelta(flat, decoded, mode.ImageWidth, mode.ImageHeight);
            var baseline = columns[(mode.ImageWidth / 4)..(mode.ImageWidth * 3 / 4)].Average();

            // A column counts as corrupted once it exceeds the mid-image baseline by a clear margin.
            // Walking back from the end stops at the first clean column, so an isolated mid-image
            // outlier cannot inflate the count.
            var threshold = Math.Max(baseline * 3.0, baseline + 10.0);
            var corrupted = 0;
            var worst = 0.0;
            for (var x = mode.ImageWidth - 1; x >= 0 && columns[x] > threshold; x--)
            {
                corrupted++;
                worst = Math.Max(worst, columns[x]);
            }

            var lastScan = mode.LineSegments.OfType<ScanSegment>().Last();
            var pitch = lastScan.DurationMs / mode.ImageWidth / 1000.0 * 44100;

            sizing.Add(
                $"| {modeId} | {(mode.NarrowModeCode is not null ? "yes" : "no")} | {pitch:F1} | "
                + $"**{corrupted}** | {worst:F1} |");
        }

        Assert.Fail(
            "D2 fix sizing -- corrupted trailing columns on FLAT grey, real chain:\n"
            + string.Join("\n", sizing)
            + "\n\nD2 real-chain probe, not a failure. Per-column mean absolute per-channel delta:\n"
            + string.Join("\n", columnLines)
            + "\n\nRight-edge error (last two columns) split by row:\n"
            + string.Join("\n", rowLines)
            + "\n\nDecoded last-column VALUES on flat grey 128 -- black means clamped or unwritten,"
            + " and an even/odd split means it is line pairing, not the DSP chain:\n"
            + string.Join("\n", valueLines));
    }

    private static async Task<IImageSource?> DecodeThroughTheRealChainAsync(
        SstvModeDefinition mode,
        IImageSource source)
    {
        var encoder = new AnalogFmSstvEncoder(44100);
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

    private static double[] MeasurePerColumnDelta(
        IImageSource expected,
        IImageSource actual,
        int width,
        int height)
    {
        var columns = new double[width];
        var rows = Math.Min(height, Math.Min(expected.Height, actual.Height));

        for (var y = 0; y < rows; y++)
        {
            var expectedLine = expected.GetScanline(y);
            var actualLine = actual.GetScanline(y);
            for (var x = 0; x < width; x++)
            {
                columns[x] += PerChannelDelta(expectedLine[x], actualLine[x]);
            }
        }

        for (var x = 0; x < width; x++)
        {
            columns[x] /= rows;
        }

        return columns;
    }

    /// <summary>
    /// Mean right-edge error (last two columns) for every row except the last, against the last row
    /// on its own. Under §4's mechanism the last row has no following picture line, so it is the one
    /// row whose right edge should be clean.
    /// </summary>
    private static (double RestOfRows, double LastRow) MeasureEdgeErrorByRow(
        IImageSource expected,
        IImageSource actual,
        int width,
        int height)
    {
        var rows = Math.Min(height, Math.Min(expected.Height, actual.Height));
        var restSum = 0.0;
        var lastSum = 0.0;

        for (var y = 0; y < rows; y++)
        {
            var expectedLine = expected.GetScanline(y);
            var actualLine = actual.GetScanline(y);
            var rowSum = PerChannelDelta(expectedLine[width - 2], actualLine[width - 2])
                + PerChannelDelta(expectedLine[width - 1], actualLine[width - 1]);

            if (y == rows - 1)
            {
                lastSum = rowSum / 2.0;
            }
            else
            {
                restSum += rowSum / 2.0;
            }
        }

        return (restSum / Math.Max(1, rows - 1), lastSum);
    }

    /// <summary>
    /// The flat source decodes to a known value everywhere, so the last column's actual values name
    /// the mechanism directly. Exact black means the reading fell below the picture band and clamped,
    /// or the pixel was never written. A split between even and odd rows means line pairing, not a
    /// frequency effect, because both rows of a pair come from the same transmitted line.
    /// </summary>
    private static string DescribeLastColumnValues(string modeId, IImageSource decoded, int width, int height)
    {
        var rows = Math.Min(height, decoded.Height);
        double total = 0, evenTotal = 0, oddTotal = 0;
        int evenCount = 0, oddCount = 0, exactBlack = 0, exactWhite = 0;

        for (var y = 0; y < rows; y++)
        {
            var pixel = decoded.GetScanline(y)[width - 1];
            var level = (pixel.R + pixel.G + pixel.B) / 3.0;
            total += level;

            if (pixel is { R: 0, G: 0, B: 0 })
            {
                exactBlack++;
            }

            if (pixel is { R: 255, G: 255, B: 255 })
            {
                exactWhite++;
            }

            if (y % 2 == 0)
            {
                evenTotal += level;
                evenCount++;
            }
            else
            {
                oddTotal += level;
                oddCount++;
            }
        }

        return $"| {modeId} | {total / rows:F1} | {exactBlack} | {exactWhite} | "
            + $"{evenTotal / Math.Max(1, evenCount):F1} | {oddTotal / Math.Max(1, oddCount):F1} |";
    }

    private static double PerChannelDelta(Rgb24 expected, Rgb24 actual) =>
        (Math.Abs(expected.R - actual.R) + Math.Abs(expected.G - actual.G) + Math.Abs(expected.B - actual.B)) / 3.0;

    private static ArrayImageSource CreateFlatImage(int width, int height)
    {
        var pixels = new Rgb24[width * height];
        Array.Fill(pixels, new Rgb24(FlatLevel, FlatLevel, FlatLevel));
        return new ArrayImageSource(width, height, pixels);
    }
}
