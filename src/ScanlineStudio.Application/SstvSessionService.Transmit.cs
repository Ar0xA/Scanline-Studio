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
    public async Task TransmitAsync(SstvModeDefinition mode, IImageSource image, CancellationToken ct = default)
    {
        Log.TxStarting(_logger, mode.Id, image.Width, image.Height);

        // Freeze the encoder before rate-dependent footer preparation, including asynchronous
        // file reads. Keep the lease until playback drains, and release it on every failure.
        var encoderReconfig = _encoder as ISstvEncoderReconfiguration;
        encoderReconfig?.BeginTransmission();
        try
        {
            var (stationId, sampleRateOffsetHz, txBpfEnabled, txBpfTapCount, txLpfEnabled, txLpfFrequencyHz) = await ResolveTransmitSettingsAsync(resolveSoundFile: true, ct).ConfigureAwait(false);
            // Reuse the exact resolved options for estimation and encoding. Estimation traverses
            // the scanlines, so keep that work off the caller's UI thread.
            var totalSamplesEstimate = await Task.Run(() => _encoder.EstimateSampleCount(mode, image, stationId, sampleRateOffsetHz), ct).ConfigureAwait(false);
            await PlayWithPttAsync(_encoder.EncodeBatchedAsync(mode, image, stationId, sampleRateOffsetHz, txBpfEnabled, txBpfTapCount, txLpfEnabled, txLpfFrequencyHz, ct), _encoder.SampleRate, ct, totalSamplesEstimate: totalSamplesEstimate).ConfigureAwait(false);
        }
        finally
        {
            encoderReconfig?.EndTransmission();
        }
    }

    public Task TuneAsync(double frequencyHz, TimeSpan duration, CancellationToken ct = default)
    {
        const int sampleRate = 48_000;

        // Round-18 finding 3: neither parameter was validated at all -- these are direct caller
        // arguments with a live caller that already catches and surfaces a failure
        // (RadioStatusViewModel's own RadioStatus.Error.TuneFailed), so this throws rather than
        // silently clamping -- the caller needs to know its own value was wrong, not have it
        // silently substituted. Checked and thrown BEFORE Log.TuneStarting/PlayWithPttAsync, so PTT is
        // never touched and _transmitInFlight is never taken on an invalid call.
        //
        // frequencyHz: NaN/infinity propagates through GenerateTone's Math.Sin into PumpToPlaybackAsync's
        // unclamped `sample * gain` as NaN samples enqueued to the playback device WITH PTT KEYED;
        // anything at or above Nyquist (half of sampleRate) aliases to arbitrary audible garbage.
        if (!double.IsFinite(frequencyHz) || frequencyHz <= 0 || frequencyHz >= sampleRate / 2.0)
        {
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), frequencyHz, $"Must be finite and in (0, {sampleRate / 2.0}) Hz.");
        }

        // duration: GenerateTone's totalSamples = (long)(duration.TotalSeconds * sampleRate) accepts
        // any TimeSpan a caller can construct -- an absurd value (e.g. a unit-conversion bug upstream)
        // keys PTT for a correspondingly absurd duration, and the one production caller
        // (RadioStatusViewModel.TuneAsync) passes CancellationToken.None with no Stop command, so
        // there is no way to interrupt it short of process exit. Zero/negative is otherwise benign
        // (the pump loop just never runs) but still rejected -- a caller passing that almost certainly
        // has a bug worth surfacing, not a deliberate "key and immediately un-key" request.
        if (duration <= TimeSpan.Zero || duration > MaxTuneDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, $"Must be greater than zero and at most {MaxTuneDuration}.");
        }

        Log.TuneStarting(_logger, frequencyHz, duration);
        return PlayWithPttAsync(GenerateTone(frequencyHz, duration, sampleRate, ct), sampleRate, ct);
    }

    public async Task<int> GetTxVolumePercentAsync(CancellationToken ct = default)
    {
        var settings = await LoadAudioSettingsAsync(ct).ConfigureAwait(false);

        // Round-16 finding (this project's own history): unlike GetStationIdTransmitOptionsAsync's
        // own WPM/tone-frequency boundary validation (same threat model: a corrupted/hand-edited
        // settings.json), this value flows straight into PlayWithPttAsync's `gain` multiplier with
        // no range check at all -- an out-of-range value (e.g. a typo'd 10000, or a negative number)
        // would reach PumpToPlaybackAsync's unclamped `sample * gain` directly, hard-clipping the
        // transmitted audio into a square wave (real-world splatter risk on an actual transmitter) or
        // inverting phase. Clamped here so every reader gets a safe value regardless of what's on disk.
        return Math.Clamp(settings.TxVolumePercent ?? 100, 0, 100);
    }

    public async Task SetTxVolumePercentAsync(int percent, CancellationToken ct = default)
    {
        // Clamped on write too, not just on read -- GetTxVolumePercentAsync's own clamp already
        // makes an out-of-range value on disk safe to READ, but leaving it unclamped here would
        // still let a bogus value silently reach disk via this API's own normal use (e.g. a UI
        // control with a bug, or a scripted settings import), for GetTxVolumePercentAsync to mask
        // again on every future read -- clamping at the write boundary keeps what's actually stored
        // consistent with what every reader promises.
        percent = Math.Clamp(percent, 0, 100);

        long version = 0;
        await _settingsStore.UpdateAsync(appSettings =>
        {
            // Not pure, deliberately: mutate runs once under the settings file lock, so this version
            // follows commit order. A failed save wastes a number but raises no event.
            version = Interlocked.Increment(ref _txVolumeVersion);
            var current = appSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings)
                ?? new AudioDeviceSettings();
            return appSettings.WithSection(
                AudioDeviceSettings.SectionKey,
                current with { TxVolumePercent = percent },
                AudioSettingsJsonContext.Default.AudioDeviceSettings);
        }, ct).ConfigureAwait(false);

        // Write-through to the live field _before_ Log/return -- so a caller awaiting this method's
        // completion (e.g. the Options window's debounced Pwr-slider persist) is guaranteed
        // PumpToPlaybackAsync's NEXT chunk read already sees the new value, not just "eventually".
        // See _liveTxGain's own doc comment for why a live in-flight transmission needs this at all.
        ApplyLiveTxGain(percent, version);

        Log.TxVolumeSet(_logger, percent);
        RaiseTxVolumePercentChanged(new TxVolumeChange(percent, version));
    }

    public async Task ResyncTxVolumeFromSettingsAsync(CancellationToken ct = default)
    {
        var percent = 100;
        long version = 0;
        // Read and version under the same lock as SetTxVolumePercentAsync; returning the input
        // unchanged makes UpdateAsync skip the write.
        await _settingsStore.UpdateAsync(appSettings =>
        {
            version = Interlocked.Increment(ref _txVolumeVersion);
            var current = appSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings);
            percent = Math.Clamp(current?.TxVolumePercent ?? 100, 0, 100);
            return appSettings;
        }, ct).ConfigureAwait(false);

        ApplyLiveTxGain(percent, version);
        Log.TxVolumeResynced(_logger, percent);
        RaiseTxVolumePercentChanged(new TxVolumeChange(percent, version));
    }

    /// <summary>Reads Pwr and samples the version under the settings lock: a write committed after this read
    /// has a higher version, so <see cref="ApplyLiveTxGain"/> lets it win in either landing order. Relies on the
    /// store skipping the write for a same-reference mutate, as JsonSettingsStore does.</summary>
    private async Task SeedLiveTxGainAsync(CancellationToken ct)
    {
        var percent = 100;
        long seenVersion = 0;
        await _settingsStore.UpdateAsync(appSettings =>
        {
            seenVersion = Interlocked.Read(ref _txVolumeVersion);
            var current = appSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings);
            percent = Math.Clamp(current?.TxVolumePercent ?? 100, 0, 100);
            return appSettings;
        }, ct).ConfigureAwait(false);

        ApplyLiveTxGain(percent, seenVersion);
    }

    /// <summary>Writes the live gain unless a newer version already did: writers resume after the
    /// settings lock in no fixed order.</summary>
    private void ApplyLiveTxGain(int percent, long version)
    {
        lock (_liveTxGainGate)
        {
            if (version < _liveTxGainVersion)
            {
                return;
            }

            _liveTxGainVersion = version;
            _liveTxGain = percent / 100f;
        }
    }

    private void RaiseTxVolumePercentChanged(TxVolumeChange change)
    {
        if (TxVolumePercentChanged is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<TxVolumeChange>)handler)(change);
            }
            catch (Exception ex)
            {
                Log.TxVolumeChangedSubscriberFailed(_logger, ex);
            }
        }
    }

    public async Task<bool> GetTxDeviceMutedAsync(CancellationToken ct = default)
    {
        var device = await TryResolveDeviceAsync(forCapture: false, ct).ConfigureAwait(false);
        if (device is null)
        {
            return false;
        }

        return await _deviceMuteQuery.IsDeviceMutedAsync(device, isCapture: false, ct).ConfigureAwait(false) ?? false;
    }

    /// <summary>Shared PTT-guarantee shape for both <see cref="TransmitAsync"/> and <see cref="TuneAsync"/>:
    /// pauses capture (resumed afterward only if RX was already running), keys PTT, plays
    /// <paramref name="samples"/>, then un-keys PTT in a <c>finally</c> no matter how playback ends.
    /// A cancellation or fault (manual Stop TX, SWR auto-cutoff -- see <c>TxControlsPaneViewModel</c>)
    /// un-keys BEFORE stopping playback; a normal completion un-keys after the drain. The un-key
    /// COMMAND is unconditional. The key/unkey epoch guards around it only matter against
    /// <c>DisposeAsync</c>'s backstop un-key, the one PTT command that can overlap this call (after its
    /// bounded wait for this call expires): they keep that backstop from clearing a newer key's
    /// "may still be keyed" belief, and let this call skip a redundant cleanup un-key once the backstop
    /// has confirmed the rig off after this call's own key (see <c>PttSafetyCoordinator</c>'s own doc
    /// comment).
    ///
    /// Device resolution and the entry PTT-key live INSIDE the guarded region, so a
    /// device-resolution failure (e.g. no playback device configured) after RX has already been
    /// paused still resumes RX.</summary>
    // T1-3 (production_audit.md): samples is IAsyncEnumerable<ReadOnlyMemory<float>> (batched), not
    // IAsyncEnumerable<float> -- verified this parameter has exactly ONE use in this method's whole
    // 844-line body (the pass-through to PumpToPlaybackAsync below), so this is a pure type-plumbing
    // change, not a logic change to anything PTT-safety-related in this method. Both real producers
    // (AnalogFmSstvEncoder.EncodeBatchedAsync via TransmitAsync, GenerateTone via TuneAsync) batch
    // now specifically so PumpToPlaybackAsync can consume batches directly -- see that method's own
    // comment for why flattening back to individual floats before this point would erase the entire
    // point of batching.
    private async Task PlayWithPttAsync(IAsyncEnumerable<ReadOnlyMemory<float>> samples, int sampleRate, CancellationToken ct, long? totalSamplesEstimate = null)
    {
        // Round-12 finding: single-flight guard, checked before ANYTHING else -- no RX pause, no
        // device resolution, no PTT touched. See _transmitInFlight's own doc comment for the failure
        // this closes: a second overlapping PlayWithPttAsync call used to silently re-key and tear
        // down the FIRST call's own live playback session instead of being rejected outright.
        if (Interlocked.CompareExchange(ref _transmitInFlight, 1, 0) != 0)
        {
            throw new InvalidOperationException("A transmit or tune is already in progress.");
        }

        // Piece C2 cross-check: DecodeFromFileAsync's own entry checks _transmitInFlight the same
        // way, in the other direction -- without this, a file decode reads _isReceiving == false
        // (RX is genuinely paused for it) and this call would see wasReceiving == false, skip its
        // own RX-pause step below, and key PTT mid-re-decode. Released by CompareExchange back to 0
        // in this method's own finally below, same as _transmitInFlight itself.
        if (Volatile.Read(ref _fileDecodeInFlight) != 0)
        {
            Volatile.Write(ref _transmitInFlight, 0);
            throw new InvalidOperationException("Cannot transmit or tune while a file decode is in progress.");
        }

        // Code-review round-1 finding: the self-test's own entry checks _transmitInFlight (rejects a
        // self-test started mid-transmit), but nothing enforced the OTHER direction -- a self-test's
        // encode+decode is real CPU work competing with this live PTT-keyed playback pump, and its
        // own result dialog is a MODAL ShowDialog that would otherwise pop up mid-transmission and
        // block the user from reaching Stop TX. Same shape as the _fileDecodeInFlight check above.
        if (Volatile.Read(ref _loopbackSelfTestInFlight) != 0)
        {
            Volatile.Write(ref _transmitInFlight, 0);
            throw new InvalidOperationException("Cannot transmit or tune while a loopback self-test is in progress.");
        }

        try
        {
            var wasReceiving = _isReceiving;
            if (wasReceiving)
            {
                // Round-15 finding 3 flagged this call as having the identical "hang here strands
                // _transmitInFlight forever" exposure as the three awaits inside the guarded
                // try/finally below, but judged a naive WaitAsync bound here UNSAFE (this call sits
                // OUTSIDE the guarded region, so no finally would run its own cleanup) and deferred it,
                // pending a "paired change" (bound the wait AND force _isReceiving = false on the
                // timeout path). Round-16 finding: that paired fix exists and is now applied INSIDE
                // StopReceivingAsync itself (see its own comment) -- the handlers are already detached
                // before its own bounded stop attempt, so "not receiving" is truthfully the state on
                // a timeout regardless of whether the underlying native close ever finishes, closing
                // the exact gap round 15 correctly identified but didn't yet have a design for.
                await StopReceivingAsync().ConfigureAwait(false);
                // User-reported gap (2026-08-18): see CapturePausedForTransmitChanged's own doc comment.
                // Raised AFTER the await completes -- capture is genuinely stopped by the time a
                // subscriber sees this, not merely "about to stop." Deliberately OUTSIDE the guarded
                // try/finally just below (this line runs before it starts) -- RaiseCapturePausedForTransmitChanged's
                // own try/catch is what keeps a throwing subscriber here from propagating out of this
                // method with capture already stopped and no cleanup ever run (auditor round-1 finding).
                RaiseCapturePausedForTransmitChanged(true);
            }

            var abnormalTermination = false;

            // Blocker 2 (Tier A Batch 3 chunk 3a): captured ONCE, at key time -- NEVER re-read at
            // catch/cleanup time. RadioController.DisconnectAsync/DisposeAsync both reset _rigId to
            // "none" WITHOUT ever un-keying PTT themselves, so a rig this call genuinely keyed can read
            // "none" by the moment the cleanup un-key fails. The old catch-time re-read then classified a
            // physically keyed transmitter as the benign "no radio, nothing to unkey" case and swallowed
            // it.
            var pttKeyedOnRealRig = false;

            // Declared out here, not alongside its own snapshot assignment inside the try below -- the
            // cleanup finally needs to read this. The 0 placeholder (and keyEpochAfterOwnKeyAttempt's,
            // below) survives into cleanup when a throw lands before the snapshot, and is still safe:
            // the only consumer requires pttKeyedOnRealRig true, and pttKeyedOnRealRig true implies some
            // key attempt bumped _pttKeyEpoch -- either this call's own (success or failure both bump
            // it) or the earlier call whose failed unkey latched the entry baseline. So _pttKeyEpoch is
            // >= 1, ConfirmedUnkeyAlreadyHappenedSinceOwnKey(0, 0) reads false, and the retry runs.
            var unkeyEpochAfterOwnKeyAttempt = 0;

            // Same reason/shape as unkeyEpochAfterOwnKeyAttempt just above -- assigned inside the try
            // below, once this call's own key phase (not just method entry) has completed.
            var keyEpochAfterOwnKeyAttempt = 0;

            // Blocker 3: this call's own handle into the coordinator's keyed-slot registration.
            var attempt = new PttSafetyCoordinator.PttKeyAttempt();

            try
            {
                // Baselined HERE, before the three bounded device/settings awaits below, so a failure in
                // any of them still reaches cleanup with pttKeyedOnRealRig true when an EARLIER call's
                // unkey failed on a real rig -- that rig may still be keyed independent of this call's
                // own RigId observation (RadioController.DisconnectAsync resets RigId to "none" without
                // un-keying), so cleanup must log Critical and retry, not a Debug "no radio" line. The
                // rigIsRealAtKeyTime branch further down still sets this true when THIS call keys the
                // rig itself, a strict superset of this baseline.
                //
                // Accepted trade: once one real un-key failure has latched _pttUnkeyFailedOnRealRig and
                // the operator disconnects (every un-key against the null-object backend throws, so the
                // flag can never be cleared by a confirmed unkey), every SUBSEQUENT transmit re-attempts
                // an un-key and re-emits a fresh Critical "PTT MAY STILL BE KEYED". The underlying belief
                // is genuinely unresolved, so a persistent, repetitive Critical is the conservative
                // failure mode for a possibly-still-transmitting rig; de-duplicating it would risk
                // under-warning.
                pttKeyedOnRealRig = _pttCoordinator.BelievesKeyed();

                // Round-15 finding 3: these three awaits had no bound of their own either -- unlike
                // findings 1/2 (round 14), PTT is NOT yet keyed at this point, so a hang here is not
                // the leaked-keyed-transmitter class. It is still a real, severe bug: RX was already
                // paused above (StopReceivingAsync, outside this guarded region -- see that call's own
                // comment for why it is deliberately NOT given the same treatment here), and a hang in
                // ANY of these three permanently strands _transmitInFlight (round 12's single-flight
                // guard never releases while an await here is still pending), killing TX *and* RX for
                // the rest of the process's life from a single wedged device enumeration or a settings
                // read against a hung network mount -- worth fixing on its own even without the PTT
                // angle. Bounded with the same WaitAsync treatment as findings 1/2: a TimeoutException
                // here routes through the generic catch below -> abnormalTermination -> the un-key
                // attempt (a harmless no-op via TryUnkeyPttAsync's own RigId=="none"/never-keyed
                // handling, since pttKeyedOnRealRig is still false at this point) and RX-resume, then
                // releases _transmitInFlight.
                //
                // Round-18 finding 4 (round-17's own deferral (a), now fixed): WaitAsync alone cannot
                // bound any of these three -- each bottoms out in JsonSettingsStore.LoadAsync, which
                // does blocking File.Exists/File.OpenRead BEFORE its own first `await` (see
                // GetStationIdTransmitOptionsAsync's own matching comment for the full reasoning), so
                // on a hung network-mounted settings path the call doesn't even return a genuinely-
                // pending Task for WaitAsync to race. Task.Run offloads that synchronous prefix onto a
                // pool thread so WaitAsync's bound becomes real.
                var device = await Task.Run(() => ResolveDeviceAsync(forCapture: false, ct), ct).WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);
                // Seeds _liveTxGain for this call in case nothing has called SetTxVolumePercentAsync
                // yet this process's life -- PumpToPlaybackAsync below reads the LIVE field per chunk,
                // not this local, so a Pwr-slider change after this point (e.g. mid-Tune) still takes
                // effect immediately. See _liveTxGain's own doc comment for the full reasoning.
                await Task.Run(() => SeedLiveTxGainAsync(ct), ct).WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);
                var audioSettings = await Task.Run(() => LoadAudioSettingsAsync(ct), ct).WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);

                // Guarded on RigId ("none" = the null-object "no radio" backend, spec/18-path-to-1.0.md
                // Critical item 1), not Capabilities -- see IRadioController.RigId's own doc comment for
                // why a live-capability check would be unsafe here (real backends connect lazily, so
                // Capabilities reads None during a real window even with a genuine PTT-capable rig
                // configured). Read into a local exactly once: RigId is NOT stable across this call (see
                // pttKeyedOnRealRig above), and this is the single read every later decision uses.
                var rigIsRealAtKeyTime = _radioSession.RigId != "none";

                if (rigIsRealAtKeyTime)
                {
                    // Published BEFORE the key command goes out and cleared only once this method's own
                    // finally has finished its un-key attempt, so DisposeAsync can never tear
                    // IRadioSessionService down out from under an in-flight un-key.
                    //
                    // Volatile.Write here paired with DisposeAsync's Volatile.Read would NOT be enough to
                    // make the publish-then-recheck below actually see each other -- a volatile write is a
                    // release-store and a volatile read is an acquire-load, and that pairing does not
                    // forbid StoreLoad reordering (the classic Dekker's-algorithm gap). On x86-64, .NET
                    // emits both as plain movs, so this call's write and DisposeAsync's _disposed write can
                    // each sit in a per-core store buffer while the OTHER side's read runs first -- this call
                    // reads _disposed as false, DisposeAsync reads this field as null, and neither side sees
                    // the other. PublishKeyedSlot's Interlocked.Exchange is a full fence, closing that gap.
                    _pttCoordinator.PublishKeyedSlot(attempt);

                    // Set BEFORE the await deliberately: a SetPttAsync that throws mid-command can still
                    // have physically keyed the rig, so "keyed" is the only safe assumption for the
                    // cleanup un-key's own failure reporting. Erring true costs at most one spurious
                    // Warning; erring false is the blocker-2 silent swallow.
                    pttKeyedOnRealRig = true;

                    // Publish-then-recheck: either DisposeAsync's read of _keyedTransmitCompletion (its own
                    // very first line sets _disposed before that read) sees THIS call's just-published
                    // completion and waits for it, or this call sees _disposed here and never issues the
                    // actual key command below at all -- the two sides cannot miss each other, even for a
                    // TuneAsync call with no CancellationToken (RadioStatusViewModel.TuneAsync passes
                    // CancellationToken.None).
                    if (_disposed)
                    {
                        // Provably never keyed BY THIS CALL -- the actual SetPttAsync(true) command is
                        // still below this block. Re-baselined to the same belief test the entry baseline
                        // uses, so the cleanup un-key's Critical-vs-Debug classification stays honest when
                        // an earlier call's failed unkey left the rig possibly keyed.
                        pttKeyedOnRealRig = _pttCoordinator.BelievesKeyed();
                        // GetType().FullName, not nameof(SstvSessionService), to match the ObjectName
                        // ObjectDisposedException.ThrowIf(_disposed, this) produces elsewhere in this class.
                        throw new ObjectDisposedException(GetType().FullName);
                    }

                    // Bounded with WaitAsync: RigctldClientProtocol bounds only its initial connect
                    // (_connectTimeout) -- the per-command reply read has none -- so a half-open CAT
                    // connection (peer power-cycled, VPN drop) could otherwise block this forever WHILE
                    // THE PTT COMMAND HAS ALREADY REACHED THE WIRE AND PHYSICALLY KEYED THE RIG. WaitAsync
                    // rather than a fresh CancellationTokenSource (contrast UnkeyForCleanupAsync's own
                    // pattern, a pure cleanup step with no caller-cancellation distinction to preserve): a
                    // genuine cancellation of `ct` still completes the awaited task with
                    // OperationCanceledException (the benign arm below), while exceeding the budget with
                    // no cancellation surfaces as TimeoutException instead, correctly routing through the
                    // generic catch below -> abnormalTermination -> urgent un-key-before-StopPlayback. `ct`
                    // is passed to WaitAsync itself too (CA2016 -- not just the inner call), so a genuine
                    // caller cancellation is observed and classified correctly HERE even if the backend
                    // itself never polls `ct` promptly once its native call has started
                    // (HamlibRadioProtocol's own documented limitation).
                    //
                    // Caveat: that urgent un-key is only a genuine recovery when the backend doesn't
                    // serialize requests behind the very command that just timed out --
                    // RigctldClientProtocol/HamlibRadioProtocol both hold a single request semaphore across
                    // the reply read, so the abandoned key command still holds it and the un-key attempt is
                    // expected to itself time out and log Critical ("MAY STILL BE KEYED"), not silently
                    // recover -- a signal to disconnect/reconnect the CAT link, not evidence the rig is
                    // actually off. The underlying call is left running in the background either way --
                    // same trade-off EnqueueAllAsync's own stall fix accepts.
                    Task? keyCommand = null;
                    try
                    {
                        keyCommand = _radioSession.SetPttAsync(true, ct);
                        await keyCommand.WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // A failed-but-possibly-keyed attempt is a NEW key for _pttKeyEpoch's purposes,
                        // and the cleanup retry gate depends on it: pttKeyedOnRealRig is already true, so
                        // this bump is what guarantees _pttKeyEpoch >= 1 when a throw here leaves
                        // keyEpochAfterOwnKeyAttempt/unkeyEpochAfterOwnKeyAttempt at their 0 placeholders.
                        // Without it, transmit #1 (key fails, urgent unkey confirms off -- unkey epoch 1)
                        // followed by transmit #2 (key fails, urgent unkey fails) would read
                        // ConfirmedUnkeyAlreadyHappenedSinceOwnKey(0, 0) as true and skip the retry on a
                        // rig transmit #2 may have keyed. It also stops a concurrent DisposeAsync backstop
                        // un-key that snapshotted the epoch BEFORE this command reached the wire from
                        // clearing _pttUnkeyFailedOnRealRig.
                        _pttCoordinator.RecordKeyEpochAdvance();

                        // Fault-observer for the abandoned key command -- this is the one abandoned
                        // command that may have physically keyed the rig. Harm is diagnostic/attribution
                        // only -- state handling is already correct regardless (epoch bump, then
                        // abnormalTermination -> urgent un-key in the finally). Only attached if the
                        // command was actually issued (not still null from a synchronous throw) and is
                        // still running (if it already finished, its own fault already propagated through
                        // the WaitAsync above as the exception this catch is handling).
                        if (keyCommand is { IsCompleted: false })
                        {
                            _ = keyCommand.ContinueWith(
                                t => SafeLog(() => Log.CleanupStepFailed(_logger, "PTT key command (finished after watchdog)", t.Exception!)),
                                CancellationToken.None,
                                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                                TaskScheduler.Default);
                        }

                        throw;
                    }

                    // See PttSafetyCoordinator's own doc comment (Race 1).
                    _pttCoordinator.RecordKeyEpochAdvance();
                    // SafeLog: the one Log.* call that executes while a real transmitter is physically
                    // keyed -- a transient logging-provider failure here must not abort the transmit with
                    // the raw logging exception instead of a caller-recognizable type.
                    SafeLog(() => Log.PttKeyed(_logger));
                }
                else
                {
                    // Same shape, no-radio branch -- PTT is never touched here, so the only harm is a
                    // transient logging failure aborting a no-radio transmit with a bogus exception type.
                    SafeLog(() => Log.PttSkippedNoRadio(_logger));
                }

                // Both epochs snapshotted HERE, right after this call's own key phase (success, failure,
                // or no-radio skip), with no await between the two reads, so the pair answers "since MY
                // OWN key: did anyone confirm off, and did anyone re-key". A snapshot at method entry
                // would misread a confirmed un-key that landed BEFORE this call's key command as
                // "confirmed after I keyed" and skip this call's own un-key. The key-epoch half makes the
                // cleanup skip require that nobody re-keyed since this point either -- the unkey epoch
                // alone answers "did a confirmed un-key happen", not "is the rig off NOW" -- erring toward
                // attempting the un-key whenever anyone has re-keyed since.
                keyEpochAfterOwnKeyAttempt = _pttCoordinator.CurrentKeyEpoch;
                unkeyEpochAfterOwnKeyAttempt = _pttCoordinator.CurrentUnkeyEpoch;

                // Round-14 finding 2: same unbounded-await shape as finding 1, one step later --
                // PTT is already keyed by this point, so an
                // indefinite native device-open hang here is exactly as much a leaked-keyed-
                // transmitter exposure as finding 1's key command itself, not a smaller-window
                // variant of it. Same WaitAsync treatment, same reasoning. Round-15 nit:
                // MiniAudioEngine.StartPlaybackAsync holds its own playback lock across the abandoned
                // native open, so the following StopPlaybackWithWatchdogAsync's own claim step queues
                // FIFO-behind it and can burn its own full watchdog budget too (self-healing, not a
                // new failure mode -- the queued claim still eventually gets and disposes the late-
                // published session -- but worth knowing this path's worst case is roughly
                // 2x _cleanupTimeout, not 1x, if this lands during DisposeAsync's own bounded wait).
                await _audioEngine.StartPlaybackAsync(
                    device, sampleRate, audioSettings.PeriodSizeInFrames, audioSettings.Periods,
                    audioSettings.StereoTxEnabled, ct).WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);
                await PumpToPlaybackAsync(samples, sampleRate, totalSamplesEstimate, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is a normal, expected way for this to end (manual Stop TX, SWR
                // auto-cutoff -- see TxControlsPaneViewModel for which one) -- this layer has no way to
                // tell which caused it, so it's logged generically at Information, not as a failure.
                abnormalTermination = true;
                // Round-22 finding (risk): a throwing log call here (a broken logging provider) used
                // to REPLACE this OperationCanceledException as what propagates out of this method --
                // TxControlsPaneViewModel's own catch (OperationCanceledException) branches on this
                // exception identity to distinguish an SWR-cutoff abort from a generic failure (see
                // RaiseCapturePausedForTransmitChanged's own doc comment for the identical harm, which
                // round 20 already guarded against for that method). PTT itself is unaffected either
                // way -- the finally below still runs the urgent un-key -- only the exception identity
                // reaching the caller was at risk.
                SafeLog(() => Log.PlaybackCancelled(_logger));
                throw;
            }
            catch (ObjectDisposedException) when (_disposed)
            {
                // Round-3 nit: without this arm, the disposed-check throw above falls into the generic
                // `catch (Exception)` below and logs at Error with a full stack trace on every ordinary
                // close-during-tune -- this is an expected shutdown condition (same category as the
                // OperationCanceledException arm above), not a failure. The `when (_disposed)` guard only
                // narrows this to "we are already mid-DisposeAsync" -- it does NOT distinguish OUR disposed-
                // check throw from a genuine ObjectDisposedException some other dependency happens to throw
                // during that same shutdown window (e.g. a disposed audio/radio backend); both are equally
                // expected once _disposed is true, so both are logged at Information here. A GENUINE
                // ObjectDisposedException reached OUTSIDE of shutdown (_disposed still false) still falls
                // through to the generic catch below and logs as a real Error-level failure.
                abnormalTermination = true;
                // Round-22 finding (risk): same shape as the OperationCanceledException arm above -- a
                // throwing log call used to substitute an unrelated exception for the
                // ObjectDisposedException this class's own established convention has callers branch
                // on.
                SafeLog(() => Log.PlaybackAbortedByDispose(_logger));
                throw;
            }
            catch (Exception ex)
            {
                abnormalTermination = true;
                // Round-22 finding (nit): same log-before-rethrow shape as the two typed catch arms
                // above, wrapped for consistency -- abnormalTermination is already set and the finally
                // below still runs the urgent un-key regardless, so this is exception-identity-only.
                SafeLog(() => Log.PlaybackFailed(_logger, ex));
                throw;
            }
            finally
            {
                try
                {
                    var unkeyAlreadyAttempted = false;
                    var unkeyConfirmed = false;

                    // ---- Blocker 1, urgent half ----
                    // An abnormal termination IS the safety path (manual Stop TX / SWR auto-cutoff): the
                    // operator wants the transmitter off NOW and there is no audio tail worth preserving
                    // (the image is aborted either way). Un-key BEFORE StopPlayback so a wedged output
                    // device can't delay it at all.
                    // Round-19 finding: this whole cleanup region (the enclosing try starting above)
                    // had a finally but NO catch -- both UnkeyForCleanupAsync and
                    // StopPlaybackWithWatchdogAsync are documented as never throwing, but neither claim
                    // was actually enforced, and a throwing logging provider (the same realistic source
                    // round 17/18 already treated as in-scope for DisposeAsync's own per-step guards) is
                    // enough to violate it. Without a guard HERE, that throw skips every step below it
                    // (StopPlayback, the retry, RX-resume) with NOTHING recorded -- worse than
                    // DisposeAsync's own equivalent gap, since this is the PTT-off record itself, not
                    // just teardown. Each step now gets its own try/catch, matching DisposeAsync's
                    // already-established per-step shape (see its own comment).
                    if (abnormalTermination)
                    {
                        try
                        {
                            unkeyConfirmed = await UnkeyForCleanupAsync(pttKeyedOnRealRig).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            // Round-20 finding: SafeLog, not a plain call -- see its own doc comment.
                            // The state record itself doesn't depend on this log succeeding either
                            // way (UnkeyForCleanupAsync's own round-20 fix already latched
                            // _pttUnkeyFailedOnRealRig before it could throw), only whether this catch
                            // itself stays reachable so StopPlayback/the retry/RX-resume below still run.
                            SafeLog(() => Log.CleanupStepFailed(_logger, "PTT off (urgent)", ex));
                        }

                        unkeyAlreadyAttempted = true;
                    }

                    // ---- Blocker 1, budget half ----
                    // On a NORMAL completion the un-key still runs AFTER the drain -- dropping PTT while
                    // the miniaudio ring / PulseAudio server queue still hold audio would truncate the
                    // tail of every successful transmission, exactly what MiniAudioEngine.DrainTailMargin
                    // exists to prevent. What changed is that the drain can no longer STARVE the un-key:
                    // it gets a bounded wait of its own, and the un-key gets its own fresh
                    // CancellationTokenSource inside UnkeyForCleanupAsync.
                    try
                    {
                        await StopPlaybackWithWatchdogAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        SafeLog(() => Log.CleanupStepFailed(_logger, "StopPlayback", ex));
                    }

                    // Round-18 finding: UnkeyForCleanupAsync's own bool return (whether the un-key was
                    // actually CONFIRMED, not just attempted) used to be discarded here -- an urgent
                    // un-key that timed out against a momentarily-busy backend (SWR cutoff / manual Stop
                    // TX racing a backend that frees up moments later) got exactly one attempt, then
                    // deferred all the way to DisposeAsync, which may not run for hours. Retried here,
                    // immediately, while the failure is still fresh. Guarded on pttKeyedOnRealRig so the
                    // benign RigId=="none" case (already correctly "confirmed" false-but-harmless) never
                    // gets a pointless second attempt.
                    //
                    // Round-19 correction: the retry does NOT race the first attempt on an independent
                    // window, as an earlier version of this comment claimed -- round 18's own fix left
                    // the first, timed-out command queued and UNCANCELLABLE (CancellationToken.None,
                    // see TryUnkeyPttAsync's own comment), and both backends serialize on a single
                    // approximately-FIFO request gate, so this retry queues BEHIND the abandoned first
                    // command rather than beside it. Net effect is still strictly better than pre-
                    // round-18 (the first command eventually reaches an unwedging rig on its own, and
                    // if it doesn't, this retry -- and later DisposeAsync's own backstop -- queue up
                    // behind it and get their own turn once it clears), just not via the two
                    // "independent" attempts the removed wording implied.
                    if (!unkeyAlreadyAttempted || (pttKeyedOnRealRig && !unkeyConfirmed))
                    {
                        // Skip the attempt when a confirmed un-key already happened anywhere since this
                        // call's own key phase AND nobody re-keyed since (see the epoch snapshots' own
                        // comment) -- e.g. DisposeAsync's backstop un-key landing after its bounded wait
                        // for this transmit expired. A redundant attempt that then fails for an unrelated
                        // reason (CAT link dropped in the interim) would falsely latch
                        // _pttUnkeyFailedOnRealRig on a rig that is demonstrably off.
                        if (pttKeyedOnRealRig && _pttCoordinator.ConfirmedUnkeyAlreadyHappenedSinceOwnKey(keyEpochAfterOwnKeyAttempt, unkeyEpochAfterOwnKeyAttempt))
                        {
                            // No `unkeyConfirmed = true` here -- nothing rechecks this block's own outer
                            // `if` condition afterward (this whole block runs at most once per call).
                            SafeLog(() => Log.PttCleanupUnkeySkippedConfirmedElsewhere(_logger));
                        }
                        else
                        {
                            try
                            {
                                await UnkeyForCleanupAsync(pttKeyedOnRealRig).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                // SafeLog -- see the urgent-half arm's own comment above.
                                SafeLog(() => Log.CleanupStepFailed(_logger, "PTT off (retry)", ex));
                            }
                        }
                    }

                    if (wasReceiving)
                    {
                        // Created only HERE, after every potentially-slow step above, so the RX-resume
                        // step gets a full independent budget rather than whatever StopPlayback/un-key left
                        // over -- the same starvation bug blocker 1 is about, one step further down the
                        // chain. ResumeReceivingBoundedAsync also wraps StartReceivingAsync in
                        // .WaitAsync(rxResumeCts.Token): passing the token as the callee's own `ct` alone
                        // never bounded it, since MiniAudioDeviceEnumerator.RefreshAsync/
                        // MiniAudioEngine.StartCaptureAsync only check `ct` at their own start/lock-acquire
                        // boundary, not during the blocking native call itself -- and a pending await here
                        // would strand _transmitInFlight (released only in this method's outer finally).
                        using var rxResumeCts = new CancellationTokenSource(_cleanupTimeout);
                        await TryCleanupAsync("Resume RX", () => ResumeReceivingBoundedAsync(rxResumeCts)).ConfigureAwait(false);
                        // Fires once the resume attempt has finished, success or failure -- this event is
                        // "no longer paused FOR THIS transmission," not a restatement of IsReceiving
                        // itself (see the event's own doc comment).
                        RaiseCapturePausedForTransmitChanged(false);
                    }
                }
                finally
                {
                    // Blocker 3: signalled no matter how the cleanup above ended (including a throw from
                    // a subscriber or a cleanup step) -- a DisposeAsync waiting on this must never be left
                    // hanging until its own timeout by an unrelated failure. See
                    // PttSafetyCoordinator.ReleaseKeyedSlot's own doc comment: only clears the shared
                    // registration if it still points at THIS call's instance, so an overlapping
                    // PlayWithPttAsync call (round-6 finding: a REAL production interleaving, not
                    // pathological) can't have its own registration erased.
                    _pttCoordinator.ReleaseKeyedSlot(attempt);
                }
            }
        }
        finally
        {
            // Released unconditionally -- whether this call completed normally, was cancelled, or
            // threw at any point above, including before PTT was ever touched.
            Volatile.Write(ref _transmitInFlight, 0);
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<float>> GenerateTone(
        double frequencyHz, TimeSpan duration, int sampleRate, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var totalSamples = (long)(duration.TotalSeconds * sampleRate);
        var angularStep = 2.0 * Math.PI * frequencyHz / sampleRate;
        var buffer = new float[ToneBatchSize];
        var count = 0;

        for (var i = 0L; i < totalSamples; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Plan-review finding: MUST stay the absolute sample index i, never a per-batch-relative
            // offset -- this project has hit exactly this local-vs-absolute-index bug class three
            // separate times before (RX buffer phases 6c-6d). angularStep*i is what keeps the sine
            // wave phase-continuous across every batch boundary.
            buffer[count++] = (float)Math.Sin(angularStep * i);
            if (count == ToneBatchSize)
            {
                yield return buffer;
                buffer = new float[ToneBatchSize];
                count = 0;
                await Task.Yield();
            }
        }

        if (count > 0)
        {
            yield return buffer.AsMemory(0, count);
        }
    }

    /// <summary><paramref name="totalSamplesEstimate"/> is <see langword="null"/> for
    /// <see cref="TuneAsync"/> (no <see cref="TransmitProgressChanged"/> reporting for a tone) and a
    /// real value for <see cref="TransmitAsync"/> (spec/18-path-to-1.0.md Medium item). The running
    /// sample counter is a local, not a field -- nothing must dangle across separate calls.</summary>
    // T1-3 (production_audit.md): samples is IAsyncEnumerable<ReadOnlyMemory<float>> (batched) --
    // consumed via an outer loop over batches (few awaits) wrapping an inner, synchronous loop over
    // each batch's own samples (unchanged from before: still one MoveNextAsync per SAMPLE would have
    // been needed to get the per-sample gain multiply either way, so the win here is entirely in the
    // OUTER loop's own await count, not a removed inner loop). chunkSize/buffer/count/gain-refresh
    // cadence/ReportTransmitProgress cadence are ALL UNCHANGED from before this change, still driven
    // entirely by this method's OWN chunkSize constant -- completely decoupled from whatever batch
    // size the producer happens to use internally, deliberately, so this method's own observable
    // behavior (progress-report frequency, gain-refresh frequency) is provably unaffected by T1-3.
    private async Task PumpToPlaybackAsync(IAsyncEnumerable<ReadOnlyMemory<float>> samples, int sampleRate, long? totalSamplesEstimate, CancellationToken ct)
    {
        const int chunkSize = 4096;
        var buffer = new float[chunkSize];
        var count = 0;
        var samplesEnqueued = 0L;

        // Re-read once per chunk (not once for the whole call, the older behavior) -- see
        // _liveTxGain's own doc comment. A per-sample volatile read would be needlessly expensive on
        // this hot loop; re-reading every 4096 samples (~0.1-0.4s at typical SSTV sample rates) is
        // well within "immediate" for a human dragging a slider while watching a power meter.
        // Auditor-caught: the chunk-boundary term above is not the WHOLE slider-to-air latency --
        // gain is applied at enqueue time, and whatever is already sitting in MiniAudioPlaybackSession's
        // own playback ring (16384 frames by default, StartPlaybackAsync's periodSizeInFrames/periods
        // args) still plays at the OLD gain regardless. That adds up to ~0.3s more at Tune's fixed
        // 48kHz (the primary workflow this exists for) and up to ~1.5s more mid-image at a real
        // 11025Hz TX sample rate -- still usable for "drag Pwr, watch the meter," just not as tight
        // as the chunk-boundary number alone implies.
        var gain = _liveTxGain;

        await foreach (var chunk in samples.WithCancellation(ct).ConfigureAwait(false))
        {
            // Plan-review finding: chunk.Span is deliberately NOT hoisted to a local outside this
            // inner loop -- the loop body contains an await (EnqueueAllAsync below), and a
            // ReadOnlySpan<float> local can't live across one (CS4012/4013, the same constraint
            // AnalogFmSstvEncoder.EncodeBatchedAsyncCore's own doc comment already documents for
            // itself). `chunk.Span[i]` inline as a transient rvalue is correct.
            for (var i = 0; i < chunk.Length; i++)
            {
                buffer[count++] = chunk.Span[i] * gain;
                if (count == chunkSize)
                {
                    await EnqueueAllAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    samplesEnqueued += count;
                    ReportTransmitProgress(samplesEnqueued, totalSamplesEstimate, sampleRate);
                    count = 0;
                    gain = _liveTxGain;
                }
            }
        }

        if (count > 0)
        {
            await EnqueueAllAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            samplesEnqueued += count;
            ReportTransmitProgress(samplesEnqueued, totalSamplesEstimate, sampleRate);
        }
    }

    /// <summary>See <see cref="ISstvSessionService.TransmitProgressChanged"/>'s own doc comment for
    /// the full threading contract this raise site must uphold: synchronous on the playback pump
    /// thread, so a throwing subscriber must not be allowed to propagate out and abort the
    /// transmission this progress report belongs to.</summary>
    private void ReportTransmitProgress(long samplesEnqueued, long? totalSamplesEstimate, int sampleRate)
    {
        if (totalSamplesEstimate is not { } total || total <= 0)
        {
            return;
        }

        // Code-review finding: try/catch scoped to ONLY the invoke, not the fraction/TimeSpan math
        // above it -- a future change to that math throwing (e.g. a genuinely degenerate sampleRate)
        // would otherwise get misattributed to "a subscriber's handler threw" in the log.
        var fraction = Math.Clamp(samplesEnqueued / (double)total, 0.0, 1.0);
        var info = new TransmitProgressInfo(
            fraction,
            TimeSpan.FromSeconds(samplesEnqueued / (double)sampleRate),
            TimeSpan.FromSeconds(total / (double)sampleRate));

        try
        {
            TransmitProgressChanged?.Invoke(info);
        }
        catch (Exception ex)
        {
            // Hot-path rate-limiting (docs/logging-guidelines.md), same reasoning/pattern as
            // _decoderExceptionCount/_waterfallExceptionCount above: this fires roughly every 4096
            // samples (~2.7/s at 11025Hz), so an unguarded Error log per occurrence would flood the
            // log file across a multi-minute transmission if a subscriber keeps throwing.
            var count = Interlocked.Increment(ref _transmitProgressHandlerExceptionCount);
            if (count == 1 || count % ExceptionLogEveryN == 0)
            {
                SafeLog(() => Log.TransmitProgressHandlerFailed(_logger, count, ex));
            }
        }
    }

    private async Task EnqueueAllAsync(ReadOnlyMemory<float> chunk, CancellationToken ct)
    {
        var offset = 0;

        // Round-13 finding: null while samples are flowing, set to the moment the FIRST consecutive
        // zero-accepted attempt happens -- see PlaybackStallTimeout's own doc comment for the failure
        // this closes. Reset to null on every successful accept, so a healthy device that only
        // legitimately backpressures briefly (buffer momentarily full, draining normally) never trips
        // this -- only a stretch of ZERO progress lasting the full timeout does.
        long? stallStartMs = null;

        while (offset < chunk.Length)
        {
            var accepted = _audioEngine.EnqueuePlaybackSamples(chunk[offset..]);
            if (accepted == 0)
            {
                stallStartMs ??= Environment.TickCount64;
                if (Environment.TickCount64 - stallStartMs.Value >= _playbackStallTimeout.TotalMilliseconds)
                {
                    throw new TimeoutException(
                        $"Playback device accepted no samples for {_playbackStallTimeout} -- assuming it is wedged.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), ct).ConfigureAwait(false);
                continue;
            }

            stallStartMs = null;
            offset += accepted;
        }
    }
}
