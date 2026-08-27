namespace ScanlineStudio.UI.Settings;

/// <summary>Pure UI-preference settings for the TX pane and its quick-controls -- no
/// hardware/DSP access, so owning this section directly in <c>ScanlineStudio.UI</c> (rather than
/// a <c>ScanlineStudio.Core.*</c> project) doesn't violate the layering rule
/// (`UiLayeringArchitectureTests`), just the "each module owns its typed section" convention
/// applied to the UI project itself for the first time.</summary>
public sealed record TxPaneUiSettings
{
    public const string SectionKey = "TxPaneUi";

    /// <summary>When true, a detected RX mode automatically becomes the selected TX mode.</summary>
    public bool AutoFollowRxMode { get; init; }

    /// <summary>Ordered <c>SstvModeDefinition.Id</c> values shown as quick-select buttons in the TX
    /// pane -- defaults to <see cref="QuickModeGridDefaults.Ids"/>. Independent of
    /// <see cref="RxPaneUiSettings.QuickModeGridIds"/> by design.</summary>
    public IReadOnlyList<string> QuickModeGridIds { get; init; } = QuickModeGridDefaults.Ids;
}
