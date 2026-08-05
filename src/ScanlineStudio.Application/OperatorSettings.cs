namespace ScanlineStudio.Application;

/// <summary>The operator's own identity -- currently just the callsign. Lives in
/// <c>ScanlineStudio.Application</c> rather than a <c>ScanlineStudio.Core.*</c> project since it
/// doesn't belong to any single hardware/DSP domain; this is the orchestration layer future
/// TX-overlay-macro and logbook features will read it from.</summary>
public sealed record OperatorSettings
{
    public const string SectionKey = "Operator";

    public string? Callsign { get; init; }
}
