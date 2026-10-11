using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

public sealed partial class AnalogFmSstvDecoder
{
    // Manual ReSync (legacy's KRFSClick, Main.cpp:14004-14020 -- ported line-for-line, not
    // ReSyncSSTV): m_SyncPos/m_SyncRPos coincide at this port's per-line observation granularity (see
    // this class's own field doc comments), so _lastLineSyncPeakPosition alone serves both the
    // deadband check and the skip computation below.
    private void PerformReSync()
    {
        if (_mode is null || _slantTracker is null || !_lastLineSyncPeakPosition.HasValue)
        {
            return;
        }

        var ofp = ComputeSyncPeakOffsetSamples(_mode); // matches legacy's literal int(SSTVSET.m_OFP)
        var syncPos = (int)_lastLineSyncPeakPosition!.Value;

        if (Math.Abs(syncPos - ofp) < 5)
        {
            return; // the deadband -- Main.cpp:14006
        }

        var skip = syncPos - ofp;
        var lineWidthSamples = (int)_effectiveSamplesPerLine; // this port's live, slant-corrected SSTVSET.m_TW equivalent
        if (skip < 0)
        {
            skip += lineWidthSamples; // Main.cpp:14009-14010 -- forward-only
        }

        // KRFSClick's other four writes -- m_AutoSyncPos = 0x7fffffff (:14013), m_AutoStopCnt = 0
        // (:14014), m_AutoStopACnt = 0 (:14015), m_AutoSyncDis = 6 (:14016). m_AutoSyncPos/m_AutoSyncDis
        // are covered by ApplySyncCorrection's own shared tail below (identical to what Auto Sync's own
        // triggers write for those two fields). m_AutoStopACnt/m_AutoStopCnt are genuinely
        // PerformReSync-specific -- Auto Sync's own triggers deliberately do NOT reset either (see
        // TryAutoSync's own doc comment for why a manual click re-arms the full warmup and an
        // automatic trigger does not), so both need their own explicit reset here. Auto Stop now exists
        // in this port too (this comment previously said m_AutoStopCnt was "NOT ported at all" -- true
        // only until Auto Stop landed; corrected here, not left stale).
        _autoSyncObservationCount = 0;
        _autoStopCnt = 0;
        ApplySyncCorrection(skip);
    }

    // Shared tail: the skip-application-and-suppression action both manual ReSync (above) and Auto
    // Sync's own two triggers (TryAutoSync) perform once each has independently decided a skip is
    // warranted -- identical for every legacy field both write (KRFSClick, Main.cpp:14004-14020;
    // AutoStopJob's own triggers, Main.cpp:3917-3925/:3950-3958): m_Skip, m_SyncPos/m_SyncRPos (this
    // port's _lastLineSyncPeakPosition=null + _suppressNextSlantProcessLine, see ApplySlantTracking's
    // own one-line-suppress branch), m_AutoSyncPos (=null here), m_AutoSyncDis (=6), and m_AutoSyncCount
    // (this port's _slantCorrectionsDisabledForRestOfImage bool). Callers differ only in what led to
    // this decision, and in m_AutoStopACnt handling, which stays with each caller -- see
    // PerformReSync's own doc comment for that divergence.
    private void ApplySyncCorrection(int skip)
    {
        _pendingSkipSamples = skip; // do NOT apply it here -- see DrainPendingSkip's own doc comment
        _lastLineSyncPeakPosition = null;
        _suppressNextSlantProcessLine = true;
        _slantCorrectionsDisabledForRestOfImage = true;
        _autoSyncReferencePosition = null;
        _autoSyncCooldown = 6;
    }

    // Port of legacy's InitAutoStop (Main.cpp:3801-3863), Auto-Sync-relevant fields only -- the
    // Auto-Slant-specific fields InitAutoStop also resets (m_ASBgnPos/m_ASDis/m_ASBitMask/etc.) are
    // already covered by SlantTracker's own construction/Reset(). Called at a fresh lock
    // (InitializeSlant) AND, unconditionally, at the top of every PerformReplay pass (buffered-replay
    // fix, §15 item 2 -- see that method's own doc comment) -- deliberately NOT after every slant
    // correction commits OUTSIDE of a replay pass, see ApplySlantTracking's own correctedSampleRate
    // branch for why an earlier draft's "reset on every commit, for consistency with
    // SlantTracker.Reset()" choice was empirically wrong (made Auto Sync untriggerable under exactly
    // the sustained-drift conditions it exists for).
    //
    // Code-level review correction: an earlier version of this comment justified NOT resetting by
    // claiming legacy's own InitAutoStop-after-commit call is "proven dead code without the not-built
    // RX-buffer-replay feature" -- that was WRONG. sys.m_UseRxBuff defaults to 1 (Main.cpp:899), and
    // OpenCloseRxBuff allocates m_StgBuf for exactly that value (sstv.cpp:1630-1639), so
    // UpdateSampFreq's own `dp->m_StgBuf != NULL` gate (Main.cpp:5597) IS satisfied by default, and
    // InitAutoStop DOES run after every commit in real legacy. The actual reason not to copy that
    // call directly at every commit: legacy resets and then immediately REPLAYS every buffered line
    // back through DrawSSTV -> AutoStopJob (Main.cpp:5603-5612, with m_ASDis=1 at :5601 suppressing
    // triggers only during that replay, cleared at :5627) -- so legacy's net Auto Sync state is
    // rebuilt, not lost. This port's own PerformReplay calls this method for exactly the same
    // reset-then-rebuild reason, at exactly the equivalent point (its own top, before the re-feed walk)
    // -- replay DOES re-feed TryAutoSync/SlantTracker (the `isReplay`-suppressed path, see TryAutoSync's
    // own doc comment and SlantTracker.ProcessLineSuppressed), so state is rebuilt via replay, not lost,
    // matching legacy's real net effect exactly now that PerformReplay always runs this call
    // unconditionally (Step B: `_rxBufferBaseTransmissionLine` stays 0 for a whole reception, so the
    // condition that used to guard this call is always true -- see PerformReplay's own call site). For
    // RxBufferMode.Off specifically, this was ALREADY provably exact, not merely a least-bad
    // approximation: legacy's own `UpdateSampFreq` gates its entire InitAutoStop-then-replay block on
    // `(dp->m_StgBuf != NULL) || WaveStg.IsOpen()` (Main.cpp:5597), which is false whenever
    // sys.m_UseRxBuff==0 -- so under Off, legacy performs NO reset and NO replay either, and this port's
    // own PerformReplay never runs under Off either (no staging buffer to replay from), so both stay in
    // lockstep with legacy exactly for Off.
    private void ResetAutoSyncDetectionState()
    {
        Array.Clear(_autoSyncPositionHistory);
        _autoSyncObservationCount = 0;
        _autoSyncReferencePosition = null;
        RecomputeAutoSyncThresholds();
        // m_AutoStopCnt = 0 (Main.cpp:3804) -- mirrors the fresh-lock InitAutoStop call AND the second
        // one UpdateSampFreq makes after every sample-rate/slant commit (Main.cpp:5597-5627), the latter
        // now via PerformReplay's own unconditional call to this method (see this method's own doc
        // comment) -- both legacy call sites are covered, not just the first.
        _autoStopCnt = 0;
    }

    // RX buffer subsystem Phase 6d code-review fix: Main.cpp:3860-3862's m_Mult/m_AutoSyncDiff half of
    // InitAutoStop, split out from ResetAutoSyncDetectionState so PerformReplay's own second-and-later
    // passes can re-derive both against a just-corrected _effectiveSamplesPerLine WITHOUT also
    // destroying the observation history/counter those passes cannot fully rebuild -- see PerformReplay's
    // own call site for the full reasoning (a real regression this split fixes, first surfaced by
    // AutoSyncTests.ManualReSync_ResetsAutoSyncObservationCount_ButNotViaAutoSyncItself once Phase 6d
    // made replay fire automatically).
    private void RecomputeAutoSyncThresholds()
    {
        _autoSyncBaseMult = (int)(_effectiveSamplesPerLine / 320.0); // Main.cpp:3860's m_Mult, from the CURRENT (slant-corrected) line width
        _autoSyncDiff = Math.Min(_autoSyncBaseMult * 3, (int)(45.0 * _sampleRate / 11025.0)); // Main.cpp:3861-3862 -- cap uses the NOMINAL declared rate, not the corrected one
    }

    // Port of legacy's m_AutoStopPos (Main.cpp:3887-3889) -- the CENTERED, ONE-SIDED-wrapped raw
    // sync-offset for the line just completed. Built from ComputeSyncPeakOffsetSamples (OFP), the same
    // quantity PerformReSync's own deadband check uses -- deliberately NOT _syncSegmentOffsetSamples
    // (ApplySlantTracking's own `relative` variable's base, fed to SlantTracker for a different
    // purpose). Round-1 plan-review finding: using the wrong base here produces a silent, constant
    // bias large enough to cause continuous spurious auto-resyncs on a perfectly-synced signal (e.g.
    // ~318 samples on Martin M1 @44.1kHz, against a ~180-sample trigger threshold).
    //
    // Wrap is ONE-SIDED ONLY (`> half ? -= lineWidth : unchanged`) -- Main.cpp:3888-3889 has no
    // `< -half` arm, unlike ApplySlantTracking's own two-sided wrap for `relative`, which is a
    // different quantity and must not be copied here.
    private int ComputeAutoSyncPosition(SstvModeDefinition mode) => WrapSyncOffset((int)_slantLinePeakPosition, mode);

    /// <summary>Shared wrap math both <see cref="ComputeAutoSyncPosition"/> (fed the live within-line
    /// <see cref="_slantLinePeakPosition"/> accumulator) and <see cref="SyncOffsetSamples"/> (fed the
    /// safe-to-read-after-the-fact <see cref="_lastLineSyncPeakPosition"/> -- see that field's own doc
    /// comment on why the two are not interchangeable as *inputs*, even though this formula is
    /// identical either way) use on their respective sync-position input.</summary>
    private int WrapSyncOffset(int syncPos, SstvModeDefinition mode)
    {
        var ofp = ComputeSyncPeakOffsetSamples(mode);
        var raw = syncPos - ofp;
        var half = (int)(_effectiveSamplesPerLine / 2.0);
        return raw > half ? (int)(raw - _effectiveSamplesPerLine) : raw;
    }

    // Port of legacy's clustering count (Main.cpp:3890-3906) -- counts how many of the 16 ring-buffer
    // entries fall within a threshold of the CURRENT position (14*mult once a full 16 real
    // observations exist, else 10*mult). Deliberately scans ALL 16 slots unconditionally, including
    // zero-initialized ones during the 8-15-observation warmup window (Main.cpp:3892's loop has no
    // "only count filled slots" logic) -- a real, load-bearing legacy quirk (a well-centered signal
    // reaches n>=8 from padding alone in that window), not an oversight to "fix."
    private int CountAutoSyncCluster(int currentPosition)
    {
        var threshold = (_autoSyncObservationCount >= 16 ? 14 : 10) * _autoSyncBaseMult;
        var n = 0;
        foreach (var entry in _autoSyncPositionHistory)
        {
            if (Math.Abs(currentPosition - entry) <= threshold)
            {
                n++;
            }
        }

        return n;
    }

    // Port of legacy's TMmsstv::AutoStopJob (Main.cpp:3884-4035) -- the Auto Sync (sys.m_AutoSync) AND
    // Auto Stop (sys.m_AutoStop, RxAutoPush) portions. KRSA (sample-rate auto-calibration,
    // Main.cpp:3968-4032) stays out of scope -- a third, unrelated sibling sharing this same legacy
    // function, with no toggle/UI in this port.
    //
    // Auto Stop's own action (abandon reception, re-arm auto-detection) is NOT applied synchronously
    // from inside this method -- this method only decides and records that intent
    // (_autoStopTriggered=true), then returns immediately (mirroring Main.cpp:3937's own `return TRUE;`,
    // which skips branch 2 and the tail bookkeeping below). The actual DecodeRestarted fire + EndOfImage
    // reset happen in TryProcessBuffer's own per-line loop, right after ApplySlantTracking() (which
    // calls this method) returns -- see that call site's own doc comment. Reason: this method runs
    // MID-ApplySlantTracking, and the statements immediately after this method's own call site
    // dereference _slantTracker/_syncEnvelopeDetector with no null-check; EndOfImage() nulls both, so
    // calling it synchronously from here would be a guaranteed NRE. Auditor plan-review finding, round 1.
    //
    // Re-entrancy guard below: legacy's own equivalent is unreachable once Stop() has run (AutoStopJob's
    // caller gate, Main.cpp:4190, requires m_Sync, which Stop() clears) -- reproduces that "can't fire
    // twice before the deferred consumption in the per-line loop runs" property explicitly, defensive
    // even though today's single-call-per-line structure shouldn't make it reachable either way.
    //
    // Called from ApplySlantTracking's own per-line loop, in the `else` arm of the
    // _suppressNextSlantProcessLine check, ahead of the existing _slantCorrectionsDisabledForRestOfImage/
    // ProcessLine split -- matches legacy's own AutoStopJob call (Main.cpp:4190, gated on `m_SyncPos !=
    // -1`, this port's equivalent exclusion) running ahead of the Auto-Slant regression block
    // (Main.cpp:3968). AVT exclusion is free: InitializeSlant nulls _slantTracker for AVT, and
    // ApplySlantTracking already returns immediately in that case, before ever reaching this call.
    //
    // KRSA->Checked (Main.cpp:3886's own entry gate, plus the branch-1 threshold ternary at :3910/:3917)
    // -- Main.cpp:3886's own OUTER gate (AutoStop||AutoSync||KRSA->Checked) was never ported as a gate
    // at all: this method's own bookkeeping (below) runs unconditionally regardless of any of the
    // three flags' values, matching every reachable state that outer OR can produce anyway (only the
    // individual trigger conditions below carry their own separate `sys.m_AutoSync &&`/`sys.m_AutoStop
    // &&` terms, via _autoSyncEnabled/_autoStopEnabled) -- so _autoSlantEnabled adds no new gate here
    // either. It DOES gate branch 1's own threshold below (`_autoSlantEnabled ? 5 : 2` times the base
    // multiplier, porting Main.cpp:3910/:3917's `(KRSA->Checked ? 5 : 2)*m_Mult` exactly) -- an earlier
    // version of this port hardcoded `5 * m_Mult` unconditionally, correct only while no Auto Slant
    // toggle existed to ever make the `2` side reachable (auditor plan-review finding before
    // _autoSlantEnabled was added).
    //
    // `!m_ASDis` (RX buffer subsystem Phase 6b): `isReplay` below is that gate, finally implemented.
    // `m_ASDis` is set to 1 only while replaying the legacy RX staging buffer after a sample-rate/slant
    // recalculation (UpdateSampFreq/RedrawSSTV, Main.cpp:5601-5864) -- Main.cpp:3907/:3933/:3945's own
    // `!m_ASDis` term (round-1 code-review correction: NOT :3917, that's the unrelated `(KRSA->Checked
    // ? 5 : 2)*m_Mult` threshold line cited two paragraphs up) applies to the TRIGGER conditions only
    // (branch 1's pair at :3907, Auto Stop's `_autoStopCnt >= 8` check at :3933, branch 2's pair at
    // :3945), never to the unconditional bookkeeping below (history ring, cluster/reference tracking,
    // cooldown decrement) -- legacy's own replay "rebuilds, not loses" that state (see
    // ResetAutoSyncDetectionState's own doc comment). Ported as `&& !isReplay` appended to each of
    // those three trigger conditions, nothing else.
    //
    // RX buffer subsystem Phase 3: `sys.m_UseRxBuff` gates branch 1 and branch 2 independently of
    // `m_ASDis`/replay -- reachable the moment RxBufferMode.Off is selectable, with NO buffer or
    // replay code involved at all. Branch 1's full legacy condition (Main.cpp:3907) has a trailing
    // `&& sys.m_UseRxBuff` term separate from every other condition -- ported below as
    // `_rxBufferMode != RxBufferMode.Off` (not `== RxBufferMode.On` -- Extended counts as "buffer
    // present" for this purpose too). Branch 2's condition (Main.cpp:3945) is
    // `(m_AutoSyncCount || !sys.m_UseRxBuff)` -- ported below as
    // `(_slantCorrectionsDisabledForRestOfImage || _rxBufferMode == RxBufferMode.Off)`. (The nested
    // `if(m_AutoSyncCount || !sys.m_UseRxBuff)` INSIDE branch 1's own body at Main.cpp:3911 is
    // tautologically false given branch 1's outer gate above -- both disjuncts are already forced
    // false by the time it's reached -- so it always takes the `else` arm, `df = m_AutoStopPos -
    // m_AutoSyncPos`, which is exactly what this port's `Math.Abs(currentPosition - reference)` below
    // already implements; nothing to change there.)
    //
    // Round-2/3 auditor finding: branch 1's own `_rxBufferMode != RxBufferMode.Off` term below is NOT
    // covered by any test that fails if it's reverted -- an earlier test attempt was found, on review,
    // to actually be exercising the CLUSTER threshold (CountAutoSyncCluster, 14*mult once >=16
    // observations exist) staying satisfied, not branch 1's own threshold failing; branch 1 was never
    // even being EVALUATED in that scenario. Round 3 corrected an overclaim in the round-2 fix's own
    // comment ("not constructible"): branch 2's own step test is actually STRICTER than branch 1's
    // (<=15 vs <=25 for this mode/rate), only its magnitude test is looser (>=15 vs >=25) -- so there
    // IS an on-paper window, sustained drift in (15,25] samples/line, where branch 2's step test fails
    // every line while branch 1's own pair could still pass if the remaining preconditions (n in [2,4),
    // a stable reference, cooldown==0) happen to line up on a real signal -- not yet confirmed to occur.
    // This term is verified today by direct source correspondence against Main.cpp:3907 only (confirmed
    // independently across three auditor code-review rounds), not by a dedicated failing-on-revert test
    // -- see RxBufferModeGatingTests.cs's own TryAutoSync_Branch2_UnlocksOnASmallSplice_
    // ExtendedMatchesOnNotOff test for the fuller reasoning trail and the unconfirmed candidate window.
    //
    // The (m_SyncMax-m_SyncMin)>5000 signal-strength test and this port's own _pendingSkipSamples guard
    // live INSIDE each Auto Sync trigger branch, NOT in the outer `_autoSyncObservationCount >= 8` gate
    // -- legacy has that test only at Main.cpp:3908/:3946 (inside each trigger body), while the n>=4
    // reference update at :3941 is gated on m_AutoStopACnt>=8 and n>=4 alone, nothing else (code-level
    // review finding from Auto Sync's own earlier round: hoisting both into the outer condition froze
    // the reference on weak-signal lines, both suppressing legitimate triggers once the signal
    // recovered and enabling spurious ones from a stale pre-weak-stretch reference).
    private void TryAutoSync(SstvModeDefinition mode, bool isReplay)
    {
        if (_autoStopTriggered)
        {
            return;
        }

        var currentPosition = ComputeAutoSyncPosition(mode);
        _lastComputedAutoSyncPositionForTests = currentPosition; // captured at the only moment it's valid -- see that field's own doc comment
        var previousPosition = _autoSyncPositionHistory[^1];

        // Bookkeeping (Main.cpp:3963-3966) below always runs (except when Auto Stop's own trigger
        // fires and returns early, mirroring Main.cpp:3937); only the individual trigger conditions
        // read _autoSyncEnabled/_autoStopEnabled -- see this method's own doc comment for why the
        // outer gate can't be used to skip the whole method when a setting is off.
        if (_autoSyncObservationCount >= 8)
        {
            var n = CountAutoSyncCluster(currentPosition);
            var envelopeSpread = _slantLineMaxEnvelope - _slantLineMinEnvelope; // one local, reused for both Auto Sync's and Auto Stop's own gates
            _lastAutoStopEnvelopeSpreadForTests = envelopeSpread;
            var signalStrongEnough = envelopeSpread > 5000;

            if (n < 4)
            {
                // Branch 1 (Main.cpp:3906-3929): can only fire once per image
                // (!_slantCorrectionsDisabledForRestOfImage, port of !m_AutoSyncCount). Two-part test:
                // (a) a small, non-noisy step from the immediately preceding observation, AND (b) a
                // real, large jump from the last stable reference. Threshold is `(KRSA->Checked ? 5 :
                // 2) * m_Mult` in legacy (Main.cpp:3910/:3917) -- auditor plan-review finding: this
                // port previously hardcoded the `5` side unconditionally, correct only before
                // _autoSlantEnabled existed to ever make the `2` side reachable.
                var branch1Threshold = (_autoSlantEnabled ? 5 : 2) * _autoSyncBaseMult;
                _lastBranch1ThresholdForTests = branch1Threshold;
                if (_autoSyncEnabled && signalStrongEnough && _pendingSkipSamples == 0
                    && n >= 2 && _autoSyncReferencePosition is { } reference
                    && !_slantCorrectionsDisabledForRestOfImage
                    && _rxBufferMode != RxBufferMode.Off // Main.cpp:3907's trailing `&& sys.m_UseRxBuff` -- not `== On`, Extended counts too
                    && !isReplay // Main.cpp:3907's `!m_ASDis`
                    && Math.Abs(currentPosition - previousPosition) <= branch1Threshold
                    && Math.Abs(currentPosition - reference) >= branch1Threshold)
                {
                    TriggerAutoSync(currentPosition);
                    _autoStopCnt = Math.Max(0, _autoStopCnt - 1); // Main.cpp:3925
                }

                // Auto Stop's own increment (Main.cpp:3930-3932) -- 8192, NOT 5000 (that's Auto Sync's
                // own signalStrongEnough gate above; a different threshold on the same quantity).
                if (n < 2 || envelopeSpread < 8192)
                {
                    _autoStopCnt++; // Main.cpp:3931
                }

                // Auto Stop's own trigger (Main.cpp:3933-3937). Deferred, not applied here -- see this
                // method's own doc comment. The early `return` mirrors Main.cpp:3937's own
                // `return TRUE;`, skipping branch 2 and the tail bookkeeping below for this line.
                if (_autoStopEnabled && !isReplay && _autoStopCnt >= 8) // Main.cpp:3933's `!m_ASDis`
                {
                    _autoStopTriggered = true;
                    _autoStopTriggerCountForTests++;
                    return;
                }
            }
            else
            {
                _autoSyncReferencePosition = currentPosition; // Main.cpp:3941 -- bookkeeping only, no skip applied here, NOT gated on signal strength
                _autoStopCnt = Math.Max(0, _autoStopCnt - 2); // Main.cpp:3942-3943
            }

            // Branch 2 (Main.cpp:3945-3961): runs regardless of n<4 or n>=4 this same call. Can only
            // fire once branch 1 (or an n>=4 reference re-establishment) has already set a reference
            // this image, and respects its own cooldown. Two-part test: (a) a small step from the
            // immediately preceding observation, AND (b) the ABSOLUTE position vs zero (not vs the
            // reference -- a different comparison target than branch 1's own (b)).
            if (_autoSyncEnabled && signalStrongEnough && _pendingSkipSamples == 0
                && _autoSyncCooldown == 0 && _autoSyncReferencePosition is not null
                && (_slantCorrectionsDisabledForRestOfImage || _rxBufferMode == RxBufferMode.Off) // Main.cpp:3945's `(m_AutoSyncCount || !sys.m_UseRxBuff)`
                && !isReplay // Main.cpp:3945's `!m_ASDis`
                && Math.Abs(currentPosition - previousPosition) <= _autoSyncDiff
                && Math.Abs(currentPosition) >= _autoSyncDiff)
            {
                TriggerAutoSync(currentPosition);
                _autoStopCnt = Math.Max(0, _autoStopCnt - 1); // Main.cpp:3957
            }
        }

        // Main.cpp:3963-3966, unconditional (except Auto Stop's own early return above).
        if (_autoSyncCooldown > 0)
        {
            _autoSyncCooldown--;
        }

        _autoSyncObservationCount++;
        Array.Copy(_autoSyncPositionHistory, 1, _autoSyncPositionHistory, 0, _autoSyncPositionHistory.Length - 1);
        _autoSyncPositionHistory[^1] = currentPosition;
    }

    private void TriggerAutoSync(int position)
    {
        var skip = position;
        if (skip < 0)
        {
            skip += (int)_effectiveSamplesPerLine; // forward-only, matching ApplySyncCorrection's own callers
        }

        ApplySyncCorrection(skip);
        _autoSyncTriggerCountForTests++;
    }

    // Same derivation TryResolveSyncAnchorCorrection already has (:2337-2354 area) -- duplicated
    // deliberately, not refactored into a shared helper with THAT method (small, read-only,
    // already-audited computation; touching that sibling method carries more risk than a few
    // duplicated lines). Extracted to its own method only so PerformReSync and
    // SyncPeakOffsetSamplesForTests (below) share one implementation instead of two copies of this
    // one's own math -- narrower reuse than the sibling-method case above.
    private int ComputeSyncPeakOffsetSamples(SstvModeDefinition mode)
    {
        var targetToneHz = mode.NarrowModeCode is not null ? 1900.0 : 1200.0;
        var preSyncSegmentOffsetMs = 0.0;
        foreach (var segment in mode.LineSegments)
        {
            if (segment is SyncSegment syncSegment && syncSegment.FrequencyHz == targetToneHz)
            {
                break;
            }

            preSyncSegmentOffsetMs += segment.DurationMs;
        }

        var syncPeakOffsetSamples = (preSyncSegmentOffsetMs + SstvModeRegistry.GetSyncPeakOffsetMs(mode)) / 1000.0 * _sampleRate;
        return (int)syncPeakOffsetSamples;
    }

    // RX buffer subsystem Phase 6c: a second, deliberately-duplicated copy of
    // ComputeSyncPeakOffsetSamples's own pre-sync-segment-offset loop -- same reasoning as that
    // method's own doc comment for why it doesn't share code with TryResolveSyncAnchorCorrection
    // (small, read-only, already-audited; touching it carries more risk than a few duplicated lines).
    // Two real differences from ComputeSyncPeakOffsetSamples, both required by
    // ReplayOriginCalculator.ComputeOrigin's own documented contract: (1) takes the CORRECTED rate as
    // a parameter, not this decoder's own nominal `_sampleRate` -- legacy's own `SSTVSET.m_OFP` is
    // recomputed by `CSSTVSET::SetSampFreq()` (sstv.cpp:655-965's per-mode switch, every arm literally
    // `m_OFP = X_ms * m_SampFreq / 1000.0`) using whatever `m_SampFreq` SetSampFreq was just called
    // with -- for a replay pass that's the JUST-CORRECTED rate (Main.cpp:5586's SetSampFreq() call,
    // immediately after a slant commit), never the nominal device rate `ComputeSyncPeakOffsetSamples`
    // uses for its own, different call site (the VIS-lock anchor, computed before any slant correction
    // exists to apply -- see ReplayOriginCalculator.ComputeOrigin's own `sampleRate` parameter doc
    // comment). (2) returns the UNTRUNCATED double, not `(int)`-cast here -- ReplayOriginCalculator.
    // AdjustPosition's own truncation-order requirement (its own doc comment) needs the full-precision
    // value, truncating only once it's combined with the histogram argmax bin, not before.
    private static double ComputeUntruncatedSyncPeakOffsetSamples(SstvModeDefinition mode, double sampleRate)
    {
        var targetToneHz = mode.NarrowModeCode is not null ? 1900.0 : 1200.0;
        var preSyncSegmentOffsetMs = 0.0;
        foreach (var segment in mode.LineSegments)
        {
            if (segment is SyncSegment syncSegment && syncSegment.FrequencyHz == targetToneHz)
            {
                break;
            }

            preSyncSegmentOffsetMs += segment.DurationMs;
        }

        return (preSyncSegmentOffsetMs + SstvModeRegistry.GetSyncPeakOffsetMs(mode)) / 1000.0 * sampleRate;
    }
}
