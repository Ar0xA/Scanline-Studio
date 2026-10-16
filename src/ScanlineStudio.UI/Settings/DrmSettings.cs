using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Settings;

/// <summary>DRM transmit settings (section <c>Drm</c>). Every field is nullable: a missing key, or an
/// out-of-range enum value, resolves to its <see cref="DrmSettingsDefaults"/> value at read time.
/// Two writers each update only their own fields with <c>previous with { … }</c>: the TX pane owns
/// <see cref="ActiveProfileIndex"/>, <see cref="SizePreset"/>, <see cref="TargetKb"/> and <see cref="Custom"/>;
/// Options owns <see cref="Profiles"/>, <see cref="VoxTone"/> and <see cref="LevelOffsetDb"/>.</summary>
public sealed record DrmSettings
{
    public const string SectionKey = "Drm";

    /// <summary>The three named profiles, Robust/Normal/Fast by default.</summary>
    public IReadOnlyList<DrmProfileSettings?>? Profiles { get; init; }

    /// <summary>The "Custom" values, written by the TX pane when a transmission field is edited.</summary>
    public DrmProfileSettings? Custom { get; init; }

    /// <summary>0-2 a named profile, 3 Custom (<see cref="DrmSettingsDefaults.CustomProfileIndex"/>).</summary>
    public int? ActiveProfileIndex { get; init; }

    public DrmSizePreset? SizePreset { get; init; }

    /// <summary>Target JPEG 2000 size in KB (×1000 bytes, as QSSTV, repeaterconfig.cpp:58), 4-100.</summary>
    public int? TargetKb { get; init; }

    /// <summary>QSSTV's 1 s VOX tone before the DRM frames. Default off (txwidget.cpp:167).</summary>
    public bool? VoxTone { get; init; }

    /// <summary>Our own DRM drive offset in dB, −20..0.</summary>
    public double? LevelOffsetDb { get; init; }
}

/// <summary>One DRM profile as stored. A null or empty <see cref="Name"/> shows the localized default name.</summary>
public sealed record DrmProfileSettings
{
    public string? Name { get; init; }

    public DrmRobustnessMode? Mode { get; init; }

    public DrmBandwidth? Bandwidth { get; init; }

    public DrmQamOrder? Qam { get; init; }

    public DrmProtectionLevel? Protection { get; init; }

    public DrmInterleaving? Interleave { get; init; }

    public DrmReedSolomonLevel? ReedSolomon { get; init; }

    public static DrmProfileSettings From(DrmProfile profile, string? name = null) => new()
    {
        Name = name,
        Mode = profile.Mode,
        Bandwidth = profile.Bandwidth,
        Qam = profile.Qam,
        Protection = profile.Protection,
        Interleave = profile.Interleave,
        ReedSolomon = profile.ReedSolomon,
    };
}

/// <summary>The six transmission fields of a profile, resolved.</summary>
public sealed record DrmProfile(
    DrmRobustnessMode Mode,
    DrmBandwidth Bandwidth,
    DrmQamOrder Qam,
    DrmProtectionLevel Protection,
    DrmInterleaving Interleave,
    DrmReedSolomonLevel ReedSolomon);

/// <summary>Defaults and read-time validation for <see cref="DrmSettings"/>.</summary>
public static class DrmSettingsDefaults
{
    public const int ProfileCount = 3;

    public const int CustomProfileIndex = 3;

    public const int DefaultActiveProfileIndex = 1;

    public const DrmSizePreset SizePreset = DrmSizePreset.Size640x480;

    public const int MinTargetKb = 4;

    public const int MaxTargetKb = 100;

    public const int DefaultTargetKb = 20;

    public const bool DefaultVoxTone = false;

    public const double DefaultLevelOffsetDb = 0;

    public static readonly DrmProfile Robust = new(
        DrmRobustnessMode.B, DrmBandwidth.Hz2500, DrmQamOrder.Qam4, DrmProtectionLevel.High, DrmInterleaving.LongInterleaver, DrmReedSolomonLevel.Rs2);

    public static readonly DrmProfile Normal = new(
        DrmRobustnessMode.B, DrmBandwidth.Hz2500, DrmQamOrder.Qam16, DrmProtectionLevel.Low, DrmInterleaving.LongInterleaver, DrmReedSolomonLevel.Rs1);

    public static readonly DrmProfile Fast = new(
        DrmRobustnessMode.E, DrmBandwidth.Hz2500, DrmQamOrder.Qam64, DrmProtectionLevel.Low, DrmInterleaving.ShortInterleaver, DrmReedSolomonLevel.None);

    /// <summary>Custom starts as Normal.</summary>
    public static DrmProfile CustomDefault => Normal;

    /// <summary>Localization keys of the default profile names, by index (0-2 named, 3 Custom).</summary>
    public static readonly IReadOnlyList<string> DefaultNameKeys =
    [
        "Drm.Profile.Robust", "Drm.Profile.Normal", "Drm.Profile.Fast", "Drm.Profile.Custom",
    ];

    public static DrmProfile DefaultProfile(int index) => index switch
    {
        0 => Robust,
        2 => Fast,
        CustomProfileIndex => CustomDefault,
        _ => Normal,
    };

    /// <summary>Each field falls back to <paramref name="fallback"/>'s when missing or not a defined enum value.</summary>
    public static DrmProfile Resolve(DrmProfileSettings? stored, DrmProfile fallback) => new(
        Valid(stored?.Mode, fallback.Mode),
        Valid(stored?.Bandwidth, fallback.Bandwidth),
        Valid(stored?.Qam, fallback.Qam),
        Valid(stored?.Protection, fallback.Protection),
        Valid(stored?.Interleave, fallback.Interleave),
        Valid(stored?.ReedSolomon, fallback.ReedSolomon));

    /// <summary>Named profile <paramref name="index"/> (0-2), or Custom (3).</summary>
    public static DrmProfile ResolveProfile(DrmSettings? settings, int index) => index == CustomProfileIndex
        ? Resolve(settings?.Custom, CustomDefault)
        : Resolve(StoredProfile(settings, index), DefaultProfile(index));

    public static string? StoredProfileName(DrmSettings? settings, int index) =>
        StoredProfile(settings, index)?.Name is { Length: > 0 } name && !string.IsNullOrWhiteSpace(name) ? name : null;

    public static int ResolveActiveProfileIndex(DrmSettings? settings) =>
        settings?.ActiveProfileIndex is { } index and >= 0 and <= CustomProfileIndex ? index : DefaultActiveProfileIndex;

    public static DrmSizePreset ResolveSizePreset(DrmSettings? settings) => Valid(settings?.SizePreset, SizePreset);

    public static int ResolveTargetKb(DrmSettings? settings) =>
        settings?.TargetKb is { } kb ? Math.Clamp(kb, MinTargetKb, MaxTargetKb) : DefaultTargetKb;

    public static bool ResolveVoxTone(DrmSettings? settings) => settings?.VoxTone ?? DefaultVoxTone;

    public static double ResolveLevelOffsetDb(DrmSettings? settings) =>
        DrmTxSettings.ClampLevelOffsetDb(settings?.LevelOffsetDb ?? DefaultLevelOffsetDb);

    private static DrmProfileSettings? StoredProfile(DrmSettings? settings, int index) =>
        settings?.Profiles is { } profiles && index >= 0 && index < profiles.Count ? profiles[index] : null;

    private static T Valid<T>(T? value, T fallback)
        where T : struct, Enum => value is { } v && Enum.IsDefined(v) ? v : fallback;
}
