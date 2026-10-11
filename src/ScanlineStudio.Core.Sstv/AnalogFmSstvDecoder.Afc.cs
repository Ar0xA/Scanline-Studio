using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

public sealed partial class AnalogFmSstvDecoder
{
    // Direct port of CSSTVDEM::SyncFreq's setup (InitAFC/SetSampFreq, sstv.cpp) -- legacy explicitly
    // excludes AVT from AFC entirely (sstv.cpp:2258's `SSTVSET.m_Mode != smAVT` guard at the call
    // site), so this leaves _afcTracker null for it. Sync target/band/BWH switch on the MN/MC
    // narrow family (NARROW_SYNC=1900/NARROW_AFCLOW=1800/NARROW_AFCHIGH=1950/NARROW_BWH=128 vs.
    // normal 1200/1000/1325/400); AFCB/AFCW timing switches on a different, overlapping grouping
    // (SstvModeRegistry.IsFastAfcGroup) -- see AfcTracker's doc comment for both.
    //
    // Legacy's real AFC source depends on m_Type (sstv.cpp:2255-2269): case 0/PLL feeds SyncFreq from
    // m_fqc.Do(...) (the zero-crossing counter, independent of the picture demodulator's own output);
    // cases 1/2 (zero-crossing/Hilbert) feed SyncFreq from the SAME `d` already used for the picture
    // stream. Demod-type subsystem Phase 2: _afcZeroCrossingCounter (constructed unconditionally in
    // the ctor, see that field's own doc comment) is retuned/cleared below regardless of which type
    // is selected -- it stays fully inert whenever _demodType != Pll, since ApplyAfcCorrections only
    // ever reads from it in that case. (This doc comment previously claimed a ZeroCrossingFrequencyCounter
    // was "no longer constructed here" at all, correct only while Hilbert was this port's sole live
    // main-path type -- now stale, corrected here.)
    private void InitializeAfc(SstvModeDefinition mode)
    {
        // Math.Max, not a bare assignment -- bug found by independent review. A mid-reception
        // restart's new anchor can land *before* wherever the abandoned transmission's own AFC pass
        // had already processed up to (see ApplyAfcCorrections' updated doc comment for why that
        // range can be large); rewinding _afcProcessedUpTo backward into it would let the new
        // AfcTracker re-correct samples the old one already corrected, doubly applying two different
        // trackers' corrections to the same content. Same defensive pattern already used for
        // _visLockProcessedUpTo in Commit(). A no-op for every non-restart Commit() (the new anchor
        // is always >= wherever AFC had gotten to in that case).
        _afcProcessedUpTo = Math.Max(_afcProcessedUpTo, _consumedSamples);

        // Demod-type subsystem Phase 2, landmine #3 -- CSSTVDEM::SetWidth retunes m_fqc
        // UNCONDITIONALLY regardless of m_Type (sstv.cpp:1707-1715), and legacy's real Start() calls
        // SetWidth BEFORE Clear() (sstv.cpp:1719,1722), itself unconditional -- not gated on AVT or
        // any AFC-enabled toggle (those don't exist as concepts inside Start() at all). Round-1
        // code-review finding: an earlier version of this method placed these two calls AFTER the
        // AVT/!_afcEnabled early return below, correct only by argument (the counter is only ever
        // READ when _afcTracker is non-null AND _demodType==Pll, both of which this early return
        // already excludes) rather than by construction -- moved here, before the early return, to
        // match legacy's real unconditional placement and remove that argument-shaped invariant.
        _afcZeroCrossingCounter.SetWidth(mode.NarrowModeCode is not null);
        _afcZeroCrossingCounter.Clear();
        // Round-1 D0-audit finding: same Start()-side reset applied to _zeroCrossingDemodulator (the
        // main-demod-role instance) -- see EndOfImage's own matching comment for the full reasoning,
        // including round 2's correction removing the SetWidth half (kept only here as Clear()).
        // Round-2 D0-audit note, not fixed (bounded): unlike _afcProcessedUpTo two lines above,
        // _demodulatedFrequenciesProcessedUpTo has no equivalent force-advance-to-anchor step here
        // (adding one would reopen Band-1 item 4a's own pre-anchor-cache reasoning) -- this Clear()
        // can therefore apply to a cursor that still trails the anchor by up to
        // TrimBuffers' preLockRetentionSamples (~16k samples @11025Hz) and then goes on to process
        // chronologically-EARLIER audio than the clear point. The 3rd-order 900Hz output filter
        // re-settles within a few samples either way, and none of that pre-anchor cache content is
        // ever read as picture data -- bounded, not chased further.
        _zeroCrossingDemodulator.Clear();

        // §5.3: legacy's Start() -> InitAFC() -> InitTone(0) resets all 5 AFC-retuned resonators to
        // nominal on EVERY fresh lock, including a mid-reception restart (sstv.cpp:2592-2594/2237/
        // 2241) -- placed here, unconditionally, before the AVT/!_afcEnabled early return below, same
        // reasoning as _afcZeroCrossingCounter two lines up. This is the fix for a real leak: this
        // method is also reached via Commit() -> FinalizeAnchorAndStartDecoding once a mid-reception
        // restart's new anchor resolves, a path that never touches EndOfImage -- without this reset,
        // these detectors would stay mistuned from the PREVIOUS lock's drift exactly while the new
        // AfcTracker is fresh and hasn't re-locked yet. EndOfImage resets the same 4 for legacy's
        // Stop() half; both are needed.
        _visLockStateMachine.Retune(0);
        _visDataD19Detector.Retune(0);
        _fskSpaceDetector.Retune(0);
        _visDataD12Detector.Retune(0);

        // AVT's own exclusion is real legacy behavior (see this method's own doc comment); the
        // !_afcEnabled branch is this port's new settings-driven toggle, layered on top without
        // changing AVT's case -- both land on the identical "_afcTracker stays null" outcome every
        // existing AFC consumer already null-checks for (see ApplyAfcCorrections).
        if (mode == SstvModeRegistry.Avt || !_afcEnabled)
        {
            _afcTracker = null;
            _lastAppliedAfcRetuneHz = null;
            return;
        }

        CreateAfcTracker(mode);
    }

    /// <summary>Builds a fresh <see cref="AfcTracker"/> for <paramref name="mode"/>. Factored out of
    /// <see cref="InitializeAfc"/> so <see cref="ApplyAfcEnabledEdge"/>'s ON edge builds an
    /// identically-configured tracker instead of a second copy of the band table.</summary>
    private void CreateAfcTracker(SstvModeDefinition mode)
    {
        var isNarrow = mode.NarrowModeCode is not null;
        var (syncTargetHz, bandLowHz, bandHighHz, bandwidthHalfHz) = isNarrow
            ? (1900.0, 1800.0, 1950.0, 128.0)
            : (1200.0, 1000.0, 1325.0, 400.0);
        var (afcBeginMs, afcWidthMs) = SstvModeRegistry.IsFastAfcGroup(mode) ? (1.0, 2.0) : (1.5, 3.0);

        _afcTracker = new AfcTracker(_sampleRate, syncTargetHz, bandLowHz, bandHighHz, afcBeginMs, afcWidthMs, bandwidthHalfHz);
        _lastAppliedAfcRetuneHz = null; // fresh lock cycle -- see ApplyAfcCorrections' retune step
    }

    // Legacy applies AFC in the same single per-sample pass as the main demod ("if(m_Sync) d +=
    // m_AFCDiff" right after m_hill.Do(...), sstv.cpp:2255-2270 -- case 2/Hilbert, this port's real
    // main-path demodulator as of the Hilbert demodulator port). This decoder demodulates samples
    // upfront (PushSamples), before mode detection can know whether/how AFC should apply to them --
    // so this instead corrects the already-demodulated buffer in place, in a separate pass, once the
    // mode (and therefore the AFC parameters) are known. This is a deferred, not an approximated,
    // adaptation: the AFC state machine below still sees the exact same already-demodulated values,
    // in the exact same order, that legacy's own SyncFreq(d) call would have -- only the wall-clock
    // timing of *when* that processing happens (relative to VIS decode) differs.
    //
    // Bounded by upperBoundSample AND _afcBoundSample, NOT _demodulatedFrequencies.Count: never
    // correct samples beyond this image's own generous nominal extent -- otherwise a single
    // TryProcessBuffer call (a bulk PushSamples caller in particular) would eagerly correct straight
    // through this image's footer/dead-zone and into a not-yet-detected *next* transmission's audio
    // using a correction tuned to this image's own frequency offset, corrupting it before that
    // transmission's own Commit() even runs -- the same category of bulk-vs-streaming ordering bug
    // already documented on ApplySlantTracking, but for AFC specifically only became reachable once
    // EndOfImage made a second Commit() within one decoder instance possible at all.
    //
    // upperBoundSample itself fixes a second, related bug found by independent review: this used to
    // have no per-call bound at all, always racing ahead to _afcBoundSample in one shot the moment a
    // transmission was committed, *before* any of its lines were actually decoded. Harmless for a
    // transmission that completes normally (a causal, resumable tracker produces the same correction
    // values regardless of when the eager pass ran relative to decode), but for one that gets
    // abandoned mid-reception (piece 6c's restart path), it meant the abandoned transmission's own
    // AFC tracker could have already "corrected" content that turns out to belong to the *real*
    // second transmission -- not bounded to "at most one line" the way an earlier note here assumed
    // (spec/14-roadmap.md), but up to the *entire* first image's nominal extent, since the eager pass
    // ran once, upfront, independent of how far line-decoding had actually progressed. Callers now
    // pass the current line's own end sample (mirroring ApplySlantTracking's existing per-line
    // bound), so a restart detected after line K can only ever have over-corrected up through
    // roughly that same line -- restoring the "at most one line" property this method's callers had
    // assumed was already true. Combined with InitializeAfc's Math.Max fix (which stops a restart's
    // new anchor from rewinding into whatever that bounded range already covered), this closes the
    // double-correction path rather than just documenting it honestly.
    private void ApplyAfcCorrections(int upperBoundSample)
    {
        if (_afcTracker is null)
        {
            return;
        }

        var bound = Math.Min(Math.Min(TotalSamplesReceived, _afcBoundSample), upperBoundSample);
        for (; _afcProcessedUpTo < bound; _afcProcessedUpTo++)
        {
            // `m_afc` is legacy's user toggle (default 1, sstv.cpp:1471), modelled by _afcEnabled:
            // while it is off this method never runs at all, because ApplyAfcEnabledEdge nulls
            // _afcTracker. AVT's exclusion lands on the same null-tracker outcome, see
            // InitializeAfc. Corrected (ultracode audit finding #4): the `m_CurMax > 16` gate wraps
            // ONLY the SyncFreq *update* (sstv.cpp:2258/2263/2267) -- the standing correction itself,
            // `d += m_AFCDiff` (sstv.cpp:2270), is a SEPARATE, unconditional statement applied to
            // every sample regardless of gate state (see the unconditional add below, outside every
            // branch).
            var gated = AgcCurMaxAt(_afcProcessedUpTo) > 16.0;

            // DemodulatedFrequencyAt must run EVERY iteration regardless of demod type, for two
            // reasons: (1) it's what the standing correction below is added to -- the picture-path
            // value is always the correction TARGET, even for PLL (case 0), where legacy's own
            // `d += m_AFCDiff` (sstv.cpp:2270) applies to the PICTURE stream's own `d`, not to the
            // separate m_fqc measurement; (2) it forward-fills _demodulatedFrequencies as a side
            // effect (see that method's own doc comment) -- skipping this call for PLL samples would
            // leave the list not yet filled up to _afcProcessedUpTo by the time the unconditional
            // index below runs (a real bug an earlier version of this method had, caught by
            // GoldenVectorTests.cs's own PLL-arm test throwing IndexOutOfRangeException immediately).
            var pictureFrequencyHz = DemodulatedFrequencyAt(_afcProcessedUpTo);

            // Demod-type subsystem Phase 2, landmine #2 -- AFC's frequency-MEASUREMENT source (as
            // opposed to the correction TARGET above, which is always the picture stream) forks by
            // demod type, matching legacy's real per-case behavior exactly (sstv.cpp:2256-2269):
            if (_demodType == DemodType.Pll)
            {
                // case 0/PLL (sstv.cpp:2257-2258): SyncFreq is fed from a SEPARATE, independently-
                // running zero-crossing counter (`m_fqc.Do(m_lvl.m_Cur)`), NOT the picture path's own
                // PLL output -- and that call is itself INSIDE the gate (unlike cases 1/2 below,
                // where the picture-path `d = <demod>.Do(...)` call is unconditional and only
                // SyncFreq(d) is gated). Reintroduces _afcZeroCrossingCounter, dormant since Hilbert
                // became the sole live main-path demod.
                if (gated)
                {
                    var measuredFrequencyHz = _afcZeroCrossingCounter.ProcessSample(BandpassFilteredSampleAt(_afcProcessedUpTo) * 32768.0);
                    ApplyGatedAfcUpdate(measuredFrequencyHz);
                }
            }
            else
            {
                // cases 1/2 (Zero-crossing/Hilbert as main path, sstv.cpp:2261-2268): SyncFreq feeds
                // from the SAME `d` already used for the picture stream -- unchanged from this
                // method's pre-Phase-2 behavior. Reads the pre-correction picture value -- matching
                // legacy's exact sequencing, where SyncFreq(d) is called with the pre-correction `d`,
                // and `d += m_AFCDiff` happens afterward (sstv.cpp:2270).
                if (gated)
                {
                    ApplyGatedAfcUpdate(pictureFrequencyHz);
                }
            }

            // Functional-audit re-confirmation (D7, round 1): unconditional by design -- matches
            // legacy's own unconditional `d += m_AFCDiff` (sstv.cpp:2270) outside the `m_CurMax > 16`
            // gate that only wraps the MEASUREMENT update above (ApplyGatedAfcUpdate/the ZC-counter
            // branch). A prior ultracode review finding moved this line outside that gate for exactly
            // this reason; reverting it back inside would silently stop applying the STANDING
            // correction during any quiet/weak stretch, even though `_afcTracker.CorrectionHz` itself
            // is still valid from the last real lock. No test currently asserts this specifically
            // (would need a real signal with a deliberate quiet stretch after AFC has already locked
            // in a correction, distinguishing "correction still applied, just not re-measured" from
            // "correction paused too") -- deliberately not constructed this round for the same budget
            // reason as this chunk's other deferred test gaps; flagged so a future revert of this
            // line's position isn't silently accepted by the existing suite.
            _demodulatedFrequencies[Rel(_afcProcessedUpTo)] += _afcTracker.CorrectionHz;
        }
    }

    /// <summary>The gated half of <see cref="ApplyAfcCorrections"/>'s per-sample work -- feeds
    /// <see cref="_afcTracker"/> and retunes the sync-envelope resonator, shared by both demod-type
    /// branches (only the frequency-source SIDE differs per type, this update logic doesn't).
    /// <paramref name="measuredFrequencyHz"/> must already be the correct per-type measurement (the
    /// AFC-dedicated zero-crossing counter's output for PLL, or the picture path's own output for
    /// Zero-crossing/Hilbert) -- this method doesn't know or care which.</summary>
    private void ApplyGatedAfcUpdate(double measuredFrequencyHz)
    {
        _afcTracker!.ProcessSample(measuredFrequencyHz);

        // ultracode audit finding #1: legacy's InitTone retunes the sync-envelope tone resonator
        // (m_iir12/19) on every SyncFreq lock update (sstv.cpp:1695-1705, called from
        // sstv.cpp:2362) -- only while synced, which this whole per-line decode loop already
        // implies (ApplyAfcCorrections only runs while a mode is locked and being decoded). Retune
        // only when the correction actually changed, matching legacy's own call cadence (InitTone
        // fires once per lock event, not once per gated sample).
        //
        // Milestone-audit fix: legacy's dfq = m_AFCDiff * m_AFC_BWH moves the resonator ONTO the
        // actually-received tone (SetFreq(1200+dfq) ~= measured frequency) -- the opposite direction
        // from CorrectionHz, which is designed to pull a MEASUREMENT back toward nominal (added to
        // the demodulated stream by ApplyAfcCorrections' caller). Using CorrectionHz directly here
        // retuned the resonator away from the signal by twice the real offset. dfq is the negation
        // of CorrectionHz: verified by hand (measured=1210Hz, syncTarget=1200Hz -> CorrectionHz=
        // -13.125, so the resonator should move to 1200+13.125=1213.125 (the real received
        // frequency, +3.125's calibration nudge) -- i.e. 1200 + (-CorrectionHz).
        var dfq = -_afcTracker.CorrectionHz;
        if (_lastAppliedAfcRetuneHz != dfq)
        {
            _lastAppliedAfcRetuneHz = dfq;
            _syncEnvelopeDetector?.Retune(dfq);

            // §5.3: legacy's InitTone retunes all 5 resonators together (m_iir11/12/13/19/fsk), not
            // just the sync-envelope one -- these 4 are genuinely read post-lock (VisLockStateMachine's
            // mid-reception re-verification path; _visDataD19Detector/_fskSpaceDetector's dual pre/
            // post-lock use in TryNarrowFskScan; _visDataD12Detector's post-lock scope-capture feed).
            // Deliberately NOT retuning _visDataD11Detector/_syncBypass1200/1900/FskDetector here --
            // those are read ONLY pre-lock and stay `readonly` for the decoder's whole lifetime, so
            // retuning them would bias the NEXT image's header search instead of helping this one.
            _visLockStateMachine.Retune(dfq);
            _visDataD19Detector.Retune(dfq);
            _fskSpaceDetector.Retune(dfq);
            _visDataD12Detector.Retune(dfq);
        }
    }
}
