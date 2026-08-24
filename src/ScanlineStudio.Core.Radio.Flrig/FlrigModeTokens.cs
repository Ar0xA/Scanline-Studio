using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio.Flrig;

/// <summary>Maps between <see cref="RadioMode"/> and flrig's real per-rig-driver mode-name vocabulary.
/// Unlike rigctld/Hamlib, flrig does NOT normalize mode names across rigs -- each driver defines its
/// own <c>modes_</c> string list (<c>rig.set_mode</c> does a case-sensitive exact match against it,
/// verified against the vendored source, <c>xml_server.cxx</c>). The token lists below are sourced by
/// grepping mode-name literals across rig drivers under a local flrig source clone's <c>src/rigs/</c>
/// (broad, not a handful of examples -- extended once already after a code-review pass found real
/// gaps, see the note below) -- still necessarily best-effort, not a claim of exhaustive coverage,
/// since flrig ships ~150 rig drivers and this project has no local rig registry
/// (spec/02-radio-layer.md's "Rig identification"); an unrecognized token resolves to
/// <see cref="RadioMode.Unknown"/> per that enum's own documented contract, never a throw.
///
/// <b>Write direction</b> (<see cref="Candidates"/>): to set a mode, the caller picks the FIRST
/// candidate token actually present in the connected rig's own <c>rig.get_modes</c> result -- never
/// sends a token blindly.
///
/// <b>Read direction</b> (<see cref="TokenToMode"/>): a SEPARATE, explicitly hand-authored dictionary,
/// not a mechanical inversion of <see cref="Candidates"/> -- some literal tokens could otherwise
/// plausibly appear in more than one mode's candidate list with no stated tie-break. Kept consistent
/// with <see cref="Candidates"/> by <c>FlrigModeTokensTests</c>'s no-duplicate-token assertion (no
/// single token string appears in two different <see cref="RadioMode"/> candidate lists), which makes
/// a mechanical inversion well-defined in practice even though this map is written out by hand.
///
/// Narrower per-rig filter/bandwidth variants with no sideband/reversal information in the token
/// itself (e.g. <c>CW-N</c>, <c>AM-N</c>, <c>FM-N</c>/<c>FM-W</c>, <c>AM-D</c>) are deliberately left
/// unmapped -- their base mode (<c>CW</c>/<c>AM</c>/<c>FM</c>) is already a candidate and present on
/// nearly every surveyed driver, so write support isn't lost; on read they resolve to
/// <see cref="RadioMode.Unknown"/>, an accepted, documented simplification rather than a defect
/// (<see cref="RadioState"/> has no bandwidth field to distinguish them anyway; <c>AM-D</c> has no
/// closer-fitting <see cref="RadioMode"/> slot at all). Digital-voice/other-digital tokens (<c>PSK</c>/
/// <c>FSK</c>/<c>DV</c>/<c>C4FM</c>/<c>DIG</c> and their sideband-tagged variants) are folded into
/// <see cref="RadioMode.Data"/>/<see cref="RadioMode.DataR"/> for lack of a closer-fitting slot in
/// this project's backend-agnostic mode set -- a deliberate, documented simplification, not a bug.
///
/// Code-review round-2 correction: the Yaesu <c>PKT</c>/<c>DIG</c>/<c>USER-U</c>/<c>USER-L</c> family
/// and Icom <c>PSK-R</c> were missing from the initial driver survey -- confirmed present on real,
/// common SSTV rigs (FT-818/FT-857D/FTdx101D/Mark-V, IC-7600/7700/7851), where
/// <c>SetModeAsync(Data|Pkt)</c> would otherwise hard-fail despite the rig genuinely supporting a
/// digital mode under a token this table didn't recognize. <c>PKT-U</c>/<c>PKT-L</c>/<c>PKT(L)</c>/
/// <c>DIG</c>/<c>USER-U</c>/<c>USER-L</c> classification below is inferred from each token's position
/// in its driver's <c>modes_</c> array and sideband-char convention, not manually verified against
/// every listed rig's own manual -- same best-effort standard as the rest of this table.</summary>
internal static class FlrigModeTokens
{
    public static readonly IReadOnlyDictionary<RadioMode, IReadOnlyList<string>> Candidates =
        new Dictionary<RadioMode, IReadOnlyList<string>>
        {
            [RadioMode.Usb] = ["USB"],
            [RadioMode.Lsb] = ["LSB"],
            [RadioMode.Am] = ["AM"],
            [RadioMode.Fm] = ["FM"],
            [RadioMode.Cw] = ["CW", "CW-U", "CWU", "CW500"],
            [RadioMode.CwR] = ["CW-R", "CWR", "CW-L", "CWL"],
            [RadioMode.Rtty] = ["RTTY", "RTTY-U"],
            [RadioMode.RttyR] = ["RTTY-R", "RTTYR", "RTTY-L"],
            [RadioMode.Data] =
                ["DATA", "USB-D", "DATA-U", "D-USB", "USB-D1", "USB-D2", "USB-D3", "PSK", "PSK-U", "FSK", "DV", "C4FM", "PKT-U", "DIG", "USER-U"],
            [RadioMode.DataR] =
                ["DATA-R", "LSB-D", "DATA-L", "D-LSB", "LSB-D1", "LSB-D2", "LSB-D3", "PSK-L", "FSK-R", "DV-R", "PKT-L", "PKT(L)", "USER-L", "PSK-R"],
            [RadioMode.Pkt] = ["FM-D", "D-FM", "DATA-FM", "FM-D1", "FM-D2", "FM-D3", "DATA-FMN", "PKT", "PKT-FM", "PKT(FM)"],
        };

    public static readonly IReadOnlyDictionary<string, RadioMode> TokenToMode =
        new Dictionary<string, RadioMode>(StringComparer.Ordinal)
        {
            ["USB"] = RadioMode.Usb,
            ["LSB"] = RadioMode.Lsb,
            ["AM"] = RadioMode.Am,
            ["FM"] = RadioMode.Fm,
            ["CW"] = RadioMode.Cw,
            ["CW-U"] = RadioMode.Cw,
            ["CWU"] = RadioMode.Cw,
            ["CW500"] = RadioMode.Cw,
            ["CW-R"] = RadioMode.CwR,
            ["CWR"] = RadioMode.CwR,
            ["CW-L"] = RadioMode.CwR,
            ["CWL"] = RadioMode.CwR,
            ["RTTY"] = RadioMode.Rtty,
            ["RTTY-U"] = RadioMode.Rtty,
            ["RTTY-R"] = RadioMode.RttyR,
            ["RTTYR"] = RadioMode.RttyR,
            ["RTTY-L"] = RadioMode.RttyR,
            ["DATA"] = RadioMode.Data,
            ["USB-D"] = RadioMode.Data,
            ["DATA-U"] = RadioMode.Data,
            ["D-USB"] = RadioMode.Data,
            ["USB-D1"] = RadioMode.Data,
            ["USB-D2"] = RadioMode.Data,
            ["USB-D3"] = RadioMode.Data,
            ["PSK"] = RadioMode.Data,
            ["PSK-U"] = RadioMode.Data,
            ["FSK"] = RadioMode.Data,
            ["DV"] = RadioMode.Data,
            ["C4FM"] = RadioMode.Data,
            ["PKT-U"] = RadioMode.Data,
            ["DIG"] = RadioMode.Data,
            ["USER-U"] = RadioMode.Data,
            ["DATA-R"] = RadioMode.DataR,
            ["LSB-D"] = RadioMode.DataR,
            ["DATA-L"] = RadioMode.DataR,
            ["D-LSB"] = RadioMode.DataR,
            ["LSB-D1"] = RadioMode.DataR,
            ["LSB-D2"] = RadioMode.DataR,
            ["LSB-D3"] = RadioMode.DataR,
            ["PSK-L"] = RadioMode.DataR,
            ["FSK-R"] = RadioMode.DataR,
            ["DV-R"] = RadioMode.DataR,
            ["PKT-L"] = RadioMode.DataR,
            ["PKT(L)"] = RadioMode.DataR,
            ["USER-L"] = RadioMode.DataR,
            ["PSK-R"] = RadioMode.DataR,
            ["FM-D"] = RadioMode.Pkt,
            ["D-FM"] = RadioMode.Pkt,
            ["DATA-FM"] = RadioMode.Pkt,
            ["FM-D1"] = RadioMode.Pkt,
            ["FM-D2"] = RadioMode.Pkt,
            ["FM-D3"] = RadioMode.Pkt,
            ["DATA-FMN"] = RadioMode.Pkt,
            ["PKT"] = RadioMode.Pkt,
            ["PKT-FM"] = RadioMode.Pkt,
            ["PKT(FM)"] = RadioMode.Pkt,
        };
}
