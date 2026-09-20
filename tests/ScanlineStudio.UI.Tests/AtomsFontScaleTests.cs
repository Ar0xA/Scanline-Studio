using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ScanlineStudio.UI.Tests;

/// <summary>Phase 2 font-size presets: parses the real AtomsFontScaleNormal.axaml/
/// AtomsFontScaleLarge.axaml directly (not a fixture) so these tests gate the actual shipping files,
/// same "grep-based guard in lieu of a real analyzer" philosophy as
/// <see cref="DarkModeStaticResourceGuardTests"/>/<see cref="AtomsTokensThemeDictionariesTests"/>
/// (Phase 1). Four jobs: (1) pin every Normal-branch value to today's shipped literal (the golden
/// test — the cheapest guard that the tokenization sweep itself didn't quietly alter Normal's
/// appearance); (2) key-set parity between the two branches (the single highest-value guard here — a
/// key present in only one branch resolves to nothing and silently drops the affected value to
/// unset/NaN, with no build failure and no visible symptom until a user actually switches to Large);
/// (3) referential integrity (every {DynamicResource <fonttoken>} consumer resolves against a real
/// key in both branches -- catches a typo); (4) zero {StaticResource <fonttoken>} usages anywhere
/// (those never re-resolve when the font-scale swap happens).
///
/// Deliberately NOT a Phase-1-style blanket "zero literal FontSize/Height/etc anywhere" test: unlike
/// color hex literals (unambiguous evidence of a color regardless of context), a bare
/// Height="24"/FontSize="11" is NOT uniquely identifiable as font-size-coupled -- most of the ~228
/// remaining literal Height/Width/FontSize-shaped attributes across this tree are legitimate,
/// unrelated fixed geometry (Window root dimensions, icon/drag-handle sizes, per-instance
/// NumericUpDown/TextBox width overrides on an already-tokenized shared style, MainWindow's own
/// fixed-row layout math) that must never become resource-driven. A hand-maintained line-numbered
/// exception list for that many sites would be fragile (drifts on any unrelated edit) and was
/// explicitly rejected during this phase's plan-review in favor of this design: a per-file PINNED
/// COUNT of remaining literal sites, verified once against the real file content at token-catalog
/// completion time. This doesn't validate WHICH literals remain (that was a one-time human audit,
/// documented in the Phase 2 plan) -- it only catches an INCREASE, which is exactly the failure mode
/// that matters: a newly-added coupled site slipping in untokenized. Same accepted limitation as
/// every closed-list guard in this codebase (Phase 1's own ColorKeys array, this file's own
/// <see cref="FontKeys"/>) -- someone has to update the pinned count deliberately when a new
/// genuinely-decorative literal is added, exactly like they'd have to add a new coupled site to
/// AtomsFontScaleNormal.axaml/Large.axaml.</summary>
public sealed partial class AtomsFontScaleTests
{
    /// <summary>The full closed key list from AtomsFontScaleNormal.axaml/Large.axaml (91 keys as of
    /// this phase's completion). Used for the StaticResource-misuse guard and as the source pattern
    /// for the referential-integrity scan.</summary>
    private static readonly string[] FontKeys =
    [
        "IndustryBtn36FontSize", "IndustryBtn36Height", "IndustryBtn36MinHeight",
        "IndustryBtn26FontSize", "IndustryBtn26Height", "IndustryBtn26MinHeight",
        "IndustryBtn25FontSize", "IndustryBtn25Height", "IndustryBtn25MinHeight",
        "IndustryBtn24FontSize", "IndustryBtn24Height", "IndustryBtn24MinHeight",
        "IndustryBtn22FontSize", "IndustryBtn22Height", "IndustryBtn22MinHeight",
        "IndustryBtn21FontSize", "IndustryBtn21Height", "IndustryBtn21MinHeight",
        "IndustryMiniFontSize", "IndustryMiniHeight", "IndustryMiniMinHeight",
        "IndustryStepperValueFontSize", "IndustryStepperHeight", "IndustryStepperMinHeight", "IndustryStepperMinWidth",
        "IndustryInputFontSize", "IndustryInputHeight", "IndustryInputMinHeight",
        "IndustryInputTallFontSize", "IndustryInputTallHeight", "IndustryInputTallMinHeight",
        "IndustryRowLabelFontSize", "IndustryRowLabelLineHeight",
        "IndustryRowValueFontSize", "IndustryRowValueLineHeight", "IndustryRowValueLetterSpacing",
        "IndustryRowMinHeight",
        "IndustryMenuHeight", "IndustryMenuMinHeight", "IndustryMenuItemFontSize",
        "IndustryTabItemFontSize", "IndustryTabItemHeight", "IndustryTabItemMinHeight", "IndustryTabItemLetterSpacing",
        "IndustryStatusFontSize", "IndustryStatusHeight", "IndustryStatusMinHeight",
        "IndustryCheckBoxFontSize", "IndustrySegFontSize",
        "IndustryThumbnailCallFontSize", "IndustryThumbnailCallGalleryFontSize",
        "IndustryThumbnailMetaFontSize", "IndustryThumbnailMetaGalleryFontSize",
        "IndustryContextMenuItemFontSize", "IndustryHelpGlyphFontSize",
        "IndustryGbTitleFontSize", "IndustryGbTitleLetterSpacing",
        "IndustryKickerFontSize", "IndustryKickerLetterSpacing", "IndustryKickerWideLetterSpacing",
        "IndustryTableHeaderFontSize", "IndustryTableHeaderLetterSpacing",
        "IndustryTableCellFontSize", "IndustryTableCellLetterSpacing",
        "IndustryDisclosureFontSize", "IndustryDisclosureLetterSpacing",
        "IndustryFrequencyReadoutFontSize", "IndustryFrequencyReadoutLineHeight", "IndustryFrequencyReadoutLetterSpacing",
        "IndustryFrequencyEditFontSize", "IndustryFrequencyEditHeight", "IndustryFrequencyEditMinHeight", "IndustryFrequencyEditWidth",
        "IndustryTxHeaderBtnPadding",
        "IndustryDialogTitleFontSize",
        "IndustryTxSelectionReadoutFontSize",
        "IndustryMiniIconButtonSize",
        "IndustryAboutTitleFontSize",
        "IndustryCallsignChipFontSize", "IndustryCallsignChipLetterSpacing",
        "IndustryRangeCaptionFontSize",
        "IndustryLogbookNotesHeight",
        "IndustryOptionsAdvancedCaptionFontSize",
        "IndustryVfoCardMinWidth",
        "IndustryBandwidthComboWidth",
        "IndustryUtcClockFontSize",
        "IndustryFrequencyPresetFontSize", "IndustryFrequencyPresetLabelFontSize", "IndustryFrequencyPresetLabelLetterSpacing",
        "IndustryFavouritesHintFontSize",
        "IndustryRadioHeaderStatusMessageFontSize",
    ];

    /// <summary>Pins every Normal-branch value shipped at token-catalog completion. Deliberately
    /// duplicated here as literals, not computed, so an accidental edit to the Normal branch is
    /// caught even if the "computation" that produced it was also wrong -- same reasoning as Phase
    /// 1's own <c>ExpectedLightValues</c>.</summary>
    private static readonly Dictionary<string, string> ExpectedNormalValues = new()
    {
        ["IndustryBtn36FontSize"] = "12.5",
        ["IndustryBtn36Height"] = "36",
        ["IndustryBtn36MinHeight"] = "36",
        ["IndustryBtn26FontSize"] = "12",
        ["IndustryBtn26Height"] = "26",
        ["IndustryBtn26MinHeight"] = "26",
        ["IndustryBtn25FontSize"] = "12",
        ["IndustryBtn25Height"] = "25",
        ["IndustryBtn25MinHeight"] = "25",
        ["IndustryBtn24FontSize"] = "11",
        ["IndustryBtn24Height"] = "24",
        ["IndustryBtn24MinHeight"] = "24",
        ["IndustryBtn22FontSize"] = "11",
        ["IndustryBtn22Height"] = "22",
        ["IndustryBtn22MinHeight"] = "22",
        ["IndustryBtn21FontSize"] = "10.5",
        ["IndustryBtn21Height"] = "21",
        ["IndustryBtn21MinHeight"] = "21",
        ["IndustryMiniFontSize"] = "10",
        ["IndustryMiniHeight"] = "17",
        ["IndustryMiniMinHeight"] = "17",
        ["IndustryStepperValueFontSize"] = "12",
        ["IndustryStepperHeight"] = "22",
        ["IndustryStepperMinHeight"] = "22",
        ["IndustryStepperMinWidth"] = "90",
        ["IndustryInputFontSize"] = "11",
        ["IndustryInputHeight"] = "24",
        ["IndustryInputMinHeight"] = "24",
        ["IndustryInputTallFontSize"] = "12",
        ["IndustryInputTallHeight"] = "26",
        ["IndustryInputTallMinHeight"] = "26",
        ["IndustryRowLabelFontSize"] = "11",
        ["IndustryRowLabelLineHeight"] = "16.5",
        ["IndustryRowValueFontSize"] = "11",
        ["IndustryRowValueLineHeight"] = "16.5",
        ["IndustryRowValueLetterSpacing"] = "-0.22",
        ["IndustryRowMinHeight"] = "16.5",
        ["IndustryMenuHeight"] = "26",
        ["IndustryMenuMinHeight"] = "26",
        ["IndustryMenuItemFontSize"] = "12",
        ["IndustryTabItemFontSize"] = "11.5",
        ["IndustryTabItemHeight"] = "26",
        ["IndustryTabItemMinHeight"] = "26",
        ["IndustryTabItemLetterSpacing"] = "1.035",
        ["IndustryStatusFontSize"] = "10",
        ["IndustryStatusHeight"] = "19",
        ["IndustryStatusMinHeight"] = "19",
        ["IndustryCheckBoxFontSize"] = "11",
        ["IndustrySegFontSize"] = "11",
        ["IndustryThumbnailCallFontSize"] = "10",
        ["IndustryThumbnailCallGalleryFontSize"] = "11",
        ["IndustryThumbnailMetaFontSize"] = "9",
        ["IndustryThumbnailMetaGalleryFontSize"] = "9.5",
        ["IndustryContextMenuItemFontSize"] = "12",
        ["IndustryHelpGlyphFontSize"] = "10",
        ["IndustryGbTitleFontSize"] = "9.5",
        ["IndustryGbTitleLetterSpacing"] = "1.33",
        ["IndustryKickerFontSize"] = "10",
        ["IndustryKickerLetterSpacing"] = "1.4",
        ["IndustryKickerWideLetterSpacing"] = "1.6",
        ["IndustryTableHeaderFontSize"] = "11",
        ["IndustryTableHeaderLetterSpacing"] = "0.88",
        ["IndustryTableCellFontSize"] = "11",
        ["IndustryTableCellLetterSpacing"] = "-0.22",
        ["IndustryDisclosureFontSize"] = "9.5",
        ["IndustryDisclosureLetterSpacing"] = "1.33",
        ["IndustryFrequencyReadoutFontSize"] = "40",
        ["IndustryFrequencyReadoutLineHeight"] = "41",
        ["IndustryFrequencyReadoutLetterSpacing"] = "-1.2",
        ["IndustryFrequencyEditFontSize"] = "24",
        ["IndustryFrequencyEditHeight"] = "41",
        ["IndustryFrequencyEditMinHeight"] = "41",
        ["IndustryFrequencyEditWidth"] = "180",
        ["IndustryTxHeaderBtnPadding"] = "6,0",
        ["IndustryDialogTitleFontSize"] = "16",
        ["IndustryTxSelectionReadoutFontSize"] = "10",
        ["IndustryMiniIconButtonSize"] = "19",
        ["IndustryAboutTitleFontSize"] = "18",
        ["IndustryCallsignChipFontSize"] = "12",
        ["IndustryCallsignChipLetterSpacing"] = "0.96",
        ["IndustryRangeCaptionFontSize"] = "10",
        ["IndustryLogbookNotesHeight"] = "60",
        ["IndustryOptionsAdvancedCaptionFontSize"] = "11",
        ["IndustryVfoCardMinWidth"] = "320",
        ["IndustryBandwidthComboWidth"] = "108",
        ["IndustryUtcClockFontSize"] = "15",
        ["IndustryFrequencyPresetFontSize"] = "12.5",
        ["IndustryFrequencyPresetLabelFontSize"] = "9.5",
        ["IndustryFrequencyPresetLabelLetterSpacing"] = "0.57",
        ["IndustryFavouritesHintFontSize"] = "10",
        ["IndustryRadioHeaderStatusMessageFontSize"] = "10",
    };

    /// <summary>Per-file pinned count of remaining literal FontSize/LineHeight/LetterSpacing/Height/
    /// MinHeight/Width/MinWidth attribute occurrences, verified once against the real file content
    /// when the token-catalog sweep completed. Files not listed here are expected to have zero
    /// (confirmed at sweep time: WaterfallPaneView.axaml/RxImagePaneView.axaml have none at all).</summary>
    private static readonly Dictionary<string, int> ExpectedRemainingLiteralCounts = new()
    {
        ["AboutWindowView.axaml"] = 4,
        ["ConfirmActionDialogView.axaml"] = 2,
        ["FavouritesEditorWindowView.axaml"] = 4,
        ["HamlibLibraryReloadFailedDialogView.axaml"] = 2,
        ["ImageViewerWindowView.axaml"] = 4,
        ["LoopbackSelfTestResultWindowView.axaml"] = 4,
        ["MacrosReferenceWindowView.axaml"] = 4,
        ["MainWindow.axaml"] = 22,
        ["OptionsWindowView.axaml"] = 6,
        ["QsoLinkWindowView.axaml"] = 4,
        ["QuickSwitchFailedDialogView.axaml"] = 2,
        ["RadioConnectionGaveUpWindowView.axaml"] = 3,
        ["RadioHeaderView.axaml"] = 4,
        ["RestartRequiredDialogView.axaml"] = 2,
        ["SampleRateChangeDeferredDialogView.axaml"] = 2,
        ["TextPromptWindowView.axaml"] = 3,
        ["ToneGeneratorWindowView.axaml"] = 4,
        // 2026-09-19: -4 for the removed Power/ALC fill-bar meters (each had a literal Height="8"),
        // per direct user request ("show numbers, not bars") -- see TxControlsPaneView.axaml's own
        // comment at the Output card's numeric Power/ALC rows.
        ["TxControlsPaneView.axaml"] = 2,
        // 2026-09-19: +1 for the new TX header progress bar's MinWidth="80" (TxImageEditorPaneView.axaml)
        // -- a fixed space-reservation literal for the ProgressBar track, same class as this file's
        // own pre-existing StackPanel MinWidth="180"/"120" literals, not a font-size-coupled site.
        // 2026-09-20 (element rotation): +6 -- Width="110" on the 4 new "Rotate by" NumericUpDown
        // controls (text/box's flyouts, image's and line's new dedicated ones), plus MinWidth="200"
        // on the 2 new dedicated flyouts' own StackPanel (image, line) -- same fixed-control-sizing
        // class as the FontSizePx/BorderThicknessPx/CornerRadiusPx/StrokeThicknessPx TextBoxes'
        // own pre-existing Width="70" literals in this same file, not font-size-coupled.
        ["TxImageEditorPaneView.axaml"] = 116,
        ["Atoms.axaml"] = 30,
    };

    /// <summary>Atoms.axaml is the only file with its own ControlThemes/Styles, so it's the only
    /// place a `&lt;Setter Property="..." Value="..."&gt;` form of these literals can occur (confirmed:
    /// zero Setter-form matches in any Views file at sweep time).</summary>
    private const int ExpectedAtomsSetterFormLiteralCount = 13;

    // \b-anchored so MaxHeight/MaxWidth/d:DesignWidth/d:DesignHeight never match (they contain
    // "Height"/"Width" as a substring but aren't in scope -- a real false-positive class confirmed
    // during this test's own design, matching NoHardcodedAxamlStringsTests's own \b-boundary lesson).
    [GeneratedRegex(@"\b(FontSize|LineHeight|LetterSpacing|Height|MinHeight|Width|MinWidth)=""-?[0-9][^""]*""")]
    private static partial Regex LiteralAttributeRegex();

    [GeneratedRegex(@"Property=""(FontSize|LineHeight|LetterSpacing|Height|MinHeight|Width|MinWidth)""\s+Value=""-?[0-9][^""]*""")]
    private static partial Regex LiteralSetterRegex();

    [Fact]
    public void NormalBranch_MatchesEveryValueShippedAtTokenCatalogCompletion()
    {
        var actual = LoadBranch("Normal");

        foreach (var (key, expected) in ExpectedNormalValues)
        {
            Assert.True(actual.TryGetValue(key, out var value), $"Normal branch is missing key '{key}'.");
            Assert.Equal(expected, value, ignoreCase: true);
        }

        Assert.Equal(ExpectedNormalValues.Count, actual.Count);
        Assert.Equal(FontKeys.Length, actual.Count);
    }

    [Fact]
    public void NormalAndLargeBranches_HaveIdenticalKeySets()
    {
        var normalKeys = LoadBranch("Normal").Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var largeKeys = LoadBranch("Large").Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(normalKeys, largeKeys);
    }

    // Round-2 code-review finding: the original design filtered candidate keys by
    // `FontKeys.Contains(key)` BEFORE checking branch membership -- which means a TYPO'D key
    // (e.g. "IndustryBtn24FontSiz") is silently skipped rather than reported, defeating the whole
    // point of this test. Filter by NAME SHAPE instead (does it look like a font-scale key at all --
    // ends in one of the tracked property suffixes), then require branch membership; that catches a
    // typo as "shaped like a font key, but missing from both branches."
    //
    // A pure suffix-shape filter is too broad on its own: AtomsTokens.axaml (Phase 0/1's color/
    // spacing/padding token file) legitimately defines its own `Industry*Padding`/`*Height`-shaped
    // keys (IndustryMiniPadding, IndustryStepperValuePadding, IndustryTableCellPadding, ...) that
    // have nothing to do with the font-scale axis -- flagging those as "missing from the font-scale
    // branches" would be a false positive, not a real bug. Exclude any candidate that's already
    // defined in AtomsTokens.axaml (checked directly, not assumed) before requiring font-scale-branch
    // membership.
    [GeneratedRegex(@"^Industry\w*(FontSize|LineHeight|LetterSpacing|Height|Width|MinWidth|MinHeight|Padding)$")]
    private static partial Regex FontKeyShapeRegex();

    [Fact]
    public void EveryFontKeyShapedConsumer_ResolvesInBothBranchesOrIsAKnownAtomsTokensKey()
    {
        var uiSourceDirectory = FindUiSourceDirectory();
        var normalKeys = LoadBranch("Normal").Keys.ToHashSet();
        var largeKeys = LoadBranch("Large").Keys.ToHashSet();
        var atomsTokensKeys = LoadAllKeys(Path.Combine(uiSourceDirectory, "Styles", "AtomsTokens.axaml"));
        var violations = new List<string>();

        foreach (var file in Directory.GetFiles(uiSourceDirectory, "*.axaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, @"\{DynamicResource (Industry\w+)\}"))
            {
                var key = match.Groups[1].Value;
                if (!FontKeyShapeRegex().IsMatch(key) || atomsTokensKeys.Contains(key))
                {
                    continue; // not shaped like a font-scale key, or a real (verified) AtomsTokens.axaml key -- out of scope here
                }

                if (!normalKeys.Contains(key) || !largeKeys.Contains(key))
                {
                    violations.Add($"{Path.GetRelativePath(uiSourceDirectory, file)}: {{DynamicResource {key}}} missing from one or both branches");
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join('\n', violations));
    }

    /// <summary>Every `x:Key` anywhere in the given file, regardless of nesting depth (AtomsTokens.axaml
    /// has both flat top-level keys -- Spacing/Padding/FontFamily -- and keys nested inside
    /// ResourceDictionary.ThemeDictionaries' Light/Dark branches).</summary>
    private static HashSet<string> LoadAllKeys(string path)
    {
        var document = XDocument.Load(path);
        var x = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        return document.Descendants()
            .Select(e => e.Attribute(x + "Key")?.Value)
            .Where(k => k is not null)
            .Select(k => k!)
            .ToHashSet();
    }

    [Fact]
    public void NoAxamlFile_UsesStaticResourceForAFontScaleKey()
    {
        var uiSourceDirectory = FindUiSourceDirectory();
        var violations = new List<string>();

        foreach (var file in Directory.GetFiles(uiSourceDirectory, "*.axaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var key in FontKeys)
            {
                if (Regex.IsMatch(text, "\\{StaticResource " + Regex.Escape(key) + "\\}"))
                {
                    violations.Add($"{Path.GetRelativePath(uiSourceDirectory, file)}: {{StaticResource {key}}}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} {{StaticResource <fontkey>}} usage(s); these never re-resolve when "
            + "the font scale swaps at runtime -- use {{DynamicResource <fontkey>}} instead:\n" + string.Join('\n', violations));
    }

    // Round-2 code-review finding: the original design only iterated ExpectedRemainingLiteralCounts's
    // own keys, so its own doc-comment claim ("files not listed here are expected to have zero") was
    // never actually enforced -- a brand-new file (or a regression in one of the two confirmed-zero
    // files, WaterfallPaneView.axaml/RxImagePaneView.axaml) with a bare literal would pass silently,
    // exactly the "newly-added coupled site slipping in untokenized" failure mode this test exists
    // for. Fixed: glob every real .axaml file under the tree; a file not present in
    // ExpectedRemainingLiteralCounts is required to have exactly zero matches.
    [Fact]
    public void RemainingLiteralFontSizeShapedAttributes_MatchThePinnedCountPerFile_NoUndetectedIncrease()
    {
        var uiSourceDirectory = FindUiSourceDirectory();
        var violations = new List<string>();

        foreach (var file in Directory.GetFiles(uiSourceDirectory, "*.axaml", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);
            var expectedCount = ExpectedRemainingLiteralCounts.GetValueOrDefault(fileName, 0);

            var text = StripAxamlComments(File.ReadAllText(file));
            var actualCount = LiteralAttributeRegex().Matches(text).Count;
            if (actualCount != expectedCount)
            {
                violations.Add($"{fileName}: expected {expectedCount} remaining literal(s), found {actualCount} -- "
                    + "if this is a genuine new decorative literal, update ExpectedRemainingLiteralCounts; if it's "
                    + "a new font-size-coupled site, tokenize it instead.");
            }
        }

        Assert.True(violations.Count == 0, string.Join('\n', violations));
    }

    [Fact]
    public void AtomsAxaml_SetterFormLiteralCount_MatchesThePinnedCount()
    {
        var uiSourceDirectory = FindUiSourceDirectory();
        var path = Path.Combine(uiSourceDirectory, "Styles", "Atoms.axaml");
        var text = StripAxamlComments(File.ReadAllText(path));
        var actualCount = LiteralSetterRegex().Matches(text).Count;

        Assert.Equal(ExpectedAtomsSetterFormLiteralCount, actualCount);
    }

    private static string StripAxamlComments(string text) => Regex.Replace(text, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static Dictionary<string, string> LoadBranch(string fileNameStem)
    {
        var uiSourceDirectory = FindUiSourceDirectory();
        var path = Path.Combine(uiSourceDirectory, "Styles", $"AtomsFontScale{fileNameStem}.axaml");
        var document = XDocument.Load(path);
        var avalonia = XNamespace.Get("https://github.com/avaloniaui");
        var x = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

        var result = new Dictionary<string, string>();
        foreach (var element in document.Root!.Elements())
        {
            var key = element.Attribute(x + "Key")?.Value;
            if (key is not null)
            {
                result[key] = element.Value;
            }
        }

        return result;
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
