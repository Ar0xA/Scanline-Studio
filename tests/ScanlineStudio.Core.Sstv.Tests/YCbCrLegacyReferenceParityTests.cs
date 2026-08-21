namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Chunk 4a finding: YCbCr.FromRgb/ToRgb carried no independent legacy-reference test -- only one
/// pinned gray level plus a +/-4 SELF round-trip, coverage an ordering/offset error can pass
/// (exactly how FromRgb's missing +128 shipped once). This is the FromRgb/ToRgb sibling of
/// YCbCrColorToFreqTruncationTests: ComLib.cpp's GetRY (:3653-3668), YCtoRGB (:3475-3482) and
/// Limit256/LimitRGB (:3461-3473) transcribed using C#'s own int semantics (double->int assignment
/// truncates toward zero, identical to C++), then swept against the port.
/// </summary>
public class YCbCrLegacyReferenceParityTests
{
    private static int Limit256(int d) => d < 0 ? 0 : d > 255 ? 255 : d;

    // ComLib.cpp:3653-3668. The `int&` out-params truncate the double RHS toward zero on
    // assignment; LimitRGB clamps AFTER that -- that order, not the reverse.
    private static (int Y, int RY, int BY) LegacyGetRY(byte r, byte g, byte b)
    {
        double R = r, G = g, B = b;
        var y = (int)(16.0 + (0.256773 * R + 0.504097 * G + 0.097900 * B));
        var ry = (int)(128.0 + (0.439187 * R - 0.367766 * G - 0.071421 * B));
        var by = (int)(128.0 + (-0.148213 * R - 0.290974 * G + 0.439187 * B));
        return (Limit256(y), Limit256(ry), Limit256(by));
    }

    // ComLib.cpp:3475-3482. Legacy's RY/BY are ZERO-centered at its real RX call sites
    // (Main.cpp:4341/4350 pass GetPixelLevel's raw output with no +128); the port's ToRgb takes the
    // 128-centered domain and subtracts internally, so the reference gets rawRy/rawBy and the port
    // gets rawRy+128/rawBy+128. Truncate-then-Limit256 is legacy's real order -- the port clamps
    // then casts, which this test pins as equivalent.
    private static (int R, int G, int B) LegacyYCtoRGB(int y, int rawRy, int rawBy)
    {
        y -= 16;
        var r = (int)(1.164457 * y + 1.596128 * rawRy);
        var g = (int)(1.164457 * y - 0.813022 * rawRy - 0.391786 * rawBy);
        var b = (int)(1.164457 * y + 2.017364 * rawBy);
        return (Limit256(r), Limit256(g), Limit256(b));
    }

    [Fact]
    public void FromRgb_MatchesLegacyGetRY_OverTheFullRgbCube()
    {
        var mismatches = new List<string>();
        for (var r = 0; r <= 255 && mismatches.Count < 10; r++)
        for (var g = 0; g <= 255 && mismatches.Count < 10; g++)
        for (var b = 0; b <= 255 && mismatches.Count < 10; b++)
        {
            var expected = LegacyGetRY((byte)r, (byte)g, (byte)b);
            var (ay, ary, aby) = YCbCr.FromRgb((byte)r, (byte)g, (byte)b);
            var actual = ((int)ay, (int)ary, (int)aby);
            if (actual != expected)
            {
                mismatches.Add($"rgb({r},{g},{b}): expected {expected}, got {actual}");
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void ToRgb_MatchesLegacyYCtoRGB_IncludingItsTruncateThenClampOrder()
    {
        // Y sweeps its full Limit256'd domain (Main.cpp:4332/4387); raw chroma sweeps past +/-128 on
        // purpose -- legacy never Limit256's R-Y/B-Y (Main.cpp:4341-4342/:4350, :4396-4397/:4405-4406),
        // so out-of-band values genuinely reach YCtoRGB and are exactly where a clamp/truncate
        // ORDER difference would surface if one existed.
        var mismatches = new List<string>();
        for (var y = 0; y <= 255 && mismatches.Count < 10; y++)
        for (var rawRy = -160; rawRy <= 160 && mismatches.Count < 10; rawRy++)
        for (var rawBy = -160; rawBy <= 160 && mismatches.Count < 10; rawBy++)
        {
            var (er, eg, eb) = LegacyYCtoRGB(y, rawRy, rawBy);
            var (ar, ag, ab) = YCbCr.ToRgb(y, rawRy + 128.0, rawBy + 128.0);
            if (ar != er || ag != eg || ab != eb)
            {
                mismatches.Add($"y={y} ry={rawRy} by={rawBy}: expected ({er},{eg},{eb}), got ({ar},{ag},{ab})");
            }
        }

        Assert.Empty(mismatches);
    }
}
