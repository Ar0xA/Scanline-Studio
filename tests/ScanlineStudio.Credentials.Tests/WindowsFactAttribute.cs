namespace ScanlineStudio.Credentials.Tests;

/// <summary>Copy of <c>ScanlineStudio.Core.Radio.Tests.WindowsFactAttribute</c>: runs only on Windows.</summary>
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
