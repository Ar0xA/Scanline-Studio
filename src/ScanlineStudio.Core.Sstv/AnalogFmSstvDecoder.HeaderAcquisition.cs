using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

public sealed partial class AnalogFmSstvDecoder
{
    private bool TryDecodeHeader()
    {
        // AVT's training-lock resolution spans multiple TryDecodeHeader calls once entered (its
        // completion point is data-dependent, not a fixed duration) -- while pending, skip straight
        // back into it rather than re-running header detection or falling through to the other
        // fallbacks, which would be wrong once the mode is already known to be AVT. SHOULD item 7
        // (spec/14-roadmap.md): TryResolveAvtTraining's own loop now also races a narrow-FSK scan in
        // lockstep -- see that method's own doc comment for why this needs to be interleaved
        // per-sample rather than checked once here (a bulk single push lets TryResolveAvtTraining's
        // own while loop consume the whole buffer in one call, before TryDecodeHeader would ever be
        // re-entered to try a narrow check "next time").
        if (_avtTrainingPending)
        {
            return TryResolveAvtTraining();
        }

        // Band-1 S2 fix (pre-Phase-2 audit): once _fixedWindowExhausted is true, the fixed-window
        // paths below are provably dead for this epoch (they are pure functions of (headerStart,
        // buffered data), tried again unchanged on every call -- see TryInterleavedHeaderScan's own
        // doc comment). Skipping them once exhausted is both an efficiency win (no more
        // fresh-detector reconstruction for a call that can only ever fail) and what makes it safe
        // for TrimBuffers to stop retaining data all the way back at the stale _consumedSamples,
        // which is otherwise NEVER advanced pre-lock (a real bug an early draft of this fix hit:
        // BufferedSampleCount_StaysBounded_ForLongNeverLockingStream, a long-idle stream that never
        // locks would retain everything forever).
        //
        // A more ambitious earlier draft tried periodically RE-ANCHORING _consumedSamples forward
        // (advancing it to track each trim, re-arming this flag so the fixed-window path got a
        // "fresh" shot at the new point) instead of just skipping. Reverted: the re-anchor point is
        // arbitrary relative to any real header's actual start (tied to when trimming happens to
        // trigger, not to signal content), so the odds of it ever landing exactly where a real
        // header begins are negligible in practice -- the added complexity (re-arming, re-closing
        // TryInterleavedHeaderScan's gate) bought no real precision back, confirmed empirically via
        // this same test still hitting the fallback's own looser anchor either way. Simply skipping
        // is simpler, carries no risk of reopening the S3 race via re-arming, and produces the
        // identical practical outcome: any header arriving well after this epoch's one legitimate
        // fixed-window opportunity was exhausted is found via TryInterleavedHeaderScan's fallback,
        // at that path's own already-documented, already-accepted anchor precision (see
        // SyncBypassDetectionTests/VisLockStateMachineDecoderTests' own looser tolerances) -- a
        // pre-existing architectural property this fix doesn't change, not a regression it causes.
        if (!_fixedWindowExhausted)
        {
            var discriminatorEndSampleCount = (int)Math.Round(NarrowDiscriminatorWindowEndMs / 1000.0 * _sampleRate);
            if (TotalSamplesReceived - _consumedSamples >= discriminatorEndSampleCount)
            {
                var windowStart = _consumedSamples + (int)Math.Round(NarrowDiscriminatorWindowStartMs / 1000.0 * _sampleRate);
                var windowEnd = _consumedSamples + discriminatorEndSampleCount;
                var avgFreq = AverageFrequencyInWindow(windowStart, windowEnd);

                var decoded = avgFreq > NarrowDiscriminatorThresholdHz
                    ? TryDecodeNarrowModeHeader()
                    : TryDecodeVisHeader();
                if (decoded)
                {
                    return true;
                }

                if (_avtTrainingPending)
                {
                    // AVT identified from its VIS byte this same call, but not yet resolved -- wait for
                    // more samples via the pending check above, don't fall through to the other fallbacks.
                    return false;
                }
            }
        }

        // The fixed-window header paths above are tried first, not because legacy orders them that
        // way (it can't: every mechanism below runs per-sample in the same real-time loop), but
        // because this port's header path can see its own fixed-duration window resolve in a single
        // call against a bulk-pushed buffer, while VisLockStateMachine/m_sint1/m_sint2/m_sint3 all
        // need to scan forward sample-by-sample. Trying header-decode first is what actually
        // reproduces the real race's outcome for a signal with a valid header (header wins, every
        // time, well before either sample-by-sample mechanism could resolve) instead of letting a
        // single large PushSamples call hand them an unrealistic head start over the whole future
        // stream at once -- the same category of bulk-vs-streaming ordering bug documented on
        // ApplySlantTracking above. The fallback below only ever actually resolves anything for a
        // transmission with no valid (or not-yet-arrived) fixed-window header to decode, which is
        // the only case where it needs to run.
        //
        // m_sint1-decoder-ordering-fix: VisLockStateMachine and the sync-interval bypass
        // (TrySyncIntervalDetectionStep, m_sint1/m_sint2/m_sint3) used to be tried here as two
        // separate, sequential full-buffer scans -- VisLockStateMachine over everything first, and
        // only if THAT found nothing at all did the sync-bypass detectors ever see a single sample.
        // That was a real bug (spec/14-roadmap.md's own write-up, "m_sint1's decoder-level priority
        // is effectively inverted from legacy's real per-sample interleaving"), not just a stylistic
        // difference: VisLockStateMachine's own doc comment documents a known false-positive risk
        // (enough consecutive dark/sync-heavy image content can, in principle, assemble a byte
        // identical to a real VIS code) -- in true legacy execution, m_sint1/m_sint2 checking every
        // sample gives a genuine chance for the *correct* mode's periodicity to be recognized before
        // a spurious false-positive byte-assembly completes, but scanning the sync-bypass detectors
        // only *after* VisLockStateMachine has already exhausted the entire buffer removes that
        // protection entirely in this port. TryInterleavedHeaderScan fixes this by running both
        // mechanisms sample-by-sample in lockstep, in legacy's own real per-sample order (see that
        // method's own doc comment).
        //
        // Round-1-review correction: an earlier version of this comment claimed the fix "is a no-op
        // for any transmission with a real, decodable VIS header" -- overclaimed, and contradicted by
        // this fix's own regression test (SyncScanInterleaveTests). What's actually true, and all
        // that's needed: VisLockStateMachine's own internal per-sample state evolution is unaffected
        // by the interleave (it still reaches the same absolute sample index for the SAME
        // transmission's header, since nothing about its own stepping changed) -- but the OUTCOME
        // (which mode is detected first) genuinely can and does change whenever a sync-bypass
        // tracker (m_sint1 has no mode allowlist -- it can recognize *any* mode's periodicity, not
        // just SyncBypassTrustedModes) matches at an earlier sample index than where
        // VisLockStateMachine would otherwise resolve, e.g. a real, headerless transmission sitting
        // earlier in the same buffer than a later transmission's own real header. That outcome
        // change is the entire point of this fix, not an accepted side effect of it.
        return TryInterleavedHeaderScan();
    }

    // S8 fix (spec/14-roadmap.md): shared by TryInterleavedHeaderScan (pre-lock) and
    // TryVisLockStateMachine (mid-reception) -- legacy's real DecodeFSK call has no equivalent of
    // either method's own gating (see _narrowFskDecoder's own field doc comment), so unlike
    // VisLockStateMachine (which needs two separately-shaped call sites, one merged into the
    // sync-bypass interleave, one standalone), one shared scan loop with a caller-supplied bound
    // correctly covers both cases. Each caller passes its OWN already-correct bound
    // (TryInterleavedHeaderScan passes scanBound, TryVisLockStateMachine passes its own
    // upperBoundSample) -- this method does no bound computation of its own beyond clamping to
    // TotalSamplesReceived as a final safety net.
    //
    // SHOULD item 7 (spec/14-roadmap.md) added a THIRD caller, TryResolveAvtTraining -- comprehensive
    // code-review correction, an earlier version of this comment (and its own "shared by BOTH")
    // predates that and was left stale. That caller passes its own two bounds
    // (Math.Min(TotalSamplesReceived, _avtTrainingProcessedUpTo) for the initial catch-up,
    // _avtTrainingProcessedUpTo + 1 per training-loop iteration) -- same pattern, a caller-supplied
    // bound this method doesn't second-guess.
    //
    // Round-1 code-level-review-caught regression, corrected here: an earlier draft had
    // TryInterleavedHeaderScan call this with TotalSamplesReceived directly instead of scanBound,
    // reasoning that this scan "has no fixed-window sibling to race/protect against." That reasoning
    // addressed the wrong risk -- scanBound isn't ONLY about racing a fixed-window path, it's also
    // what keeps ANY pre-lock detector from reading ahead into a SECOND, not-yet-legitimately-reached
    // transmission on a bulk single-PushSamples call (the same "bulk vs. streaming ordering" class
    // Band-1 items 2+3 and TryVisLockStateMachine's own doc comment already document elsewhere).
    // Caught by LegacyDerivedSpansTests.FskSpaceCursor_NeverResets_AcrossBackToBackNarrowTransmissions
    // (ModeDetected fired 4 times instead of 2 -- this scan discovered the SECOND transmission's real
    // header while the first was still mid-decode) and BandpassCacheChunkInvarianceTests
    // (chunk-size-dependent anchor drift on an unrelated fixture, same root cause) -- not anticipated
    // by either plan-review round, found empirically by running the full suite as this project's own
    // methodology requires before calling a piece done.
    //
    // _narrowFskProcessedUpTo is still never RESET or jumped specifically by EndOfImage (unlike
    // _syncBypassProcessedUpTo/_visLockProcessedUpTo, which DO get fast-forwarded 500ms at every
    // EndOfImage, see that method's own resumeFrom) -- that part of the original design goal is
    // unaffected by this correction, and is still why this cursor needs its own separate loop here
    // rather than sharing TryInterleavedHeaderScan's lockstep for-statement/entry-invariant outright.
    // (It IS fast-forwarded by Commit()'s own Math.Max whenever any OTHER detector's match commits --
    // see this field's own doc comment for the milestone-audit correction of an earlier, wrong version
    // of this same claim -- just never RESET to a fresh origin the way the sync-bypass/VIS-lock pair
    // is.) What changed by the MUST fix above is only the BOUND passed to each call, not this cursor's
    // own advancement/reset semantics.
    private bool TryNarrowFskScan(int upperBoundSample)
    {
        var bound = Math.Min(TotalSamplesReceived, upperBoundSample);
        for (; _narrowFskProcessedUpTo < bound; _narrowFskProcessedUpTo++)
        {
            var sampleIndex = _narrowFskProcessedUpTo;
            var m = (int)D19At(sampleIndex);
            var s = (int)FskSpaceAt(sampleIndex);

            var result = _narrowFskDecoder.ProcessSample(m, s);
            if (result is null)
            {
                continue;
            }

            // Station-ID (STX 0x2a) result -- see FskDecodeResult's own doc comment. Deliberately
            // NOT a mode lock: no Commit(), no `return true` (which would abort the caller's scan --
            // see StationIdDecoded's own doc comment for why that would silently break AVT
            // training/header scanning). Deliver via the event and keep scanning for more samples up
            // to `bound`, exactly like the "unregistered mode code" case below already does.
            if (result.Value.ModeCode is null)
            {
                // fsk_cwid.md A1 / auditor code-review finding: stamped here too, not just by
                // RestartableSstvDecoder -- Program.cs only ever registers the wrapper today, so this
                // class's own stamp is currently overwritten unconditionally before any real
                // subscriber sees it, but leaving it at the default "unset" sentinel would be a latent
                // trap for any future direct ISstvDecoder consumer of this class (A2/A5 both correlate
                // on this field). Interlocked.Read matches this class's own ReceptionSequence property
                // getter above.
                RaiseSubscribers(StationIdDecoded, new FskStationIdDecodedInfo(
                    result.Value.StationIdCallsign, result.Value.StationIdCompactNr, result.Value.StationIdNrText,
                    Interlocked.Read(ref _receptionSequence)));
                continue;
            }

            var mode = SstvModeRegistry.FindByNarrowCode(result.Value.ModeCode.Value);
            if (mode is null)
            {
                // Unregistered mode code -- legacy resumes scanning rather than giving up
                // (sstv.cpp:2588-2597), and so does NarrowFskHeaderDecoder internally (it already
                // reset its own _mode to 0 before returning here) -- keep scanning instead of
                // aborting, matching this class's own always-resume behavior everywhere else.
                continue;
            }

            // Anchor: see VisHeader.NarrowPostBitClockOriginDurationMs's own doc comment for the full
            // derivation (auditor plan-review's corrected reference point, the mode-3->4 transition,
            // not the mode-0 trigger). originSample is always strictly < sampleIndex (a lock needs at
            // least 24 bits' worth of samples after the origin) -- code-level auditor review correction,
            // an earlier version of this comment said "not necessarily <=", backwards. This cursor's own
            // strictly-by-1 advancement (see this method's own doc comment) guarantees originSample is a
            // real, already-fed absolute index either way; it's never used as a buffer index itself, so
            // the exact relationship doesn't matter for correctness, only for understanding the formula.
            var originSample = sampleIndex - result.Value.SamplesSinceBitClockOrigin;
            var anchor = originSample + MsToSamples(VisHeader.NarrowPostBitClockOriginDurationMs);

            // anchor can land AHEAD of TotalSamplesReceived when a match completes near the buffer's
            // current tail (the 539ms remainder is added forward from a point already in the past,
            // not measured from "now") -- auditor plan-review flagged this as an untested boundary.
            // Checked, not guarded: Commit() itself does no upper clamp (only Math.Max(0, ...) against
            // going negative), and TryProcessBuffer's own per-line loop already treats "not enough
            // samples yet" (TotalSamplesReceived - _consumedSamples < lineSampleCount) as "wait for
            // more data," which is exactly correct here too -- the same implicit handling every other
            // commit path in this file already relies on when a match resolves close to the live edge.
            //
            // No manual _narrowFskProcessedUpTo++ here, matching every other detector's identical
            // note in this file: Commit() itself fast-forwards this cursor (see its own body) via
            // Math.Max, so incrementing afterward would desync it by exactly one sample.
            //
            // D0-audit round-11 fix: legacy's own real commit gate, `(m_SyncRestart || !m_Sync) &&
            // m_NextMode && (m_SyncMode >= 0)` (sstv.cpp:2592), applies ONLY to this commit, not to
            // DecodeFSK's own per-sample scan/decode above (which legacy runs unconditionally,
            // sstv.cpp:1858 -- see this method's own doc comment). `_syncRestartEnabled` stands in for
            // `m_SyncRestart`; `_mode is null` stands in for `!m_Sync` (this method has no direct
            // m_Sync-equivalent read of its own -- `_mode` already tracks the same "locked" state).
            // While locked with Lock/Restart disabled, a narrow match still gets scanned (keeps
            // _narrowFskDecoder's own internal state moving, still delivers StationIdDecoded above)
            // but must NOT abort/restart the in-progress image -- this call's own caller-side gate
            // used to be the only enforcement of that, one level up (see TryVisLockStateMachine's own
            // caller, in TryProcessBuffer), which over-suppressed the whole scan instead of just the
            // commit. D0-audit round-12 nit: this port has no equivalent of legacy's third term,
            // `m_SyncMode >= 0` -- legacy's only two `m_SyncMode = -1` assignments (sstv.cpp:1723,
            // transient inside Start(void); sstv.cpp:1765, Start(int, f==0)'s "set mode without
            // starting" arm) have no port counterpart, since PerformForceMode always commits
            // immediately. An unreachable divergence, not a gap to close.
            if (_syncRestartEnabled || _mode is null)
            {
                Commit(mode, anchor);
                return true;
            }

            continue;
        }

        return false;
    }

    // m_sint1-decoder-ordering-fix: this method's only remaining caller is piece 6c's mid-reception
    // re-verification (below, called once per decoded line while already locked) -- the OTHER
    // caller this doc comment used to describe (TryDecodeHeader's own pre-lock fallback) was
    // replaced by TryInterleavedHeaderScan, which calls _visLockStateMachine.ProcessSample directly
    // so it can interleave with the sync-bypass detectors. Bounded by upperBoundSample (the caller
    // passes _consumedSamples): never let this run ahead into not-yet-decoded content. Without that
    // bound, a single bulk PushSamples call containing a whole transmission followed by a second,
    // genuinely valid one would let this method discover the *real* second header on its very first
    // call (mid-decode of the first transmission's very first line) and "restart" onto it
    // immediately, abandoning a transmission this port had every ability to finish -- the same
    // category of bulk-vs-streaming ordering bug already documented on ApplySlantTracking, caught
    // here by this port's own end-to-end test, not by a theoretical review. Legacy's own m_sint1/
    // m_sint2/m_sint3 are gated behind `!m_Sync` (sstv.cpp:1899) and genuinely never run at all once
    // locked, which is why this call site is intentionally NOT merged with the sync-bypass step the
    // way TryInterleavedHeaderScan merges it pre-lock.
    //
    // S8 fix: TryNarrowFskScan is checked FIRST, matching legacy's own real per-sample order
    // (DecodeFSK, sstv.cpp:1858, runs unconditionally BEFORE the `if(!m_Sync||...)` block this
    // method's own VIS-lock scan corresponds to, sstv.cpp:1889) -- unlike VisLockStateMachine's own
    // AVT exclusion here, a narrow-FSK match needs no special-casing: TryNarrowFskScan already calls
    // the same Commit() this method's own VIS-lock branch does, which already performs the full
    // in-progress-image teardown either match needs (confirmed directly: Commit()'s body has no
    // AVT-specific or VIS-specific step, see its own doc comment).
    private bool TryVisLockStateMachine(int upperBoundSample)
    {
        if (TryNarrowFskScan(upperBoundSample))
        {
            return true;
        }

        var bound = Math.Min(TotalSamplesReceived, upperBoundSample);
        for (; _visLockProcessedUpTo < bound; _visLockProcessedUpTo++)
        {
            var result = _visLockStateMachine.ProcessSample(AgcSampleAt(_visLockProcessedUpTo));
            if (result is null)
            {
                continue;
            }

            // S7 fix (spec/14-roadmap.md): S31 originally left this deliberately discarding an AVT
            // match found here, since TryStartAvtTraining didn't perform the same in-progress-image
            // teardown Commit() does for every other mode, and result.Value.LineStartSample for AVT
            // means "end of its own first VIS block," not a line-0 anchor. AbandonInProgressImage()
            // (extracted from Commit()'s own former inline header, same fix) now provides that
            // teardown without also setting a new _mode, since AVT training isn't resolved yet here.
            // Returning true unconditionally (not TryStartAvtTraining's own bool) is required: this
            // method's caller (TryProcessBuffer's per-line loop) already fires DecodeRestarted with
            // its own pre-captured (pre-abandonment) mode local and re-enters the outer while(true)
            // loop's `if (_mode is null && !TryDecodeHeader())` check on any true return -- which
            // correctly routes into TryResolveAvtTraining on the next call whether training resolved
            // same-call (rare, _mode already Avt) or is still pending (_mode still null) -- the exact
            // same machinery the pre-lock path already relies on, no new machinery needed here.
            //
            // Auditor plan-review flagged, and this deliberately accepts: _syncBypassProcessedUpTo
            // (frozen at wherever the FIRST transmission locked, only re-anchored by EndOfImage, which
            // this path does not call) pins TrimBuffers' pre-lock-branch watermark there for the whole
            // pending window once _mode goes null -- retaining the abandoned image's audio rather than
            // trimming it, bounded by the same up-to-~7.1s AVT training window this port already
            // accepts elsewhere (TrimBuffers' own _avtTrainingPending term docs the crash this WOULD
            // cause without that term; this is "more retained than ideal", not a repeat of that bug).
            // A genuine second/interrupting AVT transmission arriving mid-reception is already a rare
            // case; the false-positive-lock cost this now carries (destroying a good image on a
            // spurious AVT match, matching legacy's own equally-uncorrectable case-3-through-8 shape)
            // is the same category of accepted risk VisLockStateMachine's own class doc comment and
            // S8's narrow-FSK mid-reception wiring already carry, not a new class of risk.
            if (result.Value.Mode == SstvModeRegistry.Avt)
            {
                AbandonInProgressImage();
                TryStartAvtTraining(_visLockOriginSample + result.Value.LineStartSample);
                return true;
            }

            // No manual _visLockProcessedUpTo++ here (an earlier version had one): Commit() itself
            // already sets both _visLockProcessedUpTo and _visLockOriginSample to the same
            // Math.Max()-derived value, so incrementing afterward would desync them by exactly 1
            // sample, biasing every subsequent anchor from this instance 1 sample (~0.09ms at
            // 11025Hz) early -- cosmetic, but caught and removed by independent review.
            Commit(result.Value.Mode, _visLockOriginSample + result.Value.LineStartSample);
            return true;
        }

        return false;
    }

    // Direct port of m_sint2's and m_sint3's trigger logic, both from the same `case 0` / `!m_Sync`
    // branch (sstv.cpp:1899-1946) -- merged into one per-sample loop here because legacy computes
    // d12/d19 exactly once per real Do() call and feeds both independent checks from those same
    // values in the same iteration; running two separate passes over _rawSamples would recompute
    // the same resonator/lowpass state twice for no benefit and would diverge from legacy's actual
    // single-pass structure. As of piece 7b, d12/d19/dsp are read from AgcSampleAt -- the same
    // AGC'd/scaled ±16384-ish signal legacy's own d12/d19/dsp are computed from (see LevelAgc's doc
    // comment). As of piece 7c, the absolute amplitude thresholds (m_SLvl/m_SLvl2/m_SLvl3) legacy
    // also checks on top of the relative comparisons are applied too -- closing a simplification
    // documented since m_sint2/m_sint3 were first ported (same simplification closed on
    // AfcTracker's m_lvl.m_CurMax>16 gate and VisLockStateMachine, same piece). Piece 7d: m_sint1
    // piggybacks on this same loop's primary threshold rather than scanning independently -- see
    // spec/14-roadmap.md and _syncBypass1Tracker's own doc comment.
    //
    // Undocumented-until-now divergence, caught by independent review: legacy gates all of this
    // behind m_SyncMode's case 0 (m_sint2's SyncMax additionally continues in case 1, sstv.cpp:1954-
    // 1956) -- once a real 1200Hz trigger fires and case 0 advances to case 1, both m_sint2's
    // SyncStart calls and m_sint3's whole phase latch effectively freeze until legacy falls back to
    // case 0. This port's merged loop has no equivalent gate and keeps feeding/evaluating both
    // trackers on every sample regardless, so it can in principle call TryStart/release the phase
    // latch at moments legacy's own state machine would not. Low severity in practice -- this path
    // only ever runs when nothing else has already locked -- but a real, if narrow, structural
    // difference from legacy, not silently absorbed now that it's been identified.
    //
    // Second, related divergence from piece 7d: legacy's case-0 body is straight-line code -- m_sint1
    // winning and calling Start() (sstv.cpp:1717-1747) does NOT stop the sibling m_sint3 block
    // (sstv.cpp:1925-1944) from also evaluating in that same sample, since Start() doesn't early-
    // return out of Do(). This port's per-sample loop returns immediately on any match (m_sint1,
    // then m_sint2, then m_sint3, matching legacy's real source order for which one is checked
    // first), so a same-sample m_sint1-then-m_sint3 double-fire can't happen here. Accepted as the
    // same low-severity category as the divergence above, not fixed: legacy's own same-sample
    // second fire is close to a no-op in practice (m_Sync is already 1 by the time it would matter),
    // and this port's Commit() already fully supersedes whichever tracker's match is acted on first.
    // Piece: m_sint1 decoder-ordering fix. Single-sample step, extracted from what used to be
    // TrySyncIntervalDetection's own for-loop body so it can be interleaved with
    // VisLockStateMachine.ProcessSample one sample at a time (see TryInterleavedHeaderScan) instead
    // of each mechanism scanning the whole available buffer before the other gets a turn. Reads and
    // (on no match) leaves _syncBypassProcessedUpTo unchanged -- the caller owns advancing it.
    private bool TrySyncIntervalDetectionStep()
    {
        var agcSample = AgcSampleAt(_syncBypassProcessedUpTo);
        var d12 = _syncBypass1200Detector.ProcessSample(agcSample);
        var d19 = _syncBypass1900Detector.ProcessSample(agcSample);
        var dsp = _syncBypassFskDetector.ProcessSample(agcSample);

        // SyncInc (sstv.cpp:1890-1892) -- unconditional for all three trackers, every sample,
        // before the case-0 body below.
        _syncBypass1Tracker.Increment();
        _syncBypassTracker.Increment();
        _syncBypassNarrowTracker.Increment();

        // Legacy RX case 0 (sstv.cpp:1900-1904): first priority, any recognized mode,
        // Search only. Data-bit threshold dips must not consume the held primary peak.
        var sint1Matched = TryStartPrimarySync();
        if (sint1Matched is not null)
        {
            CommitSyncBypassMatch(sint1Matched, _syncBypass1Tracker.LastPeakPositionSamples);
            return true;
        }

        // m_sint2 (sstv.cpp:1899-1911). Piece 7c: full 3-term condition (sstv.cpp:1905), not just
        // the relative d12>d19 -- d12>SLvl2 and the difference gate (d12-d19)>=SLvl2 are what
        // actually suppress false candidate peaks now that d12/d19 live on the AGC'd scale.
        //
        // S12 fix (spec/14-roadmap.md): legacy's own case 1 (sstv.cpp:1953-1957) keeps calling
        // m_sint2.SyncMax while held, but NEVER calls SyncStart there -- that's case-0-only
        // (sstv.cpp:1906-1911). Cases 2/9/3 (real VIS-bit decode/verify) have no m_sint2 code
        // at all. _visLockStateMachine's own state already tracks exactly this (Search=case0,
        // ConfirmLock=case1, DecodeVis/DecodeExtendedVis/Verify=case2/9/3), read here as of the
        // END of the PREVIOUS sample (this method runs before _visLockStateMachine.ProcessSample
        // for the same index, see TryInterleavedHeaderScan) -- matching legacy's own
        // switch(m_SyncMode) using m_SyncMode's pre-this-sample value. An earlier version gated
        // this on _syncBypass1PrimaryHeld alone, which only tracks the case-0<->1 boundary and
        // goes false again the instant d12 dips below SLvl during real VIS-bit decoding (exactly
        // the kind of momentary dip 1100/1300Hz data-bit tones cause) -- not the same as legacy's
        // real, code-absent case-2/9/3 freeze.
        //
        // One accepted, unreachable-in-practice divergence: legacy sets m_SyncMode=256 on a
        // SUCCESSFUL case-3 lock (sstv.cpp:2146), keeping m_sint2/m_sint3 frozen until Stop() --
        // VisLockStateMachine's own ProcessSample instead resets _state back to Search in that same
        // instant (see its own doc comment). Not a live gap here: every non-null ProcessSample
        // return exits TryInterleavedHeaderScan's scan loop immediately (either Commit()s, which
        // itself Reset()s this same state machine, or starts AVT training, which short-circuits
        // TryDecodeHeader entirely via _avtTrainingPending) -- so this method is never re-entered
        // with the stale post-lock Search value in between.
        // Functional-audit re-confirmation (D5, round 1): `VisLockStateMachineTests` pins
        // `IsAtOrBeforeConfirmLock`/`IsSearching` as isolated properties on that state machine, but
        // nothing pins THIS decoder's own assignment of them to the m_sint2/m_sint3 gates below --
        // swapping which gate reads which property, or deleting the m_sint3 gate at :3269 entirely,
        // has no test that would catch it. Deliberately not adding one this round for the same
        // budget reason as the load-bearing-order note above (TryInterleavedHeaderScan); flagged as
        // a known, explicitly open gap. Separately reconfirmed: legacy's m_sint1 (the case-0/1
        // trigger-and-peak tracker feeding TrySyncIntervalDetectionStep's OWN sibling block, not
        // shown in this excerpt) freezes during real VIS-bit decode (legacy only calls its
        // SyncTrig/SyncMax from cases 0/1); this port has no equivalent freeze and gates only on
        // `_syncBypass1PrimaryHeld`, which drops on the routine d12 dips 1100/1300Hz data-bit tones
        // cause -- an existing, documented, accepted divergence (see this method's own m_sint1
        // block), not newly found, still open, still not fixed this round.
        if (_visLockStateMachine.IsAtOrBeforeConfirmLock)
        {
            if (d12 > d19 && d12 > _slvl2 && d12 - d19 >= _slvl2)
            {
                _syncBypassTracker.UpdateMax(d12);
            }
            else if (_visLockStateMachine.IsSearching)
            {
                var matched = _syncBypassTracker.TryStart();
                if (matched is not null && SyncBypassTrustedModes.Contains(matched))
                {
                    CommitSyncBypassMatch(matched, _syncBypassTracker.LastPeakPositionSamples);
                    return true;
                }
            }
        }

        // m_sint3 (sstv.cpp:1924-1946) -- explicit SyncTrig-then-SyncMax edge latch, SyncStart
        // called once on the falling edge only, matching legacy's own m_SyncPhase gating. Piece
        // 7c: full 5-term condition (sstv.cpp:1926) -- note the last difference term is gated by
        // SLvl (not SLvl3), an asymmetry confirmed by reading the literal source, not assumed.
        //
        // S12 fix: legacy's entire m_sint3 block (sstv.cpp:1925-1944) lives inside case 0's own
        // `if(!m_Sync && m_MSync)` gate -- case 1 has ZERO m_sint3 references (not even a
        // SyncMax continuation, unlike m_sint2 above), and cases 2/9/3 have none either. This port
        // implements only the `!m_Sync` half of that gate (`m_MSync` -- a real, persisted legacy
        // user option -- has no equivalent here; see docs/removed-features.md's own entry, added
        // during the functional-audit sweep, chunk D5). Gating
        // the whole block on _visLockStateMachine.IsSearching freezes _syncBypassNarrowPhaseActive
        // and the tracker's own internal state exactly as legacy's untouched m_sint3 object stays
        // frozen outside case 0, resuming from the same phase once back in Search.
        if (_visLockStateMachine.IsSearching)
        {
            if (d19 > d12 && d19 > dsp && d19 > _slvl3 && d19 - d12 >= _slvl3 && d19 - dsp >= _slvl)
            {
                if (_syncBypassNarrowPhaseActive)
                {
                    _syncBypassNarrowTracker.UpdateMax(d19);
                }
                else
                {
                    _syncBypassNarrowTracker.Trigger(d19);
                    _syncBypassNarrowPhaseActive = true;
                }
            }
            else if (_syncBypassNarrowPhaseActive)
            {
                _syncBypassNarrowPhaseActive = false;
                var matchedNarrow = _syncBypassNarrowTracker.TryStart();
                if (matchedNarrow is not null)
                {
                    CommitSyncBypassMatch(matchedNarrow, _syncBypassNarrowTracker.LastPeakPositionSamples);
                    return true;
                }
            }
        }

        // sstv.cpp:1946-1950/1958-1972 -- the primary VIS-leader threshold, a sibling statement
        // to the m_sint1/m_sint2/m_sint3 blocks above (not nested inside any of them). This is
        // the ONLY place _syncBypass1Tracker gets new peak data: SyncTrig on the rising edge,
        // SyncMax while held (mirroring VisLockStateMachine's own Search/ConfirmLock condition
        // exactly -- see _syncBypass1Tracker's own doc comment for why this is a deliberate
        // second copy, not a shared instance -- that comment's own "no access to
        // VisLockStateMachine's cursor" framing no longer applies now that the two run interleaved
        // in TryInterleavedHeaderScan, but the copy itself is still needed: legacy computes its own
        // d12/d19 once and shares them; recombining that here would mean VisLockStateMachine no
        // longer owning its own envelope detectors, a bigger change than this fix, left for a
        // follow-up).
        UpdatePrimarySyncPeak(d12, d19);

        return false;
    }

    // Legacy case 0 consumes any peak held before a rejected VIS attempt once Search resumes.
    internal SstvModeDefinition? TryStartPrimarySync() =>
        _visLockStateMachine.IsSearching ? _syncBypass1Tracker.TryStart() : null;

    internal void UpdatePrimarySyncPeak(double d12, double d19)
    {
        if (!_visLockStateMachine.IsAtOrBeforeConfirmLock) return;
        if (d12 > d19 && d12 > _slvl && d12 - d19 >= _slvl)
        {
            if (_visLockStateMachine.IsSearching)
            {
                _syncBypass1Tracker.Trigger(d12);
            }
            else _syncBypass1Tracker.UpdateMax(d12);
        }
    }

    // Piece: m_sint1 decoder-ordering fix (spec/14-roadmap.md: "m_sint1's decoder-level priority is
    // effectively inverted from legacy's real per-sample interleaving"). TrySyncIntervalDetectionStep
    // (m_sint1/m_sint2/m_sint3) and VisLockStateMachine.ProcessSample are now interleaved sample by
    // sample instead of each scanning the *entire* available buffer before the other gets a turn --
    // see TryDecodeHeader's own doc comment for why the old sequential shape was a real bug, not
    // just a stylistic difference. Reproduces sstv.cpp:1897-1951's real per-sample order exactly:
    // m_sint1/m_sint2/m_sint3 (case 0's `if (!m_Sync && m_MSync)` block -- this port implements only
    // the `!m_Sync` half, see docs/removed-features.md's `m_MSync` entry) are checked first, every
    // sample, and only then (a sibling statement, same sample) the primary VIS-leader threshold that
    // drives m_SyncMode's case 0->1 transition -- TrySyncIntervalDetectionStep's own last statement
    // is a deliberate second copy of that same threshold (see its doc comment), so calling it before
    // VisLockStateMachine.ProcessSample reproduces legacy's real order.
    //
    // _syncBypassProcessedUpTo and _visLockProcessedUpTo are NOT always kept in lockstep outside
    // this method -- Commit() only fast-forwards _visLockProcessedUpTo (via Math.Max) when some
    // OTHER path (the fixed-window header paths) committed a match, and never touches
    // _syncBypassProcessedUpTo at all. But this method is only ever entered when _mode is null, and
    // the two cursors are always equal at that point -- confirmed by checking every _mode
    // assignment in this file, even though nothing enforces that generally. D0-audit round-11
    // correction: an earlier version of this comment claimed EndOfImage is the ONLY place `_mode is
    // null` becomes true again once a transmission has started -- false, AbandonInProgressImage
    // also nulls it directly, standalone, from the S7 mid-reception AVT hand-off inside
    // TryVisLockStateMachine (see that call site's own doc comment), WITHOUT re-syncing these two
    // cursors (a deliberately accepted extra-retention tradeoff, documented at that same call
    // site). That window doesn't threaten this method's own invariant, though: TryDecodeHeader's
    // own `_avtTrainingPending` short-circuit (see that method's own doc comment) routes every call
    // straight into TryResolveAvtTraining instead of this method for as long as the cursors could be
    // out of sync from that path -- this method is simply never entered during that window, by a
    // different mechanism than EndOfImage's own direct resync.
    private bool TryInterleavedHeaderScan()
    {
        // Round-1-review fix: this was a Debug.Assert, which .github/workflows/ci.yml's own
        // `--configuration Release` builds strip entirely ([Conditional("DEBUG")]) -- providing no
        // actual protection in the one place (CI) it would matter, and this was the only
        // Debug.Assert anywhere in this codebase, so it wasn't even following an established local
        // convention. An unconditional check gives this invariant (see this method's own doc
        // comment above) a real enforcement, not just a doc-comment claim, for every caller that
        // invokes PushSamples directly (every test in this suite, and any future caller that does the
        // same). Round-2-review caveat: MiniAudioCaptureSession.SamplesAvailable's own invocation is
        // wrapped in a deliberate bare try/catch (that class has no logger of its own) that forwards
        // into MiniAudioEngine.SamplesCaptured -- if this decoder is ever wired to that event, a
        // violation here would throw silently on every chunk with no log and no visible failure,
        // rather than surfacing the way it does today. Not fixed here (no production caller of
        // PushSamples exists yet, per a repo-wide grep at the time of this review), but worth a log
        // line at that wiring's call site when it's built.
        if (_syncBypassProcessedUpTo != _visLockProcessedUpTo)
        {
            throw new InvalidOperationException(
                $"{nameof(_syncBypassProcessedUpTo)} ({_syncBypassProcessedUpTo}) and {nameof(_visLockProcessedUpTo)} ({_visLockProcessedUpTo}) " +
                "must be equal on entry to this method -- see its own doc comment for why.");
        }

        // Band-1 S3 fix (pre-Phase-2 audit): a one-shot first-refusal gate for the fixed-window
        // header paths (TryDecodeVisHeader/TryDecodeNarrowModeHeader), empirically confirmed
        // necessary -- without it, this method's own scan bound ("whatever has arrived so far") lets
        // it commit at a DIFFERENT, less precise anchor purely because of how PushSamples calls
        // happen to be chunked, racing a fixed-window path that refuses to commit until its own full
        // header duration is buffered. Measured directly on a real Martin M1 encode: one-shot and
        // large/aligned chunk sizes let the fixed-window path win (anchor 40131); small/misaligned
        // chunk sizes let this method win first instead, 440 samples (~10ms) off. Full derivation:
        // spec/14-roadmap.md's "Band-1 items 2+3" entry.
        //
        // Deliberately a ONE-SHOT gate, not a rolling "stay N samples behind the tail" cap (an
        // earlier draft of this fix used a rolling cap and an auditor plan-review caught two real
        // defects in it before any code was written: a rolling cap permanently drops the tail of a
        // finite/bulk-decoded stream, since the last MaxSearchCeilingMs of a file would never be
        // scanned once the tail stops being "behind" the live edge; and it imposes a needless
        // PERMANENT per-sample latency penalty, when the fixed-window path's own search window is
        // one-shot, not rolling -- once _rawSamples.Count reaches _consumedSamples plus the ceiling
        // with no commit, TryDecodeVisHeader/TryDecodeNarrowModeHeader are provably dead for this
        // epoch (fresh detectors each call, pure function of (headerStart, buffered data)), so there
        // is nothing left to protect against by continuing to hold this method back).
        //
        // Correctness proof this gate relies on: this method's own doc comment above already
        // establishes _syncBypassProcessedUpTo == _visLockProcessedUpTo == _consumedSamples on
        // entry (enforced by the throw just above), so any match this method could find sits at
        // sample index >= _consumedSamples -- meaning it can only ever COMMIT no earlier than
        // _consumedSamples + MaxSearchCeilingMs once gated, which is exactly the point at which the
        // fixed-window paths are already known to have exhausted their own single chance.
        //
        // Code-review finding: this single MsToSamples(MaxSearchCeilingMs) rounding can land 1-3
        // samples earlier than TryDecodeVisDataBits' own ceiling (built from 3 separately-rounded
        // MsToSamples terms, the same "independently-rounded bounds" pattern already documented
        // elsewhere in this file) -- meaning the extended-VIS path could in principle be truncated by
        // that same 1-3 samples once Band-1 S2's exhaustion-skip makes this a one-way gate instead of
        // a per-call retry. Practically unreachable (the last extended-VIS bit resolves ~185ms before
        // this ceiling), not fixed here -- flagged, not silently accepted.
        if (!_fixedWindowExhausted)
        {
            var fixedWindowCeiling = _consumedSamples + MsToSamples(VisHeader.MaxSearchCeilingMs);
            if (TotalSamplesReceived >= fixedWindowCeiling)
            {
                _fixedWindowExhausted = true;
            }
        }

        var scanBound = _fixedWindowExhausted ? TotalSamplesReceived : _consumedSamples;

        // S8 fix: TryNarrowFskScan is checked FIRST, matching legacy's real per-sample order
        // (DecodeFSK, sstv.cpp:1858, unconditional, runs before the `if(!m_Sync||...)` block the
        // sync-bypass/VIS-lock loop below corresponds to, sstv.cpp:1889). Deliberately NOT part of
        // this method's own entry-invariant check or the loop's own shared cursor advancement -- see
        // TryNarrowFskScan's and _narrowFskProcessedUpTo's own doc comments for why this cursor must
        // stay fully independent of _syncBypassProcessedUpTo/_visLockProcessedUpTo.
        //
        // Bounded by scanBound here, NOT TotalSamplesReceived -- round-1 code-level review caught a
        // real regression in an earlier draft that used TotalSamplesReceived directly: on a bulk
        // single-PushSamples call containing TWO back-to-back real transmissions, TotalSamplesReceived
        // already includes the SECOND transmission's own real header content from the very first call,
        // letting this scan discover it (and Commit() a spurious restart) before the FIRST transmission
        // had even been given a chance to lock -- the exact "bulk vs. streaming ordering" bug class
        // Band-1 items 2+3 and TryVisLockStateMachine's own doc comment already warn about elsewhere in
        // this file. scanBound already encodes the correct "how far is it legitimate to look right now"
        // answer (pinned at _consumedSamples until _fixedWindowExhausted, exactly like
        // _syncBypassProcessedUpTo/_visLockProcessedUpTo's own shared bound below), so reusing it here
        // closes the gap without needing a fourth, independently-drifting bound.
        //
        // Milestone-audit MUST fix: this call used to run to completion (match or exhaust scanBound)
        // BEFORE the interleaved loop below ever took a single step -- on a bulk push spanning TWO
        // back-to-back transmissions (e.g. an earlier non-narrow one followed later by a narrow one),
        // this whole-buffer pre-pass could find and Commit() the LATER transmission before the loop
        // below ever got a chance to examine the EARLIER one's own samples, re-introducing (for the
        // narrow-FSK path specifically) the exact bug class the m_sint1 decoder-ordering fix (piece
        // 7d) was written to eliminate. _narrowFskProcessedUpTo CAN legitimately lag behind
        // _syncBypassProcessedUpTo across image boundaries (it's never reset/jumped by EndOfImage,
        // unlike _syncBypassProcessedUpTo/_visLockProcessedUpTo -- see its own field doc comment), so
        // this first call catches it up only to wherever _syncBypassProcessedUpTo ALREADY sits (never
        // further -- Math.Min, not scanBound) in case this call's own loop below has no NEW
        // sync-bypass work to do this time; the loop below then re-checks per iteration (see its own
        // comment) so it never falls behind current again once the loop is running.
        if (TryNarrowFskScan(Math.Min(scanBound, _syncBypassProcessedUpTo)))
        {
            return true;
        }

        for (; _syncBypassProcessedUpTo < scanBound; _syncBypassProcessedUpTo++, _visLockProcessedUpTo++)
        {
            // Milestone-audit MUST fix (continued): catch _narrowFskProcessedUpTo up to THIS
            // iteration's own sample -- in the common case (already caught up from the call above, or
            // the previous iteration) this reduces to exactly one narrow-FSK sample per interleaved-
            // loop sample, reproducing legacy's real per-sample order (DecodeFSK before the m_sint/VIS
            // switch) for every sample actually in scanBound, not just the first one. Reuses
            // TryNarrowFskScan unchanged -- only the bound passed to it is tighter here than the old
            // whole-scanBound call was.
            if (TryNarrowFskScan(_syncBypassProcessedUpTo + 1))
            {
                return true;
            }

            // LOAD-BEARING ORDER, do not swap: TrySyncIntervalDetectionStep must run BEFORE
            // _visLockStateMachine.ProcessSample below for this same sample index. S12's own gating
            // (TrySyncIntervalDetectionStep's m_sint2/m_sint3 comments) reads _visLockStateMachine's
            // state to reproduce legacy's switch(m_SyncMode) using m_SyncMode's value from BEFORE
            // this sample's own transition -- swapping this order would silently invert that gate
            // (m_sint2/m_sint3 would see the state AFTER this sample's own transition instead),
            // and no existing test would catch it (S12's own tests exercise the properties directly,
            // not this ordering). Functional-audit re-confirmation (D5, round 1): still true, still
            // no test catches a swap here -- deliberately not adding one this round (constructing a
            // real signal that provably distinguishes the two orderings' S12 gate outcomes, rather
            // than an artificial/reflection-based check of the ordering itself, needs more care than
            // this pass has budget for); flagged so it stays a known, explicitly open gap rather than
            // being mistaken for closed.
            if (TrySyncIntervalDetectionStep())
            {
                return true;
            }

            // Round-1-review nitpick fix: bound to a shared local, not read from
            // _visLockProcessedUpTo directly -- makes the "same sample index as
            // TrySyncIntervalDetectionStep just processed" coupling visible at the call site,
            // rather than merely guaranteed by the two cursors currently always being equal inside
            // this loop.
            var sampleIndex = _syncBypassProcessedUpTo;
            var result = _visLockStateMachine.ProcessSample(AgcSampleAt(sampleIndex));
            if (result is not null)
            {
                // No manual _visLockProcessedUpTo++ here, matching TryVisLockStateMachine's own
                // identical note: Commit() itself sets _visLockProcessedUpTo (a Math.Max-derived
                // value), so incrementing afterward would desync it by exactly one sample.
                // _syncBypassProcessedUpTo is deliberately left un-advanced past this same sample on
                // this branch too (sync-bypass didn't "run out of turns" here, VisLockStateMachine
                // simply matched first at the same index) -- harmless: this method is never
                // re-entered without an intervening EndOfImage reset overwriting both cursors fresh
                // (see this method's own doc comment above), so a value that's off by at most one
                // sample is never actually read again.
                //
                // S31 fix: AVT (see VisLockStateMachine.ProcessSample's own doc comment) returns the
                // end of its own first VIS block here, not a line-0 anchor -- hand off to the same
                // training-lock entry point the fixed-window path (TryDecodeVisHeader) already uses,
                // instead of Commit()-ing this as a real lock. This call site only ever runs while
                // _mode is null (guaranteed by TryDecodeHeader's own caller, TryProcessBuffer's
                // `if (_mode is null && !TryDecodeHeader())`), the same precondition the fixed-window
                // AVT call already relies on -- no extra bookkeeping needed here.
                if (result.Value.Mode == SstvModeRegistry.Avt)
                {
                    return TryStartAvtTraining(_visLockOriginSample + result.Value.LineStartSample);
                }

                Commit(result.Value.Mode, _visLockOriginSample + result.Value.LineStartSample);
                return true;
            }
        }

        return false;
    }

    private void CommitSyncBypassMatch(SstvModeDefinition matched, double peakPosition)
    {
        var midpointOffsetSamples = SstvModeRegistry.GetSyncSegmentMidpointOffsetMs(matched) / 1000.0 * _sampleRate;
        var lineStart = _syncBypassOriginSample + (int)Math.Round(peakPosition - midpointOffsetSamples);
        Commit(matched, lineStart);
    }

    // Piece 13: replaces a prior proxy (averaged the shared PLL's demodulated-frequency stream over
    // fixed windows against a midpoint threshold) with a literal port of legacy's real per-sample
    // decoder, NarrowFskHeaderDecoder (CSSTVDEM::DecodeFSK, sstv.cpp:2378-2606) -- the same bug shape
    // Piece 9 already fixed for VIS-bit decode. Two rounds of auditor plan-review verified the state
    // machine itself line-by-line against source before this was written; see that class's own doc
    // comment for the full state-transition derivation.
    //
    // Search ceiling mirrors TryDecodeVisDataBits' own search-ceiling logic (round-9: dropped the
    // in-file line citation this sentence used to carry -- this chunk's own comments have grown across
    // 9 audit rounds, and every such self-citation has drifted stale at least once; a method-name
    // pointer can't drift) for the same reason: an unbounded retry-on-reject scanner is by construction
    // what this decoder is (every legacy failure path resumes scanning from mode 0), and an unbounded
    // version was a real, reverted regression there. Narrow modes keep their existing
    // _syncBypassNarrowTracker fallback (m_sint3) if this local, bounded scan misses a rare edge case.
    // Ceiling = VisHeader.NarrowSearchCeilingMs (round-8 correction: this sentence previously restated
    // the formula inline as guard+timeout+start-bit+24 data bits+200ms retry margin = 950ms, omitting
    // the 300ms leader term -- round-9 correction: the code has read the shared constant, 1250ms,
    // directly since round 4, which replaced round 3's own hand-transcribed-but-corrected value with a
    // direct read of the constant -- see VisHeader.NarrowSearchCeilingMs's own doc comment for the real
    // breakdown), matching TryDecodeVisDataBits' own generous-but-local shape.
    //
    // Commit point (functional-audit correction, D6 round 5: this paragraph previously described
    // the FIXED-OFFSET formula round 4 replaced -- "headerStart + the packet's fixed nominal
    // duration, NOT whatever sample NarrowFskHeaderDecoder happens to lock on" -- which argued for
    // the OLD, since-reverted behavior; corrected to describe what the code actually does now): the
    // commit anchor is derived from the ACTUAL sample the lock completed at (delta-robust, see the
    // method body's own comment at the anchor computation), not a fixed offset from headerStart.
    //
    // Round-6 correction: an earlier version of THIS sentence claimed the delta-robust formula
    // reproduces "the exact same value" as the old fixed formula at delta=0, calling it "verified" --
    // that overstated it. `NarrowFskNoiseTolerantDetectionTests.cs`'s own measured acceptance
    // criterion (its own comment there) puts the real-world delta-robust anchor ~104 samples
    // (~9.4ms at 11025Hz) LATE versus the fixed formula's value, consistently -- real
    // envelope-detector settling lag on the mode-2 trigger (the same category of delay already
    // documented for VisLockStateMachine's own ~7.3ms trigger lag and TryDecodeVisDataBits' own
    // ~14.5ms), not a derivation error, and not something the fixed formula ever had to account for
    // (it never measured anything, just assumed the idealized offset). So round 4's fix traded a
    // real delta bug for a small, measured, already-tolerated late bias -- a strict improvement, but
    // not the no-op the word "verified" implied. The TX-placement fact this method relies on
    // (confirmed against Main.cpp:7423-7424, image data at a fixed offset after the checksum bits --
    // see VisHeader.cs:340 for the same citation) and
    // SstvRoundTripTests.NarrowModeHeader_IsDetected_ForMnFamily's own passing
    // status are both still accurate regardless (that test asserts mode identity only, resolves via
    // TryNarrowFskScan since this path is dead for encoder output either way, and was never
    // sensitive to this anchor's exact value) -- only the "exact same value, verified" framing was
    // wrong.
    //
    // Functional-audit finding, DECIDED (D6, rounds 4-6 -- user explicitly chose to leave THIS
    // method's fully-dead-code path as documented rather than fix or retire it; see
    // TryDecodeVisHeader's own note for the shared derivation -- no longer identical to this one,
    // since round 5 found TryDecodeVisHeader is only PARTIALLY dead, with a separate, still-open
    // 0-185ms live-band finding this method does not share; see project memory
    // "narrow-vis-header-paths-left-dead" for the recorded decision): AnalogFmSstvEncoder.cs's
    // GenerateOutHeadSegments emits a 400ms burst BEFORE every narrow packet, unconditionally, for
    // every encode this port produces. `headerStart` below (== `_consumedSamples`) is assumed to be
    // where the real narrow packet itself begins (delta = 0) -- but `_consumedSamples` never
    // advances pre-lock, so delta == 400ms for this port's own encoder output, and for any real
    // MMSSTV transmission carrying the same burst. Even after round 3's own ceiling widening (below,
    // ~300ms of real tolerance past the packet's own minimum decode point, NarrowHeaderTotalDurationMs
    // = 950ms -- round-9 correction: an earlier version of this sentence said "~306ms", which matched
    // neither the plain 1250-950 arithmetic nor any other derivation checked against it) and round 4's
    // delta-robust anchor fix (also below), the search window still can't reach a 400ms delta --
    // meaning this fixed-window narrow path remains fully dead code for realistic audio today
    // (unlike its VIS sibling, which round 5 found is only PARTIALLY dead -- live and correctness-
    // risky for a narrower 0-185ms delta band; see TryDecodeVisHeader's own note). The round-3/
    // round-4 fixes in this method were still worth landing regardless (they widen the window
    // without weakening it, and the delta-robust anchor removes a real correctness trap for whenever
    // this gets revisited) -- but they do not, by themselves, revive this path. That's fine: the
    // user's own decision above already accepts this path staying fully dead.
    private bool TryDecodeNarrowModeHeader()
    {
        var totalHeaderSampleCount = (int)Math.Round(VisHeader.NarrowHeaderTotalDurationMs / 1000.0 * _sampleRate);
        if (TotalSamplesReceived - _consumedSamples < totalHeaderSampleCount)
        {
            return false;
        }

        var headerStart = _consumedSamples;
        // Functional-audit fix (D6, round 3): was missing VisHeader.NarrowLeaderDurationMs (300ms)
        // from this sum entirely. headerStart is where the 300ms leader itself begins (matching
        // totalHeaderSampleCount's own gate above, which DOES include the leader), so the fskDecoder's
        // own internal trigger search doesn't even reach the guard tone until ~300ms into a real
        // packet -- omitting that term made this ceiling numerically coincide with
        // NarrowHeaderTotalDurationMs (leader+guard+startbit+528 = 950ms) by coincidence, since
        // guard*2+bit*25+200 also sums to 950ms, leaving the "+200 retry margin" the old comment
        // claimed actually providing ZERO real margin beyond the packet's own bare minimum duration --
        // any real-world jitter (clock drift, a guard trigger landing a few samples later than the
        // idealized 300ms) would fail this attempt and fall back to the persistent scanners instead of
        // succeeding via this fixed-window path as designed. Functional-audit fix (D6, round 4): now
        // reads VisHeader.NarrowSearchCeilingMs directly (its own definition updated with the same
        // leader-term fix) instead of hand-transcribing the arithmetic a second time, matching
        // NormalSearchCeilingMs/ExtendedSearchCeilingMs's own established "one source of truth, so it
        // and MaxSearchCeilingMs can never silently desync" convention -- the desync this exact
        // hand-transcription caused (VisHeader.cs's own NarrowSearchCeilingMs staying at the stale
        // pre-fix 950ms while this method's own local formula moved to 1250ms) is what round-3's
        // finding 3 flagged.
        var searchCeiling = headerStart + MsToSamples(VisHeader.NarrowSearchCeilingMs);
        var availableUpTo = Math.Min(TotalSamplesReceived, searchCeiling);

        // Band-2 item S14: mark/space no longer cold-started fresh per call (see D19At/FskSpaceAt's
        // own doc comments) -- confirmed directly against legacy (sstv.cpp:1851/1855/1858): d19 and
        // dsp (m_iirfsk) are both continuously-running, unconditionally-fed-every-sample resonators,
        // and DecodeFSK(int(d19), int(dsp)) is likewise called every sample, not just while searching
        // for a narrow-mode packet -- exactly the shape these two caches now reproduce. fskDecoder
        // itself (the case-based bit/byte state machine) stays fresh per call, matching legacy's own
        // per-attempt CSSTVDEM member state for that piece; only the two resonators feeding it are
        // shared/persistent.
        var fskDecoder = new NarrowFskHeaderDecoder(_sampleRate) { StationIdDecodeEnabled = StationIdDecodeEnabled };

        for (var sample = headerStart; sample < availableUpTo; sample++)
        {
            var m = (int)D19At(sample);
            var s = (int)FskSpaceAt(sample);

            var result = fskDecoder.ProcessSample(m, s);
            if (result is null)
            {
                continue;
            }

            // A station-ID (STX 0x2a) result reaching this specific single-shot, fixed-headerStart
            // verification is unlikely but not vanishingly so now that StationIdDecodeEnabled can be
            // live-set true (Application-layer settings wiring, see that property's own doc comment) --
            // a minimal callsign packet, ~600ms, fits inside this method's own ~1250ms search ceiling
            // (round-9 correction: an earlier version of this sentence said "~950ms", stale since round
            // 3/4 raised the real ceiling to 1250ms -- the conclusion, that it fits, holds either way).
            // Unlike the "unregistered mode code" case
            // below (a genuinely FAILED decode this method's own design deliberately doesn't retry),
            // a station-ID result isn't a failure -- it's simply not what this method is looking for.
            // `continue` rather than `return false` (code-review finding): a real mode-announce
            // header could still legitimately follow later within this method's own remaining bound,
            // and aborting early would silently lose that chance. This method still does NOT deliver
            // station-ID events -- that's TryNarrowFskScan's persistent-scan job, not this one's.
            if (result.Value.ModeCode is null)
            {
                continue;
            }

            var mode = SstvModeRegistry.FindByNarrowCode(result.Value.ModeCode.Value);
            if (mode is null)
            {
                // Unregistered mode code -- legacy resumes scanning rather than giving up
                // (sstv.cpp:2588-2597), and so does NarrowFskHeaderDecoder internally; this port's
                // caller has no further chances at this headerStart, matching TryDecodeVisDataBits'
                // own "not part of this bug" out-of-scope note for the equivalent VIS case.
                return false;
            }

            // Functional-audit fix (D6, round 4): was `headerStart + totalHeaderSampleCount` -- a
            // FIXED offset from headerStart regardless of where in the search window `sample` (the
            // index the lock actually completed at) landed. That's only correct if the packet starts
            // exactly at headerStart (delta = 0); TryNarrowFskScan's own sibling anchor computation
            // (this method's persistent-scan counterpart, see its own doc comment) is delta-ROBUST --
            // it derives the true origin from the ACTUAL completion index minus
            // SamplesSinceBitClockOrigin, working correctly regardless of delta. Using the same
            // technique here removes a latent trap: round 3's own ceiling widening moved this
            // method's search window closer to (without reaching) the ~400ms delta this port's own
            // encoder's OutHEAD burst actually introduces (AnalogFmSstvEncoder.cs's
            // GenerateOutHeadSegments, emitted before every narrow packet) -- had the window ever
            // widened further (or OutHEAD's own duration changed), the old fixed-offset formula would
            // have silently committed an anchor up to ~400ms wrong. This fix makes a wrong anchor
            // structurally impossible here regardless of window width or delta.
            var originSample = sample - result.Value.SamplesSinceBitClockOrigin;
            var anchor = originSample + MsToSamples(VisHeader.NarrowPostBitClockOriginDurationMs);
            _consumedSamples = anchor;

            // Direct port of a real regression caught by independent review: this used to duplicate
            // Commit()'s body inline instead of calling it, which meant _afcBoundSample (added when
            // Commit() gained it, see ApplyAfcCorrections' doc comment) was never assigned for a
            // narrow transmission -- staying at its default 0, silently disabling AFC entirely for
            // every MN/MC mode. Routing through the same Commit() every other detection path already
            // uses closes this and keeps future Commit()-side fixes from needing to be duplicated a
            // second time here.
            Commit(mode, _consumedSamples);
            return true;
        }

        return false; // not enough data yet, or the bounded scan found nothing
    }

    // Ported mechanism: sstv.cpp's case 2/9 (1974-2126) -- the tone race between m_iir11/m_iir13
    // (1080Hz/1320Hz, 80Hz bandwidth, sstv.cpp:1446/1448) that decides each VIS data bit, including
    // the (d11<d19&&d13<d19)||fabs(d11-d13)<SLvl2 weak/ambiguous reject gate (sstv.cpp:1981-1984)
    // this port previously had no equivalent of at all -- it decided bits from the shared PLL's
    // demodulated-frequency stream instead, a proxy that only worked because the PLL happened to
    // also cover 1100-1300Hz (see spec/14-roadmap.md's "Piece 9" entry). VisBitDecision.TryDecide
    // is the actual decision predicate, shared with VisLockStateMachine rather than duplicated here.
    //
    // Timing, derived directly from legacy's own real-time arithmetic, not assumed: case-2 entry
    // (where d13 starts advancing, sstv.cpp:1976-1978, "frozen between attempts" otherwise) is 15ms
    // into the 30ms start-bit tone -- i.e. 15ms BEFORE DataBit0's own tone begins (VisHeader's
    // Leader+Break+Leader+StartBit = 640ms from headerStart; case-2 entry = 610ms trigger + 15ms
    // ConfirmLock hold = 625ms, matching VisLockStateMachine's own already-tested arithmetic for
    // the same trigger). Each bit's decision fires exactly 30ms after case-2 entry (or after the
    // previous decision), landing at (bit-window-start + 15ms) -- the window's own MIDPOINT, not
    // its end. This falls out of legacy's fixed trigger-to-decode arithmetic itself, not a separate
    // compensation for filter settling: legacy doesn't wait for its filters to settle before
    // reading d11/d13/d19, it reads whatever they contain at this fixed countdown-zero instant, and
    // this port's SyncEnvelopeDetector (an exact TankFilter/IirFilter port of the same
    // CIIRTANK/CIIR legacy uses) reproduces the same filter response given the same relative
    // timing -- so no additional group-delay fudge is layered on top of this analytically-derived
    // midpoint offset. Confirmed empirically by this port's own existing extended/normal-VIS
    // round-trip tests (SstvRoundTripTests) continuing to pass with this timing, not just asserted.
    //
    // d11/d19 run continuously from headerStart (well before case-2 entry, matching legacy's own
    // always-running m_iir11/m_iir19, sstv.cpp:1893-1895/1847-1853) rather than being restarted per
    // bit window. Fresh detector instances are constructed on every call -- this method is a pure
    // function of (headerStart, bitCount), safe to call again if TryDecodeVisHeader's caller
    // re-invokes it on the same not-yet-consumed samples (the method returns false/doesn't consume
    // in several places, and a streaming PushSamples caller can do exactly that).
    //
    // Returns null if the trigger precondition or the tone race rejects (weak/ambiguous, matching
    // legacy's abort-to-search) -- never for "not enough samples yet", since the caller already
    // gates on that before calling in.
    //
    // Precondition, added after this method's first version produced a real false-positive lock on
    // real captured audio (spec/14-roadmap.md's Piece 9 entry, caught by GoldenVectorTests --
    // martin-m1's real mic-noise lead-in raced to a byte that happened to match a registered mode,
    // sc2-120): legacy NEVER runs the d11/d13 tone race at all unless a genuine, sustained 1200Hz
    // dominant tone was already found first -- case 0's trigger, held for case 1's full 15ms
    // (sstv.cpp:1946-1973, "ANY single failing sample resets to Search immediately"), exactly what
    // VisLockStateMachine's own Search/ConfirmLock states check. This method previously had no
    // equivalent gate at all, so it would blindly race two envelope detectors against whatever
    // content happened to sit at headerStart, real header or not.
    //
    // The trigger point is found DYNAMICALLY (a small, locally-scoped search + 15ms hold, mirroring
    // VisLockStateMachine's Search/ConfirmLock -- NOT a full second copy of that state machine, which
    // the round-2 plan review already ruled out reusing wholesale for this call site), not assumed to
    // fall at the analytically-idealized 610ms mark. A second real bug this caught, empirically (not
    // theorized): a fixed 610ms assumption fails even on a clean, full-amplitude, real filtered
    // signal, because the leader-to-startbit tone transition isn't instantaneous through a resonator+
    // lowpass chain -- measured ~14.5ms of real settling lag before d12 actually overtakes d19 on a
    // synthetic AVT fixture with no noise at all. Legacy's own real trigger has the exact same
    // property (it fires whenever d12 actually crosses d19, not at a hardcoded offset), so searching
    // for it dynamically is the more legacy-faithful choice, not just a workaround.
    //
    // Both the trigger search AND the per-bit reject gate resume searching on failure rather than
    // aborting the whole attempt -- code-review finding, not anticipated by either plan-review round:
    // legacy's real receiver never permanently gives up either (case 1's failing condition and case
    // 2/9's own reject gate both just set `m_SyncMode = 0`, sstv.cpp:1957/1972/1983, returning to
    // case 0 which re-triggers on the very next qualifying sample). A first version of this method
    // aborted outright on any reject, which -- unlike legacy -- could permanently kill detection at
    // this headerStart if e.g. the 10ms/1200Hz break tone (VisHeader.BreakFrequencyHz, 300-310ms)
    // spuriously satisfied the trigger+hold before the real 30ms start-bit tone was ever reached.
    // VisLockStateMachine's own independent scan is a fallback that recovers from this anyway for
    // every mode, AVT included as of the S31 fix (see VisLockStateMachine's own class doc comment) --
    // but that fallback only gets a real chance against a genuinely real (noisy, non-zero-offset)
    // capture; a synthetic self-round-trip test starting at sample 0 still depends on THIS method's
    // own resume-on-failure behavior, since the fixed-window path wins that race every time (see
    // TryDecodeHeader's own doc comment).
    //
    // Bounded to a generous but LOCAL ceiling relative to headerStart, not the whole buffered
    // stream -- an unbounded version (tried first, reverted) is a real regression, not a hypothetical
    // one: with no ceiling, this fixed-window method effectively becomes a second, untested full-buffer
    // scanner whenever its first attempt fails, and on a multi-transmission stream it can walk straight
    // through unrelated image content and spuriously match some OTHER registered VIS byte deep inside
    // it -- caught by the existing multi-transmission ordering suite (PiecesSixCReachabilityTests,
    // SyncScanInterleaveTests), which expect the fixed-window path to fail LOCALLY and yield to
    // TryInterleavedHeaderScan, not to keep searching indefinitely on its own. The ceiling covers the
    // idealized trigger (610ms) plus a 200ms retry margin (generous room for one or two short spurious
    // candidates, e.g. the 10ms/1200Hz break tone, before the real start bit) plus this bitCount's own
    // full decode duration -- enough for legacy-faithful local recovery, not a general-purpose search.
    private int[]? TryDecodeVisDataBits(int headerStart, int bitCount)
    {
        var confirmHoldSamples = MsToSamples(VisHeader.BitDurationMs / 2); // 15ms, sstv.cpp:1948
        var searchCeiling = headerStart + MsToSamples(VisHeader.LeaderDurationMs * 2 + VisHeader.BreakDurationMs + 200)
            + confirmHoldSamples + bitCount * MsToSamples(VisHeader.BitDurationMs);
        var availableUpTo = Math.Min(TotalSamplesReceived, searchCeiling);

        // Band-2 item S5: d13Detector stays local/fresh per call (see D11At/D12At/D19At's own doc
        // comment for why -- legacy only feeds m_iir13 during case 2/9, so it's not a pure function of
        // absolute sample index). d11/d12/d19 are read via the persistent D11At/D12At/D19At caches
        // instead of local detector objects.
        var d13Detector = new SyncEnvelopeDetector(_sampleRate, 1320.0, bandwidthHz: 80.0);

        var sample = headerStart;
        var holdCount = 0;
        var d11 = 0.0;
        var d19 = 0.0;

        while (sample < availableUpTo)
        {
            var triggerFound = false;
            for (; sample < availableUpTo; sample++)
            {
                d11 = D11At(sample);
                var d12 = D12At(sample);
                d19 = D19At(sample);

                if (d12 > d19 && d12 > _slvl && d12 - d19 >= _slvl)
                {
                    if (++holdCount >= confirmHoldSamples)
                    {
                        triggerFound = true;
                        sample++; // case-2 entry -- d13 starts advancing from here
                        break;
                    }
                }
                else
                {
                    holdCount = 0;
                }
            }

            if (!triggerFound)
            {
                return null; // not enough data buffered yet to complete a hold anywhere available
            }

            holdCount = 0; // reset for the next trigger search, if this attempt's bits get rejected

            var bits = new int[bitCount];
            var bitIndex = 0;
            var nextDecisionSample = sample + MsToSamples(VisHeader.BitDurationMs);
            var rejected = false;

            // Loop on bitIndex, not a precomputed sample bound -- a bound computed as a single
            // MsToSamples(bitCount * BitDurationMs) rounds differently than nextDecisionSample's own
            // bitCount separate MsToSamples(BitDurationMs) increments (they can differ by a sample or
            // two after several steps), which silently left the LAST bit's decision point past the
            // loop's bound and its array slot at its default 0 -- a real bug this caught (AVT's own
            // round-trip test at 11025Hz: byte 0x44 decoded as 0x04, R24's code, with bit 6 never
            // decided). Looping until every bit has been decided (or data runs out) sidesteps the
            // mismatch entirely instead of trying to keep two independently-rounded bounds in sync.
            for (; bitIndex < bitCount; sample++)
            {
                if (sample >= availableUpTo)
                {
                    return null;
                }

                d11 = D11At(sample);
                d19 = D19At(sample);
                var d13 = d13Detector.ProcessSample(AgcSampleAt(sample));

                if (sample == nextDecisionSample)
                {
                    if (!VisBitDecision.TryDecide(d11, d13, d19, _slvl2, out var bit))
                    {
                        rejected = true;
                        sample++; // advance past this already-processed sample before the outer loop
                                  // resumes searching -- matches legacy exactly: a failed bit decision
                                  // sets m_SyncMode=0 (sstv.cpp:1983), and case 0 resumes on the NEXT
                                  // sample, not this one. (Band-2 item S5, auditor code-level review:
                                  // the original rationale here -- "these are stateful streaming
                                  // filters, not a cache, re-processing would double-apply it" -- no
                                  // longer applies to d11/d12/d19, now caches where re-reads are
                                  // idempotent; kept for d13, which the trigger-search loop never reads
                                  // anyway, and restated against the real legacy citation instead.)
                        break;
                    }

                    bits[bitIndex++] = bit;
                    nextDecisionSample += MsToSamples(VisHeader.BitDurationMs);
                }
            }

            if (!rejected)
            {
                return bits;
            }
        }

        return null;
    }

    // Functional-audit note (D6, round 1): rounds, where legacy's own `int m_SyncTime = 30 *
    // sys.m_SampFreq/1000` (sstv.cpp:1965/:1986, assigning a double into an int field, sstv.h:631)
    // TRUNCATES -- 331 samples vs legacy's 330 for the VIS bit period at this project's default
    // 11025Hz rate. Deliberately NOT changed to match: legacy's own C narrowing is itself an
    // imprecise approximation of the true 330.75-sample period (11025*0.03), and rounding is
    // actually the closer approximation (+0.25 samples/bit vs legacy's -0.75) -- matching legacy
    // exactly here would trade a smaller numeric error for byte-for-byte parity with a truncation
    // quirk, for no decode-correctness benefit (verified: bit-decision points sit ~±15ms from either
    // boundary, so a 1-sample/bit drift never flips a bit on a clean signal, even accumulated across
    // a full 30-bit extended header). AvtTrainingLockStateMachine.cs's own identically-named helper
    // makes the OPPOSITE choice (truncates) specifically for golden-vector parity with a different
    // legacy call site -- see that class's own doc comment; the two are intentionally inconsistent
    // with each other, not a bug to unify.
    private int MsToSamples(double ms) => (int)Math.Round(ms / 1000.0 * _sampleRate);

    // Functional-audit finding, DECIDED for the >~185ms band / OPEN for the 0-185ms band (D6,
    // rounds 4-6 -- see project memory "narrow-vis-header-paths-left-dead" for the recorded
    // decision, and TryDecodeNarrowModeHeader's own note for the shared derivation, though the two
    // are no longer identical -- see below for the difference). This fixed-window path assumes
    // `headerStart` (== `_consumedSamples`) is where the real VIS/narrow packet itself begins
    // (delta = 0). AnalogFmSstvEncoder.cs's GenerateOutHeadSegments emits an 800ms burst BEFORE
    // every normal/extended VIS packet (400ms before every narrow one), unconditionally, for every
    // encode this port produces -- and `_consumedSamples` never advances pre-lock, so delta == that
    // OutHEAD duration for this port's own encoder output, and for any real MMSSTV transmission
    // carrying the same burst. This method's own search ceiling (`VisHeader.NormalSearchCeilingMs`/
    // `ExtendedSearchCeilingMs`, ~1065/1305ms) tolerates delta up to only ~185ms.
    //
    // Round-5 correction: NOT fully dead code, and the prior "no wrong output results" claim here
    // was false for the live band. For 0 < delta <= ~185ms (e.g. a short lead-in capture, or a real
    // transmission whose OutHEAD is shorter/absent -- legacy's own OutHEAD is VOX-mode-dependent,
    // Main.cpp:7274-7292, unlike this port's own encoder which emits it unconditionally), this path
    // DOES fire (bit decoding is trigger-search-based, so it tolerates delta fine) but commits at
    // the fixed `headerStart + totalHeaderSampleCount + extraSampleCount` (the exact call is this
    // method's own `Commit(mode, ...)` statement below, round-9: dropped the in-file line citation --
    // `extraSampleCount` is nonzero for Scottie's own extra post-header pulse) -- an
    // anchor `delta` samples too early, unlike TryDecodeNarrowModeHeader's own sibling, which round
    // 4 already made delta-robust.
    //
    // Impact bound, corrected (round 6, citations corrected round 7, citations dropped round 9):
    // "TryResolveSyncAnchorCorrection's own fold re-phases the anchor modulo the line width" is true
    // ONLY for the non-AVT commit above -- AVT never reaches that method (`Commit`'s own AVT branch finalizes inline, matching
    // legacy's `Main.cpp:3754-3758` early-out for `smAVT`), so AVT's OWN fallback commit
    // (`Commit(SstvModeRegistry.Avt, _avtTrainingFallbackDeadlineSample)` in `TryResolveAvtTraining`,
    // reached when the AVT training lock doesn't confirm before `_avtTrainingFallbackDeadlineSample` --
    // AVT is a normal VIS byte, 68, not the 0x23 extended-VIS escape code, so it never goes through the
    // extended path above) has NO fold applied -- a raw, unfolded sub-line shift up to ~185ms (roughly
    // half an AVT image line, 3x125ms), not the "integer transmission lines" shape the folded case
    // gets. For the folded (non-AVT)
    // case, "1-2 transmission lines" is also mode-dependent, not a universal bound: RM8's own line
    // is ~66.9ms (SstvModeRegistry.cs), so a 185ms delta folds to ~3 lines there, not 1-2 -- the
    // real bound is `ceil(185ms / thatMode'sOwnLineDurationMs)` transmission lines, smaller for
    // slower modes, larger for faster ones.
    //
    // A delta-robust anchor fix for THIS method (porting the same technique already proven in
    // TryNarrowFskScan and TryDecodeNarrowModeHeader) is queued, not done here -- unlike the narrow
    // case, where the fix reused an already-computed value
    // (`FskDecodeResult.SamplesSinceBitClockOrigin`), TryDecodeVisDataBits has no equivalent
    // trigger-position value exposed to its caller yet; adding one is new, real derivation work (not
    // a mechanical port of an already-proven pattern) and deserves its own focused pass rather than
    // being rushed alongside an unrelated finding. Fully separate from, and does NOT reopen, the
    // user's own decision to leave the >~185ms fully-dead band exactly as documented.
    private bool TryDecodeVisHeader()
    {
        // S10 fix: prefix now covers the first FULL byte (leader/break/leader/start-bit + 7 data bits
        // + parity bit, VisHeader.FirstByteBitCount = 8) -- legacy's real m_VisData accumulates all 8
        // bits (m_VisCnt starts at 8, sstv.cpp:1966-1967) before ITS OWN mode-lookup switch
        // (sstv.cpp:1993-2074) ever runs, for BOTH the escape check (case 0x23) and every normal-mode
        // case -- they're arms of the SAME switch(m_VisData), not two separately-timed decisions. We
        // don't know normal-vs-extended until those 8 bits are decoded, so wait for the full first
        // byte (VisHeader.PrefixDurationMs's own 7-data-bit span PLUS one more bit-slot for parity,
        // not PrefixDurationMs alone -- code-level auditor review finding: without the extra
        // BitDurationMs here this gate is one bit-slot short of what TryDecodeVisDataBits below
        // actually needs, which is harmless in practice (the bit loop just returns null and this
        // method retries on the next call) but wastes a redundant attempt) before deciding.
        var prefixSampleCount = (int)Math.Round((VisHeader.PrefixDurationMs + VisHeader.BitDurationMs) / 1000.0 * _sampleRate);
        if (TotalSamplesReceived - _consumedSamples < prefixSampleCount)
        {
            return false;
        }

        var headerStart = _consumedSamples;

        var firstByteBits = TryDecodeVisDataBits(headerStart, VisHeader.FirstByteBitCount);
        if (firstByteBits is null)
        {
            return false; // weak/ambiguous tone race -- legacy aborts the whole attempt (sstv.cpp:1983)
        }

        var firstFullByte = VisHeader.DecodeRawByte(firstByteBits);
        var isExtended = firstFullByte == VisHeader.ExtendedVisEscapeCode;
        var tailDurationMs = isExtended ? VisHeader.ExtendedTailDurationMs : VisHeader.NormalTailDurationMs;
        var totalHeaderSampleCount = (int)Math.Round((VisHeader.PrefixDurationMs + tailDurationMs) / 1000.0 * _sampleRate);

        if (TotalSamplesReceived - headerStart < totalHeaderSampleCount)
        {
            return false; // wait for the rest of the header before consuming/deciding
        }

        SstvModeDefinition? mode;
        if (isExtended)
        {
            // 8 more bits (the real extended-mode byte) before the stop bit -- windows 8-15 of the
            // same continuous tone race, NOT guaranteed to be a fresh decode of the SAME occurrence
            // as firstByteBits above (functional-audit correction, D6 round 2): the earlier comment
            // here claimed this call is "deterministically identical to firstByteBits above,
            // harmless redundancy" -- that's only true if bits 0-15 all decode cleanly on the first
            // trigger. TryDecodeVisDataBits' own outer loop (see its doc comment) retries from a NEW
            // trigger point whenever any bit in this call's own 0-15 range gets rejected -- including
            // bits 8-15, which the FIRST call above never even attempted. A reject there resumes
            // scanning and can return a completely different header occurrence's bits 0-7 in
            // `allBits[0..8]`, not necessarily still 0x23 (legacy's real behavior for the same case,
            // sstv.cpp:1981-1983/:2066-2069: a reject anywhere in case 9 drops to case 0, forcing the
            // escape byte to be re-matched before the second byte counts -- this port previously
            // trusted `allBits[8..16]` as the extended code without that re-check).
            var allBits = TryDecodeVisDataBits(headerStart, VisHeader.ExtendedDataBitCount);
            if (allBits is null)
            {
                return false;
            }

            if (VisHeader.DecodeRawByte(allBits.AsSpan(0, VisHeader.FirstByteBitCount)) != VisHeader.ExtendedVisEscapeCode)
            {
                return false; // matches legacy: a reject anywhere in case 9 forces re-matching the escape byte, sstv.cpp:1981-1983
            }

            var extendedCode = VisHeader.DecodeRawByte(allBits.AsSpan(VisHeader.FirstByteBitCount, 8));
            mode = SstvModeRegistry.FindByExtendedCode(extendedCode);
        }
        else
        {
            mode = SstvModeRegistry.FindByFullVisByte(firstFullByte);
        }

        if (mode is null)
        {
            // Unknown VIS code. A fuller implementation would keep scanning for a valid header
            // instead of giving up — out of scope for this Phase 1 proof.
            return false;
        }

        // Scottie carries a small extra fixed pulse beyond one normal VIS transmission (see
        // VisHeader.ScottiePostVisPulseFrequencyHz) that this decoder never needed to read, only
        // skip past, since the mode is already known from the VIS bits above. AVT is handled
        // separately below: its extra header material (2 more VIS repeats + a data-dependent
        // training sequence, see AvtTrainingLockStateMachine) can't be skipped with a fixed count
        // the same way.
        if (mode == SstvModeRegistry.Avt)
        {
            return TryStartAvtTraining(headerStart + totalHeaderSampleCount);
        }

        var extraHeaderDurationMs = SstvModeRegistry.IsScottieFamily(mode) ? VisHeader.ScottiePostVisPulseDurationMs : 0.0;
        var extraSampleCount = extraHeaderDurationMs > 0
            ? (int)Math.Round(extraHeaderDurationMs / 1000.0 * _sampleRate)
            : 0;

        // Single atomic commit point: _consumedSamples (and _mode) must not change unless the FULL
        // header -- base VIS plus any Scottie extra -- is already available. Splitting this into two
        // separate advances (base header now, extra later) was a real bug caught by independent
        // review: a chunked/streaming PushSamples caller could see the base-header advance
        // committed, then hit "not enough samples yet" for the extra part and return false with
        // _mode still null -- so the next call would restart header detection from the middle of
        // Scottie's post-VIS pulse instead of skipping past it.
        if (TotalSamplesReceived - headerStart < totalHeaderSampleCount + extraSampleCount)
        {
            return false; // wait for the rest of the header (including any Scottie extra) before committing
        }

        Commit(mode, headerStart + totalHeaderSampleCount + extraSampleCount);
        return true;
    }

    // AVT and Scottie both carry extra header material beyond one normal VIS transmission (see
    // VisHeader.GenerateAvtSegments/ScottiePostVisPulseFrequencyHz), but AVT's is data-dependent
    // (see AvtTrainingLockStateMachine's doc comment for why a fixed skip alone leaves accuracy on
    // the table for real captured audio with clock drift): once visHeaderEndSample + 2 more VIS
    // repeats' worth of samples are available, start feeding already-demodulated frequencies into a
    // training-lock instance -- NOT the same point legacy's own case 3 hands off to case 4 (that's
    // right after the *first* VIS repeat, ~1835ms earlier; legacy's cases 4-8 then spend the 2nd/3rd
    // repeats' own audio as failed marker-search noise before reaching real training content). This
    // port instead skips straight past all 3 repeats before constructing AvtTrainingLockStateMachine
    // at all, which is why that class's own internal timeout budget is scoped to just the training
    // sequence's own duration, not legacy's full case-3 figure -- see that class's doc comment.
    // VisHeader.AvtExtraHeaderDurationMs's already-tested fixed duration is kept as a hard ceiling
    // here (matching legacy's own real fallback shape: if the training lock never confirms a lock,
    // completion converges on very close to this same fixed duration).
    //
    // S31 fix: takes a single already-summed sample index -- the end of AVT's own first VIS block
    // ("headerStart + totalHeaderSampleCount" in the fixed-window path's own terms) -- rather than
    // the two components separately, since every use below is already purely a function of their
    // sum (confirmed by reading every line in this method plus _avtPllWarmupStartSample below: none
    // reads headerStart or totalHeaderSampleCount on their own). This lets a second caller
    // (VisLockStateMachine's own noise-tolerant match, via AnalogFmSstvDecoder.TryInterleavedHeaderScan)
    // supply the same boundary without needing to reconstruct headerStart artificially.
    private bool TryStartAvtTraining(int visHeaderEndSample)
    {
        _avtTrainingOriginSample = visHeaderEndSample + (int)Math.Round(2 * VisHeader.AvtVisBlockDurationMs / 1000.0 * _sampleRate);
        _avtTrainingFallbackDeadlineSample = visHeaderEndSample
            + (int)Math.Round(VisHeader.AvtExtraHeaderDurationMs / 1000.0 * _sampleRate);
        _avtTrainingLock = new AvtTrainingLockStateMachine(_sampleRate);
        _avtTrainingProcessedUpTo = _avtTrainingOriginSample;
        _avtTrainingPending = true;
        // Options stub backlog item 1: constructed with the CURRENTLY APPLIED tuning fields (updated
        // by ApplyPendingPllTuningRequest's drain), not the legacy-default ctor overloads -- this
        // instance is fresh-per-attempt, so it would otherwise silently start at defaults regardless
        // of whatever tuning the picture-path _pllDemodulator is actually running with.
        _avtPllDemodulator = new PllFmDemodulator(_sampleRate, DemodulatorLowHz, DemodulatorHighHz, _pllVcoGain, _pllLoopOrder, _pllLoopCutoffHz, _pllOutputOrder, _pllOutputCutoffHz);
        _avtPllWarmedUp = false;

        // Band-2 item S16 (pre-Phase-2 audit): auditor plan-review, round 1 -- the FIRST proposed fix
        // for this item (make _avtPllDemodulator fully persistent/decoder-lifetime, like S5's tone
        // detectors) was wrong and would have been a real regression, caught before any code was
        // written. Unlike S5's d11/d12/d19 (fed unconditionally every sample in legacy), legacy's real
        // m_pll for AVT is fed ONLY during SyncMode cases 3-7 (sstv.cpp:2129/2159/2169/2187/2222) --
        // intermittent, not a pure function of absolute sample index, the exact same shape as d13's own
        // case-2/9-only feed that blocked converting IT to an index-keyed cache. A PLL's phase state
        // also has no equivalent of a resonator's fast, data-independent re-settling -- "just leave it
        // running forever" would make AVT training entry a function of the ENTIRE preceding stream,
        // which legacy's real per-attempt m_pll usage never is (case 3 gates its own feed on !m_Sync,
        // i.e. this exact attempt's own unlocked window, not session history).
        //
        // The REAL defect (confirmed correct by that same plan-review round): _avtPllDemodulator stays
        // fresh-per-training-attempt as before, but its old clamped 2000-sample warm-up was far too
        // short relative to legacy's real contiguous feed window. This port's own _avtTrainingOriginSample
        // deliberately skips past all 3 VIS repeats before ever constructing a training-lock instance at
        // all (see the class-level comment above TryResolveAvtTraining's own call site for why) -- but
        // legacy spends that entire skipped span, cases 4-7, continuously feeding m_pll real failed-
        // marker-search audio. Widened to legacy's own real contiguous span instead of the arbitrary
        // AnchorWarmupSamples constant: case 3's own 30ms VIS-stop-bit window (sstv.cpp:2127-2129,
        // `if(!m_Sync) m_pll.Do(ad);`, unconditional during that case) through _avtTrainingOriginSample
        // itself -- verified directly against source, not assumed, per the plan-review's own explicit
        // flag that this needed checking rather than guessing.
        //
        // S7 code-level review note: legacy's case-3 feed is actually `if(!m_Sync) m_pll.Do(ad)`
        // (sstv.cpp:2128) -- gated on NOT already being locked. S7 made a new call path reachable where
        // that's false (a mid-reception AVT match arriving while a DIFFERENT mode is already locked and
        // being abandoned): legacy would skip this 30ms warm-up span entirely in that case, but this
        // port always includes it, since visHeaderEndSample alone doesn't carry "was something already
        // locked" and threading that through wasn't judged worth the complexity -- 30ms at the head of
        // an ~1850ms warm-up window is immaterial to whether the PLL settles before real training
        // content starts (confirmed: S7's own new mid-reception AVT test decodes correctly).
        _avtPllWarmupStartSample = visHeaderEndSample - MsToSamples(VisHeader.BitDurationMs);

        return TryResolveAvtTraining();
    }

    private bool TryResolveAvtTraining()
    {
        // Fresh PllFmDemodulator instance -- unlike legacy's continuously-running m_pll, this starts
        // cold with no filter history. Warm it up on real, already-buffered raw samples before origin,
        // over legacy's own real contiguous pre-origin feed span (_avtPllWarmupStartSample, see
        // TryStartAvtTraining's own doc comment for the full derivation -- NOT a clamped constant
        // window; this is derived from where legacy's case 3/4 handoff actually sits, not guessed).
        // Deferred here (not done eagerly in TryStartAvtTraining) and gated on _avtTrainingOriginSample
        // itself being fully buffered -- a real bug caught by the full test suite immediately after
        // wiring this in ("test early, test often"): a streaming/chunked PushSamples caller can invoke
        // TryStartAvtTraining before _rawSamples has grown as far as _avtTrainingOriginSample yet, so
        // warming up eagerly there indexed past the end of the buffer. Runs exactly once per training
        // attempt, whenever enough data first exists.
        if (!_avtPllWarmedUp && TotalSamplesReceived >= _avtTrainingOriginSample)
        {
            for (var w = _avtPllWarmupStartSample; w < _avtTrainingOriginSample; w++)
            {
                _avtPllDemodulator!.ProcessSample(AvtPllSampleAt(w));
            }

            _avtPllWarmedUp = true;
        }

        // S11 fix (spec/14-roadmap.md): AvtPllSampleAt, not BandpassFilteredSampleAt*32768 -- legacy's
        // real AVT input is `ad` (sstv.cpp:1835, POST-2-tap-LPF/POST-bandpass-filter/POST-AGC, but
        // BEFORE the separate *32+clip scaling every other envelope detector in this file needs). This
        // closes the AGC-domain gap Piece A/B's own comment previously flagged and deferred here --
        // see AvtPllSampleAt's own doc comment for the full derivation and why this is a fidelity fix,
        // not an accuracy one (PllFmDemodulator's own internal AGC makes it scale-invariant to the
        // difference between the old and new feeds).
        //
        // SHOULD item 7 (spec/14-roadmap.md): confirmed directly against source, legacy's DecodeFSK
        // (sstv.cpp:1858) runs UNCONDITIONALLY every sample, including throughout AVT training (it's
        // called before the `if(!m_Sync||...)` block AVT's own case-3-8 state machine lives inside,
        // sstv.cpp:1889). Its own narrow-packet-completion handler (sstv.cpp:2589-2593) commits to the
        // narrow mode whenever `(m_SyncRestart || !m_Sync) && m_NextMode && (m_SyncMode >= 0)` --
        // round-2-review correction: AVT training's own real m_SyncMode values are 4/5/6/7/8
        // (sstv.cpp:2161/2173/2180/2202/2208/2212/2217/2224/2230/2235) plus the 256 timeout sentinel
        // (:2157/2167/2185) -- 512 is Stop()'s own value (:1786), not a training state, an earlier
        // version of this comment miscited it. All of training's real values are still >= 0 either
        // way, and m_Sync is still false during training (the image isn't locked yet -- the only
        // `m_Sync = 1` assignment anywhere in legacy is Start(), sstv.cpp:1743), so a valid narrow
        // packet found DURING AVT training genuinely aborts it in legacy. This port used to
        // short-circuit TryDecodeHeader straight into this method while pending, silently missing any
        // such packet.
        //
        // TryNarrowFskScan is interleaved HERE, per-sample, rather than checked once in TryDecodeHeader
        // before this loop starts -- matching TryInterleavedHeaderScan's own established lockstep
        // pattern (see that method's own doc comment for the identical reasoning): a bulk single
        // PushSamples call would otherwise let this while loop's own `TotalSamplesReceived` bound
        // consume the ENTIRE buffer in one shot, on the very first call, before TryDecodeHeader would
        // ever be re-entered to give a narrow check a "next time" -- found empirically, not
        // anticipated, by this port's own end-to-end test. The initial catch-up call (matching
        // TryInterleavedHeaderScan's own `Math.Min(scanBound, _syncBypassProcessedUpTo)`) brings
        // _narrowFskProcessedUpTo up to wherever this training's own cursor already sits (it can lag
        // behind here too, e.g. narrow-FSK scanning during the pre-lock VIS-header search never ran
        // this far ahead); the per-iteration call then advances it by exactly one sample per training
        // sample, reproducing legacy's real per-sample order (DecodeFSK before the sync-mode switch)
        // for every sample this loop actually examines.
        //
        // Comprehensive-review note: this scan's own D19At/FskSpaceAt ultimately read
        // BandpassFilteredSampleAt (via AgcSampleAt), whose `useLocked` gate requires `_mode is not
        // null` -- always false during AVT training (training hasn't committed `_mode` yet), so this
        // scan runs against the H2 (search) filter config. Legacy's own DecodeFSK, by contrast, reads
        // whichever filter `m_Sync || m_SyncMode >= 3` selects (sstv.cpp:1827) -- true throughout AVT
        // training (SyncMode 4-8), so legacy makes this same narrow-vs-continue-training decision from
        // the LOCKED filter's output. This is a pre-existing, already-documented port gap (see
        // BandpassFilteredSampleAt's own doc comment) that this fix makes load-bearing for the first
        // time -- before this fix, nothing decided anything from that gap during training; now a real
        // narrow-packet match/no-match decision does. Not fixed here (the gap itself is out of this
        // item's scope), flagged so it's not mistaken for new-and-clean.
        if (TryNarrowFskScan(Math.Min(TotalSamplesReceived, _avtTrainingProcessedUpTo)))
        {
            _avtTrainingPending = false;
            _avtTrainingLock = null;
            _avtPllDemodulator = null;
            return true;
        }

        while (_avtTrainingProcessedUpTo < TotalSamplesReceived)
        {
            if (TryNarrowFskScan(_avtTrainingProcessedUpTo + 1))
            {
                // Commit() (inside TryNarrowFskScan) already ran the general in-progress-image
                // teardown (AbandonInProgressImage), but AVT training's own fields are training-
                // specific and untouched by that -- clear them explicitly, matching EndOfImage's own
                // reset list for these same fields, so a LATER _mode-null re-entry into TryDecodeHeader
                // (e.g. once the narrow image itself ends) doesn't wrongly resume a training attempt
                // that's no longer relevant.
                _avtTrainingPending = false;
                _avtTrainingLock = null;
                _avtPllDemodulator = null;
                return true;
            }

            var avtDemodulatedHz = _avtPllDemodulator!.ProcessSample(AvtPllSampleAt(_avtTrainingProcessedUpTo));
            var completedAt = _avtTrainingLock!.ProcessSample(avtDemodulatedHz);
            _avtTrainingProcessedUpTo++;

            if (completedAt is not null)
            {
                // Round-2 D0-audit nit fix: null the other two training fields too, matching the two
                // narrow-FSK exits above (and EndOfImage's own reset list) -- inert either way (both
                // are read only while _avtTrainingPending, and TryStartAvtTraining always reconstructs
                // both before a future training attempt reads them), but keeps all four AVT-training
                // exits in this method internally consistent instead of two-of-four.
                _avtTrainingPending = false;
                _avtTrainingLock = null;
                _avtPllDemodulator = null;
                Commit(SstvModeRegistry.Avt, _avtTrainingOriginSample + completedAt.Value);
                return true;
            }

            if (_avtTrainingProcessedUpTo >= _avtTrainingFallbackDeadlineSample)
            {
                // Round-2 D0-audit nit fix: same consistency fix as the branch above.
                _avtTrainingPending = false;
                _avtTrainingLock = null;
                _avtPllDemodulator = null;
                Commit(SstvModeRegistry.Avt, _avtTrainingFallbackDeadlineSample);
                return true;
            }
        }

        return false;
    }
}
