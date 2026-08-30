namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>A regular <see cref="FactAttribute"/> that skips itself on Windows -- for a test whose
/// entire point is to exercise the "OmniRig is Windows-only, this platform can't reach it" guard
/// path, which is meaningless on the one OS where OmniRig's real COM path is actually reachable.
/// Previously an early-`return` on Windows, which xUnit reports as "passed," not "skipped."</summary>
public sealed class SkipOnWindowsFactAttribute : FactAttribute
{
    public SkipOnWindowsFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "This test exercises the non-Windows guard path; meaningless on Windows, where the real OmniRig COM path is reachable instead.";
        }
    }
}
