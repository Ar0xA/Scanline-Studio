using System.Diagnostics;
using System.Globalization;
using System.Text;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Imaging.Tests;

/// <summary>Opt-in timing probe for <see cref="TransmitImagePreparer.ComposePreview"/> with image elements: an
/// 800x600 source composed onto a 320x256 canvas with 1 and 3 image elements per fit mode. Reports the min-max of
/// 5 repeat medians in us/op and B/op (all threads) through the failure message, so it always fails when run.</summary>
public sealed class ComposePreviewMeasurementProbe
{
    public const string OptInVariable = "SCANLINE_RUN_RENDER_PROBE";
    public const string OutputVariable = "SCANLINE_PROBE_OUT";
    private const int WarmUp = 200;
    private const int Repeats = 5;
    private const int OpsPerRepeat = 200;
    private const int CanvasWidth = 320;
    private const int CanvasHeight = 256;

    private static readonly NormalizedRect[] ElementBounds =
    [
        new(0.05, 0.05, 0.4, 0.45),
        new(0.5, 0.1, 0.45, 0.4),
        new(0.25, 0.55, 0.5, 0.4),
    ];

    [RenderProbeFact]
    public void Measure()
    {
        var preparer = ElementBlitTestSupport.CreatePreparer();
        var source = ElementBlitTestSupport.Patterned(800, 600, seed: 1);
        var elementSources = Enumerable.Range(0, ElementBounds.Length).Select(i => ElementBlitTestSupport.Patterned(800, 600, seed: 11 + i)).ToArray();
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"ComposePreview 800x600 -> {CanvasWidth}x{CanvasHeight}; warm-up {WarmUp}, {Repeats} repeats x {OpsPerRepeat} ops; min-max of repeat medians");
        foreach (var count in new[] { 1, 3 })
        {
            foreach (var fit in Enum.GetValues<ImageFitMode>())
            {
                var elements = Enumerable.Range(0, count)
                    .Select(i => (TemplateElement)new TemplateImageElement(ElementBounds[i], Z: i, elementSources[i], fit))
                    .ToList();
                var document = new TemplateDocument(null, elements);
                var (minUs, maxUs, minBytes, maxBytes) = TimeOps(() => preparer.ComposePreview(
                    source, new NormalizedRect(0, 0, 1, 1), CanvasWidth, CanvasHeight, preserveAspect: false, new ImageAdjustments(), document));
                report.AppendLine(CultureInfo.InvariantCulture, $"compose {count} img {fit,-7} | {minUs,8:F1} - {maxUs,8:F1} us/op | {minBytes,9:F0} - {maxBytes,9:F0} B/op");
            }
        }

        var outDir = Environment.GetEnvironmentVariable(OutputVariable);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "compose-preview-probe.txt"), report.ToString());
        }

        Assert.Fail(report.ToString());
    }

    private static (double MinUs, double MaxUs, double MinBytes, double MaxBytes) TimeOps(Func<IImageSource> op)
    {
        for (var i = 0; i < WarmUp; i++)
        {
            op();
        }

        var medians = new List<double>();
        var bytesPerOp = new List<double>();
        var samples = new double[OpsPerRepeat];
        for (var r = 0; r < Repeats; r++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            for (var i = 0; i < OpsPerRepeat; i++)
            {
                var start = Stopwatch.GetTimestamp();
                op();
                samples[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            }

            bytesPerOp.Add((GC.GetTotalAllocatedBytes(precise: true) - before) / (double)OpsPerRepeat);
            Array.Sort(samples);
            medians.Add(samples[OpsPerRepeat / 2]);
        }

        return (medians.Min(), medians.Max(), bytesPerOp.Min(), bytesPerOp.Max());
    }
}

/// <summary>A <see cref="FactAttribute"/> that skips unless <c>SCANLINE_RUN_RENDER_PROBE=1</c>, so the default run is
/// unaffected.</summary>
public sealed class RenderProbeFactAttribute : FactAttribute
{
    public RenderProbeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(ComposePreviewMeasurementProbe.OptInVariable) != "1")
        {
            Skip = $"Set {ComposePreviewMeasurementProbe.OptInVariable}=1 to run the render-path timing probe. It reports through the failure message by design.";
        }
    }
}
