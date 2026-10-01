using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace ScanlineStudio.UI.FontTests;

/// <summary>Phase 0 exit gate for the Industry-design-system UI redesign
/// (mockups/guidance/LAYOUT-SPEC.md). Confirmed via a real (non-headless-drawing) Skia probe,
/// not assumed, that Avalonia's <c>avares://.../Folder#FamilyName</c> font-collection resolution
/// searches EVERY font file in that folder for the closest weight match -- it does not filter by
/// requested family name first. With Barlow and Barlow Condensed's .ttf files in one shared
/// folder, requesting "Barlow Condensed" at SemiBold silently resolved to plain Barlow Bold
/// (wrong family AND wrong weight, no error) -- exactly the class of silent-fallback failure the
/// auditor flagged, just via folder cross-contamination rather than internal weight-bucketing.
/// Fixed by giving each family its own subfolder (`Assets/Fonts/Barlow/`,
/// `Assets/Fonts/BarlowCondensed/`); this test guards the fix. Google's static builds also name
/// non-RIBBI weights with a per-weight legacy family (name ID 1, e.g. "Barlow Medium") alongside
/// the typographic family (name ID 16, "Barlow") -- confirmed both resolve to the CORRECT weight
/// and file once folders are separated, so this test checks resolved Weight and family-token
/// containment rather than exact FamilyName equality, which would false-fail on that legitimate
/// naming quirk.
///
/// This whole project exists (rather than living in ScanlineStudio.UI.Tests) because this test
/// requires a REAL font manager, and Avalonia.Headless's default HeadlessFontManagerStub (used by
/// every test in ScanlineStudio.UI.Tests) does not load embedded fonts at all -- confirmed
/// empirically, every lookup failed outright under the stub, even for a font's own Regular
/// weight. Avalonia's headless platform is process-global singleton state, so the two font
/// manager configurations can't coexist in one test assembly; see this project's TestAppBuilder
/// and its csproj comment.</summary>
public sealed class IndustryFontResolutionTests
{
    // BarlowUri references App's own constant (not a private copy) specifically so a typo in the
    // actual load-bearing FontManagerOptions.DefaultFamilyName site (App.axaml.cs) fails THIS
    // test, rather than silently falling back to a system font with no exception and a clean
    // build -- auditor-caught gap (Phase 0 code-review): the three prior independent copies of
    // this URI (App.axaml.cs, AtomsTokens.axaml x2, and this file) meant a typo in the one that
    // actually matters at runtime had no test coverage at all.
    private const string BarlowUri = App.DefaultFontFamilyUri;
    private const string BarlowCondensedUri = "avares://ScanlineStudio.UI/Assets/Fonts/BarlowCondensed#Barlow Condensed";
    private const string DejaVuSansMonoUri = "avares://ScanlineStudio.UI/Assets/Fonts/DejaVuSansMono#DejaVu Sans Mono";

    [AvaloniaTheory]
    [InlineData(BarlowUri, "Barlow", FontWeight.Normal)]
    [InlineData(BarlowUri, "Barlow", FontWeight.Medium)]
    [InlineData(BarlowUri, "Barlow", FontWeight.Bold)]
    [InlineData(BarlowCondensedUri, "Barlow Condensed", FontWeight.Normal)]
    [InlineData(BarlowCondensedUri, "Barlow Condensed", FontWeight.SemiBold)]
    [InlineData(DejaVuSansMonoUri, "DejaVu Sans Mono", FontWeight.Normal)]
    public void EmbeddedIndustryFont_ResolvesRequestedFamilyAndWeight_NotACrossFamilyOrWeightFallback(
        string fontFamilyUri, string expectedFamilyToken, FontWeight requestedWeight)
    {
        var typeface = new Typeface(FontFamily.Parse(fontFamilyUri), FontStyle.Normal, requestedWeight);

        var resolved = FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphTypeface);

        Assert.True(resolved, $"FontManager could not resolve {fontFamilyUri} at weight {requestedWeight} at all.");
        Assert.NotNull(glyphTypeface);
        // Weight mismatch is the "silently fell back to Regular/Bold" signal.
        Assert.Equal(requestedWeight, glyphTypeface!.Weight);
        // Family-token containment, not exact equality: Google's static builds legitimately name
        // e.g. Barlow's Medium face "Barlow Medium" (not bare "Barlow") in its own FamilyName,
        // which is correct, not a fallback. What must never happen is a Barlow Condensed request
        // resolving to a face whose family doesn't mention "Condensed" at all (the real bug found
        // and fixed here), or vice versa.
        Assert.Contains(expectedFamilyToken, glyphTypeface.FamilyName, StringComparison.Ordinal);
        if (expectedFamilyToken == "Barlow")
        {
            Assert.DoesNotContain("Condensed", glyphTypeface.FamilyName, StringComparison.Ordinal);
        }
    }

    [AvaloniaFact]
    public void EmbeddedIndustryFonts_MediumAndBold_AreDistinctFaces_NotBothFallingBackToRegular()
    {
        var regular = ResolveOrThrow(BarlowUri, FontWeight.Normal);
        var medium = ResolveOrThrow(BarlowUri, FontWeight.Medium);
        var bold = ResolveOrThrow(BarlowUri, FontWeight.Bold);

        // The specific failure mode this guards: a wrong name-ID bucketing (or, as found here, a
        // shared-folder cross-family bleed) resolves every non-Regular weight to the SAME glyph
        // typeface, which passes a naive "not null" check while silently discarding the weight.
        Assert.NotSame(regular, medium);
        Assert.NotSame(regular, bold);
        Assert.NotSame(medium, bold);
    }

    private static GlyphTypeface ResolveOrThrow(string fontFamilyUri, FontWeight weight)
    {
        var typeface = new Typeface(FontFamily.Parse(fontFamilyUri), FontStyle.Normal, weight);
        Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphTypeface));
        return glyphTypeface!;
    }
}
