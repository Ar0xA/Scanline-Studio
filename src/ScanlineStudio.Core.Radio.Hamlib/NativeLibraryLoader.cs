using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ScanlineStudio.Core.Radio.Hamlib;

/// <summary>Real <see cref="INativeLibraryLoader"/> -- a direct passthrough to
/// <see cref="NativeLibrary"/>'s static API.</summary>
internal sealed class NativeLibraryLoader : INativeLibraryLoader
{
    public bool TryLoad(string libraryPath, out nint handle, out string? errorDetail)
    {
        // Marshal.GetLastPInvokeError() reads the current thread's last SetLastError=true P/Invoke
        // result -- NativeLibrary.TryLoad's own underlying LoadLibraryEx call sets it, so this must
        // run IMMEDIATELY after TryLoad returns, with no other interop call in between that could
        // clobber it. Win32Exception turns the raw code into the same human text Windows itself
        // would show (e.g. "The specified module could not be found" for a missing dependency DLL,
        // distinct from "%1 is not a valid Win32 application" for a 32/64-bit mismatch) -- see
        // INativeLibraryLoader.TryLoad's own doc comment for why this exists at all.
        var loaded = NativeLibrary.TryLoad(libraryPath, out handle);
        errorDetail = loaded ? null : new Win32Exception(Marshal.GetLastPInvokeError()).Message;
        return loaded;
    }

    public bool TryGetExport(nint handle, string name, out nint address) =>
        NativeLibrary.TryGetExport(handle, name, out address);
}
