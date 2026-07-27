using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>
/// Built-in mode table. Per-mode timing constants from <c>CSSTVSET::SetSampFreq</c>'s per-mode
/// switch (`sstv.cpp`), VIS codes from the VIS-to-mode lookup switch (`sstv.cpp` around line 1993),
/// and — this is the part that actually matters, and the part an earlier version of this file got
/// wrong for Scottie — exact per-line segment order and sync placement read directly from the real
/// TX line-generator functions in `Main.cpp` (<c>LineMRT</c> for Martin, <c>LineSCT</c> for Scottie,
/// <c>LineR36</c>/<c>LineR72</c> for Robot), not inferred from RX decode branch widths. RX branch
/// widths give you correct *durations* but not necessarily correct *order or sync placement*: an
/// earlier Scottie entry here, built that way, had sync at the start of the line in R,G,B order —
/// the real structure (confirmed via <c>LineSCT</c>) is separator-G-separator-B-**sync in the
/// middle**-separator-R. Both versions passed their own round-trip test, because encoder and
/// decoder agreed with each other while both being wrong about real Scottie framing — exactly the
/// failure mode CLAUDE.md's "port first, invent second" rule exists to prevent, and exactly why
/// each mode here is now checked against the TX generator function, not just cross-checked for
/// total duration. Each mode's <c>LineDurationMs</c> is additionally cross-checked against
/// <c>CSSTVSET::GetTiming</c>'s return value in
/// <c>SstvRoundTripTests.LineDuration_MatchesLegacyGetTiming</c> — a necessary check but not a
/// sufficient one on its own, as the Scottie mistake demonstrated.
///
/// Known gap (see spec/06-sstv-dsp.md): <see cref="PllFmDemodulator"/> uses one fixed filter tuning
/// (the legacy <c>CPLL</c> constructor's own defaults, loopFC=1500Hz/outFC=900Hz — not invented)
/// for every mode. That's enough resolution for Martin M1, Scottie S1, and Scottie DX at the
/// legacy-matching 11025Hz default sample rate, but Martin M2 and Scottie S2 (shorter per-pixel
/// scan time) need a higher sample rate (44100Hz) to stay within round-trip tolerance at that fixed
/// tuning. Legacy likely adapts filter tuning per mode speed (there's a user-configurable "demod
/// profile" system, `PRODEM`, referenced in `Main.cpp`'s INI loading) — that adaptive selection is
/// not ported yet, so this port needs a higher sample rate as a stand-in rather than replicating
/// whatever legacy's own mechanism is. Flagged as a follow-up, not silently worked around.
/// </summary>
public static class SstvModeRegistry
{
    public static readonly SstvModeDefinition MartinM1 = new(
        Id: "martin-m1",
        DisplayName: "Martin M1",
        VisCode: 44,
        ImageWidth: 320,
        ImageHeight: 256,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments:
        [
            new SyncSegment(DurationMs: 4.862, FrequencyHz: 1200),
            new SyncSegment(DurationMs: 0.572, FrequencyHz: 1500), // porch
            new ScanSegment(ChannelName: "G", DurationMs: 146.432),
            new SyncSegment(DurationMs: 0.572, FrequencyHz: 1500), // separator
            new ScanSegment(ChannelName: "B", DurationMs: 146.432),
            new SyncSegment(DurationMs: 0.572, FrequencyHz: 1500), // separator
            new ScanSegment(ChannelName: "R", DurationMs: 146.432),
            new SyncSegment(DurationMs: 0.572, FrequencyHz: 1500), // separator
        ]);

    public static readonly SstvModeDefinition MartinM2 = new(
        Id: "martin-m2",
        DisplayName: "Martin M2",
        VisCode: 40,
        ImageWidth: 320,
        ImageHeight: 256,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments:
        [
            new SyncSegment(DurationMs: 4.862, FrequencyHz: 1200),
            new SyncSegment(DurationMs: 0.572, FrequencyHz: 1500), // porch
            new ScanSegment(ChannelName: "G", DurationMs: 73.216),
            new SyncSegment(DurationMs: 0.572, FrequencyHz: 1500), // separator
            new ScanSegment(ChannelName: "B", DurationMs: 73.216),
            new SyncSegment(DurationMs: 0.572, FrequencyHz: 1500), // separator
            new ScanSegment(ChannelName: "R", DurationMs: 73.216),
            new SyncSegment(DurationMs: 0.572, FrequencyHz: 1500), // separator
        ]);

    // Scottie family: read directly from the actual TX line-generator `TMmsstv::LineSCT` (Main.cpp)
    // after an earlier version of this entry — inferred only from RX branch widths — turned out to
    // have the wrong channel order AND wrong sync position (this comment used to claim R,G,B with
    // sync-first; both wrong). LineSCT's real order: separator, G, separator, B, SYNC (in the
    // middle, not at the start!), separator, R. No porch before the first separator, no trailing
    // separator after R. This is exactly the class of mistake CLAUDE.md's "port first, invent
    // second" rule exists to prevent — the self-consistent round-trip test passed anyway, because
    // encoder and decoder agreed with each other while both being wrong about real Scottie framing.
    public static readonly SstvModeDefinition ScottieS1 = new(
        Id: "scottie-s1",
        DisplayName: "Scottie S1",
        VisCode: 60,
        ImageWidth: 320,
        ImageHeight: 256,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments:
        [
            new SyncSegment(DurationMs: 1.5, FrequencyHz: 1500), // separator before G
            new ScanSegment(ChannelName: "G", DurationMs: 138.24),
            new SyncSegment(DurationMs: 1.5, FrequencyHz: 1500), // separator before B
            new ScanSegment(ChannelName: "B", DurationMs: 138.24),
            new SyncSegment(DurationMs: 9.0, FrequencyHz: 1200), // sync — between B and R, not at line start
            new SyncSegment(DurationMs: 1.5, FrequencyHz: 1500), // separator before R
            new ScanSegment(ChannelName: "R", DurationMs: 138.24),
        ]);

    public static readonly SstvModeDefinition ScottieS2 = new(
        Id: "scottie-s2",
        DisplayName: "Scottie S2",
        VisCode: 56,
        ImageWidth: 320,
        ImageHeight: 256,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments:
        [
            new SyncSegment(DurationMs: 1.5, FrequencyHz: 1500),
            new ScanSegment(ChannelName: "G", DurationMs: 88.064),
            new SyncSegment(DurationMs: 1.5, FrequencyHz: 1500),
            new ScanSegment(ChannelName: "B", DurationMs: 88.064),
            new SyncSegment(DurationMs: 9.0, FrequencyHz: 1200),
            new SyncSegment(DurationMs: 1.5, FrequencyHz: 1500),
            new ScanSegment(ChannelName: "R", DurationMs: 88.064),
        ]);

    public static readonly SstvModeDefinition ScottieDx = new(
        Id: "scottie-dx",
        DisplayName: "Scottie DX",
        VisCode: 76,
        ImageWidth: 320,
        ImageHeight: 256,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments:
        [
            new SyncSegment(DurationMs: 1.5, FrequencyHz: 1500),
            new ScanSegment(ChannelName: "G", DurationMs: 345.6),
            new SyncSegment(DurationMs: 1.5, FrequencyHz: 1500),
            new ScanSegment(ChannelName: "B", DurationMs: 345.6),
            new SyncSegment(DurationMs: 9.0, FrequencyHz: 1200),
            new SyncSegment(DurationMs: 1.5, FrequencyHz: 1500),
            new ScanSegment(ChannelName: "R", DurationMs: 345.6),
        ]);

    // Robot36: read directly from the real TX line-generator TMmsstv::LineR36 (Main.cpp) — sync,
    // porch, full-resolution Y scan, a color-select tone (1500Hz picks R-Y on even lines, 2300Hz
    // picks B-Y on odd lines — legacy's own comment: "RY=1500, BY=2300"), a second porch, then the
    // selected chroma channel's scan. Width/height/VIS code from CSSTVSET::GetBitmapSize/
    // GetPictureSize and the VIS lookup switch (sstv.cpp).
    public static readonly SstvModeDefinition Robot36 = new(
        Id: "robot-36",
        DisplayName: "Robot 36",
        VisCode: 8,
        ImageWidth: 320,
        ImageHeight: 240,
        ColorEncoding: ColorEncoding.YCbCrRobot,
        LineSegments:
        [
            new SyncSegment(DurationMs: 9.0, FrequencyHz: 1200),
            new SyncSegment(DurationMs: 3.0, FrequencyHz: 1500), // porch
            new ScanSegment(ChannelName: "Y", DurationMs: 88.0),
            new ToneSelectorSegment(DurationMs: 4.5, LowFrequencyHz: 1500, HighFrequencyHz: 2300), // 1500=R-Y, 2300=B-Y
            new SyncSegment(DurationMs: 1.5, FrequencyHz: 1900), // porch
            new ScanSegment(ChannelName: "C", DurationMs: 44.0), // R-Y or B-Y, selected by the tone above
        ]);

    // Robot 72: read directly from TMmsstv::LineR72 (Main.cpp) — NOT the same shape as Robot 36
    // despite the name (confirmed by reading the actual TX function, not by the RX switch's
    // case-grouping, which had misleadingly grouped R72 with the MR/ML family for shared bitmap
    // addressing only). Y, then R-Y, then B-Y, every line — no alternation, no cross-line
    // persistence. The 1500/2300Hz tones before each chroma scan are fixed markers here, not an
    // information-bearing selector like Robot 36's.
    public static readonly SstvModeDefinition Robot72 = new(
        Id: "robot-72",
        DisplayName: "Robot 72",
        VisCode: 12,
        ImageWidth: 320,
        ImageHeight: 240,
        ColorEncoding: ColorEncoding.YCbCrSequential,
        LineSegments:
        [
            new SyncSegment(DurationMs: 9.0, FrequencyHz: 1200),
            new SyncSegment(DurationMs: 3.0, FrequencyHz: 1500), // porch
            new ScanSegment(ChannelName: "Y", DurationMs: 138.0),
            new SyncSegment(DurationMs: 4.5, FrequencyHz: 1500), // R-Y marker
            new SyncSegment(DurationMs: 1.5, FrequencyHz: 1900), // porch
            new ScanSegment(ChannelName: "RY", DurationMs: 69.0),
            new SyncSegment(DurationMs: 4.5, FrequencyHz: 2300), // B-Y marker
            new SyncSegment(DurationMs: 1.5, FrequencyHz: 1900), // porch
            new ScanSegment(ChannelName: "BY", DurationMs: 69.0),
        ]);

    public static readonly IReadOnlyList<SstvModeDefinition> All = [MartinM1, MartinM2, ScottieS1, ScottieS2, ScottieDx, Robot36, Robot72];

    public static SstvModeDefinition? FindByVisCode(int visCode) => All.FirstOrDefault(m => m.VisCode == visCode);
}
