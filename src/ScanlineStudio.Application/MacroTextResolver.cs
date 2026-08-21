using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Application;

/// <summary>Template substitution for TX overlay text (and, later, other macro-consuming
/// features) -- a scoped-down C# port of legacy's <c>MacroText</c> (`Main.cpp:10679-10833`) for
/// exactly the tokens sourceable from <see cref="OperatorSettings"/> and the system clock, plus
/// two new tokens (<c>{name}</c>/<c>{grid}</c>) legacy has no equivalent for at all -- see
/// <see cref="OperatorSettings"/>'s own doc comment for why those two exist. Legacy's much larger
/// token set (his-callsign/his-name/his-QTH/RST exchange/time-of-day greetings) needs a "current
/// QSO" form concept this port doesn't have yet -- deliberately out of scope here, not silently
/// dropped (`spec/14-roadmap.md`'s TX-macro/CW-ID backlog entry).
/// <para>Phase 3 (spec/15-template-designer.md, "named template variables + fill bar") added
/// <c>{freq}</c>/<c>{mode}</c> (ordinary resolved macros sourced from <paramref name="radioState"/>
/// at each call site, not a live subscription -- see <see cref="Resolve"/>'s own doc comment) and
/// generic <c>{word}</c> VARIABLE references resolved against <paramref name="variables"/> when
/// present in the text but not among the fixed known-token set above -- this reuses the EXISTING
/// bare-brace convention rather than inventing a prefixed syntax, per spec/15's own worked example
/// (<c>{his_call}</c>, <c>{rsv}</c>), and matches plan-review's decided resolution order: variables
/// are resolved strictly AFTER every known macro (a fill VALUE containing <c>{something}</c> is
/// never itself re-resolved -- <see cref="ResolveBraceTokens"/> is a single left-to-right regex
/// pass whose replacement text is never rescanned). [Code-review correction] This is NOT, however,
/// one single scan across BOTH token families: <c>%</c>-tokens resolve in their own earlier pass
/// (<see cref="ResolvePercentTokens"/>), and the brace pass runs over THAT pass's output -- so
/// <c>%</c>-token output (in practice, only <c>OperatorSettings.Callsign</c>) IS visible to the
/// brace-token regex afterward. Real amateur-radio callsigns cannot contain <c>{</c>/<c>}</c>
/// (ITU/FCC callsign format is alphanumeric plus <c>/</c>), so this has no reachable real-world
/// effect today -- documented here so a future caller doesn't rely on a same-scan guarantee that
/// doesn't actually hold, rather than silently restructuring a working, tested two-pass resolver to
/// close a gap nothing can currently trigger. Doc correction (Tier A Batch 10 chunk 10b): "cannot
/// contain" is overstated as a HARD guarantee -- <see cref="OperatorSettings.Callsign"/> is an
/// explicitly unvalidated <c>string?</c> (see that type's own doc comment), so a user COULD type
/// <c>{name}</c> into the callsign box; unreachability rests on real-world convention, not
/// enforcement. The consequence stays benign either way (the odd callsign resolves one level
/// further, or is left verbatim).</para></summary>
public interface IMacroTextResolver
{
    /// <summary>Resolves every recognized token in <paramref name="rawText"/> against
    /// <paramref name="operatorSettings"/>, the current UTC clock, <paramref name="radioState"/>
    /// (Phase 3's <c>{freq}</c>/<c>{mode}</c>), and <paramref name="variables"/> (Phase 3's named
    /// template variables). Unrecognized <c>%&lt;char&gt;</c> sequences resolve to literal
    /// <c>%%</c>, matching legacy's own default case exactly (`Main.cpp:10817-10819`) -- a literal
    /// <c>%%</c> escape hits that same default case in legacy too, so it round-trips as-is.
    /// <c>{word}</c> tokens (character class <c>[A-Za-z0-9_]+</c>, case-SENSITIVE match, same as
    /// the existing <c>{name}</c>/<c>{grid}</c> literal-replace convention) resolve, in order: a
    /// known macro (<c>name</c>/<c>grid</c>/<c>freq</c>/<c>mode</c>) against its own source; failing
    /// that, a key in <paramref name="variables"/> against its stored value; failing that, the
    /// token is left VERBATIM (Phase 3 plan-review decision -- an unfilled variable must NOT
    /// silently resolve to an empty string and vanish from the transmitted image; this also means a
    /// user's ordinary literal <c>{note}</c>-shaped text needs no escape-hatch syntax, since the
    /// worst case is a harmless, ignorable extra fill-bar row, not deleted output).
    /// <paramref name="radioState"/>/<paramref name="variables"/> are optional (default
    /// <see langword="null"/>) so every pre-Phase-3 2-argument call site keeps compiling and
    /// behaving identically (no <c>freq</c>/<c>mode</c>/variable in older call sites' text).</summary>
    string Resolve(string rawText, OperatorSettings operatorSettings, RadioState? radioState = null, IReadOnlyDictionary<string, string>? variables = null);
}

public sealed partial class MacroTextResolver : IMacroTextResolver
{
    // LogConv.cpp:175's MONT1[] -- 1-indexed, index 0 unused, matching legacy's own array shape.
    private static readonly string[] MonthAbbreviations =
        ["", "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    // [A-Za-z0-9_]+ (no spaces -- "{his call}" is not a token), case-sensitive (matches {name}/
    // {grid}'s existing literal-string-Replace behavior: {His_Call} and {his_call} are different
    // tokens, not merged) -- Phase 3 plan-review's own decided token grammar.
    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}")]
    private static partial Regex BraceTokenPattern();

    public string Resolve(string rawText, OperatorSettings operatorSettings, RadioState? radioState = null, IReadOnlyDictionary<string, string>? variables = null)
    {
        if (string.IsNullOrEmpty(rawText))
        {
            return rawText;
        }

        var resolved = ResolvePercentTokens(rawText, operatorSettings);
        return ResolveBraceTokens(resolved, operatorSettings, radioState, variables);
    }

    private static string ResolvePercentTokens(string rawText, OperatorSettings operatorSettings)
    {
        var sb = new StringBuilder(rawText.Length);
        // Doc note (Tier A Batch 10 chunk 10b): legacy's %D/%T don't read the system clock directly
        // -- they call GetUTC (ComLib.cpp:319-323), which applies the user's own m_TimeOffset/
        // m_TimeOffsetMin clock-correction setting (ComLib.cpp:249-251) on top of GetSystemTime. This
        // port has no equivalent setting anywhere (confirmed by grep), so a legacy user who had
        // configured a clock offset would see %D/%T resolve differently here. See
        // docs/removed-features.md's "Macro %D/%T clock-offset correction" entry.
        var now = DateTime.UtcNow;

        for (var i = 0; i < rawText.Length; i++)
        {
            if (rawText[i] != '%')
            {
                sb.Append(rawText[i]);
                continue;
            }

            // Legacy always consumes the character after '%', even at end-of-string
            // (Main.cpp:10689's `p++` runs unconditionally) -- a trailing lone '%' resolves the
            // same way an unrecognized token does, not a crash/no-op.
            var token = i + 1 < rawText.Length ? rawText[i + 1] : '\0';
            i++;

            sb.Append(token switch
            {
                'm' => operatorSettings.Callsign ?? string.Empty, // Main.cpp:10691-10693
                'D' => FormatDate(now), // Main.cpp:10762-10766 (UTC)
                'T' => $"{now.Hour:D2}:{now.Minute:D2}", // Main.cpp:10772-10776 (UTC)
                _ => "%%", // Main.cpp:10817-10819 -- unrecognized AND literal %% both land here
            });
        }

        return sb.ToString();
    }

    // Main.cpp:10673-10675's default case -- this port has no Log.m_LogSet.m_DateType-equivalent
    // setting, so only the one (most common) legacy date format is ported.
    private static string FormatDate(DateTime utcNow) =>
        $"{utcNow.Year:D4}-{MonthAbbreviations[utcNow.Month]}-{utcNow.Day:D2}";

    // Main.cpp has no FREQ/MODE equivalent to verify against -- these are ordinary, non-legacy
    // app-state macros (spec/15-template-designer.md's own framing), same tier as {name}/{grid}.
    // RadioStatusViewModel's own $"{Hz/1_000_000.0:0.000000} MHz" formatting is mirrored here so a
    // template's {freq} reads the same as the header's own live frequency readout.
    // CultureInfo.InvariantCulture (code-review nit, fixed here): unlike the screen-only label this
    // mirrors, this value gets BAKED into the transmitted image -- a comma-decimal culture would
    // otherwise render "14,230000 MHz" in the actual SSTV picture. FrequencyPresetEditorRowViewModel
    // already uses InvariantCulture for this same quantity elsewhere in this codebase.
    private static string FormatFrequency(long frequencyHz) =>
        (frequencyHz / 1_000_000.0).ToString("0.000000", CultureInfo.InvariantCulture) + " MHz";

    // Single regex scan replaces the old two sequential .Replace() calls -- a MatchEvaluator only
    // ever sees the ORIGINAL text, never its own substituted output, which is what makes "a fill
    // VALUE containing {something} is not re-resolved" (Phase 3 plan-review's decided grammar) fall
    // out for free rather than needing a separate guard.
    private static string ResolveBraceTokens(
        string rawText, OperatorSettings operatorSettings, RadioState? radioState, IReadOnlyDictionary<string, string>? variables) =>
        BraceTokenPattern().Replace(rawText, match =>
        {
            var token = match.Groups[1].Value;
            return token switch
            {
                "name" => operatorSettings.Name ?? string.Empty,
                "grid" => operatorSettings.Grid ?? string.Empty,
                "freq" => radioState is { } state ? FormatFrequency(state.FrequencyHz) : string.Empty,
                // .ToUpperInvariant() (code-review nit, fixed here): bare ToString() renders "Usb",
                // not the ham-conventional "USB" -- RadioStatusViewModel already applies the same
                // uppercasing for this exact display elsewhere in this codebase.
                "mode" => radioState is { } state ? state.Mode.ToString().ToUpperInvariant() : string.Empty,
                // Auditor usability review follow-up (2026-08-18): {dist}/{bearing}, combining MY
                // grid (a real settings-tier value, same as {grid} above) with the {his_grid}
                // FILL-BAR variable the operator types in per-QSO -- see MaidenheadLocator's own doc
                // comment for why this isn't a "current QSO" model, just a computed macro over two
                // already-sourceable inputs. Empty (matching {freq}/{mode}'s own "input not
                // available yet" convention) when either grid is missing/unparseable, not a
                // hard error -- a template is a valid thing to preview/edit before HIS grid has been
                // typed in for a given QSO.
                "dist" => TryResolveDistanceBearing(operatorSettings, variables, out var distanceKm, out _) ? MaidenheadLocator.FormatDistance(distanceKm) : string.Empty,
                "bearing" => TryResolveDistanceBearing(operatorSettings, variables, out _, out var bearingDegrees) ? MaidenheadLocator.FormatBearing(bearingDegrees) : string.Empty,
                _ => variables is not null && variables.TryGetValue(token, out var value) ? value : match.Value,
            };
        });

    /// <summary>Shared by the <c>{dist}</c>/<c>{bearing}</c> cases above -- one lookup of both
    /// inputs, not two independently-maintained copies (the <c>{his_grid}</c> variable KEY is a
    /// worked example from spec/15-template-designer.md's own text, not a hardcoded assumption this
    /// project invented -- see that document's "named template variables + a fill bar" functional
    /// scope entry).</summary>
    private static bool TryResolveDistanceBearing(
        OperatorSettings operatorSettings, IReadOnlyDictionary<string, string>? variables, out double distanceKm, out double bearingDegrees)
    {
        distanceKm = 0;
        bearingDegrees = 0;
        if (variables is null || !variables.TryGetValue("his_grid", out var hisGrid))
        {
            return false;
        }

        return MaidenheadLocator.TryComputeDistanceBearing(operatorSettings.Grid, hisGrid, out distanceKm, out bearingDegrees);
    }
}
