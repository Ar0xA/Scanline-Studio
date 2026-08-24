using System.Runtime.InteropServices;

namespace ScanlineStudio.Core.Radio.Hamlib;

/// <summary>
/// See spec/03-cat-layer.md's "Frozen P/Invoke surface" and "<c>IHamlibNative</c> seam" sections.
/// The frozen Hamlib function surface, at the granularity <see cref="HamlibRadioProtocol"/> actually
/// needs -- ordinary C# types at this boundary (strings, not <c>byte[]</c>; a decoded
/// <see cref="string"/> from <see cref="RigVersion"/>, not a raw <c>nint</c>). Encoding/marshaling
/// details live in <see cref="HamlibNative"/>, the real implementation; <c>FakeHamlibNative</c> (test
/// project) backs unit tests without a real Hamlib install. Never called via <c>DllImport</c>
/// directly from <see cref="HamlibRadioProtocol"/> -- this interface is the only seam.
/// </summary>
internal interface IHamlibNative
{
    /// <summary><c>RIG *rig_init(rig_model_t)</c>. Returns <see cref="nint.Zero"/> on failure --
    /// callers must check before passing the result to any other method.</summary>
    nint RigInit(uint model);

    int RigOpen(nint rig);
    int RigClose(nint rig);
    int RigCleanup(nint rig);

    /// <summary><c>hamlib_token_t rig_token_lookup(RIG *, const char *)</c>. Returns
    /// <c>RIG_CONF_END</c> (0) if <paramref name="name"/> isn't a recognized config token --
    /// callers must check before passing the result to <see cref="RigSetConf"/>.</summary>
    CLong RigTokenLookup(nint rig, string name);

    int RigSetConf(nint rig, CLong token, string value);

    int RigSetFreq(nint rig, uint vfo, double freq);
    int RigGetFreq(nint rig, uint vfo, out double freq);

    int RigSetMode(nint rig, uint vfo, ulong mode, CLong width);
    int RigGetMode(nint rig, uint vfo, out ulong mode, out CLong width);

    int RigSetPtt(nint rig, uint vfo, int ptt);
    int RigGetPtt(nint rig, uint vfo, out int ptt);

    /// <summary><c>int rig_get_level(RIG *, vfo_t, setting_t, value_t *)</c>. <paramref name="level"/>
    /// is one of the <c>RIG_LEVEL_*</c> bit flags (e.g. <c>RIG_LEVEL_SWR</c>) -- <c>setting_t</c> is
    /// <c>typedef uint64_t setting_t</c> (verified directly against rig.h), a plain fixed-width
    /// <see langword="ulong"/>, NOT the <see cref="CLong"/> marshaling this interface uses for
    /// <c>pbwidth_t</c>/<c>hamlib_token_t</c> (those are C <c>long</c>, genuinely 32-bit on Windows/LLP64
    /// vs 64-bit on Linux/macOS/LP64 -- <c>setting_t</c> has no such platform-width ambiguity to guard
    /// against). Only the float arm of the <c>value_t</c> union is exposed here -- every level this
    /// project reads (SWR/ALC/RFPOWER_METER) is documented in rig.h as "arg float"; the union-vs-struct
    /// marshaling detail lives in <see cref="HamlibNative"/>, not this seam. See
    /// <see cref="RigGetLevelInt"/> for the sibling int arm (<c>RIG_LEVEL_STRENGTH</c>).</summary>
    int RigGetLevel(nint rig, uint vfo, ulong level, out float value);

    /// <summary>Same underlying <c>rig_get_level</c> call as <see cref="RigGetLevel"/> above, but reads
    /// the union's <c>int i</c> arm instead of its <c>float f</c> arm -- <c>RIG_LEVEL_STRENGTH</c> is
    /// documented in rig.h as "arg int (dB)", NOT float, unlike every other level this project reads.
    /// A real, easy-to-miss bug class this exists to avoid: <c>value_t</c> is a genuine C union (both
    /// arms occupy the SAME 4 bytes at offset 0), so reading an int-typed level's raw bytes back out
    /// as <see langword="float"/> would silently reinterpret its bit pattern as an unrelated IEEE-754
    /// value instead of throwing or producing an obviously-wrong number -- see
    /// <see cref="HamlibNative"/>'s own marshaling struct for where this is actually read.</summary>
    int RigGetLevelInt(nint rig, uint vfo, ulong level, out int value);

    /// <summary><c>const char *rig_version(void)</c> -- <b>not</b> <c>hamlib_version2</c>, which is a
    /// data export, not a function (see spec/03-cat-layer.md's "Version gate"). Already decoded;
    /// <see langword="null"/> if the native call returned a null pointer.</summary>
    string? RigVersion();

    /// <summary><c>int rig_load_all_backends(void)</c>. Populates Hamlib's internal backend registry
    /// so <see cref="RigListModelIds"/>/<see cref="RigGetCapsMfgName"/>/<see cref="RigGetCapsModelName"/>
    /// can enumerate every compiled-in rig. Not needed for <see cref="RigInit"/> against a single
    /// already-known model -- only for full-catalog listing (the Options dialog's Hamlib rig-list
    /// picker). Touches process-global state with no internal locking -- callers must serialize this
    /// against any other call into it (see <c>HamlibDiscoveryService</c>'s own doc comment).</summary>
    int RigLoadAllBackends();

    /// <summary><c>int rig_list_foreach_model(int (*cfunc)(rig_model_t, rig_ptr_t), rig_ptr_t)</c>.
    /// The callback receives only the scalar model id, never a <c>rig_caps</c> struct pointer --
    /// deliberately chosen over <c>rig_list_foreach</c> to avoid marshaling that struct's layout.
    /// Requires <see cref="RigLoadAllBackends"/> to have been called first, or this only sees whatever
    /// backends happen to already be registered.</summary>
    IReadOnlyList<uint> RigListModelIds();

    /// <summary><c>rig_get_caps_cptr(rig_model_t, RIG_CAPS_MFG_NAME_CPTR)</c>. <see langword="null"/>
    /// if the model id isn't registered (call <see cref="RigLoadAllBackends"/> first).</summary>
    string? RigGetCapsMfgName(uint model);

    /// <summary><c>rig_get_caps_cptr(rig_model_t, RIG_CAPS_MODEL_NAME_CPTR)</c>. <see langword="null"/>
    /// if the model id isn't registered (call <see cref="RigLoadAllBackends"/> first).</summary>
    string? RigGetCapsModelName(uint model);
}
