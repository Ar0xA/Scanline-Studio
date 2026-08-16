using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// spec/18-path-to-1.0.md Medium item: re-captures the 11 checked-in `TxCapture/*.mmv` golden-
/// vector fixtures against the CURRENT <see cref="AnalogFmSstvEncoder"/>. Deliberately gated behind
/// an explicit opt-in env var, not a plain always-running <see cref="FactAttribute"/> -- letting
/// this run on every ordinary test pass would (a) dirty the working tree / rewrite fixtures every
/// CI run, and (b) normalize "regenerate before reading," which is exactly the habit that let this
/// fixture family go stale the first time (spec/18-path-to-1.0.md's own diagnosis). Run explicitly,
/// as a separate step BEFORE rebuilding and re-running <see cref="TxCaptureFixturesTests"/>:
///
/// <code>
/// SCANLINE_REGENERATE_TX_FIXTURES=1 dotnet test tests/ScanlineStudio.Core.Sstv.Tests --filter TxCaptureFixtureGenerator
/// dotnet build tests/ScanlineStudio.Core.Sstv.Tests   # refreshes bin/'s CopyToOutputDirectory copies
/// dotnet test tests/ScanlineStudio.Core.Sstv.Tests --filter TxCaptureFixturesTests
/// </code>
///
/// A source-tree write mid-test-run does NOT retroactively refresh the CURRENT run's already-copied
/// `bin/` fixture files (`CopyToOutputDirectory` happens at build time) -- regeneration and
/// verification are genuinely two separate invocations, not one.
/// </summary>
public sealed class TxCaptureFixtureGenerator
{
    [RequiresTxFixtureRegenerationFact]
    public async Task RegenerateAllTxCaptureFixtures()
    {
        var repoRoot = FindRepoRoot();
        var fixtureDir = Path.Combine(repoRoot, "tests", "ScanlineStudio.Core.Sstv.Tests", "Fixtures", "GoldenVectors");
        var txCaptureDir = Path.Combine(fixtureDir, "TxCapture");

        foreach (var (modeId, sourceBmpFileName) in TxCaptureFixturesTests.Fixtures.Select(row => ((string)row[0]!, (string)row[1]!)))
        {
            var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);
            var source = BmpFile.Read(Path.Combine(fixtureDir, sourceBmpFileName));

            var encoder = new AnalogFmSstvEncoder(11025);

            var samples = new List<float>();
            var clippedCount = 0;
            var maxAbsSample = 0f;
            // Design point 6: pin stationId explicitly to None, matching the verification test --
            // a generator/test divergence here would change the segment stream and blow the exact-
            // sample-count assertion for a reason unrelated to real encoder fidelity.
            await foreach (var sample in encoder.EncodeAsync(mode, source, StationIdTransmitOptions.None))
            {
                var absSample = Math.Abs(sample);
                if (absSample > maxAbsSample)
                {
                    maxAbsSample = absSample;
                }

                if (absSample > 1.0f)
                {
                    clippedCount++;
                }

                samples.Add(sample);
            }

            var mmvPath = Path.Combine(txCaptureDir, $"{modeId}_TX.mmv");
            MmvFile.Write(mmvPath, samples.ToArray(), encoder.SampleRate);

            // A silent clip is exactly what manual review of a regenerated binary blob can't catch
            // -- TxOutputBandpassFilter is only unity in its PASSBAND, and FIR transient overshoot
            // at abrupt frequency steps can exceed +/-1.0 even though it didn't when these fixtures
            // were first captured (the filter was added afterward). Visible immediately, not buried.
            // Code-review finding: report max|sample| alongside the clip COUNT, not the count alone
            // -- what separates "benign passband ripple" from "real clipping distortion" is the
            // OVERSHOOT MAGNITUDE, not how many samples cross the line by an unknown amount. A
            // reader can sanity-check e.g. "7% of samples clipped, max magnitude 1.0074" as ~0.7%
            // peak overshoot (consistent with normal Kaiser-window ripple), vs. a max magnitude of
            // say 1.5 or 2.0, which would mean something is actually wrong.
            Console.WriteLine($"{modeId}: {samples.Count} samples written to {mmvPath}, {clippedCount} clipped, max|sample|={maxAbsSample:F4}.");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScanlineStudio.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root (ScanlineStudio.sln) from test output directory.");
        }

        return dir.FullName;
    }
}

/// <summary>Code-review finding: a plain <see cref="FactAttribute"/> with an early-return when not
/// opted in reports as a green PASS regardless -- an env-var typo or a misconfigured shell would
/// silently look identical to a real regeneration having happened (the same "convincing no-op"
/// failure class the source-tree-path fix elsewhere in this file already guards against). Mirrors
/// <c>RequiresPipeWireFactAttribute</c>'s own dynamic-<see cref="FactAttribute.Skip"/> pattern
/// (`ScanlineStudio.Core.Audio.MiniAudio.Tests`) so an un-opted-in run reports as an honest SKIP
/// with an actionable message, not a silent PASS.</summary>
public sealed class RequiresTxFixtureRegenerationFactAttribute : FactAttribute
{
    public RequiresTxFixtureRegenerationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SCANLINE_REGENERATE_TX_FIXTURES") != "1")
        {
            Skip = "Set SCANLINE_REGENERATE_TX_FIXTURES=1 to regenerate the TxCapture/*.mmv fixtures against the current encoder.";
        }
    }
}
