using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.UI.Imaging;

/// <summary>Fraction of an <see cref="IImageSource"/>'s pixels at pure-black/pure-white luminance
/// extremes -- backs the Receive tab's "Clip lo/hi" readout (spec/17-rx-telemetry-feasibility.md).
/// Deliberately NOT a legacy port: legacy has no equivalent statistic, this is pure image-domain
/// arithmetic over the already-decoded image (no audio/DSP touched at all), using standard
/// ITU-R BT.601 luma weights -- a generic, non-legacy-specific convention, not this codebase's own
/// SSTV-domain <c>YCbCr</c> luma (which has its own different, legacy-matched weights/offsets for a
/// different purpose: encoding into the SSTV chroma signal, not display statistics).</summary>
internal static class LuminanceClipStatistics
{
    /// <summary>Returns (fraction clipped to black, fraction clipped to white), each in
    /// <c>[0.0, 1.0]</c>, over the first <paramref name="rowCount"/> rows only -- for a live decode,
    /// callers must pass only the rows actually written so far (e.g. derived from
    /// <see cref="ScanlineStudio.Abstractions.Imaging.IReceivedImageBuffer.Progress"/>), not the
    /// whole mode-sized canvas (pass <c>source.Height</c> for a fully-decoded/static image).
    /// <c>(0.0, 0.0)</c> for <paramref name="rowCount"/> &lt;= 0 or a zero-width image, rather than
    /// dividing by zero.
    ///
    /// <b>Why this matters</b> (auditor-caught bug, fixed by requiring an explicit row count instead
    /// of always defaulting to the whole image): an in-progress decode's not-yet-written rows are
    /// zeroed <c>Rgb24</c> -- pure black -- so computing this over the WHOLE mode-sized canvas
    /// mid-decode measures how much of the canvas hasn't been drawn yet, not the actual image
    /// content.</summary>
    public static (double ClippedBlackFraction, double ClippedWhiteFraction) Compute(IImageSource source, int rowCount)
    {
        var clampedRowCount = Math.Min(rowCount, source.Height);
        var totalPixels = (long)source.Width * clampedRowCount;
        if (totalPixels <= 0)
        {
            return (0.0, 0.0);
        }

        long clippedBlack = 0;
        long clippedWhite = 0;

        for (var y = 0; y < clampedRowCount; y++)
        {
            var row = source.GetScanline(y);
            for (var x = 0; x < row.Length; x++)
            {
                var pixel = row[x];
                var luma = (int)Math.Round((0.299 * pixel.R) + (0.587 * pixel.G) + (0.114 * pixel.B));
                if (luma <= 0)
                {
                    clippedBlack++;
                }
                else if (luma >= 255)
                {
                    clippedWhite++;
                }
            }
        }

        return ((double)clippedBlack / totalPixels, (double)clippedWhite / totalPixels);
    }
}
