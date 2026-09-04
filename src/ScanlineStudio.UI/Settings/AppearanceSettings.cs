namespace ScanlineStudio.UI.Settings;

/// <summary>Pure UI-preference settings for the app's color theme -- no hardware/DSP access, so
/// owning this section directly in <c>ScanlineStudio.UI</c> doesn't violate the layering rule
/// (<c>UiLayeringArchitectureTests</c>), same precedent as <see cref="WindowGeometrySettings"/>.
/// New-to-the-port UX, not a legacy port -- legacy has no equivalent concept.</summary>
public sealed record AppearanceSettings
{
    public const string SectionKey = "Appearance";

    public AppTheme? Theme { get; init; }

    public AppFontScale? FontScale { get; init; }
}

/// <summary><see cref="AppTheme.System"/> maps to Avalonia's <c>ThemeVariant.Default</c> ("inherit
/// from parent; system theme is inherited when set on Application" -- confirmed against the
/// installed Avalonia 11.3.12 docs), giving OS-follow behavior for free rather than needing this
/// app to poll or subscribe to an OS theme-change notification itself.
///
/// Persisted as a plain integer (no <c>JsonStringEnumConverter</c> anywhere in this repo), same
/// convention as <c>DemodType</c>/<c>RxBpfPreset</c>/<c>RxBufferMode</c>. Never reorder/renumber --
/// doing so would silently remap every existing user's saved theme choice to a different one on
/// next load.</summary>
public enum AppTheme
{
    Light,
    Dark,
    System,
}

/// <summary>2 discrete, hand-tuned presets (Phase 2 of the Appearance feature) -- not a computed
/// scale factor, by user decision: no modular type scale exists anywhere in this design system's own
/// mockup guidance, so there is nothing to compute from.
///
/// Persisted as a plain integer (no <c>JsonStringEnumConverter</c> anywhere in this repo), same
/// convention as <see cref="AppTheme"/>/<c>DemodType</c>/<c>RxBpfPreset</c>/<c>RxBufferMode</c>.
/// Never reorder/renumber -- doing so would silently remap every existing user's saved font-scale
/// choice to a different one on next load. Named <c>AppFontScale</c> rather than the bare
/// <c>FontScale</c> its own property already uses, to avoid a type-name-equals-property-name
/// collision -- same reasoning as <see cref="AppTheme"/>'s own type/property split.</summary>
public enum AppFontScale
{
    Normal,
    Large,
}
