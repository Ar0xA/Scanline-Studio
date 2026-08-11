using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<OptionsWindowViewModel> _logger;

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
    private string? _operatorName;

    [ObservableProperty]
    private string? _operatorGrid;

    [ObservableProperty]
    private bool _isConfirmingResetAll;

    /// <summary>Backs the Decode tab's real toggles -- see <see cref="ScanlineStudio.Core.Sstv.SstvDecoderSettings.AutoSyncEnabled"/>/
    /// <see cref="ScanlineStudio.Core.Sstv.SstvDecoderSettings.AutoSlantEnabled"/>'s own doc comments
    /// for what each genuinely gates in <c>AnalogFmSstvDecoder</c>. Deliberately NOT the tab's other
    /// two checkboxes (Auto-stop/Auto-restart) -- <c>Options.Decode.AutoStop</c>'s loc text ("Auto-stop
    /// when sync looks stable") doesn't match what <c>AutoStopEnabled</c> actually gates (stops on
    /// erratic/weak signal, not stable sync) and <c>AutoRestart</c>'s wording is a similar mismatch
    /// against <c>SyncRestartEnabled</c>'s real semantics -- left STUB pending a wording fix, not
    /// wired here to avoid shipping a control that lies about what it does. Takes effect on next
    /// app restart, same as every other Options-dialog setting baked into a DI singleton at startup
    /// (Radio backend, Audio device, etc. -- no live-reconfiguration path exists for any of them).</summary>
    [ObservableProperty]
    private bool _autoSyncEnabled = true;

    [ObservableProperty]
    private bool _autoSlantEnabled = true;

    public OptionsWindowViewModel(
        OptionsSettingsService optionsSettingsService,
        ILocalizationService localization,
        IAudioDeviceEnumerator audioDeviceEnumerator,
        ILogger<OptionsWindowViewModel> logger)
    {
        _optionsSettingsService = optionsSettingsService;
        _localization = localization;
        _audioDeviceEnumerator = audioDeviceEnumerator;
        _logger = logger;

        _ = LoadSafeAsync();
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

    /// <summary>Unguarded fire-and-forget from the constructor before this wrap was added -- the
    /// audio enumerator's <c>RefreshAsync</c> call can throw, which used to mean the Options dialog
    /// could open completely blank with no explanation anywhere.</summary>
    private async Task LoadSafeAsync()
    {
        try
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
        catch (Exception ex)
        {
            Log.LoadFailed(_logger, ex);
        }
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
        OperatorName = snapshot.OperatorName;
        OperatorGrid = snapshot.OperatorGrid;
        AutoSyncEnabled = snapshot.AutoSyncEnabled;
        AutoSlantEnabled = snapshot.AutoSlantEnabled;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        // The single most useful Debug line in the app for "why didn't my settings take effect"
        // bugs -- deliberately omits nothing secret-shaped exists in this snapshot today (host/port/
        // device ids/sample rate/culture/backend id are all safe to log as-is).
        Log.SaveInvoked(_logger, RadioBackendId, SampleRate, SelectedCulture?.Name);

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
            Callsign: Callsign,
            OperatorName: OperatorName,
            OperatorGrid: OperatorGrid,
            AutoSyncEnabled: AutoSyncEnabled,
            AutoSlantEnabled: AutoSlantEnabled);

        try
        {
            await _optionsSettingsService.SaveAsync(snapshot);

            if (SelectedCulture is { } culture && !culture.Equals(_localization.CurrentCulture))
            {
                try
                {
                    await _localization.SetCultureAsync(culture);
                }
                catch (Exception ex)
                {
                    Log.SetCultureFailed(_logger, culture.Name, ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.SaveFailed(_logger, ex);
            return;
        }

        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Log.CancelInvoked(_logger);
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void ResetGeneralToDefault()
    {
        Log.ResetSectionInvoked(_logger, "General");
        SelectedCulture = AvailableCultures.FirstOrDefault(c => c.Name == OptionsSettingsService.Defaults.CultureCode);
    }

    [RelayCommand]
    private void ResetAudioToDefault()
    {
        Log.ResetSectionInvoked(_logger, "Audio");
        var defaults = OptionsSettingsService.Defaults;
        SelectedCaptureDevice = CaptureDevices.FirstOrDefault(d => d.Id == defaults.CaptureDeviceId);
        SelectedPlaybackDevice = PlaybackDevices.FirstOrDefault(d => d.Id == defaults.PlaybackDeviceId);
        SampleRate = defaults.SampleRate;
    }

    [RelayCommand]
    private void ResetRadioToDefault()
    {
        Log.ResetSectionInvoked(_logger, "Radio");
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
    private void ResetTxToDefault()
    {
        Log.ResetSectionInvoked(_logger, "Tx");
        Callsign = OptionsSettingsService.Defaults.Callsign;
        OperatorName = OptionsSettingsService.Defaults.OperatorName;
        OperatorGrid = OptionsSettingsService.Defaults.OperatorGrid;
    }

    [RelayCommand]
    private void ResetDecodeToDefault()
    {
        Log.ResetSectionInvoked(_logger, "Decode");
        var defaults = OptionsSettingsService.Defaults;
        AutoSyncEnabled = defaults.AutoSyncEnabled;
        AutoSlantEnabled = defaults.AutoSlantEnabled;
    }

    [RelayCommand]
    private void RequestResetAll() => IsConfirmingResetAll = true;

    [RelayCommand]
    private void ConfirmResetAll()
    {
        Log.ConfirmResetAllInvoked(_logger);
        ResetGeneralToDefault();
        ResetAudioToDefault();
        ResetRadioToDefault();
        ResetTxToDefault();
        ResetDecodeToDefault();
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

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Error, Message = "Loading Options failed; dialog may render with defaults")]
        public static partial void LoadFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Save invoked: radioBackend={RadioBackendId}, sampleRate={SampleRate}, culture={CultureCode}")]
        public static partial void SaveInvoked(ILogger logger, string radioBackendId, int sampleRate, string? cultureCode);

        [LoggerMessage(Level = LogLevel.Error, Message = "Save failed; settings not persisted")]
        public static partial void SaveFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SetCultureAsync({Culture}) failed after a successful settings save")]
        public static partial void SetCultureFailed(ILogger logger, string culture, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Cancel invoked")]
        public static partial void CancelInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Reset section to default: {Section}")]
        public static partial void ResetSectionInvoked(ILogger logger, string section);

        [LoggerMessage(Level = LogLevel.Information, Message = "Reset ALL to defaults confirmed")]
        public static partial void ConfirmResetAllInvoked(ILogger logger);
    }
}
