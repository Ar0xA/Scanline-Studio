using System.Text.RegularExpressions;

namespace ScanlineStudio.UI.Tests;

/// <summary>Guards against a real crash hit twice during the UI-restructure pass: a
/// <c>Margin</c>/<c>Padding</c>/<c>BorderThickness</c> bound to one of <c>Tokens.axaml</c>'s
/// <c>ScanlineStudio*Spacing*</c> resources (an <c>x:Double</c>) instead of its
/// <c>ScanlineStudio*Padding*</c> twin (the matching <c>Thickness</c>) -- Avalonia's
/// <c>DynamicResource</c> lookup doesn't run the type converter a plain XAML literal would, so
/// this throws <c>InvalidCastException</c> the moment the window is actually constructed, not at
/// build time. No test in this project constructs a real <c>Window</c>, so nothing else catches
/// this class of mistake; same file-scanning shape as <see cref="NoHardcodedAxamlStringsTests"/>.</summary>
public sealed partial class NoThicknessSpacingMismatchTests
{
    [GeneratedRegex("(Margin|Padding|BorderThickness)=\"\\{DynamicResource [A-Za-z]*Spacing[A-Za-z]*\\}\"")]
    private static partial Regex ThicknessBoundToSpacingResourceRegex();

    [Fact]
    public void NoAxamlFile_BindsAThicknessPropertyToADoubleTypedSpacingResource()
    {
        var uiSourceDirectory = FindUiSourceDirectory();
        var violations = new List<string>();

        foreach (var file in Directory.GetFiles(uiSourceDirectory, "*.axaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in ThicknessBoundToSpacingResourceRegex().Matches(text))
            {
                violations.Add($"{Path.GetRelativePath(uiSourceDirectory, file)}: {match.Value}");
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} Thickness property bound to a Double-typed *Spacing* resource; "
            + "use the matching *Padding* resource instead:\n" + string.Join('\n', violations));
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
