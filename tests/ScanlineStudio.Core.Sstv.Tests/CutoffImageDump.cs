using System.Globalization;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>Writes decoded pictures for eyeball review, because a mean-absolute-delta cannot see
/// blur and has already, in this project, called a change large that looked identical on screen.
///
/// No viewer, no comparison logic, no assertions: it writes .bmp files with self-describing names
/// into one directory and prints the path. The source image is written alongside as the reference.
///
/// Cutoffs come from SCANLINE_CUTOFFS (comma-separated Hz) so the ladder's measured optimum can be
/// dumped without editing this file.</summary>
public sealed class CutoffImageDump
{
    private const int SampleRate = 44100;
    private const double H2LowHz = 400;
    private const double H2HighHz = 2500;
    private const int MeasurementTaps = 1023;

    [RequiresDemodulatorRankingFact]
    public async Task DumpDecodedImagesAcrossCutoffs()
    {
        var corpus = RealNoiseCorpus.Load(Environment.GetEnvironmentVariable("SCANLINE_REAL_NOISE_DIR")!);
        var h2 = FirFilter.DesignBandPass(H2LowHz, H2HighHz, SampleRate, MeasurementTaps);
        var encoder = new AnalogFmSstvEncoder(SampleRate);

        var outputDir = Path.Combine(
            ImpairmentSweepHarness.FindRepoRoot(),
            "impairment-reports",
            string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}-cutoff-images"));
        Directory.CreateDirectory(outputDir);

        var modeIds = (Environment.GetEnvironmentVariable("SCANLINE_IMPAIRMENT_MODES") ?? "scottie-dx,scottie-s1,robot-36")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cutoffs = (Environment.GetEnvironmentVariable("SCANLINE_CUTOFFS") ?? "300,600,900,1800")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var snrLevels = (Environment.GetEnvironmentVariable("SCANLINE_SNRS") ?? "16,12")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();

        var longest = SstvModeRegistry.All.Where(m => modeIds.Contains(m.Id)).Max(m => m.LineDurationMs * m.ImageHeight / 1000.0);
        var noise = RealNoiseStreamBuilder.Build(corpus, seed: 12345, seedOrdinal: 0, requiredSeconds: (longest * 1.25) + 15.0);
        Console.WriteLine($"noise: seed 12345, {noise.Day}/{noise.Receiver}");

        foreach (var modeId in modeIds)
        {
            var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);
            // SCANLINE_SOURCE_BMP overrides the per-mode fixture. It exists because EVERY
            // golden-vector fixture is the same smooth gradient (mean horizontal pixel delta 0.24,
            // against 43-54 for a real received picture), so a change that merely blurs scores better
            // and the metric cannot tell smooth from correct. Point this at testcard.bmp to measure
            // resolution loss instead of noise removal.
            var overrideBmp = Environment.GetEnvironmentVariable("SCANLINE_SOURCE_BMP");
            var fixture = ImpairmentSweepHarness.RealFixtureModes.FirstOrDefault(f => f.ModeId == modeId);
            // SCANLINE_SOURCE_BMP is a STEM, not a filename: "testcard" resolves to
            // testcard-320x240.bmp for a 240-line mode and testcard-320x256.bmp for a 256-line one.
            // Each size is generated at its own dimensions rather than resampled from one master,
            // because resampling would move the card's gratings off their exact 2/3/4/6/8/12-pixel
            // periods -- and those exact periods are the entire reason the card exists.
            // A rooted stem is used as-is, so a photographic source can live OUTSIDE the repo, the
            // same posture as the HF noise corpus. Nothing whose licence needs auditing is bundled.
            var sourcePath = string.IsNullOrEmpty(overrideBmp)
                ? null
                : $"{overrideBmp}-{mode.ImageWidth}x{mode.ImageHeight}.bmp";
            IImageSource source = sourcePath is not null
                ? BmpFile.Read(Path.IsPathRooted(sourcePath) ? sourcePath : Path.Combine(ImpairmentSweepHarness.FixtureDir, sourcePath))
                : fixture.SourceBmp is not null
                    ? BmpFile.Read(Path.Combine(ImpairmentSweepHarness.FixtureDir, fixture.SourceBmp))
                    : ImpairmentSweepHarness.CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

            BmpFile.Write(Path.Combine(outputDir, $"{modeId}__SOURCE.bmp"), Snapshot(source));

            var encoded = new List<float>();
            await foreach (var sample in encoder.EncodeAsync(mode, source))
            {
                encoded.Add(sample);
            }

            var clean = encoded.ToArray();
            encoded.Clear();
            var signalInBand = FirFilter.BandRms(clean, h2);

            foreach (var snrDb in snrLevels)
            {
                var scale = signalInBand / Math.Pow(10.0, snrDb / 20.0) / FirFilter.BandRms(noise.Samples[..clean.Length], h2);
                var noisy = new float[clean.Length];
                for (var i = 0; i < clean.Length; i++)
                {
                    noisy[i] = (float)(clean[i] + (noise.Samples[i] * scale));
                }

                foreach (var demod in new[] { DemodType.Hilbert, DemodType.Pll, DemodType.ZeroCrossing })
                {
                    foreach (var cutoff in cutoffs)
                    {
                        // Each demodulator has its own output-cutoff parameter; there is no shared one.
                        var decoder = demod switch
                        {
                            DemodType.Hilbert => new AnalogFmSstvDecoder(SampleRate, demodType: DemodType.Hilbert, hilbertOutputCutoffHz: cutoff),
                            DemodType.Pll => new AnalogFmSstvDecoder(SampleRate, demodType: DemodType.Pll, pllOutputCutoffHz: cutoff),
                            _ => new AnalogFmSstvDecoder(SampleRate, demodType: DemodType.ZeroCrossing, zeroCrossingOutputCutoffHz: cutoff),
                        };

                        IImageSource? image = null;
                        SstvModeDefinition? detected = null;
                        decoder.ModeDetected += m => detected = m;
                        decoder.LineDecoded += u => image = u.Image;
                        decoder.PushSamples(noisy);

                        var name = string.Create(CultureInfo.InvariantCulture,
                            $"{modeId}__snr{snrDb:F0}dB__{demod}__{cutoff:F0}Hz.bmp");
                        if (detected?.Id != mode.Id || image is null)
                        {
                            Console.WriteLine($"  NO LOCK: {name}");
                            continue;
                        }

                        BmpFile.Write(Path.Combine(outputDir, name), Snapshot(image));
                    }
                }

                Console.WriteLine($"  {modeId} @ {snrDb:F0}dB written");
            }
        }

        Console.WriteLine($"\nIMAGES WRITTEN TO: {outputDir}");
    }

    private static ArrayImageSource Snapshot(IImageSource image)
    {
        var pixels = new Rgb24[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            var line = image.GetScanline(y);
            for (var x = 0; x < image.Width; x++)
            {
                pixels[(y * image.Width) + x] = line[x];
            }
        }

        return new ArrayImageSource(image.Width, image.Height, pixels);
    }
}
