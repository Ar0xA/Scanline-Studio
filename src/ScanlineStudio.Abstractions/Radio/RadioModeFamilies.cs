namespace ScanlineStudio.Abstractions.Radio;

/// <summary>Which filter widths make sense for a given <see cref="RadioMode"/>, and which width to
/// use when a <see cref="FrequencyPreset"/> carries none.
///
/// <para>This exists because a single app-wide default is wrong. Applying a Favourite sets the mode,
/// and a mode set asks the rig for its own default passband (Hamlib's <c>RIG_PASSBAND_NORMAL</c>) —
/// which on some rigs resolves to 500 Hz and destroys SSTV reception. Applying an explicit width
/// fixes that, but one fixed width cannot serve both families: SSTV tones occupy roughly
/// 1200-2300 Hz and fit a 2400 Hz SSB filter, while FM SSTV needs a filter an order of magnitude
/// wider. A single 2400 Hz default would break FM the same way 500 Hz breaks SSB.</para>
///
/// <para><b>The plausible band and the persistence validation range are different things.</b> The
/// band here drives ONLY the snap-on-mode-change behaviour in the Favourites editor: change a row
/// from USB to FM and its width follows, because 2400 is outside the FM band. It must never gate the
/// apply path — a width the operator deliberately typed and saved is applied as typed, and is
/// checked only against the wider persistence range, so a 12 kHz filter on USB stays a 12 kHz filter
/// on USB.</para>
///
/// <para>The bands are half-open at <see cref="FamilyBoundaryHz"/>. With both written inclusive, a
/// value of exactly 4000 Hz would satisfy neither family's snap trigger, so USB to FM would leave
/// 4000 Hz (far too narrow for FM) and FM to USB would leave 4000 Hz (very wide for SSB).</para>
///
/// <para>Modes outside the two families — <see cref="RadioMode.Am"/>, the CW and RTTY pairs, and
/// <see cref="RadioMode.Unknown"/> — deliberately have NO family. They are unreachable from the
/// Favourites editor's own mode picker and from "Store current", but a hand-edited settings file can
/// still carry one, so every caller must handle <see langword="null"/> rather than assume a family
/// exists.</para></summary>
public static class RadioModeFamilies
{
    /// <summary>Which family <paramref name="mode"/> belongs to, or <see langword="null"/> for a mode
    /// this app has no filter-width opinion about. Lets a caller switch on the family once instead of
    /// repeating the member list.</summary>
    public static RadioModeFamily? FamilyOf(RadioMode mode) => mode switch
    {
        RadioMode.Usb or RadioMode.Lsb or RadioMode.Data or RadioMode.DataR => RadioModeFamily.Ssb,
        RadioMode.Fm or RadioMode.Pkt => RadioModeFamily.Fm,
        _ => null,
    };

    /// <summary>Half-open split between the two families: the SSB band ends below this value, the FM
    /// band starts at it.</summary>
    public const int FamilyBoundaryHz = 4000;

    /// <summary>Narrowest width the persistence layer accepts. The narrowest real roofing filter on
    /// common HF rigs. Anything below this is a typo, not a filter.</summary>
    public const int MinValidBandwidthHz = 50;

    /// <summary>Widest width the persistence layer accepts, matching the figure the Transceiver
    /// header's own bandwidth control already uses as "realistically wide enough to matter".</summary>
    public const int MaxValidBandwidthHz = 20_000;

    private static readonly int[] SsbPresets = [1800, 2400, 2800];
    private static readonly int[] FmPresets = [9000, 12_000, 15_000];

    /// <summary>Width a preset with no stored bandwidth resolves to on an SSB or data mode. Also the
    /// seed for a Favourites-editor row whose mode has no family at all, so that a row the operator
    /// never touched can never hold a value <see cref="IsValidBandwidth"/> rejects and block a
    /// save.</summary>
    public const int SsbFallbackHz = 2400;

    /// <summary>Width a preset with no stored bandwidth resolves to on an FM mode.</summary>
    public const int FmFallbackHz = 15_000;

    /// <summary>The quick-pick list for <paramref name="mode"/>, or <see langword="null"/> when the
    /// mode has no family.</summary>
    public static IReadOnlyList<int>? PresetsFor(RadioMode mode) => mode switch
    {
        RadioMode.Usb or RadioMode.Lsb or RadioMode.Data or RadioMode.DataR => SsbPresets,
        RadioMode.Fm or RadioMode.Pkt => FmPresets,
        _ => null,
    };

    /// <summary>The width a preset on <paramref name="mode"/> uses when it stores none, or
    /// <see langword="null"/> when the mode has no family. A <see langword="null"/> result means
    /// "send no bandwidth command at all" on the apply path: there is no defensible width to invent
    /// for a mode this app does not support.</summary>
    public static int? FallbackFor(RadioMode mode) => mode switch
    {
        RadioMode.Usb or RadioMode.Lsb or RadioMode.Data or RadioMode.DataR => SsbFallbackHz,
        RadioMode.Fm or RadioMode.Pkt => FmFallbackHz,
        _ => null,
    };

    /// <summary>Whether <paramref name="bandwidthHz"/> already suits <paramref name="mode"/>'s own
    /// family. Drives the snap-on-mode-change behaviour only — see this type's own doc comment for
    /// why this must never gate the apply path. A mode with no family returns
    /// <see langword="true"/>, so nothing ever snaps a width the app has no opinion about.</summary>
    public static bool IsInFamilyBand(RadioMode mode, double bandwidthHz) => mode switch
    {
        RadioMode.Usb or RadioMode.Lsb or RadioMode.Data or RadioMode.DataR =>
            bandwidthHz >= MinValidBandwidthHz && bandwidthHz < FamilyBoundaryHz,
        RadioMode.Fm or RadioMode.Pkt =>
            bandwidthHz >= FamilyBoundaryHz && bandwidthHz <= MaxValidBandwidthHz,
        _ => true,
    };

    /// <summary>Whether <paramref name="bandwidthHz"/> is a filter width at all, independent of mode.
    /// Deliberately wider than either family band. 0 is rejected explicitly: it is Hamlib's
    /// <c>RIG_PASSBAND_NORMAL</c> sentinel, so persisting or sending it would silently reinstate the
    /// rig-picks-its-own-default behaviour this whole feature exists to replace.</summary>
    public static bool IsValidBandwidth(double bandwidthHz) =>
        double.IsFinite(bandwidthHz)
        && bandwidthHz >= MinValidBandwidthHz
        && bandwidthHz <= MaxValidBandwidthHz;

    /// <summary>The six modes the Favourites editor offers, in display order. Everything else is
    /// either not used for SSTV (AM, CW, RTTY) or not a settable mode at all
    /// (<see cref="RadioMode.Unknown"/>, a readback sentinel with no entry in any backend's mode
    /// map).</summary>
    public static IReadOnlyList<RadioMode> PresetModes { get; } =
    [
        RadioMode.Usb,
        RadioMode.Lsb,
        RadioMode.Fm,
        RadioMode.Data,
        RadioMode.DataR,
        RadioMode.Pkt,
    ];
}
