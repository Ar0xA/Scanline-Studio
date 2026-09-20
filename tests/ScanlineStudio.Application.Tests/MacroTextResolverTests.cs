using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Application.Tests;

public sealed class MacroTextResolverTests
{
    private readonly MacroTextResolver _resolver = new();

    [Fact]
    public void ResolvesCallsignToken()
    {
        var settings = new OperatorSettings { Callsign = "W1AW" };

        Assert.Equal("DE W1AW", _resolver.Resolve("DE %m", settings));
    }

    // User-reported (2026-09-20): a blank Callsign used to resolve to an empty string -- now it
    // resolves to OperatorSettings.CallsignFallback ("N0CALL"), same "unset means apply the
    // documented default" convention as DefaultRst/DefaultRstFallback.
    [Fact]
    public void MissingCallsign_ResolvesToCallsignFallback()
    {
        var settings = new OperatorSettings();

        Assert.Equal($"DE {OperatorSettings.CallsignFallback}", _resolver.Resolve("DE %m", settings));
    }

    [Fact]
    public void BlankCallsign_ResolvesToCallsignFallback()
    {
        var settings = new OperatorSettings { Callsign = "   " };

        Assert.Equal($"DE {OperatorSettings.CallsignFallback}", _resolver.Resolve("DE %m", settings));
    }

    [Fact]
    public void ResolvesNameAndGridBraceTokens()
    {
        var settings = new OperatorSettings { Name = "Jane", Grid = "EN52" };

        Assert.Equal("Jane, EN52", _resolver.Resolve("{name}, {grid}", settings));
    }

    // User-reported (2026-09-20): a blank Name now resolves to OperatorSettings.NameFallback
    // ("NONAME"), same reasoning as Callsign/Grid above.
    [Fact]
    public void MissingNameAndGrid_ResolveToTheirOwnFallbacks()
    {
        var settings = new OperatorSettings();

        Assert.Equal($"{OperatorSettings.NameFallback}, {OperatorSettings.GridFallback}", _resolver.Resolve("{name}, {grid}", settings));
    }

    [Fact]
    public void BlankName_ResolvesToNameFallback()
    {
        var settings = new OperatorSettings { Name = "  " };

        Assert.Equal(OperatorSettings.NameFallback, _resolver.Resolve("{name}", settings));
    }

    [Fact]
    public void BlankGrid_ResolvesToGridFallback()
    {
        var settings = new OperatorSettings { Grid = "  " };

        Assert.Equal(OperatorSettings.GridFallback, _resolver.Resolve("{grid}", settings));
    }

    [Fact]
    public void UnrecognizedPercentToken_ResolvesToLiteralDoublePercent()
    {
        // Matches legacy's own default case (Main.cpp:10817-10819) exactly -- an unrecognized
        // token doesn't pass through unchanged, it becomes "%%".
        var settings = new OperatorSettings();

        Assert.Equal("x%%y", _resolver.Resolve("x%zy", settings));
    }

    [Fact]
    public void LiteralDoublePercent_RoundTripsAsLiteralDoublePercent()
    {
        // "%%" has no dedicated case in legacy's switch either -- it falls into the same default
        // case as any other unrecognized token, so it round-trips rather than collapsing to "%".
        var settings = new OperatorSettings();

        Assert.Equal("100%%", _resolver.Resolve("100%%", settings));
    }

    [Fact]
    public void TrailingLonePercent_DoesNotThrow()
    {
        var settings = new OperatorSettings();

        Assert.Equal("abc%%", _resolver.Resolve("abc%", settings));
    }

    [Fact]
    public void UnrecognizedBraceToken_IsLeftAsIs()
    {
        // No legacy equivalent to match for {}-tokens, so an unknown one is left alone rather than
        // guessing at a "%%"-style escape convention that has no precedent here.
        var settings = new OperatorSettings();

        Assert.Equal("{unknown}", _resolver.Resolve("{unknown}", settings));
    }

    [Fact]
    public void DateToken_ResolvesToUtcDateInLegacyFormat()
    {
        // Deliberately checks shape (YYYY-MON-DD, MON from legacy's own MONT1[] table,
        // LogConv.cpp:175) rather than re-deriving "today" independently -- avoids both a
        // culture-dependent month abbreviation and a midnight-rollover race against DateTime.UtcNow.
        var settings = new OperatorSettings();
        var monthAbbreviations = new[] { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };

        var resolved = _resolver.Resolve("%D", settings);
        var parts = resolved.Split('-');

        Assert.Equal(3, parts.Length);
        Assert.Equal(4, parts[0].Length);
        Assert.True(int.TryParse(parts[0], out _));
        Assert.Contains(parts[1], monthAbbreviations);
        Assert.Equal(2, parts[2].Length);
        Assert.True(int.TryParse(parts[2], out _));
    }

    [Fact]
    public void TimeToken_ResolvesToUtcTimeInLegacyFormat()
    {
        // Closes a coverage gap flagged by Tier A Batch 10 chunk 10b (docs/functional-audit-playbook.md):
        // %T -- one of only three ported legacy %-token cases -- had zero test coverage. Same shape-
        // only check as DateToken above, for the same reason (avoids an hour/minute-rollover race
        // against DateTime.UtcNow during a slow test run).
        var settings = new OperatorSettings();

        var resolved = _resolver.Resolve("%T", settings);
        var parts = resolved.Split(':');

        Assert.Equal(2, parts.Length);
        Assert.Equal(2, parts[0].Length);
        Assert.True(int.TryParse(parts[0], out var hour) && hour is >= 0 and <= 23);
        Assert.Equal(2, parts[1].Length);
        Assert.True(int.TryParse(parts[1], out var minute) && minute is >= 0 and <= 59);
    }

    [Fact]
    public void EmptyInput_ReturnsEmptyString()
    {
        var settings = new OperatorSettings();

        Assert.Equal(string.Empty, _resolver.Resolve(string.Empty, settings));
    }

    // Phase 3 (spec/15-template-designer.md, "named template variables + fill bar") -- {freq}/{mode}
    // ordinary resolved macros, and generic {word} variable resolution.

    [Fact]
    public void FreqToken_ResolvesToFormattedMegahertz()
    {
        var settings = new OperatorSettings();
        var state = new RadioState(FrequencyHz: 14_230_000, Mode: RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow);

        // RadioStatusViewModel's own $"{Hz/1_000_000.0:0.000000} MHz" formatting, mirrored here so a
        // template's {freq} reads the same as the header's own live frequency readout.
        Assert.Equal("14.230000 MHz", _resolver.Resolve("{freq}", settings, state));
    }

    [Fact]
    public void ModeToken_ResolvesToRadioModeToString()
    {
        var settings = new OperatorSettings();
        var state = new RadioState(FrequencyHz: 14_230_000, Mode: RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow);

        Assert.Equal("USB", _resolver.Resolve("{mode}", settings, state));
    }

    // Auditor usability review follow-up (2026-08-18): {dist}/{bearing} -- the TX Image Editor's own
    // DIST/BEAM insert-field chips used to be a stub with no Command at all. Combines MY grid
    // (OperatorSettings.Grid, a real settings-tier value like {grid} above) with the {his_grid}
    // fill-bar variable.

    [Fact]
    public void DistAndBearingTokens_BothGridsPresent_ResolveToComputedValues()
    {
        var settings = new OperatorSettings { Grid = "JN58tc" };
        var variables = new Dictionary<string, string> { ["his_grid"] = "FN31pr" };

        var resolved = _resolver.Resolve("{dist} / {bearing}", settings, variables: variables);

        // Real-world Frankfurt(JN58tc)->NY-area(FN31pr) great-circle distance/bearing, same values
        // MaidenheadLocatorTests pins directly against the underlying math -- this test only checks
        // the token wiring reaches that math, not the math itself.
        Assert.Equal("6338 km / 298°", resolved);
    }

    [Fact]
    public void DistAndBearingTokens_NoHisGridVariable_ResolveToEmptyString()
    {
        var settings = new OperatorSettings { Grid = "JN58tc" };

        // No variables dictionary at all (mirrors a template that references {dist}/{bearing} before
        // the operator has typed anything into the QSO fill bar yet) -- same "input not available
        // yet, empty string, not an error" convention {freq}/{mode} already establish for a null
        // RadioState.
        Assert.Equal(" / ", _resolver.Resolve("{dist} / {bearing}", settings, variables: null));
    }

    [Fact]
    public void DistAndBearingTokens_HisGridPresentButUnparseable_ResolveToEmptyString()
    {
        var settings = new OperatorSettings { Grid = "JN58tc" };
        var variables = new Dictionary<string, string> { ["his_grid"] = "not a grid" };

        Assert.Equal(" / ", _resolver.Resolve("{dist} / {bearing}", settings, variables: variables));
    }

    [Fact]
    public void DistAndBearingTokens_MyGridMissing_ResolveToEmptyString()
    {
        var settings = new OperatorSettings(); // no Grid set
        var variables = new Dictionary<string, string> { ["his_grid"] = "FN31pr" };

        Assert.Equal(" / ", _resolver.Resolve("{dist} / {bearing}", settings, variables: variables));
    }

    [Fact]
    public void FreqAndModeTokens_NullRadioState_ResolveToEmptyString()
    {
        // No radio connected is a normal, fully-supported state (IRadioSessionService.LastKnownState's
        // own doc comment) -- {freq}/{mode} must not throw or leave the literal token behind.
        var settings = new OperatorSettings();

        Assert.Equal(" / ", _resolver.Resolve("{freq} / {mode}", settings, radioState: null));
    }

    [Fact]
    public void FreqAndModeTokens_OmittedRadioStateArgument_ResolveToEmptyString()
    {
        // The 2 pre-Phase-3 arguments still compile and behave the same as a null radioState --
        // confirms the new parameters are genuinely optional, not just nullable.
        var settings = new OperatorSettings();

        Assert.Equal(" / ", _resolver.Resolve("{freq} / {mode}", settings));
    }

    [Fact]
    public void VariableToken_ResolvesFromVariablesDictionary()
    {
        var settings = new OperatorSettings();
        var variables = new Dictionary<string, string> { ["his_call"] = "K1ABC" };

        Assert.Equal("DE K1ABC", _resolver.Resolve("DE {his_call}", settings, variables: variables));
    }

    [Fact]
    public void UnfilledVariableToken_ResolvesVerbatim_NotToEmptyString()
    {
        // Phase 3 plan-review decision: an unfilled variable must NOT silently resolve to an empty
        // string and vanish from the transmitted image -- it stays literally in the text, same as any
        // other unrecognized {token} already does, needing no escape-hatch syntax for a user's
        // ordinary literal {note}-shaped text.
        var settings = new OperatorSettings();
        var variables = new Dictionary<string, string>();

        Assert.Equal("DE {his_call}", _resolver.Resolve("DE {his_call}", settings, variables: variables));
    }

    [Fact]
    public void VariableToken_NullVariablesDictionary_ResolvesVerbatim()
    {
        var settings = new OperatorSettings();

        Assert.Equal("DE {his_call}", _resolver.Resolve("DE {his_call}", settings, variables: null));
    }

    [Fact]
    public void VariableTokenMatching_IsCaseSensitive()
    {
        // Matches {name}/{grid}'s own existing literal-Replace case sensitivity -- {His_Call} and
        // {his_call} are different tokens, not merged.
        var settings = new OperatorSettings();
        var variables = new Dictionary<string, string> { ["his_call"] = "K1ABC" };

        Assert.Equal("{His_Call}", _resolver.Resolve("{His_Call}", settings, variables: variables));
    }

    [Fact]
    public void VariableValueContainingBraceToken_IsNotReResolved()
    {
        // Single-pass grammar (Phase 3 plan-review decision): a fill VALUE containing "{something}"
        // is never itself re-resolved -- the regex scan only ever sees the ORIGINAL text, never its
        // own substituted output.
        var settings = new OperatorSettings { Name = "Jane" };
        var variables = new Dictionary<string, string> { ["note"] = "say {name}" };

        Assert.Equal("say {name}", _resolver.Resolve("{note}", settings, variables: variables));
    }

    [Fact]
    public void KnownMacroToken_TakesPriorityOverAVariableOfTheSameName()
    {
        // "name"/"grid"/"freq"/"mode" are always resolved as macros first -- a variables dictionary
        // that happens to also contain one of those keys must never shadow the real macro source.
        var settings = new OperatorSettings { Name = "Jane" };
        var variables = new Dictionary<string, string> { ["name"] = "SHOULD NOT WIN" };

        Assert.Equal("Jane", _resolver.Resolve("{name}", settings, variables: variables));
    }
}
