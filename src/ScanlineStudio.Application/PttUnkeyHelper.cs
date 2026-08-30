namespace ScanlineStudio.Application;

/// <summary>T0-1: the bounded-wait/unbounded-command/fault-observer shape
/// <see cref="SstvSessionService"/>'s own <c>TryUnkeyPttAsync</c> already used (rounds 17/18/20/26),
/// extracted so <see cref="RadioSessionService"/>'s own un-key retry loop can reuse the identical
/// shape rather than relying on an unbounded <c>await protocol.SetPttAsync(...)</c> whose only real
/// bound is whatever the specific <c>IRadioProtocol</c> backend happens to enforce internally. This
/// is now the one place that history lives -- <c>TryUnkeyPttAsync</c>'s own doc comment just points
/// here.
///
/// <b>Not a general PTT-safety extraction</b> -- this holds ONLY the un-key retry's own
/// wait/command/observer shape, nothing about <c>PlayWithPttAsync</c>'s wider state machine (12
/// tracked fields, two self-documented narrowed-not-closed races). That's a separate, much larger
/// audit item (T1-5), gated on its own plan-review round and the
/// <c>SstvSessionServicePttSafetyTests</c> suite -- do not grow this class to absorb any of it.
///
/// <b>Shape</b>: the un-key COMMAND always runs on <see cref="CancellationToken.None"/>, never the
/// caller's own <paramref name="waitCt"/> -- round-18's finding was that passing the caller's token
/// to the command itself (not just the wait) cancelled the un-key AT THE BACKEND'S OWN REQUEST GATE
/// once the budget expired, so it never reached the rig at all; decoupled here so it instead stays
/// queued and eventually reaches the rig once whatever's blocking it clears. The WAIT for that
/// command is bounded by <paramref name="waitCt"/>, which each caller constructs independently. If
/// the wait gives up before the command completes, <paramref name="onLateFailure"/> (if supplied) is
/// attached as a fault-only continuation (round-26) so an eventual failure on the abandoned command
/// is still observed, never an unobserved task exception. An eventual LATE SUCCESS is not reported
/// to either caller, by design, matching every other abandoned-task site in
/// <see cref="SstvSessionService"/>.</summary>
internal static class PttUnkeyHelper
{
    /// <param name="Success">Whether the un-key command completed successfully within the wait.</param>
    /// <param name="Exception">Non-null exactly when <see cref="Success"/> is <see langword="false"/>
    /// -- either a synchronous throw from the un-key call itself, the command's own eventual fault
    /// (observed via the wait), or the wait's own timeout/cancellation.</param>
    public readonly record struct Result(bool Success, Exception? Exception);

    public static async Task<Result> TryUnkeyBoundedAsync(
        Func<bool, CancellationToken, Task> setPtt,
        CancellationToken waitCt,
        Action<Exception>? onLateFailure = null)
    {
        Task unkeyTask;
        try
        {
            // A synchronous throw (e.g. the null-object NoneRadioProtocol, or a backend's own
            // ObjectDisposedException.ThrowIf) never produces a Task at all -- caught here, not by
            // the WaitAsync below.
            unkeyTask = setPtt(false, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return new Result(false, ex);
        }

        try
        {
            await unkeyTask.WaitAsync(waitCt).ConfigureAwait(false);
            return new Result(true, null);
        }
        catch (Exception ex)
        {
            // Gated on the task genuinely still being abandoned -- if it's already complete by the
            // time this catch runs, its own fault already propagated through the WaitAsync above as
            // `ex`, so there's nothing left to observe later.
            if (onLateFailure is not null && !unkeyTask.IsCompleted)
            {
                _ = unkeyTask.ContinueWith(
                    t => onLateFailure(t.Exception!),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return new Result(false, ex);
        }
    }
}
