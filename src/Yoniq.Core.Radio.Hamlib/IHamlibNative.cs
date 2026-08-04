using System.Runtime.InteropServices;

namespace Yoniq.Core.Radio.Hamlib;

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

    /// <summary><c>const char *rig_version(void)</c> -- <b>not</b> <c>hamlib_version2</c>, which is a
    /// data export, not a function (see spec/03-cat-layer.md's "Version gate"). Already decoded;
    /// <see langword="null"/> if the native call returned a null pointer.</summary>
    string? RigVersion();
}
