namespace Yoniq.Core.Sstv;

/// <summary>
/// YCbCr<->RGB conversion for the Robot family. <see cref="ToRgb"/> is a direct port of legacy
/// <c>YCtoRGB</c> (`ComLib.cpp`), used by the RX decode path. <see cref="FromRgb"/> uses the actual
/// weight coefficients from legacy's TX-side forward conversion, <c>GetRY</c> (`ComLib.cpp:3653`,
/// the active `#else` branch, not the `#if 0`'d alternate set) — found by searching for that
/// specific function name after an earlier attempt incorrectly assumed no forward function existed
/// in source and mathematically inverted <c>YCtoRGB</c>'s matrix instead. Per CLAUDE.md's "port
/// first, invent second" rule, using the real function's actual coefficients is strictly better
/// than a derived inverse, even though it isn't a perfect fit: see the offset note below.
///
/// <b>Known unresolved offset asymmetry</b>: legacy's <c>GetRY</c> centers R-Y/B-Y at 128 (adding
/// 128, mirroring how Y is stored 16-235-ish), but <c>YCtoRGB</c> does not subtract 128 before use
/// — read literally, these two legacy functions are not exact inverses of each other. The missing
/// "-128" is presumably absorbed somewhere in legacy's RX calibration layer (`GetPixelLevel`'s
/// user-configurable `m_DemOff`/`m_DemWhite`/`m_DemBlack` gain constants), which this port does not
/// replicate (see `PllFmDemodulator`'s and `AnalogFmSstvDecoder`'s parity notes — decode here uses
/// a direct linear frequency-to-byte mapping instead). To keep this port's own encode/decode pair
/// internally consistent, <see cref="FromRgb"/> uses <c>GetRY</c>'s real weight coefficients but
/// omits the R-Y/B-Y +128 offset (Y's +16 is kept, since that one *does* pair correctly with
/// <see cref="ToRgb"/>'s -16 step). Flagged here rather than silently resolved, since it's a real
/// gap in understanding the legacy calibration pipeline, not a settled design choice.
/// </summary>
internal static class YCbCr
{
    public static (byte R, byte G, byte B) ToRgb(double y, double rMinusY, double bMinusY)
    {
        var yy = y - 16;
        var r = 1.164457 * yy + 1.596128 * rMinusY;
        var g = 1.164457 * yy - 0.813022 * rMinusY - 0.391786 * bMinusY;
        var b = 1.164457 * yy + 2.017364 * bMinusY;
        return ((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));
    }

    public static (double Y, double RMinusY, double BMinusY) FromRgb(byte r, byte g, byte b)
    {
        var y = 16 + 0.256773 * r + 0.504097 * g + 0.097900 * b;
        var rMinusY = 0.439187 * r - 0.367766 * g - 0.071421 * b; // GetRY's real weights, +128 omitted, see doc comment
        var bMinusY = -0.148213 * r - 0.290974 * g + 0.439187 * b;
        return (y, rMinusY, bMinusY);
    }
}
