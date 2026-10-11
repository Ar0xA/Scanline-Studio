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
    /// <summary>Manual-keying diagnostic aid (e.g. a "PTT lock" button) -- keys PTT immediately and
    /// holds it keyed independent of any <see cref="TransmitAsync"/>/<see cref="TuneAsync"/> call,
    /// until unlocked. Operates directly on the PTT line only -- unlike <see cref="PlayWithPttAsync"/>,
    /// this method does NOT itself pause/resume RX capture; if a lock is engaged while a
    /// Transmit/Tune call is skipping its own un-key because of this lock (see
    /// <see cref="PlayWithPttAsync"/>'s own doc comment), THAT call's paused RX is what gets resumed
    /// here on unlock (see <c>PttSafetyCoordinator</c>'s own <c>_rxPendingResumeAfterUnlock</c>) --
    /// not a capture pause owned by this method itself.
    ///
    /// <b>Idempotent in OUTCOME, not by skipping redundant calls</b> (an audit-fix correction from an
    /// earlier version of this method that short-circuited when the requested state already matched
    /// <c>IsPttLocked</c> -- a real bug: <see cref="TuneAsync"/>'s <c>leaveKeyedAfterTune</c>
    /// leaves PTT physically keyed without ever setting it, and a failed
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
    /// attempt an immediate recovery un-key, but only when <c>PttSafetyCoordinator.HasSingleInFlightKeyedTransmit</c>
    /// reads true at that moment -- i.e. only when nothing else (no concurrent
    /// <see cref="PlayWithPttAsync"/> transmission, no other <see cref="SetPttLockAsync"/> call) has
    /// a registration in flight to endanger. Round 11 originally left this un-recovered entirely,
    /// reasoning an unconditional recovery would introduce a new instance of the race above; round 12
    /// corrected that -- the guarded version is safe (see the catch block's own comment for the full
    /// argument) and closes what would otherwise be an hours-long recorded-but-not-recovered window
    /// in the common (non-concurrent) case, which is the overwhelmingly likely one for this
    /// unwired manual diagnostic aid.</summary>
    public async Task SetPttLockAsync(bool locked, CancellationToken ct = default)
    {
        // Tier B audit finding: the command dispatch further down already special-cases the UNLOCK
        // direction to keep the emergency-unlock escape hatch un-cancellable at the backend gate
        // (round 19's own fix, see that call site's own comment for the full reasoning -- "it must
        // stay queued and eventually reach the rig, not be cancellable away"), but this earlier gate
        // wait was still unconditionally `ct`-cancellable -- a caller cancelling while queued behind
        // a wedged prior command (up to _cleanupTimeout) could abort the emergency unlock before it
        // ever reaches the command dispatch at all, the same class of gap one level up. Same
        // conditional token this method already uses for the command dispatch below.
        await _pttLockGate.WaitAsync(locked ? ct : CancellationToken.None).ConfigureAwait(false);

        // Round-8 finding: this call's own handle into the coordinator's keyed-slot registration --
        // see PttSafetyCoordinator.PublishKeyedSlot's own doc comment for why publishing BEFORE the
        // key command (and this method's own finally clearing ONLY this call's own instance) both
        // matter. Without this, a key command in flight here was invisible to BOTH DisposeAsync's
        // bounded shutdown WAIT (AwaitInFlightKeyedTransmitAsync reads the same registration) and its
        // four-state backstop check -- DisposeAsync could run to completion, dispose
        // IRadioSessionService, and only THEN would this call's own post-await disposal-race recovery
        // (the `if (locked && _disposed)` block below) get a chance to run -- against a radio session
        // that no longer exists.
        var attempt = new PttSafetyCoordinator.PttKeyAttempt();

        // Round-31 finding (nit): this used to be a NESTED try/finally (an inner one ending at
        // _pttLockGate.Release(), wrapped by an outer one whose own finally ran the
        // _keyedTransmitCount decrement AFTER that release) -- now a single try/finally, since moving
        // the decrement to run BEFORE the release (see that finally's own comment) removed the only
        // reason the outer wrapper existed. A single try/finally still guarantees the same thing the
        // outer one did: this runs no matter how the body below exits.
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
            // PttSafetyCoordinator's own doc comment for the lost-update race this closes. Same fix
            // shape UnkeyForCleanupAsync already uses; round 5 fixed only that twin location and
            // missed this one -- SetPttLockAsync's own unlock-path clears just below had the
            // identical gap.
            var epochAtUnkeyStart = _pttCoordinator.CurrentKeyEpoch;

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
            //
            // Round-32 finding (nit): the ENGAGE arm just below had the identical device-identity
            // filter round 24 already removed from the unlock arm above -- rigIsRealAtKeyTime is
            // captured once, before the key command (see its own comment), so it is not a fresh
            // read and not the blocker-2 pattern itself, but it is still a device-identity gate on
            // TOP of a belief-based test that doesn't need one. RadioController.ConnectAsync writes
            // _protocol before _rigId (RadioController.cs, ConnectAsync), so a key command landing
            // in that narrow window sees RigId == "none" with a real protocol already assigned --
            // rigIsRealAtKeyTime reads false, this catch never runs, and a mid-command failure here
            // leaves every shutdown-backstop flag untouched. Zero production callers reach this
            // arm today (SetPttLockAsync has none), so this stays a nit -- upgrade the moment it
            // gets one. Filtering on `locked` instead removes the device-identity gate the same way
            // round 24 already did for the unlock arm.

            if (rigIsRealAtKeyTime)
            {
                // Round-8 finding: published BEFORE the key command, same shape/reasoning as
                // PlayWithPttAsync's own publish (see its own comment) -- so DisposeAsync can never
                // tear IRadioSessionService down out from under this call's own in-flight key/un-key.
                _pttCoordinator.PublishKeyedSlot(attempt);

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
            catch (Exception) when (locked)
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
                // Tier B audit finding: this write used to be unconditional inside the catch --
                // but with RigId == "none" (the default, no-radio-configured state), the whole
                // production chain (RadioSessionService -> RadioController -> NoneRadioProtocol)
                // throws SYNCHRONOUSLY, before pttCommand is ever assigned (still null here). That
                // path never sent anything to a real backend, so nothing was ever physically keyed
                // -- but the unconditional write latched _pttLeftKeyedByCall=true anyway, violating
                // that field's own documented invariant ("never fires for the benign RigId=='none'
                // path," see its own doc comment) permanently: neither clear site can ever run
                // without a confirmed un-key, which needs a real rig that was never involved.
                // DisposeAsync's backstop then fires a false Critical "PTT MAY STILL BE KEYED" on a
                // machine with no radio at all, and every SUBSEQUENT transmit's own baseline reads
                // this same stale true, repeating the false Critical for the rest of the process
                // (the round-30 signal-erosion class, now reachable with no radio configured).
                // Gated on pttCommand actually having been issued -- only a genuine dispatch to a
                // real backend can mean "may have physically keyed."
                if (pttCommand is not null)
                {
                    _pttCoordinator.RecordLockEngageFailed();
                    SafeLog(() => Log.PttKeyCommandFailedMayHaveKeyed(_logger));
                }

                // Round-30 finding (risk): this fault-observer attachment used to sit AFTER the
                // recovery-un-key await below (up to _cleanupTimeout, ~5s) -- but both shipped
                // backends serialize on a single approximately-FIFO request gate (see round-19's own
                // comment further down, and round-24's), so the recovery un-key cannot make
                // progress until the abandoned pttCommand clears that SAME gate. That makes "the key
                // command completes during the recovery await" not an unlucky race but the EXPECTED
                // ordering whenever the recovery does anything at all -- so by the time the old
                // placement's own IsCompleted check ran, it was almost always already true, and the
                // observer almost never actually attached on the exact path it exists for. Moved to
                // run BEFORE the recovery await, right after this catch's own state-latching and
                // Critical log, so nothing here depends on the recovery step below. This is the ONE
                // fault-observer site in this class with an await between the triggering throw and
                // the original placement's own IsCompleted check; the sibling sites (TryUnkeyPttAsync,
                // StopReceivingAsync, StopPlaybackWithWatchdogAsync, ResumeReceivingBoundedAsync, and
                // this method's own unlock-direction arm below) all attach immediately with no
                // intervening await, so their own placement is correct as-is.
                //
                // Round-31 finding (nit): an earlier version of this comment claimed "pttCommand is
                // fully assigned by this point (this catch only runs once the command has been
                // issued)" -- FALSE on one path, as round 29's own comment at pttCommand's own
                // declaration already correctly states: a synchronous throw from
                // _radioSession.SetPttAsync leaves pttCommand null, and this catch runs for that case
                // too. The code below was always correct regardless (the `is { IsCompleted: false }`
                // pattern already handles null), but the claim itself was wrong and could have misled
                // a future round into "simplifying" the null-tolerance away as redundant.
                if (pttCommand is { IsCompleted: false })
                {
                    _ = pttCommand.ContinueWith(
                        t => SafeLog(() => Log.CleanupStepFailed(_logger, "PTT command (finished after watchdog)", t.Exception!)),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }

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
                //
                // Tier B audit finding: this comment's own premise -- "_keyedTransmitCount was
                // already incremented for THIS call at the publish above (guarded by
                // rigIsRealAtKeyTime, same as this catch)" -- stopped being true once this catch's
                // own filter above widened from `when (rigIsRealAtKeyTime)` to `when (locked)`: the
                // publish is still gated on rigIsRealAtKeyTime alone, so this catch can now run on
                // paths where THIS call never published at all. A bare count==1 check can then
                // misfire in both directions: it can read a DIFFERENT concurrent call's own
                // registration as "safe to recover" and un-key the rig out from under that other
                // call's genuinely in-flight, on-air transmission mid-frame -- the exact harm this
                // whole guard exists to prevent -- or it can read 0 and skip a recovery this call
                // itself should have run. keyedCompletion is non-null if and only if THIS call's own
                // publish actually ran, so it's the correct, call-scoped guard the count alone no
                // longer is.
                if (attempt.KeyedCompletion is not null && _pttCoordinator.HasSingleInFlightKeyedTransmit)
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
                if (_pttCoordinator.BelievesKeyed(_pttCoordinator.IsPttLocked))
                {
                    _pttCoordinator.RecordUnlockFailed();
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

            // Round-5/6 findings (engage direction: epoch bumped before the lock is recorded, closing
            // an ordering gap a concurrent un-keyer could otherwise observe) and round-4/6/26 findings
            // (disengage direction: every belief flag invalidated on a CONFIRMED un-key, but only if
            // nothing re-keyed while the command was in flight -- Race 1's own guard) -- see
            // PttSafetyCoordinator.CommitLockCommandSucceeded's own doc comment for the full reasoning,
            // including why the engage/disengage directions use genuinely different field policies.
            var commitResult = _pttCoordinator.CommitLockCommandSucceeded(locked, epochAtUnkeyStart);
            if (commitResult == PttSafetyCoordinator.PttLockCommitResult.UnlockRaceLostToNewerKey)
            {
                SafeLog(() => Log.PttUnkeyRaceLostToNewerKey(_logger));
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

            if (!locked && _pttCoordinator.TryConsumeRxResumePending())
            {
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
            // Round-8 finding: signalled no matter how the above ended (success, un-key failure, a
            // throw from the disposal-race recovery, RX-resume failure) -- a DisposeAsync waiting on
            // this must never be left hanging until its own timeout by an unrelated failure. Same
            // CompareExchange pattern as PlayWithPttAsync's own inner finally (see its own comment):
            // only clear the field if it still points at THIS call's instance, so an overlapping
            // PlayWithPttAsync/SetPttLockAsync call can't have its own registration erased.
            //
            // Round-31 finding (nit): moved here, BEFORE _pttLockGate.Release() just below (this
            // used to run in a separate OUTER finally, AFTER the release) -- releasing the gate
            // first let a new SetPttLockAsync(true) call acquire it, publish its own registration,
            // and increment _keyedTransmitCount WHILE this call's own registration was still
            // counted, making round-12's own recovery guard
            // (Volatile.Read(ref _keyedTransmitCount) == 1, in the engage-direction catch above)
            // read 2 spuriously and skip the recovery un-key on a rig this call's own key command
            // may have physically keyed -- the precise harm round 12's fix exists to prevent.
            // Instruction-scale window (no await between the old release and decrement), the same
            // accepted-residual CLASS _pttKeyEpoch's own field doc documents -- but this specific
            // instance is now closed for free by reordering, not just documented. Safe to decrement
            // before releasing: a successful engage has already written _pttLocked = true and a
            // failed one _pttLeftKeyedByCall = true, so DisposeAsync's own backstop is never
            // blinded by the earlier decrement.
            _pttCoordinator.ReleaseKeyedSlot(attempt);

            _pttLockGate.Release();
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
    /// callers that need to know (e.g. only clearing <c>IsPttLocked</c> once a PTT-off command
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

    /// <summary>Blocker 3's wait. <c>TxControlsPaneViewModel.Dispose()</c> cancels an in-flight
    /// transmit's token WITHOUT awaiting the transmit task, so on window-close-during-TX the
    /// transmit's own <c>finally</c>/un-key can still be running while DI teardown proceeds. Bounded
    /// (see <see cref="_inFlightKeyedTransmitWait"/>'s sizing against Program.cs's own host-teardown
    /// bound) and never throws: whether the wait succeeds or times out, DisposeAsync's own backstop
    /// un-key runs next either way, and a redundant un-key is documented-harmless on every shipped
    /// backend (see SetPttLockAsync's own doc comment).
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
