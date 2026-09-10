using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// `BACKLOG.md` item D1 — the whole-image ideal-transport probe for the green cast documented in
/// `docs/known-decode-defects.md` §1.
///
/// <para>§1 decomposes the cast into two faults. Fault A (about +2.3) is reproduced with no signal
/// path at all, by modelling the colour maths over random RGB triples, and is a deliberate
/// legacy-parity decision — legacy truncates identically. Fault B (about +2.4) has never been
/// attributed to a side. This probe attributes it.</para>
///
/// <para><b>Method.</b> Take the real encoder's per-line frequency sequence and hand it straight to
/// the matching decoder, with NO modulation, filtering or demodulation in between — the decoder's
/// "demodulated Hz at sample i" is exactly the Hz the encoder asked for at that instant. Everything
/// that is not the colour maths and the channel layout is therefore removed. What survives is
/// protocol-side or TX-side by construction.</para>
///
/// <para><b>Reading the result.</b> A YCbCr green bias near the +4.4 to +4.8 that §1 measured over
/// the real DSP chain means Fault B is also protocol/TX-side, and the whole cast closes as a parity
/// decision like Fault A. A bias near +2.3 — Fault A alone — means Fault B is lost somewhere in the
/// DSP chain and earns its own investigation at the full review cadence.</para>
///
/// <para><b>The RGB-sequential modes are this harness's own falsifier.</b> §1 measures them at +0.1,
/// because all three channels move together there. If they do not come out near zero here, the
/// harness is wrong and no YCbCr number it prints can be trusted.</para>
///
/// Gated behind <c>SCANLINE_SOURCE_BMP</c> like every other harness in this project: §1's numbers
/// were measured on a photographic source, and photographic sources live outside the repo so nothing
/// whose licence needs auditing is bundled. Point it at a stem, e.g.
/// <c>SCANLINE_SOURCE_BMP=$HOME/sstv-test-images/photo-city</c>.
/// </summary>
public sealed class IdealTransportGreenBiasProbe
{
    // Every mode §1 measured, so the printed table lines up against its own table row by row.
    // The two RgbSequential entries are controls, not subjects -- see the class doc comment.
    private static readonly string[] ProbeModeIds =
        ["robot-36", "r24", "robot-72", "pd90", "mn110", "martin-m1", "scottie-s1"];

    [RequiresPhotographicSourceFact]
    public void IdealTransport_AttributesFaultB()
    {
        var stem = Environment.GetEnvironmentVariable("SCANLINE_SOURCE_BMP")!;

        var lines = new List<string>
        {
            "| mode | colour encoding | green bias, ideal transport | §1's real-chain figure |",
            "|---|---|---|---|",
        };

        var expectedFromSectionOne = new Dictionary<string, string>
        {
            ["robot-36"] = "+4.7",
            ["r24"] = "+4.8",
            ["robot-72"] = "+4.4",
            ["pd90"] = "+4.4",
            ["mn110"] = "+3.8",
            ["martin-m1"] = "+0.1",
            ["scottie-s1"] = "+0.1",
        };

        foreach (var modeId in ProbeModeIds)
        {
            var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);
            var sourcePath = $"{stem}-{mode.ImageWidth}x{mode.ImageHeight}.bmp";
            if (!File.Exists(sourcePath))
            {
                lines.Add($"| {modeId} | {mode.ColorEncoding} | (no {mode.ImageWidth}x{mode.ImageHeight} source) | {expectedFromSectionOne[modeId]} |");
                continue;
            }

            var source = BmpFile.Read(sourcePath);
            var decoded = IdealTransport.Decode(mode, source);
            var bias = MeasureGreenBias(source, decoded, mode.ImageHeight);

            lines.Add($"| {modeId} | {mode.ColorEncoding} | {bias:+0.0;-0.0} | {expectedFromSectionOne[modeId]} |");
        }

        // The probe's output IS the deliverable -- there is no pass/fail threshold to assert,
        // because the number is what decides whether item D1 closes as a parity decision or opens
        // a DSP investigation. Assert.Fail is how xunit surfaces it; this is a measurement run,
        // not a regression gate, which is why it skips by default.
        Assert.Fail(string.Join(Environment.NewLine, lines));
    }

    /// <summary>
    /// §1's own metric, reproduced exactly: <c>G_err - (R_err + B_err)/2</c>, where each err is the
    /// mean signed per-channel difference between the decode and the source.
    /// </summary>
    private static double MeasureGreenBias(IImageSource source, IImageSource decoded, int height)
    {
        double rSum = 0, gSum = 0, bSum = 0;
        var count = 0;

        for (var y = 0; y < height; y++)
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

        return (gSum / count) - (((rSum / count) + (bSum / count)) / 2.0);
    }
}

/// <summary>Same env-var gating shape as <see cref="RequiresDemodulatorRankingFactAttribute"/> —
/// photographic sources live outside the repo, so this probe skips honestly rather than failing on
/// a machine that does not have them.</summary>
public sealed class RequiresPhotographicSourceFactAttribute : FactAttribute
{
    public RequiresPhotographicSourceFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SCANLINE_SOURCE_BMP")))
        {
            Skip = "Set SCANLINE_SOURCE_BMP to a photographic source stem (e.g. ~/sstv-test-images/photo-city) to run this probe.";
        }
    }
}
