using System.Text.RegularExpressions;

namespace ScanlineStudio.UI.Tests;

/// <summary>The grep-based CI step spec/10-localization.md's Testing section calls for in lieu of a
/// real analyzer: no <c>.axaml</c> file may set <c>Content=</c>/<c>Text=</c>/<c>Header=</c> to a
/// literal string — every user-visible string must go through <c>{loc:Translate ...}</c> (or at least
/// a binding) instead. Runs against this repo's real <c>src/ScanlineStudio.UI</c> tree so it gates content as
/// panes are added (spec/14-roadmap.md's "in place from the start" requirement), not a fixture that
/// can drift from what's actually shipping.</summary>
public sealed partial class NoHardcodedAxamlStringsTests
{
    // \b before the group is load-bearing (found via a real false positive, 2026-08-28):
    // SizeToContent="Height" otherwise matches as if it were Content="Height", since the original
    // regex had no word boundary and "Content" is a substring of "SizeToContent". No boundary is
    // needed between "o" and "C" within a single word (both \w), so \b correctly rejects that case
    // while still matching a real standalone Content=/Text=/Header= (always preceded by whitespace
    // or a tag-opening char, both non-word).
    [GeneratedRegex("\\b(Content|Text|Header)=\"([^\"{][^\"]*)\"")]
    private static partial Regex HardcodedAttributeRegex();

    [Fact]
    public void NoAxamlFile_SetsContentTextOrHeaderToALiteralString()
    {
        var uiSourceDirectory = FindUiSourceDirectory();
        var violations = new List<string>();

        foreach (var file in Directory.GetFiles(uiSourceDirectory, "*.axaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in HardcodedAttributeRegex().Matches(text))
            {
                violations.Add($"{Path.GetRelativePath(uiSourceDirectory, file)}: {match.Value}");
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} hardcoded string(s) in .axaml files; use {{loc:Translate ...}} instead:\n"
            + string.Join('\n', violations));
    }

    private static string FindUiSourceDirectory()
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

        return Path.Combine(dir.FullName, "src", "ScanlineStudio.UI");
    }
}
