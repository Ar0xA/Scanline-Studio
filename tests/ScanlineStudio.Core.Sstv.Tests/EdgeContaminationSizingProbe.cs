using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Sizes the edge-contamination defect for `BACKLOG.md` D2/D4, to the shape plan-review round 1
/// demanded. <see cref="NarrowModeEdgeRealChainProbe"/> established the mechanism; this probe
/// measures the three things that one could not.
///
/// <para><b>Per channel, not a 3-channel mean.</b> The earlier probe averaged R, G and B, which
/// caps a single fully-clamped channel at 42.7. martin-m2 read 104.2 and martin-m1 read 73.4, both
/// arithmetically impossible from one channel — so Martin and Pasokon, which put a separator after
/// EVERY channel, must have all three tails pulled. Repairing only the final scan segment would leave
/// the other two dark and turn a grey bar into a coloured fringe. This probe splits the channels so
/// that is measured rather than argued.</para>
///
/// <para><b>Flat WHITE, not flat grey.</b> Grey is the weakest possible case. The pull is toward the
/// following tone, so a white edge sits further from it — about 57% further for a wide mode against a
/// 1200 Hz sync. A repair width fitted to grey would under-reach on real pictures, and an
/// under-reaching replication is worse than none: it copies a still-contaminated pixel across the
/// whole tail.</para>
///
/// <para><b>Both edges.</b> The RX bandpass is a linear-phase FIR whose window is centred by an
/// anchor shift, so its reach is symmetric on the aligned timeline. The porch-to-picture transition
/// should therefore contaminate the LEADING columns as well. Nothing has ever measured that, and a
/// trailing-only repair would leave the picture asymmetric.</para>
///
/// <para>This is a measurement. It changes no shipping code.</para>
/// </summary>
public sealed class EdgeContaminationSizingProbe
{
    private const int SampleRate = 44100;

    // How far a column must exceed the mid-image baseline before it counts as contaminated.
    private const double AbsoluteMargin = 8.0;
    private const double RelativeMargin = 3.0;

    [Fact]
    public async Task EdgeContamination_PerChannel_OnAWorstCaseSource()
    {
        var rows = new List<string>
        {
            "| mode | pitch | lead R | lead G | lead B | tail R | tail G | tail B | worst |",
            "|---|---|---|---|---|---|---|---|---|",
        };

        var affected = 0;
        var multiChannel = 0;
        var leadingAffected = 0;

        foreach (var mode in SstvModeRegistry.All.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var source = CreateFlatImage(mode.ImageWidth, mode.ImageHeight, 255);
            var decoded = await DecodeThroughTheRealChainAsync(mode, source);
            if (decoded is null)
            {
                rows.Add($"| {mode.Id} | | DECODE FAILED | | | | | | |");
                continue;
            }

            var lead = new int[3];
            var tail = new int[3];
            var worst = 0.0;

            for (var channel = 0; channel < 3; channel++)
            {
                var columns = MeasureColumnDelta(source, decoded, mode, channel);
                var baseline = columns[(mode.ImageWidth / 4)..(mode.ImageWidth * 3 / 4)].Average();
                var threshold = Math.Max(baseline * RelativeMargin, baseline + AbsoluteMargin);

                for (var x = mode.ImageWidth - 1; x >= 0 && columns[x] > threshold; x--)
                {
                    tail[channel]++;
                    worst = Math.Max(worst, columns[x]);
                }

                for (var x = 0; x < mode.ImageWidth && columns[x] > threshold; x++)
                {
                    lead[channel]++;
                    worst = Math.Max(worst, columns[x]);
                }
            }

            var lastScan = mode.LineSegments.OfType<ScanSegment>().Last();
            var pitch = lastScan.DurationMs / mode.ImageWidth / 1000.0 * SampleRate;

            if (tail.Sum() + lead.Sum() > 0)
            {
                affected++;
            }

            if (tail.Count(c => c > 0) > 1)
            {
                multiChannel++;
            }

            if (lead.Sum() > 0)
            {
                leadingAffected++;
            }

            rows.Add(
                $"| {mode.Id} | {pitch:F1} | {lead[0]} | {lead[1]} | {lead[2]} | "
                + $"**{tail[0]}** | **{tail[1]}** | **{tail[2]}** | {worst:F1} |");
        }

        Assert.Fail(
            $"Edge contamination, per channel, FLAT WHITE source, real chain at {SampleRate}.\n"
            + $"Affected modes: {affected} of {SstvModeRegistry.All.Count}. "
            + $"More than one channel corrupted at the tail: {multiChannel}. "
            + $"Leading edge also corrupted: {leadingAffected}.\n\n"
            + string.Join("\n", rows));
    }

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

    private static double[] MeasureColumnDelta(
        IImageSource expected,
        IImageSource actual,
        SstvModeDefinition mode,
        int channel)
    {
        var columns = new double[mode.ImageWidth];
        var rows = Math.Min(mode.ImageHeight, Math.Min(expected.Height, actual.Height));

        for (var y = 0; y < rows; y++)
        {
            var expectedLine = expected.GetScanline(y);
            var actualLine = actual.GetScanline(y);
            for (var x = 0; x < mode.ImageWidth; x++)
            {
                columns[x] += Math.Abs(Channel(expectedLine[x], channel) - Channel(actualLine[x], channel));
            }
        }

        for (var x = 0; x < mode.ImageWidth; x++)
        {
            columns[x] /= rows;
        }

        return columns;
    }

    private static int Channel(Rgb24 pixel, int channel) => channel switch
    {
        0 => pixel.R,
        1 => pixel.G,
        _ => pixel.B,
    };

    private static ArrayImageSource CreateFlatImage(int width, int height, byte level)
    {
        var pixels = new Rgb24[width * height];
        Array.Fill(pixels, new Rgb24(level, level, level));
        return new ArrayImageSource(width, height, pixels);
    }
}
