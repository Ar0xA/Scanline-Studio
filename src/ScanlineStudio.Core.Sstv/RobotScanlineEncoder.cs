using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Robot-family encoder: full-resolution Y scan, then a tone indicating which chroma channel
/// follows (alternates R-Y/B-Y by line index — deterministic, matching legacy's own
/// <c>mp->m_wLine &amp; 1</c> check), then that chroma channel's scan. Ported directly from the
/// real TX line-generator <c>TMmsstv::LineR36</c> (`Main.cpp`) — segment durations, order, and
/// tone frequencies (1500Hz selects R-Y, 2300Hz selects B-Y, a 1900Hz porch after the tone) all
/// read from that function, not inferred from RX decode code (see CLAUDE.md's TX/RX-split rule).
/// </summary>
internal sealed class RobotScanlineEncoder : IScanlineEncoder
{
    public IEnumerable<(double FrequencyHz, double DurationMs)> GenerateLine(SstvModeDefinition mode, IImageSource image, int lineIndex)
    {
        var isEvenLine = lineIndex % 2 == 0; // even -> R-Y, odd -> B-Y, per LineR36's `m_wLine & 1`

        var (syncSegment, porchSegment, ySegment, selector, porch2Segment, chromaSegment) = GetSegments(mode);

        yield return (syncSegment.FrequencyHz, syncSegment.DurationMs);
        yield return (porchSegment.FrequencyHz, porchSegment.DurationMs);

        var yPerPixelMs = ySegment.DurationMs / mode.ImageWidth;
        for (var x = 0; x < mode.ImageWidth; x++)
        {
            var (y, _, _) = YCbCr.FromRgb(image.GetScanline(lineIndex)[x].R, image.GetScanline(lineIndex)[x].G, image.GetScanline(lineIndex)[x].B);
            yield return (YCbCr.ColorToFreq(y, mode.LuminanceMinHz, mode.LuminanceMaxHz), yPerPixelMs);
        }

        yield return (isEvenLine ? selector.LowFrequencyHz : selector.HighFrequencyHz, selector.DurationMs);
        yield return (porch2Segment.FrequencyHz, porch2Segment.DurationMs);

        var chromaPerPixelMs = chromaSegment.DurationMs / mode.ImageWidth;
        for (var x = 0; x < mode.ImageWidth; x++)
        {
            var pixel = image.GetScanline(lineIndex)[x];
            var (_, rMinusY, bMinusY) = YCbCr.FromRgb(pixel.R, pixel.G, pixel.B);
            yield return (YCbCr.ColorToFreq(isEvenLine ? rMinusY : bMinusY, mode.LuminanceMinHz, mode.LuminanceMaxHz), chromaPerPixelMs);
        }
    }

    private static (SyncSegment Sync, SyncSegment Porch, ScanSegment Y, ToneSelectorSegment Selector, SyncSegment Porch2, ScanSegment Chroma)
        GetSegments(SstvModeDefinition mode)
    {
        SyncSegment? sync = null;
        SyncSegment? porch = null;
        ScanSegment? y = null;
        ToneSelectorSegment? selector = null;
        SyncSegment? porch2 = null;
        ScanSegment? chroma = null;

        foreach (var segment in mode.LineSegments)
        {
            switch (segment)
            {
                case SyncSegment s when sync is null:
                    sync = s;
                    break;
                case SyncSegment s when porch is null:
                    porch = s;
                    break;
                case ScanSegment sc when y is null:
                    y = sc;
                    break;
                case ToneSelectorSegment t:
                    selector = t;
                    break;
                case SyncSegment s:
                    porch2 = s;
                    break;
                case ScanSegment sc:
                    chroma = sc;
                    break;
            }
        }

        if (sync is null || porch is null || y is null || selector is null || porch2 is null || chroma is null)
        {
            throw new InvalidOperationException($"Mode '{mode.Id}' is missing a required Robot-family line segment.");
        }

        return (sync, porch, y, selector, porch2, chroma);
    }
}
