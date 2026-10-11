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
    public async Task StartReceivingAsync(CancellationToken ct = default)
    {
        // Round-3 finding: without this, PlayWithPttAsync's own "Resume RX" cleanup step could restart
        // capture (subscribing _decoderHandler/_waterfallHandler to a live engine, calling
        // _decoder.ResetAgc()) AFTER DisposeAsync had already disposed the waterfall/decoder --
        // reachable when AwaitInFlightKeyedTransmitAsync's bounded wait expires while a transmit's own
        // cleanup is still inside its resume-RX step. Every AWAITED caller of this method routes
        // through TryCleanupAsync or its own try/catch, so this degrades to a logged Warning there
        // rather than propagating unhandled. Round-19 correction: that guarantee does NOT cover an
        // ABANDONED call -- round 18's own Task.Run wrapping means a timed-out RX-resume keeps running
        // as untracked background work with no caller left to catch anything, which is exactly why this
        // check is no longer sufficient alone; see the SECOND recheck further down this method, just
        // before the publish that actually matters.
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Tier B audit finding (Area 4): bounded like StopReceivingAsync's own gate wait, not just
        // cancellable by the caller's own `ct` -- without this, an ABANDONED RX-resume
        // (ResumeReceivingBoundedAsync's own rxResumeCts already expired, so ITS OWN token is no
        // longer any help) can hold this gate forever if its own StartReceivingLockedAsync call is
        // still genuinely stuck inside StartCaptureAsync (round-16's own established "a native call
        // does not actually respect `ct` mid-flight" fact) -- every LATER caller of this method
        // (including the direct UI Start-RX path, which passes CancellationToken.None) would then
        // hang indefinitely trying to acquire an effectively-permanently-held semaphore, with no log,
        // no throw, just a stuck Receiving toggle. Same _cleanupTimeout budget, same reasoning as
        // StopReceivingAsync's own bound -- see that method's own comment.
        if (!await _rxTransitionGate.WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false))
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "StartReceivingAsync (rxTransitionGate wait timed out)", new TimeoutException()));
            throw new TimeoutException("Timed out waiting to start receiving -- a concurrent RX transition did not finish in time.");
        }

        try
        {
            await StartReceivingLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _rxTransitionGate.Release();
        }
    }

    private async Task StartReceivingLockedAsync(CancellationToken ct)
    {
        if (_isReceiving)
        {
            return;
        }

        // Code-review finding (round 2, elegant-wondering-hinton.md): _autoDetectPaused is
        // deliberately NOT reset here -- a DELIBERATE DEVIATION from verified legacy behavior, not a
        // legacy-fidelity claim (an earlier version of this comment wrongly claimed the latter; see
        // that finding for the correction). Legacy's own pause flag (pDem->m_SyncMode = -1,
        // Main.cpp:6042-6060) does NOT survive a transmit: TMmsstv::ToTX (Main.cpp:7360) calls
        // pDem->Stop() (guarded only by the TXLoopBack option, off by default -- Main.cpp:7358),
        // which sets m_SyncMode = 512 (sstv.cpp:1786) -- not a parking
        // state, but the entry to a 0.5s self-clearing wait (case 512/513, sstv.cpp:2243-2252) that
        // lands on 0 (unpaused) regardless of what the flag was before Stop(). So in legacy, pausing
        // then transmitting silently un-pauses ~0.5s after RX audio resumes, and legacy's own UI
        // honestly reflects that (SBAuto->Down re-derives from the flag, Main.cpp:5988). This port
        // chooses STICKY pause instead -- surviving TX and any Stop/Start RX cycle, changed only by
        // the user's own toggle (or QuickSelectMode's own explicit resume-on-force, see that
        // method's comment) -- because this is session/UX state, not DSP/protocol math (out of this
        // project's legacy-fidelity scope), and legacy's drop-after-TX reads as an artifact of
        // Stop()'s shared teardown path rather than an intentional design choice; an operator who
        // explicitly paused auto-detect should not have it silently resume just because they keyed
        // up. A fresh SstvSessionService instance still starts unpaused, via _autoDetectPaused's own
        // `false` field default -- no explicit reset needed for that case.
        var device = await ResolveDeviceAsync(forCapture: true, ct).ConfigureAwait(false);
        var settings = await LoadAudioSettingsAsync(ct).ConfigureAwait(false);

        // CW-ID/FSK station-ID subsystem Phase 4 code-review finding: this was documented (Phase 3's
        // AnalogFmSstvDecoder.StationIdDecodeEnabled/NarrowFskHeaderDecoder.StationIdDecodeEnabled
        // doc comments) as "Phase 4 wires this to the live user setting" but nothing ever did --
        // m_fskdecode's port-equivalent field (StationIdSettings.FskIdRxEnabled) was a dead setting.
        // Re-applied on every StartReceivingAsync call (not just once at DI-construction time, unlike
        // the other decoder toggles below the audio-capture start) since ISstvDecoder.StationIdDecodeEnabled
        // is deliberately live-settable -- see that property's own doc comment for why.
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var stationIdSettings = appSettings.GetSection(StationIdSettings.SectionKey, StationIdSettingsJsonContext.Default.StationIdSettings)
            ?? new StationIdSettings();
        _decoder.StationIdDecodeEnabled = stationIdSettings.FskIdRxEnabled ?? StationIdSettings.DefaultFskIdRxEnabled;

        // fsk_cwid.md §8.2: same "re-read the persisted section, cache into a field" pattern as
        // FskIdRxEnabled immediately above -- see SstvSessionService.CwId.cs's own _cwIdRxEnabled
        // field doc comment for why this mirrors that setting's shape rather than SetAutoSaveAudioEnabled's.
        _cwIdRxEnabled = stationIdSettings.CwIdRxEnabled ?? StationIdSettings.DefaultCwIdRxEnabled;
        _cwIdRxWindowSeconds = stationIdSettings.CwIdRxWindowSeconds ?? StationIdSettings.DefaultCwIdRxWindowSeconds;

        await _audioEngine.StartCaptureAsync(
            device, _decoder.SampleRate, settings.CaptureThreadPriority,
            settings.PeriodSizeInFrames, settings.Periods, settings.CaptureChannelSource, ct).ConfigureAwait(false);

        // Round-19 finding: rechecked HERE, immediately before publishing -- the entry check above is
        // no longer sufficient on its own now that RX-resume attempts run as abandoned Task.Run
        // background work on timeout (round 18's own Task.Run wrapping). An abandoned resume can pass
        // the entry check, then keep running through the awaits above while DisposeAsync proceeds and
        // disposes the decoder/waterfall/engine -- reaching this point afterward would subscribe
        // handlers to a disposed engine and call ResetAgc() on a disposed decoder. Throwing here is
        // caught by this method's own callers exactly like the entry check already is.
        //
        // Round-21 re-raised finding: throwing here alone used to leak the native capture session
        // StartCaptureAsync just opened above -- _isReceiving is still false at this point (it only
        // flips true below), so a later StopReceivingAsync call unconditionally early-returns via its
        // own `if (!_isReceiving) return;` guard, and this session is never closed. Rounds 18/19 made
        // an abandoned/timed-out RX-resume the NORMAL way to reach this branch (not an exotic race),
        // so this is a real session/device/thread leak at shutdown, not just a theoretical one.
        // Best-effort close the session we just opened before throwing -- swallow any failure from
        // that close, since ObjectDisposedException is already about to propagate and is the
        // operative signal to whatever caller/fault-observer is left; nothing here re-touches
        // _isReceiving, since it is correctly still false either way.
        if (_disposed)
        {
            try
            {
                await _audioEngine.StopCaptureAsync().WaitAsync(_cleanupTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SafeLog(() => Log.CleanupStepFailed(_logger, "StopCapture (abandoned resume, disposed mid-flight)", ex));
            }

            throw new ObjectDisposedException(GetType().FullName);
        }

        // Tier B audit finding (Area 4): same shape as the _disposed recheck just above, for a
        // DIFFERENT abandoned-resume hazard that recheck doesn't cover -- an ABANDONED RX-resume
        // (ResumeReceivingBoundedAsync's own rxResumeCts already expired, per that method's own
        // comment; round-16's own established "a native call does not actually respect `ct`
        // mid-flight" fact means StartCaptureAsync above can still be genuinely running well past
        // that point) reaching HERE would publish `_isReceiving = true` regardless of what happened
        // in the meantime -- including a NEWER PlayWithPttAsync call that is by then actively
        // transmitting, turning capture ON mid-keyed-TX even though that method's own entry-time
        // `wasReceiving` read correctly saw `false` and skipped pausing RX for it. `ct.IsCancellationRequested`
        // is a reliable signal this specific call IS exactly that abandoned resume:
        // ResumeReceivingBoundedAsync passes rxResumeCts.Token all the way through as this method's
        // own `ct`, and no other caller of this method ever cancels its own token (the direct UI
        // Start-RX path passes CancellationToken.None). Same close-the-just-opened-session-then-throw
        // shape as the _disposed recheck above.
        if (ct.IsCancellationRequested)
        {
            try
            {
                await _audioEngine.StopCaptureAsync().WaitAsync(_cleanupTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SafeLog(() => Log.CleanupStepFailed(_logger, "StopCapture (abandoned resume, caller gave up waiting)", ex));
            }

            throw new OperationCanceledException(
                "Abandoned RX-resume: the caller gave up waiting before StartCaptureAsync finished -- not publishing IsReceiving.", ct);
        }

        // Reset before subscribing: capture is running, but its callbacks must not enter
        // PushSamples while ResetAgc mutates the decoder's level-tracking state.
        // A reset failure still leaves real capture available; preserve swallow-and-log behavior.
        try
        {
            _decoder.ResetAgc();
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "ResetAgc (RX start)", ex));
        }

        _audioEngine.SamplesCaptured += _decoderHandler;
        _audioEngine.SamplesCaptured += _waterfallHandler;
        _audioEngine.SamplesCaptured += _levelMeterHandler;
        _audioEngine.SamplesCaptured += _audioAutoSaveHandler;
        _audioEngine.SamplesCaptured += _cwIdHandler;
        _isReceiving = true;
        // Configurations-preset backlog, Phase 1 (2026-08-28): latched here, at the exact point
        // capture is confirmed genuinely open -- see this field's own doc comment for why it isn't
        // seeded at construction or read back from settings.
        _activeCaptureDeviceId = device.Id;

        // Round-22 finding (nit): round 21's own ResetAgc() fix above justified itself partly as "no
        // longer skips Log.RxStarted below" -- but this call itself was still unwrapped, so a throwing
        // provider still propagated out of this method after capture had genuinely started (swallowed
        // on the RX-resume path via TryCleanupAsync, but not on the direct UI Start-RX path).
        SafeLog(() => Log.RxStarted(_logger, device.Id, _decoder.SampleRate));
    }

    public async Task StopReceivingAsync()
    {
        // Bounded, not indefinite or uncancellable-and-unbounded -- DisposeAsync (this method's other
        // production caller alongside PlayWithPttAsync's entry and, since T1-6, the deferred
        // Task.Run OnDecoderRestartCriticallyOverdue schedules on gate contention) has its own
        // established contract of best-effort, never-hanging teardown (every other cleanup-path wait
        // in this file uses this same _cleanupTimeout budget). Without a bound, a concurrent StartReceivingAsync still
        // resolving its own device/settings (itself unbounded -- a separate, already-tracked risk-tier
        // finding) would make DisposeAsync's own call here hang for as long as that resolution takes.
        // Safe to just give up and return on timeout: by the time DisposeAsync calls this, _disposed
        // is already true, so the racing StartReceivingAsync's own recheck-then-throw unwinds and
        // closes the session it opened on its own -- this method has nothing left to do in that case
        // either way. A genuine (non-disposal) concurrent Start finishing slowly degrades the same way:
        // logged and skipped rather than silently no-op'd, which is still strictly better than the
        // race this gate exists to close (see _rxTransitionGate's own doc comment).
        if (!await _rxTransitionGate.WaitAsync(_cleanupTimeout, CancellationToken.None).ConfigureAwait(false))
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "StopReceivingAsync (rxTransitionGate wait timed out)", new TimeoutException()));
            return;
        }

        try
        {
            await StopReceivingLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _rxTransitionGate.Release();
        }
    }

    private async Task StopReceivingLockedAsync()
    {
        if (!_isReceiving)
        {
            return;
        }

        _audioEngine.SamplesCaptured -= _decoderHandler;
        _audioEngine.SamplesCaptured -= _waterfallHandler;
        _audioEngine.SamplesCaptured -= _levelMeterHandler;
        _audioEngine.SamplesCaptured -= _audioAutoSaveHandler;
        _audioEngine.SamplesCaptured -= _cwIdHandler;
        // RawInputPeakLevel's own contract: 0.0 whenever capture isn't running, never a stale
        // reading left over from before this stop -- no more buffers will arrive to overwrite it.
        _rawInputPeakLevel = 0f;
        try
        {
            // Round-16 finding: IAudioEngine.StopCaptureAsync takes no CancellationToken at all, and
            // its drain-thread join is unbounded by design (MiniAudioCaptureSession's own doc comment
            // -- only a managed SamplesCaptured subscriber that never returns can make it hang, but
            // the handlers are already detached two lines up, so that specific cause can't apply to
            // THIS call; a genuinely wedged native close is still possible). Mirrors
            // StopPlaybackWithWatchdogAsync's own shape exactly -- bounded with WaitAsync on the TASK
            // itself, not a token (there is none to bound), swallow-and-log on either a real failure
            // or a timeout, never propagate. WaitAsync specifically, not Task.WhenAny+Task.Delay: on
            // an ALREADY-COMPLETED task (the common case -- every ordinary uncontended stop, and the
            // synchronous drain-thread-inline path OnDecoderRestartCriticallyOverdue's own
            // GetAwaiter().GetResult() call relies on) WaitAsync returns the same task directly with
            // no hop, so that path stays genuinely synchronous and zero-allocation exactly as before;
            // WhenAny would force a state-machine yield even when nothing is actually pending, moving
            // ResetAgc()/the log line/MaintenanceCriticalStopRaised off the drain thread on EVERY
            // call, not just a hung one -- a real regression the WhenAny shape would have introduced.
            //
            // Round-17 correction: for that SAME drain-thread caller, the watchdog below bounds
            // NOTHING -- MiniAudioEngine's own claim-then-dispose sequence runs entirely inline and
            // synchronously on that thread (ClaimCaptureSessionAsync's thread-blocking Wait() branch,
            // then session.Dispose() called directly, not via Task.Run), so stopTask is already
            // completed by the time it reaches WaitAsync and the 5s budget is never actually
            // consulted. The watchdog only does real work for a NON-drain-thread caller (PlayWithPttAsync's
            // entry, DisposeAsync, and, since T1-6, the deferred Task.Run
            // OnDecoderRestartCriticallyOverdue itself schedules on _rxTransitionGate contention --
            // that deferred call runs on a genuine pool thread, not the drain thread it was
            // originally invoked from).
            Task stopTask;
            try
            {
                stopTask = _audioEngine.StopCaptureAsync();
            }
            catch (Exception ex)
            {
                // A synchronous throw (e.g. ObjectDisposedException) never produces a Task at all --
                // same shape as StopPlaybackWithWatchdogAsync's own sync-throw arm. Round-17 nit:
                // no early `return` here (unlike that method) -- this one still has ResetAgc()/
                // Log.RxStopped() below the finally, and skipping them left the legacy-parity TX<->RX
                // AGC reset (ultracode finding #6) un-run and no "RX stopped" line ever logged on this
                // specific path, even though _isReceiving still correctly flips false via the finally
                // either way.
                SafeLog(() => Log.CleanupStepFailed(_logger, "StopCapture", ex));
                stopTask = Task.CompletedTask;
            }

            try
            {
                await stopTask.WaitAsync(_cleanupTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The handlers are already detached above, so "not receiving" (set in the finally
                // below regardless of this branch) is the only state consistent with reality whether
                // or not the underlying stop call ever actually finishes. The abandoned stopTask is
                // left running in the background -- observe its eventual fault so it doesn't surface
                // as an unobserved task exception at GC time, detached from this call's own context.
                //
                // Round-17 nit: the capture-side twin of StopPlaybackWithWatchdogAsync's own documented
                // "known consequence" (see that method's own doc comment) -- a following
                // StartReceivingAsync can now open a SECOND native session while this abandoned one is
                // still closing. Verified benign here (unlike that residual concurrent-open risk on
                // the playback side, which is flagged, not fixed): MiniAudioEngine's own claim step
                // nulls _captureSession AND unsubscribes SamplesAvailable together, before release, so
                // the abandoned session cannot interleave audio into the new one.
                SafeLog(() => Log.CaptureStopWatchdogFired(_logger, _cleanupTimeout));
                _ = stopTask.ContinueWith(
                    t => SafeLog(() => Log.CleanupStepFailed(_logger, "StopCapture (finished after watchdog)", t.Exception!)),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                SafeLog(() => Log.CleanupStepFailed(_logger, "StopCapture", ex));
            }
        }
        finally
        {
            // Round-12 finding: moved into a finally -- previously, if StopCaptureAsync threw,
            // _isReceiving stayed true with the handlers already detached above. Every subsequent
            // StartReceivingAsync call would then silently early-return (its own `if (_isReceiving)
            // return;` guard, believing capture was already running), leaving RX invisibly dead for
            // the rest of the process with no way to recover via this API at all. The handlers are
            // already unsubscribed either way, so "not receiving" is the only state consistent with
            // reality regardless of whether the underlying stop call itself succeeded.
            _isReceiving = false;
        }

        // ultracode audit finding #6: the RX-halting (entering-TX) side of the same transition.
        //
        // Round-21 finding (risk 6): previously unguarded -- a throwing ResetAgc() here propagated
        // out of StopReceivingAsync even though _isReceiving was already correctly latched false by
        // the finally above, and skipped Log.RxStopped below. This is also the direct fix for the
        // "risk 6b" call site in PlayWithPttAsync's entry (a bare `await StopReceivingAsync()`, inside
        // its own single-flight-guarded try but before PTT is ever touched) -- with ResetAgc() no
        // longer able to throw out of this method, that call site needs no separate guard of its own:
        // capture is genuinely stopped either way, no PTT/radio state is touched yet, and the
        // single-flight guard's own finally still releases normally.
        try
        {
            _decoder.ResetAgc();
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "ResetAgc (RX stop)", ex));
        }

        // ui_transition_plan.md step 12 (Auto-save RX audio): deliberately placed HERE, after
        // StopCaptureAsync has been joined/timed-out above -- NOT right after the SamplesCaptured
        // unsubscribes. Auditor-caught (round 2 code-review): `-=` on a multicast delegate cannot
        // affect an invocation already in progress (MiniAudioEngine's own documented snapshot
        // semantics), so a straggler `_audioAutoSaveHandler` call can still be running after the
        // unsubscribe line above. Resetting the ring/arm before that straggler completes let it
        // re-seed the pre-roll ring sized against the OLD (about-to-change) sample rate, silently
        // undersizing it for the rest of the session. Placing this after the stop is actually
        // awaited closes that window -- this ONE hook, gated by the same "only on a real transition"
        // guard as the unsubscribes above, covers every AudioCaptureReset seam the plan doc calls for
        // (sample-rate change, capture-device change, TX pause/resume, file-decode entry) --
        // RequestSampleRateAsync/RequestCaptureDeviceAsync/PlayWithPttAsync/DecodeFromFileAsync all
        // funnel through this method before doing their own respective mutation, so a single call
        // site here is sufficient; no need to duplicate it at each of those 4 call sites separately.
        HandleAudioCaptureReset();

        SafeLog(() => Log.RxStopped(_logger));
    }
}
