namespace ScanlineStudio.UI.Settings;

/// <summary>Pure UI-preference settings for the main window's remembered position/size -- no
/// hardware/DSP access, so owning this section directly in <c>ScanlineStudio.UI</c> (rather than a
/// <c>ScanlineStudio.Core.*</c> project) doesn't violate the layering rule
/// (<c>UiLayeringArchitectureTests</c>), matching <see cref="TxPaneUiSettings"/>'s own established
/// precedent for this project's UI-only settings sections.
///
/// Port of legacy's real, user-toggleable <c>sys.m_MemWindow</c> ("Remember window position and
/// size" checkbox, <c>Option.dfm</c>'s <c>MemWin</c>, <c>Option.cpp:250/616</c>) plus the geometry
/// it gates (<c>Main.cpp:1686-1692</c> load, <c>:2214-2224</c> save -- both gated on
/// <c>sys.m_MemWindow</c>; save is ALSO gated on <c>WindowState == wsNormal</c>, so geometry is
/// never captured while maximized/minimized). <see cref="RememberWindowPosition"/>'s desired
/// default when absent is <see langword="true"/> -- legacy's own ctor-default list
/// (<c>Main.cpp:890-920</c>) never assigns <c>sys.m_MemWindow</c> an explicit value, so there is no
/// confirmed "shipped fresh-install default" to port either way; this project's own UX call is to
/// remember window position/size out of the box, matching most users' actual expectation for a
/// desktop app.</summary>
public sealed record WindowGeometrySettings
{
    public const string SectionKey = "WindowGeometry";

    public bool? RememberWindowPosition { get; init; } = true;

    public double? Left { get; init; }

    public double? Top { get; init; }

    public double? Width { get; init; }

    public double? Height { get; init; }
}
