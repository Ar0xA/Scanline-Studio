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

        // Plain text with a .dll extension: a file that exists but is not a loadable image on ANY
        // platform. This used to point at the test assembly itself, which is not an ELF and so fails
        // dlopen on Linux -- but IS a valid PE image, which LoadLibrary maps happily, so TryLoad
        // correctly returned true on Windows and the test failed for the right reason.
        var notALibrary = Path.Combine(Path.GetTempPath(), $"scanline-not-a-library-{Guid.NewGuid():N}.dll");
        File.WriteAllText(notALibrary, "this is not a shared library");

        try
        {
            var loaded = sut.TryLoad(notALibrary, out _, out var errorDetail);

            Assert.False(loaded);

            // The real intent of the test, which survives the fixture change: a failure must carry a
            // usable reason. Windows' loader can otherwise report "operation completed successfully"
            // for a failed load, which would reach the user as a support message saying nothing.
            Assert.False(string.IsNullOrWhiteSpace(errorDetail));
            Assert.DoesNotContain("operation completed successfully", errorDetail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("success", errorDetail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(notALibrary);
        }
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
