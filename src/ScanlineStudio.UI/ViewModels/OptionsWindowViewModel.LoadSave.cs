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
    /// <summary>Unguarded fire-and-forget from the constructor before this wrap was added -- the
    /// audio enumerator's <c>RefreshAsync</c> call can throw, which used to mean the Options dialog
    /// could open completely blank with no explanation anywhere. Tier B audit finding: on ANY
    /// exception here (not just the audio enumerator's -- LoadAsync/settingsStore.LoadAsync can also
    /// throw), the fields left at their hardcoded constructor defaults used to be exactly what
    /// SaveAsync would persist if the user touched anything else and hit Save, silently overwriting
    /// real settings (callsign, JPEG quality, capture/playback device IDs, ...) with defaults with
    /// zero warning. _loadSucceeded now gates SaveCommand's CanExecute so Save is simply unavailable
    /// until a load has actually completed -- the general-case fix, not a per-field patch, since the
    /// failure isn't specific to any one field.</summary>
    private async Task LoadSafeAsync()
    {
        try
        {
            var snapshot = await _optionsSettingsService.LoadAsync();
            ApplyFromSnapshot(snapshot);
            await LoadQrzPasswordFieldAsync(allowPrompt: false);
            RefreshSerialPorts();

            var appSettings = await _settingsStore.LoadAsync();
            RememberWindowPosition = appSettings.GetSection(WindowGeometrySettings.SectionKey, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings)?.RememberWindowPosition ?? true;
            JpegQuality = Math.Clamp(appSettings.GetSection(ImageExportSettings.SectionKey, ImageExportSettingsJsonContext.Default.ImageExportSettings)?.JpegQuality ?? 85, 1, 100);
            // One shared GetSection call for both fields (code-review fix: was two independent
            // deserializations of the same section), matching Program.cs's own one-shared-read shape.
            var appearanceSettings = appSettings.GetSection(AppearanceSettings.SectionKey, AppearanceSettingsJsonContext.Default.AppearanceSettings);
            AppTheme = appearanceSettings?.Theme ?? AppTheme.Light;
            FontScale = appearanceSettings?.FontScale ?? AppFontScale.Normal;
            DecodingIndicatorBlinks = appearanceSettings?.DecodingIndicatorBlinks ?? AppearanceSettings.DefaultDecodingIndicatorBlinks;
            LoadDrmOptions(appSettings.GetSection(DrmSettings.SectionKey, DrmSettingsJsonContext.Default.DrmSettings));

            // SWR auto-cutoff (2026-08-26, relocated here from TxControlsPaneView's own Output card
            // per user request -- see RadioSafetySpec's own doc comment for why this is Abstractions,
            // not Core.Radio, and IRadioSessionService.SafetySettingsChanged's own doc comment for how
            // TxControlsPaneViewModel's live enforcement picks up a Save made here without being
            // reconstructed). NOT part of OptionsSnapshot/_optionsSettingsService -- RadioSafety is its
            // own settings section, saved via _radioSession.SaveSafetySettingsAsync (SaveAsync below),
            // same reasoning as why RadioConnectionSettings itself isn't in the snapshot either.
            var safetySpec = await _radioSession.GetSafetySettingsAsync();
            SwrCutoffEnabled = safetySpec.SwrCutoffEnabled;
            SwrCutoffThreshold = safetySpec.SwrCutoffThreshold;

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

            // User-reported fix (2026-08-23): an exact Id match can genuinely fail even for the
            // SAME physical device -- backend device ids can churn across a reconnect/profile
            // change (observed live: a PipeWire USB capture node re-created under a new id after a
            // mute toggle). Falls back to the last-known device Name (AudioDeviceSettings.
            // CaptureDeviceName/PlaybackDeviceName's own doc comment) before giving up.
            //
            // Round 3, same day (explicit product decision, overriding this method's own prior
            // "leave the selection blank" fallback for a genuinely-missing device): "if in the file
            // there is an RX/TX device that is not currently attached to the computer, just set it
            // to the OS defaults" -- a configured device matching NEITHER by id nor by name now
            // falls through to whatever device is currently the backend-reported default, same as
            // "nothing configured," instead of leaving the dropdown blank. Mirrors
            // SstvSessionService.TryResolveDeviceAsync's identical three-step fallback (id -> name ->
            // backend default) for the actual runtime resolution.
            SelectedCaptureDevice = snapshot.CaptureDeviceId is { } captureId
                ? CaptureDevices.FirstOrDefault(d => d.Id == captureId)
                    ?? (snapshot.CaptureDeviceName is { } captureName ? CaptureDevices.FirstOrDefault(d => d.Name == captureName) : null)
                    ?? CaptureDevices.FirstOrDefault(d => d.IsDefault)
                : CaptureDevices.FirstOrDefault(d => d.IsDefault);
            SelectedPlaybackDevice = snapshot.PlaybackDeviceId is { } playbackId
                ? PlaybackDevices.FirstOrDefault(d => d.Id == playbackId)
                    ?? (snapshot.PlaybackDeviceName is { } playbackName ? PlaybackDevices.FirstOrDefault(d => d.Name == playbackName) : null)
                    ?? PlaybackDevices.FirstOrDefault(d => d.IsDefault)
                : PlaybackDevices.FirstOrDefault(d => d.IsDefault);

            _loadSucceeded = true;
        }
        catch (Exception ex)
        {
            Log.LoadFailed(_logger, ex);
        }
        finally
        {
            SaveCommand.NotifyCanExecuteChanged();
            // Plan-review finding (step 7): RelayCommand caches CanExecute -- without this,
            // ApplyCommand stays permanently disabled even after a successful load, since nothing
            // else ever notifies it (unlike SaveCommand, which this method already covered).
            ApplyCommand.NotifyCanExecuteChanged();
        }
    }

    private void ApplyFromSnapshot(OptionsSnapshot snapshot)
    {
        SelectedCulture = AvailableCultures.FirstOrDefault(c => c.Name == snapshot.CultureCode) ?? _localization.CurrentCulture;
        SampleRate = snapshot.SampleRate;
        TxSampleRateOffsetHz = snapshot.TxSampleRateOffsetHz;
        CaptureChannelSource = Enum.IsDefined(snapshot.CaptureChannelSource) ? snapshot.CaptureChannelSource : AudioChannelSource.Mono;
        StereoTxEnabled = snapshot.StereoTxEnabled;
        RadioBackendId = snapshot.RadioBackendId is "none" or "rigctld" or "hamlib" or "flrig" or "omnirig" ? snapshot.RadioBackendId : "none";
        RigctldHost = snapshot.RigctldHost;
        RigctldPort = snapshot.RigctldPort;
        HamlibModel = snapshot.HamlibModel;
        HamlibLibraryPath = snapshot.HamlibLibraryPath;
        // Save-time restart-warning baseline -- see RestartRequiredWarningRequested's own doc
        // comment. Captured here (dialog-open time), not re-captured by ResetRadioToDefault, so a
        // Save after "Reset to default" still correctly detects a real change against what was
        // actually loaded.
        _originalHamlibLibraryPath = snapshot.HamlibLibraryPath;
        HamlibSerialPort = snapshot.HamlibSerialPort;
        HamlibBaudRate = snapshot.HamlibBaudRate;
        // Normalize, not trust -- the old free-text field's own help text used to tell users to
        // type e.g. "RIG_PTT_SERIAL_DTR", which HamlibRadioProtocol's ctor actually rejects, and a
        // persisted null (a fresh install) selects nothing among the 4 fixed radio buttons either.
        // Falls back to "RIG" (CAT) -- the safest common default, needs no separate PTT port.
        // Code-review finding: a real-but-rarer Hamlib token this UI doesn't offer a button for
        // (RIGMICDATA/Parallel/CM108/GPIO/GPION) used to pass this check unchanged and render as a
        // blank radio group -- same symptom this normalization exists to prevent, just narrower.
        // Only the 4 values this UI can actually represent survive as-is.
        HamlibPttType = snapshot.HamlibPttType is "RIG" or "DTR" or "RTS" or "None"
            ? snapshot.HamlibPttType
            : "RIG";
        HamlibPttPort = snapshot.HamlibPttPort;
        FlrigHost = snapshot.FlrigHost;
        FlrigPort = snapshot.FlrigPort;
        Callsign = snapshot.Callsign;
        OperatorName = snapshot.OperatorName;
        OperatorGrid = snapshot.OperatorGrid;
        DefaultRst = snapshot.DefaultRst;
        AutoSyncEnabled = snapshot.AutoSyncEnabled;
        AutoSlantEnabled = snapshot.AutoSlantEnabled;
        AutoStopEnabled = snapshot.AutoStopEnabled;
        SyncRestartEnabled = snapshot.SyncRestartEnabled;
        SnrMeasurementEnabled = snapshot.SnrMeasurementEnabled;
        // Clamp, not trust -- a hand-edited settings.json can persist an out-of-range value; falls
        // back to index 0 ("Very low"), matching legacy's own SetSenseLvl switch `default:` branch
        // (see SstvDecoderSettings.SenseLevel's own doc comment for why 0, not 1, is the fallback
        // here specifically -- deliberately different from the absent-key default).
        SenseLevel = snapshot.SenseLevel is >= 0 and <= 3 ? snapshot.SenseLevel : 0;
        // Clamp, not trust -- same reasoning as SenseLevel above, but unlike SenseLevel both
        // fallbacks (absent AND out-of-range) resolve to the SAME value here (Hilbert), matching
        // SstvDecoderSettings.DemodType's own doc comment, not SenseLevel's "different fallback"
        // shape.
        DemodType = Enum.IsDefined(snapshot.DemodType) ? snapshot.DemodType : DemodType.Hilbert;
        // Clamp, not trust -- same reasoning as DemodType above, both fallbacks (absent AND
        // out-of-range) resolve to the SAME value here (Wide), matching
        // SstvDecoderSettings.RxBpfPreset's own doc comment.
        RxBpfPreset = Enum.IsDefined(snapshot.RxBpfPreset) ? snapshot.RxBpfPreset : RxBpfPreset.Wide;
        // Clamp, not trust -- same reasoning as DemodType/RxBpfPreset above, both fallbacks (absent
        // AND out-of-range) resolve to the SAME value here (On), matching
        // SstvDecoderSettings.RxBufferMode's own doc comment.
        RxBufferMode = Enum.IsDefined(snapshot.RxBufferMode) ? snapshot.RxBufferMode : RxBufferMode.On;
        // Options stub backlog item 1 -- no clamp needed (unlike the enums above), these are plain
        // numeric fields whose only "invalid" case is an unreachable-Nyquist cutoff, clamped
        // decoder-side (PllFmDemodulator.SetTuning) at apply time, not here at load time.
        PllVcoGain = snapshot.PllVcoGain;
        PllLoopOrder = snapshot.PllLoopOrder;
        PllLoopCutoffHz = snapshot.PllLoopCutoffHz;
        PllOutputOrder = snapshot.PllOutputOrder;
        PllOutputCutoffHz = snapshot.PllOutputCutoffHz;
        // Options stub backlog item 2 -- ZeroCrossingSmoothingMode clamped (same reasoning as
        // DemodType/RxBpfPreset/RxBufferMode above, falls back to Iir, matching
        // SstvDecoderSettings.ZeroCrossingSmoothingMode's own absent-value default -- this snapshot
        // field is always already-resolved, never genuinely absent, but a hand-edited settings.json
        // could still carry an out-of-range value through to here); the 3 numeric fields need no
        // clamp, same reasoning as the PLL numeric fields above.
        ZeroCrossingSmoothingMode = Enum.IsDefined(snapshot.ZeroCrossingSmoothingMode) ? snapshot.ZeroCrossingSmoothingMode : ZeroCrossingSmoothingMode.Iir;
        ZeroCrossingOutputOrder = snapshot.ZeroCrossingOutputOrder;
        ZeroCrossingOutputCutoffHz = snapshot.ZeroCrossingOutputCutoffHz;
        ZeroCrossingSmoothingFrequencyHz = snapshot.ZeroCrossingSmoothingFrequencyHz;
        // Options stub backlog item 3 -- no clamp needed, same reasoning as the PLL/zero-crossing
        // numeric fields above (OptionsSettingsService's own load path already resolves absent/
        // out-of-range values before this snapshot is ever constructed).
        TxBpfEnabled = snapshot.TxBpfEnabled;
        TxBpfTapCount = snapshot.TxBpfTapCount;
        TxLpfEnabled = snapshot.TxLpfEnabled;
        TxLpfFrequencyHz = snapshot.TxLpfFrequencyHz;
        QrzLookupEnabled = snapshot.QrzLookupEnabled;
        QrzLookupUsername = snapshot.QrzLookupUsername;
        // Clamp, not trust -- same reasoning as SenseLevel above: a hand-edited settings.json could
        // in principle carry an out-of-range enum value. Falls back to Off, matching CwIdMode's own
        // CLR/legacy default.
        CwIdMode = Enum.IsDefined(snapshot.CwIdMode) ? snapshot.CwIdMode : CwIdMode.Off;
        CwText = snapshot.CwText;
        CwWpm = snapshot.CwWpm;
        CwToneFrequencyHz = snapshot.CwToneFrequencyHz;
        FskIdTxEnabled = snapshot.FskIdTxEnabled;
        FskIdRxEnabled = snapshot.FskIdRxEnabled;
        CwIdRxEnabled = snapshot.CwIdRxEnabled;
        CwIdRxWindowSeconds = snapshot.CwIdRxWindowSeconds;
        NrRstEnabled = snapshot.NrRstEnabled;
        NrRstText = snapshot.NrRstText;
        SoundFileMmvPath = snapshot.SoundFileMmvPath;

        AdifUdpDestinations.Clear();
        foreach (var destination in snapshot.AdifUdpDestinations)
        {
            // Tier B audit finding: a null element (a hand-edited/partially-written settings.json can
            // produce "Destinations": [null] via System.Text.Json) used to NRE here and take down the
            // whole load -- matches AdifUdpStreamer.SendAsync's own `d?.Enabled == true` guard on the
            // same data, but skips the row entirely rather than showing a disabled placeholder for
            // data that was never really there.
            if (destination is null)
            {
                continue;
            }

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

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (await SaveCoreAsync())
        {
            await ShowPostSaveWarningsAsync();
            RequestClose?.Invoke();
        }
    }

    /// <summary>ui_transition_plan.md step 7 (T2-3): saves via <see cref="SaveCoreAsync"/> like
    /// <see cref="SaveAsync"/>, but does NOT close the dialog -- lets an operator persist a Radio/CAT
    /// change (or anything else) and keep editing, instead of Save-close-reopen just to reach
    /// Connect with the values Test just validated.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task ApplyAsync()
    {
        if (await SaveCoreAsync())
        {
            await ShowPostSaveWarningsAsync();
        }
    }

    /// <summary>Extracted from <see cref="SaveAsync"/>'s own post-save sequence (mechanical,
    /// behavior-preserving split) so <see cref="ApplyAsync"/> can run the identical three warnings
    /// without also closing the window.</summary>
    private async Task ShowPostSaveWarningsAsync()
    {
        // See RestartRequiredWarningRequested's own doc comment -- genuinely restart-required
        // with no live-apply, unlike everything else this dialog saves. _hasPendingDatabaseRelocation
        // (not IsConfirmingDatabaseRestart, and no longer PendingDatabaseDirectory is not null now that
        // it always holds a real path for display -- see that field's own doc comment) so a relocation
        // staged in an earlier dialog session, then left pending, still warns here even if the user
        // never revisits that row this time. HamlibLibraryPath is NOT part of this condition anymore
        // (restart-required-settings backlog item 5, 2026-08-28) -- it applies live now, see
        // HamlibLibraryReloadFailedWarningRequested below for its own (different-shaped) notice.
        var needsRestartWarning = _hasPendingDatabaseRelocation;
        if (needsRestartWarning && RestartRequiredWarningRequested is not null)
        {
            await RestartRequiredWarningRequested.Invoke();
        }

        // See SampleRateChangeDeferredWarningRequested's own doc comment. Independent of the
        // restart-warning check above -- both can fire on the same Save/Apply.
        if (_sampleRateChangeDeferred && SampleRateChangeDeferredWarningRequested is not null)
        {
            await SampleRateChangeDeferredWarningRequested.Invoke();
        }

        // See HamlibLibraryReloadFailedWarningRequested's own doc comment. Independent of both
        // checks above -- all three can fire on the same Save/Apply.
        if (_hamlibLibraryReloadFailed && HamlibLibraryReloadFailedWarningRequested is not null)
        {
            await HamlibLibraryReloadFailedWarningRequested.Invoke();
        }
    }

    /// <summary>Extracted from <see cref="SaveAsync"/> (mechanical, behavior-preserving split) so
    /// <see cref="RestartNowAsync"/> (Config/Database rows' Restart Now), <see cref="ApplyAsync"/>,
    /// and <see cref="ToggleRadioConnectionCommand"/>'s own Connect branch can save any dirty edit
    /// elsewhere in this dialog WITHOUT closing the window itself, and can check whether it
    /// actually succeeded -- the real <see cref="SaveAsync"/> used to signal failure only by
    /// silently not closing, with no way for another caller to observe that. Returns whether the
    /// save succeeded; <see cref="SaveAsync"/> itself still only closes the window on
    /// <c>true</c>, identical to its own prior externally-observable behavior.</summary>
    private async Task<bool> SaveCoreAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            return await SaveCoreUnguardedAsync();
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task<bool> SaveCoreUnguardedAsync()
    {
        // Reset every call -- see the fields' own doc comments for why SaveAsync reads these
        // afterward rather than this method's own bool return.
        _sampleRateChangeDeferred = false;
        _hamlibLibraryReloadFailed = false;
        HamlibLibraryReloadFailedMessage = null;
        SaveErrorMessage = null;

        // ui_transition_plan.md step 8 (T2-2): block Save only when the sound-file ID method is
        // actually SELECTED and a path is configured -- an unconfigured/off sound-file ID must never
        // block Save, matching every other field's own "nothing to validate when unused" convention.
        // Re-validates FRESH here rather than trusting SoundFileIdIsValid's live UI state, which can
        // be mid-debounce or stale (e.g. the file changed on disk since the last keystroke-triggered
        // check).
        if (IsIdMethodSoundFileSelected && !string.IsNullOrWhiteSpace(SoundFileMmvPath))
        {
            // Code-review finding: deliberately NOT ConfigureAwait(false) -- every other await in
            // this method stays on the UI thread so its property writes (this branch's own included)
            // are safe; a dropped SynchronizationContext here would carry through the rest of the
            // method's tail, not just this one write.
            var soundFileResult = await _sstvSession.ValidateStationIdSoundFileAsync(SoundFileMmvPath);
            if (!soundFileResult.IsValid)
            {
                SoundFileIdSuccessMessage = null;
                SoundFileIdErrorMessage = ResolveSoundFileIdFailureMessage(soundFileResult.Failure);
                SaveErrorMessage = _localization.GetString("Options.Radio.SoundFileId.Error.BlocksSave", SoundFileIdErrorMessage);
                return false;
            }
        }

        // The single most useful Debug line in the app for "why didn't my settings take effect"
        // bugs -- logs only the fields that are actually safe to log as-is (host/port/device ids/
        // sample rate/culture/backend id). Code-review correction: an earlier version of this
        // comment claimed "nothing secret-shaped exists in this snapshot today" -- FALSE as of
        // QrzLookupPassword's addition below; that field is deliberately never passed to this log
        // call.
        Log.SaveInvoked(_logger, RadioBackendId, SampleRate, SelectedCulture?.Name);

        var snapshot = new OptionsSnapshot(
            CultureCode: SelectedCulture?.Name,
            CaptureDeviceId: SelectedCaptureDevice?.Id,
            PlaybackDeviceId: SelectedPlaybackDevice?.Id,
            CaptureDeviceName: SelectedCaptureDevice?.Name,
            PlaybackDeviceName: SelectedPlaybackDevice?.Name,
            SampleRate: SampleRate,
            TxSampleRateOffsetHz: TxSampleRateOffsetHz,
            TxBpfEnabled: TxBpfEnabled,
            TxBpfTapCount: TxBpfTapCount,
            TxLpfEnabled: TxLpfEnabled,
            TxLpfFrequencyHz: TxLpfFrequencyHz,
            RadioBackendId: RadioBackendId,
            RigctldHost: RigctldHost,
            RigctldPort: RigctldPort,
            HamlibModel: HamlibModel,
            HamlibLibraryPath: HamlibLibraryPath,
            HamlibSerialPort: HamlibSerialPort,
            HamlibBaudRate: HamlibBaudRate,
            HamlibPttType: HamlibPttType,
            HamlibPttPort: HamlibPttPort,
            FlrigHost: FlrigHost,
            FlrigPort: FlrigPort,
            Callsign: Callsign,
            OperatorName: OperatorName,
            OperatorGrid: OperatorGrid,
            DefaultRst: DefaultRst,
            AutoSyncEnabled: AutoSyncEnabled,
            AutoSlantEnabled: AutoSlantEnabled,
            AutoStopEnabled: AutoStopEnabled,
            SyncRestartEnabled: SyncRestartEnabled,
            SenseLevel: SenseLevel,
            SnrMeasurementEnabled: SnrMeasurementEnabled,
            DemodType: DemodType,
            RxBpfPreset: RxBpfPreset,
            RxBufferMode: RxBufferMode,
            PllVcoGain: PllVcoGain,
            PllLoopOrder: PllLoopOrder,
            PllLoopCutoffHz: PllLoopCutoffHz,
            PllOutputOrder: PllOutputOrder,
            PllOutputCutoffHz: PllOutputCutoffHz,
            ZeroCrossingSmoothingMode: ZeroCrossingSmoothingMode,
            ZeroCrossingOutputOrder: ZeroCrossingOutputOrder,
            ZeroCrossingOutputCutoffHz: ZeroCrossingOutputCutoffHz,
            ZeroCrossingSmoothingFrequencyHz: ZeroCrossingSmoothingFrequencyHz,
            QrzLookupEnabled: QrzLookupEnabled,
            QrzLookupUsername: QrzLookupUsername,
            CaptureChannelSource: CaptureChannelSource,
            StereoTxEnabled: StereoTxEnabled,
            CwIdMode: CwIdMode,
            CwText: CwText,
            CwWpm: CwWpm,
            CwToneFrequencyHz: CwToneFrequencyHz,
            FskIdTxEnabled: FskIdTxEnabled,
            FskIdRxEnabled: FskIdRxEnabled,
            CwIdRxEnabled: CwIdRxEnabled,
            CwIdRxWindowSeconds: CwIdRxWindowSeconds,
            NrRstEnabled: NrRstEnabled,
            NrRstText: NrRstText,
            SoundFileMmvPath: SoundFileMmvPath,
            AdifUdpDestinations: AdifUdpDestinations.Select(row => row.ToDestination()).ToList());

        try
        {
            // Keyring first: a failed keyring write aborts the whole Save before settings.json is touched.
            if (!await SaveQrzPasswordIfChangedAsync())
            {
                return false;
            }

            await _optionsSettingsService.SaveAsync(snapshot);

            // User-reported (2026-09-20): fire immediately after the persist succeeds, not after
            // this whole method returns -- everything below this point (storage rows, live decoder
            // pushes) is unrelated to Callsign/OperatorName/OperatorGrid, so there's no reason to
            // delay the header chip / macro-preview refresh behind it.
            OperatorSettingsSaved?.Invoke();

            // Storage section (Images/Audio/Config/Database/Log directories, user-reported
            // 2026-09-01: "Auto save RX audio" only took effect via its OWN row Apply button, not
            // this window's general Apply/Save). All 5 rows were deliberately excluded from
            // OptionsSnapshot above -- see that field group's own doc comment -- because each has a
            // real side effect an accidental blank/incidental value must not trigger: Config/Log
            // physically MOVE a file on disk, Database only STAGES a pending change behind its own
            // restart-confirmation UI. Reusing each row's own Apply*Async method here (not
            // duplicating its logic) keeps a general Save's SUCCESS-path behavior equivalent to
            // clicking that row's own button -- same validation, same normalization, same live-apply.
            // NOT equivalent on the FAILURE path though: none of these five ever throws out to this
            // method (an internal try/catch sets that row's own ErrorMessage instead), but a general
            // Save still closes the dialog on success overall (SaveCommand) where the row's own Apply
            // would have left the window open with that error visible -- a genuine, if narrow, gap
            // (needs a real I/O failure, e.g. an unwritable target) accepted as a strict improvement
            // over "didn't apply at all," not fixed here.
            //
            // Gated on _storageLocationsLoaded (code-review finding): LoadStorageLocationsSafeAsync
            // is its OWN fire-and-forget, separate from the one _loadSucceeded/CanSave track -- a
            // Save that races ahead of (or outlives a failed) storage load must not write this
            // dialog's still-default/blank construction values out as if they were the user's real
            // current settings (blank is NOT a no-op downstream: it resolves to "reset to app
            // default" for Images/Audio/Log, unlike every other field this dialog owns).
            if (_storageLocationsLoaded)
            {
                await ApplyImagesDirectoryAsync();
                await ApplyAudioDirectoryAsync();

                // Gated on the value having actually changed (user-reported 2026-09-18: this row's
                // TextBox is now pre-filled with the current path, not blank, so a plain non-blank
                // check would re-apply it on every unrelated Save). Same shape as Database's own gate
                // below.
                if (!string.IsNullOrWhiteSpace(ConfigDirectoryInput) &&
                    !string.Equals(ConfigDirectoryInput, ConfigDirectory, StringComparison.Ordinal))
                {
                    await ApplyConfigDirectoryAsync();
                }

                // Gated on the value having actually changed since the last stage (code-review
                // finding), not just non-blank -- PendingDatabaseDirectory can be non-blank simply
                // because a relocation was already staged in an EARLIER session and the user never
                // touched this row this time. An unconditional non-blank gate would re-stage that
                // same value (a real directory-exists check against it) and re-set
                // IsConfirmingDatabaseRestart = true on every unrelated Save -- undoing an explicit
                // "Not Now" dismissal the moment the user saves anything else. See
                // _lastStagedDatabaseDirectory's own doc comment.
                if (!string.IsNullOrWhiteSpace(PendingDatabaseDirectory) &&
                    !string.Equals(PendingDatabaseDirectory, _lastStagedDatabaseDirectory, StringComparison.Ordinal))
                {
                    await ApplyDatabaseDirectoryAsync();
                }

                await ApplyLogDirectoryAsync();
            }

            // Squelch level (user-reported 2026-08-27, "Squelch level" live control): unlike every
            // OTHER SstvDecoderSettings field this dialog owns, SenseLevel is now genuinely live --
            // matches legacy's own Option.cpp:612-613 (SetSenseLvl() called on the live demodulator
            // on every OK, unconditionally). A plain synchronous call, not fire-and-forget: this is
            // Interlocked.Exchange plus a short lock, effectively non-throwing, unlike the real async
            // I/O just above/below it. Idempotent if unchanged (re-applying the same value is a safe
            // no-op, see AnalogFmSstvDecoder.ApplyPendingSenseLevelRequest's own doc comment), so no
            // need to diff against the previously-saved value first. The Receive tab's own "Squelch
            // level" dropdown (RxImagePaneViewModel) is refreshed separately, on this window's own
            // Closed event (see MainWindow.axaml.cs) -- this call is what keeps the running decoder
            // itself in sync with an Options-driven change.
            _sstvSession.RequestSenseLevel(SenseLevel);

            // Auto-Sync/Auto-Stop/Auto-Slant/Sync-Restart (2026-08-27, restart-required-settings
            // backlog item 1): same reasoning as RequestSenseLevel immediately above -- these four
            // are now ALSO genuinely live, no separate PersistXAsync needed (see
            // ISstvSessionService.RequestAutoSyncEnabled's own doc comment for why: none of these
            // four has a standalone out-of-dialog control, so OptionsSettingsService.SaveAsync above
            // already persists them as part of the whole-dialog snapshot). AutoSlantEnabled's own
            // Receive-tab status text is refreshed separately, on this window's own Closed event
            // (see MainWindow.axaml.cs), same pattern as SenseLevel's dropdown above.
            _sstvSession.RequestAutoSyncEnabled(AutoSyncEnabled);
            _sstvSession.RequestAutoStopEnabled(AutoStopEnabled);
            _sstvSession.RequestAutoSlantEnabled(AutoSlantEnabled);
            _sstvSession.RequestSyncRestartEnabled(SyncRestartEnabled);
            _sstvSession.RequestSnrMeasurementEnabled(SnrMeasurementEnabled);

            // RX BPF preset/Demod type/RX buffer mode (2026-08-27, restart-required-settings backlog
            // item 2): same unconditional-every-Save convention as RequestSenseLevel/RequestAutoSyncEnabled
            // above -- ISstvSessionService.RequestReconfiguration's own doc comment covers why no diff
            // check is needed first (an equality guard against the currently-COMMITTED decoder value
            // already lives inside RestartableSstvDecoder itself). Unlike those, this one is idle-GATED,
            // not applied immediately -- the Receive tab's own Input Chain "BPF" row is refreshed
            // separately, via ISstvSessionService.DecoderInstanceReplaced (RxImagePaneViewModel), not
            // this window's Closed event (see that property's own doc comment for why a pull-based
            // Closed-event refresh would be redundant here).
            _sstvSession.RequestReconfiguration(RxBpfPreset, DemodType, RxBufferMode);

            // PLL demodulator tuning (Options stub backlog item 1, docs/plans/options-stub-item1-pll-tuning-plan.md):
            // same unconditional-every-Save convention as RequestSenseLevel above -- applied
            // immediately (not idle-gated like RequestReconfiguration just above), matching legacy's
            // own live-edit shape (Option.cpp's Save handler calls CPLL::SetVcoGain/MakeLoopLPF/
            // MakeOutLPF directly on the live decoder, no restart).
            _sstvSession.RequestPllTuning(PllVcoGain, PllLoopOrder, PllLoopCutoffHz, PllOutputOrder, PllOutputCutoffHz);

            // Zero-crossing demodulator tuning (Options stub backlog item 2,
            // docs/plans/options-stub-item2-zerocrossing-tuning-plan.md): same shape as PLL tuning
            // immediately above -- legacy's own live-edit shape (Option.cpp's Save handler calls
            // CFQC::CalcLPF directly on the live decoder, no restart).
            _sstvSession.RequestZeroCrossingTuning(ZeroCrossingSmoothingMode, ZeroCrossingOutputOrder, ZeroCrossingOutputCutoffHz, ZeroCrossingSmoothingFrequencyHz);

            // Sample rate (2026-08-27/28, restart-required-settings backlog item 4): genuinely live
            // now too, no longer requiring a restart. Different in
            // kind from every Request* call above -- this can reopen the actual RX capture device, so
            // it gets its OWN try/catch (not folded into the shared try this method is already inside)
            // -- a thrown TimeoutException/failure here must not abort the unrelated writes already
            // committed above (window geometry/JpegQuality/culture below), matching the established
            // SetCultureAsync precedent just below. DeferredRecordingInProgress: the new rate was
            // ALREADY persisted by the whole-dialog snapshot save above (OptionsSnapshot.SampleRate,
            // `await _optionsSettingsService.SaveAsync(snapshot)`), so it takes effect on the next
            // restart regardless of what happens here -- this branch only decides whether the LIVE
            // apply happens now or not, and sets _sampleRateChangeDeferred so SaveAsync can tell the
            // user (2026-08-28, user-requested).
            try
            {
                var sampleRateResult = await _sstvSession.RequestSampleRateAsync(SampleRate);
                if (sampleRateResult == SampleRateApplyResult.DeferredRecordingInProgress)
                {
                    _sampleRateChangeDeferred = true;
                    Log.SampleRateChangeDeferred(_logger, SampleRate);
                }
                else if (sampleRateResult == SampleRateApplyResult.Rejected)
                {
                    Log.SampleRateChangeRejected(_logger, SampleRate);
                }
            }
            catch (Exception ex)
            {
                Log.SampleRateChangeFailed(_logger, SampleRate, ex);
            }

            // Capture (RX input) device (2026-09-01, user-reported "requires restart to actually
            // work" bug): genuinely live now too, same shape as sample rate immediately above --
            // ISstvSessionService.RequestCaptureDeviceAsync already existed (built for the
            // configuration-preset apply path, ConfigurationPresetService.cs) but this dialog's own
            // Save never called it, so a device picked here only took effect on the next app restart.
            // Called unconditionally, not gated on a local "did the selection change" check -- the
            // session's own no-op guard (compares against its in-memory _activeCaptureDeviceId, see
            // CaptureDeviceApplyResult.NoChange's own doc comment) already makes an unrelated Save a
            // cheap no-op ONCE that latch has been set by a prior apply/RX start this run (before
            // that, it's null and every Save -- even an unrelated one -- takes the idle-apply path;
            // still harmless, since no capture is touched while RX isn't running, see
            // ApplyCaptureDeviceLockedAsync's own wasReceiving check), matching RequestSampleRateAsync's
            // own unconditional-call convention above. Playback (TX output) device needed NO
            // equivalent fix -- unlike capture, TX opens a fresh native playback stream per
            // transmission and already re-reads AudioDeviceSettings.PlaybackDeviceId from the settings
            // store on every single transmission, proven empirically by
            // TransmitAsync_PlaybackDeviceChangedInSettingsBetweenTwoTransmissions_UsesTheNewDeviceOnEveryCall
            // in SstvSessionServiceTests.cs (two transmissions, settings mutated in between, both
            // resolve their own current value) -- so the whole-dialog snapshot save above was already
            // sufficient for it. 2nd-round auditor finding, not yet addressed: neither the Rejected
            // nor the thrown-exception outcome below gets a user-facing notice or VM reconciliation --
            // same scope-limited treatment RequestSampleRateAsync's own Rejected case gets above,
            // tracked as a follow-up rather than blocking this fix (which already resolves the
            // reported "requires restart" bug for the overwhelmingly common case).
            try
            {
                var captureDeviceResult = await _sstvSession.RequestCaptureDeviceAsync(SelectedCaptureDevice?.Id, SelectedCaptureDevice?.Name);
                if (captureDeviceResult == CaptureDeviceApplyResult.DeferredRecordingInProgress)
                {
                    Log.CaptureDeviceChangeDeferred(_logger, SelectedCaptureDevice?.Id);
                }
                else if (captureDeviceResult == CaptureDeviceApplyResult.Rejected)
                {
                    // Rejected is only returned when ApplyCaptureDeviceLockedAsync's OWN restart
                    // attempt on the rolled-back device SUCCEEDED (see its own doc comment) -- RX
                    // capture is still running here, just on whatever fallback device that restart
                    // resolved, not the one the user just picked. It's the CATCH block below (a
                    // thrown exception, meaning even that restart attempt failed) that actually
                    // leaves RX capture stopped -- these two log messages describe different outcomes
                    // on purpose, not swapped.
                    Log.CaptureDeviceChangeRejected(_logger, SelectedCaptureDevice?.Id);
                }
            }
            catch (Exception ex)
            {
                // Both real callers persist the NEW device before calling RequestCaptureDeviceAsync
                // (the whole-dialog snapshot save above, for this caller), so a THROW here (unlike
                // Rejected immediately above) means ApplyCaptureDeviceLockedAsync's own rollback
                // restart attempt ALSO failed on that same already-persisted device -- RX capture is
                // left stopped, and the unopenable device stays persisted across a future restart too.
                Log.CaptureDeviceChangeFailed(_logger, SelectedCaptureDevice?.Id, ex);
            }

            // Hamlib library path (2026-08-28, restart-required-settings backlog item 5 -- the LAST
            // item of this backlog): genuinely live now too. Different in kind from every other
            // Request* call above -- it performs a real, uncancellable native library load, so it
            // gets its OWN try/catch (a thrown TimeoutException from a concurrent reload still in
            // progress must not abort the unrelated writes below, same reasoning as the sample-rate
            // block above). Only called when the path actually changed (normalized so a blank-to-blank
            // reformat, e.g. null vs "", doesn't count as a change -- IHamlibLibraryReconfiguration's
            // own doc comment: every call performs a fresh native load that is NEVER unloaded, so
            // calling this unconditionally on every Save would leak a new loaded copy every time,
            // whether or not the field was actually touched).
            if (!HamlibPathsAreEquivalent(HamlibLibraryPath, _originalHamlibLibraryPath))
            {
                try
                {
                    var reloadResult = await _radioSession.RequestHamlibLibraryPathAsync(HamlibLibraryPath);
                    if (reloadResult is { Applied: true })
                    {
                        // Only refresh the baseline on success -- see _originalHamlibLibraryPath's own
                        // doc comment for why a failed reload must NOT refresh it (a later Save must
                        // still be able to retry).
                        _originalHamlibLibraryPath = HamlibLibraryPath;
                        Log.HamlibLibraryPathApplied(_logger, reloadResult.ResolvedPath ?? "(unknown)");
                    }
                    else if (reloadResult is { Applied: false } rejected)
                    {
                        _hamlibLibraryReloadFailed = true;
                        HamlibLibraryReloadFailedMessage = _localization.GetString(
                            "Options.HamlibLibraryReloadFailedDialog.Message", string.Join("; ", rejected.Attempts));
                        if (_logger.IsEnabled(LogLevel.Warning))
                        {
                            // CA1873 doesn't recognize an IsEnabled guard around a custom
                            // [LoggerMessage] partial method (only the canonical ILogger.LogXxx
                            // extensions) -- the guard above already skips string.Join when logging
                            // is disabled, which is the rule's real intent.
#pragma warning disable CA1873
                            Log.HamlibLibraryPathRejected(_logger, string.Join("; ", rejected.Attempts));
#pragma warning restore CA1873
                        }
                    }

                    // reloadResult is null when the registered IRadioProtocolFactory doesn't
                    // implement IHamlibLibraryReconfiguration at all (e.g. a fake used by a test) --
                    // matches every other is-test-gated Request* call's own silent-skip convention.
                }
                catch (Exception ex)
                {
                    // Round-2 plan-review finding: must NOT be a silent swallow (the nearest-shaped
                    // precedent, SetCultureAsync's own catch below, logs-and-drops) -- a
                    // TimeoutException here means the path change genuinely did not apply, and the
                    // user needs to see that, not just have it vanish into a log line.
                    _hamlibLibraryReloadFailed = true;
                    HamlibLibraryReloadFailedMessage = _localization.GetString(
                        "Options.HamlibLibraryReloadFailedDialog.Message", ex.Message);
                    Log.HamlibLibraryPathReloadThrew(_logger, ex);
                }
            }

            // SWR auto-cutoff (2026-08-26): NOT part of `snapshot`/OptionsSnapshot above -- RadioSafety
            // is its own settings section, saved through IRadioSessionService.SaveSafetySettingsAsync.
            // T0-2: both this call and the geometry/ImageExport write below now go through
            // ISettingsStore.UpdateAsync, so they're safe against each other (and any other concurrent
            // writer) regardless of sequencing -- no longer safe "only because this call happens to be
            // sequenced between two whole-document saves," as it was before UpdateAsync existed.
            // Also raises IRadioSessionService.SafetySettingsChanged, which is how
            // TxControlsPaneViewModel's live enforcement picks up the new value without being
            // reconstructed.
            await _radioSession.SaveSafetySettingsAsync(new RadioSafetySpec(SwrCutoffEnabled, SwrCutoffThreshold));

            // See RememberWindowPosition's own doc comment for why this bypasses
            // _optionsSettingsService entirely. Preserves Left/Top/Width/Height as-is -- those are
            // MainWindow's own domain (captured passively on Closing), not user-edited fields here.
            // T0-2: both captured into locals here, before UpdateAsync -- both are [ObservableProperty]
            // (UI-thread-affine), and UpdateAsync's own mutate lambda may run on any thread, so they
            // must not be read from inside it. One atomic UpdateAsync call, not two independent
            // LoadAsync/SaveAsync round trips (that's what let this whole-document save race a
            // concurrent writer of e.g. AudioDeviceSettings.TxVolumePercent in the first place).
            var rememberWindowPosition = RememberWindowPosition;
            var jpegQuality = JpegQuality;
            var appTheme = AppTheme;
            var fontScale = FontScale;
            var decodingIndicatorBlinks = DecodingIndicatorBlinks;
            var drmUpdate = CaptureDrmOptionsUpdate();
            await _settingsStore.UpdateAsync(appSettings =>
            {
                var currentDrm = appSettings.GetSection(DrmSettings.SectionKey, DrmSettingsJsonContext.Default.DrmSettings) ?? new DrmSettings();
                appSettings = appSettings.WithSection(DrmSettings.SectionKey, drmUpdate(currentDrm), DrmSettingsJsonContext.Default.DrmSettings);
                var currentGeometry = appSettings.GetSection(WindowGeometrySettings.SectionKey, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings) ?? new WindowGeometrySettings();
                var updatedAppSettings = appSettings.WithSection(
                    WindowGeometrySettings.SectionKey, currentGeometry with { RememberWindowPosition = rememberWindowPosition }, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings);
                var currentImageExport = appSettings.GetSection(ImageExportSettings.SectionKey, ImageExportSettingsJsonContext.Default.ImageExportSettings) ?? new ImageExportSettings();
                updatedAppSettings = updatedAppSettings.WithSection(
                    ImageExportSettings.SectionKey, currentImageExport with { JpegQuality = jpegQuality }, ImageExportSettingsJsonContext.Default.ImageExportSettings);
                var currentAppearance = appSettings.GetSection(AppearanceSettings.SectionKey, AppearanceSettingsJsonContext.Default.AppearanceSettings) ?? new AppearanceSettings();
                return updatedAppSettings.WithSection(
                    AppearanceSettings.SectionKey, currentAppearance with { Theme = appTheme, FontScale = fontScale, DecodingIndicatorBlinks = decodingIndicatorBlinks }, AppearanceSettingsJsonContext.Default.AppearanceSettings);
            });
            // Live-apply, same reasoning as the Theme/FontScale blocks immediately below -- an
            // already-open RadioStatusViewModel must react to this without an app restart.
            _appearanceSettings.NotifyDecodingIndicatorBlinksChanged(decodingIndicatorBlinks);

            // Phase 1 dark mode: live-apply, own try/catch (same "one field's failure must not
            // abort the other unrelated writes in this Save" reasoning as the sample-rate/capture-
            // device live-apply blocks above) -- safe directly on the UI thread, no
            // ConfigureAwait(false) anywhere in this method (see this method's own doc comment).
            try
            {
                // Fully qualified: bare "Application" is ambiguous with the ScanlineStudio.Application
                // project's own namespace (this file already references it), same reasoning as
                // App.axaml.cs's own doc comment on its class declaration.
                Avalonia.Application.Current!.RequestedThemeVariant = appTheme switch
                {
                    AppTheme.Light => ThemeVariant.Light,
                    AppTheme.Dark => ThemeVariant.Dark,
                    AppTheme.System => ThemeVariant.Default,
                    _ => ThemeVariant.Light,
                };
            }
            catch (Exception ex)
            {
                Log.AppThemeApplyFailed(_logger, appTheme, ex);
            }

            // Phase 2 font-size presets: own separate try/catch, same "one field's failure must not
            // abort the other" reasoning as the theme live-apply block immediately above -- kept
            // independent rather than merged into it.
            try
            {
                App.ApplyFontScale(fontScale);
            }
            catch (Exception ex)
            {
                Log.FontScaleApplyFailed(_logger, fontScale, ex);
            }

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
            // ui_transition_plan.md step 7 (T2-3): this used to signal failure ONLY by the caller
            // not closing the dialog, with no message anywhere -- now every SaveCoreAsync caller
            // (Save, Apply, Connect's implicit save) can show the operator what actually went wrong.
            SaveErrorMessage = ex.Message;
            return false;
        }

        return true;
    }
}
