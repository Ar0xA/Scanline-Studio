namespace ScanlineStudio.UI.Controls;

/// <summary>Pure data-transform math for <see cref="WaterfallControl"/>'s Start/Span windowing
/// (spec/18-path-to-1.0.md Medium item 2, second half) -- kept out of the control itself so it's
/// testable without a real Avalonia render pass, same reasoning as <see cref="SpectrumTraceMath"/>
/// (deliberately returns plain <see cref="double"/>s, not <c>Avalonia.Rect</c>, for the same
/// reason that class does).</summary>
public static class WaterfallRenderMath
{
    /// <summary>Computes the SOURCE rect (bin-column range, in <see cref="WaterfallControl"/>'s own
    /// bitmap pixel space) and DEST rect (screen pixels within a <paramref name="destWidth"/>-wide
    /// control) needed to render only the <c>[startHz, startHz + spanHz)</c> window, matching
    /// <see cref="SpectrumTraceControl"/>'s own already-correct windowing
    /// (<c>freqHz = i * binWidthHz</c>) so the two plots agree after any Start/Span adjustment.
    ///
    /// Deliberately scales BOTH rects together, not just the source: clamping the source range to
    /// <c>[0, bins]</c> alone (an out-of-range window, e.g. Start/Span beyond the frame's own
    /// Nyquist limit -- reachable, the Start/Span steppers have no Minimum/Maximum) while stretching
    /// it across the FULL destination width would silently zoom past the intended range, exactly
    /// re-creating the "plots visibly disagree" symptom this fix exists to close (the spectrum trace
    /// leaves blank space for the out-of-range portion instead). Scaling <paramref name="destWidth"/>
    /// down by the same clamp keeps both plots consistent: the waterfall leaves an unfilled screen
    /// strip for the same out-of-range portion, not a false zoom.
    ///
    /// Returns <see langword="false"/> (all out-params zeroed) for every degenerate case a caller
    /// must not attempt to draw against: no frame yet (<paramref name="binWidthHz"/>/
    /// <paramref name="bins"/> &lt;= 0), zero/negative effective span (after Hz-to-bin conversion,
    /// covers NaN too -- comparing against NaN is always false), or a window that clamps to nothing
    /// because it falls entirely outside <c>[0, bins]</c>.</summary>
    public static bool TryComputeWindow(
        double startHz,
        double spanHz,
        double binWidthHz,
        int bins,
        double controlWidth,
        out double sourceX,
        out double sourceWidth,
        out double destX,
        out double destWidth)
    {
        sourceX = 0;
        sourceWidth = 0;
        destX = 0;
        destWidth = 0;

        if (binWidthHz <= 0 || bins <= 0 || controlWidth <= 0)
        {
            return false;
        }

        var rawStart = startHz / binWidthHz;
        var rawEnd = (startHz + spanHz) / binWidthHz;
        if (!(rawEnd > rawStart))
        {
            return false;
        }

        var clampedStart = Math.Clamp(rawStart, 0d, (double)bins);
        var clampedEnd = Math.Clamp(rawEnd, 0d, (double)bins);
        if (clampedEnd <= clampedStart)
        {
            return false;
        }

        var scale = controlWidth / (rawEnd - rawStart);
        sourceX = clampedStart;
        sourceWidth = clampedEnd - clampedStart;
        destX = (clampedStart - rawStart) * scale;
        destWidth = sourceWidth * scale;
        return true;
    }
}
