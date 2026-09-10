using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// `BACKLOG.md` D1, first step: does LEGACY have the green cast too?
///
/// <para><b>Why this runs before anything else.</b> The D2 edge-stripe investigation spent a very
/// long time attributing an artefact to this port, built four fix designs, and only late on measured
/// legacy's own decodes — which carried the same artefact on all 8 captured modes. That one cheap
/// check would have closed the item on day one. This applies the lesson to D1 before any DSP work is
/// committed to.</para>
///
/// <para><b>Three numbers per mode, one input.</b> `Fixtures/GoldenVectors/` holds real audio captured
/// from a running legacy install, the source image legacy transmitted, and legacy's own decode of that
/// audio. Decoding that same audio with this port gives an apples-to-apples comparison that involves
/// neither our encoder nor any synthetic stimulus:</para>
///
/// <list type="bullet">
/// <item><b>legacy</b> — legacy's own decode against the source it sent.</item>
/// <item><b>ours</b> — our decode of the same audio against the same source.</item>
/// <item><b>ours − legacy</b> — the only figure attributable to this port.</item>
/// </list>
///
/// <para><b>Reading the result.</b> If legacy shows a comparable bias, Fault B is parity and D1
/// becomes a §0a improvement decision rather than a defect. If legacy is clean and we are not, the
/// difference is a genuine port defect in the DSP chain and needs no improvement argument.</para>
///
/// <para>Metric is `docs/known-decode-defects.md` §1's own, reproduced exactly:
/// <c>G_err - (R_err + B_err)/2</c>, each err being the mean SIGNED per-channel difference between
/// decode and source. Same metric as <see cref="IdealTransportGreenBiasProbe"/>, so all three columns
/// are directly comparable to §1's table.</para>
///
/// <para>This is a measurement. It changes no shipping code.</para>
/// </summary>
public sealed class LegacyGreenBiasProbe
{
    // Fixture stem -> registry id. Four YCbCr subjects, then the two RGB-sequential controls that
    // validate the metric: §1 documents those at about +0.1, so a large reading there means the
    // measurement is wrong rather than the decoder.
    private static readonly (string Fixture, string ModeId, string Role)[] Subjects =
    [
        ("robot36", "robot-36", "YCbCr subject"),
        ("robot72", "robot-72", "YCbCr subject"),
        ("pd90", "pd90", "YCbCr subject"),
        ("mn110", "mn110", "YCbCr subject"),
        ("martin-m1", "martin-m1", "RGB control"),
        ("scottie-s1", "scottie-s1", "RGB control"),
    ];

    // What section 1 recorded for this port's real chain, for comparison.
    private static readonly Dictionary<string, string> SectionOneRealChain = new(StringComparer.Ordinal)
    {
        ["robot-36"] = "+4.7",
        ["robot-72"] = "+4.6",
        ["pd90"] = "+4.5",
        ["mn110"] = "+4.4",
        ["martin-m1"] = "+0.1",
        ["scottie-s1"] = "+0.1",
    };

    [Fact]
    public void GreenBias_OnLegacyAudio_ShowsWhetherFaultBIsOursOrLegacys()
    {
        var directory = FindFixtureDirectory();

        var rows = new List<string>
        {
            "| mode | role | legacy | ours | ours - legacy | §1 real chain |",
            "|---|---|---|---|---|---|",
        };

        foreach (var (fixture, modeId, role) in Subjects)
        {
            var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);
            var sourcePath = Path.Combine(directory, $"{fixture}.bmp");
            var audioPath = Path.Combine(directory, $"{fixture}.mmv");
            var legacyPath = Path.Combine(directory, $"{fixture}_RX.bmp");

            if (!File.Exists(sourcePath) || !File.Exists(audioPath) || !File.Exists(legacyPath))
            {
                rows.Add($"| {modeId} | {role} | (fixture missing) | | | |");
                continue;
            }

            var source = BmpFile.Read(sourcePath);
            var legacyDecode = BmpFile.Read(legacyPath);
            var (samples, sampleRate) = MmvFile.Read(audioPath);

            var ourDecode = DecodeLegacyAudio(mode, samples, sampleRate);
            if (ourDecode is null)
            {
                rows.Add($"| {modeId} | {role} | | OUR DECODE FAILED | | |");
                continue;
            }

            var legacyBias = MeasureGreenBias(source, legacyDecode, mode.ImageHeight);
            var ourBias = MeasureGreenBias(source, ourDecode, mode.ImageHeight);

            rows.Add(
                $"| {modeId} | {role} | {legacyBias:+0.0;-0.0} | {ourBias:+0.0;-0.0} | "
                + $"**{ourBias - legacyBias:+0.0;-0.0}** | {SectionOneRealChain[modeId]} |");
        }

        Assert.Fail(
            "Green bias (docs/known-decode-defects.md §1 Fault B) on REAL LEGACY AUDIO.\n"
            + "Metric: G_err - (R_err + B_err)/2, mean signed difference against the transmitted source.\n"
            + "'legacy' and 'ours' decode the SAME audio, so 'ours - legacy' is the only column\n"
            + "attributable to this port. A comparable legacy bias makes Fault B a parity question.\n\n"
            + string.Join("\n", rows));
    }

    /// <summary>§1's own metric, reproduced exactly.</summary>
    private static double MeasureGreenBias(IImageSource source, IImageSource decoded, int height)
    {
        double rSum = 0, gSum = 0, bSum = 0;
        var count = 0;
        var rows = Math.Min(height, Math.Min(source.Height, decoded.Height));

        for (var y = 0; y < rows; y++)
        {
            var sourceLine = source.GetScanline(y);
            var decodedLine = decoded.GetScanline(y);
            for (var x = 0; x < source.Width; x++)
            {
                rSum += decodedLine[x].R - sourceLine[x].R;
                gSum += decodedLine[x].G - sourceLine[x].G;
                bSum += decodedLine[x].B - sourceLine[x].B;
                count++;
            }
        }

        if (count == 0)
        {
            return double.NaN;
        }

        return (gSum / count) - (((rSum / count) + (bSum / count)) / 2.0);
    }

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
