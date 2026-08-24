using ScanlineStudio.Core.Radio.Hamlib;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>Scriptable <see cref="INativeLibraryLoader"/> test double -- lets
/// <see cref="HamlibLibraryLocator"/>'s tier-ordering logic (and <see cref="HamlibRuntime"/>'s use of
/// it) be tested without touching the real filesystem/dynamic linker.</summary>
internal sealed class FakeNativeLibraryLoader : INativeLibraryLoader
{
    private readonly Dictionary<string, nint> _loadable = new(StringComparer.Ordinal);
    private readonly Dictionary<(nint Handle, string Name), nint> _exports = [];
    private readonly Dictionary<string, string> _errorDetails = new(StringComparer.Ordinal);

    public void Succeed(string libraryPath, nint handle) => _loadable[libraryPath] = handle;

    public void SucceedExport(nint handle, string name, nint address) => _exports[(handle, name)] = address;

    /// <summary>Scripts what a FAILED <see cref="TryLoad"/> call for this exact path reports as its
    /// <c>errorDetail</c> -- mirrors the real loader's caught-exception message text (e.g. "The
    /// specified module could not be found."). Paths with nothing scripted here default to a
    /// generic placeholder, same as any path that was simply never registered via
    /// <see cref="Succeed"/>.</summary>
    public void FailWithDetail(string libraryPath, string errorDetail) => _errorDetails[libraryPath] = errorDetail;

    public bool TryLoad(string libraryPath, out nint handle, out string? errorDetail)
    {
        if (_loadable.TryGetValue(libraryPath, out handle))
        {
            errorDetail = null;
            return true;
        }

        errorDetail = _errorDetails.GetValueOrDefault(libraryPath, "(fake) not registered as loadable");
        return false;
    }

    public bool TryGetExport(nint handle, string name, out nint address) =>
        _exports.TryGetValue((handle, name), out address);
}
