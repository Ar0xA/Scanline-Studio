namespace ScanlineStudio.Application.Tests;

/// <summary>
/// T1-5 (production_audit.md): isolated unit tests for <see cref="PttSafetyCoordinator"/> itself --
/// the direct benefit the extraction argues for. Race 1 and the RX-resume "raced" recheck (Risk B's
/// own deterministic half) are now testable via ordinary sequential calls, with no thread races
/// needed to prove the STATE logic correct; only the end-to-end regression gate
/// (<c>SstvSessionServicePttSafetyTests.cs</c>) still needs real interleaving, and only for behavior
/// this class doesn't own (I/O ordering, logging).
/// </summary>
public class PttSafetyCoordinatorTests
{
    [Fact]
    public void CommitConfirmedUnkey_NoConcurrentKey_ClearsAndConfirms()
    {
        var coordinator = new PttSafetyCoordinator();
        coordinator.CommitLockCommandSucceeded(locked: true, epochAtUnkeyStart: 0);

        var attempt = coordinator.BeginConfirmUnkey(pttKeyedOnRealRig: true);
        var result = coordinator.CommitConfirmedUnkey(attempt);

        Assert.Equal(PttSafetyCoordinator.PttUnkeyCommitResult.Confirmed, result);
        Assert.False(coordinator.IsPttLocked);
    }

    [Fact]
    public void CommitConfirmedUnkey_NewerKeyLandedDuringAttempt_SkipsClearsAndReportsRaceLost()
    {
        // Race 1 (this class's own doc comment): an unkey attempt's post-success clears must not wipe
        // a genuinely newer key's own "still keyed" state if that key completed while the unkey
        // command was in flight. Directly constructible now via ordinary sequential calls -- no thread
        // race needed to prove this specific state transition correct.
        var coordinator = new PttSafetyCoordinator();
        coordinator.CommitLockCommandSucceeded(locked: true, epochAtUnkeyStart: 0);

        var attempt = coordinator.BeginConfirmUnkey(pttKeyedOnRealRig: true);

        // Simulate a newer key completing while this unkey attempt's own command is "in flight"
        // (between BeginConfirmUnkey's own epoch snapshot and CommitConfirmedUnkey being called):
        // a confirmed unkey followed by a re-key, both landing after the snapshot.
        var confirmAttempt = coordinator.BeginConfirmUnkey(pttKeyedOnRealRig: true);
        coordinator.CommitConfirmedUnkey(confirmAttempt);
        coordinator.CommitLockCommandSucceeded(locked: true, epochAtUnkeyStart: 0);

        var result = coordinator.CommitConfirmedUnkey(attempt);

        Assert.Equal(PttSafetyCoordinator.PttUnkeyCommitResult.RaceLostToNewerKey, result);
        Assert.True(coordinator.IsPttLocked); // the re-key's own state must survive, not be wiped
    }

    [Fact]
    public void BeginConfirmUnkey_NotKeyedOnRealRig_DoesNotLatchFailureFlag()
    {
        // Round-2 plan-review blocker: an earlier draft set _pttUnkeyFailedOnRealRig unconditionally,
        // which would permanently latch a false "still keyed" belief on the benign no-radio path,
        // where the whole unkey attempt never reaches a real backend at all.
        var coordinator = new PttSafetyCoordinator();

        coordinator.BeginConfirmUnkey(pttKeyedOnRealRig: false);

        Assert.False(coordinator.BelievesKeyed(pttLockedValue: false));
    }

    [Fact]
    public void ReleaseKeyedSlot_NeverPublished_IsANoOp()
    {
        // Round-2 plan-review blocker: an earlier draft decremented the count unconditionally, which
        // would drive it to -1 permanently on the common no-radio path (nothing was ever published).
        var coordinator = new PttSafetyCoordinator();
        var attempt = new PttSafetyCoordinator.PttKeyAttempt();

        coordinator.ReleaseKeyedSlot(attempt);

        Assert.True(coordinator.HasSingleInFlightKeyedTransmit == false);
        // A genuine publish afterward must still read as a clean, single in-flight registration -- a
        // negative count from the no-op above would make this read false too.
        var realAttempt = new PttSafetyCoordinator.PttKeyAttempt();
        coordinator.PublishKeyedSlot(realAttempt);
        Assert.True(coordinator.HasSingleInFlightKeyedTransmit);
    }

    [Fact]
    public void PublishKeyedSlot_CompletionResumesContinuationsAsynchronously_NotInline()
    {
        // Round-2 plan-review blocker: an earlier draft dropped TaskCreationOptions.
        // RunContinuationsAsynchronously -- without it, ReleaseKeyedSlot's own TrySetResult can resume
        // a waiter (DisposeAsync's own bounded wait) INLINE on the releasing thread, before that
        // thread has released whatever lock it's about to release next (SetPttLockAsync's own
        // _pttLockGate) -- a self-deadlock/reentrancy risk.
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

    [Fact]
    public void DecideWasReceivingResume_ConcurrentUnlockAlreadyLanded_ConsumesImmediately()
    {
        // Risk B's own deterministic half (this class's own doc comment): a caller's own
        // pttLockedAtCleanup snapshot can be stale by the time this decision runs, if a concurrent
        // unlock landed in between -- the internal recheck must catch that and resume immediately
        // rather than stranding a pending flag nothing will ever consume.
        var coordinator = new PttSafetyCoordinator();
        coordinator.CommitLockCommandSucceeded(locked: true, epochAtUnkeyStart: 0);
        coordinator.CommitLockCommandSucceeded(locked: false, epochAtUnkeyStart: coordinator.CurrentKeyEpoch);

        var decision = coordinator.DecideWasReceivingResume(skipUnkeyAndRxResume: true, abnormalTermination: false, pttLockedAtCleanup: true);

        Assert.Equal(PttSafetyCoordinator.PttRxResumeDecision.ResumeThenRaiseUnlockRaced, decision);
    }

    [Fact]
    public void DecideWasReceivingResume_StillLocked_DefersPending()
    {
        var coordinator = new PttSafetyCoordinator();
        coordinator.CommitLockCommandSucceeded(locked: true, epochAtUnkeyStart: 0);

        var decision = coordinator.DecideWasReceivingResume(skipUnkeyAndRxResume: true, abnormalTermination: false, pttLockedAtCleanup: true);

        Assert.Equal(PttSafetyCoordinator.PttRxResumeDecision.DeferPending, decision);
        Assert.True(coordinator.TryConsumeRxResumePending()); // the flag really was published, for a later unlock to find
    }

    [Fact]
    public void DecideWasReceivingResume_NotSkipping_ResumesUnconditionally()
    {
        var coordinator = new PttSafetyCoordinator();

        var decision = coordinator.DecideWasReceivingResume(skipUnkeyAndRxResume: false, abnormalTermination: false, pttLockedAtCleanup: false);

        Assert.Equal(PttSafetyCoordinator.PttRxResumeDecision.ResumeThenRaise, decision);
    }

    [Fact]
    public void DecideWasReceivingResume_LeaveKeyedResidual_RaisesOnlyWithNoResume()
    {
        var coordinator = new PttSafetyCoordinator();

        var decision = coordinator.DecideWasReceivingResume(skipUnkeyAndRxResume: true, abnormalTermination: false, pttLockedAtCleanup: false);

        Assert.Equal(PttSafetyCoordinator.PttRxResumeDecision.RaiseOnly, decision);
        Assert.False(coordinator.TryConsumeRxResumePending()); // nothing was published -- no resume owed to anyone
    }

    [Theory]
    [InlineData(200)]
    public void DecideWasReceivingResume_RacingTryConsumeRxResumePending_NeverThrows(int iterations)
    {
        // Risk B's own genuine multi-threaded window (this class's own doc comment: a plain, unfenced
        // volatile read-then-write on both sides, deliberately not Interlocked). Not asserting a strong
        // quiescent invariant here -- the documented accepted outcome is a harmless DUPLICATE resume,
        // which a naive "never both consume" assertion would flag as a false failure. What must hold
        // unconditionally: no exception from either side, and the flag is always independently
        // recoverable afterward (never corrupted into some third state).
        for (var i = 0; i < iterations; i++)
        {
            var coordinator = new PttSafetyCoordinator();
            coordinator.CommitLockCommandSucceeded(locked: true, epochAtUnkeyStart: 0);

            using var barrier = new Barrier(2);
            Exception? exceptionA = null;
            Exception? exceptionB = null;

            var threadA = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    coordinator.DecideWasReceivingResume(skipUnkeyAndRxResume: true, abnormalTermination: false, pttLockedAtCleanup: true);
                }
                catch (Exception ex)
                {
                    exceptionA = ex;
                }
            });
            var threadB = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    coordinator.TryConsumeRxResumePending();
                }
                catch (Exception ex)
                {
                    exceptionB = ex;
                }
            });

            threadA.Start();
            threadB.Start();
            threadA.Join();
            threadB.Join();

            Assert.Null(exceptionA);
            Assert.Null(exceptionB);

            // Settles either way -- true (still pending, a real unlock will find it) or false (already
            // consumed by one side or the other) are both valid terminal states; TryConsumeRxResumePending
            // must not throw or behave inconsistently regardless of which state it lands in.
            coordinator.TryConsumeRxResumePending();
        }
    }
}
