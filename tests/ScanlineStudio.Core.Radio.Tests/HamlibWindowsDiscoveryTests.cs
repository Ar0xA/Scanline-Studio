using ScanlineStudio.Core.Radio.Hamlib;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>
/// `BACKLOG.md` W9 item 1. `HamlibLibraryLocator`'s Windows candidate list and base-directory probe
/// decide whether CAT works at all on Windows, and were absent from W1-W8 entirely — the branch had
/// no test of any kind.
///
/// <para><b>Device cost: none.</b> Nothing is loaded and no rig is contacted. These assert on the
/// SHAPE of the candidate sequence the locator would try.</para>
///
/// <para><b>Why this matters more than it looks.</b> The Hamlib suite spent its whole life
/// registering `libhamlib.so.4` with a fake loader, so on Windows nothing the locator actually
/// attempts was ever exercised. Fourteen tests failed there while the production locator was
/// correct. These assert the Windows sequence directly, so the next change to it cannot pass
/// unnoticed.</para>
/// </summary>
public sealed class HamlibWindowsDiscoveryTests
{
    [WindowsFact]
    public void WindowsCandidates_AreTheDllNamesHamlibActuallyShips()
    {
        var candidates = HamlibLibraryLocator.CandidatesForCurrentPlatform;

        // Both spellings ship in the wild: the official Windows build names it hamlib-4.dll, while
        // MSYS2 and several redistributions use the lib-prefixed form. Dropping either would break
        // auto-detection for a real population of users, silently.
        Assert.Contains("hamlib-4.dll", candidates);
        Assert.Contains("libhamlib-4.dll", candidates);
    }

    [WindowsFact]
    public void WindowsCandidates_CarryNoUnixSonames()
    {
        // A Unix soname reaching the Windows list would not fail loudly -- NativeLibrary.TryLoad just
        // returns false -- so it would cost a load attempt per launch and mislead anyone reading the
        // failure list in a bug report.
        foreach (var candidate in HamlibLibraryLocator.CandidatesForCurrentPlatform)
        {
            Assert.DoesNotContain(".so", candidate, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".dylib", candidate, StringComparison.OrdinalIgnoreCase);
        }
    }

    [WindowsFact]
    public void EveryBareName_IsAlsoProbedBesideTheRunningApplication()
    {
        // The user-reported gap this branch exists to close: dropping the DLL next to the executable
        // did not get auto-detected, because the OS loader's default search order does not reliably
        // cover AppContext.BaseDirectory -- notably under a single-file publish, where it can differ
        // from the process directory.
        var candidates = HamlibLibraryLocator.CandidatesForCurrentPlatform;

        foreach (var bare in candidates.Where(c => !Path.IsPathRooted(c)))
        {
            Assert.Contains(Path.Combine(AppContext.BaseDirectory, bare), candidates);
        }
    }
}
