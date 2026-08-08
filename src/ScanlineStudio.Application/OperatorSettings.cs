namespace ScanlineStudio.Application;

/// <summary>The operator's own identity. Lives in <c>ScanlineStudio.Application</c> rather than a
/// <c>ScanlineStudio.Core.*</c> project since it doesn't belong to any single hardware/DSP domain;
/// this is the orchestration layer TX-overlay-macro and logbook features read it from --
/// <see cref="IMacroTextResolver"/> is the first consumer of <see cref="Name"/>/<see cref="Grid"/>.
/// <see cref="Name"/>/<see cref="Grid"/> have no legacy MMSSTV equivalent (legacy's own macro
/// engine, <c>MacroText</c>, has no "my name"/"my QTH" token at all -- only <c>%m</c> for callsign)
/// -- added deliberately, matching mock2's own "MY NAME"/"MY GRID" insert-field chips
/// (`TxImageEditorPaneView.axaml`) and real-world SSTV overlay conventions other software already
/// supports.</summary>
public sealed record OperatorSettings
{
    public const string SectionKey = "Operator";

    public string? Callsign { get; init; }

    public string? Name { get; init; }

    /// <summary>Maidenhead grid square (or any free-text location the operator prefers) --
    /// deliberately loose/unvalidated, matching <see cref="Callsign"/>'s own untyped-string
    /// precedent; nothing in this pass computes distance/bearing from it.</summary>
    public string? Grid { get; init; }
}
