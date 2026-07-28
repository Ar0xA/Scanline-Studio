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

    private SyncEnvelopeDetector? _syncEnvelopeDetector;
    private SlantTracker? _slantTracker;
    private int _slantProcessedUpTo;
    private double _effectiveSamplesPerLine;
    private double _syncSegmentOffsetSamples;
    private double _slantIdealSamplesSoFarInLine;
    private double _slantLineMaxEnvelope;
    private double _slantLinePeakPosition;

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

    // AVT training-sequence lock (sstv.cpp cases 4-7, see AvtTrainingLockStateMachine's own doc
    // comment) -- once TryDecodeVisHeader identifies AVT from its VIS byte, resolution moves into
    // this multi-call pending phase instead of committing atomically with a fixed-duration skip,
    // since the training lock's completion point is data-dependent, not a fixed duration.
    private bool _avtTrainingPending;
    private AvtTrainingLockStateMachine? _avtTrainingLock;
    private int _avtTrainingOriginSample;
    private int _avtTrainingProcessedUpTo;
    private int _avtTrainingFallbackDeadlineSample;

    public AnalogFmSstvDecoder(int sampleRate = 11025)
    {
        _sampleRate = sampleRate;
        _demodulator = new PllFmDemodulator(sampleRate, DemodulatorLowHz, DemodulatorHighHz);
        _syncBypass1200Detector = new SyncEnvelopeDetector(sampleRate, 1200.0);
        _syncBypass1900Detector = new SyncEnvelopeDetector(sampleRate, 1900.0);
        _syncBypassTracker = new SyncIntervalTracker(sampleRate, isNarrow: false, SstvModeRegistry.GetSyncIntervalCandidates(sampleRate));
        _syncBypassFskDetector = new SyncEnvelopeDetector(sampleRate, VisHeader.NarrowSpaceFrequencyHz);
        _syncBypassNarrowTracker = new SyncIntervalTracker(sampleRate, isNarrow: true, SstvModeRegistry.GetSyncIntervalCandidates(sampleRate));
        _visLockStateMachine = new VisLockStateMachine(sampleRate);
    }

    public event Action<DecodedImageUpdate>? LineDecoded;

    public event Action<SstvModeDefinition>? ModeDetected;

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

    private void TryProcessBuffer()
    {
        if (_mode is null && !TryDecodeHeader())
        {
            return;
        }

        ApplyAfcCorrections();

        var mode = _mode!;
        var lineDecoder = _lineDecoder!;
        var pixels = _pixels!;

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
                break;
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
        }
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
        if (TryVisLockStateMachine())
        {
            return true;
        }

        return TrySyncIntervalDetection();
    }

    private bool TryVisLockStateMachine()
    {
        for (; _visLockProcessedUpTo < _rawSamples.Count; _visLockProcessedUpTo++)
        {
            var result = _visLockStateMachine.ProcessSample(_rawSamples[_visLockProcessedUpTo]);
            if (result is null)
            {
                continue;
            }

            Commit(result.Value.Mode, result.Value.LineStartSample);
            _visLockProcessedUpTo++;
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
    // single-pass structure. Simplification, flagged not silently absorbed: legacy's m_sint2/m_sint3
    // conditions also check absolute amplitude thresholds (m_SLvl/m_SLvl2/m_SLvl3) on top of the
    // relative comparisons used here -- a noise/squelch gate on legacy's internal AGC'd ±16384
    // amplitude scale, which this port's SyncEnvelopeDetector doesn't model (same simplification
    // already documented on AfcTracker's own omitted m_lvl.m_CurMax>16 gate, and on
    // SyncEnvelopeDetector itself). Harmless for a clean synthetic signal; would need real AGC for
    // reliable noise immunity against real captured audio. m_sint1 (VIS-leader-only detection,
    // redundant with the header path below) is deliberately not ported here -- separately scoped,
    // later work.
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
    private bool TrySyncIntervalDetection()
    {
        for (; _syncBypassProcessedUpTo < _rawSamples.Count; _syncBypassProcessedUpTo++)
        {
            var raw = _rawSamples[_syncBypassProcessedUpTo];
            var d12 = _syncBypass1200Detector.ProcessSample(raw);
            var d19 = _syncBypass1900Detector.ProcessSample(raw);
            var dsp = _syncBypassFskDetector.ProcessSample(raw);

            _syncBypassTracker.Increment();
            _syncBypassNarrowTracker.Increment();

            // m_sint2 (sstv.cpp:1899-1911).
            if (d12 > d19)
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
            // called once on the falling edge only, matching legacy's own m_SyncPhase gating.
            if (d19 > d12 && d19 > dsp)
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
        }

        return false;
    }

    private void CommitSyncBypassMatch(SstvModeDefinition matched, double peakPosition)
    {
        var midpointOffsetSamples = SstvModeRegistry.GetSyncSegmentMidpointOffsetMs(matched) / 1000.0 * _sampleRate;
        var lineStart = (int)Math.Round(peakPosition - midpointOffsetSamples);
        Commit(matched, lineStart);
    }

    private void Commit(SstvModeDefinition matched, int lineStartSample)
    {
        _consumedSamples = Math.Max(0, lineStartSample);
        _mode = matched;
        _lineDecoder = ScanlineCodecFactory.CreateDecoder(matched.ColorEncoding);
        _pixels = new Rgb24[matched.ImageWidth * matched.ImageHeight];
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

        _mode = mode;
        _lineDecoder = ScanlineCodecFactory.CreateDecoder(mode.ColorEncoding);
        _pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        InitializeAfc(mode);
        InitializeSlant(mode);
        ModeDetected?.Invoke(mode);
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
    private void ApplyAfcCorrections()
    {
        if (_afcTracker is null)
        {
            return;
        }

        for (; _afcProcessedUpTo < _demodulatedFrequencies.Count; _afcProcessedUpTo++)
        {
            var measuredFrequencyHz = _afcFrequencyCounter!.ProcessSample(_rawSamples[_afcProcessedUpTo]);
            var correctionHz = _afcTracker.ProcessSample(measuredFrequencyHz);
            _demodulatedFrequencies[_afcProcessedUpTo] += correctionHz;
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
            var envelope = _syncEnvelopeDetector!.ProcessSample(_rawSamples[_slantProcessedUpTo]);

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
