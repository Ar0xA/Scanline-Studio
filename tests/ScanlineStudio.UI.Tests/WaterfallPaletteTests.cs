using ScanlineStudio.UI.Controls;

namespace ScanlineStudio.UI.Tests;

public sealed class WaterfallPaletteTests
{
    [Fact]
    public void Lerp_AtZero_ReturnsTheFirstStopExactly()
    {
        var (r, g, b) = WaterfallPalette.Lerp(0.0);

        Assert.Equal((0x0A, 0x0A, 0x14), ((int)r, (int)g, (int)b));
    }

    [Fact]
    public void Lerp_AtOne_ReturnsTheLastStopExactly()
    {
        var (r, g, b) = WaterfallPalette.Lerp(1.0);

        Assert.Equal((0xFF, 0x3C, 0x28), ((int)r, (int)g, (int)b));
    }

    [Theory]
    [InlineData(-5.0)]
    [InlineData(-0.001)]
    public void Lerp_BelowZero_ClampsToTheFirstStop(double normalized)
    {
        var atZero = WaterfallPalette.Lerp(0.0);
        var belowZero = WaterfallPalette.Lerp(normalized);

        Assert.Equal(atZero, belowZero);
    }

    [Theory]
    [InlineData(1.001)]
    [InlineData(50.0)]
    public void Lerp_AboveOne_ClampsToTheLastStop(double normalized)
    {
        var atOne = WaterfallPalette.Lerp(1.0);
        var aboveOne = WaterfallPalette.Lerp(normalized);

        Assert.Equal(atOne, aboveOne);
    }

    [Fact]
    public void Lerp_StrongSignalEndIsBrighterThanWeakSignalEnd()
    {
        // Auditor-caught (batch 8): this does NOT assert monotonic brightness across the whole
        // gradient -- the palette genuinely dips in brightness between the cyan (0.35) and green
        // (0.55) stops (a known, accepted cost of a jet-style multi-hue ramp, same convention
        // WSJT-X/SDR++/GQRX use). Only the two ENDPOINTS are compared: the weak-signal end must be
        // genuinely darker than the strong-signal end, the one property that actually matters for a
        // Low=weak/High=strong waterfall (see WaterfallPalette's own doc comment for the legacy
        // semantic this preserves).
        var weak = WaterfallPalette.Lerp(0.0);
        var strong = WaterfallPalette.Lerp(1.0);

        var weakBrightness = weak.R + weak.G + weak.B;
        var strongBrightness = strong.R + strong.G + strong.B;

        Assert.True(strongBrightness > weakBrightness);
    }

    [Fact]
    public void Lerp_AtAnInteriorStop_ReturnsThatStopExactly()
    {
        // 0.55 is the "green" stop -- confirms interior stops are hit exactly, not just the ends.
        var (r, g, b) = WaterfallPalette.Lerp(0.55);

        Assert.Equal((0x00, 0xC8, 0x50), ((int)r, (int)g, (int)b));
    }

    [Fact]
    public void Lerp_BetweenTwoStops_InterpolatesLinearly()
    {
        // Midpoint between the 0.75 (yellow, 0xE6DC00) and 1.00 (red, 0xFF3C28) stops -- 242.5/140/20
        // rounds via Math.Round's default banker's rounding (round-half-to-even), so 242.5 -> 242.
        var (r, g, b) = WaterfallPalette.Lerp(0.875);

        Assert.Equal((242, 140, 20), ((int)r, (int)g, (int)b));
    }
}
