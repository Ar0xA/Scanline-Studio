using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>
/// Generic decoder counterpart to <see cref="AnalogFmSstvEncoder"/>, using a ported
/// <see cref="HilbertFmDemodulator"/> (see that type's doc comment) run continuously over the
/// incoming sample stream for the main picture demodulation -- legacy's real compiled-in default
/// (`m_Type=2`, `sstv.cpp:1492`), not the PLL this port used before the Hilbert demodulator piece.
/// <see cref="PllFmDemodulator"/> stays genuinely in use, just no longer for the picture stream: a
/// dedicated instance still drives AVT training-lock detection (see
/// <see cref="AvtTrainingLockStateMachine"/>'s own doc comment for why legacy always uses PLL there
/// regardless of the picture demodulator's own `m_Type`). Once VIS reveals the mode, per-line
/// decoding is delegated to a
/// <see cref="IScanlineDecoder"/> selected via <see cref="ScanlineCodecFactory"/> — the decoder
/// can't know the family upfront the way the encoder does, since VIS detection is itself part of
/// this shared, family-agnostic shell.
///
/// Corrected doc-comment claim (an earlier revision of this comment, written before AFC/Slant/the
/// VIS-preamble-lock system existed, claimed this "does not independently re-search for each line's
/// sync pulse" and "does not yet implement the clock-drift/slant correction" -- both false as of the
/// work described below, left stale until now): per-pixel readout is a single sample at a
/// sync-anchored index, not a windowed average (see <see cref="PixelSampleReader"/>'s own doc
/// comment) -- confirmed directly against `Main.cpp`'s `GetPixelLevel`/`GetPictureLevel`
/// (`Main.cpp:4038-4073`, round-1-review fix: an earlier revision of this very correction
/// mis-attributed these to `sstv.cpp`) that legacy does the same, and that there is no per-line re-search during
/// live reception either (legacy's own `m_rBase`/`m_TW` nominal-timing arithmetic is set once at
/// lock and never re-anchored mid-image; verified directly, not assumed, when an earlier attempt to
/// scope a "port the sync-search state machine" task found that premise wrong before writing any
/// code -- see spec/14-roadmap.md's "CSSTVDEM investigation, reframed" entry). AFC (<see cref="AfcTracker"/>,
/// direct port of `CSSTVDEM::SyncFreq`) and Auto Slant (<see cref="SlantTracker"/>, clock-drift
/// correction) are both ported and wired in via <see cref="ApplyAfcCorrections"/>/
/// <see cref="ApplySlantTracking"/>. The real, still-open gap for real captured audio (as opposed to
/// this port's own synthetic fixtures) is golden-vector validation against actual legacy binary
/// output -- in progress, not yet complete; see spec/14-roadmap.md's Phase 1 section.
/// </summary>
public sealed class AnalogFmSstvDecoder : ISstvDecoder
{
    // Legacy's real CPLL is always 1500-2300Hz for every mode except the MN/MC narrow family
    // (narrowed further to 2044-2300Hz via CSSTVDEM::SetWidth/IsNarrowMode, sstv.cpp:1707-1719/
    // 266-279) -- this port narrows flat to 1500-2300Hz for ALL modes including MN/MC, not
    // implementing legacy's further MN/MC narrowing (a documented Phase 1 simplification, not a
    // bug: MN/MC's own pixel tones, 2044-2300Hz, stay in-band either way). VIS decode never touches
    // the PLL at all (see VisBitDecision/TryDecodeVisDataBits) -- legacy doesn't even feed the PLL
    // during VIS cases 0/1/2/9, only from case 3 onward (sstv.cpp:2129); this port runs it
    // continuously regardless, a further simplification. Previously 1100-2300Hz, a stale artifact of
    // an earlier design where VIS-bit decode read this same PLL's demodulated-frequency stream --
    // narrowed to the real band once that dependency was removed (spec/14-roadmap.md's "Piece 9").
    //
    // Piece: Hilbert demodulator port -- these constants now scope ONLY the AVT-dedicated
    // PllFmDemodulator instance (_avtPllDemodulator), not the main picture path, which uses
    // HilbertFmDemodulator's own fixed-width config instead (see that class's own doc comment).
    private const double DemodulatorLowHz = 1500;
    private const double DemodulatorHighHz = 2300;

    // SetSenseLvl (sstv.cpp:1793-1817), case 1 -- the actual shipped default. CSSTVDEM's constructor
    // sets m_SenseLvl = 1 unconditionally (sstv.cpp:1489) before calling SetSenseLvl(), and the only
    // other write path (Main.cpp:1865's `ReadInteger("Define","DEMSLVL", pDem->m_SenseLvl)`) falls
    // back to that same ctor value when the INI key is absent -- so case 1 (3500/1750/5700) is what
    // ships out of the box, NOT the switch's `default:` branch (2400/1200/5000), which is only
    // reachable via a discrete 4-option "Sense Level" UI setting (Option.dfm's RGSLvl radio group)
    // this port doesn't expose yet. Verified by reading the constructor directly, not assumed from
    // the switch's own default label. m_SLvl2 is always m_SLvl*0.5 in every case (sstv.cpp:1798/1803/
    // 1808/1813) -- ported as a derived value, not a second independent constant.
    internal const double SLvl = 3500.0;
    internal const double SLvl2 = SLvl * 0.5;
    internal const double SLvl3 = 5700.0;

    private readonly int _sampleRate;
    private readonly List<double> _demodulatedFrequencies = [];
    private int _demodulatedFrequenciesProcessedUpTo; // Band-1 item 4a: see DemodulatedFrequencyAt
    private readonly List<float> _rawSamples = [];
    private readonly HilbertFmDemodulator _demodulator;
    private readonly SearchBandpassFilter _searchBandpassFilter;

    // Band-1 S2 fix (pre-Phase-2 audit): the absolute sample index _rawSamples[0]/_demodulatedFrequencies[0]/
    // _agcSamples[0]/_agcCurMaxSamples[0]/_bandpassFilteredSamples[0] currently correspond to -- 0
    // until TrimBuffers (sub-piece D) starts advancing it. Every one of the 5 growing buffers is
    // conceptually indexed by the SAME absolute sample-index space (the index PushSamples' incoming
    // stream defines), even though each buffer's own List<T> only physically holds
    // [_bufferBase, TotalSamplesReceived) for _rawSamples (which always grows 1:1, see PushSamples) or
    // a shorter, independently-lazily-filled range for the other four -- _demodulatedFrequencies
    // included, as of Band-1 item 4a (it used to also always grow 1:1, filled eagerly inside
    // PushSamples' own per-sample loop; see DemodulatedFrequencyAt's own doc comment for why that
    // changed) -- each with its own forward-fill cursor (AgcSampleAt/BandpassFilteredSampleAt/
    // DemodulatedFrequencyAt) that may lag well behind TotalSamplesReceived. Rel() translates an
    // absolute index to the current physical List<T> index for whichever buffer is being read; every
    // accessor in this class must go through it rather than indexing a buffer directly, so a future
    // trim can never silently read stale/wrong data -- this is a `checked`-style guard, not just a
    // convenience: Rel() throws if asked to translate an index that has already been trimmed away,
    // matching this piece's auditor plan-review's own explicit warning that a silently-wrong (not
    // throwing) site is the dangerous failure class here, not a loud one.
    private int _bufferBase;

    // Band-1 S2 fix (pre-Phase-2 audit): EndOfImage's 0.5s dead-time skip means nothing ever asks
    // AgcSampleAt for that range's samples (header detection correctly resumes at resumeFrom, past
    // it) -- but this class's own documented legacy-fidelity property (see _levelAgc's own doc
    // comment: CLVL "advances monotonically over the whole _rawSamples stream regardless of
    // EndOfImage's dead-time skip") means _levelAgcProcessedUpTo is supposed to catch up through it
    // anyway, just not synchronously in EndOfImage itself (the dead-zone's own samples may not have
    // arrived yet at that exact moment, for a streaming/chunked caller). Set to the new resumeFrom in
    // EndOfImage; drained incrementally, as data allows, by AdvanceAgcThroughDeadZone.
    private int _agcDeadZoneCatchUpTarget;

    // TryResolveSyncAnchorCorrection's/TryResolveAvtTraining's own shared warm-up depth (both sites'
    // doc comments already explain WHY 2000 -- an order of magnitude past a resonator's ~3.2ms
    // settling time) -- named here, and shared with those two existing sites (previously each had its
    // own independent literal 2000), so TrimBuffers' own lookback requirement can never silently
    // desync from what those warm-ups actually need to read.
    private const int AnchorWarmupSamples = 2000;

    /// <summary>Diagnostic-only: the number of samples currently physically held in
    /// <c>_rawSamples</c> (i.e. after trimming, NOT <see cref="TotalSamplesReceived"/>). Mirrors
    /// <c>MiniAudioCaptureSession.OverrunCount</c>'s own shape -- a raw number for a caller/test to
    /// interpret, not a verdict. Exists so <see cref="TrimBuffers"/>'s bound can actually be verified
    /// (a long, never-locking stream should NOT grow this linearly with total samples pushed).</summary>
    internal int BufferedSampleCount => _rawSamples.Count;

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
    // ~13.5h of continuous streaming @44100Hz (~54h @11025Hz). Explicitly out of scope for this fix
    // (a session that long is well beyond anything this port's test suite or any near-term real usage
    // exercises) rather than silently fixed -- widening every one of this class's absolute-index
    // fields to `long` would be a much larger, separately-scoped change. Flagged here, not hidden,
    // for whenever a genuinely long-running production caller (e.g. an always-on Phase-2 receiver)
    // makes this a real constraint instead of a theoretical one.
    private int TotalSamplesReceived => _bufferBase + _rawSamples.Count;

    private int Rel(int absoluteIndex)
    {
        if (absoluteIndex < _bufferBase)
        {
            throw new InvalidOperationException(
                $"Absolute sample index {absoluteIndex} was requested but the buffer has already been trimmed up to {_bufferBase} -- " +
                "some cursor read behind the trim watermark, which TrimBuffers' own retention rule is supposed to prevent.");
        }

        return absoluteIndex - _bufferBase;
    }

    private int _consumedSamples;
    private SstvModeDefinition? _mode;
    private IScanlineDecoder? _lineDecoder;
    private Rgb24[]? _pixels;
    private int _nextLine;

    // Band-1 item 4b (pre-Phase-2 audit): the absolute sample index BandpassFilteredSampleAt's H1/H2
    // selection switches at -- captured once, in Commit(), NOT read live off _mode. Auditor code-level
    // review of item 4a found this is load-bearing, not a style choice: the measured bandpass-cache
    // cursor sits BEHIND the lock anchor at the moment Commit() fires (item 4a's whole point), so a few
    // thousand samples strictly BEFORE the anchor get computed AFTER _mode is already non-null --
    // gating on live "_mode is not null" alone would wrongly assign those pre-anchor samples H1, when
    // legacy used H2 for nearly all of that span (only the final ~30ms/~270ms stop-bit window is
    // m_SyncMode>=3, already decided out of scope, see SearchBandpassFilter's own doc comment). Reset
    // to int.MaxValue in EndOfImage -- defensive, not strictly required for correctness on its own
    // (BandpassFilteredSampleAt's gate also requires _mode is not null, which EndOfImage already resets
    // to null), but avoids a stale value lingering between images.
    private int _bandpassLockedFromSample = int.MaxValue;

    // Piece 8c: legacy re-anchors the per-pixel phase from a measured sync-envelope peak
    // (TMmsstv::SyncSSTV, Main.cpp:3751-3799) before ever drawing a pixel for a newly-locked image --
    // gated behind CSSTVDEM::Start's m_wBgn (sstv.cpp:1732), which DrawSSTV (Main.cpp:4917-4986)
    // checks before every draw call, returning without drawing until enough lines are buffered. This
    // field is that same gate: non-null between Commit() locking a (non-AVT) mode and
    // TryResolveSyncAnchorCorrection successfully applying the correction -- see both methods' own
    // doc comments. AFC/Slant initialization and the ModeDetected event are deliberately deferred
    // until this resolves (FinalizeAnchorAndStartDecoding), since both derive their own initial
    // cursors from _consumedSamples and mutating it afterward would silently desync them (found by
    // Opus plan-review before this piece was implemented).
    private SstvModeDefinition? _pendingAnchorCorrectionMode;

    private AfcTracker? _afcTracker;
    private int _afcProcessedUpTo;
    private int _afcBoundSample; // see Commit -- never correct past this image's own generous nominal extent

    private SyncEnvelopeDetector? _syncEnvelopeDetector;
    private SlantTracker? _slantTracker;
    private int _slantProcessedUpTo;
    private double _effectiveSamplesPerLine;
    private double _syncSegmentOffsetSamples;
    private double _slantIdealSamplesSoFarInLine;
    private double _slantLineMaxEnvelope;
    private double _slantLinePeakPosition;

    // m_sint1 (sstv.cpp:1899-1904/1946-1972, sstv.h:700, isNarrow:false -- SyncCheckSub's own
    // m_fNarrow gating restricts it to non-narrow candidates, same as m_sint2) -- piece 7d, the last
    // of the 7-piece VIS/preamble-lock breakdown. Same CSYNCINT class as m_sint2/m_sint3, but wired
    // completely differently: checked FIRST every sample (top priority, no SyncBypassTrustedModes-
    // style allowlist -- legacy acts on *any* mode SyncStart returns, sstv.cpp:1900-1904), and never
    // independently scans -- it only ever gets new peak data from the SAME primary threshold
    // (d12>d19 && d12>SLvl && (d12-d19)>=SLvl) that also drives VisLockStateMachine's own Search/
    // ConfirmLock (sstv.cpp:1946-1950/1958-1972), via SyncTrig on that threshold's rising edge and
    // SyncMax while it holds -- not a lower, always-checked threshold like m_sint2/m_sint3's own.
    // _syncBypass1PrimaryHeld is this port's own local case-0/case-1 latch for that same threshold,
    // a deliberate second copy of what VisLockStateMachine already tracks internally. Round-1-review
    // correction: this used to say "requires evaluating it inside this same loop, and this loop has
    // no access to VisLockStateMachine's separate instance/cursor" -- false since the m_sint1
    // decoder-ordering fix (TryInterleavedHeaderScan/TrySyncIntervalDetectionStep run interleaved,
    // in the same class, with direct field access to _visLockStateMachine). The copy is still needed
    // for a different reason: legacy computes its own d12/d19 once per Do() call and shares them
    // (sstv.cpp:1841-1853); recombining that here would mean VisLockStateMachine no longer owning
    // its own envelope detectors, a bigger change than that fix, left for a follow-up.
    //
    // Corrected by independent review -- an earlier version of this comment claimed the two latches
    // "necessarily agree sample-for-sample," which is only true pre-lock. While locked, only
    // VisLockStateMachine runs (TrySyncIntervalDetectionStep is hard-gated behind !m_Sync, matching
    // legacy); its d12/d19 detectors keep running against the whole image, while this loop's own
    // d12/d19 detectors (_syncBypass1200Detector/_syncBypass1900Detector) sit idle. EndOfImage then
    // fast-forwards both cursors to the same resumeFrom, but the two detector pairs now carry
    // different filter histories and produce different d12/d19 for the same samples until their
    // resonators resettle -- so the latches can genuinely disagree for a short window at the start
    // of every transmission after the first. Small (bounded by the envelope detectors' own settling
    // time, the same order of magnitude as other already-accepted small imprecisions in this
    // system), not eliminated, documented honestly rather than assumed away.
    private readonly SyncIntervalTracker _syncBypass1Tracker;
    private bool _syncBypass1PrimaryHeld;

    // m_sint2 (sstv.cpp:1899-1924, sstv.h:701) -- recognizes Scottie1/Martin1/Martin2/SC2-180 from
    // sync-pulse periodicity alone, without ever decoding a VIS code. Unlike AFC/Slant's detectors,
    // this one has to run *before* the mode is known (that's its entire purpose), so it's
    // constructed once up front rather than per-mode in an Initialize* method. Legacy never sets
    // m_sint2.m_fNarrow (only m_sint3's is ever set to TRUE, sstv.cpp:1483), so this always runs
    // with isNarrow: false, matching m_sint2's real, fixed configuration exactly.
    private readonly SyncEnvelopeDetector _syncBypass1200Detector;
    private readonly SyncEnvelopeDetector _syncBypass1900Detector;
    private readonly SyncIntervalTracker _syncBypassTracker;
    private int _syncBypassProcessedUpTo;
    private int _syncBypassOriginSample; // see EndOfImage -- 0 until the first image completes and this tracker is Reset() past a dead zone

    // Band-1 S3 fix (pre-Phase-2 audit): TryInterleavedHeaderScan's own one-shot gate -- true once
    // TryDecodeHeader's fixed-window paths (TryDecodeVisHeader/TryDecodeNarrowModeHeader) have had
    // their full local search window (VisHeader.MaxSearchCeilingMs, relative to the current epoch's
    // _consumedSamples) to succeed and didn't, so the fallback is now free to scan/commit without
    // risking a race the fixed-window path was never given a fair chance to win. See
    // TryInterleavedHeaderScan's own doc comment for the full empirical/plan-review history. Reset
    // to false in EndOfImage, alongside every other pre-lock detection cursor/flag.
    private bool _fixedWindowExhausted;

    // m_sint3 (sstv.cpp:1924-1946, sstv.h:702, m_fNarrow=TRUE set at sstv.cpp:1483) -- the narrow
    // (MN73/110/140, MC110/140/180) counterpart to m_sint2 above, sharing the same d19 (1900Hz)
    // envelope this port already computes for m_sint2's own condition, plus a dedicated dsp
    // (2100Hz, FSKSPACE) envelope this trigger alone needs. Its calling-code shape genuinely
    // differs from m_sint2's: legacy explicitly latches SyncTrig (unconditional, on first entry
    // into the candidate band) then SyncMax (running max while still inside it), and calls
    // SyncStart() exactly once, on the falling edge -- m_sint2's simpler "SyncMax while high, else
    // SyncStart every sample" works too only because SyncStart is idempotent once its internal
    // peak is consumed; m_sint3 is ported literally as legacy wrote it, not collapsed to the
    // (provably equivalent) simpler shape, so the port stays a direct structural match. No trusted-
    // mode allowlist is needed here (unlike m_sint2's SyncBypassTrustedModes): SyncCheckSub's own
    // m_fNarrow gating (GetSyncIntervalMatchDepth(isNarrow: true)) already restricts every match to
    // the 6 real narrow modes, and legacy's own case 0 branch (sstv.cpp:1937-1941) acts on whatever
    // SyncStart() returns unconditionally, with no further switch/case filter.
    private readonly SyncEnvelopeDetector _syncBypassFskDetector;
    private readonly SyncIntervalTracker _syncBypassNarrowTracker;
    private bool _syncBypassNarrowPhaseActive; // m_sint3.m_SyncPhase

    // m_SyncMode cases 0(trigger)/1/2/9/3 (sstv.cpp:1946-2154) -- see VisLockStateMachine's own doc
    // comment. Tried between the fixed-window header path and the sync-interval bypass detectors:
    // it can locate any VIS-coded mode (not just the trusted subset those cover), but at a real,
    // measured anchor-precision cost the fixed-window path doesn't have, so it's a fallback, not a
    // replacement.
    private readonly VisLockStateMachine _visLockStateMachine;
    private int _visLockProcessedUpTo;
    private int _visLockOriginSample; // see EndOfImage -- 0 until the first image completes and this is Reset() past a dead zone

    // AVT training-sequence lock (sstv.cpp cases 4-7, see AvtTrainingLockStateMachine's own doc
    // comment) -- once TryDecodeVisHeader identifies AVT from its VIS byte, resolution moves into
    // this multi-call pending phase instead of committing atomically with a fixed-duration skip,
    // since the training lock's completion point is data-dependent, not a fixed duration.
    //
    // Piece: Hilbert demodulator port. Legacy's AVT lock state machine (sstv.cpp:2129/2159/2169/
    // 2187/2222) always calls m_pll.Do(ad) directly, regardless of CSSTVDEM::m_Type -- confirmed by
    // reading every one of those call sites, all outside the m_Type-dispatched switch the main
    // picture demodulation goes through (sstv.cpp:2255-2269). Legacy always uses PLL for AVT lock
    // detection even when Hilbert (or zero-crossing) is the active picture demodulator. Before this
    // piece, _demodulatedFrequencies (then PLL-sourced) was reused directly for AVT, justified on the
    // (now-false) grounds that both paths were literally the same demodulator -- see
    // AvtTrainingLockStateMachine's own doc comment. Now that the main picture path uses
    // HilbertFmDemodulator, AVT needs its own independent PllFmDemodulator instance, fed the same raw
    // samples, matching legacy's real dual-demodulator structure -- the exact "inferred one code path
    // from a neighboring one" failure shape CLAUDE.md §4's Scottie incident warns about, caught by
    // auditor plan-review before this was wired in.
    private PllFmDemodulator? _avtPllDemodulator;
    private bool _avtPllWarmedUp;
    private int _avtPllWarmupStartSample; // Band-2 item S16 -- see TryStartAvtTraining's own doc comment
    private bool _avtTrainingPending;
    private AvtTrainingLockStateMachine? _avtTrainingLock;
    private int _avtTrainingOriginSample;
    private int _avtTrainingProcessedUpTo;
    private int _avtTrainingFallbackDeadlineSample;

    // CLVL (sstv.cpp:1834-1839, see LevelAgc's own doc comment) -- feeds only the sync/tone-envelope
    // discriminators above, never reset mid-stream (legacy's Stop() doesn't touch m_lvl; the only
    // Init() call sites are the PTT-transition ones, Sound.cpp:398/443, which for an RX-only decoder
    // map to construction, not EndOfImage). _agcSamples/_levelAgcProcessedUpTo are therefore NOT
    // origin-relative like the other detectors' cursors -- this advances monotonically over the whole
    // _rawSamples stream regardless of EndOfImage's dead-time skip, matching that legacy keeps feeding
    // m_lvl continuously even through cases 512/513's dead zone.
    private readonly LevelAgc _levelAgc;
    private readonly List<double> _agcSamples = [];
    private readonly List<double> _agcCurMaxSamples = []; // LevelAgc.CurMax snapshotted at the same index -- see AgcCurMaxAt
    private int _levelAgcProcessedUpTo;

    // Piece B: forward-fill cache for BandpassFilteredSampleAt, mirroring _agcSamples' own established
    // pattern -- computed at most once per index regardless of call order between the several
    // independent consumers that read this (AgcSampleAt, PushSamples' demodulator feed, both AVT
    // sites). Added after measuring a real, not hypothetical, ~4x full-suite slowdown without it: the
    // filter's O(tap) convolution (up to 97 taps at 44100Hz) was being recomputed from scratch on every
    // call, including redundantly for the SAME index from multiple call sites.
    private readonly List<double> _bandpassFilteredSamples = [];
    private int _bandpassFilteredProcessedUpTo;

    public AnalogFmSstvDecoder(int sampleRate = 11025)
    {
        _sampleRate = sampleRate;
        _demodulator = new HilbertFmDemodulator(sampleRate);
        _searchBandpassFilter = new SearchBandpassFilter(sampleRate);
        _syncBypass1Tracker = new SyncIntervalTracker(sampleRate, isNarrow: false, SstvModeRegistry.GetSyncIntervalCandidates(sampleRate));
        _syncBypass1200Detector = new SyncEnvelopeDetector(sampleRate, 1200.0);
        _syncBypass1900Detector = new SyncEnvelopeDetector(sampleRate, 1900.0);
        _syncBypassTracker = new SyncIntervalTracker(sampleRate, isNarrow: false, SstvModeRegistry.GetSyncIntervalCandidates(sampleRate));
        _syncBypassFskDetector = new SyncEnvelopeDetector(sampleRate, VisHeader.NarrowSpaceFrequencyHz);
        _syncBypassNarrowTracker = new SyncIntervalTracker(sampleRate, isNarrow: true, SstvModeRegistry.GetSyncIntervalCandidates(sampleRate));
        _visLockStateMachine = new VisLockStateMachine(sampleRate, SLvl, SLvl2);
        _levelAgc = new LevelAgc(sampleRate);
        // Band-2 item S5 -- params match TryDecodeVisDataBits' own previous cold-start construction
        // (sstv.cpp:1446-1449).
        _visDataD11Detector = new SyncEnvelopeDetector(sampleRate, 1080.0, bandwidthHz: 80.0);
        _visDataD12Detector = new SyncEnvelopeDetector(sampleRate, 1200.0);
        _visDataD19Detector = new SyncEnvelopeDetector(sampleRate, 1900.0);
        // Band-2 item S14 -- params match TryDecodeNarrowModeHeader's own previous cold-start
        // construction of spaceDetector. The mark detector (1900Hz) needed no new field here: it's
        // the exact same tone/bandwidth as _visDataD19Detector above, so TryDecodeNarrowModeHeader
        // reuses D19At directly instead of a fourth 1900Hz instance.
        _fskSpaceDetector = new SyncEnvelopeDetector(sampleRate, VisHeader.NarrowSpaceFrequencyHz);
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
            _agcSamples.Add(Math.Clamp(ad, -16384.0, 16384.0));
            _agcCurMaxSamples.Add(_levelAgc.CurMax);
        }

        return _agcSamples[Rel(index)];
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
    // Band-1 item 4b: useLocked is `_mode is not null && _mode.NarrowModeCode is null &&
    // thisIndex >= _bandpassLockedFromSample`, evaluated once, at THIS SAME index's own first-
    // computation time (never re-evaluated -- the cache never recomputes an index once set; code-review
    // note, round 4: "thisIndex" here is _bandpassFilteredProcessedUpTo, the index actually being
    // computed by this loop iteration -- deliberately NOT this method's own `index` parameter, which is
    // only the caller's requested upper bound and may already have been satisfied by earlier iterations
    // computing lower indices first). `_mode is not null` excludes both "never locked yet" (field still
    // int.MaxValue) and the between-images gap (EndOfImage resets both _mode and
    // _bandpassLockedFromSample) -- narrow mode's H3/HBPFN stays out of scope (see class doc comment),
    // so `_mode.NarrowModeCode is null` keeps narrow modes on H2 always. `thisIndex >=
    // _bandpassLockedFromSample`, NOT live state alone, is what makes this chunk-invariant AND correct
    // for the handful of samples strictly before the lock anchor that item 4a's fix means get computed
    // AFTER Commit() already fired (auditor code-level review of item 4a, round 3) -- those must stay H2
    // like legacy, not flip to H1 just because _mode happens to be set by the time they're computed.
    private double BandpassFilteredSampleAt(int index)
    {
        for (; _bandpassFilteredProcessedUpTo <= index; _bandpassFilteredProcessedUpTo++)
        {
            var thisIndex = _bandpassFilteredProcessedUpTo;
            var useLocked = _mode is not null && _mode.NarrowModeCode is null && thisIndex >= _bandpassLockedFromSample;
            if (useLocked)
            {
                FirstLockedBandpassIndex ??= thisIndex; // diagnostic-only, see its own doc comment
            }

            _bandpassFilteredSamples.Add(_searchBandpassFilter.ProcessSample(FilteredRawSampleAt(thisIndex), useLocked));
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
            _demodulatedFrequencies.Add(_demodulator.ProcessSample(BandpassFilteredSampleAt(thisIndex) * 32768.0, isNarrow));
        }

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

    // Band-2 item S5 (pre-Phase-2 audit): TryDecodeVisDataBits' own d11/d12/d19 tone-envelope detectors
    // -- legacy's m_iir11/m_iir12/m_iir19 (+ matching m_lpf11/12/19), sstv.h:609-612/614-617. Verified
    // directly against source (auditor plan-review, round 1): CIIRTANK has no Clear() method at all
    // (fir.cpp:40-74) and SetFreq (fir.cpp:46-63) only ever writes the coefficients (b1/b2/a0), never
    // the resonator's own internal state (z1/z2) -- these detectors run with continuous, NEVER-RESET
    // state for the entire life of the real CSSTVDEM object, even across legacy's own AFC-triggered
    // SetFreq re-tunes (sstv.cpp:1698-1701). m_iir12/m_iir19 (d12/d19) are fed UNCONDITIONALLY on every
    // sample (sstv.cpp:1847/1851); m_iir11 (d11) on every sample header-detection is active
    // (sstv.cpp:1893, effectively every sample given m_SyncRestart's hardwired default). This port's
    // TryDecodeVisDataBits used to construct all of these fresh, cold-started, on every single call --
    // repeated on every retry with the SAME headerStart, so the FIRST few samples of every attempt were
    // measurably weaker than legacy's real, long-since-settled detectors. Made persistent instead,
    // mirroring AgcSampleAt/BandpassFilteredSampleAt/DemodulatedFrequencyAt's own established lazy
    // forward-fill pattern exactly -- computed at most once per absolute sample index, regardless of
    // how many times TryDecodeVisDataBits itself gets called or retried for the same headerStart.
    //
    // d13 (m_iir13) is deliberately NOT converted here, even though it looks like the same shape --
    // auditor plan-review (round 1) caught this before it became a real regression: legacy only feeds
    // m_iir13 during case 2/9 (sstv.cpp:1976), so d13's value at a given sample is NOT a pure function
    // of that sample's own index -- it depends on which prior samples the bit-decode loop actually fed
    // it, i.e. on trigger history. An index-keyed forward-fill cache is structurally the wrong container
    // for that. d13 stays a genuine method-local, freshly-constructed, per-call stateful object inside
    // TryDecodeVisDataBits itself, fed only in its own bit-decode loop, exactly as before -- and its
    // cold start costs nothing measurable: its first read is a full BitDurationMs (30ms) after it starts
    // being fed, while an 80Hz-bandwidth resonator settles in ~4ms, so it's fully rung up well before
    // anything ever reads it. That asymmetry (d11/d12/d19 are read from the very first sample of the
    // trigger search; d13 is read only after 30ms of its own settling) is the actual reason S5 matters
    // for the first three and not the fourth.
    //
    // Deliberately NOT unified with the existing, separate _syncBypass1200Detector/_syncBypass1900Detector
    // (which already faithfully port legacy's SAME shared d12/d19 values for the continuous
    // TryInterleavedHeaderScan/TrySyncIntervalDetectionStep fallback path) -- auditor plan-review: doing
    // so would require _syncBypassProcessedUpTo to already be caught up to whatever index
    // TryDecodeVisDataBits needs at call time, which is unverified (TryDecodeVisHeader's fixed-window
    // path is tried BEFORE TryInterleavedHeaderScan's fallback in TryDecodeHeader, so it may genuinely
    // lag). This is not a permanent scope cut, just a smaller one: converting d12/d19 here to
    // index-keyed caches is exactly the prerequisite that would make that future unification mechanical
    // instead of a redesign, when/if it's ever done (see the existing _syncBypass1PrimaryHeld doc
    // comment for the sibling case of this same deferred unification).
    private readonly SyncEnvelopeDetector _visDataD11Detector;
    private readonly List<double> _visDataD11Samples = [];
    private int _visDataD11ProcessedUpTo;

    private readonly SyncEnvelopeDetector _visDataD12Detector;
    private readonly List<double> _visDataD12Samples = [];
    private int _visDataD12ProcessedUpTo;

    private readonly SyncEnvelopeDetector _visDataD19Detector;
    private readonly List<double> _visDataD19Samples = [];
    private int _visDataD19ProcessedUpTo;

    // Band-2 item S14: TryDecodeNarrowModeHeader's own markDetector/spaceDetector (1900Hz/2100Hz)
    // used to be cold-started fresh on every call, same shape S5 already fixed for d11/d12/d19 above
    // -- same fix, same lazy forward-fill pattern. The 1900Hz mark tone doesn't get a new field/cache
    // at all: it's the SAME tone D19At already tracks (legacy's narrow-mode mark IS d19, sstv.cpp's
    // shared m_iir19 -- narrow-mode detection is a variant SyncMode path off the same resonator, not
    // a separate one), so TryDecodeNarrowModeHeader reads D19At directly. Only the 2100Hz space tone
    // is genuinely new.
    private readonly SyncEnvelopeDetector _fskSpaceDetector;
    private readonly List<double> _fskSpaceSamples = [];
    private int _fskSpaceProcessedUpTo;

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
            _visDataD12Samples.Add(_visDataD12Detector.ProcessSample(AgcSampleAt(_visDataD12ProcessedUpTo)));
        }

        return _visDataD12Samples[Rel(index)];
    }

    private double D19At(int index)
    {
        for (; _visDataD19ProcessedUpTo <= index; _visDataD19ProcessedUpTo++)
        {
            _visDataD19Samples.Add(_visDataD19Detector.ProcessSample(AgcSampleAt(_visDataD19ProcessedUpTo)));
        }

        return _visDataD19Samples[Rel(index)];
    }

    public event Action<DecodedImageUpdate>? LineDecoded;

    public event Action<SstvModeDefinition>? ModeDetected;

    public event Action<SstvModeDefinition>? DecodeRestarted;

    public void PushSamples(ReadOnlyMemory<float> samples)
    {
        var span = samples.Span;
        for (var i = 0; i < span.Length; i++)
        {
            _rawSamples.Add(span[i]);
        }

        // Band-1 item 4a: _demodulatedFrequencies is no longer filled eagerly here -- see
        // DemodulatedFrequencyAt's own doc comment for why (this used to race the shared bandpass
        // cache all the way to TotalSamplesReceived before Commit() ever got a chance to run, for any
        // bulk-pushed caller). It's now driven lazily, on demand, by whichever real consumer
        // (PixelSampleReader during line decode, ApplyAfcCorrections, or AverageFrequencyInWindow's
        // pre-lock narrow-mode discriminator) actually needs a given index next.
        TryProcessBuffer();
        AdvanceAgcThroughDeadZone();
        TrimBuffers();
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

    // Band-1 S2 fix (pre-Phase-2 audit): bounds the 5 growing sample buffers (_rawSamples,
    // _demodulatedFrequencies, _agcSamples, _agcCurMaxSamples, _bandpassFilteredSamples), which
    // would otherwise grow without limit for the lifetime of this decoder instance (~5.7GB/hr
    // @44100Hz measured before this fix) -- a real problem the moment a production caller wires this
    // decoder to continuous live capture, not just a test-only decoder's usual short-lived scope.
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
            // pre-lock (AFC/Slant don't exist yet, _afcTracker/_slantTracker are null pre-lock, so
            // they're excluded here, not because they're unsafe to include but because there's
            // nothing to include).
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

            // Code-review finding: TryResolveAvtTraining's own warm-up (from _avtPllWarmupStartSample,
            // Band-2 item S16 -- widened from a clamped constant to legacy's own real ~1850ms
            // contiguous pre-origin m_pll feed span, see TryStartAvtTraining's own doc comment) is NOT
            // separately included in this min() -- it's covered only transitively, because
            // _fixedWindowExhausted is provably false for the entire _avtTrainingPending window
            // (TryDecodeHeader returns before ever reaching TryInterleavedHeaderScan while pending), so
            // the `if` above already pins the watermark at _consumedSamples (= headerStart), which sits
            // before _avtPllWarmupStartSample too (headerStart + totalHeaderSampleCount - one bit
            // period). Correct today; stated explicitly so a future change to when _fixedWindowExhausted
            // is set doesn't silently reopen this (it would fail LOUDLY via Rel()'s own throw if it did,
            // not silently -- but better to not need that safety net's help).

            watermark = Math.Min(watermark, _syncBypassProcessedUpTo);
            watermark = Math.Min(watermark, _visLockProcessedUpTo);
            watermark = Math.Min(watermark, _levelAgcProcessedUpTo);
            watermark = Math.Min(watermark, _bandpassFilteredProcessedUpTo);

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
            watermark = Math.Min(_afcProcessedUpTo, _slantProcessedUpTo);
            watermark = Math.Min(watermark, _visLockProcessedUpTo);
            watermark = Math.Min(watermark, _levelAgcProcessedUpTo);
            watermark = Math.Min(watermark, _bandpassFilteredProcessedUpTo);
            watermark = Math.Min(watermark, _demodulatedFrequenciesProcessedUpTo); // Band-1 item 4a, see above
            watermark = Math.Min(watermark, _consumedSamples);
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
        // their own Min() chain) -- so DemodulatedFrequencyAt(watermark - 1) here can never advance
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
        // post-lock consumers), all four of these are read only by pre-lock-only callers: D11At/D12At by
        // TryDecodeVisDataBits alone; D19At by BOTH TryDecodeVisDataBits and TryDecodeNarrowModeHeader
        // (S14 -- legacy's shared m_iir19, sstv.cpp:1851/1858, read by both the VIS tone race and the
        // narrow-mode FSK packet decode); FskSpaceAt by TryDecodeNarrowModeHeader alone. All of these
        // callers are themselves only ever invoked pre-lock (via TryDecodeVisHeader/TryDecodeNarrowModeHeader/
        // TryDecodeHeader). Once locked, all four simply freeze wherever they were at the moment of lock --
        // the exact same "frozen once locked" shape _syncBypassProcessedUpTo's own doc comment above
        // already describes for a different cursor, not a new pattern. Including them in the locked
        // branch's Min() chain would pin the watermark at that frozen value for the whole image,
        // blocking trimming during decode; including them in the pre-lock branch hits the exact
        // permanently-pinned-near-0 failure shape _demodulatedFrequenciesProcessedUpTo's own comment
        // documents. So: excluded from watermark in both branches, safety guaranteed purely by this
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

                FinalizeAnchorAndStartDecoding(_pendingAnchorCorrectionMode);
                _pendingAnchorCorrectionMode = null;
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
                var lineSampleCount = (int)Math.Round(_effectiveSamplesPerLine);
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
                ApplyAfcCorrections(_consumedSamples + lineSampleCount);

                // Piece 10: PixelSampleReader is constructed fresh per line, not per mode/session --
                // GetKsbSamples depends on effectiveSampleRate, which this port recomputes per line
                // for Auto Slant (matching legacy's own per-line m_KSB recompute-on-slant-change,
                // Main.cpp:4015/:5900-5903). lineEndSampleExclusive is this line's own extent, used
                // only by the (currently unreachable at every real mode) line-end guard.
                var reader = new PixelSampleReader(
                    index => DemodulatedFrequencyAt(Math.Clamp(index, _bufferBase, TotalSamplesReceived - 1)),
                    SstvModeRegistry.GetKsbSamples(mode, effectiveSampleRate),
                    _consumedSamples + lineSampleCount,
                    mode.LuminanceMinHz,
                    SstvModeRegistry.NeverPeakPicks(mode));

                lineDecoder.DecodeLine(mode, effectiveSampleRate, _consumedSamples, _nextLine, reader, pixels);
                _consumedSamples += lineSampleCount;

                LineDecoded?.Invoke(new DecodedImageUpdate(_nextLine, new MutableImageSource(mode.ImageWidth, mode.ImageHeight, pixels)));
                _nextLine += lineDecoder.RowsPerTransmissionLine;

                // Now that this line is fully decoded and _consumedSamples reflects it, let slant
                // tracking catch up through exactly this line's raw samples -- never further ahead,
                // and never for a line that hasn't been decoded yet.
                ApplySlantTracking();

                // Piece 6c: legacy's case-0 trigger (sstv.cpp:1946-1950) carries no `!m_Sync` guard on
                // the transition itself, so it keeps running even while m_Sync is true and a
                // stronger/cleaner new lock found mid-reception aborts and restarts on it (Start()
                // resets m_SyncMode back to 0 unconditionally) -- true at legacy's own shipped
                // defaults: the whole switch only runs while locked because the enclosing gate at
                // sstv.cpp:1889, `!m_Sync || m_SyncRestart || m_SyncAVT`, is satisfied by
                // m_SyncRestart defaulting to 1 (sstv.cpp:1486), a real user-toggleable option
                // (spec/14-roadmap.md) this port hard-wires on with no way to disable -- round-2-review
                // correction, an earlier version of this comment said "ungated" without that
                // qualification. Checked once per decoded line, not once per TryProcessBuffer
                // call: for a bulk-pushed buffer containing a whole (possibly truncated)
                // transmission followed immediately by a second one, the loop above would otherwise
                // just keep decoding every available sample as if it were more lines of the *first*
                // transmission -- it has no notion of "this content doesn't actually belong to this
                // image" -- and would never return control to notice the second transmission's real
                // header at all. Only VisLockStateMachine runs here, not the fixed-window path (which
                // assumes _consumedSamples is a header start, not mid-image) or TrySyncIntervalDetectionStep
                // (m_sint2 is hard-gated behind !m_Sync at every call site in legacy -- case 0's shared
                // guard, sstv.cpp:1899, and its own case-1 guard, sstv.cpp:1953; m_sint3's calls at
                // sstv.cpp:1927-1937 sit inside the same case-0 :1899 guard -- they must not run while
                // locked; round-2-review fix, an earlier version of this citation pointed at m_sint1's
                // own gates, sstv.cpp:1949/1959, by mistake). Bounded to
                // _consumedSamples (the current decode position), NOT the whole buffer -- see
                // TryVisLockStateMachine's own doc comment for the bulk-vs-streaming bug this bound fixes.
                if (TryVisLockStateMachine(_consumedSamples))
                {
                    restarted = true;
                    // The abandoned (local `mode`, captured at the top of this outer-loop iteration),
                    // not the new one -- TryVisLockStateMachine already called Commit(), which fired
                    // ModeDetected for the *new* mode before we get here. Passing that same new mode
                    // to DecodeRestarted too (an earlier version did, via `_mode!`) is a trap review
                    // caught: a caller that allocates a buffer on ModeDetected and discards on
                    // DecodeRestarted would discard the buffer it just allocated for the new mode,
                    // not the old one it actually needs to throw away.
                    DecodeRestarted?.Invoke(mode);
                    break;
                }
            }

            if (restarted)
            {
                continue; // TryVisLockStateMachine already Commit()-ed the new transmission -- decode it from scratch
            }

            if (_nextLine >= mode.ImageHeight)
            {
                EndOfImage();
                continue;
            }
        }
    }

    // Direct port of Stop() (sstv.cpp:1769-1791) + cases 512/513's 0.5s dead-time wait
    // (sstv.cpp:2243-2252), called once _nextLine reaches mode.ImageHeight -- i.e. right where
    // legacy's own `m_AY > SSTVSET.m_L` check calls Stop() (Main.cpp:5014-5017). Modeled as an
    // analytic skip rather than a literal per-sample countdown (the same style already used for
    // fixed-duration header skips): jump straight to _consumedSamples + 0.5s worth of samples,
    // rather than simulating 0.5s of idle per-sample ticks.
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
    private void EndOfImage()
    {
        var resumeFrom = _consumedSamples + (int)Math.Round(0.5 * _sampleRate);

        _mode = null;
        _lineDecoder = null;
        _pixels = null;
        _nextLine = 0;
        _bandpassLockedFromSample = int.MaxValue; // Band-1 item 4b -- see field's own doc comment

        _afcTracker = null;
        _syncEnvelopeDetector = null;
        _slantTracker = null;

        _avtTrainingPending = false;
        _avtTrainingLock = null;
        _avtPllDemodulator = null;

        _fixedWindowExhausted = false;

        _syncBypass1Tracker.Reset();
        _syncBypass1PrimaryHeld = false;
        _syncBypassTracker.Reset();
        _syncBypassNarrowTracker.Reset();
        _syncBypassNarrowPhaseActive = false;
        _syncBypassProcessedUpTo = resumeFrom;
        _syncBypassOriginSample = resumeFrom;

        _visLockStateMachine.Reset();
        _visLockProcessedUpTo = resumeFrom;
        _visLockOriginSample = resumeFrom;

        _consumedSamples = resumeFrom;

        _agcDeadZoneCatchUpTarget = resumeFrom;
    }

    // Discriminates between a normal/extended-VIS header and an MN/MC narrow-mode-announce packet
    // (see VisHeader.GenerateNarrowModeSegments) before either has fully arrived. Both start with
    // a 300ms/1900Hz leader, but diverge immediately after: normal VIS goes to a brief 1200Hz
    // break (300-310ms) then back to 1900Hz leader until 610ms, while the narrow packet holds
    // 2100Hz guard tone for the full 300-400ms window. A window placed at [320,380]ms is safely
    // inside "1900Hz leader2" for the VIS case and "2100Hz guard" for the narrow case in every
    // real capture, so a single average-frequency comparison against the 1900/2100 midpoint
    // (2000Hz) reliably tells them apart without needing to fully decode either one first.
    private const double NarrowDiscriminatorWindowStartMs = 320;
    private const double NarrowDiscriminatorWindowEndMs = 380;
    private const double NarrowDiscriminatorThresholdHz = (VisHeader.LeaderFrequencyHz + VisHeader.NarrowSpaceFrequencyHz) / 2;

    private bool TryDecodeHeader()
    {
        // AVT's training-lock resolution spans multiple TryDecodeHeader calls once entered (its
        // completion point is data-dependent, not a fixed duration) -- while pending, skip straight
        // back into it rather than re-running header detection or falling through to the other
        // fallbacks, which would be wrong once the mode is already known to be AVT.
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
    private bool TryVisLockStateMachine(int upperBoundSample)
    {
        var bound = Math.Min(TotalSamplesReceived, upperBoundSample);
        for (; _visLockProcessedUpTo < bound; _visLockProcessedUpTo++)
        {
            var result = _visLockStateMachine.ProcessSample(AgcSampleAt(_visLockProcessedUpTo));
            if (result is null)
            {
                continue;
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

    // Legacy's trusted subset for VIS-bypass mode switching (sstv.cpp:1912-1922) -- SyncIntervalTracker
    // (m_sint2's underlying SyncCheck/SyncCheckSub) can in principle recognize other candidate modes
    // too, but legacy's own switch only ever acts on these four (`case smSCT1/smMRT1/smMRT2/
    // smSC2_180: ... default: break;`) -- every other match is silently ignored, so this port
    // ignores them the same way rather than acting on a broader set than legacy itself trusts here.
    private static readonly HashSet<SstvModeDefinition> SyncBypassTrustedModes =
    [
        SstvModeRegistry.ScottieS1,
        SstvModeRegistry.MartinM1,
        SstvModeRegistry.MartinM2,
        SstvModeRegistry.Sc2180,
    ];

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

        // m_sint1 (sstv.cpp:1900-1904) -- checked FIRST every sample, top priority, no mode
        // allowlist, but ONLY while NOT currently holding the primary threshold: legacy's
        // SyncStart() is polled from case 0 alone (sstv.cpp:1900), never from case 1
        // (sstv.cpp:1952-1973 calls SyncMax there, never SyncStart). Bug fixed by independent
        // review: an earlier version called TryStart() unconditionally every sample, which meant
        // the very next sample after Trigger() latched a peak would immediately consume it (via
        // SyncIntervalTracker.TryStart's unconditional _peakAmplitude=0), before SyncMax ever got
        // a chance to track the pulse's real running max -- anchoring every match at the
        // threshold-crossing edge instead of the envelope peak GetSyncSegmentMidpointOffsetMs
        // assumes, and (worse) letting VIS data-bit tones (1100/1300Hz, only ±100Hz from d12's
        // 1200Hz/100Hz-bandwidth center) spuriously re-trigger it throughout every VIS-bit-decode
        // attempt, polluting the interval history legacy's own case-2/9 freeze would have
        // prevented. Gating on !_syncBypass1PrimaryHeld reproduces that freeze for m_sint1
        // specifically (m_sint2 already has an equivalent effect for free, see its own condition
        // below -- its held-branch check subsumes its TryStart branch's threshold, so a held
        // m_sint2 never calls TryStart either; m_sint1's bare top-of-loop poll had no such
        // built-in protection).
        if (!_syncBypass1PrimaryHeld)
        {
            var sint1Matched = _syncBypass1Tracker.TryStart();
            if (sint1Matched is not null)
            {
                CommitSyncBypassMatch(sint1Matched, _syncBypass1Tracker.LastPeakPositionSamples);
                return true;
            }
        }

        // m_sint2 (sstv.cpp:1899-1911). Piece 7c: full 3-term condition (sstv.cpp:1905), not just
        // the relative d12>d19 -- d12>SLvl2 and the difference gate (d12-d19)>=SLvl2 are what
        // actually suppress false candidate peaks now that d12/d19 live on the AGC'd scale.
        if (d12 > d19 && d12 > SLvl2 && d12 - d19 >= SLvl2)
        {
            _syncBypassTracker.UpdateMax(d12);
        }
        else
        {
            var matched = _syncBypassTracker.TryStart();
            if (matched is not null && SyncBypassTrustedModes.Contains(matched))
            {
                CommitSyncBypassMatch(matched, _syncBypassTracker.LastPeakPositionSamples);
                return true;
            }
        }

        // m_sint3 (sstv.cpp:1924-1946) -- explicit SyncTrig-then-SyncMax edge latch, SyncStart
        // called once on the falling edge only, matching legacy's own m_SyncPhase gating. Piece
        // 7c: full 5-term condition (sstv.cpp:1926) -- note the last difference term is gated by
        // SLvl (not SLvl3), an asymmetry confirmed by reading the literal source, not assumed.
        if (d19 > d12 && d19 > dsp && d19 > SLvl3 && d19 - d12 >= SLvl3 && d19 - dsp >= SLvl)
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
        if (d12 > d19 && d12 > SLvl && d12 - d19 >= SLvl)
        {
            if (_syncBypass1PrimaryHeld)
            {
                _syncBypass1Tracker.UpdateMax(d12);
            }
            else
            {
                _syncBypass1Tracker.Trigger(d12);
                _syncBypass1PrimaryHeld = true;
            }
        }
        else
        {
            _syncBypass1PrimaryHeld = false;
        }

        return false;
    }

    // Piece: m_sint1 decoder-ordering fix (spec/14-roadmap.md: "m_sint1's decoder-level priority is
    // effectively inverted from legacy's real per-sample interleaving"). TrySyncIntervalDetectionStep
    // (m_sint1/m_sint2/m_sint3) and VisLockStateMachine.ProcessSample are now interleaved sample by
    // sample instead of each scanning the *entire* available buffer before the other gets a turn --
    // see TryDecodeHeader's own doc comment for why the old sequential shape was a real bug, not
    // just a stylistic difference. Reproduces sstv.cpp:1897-1951's real per-sample order exactly:
    // m_sint1/m_sint2/m_sint3 (case 0's `if (!m_Sync && m_MSync)` block) are checked first, every
    // sample, and only then (a sibling statement, same sample) the primary VIS-leader threshold that
    // drives m_SyncMode's case 0->1 transition -- TrySyncIntervalDetectionStep's own last statement
    // is a deliberate second copy of that same threshold (see its doc comment), so calling it before
    // VisLockStateMachine.ProcessSample reproduces legacy's real order.
    //
    // _syncBypassProcessedUpTo and _visLockProcessedUpTo are NOT always kept in lockstep outside
    // this method -- Commit() only fast-forwards _visLockProcessedUpTo (via Math.Max) when some
    // OTHER path (the fixed-window header paths) committed a match, and never touches
    // _syncBypassProcessedUpTo at all. But this method is only ever entered when _mode is null, and
    // the only place that becomes true again once a transmission has started is EndOfImage, which
    // always resets both cursors to the same resumeFrom -- so they are always equal on entry here,
    // confirmed by checking every _mode assignment in this file, even though nothing enforces that
    // generally.
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
        for (; _syncBypassProcessedUpTo < scanBound; _syncBypassProcessedUpTo++, _visLockProcessedUpTo++)
        {
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

    /// <summary>Diagnostic-only: fires the PROVISIONAL, immediate lock anchor the moment
    /// <see cref="Commit"/> sets it -- not <see cref="ModeDetected"/>, which is deferred until
    /// <c>TryResolveSyncAnchorCorrection</c> succeeds, several lines (and several buffered picture
    /// lines) later. Kept as permanent test infrastructure (Band-1 item 4a, pre-Phase-2 audit): this
    /// is what let a real spike measure how far <see cref="BandpassFilteredProcessedUpTo"/> had
    /// already raced ahead of the true lock instant, which is exactly the quantity item 4a's fix
    /// needed to bound -- see <c>BandpassCacheChunkInvarianceTests</c>.</summary>
    internal event Action<int>? LockAnchorCommitted;

    private void Commit(SstvModeDefinition matched, int lineStartSample)
    {
        _consumedSamples = Math.Max(0, lineStartSample);
        LockAnchorCommitted?.Invoke(_consumedSamples);
        _bandpassLockedFromSample = _consumedSamples; // Band-1 item 4b -- see field's own doc comment
        _mode = matched;
        _lineDecoder = ScanlineCodecFactory.CreateDecoder(matched.ColorEncoding);
        _pixels = new Rgb24[matched.ImageWidth * matched.ImageHeight];
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
        // (Option.cpp:611, Main.cpp:1857/10907/11887, already logged at spec/14-roadmap.md) that this
        // port hard-wires on with no way to disable. So "the same way legacy does" means "at legacy's
        // shipped defaults," not "unconditionally in every configuration." That framing correction
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

        // Piece 8c: AVT has no equivalent of this mechanism at all -- legacy's SyncSSTV itself
        // early-outs for smAVT (Main.cpp:3754-3758: zeroes the offset, clears m_wBgn immediately,
        // no waiting for buffered lines), so AFC/slant initialize immediately here exactly as before
        // this piece existed. Every other mode defers to TryResolveSyncAnchorCorrection (called from
        // TryProcessBuffer, once enough samples are buffered) -- see _pendingAnchorCorrectionMode's
        // own doc comment for why the deferral is needed.
        if (matched == SstvModeRegistry.Avt)
        {
            FinalizeAnchorAndStartDecoding(matched);
        }
        else
        {
            _pendingAnchorCorrectionMode = matched;
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
        ModeDetected?.Invoke(matched);
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
        // && SSTVSET.m_TW >= SSTVSET.m_SampFreq` -- both settings default ON (Main.cpp:730/899, no
        // UI/config knob this port has an equivalent of yet), so this reduces to exactly
        // LineDurationMs >= 1000.0 -- a one-line exact port, not a simplification, verified against
        // source during plan-review. Reachable for Scottie DX/PD240/MP140/MP175/MN140 -- not either
        // golden-vector fixture, but a real divergence for those modes if skipped.
        var lineCount = mode.LineDurationMs >= 1000.0 ? 3 : 4;
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
        // must move later too). Unconditional here, unlike legacy's `m_Type==2` check -- this port's
        // main picture path is always HilbertFmDemodulator now, no PLL/Hilbert branching needed.
        delta += _demodulator.HalfTap / 4;

        // Legacy's own equivalent of a negative result is DrawSSTVNormal skipping samples whose
        // phase is still negative (`if (n<0) continue`, Main.cpp:4146) rather than reading earlier
        // samples that were never buffered. Clamping to 0 here is the direct equivalent for a
        // sample-cursor variable that cannot legitimately go negative (it indexes _rawSamples from
        // its own start) -- flagged by review as a real edge case (a correction up to -OFP, ~118
        // samples for Robot 36, applied very early in a short buffer could clamp) but expected to be
        // rare in practice: real transmissions carry several seconds of lead-in before the image.
        _consumedSamples = Math.Max(0, origin + delta);

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

        return true;
    }

    // Piece 13: replaces a prior proxy (averaged the shared PLL's demodulated-frequency stream over
    // fixed windows against a midpoint threshold) with a literal port of legacy's real per-sample
    // decoder, NarrowFskHeaderDecoder (CSSTVDEM::DecodeFSK, sstv.cpp:2378-2606) -- the same bug shape
    // Piece 9 already fixed for VIS-bit decode. Two rounds of auditor plan-review verified the state
    // machine itself line-by-line against source before this was written; see that class's own doc
    // comment for the full state-transition derivation.
    //
    // Search ceiling mirrors TryDecodeVisDataBits' own (:1207-1212) for the same reason: an unbounded
    // retry-on-reject scanner is by construction what this decoder is (every legacy failure path
    // resumes scanning from mode 0), and an unbounded version was a real, reverted regression there.
    // Narrow modes keep their existing _syncBypassNarrowTracker fallback (m_sint3) if this local,
    // bounded scan misses a rare edge case. Ceiling = guard(100ms) + timeout(100ms) + start-bit(22ms)
    // + 24 data bits' worth (24*22ms) + a 200ms retry margin, matching TryDecodeVisDataBits' own
    // generous-but-local shape.
    //
    // Commit point: headerStart + the packet's fixed nominal duration (VisHeader.NarrowHeaderTotalDurationMs),
    // NOT whatever sample NarrowFskHeaderDecoder happens to lock on -- confirmed by round-2 auditor
    // review against TX's real placement (Main.cpp:7423-7424 puts image data at a fixed offset after
    // the checksum bits, not at Start()'s slightly-earlier legacy firing point) and matching this
    // method's own prior (buggy) behavior, which the currently-passing
    // SstvRoundTripTests.NarrowModeHeader_IsDetected_ForMnFamily test already relies on.
    private bool TryDecodeNarrowModeHeader()
    {
        var totalHeaderSampleCount = (int)Math.Round(VisHeader.NarrowHeaderTotalDurationMs / 1000.0 * _sampleRate);
        if (TotalSamplesReceived - _consumedSamples < totalHeaderSampleCount)
        {
            return false;
        }

        var headerStart = _consumedSamples;
        var searchCeiling = headerStart + MsToSamples(
            VisHeader.NarrowGuardDurationMs * 2 // guard hold + mode-2's own timeout window
            + VisHeader.NarrowBitDurationMs * (1 + 24) // start-bit training pulse + 24 data bits
            + 200); // retry margin, matching TryDecodeVisDataBits' own shape
        var availableUpTo = Math.Min(TotalSamplesReceived, searchCeiling);

        // Band-2 item S14: mark/space no longer cold-started fresh per call (see D19At/FskSpaceAt's
        // own doc comments) -- confirmed directly against legacy (sstv.cpp:1851/1855/1858): d19 and
        // dsp (m_iirfsk) are both continuously-running, unconditionally-fed-every-sample resonators,
        // and DecodeFSK(int(d19), int(dsp)) is likewise called every sample, not just while searching
        // for a narrow-mode packet -- exactly the shape these two caches now reproduce. fskDecoder
        // itself (the case-based bit/byte state machine) stays fresh per call, matching legacy's own
        // per-attempt CSSTVDEM member state for that piece; only the two resonators feeding it are
        // shared/persistent.
        var fskDecoder = new NarrowFskHeaderDecoder(_sampleRate);

        for (var sample = headerStart; sample < availableUpTo; sample++)
        {
            var m = (int)D19At(sample);
            var s = (int)FskSpaceAt(sample);

            var modeCode = fskDecoder.ProcessSample(m, s);
            if (modeCode is null)
            {
                continue;
            }

            var mode = SstvModeRegistry.FindByNarrowCode(modeCode.Value);
            if (mode is null)
            {
                // Unregistered mode code -- legacy resumes scanning rather than giving up
                // (sstv.cpp:2588-2597), and so does NarrowFskHeaderDecoder internally; this port's
                // caller has no further chances at this headerStart, matching TryDecodeVisDataBits'
                // own "not part of this bug" out-of-scope note for the equivalent VIS case.
                return false;
            }

            _consumedSamples = headerStart + totalHeaderSampleCount;

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
    // spuriously satisfied the trigger+hold before the real 30ms start-bit tone was ever reached. For
    // every mode except AVT, VisLockStateMachine's own independent scan is a fallback that recovers
    // from this anyway -- but VisLockStateMachine deliberately never reports AVT (see its own class
    // doc comment), making this method AVT's ONLY detector, with no second chance.
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

                if (d12 > d19 && d12 > SLvl && d12 - d19 >= SLvl)
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
                    if (!VisBitDecision.TryDecide(d11, d13, d19, SLvl2, out var bit))
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

    private int MsToSamples(double ms) => (int)Math.Round(ms / 1000.0 * _sampleRate);

    private bool TryDecodeVisHeader()
    {
        // Prefix (leader/break/leader/start-bit + first 7 data bits) is the same length whether
        // this is a normal single-byte VIS code or an "extended" MR/MP/ML one (see VisHeader) — we
        // don't know which until those 7 bits are decoded, so read the prefix first, then decide.
        var prefixSampleCount = (int)Math.Round(VisHeader.PrefixDurationMs / 1000.0 * _sampleRate);
        if (TotalSamplesReceived - _consumedSamples < prefixSampleCount)
        {
            return false;
        }

        var headerStart = _consumedSamples;

        var firstByteBits = TryDecodeVisDataBits(headerStart, VisHeader.DataBitCount);
        if (firstByteBits is null)
        {
            return false; // weak/ambiguous tone race -- legacy aborts the whole attempt (sstv.cpp:1983)
        }

        var firstByteValue = VisHeader.DecodeVisCode(firstByteBits);
        var isExtended = firstByteValue == VisHeader.ExtendedVisEscapeCode;
        var tailDurationMs = isExtended ? VisHeader.ExtendedTailDurationMs : VisHeader.NormalTailDurationMs;
        var totalHeaderSampleCount = (int)Math.Round((VisHeader.PrefixDurationMs + tailDurationMs) / 1000.0 * _sampleRate);

        if (TotalSamplesReceived - headerStart < totalHeaderSampleCount)
        {
            return false; // wait for the rest of the header before consuming/deciding
        }

        SstvModeDefinition? mode;
        if (isExtended)
        {
            // 1 leftover bit from the escape byte (its bit 7, unused) + all 8 bits of the real
            // extended-mode byte = 9 more bit-slots before the stop bit -- windows 7-15 of the same
            // continuous tone race, not a fresh decode (TryDecodeVisDataBits recomputes bits 0-6
            // too, deterministically identical to firstByteBits above; harmless redundancy).
            var allBits = TryDecodeVisDataBits(headerStart, VisHeader.DataBitCount + 9);
            if (allBits is null)
            {
                return false;
            }

            var remainingBits = allBits[VisHeader.DataBitCount..];
            var extendedCode = VisHeader.DecodeRawByte(remainingBits.AsSpan(1, 8));
            mode = SstvModeRegistry.FindByExtendedCode(extendedCode);
        }
        else
        {
            mode = SstvModeRegistry.FindByVisCode(firstByteValue);
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
            return TryStartAvtTraining(headerStart, totalHeaderSampleCount);
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
    // the table for real captured audio with clock drift): once headerStart + totalHeaderSampleCount
    // + 2 more VIS repeats' worth of samples are available, start feeding already-demodulated
    // frequencies into a training-lock instance -- NOT the same point legacy's own case 3 hands off
    // to case 4 (that's right after the *first* VIS repeat, ~1835ms earlier; legacy's cases 4-8 then
    // spend the 2nd/3rd repeats' own audio as failed marker-search noise before reaching real
    // training content). This port instead skips straight past all 3 repeats before constructing
    // AvtTrainingLockStateMachine at all, which is why that class's own internal timeout budget is
    // scoped to just the training sequence's own duration, not legacy's full case-3 figure -- see
    // that class's doc comment. VisHeader.AvtExtraHeaderDurationMs's already-tested fixed duration
    // is kept as a hard ceiling here (matching legacy's own real fallback shape: if the training
    // lock never confirms a lock, completion converges on very close to this same fixed duration).
    private bool TryStartAvtTraining(int headerStart, int totalHeaderSampleCount)
    {
        _avtTrainingOriginSample = headerStart + totalHeaderSampleCount + (int)Math.Round(2 * VisHeader.AvtVisBlockDurationMs / 1000.0 * _sampleRate);
        _avtTrainingFallbackDeadlineSample = headerStart + totalHeaderSampleCount
            + (int)Math.Round(VisHeader.AvtExtraHeaderDurationMs / 1000.0 * _sampleRate);
        _avtTrainingLock = new AvtTrainingLockStateMachine(_sampleRate);
        _avtTrainingProcessedUpTo = _avtTrainingOriginSample;
        _avtTrainingPending = true;
        _avtPllDemodulator = new PllFmDemodulator(_sampleRate, DemodulatorLowHz, DemodulatorHighHz);
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
        _avtPllWarmupStartSample = headerStart + totalHeaderSampleCount - MsToSamples(VisHeader.BitDurationMs);

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
                _avtPllDemodulator!.ProcessSample(BandpassFilteredSampleAt(w) * 32768.0);
            }

            _avtPllWarmedUp = true;
        }

        // Piece A/B: BandpassFilteredSampleAt, not raw -- legacy's real AVT input is `ad` (sstv.cpp:1835,
        // POST-2-tap-LPF-POST-bandpass-filter, post-AGC, unscaled), a domain this port doesn't model
        // at all (only "raw" and "AGC+x32+clip" exist here). Adding both filters closes two of the
        // three missing stages and is unambiguously closer to legacy; the AGC-domain gap stays exactly
        // as already flagged and deferred from the Hilbert demodulator piece, not expanded into here.
        while (_avtTrainingProcessedUpTo < TotalSamplesReceived)
        {
            var avtDemodulatedHz = _avtPllDemodulator!.ProcessSample(BandpassFilteredSampleAt(_avtTrainingProcessedUpTo) * 32768.0);
            var completedAt = _avtTrainingLock!.ProcessSample(avtDemodulatedHz);
            _avtTrainingProcessedUpTo++;

            if (completedAt is not null)
            {
                _avtTrainingPending = false;
                Commit(SstvModeRegistry.Avt, _avtTrainingOriginSample + completedAt.Value);
                return true;
            }

            if (_avtTrainingProcessedUpTo >= _avtTrainingFallbackDeadlineSample)
            {
                _avtTrainingPending = false;
                Commit(SstvModeRegistry.Avt, _avtTrainingFallbackDeadlineSample);
                return true;
            }
        }

        return false;
    }

    // Direct port of CSSTVDEM::SyncFreq's setup (InitAFC/SetSampFreq, sstv.cpp) -- legacy explicitly
    // excludes AVT from AFC entirely (sstv.cpp:2258's `SSTVSET.m_Mode != smAVT` guard at the call
    // site), so this leaves _afcTracker null for it. Sync target/band/BWH switch on the MN/MC
    // narrow family (NARROW_SYNC=1900/NARROW_AFCLOW=1800/NARROW_AFCHIGH=1950/NARROW_BWH=128 vs.
    // normal 1200/1000/1325/400); AFCB/AFCW timing switches on a different, overlapping grouping
    // (SstvModeRegistry.IsFastAfcGroup) -- see AfcTracker's doc comment for both.
    //
    // Piece: Hilbert demodulator port -- no longer constructs a ZeroCrossingFrequencyCounter here.
    // Legacy's real AFC source depends on m_Type (sstv.cpp:2255-2269): case 0/PLL feeds SyncFreq from
    // m_fqc.Do(...) (the zero-crossing counter, independent of the picture demodulator's own output);
    // cases 1/2 (zero-crossing/Hilbert) feed SyncFreq from the SAME `d` already used for the picture
    // stream. This port's main picture path is now HilbertFmDemodulator (case 2's real shape), so AFC
    // now reads directly from the already-demodulated buffer instead -- see ApplyAfcCorrections.
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

        if (mode == SstvModeRegistry.Avt)
        {
            _afcTracker = null;
            return;
        }

        var isNarrow = mode.NarrowModeCode is not null;
        var (syncTargetHz, bandLowHz, bandHighHz, bandwidthHalfHz) = isNarrow
            ? (1900.0, 1800.0, 1950.0, 128.0)
            : (1200.0, 1000.0, 1325.0, 400.0);
        var (afcBeginMs, afcWidthMs) = SstvModeRegistry.IsFastAfcGroup(mode) ? (1.0, 2.0) : (1.5, 3.0);

        _afcTracker = new AfcTracker(_sampleRate, syncTargetHz, bandLowHz, bandHighHz, afcBeginMs, afcWidthMs, bandwidthHalfHz);
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
            // Piece: Hilbert demodulator port -- re-sourced from sstv.cpp:2265-2270 (case 2/Hilbert),
            // not case 0/PLL as an earlier version of this method modeled (case 0 feeds SyncFreq from
            // a SEPARATE zero-crossing counter, m_fqc.Do(...), independent of the picture
            // demodulator's own output; case 2 feeds SyncFreq from the SAME `d` already used for the
            // picture stream: `d = m_hill.Do(m_lvl.m_Cur); ...; SyncFreq(d);`). The `m_CurMax > 16`
            // gate's own rationale differs from before too, though the CODE shape stays the same:
            // under case 0 the gate wraps the frequency counter's own read; under case 2 the
            // demodulator (HilbertFmDemodulator) already ran unconditionally as part of the main
            // per-sample demodulation pass in PushSamples -- only feeding AfcTracker is gated here,
            // matching legacy's real case-2 shape (`m_afc && m_CurMax>16 && mode!=AVT`, wrapping only
            // the SyncFreq call, not the m_hill.Do() call before it). `m_afc` itself is legacy's own
            // always-on default (sstv.cpp:1471 -- no separate toggle to model; AVT's exclusion is
            // already handled by _afcTracker staying null, see InitializeAfc).
            //
            // Reads _demodulatedFrequencies BEFORE this same iteration's own correction is added to
            // it below -- matching legacy's exact sequencing, where SyncFreq(d) is called with the
            // pre-correction `d`, and `d += m_AFCDiff` happens afterward (sstv.cpp:2270).
            if (AgcCurMaxAt(_afcProcessedUpTo) > 16.0)
            {
                // Band-1 item 4a: DemodulatedFrequencyAt (not direct indexing) ensures this index is
                // actually filled before reading it -- the eager per-sample fill this used to rely on
                // is gone. The in-place mutation below stays direct indexing: the accessor call above
                // already guarantees the slot exists.
                var measuredFrequencyHz = DemodulatedFrequencyAt(_afcProcessedUpTo);
                var correctionHz = _afcTracker.ProcessSample(measuredFrequencyHz);
                _demodulatedFrequencies[Rel(_afcProcessedUpTo)] += correctionHz;
            }
        }
    }

    // Direct port of Auto Slant's setup (InitAutoStop, Main.cpp:3801-3862) -- excluded for AVT, same
    // as AFC (Main.cpp:3886's `mode != smAVT` guard covers both features via the same outer gate).
    // See SlantTracker's doc comment for what's deliberately not ported (Auto Stop, Auto Sync, and
    // retroactive re-decode of already-buffered lines) and why the math itself still is.
    private void InitializeSlant(SstvModeDefinition mode)
    {
        _slantProcessedUpTo = _consumedSamples;
        _effectiveSamplesPerLine = mode.LineDurationMs / 1000.0 * _sampleRate;
        _slantIdealSamplesSoFarInLine = 0;
        _slantLineMaxEnvelope = double.NegativeInfinity;
        _slantLinePeakPosition = 0;

        if (mode == SstvModeRegistry.Avt)
        {
            _syncEnvelopeDetector = null;
            _slantTracker = null;
            return;
        }

        var isNarrow = mode.NarrowModeCode is not null;
        _syncEnvelopeDetector = new SyncEnvelopeDetector(_sampleRate, isNarrow ? 1900.0 : 1200.0);
        _syncSegmentOffsetSamples = SstvModeRegistry.GetSyncSegmentOffsetMs(mode) / 1000.0 * _sampleRate;
        _slantTracker = new SlantTracker(_sampleRate, _effectiveSamplesPerLine, SstvModeRegistry.GetAutoSlantThresholdPositions(mode));
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

            if (envelope > _slantLineMaxEnvelope)
            {
                _slantLineMaxEnvelope = envelope;
                _slantLinePeakPosition = _slantIdealSamplesSoFarInLine;
            }

            _slantIdealSamplesSoFarInLine += 1;

            if (_slantIdealSamplesSoFarInLine < _effectiveSamplesPerLine)
            {
                continue;
            }

            // Line complete. Report the peak position relative to this mode's expected sync offset,
            // wrapped to the representation closest to zero (Main.cpp:5877-5883-style centering) --
            // mirrors legacy's own m_AutoStopPos wraparound, needed so a peak that lands just before
            // vs. just after the line boundary isn't reported as a huge spurious jump.
            var relative = _slantLinePeakPosition - _syncSegmentOffsetSamples;
            var half = _effectiveSamplesPerLine / 2.0;
            if (relative > half)
            {
                relative -= _effectiveSamplesPerLine;
            }
            else if (relative < -half)
            {
                relative += _effectiveSamplesPerLine;
            }

            var correctedSampleRate = _slantTracker.ProcessLine(relative);
            if (correctedSampleRate is not null)
            {
                _effectiveSamplesPerLine = _mode!.LineDurationMs / 1000.0 * correctedSampleRate.Value;
            }

            _slantIdealSamplesSoFarInLine -= _effectiveSamplesPerLine; // carry remainder, don't reset to 0 -- keeps line boundaries from drifting
            _slantLineMaxEnvelope = double.NegativeInfinity;
            _slantLinePeakPosition = 0;
        }
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

    private sealed class MutableImageSource(int width, int height, Rgb24[] pixels) : IImageSource
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public ReadOnlySpan<Rgb24> GetScanline(int y) => pixels.AsSpan(y * Width, Width);
    }
}
