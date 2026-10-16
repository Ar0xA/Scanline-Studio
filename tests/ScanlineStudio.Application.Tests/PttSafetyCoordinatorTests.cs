namespace ScanlineStudio.Application.Tests;

/// <summary>
/// Isolated unit tests for <see cref="PttSafetyCoordinator"/> itself. Race 1 is testable via ordinary
/// sequential calls, with no thread races needed to prove the STATE logic correct; only the end-to-end regression gate
/// (<c>SstvSessionServicePttSafetyTests.cs</c>) still needs real interleaving, and only for behavior
/// this class doesn't own (I/O ordering, logging).
/// </summary>
public class PttSafetyCoordinatorTests
{
    [Fact]
    public void CommitConfirmedUnkey_NoConcurrentKey_ClearsAndConfirms()
    {
        var coordinator = new PttSafetyCoordinator();
        coordinator.RecordKeyEpochAdvance();

        var attempt = coordinator.BeginConfirmUnkey(pttKeyedOnRealRig: true);
        Assert.True(coordinator.BelievesKeyed()); // pessimistic pre-set while the command is in flight
        var result = coordinator.CommitConfirmedUnkey(attempt);

        Assert.Equal(PttSafetyCoordinator.PttUnkeyCommitResult.Confirmed, result);
        Assert.False(coordinator.BelievesKeyed());
        Assert.Equal(1, coordinator.CurrentUnkeyEpoch);
    }

    [Fact]
    public void CommitConfirmedUnkey_NewerKeyLandedDuringAttempt_SkipsClearsAndReportsRaceLost()
    {
        // Race 1 (this class's own doc comment): an unkey attempt's post-success clears must not wipe
        // a genuinely newer key's own "still keyed" state if that key completed while the unkey
        // command was in flight. Directly constructible now via ordinary sequential calls -- no thread
        // race needed to prove this specific state transition correct.
        var coordinator = new PttSafetyCoordinator();
        coordinator.RecordKeyEpochAdvance();

        var attempt = coordinator.BeginConfirmUnkey(pttKeyedOnRealRig: true);

        // Simulate a newer key completing while this unkey attempt's own command is "in flight"
        // (between BeginConfirmUnkey's own epoch snapshot and CommitConfirmedUnkey being called):
        // a confirmed unkey, then a re-key whose own unkey fails (latching "may still be keyed"),
        // all landing after the snapshot.
        var confirmAttempt = coordinator.BeginConfirmUnkey(pttKeyedOnRealRig: true);
        coordinator.CommitConfirmedUnkey(confirmAttempt);
        coordinator.RecordKeyEpochAdvance();
        coordinator.BeginConfirmUnkey(pttKeyedOnRealRig: true);

        var result = coordinator.CommitConfirmedUnkey(attempt);

        Assert.Equal(PttSafetyCoordinator.PttUnkeyCommitResult.RaceLostToNewerKey, result);
        Assert.True(coordinator.BelievesKeyed()); // the re-key's own state must survive, not be wiped
    }

    [Fact]
    public void BeginConfirmUnkey_NotKeyedOnRealRig_DoesNotLatchFailureFlag()
    {
        // Round-2 plan-review blocker: an earlier draft set _pttUnkeyFailedOnRealRig unconditionally,
        // which would permanently latch a false "still keyed" belief on the benign no-radio path,
        // where the whole unkey attempt never reaches a real backend at all.
        var coordinator = new PttSafetyCoordinator();

        coordinator.BeginConfirmUnkey(pttKeyedOnRealRig: false);

        Assert.False(coordinator.BelievesKeyed());
    }

    [Fact]
    public void ReleaseKeyedSlot_NeverPublished_IsANoOp()
    {
        // Round-2 plan-review blocker: an earlier draft decremented the count unconditionally, which
        // would drive it to -1 permanently on the common no-radio path (nothing was ever published).
        var coordinator = new PttSafetyCoordinator();
        var attempt = new PttSafetyCoordinator.PttKeyAttempt();

        coordinator.ReleaseKeyedSlot(attempt);

        Assert.False(coordinator.BelievesKeyedOrInFlight());
        // A genuine publish afterward must still read as in flight -- a -1 count from the no-op above
        // would leave the count at 0 here and blind DisposeAsync's shutdown backstop.
        var realAttempt = new PttSafetyCoordinator.PttKeyAttempt();
        coordinator.PublishKeyedSlot(realAttempt);
        Assert.True(coordinator.BelievesKeyedOrInFlight());
    }

    [Fact]
    public void PublishKeyedSlot_CompletionResumesContinuationsAsynchronously_NotInline()
    {
        // Round-2 plan-review blocker: an earlier draft dropped TaskCreationOptions.
        // RunContinuationsAsynchronously -- without it, ReleaseKeyedSlot's own TrySetResult would
        // resume DisposeAsync's bounded wait INLINE on the transmit's cleanup thread, running the whole
        // DI teardown there before that thread has released _transmitInFlight.
        //
        // Code-review finding: releasing from THIS test's own thread would flake -- TrySetResult queues
        // the continuation onto the releasing thread's own thread-pool local queue, and a later `await`
        // on that same thread can dequeue and run it right there, coincidentally matching thread ids
        // even with the option correctly present. Releasing from a dedicated, non-pool Thread instead
        // makes the assertion exact in both directions: a genuine thread-pool continuation can never
        // report that specific thread's id.
        var coordinator = new PttSafetyCoordinator();
        var attempt = new PttSafetyCoordinator.PttKeyAttempt();
        coordinator.PublishKeyedSlot(attempt);

        var continuationThreadId = -1;
        var continuationRan = new ManualResetEventSlim();
        attempt.KeyedCompletion!.Task.ContinueWith(
            _ =>
            {
                continuationThreadId = Environment.CurrentManagedThreadId;
                continuationRan.Set();
            },
            TaskContinuationOptions.ExecuteSynchronously);

        var releasingThread = new Thread(() => coordinator.ReleaseKeyedSlot(attempt));
        releasingThread.Start();
        releasingThread.Join();

        Assert.True(continuationRan.Wait(TimeSpan.FromSeconds(5)));
        Assert.NotEqual(releasingThread.ManagedThreadId, continuationThreadId);
    }
}
