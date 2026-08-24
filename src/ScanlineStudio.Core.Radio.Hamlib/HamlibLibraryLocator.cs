using System.Runtime.InteropServices;

namespace ScanlineStudio.Core.Radio.Hamlib;

/// <summary>
/// See spec/03-cat-layer.md's "Discovery order" -- three tiers overall (1: user override, 2: bare
/// soname, 3: known extra directories). If a caller-supplied override path is set, it is tried
/// <b>exclusively</b> (tier 1) — a user who explicitly configured a path wants exactly that library,
/// not a silent substitution from auto-detection. Otherwise, this class builds candidates for tiers
/// 2 and 3, each falling through to the next: bare per-OS soname (tier 2), letting the OS's own
/// dynamic linker search its normal paths, then a short hand-maintained list of known extra install
/// directories (tier 3, macOS-only today per the spec's own "e.g." wording -- not an accidental
/// Windows/Linux omission). Throws <see cref="HamlibUnavailableException"/> listing every candidate
/// tried and why if nothing loads.
/// </summary>
internal sealed class HamlibLibraryLocator
{
    private static readonly string[] LinuxCandidates = ["libhamlib.so.4"];
    private static readonly string[] MacCandidates = ["libhamlib.4.dylib"];
    private static readonly string[] WindowsCandidates = ["hamlib-4.dll", "libhamlib-4.dll"];

    // Homebrew on Apple Silicon doesn't always land on the default dynamic-linker search path.
    private static readonly string[] MacExtraDirs = ["/opt/homebrew/lib"];

    private readonly INativeLibraryLoader _loader;
    private readonly string? _overridePath;

    public HamlibLibraryLocator(INativeLibraryLoader loader, string? overridePath)
    {
        _loader = loader;
        _overridePath = overridePath;
    }

    /// <summary>Returns the loaded library handle and which candidate path succeeded (for
    /// diagnostics). Throws <see cref="HamlibUnavailableException"/> if every attempted candidate
    /// fails.</summary>
    public (nint Handle, string ResolvedPath) Locate()
    {
        // Round-1 code-review finding (Tier A Batch 9 chunk 9d): `is not null` alone doesn't catch
        // an override persisted as an empty/whitespace string -- that would disable tiers 2/3 for
        // no real path at all, failing with a confusing "(user override): failed to load" instead of
        // falling back to auto-detection. Unreachable today (no caller passes one yet, per
        // HamlibProtocolFactory.cs), but this is exactly the kind of setting a future Settings
        // wiring pass would introduce without necessarily re-reading this method.
        if (!string.IsNullOrWhiteSpace(_overridePath))
        {
            if (_loader.TryLoad(_overridePath, out var overrideHandle, out var overrideError))
            {
                return (overrideHandle, _overridePath);
            }

            throw new HamlibUnavailableException([$"{_overridePath} (user override): failed to load ({overrideError})"]);
        }

        var attempts = new List<string>();

        foreach (var candidate in BuildTier2And3Candidates())
        {
            if (_loader.TryLoad(candidate, out var handle, out var candidateError))
            {
                return (handle, candidate);
            }

            attempts.Add($"{candidate}: failed to load ({candidateError})");
        }

        throw new HamlibUnavailableException(attempts);
    }

    private static IEnumerable<string> BuildTier2And3Candidates()
    {
        string[] platformCandidates;
        string[] extraDirs = [];

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            platformCandidates = WindowsCandidates;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            platformCandidates = MacCandidates;
            extraDirs = MacExtraDirs;
        }
        else
        {
            platformCandidates = LinuxCandidates;
        }

        foreach (var candidate in platformCandidates)
        {
            yield return candidate;
        }

        // User-reported gap: dropping the library right next to the running app didn't get
        // auto-detected even under the standalone-publish exe (the case with the least reason to
        // fail). Bare NativeLibrary.TryLoad relies entirely on the OS loader's own default search
        // order, which does NOT reliably cover this: dlopen on Linux never searches argv[0]'s own
        // directory (only LD_LIBRARY_PATH/rpath/ldconfig's cache), and on Windows a self-contained
        // single-file publish is a real, documented case where AppContext.BaseDirectory can diverge
        // from the directory the .exe physically sits in once its bundled content gets extracted to
        // a temp directory at startup. Explicitly trying each candidate name joined against
        // AppContext.BaseDirectory makes "drop the library next to the app" a real, tested
        // placement instead of leaning on OS-implicit behavior this project doesn't control.
        foreach (var candidate in platformCandidates)
        {
            yield return Path.Combine(AppContext.BaseDirectory, candidate);
        }

        foreach (var dir in extraDirs)
        {
            foreach (var candidate in platformCandidates)
            {
                yield return Path.Combine(dir, candidate);
            }
        }
    }
}
