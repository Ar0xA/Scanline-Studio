using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

public sealed partial class AnalogFmSstvDecoder
{
    // S7 fix (spec/14-roadmap.md): extracted from Commit()'s own former inline header so the new
    // mid-reception AVT hand-off (TryVisLockStateMachine) can reuse the exact same "abandon whatever
    // is currently mid-decode" step Commit() already performs for every other restart, without also
    // setting a new _mode (AVT training isn't resolved yet at that point). Auditor code-level review
    // (of the extraction itself, during plan-review) confirmed this is behavior-preserving for every
    // EXISTING Commit() caller: nothing between the old inline header and the rest of that method's
    // body reads _mode/_lineDecoder/_pixels/_nextLine/_bandpassLockedFromSample before they're
    // reassigned, and int.MaxValue matches EndOfImage's own existing "no lock" convention for
    // _bandpassLockedFromSample. Deliberately does NOT touch _afcTracker/_slantTracker/
    // _syncEnvelopeDetector/_afcBoundSample or _syncBypassProcessedUpTo/_syncBypassOriginSample --
    // the former are only ever read from the per-line loop (which requires _mode != null) and are
    // unconditionally reassigned by InitializeAfc/InitializeSlant on the eventual real Commit() either
    // way; the latter's own mid-pending-window trim-retention cost (see TryVisLockStateMachine's own
    // new AVT branch doc comment) is a deliberate, bounded, accepted tradeoff, not an oversight.
    // SHOULD item 9 (spec/14-roadmap.md): this is a SECOND way `_mode` can become null without going
    // through `EndOfImage()` (the first being a normal image completing) -- `EndOfImage()` is what
    // normally re-syncs `_syncBypassProcessedUpTo`/`_visLockProcessedUpTo` (its own `resumeFrom` jump)
    // before `TryInterleavedHeaderScan` (the pre-lock scanner gated on `_mode is null`) would ever run
    // again. This method does NOT do that resync. Currently safe by REACHABILITY, not by construction:
    // every call site of this method (`Commit()`, and S7's own AVT-training-abandon-for-a-cleaner-match
    // path) either immediately re-assigns `_mode` itself (`Commit()`) or leaves `_avtTrainingPending`
    // true (short-circuiting `TryDecodeHeader` straight past `TryInterleavedHeaderScan` entirely until
    // a guaranteed later `Commit()`) -- so `TryInterleavedHeaderScan` never actually observes the
    // unsynced cursors this method leaves behind. A future call site that abandons an in-progress image
    // WITHOUT either committing a new one or entering AVT training would break that coupling silently
    // (no compiler error, no test failure until such a path is added) and could let
    // `TryInterleavedHeaderScan` re-examine already-accounted-for samples. Not fixed here: the two
    // existing call sites are exhaustively safe today, and adding a resync this method's own current
    // callers don't need would be validating a scenario that can't currently happen -- flagged so a
    // FUTURE new call site's author checks this coupling explicitly instead of discovering it via a
    // hard-to-diagnose spurious restart.
    //
    // Also clears the manual-ReSync state (ResetReSyncState) -- legacy zeroes m_Skip in Start()
    // (sstv.cpp:1725) as well as Stop(), and this method is the Start()-side teardown for both its
    // call sites (Commit and S7's AVT hand-off), so Commit needs no separate clear of its own.
    private void AbandonInProgressImage()
    {
        EndLiveSnr();
        _mode = null;
        _lineDecoder = null;
        _pixels = null;
        _nextLine = 0;
        _bandpassLockedFromSample = int.MaxValue; // matches EndOfImage's own "no lock" convention
        // Functional-audit fix (D6, round 1): defense-in-depth for IsIdle's own `_mode is null ⇒
        // _pendingAnchorCorrectionMode is null` invariant (see that field's own doc comment for the
        // full reasoning) -- reachability alone already protects this today (this method's own AVT
        // hand-off call site only ever runs after the flag has already been cleared earlier in the
        // same PushSamples iteration), but clearing it here too costs nothing for the ordinary
        // Commit()-calls-this-first path (Commit's own non-AVT branch unconditionally reassigns it
        // moments later anyway) and closes the one branch that never otherwise touches it (Commit's
        // AVT branch), matching PerformForceMode's own explicit defensive clear for the same field.
        _pendingAnchorCorrectionMode = null;
        ResetReSyncState(); // legacy's Start()-side m_Skip = 0, sstv.cpp:1725
    }

    // Legacy clears m_Skip at both ends of a reception: Start() (sstv.cpp:1725) and Stop()
    // (sstv.cpp:1789). Same for the click-handler state KRFSClick sets -- m_SyncPos/m_SyncRPos are
    // reset per line either way, and m_AutoSyncCount/m_AutoSyncDis are cleared once per reception
    // (Main.cpp:4994). Called from AbandonInProgressImage (legacy's Start() side -- covers BOTH
    // Commit() and the S7 AVT hand-off) and from EndOfImage (legacy's Stop() side).
    //
    // Dropping a partially-drained skip rather than finishing it across the boundary is deliberate and
    // matches legacy exactly (m_Skip = 0, not "let it finish"). _consumedSamples/_idealLineStartSample
    // stay consistent through such a partial drain: both callers reassign both of them together, and
    // _slantProcessedUpTo is resynced by InitializeSlant before ApplySlantTracking can run again.
    private void ResetReSyncState()
    {
        _reSyncRequested = false;
        _pendingSkipSamples = 0;
        _lastLineSyncPeakPosition = null;
        _suppressNextSlantProcessLine = false;
        _slantCorrectionsDisabledForRestOfImage = false;
        _autoSyncCooldown = 0; // Main.cpp:4994's own m_AutoSyncDis=0, alongside m_AutoSyncCount=0 above
    }

    // ui_transition_plan.md step 10 (T2-5): applyModeLock defaults true for every ordinary
    // detection-driven caller; PerformForceMode passes false so a manual quick-mode-button click
    // always wins its OWN reception regardless of an active lock (a one-shot EXCEPTION to the lock,
    // not a change to it -- ForceMode's own _forcedMode field is untouched by _lockedMode either
    // way, see that field's own doc comment). matched == Avt is excluded from substitution even when
    // applyModeLock is true -- a detected-AVT signal always trains/commits as AVT (v1 limitation,
    // deliberately not given a cross-family AVT<->locked-mode substitution -- see _lockedMode's own
    // doc comment). The pre-Commit anchor math every caller already computed (e.g.
    // IsScottieFamily/GetSyncSegmentMidpointOffsetMs) stays keyed to `matched`, the ACTUALLY
    // DETECTED mode -- it describes the observed signal, not what this method chooses to label it
    // as; only what happens FROM HERE ON (the mode actually decoded/committed) substitutes.
    // TryResolveSyncAnchorCorrection (called from TryProcessBuffer against _pendingAnchorCorrectionMode,
    // set below) re-derives the anchor modulo the COMMITTED mode's own line width regardless, so a
    // locked-mode substitution here is corrected for automatically, not left as a mismatch.
    private void Commit(SstvModeDefinition matched, int lineStartSample, bool applyModeLock = true)
    {
        var effectiveMode = applyModeLock && matched != SstvModeRegistry.Avt && _lockedMode is not null
            ? _lockedMode
            : matched;

        AbandonInProgressImage();
        ResetSnrForNewReception();
        _consumedSamples = Math.Max(0, lineStartSample);
        _idealLineStartSample = _consumedSamples; // MUST 4 -- see field's own doc comment
        RaiseSubscribers(LockAnchorCommitted, _consumedSamples);
        _bandpassLockedFromSample = _consumedSamples; // Band-1 item 4b -- see field's own doc comment
        _mode = effectiveMode;
        // H3/HBPFN item (decoder_quality_improvement.md §5.1): mirrors legacy's Start()-time
        // `if( m_fNarrow ) CalcNarrowBPF(HBPFN, m_bpftap, m_bpf, SSTVSET.m_Mode)` (sstv.cpp:1745-1746)
        // exactly -- built from EFFECTIVEMODE, not `matched`, since the `_lockedMode` substitution
        // above can turn a non-narrow `matched` into a narrow `effectiveMode` (or vice versa); using
        // the wrong one would build the wrong mode's band. `_mode` has exactly one non-null-assigning
        // statement in this whole file (this one) -- SearchBandpassFilter.ProcessSample's own
        // useNarrow=true path can never run before this call has already built H3 for that same mode.
        if (effectiveMode.NarrowModeCode is not null)
        {
            _searchBandpassFilter?.BuildNarrowLockedFilter(effectiveMode);
        }

        _lineDecoder = ScanlineCodecFactory.CreateDecoder(effectiveMode.ColorEncoding);
        _pixels = new Rgb24[effectiveMode.ImageWidth * effectiveMode.ImageHeight];
        _nextLine = 0;

        // Piece 6c prerequisite, a real bug caught by this port's own end-to-end test: whichever
        // path found this match, VisLockStateMachine must never re-examine samples already accounted
        // for by the time reception is locked, or its now-continuously-running re-verification scan
        // (see TryProcessBuffer) immediately rediscovers the very header that just committed and
        // fires a spurious mid-reception "restart" against itself. When the fixed-window, narrow, or
        // AVT path triggered this Commit(), _visLockProcessedUpTo may still be at its initial 0
        // (never touched -- TryDecodeHeader only falls through to TryInterleavedHeaderScan once the
        // fixed-window paths fail, and neither AVT resolution nor anything before that fallthrough
        // touches this cursor) -- Math.Max fast-forwards it past the header those paths already
        // resolved. Round-3-review correction: a sync-bypass match does NOT belong in that "still at
        // 0" case -- TryInterleavedHeaderScan advances _visLockProcessedUpTo in lockstep with
        // _syncBypassProcessedUpTo on every sample, sync-bypass match or not, so by the time one
        // fires it's already at the matching sample index (see the paragraph below); an earlier
        // version of this parenthetical said sync-bypass "doesn't touch this cursor on its own",
        // which was wrong -- it's mechanically the same Math.Max fast-forward as the other paths,
        // but the "never touched"/"still at 0" framing specifically does not apply to it. When
        // VisLockStateMachine itself resolved the match --
        // whether via TryInterleavedHeaderScan's inlined branch pre-lock, or via
        // TryVisLockStateMachine's piece-6c mid-reception call -- Math.Max still *advances* it (not a
        // no-op): re-derived independently by review, walking VisLockStateMachine's own anchor formula
        // against how many samples ProcessSample actually consumed to return a match shows the anchor
        // is always slightly *later* (~15ms/164 samples at 11025Hz, for every candidate mode, normal
        // or extended) than where the state machine itself stopped needing samples -- an earlier
        // version of this comment claimed Math.Max "leaves it alone" here, which was backwards, though
        // harmless (the skipped samples are header tail, never re-examined either way). Always
        // Reset(), even when self-triggered (already resets itself internally on a match) or already
        // fast-forwarded (Reset() only clears logical state, not the origin) -- cheap, and guarantees
        // no stale in-progress bit accumulation survives into the new transmission if a different
        // path pre-empted an in-progress VisLockStateMachine scan.
        //
        // Round-1-review addition, round-2-review-corrected: a sync-bypass match (CommitSyncBypassMatch,
        // m_sint1/m_sint2/m_sint3) triggering this Commit() from inside TryInterleavedHeaderScan is
        // already covered mechanically by the first paragraph above (Math.Max fast-forwards
        // _visLockProcessedUpTo the same way any other non-self-triggering path does), but it has a
        // consequence worth calling out on its own. Pre-fix, this case could only happen after
        // TryVisLockStateMachine had already separately exhausted the whole buffer first (the old
        // sequential shape), so _visLockProcessedUpTo was already at _rawSamples.Count by the time a
        // sync-bypass match landed here -- pinning _visLockOriginSample at the buffer's end. Post-fix,
        // the two cursors advance together inside TryInterleavedHeaderScan's own loop, so a sync-bypass
        // match at index i leaves _visLockProcessedUpTo at i too (Math.Max(i, lineStart) == i, since
        // lineStart <= i by construction) -- _visLockOriginSample now lands near the actual lock point,
        // not the buffer end. Consequence: piece 6c's mid-reception re-verification
        // (TryVisLockStateMachine(_consumedSamples) inside TryProcessBuffer's per-line loop) used to
        // be an effective no-op for the rest of a bulk-pushed, sync-bypass-locked transmission (its
        // own bound, _consumedSamples, could never catch up to a _visLockProcessedUpTo already pinned
        // at the buffer end) -- it now actually runs, scanning the locked transmission's own image
        // content for a stronger/cleaner VIS lock. This is closer to legacy's behavior at its SHIPPED
        // DEFAULTS, not a universal legacy truth -- round-1-review's original wording here said
        // legacy's case-0 trigger is "permanently-ungated," which round-2-review found to be wrong:
        // the whole switch (sstv.cpp:1897) only runs at all while m_Sync is set because of the
        // enclosing gate at sstv.cpp:1889, `if(!m_Sync || m_SyncRestart || m_SyncAVT)`, which is
        // satisfied by m_SyncRestart defaulting to 1 (sstv.cpp:1486) -- a real, user-toggleable option
        // (Option.cpp:611, Main.cpp:1857/10907/11887) now exposed as `_syncRestartEnabled` (see that
        // field's own doc comment) -- this port no longer hard-wires it on with no way to disable. So
        // "the same way legacy does" means "at legacy's shipped defaults," not "unconditionally in
        // every configuration." That framing correction
        // doesn't change the substance: this remains a genuine improvement in fidelity relative to
        // this port's own prior (accidentally-inert) behavior, but one that extends
        // VisLockStateMachine's own already-documented, already-accepted false-positive risk (see its
        // class doc comment) to a scenario (mid-reception, bulk-pushed, sync-bypass-locked) that was
        // previously immune to it by accident. Not given a dedicated false-positive-forcing test,
        // matching this project's own established precedent for that same risk elsewhere
        // (VisLockStateMachine's own doc comment: hand-verified reachable, deliberately not chased
        // with a fixture) -- SyncScanInterleaveTests' Assert.Equal(0, restartCount) is, incidentally,
        // a real (if narrow) negative check that this newly-reachable path does not false-positive on
        // exactly the fixture (a Robot 36 transmission) VisLockStateMachine's own doc comment names as
        // the hand-verified false-positive risk case -- see PiecesSixCReachabilityTests for a positive
        // check that the path actually engages, not just that it stays silent.
        _visLockStateMachine.Reset();
        _visLockProcessedUpTo = Math.Max(_visLockProcessedUpTo, _consumedSamples);
        _visLockOriginSample = _visLockProcessedUpTo;

        // S8 fix: _narrowFskProcessedUpTo needs the exact same fast-forward, for the exact same
        // reason this paragraph's own comment above already explains for _visLockProcessedUpTo --
        // regression caught by the full suite (LegacyDerivedSpansTests.FskSpaceCursor_NeverResets_
        // AcrossBackToBackNarrowTransmissions: 4 ModeDetected events instead of 2). Root cause: the
        // PERSISTENT _narrowFskDecoder had never actually been fed samples 0.._consumedSamples when
        // the FIXED-WINDOW path (TryDecodeNarrowModeHeader, which uses its own separate, local,
        // fresh decoder instance) is what found this match -- without this fast-forward,
        // TryVisLockStateMachine's own mid-reception scan (TryNarrowFskScan) would start feeding the
        // persistent instance from sample 0 the first time it runs, rediscovering the SAME real
        // header a second time and firing a spurious restart into the transmission that just locked.
        // Deliberately NOT the same fix as EndOfImage's own +0.5s jump (auditor plan-review's actual
        // blocker was about THAT jump specifically starving this decoder of the post-image window) --
        // this is the smaller, always-necessary "don't re-examine what was just committed" correction
        // every other detector's own Commit()-side fast-forward already performs, regardless of which
        // path found the match.
        _narrowFskProcessedUpTo = Math.Max(_narrowFskProcessedUpTo, _consumedSamples);

        // Piece 8c: AVT has no equivalent of this mechanism at all -- legacy's SyncSSTV itself
        // early-outs for smAVT (Main.cpp:3754-3758: zeroes the offset, clears m_wBgn immediately,
        // no waiting for buffered lines), so AFC/slant initialize immediately here exactly as before
        // this piece existed. Every other mode defers to TryResolveSyncAnchorCorrection (called from
        // TryProcessBuffer, once enough samples are buffered) -- see _pendingAnchorCorrectionMode's
        // own doc comment for why the deferral is needed.
        if (effectiveMode == SstvModeRegistry.Avt)
        {
            FinalizeAnchorAndStartDecoding(effectiveMode);
        }
        else
        {
            _pendingAnchorCorrectionMode = effectiveMode;
        }
    }

    // Piece 8c: the deferred tail of Commit() -- everything that must see the FINAL (possibly
    // sync-anchor-corrected) _consumedSamples, not the provisional VIS-lock-derived value Commit()
    // itself sets. Called either immediately from Commit() (AVT, which has no correction step) or
    // from TryProcessBuffer once TryResolveSyncAnchorCorrection succeeds.
    private void FinalizeAnchorAndStartDecoding(SstvModeDefinition matched)
    {
        // An upper bound on this image's own total audio extent -- exactly the *nominal* (pre-slant-
        // correction) duration, not a deliberately generous margin (corrected wording, caught by
        // independent review: with Auto Slant active on a slow clock, actual elapsed samples can
        // exceed this nominal figure by a small amount, meaning the image's last lines are AFC-
        // corrected against slightly stale state -- bounded by ~0.1% of image length in practice,
        // well under one line at realistic drift, not fixed further here). See ApplyAfcCorrections'
        // doc comment for why this bound exists at all: without it, a single TryProcessBuffer call
        // can eagerly AFC-correct straight through this image's own footer/dead-zone and into a not-
        // yet-detected *next* transmission's audio, using a correction tuned to this image's own
        // frequency offset. A real bug caught by independent review once EndOfImage made a second
        // Commit() within one decoder instance possible at all.
        var totalTransmissionLines = matched.ImageHeight / _lineDecoder!.RowsPerTransmissionLine;
        _afcBoundSample = _consumedSamples + (int)Math.Round(totalTransmissionLines * matched.LineDurationMs / 1000.0 * _sampleRate);

        InitializeAfc(matched);
        InitializeSlant(matched);

        // ISstvDecoder.AnchorLagSamples: captured BEFORE the raise below, from the exact same
        // corrected _consumedSamples anchor _afcBoundSample above was just computed from -- see
        // that property's own doc comment (fsk_cwid.md §8.2) for why this is the single point
        // where the lag is knowable without a caller-side backfill buffer.
        Volatile.Write(ref _anchorLagSamples, TotalSamplesReceived - _consumedSamples);

        // ISstvDecoder.ReceptionSequence: bumped BEFORE the raise below, at the single true
        // ModeDetected raise site in this class -- see that property's own doc comment for why this
        // is the only correct place (never on DecodeRestarted).
        Interlocked.Increment(ref _receptionSequence);
        RaiseSubscribers(ModeDetected, matched);
    }

    // Piece 8c: port of TMmsstv::SyncSSTV (Main.cpp:3751-3799) -- see SyncAnchorCorrector's own doc
    // comment for the fold/argmax algorithm and sign derivation this wraps. Returns false (and
    // consumes nothing) if fewer than `e` transmission lines' worth of samples are buffered yet from
    // the provisional anchor, matching legacy's own DrawSSTV/m_wBgn gate (Main.cpp:4917-4986):
    // return without drawing until enough data has arrived, re-checked on every TryProcessBuffer
    // call exactly like every other "not enough data yet" path in this class.
    //
    // Uses a DEDICATED SyncEnvelopeDetector instance, not the one InitializeSlant creates -- legacy
    // shares one continuously-running d12/d19 computation for everything (a passive buffer, m_B12,
    // read back later in whatever order SyncSSTV wants), but this port's SyncEnvelopeDetector is a
    // stateful *streaming* filter that must see each sample exactly once, in order -- feeding it
    // through this fold AND then again through ApplySlantTracking's own instance would double-
    // process. Matches this file's own established precedent for the same tradeoff (_syncBypass1Tracker's
    // own doc comment: "legacy computes its own d12/d19 once per Do() call and shares them...
    // recombining that here would mean [merging instances], a bigger change... left for a follow-up").
    // Harmless here specifically because InitializeSlant already creates a brand-new
    // SyncEnvelopeDetector from scratch on every call (confirmed by reading it) -- this temporary
    // instance's state is simply discarded once the fold completes, and slant tracking starts fresh
    // from the corrected _consumedSamples exactly as it already would have from the uncorrected one.
    private bool TryResolveSyncAnchorCorrection(SstvModeDefinition mode)
    {
        var lineWidthSamples = mode.LineDurationMs / 1000.0 * _sampleRate;
        var pageWidthSamples = (int)lineWidthSamples;

        // e=3 vs e=4: legacy's real condition (Main.cpp:3760) is `m_SyncAccuracy && sys.m_UseRxBuff
        // && SSTVSET.m_TW >= SSTVSET.m_SampFreq`, three independent terms.
        //
        // RX buffer subsystem Phase 3: `sys.m_UseRxBuff` is now real in this port (RxBufferMode), so
        // `_rxBufferMode != RxBufferMode.Off` (not `== On` -- Extended counts too, same reasoning as
        // TryAutoSync's own branch 1/2 gating) is ported below as a genuine condition, not folded away.
        //
        // `m_SyncAccuracy` is STILL hardcoded truthy -- round-2 auditor finding: this is a real,
        // separate, persisted, user-settable 3-way legacy option (Main.cpp:1861's `SyncAccuracy` ini
        // key, menu handlers Main.cpp:13412/:13415/:13418, UI Main.cpp:11969), NOT merely "no UI/
        // config knob" as an earlier version of this comment claimed -- this port has no equivalent
        // toggle and treats it as always nonzero (truthy), same as before this phase. `Main.cpp:3760`
        // only tests `m_SyncAccuracy`'s truthiness (`&&`), so its two nonzero values (1/2) are
        // indistinguishable there regardless.
        //
        // Round-3 correction (an earlier version of this comment had the divergence direction
        // backwards): under RxBufferMode.Off this port now correctly yields e=4 -- matching legacy at
        // EVERY m_SyncAccuracy value, since `sys.m_UseRxBuff` alone being false already forces legacy's
        // whole `&&`-chain false regardless of m_SyncAccuracy. There is NO remaining divergence when
        // Off is selected. The one still-open divergence is the opposite case:
        // RxBufferMode != Off (this port takes the m_TW>=m_SampFreq branch) while legacy's real
        // m_SyncAccuracy happens to be 0 (legacy forces e=4 regardless) -- reachable for Scottie DX/
        // PD240/MP140/MP175/MN140 whenever a real user has SyncAccuracy set to its "Low"/0 option, not
        // either golden-vector fixture. Same gap as before this phase, not introduced by it.
        var lineCount = mode.LineDurationMs >= 1000.0 && _rxBufferMode != RxBufferMode.Off ? 3 : 4;
        var neededSamples = lineCount * pageWidthSamples;

        if (TotalSamplesReceived - _consumedSamples < neededSamples)
        {
            return false;
        }

        var origin = _consumedSamples;
        var targetToneHz = mode.NarrowModeCode is not null ? 1900.0 : 1200.0;
        var detector = new SyncEnvelopeDetector(_sampleRate, targetToneHz);

        // This port's anchor (_consumedSamples) is the start of LineSegments[0], not legacy's own
        // internal "phase 0" -- for every mode except the Scottie family those agree (TX places its
        // sync tone first), but Scottie's real TX order (LineSCT, Main.cpp:6620-6640) puts the
        // tracked sync tone roughly two-thirds into the line (see SyncAnchorCorrector's own doc
        // comment for the full derivation and the wraparound-trick bug this replaced). Computing the
        // pre-sync-segment offset directly from LineSegments (rather than a new hardcoded table)
        // keeps this correct automatically and is 0 -- a no-op -- for every mode whose tracked sync
        // segment is already first.
        // Functional-audit fix (D6, round 1): was an inlined duplicate of this exact computation
        // (exact `==` on `double` frequencies, and -- the real defect -- silently falling through to
        // `preSyncSegmentOffsetMs == 0` on no match instead of throwing) rather than a call to the
        // shared, already-tested SstvModeRegistry.GetSyncSegmentOffsetMs (used elsewhere in this
        // class for the identical value, e.g. InitializeSlant). Unreachable today (every non-AVT mode
        // has a matching sync segment; AVT never reaches this method), but a silent duplicate of a
        // helper whose whole other purpose is to throw loudly on that exact condition is a
        // maintenance hazard -- a future mode registration bug here would have shifted the anchor a
        // full transmission line backward with no diagnostic, instead of throwing like every other
        // caller of the shared helper does.
        var preSyncSegmentOffsetMs = SstvModeRegistry.GetSyncSegmentOffsetMs(mode);
        var syncPeakOffsetSamples = (preSyncSegmentOffsetMs + SstvModeRegistry.GetSyncPeakOffsetMs(mode)) / 1000.0 * _sampleRate;

        // Round-1-review-equivalent fix, caught by this piece's own test run (not assumed): legacy's
        // real d12/d19 filter chain runs continuously from long before any given lock point (it's
        // CSSTVDEM's own persistent member, never reset -- confirmed elsewhere in this codebase,
        // e.g. CLVL/m_lvl), so by the time SyncSSTV's fold reads it back, it's long since settled.
        // A brand-new SyncEnvelopeDetector instance starting cold exactly at `origin` has no such
        // history -- TankFilter's resonator ring-up/decay time constant is
        // sampleRate/(pi*bandwidthHz) (~35 samples/~3.2ms at 100Hz/11025Hz), plus the smoother's own
        // settling -- confirmed empirically to matter: without this warm-up, the synthetic
        // self-round-trip suite (SstvRoundTripTests) regressed hard for Scottie S1/S2/DX and Robot 36
        // (average delta jumping from ~10-13 to 50-73), immediately caught by running the full suite
        // after wiring this piece in, per this session's "test early, test often" instruction. Warm
        // up on real, already-buffered samples before `origin` (the VIS header itself, ~900ms+, or
        // whatever preceded this lock) without accumulating that output into any fold bin -- 2000
        // samples (~180ms at 11025Hz) is comfortably more than an order of magnitude past the
        // resonator's own ~3.2ms time constant, clamped to what's actually available before `origin`.
        //
        // Code-review finding (Band-1 S2 fix): this clamp is against absolute 0, not _bufferBase --
        // i.e. it still assumes `origin - AnchorWarmupSamples` is always safe to read. Correct only
        // because TrimBuffers' own watermark formulas both subtract at least AnchorWarmupSamples
        // (pre-lock: folded into preLockRetentionSamples; locked: the explicit `- AnchorWarmupSamples`
        // at the end of that branch) before ever advancing _bufferBase -- this margin is what makes
        // that safe, not this clamp itself.
        var warmupSamples = Math.Min(origin, AnchorWarmupSamples);
        for (var w = origin - warmupSamples; w < origin; w++)
        {
            detector.ProcessSample(AgcSampleAt(w));
        }

        var delta = SyncAnchorCorrector.ComputeAnchorCorrection(
            lineWidthSamples,
            syncPeakOffsetSamples,
            lineCount,
            n => detector.ProcessSample(AgcSampleAt(origin + n)));

        // Piece: Hilbert demodulator port -- Main.cpp:3794's Hilbert-specific term,
        // `if (dp->m_Type == 2) n -= dp->m_hill.m_htap/4;`, applied to legacy's own `n` (a PHASE,
        // per SyncAnchorCorrector's own doc comment on the sign derivation this class already
        // established: the correction to a SAMPLE-CURSOR variable is `-n`, the OPPOSITE sign of
        // legacy's literal `n`). Legacy's term makes `n` MORE NEGATIVE, so by that same established
        // sign flip the correction here is INCREASED (added, not subtracted) -- confirmed two
        // independent ways during plan-review (algebraic substitution into the `-n` relationship, and
        // a physical cross-check: the Hilbert path delays the picture stream by ~htap samples
        // relative to the sync envelope this fold tracks, so the picture arrives later and the anchor
        // must move later too).
        //
        // Demod-type subsystem Phase 2, landmine #1 -- re-gated on `_demodType == Hilbert`, matching
        // legacy's own `m_Type==2` check exactly (unconditional only while Hilbert was the sole
        // reachable main-path type). Neither PllFmDemodulator nor ZeroCrossingFrequencyCounter has an
        // equivalent group-delay constant, matching legacy: CPLL/CFQC have no such correction term at
        // all -- the confirmed-correct behavior for those two types is simply no correction here.
        if (_demodType == DemodType.Hilbert)
        {
            delta += _demodulator.HalfTap / 4;
        }

        // Clamped deliberately, and NOT because it matches legacy -- it does not.
        //
        // An earlier comment here claimed this was the direct equivalent of legacy's
        // `if (n<0) continue` (Main.cpp:4146). That was backwards. The sign conventions run opposite:
        // legacy's `n` is a PHASE (`n = OFP - argmax`, Main.cpp:3784), this is a SAMPLE CURSOR.
        // Legacy's `continue` branch fires on `m_rBase < 0`, which corresponds to a POSITIVE
        // correction here and never reaches this guard. This branch corresponds to legacy's
        // `m_rBase > 0`, where legacy does NOT skip -- it draws from page index 0 at phase
        // `m_rBase`, keeping every pixel in its correct column and row and leaving row 0's left
        // portion unpainted. So clamping discards registration where legacy preserves it.
        //
        // Left as-is by user decision 2026-09-10 after measurement, not by oversight:
        //   - Reachable only via ForceMode within ~0.28s of a fresh decoder's first sample (Scottie
        //     worst case). A mid-transmission tune-in CANNOT reach it -- VIS lock and narrow-FSK
        //     anchor past the danger window, and the sync-bypass anchor is peak-derived so its
        //     correction is ~0 by construction.
        //   - The predicted failure (a vertical seam, up to ~65% of width on Scottie) could NOT be
        //     reproduced. The branch does execute in a provoking harness, yet clamp and wrap produced
        //     no difference this port could measure, at the top of the picture or the middle.
        //   - Wrapping by `+(int)lineWidthSamples` is the closer-to-legacy behaviour and is a ~3-line
        //     change if this is ever revisited. It was written, tested and reverted for want of any
        //     test that could tell the two apart.
        // Full analysis: BACKLOG.md D6.
        _consumedSamples = Math.Max(0, origin + delta);
        _idealLineStartSample = _consumedSamples; // MUST 4 -- see field's own doc comment

        // Round-1-Opus-review fix: Commit() already fast-forwarded _visLockProcessedUpTo/
        // _visLockOriginSample past the PROVISIONAL (pre-correction) _consumedSamples via its own
        // Math.Max -- see Commit()'s own doc comment for why that fast-forward exists at all
        // (piece 6c's re-verification must never re-examine samples already accounted for, or it
        // spuriously rediscovers the header that just committed and fires a self-triggered restart).
        // A positive delta here moves the FINAL _consumedSamples past that provisional value without
        // this line, leaving a [origin, origin+delta) gap that piece 6c's first catch-up call
        // (TryVisLockStateMachine, on this image's very first decoded line) would scan fresh --
        // exactly the failure mode Commit()'s own Math.Max exists to prevent, just reopened one step
        // later by this piece. Negative delta needs no equivalent fix: _visLockProcessedUpTo is
        // already >= the (now smaller) final _consumedSamples, matching Commit()'s existing
        // Math.Max semantics (never move this cursor backward).
        _visLockProcessedUpTo = Math.Max(_visLockProcessedUpTo, _consumedSamples);
        _visLockOriginSample = _visLockProcessedUpTo;

        // S8 fix: same re-fast-forward as above, same reason -- Commit()'s own provisional
        // fast-forward for _narrowFskProcessedUpTo (see that method's own doc comment) needs
        // reapplying here too if this correction moved _consumedSamples further forward.
        _narrowFskProcessedUpTo = Math.Max(_narrowFskProcessedUpTo, _consumedSamples);

        return true;
    }
}
