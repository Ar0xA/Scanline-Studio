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

    public static readonly IReadOnlyList<SstvModeDefinition> All = [MartinM1, MartinM2, ScottieS1, ScottieS2, ScottieDx];

    public static SstvModeDefinition? FindByVisCode(int visCode) => All.FirstOrDefault(m => m.VisCode == visCode);
}
