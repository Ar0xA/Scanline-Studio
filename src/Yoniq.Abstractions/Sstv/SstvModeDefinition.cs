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
/// These two frequencies are standards-informed, not traced from legacy source — the legacy RX
/// code makes this decision on a calibrated amplitude scale (<c>GetPixelLevel</c>'s output), not a
/// raw Hz comparison, so there's no source Hz constant to read directly. See
/// <see cref="SstvModeDefinition"/>'s parity note.</summary>
public sealed record ToneSelectorSegment(double DurationMs, double LowFrequencyHz, double HighFrequencyHz) : LineSegment(DurationMs);

/// <summary>
/// Data-driven mode description — see spec/06-sstv-dsp.md: "Mode timing/frequency tables are data,
/// not code." Adding a mode is adding a new <see cref="SstvModeDefinition"/>, not a new class.
///
/// Timing/frequency constants below are taken from public SSTV protocol documentation, not from
/// the legacy MMSSTV/YONIQ binary directly — golden-vector cross-validation against that binary
/// (CLAUDE.md's behavioral-parity rule, spec/13-testing.md) has not been done yet, since it
/// requires building/running the original C++Builder application, which isn't available in this
/// environment. Treat these constants as "internally consistent and standards-informed," not yet
/// "verified to match legacy MMSSTV output bit-for-bit."
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
    int? ExtendedVisCode = null)
{
    public double LineDurationMs => LineSegments.Sum(s => s.DurationMs);
}
