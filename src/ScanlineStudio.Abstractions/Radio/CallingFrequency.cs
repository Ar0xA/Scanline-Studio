namespace ScanlineStudio.Abstractions.Radio;

/// <summary>Which picture traffic a <see cref="CallingFrequency"/> carries: analog SSTV, digital SSTV (DRM/EasyPal),
/// or both.</summary>
[Flags]
public enum CallingFrequencyKind
{
    Sstv = 1,
    Drm = 2,
}

/// <summary>Where a <see cref="CallingFrequency"/> is used, when its source says it is region-specific.</summary>
[Flags]
public enum CallingFrequencyRegion
{
    None = 0,
    Eu = 1,
    Us = 2,
    Au = 4,
}

/// <summary>A note shown beside a <see cref="CallingFrequency"/>.</summary>
[Flags]
public enum CallingFrequencyTag
{
    None = 0,

    /// <summary>The ISS (ARISS) SSTV downlink: receive only.</summary>
    Iss = 1,
}

/// <summary>One popular SSTV or DRM calling frequency, in the sideband its source gives.</summary>
/// <param name="FrequencyHz">Dial frequency.</param>
/// <param name="Mode">Sideband or FM, as published; SSB-as-PKT mapping happens when it is applied.</param>
/// <param name="Kind">Analog SSTV, DRM, or both.</param>
/// <param name="Region">Region label, <see cref="CallingFrequencyRegion.None"/> when the frequency is not region-specific.</param>
/// <param name="Tag">Extra note.</param>
/// <param name="Band">Amateur band name ("20 m"); a unit, the same in every language.</param>
public sealed record CallingFrequency(
    long FrequencyHz, RadioMode Mode, CallingFrequencyKind Kind, CallingFrequencyRegion Region, CallingFrequencyTag Tag, string Band)
{
    /// <summary>Filter width for DRM, which needs more than the 2400 Hz SSB fallback.</summary>
    public const int DrmBandwidthHz = 2800;

    /// <summary>Filter width to request: <see cref="DrmBandwidthHz"/> on any row that carries DRM (a 2.5 kHz DRM signal
    /// occupies about 380-2760 Hz), else <see langword="null"/> for the mode family's fallback.</summary>
    public int? BandwidthHz => Kind.HasFlag(CallingFrequencyKind.Drm) ? DrmBandwidthHz : null;
}

/// <summary>The built-in calling-frequency list behind the header's calling-frequency menu.</summary>
/// <remarks>Sources, fetched 2026-10-17: P = pd2tx.com/other-stuff/sstv-freq, V = vk6ysf.com/sstvfrequ.htm,
/// W = the "Frequencies" table of en.wikipedia.org/wiki/Slow-scan_television. Values and sidebands only; "digital" in a
/// source is DRM. Sorted by frequency, USB before FM at the same frequency.</remarks>
public static class CallingFrequencies
{
    private const CallingFrequencyKind Sstv = CallingFrequencyKind.Sstv;
    private const CallingFrequencyKind Drm = CallingFrequencyKind.Drm;
    private const CallingFrequencyKind Both = CallingFrequencyKind.Sstv | CallingFrequencyKind.Drm;
    private const CallingFrequencyRegion Any = CallingFrequencyRegion.None;
    private const CallingFrequencyRegion Eu = CallingFrequencyRegion.Eu;
    private const CallingFrequencyRegion Us = CallingFrequencyRegion.Us;
    private const CallingFrequencyRegion Au = CallingFrequencyRegion.Au;
    private const CallingFrequencyTag NoTag = CallingFrequencyTag.None;

    public static IReadOnlyList<CallingFrequency> All { get; } =
    [
        new(1_890_000, RadioMode.Lsb, Sstv, Any, NoTag, "160 m"), // P
        new(1_924_000, RadioMode.Lsb, Sstv, Any, NoTag, "160 m"), // P
        new(3_640_000, RadioMode.Lsb, Sstv, Au, NoTag, "80 m"), // V
        new(3_730_000, RadioMode.Lsb, Sstv, Eu, NoTag, "80 m"), // P, W
        new(3_733_000, RadioMode.Lsb, Drm, Eu, NoTag, "80 m"), // P
        new(3_845_000, RadioMode.Lsb, Sstv, Us, NoTag, "80 m"), // P, W; US: outside the Region 1 and VK 80 m band
        new(7_033_000, RadioMode.Lsb, Sstv, Any, NoTag, "40 m"), // P
        new(7_040_000, RadioMode.Lsb, Sstv, Eu, NoTag, "40 m"), // P
        new(7_043_000, RadioMode.Lsb, Sstv, Any, NoTag, "40 m"), // V (no sideband given; LSB as every source below 10 MHz)
        new(7_058_000, RadioMode.Lsb, Drm, Eu, NoTag, "40 m"), // P
        new(7_165_000, RadioMode.Lsb, Sstv, Eu, NoTag, "40 m"), // W
        new(7_170_000, RadioMode.Lsb, Sstv, Au | Us, NoTag, "40 m"), // V
        new(7_171_000, RadioMode.Lsb, Both, Any, NoTag, "40 m"), // P (analogue/digital), W (analog)
        new(7_181_000, RadioMode.Lsb, Sstv, Us, NoTag, "40 m"), // W
        new(7_214_000, RadioMode.Lsb, Drm, Au, NoTag, "40 m"), // W
        new(10_132_000, RadioMode.Usb, Sstv, Any, NoTag, "30 m"), // P, V
        new(14_227_000, RadioMode.Usb, Sstv, Any, NoTag, "20 m"), // W
        new(14_230_000, RadioMode.Usb, Sstv, Any, NoTag, "20 m"), // P, V, W
        new(14_233_000, RadioMode.Usb, Both, Any, NoTag, "20 m"), // P, V (digital), W (analog)
        new(14_236_000, RadioMode.Usb, Sstv, Any, NoTag, "20 m"), // V
        new(14_240_000, RadioMode.Usb, Sstv, Eu, NoTag, "20 m"), // P, V
        new(18_117_000, RadioMode.Usb, Sstv, Any, NoTag, "17 m"), // P
        new(21_337_000, RadioMode.Usb, Drm, Any, NoTag, "15 m"), // P
        new(21_340_000, RadioMode.Usb, Sstv, Any, NoTag, "15 m"), // P, V, W
        new(24_927_000, RadioMode.Usb, Sstv, Any, NoTag, "12 m"), // P
        new(24_940_000, RadioMode.Usb, Sstv, Any, NoTag, "12 m"), // P
        new(28_260_000, RadioMode.Usb, Sstv, Any, NoTag, "10 m"), // P
        new(28_677_000, RadioMode.Usb, Sstv, Any, NoTag, "10 m"), // P
        new(28_680_000, RadioMode.Usb, Sstv, Any, NoTag, "10 m"), // V, W
        new(50_680_000, RadioMode.Fm, Sstv, Any, NoTag, "6 m"), // P
        new(50_950_000, RadioMode.Usb, Sstv, Any, NoTag, "6 m"), // P, V
        new(144_550_000, RadioMode.Usb, Sstv, Any, NoTag, "2 m"), // P
        new(145_500_000, RadioMode.Fm, Sstv, Any, NoTag, "2 m"), // P
        new(145_550_000, RadioMode.Usb, Sstv, Any, NoTag, "2 m"), // P
        new(145_600_000, RadioMode.Fm, Sstv, Any, NoTag, "2 m"), // P
        new(145_625_000, RadioMode.Usb, Sstv, Au, NoTag, "2 m"), // V (USB & FM)
        new(145_625_000, RadioMode.Fm, Sstv, Au, NoTag, "2 m"), // V (USB & FM)
        new(145_800_000, RadioMode.Fm, Sstv, Any, CallingFrequencyTag.Iss, "2 m"), // P
        new(430_950_000, RadioMode.Usb, Sstv, Any, NoTag, "70 cm"), // P
        new(433_775_000, RadioMode.Usb, Sstv, Au, NoTag, "70 cm"), // V (USB & FM)
        new(433_775_000, RadioMode.Fm, Sstv, Au, NoTag, "70 cm"), // V (USB & FM)
    ];
}
