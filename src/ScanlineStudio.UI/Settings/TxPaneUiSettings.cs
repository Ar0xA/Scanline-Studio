namespace ScanlineStudio.UI.Settings;

/// <summary>Pure UI-preference settings for the TX pane and its quick-controls -- no
/// hardware/DSP access, so owning this section directly in <c>ScanlineStudio.UI</c> (rather than
/// a <c>ScanlineStudio.Core.*</c> project) doesn't violate the layering rule
/// (`UiLayeringArchitectureTests`), just the "each module owns its typed section" convention
/// applied to the UI project itself for the first time.</summary>
public sealed record TxPaneUiSettings
{
    public const string SectionKey = "TxPaneUi";

    /// <summary>Ordered <c>SstvModeDefinition.Id</c> values shown as quick-select buttons in the
    /// TX pane -- empty by default (no built-in favorites assumed).</summary>
    public IReadOnlyList<string> FavoriteModeIds { get; init; } = [];

    /// <summary>When true, a detected RX mode automatically becomes the selected TX mode.</summary>
    public bool AutoFollowRxMode { get; init; }

    // TxVolumePercent deliberately does NOT live here: it needs to be read inside
    // ScanlineStudio.Application's own playback pipeline (SstvSessionService), which cannot
    // reference a ScanlineStudio.UI-owned type without inverting the dependency direction -- see
    // AudioDeviceSettings.TxVolumePercent (ScanlineStudio.Core.Audio) instead. Caught before this
    // field was ever consumed anywhere, not a breaking change to shipped behavior.
}
