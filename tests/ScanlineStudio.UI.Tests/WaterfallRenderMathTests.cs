using ScanlineStudio.UI.Controls;

namespace ScanlineStudio.UI.Tests;

public sealed class WaterfallRenderMathTests
{
    [Fact]
    public void TryComputeWindow_FullFrameRange_SourceRectCoversAllBinsAndDestFillsTheWholeWidth()
    {
        // 400 bins at 10Hz/bin = 0..4000Hz; a window matching that exactly should behave like the
        // pre-windowing code (full bitmap, full width) -- the no-regression baseline case.
        var ok = WaterfallRenderMath.TryComputeWindow(
            startHz: 0, spanHz: 4000, binWidthHz: 10, bins: 400, controlWidth: 400,
            out var sourceX, out var sourceWidth, out var destX, out var destWidth);

        Assert.True(ok);
        Assert.Equal(0, sourceX);
        Assert.Equal(400, sourceWidth);
        Assert.Equal(0, destX);
        Assert.Equal(400, destWidth);
    }

    [Fact]
    public void TryComputeWindow_NarrowerWindow_SourceRectIsStrictlyInsideTheFullBinRange()
    {
        // spec/18-path-to-1.0.md Medium item 2, second half: the actual target case -- Start=1000,
        // Span=1600 (the app's own real default) against a wider 0..4000Hz frame.
        var ok = WaterfallRenderMath.TryComputeWindow(
            startHz: 1000, spanHz: 1600, binWidthHz: 10, bins: 400, controlWidth: 400,
            out var sourceX, out var sourceWidth, out var destX, out var destWidth);

        Assert.True(ok);
        Assert.Equal(100, sourceX); // 1000Hz / 10Hz per bin
        Assert.Equal(160, sourceWidth); // 1600Hz span / 10Hz per bin
        Assert.Equal(0, destX); // window fully inside the frame -- fills the whole control
        Assert.Equal(400, destWidth);
    }

    [Fact]
    public void TryComputeWindow_WindowExtendsPastTheFramesOwnRange_ClampsSourceAndShrinksDestToMatch()
    {
        // The real risk plan-review caught: clamping the SOURCE alone and stretching it across the
        // full dest width would silently zoom past the requested range -- the exact "plots visibly
        // disagree" symptom this item exists to close (SpectrumTraceControl leaves blank space for
        // the out-of-range portion; the waterfall must leave an unfilled strip too, not fill it).
        // Frame covers 0..4000Hz (400 bins @ 10Hz); window asks for 3000..5000Hz -- only the first
        // 1000Hz of the requested 2000Hz span is actually available.
        var ok = WaterfallRenderMath.TryComputeWindow(
            startHz: 3000, spanHz: 2000, binWidthHz: 10, bins: 400, controlWidth: 400,
            out var sourceX, out var sourceWidth, out var destX, out var destWidth);

        Assert.True(ok);
        Assert.Equal(300, sourceX); // 3000Hz / 10Hz per bin
        Assert.Equal(100, sourceWidth); // only 1000Hz of the 2000Hz requested span exists
        Assert.Equal(0, destX); // the visible portion starts at the control's own left edge
        Assert.Equal(200, destWidth); // half the requested span is available -> half the width, not the full 400
    }

    [Fact]
    public void TryComputeWindow_WindowStartsBeforeTheFramesOwnRange_LeavesALeadingGapInTheDestRect()
    {
        // Mirror of the above at the other edge: Start=-500 (before 0Hz), Span=1500 -- only the
        // 0..1000Hz portion exists, and it should render starting partway across the control, not
        // flush against the left edge (which would silently zoom in, same failure class as above).
        var ok = WaterfallRenderMath.TryComputeWindow(
            startHz: -500, spanHz: 1500, binWidthHz: 10, bins: 400, controlWidth: 300,
            out var sourceX, out var sourceWidth, out var destX, out var destWidth);

        Assert.True(ok);
        Assert.Equal(0, sourceX); // clamped up from the negative raw start
        Assert.Equal(100, sourceWidth); // 0..1000Hz = 100 bins
        Assert.True(destX > 0); // a real leading gap, not flush against the left edge
        // Requested span is 1500Hz (-500..1000); 500Hz of it (1/3) falls before 0Hz and doesn't
        // exist, leaving a proportional gap; the remaining 1000Hz (2/3) is what actually renders.
        Assert.Equal(500.0 / 1500.0 * 300, destX, precision: 6);
        Assert.Equal(1000.0 / 1500.0 * 300, destWidth, precision: 6);
    }

    [Fact]
    public void TryComputeWindow_WindowEntirelyOutsideTheFramesRange_ReturnsFalse()
    {
        var ok = WaterfallRenderMath.TryComputeWindow(
            startHz: 10000, spanHz: 1000, binWidthHz: 10, bins: 400, controlWidth: 400,
            out var sourceX, out var sourceWidth, out var destX, out var destWidth);

        Assert.False(ok);
        Assert.Equal(0, sourceX);
        Assert.Equal(0, sourceWidth);
        Assert.Equal(0, destX);
        Assert.Equal(0, destWidth);
    }

    [Theory]
    [InlineData(0.0)] // zero span
    [InlineData(-100.0)] // negative span
    public void TryComputeWindow_NonPositiveEffectiveSpan_ReturnsFalse(double spanHz)
    {
        var ok = WaterfallRenderMath.TryComputeWindow(
            startHz: 1000, spanHz: spanHz, binWidthHz: 10, bins: 400, controlWidth: 400,
            out _, out _, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryComputeWindow_NoFrameYet_BinWidthHzZero_ReturnsFalseWithoutDividingByZero()
    {
        var ok = WaterfallRenderMath.TryComputeWindow(
            startHz: 1000, spanHz: 1600, binWidthHz: 0, bins: 0, controlWidth: 400,
            out var sourceX, out var sourceWidth, out var destX, out var destWidth);

        Assert.False(ok);
        Assert.Equal(0, sourceX);
        Assert.Equal(0, sourceWidth);
        Assert.Equal(0, destX);
        Assert.Equal(0, destWidth);
    }

    [Fact]
    public void TryComputeWindow_ZeroControlWidth_ReturnsFalse()
    {
        var ok = WaterfallRenderMath.TryComputeWindow(
            startHz: 1000, spanHz: 1600, binWidthHz: 10, bins: 400, controlWidth: 0,
            out _, out _, out _, out _);

        Assert.False(ok);
    }
}
