using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Localization;
using ScanlineStudio.Core.Logbook;
using ScanlineStudio.Core.Radio;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application;

/// <summary>See <see cref="IConfigurationPresetService"/>. Configurations-preset backlog, Phase 3
/// (2026-08-28) -- 2 plan-review rounds; round 2's 4 corrections are all reflected here (a MERGE, not
/// a wholesale settings.json overwrite; culture/UI-refresh returned to the caller, never applied
/// here; an up-front reject for TX-in-flight/recording, not a late one; an unconditional rate+device
/// push and a per-DECISION Radio-section diff, not per-section). Round-1 CODE-review (as opposed to
/// the plan-review above) added 3 more fixes on top, all reflected here too: a single-flight guard
/// (nothing previously stopped two concurrent switches from interleaving), a per-push try/catch so a
/// thrown exception from one section's live push can't abort every section after it (this class'
/// nearest precedent, <c>OptionsWindowViewModel.SaveCoreAsync</c>, already does this for its own
/// sample-rate/Hamlib pushes), and dropping the audio section's rate/device DIFF entirely in favor of
/// always calling both <c>Request*Async</c> methods whenever the section is present at all -- both
/// already have their OWN authoritative live-state no-op guards, so diffing against a "previous
/// settings snapshot" here was redundant AND wrong in one direction the round-1 code-review found:
/// that snapshot can disagree with live state in EITHER direction (not just the "diff says changed,
/// live says no-change" case Correction 4 originally covered), and only the callee's own guard is
/// authoritative.</summary>
public sealed partial class ConfigurationPresetService : IConfigurationPresetService
{
    private readonly IConfigurationPresetStore _presetStore;
    private readonly ISettingsStore _settingsStore;
    private readonly ISstvSessionService _sstvSession;
    private readonly IRadioSessionService _radioSession;
    private readonly ILogger<ConfigurationPresetService> _logger;

    // Round-1 code-review fix: Interlocked.CompareExchange over a plain int, matching
    // SstvSessionService's own _transmitInFlight idiom (this class has no other shared mutable state
    // to protect, so a full SemaphoreSlim would be more machinery than the job needs).
    private int _switchInFlight;

    public ConfigurationPresetService(
        IConfigurationPresetStore presetStore, ISettingsStore settingsStore, ISstvSessionService sstvSession,
        IRadioSessionService radioSession, ILogger<ConfigurationPresetService> logger)
    {
        _presetStore = presetStore;
        _settingsStore = settingsStore;
        _sstvSession = sstvSession;
        _radioSession = radioSession;
        _logger = logger;
    }

    public async Task<ConfigurationPresetSwitchResult> SwitchToPresetAsync(string name, CancellationToken ct = default)
    {
        Log.SwitchRequested(_logger, name);

        // Step 0: single-flight guard, BEFORE even the TX/recording checks below -- two concurrent
        // switches must never both pass those checks and then race on the same settings snapshot.
        if (Interlocked.CompareExchange(ref _switchInFlight, 1, 0) != 0)
        {
            Log.SwitchRejectedConcurrent(_logger, name);
            return ConfigurationPresetSwitchResult.RejectedConcurrentSwitch;
        }

        try
        {
            // Step 1: reject UP FRONT, before touching anything -- round-2 plan-review finding: a LATE
            // reject (discovered partway through, the way a single setting's own deferral works) can't
            // actually keep the "whole switch either applies or doesn't" promise once settings are
            // already partway overwritten. No existing precedent silently aborts a live transmission
            // (unlike RX, which sample-rate/BPF/device changes already abort routinely).
            if (_sstvSession.IsTransmitting)
            {
                Log.SwitchRejectedTransmitting(_logger, name);
                return ConfigurationPresetSwitchResult.RejectedTransmitting;
            }

            if (_sstvSession.IsRecording)
            {
                Log.SwitchRejectedRecording(_logger, name);
                return ConfigurationPresetSwitchResult.RejectedRecording;
            }

            // Step 2 (before step 3's snapshot -- an absent preset means nothing else needs touching or
            // even reading).
            var preset = await _presetStore.LoadPresetAsync(name, ct).ConfigureAwait(false);
            if (preset is null)
            {
                Log.SwitchPresetNotFound(_logger, name);
                return ConfigurationPresetSwitchResult.NotFound;
            }

            // Step 3: snapshot BEFORE overwriting anything -- the step-6 diff needs both "before" (this)
            // and "after" (the preset's own content, since every key present in the preset determines
            // exactly what a live re-read of the merged settings would show for that key -- no need to
            // re-load after saving).
            var previous = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);

            // Steps 4-5: the bulk section merge and the active-preset marker write -- see
            // MergeIntoLiveSettingsAsync's own doc comment for the T0-2 collapse to one atomic call.
            await MergeIntoLiveSettingsAsync(preset, name, ct).ConfigureAwait(false);

            // Step 6: push every Application-layer-reachable section live. Each push is independently
            // try/catch'd -- round-1 code-review finding: without this, a single thrown TimeoutException
            // (any of RequestSampleRateAsync/RequestCaptureDeviceAsync/RequestHamlibLibraryPathAsync can
            // throw one on a busy gate) would abort every push after it, while settings.json and the
            // active-preset marker are ALREADY fully committed to the new preset -- and the switch is
            // NOT idempotent, since a retry diffs the already-merged settings against themselves and
            // finds nothing changed. OperationCanceledException is deliberately let through uncaught --
            // real cancellation must still propagate, not read as "partially applied."
            var rxAudioDeferred = false;
            var partiallyApplied = false;

            try
            {
                var audioResult = await PushAudioChangesAsync(preset, ct).ConfigureAwait(false);
                rxAudioDeferred = audioResult.Deferred;
                partiallyApplied |= audioResult.Rejected;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                partiallyApplied = true;
                Log.SwitchAudioPushFailed(_logger, name, ex);
            }

            try
            {
                PushDecoderChanges(previous, preset);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                partiallyApplied = true;
                Log.SwitchDecoderPushFailed(_logger, name, ex);
            }

            try
            {
                await PushRadioSafetyChangesAsync(previous, preset, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                partiallyApplied = true;
                Log.SwitchRadioSafetyPushFailed(_logger, name, ex);
            }

            try
            {
                await PushRadioConnectionChangesAsync(previous, preset, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                partiallyApplied = true;
                Log.SwitchRadioConnectionPushFailed(_logger, name, ex);
            }

            // ui_transition_plan.md step 12 (Auto-save RX audio), Step 4: auditor-caught round 1 --
            // ISstvSessionService's own SetAutoSaveAudioEnabled/SetAudioDirectory are volatile fields
            // cached at the decode path, never re-read from settings on their own, so without this
            // push a preset switch left capture following the PREVIOUS preset's enable flag/directory
            // until an app restart, even though Options and the Gallery Storage card both already show
            // the new preset's values.
            try
            {
                PushReceiveHistoryChanges(preset);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                partiallyApplied = true;
                Log.SwitchReceiveHistoryPushFailed(_logger, name, ex);
            }

            // Round-1 code-review fix: CultureChanged is the only reliable "did it change" signal --
            // see ConfigurationPresetSwitchResult's own doc comment for why CultureToApply alone
            // (null-vs-unchanged) can't distinguish "no change" from "changed TO null/default."
            var oldCulture = previous.GetSection(LocalizationSettings.SectionKey, LocalizationSettingsJsonContext.Default.LocalizationSettings)?.CultureCode;
            var cultureSectionPresent = preset.Sections.ContainsKey(LocalizationSettings.SectionKey);
            var newCulture = cultureSectionPresent
                ? preset.GetSection(LocalizationSettings.SectionKey, LocalizationSettingsJsonContext.Default.LocalizationSettings)?.CultureCode
                : oldCulture;
            var cultureChanged = cultureSectionPresent && newCulture != oldCulture;

            Log.SwitchApplied(_logger, name, rxAudioDeferred, partiallyApplied);
            return new ConfigurationPresetSwitchResult(
                ConfigurationPresetSwitchOutcome.Applied,
                CultureChanged: cultureChanged,
                CultureToApply: cultureChanged ? newCulture : null,
                RxAudioDeferred: rxAudioDeferred,
                PartiallyApplied: partiallyApplied);
        }
        finally
        {
            Volatile.Write(ref _switchInFlight, 0);
        }
    }

    /// <summary>Steps 4-5, T0-2: ONE atomic <see cref="ISettingsStore.UpdateAsync"/> call covering
    /// both the bulk section merge and the active-preset marker write. Previously two SEPARATE
    /// load/save round trips, with the marker write (step 5) deliberately re-loading FRESH right
    /// before its own write specifically to MINIMIZE (not close) the window a concurrent writer's
    /// own change could be clobbered in -- that reasoning predates <see cref="ISettingsStore.UpdateAsync"/>
    /// existing; once it exists there's no remaining reason to accept any window at all, so this
    /// collapses to one call.</summary>
    private async Task MergeIntoLiveSettingsAsync(AppSettings preset, string name, CancellationToken ct)
    {
        await _settingsStore.UpdateAsync(current =>
        {
            // Step 4: MERGE every key PRESENT IN THE PRESET onto the current live settings -- NOT a
            // wholesale AppSettings.SaveAsync(preset), which would delete every section Phase 2's
            // store deliberately excludes (WindowGeometry/TxPaneUi/RxPaneUi) from the live
            // settings.json on every single switch (round-2 plan-review blocker). Raw dictionary
            // merge, not WithSection<T>-per-key -- this class never needs to know any individual
            // section's own TYPE to merge it, only that it's a JsonElement blob (same
            // "ScanlineStudio.Settings itself never needs to know any module-specific type" property
            // AppSettings.cs's own doc comment describes, reused here one layer up). Keeps the
            // CURRENT SchemaVersion, not the preset's (Phase 2 round-2 decision 4) -- `with { Sections
            // = ... }` only replaces Sections.
            var merged = new Dictionary<string, System.Text.Json.JsonElement>(current.Sections);
            foreach (var (key, value) in preset.Sections)
            {
                merged[key] = value;
            }

            // T0-8: ConfigurationPresetStore.Sanitize strips Password/ApiKey from every saved/
            // loaded preset (presets are user-shareable files) -- without this, applying ANY
            // preset that carries a QrzLookup/QrzUpload section would silently wipe the live
            // secret (Enabled stays true, so QRZ lookups/uploads then start failing) and force the
            // user to retype it after every switch, the opposite of the point of field-level (not
            // whole-section) redaction. Only touches a section the preset actually carried -- if
            // the preset has no QrzLookup/QrzUpload section at all, `merged` still holds current's
            // own original entry untouched, nothing to do.
            // Code-review nit: a malformed live/preset section (hand-edited settings.json/preset
            // file) would otherwise throw JsonException out of the whole switch, unguarded --
            // wrapped so one corrupt QRZ section degrades to "carry-forward skipped for this
            // section" rather than aborting every other section's own merge.
            if (preset.Sections.ContainsKey(QrzLookupSettings.SectionKey))
            {
                try
                {
                    var livePassword = current.GetSection(QrzLookupSettings.SectionKey, QrzLookupSettingsJsonContext.Default.QrzLookupSettings)?.Password;
                    var mergedQrzLookup = System.Text.Json.JsonSerializer.Deserialize(merged[QrzLookupSettings.SectionKey], QrzLookupSettingsJsonContext.Default.QrzLookupSettings) ?? new QrzLookupSettings();
                    merged[QrzLookupSettings.SectionKey] = System.Text.Json.JsonSerializer.SerializeToElement(
                        mergedQrzLookup with { Password = livePassword }, QrzLookupSettingsJsonContext.Default.QrzLookupSettings);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    Log.QrzSecretCarryForwardFailed(_logger, QrzLookupSettings.SectionKey, ex);
                }
            }

            if (preset.Sections.ContainsKey(QrzUploadSettings.SectionKey))
            {
                try
                {
                    var liveApiKey = current.GetSection(QrzUploadSettings.SectionKey, QrzUploadSettingsJsonContext.Default.QrzUploadSettings)?.ApiKey;
                    var mergedQrzUpload = System.Text.Json.JsonSerializer.Deserialize(merged[QrzUploadSettings.SectionKey], QrzUploadSettingsJsonContext.Default.QrzUploadSettings) ?? new QrzUploadSettings();
                    merged[QrzUploadSettings.SectionKey] = System.Text.Json.JsonSerializer.SerializeToElement(
                        mergedQrzUpload with { ApiKey = liveApiKey }, QrzUploadSettingsJsonContext.Default.QrzUploadSettings);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    Log.QrzSecretCarryForwardFailed(_logger, QrzUploadSettings.SectionKey, ex);
                }
            }

            // Step 5: re-set the active-preset marker, against the SAME snapshot the merge above just
            // built (current is a fresh AppSettings.UpdateAsync gave this lambda, not a stale outer
            // read -- that's what makes the two-round-trip precedent above obsolete).
            var withSections = current with { Sections = merged };
            return withSections.WithSection(
                ConfigurationPresetSettings.SectionKey, new ConfigurationPresetSettings { ActivePresetName = name },
                ConfigurationPresetSettingsJsonContext.Default.ConfigurationPresetSettings);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The rate and device fields of <c>AudioDeviceSettings</c>, pushed UNCONDITIONALLY --
    /// no "previous settings snapshot" diff -- whenever the section is present in the preset at all.
    /// Round-1 code-review finding, correcting the original Correction-4 design: a diff against
    /// "previous settings" can disagree with live state in EITHER direction, not just the one case
    /// Correction 4 originally patched (diff-says-changed, live-says-no-change, handled by the old
    /// sequential-with-fallback branch below it). The MIRROR case -- diff-says-UNCHANGED while live
    /// state actually differs (e.g. a previously-<see cref="SampleRateApplyResult.Rejected"/> rate
    /// change leaves settings.json at the new rate but the decoder still at the old one) -- was never
    /// handled at all, and a preset carrying that same "new" rate would diff as unchanged and never
    /// heal it. Both <see cref="ISstvSessionService.RequestSampleRateAsync"/> and
    /// <see cref="ISstvSessionService.RequestCaptureDeviceAsync"/> already have their OWN authoritative
    /// live-state no-op guards (against <c>_decoder.SampleRate</c>/<c>_activeCaptureDeviceId</c>
    /// respectively) that make an actually-unchanged call cheap and side-effect-free -- including when
    /// a rate change's own restart already reopened capture on the right device, which updates
    /// <c>_activeCaptureDeviceId</c> too, so the immediately-following device call below still no-ops
    /// correctly rather than triggering a second, redundant restart. Returns whether either push
    /// degraded to a deferred (recording-in-progress) or an outright rejected outcome.</summary>
    private async Task<(bool Deferred, bool Rejected)> PushAudioChangesAsync(AppSettings preset, CancellationToken ct)
    {
        if (!preset.Sections.ContainsKey(AudioDeviceSettings.SectionKey))
        {
            return (Deferred: false, Rejected: false);
        }

        var newAudio = preset.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings) ?? new AudioDeviceSettings();

        var deferred = false;
        var rejected = false;

        var rateResult = await _sstvSession.RequestSampleRateAsync(newAudio.SampleRate, ct).ConfigureAwait(false);
        if (rateResult == SampleRateApplyResult.DeferredRecordingInProgress)
        {
            deferred = true;
        }
        else if (rateResult == SampleRateApplyResult.Rejected)
        {
            rejected = true;
        }

        var deviceResult = await _sstvSession.RequestCaptureDeviceAsync(newAudio.CaptureDeviceId, newAudio.CaptureDeviceName, ct).ConfigureAwait(false);
        if (deviceResult == CaptureDeviceApplyResult.DeferredRecordingInProgress)
        {
            deferred = true;
        }
        else if (deviceResult == CaptureDeviceApplyResult.Rejected)
        {
            rejected = true;
        }

        return (deferred, rejected);
    }

    /// <summary>The SstvDecoderSettings bundle -- pushed as a WHOLE, unconditionally, whenever the
    /// section's own RESOLVED values differ at all, matching <c>OptionsWindowViewModel.SaveCoreAsync</c>'s
    /// own established convention (every one of these Request* calls except SampleRate has no
    /// standalone no-op guard of its own -- see that method's own doc comment for why none is
    /// needed). Diffs via <see cref="SstvDecoderSettings.Resolve"/> (RESOLVED, concrete values, the
    /// SAME resolution `Program.CreateSstvDecoder`/the loopback self-test already use) rather than
    /// the raw nullable fields directly -- a null vs. explicit-same-as-default field must NOT read as
    /// "changed." NOTE: <see cref="ResolvedSstvDecoderSettings.AfcEnabled"/> is part of this diffed
    /// bundle but has NO live-push path of its own -- <c>RestartableSstvDecoder</c> only reads it once,
    /// at construction. An AfcEnabled-only preset difference still fires every call below (harmlessly,
    /// each is a no-op against unchanged values) but does not actually apply the new AFC setting live;
    /// that field stays restart-required.</summary>
    private void PushDecoderChanges(AppSettings previous, AppSettings preset)
    {
        if (!preset.Sections.ContainsKey(SstvDecoderSettings.SectionKey))
        {
            return;
        }

        var oldResolved = (previous.GetSection(SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings) ?? new SstvDecoderSettings()).Resolve();
        var newSettings = preset.GetSection(SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings) ?? new SstvDecoderSettings();
        var newResolved = newSettings.Resolve();

        if (oldResolved == newResolved)
        {
            return;
        }

        _sstvSession.RequestSenseLevel(newResolved.SenseLevel);
        _sstvSession.RequestAutoSyncEnabled(newResolved.AutoSyncEnabled);
        _sstvSession.RequestAutoStopEnabled(newResolved.AutoStopEnabled);
        _sstvSession.RequestAutoSlantEnabled(newResolved.AutoSlantEnabled);
        _sstvSession.RequestSyncRestartEnabled(newResolved.SyncRestartEnabled);
        _sstvSession.RequestReconfiguration(newResolved.RxBpfPreset, newResolved.DemodType, newResolved.RxBufferMode);
        _sstvSession.RequestPllTuning(newResolved.PllVcoGain, newResolved.PllLoopOrder, newResolved.PllLoopCutoffHz, newResolved.PllOutputOrder, newResolved.PllOutputCutoffHz);
        _sstvSession.RequestZeroCrossingTuning(newResolved.ZeroCrossingSmoothingMode, newResolved.ZeroCrossingOutputOrder, newResolved.ZeroCrossingOutputCutoffHz, newResolved.ZeroCrossingSmoothingFrequencyHz);
    }

    /// <summary>ui_transition_plan.md step 12 (Auto-save RX audio), Step 4: same "always call the
    /// live-apply method whenever the section is present at all, no snapshot diff" reasoning as
    /// <see cref="PushDecoderChanges"/> above -- <see cref="ISstvSessionService.SetAutoSaveAudioEnabled"/>/
    /// <see cref="ISstvSessionService.SetAudioDirectory"/> are plain field assignments with no
    /// no-op guard of their own, but calling them with the same value twice is harmless, and this
    /// avoids a second class of "diff disagrees with live state" bug this class' own top doc comment
    /// already describes for the audio-device push. Passes the RESOLVED directory (never the raw,
    /// possibly-null/relative persisted value) -- the SAME value <c>SqliteReceiveHistoryStore</c>
    /// would resolve to, so the live decode-path directory can never diverge from what
    /// <c>RxAudioAutoSaver</c> reads back when a pairing completes.</summary>
    private void PushReceiveHistoryChanges(AppSettings preset)
    {
        if (!preset.Sections.ContainsKey(ReceiveHistorySettings.SectionKey))
        {
            return;
        }

        var section = preset.GetSection(ReceiveHistorySettings.SectionKey, ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings);
        _sstvSession.SetAutoSaveAudioEnabled(section?.AutoSaveAudioEnabled ?? false);
        _sstvSession.SetAudioDirectory(ReceiveHistorySettings.ResolveAudioDirectory(section));
    }

    private async Task PushRadioSafetyChangesAsync(AppSettings previous, AppSettings preset, CancellationToken ct)
    {
        if (!preset.Sections.ContainsKey(RadioSafetySettings.SectionKey))
        {
            return;
        }

        var oldSafety = previous.GetSection(RadioSafetySettings.SectionKey, RadioSafetySettingsJsonContext.Default.RadioSafetySettings) ?? new RadioSafetySettings();
        var newSafety = preset.GetSection(RadioSafetySettings.SectionKey, RadioSafetySettingsJsonContext.Default.RadioSafetySettings) ?? new RadioSafetySettings();

        if (oldSafety == newSafety)
        {
            return;
        }

        await _radioSession.SaveSafetySettingsAsync(new(newSafety.SwrCutoffEnabled, newSafety.SwrCutoffThreshold), ct).ConfigureAwait(false);
    }

    /// <summary>Correction 4's per-DECISION Radio-section diff -- the reconnect decision and the
    /// Hamlib-reload decision are independent, even though both fields live in the SAME
    /// <c>RadioConnectionSettings</c> section: a host-only change must never trigger an unconditional
    /// Hamlib reload (a real native-library-load leak per call, <c>RequestHamlibLibraryPathAsync</c>'s
    /// own doc comment), and a Hamlib-path-only change must never tear down a working, unrelated
    /// connection.</summary>
    private async Task PushRadioConnectionChangesAsync(AppSettings previous, AppSettings preset, CancellationToken ct)
    {
        if (!preset.Sections.ContainsKey(RadioConnectionSettings.SectionKey))
        {
            return;
        }

        var oldRadio = previous.GetSection(RadioConnectionSettings.SectionKey, RadioSettingsJsonContext.Default.RadioConnectionSettings) ?? new RadioConnectionSettings();
        var newRadio = preset.GetSection(RadioConnectionSettings.SectionKey, RadioSettingsJsonContext.Default.RadioConnectionSettings) ?? new RadioConnectionSettings();

        if (oldRadio.ToConnectionSpec() != newRadio.ToConnectionSpec())
        {
            await _radioSession.DisconnectAsync().ConfigureAwait(false);
            // Re-reads settings fresh -- already merged onto the live settings.json by this point
            // (steps 4-5 above ran before this method is ever called), so this picks up newRadio's
            // own values with no parameter threading needed.
            await _radioSession.ConnectUsingSettingsAsync(ct).ConfigureAwait(false);
        }

        if (!RadioConnectionSettingsExtensions.HamlibPathsAreEquivalent(oldRadio.HamlibLibraryPath, newRadio.HamlibLibraryPath))
        {
            await _radioSession.RequestHamlibLibraryPathAsync(newRadio.HamlibLibraryPath, ct).ConfigureAwait(false);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Configuration preset switch requested: {Name}")]
        public static partial void SwitchRequested(ILogger logger, string name);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Configuration preset switch: could not carry the live secret forward onto section {SectionKey} (malformed section) -- that field was left as the preset provided it")]
        public static partial void QrzSecretCarryForwardFailed(ILogger logger, string sectionKey, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Configuration preset switch to {Name} rejected -- a transmission is in progress")]
        public static partial void SwitchRejectedTransmitting(ILogger logger, string name);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Configuration preset switch to {Name} rejected -- a recording is in progress")]
        public static partial void SwitchRejectedRecording(ILogger logger, string name);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Configuration preset switch to {Name} rejected -- another switch is already in progress")]
        public static partial void SwitchRejectedConcurrent(ILogger logger, string name);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Configuration preset switch to {Name} failed -- no such preset")]
        public static partial void SwitchPresetNotFound(ILogger logger, string name);

        [LoggerMessage(Level = LogLevel.Information, Message = "Configuration preset switch to {Name} applied (RX audio deferred: {RxAudioDeferred}, partially applied: {PartiallyApplied})")]
        public static partial void SwitchApplied(ILogger logger, string name, bool rxAudioDeferred, bool partiallyApplied);

        [LoggerMessage(Level = LogLevel.Error, Message = "Configuration preset switch to {Name}: audio push failed")]
        public static partial void SwitchAudioPushFailed(ILogger logger, string name, Exception exception);

        [LoggerMessage(Level = LogLevel.Error, Message = "Configuration preset switch to {Name}: decoder push failed")]
        public static partial void SwitchDecoderPushFailed(ILogger logger, string name, Exception exception);

        [LoggerMessage(Level = LogLevel.Error, Message = "Configuration preset switch to {Name}: radio safety push failed")]
        public static partial void SwitchRadioSafetyPushFailed(ILogger logger, string name, Exception exception);

        [LoggerMessage(Level = LogLevel.Error, Message = "Configuration preset switch to {Name}: radio connection push failed")]
        public static partial void SwitchRadioConnectionPushFailed(ILogger logger, string name, Exception exception);

        [LoggerMessage(Level = LogLevel.Error, Message = "Configuration preset switch to {Name}: auto-save-audio push failed")]
        public static partial void SwitchReceiveHistoryPushFailed(ILogger logger, string name, Exception exception);
    }
}
