using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Logbook;

/// <summary>Shared <see cref="RadioMode"/> &lt;-&gt; ADIF <c>MODE</c>/<c>SUBMODE</c> token mapping
/// used by both <see cref="AdifExporter"/> and <see cref="AdifImporter"/>, kept in one place so the
/// two directions can't drift apart. Every emitted <see cref="ToAdif"/> token is a real ADIF 3.x Mode
/// enumeration value — <c>USB</c>/<c>LSB</c>/<c>DATA</c>/<c>PKTUSB</c> (the pre-fix tokens) are NOT
/// legal ADIF Mode values (<c>USB</c>/<c>LSB</c> are Submodes of <c>MODE=SSB</c>; <c>DATA</c> isn't
/// defined at all; ADIF's packet token is <c>PKT</c>, not Hamlib's <c>PKTUSB</c>) and third-party
/// loggers/LoTW/QRZ reject or mis-file a record carrying one of them.
///
/// <see cref="RadioMode.CwR"/>/<see cref="RadioMode.RttyR"/> deliberately collapse onto their
/// non-reversed sibling's token on export — a real, one-way lossy step, not a bug (ADIF has no
/// reverse-sideband concept for CW/RTTY). <see cref="RadioMode.Data"/>/<see cref="RadioMode.DataR"/>
/// (Hamlib/rigctld's generic rig-side "DATA" mode, carrying no sideband/protocol info of its own —
/// see <see cref="RadioMode"/>'s doc comment) has no ADIF equivalent either; it collapses onto bare
/// <c>MODE=SSB</c> with no <c>SUBMODE</c>, the same convention several third-party loggers use for an
/// unspecified rig DATA mode — also one-way lossy, not a bug.</summary>
internal static class AdifRadioModeMapping
{
    public static (string? Mode, string? Submode) ToAdif(RadioMode mode) => mode switch
    {
        RadioMode.Usb => ("SSB", "USB"),
        RadioMode.Lsb => ("SSB", "LSB"),
        RadioMode.Cw or RadioMode.CwR => ("CW", null),
        RadioMode.Am => ("AM", null),
        RadioMode.Fm => ("FM", null),
        RadioMode.Rtty or RadioMode.RttyR => ("RTTY", null),
        RadioMode.Data or RadioMode.DataR => ("SSB", null),
        RadioMode.Pkt => ("PKT", null),
        _ => (null, null),
    };

    /// <summary>Reverse mapping. <paramref name="adifSubmode"/> disambiguates <c>MODE=SSB</c> into
    /// <see cref="RadioMode.Usb"/>/<see cref="RadioMode.Lsb"/> — a missing or unrecognized submode
    /// defaults to <see cref="RadioMode.Usb"/> (the far more common sideband in practice), a
    /// documented one-way lossy default, not a crash. <c>PKTUSB</c> is accepted as an alias of the
    /// canonical <c>PKT</c> token for reading files this app itself wrote before this fix, and files
    /// from other Hamlib-based tools that still emit the non-standard token.
    ///
    /// <c>SubmodeRecognized</c> tells the caller whether <paramref name="adifSubmode"/> was actually
    /// USED to derive the result (i.e. it was literally <c>USB</c> or <c>LSB</c>) — <b>not</b> merely
    /// whether a submode string was present. A stray/unrecognized <c>SUBMODE</c> alongside
    /// <c>MODE=SSB</c> (or any other mode) must NOT be treated as consumed by the caller, so it still
    /// surfaces in the unmapped-fields notes bag instead of being silently discarded alongside the
    /// <see cref="RadioMode.Usb"/> default.</summary>
    public static (RadioMode Mode, bool SubmodeRecognized) FromAdif(string adifMode, string? adifSubmode = null)
    {
        if (adifMode.Equals("SSB", StringComparison.OrdinalIgnoreCase))
        {
            return adifSubmode?.ToUpperInvariant() switch
            {
                "LSB" => (RadioMode.Lsb, true),
                "USB" => (RadioMode.Usb, true),
                _ => (RadioMode.Usb, false),
            };
        }

        return adifMode.ToUpperInvariant() switch
        {
            "CW" => (RadioMode.Cw, false),
            "AM" => (RadioMode.Am, false),
            "FM" => (RadioMode.Fm, false),
            "RTTY" => (RadioMode.Rtty, false),
            "PKT" or "PKTUSB" => (RadioMode.Pkt, false),
            _ => (RadioMode.Unknown, false),
        };
    }
}
