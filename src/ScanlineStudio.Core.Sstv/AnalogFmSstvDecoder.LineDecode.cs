using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

public sealed partial class AnalogFmSstvDecoder
{
    // Legacy's m_Skip drain (sstv.cpp:2271-2274): while m_Skip > 0 it decrements once per INCOMING
    // sample and drops that sample, so a skip larger than one audio callback's worth of samples
    // inherently spans several callbacks. This port advances the READ cursor instead of dropping
    // write-side samples (see PerformReSync), so the drain has to be incremental for the same reason:
    // at the top of a PushSamples call, TotalSamplesReceived - _consumedSamples is normally LESS than
    // one line (exactly why TryProcessBuffer's own per-line guard returns early), while `skip` can be
    // nearly a full line -- applying it in one shot would call AgcSampleAt past the end of the
    // received stream and throw on the audio thread, on the common path.
    //
    // TWO call sites, D0-audit round-4 fix: the top of PushSamplesCore (streaming pattern -- a skip
    // requested by PerformReSync, an external/UI-driven trigger, or left over from a chunk that ended
    // mid-skip) AND, since this fix, TryProcessBuffer's own per-line loop right after
    // ApplySlantTracking() (a skip TriggerAutoSync -- decode-driven, not caller-driven -- may have
    // just requested in that same call). The top-level call alone was insufficient for the SAME
    // caller-chunk-boundary reason _pendingReplayRequested's own drain was moved off it: a bulk
    // caller pushing a whole transmission in one PushSamplesCore call never lets a SECOND call arrive
    // to drain a skip TriggerAutoSync requests mid-decode, so it was silently zeroed unapplied by
    // EndOfImage's ResetReSyncState() while ApplySyncCorrection's other side-effects
    // (_slantCorrectionsDisabledForRestOfImage, the Auto-Sync cooldown) still applied regardless. Both
    // sites are safe to call unconditionally on every entry -- this method is a no-op whenever
    // _pendingSkipSamples is already 0.
    //
    // Every cursor moves in exact lockstep, one sample per iteration:
    //  * _consumedSamples      -- the decode read cursor; the actual correction.
    //  * _idealLineStartSample -- kept round()-consistent with _consumedSamples at EVERY step, not
    //    just once the drain finishes. Advancing it only on completion would leave _consumedSamples >
    //    round(_idealLineStartSample) for the whole partial-drain window, shrinking the per-line
    //    loop's own line-sample-count by however much has drained so far. `+= 1.0` rather than a final
    //    `= _consumedSamples` also keeps the accumulated fractional residue MUST-4 exists to preserve.
    //  * _slantProcessedUpTo   -- so ApplySlantTracking's catch-up loop never re-walks the skipped span
    //    through its NORMAL per-sample path, which would let those samples count toward
    //    _slantIdealSamplesSoFarInLine and reopen the non-convergence this correction exists to fix.
    //
    // _slantIdealSamplesSoFarInLine is DELIBERATELY not advanced: leaving it at its pre-jump value is
    // precisely what shifts the in-line mapping back by `skip` and lands the next measured peak on
    // m_OFP.
    //
    // The envelope detector IS still fed every skipped sample, with the result discarded and
    // deliberately NOT compared against _slantLineMaxEnvelope: legacy computes d12/d19 and runs them
    // into m_iir12/m_iir19 BEFORE the m_Skip check (sstv.cpp:1841-1853 vs :2271) but only writes m_B12
    // in the non-skip branch (sstv.cpp:2284-2293) -- filter state advances, the peak tracker never
    // sees the dropped samples. AFC still covers this span too, just lazily, via its own existing
    // catch-up loop on the next decoded line (matching legacy, whose SyncFreq/m_hill also run before
    // the m_Skip check).
    //
    // Bounded by TotalSamplesReceived; AgcSampleAt reads no further ahead than the index requested, so
    // strict `<` is exactly right here -- `<=` would throw.
    private void DrainPendingSkip()
    {
        // _syncEnvelopeDetector is non-null whenever _pendingSkipSamples can be (PerformReSync
        // requires a live _slantTracker, and the two are created and destroyed together,
        // InitializeSlant/EndOfImage) -- captured into a local for the nullable-reference contract,
        // not because the field can actually change mid-loop.
        var detector = _syncEnvelopeDetector;
        if (_pendingSkipSamples <= 0 || detector is null)
        {
            return;
        }

        while (_pendingSkipSamples > 0 && _consumedSamples < TotalSamplesReceived)
        {
            // Buffered-replay fix (§15 item 2), 3-way guard: in the post-backward-snap window
            // (_consumedSamples < _slantProcessedUpTo), these samples were ALREADY staged/fed before
            // the snap -- PerformReplay only rewinds the live decode-side cursors, never the staging
            // buffer or _slantProcessedUpTo/_rxBufferAnchorSample. Feeding the detector or advancing
            // either cursor again here would double-feed the detector and corrupt the fixed coordinate
            // map (origin + (raw - anchor)) that every replay resumeDest read depends on. Outside that
            // window this guard is always true and behaves exactly as before.
            // Honest residual (accepted, see robust-giggling-codd.md item 1): in the post-snap window,
            // _consumedSamples/_idealLineStartSample still advance while the anchor stays frozen, so
            // resumeDest can land up to one row past live decode. Self-healing under Step B (buffer
            // never truncated, base transmission line pinned at 0) -- the next pass's redraw covers the
            // whole staged reception regardless, so a one-row landing error corrects itself.
            if (_consumedSamples >= _slantProcessedUpTo)
            {
                detector.ProcessSample(AgcSampleAt(_consumedSamples)); // history continuity only; return value deliberately discarded
                _slantProcessedUpTo++;
                _rxBufferAnchorSample++; // RX buffer subsystem Phase 6c round-3 fix -- see this field's own doc comment: a skipped sample advances _consumedSamples without ever being staged, so the anchor must advance too or PerformReplay's own resumeDest overshoots by the skip amount
            }
            _consumedSamples++;
            _idealLineStartSample += 1.0;

            // Round-5 code-review known, deferred gap (same defect CLASS PerformReplay's own jump had
            // before its round-4 truncation fix, in a sibling path): this additive-only anchor
            // adjustment keeps the sample-COUNT invariant correct, but -- like the pre-round-4 replay
            // jump -- leaves RxLineStagingBuffer's own flat, contiguous list with a mid-buffer HOLE (the
            // skipped samples are never staged), which a later replay pass would read straight across.
            // NOT fixed here: unlike the replay jump (which lands exactly on a destination-row boundary,
            // making a clean truncation trivial), a manual-ReSync-driven skip can land mid-row, so the
            // SAME truncate-and-rebase fix doesn't drop in cleanly. Currently unreachable in practice
            // (Auto-Sync's own branch-1 trigger requires RxBufferMode != Off, but any skip that also
            // sets _slantCorrectionsDisabledForRestOfImage -- see ApplySyncCorrection -- disables the
            // Auto-Slant correction branch for the rest of the image, and replay has no production
            // caller at all yet) -- but a manual "re-apply corrected rate" UI trigger, if Phase 6d or a
            // later phase adds one, would make this reachable. Buffered-replay fix (§15 item 2) changes
            // the exact redraw-trigger landscape this reachability claim depends on (truncation, the
            // thing that used to erase any hole this created, is now gone) -- recommendation (plan
            // robust-giggling-codd.md item 5): treat re-verifying this claim against the post-fix
            // codebase as the immediate next follow-up piece, not something to leave deferred
            // indefinitely on the strength of the pre-fix reachability argument.
            _pendingSkipSamples--;
        }
    }

    // See _agcDeadZoneCatchUpTarget's own doc comment for why this is deferred instead of running
    // synchronously inside EndOfImage. A no-op once caught up (or before the first image ever
    // completes, when the target is still its default 0).
    private void AdvanceAgcThroughDeadZone()
    {
        if (_agcDeadZoneCatchUpTarget <= _levelAgcProcessedUpTo)
        {
            return;
        }

        var catchUpToExclusive = Math.Min(_agcDeadZoneCatchUpTarget, TotalSamplesReceived);
        if (catchUpToExclusive > _levelAgcProcessedUpTo)
        {
            AgcSampleAt(catchUpToExclusive - 1); // forward-fills _levelAgcProcessedUpTo through catchUpToExclusive-1
        }
    }

    // Read-only measurement: reads _rawSamples by index only (never a *SampleAt accessor, which would
    // forward-fill caches) and skips, never throws, when any index is outside the retained buffer.
    private void MeasureLineSyncSnr(SstvModeDefinition mode, int lineAnchor, int nextLineStartSample, int effectiveSampleRate, int transmissionLine)
    {
        var observer = SyncWindowObservedForTests;
        if ((!_snrMeasurementEnabled && observer is null) || mode == SstvModeRegistry.Avt)
        {
            return;
        }

        var searchCentre = SyncSnrPlacement.SearchCentreSample(
            lineAnchor,
            SstvModeRegistry.GetSyncSegmentOffsetMs(mode),
            effectiveSampleRate,
            _searchBandpassFilter?.Tap ?? 0,
            _demodType == DemodType.Hilbert ? _demodulator.HalfTap / 4 : 0);
        observer?.Invoke(transmissionLine, searchCentre);
        if (!_snrMeasurementEnabled)
        {
            return;
        }

        // Any anchor jump (ReSync, Auto-Sync, notch shifts, replay snaps, Correct Slant) invalidates the
        // placement history. A stride change alone does not: the anchor stays continuous and the tracker
        // follows the slow drift it causes (resetting on it cost most lines under Auto Slant; see
        // docs/reception-snr-validation.md).
        if (lineAnchor != _snrExpectedAnchor)
        {
            _syncSnrTracker.ResetPlacement();
        }

        _snrExpectedAnchor = nextLineStartSample;

        var syncDurationMs = SstvModeRegistry.GetSyncSegmentDurationMs(mode);
        var halfRange = (int)Math.Round(SyncSnrPlacement.SearchHalfRangeFor(syncDurationMs) * _syncSnrTracker.RangeMultiplier / 1000.0 * effectiveSampleRate);
        var pulseLength = (int)Math.Round(syncDurationMs / 1000.0 * effectiveSampleRate);
        var trim = (int)Math.Round(SyncSnrTrimMs / 1000.0 * effectiveSampleRate);
        var positions = (2 * halfRange) + 1;
        var spanOffset = _syncSnrTracker.SearchBias - halfRange;
        var first = (int)Math.Floor(searchCentre) + spanOffset;
        var endExclusive = (long)first + positions - 1 + pulseLength;
        if (first < _bufferBase || endExclusive > TotalSamplesReceived)
        {
            return;
        }

        var span = CollectionsMarshal.AsSpan(_rawSamples).Slice(first - _bufferBase, (int)(endExclusive - first));
        var nominalHz = mode.NarrowModeCode is not null ? 1900.0 : 1200.0;
        var expectedToneHz = nominalHz - (_afcTracker?.CorrectionHz ?? 0.0);
        var outcome = _syncSnrTracker.ProcessLine(transmissionLine, span, positions, pulseLength, trim, _sampleRate, expectedToneHz, spanOffset);
        PublishSnr();
        SyncSnrLineObservedForTests?.Invoke(
            transmissionLine,
            outcome.PlacedStart < 0 ? double.NaN : first + outcome.PlacedStart,
            outcome.Estimate);
    }

    private void PublishSnr()
    {
        Interlocked.Exchange(ref _liveSnrBits, BitConverter.DoubleToInt64Bits(_syncSnrTracker.LiveSnrDb));
        Interlocked.Exchange(ref _receptionSnrBits, BitConverter.DoubleToInt64Bits(_syncSnrTracker.ReceptionSnrDb));
    }

    /// <summary>See <see cref="ISstvDecoder.LiveSnrDb"/>. Lock-free read of the decode thread's last publish.</summary>
    public double LiveSnrDb => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _liveSnrBits));

    /// <summary>See <see cref="ISstvDecoder.ReceptionSnrDb"/>. Lock-free read of the decode thread's last publish.</summary>
    public double ReceptionSnrDb => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _receptionSnrBits));

    /// <summary>See <see cref="ISstvDecoder.SnrMeasurementEnabled"/>. The getter returns the last
    /// requested value; the change takes effect at the next <see cref="PushSamples"/>.</summary>
    public bool SnrMeasurementEnabled
    {
        get => Volatile.Read(ref _snrMeasurementRequested) == 1;
        set => Volatile.Write(ref _snrMeasurementRequested, value ? 1 : 0);
    }

    private void ResetSnrForNewReception()
    {
        _snrExpectedAnchor = int.MinValue;
        _syncSnrTracker.ResetReception();
        PublishSnr();
    }

    private void EndLiveSnr()
    {
        _syncSnrTracker.EndLive();
        PublishSnr();
    }

    // Outer loop added for piece 6a (end-of-image reset, sstv.cpp's Stop()/cases 512-513): once an
    // image completes, EndOfImage() clears _mode and this loops back to try detecting a *subsequent*
    // transmission already sitting in the same pushed buffer, rather than requiring a separate
    // PushSamples call to notice it. Terminates whenever there isn't yet enough data to either find
    // a header or finish the line currently in progress.
    private void TryProcessBuffer()
    {
        while (true)
        {
            if (_mode is null && !TryDecodeHeader())
            {
                return;
            }

            // Piece 8c: a non-AVT mode just locked (or is still waiting from a previous call) --
            // resolve the sync-anchor correction before decoding any pixels for it. See
            // _pendingAnchorCorrectionMode's own doc comment.
            if (_pendingAnchorCorrectionMode is not null)
            {
                if (!TryResolveSyncAnchorCorrection(_pendingAnchorCorrectionMode))
                {
                    return;
                }

                // Functional-audit fix (D6, round 1): clear BEFORE calling FinalizeAnchorAndStartDecoding
                // (which fires ModeDetected), not after. A throwing ModeDetected subscriber previously
                // left this flag still set when the exception propagated out of PushSamples, so the
                // NEXT PushSamples call would re-run TryResolveSyncAnchorCorrection +
                // FinalizeAnchorAndStartDecoding for the SAME already-finalized lock -- a duplicate
                // InitializeAfc/InitializeSlant and a duplicate ModeDetected for one real detection.
                // Capturing the mode into a local first, since the field is cleared before the call
                // that needs it.
                var resolvedMode = _pendingAnchorCorrectionMode;
                _pendingAnchorCorrectionMode = null;
                FinalizeAnchorAndStartDecoding(resolvedMode);
            }

            var mode = _mode!;
            var lineDecoder = _lineDecoder!;
            var pixels = _pixels!;
            var restarted = false;

            while (_nextLine < mode.ImageHeight)
            {
                // _effectiveSamplesPerLine reflects Auto Slant corrections from lines *before* this one
                // only -- ApplySlantTracking() runs strictly after this same line's own decode below,
                // never ahead of it. Matches legacy's real causal order exactly (Main.cpp's real-time
                // loop): a transmission line is always decoded using whatever m_TW/m_SampFreq stood
                // *before* it started, then that same line's own sync-envelope data feeds AutoStopJob,
                // which may update m_TW/m_SampFreq for the *next* line. An earlier version of this method
                // ran slant-tracking as a single bulk pass over the whole buffered stream before this
                // loop even started -- fine for streaming callers where only a little data is ever
                // available ahead of decode, but wrong (and, worse, silently wrong) for a caller that
                // pushes everything at once: slant-tracking would run all the way through image data
                // *and into the trailing footer tone*, corrupting the sync-position history with
                // non-image content before the very first line was ever decoded. Caught by an end-to-end
                // mistuned-rate test, not by any of the isolated or single-shot unit tests above.
                // MUST 4 (spec/14-roadmap.md, Phase 3): nextLineStartSample is derived from
                // _idealLineStartSample's own running double total, not by rounding-then-accumulating
                // _effectiveSamplesPerLine every line -- see _idealLineStartSample's own doc comment.
                // lineSampleCount (this line's own sample span) is the DIFFERENCE between the two
                // rounded cursors, matching legacy's own naturally-varying per-line span (each line's
                // width, in samples, differs from its neighbors by up to 1 -- an artifact of `y =
                // int(n/m_TW)` against a non-integer m_TW -- not a fixed per-line constant).
                var nextLineStartSample = (int)Math.Round(_idealLineStartSample + _effectiveSamplesPerLine);
                var lineSampleCount = nextLineStartSample - _consumedSamples;
                if (TotalSamplesReceived - _consumedSamples < lineSampleCount)
                {
                    return; // waiting for more samples to finish this image -- not done, don't reset
                }

                // mode.LineDurationMs/1000*effectiveSampleRate == _effectiveSamplesPerLine by
                // construction: passing this adjusted rate into DecodeLine scales every per-segment
                // sample calculation inside it proportionally, without IScanlineDecoder needing to know
                // anything about slant correction at all.
                var effectiveSampleRate = (int)Math.Round(_effectiveSamplesPerLine / (mode.LineDurationMs / 1000.0));

                // Bounded to exactly this line's own extent, not a single eager bulk pass over the
                // whole image -- see ApplyAfcCorrections' own doc comment for why (mid-reception
                // restart double-correction fix). Must run before DecodeLine, which reads the
                // corrected frequencies via the reader constructed below.
                ApplyAfcCorrections(nextLineStartSample);

                // Piece 10: PixelSampleReader is constructed fresh per line, not per mode/session --
                // GetKsbSamples depends on effectiveSampleRate, which this port recomputes per line
                // for Auto Slant (matching legacy's own per-line m_KSB recompute-on-slant-change,
                // Main.cpp:4015/:5900-5903). lineEndSampleExclusive is this line's own extent, used
                // only by the (currently unreachable at every real mode) line-end guard.
                //
                // RX buffer subsystem Phase 5 finding: `RxBufferMode.Extended` disables peak-picking
                // for EVERY mode/channel, not just Scottie DX. Legacy's own `GetPictureLevel`/
                // `GetPictureLevelDiff` (Main.cpp:4058-4084, this port's own ReadPeakPicked/its diff
                // sibling) both gate the KSB peek-ahead comparison on `sys.m_UseRxBuff != 2`: under
                // Extended, `d = GetPixelLevel(ip)` unconditionally (bare, no peek), for every one of
                // their real call sites in the live per-line draw dispatch (Main.cpp:4230/:4251/:4267/
                // :4280/:4330/:4385/:4420/:4437/:4459/:4470/:4481) -- NOT only a replay-path detail.
                // The RX buffer plan's own "out of scope, C++ pointer-arithmetic safety detail" framing
                // for this was WRONG (round-1 code-review finding on this phase): it's a real,
                // observable pixel-level decode divergence, reachable today since RxBufferMode.Extended
                // has been selectable since Phase 2/3. Folds cleanly into the SAME `neverPeakPicks`
                // mechanism Scottie DX's own existing exclusion already uses (PixelSampleReader's own
                // `_neverPeakPicks` field, already unit-tested in isolation) -- OR'd together, not a
                // separate code path.
                // Functional-audit fix (chunk D4 round 1): was `Math.Clamp(index, _bufferBase,
                // TotalSamplesReceived - 1)` -- the lower bound silently substituted the oldest
                // retained sample for any index behind the trim watermark, defeating Rel()'s own
                // deliberate throw guard (Rel's doc comment names a silently-wrong translation as
                // the dangerous failure class here, not a loud one) in the single hottest read path
                // in this file. No reachable trip site was found (TrimBuffers' locked-branch
                // watermark tracks _consumedSamples via _slantProcessedUpTo, and this reader is only
                // ever constructed for the current, not-yet-trimmed line), so dropping it is a pure
                // defense-in-depth restoration, not a behavior change today. The upper bound stays,
                // but round 1's own justification for it was wrong (round-2 correction): KSB
                // peek-ahead can't be the reason -- PixelSampleReader.ReadPeakPicked already bails
                // out without reading the peek whenever it would land at/past this line's own
                // exclusive end (lineEndSampleExclusive == nextLineStartSample, itself already
                // proven <= TotalSamplesReceived by the availability guard above). The real
                // (currently unreached) case this upper bound guards is a start index landing at or
                // past the line end for a decoder whose per-pixel sample pitch is under ~2-3 samples
                // (sub-11025Hz rates at wide-image modes) -- kept for that reason, not KSB peek-ahead.
                var reader = new PixelSampleReader(
                    index => DemodulatedFrequencyAt(Math.Min(index, TotalSamplesReceived - 1)),
                    SstvModeRegistry.GetKsbSamples(mode, effectiveSampleRate),
                    nextLineStartSample,
                    mode.LuminanceMinHz,
                    SstvModeRegistry.NeverPeakPicks(mode) || _rxBufferMode == RxBufferMode.Extended);

                var lineAnchor = _consumedSamples;
                lineDecoder.DecodeLine(mode, effectiveSampleRate, _consumedSamples, _nextLine, reader, pixels);
                _idealLineStartSample += _effectiveSamplesPerLine;
                _consumedSamples = nextLineStartSample;

                MeasureLineSyncSnr(mode, lineAnchor, nextLineStartSample, effectiveSampleRate, _nextLine / lineDecoder.RowsPerTransmissionLine);
                RaiseSubscribers(LineDecoded, new DecodedImageUpdate(_nextLine, new MutableImageSource(mode.ImageWidth, mode.ImageHeight, pixels)));
                _nextLine += lineDecoder.RowsPerTransmissionLine;

                // RX buffer subsystem Phase 6d: the once-per-image replay latch -- port of legacy's
                // `!(m_SyncAccuracyN & 1) && (m_AY>=16)` (Main.cpp:3530-3562), originally ported as a
                // literal unconditional trigger (Phase 6's own round-1 plan-review found that narrowing
                // it to "only alongside a real commit" was based on a wrong premise, since legacy's own
                // ReSyncSSTV origin re-derivation runs unconditionally, WITH NO VISIBLE COST -- legacy's
                // own replay never loses a row). `_nextLine` counts BITMAP ROWS; legacy's `m_AY` counts
                // TRANSMISSION lines -- divide before comparing against the literal `16`, or this fires
                // at 8 transmission lines instead of 16 for paired-channel modes (round-2 plan-review
                // finding). `mode != Avt` matches legacy's own outer gate (`pDem->m_Sync && m_SyncAccuracy
                // && !m_ReqSampChg && (mode != AVT)`) -- AVT already reaches PerformReplay's own no-op
                // guard (_slantTracker is null there) regardless, but gating here too avoids wastefully
                // latching for a mode that can never replay.
                //
                // RX buffer subsystem Phase 6d round-2, two ADDITIONAL gates, both real fixes found once
                // this trigger was actually wired up and run (not caught by any earlier plan-review):
                //
                // `_anyCorrectionCommittedThisImage` -- explicit user decision (2026-08-13), a real,
                // documented divergence from the literal-unconditional trigger above. This port's OWN
                // PerformReplay used to sacrifice one row per pass by design under the old forward-jump
                // design, so the unconditional trigger, once actually firing on every default decode,
                // guaranteed a small visible defect (a black stripe) in EVERY image, even when no
                // correction was ever needed. Gating on "has a correction actually committed this image"
                // restored legacy's own real "no cost when idle" property, in this port's own way.
                // Buffered-replay fix (§15 item 2), code-review finding: PerformReplay's own backward
                // snap no longer sacrifices a row (see its own doc comment), so this gate's documented
                // rationale no longer applies -- see `_anyCorrectionCommittedThisImage`'s own field-level
                // doc comment for the full, current status (a real, undecided divergence from legacy's
                // unconditional trigger, deliberately left as-is, not silently fixed, by this piece).
                //
                // `!_slantCorrectionsDisabledForRestOfImage` -- closes a real staging-buffer-integrity
                // hole, not a stylistic gate: ApplySyncCorrection (the shared tail both manual ReSync and
                // an Auto-Sync trigger use) sets this flag AND leaves a mid-buffer HOLE in
                // RxLineStagingBuffer (DrainPendingSkip's own skipped samples advance _consumedSamples/
                // _rxBufferAnchorSample without ever being staged -- see that method's own doc comment).
                // The commit trigger above is already naturally unreachable once this flag is set (it
                // lives inside ProcessSlantTrackingSample's own `else` arm, which requires
                // `!_slantCorrectionsDisabledForRestOfImage`) -- but THIS latch has no such structural
                // protection on its own, and round-1 code review found it's the only remaining path that
                // could fire replay across that hole, corrupting every row after it.
                if (!_replayOnceLatchFired && _rxLineStagingBuffer is not null && mode != SstvModeRegistry.Avt
                    && !_slantCorrectionsDisabledForRestOfImage && _anyCorrectionCommittedThisImage
                    && _nextLine / lineDecoder.RowsPerTransmissionLine >= 16)
                {
                    _replayOnceLatchFired = true;
                    _pendingReplayRequested = true;
                }

                // Now that this line is fully decoded and _consumedSamples reflects it, let slant
                // tracking catch up through exactly this line's raw samples -- never further ahead,
                // and never for a line that hasn't been decoded yet.
                ApplySlantTracking();

                // D0-audit round-4 finding: drain a skip TriggerAutoSync may have just requested
                // (inside the ApplySlantTracking() call immediately above) HERE, at this same
                // decoded-line boundary -- not only at the top of the next PushSamplesCore call,
                // which was this method's ONLY drain site before this fix. Exactly the
                // caller-chunk-boundary bug class _pendingReplayRequested's own round-1 fix (below)
                // already exists to prevent, for a sibling flag ApplySyncCorrection sets in the SAME
                // call: a bulk caller pushing a whole transmission in one PushSamplesCore call would
                // never see a second call arrive to drain the skip at all, so EndOfImage's
                // ResetReSyncState() would silently zero _pendingSkipSamples unapplied -- while
                // _slantCorrectionsDisabledForRestOfImage (set by the SAME ApplySyncCorrection call)
                // still suppressed Auto-Slant/replay for the rest of the image regardless, paying the
                // suppression cost without the realignment it exists to buy. A streaming caller (any
                // chunk shorter than one line) was never affected -- the pre-fix top-of-PushSamplesCore
                // drain runs before the next line can complete either way -- which is why this was a
                // risk, not a blocker, in production (MiniAudio capture chunks are always sub-line).
                // Runs BEFORE the replay-request drains below: DrainPendingSkip can advance
                // _consumedSamples/_rxBufferAnchorSample (see its own doc comment), and
                // ReplayOriginCalculator.ComputeOrigin's inputs are bounded by _consumedSamples at
                // the point PerformReplay actually runs -- draining the skip first matches legacy's
                // own real-time order (m_Skip's per-sample drain always precedes that sample's own
                // decode decision, sstv.cpp:2271-2293).
                DrainPendingSkip();

                // RX buffer subsystem Phase 6d round-1 code-review fix: drain the deferred replay
                // request HERE, at a DECODED-LINE boundary, not at the top of the next PushSamples
                // call (an earlier version of this drain lived there, mirroring _reSyncRequested's own
                // established point). Both set sites -- the once-per-image latch a few lines up, and
                // ProcessSlantTrackingSample's own commit branch inside the ApplySlantTracking() call
                // immediately above -- run inside this same loop iteration, so a request is always
                // consumed in the iteration that raised it and can never survive to a caller-visible
                // boundary.
                //
                // WHY NOT the top of PushSamples: that is a CALLER-CHUNK boundary, not a decode
                // position. _reSyncRequested is set by an external UI thread, so chunk-dependent timing
                // is inherent to it there. _pendingReplayRequested is set by decode itself -- and
                // PerformReplay's own backward-snap target is a function of exactly where LIVE decode
                // currently sits (buffered-replay fix, §15 item 2: no longer by discarding samples/
                // sacrificing a row/truncating the buffer -- those are gone, see PerformReplay's own doc
                // comment -- but _consumedSamples' own live position at the moment it runs still matters),
                // so draining it at a chunk boundary makes the DECODED IMAGE a function of how the caller
                // sliced its PushSamples calls. Confirmed as a real bug, not a theoretical one: a caller that pushes
                // a whole transmission in one call never drained it at the old point at all (no second
                // PushSamples call ever arrived), while a chunked caller did -- exactly what
                // SstvRoundTripTests.DecodedImage_IsPixelIdentical_WhetherSamplesArriveInOneChunkOrMany
                // caught. This point also pins ReplayOriginCalculator.ComputeOrigin's own inputs (staged
                // LineCount/SampleCountThroughLine, both bounded by _consumedSamples here), which would
                // otherwise vary with chunk size once a single chunk exceeds one line's worth of samples.
                //
                // Reentrancy is still respected: ApplySlantTracking()'s own per-sample call stack has
                // fully unwound by this statement, exactly as it had at the old drain point -- see
                // _pendingReplayRequested's own doc comment. Cleared-without-replaying when Auto Stop has
                // just fired (checked further below in this same loop iteration, via
                // _autoStopTriggered -- D0-audit round-8 correction: dropped a stale in-file line
                // citation here, same hazard this chunk's own history keeps hitting): that block is
                // about to abandon this image, so redrawing its rows is pointless and PerformReplay
                // would run against state EndOfImage is about to discard.
                //
                // RX buffer subsystem Phase 8c: the manual "Correct Slant" request is drained HERE, at
                // the SAME statement position, and BEFORE the automatic _pendingReplayRequested block
                // below -- both for the identical caller-chunk-boundary reasoning above, and so a real
                // commit can subsume/clear _pendingReplayRequested before that block ever checks it
                // (round-2 plan-review finding: both flags can legitimately be set for the same decoded
                // line -- an automatic tracker commit landing on the same line as a manual Correct-Slant
                // convergence -- and running PerformReplay() twice for one line would redundantly redraw
                // the same span twice and jump/re-anchor the cursor twice instead of once).
                // `!_slantCorrectionsDisabledForRestOfImage`
                // mirrors the once-per-image latch's own defense-in-depth gate a few lines up (the
                // `_replayOnceLatchFired`/`_pendingReplayRequested = true` block earlier in this same
                // loop iteration -- D0-audit round-8: dropped a stale in-file line citation) -- that
                // flag marks a real mid-buffer hole left by DrainPendingSkip's skipped samples, and
                // RequestCorrectSlant is a NEW externally-reachable path into PerformReplay that has no
                // other structural protection against firing across that hole. Not gated by
                // SuppressAutomaticReplayForTests -- that flag exists specifically to isolate the
                // AUTOMATIC tracker's own replay behavior for testing; gating the manual path with it
                // would make it impossible to test Correct Slant's own replay in isolation.
                if (_correctSlantRequested)
                {
                    _correctSlantRequested = false;
                    // D0-audit round-10 structural fix: TryCorrectSlant() + PerformReplay() (called
                    // separately here up through round 9) are now one transaction,
                    // TryCorrectSlantAndApply() -- see its own doc comment for why a bare sequential
                    // call pair could leave TryCorrectSlant's own commits stranded on a PerformReplay
                    // bail.
                    if (!_autoStopTriggered && !_slantCorrectionsDisabledForRestOfImage && TryCorrectSlantAndApply())
                    {
                        _pendingReplayRequested = false; // subsume: this replay already covers what a same-line automatic trigger wanted

                        // Deliberately NOT setting _anyCorrectionCommittedThisImage here (auditor
                        // code-review finding, Phase 8c: flagged as undocumented, not wrong) -- that
                        // flag exists solely to arm the once-per-image origin-re-derivation latch
                        // earlier in this same loop iteration (D0-audit round-8: dropped a stale
                        // in-file line citation here), whose whole purpose is triggering ONE MORE replay pass
                        // once 16 lines have decoded. This manual commit's own PerformReplay() call
                        // immediately above already re-derived the origin at the corrected rate -- arming
                        // the latch here would only cost a second, unnecessary redraw pass later in this
                        // same image for no benefit.
                    }
                }

                // Functional-audit fix (D3+D8+D9 coupled round 1): was missing
                // `!_slantCorrectionsDisabledForRestOfImage`, unlike its two sibling entry points
                // above (the once-per-image latch, and the manual Correct-Slant drain immediately
                // above this comment -- D0-audit round-8: dropped 2 stale in-file line citations
                // here) -- both of which gate on it for the identical reason: ApplySyncCorrection
                // leaves a mid-buffer HOLE in RxLineStagingBuffer (DrainPendingSkip's own skipped
                // samples advance _consumedSamples/_rxBufferAnchorSample without ever being staged),
                // and PerformReplay must never run across that hole. The old inline comment argued
                // this drain was "structurally unreachable" while the flag is set, since it lives
                // inside ProcessSlantTrackingSample's own commit branch, which itself requires
                // `!_slantCorrectionsDisabledForRestOfImage` -- true PER LINE COMPLETION, but
                // ApplySlantTracking() can complete more than one line in a single call (per-line
                // rounding against _effectiveSamplesPerLine), so a second line's own TryAutoSync ->
                // ApplySyncCorrection can set the flag AFTER the first line's commit already set
                // _pendingReplayRequested, all within the SAME ApplySlantTracking() call -- this
                // statement would then run PerformReplay() across a hole that opened moments earlier
                // in the same loop iteration. Matching the sibling gates closes it.
                if (_pendingReplayRequested)
                {
                    _pendingReplayRequested = false;
                    if (!_autoStopTriggered && !_slantCorrectionsDisabledForRestOfImage && !SuppressAutomaticReplayForTests)
                    {
                        PerformReplay();
                    }
                }

                // Auto Stop's own trigger (TryAutoSync, port of Main.cpp:3933-3937's `RxAutoPush(TRUE)`)
                // may have fired synchronously inside the ApplySlantTracking() call just above -- applied
                // HERE, not inside TryAutoSync itself, because that method runs mid-ApplySlantTracking and
                // the statements immediately after its own call site dereference _slantTracker/
                // _syncEnvelopeDetector with no null-check; EndOfImage() nulls both, so calling it from
                // inside TryAutoSync would be a guaranteed NRE (auditor plan-review finding, round 1).
                //
                // Checked BEFORE the TryVisLockStateMachine restart check below, not after -- round-1
                // finding 3: ApplySlantTracking() (where the flag gets set) runs before that check in
                // this same iteration, so checking-and-consuming the flag immediately closes the race
                // where a freshly-committed new lock from TryVisLockStateMachine could otherwise be
                // destroyed by a stale Auto Stop flag from this same pass.
                //
                // Two known, accepted divergences from legacy's own control flow here (harmless, noted so
                // code review doesn't need to rediscover them): legacy's WriteHistory(0) call inside
                // RxAutoPush gates the history write on m_Sync captured BEFORE Stop() runs (Main.cpp:6046/
                // :6048) -- this port's unconditional DecodeRestarted invoke below is equivalent only
                // because this whole call site is unreachable except from inside the locked per-line loop
                // (m_Sync is always true here already). And legacy's own `return TRUE;` (Main.cpp:3937)
                // exits AutoStopJob before the Auto-Slant regression block ever runs for that line, but
                // ApplySlantTracking's own ProcessLine call already ran AFTER TryAutoSync earlier in this
                // same ApplySlantTracking() invocation (code-level review correction: an earlier version
                // of this comment said "before," backwards) -- harmless, since EndOfImage() below discards
                // _slantTracker entirely and InitializeSlant reconstructs it fresh on the next lock; the
                // only state that outlives the discard, _effectiveSamplesPerLine, is likewise overwritten
                // by InitializeSlant before anything can read it again.
                if (_autoStopTriggered)
                {
                    _autoStopTriggered = false;
                    restarted = true;
                    // The abandoned mode -- same local already used by the DecodeRestarted invoke below
                    // for the OTHER restart path; matches that event's existing "abandoned mode, not a
                    // new one" contract (see ISstvDecoder.cs's own DecodeRestarted doc comment).
                    RaiseSubscribers(DecodeRestarted, mode);
                    // applyDeadTime:false -- RxAutoPush's own m_SyncMode=0 (Main.cpp:6053) overrides
                    // Stop()'s own m_SyncMode=512 dead-time state (sstv.cpp:1786) immediately, skipping
                    // the 0.5s dead-time wait entirely and resuming scanning from the current sample.
                    EndOfImage(applyDeadTime: false);
                    break;
                }

                // Piece 6c: legacy's case-0 trigger (sstv.cpp:1946-1950) carries no `!m_Sync` guard on
                // the transition itself, so it keeps running even while m_Sync is true and a
                // stronger/cleaner new lock found mid-reception aborts and restarts on it (Start()
                // resets m_SyncMode back to 0 unconditionally) -- true at legacy's own shipped
                // defaults: the whole switch only runs while locked because the enclosing gate at
                // sstv.cpp:1889, `!m_Sync || m_SyncRestart || m_SyncAVT`, is satisfied by
                // m_SyncRestart defaulting to 1 (sstv.cpp:1486), a real user-toggleable option
                // now exposed as `_syncRestartEnabled` (see that field's own doc comment) -- this port
                // no longer hard-wires it on with no way to disable. `_syncRestartEnabled` stands in
                // for the whole `!m_Sync || m_SyncRestart || m_SyncAVT` gate at this call site
                // specifically: `!m_Sync` is always false here (this branch only runs while locked)
                // and this port has no `m_SyncAVT`-equivalent gating this particular call site today
                // (unverified as a further gap, not silently assumed absent -- no such flag exists
                // anywhere else in this class). Checked once per decoded line, not once per TryProcessBuffer
                // call: for a bulk-pushed buffer containing a whole (possibly truncated)
                // transmission followed immediately by a second one, the loop above would otherwise
                // just keep decoding every available sample as if it were more lines of the *first*
                // transmission -- it has no notion of "this content doesn't actually belong to this
                // image" -- and would never return control to notice the second transmission's real
                // header at all. VisLockStateMachine runs here (gated by _syncRestartEnabled, as
                // described above), not the fixed-window path (which assumes _consumedSamples is a
                // header start, not mid-image) or TrySyncIntervalDetectionStep (m_sint2 is hard-gated
                // behind !m_Sync at every call site in legacy -- case 0's shared guard, sstv.cpp:1899,
                // and its own case-1 guard, sstv.cpp:1953; m_sint3's calls at sstv.cpp:1927-1937 sit
                // inside the same case-0 :1899 guard -- they must not run while locked; round-2-review
                // fix, an earlier version of this citation pointed at m_sint1's own gates,
                // sstv.cpp:1949/1959, by mistake). Bounded to _consumedSamples (the current decode
                // position), NOT the whole buffer -- see TryVisLockStateMachine's own doc comment for
                // the bulk-vs-streaming bug this bound fixes.
                //
                // D0-audit round-11 fix: TryNarrowFskScan is hoisted OUT of the _syncRestartEnabled
                // gate below -- narrow FSK has no legacy equivalent of the sstv.cpp:1889 gate this
                // port's _syncRestartEnabled models (see TryNarrowFskScan's own doc comment: its real
                // legacy call, DecodeFSK at sstv.cpp:1858, runs unconditionally, 31 lines ABOVE that
                // gate). The old code called TryNarrowFskScan only indirectly, as TryVisLockStateMachine's
                // own first step, so gating the WHOLE TryVisLockStateMachine call here starved narrow
                // FSK of the sample stream -- and its StationIdDecoded delivery -- for an entire locked
                // image whenever the user disables Lock/Restart. TryNarrowFskScan's own commit is still
                // correctly suppressed while locked with Restart disabled (see that method's own inner
                // gate, at its `Commit(mode, anchor)` call) -- sstv.cpp:2592 is where legacy actually
                // applies `m_SyncRestart`, to the commit only, not to DecodeFSK's own per-sample scan.
                // TryVisLockStateMachine still calls TryNarrowFskScan internally too (unchanged) -- when
                // _syncRestartEnabled is true, that internal call is a harmless no-op here, since the
                // hoisted call already advanced _narrowFskProcessedUpTo to this same bound.
                if (TryNarrowFskScan(_consumedSamples) || (_syncRestartEnabled && TryVisLockStateMachine(_consumedSamples)))
                {
                    restarted = true;
                    // The abandoned (local `mode`, captured at the top of this outer-loop iteration),
                    // not the new one. Round-2-review correction (found while planning the abandoned-
                    // image-save feature): an earlier version of this comment claimed ModeDetected for
                    // the new mode always fires before this event -- wrong since piece 8c. For a
                    // non-AVT match (the common case here), Commit() defers ModeDetected until the new
                    // mode's own sync anchor resolves (_pendingAnchorCorrectionMode), which can be a
                    // LATER PushSamples call entirely -- so THIS event fires first, not second. Only
                    // an AVT match resolving within this same call (S7's Commit() -> immediate
                    // FinalizeAnchorAndStartDecoding) fires ModeDetected before this. See
                    // ISstvDecoder.cs's own DecodeRestarted doc comment for the corrected, general
                    // statement of this ordering. Passing the new mode here too (an earlier version
                    // did, via `_mode!`) is a trap review caught: a caller that allocates a buffer on
                    // ModeDetected and discards on DecodeRestarted would discard the buffer it just
                    // allocated for the new mode, not the old one it actually needs to throw away.
                    RaiseSubscribers(DecodeRestarted, mode);
                    break;
                }
            }

            if (restarted)
            {
                // Functional-audit fix (chunk D4 round 1): the old comment here claimed "_mode is
                // null" unconditionally, which is false for the common mid-reception restart path --
                // TryVisLockStateMachine's Commit() call sets _mode to the newly-matched mode (and
                // _pendingAnchorCorrectionMode), not null. Both cases are still handled correctly by
                // falling through to the top of the outer loop: the `_mode is null` branch picks up
                // a fresh header search when true, and the `_pendingAnchorCorrectionMode is not
                // null` branch (above) picks up the pending anchor otherwise -- only the STATED
                // invariant was wrong, not the control flow.
                continue;
            }

            if (_nextLine >= mode.ImageHeight)
            {
                EndOfImage();
                continue;
            }
        }
    }

    // Direct port of Stop() (sstv.cpp:1769-1791) + cases 512/513's 0.5s dead-time wait
    // (sstv.cpp:2243-2252), called once _nextLine reaches mode.ImageHeight. Modeled as an
    // analytic skip rather than a literal per-sample countdown (the same style already used for
    // fixed-duration header skips): jump straight to _consumedSamples + 0.5s worth of samples,
    // rather than simulating 0.5s of idle per-sample ticks.
    //
    // NOT a byte-exact port of WHEN Stop() fires (functional-audit finding, chunk D4 round 1, figures
    // corrected round 2 -- an earlier version of this comment understated Martin M1's gap 3.2x and
    // contradicted its own overshoot-count sentence for Scottie): legacy's own check is `m_AY >
    // SSTVSET.m_L` (Main.cpp:5014-5017), where m_AY is the row index of the last sample of the page
    // just DRAWN -- so legacy always draws/consumes at least one row index past the last valid image
    // row before calling Stop() (2 transmission lines for the y-indexed families at Main.cpp:4165, 1
    // line for Scottie's 1-based ScanLine[y-1] at Main.cpp:4152). This port's `_nextLine >=
    // mode.ImageHeight` check stops at exactly ImageHeight decoded transmission lines, zero
    // overshoot. Net effect: the 0.5s dead-time skip (and therefore next-header scanning) begins one
    // full transmission-line duration earlier than legacy for Scottie modes, or two for every other
    // family -- concretely, ~1.05s early for Scottie DX (1 line x 1050.3ms) and ~0.89s early for
    // Martin M1 (2 lines x 446.446ms; both figures independently re-derived from each mode's own
    // LineSegments in SstvModeRegistry.cs, not eyeballed) -- accepted as-is (not matched to legacy's
    // overshoot) since it causes no pixel divergence for the image just decoded (verified: legacy's
    // own overshoot lines set gp=NULL and draw nothing, Main.cpp:4156-4157/4170-4173); the only
    // residual risk is handing slightly more of the outgoing footer/FSK-ID audio to the next header
    // search than legacy would, which has no mode allowlist protecting against it either way -- and
    // the footer itself carries no 1200Hz sync structure and no 300ms+ leader for the sync-bypass or
    // VIS-lock paths to latch onto, so this is judged low-risk, not zero-analysis-accepted. Revisit
    // if a real false-lock-on-footer report ever surfaces.
    //
    // Not literally "no detectable effect either way" (an earlier version of this comment overclaimed
    // this, caught by independent review): the skip advances the resonator-fed detectors'
    // (SyncEnvelopeDetector inside SyncIntervalTracker/VisLockStateMachine) processed-up-to cursors
    // without feeding them the skipped samples, so their filter state carries over from the previous
    // image's tail rather than the 0.5s of real dead-time content legacy's own filters would have
    // settled against. In practice this resettles within ~10-30ms of resumed scanning against a
    // 300ms leader, so it's a real but small divergence, not a functional problem -- described
    // honestly rather than as literally undetectable.
    //
    // Legacy's Stop() resets m_sint1/m_sint2/m_sint3 -- so does this, plus
    // VisLockStateMachine's own equivalent of cases 0-9's logical state (case 0 is where
    // m_SyncMode lands once cases 512/513 finish) -- clearing stale history/in-progress decode
    // state from before this image locked, so it isn't mistaken for real content in the next
    // transmission's search. Each reset detector also needs a new origin sample: they were
    // previously always fed starting at absolute index 0, but now resume mid-buffer.
    // applyDeadTime=false (Auto Stop's own call site, TryProcessBuffer's per-line loop):
    // RxAutoPush's own m_SyncMode=0 (Main.cpp:6053) overrides Stop()'s own m_SyncMode=512
    // (sstv.cpp:1786) immediately after calling it, skipping cases 512/513's 0.5s dead-time
    // wait entirely -- legacy's real Auto Stop path resumes header scanning from the
    // current sample, not 0.5s later. Auditor plan-review finding: an earlier draft of
    // this method reused the unconditional dead-time skip for Auto Stop too, which would
    // have created a silent ~500ms blind spot no duration/round-trip test could surface.
    /// <param name="resumeFrom">Overrides the anchor every reset cursor/watermark below is set to.
    /// Omitted (the default) for every pre-existing call site, which keeps the original
    /// <paramref name="applyDeadTime"/>-based computation (anchored at <see cref="_consumedSamples"/>,
    /// a cursor only valid while a mode is/was locked). <see cref="RequestAbandonReception"/>'s own
    /// idle/AVT-pending branch is the one caller that supplies this explicitly, anchored at
    /// <see cref="TotalSamplesReceived"/> instead -- <see cref="_consumedSamples"/> is a frozen
    /// pre-lock header-search start that can sit behind <see cref="_bufferBase"/> once
    /// <see cref="_fixedWindowExhausted"/> stops protecting it (see that field's own doc comment),
    /// so anchoring there for a decoder that was never locked can leave every cursor below pointing
    /// at already-trimmed buffer -- the same reasoning <see cref="PerformForceMode"/>'s own idle-safe
    /// reset already anchors at <see cref="TotalSamplesReceived"/> for, not a new invariant.</param>
    private void EndOfImage(bool applyDeadTime = true, int? resumeFrom = null)
    {
        EndLiveSnr();
        var resolvedResumeFrom = resumeFrom ?? (applyDeadTime
            ? _consumedSamples + (int)Math.Round(0.5 * _sampleRate)
            : _consumedSamples);

        _mode = null;
        _lineDecoder = null;
        _pixels = null;
        _nextLine = 0;
        _bandpassLockedFromSample = int.MaxValue; // Band-1 item 4b -- see field's own doc comment
        // Round-2 D0-audit nit fix: defensive clear, matching AbandonInProgressImage's own identical
        // one (see that method's doc comment for the full reasoning) -- safe today by reachability
        // alone (TryProcessBuffer always clears this before the per-line loop that can reach
        // EndOfImage), but costs nothing and keeps both `_mode is null` teardown paths consistent.
        _pendingAnchorCorrectionMode = null;

        _afcTracker = null;
        // Demod-type subsystem Phase 2, landmine #3 -- legacy's Stop() resets m_fqc back to wide too
        // (sstv.cpp:1781 Clear(), :1790 SetWidth(0)) -- same reasoning as InitializeAfc's own
        // SetWidth/Clear pair (this port's EndOfImage is the Stop() analogue). Order (SetWidth then
        // Clear, not legacy's own Clear-then-SetWidth at this specific call site) doesn't matter here:
        // Clear() always resets to the EXACT ZEROFQ-denormalized value for whichever width is active
        // at the moment it runs, and SetWidth's own rescale is an algebraic identity when composed
        // with that -- both orders converge to the identical final state.
        _afcZeroCrossingCounter.SetWidth(isNarrow: false);
        _afcZeroCrossingCounter.Clear();
        // Round-1 D0-audit finding: _zeroCrossingDemodulator (the main-demod-role instance) was
        // missing this same reset. Legacy's single m_fqc instance serves both roles, so its real
        // Stop()-side SetWidth(0)/Clear() (sstv.cpp:1781,1790) implicitly resets whichever port-side
        // instance is acting as the main demodulator too -- this port's split into two instances
        // means each needs its own explicit reset. Reachable whenever DemodType.ZeroCrossing is
        // selected; without a Clear() here, a stale interval estimate/phase from the previous image
        // could leak into the next image's first demodulated samples, exactly the leak
        // ZeroCrossingFrequencyCounter.Clear()'s own doc comment says must never happen. Round-2
        // D0-audit correction: round 1's own fix ALSO called SetWidth here, mirroring
        // _afcZeroCrossingCounter's pair above -- but unlike that sibling, _zeroCrossingDemodulator's
        // width is independently owned by the edge-tracked _mainPathIsNarrow flag (see
        // DemodulatedFrequencyAt's own `isNarrow != _mainPathIsNarrow` check), which this out-of-band
        // call did not update -- desyncing the flag from the object's real width state and letting a
        // later edge-check wrongly skip a needed SetWidth call. Clear() alone is sufficient: it
        // always resets to whatever width is CURRENTLY active (correct, since _mainPathIsNarrow was
        // never touched), and the very next real width transition self-corrects normally, same as
        // before this whole D0 chunk existed -- rescaling an already-cleared value through SetWidth's
        // own algebraic identity lands on the identical final state either way (verified both
        // directions). No SetWidth call belongs here.
        _zeroCrossingDemodulator.Clear();
        _syncEnvelopeDetector = null;
        // §5.3: legacy's Stop() half of InitTone(0)'s reset (sstv.cpp:1769-1791's outer `if(m_AFCFQ)`
        // guard) -- only for the `m_fskdecode == 0` branch; legacy deliberately leaves m_iir19/m_iirfsk
        // retuned when FSK-ID RX decode is on, to catch a trailing station-ID burst on the same offset
        // carrier (code-review round-1 finding). This port doesn't model that carve-out -- always
        // resets all 4 -- a pre-existing gap (these detectors were already nominal on this path before
        // §5.3), not a regression, and small in practice (dfq is single-digit Hz vs 100Hz bandwidth);
        // noted here rather than silently diverging from what this comment used to claim.
        // _lastAppliedAfcRetuneHz itself is reset inside InitializeAfc, not here (this method runs
        // before that), so this gate correctly skips the reset on an image that never triggered AFC --
        // nulled below too so a stale non-null value can't survive to be misread as "still retuned."
        if (_lastAppliedAfcRetuneHz is not null)
        {
            _visLockStateMachine.Retune(0);
            _visDataD19Detector.Retune(0);
            _fskSpaceDetector.Retune(0);
            _visDataD12Detector.Retune(0);
            _lastAppliedAfcRetuneHz = null;
        }
        _slantTracker = null;
        ResetReSyncState(); // legacy's Stop()-side m_Skip = 0, sstv.cpp:1789

        _avtTrainingPending = false;
        _avtTrainingLock = null;
        _avtPllDemodulator = null;

        _fixedWindowExhausted = false;

        _syncBypass1Tracker.Reset();
        _syncBypassTracker.Reset();
        _syncBypassNarrowTracker.Reset();
        _syncBypassNarrowPhaseActive = false;
        _syncBypassProcessedUpTo = resolvedResumeFrom;
        _syncBypassOriginSample = resolvedResumeFrom;

        _visLockStateMachine.Reset();
        _visLockProcessedUpTo = resolvedResumeFrom;
        _visLockOriginSample = resolvedResumeFrom;

        _consumedSamples = resolvedResumeFrom;
        _idealLineStartSample = resolvedResumeFrom; // MUST 4 -- see field's own doc comment

        _agcDeadZoneCatchUpTarget = resolvedResumeFrom;
    }
}
