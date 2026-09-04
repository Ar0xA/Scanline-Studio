using System.Text.RegularExpressions;

namespace ScanlineStudio.UI.Tests;

/// <summary>Phase 1 dark mode's own permanent regression guard, same "grep-based CI step in lieu of a
/// real analyzer" pattern as <see cref="NoHardcodedAxamlStringsTests"/>/
/// <see cref="NoThicknessSpacingMismatchTests"/>. A missed {StaticResource Industry*} -&gt;
/// {DynamicResource Industry*} conversion (or a fresh literal hex color slipping back in) is silent --
/// no crash, no build failure, no visible symptom in the majority-Light-by-default startup state, just
/// one control frozen in Light forever. Runs against the real <c>src/ScanlineStudio.UI</c> tree so it
/// gates new views as they're added, not a fixture that can drift from what's actually shipping.</summary>
public sealed partial class DarkModeStaticResourceGuardTests
{
    /// <summary>The closed set of collapsed color-key names now living in
    /// AtomsTokens.axaml's ResourceDictionary.ThemeDictionaries (Light/Dark branches) --
    /// every consumer of one of these MUST use {DynamicResource}, never {StaticResource}, or it
    /// silently never re-resolves when the theme changes at runtime.</summary>
    private static readonly string[] ColorKeys =
    [
        "IndustryBg", "IndustrySurface", "IndustryText", "IndustryAccent",
        "IndustryTextMuted42", "IndustryTextMuted50", "IndustryTextMuted55", "IndustryTextMuted62", "IndustryTextMuted72",
        "IndustryRowRule7", "IndustryRowRule6", "IndustryTableRule8", "IndustryTableHeaderBorder",
        "IndustryNeutral200", "IndustryNeutral300",
        "IndustryAccent100", "IndustryAccent600", "IndustryAccent700", "IndustryAccent800", "IndustryAccent900",
        "IndustryDanger", "IndustryDangerForeground", "IndustryTxLed", "IndustryPlotInk",
        "IndustryControlFace", "IndustryDataField", "IndustryEditField", "IndustryDisabledFace",
        "IndustryChromeBorder", "IndustryControlBorder", "IndustryDisabledBorder",
        "IndustryLabel", "IndustryMutedText", "IndustryDisabledText", "IndustryStatusText",
        "IndustrySelectSolidBorder", "IndustrySelectTint", "IndustrySelectTintBorder", "IndustrySelectTintText",
        "IndustryHealthy", "IndustryHealthySolidBorder", "IndustryHealthyTint", "IndustryHealthyTintBorder", "IndustryHealthyTintText",
        "IndustryAttention", "IndustryAttentionSolidBorder", "IndustryAttentionTint", "IndustryAttentionTintBorder", "IndustryAttentionTintText",
        "IndustryDangerSolidBorder", "IndustryDangerTint", "IndustryDangerTintBorder", "IndustryDangerTintText",
        "IndustryHoverFace", "IndustryHoverBorder", "IndustryPressFace", "IndustryPressBorder",
        "IndustryHoverInk", "IndustryPressInk", "IndustryInputHoverInk",
        "IndustryPrimaryHover", "IndustryPrimaryHoverBorder", "IndustryDangerHover",
        "IndustryLedOffStroke", "IndustryLedOffFill", "IndustryOverlayFill", "IndustryHatchStripeBrush",
    ];

    [GeneratedRegex("#[0-9A-Fa-f]{6,8}")]
    private static partial Regex HexColorLiteralRegex();

    [Fact]
    public void NoAxamlFile_UsesStaticResourceForAThemeColorKey()
    {
        var uiSourceDirectory = FindUiSourceDirectory();
        var violations = new List<string>();

        foreach (var file in Directory.GetFiles(uiSourceDirectory, "*.axaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var key in ColorKeys)
            {
                if (Regex.IsMatch(text, "\\{StaticResource " + Regex.Escape(key) + "\\}"))
                {
                    violations.Add($"{Path.GetRelativePath(uiSourceDirectory, file)}: {{StaticResource {key}}}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} {{StaticResource <colorkey>}} usage(s); these never re-resolve when the "
            + "theme changes at runtime -- use {{DynamicResource <colorkey>}} instead:\n" + string.Join('\n', violations));
    }

    /// <summary>Catches a fresh hardcoded color literal slipping back in anywhere outside
    /// AtomsTokens.axaml (the one file allowed to define raw hex values, in its Light/Dark
    /// ThemeDictionaries branches) -- a literal has no theme-aware branch at all, so it silently
    /// stays whatever color it was written as regardless of the active theme.</summary>
    [Fact]
    public void NoAxamlFile_OutsideAtomsTokens_ContainsARawHexColorLiteral()
    {
        var uiSourceDirectory = FindUiSourceDirectory();
        var violations = new List<string>();

        foreach (var file in Directory.GetFiles(uiSourceDirectory, "*.axaml", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "AtomsTokens.axaml")
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (Match match in HexColorLiteralRegex().Matches(text))
            {
                // Comment-only mentions (design-spec citations, e.g. "#0F62A8") are common and
                // harmless -- only a real attribute value ("...=\"#RRGGBB\"") is a live consumer.
                var precedingChar = match.Index > 0 ? text[match.Index - 1] : ' ';
                if (precedingChar != '"')
                {
                    continue;
                }

                violations.Add($"{Path.GetRelativePath(uiSourceDirectory, file)}: {match.Value}");
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} raw hex color literal attribute value(s) outside AtomsTokens.axaml; "
            + "add a themed token instead so both Light and Dark get a deliberate value:\n" + string.Join('\n', violations));
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
