using System.Text.Json;

namespace Yoniq.Core.Localization.Tests;

/// <summary>The CI-style guard spec/10-localization.md's Testing section calls for: every non-English
/// locale's key set must be a subset of <c>en.json</c>'s (catches stale/renamed keys), reporting
/// missing-key coverage rather than letting a translation silently drift. Runs against this repo's
/// real <c>assets/locale/</c> directory, not a fixture copy — so it actually gates content as it's
/// added, per spec/14-roadmap.md's "in place from the start" requirement, not just JSON some fixture
/// pretends is representative.</summary>
public sealed class LocaleFileIntegrityTests
{
    private static readonly string LocaleDirectory = FindLocaleDirectory();

    [Fact]
    public void EveryNonEnglishLocale_KeySetIsSubsetOfEnglish()
    {
        var englishKeys = LoadKeys("en");

        foreach (var file in Directory.GetFiles(LocaleDirectory, "*.json"))
        {
            var code = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(code, "en", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "locales", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var keys = LoadKeys(code);
            var orphaned = keys.Except(englishKeys).ToList();
            Assert.True(orphaned.Count == 0,
                $"Locale '{code}' has {orphaned.Count} key(s) not present in en.json: {string.Join(", ", orphaned)}");
        }
    }

    private static HashSet<string> LoadKeys(string code)
    {
        // Plain (non-source-generated) deserialization here is fine -- this is test-only code, not
        // the hot path LocalizationJsonContext exists for, and LocalizationJsonContext is internal
        // to Yoniq.Core.Localization on purpose (matches AppSettingsJsonContext's own convention).
        var path = Path.Combine(LocaleDirectory, $"{code}.json");
        using var stream = File.OpenRead(path);
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        return map is null ? [] : new HashSet<string>(map.Keys, StringComparer.Ordinal);
    }

    private static string FindLocaleDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yoniq.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root (Yoniq.sln) from test output directory.");
        }

        return Path.Combine(dir.FullName, "assets", "locale");
    }
}
