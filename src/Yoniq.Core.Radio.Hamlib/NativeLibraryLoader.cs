using System.Runtime.InteropServices;

namespace Yoniq.Core.Radio.Hamlib;

/// <summary>Real <see cref="INativeLibraryLoader"/> -- a direct passthrough to
/// <see cref="NativeLibrary"/>'s static API.</summary>
internal sealed class NativeLibraryLoader : INativeLibraryLoader
{
    public bool TryLoad(string libraryPath, out nint handle) =>
        NativeLibrary.TryLoad(libraryPath, out handle);

    public bool TryGetExport(nint handle, string name, out nint address) =>
        NativeLibrary.TryGetExport(handle, name, out address);
}
