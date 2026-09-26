namespace ScanlineStudio.Host;

/// <summary>Which Avalonia windowing backend to start.</summary>
public enum WindowingBackendChoice
{
    /// <summary><c>UsePlatformDetect()</c> as-is: Win32, macOS native, or X11 (XWayland on a Wayland desktop).</summary>
    PlatformDetect,

    /// <summary>Avalonia's experimental native Wayland backend.</summary>
    Wayland,

    /// <summary><c>SCANLINE_WAYLAND=1</c> was set but there is no Wayland session; falls back to <see cref="PlatformDetect"/>.</summary>
    WaylandRequestedWithoutSession,
}

/// <summary>Opt-in native Wayland: only when <c>SCANLINE_WAYLAND=1</c> AND <c>WAYLAND_DISPLAY</c> is set, on Linux.
/// Avalonia 12.1's Wayland backend is experimental and <c>UsePlatformDetect()</c> never picks it on its own.</summary>
internal static class WindowingBackendSelector
{
    public const string OptInVariable = "SCANLINE_WAYLAND";
    public const string WaylandDisplayVariable = "WAYLAND_DISPLAY";

    public static WindowingBackendChoice Choose(bool isLinux, Func<string, string?> getEnvironmentVariable)
    {
        if (!isLinux || getEnvironmentVariable(OptInVariable) != "1")
        {
            return WindowingBackendChoice.PlatformDetect;
        }

        return string.IsNullOrEmpty(getEnvironmentVariable(WaylandDisplayVariable))
            ? WindowingBackendChoice.WaylandRequestedWithoutSession
            : WindowingBackendChoice.Wayland;
    }
}
