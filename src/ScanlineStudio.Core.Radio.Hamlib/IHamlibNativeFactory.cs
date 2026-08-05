using System.Runtime.InteropServices;

namespace ScanlineStudio.Core.Radio.Hamlib;

/// <summary>
/// Seam around constructing <see cref="HamlibNative"/> from an already-loaded library handle, so
/// <see cref="HamlibRuntime"/> is unit-testable with a fake <see cref="IHamlibNative"/> regardless of
/// the (meaningless, in a test) handle value <see cref="HamlibLibraryLocator"/> produced --
/// <see cref="Marshal.GetDelegateForFunctionPointer{TDelegate}"/> needs a real callable native
/// function pointer, which a fake <see cref="INativeLibraryLoader"/> can't meaningfully provide.
/// </summary>
internal interface IHamlibNativeFactory
{
    IHamlibNative Create(INativeLibraryLoader loader, nint handle);
}
