using ScanlineStudio.Abstractions.Cw;

namespace ScanlineStudio.Core.Cw;

/// <summary>v1 CW-ID decoder backend (fsk_cwid.md §8.4) -- an adaptive (variable-WPM) classical
/// timing decoder, own code, no license question (see fsk_cwid.md §7/§7.1 for why: deepcw-engine is
/// AGPL-3.0-only, ggmorse/ditdah were evaluated and not adopted). New capability, not a legacy port
/// -- YONIQ never decoded CW on receive, so there is no source function this is verified against;
/// correctness rests on internal consistency and round-trip agreement with the already-verified
/// TX-side <c>ScanlineStudio.Core.Sstv.CwMorseGenerator</c>, cross-checked per CLAUDE.md §4 (a
/// round-trip alone is necessary but not sufficient -- both halves could be wrong the same way,
/// which is why <see cref="MorseAlphabet"/> and <see cref="CwIdCallsignExtractor"/> are each
/// independently isolate-tested first).
///
/// Scope is deliberately narrow: one machine-keyed tone, unknown speed that is constant within one
/// ID, a short (5-20s) window, known text shape ("DE &lt;callsign&gt;"). Known weaknesses, stated up
/// front: hand-keyed or badly weighted CW, QSB inside the window, and two overlapping stations --
/// none of these describe an SSTV CW ID from a station whose image just decoded.</summary>
public sealed class ClassicalCwDecoder : ICwIdDecoder
{
    // §8.4 step 1: the full 100-3000 Hz range CwToneFrequencyHz lets any operator configure --
    // NOT the narrower 400-1200 Hz band an earlier draft copied from DeepCW's own spectrogram band,
    // which has no bearing on a classical decoder searching for an unknown user-configured tone.
    private const double MinToneHz = 100.0;
    private const double MaxToneHz = 3000.0;
    private const double ToneStepHz = 10.0;
    private const int ToneCandidateCount = 5;
    private const double ToneCandidateMinSeparationHz = 50.0;
    private const double ToneCandidateMinPeakToMedianRatio = 2.0;

    private const double EnvelopeBlockMs = 5.0;
    private const int EnvelopeSmoothingBlocks = 3;

    // Peak-relative coarse-gate/separation reference (see DetectKeying's own comment for why this is
    // NOT median-relative) -- 20% of the observed peak is comfortably above a real noise floor for a
    // clean-to-moderately-noisy signal without being so high it clips genuine, weaker marks out of a
    // low-SNR capture.
    private const double CoarseGatePeakFraction = 0.20;
    private const double CoarseGatePadMs = 300.0;
    private const double HysteresisFraction = 0.10;

    // §8.4 step 4: widest span across legacy's own truncating formula (23ms floor at 50 WPM),
    // this port's own non-truncating generator (22.2ms floor at its own 50 WPM max), and textbook
    // PARIS timing (24ms at 50 WPM) -- a fixed-narrower floor would reject this port's OWN encoder
    // output at its own maximum supported speed, the same class of bug that killed fixed-28-WPM.
    private const double MinDotMs = 22.0;
    private const double MaxDotMs = 240.0;
    private const double MinLongShortRatio = 2.2;
    private const double MaxLongShortRatio = 4.0;
    private const int MinMarksForSpeedEstimate = 6;
    private const double ClusterGapMaxOccupancyFraction = 0.15;
    private const double RunningEstimateSmoothingFactor = 0.3;

    // The gap-occupancy check alone rejects marks sitting in the MIDDLE third between the two cluster
    // centres, but a data-dependent run-length distribution (real bug found via the FSK-ID-collision
    // regression test, not hypothetical: a real FskStationIdEncoder "W1AW" packet's own bit-run
    // lengths quantize at 1x/2x/3x/4x its fixed 22ms bit period) can put an outlier just OUTSIDE that
    // middle band while still landing far from its own assigned cluster's centre -- TwoMeans forces
    // every point into one of two buckets regardless of real fit, so "which bucket" alone can't catch
    // this. 35% tolerance (looser than TimingScore's own 25%) accounts for 5ms envelope-block
    // quantization jitter on top of real dit-length variance, verified empirically against this
    // file's own noisy/WPM-range fixtures without any regression.
    private const double MaxClusterMemberRelativeDeviation = 0.35;

    // Deliberately much tighter than ClusterGapMaxOccupancyFraction (0.15) -- a single stray point in
    // a 10-mark window is already 10% of the sample, so 0.15 alone let the FSK-ID collision case (1
    // outlier of 10 marks) through. Genuine machine-keyed CW should have ~zero cluster-relative
    // outliers; this leaves headroom only for occasional noise-driven jitter, not a real second mode.
    private const double MaxClusterOutlierFraction = 0.05;

    // §8.4 step 5's real ideal gap values, matching what WriteCWID actually emits (sstv.cpp:2988-3002):
    // dot after every element, dot*2 after every character, dot*7 for a space with no trailing dot*2
    // -- so a real inter-word gap on the wire is 1+2+7 = 10 dots, not the textbook 7.
    private const double IdealIntraElementDots = 1.0;
    private const double IdealInterCharacterDots = 3.0;
    private const double IdealInterWordDots = 10.0;
    private const double ConfidenceToleranceFraction = 0.25;

    // §8.4 step 6: a character whose pattern has no MorseAlphabet entry renders as U+FFFD AND takes a
    // confidence penalty -- a timing-plausible run of elements that decodes to no real letter is still
    // evidence against this being a genuine CW-ID (vs. e.g. the FSK ID's own OOK-shaped tone), not a
    // neutral "the letter itself just doesn't matter" case.
    private const double UnknownCharacterConfidencePenalty = 0.1;

    // §8.4 step 1: "report 'no tone' only if every candidate scores below a floor" -- a candidate can
    // clear every structural gate (SeedDotEstimate's ratio/gap-occupancy checks, DetectKeying's Otsu
    // separation) and still be a bad match on timing fit alone. 0.4 sits below a clean decode's own
    // ~0.9-1.0 typical confidence but above a genuinely mismatched candidate; tuned against this
    // file's own FSK-ID-collision regression test, not picked blind.
    private const double MinAcceptableConfidence = 0.4;

    public Task<CwDecodeResult> DecodeAsync(ReadOnlyMemory<float> samples, int sampleRate, CancellationToken ct = default)
    {
        var span = samples.Span;
        if (span.Length == 0 || sampleRate <= 0)
        {
            return Task.FromResult(NoTone());
        }

        CwDecodeResult? best = null;
        foreach (var toneHz in FindToneCandidates(span, sampleRate))
        {
            ct.ThrowIfCancellationRequested();

            var result = TryDecodeAtTone(span, sampleRate, toneHz);
            if (result is not null && (best is null || result.Confidence > best.Confidence))
            {
                best = result;
            }
        }

        return Task.FromResult(best is not null && best.Confidence >= MinAcceptableConfidence ? best : NoTone());
    }

    private static CwDecodeResult NoTone() => new(string.Empty, 0, null, null, []);

    /// <summary>§8.4 step 1: magnitude spectrum via a Goertzel sweep across the full configurable
    /// tone range, at ~10 Hz steps. Returns up to <see cref="ToneCandidateCount"/> distinct peaks
    /// (a minimum separation excludes adjacent bins of the same real peak from being reported as
    /// separate candidates), ranked by magnitude -- but the CALLER scores each through the full
    /// pipeline and picks the best-scoring one, not the first that merely passes a keying check
    /// (see this method's own call site): a bare magnitude peak alone can't distinguish a real CW
    /// tone from the FSK ID's own on/off-shift-keyed-looking mark or space tone.</summary>
    internal static List<double> FindToneCandidates(ReadOnlySpan<float> samples, int sampleRate)
    {
        var swept = new List<(double Hz, double Magnitude)>();
        for (var hz = MinToneHz; hz <= MaxToneHz; hz += ToneStepHz)
        {
            swept.Add((hz, GoertzelMagnitude(samples, sampleRate, hz)));
        }

        var median = Median(swept.Select(s => s.Magnitude).ToArray());
        if (median <= 0)
        {
            return [];
        }

        var candidates = new List<double>();
        foreach (var (hz, magnitude) in swept.OrderByDescending(s => s.Magnitude))
        {
            if (magnitude < median * ToneCandidateMinPeakToMedianRatio)
            {
                break;
            }

            if (candidates.Any(c => Math.Abs(c - hz) < ToneCandidateMinSeparationHz))
            {
                continue;
            }

            candidates.Add(hz);
            if (candidates.Count >= ToneCandidateCount)
            {
                break;
            }
        }

        return candidates;
    }

    internal static double GoertzelMagnitude(ReadOnlySpan<float> samples, int sampleRate, double targetHz)
    {
        var k = (int)(0.5 + samples.Length * targetHz / sampleRate);
        var omega = 2.0 * Math.PI * k / samples.Length;
        var coeff = 2.0 * Math.Cos(omega);
        double q1 = 0, q2 = 0;
        foreach (var sample in samples)
        {
            var q0 = coeff * q1 - q2 + sample;
            q2 = q1;
            q1 = q0;
        }

        var real = q1 - q2 * Math.Cos(omega);
        var imag = q2 * Math.Sin(omega);
        return Math.Sqrt(real * real + imag * imag);
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }

    /// <summary>Runs §8.4 steps 2-7 for one candidate tone. Returns <see langword="null"/> (not a
    /// zero-confidence result) when this candidate doesn't produce a plausible CW decode at all --
    /// distinct from a genuine "no tone" result, so the caller can try the NEXT candidate instead of
    /// treating this one as the final (if worse) answer.</summary>
    private static CwDecodeResult? TryDecodeAtTone(ReadOnlySpan<float> samples, int sampleRate, double toneHz)
    {
        var (envelope, blockDurationMs) = ComputeEnvelope(samples, sampleRate, toneHz);
        var intervals = DetectKeying(envelope, blockDurationMs);
        if (intervals is null || intervals.Count == 0)
        {
            return null;
        }

        var markDurations = intervals.Where(iv => iv.IsMark).Select(iv => iv.DurationMs).ToList();
        if (markDurations.Count < MinMarksForSpeedEstimate)
        {
            return null;
        }

        var seed = SeedDotEstimate(markDurations);
        if (seed is null)
        {
            return null;
        }

        return ClassifyAndDecode(intervals, seed.Value, toneHz);
    }

    /// <summary>§8.4 step 2: Goertzel magnitude at the candidate tone over
    /// <see cref="EnvelopeBlockMs"/> blocks, smoothed with a <see cref="EnvelopeSmoothingBlocks"/>-block
    /// median (odd window, centered).</summary>
    internal static (double[] Envelope, double BlockDurationMs) ComputeEnvelope(ReadOnlySpan<float> samples, int sampleRate, double toneHz)
    {
        var blockSamples = Math.Max(1, (int)(sampleRate * EnvelopeBlockMs / 1000.0));
        var blockCount = samples.Length / blockSamples;
        var raw = new double[blockCount];
        for (var i = 0; i < blockCount; i++)
        {
            raw[i] = GoertzelMagnitude(samples.Slice(i * blockSamples, blockSamples), sampleRate, toneHz);
        }

        var smoothed = new double[blockCount];
        var halfWindow = EnvelopeSmoothingBlocks / 2;
        for (var i = 0; i < blockCount; i++)
        {
            var lo = Math.Max(0, i - halfWindow);
            var hi = Math.Min(blockCount - 1, i + halfWindow);
            smoothed[i] = Median(raw[lo..(hi + 1)]);
        }

        var actualBlockDurationMs = blockSamples * 1000.0 / sampleRate;
        return (smoothed, actualBlockDurationMs);
    }

    /// <summary>§8.4 step 3: (i) coarse-gate the envelope to its own active-signal region (the CW
    /// burst itself), (ii) Otsu-threshold WITHIN that region only (not fixed global percentiles,
    /// which collapse toward the noise floor for a short ID inside a long window), (iii) hysteresis
    /// to avoid chatter at the threshold boundary, (iv) run-length encode into mark/space intervals.
    /// Returns <see langword="null"/> if no region passes the coarse gate, or the two Otsu classes
    /// aren't separated by at least the SNR margin (i.e. there's no real bimodal signal here at
    /// all).</summary>
    internal static List<(bool IsMark, double DurationMs)>? DetectKeying(double[] envelope, double blockDurationMs)
    {
        if (envelope.Length == 0)
        {
            return null;
        }

        var peak = envelope.Max();
        if (peak <= 0)
        {
            // Genuine, complete silence -- no signal at all, nothing else to check.
            return null;
        }

        // The coarse-gate reference level is deliberately PEAK-relative, not median-relative. A
        // median-based gate (`median * margin`) was tried first and is fundamentally fragile here:
        // real CW-ID text's duty cycle is NOT reliably low the way an earlier draft of this comment
        // assumed -- measured directly against CwMorseGenerator's own segment output, "DE W1AW" is
        // ~47-50% mark time regardless of WPM (duty cycle is WPM-invariant, since every element/gap
        // scales by the same 1/WPM factor). At a duty cycle that close to 50%, the median sits close
        // to peak/2, so `median * 2` sits close to or ABOVE peak itself -- an impossible gate that
        // silently rejected EVERY block, including genuine marks. This was a real bug, not a
        // hypothetical: caught because this file's own round-trip tests failed specifically at 5 WPM
        // (duty cycle landed just far enough past 50% to push the old gate over peak) while passing
        // at other WPM values by coincidence of block-count rounding, not because the design was
        // actually WPM-duty-cycle-robust. A pure peak fraction has no such failure mode -- it
        // degrades gracefully across the whole duty-cycle range from near-0% to near-100%.
        var gate = peak * CoarseGatePeakFraction;

        var padBlocks = Math.Max(1, (int)(CoarseGatePadMs / blockDurationMs));
        var activeRegion = FindActiveRegion(envelope, gate, padBlocks);
        if (activeRegion is not var (start, end))
        {
            return null;
        }

        var regionValues = envelope[start..end];
        var (threshold, lowClassMean, highClassMean) = OtsuThreshold(regionValues);
        if (highClassMean - lowClassMean < peak * CoarseGatePeakFraction)
        {
            // The two classes aren't genuinely separated -- no real keying here, just noise that
            // happened to clear the coarse gate. Same peak-relative reference as the gate above, for
            // the same reason (a median-relative check here degenerates identically).
            return null;
        }

        var highGate = threshold * (1.0 + HysteresisFraction);
        var lowGate = threshold * (1.0 - HysteresisFraction);

        var intervals = new List<(bool IsMark, double DurationMs)>();
        var currentIsMark = regionValues[0] >= threshold;
        var currentLength = 1;
        for (var i = 1; i < regionValues.Length; i++)
        {
            var value = regionValues[i];
            var nextIsMark = currentIsMark ? value >= lowGate : value >= highGate;
            if (nextIsMark == currentIsMark)
            {
                currentLength++;
                continue;
            }

            intervals.Add((currentIsMark, currentLength * blockDurationMs));
            currentIsMark = nextIsMark;
            currentLength = 1;
        }

        intervals.Add((currentIsMark, currentLength * blockDurationMs));
        return intervals;
    }

    /// <summary>Finds the region spanning the FIRST block whose envelope exceeds
    /// <paramref name="gate"/> through the LAST such block, padded by <paramref name="padBlocks"/>
    /// on each side (clamped to the envelope's own bounds). Deliberately NOT "the single longest
    /// contiguous above-gate run" -- a real CW ID is many short mark bursts separated by
    /// below-gate spaces (each dit/dah is its own contiguous run), so the longest SINGLE run is only
    /// one dah, not the whole transmission; an earlier draft of this method returned exactly that
    /// (found and fixed via this file's own round-trip tests, which decoded only the tail
    /// character(s) of a real "DE W1AW" fixture instead of the whole string). Below-gate blocks
    /// BETWEEN the first and last above-gate block are correctly included in the returned region --
    /// they're real inter-element/character spaces Otsu/hysteresis still need to see, not gaps to
    /// exclude. Returns <see langword="null"/> if no block clears the gate at all.</summary>
    private static (int Start, int End)? FindActiveRegion(double[] envelope, double gate, int padBlocks)
    {
        var first = -1;
        var last = -1;
        for (var i = 0; i < envelope.Length; i++)
        {
            if (envelope[i] < gate)
            {
                continue;
            }

            if (first < 0)
            {
                first = i;
            }

            last = i;
        }

        if (first < 0)
        {
            return null;
        }

        var start = Math.Max(0, first - padBlocks);
        var end = Math.Min(envelope.Length, last + 1 + padBlocks);
        return (start, end);
    }

    /// <summary>Two-class Otsu threshold (maximizes between-class variance) over a 256-bin histogram
    /// of <paramref name="values"/> -- converges to the mark/space midpoint regardless of duty
    /// cycle, unlike a fixed percentile split.</summary>
    private static (double Threshold, double LowClassMean, double HighClassMean) OtsuThreshold(double[] values)
    {
        const int binCount = 256;
        var min = values.Min();
        var max = values.Max();
        if (max <= min)
        {
            return (min, min, min);
        }

        var binWidth = (max - min) / binCount;
        var histogram = new int[binCount];
        foreach (var v in values)
        {
            var bin = Math.Clamp((int)((v - min) / binWidth), 0, binCount - 1);
            histogram[bin]++;
        }

        var total = values.Length;
        var sumAll = 0.0;
        for (var i = 0; i < binCount; i++)
        {
            sumAll += i * (double)histogram[i];
        }

        double sumBelow = 0;
        var countBelow = 0;
        var bestVariance = -1.0;
        var bestBin = 0;
        for (var t = 0; t < binCount; t++)
        {
            countBelow += histogram[t];
            sumBelow += t * (double)histogram[t];
            var countAbove = total - countBelow;
            if (countBelow == 0 || countAbove == 0)
            {
                continue;
            }

            var meanBelow = sumBelow / countBelow;
            var meanAbove = (sumAll - sumBelow) / countAbove;
            var variance = (double)countBelow * countAbove * (meanBelow - meanAbove) * (meanBelow - meanAbove);
            if (variance > bestVariance)
            {
                bestVariance = variance;
                bestBin = t;
            }
        }

        var threshold = min + (bestBin + 0.5) * binWidth;
        var lowValues = values.Where(v => v < threshold).ToArray();
        var highValues = values.Where(v => v >= threshold).ToArray();
        var lowMean = lowValues.Length > 0 ? lowValues.Average() : min;
        var highMean = highValues.Length > 0 ? highValues.Average() : max;
        return (threshold, lowMean, highMean);
    }

    /// <summary>§8.4 step 4: seeds the dot length from the mark-length distribution via 1D two-means
    /// clustering (dot = short-cluster centre, cross-checked against long-cluster/3, averaged --
    /// matching wb7fhc's own dash-duration cross-check shape). Rejects the window (returns
    /// <see langword="null"/>) if the long/short ratio is out of range, if a material fraction of
    /// marks falls in the gap BETWEEN the two clusters, or if a material fraction of marks sit far
    /// from THEIR OWN assigned cluster's centre (see <see cref="MaxClusterMemberRelativeDeviation"/>'s
    /// own doc comment for why the gap check alone isn't a sufficient discriminator).</summary>
    internal static double? SeedDotEstimate(List<double> markDurations)
    {
        var (shortCentre, longCentre) = TwoMeans(markDurations);
        if (shortCentre <= 0 || longCentre <= 0)
        {
            return null;
        }

        var ratio = longCentre / shortCentre;
        if (ratio < MinLongShortRatio || ratio > MaxLongShortRatio)
        {
            return null;
        }

        var gapLow = shortCentre + (longCentre - shortCentre) / 3.0;
        var gapHigh = shortCentre + (longCentre - shortCentre) * 2.0 / 3.0;
        var inGapCount = markDurations.Count(m => m >= gapLow && m <= gapHigh);
        if (inGapCount / (double)markDurations.Count > ClusterGapMaxOccupancyFraction)
        {
            return null;
        }

        var outlierCount = markDurations.Count(m =>
        {
            var nearerCentre = Math.Abs(m - shortCentre) <= Math.Abs(m - longCentre) ? shortCentre : longCentre;
            return Math.Abs(m - nearerCentre) / nearerCentre > MaxClusterMemberRelativeDeviation;
        });
        if (outlierCount / (double)markDurations.Count > MaxClusterOutlierFraction)
        {
            return null;
        }

        var dot = (shortCentre + longCentre / 3.0) / 2.0;
        return dot is >= MinDotMs and <= MaxDotMs ? dot : null;
    }

    private static (double ShortCentre, double LongCentre) TwoMeans(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var shortCentre = sorted[0];
        var longCentre = sorted[^1];
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var shortGroup = new List<double>();
            var longGroup = new List<double>();
            foreach (var v in sorted)
            {
                (Math.Abs(v - shortCentre) <= Math.Abs(v - longCentre) ? shortGroup : longGroup).Add(v);
            }

            if (shortGroup.Count == 0 || longGroup.Count == 0)
            {
                break;
            }

            var newShort = shortGroup.Average();
            var newLong = longGroup.Average();
            var converged = Math.Abs(newShort - shortCentre) < 0.01 && Math.Abs(newLong - longCentre) < 0.01;
            shortCentre = newShort;
            longCentre = newLong;
            if (converged)
            {
                break;
            }
        }

        return (shortCentre, longCentre);
    }

    /// <summary>§8.4 steps 5-7: walks the mark/space intervals in order, classifying against a
    /// RUNNING dot estimate (not the fixed seed -- the classifier follows the sender's actual timing
    /// drift across the ID) and building per-character dit/dah patterns, resolved via
    /// <see cref="MorseAlphabet"/>. A word-gap space both ends the current character AND appends a
    /// literal space to <see cref="CwDecodeResult.Text"/>.</summary>
    internal static CwDecodeResult ClassifyAndDecode(List<(bool IsMark, double DurationMs)> intervals, double seedDot, double toneHz)
    {
        var dot = seedDot;
        var textBuilder = new System.Text.StringBuilder();
        var characters = new List<CwDecodedCharacter>();
        var currentPattern = new System.Text.StringBuilder();
        // Scores for the character CURRENTLY being built -- reset on every flush, so each character's
        // own CwDecodedCharacter.Confidence reflects only ITS OWN elements/gaps, not the whole decode's
        // (an earlier draft hardcoded 1.0 here regardless of actual timing fit or lookup success).
        var currentCharacterScores = new List<double>();

        void FlushCharacter()
        {
            if (currentPattern.Length == 0)
            {
                currentCharacterScores.Clear();
                return;
            }

            var pattern = currentPattern.ToString();
            var decodedChar = MorseAlphabet.Lookup(pattern);
            var isUnknown = decodedChar is null;
            var timingConfidence = currentCharacterScores.Count > 0 ? currentCharacterScores.Average() : 0.0;
            var confidence = isUnknown ? timingConfidence * UnknownCharacterConfidencePenalty : timingConfidence;
            characters.Add(new CwDecodedCharacter(decodedChar ?? '\0', pattern, isUnknown, confidence));
            textBuilder.Append(isUnknown ? '\uFFFD' : decodedChar!.Value);
            currentPattern.Clear();
            currentCharacterScores.Clear();
        }

        foreach (var (isMark, durationMs) in intervals)
        {
            if (isMark)
            {
                var isDit = durationMs < dot * 2.0;
                currentPattern.Append(isDit ? '.' : '-');

                // Element duration's own ideal is 1 dot (dit) or 3 dots (dah) -- scored against the
                // CURRENT running estimate, same as classification itself.
                var elementIdealDots = isDit ? 1.0 : 3.0;
                currentCharacterScores.Add(TimingScore(durationMs, elementIdealDots * dot));

                dot = isDit
                    ? (1 - RunningEstimateSmoothingFactor) * dot + RunningEstimateSmoothingFactor * durationMs
                    : (1 - RunningEstimateSmoothingFactor) * dot + RunningEstimateSmoothingFactor * (durationMs / 3.0);
                continue;
            }

            if (durationMs < dot * 2.0)
            {
                // Intra-character gap -- part of the same character, not a boundary. §8.4 step 5's
                // own ideal is 1 dot (the per-element trailing silence), scored but not flushed.
                currentCharacterScores.Add(TimingScore(durationMs, IdealIntraElementDots * dot));
                continue;
            }

            if (durationMs < dot * 5.0)
            {
                // Character gap -- the gap that ENDS the character being flushed, so its timing fit
                // counts toward that character's own confidence before the flush.
                currentCharacterScores.Add(TimingScore(durationMs, IdealInterCharacterDots * dot));
                FlushCharacter();
                continue;
            }

            // Word gap -- ends the character AND appends a literal space.
            currentCharacterScores.Add(TimingScore(durationMs, IdealInterWordDots * dot));
            FlushCharacter();
            if (textBuilder.Length > 0 && textBuilder[^1] != ' ')
            {
                textBuilder.Append(' ');
            }
        }

        FlushCharacter();

        var text = textBuilder.ToString().Trim();
        if (text.Length == 0)
        {
            return NoTone();
        }

        // Overall confidence is the average of the PER-CHARACTER confidences, not a flat average of
        // every raw element score -- so a decode that is entirely/mostly unknown characters can no
        // longer report near-1.0 confidence (§8.4 step 6's own required penalty flows through here).
        var confidence = characters.Count > 0 ? characters.Average(c => c.Confidence) : 0.0;
        return new CwDecodeResult(text, confidence, toneHz, MillisecondsPerDotToWpm(dot), characters);
    }

    /// <summary>§8.4 step 7: 1.0 if <paramref name="actualMs"/> is within
    /// <see cref="ConfidenceToleranceFraction"/> of <paramref name="idealMs"/>, linearly falling to
    /// 0 as the relative error doubles past that tolerance -- not a hard cutoff, so a slightly-off
    /// element still contributes partial confidence rather than zeroing the whole result over one
    /// borderline timing.</summary>
    private static double TimingScore(double actualMs, double idealMs)
    {
        if (idealMs <= 0)
        {
            return 0;
        }

        var relativeError = Math.Abs(actualMs - idealMs) / idealMs;
        if (relativeError <= ConfidenceToleranceFraction)
        {
            return 1.0;
        }

        var score = 1.0 - (relativeError - ConfidenceToleranceFraction) / ConfidenceToleranceFraction;
        return Math.Clamp(score, 0.0, 1.0);
    }

    // Inverse of CwMorseGenerator.MillisecondsPerDotFromWpm (1110.0 / wpm) -- this port's own
    // convention (see that method's own doc comment for why 1110, not the textbook PARIS 1200), used
    // here only to report an estimated WPM back to the caller, not to constrain decoding itself
    // (decoding uses the measured dot length directly throughout).
    private static double MillisecondsPerDotToWpm(double dotMs) => dotMs > 0 ? 1110.0 / dotMs : 0;
}
