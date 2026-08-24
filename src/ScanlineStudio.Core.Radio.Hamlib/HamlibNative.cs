using System.Runtime.InteropServices;
using System.Text;

namespace ScanlineStudio.Core.Radio.Hamlib;

/// <summary>Hamlib's own <c>enum rig_debug_level_e</c> -- verified directly against the vendored
/// Hamlib source clone's <c>hamlib/include/hamlib/rig.h</c> (lines 238-245), not assumed. A plain C
/// enum starting at 0 with no gaps, so the numeric ordering below is load-bearing, not incidental --
/// don't reorder these to "look nicer."</summary>
internal enum HamlibDebugLevel
{
    None = 0,
    Bug = 1,
    Err = 2,
    Warn = 3,
    Verbose = 4,
    Trace = 5,
    Cache = 6,
}

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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RigLoadAllBackendsDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RigListForeachModelCallback(uint model, nint data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RigListForeachModelDelegate(RigListForeachModelCallback cfunc, nint data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint RigGetCapsCptrDelegate(uint model, int capsCptr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RigSetDebugDelegate(int debugLevel);

    // enum rig_caps_cptr_e (rig.h) -- order is VERSION=0, MFG_NAME=1, MODEL_NAME=2, STATUS=3; only
    // the two this project reads are named here.
    private const int RigCapsMfgNameCptr = 1;
    private const int RigCapsModelNameCptr = 2;

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
    private readonly RigLoadAllBackendsDelegate _rigLoadAllBackends;
    private readonly RigListForeachModelDelegate _rigListForeachModel;
    private readonly RigGetCapsCptrDelegate _rigGetCapsCptr;

    /// <summary>User-reported gap's own explicit requirement: resolved TOLERANTLY (unlike every
    /// other delegate above, all mandatory) -- a Hamlib build missing this export must not take out
    /// Hamlib support entirely over a logging nicety. <see langword="null"/> means "silently do
    /// nothing" everywhere this is used, never a thrown exception.</summary>
    private readonly RigSetDebugDelegate? _rigSetDebug;

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
        _rigLoadAllBackends = Resolve<RigLoadAllBackendsDelegate>(loader, handle, "rig_load_all_backends");
        _rigListForeachModel = Resolve<RigListForeachModelDelegate>(loader, handle, "rig_list_foreach_model");
        _rigGetCapsCptr = Resolve<RigGetCapsCptrDelegate>(loader, handle, "rig_get_caps_cptr");
        _rigSetDebug = TryResolve<RigSetDebugDelegate>(loader, handle, "rig_set_debug");

        // Must run before any other Hamlib call (rig_init, rig_load_all_backends, rig_version, ...)
        // -- this constructor is the first thing that ever touches the loaded library, so the end of
        // it is the natural place. WARN, not the library's own TRACE-level default: TRACE floods
        // stderr with a hex dump of every CAT frame plus per-call entry/exit traces on every poll
        // (write_block/read_string_generic/vfo_fixup, RIG_DEBUG_TRACE in the vendored source).
        // VERBOSE was tried first and measured live: it does NOT meaningfully quiet this down --
        // Hamlib's own ENTERFUNC/RETURNFUNC macros (misc.h), which fire on every single function
        // call, are ALSO RIG_DEBUG_VERBOSE, not TRACE, so VERBOSE still logs a full call trace, just
        // without the raw byte-level I/O dumps (~60% reduction measured against a live Test CAT
        // attempt, not the silence the reported issue actually asks for). WARN silences both
        // ENTERFUNC/RETURNFUNC and the byte-level TRACE dumps entirely, surfacing only genuine
        // Hamlib-detected problems (BUG/ERR/WARN) -- confirmed via the same live measurement.
        _rigSetDebug?.Invoke((int)HamlibDebugLevel.Warn);
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

    public int RigLoadAllBackends() => _rigLoadAllBackends();

    public IReadOnlyList<uint> RigListModelIds()
    {
        var models = new List<uint>();

        // The delegate instance must stay alive for the duration of this native call --
        // Marshal.GetFunctionPointerForDelegate (used internally when passing a delegate as a native
        // callback parameter) hands the native side a raw function pointer with no managed reference
        // keeping the delegate object alive, so an unrooted delegate is a real use-after-free risk if
        // the GC collects it mid-enumeration. GC.KeepAlive below pins it past the native call.
        RigListForeachModelCallback callback = (model, _) =>
        {
            models.Add(model);
            return 1; // non-zero = continue enumeration (rig_list_foreach_model returns early on 0)
        };

        _rigListForeachModel(callback, nint.Zero);
        GC.KeepAlive(callback);
        return models;
    }

    public string? RigGetCapsMfgName(uint model) => DecodeCapsCptr(model, RigCapsMfgNameCptr);

    public string? RigGetCapsModelName(uint model) => DecodeCapsCptr(model, RigCapsModelNameCptr);

    /// <summary>No-op if <c>rig_set_debug</c> wasn't resolvable at construction -- see
    /// <see cref="_rigSetDebug"/>'s own doc comment. Also exposed publicly (not just called once
    /// from the constructor) so a future diagnostics setting can deliberately raise this back to
    /// <see cref="HamlibDebugLevel.Trace"/> when troubleshooting a rig, without needing new interop.</summary>
    public void RigSetDebug(int level) => _rigSetDebug?.Invoke(level);

    private string? DecodeCapsCptr(uint model, int capsCptr)
    {
        var ptr = _rigGetCapsCptr(model, capsCptr);
        return ptr == nint.Zero ? null : Marshal.PtrToStringUTF8(ptr);
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

    /// <summary>Same resolution as <see cref="Resolve{TDelegate}"/>, but for an OPTIONAL export --
    /// <see langword="null"/> instead of throwing when the loaded library doesn't have it. Only
    /// <c>rig_set_debug</c> uses this today; every other export this project depends on is mandatory
    /// (a build missing e.g. <c>rig_open</c> genuinely can't function as a Hamlib backend, but one
    /// missing <c>rig_set_debug</c> can -- it would just run noisier than intended).</summary>
    private static TDelegate? TryResolve<TDelegate>(INativeLibraryLoader loader, nint handle, string exportName)
        where TDelegate : Delegate =>
        loader.TryGetExport(handle, exportName, out var address)
            ? Marshal.GetDelegateForFunctionPointer<TDelegate>(address)
            : null;

    private static byte[] ToUtf8NullTerminated(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var bytes = new byte[byteCount + 1]; // trailing 0x00 -- GetBytes alone doesn't add one, and
                                              // Hamlib's strcmp/strncpy on this value needs it.
        Encoding.UTF8.GetBytes(value, 0, value.Length, bytes, 0);
        return bytes;
    }
}
