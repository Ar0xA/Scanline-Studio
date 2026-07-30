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
/// tuning.
///
/// CORRECTION (previously this comment claimed legacy has a per-mode-speed adaptive "demod
/// profile" system named `PRODEM` — that name does not exist anywhere in the legacy source; it was
/// an unverified guess that got written down as if confirmed. Directly checked: `CPLL`'s only
/// tuning toggle is <c>SetWidth(fNarrow)</c>, which switches between exactly two fixed
/// configurations — Normal (1500-2300Hz) and Narrow (`NARROW_LOW`=2044/`NARROW_HIGH`=2300,
/// `sstv.h`) — selected by <c>IsNarrowMode(mode)</c>, i.e. precisely the MN/MC family already
/// handled by this registry's <c>LuminanceMinHz</c>/<c>MaxHz</c> fields. There is no third variant
/// and no per-speed adaptation: legacy decodes Martin M2 and Scottie S2 with the *same* fixed
/// loopFC=1500/outFC=900 tuning, at the *same* 11025Hz default sample rate, as every other mode.
/// So the 44100Hz stand-in in this port is not a missing legacy feature to port — it's compensating
/// for a gap elsewhere in this port's own Phase 1 decoder (see <see cref="AnalogFmSstvDecoder"/>'s
/// doc comment: nominal-timing line reconstruction with no per-line sync re-search/AFC), which is
/// part of the separate, larger, still-unported <c>CSSTVDEM</c> sync/AFC pipeline.
///
/// Also not an M2/S2-only issue: measured every RGB/YCbCr-sequential mode's round-trip at 11025Hz
/// directly (temporary experiment, reverted) and found a clean correlation with samples-per-pixel
/// at that rate, not mode identity — everything below ~4 samples/pixel fails the 10.0
/// average-per-channel-delta tolerance (Robot36, Robot72, MR73, ML180/240/280/320, Martin M2 all
/// fail; Martin M1, Scottie S1/DX, MR90/140/175 all comfortably pass).
///
/// UPDATE: root-caused and partially fixed. The suspected cause above (block-averaging needing more
/// samples to settle) was investigated by reading legacy's actual per-pixel readout
/// (`Main.cpp:4144-4148`): it reads a single raw discriminator sample per pixel (whichever sample
/// is first at that pixel's computed index — `GetPictureLevel`/`GetPixelLevel` just dereference
/// `*ip`), not a windowed average. This port's block-averaging was an invented technique, not traced
/// from source. Fixed (see <see cref="IScanlineDecoder"/>'s `sampleFrequencyAt` — the old
/// `averageFrequencyInWindow` name was renamed because it was no longer, and shouldn't have ever
/// been, an accurate description). Re-measured at 11025Hz after the fix: real, consistent
/// improvement across nearly every previously-failing mode, and MR115 now passes outright — but
/// several modes (Robot36/72, MR73, ML180-320) still fail the 10.0-delta tolerance even with the
/// single-sample fix. So this was one real, correctly-targeted bug, not the *whole* explanation —
/// the remaining gap likely needs the still-unported AFC drift-tracking loop (`SyncFreq`/
/// `m_AFCDiff`) or a closer look at this port's `PllFmDemodulator` settling speed, kept as further
/// follow-up rather than assumed away. See spec/14-roadmap.md's Phase 1 entry for the full
/// before/after measured table.
///
/// 43/43 modes done — every mode in the legacy table has an entry here. MN/MC ("narrow" family)
/// surfaced a real protocol difference rather than a
/// shape difference — legacy identifies these modes via neither a normal nor an "extended" VIS
/// code (confirmed by searching both stages of <c>sstv.cpp</c>'s VIS-decode switch directly), but
/// via a distinct, small, fixed FSK mode-announce packet sent in its place
/// (<c>Main.cpp:7395-7424</c>). See <see cref="CreateMnFamilyMode"/>'s doc comment and
/// <see cref="Yoniq.Abstractions.Sstv.SstvModeDefinition.NarrowModeCode"/> for the full story, and
/// <see cref="VisHeader.GenerateNarrowModeSegments"/> for the ported mechanism itself — added
/// because "port first, invent second" means porting *that* packet, not inventing a fake VIS code
/// to fit the existing mechanism just because it was already there.
///
/// R24 fits the existing <see cref="ColorEncoding.YCbCrSequential"/> family (same Y/R-Y/B-Y shape
/// as Robot 72, confirmed via the RX decode switch grouping them together, not by name
/// resemblance) but surfaced a genuine legacy display-doubling quirk: see <see cref="R24"/>'s doc
/// comment for why its <c>ImageHeight</c> is modeled as 120, not the 240 its VIS-adjacent picture
/// dimensions might suggest.
///
/// RM8/RM12 needed a genuinely new <see cref="ColorEncoding.MonoAveragedPaired"/> family —
/// monochrome only, no chroma, averaging two source rows into one transmitted value. See
/// <see cref="CreateMonoAveragedMode"/>'s doc comment for the full mechanism and for a correction
/// this addition surfaced in <see cref="R24"/>'s own doc comment (a double-increment in the TX
/// dispatch loop that an earlier pass over R24 had misread).
///
/// SC2-180/120/60 (the last family) looked unusual at first glance — <c>TMmsstv::LineSC2180</c>
/// writes frequencies like <c>ColorToFreq(r)+0x1000</c> — but turned out to be plain
/// <see cref="ColorEncoding.RgbSequential"/> after tracing <c>CSSTVMOD::Do</c>: the upper nibble is
/// a separate, optional per-channel TX-gain tag masked off before use as frequency, not part of
/// the waveform. See <see cref="CreateSc2Mode"/>'s doc comment for the full trace.
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
    //
    // Second bug, same lesson, found by an independent Opus-driven verification pass over every
    // family's TX/RX against source: legacy emits an extra 9ms/1200Hz pulse right after the VIS
    // header for Scottie only (Main.cpp:7576-7578, `else if ((TxMode==smSCT1)||(TxMode==smSCT2)||
    // (TxMode==smSCTDX)) mp->Write(1200, 9.0);`), because legacy's RX VIS-decode state machine
    // (sstv.cpp:2127-2153) expects 1200Hz still present ~30ms after the last VIS bit, and Scottie's
    // own line generator starts directly on a 1500Hz separator with no sync of its own. This port's
    // header generation now emits that pulse (AnalogFmSstvEncoder, via
    // VisHeader.ScottiePostVisPulseFrequencyHz/DurationMs) and the decoder skips it
    // (AnalogFmSstvDecoder.TryDecodeVisHeader). The line-segment structure itself, above, was
    // already correct -- this was a header-only gap, invisible to this port's own round-trip tests
    // since encoder and decoder now agree on both sides of it, but would have made this port's
    // Scottie transmissions unrecognizable to a real legacy receiver before this fix.
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

    // R24: read directly from TMmsstv::LineR24 (Main.cpp:6535-6555) -- same Y/R-Y/B-Y shape as
    // Robot 72 (fits YCbCrSequential as-is; the RX per-pixel decode switch, Main.cpp:4315-4363,
    // confirms this by grouping smR24 with smR72/smMR*/smML* under one shared decode branch), just
    // different timing values: sync=6ms, porch=2ms, Y=92ms, R-Y marker=3ms/porch=1ms/R-Y=46ms, B-Y
    // marker=3ms/porch=1ms/B-Y=46ms -- sums to exactly 200.0ms, matching GetTiming (sstv.cpp:1257).
    // VIS code: legacy's switch matches the *full* received byte (7 data bits + even parity bit as
    // MSB), case 0x84 (sstv.cpp) -- this port's VisCode field, like every other mode's here
    // (e.g. Robot36's 8 from legacy's 0x88), stores only the 7 data bits actually carried by
    // VisHeader.GenerateSegments/DecodeVisCode, i.e. 0x84 & 0x7F = 0x04 = 4, not 0x84 itself.
    //
    // ImageHeight: modeled here as 120, not 240 -- correction after initially misreading the TX
    // loop (see RM8/RM12's CreateMonoAveragedMode doc comment, which caught this): LineR24 itself
    // does `mp->m_wLine++` at its own end (Main.cpp:6555), *in addition to* the outer SendSSTV
    // dispatch loop's own unconditional `mp->m_wLine++` after every mode's switch-case
    // (Main.cpp, right after the `switch(SSTVSET.m_TxMode)` block). So each outer-loop iteration
    // advances m_wLine by 2, not 1 -- meaning the loop (bounded by m_TL=hp=240, GetPictureSize)
    // only ever runs 120 times, and LineR24 reads pixels at m_wLine's value *before* its own
    // increment, i.e. only the even source rows (0, 2, 4, ..., 238) are ever read; odd rows are
    // never touched. This matches CSSTVSET::SetSampFreq's decode-side m_L=120 exactly (sstv.cpp:655)
    // and the RX row-address computation (Main.cpp:4160-4168, `R=y*2`, gated on `y < m_L`), which
    // duplicates each decoded (even-row-sourced) value into two consecutive output rows to fill
    // the 240-row display canvas. Net effect: R24 is a genuine reduced-vertical-resolution mode
    // (120 real, distinct rows, nearest-neighbor row-doubled for display), not a
    // redundant-transmission/wasted-airtime quirk as an earlier draft of this comment incorrectly
    // described. This port models the 120 real rows directly; the display-side row-doubling is a
    // presentation-layer detail with no round-trip-testable effect, not part of the DSP shape.
    public static readonly SstvModeDefinition R24 = new(
        Id: "r24",
        DisplayName: "R24",
        VisCode: 4,
        ImageWidth: 320,
        ImageHeight: 120,
        ColorEncoding: ColorEncoding.YCbCrSequential,
        LineSegments:
        [
            new SyncSegment(DurationMs: 6.0, FrequencyHz: 1200),
            new SyncSegment(DurationMs: 2.0, FrequencyHz: 1500), // porch
            new ScanSegment(ChannelName: "Y", DurationMs: 92.0),
            new SyncSegment(DurationMs: 3.0, FrequencyHz: 1500), // R-Y marker
            new SyncSegment(DurationMs: 1.0, FrequencyHz: 1900), // porch
            new ScanSegment(ChannelName: "RY", DurationMs: 46.0),
            new SyncSegment(DurationMs: 3.0, FrequencyHz: 2300), // B-Y marker
            new SyncSegment(DurationMs: 1.0, FrequencyHz: 1900), // porch
            new ScanSegment(ChannelName: "BY", DurationMs: 46.0),
        ]);

    // AVT: read directly from TMmsstv::LineAVT (Main.cpp) — no sync/porch at all, just R,G,B
    // scanned back to back. Simplest *line* shape found so far -- but its header is the most
    // elaborate of any mode, and was missing entirely until an independent Opus-driven verification
    // pass over every family's TX/RX against source caught it: legacy sends the VIS block 3 times
    // for AVT specifically (Main.cpp:7429, `int e = (TxMode == smAVT) ? 3 : 1;`), then appends a
    // ~5.3 second sync/AFC training sequence (Main.cpp:7563-7575: 32 blocks of a 1900Hz marker plus
    // 16 data bits at 1600/2200Hz, encoding a shifting counter seeded at 0x5fa0) before any line
    // data starts -- legacy's own RX explicitly budgets for exactly this preamble length
    // (sstv.cpp:2140: 9 + 910 + 910 + 5311.9424 + 0.30514375). Ported as
    // VisHeader.GenerateAvtSegments; the decoder identifies the mode from the first VIS repeat and
    // skips the rest (VisHeader.AvtExtraHeaderDurationMs, in AnalogFmSstvDecoder.TryDecodeVisHeader).
    // Before this fix, this port's AVT transmissions had no preamble at all beyond a single normal
    // VIS header -- invisible to round-trip tests (both sides agreed) but would have made this
    // port's AVT unrecognizable to a real legacy receiver.
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

    // MN family: read from TMmsstv::LineMN (Main.cpp:6803-6825) -- same Y1-RY-BY-Y2 line-paired
    // shape as MP (fits YCbCrLinePaired as-is), but the "narrow" frequency range (NARROW_SYNC via
    // ColorToFreqNarrow, sstv.h:440-442 -- sync=1900Hz, low=2044Hz, high=2300Hz) instead of the
    // normal 1200Hz-sync/1500-2300Hz scheme. Confirmed by direct source search -- not inferred --
    // that MN has no standard or extended VIS code anywhere in sstv.cpp's VIS-decode switch
    // (checked both stages, the ones already used for RM8/R36/etc. and for MR/MP/ML). Identified
    // instead by a small fixed FSK packet sent in place of a VIS header; see
    // SstvModeDefinition.NarrowModeCode's doc comment and VisHeader.GenerateNarrowModeSegments for
    // the full mechanism, ported from Main.cpp:7395-7424. Scan durations (140.0/212.0/270.0)
    // cross-checked against both CSSTVSET::GetTiming (sstv.cpp:1263-1268: 570.0/858.0/1090.0 minus
    // the fixed 10ms sync+porch, divided by 4 equal-length passes) and SetSampFreq's m_KS
    // (sstv.cpp:1036-1061), which agree exactly.
    //
    // FIXED (see spec/14-roadmap.md): YCbCrLinePairedScanlineEncoder/Decoder used to hardcode
    // 1500/2300Hz instead of reading LuminanceMinHz/MaxHz, so MN's pixel data decoded with the
    // wrong frequency mapping (wrong image colors) even though the narrow-mode-announce packet
    // (header/mode detection) below was already correct. Per the user-directed sequencing (all
    // modes first, then this bug), that hardcoding was left in place until every mode above was
    // added, then fixed across all three affected families (YCbCrLinePaired, YCbCrSequential,
    // Robot) in one pass. MN now has a full passing pixel round-trip in SstvRoundTripTests.
    private static SstvModeDefinition CreateMnFamilyMode(string id, string displayName, int narrowModeCode, int transmissionUnits, double scanDurationMs) => new(
        Id: id,
        DisplayName: displayName,
        VisCode: 0,
        NarrowModeCode: narrowModeCode,
        ImageWidth: 320,
        ImageHeight: transmissionUnits * 2,
        ColorEncoding: ColorEncoding.YCbCrLinePaired,
        LuminanceMinHz: 2044,
        LuminanceMaxHz: 2300,
        LineSegments:
        [
            new SyncSegment(DurationMs: 9.0, FrequencyHz: 1900),
            new SyncSegment(DurationMs: 1.0, FrequencyHz: 2044), // porch
            new ScanSegment(ChannelName: "Y1", DurationMs: scanDurationMs),
            new ScanSegment(ChannelName: "RY", DurationMs: scanDurationMs),
            new ScanSegment(ChannelName: "BY", DurationMs: scanDurationMs),
            new ScanSegment(ChannelName: "Y2", DurationMs: scanDurationMs),
        ]);

    public static readonly SstvModeDefinition Mn73 = CreateMnFamilyMode("mn73", "MN73", 0x02, 128, 140.0);
    public static readonly SstvModeDefinition Mn110 = CreateMnFamilyMode("mn110", "MN110", 0x04, 128, 212.0);
    public static readonly SstvModeDefinition Mn140 = CreateMnFamilyMode("mn140", "MN140", 0x05, 128, 270.0);

    // MC family: read from TMmsstv::LineMC (Main.cpp:6827-6845) -- plain sequential R/G/B, the
    // same shape as Scottie/Martin (fits RgbSequential as-is). Unlike MN, MC's round-trip is NOT
    // blocked by the LuminanceMinHz/MaxHz hardcoding bug -- RgbSequentialScanlineEncoder/Decoder
    // already reads those fields correctly (existing code, not a new fix; see MR/ML/MP's own
    // comment for the same observation). Same "narrow" range and narrow-mode-announce mechanism as
    // MN -- see CreateMnFamilyMode's doc comment. Scan durations (140.0/180.0/232.0) cross-checked
    // against GetTiming (sstv.cpp:1269-1273: 428.5/548.5/704.5 minus the fixed 8.5ms sync+porch,
    // divided by 3 equal-length passes) and SetSampFreq's m_KS (sstv.cpp:1066-1086), which agree
    // exactly.
    private static SstvModeDefinition CreateMcFamilyMode(string id, string displayName, int narrowModeCode, double scanDurationMs) => new(
        Id: id,
        DisplayName: displayName,
        VisCode: 0,
        NarrowModeCode: narrowModeCode,
        ImageWidth: 320,
        ImageHeight: 256,
        ColorEncoding: ColorEncoding.RgbSequential,
        LuminanceMinHz: 2044,
        LuminanceMaxHz: 2300,
        LineSegments:
        [
            new SyncSegment(DurationMs: 8.0, FrequencyHz: 1900),
            new SyncSegment(DurationMs: 0.5, FrequencyHz: 2044), // porch
            new ScanSegment(ChannelName: "R", DurationMs: scanDurationMs),
            new ScanSegment(ChannelName: "G", DurationMs: scanDurationMs),
            new ScanSegment(ChannelName: "B", DurationMs: scanDurationMs),
        ]);

    public static readonly SstvModeDefinition Mc110 = CreateMcFamilyMode("mc110", "MC110", 0x14, 140.0);
    public static readonly SstvModeDefinition Mc140 = CreateMcFamilyMode("mc140", "MC140", 0x15, 180.0);
    public static readonly SstvModeDefinition Mc180 = CreateMcFamilyMode("mc180", "MC180", 0x16, 232.0);

    // RM8/RM12: read from TMmsstv::LineRM (Main.cpp:6785-6800, called as LineRM(mp,6.0,58.89709)
    // for RM8 and LineRM(mp,6.0,92.0) for RM12) -- a genuinely new shape, not a variant of anything
    // implemented so far: monochrome only (GetRY's RY/BY outputs are computed but discarded, never
    // transmitted), and each transmission averages TWO consecutive source rows' luminance into a
    // single scanned value (`YY = (Y[x] + Y2[x]) / 2`) -- unlike YCbCrLinePaired's two INDEPENDENT
    // luma scans sharing one chroma pair. Confirmed via the RX per-pixel decode switch (Main.cpp,
    // case smRM8/smRM12, a block distinct from smR24/R72/MR/ML's), which decodes exactly one
    // channel and writes that single value into both of the two output rows it addresses (the same
    // R=y*2/R+1 row-doubling mechanism as R24, but here genuinely averaging two distinct source
    // rows rather than R24's single-row-per-transmission read -- confirmed by re-tracing LineRM's
    // own internal `mp->m_wLine++` plus the outer TX dispatch loop's own increment, the exact
    // double-increment structure an earlier pass over R24 initially misread; see R24's doc comment).
    //
    // Sync=6ms@1200Hz, porch=(ts/3.0)=2ms@1500Hz, single Y scan. Total: RM8 6+2+58.89709=66.89709ms,
    // RM12 6+2+92.0=100.0ms -- both match GetTiming (sstv.cpp) exactly.
    //
    // VIS codes: RM8's legacy switch case is 0x82, RM12's is 0x86 -- both, like R24's 0x84, bake
    // a parity-looking bit into their MSB (see R24's doc comment for why this port strips it):
    // 0x82 & 0x7F = 2, 0x86 & 0x7F = 6. CORRECTION (independent Opus-driven verification pass):
    // 0x82's MSB really is even parity (low 7 bits = 0000010, one set bit, needs parity=1 for an
    // even total -- matches). 0x86's is NOT: low 7 bits = 0000110, two set bits (already even),
    // so a correctly-computed even-parity bit would be 0, giving 0x06 -- but legacy's real assigned
    // byte is 0x86 (bit7=1). Checked every other normal VIS byte in the legacy table bit-by-bit;
    // all of them do follow even parity, so this is a one-off quirk in legacy's own RM12 byte, not
    // a bug in this port's original (correct) parity-stripping logic. Since legacy's VIS-decode
    // switch (sstv.cpp:1993-2074) matches the literal received byte, this port's header generator
    // now transmits RM12's forced byte 0x86 (not a computed-parity 0x06) via
    // VisHeader.GenerateSegments's forcedParityBit parameter / VisHeader.Rm12ForcedParityBit --
    // otherwise this port's RM12 TX would have been unrecognizable to a real legacy receiver. RX is
    // unaffected either way: VisHeader.DecodeVisCode only ever reads the 7 data bits.
    //
    // NOT ported: legacy's RX applies an RM-specific gain correction (`d *= 256.0/(256.0-32.0)`,
    // Main.cpp) on top of its own GetPictureLevel/GetPixelLevel calibration pipeline before writing
    // the pixel. This port's decoder doesn't replicate that calibration pipeline for *any* mode
    // (see AnalogFmSstvDecoder's parity notes; every mode here uses a simpler direct
    // frequency-averaging inverse instead) -- the correction is meaningful only relative to that
    // specific pipeline's own internal scale, so applying it inside this port's linear
    // frequency-to-pixel mapping would be a mismatched, uninterpretable value, not a faithful port.
    // Flagged rather than silently dropped or silently guessed at.
    private static SstvModeDefinition CreateMonoAveragedMode(string id, string displayName, int visCode, int transmissionUnits, double scanDurationMs) => new(
        Id: id,
        DisplayName: displayName,
        VisCode: visCode,
        ImageWidth: 320,
        ImageHeight: transmissionUnits * 2,
        ColorEncoding: ColorEncoding.MonoAveragedPaired,
        LineSegments:
        [
            new SyncSegment(DurationMs: 6.0, FrequencyHz: 1200),
            new SyncSegment(DurationMs: 2.0, FrequencyHz: 1500), // porch
            new ScanSegment(ChannelName: "Y", DurationMs: scanDurationMs),
        ]);

    public static readonly SstvModeDefinition Rm8 = CreateMonoAveragedMode("rm8", "RM8", 2, 120, 58.89709);
    public static readonly SstvModeDefinition Rm12 = CreateMonoAveragedMode("rm12", "RM12", 6, 120, 92.0);

    // SC2-180/120/60: read from TMmsstv::LineSC2180 (Main.cpp:6665-6683, called as
    // LineSC2180(mp, 5.5437, 235.0) / (mp, 5.52248, 156.5) / (mp, 5.5006, 78.128) respectively) --
    // despite the unusual-looking source (`ColorToFreq(cp->b.r)+0x1000`, `+0x2000` for G,
    // `+0x3000` for B), this is plain sequential R,G,B, the same shape as Martin/Scottie/AVT/R24 --
    // NOT a fourth codec family. Verified by reading CSSTVMOD::Do (sstv.cpp:2868): the actual VCO
    // frequency is `(f & 0x0fff) - 1100` -- the upper nibble (0x1000/0x2000/0x3000) is masked off
    // entirely before being used as frequency. That upper nibble is a *separate* per-channel
    // TX-gain tag (sstv.cpp:2880: `switch(f & 0xf000){ case 0x1000: d *= m_outgainR; ...}`), only
    // consulted when `m_VariOut` (an optional independent-R/G/B-gain TX calibration feature) is
    // enabled -- it has zero effect on the transmitted frequency/waveform content, and this port
    // doesn't model per-channel TX gain trim for any mode, so it's correctly omitted here, not
    // silently lost: the actual audio is unaffected either way. RX confirms the same plain-RGB
    // shape: SC2-180/120/60 aren't named in the RX per-pixel decode switch's specific-mode cases
    // (Main.cpp:4200-4452, covering Scottie/Robot/MR/ML/PD/MP/MN/RM8/RM12) -- they fall through to
    // `default:` (Main.cpp:4454-4502), the same generic 3-phase branch other unlisted plain-RGB
    // modes use, and (since they aren't smMRT1/smMRT2, the one exception that branch special-cases
    // for a rotated channel order) its non-MRT write order is phase1->R, phase2->G, phase3->B --
    // matching TX's scan order exactly, sync-first, no separator markers between channels beyond
    // the fixed porch.
    //
    // Sync=S ms@1200Hz, porch=0.5ms@1500Hz (the +0x1000 tag on this Write call is likewise just a
    // gain tag, not part of the frequency), then R/G/B each tw ms. Totals: SC2-180
    // 5.5437+0.5+3*235.0=711.0437ms, SC2-120 5.52248+0.5+3*156.5=475.52248ms, SC2-60
    // 5.5006+0.5+3*78.128=240.3846ms -- all three match GetTiming (sstv.cpp) exactly.
    //
    // VIS codes, parity-stripped per R24's doc comment's convention (and this time legacy's own
    // comments spell out the stripped value directly, confirming the convention): case 0xb7 "// $37
    // 00110111" -> 55, case 0x3f "// $3f 00111111" -> 63 (already <128, no parity bit set), case
    // 0xbb "// $3b 10111011" -> 59.
    //
    // GetBitmapSize doesn't special-case this family -> default 320x256, and GetPictureSize's
    // hp-override list (RM8/RM12/R24/R36/R72/AVT) doesn't include it either -> hp=h=256, no
    // row-doubling quirk like R24's.
    private static SstvModeDefinition CreateSc2Mode(string id, string displayName, int visCode, double syncMs, double scanDurationMs) => new(
        Id: id,
        DisplayName: displayName,
        VisCode: visCode,
        ImageWidth: 320,
        ImageHeight: 256,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments:
        [
            new SyncSegment(DurationMs: syncMs, FrequencyHz: 1200),
            new SyncSegment(DurationMs: 0.5, FrequencyHz: 1500), // porch
            new ScanSegment(ChannelName: "R", DurationMs: scanDurationMs),
            new ScanSegment(ChannelName: "G", DurationMs: scanDurationMs),
            new ScanSegment(ChannelName: "B", DurationMs: scanDurationMs),
        ]);

    public static readonly SstvModeDefinition Sc2180 = CreateSc2Mode("sc2-180", "SC2-180", 55, 5.5437, 235.0);
    public static readonly SstvModeDefinition Sc2120 = CreateSc2Mode("sc2-120", "SC2-120", 63, 5.52248, 156.5);
    public static readonly SstvModeDefinition Sc260 = CreateSc2Mode("sc2-60", "SC2-60", 59, 5.5006, 78.128);

    public static readonly IReadOnlyList<SstvModeDefinition> All =
    [
        MartinM1, MartinM2, ScottieS1, ScottieS2, ScottieDx, Robot36, Robot72, Avt, R24,
        Mr73, Mr90, Mr115, Mr140, Mr175, Ml180, Ml240, Ml280, Ml320,
        Mp73, Mp115, Mp140, Mp175,
        Pd50, Pd90, Pd120, Pd160, Pd180, Pd240, Pd290,
        P3, P5, P7,
        Mn73, Mn110, Mn140, Mc110, Mc140, Mc180,
        Rm8, Rm12,
        Sc2180, Sc2120, Sc260,
    ];

    public static SstvModeDefinition? FindByVisCode(int visCode) =>
        All.FirstOrDefault(m => m.ExtendedVisCode is null && m.NarrowModeCode is null && m.VisCode == visCode);

    public static SstvModeDefinition? FindByExtendedCode(int extendedCode) =>
        All.FirstOrDefault(m => m.ExtendedVisCode == extendedCode);

    /// <summary>Matches the *full* legacy VIS byte -- 7 data bits plus the even-parity bit as bit 7
    /// (e.g. R36's real case is <c>0x88</c>, not the parity-stripped <c>8</c> <see cref="FindByVisCode"/>
    /// matches) -- the way legacy's own real-time bit-decode actually compares it
    /// (`sstv.cpp:1993-2074`'s `switch(m_VisData)`, a `default:` rejects any byte with the wrong
    /// parity bit outright). Reuses <see cref="VisHeader.GenerateSegments"/>'s own parity computation
    /// (including <see cref="VisHeader.Rm12ForcedParityBit"/> for RM12's real anomalous byte) as the
    /// single source of truth, rather than re-transcribing a second copy of the mode-code table --
    /// verified to reproduce every one of the 24 real legacy bytes at `sstv.cpp:1993-2074` byte-for-byte.
    /// Only used by <see cref="VisLockStateMachine"/>, which (unlike <see cref="FindByVisCode"/>'s
    /// windowed-average-frequency callers) actually reads the parity bit as part of its own byte
    /// accumulation.</summary>
    internal static SstvModeDefinition? FindByFullVisByte(int fullByte)
    {
        foreach (var mode in All)
        {
            if (mode.ExtendedVisCode is not null || mode.NarrowModeCode is not null)
            {
                continue;
            }

            var parity = 0;
            for (var bitIndex = 0; bitIndex < 7; bitIndex++)
            {
                parity ^= (mode.VisCode >> bitIndex) & 1;
            }

            var parityBit = mode == Rm12 ? VisHeader.Rm12ForcedParityBit : parity;
            var expectedByte = mode.VisCode | (parityBit << 7);

            if (expectedByte == fullByte)
            {
                return mode;
            }
        }

        return null;
    }

    public static SstvModeDefinition? FindByNarrowCode(int narrowCode) =>
        All.FirstOrDefault(m => m.NarrowModeCode == narrowCode);

    /// <summary>Scottie S1/S2/DX share a legacy header quirk (<c>Main.cpp:7576-7578</c>'s extra
    /// post-VIS pulse) that neither the general VIS mechanism nor any other family needs — see
    /// <see cref="VisHeader.ScottiePostVisPulseFrequencyHz"/>'s doc comment. Shared by
    /// <see cref="AnalogFmSstvEncoder"/> and <see cref="AnalogFmSstvDecoder"/> so both sides agree
    /// on exactly which modes this applies to.</summary>
    internal static bool IsScottieFamily(SstvModeDefinition mode) =>
        mode == ScottieS1 || mode == ScottieS2 || mode == ScottieDx;

    /// <summary>AFC's begin/width timing (<c>CSSTVSET::SetSampFreq</c>, `sstv.cpp:1162-1177`) uses a
    /// faster 1.0ms/2.0ms pair for exactly Martin M1/M2, the SC2 family, and MC110/140/180 — every
    /// other mode (including MN, Robot, MR/ML/MP/PD/Pasokon/R24/RM) uses the default 1.5ms/3.0ms
    /// pair. See <see cref="AfcTracker"/>.</summary>
    internal static bool IsFastAfcGroup(SstvModeDefinition mode) =>
        mode == MartinM1 || mode == MartinM2
        || mode == Sc2180 || mode == Sc2120 || mode == Sc260
        || mode == Mc110 || mode == Mc140 || mode == Mc180;

    /// <summary>Auto Slant's <c>m_ASPos[0..3]</c> (`Main.cpp:3801-3857`, <c>InitAutoStop</c>) --
    /// line-count thresholds at which progressively smaller drift becomes trustworthy enough to act
    /// on. Default is [64,128,160,ImageHeight-36]; several mode groups override some or all of it.
    /// See <see cref="SlantTracker"/>.</summary>
    internal static int[] GetAutoSlantThresholdPositions(SstvModeDefinition mode)
    {
        if (mode == Pd50 || mode == Pd90 || mode == Mp73 || mode == Mp115 || mode == Mp140 || mode == Mp175
            || mode == R24 || mode == Rm8 || mode == Rm12 || mode == Mn73 || mode == Mn110 || mode == Mn140)
        {
            return [48, 64, 72, 110];
        }

        if (mode == Pd160)
        {
            return [48, 80, 126, 160];
        }

        if (mode == Pd290)
        {
            return [64, 128, 160, 240];
        }

        if (mode == P3)
        {
            return [64, 200, 360, 496 - 48];
        }

        if (mode == P5)
        {
            return [64, 200, 300, 380];
        }

        if (mode == P7)
        {
            return [64, 128, 220, 280];
        }

        return [64, 128, 160, mode.ImageHeight - 36];
    }

    /// <summary>Where this mode's sync pulse starts within the line, in ms — legacy's <c>m_OFP</c>
    /// (`sstv.cpp`'s `CSSTVSET::SetSampFreq`, dozens of per-mode literals like
    /// <c>m_OFP = 10.7 * m_SampFreq / 1000.0</c>). Rather than re-transcribing that whole separate
    /// per-mode table, this is derived from the same <c>LineSegments</c> data already verified
    /// against the real TX line-generator functions (see this registry's own class doc comment) --
    /// the first <see cref="SyncSegment"/> whose frequency matches the mode's sync tone (1200Hz
    /// normal, 1900Hz `NARROW_SYNC` for MN/MC). This is a deliberate substitution, not a
    /// re-derivation of legacy's literal constant: <see cref="SlantTracker"/> only ever uses this
    /// value as a fixed reference point for wrap-to-nearest-zero centering of the measured drift, so
    /// any consistent reference point works -- it's not meant to reproduce m_OFP's exact number.
    /// Not meaningful for AVT (no sync segment at all) or Scottie, whose sync is mid-line, not at
    /// line start -- both handled correctly by finding the segment wherever it actually is, per
    /// LineSCT's real structure (see the Scottie doc comment above).</summary>
    internal static double GetSyncSegmentOffsetMs(SstvModeDefinition mode)
    {
        var expectedSyncHz = mode.NarrowModeCode is not null ? 1900.0 : 1200.0;
        var offsetMs = 0.0;

        foreach (var segment in mode.LineSegments)
        {
            if (segment is SyncSegment sync && Math.Abs(sync.FrequencyHz - expectedSyncHz) < 0.01)
            {
                return offsetMs;
            }

            offsetMs += segment.DurationMs;
        }

        throw new InvalidOperationException($"Mode '{mode.Id}' has no sync segment at {expectedSyncHz}Hz.");
    }

    /// <summary>The midpoint (not start) of this mode's sync segment, in ms from line start --
    /// <see cref="GetSyncSegmentOffsetMs"/> plus half that same segment's own duration. Used only by
    /// the sync-interval-bypass mode detector (<c>m_sint2</c>, see
    /// <c>AnalogFmSstvDecoder.TrySyncIntervalDetectionStep</c>) to turn a detected sync-envelope peak
    /// position back into a line-start anchor: <see cref="SyncEnvelopeDetector"/>'s peak naturally
    /// lands near the temporal center of the (constant-tone) sync segment, not its leading edge, so
    /// anchoring off the start alone would offset every subsequent line by roughly half a sync pulse.
    /// This is a documented approximation, not a ported legacy mechanism: legacy's own real-time
    /// bootstrap (<c>CSSTVDEM::Start</c>'s <c>m_wBgn</c> staged buffer-alignment sequence,
    /// `sstv.cpp:1717-1744`) is a separate, deeper piece of machinery this port doesn't have --
    /// unlike the VIS-header path (whose fixed, known-duration header gives an exact deterministic
    /// anchor), sync-bypass detection only ever has an observed peak to work from.</summary>
    internal static double GetSyncSegmentMidpointOffsetMs(SstvModeDefinition mode)
    {
        var expectedSyncHz = mode.NarrowModeCode is not null ? 1900.0 : 1200.0;
        var offsetMs = 0.0;

        foreach (var segment in mode.LineSegments)
        {
            if (segment is SyncSegment sync && Math.Abs(sync.FrequencyHz - expectedSyncHz) < 0.01)
            {
                return offsetMs + sync.DurationMs / 2.0;
            }

            offsetMs += segment.DurationMs;
        }

        throw new InvalidOperationException($"Mode '{mode.Id}' has no sync segment at {expectedSyncHz}Hz.");
    }

    /// <summary>Legacy's per-mode expected sync-repeat interval, <c>SSTVSET.m_MS[i] = GetTiming(i) *
    /// m_SampFreq / 1000.0</c> (`sstv.cpp:577`) — the mode's own line duration, since (for every
    /// mode except AVT) the sync tone repeats once per transmission line. Derived from this port's
    /// own <c>LineDurationMs</c> (already cross-checked against <c>GetTiming</c> in
    /// <c>SstvRoundTripTests.LineDuration_MatchesLegacyGetTiming</c>) rather than re-transcribing a
    /// second, separate per-mode table. AVT is excluded (<c>m_MS[smAVT] = 0</c>, `sstv.cpp:579`,
    /// explicitly zeroed after the loop) — matches its exclusion from AFC and Auto Slant too.</summary>
    internal static IReadOnlyList<(SstvModeDefinition Mode, double ExpectedIntervalSamples)> GetSyncIntervalCandidates(double sampleRate) =>
        All.Where(m => m != Avt).Select(m => (m, m.LineDurationMs / 1000.0 * sampleRate)).ToList();

    /// <summary><c>SyncCheckSub</c>'s per-mode-group required-match depth (`sstv.cpp:1290-1325`) —
    /// how many *additional* consecutive prior intervals must also match before a candidate is
    /// trusted. Returns null for modes this mechanism never matches at all: SC2-120/60
    /// (`sstv.cpp:1296-1298`, unconditionally excluded) or modes gated to the wrong narrow/normal
    /// band (`if (m_fNarrow) return 0` / `if (!m_fNarrow) return 0`).</summary>
    internal static int? GetSyncIntervalMatchDepth(SstvModeDefinition mode, bool isNarrow)
    {
        if (mode == Sc260 || mode == Sc2120)
        {
            return null;
        }

        if (mode == R24 || mode == Robot36 || mode == MartinM2 || mode == Pd50 || mode == Pd240)
        {
            return isNarrow ? null : 8 - 4; // MSYNCLINE - 4
        }

        if (mode == Rm8 || mode == Rm12)
        {
            return isNarrow ? null : 0;
        }

        if (mode == Mn73 || mode == Mn110 || mode == Mn140 || mode == Mc110 || mode == Mc140 || mode == Mc180)
        {
            return isNarrow ? 8 - 5 : null; // MSYNCLINE - 5
        }

        return isNarrow ? null : 8 - 3; // MSYNCLINE - 3, default group
    }
}
