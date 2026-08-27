namespace ScanlineStudio.UI.Settings;

/// <summary>Pure UI-preference settings for the RX pane's quick-controls -- mirrors
/// <see cref="TxPaneUiSettings"/>'s own reasoning (owning this directly in
/// <c>ScanlineStudio.UI</c> is allowed, see that type's doc comment) as a separate section/type
/// rather than a shared one, since RX and TX are kept independent by design (confirmed with the
/// user: reassigning a quick-mode-grid button on one page never touches the other page's grid).</summary>
public sealed record RxPaneUiSettings
{
    public const string SectionKey = "RxPaneUi";

    /// <summary>Ordered <c>SstvModeDefinition.Id</c> values shown as quick-select buttons in the RX
    /// pane -- defaults to <see cref="QuickModeGridDefaults.Ids"/>.</summary>
    public IReadOnlyList<string> QuickModeGridIds { get; init; } = QuickModeGridDefaults.Ids;
}
