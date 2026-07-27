namespace Yoniq.Abstractions.Sstv;

public enum ColorEncoding
{
    RgbSequential,
}

/// <summary>One timed segment of an SSTV scanline: either a fixed-frequency sync/porch/separator
/// pulse, or a scan of one color channel whose frequency varies with pixel data.</summary>
public abstract record LineSegment(double DurationMs);

public sealed record SyncSegment(double DurationMs, double FrequencyHz) : LineSegment(DurationMs);

public sealed record ScanSegment(string ChannelName, double DurationMs) : LineSegment(DurationMs);

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
    double LuminanceMaxHz = 2300)
{
    public double LineDurationMs => LineSegments.Sum(s => s.DurationMs);
}
