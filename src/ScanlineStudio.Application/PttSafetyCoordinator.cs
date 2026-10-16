namespace ScanlineStudio.Application;

/// <summary>
/// The PTT (push-to-talk) keying/un-keying state machine, extracted from
/// <see cref="SstvSessionService"/>'s <c>PlayWithPttAsync</c>/<c>DisposeAsync</c>.
/// Every method here is deliberately SYNCHRONOUS, no I/O, no logging, no awaiting:
/// <see cref="SstvSessionService"/> keeps every <c>await</c> and every <c>Log.*</c> call, this class
/// owns only the field reads/writes that decide what state to record. That split makes the
/// lost-update race below directly unit-testable via ordinary sequential calls, and it removes an
/// entire class of self-deadlock risk a callback-shaped coordinator would have introduced (this class
/// never calls back into the service, because it never awaits or calls back at all).
///
/// The step ORDERING in <c>PlayWithPttAsync</c> stays in that method; only the field mutations
/// themselves live here.
///
/// <b>Field ownership.</b> This class owns every field that is purely about "what does this session
/// currently believe about PTT/keying state." It deliberately does NOT own <c>_transmitInFlight</c>
/// (a whole-method single-flight guard that also cross-checks unrelated file-decode/loopback-self-test
/// state) or <c>_disposed</c> (a whole-service concern -- every method below that needs it takes it as
/// a parameter at the exact call site that needs it, so there is exactly one source of truth).
///
/// <b>Race 1, narrowed not fully closed</b> (the key/unkey epoch fields): an unkey's post-success
/// clears run on the continuation after the actual unkey command succeeds -- a CONCURRENT, NEWER key
/// attempt can complete in that same window and record its own "still keyed" state, and the stale
/// continuation's clears would wipe it out. The only overlap left in production is
/// <c>DisposeAsync</c>'s backstop unkey against a transmit whose bounded in-flight wait expired.
/// <see cref="BeginConfirmUnkey"/>/<see cref="CommitConfirmedUnkey"/> close this to instruction-scale
/// (an epoch snapshotted before the unkey attempt, rechecked before clearing) -- a keyer whose ENTIRE
/// publish lands exactly between an un-keyer's epoch recheck and its own clears is still wiped.
/// Closing this fully would need a lock across a few synchronous field accesses on both sides; not
/// added -- the surviving window is a few CPU instructions wide against a real device I/O round-trip.
/// </summary>
internal sealed class PttSafetyCoordinator
{
    // volatile: read/written from whatever thread runs PlayWithPttAsync/DisposeAsync, no lock between
    // them. Interlocked.Increment on a volatile field is CS0420, so the epoch/count/completion fields
    // below must stay non-volatile.
    private volatile bool _pttUnkeyFailedOnRealRig;

    // Deliberately NOT volatile: Interlocked.Increment/CompareExchange/Exchange on these is CS0420 if
    // they were. Read via Volatile.Read/Interlocked at every access instead, same visibility guarantee.
    private TaskCompletionSource? _keyedTransmitCompletion;
    private int _pttKeyEpoch;
    private int _pttUnkeyEpoch;
    private int _keyedTransmitCount;

    /// <summary>The most recently published in-flight keyed-transmit completion, or null if nothing is
    /// currently keyed. Best-effort: if two calls overlap, an older call's own TCS can be overwritten
    /// by a newer call's publish (see <see cref="_keyedTransmitCount"/>'s own role in
    /// <see cref="BelievesKeyedOrInFlight"/> for why the caller's shutdown backstop does not rely on
    /// this alone).</summary>
    public TaskCompletionSource? PendingKeyedTransmit => Volatile.Read(ref _keyedTransmitCompletion);

    /// <summary>A fresh snapshot of the key epoch, for a caller that needs to capture it BEFORE
    /// issuing its own key/unkey command (e.g. the post-key-phase <c>keyEpochAfterOwnKeyAttempt</c>
    /// snapshot) and pass it back into a later coordinator call
    /// (<see cref="ConfirmedUnkeyAlreadyHappenedSinceOwnKey"/>) once its own command has resolved. The
    /// snapshot timing itself is the caller's own responsibility -- this class has no way to know when
    /// a caller's command is "about to start."</summary>
    public int CurrentKeyEpoch => Volatile.Read(ref _pttKeyEpoch);

    /// <summary>Same shape as <see cref="CurrentKeyEpoch"/>, for the unkey-direction epoch.</summary>
    public int CurrentUnkeyEpoch => Volatile.Read(ref _pttUnkeyEpoch);

    /// <summary>Mutable, per-call bookkeeping created fresh by each <c>PlayWithPttAsync</c> call and
    /// passed BY REFERENCE (a class, not a struct -- must actually mutate in place) to every coordinator
    /// method that call needs. Deliberately minimal: "did this call believe it was physically keying a
    /// real rig" stays a plain local in the caller, fed by <see cref="BelievesKeyed"/>, never
    /// coordinator-owned.</summary>
    public sealed class PttKeyAttempt
    {
        public TaskCompletionSource? KeyedCompletion { get; set; }
    }

    public readonly record struct PttUnkeyAttempt(int EpochAtStart);

    public enum PttUnkeyCommitResult { Confirmed, RaceLostToNewerKey }

    /// <summary>Publishes a fresh keyed-transmit registration, only when the caller has already
    /// determined a real rig is about to be keyed. <c>TaskCreationOptions.RunContinuationsAsynchronously</c>
    /// is load-bearing, not a tuning knob: without it, <see cref="ReleaseKeyedSlot"/>'s own
    /// <c>TrySetResult</c> would resume <c>DisposeAsync</c>'s bounded wait INLINE on the transmit's
    /// cleanup thread, running the whole DI teardown there before that thread has released
    /// <c>_transmitInFlight</c> in its own enclosing finally.
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

    /// <summary>"Does this session believe PTT is still physically keyed from an earlier unkey that
    /// failed on a real rig" -- used at every site EXCEPT <c>DisposeAsync</c>'s own shutdown check (see
    /// <see cref="BelievesKeyedOrInFlight"/>).</summary>
    public bool BelievesKeyed() => _pttUnkeyFailedOnRealRig;

    /// <summary><c>DisposeAsync</c>'s own shutdown check -- adds the in-flight-count term (a keyed
    /// transmit currently in progress, whose own completion hasn't published a failed unkey yet) to
    /// <see cref="BelievesKeyed"/>.</summary>
    public bool BelievesKeyedOrInFlight() =>
        Volatile.Read(ref _keyedTransmitCount) > 0 || _pttUnkeyFailedOnRealRig;

    /// <summary>Records that a key attempt happened -- success or failure, the epoch bump is identical
    /// either way (a failed-but-possibly-keyed attempt is just as much a "new key" for Race 1's
    /// purposes as a confirmed one).</summary>
    public void RecordKeyEpochAdvance() => Interlocked.Increment(ref _pttKeyEpoch);

    /// <summary>First half of a confirmed-unkey attempt (the un-key command itself is awaited by the
    /// caller, between this call and <see cref="CommitConfirmedUnkey"/> -- this class has no I/O of its
    /// own). Epoch snapshot happens FIRST, the pessimistic pre-set of
    /// <see cref="_pttUnkeyFailedOnRealRig"/> SECOND -- reading the epoch any later would make a
    /// concurrent key more likely to be folded into this snapshot, widening Race 1's own window rather
    /// than closing it. The pre-set is gated on <paramref name="pttKeyedOnRealRig"/> -- setting it
    /// unconditionally would permanently latch a false "still keyed" belief on the benign no-radio
    /// path, where the whole unkey attempt never reaches a real backend at all.</summary>
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
    /// the failed-unkey belief (PTT is now confirmed physically off) and bumps the unkey epoch, but ONLY
    /// if nothing re-keyed since <see cref="BeginConfirmUnkey"/>'s own snapshot (Race 1's own guard) --
    /// the command still genuinely succeeded against the backend either way, so the caller always
    /// treats this as a successful unkey; the returned result only decides which of two logs to
    /// emit.</summary>
    public PttUnkeyCommitResult CommitConfirmedUnkey(PttUnkeyAttempt attempt)
    {
        if (Volatile.Read(ref _pttKeyEpoch) == attempt.EpochAtStart)
        {
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

    /// <summary>Releases a call's own keyed-transmit registration -- a no-op if this call never
    /// published one (the common no-radio path): an unconditional decrement here would drive
    /// <see cref="_keyedTransmitCount"/> to -1 permanently on that path, blinding
    /// <see cref="BelievesKeyedOrInFlight"/>'s own shutdown check for the rest of the process's life.
    /// <c>CompareExchange</c>, not a plain null write: only clears the shared field if it still points
    /// at THIS call's own instance, so an overlapping call's own still-live registration is never
    /// erased.</summary>
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
}
