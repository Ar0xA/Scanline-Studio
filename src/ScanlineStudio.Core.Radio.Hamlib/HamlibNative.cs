using System.Runtime.InteropServices;
using System.Text;

namespace ScanlineStudio.Core.Radio.Hamlib;

/// <summary>
/// Real <see cref="IHamlibNative"/>, built from an already-loaded library handle (see
/// <see cref="HamlibLibraryLocator"/>). Resolves each export once at construction via
/// <see cref="INativeLibraryLoader.TryGetExport"/> + <see cref="Marshal.GetDelegateForFunctionPointer"/>
/// and caches the delegate -- never re-resolved per call. Every delegate is
/// <see cref="UnmanagedFunctionPointerAttribute"/>-marked <see cref="CallingConvention.Cdecl"/>, bound
/// at the non-<c>BUILTINFUNC</c> (default-build) arity (spec/03-cat-layer.md's "Frozen P/Invoke
/// surface"). String in-params are marshaled as explicit UTF-8 byte arrays with an explicit trailing
/// <c>0x00</c> -- <see cref="Encoding.UTF8.GetBytes(string)"/> alone has no null terminator, and
/// Hamlib's own <c>strcmp</c>/<c>strncpy</c> on these needs one (CLAUDE.md's explicit-encoding rule;
/// default delegate string marshaling is ANSI-on-Windows, which this deliberately avoids).
/// </summary>
internal sealed class HamlibNative : IHamlibNative
{
    /// <summary>Marshals Hamlib's <c>value_t</c> union (rig.h) -- a C union whose largest member is
    /// the nested <c>{int l; unsigned char *d;}</c> struct (4-byte int + padding + 8-byte pointer =
    /// 16 bytes on both LP64 and LLP64). <c>Size = 16</c> so the native side never writes past the
    /// marshaled buffer regardless of which arm it actually touches. <see cref="FloatValue"/> and
    /// <see cref="IntValue"/> both sit at offset 0 (the union's <c>float f</c>/<c>signed int i</c>
    /// arms) -- a caller must read whichever one matches the specific <c>RIG_LEVEL_*</c> just
    /// queried (float for SWR/ALC/RFPOWER_METER, int for STRENGTH, both per rig.h's own per-level
    /// "arg float"/"arg int" documentation) -- reading the wrong arm reinterprets the same 4 raw
    /// bytes as an unrelated value, not a type error, so this has no compiler-catchable failure
    /// mode if a caller ever gets it backwards.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct HamlibValue
    {
        [FieldOffset(0)]
        public float FloatValue;

        [FieldOffset(0)]
        public int IntValue;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint RigInitDelegate(uint model);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RigHandleOnlyDelegate(nint rig);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate CLong RigTokenLookupDelegate(nint rig, byte[] name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RigSetConfDelegate(nint rig, CLong token, byte[] value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RigSetFreqDelegate(nint rig, uint vfo, double freq);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RigGetFreqDelegate(nint rig, uint vfo, out double freq);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RigSetModeDelegate(nint rig, uint vfo, ulong mode, CLong width);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RigGetModeDelegate(nint rig, uint vfo, out ulong mode, out CLong width);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RigSetPttDelegate(nint rig, uint vfo, int ptt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RigGetPttDelegate(nint rig, uint vfo, out int ptt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint RigVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RigGetLevelDelegate(nint rig, uint vfo, ulong level, out HamlibValue value);

    private readonly RigInitDelegate _rigInit;
    private readonly RigHandleOnlyDelegate _rigOpen;
    private readonly RigHandleOnlyDelegate _rigClose;
    private readonly RigHandleOnlyDelegate _rigCleanup;
    private readonly RigTokenLookupDelegate _rigTokenLookup;
    private readonly RigSetConfDelegate _rigSetConf;
    private readonly RigSetFreqDelegate _rigSetFreq;
    private readonly RigGetFreqDelegate _rigGetFreq;
    private readonly RigSetModeDelegate _rigSetMode;
    private readonly RigGetModeDelegate _rigGetMode;
    private readonly RigSetPttDelegate _rigSetPtt;
    private readonly RigGetPttDelegate _rigGetPtt;
    private readonly RigVersionDelegate _rigVersion;
    private readonly RigGetLevelDelegate _rigGetLevel;

    public HamlibNative(INativeLibraryLoader loader, nint handle)
    {
        _rigInit = Resolve<RigInitDelegate>(loader, handle, "rig_init");
        _rigOpen = Resolve<RigHandleOnlyDelegate>(loader, handle, "rig_open");
        _rigClose = Resolve<RigHandleOnlyDelegate>(loader, handle, "rig_close");
        _rigCleanup = Resolve<RigHandleOnlyDelegate>(loader, handle, "rig_cleanup");
        _rigTokenLookup = Resolve<RigTokenLookupDelegate>(loader, handle, "rig_token_lookup");
        _rigSetConf = Resolve<RigSetConfDelegate>(loader, handle, "rig_set_conf");
        _rigSetFreq = Resolve<RigSetFreqDelegate>(loader, handle, "rig_set_freq");
        _rigGetFreq = Resolve<RigGetFreqDelegate>(loader, handle, "rig_get_freq");
        _rigSetMode = Resolve<RigSetModeDelegate>(loader, handle, "rig_set_mode");
        _rigGetMode = Resolve<RigGetModeDelegate>(loader, handle, "rig_get_mode");
        _rigSetPtt = Resolve<RigSetPttDelegate>(loader, handle, "rig_set_ptt");
        _rigGetPtt = Resolve<RigGetPttDelegate>(loader, handle, "rig_get_ptt");
        // rig_version(), never hamlib_version2 -- that name is a data export, not a function
        // (spec/03-cat-layer.md's "Version gate"); P/Invoking it as a delegate would crash.
        _rigVersion = Resolve<RigVersionDelegate>(loader, handle, "rig_version");
        _rigGetLevel = Resolve<RigGetLevelDelegate>(loader, handle, "rig_get_level");
    }

    public nint RigInit(uint model) => _rigInit(model);
    public int RigOpen(nint rig) => _rigOpen(rig);
    public int RigClose(nint rig) => _rigClose(rig);
    public int RigCleanup(nint rig) => _rigCleanup(rig);

    public CLong RigTokenLookup(nint rig, string name) => _rigTokenLookup(rig, ToUtf8NullTerminated(name));

    public int RigSetConf(nint rig, CLong token, string value) =>
        _rigSetConf(rig, token, ToUtf8NullTerminated(value));

    public int RigSetFreq(nint rig, uint vfo, double freq) => _rigSetFreq(rig, vfo, freq);
    public int RigGetFreq(nint rig, uint vfo, out double freq) => _rigGetFreq(rig, vfo, out freq);

    public int RigSetMode(nint rig, uint vfo, ulong mode, CLong width) => _rigSetMode(rig, vfo, mode, width);

    public int RigGetMode(nint rig, uint vfo, out ulong mode, out CLong width) =>
        _rigGetMode(rig, vfo, out mode, out width);

    public int RigSetPtt(nint rig, uint vfo, int ptt) => _rigSetPtt(rig, vfo, ptt);
    public int RigGetPtt(nint rig, uint vfo, out int ptt) => _rigGetPtt(rig, vfo, out ptt);

    public string? RigVersion()
    {
        var ptr = _rigVersion();
        return ptr == nint.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }

    public int RigGetLevel(nint rig, uint vfo, ulong level, out float value)
    {
        var code = _rigGetLevel(rig, vfo, level, out var native);
        value = native.FloatValue;
        return code;
    }

    public int RigGetLevelInt(nint rig, uint vfo, ulong level, out int value)
    {
        var code = _rigGetLevel(rig, vfo, level, out var native);
        value = native.IntValue;
        return code;
    }

    private static TDelegate Resolve<TDelegate>(INativeLibraryLoader loader, nint handle, string exportName)
        where TDelegate : Delegate
    {
        if (!loader.TryGetExport(handle, exportName, out var address))
        {
            throw new HamlibUnavailableException([$"{exportName}: export not found in loaded library"]);
        }

        return Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
    }

    private static byte[] ToUtf8NullTerminated(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var bytes = new byte[byteCount + 1]; // trailing 0x00 -- GetBytes alone doesn't add one, and
                                              // Hamlib's strcmp/strncpy on this value needs it.
        Encoding.UTF8.GetBytes(value, 0, value.Length, bytes, 0);
        return bytes;
    }
}
