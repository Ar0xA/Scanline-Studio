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

    public Task TuneAsync(double frequencyHz, TimeSpan duration, bool leaveKeyedAfterTune = false, CancellationToken ct = default)
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
        return PlayWithPttAsync(GenerateTone(frequencyHz, duration, sampleRate, ct), sampleRate, ct, leaveKeyedAfterCall: leaveKeyedAfterTune);
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
    /// <paramref name="samples"/>, then un-keys PTT in a <c>finally</c> no matter how playback ends --
    /// unless <paramref name="leaveKeyedAfterCall"/> is set (see <see cref="TuneAsync"/>'s own doc
    /// comment for its one caller) or <see cref="SetPttLockAsync"/>'s lock is currently engaged, in
    /// which case the un-key/resume-RX steps are skipped -- <b>but only on a NORMAL (successful)
    /// completion</b>. A cancellation or fault (manual Stop TX, SWR auto-cutoff -- see
    /// <c>TxControlsPaneViewModel</c>) ALWAYS un-keys PTT, even if the lock was engaged: a safety
    /// cutoff/manual stop must never be overridable by "stay keyed" state (a real defect an audit pass
    /// caught and this fix closes -- the lock existing at all must never be able to defeat the SWR
    /// cutoff's whole reason for existing). <b>Doc-comment correction (round-7 finding):</b> the un-key
    /// COMMAND is unconditional, but clearing the lock/state flags it left behind is not, as of round
    /// 5's epoch guard in <c>UnkeyForCleanupAsync</c> -- if a CONCURRENT, NEWER key command completed
    /// during this call's own un-key attempt, the clear is deliberately skipped so it doesn't wipe
    /// that newer call's genuinely-still-keyed state (see <c>_pttKeyEpoch</c>'s own doc comment).
    /// "Force-clears the lock" was accurate before that fix; it is not unconditional anymore.
    ///
    /// Device resolution and the entry PTT-key now live INSIDE the guarded region (moved in during
    /// the same audit-fix pass) -- previously a device-resolution failure (e.g. no playback device
    /// configured) after RX had already been paused above left RX stopped forever, since the old
    /// shape's <c>try</c>/<c>finally</c> didn't start until after those calls.</summary>
    // T1-3 (production_audit.md): samples is IAsyncEnumerable<ReadOnlyMemory<float>> (batched), not
    // IAsyncEnumerable<float> -- verified this parameter has exactly ONE use in this method's whole
    // 844-line body (the pass-through to PumpToPlaybackAsync below), so this is a pure type-plumbing
    // change, not a logic change to anything PTT-safety-related in this method. Both real producers
    // (AnalogFmSstvEncoder.EncodeBatchedAsync via TransmitAsync, GenerateTone via TuneAsync) batch
    // now specifically so PumpToPlaybackAsync can consume batches directly -- see that method's own
    // comment for why flattening back to individual floats before this point would erase the entire
    // point of batching.
    private async Task PlayWithPttAsync(IAsyncEnumerable<ReadOnlyMemory<float>> samples, int sampleRate, CancellationToken ct, bool leaveKeyedAfterCall = false, long? totalSamplesEstimate = null)
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

            // Round-26 finding, round-28 correction: declared out here, not alongside its own snapshot
            // assignment inside the try below -- the cleanup finally needs to read this, and a local
            // declared inside the try is not in scope there. Round-28 finding (nit): the ORIGINAL
            // safety argument for this placeholder ("every guard is prefixed with `pttKeyedOnRealRig &&`,
            // also false-by-default until the try reaches its own snapshot line") went stale the moment
            // round 27 moved pttKeyedOnRealRig's own assignment earlier (now near the top of the try,
            // before this field is ever touched) -- so it is generally NOT false-by-default anymore at
            // any point this placeholder could matter. Still safe, for a different reason: every
            // consumer requires pttKeyedOnRealRig true, which requires _pttLocked having been true at
            // some point, which requires _pttKeyEpoch >= 1 -- combined with the second epoch term below,
            // every interleaving still errs toward attempting the un-key, never toward wrongly skipping
            // one based on this specific placeholder value.
            var unkeyEpochAfterOwnKeyAttempt = 0;

            // Round-27 finding: same reason/shape as unkeyEpochAfterOwnKeyAttempt just above -- declared out here,
            // assigned inside the try below, once this call's own key phase (not just method entry) has
            // completed. See that assignment's own comment.
            var keyEpochAfterOwnKeyAttempt = 0;

            // Blocker 3: this call's own handle into the coordinator's keyed-slot registration.
            var attempt = new PttSafetyCoordinator.PttKeyAttempt();

            try
            {
                // Round-27 finding (risk): pttKeyedOnRealRig's own round-25 baseline used to read AND
                // ASSIGN from _pttLocked via pttLockedBeforeKeyDecision, both of which happen LATE -- just before
                // the rigIsRealAtKeyTime branch, AFTER the three bounded device/settings awaits below.
                // Correct for pttLockedBeforeKeyDecision's OWN purpose (deciding whether to double-key an
                // already-engaged lock, which must see the freshest possible state), but wrong for
                // baselining pttKeyedOnRealRig: a failure in any of the three awaits below (device not
                // found, no device configured, or the WaitAsync timeout itself -- all realistic)
                // reached the generic catch with pttKeyedOnRealRig still at its OUTER false default,
                // since the code never reached the late assignment -- even when a lock was ALREADY
                // engaged at this call's own true entry, reproducing verbatim the harm round 25 exists
                // to prevent (no Critical, no retry, just a Debug "no radio configured" line). Both the
                // read AND the assignment happen HERE instead, before those awaits -- the read into a
                // separate local from pttLockedBeforeKeyDecision (kept late, for its own purpose), the assignment
                // directly into pttKeyedOnRealRig so it survives a throw from any of the three awaits.
                // The rigIsRealAtKeyTime branch further down still unconditionally sets this true for
                // the case THIS call keys/re-keys the rig itself, a strict superset of this baseline.
                //
                // Round-28 finding (nit, flagged not fixed): moving this baseline earlier widened its
                // OWN false-positive window in the opposite direction -- if a lock is engaged here, then
                // during the three awaits below a confirmed un-key genuinely turns the rig off AND
                // RigId separately goes to "none" (RadioController.DisconnectAsync), this call never
                // keys (rigIsRealAtKeyTime false) but pttKeyedOnRealRig stays stuck true from this
                // stale baseline -- cleanup then attempts a doomed un-key against the null-object
                // backend and logs a false Critical on a rig already confirmed off elsewhere. Pre-round-
                // 27 this same window was instruction-scale (right up to the read); it is now up to
                // three bounded-await-widths. unkeyEpochAfterOwnKeyAttempt's own round-28 fix does not rescue this
                // -- that confirmed un-key's own epoch bump lands BEFORE unkeyEpochAfterOwnKeyAttempt's own (also
                // moved-later) read, so the pair sees no change and does not skip the doomed attempt.
                // Narrow (needs lock-engaged-then-confirmed-unlock-then-disconnect, all within these
                // three awaits) and requires SetPttLockAsync, which has zero production callers today --
                // not fixed this round; a real fix would need this baseline itself to be downgradeable
                // by a confirmed un-key observed since this exact line, not just guardable at the
                // consumer sites the way _pttUnkeyEpoch already is.
                //
                // Round-29 finding (nit): this used to read _pttLocked alone -- narrower than this
                // class's own equivalent "did this class believe something was keyed" test used at the
                // unlock-direction catch above (_pttLocked || _pttLeftKeyedByCall ||
                // _pttUnkeyFailedOnRealRig). Scenario: a prior TuneAsync(leaveKeyedAfterTune: true) left
                // the rig physically keyed (_pttLeftKeyedByCall = true, _pttLocked still false), then
                // RadioController.DisconnectAsync sets RigId to "none" without un-keying -- a new
                // transmit entering here baselined false, so its cleanup un-key logged Debug instead of
                // the Critical this baseline exists to guarantee. Not a leak either way
                // (_pttLeftKeyedByCall itself survives untouched, so DisposeAsync's own four-state
                // backstop still fires) -- same signal-quality class this whole baseline exists to
                // protect, one flag short. Widened to match the established belief test exactly.
                //
                // Round-30 finding (nit, considered and accepted, not changed): including
                // _pttUnkeyFailedOnRealRig here means that once ONE real un-key failure has latched it
                // and the operator then disconnects (RigId -> "none", where every un-key attempt throws
                // synchronously and so can never CONFIRM success to clear the flag -- see its own clear
                // sites), every SUBSEQUENT transmit's cleanup re-attempts an un-key against the
                // null-object backend, fails again, and re-emits a fresh Critical "PTT MAY STILL BE
                // KEYED" -- once per transmit, indefinitely, not just once. This is the signal-erosion
                // SHAPE rounds 4/19 added gates to prevent, but the underlying belief (a rig that failed
                // to un-key and is now unreachable) is genuinely still unresolved -- unlike the false
                // positives those two rounds actually fixed (a rig confirmed OFF, or never keyed at
                // all), there is no confirmed-safe state to fall silent about here. A real fix would need
                // to distinguish "the same still-latent failure repeating" from "a genuinely new event"
                // (e.g. de-duplicating by _pttUnkeyEpoch's own value at latch time), which trades a
                // repeated-but-true alarm for a real risk of under-warning if implemented wrong. Left as
                // a persistent, if repetitive, Critical -- the more conservative failure mode for a
                // possibly-still-transmitting rig.
                pttKeyedOnRealRig = _pttCoordinator.BelievesKeyed(_pttCoordinator.IsPttLocked);

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
                // configured). Read into a local exactly once: the old code's "RigId is stable so there's
                // no was-available-at-entry-gone-by-cleanup scenario" claim was FALSE (see
                // pttKeyedOnRealRig above), and this is the single read that claim is now replaced by.
                //
                // Round-31 finding (nit): renamed from pttLockedAtEntry -- despite the name, this was
                // never read at method ENTRY (that read is the separate, genuinely-early one feeding
                // pttKeyedOnRealRig's own baseline above, before the three bounded awaits). This one is
                // deliberately read LATE, right here, for its own different purpose (deciding whether to
                // double-key an already-engaged lock, which needs the freshest possible observation) --
                // see its own consumers' comments. The old name invited exactly the kind of confusion
                // round 29 already fixed once for unkeyEpochAtEntry -> unkeyEpochAfterOwnKeyAttempt.
                var pttLockedBeforeKeyDecision = _pttCoordinator.IsPttLocked;
                var rigIsRealAtKeyTime = _radioSession.RigId != "none";

                // Round-28 finding (blocker): this used to be snapshotted here, at method entry -- see
                // _pttUnkeyEpoch's own doc comment for what it's used for. Round 28 found that reading
                // it THIS early was itself the bug: it let a confirmed un-key that happened BEFORE this
                // call's own key command be misread, at cleanup time, as "confirmed AFTER I keyed" --
                // the pairing with keyEpochAfterOwnKeyAttempt (below) only proves "nobody re-keyed since
                // MY key phase", not "this specific confirmed un-key postdates my key". Moved to the
                // SAME point as keyEpochAfterOwnKeyAttempt (right after this call's own key phase, see
                // that assignment's own comment) so the pair now correctly means "since MY OWN key:
                // someone confirmed off, and nobody re-keyed" -- preserving both of round 26's own
                // motivating scenarios (an unlock mid-tone, and an unlock during
                // StopPlaybackWithWatchdogAsync's own drain), both of which land after that point.

                // Round-25 finding (risk): pttKeyedOnRealRig is baselined on an already-engaged lock --
                // an already-engaged lock means the rig is genuinely keyed independent of THIS call's
                // own RigId observation, even once RigId has since gone to "none" (RadioController.
                // DisconnectAsync's own documented no-unkey-on-disconnect behavior, the file's own
                // blocker-2 premise). Previously this stayed false whenever rigIsRealAtKeyTime was
                // false, silently disabling the cleanup Critical log AND the round-18 retry for a call
                // that entered on a rig genuinely keyed by an EARLIER SetPttLockAsync call whose RigId
                // has since gone stale -- an SWR cutoff/manual Stop TX on that call then un-keyed with
                // no Critical, no retry, just a Debug "no radio configured" line. The branch below still
                // unconditionally sets this true for the case THIS call keys/re-keys the rig itself, a
                // strict superset of this baseline.
                //
                // Round-27 finding (risk): that baseline is now assigned at this method's own true
                // entry (see the assignment right after the try block opens, above), not here -- this
                // comment stays as the reasoning for WHY the baseline exists; see that assignment's own
                // comment for WHY it had to move earlier.

                // Round-26 finding (nit, flagged not fixed): when pttLockedBeforeKeyDecision is true but
                // rigIsRealAtKeyTime is false (a lock engaged on a real rig, then RadioController.
                // DisconnectAsync set RigId to "none" without un-keying), the baseline just above
                // correctly declares this call IS holding a genuinely keyed real rig -- but the
                // keyedCompletion publish/_keyedTransmitCount increment just below are still gated
                // entirely on rigIsRealAtKeyTime, so DisposeAsync will not wait for THIS call's own
                // cleanup un-key. Harmless while RigId stays "none" (nothing to talk to either way);
                // if RadioController reconnects mid-transmit, the cleanup un-key could race
                // IRadioSessionService's own DI teardown, the exact hazard the round-8 publish exists
                // to prevent. Narrow (requires a lock-then-disconnect-then-reconnect-mid-transmit
                // sequence) and not a live bug today -- not fixed this round; revisit if reachability
                // ever changes.
                if (rigIsRealAtKeyTime)
                {
                    // Published BEFORE the key command goes out and cleared only once this method's own
                    // finally has finished its un-key attempt, so DisposeAsync can never tear
                    // IRadioSessionService down out from under an in-flight un-key. Published in the
                    // pttLockedBeforeKeyDecision case too: this call didn't key the rig, but the rig IS keyed for
                    // the whole duration of this call either way.
                    //
                    // Round-3 finding: Volatile.Write here paired with DisposeAsync's Volatile.Read is NOT
                    // enough to make the publish-then-recheck below actually see each other -- a volatile
                    // write is a release-store and a volatile read is an acquire-load, and that pairing does
                    // not forbid StoreLoad reordering (the classic Dekker's-algorithm gap). On x86-64, .NET
                    // emits both as plain movs, so this call's write and DisposeAsync's _disposed write can
                    // each sit in a per-core store buffer while the OTHER side's read runs first -- this call
                    // reads _disposed as false, DisposeAsync reads this field as null, and neither side sees
                    // the other. Interlocked.Exchange is a full fence, closing that gap. (The pre-existing
                    // Risk B pattern below -- _rxPendingResumeAfterUnlock/_pttLocked -- has the identical
                    // shape and the identical gap; that one's failure mode is a stranded RX pause, not a
                    // leaked keyed transmitter, so it hasn't been given the same treatment here.)
                    _pttCoordinator.PublishKeyedSlot(attempt);

                    // Set BEFORE the await deliberately: a SetPttAsync that throws mid-command can still
                    // have physically keyed the rig, so "keyed" is the only safe assumption for the
                    // cleanup un-key's own failure reporting. Erring true costs at most one spurious
                    // Warning; erring false is the blocker-2 silent swallow.
                    pttKeyedOnRealRig = true;

                    // Round-2 fix, publish-then-recheck (same shape as Risk B's own at the RX-resume branch
                    // further down this method): either DisposeAsync's read of _keyedTransmitCompletion
                    // (its own very first line now sets _disposed before that read) sees THIS call's
                    // just-published completion and waits for it, or this call sees _disposed here and
                    // never issues the actual key command below at all -- the two sides can no longer miss
                    // each other the way a TuneAsync call with no CancellationToken
                    // (RadioStatusViewModel.TuneAsync passes CancellationToken.None) previously could race
                    // a closing DisposeAsync.
                    if (_disposed)
                    {
                        // Provably never keyed BY THIS CALL -- the actual SetPttAsync(true) command is
                        // still below this block. Only an already-engaged lock could have the rig keyed
                        // independent of this call, and pttLockedBeforeKeyDecision covers exactly that -- keeps the
                        // cleanup un-key's Critical-vs-Debug classification honest in the finally below.
                        // Deliberately the LATE read here -- this is exactly the "don't double-key/
                        // misclassify an already-engaged lock" decision that local is designed for, and
                        // the freshest possible observation is correct for it.
                        // (Round 27 note: no longer necessarily the same value the baseline near this
                        // method's own entry assigned -- that one now deliberately uses the EARLY
                        // `_pttLocked` read taken at entry instead, so the two can genuinely differ if
                        // _pttLocked changed during this method's own device/settings awaits.)
                        //
                        // Round-30 finding (nit): this used to silently re-narrow round 29's own widened
                        // baseline back down to pttLockedBeforeKeyDecision alone -- e.g. a prior
                        // TuneAsync(leaveKeyedAfterTune: true) leaving the rig keyed via
                        // _pttLeftKeyedByCall alone (with _pttLocked false) survived the entry baseline
                        // correctly, then got wiped back to false right here, downgrading the eventual
                        // cleanup failure from Critical to Debug. Widened to match the SAME three-flag
                        // belief test the entry baseline uses, keeping pttLockedBeforeKeyDecision's own late/fresh
                        // read for the _pttLocked term specifically (this branch's whole reason to exist)
                        // while no longer silently dropping the other two flags.
                        pttKeyedOnRealRig = _pttCoordinator.BelievesKeyed(pttLockedBeforeKeyDecision);
                        // Round-4 nit: GetType().FullName, not nameof(SstvSessionService), to match the
                        // ObjectName ObjectDisposedException.ThrowIf(_disposed, this) produces elsewhere in
                        // this class -- both throw sites should report the same object identity.
                        throw new ObjectDisposedException(GetType().FullName);
                    }
                }

                if (!pttLockedBeforeKeyDecision)
                {
                    if (rigIsRealAtKeyTime)
                    {
                        // Round-14 finding 1: this await had no bound of its own.
                        // RigctldClientProtocol bounds only its initial connect (_connectTimeout) --
                        // the per-command reply read has none -- so a half-open CAT connection (peer
                        // power-cycled, VPN drop) can block this forever WHILE THE PTT COMMAND HAS
                        // ALREADY REACHED THE WIRE AND PHYSICALLY KEYED THE RIG. Bounded with
                        // WaitAsync rather than a fresh CancellationTokenSource (contrast
                        // UnkeyForCleanupAsync's own pattern, a pure cleanup step with no caller-
                        // cancellation distinction to preserve): a genuine cancellation of `ct` still
                        // completes the awaited task with OperationCanceledException (the benign arm
                        // below), while exceeding the budget with no cancellation surfaces as
                        // TimeoutException instead, correctly routing through the generic catch below
                        // -> abnormalTermination -> urgent un-key-before-StopPlayback. `ct` is passed
                        // to WaitAsync itself too (CA2016 -- not just the inner call): WaitAsync(TimeSpan,
                        // CancellationToken) races completion/timeout/cancellation independently, so a
                        // genuine caller cancellation is observed and classified correctly HERE even if
                        // the backend itself never polls `ct` promptly once its native call has started
                        // (HamlibRadioProtocol's own documented limitation -- round-15 finding 4, closed
                        // by this rather than just caveated).
                        //
                        // Round-15 caveat: that urgent un-key is only a genuine recovery when the backend
                        // doesn't serialize requests behind the very command that just timed out --
                        // RigctldClientProtocol/HamlibRadioProtocol both hold a single request
                        // semaphore across the reply read, so the abandoned key command still holds it
                        // and the un-key attempt is expected to itself time out and log Critical
                        // ("MAY STILL BE KEYED"), not silently recover -- still strictly better than
                        // the pre-round-14 unbounded hang (the operator is now told, loudly, instead
                        // of the app just stalling), but this is a signal to disconnect/reconnect the
                        // CAT link, not evidence the rig is actually off. The underlying call is left
                        // running in the background either way -- same trade-off EnqueueAllAsync's own
                        // round-13 stall fix accepts.
                        Task? keyCommand = null;
                        try
                        {
                            keyCommand = _radioSession.SetPttAsync(true, ct);
                            await keyCommand.WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // Round-15 finding: this call is just as much a lost-update hazard as
                            // SetPttLockAsync's own key-failure catch (round-8 finding, see its own
                            // comment) -- a failed-but-possibly-keyed attempt is a NEW key for
                            // _pttKeyEpoch's purposes. Without this, a concurrent un-keyer that
                            // snapshotted the epoch BEFORE this call reached the wire can read the
                            // epoch as unchanged after this call's own cleanup un-key also fails, and
                            // wrongly clear _pttUnkeyFailedOnRealRig -- reporting a rig this call may
                            // have genuinely keyed as confirmed off. This was the one site rounds 5-8
                            // missed of this class; round 14's WaitAsync fix made it newly reachable in
                            // practice (a hung key command now reliably throws instead of hanging).
                            _pttCoordinator.RecordKeyEpochAdvance();

                            // Round-31 finding (risk): this abandoned key command had no fault-observer
                            // -- the direct twin of SetPttLockAsync's own pttCommand (rounds 29/30), but
                            // strictly more reachable (this method has live production callers,
                            // SetPttLockAsync currently has none) and the most safety-relevant instance
                            // in the whole class: this is the one abandoned command that, by this
                            // method's own reasoning just above, may have physically keyed the rig.
                            // Harm is diagnostic/attribution only -- state handling above is already
                            // correct regardless (epoch bump, then abnormalTermination -> urgent un-key
                            // in the finally). Same gating as every sibling site: only attach if the
                            // command was actually issued (not still null from a synchronous throw) and
                            // is still running (not yet completed -- if it already finished, its own
                            // fault already propagated through the WaitAsync above as the exception this
                            // catch is handling).
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

                        // Round-5 finding: see PttSafetyCoordinator's own doc comment.
                        _pttCoordinator.RecordKeyEpochAdvance();
                        // Round-23 finding (risk): the ONE Log.* call in the whole file that executes
                        // while a real transmitter is physically keyed -- was still unwrapped. A
                        // transient logging-provider failure landing in this exact window (the state
                        // latches above already correctly mark this key attempt, so no state-latching
                        // write is skipped -- just a risk, not a blocker) used to abort the transmit
                        // with the raw logging exception instead of a caller-recognizable type, and
                        // swallow the very PlaybackFailed log that would have explained why.
                        SafeLog(() => Log.PttKeyed(_logger));
                    }
                    else
                    {
                        // Round-23 finding (nit): same shape, no-radio branch -- PTT is never touched
                        // here, so the only harm is a transient logging failure aborting a no-radio
                        // transmit with a bogus exception type.
                        SafeLog(() => Log.PttSkippedNoRadio(_logger));
                    }
                }

                // Round-27 finding (risk): _pttUnkeyEpoch alone answers "did a confirmed un-key happen
                // since I started", not "is the rig off NOW" -- a key landing AFTER that confirmed
                // un-key but BEFORE this call's own cleanup is invisible to unkeyEpochAfterOwnKeyAttempt's own
                // check, unlike _pttKeyEpoch's own consumers, which snapshot immediately before their
                // own un-key await (a one-await-wide window) rather than at method entry (a whole-
                // transmission-wide window, potentially minutes). Concrete leak: this transmit keys ->
                // a concurrent SetPttLockAsync(false) confirms the rig off (_pttUnkeyEpoch bumps) -> a
                // LATER SetPttLockAsync(true) re-keys it (successfully, or fails after physically
                // keying -- either way _pttKeyEpoch bumps again, per round-7/8's own established rule)
                // -> this transmit's own cleanup sees _pttUnkeyEpoch moved and skips its un-key
                // entirely, leaving a genuinely re-keyed rig un-attended. Snapshotted here, right after
                // this call's own key phase (success, failure, or no-radio skip) -- both consumer sites
                // now also require this to be UNCHANGED before trusting the "already off" signal,
                // erring toward attempting the un-key (this file's own established rule) whenever
                // ANYONE has re-keyed since this point, not just when this call's own belief is stale.
                keyEpochAfterOwnKeyAttempt = _pttCoordinator.CurrentKeyEpoch;

                // Round-28 finding (blocker): unkeyEpochAfterOwnKeyAttempt now read HERE too, not at method entry
                // -- see its own comment (near pttLockedBeforeKeyDecision, above) for why the entry-time read was
                // itself the bug. Reading both epochs at this SAME point (immediately after this call's
                // own key phase, no await between the two reads) means the pair now correctly answers
                // "since MY OWN key: did anyone confirm off, and did anyone re-key" -- not two questions
                // anchored to different, unrelated points in time.
                unkeyEpochAfterOwnKeyAttempt = _pttCoordinator.CurrentUnkeyEpoch;

                // Round-14 finding 2: same unbounded-await shape as finding 1, one step later --
                // PTT is already keyed (or was already locked keyed at entry) by this point, so an
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
                // on (see SetPttLockAsync's own post-dispose recovery guard for the same reasoning).
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
                    // device can't delay it at all. skipUnkeyAndRxResume is false by construction whenever
                    // abnormalTermination is true, so this can never fire on a "stay keyed" path.
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

                    // Risk B (Tier A Batch 3 chunk 3a, round 19): _pttLocked read ONCE for this whole
                    // decision, not independently at the skip computation and again at the deferred-
                    // resume branch below -- two independent reads let a SetPttLockAsync(false) landing
                    // between them disagree, taking the "leaveKeyedAfterCall residual" branch on a call
                    // that had actually paused RX, stranding it with nothing left to resume it.
                    //
                    // Round-25 finding (risk): round 19's fix took this ONE read too early -- at the
                    // very top of this finally, BEFORE StopPlaybackWithWatchdogAsync's own await (up to
                    // playbackStopWaitBudget). A SetPttLockAsync(true) that completes DURING that drain
                    // wait -- engaging a genuine operator lock seconds after this transmit's own body
                    // finished -- was invisible to a snapshot already taken before the drain even
                    // started: this call's own un-key below then ran unconditionally and silently
                    // defeated that just-engaged lock, with UnkeyForCleanupAsync's own epoch guard
                    // unable to catch it either (that guard protects against a newer key completing
                    // WHILE the un-key call itself is in flight, not one that already completed before
                    // the un-key call was even entered). Moved here, immediately before this same read's
                    // first actual use -- still exactly ONE read shared by every decision below (round
                    // 19's own property is preserved), just taken as late as StopPlaybackWithWatchdogAsync
                    // allows rather than long before it. Shrinks the race to the residual, instruction-
                    // scale window between this read and the un-key command actually reaching the wire
                    // just below -- the same class of accepted residual this file already documents at
                    // its other epoch-guarded sites, not eliminated outright.
                    var pttLockedAtCleanup = _pttCoordinator.IsPttLocked;

                    // Only a NORMAL completion honors "stay keyed" (leaveKeyedAfterCall/lock) -- see this
                    // method's own doc comment for why an abnormal termination always overrides both.
                    var skipUnkeyAndRxResume = PttSafetyCoordinator.ComputeSkipUnkeyAndRxResume(abnormalTermination, leaveKeyedAfterCall, pttLockedAtCleanup);

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
                    if (!skipUnkeyAndRxResume && (!unkeyAlreadyAttempted || (pttKeyedOnRealRig && !unkeyConfirmed)))
                    {
                        // Round-26 finding (nit, closed as a side effect of finding 1's own
                        // _pttUnkeyEpoch mechanism): round 25 moved pttLockedAtCleanup's own read to
                        // right before this decision, closing a leaked-transmitter risk -- but that also
                        // opened a symmetric window: an operator's own SetPttLockAsync(false) completing
                        // DURING StopPlaybackWithWatchdogAsync's own drain now reads pttLockedAtCleanup
                        // as false too, so this call's cleanup would otherwise attempt a REDUNDANT
                        // un-key on a rig the operator's own unlock already confirmed off. If that
                        // redundant attempt then fails for any unrelated reason (e.g. the CAT link
                        // dropped in the interim), it falsely latches _pttUnkeyFailedOnRealRig on a rig
                        // that is demonstrably fine -- the exact signal erosion round 4's clear exists to
                        // prevent. Skip the attempt entirely when a confirmed un-key already happened
                        // anywhere since this call's own key phase (round-29 nit: corrected from "since
                        // this call's own entry" -- the actual snapshot point, per round 28, is right
                        // after the key phase, not at method entry).
                        //
                        // Round-27 finding (risk): also requires _pttKeyEpoch to be UNCHANGED since
                        // this call's own key phase -- see keyEpochAfterOwnKeyAttempt's own comment for
                        // why the confirmed-un-key signal alone is not enough (a re-key AFTER that
                        // confirmed un-key, still within this call's own lifetime, must never be
                        // skipped over).
                        if (pttKeyedOnRealRig && _pttCoordinator.ConfirmedUnkeyAlreadyHappenedSinceOwnKey(keyEpochAfterOwnKeyAttempt, unkeyEpochAfterOwnKeyAttempt))
                        {
                            // Round-27 finding (nit): no `unkeyConfirmed = true` here -- nothing rechecks
                            // this block's own outer `if` condition afterward, so that write was dead
                            // (this whole block runs at most once per call).
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
                                // Round-20 finding: SafeLog -- see the urgent-half arm's own comment above.
                                SafeLog(() => Log.CleanupStepFailed(_logger, "PTT off (retry)", ex));
                            }
                        }
                    }

                    if (skipUnkeyAndRxResume && leaveKeyedAfterCall && pttKeyedOnRealRig)
                    {
                        // PTT is deliberately left physically keyed after this call returns, with no
                        // _pttLocked to record it (SetPttLockAsync's own doc comment already calls this
                        // gap out) -- DisposeAsync's shutdown backstop needs to know about it.
                        //
                        // Round-7 finding: only ever writes `true` here, never `false` -- this line runs
                        // AFTER StopPlaybackWithWatchdogAsync's own await (up to 5s), the same
                        // snapshot-then-act-on-stale-state shape rounds 5/6 already closed at the two
                        // epoch-guarded sites. Writing `false` unconditionally here (the old behavior, when
                        // pttKeyedOnRealRig happened to be false -- e.g. RigId was "none" at this call's own
                        // key time) could stomp a CONCURRENT call's genuinely-keyed `true`, set moments
                        // earlier while this await was in flight. The `false` direction is never load-
                        // bearing here -- the only correct owner of clearing this flag is a CONFIRMED
                        // un-key, which the two epoch-guarded sites already handle.
                        //
                        // Round-26 finding (risk): that reasoning covered a re-KEY racing this write, but
                        // not a confirmed UN-key racing it -- e.g. TuneAsync(leaveKeyedAfterTune: true)
                        // keys the rig, the operator calls SetPttLockAsync(false) mid-tone and genuinely
                        // un-keys it (rig confirmed OFF, _pttUnkeyEpoch bumped), then this line ran anyway
                        // and wrote a permanent false "still keyed" belief on a rig that is demonstrably
                        // off. Guarded on unkeyEpochAfterOwnKeyAttempt: if a confirmed un-key happened
                        // anywhere since this call's own key phase, skip the write -- the rig is already
                        // known to be off, and writing `true` here would just be wrong, not merely
                        // stale. (Round-29 nit: this comment previously said "since this call started" --
                        // corrected; the actual snapshot point, per round 28, is right after this call's
                        // own key phase, not at method entry/start.)
                        //
                        // Round-27 finding (risk, reported as "covered by luck" today -- whoever re-keys
                        // after a confirmed un-key sets _pttLocked or _pttLeftKeyedByCall itself, so
                        // nothing is currently lost by this write's own skip -- but the same
                        // _pttKeyEpoch cross-check is added here anyway for defense-in-depth, matching
                        // the un-key call site's own fix and not relying on that luck holding under a
                        // future change).
                        var latchResult = _pttCoordinator.TryLatchLeftKeyedAfterCall(keyEpochAfterOwnKeyAttempt, unkeyEpochAfterOwnKeyAttempt);
                        if (latchResult == PttSafetyCoordinator.PttLeftKeyedLatchResult.SkippedConfirmedUnkeyRace)
                        {
                            SafeLog(() => Log.PttLeftKeyedSkippedConfirmedUnkeyRace(_logger));
                        }
                    }

                    // Created only HERE, after every potentially-slow step above, so the RX-resume steps
                    // get a full independent budget rather than whatever StopPlayback/un-key left over --
                    // the same starvation bug blocker 1 is about, one step further down the chain. At most
                    // one StartReceivingAsync call runs per invocation (the three branches below are
                    // mutually exclusive on wasReceiving/skipUnkeyAndRxResume), so one source is enough.
                    //
                    // Round-16 correction (mirrors SetPttLockAsync's own twin CTS's identical fix, see
                    // its own comment): every StartReceivingAsync(rxResumeCts.Token) call below is now
                    // ALSO wrapped in .WaitAsync(rxResumeCts.Token) -- passing the token as the callee's
                    // own `ct` parameter alone never actually bounded this call, since
                    // MiniAudioDeviceEnumerator.RefreshAsync/MiniAudioEngine.StartCaptureAsync only check
                    // `ct` at their own start/lock-acquire boundary, not during the blocking native call
                    // itself. Without this, a wedged capture device leaves this await pending forever,
                    // and since this whole method's outer finally (where _transmitInFlight is finally
                    // released) can't complete while ANY await inside it is still pending, that strands
                    // _transmitInFlight too -- the exact permanent-lockout class rounds 13-15 closed at
                    // every OTHER site in this method, missed here because this CTS looked
                    // already-sufficient.
                    using var rxResumeCts = new CancellationTokenSource(_cleanupTimeout);

                    // Auditor-caught (round 2): a PRIOR locked call may have already stopped capture
                    // and set the coordinator's own deferred-resume flag before THIS call force-unkeyed
                    // on an abnormal termination -- if so, THIS call's own `wasReceiving` is false, so the
                    // `if (wasReceiving)` block below would never see it, leaving RX stopped forever
                    // with IsPttLocked already reporting false. See
                    // PttSafetyCoordinator.DecideStrandedLockResume's own doc comment: guarded on
                    // `!wasReceiving` internally so this never double-fires alongside that block's own
                    // resume for THIS call.
                    if (_pttCoordinator.DecideStrandedLockResume(skipUnkeyAndRxResume, wasReceiving) == PttSafetyCoordinator.PttRxResumeDecision.ResumeThenRaise)
                    {
                        await TryCleanupAsync("Resume RX (stranded lock pending-resume)", () => ResumeReceivingBoundedAsync(rxResumeCts)).ConfigureAwait(false);
                        RaiseCapturePausedForTransmitChanged(false);
                    }

                    if (wasReceiving)
                    {
                        // See PttSafetyCoordinator.DecideWasReceivingResume's own doc comment for the
                        // full 3-way reasoning: ResumeThenRaise (not skipping -- normal resume);
                        // ResumeThenRaiseUnlockRaced (Risk B, publish-then-recheck -- a concurrent
                        // SetPttLockAsync(false) raced this call's own deferred-resume publish, narrows
                        // but does not fully close the window, deliberately unfenced since the failure
                        // mode is a user-recoverable stranded RX pause, not a leaked keyed transmitter --
                        // see the coordinator's own class doc comment); RaiseOnly (Auditor-caught round 1
                        // residual: leaveKeyedAfterCall=true and NOT locked -- RX intentionally stays
                        // stopped, but this transmission's own pause window is still over, so the event
                        // must not stay stuck true forever); DeferPending (published, nothing to do yet).
                        var decision = _pttCoordinator.DecideWasReceivingResume(skipUnkeyAndRxResume, abnormalTermination, pttLockedAtCleanup);
                        switch (decision)
                        {
                            case PttSafetyCoordinator.PttRxResumeDecision.ResumeThenRaise:
                                await TryCleanupAsync("Resume RX", () => ResumeReceivingBoundedAsync(rxResumeCts)).ConfigureAwait(false);
                                // User-reported gap (2026-08-18): fires once the resume attempt has finished,
                                // success or failure -- this event is "no longer paused FOR THIS transmission,"
                                // not a restatement of IsReceiving itself (see the event's own doc comment).
                                RaiseCapturePausedForTransmitChanged(false);
                                break;
                            case PttSafetyCoordinator.PttRxResumeDecision.ResumeThenRaiseUnlockRaced:
                                await TryCleanupAsync("Resume RX (unlock raced cleanup)", () => ResumeReceivingBoundedAsync(rxResumeCts)).ConfigureAwait(false);
                                RaiseCapturePausedForTransmitChanged(false);
                                break;
                            case PttSafetyCoordinator.PttRxResumeDecision.RaiseOnly:
                                RaiseCapturePausedForTransmitChanged(false);
                                break;
                            case PttSafetyCoordinator.PttRxResumeDecision.DeferPending:
                            case PttSafetyCoordinator.PttRxResumeDecision.None:
                            default:
                                break;
                        }
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
