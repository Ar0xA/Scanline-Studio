using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Application;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>The fixed radio/frequency status strip — see the Phase-3 plan's pane-count scope trim:
/// this is `MainWindow` chrome, not a 4th dockable pane (`spec/14-roadmap.md`'s Phase 3 bullet and
/// `spec/09-ui.md`'s inventory table both only call for 3 dockable panes). Piece 5
/// (spec/14-roadmap.md's re-scoped Phase 4) makes it interactive: an editable frequency/mode
/// control, frequency memory presets, a WSJT-X-style TX volume slider, and a Tune button — on top
/// of the original read-only frequency/mode readout.</summary>
public sealed partial class RadioStatusViewModel : ViewModelBase
{
    private static readonly TimeSpan VolumePersistDebounce = TimeSpan.FromMilliseconds(400);

    private readonly IRadioSessionService _radioSession;
    private readonly ISstvSessionService _sstvSession;
    private readonly ILocalizationService _localization;
    private bool _suppressVolumePersist;
    private CancellationTokenSource? _volumePersistCts;

    [ObservableProperty]
    private string _frequencyDisplay;

    [ObservableProperty]
    private string _modeDisplay;

    [ObservableProperty]
    private string _frequencyInputMhz = string.Empty;

    /// <summary>Any of this strip's commands can call into <see cref="IRadioSessionService"/> while no
    /// radio is connected (a routine, common state, not an edge case) -- that throws
    /// <c>InvalidOperationException</c>, and left uncaught that crashes the whole app (a real crash
    /// found via hands-on verification, not hypothetical). Same catch-and-report-locally pattern as
    /// <c>TxControlsPaneViewModel.ErrorMessage</c>.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private RadioMode _selectedRadioMode = RadioMode.Usb;

    private bool _suppressModeCommand;

    [ObservableProperty]
    private int _txVolumePercent = 100;

    [ObservableProperty]
    private double _tuneFrequencyHz = 1750;

    [ObservableProperty]
    private double _tuneDurationSeconds = 5;

    /// <summary>Backs the Transceiver card's Receiving/Halt toggle -- real state, mirrors
    /// <see cref="ISstvSessionService.IsReceiving"/> exactly (including the case where a startup
    /// auto-start silently failed for lack of an audio device).</summary>
    [ObservableProperty]
    private bool _isReceiving;

    private bool _suppressReceivingCommand;

    /// <summary>Real state: true only while <see cref="IRadioSessionService.ConnectionEvents"/>'s most
    /// recent lifecycle transition was <see cref="RadioConnectionState.Connected"/> -- a
    /// <see cref="RadioConnectionState.CommandFailed"/> event is deliberately ignored here (that state
    /// means a single command failed while the connection itself stays healthy, per that enum's own
    /// doc comment; it must not flip this indicator off).</summary>
    [ObservableProperty]
    private bool _catLinked;

    public RadioStatusViewModel(IRadioSessionService radioSession, ISstvSessionService sstvSession, ILocalizationService localization)
    {
        _radioSession = radioSession;
        _sstvSession = sstvSession;
        _localization = localization;
        _frequencyDisplay = localization.GetString("RadioStatus.NoFrequency");
        _modeDisplay = string.Empty;
        _isReceiving = sstvSession.IsReceiving;
        _catLinked = radioSession.LastKnownState is not null;

        radioSession.StateChanges.Subscribe(OnStateChanged);
        radioSession.ConnectionEvents.Subscribe(OnConnectionEvent);
        if (radioSession.LastKnownState is { } state)
        {
            OnStateChanged(state);
        }

        _ = LoadPresetsAsync();
        _ = LoadTxVolumeAsync();
    }

    public IReadOnlyList<RadioMode> AvailableModes { get; } = Enum.GetValues<RadioMode>();

    public ObservableCollection<FrequencyPresetButtonViewModel> Presets { get; } = [];

    public ObservableCollection<FrequencyPresetEditorRowViewModel> EditorRows { get; } = [];

    private void OnStateChanged(RadioState state)
    {
        // IRadioSessionService.StateChanges pushes synchronously from IRadioController's own poll
        // loop thread (see that interface's doc comment) -- marshal to the UI thread here, the only
        // place in this codebase allowed to touch Avalonia's Dispatcher (spec/01-architecture.md).
        Dispatcher.UIThread.Post(() =>
        {
            FrequencyDisplay = $"{state.FrequencyHz / 1_000_000.0:0.000000} MHz";
            ModeDisplay = state.Mode.ToString();

            _suppressModeCommand = true;
            SelectedRadioMode = state.Mode;
            _suppressModeCommand = false;
        });
    }

    private void OnConnectionEvent(RadioConnectionEvent evt)
    {
        if (evt.State == RadioConnectionState.CommandFailed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => CatLinked = evt.State == RadioConnectionState.Connected);
    }

    private async Task LoadPresetsAsync()
    {
        var presets = await _radioSession.GetFrequencyPresetsAsync().ConfigureAwait(false);
        Dispatcher.UIThread.Post(() => RebuildPresetCollections(presets));
    }

    private async Task LoadTxVolumeAsync()
    {
        var percent = await _sstvSession.GetTxVolumePercentAsync().ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
        {
            _suppressVolumePersist = true;
            TxVolumePercent = percent;
            _suppressVolumePersist = false;
        });
    }

    private void RebuildPresetCollections(IReadOnlyList<FrequencyPreset> presets)
    {
        Presets.Clear();
        EditorRows.Clear();
        foreach (var preset in presets)
        {
            Presets.Add(new FrequencyPresetButtonViewModel(preset, ApplyPresetCommand));
            EditorRows.Add(new FrequencyPresetEditorRowViewModel(preset, RemovePresetRowCommand));
        }
    }

    [RelayCommand]
    private async Task SetFrequencyAsync()
    {
        if (!double.TryParse(FrequencyInputMhz, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz))
        {
            return;
        }

        try
        {
            ErrorMessage = null;
            await _radioSession.SetFrequencyAsync((long)(mhz * 1_000_000)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("RadioStatus.Error.NoRadioConnected"));
        }
    }

    [RelayCommand]
    private async Task ApplyPresetAsync(FrequencyPreset preset)
    {
        try
        {
            ErrorMessage = null;
            await _radioSession.SetFrequencyAsync(preset.FrequencyHz).ConfigureAwait(false);
            await _radioSession.SetModeAsync(preset.Mode).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("RadioStatus.Error.NoRadioConnected"));
        }
    }

    [RelayCommand]
    private void AddPresetRow() => EditorRows.Add(new FrequencyPresetEditorRowViewModel(new FrequencyPreset(string.Empty, 14_230_000, RadioMode.Usb), RemovePresetRowCommand));

    [RelayCommand]
    private void RemovePresetRow(FrequencyPresetEditorRowViewModel row) => EditorRows.Remove(row);

    [RelayCommand]
    private async Task SavePresetsAsync()
    {
        var presets = new List<FrequencyPreset>();
        foreach (var row in EditorRows)
        {
            if (double.TryParse(row.FrequencyMhzText, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz))
            {
                presets.Add(new FrequencyPreset(row.Label, (long)(mhz * 1_000_000), row.SelectedMode));
            }
        }

        await _radioSession.SaveFrequencyPresetsAsync(presets).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() => RebuildPresetCollections(presets));
    }

    [RelayCommand]
    private async Task TuneAsync()
    {
        try
        {
            ErrorMessage = null;
            await _sstvSession.TuneAsync(TuneFrequencyHz, TimeSpan.FromSeconds(TuneDurationSeconds)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("RadioStatus.Error.TuneFailed"));
        }
    }

    partial void OnIsReceivingChanged(bool value)
    {
        if (_suppressReceivingCommand)
        {
            return;
        }

        _ = SetReceivingSafeAsync(value);
    }

    private async Task SetReceivingSafeAsync(bool value)
    {
        try
        {
            if (value)
            {
                await _sstvSession.StartReceivingAsync().ConfigureAwait(false);
            }
            else
            {
                await _sstvSession.StopReceivingAsync().ConfigureAwait(false);
            }

            Dispatcher.UIThread.Post(() => ErrorMessage = null);
        }
        catch (Exception)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ErrorMessage = _localization.GetString("RadioStatus.Error.ReceivingFailed");
                _suppressReceivingCommand = true;
                IsReceiving = !value;
                _suppressReceivingCommand = false;
            });
        }
    }

    /// <summary>Separate from unchecking the Receiving toggle -- mirrors mock2's own explicit
    /// Receiving/Halt button pair, not just a single two-state toggle.</summary>
    [RelayCommand]
    private async Task HaltReceivingAsync()
    {
        try
        {
            await _sstvSession.StopReceivingAsync().ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                _suppressReceivingCommand = true;
                IsReceiving = false;
                _suppressReceivingCommand = false;
                ErrorMessage = null;
            });
        }
        catch (Exception)
        {
            Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("RadioStatus.Error.ReceivingFailed"));
        }
    }

    partial void OnTxVolumePercentChanged(int value)
    {
        if (_suppressVolumePersist)
        {
            return;
        }

        // Debounced (not one settings write per slider tick, per the Piece 5 plan) -- also avoids
        // a real correctness hazard: overlapping un-awaited SaveAsync calls racing each other could
        // let a stale write clobber a fresher one.
        _volumePersistCts?.Cancel();
        var cts = new CancellationTokenSource();
        _volumePersistCts = cts;
        _ = PersistVolumeDebouncedAsync(value, cts.Token);
    }

    private async Task PersistVolumeDebouncedAsync(int value, CancellationToken ct)
    {
        try
        {
            await Task.Delay(VolumePersistDebounce, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        await _sstvSession.SetTxVolumePercentAsync(value, ct).ConfigureAwait(false);
    }

    partial void OnSelectedRadioModeChanged(RadioMode value)
    {
        if (_suppressModeCommand)
        {
            return;
        }

        _ = SetModeSafeAsync(value);
    }

    private async Task SetModeSafeAsync(RadioMode value)
    {
        try
        {
            await _radioSession.SetModeAsync(value).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("RadioStatus.Error.NoRadioConnected"));
        }
    }
}

/// <summary>One frequency memory button -- carries its own <see cref="SelectCommand"/> (the parent's
/// <see cref="RadioStatusViewModel.ApplyPresetCommand"/>, set once at construction), same shape as
/// <c>FavoriteModeButtonViewModel</c> and for the identical reason (avoids a cross-DataTemplate
/// binding cast, which crashes at runtime the first time it renders with real data -- see that
/// type's own doc comment).</summary>
public sealed record FrequencyPresetButtonViewModel(FrequencyPreset Preset, System.Windows.Input.ICommand SelectCommand)
{
    public string Label => Preset.Label;
}

/// <summary>One editable row in the "Edit presets..." flyout. A plain mutable view-model (not a
/// record like <see cref="FrequencyPresetButtonViewModel"/>) since its fields are two-way bound
/// text/combo inputs, not fixed-at-construction display data.</summary>
public sealed partial class FrequencyPresetEditorRowViewModel : ObservableObject
{
    public FrequencyPresetEditorRowViewModel(FrequencyPreset preset, System.Windows.Input.ICommand removeCommand)
    {
        _label = preset.Label;
        _frequencyMhzText = (preset.FrequencyHz / 1_000_000.0).ToString("0.000000", CultureInfo.InvariantCulture);
        _selectedMode = preset.Mode;
        RemoveCommand = removeCommand;
    }

    public System.Windows.Input.ICommand RemoveCommand { get; }

    public IReadOnlyList<RadioMode> AvailableModes { get; } = Enum.GetValues<RadioMode>();

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private string _frequencyMhzText;

    [ObservableProperty]
    private RadioMode _selectedMode;
}
