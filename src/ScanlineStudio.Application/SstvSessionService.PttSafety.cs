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
    /// <summary>The un-key half of <see cref="PlayWithPttAsync"/>'s cleanup, extracted so blocker 1's
    /// two call sites (the abnormal-termination early un-key and the normal-completion post-drain one)
    /// share one implementation -- and so each gets its OWN fresh <see cref="CancellationTokenSource"/>.
    /// That is blocker 1's actual fix: the un-key's deadline must never be the leftover of an earlier
    /// cleanup step's spend.
    ///
    /// Round-17 correction: giving this its own fresh CTS closes blocker 1 (a shared deadline), but
    /// passing that CTS's token as <see cref="TryUnkeyPttAsync"/>'s own `ct` parameter did NOT, by
    /// itself, actually bound the un-key call -- see that method's own comment for why (neither
    /// shipped CAT backend polls `ct` once its native/protocol call has started). <c>unkeyCts</c>'s
    /// own timeout is real and still correctly sized; what was missing was an independent
    /// <c>WaitAsync</c> on the awaiting side, now added at the one call site.</summary>
    // T1-5: thin wrapper around PttSafetyCoordinator.BeginConfirmUnkey/CommitConfirmedUnkey -- this
    // method still owns the actual awaited unkey command and every Log.* call; the coordinator owns
    // only the epoch-snapshot/pessimistic-latch/confirmed-clear state transitions (see that class's
    // own doc comment for the full lost-update-race reasoning this decomposition preserves).
    private async Task<bool> UnkeyForCleanupAsync(bool pttKeyedOnRealRig)
    {
        var attempt = _pttCoordinator.BeginConfirmUnkey(pttKeyedOnRealRig);

        using var unkeyCts = new CancellationTokenSource(_cleanupTimeout);
        if (await TryUnkeyPttAsync(pttKeyedOnRealRig, unkeyCts.Token).ConfigureAwait(false))
        {
            var commitResult = _pttCoordinator.CommitConfirmedUnkey(attempt);
            if (commitResult == PttSafetyCoordinator.PttUnkeyCommitResult.Confirmed)
            {
                SafeLog(() => Log.PttReleased(_logger));
            }
            else
            {
                SafeLog(() => Log.PttUnkeyRaceLostToNewerKey(_logger));
            }

            return true;
        }

        if (pttKeyedOnRealRig)
        {
            // Round-3 finding: this is the fourth "keyed at shutdown" state -- see the field's own doc
            // comment for why DisposeAsync's existing three-state check otherwise misses exactly this
            // case. Round-20 finding: the write itself now happens BEFORE the attempt above, not here
            // -- see this method's own comment at the top for why.
            //
            // Risk A (partial fix, Tier A Batch 3 chunk 3a): a Warning is too quiet for "a real
            // transmitter this call keyed may still be on the air." Escalated to Critical, and only
            // for the captured-at-key-time real-rig case, so it can never fire for the benign
            // fresh-install RigId=="none" path that the Warning-suppression logic exists for.
            SafeLog(() => Log.PttStillKeyedAfterFailedUnkey(_logger));
        }

        return false;
    }

    /// <summary>Blocker 1's other half. <see cref="IAudioEngine.StopPlaybackAsync"/> takes no
    /// <see cref="CancellationToken"/> -- it cannot be cancelled, only stopped waiting on. The real
    /// <c>MiniAudioEngine</c> can take ~10.2s worst case on a wedged output device, which is how the
    /// shared-deadline version of this cleanup consumed the entire PTT-off budget before the un-key
    /// was ever attempted. This waits at most <see cref="_playbackStopWaitBudget"/> and then returns,
    /// letting the stop finish in the background -- it is still the same single call (so the engine's
    /// own session teardown is never skipped) and its eventual outcome is still observed and logged
    /// (so it can never surface as an unobserved task exception).
    ///
    /// <b>Known consequence, accepted:</b> on the timeout path this method can return while a
    /// playback stop is still in flight. Round-14 correction: an immediately following transmit does
    /// NOT fail loudly against the still-wedged device -- MiniAudioEngine's own StopPlaybackAsync
    /// un-publishes (nulls) its session field at CLAIM time, synchronously, before the drain/dispose
    /// that can actually block even starts, so <see cref="IAudioEngine.StartPlaybackAsync"/> sees no
    /// session and proceeds to open a SECOND native session against the same wedged device rather
    /// than throwing "already started". That second open is itself now bounded by finding 2's
    /// WaitAsync fix (see the call site in <see cref="PlayWithPttAsync"/>), so it can no longer hang
    /// forever -- but two concurrent native opens against one wedged device is still an
    /// undocumented-behavior risk this class does not otherwise attempt to prevent. Out of this
    /// chunk's scope (the fix, if any, belongs in <c>MiniAudioEngine</c>, not here) -- flagged, not
    /// fixed.</summary>
    private async Task StopPlaybackWithWatchdogAsync()
    {
        Task stopTask;
        try
        {
            stopTask = _audioEngine.StopPlaybackAsync();
        }
        catch (Exception ex)
        {
            // A synchronous throw (e.g. ObjectDisposedException) never produces a Task at all.
            SafeLog(() => Log.CleanupStepFailed(_logger, "StopPlayback", ex));
            return;
        }

        // Round-14 nit: an uncancelled Task.Delay stays queued to the timer wheel until its own full
        // duration elapses even after WhenAny returns on the OTHER branch -- a small, bounded leak per
        // call (once per transmission), closed by cancelling it on the stopTask-wins path below.
        using var watchdogCts = new CancellationTokenSource();
        var completed = await Task.WhenAny(stopTask, Task.Delay(_playbackStopWaitBudget, watchdogCts.Token)).ConfigureAwait(false);
        if (completed == stopTask)
        {
            watchdogCts.Cancel();
            try
            {
                await stopTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Same best-effort contract as TryCleanupAsync -- see its own doc comment.
                SafeLog(() => Log.CleanupStepFailed(_logger, "StopPlayback", ex));
            }

            return;
        }

        SafeLog(() => Log.PlaybackStopWatchdogFired(_logger, _playbackStopWaitBudget));
        _ = stopTask.ContinueWith(
            t => SafeLog(() => Log.CleanupStepFailed(_logger, "StopPlayback (finished after watchdog)", t.Exception!)),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>The cleanup un-key call is deliberately unconditional (see PlayWithPttAsync's own doc
    /// comment) -- but with RigId == "none" that means it throws on every single default-config
    /// transmit, since the null-object backend always throws from SetPttAsync. Routing that through
    /// the generic TryCleanupAsync would log a Warning with a stack trace on every transmit for the
    /// most common configuration (fresh install, no radio set up yet), undercutting that Warning's own
    /// purpose (flagging a genuinely stuck-keyed rig).
    ///
    /// <b>Blocker 2 fix (Tier A Batch 3 chunk 3a) -- this used to re-check
    /// <see cref="IRadioSessionService.RigId"/> AT CATCH TIME, which was unsound.</b>
    /// <c>RadioController.DisconnectAsync</c>/<c>DisposeAsync</c> both reset <c>_rigId</c> to
    /// <c>"none"</c> WITHOUT un-keying PTT, so the sequence [rig genuinely keyed by this transmit] ->
    /// [controller disconnects/disposes mid-cleanup] -> [SetPttAsync(false) throws "No radio
    /// connected"] -> [RigId now reads "none"] reported a physically keyed transmitter as the benign
    /// "nothing to unkey" case and swallowed it. <paramref name="pttKeyedOnRealRig"/> is captured
    /// ONCE, at the moment PTT was keyed, and is the only input to that decision now -- RigId is never
    /// re-read here.</summary>
    private async Task<bool> TryUnkeyPttAsync(bool pttKeyedOnRealRig, CancellationToken ct)
    {
        // Round-17/18/20/26 history (why the command runs on CancellationToken.None while only the
        // WAIT is bounded by `ct`; why a failed logging call itself must not throw; why a late
        // failure on the abandoned command still needs a fault-observer continuation) now lives on
        // PttUnkeyHelper.TryUnkeyBoundedAsync's own doc comment -- T0-1 extracted it there so
        // RadioSessionService's own un-key retry loop can reuse the identical shape. This method's
        // own observable behavior (exceptions, logging, fault-observer semantics) is unchanged by
        // that extraction.
        var result = await PttUnkeyHelper.TryUnkeyBoundedAsync(
            _radioSession.SetPttAsync,
            ct,
            onLateFailure: ex => SafeLog(() => Log.CleanupStepFailed(_logger, "PTT off (finished after watchdog)", ex))).ConfigureAwait(false);

        if (!result.Success)
        {
            // Round-20 finding: this method's own doc/callers assumed it "never throws" -- it did,
            // via these very log calls, when the logging provider itself failed. SafeLog closes that
            // specific hole (see its own doc comment).
            if (pttKeyedOnRealRig)
            {
                SafeLog(() => Log.CleanupStepFailed(_logger, "PTT off", result.Exception!));
            }
            else
            {
                SafeLog(() => Log.PttUnkeySkippedNoRadio(_logger));
            }
        }

        return result.Success;
    }

    /// <summary>Returns whether <paramref name="step"/> actually completed without throwing --
    /// callers that don't care can ignore it.</summary>
    private async Task<bool> TryCleanupAsync(string stepName, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // Best-effort cleanup step -- see PlayWithPttAsync's own doc comment for why a failure
            // here must never mask the original exception or block a sibling cleanup step. Still
            // logged at Warning (not swallowed silently) -- a failed "PTT off" step in particular
            // leaves the rig keyed, a safety-relevant condition a user needs to know about.
            //
            // Round-20 finding: this method's own "never masks the original exception" guarantee
            // (its own doc comment above) depended on this log call itself never throwing -- SafeLog
            // closes that specific hole (a broken logging provider is the realistic source this round
            // treats as in-scope, same as everywhere else this fix was applied this round).
            SafeLog(() => Log.CleanupStepFailed(_logger, stepName, ex));
            return false;
        }
    }

    /// <summary>RX-resume bound for <see cref="PlayWithPttAsync"/>'s cleanup -- Task.Run offloads
    /// StartReceivingAsync's own synchronous settings-read prefix (see
    /// GetStationIdTransmitOptionsAsync's own comment for why WaitAsync alone can't bound that),
    /// WaitAsync(rxResumeCts.Token) bounds the wait itself. On timeout, the abandoned task's eventual
    /// fault is still observed and logged, matching StopPlaybackWithWatchdogAsync/StopReceivingAsync's
    /// own fault-observer continuations.</summary>
    private async Task ResumeReceivingBoundedAsync(CancellationTokenSource rxResumeCts)
    {
        var resumeTask = Task.Run(() => StartReceivingAsync(rxResumeCts.Token), rxResumeCts.Token);
        try
        {
            await resumeTask.WaitAsync(rxResumeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!resumeTask.IsCompleted)
        {
            // Round-20 nit: the round-19 shape attached this observer UNCONDITIONALLY at creation
            // time, which double-logged every ORDINARY (non-timeout) resume failure -- resumeTask
            // faulting for a mundane reason (e.g. no capture device configured) makes this WAIT throw
            // the SAME exception, which the caller (TryCleanupAsync) already logs once as "Resume RX";
            // the unconditional observer then logged it AGAIN as "(finished after watchdog)" once
            // resumeTask's own fault also satisfied OnlyOnFaulted. Gated here on the wait genuinely
            // giving up while resumeTask is STILL running (the actual "abandoned" case this observer
            // exists for) -- if resumeTask is already complete by the time this catch runs, its
            // result/fault already propagated through this same WaitAsync call, so there is nothing
            // left to observe later.
            _ = resumeTask.ContinueWith(
                t => SafeLog(() => Log.CleanupStepFailed(_logger, "Resume RX (finished after watchdog)", t.Exception!)),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>Blocker 3's wait. <c>TxControlsPaneViewModel.Dispose()</c> cancels an in-flight
    /// transmit's token WITHOUT awaiting the transmit task, so on window-close-during-TX the
    /// transmit's own <c>finally</c>/un-key can still be running while DI teardown proceeds. Bounded
    /// (see <see cref="_inFlightKeyedTransmitWait"/>'s sizing against Program.cs's own host-teardown
    /// bound) and never throws: whether the wait succeeds or times out, DisposeAsync's own backstop
    /// un-key runs next either way, and a redundant PTT-off is harmless on every shipped backend (an
    /// un-key of an un-keyed rig is a no-op command).
    ///
    /// <b>Round-6 finding: this wait targets only the most recently published
    /// <c>PttSafetyCoordinator.PendingKeyedTransmit</c>, best-effort</b> -- if two <see cref="PlayWithPttAsync"/>
    /// calls overlap, an older call's own TCS can be overwritten by a newer one's publish (see
    /// that class's own doc comment), so this method has nothing to await for
    /// that older call even though it may still be genuinely keyed. Not fixed here -- DisposeAsync's own
    /// caller reads the coordinator's own in-flight count (not this field's nullness) for its "is anything
    /// still in flight" decision, so the fallback backstop un-key below still fires correctly even when
    /// this wait has nothing to observe; only the WAIT itself (letting the older call finish on its own
    /// terms first) is best-effort, not the safety guarantee.</summary>
    private async Task AwaitInFlightKeyedTransmitAsync()
    {
        var pending = _pttCoordinator.PendingKeyedTransmit;
        if (pending is null)
        {
            return;
        }

        SafeLog(() => Log.WaitingForKeyedTransmitAtShutdown(_logger));
        // Round-14 nit: same uncancelled-Task.Delay leak as StopPlaybackWithWatchdogAsync's own --
        // this runs once per DisposeAsync, so bounded, but closed the same way for consistency.
        using var waitCts = new CancellationTokenSource();
        var completed = await Task.WhenAny(pending.Task, Task.Delay(_inFlightKeyedTransmitWait, waitCts.Token)).ConfigureAwait(false);
        if (completed == pending.Task)
        {
            waitCts.Cancel();
        }
        else
        {
            SafeLog(() => Log.KeyedTransmitCleanupWaitTimedOut(_logger, _inFlightKeyedTransmitWait));
        }
    }
}
