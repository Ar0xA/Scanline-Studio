using System.Runtime.InteropServices;
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
public sealed class AnalogFmSstvDecoder : ISstvDecoder, IDisposable
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
    internal readonly double _slvl;
    internal readonly double _slvl2;
    internal readonly double _slvl3;

    private readonly int _sampleRate;
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
    // the only other such flag. `_pendingAnchorCorrectionMode` does NOT need its own term here: Commit()
    // sets `_mode` before that flag, so it can never be non-null while `_mode` is null.
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
    private int _afcProcessedUpTo;
    private int _afcBoundSample; // see Commit -- never correct past this image's own generous nominal extent
    private double? _lastAppliedAfcRetuneHz; // last offset applied to _syncEnvelopeDetector; null = never retuned this lock cycle

    private SyncEnvelopeDetector? _syncEnvelopeDetector;
    private SlantTracker? _slantTracker;
    private int? _lastReplayOriginForTests; // RX buffer subsystem Phase 6c -- set inside PerformReplay, test-only
    // RX buffer subsystem Phase 6c round-2 code-review fix (round-3 correction: this is a RUNNING
    // OFFSET, not a fixed anchor -- see below): the value such that `_consumedSamples -
    // _rxBufferAnchorSample == (a raw-sample-equivalent count of everything staged in
    // _rxLineStagingBuffer, plus whatever's been consumed live but not yet flushed there)`. Set
    // initially to _consumedSamples's own value at the moment the buffer is Clear()ed (InitializeSlant),
    // matching "local index 0 corresponds to THIS raw sample" at that instant -- but that identity only
    // holds as long as every unit _consumedSamples advances by is EITHER eventually staged OR explicitly
    // compensated for here. DrainPendingSkip's own per-sample skip loop advances this field by 1 per
    // skipped sample, keeping the invariant true across a manual-ReSync-driven skip. Round-4 code review
    // correction: PerformReplay's own forward cursor jump does NOT advance this field additively anymore
    // (an earlier, round-3 version of this fix did) -- round-4's own two-pass trace found the additive
    // fix kept this COUNT invariant true while leaving the staging buffer PHYSICALLY discontinuous (the
    // jumped samples are never staged, but the flat buffer has no gap marker), so a SECOND replay pass
    // would read straight across the splice as if it were continuous audio. PerformReplay now truncates
    // the staging buffer at its own jump instead (see that method's own doc comment), which makes a
    // direct re-anchor (`_rxBufferAnchorSample = _consumedSamples`) the correct operation there, not an
    // additive one -- the buffer is empty again, so "local index 0" genuinely IS "this raw sample" once
    // more, the same identity InitializeSlant establishes at a fresh lock. Deliberately NOT derived from
    // _consumedSamples/_rxBufferLineDemod.Count/stagedSampleCount at read time, which round-1 code
    // review found silently decouples from the true anchor once TryAppendLine starts rejecting lines at
    // capacity (ApplySlantTracking still clears the per-line capture accumulators on a rejected append --
    // see that method's own capture-flush hook).
    private int _rxBufferAnchorSample;
    // RX buffer subsystem Phase 6c round-4 fix, companion to _rxBufferAnchorSample: the TRANSMISSION-LINE
    // index (not bitmap row -- multiply by RowsPerTransmissionLine at use, matching _nextLine's own
    // reconciliation convention) of the image row that staged index 0 currently corresponds to. Zero
    // from a fresh lock (InitializeSlant, which Clear()s the buffer at the image's own first line), and
    // re-based by PerformReplay every time it truncates the staging buffer at its own forward cursor
    // jump. Without this, PerformReplay's own row loop -- which otherwise hard-assumes staged index 0 is
    // always image row 0, true only for an untruncated buffer -- would stamp mid-image content into rows
    // 0..N on every replay pass after the first truncation.
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
    // reads the staging buffer, and PerformReplay is destructive, so draining at a caller-chunk
    // boundary would make the decoded image a function of how the caller sliced its PushSamples calls.
    private volatile bool _correctSlantRequested;
    private int _pendingSkipSamples; // port-equivalent of legacy's own m_Skip field
    private double? _lastLineSyncPeakPosition; // port-equivalent of m_SyncRPos (see ApplySlantTracking's capture point for why one field also stands in for m_SyncPos at this port's granularity)
    private bool _suppressNextSlantProcessLine;
    private bool _slantCorrectionsDisabledForRestOfImage; // port of m_AutoSyncCount's gate on AutoStopJob's correction branch

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
    // decode itself, and PerformReplay is DESTRUCTIVE (its cursor jump discards >=1 raw sample and
    // sacrifices a row, and it truncates the staging buffer) -- draining it at a chunk boundary made the
    // DECODED IMAGE a function of how the caller sliced its PushSamples calls (caught by
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
    // truncated). Round-1 code review found this port's own PerformReplay -- which DOES sacrifice one
    // row per pass, by design, see that method's own doc comment -- makes the SAME unconditional trigger
    // a guaranteed visible defect (a small black stripe) in EVERY default decode once wired to fire
    // automatically, even when no correction was ever needed. This flag gates the latch so that cost is
    // only paid when a correction has actually committed -- i.e. when replay is actually fixing
    // something. Reset at every fresh lock (InitializeSlant).
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
    //
    // S12 code-level review addition: since m_sint2/m_sint3 now gate on VisLockStateMachine's own
    // state (see the S12 comments on those blocks below), a transient false Search->ConfirmLock->
    // DecodeVis in THAT copy during this same resettle window doesn't just disagree with m_sint1's
    // latch -- it also freezes m_sint2/m_sint3 for up to ~270ms (~540ms extended) where legacy would
    // not. Bounded and low-probability (same settling-time order of magnitude as the disagreement
    // above), and VisLockStateMachine's own state is the more legacy-faithful of the two available
    // proxies -- not fixed, flagged so a future reader has the fuller picture.
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

    // Settings-driven toggle, real but not a legacy port -- legacy's own AFC (sstv.cpp:1471) is
    // unconditionally always-on with no user-facing off switch of its own; the only "off" case that
    // exists in legacy is AVT mode's own exclusion (see InitializeAfc below), which this field does
    // NOT replace -- AVT stays excluded regardless of this flag's value. Restart-only: this decoder
    // is a DI singleton constructed once (Program.cs), and this field is readonly -- changing the
    // setting takes effect on the next app launch, not live.
    private readonly bool _afcEnabled;

    // Port of legacy's real m_SyncRestart (default 1, sstv.cpp:1486, toggled via the "Lock" toolbar
    // button, Main.cpp:10907/11887's SBLKClick) -- see the mid-reception restart call site in
    // TryProcessBuffer for the full citation trail on why this port previously hard-wired it on.
    // Restart-only, same reasoning as _afcEnabled above.
    private readonly bool _syncRestartEnabled;

    // Port of legacy's real sys.m_AutoSync (default 1, Main.cpp:901) -- gates only the two Auto Sync
    // trigger branches (TryAutoSync's own doc comment), not the drift-detection bookkeeping that
    // Main.cpp:3886's own outer gate (constant-true in this port, see TryAutoSync) keeps running
    // unconditionally either way. Restart-only, same reasoning as _afcEnabled above.
    private readonly bool _autoSyncEnabled;

    // Port of legacy's real sys.m_AutoStop (Main.cpp:900) -- unlike _afcEnabled/_syncRestartEnabled/
    // _autoSyncEnabled above, legacy's own FRESH default is OFF, not on. Caveat: SBLKClick
    // (Main.cpp:10898-10907) ties sys.m_AutoStop to the same Lock toolbar button as the other two
    // (`sys.m_AutoStop = !SBLK->Down`) -- Lock engaged means all three are off, Lock DISENGAGED
    // (unlocked) means all three are on. Legacy's Lock button can never produce this port's own
    // default combination (SyncRestart=true, AutoSync=true, AutoStop=false); default-false here is a
    // deliberate, defensible startup default matching sys's own unmodified .ini default, not a claim
    // that it matches any single real legacy Lock state.
    private readonly bool _autoStopEnabled;

    // Port of legacy's real KRSA->Checked (Main.cpp:1863's Define/AutoSlant .ini key) -- gates only
    // SlantTracker's own correction-commit branch in ApplySlantTracking (KRSA->Checked's exact scope
    // per SlantTracker.cs's own doc comment, Main.cpp:3968-4018), not the drift-detection bookkeeping,
    // which runs unconditionally either way, same reasoning as _autoSyncEnabled/_autoStopEnabled
    // above. ALSO gates TryAutoSync's own branch-1 threshold (Main.cpp:3910/:3917's
    // `(KRSA->Checked ? 5 : 2)*m_Mult`) -- auditor plan-review finding: KRSA->Checked is read at that
    // SEPARATE call site too, not just the slant-commit block; this port previously hardcoded the `5`
    // side only because this flag didn't exist yet. Restart-only, same reasoning as _afcEnabled above.
    private readonly bool _autoSlantEnabled;

    /// <param name="senseLevel">Squelch preset index (0-3, "Very low".."Very high"), see
    /// <see cref="SenseLevelPresets"/>. Out-of-range values (e.g. a hand-edited settings.json) fall
    /// back to index 0, matching legacy's own SetSenseLvl switch `default:` branch -- deliberately
    /// NOT the same fallback as an absent/null setting (see SstvDecoderSettings.SenseLevel's own doc
    /// comment). Restart-only: unlike legacy's Option.cpp:613 (which calls SetSenseLvl() on the live
    /// CSSTVDEM instantly), this is read once at DI construction (ScanlineStudio.Host.Program), same
    /// limitation as afcEnabled/autoStopEnabled/etc. above -- most user-visible for this particular
    /// field since squelch is the control most likely to be adjusted while actively chasing a signal.</param>
    /// <param name="demodType">Main-picture FM demodulator algorithm, mirrors legacy's
    /// <c>CSSTVDEM::m_Type</c> (`sstv.cpp:2256-2269`). Legacy's real compiled-in default is
    /// <see cref="DemodType.Hilbert"/> (`sstv.cpp:1492`), matching this port's own pre-existing
    /// hardcoded behavior. Restart-only, same reasoning/limitation as every other parameter here.</param>
    /// <param name="rxBpfPreset">RX bandpass-filter sharpness, mirrors legacy's real
    /// <c>CSSTVDEM::m_bpf</c> (`sstv.cpp:1522-1550`'s <c>CalcBPF</c>, `.ini` key <c>DEMBPF</c>).
    /// Legacy's real compiled-in default is <see cref="RxBpfPreset.Wide"/> (`sstv.cpp:1416`,
    /// `m_bpf=1`), matching this port's own pre-existing hardcoded behavior before the RX BPF
    /// runtime-dispatch subsystem made the other three live alternatives. Restart-only, same
    /// reasoning/limitation as every other parameter here.</param>
    /// <param name="rxBufferMode">RX buffer mode, mirrors legacy's real <c>sys.m_UseRxBuff</c>
    /// (`sstv.cpp:1626-1644`'s <c>OpenCloseRxBuff</c>). Legacy's real compiled-in default is
    /// <see cref="RxBufferMode.On"/> (`Main.cpp:899`, `sys.m_UseRxBuff=1`). Gates real decode-path
    /// behavior -- see <see cref="_rxBufferMode"/>'s own doc comment for the current read sites.
    /// Restart-only, same reasoning/limitation as every other parameter here.</param>
    public AnalogFmSstvDecoder(int sampleRate = 11025, bool afcEnabled = true, bool syncRestartEnabled = true, bool autoSyncEnabled = true, bool autoStopEnabled = false, bool autoSlantEnabled = true, int senseLevel = 1, DemodType demodType = DemodType.Hilbert, RxBpfPreset rxBpfPreset = RxBpfPreset.Wide, RxBufferMode rxBufferMode = RxBufferMode.On)
    {
        _sampleRate = sampleRate;
        _afcEnabled = afcEnabled;
        _autoSyncEnabled = autoSyncEnabled;
        _autoStopEnabled = autoStopEnabled;
        _syncRestartEnabled = syncRestartEnabled;
        _autoSlantEnabled = autoSlantEnabled;
        (_slvl, _slvl2, _slvl3) = SenseLevelPresets[senseLevel is >= 0 and <= 3 ? senseLevel : 0];
        _demodType = demodType;
        _demodulator = new HilbertFmDemodulator(sampleRate);
        _pllDemodulator = new PllFmDemodulator(sampleRate, DemodulatorLowHz, DemodulatorHighHz);
        _zeroCrossingDemodulator = new ZeroCrossingFrequencyCounter(sampleRate);
        _afcZeroCrossingCounter = new ZeroCrossingFrequencyCounter(sampleRate);
        // Round-2 auditor finding: pass the `syncRestartEnabled` CTOR PARAMETER directly here, not the
        // `_syncRestartEnabled` FIELD assigned two lines above -- today the field happens to already be
        // set first, but that's incidental to field-declaration order, not a guarantee; a future
        // reorder would silently default the field to `false` with no compiler error and no test
        // catching it unless a test specifically pins fcl with sync-restart on.
        _rxBpfPreset = rxBpfPreset;
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
            RxBufferMode.Extended => new RxDiskLineStagingBuffer(),
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
            var useLocked = _mode is not null && _mode.NarrowModeCode is null && thisIndex >= _bandpassLockedFromSample;
            if (useLocked)
            {
                FirstLockedBandpassIndex ??= thisIndex; // diagnostic-only, see its own doc comment
            }

            var rawFiltered = FilteredRawSampleAt(thisIndex);
            _bandpassFilteredSamples.Add(_searchBandpassFilter?.ProcessSample(rawFiltered, useLocked) ?? rawFiltered);
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
    // Re-entrancy: UNGUARDED. A subscriber that calls PushSamples again (directly, or indirectly via
    // a scheduler that re-enters synchronously) re-enters TryProcessBuffer while `mode`/`pixels`/
    // `lineDecoder` locals from the OUTER call are still live on the stack, mutating the same
    // `_consumedSamples`/`_nextLine`/`_mode`/`_pixels` fields the outer call will resume reading from
    // once the inner call returns -- the outer call then continues against fields that may belong to
    // a different image/epoch than its own captured locals. No production caller does this today (no
    // reachable path re-enters PushSamples from within one of these three handlers), so currently
    // safe -- but a future UI/`ScanlineStudio.Application` subscriber must not call back into this decoder
    // synchronously from any of these three handlers.
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

    /// <summary>See <see cref="ISstvDecoder.ResetAgc"/>.</summary>
    public void ResetAgc() => _levelAgc.Init();

    /// <summary>See <see cref="ISstvDecoder.RequestReSync"/>. A single volatile write, safe from any
    /// thread -- consumed at the top of the next <see cref="PushSamples"/> call, on whichever thread
    /// actually calls that (matching this class's own established single-caller-thread contract).</summary>
    public void RequestReSync() => _reSyncRequested = true;

    /// <summary>See <see cref="ISstvDecoder.RequestCorrectSlant"/>. A single volatile write, safe from
    /// any thread -- consumed inside <see cref="TryProcessBuffer"/>'s own per-line loop on whichever
    /// thread next calls <see cref="PushSamples"/>, NOT at the top of that call (see
    /// <see cref="_correctSlantRequested"/>'s own doc comment for why).</summary>
    public void RequestCorrectSlant() => _correctSlantRequested = true;

    /// <summary>See <see cref="ISstvDecoder.ForceMode"/>. A single atomic exchange, safe from any
    /// thread -- consumed at the top of the next <see cref="PushSamples"/> call, on whichever thread
    /// actually calls that (matching this class's own established single-caller-thread contract; no
    /// thread marshaling, no synchronization context, no background dispatch).</summary>
    public void ForceMode(SstvModeDefinition mode) => Interlocked.Exchange(ref _forcedMode, mode);

    /// <summary>See <see cref="ISstvDecoder.SlantPpm"/>. Thin wrapper over
    /// <see cref="SlantTracker.DriftPpm"/>, but NOT simply <c>_slantTracker?.DriftPpm</c> -- auditor
    /// finding: <see cref="_slantTracker"/> alone is not null in every case the interface documents as
    /// null. <see cref="AbandonInProgressImage"/> (the AVT training-resettle hand-off,
    /// `sstv.cpp`'s equivalent lock-abandon path) nulls <see cref="_mode"/> but deliberately leaves
    /// <see cref="_slantTracker"/> alive (see that method's own doc comment) for up to ~7.1s while a
    /// new AVT training lock resolves -- without the explicit <see cref="_mode"/> check here, a
    /// polling GUI would keep reading the ABANDONED image's stale drift for that whole window, not
    /// null. Reads both fields into locals once, not twice -- see <see cref="SyncOffsetSamples"/>'s
    /// own doc comment for why a cross-thread poll needs that even though this class documents no
    /// general thread-safety guarantee beyond the single-producer-thread contract every other member
    /// already assumes.</summary>
    public double? SlantPpm
    {
        get
        {
            var mode = _mode;
            var tracker = _slantTracker;
            return mode is null ? null : tracker?.DriftPpm;
        }
    }

    /// <summary>See <see cref="ISstvDecoder.SyncOffsetSamples"/>. Deliberately reads
    /// <see cref="_lastLineSyncPeakPosition"/>, NOT <see cref="ComputeAutoSyncPosition"/>'s own live
    /// <see cref="_slantLinePeakPosition"/> input -- that field is a within-line accumulator reset to
    /// 0 at the end of every completed line, so a caller reading it from outside the decode loop
    /// (exactly what a public property getter is) would not reliably observe the value for whichever
    /// line was current when <see cref="PushSamples"/> last returned (see that field's own doc
    /// comment). <see cref="_lastLineSyncPeakPosition"/> is the field already designed for this
    /// exact after-the-fact read -- <see cref="PerformReSync"/>'s own deadband check uses it the same
    /// way.
    ///
    /// Auditor finding: each of <see cref="_mode"/>/<see cref="_slantTracker"/>/
    /// <see cref="_lastLineSyncPeakPosition"/> is read into a local exactly ONCE, not re-read between
    /// the null-check and its use -- a caller on a different thread than whichever one calls
    /// <see cref="PushSamples"/> (the documented intended use: a GUI polling this on a timer) can race
    /// a decode-thread write that nulls one of these fields (<see cref="ResetReSyncState"/> alone runs
    /// at the end of EVERY image) between two separate reads of the same field. An earlier version of
    /// this getter checked <c>_lastLineSyncPeakPosition.HasValue</c> then separately read
    /// <c>_lastLineSyncPeakPosition.Value</c> -- two non-volatile field loads of the same
    /// <see cref="Nullable{T}"/>, which a race between them can turn into a thrown
    /// <see cref="InvalidOperationException"/> instead of a clean null. Reading once into a local and
    /// pattern-matching it removes that window entirely (a snapshot read can still be stale, but never
    /// throws).</summary>
    public int? SyncOffsetSamples
    {
        get
        {
            var mode = _mode;
            var tracker = _slantTracker;
            var lastPeak = _lastLineSyncPeakPosition;
            return mode is null || tracker is null || lastPeak is not double peak
                ? null
                : WrapSyncOffset((int)peak, mode);
        }
    }

    /// <summary>See <see cref="ISstvDecoder.SignalPeakLevel"/>. <see cref="_levelAgc"/> is
    /// decoder-lifetime (constructed once in the ctor, unlike <see cref="_slantTracker"/>'s
    /// per-lock construction), so there is no null/not-ready case to model here -- a single plain
    /// field read of <see cref="LevelAgc.CurMax"/>, which is itself already <c>0.0</c> (not
    /// garbage) before its first <c>Fix()</c> window completes.</summary>
    public double SignalPeakLevel => _levelAgc.CurMax / 32768.0;

    /// <summary>See <see cref="ISstvDecoder.IsLevelOverdriven"/>. Deliberately its own independent
    /// read of <see cref="LevelAgc.CurMax"/>, not derived from <see cref="SignalPeakLevel"/>'s own
    /// already-divided value -- see that property's interface doc comment for why the two can
    /// disagree by one sample generation, an accepted consequence of two separate reads, not a
    /// bug to fix by coupling them.</summary>
    public bool IsLevelOverdriven => _levelAgc.CurMax >= 24578.0;

    /// <summary>See <see cref="ISstvDecoder.AutoSlantEnabled"/>. Plain restart-only field readback --
    /// see <see cref="_autoSlantEnabled"/>'s own doc comment for what this gates.</summary>
    public bool AutoSlantEnabled => _autoSlantEnabled;

    /// <summary>See <see cref="ISstvDecoder.SyncFrequencyCorrectionHz"/>. Same shape as
    /// <see cref="SlantPpm"/>'s fix above, for the same reason: <see cref="_afcTracker"/>, like
    /// <see cref="_slantTracker"/>, is deliberately NOT nulled by <see cref="AbandonInProgressImage"/>
    /// (see that method's own doc comment) -- so <see cref="_mode"/> must be checked explicitly,
    /// not just the tracker reference, or this would leak an abandoned image's stale correction
    /// for the whole AVT mid-reception training pending window. Fields read into locals once, same
    /// TOCTOU reasoning as <see cref="SyncOffsetSamples"/>.</summary>
    public double? SyncFrequencyCorrectionHz
    {
        get
        {
            var mode = _mode;
            var tracker = _afcTracker;
            return mode is null ? null : tracker?.CorrectionHz;
        }
    }

    public void PushSamples(ReadOnlyMemory<float> samples)
    {
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

        var span = samples.Span;
        for (var i = 0; i < span.Length; i++)
        {
            _rawSamples.Add(span[i]);
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
    }

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
    // already covered by SlantTracker's own construction/Reset(). Called ONLY at a fresh lock
    // (InitializeSlant), deliberately NOT after every slant correction commits -- see
    // ApplySlantTracking's own correctedSampleRate branch for why an earlier draft's "reset on every
    // commit, for consistency with SlantTracker.Reset()" choice was empirically wrong (made Auto Sync
    // untriggerable under exactly the sustained-drift conditions it exists for).
    //
    // Code-level review correction: an earlier version of this comment justified NOT resetting by
    // claiming legacy's own InitAutoStop-after-commit call is "proven dead code without the not-built
    // RX-buffer-replay feature" -- that was WRONG. sys.m_UseRxBuff defaults to 1 (Main.cpp:899), and
    // OpenCloseRxBuff allocates m_StgBuf for exactly that value (sstv.cpp:1630-1639), so
    // UpdateSampFreq's own `dp->m_StgBuf != NULL` gate (Main.cpp:5597) IS satisfied by default, and
    // InitAutoStop DOES run after every commit in real legacy. The actual reason not to copy that
    // call: legacy resets and then immediately REPLAYS every buffered line back through
    // DrawSSTV -> AutoStopJob (Main.cpp:5603-5612, with m_ASDis=1 at :5601 suppressing triggers only
    // during that replay, cleared at :5627) -- so legacy's net Auto Sync state is rebuilt, not lost.
    // RX buffer subsystem Phase 6b confirms this reasoning still holds: replay DOES re-feed
    // TryAutoSync/SlantTracker (the new `isReplay`-suppressed path, see TryAutoSync's own doc comment
    // and SlantTracker.ProcessLineSuppressed), so state is rebuilt via replay, not lost -- the "no
    // replay mechanism yet" premise above no longer applies as of this decision. Phase 6c's still-
    // unbuilt replay engine is DESIGNED to call this method (plus SlantTracker.ResetBaseline())
    // immediately before every replay pass, mirroring legacy's own UpdateSampFreq/RedrawSSTV shape
    // (reset-then-rebuild, Main.cpp:5601-5627) -- not yet wired as of Phase 6b; do not assume this call
    // exists until Phase 6c lands. For RxBufferMode.Off specifically, this was ALREADY provably exact,
    // not merely a least-bad approximation: legacy's own `UpdateSampFreq` gates its entire
    // InitAutoStop-then-replay block on `(dp->m_StgBuf != NULL) || WaveStg.IsOpen()` (Main.cpp:5597),
    // which is false whenever sys.m_UseRxBuff==0 -- so under Off, legacy performs NO reset and NO
    // replay either, meaning this port's no-reset-here (post-commit) behavior already matches legacy
    // exactly for Off.
    private void ResetAutoSyncDetectionState()
    {
        Array.Clear(_autoSyncPositionHistory);
        _autoSyncObservationCount = 0;
        _autoSyncReferencePosition = null;
        RecomputeAutoSyncThresholds();
        // m_AutoStopCnt = 0 (Main.cpp:3804) -- mirrors the fresh-lock InitAutoStop call only, not the
        // second one UpdateSampFreq makes after every sample-rate/slant commit (Main.cpp:5597-5627),
        // same already-accepted reasoning as this method's own doc comment above: legacy rebuilds state
        // via a buffered-line replay after that second call, this port has no replay mechanism, so not
        // resetting there is closer to legacy's real net effect than resetting-without-rebuilding.
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

    // Force-mode (legacy's RX quick-mode-button click, Main.cpp:6096-6122 -> CSSTVDEM::Start(mode,
    // TRUE), sstv.cpp:1749-1767 -> Start(void), sstv.cpp:1717-1747). Reuses the exact same
    // Commit()/_pendingAnchorCorrectionMode/TryResolveSyncAnchorCorrection/
    // FinalizeAnchorAndStartDecoding pipeline VIS auto-detect itself uses -- legacy-faithful, not an
    // invented shortcut: legacy's own Start() is shared between the VIS-auto path (sstv.cpp:1902-1903)
    // and the force-mode path.
    private void PerformForceMode(SstvModeDefinition mode)
    {
        // Captured BEFORE anything below mutates them. previousMode: DecodeRestarted must report the
        // OLD mode, matching every other restart call site's convention. hadPendingAnchor: whether
        // previousMode ever actually reached ModeDetected -- Commit() fires ModeDetected immediately
        // only for AVT (FinalizeAnchorAndStartDecoding called inline); every other mode defers it
        // until TryResolveSyncAnchorCorrection succeeds, via _pendingAnchorCorrectionMode. A mode still
        // sitting in that field was never announced, so DecodeRestarted must not fire for it either --
        // see this method's own DecodeRestarted call below.
        var previousMode = _mode;
        var hadPendingAnchor = _pendingAnchorCorrectionMode is not null;

        // Aborts any in-progress AVT training. Legacy basis: Start(void) unconditionally lands on
        // m_SyncMode=0 (sstv.cpp:1744), and AVT training lives entirely in m_SyncMode states 4-8 (see
        // TryResolveAvtTraining's own doc comment for that state list) -- a forced start unambiguously
        // aborts in-flight training. Matches EndOfImage's own AVT cleanup exactly (same three fields).
        // Deliberately done HERE, not inside AbandonInProgressImage() -- the S7 AVT hand-off call site
        // relies on _avtTrainingPending surviving through that method (see its own doc comment).
        _avtTrainingPending = false;
        _avtTrainingLock = null;
        _avtPllDemodulator = null;

        // Also clears any OTHER mode's still-unresolved anchor correction. _pendingAnchorCorrectionMode
        // is otherwise cleared in exactly one place (TryResolveSyncAnchorCorrection's own success path)
        // -- every existing caller provably can't reach Commit() while it's already set, an invariant
        // ForceMode is the first to break. Left stale, forcing (say) AVT while a different mode's
        // anchor correction is still pending would leave that stale mode's entry behind: the next
        // TryProcessBuffer call would block AVT decoding until the stale mode's own 3-4-line window
        // fills, then resolve it against AVT's _lineDecoder/_consumedSamples and fire a spurious second
        // ModeDetected for a mode that isn't _mode anymore. hadPendingAnchor above is captured before
        // this clear, so the DecodeRestarted gate below still sees the pre-clear value.
        _pendingAnchorCorrectionMode = null;

        // Anchor at TotalSamplesReceived, NOT _consumedSamples -- pre-lock, _consumedSamples is a
        // frozen header-search start that TrimBuffers stops protecting once _fixedWindowExhausted is
        // set, so on a decoder idle long enough it can sit behind _bufferBase; committing there would
        // read already-trimmed-away buffer and throw on this call's thread. TotalSamplesReceived is
        // always within TrimBuffers' retained window in both the pre-lock and already-locked cases (see
        // TrimBuffers' own watermark comments) and matches legacy's actual reset target -- Start(void)
        // zeroes m_wBase/m_wPage/m_rPage/m_rBase (sstv.cpp:1726-1730), "begin buffering from now", not
        // the m_wBgn=2 buffered-lines-gate flag.
        Commit(mode, TotalSamplesReceived);

        // See this method's own top comment for why this is gated on !hadPendingAnchor: firing
        // DecodeRestarted for a mode that never reached ModeDetected would violate the event's own
        // documented contract (a caller that allocates on ModeDetected and discards on DecodeRestarted
        // would discard a buffer it never allocated).
        if (previousMode is not null && !hadPendingAnchor)
        {
            DecodeRestarted?.Invoke(previousMode);
        }
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

    // Legacy's m_Skip drain (sstv.cpp:2271-2274): while m_Skip > 0 it decrements once per INCOMING
    // sample and drops that sample, so a skip larger than one audio callback's worth of samples
    // inherently spans several callbacks. This port advances the READ cursor instead of dropping
    // write-side samples (see PerformReSync), so the drain has to be incremental for the same reason:
    // at the top of a PushSamples call, TotalSamplesReceived - _consumedSamples is normally LESS than
    // one line (exactly why TryProcessBuffer's own per-line guard returns early), while `skip` can be
    // nearly a full line -- applying it in one shot would call AgcSampleAt past the end of the
    // received stream and throw on the audio thread, on the common path.
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
            detector.ProcessSample(AgcSampleAt(_consumedSamples)); // history continuity only; return value deliberately discarded
            _consumedSamples++;
            _idealLineStartSample += 1.0;
            _slantProcessedUpTo = _consumedSamples;
            _rxBufferAnchorSample++; // RX buffer subsystem Phase 6c round-3 fix -- see this field's own doc comment: a skipped sample advances _consumedSamples without ever being staged, so the anchor must advance too or PerformReplay's own resumeDest overshoots by the skip amount

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
            // later phase adds one, would make this reachable. Must be resolved (or the same reachability
            // argument re-verified) before any manual redraw trigger ships, not silently assumed safe.
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

            watermark = Math.Min(watermark, _visLockProcessedUpTo);

            // S8 fix: included here too, unlike _syncBypassProcessedUpTo above -- _narrowFskProcessedUpTo
            // is NOT frozen while locked (TryVisLockStateMachine, this branch's own mid-reception
            // caller, calls TryNarrowFskScan every time it runs, once per decoded line -- see that
            // method's own doc comment), so it keeps advancing here the same way _visLockProcessedUpTo
            // does, not the way the frozen _syncBypassProcessedUpTo does.
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

                lineDecoder.DecodeLine(mode, effectiveSampleRate, _consumedSamples, _nextLine, reader, pixels);
                _idealLineStartSample += _effectiveSamplesPerLine;
                _consumedSamples = nextLineStartSample;

                LineDecoded?.Invoke(new DecodedImageUpdate(_nextLine, new MutableImageSource(mode.ImageWidth, mode.ImageHeight, pixels)));
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
                // PerformReplay -- unlike legacy's -- sacrifices one row per pass by design (see that
                // method's own doc comment), so the unconditional trigger, once actually firing on every
                // default decode, guarantees a small visible defect (a black stripe) in EVERY image, even
                // when no correction was ever needed. Gating on "has a correction actually committed this
                // image" restores legacy's own real "no cost when idle" property, in this port's own way.
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
                // PerformReplay is DESTRUCTIVE (its cursor jump discards >=1 raw sample and sacrifices a
                // row, and it truncates the staging buffer, see its own doc comment), so draining it at
                // a chunk boundary makes the DECODED IMAGE a function of how the caller sliced its
                // PushSamples calls. Confirmed as a real bug, not a theoretical one: a caller that pushes
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
                // just fired (checked below, at :2360 in this same iteration): that block is about to
                // abandon this image, so redrawing its rows is pointless and PerformReplay would run
                // against state EndOfImage is about to discard.
                //
                // RX buffer subsystem Phase 8c: the manual "Correct Slant" request is drained HERE, at
                // the SAME statement position, and BEFORE the automatic _pendingReplayRequested block
                // below -- both for the identical caller-chunk-boundary reasoning above, and so a real
                // commit can subsume/clear _pendingReplayRequested before that block ever checks it
                // (round-2 plan-review finding: both flags can legitimately be set for the same decoded
                // line -- an automatic tracker commit landing on the same line as a manual Correct-Slant
                // convergence -- and running PerformReplay() twice for one line would sacrifice two rows
                // and jump the cursor twice instead of once). `!_slantCorrectionsDisabledForRestOfImage`
                // mirrors the once-per-image latch's own defense-in-depth gate a few lines up (:2392) --
                // that flag marks a real mid-buffer hole left by DrainPendingSkip's skipped samples, and
                // RequestCorrectSlant is a NEW externally-reachable path into PerformReplay that has no
                // other structural protection against firing across that hole. Not gated by
                // SuppressAutomaticReplayForTests -- that flag exists specifically to isolate the
                // AUTOMATIC tracker's own replay behavior for testing; gating the manual path with it
                // would make it impossible to test Correct Slant's own replay in isolation.
                if (_correctSlantRequested)
                {
                    _correctSlantRequested = false;
                    if (!_autoStopTriggered && !_slantCorrectionsDisabledForRestOfImage && TryCorrectSlant())
                    {
                        PerformReplay();
                        _pendingReplayRequested = false; // subsume: this replay already covers what a same-line automatic trigger wanted

                        // Deliberately NOT setting _anyCorrectionCommittedThisImage here (auditor
                        // code-review finding, Phase 8c: flagged as undocumented, not wrong) -- that
                        // flag exists solely to arm the once-per-image origin-re-derivation latch a few
                        // lines up (:2409-2415), whose whole purpose is triggering ONE MORE replay pass
                        // once 16 lines have decoded. This manual commit's own PerformReplay() call
                        // immediately above already re-derived the origin at the corrected rate -- arming
                        // the latch here would only cost a second, unnecessary sacrificed row later in
                        // this same image for no benefit.
                    }
                }

                if (_pendingReplayRequested)
                {
                    _pendingReplayRequested = false;
                    if (!_autoStopTriggered && !SuppressAutomaticReplayForTests)
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
                    DecodeRestarted?.Invoke(mode);
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
                // header at all. Only VisLockStateMachine runs here, not the fixed-window path (which
                // assumes _consumedSamples is a header start, not mid-image) or TrySyncIntervalDetectionStep
                // (m_sint2 is hard-gated behind !m_Sync at every call site in legacy -- case 0's shared
                // guard, sstv.cpp:1899, and its own case-1 guard, sstv.cpp:1953; m_sint3's calls at
                // sstv.cpp:1927-1937 sit inside the same case-0 :1899 guard -- they must not run while
                // locked; round-2-review fix, an earlier version of this citation pointed at m_sint1's
                // own gates, sstv.cpp:1949/1959, by mistake). Bounded to
                // _consumedSamples (the current decode position), NOT the whole buffer -- see
                // TryVisLockStateMachine's own doc comment for the bulk-vs-streaming bug this bound fixes.
                if (_syncRestartEnabled && TryVisLockStateMachine(_consumedSamples))
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
                    DecodeRestarted?.Invoke(mode);
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
    private void EndOfImage(bool applyDeadTime = true)
    {
        var resumeFrom = applyDeadTime
            ? _consumedSamples + (int)Math.Round(0.5 * _sampleRate)
            : _consumedSamples;

        _mode = null;
        _lineDecoder = null;
        _pixels = null;
        _nextLine = 0;
        _bandpassLockedFromSample = int.MaxValue; // Band-1 item 4b -- see field's own doc comment

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
        _syncEnvelopeDetector = null;
        _slantTracker = null;
        ResetReSyncState(); // legacy's Stop()-side m_Skip = 0, sstv.cpp:1789

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
        _idealLineStartSample = resumeFrom; // MUST 4 -- see field's own doc comment

        _agcDeadZoneCatchUpTarget = resumeFrom;
    }

    // Functional-audit fix (chunk D4 round 1): `EndOfImage` itself has no precondition on decode
    // state (every field it touches is unconditionally reassigned, not conditionally read first),
    // so it's safe to call directly on a freshly-constructed decoder -- this internal wrapper (same
    // "internal for direct testability" convention as Rel/FirstLockedBandpassIndex/etc.) lets a test
    // pin the applyDeadTime:true/false VALUE deterministically, instead of relying on Auto Stop's
    // own statistical trigger (which every existing Auto-Stop test already needs "several
    // consecutive qualifying lines" to reach) -- EndOfImageResetTests/AutoStopTests use this.
    internal void EndOfImageForTests(bool applyDeadTime) => EndOfImage(applyDeadTime);

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
                StationIdDecoded?.Invoke(new FskStationIdDecodedInfo(
                    result.Value.StationIdCallsign, result.Value.StationIdCompactNr, result.Value.StationIdNrText));
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
            Commit(mode, anchor);
            return true;
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
        // prevented. Gating on !_syncBypass1PrimaryHeld reproduces the case-0<->1 boundary for
        // m_sint1. S12 code-level review correction: an earlier version of this comment claimed
        // "m_sint2 already has an equivalent effect for free" -- false; m_sint2 needed (and, as of
        // S12, has) its own explicit gate below, since _syncBypass1PrimaryHeld only tracks the
        // case-0<->1 boundary, not case 2/9/3's real freeze (see m_sint2's own comment for the S12
        // fix). m_sint1's own gate here has the SAME residual gap for case 2/9/3 that m_sint2/
        // m_sint3 had before S12 -- _syncBypass1PrimaryHeld can go false mid-VIS-bit-decode if d12
        // momentarily dips below SLvl, letting !_syncBypass1PrimaryHeld admit a stray TryStart()
        // during real decode -- out of S12's own scope (m_sint1 specifically), not fixed here,
        // left as a known follow-up rather than silently absorbed.
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
        // SyncMax continuation, unlike m_sint2 above), and cases 2/9/3 have none either. Gating
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
        if (d12 > d19 && d12 > _slvl && d12 - d19 >= _slvl)
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
            // not this ordering).
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

    /// <summary>Diagnostic-only: fires the PROVISIONAL, immediate lock anchor the moment
    /// <see cref="Commit"/> sets it -- not <see cref="ModeDetected"/>, which is deferred until
    /// <c>TryResolveSyncAnchorCorrection</c> succeeds, several lines (and several buffered picture
    /// lines) later. Kept as permanent test infrastructure (Band-1 item 4a, pre-Phase-2 audit): this
    /// is what let a real spike measure how far <see cref="BandpassFilteredProcessedUpTo"/> had
    /// already raced ahead of the true lock instant, which is exactly the quantity item 4a's fix
    /// needed to bound -- see <c>BandpassCacheChunkInvarianceTests</c>.</summary>
    internal event Action<int>? LockAnchorCommitted;

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
        _mode = null;
        _lineDecoder = null;
        _pixels = null;
        _nextLine = 0;
        _bandpassLockedFromSample = int.MaxValue; // matches EndOfImage's own "no lock" convention
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

    private void Commit(SstvModeDefinition matched, int lineStartSample)
    {
        AbandonInProgressImage();
        _consumedSamples = Math.Max(0, lineStartSample);
        _idealLineStartSample = _consumedSamples; // MUST 4 -- see field's own doc comment
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

        // Legacy's own equivalent of a negative result is DrawSSTVNormal skipping samples whose
        // phase is still negative (`if (n<0) continue`, Main.cpp:4146) rather than reading earlier
        // samples that were never buffered. Clamping to 0 here is the direct equivalent for a
        // sample-cursor variable that cannot legitimately go negative (it indexes _rawSamples from
        // its own start) -- flagged by review as a real edge case (a correction up to -OFP, ~118
        // samples for Robot 36, applied very early in a short buffer could clamp) but expected to be
        // rare in practice: real transmissions carry several seconds of lead-in before the image.
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
            // a minimal callsign packet, ~600ms, fits inside this method's own ~950ms search ceiling.
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

    private int MsToSamples(double ms) => (int)Math.Round(ms / 1000.0 * _sampleRate);

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
            // same continuous tone race, not a fresh decode (TryDecodeVisDataBits recomputes bits 0-7
            // too, deterministically identical to firstByteBits above; harmless redundancy).
            var allBits = TryDecodeVisDataBits(headerStart, VisHeader.ExtendedDataBitCount);
            if (allBits is null)
            {
                return false;
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

        var isNarrow = mode.NarrowModeCode is not null;
        var (syncTargetHz, bandLowHz, bandHighHz, bandwidthHalfHz) = isNarrow
            ? (1900.0, 1800.0, 1950.0, 128.0)
            : (1200.0, 1000.0, 1325.0, 400.0);
        var (afcBeginMs, afcWidthMs) = SstvModeRegistry.IsFastAfcGroup(mode) ? (1.0, 2.0) : (1.5, 3.0);

        _afcTracker = new AfcTracker(_sampleRate, syncTargetHz, bandLowHz, bandHighHz, afcBeginMs, afcWidthMs, bandwidthHalfHz);
        _lastAppliedAfcRetuneHz = null; // fresh lock cycle -- see ApplyAfcCorrections' retune step
    }

    /// <summary>Test-only: directly invokes <see cref="InitializeAfc"/> (normally only reached via a
    /// real VIS-lock/mode-match during <see cref="PushSamples"/>) so a test can check
    /// <see cref="HasAfcTrackerForTests"/> without needing to drive a full decode. Production code
    /// never calls this.</summary>
    internal void InitializeAfcForTests(SstvModeDefinition mode) => InitializeAfc(mode);

    /// <summary>Test-only visibility into whether AFC is currently active for the mode last passed to
    /// <see cref="InitializeAfc"/> -- production code has no need to read this back.</summary>
    internal bool HasAfcTrackerForTests => _afcTracker is not null;

    /// <summary>Test-only visibility into the demod type this instance was actually constructed
    /// with -- production code has no need to read this back. Same reasoning as
    /// <see cref="RestartableSstvDecoder.InnerStationIdDecodeEnabledForTests"/>: exists so a test can
    /// prove a value actually reached the LIVE inner decoder after a
    /// <see cref="RestartableSstvDecoder"/> rebuild, not just that the wrapper still remembers what
    /// it was told.</summary>
    internal DemodType DemodTypeForTests => _demodType;

    /// <summary>Test-only visibility into the RX BPF preset this instance was actually constructed
    /// with -- production code has no need to read this back (and <see cref="_searchBandpassFilter"/>
    /// itself can't answer this, since it's null for <see cref="RxBpfPreset.Off"/>). Same reasoning as
    /// <see cref="DemodTypeForTests"/> above.</summary>
    internal RxBpfPreset RxBpfPresetForTests => _rxBpfPreset;

    /// <summary>Test-only visibility into the RX buffer mode this instance was actually constructed
    /// with -- production code has no need to read this back (nothing external needs to know the mode;
    /// <see cref="_rxBufferMode"/> is read internally by TryAutoSync/TryResolveSyncAnchorCorrection).
    /// Same reasoning as <see cref="DemodTypeForTests"/>/<see cref="RxBpfPresetForTests"/> above.</summary>
    internal RxBufferMode RxBufferModeForTests => _rxBufferMode;

    /// <summary>Test-only visibility into the RX buffer subsystem's own staging buffer -- null only
    /// for <see cref="RxBufferMode.Off"/>; non-null for <see cref="RxBufferMode.On"/>
    /// (<see cref="RxLineStagingBuffer"/>, RAM) and, as of this Phase 7 sub-piece,
    /// <see cref="RxBufferMode.Extended"/> too (<c>RxDiskLineStagingBuffer</c>, disk-backed -- see
    /// <see cref="_rxLineStagingBuffer"/>'s own doc comment). Declared type is the interface (an
    /// earlier Phase 7 sub-piece's own extraction) -- every real call site in the shipped Phase 3-6
    /// tests only ever touches <c>Count</c>/<c>LineCount</c> against this property, both interface
    /// members, so this kept compiling unchanged through that extraction.</summary>
    internal IRxLineStagingBuffer? RxLineStagingBufferForTests => _rxLineStagingBuffer;

    /// <summary>Test-only visibility into the output-row cursor (<see cref="_nextLine"/>, in BITMAP
    /// ROWS -- 2x transmission-line count for paired-channel modes, see <see cref="PerformReplay"/>'s
    /// own reconciliation doc comment). RX buffer subsystem Phase 6c: lets a test verify
    /// <see cref="PerformReplay"/>'s own reconciliation independently of trusting its internals.</summary>
    internal int NextLineForTests => _nextLine;

    /// <summary>Test-only visibility into the signed replay origin <see cref="PerformReplay"/> most
    /// recently computed (<see cref="ReplayOriginCalculator.ComputeOrigin"/>'s own return value) -- null
    /// until the first replay pass runs. RX buffer subsystem Phase 6c.</summary>
    internal int? LastReplayOriginForTests => _lastReplayOriginForTests;

    /// <summary>Test-only visibility into <see cref="_rxBufferAnchorSample"/> -- RX buffer subsystem
    /// Phase 6c round-4: lets a test confirm the invariant that field's own doc comment states
    /// (<c>_consumedSamples - _rxBufferAnchorSample == staged + in-flight</c>) directly, e.g. that it
    /// equals <see cref="ConsumedSamplesForTests"/> immediately after a <see cref="PerformReplay"/>
    /// truncation (staged + in-flight == 0 right then).</summary>
    internal int RxBufferAnchorSampleForTests => _rxBufferAnchorSample;

    /// <summary>Test-only visibility into <see cref="_rxBufferBaseTransmissionLine"/> -- RX buffer
    /// subsystem Phase 6c round-4: lets a test confirm it tracks <see cref="NextLineForTests"/> (divided
    /// by <c>RowsPerTransmissionLine</c>) after a <see cref="PerformReplay"/> truncation, the invariant
    /// that keeps a SECOND replay pass's own row loop stamping pixels into the correct image rows.</summary>
    internal int RxBufferBaseTransmissionLineForTests => _rxBufferBaseTransmissionLine;

    /// <summary>Test-only visibility into the Auto-Slant sync-envelope detector -- the one AFC
    /// retunes (ultracode audit finding #1). Null until <see cref="InitializeSlant"/> runs for a
    /// non-AVT mode.</summary>
    internal SyncEnvelopeDetector? SyncEnvelopeDetectorForTests => _syncEnvelopeDetector;

    /// <summary>Test-only visibility into one of the seven VIS-time tone detectors AFC must NOT
    /// retune (ultracode audit finding #1's scope correction).</summary>
    internal SyncEnvelopeDetector SyncBypass1200DetectorForTests => _syncBypass1200Detector;

    /// <summary>Test-only visibility into the Auto-Slant tracker itself -- needed to distinguish
    /// manual ReSync's two suppression scopes from the outside: the one-line gate
    /// (<see cref="_suppressNextSlantProcessLine"/>) must leave <see cref="SlantTracker.TotalLinesObservedForTests"/>
    /// completely untouched for that one line, while the whole-image gate
    /// (<see cref="_slantCorrectionsDisabledForRestOfImage"/>) still advances it by exactly one per
    /// line via <see cref="SlantTracker.ProcessLineHistoryOnly"/>. Null until <see cref="InitializeSlant"/>
    /// runs for a non-AVT mode.</summary>
    internal SlantTracker? SlantTrackerForTests => _slantTracker;

    /// <summary>Test-only visibility into the decoder-lifetime <see cref="LevelAgc"/> instance --
    /// lets a test drive <see cref="LevelAgc.Do"/>/<see cref="LevelAgc.Fix"/> directly with exact,
    /// known values, then assert against the real <see cref="SignalPeakLevel"/>/
    /// <see cref="IsLevelOverdriven"/> production properties (not a duplicate of their math written
    /// independently in the test, which a code-level audit found couldn't actually discriminate a
    /// wrong divisor or a <c>&gt;</c>-vs-<c>&gt;=</c> threshold bug).</summary>
    internal LevelAgc LevelAgcForTests => _levelAgc;

    /// <summary>Test-only visibility into the within-line sample accumulator Auto Slant advances --
    /// should always satisfy 0 &lt;= this &lt; <see cref="EffectiveSamplesPerLineForTests"/> even
    /// across a rate-change commit (ultracode audit finding #10).</summary>
    internal double SlantIdealSamplesSoFarInLineForTests => _slantIdealSamplesSoFarInLine;

    /// <summary>Test-only visibility into the current (possibly Auto-Slant-corrected) samples-per-line.</summary>
    internal double EffectiveSamplesPerLineForTests => _effectiveSamplesPerLine;

    /// <summary>Test-only visibility into <see cref="_correctSlantRequested"/> -- lets a test prove
    /// the stale-request clear at a fresh lock (<see cref="InitializeSlant"/>) actually ran, rather
    /// than inferring it indirectly from "no replay happened" (which the entry gate's own
    /// cumulative-line-count check would also produce for an unrelated reason on a fresh lock, making
    /// that inference vacuous -- auditor code-review finding, Phase 8c round 1).</summary>
    internal bool CorrectSlantRequestedForTests => _correctSlantRequested;

    /// <summary>Test-only visibility into the current decode read cursor -- manual ReSync
    /// (<see cref="RequestReSync"/>) is the first feature that shifts this outside the normal per-line
    /// loop's own advancement, so tests need to observe it directly.</summary>
    internal int ConsumedSamplesForTests => _consumedSamples;

    /// <summary>Test-only visibility into the captured last-completed-line sync-peak position manual
    /// ReSync reads from -- <see langword="null"/> once a correction has applied (or before any line
    /// has completed since lock). Read this BEFORE the <see cref="PushSamples"/> call that triggers a
    /// pending <see cref="RequestReSync"/> request, not after -- it's nulled as part of applying the
    /// correction.</summary>
    internal double? LastLineSyncPeakPositionForTests => _lastLineSyncPeakPosition;

    /// <summary>Test-only visibility into the same "ofp" (<c>SSTVSET.m_OFP</c>) value
    /// <see cref="PerformReSync"/> computes internally, so tests can hand-derive an expected skip from
    /// <see cref="LastLineSyncPeakPositionForTests"/> without re-deriving that computation a second,
    /// independently-fallible time. <see langword="null"/> before any mode is locked.</summary>
    internal int? SyncPeakOffsetSamplesForTests => _mode is null ? null : ComputeSyncPeakOffsetSamples(_mode);

    /// <summary>Test-only visibility into the in-progress manual-ReSync skip still left to drain --
    /// reaches 0 exactly when <see cref="DrainPendingSkip"/> has fully applied a correction.</summary>
    internal int PendingSkipSamplesForTests => _pendingSkipSamples;

    /// <summary>Test-only: how many times <see cref="TryAutoSync"/> itself has applied a correction
    /// (distinct from a manual <see cref="RequestReSync"/> call, which shares the same underlying
    /// <see cref="ApplySyncCorrection"/> tail but is not counted here) -- the regression check for
    /// round-1 plan-review's own most severe finding (a wrong anchor base causing continuous spurious
    /// auto-resyncs on a good signal) needs to observe THIS specifically staying at 0.</summary>
    internal int AutoSyncTriggerCountForTests => _autoSyncTriggerCountForTests;

    /// <summary>Test-only: the actual <c>(_autoSlantEnabled ? 5 : 2) * _autoSyncBaseMult</c> value
    /// branch 1 last computed and used for its own two threshold comparisons (auditor plan-review
    /// finding, batch 6) -- <see langword="null"/> until branch 1 has evaluated at least once
    /// (requires <c>n &lt; 4</c>, where <c>n</c> is <see cref="CountAutoSyncCluster"/>'s own
    /// "how many of the last 16 readings cluster near the current position" count -- NOT a simple
    /// observation-index warmup counter; a clean, well-synced signal stays clustered (<c>n &gt;= 4</c>)
    /// almost immediately and can revisit <c>n &lt; 4</c> later too, e.g. right after a genuine sync
    /// glitch). Lets a test verify the SELECTED threshold directly, without needing to empirically tune
    /// a real-audio splice scenario that happens to trigger at one threshold value but not the other --
    /// both threshold values move the trigger window in opposite directions simultaneously (a looser
    /// "small step" ceiling but a stricter "large jump" floor, or vice versa), which the existing
    /// splice-based tests in AutoSyncTests.cs were never designed to isolate.</summary>
    internal int? LastBranch1ThresholdForTests => _lastBranch1ThresholdForTests;

    /// <summary>Test-only visibility into Auto Sync's own observation counter (port of
    /// <c>m_AutoStopACnt</c>) -- gates the 8-line warmup and the 14-vs-10 cluster threshold.</summary>
    internal int AutoSyncObservationCountForTests => _autoSyncObservationCount;

    /// <summary>Test-only visibility into Auto Sync's own stable-reference position (port of
    /// <c>m_AutoSyncPos</c>, <see langword="null"/> = legacy's <c>0x7fffffff</c> sentinel).</summary>
    internal int? AutoSyncReferencePositionForTests => _autoSyncReferencePosition;

    /// <summary>Test-only visibility into Auto Sync's own retrigger cooldown (port of
    /// <c>m_AutoSyncDis</c>).</summary>
    internal int AutoSyncCooldownForTests => _autoSyncCooldown;

    /// <summary>Test-only visibility into <see cref="_slantCorrectionsDisabledForRestOfImage"/> (port
    /// of <c>m_AutoSyncCount</c>'s own gate) -- RX buffer subsystem Phase 6d round-2: lets a test
    /// directly confirm a manual ReSync/Auto-Sync trigger actually set this flag, without depending on
    /// indirect symptoms.</summary>
    internal bool SlantCorrectionsDisabledForRestOfImageForTests => _slantCorrectionsDisabledForRestOfImage;

    /// <summary>Test-only: <see cref="ComputeAutoSyncPosition"/>'s own real return value from the last
    /// time <see cref="TryAutoSync"/> ran -- see <see cref="_lastComputedAutoSyncPositionForTests"/>'s
    /// own doc comment for why this is captured internally rather than recomputed from outside.</summary>
    internal int? LastComputedAutoSyncPositionForTests => _lastComputedAutoSyncPositionForTests;

    /// <summary>Test-only visibility into Auto Stop's own counter (port of <c>m_AutoStopCnt</c>).</summary>
    internal int AutoStopCntForTests => _autoStopCnt;

    /// <summary>Test-only: how many times Auto Stop's own trigger has fired.</summary>
    internal int AutoStopTriggerCountForTests => _autoStopTriggerCountForTests;

    /// <summary>Test-only: the envelope-spread value (<c>_slantLineMaxEnvelope - _slantLineMinEnvelope</c>)
    /// last observed by <see cref="TryAutoSync"/>, for pinning the 5000/8192 threshold scale against
    /// this port's own <see cref="SyncEnvelopeDetector"/> output rather than assuming it matches
    /// legacy's raw <c>m_SyncMax - m_SyncMin</c> scale.</summary>
    internal double? LastAutoStopEnvelopeSpreadForTests => _lastAutoStopEnvelopeSpreadForTests;

    /// <summary>Test-only visibility into the currently-locked mode (<see langword="null"/> when
    /// idle/between images) -- <see cref="ForceMode"/> is the first feature whose own test suite
    /// needs to distinguish "idle" from "locked" independently of <see cref="ModeDetected"/> having
    /// fired yet (a non-AVT commit can be locked with a pending, not-yet-announced anchor).</summary>
    internal SstvModeDefinition? ModeForTests => _mode;

    /// <summary>Test-only visibility into whether AVT training is currently in flight --
    /// <see cref="ForceMode"/>'s own teardown is the first production code that needs to abort this
    /// from outside <see cref="TryResolveAvtTraining"/>'s own exit points/<see cref="EndOfImage"/>.</summary>
    internal bool AvtTrainingPendingForTests => _avtTrainingPending;

    /// <summary>Test-only visibility into a still-unresolved, not-yet-<see cref="ModeDetected"/>
    /// anchor-correction commit -- see <see cref="PerformForceMode"/>'s own doc comment for why a
    /// stale entry here is the exact hazard its clear step exists to prevent.</summary>
    internal SstvModeDefinition? PendingAnchorCorrectionModeForTests => _pendingAnchorCorrectionMode;

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
            // `m_afc` itself is legacy's own always-on default (sstv.cpp:1471 -- no separate toggle
            // to model; AVT's exclusion is already handled by _afcTracker staying null, see
            // InitializeAfc). Corrected (ultracode audit finding #4): the `m_CurMax > 16` gate wraps
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
        }
    }

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
            if (_rxLineStagingBuffer is not null)
            {
                _rxBufferLineDemod.Add(DemodulatedFrequencyAt(_slantProcessedUpTo));
                _rxBufferLineSync.Add(envelope);
            }

            var lineCompleted = ProcessSlantTrackingSample(envelope, isReplay: false);

            // RX buffer subsystem Phase 5: flush this now-completed line to the staging buffer, one
            // atomic TryAppendLine call per line (matching legacy's own per-line, not per-sample,
            // buffer-full admission test). Runs regardless of which branch inside
            // ProcessSlantTrackingSample fired (suppressed, history-only, or a real ProcessLine commit)
            // -- legacy's own capture (Main.cpp:4996-5013) happens unconditionally in the general
            // per-line draw loop too, before any Auto-Slant-family branching. A rejected (buffer-full)
            // append is silently dropped, matching RxLineStagingBuffer's own documented "capture simply
            // stops" behavior -- nothing here needs to react to the return value.
            if (lineCompleted && _rxLineStagingBuffer is not null)
            {
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
        var relative = _slantLinePeakPosition - _syncSegmentOffsetSamples;

        // Manual ReSync's capture point (legacy's m_SyncRPos = m_SyncPos at its own line-boundary
        // point, Main.cpp:4193-4194) -- MUST stay here, before the two-flag suppression block
        // below, not moved down next to the _slantLineMaxEnvelope/_slantLinePeakPosition resets.
        // The one-line-suppress branch's own `_lastLineSyncPeakPosition = null;` only has any
        // effect because this capture already ran earlier in this same pass; placing it after
        // that block would silently overwrite the null and make the suppression dead code.
        _lastLineSyncPeakPosition = _slantLinePeakPosition;

        var half = _effectiveSamplesPerLine / 2.0;
        if (relative > half)
        {
            relative -= _effectiveSamplesPerLine;
        }
        else if (relative < -half)
        {
            relative += _effectiveSamplesPerLine;
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
                var correctedSampleRate = _slantTracker!.ProcessLine(relative);
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
                    if (_rxLineStagingBuffer is not null)
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
    /// transmission line at a time, redraws each row from the staged buffer, and fires
    /// <see cref="LineDecoded"/> per redrawn row -- retroactively re-decoding the entire image received
    /// so far at the corrected rate, matching legacy's own `RedrawSampFreq` semantics (see
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
    /// remains available for direct, single-call exercising in tests.
    ///
    /// <b>Round-2 code-review redesign: the live per-line slant-tracking accumulator is reset once, at
    /// the top, and NEVER restored.</b> An earlier version of this method saved a snapshot of
    /// (<see cref="_slantIdealSamplesSoFarInLine"/>, <see cref="_slantLineEnvelopeSeeded"/>,
    /// <see cref="_slantLineMaxEnvelope"/>/<see cref="_slantLineMinEnvelope"/>/
    /// <see cref="_slantLinePeakPosition"/>, <see cref="_lastLineSyncPeakPosition"/>,
    /// <see cref="_suppressNextSlantProcessLine"/>) before replay's own walk and restored it afterward,
    /// reasoning that the live decode's own in-flight line measurement needed to survive untouched.
    /// That reasoning no longer applies now that the skip-forward re-anchor below (see this method's
    /// own "sample-cursor re-anchor" paragraph) ALWAYS moves <see cref="_consumedSamples"/> to a brand
    /// new destination-coordinate line boundary once this method returns -- the pre-replay snapshot
    /// would describe a line the live decode is no longer measuring towards. Reset to a clean slate
    /// once (so replay's own walk isn't biased by whatever partial live measurement preceded it,
    /// including resolving the auditor's own 6b round-1 off-scope note: <c>_suppressNextSlantProcessLine</c>
    /// is reset to <see langword="false"/> here, so a stale live-side skip-suppression flag can never
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
    /// <b>Round-1/round-2 code-review fix: the live sample cursor
    /// (<see cref="_consumedSamples"/>/<see cref="_idealLineStartSample"/>/<see cref="_slantProcessedUpTo"/>),
    /// not just <see cref="_nextLine"/>, is re-anchored after the replay loop.</b> Round-1's own first
    /// attempt at this fix (setting `_idealLineStartSample = _consumedSamples`) was itself wrong --
    /// round-2 code review found `_consumedSamples` is BY INVARIANT always within 0.5 samples of
    /// `_idealLineStartSample` already (they're re-synced every line, see <see cref="TryProcessBuffer"/>),
    /// so that assignment was a sub-sample no-op that left the real defect -- a permanent
    /// `origin`-derived phase offset applied to EVERY row decoded for the rest of the reception, not
    /// just the first one -- completely unfixed. The real fix: compute where the LIVE cursor actually
    /// sits in DESTINATION-coordinate terms (`resumeDest = origin + (_consumedSamples -
    /// <see cref="_rxBufferAnchorSample"/>)` -- see that field's own doc comment for why the anchor must
    /// be tracked explicitly, not derived from the staged sample count, which can silently lag once the
    /// staging buffer fills), find which row that falls in (`resumeLine`), and jump the live cursor
    /// FORWARD to that row's own clean destination-coordinate start -- mirroring
    /// <see cref="DrainPendingSkip"/>'s own established precedent of moving
    /// `_consumedSamples`/`_idealLineStartSample`/`_slantProcessedUpTo` together for an explicit,
    /// non-incremental cursor jump. The accepted cost, matching this project's own precedent for this
    /// CLASS of divergence (see `ApplySlantTracking`'s own "ultracode audit finding #10" boundary-jump
    /// handling: this port has no equivalent of legacy's retroactive whole-image re-decode from raw
    /// audio): row `resumeLine` itself is never drawn by this call (not by the replay loop above, which
    /// stopped before it because it wasn't fully staged yet, and not by this jump, which skips past its
    /// raw samples entirely) -- a single, bounded, visible gap, not a growing one.
    ///
    /// <b>Round-4 code-review blocker fix: the jump also TRUNCATES the staging buffer</b> (see this
    /// method's own tail, and <see cref="_rxBufferBaseTransmissionLine"/>'s own doc comment) -- the
    /// jumped-past raw samples are never staged, and a flat, gap-unaware buffer left untruncated would
    /// let a SECOND replay pass read straight across that hole as if it were continuous audio, silently
    /// misaligning every row drawn after it (round-4's own two-pass trace caught this; round-3's
    /// additive-only anchor fix kept the sample COUNT bookkeeping correct but not the buffer's own
    /// physical contiguity). The cost stated in the paragraph above is therefore per-PASS, not
    /// per-reception: each replay pass can only retroactively correct rows staged SINCE the previous
    /// pass, not the whole reception from the start -- a real, bounded, and now-documented divergence
    /// from legacy's own whole-buffer re-decode (`UpdateSampFreq`, `Main.cpp:5603-5612`).
    ///
    /// <b>Investigated as a possible Robot-family chroma-bleed bug (2026-08-14), closed as NOT a bug</b>:
    /// <see cref="RobotScanlineDecoder"/> (confirmed the only <see cref="IScanlineDecoder"/>
    /// implementation with cross-line instance state, per the whole-subsystem review's own sweep of all
    /// five -- <see cref="YCbCrSequentialScanlineDecoder"/>, <see cref="YCbCrLinePairedScanlineDecoder"/>,
    /// <see cref="RgbSequentialScanlineDecoder"/>, and <see cref="MonoAveragedPairedScanlineDecoder"/> all
    /// allocate fresh per-call state) caches the PREVIOUS line's other chroma channel across
    /// `DecodeLine` calls, matching legacy's own `m_D36[2][320]` cross-line state -- Robot 36's real,
    /// by-design vertical chroma subsampling (one channel scanned per row, the other borrowed from the
    /// row before it), not a port defect. An earlier pass here wrongly concluded this method's "sacrifice
    /// one row per pass, and sometimes re-decode an already-decoded row" design corrupts MULTIPLE rows
    /// per replay pass, based on a two-pass reproduction that turned out to be confounded: the test image
    /// (`CreateRowIdentityTestImage`, deliberately adjacent-rows-differ-wildly to stress OTHER
    /// row-misalignment bugs) is a pathological, invalid fidelity target for Robot 36 specifically --
    /// its own inherent per-row chroma-subsampling error against that image (large, by design) was
    /// mistaken for replay-caused corruption. Empirically closed via an auditor-derived arithmetic model
    /// (predicting each row's delta from "own channel + neighbor's other channel," matching measured
    /// values to within ~3 units) AND a direct no-replay control (`RxBufferMode.Off`, same image, same
    /// scenario) that reproduced the SAME per-row deltas with zero replay activity at all. The only real,
    /// replay-attributable artifact is a single stale seed row at each redraw window's start -- smaller
    /// than the mode's own inherent per-row error on this stress image, and legacy-equivalent: legacy's
    /// `UpdateSampFreq` (`Main.cpp:5603-5612`) never resets `m_D36` either and walks staged lines in the
    /// same contiguous order, so legacy has the identical one-row seed artifact (always at image row 0,
    /// since legacy's staging buffer is never truncated -- this port's own truncate-on-jump divergence
    /// means the port's seed row lands mid-image on passes 2+, a location difference, not a severity
    /// one). Not a Tier-0 item (`spec/14-roadmap.md`) -- a legacy-faithful characteristic, not a bug.
    /// Only Robot 36 uses this decoder; Robot 72 (`ColorEncoding.YCbCrSequential`,
    /// <see cref="YCbCrSequentialScanlineDecoder"/>) is stateless and entirely unaffected -- an earlier
    /// "Robot36/Robot72" reachability claim here was wrong, corrected.
    ///
    /// The genuinely separate, real, mode-independent divergence from legacy is the sacrificed row
    /// itself (this method's own doc comment above) -- legacy never loses a row on replay, this port's
    /// per-line `DecodeLine` granularity does, for every mode, not a Robot-specific concern. That is the
    /// one piece of this area that would be real follow-up work if ever prioritized, tracked as a
    /// pre-existing, already-documented, already-accepted tradeoff -- not new scope from this
    /// investigation.</summary>
    private void PerformReplay()
    {
        if (_rxLineStagingBuffer is null || _mode is null || _slantTracker is null || _lineDecoder is null || _pixels is null)
        {
            return; // not in a state replay applies to (AVT/pre-lock/RxBufferMode.Off -- matches ApplySlantTracking's own guard; code-review fix: Extended DOES replay as of Phase 7's decoder-wiring sub-piece, via this same is-null gate)
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
            return;
        }

        var stagedSampleCount = stagingBuffer.Count;
        if (stagedSampleCount == 0)
        {
            return; // nothing staged yet
        }

        // Reset-then-rebuild, immediately before every replay pass (Main.cpp:5600ish's InitAutoStop
        // call via UpdateSampFreq, before the replay loop) -- both Auto Sync's own detection window and
        // Auto Slant's own baseline, matching legacy exactly (see ResetAutoSyncDetectionState's and
        // SlantTracker.ResetBaseline's own doc comments).
        //
        // Round-1 code-review fix (real regression, not a nit): legacy's own InitAutoStop-then-replay
        // shape (Main.cpp:5600-5612) always rebuilds Auto Sync's own observation counter/history to the
        // FULL running staged-line count, because legacy's staging buffer is NEVER truncated
        // (`m_wStgLine` is cumulative from lock). This port's own 6c truncate-on-jump divergence (see
        // this method's own tail) means every pass AFTER THE FIRST can only re-feed lines staged SINCE
        // the previous pass -- resetting the full observation window on every pass, when only a handful
        // of lines exist to refill it, permanently starves TryAutoSync's own `_autoSyncObservationCount
        // >= 8` gate and Auto Stop's own `_autoStopCnt >= 8` gate for the rest of the image (both
        // effectively disabled by Auto Slant once corrections recur every few lines -- exactly the
        // sustained-drift scenario replay exists to help with). Only the FIRST pass of an image (staged
        // index 0 still IS image row 0, i.e. _rxBufferBaseTransmissionLine == 0 -- no truncation has
        // happened yet) gets the full reset; every subsequent pass only re-derives the mult/diff
        // thresholds against the just-corrected stride, leaving the observation history/counters to
        // keep accumulating across passes instead of restarting from zero. A real, documented divergence
        // from legacy (which has no truncation to create this problem in the first place), first
        // surfaced by AutoSyncTests.ManualReSync_ResetsAutoSyncObservationCount_ButNotViaAutoSyncItself
        // once Phase 6d made replay fire automatically.
        if (_rxBufferBaseTransmissionLine == 0)
        {
            ResetAutoSyncDetectionState();
        }
        else
        {
            RecomputeAutoSyncThresholds();
        }

        _slantTracker.ResetBaseline();

        // Clean slate for replay's own per-sample walk -- see this method's own doc comment for why
        // this is a one-time reset, never restored. _slantIdealSamplesSoFarInLine is NOT included here
        // (unlike an earlier version of this method) -- it needs to be seeded relative to `origin`, not
        // zeroed, computed further below once `origin` is known.
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
            // never reset mid-reception (Main.cpp:5451-5456) -- `stagingBuffer.LineCount` alone is only
            // the count SINCE the last truncation (this method's own jump, see its doc comment), which
            // silently reverts to the `wStgLineCount < 20` branch's 0.30ms constant on every pass after
            // the first, ~4.4 samples of origin error at 44100Hz. Adding the running
            // _rxBufferBaseTransmissionLine offset restores the cumulative count (both are
            // RowsPerTransmissionLine == 1 families -- Martin/SC2 are never paired-channel modes -- so
            // the units already match with no conversion needed).
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
        // unused below. Unlike checkpoint 1, the AutoSync/SlantTracker/_slantLine* resets above
        // (:5296-5316) have ALREADY run by the time this checkpoint can fire -- deliberately not
        // rolled back (see checkpoint 1's own "accepted note" on this trade-off in the plan doc);
        // don't read checkpoint 1's "exactly as if this replay request had never fired" as applying
        // to a bail that happens here instead.
        if (stagingBuffer.HasWriteFailed)
        {
            return;
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

            // Round-4 code-review fix: staged index 0 is NOT always image row 0 -- a prior replay pass
            // may have truncated the staging buffer at its own forward cursor jump (see this method's
            // own tail), in which case staged index 0 corresponds to whatever transmission line
            // _rxBufferBaseTransmissionLine records. Zero for a fresh lock (InitializeSlant), so this is
            // a no-op there.
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
            LineDecoded?.Invoke(new DecodedImageUpdate(bitmapRow, new MutableImageSource(mode.ImageWidth, mode.ImageHeight, pixels)));
        }

        // Sample-cursor re-anchor -- see this method's own doc comment for the full derivation and why
        // round-1's first attempt at this (setting _idealLineStartSample = _consumedSamples, a
        // sub-sample no-op) didn't actually fix anything. resumeDest is the LIVE cursor's own current
        // position, expressed in the SAME destination-coordinate space `origin`/the replay loop above
        // use -- NOT origin+stagedSampleCount, which only reflects how far STAGING has gotten (it can
        // lag the live cursor by up to one not-yet-flushed line's worth of samples).
        var resumeDest = origin + (_consumedSamples - _rxBufferAnchorSample);
        var resumeLine = (int)Math.Floor(resumeDest / correctedLineWidthSamples);
        var nextRowDestStart = (int)Math.Ceiling((resumeLine + 1) * correctedLineWidthSamples);
        var jumpSamples = nextRowDestStart - resumeDest;

        _idealLineStartSample = _consumedSamples + jumpSamples;
        _consumedSamples = (int)Math.Round(_idealLineStartSample);
        // DrainPendingSkip's own established precedent: never let ApplySlantTracking's normal per-sample
        // catch-up loop re-walk the skipped span (it would feed those raw samples through the LIVE,
        // non-suppressed path a second time, reopening exactly the kind of double-count this port's
        // other cursor-jump call sites already guard against).
        _slantProcessedUpTo = _consumedSamples;
        // Round-4 code-review self-caught bug (found via the two-pass test's own diagnostic output, not
        // by auditor review -- verified by tracing a real gap2=-10 failure back to its cause): `origin`/
        // `resumeDest`/`resumeLine` are ALL computed relative to the CURRENT (possibly-truncated)
        // staging buffer's own LOCAL index 0 -- see ReplayOriginCalculator.ComputeOrigin's own contract,
        // which is phase-agnostic and knows nothing about image row numbering. `resumeLine + 1` is
        // therefore a LOCAL transmission-line count, not an absolute image row. The row-drawing loop
        // above already converts local -> absolute correctly (`bitmapRow = (_rxBufferBaseTransmissionLine
        // + transmissionLineIndex) * RPTL`) -- this reconciliation must apply the SAME conversion, using
        // the OLD (pre-this-pass) base, captured before it's overwritten below.
        var previousBaseTransmissionLine = _rxBufferBaseTransmissionLine;
        var resumeRowTransmissionLine = previousBaseTransmissionLine + Math.Max(0, resumeLine + 1);
        _nextLine = resumeRowTransmissionLine * lineDecoder.RowsPerTransmissionLine;

        // Round-3 code-review blocker fix: re-seed the accumulator with the CORRECT leftover phase at
        // the new destination-coordinate boundary the cursor just jumped to, not left at wherever
        // replay's own continuous walk (above) happened to end. `nextRowDestStart` is a `Math.Ceiling`
        // result, so this residue is always in [0, 1) -- the same order of magnitude as the ordinary
        // per-line fractional carry MUST-4 already preserves elsewhere in this class, not a new kind of
        // imprecision. Leaving the walk's own end-state here instead (an earlier version of this fix
        // did) put the NEXT live-decoded line's own boundary out of phase by up to a full stride, since
        // the walk's own sample count essentially never lands exactly on this jump's own chosen boundary
        // once a real stride correction has occurred -- exactly the case replay exists for.
        _slantIdealSamplesSoFarInLine = nextRowDestStart - ((resumeLine + 1) * correctedLineWidthSamples);
        _slantLineEnvelopeSeeded = false;
        _slantLineMaxEnvelope = double.NegativeInfinity;
        _slantLineMinEnvelope = double.PositiveInfinity;
        _slantLinePeakPosition = 0;

        // Round-4 code-review BLOCKER fix, superseding round-3's `_rxBufferAnchorSample += jumpSamples`:
        // that fix kept the COUNT invariant (_consumedSamples - _rxBufferAnchorSample == staged +
        // in-flight) true across the jump, but the jump ALSO leaves the staged stream PHYSICALLY
        // discontinuous -- `jumpSamples` raw samples (always >= 1) are consumed and never staged, while
        // RxLineStagingBuffer is a single flat, contiguous list with no gap marker. A SECOND replay pass
        // (which Phase 6d will trigger routinely, once per Auto-Slant commit -- not a rare scenario)
        // would read straight across that splice as if it were continuous audio, misaligning every row
        // drawn after it by up to a full line -- confirmed by round-4's own two-pass trace, a genuinely
        // new defect beyond the count-invariant one round-3 fixed.
        //
        // Truncating the staging buffer here is the fix: the cursor jump lands exactly on
        // `nextRowDestStart`, a destination-ROW boundary, so the buffer restarts both gap-free AND
        // row-aligned -- ReplayOriginCalculator.ComputeOrigin's own fold is phase-agnostic (origin is
        // defined relative to staged index 0, whatever raw sample that happens to be), so nothing there
        // needs to change. The in-flight per-line capture accumulators (_rxBufferLineDemod/
        // _rxBufferLineSync) must be cleared too -- left alone, they hold PRE-jump samples that
        // ApplySlantTracking's own capture-flush hook would otherwise splice onto POST-jump ones and
        // flush as a single over-long "line", corrupting _lineBoundaries as well as the sample stream
        // itself. _rxBufferAnchorSample is DIRECTLY re-anchored (not incremented) because the buffer is
        // empty again -- "local index 0 is this raw sample" is true once more, the exact same identity
        // InitializeSlant establishes at a fresh lock. _rxBufferBaseTransmissionLine records WHERE in the
        // image that fresh "index 0" now sits, so the row loop above stays correct on every future pass
        // (see that field's own doc comment).
        //
        // Accepted cost, and a real divergence from legacy (which re-decodes its WHOLE buffer on every
        // UpdateSampFreq, Main.cpp:5603-5612): each replay pass can only re-correct rows staged since the
        // PREVIOUS pass, not the whole reception. Bounded and visible (a documented scope limit), unlike
        // the silent cross-splice misalignment it replaces. A replay pass triggered before ANY new line
        // has staged since the last truncation hits this method's own `stagedSampleCount == 0` early
        // return -- correctly a complete no-op (no bookkeeping reset, no re-anchor), since the cursor is
        // already row-aligned from the previous pass and `origin` is undefined against an empty buffer.
        stagingBuffer.Clear();
        _rxBufferLineDemod.Clear();
        _rxBufferLineSync.Clear();
        _rxBufferAnchorSample = _consumedSamples;
        _rxBufferBaseTransmissionLine = resumeRowTransmissionLine;
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
    /// line count &gt;= 16 (NOT <see cref="IRxLineStagingBuffer.LineCount"/> alone -- that resets on
    /// every replay truncation, unlike legacy's own never-truncated `m_wStgLine`; add
    /// <see cref="_rxBufferBaseTransmissionLine"/> back, same as <see cref="PerformReplay"/>'s own
    /// origin calculation does), mode is not AVT, and (RAM mode only) there's headroom for one more
    /// line -- <see cref="IRxLineStagingBuffer.HasHeadroomForSamples"/>.
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

        // Auditor code-review finding, Phase 8c round 1: without this, _slantTracker's own evolving
        // _currentSampleRate/_nominalSamplesPerLine stay at their pre-manual values, so the NEXT
        // automatic Auto-Slant commit computes its drift delta against a stale baseline and silently
        // reverts this correction -- see AdoptCorrectedRate's own doc comment for the full legacy
        // citation (Main.cpp:3994-3997 reads the SAME SSTVSET.m_SampFreq/m_TW this search's own
        // SetSampFreq()-equivalent writes). Non-null for every mode this method can ever commit for
        // (the entry gate above already rejects AVT, the only mode InitializeSlant leaves this null
        // for) -- the `?.` is defensive, not an expected-null path.
        _slantTracker?.AdoptCorrectedRate(candidateSampleRate);

        return true;
    }

    /// <summary>Test-only entry point for <see cref="TryCorrectSlant"/> -- lets a test drive the
    /// search directly and inspect its result, without depending on the request/drain plumbing (a
    /// later RX buffer subsystem Phase 8 sub-piece). Mirrors the established
    /// <see cref="PerformReplayForTests"/> precedent (a thin pass-through wrapper around an
    /// otherwise-private method).</summary>
    internal bool TryCorrectSlantForTests() => TryCorrectSlant();

    /// <summary>Test-only entry point for <see cref="InitializeSlant"/> -- lets a test reach a fully
    /// initialized slant/staging-buffer state (in particular, a real, non-zero
    /// <see cref="EffectiveSamplesPerLineForTests"/>) for a mode locked via <see cref="ForceMode"/>,
    /// without needing a real audio decode. <see cref="ForceMode"/> alone is NOT sufficient for this:
    /// its own <c>Commit()</c> sets <c>_mode</c> immediately, but defers <see cref="InitializeSlant"/>
    /// itself to <c>FinalizeAnchorAndStartDecoding</c>, which only runs once
    /// <c>TryResolveSyncAnchorCorrection</c> succeeds against real buffered audio (immediately, only
    /// for AVT) -- a real gap an earlier draft of the Phase 8 test plan assumed away and had to be
    /// corrected against actual behavior, not inferred. Mirrors the established
    /// <see cref="PerformReplayForTests"/> precedent.</summary>
    internal void InitializeSlantForTests(SstvModeDefinition mode) => InitializeSlant(mode);

    /// <summary>Test-only entry point for <see cref="PerformReplay"/> -- lets a test drive a single
    /// replay pass directly and deterministically, without depending on the automatic triggers'
    /// (RX buffer subsystem Phase 6d) own timing. Mirrors the established <c>InitializeAfcForTests</c>
    /// precedent (a thin pass-through wrapper around an otherwise-private method).</summary>
    internal void PerformReplayForTests() => PerformReplay();

    /// <summary>Test-only: when <see langword="true"/>, <see cref="TryProcessBuffer"/>'s own automatic
    /// replay drain (RX buffer subsystem Phase 6d) becomes a no-op (the pending flag is still cleared,
    /// so it never accumulates across pushes -- only the <see cref="PerformReplay"/> call itself is
    /// skipped). For a test that wants to drive replay ONLY via <see cref="PerformReplayForTests"/>, at
    /// controlled moments, without an automatic trigger firing unpredictably during the same
    /// <see cref="PushSamples"/> calls and corrupting the test's own row-count/pixel-content
    /// bookkeeping.</summary>
    internal bool SuppressAutomaticReplayForTests { get; set; }

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
