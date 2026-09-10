using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Renders the edge-contamination defect (`BACKLOG.md` D2-FIX) as images a person can look at,
/// because the numbers alone cannot answer the question the user actually has to decide: does the
/// repair look BETTER, or does replicating a pixel across three or four columns read as a smear that
/// is worse than the stripe it replaced?
///
/// <para>The defect is 1 to 4 pixels wide on a picture 320 to 800 wide, so a full-size render hides
/// it. Every output here is therefore written twice: the whole picture, and a magnified crop of the
/// affected edge.</para>
///
/// <para>Two sources, because they answer different questions. FLAT WHITE makes the defect
/// unmistakable and is the worst case. The PHOTOGRAPHIC source is the one that decides whether a
/// repair looks natural next to real detail.</para>
///
/// <para>Set <c>SCANLINE_EDGE_COMPARE_OUT</c> to a directory to write into. The probe is gated on
/// that, so it never writes files during an ordinary test run.</para>
/// </summary>
public sealed class EdgeRepairVisualComparison
{
    private const int SampleRate = 44100;
    private const int Magnification = 8;
    private const int CropColumns = 12;

    // The four worst cases, each for a different reason.
    private static readonly (string ModeId, string Why)[] ShowcaseModes =
    [
        ("martin-m2", "worst overall: 4 columns, all three channels, error 253.8"),
        ("mc110", "loses the B channel outright at the edge, error 255.0"),
        ("mn140", "the original D2 black stripe, one column"),
        ("robot-36", "corrupted at BOTH edges, and its chroma selector alternates per line"),
    ];

    [RequiresEdgeCompareOutputFact]
    public async Task RenderBeforeAndAfterCrops()
    {
        var outputDirectory = Environment.GetEnvironmentVariable("SCANLINE_EDGE_COMPARE_OUT")!;
        Directory.CreateDirectory(outputDirectory);

        var stem = Environment.GetEnvironmentVariable("SCANLINE_SOURCE_BMP");
        var written = new List<string>();

        foreach (var (modeId, why) in ShowcaseModes)
        {
            var mode = SstvModeRegistry.All.SingleOrDefault(m => m.Id == modeId);
            if (mode is null)
            {
                continue;
            }

            var sources = new List<(string Name, IImageSource Image)>
            {
                ("white", CreateFlatImage(mode.ImageWidth, mode.ImageHeight, 255)),
            };

            var photoPath = stem is null ? null : $"{stem}-{mode.ImageWidth}x{mode.ImageHeight}.bmp";
            if (photoPath is not null && File.Exists(photoPath))
            {
                sources.Add(("photo", BmpFile.Read(photoPath)));
            }

            foreach (var (sourceName, source) in sources)
            {
                var decoded = await DecodeThroughTheRealChainAsync(mode, source);
                if (decoded is null)
                {
                    continue;
                }

                var prefix = Path.Combine(outputDirectory, $"{modeId}-{sourceName}");
                BmpFile.Write($"{prefix}-full.bmp", decoded);
                BmpFile.Write($"{prefix}-right-edge.bmp", MagnifiedCrop(decoded, mode, fromRight: true));
                BmpFile.Write($"{prefix}-left-edge.bmp", MagnifiedCrop(decoded, mode, fromRight: false));
                written.Add($"{modeId} / {sourceName} -- {why}");
            }
        }

        Assert.Fail(
            $"Wrote {written.Count * 3} images to {outputDirectory}:\n"
            + string.Join("\n", written)
            + $"\n\nEach is written three ways: -full, -right-edge and -left-edge. The edge crops are "
            + $"the outermost {CropColumns} columns at {Magnification}x, nearest-neighbour, because "
            + "the defect is 1 to 4 pixels wide and invisible at full size.");
    }

    /// <summary>
    /// Nearest-neighbour so the pixels stay square and countable. A smooth resample would blur the
    /// very boundary the viewer is being asked to judge.
    /// </summary>
    private static ArrayImageSource MagnifiedCrop(IImageSource decoded, SstvModeDefinition mode, bool fromRight)
    {
        var columns = Math.Min(CropColumns, mode.ImageWidth);
        var startX = fromRight ? mode.ImageWidth - columns : 0;
        var rows = Math.Min(mode.ImageHeight, decoded.Height);

        var width = columns * Magnification;
        var height = rows * Magnification;
        var pixels = new Rgb24[width * height];

        for (var y = 0; y < height; y++)
        {
            var sourceLine = decoded.GetScanline(y / Magnification);
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = sourceLine[startX + (x / Magnification)];
            }
        }

        return new ArrayImageSource(width, height, pixels);
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

    private static ArrayImageSource CreateFlatImage(int width, int height, byte level)
    {
        var pixels = new Rgb24[width * height];
        Array.Fill(pixels, new Rgb24(level, level, level));
        return new ArrayImageSource(width, height, pixels);
    }
}

public sealed class RequiresEdgeCompareOutputFactAttribute : FactAttribute
{
    public RequiresEdgeCompareOutputFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SCANLINE_EDGE_COMPARE_OUT")))
        {
            Skip = "Set SCANLINE_EDGE_COMPARE_OUT to a directory to render the edge comparison images.";
        }
    }
}
