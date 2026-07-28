namespace Yoniq.Abstractions.Sstv;

/// <summary>
/// Which family of per-line codec logic a mode needs — see <c>Yoniq.Core.Sstv.ScanlineCodecFactory</c>.
/// Not just a display label: this selects genuinely different encode/decode logic (composition,
/// not a single generic interpreter), because e.g. Robot36's YCbCr-with-alternating-chroma scheme
/// cannot be expressed as a fixed sequence of same-shaped channel scans the way RgbSequential can.
/// </summary>
public enum ColorEncoding
{
    /// <summary>N sequential same-shaped channel scans per line (Martin, Scottie, ...). Channel
    /// order/timing is pure data (<see cref="LineSegment"/> list) — see <c>AnalogFmSstvEncoder</c>/
    /// <c>AnalogFmSstvDecoder</c>'s default <c>RgbSequentialScanlineEncoder</c>/<c>Decoder</c>.</summary>
    RgbSequential,

    /// <summary>Robot-family: full-resolution Y scan + a short tone indicating which chroma
    /// channel follows (alternates R-Y/B-Y line to line) + a chroma scan, YCbCr->RGB converted at
    /// decode time using this line's fresh chroma and the other channel's most recently decoded
    /// value. See <c>RobotScanlineEncoder</c>/<c>RobotScanlineDecoder</c>.</summary>
    YCbCrRobot,

    /// <summary>Robot 72-style: Y, then R-Y, then B-Y, all scanned every line (no alternation, no
    /// cross-line chroma persistence) — a fixed marker tone before each chroma scan, not an
    /// information-bearing selector. See <c>YCbCrSequentialScanlineEncoder</c>/<c>Decoder</c>.</summary>
    YCbCrSequential,

    /// <summary>MP/PD-family: Y(odd line), R-Y, B-Y, Y(even line) — one chroma pair shared between
    /// two luma lines, so each transmission unit covers 2 image rows
    /// (<c>IScanlineEncoder.RowsPerTransmissionLine</c> = 2). See
    /// <c>YCbCrLinePairedScanlineEncoder</c>/<c>Decoder</c>.</summary>
    YCbCrLinePaired,

    /// <summary>RM8/RM12: monochrome only, no chroma channels at all. One transmitted line
    /// averages the luminance of two consecutive source rows into a single scanned value; on
    /// decode, that one value is written back into both of those rows (grayscale, R=G=B), so each
    /// transmission unit also covers 2 image rows (<c>RowsPerTransmissionLine</c> = 2) — but unlike
    /// <see cref="YCbCrLinePaired"/>, those 2 rows are never independently distinguishable, only a
    /// shared average. See <c>MonoAveragedPairedScanlineEncoder</c>/<c>Decoder</c>.</summary>
    MonoAveragedPaired,
}

/// <summary>One timed segment of an SSTV scanline: a fixed-frequency sync/porch/separator pulse, a
/// scan of one color channel whose frequency varies with pixel data, or (Robot family) a short
/// tone whose frequency indicates which of two alternating channels the following scan carries.</summary>
public abstract record LineSegment(double DurationMs);

public sealed record SyncSegment(double DurationMs, double FrequencyHz) : LineSegment(DurationMs);

public sealed record ScanSegment(string ChannelName, double DurationMs) : LineSegment(DurationMs);

/// <summary>Robot-family "which chroma channel follows" indicator tone. Encoder/decoder agree on
/// <paramref name="LowFrequencyHz"/>/<paramref name="HighFrequencyHz"/> as the two possible tones;
/// which one is sent for a given line is alternated by <c>RobotScanlineEncoder</c>, not fixed here.
/// CORRECTION (an independent Opus-driven verification pass caught this): an earlier version of
/// this comment claimed these two frequencies were "standards-informed, not traced from legacy
/// source... no source Hz constant to read directly" — that was false, and false for exactly the
/// reason CLAUDE.md's rules warn about: the RX decode path (which really does use a calibrated
/// amplitude comparison, not a raw Hz threshold) was checked instead of the TX line-generator
/// function, which has the literal value in plain sight. <c>TMmsstv::LineR36</c>
/// (<c>Main.cpp:6568</c>): <c>mp-&gt;Write(short(mp-&gt;m_wLine &amp; 1 ? 2300 : 1500), 4.5); //
/// RY=1500, BY=2300</c> — both frequencies below are the real, traced legacy TX values.</summary>
public sealed record ToneSelectorSegment(double DurationMs, double LowFrequencyHz, double HighFrequencyHz) : LineSegment(DurationMs);

/// <summary>
/// Data-driven mode description — see spec/06-sstv-dsp.md: "Mode timing/frequency tables are data,
/// not code." Adding a mode is adding a new <see cref="SstvModeDefinition"/>, not a new class.
///
/// CORRECTION (an independent Opus-driven verification pass caught this): an earlier version of
/// this comment claimed timing/frequency constants here were "taken from public SSTV protocol
/// documentation, not from the legacy MMSSTV/YONIQ binary directly" — that was false, and directly
/// contradicted <c>SstvModeRegistry</c>'s own (accurate) doc comment on the very same data. Per
/// CLAUDE.md: legacy source is the ground truth, not general SSTV domain knowledge or public
/// protocol write-ups — if the two ever disagreed, legacy wins. Every constant below is read
/// directly from the legacy TX line-generator functions (<c>Main.cpp</c>'s <c>Line*</c> family) and
/// the VIS/timing tables (<c>sstv.cpp</c>'s <c>GetTiming</c>/VIS-decode switch), per-mode, cross
/// checked against <c>GetTiming</c> in <c>SstvRoundTripTests</c> — see <c>SstvModeRegistry</c>'s
/// class doc comment for the full sourcing story and its record of near-misses caught this way.
///
/// The one caveat that *is* still real and unresolved: golden-vector cross-validation against
/// captured *output* from the actual running legacy binary (CLAUDE.md's behavioral-parity rule,
/// spec/13-testing.md) hasn't been done yet, since it requires building/running the original
/// C++Builder application, which isn't available in this environment. That's a narrower gap than
/// the one this comment used to (wrongly) claim — "read correctly from source code" and "verified
/// bit-for-bit against a real captured recording" are different, and only the second is still open.
/// </summary>
public sealed record SstvModeDefinition(
    string Id,
    string DisplayName,
    int VisCode,
    int ImageWidth,
    int ImageHeight,
    ColorEncoding ColorEncoding,
    IReadOnlyList<LineSegment> LineSegments,
    double LuminanceMinHz = 1500,
    double LuminanceMaxHz = 2300,
    /// <summary>Non-null for the MR/MP/ML family: this mode is identified by the two-byte
    /// "extended VIS" mechanism (escape code 0x23 followed by this raw byte), not a normal
    /// single-byte VIS code. <see cref="VisCode"/> is unused for these modes (set to a
    /// placeholder). See <c>Yoniq.Core.Sstv.VisHeader.GenerateExtendedSegments</c>.</summary>
    int? ExtendedVisCode = null,
    /// <summary>Non-null for the MN/MC ("narrow") family: confirmed by reading both stages of
    /// <c>sstv.cpp</c>'s VIS-decode switch directly that these modes have <em>no</em> standard or
    /// extended VIS code at all. Legacy identifies them with a completely different, fixed 4-byte
    /// FSK packet sent instead of a VIS header (<c>TMmsstv::ToTX</c>, <c>Main.cpp:7395-7424</c>):
    /// <c>[0x2d][0x15][modeCode][modeCode^0x15]</c>, each byte sent 6-bit LSB-first via
    /// <c>CSSTVMOD::WriteFSK</c> (<c>sstv.cpp:2942</c>). This is a small, self-contained
    /// mode-announce packet — not the general station-ID FSK subsystem (the separate
    /// 0x2a-prefixed callsign packet decoded by the same <c>DecodeFSK</c> state machine,
    /// <c>sstv.cpp</c>, still not ported — see spec/06-sstv-dsp.md's FSK/CW station ID item).
    /// <see cref="VisCode"/>/<see cref="ExtendedVisCode"/> are unused for these modes. See
    /// <c>Yoniq.Core.Sstv.VisHeader.GenerateNarrowModeSegments</c>.</summary>
    int? NarrowModeCode = null)
{
    public double LineDurationMs => LineSegments.Sum(s => s.DurationMs);
}
