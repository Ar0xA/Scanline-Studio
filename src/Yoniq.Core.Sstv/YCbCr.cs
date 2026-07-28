namespace Yoniq.Core.Sstv;

/// <summary>
/// YCbCr<->RGB conversion for the Robot family. <see cref="ToRgb"/> is a direct port of legacy
/// <c>YCtoRGB</c> (`ComLib.cpp:3475-3480`), used by the RX decode path. <see cref="FromRgb"/> uses
/// the actual weight coefficients from legacy's TX-side forward conversion, <c>GetRY</c>
/// (`ComLib.cpp:3653-3665`, the active `#else` branch, not the `#if 0`'d alternate set).
///
/// <b>Offset asymmetry — resolved, not a gap</b>: legacy's <c>GetRY</c> centers R-Y/B-Y at 128
/// (`ComLib.cpp:3664-3665`: <c>RY = 128.0 + (...)</c>), but <c>YCtoRGB</c> uses <c>RY</c>/<c>BY</c>
/// directly with no offset (`ComLib.cpp:3477-3479`) — read in isolation, these two legacy functions
/// really are not inverses. An earlier version of this comment called that an unresolved gap
/// "presumably absorbed somewhere in legacy's RX calibration layer" and shipped <see cref="FromRgb"/>
/// without the +128, matching <see cref="ToRgb"/>'s no-offset expectation directly. That premise was
/// wrong and the actual mechanism is in the source: legacy's RX chroma path calls
/// <c>GetPixelLevel</c> (no offset added) while its luminance path calls <c>GetPictureLevel</c> and
/// then explicitly adds 128 afterward (`Main.cpp:4331` vs `4341`/`4350`/`4396`/`4405`) — asymmetric
/// on purpose. With the demodulator calibration defaults <c>m_DemOff=0</c>,
/// <c>m_DemWhite=m_DemBlack=128/16384</c> (`Main.cpp:875-877`), <c>GetPixelLevel</c> maps the
/// *center* frequency (1900Hz, the middle of the 1500-2300Hz band) to exactly 0 — i.e. legacy's RX
/// already hands <c>YCtoRGB</c> a zero-centered chroma difference, and <c>GetRY</c>'s +128 exists
/// purely to put chroma into the same 0-256 byte domain <c>ColorToFreq</c> expects for frequency
/// generation (mirroring how Y's own +16 exists for the same reason and is *kept*, not omitted).
/// So both functions really are consistent end to end; the missing "-128" isn't missing, it happens
/// in the demodulation step this port folds directly into <see cref="ToRgb"/> instead (see below),
/// since this port doesn't model legacy's separate raw-ADC <c>GetPixelLevel</c> layer.
///
/// Bug this caused: without the +128, <see cref="FromRgb"/> produced a chroma value already
/// zero-centered (~-112..+112) which the scanline encoders then fed through the *same* 0-256-domain
/// <c>ColorToFreq</c>-style mapping used for Y (`mode.LuminanceMinHz + value * range / 256`) that
/// legacy's real 128-centered R-Y/B-Y value expects — shifting the whole chroma band down by
/// ~400Hz (center at ~1500Hz instead of 1900Hz), far enough for a saturated color to land at or
/// below the 1200Hz sync tone. Round-trip tests never caught this because <see cref="ToRgb"/> was
/// symmetrically missing the same offset, so encode+decode agreed with each other while both
/// disagreeing with real legacy TX/RX — exactly the failure mode CLAUDE.md's rules exist to catch.
/// </summary>
internal static class YCbCr
{
    public static (byte R, byte G, byte B) ToRgb(double y, double rMinusY, double bMinusY)
    {
        var yy = y - 16;
        var ry = rMinusY - 128; // undoes GetRY's +128 -- see this type's doc comment
        var by = bMinusY - 128;
        var r = 1.164457 * yy + 1.596128 * ry;
        var g = 1.164457 * yy - 0.813022 * ry - 0.391786 * by;
        var b = 1.164457 * yy + 2.017364 * by;
        return ((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));
    }

    public static (double Y, double RMinusY, double BMinusY) FromRgb(byte r, byte g, byte b)
    {
        var y = 16 + 0.256773 * r + 0.504097 * g + 0.097900 * b;
        var rMinusY = 128 + 0.439187 * r - 0.367766 * g - 0.071421 * b; // GetRY's real weights + offset
        var bMinusY = 128 - 0.148213 * r - 0.290974 * g + 0.439187 * b;
        return (y, rMinusY, bMinusY);
    }
}
