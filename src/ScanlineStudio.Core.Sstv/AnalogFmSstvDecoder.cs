using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Generic decoder counterpart to <see cref="AnalogFmSstvEncoder"/>, dispatching the main-picture
/// FM demodulation to one of three ported classes based on <see cref="DemodType"/> (the
/// <c>demodType</c> constructor parameter, mirroring legacy's real <c>CSSTVDEM::m_Type</c>,
/// `sstv.cpp:2256-2269`) -- <see cref="HilbertFmDemodulator"/> (legacy's real compiled-in default,
/// `m_Type=2`, `sstv.cpp:1492`), <see cref="PllFmDemodulator"/>, or
/// <c>ZeroCrossingFrequencyCounter</c> (both internal to this assembly). All three are constructed
/// unconditionally regardless of the selected type, matching legacy's own always-constructed
/// <c>CSSTVDEM</c> member fields. <see cref="PllFmDemodulator"/> is also used a SECOND, entirely
/// separate way regardless of <c>demodType</c>: a dedicated instance always drives AVT training-lock
/// detection (see <see cref="AvtTrainingLockStateMachine"/>'s own doc comment for why legacy always
/// uses PLL there regardless of the picture demodulator's own `m_Type`) -- see the demod-type
/// runtime-dispatch subsystem's implementation plan for the explicit decision that these stay
/// separate instances rather than sharing state. Once VIS reveals the mode, per-line
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
public sealed partial class AnalogFmSstvDecoder : ISstvDecoder, IDisposable
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
    // ships out of the box, NOT the switch's `default:` branch (2400/1200/5000). Verified by reading
    // the constructor directly, not assumed from the switch's own default label. m_SLvl2 is always
    // m_SLvl*0.5 in every case (sstv.cpp:1798/1803/1808/1813) -- ported as a derived value, not a
    // second independent constant. Kept as named consts (not folded into SenseLevelPresets below)
    // because several existing tests (VisLockStateMachineTests, VisToneRaceHeaderTests,
    // SenseLevelCalibrationTests) reference them directly as "the default preset's known-good
    // values" for standalone construction outside a full AnalogFmSstvDecoder.
    internal const double SLvl = 3500.0;
    internal const double SLvl2 = SLvl * 0.5;
    internal const double SLvl3 = 5700.0;

    // The full 4-option "Sense Level" / squelch table (Option.dfm's RGSLvl radio group,
    // Options.Decode.SenseLevel in this port), exposed via the SstvDecoderSettings.SenseLevel /
    // Options > Decode wiring (restart-only -- see the ctor's senseLevel parameter doc below).
    // Index = legacy m_SenseLvl = Option.cpp:612's RGSLvl->ItemIndex directly, no offset. Row 1 is
    // derived from the named consts above rather than re-typed, and every SLvl2 is SLvl*0.5 per row
    // (matching SetSenseLvl's own m_SLvl2 = m_SLvl*0.5 in every branch) rather than a 4th literal --
    // both deliberately avoid a hand-duplicated-literal transcription-typo risk (an earlier draft of
    // this table had preset 2's SLvl2 (2400) accidentally colliding with preset 0's SLvl (2400)).
    internal static readonly (double SLvl, double SLvl2, double SLvl3)[] SenseLevelPresets =
    [
        (2400.0, 2400.0 * 0.5, 5000.0), // 0 -- "Very low" (switch default:, sstv.cpp:1812-1814)
        (SLvl, SLvl2, SLvl3),           // 1 -- "Low", the real shipped default
        (4800.0, 4800.0 * 0.5, 6800.0), // 2 -- "High"
        (6000.0, 6000.0 * 0.5, 8000.0), // 3 -- "Very high"
    ];

    // internal, not private -- matches SLvl/SLvl2/SLvl3/SenseLevelPresets' own accessibility above,
    // so a test can directly confirm the ctor's senseLevel argument actually reached these fields
    // (an amplitude-based behavioral test is unreliable here: LevelAgc is a true AGC that normalizes
    // toward a target level regardless of input amplitude once settled, so scaling a synthetic
    // tone's input amplitude down does not reliably produce a proportionally scaled steady-state
    // envelope to assert against).
    // NOT readonly (user-reported 2026-08-27, "Squelch level" live control): live-updated by
    // ApplyPendingSenseLevelRequest, drained from PushSamplesCore -- see _pendingSenseLevelRequest's
    // own doc comment for the full deferred-request mechanism, mirroring RequestNotch's own shape.
    internal double _slvl;
    internal double _slvl2;
    internal double _slvl3;

    private readonly int _sampleRate;

    /// <summary>See <see cref="ISstvDecoder.SampleRate"/>. Immutable for this decoder instance.</summary>
    public int SampleRate => _sampleRate;
    private readonly List<double> _demodulatedFrequencies = [];
    private int _demodulatedFrequenciesProcessedUpTo; // Band-1 item 4a: see DemodulatedFrequencyAt
    private readonly List<float> _rawSamples = [];
    private readonly DemodType _demodType;
    private readonly HilbertFmDemodulator _demodulator;
    // Demod-type subsystem Phase 2 -- main-picture-path alternatives to _demodulator above, live only
    // when _demodType selects them (constructed unconditionally regardless, matching legacy's own
    // always-constructed CSSTVDEM member fields -- cheap, and avoids null-conditional complexity in
    // the dispatch switch). _pllDemodulator is a THIRD PllFmDemodulator instance, separate from
    // _avtPllDemodulator below (see this subsystem's implementation plan for the explicit
    // instance-sharing decision and why). _afcZeroCrossingCounter is a SEPARATE instance from
    // _zeroCrossingDemodulator -- legacy's single m_fqc serves a dual role (main demod when
    // m_Type==1, AFC-only frequency source when m_Type==0) because it's fed from ONE real-time pass;
    // this port's AFC correction runs as a separate deferred bulk pass on its own cursor
    // (_afcProcessedUpTo, see ApplyAfcCorrections), so the two roles need independent instances even
    // though only one is ever actually fed samples for a given _demodType.
    private readonly PllFmDemodulator _pllDemodulator;
    private readonly ZeroCrossingFrequencyCounter _zeroCrossingDemodulator;
    private readonly ZeroCrossingFrequencyCounter _afcZeroCrossingCounter;
    private bool _mainPathIsNarrow; // narrow-mode edge tracker for the main-path PLL/ZeroCrossing retune -- Hilbert takes isNarrow per-call instead, needs no state here
    // RX BPF subsystem Phase 2: null represents RxBpfPreset.Off -- a TRUE bypass matching legacy's own
    // `if(m_bpf){...}` gate (sstv.cpp:1826), not a discard-output filter. Legacy's Off path never calls
    // m_BPF.Do at all, so the delay line itself is never advanced under Off; constructing a real filter
    // and throwing its output away would NOT be equivalent (it would silently prime the delay line), so
    // null is the only correct representation -- see BandpassFilteredSampleAt's own doc comment for how
    // the null-coalesce keeps the buffer-trim cursor (_bandpassFilteredProcessedUpTo) advancing under
    // Off exactly like every other preset.
    private readonly SearchBandpassFilter? _searchBandpassFilter;
    // Un-stub-RX-tab Piece A: null means off, matching _searchBandpassFilter's own null-for-bypass
    // shape -- but NOT readonly, since (unlike the BPF preset, fixed for the decoder's whole
    // lifetime) the notch is toggled at runtime via RequestNotch. Only ever mutated inside
    // PushSamplesCore's own single-caller-thread section (draining _pendingNotchRequest), never
    // from RequestNotch itself -- see that method's own doc comment.
    private NotchFilter? _notchFilter;
    // Un-stub-RX-tab Piece A (code-review finding): legacy's own CNotch::m_freq (fir.h:131) is a
    // persistent member that survives a disable -- a right-click toggle-on with no explicit
    // frequency (Main.cpp:14364-14371) resumes at whatever was last tuned, never a hardcoded
    // default. _notchFilter itself is destroyed on disable (see ApplyPendingNotchRequest), so the
    // last-tuned frequency has to live somewhere that outlives that -- this field is it.
    private double _notchFrequencyHz = 2400.0;
    // RX BPF subsystem Phase 3: stored separately from _searchBandpassFilter (which is null for Off,
    // so it can't itself answer "which preset was selected") -- exists solely for RxBpfPresetForTests,
    // mirroring _demodType/DemodTypeForTests' own shape below.
    private readonly RxBpfPreset _rxBpfPreset;
    // RX buffer subsystem Phase 2: threaded through the constructor (mirrors _demodType/_rxBpfPreset's
    // own shape). Phase 3 wired it into real decode-path gating -- TryAutoSync's branch 1/2 conditions
    // (Main.cpp:3907/:3945) and TryResolveSyncAnchorCorrection's averaging-depth selection
    // (Main.cpp:3760) -- see those methods' own doc comments for the citation trail. Phase 5/7 (below)
    // wires the staging buffer itself -- RAM for RxBufferMode.On, disk-backed for
    // RxBufferMode.Extended -- so `_rxLineStagingBuffer is not null` below IS exactly `_rxBufferMode
    // != Off` (unlike this class's OTHER `_rxBufferMode != Off` gates, e.g. TryAutoSync, which check
    // the mode directly since they need to fire the same way for both On and Extended regardless of
    // which storage backend is live).
    private readonly RxBufferMode _rxBufferMode;

    // RX buffer subsystem Phase 5/7: null for RxBufferMode.Off ONLY (NOT the same distinction as
    // TryAutoSync/TryResolveSyncAnchorCorrection's own `_rxBufferMode != Off` gating in Phase 3 --
    // that gating treats On and Extended identically for decode-path behavior; THIS field is about
    // which STORAGE BACKEND is live). Non-null for both On (RxLineStagingBuffer, RAM) and Extended
    // (RxDiskLineStagingBuffer, disk-backed, wired in this sub-piece of Phase 7) -- see the
    // constructor assignment below. Constructed once, fixed for this decoder's lifetime (mirrors
    // _searchBandpassFilter's own null-for-bypass shape). Captured samples feed PerformReplay
    // (Phase 6) -- giving Extended a non-null buffer here also turns replay ON for Extended, via the
    // existing `_rxLineStagingBuffer is not null` gates elsewhere in this class: legacy-correct
    // (`Main.cpp:5597`: `(dp->m_StgBuf != NULL) || WaveStg.IsOpen()`), a real and intended
    // consequence of this sub-piece, not an accident.
    private readonly IRxLineStagingBuffer? _rxLineStagingBuffer;

    // In-progress accumulator for the CURRENT (not-yet-complete) line, since ApplySlantTracking's own
    // per-sample loop can pause mid-line across multiple PushSamples calls (bounded by _consumedSamples,
    // which only advances one line at a time) -- these must be fields, not method-locals, to survive
    // across those calls. Always allocated (even when _rxLineStagingBuffer is null) to keep the capture
    // hook itself branch-free except for the one null-check that actually matters; cost is negligible
    // (empty lists) when capture is off.
    private readonly List<double> _rxBufferLineDemod = new();
    private readonly List<double> _rxBufferLineSync = new();
    // Buffered-replay fix (§15 item 2): PerformReplay no longer clears these at its own tail (the old
    // forward-jump design did, alongside the staging buffer it also used to truncate). Accepted,
    // documented tradeoff (round 2's own finding, robust-giggling-codd.md): these accumulators do NOT
    // "resume normally" across a backward snap -- if a line was PARTIALLY accumulated here when a pass's
    // backward snap runs, that partial content survives into whatever comes next once live decode
    // resumes, and the FIRST line boundary at/after the snap can end up mis-sized (its own real sample
    // count differs slightly from what the just-corrected stride implies) as a result. Bounded to at
    // most one mis-sized line boundary per pass, and its only measured consequence is a second-order
    // perturbation of ReplayOriginCalculator's own fold-window histogram on the NEXT pass -- not a pixel
    // decode-correctness issue. Left unfixed deliberately: correctly reconciling a partial accumulator
    // against a newly-corrected stride mid-line is real added complexity for a bounded, cosmetic-only
    // artifact.

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
    internal const int AnchorWarmupSamples = 2000;

    private int _consumedSamples;

    // Milestone-audit Phase 3 MUST 4 (spec/14-roadmap.md): the TRUE, unrounded line-start position,
    // advanced by the unrounded _effectiveSamplesPerLine every decoded line -- legacy (Main.cpp:4133-
    // 4148, DrawSSTVNormal) keeps ONE continuous integer sample counter for the whole transmission and
    // derives every line boundary via unrounded `double` division against it (`y =
    // int(double(n)/SSTVSET.m_TW)`), never by re-rounding a per-line step and accumulating the rounded
    // result. An earlier version of the per-line loop below did exactly that
    // (`_consumedSamples += (int)Math.Round(_effectiveSamplesPerLine)`), so line k started at
    // k*round(E) instead of legacy's exact k*E -- a drift that compounds across the whole image
    // (same trimmed-vs-full mismatch MUST fix 3 fixed within one scan segment, one level up: between
    // lines instead of within a line). "k*E" here assumes a constant E across the image, true only
    // without Auto Slant -- comprehensive-review note: legacy's own `y = int(n/m_TW)` naturally
    // RETROACTIVELY repositions every line boundary the instant `m_TW` changes mid-image (the same `n`
    // divided by a new `m_TW`), while this port's own per-line accumulator only applies a slant
    // correction to lines from that point FORWARD -- an already-documented, separate port gap (see
    // `SlantTracker`'s own doc comment, "retroactive re-decode not ported"), not something this fix
    // changes or claims to close. Code-level review note: the ROUNDED per-line cursor actually
    // used to decode (_consumedSamples, below) still isn't bit-identical to legacy's own `int(n/m_TW)`
    // (that's effectively a ceiling against m_TW, this port's is Math.Round's round-half-to-even) --
    // a small, uniform, non-compounding bias absorbed by SyncAnchorCorrector, unlike the compounding
    // drift this fix removes -- comprehensive-review correction: an earlier version of this comment
    // called it "~0.1px," which only holds at 44100Hz; at 11025Hz (Robot 36's own pixel pitch is ~3
    // samples there) the same sub-sample rounding bias is worth up to ~0.33px (mean ~0.16px). Still
    // uniform and non-compounding either way, just not a single rate-independent figure. Invariant:
    // kept exactly equal to _consumedSamples at every
    // OTHER site that assigns _consumedSamples (Commit -- including via TryDecodeNarrowModeHeader's
    // own assignment immediately followed by a Commit call, TryResolveSyncAnchorCorrection,
    // EndOfImage's dead-time skip) -- it only diverges from the rounded _consumedSamples during the
    // per-line loop's own fractional accumulation, never across an image boundary. DrainPendingSkip is
    // a further site that assigns _consumedSamples; it advances this field by exactly 1.0 per drained
    // sample specifically so the invariant holds at every step, not only at the drain's end.
    private double _idealLineStartSample;

    private SstvModeDefinition? _mode;
    private IScanlineDecoder? _lineDecoder;
    private Rgb24[]? _pixels;
    private int _nextLine;

    // RestartableSstvDecoder's swap-safety gate (ultracode audit finding #34) -- "no image currently
    // locked or confirmed-but-not-yet-committed" is the only safe moment to discard this instance's
    // whole object graph. `_mode is null` alone is NOT sufficient: `_avtTrainingPending` (AVT's own
    // training window, up to ~7.1s) is a CONFIRMED detection that hasn't reached Commit() yet, unlike
    // every other pre-lock flag in this class (e.g. `_syncBypass1PrimaryHeld`,
    // `_syncBypassNarrowPhaseActive`), which are speculative scan state only -- every OTHER confirmed
    // match commits synchronously within the same PushSamples call that found it, so `_mode is null`
    // alone already excludes them. Verified via a full field sweep (round-2 plan review) that this is
    // the only other such flag. `_pendingAnchorCorrectionMode` does NOT need its own term here --
    // functional-audit correction (D6, round 1): the previous justification ("Commit() sets `_mode`
    // before that flag") was backwards -- Commit()'s own AVT branch sets `_mode` WITHOUT ever touching
    // this flag at all (see FinalizeAnchorAndStartDecoding's call site in Commit()). The real reason
    // this invariant holds is REACHABILITY, not construction: TryProcessBuffer's own per-line loop
    // resolves any pending anchor correction (or returns early if it can't yet) strictly BEFORE it
    // will ever try a fresh header search that could reach Commit() again, so a second Commit() call
    // is provably unreachable while this flag is already set -- except via PerformForceMode, which is
    // explicitly aware of this and defensively clears the flag itself before proceeding (see that
    // method's own doc comment for the one case where the invariant would otherwise break). Also now
    // defensively cleared in AbandonInProgressImage (see its own doc comment) as belt-and-suspenders
    // for the AVT branch specifically, matching PerformForceMode's own defensive posture rather than
    // relying on reachability alone.
    //
    // Final code-level review (post-implementation) found one accepted, bounded gap: this is also
    // true during EndOfImage's own 0.5s dead-time skip, so a swap landing in that exact window hands
    // the just-finished image's tail audio to the fresh instance as header-search input -- precisely
    // what the skip exists to suppress. Bounded to once per restart cycle (~12h+), worst case one
    // spurious false-start detection; consistent with this whole mechanism's "fresh instance == app
    // restart" framing, not a correctness regression worth gating on.
    internal bool IsIdle => _mode is null && !_avtTrainingPending;

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

    // Sync-pulse SNR measurement (MeasureLineSyncSnr). Tracker/enabled/expected-anchor are decode
    // thread only. The two published figures are double bits written with Interlocked.Exchange on the
    // decode thread and read with Interlocked.Read from any thread: the producer never waits, a slow
    // reader simply sees the latest value (older values are dropped). _snrMeasurementRequested is the
    // any-thread toggle latch drained at the top of PushSamples.
    private readonly SyncSnrTracker _syncSnrTracker = new();
    private bool _snrMeasurementEnabled;
    private int _snrMeasurementRequested;
    private int _snrExpectedAnchor = int.MinValue;
    private long _liveSnrBits = BitConverter.DoubleToInt64Bits(double.NaN);
    private long _receptionSnrBits = BitConverter.DoubleToInt64Bits(double.NaN);
    private int _afcProcessedUpTo;
    private int _afcBoundSample; // see Commit -- never correct past this image's own generous nominal extent
    private double? _lastAppliedAfcRetuneHz; // last offset applied to _syncEnvelopeDetector; null = never retuned this lock cycle

    private SyncEnvelopeDetector? _syncEnvelopeDetector;
    private SlantTracker? _slantTracker;
    private int? _lastReplayOriginForTests; // RX buffer subsystem Phase 6c -- set inside PerformReplay, test-only
    private int _replayPassCountForTests; // §15 item 2 measurement -- cumulative count of genuinely completed PerformReplay passes across this decoder's whole lifetime, test-only. Not reset per image (matches _lastReplayOriginForTests' own convention) -- a caller wanting a per-image count reads this at ModeDetected/DecodeRestarted boundaries and takes the delta.
    private int _rawFloorClampForwardCountForTests; // §15 item 2, code-review addition -- cumulative count of PerformReplay passes that hit the raw-floor clamp-forward fallback branch (backward snap would violate _bufferBase), test-only. Same whole-lifetime, not-reset-per-image convention as _replayPassCountForTests.
    // RX buffer subsystem Phase 6c round-2 code-review fix. PRIMARY DEFINITION (functional-audit
    // fix, D3+D8+D9 coupled round 2 correction -- round 1's own fix, described further down, got
    // this backwards): a FIXED COORDINATE MAP, `d(raw) = origin + (raw - anchor)`, mapping a raw
    // sample index to its position in the staging buffer's own local/destination coordinate space.
    // "Fixed" means unchanged between re-anchor events -- it is NOT a running tally of how much has
    // been staged. Only ONE thing ever legitimately RE-ANCHORS it (resets the identity outright):
    // InitializeSlant sets it to _consumedSamples's own value at the moment the buffer is Clear()ed (a
    // fresh lock -- "local index 0 IS this raw sample" at that instant). Buffered-replay fix (§15 item
    // 2): PerformReplay used to ALSO re-anchor it this same way, after truncating the staging buffer at
    // the tail of every replay pass -- that truncation, and this field's own re-anchor alongside it, are
    // BOTH GONE now (PerformReplay's own doc comment has the full replacement design; this field is left
    // completely untouched by PerformReplay in both its backward-snap and raw-floor-clamp-forward
    // branches). Separately from re-anchoring, two mutators ADVANCE this field without resetting its
    // identity: DrainPendingSkip's per-sample skip loop moves it by 1 per skipped sample outside the
    // post-snap window ITS OWN 3-way guard defines (see that method's own doc comment) -- correct,
    // because a manual-ReSync-driven skip genuinely DELETES that raw sample from the image-time
    // timeline the map describes, so the map's origin must shift to compensate; inside the post-snap
    // window the guard suppresses this advance instead, since those samples are already accounted for
    // by an earlier pass, not newly deleted. ApplyNotchDisableShift's own `-= n` is the instantaneous,
    // opposite-direction sibling of DrainPendingSkip's `++` (see that method's own doc comment) -- same
    // reasoning, mirrored: the notch-disable direction genuinely un-consumes `n` raw samples, so the
    // map's origin must shift back to compensate, in one shot rather than a per-sample loop. A REJECTED
    // (buffer-full)
    // TryAppendLine is different in kind, not degree:
    // the line was still decoded and stamped into the image at its normal position -- only STAGING
    // (for a potential future replay) failed, not decoding -- so the raw-to-destination map is
    // UNCHANGED and this field must NOT move for it. PerformReplay's own resumeDest computation
    // (see that method's doc comment at its own call site) depends on exactly this: it needs the
    // LIVE cursor's true destination-coordinate position, including however far it has run AHEAD of
    // what's actually staged once rejections start -- not "wherever staging happens to have
    // gotten to," which is a different, smaller number once anything has been rejected.
    //
    // Round-1 functional-audit history (D3+D8+D9 coupled track, corrected in round 2): round 1
    // treated an earlier draft of this comment's "_consumedSamples - anchor == staged + in-flight"
    // phrasing as a hard invariant, and "fixed" a perceived violation by advancing this field on
    // every rejection (matching DrainPendingSkip's own `++` shape). That phrasing was itself the
    // bug -- it only happens to hold in the common case where every consumed sample is eventually
    // staged, and is not what the map is actually FOR. The round-1 fix pinned resumeDest to the
    // staged extent instead of the live cursor's true position, confirmed via a worked numeric trace
    // during round 2 review to silently re-stamp already-decoded rows with stale audio on the next
    // replay pass -- reverted; see ApplySlantTracking's own capture-flush hook for where that fix
    // used to live and why it doesn't belong there.
    //
    // Known, deferred gap (unchanged by either round): a rejected line followed by a LATER accepted
    // shorter line (line lengths vary +/-1 sample, and can drop on an Auto-Slant commit) would leave
    // the staging buffer with a real physical discontinuity that nothing currently detects -- same
    // defect class as the already-documented, already-deferred DrainPendingSkip mid-buffer-hole gap.
    // Not fixed here; flagged so it isn't mistaken for closed.
    private int _rxBufferAnchorSample;
    // RX buffer subsystem Phase 6c round-4 fix, companion to _rxBufferAnchorSample: the TRANSMISSION-LINE
    // index (not bitmap row -- multiply by RowsPerTransmissionLine at use, matching _nextLine's own
    // reconciliation convention) of the image row that staged index 0 currently corresponds to. Set
    // from a fresh lock (InitializeSlant, which Clear()s the buffer at the image's own first line) to 0.
    // Buffered-replay fix (§15 item 2): PerformReplay no longer re-bases this at its own tail -- the
    // staging buffer is never truncated anymore, so staged index 0 stays image row 0 for a whole
    // reception, matching legacy's own never-truncated m_StgBuf. This field therefore now stays 0 for
    // an entire reception once set at lock; every read site that adds it (PerformReplay's own origin
    // calculation and bitmapRow conversion, TryCorrectSlant's own entry-gate cumulative count) keeps
    // working unchanged with the addition simply becoming a no-op, kept rather than removed so those
    // expressions stay correct if a future change ever re-introduces a re-base for a different reason.
    private int _rxBufferBaseTransmissionLine;
    private int _slantProcessedUpTo;
    private double _effectiveSamplesPerLine;
    private double _syncSegmentOffsetSamples;
    private double _slantIdealSamplesSoFarInLine;
    private double _slantLineMaxEnvelope;
    private double _slantLineMinEnvelope; // port of legacy's m_SyncMin (Main.cpp:4193/4200-4201) -- Auto Sync's own signal-strength gate, (max-min)>5000
    private bool _slantLineEnvelopeSeeded; // has the current line's first sample seeded both max/min yet (see ApplySlantTracking's own doc comment on the seed-both-then-else-if convention)
    private double _slantLinePeakPosition;

    // Manual ReSync (legacy's KRFSClick/m_Skip, Main.cpp:14004-14020 -- NOT ReSyncSSTV) state. See
    // RequestReSync/PerformReSync/DrainPendingSkip and ApplySlantTracking's own capture point.
    private volatile bool _reSyncRequested;

    // RX buffer subsystem Phase 8c: RequestCorrectSlant's own deferred-request field. Volatile, like
    // _reSyncRequested above (not plain, like _pendingReplayRequested below) -- RequestCorrectSlant is
    // an EXTERNAL caller's request (mirrors RequestReSync's own any-thread contract), not a decode-
    // internal signal the way _pendingReplayRequested is. Drained inside TryProcessBuffer's per-line
    // loop, at the SAME statement position _pendingReplayRequested already drains at (immediately
    // after ApplySlantTracking()) -- NOT at the top of PushSamples, for the identical reason
    // _pendingReplayRequested's own doc comment already gives for its own drain point: TryCorrectSlant
    // reads the staging buffer, and PerformReplay's own backward-snap target depends on the LIVE
    // cursor's own position at the moment it runs (see _pendingReplayRequested's own doc comment for
    // the full, current reasoning -- no longer "destructive" since the buffered-replay fix, §15 item 2,
    // removed the discard/truncate behavior that word used to describe), so draining at a caller-chunk
    // boundary would make the decoded image a function of how the caller sliced its PushSamples calls.
    private volatile bool _correctSlantRequested;
    private int _manualSlantEntrySafetyCheckCountForTests;
    private int _manualSlantCandidateSafetyCheckCountForTests;
    private int _pendingSkipSamples; // port-equivalent of legacy's own m_Skip field
    private double? _lastLineSyncPeakPosition; // port-equivalent of m_SyncRPos (see ApplySlantTracking's capture point for why one field also stands in for m_SyncPos at this port's granularity)
    private bool _suppressNextSlantProcessLine;
    private bool _slantCorrectionsDisabledForRestOfImage; // port of m_AutoSyncCount's gate on AutoStopJob's correction branch

    // Un-stub-RX-tab Piece A: RequestNotch's own deferred-request payload -- a reference type (not a
    // plain bool/nullable-struct pair) specifically so Interlocked.Exchange can atomically hand off
    // BOTH the enabled flag and the frequency together, matching _forcedMode's own established
    // shape/reasoning immediately above (a bare bool + separate double field would let a caller's
    // two writes race and get split across two different PushSamplesCore drains).
    private sealed record NotchRequest(bool Enabled, double? FrequencyHz);
    private NotchRequest? _pendingNotchRequest;

    // Options stub backlog item 1 (docs/plans/options-stub-item1-pll-tuning-plan.md): PLL
    // demodulator tuning, exact same deferred-request shape as _pendingNotchRequest immediately
    // above -- all 5 fields are always set together as one atomic group (never independently, unlike
    // _desiredFlags' own 4-property CAS-merge shape), so a plain Interlocked.Exchange is sufficient,
    // no merge logic needed.
    private sealed record PllTuningRequest(double VcoGain, int LoopOrder, double LoopCutoffHz, int OutputOrder, double OutputCutoffHz);
    private PllTuningRequest? _pendingPllTuningRequest;

    // Holds the CURRENTLY APPLIED tuning (updated only by ApplyPendingPllTuningRequest, the drain --
    // never written directly by RequestPllTuning) -- needed because _avtPllDemodulator is
    // constructed FRESH per AVT-training attempt (see its own field doc comment), not once per
    // decoder lifetime like _pllDemodulator. Without these, a freshly-(re)constructed AVT instance
    // between two drains would silently start at the legacy defaults instead of whatever tuning is
    // actually currently in effect.
    private double _pllVcoGain;
    private int _pllLoopOrder;
    private double _pllLoopCutoffHz;
    private int _pllOutputOrder;
    private double _pllOutputCutoffHz;

    // Options stub backlog item 2 (docs/plans/options-stub-item2-zerocrossing-tuning-plan.md): same
    // deferred-request shape as PllTuningRequest above. Unlike PLL's _avtPllDemodulator, BOTH
    // ZeroCrossingFrequencyCounter instances here are constructed once, in the constructor, never
    // freshly reconstructed mid-session -- so these tracking fields aren't load-bearing for a future
    // construction site, only for InnerZeroCrossingTuningForTests-style test visibility, kept for
    // symmetry with the PLL pattern.
    private sealed record ZeroCrossingTuningRequest(ZeroCrossingSmoothingMode SmoothingMode, int OutputOrder, double OutputCutoffHz, double SmoothingFrequencyHz);
    private ZeroCrossingTuningRequest? _pendingZeroCrossingTuningRequest;
    private ZeroCrossingSmoothingMode _zeroCrossingSmoothingMode;
    private int _zeroCrossingOutputOrder;
    private double _zeroCrossingOutputCutoffHz;
    private double _zeroCrossingSmoothingFrequencyHz;

    // Un-stub-RX-tab Piece B: RX Decoder Trace capture buffers -- see the constructor's own doc
    // comment for why these are externally-owned (unlike _notchFilter), and D12At/D19At's own doc
    // comments for the channel-0 write hook, ApplySlantTracking's for channel-1's. The d12-vs-d19
    // narrow/wide latch for channel 0 lives on _scopeCaptureChannel0.UsesD19 itself, not as a field
    // here -- see that property's own doc comment for why (must survive a RestartableSstvDecoder
    // rebuild, which a field on THIS class would not).
    private readonly ScopeCaptureBuffer _scopeCaptureChannel0;
    private readonly ScopeCaptureBuffer _scopeCaptureChannel1;

    private sealed record ScopeCaptureArmRequest(int Size);
    private ScopeCaptureArmRequest? _pendingScopeCaptureArm;

    // RX buffer subsystem Phase 6d: deferred replay trigger. Plain (not volatile/Interlocked, unlike
    // _reSyncRequested) -- both set sites and the drain site all run on the same thread, inside the
    // same PushSamples call stack; there is no cross-thread producer here the way RequestReSync's own
    // external caller is. Set (never acted on synchronously -- see PerformReplay's own reentrancy
    // doc note) inside ProcessSlantTrackingSample's own commit branch and at the once-per-image
    // _replayOnceLatchFired trigger, both inside TryProcessBuffer's own per-line loop.
    //
    // Round-1 code-review correction (real bug, not a nit): an earlier version of this field drained at
    // the top of PushSamples, mirroring _reSyncRequested's own point -- WRONG for this specific flag.
    // That point is a CALLER-CHUNK boundary, not a decode position; _reSyncRequested is set by an
    // external UI thread, so chunk-dependent timing is inherent and harmless there. This flag is set by
    // decode itself, and PerformReplay's own cursor re-anchor is a function of exactly where LIVE decode
    // currently sits (buffered-replay fix, §15 item 2: no longer by discarding samples/sacrificing a row/
    // truncating the buffer -- those are gone -- but the backward-snap target itself still depends on
    // _consumedSamples' own live position at the moment PerformReplay runs) -- draining it at a chunk
    // boundary made the DECODED IMAGE a function of how the caller sliced its PushSamples calls (caught by
    // SstvRoundTripTests.DecodedImage_IsPixelIdentical_WhetherSamplesArriveInOneChunkOrMany: a caller
    // pushing a whole transmission in one call never drained it at all, since no second PushSamples
    // call ever arrived). Now drained inside TryProcessBuffer's own per-line loop, immediately after
    // ApplySlantTracking() -- see that call site's own doc comment for the full reasoning and the
    // reentrancy argument (still valid: ApplySlantTracking's own per-sample call stack has fully
    // unwound by that point in the same iteration that raised the request).
    private bool _pendingReplayRequested;

    // RX buffer subsystem Phase 6d: port of legacy's m_SyncAccuracyN one-shot-per-image bitmask
    // (Main.cpp:3530-3562) -- this port only ever implements the FIRST of its two bits (the
    // m_SyncAccuracy==2-gated second trigger is not ported, see the RX buffer plan's own note: that
    // toggle itself remains unported, Phase 3's own already-established gap), so a single bool suffices
    // where legacy needs two. Reset at every fresh lock (InitializeSlant) -- fires at most once per
    // image, matching legacy's own `!(m_SyncAccuracyN & 1)` guard.
    private bool _replayOnceLatchFired;

    // RX buffer subsystem Phase 6d round-2: explicit user decision (2026-08-13) narrowing the
    // once-per-image latch to require this -- see that trigger's own doc comment. Legacy's own
    // `ReSyncSSTV` re-derives the horizontal origin unconditionally, with NO visible cost either way
    // (its replay never loses a row -- Main.cpp:5602-5612 re-decodes the WHOLE buffer, nothing is ever
    // truncated). Round-1 code review found this port's own PerformReplay -- which DID sacrifice one
    // row per pass, by design, under the old forward-jump design -- made the SAME unconditional trigger
    // a guaranteed visible defect (a small black stripe) in EVERY default decode once wired to fire
    // automatically, even when no correction was ever needed. This flag gated the latch so that cost was
    // only paid when a correction had actually committed -- i.e. when replay was actually fixing
    // something. Reset at every fresh lock (InitializeSlant).
    //
    // Buffered-replay fix (§15 item 2), code-review finding: the row-sacrifice this gate exists to avoid
    // no longer happens -- PerformReplay's own backward snap never loses a row (see that method's own
    // doc comment) -- so this gate is now a real, DOCUMENTED-HERE-BUT-NOT-YET-DECIDED divergence from
    // legacy's own unconditional trigger, with no remaining cost-avoidance rationale on paper. Removing
    // it (matching legacy exactly) is explicitly OUT OF SCOPE for this piece -- a separate, deliberate
    // decision for whoever picks this up next, not a bug this piece should silently fix or silently
    // leave unexplained.
    private bool _anyCorrectionCommittedThisImage;

    // Auto Sync (legacy's sys.m_AutoSync, an automatic trigger for the exact same skip-and-suppress
    // action manual ReSync applies -- see TryAutoSync/ApplySyncCorrection's own doc comments for the
    // full design and the two rounds of plan-readiness review this went through) detection state, port
    // of legacy's InitAutoStop (Main.cpp:3801-3863), Auto-Sync-relevant fields only. Deliberately a
    // SEPARATE 16-entry ring buffer from SlantTracker's own _history -- that one is fed the
    // segment-offset-relative `relative` ApplySlantTracking computes for ITS OWN purposes; this one
    // needs the OFP-relative ComputeAutoSyncPosition value instead (see that method's own doc comment
    // for why conflating the two would be a real, silent bug, not just a style choice).
    private readonly int[] _autoSyncPositionHistory = new int[16]; // port of m_AutoStopAPos[16]
    private int _autoSyncObservationCount; // port of m_AutoStopACnt
    private int? _autoSyncReferencePosition; // port of m_AutoSyncPos (sentinel 0x7fffffff -> null)
    private int _autoSyncCooldown; // port of m_AutoSyncDis
    private int _autoSyncBaseMult; // port of m_Mult
    private int _autoSyncDiff; // port of m_AutoSyncDiff
    private int _autoSyncTriggerCountForTests; // test-only: how many times TryAutoSync itself (not manual ReSync) has applied a correction
    private int? _lastBranch1ThresholdForTests; // test-only: the (_autoSlantEnabled ? 5 : 2) * _autoSyncBaseMult value branch 1 last actually used

    // Auto Stop (sys.m_AutoStop, Main.cpp:3930-3937/:3942-3943/:3957) -- the erratic/weak-signal
    // detector sharing AutoStopJob with Auto Sync above. m_AutoStopCnt is genuinely shared: Auto
    // Sync's own two triggers (and the n>=4 stable-cluster branch) decrement it, only Auto Stop's
    // own branch increments and reads it.
    private int _autoStopCnt; // port of m_AutoStopCnt

    // Set by TryAutoSync when Auto Stop's own trigger condition fires; consumed by the per-line loop
    // in TryProcessBuffer (NOT applied synchronously inside TryAutoSync itself -- see that method's
    // own doc comment for why: TryAutoSync runs mid-ApplySlantTracking, and the statements
    // immediately after its own call site dereference _slantTracker/_syncEnvelopeDetector with no
    // null-check, so calling EndOfImage() -- which nulls both -- synchronously from inside TryAutoSync
    // would be a guaranteed NRE. Auditor plan-review finding, round 1.
    private bool _autoStopTriggered;
    private int _autoStopTriggerCountForTests; // test-only
    private double? _lastAutoStopEnvelopeSpreadForTests; // test-only -- see AutoStopTests.cs's own envelope-scale check

    // Test-only: ComputeAutoSyncPosition's own real return value, captured INSIDE TryAutoSync at the
    // only moment it's valid to read (_slantLinePeakPosition is a live, within-line accumulator reset
    // at the end of every completed line -- reading the production method's result from OUTSIDE the
    // decode loop, asynchronously after PushSamples returns, does not reliably observe the value for
    // whichever line was current at capture time). A round-trip revert-fix-confirm-fail check found
    // that neither an externally-recomputed independent value NOR the end-to-end trigger count alone
    // reliably catches a wrong anchor base: a CONSTANT wrong bias reads as a stable, self-consistent
    // cluster to TryAutoSync's own clustering check (nothing ever looks like a "jump" if every
    // reading is uniformly offset the same way), so AutoSyncTriggerCountForTests alone stayed 0
    // either way. This field captures the actual production value directly, so a test can compare it
    // against an independently-derived expectation without needing a live re-read's timing hazard OR
    // relying on trigger behavior that's provably insensitive to this specific class of bug.
    private int? _lastComputedAutoSyncPositionForTests;

    // Force-mode (legacy's RX quick-mode-button click, Main.cpp:6096-6122 -> CSSTVDEM::Start(mode,
    // TRUE), sstv.cpp:1749-1767/1717-1747) request state. Reference-type field, not a plain volatile
    // bool like _reSyncRequested above -- the payload (which mode) must survive to consumption
    // without a lost-update race, so consumption uses Interlocked.Exchange for an atomic
    // read-and-clear (last-request-wins) rather than a separate test-then-clear pair. See
    // ForceMode/PushSamples's own consumption point for why this is checked BEFORE _reSyncRequested.
    private SstvModeDefinition? _forcedMode;

    // ui_transition_plan.md step 10 (T2-5): persistent RX mode lock -- genuinely NEW UI/workflow
    // (CLAUDE.md ss2), no legacy precedent (exhaustively grepped yoniq-old/YONIQ-main for
    // AutoDetect/FixMode/ManualMode/LockMode/VIS-disable-shaped concepts -- legacy's own related
    // state, CSSTVDEM::m_SyncMode/RxAutoPush/SBMClick, maps to features ALREADY ported here
    // (SetAutoDetectPaused, and this class' own one-shot ForceMode above), not to "stays locked
    // across many receptions"). Deliberately a SEPARATE field from _forcedMode, not a reuse of it --
    // _forcedMode self-clears (one-shot, Interlocked.Exchange to null on every drain,
    // ApplyPendingModeLockRequest below); this one persists across receptions until explicitly
    // unlocked, and Commit() applies it via substitution (see that method's own doc comment) rather
    // than by pre-empting VIS auto-detection -- detection keeps running exactly as today, which is
    // what keeps IsIdle/RestartableSstvDecoder's idle-gated swap machinery/history-recording all
    // correct during silence (2 rounds of plan-review: round 1's "skip auto-detect at the idle
    // boundary" strawman made the decoder never idle, an infinite noise-frame decode+disk-write loop
    // during silence -- round 2's corrected design, implemented here, keeps detection unchanged and
    // substitutes only the mode actually committed).
    private SstvModeDefinition? _lockedMode;

    private sealed record ModeLockRequest(SstvModeDefinition? Mode);

    // Deferred-request latch, exact same shape/reasoning as _pendingSenseLevelRequest above (a
    // record, not SstvModeDefinition? directly, because Interlocked.Exchange<T> requires a reference
    // type and there is no Nullable<T> overload -- the record's own null Mode is a real "unlock"
    // request, distinct from this OUTER field being null, "nothing pending").
    private ModeLockRequest? _pendingModeLockRequest;

    // Pause/abandon (legacy's RxAutoPush -> pDem->Stop(), Main.cpp:6042-6060) request state. Plain
    // int (0/1), not a reference-type payload like _forcedMode above -- there's no payload to carry,
    // just a one-shot "clean up whatever's in progress" signal, so Interlocked.Exchange's atomic
    // read-and-clear (0 = no request pending) is sufficient. Deliberately NOT persistent "paused"
    // state (SstvSessionService owns that, at the session layer, by simply not forwarding audio) --
    // see RequestAbandonReception's own doc comment for why this stays a one-shot command.
    private int _abandonRequested;

    // m_sint1 (sstv.cpp:1899-1904/1946-1972, sstv.h:700, isNarrow:false -- SyncCheckSub's own
    // m_fNarrow gating restricts it to non-narrow candidates, same as m_sint2) -- piece 7d, the last
    // of the 7-piece VIS/preamble-lock breakdown. Same CSYNCINT class as m_sint2/m_sint3, but wired
    // completely differently: checked FIRST every sample (top priority, no SyncBypassTrustedModes-
    // style allowlist -- legacy acts on *any* mode SyncStart returns, sstv.cpp:1900-1904), and never
    // independently scans -- it only ever gets new peak data from the SAME primary threshold
    // (d12>d19 && d12>SLvl && (d12-d19)>=SLvl) that also drives VisLockStateMachine's own Search/
    // ConfirmLock (sstv.cpp:1946-1950/1958-1972), via SyncTrig on that threshold's rising edge and
    // SyncMax while it holds -- not a lower, always-checked threshold like m_sint2/m_sint3's own.
    // ASTRA-030 uses the actual VIS state for all three trackers: Search consumes/triggers
    // peaks, ConfirmLock updates maxima, and VIS-bit states freeze peak work. Counters advance
    // in every state. Separate envelope filter histories remain unchanged.
    private readonly SyncIntervalTracker _syncBypass1Tracker;

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

    // S8 fix (spec/14-roadmap.md): mid-image/noise-tolerant narrow-mode (MN/MC) FSK-announce re-lock.
    // sstv.cpp:1858's real DecodeFSK(int(d19), int(dsp)) call is UNCONDITIONAL every sample -- outside
    // and before the `if(!m_Sync||m_SyncRestart||m_SyncAVT)` gate (sstv.cpp:1889) that DOES restrict
    // m_sint1/m_sint2/m_sint3 and the VIS-decode switch. Confirmed directly against source (Stop(),
    // sstv.cpp:1769-1791, and Start()/Start(int,int)) that legacy's own m_fsk* state is NEVER
    // externally reset either -- only NarrowFskHeaderDecoder's own internal failure/success paths
    // reset it, exactly matching this class's already-existing design. This makes
    // _narrowFskDecoder/_narrowFskProcessedUpTo genuinely different from _visLockStateMachine/
    // _visLockProcessedUpTo above: this pair is NEVER Reset() and NEVER jumped by EndOfImage
    // specifically (auditor plan-review finding: sharing the _syncBypassProcessedUpTo/
    // _visLockProcessedUpTo lockstep loop, which DOES jump 500ms forward at every EndOfImage, would
    // starve this decoder of exactly the post-image window a mode-change announce is most likely to
    // arrive in -- see TryNarrowFskScan's own doc comment for the independent-cursor design this
    // led to). Milestone-audit stale-comment fix: an earlier version of this comment also claimed
    // "never jumped by ... Commit," which is wrong and contradicted by Commit()'s own
    // `_narrowFskProcessedUpTo = Math.Max(_narrowFskProcessedUpTo, _consumedSamples)` fast-forward
    // (its own doc comment, "needs the exact same fast-forward, for the exact same reason" as every
    // other detector's cursor) -- this cursor DOES get fast-forwarded whenever ANY other detector's
    // match commits, it just isn't RESET/re-anchored to a fresh origin the way
    // _syncBypassProcessedUpTo/_visLockProcessedUpTo are.
    private readonly NarrowFskHeaderDecoder _narrowFskDecoder;
    private int _narrowFskProcessedUpTo;

    /// <summary>Delivery channel for a decoded FSK station-ID callsign/NR-RST (legacy STX 0x2a,
    /// `sstv.cpp:2465-2551`) -- fired from <see cref="TryNarrowFskScan"/>, which runs on whatever
    /// thread feeds this decoder samples (the DSP/decode pipeline thread, not the UI thread).
    /// Concurrency contract (CLAUDE.md §4): subscribers are invoked SYNCHRONOUSLY on that thread, with
    /// no buffering and no marshaling -- a slow or blocking subscriber blocks decode. Any UI-facing
    /// consumer (Phase 5, not built yet) must dispatch to its own thread itself, immediately, rather
    /// than doing real work inline here.</summary>
    public event Action<FskStationIdDecodedInfo>? StationIdDecoded;

    /// <summary>Legacy <c>m_fskdecode</c> equivalent (see <see cref="NarrowFskHeaderDecoder.StationIdDecodeEnabled"/>'s
    /// own doc comment) -- defaults false, matching legacy's real default. Wired to the live user
    /// setting (<c>StationIdSettings.FskIdRxEnabled</c>) by <c>SstvSessionService.StartReceivingAsync</c>,
    /// re-applied on every RX start; <see cref="ScanlineStudio.Core.Sstv.RestartableSstvDecoder"/>
    /// (the type actually registered for DI, not this class directly) additionally preserves a set
    /// value across its own periodic inner-decoder reconstruction -- see that class' own
    /// <c>StationIdDecodeEnabled</c> doc comment. Exposed as a plain property (not deferred until
    /// that wiring existed) so both places this port constructs a <see cref="NarrowFskHeaderDecoder"/>
    /// -- <see cref="_narrowFskDecoder"/> (the persistent scan) and <see cref="TryDecodeNarrowModeHeader"/>'s
    /// own fresh per-call instance -- stay consistent by construction rather than needing the settings
    /// wiring to remember both separately.</summary>
    public bool StationIdDecodeEnabled
    {
        get => _narrowFskDecoder.StationIdDecodeEnabled;
        set => _narrowFskDecoder.StationIdDecodeEnabled = value;
    }

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

    // Port of legacy's user-toggleable m_afc (default 1, sstv.cpp:1471), driven by the "AFC" speed
    // button in the main window's "DSP" group box (Main.dfm's GB1/SBAFC, Main.cpp:1917/6011-6013).
    // Separate from AVT mode's own AFC exclusion (see InitializeAfc below), which this field does
    // NOT replace -- AVT stays excluded regardless of this flag's value. Live-settable via the
    // AfcEnabled property, drained by ApplyPendingDecoderFlagsRequest, which also runs legacy's own
    // InitAFC()-equivalent reset on the off edge.
    private bool _afcEnabled;

    // Port of legacy's real m_SyncRestart (default 1, sstv.cpp:1486, toggled via the "Lock" toolbar
    // button, Main.cpp:10907/11887's KRARClick) -- see the mid-reception restart call site in
    // TryProcessBuffer for the full citation trail on why this port previously hard-wired it on.
    // NOT readonly (2026-08-27, restart-required-settings backlog item 1) -- genuinely live now, see
    // the SyncRestartEnabled property below and _desiredFlags/_appliedFlags's own doc comment for the
    // deferred-latch mechanism (a plain write is NOT safe here, unlike StationIdDecodeEnabled -- see
    // that property's own doc comment for why this field group specifically needs the latch).
    private bool _syncRestartEnabled;

    // Port of legacy's real sys.m_AutoSync (default 1, Main.cpp:901) -- gates only the two Auto Sync
    // trigger branches (TryAutoSync's own doc comment), not the drift-detection bookkeeping that
    // Main.cpp:3886's own outer gate (constant-true in this port, see TryAutoSync) keeps running
    // unconditionally either way. NOT readonly (2026-08-27) -- see _syncRestartEnabled's own comment
    // immediately above for why.
    private bool _autoSyncEnabled;

    // Port of legacy's real sys.m_AutoStop (Main.cpp:900) -- unlike _afcEnabled above, legacy's own
    // FRESH default is OFF, not on. Caveat: SBLKClick (Main.cpp:10898-10907) ties sys.m_AutoStop to
    // the same Lock toolbar button as the other two (`sys.m_AutoStop = !SBLK->Down`) -- Lock engaged
    // means all three are off, Lock DISENGAGED (unlocked) means all three are on. Legacy's Lock
    // button can never produce this port's own default combination (SyncRestart=true, AutoSync=true,
    // AutoStop=false); default-false here is a deliberate, defensible startup default matching sys's
    // own unmodified .ini default, not a claim that it matches any single real legacy Lock state.
    //
    // Divergence, now independently live-toggleable (2026-08-27, plan-review round 2/3 finding): the
    // counter this flag gates (see AutoStopEnabled property below) accumulates unconditionally in
    // this port (justified at TryAutoSync's own call site as matching every reachable state WHILE
    // these three flags were construction-time-only) -- legacy only accumulates it at all when
    // `sys.m_AutoStop || sys.m_AutoSync || KRSA->Checked` (Main.cpp:3886). Now that the three are
    // independently live, "all three off" is newly reachable, where legacy would not accumulate and
    // this port does -- a real, tested divergence (see the AutoStop-divergence test), not ported here
    // (the ripple into TryAutoSync's own history-ring/branch-1 bookkeeping was not measured as safe).
    private bool _autoStopEnabled;

    // Port of legacy's real KRSA->Checked (Main.cpp:1863's Define/AutoSlant .ini key) -- gates only
    // SlantTracker's own correction-commit branch in ApplySlantTracking (KRSA->Checked's exact scope
    // per SlantTracker.cs's own doc comment, Main.cpp:3968-4018), not the drift-detection bookkeeping,
    // which runs unconditionally either way, same reasoning as _autoSyncEnabled/_autoStopEnabled
    // above. ALSO gates TryAutoSync's own branch-1 threshold (Main.cpp:3910/:3917's
    // `(KRSA->Checked ? 5 : 2)*m_Mult`) -- auditor plan-review finding: KRSA->Checked is read at that
    // SEPARATE call site too, not just the slant-commit block; this port previously hardcoded the `5`
    // side only because this flag didn't exist yet. NOT readonly (2026-08-27) -- see
    // _syncRestartEnabled's own comment above for why.
    private bool _autoSlantEnabled;

    // Clamped index actually used to select SenseLevelPresets below (senseLevel is >= 0 and <= 3 ?
    // senseLevel : 0) -- stored only for SenseLevelForTests; production code reads the derived
    // _slvl/_slvl2/_slvl3 thresholds, never this raw index. NOT readonly (user-reported 2026-08-27,
    // "Squelch level" live control) -- unlike its sibling fields above, this one is now genuinely
    // live-settable, see the SenseLevel property and _pendingSenseLevelRequest below.
    private int _senseLevel;

    private sealed record SenseLevelRequest(int Level);

    // Deferred-request latch, exact same shape as _pendingNotchRequest (see that field's own doc
    // comment) -- safe to set from any thread, drained by ApplyPendingSenseLevelRequest at the same
    // PushSamplesCore point notch/scope-capture already are. A record class (not `int?`) because
    // Interlocked.Exchange<T> requires a reference type -- there is no Nullable<T> overload.
    private SenseLevelRequest? _pendingSenseLevelRequest;

    private sealed record DecoderFlagsRequest(bool AutoSyncEnabled, bool AutoStopEnabled, bool AutoSlantEnabled, bool SyncRestartEnabled, bool AfcEnabled);

    // Deferred-request latch for AutoSyncEnabled/AutoStopEnabled/AutoSlantEnabled/SyncRestartEnabled
    // (2026-08-27, restart-required-settings backlog item 1) -- a CAS-loop merge, not a bare
    // Interlocked.Exchange like _pendingSenseLevelRequest above, because 4 INDEPENDENT property
    // setters share this one record and must not lose each other's not-yet-drained changes (plan-
    // review round 1 finding: a plain write is unsafe for these tight-loop-read fields for the same
    // reason SenseLevel's own doc comment already gives for SenseLevel itself -- no memory-model
    // ordering guarantee against the decode loop, and no atomicity across a multi-field group; plan-
    // review round 2 finding: the FIRST fix attempt still lost updates because its CAS baseline was
    // reconstructed from these very fields below, which only the DRAIN writes -- a UI setter racing a
    // drain could rebuild its baseline from a stale pre-drain read and silently revert a sibling's
    // just-applied change).
    //
    // _desiredFlags is UI-thread-owned: every setter CAS-updates it, the drain (ApplyPendingDecoderFlagsRequest)
    // NEVER writes it. _appliedFlags is decode-thread-owned: ONLY the drain writes it, used purely for
    // reference-equality edge detection ("has anything been requested since the last drain") -- no
    // synchronization needed for _appliedFlags itself, since it has exactly one writer and one reader,
    // both the same (decode) thread. Both start pointing at the SAME instance (constructor), so the
    // drain's first no-op check correctly sees "nothing pending" until a setter is actually called.
    //
    // Getter contract (plan-review round 3, resolved per-layer, do not blur the two): THIS class's own
    // four properties below return the DECODE-THREAD-OWNED, already-applied plain fields
    // (_autoSyncEnabled/etc., last-drained) -- mirrors SenseLevel's own inner-layer getter (:1804,
    // "the getter returns the already-CLAMPED value... currently in effect"). RestartableSstvDecoder's
    // WRAPPER-layer properties must NOT simply forward to these getters -- they keep their OWN
    // immediately-updated fields (last-REQUESTED), exactly like RestartableSstvDecoder.SenseLevel's own
    // wrapper shape (see that property's doc comment for why: a wrapper getter that forwarded to this
    // inner class's own deferred value could read stale for a moment after a set, and CreateInner needs
    // the wrapper's own field as the seed for a rebuilt inner across a maintenance swap either way).
    private DecoderFlagsRequest _desiredFlags; // assigned in the constructor, never null after that
    private DecoderFlagsRequest _appliedFlags; // decode-thread-only; assigned in the constructor and by the drain

    /// <summary>Builds a decoder from one resolved settings record, so a caller cannot drop or transpose
    /// a field the way a hand-written 19-argument call can. Seeds <see cref="SnrMeasurementEnabled"/>
    /// from the record too, which the parameter-list constructor below leaves at its own default (off).
    /// See that constructor's parameter docs for each value's meaning and legacy source.</summary>
    public AnalogFmSstvDecoder(int sampleRate, ResolvedSstvDecoderSettings settings, ILoggerFactory? loggerFactory = null, ScopeCaptureBuffer? scopeCaptureChannel0 = null, ScopeCaptureBuffer? scopeCaptureChannel1 = null)
        : this(
            sampleRate: sampleRate,
            afcEnabled: settings.AfcEnabled,
            syncRestartEnabled: settings.SyncRestartEnabled,
            autoSyncEnabled: settings.AutoSyncEnabled,
            autoStopEnabled: settings.AutoStopEnabled,
            autoSlantEnabled: settings.AutoSlantEnabled,
            senseLevel: settings.SenseLevel,
            demodType: settings.DemodType,
            rxBpfPreset: settings.RxBpfPreset,
            rxBufferMode: settings.RxBufferMode,
            pllVcoGain: settings.PllVcoGain,
            pllLoopOrder: settings.PllLoopOrder,
            pllLoopCutoffHz: settings.PllLoopCutoffHz,
            pllOutputOrder: settings.PllOutputOrder,
            pllOutputCutoffHz: settings.PllOutputCutoffHz,
            zeroCrossingSmoothingMode: settings.ZeroCrossingSmoothingMode,
            zeroCrossingOutputOrder: settings.ZeroCrossingOutputOrder,
            zeroCrossingOutputCutoffHz: settings.ZeroCrossingOutputCutoffHz,
            zeroCrossingSmoothingFrequencyHz: settings.ZeroCrossingSmoothingFrequencyHz,
            loggerFactory: loggerFactory,
            scopeCaptureChannel0: scopeCaptureChannel0,
            scopeCaptureChannel1: scopeCaptureChannel1)
    {
        SnrMeasurementEnabled = settings.SnrMeasurementEnabled;
    }

    /// <param name="sampleRate">Positive whole-Hz sample rate for this low-level decoder. Direct
    /// construction deliberately permits values outside <see cref="SstvSampleRate"/>'s configured
    /// 5000-48500 Hz policy for focused tests and low-level tools; those values do not carry the
    /// production long-session index-safety guarantee. Production composition must use
    /// <see cref="RestartableSstvDecoder"/>, which enforces the configured range and performs
    /// capacity-aware replacement.</param>
    /// <param name="senseLevel">Squelch preset index (0-3, "Very low".."Very high"), see
    /// <see cref="SenseLevelPresets"/>. Out-of-range values (e.g. a hand-edited settings.json) fall
    /// back to index 0, matching legacy's own SetSenseLvl switch `default:` branch -- deliberately
    /// NOT the same fallback as an absent/null setting (see SstvDecoderSettings.SenseLevel's own doc
    /// comment). This constructor value is only the SEED -- unlike afcEnabled above, this one is
    /// genuinely live-settable after construction (user-reported 2026-08-27, "Squelch level" live
    /// control), matching legacy's own Option.cpp:613 (which calls SetSenseLvl() on the live CSSTVDEM
    /// instantly) -- see the <see cref="SenseLevel"/> property's own doc comment. (autoSyncEnabled/
    /// autoStopEnabled/autoSlantEnabled/syncRestartEnabled below are ALSO now genuinely live-settable,
    /// 2026-08-27 -- only afcEnabled/demodType/rxBpfPreset/rxBufferMode remain restart-only.)</param>
    /// <param name="demodType">Main-picture FM demodulator algorithm, mirrors legacy's
    /// <c>CSSTVDEM::m_Type</c> (`sstv.cpp:2256-2269`). Legacy's real compiled-in default is
    /// <see cref="DemodType.Hilbert"/> (`sstv.cpp:1492`), matching this port's own pre-existing
    /// hardcoded behavior. Restart-only, same reasoning/limitation as afcEnabled/rxBpfPreset/
    /// rxBufferMode -- unlike senseLevel/autoSyncEnabled/autoStopEnabled/autoSlantEnabled/
    /// syncRestartEnabled above/below, this one has no live-apply path.</param>
    /// <param name="rxBpfPreset">RX bandpass-filter sharpness, mirrors legacy's real
    /// <c>CSSTVDEM::m_bpf</c> (`sstv.cpp:1522-1550`'s <c>CalcBPF</c>, `.ini` key <c>DEMBPF</c>).
    /// Legacy's real compiled-in default is <see cref="RxBpfPreset.Wide"/> (`sstv.cpp:1416`,
    /// `m_bpf=1`), matching this port's own pre-existing hardcoded behavior before the RX BPF
    /// runtime-dispatch subsystem made the other three live alternatives. Restart-only, same
    /// reasoning/limitation as demodType above (see that parameter's own doc comment).</param>
    /// <param name="rxBufferMode">RX buffer mode, mirrors legacy's real <c>sys.m_UseRxBuff</c>
    /// (`sstv.cpp:1626-1644`'s <c>OpenCloseRxBuff</c>). Legacy's real compiled-in default is
    /// <see cref="RxBufferMode.On"/> (`Main.cpp:899`, `sys.m_UseRxBuff=1`). Gates real decode-path
    /// behavior -- see <see cref="_rxBufferMode"/>'s own doc comment for the current read sites.
    /// Restart-only, same reasoning/limitation as demodType above (see that parameter's own doc
    /// comment).</param>
    public AnalogFmSstvDecoder(int sampleRate = 11025, bool afcEnabled = true, bool syncRestartEnabled = true, bool autoSyncEnabled = true, bool autoStopEnabled = false, bool autoSlantEnabled = true, int senseLevel = 1, DemodType demodType = DemodType.Hilbert, RxBpfPreset rxBpfPreset = RxBpfPreset.Wide, RxBufferMode rxBufferMode = RxBufferMode.On, double pllVcoGain = 1.0, int pllLoopOrder = 1, double pllLoopCutoffHz = 1500, int pllOutputOrder = 3, double pllOutputCutoffHz = 900, ZeroCrossingSmoothingMode zeroCrossingSmoothingMode = ZeroCrossingSmoothingMode.Iir, int zeroCrossingOutputOrder = 3, double zeroCrossingOutputCutoffHz = 900, double zeroCrossingSmoothingFrequencyHz = 2200, ILoggerFactory? loggerFactory = null, ScopeCaptureBuffer? scopeCaptureChannel0 = null, ScopeCaptureBuffer? scopeCaptureChannel1 = null, double hilbertOutputCutoffHz = HilbertFmDemodulator.DefaultOutputCutoffHz)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);

        // Un-stub-RX-tab Piece B: unlike every other constructor-injected field above/below, these
        // two are OPTIONALLY externally-owned -- RestartableSstvDecoder passes the SAME two
        // instances into every CreateInner call across a periodic rebuild (an in-progress capture
        // must survive a restart, unlike notch state's fresh-reapply-per-rebuild shape; legacy's own
        // CScope has no rebuild concept to begin with, it just lives on CSSTVDEM for that object's
        // whole lifetime). Defaults to fresh instances for direct/test construction.
        _scopeCaptureChannel0 = scopeCaptureChannel0 ?? new ScopeCaptureBuffer();
        _scopeCaptureChannel1 = scopeCaptureChannel1 ?? new ScopeCaptureBuffer();

        _sampleRate = sampleRate;
        _afcEnabled = afcEnabled;
        _autoSyncEnabled = autoSyncEnabled;
        _autoStopEnabled = autoStopEnabled;
        _syncRestartEnabled = syncRestartEnabled;
        _autoSlantEnabled = autoSlantEnabled;
        // Same reference initially -- see _desiredFlags/_appliedFlags's own doc comment for why this
        // matters (the drain's first no-op check must correctly see "nothing pending" until a setter
        // is actually called).
        _desiredFlags = _appliedFlags = new DecoderFlagsRequest(autoSyncEnabled, autoStopEnabled, autoSlantEnabled, syncRestartEnabled, afcEnabled);
        _senseLevel = senseLevel is >= 0 and <= 3 ? senseLevel : 0;
        (_slvl, _slvl2, _slvl3) = SenseLevelPresets[_senseLevel];
        _demodType = demodType;
        _pllVcoGain = PllFmDemodulator.NormalizeVcoGain(pllVcoGain);
        _pllLoopOrder = pllLoopOrder;
        _pllLoopCutoffHz = pllLoopCutoffHz;
        _pllOutputOrder = pllOutputOrder;
        _pllOutputCutoffHz = pllOutputCutoffHz;
        _zeroCrossingSmoothingMode = zeroCrossingSmoothingMode;
        _zeroCrossingOutputOrder = zeroCrossingOutputOrder;
        _zeroCrossingOutputCutoffHz = zeroCrossingOutputCutoffHz;
        _zeroCrossingSmoothingFrequencyHz = zeroCrossingSmoothingFrequencyHz;
        _demodulator = new HilbertFmDemodulator(sampleRate, hilbertOutputCutoffHz);
        _pllDemodulator = new PllFmDemodulator(sampleRate, DemodulatorLowHz, DemodulatorHighHz, pllVcoGain, pllLoopOrder, pllLoopCutoffHz, pllOutputOrder, pllOutputCutoffHz);
        _zeroCrossingDemodulator = new ZeroCrossingFrequencyCounter(sampleRate, zeroCrossingSmoothingMode, zeroCrossingOutputOrder, zeroCrossingOutputCutoffHz, zeroCrossingSmoothingFrequencyHz);
        _afcZeroCrossingCounter = new ZeroCrossingFrequencyCounter(sampleRate, zeroCrossingSmoothingMode, zeroCrossingOutputOrder, zeroCrossingOutputCutoffHz, zeroCrossingSmoothingFrequencyHz);
        _rxBpfPreset = rxBpfPreset;
        // Round-2 auditor finding (D0-audit round-7: moved to sit directly above the call it
        // actually describes, previously misplaced one statement early): pass the
        // `syncRestartEnabled` CTOR PARAMETER directly here, not the `_syncRestartEnabled` FIELD
        // assigned a few lines above -- today the field happens to already be set first, but that's
        // incidental to field-declaration order, not a guarantee; a future reorder would silently
        // default the field to `false` with no compiler error and no test catching it unless a test
        // specifically pins fcl with sync-restart on.
        _searchBandpassFilter = rxBpfPreset == RxBpfPreset.Off
            ? null
            : new SearchBandpassFilter(sampleRate, rxBpfPreset, syncRestartEnabled);
        _rxBufferMode = rxBufferMode;
        // RX buffer subsystem Phase 7 (decoder-wiring sub-piece): On gets the RAM implementation,
        // constructed against `sampleRate` (this class's own NOMINAL sample-rate parameter, never a
        // slant/AFC-corrected rate -- see RxLineStagingBuffer's own constructor doc comment for why
        // that distinction matters for its capacity formula); Extended gets the disk-backed
        // implementation (no sample-rate-derived capacity -- unbounded by design, see
        // RxDiskLineStagingBuffer's own doc comment); Off gets null, unchanged.
        _rxLineStagingBuffer = rxBufferMode switch
        {
            RxBufferMode.On => new RxLineStagingBuffer(sampleRate),
            RxBufferMode.Extended => new RxDiskLineStagingBuffer(
                (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RxDiskLineStagingBuffer>()),
            _ => null,
        };
        _syncBypass1Tracker = new SyncIntervalTracker(sampleRate, isNarrow: false, SstvModeRegistry.GetSyncIntervalCandidates(sampleRate));
        _syncBypass1200Detector = new SyncEnvelopeDetector(sampleRate, 1200.0);
        _syncBypass1900Detector = new SyncEnvelopeDetector(sampleRate, 1900.0);
        _syncBypassTracker = new SyncIntervalTracker(sampleRate, isNarrow: false, SstvModeRegistry.GetSyncIntervalCandidates(sampleRate));
        _syncBypassFskDetector = new SyncEnvelopeDetector(sampleRate, VisHeader.NarrowSpaceFrequencyHz);
        _syncBypassNarrowTracker = new SyncIntervalTracker(sampleRate, isNarrow: true, SstvModeRegistry.GetSyncIntervalCandidates(sampleRate));
        _visLockStateMachine = new VisLockStateMachine(sampleRate, _slvl, _slvl2);
        _narrowFskDecoder = new NarrowFskHeaderDecoder(sampleRate); // S8 fix -- constructed once, decoder-lifetime, never Reset()
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

    // Milestone-audit Phase 3 (spec/14-roadmap.md, "Other RX chain findings"): CLAUDE.md §4's
    // concurrency rule requires every cross-thread event stream to state its scheduler and
    // slow-subscriber behavior -- these three didn't. Stated here, for all three below:
    //
    // Scheduler: NONE. Each is a plain C# multicast delegate, invoked SYNCHRONOUSLY from inside
    // PushSamples's own call stack (TryProcessBuffer's per-line loop, or the header-detection
    // branches above it) -- there is no thread marshaling, no SynchronizationContext capture, no
    // background dispatch. The calling thread (whatever thread called PushSamples) IS the thread
    // every subscriber runs on.
    //
    // Slow-subscriber behavior: BLOCKS. A subscriber that does real work (rendering, I/O) blocks
    // PushSamples -- and therefore the caller -- for the duration. No buffering, no dropping.
    //
    // Re-entrancy: REJECTED. A subscriber that calls PushSamples again (directly or indirectly) gets
    // InvalidOperationException before it can mutate the outer call's live state. That exception is
    // treated as a subscriber failure: later subscribers still run, the outer transition completes,
    // and the exception is rethrown from the stable PushSamples boundary.
    //
    // LineDecoded specifically also hands out a LIVE ALIAS of this decoder's own mutable pixel
    // buffer, not a copy -- MutableImageSource wraps `pixels` (the same array `Commit`/
    // `AbandonInProgressImage` will later replace or that subsequent lines will keep mutating in
    // place), matching the pattern GoldenVectorTests.cs's own `Snapshot` helper works around test-side
    // (see that helper's own doc comment for the general hazard). A subscriber that queues the
    // `IImageSource` for later/async rendering instead of consuming it synchronously will read torn or
    // stale-image data. Not a legacy divergence (legacy's own `PostMessage`-driven UI read a bitmap
    // the DSP side also owned) -- but an undocumented ownership contract at exactly the seam the
    // eventual UI will attach to. Deliberately NOT changed to a defensive copy here: no production
    // subscriber exists yet to need one, and CLAUDE.md's own guidance is not to add cost/complexity
    // for a scenario that can't happen today -- documented so whoever wires the first real subscriber
    // makes an informed choice (consume synchronously, or copy at the subscription site).
    public event Action<DecodedImageUpdate>? LineDecoded;

    public event Action<SstvModeDefinition>? ModeDetected;

    public event Action<SstvModeDefinition>? DecodeRestarted;

    // Public decode notifications are synchronous, but a subscriber failure must not interrupt a
    // partially-completed decoder transition. One PushSamples call owns this scope; event fan-out
    // records the first subscriber failure and processing continues to the stable operation tail.
    private int _pushActive;
    private bool _subscriberFailureDeferralActive;
    private ExceptionDispatchInfo? _deferredSubscriberFailure;

    // ISstvDecoder.ReceptionSequence's backing field -- see that property's own doc comment for the
    // full contract. Interlocked, not a plain field: this class's own PushSamples recursion guard
    // above already establishes that a caller may poll properties from a different thread than the
    // one driving PushSamples, and ReceptionSequence's own contract promises any-thread reads.
    private long _receptionSequence;

    /// <summary>See <see cref="ISstvDecoder.ReceptionSequence"/>.</summary>
    public long ReceptionSequence => Interlocked.Read(ref _receptionSequence);

    // ISstvDecoder.AnchorLagSamples's backing field -- Volatile, not a plain int, for the same
    // any-thread-read reason as _receptionSequence immediately above (int reads/writes are
    // atomic on every supported platform, but Volatile is still needed for cross-thread
    // visibility of the write in FinalizeAnchorAndStartDecoding without a full fence).
    private int _anchorLagSamples;

    /// <summary>See <see cref="ISstvDecoder.AnchorLagSamples"/>.</summary>
    public int AnchorLagSamples => Volatile.Read(ref _anchorLagSamples);

    public void PushSamples(ReadOnlyMemory<float> samples)
    {
        if (Interlocked.CompareExchange(ref _pushActive, 1, 0) != 0)
        {
            throw new InvalidOperationException("Recursive or concurrent PushSamples calls are not supported.");
        }

        try
        {
            ExecuteWithDeferredSubscriberFailures(() => PushSamplesCore(samples));
        }
        finally
        {
            Volatile.Write(ref _pushActive, 0);
        }
    }

    private void PushSamplesCore(ReadOnlyMemory<float> samples)
    {
        // D2 round 1 fix: this was the only real gap in the public IDisposable contract -- no member
        // on this type previously threw ObjectDisposedException after Dispose(), and behavior was
        // actually INCONSISTENT across code paths (RxBufferMode.Extended's disk-backed replay path
        // already throws from deep inside a post-dispose PushSamples call via
        // RxDiskLineStagingBuffer's own guard, while every other path just silently kept running).
        // Not reachable via the production wrapper today (RestartableSstvDecoder disposes an instance
        // only after fully unsubscribing and never pushing to it again), but this is a public type with
        // no such guarantee documented for arbitrary callers.
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Consumed BEFORE _forcedMode below (plan-review round 3 finding -- see
        // RequestAbandonReception's own doc comment for why the order matters): a pending pause
        // request must tear down whatever's in progress before any pending ForceMode request gets a
        // chance to commit a new one, so a mode forced while paused always lands cleanly.
        if (Interlocked.Exchange(ref _abandonRequested, 0) != 0)
        {
            if (_mode is not null)
            {
                var previousMode = _mode;
                var hadPendingAnchor = _pendingAnchorCorrectionMode is not null;
                EndOfImage(applyDeadTime: false);
                if (previousMode is not null && !hadPendingAnchor)
                {
                    RaiseSubscribers(DecodeRestarted, previousMode);
                }
            }
            else
            {
                // Idle or AVT-training-pending: EndOfImage's default _consumedSamples anchor is only
                // valid while a mode is/was locked -- see EndOfImage's own resumeFrom doc comment.
                // No DecodeRestarted here, matching legacy's WriteHistory being gated on m_Sync
                // (false during AVT training -- no mode was ever locked to report).
                EndOfImage(applyDeadTime: false, resumeFrom: TotalSamplesReceived);
            }
        }

        // Consumed before _reSyncRequested below: legacy resolves the same simultaneous-command
        // question by having Start() unconditionally zero m_Skip (sstv.cpp:1725), i.e. a forced start
        // wins over a pending ReSync. PerformForceMode's own Commit() -> AbandonInProgressImage() ->
        // ResetReSyncState() chain clears _reSyncRequested/_pendingSkipSamples as a side effect, so no
        // separate interaction code is needed here beyond this ordering.
        var forcedMode = Interlocked.Exchange(ref _forcedMode, null);
        if (forcedMode is not null)
        {
            PerformForceMode(forcedMode);
        }

        if (_reSyncRequested)
        {
            _reSyncRequested = false;
            PerformReSync();
        }

        // Consumed after _reSyncRequested, before the raw-sample append loop below -- the group-
        // delay compensation this can trigger (ApplySyncCorrection/ApplyNotchDisableShift) needs
        // _consumedSamples/TotalSamplesReceived/etc. as they stand BEFORE this batch's samples are
        // appended, and the append loop right below needs _notchFilter to already reflect this
        // batch's toggle state for every sample in it.
        ApplyPendingNotchRequest();
        ApplyPendingScopeCaptureArm();
        // Order vs. the calls above/below doesn't matter -- zero field overlap (notch touches
        // _notchFilter/_notchFrequencyHz/sync-correction bookkeeping, scope capture touches its own
        // arm/fill state, PLL tuning touches only _pllDemodulator/_avtPllDemodulator/_pll* fields,
        // none of which any of the others read or write).
        ApplyPendingPllTuningRequest();
        ApplyPendingZeroCrossingTuningRequest();
        ApplyPendingSenseLevelRequest();
        ApplyPendingSnrMeasurementRequest();
        ApplyPendingModeLockRequest();
        // Order vs. the call above doesn't matter (zero field overlap, see this method's own doc
        // comment) -- but this one MUST stay below the abandon/ForceMode/ReSync/Notch block above,
        // not just below ApplyPendingSenseLevelRequest -- see that method's own doc comment for why.
        ApplyPendingDecoderFlagsRequest();

        var span = samples.Span;
        for (var i = 0; i < span.Length; i++)
        {
            // sstv.cpp:342-347's own notch.Do(*lp) mutates the raw sample in place, upstream of
            // EVERYTHING else (the 2-tap average, the bandpass filter, the FFT) -- _rawSamples has
            // exactly one other consumer in this whole file (FilteredRawSampleAt), so storing the
            // notched value here needs no separate buffer/cache. Only feeds the filter's own delay
            // line while enabled, matching legacy (the notch isn't fed while m_notch=0 either).
            _rawSamples.Add(_notchFilter is null ? span[i] : (float)_notchFilter.ProcessSample(span[i]));
        }

        DrainPendingSkip(); // must run AFTER the append loop above, BEFORE TryProcessBuffer() below --
                             // see that method's own doc comment for why the ordering matters.

        // Band-1 item 4a: _demodulatedFrequencies is no longer filled eagerly here -- see
        // DemodulatedFrequencyAt's own doc comment for why (this used to race the shared bandpass
        // cache all the way to TotalSamplesReceived before Commit() ever got a chance to run, for any
        // bulk-pushed caller). It's now driven lazily, on demand, by whichever real consumer
        // (PixelSampleReader during line decode, ApplyAfcCorrections, or AverageFrequencyInWindow's
        // pre-lock narrow-mode discriminator) actually needs a given index next.
        TryProcessBuffer();
        AdvanceAgcThroughDeadZone();
        TrimBuffers();
        ForceScopeCaptureChannel0Progress();
    }

    private void ExecuteWithDeferredSubscriberFailures(Action action)
    {
        if (_subscriberFailureDeferralActive)
        {
            throw new InvalidOperationException("A subscriber-failure deferral scope is already active.");
        }

        _subscriberFailureDeferralActive = true;
        _deferredSubscriberFailure = null;
        ExceptionDispatchInfo? decoderFailure = null;
        ExceptionDispatchInfo? subscriberFailure;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            decoderFailure = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            subscriberFailure = _deferredSubscriberFailure;
            _deferredSubscriberFailure = null;
            _subscriberFailureDeferralActive = false;
        }

        // Decoder/state-machine failures take precedence over notification failures observed during
        // the same operation. In either case ExceptionDispatchInfo preserves the original identity
        // and stack instead of replacing it at this arbitration boundary.
        decoderFailure?.Throw();
        subscriberFailure?.Throw();
    }

    private void RaiseSubscribers<T>(Action<T>? handlers, T value)
    {
        if (handlers is null)
        {
            return;
        }

        ExceptionDispatchInfo? localFailure = null;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                handler(value);
            }
            catch (Exception ex)
            {
                var failure = ExceptionDispatchInfo.Capture(ex);
                if (_subscriberFailureDeferralActive)
                {
                    _deferredSubscriberFailure ??= failure;
                }
                else
                {
                    localFailure ??= failure;
                }
            }
        }

        localFailure?.Throw();
    }

    /// <summary>Edge trim applied to both ends of the sync pulse for the SNR window, in ms.</summary>
    internal double SyncSnrTrimMs { get; set; } = SyncSnrTrimDefaultMs;

    /// <summary>Chosen by the step-7 trim sweep (docs/reception-snr-validation.md).</summary>
    internal const double SyncSnrTrimDefaultMs = 0.75;

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

    /// <summary>Diagnostic-only: fires the PROVISIONAL, immediate lock anchor the moment
    /// <see cref="Commit"/> sets it -- not <see cref="ModeDetected"/>, which is deferred until
    /// <c>TryResolveSyncAnchorCorrection</c> succeeds, several lines (and several buffered picture
    /// lines) later. Kept as permanent test infrastructure (Band-1 item 4a, pre-Phase-2 audit): this
    /// is what let a real spike measure how far <see cref="BandpassFilteredProcessedUpTo"/> had
    /// already raced ahead of the true lock instant, which is exactly the quantity item 4a's fix
    /// needed to bound -- see <c>BandpassCacheChunkInvarianceTests</c>.</summary>
    internal event Action<int>? LockAnchorCommitted;

    /// <summary>See <see cref="ISstvDecoder.RxBufferDegraded"/>. Extended mode only -- always
    /// <see langword="false"/> for On/Off, since neither has a staging buffer that can fail this
    /// way.</summary>
    public bool RxBufferDegraded => _rxLineStagingBuffer?.HasWriteFailed ?? false;

    // RX buffer subsystem Phase 7 (disposal-chain sub-piece): the only disposable resource this class
    // owns is _rxLineStagingBuffer -- RxBufferMode.Extended's disk-backed implementation owns scratch
    // files and a background writer task that must be torn down; RAM's own Dispose() is a documented
    // no-op, and null (RxBufferMode.Off) needs nothing. No other field in this class implements
    // IDisposable (SearchBandpassFilter/SyncIntervalTracker/SyncEnvelopeDetector are all pure DSP
    // state, verified before adding this, along with every other field in the class). Idempotent (a
    // guard field) -- code-review correction: RestartableSstvDecoder's own two dispose call sites
    // (Swap()'s outgoing-instance disposal, and its own Dispose() disposing whichever instance is
    // current) each target a DIFFERENT AnalogFmSstvDecoder instance, never the same one twice, so
    // that chain alone can't double-dispose any single instance here. The real reason for the guard:
    // this is a public IDisposable type, and any direct consumer calling Dispose() twice (the
    // ordinary .NET IDisposable contract, not a scenario specific to this class's own callers) must
    // not throw or double-run the teardown -- the same reasoning RestartableSstvDecoder's own guard
    // states accurately below (that one DOES have a real double-dispose caller: the DI container).
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _rxLineStagingBuffer?.Dispose();
    }

    private sealed class MutableImageSource(int width, int height, Rgb24[] pixels) : IImageSource
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public ReadOnlySpan<Rgb24> GetScanline(int y) => pixels.AsSpan(y * Width, Width);
    }
}
