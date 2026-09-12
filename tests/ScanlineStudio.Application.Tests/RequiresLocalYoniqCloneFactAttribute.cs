namespace ScanlineStudio.Application.Tests;

/// <summary>Gates a test on the local, gitignored `yoniq-old/YONIQ-main/` clone (CLAUDE.md's
/// top-of-file note: "gitignored, never committed -- clone separately to inspect") being present on
/// THIS machine. Unlike this project's opt-in device-cost attributes (audio/OmniRig), no environment
/// variable is needed -- reading a handful of small, pre-existing local files has zero cost and no
/// side effect, so the only real gate is whether they exist here at all. Skips cleanly (not fails) on
/// any other machine or CI, where the clone is legitimately absent.</summary>
public sealed class RequiresLocalYoniqCloneFactAttribute : FactAttribute
{
    public RequiresLocalYoniqCloneFactAttribute()
    {
        if (LegacyMtmSampleFiles.FindYoniqOldDirectory() is null)
        {
            Skip = "Local-only: yoniq-old/YONIQ-main/ (gitignored) is not present on this machine. "
                + "Clone it separately (see CLAUDE.md's top-of-file note) to run this test.";
        }
    }
}

/// <summary>Locates the real, local, never-committed `.mtm` sample files this test suite validates
/// against -- separated from the test class itself so <see cref="RequiresLocalYoniqCloneFactAttribute"/>
/// can reuse the exact same lookup for its own presence check.</summary>
internal static class LegacyMtmSampleFiles
{
    /// <summary>Walks up from the test assembly's own output directory looking for a directory that
    /// itself contains <c>yoniq-old/YONIQ-main</c> -- the repo root, wherever this test happens to run
    /// from (`dotnet test`'s working directory is not reliably the repo root). Returns
    /// <see langword="null"/>, never throws, when not found -- the caller decides what that means
    /// (skip a test, or return an empty file list).</summary>
    public static string? FindYoniqOldDirectory()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "yoniq-old", "YONIQ-main");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    /// <summary>Every real `.mtm` sample known to exist locally as of the reverse-engineering pass
    /// that produced `docs/mtm-binary-format.md` -- both the `TemplateDir` and `Stock/` copies of the
    /// five `def`/`t` pairs plus the two singles, 24 CANDIDATE paths (12 names x 2 locations). Not
    /// every candidate exists on every machine (`Stock/` and the root copy can drift, e.g. only 19 of
    /// the 24 exist here) -- a listed path that doesn't exist is simply skipped by the caller, not a
    /// hard failure. This is a best-known list, not a contract this project ships or controls.</summary>
    public static IReadOnlyList<string> ListSampleFiles(string yoniqOldDirectory)
    {
        string[] relativePaths =
        [
            "def1.mtm", "def2.mtm", "def3.mtm", "def4.mtm", "def5.mtm",
            "t1.mtm", "t2.mtm", "t3.mtm", "t4.mtm", "t5.mtm",
            "Current.mtm", "List.mtm",
            "Stock/def1.mtm", "Stock/def2.mtm", "Stock/def3.mtm", "Stock/def4.mtm", "Stock/def5.mtm",
            "Stock/t1.mtm", "Stock/t2.mtm", "Stock/t3.mtm", "Stock/t4.mtm", "Stock/t5.mtm",
            "Stock/Current.mtm", "Stock/List.mtm",
        ];

        return relativePaths
            .Select(relative => Path.Combine(yoniqOldDirectory, relative.Replace('/', Path.DirectorySeparatorChar)))
            .Where(File.Exists)
            .ToList();
    }
}
