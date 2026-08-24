using ScanlineStudio.Core.Radio.Hamlib;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>Tests against the REAL <see cref="NativeLibraryLoader"/>, not
/// <see cref="FakeNativeLibraryLoader"/> -- closes an auditor-flagged coverage gap where every
/// existing test scripted <c>errorDetail</c> and so never actually exercised the real error-reporting
/// mechanism. Regression guard for the bug this class previously shipped: a prior implementation read
/// <c>Marshal.GetLastPInvokeError()</c> after <c>NativeLibrary.TryLoad</c>, which is a CoreCLR QCall
/// and never populates that state -- so on failure it silently reported a stale value, most often
/// <c>ERROR_SUCCESS</c> ("The operation completed successfully."), actively misleading rather than
/// merely uninformative.</summary>
public class NativeLibraryLoaderTests
{
    [Fact]
    public void TryLoad_PathExistsButIsNotALoadableLibrary_ReturnsFalseWithARealErrorDetail()
    {
        var sut = new NativeLibraryLoader();
        // This test's own assembly DLL exists on disk but is not a native shared library the OS
        // loader can load -- guarantees a real, non-scripted failure from the actual OS loader.
        var notALibrary = typeof(NativeLibraryLoaderTests).Assembly.Location;

        var loaded = sut.TryLoad(notALibrary, out _, out var errorDetail);

        Assert.False(loaded);
        Assert.False(string.IsNullOrWhiteSpace(errorDetail));
        Assert.DoesNotContain("operation completed successfully", errorDetail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("success", errorDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryLoad_PathDoesNotExist_ReturnsFalseWithARealErrorDetail()
    {
        var sut = new NativeLibraryLoader();

        var loaded = sut.TryLoad("/definitely/not/a/real/path/libdoesnotexist.so", out _, out var errorDetail);

        Assert.False(loaded);
        Assert.False(string.IsNullOrWhiteSpace(errorDetail));
        Assert.DoesNotContain("operation completed successfully", errorDetail, StringComparison.OrdinalIgnoreCase);
    }
}
