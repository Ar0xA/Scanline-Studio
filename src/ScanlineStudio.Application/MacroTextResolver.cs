using System.Text;

namespace ScanlineStudio.Application;

/// <summary>Template substitution for TX overlay text (and, later, other macro-consuming
/// features) -- a scoped-down C# port of legacy's <c>MacroText</c> (`Main.cpp:10679-10833`) for
/// exactly the tokens sourceable from <see cref="OperatorSettings"/> and the system clock, plus
/// two new tokens (<c>{name}</c>/<c>{grid}</c>) legacy has no equivalent for at all -- see
/// <see cref="OperatorSettings"/>'s own doc comment for why those two exist. Legacy's much larger
/// token set (his-callsign/his-name/his-QTH/RST exchange/time-of-day greetings) needs a "current
/// QSO" form concept this port doesn't have yet -- deliberately out of scope here, not silently
/// dropped (`spec/14-roadmap.md`'s TX-macro/CW-ID backlog entry).</summary>
public interface IMacroTextResolver
{
    /// <summary>Resolves every recognized token in <paramref name="rawText"/> against
    /// <paramref name="operatorSettings"/> and the current UTC clock. Unrecognized
    /// <c>%&lt;char&gt;</c> sequences resolve to literal <c>%%</c>, matching legacy's own default
    /// case exactly (`Main.cpp:10817-10819`) -- a literal <c>%%</c> escape hits that same default
    /// case in legacy too, so it round-trips as-is. Unrecognized <c>{token}</c> sequences (no
    /// legacy equivalent to match) are left as-is.</summary>
    string Resolve(string rawText, OperatorSettings operatorSettings);
}

public sealed class MacroTextResolver : IMacroTextResolver
{
    // LogConv.cpp:175's MONT1[] -- 1-indexed, index 0 unused, matching legacy's own array shape.
    private static readonly string[] MonthAbbreviations =
        ["", "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    public string Resolve(string rawText, OperatorSettings operatorSettings)
    {
        if (string.IsNullOrEmpty(rawText))
        {
            return rawText;
        }

        var resolved = ResolvePercentTokens(rawText, operatorSettings);
        return ResolveBraceTokens(resolved, operatorSettings);
    }

    private static string ResolvePercentTokens(string rawText, OperatorSettings operatorSettings)
    {
        var sb = new StringBuilder(rawText.Length);
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

    private static string ResolveBraceTokens(string rawText, OperatorSettings operatorSettings) =>
        rawText
            .Replace("{name}", operatorSettings.Name ?? string.Empty)
            .Replace("{grid}", operatorSettings.Grid ?? string.Empty);
}
