namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>One assembled noise stream at the sweep's own sample rate, with everything needed to
/// reproduce and audit it. The metadata is not decoration: the SNR label on a sweep point is a
/// long-run average over a stream whose instantaneous level steps, so a mode's floor partly encodes
/// which clips its stream happened to contain.</summary>
public sealed record RealNoiseBuffer(
    float[] Samples,
    string Day,
    string Receiver,
    IReadOnlyList<string> FileNames,
    IReadOnlyList<double> JoinOffsetsSeconds,
    IReadOnlyList<double> ClipRmsDb,
    double ClipRmsSpreadDb,
    bool ClipRmsSpreadWarning);

/// <summary>Assembles a continuous noise stream from the recorded HF corpus.
///
/// Clips are 2-34s but a mode needs up to about 290s, so roughly 17 joins are unavoidable. Measured
/// evidence says every join is a real discontinuity: clips sharing a session prefix show a
/// systematic 1.2-2.4dB step at the boundary with no fade ramp at either edge, and the underlying
/// within-clip rise is corpus-wide (mean +0.66dB last-quartile over first, 79% of 250 clips, across
/// all 3 capture days). Concatenation therefore leaves a small sawtooth, which is recorded as a known
/// artifact rather than designed around -- it is sub-grid-step and AGC-tracked, and the alternative
/// (per-clip level matching) reintroduces exactly the normalization this sweep deliberately rejects.
///
/// Clip levels are kept RAW. Within a stream the clips share one receiver, so their level
/// differences are partly real drift, which is the variation the whole exercise exists to capture.
/// Measured spread is 8.7dB corpus-wide and up to 15dB within one receiver, so the compensating
/// record-keeping below is what keeps that honest.</summary>
public static class RealNoiseStreamBuilder
{
    public const int OutputSampleRate = 44100;

    // Anti-click only, NOT level-matching. A raw step is a synthetic impulse and would corrupt the
    // impulsive statistic this sweep exists to measure; a real 1.2-2.4dB level step across the join
    // is left intact, because it is real HF behavior.
    private const double CrossfadeSeconds = 0.005;

    // Above this, a stream's own level spread is wide enough that its SNR label is a weak summary of
    // what the decoder actually saw. Flagged, not rejected.
    private const double SpreadWarningDb = 12.0;

    public static RealNoiseBuffer Build(RealNoiseCorpus corpus, int seed, int seedOrdinal, double requiredSeconds)
    {
        var (day, receiver, ordered) = SelectStratum(corpus, seed, seedOrdinal);

        var crossfade = (int)(CrossfadeSeconds * corpus.SampleRate);
        var trim = PolyphaseResampler.OutputGroupDelay;
        var neededOutput = (int)(requiredSeconds * OutputSampleRate) + trim;
        // Assemble in the corpus's own rate, then resample once. Resampling each clip separately
        // would put a filter warm-up transient at every join instead of only at the head.
        var neededAssembled = (long)Math.Ceiling(neededOutput * (double)PolyphaseResampler.DownFactor / PolyphaseResampler.UpFactor) + crossfade;

        var assembled = new List<float>(capacity: (int)Math.Min(neededAssembled + crossfade, int.MaxValue));
        var fileNames = new List<string>();
        var clipRmsDb = new List<double>();
        var joinOffsetsAssembled = new List<int>();

        var cursor = 0;
        while (assembled.Count < neededAssembled)
        {
            // Wrap rather than stop: a receiver's own corpus can be shorter than the longest mode.
            var clip = ordered[cursor % ordered.Count];
            cursor++;

            var samples = RealNoiseCorpus.ReadClip(clip);
            if (samples.Length <= crossfade * 2)
            {
                continue;
            }

            fileNames.Add(clip.FileName);
            clipRmsDb.Add(20.0 * Math.Log10(FirFilter.Rms(samples) + double.Epsilon));

            if (assembled.Count == 0)
            {
                assembled.AddRange(samples);
                continue;
            }

            joinOffsetsAssembled.Add(assembled.Count - crossfade);
            CrossfadeInto(assembled, samples, crossfade);
        }

        var resampled = PolyphaseResampler.Resample(assembled, corpus.SampleRate);

        // The prototype's warm-up lands at output sample 0, which is exactly where VIS lock happens.
        var head = Math.Min(trim, resampled.Length);
        var take = Math.Min((int)(requiredSeconds * OutputSampleRate), resampled.Length - head);
        var output = resampled[head..(head + take)];

        var scale = (double)OutputSampleRate / corpus.SampleRate;
        var joinOffsetsSeconds = joinOffsetsAssembled
            .Select(a => ((a * scale) - head) / OutputSampleRate)
            .Where(t => t >= 0 && t < take / (double)OutputSampleRate)
            .ToList();

        var spread = clipRmsDb.Count == 0 ? 0 : clipRmsDb.Max() - clipRmsDb.Min();

        return new RealNoiseBuffer(
            output,
            day,
            receiver,
            fileNames,
            joinOffsetsSeconds,
            clipRmsDb,
            spread,
            spread > SpreadWarningDb);
    }

    /// <summary>Picks the (day, receiver) stratum this seed draws from. The day is chosen by the
    /// seed's ORDINAL, not by its value, so a set of N seeds walks the available capture days instead
    /// of possibly landing on one of only 3.</summary>
    private static (string Day, string Receiver, IReadOnlyList<RealNoiseClip> Clips) SelectStratum(
        RealNoiseCorpus corpus, int seed, int seedOrdinal)
    {
        var strata = corpus.Strata();
        var days = corpus.Days;
        var day = days[seedOrdinal % days.Count];

        var receivers = strata.Keys
            .Where(k => k.Day == day)
            .Select(k => k.Receiver)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        var random = new Random(seed);
        var receiver = receivers[random.Next(receivers.Count)];
        var clips = strata[(day, receiver)];

        // Start somewhere inside the stratum, so two seeds landing on the same receiver still differ.
        var start = random.Next(clips.Count);
        var rotated = clips.Skip(start).Concat(clips.Take(start)).ToList();
        return (day, receiver, rotated);
    }

    /// <summary>Constant-power (sin/cos) crossfade, not an amplitude fade. For uncorrelated segments
    /// an amplitude-linear or raised-cosine fade dips summed power about 3dB mid-fade, which would
    /// put a systematic noise notch at every join.</summary>
    internal static void CrossfadeInto(List<float> assembled, float[] incoming, int crossfade)
    {
        var overlapStart = assembled.Count - crossfade;
        for (var i = 0; i < crossfade; i++)
        {
            var theta = Math.PI / 2.0 * ((i + 0.5) / crossfade);
            assembled[overlapStart + i] = (float)((assembled[overlapStart + i] * Math.Cos(theta)) + (incoming[i] * Math.Sin(theta)));
        }

        for (var i = crossfade; i < incoming.Length; i++)
        {
            assembled.Add(incoming[i]);
        }
    }
}
