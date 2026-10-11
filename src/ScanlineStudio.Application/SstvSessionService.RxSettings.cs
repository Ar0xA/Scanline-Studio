using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Cw;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Cw;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application;

public sealed partial class SstvSessionService
{
    /// <summary>See <see cref="ISstvSessionService.RequestReSync"/>.</summary>
    public void RequestReSync()
    {
        Log.ReSyncRequested(_logger);
        _decoder.RequestReSync();
    }

    /// <summary>See <see cref="ISstvSessionService.RequestNotch"/>.</summary>
    public void RequestNotch(bool enabled, double? frequencyHz)
    {
        Log.NotchRequested(_logger, enabled, frequencyHz);
        _decoder.RequestNotch(enabled, frequencyHz);
    }

    /// <summary>See <see cref="ISstvSessionService.RequestPllTuning"/>.</summary>
    public void RequestPllTuning(double vcoGain, int loopOrder, double loopCutoffHz, int outputOrder, double outputCutoffHz)
    {
        Log.PllTuningRequested(_logger, vcoGain, loopOrder, loopCutoffHz, outputOrder, outputCutoffHz);
        _decoder.RequestPllTuning(vcoGain, loopOrder, loopCutoffHz, outputOrder, outputCutoffHz);
    }

    /// <summary>See <see cref="ISstvSessionService.RequestZeroCrossingTuning"/>.</summary>
    public void RequestZeroCrossingTuning(ZeroCrossingSmoothingMode smoothingMode, int outputOrder, double outputCutoffHz, double smoothingFrequencyHz)
    {
        Log.ZeroCrossingTuningRequested(_logger, smoothingMode, outputOrder, outputCutoffHz, smoothingFrequencyHz);
        _decoder.RequestZeroCrossingTuning(smoothingMode, outputOrder, outputCutoffHz, smoothingFrequencyHz);
    }

    /// <summary>See <see cref="ISstvSessionService.ArmScopeCapture"/>.</summary>
    public void ArmScopeCapture(int size)
    {
        Log.ScopeCaptureArmed(_logger, size);
        _decoder.ArmScopeCapture(size);
    }

    /// <summary>See <see cref="ISstvSessionService.RequestSenseLevel"/>.</summary>
    public void RequestSenseLevel(int level)
    {
        Log.SenseLevelRequested(_logger, level);
        _decoder.SenseLevel = level;
    }

    /// <summary>See <see cref="ISstvSessionService.RequestAutoSyncEnabled"/>.</summary>
    public void RequestAutoSyncEnabled(bool enabled)
    {
        Log.AutoSyncEnabledRequested(_logger, enabled);
        _decoder.AutoSyncEnabled = enabled;
    }

    /// <summary>See <see cref="ISstvSessionService.RequestAutoStopEnabled"/>.</summary>
    public void RequestAutoStopEnabled(bool enabled)
    {
        Log.AutoStopEnabledRequested(_logger, enabled);
        _decoder.AutoStopEnabled = enabled;
    }

    /// <summary>See <see cref="ISstvSessionService.RequestSnrMeasurementEnabled"/>.</summary>
    public void RequestSnrMeasurementEnabled(bool enabled)
    {
        Log.SnrMeasurementEnabledRequested(_logger, enabled);
        _decoder.SnrMeasurementEnabled = enabled;
    }

    /// <summary>See <see cref="ISstvSessionService.RequestAutoSlantEnabled"/>.</summary>
    public void RequestAutoSlantEnabled(bool enabled)
    {
        Log.AutoSlantEnabledRequested(_logger, enabled);
        _decoder.AutoSlantEnabled = enabled;
    }

    /// <summary>See <see cref="ISstvSessionService.RequestAfcEnabled"/>.</summary>
    public void RequestAfcEnabled(bool enabled)
    {
        Log.AfcEnabledRequested(_logger, enabled);
        _decoder.AfcEnabled = enabled;
    }

    /// <summary>See <see cref="ISstvSessionService.RequestSyncRestartEnabled"/>.</summary>
    public void RequestSyncRestartEnabled(bool enabled)
    {
        Log.SyncRestartEnabledRequested(_logger, enabled);
        _decoder.SyncRestartEnabled = enabled;
    }

    /// <summary>See <see cref="ISstvSessionService.RequestReconfiguration"/>.</summary>
    public void RequestReconfiguration(RxBpfPreset rxBpfPreset, DemodType demodType, RxBufferMode rxBufferMode)
    {
        Log.ReconfigurationRequested(_logger, rxBpfPreset, demodType, rxBufferMode);
        if (_decoder is ISstvDecoderReconfiguration reconfiguration)
        {
            reconfiguration.RequestReconfiguration(rxBpfPreset, demodType, rxBufferMode);
        }
    }

    /// <summary>See <see cref="ISstvSessionService.RequestRxBpfPreset"/>.</summary>
    public void RequestRxBpfPreset(RxBpfPreset rxBpfPreset)
    {
        Log.RxBpfPresetRequested(_logger, rxBpfPreset);
        if (_decoder is ISstvDecoderReconfiguration reconfiguration)
        {
            reconfiguration.RequestRxBpfPreset(rxBpfPreset);
        }
    }

    /// <summary>See <see cref="ISstvSessionService.RequestSampleRateAsync"/>.</summary>
    public async Task<SampleRateApplyResult> RequestSampleRateAsync(int sampleRate, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Log.SampleRateChangeRequested(_logger, sampleRate);

        // Step 0 (round-2 finding B3): a no-op guard BEFORE touching anything -- without this, the
        // unconditional-every-Save call shape every other Request* method on this interface uses
        // would drop and reopen the capture session, aborting any in-progress reception, on every
        // Options Save that didn't even touch this setting.
        if (sampleRate == _decoder.SampleRate)
        {
            return SampleRateApplyResult.NoChange;
        }

        if (!await _rxTransitionGate.WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false))
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "RequestSampleRateAsync (rxTransitionGate wait timed out)", new TimeoutException()));
            throw new TimeoutException("Timed out waiting to apply a sample rate change -- a concurrent RX transition did not finish in time.");
        }

        try
        {
            // Re-checked now that the gate is actually held (round-4 finding R2, same discipline
            // DecodeFromFileAsync's own re-check above uses) -- a racing caller may have already
            // committed this exact rate while this call was waiting for the gate.
            if (sampleRate == _decoder.SampleRate)
            {
                return SampleRateApplyResult.NoChange;
            }

            lock (_recordingLock)
            {
                if (_recordingChunks is not null)
                {
                    Log.SampleRateChangeDeferred(_logger, sampleRate);
                    return SampleRateApplyResult.DeferredRecordingInProgress;
                }

                _sampleRateChangeInProgress = true;
            }

            try
            {
                return await ApplySampleRateLockedAsync(sampleRate).ConfigureAwait(false);
            }
            finally
            {
                lock (_recordingLock)
                {
                    _sampleRateChangeInProgress = false;
                }
            }
        }
        finally
        {
            _rxTransitionGate.Release();
        }
    }

    /// <summary>The actual commit sequence for <see cref="RequestSampleRateAsync"/> -- called only
    /// while <see cref="_rxTransitionGate"/> is held and the recording-in-progress check has already
    /// passed. Encoder (TX) applies independently of everything below -- RX and TX are separate
    /// streams (see <c>ISstvEncoderReconfiguration</c>'s own doc comment); called first so it always
    /// runs regardless of what happens to the decoder/capture side.</summary>
    private async Task<SampleRateApplyResult> ApplySampleRateLockedAsync(int sampleRate)
    {
        if (_encoder is ISstvEncoderReconfiguration encoderReconfig)
        {
            encoderReconfig.RequestSampleRate(sampleRate);
        }

        if (_decoder is not ISstvDecoderReconfiguration decoderReconfig)
        {
            // No live-apply side-channel on this decoder implementation (e.g. a fake used by a test
            // project) -- matches every other is-test-gated Request* method's own silent-skip
            // convention on this class.
            return SampleRateApplyResult.NoChange;
        }

        // Round-3 plan-review: stop-then-commit-then-restart, not commit-then-restart -- capture
        // being fully stopped BEFORE the decoder commits is what makes committing directly (no
        // idle-gating) safe at all. See ISstvDecoderReconfiguration.ApplyPendingReconfigurationNow's
        // own doc comment for the decoder-side half of this contract.
        var wasReceiving = _isReceiving;
        if (wasReceiving)
        {
            await StopReceivingLockedAsync().ConfigureAwait(false);
        }

        decoderReconfig.RequestSampleRate(sampleRate);
        var result = decoderReconfig.ApplyPendingReconfigurationNow();

        switch (result)
        {
            case SwapResult.Committed:
                if (Waterfall is IWaterfallSourceReconfiguration waterfallReconfig)
                {
                    waterfallReconfig.RequestSampleRate(sampleRate);
                }

                if (wasReceiving)
                {
                    await StartReceivingLockedAsync(CancellationToken.None).ConfigureAwait(false);
                }

                Log.SampleRateChangeApplied(_logger, sampleRate);
                return SampleRateApplyResult.Applied;

            case SwapResult.Rejected:
            case SwapResult.NothingPending:
                // Old inner decoder (or, for NothingPending, nothing at all) untouched, still at the
                // previous rate -- restore capture at that unchanged rate, don't touch the waterfall.
                if (wasReceiving)
                {
                    await StartReceivingLockedAsync(CancellationToken.None).ConfigureAwait(false);
                }

                Log.SampleRateChangeRejected(_logger, sampleRate);
                return SampleRateApplyResult.Rejected;

            case SwapResult.Busy:
                return await HandleSampleRateBusyAsync(decoderReconfig, wasReceiving).ConfigureAwait(false);

            default:
                throw new InvalidOperationException($"Unexpected {nameof(SwapResult)} value: {result}.");
        }
    }

    /// <summary>Round-3/round-4 plan-review finding C2: <see cref="SwapResult.Busy"/> is genuinely
    /// reachable via <see cref="StopReceivingLockedAsync"/>'s own watchdog-timeout/abandoned-stop
    /// path (a straggler <c>PushSamples</c> call can still be genuinely in flight even after that
    /// method returns) -- NOT retried (a 5s <see cref="IAudioEngine.StopCaptureAsync"/> timeout means
    /// the drain thread is genuinely wedged, so retrying inside any sane budget would not succeed
    /// either).</summary>
    private async Task<SampleRateApplyResult> HandleSampleRateBusyAsync(ISstvDecoderReconfiguration decoderReconfig, bool wasReceiving)
    {
        // Clears the now-stuck pending rate via the decoder's own equality guard (requesting the
        // CURRENT committed value) -- otherwise it would sit armed and get silently applied by a
        // future unrelated maintenance swap (exactly what round-4 finding C1 exists to prevent).
        decoderReconfig.RequestSampleRate(_decoder.SampleRate);

        Exception? restartFailure = null;
        if (wasReceiving)
        {
            try
            {
                await StartReceivingLockedAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                restartFailure = ex;
            }
        }

        Log.SampleRateChangeBusy(_logger, restartFailure);
        var message = restartFailure is null
            ? "Sample rate change failed: the decoder was busy after an abandoned capture stop; capture was restarted at the previous rate."
            : "Sample rate change failed: the decoder was busy after an abandoned capture stop, and restarting capture also failed.";
        throw new InvalidOperationException(message, restartFailure);
    }

    /// <summary>See <see cref="ISstvSessionService.RequestCaptureDeviceAsync"/>. Same gate/no-op/
    /// defer-on-recording shape as <see cref="RequestSampleRateAsync"/> above -- see that method's
    /// own doc comment for the shared reasoning behind each step.</summary>
    public async Task<CaptureDeviceApplyResult> RequestCaptureDeviceAsync(string? deviceId, string? deviceName, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Log.CaptureDeviceChangeRequested(_logger, deviceId);

        // Step 0: a no-op guard BEFORE touching anything -- see _activeCaptureDeviceId's own doc
        // comment for why this compares against THAT field, not persisted settings.
        if (deviceId == _activeCaptureDeviceId)
        {
            return CaptureDeviceApplyResult.NoChange;
        }

        if (!await _rxTransitionGate.WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false))
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "RequestCaptureDeviceAsync (rxTransitionGate wait timed out)", new TimeoutException()));
            throw new TimeoutException("Timed out waiting to apply a capture device change -- a concurrent RX transition did not finish in time.");
        }

        try
        {
            // Re-checked now that the gate is actually held -- a racing caller may have already
            // committed this exact device while this call was waiting for the gate.
            if (deviceId == _activeCaptureDeviceId)
            {
                return CaptureDeviceApplyResult.NoChange;
            }

            lock (_recordingLock)
            {
                if (_recordingChunks is not null)
                {
                    Log.CaptureDeviceChangeDeferred(_logger, deviceId);
                    return CaptureDeviceApplyResult.DeferredRecordingInProgress;
                }

                _captureDeviceChangeInProgress = true;
            }

            try
            {
                return await ApplyCaptureDeviceLockedAsync(deviceId, deviceName).ConfigureAwait(false);
            }
            finally
            {
                lock (_recordingLock)
                {
                    _captureDeviceChangeInProgress = false;
                }
            }
        }
        finally
        {
            _rxTransitionGate.Release();
        }
    }

    /// <summary>The actual commit sequence for <see cref="RequestCaptureDeviceAsync"/> -- called only
    /// while <see cref="_rxTransitionGate"/> is held and the recording-in-progress check has already
    /// passed. Same stop-then-commit-then-restart shape as <see cref="ApplySampleRateLockedAsync"/> --
    /// capture being fully stopped BEFORE the settings write is what makes
    /// <see cref="StartReceivingLockedAsync"/>'s own fresh device resolution safe to rely on
    /// afterward.</summary>
    private async Task<CaptureDeviceApplyResult> ApplyCaptureDeviceLockedAsync(string? deviceId, string? deviceName)
    {
        var previousSettings = await LoadAudioSettingsAsync(CancellationToken.None).ConfigureAwait(false);
        var previousDeviceId = previousSettings.CaptureDeviceId;
        var previousDeviceName = previousSettings.CaptureDeviceName;

        var wasReceiving = _isReceiving;
        if (wasReceiving)
        {
            await StopReceivingLockedAsync().ConfigureAwait(false);
        }

        try
        {
            await PersistCaptureDeviceAsync(deviceId, deviceName, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Code-review round-1 finding: persisting the NEW device itself failing (e.g. disk
            // full/permission denied) used to leave RX silently dead with no restart attempt --
            // capture is already stopped at this point. Best-effort restore RX at the OLD device
            // before this exception propagates, so a persist failure doesn't ALSO strand RX. The
            // persist failure itself is a genuinely different problem than "device unresolvable"
            // below -- it must still propagate (not get silently turned into a Rejected result), a
            // restart failure here must not mask it (swallowed, not rethrown).
            if (wasReceiving)
            {
                try
                {
                    await StartReceivingLockedAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Swallowed deliberately -- the persist failure about to propagate is the one
                    // that matters; a restart failure here would just mask it.
                }
            }

            throw;
        }

        _activeCaptureDeviceId = deviceId;

        if (!wasReceiving)
        {
            Log.CaptureDeviceChangeApplied(_logger, _activeCaptureDeviceId);
            return CaptureDeviceApplyResult.Applied;
        }

        try
        {
            // StartReceivingLockedAsync resolves the device FRESH from the settings write just
            // above (ResolveDeviceAsync -> TryResolveDeviceAsync, SstvSessionService.cs -- no
            // parameter threading needed here) -- if resolution falls back to a DIFFERENT device
            // than requested (e.g. the requested one vanished between persist and restart), that
            // fallback is what actually ends up latched into _activeCaptureDeviceId by
            // StartReceivingLockedAsync itself (see that field's own doc comment), which is correct:
            // this method's own Applied result reflects reality, not blindly the input -- the log
            // line below reads _activeCaptureDeviceId for the same reason (code-review round-1
            // finding: this used to log the REQUESTED deviceId, which could be wrong on a fallback).
            await StartReceivingLockedAsync(CancellationToken.None).ConfigureAwait(false);
            Log.CaptureDeviceChangeApplied(_logger, _activeCaptureDeviceId);
            return CaptureDeviceApplyResult.Applied;
        }
        catch (Exception ex) when (ex is AudioDeviceUnavailableException || (ex is InvalidOperationException and not ObjectDisposedException))
        {
            // Code-review round-1 finding: the original filter was `catch (InvalidOperationException)`
            // alone, which (a) MISSED the realistic failure -- MiniAudioEngine wraps every native
            // capture-open failure (device busy, unsupported rate, unplugged mid-swap) in
            // AudioDeviceUnavailableException, not InvalidOperationException, so that case escaped
            // uncaught, leaving RX dead with an unopenable device already persisted across restarts
            // -- while (b) being simultaneously TOO BROAD, since ObjectDisposedException derives from
            // InvalidOperationException and would have triggered a spurious rollback write during a
            // disposal race (StopReceivingLockedAsync's own watchdog can force _isReceiving=false
            // while a genuinely disposed engine still throws from underneath). Corrected filter
            // catches the real "device didn't come up" cases and explicitly excludes disposal.
            //
            // Roll back to whatever was active before this call and attempt ONE restart at that
            // value. If even THAT throws, this method lets it propagate uncaught (matches
            // HandleSampleRateBusyAsync's own "genuinely unrecoverable -- throw, don't return an enum
            // value pretending it's a normal outcome" precedent).
            await PersistCaptureDeviceAsync(previousDeviceId, previousDeviceName, CancellationToken.None).ConfigureAwait(false);
            _activeCaptureDeviceId = previousDeviceId;

            await StartReceivingLockedAsync(CancellationToken.None).ConfigureAwait(false);
            // Code-review round-2 finding: the caught exception used to be discarded entirely, and
            // the log message used to unconditionally claim "no capture device could be resolved at
            // all" -- wrong for the REALISTIC case this filter also catches (a device that enumerates
            // but fails to actually open, e.g. AudioDeviceUnavailableException). Logging `ex` here
            // captures the real native reason regardless of which of the two catches fired.
            Log.CaptureDeviceChangeRejected(_logger, deviceId, ex);
            return CaptureDeviceApplyResult.Rejected;
        }
    }

    /// <summary>Writes the caller-REQUESTED device id/name directly -- unlike the existing
    /// <c>PersistResolvedDeviceAsync</c> (which persists whatever a resolution actually landed on),
    /// this persists exactly what the caller asked for, since <see cref="RequestCaptureDeviceAsync"/>
    /// is the one entry point that DECIDES the device, rather than resolving one from existing
    /// settings. Same read-modify-write shape as <see cref="PersistSenseLevelAsync"/> below.</summary>
    private async Task PersistCaptureDeviceAsync(string? deviceId, string? deviceName, CancellationToken ct)
    {
        await _settingsStore.UpdateAsync(appSettings =>
        {
            var previous = appSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings) ?? new AudioDeviceSettings();
            var updated = previous with { CaptureDeviceId = deviceId, CaptureDeviceName = deviceName };
            return appSettings.WithSection(AudioDeviceSettings.SectionKey, updated, AudioSettingsJsonContext.Default.AudioDeviceSettings);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>See <see cref="ISstvSessionService.PersistSenseLevelAsync"/>. Read-modify-write
    /// against whatever is currently persisted for this section, not a fresh
    /// <c>new SstvDecoderSettings { ... }</c> -- matches this codebase's own established convention
    /// for a targeted single-field settings write (e.g. <c>RxImagePaneViewModel.PersistQuickModeGridAsync</c>).</summary>
    public async Task PersistSenseLevelAsync(int level, CancellationToken ct = default)
    {
        await _settingsStore.UpdateAsync(appSettings =>
        {
            var current = appSettings.GetSection(SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings) ?? new SstvDecoderSettings();
            var updated = current with { SenseLevel = level };
            return appSettings.WithSection(SstvDecoderSettings.SectionKey, updated, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>See <see cref="ISstvSessionService.PersistAfcEnabledAsync"/>. Same targeted
    /// read-modify-write shape as <see cref="PersistSenseLevelAsync"/> above.</summary>
    public async Task PersistAfcEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        await _settingsStore.UpdateAsync(appSettings =>
        {
            var current = appSettings.GetSection(SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings) ?? new SstvDecoderSettings();
            var updated = current with { AfcEnabled = enabled };
            return appSettings.WithSection(SstvDecoderSettings.SectionKey, updated, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>See <see cref="ISstvSessionService.PersistRxBpfPresetAsync"/>. Same targeted
    /// read-modify-write shape as <see cref="PersistSenseLevelAsync"/> above.</summary>
    public async Task PersistRxBpfPresetAsync(RxBpfPreset preset, CancellationToken ct = default)
    {
        await _settingsStore.UpdateAsync(appSettings =>
        {
            var current = appSettings.GetSection(SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings) ?? new SstvDecoderSettings();
            var updated = current with { RxBpfPreset = preset };
            return appSettings.WithSection(SstvDecoderSettings.SectionKey, updated, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>See <see cref="ISstvSessionService.TryGetScopeCaptureChannel0"/>. A plain read, no
    /// logging -- matches this class's own established convention of only logging COMMANDS
    /// (RequestNotch/ArmScopeCapture above), not polled getters.</summary>
    public double[]? TryGetScopeCaptureChannel0() => _decoder.TryGetScopeCaptureChannel0();

    /// <summary>See <see cref="ISstvSessionService.TryGetScopeCaptureChannel1"/>.</summary>
    public double[]? TryGetScopeCaptureChannel1() => _decoder.TryGetScopeCaptureChannel1();

    /// <summary>See <see cref="ISstvSessionService.RequestCorrectSlant"/>.</summary>
    public void RequestCorrectSlant()
    {
        Log.CorrectSlantRequested(_logger);
        _decoder.RequestCorrectSlant();
    }

    /// <summary>See <see cref="ISstvSessionService.AbortReception"/>.</summary>
    public void AbortReception()
    {
        Log.ReceptionAborted(_logger);
        _decoder.RequestAbandonReception();
    }

    /// <summary>See <see cref="ISstvSessionService.ForceMode"/>.</summary>
    public void ForceMode(SstvModeDefinition mode)
    {
        Log.ModeForced(_logger, mode.Id);
        _decoder.ForceMode(mode);
    }

    /// <summary>See <see cref="ISstvSessionService.SetModeLock"/>.</summary>
    public void SetModeLock(SstvModeDefinition? mode)
    {
        Log.ModeLockChanged(_logger, mode?.Id ?? "(unlocked)");
        _decoder.SetModeLock(mode);
    }
}
