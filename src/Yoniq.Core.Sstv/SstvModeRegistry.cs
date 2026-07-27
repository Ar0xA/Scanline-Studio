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

    // AVT: read directly from TMmsstv::LineAVT (Main.cpp) — no sync/porch at all, just R,G,B
    // scanned back to back. Simplest mode found so far.
    public static readonly SstvModeDefinition Avt = new(
        Id: "avt",
        DisplayName: "AVT",
        VisCode: 68,
        ImageWidth: 320,
        ImageHeight: 240,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments:
        [
            new ScanSegment(ChannelName: "R", DurationMs: 125.0),
            new ScanSegment(ChannelName: "G", DurationMs: 125.0),
            new ScanSegment(ChannelName: "B", DurationMs: 125.0),
        ]);

    // Pasokon P3/P5/P7: read from TMmsstv::LineP (Main.cpp) -- sync, then R/G/B each preceded by a
    // porch, plus one trailing untagged porch. Fits the existing RgbSequential family (simple
    // sequential channel scans), no new codec code needed. 640x496 per CSSTVSET::GetBitmapSize.
    private static SstvModeDefinition CreatePasokonMode(string id, string displayName, int visCode, double syncMs, double porchMs, double scanMs) => new(
        Id: id,
        DisplayName: displayName,
        VisCode: visCode,
        ImageWidth: 640,
        ImageHeight: 496,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments:
        [
            new SyncSegment(DurationMs: syncMs, FrequencyHz: 1200),
            new SyncSegment(DurationMs: porchMs, FrequencyHz: 1500),
            new ScanSegment(ChannelName: "R", DurationMs: scanMs),
            new SyncSegment(DurationMs: porchMs, FrequencyHz: 1500),
            new ScanSegment(ChannelName: "G", DurationMs: scanMs),
            new SyncSegment(DurationMs: porchMs, FrequencyHz: 1500),
            new ScanSegment(ChannelName: "B", DurationMs: scanMs),
            new SyncSegment(DurationMs: porchMs, FrequencyHz: 1500), // trailing porch
        ]);

    public static readonly SstvModeDefinition P3 = CreatePasokonMode("p3", "Pasokon P3", 113, 5.208, 1.042, 133.333);
    public static readonly SstvModeDefinition P5 = CreatePasokonMode("p5", "Pasokon P5", 114, 7.813, 1.562375, 200.000);
    public static readonly SstvModeDefinition P7 = CreatePasokonMode("p7", "Pasokon P7", 115, 10.417, 2.083, 266.667);

    // MR (Robot-Martin hybrid, per-line full Y + full-width-but-half-duration R-Y and B-Y, no
    // alternation) and ML (same shape, larger 640x496 bitmap per CSSTVSET::GetBitmapSize) families:
    // read from TMmsstv::LineMR (Main.cpp). Uses the two-byte "extended VIS" mechanism (escape
    // 0x23 + a second raw byte — see VisHeader.GenerateExtendedSegments), not a normal VIS code.
    // The three 0.1ms "hold last frequency" segments after each scan (analog PLL settling in real
    // hardware) are approximated here as brief 1900Hz pulses rather than modeling the actual
    // repeated-last-sample behavior — negligible at 0.1ms out of a 200-700ms line, and not
    // information-bearing, but noted as a simplification rather than silently assumed identical.
    private static SstvModeDefinition CreateMrFamilyMode(string id, string displayName, int extendedCode, int width, int height, double scanDurationMs) => new(
        Id: id,
        DisplayName: displayName,
        VisCode: 0,
        ExtendedVisCode: extendedCode,
        ImageWidth: width,
        ImageHeight: height,
        ColorEncoding: ColorEncoding.YCbCrSequential,
        LineSegments:
        [
            new SyncSegment(DurationMs: 9.0, FrequencyHz: 1200),
            new SyncSegment(DurationMs: 1.0, FrequencyHz: 1500), // porch
            new ScanSegment(ChannelName: "Y", DurationMs: scanDurationMs),
            new SyncSegment(DurationMs: 0.1, FrequencyHz: 1900), // hold, see note above
            new ScanSegment(ChannelName: "RY", DurationMs: scanDurationMs / 2),
            new SyncSegment(DurationMs: 0.1, FrequencyHz: 1900), // hold
            new ScanSegment(ChannelName: "BY", DurationMs: scanDurationMs / 2),
            new SyncSegment(DurationMs: 0.1, FrequencyHz: 1900), // hold
        ]);

    public static readonly SstvModeDefinition Mr73 = CreateMrFamilyMode("mr73", "MR73", 0x45, 320, 256, 138.0);
    public static readonly SstvModeDefinition Mr90 = CreateMrFamilyMode("mr90", "MR90", 0x46, 320, 256, 171.0);
    public static readonly SstvModeDefinition Mr115 = CreateMrFamilyMode("mr115", "MR115", 0x49, 320, 256, 220.0);
    public static readonly SstvModeDefinition Mr140 = CreateMrFamilyMode("mr140", "MR140", 0x4a, 320, 256, 269.0);
    public static readonly SstvModeDefinition Mr175 = CreateMrFamilyMode("mr175", "MR175", 0x4c, 320, 256, 337.0);

    public static readonly SstvModeDefinition Ml180 = CreateMrFamilyMode("ml180", "ML180", 0x85, 640, 496, 176.5);
    public static readonly SstvModeDefinition Ml240 = CreateMrFamilyMode("ml240", "ML240", 0x86, 640, 496, 236.5);
    public static readonly SstvModeDefinition Ml280 = CreateMrFamilyMode("ml280", "ML280", 0x89, 640, 496, 277.5);
    public static readonly SstvModeDefinition Ml320 = CreateMrFamilyMode("ml320", "ML320", 0x8a, 640, 496, 317.5);

    // MP family: read from TMmsstv::LineMP (Main.cpp) -- NOT the same shape as MR/ML (name
    // similarity again nearly misleading, per CLAUDE.md's "no assumptions" rule): it's line-paired
    // like PD (Y-odd, R-Y, B-Y, Y-even sharing one chroma pair), not a per-line hold-then-half-scan
    // shape. Also uses the extended VIS mechanism. ImageHeight = 2 x CSSTVSET's m_L (confirmed by
    // cross-checking against GetBitmapSize for the PD-series, which shares this exact TX function
    // shape and whose m_L values line up with half of GetBitmapSize's explicit heights).
    private static SstvModeDefinition CreateMpFamilyMode(string id, string displayName, int extendedCode, int width, int transmissionUnits, double scanDurationMs) => new(
        Id: id,
        DisplayName: displayName,
        VisCode: 0,
        ExtendedVisCode: extendedCode,
        ImageWidth: width,
        ImageHeight: transmissionUnits * 2,
        ColorEncoding: ColorEncoding.YCbCrLinePaired,
        LineSegments:
        [
            new SyncSegment(DurationMs: 9.0, FrequencyHz: 1200),
            new SyncSegment(DurationMs: 1.0, FrequencyHz: 1500), // porch
            new ScanSegment(ChannelName: "Y1", DurationMs: scanDurationMs),
            new ScanSegment(ChannelName: "RY", DurationMs: scanDurationMs),
            new ScanSegment(ChannelName: "BY", DurationMs: scanDurationMs),
            new ScanSegment(ChannelName: "Y2", DurationMs: scanDurationMs),
        ]);

    public static readonly SstvModeDefinition Mp73 = CreateMpFamilyMode("mp73", "MP73", 0x25, 320, 128, 140.0);
    public static readonly SstvModeDefinition Mp115 = CreateMpFamilyMode("mp115", "MP115", 0x29, 320, 128, 223.0);
    public static readonly SstvModeDefinition Mp140 = CreateMpFamilyMode("mp140", "MP140", 0x2a, 320, 128, 270.0);
    public static readonly SstvModeDefinition Mp175 = CreateMpFamilyMode("mp175", "MP175", 0x2c, 320, 128, 340.0);

    // PD-series: read from TMmsstv::LinePD (Main.cpp) -- structurally identical to LineMP (same
    // Y1-RY-BY-Y2 shape) but with fixed 20ms/2.08ms sync/porch (hardcoded in LinePD, not a
    // parameter) and normal (non-extended) VIS codes from sstv.cpp's VIS lookup switch.
    private static SstvModeDefinition CreatePdMode(string id, string displayName, int visCode, int width, int transmissionUnits, double scanDurationMs) => new(
        Id: id,
        DisplayName: displayName,
        VisCode: visCode,
        ImageWidth: width,
        ImageHeight: transmissionUnits * 2,
        ColorEncoding: ColorEncoding.YCbCrLinePaired,
        LineSegments:
        [
            new SyncSegment(DurationMs: 20.0, FrequencyHz: 1200),
            new SyncSegment(DurationMs: 2.08, FrequencyHz: 1500), // porch
            new ScanSegment(ChannelName: "Y1", DurationMs: scanDurationMs),
            new ScanSegment(ChannelName: "RY", DurationMs: scanDurationMs),
            new ScanSegment(ChannelName: "BY", DurationMs: scanDurationMs),
            new ScanSegment(ChannelName: "Y2", DurationMs: scanDurationMs),
        ]);

    public static readonly SstvModeDefinition Pd50 = CreatePdMode("pd50", "PD50", 93, 320, 128, 91.520);
    public static readonly SstvModeDefinition Pd90 = CreatePdMode("pd90", "PD90", 99, 320, 128, 170.240);
    public static readonly SstvModeDefinition Pd120 = CreatePdMode("pd120", "PD120", 95, 640, 248, 121.600);
    public static readonly SstvModeDefinition Pd160 = CreatePdMode("pd160", "PD160", 98, 512, 200, 195.584);
    public static readonly SstvModeDefinition Pd180 = CreatePdMode("pd180", "PD180", 96, 640, 248, 183.040);
    public static readonly SstvModeDefinition Pd240 = CreatePdMode("pd240", "PD240", 97, 640, 248, 244.480);
    public static readonly SstvModeDefinition Pd290 = CreatePdMode("pd290", "PD290", 94, 800, 308, 228.800);

    public static readonly IReadOnlyList<SstvModeDefinition> All =
    [
        MartinM1, MartinM2, ScottieS1, ScottieS2, ScottieDx, Robot36, Robot72, Avt,
        Mr73, Mr90, Mr115, Mr140, Mr175, Ml180, Ml240, Ml280, Ml320,
        Mp73, Mp115, Mp140, Mp175,
        Pd50, Pd90, Pd120, Pd160, Pd180, Pd240, Pd290,
        P3, P5, P7,
    ];

    public static SstvModeDefinition? FindByVisCode(int visCode) =>
        All.FirstOrDefault(m => m.ExtendedVisCode is null && m.VisCode == visCode);

    public static SstvModeDefinition? FindByExtendedCode(int extendedCode) =>
        All.FirstOrDefault(m => m.ExtendedVisCode == extendedCode);
}
