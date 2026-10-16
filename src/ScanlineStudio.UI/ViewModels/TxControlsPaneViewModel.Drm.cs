using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Services;
using ScanlineStudio.UI.Settings;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>The DRM block of the TX pane: profile, size, file size, the six transmission fields, the
/// estimate, the callsign and codec availability. Everything here runs on the UI thread.</summary>
public sealed partial class TxControlsPaneViewModel
{
    private enum DrmAvailability
    {
        Unknown,
        Available,
        Unavailable,
    }

    /// <summary>On-air name when the picture did not come from a file (blank canvas, Copy-to-TX).</summary>
    internal const string DefaultDrmFileName = "image";

    private static readonly TimeSpan DrmEstimateDebounce = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan DrmTargetKbPersistDebounce = TimeSpan.FromMilliseconds(400);

    private readonly Debouncer _drmTargetKbPersist = new(DrmTargetKbPersistDebounce, TimeProvider.System, resumeOnCapturedContext: false);

    private DrmAvailability _drmAvailability = DrmAvailability.Unknown;

    /// <summary>The three named profiles as last loaded (Options owns them), and the TX pane's own Custom values.</summary>
    private DrmProfile[] _drmNamedProfiles = [DrmSettingsDefaults.Robust, DrmSettingsDefaults.Normal, DrmSettingsDefaults.Fast];
    private string?[] _drmProfileNames = new string?[DrmSettingsDefaults.ProfileCount];
    private DrmProfile _drmCustom = DrmSettingsDefaults.CustomDefault;

    private bool _drmVoxTone = DrmSettingsDefaults.DefaultVoxTone;
    private double _drmLevelOffsetDb = DrmSettingsDefaults.DefaultLevelOffsetDb;
    private string? _operatorCallsign;

    // Set while a profile writes its six fields, so those writes do not flip the profile to Custom.
    private bool _applyingDrmProfile;

    // Set while persisted DRM settings are applied, so applying them does not write them back.
    private bool _loadingDrmSettings;

    /// <summary>The file the open editor's background came from (Browse, Stock, Send-to-TX, in-place load), and
    /// the background instance it belongs to. The path counts only while that instance is still the editor's
    /// background, so Remove Background drops it, Undo brings it back, and a baked or promoted background does not
    /// inherit it.</summary>
    private string? _currentSourcePath;
    private IImageSource? _currentSourceBaseline;

    /// <summary>The source file of the APPLIED image (<see cref="_loadedImage"/>): the DRM on-air name follows what
    /// is actually sent, not a picture loaded since.</summary>
    private string? _loadedImageSourcePath;

    /// <summary>The airtime of the last estimate, shown in the editor's header for DRM.</summary>
    private double? _drmEstimatedSeconds;

    // Set by any change that alters the picture; a profile-only change leaves it clear and only recounts.
    private bool _drmEstimateImageDirty = true;

    private int _drmProfileIndex = DrmSettingsDefaults.DefaultActiveProfileIndex;
    private int _drmModeIndex;
    private int _drmBandwidthIndex;
    private int _drmQamIndex;
    private int _drmProtectionIndex;
    private int _drmInterleaveIndex;
    private int _drmReedSolomonIndex;

    [ObservableProperty]
    private double _drmTargetKb = DrmSettingsDefaults.DefaultTargetKb;

    [ObservableProperty]
    private string? _drmEstimateText;

    // Estimate scheduling: at most one encode in flight; a result is used only if no newer request came in.
    private DispatcherTimer? _drmEstimateTimer;
    private bool _drmEstimateInFlight;
    private bool _drmEstimateDirty;
    private long _drmEstimateRequestSeq;
    private byte[]? _cachedJp2;
    private int _cachedJp2TargetBytes;

    public bool IsDrmTransmitAvailable => _drmAvailability == DrmAvailability.Available;

    public bool IsDrmUnavailableMessageVisible => IsDrmSelected && _drmAvailability == DrmAvailability.Unavailable;

    public IReadOnlyList<string> DrmProfileOptions => BuildDrmProfileOptions();

    public IReadOnlyList<string> DrmSizeOptions =>
        [.. Enum.GetValues<DrmSizePreset>().Select(p => _localization.GetString("Panes.TxControls.Drm.SizeFormat", p.Dimensions().Width, p.Dimensions().Height))];

    public IReadOnlyList<string> DrmModeOptions => DrmChoiceLabels.Modes(_localization);

    public IReadOnlyList<string> DrmBandwidthOptions => DrmChoiceLabels.Bandwidths(_localization);

    public IReadOnlyList<string> DrmQamOptions => DrmChoiceLabels.QamOrders(_localization);

    public IReadOnlyList<string> DrmProtectionOptions => DrmChoiceLabels.Protections(_localization);

    public IReadOnlyList<string> DrmInterleaveOptions => DrmChoiceLabels.Interleaves(_localization);

    public IReadOnlyList<string> DrmReedSolomonOptions => DrmChoiceLabels.ReedSolomonLevels(_localization);

    /// <summary>0-2 a named profile, 3 Custom. Selecting one writes its six fields and stays on it.</summary>
    public int DrmProfileIndex
    {
        get => _drmProfileIndex;
        set
        {
            if (value is < 0 or > DrmSettingsDefaults.CustomProfileIndex)
            {
                OnPropertyChanged(nameof(DrmProfileIndex));
                return;
            }

            if (!SetProperty(ref _drmProfileIndex, value))
            {
                return;
            }

            if (_applyingDrmProfile)
            {
                return;
            }

            Log.DrmProfileSelected(_logger, value);
            ApplyDrmProfileFields(ResolvedDrmProfile(value));
            PersistDrmPaneSettings();
            RequestDrmEstimate(imageChanged: false);
        }
    }

    public int DrmSizeIndex
    {
        get => (int)SelectedDrmSize;
        set
        {
            if (IsTransmitting || !Enum.IsDefined((DrmSizePreset)value))
            {
                OnPropertyChanged(nameof(DrmSizeIndex));
                return;
            }

            SelectedDrmSize = (DrmSizePreset)value;
        }
    }

    public int DrmModeIndex
    {
        get => _drmModeIndex;
        set => SetDrmField(ref _drmModeIndex, value, Enum.GetValues<DrmRobustnessMode>().Length);
    }

    public int DrmBandwidthIndex
    {
        get => _drmBandwidthIndex;
        set => SetDrmField(ref _drmBandwidthIndex, value, Enum.GetValues<DrmBandwidth>().Length);
    }

    public int DrmQamIndex
    {
        get => _drmQamIndex;
        set => SetDrmField(ref _drmQamIndex, value, Enum.GetValues<DrmQamOrder>().Length);
    }

    public int DrmProtectionIndex
    {
        get => _drmProtectionIndex;
        set => SetDrmField(ref _drmProtectionIndex, value, Enum.GetValues<DrmProtectionLevel>().Length);
    }

    public int DrmInterleaveIndex
    {
        get => _drmInterleaveIndex;
        set => SetDrmField(ref _drmInterleaveIndex, value, Enum.GetValues<DrmInterleaving>().Length);
    }

    public int DrmReedSolomonIndex
    {
        get => _drmReedSolomonIndex;
        set => SetDrmField(ref _drmReedSolomonIndex, value, Enum.GetValues<DrmReedSolomonLevel>().Length);
    }

    /// <summary>The six transmission fields as currently shown.</summary>
    public DrmProfile CurrentDrmProfile => new(
        Enum.GetValues<DrmRobustnessMode>()[_drmModeIndex],
        Enum.GetValues<DrmBandwidth>()[_drmBandwidthIndex],
        Enum.GetValues<DrmQamOrder>()[_drmQamIndex],
        Enum.GetValues<DrmProtectionLevel>()[_drmProtectionIndex],
        Enum.GetValues<DrmInterleaving>()[_drmInterleaveIndex],
        Enum.GetValues<DrmReedSolomonLevel>()[_drmReedSolomonIndex]);

    public string DrmTargetKbText => _localization.GetString("Panes.TxControls.Drm.FileSizeFormat", (int)DrmTargetKb);

    /// <summary>The callsign as the FAC label sends it (<see cref="DrmCallsign.Normalize"/>), or a dash when empty.</summary>
    public string DrmCallsignDisplay => DrmCallsign.Normalize(_operatorCallsign) is { Length: > 0 } onAir
        ? onAir
        : _localization.GetString("Panes.TxControls.AutoPicksValue.None");

    /// <summary>True when what goes on air differs from the configured callsign (too long, non-Latin-1, …).</summary>
    public bool DrmCallsignDiffers => !string.Equals(DrmCallsign.Normalize(_operatorCallsign), _operatorCallsign ?? string.Empty, StringComparison.Ordinal);

    public string? DrmCallsignToolTip => DrmCallsignDiffers
        ? _localization.GetString("Panes.TxControls.Drm.CallsignDiffers", _operatorCallsign ?? string.Empty)
        : null;

    public string DrmVoxToneText => _localization.GetString(_drmVoxTone ? "Panes.TxControls.Drm.VoxOn" : "Panes.TxControls.Drm.VoxOff");

    private string SelectedTargetDisplayNameForDrm => _localization.GetString("Panes.TxControls.Drm.TargetName");

    /// <summary>The DRM on-air file name: the applied image's source file name, else <see cref="DefaultDrmFileName"/>.</summary>
    internal string DrmOnAirFileName => OnAirFileNameFor(_loadedImageSourcePath);

    private static string OnAirFileNameFor(string? path) =>
        path is not null && Path.GetFileName(path) is { Length: > 0 } name ? name : DefaultDrmFileName;

    private void TrackSourcePath(TxImageEditorPaneViewModel? editor, string? path)
    {
        _currentSourcePath = editor is null ? null : path;
        _currentSourceBaseline = path is null ? null : editor?.BackgroundBaseline;
    }

    /// <summary>The tracked path, if <paramref name="editor"/>'s background is still the one it was loaded with.</summary>
    private string? CurrentBackgroundSourcePath(TxImageEditorPaneViewModel editor) =>
        _currentSourceBaseline is { } baseline && ReferenceEquals(editor.BackgroundBaseline, baseline) ? _currentSourcePath : null;

    /// <summary>The raw callsign goes to the transmitter; it applies the on-air rules itself.</summary>
    internal DrmTxSettings BuildDrmTxSettings()
    {
        var profile = CurrentDrmProfile;
        return new DrmTxSettings(
            profile.Mode, profile.Bandwidth, profile.Qam, profile.Protection, profile.Interleave, profile.ReedSolomon,
            _operatorCallsign ?? string.Empty, _drmVoxTone, (int)DrmTargetKb * 1000, _drmLevelOffsetDb);
    }

    partial void OnDrmTargetKbChanged(double value)
    {
        var rounded = Math.Clamp(Math.Round(value), DrmSettingsDefaults.MinTargetKb, DrmSettingsDefaults.MaxTargetKb);
        if (rounded != value)
        {
            DrmTargetKb = rounded;
            return;
        }

        OnPropertyChanged(nameof(DrmTargetKbText));
        if (_loadingDrmSettings)
        {
            return;
        }

        var kb = (int)rounded;
        _ = _drmTargetKbPersist.Schedule(_ => PersistDrmTargetKbAsync(kb));
        RequestDrmEstimate();
    }

    private string FormatDrmCaption(DrmTxSettings settings, int jp2Bytes) => _localization.GetString(
        "Panes.TxControls.Drm.HistoryCaptionFormat",
        settings.Mode.ToString(),
        DrmChoiceLabels.QamShort(_localization, settings.Qam),
        jp2Bytes / 1000.0);

    private string[] BuildDrmProfileOptions()
    {
        var names = new string[DrmSettingsDefaults.CustomProfileIndex + 1];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = (i < _drmProfileNames.Length ? _drmProfileNames[i] : null)
                ?? _localization.GetString(DrmSettingsDefaults.DefaultNameKeys[i]);
        }

        return names;
    }

    private DrmProfile ResolvedDrmProfile(int index) =>
        index == DrmSettingsDefaults.CustomProfileIndex ? _drmCustom : _drmNamedProfiles[index];

    /// <summary>Writes the six fields under the re-entrancy guard, so the profile does not flip to Custom.</summary>
    private void ApplyDrmProfileFields(DrmProfile profile)
    {
        _applyingDrmProfile = true;
        try
        {
            DrmModeIndex = Array.IndexOf(Enum.GetValues<DrmRobustnessMode>(), profile.Mode);
            DrmBandwidthIndex = Array.IndexOf(Enum.GetValues<DrmBandwidth>(), profile.Bandwidth);
            DrmQamIndex = Array.IndexOf(Enum.GetValues<DrmQamOrder>(), profile.Qam);
            DrmProtectionIndex = Array.IndexOf(Enum.GetValues<DrmProtectionLevel>(), profile.Protection);
            DrmInterleaveIndex = Array.IndexOf(Enum.GetValues<DrmInterleaving>(), profile.Interleave);
            DrmReedSolomonIndex = Array.IndexOf(Enum.GetValues<DrmReedSolomonLevel>(), profile.ReedSolomon);
        }
        finally
        {
            _applyingDrmProfile = false;
        }
    }

    /// <summary>A hand edit of one transmission field: the values become Custom, and the profile switches to it.</summary>
    private void SetDrmField(ref int field, int value, int count, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (value < 0 || value >= count)
        {
            OnPropertyChanged(propertyName);
            return;
        }

        if (!SetProperty(ref field, value, propertyName))
        {
            return;
        }

        if (_applyingDrmProfile)
        {
            return;
        }

        _drmCustom = CurrentDrmProfile;
        if (_drmProfileIndex != DrmSettingsDefaults.CustomProfileIndex)
        {
            _applyingDrmProfile = true;
            try
            {
                DrmProfileIndex = DrmSettingsDefaults.CustomProfileIndex;
            }
            finally
            {
                _applyingDrmProfile = false;
            }
        }

        Log.DrmFieldEdited(_logger, propertyName ?? string.Empty);
        PersistDrmPaneSettings();
        RequestDrmEstimate(imageChanged: false);
    }

    private async Task PrimeDrmAvailabilityAsync()
    {
        bool available;
        try
        {
            available = await _sstvSession.GetDrmTransmitAvailabilityAsync();
        }
        catch (Exception ex)
        {
            Log.DrmAvailabilityCheckFailed(_logger, ex);
            available = false;
        }

        _drmAvailability = available ? DrmAvailability.Available : DrmAvailability.Unavailable;
        OnPropertyChanged(nameof(IsDrmTransmitAvailable));
        OnPropertyChanged(nameof(IsDrmUnavailableMessageVisible));
        TransmitCommand.NotifyCanExecuteChanged();
        _currentEditor?.NotifyTransmitAvailabilityChanged();
        RequestDrmEstimate();
    }

    /// <summary>Constructor-time load of the DRM section and the operator callsign.</summary>
    private async Task LoadDrmSettingsAsync()
    {
        try
        {
            var settings = await _settingsStore.LoadAsync();
            var drm = settings.GetSection(DrmSettings.SectionKey, DrmSettingsJsonContext.Default.DrmSettings);
            var callsign = settings.GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings)?.Callsign;

            _loadingDrmSettings = true;
            try
            {
                ApplyOptionsOwnedDrmSettings(drm, callsign);
                _drmCustom = DrmSettingsDefaults.ResolveProfile(drm, DrmSettingsDefaults.CustomProfileIndex);
                _applyingDrmProfile = true;
                try
                {
                    DrmProfileIndex = DrmSettingsDefaults.ResolveActiveProfileIndex(drm);
                }
                finally
                {
                    _applyingDrmProfile = false;
                }

                ApplyDrmProfileFields(ResolvedDrmProfile(DrmProfileIndex));
                SelectedDrmSize = DrmSettingsDefaults.ResolveSizePreset(drm);
                DrmTargetKb = DrmSettingsDefaults.ResolveTargetKb(drm);
            }
            finally
            {
                _loadingDrmSettings = false;
            }

            RequestDrmEstimate();
        }
        catch (Exception ex)
        {
            Log.LoadDrmSettingsFailed(_logger, ex);
        }
    }

    /// <summary>Called when the Options dialog closes. Refreshes only what Options owns (profiles, VOX tone, level
    /// offset) plus the callsign; a named active profile re-applies its possibly edited fields.</summary>
    public async Task ReloadDrmOptionsAsync()
    {
        try
        {
            var settings = await _settingsStore.LoadAsync();
            var drm = settings.GetSection(DrmSettings.SectionKey, DrmSettingsJsonContext.Default.DrmSettings);
            var callsign = settings.GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings)?.Callsign;
            ApplyOptionsOwnedDrmSettings(drm, callsign);
            if (_drmProfileIndex != DrmSettingsDefaults.CustomProfileIndex)
            {
                ApplyDrmProfileFields(ResolvedDrmProfile(_drmProfileIndex));
            }

            RequestDrmEstimate(imageChanged: false);
        }
        catch (Exception ex)
        {
            Log.LoadDrmSettingsFailed(_logger, ex);
        }
    }

    private void ApplyOptionsOwnedDrmSettings(DrmSettings? drm, string? callsign)
    {
        for (var i = 0; i < DrmSettingsDefaults.ProfileCount; i++)
        {
            _drmNamedProfiles[i] = DrmSettingsDefaults.ResolveProfile(drm, i);
            _drmProfileNames[i] = DrmSettingsDefaults.StoredProfileName(drm, i);
        }

        _drmVoxTone = DrmSettingsDefaults.ResolveVoxTone(drm);
        _drmLevelOffsetDb = DrmSettingsDefaults.ResolveLevelOffsetDb(drm);
        _operatorCallsign = callsign;

        // A new names list resets the ComboBox selection; re-raising the index puts it back.
        OnPropertyChanged(nameof(DrmProfileOptions));
        OnPropertyChanged(nameof(DrmProfileIndex));
        OnPropertyChanged(nameof(DrmVoxToneText));
        OnPropertyChanged(nameof(DrmCallsignDisplay));
        OnPropertyChanged(nameof(DrmCallsignDiffers));
        OnPropertyChanged(nameof(DrmCallsignToolTip));
    }

    /// <summary>Writes the TX-pane-owned DRM fields except the debounced file size.</summary>
    private void PersistDrmPaneSettings()
    {
        if (_loadingDrmSettings)
        {
            return;
        }

        var activeIndex = _drmProfileIndex;
        var size = SelectedDrmSize;
        var custom = DrmProfileSettings.From(_drmCustom);
        _ = UpdateDrmSettingsAsync(current => current with { ActiveProfileIndex = activeIndex, SizePreset = size, Custom = custom });
    }

    private Task PersistDrmTargetKbAsync(int kb) => UpdateDrmSettingsAsync(current => current with { TargetKb = kb });

    private async Task UpdateDrmSettingsAsync(Func<DrmSettings, DrmSettings> update)
    {
        try
        {
            await _settingsStore.UpdateAsync(settings =>
            {
                var current = settings.GetSection(DrmSettings.SectionKey, DrmSettingsJsonContext.Default.DrmSettings) ?? new DrmSettings();
                return settings.WithSection(DrmSettings.SectionKey, update(current), DrmSettingsJsonContext.Default.DrmSettings);
            });
        }
        catch (Exception ex)
        {
            Log.PersistDrmSettingsFailed(_logger, ex);
        }
    }

    /// <summary>Marks the estimate stale and restarts the 400 ms debounce. No estimate runs while transmitting; a
    /// stale one re-runs when the transmission ends.</summary>
    private void RequestDrmEstimate(bool imageChanged = true)
    {
        _drmEstimateRequestSeq++;
        _drmEstimateImageDirty |= imageChanged;
        if (!IsDrmSelected || (!UseEditorForDrmEstimate && _loadedImage is null))
        {
            _drmEstimateDirty = false;
            _drmEstimateTimer?.Stop();
            DrmEstimateText = null;
            SetDrmEstimatedSeconds(null);
            return;
        }

        _drmEstimateDirty = true;
        if (IsTransmitting)
        {
            return;
        }

        _drmEstimateTimer ??= CreateDrmEstimateTimer();
        _drmEstimateTimer.Stop();
        _drmEstimateTimer.Start();
    }

    private DispatcherTimer CreateDrmEstimateTimer()
    {
        var timer = new DispatcherTimer { Interval = DrmEstimateDebounce };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = RunDrmEstimateAsync();
        };
        return timer;
    }

    /// <summary>Runs a pending estimate now instead of after the debounce. For tests; nothing binds it.</summary>
    public Task FlushDrmEstimateAsync()
    {
        _drmEstimateTimer?.Stop();
        return RunDrmEstimateAsync();
    }

    /// <summary>Whether the debounce timer is waiting to run an estimate. For tests; nothing binds it.</summary>
    public bool IsDrmEstimateScheduled => _drmEstimateTimer?.IsEnabled ?? false;

    private void OnTransmitEndedForDrmEstimate()
    {
        if (_drmEstimateDirty)
        {
            RequestDrmEstimate();
        }
    }

    /// <summary>Estimates the editor's composed frame, blank canvas included, as Apply &amp; Transmit sends it; the
    /// applied image only when no editor is open.</summary>
    private bool UseEditorForDrmEstimate => _currentEditor is not null;

    private void SetDrmEstimatedSeconds(double? seconds)
    {
        _drmEstimatedSeconds = seconds;
        _currentEditor?.SetEstimatedFrameSeconds(seconds);
    }

    private async Task RunDrmEstimateAsync()
    {
        if (_drmEstimateInFlight || !_drmEstimateDirty || IsTransmitting)
        {
            return;
        }

        Func<IImageSource>? compose = null;
        IImageSource? image = null;
        string? sourcePath;
        if (IsDrmSelected && UseEditorForDrmEstimate && _currentEditor is { } editor)
        {
            compose = editor.CaptureFinalOutputComposer();
            sourcePath = CurrentBackgroundSourcePath(editor);
        }
        else if (IsDrmSelected && _loadedImage is { } loaded)
        {
            image = loaded;
            sourcePath = _loadedImageSourcePath;
        }
        else
        {
            _drmEstimateDirty = false;
            return;
        }

        // Stays dirty: PrimeDrmAvailabilityAsync requests a fresh estimate once the codec is known to load.
        if (!IsDrmTransmitAvailable)
        {
            return;
        }

        var requestSeq = _drmEstimateRequestSeq;
        _drmEstimateDirty = false;
        var settings = BuildDrmTxSettings();
        var fileName = OnAirFileNameFor(sourcePath);
        var cached = !_drmEstimateImageDirty && _cachedJp2 is { } jp2Cache && _cachedJp2TargetBytes == settings.TargetJp2Bytes
            ? jp2Cache
            : null;
        _drmEstimateImageDirty = false;

        _drmEstimateInFlight = true;
        DrmEstimateText = _localization.GetString("Panes.TxControls.Drm.Estimating");
        int jp2Bytes;
        TimeSpan duration;
        try
        {
            if (cached is null)
            {
                image ??= await Task.Run(compose!);
                var estimate = await _sstvSession.EstimateDrmAsync(image, fileName, settings);
                _cachedJp2 = estimate.Jp2;
                _cachedJp2TargetBytes = settings.TargetJp2Bytes;
                (jp2Bytes, duration) = (estimate.Jp2Bytes, estimate.Duration);
            }
            else
            {
                duration = await _sstvSession.EstimateDrmDurationAsync(cached, fileName, settings);
                jp2Bytes = cached.Length;
            }
        }
        catch (Exception ex)
        {
            Log.DrmEstimateFailed(_logger, ex);
            _drmEstimateInFlight = false;
            _drmEstimateImageDirty = true;
            if (requestSeq == _drmEstimateRequestSeq)
            {
                DrmEstimateText = _localization.GetString("Panes.TxControls.Drm.EstimateFailed");
                SetDrmEstimatedSeconds(null);
            }
            else
            {
                ContinueSupersededDrmEstimate();
            }

            return;
        }

        _drmEstimateInFlight = false;
        if (requestSeq != _drmEstimateRequestSeq)
        {
            ContinueSupersededDrmEstimate();
            return;
        }

        DrmEstimateText = _localization.GetString("Panes.TxControls.Drm.EstimateFormat", jp2Bytes / 1000.0, FormatRemaining(duration));
        SetDrmEstimatedSeconds(duration.TotalSeconds);
    }

    /// <summary>A newer request arrived while the encode ran: drop the result, and run again unless the debounce
    /// timer will.</summary>
    private void ContinueSupersededDrmEstimate()
    {
        if (_drmEstimateDirty && !IsDrmEstimateScheduled && !IsTransmitting)
        {
            _ = RunDrmEstimateAsync();
        }
    }
}
