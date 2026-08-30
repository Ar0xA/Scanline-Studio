namespace ScanlineStudio.Settings.Tests;

/// <summary>A regular <see cref="FactAttribute"/> that skips itself on Windows -- for a test whose
/// entire point is to exercise Unix file-mode restriction (<see cref="System.IO.File.SetUnixFileMode"/>
/// throws <see cref="PlatformNotSupportedException"/> on Windows, which this store's own
/// production code guards against). Local copy of
/// <c>ScanlineStudio.Core.Radio.Tests.SkipOnWindowsFactAttribute</c>'s identical pattern -- a bare
/// early-`return` on Windows would have xUnit report the test as "passed," not "skipped."</summary>
public sealed class SkipOnWindowsFactAttribute : FactAttribute
{
    public SkipOnWindowsFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Exercises Unix file-mode restriction; File.SetUnixFileMode is not supported on Windows.";
        }
    }
}
