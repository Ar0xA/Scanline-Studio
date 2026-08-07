using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Logbook;

/// <summary>Shared <see cref="RadioMode"/> &lt;-&gt; ADIF <c>MODE</c> token mapping used by both
/// <see cref="AdifExporter"/> and <see cref="AdifImporter"/>, kept in one place so the two
/// directions can't drift apart. ADIF has no reverse-sideband concept, so <see cref="RadioMode.CwR"/>/
/// <see cref="RadioMode.RttyR"/>/<see cref="RadioMode.DataR"/> deliberately collapse onto their
/// non-reversed sibling's token on export — a real, one-way lossy step, not a bug.</summary>
internal static class AdifRadioModeMapping
{
    public static string? ToAdif(RadioMode mode) => mode switch
    {
        RadioMode.Usb => "USB",
        RadioMode.Lsb => "LSB",
        RadioMode.Cw or RadioMode.CwR => "CW",
        RadioMode.Am => "AM",
        RadioMode.Fm => "FM",
        RadioMode.Rtty or RadioMode.RttyR => "RTTY",
        RadioMode.Data or RadioMode.DataR => "DATA",
        RadioMode.Pkt => "PKTUSB",
        _ => null,
    };

    public static RadioMode FromAdif(string adifMode) => adifMode.ToUpperInvariant() switch
    {
        "USB" => RadioMode.Usb,
        "LSB" => RadioMode.Lsb,
        "CW" => RadioMode.Cw,
        "AM" => RadioMode.Am,
        "FM" => RadioMode.Fm,
        "RTTY" => RadioMode.Rtty,
        "DATA" => RadioMode.Data,
        "PKTUSB" or "PKT" => RadioMode.Pkt,
        _ => RadioMode.Unknown,
    };
}
