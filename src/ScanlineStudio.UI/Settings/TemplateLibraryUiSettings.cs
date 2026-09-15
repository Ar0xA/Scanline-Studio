namespace ScanlineStudio.UI.Settings;

/// <summary>Pure UI-preference settings for the TX image editor's Template Library panel -- no
/// hardware/DSP access, same "each module owns its typed section" shape as
/// <see cref="TxPaneUiSettings"/>/<see cref="AppearanceSettings"/>. Deliberately NOT added to
/// <c>ScanlineStudio.Application.ReadyRackSettings</c> (which owns <c>PinnedTemplateIds</c>, a
/// domain concept -- what's pinned) since a view-mode toggle is a display preference, not domain
/// state.</summary>
public sealed record TemplateLibraryUiSettings
{
    public const string SectionKey = "TemplateLibraryUi";

    /// <summary>True shows the Template Library as a thumbnail grid; false (default) keeps the
    /// original compact list.</summary>
    public bool IsGridView { get; init; }
}
