using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>
/// Built-in mode table. Unlike the original Martin M1 entry (standards-informed, not yet
/// cross-checked), these values are read directly from legacy source: per-mode timing constants
/// from <c>CSSTVSET::SetSampFreq</c>'s per-mode switch, channel order from <c>Main.cpp</c>'s RX
/// decode switch (`case smSCT1/SCT2/SCTDX` for Scottie's R,G,B order; the `default:` branch's
/// explicit `if (mode == smMRT1 || smMRT2)` check for Martin's G,B,R order), and VIS codes from the
/// VIS-to-mode lookup switch (`sstv.cpp` around line 1993). Each mode's <c>LineDurationMs</c> is
/// independently cross-checked against <c>CSSTVSET::GetTiming</c>'s return value in
/// <c>SstvRoundTripTests.LineDuration_MatchesLegacyGetTiming</c> — not just self-consistent, matched
/// against a legacy-source constant this codebase doesn't otherwise compute from.
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

    // Scottie family: R,G,B order (not G,B,R like Martin) confirmed directly from Main.cpp's RX
    // decode switch (`case smSCT1/SCT2/SCTDX` writes fresh-decoded R in the branch labeled first,
    // caches G/B from the second/third branches) — cross-checked against CSSTVSET::GetTiming's
    // total line duration for all three variants (see SstvModeRegistryTests). No trailing
    // separator after B, unlike Martin — also confirmed via the GetTiming cross-check (OF+CB sums
    // exactly to GetTiming with no remainder, vs. Martin's 0.572ms remainder).
    public static readonly SstvModeDefinition ScottieS1 = new(
        Id: "scottie-s1",
        DisplayName: "Scottie S1",
        VisCode: 60,
        ImageWidth: 320,
        ImageHeight: 256,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments:
        [
            new SyncSegment(DurationMs: 10.5, FrequencyHz: 1200),
            new ScanSegment(ChannelName: "R", DurationMs: 138.24),
            new SyncSegment(DurationMs: 1.5, FrequencyHz: 1500), // separator
            new ScanSegment(ChannelName: "G", DurationMs: 138.24),
            new SyncSegment(DurationMs: 1.5, FrequencyHz: 1500), // separator
            new ScanSegment(ChannelName: "B", DurationMs: 138.24),
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
            new SyncSegment(DurationMs: 10.5, FrequencyHz: 1200),
            new ScanSegment(ChannelName: "R", DurationMs: 88.064),
            new SyncSegment(DurationMs: 1.5, FrequencyHz: 1500), // separator
            new ScanSegment(ChannelName: "G", DurationMs: 88.064),
            new SyncSegment(DurationMs: 1.5, FrequencyHz: 1500), // separator
            new ScanSegment(ChannelName: "B", DurationMs: 88.064),
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
            new SyncSegment(DurationMs: 10.5, FrequencyHz: 1200),
            new ScanSegment(ChannelName: "R", DurationMs: 345.6),
            new SyncSegment(DurationMs: 1.5, FrequencyHz: 1500), // separator
            new ScanSegment(ChannelName: "G", DurationMs: 345.6),
            new SyncSegment(DurationMs: 1.5, FrequencyHz: 1500), // separator
            new ScanSegment(ChannelName: "B", DurationMs: 345.6),
        ]);

    public static readonly IReadOnlyList<SstvModeDefinition> All = [MartinM1, MartinM2, ScottieS1, ScottieS2, ScottieDx];

    public static SstvModeDefinition? FindByVisCode(int visCode) => All.FirstOrDefault(m => m.VisCode == visCode);
}
