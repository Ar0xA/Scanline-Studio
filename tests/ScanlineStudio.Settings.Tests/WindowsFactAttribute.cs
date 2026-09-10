namespace ScanlineStudio.Settings.Tests;

/// <summary>
/// Counterpart to this project's existing <c>SkipOnWindowsFactAttribute</c>: runs ONLY on Windows,
/// for the assertions that had no Windows side at all (`BACKLOG.md` W6, W8). Neither attribute
/// touches hardware — both gate on the operating system alone.
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
