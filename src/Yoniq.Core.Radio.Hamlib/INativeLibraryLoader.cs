namespace Yoniq.Core.Radio.Hamlib;

/// <summary>
/// See spec/03-cat-layer.md's "Discovery order". Thin seam around
/// <see cref="System.Runtime.InteropServices.NativeLibrary"/>'s static API so
/// <see cref="HamlibLibraryLocator"/>'s tier-ordering logic is unit-testable without touching the real
/// filesystem/dynamic linker -- mirrors why <c>IRadioTransport</c> exists as a seam for
/// <c>RigctldClientProtocol</c>.
/// </summary>
internal interface INativeLibraryLoader
{
    bool TryLoad(string libraryPath, out nint handle);
    bool TryGetExport(nint handle, string name, out nint address);
}
