using CommunityToolkit.Mvvm.ComponentModel;
using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.UI.Settings;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>One named DRM profile in Options › Station. Carries its own option lists so the item template needs
/// no ancestor binding. An empty <see cref="Name"/> saves as null, which shows the localized default name.</summary>
public sealed partial class DrmProfileEditorViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;

    public DrmProfileEditorViewModel(int index, ILocalizationService localization)
    {
        Index = index;
        _localization = localization;
        Apply(DrmSettingsDefaults.DefaultProfile(index), name: null);
    }

    public int Index { get; }

    /// <summary>The localized default name, shown as the watermark of an empty name.</summary>
    public string DefaultName => _localization.GetString(DrmSettingsDefaults.DefaultNameKeys[Index]);

    /// <summary>The per-profile heading, numbered from 1.</summary>
    public string Heading => _localization.GetString("Options.Drm.ProfileHeadingFormat", Index + 1);

    [ObservableProperty]
    private string? _name;

    private int _modeIndex;

    public int ModeIndex
    {
        get => _modeIndex;
        set => SetIndex(ref _modeIndex, value, Enum.GetValues<DrmRobustnessMode>().Length);
    }

    private int _bandwidthIndex;

    public int BandwidthIndex
    {
        get => _bandwidthIndex;
        set => SetIndex(ref _bandwidthIndex, value, Enum.GetValues<DrmBandwidth>().Length);
    }

    private int _qamIndex;

    public int QamIndex
    {
        get => _qamIndex;
        set => SetIndex(ref _qamIndex, value, Enum.GetValues<DrmQamOrder>().Length);
    }

    private int _protectionIndex;

    public int ProtectionIndex
    {
        get => _protectionIndex;
        set => SetIndex(ref _protectionIndex, value, Enum.GetValues<DrmProtectionLevel>().Length);
    }

    private int _interleaveIndex;

    public int InterleaveIndex
    {
        get => _interleaveIndex;
        set => SetIndex(ref _interleaveIndex, value, Enum.GetValues<DrmInterleaving>().Length);
    }

    private int _reedSolomonIndex;

    public int ReedSolomonIndex
    {
        get => _reedSolomonIndex;
        set => SetIndex(ref _reedSolomonIndex, value, Enum.GetValues<DrmReedSolomonLevel>().Length);
    }

    public IReadOnlyList<string> ModeOptions => DrmChoiceLabels.Modes(_localization);

    public IReadOnlyList<string> BandwidthOptions => DrmChoiceLabels.Bandwidths(_localization);

    public IReadOnlyList<string> QamOptions => DrmChoiceLabels.QamOrders(_localization);

    public IReadOnlyList<string> ProtectionOptions => DrmChoiceLabels.Protections(_localization);

    public IReadOnlyList<string> InterleaveOptions => DrmChoiceLabels.Interleaves(_localization);

    public IReadOnlyList<string> ReedSolomonOptions => DrmChoiceLabels.ReedSolomonLevels(_localization);

    /// <summary>Re-reads the localized default name and option lists after a language switch.</summary>
    public void RefreshLocalizedText() => OnPropertyChanged(string.Empty);

    public void Apply(DrmProfile profile, string? name)
    {
        Name = name;
        ModeIndex = Array.IndexOf(Enum.GetValues<DrmRobustnessMode>(), profile.Mode);
        BandwidthIndex = Array.IndexOf(Enum.GetValues<DrmBandwidth>(), profile.Bandwidth);
        QamIndex = Array.IndexOf(Enum.GetValues<DrmQamOrder>(), profile.Qam);
        ProtectionIndex = Array.IndexOf(Enum.GetValues<DrmProtectionLevel>(), profile.Protection);
        InterleaveIndex = Array.IndexOf(Enum.GetValues<DrmInterleaving>(), profile.Interleave);
        ReedSolomonIndex = Array.IndexOf(Enum.GetValues<DrmReedSolomonLevel>(), profile.ReedSolomon);
    }

    /// <summary>The stored form. The index setters only ever hold in-range values.</summary>
    public DrmProfileSettings ToSettings()
    {
        var profile = new DrmProfile(
            Enum.GetValues<DrmRobustnessMode>()[ModeIndex],
            Enum.GetValues<DrmBandwidth>()[BandwidthIndex],
            Enum.GetValues<DrmQamOrder>()[QamIndex],
            Enum.GetValues<DrmProtectionLevel>()[ProtectionIndex],
            Enum.GetValues<DrmInterleaving>()[InterleaveIndex],
            Enum.GetValues<DrmReedSolomonLevel>()[ReedSolomonIndex]);
        return DrmProfileSettings.From(profile, string.IsNullOrWhiteSpace(Name) ? null : Name.Trim());
    }

    // A ComboBox reports -1 while its items are replaced; keep the value and re-raise so the view snaps back.
    private void SetIndex(ref int field, int value, int count, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (value < 0 || value >= count)
        {
            OnPropertyChanged(propertyName);
            return;
        }

        SetProperty(ref field, value, propertyName);
    }
}
