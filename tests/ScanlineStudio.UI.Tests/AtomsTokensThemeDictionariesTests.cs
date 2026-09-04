using System.Xml.Linq;

namespace ScanlineStudio.UI.Tests;

/// <summary>Phase 1 dark mode: parses the real AtomsTokens.axaml directly (not a fixture) so these
/// tests gate the actual shipping file. Two jobs: (1) pin the Light branch's values to exactly what
/// shipped before this restructure -- the cheapest guard that collapsing Color+Brush pairs into
/// ResourceDictionary.ThemeDictionaries didn't quietly alter the current (Light) theme; (2) confirm
/// every Light key has a Dark counterpart, and the hatch DrawingBrush's geometry is identical between
/// branches (only its fill Brush should differ) -- this file's own header comment documents a prior
/// real "bowtie" bug from divergent geometry between duplicated copies of this exact brush.</summary>
public sealed class AtomsTokensThemeDictionariesTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Exactly what shipped before this restructure -- see AtomsTokensTests (if present)
    /// or git history for the pre-Phase-1 values; deliberately duplicated here as literals, not
    /// computed, so an accidental edit to the Light branch is caught even if the "computation" that
    /// produced it was also wrong.</summary>
    private static readonly Dictionary<string, string> ExpectedLightValues = new()
    {
        ["IndustryBg"] = "#F0F0F0",
        ["IndustrySurface"] = "#E9E9EA",
        ["IndustryText"] = "#101010",
        ["IndustryAccent"] = "#0F62A8",
        ["IndustryTextMuted42"] = "#99999A",
        ["IndustryTextMuted50"] = "#88898A",
        ["IndustryTextMuted55"] = "#7D7E7F",
        ["IndustryTextMuted62"] = "#6E6F70",
        ["IndustryTextMuted72"] = "#595A5B",
        ["IndustryRowRule7"] = "#E3E3E4",
        ["IndustryRowRule6"] = "#E5E5E6",
        ["IndustryTableRule8"] = "#E1E1E2",
        ["IndustryTableHeaderBorder"] = "#C8C8C8",
        ["IndustryNeutral200"] = "#E7E7EA",
        ["IndustryNeutral300"] = "#D4D4D7",
        ["IndustryAccent100"] = "#EEF6FF",
        ["IndustryAccent600"] = "#597EA3",
        ["IndustryAccent700"] = "#416180",
        ["IndustryAccent800"] = "#2C455D",
        ["IndustryAccent900"] = "#1D2D3D",
        ["IndustryPlotInk"] = "#F0F0F0",
        ["IndustryDanger"] = "#B42A2A",
        ["IndustryDangerForeground"] = "#FFFFFF",
        ["IndustryTxLed"] = "#D42A2A",
        ["IndustryControlFace"] = "#E9E9E9",
        ["IndustryDataField"] = "#E4E4E4",
        ["IndustryEditField"] = "#FFFFFF",
        ["IndustryDisabledFace"] = "#EDEDED",
        ["IndustryChromeBorder"] = "#B9B9B9",
        ["IndustryControlBorder"] = "#A0A0A0",
        ["IndustryDisabledBorder"] = "#C6C6C6",
        ["IndustryLabel"] = "#454545",
        ["IndustryMutedText"] = "#8A8A8A",
        ["IndustryDisabledText"] = "#9A9A9A",
        ["IndustryStatusText"] = "#3A3A3A",
        ["IndustrySelectSolidBorder"] = "#0B4C83",
        ["IndustrySelectTint"] = "#C4DDF5",
        ["IndustrySelectTintBorder"] = "#2A5D8F",
        ["IndustrySelectTintText"] = "#0B3E68",
        ["IndustryHealthy"] = "#2E9E44",
        ["IndustryHealthySolidBorder"] = "#256F33",
        ["IndustryHealthyTint"] = "#DCEFD9",
        ["IndustryHealthyTintBorder"] = "#4E9A4E",
        ["IndustryHealthyTintText"] = "#14471C",
        ["IndustryAttention"] = "#C88A00",
        ["IndustryAttentionSolidBorder"] = "#8F6300",
        ["IndustryAttentionTint"] = "#FAECC4",
        ["IndustryAttentionTintBorder"] = "#B0871F",
        ["IndustryAttentionTintText"] = "#4A3600",
        ["IndustryDangerSolidBorder"] = "#8C1F1F",
        ["IndustryDangerTint"] = "#F3D9D9",
        ["IndustryDangerTintBorder"] = "#B45A5A",
        ["IndustryDangerTintText"] = "#5A1010",
        ["IndustryHoverFace"] = "#DCE9F5",
        ["IndustryHoverBorder"] = "#7EB4EA",
        ["IndustryPressFace"] = "#C4DDF5",
        ["IndustryPressBorder"] = "#5A9AD4",
        ["IndustryHoverInk"] = "#121D1F20",
        ["IndustryPressInk"] = "#241D1F20",
        ["IndustryInputHoverInk"] = "#731D1F20",
        ["IndustryPrimaryHover"] = "#FF1472C0",
        ["IndustryPrimaryHoverBorder"] = "#FF1472C0",
        ["IndustryDangerHover"] = "#FFC93333",
        ["IndustryLedOffStroke"] = "#FF6E6E6E",
        ["IndustryLedOffFill"] = "#FFC8C8C8",
        ["IndustryOverlayFill"] = "#33FFFFFF",
    };

    [Fact]
    public void LightBranch_MatchesEveryValueShippedBeforeTheThemeDictionariesRestructure()
    {
        var (lightBrushes, _) = LoadBranches();

        foreach (var (key, expected) in ExpectedLightValues)
        {
            Assert.True(lightBrushes.TryGetValue(key, out var actual), $"Light branch is missing key '{key}'.");
            Assert.Equal(expected, actual, ignoreCase: true);
        }

        Assert.Equal(ExpectedLightValues.Count, lightBrushes.Count);
    }

    [Fact]
    public void DarkBranch_HasAMatchingEntryForEveryLightKey()
    {
        var (lightBrushes, darkBrushes) = LoadBranches();

        var missing = lightBrushes.Keys.Where(k => !darkBrushes.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, $"Dark branch is missing key(s): {string.Join(", ", missing)}");
    }

    [Fact]
    public void HatchStripeBrush_GeometryIsIdenticalBetweenLightAndDarkBranches_OnlyFillDiffers()
    {
        var document = LoadAtomsTokensDocument();
        var themeDictionaries = document.Descendants(Avalonia + "ResourceDictionary.ThemeDictionaries").Single();

        var lightHatch = FindNamedChild(themeDictionaries, "Light", "DrawingBrush", "IndustryHatchStripeBrush");
        var darkHatch = FindNamedChild(themeDictionaries, "Dark", "DrawingBrush", "IndustryHatchStripeBrush");

        Assert.Equal((string?)lightHatch.Attribute("SourceRect"), (string?)darkHatch.Attribute("SourceRect"));
        Assert.Equal((string?)lightHatch.Attribute("DestinationRect"), (string?)darkHatch.Attribute("DestinationRect"));

        var lightGeometry = lightHatch.Descendants(Avalonia + "GeometryGroup").Single().ToString();
        var darkGeometry = darkHatch.Descendants(Avalonia + "GeometryGroup").Single().ToString();
        Assert.Equal(lightGeometry, darkGeometry);

        var lightFill = (string?)lightHatch.Descendants(Avalonia + "GeometryDrawing").Single().Attribute("Brush");
        var darkFill = (string?)darkHatch.Descendants(Avalonia + "GeometryDrawing").Single().Attribute("Brush");
        Assert.NotEqual(lightFill, darkFill);
    }

    private static (Dictionary<string, string> Light, Dictionary<string, string> Dark) LoadBranches()
    {
        var document = LoadAtomsTokensDocument();
        var themeDictionaries = document.Descendants(Avalonia + "ResourceDictionary.ThemeDictionaries").Single();

        var lightDict = themeDictionaries.Elements(Avalonia + "ResourceDictionary")
            .Single(e => (string?)e.Attribute(X + "Key") == "Light");
        var darkDict = themeDictionaries.Elements(Avalonia + "ResourceDictionary")
            .Single(e => (string?)e.Attribute(X + "Key") == "Dark");

        return (ExtractSolidColorBrushes(lightDict), ExtractSolidColorBrushes(darkDict));
    }

    private static Dictionary<string, string> ExtractSolidColorBrushes(XElement dictionary)
    {
        return dictionary.Elements(Avalonia + "SolidColorBrush")
            .ToDictionary(
                e => (string)e.Attribute(X + "Key")!,
                e => (string)e.Attribute("Color")!);
    }

    private static XElement FindNamedChild(XElement themeDictionaries, string branchKey, string elementName, string resourceKey)
    {
        var branch = themeDictionaries.Elements(Avalonia + "ResourceDictionary")
            .Single(e => (string?)e.Attribute(X + "Key") == branchKey);
        return branch.Elements(Avalonia + elementName)
            .Single(e => (string?)e.Attribute(X + "Key") == resourceKey);
    }

    private static XDocument LoadAtomsTokensDocument()
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

        var path = Path.Combine(dir.FullName, "src", "ScanlineStudio.UI", "Styles", "AtomsTokens.axaml");
        return XDocument.Load(path);
    }
}
