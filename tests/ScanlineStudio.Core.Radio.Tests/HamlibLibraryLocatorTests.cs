using ScanlineStudio.Core.Radio.Hamlib;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>Discovery-order tests -- see spec/03-cat-layer.md's "Discovery order". This test suite
/// runs on Linux (this project's dev sandbox), so the auto-detection tier only ever exercises the
/// the locator's own first candidate for the running platform -- <see cref="HamlibLibraryLocator"/>'s Windows/macOS
/// candidate lists (and macOS's extra-directory tier) are unverified from here, same caveat as this
/// project's other Windows/macOS-only code paths.</summary>
public class HamlibLibraryLocatorTests
{
    // The locator's OWN first candidate on this platform. Hardcoding "libhamlib.so.4" pinned these
    // tests to Linux: on Windows the locator yields hamlib-4.dll, so nothing the fake registered was
    // ever attempted and every auto-detect case threw HamlibUnavailableException.
    private static string PrimarySoname => HamlibLibraryLocator.PrimaryCandidateForCurrentPlatform;

    [Fact]
    public void Locate_NoOverride_BareSonameLoads_ReturnsHandleAndCandidate()
    {
        var loader = new FakeNativeLibraryLoader();
        loader.Succeed(PrimarySoname, 42);
        var sut = new HamlibLibraryLocator(loader, overridePath: null);

        var (handle, resolvedPath) = sut.Locate();

        Assert.Equal(42, handle);
        Assert.Equal(PrimarySoname, resolvedPath);
    }

    [Fact]
    public void Locate_NoOverride_NothingLoads_ThrowsListingEveryCandidateTried()
    {
        var loader = new FakeNativeLibraryLoader(); // nothing registered -- every TryLoad fails
        var sut = new HamlibLibraryLocator(loader, overridePath: null);

        var ex = Assert.Throws<HamlibUnavailableException>(() => sut.Locate());

        Assert.Contains(ex.Attempts, a => a.Contains(PrimarySoname, StringComparison.Ordinal));
    }

    [Fact]
    public void Locate_OverrideSet_TriedExclusively_IgnoresAnAutoDetectableCandidate()
    {
        var loader = new FakeNativeLibraryLoader();
        loader.Succeed(PrimarySoname, 1);        // would succeed via auto-detection...
        loader.Succeed("/opt/my-hamlib.so", 2); // ...but the override must win instead
        var sut = new HamlibLibraryLocator(loader, overridePath: "/opt/my-hamlib.so");

        var (handle, resolvedPath) = sut.Locate();

        Assert.Equal(2, handle);
        Assert.Equal("/opt/my-hamlib.so", resolvedPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Locate_OverridePathIsEmptyOrWhitespace_FallsBackToAutoDetection(string emptyOverride)
    {
        // Closes a coverage gap flagged by Tier A Batch 9 chunk 9d (docs/functional-audit-playbook.md):
        // a bare `is not null` check treats an empty/whitespace override the same as a real one,
        // going exclusive-mode for no real path at all and failing with a confusing "failed to load"
        // instead of falling back to auto-detection. Unreachable today (no caller passes one yet),
        // but a real footgun the moment a future Settings wiring pass introduces one.
        var loader = new FakeNativeLibraryLoader();
        loader.Succeed(PrimarySoname, 42);
        var sut = new HamlibLibraryLocator(loader, overridePath: emptyOverride);

        var (handle, resolvedPath) = sut.Locate();

        Assert.Equal(42, handle);
        Assert.Equal(PrimarySoname, resolvedPath);
    }

    // User-reported gap: dropping the library next to the running app (including the standalone-
    // publish exe) wasn't found by auto-detect -- bare soname load alone can't cover this (dlopen
    // never searches argv[0]'s own directory on Linux). Fixed by also trying each candidate name
    // joined against AppContext.BaseDirectory as a fallback tier.
    [Fact]
    public void Locate_BareSonameFailsButBaseDirectoryCandidateSucceeds_ReturnsThatHandle()
    {
        var loader = new FakeNativeLibraryLoader();
        var baseDirCandidate = Path.Combine(AppContext.BaseDirectory, PrimarySoname);
        loader.Succeed(baseDirCandidate, 7); // bare PrimarySoname is deliberately NOT registered
        var sut = new HamlibLibraryLocator(loader, overridePath: null);

        var (handle, resolvedPath) = sut.Locate();

        Assert.Equal(7, handle);
        Assert.Equal(baseDirCandidate, resolvedPath);
    }

    // User-reported gap: a real "not found" message for a user-override path that genuinely EXISTS
    // (picked via a real file dialog) gave zero indication of why the OS loader actually rejected
    // it -- e.g. a missing dependency DLL vs. a 32/64-bit image mismatch look identical from a bare
    // "not found". The real NativeLibraryLoader now reports the OS error text captured by the
    // throwing NativeLibrary.Load overload's exception message; this test only checks that the
    // locator actually INCLUDES whatever detail the loader reports in its own thrown message, not
    // the real Win32-specific error text (that's the real loader's own concern, unverifiable on
    // this Linux sandbox).
    [Fact]
    public void Locate_OverrideSetButFails_IncludesTheLoaderReportedErrorDetail()
    {
        var loader = new FakeNativeLibraryLoader();
        loader.FailWithDetail("/opt/missing-hamlib.so", "The specified module could not be found.");
        var sut = new HamlibLibraryLocator(loader, overridePath: "/opt/missing-hamlib.so");

        var ex = Assert.Throws<HamlibUnavailableException>(() => sut.Locate());

        Assert.Contains("The specified module could not be found.", ex.Attempts[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Locate_OverrideSetButFails_ThrowsWithoutFallingBackToAutoDetection()
    {
        var loader = new FakeNativeLibraryLoader();
        loader.Succeed(PrimarySoname, 1); // auto-detection would succeed, but must never be tried
        var sut = new HamlibLibraryLocator(loader, overridePath: "/opt/missing-hamlib.so");

        var ex = Assert.Throws<HamlibUnavailableException>(() => sut.Locate());

        Assert.Single(ex.Attempts);
        Assert.Contains("/opt/missing-hamlib.so", ex.Attempts[0], StringComparison.Ordinal);
    }
}
