using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Settings;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Services;
using ScanlineStudio.UI.Settings;

namespace ScanlineStudio.UI.ViewModels;

public sealed partial class OptionsWindowViewModel
{
    [RelayCommand]
    private void ResetGeneralToDefault()
    {
        Log.ResetSectionInvoked(_logger, "General");
        // Tier B audit finding: OptionsSettingsService.Defaults.CultureCode is always null (the
        // record default), so a bare FirstOrDefault here always misses and used to blank the
        // Language ComboBox on every Reset -- ApplyFromSnapshot already has this exact fallback for
        // the same reason (see its own line), applied here too.
        SelectedCulture = AvailableCultures.FirstOrDefault(c => c.Name == OptionsSettingsService.Defaults.CultureCode) ?? _localization.CurrentCulture;
        RememberWindowPosition = true;
        JpegQuality = 85;
    }

    /// <summary>Same "deliberately NOT part of OptionsSnapshot/OptionsSettingsService.Defaults"
    /// reasoning as <see cref="ResetGeneralToDefault"/>'s own RememberWindowPosition/JpegQuality --
    /// resets to <c>App.axaml</c>'s own hardcoded fallback, not a value read from
    /// OptionsSettingsService.Defaults (AppTheme has no entry there at all).</summary>
    [RelayCommand]
    private void ResetAppearanceToDefault()
    {
        Log.ResetSectionInvoked(_logger, "Appearance");
        AppTheme = AppTheme.Light;
        // Phase 2 addition -- does not live-apply, same as AppTheme immediately above: this only
        // mutates the in-memory property, matching this command's own established behavior. Live-apply
        // only happens from SaveCoreUnguardedAsync, for both fields, on an actual Save click.
        FontScale = AppFontScale.Normal;
        DecodingIndicatorBlinks = AppearanceSettings.DefaultDecodingIndicatorBlinks;
    }

    [RelayCommand]
    private void ResetAudioToDefault()
    {
        Log.ResetSectionInvoked(_logger, "Audio");
        var defaults = OptionsSettingsService.Defaults;
        SelectedCaptureDevice = CaptureDevices.FirstOrDefault(d => d.Id == defaults.CaptureDeviceId);
        SelectedPlaybackDevice = PlaybackDevices.FirstOrDefault(d => d.Id == defaults.PlaybackDeviceId);
        SampleRate = defaults.SampleRate;
        TxSampleRateOffsetHz = defaults.TxSampleRateOffsetHz;
        CaptureChannelSource = defaults.CaptureChannelSource;
        StereoTxEnabled = defaults.StereoTxEnabled;
    }

    [RelayCommand]
    private void ResetRadioToDefault()
    {
        InvalidateRadioTests();
        Log.ResetSectionInvoked(_logger, "Radio");
        var defaults = OptionsSettingsService.Defaults;
        RadioBackendId = defaults.RadioBackendId;
        RigctldHost = defaults.RigctldHost;
        RigctldPort = defaults.RigctldPort;
        HamlibModel = defaults.HamlibModel;
        HamlibLibraryPath = defaults.HamlibLibraryPath;
        HamlibSerialPort = defaults.HamlibSerialPort;
        HamlibBaudRate = defaults.HamlibBaudRate;
        // "RIG" (CAT), not defaults.HamlibPttType (null) -- see ApplyFromSnapshot's own comment for
        // why null selects nothing among the 4 fixed radio buttons.
        HamlibPttType = "RIG";
        HamlibPttPort = defaults.HamlibPttPort;
        FlrigHost = defaults.FlrigHost;
        FlrigPort = defaults.FlrigPort;
        // Tier B audit finding: sibling ResetQrzToDefault already clears its own test-result status
        // (TestQrzLookupStatus) -- this one didn't, so a prior "Connected to IC-7300" success line
        // stayed visible under the now-blank host field after a reset.
        TestConnectionStatusMessage = null;
        TestPttErrorMessage = null;
        HamlibDiscoveryStatusMessage = null;
        HamlibRigModels.Clear();
        SelectedHamlibRigModel = null;
        // Explicit, not relying on the OnXxxChanged resets above firing as a side effect --
        // [ObservableProperty]'s generated setter skips the changed-callback entirely when the new
        // value equals the old one (e.g. resetting an already-default field), which would otherwise
        // leave a stale "tested" flag from BEFORE the reset still authorizing Connect afterward.
        RigctldTestSucceeded = false;
        HamlibCatTestSucceeded = false;
        HamlibPttTestSucceeded = false;
        FlrigTestSucceeded = false;
        FlrigPttTestSucceeded = false;
        OmniRigTestSucceeded = false;
        // Not part of OptionsSettingsService.Defaults -- RadioSafety is its own settings section, not
        // in OptionsSnapshot (see SaveAsync's own comment for why). RadioSafetySpec.DefaultSwrCutoffThreshold
        // is the single source of truth for this default, shared with RadioSafetySettings' own
        // property initializer.
        SwrCutoffEnabled = false;
        SwrCutoffThreshold = RadioSafetySpec.DefaultSwrCutoffThreshold;
    }

    [RelayCommand]
    private void ResetTxToDefault()
    {
        Log.ResetSectionInvoked(_logger, "Tx");
        Callsign = OptionsSettingsService.Defaults.Callsign;
        OperatorName = OptionsSettingsService.Defaults.OperatorName;
        OperatorGrid = OptionsSettingsService.Defaults.OperatorGrid;
        DefaultRst = OptionsSettingsService.Defaults.DefaultRst;
        ResetDrmOptions();
    }

    [RelayCommand]
    private void ResetDecodeToDefault()
    {
        Log.ResetSectionInvoked(_logger, "Decode");
        var defaults = OptionsSettingsService.Defaults;
        AutoSyncEnabled = defaults.AutoSyncEnabled;
        AutoSlantEnabled = defaults.AutoSlantEnabled;
        AutoStopEnabled = defaults.AutoStopEnabled;
        SyncRestartEnabled = defaults.SyncRestartEnabled;
        SnrMeasurementEnabled = defaults.SnrMeasurementEnabled;
        SenseLevel = defaults.SenseLevel;
        DemodType = defaults.DemodType;
        RxBpfPreset = defaults.RxBpfPreset;
        RxBufferMode = defaults.RxBufferMode;
    }

    /// <summary>Advanced tab's own "Reset section" button -- the first real, resettable content on
    /// that tab (Options stub backlog item 1); scoped to the PLL, zero-crossing, and TX BPF/LPF
    /// tuning fields wired so far (items 2 and 3 added the latter two), same incremental-growth
    /// pattern as every other <c>Reset*ToDefault</c> command here.</summary>
    [RelayCommand]
    private void ResetAdvancedToDefault()
    {
        Log.ResetSectionInvoked(_logger, "Advanced");
        var defaults = OptionsSettingsService.Defaults;
        PllVcoGain = defaults.PllVcoGain;
        PllLoopOrder = defaults.PllLoopOrder;
        PllLoopCutoffHz = defaults.PllLoopCutoffHz;
        PllOutputOrder = defaults.PllOutputOrder;
        PllOutputCutoffHz = defaults.PllOutputCutoffHz;
        ZeroCrossingSmoothingMode = defaults.ZeroCrossingSmoothingMode;
        ZeroCrossingOutputOrder = defaults.ZeroCrossingOutputOrder;
        ZeroCrossingOutputCutoffHz = defaults.ZeroCrossingOutputCutoffHz;
        ZeroCrossingSmoothingFrequencyHz = defaults.ZeroCrossingSmoothingFrequencyHz;
        TxBpfEnabled = defaults.TxBpfEnabled;
        TxBpfTapCount = defaults.TxBpfTapCount;
        TxLpfEnabled = defaults.TxLpfEnabled;
        TxLpfFrequencyHz = defaults.TxLpfFrequencyHz;
    }

    [RelayCommand]
    private void ResetQrzToDefault()
    {
        Log.ResetSectionInvoked(_logger, "Qrz");
        var defaults = OptionsSettingsService.Defaults;
        QrzLookupEnabled = defaults.QrzLookupEnabled;
        QrzLookupUsername = defaults.QrzLookupUsername;
        QrzLookupPassword = null;
        TestQrzLookupStatus = null;
    }

    [RelayCommand]
    private void ResetIdentificationToDefault()
    {
        Log.ResetSectionInvoked(_logger, "Identification");
        var defaults = OptionsSettingsService.Defaults;
        CwIdMode = defaults.CwIdMode;
        CwText = defaults.CwText;
        CwWpm = defaults.CwWpm;
        CwToneFrequencyHz = defaults.CwToneFrequencyHz;
        FskIdTxEnabled = defaults.FskIdTxEnabled;
        FskIdRxEnabled = defaults.FskIdRxEnabled;
        CwIdRxEnabled = defaults.CwIdRxEnabled;
        CwIdRxWindowSeconds = defaults.CwIdRxWindowSeconds;
        NrRstEnabled = defaults.NrRstEnabled;
        NrRstText = defaults.NrRstText;
        SoundFileMmvPath = defaults.SoundFileMmvPath;
    }

    [RelayCommand]
    private void ResetForwardingToDefault()
    {
        Log.ResetSectionInvoked(_logger, "Forwarding");
        // Reads OptionsSettingsService.Defaults (today an empty list) rather than hardcoding
        // Clear() -- same "never hardcoded again at the call site" convention that property's own
        // doc comment states, so this stays correct if a future default ever seeds a destination.
        AdifUdpDestinations.Clear();
        foreach (var destination in OptionsSettingsService.Defaults.AdifUdpDestinations)
        {
            AdifUdpDestinations.Add(new AdifUdpDestinationRowViewModel
            {
                Enabled = destination.Enabled == true,
                Name = destination.Name,
                Host = destination.Host,
                Port = destination.Port,
                RemoveCommand = RemoveAdifUdpDestinationCommand,
            });
        }
    }
}
