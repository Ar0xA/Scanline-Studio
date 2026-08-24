using System.Runtime.InteropServices;

namespace ScanlineStudio.Core.Radio.Hamlib;

/// <summary>Real <see cref="INativeLibraryLoader"/> -- a direct passthrough to
/// <see cref="NativeLibrary"/>'s static API.</summary>
internal sealed class NativeLibraryLoader : INativeLibraryLoader
{
    public bool TryLoad(string libraryPath, out nint handle, out string? errorDetail)
    {
        // NativeLibrary.TryLoad's underlying LoadFromPath call is a CoreCLR QCall, not a
        // SetLastError=true P/Invoke -- it never populates the state Marshal.GetLastPInvokeError()
        // reads, so a prior version of this method always reported the stale value from whatever
        // SetLastError=true call last ran on this thread (often ERROR_SUCCESS, surfacing the
        // actively wrong "The operation completed successfully." on every real failure). The
        // throwing NativeLibrary.Load(string) overload calls the same LoadFromPath with
        // throwOnError: true, so which libraries resolve is unchanged -- only the failure path
        // differs: CoreCLR formats the error it captured AT THE FAILURE SITE into the exception
        // message (e.g. "...: The specified module could not be found. (0x8007007E)" for a missing
        // dependency DLL, or a BadImageFormatException for a 32/64-bit mismatch).
        try
        {
            handle = NativeLibrary.Load(libraryPath);
            errorDetail = null;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or ArgumentException)
        {
            handle = 0;
            errorDetail = ex.Message;
            return false;
        }
    }

    public bool TryGetExport(nint handle, string name, out nint address) =>
        NativeLibrary.TryGetExport(handle, name, out address);
}
