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
    // These three run synchronously on the audio drain thread, inside the same call stack as
    // ISstvDecoder.PushSamples -- _decoderHandler's own try/catch (constructor, above) wraps the
    // PushSamples call itself, but an exception thrown by one of THESE handlers would otherwise be
    // caught there too and mis-logged as "decoder PushSamples threw" instead of attributing it to the
    // actual maintenance handler that failed. Each gets its own try/catch for that reason.
    private void OnDecoderRestartOverdue()
    {
        try
        {
            _maintenanceWarningActive = true;
            // Round-22 finding (nit): same log-before-invoke shape as OnDecoderRestartCriticallyOverdue's
            // own MaintenanceCriticalStop fix above -- a throwing log call used to skip Invoke() below.
            SafeLog(() => Log.MaintenanceWarningRaised(_logger));
            MaintenanceWarningRaised?.Invoke();
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.MaintenanceHandlerFailed(_logger, nameof(OnDecoderRestartOverdue), ex));
        }
    }

    private void OnDecoderRestarted()
    {
        try
        {
            if (_maintenanceWarningActive)
            {
                _maintenanceWarningActive = false;
                // Round-22 finding (nit): same log-before-invoke shape as the other 2 maintenance
                // handlers above -- a throwing log call used to skip Invoke() below.
                SafeLog(() => Log.MaintenanceWarningCleared(_logger));
                MaintenanceWarningCleared?.Invoke();
            }

            // Restart-required-settings backlog item 2 (2026-08-27): unconditional, unlike
            // MaintenanceWarningCleared above -- every swap replaces _inner, whether or not a
            // maintenance warning was ever raised for it, so any UI-layer cache of a decoder-read
            // value (currently only RxImagePaneViewModel.RxBpfPreset) needs this signal every time,
            // not just on the "overdue warning just cleared" subset.
            //
            // T1-4: also unconditionally logged, same reasoning -- a periodic restart with no prior
            // maintenance warning (the common case) used to leave zero trace of it anywhere.
            SafeLog(() => Log.DecoderRestarted(_logger));
            DecoderInstanceReplaced?.Invoke();
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.MaintenanceHandlerFailed(_logger, nameof(OnDecoderRestarted), ex));
        }
    }

    /// <summary>See <see cref="ScanlineStudio.Core.Sstv.ISstvDecoderReconfiguration.ReconfigurationRejected"/>
    /// for the full contract -- restart-required-settings backlog item 2 (2026-08-27). Originally
    /// log-only: <see cref="ISstvSessionService.RxBpfPreset"/>'s live refresh (via
    /// <see cref="DecoderInstanceReplaced"/>) made a rejected reconfiguration visible to the operator
    /// as the Receive tab's Input Chain card silently disagreeing with what Options shows (round-3
    /// plan-review Q3) -- no separate dialog/toast needed. That premise stopped holding once that
    /// same row became a live EDITOR, not a read-only mirror (restart-required-settings backlog item
    /// 6, RX BPF Receive-tab live dropdown, 2026-08-28): a rejected change now needs an explicit
    /// re-sync signal, or the row (and, if the rejected request came from the Receive tab, the
    /// already-written settings.json entry) would permanently show a preset that was requested but
    /// never actually applied. <see cref="ReconfigurationRejected"/> below now carries that signal.</summary>
    private void OnReconfigurationRejected()
    {
        try
        {
            SafeLog(() => Log.ReconfigurationRejected(_logger));
            ReconfigurationRejected?.Invoke();
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.MaintenanceHandlerFailed(_logger, nameof(OnReconfigurationRejected), ex));
        }
    }

    private void OnDecoderRestartCriticallyOverdue()
    {
        try
        {
            // The decoder has ALREADY force-restarted unconditionally by the time this fires (see
            // ISstvDecoderMaintenance's own doc comment) -- this only needs to tear down capture and
            // notify the user, not request another swap. RestartableSstvDecoder raises this event
            // strictly after releasing its own swap lock, so ResetAgc() re-entering it from inside
            // StopReceivingLockedAsync below never contends for anything already held -- that part
            // is confirmed (round-3 plan review).
            //
            // T1-6 (production_audit.md), replaces the Round-15/Tier-B-Area-5 comments this method
            // used to carry (both correct as far as they went, neither resolved the real question --
            // see production_audit.md's own T1-6 note for that history): this fires SYNCHRONOUSLY on
            // the audio capture drain thread (RestartableSstvDecoder's maintenance events fire inline
            // from PushSamples, itself called from IAudioEngine.SamplesCaptured on that thread).
            // Blocking that thread on _rxTransitionGate is the real hazard -- NOT a deadlock. This
            // side is bounded by _cleanupTimeout via StopReceivingAsync's own
            // WaitAsync(_cleanupTimeout, ...). Whichever OTHER caller holds the gate matters too --
            // if it's a Stop-family caller (StopReceivingAsync/StopReceivingLockedAsync, e.g.
            // PlayWithPttAsync's own RX-pause below), IT is independently bounded by
            // StopReceivingLockedAsync's own `stopTask.WaitAsync(_cleanupTimeout)` around
            // MiniAudioEngine.DisposeCaptureSessionAsync's Task.Run(session.Dispose) -> drain-thread
            // Join (that WaitAsync is what actually bounds it, not the Task.Run dispatch itself) --
            // this drain thread's own bound alone is sufficient regardless. A Start-family holder
            // (StartReceivingAsync) is a DIFFERENT story -- its own far side (StartCaptureAsync's
            // native device open) is unbounded (this file's own Area-4 finding) -- but that doesn't
            // change the conclusion here: THIS thread still unblocks on its own _cleanupTimeout wait
            // regardless of how long the other holder's own work takes.
            // The real cost is a bounded ~5s FREEZE of this drain thread: capture keeps producing
            // audio into the native ring with nothing draining it (real RX audio dropped mid-image),
            // and the OTHER _rxTransitionGate holder's own stop likely times out and abandons its
            // still-closing native session (CaptureStopWatchdogFired) once its own bound is reached.
            // Reachable on an ordinary transmission, not just at shutdown -- PlayWithPttAsync's own
            // routine RX-pause-for-TX step (its entry, see that method's own doc comment) acquires
            // this exact gate too, not just DisposeAsync as the superseded comment here claimed.
            //
            // Fix: never let this drain-thread-originated call block waiting for a contended gate.
            // Try a zero-wait, non-blocking acquire first (SemaphoreSlim.Wait(0) never suspends, so
            // it can never itself hop this call off the drain thread or cause a wait).
            if (_rxTransitionGate.Wait(0))
            {
                // Fast path (the overwhelmingly common case: gate free). Calls the PRIVATE
                // already-locked method, not the public StopReceivingAsync -- this thread already
                // holds the gate, and it's a non-reentrant SemaphoreSlim. Genuinely synchronous and
                // safe end to end: StopReceivingLockedAsync has no await before
                // IAudioEngine.StopCaptureAsync(), so MiniAudioEngine.ClaimCaptureSessionAsync's own
                // speculative IsRunningOnDrainThread check still runs on THIS thread. In the normal
                // case it sees ITS OWN session, taking the thread-blocking Wait() branch -- no hop,
                // no self-join (MiniAudioCaptureSession.Dispose()'s own Thread.CurrentThread !=
                // _drainThread guard). It CAN legitimately see a different session or null instead --
                // e.g. MiniAudioEngine.DisposeAsync claims the capture session directly, without ever
                // touching _rxTransitionGate -- in which case ClaimCaptureSessionAsync takes its
                // async _captureLock.WaitAsync() branch instead. Still safe even then: the claim
                // returns null or a FOREIGN session (never one requiring THIS thread's own Join), so
                // no Task.Run(session.Dispose) this call triggers can ever target this thread -- see
                // MiniAudioEngine.ClaimCaptureSessionAsync's own doc comment for why that distinction
                // is load-bearing, not pedantic.
                try
                {
                    StopReceivingLockedAsync().GetAwaiter().GetResult();
                }
                finally
                {
                    _rxTransitionGate.Release();
                }

                NotifyMaintenanceCriticalStop();
            }
            else
            {
                // Contended -- defer instead of freezing this drain thread for up to _cleanupTimeout.
                // Returning immediately here lets DrainLoop exit normally, which is exactly what the
                // OTHER gate holder's own MiniAudioEngine.DisposeCaptureSessionAsync call is (in the
                // non-drain-thread case) blocked waiting for via its Task.Run(session.Dispose) join --
                // so deferring, not blocking, is what lets that other call actually finish and release
                // the gate. This deferred call then proceeds normally off the drain thread once the
                // gate frees. StopReceivingAsync/StopReceivingLockedAsync are already idempotent
                // no-ops if RX has already stopped by the time this runs (including post-DisposeAsync
                // -- neither method checks _disposed, by existing design; see StopReceivingAsync's own
                // doc comment). Known, accepted consequence of deferring: if a legitimate RX
                // start/resume (e.g. PlayWithPttAsync's own post-TX resume) lands in the window before
                // this deferred call actually runs, this stops THAT session too -- arguably still the
                // right outcome for a critical/overdue restart notification, just a genuinely new
                // ordering this synchronous call never had to consider before. Also deliberately NOT
                // guarded against firing MaintenanceCriticalStopRaised after DisposeAsync has already
                // torn this instance down -- parity with today's inline call, which has the identical
                // exposure and was never guarded either; not a new gap this fix introduces.
                SafeLog(() => Log.RxTransitionGateContendedDuringMaintenanceStop(_logger));
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await StopReceivingAsync().ConfigureAwait(false);
                        NotifyMaintenanceCriticalStop();
                    }
                    catch (Exception ex)
                    {
                        SafeLog(() => Log.MaintenanceHandlerFailed(_logger, nameof(OnDecoderRestartCriticallyOverdue), ex));
                    }
                });
            }
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.MaintenanceHandlerFailed(_logger, nameof(OnDecoderRestartCriticallyOverdue), ex));
        }
    }

    // T1-6 (production_audit.md): extracted so both the fast (gate-free) and deferred (gate-contended)
    // paths above notify only AFTER the actual stop attempt completes, matching this method's own
    // pre-existing behavior (notify after the attempt, even if that attempt itself timed out and
    // stopped nothing -- unchanged by this fix, not strengthened). Round-22 finding (nit, still
    // applies): a throwing log call here must not skip MaintenanceCriticalStopRaised below (sequenced
    // after it) -- RX could already be force-stopped with the UI never told why.
    private void NotifyMaintenanceCriticalStop()
    {
        _maintenanceWarningActive = false;
        SafeLog(() => Log.MaintenanceCriticalStop(_logger));
        MaintenanceCriticalStopRaised?.Invoke();
    }
}
