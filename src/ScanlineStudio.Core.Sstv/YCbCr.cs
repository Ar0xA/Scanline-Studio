namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// YCbCr<->RGB conversion for the Robot family. <see cref="ToRgb"/> is a direct port of legacy
/// <c>YCtoRGB</c> (`ComLib.cpp:3475-3480`), used by the RX decode path. <see cref="FromRgb"/> uses
/// the actual weight coefficients from legacy's TX-side forward conversion, <c>GetRY</c>
/// (`ComLib.cpp:3653-3666`, the active `#else` branch, not the `#if 0`'d alternate set --
/// comprehensive-review correction: an earlier version of this range stopped at :3665, one line short
/// of the B-Y assignment at :3666).
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

    /// <summary>
    /// SHOULD item 4 (spec/14-roadmap.md): legacy's <c>GetRY</c> (`ComLib.cpp:3653-3669`) writes
    /// into <c>int&amp;</c> out-parameters -- assigning the double RHS truncates toward zero (this
    /// is legacy's FIRST of two truncations in the TX color-to-frequency chain; the second is in
    /// <see cref="ColorToFreq"/>). <c>LimitRGB(Y, RY, BY)</c> (`ComLib.cpp:3468-3473`, calling
    /// <c>Limit256</c> on each) runs AFTER that truncation, clamping the already-truncated ints to
    /// [0,255] -- <c>Math.Floor</c> then <c>Math.Clamp</c> below matches that exact order. Truncating
    /// toward zero via <c>Math.Floor</c> is valid here specifically because Y/R-Y/B-Y are always
    /// non-negative for any real 8-bit RGB input (confirmed by bounding each weighted sum against
    /// its own worst-case R/G/B combination) -- for a value that could go negative, C++'s
    /// truncate-toward-zero and <c>Math.Floor</c> would diverge, but that case doesn't arise here.
    /// </summary>
    public static (double Y, double RMinusY, double BMinusY) FromRgb(byte r, byte g, byte b)
    {
        // Round-1-review finding (auditor): parenthesized to match legacy's exact operation grouping
        // (`ComLib.cpp:3664-3666`: `16.0 + (0.256773*R + ...)`, weighted terms summed FIRST, then the
        // offset added last) -- not a style choice. C#'s default left-to-right `+` association
        // (`16 + a*r + b*g + c*b` = `((16 + a*r) + b*g) + c*b`) evaluates in a DIFFERENT order than
        // legacy's, and floating-point addition isn't associative: for R-Y/B-Y specifically, every
        // R=G=B gray level's exact value is precisely 128 (the weight coefficients sum to exactly
        // zero), i.e. exactly on the Math.Floor truncation boundary -- which of the two association
        // orders is used decides whether ~1e-14 rounding noise lands each gray level on 128 or 127.
        // Real (not hypothetical): reachable on every neutral-gray pixel, not just the R=G=B=128 case
        // FromRgb_NeutralGray_ProducesChromaCenteredAt128 already pins. Comprehensive-review caveat:
        // that test's own R=G=B=128 case is the one gray level provably bit-exact regardless of FPU
        // behavior (every product is an exact power-of-2 scaling of 128, every subtraction is
        // Sterbenz-exact), which is why it's the one pinned -- for OTHER gray levels, bit-exactness
        // against the real legacy BINARY additionally depends on C++Builder's own FPU precision-control
        // word (classic BCB defaults to 80-bit x87 extended, not .NET's 53-bit double), which can't be
        // confirmed from source alone. Impact if they diverge is cosmetic and bounded to +-1 chroma
        // level on non-128 gray pixels specifically -- not chased further here, no legacy binary
        // available to test against.
        var y = 16 + (0.256773 * r + 0.504097 * g + 0.097900 * b);
        var rMinusY = 128 + (0.439187 * r - 0.367766 * g - 0.071421 * b); // GetRY's real weights + offset
        var bMinusY = 128 + (-0.148213 * r - 0.290974 * g + 0.439187 * b);
        return (
            Math.Clamp(Math.Floor(y), 0, 255),
            Math.Clamp(Math.Floor(rMinusY), 0, 255),
            Math.Clamp(Math.Floor(bMinusY), 0, 255));
    }

    /// <summary>
    /// SHOULD item 4 (spec/14-roadmap.md): direct port of legacy's <c>ColorToFreq</c>
    /// (`ComLib.cpp:3491-3495`: <c>d = d*(2300-1500)/256; return d+1500;</c>) and
    /// <c>ColorToFreqNarrow</c> (`ComLib.cpp:3497-3501`, same shape with narrow-band constants) --
    /// generalized via <paramref name="luminanceMinHz"/>/<paramref name="luminanceMaxHz"/>, which
    /// already carry each mode's correct band (matching this port's existing per-mode convention).
    /// Legacy's <c>d*800/256</c> is INTEGER division, not floating-point -- <c>Math.Floor</c> after
    /// the multiply-divide reproduces that exactly (bit-exact, not merely close, for two SEPARATE
    /// reasons, both required: the MULTIPLY, <paramref name="colorValue"/> * (max-min), is an exact
    /// integer product &lt;= 255*800 = 204000, far under 2^53, so IEEE double multiplication commits
    /// no rounding error on it at all -- not merely "well-rounded"; the subsequent DIVIDE is by 256,
    /// a power of two, so it's exact scaling too. Round-1-review correction: an earlier version of
    /// this comment only named the division's own exactness, leaving the multiply's -- the actually
    /// load-bearing half, since colorValue is only ever exactly 0-255 at real call sites, never an
    /// arbitrary double -- unstated. <paramref name="colorValue"/> is always an already-integer value
    /// at every real call site -- either <see cref="FromRgb"/>'s own output, already truncated above,
    /// or a raw RGB byte, which is an integer by construction. <paramref name="colorValue"/> must
    /// already be in legacy's integer domain -- this method does not itself truncate its input, only
    /// reproduces <c>ColorToFreq</c>'s own internal integer division. Every band this port currently
    /// defines (`SstvModeDefinition.LuminanceMinHz`/`MaxHz`) has integer Hz edges (the default
    /// 1500/2300, MN110's narrow 2044/2300) -- a future NON-integer band edge would silently break
    /// this bit-exactness argument without any test failing, since the existing exhaustive test
    /// hardcodes today's two bands rather than enumerating the mode registry.
    /// </summary>
    public static double ColorToFreq(double colorValue, double luminanceMinHz, double luminanceMaxHz) =>
        Math.Floor(colorValue * (luminanceMaxHz - luminanceMinHz) / 256.0) + luminanceMinHz;
}
