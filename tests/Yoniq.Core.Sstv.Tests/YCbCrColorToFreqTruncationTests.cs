namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// SHOULD item 4 (spec/14-roadmap.md): legacy's TX pixel-to-frequency chain truncates twice via
/// integer arithmetic -- <c>GetRY</c> (<c>ComLib.cpp:3653-3668</c>, double RHS assigned to an
/// <c>int&amp;</c> out-parameter, truncating toward zero) and <c>ColorToFreq</c>
/// (<c>ComLib.cpp:3491-3495</c>: <c>d = d*(2300-1500)/256;</c>, INTEGER division). This port used to
/// map in unrounded doubles end to end. This file proves <see cref="YCbCr.ColorToFreq"/> reproduces
/// legacy's own integer-division truncation exactly, independent of any scanline/segment/pixel-array
/// plumbing -- matching this project's established chop-into-pieces methodology (see
/// <c>MonoAveragedPairedScanlineDecoderTests</c>'s own doc comment for the RX-side sibling of this
/// exact approach).
/// </summary>
public class YCbCrColorToFreqTruncationTests
{
    // Legacy's real `int d; d = d*(2300-1500)/256; return d+1500;` (ComLib.cpp:3491-3495) --
    // replicated using C#'s own `int` division operator (truncates toward zero, identical to C++'s
    // int/int), independent of YCbCr.ColorToFreq's own implementation -- this is the reference the
    // port's formula is measured against, not a restatement of it.
    private static int LegacyColorToFreq(int d, int luminanceMinHz, int luminanceMaxHz)
    {
        d = d * (luminanceMaxHz - luminanceMinHz) / 256;
        return d + luminanceMinHz;
    }

    [Fact]
    public void ColorToFreq_MatchesLegacysIntegerDivision_ForEveryAchievableInputAndBand()
    {
        // Every integer-domain input GetRY/FromRgb can actually produce (already truncated+clamped
        // to [0,255] by FromRgb -- see its own doc comment), against both bands this port actually
        // uses (the standard 1500-2300Hz band, and MN110's narrow 2044-2300Hz band, confirming the
        // generalization via luminanceMinHz/MaxHz holds for a second, differently-shaped band too).
        var bands = new (int Min, int Max)[] { (1500, 2300), (2044, 2300) };

        foreach (var (min, max) in bands)
        {
            for (var d = 0; d <= 255; d++)
            {
                var expected = LegacyColorToFreq(d, min, max);
                var actual = YCbCr.ColorToFreq(d, min, max);

                // Bit-exact, not merely close: Math.Floor(d*(max-min)/256.0) is provably identical
                // to integer division here (see YCbCr.ColorToFreq's own doc comment -- 256 is a
                // power of two, so the floating-point division carries no rounding error).
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void FromRgb_AlwaysProducesAlreadyIntegerValues_MatchingGetRYsIntDomain()
    {
        // ColorToFreq's own correctness (proven above) is only meaningful if its real callers
        // actually feed it integer-domain values, matching legacy's own `int d` parameter --
        // confirms FromRgb's truncation (not just ColorToFreq's own division) is load-bearing.
        for (var r = 0; r <= 255; r += 17) // sparse sweep, full 256^3 is unnecessary for this property
        {
            for (var g = 0; g <= 255; g += 17)
            {
                for (var b = 0; b <= 255; b += 17)
                {
                    var (y, rMinusY, bMinusY) = YCbCr.FromRgb((byte)r, (byte)g, (byte)b);

                    Assert.Equal(y, Math.Floor(y));
                    Assert.Equal(rMinusY, Math.Floor(rMinusY));
                    Assert.Equal(bMinusY, Math.Floor(bMinusY));
                }
            }
        }
    }
}
