using Yoniq.Core.Radio.Hamlib;

namespace Yoniq.Core.Radio.Tests;

/// <summary>Scriptable <see cref="INativeLibraryLoader"/> test double -- lets
/// <see cref="HamlibLibraryLocator"/>'s tier-ordering logic (and <see cref="HamlibRuntime"/>'s use of
/// it) be tested without touching the real filesystem/dynamic linker.</summary>
internal sealed class FakeNativeLibraryLoader : INativeLibraryLoader
{
    private readonly Dictionary<string, nint> _loadable = new(StringComparer.Ordinal);
    private readonly Dictionary<(nint Handle, string Name), nint> _exports = [];

    public void Succeed(string libraryPath, nint handle) => _loadable[libraryPath] = handle;

    public void SucceedExport(nint handle, string name, nint address) => _exports[(handle, name)] = address;

    public bool TryLoad(string libraryPath, out nint handle) => _loadable.TryGetValue(libraryPath, out handle);

    public bool TryGetExport(nint handle, string name, out nint address) =>
        _exports.TryGetValue((handle, name), out address);
}
