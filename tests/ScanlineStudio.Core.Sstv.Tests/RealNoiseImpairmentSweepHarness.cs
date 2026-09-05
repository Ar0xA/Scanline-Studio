using System.Globalization;
using System.Text.Json;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>Runs the impairment sweep against REAL recorded HF noise, and against a Gaussian control
/// band-matched to that corpus. Answers the one question <see cref="ImpairmentSweepHarness"/> cannot:
/// real HF noise is not Gaussian (measured kurtosis 3.73 median against AWGN's 2.98, crest to 11.4
/// against 4.57), so an AWGN-only sweep may overstate discriminator performance.
///
/// Why this lives beside the original harness instead of inside it: the historic <c>awgn-v1</c>
/// generation path must stay bit-identical so every existing baseline report stays valid. Nothing
/// here touches it. The two files share measurement helpers, not the noise path.
///
/// THREE noise models, and they are not interchangeable:
/// <list type="bullet">
/// <item><c>awgn-v1</c> -- the historic model, calibrated on total-band power with the original
/// 2-seed rule. Owned by <see cref="ImpairmentSweepHarness"/>.</item>
/// <item><c>awgn-inband-v1</c> -- the control for this comparison. Gaussian, but LOW-PASSED to the
/// corpus's own bandwidth and calibrated on the same in-band axis with the same floor estimator as
/// the real-noise arm. Without the band match the two arms would differ in spectrum as well as in
/// amplitude statistic, and the headline result would not be attributable to non-Gaussianity at all.
/// Band-limiting does not compromise the control: a linear filter of Gaussian noise is still
/// Gaussian.</item>
/// <item><c>paderborn-real-v1</c> -- real recorded HF noise from the external corpus.</item>
/// </list>
///
/// BAND ATTRIBUTION RULE, and it is the whole point of recording four band powers per point: never
/// quote a floor or a delta against a band that does not govern the mechanism being claimed. Floors
/// go against H2 (400-2500Hz), because the pass/fail bar is a lock test and H2 is the search filter
/// that decides whether a lock happens. Post-lock pixel delta goes against H1-Wide (1100-2600Hz). Any
/// MN/MC claim goes against that mode's own H3. All four are recorded per point, so any of these can
/// be re-plotted later without re-running.
///
/// Cost: band powers are measured with 1023-tap FIRs, but each noise stream is filtered ONCE per band
/// and reduced to a prefix sum of squares, so a mode's own slice RMS is then O(1). That is 25 filter
/// passes for the whole sweep rather than one per mode per point.</summary>
public sealed class RealNoiseImpairmentSweepHarness
{
    // H2, the search/pre-lock filter -- SearchBandpassFilter.cs:140. This is the calibration band:
    // the sweep's own DecodedCorrectly bar is a lock-and-mode-match test, and H2 governs the lock.
    private const double H2LowHz = 400;
    private const double H2HighHz = 2500;

    // H1 at the Wide preset, the default -- SearchBandpassFilter.cs:125 with syncRestart on.
    private const double H1LowHz = 1100;
    private const double H1HighHz = 2600;

    // The corpus's own edge, from its MEDIAN 99%-energy frequency (3530Hz). About half the clips
    // carry some energy above it; that approximation is what this design chose over PSD-matching.
    private const double CorpusBandwidthHz = 3500;

    private const int MeasurementTaps = 1023;

    // Same "usable decode" bar as NoiseRobustnessTests and the AWGN sweep, kept deliberately in sync.
    private const double MeanUsableDeltaBar = 30.0;

    // Twice the mean bar, applied to the 95th-percentile per-line delta. A whole-image mean tracks
    // perceived damage under near-uniform AWGN, but impulsive noise wrecks a few lines among many
    // clean ones, and such an image can still score under the mean bar. Recorded alongside the
    // mean-based flag, never instead of it, so either floor can be computed later without re-running.
    private const double PercentileUsableDeltaBar = 60.0;

    private static readonly int[] InBandSeeds = [12345, 67890, 24680, 13579, 55555];

    // 4 of 5, not "every seed" -- see ImpairmentSeedOutcome's own doc comment for why a first-failure
    // rule turns the floor into a min-of-N order statistic once the noise is heavy-tailed.
    private const int RequiredUsableSeeds = 4;

    [RequiresRealNoiseSweepFact]
    public async Task RunRealNoiseSweep_ProducesBaselineReport()
    {
        var corpus = RealNoiseCorpus.Load(Environment.GetEnvironmentVariable("SCANLINE_REAL_NOISE_DIR")!);
        Console.WriteLine($"Corpus: {corpus.Clips.Count} clips, {corpus.TotalSeconds / 3600.0:F1}h, hash {corpus.IdentityHash}, {corpus.SkippedFiles.Count} skipped.");
        foreach (var skipped in corpus.SkippedFiles.Take(20))
        {
            Console.WriteLine($"  skipped {skipped}");
        }

        await RunSweepAsync("paderborn-real-v1", corpus);
    }

    [RequiresBandLimitedAwgnSweepFact]
    public async Task RunBandLimitedAwgnControlSweep_ProducesBaselineReport()
    {
        await RunSweepAsync("awgn-inband-v1", corpus: null);
    }

    private static async Task RunSweepAsync(string noiseModel, RealNoiseCorpus? corpus)
    {
        var encoder = new AnalogFmSstvEncoder(44100);
        var runDir = Path.Combine(
            ImpairmentSweepHarness.FindRepoRoot(),
            "impairment-reports",
            string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}-{noiseModel}-{encoder.SampleRate}Hz"));
        Directory.CreateDirectory(runDir);

        var selected = SelectModes();
        // Only the bands the selected modes actually need. Each band costs a full-length filter pass
        // per realization, and the three H3 variants are dead weight unless a narrow mode is in the run.
        var bands = BuildMeasurementBands(encoder.SampleRate, selected.Select(m => m.ModeId));
        var progressPath = Path.Combine(runDir, "progress.log");

        // Progress goes to a FILE, not only to Console: xUnit buffers a test's console output until
        // the test finishes, and this sweep runs for hours. A run nobody can watch is a run nobody
        // can tell apart from a hang.
        Progress(progressPath, $"{noiseModel} at {encoder.SampleRate}Hz, {selected.Count} modes selected.");

        var maxSeconds = MaxModeSeconds(selected.Select(m => m.ModeId));
        Progress(progressPath, $"preparing {InBandSeeds.Length} noise realizations of {maxSeconds:F0}s...");
        var realizations = InBandSeeds
            .Select((seed, ordinal) => NoiseRealization.Prepare(seed, ordinal, maxSeconds, encoder.SampleRate, bands, corpus))
            .ToList();
        Progress(progressPath, "noise ready.");

        var metadata = BuildMetadata(noiseModel, encoder.SampleRate, bands, corpus, realizations.Select(r => r.Statistics).ToList());
        var reports = new List<ImpairmentModeReport>();

        foreach (var (modeId, source, pictureHeight) in selected)
        {
            Progress(progressPath, $"starting {modeId}...");
            var report = await MeasureModeAsync(modeId, source, pictureHeight, encoder, realizations, bands, metadata);
            reports.Add(report);

            // Rewritten after every mode, so a run that is killed or times out still leaves usable
            // results on disk rather than nothing at all.
            File.WriteAllText(
                Path.Combine(runDir, "report.json"),
                JsonSerializer.Serialize(reports, ImpairmentSweepHarness.ReportJsonOptions));

            var floor = report.NoiseFloorDb is null ? "NEVER" : $"{report.NoiseFloorDb:F1}dB";
            Progress(progressPath, $"finished {modeId}: floor ({metadata.FloorAxis}) {floor}");
        }

        Progress(progressPath, $"done: {reports.Count} modes, written to {runDir}.");
        Console.WriteLine($"{noiseModel}: {reports.Count} modes, written to {runDir}.");
        foreach (var report in reports)
        {
            var floor = report.NoiseFloorDb is null ? "NEVER" : $"{report.NoiseFloorDb:F1}dB";
            Console.WriteLine($"  [{report.ModeId}] floor ({metadata.FloorAxis}): {floor}");
        }
    }

    /// <summary>The modes this run covers. <c>SCANLINE_IMPAIRMENT_MODES</c> narrows it to a
    /// comma-separated list, which is what the floor-reproducibility subset run needs -- two runs over
    /// the same handful of modes with disjoint seed sets, to find out whether 5 realizations is
    /// enough before a full run is spent.</summary>
    private static IReadOnlyList<(string ModeId, IImageSource Source, int PictureHeight)> SelectModes()
    {
        var filter = Environment.GetEnvironmentVariable("SCANLINE_IMPAIRMENT_MODES");
        var wanted = string.IsNullOrWhiteSpace(filter)
            ? null
            : filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selected = new List<(string, IImageSource, int)>();
        var realFixtureModeIds = ImpairmentSweepHarness.RealFixtureModes.Select(m => m.ModeId).ToHashSet();

        foreach (var (modeId, sourceBmp, pictureHeight) in ImpairmentSweepHarness.RealFixtureModes)
        {
            if (wanted is null || wanted.Contains(modeId))
            {
                selected.Add((modeId, BmpFile.Read(Path.Combine(ImpairmentSweepHarness.FixtureDir, sourceBmp)), pictureHeight));
            }
        }

        foreach (var mode in SstvModeRegistry.All.Where(m => !realFixtureModeIds.Contains(m.Id)))
        {
            if (wanted is not null && !wanted.Contains(mode.Id))
            {
                continue;
            }

            IImageSource source = mode.ColorEncoding == ColorEncoding.MonoAveragedPaired
                ? ImpairmentSweepHarness.CreateGrayscaleGradientTestImage(mode.ImageWidth, mode.ImageHeight)
                : ImpairmentSweepHarness.CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
            selected.Add((mode.Id, source, mode.ImageHeight));
        }

        if (selected.Count == 0)
        {
            throw new InvalidOperationException($"SCANLINE_IMPAIRMENT_MODES='{filter}' matched no registered mode.");
        }

        return selected;
    }

    private static void Progress(string path, string message)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"[{DateTime.UtcNow:HH:mm:ss}] {message}");
        File.AppendAllText(path, line + Environment.NewLine);
        Console.WriteLine(line);
    }

    private static async Task<ImpairmentModeReport> MeasureModeAsync(
        string modeId,
        IImageSource source,
        int pictureHeight,
        AnalogFmSstvEncoder encoder,
        IReadOnlyList<NoiseRealization> realizations,
        IReadOnlyList<MeasurementBand> bands,
        ImpairmentRunMetadata metadata)
    {
        var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);

        var cleanSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, source))
        {
            cleanSamples.Add(sample);
        }

        foreach (var realization in realizations)
        {
            if (realization.SampleCount < cleanSamples.Count)
            {
                throw new InvalidOperationException(
                    $"Mode {modeId} encodes to {cleanSamples.Count / (double)encoder.SampleRate:F1}s but the prepared noise " +
                    $"realizations are only {realization.SampleCount / (double)encoder.SampleRate:F1}s. Raise MaxModeSeconds.");
            }
        }

        var h2 = bands.Single(b => b.Name == "H2").Coefficients;
        var signalInBandRms = FirFilter.BandRms(cleanSamples, h2);
        var signalTotalRms = FirFilter.Rms(cleanSamples);
        // Non-null exactly when BuildMeasurementBands was told to build that band: both derive from
        // NarrowBandFor over the same selected mode set, so the lookup below cannot miss.
        var h3Band = NarrowBandFor(mode);

        var points = new List<ImpairmentPoint>();
        double? noiseFloorDb = null;
        var stillUnbrokenFromTop = true;

        foreach (var snrDb in ImpairmentSweepHarness.SnrLevelsDb)
        {
            var outcomes = new List<ImpairmentSeedOutcome>();
            foreach (var realization in realizations)
            {
                var noiseInBandRms = realization.BandRms("H2", cleanSamples.Count);
                var scale = signalInBandRms / Math.Pow(10.0, snrDb / 20.0) / noiseInBandRms;
                var noisy = realization.Mix(cleanSamples, scale);

                var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
                SstvModeDefinition? detectedMode = null;
                IImageSource? decodedImage = null;
                decoder.ModeDetected += m => detectedMode = m;
                decoder.LineDecoded += update => decodedImage = update.Image;
                decoder.PushSamples(noisy);

                var decodedCorrectly = detectedMode is not null && detectedMode.Id == mode.Id && decodedImage is not null;
                if (!decodedCorrectly)
                {
                    // Recorded, never skipped: every seed's own outcome is what makes the floor a
                    // success fraction rather than a min-of-N.
                    outcomes.Add(new ImpairmentSeedOutcome(realization.Seed, false, null, null, false));
                    continue;
                }

                var actual = ImpairmentSweepHarness.CropToTop(decodedImage!, pictureHeight);
                var delta = ImpairmentSweepHarness.MeasureAveragePerChannelDelta(source, actual, pictureHeight);
                var perLine95 = PerLineDelta95(source, actual, pictureHeight);
                outcomes.Add(new ImpairmentSeedOutcome(realization.Seed, true, delta, perLine95, delta <= MeanUsableDeltaBar));
            }

            var decodedOutcomes = outcomes.Where(o => o.DecodedCorrectly).ToList();
            var usableCount = outcomes.Count(o => o.Usable);
            var meanDelta = decodedOutcomes.Count == 0 ? double.NaN : decodedOutcomes.Average(o => o.Delta!.Value);
            var meanPerLine95 = decodedOutcomes.Count == 0 ? (double?)null : decodedOutcomes.Average(o => o.PerLineDelta95!.Value);

            var reference = realizations[0];
            var referenceNoiseInBand = reference.BandRms("H2", cleanSamples.Count);
            var scaleAtPoint = signalInBandRms / Math.Pow(10.0, snrDb / 20.0) / referenceNoiseInBand;

            points.Add(new ImpairmentPoint(
                SnrDb: snrDb,
                Delta: meanDelta,
                DecodedCorrectly: decodedOutcomes.Count == outcomes.Count,
                TotalBandSnrDb: ToDb(signalTotalRms / (reference.BandRms("total", cleanSamples.Count) * scaleAtPoint)),
                InBandSnrDb: snrDb,
                NoiseBandPowerH1Db: ToDb(reference.BandRms("H1", cleanSamples.Count) * scaleAtPoint),
                NoiseBandPowerH2Db: ToDb(referenceNoiseInBand * scaleAtPoint),
                NoiseBandPowerH3Db: h3Band is null ? null : ToDb(reference.BandRms(h3Band, cleanSamples.Count) * scaleAtPoint),
                NoiseBandPowerTotalDb: ToDb(reference.BandRms("total", cleanSamples.Count) * scaleAtPoint),
                PerLineDelta95: meanPerLine95,
                UsableByPercentile: meanPerLine95 is null ? null : meanPerLine95 <= PercentileUsableDeltaBar,
                SeedOutcomes: outcomes));

            if (usableCount >= RequiredUsableSeeds)
            {
                if (stillUnbrokenFromTop)
                {
                    noiseFloorDb = snrDb;
                }
            }
            else
            {
                stillUnbrokenFromTop = false;
            }
        }

        var first = realizations[0];
        return new ImpairmentModeReport(
            modeId,
            points,
            noiseFloorDb,
            metadata,
            first.FileNames,
            first.JoinOffsetsSeconds,
            first.ClipRmsSpreadDb,
            first.ClipRmsSpreadWarning,
            first.Day,
            first.Receiver);
    }

    /// <summary>95th-percentile of the per-LINE mean delta. The whole-image mean that drives the
    /// usability bar hides the impulsive failure shape: a handful of wrecked lines against many clean
    /// ones can still average under the bar.</summary>
    private static double PerLineDelta95(IImageSource expected, IImageSource actual, int height)
    {
        var perLine = new double[height];
        for (var y = 0; y < height; y++)
        {
            var expectedLine = expected.GetScanline(y);
            var actualLine = actual.GetScanline(y);
            double total = 0;
            for (var x = 0; x < expected.Width; x++)
            {
                total += Math.Abs(expectedLine[x].R - actualLine[x].R);
                total += Math.Abs(expectedLine[x].G - actualLine[x].G);
                total += Math.Abs(expectedLine[x].B - actualLine[x].B);
            }

            perLine[y] = total / (expected.Width * 3);
        }

        Array.Sort(perLine);
        var index = Math.Min(perLine.Length - 1, (int)Math.Ceiling(0.95 * perLine.Length) - 1);
        return perLine[Math.Max(0, index)];
    }

    private static IReadOnlyList<MeasurementBand> BuildMeasurementBands(int sampleRate, IEnumerable<string> modeIds)
    {
        var bands = new List<MeasurementBand>
        {
            new("H1", H1LowHz, H1HighHz, FirFilter.DesignBandPass(H1LowHz, H1HighHz, sampleRate, MeasurementTaps)),
            new("H2", H2LowHz, H2HighHz, FirFilter.DesignBandPass(H2LowHz, H2HighHz, sampleRate, MeasurementTaps)),
        };

        // H3 is per-mode AND per-preset (SearchBandpassFilter.BuildNarrowLockedFilter). At the Wide
        // default the fcl offset is -200Hz, giving these three distinct narrow bands. Only the ones a
        // selected mode actually locks with are built.
        var ids = modeIds.ToHashSet(StringComparer.Ordinal);
        var needed = SstvModeRegistry.All
            .Where(m => ids.Contains(m.Id))
            .Select(NarrowBandFor)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (name, low, high) in NarrowBands.Where(b => needed.Contains(b.Name)))
        {
            bands.Add(new MeasurementBand(name, low, high, FirFilter.DesignBandPass(low, high, sampleRate, MeasurementTaps)));
        }

        return bands;
    }

    private static readonly (string Name, double LowHz, double HighHz)[] NarrowBands =
    [
        ("H3-1400-2500", 1400, 2500),
        ("H3-1500-2400", 1500, 2400),
        ("H3-1450-2500", 1450, 2500),
    ];

    private static string? NarrowBandFor(SstvModeDefinition mode) => mode.NarrowModeCode switch
    {
        0x02 or 0x04 or 0x14 => "H3-1400-2500",
        0x05 or 0x16 => "H3-1500-2400",
        0x15 => "H3-1450-2500",
        null => null,
        _ => "H3-1400-2500",
    };

    private static double MaxModeSeconds(IEnumerable<string> modeIds)
    {
        // Generous by construction: line folding means LineDurationMs x ImageHeight is an estimate,
        // not the encoded length, and MeasureModeAsync throws with the real shortfall if it is short.
        var ids = modeIds.ToHashSet(StringComparer.Ordinal);
        var longest = SstvModeRegistry.All.Where(m => ids.Contains(m.Id)).Max(m => m.LineDurationMs * m.ImageHeight / 1000.0);
        return (longest * 1.25) + 15.0;
    }

    private static ImpairmentRunMetadata BuildMetadata(
        string noiseModel, int sampleRate, IReadOnlyList<MeasurementBand> bands, RealNoiseCorpus? corpus,
        IReadOnlyList<string> noiseStatistics)
    {
        var specs = bands
            .Select(b => new FirSpec($"measurement-{b.Name}", MeasurementTaps, b.LowHz, b.HighHz, sampleRate, "blackman").ToString())
            .ToList();
        specs.Add(PolyphaseResampler.Spec(48000).ToString());
        if (noiseModel == "awgn-inband-v1")
        {
            specs.Add(new FirSpec("control-band-limiter", MeasurementTaps, 0, CorpusBandwidthHz, sampleRate, "blackman").ToString());
        }

        return new ImpairmentRunMetadata(
            NoiseModel: noiseModel,
            SampleRate: sampleRate,
            CalibrationBand: string.Create(CultureInfo.InvariantCulture, $"H2 {H2LowHz:F0}-{H2HighHz:F0}Hz"),
            SeedCount: InBandSeeds.Length,
            FloorAxis: string.Create(CultureInfo.InvariantCulture, $"H2 {H2LowHz:F0}-{H2HighHz:F0}Hz in-band SNR"),
            FloorRule: string.Create(CultureInfo.InvariantCulture, $"lowest SNR where at least {RequiredUsableSeeds} of {InBandSeeds.Length} seeds are usable, unbroken from the top"),
            MeanUsableDeltaBar: MeanUsableDeltaBar,
            PercentileUsableDeltaBar: PercentileUsableDeltaBar,
            FilterSpecs: specs,
            NoiseStatistics: noiseStatistics,
            CorpusDirectory: corpus?.Directory,
            CorpusIdentityHash: corpus?.IdentityHash,
            CorpusClipCount: corpus?.Clips.Count,
            CorpusTotalSeconds: corpus?.TotalSeconds,
            CorpusCaveats: corpus is null
                ? null
                : [
                    $"Corpus spans only {corpus.Days.Count} capture days ({string.Join(", ", corpus.Days)}); ionospheric diversity is limited.",
                    "Clip levels are kept raw, not normalized; measured spread is 8.7dB corpus-wide and up to 15dB within one receiver.",
                    "Concatenation leaves a small sawtooth: clips rise about 0.66dB from first to last quartile (79% of 250 sampled clips), and joins showed 1.2-2.4dB edge steps in the one group measured directly.",
                    "No noise-only lead-in is prepended, matching the AWGN path; an impulsive burst at sample 0 can therefore block VIS lock.",
                  ],
            CorpusSkippedFiles: corpus?.SkippedFiles);
    }

    private static double? ToDb(double value) => value <= 0 ? null : 20.0 * Math.Log10(value);

    private sealed record MeasurementBand(string Name, double LowHz, double HighHz, double[] Coefficients);

    /// <summary>One prepared noise realization, shared across every mode and every SNR point.
    ///
    /// Each band is filtered ONCE over the whole stream and reduced to a prefix sum of squares, so a
    /// mode's own slice RMS is an O(1) lookup afterwards. Recomputing it per mode per point would cost
    /// thousands of full-length filter passes.
    ///
    /// Scaling is applied at mix time, not here: band power is linear in the scale factor, so one
    /// unscaled measurement serves every SNR point. That also gives the "same realization, only the
    /// level changes" property the monotone-from-top floor depends on.</summary>
    private sealed class NoiseRealization
    {
        private readonly float[] _samples;
        private readonly Dictionary<string, double[]> _prefixSquares;

        private NoiseRealization(
            int seed, float[] samples, Dictionary<string, double[]> prefixSquares,
            string? day, string? receiver, IReadOnlyList<string> fileNames,
            IReadOnlyList<double> joinOffsets, double? spreadDb, bool? spreadWarning)
        {
            Seed = seed;
            _samples = samples;
            _prefixSquares = prefixSquares;
            Day = day;
            Receiver = receiver;
            FileNames = fileNames;
            JoinOffsetsSeconds = joinOffsets;
            ClipRmsSpreadDb = spreadDb;
            ClipRmsSpreadWarning = spreadWarning;
        }

        public int Seed { get; }

        public int SampleCount => _samples.Length;

        /// <summary>Kurtosis and crest factor of this realization. The control arm's crest shifts
        /// slightly under band-limiting, because filtering correlates neighbouring samples -- that is
        /// expected, and worth recording rather than being surprised by later.</summary>
        public string Statistics
        {
            get
            {
                var rms = FirFilter.Rms(_samples);
                double fourth = 0;
                double peak = 0;
                foreach (var sample in _samples)
                {
                    var normalized = sample / rms;
                    fourth += normalized * normalized * normalized * normalized;
                    peak = Math.Max(peak, Math.Abs(sample));
                }

                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"seed={Seed} kurtosis={fourth / _samples.Length:F3} crest={peak / rms:F2}");
            }
        }

        public string? Day { get; }

        public string? Receiver { get; }

        public IReadOnlyList<string> FileNames { get; }

        public IReadOnlyList<double> JoinOffsetsSeconds { get; }

        public double? ClipRmsSpreadDb { get; }

        public bool? ClipRmsSpreadWarning { get; }

        public static NoiseRealization Prepare(
            int seed, int seedOrdinal, double seconds, int sampleRate,
            IReadOnlyList<MeasurementBand> bands, RealNoiseCorpus? corpus)
        {
            float[] samples;
            string? day = null;
            string? receiver = null;
            IReadOnlyList<string> fileNames = [];
            IReadOnlyList<double> joins = [];
            double? spread = null;
            bool? spreadWarning = null;

            if (corpus is not null)
            {
                var buffer = RealNoiseStreamBuilder.Build(corpus, seed, seedOrdinal, seconds);
                samples = buffer.Samples;
                day = buffer.Day;
                receiver = buffer.Receiver;
                fileNames = buffer.FileNames;
                joins = buffer.JoinOffsetsSeconds;
                spread = buffer.ClipRmsSpreadDb;
                spreadWarning = buffer.ClipRmsSpreadWarning;
            }
            else
            {
                // The control arm: Gaussian, then low-passed to the corpus's own bandwidth so the two
                // arms differ ONLY in amplitude statistic. A linear filter of Gaussian noise is still
                // Gaussian, so this stays a valid Gaussian control.
                //
                // The band-limiter's warm-up is generated and then discarded, matching the trim the
                // real arm already applies to its resampler transient. Left in, a few ms of
                // ramped-up noise would sit at sample 0 -- exactly where VIS lock happens -- and make
                // the control look marginally better than the real arm for no physical reason.
                var warmup = (MeasurementTaps - 1) / 2;
                var count = (int)(seconds * sampleRate);
                var raw = Gaussian(seed, count + (warmup * 2));
                var filtered = FirFilter.Apply(raw, FirFilter.DesignLowPass(CorpusBandwidthHz, sampleRate, MeasurementTaps));
                samples = filtered[warmup..(warmup + count)];
            }

            var prefixSquares = new Dictionary<string, double[]>(StringComparer.Ordinal)
            {
                ["total"] = PrefixSquares(samples),
            };
            foreach (var band in bands)
            {
                prefixSquares[band.Name] = PrefixSquares(FirFilter.Apply(samples, band.Coefficients));
            }

            return new NoiseRealization(seed, samples, prefixSquares, day, receiver, fileNames, joins, spread, spreadWarning);
        }

        public double BandRms(string band, int count) =>
            Math.Sqrt(_prefixSquares[band][count] / count);

        public float[] Mix(IReadOnlyList<float> signal, double scale)
        {
            var mixed = new float[signal.Count];
            for (var i = 0; i < signal.Count; i++)
            {
                mixed[i] = (float)(signal[i] + (_samples[i] * scale));
            }

            return mixed;
        }

        private static double[] PrefixSquares(float[] samples)
        {
            var prefix = new double[samples.Length + 1];
            for (var i = 0; i < samples.Length; i++)
            {
                prefix[i + 1] = prefix[i] + ((double)samples[i] * samples[i]);
            }

            return prefix;
        }

        private static float[] Gaussian(int seed, int count)
        {
            var random = new Random(seed);
            var samples = new float[count];
            for (var i = 0; i < count; i++)
            {
                var u1 = 1.0 - random.NextDouble();
                var u2 = random.NextDouble();
                samples[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
            }

            return samples;
        }
    }
}

/// <summary>Needs BOTH the corpus path and an explicit opt-in. A mounted external drive must not
/// silently start a multi-hour sweep, which is why directory presence alone is not the gate here even
/// though <see cref="RequiresOtaRecordingsFactAttribute"/> uses that convention.</summary>
public sealed class RequiresRealNoiseSweepFactAttribute : FactAttribute
{
    public RequiresRealNoiseSweepFactAttribute()
    {
        var dir = Environment.GetEnvironmentVariable("SCANLINE_REAL_NOISE_DIR");
        if (Environment.GetEnvironmentVariable("SCANLINE_RUN_REAL_NOISE_SWEEP") != "1")
        {
            Skip = "Set SCANLINE_RUN_REAL_NOISE_SWEEP=1 (and SCANLINE_REAL_NOISE_DIR) to run the real-HF-noise sweep.";
        }
        else if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            Skip = "Set SCANLINE_REAL_NOISE_DIR to the recorded HF noise corpus directory.";
        }
    }
}

public sealed class RequiresBandLimitedAwgnSweepFactAttribute : FactAttribute
{
    public RequiresBandLimitedAwgnSweepFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SCANLINE_RUN_AWGN_INBAND_SWEEP") != "1")
        {
            Skip = "Set SCANLINE_RUN_AWGN_INBAND_SWEEP=1 to run the band-limited Gaussian control sweep.";
        }
    }
}
