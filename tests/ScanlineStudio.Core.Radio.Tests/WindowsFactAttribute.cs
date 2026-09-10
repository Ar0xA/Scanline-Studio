namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>
/// Counterpart to this project's existing <c>SkipOnWindowsFactAttribute</c>: runs ONLY on Windows,
/// for the code paths that had no Windows-side assertion at all (`BACKLOG.md` W7). Gates on the
/// operating system alone — it opens no port and touches no hardware.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only: this asserts behaviour of a Windows platform API.";
        }
    }
}
