using System.Security.Cryptography;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>Test-suite fixes phase 1, item 11: a <see cref="TheoryAttribute"/> subclass (which
/// still honors <c>Skip</c>, since <see cref="TheoryAttribute"/> derives from
/// <see cref="FactAttribute"/>) that skips
/// <see cref="GoldenVectorTests.TxCaptureFixtureBmps_AreInternallyConsistent"/> entirely whenever
/// any of the checked-in <c>*_TX.provenance</c> sentinel files doesn't match its corresponding
/// <c>*_TX.mmv</c> fixture's real SHA-256 -- which is every mode today, since the provenance files
/// are seeded with an unmatchable sentinel value pending a real human re-capture session
/// (TxCapture/README.md's own "What to do" section).
///
/// xUnit 2.5.3's <c>TheoryDiscoverer</c> short-circuits on a non-null <c>Skip</c> and emits exactly
/// ONE skipped test case for the whole method, never enumerating <c>[MemberData]</c> -- so this
/// produces one skipped theory naming every stale mode, not 11 individually skipped cases. A
/// per-row skip would need the <c>Xunit.SkippableFact</c> package, not worth adding since all 11
/// modes share one sentinel-pending state today; revisit if a future re-capture session becomes
/// partial (some modes refreshed, some not).</summary>
public sealed class StaleFixtureTheoryAttribute : TheoryAttribute
{
    public StaleFixtureTheoryAttribute()
    {
        var staleModes = StaleModes.Value;
        if (staleModes.Count > 0)
        {
            Skip = "TX capture fixtures stale for: " + string.Join(", ", staleModes) +
                " -- re-capture per Fixtures/GoldenVectors/TxCapture/README.md, then replace each " +
                "mode's *_TX.provenance file with its real *_TX.mmv SHA-256 (do NOT hash the " +
                "current .mmv against itself -- that's what the round-1 version of this test " +
                "silently did, producing a permanently-green, permanently-lying result).";
        }
    }

    /// <summary>Computed once per test process, matching this project's own established
    /// <c>Lazy&lt;bool&gt;</c>-probe convention (e.g. <c>RigctldAvailabilityProbe</c> in
    /// <c>Core.Radio.Tests</c>) -- xUnit constructs a fresh attribute instance per discovered test,
    /// and re-hashing every <c>.mmv</c> file per instance would be wasteful.</summary>
    private static readonly Lazy<IReadOnlyList<string>> StaleModes = new(ComputeStaleModes);

    private static IReadOnlyList<string> ComputeStaleModes()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "GoldenVectors", "TxCapture");
        var stale = new List<string>();

        foreach (var row in GoldenVectorTests.TxFixtures)
        {
            var modeId = (string)row[0]!;
            var mmvPath = Path.Combine(directory, $"{modeId}_TX.mmv");
            var provenancePath = Path.Combine(directory, $"{modeId}_TX.provenance");

            var recordedHash = File.Exists(provenancePath) ? File.ReadAllText(provenancePath).Trim() : null;
            var actualHash = File.Exists(mmvPath) ? ComputeSha256(mmvPath) : null;

            if (recordedHash is null || actualHash is null ||
                !string.Equals(recordedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                stale.Add(modeId);
            }
        }

        return stale;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }
}
