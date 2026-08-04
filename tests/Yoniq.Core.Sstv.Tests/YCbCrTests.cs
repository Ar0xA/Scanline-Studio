namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Targeted regression tests for the chroma-offset bug found by an independent Opus-driven
/// verification pass: <see cref="YCbCr.FromRgb"/> used to omit legacy <c>GetRY</c>'s +128 offset on
/// R-Y/B-Y, which <see cref="SstvRoundTripTests"/>'s self-consistency round-trip tests could never
/// catch, since <see cref="YCbCr.ToRgb"/> was symmetrically missing the same offset. These tests
/// check the conversion functions directly, against reference values derived from the legacy
/// formulas (<c>ComLib.cpp</c>'s <c>GetRY</c>/<c>YCtoRGB</c>), not just each other.
/// </summary>
public class YCbCrTests
{
    [Fact]
    public void FromRgb_NeutralGray_ProducesChromaCenteredAt128()
    {
        // For R=G=B, GetRY's R-Y/B-Y weight coefficients sum to exactly zero (0.439187 -
        // 0.367766 - 0.071421 = 0, and -0.148213 - 0.290974 + 0.439187 = 0), so neutral gray must
        // decode to exactly the +128 center point, not 0 -- the bug's exact signature.
        var (_, rMinusY, bMinusY) = YCbCr.FromRgb(128, 128, 128);

        Assert.Equal(128.0, rMinusY, precision: 6);
        Assert.Equal(128.0, bMinusY, precision: 6);
    }

    [Theory]
    [InlineData((byte)0, (byte)0, (byte)0)]
    [InlineData((byte)255, (byte)255, (byte)255)]
    [InlineData((byte)255, (byte)0, (byte)0)]
    [InlineData((byte)0, (byte)255, (byte)0)]
    [InlineData((byte)0, (byte)0, (byte)255)]
    [InlineData((byte)37, (byte)200, (byte)128)]
    public void ToRgb_InvertsFromRgb_WithinRoundingTolerance(byte r, byte g, byte b)
    {
        var (y, rMinusY, bMinusY) = YCbCr.FromRgb(r, g, b);
        var (r2, g2, b2) = YCbCr.ToRgb(y, rMinusY, bMinusY);

        // SHOULD item 4 (spec/14-roadmap.md): widened from +/-1 to +/-4 after FromRgb started
        // truncating (matching legacy's own GetRY int-truncation, ComLib.cpp:3653-3668) --
        // measured, not guessed: a 2,000,000-sample random sweep over the full RGB cube found a
        // real max divergence of exactly 4 (one FromRgb-side truncation of up to 1 level per
        // channel, amplified by ToRgb's own matrix coefficients, up to ~2x for the B channel's
        // BY-derived term). This is expected and legacy-faithful, not a regression: legacy's own
        // real TX/RX round-trip is lossy by the same two-truncation chain this fix now reproduces
        // -- exactly why the golden-vector tests carry a 15-25 average-delta tolerance rather than
        // expecting bit-exact reproduction. Round-1-review correction: an earlier version of this
        // comment said "+/-4 gives real margin above the measured max" -- wrong, the bound below
        // EQUALS the measured max exactly (4), not a margin above it; re-swept after the
        // FromRgb operator-grouping fix (this same review round) and confirmed the max is still
        // exactly 4, unchanged. Tight but not flaky: this measures a mathematical property of the
        // conversion functions, not floating-point/platform-dependent timing.
        Assert.InRange(r2, Math.Max(0, r - 4), Math.Min(255, r + 4));
        Assert.InRange(g2, Math.Max(0, g - 4), Math.Min(255, g + 4));
        Assert.InRange(b2, Math.Max(0, b - 4), Math.Min(255, b + 4));
    }
}
