namespace ScanlineStudio.Application;

/// <summary>
/// T1-5 (production_audit.md): the PTT (push-to-talk) keying/un-keying state machine, extracted from
/// <see cref="SstvSessionService"/>'s <c>PlayWithPttAsync</c>/<c>SetPttLockAsync</c>/<c>DisposeAsync</c>.
/// Every method here is deliberately SYNCHRONOUS, no I/O, no logging, no awaiting -- three rounds of
/// plan-review settled on this shape specifically: <see cref="SstvSessionService"/> keeps every
/// <c>await</c> and every <c>Log.*</c> call, this class owns only the field reads/writes that decide
/// what state to record. That split makes the two documented lost-update races directly unit-testable
/// via ordinary sequential calls, with no thread races needed to test the STATE logic itself (only a
/// true end-to-end integration test needs real concurrency) -- and it removes an entire class of
/// self-deadlock risk a callback-shaped coordinator would have introduced (this class never calls back
/// into the service while any lock is held, because it never awaits or calls back at all).
///
/// This extraction does NOT shrink <c>PlayWithPttAsync</c> meaningfully -- the value is state-machine
/// testability and correctness isolation, not line count. The step ORDERING that ~30 rounds of
/// plan-review/code-review hardened into that method stays exactly where it was; only the field
/// mutations themselves moved.
///
/// <b>Field ownership.</b> This class owns every field that is purely about "what does this session
/// currently believe about PTT/keying state." It deliberately does NOT own <c>_transmitInFlight</c>
/// (a whole-method single-flight guard that also cross-checks unrelated file-decode/loopback-self-test
/// state), <c>_disposed</c> (a whole-service concern -- every method below that needs it takes it as a
/// parameter at the exact call site that needs it, never a stored delegate, so there is exactly one
/// source of truth), or <c>_pttLockGate</c> (serializes <see cref="SstvSessionService.SetPttLockAsync"/>
/// against itself -- an orchestration concern that belongs with the awaiting, not the state).
///
/// <b>Two lost-update races, both narrowed not fully closed (deliberate, documented, not silently
/// left implicit):</b>
/// <list type="bullet">
/// <item><b>Race 1</b> (the key/unkey epoch fields): an unkey's post-success clears run on the
/// continuation after the actual unkey command succeeds -- but a CONCURRENT, NEWER key command can
/// complete in that same window and record its own "still keyed" state, and the stale continuation's
/// clears would wipe it out. <see cref="BeginConfirmUnkey"/>/<see cref="CommitConfirmedUnkey"/> and
/// <see cref="CommitLockCommandSucceeded"/>'s own unlock branch close this to instruction-scale (an
/// epoch snapshotted before the unkey attempt, rechecked before clearing) -- a keyer whose ENTIRE
/// publish (bump + flag write) lands exactly between an un-keyer's epoch recheck and its own clears is
/// still wiped. Closing this fully would need a lock across a few synchronous field accesses on both
/// sides; not added -- the surviving window is a few CPU instructions wide against a
/// multi-millisecond-to-multi-second pre-fix window (a real device I/O round-trip), and this file has
/// zero production callers of the one method (<see cref="SstvSessionService.SetPttLockAsync"/>) that
/// could even race it today.</item>
/// <item><b>Risk B</b> (<see cref="TryConsumeRxResumePending"/> vs. <see cref="DecideWasReceivingResume"/>'s
/// own publish): a locked transmit defers its own RX-resume by publishing a pending flag for a LATER
/// unlock to consume; a concurrent unlock's own consumption check can complete in the narrow window
/// between the publish and the transmit's own post-publish recheck, missing what was just published.
/// Deliberately left as a plain, unfenced volatile read-then-write (not <c>Interlocked</c>) on BOTH
/// sides -- its failure mode is a stranded RX pause (user-recoverable, lower severity) rather than a
/// leaked keyed transmitter, and the residual interleaving beyond the narrowed window is a harmless
/// DUPLICATE resume far more often than a strand (both sides test the flag before consuming it) --
/// changing either side to <c>Interlocked.Exchange</c> would change this accepted trade, not just
/// harden it.</item>
/// </list>
///
/// <b>Two genuinely different policies for the same field across call paths</b> (confirmed by direct
/// re-read of both, not assumed from shape-similarity -- this is exactly the kind of subtlety a
/// glossed-over "port" would get wrong): <see cref="CommitConfirmedUnkey"/>'s own <c>_pttLocked</c>
/// clear is epoch-guarded (skipped if a newer key raced it); <see cref="CommitLockCommandSucceeded"/>'s
/// own unlock-direction <c>_pttLocked</c> clear is UNCONDITIONAL, never epoch-guarded -- a direct
/// operator unlock command force-releases the lock belief regardless of any race, by design.
/// </summary>
internal sealed class PttSafetyCoordinator
{
    // volatile: read/written from whatever thread calls PlayWithPttAsync/SetPttLockAsync, no lock
    // between them -- matches the exact field-modifier fidelity of the pre-extraction fields this
    // class replaces (round-3 plan-review finding: a "tidy up while moving" pass here would be a
    // silent correctness change -- Interlocked.Increment on a volatile field is CS0420, so the epoch/
    // count/completion fields below must stay non-volatile).
    private volatile bool _pttLocked;
    private volatile bool _rxPendingResumeAfterUnlock;
    private volatile bool _pttLeftKeyedByCall;
    private volatile bool _pttUnkeyFailedOnRealRig;

    // Deliberately NOT volatile: Interlocked.Increment/CompareExchange/Exchange on these is CS0420 if
    // they were. Read via Volatile.Read/Interlocked at every access instead, same visibility guarantee.
    private TaskCompletionSource? _keyedTransmitCompletion;
    private int _pttKeyEpoch;
    private int _pttUnkeyEpoch;
    private int _keyedTransmitCount;

    /// <summary>Operator-engaged PTT lock is currently held. Mirrors <c>SstvSessionService.IsPttLocked</c>.</summary>
    public bool IsPttLocked => _pttLocked;

    /// <summary>True iff exactly one call currently holds a published keyed-transmit registration --
    /// the round-12 guard for a SAFE immediate recovery unkey after a failed lock-engage (nothing else
    /// has a registration in flight to endanger). Deliberately count-based, not nullness-based (see
    /// <see cref="PublishKeyedSlot"/>'s own doc comment for why nullness alone goes blind once two
    /// calls can overlap).</summary>
    public bool HasSingleInFlightKeyedTransmit => Volatile.Read(ref _keyedTransmitCount) == 1;

    /// <summary>The most recently published in-flight keyed-transmit completion, or null if nothing is
    /// currently keyed. Best-effort: if two calls overlap, an older call's own TCS can be overwritten
    /// by a newer call's publish (see <see cref="_keyedTransmitCount"/>'s own role in
    /// <see cref="BelievesKeyedOrInFlight"/> for why the caller's shutdown backstop does not rely on
    /// this alone).</summary>
    public TaskCompletionSource? PendingKeyedTransmit => Volatile.Read(ref _keyedTransmitCompletion);

    /// <summary>A fresh snapshot of the key epoch, for a caller that needs to capture it BEFORE
    /// issuing its own key/unkey command (e.g. <c>epochAtUnkeyStart</c>, or the post-key-phase
    /// <c>keyEpochAfterOwnKeyAttempt</c> snapshot) and pass it back into a later coordinator call
    /// (<see cref="CommitLockCommandSucceeded"/>, <see cref="ConfirmedUnkeyAlreadyHappenedSinceOwnKey"/>,
    /// <see cref="TryLatchLeftKeyedAfterCall"/>) once its own command has resolved. The snapshot timing
    /// itself is the caller's own responsibility -- this class has no way to know when a caller's
    /// command is "about to start."</summary>
    public int CurrentKeyEpoch => Volatile.Read(ref _pttKeyEpoch);

    /// <summary>Same shape as <see cref="CurrentKeyEpoch"/>, for the unkey-direction epoch.</summary>
    public int CurrentUnkeyEpoch => Volatile.Read(ref _pttUnkeyEpoch);

    /// <summary>Mutable, per-call bookkeeping created fresh by each <c>PlayWithPttAsync</c>/
    /// <c>SetPttLockAsync</c> call and passed BY REFERENCE (a class, not a struct -- must actually
    /// mutate in place) to every coordinator method that call needs. Deliberately minimal: earlier
    /// drafts of this extraction tried to also carry a mutable "did this call believe it was
    /// physically keying a real rig" flag here, but every one of that flag's 3 mutation sites in the
    /// real source is service-side logic (a snapshot-and-branch policy, not state) -- so it stays a
    /// plain local in the caller, fed by <see cref="BelievesKeyed"/>, never coordinator-owned.</summary>
    public sealed class PttKeyAttempt
    {
        public TaskCompletionSource? KeyedCompletion { get; set; }
    }

    public readonly record struct PttUnkeyAttempt(int EpochAtStart);

    public enum PttLockCommitResult { LockEngaged, UnlockConfirmed, UnlockRaceLostToNewerKey }

    public enum PttUnkeyCommitResult { Confirmed, RaceLostToNewerKey }

    public enum PttLeftKeyedLatchResult { Latched, SkippedConfirmedUnkeyRace }

    /// <summary>The RX-resume outcome a cleanup decision produced, bearing the caller's FULL
    /// obligation (resume attempt, which log-worthy cleanup-step name to use for it if any, and
    /// whether to raise the capture-paused-for-transmit event) -- not just "did a resume happen."
    /// Baking event/step-name responsibility into the enum (rather than a bare bool) removes a real
    /// drift risk two rounds of plan-review both independently flagged: a caller-side "when do I still
    /// need to raise the event" side condition, computed separately from this result, is exactly the
    /// kind of ordering coupling that has caused real bugs elsewhere in this class's own history.</summary>
    public enum PttRxResumeDecision
    {
        /// <summary>No resume, no event -- either nothing was pending, or (for the wasReceiving branch)
        /// the call is intentionally leaving RX stopped with a pending resume owed later.</summary>
        None,

        /// <summary>Resume RX now (step name "Resume RX"), then raise the event false regardless of
        /// whether the resume attempt itself succeeded.</summary>
        ResumeThenRaise,

        /// <summary>Resume RX now (step name "Resume RX (unlock raced cleanup)" -- a DIFFERENT
        /// forensic log signal than <see cref="ResumeThenRaise"/>, since this specific branch means a
        /// concurrent unlock raced this call's own deferred-resume publish), then raise the event
        /// false regardless of whether the resume attempt itself succeeded.</summary>
        ResumeThenRaiseUnlockRaced,

        /// <summary>No resume attempted; raise the event false anyway (RX intentionally stays stopped
        /// -- the <c>leaveKeyedAfterCall</c>, not-locked residual case -- but this call's own pause
        /// window is still over).</summary>
        RaiseOnly,

        /// <summary>Published <see cref="_rxPendingResumeAfterUnlock"/> for a later unlock to consume;
        /// no resume, no event now.</summary>
        DeferPending,
    }

    /// <summary>Publishes a fresh keyed-transmit registration -- shared by <c>PlayWithPttAsync</c> and
    /// <c>SetPttLockAsync</c>, both only when the caller has already determined a real rig is about to
    /// be keyed. <c>TaskCreationOptions.RunContinuationsAsynchronously</c> is load-bearing, not a
    /// tuning knob: without it, <see cref="ReleaseKeyedSlot"/>'s own <c>TrySetResult</c> can resume a
    /// waiter (<c>DisposeAsync</c>'s own bounded wait) INLINE on the releasing thread, before that
    /// thread has released whatever lock it's about to release next (<c>SetPttLockAsync</c>'s own
    /// <c>_pttLockGate</c>) -- a self-deadlock/reentrancy risk this project's own established
    /// convention (every other <c>TaskCompletionSource</c> in this codebase already uses this option)
    /// exists to prevent.
    ///
    /// <see cref="Interlocked.Exchange(ref TaskCompletionSource?, TaskCompletionSource?)"/> here is a
    /// FULL FENCE, not merely a thread-safe write -- it closes a genuine StoreLoad-reordering gap
    /// against <c>DisposeAsync</c>'s own <c>_disposed = true; Interlocked.MemoryBarrier();</c> pairing.
    /// The caller MUST immediately recheck <c>_disposed</c> after this call returns, before issuing the
    /// actual key command -- that pairing is what makes "this call publishes and DisposeAsync's own
    /// wait sees it" and "DisposeAsync runs first and this call sees disposed" mutually exclusive
    /// instead of both silently missing each other.</summary>
    public void PublishKeyedSlot(PttKeyAttempt attempt)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        attempt.KeyedCompletion = tcs;
        Interlocked.Exchange(ref _keyedTransmitCompletion, tcs);
        Interlocked.Increment(ref _keyedTransmitCount);
    }

    /// <summary>The 3-flag "does this session believe PTT is/was physically keyed" test, used at every
    /// site EXCEPT <c>DisposeAsync</c>'s own 4-term shutdown check (see
    /// <see cref="BelievesKeyedOrInFlight"/>). <paramref name="pttLockedValue"/> is the substitutable
    /// term, not a hidden internal read: one real call site deliberately passes a SNAPSHOT taken
    /// earlier in the caller's own method (not a fresh read) to answer a genuinely different question
    /// ("what did this call believe about the lock at ITS OWN key-time," not "what is true right now")
    /// -- collapsing that distinction into an internal fresh read would silently re-break the fix that
    /// distinction exists for.</summary>
    public bool BelievesKeyed(bool pttLockedValue) =>
        pttLockedValue || _pttLeftKeyedByCall || _pttUnkeyFailedOnRealRig;

    /// <summary><c>DisposeAsync</c>'s own 4-term shutdown check -- a DIFFERENT formula from
    /// <see cref="BelievesKeyed"/>, not a variant of it: adds the in-flight-count term (a keyed
    /// transmit currently in progress, whose own completion hasn't published any of the other 3 flags
    /// yet), always a fresh <see cref="_pttLocked"/> read (this call site has no earlier-snapshot
    /// concern to preserve).</summary>
    public bool BelievesKeyedOrInFlight() =>
        _pttLocked || _pttLeftKeyedByCall || Volatile.Read(ref _keyedTransmitCount) > 0 || _pttUnkeyFailedOnRealRig;

    /// <summary>Records that a key attempt happened -- success or failure, the epoch bump is identical
    /// either way (this is genuinely one primitive operation, confirmed against all 4 real call sites,
    /// not a case of conflating different formulas the way <see cref="TryLatchLeftKeyedAfterCall"/>'s
    /// 3 sites would have been). See <see cref="_pttKeyEpoch"/>'s own field doc (this class's own
    /// summary) for the lost-update race this closes.</summary>
    public void RecordKeyEpochAdvance() => Interlocked.Increment(ref _pttKeyEpoch);

    /// <summary><c>SetPttLockAsync</c>'s own engage-direction command-failure handling: bumps the key
    /// epoch (a failed-but-possibly-keyed attempt is just as much a "new key" as a confirmed one, for
    /// Race 1's own purposes) and latches <see cref="_pttLeftKeyedByCall"/> (the rig may be physically
    /// keyed with no other record of it). Caller gates the call itself on "a command was actually
    /// issued" (a synchronous throw before dispatch means nothing was ever sent) -- that gate is
    /// service-local (depends on a local `Task?` reference), not coordinator state.</summary>
    public void RecordLockEngageFailed()
    {
        RecordKeyEpochAdvance();
        _pttLeftKeyedByCall = true;
    }

    /// <summary><c>SetPttLockAsync</c>'s own disengage-direction command-failure handling. No epoch
    /// bump here (this call never successfully keyed anything -- <see cref="_pttLocked"/> is untouched
    /// and correctly remains true, so no lost-update race is possible on this path). Caller gates the
    /// call itself on <see cref="BelievesKeyed"/> (an unlock of a rig this session never believed was
    /// keyed at all must not latch a false alarm).</summary>
    public void RecordUnlockFailed() => _pttUnkeyFailedOnRealRig = true;

    /// <summary><c>SetPttLockAsync</c>'s own post-command-success handling, BOTH directions in one
    /// method (matching the real source, which is one straight-line block, not two call sites). The
    /// engage direction bumps the epoch BEFORE writing <c>_pttLocked = true</c> (closes a narrow
    /// ordering gap a concurrent un-keyer could otherwise observe). The disengage direction writes
    /// <c>_pttLocked = false</c> UNCONDITIONALLY -- see this class's own summary for why that is a
    /// deliberately different policy from <see cref="CommitConfirmedUnkey"/>'s own epoch-guarded clear
    /// of the same field -- then only clears the other two belief flags and bumps the unkey epoch if
    /// nothing re-keyed since <paramref name="epochAtUnkeyStart"/> was snapshotted (Race 1's own
    /// guard on this path).
    ///
    /// Caller must apply <see cref="Interlocked.MemoryBarrier"/> itself, AFTER this call returns and
    /// BEFORE its own subsequent <c>_disposed</c> read -- that fence pairs with <c>DisposeAsync</c>'s
    /// own barrier and must not move inside this method (round-3 plan-review finding: pulling it in
    /// here would break that pairing, since the fence needs to sit after the CALLER's own load of
    /// whatever this method wrote, not merely after this method's own internal writes).</summary>
    public PttLockCommitResult CommitLockCommandSucceeded(bool locked, int epochAtUnkeyStart)
    {
        if (locked)
        {
            RecordKeyEpochAdvance();
            _pttLocked = true;
            return PttLockCommitResult.LockEngaged;
        }

        _pttLocked = false;

        if (Volatile.Read(ref _pttKeyEpoch) == epochAtUnkeyStart)
        {
            _pttLeftKeyedByCall = false;
            _pttUnkeyFailedOnRealRig = false;
            Interlocked.Increment(ref _pttUnkeyEpoch);
            return PttLockCommitResult.UnlockConfirmed;
        }

        return PttLockCommitResult.UnlockRaceLostToNewerKey;
    }

    /// <summary>First half of a confirmed-unkey attempt (the un-key command itself is awaited by the
    /// caller, between this call and <see cref="CommitConfirmedUnkey"/> -- this class has no I/O of its
    /// own). Epoch snapshot happens FIRST, the pessimistic pre-set of
    /// <see cref="_pttUnkeyFailedOnRealRig"/> SECOND -- reading the epoch any later would make a
    /// concurrent key more likely to be folded into this snapshot, widening Race 1's own window rather
    /// than closing it (round-2 plan-review finding: an earlier draft had this order reversed). The
    /// pre-set is gated on <paramref name="pttKeyedOnRealRig"/> -- setting it unconditionally would
    /// permanently latch a false "still keyed" belief on the benign no-radio path, where the whole
    /// unkey attempt never reaches a real backend at all (round-2 plan-review blocker: an earlier draft
    /// set this unconditionally).</summary>
    public PttUnkeyAttempt BeginConfirmUnkey(bool pttKeyedOnRealRig)
    {
        var epochAtStart = Volatile.Read(ref _pttKeyEpoch);
        if (pttKeyedOnRealRig)
        {
            _pttUnkeyFailedOnRealRig = true;
        }

        return new PttUnkeyAttempt(epochAtStart);
    }

    /// <summary>Second half: called only after the caller's own unkey command has succeeded. Clears
    /// every belief flag (force-release: whether or not a lock was engaged, PTT is now confirmed
    /// physically off) and bumps the unkey epoch, but ONLY if nothing re-keyed since
    /// <see cref="BeginConfirmUnkey"/>'s own snapshot (Race 1's own guard) -- the command still
    /// genuinely succeeded against the backend either way, so the caller always treats this as a
    /// successful unkey; the returned result only decides which of two logs to emit.</summary>
    public PttUnkeyCommitResult CommitConfirmedUnkey(PttUnkeyAttempt attempt)
    {
        if (Volatile.Read(ref _pttKeyEpoch) == attempt.EpochAtStart)
        {
            _pttLocked = false;
            _pttLeftKeyedByCall = false;
            _pttUnkeyFailedOnRealRig = false;
            Interlocked.Increment(ref _pttUnkeyEpoch);
            return PttUnkeyCommitResult.Confirmed;
        }

        return PttUnkeyCommitResult.RaceLostToNewerKey;
    }

    /// <summary>Read-only predicate: has a confirmed unkey happened anywhere since THIS call's own key
    /// phase (not since method entry -- see the epoch parameters' own callers for why that distinction
    /// matters), such that a retry-unkey attempt would be redundant. Caller retains its own
    /// <c>pttKeyedOnRealRig &amp;&amp;</c> prefix around this call -- deliberately not folded in here,
    /// since <paramref name="pttKeyedOnRealRig"/> is a plain service-local, not coordinator state.</summary>
    public bool ConfirmedUnkeyAlreadyHappenedSinceOwnKey(int keyEpochAfterOwnKeyAttempt, int unkeyEpochAfterOwnKeyAttempt) =>
        Volatile.Read(ref _pttUnkeyEpoch) != unkeyEpochAfterOwnKeyAttempt
        && Volatile.Read(ref _pttKeyEpoch) == keyEpochAfterOwnKeyAttempt;

    /// <summary>The cleanup-time "this call is intentionally leaving PTT physically keyed" latch
    /// (<c>TuneAsync(leaveKeyedAfterTune: true)</c>'s own path, with no <see cref="_pttLocked"/> to
    /// record it). Guarded on the SAME two epochs as <see cref="ConfirmedUnkeyAlreadyHappenedSinceOwnKey"/>
    /// but with the opposite polarity of use: only writes `true` if NEITHER epoch shows a confirmed
    /// unkey specifically covering this call's own key phase, since writing `true` after a confirmed
    /// unkey would be flatly wrong (not merely stale) about a rig demonstrably off. Caller retains its
    /// own outer gate (<c>skipUnkeyAndRxResume &amp;&amp; leaveKeyedAfterCall &amp;&amp;
    /// pttKeyedOnRealRig</c>) -- all three are service-local/already-computed values, not coordinator
    /// state.</summary>
    public PttLeftKeyedLatchResult TryLatchLeftKeyedAfterCall(int keyEpochAfterOwnKeyAttempt, int unkeyEpochAfterOwnKeyAttempt)
    {
        if (Volatile.Read(ref _pttUnkeyEpoch) == unkeyEpochAfterOwnKeyAttempt
            || Volatile.Read(ref _pttKeyEpoch) != keyEpochAfterOwnKeyAttempt)
        {
            _pttLeftKeyedByCall = true;
            return PttLeftKeyedLatchResult.Latched;
        }

        return PttLeftKeyedLatchResult.SkippedConfirmedUnkeyRace;
    }

    /// <summary>Releases a call's own keyed-transmit registration -- a no-op if this call never
    /// published one (the common no-radio path), matching the real source's own
    /// <c>if (keyedCompletion is not null) { ... }</c> wrapping at both call sites exactly: an
    /// unconditional decrement here would drive <see cref="_keyedTransmitCount"/> to -1 permanently on
    /// that path, blinding <see cref="BelievesKeyedOrInFlight"/>'s own shutdown check and
    /// <see cref="HasSingleInFlightKeyedTransmit"/>'s own recovery guard for the rest of the process's
    /// life (round-2 plan-review blocker: an earlier draft only null-guarded the final
    /// <c>TrySetResult</c> call, not the count decrement). <c>CompareExchange</c>, not a plain null
    /// write: only clears the shared field if it still points at THIS call's own instance, so an
    /// overlapping call's own still-live registration is never erased.</summary>
    public void ReleaseKeyedSlot(PttKeyAttempt attempt)
    {
        if (attempt.KeyedCompletion is null)
        {
            return;
        }

        Interlocked.CompareExchange(ref _keyedTransmitCompletion, null, attempt.KeyedCompletion);
        Interlocked.Decrement(ref _keyedTransmitCount);
        attempt.KeyedCompletion.TrySetResult();
    }

    /// <summary>The real source's own single computed-once-reused-four-times local -- exposed as one
    /// pure function so the caller can compute it exactly once per cleanup and thread the SAME value
    /// through every decision that needs it (the retry-unkey gate, the left-keyed latch gate, and both
    /// RX-resume decisions below), rather than passing 3 independent booleans whose combination could
    /// represent a state that can't actually occur.</summary>
    public static bool ComputeSkipUnkeyAndRxResume(bool abnormalTermination, bool leaveKeyedAfterCall, bool pttLockedAtCleanup) =>
        !abnormalTermination && (leaveKeyedAfterCall || pttLockedAtCleanup);

    /// <summary>The "stranded lock pending-resume" check -- independent of <paramref name="wasReceiving"/>
    /// (fires even when this specific call was never itself receiving, if an EARLIER call's own deferred
    /// resume is still pending and this call is not itself skipping its own resume).</summary>
    public PttRxResumeDecision DecideStrandedLockResume(bool skipUnkeyAndRxResume, bool wasReceiving)
    {
        if (!skipUnkeyAndRxResume && !wasReceiving && _rxPendingResumeAfterUnlock)
        {
            _rxPendingResumeAfterUnlock = false;
            return PttRxResumeDecision.ResumeThenRaise;
        }

        return PttRxResumeDecision.None;
    }

    /// <summary>The 3-way RX-resume decision for a call that WAS itself receiving -- caller only
    /// invokes this when <c>wasReceiving</c> is true. Case A: normal resume (not skipping). Case B:
    /// skipping specifically because the lock is engaged (not because of <c>leaveKeyedAfterCall</c>) --
    /// publishes the deferred-resume flag, then immediately rechecks it (Risk B's own narrow-not-close
    /// mitigation, see this class's own summary) in case a concurrent unlock already raced this exact
    /// publish. Case C (the `else`): skipping specifically because of <c>leaveKeyedAfterCall</c>, not a
    /// lock -- RX intentionally stays stopped, but this call's own pause window is still over.</summary>
    public PttRxResumeDecision DecideWasReceivingResume(bool skipUnkeyAndRxResume, bool abnormalTermination, bool pttLockedAtCleanup)
    {
        if (!skipUnkeyAndRxResume)
        {
            return PttRxResumeDecision.ResumeThenRaise;
        }

        if (!abnormalTermination && pttLockedAtCleanup)
        {
            _rxPendingResumeAfterUnlock = true;

            if (!_pttLocked && _rxPendingResumeAfterUnlock)
            {
                _rxPendingResumeAfterUnlock = false;
                return PttRxResumeDecision.ResumeThenRaiseUnlockRaced;
            }

            return PttRxResumeDecision.DeferPending;
        }

        return PttRxResumeDecision.RaiseOnly;
    }

    /// <summary><c>SetPttLockAsync</c>'s own unlock-direction consumption of a pending deferred resume.
    /// Deliberately a plain, non-atomic test-then-clear -- NOT <c>Interlocked.Exchange</c> -- matching
    /// Risk B's own documented accepted-residual shape (see this class's own summary); making this
    /// atomic would change what interleavings are possible, not merely harden the existing ones.</summary>
    public bool TryConsumeRxResumePending()
    {
        if (!_rxPendingResumeAfterUnlock)
        {
            return false;
        }

        _rxPendingResumeAfterUnlock = false;
        return true;
    }
}
