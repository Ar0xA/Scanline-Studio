using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

public sealed partial class AnalogFmSstvDecoder
{
    // Direct port of Auto Slant's setup (InitAutoStop, Main.cpp:3801-3862) -- excluded for AVT, same
    // as AFC, but via its own SEPARATE guard (Main.cpp:3886's `mode != smAVT`, not the same gate AFC
    // uses -- code-level review correction: an earlier version of this comment claimed "the same
    // outer gate," but AFC's own real exclusion is `sstv.cpp:2258/2263/2267`'s
    // `m_afc && m_CurMax>16 && mode!=smAVT`, a different guard in a different function).
    // See SlantTracker's doc comment for what's deliberately not ported (Auto Stop, Auto Sync, and
    // retroactive re-decode of already-buffered lines) and why the math itself still is.
    private void InitializeSlant(SstvModeDefinition mode)
    {
        _slantProcessedUpTo = _consumedSamples;
        _effectiveSamplesPerLine = mode.LineDurationMs / 1000.0 * _sampleRate;
        _slantIdealSamplesSoFarInLine = 0;
        _slantLineMaxEnvelope = double.NegativeInfinity;
        _slantLineMinEnvelope = double.PositiveInfinity;
        _slantLineEnvelopeSeeded = false;
        _slantLinePeakPosition = 0;

        // RX buffer subsystem Phase 5: fresh-lock reset, mirrors legacy's own `dp->m_wStgLine = 0`
        // (Main.cpp:4958) -- unconditional, before the AVT early-return below, matching every other
        // per-line-accounting reset on this method's own first few lines. Any partial in-progress line
        // from a just-abandoned prior reception is discarded, not carried into the new lock.
        _rxLineStagingBuffer?.Clear();
        _rxBufferLineDemod.Clear();
        _rxBufferLineSync.Clear();
        _rxBufferAnchorSample = _consumedSamples; // RX buffer subsystem Phase 6c round-2 fix -- see this field's own doc comment
        _rxBufferBaseTransmissionLine = 0; // RX buffer subsystem Phase 6c round-4 fix -- fresh lock: staged index 0 IS image row 0 again
        _replayOnceLatchFired = false; // RX buffer subsystem Phase 6d -- fresh lock, the once-per-image latch re-arms
        _pendingReplayRequested = false; // RX buffer subsystem Phase 6d -- a stale request from an abandoned prior image must not replay against the NEW one's staging buffer
        _correctSlantRequested = false; // RX buffer subsystem Phase 8c -- same reasoning as _pendingReplayRequested above: a stale manual request from an abandoned prior image must not search against the NEW one's staging buffer
        _anyCorrectionCommittedThisImage = false; // RX buffer subsystem Phase 6d round-2 -- fresh lock, fresh image, no commit has happened yet
        // D0-audit round-12 fix: moved ahead of the AVT early-return below, matching every other
        // per-line-accounting reset above -- the real, mode-specific value is still assigned further
        // down for non-AVT modes (this method's own tail), this is only the AVT-lock default. Provably
        // unread for AVT either way (its only reader, ProcessSlantTrackingSample, is unreachable once
        // _slantTracker is null, see the AVT branch immediately below) -- inert today, fixed for
        // consistency with this method's own established convention rather than left as the sole
        // exception to it.
        _syncSegmentOffsetSamples = 0;

        if (mode == SstvModeRegistry.Avt)
        {
            // AVT is not captured -- ApplySlantTracking (this class's own capture hook, see its doc
            // comment) no-ops entirely for AVT via the _slantTracker-null check below, matching every
            // other Auto-Slant-adjacent feature's own established AVT exclusion in this port (Auto
            // Sync, Auto Slant, manual ReSync all already exclude AVT the same way). A real, documented
            // scope gap, not silently dropped: legacy's own capture is NOT AVT-gated (it happens in the
            // general per-line draw loop, Main.cpp:4996-5013, before any Auto-Slant-family dispatch) --
            // this port's AVT-exclusion-by-construction is narrower than legacy's real capture scope.
            // Revisit if/when a concrete AVT-replay use case appears; not chased further here per this
            // project's own scope discipline.
            _syncEnvelopeDetector = null;
            _slantTracker = null;
            return;
        }

        var isNarrow = mode.NarrowModeCode is not null;
        _syncEnvelopeDetector = new SyncEnvelopeDetector(_sampleRate, isNarrow ? 1900.0 : 1200.0);
        _syncSegmentOffsetSamples = SstvModeRegistry.GetSyncSegmentOffsetMs(mode) / 1000.0 * _sampleRate;
        _slantTracker = new SlantTracker(_sampleRate, _effectiveSamplesPerLine, SstvModeRegistry.GetAutoSlantThresholdPositions(mode));

        // Auto Sync's own InitAutoStop-equivalent (Main.cpp:3801-3863, Auto-Sync-relevant fields only
        // -- see ResetAutoSyncDetectionState's own doc comment). Deliberately AFTER the AVT
        // early-return above: Auto Sync state has no meaning for AVT (matches AutoStopJob's own entry
        // gate, Main.cpp:3886, excluding smAVT), and this port's AVT exclusion is free because
        // TryAutoSync is only ever called from inside this same non-AVT branch of ApplySlantTracking.
        ResetAutoSyncDetectionState();
    }

    // Legacy tracks the sync-envelope's peak position continuously as part of the same real-time
    // pass as everything else, and AutoStopJob (called once per line, at that line's start, using
    // the position found during the line just completed) commits corrections that affect
    // SSTVSET.m_TW/m_SampFreq going forward. This port's equivalent: walk the raw samples using the
    // *current* _effectiveSamplesPerLine to find each line's boundary (matching legacy's own
    // self-referential `ps = fmod(n, m_TW)`, where m_TW can itself change mid-stream), track the
    // envelope's peak within each line, and feed completed lines to SlantTracker one at a time.
    private void ApplySlantTracking()
    {
        if (_slantTracker is null)
        {
            return;
        }

        // Bounded by _consumedSamples, NOT _rawSamples.Count: never process raw samples beyond what
        // pixel decode has actually consumed so far -- otherwise, when a caller pushes a large batch
        // at once, this would run ahead into not-yet-decoded (or, past the last real line, into the
        // trailing footer tone's non-image content) samples. See TryProcessBuffer's call site.
        for (; _slantProcessedUpTo < _consumedSamples; _slantProcessedUpTo++)
        {
            // Piece 7b2: legacy feeds this same envelope (m_B12[n] = d12, or d19 for narrow modes)
            // from the AGC'd/scaled signal too (sstv.cpp:2292-2299, live branch since NARROW_SYNC==
            // 1900) -- the exact same d12/d19 values already computed once per sample ahead of the
            // switch(m_SyncMode) dispatch (sstv.cpp:1841-1853), not a separately-scaled reading.
            var envelope = _syncEnvelopeDetector!.ProcessSample(AgcSampleAt(_slantProcessedUpTo));

            // RX buffer subsystem Phase 5: capture hook. Mirrors legacy's own per-sample write into
            // m_StgBuf/m_StgB12 (Main.cpp:4996-5013) -- demod-stream half from DemodulatedFrequencyAt
            // (this port's m_Buf equivalent, already AFC-corrected by the time this line reaches here,
            // see RxLineStagingBuffer's own doc comment on why that's the legacy-correct capture
            // point), sync-stream half from `envelope` just computed above (this port's m_StgB12
            // equivalent -- reused directly, NOT a second SyncEnvelopeDetector call, which would
            // double-process a streaming filter that must see each sample exactly once).
            //
            // Round-1 code-review correction: an earlier version of this comment claimed
            // DemodulatedFrequencyAt(_slantProcessedUpTo) is always a pure cache read because
            // `DecodeLine` (via PixelSampleReader) has already computed every index in this line --
            // WRONG. PixelSampleReader.ReadBare/ReadPeakPicked only ever read at PIXEL-START indices
            // (spaced `KsbSamples`-ish apart), not every sample -- DecodeLine leaves a real gap of
            // uncomputed indices at the tail of most lines. The actual guarantee this loop relies on is
            // `ApplyAfcCorrections(nextLineStartSample)` (called earlier in TryProcessBuffer's own
            // per-line loop, before DecodeLine), whose own loop DOES call DemodulatedFrequencyAt for
            // EVERY index up to its bound -- but only when (a) `_afcTracker is not null` (false when
            // this decoder is constructed with `afcEnabled: false`, a real user setting) AND (b)
            // `nextLineStartSample &lt;= _afcBoundSample` (can fail on the trailing lines of an image
            // after an Auto-Slant commit lengthens the per-line stride). Outside those two conditions,
            // THIS loop can be the first toucher of a line's tail samples -- still safe in the sense
            // that DemodulatedFrequencyAt's own strict-monotonic-first-touch contract is preserved (no
            // double-computation, no corrupted streaming-filter state). Round-3 code-review correction:
            // an earlier version of this comment additionally claimed the first-touch timing could
            // change `useLocked` (this line's own bandpass-filter selection) -- WRONG. This SAME loop
            // already calls `AgcSampleAt(_slantProcessedUpTo)` one line above (feeding `envelope`),
            // which settles `BandpassFilteredSampleAt`/`useLocked` for that exact index UNCONDITIONALLY,
            // in every RxBufferMode -- so by the time this hook's own DemodulatedFrequencyAt call runs,
            // `useLocked` for this sample is already fixed, regardless of whether capture is on. The
            // real, narrower residual risk is `isNarrow` (`DemodulatedFrequencyAt`'s own
            // `thisIndex >= _bandpassLockedFromSample` check): constant for the whole of an ordinary
            // image, but a mid-image restart landing on a NARROW mode could in principle put this loop's
            // own first-touch on the wrong side of that check versus a run where capture is off and
            // touches those same indices later, after the restart's own re-anchor. Untested (this port's
            // own narrow-mode H3/HBPFN gap already puts that combination outside today's fixture
            // coverage) -- flagged, not fixed, here.
            //
            // Buffered per-sample in _rxBufferLineDemod/_rxBufferLineSync (fields, not locals -- this
            // loop can pause mid-line across PushSamples calls). RX buffer subsystem Phase 6b: this
            // capture-append, and the capture-flush below, deliberately stay OUTSIDE
            // ProcessSlantTrackingSample's own extracted core -- replaying already-staged data must
            // NEVER re-stage it, so both steps are live-decode-only, never shared with the replay path.
            // Un-stub-RX-tab Piece B: hoisted so the Decoder Trace channel-1 write below can reuse
            // this exact value instead of calling DemodulatedFrequencyAt(_slantProcessedUpTo) a
            // second time for the same index (harmless but wasteful -- see this method's own doc
            // comment above for why this index is already, by this point, either genuinely
            // post-AFC-corrected or legitimately still raw, matching legacy's own "always applied,
            // possibly a no-op" m_AFCDiff semantics either way).
            var demodulatedHz = DemodulatedFrequencyAt(_slantProcessedUpTo);

            if (_rxLineStagingBuffer is not null)
            {
                _rxBufferLineDemod.Add(demodulatedHz);
                _rxBufferLineSync.Add(envelope);
            }

            // Un-stub-RX-tab Piece B: Decoder Trace channel-1 write, this port's counterpart to
            // legacy's m_Scope[1].WriteData(d) (sstv.cpp:2286, inside if(m_Sync){...} -- this whole
            // loop only runs while _slantTracker is non-null. NOT AVT-gated in legacy, but IS here --
            // InitializeSlant nulls _slantTracker for AVT, same accepted gap RX buffer's own capture
            // hook already documents above -- channel 1 never fills during AVT training). Units
            // conversion required (unlike channel 0): DemodulatedFrequencyAt returns Hz, but legacy's
            // `d` here is CHILL::Do's raw, never-Hz-converted ±16384-domain scaled value
            // (sstv.cpp:3086) -- invert HilbertFmDemodulator's own Hz descale
            // (`centerHz - scaled*bandwidthHz/32768.0`) using the SAME center/bandwidth pair it
            // itself uses for this exact index. Recomputed here, not cached -- auditor code-review
            // correction: "guaranteed to match" overclaimed this for an earlier version of this
            // comment. The _bandpassLockedFromSample half is genuinely safe (Commit sets both it and
            // _slantProcessedUpTo's own anchor from the SAME _consumedSamples snapshot, so this loop
            // can never sit below it) -- but _mode itself is not: _demodulatedFrequenciesProcessedUpTo
            // is never rewound, so a re-lock whose anchor lands behind already-Hz-cached indices would
            // re-walk them here under the NEW _mode while DemodulatedFrequencyAt originally filled them
            // under the OLD one. Only observable across a narrowness-CHANGING re-lock (same gap this
            // port's own narrow-mode coverage already leaves untested elsewhere) -- flagged, not fixed.
            if (_scopeCaptureChannel1.IsCapturing)
            {
                var isNarrow = _mode is not null && _mode.NarrowModeCode is not null && _slantProcessedUpTo >= _bandpassLockedFromSample;
                var centerHz = isNarrow ? HilbertFmDemodulator.NarrowCenterHz : HilbertFmDemodulator.NormalCenterHz;
                var bandwidthHz = isNarrow ? HilbertFmDemodulator.NarrowBandwidthHz : HilbertFmDemodulator.NormalBandwidthHz;
                _scopeCaptureChannel1.Write((centerHz - demodulatedHz) * 32768.0 / bandwidthHz);
            }

            var lineCompleted = ProcessSlantTrackingSample(envelope, isReplay: false);

            // RX buffer subsystem Phase 5: flush this now-completed line to the staging buffer, one
            // atomic TryAppendLine call per line (matching legacy's own per-line, not per-sample,
            // buffer-full admission test). Runs regardless of which branch inside
            // ProcessSlantTrackingSample fired (suppressed, history-only, or a real ProcessLine commit)
            // -- legacy's own capture (Main.cpp:4996-5013) happens unconditionally in the general
            // per-line draw loop too, before any Auto-Slant-family branching.
            if (lineCompleted && _rxLineStagingBuffer is not null)
            {
                // Functional-audit fix (D3+D8+D9 coupled round 1 introduced a regression here, caught
                // and reverted in round 2 -- see _rxBufferAnchorSample's own doc comment for the full
                // corrected reasoning): a round-1 attempt bumped _rxBufferAnchorSample by this line's
                // own sample count whenever TryAppendLine below returns false (rejected, buffer-full),
                // reasoning that the field's own doc comment described a "count invariant"
                // (_consumedSamples - anchor == staged + in-flight) that a rejection would silently
                // break. That reasoning inverted the field's actual, load-bearing meaning: it defines
                // a FIXED coordinate map (`d(raw) = origin + (raw - anchor)`, established once at the
                // last fresh lock and unchanged for the rest of the reception -- buffered-replay fix,
                // §15 item 2, retired the OTHER re-anchor trigger this comment used to also name, a
                // replay-pass truncation; see this field's own doc comment), not a running tally of
                // staged content. PerformReplay's own resumeDest computation
                // (this method's sibling, see its own doc comment there) explicitly depends on that
                // map staying fixed so it reflects the LIVE cursor's true position even when it has
                // moved past un-staged (rejected) content -- moving the anchor on rejection instead
                // pins resumeDest to the STAGED extent, which is exactly the wrong quantity that doc
                // comment already warns against, and silently re-stamps already-correct rows with
                // stale audio on the next replay pass. Confirmed via a worked numeric trace during
                // round 2 review: pre-round-1 behavior (anchor untouched here) computes the correct
                // resume row; round-1's fix computes a row 5+ positions too early for the same input.
                // No compensation belongs here -- a rejected line simply means this line's samples
                // are not part of the replayable buffer at all, correctly reflected by resumeDest
                // legitimately running ahead of stagedSampleCount once decode has passed them.
                _rxLineStagingBuffer.TryAppendLine(CollectionsMarshal.AsSpan(_rxBufferLineDemod), CollectionsMarshal.AsSpan(_rxBufferLineSync));
                _rxBufferLineDemod.Clear();
                _rxBufferLineSync.Clear();
            }
        }
    }

    /// <summary>RX buffer subsystem Phase 6b -- the data-source-agnostic core of per-sample Auto-Sync/
    /// Auto-Slant bookkeeping, extracted from <see cref="ApplySlantTracking"/>'s own former single
    /// per-sample loop body so Phase 6c's still-unbuilt replay engine can drive the SAME logic from the
    /// staged sync-envelope stream instead of <see cref="_syncEnvelopeDetector"/>/<see cref="AgcSampleAt"/>.
    /// Mechanical extraction for the LIVE path (<paramref name="isReplay"/><c> = false</c>) -- same
    /// inputs, same order, same computation as before this phase, just re-entered through an extra
    /// layer of indirection; verified zero-behavior-change via the full existing regression suite. The
    /// REPLAY path (<paramref name="isReplay"/><c> = true</c>, not yet exercised by any caller until
    /// Phase 6c lands) is new behavior with its own dedicated tests.
    ///
    /// Deliberately EXCLUDES the RX buffer staging-capture append/flush steps (Phase 5's own hook) --
    /// those stay in <see cref="ApplySlantTracking"/>'s own per-sample loop, never here, since replaying
    /// already-staged data must never re-stage it.
    ///
    /// Returns <see langword="true"/> exactly when this call completed a line (legacy's own
    /// `!int(ps)` line-boundary transition, `Main.cpp:4189`) -- <see cref="ApplySlantTracking"/> uses
    /// this to know when to flush staging-buffer capture; Phase 6c's replay engine will use it to know
    /// when to advance to the next staged line.</summary>
    private bool ProcessSlantTrackingSample(double envelope, bool isReplay)
    {
        // Auto Sync's own (m_SyncMax-m_SyncMin)>5000 gate needs both a running max AND min per
        // line (Main.cpp:4193/4196/4200-4201) -- legacy seeds BOTH from the line's first sample
        // explicitly, then uses an else-if chain for the rest (one sample can't set both). Round-2
        // plan-review finding: a naive independent-if-per-extreme implementation with infinity
        // sentinels leaves min stuck at +Infinity on a monotonically-rising line, since the max
        // branch would keep winning every single sample.
        if (!_slantLineEnvelopeSeeded)
        {
            _slantLineEnvelopeSeeded = true;
            _slantLineMaxEnvelope = envelope;
            _slantLineMinEnvelope = envelope;
            _slantLinePeakPosition = _slantIdealSamplesSoFarInLine;
        }
        else if (envelope > _slantLineMaxEnvelope)
        {
            _slantLineMaxEnvelope = envelope;
            _slantLinePeakPosition = _slantIdealSamplesSoFarInLine;
        }
        else if (envelope < _slantLineMinEnvelope)
        {
            _slantLineMinEnvelope = envelope;
        }

        _slantIdealSamplesSoFarInLine += 1;

        if (_slantIdealSamplesSoFarInLine < _effectiveSamplesPerLine)
        {
            return false;
        }

        // Line complete. Report the peak position relative to this mode's expected sync offset,
        // wrapped to the representation closest to zero (Main.cpp:5877-5883-style centering) --
        // mirrors legacy's own m_AutoStopPos wraparound, needed so a peak that lands just before
        // vs. just after the line boundary isn't reported as a huge spurious jump.
        // Legacy computes this quantity entirely in `int` (Main.h:1350's `int m_AutoStopPos`), with
        // truncation at three separate assignments, all C-style toward zero (what a C# `(int)` cast
        // does): the within-line peak is truncated on assignment into `int m_SyncPos`
        // (Main.cpp:4198, `m_SyncPos = ps;` from a double `ps`), the sync offset is truncated
        // BEFORE the subtraction (Main.cpp:3887, `- int(SSTVSET.m_OFP)`), and the wrapped result is
        // truncated again on assignment back into the same int (Main.cpp:3889). Truncating only the
        // final difference is NOT equivalent -- trunc(a) - trunc(b) and trunc(a - b) differ by 1
        // whenever both have fractional parts (e.g. 10 - 3.7 -> 7 vs 6). Everything downstream in
        // SlantTracker (the 16-entry history, GetSqerrPos, the 8*mult jitter gate, m_ASBgnPos) is
        // integer-typed in legacy and calibrated in whole samples.
        var relative = (int)_slantLinePeakPosition - (int)_syncSegmentOffsetSamples;

        // Manual ReSync's capture point (legacy's m_SyncRPos = m_SyncPos at its own line-boundary
        // point, Main.cpp:4193-4194) -- MUST stay here, before the two-flag suppression block
        // below, not moved down next to the _slantLineMaxEnvelope/_slantLinePeakPosition resets.
        // The one-line-suppress branch's own `_lastLineSyncPeakPosition = null;` only has any
        // effect because this capture already ran earlier in this same pass; placing it after
        // that block would silently overwrite the null and make the suppression dead code.
        _lastLineSyncPeakPosition = _slantLinePeakPosition;

        // `int h = SSTVSET.m_TW/2;` (Main.cpp:3888) -- truncated threshold, and the wrap result is
        // truncated back into the int (Main.cpp:3889). Same shape as WrapSyncOffset above.
        var half = (int)(_effectiveSamplesPerLine / 2.0);
        if (relative > half)
        {
            relative = (int)(relative - _effectiveSamplesPerLine);
        }
        else if (relative < -half)
        {
            relative = (int)(relative + _effectiveSamplesPerLine);
        }

        // ultracode audit finding #10: capture the OLD samples-per-line before a correction below
        // can overwrite _effectiveSamplesPerLine. The accumulator reached (was >=) the OLD value,
        // not whatever a same-line correction just changed it to -- subtracting the NEW value
        // instead produces a one-time boundary jump of |old-new| samples on the very line a
        // correction commits (a bug this port introduced; legacy has no equivalent because its
        // own rate-change path rebases and re-decodes the whole image from an absolute sample
        // count, a retroactive-re-decode mechanism this port deliberately doesn't have -- see
        // SlantTracker's own doc comment).
        var completedLineSamples = _effectiveSamplesPerLine;

        if (_suppressNextSlantProcessLine)
        {
            _suppressNextSlantProcessLine = false;

            // Legacy's AutoStopJob() doesn't run AT ALL for this one line: Main.cpp:4190 gates the
            // call on `m_SyncPos != -1`, and KRFSClick set m_SyncPos = -1. So no history push, no
            // m_AutoStopACnt++/m_ASCurY++, no correction -- nothing.
            //
            // Also undo this line's own capture above: legacy's :4194 (`m_SyncRPos = m_SyncPos`)
            // propagates that same -1 across this boundary, so legacy has no valid m_SyncRPos to
            // ReSync from either. Load-bearing, not tidiness: this is the one line whose peak
            // straddles the jump AND whose skipped span never entered the _slantLineMaxEnvelope
            // comparison (see DrainPendingSkip), so the captured value is meaningless -- leaving it
            // set would let an immediate second ReSync click apply a bogus second skip from it.
            _lastLineSyncPeakPosition = null;
        }
        else
        {
            // Auto Sync (Main.cpp:3907-3961) runs here, ahead of the Auto-Slant regression split
            // below and regardless of _slantCorrectionsDisabledForRestOfImage -- that flag gates
            // Auto SLANT's own correction branch (Main.cpp:3968's !m_AutoSyncCount), not Auto
            // Sync's, which has its own independent gates. See TryAutoSync's own doc comment for
            // the full design, including its own RX buffer subsystem Phase 6b `isReplay` suppression.
            TryAutoSync(_mode!, isReplay);

            if (_slantCorrectionsDisabledForRestOfImage || !_autoSlantEnabled)
            {
                // m_AutoSyncCount (Main.cpp:3968), set by a successful ReSync and cleared only at
                // the next reception's start (:4994): the history/counter bookkeeping legacy runs
                // unconditionally keeps running, the whole correction branch does not. Deliberately
                // NOT `ProcessLine(relative)`-and-discard -- see ProcessLineHistoryOnly's own doc
                // comment (SlantTracker.cs) for what that would additionally (and wrongly) mutate.
                // !_autoSlantEnabled routes here too (KRSA->Checked false, Main.cpp:3968's own outer
                // condition on the whole 3968-4018 block) -- same "keep history, never commit" path,
                // not a separate skip. NOT "SlantPpm stays null" (an earlier version of this
                // comment wrongly claimed that, caught by a test actually asserting it and failing):
                // SlantTracker.DriftPpm reads 0.0 (non-null) from construction onward regardless of
                // this flag, since _currentSampleRate starts equal to _sampleRate
                // (SlantTracker.cs:80) -- what actually stays true is SlantPpm never MOVES from
                // that 0.0 default, since _currentSampleRate is only ever reassigned inside
                // ProcessLine's own commit path, which this branch never reaches. That exactly
                // matches legacy leaving SSTVSET.m_SampFreq untouched when the checkbox is off.
                _slantTracker!.ProcessLineHistoryOnly(relative);
            }
            else if (isReplay)
            {
                // RX buffer subsystem Phase 6b: legacy's own `m_ASDis`-suppressed replay re-feed
                // (Main.cpp:3989-4017) runs the SAME fit/baseline/average/bitmask logic a live commit
                // does -- only the final SSTVSET.m_SampFreq write is suppressed. ProcessLineSuppressed
                // is that exact path; unlike ProcessLine below, its return value is discarded by
                // design (see that method's own doc comment) -- nothing here may act on a
                // would-be-corrected rate during a suppressed pass.
                _slantTracker!.ProcessLineSuppressed(relative);
            }
            else
            {
                // Batch 2 chunk 2b round-2 fix: ONE local for legacy's ONE guard. Main.cpp:5597's
                // `if( (dp->m_StgBuf != NULL) || WaveStg.IsOpen() )` gates BOTH the post-commit
                // InitAutoStop (:5600, now SlantTracker's `hasStagingBuffer` parameter) AND the
                // retroactive replay loop (:5603-5612, this port's _pendingReplayRequested below) --
                // one shared local so the two uses can never disagree.
                var hasStagingBuffer = _rxLineStagingBuffer is not null;

                var correctedSampleRate = _slantTracker!.ProcessLine(relative, hasStagingBuffer);
                if (correctedSampleRate is not null)
                {
                    _effectiveSamplesPerLine = _mode!.LineDurationMs / 1000.0 * correctedSampleRate.Value;

                    // RX buffer subsystem Phase 6d: legacy's own trigger for RedrawSampFreq's replay
                    // pass is exactly this event (Main.cpp:4016's m_ReqSampChg=1, set right after
                    // SSTVSET.m_SampFreq is reassigned, drained one timer tick later at Main.cpp:3670-
                    // 3680 -- this port's own deferred-request precedent, see _pendingReplayRequested's
                    // own doc comment for why synchronous is unsafe here). Gated on _rxLineStagingBuffer
                    // being non-null (RxBufferMode.On or, as of Phase 7's decoder-wiring sub-piece,
                    // Extended too -- code-review fix, this comment previously said "On" only) --
                    // matches legacy's own UpdateSampFreq gate
                    // (`(dp->m_StgBuf != NULL) || WaveStg.IsOpen()`, Main.cpp:5597). No separate isReplay
                    // guard is needed here: this branch is only ever reached when isReplay is false (the
                    // enclosing if/else routes isReplay:true to ProcessLineSuppressed instead, a few
                    // lines up) -- so a replay pass's own suppressed re-feed can never reach this line
                    // and request another replay of itself.
                    if (hasStagingBuffer)
                    {
                        _pendingReplayRequested = true;
                        // RX buffer subsystem Phase 6d round-2: explicit user decision (2026-08-13) to
                        // NARROW the once-per-image latch below to require at least one real commit --
                        // see that trigger's own doc comment for the full reasoning (a guaranteed
                        // visible cost, once wired to fire by default on every decode, that the
                        // ORIGINAL Phase 6 plan-review's literal-legacy-fidelity framing didn't
                        // anticipate). This flag records that a commit has happened.
                        _anyCorrectionCommittedThisImage = true;
                    }

                    // Deliberately does NOT reset Auto Sync's own detection state here -- an
                    // earlier draft of this port called ResetAutoSyncDetectionState() on every
                    // commit "for consistency" with SlantTracker.Reset()'s own already-shipped
                    // behavior (which DOES reset on every commit, ultracode audit finding #9).
                    // Empirically wrong, caught by this feature's own test suite: SlantTracker
                    // commits corrections often enough under sustained drift that Auto Sync's own
                    // 8-line warmup and 16-entry ring buffer never survived long enough to
                    // accumulate anything, making the whole feature untriggerable in exactly the
                    // sustained-drift scenario it exists to help with.
                    //
                    // Code-level review correction: an earlier version of this comment additionally
                    // claimed this was "the MORE legacy-faithful choice too" because legacy's own
                    // InitAutoStop-after-commit call is "proven dead code without the not-built
                    // RX-buffer-replay feature" -- that specific claim was WRONG. sys.m_UseRxBuff
                    // defaults to 1 (Main.cpp:899) and OpenCloseRxBuff allocates m_StgBuf for
                    // exactly that value (sstv.cpp:1630-1639), so UpdateSampFreq's own gate
                    // (Main.cpp:5597) IS satisfied by default and InitAutoStop DOES run after every
                    // commit in real legacy. See ResetAutoSyncDetectionState's own doc comment for
                    // the real reason not to copy that call: legacy immediately REPLAYS every
                    // buffered line back through DrawSSTV afterward (Main.cpp:5603-5612), rebuilding
                    // its own state rather than losing it -- RX buffer subsystem Phase 6 is what
                    // finally builds that replay mechanism (see ResetAutoSyncDetectionState's own
                    // doc comment for the current, Phase-6-aware status of this reasoning).
                }
            }
        }

        _slantIdealSamplesSoFarInLine -= completedLineSamples; // carry remainder against the OLD samples-per-line -- keeps line boundaries from drifting
        _slantLineMaxEnvelope = double.NegativeInfinity;
        _slantLineMinEnvelope = double.PositiveInfinity;
        _slantLineEnvelopeSeeded = false;
        _slantLinePeakPosition = 0;
        return true;
    }

    /// <summary>RX buffer subsystem Phase 6c -- the replay engine itself. Port of
    /// `UpdateSampFreq`/`RedrawSSTV`'s own reset-then-rebuild-then-redraw shape (`Main.cpp:5586-5629`):
    /// given 6a's origin (<see cref="ReplayOriginCalculator.ComputeOrigin"/>) + the current corrected
    /// stride (<see cref="_effectiveSamplesPerLine"/>) + 6b's reusable core
    /// (<see cref="ProcessSlantTrackingSample"/>), walks the flat staged stream one FULLY-staged
    /// transmission line at a time, redraws each row from the staged buffer, and fires ONE
    /// <see cref="LineDecoded"/> event per pass (the last redrawn row -- see this method's own tail for
    /// the exact batching rule; buffered-replay fix, §15 item 2) -- retroactively re-decoding the entire
    /// image received so far at the corrected rate, matching legacy's own `RedrawSampFreq` semantics (see
    /// <see cref="SlantTracker"/>'s own updated class doc comment: this is what finally makes that
    /// comment's "applies going forward only" caveat obsolete).
    ///
    /// <b>Reachability, RX buffer subsystem Phase 6d</b>: called from exactly one place --
    /// <see cref="TryProcessBuffer"/>'s own per-line loop, immediately after each line's own
    /// <see cref="ApplySlantTracking"/> call, draining <c>_pendingReplayRequested</c> at a
    /// DECODED-LINE boundary (round-1 code-review fix: NOT the top of <see cref="PushSamples"/>, a
    /// caller-chunk boundary -- see that field's own doc comment for the real bug this correction
    /// fixes). Never called synchronously from inside <see cref="ApplySlantTracking"/>'s own
    /// per-sample call stack -- see <c>_pendingReplayRequested</c>'s own doc comment for the
    /// reentrancy hazard that deferred-request shape exists to avoid. <see cref="PerformReplayForTests"/>
    /// remains available for direct, single-call exercising in tests. Both trigger sites (Auto-Slant's
    /// own automatic commit and manual Correct Slant) require cumulative staged transmission lines >= 16
    /// before this method can ever run, which structurally guarantees a batched replay
    /// <see cref="LineDecoded"/> event can never be an image's first or second such event -- a stated
    /// invariant relied on by <c>ReceiveHistoryRecorder</c>/<c>ReceivedImageBuffer</c>/the RX loopback
    /// self-test in `SstvSessionService`, each of which derives its own step size from only the first two
    /// events.
    ///
    /// <b>Round-2 code-review redesign: the live per-line slant-tracking accumulator is reset once, at
    /// the top, and NEVER restored.</b> An earlier version of this method saved a snapshot of
    /// (<see cref="_slantIdealSamplesSoFarInLine"/>, <see cref="_slantLineEnvelopeSeeded"/>,
    /// <see cref="_slantLineMaxEnvelope"/>/<see cref="_slantLineMinEnvelope"/>/
    /// <see cref="_slantLinePeakPosition"/>, <see cref="_lastLineSyncPeakPosition"/>,
    /// <see cref="_suppressNextSlantProcessLine"/>) before replay's own walk and restored it afterward,
    /// reasoning that the live decode's own in-flight line measurement needed to survive untouched.
    /// That reasoning no longer applies now that the sample-cursor re-anchor below (see this method's
    /// own "sample-cursor re-anchor" paragraph) ALWAYS moves <see cref="_consumedSamples"/> to a
    /// destination-coordinate line boundary once this method returns -- the pre-replay snapshot would
    /// describe a line the live decode is no longer measuring towards. Reset to a clean slate once (so
    /// replay's own walk isn't biased by whatever partial live measurement preceded it, including
    /// resolving the auditor's own 6b round-1 off-scope note: <c>_suppressNextSlantProcessLine</c> is
    /// reset to <see langword="false"/> here, so a stale live-side skip-suppression flag can never
    /// silently eat replay's own first line) and then simply let it be -- live decode's own subsequent
    /// calls continue accumulating from wherever replay's own walk naturally left it, exactly the way
    /// it already continues across ordinary line boundaries during ordinary live decode.
    ///
    /// Deliberately does NOT reset/restore <see cref="_slantTracker"/>'s own internal state or the
    /// Auto-Sync detection-window fields <see cref="ResetAutoSyncDetectionState"/> resets -- those are
    /// exactly the state legacy's own `InitAutoStop`-then-suppressed-refeed REBUILDS (not loses) on
    /// every replay pass; the whole point is that they end up reflecting replay's own rebuild and keep
    /// serving live decode from there on, matching `ResetAutoSyncDetectionState`'s own doc comment.
    ///
    /// <b>Round-2 code-review redesign: the Auto-Sync/Auto-Slant re-feed is now ONE continuous
    /// per-sample walk over the whole valid staged range, decoupled from the per-ROW pixel-drawing
    /// loop.</b> An earlier version fed each pixel row's own staged span separately, which (round-2
    /// finding) introduced a systematic offset, up to `origin` samples, between the sync-tracking
    /// accumulator's own internal line-boundary detection and the pixel-row boundaries whenever `origin`
    /// is positive (reachable: the whole Scottie family's own `AdjustSyncPos` wrap,
    /// `ReplayOriginCalculator.cs:66-69`, forces a positive origin by construction) -- every replayed
    /// row's own partial re-feed (clamped to what's staged) would under-feed the accumulator relative to
    /// a full line, shifting where IT thinks each "line" completes away from where the pixel rows
    /// actually are. A single continuous walk matches <see cref="ApplySlantTracking"/>'s own live
    /// per-sample loop shape exactly (one walk, letting <see cref="ProcessSlantTrackingSample"/>'s own
    /// internal accumulator discover line boundaries on its own terms) and sidesteps the issue entirely.
    ///
    /// <b>Buffered-replay fix (§15 item 2, `robust-giggling-codd.md`): the sample cursor
    /// (<see cref="_consumedSamples"/>/<see cref="_idealLineStartSample"/>/<see cref="_nextLine"/>) is
    /// re-anchored BACKWARD, not forward, and the staging buffer is never truncated.</b> Supersedes the
    /// prior forward-jump-then-truncate design entirely (full history and the reseed-formula derivation
    /// live in the plan doc, not restated here). That design had two coupled defects: (1) it jumped the
    /// live cursor FORWARD past the corrected row (`resumeLine`) to the NEXT row's start on every pass,
    /// sacrificing that row's own redraw every time; (2) it truncated <see cref="_rxLineStagingBuffer"/>
    /// after every pass, so a SECOND pass could only re-correct rows staged since the first, never rows
    /// from earlier in the reception -- a later, better correction could never fix rows an earlier one
    /// had already drawn wrong (confirmed common against real over-the-air captures: most that trigger
    /// any correction trigger 2 or more).
    ///
    /// The fix instead snaps the live cursor BACKWARD to `resumeLine`'s own destination-coordinate row
    /// start (`Math.Max(0, resumeLine)`, clamped against the RAW floor <see cref="_bufferBase"/> --
    /// falling forward to the OLD forward landing-point formula, touching only the landing point, for
    /// the rare case a manual Correct Slant's own uncapped backward jump would violate it) and leaves
    /// <see cref="_slantProcessedUpTo"/>/<see cref="_rxBufferAnchorSample"/> untouched -- the staging
    /// buffer already holds this data (never truncated), so the normal live per-line loop simply redraws
    /// it from data already on hand once <see cref="_consumedSamples"/> naturally advances back past the
    /// frozen <see cref="_slantProcessedUpTo"/>. No row is ever sacrificed, and every pass's redraw walk
    /// above covers the WHOLE staged reception from row 0, not just what's staged since the last pass --
    /// matching legacy's own `UpdateSampFreq` (`Main.cpp:5603-5612`), which never truncates `m_StgBuf`
    /// either. <see cref="_rxBufferBaseTransmissionLine"/> therefore stays 0 for a whole image's entire
    /// reception now (set once at a fresh lock) -- every read site of it keeps working unchanged, just
    /// with a permanently-0 offset.
    ///
    /// <b>Investigated as a possible Robot-family chroma-bleed bug (2026-08-14), closed as NOT a bug</b>:
    /// <see cref="RobotScanlineDecoder"/> (confirmed the only <see cref="IScanlineDecoder"/>
    /// implementation with cross-line instance state, per the whole-subsystem review's own sweep of all
    /// five -- <see cref="YCbCrSequentialScanlineDecoder"/>, <see cref="YCbCrLinePairedScanlineDecoder"/>,
    /// <see cref="RgbSequentialScanlineDecoder"/>, and <see cref="MonoAveragedPairedScanlineDecoder"/> all
    /// allocate fresh per-call state) caches the PREVIOUS line's other chroma channel across
    /// `DecodeLine` calls, matching legacy's own `m_D36[2][320]` cross-line state -- Robot 36's real,
    /// by-design vertical chroma subsampling (one channel scanned per row, the other borrowed from the
    /// row before it), not a port defect. An earlier pass here wrongly concluded this method's (then)
    /// "sacrifice one row per pass" design corrupts MULTIPLE rows per replay pass, based on a two-pass
    /// reproduction that turned out to be confounded: the test image (`CreateRowIdentityTestImage`,
    /// deliberately adjacent-rows-differ-wildly to stress OTHER row-misalignment bugs) is a pathological,
    /// invalid fidelity target for Robot 36 specifically -- its own inherent per-row chroma-subsampling
    /// error against that image (large, by design) was mistaken for replay-caused corruption. Empirically
    /// closed via an auditor-derived arithmetic model (predicting each row's delta from "own channel +
    /// neighbor's other channel," matching measured values to within ~3 units) AND a direct no-replay
    /// control (`RxBufferMode.Off`, same image, same scenario) that reproduced the SAME per-row deltas
    /// with zero replay activity at all. The only real, replay-attributable artifact was a single stale
    /// seed row at each redraw window's start, always at image row 0 (legacy-equivalent, since legacy's
    /// staging buffer is never truncated either) -- smaller than the mode's own inherent per-row error on
    /// this stress image. Not a Tier-0 item (`spec/14-roadmap.md`) -- a legacy-faithful characteristic,
    /// not a bug. Only Robot 36 uses this decoder; Robot 72 (`ColorEncoding.YCbCrSequential`,
    /// <see cref="YCbCrSequentialScanlineDecoder"/>) is stateless and entirely unaffected.
    ///
    /// The genuinely separate, real, mode-independent divergence this investigation flagged as future
    /// follow-up work -- legacy never loses a row on replay, the port's per-line `DecodeLine` granularity
    /// used to -- is RESOLVED by the buffered-replay fix above: the backward snap no longer skips
    /// `resumeLine`'s own redraw, so no row is sacrificed on any pass, for any mode.</summary>
    // D0-audit round-10 structural fix: return type is now bool, not void. false means NOTHING in
    // this method ran (all 3 early exits below sit before any of this method's own mutations) --
    // the caller (TryCorrectSlantAndApply, for the manual path) uses this to know whether a
    // just-committed TryCorrectSlant() result must be rolled back, matching legacy's own
    // commit-or-revert shape (Main.cpp:5415-5423). true covers both a normal full pass AND
    // checkpoint 2's bail below (that bail runs AFTER this method's own resets have already fired --
    // deliberately not rolled back, a pre-existing, already-accepted design this fix does not
    // change, see that checkpoint's own doc comment). The automatic _pendingReplayRequested caller
    // needs no change and discards this return value -- ProcessSlantTrackingSample's own commit tail
    // already normalizes the accumulator/envelope fields at its own line boundary, independent of
    // this method's outcome (round-2 plan-review, re-derived from source).
    private bool PerformReplay()
    {
        if (_rxLineStagingBuffer is null || _mode is null || _slantTracker is null || _lineDecoder is null || _pixels is null)
        {
            return false; // not in a state replay applies to (AVT/pre-lock/RxBufferMode.Off -- matches ApplySlantTracking's own guard; code-review fix: Extended DOES replay as of Phase 7's decoder-wiring sub-piece, via this same is-null gate)
        }

        var mode = _mode;
        var lineDecoder = _lineDecoder;
        var pixels = _pixels;
        var stagingBuffer = _rxLineStagingBuffer;

        // spec/18-path-to-1.0.md High item 6, checkpoint 1 of 2 (see the second checkpoint below,
        // right after ComputeOrigin, for why one check here isn't enough on its own). A disk write
        // failure (RxBufferMode.Extended only -- always false for RAM, RxLineStagingBuffer.cs's own
        // hardcoded HasWriteFailed) means EnsureSnapshot/ReadSnapshot CAN zero-fill the ENTIRE
        // staged snapshot on the next read (RxDiskLineStagingBuffer.cs's own doc comment, only when
        // the read itself actually fails/comes up short -- not on every latch), not just whatever's
        // actually missing -- replaying against that would silently redraw an already-correctly-
        // decoded portion of the image with zeros. Bailing here, before any of the AutoSync/
        // SlantTracker resets below, leaves the decoder exactly as if this replay request had never
        // fired; live (non-replay) decoding of new lines is unaffected regardless (Extended capture
        // has already stopped admitting new lines by construction once HasWriteFailed latches --
        // TryAppendLine's own guard). Code-review correction: HasWriteFailed does NOT reset on a
        // fresh lock -- InitializeSlant only calls Clear() (RxLineStagingBuffer's own Clear/
        // TryAppendLine contract), which never clears this flag, and Clear()'s own failure path
        // deliberately leaves it set; a genuinely fresh RxDiskLineStagingBuffer instance only exists
        // after a RestartableSstvDecoder.Swap() (the ~12h restart interval).
        // So once latched, replay and Correct Slant (see TryCorrectSlant's own guard) are silently
        // disabled for the rest of THIS DECODER INSTANCE's lifetime -- across every subsequent
        // reception, not just this one. This is exactly where these guards earn their keep the most:
        // without them, the very NEXT reception's first replay would redraw from the PREVIOUS
        // reception's stale/zeroed staging data (Clear()'s own failure path leaves _count/
        // _lineBoundaries non-zero against a file that was never actually truncated).
        if (stagingBuffer.HasWriteFailed)
        {
            return false;
        }

        var stagedSampleCount = stagingBuffer.Count;
        if (stagedSampleCount == 0)
        {
            return false; // nothing staged yet
        }

        // Reset-then-rebuild, immediately before every replay pass (Main.cpp:5600ish's InitAutoStop
        // call via UpdateSampFreq, before the replay loop) -- both Auto Sync's own detection window and
        // Auto Slant's own baseline, matching legacy exactly (see ResetAutoSyncDetectionState's and
        // SlantTracker.ResetBaseline's own doc comments).
        //
        // Buffered-replay fix (§15 item 2): this used to branch on _rxBufferBaseTransmissionLine == 0
        // (true only for a pass's FIRST call, before this method's own truncate-on-jump tail re-based
        // it away from 0) -- legacy's own InitAutoStop-then-replay shape (Main.cpp:5600-5612) always
        // rebuilds Auto Sync's own observation counter/history to the FULL running staged-line count,
        // because legacy's staging buffer is NEVER truncated (`m_wStgLine` is cumulative from lock), and
        // resetting the full window against only a handful of lines (what a truncated buffer left to
        // refill it) permanently starved TryAutoSync's own `_autoSyncObservationCount >= 8`/Auto Stop's
        // `_autoStopCnt >= 8` gates for the rest of the image on every pass after the first. Now that the
        // staging buffer is never truncated, _rxBufferBaseTransmissionLine stays 0 for a whole image's
        // entire reception (see this method's own doc comment), so this condition is unconditionally
        // true -- collapsed to a single unconditional call, matching legacy's own unconditional
        // InitAutoStop-then-replay call exactly, with no truncated-buffer case left to special-case.
        ResetAutoSyncDetectionState();

        _slantTracker.ResetBaseline();

        // Clean slate for replay's own per-sample walk -- see this method's own doc comment for why
        // this is a one-time reset, never restored. _slantIdealSamplesSoFarInLine is NOT zeroed here --
        // it needs to be seeded relative to `origin`, not zeroed, computed further below (the
        // `firstFedDest`-based assignment, once `origin` is known), and that assignment overwrites the
        // modulo below on every path that reaches it. D0-audit round-10 structural fix: the modulo IS
        // placed here (not omitted, as an earlier version of this method did) so it runs before
        // checkpoint 2 below -- on that checkpoint's own bail (after this point, before the
        // `firstFedDest` seed), it is the only thing keeping the accumulator valid against the
        // just-corrected _effectiveSamplesPerLine, since TryCorrectSlant's caller
        // (TryCorrectSlantAndApply) treats a checkpoint-2 bail as a committed replay, not a revert --
        // matching this method's own already-accepted "resets already ran, not rolled back" design at
        // that checkpoint, see its own doc comment. A no-op on every other path (overwritten by the
        // `firstFedDest` seed further below, then again by the cursor-reanchor seed near this method's
        // own tail).
        _slantIdealSamplesSoFarInLine %= _effectiveSamplesPerLine;
        _slantLineEnvelopeSeeded = false;
        _slantLineMaxEnvelope = double.NegativeInfinity;
        _slantLineMinEnvelope = double.PositiveInfinity;
        _slantLinePeakPosition = 0;
        _lastLineSyncPeakPosition = null;
        _suppressNextSlantProcessLine = false;

        var correctedLineWidthSamples = _effectiveSamplesPerLine; // the CURRENT, just-corrected stride -- the ONE uniform stride this whole pass applies, matching ReplayOriginCalculator.ComputeOrigin's own `correctedLineWidthSamples` contract
        var effectiveSampleRate = correctedLineWidthSamples / (mode.LineDurationMs / 1000.0);
        var ofpSamples = ComputeUntruncatedSyncPeakOffsetSamples(mode, effectiveSampleRate);

        var foldedLineCount = Math.Min(stagingBuffer.LineCount, 32); // Main.cpp:5504's own 32-line fold cap
        var foldedSampleCount = stagingBuffer.SampleCountThroughLine(foldedLineCount);

        var hilbertGroupDelayCorrection = _demodType == DemodType.Hilbert ? _demodulator.HalfTap / 4 : 0;

        var origin = ReplayOriginCalculator.ComputeOrigin(
            stagingBuffer.SyncEnvelopeAt,
            foldedSampleCount,
            correctedLineWidthSamples,
            // Round-5 code-review fix: legacy's own `wStgLineCount` parameter (AdjustPosition's
            // Martin-M2/SC2-60 `wStgLineCount < 20` branch) is `dp->m_wStgLine`, a CUMULATIVE count
            // never reset mid-reception (Main.cpp:5451-5456). Buffered-replay fix (§15 item 2):
            // `stagingBuffer.LineCount` is now ITSELF cumulative for the whole reception too (the
            // staging buffer is never truncated/re-based anymore), so `_rxBufferBaseTransmissionLine`
            // (always 0 now) is a no-op addition here -- kept rather than removed, matching this
            // method's other read sites, so the expression stays correct if a future change ever
            // re-introduces a re-base of that field for a different reason. Both are
            // RowsPerTransmissionLine == 1 families (Martin/SC2 are never paired-channel modes), so the
            // units already match with no conversion needed.
            _rxBufferBaseTransmissionLine + stagingBuffer.LineCount,
            ofpSamples,
            mode,
            _demodType,
            hilbertGroupDelayCorrection,
            effectiveSampleRate);
        _lastReplayOriginForTests = origin;

        // Checkpoint 2 of 2 (see the entry guard above for checkpoint 1 and the full rationale).
        // ComputeOrigin above is this pass's own FIRST read of the staging buffer (via the
        // SyncEnvelopeAt delegate) -- the first read after any new writes is exactly when
        // RxDiskLineStagingBuffer.EnsureSnapshot/ReadSnapshot can latch HasWriteFailed for the FIRST
        // time (a drain/flush failure, or a short file caught mid-read), i.e. after checkpoint 1
        // already passed with the flag still false -- the EnsureSnapshot/ReadSnapshot latch itself is
        // synchronous within this read, so checkpoint 2 doesn't depend on any timing window to catch
        // THAT case. Round-2 code-review correction: the flag can ALSO latch from the background
        // writer task at any moment regardless of any read (HasWriteFailed is `volatile` precisely
        // because of this -- RxDiskLineStagingBuffer.cs's own ConsumeAsync catch blocks), so a real
        // write error landing between checkpoint 1 and checkpoint 2 is a genuine possibility, not
        // ruled out -- checkpoint 2 catches that case too, which is what makes it load-bearing beyond
        // just the read-triggered case. Either way, once EnsureSnapshot has run once for this
        // generation its result is cached (invalidated only by the next TryAppendLine/Clear, neither
        // of which runs mid-pass), so the redraw loop further down is safe reading that same cached
        // snapshot regardless of which path set the flag. Placed HERE specifically (not lower, inside
        // the loops below): any later point either writes persistent decoder state from a possibly-
        // corrupted `origin` (_slantIdealSamplesSoFarInLine, right below) or mutates _pixels (the
        // redraw loop further down) -- this is the last point before either happens. `origin` itself
        // may have been computed from zeroed data in this scenario; harmless, since it's discarded
        // unused below. Unlike checkpoint 1, the AutoSync/SlantTracker/_slantLine* resets earlier in
        // this method (D0-audit round-8: dropped a stale in-file line citation here) have ALREADY
        // run by the time this checkpoint can fire -- deliberately not rolled back (see checkpoint
        // 1's own "accepted note" on this trade-off in the plan doc);
        // don't read checkpoint 1's "exactly as if this replay request had never fired" as applying
        // to a bail that happens here instead.
        if (stagingBuffer.HasWriteFailed)
        {
            return true; // D0-audit round-10 structural fix: this bail's own resets already ran (see this checkpoint's own doc comment above) -- treated as a committed replay, not reverted, matching the pre-existing accepted design.
        }

        // Auto-Sync/Auto-Slant re-feed: ONE continuous per-sample walk over the whole valid staged
        // range, suppressed (isReplay: true) -- matches legacy's own per-sample AutoStopJob re-feed
        // during replay (Main.cpp:4189-4202, m_ASDis=1-bracketed) and, per round-1 code review, its
        // `if(n<0) continue` (Main.cpp:4146) skipping AutoStopJob too for samples mapping before row 0
        // -- `Math.Max(0, -origin)` is that same skip, expressed directly in staged-index terms (a
        // negative destination coordinate n=origin+i means i<-origin). Runs through the FULL staged
        // extent regardless of pixel-row/ImageHeight bounds, matching legacy's own lack of
        // ImageHeight-awareness in AutoStopJob (round-1 code review, point 6). See this method's own
        // doc comment for why this is decoupled from the pixel-drawing loop below, not partitioned per
        // row the way an earlier version of this method did.
        var feedFrom = Math.Max(0, -origin);

        // Round-3 code-review blocker fix: seed the accumulator to the FIRST fed sample's own phase
        // within a line, not zero. Legacy's replay walk derives its line boundary from `fmod(n, m_TW)`
        // on the DESTINATION coordinate `n` directly (Main.cpp:4147), which is locked to the
        // destination grid (`n ≡ 0 mod TW`) regardless of `origin`'s value. This port's accumulator
        // instead COUNTS fed samples from zero -- if the first fed sample's own destination coordinate
        // (`firstFedDest`) isn't itself a multiple of the stride (true whenever `origin > 0`, since the
        // walk then starts at destination `origin`, not `0`), a bare zero seed puts every replayed
        // line's own measured boundary out of phase with the pixel-row grid by `origin mod stride` --
        // silently biasing every re-fed sync position by a constant amount for the whole pass.
        var firstFedDest = (double)Math.Max(origin, 0);
        _slantIdealSamplesSoFarInLine = firstFedDest - Math.Floor(firstFedDest / correctedLineWidthSamples) * correctedLineWidthSamples;

        for (var i = feedFrom; i < stagedSampleCount; i++)
        {
            ProcessSlantTrackingSample(stagingBuffer.SyncEnvelopeAt(i), isReplay: true);
        }

        // Round-3 plan-review pin: the transmission-line bound is `(origin + stagedSampleCount) /
        // correctedStride`, matching legacy's real `m_rBase = origin + wStgLine*m_WD`
        // (Main.cpp:5602+:5612) -- origin is SIGNED and ADDED, not a separate skew subtracted
        // elsewhere. Only FULLY-staged transmission lines are ever replayed (never a line whose own
        // end extends past stagedSampleCount).
        var fullyStagedTransmissionLines = (int)((origin + stagedSampleCount) / correctedLineWidthSamples);
        var roundedSampleRate = (int)Math.Round(effectiveSampleRate); // round-1 code-review nit: one rounding, reused for both GetKsbSamples and DecodeLine (previously GetKsbSamples got the unrounded double, a gratuitous live/replay divergence)

        // Buffered-replay fix (§15 item 2), LineDecoded batching: this loop used to raise LineDecoded
        // once per redrawn row, which could fire dozens/hundreds of events for a single replay pass over
        // a large reception. Now raises exactly ONE event for the whole pass, carrying the LAST row that
        // actually passed the bounds guard below (NOT fullyStagedTransmissionLines - 1, which can
        // overcount past the image's real height) -- null if this pass redraws zero rows. Structurally
        // never the image's first or second LineDecoded event overall (see this method's own doc
        // comment's Reachability paragraph), which is what makes this safe for the three real consumers
        // (ReceiveHistoryRecorder, ReceivedImageBuffer, SstvSessionService's RX loopback self-test) that
        // each derive their own step size from only the first two such events, one-shot, never re-learned.
        int? lastRedrawnBitmapRow = null;
        for (var transmissionLineIndex = 0; transmissionLineIndex < fullyStagedTransmissionLines; transmissionLineIndex++)
        {
            // Round-1 code-review correction: legacy's own row membership is `y = int(n/m_TW)`
            // (Main.cpp:4148) -- for integer sample index n, the set of n's with `int(n/TW)==y` is
            // `[ceil(y*TW), ceil((y+1)*TW))`, NOT `[floor(y*TW), floor((y+1)*TW))`. A bare `(int)`
            // cast truncates toward zero (floor, for non-negative values) and silently drops the
            // LAST sample of every row whenever `correctedLineWidthSamples` has a nonzero fractional
            // part.
            var lineStartDest = (int)Math.Ceiling(transmissionLineIndex * correctedLineWidthSamples);
            var lineEndDestExclusive = (int)Math.Ceiling((transmissionLineIndex + 1) * correctedLineWidthSamples);
            var lineStartStaged = lineStartDest - origin;

            // Buffered-replay fix (§15 item 2): _rxBufferBaseTransmissionLine now stays 0 for a whole
            // reception (the staging buffer is never truncated/re-based anymore, see this method's own
            // doc comment) -- staged index 0 IS always image row 0 now, matching legacy's own
            // never-truncated m_StgBuf exactly. Kept in this expression rather than removed, so it stays
            // correct if a future change ever re-introduces a re-base of that field for a different
            // reason (set once, at a fresh lock, per InitializeSlant).
            var bitmapRow = (_rxBufferBaseTransmissionLine + transmissionLineIndex) * lineDecoder.RowsPerTransmissionLine;
            if (lineStartStaged < 0 || bitmapRow >= mode.ImageHeight)
            {
                // This line's own start predates the staged buffer (positive origin -- see this
                // method's own doc comment; a small, documented divergence: legacy's per-sample loop
                // can partially draw such a line, this port's per-row `DecodeLine` cannot) or is past
                // the end of the image (more got staged/corrected than the image actually needs) -- no
                // pixel redraw, no LineDecoded. The bookkeeping re-feed above already covered this
                // line's own samples regardless.
                continue;
            }

            // Peek-past-staged-extent, defined not inherited (RX buffer plan's own flagged risk):
            // legacy's GetPictureLevel peek at the final staged line reads past the staged extent
            // into uninitialized memory -- undefined behavior legacy happens to get away with. This
            // port instead relies on PixelSampleReader.ReadPeakPicked's own existing
            // floor-at-luminanceMinHz logic against this line's own theoretical end -- provably never
            // past the staged buffer's real extent given the fully-staged-lines-only loop bound above.
            var reader = new PixelSampleReader(
                index => stagingBuffer.DemodulatedAt(index - origin),
                SstvModeRegistry.GetKsbSamples(mode, roundedSampleRate),
                lineEndDestExclusive,
                mode.LuminanceMinHz,
                SstvModeRegistry.NeverPeakPicks(mode) || _rxBufferMode == RxBufferMode.Extended);

            lineDecoder.DecodeLine(mode, roundedSampleRate, lineStartDest, bitmapRow, reader, pixels);
            lastRedrawnBitmapRow = bitmapRow;
        }

        if (lastRedrawnBitmapRow is { } finalBitmapRow)
        {
            RaiseSubscribers(LineDecoded, new DecodedImageUpdate(finalBitmapRow, new MutableImageSource(mode.ImageWidth, mode.ImageHeight, pixels)));
        }

        // Sample-cursor re-anchor: BACKWARD SNAP (buffered-replay fix, §15 item 2,
        // robust-giggling-codd.md) -- supersedes the old forward-jump-then-truncate design entirely, see
        // this method's own doc comment above and the plan doc for the full history.
        //
        // resumeDest is the LIVE cursor's own current position, expressed in the SAME
        // destination-coordinate space `origin`/the replay loop above use -- NOT origin+stagedSampleCount,
        // which only reflects how far STAGING has gotten (it can lag the live cursor by up to one
        // not-yet-flushed line's worth of samples).
        // Code-review nit: the plan's own sign note (its reseed-formula section) covers
        // slantProcessedDest's possible negativity below, but not resumeDest's -- stated here for the
        // same reason: resumeDest CAN be negative for a sufficiently negative `origin` on an early pass,
        // which would make `Math.Floor(resumeDest / correctedLineWidthSamples)` land below 0 -- exactly
        // what the `Math.Max(0, ...)` clamp catches. Unreachable in practice given the >= 16-staged-line
        // entry gate both replay triggers share (resumeDest is then always at least ~16 strides above a
        // sub-stride-magnitude `origin`), and harmless even if it were reached: landedRow would clamp to
        // 0 and the backward target would land at or after _consumedSamples (a forward move, not
        // backward) -- still safe, since _slantProcessedUpTo stays untouched either way, so
        // ApplySlantTracking still stages whatever span this skips over, leaving no hole.
        var resumeDest = origin + (_consumedSamples - _rxBufferAnchorSample);
        var resumeLine = Math.Max(0, (int)Math.Floor(resumeDest / correctedLineWidthSamples));
        var backwardRowStartDest = (int)Math.Ceiling(resumeLine * correctedLineWidthSamples);

        // The fixed coordinate map inverted (see _rxBufferAnchorSample's own doc comment: d(raw) =
        // origin + (raw - anchor), so raw = anchor + (d - origin)) -- valid here because
        // _rxBufferAnchorSample/origin are both left untouched by this pass (see below).
        var backwardTargetConsumedSamples = _rxBufferAnchorSample + (backwardRowStartDest - origin);

        int newConsumedSamples;
        int landedRow;
        if (backwardTargetConsumedSamples < (BufferBaseOverrideForTests ?? _bufferBase))
        {
            // Raw floor violated -- TrimBuffers' own retention margin for this (see that method's own
            // watermark term) only covers one corrected stride of backward snap; this is most likely a
            // manual Correct Slant correction, whose own 5-iteration search has no upper-stride clamp on
            // how far back it can land. Falls forward to the OLD forward-jump landing-point formula for
            // this one narrow case ONLY -- touches ONLY the landing point
            // (_idealLineStartSample/_consumedSamples/_nextLine below), never
            // _slantProcessedUpTo/_rxBufferAnchorSample/the staging buffer, which stay identical to the
            // normal backward branch in every other respect. Do NOT set _slantProcessedUpTo =
            // _consumedSamples here (today's retired forward-jump tail did) -- leaving
            // _slantProcessedUpTo untouched means ApplySlantTracking's own catch-up loop will naturally
            // walk and stage the forward-jumped span once live decode overtakes it, so unlike today's old
            // tail, this branch leaves no un-staged hole.
            // Revert-safety invariant (TryCorrectSlantAndApply's own doc comment): this branch sits
            // well after both of PerformReplay's own early-exit checkpoints (the only `return false`
            // paths), inside a run that has already mutated state and always reaches `return true` at
            // the bottom -- so it never risks returning false after mutating, which would signal a
            // revert TryCorrectSlantAndApply cannot actually perform for these fields. A hard
            // constraint on this branch, not an incidental fact: never add an early return here.
            landedRow = resumeLine + 1;
            var forwardRowStartDest = (int)Math.Ceiling(landedRow * correctedLineWidthSamples);
            newConsumedSamples = _rxBufferAnchorSample + (forwardRowStartDest - origin);
            _rawFloorClampForwardCountForTests++;
        }
        else
        {
            landedRow = resumeLine;
            newConsumedSamples = backwardTargetConsumedSamples;
        }

        _idealLineStartSample = newConsumedSamples;
        _consumedSamples = newConsumedSamples;

        // _slantProcessedUpTo/_rxBufferAnchorSample are DELIBERATELY left untouched in BOTH branches --
        // the core of the backward-snap design. The staging buffer already holds this data (never
        // truncated, see below), so the normal live per-line loop redraws it from data already on hand
        // once _consumedSamples naturally advances back past the frozen _slantProcessedUpTo -- no
        // re-feed, no double-count, no hole.
        _nextLine = (_rxBufferBaseTransmissionLine + landedRow) * lineDecoder.RowsPerTransmissionLine;

        // Reseed formula (robust-giggling-codd.md, restated explicitly after round 3 found a prior
        // revision omitted it): anchors to _slantProcessedUpTo (the FROZEN tracking cursor), NOT the
        // snapped-back _consumedSamples -- the NEXT ProcessSlantTrackingSample call (once live decode
        // naturally advances _consumedSamples back past _slantProcessedUpTo) processes the sample AT
        // _slantProcessedUpTo, so the accumulator's phase must reflect THAT cursor's own
        // destination-coordinate position, not the snap target (a purely decode-side concept unrelated to
        // the tracker's own progress). Anchoring to the wrong cursor would leave the sync-envelope line
        // grid permanently offset from the pixel grid by _slantProcessedUpTo - _consumedSamples, biasing
        // every subsequent line's measured sync position. Can be negative when origin < 0 on an early
        // pass -- Math.Floor still produces a valid [0, correctedLineWidthSamples) residue for a negative
        // input, so nothing breaks; this is relied on, not guarded against (unlike firstFedDest's own
        // Math.Max(origin, 0) clamp above), since no guard is needed here.
        var slantProcessedDest = origin + (_slantProcessedUpTo - _rxBufferAnchorSample);
        _slantIdealSamplesSoFarInLine = slantProcessedDest
            - Math.Floor(slantProcessedDest / correctedLineWidthSamples) * correctedLineWidthSamples;
        _slantLineEnvelopeSeeded = false;
        _slantLineMaxEnvelope = double.NegativeInfinity;
        _slantLineMinEnvelope = double.PositiveInfinity;
        _slantLinePeakPosition = 0;

        // Both existing precedents that set _suppressNextSlantProcessLine = true
        // (ApplySyncCorrection/ApplyNotchDisableShift) pair it with _lastLineSyncPeakPosition = null, and
        // the suppression branch itself reads both together -- without this, a manual ReSync landing in
        // the window before the first post-snap line completes would read a stale peak position (the LAST
        // REPLAYED line's own peak, written during the re-feed walk above) as if it were current. This
        // also excludes the first post-snap line's own observation from Auto-Sync, since that line's grid
        // may still be settling around the snap boundary.
        _suppressNextSlantProcessLine = true;
        _lastLineSyncPeakPosition = null;

        // Step B (buffered-replay fix): the staging buffer, its in-flight per-line capture accumulators,
        // and _rxBufferAnchorSample/_rxBufferBaseTransmissionLine are NO LONGER truncated/re-anchored
        // here -- matching legacy's own UpdateSampFreq/RedrawSSTV, which never truncates its own
        // m_StgBuf either (Main.cpp:5603-5612). This is what lets a later, better correction fix rows an
        // earlier one already drew wrong: with the buffer never cleared, every pass's redraw loop above
        // covers the whole staged reception from row 0 regardless of how many passes ran before it.
        // _rxBufferBaseTransmissionLine therefore stays 0 for this image's entire reception (set once at
        // InitializeSlant) -- every read site of it (ComputeOrigin's own cumulative-line-count term
        // above, the redraw loop's own bitmapRow conversion, `landedRow` above) keeps working unchanged,
        // just with a permanently-0 offset now.
        _replayPassCountForTests++; // §15 item 2 measurement: counts a genuinely completed pass only -- not the entry-guard/stagedSampleCount==0 early returns above (true no-ops, nothing redrawn), and not checkpoint 2's own HasWriteFailed bail either (that one's own resets already ran, per its own doc comment, but it still redraws nothing -- excluded from this counter the same way, just for a different reason than the true no-ops).

        return true;
    }

    /// <summary>
    /// RX buffer subsystem Phase 8 -- ports legacy's `CorrectSlant`/`KRCS` toolbar action
    /// (`Main.cpp:5264-5426`), a one-shot 5-iteration search over the staged reception for a
    /// corrected sample rate, distinct from Auto-Slant's continuous per-line tracking
    /// (<see cref="SlantTracker"/>, already ported). Reads ONLY
    /// <see cref="IRxLineStagingBuffer.SyncEnvelopeAt"/> -- confirmed by reading both legacy loop
    /// bodies directly, the demodulated/picture stream is never touched by this algorithm. On a
    /// real, converged rate change, writes <see cref="_effectiveSamplesPerLine"/> exactly once (at
    /// commit, never mid-search -- a search that runs all 5 iterations without committing must
    /// never leave live decode state corrupted) and returns <see langword="true"/>; the caller (RX
    /// buffer subsystem Phase 8's own request/drain plumbing, a later sub-piece) is responsible for
    /// triggering <see cref="PerformReplay"/> on a real commit, mirroring legacy's own
    /// `RedrawSampFreq(FALSE)` call, which this port's Phase 6/6d already built the same mechanism
    /// for.
    ///
    /// <b>Entry gate</b> (`:5267-5270`): buffer present, mode not null, NOT
    /// <see cref="IRxLineStagingBuffer.HasWriteFailed"/> (spec/18-path-to-1.0.md High item 6 --
    /// defense-in-depth only, see this early exit's own inline comment; correctness on a failed
    /// buffer is already guaranteed by the headroom checks below regardless), cumulative staged
    /// line count &gt;= 16, mode is not AVT, and (RAM mode only) there's headroom for one more line --
    /// <see cref="IRxLineStagingBuffer.HasHeadroomForSamples"/>. Buffered-replay fix (§15 item 2):
    /// the cumulative count still adds <see cref="_rxBufferBaseTransmissionLine"/> to
    /// <see cref="IRxLineStagingBuffer.LineCount"/>, matching <see cref="PerformReplay"/>'s own origin
    /// calculation -- but that field now stays 0 for a whole reception (the staging buffer is never
    /// truncated/re-based anymore), so this addition is a no-op in practice, not dead: it stays
    /// correct if a future change ever re-introduces a re-base of that field for a different reason.
    ///
    /// <b>Per iteration</b>: (1) a circular sync-envelope-amplitude histogram over the first
    /// `min(LineCount, 32)` STAGED lines (this port's own local index space, NOT the entry gate's
    /// cumulative count -- matching <see cref="PerformReplay"/>'s own
    /// `SampleCountThroughLine(Math.Min(LineCount, 32))` bound)
    /// finds the dominant sync-pulse position (`bpos`); (2) a full-buffer linear-regression fit of
    /// peak-sync-position-vs-row-index, walking every staged sample as ONE flat pass (not per-line
    /// -- `basePos` is a running absolute index, exactly how <see cref="PerformReplay"/> already
    /// walks this same buffer), with a 4-branch wraparound-correction cascade against the CURRENT
    /// `bpos` estimate (two of those branches ABORT THE WHOLE FIT PASS, `goto _nx` in the source --
    /// ported as an early `break` out of the scan, not a `continue`) and `bpos` itself updated to
    /// each newly-accepted row's own position (`:5364` -- NOT held fixed at the histogram's own
    /// argmax); (3) if at least 6 rows were accumulated into the regression sums, a least-squares
    /// slope converted to a candidate rate, snapped to the nearest 0.01Hz. The convergence check
    /// itself is UNCONDITIONAL (not gated on the 6-row minimum) -- with fewer than 6 rows the
    /// candidate defaults to the CURRENT iteration's own rate, which trivially "converges" and
    /// breaks the loop immediately, committing whatever rate the PREVIOUS iteration had already
    /// proposed (not "no commit" -- a real, easy-to-get-wrong behavior, see this method's own test
    /// coverage). Non-convergence halves the search window (`searchWindow`, legacy's `LW`, an `int`
    /// -- truncates every halving, `:5413`) and loops with the new candidate carried forward as
    /// THIS method's own local state, never touching <see cref="_effectiveSamplesPerLine"/>
    /// mid-search.
    ///
    /// <b>Tail</b> (`:5415-5423`): commits only if the rate actually changed from the search's own
    /// starting value (an exact <c>!=</c> comparison, matching legacy's own real behavior -- both
    /// sides are plain value copies through this method, never independently recomputed, so exact
    /// double equality is safe and correct here, not a bug) AND (not-RAM-mode OR
    /// RAM-mode-with-headroom-for-32-more-lines, checked against the line width at SEARCH START,
    /// matching legacy's own frozen `m_WD` -- confirmed assigned exactly once in the entire legacy
    /// codebase, `sstv.cpp:594`, never touched by a sample-rate change) --
    /// <see cref="IRxLineStagingBuffer.HasHeadroomForSamples"/> again, this time also covering the
    /// disk-always-commits case (that implementation is unconditionally <see langword="true"/>
    /// there unless a write has already failed).
    ///
    /// <b>Two stated divergences from legacy</b>, both accepted, neither a design flaw: legacy's own
    /// `MultProc()` (dropped here) lets its single-threaded UI keep draining LIVE incoming capture
    /// WHILE this synchronous search runs, so legacy's OWN scanned extent can grow between
    /// iterations during a live reception -- this port's decode thread serializes the search, so the
    /// scanned extent (<see cref="IRxLineStagingBuffer.Count"/> at the moment the search begins) is
    /// frozen across all 5 iterations, a more reproducible/testable choice than legacy's own
    /// timing-dependent behavior, not a "missing feature." And: 10 full linear passes over a long
    /// <see cref="RxBufferMode.Extended"/> reception (Phase 7's own "no RAM cap" design point) is a
    /// real, accepted multi-second-scale synchronous stall on the decode thread, during which
    /// inbound audio risks being dropped by <c>IAudioEngine</c>'s own drain-thread-overrun policy --
    /// same category of accepted tradeoff as Phase 7's own drain-barrier blocking points, not a new
    /// concern this method invents.
    /// </summary>
    private bool TryCorrectSlant()
    {
        var stagingBuffer = _rxLineStagingBuffer;
        if (stagingBuffer is null || _mode is null)
        {
            return false;
        }

        // spec/18-path-to-1.0.md High item 6. Defense-in-depth, not a correctness fix: this
        // method's own existing HasHeadroomForSamples checks below (entry and pre-commit) already
        // fully protect correctness on a failed buffer -- RxDiskLineStagingBuffer.HasHeadroomForSamples
        // is `=> !_hasWriteFailed`, so a failure is already caught before any commit either way. This
        // explicit check exists only to skip the full 5-iteration/10-pass linear search over a
        // known-already-failed buffer (a real cost on the decode thread for a long Extended
        // reception, see this method's own performance-tradeoff doc comment below) rather than to
        // catch a bug HasHeadroomForSamples doesn't already catch. See PerformReplay's own guards for
        // the write-failure case that IS a real correctness fix.
        if (stagingBuffer.HasWriteFailed)
        {
            return false;
        }

        var mode = _mode;
        var cumulativeLineCount = _rxBufferBaseTransmissionLine + stagingBuffer.LineCount;
        if (cumulativeLineCount < 16 || mode == SstvModeRegistry.Avt)
        {
            return false;
        }

        // this port's m_WD-equivalent -- computed ONCE, at search start, and never re-derived; the
        // tail's own headroom check reuses this exact value (legacy's own m_WD is frozen for the
        // same reason, see this method's own doc comment).
        var startLineWidthSamples = (int)_effectiveSamplesPerLine;
        if (!stagingBuffer.HasHeadroomForSamples(startLineWidthSamples))
        {
            return false;
        }

        var startSampleRate = _effectiveSamplesPerLine / (mode.LineDurationMs / 1000.0);
        var candidateSampleRate = startSampleRate;
        var candidateLineWidthSamples = _effectiveSamplesPerLine;
        var rowsPerTransmissionLine = _lineDecoder!.RowsPerTransmissionLine;
        var remainingTransmissionLines = Math.Max(
            0,
            (int)Math.Ceiling((mode.ImageHeight - _nextLine) / (double)rowsPerTransmissionLine));
        if (!IsManualSlantEntryProjectionSafe(
                candidateSampleRate,
                mode.LineDurationMs,
                _consumedSamples,
                remainingTransmissionLines))
        {
            return false;
        }

        // Legacy's own LW: an int, truncates at init and every halving below (Main.cpp:5273/:5413)
        // -- ported literally, not "cleaned up" to a double, since the truncation is load-bearing
        // for the search-window boundary from iteration 3 onward.
        var searchWindow = (int)(candidateLineWidthSamples * 0.1);

        // Frozen for the whole search -- see this method's own doc comment on the MultProc
        // divergence (legacy's own scanned extent can grow mid-search during a live reception;
        // this port's decode thread serializes the search, so nothing grows underneath it).
        var scannedSampleCount = stagingBuffer.Count;
        var histogramSampleCount = stagingBuffer.SampleCountThroughLine(Math.Min(stagingBuffer.LineCount, 32));

        for (var iteration = 0; iteration < 5; iteration++)
        {
            // --- Pass 1: circular sync-envelope-amplitude histogram, first histogramSampleCount
            // samples only (Main.cpp:5277-5309). Values stay `double`, not truncated to `int` the
            // way legacy's own already-16-bit-quantized `short* sp` naturally are -- this port's own
            // sync envelope was never quantized (RxLineStagingBuffer's own doc comment: "this port's
            // replay reads back full-precision doubles," an already-accepted, documented divergence
            // from legacy's write-time 16-bit quantization) -- introducing a NEW truncation here
            // would add a second, gratuitous precision loss this port doesn't otherwise have.
            var histogramWidth = (int)candidateLineWidthSamples;
            var histogram = new double[histogramWidth];
            var histogramIndex = 0;
            for (var i = 0; i < histogramSampleCount; i++)
            {
                histogram[histogramIndex] += stagingBuffer.SyncEnvelopeAt(i);
                histogramIndex++;
                if (histogramIndex >= histogramWidth)
                {
                    histogramIndex = 0;
                }
            }

            double bpos = 0;
            var histogramMax = 0.0;
            for (var i = 0; i < histogramWidth; i++)
            {
                if (histogramMax < histogram[i])
                {
                    histogramMax = histogram[i];
                    bpos = i;
                }
            }

            // --- Pass 2: slant regression fit, ALL scannedSampleCount samples, one flat walk
            // (Main.cpp:5312-5390) -- not per-line, exactly how PerformReplay already walks this
            // same buffer by absolute index.
            var y = 0;
            var max = 0.0;
            var min = 16384.0;
            var n = 0;
            var m = 0;
            double ps = 0;
            double sumY = 0, sumL = 0, sumYY = 0, sumYL = 0;

            for (var basePos = 0; basePos < scannedSampleCount; basePos++)
            {
                var yy = (int)(basePos / candidateLineWidthSamples);
                var xx = basePos % candidateLineWidthSamples; // fmod-equivalent -- C#'s % on a double operand matches fmod, not Math.IEEERemainder

                if (yy != y)
                {
                    var aborted = false;

                    // 4-branch wraparound cascade against the CURRENT bpos estimate -- only one
                    // branch (if any) fires per row boundary. Two of these ABORT THE WHOLE FIT PASS
                    // (Main.cpp:5341-5342/:5349-5350's `goto _nx`), not just adjust `ps`.
                    if (bpos < 0)
                    {
                        if (ps >= candidateLineWidthSamples / 4)
                        {
                            ps -= candidateLineWidthSamples;
                        }
                        else if (ps >= candidateLineWidthSamples / 8)
                        {
                            aborted = true;
                        }
                    }
                    else if (bpos >= candidateLineWidthSamples)
                    {
                        if (ps < candidateLineWidthSamples * 3 / 4)
                        {
                            ps += candidateLineWidthSamples;
                        }
                        else if (ps < candidateLineWidthSamples * 7 / 8)
                        {
                            aborted = true;
                        }
                    }
                    else if (bpos >= candidateLineWidthSamples * 3 / 4)
                    {
                        if (ps < candidateLineWidthSamples / 4)
                        {
                            ps += candidateLineWidthSamples;
                        }
                    }
                    else if (bpos <= candidateLineWidthSamples / 4)
                    {
                        if (ps >= candidateLineWidthSamples * 3 / 4)
                        {
                            ps -= candidateLineWidthSamples;
                        }
                    }

                    if (aborted)
                    {
                        break;
                    }

                    // y is always >= 0 in practice here (starts at 0, only ever assigned from yy,
                    // which is itself always >= 0) -- ported literally, matching Main.cpp:5363's own
                    // always-true guard rather than rationalizing it away.
                    if (y >= 0 && (max - min) >= 4800 && Math.Abs(ps - bpos) <= searchWindow)
                    {
                        bpos = ps; // NOT held fixed at the histogram's own argmax -- this is what lets bpos legitimately drift outside [0, candidateLineWidthSamples), the only thing that makes the two abort branches above reachable at all
                        if (n >= 2)
                        {
                            sumY += y;
                            sumL += ps;
                            sumYY += (double)y * y;
                            sumYL += y * ps;
                            m++;
                        }

                        n++;
                        if (n >= stagingBuffer.LineCount)
                        {
                            break;
                        }
                    }

                    y = yy;
                    max = 0;
                    min = 16384;
                    ps = 0;
                }

                var sample = stagingBuffer.SyncEnvelopeAt(basePos);
                if (max < sample)
                {
                    max = sample;
                    ps = xx;
                }

                if (min > sample)
                {
                    min = sample;
                }
            }

            // --- Regression solve + convergence check (Main.cpp:5391-5412) ---
            // `fq` is initialized to the CURRENT candidate BEFORE the `m >= 6` check -- this ordering
            // is load-bearing (round-2 plan-review finding): with fewer than 6 accumulated rows, the
            // convergence check below trivially succeeds against this unchanged default, breaking
            // the loop and committing whatever candidate this iteration STARTED with -- "no real
            // regression data" does not mean "no commit" once iteration > 0.
            var fq = candidateSampleRate;
            if (m >= 6)
            {
                var k0 = (m * sumYL - sumL * sumY) / (m * sumYY - sumY * sumY);
                fq = candidateSampleRate + k0 * candidateSampleRate / candidateLineWidthSamples;
                fq = Math.Floor(fq * 100.0 + 0.5) / 100.0; // NormalSampFreq(fq, 100) -- half-away-from-zero, NOT Math.Round's banker's rounding (ComLib.cpp:203-206)
            }

            // Legacy keeps fq/m_TW as double and has no corresponding range guard. This port later
            // narrows both the reconstructed effective rate and line width to int in live/replay
            // decoding. Reject only candidates those consumers cannot represent, before fq can
            // seed the next iteration's histogram allocation or mutate live slant state.
            if (!IsManualSlantCandidateProjectionSafe(
                    fq,
                    mode.LineDurationMs,
                    _consumedSamples,
                    remainingTransmissionLines))
            {
                return false;
            }

            if (Math.Abs(fq - candidateSampleRate) < 0.1 / 11025.0 * candidateSampleRate)
            {
                candidateSampleRate = fq;
                candidateLineWidthSamples = mode.LineDurationMs / 1000.0 * candidateSampleRate;
                break;
            }

            // Non-convergence: legacy reverts to StartSamp, calls MultProc() (dropped -- no DSP
            // effect for this port, see this method's own doc comment), then advances to the new
            // candidate `fq` -- since nothing observes the intermediate reverted state (MultProc is
            // a no-op here), this collapses to advancing straight to `fq`.
            candidateSampleRate = fq;
            candidateLineWidthSamples = mode.LineDurationMs / 1000.0 * candidateSampleRate;
            searchWindow /= 2; // legacy's `LW *= 0.5` on an int -- integer-truncating halving
        }

        if (candidateSampleRate == startSampleRate)
        {
            return false;
        }

        if (!stagingBuffer.HasHeadroomForSamples(32 * startLineWidthSamples))
        {
            return false;
        }

        _effectiveSamplesPerLine = candidateLineWidthSamples;

        // D0-audit round-8/9 history (superseded, kept for context): TryCorrectSlant is the only
        // writer of _effectiveSamplesPerLine that commits MID-LINE, unlike the automatic path
        // (ProcessSlantTrackingSample), which always commits at a line boundary and re-anchors the
        // accumulator/envelope fields immediately after, by construction. Rounds 8 and 9 patched this
        // method itself to re-anchor the same fields here, to cover PerformReplay's own checkpoint-1
        // bail (RxBufferMode.Extended's HasWriteFailed) -- but that patch left this method's OWN
        // commits (_effectiveSamplesPerLine and the tracker's adopted rate, immediately below)
        // stranded uncommitted-but-unreverted on that exact bail: the accumulator/envelope fields
        // ended up correctly re-anchored against a rate that was never actually applied. Same bug
        // class rounds 8/9 were trying to close, one layer up.
        //
        // D0-audit round-10 structural fix: this method now commits ONLY _effectiveSamplesPerLine and
        // the tracker's rate (immediately below) -- it no longer touches the accumulator/envelope
        // fields/Auto-Sync thresholds at all. The caller, TryCorrectSlantAndApply(), snapshots this
        // method's full commit set BEFORE calling it and reverts everything if the subsequent
        // PerformReplay() call reports it never started applying the correction (its own new bool
        // return) -- a real commit-or-revert transaction, matching legacy's own shape exactly
        // (Main.cpp:5415-5423). The accumulator/envelope-field re-anchor rounds 8/9 added here moved
        // into PerformReplay itself (right after its own checkpoint 1), the one place a
        // checkpoint-2-only bail (deliberately not rolled back, a separate, unchanged, pre-existing
        // acceptance) can still need it live.

        // Auditor code-review finding, Phase 8c round 1: without this, _slantTracker's own evolving
        // _currentSampleRate/_nominalSamplesPerLine stay at their pre-manual values, so the NEXT
        // automatic Auto-Slant commit computes its drift delta against a stale baseline and silently
        // reverts this correction -- see AdoptCorrectedRate's own doc comment for the full legacy
        // citation (Main.cpp:3994-3997 reads the SAME SSTVSET.m_SampFreq/m_TW this search's own
        // SetSampFreq()-equivalent writes). Non-null for every mode this method can ever commit for
        // (the entry gate above already rejects AVT, the only mode InitializeSlant leaves this null
        // for) -- the `?.` is defensive, not an expected-null path.
        //
        // Batch 2 chunk 2b round-1 fix: RestoreRateWithoutReset, NOT AdoptCorrectedRate. In legacy
        // the manual path's InitAutoStop is reached only through the success arm's RedrawSampFreq ->
        // UpdateSampFreq (Main.cpp:5418 -> 5585 -> 5600); resetting here instead would fire it
        // BEFORE TryCorrectSlantAndApply has even learned whether PerformReplay will bail, making
        // the reset unrecoverable on the revert path. PerformReplay's own ResetBaseline() call
        // supplies legacy's InitAutoStop at exactly legacy's position -- after all of its early-exit
        // `return false`s, before every `return true`.
        _slantTracker?.RestoreRateWithoutReset(candidateSampleRate);

        return true;
    }

    /// <summary>Transactional wrapper around <see cref="TryCorrectSlant"/> + <see cref="PerformReplay"/>
    /// for the manual Correct Slant path only -- D0-audit round-10 structural fix, replacing rounds
    /// 8/9's incremental patches inside <see cref="TryCorrectSlant"/> itself (see that method's own
    /// doc comment for why those were still incomplete). Snapshots every field
    /// <see cref="TryCorrectSlant"/> can mutate BEFORE calling it. If it returns
    /// <see langword="false"/>, nothing was touched, so there is nothing to revert. If it returns
    /// <see langword="true"/>, calls <see cref="PerformReplay"/>; a <see langword="false"/> result
    /// there (only reachable via <see cref="PerformReplay"/>'s own pre-mutation early exits --
    /// checkpoint 1 among them, see its own doc comment) means the whole correction must revert,
    /// restoring every snapshotted field verbatim -- matching legacy's own commit-or-revert shape
    /// (<c>Main.cpp:5415-5423</c>: <c>RedrawSampFreq(FALSE)</c> only when about to redraw, otherwise
    /// <c>m_SampFreq = StartSamp; SetSampFreq();</c>). The accumulator/envelope/Auto-Sync-threshold
    /// fields are snapshotted too even though <see cref="TryCorrectSlant"/> no longer writes them
    /// directly (round-2 plan-review: kept as cheap, currently-inert insurance against this method's
    /// own snapshot list silently going stale -- NOT a guarantee that any future change to which of
    /// <see cref="PerformReplay"/>'s exits return true vs. false stays safe on its own: this list must
    /// still be re-audited against <see cref="PerformReplay"/>'s full mutation set, including
    /// <c>_lastLineSyncPeakPosition</c>/<c>_suppressNextSlantProcessLine</c>/the tracker's baseline/the
    /// Auto-Sync observation history, none of which are snapshotted here, whenever that boundary
    /// changes). <see cref="SlantTracker.RestoreRateWithoutReset"/> restores the rate pair, and — as
    /// of the Batch 2 chunk 2b round-1 fix — the forward commit no longer resets the tracker either
    /// (see <see cref="TryCorrectSlant"/>'s own call site), so the baseline/history/average/bitmask
    /// genuinely survive a reverted manual correction, matching legacy's own revert arm (a bare
    /// <c>SetSampFreq()</c>, no <c>InitAutoStop</c> call -- verified against
    /// <c>Main.cpp:5420-5423</c>). The success-arm reset now comes solely from
    /// <see cref="PerformReplay"/>'s own <see cref="SlantTracker.ResetBaseline"/> call, matching
    /// legacy's <c>InitAutoStop</c>-inside-<c>UpdateSampFreq</c> placement (<c>Main.cpp:5600</c>).
    /// The two
    /// <c>*SafetyCheckCountForTests</c> diagnostic counters are deliberately excluded from the
    /// snapshot: they count how many times a check ran, not correction state, and must keep
    /// incrementing regardless of outcome.</summary>
    private bool TryCorrectSlantAndApply()
    {
        var preCommitEffectiveSamplesPerLine = _effectiveSamplesPerLine;
        var preCommitIdealSamplesSoFarInLine = _slantIdealSamplesSoFarInLine;
        var preCommitLineEnvelopeSeeded = _slantLineEnvelopeSeeded;
        var preCommitLineMaxEnvelope = _slantLineMaxEnvelope;
        var preCommitLineMinEnvelope = _slantLineMinEnvelope;
        var preCommitLinePeakPosition = _slantLinePeakPosition;
        var preCommitAutoSyncBaseMult = _autoSyncBaseMult;
        var preCommitAutoSyncDiff = _autoSyncDiff;

        if (!TryCorrectSlant())
        {
            return false;
        }

        if (PerformReplay())
        {
            return true;
        }

        // Revert -- see this method's own doc comment. `_mode` is guaranteed non-null here:
        // TryCorrectSlant only returns true after its own `_mode is null` entry guard has passed, and
        // nothing between there and here can null it (decode-thread-only, no re-entrancy).
        _effectiveSamplesPerLine = preCommitEffectiveSamplesPerLine;
        _slantIdealSamplesSoFarInLine = preCommitIdealSamplesSoFarInLine;
        _slantLineEnvelopeSeeded = preCommitLineEnvelopeSeeded;
        _slantLineMaxEnvelope = preCommitLineMaxEnvelope;
        _slantLineMinEnvelope = preCommitLineMinEnvelope;
        _slantLinePeakPosition = preCommitLinePeakPosition;
        _autoSyncBaseMult = preCommitAutoSyncBaseMult;
        _autoSyncDiff = preCommitAutoSyncDiff;

        var preCommitSampleRate = preCommitEffectiveSamplesPerLine / (_mode!.LineDurationMs / 1000.0);
        _slantTracker?.RestoreRateWithoutReset(preCommitSampleRate);

        return false;
    }

    /// <summary>Pure safety predicate for manual Correct Slant's unbounded regression result.
    /// Automatic slant has legacy's own upper clamp; this path does not. The two extra line widths
    /// conservatively cover the live next-line cursor plus ReSync/replay reconciliation beyond the
    /// remaining image extent.</summary>
    internal static bool IsManualSlantProjectionSafe(
        double candidateSampleRate,
        double lineDurationMs,
        int currentConsumedSample,
        int remainingTransmissionLines)
    {
        if (!double.IsFinite(candidateSampleRate))
        {
            return false;
        }

        var roundedEffectiveSampleRate = Math.Round(candidateSampleRate);
        if (!double.IsFinite(roundedEffectiveSampleRate)
            || roundedEffectiveSampleRate < 1d
            || roundedEffectiveSampleRate > int.MaxValue)
        {
            return false;
        }

        var candidateLineWidthSamples = lineDurationMs / 1000d * candidateSampleRate;
        if (!double.IsFinite(candidateLineWidthSamples)
            || candidateLineWidthSamples < 1d
            || candidateLineWidthSamples > int.MaxValue)
        {
            return false;
        }

        var projectedSamples = Math.Ceiling(
            (Math.Max(0, remainingTransmissionLines) + 2d) * candidateLineWidthSamples);
        // Several absolute-index cache-fill loops use `cursor <= requestedIndex; cursor++`.
        // Equality with int.MaxValue would therefore wrap the loop cursor after processing the
        // requested sample; preserve one representable index beyond the projection.
        return double.IsFinite(projectedSamples)
            && projectedSamples < int.MaxValue - (double)currentConsumedSample;
    }

    private bool IsManualSlantEntryProjectionSafe(
        double candidateSampleRate,
        double lineDurationMs,
        int currentConsumedSample,
        int remainingTransmissionLines)
    {
        _manualSlantEntrySafetyCheckCountForTests++;
        return IsManualSlantProjectionSafe(
            candidateSampleRate,
            lineDurationMs,
            currentConsumedSample,
            remainingTransmissionLines);
    }

    private bool IsManualSlantCandidateProjectionSafe(
        double candidateSampleRate,
        double lineDurationMs,
        int currentConsumedSample,
        int remainingTransmissionLines)
    {
        _manualSlantCandidateSafetyCheckCountForTests++;
        return IsManualSlantProjectionSafe(
            candidateSampleRate,
            lineDurationMs,
            currentConsumedSample,
            remainingTransmissionLines);
    }

    /// <summary>Averages the (already fully demodulated) frequency stream over [startSample,
    /// endSample), skipping a settling margin at the start for the demodulator's own transient
    /// response after the preceding frequency change -- stale reference to "PLL loop" fixed (third
    /// flag, an auditor code-level review each time): this port's main picture demodulator has been
    /// <see cref="HilbertFmDemodulator"/> since Piece 14, not a PLL.</summary>
    private double AverageFrequencyInWindow(int startSample, int endSample)
    {
        var sampleCount = Math.Max(1, endSample - startSample);
        var settleSamples = sampleCount / 4;

        var from = Math.Clamp(startSample + settleSamples, _bufferBase, TotalSamplesReceived);
        var to = Math.Clamp(endSample, _bufferBase, TotalSamplesReceived);
        if (to <= from)
        {
            from = Math.Clamp(startSample, _bufferBase, TotalSamplesReceived);
            to = Math.Clamp(endSample, _bufferBase, TotalSamplesReceived);
        }

        if (to <= from)
        {
            return 0;
        }

        double sum = 0;
        for (var i = from; i < to; i++)
        {
            // Band-1 item 4a: this is the one confirmed pre-lock reader of _demodulatedFrequencies
            // (the narrow-vs-normal-VIS discriminator, called before Commit() from TryDecodeHeader) --
            // DemodulatedFrequencyAt lazily fills it correctly either way, no special-casing needed.
            sum += DemodulatedFrequencyAt(i);
        }

        return sum / (to - from);
    }
}
