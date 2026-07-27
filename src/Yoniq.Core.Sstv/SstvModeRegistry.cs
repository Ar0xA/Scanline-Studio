using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>
/// Built-in mode table. See the parity caveat on <see cref="SstvModeDefinition"/> — these constants
/// are standards-informed, not yet golden-vector-validated against the legacy binary.
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

    public static readonly IReadOnlyList<SstvModeDefinition> All = [MartinM1];

    public static SstvModeDefinition? FindByVisCode(int visCode) => All.FirstOrDefault(m => m.VisCode == visCode);
}
