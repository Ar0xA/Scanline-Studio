namespace Yoniq.Core.Radio.Hamlib;

/// <summary>
/// See spec/03-cat-layer.md's "Version gate". Pure string-parsing, no native call -- takes
/// <see cref="IHamlibNative.RigVersion"/>'s raw output (format
/// <c>"Hamlib &lt;major&gt;.&lt;minor&gt;.&lt;patch&gt; &lt;date&gt; &lt;arch&gt;"</c>) and decides
/// whether the loaded library's major version is one Yoniq's frozen surface was pinned against. Only
/// major version 4 is accepted; anything else (including a null/empty/unparseable result) is
/// "unavailable," never thrown from here -- the caller (<see cref="HamlibRuntime"/>) decides what to
/// do with a rejection.
/// </summary>
internal static class HamlibVersionGate
{
    private const int SupportedMajorVersion = 4;

    public static bool IsSupported(string? rigVersionOutput)
    {
        if (string.IsNullOrWhiteSpace(rigVersionOutput))
        {
            return false;
        }

        var tokens = rigVersionOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return false;
        }

        var versionToken = tokens[1];
        var dotIndex = versionToken.IndexOf('.');
        var majorText = dotIndex < 0 ? versionToken : versionToken[..dotIndex];

        return int.TryParse(majorText, out var major) && major == SupportedMajorVersion;
    }
}
