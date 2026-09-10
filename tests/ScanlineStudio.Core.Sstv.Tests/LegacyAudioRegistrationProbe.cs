using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Measures horizontal registration against REAL LEGACY AUDIO, which
/// <see cref="HorizontalRegistrationProbe"/> structurally cannot.
///
/// <para><b>Why this exists.</b> That probe encodes with this port's encoder and decodes with this
/// port's decoder, so every number it produces is a TX+RX composite. A per-mode error in our own
/// TRANSMITTER is indistinguishable from a receive-side registration error in such a measurement, and
/// a correction table derived from it would enshrine a TX bug as an RX constant — misregistering real
/// off-air signals in the opposite direction. That is the recorded Scottie incident exactly
/// (`CLAUDE.md` §4): encoder and decoder agreeing with each other while both are wrong about
/// reality.</para>
///
/// <para><b>What breaks the circle.</b> `Fixtures/GoldenVectors/` holds real audio captured from a
/// running legacy install (`.mmv`), the exact source image legacy transmitted (`.bmp`), AND legacy's
/// own decode of that audio (`_RX.bmp`). Our decoder never touches our encoder here. Three
/// registrations become comparable:</para>
///
/// <list type="bullet">
/// <item><b>Ours</b> — our decode of legacy's audio, against the source legacy actually sent.</item>
/// <item><b>Legacy's</b> — legacy's own decode of the same audio, against the same source.</item>
/// <item><b>The difference</b> — how far this port's receive path sits from legacy's. THAT is the
/// only number a receive-side correction may be built from.</item>
/// </list>
///
/// <para>If ours and legacy's agree, the offset is legacy's own registration behaviour and correcting
/// it is a `CLAUDE.md` §0a improvement over legacy. If they differ, the difference is a port defect
/// and must be fixed as one.</para>
///
/// <para>This is a measurement. It changes no shipping code.</para>
/// </summary>
public sealed class LegacyAudioRegistrationProbe
{
    // Every mode with real legacy audio AND legacy's own decode beside it.
    private static readonly string[] CapturedModes =
        ["robot36", "robot72", "martin-m1", "scottie-s1", "pd90", "rm8", "mn110", "avt"];

    private static readonly Dictionary<string, string> FixtureToModeId = new(StringComparer.Ordinal)
    {
        ["robot36"] = "robot-36",
        ["robot72"] = "robot-72",
        ["martin-m1"] = "martin-m1",
        ["scottie-s1"] = "scottie-s1",
        ["pd90"] = "pd90",
        ["rm8"] = "rm8",
        ["mn110"] = "mn110",
        ["avt"] = "avt",
    };

    private const double SearchRadiusPixels = 14.0;
    private const double SearchStep = 0.05;

    [Fact]
    public void RegistrationAgainstLegacyAudio_SeparatesPortDefectFromLegacyBehaviour()
    {
        var directory = FindFixtureDirectory();

        var rows = new List<string>
        {
            "| mode | ours (px) | legacy (px) | ours - legacy | ours (ms) | verdict |",
            "|---|---|---|---|---|---|",
        };

        foreach (var fixture in CapturedModes)
        {
            var modeId = FixtureToModeId[fixture];
            var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);

            var sourcePath = Path.Combine(directory, $"{fixture}.bmp");
            var audioPath = Path.Combine(directory, $"{fixture}.mmv");
            var legacyDecodePath = Path.Combine(directory, $"{fixture}_RX.bmp");

            if (!File.Exists(sourcePath) || !File.Exists(audioPath) || !File.Exists(legacyDecodePath))
            {
                rows.Add($"| {modeId} | (fixture missing) | | | | |");
                continue;
            }

            var source = BmpFile.Read(sourcePath);
            var legacyDecode = BmpFile.Read(legacyDecodePath);
            var (samples, sampleRate) = MmvFile.Read(audioPath);

            var ourDecode = DecodeLegacyAudio(mode, samples, sampleRate);
            if (ourDecode is null)
            {
                rows.Add($"| {modeId} | OUR DECODE FAILED | | | | |");
                continue;
            }

            var ours = MeanHorizontalShift(source, ourDecode, mode);
            var legacy = MeanHorizontalShift(source, legacyDecode, mode);

            if (ours is null || legacy is null)
            {
                rows.Add($"| {modeId} | {(ours is null ? "no usable rows" : "ours ok")} | "
                    + $"{(legacy is null ? "no usable rows" : "legacy ok")} | | | |");
                continue;
            }

            var lastScan = mode.LineSegments.OfType<ScanSegment>().Last();
            var pitchMs = lastScan.DurationMs / mode.ImageWidth;
            var difference = ours.Value - legacy.Value;

            var verdict = Math.Abs(difference) < 0.25
                ? "matches legacy -- §0a candidate"
                : "DIVERGES from legacy -- port defect";

            rows.Add(
                $"| {modeId} | {ours.Value:F2} | {legacy.Value:F2} | **{difference:F2}** | "
                + $"{ours.Value * pitchMs:F3} | {verdict} |");
        }

        Assert.Fail(
            "Horizontal registration against REAL LEGACY AUDIO. Negative means the decode lands LEFT\n"
            + "of the transmitted source, i.e. the reads are LATE.\n\n"
            + "'ours' and 'legacy' both decode the SAME legacy-captured audio, so this measurement\n"
            + "contains no contribution from this port's encoder.\n\n"
            + string.Join("\n", rows));
    }

    /// <summary>
    /// Best sub-pixel horizontal shift aligning the decode to the source, by least squares over the
    /// row. Content-agnostic on purpose: the fixtures are gradients today, but a correlation fit does
    /// not care, and a formula tied to the gradient would silently rot if a fixture is replaced.
    /// </summary>
    private static double? MeanHorizontalShift(IImageSource source, IImageSource decoded, SstvModeDefinition mode)
    {
        var rows = Math.Min(mode.ImageHeight, Math.Min(source.Height, decoded.Height));
        if (rows < 8)
        {
            return null;
        }

        // Skip the outer eighth: those columns carry the very edge contamination under investigation,
        // and letting them vote would bias the alignment they are supposed to be measured against.
        var margin = Math.Max(8, mode.ImageWidth / 8);
        var shifts = new List<double>();

        for (var y = rows / 4; y < rows * 3 / 4; y++)
        {
            var sourceLine = source.GetScanline(y).ToArray();
            var decodedLine = decoded.GetScanline(y).ToArray();

            var best = double.MaxValue;
            var bestShift = 0.0;
            for (var shift = -SearchRadiusPixels; shift <= SearchRadiusPixels; shift += SearchStep)
            {
                var error = 0.0;
                var counted = 0;
                for (var x = margin; x < mode.ImageWidth - margin; x++)
                {
                    var sampled = SampleLinear(sourceLine, x - shift);
                    if (sampled is null)
                    {
                        continue;
                    }

                    var delta = Luma(decodedLine[x]) - sampled.Value;
                    error += delta * delta;
                    counted++;
                }

                if (counted > 0 && error / counted < best)
                {
                    best = error / counted;
                    bestShift = shift;
                }
            }

            shifts.Add(bestShift);
        }

        if (shifts.Count == 0)
        {
            return null;
        }

        // Median, not mean: one badly decoded row must not drag the answer.
        shifts.Sort();
        return shifts[shifts.Count / 2];
    }

    private static double? SampleLinear(Rgb24[] line, double x)
    {
        if (x < 0 || x >= line.Length - 1)
        {
            return null;
        }

        var whole = (int)Math.Floor(x);
        var fraction = x - whole;
        return (Luma(line[whole]) * (1 - fraction)) + (Luma(line[whole + 1]) * fraction);
    }

    private static double Luma(Rgb24 pixel) => (pixel.R + pixel.G + pixel.B) / 3.0;

    private static IImageSource? DecodeLegacyAudio(SstvModeDefinition mode, float[] samples, int sampleRate)
    {
        var decoder = new AnalogFmSstvDecoder(sampleRate);
        SstvModeDefinition? detected = null;
        IImageSource? decodedImage = null;
        decoder.ModeDetected += m => detected = m;
        decoder.LineDecoded += update => decodedImage = update.Image;
        decoder.PushSamples(samples);

        return detected?.Id == mode.Id ? decodedImage : null;
    }

    private static string FindFixtureDirectory()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "Fixtures", "GoldenVectors");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException("Could not locate Fixtures/GoldenVectors.");
    }
}
