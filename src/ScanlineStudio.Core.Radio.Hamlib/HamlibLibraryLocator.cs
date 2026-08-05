using System.Runtime.InteropServices;

namespace ScanlineStudio.Core.Radio.Hamlib;

/// <summary>
/// See spec/03-cat-layer.md's "Discovery order". If a caller-supplied override path is set, it is
/// tried <b>exclusively</b> — a user who explicitly configured a path wants exactly that library, not
/// a silent substitution from auto-detection. Otherwise, two tiers, each falling through to the next:
/// (1) bare per-OS soname, letting the OS's own dynamic linker search its normal paths; (2) a short
/// hand-maintained list of known extra install directories. Throws
/// <see cref="HamlibUnavailableException"/> listing every candidate tried and why if nothing loads.
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
        if (_overridePath is not null)
        {
            if (_loader.TryLoad(_overridePath, out var overrideHandle))
            {
                return (overrideHandle, _overridePath);
            }

            throw new HamlibUnavailableException([$"{_overridePath} (user override): not found"]);
        }

        var attempts = new List<string>();

        foreach (var candidate in BuildTier1And2Candidates())
        {
            if (_loader.TryLoad(candidate, out var handle))
            {
                return (handle, candidate);
            }

            attempts.Add($"{candidate}: not found");
        }

        throw new HamlibUnavailableException(attempts);
    }

    private static IEnumerable<string> BuildTier1And2Candidates()
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
