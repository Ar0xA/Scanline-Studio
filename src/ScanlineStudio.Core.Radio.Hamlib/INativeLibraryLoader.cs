namespace ScanlineStudio.Core.Radio.Hamlib;

/// <summary>
/// See spec/03-cat-layer.md's "Discovery order". Thin seam around
/// <see cref="System.Runtime.InteropServices.NativeLibrary"/>'s static API so
/// <see cref="HamlibLibraryLocator"/>'s tier-ordering logic is unit-testable without touching the real
/// filesystem/dynamic linker -- mirrors why <c>IRadioTransport</c> exists as a seam for
/// <c>RigctldClientProtocol</c>.
/// </summary>
internal interface INativeLibraryLoader
{
    /// <summary>User-reported gap: <c>NativeLibrary.TryLoad</c> alone collapses every possible OS
    /// loader failure -- file genuinely missing, a dependency DLL it needs isn't found, a 32/64-bit
    /// image mismatch, a permissions problem -- into a single <see langword="false"/>, with zero way
    /// to tell them apart. A user who picked an EXISTING file (confirmed via a real file-picker
    /// dialog) and got "not found" back had no way to know it actually meant something like "this
    /// DLL's own dependency couldn't be resolved." <paramref name="errorDetail"/> is the real
    /// implementation's <c>Marshal.GetLastPInvokeError()</c> read immediately after the underlying
    /// OS call, formatted into a human message -- <see langword="null"/> whenever this returns
    /// <see langword="true"/>.</summary>
    bool TryLoad(string libraryPath, out nint handle, out string? errorDetail);
    bool TryGetExport(nint handle, string name, out nint address);
}
