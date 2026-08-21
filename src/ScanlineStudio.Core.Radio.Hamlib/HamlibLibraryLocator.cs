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
        // no real path at all, failing with a confusing "(user override): not found" instead of
        // falling back to auto-detection. Unreachable today (no caller passes one yet, per
        // HamlibProtocolFactory.cs), but this is exactly the kind of setting a future Settings
        // wiring pass would introduce without necessarily re-reading this method.
        if (!string.IsNullOrWhiteSpace(_overridePath))
        {
            if (_loader.TryLoad(_overridePath, out var overrideHandle))
            {
                return (overrideHandle, _overridePath);
            }

            throw new HamlibUnavailableException([$"{_overridePath} (user override): not found"]);
        }

        var attempts = new List<string>();

        foreach (var candidate in BuildTier2And3Candidates())
        {
            if (_loader.TryLoad(candidate, out var handle))
            {
                return (handle, candidate);
            }

            attempts.Add($"{candidate}: not found");
        }

        throw new HamlibUnavailableException(attempts);
    }

    private static IEnumerable<string> BuildTier2And3Candidates()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var candidate in WindowsCandidates)
            {
                yield return candidate;
            }

            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            foreach (var candidate in MacCandidates)
            {
                yield return candidate;
            }

            foreach (var dir in MacExtraDirs)
            {
                foreach (var candidate in MacCandidates)
                {
                    yield return Path.Combine(dir, candidate);
                }
            }

            yield break;
        }

        foreach (var candidate in LinuxCandidates)
        {
            yield return candidate;
        }
    }
}
