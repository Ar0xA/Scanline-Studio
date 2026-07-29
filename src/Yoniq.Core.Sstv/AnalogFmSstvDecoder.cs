using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>
/// Generic decoder counterpart to <see cref="AnalogFmSstvEncoder"/>, using a ported
/// <see cref="PllFmDemodulator"/> (see that type's doc comment) run continuously over the incoming
/// sample stream. Once VIS reveals the mode, per-line decoding is delegated to a
/// <see cref="IScanlineDecoder"/> selected via <see cref="ScanlineCodecFactory"/> — the decoder
/// can't know the family upfront the way the encoder does, since VIS detection is itself part of
/// this shared, family-agnostic shell. Scope note (Phase 1): this reconstructs scanlines using the
/// mode's *nominal* timing — it does not independently re-search for each line's sync pulse (the
/// legacy AFC/sync state machine in `CSSTVDEM` is a separate, larger piece of work not yet ported),
/// and does not yet implement the clock-drift/slant correction described in spec/06-sstv-dsp.md.
/// That's fine for the same-process, no-channel-noise round-trip this proves; real captured audio
/// (with clock drift between transmitter and receiver sound cards) needs both of those added
/// before this is usable on the air.
/// </summary>
public sealed class AnalogFmSstvDecoder : ISstvDecoder
{
    // Covers every frequency this decoder needs to track: VIS tones (1100-1900Hz) and per-line
    // sync/porch/separator/luminance (1200-2300Hz). See PllFmDemodulator's doc comment — legacy
    // switches tracking bandwidth between VIS detection and image data; this port uses one fixed
    // range for both, which is simpler and a documented Phase 1 simplification.
    private const double DemodulatorLowHz = 1100;
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
    private readonly List<float> _rawSamples = [];
    private readonly PllFmDemodulator _demodulator;

    private int _consumedSamples;
    private SstvModeDefinition? _mode;
    private IScanlineDecoder? _lineDecoder;
    private Rgb24[]? _pixels;
    private int _nextLine;

    private ZeroCrossingFrequencyCounter? _afcFrequencyCounter;
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
    // a deliberate second copy of what VisLockStateMachine already tracks internally: enforcing
    // "m_sint1 checked before m_sint2/m_sint3, every sample" requires evaluating it inside this same
    // loop, and this loop has no access to VisLockStateMachine's separate instance/cursor (they run
    // over the same raw samples from the same origin, so the two latches necessarily agree sample-
    // for-sample -- documented duplication, not an invented shortcut).
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

    public AnalogFmSstvDecoder(int sampleRate = 11025)
    {
        _sampleRate = sampleRate;
        _demodulator = new PllFmDemodulator(sampleRate, DemodulatorLowHz, DemodulatorHighHz);
        _syncBypass1Tracker = new SyncIntervalTracker(sampleRate, isNarrow: false, SstvModeRegistry.GetSyncIntervalCandidates(sampleRate));
        _syncBypass1200Detector = new SyncEnvelopeDetector(sampleRate, 1200.0);
        _syncBypass1900Detector = new SyncEnvelopeDetector(sampleRate, 1900.0);
        _syncBypassTracker = new SyncIntervalTracker(sampleRate, isNarrow: false, SstvModeRegistry.GetSyncIntervalCandidates(sampleRate));
        _syncBypassFskDetector = new SyncEnvelopeDetector(sampleRate, VisHeader.NarrowSpaceFrequencyHz);
        _syncBypassNarrowTracker = new SyncIntervalTracker(sampleRate, isNarrow: true, SstvModeRegistry.GetSyncIntervalCandidates(sampleRate));
        _visLockStateMachine = new VisLockStateMachine(sampleRate, SLvl, SLvl2);
        _levelAgc = new LevelAgc(sampleRate);
    }

    // sstv.cpp:1834-1839: m_lvl.Do(d); ad = m_lvl.AGC(d); d = clamp(ad*32, +-16384). Scale bridge --
    // see LevelAgc's own doc comment: legacy's d is int16-valued, this port's raw samples are float in
    // [-1.0, 1.0] (spec/05-audio-engine.md:44) -- multiply by 32768.0 before handing to LevelAgc so
    // every constant inside that class stays literally identical to legacy's own. Computed at most
    // once per index regardless of call order between the several independent cursors that read this
    // (TrySyncIntervalDetection's, TryVisLockStateMachine's, ApplySlantTracking's) -- each just asks
    // for whatever index it's currently at; the cache fills forward monotonically the first time any
    // of them reaches a new index.
    private double AgcSampleAt(int index)
    {
        for (; _levelAgcProcessedUpTo <= index; _levelAgcProcessedUpTo++)
        {
            var scaled = _rawSamples[_levelAgcProcessedUpTo] * 32768.0;
            _levelAgc.Do(scaled);
            _levelAgc.Fix();
            var ad = _levelAgc.Agc(scaled) * 32.0;
            _agcSamples.Add(Math.Clamp(ad, -16384.0, 16384.0));
            _agcCurMaxSamples.Add(_levelAgc.CurMax);
        }

        return _agcSamples[index];
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
        return _agcCurMaxSamples[index];
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
            _demodulatedFrequencies.Add(_demodulator.ProcessSample(span[i]));
        }

        TryProcessBuffer();
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

            ApplyAfcCorrections();

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
                if (_demodulatedFrequencies.Count - _consumedSamples < lineSampleCount)
                {
                    return; // waiting for more samples to finish this image -- not done, don't reset
                }

                // mode.LineDurationMs/1000*effectiveSampleRate == _effectiveSamplesPerLine by
                // construction: passing this adjusted rate into DecodeLine scales every per-segment
                // sample calculation inside it proportionally, without IScanlineDecoder needing to know
                // anything about slant correction at all.
                var effectiveSampleRate = (int)Math.Round(_effectiveSamplesPerLine / (mode.LineDurationMs / 1000.0));

                lineDecoder.DecodeLine(mode, effectiveSampleRate, _consumedSamples, _nextLine, SampleFrequencyAt, pixels);
                _consumedSamples += lineSampleCount;

                LineDecoded?.Invoke(new DecodedImageUpdate(_nextLine, new MutableImageSource(mode.ImageWidth, mode.ImageHeight, pixels)));
                _nextLine += lineDecoder.RowsPerTransmissionLine;

                // Now that this line is fully decoded and _consumedSamples reflects it, let slant
                // tracking catch up through exactly this line's raw samples -- never further ahead,
                // and never for a line that hasn't been decoded yet.
                ApplySlantTracking();

                // Piece 6c: legacy's case-0 trigger (sstv.cpp:1946-1950) is ungated -- it keeps
                // running even while m_Sync is true, so a stronger/cleaner new lock found mid-
                // reception aborts and restarts on it (Start() resets m_SyncMode back to 0
                // unconditionally). Checked once per decoded line, not once per TryProcessBuffer
                // call: for a bulk-pushed buffer containing a whole (possibly truncated)
                // transmission followed immediately by a second one, the loop above would otherwise
                // just keep decoding every available sample as if it were more lines of the *first*
                // transmission -- it has no notion of "this content doesn't actually belong to this
                // image" -- and would never return control to notice the second transmission's real
                // header at all. Only VisLockStateMachine runs here, not the fixed-window path (which
                // assumes _consumedSamples is a header start, not mid-image) or TrySyncIntervalDetection
                // (both m_sint2 and m_sint3 are hard-gated behind !m_Sync at every call site in
                // legacy, sstv.cpp:1899/1949/1953/1959 -- they must not run while locked). Bounded to
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

        _afcFrequencyCounter = null;
        _afcTracker = null;
        _syncEnvelopeDetector = null;
        _slantTracker = null;

        _avtTrainingPending = false;
        _avtTrainingLock = null;

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

        var discriminatorEndSampleCount = (int)Math.Round(NarrowDiscriminatorWindowEndMs / 1000.0 * _sampleRate);
        if (_demodulatedFrequencies.Count - _consumedSamples >= discriminatorEndSampleCount)
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

        // m_sint2 runs continuously in real time, racing the VIS/narrow header path above
        // (sstv.cpp:1888-1924 reads d12/d19 on every sample regardless of m_SyncMode) -- tried
        // *after* the header path here, not because legacy orders them that way (it can't: both
        // run per-sample in the same real-time loop), but because this port's header path can see
        // its own fixed-duration window resolve in a single call against a bulk-pushed buffer,
        // while a real m_sint2 needs several consecutive image lines (multiple seconds) of matching
        // peaks to confirm anything. Trying header-decode first is what actually reproduces the real
        // race's outcome for a signal with a valid header (header wins, every time, well before
        // sync-interval matching could ever accumulate enough consecutive peaks) instead of letting
        // a single large PushSamples call hand this detector an unrealistic head start over the
        // whole future stream at once -- the same category of bulk-vs-streaming ordering bug
        // documented on ApplySlantTracking above. It only ever actually resolves anything for a
        // transmission with no valid header to decode at all, which is the only case where it needs
        // to run.
        //
        // VisLockStateMachine is tried next, before the sync-interval bypass: unlike the fixed-window
        // path above (which assumes the header starts exactly at _consumedSamples), it scans forward
        // sample-by-sample and can find a header despite arbitrary leading silence/noise, for any
        // VIS-coded mode -- not just the trusted subset TrySyncIntervalDetection covers. Tried before
        // the bypass detectors since it identifies a mode from actual bit content, not periodicity
        // alone, making it the more reliable of the two remaining fallbacks.
        if (TryVisLockStateMachine(_rawSamples.Count))
        {
            return true;
        }

        return TrySyncIntervalDetection();
    }

    // Bounded by upperBoundSample, NOT always _rawSamples.Count: pre-lock (called from
    // TryDecodeHeader), there's no "current decode position" to bound against, so the caller passes
    // _rawSamples.Count and this scans everything available, same as always. While already locked
    // (piece 6c, called from inside the per-line loop below), the caller passes _consumedSamples --
    // never letting this run ahead into not-yet-decoded content. Without that bound, a single bulk
    // PushSamples call containing a whole transmission followed by a second, genuinely valid one
    // would let this method discover the *real* second header on its very first call (mid-decode of
    // the first transmission's very first line) and "restart" onto it immediately, abandoning a
    // transmission this port had every ability to finish -- the same category of bulk-vs-streaming
    // ordering bug already documented on ApplySlantTracking and the original TrySyncIntervalDetection
    // fix, caught here by this port's own end-to-end test, not by a theoretical review.
    private bool TryVisLockStateMachine(int upperBoundSample)
    {
        var bound = Math.Min(_rawSamples.Count, upperBoundSample);
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
    private bool TrySyncIntervalDetection()
    {
        for (; _syncBypassProcessedUpTo < _rawSamples.Count; _syncBypassProcessedUpTo++)
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
            // allowlist. See _syncBypass1Tracker's own doc comment for why it's fed differently from
            // m_sint2/m_sint3 below.
            var sint1Matched = _syncBypass1Tracker.TryStart();
            if (sint1Matched is not null)
            {
                CommitSyncBypassMatch(sint1Matched, _syncBypass1Tracker.LastPeakPositionSamples);
                _syncBypassProcessedUpTo++;
                return true;
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
                    _syncBypassProcessedUpTo++;
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
                    _syncBypassProcessedUpTo++;
                    return true;
                }
            }

            // sstv.cpp:1946-1950/1958-1972 -- the primary VIS-leader threshold, a sibling statement
            // to the m_sint1/m_sint2/m_sint3 blocks above (not nested inside any of them). This is
            // the ONLY place _syncBypass1Tracker gets new peak data: SyncTrig on the rising edge,
            // SyncMax while held (mirroring VisLockStateMachine's own Search/ConfirmLock condition
            // exactly -- see _syncBypass1Tracker's own doc comment for why this is a deliberate
            // second copy, not a shared instance).
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
        }

        return false;
    }

    private void CommitSyncBypassMatch(SstvModeDefinition matched, double peakPosition)
    {
        var midpointOffsetSamples = SstvModeRegistry.GetSyncSegmentMidpointOffsetMs(matched) / 1000.0 * _sampleRate;
        var lineStart = _syncBypassOriginSample + (int)Math.Round(peakPosition - midpointOffsetSamples);
        Commit(matched, lineStart);
    }

    private void Commit(SstvModeDefinition matched, int lineStartSample)
    {
        _consumedSamples = Math.Max(0, lineStartSample);
        _mode = matched;
        _lineDecoder = ScanlineCodecFactory.CreateDecoder(matched.ColorEncoding);
        _pixels = new Rgb24[matched.ImageWidth * matched.ImageHeight];
        _nextLine = 0;

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
        var totalTransmissionLines = matched.ImageHeight / _lineDecoder.RowsPerTransmissionLine;
        _afcBoundSample = _consumedSamples + (int)Math.Round(totalTransmissionLines * matched.LineDurationMs / 1000.0 * _sampleRate);

        // Piece 6c prerequisite, a real bug caught by this port's own end-to-end test: whichever
        // path found this match, VisLockStateMachine must never re-examine samples already accounted
        // for by the time reception is locked, or its now-continuously-running re-verification scan
        // (see TryProcessBuffer) immediately rediscovers the very header that just committed and
        // fires a spurious mid-reception "restart" against itself. When any path other than
        // TryVisLockStateMachine itself (fixed-window, narrow, AVT) triggered this Commit(),
        // _visLockProcessedUpTo may still be at its initial 0 (never touched, since TryDecodeHeader
        // only falls through to TryVisLockStateMachine when the faster paths fail) -- Math.Max fast-
        // forwards it past the header those paths already resolved. When TryVisLockStateMachine
        // itself triggered this Commit(), Math.Max still *advances* it (not a no-op): re-derived
        // independently by review, walking VisLockStateMachine's own anchor formula against how many
        // samples ProcessSample actually consumed to return a match shows the anchor is always
        // slightly *later* (~15ms/164 samples at 11025Hz, for every candidate mode, normal or
        // extended) than where the state machine itself stopped needing samples -- an earlier version
        // of this comment claimed Math.Max "leaves it alone" here, which was backwards, though
        // harmless (the skipped samples are header tail, never re-examined either way). Always
        // Reset(), even when self-triggered (already resets itself internally on a match) or already
        // fast-forwarded (Reset() only clears logical state, not the origin) -- cheap, and guarantees
        // no stale in-progress bit accumulation survives into the new transmission if a different
        // path pre-empted an in-progress VisLockStateMachine scan.
        _visLockStateMachine.Reset();
        _visLockProcessedUpTo = Math.Max(_visLockProcessedUpTo, _consumedSamples);
        _visLockOriginSample = _visLockProcessedUpTo;

        InitializeAfc(matched);
        InitializeSlant(matched);
        ModeDetected?.Invoke(matched);
    }

    private bool TryDecodeNarrowModeHeader()
    {
        var totalHeaderSampleCount = (int)Math.Round(VisHeader.NarrowHeaderTotalDurationMs / 1000.0 * _sampleRate);
        if (_demodulatedFrequencies.Count - _consumedSamples < totalHeaderSampleCount)
        {
            return false;
        }

        var headerStart = _consumedSamples;
        var idealSamples = (VisHeader.NarrowLeaderDurationMs + VisHeader.NarrowGuardDurationMs + VisHeader.NarrowBitDurationMs)
            / 1000.0 * _sampleRate; // bits start right after leader + guard + start-bit train

        const int totalBits = 6 * 4; // 4 bytes (STX, marker, mode code, checksum), 6 bits each
        var bits = new int[totalBits];
        for (var bitIndex = 0; bitIndex < totalBits; bitIndex++)
        {
            var startSample = headerStart + (int)Math.Round(idealSamples);
            idealSamples += VisHeader.NarrowBitDurationMs / 1000.0 * _sampleRate;
            var endSample = headerStart + (int)Math.Round(idealSamples);

            var avgFreq = AverageFrequencyInWindow(startSample, endSample);
            bits[bitIndex] = avgFreq < NarrowDiscriminatorThresholdHz ? 1 : 0; // closer to 1900Hz (mark) => bit=1
        }

        _consumedSamples = headerStart + totalHeaderSampleCount;

        var stxByte = VisHeader.DecodeRawByte(bits.AsSpan(0, 6));
        var markerByte = VisHeader.DecodeRawByte(bits.AsSpan(6, 6));
        var modeCode = VisHeader.DecodeRawByte(bits.AsSpan(12, 6));
        var checksumByte = VisHeader.DecodeRawByte(bits.AsSpan(18, 6));
        var expectedChecksum = (modeCode ^ VisHeader.NarrowMarkerByte) & 0x3F; // WriteFSK only ever sends 6 bits

        if (stxByte != VisHeader.NarrowStxByte || markerByte != VisHeader.NarrowMarkerByte || checksumByte != expectedChecksum)
        {
            // Invalid packet. As with the unknown-VIS-code case below, a fuller implementation
            // would keep scanning for a valid header instead of giving up — out of scope here.
            return false;
        }

        var mode = SstvModeRegistry.FindByNarrowCode(modeCode);
        if (mode is null)
        {
            return false;
        }

        // Direct port of a real regression caught by independent review: this used to duplicate
        // Commit()'s body inline instead of calling it, which meant _afcBoundSample (added when
        // Commit() gained it, see ApplyAfcCorrections' doc comment) was never assigned for a narrow
        // transmission -- staying at its default 0, silently disabling AFC entirely for every
        // MN/MC mode (Math.Min(_demodulatedFrequencies.Count, 0) == 0, so ApplyAfcCorrections'
        // loop never ran). No existing test caught this: nothing in this suite exercises a
        // mistuned-audio narrow-mode decode end to end. Routing through the same Commit() every
        // other detection path already uses closes this and keeps future Commit()-side fixes from
        // needing to be duplicated a second time here.
        // Direct port of a real regression caught by independent review: this used to duplicate
        // Commit()'s body inline instead of calling it, which meant _afcBoundSample (added when
        // Commit() gained it, see ApplyAfcCorrections' doc comment) was never assigned for a narrow
        // transmission -- staying at its default 0, silently disabling AFC entirely for every
        // MN/MC mode (Math.Min(_demodulatedFrequencies.Count, 0) == 0, so ApplyAfcCorrections'
        // loop never ran). No existing test caught this: nothing in this suite exercises a
        // mistuned-audio narrow-mode decode end to end. Routing through the same Commit() every
        // other detection path already uses closes this and keeps future Commit()-side fixes from
        // needing to be duplicated a second time here.
        // Direct port of a real regression caught by independent review: this used to duplicate
        // Commit()'s body inline instead of calling it, which meant _afcBoundSample (added when
        // Commit() gained it, see ApplyAfcCorrections' doc comment) was never assigned for a narrow
        // transmission -- staying at its default 0, silently disabling AFC entirely for every
        // MN/MC mode (Math.Min(_demodulatedFrequencies.Count, 0) == 0, so ApplyAfcCorrections'
        // loop never ran). No existing test caught this: nothing in this suite exercises a
        // mistuned-audio narrow-mode decode end to end. Routing through the same Commit() every
        // other detection path already uses closes this and keeps future Commit()-side fixes from
        // needing to be duplicated a second time here.
        Commit(mode, _consumedSamples);
        return true;
    }

    private bool TryDecodeVisHeader()
    {
        // Prefix (leader/break/leader/start-bit + first 7 data bits) is the same length whether
        // this is a normal single-byte VIS code or an "extended" MR/MP/ML one (see VisHeader) — we
        // don't know which until those 7 bits are decoded, so read the prefix first, then decide.
        var prefixSampleCount = (int)Math.Round(VisHeader.PrefixDurationMs / 1000.0 * _sampleRate);
        if (_demodulatedFrequencies.Count - _consumedSamples < prefixSampleCount)
        {
            return false;
        }

        var headerStart = _consumedSamples;
        var bitMidpointHz = (VisHeader.Bit1FrequencyHz + VisHeader.Bit0FrequencyHz) / 2;
        var prefixIdealSamples = (VisHeader.LeaderDurationMs + VisHeader.BreakDurationMs + VisHeader.LeaderDurationMs + VisHeader.BitDurationMs)
            / 1000.0 * _sampleRate;

        var firstByteBits = new int[VisHeader.DataBitCount];
        for (var bitIndex = 0; bitIndex < VisHeader.DataBitCount; bitIndex++)
        {
            var startSample = headerStart + (int)Math.Round(prefixIdealSamples);
            prefixIdealSamples += VisHeader.BitDurationMs / 1000.0 * _sampleRate;
            var endSample = headerStart + (int)Math.Round(prefixIdealSamples);

            var avgFreq = AverageFrequencyInWindow(startSample, endSample);
            firstByteBits[bitIndex] = avgFreq < bitMidpointHz ? 1 : 0; // closer to Bit1FrequencyHz (1100) => 1
        }

        var firstByteValue = VisHeader.DecodeVisCode(firstByteBits);
        var isExtended = firstByteValue == VisHeader.ExtendedVisEscapeCode;
        var tailDurationMs = isExtended ? VisHeader.ExtendedTailDurationMs : VisHeader.NormalTailDurationMs;
        var totalHeaderSampleCount = (int)Math.Round((VisHeader.PrefixDurationMs + tailDurationMs) / 1000.0 * _sampleRate);

        if (_demodulatedFrequencies.Count - headerStart < totalHeaderSampleCount)
        {
            return false; // wait for the rest of the header before consuming/deciding
        }

        SstvModeDefinition? mode;
        if (isExtended)
        {
            // 1 leftover bit from the escape byte (its bit 7, unused) + all 8 bits of the real
            // extended-mode byte = 9 more bit-slots before the stop bit.
            var remainingBits = new int[9];
            for (var bitIndex = 0; bitIndex < remainingBits.Length; bitIndex++)
            {
                var startSample = headerStart + (int)Math.Round(prefixIdealSamples);
                prefixIdealSamples += VisHeader.BitDurationMs / 1000.0 * _sampleRate;
                var endSample = headerStart + (int)Math.Round(prefixIdealSamples);

                var avgFreq = AverageFrequencyInWindow(startSample, endSample);
                remainingBits[bitIndex] = avgFreq < bitMidpointHz ? 1 : 0;
            }

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
        if (_demodulatedFrequencies.Count - headerStart < totalHeaderSampleCount + extraSampleCount)
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
        return TryResolveAvtTraining();
    }

    private bool TryResolveAvtTraining()
    {
        while (_avtTrainingProcessedUpTo < _demodulatedFrequencies.Count)
        {
            var completedAt = _avtTrainingLock!.ProcessSample(_demodulatedFrequencies[_avtTrainingProcessedUpTo]);
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
    private void InitializeAfc(SstvModeDefinition mode)
    {
        _afcProcessedUpTo = _consumedSamples;

        if (mode == SstvModeRegistry.Avt)
        {
            _afcFrequencyCounter = null;
            _afcTracker = null;
            return;
        }

        var isNarrow = mode.NarrowModeCode is not null;
        _afcFrequencyCounter = new ZeroCrossingFrequencyCounter(_sampleRate);
        _afcFrequencyCounter.SetWidth(isNarrow);

        var (syncTargetHz, bandLowHz, bandHighHz, bandwidthHalfHz) = isNarrow
            ? (1900.0, 1800.0, 1950.0, 128.0)
            : (1200.0, 1000.0, 1325.0, 400.0);
        var (afcBeginMs, afcWidthMs) = SstvModeRegistry.IsFastAfcGroup(mode) ? (1.0, 2.0) : (1.5, 3.0);

        _afcTracker = new AfcTracker(_sampleRate, syncTargetHz, bandLowHz, bandHighHz, afcBeginMs, afcWidthMs, bandwidthHalfHz);
    }

    // Legacy applies AFC in the same single per-sample pass as the main demod ("if(m_Sync) d +=
    // m_AFCDiff" right after m_pll.Do(...), sstv.cpp:2255-2270). This decoder demodulates samples
    // upfront (PushSamples), before mode detection can know whether/how AFC should apply to them --
    // so this instead corrects the already-demodulated buffer in place, in a separate pass, once the
    // mode (and therefore the AFC parameters) are known. This is a deferred, not an approximated,
    // adaptation: the zero-crossing counter and AFC state machine below still see the exact same raw
    // samples in the exact same order, one at a time, that legacy's own would have -- only the wall-
    // clock timing of *when* that processing happens (relative to VIS decode) differs.
    //
    // Bounded by _afcBoundSample, NOT _demodulatedFrequencies.Count: never correct samples beyond
    // this image's own generous nominal extent -- otherwise a single TryProcessBuffer call (a bulk
    // PushSamples caller in particular) would eagerly correct straight through this image's footer/
    // dead-zone and into a not-yet-detected *next* transmission's audio using a correction tuned to
    // this image's own frequency offset, corrupting it before that transmission's own Commit() even
    // runs -- the same category of bulk-vs-streaming ordering bug already documented on
    // ApplySlantTracking, but for AFC specifically only became reachable once EndOfImage made a
    // second Commit() within one decoder instance possible at all.
    private void ApplyAfcCorrections()
    {
        if (_afcTracker is null)
        {
            return;
        }

        var bound = Math.Min(_demodulatedFrequencies.Count, _afcBoundSample);
        for (; _afcProcessedUpTo < bound; _afcProcessedUpTo++)
        {
            // Piece 7c: sstv.cpp:2258 (case 0/PLL -- the case this method's own doc comment cites as
            // what it models) calls m_fqc.Do(...) *only inside* the `m_lvl.m_CurMax > 16` gate -- if
            // the gate fails, legacy's frequency counter doesn't even see this sample, so this port's
            // ZeroCrossingFrequencyCounter must skip ProcessSample entirely too, not just have its
            // correction discarded afterward (`m_afc` itself is legacy's own always-on default,
            // sstv.cpp:1471 -- no separate toggle to model; AVT's exclusion is already handled by
            // _afcTracker staying null, see InitializeAfc).
            if (AgcCurMaxAt(_afcProcessedUpTo) > 16.0)
            {
                var measuredFrequencyHz = _afcFrequencyCounter!.ProcessSample(_rawSamples[_afcProcessedUpTo]);
                var correctionHz = _afcTracker.ProcessSample(measuredFrequencyHz);
                _demodulatedFrequencies[_afcProcessedUpTo] += correctionHz;
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
    /// endSample), skipping a settling margin at the start for the PLL loop's transient response
    /// after the preceding frequency change.</summary>
    private double AverageFrequencyInWindow(int startSample, int endSample)
    {
        var sampleCount = Math.Max(1, endSample - startSample);
        var settleSamples = sampleCount / 4;

        var from = Math.Clamp(startSample + settleSamples, 0, _demodulatedFrequencies.Count);
        var to = Math.Clamp(endSample, 0, _demodulatedFrequencies.Count);
        if (to <= from)
        {
            from = Math.Clamp(startSample, 0, _demodulatedFrequencies.Count);
            to = Math.Clamp(endSample, 0, _demodulatedFrequencies.Count);
        }

        if (to <= from)
        {
            return 0;
        }

        double sum = 0;
        for (var i = from; i < to; i++)
        {
            sum += _demodulatedFrequencies[i];
        }

        return sum / (to - from);
    }

    /// <summary>Reads the demodulated frequency at a single sample point — the delegate passed to
    /// <see cref="IScanlineDecoder.DecodeLine"/> for per-pixel reads (see that interface's doc
    /// comment for why: legacy's own pixel readout is a single raw sample, not a windowed average,
    /// and this is a direct port of that, not an invented technique). <paramref name="endSample"/>
    /// is accepted but ignored, matching <see cref="IScanlineDecoder"/>'s <c>sampleFrequencyAt</c>
    /// contract — callers pick which endpoint of their computed window to pass depending on whether
    /// they want the pixel's first sample (ordinary scans) or last sample (e.g. Robot's
    /// tone-selector, which legacy re-decides on every sample of its window with no "first wins"
    /// gate, so its real effective reading is whatever the window's last sample decided).</summary>
    private double SampleFrequencyAt(int startSample, int endSample)
    {
        var index = Math.Clamp(startSample, 0, _demodulatedFrequencies.Count - 1);
        return _demodulatedFrequencies[index];
    }

    private sealed class MutableImageSource(int width, int height, Rgb24[] pixels) : IImageSource
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public ReadOnlySpan<Rgb24> GetScanline(int y) => pixels.AsSpan(y * Width, Width);
    }
}
