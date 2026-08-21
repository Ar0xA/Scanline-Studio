using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application;

public sealed partial class SstvSessionService : ISstvSessionService
{
    private readonly IAudioEngine _audioEngine;
    private readonly IAudioDeviceEnumerator _deviceEnumerator;
    private readonly ISettingsStore _settingsStore;
    private readonly ISstvDecoder _decoder;
    private readonly ISstvEncoder _encoder;
    private readonly IMacroTextResolver _macroTextResolver;
    private readonly IRadioSessionService _radioSession;
    private readonly ILogger<SstvSessionService> _logger;
    private readonly TimeSpan _cleanupTimeout;
    private readonly TimeSpan _playbackStopWaitBudget;
    private readonly TimeSpan _inFlightKeyedTransmitWait;
    private readonly TimeSpan _playbackStallTimeout;
    private readonly Action<ReadOnlyMemory<float>> _decoderHandler;
    private readonly Action<ReadOnlyMemory<float>> _waterfallHandler;
    // Ultracode audit finding #34's OnDecoderRestartCriticallyOverdue can now call StopReceivingAsync
    // (which writes this) from the audio drain thread, not just from a UI-thread-initiated
    // StartReceivingAsync/StopReceivingAsync call -- volatile for the same reason _pttLocked below
    // already is (this field's own reads/writes now span more than one caller thread with no lock
    // between them).
    private volatile bool _isReceiving;

    // Round-15 nit: every other cross-thread flag on this class (_isReceiving immediately above,
    // _pttLocked/_rxPendingResumeAfterUnlock below) is volatile with an explicit comment justifying
    // it -- this one is written from OnDecoderRestarted/OnDecoderRestartCriticallyOverdue (see the
    // latter's own doc comment: reachable from the audio drain thread, not just a UI-thread caller)
    // and read from OnDecoderRestarted, with no lock between them.
    private volatile bool _maintenanceWarningActive;

    // See SetPttLockAsync's own doc comment for the full concurrency reasoning. volatile (not a
    // plain bool) since this is written from whatever thread calls SetPttLockAsync and read from
    // PlayWithPttAsync's entry/finally on the caller's own thread -- no dedicated background thread
    // owns this class the way MiniAudioCaptureSession's drain thread does, but the two call paths
    // are still logically concurrent callers with no lock between them.
    private volatile bool _pttLocked;

    // See PlayWithPttAsync's finally block (where this is set) and SetPttLockAsync (where it's
    // consumed) for the full reasoning -- tracks "a lock-covered Transmit/Tune call paused RX and is
    // relying on a later unlock to resume it" across the gap between those two independent calls.
    // Same threading shape/reasoning as _pttLocked immediately above.
    private volatile bool _rxPendingResumeAfterUnlock;

    // Blocker 3 (Tier A Batch 3 chunk 3a): tracks "a PlayWithPttAsync call has PTT keyed RIGHT NOW,
    // mid-transmit" so DisposeAsync can WAIT for that call's own un-key instead of racing it. A plain
    // bool can't be awaited, so this is a TaskCompletionSource: non-null exactly between the point
    // PlayWithPttAsync keys (or inherits an already-keyed) PTT and the point its finally has finished
    // its un-key attempt. Deliberately NOT `volatile` (unlike _pttLocked/_isReceiving above):
    // Interlocked.CompareExchange on a volatile field is CS0420, and this project treats warnings as
    // errors -- every access goes through Volatile.Read/Interlocked instead, same guarantee.
    private TaskCompletionSource? _keyedTransmitCompletion;

    // Two producers, both "physically keyed after the call returned/failed" states _pttLocked does NOT
    // cover: (1) TuneAsync's leaveKeyedAfterTune leaves PTT keyed without ever setting _pttLocked
    // (SetPttLockAsync's own doc comment already documents that gap); (2) round-7 finding:
    // SetPttLockAsync(true)'s own SetPttAsync call throwing AFTER physically keying the rig --
    // _pttLocked never gets set in that case either (the throw happens before that write), so this is
    // the only record of the attempt. Tracked so DisposeAsync's shutdown backstop covers both -- same
    // failure class as blocker 3, one field to close. Same threading shape as _pttLocked.
    private volatile bool _pttLeftKeyedByCall;

    // Tier A Batch 3 chunk 3a round-2 finding: DisposeAsync's backstop only guards
    // _keyedTransmitCompletion state published BEFORE it runs -- a PlayWithPttAsync call that hasn't
    // reached its own publish point yet (e.g. RadioStatusViewModel.TuneAsync's real production call
    // passes CancellationToken.None, so nothing can ever cancel it) could previously key PTT AFTER
    // DisposeAsync had already run its backstop and returned, with no shutdown safety net left to catch
    // it. Read by PlayWithPttAsync's own thread, written by whatever thread calls DisposeAsync -- same
    // threading shape as _pttLocked above.
    private volatile bool _disposed;

    // Round-3 finding: the fourth "physically keyed at shutdown" state DisposeAsync's own three-state
    // enumeration (see its own comment) does not cover -- a transmit whose cleanup un-key was ATTEMPTED
    // and FAILED records nothing today: _pttLocked was already false by that point, _pttLeftKeyedByCall
    // is never set for this path, and _keyedTransmitCompletion is unconditionally cleared by
    // PlayWithPttAsync's own inner finally regardless of whether the un-key succeeded. Without this, a
    // rig UnkeyForCleanupAsync already logged Critical ("MAY STILL BE KEYED") is then read by
    // DisposeAsync as "nothing to do" -- most plausible when the failure was CleanupTimeout (5s)
    // expiring against a slow-but-healthy backend, where a fresh attempt at shutdown would likely
    // succeed. Set only for the captured-at-key-time real-rig case, so it can never fire for the benign
    // RigId=="none" path. Same threading shape as _pttLocked above.
    private volatile bool _pttUnkeyFailedOnRealRig;

    // Round-5 finding: closes a lost-update race UnkeyForCleanupAsync's own unconditional post-success
    // clears (_pttLocked/_pttLeftKeyedByCall/_pttUnkeyFailedOnRealRig = false) could otherwise cause.
    // Those clears run on the continuation AFTER `await TryUnkeyPttAsync(...)` succeeds -- but a
    // CONCURRENT, NEWER key command (SetPttLockAsync(true) or another PlayWithPttAsync call) can
    // complete and set its own state in that same window, and the un-key's stale continuation would
    // then wipe that newer call's "still keyed" state out from under it: transmitter genuinely re-keyed,
    // every shutdown-backstop flag reads false, DisposeAsync's four-state check finds nothing to do.
    // Incremented via Interlocked.Increment immediately after every SetPttAsync(true) call that either
    // SUCCEEDED or may have physically keyed the rig before throwing (round-8 finding: SetPttLockAsync's
    // own key-command-failure catch bumps this too, for the identical reason -- a failed-but-possibly-
    // keyed attempt is just as much a "new key" as a confirmed one, and skipping the bump there was
    // itself a fourth instance of this same lost-update shape). Never on the false/unkey direction --
    // every un-key/unlock cleanup path snapshots this before its own un-key attempt and only performs
    // its clears if nothing re-keyed in the meantime. Not `volatile`, matching _keyedTransmitCompletion's
    // own reasoning above: Interlocked.Increment on a volatile field is CS0420, so this is read via
    // Volatile.Read/Interlocked instead everywhere it's touched.
    //
    // <b>Round-6 finding, honestly documented rather than silently left implicit (matching Risk B's own
    // precedent below): this NARROWS the lost-update window to instruction-scale, it does not fully
    // close it.</b> The read-epoch-then-conditionally-clear sequence in each un-key/unlock cleanup path
    // is not atomic with the increment-then-write-flag sequence at each key site -- a keyer whose
    // ENTIRE publish (bump + flag write) lands exactly between an un-keyer's epoch recheck and its own
    // clears is still wiped. Closing this fully would need a plain (non-async) lock held only across
    // those few synchronous field accesses on both sides (no `await` inside either region, so it would
    // NOT reintroduce the objection _pttLockGate's own doc comment raises against a broader gate) --
    // deliberately not added: the surviving window is a few CPU instructions wide against a
    // multi-millisecond-to-multi-second pre-fix window (a device I/O round-trip or a full serial/TCP
    // PTT command), the same order-of-magnitude trade this file already accepts for Risk B, and adding a
    // new synchronization primitive has its own review/deadlock-risk cost that this residual doesn't
    // currently justify. Revisit if a real caller ever makes this reachable at meaningfully higher key
    // rates than today's zero-production-caller status.
    private int _pttKeyEpoch;

    // Round-26 finding: the mirror of _pttKeyEpoch above, for the opposite direction. PlayWithPttAsync's
    // own cleanup used to have no way to detect "a CONFIRMED un-key already happened since I keyed" --
    // e.g. TuneAsync(leaveKeyedAfterTune: true) keys the rig, then the operator calls
    // SetPttLockAsync(false) mid-tone and genuinely un-keys it (rig confirmed OFF), then the tune
    // completes and unconditionally writes _pttLeftKeyedByCall = true anyway (see that write's own
    // comment) -- a permanent false "still keyed" belief on a rig that is demonstrably off, with no
    // existing signal able to catch it (_pttKeyEpoch is deliberately bumped ONLY on the key direction,
    // so it cannot observe an unlock). Incremented via Interlocked.Increment at every CONFIRMED un-key
    // site -- both UnkeyForCleanupAsync's own success branch and SetPttLockAsync's own successful-unlock
    // branch (each alongside their existing _pttKeyEpoch-guarded clears, so this only bumps when THAT
    // un-key's own epoch check already confirmed nothing newer re-keyed in the meantime). Snapshotted
    // once by PlayWithPttAsync -- round-28 correction: NOT at method entry (that was itself a bug, see
    // its own finding) but at the SAME point as the sibling keyEpochAfterOwnKeyAttempt snapshot,
    // immediately after this call's own key phase -- and rechecked before writing
    // _pttLeftKeyedByCall = true or before attempting a redundant cleanup un-key -- see both call sites'
    // own comments. Same threading shape as _pttKeyEpoch: not volatile (Interlocked.Increment on a
    // volatile field is CS0420), read via Volatile.Read/Interlocked everywhere it's touched.
    private int _pttUnkeyEpoch;

    // Round-6 finding: DisposeAsync's "is a transmit currently keyed" check used to be
    // Volatile.Read(ref _keyedTransmitCompletion) is not null -- unsound when two PlayWithPttAsync
    // calls overlap (a real production interleaving: RadioStatusViewModel's Tune command has no
    // TX-in-progress CanExecute gate, so clicking Tune during a TransmitAsync produces two live calls).
    // _keyedTransmitCompletion's own Interlocked.Exchange publish is a last-writer-wins overwrite, not a
    // registry -- the newer call's publish silently drops the older call's TCS, and if the newer call
    // finishes first, the field goes null while the OLDER call is still genuinely keyed, blinding this
    // check during that window. A reference count (not nullness) survives overwrite: incremented
    // alongside every _keyedTransmitCompletion publish, decremented alongside every clear -- see both
    // call sites' own comments. The actual bounded WAIT in AwaitInFlightKeyedTransmitAsync still only
    // targets whichever TCS is most recently published (a best-effort wait, not a fix for that -- see
    // its own comment), but the OR-condition this count feeds now correctly still triggers the
    // backstop un-key even when the wait itself has nothing to observe. Same threading shape as
    // _pttKeyEpoch above.
    private int _keyedTransmitCount;

    // Round-12 finding: two overlapping PlayWithPttAsync calls (TransmitAsync racing TuneAsync --
    // RadioStatusViewModel's own Tune command has no TX-in-progress CanExecute gate, so clicking Tune
    // during a TransmitAsync produces two live calls -- see _keyedTransmitCount's own comment for this
    // same reachability) used to let the SECOND call unconditionally re-key and call
    // StartPlaybackAsync, which throws "already started" against the FIRST call's own live playback
    // session -- that throw is caught generically, classified abnormalTermination, and its finally's
    // urgent un-key runs immediately, dropping the FIRST call's carrier mid-frame, followed by
    // StopPlaybackWithWatchdogAsync disposing the session out from under it. This is a DIFFERENT
    // failure mode than the leaked-keyed-transmitter class every other field on this class defends
    // against: PTT ends up correctly OFF, but a genuinely in-flight, correctly-behaving transmission
    // gets silently killed by an unrelated second call. A single-flight guard (CompareExchange at
    // PlayWithPttAsync's very entry, before ANYTHING else -- no RX pause, no device resolution, no PTT
    // touched) rejects the second call outright instead. Deliberately a SEPARATE field from
    // _keyedTransmitCount: that one is also incremented by SetPttLockAsync (a different, narrower
    // concern -- shutdown-wait visibility, not single-flight exclusivity) and is never incremented at
    // all when RigId == "none" (the "no radio configured" case still needs the SAME playback-session
    // exclusivity this field protects). 0 = idle, 1 = a call owns it; plain `int` is sufficient since
    // CompareExchange is the only operation ever performed on it.
    private int _transmitInFlight;

    // Hot-path exception rate-limiting (docs/logging-guidelines.md's "Hot-path rule") -- these
    // handlers run on the audio engine's own capture-forwarding path, once per captured chunk;
    // logging every occurrence would turn a logging change into dropped RX samples. First
    // occurrence logs immediately, then only every Nth after that.
    private const int ExceptionLogEveryN = 200;
    private int _decoderExceptionCount;
    private int _waterfallExceptionCount;
    private int _transmitProgressHandlerExceptionCount;

    public SstvSessionService(
        IAudioEngine audioEngine,
        IAudioDeviceEnumerator deviceEnumerator,
        ISettingsStore settingsStore,
        ISstvDecoder decoder,
        ISstvEncoder encoder,
        IMacroTextResolver macroTextResolver,
        IWaterfallSource waterfall,
        IReceivedImageBuffer receivedImage,
        IRadioSessionService radioSession,
        ILogger<SstvSessionService> logger)
        : this(audioEngine, deviceEnumerator, settingsStore, decoder, encoder, macroTextResolver,
               waterfall, receivedImage, radioSession, logger,
               cleanupTimeoutForTests: null, playbackStopWaitBudgetForTests: null,
               inFlightKeyedTransmitWaitForTests: null, playbackStallTimeoutForTests: null)
    {
    }

    /// <summary>Test-only: lets a test shrink the PTT-safety cleanup budgets (production 5s/5s/3s/5s)
    /// so the blocker-1/blocker-3/round-13 regression tests (Tier A Batch 3 chunk 3a) can actually let
    /// a budget EXPIRE without a multi-second-per-test suite -- same shape as
    /// <c>RxDiskLineStagingBuffer</c>'s own <c>disposeDrainTimeoutForTests</c> precedent.</summary>
    internal SstvSessionService(
        IAudioEngine audioEngine,
        IAudioDeviceEnumerator deviceEnumerator,
        ISettingsStore settingsStore,
        ISstvDecoder decoder,
        ISstvEncoder encoder,
        IMacroTextResolver macroTextResolver,
        IWaterfallSource waterfall,
        IReceivedImageBuffer receivedImage,
        IRadioSessionService radioSession,
        ILogger<SstvSessionService> logger,
        TimeSpan? cleanupTimeoutForTests,
        TimeSpan? playbackStopWaitBudgetForTests,
        TimeSpan? inFlightKeyedTransmitWaitForTests,
        TimeSpan? playbackStallTimeoutForTests)
    {
        _cleanupTimeout = cleanupTimeoutForTests ?? CleanupTimeout;
        _playbackStopWaitBudget = playbackStopWaitBudgetForTests ?? PlaybackStopWaitBudget;
        _inFlightKeyedTransmitWait = inFlightKeyedTransmitWaitForTests ?? InFlightKeyedTransmitWait;
        _playbackStallTimeout = playbackStallTimeoutForTests ?? PlaybackStallTimeout;

        _audioEngine = audioEngine;
        _deviceEnumerator = deviceEnumerator;
        _settingsStore = settingsStore;
        _decoder = decoder;
        _encoder = encoder;
        _macroTextResolver = macroTextResolver;
        Waterfall = waterfall;
        ReceivedImage = receivedImage;
        _radioSession = radioSession;
        _logger = logger;

        // Isolated fan-out (Phase-3 plan decision #3): a throwing/slow handler on one target must
        // never prevent the other from running -- this is what actually fixes the bug the pre-build
        // spike's original IWaterfallSource design would otherwise have reintroduced.
        _decoderHandler = samples =>
        {
            try
            {
                _decoder.PushSamples(samples);
            }
            catch (Exception ex)
            {
                // Deliberately swallowed here, not rethrown into the audio engine's own forwarder
                // (which has no exception isolation of its own -- see IWaterfallSource's doc
                // comment) -- silently continuing beats corrupting the other fan-out target or
                // crashing the drain thread. Rate-limited log per the hot-path rule above.
                var count = Interlocked.Increment(ref _decoderExceptionCount);
                if (count == 1 || count % ExceptionLogEveryN == 0)
                {
                    SafeLog(() => Log.DecoderPushSamplesFailed(_logger, count, ex));
                }
            }
        };
        _waterfallHandler = samples =>
        {
            try
            {
                Waterfall.PushSamples(samples);
            }
            catch (Exception ex)
            {
                var count = Interlocked.Increment(ref _waterfallExceptionCount);
                if (count == 1 || count % ExceptionLogEveryN == 0)
                {
                    SafeLog(() => Log.WaterfallPushSamplesFailed(_logger, count, ex));
                }
            }
        };

        // Ultracode audit finding #34: ISstvDecoderMaintenance is an optional side-channel only
        // RestartableSstvDecoder implements (not on ISstvDecoder itself -- see that interface's own
        // doc comment for why). Real decoders wire this up; the various FakeSstvDecoders used by
        // other test projects don't implement it, so this is a no-op there.
        if (_decoder is ISstvDecoderMaintenance maintenance)
        {
            maintenance.RestartOverdue += OnDecoderRestartOverdue;
            maintenance.Restarted += OnDecoderRestarted;
            maintenance.RestartCriticallyOverdue += OnDecoderRestartCriticallyOverdue;
        }
    }

    public IWaterfallSource Waterfall { get; }

    public IReceivedImageBuffer ReceivedImage { get; }

    public IReadOnlyList<SstvModeDefinition> AvailableModes => SstvModeRegistry.All;

    public bool IsReceiving => _isReceiving;

    public bool IsPttLocked => _pttLocked;

    /// <summary>Serializes <see cref="SetPttLockAsync"/> so two overlapping calls can never interleave
    /// (a real TOCTOU an earlier check-then-act version had) -- see that method's own doc comment for
    /// the full reasoning. Round-14 nit: deliberately never disposed by <see cref="DisposeAsync"/>,
    /// matching this class's post-dispose contract of reaching the idempotency/disposed check on a
    /// still-usable primitive rather than throwing from the primitive itself.</summary>
    private readonly SemaphoreSlim _pttLockGate = new(1, 1);

    /// <summary>Manual-keying diagnostic aid (e.g. a "PTT lock" button) -- keys PTT immediately and
    /// holds it keyed independent of any <see cref="TransmitAsync"/>/<see cref="TuneAsync"/> call,
    /// until unlocked. Operates directly on the PTT line only -- unlike <see cref="PlayWithPttAsync"/>,
    /// this method does NOT itself pause/resume RX capture; if a lock is engaged while a
    /// Transmit/Tune call is skipping its own un-key because of this lock (see
    /// <see cref="PlayWithPttAsync"/>'s own doc comment), THAT call's paused RX is what gets resumed
    /// here on unlock (see <see cref="_rxPendingResumeAfterUnlock"/>) -- not a capture pause owned by
    /// this method itself.
    ///
    /// <b>Idempotent in OUTCOME, not by skipping redundant calls</b> (an audit-fix correction from an
    /// earlier version of this method that short-circuited when the requested state already matched
    /// <see cref="_pttLocked"/> -- a real bug: <see cref="TuneAsync"/>'s <c>leaveKeyedAfterTune</c>
    /// leaves PTT physically keyed without ever setting <see cref="_pttLocked"/>, and a failed
    /// lock-engage leaves it false too -- either way, a subsequent unlock call would have silently
    /// no-op'd on a still-keyed rig with no way to recover via this API at all). Every call now always
    /// issues the underlying <see cref="IRadioSessionService.SetPttAsync"/> command -- confirmed
    /// harmless: every shipped protocol backend's PTT set is an absolute, idempotent command, not a
    /// read-modify-write. Serialized via <see cref="_pttLockGate"/> so two overlapping calls can never
    /// interleave (a real TOCTOU an earlier check-then-act version had).
    ///
    /// <b>Failure behavior is deliberately asymmetric-by-outcome, not by direction</b>: the internal
    /// "locked" flag is only updated AFTER <see cref="IRadioSessionService.SetPttAsync"/> actually
    /// succeeds, in both directions -- so a failed lock-engage leaves <see cref="IsPttLocked"/> false
    /// (correctly reflecting that PTT was never confirmed keyed), and a failed unlock leaves it TRUE
    /// (correctly reflecting that PTT was never confirmed un-keyed, so a caller can safely retry
    /// unlock rather than the lock silently "forgetting" a still-keyed rig). This call's own
    /// <see cref="IRadioSessionService.SetPttAsync"/> failure is NOT swallowed here (unlike
    /// <see cref="PlayWithPttAsync"/>'s best-effort cleanup steps) -- this is a direct, explicit
    /// caller action, not an automatic cleanup path, so the caller needs to know if it failed. (RX
    /// resume-after-unlock IS best-effort/swallowed, deliberately -- a failure to resume monitoring
    /// must not be reported as "unlock failed" when PTT itself was genuinely un-keyed successfully.)
    ///
    /// <b>Known, accepted race (unchanged by this fix, documented not silently left implicit)</b>: an
    /// unlock call racing a Transmit/Tune call's own entry (which already decided not to key because
    /// the lock looked engaged) can un-key PTT out from under an in-flight transmission, sending the
    /// rest of that frame into a dead carrier. No production caller exists yet for this method or
    /// <c>leaveKeyedAfterTune: true</c> (unwired UI), so this is latent, not exercised -- fixing it
    /// fully would need a single shared gate across <see cref="PlayWithPttAsync"/> AND this method,
    /// which would also make an emergency unlock wait behind an in-flight transmission's own gate
    /// hold -- a worse safety property than the current race for what unlock is meant to be (an
    /// escape hatch). Revisit if/when a real caller actually needs this closed.
    ///
    /// <b>Round-11/round-12: a failed ENGAGE attempt</b> (this method's own key command throwing
    /// after possibly physically keying the rig -- see the round-7/11/12 catch block below) DOES now
    /// attempt an immediate recovery un-key, but only when <see cref="_keyedTransmitCount"/> reads
    /// exactly 1 at that moment -- i.e. only when nothing else (no concurrent
    /// <see cref="PlayWithPttAsync"/> transmission, no other <see cref="SetPttLockAsync"/> call) has
    /// a registration in flight to endanger. Round 11 originally left this un-recovered entirely,
    /// reasoning an unconditional recovery would introduce a new instance of the race above; round 12
    /// corrected that -- the guarded version is safe (see the catch block's own comment for the full
    /// argument) and closes what would otherwise be an hours-long recorded-but-not-recovered window
    /// in the common (non-concurrent) case, which is the overwhelmingly likely one for this
    /// unwired manual diagnostic aid.</summary>
    public async Task SetPttLockAsync(bool locked, CancellationToken ct = default)
    {
        await _pttLockGate.WaitAsync(ct).ConfigureAwait(false);

        // Round-8 finding: this call's own handle into _keyedTransmitCompletion -- local as well as
        // field so the outermost finally below can clear the field ONLY if it still points at this
        // call's own instance (same CompareExchange pattern as PlayWithPttAsync's own blocker-3
        // mechanism, which this method never adopted). Without this, a key command in flight here was
        // invisible to BOTH DisposeAsync's bounded shutdown WAIT (AwaitInFlightKeyedTransmitAsync reads
        // this same field) and its four-state backstop check (_keyedTransmitCount) -- DisposeAsync could
        // run to completion, dispose IRadioSessionService, and only THEN would this call's own
        // post-await disposal-race recovery (the `if (locked && _disposed)` block below) get a chance to
        // run -- against a radio session that no longer exists. Publishing this BEFORE the key command
        // makes DisposeAsync's wait actually block for this call, which keeps IRadioSessionService alive
        // long enough for this method's own recovery un-key to have something to talk to.
        TaskCompletionSource? keyedCompletion = null;

        try
        {
            try
            {
                // Round-2 fix: only the ENGAGE direction is rejected post-disposal -- an unlock must
                // stay a valid escape hatch for a rig this class already left keyed (matches
                // _pttLocked's own failed-unlock-stays-true reasoning below; disposal must never remove
                // the one remaining way to un-key a rig that is still physically keyed).
                if (locked)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                }

                // Round-6 finding: snapshotted BEFORE the SetPttAsync await below, not after -- see
                // _pttKeyEpoch's own doc comment for the lost-update race this closes. Same fix shape
                // UnkeyForCleanupAsync already uses; round 5 fixed only that twin location and missed
                // this one -- SetPttLockAsync's own unlock-path clears just below had the identical gap.
                var epochAtUnkeyStart = Volatile.Read(ref _pttKeyEpoch);

                // Round-7 finding: captured ONCE, before the key command -- the same blocker-2 rule
                // PlayWithPttAsync already follows (never re-read RigId at catch/failure time, since
                // RadioController.DisconnectAsync/DisposeAsync can reset it to "none" without un-keying).
                // Only meaningful for the ENGAGE direction: an unlock failure doesn't need this --
                // TryUnkeyPttAsync already classifies that failure using the parameter passed to it, not
                // a fresh RigId read.
                var rigIsRealAtKeyTime = locked && _radioSession.RigId != "none";

                // Round-18 finding 5: the unlock direction's own SetPttAsync failure had NO catch arm
                // at all (the existing one below is filtered on rigIsRealAtKeyTime, which is always
                // false when `locked` is false) -- it propagated straight out with no log whatsoever,
                // not even a Warning. The safety net itself was never broken (_pttLocked is only ever
                // cleared on a CONFIRMED success further down, so it correctly stays true here,
                // and DisposeAsync's own backstop still catches it) -- but the operator got no signal
                // at all until shutdown, for a failed EMERGENCY unlock on a genuinely keyed rig, the
                // one action in this whole class an operator reaches for specifically because
                // something already went wrong.
                //
                // Round-24 finding (risk): round 18's own fix filtered this catch on a FRESH RigId
                // read (rigIsRealAtUnlockTime) -- the exact blocker-2 anti-pattern this file's own
                // established rule forbids (see rigIsRealAtKeyTime's own comment just above: never
                // re-read RigId at catch/failure time). If the CAT link drops between key and unlock
                // (RadioController.DisconnectAsync sets RigId to "none" WITHOUT un-keying, per its own
                // doc comment), the rig is still genuinely keyed but this filter reads "none" and never
                // matches AT ALL -- not even the inner belief-based gate below ever runs, so the
                // emergency unlock's own failure produced NO log whatsoever, verbatim the harm round 18
                // exists to prevent. Fixed by filtering on `!locked` instead: the inner gate just below
                // (_pttLocked || _pttLeftKeyedByCall || _pttUnkeyFailedOnRealRig) is already the correct,
                // belief-based test and needs no device-identity filter layered on top of it.

                if (rigIsRealAtKeyTime)
                {
                    // Round-8 finding: published BEFORE the key command, same shape/reasoning as
                    // PlayWithPttAsync's own publish (see its own comment) -- so DisposeAsync can never
                    // tear IRadioSessionService down out from under this call's own in-flight key/un-key.
                    keyedCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    Interlocked.Exchange(ref _keyedTransmitCompletion, keyedCompletion);
                    Interlocked.Increment(ref _keyedTransmitCount);

                    // Round-9 finding: PlayWithPttAsync rechecks _disposed IMMEDIATELY after this same
                    // publish, before ITS key command (see its own comment) -- SetPttLockAsync published
                    // the registration but never adopted the matching recheck, so it could still issue a
                    // key command after DisposeAsync had already read this registration as empty, found
                    // nothing to do, and returned. The window is instruction-scale (this publish and the
                    // key command below have no await between them), and this call still self-detects and
                    // recovers via the existing `if (locked && _disposed)` block further down if the key
                    // itself succeeds -- but that recovery then races IRadioSessionService's own DI
                    // teardown, which is exactly the class of failure this whole chunk exists to close.
                    // Closing it HERE, before the command is ever issued, is strictly better than relying
                    // on the recovery alone. No `locked &&` needed here (unlike the recovery block
                    // further down) -- this is already inside `if (rigIsRealAtKeyTime)`, which is itself
                    // `locked && ...`.
                    ObjectDisposedException.ThrowIf(_disposed, this);
                }

                // Round-29 finding (risk): declared out here, not inside the try below, so the two
                // catch arms can reach it for their own fault-observer attachment (see each catch's own
                // comment) -- a local declared inside a try is not in scope in that try's own catch
                // blocks. Nullable: if _radioSession.SetPttAsync itself throws synchronously (e.g. the
                // null-object backend), this is never assigned at all, and there is nothing to observe.
                Task? pttCommand = null;
                try
                {
                    // Round-17 finding: this await had no bound of its own -- the identical gap
                    // PlayWithPttAsync's own key command had before round 14's fix (see that call
                    // site's own comment for the full reasoning; same WaitAsync treatment applies
                    // here for the same reason). Verified against both shipped backends directly:
                    // HamlibRadioProtocol.SetPttAsync's CallAsync wrapper takes NO CancellationToken
                    // parameter at all -- once the blocking native rig_set_ptt call starts, nothing
                    // can interrupt it -- and RigctldClientProtocol's reply read has no timeout of its
                    // own either. A wedged rig here (engage OR disengage direction) previously hung
                    // this call forever WHILE HOLDING _pttLockGate -- stranding every future
                    // SetPttLockAsync call, including a future emergency unlock, the one escape hatch
                    // this whole method exists to be.
                    //
                    // Round-19 correction: for the UNLOCK direction specifically, `ct` reaching the
                    // COMMAND itself (not just the wait) has the identical shape TryUnkeyPttAsync's own
                    // round-18 fix closed -- a caller cancelling `ct` while this call is queued behind
                    // a wedged prior command aborts the un-key AT THE BACKEND'S REQUEST GATE, so it
                    // never reaches the rig. This is the emergency-unlock escape hatch this method's
                    // own doc comment says exists precisely for "when something already went wrong" --
                    // it must stay queued and eventually reach the rig, not be cancellable away. The
                    // ENGAGE direction keeps `ct` on the command (a cancelled key SHOULD be dropped,
                    // matching the caller's actual intent); the wait itself stays bounded by `ct` in
                    // both directions either way.
                    pttCommand = _radioSession.SetPttAsync(locked, locked ? ct : CancellationToken.None);
                    await pttCommand.WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);
                }
                catch (Exception) when (rigIsRealAtKeyTime)
                {
                    // Round-7 finding: SetPttAsync(true) throwing does not mean the rig wasn't physically
                    // keyed -- matches PlayWithPttAsync's own "erring true costs at most one spurious
                    // Warning; erring false is the blocker-2 silent swallow" reasoning (see its own comment
                    // at the pttKeyedOnRealRig assignment), which SetPttLockAsync never adopted. Without
                    // this, a mid-command failure here (e.g. RigctldClientProtocol writes the PTT command,
                    // then the READ of its reply times out or the connection drops) leaves EVERY
                    // shutdown-backstop flag false -- _pttLocked never gets set (this throw happens before
                    // that write below), and nothing else records the attempt -- so DisposeAsync's
                    // four-state check finds nothing to do and the process can exit with the transmitter
                    // genuinely keyed, silently. _pttLeftKeyedByCall is reused as the marker (see its own
                    // doc comment, widened to cover this second producer) rather than adding a new field,
                    // since its existing meaning ("physically keyed, not tracked by _pttLocked") is exactly
                    // this situation.
                    //
                    // Round-8 finding: this write is ITSELF exactly the lost-update shape rounds 5-7 already
                    // closed at three other sites -- without bumping the epoch here too, a concurrent
                    // UnkeyForCleanupAsync call whose own epoch snapshot predates this write can still wipe
                    // it moments later, believing nothing new happened. Bumping errs conservative (a
                    // concurrent un-keyer just skips its clears and logs PttUnkeyRaceLostToNewerKey instead
                    // of wiping this call's state) -- same "erring true costs at most one spurious Warning"
                    // rule this file already applies everywhere else.
                    Interlocked.Increment(ref _pttKeyEpoch);
                    _pttLeftKeyedByCall = true;
                    SafeLog(() => Log.PttKeyCommandFailedMayHaveKeyed(_logger));

                    // Round-11 found this catch never attempts an immediate recovery un-key (unlike
                    // PlayWithPttAsync's own finally), leaving the rig recorded-but-not-recovered for
                    // potentially the rest of the process's life. Round-11 initially left this
                    // deliberately unfixed, reasoning that an unconditional recovery un-key here would
                    // introduce a NEW instance of this class's own "Known, accepted race" (a
                    // concurrent, genuinely on-air PlayWithPttAsync transmission losing its carrier).
                    //
                    // Round-12 correction: that reasoning was wrong on two counts. First, the
                    // "unconditional" un-key in PlayWithPttAsync's OWN finally already accepts the
                    // identical harm today, unguarded, on a MORE reachable path (two overlapping
                    // PlayWithPttAsync calls) -- so recovering here is not a new risk class, just a
                    // second place accepting the same one. Second, and more usefully, a genuinely safe
                    // GUARDED recovery is available and is what's implemented below: _keyedTransmitCount
                    // was already incremented for THIS call at the publish above (guarded by
                    // rigIsRealAtKeyTime, same as this catch), so it reads exactly 1 if and only if
                    // nothing else currently holds a registration. When it's 1, there is nothing else
                    // to endanger, and recovering immediately is strictly safe. When it's >1, this
                    // skips the recovery -- byte-for-byte today's behavior, zero regression on the
                    // concurrent case. The residual (something registers AFTER this check but before
                    // the un-key command reaches the wire) is strictly narrower than the unguarded
                    // window PlayWithPttAsync's own finally already accepts, and lands before that
                    // call's own StartPlaybackAsync, not mid-frame. UnkeyForCleanupAsync's own epoch
                    // snapshot (taken after this catch's own bump above) still correctly suppresses its
                    // clears if a genuinely newer key raced it -- unchanged by this addition. Bounded:
                    // holds _pttLockGate for at most _cleanupTimeout (UnkeyForCleanupAsync's own CTS),
                    // same precedent as the existing in-gate recovery a few lines below. `await` inside
                    // a `catch` followed by a bare `throw;` is valid C# and preserves the original
                    // exception/stack trace -- ONLY if UnkeyForCleanupAsync itself doesn't throw a NEW
                    // one first, which round 20 found it actually can (see its own comment). Guarded
                    // here so the ORIGINAL key-command failure always reaches the caller, not a
                    // logging-provider failure substituted in its place -- the state record itself
                    // doesn't depend on this guard (UnkeyForCleanupAsync's own round-20 fix latches
                    // _pttUnkeyFailedOnRealRig before it can throw), only which exception surfaces.
                    if (Volatile.Read(ref _keyedTransmitCount) == 1)
                    {
                        try
                        {
                            await UnkeyForCleanupAsync(pttKeyedOnRealRig: true).ConfigureAwait(false);
                        }
                        catch (Exception recoveryEx)
                        {
                            SafeLog(() => Log.CleanupStepFailed(_logger, "PTT recovery un-key", recoveryEx));
                        }
                    }

                    // Round-29 finding (risk): every OTHER abandoned task in this class attaches a
                    // fault-observer continuation to its own abandoned task (StopReceivingAsync,
                    // StopPlaybackWithWatchdogAsync, ResumeReceivingBoundedAsync, TryUnkeyPttAsync's own
                    // round-26 fix) -- this one, the key-command await this whole method exists to
                    // protect, did not. Gated the same way those sites gate it: only if pttCommand was
                    // actually assigned (the try got far enough to issue the command) and is still
                    // running (not yet completed -- if it already finished, its own fault already
                    // propagated through the WaitAsync above as the exception this catch is handling).
                    if (pttCommand is { IsCompleted: false })
                    {
                        _ = pttCommand.ContinueWith(
                            t => SafeLog(() => Log.CleanupStepFailed(_logger, "PTT command (finished after watchdog)", t.Exception!)),
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }

                    throw;
                }
                catch (Exception) when (!locked)
                {
                    // Round-18 finding 5's fix, corrected by round 24: see the capture site's own
                    // comment just above for why this filters on `!locked` rather than a fresh RigId
                    // read. Deliberately simpler than the engage-direction arm above -- no epoch bump
                    // (this call never successfully keyed anything; _pttLocked is untouched by this
                    // catch and correctly remains true, so no lost-update race is possible here) and
                    // no recovery attempt
                    // (an unlock that itself failed has nothing safe to recover TO -- the existing
                    // "Known, accepted race" this method's own doc comment already documents governs
                    // any retry). Just makes the failure loudly visible immediately, matching
                    // UnkeyForCleanupAsync's own Critical classification for the identical condition
                    // (a real rig this call believed was keyed, whose un-key attempt failed).
                    //
                    // Round-19 correction: gated on this class actually having believed something was
                    // keyed. SetPttLockAsync is documented as always issuing the command regardless of
                    // current belief (idempotent-in-outcome) -- an operator can legitimately call
                    // SetPttLockAsync(false) on a rig this class never believed was keyed at all. Without
                    // this gate, a failed unlock of an already-unkeyed rig latched a false
                    // _pttUnkeyFailedOnRealRig for the process lifetime and made DisposeAsync's own
                    // backstop fire a spurious Critical "MAY STILL BE KEYED" for a rig that demonstrably
                    // never was -- the exact signal erosion the round-4 fix (SetPttLockAsync's
                    // successful-unlock clear) exists to prevent.
                    if (_pttLocked || _pttLeftKeyedByCall || _pttUnkeyFailedOnRealRig)
                    {
                        _pttUnkeyFailedOnRealRig = true;
                        SafeLog(() => Log.PttStillKeyedAfterFailedUnkey(_logger));
                    }

                    // Round-29 finding (risk): see the engage-direction arm's own comment above for why
                    // -- identical reasoning applies here. This is the specific direction round 18's own
                    // comment names as "must stay queued and eventually reach the rig" (CancellationToken.
                    // None on the command itself), making a late failure a real, expected outcome that
                    // previously went unobserved.
                    if (pttCommand is { IsCompleted: false })
                    {
                        _ = pttCommand.ContinueWith(
                            t => SafeLog(() => Log.CleanupStepFailed(_logger, "PTT command (finished after watchdog)", t.Exception!)),
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }

                    throw;
                }

                if (locked)
                {
                    // Round-5 finding: a NEW successful key -- see _pttKeyEpoch's own doc comment for why
                    // this must be recorded before any concurrent UnkeyForCleanupAsync/SetPttLockAsync(false)
                    // call can mistake this for the un-key it's in the middle of and wipe this call's "still
                    // keyed" state out. Round-6 finding: moved to run BEFORE `_pttLocked = locked;` below
                    // (was after) -- the old order let a concurrent un-key observe _pttLocked already true
                    // but the epoch not yet bumped, narrowly missing this call's own publish. Incrementing
                    // first closes that ordering gap.
                    Interlocked.Increment(ref _pttKeyEpoch);
                }

                _pttLocked = locked;

                if (!locked)
                {
                    // Round-6 finding: if the epoch moved while the await above was in flight, a
                    // CONCURRENT, NEWER key command (another SetPttLockAsync(true) or a PlayWithPttAsync
                    // key) completed and already recorded its own "still keyed" state -- clearing the flags
                    // below would wipe that out from under it, the identical lost-update shape round 5 fixed
                    // in UnkeyForCleanupAsync. This call's own SetPttAsync(false) still genuinely succeeded
                    // against the backend either way.
                    if (Volatile.Read(ref _pttKeyEpoch) == epochAtUnkeyStart)
                    {
                        // A CONFIRMED un-key invalidates every "still keyed" belief this class holds, not
                        // just _pttLocked -- see _pttLeftKeyedByCall's own doc comment. Placed after
                        // SetPttAsync succeeded, matching _pttLocked's own deliberate only-on-success rule.
                        _pttLeftKeyedByCall = false;

                        // Round-4 finding: this confirmed un-key invalidates _pttUnkeyFailedOnRealRig too,
                        // not just _pttLeftKeyedByCall -- without this, a PRIOR transmit's failed cleanup
                        // un-key (which set this flag and already logged Critical about it) survives a
                        // later, genuinely successful unlock through this escape hatch. DisposeAsync then
                        // still fires a spurious backstop un-key on a rig that is demonstrably off, and if
                        // THAT attempt fails for any unrelated reason (e.g. the radio is already
                        // disconnected at shutdown), it emits a false "PTT MAY STILL BE KEYED" Critical --
                        // undermining the one signal this whole chunk exists to keep trustworthy.
                        _pttUnkeyFailedOnRealRig = false;

                        // Round-26 finding: a CONFIRMED un-key -- see _pttUnkeyEpoch's own doc comment
                        // for the lost-update race this closes on the OPPOSITE direction from
                        // _pttKeyEpoch above.
                        Interlocked.Increment(ref _pttUnkeyEpoch);
                    }
                    else
                    {
                        SafeLog(() => Log.PttUnkeyRaceLostToNewerKey(_logger));
                    }
                }

                // Round-3 finding: the pre-await disposed check above (line ~256) only catches the CHEAP,
                // common case. _radioSession.SetPttAsync is a real serial/TCP round-trip -- DisposeAsync can
                // run its entire backstop (which reads _pttLocked/_keyedTransmitCompletion, neither of which
                // this call has published yet) while this await is in flight, find nothing to do, and
                // return. Left uncorrected, this call would then set _pttLocked = true above with nothing
                // left to ever un-key it. Latent today (SetPttLockAsync has zero production callers -- see
                // this method's own doc comment), fixed anyway since the fix is cheap and this method is
                // otherwise fully hardened against the same race everywhere else in this class.
                //
                // Round-4 finding: the plain `_pttLocked = locked;` write above and the `_disposed` read
                // just below are release-store/acquire-load -- the SAME StoreLoad-reordering gap
                // PlayWithPttAsync's Interlocked.Exchange publish closes (see its own comment). DisposeAsync
                // already carries the matching half (Interlocked.MemoryBarrier right after its own
                // `_disposed = true` write); this fence is the other half. `_pttLocked` is `volatile`, so
                // Interlocked.Exchange on it is CS0420 -- a standalone full fence after the plain write is
                // the equivalent. Unlike Risk B further down (whose failure mode is a stranded RX pause,
                // not a leaked keyed transmitter -- see that comment for the criterion), this pattern's
                // failure mode IS a leaked keyed transmitter, so by this file's own stated rule it needs
                // the fence Risk B deliberately goes without.
                Interlocked.MemoryBarrier();

                if (locked && _disposed)
                {
                    // Round-20 finding: guarded so a genuine ObjectDisposedException always reaches the
                    // caller below -- UnkeyForCleanupAsync's own "never throws" contract turned out to
                    // be false (see its own comment), and letting that substitute a logging-provider
                    // failure for the real disposal signal misleads any caller that branches on
                    // ObjectDisposedException specifically (this class's own established convention).
                    // The state record itself doesn't depend on this guard -- UnkeyForCleanupAsync's own
                    // round-20 fix latches _pttUnkeyFailedOnRealRig before it can throw.
                    try
                    {
                        await UnkeyForCleanupAsync(pttKeyedOnRealRig: true).ConfigureAwait(false);
                    }
                    catch (Exception recoveryEx)
                    {
                        SafeLog(() => Log.CleanupStepFailed(_logger, "PTT off (post-dispose recovery)", recoveryEx));
                    }

                    throw new ObjectDisposedException(GetType().FullName);
                }

                SafeLog(() => Log.PttLockChanged(_logger, locked));

                if (!locked && _rxPendingResumeAfterUnlock)
                {
                    _rxPendingResumeAfterUnlock = false;
                    try
                    {
                        // Round-10 finding: bounded on a FRESH CancellationTokenSource, not the
                        // caller's own `ct` -- matching PlayWithPttAsync's own `rxResumeCts` pattern
                        // (see its own comment). Without this, a wedged capture device (device
                        // enumeration, settings I/O, or _audioEngine.StartCaptureAsync itself hanging)
                        // parks this call inside `_pttLockGate` indefinitely -- stranding the PTT
                        // lock/unlock escape hatch itself, since every other SetPttLockAsync call
                        // (including a future emergency unlock) blocks on the SAME gate. Not the
                        // leaked-keyed-transmitter class (the rig is already confirmed un-keyed by the
                        // time this runs -- see this block's own `!locked` guard), but a real
                        // availability bug in the one API this whole method exists to keep working.
                        //
                        // Round-16 correction: passing rxResumeCts.Token as the CALLEE's own `ct`
                        // parameter alone does NOT bound this -- MiniAudioDeviceEnumerator.RefreshAsync
                        // and MiniAudioEngine.StartCaptureAsync both only check `ct` at their own
                        // start/lock-acquire boundary, never during the actual blocking native call, so
                        // a token cancelling mid-call does not unblock it. WaitAsync(rxResumeCts.Token)
                        // closes this: it observes the SAME token's cancellation independently of
                        // whether the awaited task itself ever polls it, which is exactly what was
                        // missing -- round 10 through round 15 all assumed this CTS alone was already a
                        // real bound.
                        //
                        // Round-18 correction: WaitAsync alone STILL isn't enough -- StartReceivingAsync
                        // bottoms out in JsonSettingsStore.LoadAsync, which does blocking
                        // File.Exists/File.OpenRead before its own first `await` (see
                        // GetStationIdTransmitOptionsAsync's own comment for the full reasoning), so a
                        // hung network-mounted settings path never even returns a genuinely-pending Task
                        // for WaitAsync to race. Task.Run offloads that synchronous prefix onto a pool
                        // thread, closing the gap regardless of what StartReceivingAsync does internally.
                        using var rxResumeCts = new CancellationTokenSource(_cleanupTimeout);
                        await ResumeReceivingBoundedAsync(rxResumeCts).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        SafeLog(() => Log.CleanupStepFailed(_logger, "Resume RX (after unlock)", ex));
                    }
                    finally
                    {
                        // User-reported gap (2026-08-18): the deferred half of PlayWithPttAsync's own
                        // pause -- see CapturePausedForTransmitChanged's own doc comment for why this
                        // fires here too, not just at PlayWithPttAsync's own immediate-resume point.
                        RaiseCapturePausedForTransmitChanged(false);
                    }
                }
            }
            finally
            {
                _pttLockGate.Release();
            }
        }
        finally
        {
            // Round-8 finding: signalled no matter how the above ended (success, un-key failure, a
            // throw from the disposal-race recovery, RX-resume failure) -- a DisposeAsync waiting on
            // this must never be left hanging until its own timeout by an unrelated failure. Same
            // CompareExchange pattern as PlayWithPttAsync's own inner finally (see its own comment):
            // only clear the field if it still points at THIS call's instance, so an overlapping
            // PlayWithPttAsync/SetPttLockAsync call can't have its own registration erased.
            if (keyedCompletion is not null)
            {
                Interlocked.CompareExchange(ref _keyedTransmitCompletion, null, keyedCompletion);
                Interlocked.Decrement(ref _keyedTransmitCount);
                keyedCompletion.TrySetResult();
            }
        }
    }

    public event Action<SstvModeDefinition>? ModeDetected
    {
        add => _decoder.ModeDetected += value;
        remove => _decoder.ModeDetected -= value;
    }

    public event Action<SstvModeDefinition>? DecodeRestarted
    {
        add => _decoder.DecodeRestarted += value;
        remove => _decoder.DecodeRestarted -= value;
    }

    public event Action<FskStationIdDecodedInfo>? StationIdDecoded
    {
        add => _decoder.StationIdDecoded += value;
        remove => _decoder.StationIdDecoded -= value;
    }

    /// <summary>See <see cref="ISstvSessionService.TransmitProgressChanged"/> for the full threading
    /// contract. Raised from <see cref="PumpToPlaybackAsync"/> via <see cref="ReportTransmitProgress"/>.</summary>
    public event Action<TransmitProgressInfo>? TransmitProgressChanged;

    public event Action<bool>? CapturePausedForTransmitChanged;

    /// <summary>Auditor-caught (round 1): the raw <c>CapturePausedForTransmitChanged?.Invoke(...)</c>
    /// calls this wraps were previously inline at each call site, unguarded -- unlike this class's own
    /// established convention for the sibling <see cref="TransmitProgressChanged"/> event
    /// (<see cref="ReportTransmitProgress"/>'s own try/catch), a throwing subscriber could propagate
    /// out of <see cref="PlayWithPttAsync"/> (leaving RX permanently stopped, since the `true` raise
    /// sits outside that method's own guarded region by design -- see this method's own call site) or
    /// out of a `finally` block, masking the real exception/original cancellation reason a caller like
    /// <c>TxControlsPaneViewModel</c> distinguishes on. Not rate-limited like
    /// <see cref="ReportTransmitProgress"/>'s own catch (that one is a genuine hot path, ~every 4096
    /// samples; this fires at most twice per transmission -- or once for a stranded deferred-lock
    /// resume, see this method's other call sites -- so a plain per-occurrence Error (matching the
    /// sibling <c>MaintenanceHandlerFailed</c>/<c>TransmitProgressHandlerFailed</c> level) cannot
    /// flood the log).</summary>
    private void RaiseCapturePausedForTransmitChanged(bool paused)
    {
        try
        {
            CapturePausedForTransmitChanged?.Invoke(paused);
        }
        catch (Exception ex)
        {
            // Round-20 finding: this method's own doc comment (see its callers) says it exists
            // specifically so a throwing subscriber can never propagate out and mask a caller's real
            // exception -- but the log call right below could ALSO throw (the same logging-provider
            // failure this whole round is about), defeating that guarantee at one remove. SafeLog
            // closes it.
            SafeLog(() => Log.CapturePausedHandlerFailed(_logger, paused, ex));
        }
    }

    public async Task<string?> GetOperatorCallsignAsync(CancellationToken ct = default)
    {
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var operatorSettings = appSettings.GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings)
            ?? new OperatorSettings();

        // Auditor code-review finding on Phase 5 (real bug, fixed): a decoded FSK station-ID
        // callsign is ALWAYS normalized by construction (every transmitter, including this port's
        // own, runs StationIdCallsignNormalizer.Normalize before sending) -- comparing it against the
        // operator's RAW stored callsign (e.g. "w1aw") would silently fail the self-filter for any
        // operator whose stored callsign isn't already uppercase/trimmed. Normalizing here, not just
        // storing it normalized in OperatorSettings, keeps the raw-as-typed value available for
        // every OTHER consumer (macros/display/QRZ) that doesn't want it force-uppercased.
        return string.IsNullOrEmpty(operatorSettings.Callsign)
            ? operatorSettings.Callsign
            : StationIdCallsignNormalizer.Normalize(operatorSettings.Callsign);
    }

    /// <summary>See <see cref="ISstvSessionService.SlantPpm"/> / <see cref="ISstvDecoder.SlantPpm"/>.</summary>
    public double? SlantPpm => _decoder.SlantPpm;

    /// <summary>See <see cref="ISstvSessionService.SyncOffsetSamples"/> / <see cref="ISstvDecoder.SyncOffsetSamples"/>.</summary>
    public int? SyncOffsetSamples => _decoder.SyncOffsetSamples;

    /// <summary>See <see cref="ISstvSessionService.SignalPeakLevel"/> / <see cref="ISstvDecoder.SignalPeakLevel"/>.</summary>
    public double SignalPeakLevel => _decoder.SignalPeakLevel;

    /// <summary>See <see cref="ISstvSessionService.IsLevelOverdriven"/> / <see cref="ISstvDecoder.IsLevelOverdriven"/>.</summary>
    public bool IsLevelOverdriven => _decoder.IsLevelOverdriven;

    /// <summary>See <see cref="ISstvSessionService.AutoSlantEnabled"/> / <see cref="ISstvDecoder.AutoSlantEnabled"/>.</summary>
    public bool AutoSlantEnabled => _decoder.AutoSlantEnabled;

    /// <summary>See <see cref="ISstvSessionService.SyncFrequencyCorrectionHz"/> / <see cref="ISstvDecoder.SyncFrequencyCorrectionHz"/>.</summary>
    public double? SyncFrequencyCorrectionHz => _decoder.SyncFrequencyCorrectionHz;

    /// <summary>See <see cref="ISstvSessionService.BufferedSampleCount"/> / <see cref="ISstvDecoder.BufferedSampleCount"/>.</summary>
    public int BufferedSampleCount => _decoder.BufferedSampleCount;

    /// <summary>See <see cref="ISstvSessionService.CaptureOverrunCount"/>. Absorbs the narrow,
    /// documented THROW race <c>MiniAudioEngine.CaptureOverrunCount</c>'s own doc comment describes
    /// (a concurrent <see cref="StopReceivingAsync"/> disposing the capture session between that
    /// property's field read and its underlying native call) -- auditor-caught: this property is
    /// polled every 250ms by <c>RxImagePaneViewModel</c>'s telemetry timer, an ordinary "user clicks
    /// Stop RX mid-poll" interleaving reachable on every real session, and a <c>DispatcherTimer</c>
    /// tick exception has nowhere safe to land (this port's own global unhandled-exception handler
    /// only logs, it doesn't recover). <c>0</c> is not a fallback value here -- it IS the documented
    /// contract value for "capture isn't running", so this doesn't hide a real failure, it just
    /// reaches the same answer a clean read would have found a moment later. <b>Tier A Batch 1
    /// re-audit round 5/6 correction: this catch does NOT absorb everything the same race can do</b>
    /// -- see <see cref="ISstvSessionService.CaptureOverrunCount"/>'s own doc comment (which this
    /// summary's first line already points to) for the BLOCK half of the race this `try`/`catch`
    /// has no way to catch: the underlying read can stall the calling thread (here, the UI thread)
    /// for as long as a concurrent capture-session `Dispose()` holds its own write lock, before
    /// this method's call even reaches the point where it could throw.</summary>
    public int CaptureOverrunCount
    {
        get
        {
            try
            {
                return _audioEngine.CaptureOverrunCount;
            }
            catch (ObjectDisposedException)
            {
                // Round-22 finding (risk): this getter's own doc comment states the reason this catch
                // exists -- polled every 250ms by RxImagePaneViewModel's telemetry timer, and a
                // DispatcherTimer tick exception has nowhere safe to land. A throwing log call (a
                // broken logging provider) used to defeat that guarantee one frame deeper.
                SafeLog(() => Log.CaptureOverrunCountRaceObserved(_logger));
                return 0;
            }
        }
    }

    public event Action? MaintenanceWarningRaised;

    public event Action? MaintenanceWarningCleared;

    public event Action? MaintenanceCriticalStopRaised;

    /// <summary>See <see cref="ISstvSessionService.RequestReSync"/>.</summary>
    public void RequestReSync()
    {
        Log.ReSyncRequested(_logger);
        _decoder.RequestReSync();
    }

    /// <summary>See <see cref="ISstvSessionService.RequestCorrectSlant"/>.</summary>
    public void RequestCorrectSlant()
    {
        Log.CorrectSlantRequested(_logger);
        _decoder.RequestCorrectSlant();
    }

    /// <summary>See <see cref="ISstvSessionService.ForceMode"/>.</summary>
    public void ForceMode(SstvModeDefinition mode)
    {
        Log.ModeForced(_logger, mode.Id);
        _decoder.ForceMode(mode);
    }

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
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.MaintenanceHandlerFailed(_logger, nameof(OnDecoderRestarted), ex));
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
            // StopReceivingAsync below never contends for anything already held -- that part is
            // confirmed (round-3 plan review).
            //
            // Round-15 correction: the earlier version of this comment additionally claimed the
            // GetAwaiter().GetResult() below was "confirmed safe" via that same round-3 tracing.
            // That tracing covered only the decoder swap lock above, not this synchronous block --
            // MiniAudioEngine's OWN doc comments (ClaimCaptureSessionAsync's round-3-engine-review
            // fix, DisposeCaptureSessionAsync's own doc comment) are the authoritative, currently-
            // maintained source on whether a drain-thread-originated synchronous re-entrant call like
            // this one can deadlock -- re-check those directly rather than trusting this comment's own
            // conclusion, which was never independently verified against them and can go stale as
            // that class evolves on its own schedule. Not re-verified as part of this chunk (out of
            // scope -- MiniAudioEngine is a different project); flagging the overstated claim, not
            // fixing or re-confirming the underlying question.
            StopReceivingAsync().GetAwaiter().GetResult();
            _maintenanceWarningActive = false;
            // Round-22 finding (nit): a throwing log call here used to skip Invoke() below entirely
            // (sequenced after it) -- RX would already be force-stopped with the UI never told why.
            SafeLog(() => Log.MaintenanceCriticalStop(_logger));
            MaintenanceCriticalStopRaised?.Invoke();
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.MaintenanceHandlerFailed(_logger, nameof(OnDecoderRestartCriticallyOverdue), ex));
        }
    }

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

        if (_isReceiving)
        {
            return;
        }

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
        _decoder.StationIdDecodeEnabled = stationIdSettings.FskIdRxEnabled;

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

        _audioEngine.SamplesCaptured += _decoderHandler;
        _audioEngine.SamplesCaptured += _waterfallHandler;
        _isReceiving = true;

        // ultracode audit finding #6: legacy resets its AGC (CLVL::Init) at every TX<->RX transition
        // (Sound.cpp:398,443) -- this is that transition point on the RX-resuming side.
        //
        // Round-21 finding (risk 6/6b): previously unguarded -- a throwing ResetAgc() propagated out
        // of this method even though capture had already genuinely started and _isReceiving was
        // already latched true above, and skipped Log.RxStarted below. Swallow-and-log instead: the
        // capture-started state is real regardless of whether AGC reset itself succeeded.
        try
        {
            _decoder.ResetAgc();
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "ResetAgc (RX start)", ex));
        }

        // Round-22 finding (nit): round 21's own ResetAgc() fix above justified itself partly as "no
        // longer skips Log.RxStarted below" -- but this call itself was still unwrapped, so a throwing
        // provider still propagated out of this method after capture had genuinely started (swallowed
        // on the RX-resume path via TryCleanupAsync, but not on the direct UI Start-RX path).
        SafeLog(() => Log.RxStarted(_logger, device.Id, _decoder.SampleRate));
    }

    public async Task StopReceivingAsync()
    {
        if (!_isReceiving)
        {
            return;
        }

        _audioEngine.SamplesCaptured -= _decoderHandler;
        _audioEngine.SamplesCaptured -= _waterfallHandler;
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
            // consulted. The watchdog only does real work for a NON-drain-thread caller (the two
            // production callers via StartReceivingAsync's own guard: PlayWithPttAsync's entry, and
            // DisposeAsync).
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

        SafeLog(() => Log.RxStopped(_logger));
    }

    public async Task TransmitAsync(SstvModeDefinition mode, IImageSource image, CancellationToken ct = default)
    {
        Log.TxStarting(_logger, mode.Id, image.Width, image.Height);
        var stationId = await GetStationIdTransmitOptionsAsync(ct).ConfigureAwait(false);

        // Plan-review finding: MUST reuse this SAME resolved stationId for the estimate below, not
        // re-resolve it -- MacroTextResolver's CW-ID text can be time-dependent (DateTime.UtcNow), so
        // two independent resolutions aren't guaranteed to produce the same footer duration, which
        // would make the estimate silently disagree with what EncodeAsync actually emits.
        //
        // Code-review finding: EstimateSampleCount is a real traversal of every scanline segment
        // (its own doc comment says so), not O(1) metadata math -- Task.Run keeps it off whichever
        // thread called TransmitAsync (the caller's own await above may not have yielded at all, e.g.
        // JsonSettingsStore.LoadAsync returns synchronously when no settings file exists yet), so a
        // large image's estimate can't delay PTT keying/RX pause by running inline on the UI thread.
        var totalSamplesEstimate = await Task.Run(() => _encoder.EstimateSampleCount(mode, image, stationId), ct).ConfigureAwait(false);
        await PlayWithPttAsync(_encoder.EncodeAsync(mode, image, stationId, ct), _encoder.SampleRate, ct, totalSamplesEstimate: totalSamplesEstimate).ConfigureAwait(false);
    }

    /// <summary>Resolves the CW-ID/FSK station-ID settings + operator identity into one fully-formed
    /// <see cref="StationIdTransmitOptions"/> -- see that type's own doc comment for why this
    /// resolution lives here (Application layer) rather than inside the pure-DSP encoder. Settings-
    /// boundary validation for WPM/tone-frequency lives here too (Phase 1 code-review finding): a
    /// corrupted/hand-edited settings.json with WPM &lt;= 0 would otherwise make
    /// <c>CwMorseGenerator.MillisecondsPerDotFromWpm</c> return Infinity/negative -- this falls back
    /// to the documented default instead of letting a bad value reach the generator or abort an
    /// in-flight transmission. Also the read-only preview <see cref="ISstvSessionService.GetStationIdTransmitOptionsAsync"/>
    /// exposes (see that member's own doc comment) -- <see cref="TransmitAsync"/> and that preview
    /// path share this exact same resolution, so they can never disagree with each other.</summary>
    public async Task<StationIdTransmitOptions> GetStationIdTransmitOptionsAsync(CancellationToken ct = default)
    {
        // Round-15 finding (discovered while testing finding 3, not itself in the auditor's report):
        // same unbounded-external-read shape as ResolveDeviceAsync/GetTxVolumePercentAsync/
        // LoadAudioSettingsAsync inside PlayWithPttAsync -- but reached from TransmitAsync's own
        // preamble, BEFORE PlayWithPttAsync (and its _transmitInFlight guard) is ever entered. Lower
        // severity than finding 3 (a hang here does NOT strand _transmitInFlight, since it hasn't
        // been acquired yet -- a concurrent second TransmitAsync/TuneAsync call is unaffected), but
        // still a real unbounded wait on a settings read (e.g. a config file on a hung network mount)
        // with no caller-visible way to bound it on the TuneAsync-with-no-ct production path. Same
        // fix, same reasoning.
        //
        // Round-18 finding 4 (round-17's own deferral (a), now fixed): WaitAsync alone cannot bound
        // this -- JsonSettingsStore.LoadAsync does blocking File.Exists/File.OpenRead BEFORE its own
        // first `await` (this file's own EstimateSampleCount comment already states this exact fact,
        // just never connected it to the WaitAsync bounds above), so on a hung network-mounted
        // settings path the call doesn't even return a genuinely-pending Task for WaitAsync to race --
        // the calling thread blocks synchronously before WaitAsync is ever reached. Task.Run offloads
        // that synchronous prefix onto a pool thread, so the awaited Task really is pending
        // immediately and WaitAsync's bound becomes real. Cost: a wedged mount leaks one abandoned
        // pool thread per call using this pattern -- bounded and slow-growing in practice, since this
        // path (unlike PlayWithPttAsync's own) isn't behind _transmitInFlight.
        var appSettings = await Task.Run(() => _settingsStore.LoadAsync(ct), ct).WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);
        var stationIdSettings = appSettings.GetSection(StationIdSettings.SectionKey, StationIdSettingsJsonContext.Default.StationIdSettings)
            ?? new StationIdSettings();
        var operatorSettings = appSettings.GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings)
            ?? new OperatorSettings();

        // Main.cpp:6969's !sys.m_CWIDText.IsEmpty() -- checked on the RAW (pre-macro) text, matching
        // legacy exactly (a macro token that resolves to empty would still fire in legacy, since the
        // gate never re-checks after MacroText expansion).
        var cwEnabled = stationIdSettings.CwIdMode == CwIdMode.Cw && !string.IsNullOrEmpty(stationIdSettings.CwText);
        var wpm = stationIdSettings.CwWpm is > 0 ? stationIdSettings.CwWpm.Value : StationIdSettings.DefaultCwWpm;
        var toneFrequencyHz = stationIdSettings.CwToneFrequencyHz is > 0
            ? stationIdSettings.CwToneFrequencyHz.Value
            : StationIdSettings.DefaultCwToneFrequencyHz;
        var nrRstEnabled = stationIdSettings.NrRstEnabled ?? StationIdSettings.DefaultNrRstEnabled;

        // Auditor code-review finding on Phase 4 (real, low-severity, and refined in round 2): legacy
        // caps the MACRO-RESOLVED CW-ID text at 77 chars for plain (non-macro) literal text --
        // `MacroText`'s own break condition (`Main.cpp:10829`, `if (n >= (size-1)) break;` with
        // `size = sizeof(bf)-2 = 78`) stops once `n` reaches 77, not 78; `sizeof(bf)-2` is the buffer
        // SIZE passed in, not the actual max character count written. A macro token can overshoot
        // this (`n += l` for a multi-char expansion checked only AFTER appending) -- legacy itself can
        // write past `bf[80]` in that case, a real legacy buffer bug this port has no reason to
        // reproduce; 77 is deliberately chosen as the exact literal-text limit, not an attempt to
        // replicate the macro-overshoot case. An uncapped resolved text doesn't corrupt anything here
        // either way (CwMorseGenerator has no buffer to overrun), just runs a longer Morse tail than
        // legacy would have sent for the same configured literal text -- capped to match legacy's
        // actual on-air behavior instead of silently diverging.
        const int maxCwResolvedTextLength = 77;
        var cwResolvedText = cwEnabled ? _macroTextResolver.Resolve(stationIdSettings.CwText!, operatorSettings) : string.Empty;
        if (cwResolvedText.Length > maxCwResolvedTextLength)
        {
            cwResolvedText = cwResolvedText[..maxCwResolvedTextLength];
        }

        return new StationIdTransmitOptions
        {
            CwEnabled = cwEnabled,
            CwResolvedText = cwResolvedText,
            CwToneFrequencyHz = toneFrequencyHz,
            CwWpm = wpm,
            FskIdEnabled = stationIdSettings.FskIdTxEnabled,
            Callsign = operatorSettings.Callsign ?? string.Empty,
            NrRstText = nrRstEnabled ? stationIdSettings.NrRstText : null,
        };
    }

    // Round-18 finding 3 (round-17's own deferral (b), now fixed): a generous backstop, not a UX
    // limit -- the default UI value is 5s and legitimate antenna-tuning use is on that order. Exists
    // only to bound the worst case of a corrupted/mistyped duration reaching TuneAsync below with no
    // caller-side cancellation available (see that method's own comment).
    private static readonly TimeSpan MaxTuneDuration = TimeSpan.FromMinutes(5);

    public Task TuneAsync(double frequencyHz, TimeSpan duration, bool leaveKeyedAfterTune = false, CancellationToken ct = default)
    {
        const int sampleRate = 48_000;

        // Round-18 finding 3: neither parameter was validated at all -- unlike TxVolumePercent
        // (round 16's precedent, same threat model of an unvalidated value reaching the transmitter),
        // these are direct caller arguments with a live caller that already catches and surfaces a
        // failure (RadioStatusViewModel's own RadioStatus.Error.TuneFailed), so this throws rather
        // than silently clamping -- the caller needs to know its own value was wrong, not have it
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

        // Round-16 finding: unlike GetStationIdTransmitOptionsAsync's own WPM/tone-frequency
        // boundary validation (see that method's own comment, same threat model: a corrupted/hand-
        // edited settings.json), this value flowed straight into PlayWithPttAsync's `gain` multiplier
        // with no range check at all -- an out-of-range value (e.g. a typo'd 10000, or a negative
        // number) reaches PumpToPlaybackAsync's unclamped `sample * gain` directly, hard-clipping the
        // transmitted audio into a square wave (real-world splatter risk on an actual transmitter) or
        // inverting phase. Clamped here so every reader gets a safe value regardless of what's on
        // disk.
        return Math.Clamp(settings.TxVolumePercent ?? 100, 0, 100);
    }

    public async Task SetTxVolumePercentAsync(int percent, CancellationToken ct = default)
    {
        // Round-16 finding: clamped on write too, not just on read -- GetTxVolumePercentAsync's own
        // clamp already makes an out-of-range value on disk safe to READ, but leaving it unclamped
        // here would still let a bogus value silently reach disk via this API's own normal use (e.g.
        // a UI control with a bug, or a scripted settings import), for GetTxVolumePercentAsync to mask
        // again on every future read -- clamping at the write boundary keeps what's actually stored
        // consistent with what every reader promises.
        percent = Math.Clamp(percent, 0, 100);

        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var current = appSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings)
            ?? new AudioDeviceSettings();
        var updated = appSettings.WithSection(
            AudioDeviceSettings.SectionKey,
            current with { TxVolumePercent = percent },
            AudioSettingsJsonContext.Default.AudioDeviceSettings);
        await _settingsStore.SaveAsync(updated, ct).ConfigureAwait(false);
        Log.TxVolumeSet(_logger, percent);
    }

    /// <summary>Shared bounded-step budget reused across this class's various cleanup/safety-critical
    /// awaits (un-key, RX-resume-after-unlock, the round-14 key-command/StartPlaybackAsync bounds) --
    /// see each call site's own comment for why that particular step needs one.</summary>
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    // Blocker 1 (Tier A Batch 3 chunk 3a). IAudioEngine.StopPlaybackAsync takes no CancellationToken
    // -- it cannot be cancelled, only stopped waiting on -- and the real MiniAudioEngine can take
    // ~10.2s worst case on a wedged output device (MiniAudioEngine.DrainTimeout 5s +
    // MiniAudioPlaybackSession.CloseTimeout 5s + DrainTailMargin 200ms). Sharing ONE 5s deadline
    // across StopPlayback-then-un-key therefore meant the un-key's token was already cancelled by the
    // time it ran, and both real protocol backends honor the token at their lock-acquire gate
    // (HamlibRadioProtocol.cs, RigctldClientProtocol.cs) -- so PTT-off was never even attempted on the
    // exact hardware failure where it matters most. This is how long we WAIT before moving on to the
    // un-key; generous enough that a healthy drain (ring ~0.37s + 200ms tail margin) always finishes
    // inside it, so normal transmissions are bit-for-bit unaffected.
    private static readonly TimeSpan PlaybackStopWaitBudget = TimeSpan.FromSeconds(5);

    // Blocker 3: how long DisposeAsync waits for an in-flight keyed transmit's OWN cleanup before
    // force-un-keying itself. Originally sized against ScanlineStudio.Host/Program.cs's 10s total
    // host-teardown bound as this (3s) + the backstop un-key's own CleanupTimeout (5s) = 8s worst
    // case.
    //
    // Round-22 finding (nit): that arithmetic is now stale -- round 16 added a 5s StopCapture
    // watchdog inside StopReceivingAsync, which DisposeAsync also calls, bringing the real worst case
    // to roughly 13s, plus Waterfall.Dispose()/Decoder.Dispose() (both unbounded, out of this chunk's
    // scope). No PTT consequence today -- the backstop un-key is deliberately ordered first and
    // completes well inside its own budget regardless of what runs after it -- but a future round
    // sizing a NEW budget against this comment's original "8s worst case" claim would be misled.
    private static readonly TimeSpan InFlightKeyedTransmitWait = TimeSpan.FromSeconds(3);

    // Round-13 finding: EnqueueAllAsync's own "buffer full, wait 10ms, retry" loop (see its own
    // comment) had no bound at all -- a fully-wedged playback device (driver stall, device
    // removed mid-transmission -- the same "wedged output device" class StopPlaybackWithWatchdogAsync
    // already treats as a real failure mode) left the ring permanently full, `accepted` permanently
    // 0, and the loop spinning forever WITH PTT STILL KEYED. Round-14 nit: this only catches a device
    // that accepts literally ZERO samples per attempt -- a device accepting a nonzero trickle far
    // below real-time (a severe rate mismatch or a slow virtual/loopback sink) resets the stall timer
    // on every partial accept and is not caught by this budget at all. Accepted as out of this fix's
    // stated scope; a whole-transmission bound derived from totalSamplesEstimate would be the fix for
    // that broader case, not attempted here. TuneAsync's real production caller
    // (RadioStatusViewModel) passes no CancellationToken at all and has no Stop command, so on that
    // path there was no way to interrupt it short of process exit -- and round 12's own
    // _transmitInFlight guard means this hang now ALSO permanently blocks every subsequent
    // Transmit/Tune for the rest of the process, not just the stuck one. This is how long
    // EnqueueAllAsync waits with zero progress before concluding the device is wedged and aborting
    // (throwing, which routes through PlayWithPttAsync's own generic catch -> abnormalTermination ->
    // immediate urgent un-key, the same bounded recovery path every other failure on this method
    // already uses).
    private static readonly TimeSpan PlaybackStallTimeout = TimeSpan.FromSeconds(5);

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
    private async Task PlayWithPttAsync(IAsyncEnumerable<float> samples, int sampleRate, CancellationToken ct, bool leaveKeyedAfterCall = false, long? totalSamplesEstimate = null)
    {
        // Round-12 finding: single-flight guard, checked before ANYTHING else -- no RX pause, no
        // device resolution, no PTT touched. See _transmitInFlight's own doc comment for the failure
        // this closes: a second overlapping PlayWithPttAsync call used to silently re-key and tear
        // down the FIRST call's own live playback session instead of being rejected outright.
        if (Interlocked.CompareExchange(ref _transmitInFlight, 1, 0) != 0)
        {
            throw new InvalidOperationException("A transmit or tune is already in progress.");
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

            // Blocker 3: this call's own handle into _keyedTransmitCompletion. Local as well as field so
            // the finally can clear the field ONLY if it still points at this call's own instance.
            TaskCompletionSource? keyedCompletion = null;

            try
            {
                // Round-27 finding (risk): pttKeyedOnRealRig's own round-25 baseline used to read AND
                // ASSIGN from _pttLocked via pttLockedAtEntry, both of which happen LATE -- just before
                // the rigIsRealAtKeyTime branch, AFTER the three bounded device/settings awaits below.
                // Correct for pttLockedAtEntry's OWN purpose (deciding whether to double-key an
                // already-engaged lock, which must see the freshest possible state), but wrong for
                // baselining pttKeyedOnRealRig: a failure in any of the three awaits below (device not
                // found, no device configured, or the WaitAsync timeout itself -- all realistic)
                // reached the generic catch with pttKeyedOnRealRig still at its OUTER false default,
                // since the code never reached the late assignment -- even when a lock was ALREADY
                // engaged at this call's own true entry, reproducing verbatim the harm round 25 exists
                // to prevent (no Critical, no retry, just a Debug "no radio configured" line). Both the
                // read AND the assignment happen HERE instead, before those awaits -- the read into a
                // separate local from pttLockedAtEntry (kept late, for its own purpose), the assignment
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
                pttKeyedOnRealRig = _pttLocked || _pttLeftKeyedByCall || _pttUnkeyFailedOnRealRig;

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
                var gain = (await Task.Run(() => GetTxVolumePercentAsync(ct), ct).WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false)) / 100f;
                var audioSettings = await Task.Run(() => LoadAudioSettingsAsync(ct), ct).WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);

                // Guarded on RigId ("none" = the null-object "no radio" backend, spec/18-path-to-1.0.md
                // Critical item 1), not Capabilities -- see IRadioController.RigId's own doc comment for
                // why a live-capability check would be unsafe here (real backends connect lazily, so
                // Capabilities reads None during a real window even with a genuine PTT-capable rig
                // configured). Read into a local exactly once: the old code's "RigId is stable so there's
                // no was-available-at-entry-gone-by-cleanup scenario" claim was FALSE (see
                // pttKeyedOnRealRig above), and this is the single read that claim is now replaced by.
                var pttLockedAtEntry = _pttLocked;
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

                // Round-26 finding (nit, flagged not fixed): when pttLockedAtEntry is true but
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
                    // pttLockedAtEntry case too: this call didn't key the rig, but the rig IS keyed for
                    // the whole duration of this call either way.
                    keyedCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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
                    Interlocked.Exchange(ref _keyedTransmitCompletion, keyedCompletion);

                    // Round-6 finding: see _keyedTransmitCount's own doc comment for why nullness of the
                    // field above is not enough once two PlayWithPttAsync calls can overlap.
                    Interlocked.Increment(ref _keyedTransmitCount);

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
                        // independent of this call, and pttLockedAtEntry covers exactly that -- keeps the
                        // cleanup un-key's Critical-vs-Debug classification honest in the finally below.
                        // Deliberately the LATE read here -- this is exactly the "don't double-key/
                        // misclassify an already-engaged lock" decision that local is designed for, and
                        // the freshest possible observation is correct for it.
                        // (Round 27 note: no longer necessarily the same value the baseline near this
                        // method's own entry assigned -- that one now deliberately uses the EARLY
                        // `_pttLocked` read taken at entry instead, so the two can genuinely differ if
                        // _pttLocked changed during this method's own device/settings awaits.)
                        pttKeyedOnRealRig = pttLockedAtEntry;
                        // Round-4 nit: GetType().FullName, not nameof(SstvSessionService), to match the
                        // ObjectName ObjectDisposedException.ThrowIf(_disposed, this) produces elsewhere in
                        // this class -- both throw sites should report the same object identity.
                        throw new ObjectDisposedException(GetType().FullName);
                    }
                }

                if (!pttLockedAtEntry)
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
                        try
                        {
                            await _radioSession.SetPttAsync(true, ct).WaitAsync(_cleanupTimeout, ct).ConfigureAwait(false);
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
                            Interlocked.Increment(ref _pttKeyEpoch);
                            throw;
                        }

                        // Round-5 finding: see _pttKeyEpoch's own doc comment.
                        Interlocked.Increment(ref _pttKeyEpoch);
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
                keyEpochAfterOwnKeyAttempt = Volatile.Read(ref _pttKeyEpoch);

                // Round-28 finding (blocker): unkeyEpochAfterOwnKeyAttempt now read HERE too, not at method entry
                // -- see its own comment (near pttLockedAtEntry, above) for why the entry-time read was
                // itself the bug. Reading both epochs at this SAME point (immediately after this call's
                // own key phase, no await between the two reads) means the pair now correctly answers
                // "since MY OWN key: did anyone confirm off, and did anyone re-key" -- not two questions
                // anchored to different, unrelated points in time.
                unkeyEpochAfterOwnKeyAttempt = Volatile.Read(ref _pttUnkeyEpoch);

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
                await PumpToPlaybackAsync(samples, gain, sampleRate, totalSamplesEstimate, ct).ConfigureAwait(false);
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
                    var pttLockedAtCleanup = _pttLocked;

                    // Only a NORMAL completion honors "stay keyed" (leaveKeyedAfterCall/lock) -- see this
                    // method's own doc comment for why an abnormal termination always overrides both.
                    var skipUnkeyAndRxResume = !abnormalTermination && (leaveKeyedAfterCall || pttLockedAtCleanup);

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
                        if (pttKeyedOnRealRig && Volatile.Read(ref _pttUnkeyEpoch) != unkeyEpochAfterOwnKeyAttempt
                            && Volatile.Read(ref _pttKeyEpoch) == keyEpochAfterOwnKeyAttempt)
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
                        if (Volatile.Read(ref _pttUnkeyEpoch) == unkeyEpochAfterOwnKeyAttempt
                            || Volatile.Read(ref _pttKeyEpoch) != keyEpochAfterOwnKeyAttempt)
                        {
                            _pttLeftKeyedByCall = true;
                        }
                        else
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

                    if (!skipUnkeyAndRxResume)
                    {
                        // Auditor-caught (round 2): a PRIOR locked call may have already stopped capture
                        // and set _rxPendingResumeAfterUnlock before THIS call force-unkeyed on an
                        // abnormal termination -- if so, THIS call's own `wasReceiving` is false, so the
                        // `if (wasReceiving)` block below would never see it, leaving RX stopped forever
                        // with IsPttLocked already reporting false. Guarded on `!wasReceiving` so this
                        // never double-fires alongside that block's own resume for THIS call.
                        if (!wasReceiving && _rxPendingResumeAfterUnlock)
                        {
                            _rxPendingResumeAfterUnlock = false;
                            await TryCleanupAsync("Resume RX (stranded lock pending-resume)", () => ResumeReceivingBoundedAsync(rxResumeCts)).ConfigureAwait(false);
                            RaiseCapturePausedForTransmitChanged(false);
                        }
                    }

                    if (wasReceiving)
                    {
                        if (!skipUnkeyAndRxResume)
                        {
                            await TryCleanupAsync("Resume RX", () => ResumeReceivingBoundedAsync(rxResumeCts)).ConfigureAwait(false);
                            // User-reported gap (2026-08-18): fires once the resume attempt has finished,
                            // success or failure -- this event is "no longer paused FOR THIS transmission,"
                            // not a restatement of IsReceiving itself (see the event's own doc comment).
                            RaiseCapturePausedForTransmitChanged(false);
                        }
                        else if (!abnormalTermination && pttLockedAtCleanup)
                        {
                            // Specifically the lock case, not leaveKeyedAfterCall -- a lock can stay
                            // engaged indefinitely with no automatic next step. RX must resume once
                            // SetPttLockAsync(false) eventually un-keys, not be silently forgotten.
                            _rxPendingResumeAfterUnlock = true;

                            // Risk B, publish-then-recheck. A SetPttLockAsync(false) that completed
                            // between the snapshot above and this write already ran its own deferred-resume
                            // check against a still-false flag, so nothing would ever consume what we just
                            // set -- RX stranded stopped forever. Re-reading _pttLocked AFTER publishing
                            // NARROWS that window (does not fully close it -- round-4 finding: this is a
                            // plain volatile write/read pair, the same StoreLoad-reordering gap the
                            // Interlocked.Exchange publish in PlayWithPttAsync closes for the PTT-keyed case;
                            // see that comment for the mechanism). Left un-fenced here deliberately: this
                            // pattern's failure mode is a stranded RX pause, not a leaked keyed transmitter
                            // -- a real but lower-severity bug than what the fenced pattern protects against,
                            // and one a user can recover from by toggling RX again. Both sides test the flag
                            // before consuming it, so the residual interleaving beyond the narrowed window is
                            // usually a harmless duplicate resume (StartReceivingAsync's `if (_isReceiving)
                            // return;` early-return, and a second CapturePausedForTransmitChanged(false) is
                            // idempotent for every subscriber). Round-5 finding: "usually," not always --
                            // StartReceivingAsync's own _isReceiving check-then-act has an unrelated,
                            // separately-tracked concurrent-double-subscription gap of its own (queued, not
                            // fixed here -- its failure mode is corrupted RX decode, not a leaked keyed
                            // transmitter, so it's out of this chunk's failure class); if BOTH resume paths
                            // land inside that gap's own multi-await window, the early-return doesn't hold
                            // and this "harmless" claim doesn't either. Still an accepted trade for the same
                            // stated reason above (out of this chunk's failure class) -- not upgraded to
                            // "always harmless." Round-11 correction: that gap is NOT user-recoverable by
                            // toggling RX, contrary to what an earlier version of this comment implied --
                            // StopReceivingAsync's `-=` removes only ONE copy of each duplicated handler, so
                            // the extra subscription survives a Stop RX / Start RX cycle and keeps doubling
                            // every captured chunk into the decoder for the rest of the process's life.
                            // Round-17 nit: this specific claim may be overstated against the CURRENT
                            // MiniAudioEngine, not re-verified here (out of this chunk's own failure
                            // class, as stated above) -- that implementation publishes _captureSession
                            // under its own lock before release and a second StartCaptureAsync throws
                            // "already started" before this class's own `+=` is ever reached, which
                            // would prevent the double-subscription this comment describes. Flagging the
                            // possible overstatement, not asserting the underlying gap is closed --
                            // unverified against any OTHER IAudioEngine implementation.
                            if (!_pttLocked && _rxPendingResumeAfterUnlock)
                            {
                                _rxPendingResumeAfterUnlock = false;
                                await TryCleanupAsync("Resume RX (unlock raced cleanup)", () => ResumeReceivingBoundedAsync(rxResumeCts)).ConfigureAwait(false);
                                RaiseCapturePausedForTransmitChanged(false);
                            }
                        }
                        else
                        {
                            // Auditor-caught (round 1): the residual case -- leaveKeyedAfterCall=true and
                            // NOT locked (TuneAsync's own unwired "stay keyed" option). RX intentionally
                            // stays stopped (nobody's job to resume it), but THIS transmission's own pause
                            // window is over either way, so the event must not stay stuck `true` forever
                            // with no `false` ever coming (a real latent bug: harmless today since no
                            // production caller sets leaveKeyedAfterCall=true, but would permanently
                            // freeze the Receiving indicator dimmed the moment one did).
                            RaiseCapturePausedForTransmitChanged(false);
                        }
                    }
                }
                finally
                {
                    // Blocker 3: signalled no matter how the cleanup above ended (including a throw from
                    // a subscriber or a cleanup step) -- a DisposeAsync waiting on this must never be left
                    // hanging until its own timeout by an unrelated failure. CompareExchange, not a plain
                    // null write: only clear the field if it still points at THIS call's instance, so an
                    // overlapping PlayWithPttAsync call (round-6 finding: a REAL production interleaving,
                    // not pathological -- see _keyedTransmitCount's own doc comment) can't have its own
                    // registration erased.
                    if (keyedCompletion is not null)
                    {
                        Interlocked.CompareExchange(ref _keyedTransmitCompletion, null, keyedCompletion);
                        // Round-6 finding: see _keyedTransmitCount's own doc comment.
                        Interlocked.Decrement(ref _keyedTransmitCount);
                        keyedCompletion.TrySetResult();
                    }
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
    private async Task<bool> UnkeyForCleanupAsync(bool pttKeyedOnRealRig)
    {
        // Round-5 finding: snapshotted BEFORE the un-key attempt below, not after -- see _pttKeyEpoch's
        // own doc comment for the lost-update race this closes. Volatile.Read pairs with the
        // Interlocked.Increment at every successful key site.
        var epochAtUnkeyStart = Volatile.Read(ref _pttKeyEpoch);

        // Round-20 finding: assume-failed-until-confirmed, latched BEFORE the attempt below, not
        // after. Round 19's own "write before the log call" reorder helped, but not enough --
        // TryUnkeyPttAsync's OWN "never throws" contract turned out to be false too (its own doc
        // comment now explains why), and that throw originates INSIDE the await below, before this
        // method ever reaches its own post-await write. Latching here instead means the state record
        // no longer depends on TryUnkeyPttAsync returning at all, let alone returning successfully --
        // the confirmed-success branch just below still correctly clears this on a genuine success,
        // or leaves it set (correctly) if a race with a newer key means this call's own view is
        // already stale.
        if (pttKeyedOnRealRig)
        {
            _pttUnkeyFailedOnRealRig = true;
        }

        using var unkeyCts = new CancellationTokenSource(_cleanupTimeout);
        if (await TryUnkeyPttAsync(pttKeyedOnRealRig, unkeyCts.Token).ConfigureAwait(false))
        {
            // Round-5 finding: if the epoch moved while the await above was in flight, a CONCURRENT,
            // NEWER key command completed and already recorded its own "still keyed" state -- clearing
            // the flags below would wipe that out from under it (rig genuinely re-keyed, every
            // shutdown-backstop flag reading false). This un-key command still genuinely succeeded
            // against the backend (hence `return true` unconditionally below), but the state it would
            // normally confirm is now stale; skip both the clears and the success log for that case.
            if (Volatile.Read(ref _pttKeyEpoch) == epochAtUnkeyStart)
            {
                // Force-release: whether or not a lock was engaged, PTT is now confirmed physically off
                // -- IsPttLocked must never report true once that's true.
                _pttLocked = false;
                _pttLeftKeyedByCall = false;
                _pttUnkeyFailedOnRealRig = false;

                // Round-26 finding: a CONFIRMED un-key -- see _pttUnkeyEpoch's own doc comment for the
                // lost-update race this closes on the OPPOSITE direction from _pttKeyEpoch above.
                Interlocked.Increment(ref _pttUnkeyEpoch);
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
        // Round-17 finding (the single most safety-critical await in this file): passing `ct`
        // (UnkeyForCleanupAsync's own fresh unkeyCts.Token) as this call's OWN parameter never
        // actually bounded it -- the identical mistake round 16 found and fixed at every RX-resume
        // site, one call away from the file's whole reason for existing.
        //
        // Round-18 correction: round 17's own fix bounded the WAIT but not the COMMAND -- `ct` was
        // *also* passed to SetPttAsync itself, so once the budget expired the un-key was CANCELLED AT
        // THE BACKEND'S REQUEST GATE and never actually reached the rig, rather than staying queued
        // behind a wedged key command and reaching it once that clears -- verbatim the scenario
        // PlayWithPttAsync's own blocker-1 doc comment already names as the one that matters most
        // ("PTT-off was never even attempted on the exact hardware failure where it matters most").
        // Decoupled here: the command itself now gets CancellationToken.None (never cancelled, so it
        // stays queued and eventually reaches the rig once the gate frees), while the WAIT is still
        // bounded by `ct` so this method still returns on time either way.
        Task unkeyTask;
        try
        {
            unkeyTask = _radioSession.SetPttAsync(false, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A synchronous throw (e.g. the null-object NoneRadioProtocol) never produces a Task at
            // all.
            // Round-20 finding: this method's own doc/callers assumed it "never throws" -- it did,
            // via these very log calls, when the logging provider itself failed. SafeLog closes that
            // specific hole (see its own doc comment).
            if (pttKeyedOnRealRig)
            {
                SafeLog(() => Log.CleanupStepFailed(_logger, "PTT off", ex));
            }
            else
            {
                SafeLog(() => Log.PttUnkeySkippedNoRadio(_logger));
            }

            return false;
        }

        try
        {
            await unkeyTask.WaitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // Round-20 finding: this method's own doc/callers assumed it "never throws" -- it did,
            // via these very log calls, when the logging provider itself failed. SafeLog closes that
            // specific hole (see its own doc comment).
            if (pttKeyedOnRealRig)
            {
                SafeLog(() => Log.CleanupStepFailed(_logger, "PTT off", ex));
            }
            else
            {
                SafeLog(() => Log.PttUnkeySkippedNoRadio(_logger));
            }

            // Round-26 finding (risk): every OTHER abandoned task in this class attaches a
            // fault-observer continuation to its own abandoned task (StopReceivingAsync,
            // StopPlaybackWithWatchdogAsync, ResumeReceivingBoundedAsync) -- this one, the single most
            // safety-critical await in the file (see this method's own doc comment), did not. Round
            // 18's own design deliberately gives the command CancellationToken.None so it "stays
            // queued and eventually reaches the rig once the gate frees" once WaitAsync gives up above
            // -- but with no observer, that eventual FAILURE surfaced only as an unobserved task
            // exception, unrecoverable information on the exact path this whole chunk exists to keep
            // observable. Gated on the task genuinely still being abandoned (not yet completed) the
            // same way those three sites gate it -- if unkeyTask is already complete by the time this
            // catch runs, its own fault already propagated through this same WaitAsync call as `ex`
            // above, so there is nothing left to observe later. Matches those sites' own accepted
            // trade-off of only observing a LATE FAILURE, not a late success -- a late success remains
            // unlogged, same as everywhere else in this class.
            if (!unkeyTask.IsCompleted)
            {
                _ = unkeyTask.ContinueWith(
                    t => SafeLog(() => Log.CleanupStepFailed(_logger, "PTT off (finished after watchdog)", t.Exception!)),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return false;
        }
    }

    /// <summary>Round-20 finding: this class's own cleanup/dispose paths, across rounds 17-19, all
    /// assumed logging a failure could itself never fail -- a throwing logging provider (a file logger
    /// on a full disk, the realistic source this chunk has repeatedly treated as in-scope) proved that
    /// wrong at <see cref="TryUnkeyPttAsync"/>, defeating every guard built on top of it. Used at every
    /// log call this class's own cleanup/dispose/catch paths reach -- round 21 finding: round 20's own
    /// claim that this was already true was itself false (round 19 and round 20 each thought they'd
    /// found "the" instance of the never-throws-callee pattern and were each wrong one frame deeper);
    /// round 21 did a full enumeration of every <c>Log.*</c> call site in the file and applied this
    /// everywhere a throw could skip a safety-relevant step or escape into a background/drain thread
    /// with no isolation of its own. Given that round 19 and round 20 each made this same
    /// "comprehensive" claim and were each subsequently found incomplete, treat "applied everywhere"
    /// above as a snapshot of round 21's own sweep, not a closed guarantee -- a future round finding
    /// one more unwrapped <c>Log.*</c> call inside a catch/cleanup/fault-observer path on this class's
    /// PTT-safety-relevant chain would not be a surprise; keep checking rather than trusting this
    /// comment.
    ///
    /// Round-21 finding 7: NOT silent-only anymore. A totally broken logging provider (this whole
    /// mechanism's own threat model) used to leave an operator with ZERO indication a transmitter
    /// might still be keyed -- "safe" and "silent" are not the same thing for a Critical "PTT MAY
    /// STILL BE KEYED" message. Falls back to stderr, bypassing the broken <see cref="ILogger"/>
    /// entirely, so a broken PROVIDER specifically still surfaces something. That fallback is itself
    /// wrapped -- if stderr is ALSO broken (e.g. redirected somewhere failing), there is genuinely
    /// nothing left this method can safely do, and it gives up rather than risk being the thing that
    /// crashes the process.</summary>
    private static void SafeLog(Action logAction)
    {
        try
        {
            logAction();
        }
#pragma warning disable CA1031 // Deliberately catches everything -- see this method's own doc comment.
        catch (Exception ex)
        {
            try
            {
                Console.Error.WriteLine($"[SstvSessionService] Logging provider failed while reporting a safety-critical event: {ex}");
            }
            catch
            {
                // Truly nothing left to do -- see this method's own doc comment.
            }
        }
#pragma warning restore CA1031
    }

    /// <summary>Returns whether <paramref name="step"/> actually completed without throwing --
    /// callers that need to know (e.g. only clearing <see cref="_pttLocked"/> once a PTT-off command
    /// is confirmed sent, not just attempted) check this; callers that don't care can ignore it, same
    /// as before this return value was added.</summary>
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

    /// <summary>Shared RX-resume bound for all 4 call sites -- Task.Run offloads
    /// StartReceivingAsync's own synchronous settings-read prefix (see
    /// GetStationIdTransmitOptionsAsync's own comment for why WaitAsync alone can't bound that),
    /// WaitAsync(rxResumeCts.Token) bounds the wait itself.
    ///
    /// Round-19 finding: on timeout, the abandoned background task's eventual outcome used to be
    /// unobserved -- unlike StopPlaybackWithWatchdogAsync/StopReceivingAsync's own established
    /// fault-observer continuations for their own abandoned tasks. Attached unconditionally right
    /// here (rather than only inside a timeout branch, the shape those two siblings use) since this
    /// helper is the single choke point for the Task.Run creation across all 4 call sites --
    /// OnlyOnFaulted means it is a no-op on the (common) success path regardless of when it was
    /// attached.</summary>
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

    private static async IAsyncEnumerable<float> GenerateTone(
        double frequencyHz, TimeSpan duration, int sampleRate, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var totalSamples = (long)(duration.TotalSeconds * sampleRate);
        var angularStep = 2.0 * Math.PI * frequencyHz / sampleRate;
        for (var i = 0L; i < totalSamples; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return (float)Math.Sin(angularStep * i);
            if (i % 4096 == 0)
            {
                await Task.Yield();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Round-2 fix: must be the very FIRST thing this method does, before even
        // AwaitInFlightKeyedTransmitAsync's read of _keyedTransmitCompletion just below -- this is the
        // other half of the publish-then-recheck race PlayWithPttAsync now performs against this same
        // field (see its own disposed-check right after publishing _keyedTransmitCompletion). Whichever
        // side's write happens first, the other side's read is guaranteed to observe it.
        //
        // Round-3 finding: the plain write above is release-store, not a full fence -- see
        // PlayWithPttAsync's own comment at its Interlocked.Exchange publish for why that pairing alone
        // does not forbid StoreLoad reordering. This is the other half of that same fence.
        _disposed = true;
        Interlocked.MemoryBarrier();

        // ---- Blocker 3 (Tier A Batch 3 chunk 3a) ----
        // ORDER CHANGED DELIBERATELY: the PTT backstop now runs BEFORE StopReceivingAsync, which used
        // to be this method's first line. StopReceivingAsync ends in MiniAudioCaptureSession.Dispose's
        // own drain-thread join, which has NO timeout at all if a SamplesCaptured subscriber never
        // returns -- so a wedged drain thread hung shutdown before the PTT-off backstop was ever
        // reached, and the process exited (Program.cs's own ~10s host-teardown bound) with the
        // transmitter still keyed. Un-keying is the one step that must not queue behind anything else.
        //
        // This whole method's correctness also depends on DI teardown order: Program.cs resolves
        // IRadioSessionService before ISstvSessionService, so RadioController/RadioSessionService are
        // constructed first, registered as disposables first, and therefore disposed LAST --
        // IRadioSessionService is still live for everything below. If that resolution order ever
        // changes, this backstop silently stops working.
        // Round-18 finding 6: this step (and the backstop un-key just below) used to run with no
        // guard at all -- the identical "one throw skips everything after it" shape round 17 fixed 30
        // lines below, just two steps earlier. A throw here (e.g. a logging provider failing inside
        // Log.WaitingForKeyedTransmitAtShutdown/Log.KeyedTransmitCleanupWaitTimedOut -- a file logger
        // on a full disk) would skip the backstop un-key AND every step round 17 already guarded,
        // with _disposed already true and the exception escaping into DI teardown.
        try
        {
            await AwaitInFlightKeyedTransmitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "AwaitInFlightKeyedTransmit (dispose)", ex));
        }

        // A rig can be physically keyed at shutdown for FOUR distinct reasons, and _pttLocked only
        // covered one of them: an engaged PTT lock (_pttLocked), a TuneAsync(leaveKeyedAfterTune:true)
        // that deliberately left it keyed (_pttLeftKeyedByCall), an in-flight transmit whose own
        // un-key never completed (_keyedTransmitCount still nonzero after the bounded wait above), and
        // a completed transmit whose own un-key attempt FAILED (_pttUnkeyFailedOnRealRig -- round-3
        // finding: _pttLocked is already false by the time this failure is even observed, and
        // _keyedTransmitCompletion is unconditionally cleared by PlayWithPttAsync's own finally
        // regardless of whether the un-key succeeded, so without this fourth flag a rig this class
        // already logged Critical about would be silently walked away from here). All four get the same
        // best-effort, bounded, swallowed un-key -- a failed shutdown PTT-off must not prevent the rest
        // of teardown from completing, but it is now logged at Critical (inside UnkeyForCleanupAsync),
        // not swallowed silently.
        //
        // Round-6 finding: this reads _keyedTransmitCount, NOT _keyedTransmitCompletion's nullness --
        // see the count field's own doc comment for why nullness alone goes blind when two
        // PlayWithPttAsync calls overlap and the newer one's publish overwrites the older one's TCS.
        var keyedTransmitStillInFlight = Volatile.Read(ref _keyedTransmitCount) > 0;
        if (_pttLocked || _pttLeftKeyedByCall || keyedTransmitStillInFlight || _pttUnkeyFailedOnRealRig)
        {
            // pttKeyedOnRealRig: true, and NOT a re-read of RigId (blocker 2's whole point). Every one
            // of the four states above is only reachable via a SetPttAsync issued against a
            // non-"none" RigId -- SetPttLockAsync only sets _pttLocked AFTER a successful set, the
            // null-object backend always throws, and _pttLeftKeyedByCall/_keyedTransmitCompletion are
            // only published when RigId was real at key time. A failure here is therefore always a
            // genuinely stuck-keyed rig, never the benign no-radio case.
            //
            // Round-18 finding 6: also now guarded -- see the comment on this method's own first step
            // above for why (same shape, same reasoning). UnkeyForCleanupAsync itself does not throw
            // by design (TryUnkeyPttAsync's own doc comment), so this guards only the logging calls
            // inside it, but that is exactly the realistic failure source identified above.
            //
            // Round-24 finding (nit, documentation-only): this backstop's own SetPttAsync(false) queues
            // FIFO behind an abandoned in-flight command on the backend's single request gate if one is
            // still outstanding (the same trade-off documented at PlayWithPttAsync's urgent un-key and
            // its retry) -- it can burn its whole CleanupTimeout budget waiting behind that queued
            // command without ever reaching the rig, and this method still returns once the budget
            // expires. Presented here as the reliable last-resort step; it is best-effort like every
            // other step in this method, not a guarantee the command reaches real hardware before the
            // host's own teardown bound expires.
            try
            {
                await UnkeyForCleanupAsync(pttKeyedOnRealRig: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SafeLog(() => Log.CleanupStepFailed(_logger, "UnkeyForCleanup (dispose backstop)", ex));
            }
        }

        // Round-17 finding: these three teardown steps used to run with no guard at all -- a throw
        // from any one (e.g. StopReceivingAsync's own _decoder.ResetAgc() call, which sits outside
        // its own internal try, or a throwing Waterfall.Dispose()) aborted DisposeAsync mid-way,
        // skipping whatever came after and leaking that resource. Matches MiniAudioEngine.DisposeAsync's
        // own established fix for the identical shape (its own comment: "each lifecycle now gets its
        // own try/finally so a failure in one never prevents the other") -- each step here now gets
        // its own try/catch instead, logged and swallowed, so teardown always reaches every step.
        try
        {
            await StopReceivingAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "StopReceiving (dispose)", ex));
        }

        try
        {
            if (Waterfall is IDisposable disposableWaterfall)
            {
                disposableWaterfall.Dispose();
            }
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "Waterfall.Dispose", ex));
        }

        // RX buffer subsystem Phase 7 (disposal-chain sub-piece): _decoder is ISstvDecoder-typed, not
        // IDisposable itself (RestartableSstvDecoder implements it, but ISstvDecoder deliberately
        // doesn't extend it -- avoids widening that interface's surface for one production
        // implementation's own resource-cleanup need), same duck-typed pattern as the Waterfall check
        // above. Still placed AFTER StopReceivingAsync so no in-flight PushSamples call can race the
        // decoder's own disposal.
        try
        {
            if (_decoder is IDisposable disposableDecoder)
            {
                disposableDecoder.Dispose();
            }
        }
        catch (Exception ex)
        {
            SafeLog(() => Log.CleanupStepFailed(_logger, "Decoder.Dispose", ex));
        }
    }

    /// <summary>Blocker 3's wait. <c>TxControlsPaneViewModel.Dispose()</c> cancels an in-flight
    /// transmit's token WITHOUT awaiting the transmit task, so on window-close-during-TX the
    /// transmit's own <c>finally</c>/un-key can still be running while DI teardown proceeds. Bounded
    /// (see <see cref="_inFlightKeyedTransmitWait"/>'s sizing against Program.cs's own host-teardown
    /// bound) and never throws: whether the wait succeeds or times out, DisposeAsync's own backstop
    /// un-key runs next either way, and a redundant un-key is documented-harmless on every shipped
    /// backend (see SetPttLockAsync's own doc comment).
    ///
    /// <b>Round-6 finding: this wait targets only the most recently published
    /// <see cref="_keyedTransmitCompletion"/>, best-effort</b> -- if two <see cref="PlayWithPttAsync"/>
    /// calls overlap, an older call's own TCS can be overwritten by a newer one's publish (see
    /// <see cref="_keyedTransmitCount"/>'s own doc comment), so this method has nothing to await for
    /// that older call even though it may still be genuinely keyed. Not fixed here -- DisposeAsync's own
    /// caller reads <see cref="_keyedTransmitCount"/> (not this field's nullness) for its "is anything
    /// still in flight" decision, so the fallback backstop un-key below still fires correctly even when
    /// this wait has nothing to observe; only the WAIT itself (letting the older call finish on its own
    /// terms first) is best-effort, not the safety guarantee.</summary>
    private async Task AwaitInFlightKeyedTransmitAsync()
    {
        var pending = Volatile.Read(ref _keyedTransmitCompletion);
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

    /// <summary><paramref name="totalSamplesEstimate"/> is <see langword="null"/> for
    /// <see cref="TuneAsync"/> (no <see cref="TransmitProgressChanged"/> reporting for a tone) and a
    /// real value for <see cref="TransmitAsync"/> (spec/18-path-to-1.0.md Medium item). The running
    /// sample counter is a local, not a field -- nothing must dangle across separate calls.</summary>
    private async Task PumpToPlaybackAsync(IAsyncEnumerable<float> samples, float gain, int sampleRate, long? totalSamplesEstimate, CancellationToken ct)
    {
        const int chunkSize = 4096;
        var buffer = new float[chunkSize];
        var count = 0;
        var samplesEnqueued = 0L;

        await foreach (var sample in samples.WithCancellation(ct).ConfigureAwait(false))
        {
            buffer[count++] = sample * gain;
            if (count == chunkSize)
            {
                await EnqueueAllAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                samplesEnqueued += count;
                ReportTransmitProgress(samplesEnqueued, totalSamplesEstimate, sampleRate);
                count = 0;
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

    private async Task<AudioDeviceInfo> ResolveDeviceAsync(bool forCapture, CancellationToken ct)
    {
        var device = await TryResolveDeviceAsync(forCapture, ct).ConfigureAwait(false);
        if (device is null)
        {
            var kind = forCapture ? "capture" : "playback";
            var settings = await LoadAudioSettingsAsync(ct).ConfigureAwait(false);
            var deviceId = forCapture ? settings.CaptureDeviceId : settings.PlaybackDeviceId;
            if (deviceId is null)
            {
                // TryResolveDeviceAsync already tried the backend-reported default and found none
                // (spec/18-path-to-1.0.md Critical item 1 / item 8) -- this is now the rarer
                // "genuinely no audio device available at all" case, not "user never opened
                // Options."
                Log.NoDeviceConfigured(_logger, kind);
                throw new InvalidOperationException($"No {kind} audio device configured, and no default {kind} device is available -- set one in Options before starting a session.");
            }

            await _deviceEnumerator.RefreshAsync(ct).ConfigureAwait(false);
            var devices = forCapture ? _deviceEnumerator.InputDevices : _deviceEnumerator.OutputDevices;
            Log.ConfiguredDeviceNotFound(_logger, kind, deviceId, devices.Count);
            throw new InvalidOperationException($"Configured {kind} device '{deviceId}' was not found among currently available devices.");
        }

        return device;
    }

    /// <summary>Non-throwing counterpart to <see cref="ResolveDeviceAsync"/>, extracted from it (not
    /// duplicated) so <see cref="GetConfiguredPlaybackDeviceNameAsync"/>'s passive readout use and
    /// <see cref="ResolveDeviceAsync"/>'s action-that-should-fail-loudly use share one lookup.
    /// <see langword="null"/> means either "configured device not found" (a device WAS explicitly
    /// configured, but isn't among the currently enumerated devices -- deliberately NOT
    /// substituted with the default, since a device the user explicitly picked going missing is a
    /// real problem worth surfacing, not silently working around) or "nothing configured, and the
    /// backend reports no default device either" (spec/18-path-to-1.0.md Critical item 1 / item 8
    /// -- a genuinely rare case, e.g. a headless machine with no audio hardware at all). When
    /// nothing is explicitly configured but the backend DOES report a default, that default is
    /// returned here -- this deliberately changes what
    /// <see cref="GetConfiguredPlaybackDeviceNameAsync"/>/<see cref="GetConfiguredCaptureDeviceNameAsync"/>
    /// display: they now show the device that will actually be used, not a blank "not configured"
    /// placeholder that silently implied nothing would happen.</summary>
    private async Task<AudioDeviceInfo?> TryResolveDeviceAsync(bool forCapture, CancellationToken ct)
    {
        var settings = await LoadAudioSettingsAsync(ct).ConfigureAwait(false);
        var deviceId = forCapture ? settings.CaptureDeviceId : settings.PlaybackDeviceId;

        await _deviceEnumerator.RefreshAsync(ct).ConfigureAwait(false);
        var devices = forCapture ? _deviceEnumerator.InputDevices : _deviceEnumerator.OutputDevices;

        if (deviceId is not null)
        {
            return devices.FirstOrDefault(d => d.Id == deviceId);
        }

        var fallback = devices.FirstOrDefault(d => d.IsDefault);
        if (fallback is not null)
        {
            Log.UsingDefaultDevice(_logger, forCapture ? "capture" : "playback", fallback.Name);
        }

        return fallback;
    }

    public async Task<string?> GetConfiguredPlaybackDeviceNameAsync(CancellationToken ct = default)
    {
        var device = await TryResolveDeviceAsync(forCapture: false, ct).ConfigureAwait(false);
        return device?.Name;
    }

    /// <summary>See <see cref="ISstvSessionService.GetConfiguredCaptureDeviceNameAsync"/>.</summary>
    public async Task<string?> GetConfiguredCaptureDeviceNameAsync(CancellationToken ct = default)
    {
        var device = await TryResolveDeviceAsync(forCapture: true, ct).ConfigureAwait(false);
        return device?.Name;
    }

    private async Task<AudioDeviceSettings> LoadAudioSettingsAsync(CancellationToken ct)
    {
        var appSettings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        return appSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings)
            ?? new AudioDeviceSettings();
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Error, Message = "Decoder PushSamples threw ({Count} occurrences so far)")]
        public static partial void DecoderPushSamplesFailed(ILogger logger, int count, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Waterfall PushSamples threw ({Count} occurrences so far)")]
        public static partial void WaterfallPushSamplesFailed(ILogger logger, int count, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Manual ReSync requested")]
        public static partial void ReSyncRequested(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Manual Correct Slant requested")]
        public static partial void CorrectSlantRequested(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Decode mode forced to {ModeId}")]
        public static partial void ModeForced(ILogger logger, string modeId);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX started: device={DeviceId}, sampleRate={SampleRate}Hz")]
        public static partial void RxStarted(ILogger logger, string deviceId, int sampleRate);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX stopped")]
        public static partial void RxStopped(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "TX starting: mode={ModeId}, {Width}x{Height}")]
        public static partial void TxStarting(ILogger logger, string modeId, int width, int height);

        [LoggerMessage(Level = LogLevel.Information, Message = "Tune starting: {FrequencyHz}Hz for {Duration}")]
        public static partial void TuneStarting(ILogger logger, double frequencyHz, TimeSpan duration);

        [LoggerMessage(Level = LogLevel.Debug, Message = "TX volume set to {Percent}%")]
        public static partial void TxVolumeSet(ILogger logger, int percent);

        [LoggerMessage(Level = LogLevel.Debug, Message = "CaptureOverrunCount read raced a concurrent StopReceivingAsync; reporting 0")]
        public static partial void CaptureOverrunCountRaceObserved(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "PTT keyed")]
        public static partial void PttKeyed(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "PTT key skipped -- no radio backend configured (RigId=\"none\")")]
        public static partial void PttSkippedNoRadio(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "PTT un-key skipped -- no radio backend configured (RigId=\"none\")")]
        public static partial void PttUnkeySkippedNoRadio(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "PTT released")]
        public static partial void PttReleased(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "PTT un-key succeeded, but a newer key command completed while it was in flight -- leaving that call's own \"still keyed\" state intact instead of clearing it")]
        public static partial void PttUnkeyRaceLostToNewerKey(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping \"left keyed\" state write -- a confirmed PTT un-key completed elsewhere while this call was finishing up, so the rig is already known to be off")]
        public static partial void PttLeftKeyedSkippedConfirmedUnkeyRace(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping cleanup PTT un-key attempt -- a confirmed un-key completed elsewhere while this call was finishing up, so the rig is already known to be off")]
        public static partial void PttCleanupUnkeySkippedConfirmedElsewhere(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "PTT lock {Locked}")]
        public static partial void PttLockChanged(ILogger logger, bool locked);

        [LoggerMessage(Level = LogLevel.Information, Message = "Playback cancelled")]
        public static partial void PlaybackCancelled(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Playback aborted -- the session was disposed while this call was still resolving its device")]
        public static partial void PlaybackAbortedByDispose(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error, Message = "Playback failed")]
        public static partial void PlaybackFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Cleanup step '{StepName}' failed")]
        public static partial void CleanupStepFailed(ILogger logger, string stepName, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "StopPlayback did not finish within {Budget}; continuing to the PTT un-key without waiting (the stop is still running in the background)")]
        public static partial void PlaybackStopWatchdogFired(ILogger logger, TimeSpan budget);

        [LoggerMessage(Level = LogLevel.Warning, Message = "StopCapture did not finish within {Budget}; treating RX as stopped without waiting (the stop is still running in the background)")]
        public static partial void CaptureStopWatchdogFired(ILogger logger, TimeSpan budget);

        [LoggerMessage(Level = LogLevel.Critical, Message = "PTT MAY STILL BE KEYED -- the un-key command failed on a rig this session actually keyed. Check the radio and un-key it manually.")]
        public static partial void PttStillKeyedAfterFailedUnkey(ILogger logger);

        [LoggerMessage(Level = LogLevel.Critical, Message = "PTT MAY HAVE BEEN KEYED -- the key command failed, but may have physically keyed the rig before failing. Check the radio and un-key it manually if needed.")]
        public static partial void PttKeyCommandFailedMayHaveKeyed(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Shutdown is waiting for an in-flight keyed transmit to finish un-keying PTT")]
        public static partial void WaitingForKeyedTransmitAtShutdown(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error, Message = "In-flight keyed transmit did not finish its own PTT un-key within {Budget}; forcing an un-key from shutdown")]
        public static partial void KeyedTransmitCleanupWaitTimedOut(ILogger logger, TimeSpan budget);

        [LoggerMessage(Level = LogLevel.Warning, Message = "No {Kind} audio device configured")]
        public static partial void NoDeviceConfigured(ILogger logger, string kind);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Configured {Kind} device '{DeviceId}' not found among {AvailableCount} available devices")]
        public static partial void ConfiguredDeviceNotFound(ILogger logger, string kind, string deviceId, int availableCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "No {Kind} device configured -- using backend-reported default '{DeviceName}'")]
        public static partial void UsingDefaultDevice(ILogger logger, string kind, string deviceName);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX maintenance warning raised (approaching automatic restart threshold)")]
        public static partial void MaintenanceWarningRaised(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX maintenance warning cleared")]
        public static partial void MaintenanceWarningCleared(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "RX force-stopped for required maintenance restart")]
        public static partial void MaintenanceCriticalStop(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error, Message = "Maintenance handler '{HandlerName}' threw")]
        public static partial void MaintenanceHandlerFailed(ILogger logger, string handlerName, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "TransmitProgressChanged handler threw ({Count} occurrence(s) so far this session)")]
        public static partial void TransmitProgressHandlerFailed(ILogger logger, int count, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "CapturePausedForTransmitChanged handler threw (paused={Paused})")]
        public static partial void CapturePausedHandlerFailed(ILogger logger, bool paused, Exception ex);
    }
}
