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

        // +/-1 for byte rounding on each side of the forward/inverse matrix pair.
        Assert.InRange(r2, Math.Max(0, r - 1), Math.Min(255, r + 1));
        Assert.InRange(g2, Math.Max(0, g - 1), Math.Min(255, g + 1));
        Assert.InRange(b2, Math.Max(0, b - 1), Math.Min(255, b + 1));
    }
}
