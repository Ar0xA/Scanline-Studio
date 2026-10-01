namespace ScanlineStudio.UI.Views;

/// <summary>Host-registered facts about the windowing backend that change how the main window persists geometry.
/// Absent from DI means "a backend that can place windows" (Win32, macOS, X11).</summary>
/// <param name="SupportsWindowPosition">False under native Wayland: a client can neither read nor set its own
/// screen position there, so a saved position would be a fake (0,0) that the next X11 launch would restore.</param>
public sealed record WindowingBackendInfo(bool SupportsWindowPosition);
