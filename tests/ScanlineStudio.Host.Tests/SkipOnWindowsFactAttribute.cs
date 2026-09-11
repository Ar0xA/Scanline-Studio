namespace ScanlineStudio.Host.Tests;

/// <summary>
/// For assertions that can only be set up on Unix — typically by making a directory unwritable with
/// <c>File.SetUnixFileMode</c>, which throws <c>PlatformNotSupportedException</c> on Windows and has
/// no one-line ACL equivalent.
///
/// <para><b>Why an attribute and not <c>if (OperatingSystem.IsWindows()) return;</c>.</b> A bare
/// early return reports the test as PASSED on Windows while it asserts nothing — the suite claims
/// coverage it does not have, and nothing in the output says otherwise. A skip is honest: it shows up
/// as skipped, with a reason.</para>
/// </summary>
public sealed class SkipOnWindowsFactAttribute : FactAttribute
{
    public SkipOnWindowsFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Unix-only: this sets up its precondition with Unix file permissions, which have "
                + "no one-line equivalent on Windows.";
        }
    }
}
