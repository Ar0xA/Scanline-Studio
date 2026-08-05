using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Application;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>The first `Window`/dialog-backed view-model in the app (every other view-model so far
/// backs a dockable `Tool` pane) -- holds one editable in-memory copy of every settings field it
/// covers, loaded via <see cref="OptionsSettingsService"/> on construction (never the concrete
/// per-module settings-section types directly -- see that service's own doc comment for why),
/// committed back only on <see cref="SaveCommand"/>; <see cref="CancelCommand"/> discards every
/// edit by simply closing without saving. Per-section resets are a single click each (nothing is
/// persisted until Save, so an accidental reset costs nothing); the global "reset ALL" is the one
/// genuinely destructive action here and requires an explicit confirm step.
///
/// Radio/CAT offers None/rigctld/Hamlib -- all three <c>IRadioProtocolFactory</c> backends are
/// registered in DI (spec/14-roadmap.md's Piece 3).</summary>
public sealed partial class OptionsWindowViewModel : ViewModelBase
{
    private readonly OptionsSettingsService _optionsSettingsService;
    private readonly ILocalizationService _localization;
    private readonly IAudioDeviceEnumerator _audioDeviceEnumerator;

    [ObservableProperty]
    private CultureInfo? _selectedCulture;

    [ObservableProperty]
    private AudioDeviceInfo? _selectedCaptureDevice;

    [ObservableProperty]
    private AudioDeviceInfo? _selectedPlaybackDevice;

    [ObservableProperty]
    private int _sampleRate = 11025;

    [ObservableProperty]
    private string _radioBackendId = "none";

    [ObservableProperty]
    private string? _rigctldHost;

    [ObservableProperty]
    private int? _rigctldPort;

    [ObservableProperty]
    private uint? _hamlibModel;

    [ObservableProperty]
    private string? _hamlibSerialPort;

    [ObservableProperty]
    private int? _hamlibBaudRate;

    [ObservableProperty]
    private string? _hamlibPttType;

    [ObservableProperty]
    private string? _callsign;

    [ObservableProperty]
    private bool _isConfirmingResetAll;

    public OptionsWindowViewModel(
        OptionsSettingsService optionsSettingsService,
        ILocalizationService localization,
        IAudioDeviceEnumerator audioDeviceEnumerator)
    {
        _optionsSettingsService = optionsSettingsService;
        _localization = localization;
        _audioDeviceEnumerator = audioDeviceEnumerator;

        _ = LoadAsync();
    }

    public IReadOnlyList<CultureInfo> AvailableCultures => _localization.AvailableCultures;

    public ObservableCollection<AudioDeviceInfo> CaptureDevices { get; } = [];

    public ObservableCollection<AudioDeviceInfo> PlaybackDevices { get; } = [];

    public bool IsRigctldSelected => RadioBackendId == "rigctld";

    /// <summary>Plain computed bool pair backing the Radio/CAT tab's two <c>RadioButton</c>s --
    /// Avalonia's own <c>StringConverters</c> has no "equals this parameter" converter, so this is
    /// simpler than writing a one-off converter class, and matches this ViewModel's existing
    /// <see cref="IsRigctldSelected"/> computed-property idiom.</summary>
    public bool IsNoneBackendSelected
    {
        get => RadioBackendId == "none";
        set
        {
            if (value)
            {
                RadioBackendId = "none";
            }
        }
    }

    public bool IsRigctldBackendSelected
    {
        get => RadioBackendId == "rigctld";
        set
        {
            if (value)
            {
                RadioBackendId = "rigctld";
            }
        }
    }

    public bool IsHamlibBackendSelected
    {
        get => RadioBackendId == "hamlib";
        set
        {
            if (value)
            {
                RadioBackendId = "hamlib";
            }
        }
    }

    public bool IsHamlibSelected => RadioBackendId == "hamlib";

    /// <summary>Fired on Save (after a successful persist) and on Cancel -- the View closes the
    /// window either way; it does not need to distinguish which.</summary>
    public event Action? RequestClose;

    private async Task LoadAsync()
    {
        var snapshot = await _optionsSettingsService.LoadAsync();
        ApplyFromSnapshot(snapshot);

        await _audioDeviceEnumerator.RefreshAsync();
        CaptureDevices.Clear();
        foreach (var device in _audioDeviceEnumerator.InputDevices)
        {
            CaptureDevices.Add(device);
        }

        PlaybackDevices.Clear();
        foreach (var device in _audioDeviceEnumerator.OutputDevices)
        {
            PlaybackDevices.Add(device);
        }

        SelectedCaptureDevice = CaptureDevices.FirstOrDefault(d => d.Id == snapshot.CaptureDeviceId);
        SelectedPlaybackDevice = PlaybackDevices.FirstOrDefault(d => d.Id == snapshot.PlaybackDeviceId);
    }

    private void ApplyFromSnapshot(OptionsSnapshot snapshot)
    {
        SelectedCulture = AvailableCultures.FirstOrDefault(c => c.Name == snapshot.CultureCode) ?? _localization.CurrentCulture;
        SampleRate = snapshot.SampleRate;
        RadioBackendId = snapshot.RadioBackendId is "none" or "rigctld" or "hamlib" ? snapshot.RadioBackendId : "none";
        RigctldHost = snapshot.RigctldHost;
        RigctldPort = snapshot.RigctldPort;
        HamlibModel = snapshot.HamlibModel;
        HamlibSerialPort = snapshot.HamlibSerialPort;
        HamlibBaudRate = snapshot.HamlibBaudRate;
        HamlibPttType = snapshot.HamlibPttType;
        Callsign = snapshot.Callsign;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var snapshot = new OptionsSnapshot(
            CultureCode: SelectedCulture?.Name,
            CaptureDeviceId: SelectedCaptureDevice?.Id,
            PlaybackDeviceId: SelectedPlaybackDevice?.Id,
            SampleRate: SampleRate,
            RadioBackendId: RadioBackendId,
            RigctldHost: RigctldHost,
            RigctldPort: RigctldPort,
            HamlibModel: HamlibModel,
            HamlibSerialPort: HamlibSerialPort,
            HamlibBaudRate: HamlibBaudRate,
            HamlibPttType: HamlibPttType,
            Callsign: Callsign);

        await _optionsSettingsService.SaveAsync(snapshot);

        if (SelectedCulture is { } culture && !culture.Equals(_localization.CurrentCulture))
        {
            await _localization.SetCultureAsync(culture);
        }

        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();

    [RelayCommand]
    private void ResetGeneralToDefault()
        => SelectedCulture = AvailableCultures.FirstOrDefault(c => c.Name == OptionsSettingsService.Defaults.CultureCode);

    [RelayCommand]
    private void ResetAudioToDefault()
    {
        var defaults = OptionsSettingsService.Defaults;
        SelectedCaptureDevice = CaptureDevices.FirstOrDefault(d => d.Id == defaults.CaptureDeviceId);
        SelectedPlaybackDevice = PlaybackDevices.FirstOrDefault(d => d.Id == defaults.PlaybackDeviceId);
        SampleRate = defaults.SampleRate;
    }

    [RelayCommand]
    private void ResetRadioToDefault()
    {
        var defaults = OptionsSettingsService.Defaults;
        RadioBackendId = defaults.RadioBackendId;
        RigctldHost = defaults.RigctldHost;
        RigctldPort = defaults.RigctldPort;
        HamlibModel = defaults.HamlibModel;
        HamlibSerialPort = defaults.HamlibSerialPort;
        HamlibBaudRate = defaults.HamlibBaudRate;
        HamlibPttType = defaults.HamlibPttType;
    }

    [RelayCommand]
    private void ResetTxToDefault() => Callsign = OptionsSettingsService.Defaults.Callsign;

    [RelayCommand]
    private void RequestResetAll() => IsConfirmingResetAll = true;

    [RelayCommand]
    private void ConfirmResetAll()
    {
        ResetGeneralToDefault();
        ResetAudioToDefault();
        ResetRadioToDefault();
        ResetTxToDefault();
        IsConfirmingResetAll = false;
    }

    [RelayCommand]
    private void CancelResetAll() => IsConfirmingResetAll = false;

    partial void OnRadioBackendIdChanged(string value)
    {
        OnPropertyChanged(nameof(IsRigctldSelected));
        OnPropertyChanged(nameof(IsHamlibSelected));
        OnPropertyChanged(nameof(IsNoneBackendSelected));
        OnPropertyChanged(nameof(IsRigctldBackendSelected));
        OnPropertyChanged(nameof(IsHamlibBackendSelected));
    }
}
