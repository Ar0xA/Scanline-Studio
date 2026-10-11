using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

public sealed partial class AnalogFmSstvDecoder
{
    /// <summary>See <see cref="ISstvDecoder.BufferedSampleCount"/> -- the number of samples
    /// currently physically held in <c>_rawSamples</c> (i.e. after trimming, NOT
    /// <see cref="TotalSamplesReceived"/>). Mirrors <c>MiniAudioCaptureSession.OverrunCount</c>'s
    /// own shape -- a raw number for a caller/test to interpret, not a verdict. Originally
    /// diagnostic-only (so <see cref="TrimBuffers"/>'s bound could be verified -- a long,
    /// never-locking stream should NOT grow this linearly with total samples pushed), now also a
    /// real public telemetry surface -- both uses read the exact same field, no behavior
    /// difference.</summary>
    public int BufferedSampleCount => _rawSamples.Count;

    /// <summary>Diagnostic-only: combined physical length of the persistent VIS-bit/narrow-mode-header
    /// detector caches added for Band-2 item S5 (<see cref="D11At"/>/<see cref="D12At"/>/<see cref="D19At"/>)
    /// and item S14 (<see cref="FskSpaceAt"/>). Exists because an auditor code-level review of S5's plan
    /// flagged a real test gap: <see cref="BufferedSampleCount"/> only tracks <c>_rawSamples</c>, so these
    /// new <c>List&lt;double&gt;</c>s silently failing to trim (the exact bug class Band-1 item 2/4a each
    /// hit once already, for different cursors) would have passed
    /// <c>BufferedSampleCount_StaysBounded_ForLongNeverLockingStream</c> without this. S14 extends the
    /// same property to its own new cache rather than adding a parallel diagnostic.</summary>
    internal int VisDataDetectorBufferedSampleCount => _visDataD11Samples.Count + _visDataD12Samples.Count + _visDataD19Samples.Count + _fskSpaceSamples.Count;

    /// <summary>Diagnostic-only: how far the persistent D11 tone-detector cache's forward-fill cursor
    /// has advanced. Band-2 item S5 -- an auditor code-level review (round 4) noted that, unlike
    /// <see cref="VisDataDetectorBufferedSampleCount"/> (which guards memory boundedness), nothing
    /// directly pins the actual FIDELITY property S5 exists for: that this detector is fed
    /// continuously from early in the stream, not cold-started at each decode attempt's own
    /// <c>headerStart</c>. <c>LegacyDerivedSpansTests</c> uses this to close that gap.</summary>
    internal int VisDataD11ProcessedUpTo => _visDataD11ProcessedUpTo;

    /// <summary>Diagnostic-only: how far the persistent D19 tone-detector cache's forward-fill cursor
    /// has advanced. Band-2 item S14 -- an auditor code-level review noted that, while
    /// <see cref="VisDataDetectorBufferedSampleCount"/> pins memory boundedness, nothing pinned the
    /// actual reuse decision this item made: that <see cref="TryDecodeNarrowModeHeader"/> reads the
    /// SAME <see cref="D19At"/> cache <see cref="TryDecodeVisDataBits"/> already used, rather than a
    /// fourth cold-started 1900Hz detector. A narrow-mode-only decode (no VIS data-bit path ever runs)
    /// advancing this cursor is exactly the property a revert to a separate detector would break.</summary>
    internal int VisDataD19ProcessedUpTo => _visDataD19ProcessedUpTo;

    /// <summary>Diagnostic-only: how far the persistent D12 tone-detector cache's forward-fill
    /// cursor has advanced. Un-stub-RX-tab Piece B code-review regression test: proves an armed
    /// Decoder Trace capture's backlog-drain step actually caught this cursor up to
    /// <see cref="_levelAgcProcessedUpTo"/> at arm time, rather than dumping a stale backlog into
    /// the freshly-armed buffer on the next push.</summary>
    internal int VisDataD12ProcessedUpTo => _visDataD12ProcessedUpTo;

    /// <summary>Diagnostic-only: how far the persistent FSK-space tone-detector cache's forward-fill
    /// cursor has advanced. Band-2 item S14 -- same fidelity gap <see cref="VisDataD11ProcessedUpTo"/>
    /// closes for S5, applied to the new detector this item adds.</summary>
    internal int FskSpaceProcessedUpTo => _fskSpaceProcessedUpTo;

    /// <summary>Diagnostic-only: the absolute sample index AVT's dedicated PLL warm-up starts from
    /// (Band-2 item S16) and the training origin it warms up TO. Exposed together so a test can pin
    /// the derived span directly (<c>AvtTrainingOriginSample - AvtPllWarmupStartSample</c> is a pure
    /// constant, independent of where in the stream the header actually started) rather than relying
    /// solely on the doc comment at <see cref="TryStartAvtTraining"/>'s own call site.</summary>
    internal int AvtPllWarmupStartSample => _avtPllWarmupStartSample;

    internal int AvtTrainingOriginSample => _avtTrainingOriginSample;

    /// <summary>Diagnostic-only: how far the shared bandpass cache's forward-fill cursor has advanced.
    /// Kept as permanent test infrastructure (Band-1 item 4a, pre-Phase-2 audit) -- see
    /// <see cref="LockAnchorCommitted"/>'s own doc comment and
    /// <c>BandpassCacheChunkInvarianceTests</c> for what this measures and why it matters: item 4a's
    /// whole point was to stop this cursor racing arbitrarily far ahead of the true lock anchor before
    /// <c>Commit()</c> gets a chance to run, so the gap between the two staying small and chunk-size-
    /// invariant is this fix's own acceptance criterion, not just a nice-to-have number.</summary>
    internal int BandpassFilteredProcessedUpTo => _bandpassFilteredProcessedUpTo;

    // Code-review finding (Band-1 S2 fix, pre-Phase-2 audit): TrimBuffers bounds MEMORY but not this
    // absolute sample-index space, which is `int` -- _bufferBase + _rawSamples.Count overflows after
    // ~13.5h of continuous streaming @44100Hz (~54h @11025Hz). Widening every one of this class's
    // absolute-index fields to `long` was tried as a fix (ultracode audit finding #34) and rejected --
    // plan-readiness review found the coordinate space escapes into IScanlineDecoder/PixelSampleReader
    // and VisLockStateMachine has its own internal unbounded counter, a much larger blast radius than
    // this class alone. Fixed instead by RestartableSstvDecoder (same project), which periodically
    // discards and reconstructs the whole object graph while IsIdle -- a fresh instance can never
    // overflow, by construction, without needing to enumerate every absolute-index field anywhere in
    // the pipeline. Bumped to `internal` (stays `int`) so that wrapper can read it directly instead of
    // tracking a second, parallel counter that could desync from this one.
    internal int TotalSamplesReceived => _bufferBase + _rawSamples.Count;

    // Functional-audit fix (chunk D1, round 1): was `private`, with the throw guard's correctness
    // protected only by the doc comment above (which itself calls this "a `checked`-style guard, not
    // just a convenience") -- nothing exercised the below-base-index throw path directly. Bumped to
    // `internal` (stays otherwise unchanged) so RelThrows_WhenAbsoluteIndexIsBehindTheTrimWatermark
    // can call it directly, same "internal for direct testability" convention already established by
    // this class's other diagnostic-only members (see e.g. FirstLockedBandpassIndex's own doc
    // comment).
    internal int Rel(int absoluteIndex)
    {
        if (absoluteIndex < _bufferBase)
        {
            throw new InvalidOperationException(
                $"Absolute sample index {absoluteIndex} was requested but the buffer has already been trimmed up to {_bufferBase} -- " +
                "some cursor read behind the trim watermark, which TrimBuffers' own retention rule is supposed to prevent.");
        }

        return absoluteIndex - _bufferBase;
    }

    // sstv.cpp:1834-1839: m_lvl.Do(d); ad = m_lvl.AGC(d); d = clamp(ad*32, +-16384). Scale bridge --
    // see LevelAgc's own doc comment: legacy's d is int16-valued, this port's raw samples are float in
    // [-1.0, 1.0] (spec/05-audio-engine.md:44) -- multiply by 32768.0 before handing to LevelAgc so
    // every constant inside that class stays literally identical to legacy's own. Computed at most
    // once per index regardless of call order between the several independent cursors that read this
    // (TrySyncIntervalDetectionStep's, TryVisLockStateMachine's, ApplySlantTracking's) -- each just asks
    // for whatever index it's currently at; the cache fills forward monotonically the first time any
    // of them reaches a new index.
    //
    // Piece A/B: the `d` fed into m_lvl.Do here is legacy's real POST-2-tap-LPF-POST-bandpass-filter
    // value (sstv.cpp:1824-1834) -- BandpassFilteredSampleAt applies both filters before the existing
    // AGC+scale+clip logic below, which was already correct.
    private double AgcSampleAt(int index)
    {
        for (; _levelAgcProcessedUpTo <= index; _levelAgcProcessedUpTo++)
        {
            var scaled = BandpassFilteredSampleAt(_levelAgcProcessedUpTo) * 32768.0;
            _levelAgc.Do(scaled);
            _levelAgc.Fix();
            var ad = _levelAgc.Agc(scaled) * 32.0;
            // S11 fix (spec/14-roadmap.md): _agcSamples now stores this UNCLIPPED (the clamp moved to
            // this method's own return statement below) so AvtPllSampleAt can recover legacy's real
            // pre-clip `ad` value exactly, by dividing back out the *32 -- see that method's own doc
            // comment. Every EXISTING reader of this cache (via this method's return value) sees no
            // behavior change: the clamp still applies at the same point relative to every caller.
            _agcSamples.Add(ad);
            _agcCurMaxSamples.Add(_levelAgc.CurMax);
        }

        return Math.Clamp(_agcSamples[Rel(index)], -16384.0, 16384.0);
    }

    // S11 fix (spec/14-roadmap.md): legacy's real AVT training PLL is fed `ad` directly (`sstv.cpp`
    // cases 3-7's own `m_pll.Do(ad)`, e.g. :2129/2159/2169/2187/2222) -- the AGC output BEFORE the
    // *32 scale-up AND the ±16384 clip (`sstv.cpp:1835-1839`: `double ad = m_lvl.AGC(d); d = ad*32;`
    // clipped -- TWO SEPARATE variables). Every OTHER envelope-detector consumer in this file
    // (D11At/D12At/D19At/FskSpaceAt, the sync-bypass detectors, VisLockStateMachine's own feed) reads
    // AgcSampleAt directly because they all correctly want legacy's `d` (the *32'd, clipped value) --
    // the AVT PLL is the ONE consumer that genuinely needs the pre-scale, unclipped `ad` instead, not
    // an oversight that AgcSampleAt itself should be "fixed" to match. Auditor plan-review: an earlier
    // draft approximated this as AgcSampleAt(w)/32.0 (dividing the ALREADY-clipped value back down) --
    // wrong, and backwards on its own severity claim: `|ad|` peaks at ~16384 by construction
    // (`m_agc = 16384.0/m_CurMax`), so the *32'd/clipped domain is saturated at ±16384 for roughly 98%
    // of every cycle at normal amplitude, not "rarely" -- dividing that back down would feed the PLL a
    // hard-limited ±512 square wave, not a scaled copy of the real analog-ish `ad` waveform. This
    // method instead reuses the SAME underlying _agcSamples cache (now storing the unclipped *32'd
    // value, see AgcSampleAt's own comment) and divides back out only the *32 term, never the clip --
    // exact in every case, not an approximation, and needs no new cursor/list/TrimBuffers entry since
    // it's the same cache AgcSampleAt already maintains.
    //
    // Expected effect on measured AVT accuracy: none within the fixture's own natural measurement
    // noise -- PllFmDemodulator's own internal AGC (see its class doc comment) normalizes input
    // amplitude every half-cycle, making it scale-invariant to any consistent input multiplier far
    // above its own ~1.0 floor; both the old (BandpassFilteredSampleAt*32768) and new (AvtPllSampleAt)
    // feeds are the SAME underlying filtered signal, differing only by a slowly-varying scalar
    // (m_agc/_levelAgc's own gain, updated every ~100ms) that PllFmDemodulator's own AGC already
    // divides back out. This is a fidelity fix (matching legacy's real signal-domain choice exactly)
    // for the AGC STAGE ONLY, not an accuracy fix -- stated honestly so it isn't later "corrected"
    // back on a false assumption that a measured-delta improvement was expected and didn't appear.
    // Code-level review (S7/S11/S17 batch) flagged the natural follow-on question: the UPSTREAM
    // bandpass stage still diverges during AVT training -- legacy selects H1 whenever
    // `m_Sync || m_SyncMode >= 3` (`sstv.cpp:1827`, true throughout AVT training's SyncMode 4-7), but
    // BandpassFilteredSampleAt gates purely on `_mode is not null`, so this port runs H2/search for the
    // whole training window instead. Pre-existing, already tracked as its own separate gap
    // (SearchBandpassFilter.cs's own doc comment), not introduced or closed by this fix -- the AGC
    // domain match above is real and exact, just not the full chain.
    private double AvtPllSampleAt(int index)
    {
        AgcSampleAt(index); // ensures _agcSamples is filled up to index; its own (clamped) return value is not what this needs
        return _agcSamples[Rel(index)] / 32.0;
    }

    // sstv.cpp:1824-1825 -- always-on, never gated by m_bpf. m_ad zeroed once at construction
    // (sstv.cpp:1417), never reset in Start()/Stop() (confirmed by reading both) -- matches this
    // port's existing continuously-running-filter precedent (LevelAgc/CLVL, SyncEnvelopeDetector).
    // Pure function of _rawSamples (no adaptive state -- unlike AGC, calling this independently from
    // multiple sites gives bit-identical results, no shared cache/streaming object needed). Returns
    // double, matching legacy's own double-domain arithmetic exactly (casting the first operand to
    // double before adding promotes the whole expression, avoiding an avoidable float-precision
    // rounding step legacy's real computation never has).
    private double FilteredRawSampleAt(int index) =>
        index > 0 ? ((double)_rawSamples[Rel(index)] + _rawSamples[Rel(index - 1)]) * 0.5 : _rawSamples[Rel(index)] * 0.5;

    // Piece: pre-AGC bandpass filter, H1 (locked) once locked / H2 (search) otherwise -- see
    // SearchBandpassFilter's own doc comment for the full scope decision and the causal-window/
    // group-delay reasoning. Chains onto FilteredRawSampleAt exactly as legacy chains m_BPF.Do onto
    // its own 2-tap LPF output (sstv.cpp:1824-1833), applied at the same 4 sites piece 15 already
    // touches. Forward-fill CACHED, feeding SearchBandpassFilter's own streaming, one-sample-at-a-time
    // API in strict index order (unlike FilteredRawSampleAt, which stays a cheap stateless
    // recomputation) -- the O(tap) convolution (up to 97 taps at 44100Hz) is expensive enough that an
    // earlier stateless-window-lookup version was measured to cause a real ~4x full-suite slowdown
    // (delegate-call overhead plus redundant re-reads of overlapping windows from multiple call sites --
    // AgcSampleAt, the main demodulator feed, both AVT sites -- that frequently request the SAME index).
    // Mirrors _agcSamples' own established forward-fill pattern: computed at most once per index
    // regardless of call order.
    //
    // Band-1 item 4b / H3 item (decoder_quality_improvement.md §5.1): useLocked is `_mode is not null
    // && thisIndex >= _bandpassLockedFromSample`, evaluated once, at THIS SAME index's own first-
    // computation time (never re-evaluated -- the cache never recomputes an index once set; code-review
    // note, round 4: "thisIndex" here is _bandpassFilteredProcessedUpTo, the index actually being
    // computed by this loop iteration -- deliberately NOT this method's own `index` parameter, which is
    // only the caller's requested upper bound and may already have been satisfied by earlier iterations
    // computing lower indices first). `_mode is not null` excludes both "never locked yet" (field still
    // int.MaxValue) and the between-images gap (EndOfImage resets both _mode and
    // _bandpassLockedFromSample). useLocked does not exclude narrow modes -- legacy's own gate
    // (`m_Sync || m_SyncMode>=3`, sstv.cpp:1827) never consults narrow-ness for the locked/search
    // decision either, only for WHICH locked table applies (mirrored below via useNarrow, same shape as
    // DemodulatedFrequencyAt's own already-established `isNarrow` local). `thisIndex >=
    // _bandpassLockedFromSample`, NOT live state alone, is what makes this chunk-invariant AND correct
    // for the handful of samples strictly before the lock anchor that item 4a's fix means get computed
    // AFTER Commit() already fired (auditor code-level review of item 4a, round 3) -- those must stay H2
    // like legacy, not flip to H1/H3 just because _mode happens to be set by the time they're computed.
    //
    // RX BPF subsystem Phase 2: `_searchBandpassFilter?.ProcessSample(...) ?? FilteredRawSampleAt(...)`
    // is RxBpfPreset.Off's bypass -- matching legacy's `if(m_bpf){...}` gate (sstv.cpp:1826-1833) where
    // `d` is simply never reassigned. LOAD-BEARING: this must stay a null-coalesce INSIDE the existing
    // fill loop, never a method-level `if (_searchBandpassFilter is null) return FilteredRawSampleAt(index);`
    // early return -- an early return would skip the loop entirely, pinning _bandpassFilteredProcessedUpTo
    // at 0 for the whole session. That cursor is load-bearing in BOTH TrimBuffers watermark branches
    // (`watermark = Math.Min(watermark, Math.Max(0, _bandpassFilteredProcessedUpTo - 1))`) and its
    // unconditional RemoveRange -- pinning it at 0 makes TrimBuffers a permanent no-op under Off, i.e.
    // unbounded _rawSamples/_agcSamples growth for the entire session (round-1 auditor plan-review
    // finding, caught before any code was written). The cursor must advance every sample regardless of
    // preset, Off included -- a future "simplification" back to an early return would silently
    // reintroduce this leak. FirstLockedBandpassIndex (diagnostic-only) still gets set under Off even
    // though no real filter selection occurs there -- harmless, but not a meaningful lock signal when
    // the preset is Off.
    private double BandpassFilteredSampleAt(int index)
    {
        for (; _bandpassFilteredProcessedUpTo <= index; _bandpassFilteredProcessedUpTo++)
        {
            var thisIndex = _bandpassFilteredProcessedUpTo;
            var useLocked = _mode is not null && thisIndex >= _bandpassLockedFromSample;
            var useNarrow = useLocked && _mode!.NarrowModeCode is not null;
            if (useLocked)
            {
                FirstLockedBandpassIndex ??= thisIndex; // diagnostic-only, see its own doc comment
            }

            var rawFiltered = FilteredRawSampleAt(thisIndex);
            _bandpassFilteredSamples.Add(_searchBandpassFilter?.ProcessSample(rawFiltered, useLocked, useNarrow) ?? rawFiltered);
        }

        return _bandpassFilteredSamples[Rel(index)];
    }

    /// <summary>Diagnostic-only: the first absolute sample index <see cref="BandpassFilteredSampleAt"/>
    /// ever selected H1 (locked) for. Band-1 item 4b, added per an auditor code-level review finding
    /// (round 4): the round-3 correction -- gating on the captured <c>_bandpassLockedFromSample</c>
    /// index rather than live <c>_mode</c> state, so samples strictly before the lock anchor that item
    /// 4a's fix means get computed AFTER <c>Commit()</c> fires still correctly stay H2 -- was previously
    /// protected only by a doc comment, not a test. <c>BandpassCacheChunkInvarianceTests</c> pins this
    /// equal to <see cref="LockAnchorCommitted"/>'s own value, closing that gap.</summary>
    internal int? FirstLockedBandpassIndex { get; private set; }

    /// <summary>Diagnostic-only: the first absolute sample index <see cref="DemodulatedFrequencyAt"/>
    /// ever selected the narrow-mode demod width for. Functional-audit fix (chunk D1, round 1): this
    /// gate (Band-2 item S6, `isNarrow` in <see cref="DemodulatedFrequencyAt"/>) is structurally
    /// identical to <see cref="FirstLockedBandpassIndex"/>'s own H1/H2 gate -- same captured
    /// <c>_bandpassLockedFromSample</c> anchor, same "cursor trails the anchor at <c>Commit()</c>"
    /// property -- but only the bandpass gate got a diagnostic + test after item 4b's own auditor
    /// finding; this sibling gate one method down got neither, and the existing
    /// <c>BandpassCacheChunkInvarianceTests</c> test structurally cannot cover it (it uses a wide
    /// mode, and this gate only ever fires for narrow modes by design).
    /// <c>FirstNarrowDemodIndex_EqualsTheLockAnchor_ForANarrowMode</c> closes that gap.</summary>
    internal int? FirstNarrowDemodIndex { get; private set; }

    // Band-1 item 4a (pre-Phase-2 audit, S1 follow-up): _demodulatedFrequencies used to be filled
    // EAGERLY, one sample at a time, directly inside PushSamples' per-sample loop -- for every raw
    // sample as it arrived, regardless of lock state. That's what let this cache (transitively, via
    // BandpassFilteredSampleAt) race arbitrarily far ahead of Commit()'s own lock decision: a bulk
    // caller pushing a whole file in one PushSamples call drove this cache all the way to
    // TotalSamplesReceived before TryProcessBuffer() (where Commit() actually runs) ever got a chance
    // to execute even once -- measured directly (a throwaway spike, since removed) at ~115 SECONDS of
    // wrongly-filtered content for a bulk push, vs. single-digit milliseconds for realistic small
    // chunk sizes. Auditor plan-review (round 2) confirmed the fix: make this lazy, forward-fill
    // CACHED like AgcSampleAt/BandpassFilteredSampleAt already are, driven only by an ACTUAL consumer
    // asking for a specific index -- not by PushSamples eagerly draining ahead of any consumer's real
    // need. This is a pure refactor of WHEN _demodulator.ProcessSample runs, not of what it computes:
    // the same stateful streaming demodulator still gets fed every index exactly once, in strict
    // monotonic order, the same guarantee AgcSampleAt/BandpassFilteredSampleAt already rely on for
    // their own stateful filters (_levelAgc/_searchBandpassFilter) -- so this preserves bit-identical
    // output for every existing caller (verified: full suite + golden vectors unchanged). This alone
    // does NOT switch bandpass filters on lock (that's item 4b, separately scoped) -- it only removes
    // ONE of the two drivers that raced BandpassFilteredSampleAt's own cache ahead of lock. The other,
    // AgcSampleAt's own eager continuous scanning (genuinely required for header detection to ever
    // find a header at all), stays -- auditor's assessment is that the residual gap it alone leaves is
    // bounded by detection latency, not chunk size; re-measured directly once this lands (see the
    // BufferedSampleCount-style diagnostic test this piece adds).
    private double DemodulatedFrequencyAt(int index)
    {
        for (; _demodulatedFrequenciesProcessedUpTo <= index; _demodulatedFrequenciesProcessedUpTo++)
        {
            var thisIndex = _demodulatedFrequenciesProcessedUpTo;

            // Band-2 item S6: reuses item 4b's own _bandpassLockedFromSample anchor (captured in
            // Commit(), reset to int.MaxValue in EndOfImage()) -- both gates fire off the same lock
            // event, and auditor plan-review confirmed the same negative-gap property 4a discovered
            // for the bandpass cache recurs here (this cursor also trails the anchor at Commit() time,
            // so pre-anchor samples correctly stay wide -- see HilbertFmDemodulator's own doc comment).
            var isNarrow = _mode is not null && _mode.NarrowModeCode is not null && thisIndex >= _bandpassLockedFromSample;
            if (isNarrow)
            {
                FirstNarrowDemodIndex ??= thisIndex; // diagnostic-only, see its own doc comment
            }

            // Demod-type subsystem Phase 2, landmine #3 -- retune the main-path PLL/ZeroCrossing
            // instance on the narrow-mode edge (not every sample). Only the CURRENTLY SELECTED
            // instance is retuned: unlike legacy's CSSTVDEM::SetWidth (which unconditionally retunes
            // all three regardless of m_Type), the non-selected instances here are never fed samples
            // at all, so their own width state is unobservable dead state -- retuning only the live
            // one is behaviorally identical, not a simplification that changes anything observable.
            // Hilbert needs no such call: its isNarrow is a stateless per-call ProcessSample argument.
            if (isNarrow != _mainPathIsNarrow)
            {
                _mainPathIsNarrow = isNarrow;
                switch (_demodType)
                {
                    case DemodType.Pll:
                        _pllDemodulator.SetWidth(isNarrow);
                        break;
                    case DemodType.ZeroCrossing:
                        _zeroCrossingDemodulator.SetWidth(isNarrow);
                        break;
                }
            }

            var scaledSample = BandpassFilteredSampleAt(thisIndex) * 32768.0;
            var demodulated = _demodType switch
            {
                // sstv.cpp:2256-2269's exact case order/shape (case 0=PLL, case 1=ZeroCrossing,
                // default=Hilbert).
                DemodType.Pll => _pllDemodulator.ProcessSample(scaledSample),
                DemodType.ZeroCrossing => _zeroCrossingDemodulator.ProcessSample(scaledSample),
                _ => _demodulator.ProcessSample(scaledSample, isNarrow),
            };
            _demodulatedFrequencies.Add(demodulated);
        }

        // Functional-audit correction (D1 cross-chunk note, confirmed accurate by D7 round 1): this
        // is the ONE cache of the nine in this substrate that is NOT a pure function of `index` --
        // ApplyAfcCorrections' own deferred bulk pass (see its own doc comment) post-mutates an
        // already-cached entry in place (`_demodulatedFrequencies[Rel(i)] += correction`), so a read
        // here can return either the pre- or post-AFC-correction value depending on whether that
        // deferred pass has reached index `i` yet relative to THIS call. Safe today (AFC advances
        // monotonically and only ever corrects an index once, confirmed by both D1 and D7's own
        // independent audits) but genuinely order-dependent, unlike every other cache this class
        // documents as a simple index function elsewhere.
        return _demodulatedFrequencies[Rel(index)];
    }

    // sstv.cpp:2258/2263/2267's `m_lvl.m_CurMax > 16` AFC silence gate reads m_CurMax as of the exact
    // sample being processed at that moment in legacy's single real-time pass -- not "whatever
    // LevelAgc's CurMax happens to be right now" (this port's AFC correction runs as a deferred bulk
    // pass, potentially well after the shared AGC cache has already advanced past this index for an
    // unrelated consumer), so this reads the value snapshotted into _agcCurMaxSamples at the same
    // index AgcSampleAt itself cached, not the live LevelAgc.CurMax.
    private double AgcCurMaxAt(int index)
    {
        AgcSampleAt(index);
        return _agcCurMaxSamples[Rel(index)];
    }

    private double FskSpaceAt(int index)
    {
        for (; _fskSpaceProcessedUpTo <= index; _fskSpaceProcessedUpTo++)
        {
            _fskSpaceSamples.Add(_fskSpaceDetector.ProcessSample(AgcSampleAt(_fskSpaceProcessedUpTo)));
        }

        return _fskSpaceSamples[Rel(index)];
    }

    private double D11At(int index)
    {
        for (; _visDataD11ProcessedUpTo <= index; _visDataD11ProcessedUpTo++)
        {
            _visDataD11Samples.Add(_visDataD11Detector.ProcessSample(AgcSampleAt(_visDataD11ProcessedUpTo)));
        }

        return _visDataD11Samples[Rel(index)];
    }

    private double D12At(int index)
    {
        for (; _visDataD12ProcessedUpTo <= index; _visDataD12ProcessedUpTo++)
        {
            var value = _visDataD12Detector.ProcessSample(AgcSampleAt(_visDataD12ProcessedUpTo));
            _visDataD12Samples.Add(value);

            // Un-stub-RX-tab Piece B: Decoder Trace channel-0 write, this port's counterpart to
            // legacy's m_Scope[0].WriteData(d12) (sstv.cpp:1871-1882, unconditional -- every
            // sample, synced or not). No Hz conversion needed here (unlike channel 1) -- AgcSampleAt
            // already returns legacy's own ±16384-domain d/d12/d19 values. Only the LATCHED source
            // writes (_scopeCaptureChannel0.UsesD19's own doc comment) -- exactly-once/monotonic/
            // gap-free by construction, this loop only ever advances forward past each index once.
            if (!_scopeCaptureChannel0.UsesD19)
            {
                _scopeCaptureChannel0.Write(value);
            }
        }

        return _visDataD12Samples[Rel(index)];
    }

    private double D19At(int index)
    {
        for (; _visDataD19ProcessedUpTo <= index; _visDataD19ProcessedUpTo++)
        {
            var value = _visDataD19Detector.ProcessSample(AgcSampleAt(_visDataD19ProcessedUpTo));
            _visDataD19Samples.Add(value);

            // See D12At's own comment immediately above -- same hook, the narrow-mode counterpart.
            if (_scopeCaptureChannel0.UsesD19)
            {
                _scopeCaptureChannel0.Write(value);
            }
        }

        return _visDataD19Samples[Rel(index)];
    }

    // Band-1 S2 fix (pre-Phase-2 audit): bounds this decoder's growing per-sample caches (originally
    // _rawSamples/_demodulatedFrequencies/_agcSamples/_agcCurMaxSamples/_bandpassFilteredSamples;
    // round-11 D2-audit correction -- more caches have been added to this method since, so this
    // comment no longer states a fixed count; see this method's own RemoveRange calls below for the
    // current full list), which would otherwise grow without limit for the lifetime of this decoder
    // instance (~5.7GB/hr @44100Hz measured before this fix) -- a real problem the moment a
    // production caller wires this decoder to continuous live capture, not just a test-only
    // decoder's usual short-lived scope.
    //
    // Auditor plan-review correction, the single most important one: an earlier draft of this method
    // never trimmed while `_mode is null`, on the reasoning that pre-lock state is somehow more
    // fragile -- backwards. The actual unbounded-growth scenario this fix exists for IS the
    // never-locks case (a receiver left on an open squelch, or listening to a band with no SSTV
    // activity) -- excluding it from trimming would leave the exact motivating case unfixed. The
    // locked case is already naturally bounded by one image's own duration regardless. So pre-lock
    // gets its OWN trimming rule (a fixed trailing retention window, not a cursor-derived watermark --
    // there's no committed anchor yet to be conservative around), and only the
    // `_pendingAnchorCorrectionMode is not null` window (between Commit() and the anchor correction
    // resolving, where the sync-anchor-correction and AVT-PLL warm-ups read up to AnchorWarmupSamples
    // behind a PRE-correction anchor that may already be behind any cursor-derived watermark) is ever
    // fully excluded from trimming.
    private void TrimBuffers()
    {
        if (_pendingAnchorCorrectionMode is not null)
        {
            return;
        }

        int watermark;
        if (_mode is null)
        {
            // Pre-lock: retain enough trailing samples for TryInterleavedHeaderScan's own worst-case
            // lookback needs -- the fixed-window header paths' own full search window
            // (VisHeader.MaxSearchCeilingMs) OR (whichever is larger) CommitSyncBypassMatch's own
            // ability to anchor up to one full sync interval behind _syncBypassProcessedUpTo
            // (SyncIntervalTracker.MaxIntervalSamples, shared from that class rather than
            // re-derived), plus the same AnchorWarmupSamples margin the eventual lock's own anchor
            // correction will need once a match commits.
            var preLockRetentionSamples =
                Math.Max(MsToSamples(VisHeader.MaxSearchCeilingMs), (int)Math.Ceiling(_syncBypassTracker.MaxIntervalSamples))
                + AnchorWarmupSamples;
            watermark = TotalSamplesReceived - preLockRetentionSamples;

            // Never trim ahead of any cursor's own current position either -- all of these are valid
            // pre-lock. AFC/Slant are deliberately NOT in this list (D2 round 1 correction: an earlier
            // version of this comment claimed _afcTracker/_slantTracker are always null pre-lock,
            // "so there's nothing to include" -- false during the AVT training-pending window:
            // AbandonInProgressImage nulls _mode but deliberately leaves both trackers alive, see that
            // method's own doc comment). The real reason they're excluded here is that their OWN cursor
            // fields (_afcProcessedUpTo/_slantProcessedUpTo) get re-based forward to _consumedSamples
            // the moment a fresh lock's InitializeAfc/InitializeSlant runs (Math.Max(...)/direct
            // assignment respectively, both before either method's own AVT early-return), so an
            // abandoned-image tracker's stale cursor position never needs protecting from trimming --
            // it gets thrown away and re-based, not read from, once the next lock happens. That's only
            // half the argument (D2 round 2 addition): the re-base alone wouldn't matter if something
            // could still READ a tracker's cursor while _mode is null -- it can't, since
            // ApplyAfcCorrections/ApplySlantTracking are called only from TryProcessBuffer's own locked
            // per-line loop, never while pre-lock. If that ever changed, this exclusion would need
            // revisiting even though the re-base fact alone would still be true.
            //
            // _consumedSamples is included ONLY while the fixed-window paths might still run (see
            // TryDecodeHeader's own matching guard, and its doc comment for why skipping is simpler
            // and just as correct as an earlier, reverted attempt at periodically re-anchoring it
            // instead). It is NEVER otherwise advanced pre-lock, so for a long-idle, never-locking
            // stream it would stay 0 forever and permanently block all trimming -- the exact bug
            // BufferedSampleCount_StaysBounded_ForLongNeverLockingStream caught. Once
            // _fixedWindowExhausted, TryDecodeHeader provably never reads starting from the stale
            // _consumedSamples again, so it's safe to trim past it.
            if (!_fixedWindowExhausted)
            {
                watermark = Math.Min(watermark, _consumedSamples);
            }

            // S31 fix (auditor plan-review caught this before it shipped): TryResolveAvtTraining's own
            // warm-up (from _avtPllWarmupStartSample, Band-2 item S16 -- widened from a clamped
            // constant to legacy's own real ~1850ms contiguous pre-origin m_pll feed span, see
            // TryStartAvtTraining's own doc comment) now needs an EXPLICIT term here. Previously this
            // was covered only transitively: _avtTrainingPending could only ever become true via
            // TryDecodeVisHeader (the fixed-window path), which runs strictly BEFORE
            // TryInterleavedHeaderScan ever gets a chance to set _fixedWindowExhausted -- so at the
            // moment _avtTrainingPending first became true, _fixedWindowExhausted was still
            // guaranteed false, and the `if` above already pinned the watermark at _consumedSamples
            // (= headerStart), which sits before _avtPllWarmupStartSample too (headerStart +
            // totalHeaderSampleCount - one bit period). That's no longer true: TryInterleavedHeaderScan
            // can now ALSO start AVT training (a VisLockStateMachine match found via its own
            // noise-tolerant scan), and it sets _fixedWindowExhausted = true in the SAME call, before
            // its own scan loop even runs -- so by the time it finds an AVT match and calls
            // TryStartAvtTraining, the `if` above has already stopped protecting _consumedSamples,
            // and _avtPllWarmupStartSample sits only a NARROW margin behind wherever
            // _syncBypassProcessedUpTo/_visLockProcessedUpTo happen to be frozen at (the match
            // sample) -- code-level auditor review measured this directly: ~166 samples (~15ms at
            // 11025Hz, VisLockStateMachine's own Verify-state 15ms reconciliation term), not the
            // wide margin _consumedSamples used to provide. Without this explicit term, a
            // long-running chunked/streaming push could trim past _avtPllWarmupStartSample during
            // the up-to-~7.1s _avtTrainingPending window and crash Rel()'s own bounds check inside
            // TryResolveAvtTraining's warm-up loop -- exactly the failure mode the ORIGINAL version
            // of this comment (wrongly) claimed couldn't happen.
            if (_avtTrainingPending)
            {
                watermark = Math.Min(watermark, _avtPllWarmupStartSample);
            }

            watermark = Math.Min(watermark, _syncBypassProcessedUpTo);
            watermark = Math.Min(watermark, _visLockProcessedUpTo);

            // S8 fix: within a single pre-lock epoch this cursor is now bound-gated the same way
            // _syncBypassProcessedUpTo/_visLockProcessedUpTo are (TryNarrowFskScan is called with a
            // bound that's always <= scanBound, see that call site's own corrected doc comment --
            // milestone-audit MUST fix: now called once per iteration with a tight per-sample bound,
            // not once with scanBound directly, but every bound passed is still <= scanBound, so this
            // watermark term's own conclusion is unaffected) -- but ACROSS images it still
            // differs: _syncBypassProcessedUpTo/_visLockProcessedUpTo get re-anchored/jumped by
            // EndOfImage and Commit(), so they can never lag far behind, while _narrowFskProcessedUpTo
            // is NEVER reset or jumped (see its own field doc comment), so over a multi-image stream it
            // becomes the SLOWEST cursor in the system. Included explicitly anyway, not left to
            // accident. Provably bounded, not permanently stallable: TryNarrowFskScan is called at
            // least once per TryInterleavedHeaderScan call (i.e. at least once per PushSamples call,
            // while _mode is null -- now potentially many more times, once per interleaved-loop
            // iteration, but never fewer). SHOULD item 7 correction (spec/14-roadmap.md, comprehensive
            // code-review round): an earlier version of this comment claimed this cursor PAUSES for
            // the whole up-to-~7.1s _avtTrainingPending window -- imprecise, and worth stating
            // correctly rather than just "no longer true": TryDecodeHeader still short-circuits PAST
            // TryInterleavedHeaderScan entirely while `_avtTrainingPending` (that guard is unchanged,
            // see TryDecodeHeader's own doc comment) -- what changed is that the OTHER path this
            // cursor advances through, TryResolveAvtTraining, now ALSO interleaves its own
            // TryNarrowFskScan call every training sample (see that method's own doc comment). So this
            // cursor no longer sits idle during training -- not because the short-circuit went away,
            // but because a second call site now feeds it. The bound-provably-stallable conclusion
            // only gets stronger, not weaker.
            watermark = Math.Min(watermark, _narrowFskProcessedUpTo);

            watermark = Math.Min(watermark, _levelAgcProcessedUpTo);
            // SHOULD item 8 (spec/14-roadmap.md): strict `<`, not `<=` -- FilteredRawSampleAt reads
            // `_rawSamples[Rel(index-1)]` for any index > 0, so if this watermark term ever let
            // `_bufferBase == _bandpassFilteredProcessedUpTo` exactly, the NEXT fill
            // (FilteredRawSampleAt(_bandpassFilteredProcessedUpTo)) would read index-1 == _bufferBase-1,
            // which Rel() throws on. `Math.Max(0, ...)`, not a bare `-1`: at
            // _bandpassFilteredProcessedUpTo == 0 this reduces to exactly today's own value (0, not -1)
            // -- safe, because FilteredRawSampleAt(0) doesn't read index-1 at all (its own ternary
            // special-cases index == 0), so there's nothing to protect against there. Round-1-review
            // note: a bare `-1` there wouldn't actually reach the `Xxx(watermark-1)` catch-up calls
            // below either (the `Math.Max(watermark, _bufferBase)` clamp a few lines down, plus the
            // trimAmount<MinTrimSamples early return, would already absorb it before those run) -- kept
            // `Math.Max(0, ...)` anyway because it's locally self-evidently correct without depending on
            // a clamp ~90 lines away, not because the alternative was provably broken. Currently
            // unreachable either way (documented margin: preLockRetentionSamples/AnchorWarmupSamples
            // already exceed what would ever let this term be the chain's own minimum) -- tightened
            // anyway since the margin was previously undocumented/unenforced by this line itself.
            watermark = Math.Min(watermark, Math.Max(0, _bandpassFilteredProcessedUpTo - 1));

            // Band-1 item 4a: deliberately NOT including _demodulatedFrequenciesProcessedUpTo here,
            // unlike _bandpassFilteredProcessedUpTo above. First attempt did include it (matching that
            // sibling cursor's own pattern) and broke two real tests two different ways: (1) included
            // unconditionally -> permanently pinned near 0 for a long-idle, never-locking stream (its
            // ONLY pre-lock reader, AverageFrequencyInWindow's narrow-vs-normal-VIS discriminator, is
            // itself gated behind `!_fixedWindowExhausted`, so it simply stops advancing once that
            // flips -- the exact BufferedSampleCount_StaysBounded_ForLongNeverLockingStream failure
            // shape, just for a different cursor than _consumedSamples' own already-documented case
            // above); (2) included conditionally, matching _consumedSamples' own `if
            // (!_fixedWindowExhausted)` pattern -> let the watermark advance PAST this cursor's actual
            // fill position, which crashes: unlike _consumedSamples (a logical cursor value, safe to
            // go stale), this one IS this list's own physical length -- RemoveRange(0, trimAmount)
            // throws ArgumentException the moment trimAmount exceeds what _demodulatedFrequencies
            // actually holds. Resolved below instead: an explicit catch-up right before the RemoveRange
            // block guarantees this list is never asked to remove more than it has, without needing a
            // watermark term here at all -- see that comment for the full reasoning.
        }
        else
        {
            // Locked: bounded by the image's own duration in practice, but still computed
            // correctly rather than skipped -- an idle-forever *previous* lock (e.g. AFC/Slant
            // stalled) shouldn't be able to grow unboundedly either. _syncBypassProcessedUpTo is
            // DELIBERATELY excluded here (auditor plan-review finding): it's frozen at whatever
            // value it held when this transmission locked (TrySyncIntervalDetectionStep is
            // hard-gated behind !m_Sync, matching legacy) until EndOfImage overwrites it fresh --
            // including it in this min() would pin the watermark for the whole image instead of
            // letting it advance as decoding progresses.
            //
            // Milestone-audit MUST fix: _afcProcessedUpTo/_slantProcessedUpTo are ONLY included when
            // their tracker actually exists. AVT is the one mode where InitializeAfc/InitializeSlant
            // both return early with _afcTracker/_slantTracker left null (AFC/Slant are legacy
            // features AVT doesn't have -- two SEPARATE `mode != smAVT` guards, not one: AFC's is
            // `sstv.cpp:2258/2263/2267` inside all three m_Type branches' `if(m_afc && m_CurMax>16 &&
            // SSTVSET.m_Mode!=smAVT) SyncFreq(...)`; Slant/AutoStop's is `Main.cpp:3886`'s
            // `if((m_AutoStop||m_AutoSync||KRSA->Checked) && (SSTVSET.m_Mode!=smAVT))` -- code-level
            // review correction, an earlier version of this comment cited only the Slant guard for
            // both) -- ApplyAfcCorrections/
            // ApplySlantTracking both then return immediately every call without ever advancing their
            // own cursor again, so for AVT specifically these two terms are permanently frozen at
            // whatever they were assigned once, at commit time, not "stalled" but simply inapplicable.
            // Unconditionally including them (as this used to) meant an AVT image's own buffer NEVER
            // trimmed for the image's whole ~90s duration (240 lines x 375ms), retaining tens to
            // hundreds of MB across all 9 buffers depending on sample rate -- the exact unbounded-
            // growth failure class Band-1 item S2 already fixed pre-lock, silently reopened here on
            // the locked side for AVT. Mirrors the pre-lock branch's own already-established pattern
            // for these same two cursors (see its own comment: "AFC/Slant don't exist yet ... excluded
            // here ... because there's nothing to include") -- the defensive stall-protection property
            // this comment describes is preserved exactly for every OTHER mode, where the tracker is
            // real and a genuine stall would (correctly) still pin the watermark.
            watermark = TotalSamplesReceived;
            if (_afcTracker is not null)
            {
                watermark = Math.Min(watermark, _afcProcessedUpTo);
            }

            if (_slantTracker is not null)
            {
                watermark = Math.Min(watermark, _slantProcessedUpTo);
            }

            // D0-audit round-11 fix: only included while _syncRestartEnabled, unlike
            // _narrowFskProcessedUpTo below. TryVisLockStateMachine (this cursor's only advancer) is
            // still correctly gated behind _syncRestartEnabled at its own caller -- mid-reception
            // VIS-lock re-scanning is exactly what disabling Lock/Restart is meant to suppress, unlike
            // narrow FSK's own unconditional scan (see that caller's own doc comment). So with
            // Lock/Restart disabled, this cursor is frozen at whatever value it held when this
            // transmission locked, for the transmission's WHOLE duration -- same "frozen, not stalled"
            // shape as _syncBypassProcessedUpTo above, just for a user-toggleable reason instead of an
            // always-true one. Including it unconditionally (as this used to) pinned the trim watermark
            // for an entire locked image's duration whenever the user disables Lock/Restart -- the same
            // unbounded-retention failure class the AVT fix above already closed, here gated by a
            // runtime setting instead of the mode. Safe to exclude when frozen: both EndOfImage
            // (:3061-ish, its own resumeFrom) and Commit (:3984-ish, its own Math.Max) always
            // re-anchor this cursor forward past _bufferBase at the next transmission boundary, so
            // excluding it here can never let trimming pass a value this cursor will actually need to
            // read again.
            if (_syncRestartEnabled)
            {
                watermark = Math.Min(watermark, _visLockProcessedUpTo);
            }

            // S8 fix: included here unconditionally, unlike _visLockProcessedUpTo just above --
            // _narrowFskProcessedUpTo is NOT frozen while locked regardless of _syncRestartEnabled
            // (D0-audit round-11 fix: TryNarrowFskScan is now called every decoded line via its own
            // hoisted, unconditional caller -- see that call site's own doc comment), so it keeps
            // advancing here the same way _visLockProcessedUpTo does when Lock/Restart is enabled, not
            // the way the frozen _syncBypassProcessedUpTo (or now-conditionally-frozen
            // _visLockProcessedUpTo) does.
            watermark = Math.Min(watermark, _narrowFskProcessedUpTo);

            watermark = Math.Min(watermark, _levelAgcProcessedUpTo);
            // SHOULD item 8 (spec/14-roadmap.md): strict `<`, not `<=` -- FilteredRawSampleAt reads
            // `_rawSamples[Rel(index-1)]` for any index > 0, so if this watermark term ever let
            // `_bufferBase == _bandpassFilteredProcessedUpTo` exactly, the NEXT fill
            // (FilteredRawSampleAt(_bandpassFilteredProcessedUpTo)) would read index-1 == _bufferBase-1,
            // which Rel() throws on. `Math.Max(0, ...)`, not a bare `-1`: at
            // _bandpassFilteredProcessedUpTo == 0 this reduces to exactly today's own value (0, not -1)
            // -- safe, because FilteredRawSampleAt(0) doesn't read index-1 at all (its own ternary
            // special-cases index == 0), so there's nothing to protect against there. Round-1-review
            // note: a bare `-1` there wouldn't actually reach the `Xxx(watermark-1)` catch-up calls
            // below either (the `Math.Max(watermark, _bufferBase)` clamp a few lines down, plus the
            // trimAmount<MinTrimSamples early return, would already absorb it before those run) -- kept
            // `Math.Max(0, ...)` anyway because it's locally self-evidently correct without depending on
            // a clamp ~90 lines away, not because the alternative was provably broken. Currently
            // unreachable either way (documented margin: preLockRetentionSamples/AnchorWarmupSamples
            // already exceed what would ever let this term be the chain's own minimum) -- tightened
            // anyway since the margin was previously undocumented/unenforced by this line itself.
            watermark = Math.Min(watermark, Math.Max(0, _bandpassFilteredProcessedUpTo - 1));
            watermark = Math.Min(watermark, _demodulatedFrequenciesProcessedUpTo); // Band-1 item 4a, see above
            watermark = Math.Min(watermark, _consumedSamples);

            // Buffered-replay fix (§15 item 2, robust-giggling-codd.md): PerformReplay's own backward
            // snap can move _consumedSamples back by up to one corrected line width when a sync
            // correction lands on the previous row. Retain one stride of margin (+1 sample) so the
            // common per-line Auto-Slant correction case never needs PerformReplay's own raw-floor
            // clamp-forward fallback. Does NOT fully protect manual Correct Slant: that path's own
            // 5-iteration search has no upper-stride clamp on how far back it can land, so it can still
            // hit the raw floor -- PerformReplay's own clamp-forward branch is the real safety net for
            // that path, not this margin.
            watermark = Math.Min(watermark, _consumedSamples - (int)Math.Ceiling(_effectiveSamplesPerLine) - 1);
            watermark -= AnchorWarmupSamples; // margin for the NEXT lock's own anchor-correction warm-up
        }

        watermark = Math.Max(watermark, _bufferBase); // never move backward
        watermark = Math.Min(watermark, TotalSamplesReceived); // never move ahead of what's been received

        // Amortize: List<T>.RemoveRange is O(remaining), so trimming on every single PushSamples call
        // (a streaming caller's normal traffic pattern) would make buffer maintenance O(n^2) over a
        // session's lifetime. Only actually trim once enough slack has accumulated to make the O(n)
        // cost worthwhile.
        const int MinTrimSamples = 44100; // ~1s @44100Hz, ~4s @11025Hz -- either way, a small fraction of a typical image
        var trimAmount = watermark - _bufferBase;
        if (trimAmount < MinTrimSamples)
        {
            return;
        }

        // Band-1 item 4a: _demodulatedFrequencies' own forward-fill cursor is deliberately NOT a
        // watermark term in the pre-lock branch above (see that comment) -- so, unlike the other 4
        // buffers, its physical length can be shorter than trimAmount at this point. Catch it up
        // before trimming, not because anything will ever read this range again (nothing will -- the
        // watermark computation above already established that for every other buffer), but because
        // List<T>.RemoveRange itself can never remove more elements than a list actually holds. A
        // no-op in the locked branch, where _demodulatedFrequenciesProcessedUpTo already sits in the
        // watermark's own Min() chain and can therefore never be exceeded here.
        //
        // Load-bearing invariant this relies on (auditor code-level review, round 3): watermark is
        // ALWAYS <= _bandpassFilteredProcessedUpTo in both branches above (both include it directly in
        // their own Min() chain -- SHOULD item 8's own fix tightened this further, to strictly <
        // whenever _bandpassFilteredProcessedUpTo >= 1, which only strengthens this invariant, doesn't
        // weaken it) -- so DemodulatedFrequencyAt(watermark - 1) here can never advance
        // _bandpassFilteredProcessedUpTo itself; its inner BandpassFilteredSampleAt calls are pure
        // cache reads, not new fills. That is what stops this catch-up from reintroducing item 4a's own
        // bug (the bandpass cache racing ahead of the lock anchor) from inside TrimBuffers. If a future
        // change ever drops _bandpassFilteredProcessedUpTo from either branch's Min() chain, this catch-
        // up can silently start driving that cache forward again -- don't remove it from either chain.
        //
        // Note this mostly defeats laziness, not cost, for a long pre-lock stream: the demodulator still
        // eventually runs over ~all pre-lock audio (just lagged by preLockRetentionSamples behind the
        // stream head instead of running at it) -- item 4a's actual win is ORDERING relative to Commit(),
        // not CPU savings, and this catch-up is exactly where that trade becomes visible.
        if (watermark > _demodulatedFrequenciesProcessedUpTo)
        {
            DemodulatedFrequencyAt(watermark - 1);
        }

        // Band-2 item S5 (extended by S14): D11At/D12At/D19At/FskSpaceAt's own cursors are deliberately
        // excluded from BOTH branches' watermark computation above, not just the pre-lock one -- unlike
        // _bandpassFilteredProcessedUpTo/_demodulatedFrequenciesProcessedUpTo (which get real, ongoing
        // post-lock consumers), D11At/D12At are read only by pre-lock-only TryDecodeVisDataBits, invoked
        // only via TryDecodeVisHeader/TryDecodeHeader -- once locked, both simply freeze wherever they
        // were at the moment of lock, the exact same "frozen once locked" shape _syncBypassProcessedUpTo's
        // own doc comment above already describes for a different cursor, not a new pattern.
        //
        // S8 fix correction: D19At/FskSpaceAt are NO LONGER pre-lock-only as of this fix -- TryNarrowFskScan
        // (called from TryVisLockStateMachine while LOCKED, via piece 6c's per-line mid-reception
        // re-verification, not just pre-lock via TryDecodeNarrowModeHeader/TryDecodeHeader) reads both
        // post-lock too. This doesn't change the exclusion decision itself, but the safety argument for
        // it is NOT "_narrowFskProcessedUpTo is provably always <= _visDataD19ProcessedUpTo/
        // _fskSpaceProcessedUpTo" -- code-level auditor review correction: Commit()'s own fast-forward
        // (`_narrowFskProcessedUpTo = Math.Max(_narrowFskProcessedUpTo, _consumedSamples)`) can jump this
        // cursor forward WITHOUT reading D19At/FskSpaceAt at all, so that ordering guarantee doesn't
        // strictly hold. Still safe regardless: the SAME unconditional catch-up this section's own
        // opening paragraph describes (`if (watermark > _visDataD19ProcessedUpTo) { D19At(watermark - 1); }`
        // below) is what actually guarantees safety here, exactly as it already does for D11At/D12At --
        // not this cursor-ordering argument. Kept out of the watermark chains anyway (the catch-up makes
        // it unnecessary, not because it would be incorrect to include). The "once locked, all four
        // simply freeze" claim an earlier version of this comment made is no longer accurate for D19At/
        // FskSpaceAt specifically.
        //
        // For all four: excluded from watermark in both branches, safety guaranteed purely by this
        // catch-up instead -- same load-bearing invariant as DemodulatedFrequencyAt's own catch-up above
        // (watermark <= _levelAgcProcessedUpTo in BOTH branches, so AgcSampleAt(watermark-1) inside these
        // is always a pure cache read, never a new fill -- don't remove _levelAgcProcessedUpTo from
        // either chain either).
        if (watermark > _visDataD11ProcessedUpTo)
        {
            D11At(watermark - 1);
        }

        if (watermark > _visDataD12ProcessedUpTo)
        {
            D12At(watermark - 1);
        }

        if (watermark > _visDataD19ProcessedUpTo)
        {
            D19At(watermark - 1);
        }

        if (watermark > _fskSpaceProcessedUpTo)
        {
            FskSpaceAt(watermark - 1);
        }

        _rawSamples.RemoveRange(0, trimAmount);
        _demodulatedFrequencies.RemoveRange(0, trimAmount);
        _agcSamples.RemoveRange(0, trimAmount);
        _agcCurMaxSamples.RemoveRange(0, trimAmount);
        _bandpassFilteredSamples.RemoveRange(0, trimAmount);
        _visDataD11Samples.RemoveRange(0, trimAmount);
        _visDataD12Samples.RemoveRange(0, trimAmount);
        _visDataD19Samples.RemoveRange(0, trimAmount);
        _fskSpaceSamples.RemoveRange(0, trimAmount);

        _bufferBase = watermark;
    }
}
