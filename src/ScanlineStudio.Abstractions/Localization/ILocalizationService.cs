using System.Globalization;

namespace ScanlineStudio.Abstractions.Localization;

/// <summary>See spec/10-localization.md. Backs every user-visible string in the UI, with genuine
/// runtime language switching (no restart) as the explicit design goal this replaces legacy's
/// startup-only <c>GetThreadLocale()</c> branch with.
///
/// <b>Placeholder format — a deliberate Phase-3 simplification, not the spec's full ambition.</b>
/// <see cref="GetString"/> uses plain positional composite formatting (<c>string.Format</c> against
/// <c>{0}</c>/<c>{1}</c>-style placeholders), not the ICU-style named-placeholder-plus-pluralization
/// engine spec/10 describes. None of the strings this phase introduces need plural forms; revisit
/// with a real pluralization helper only once a string actually needs one, rather than building it
/// speculatively now.
///
/// <b>Concurrency</b>: <see cref="CultureChanged"/> fires synchronously, on whatever thread called
/// <see cref="SetCultureAsync"/> — expected to always be the UI thread in practice (culture is
/// switched from a settings dialog), so no marshaling contract is stated here; this is not a
/// high-frequency stream like <c>IAudioEngine.SamplesCaptured</c>.</summary>
public interface ILocalizationService
{
    IReadOnlyList<CultureInfo> AvailableCultures { get; }

    CultureInfo CurrentCulture { get; }

    /// <summary>Switches the active culture and raises <see cref="CultureChanged"/> once every
    /// locale file for the new culture has finished loading. Passing the already-current culture
    /// is a no-op that still raises <see cref="CultureChanged"/> (callers may rely on it to force a
    /// re-bind).</summary>
    Task SetCultureAsync(CultureInfo culture, CancellationToken ct = default);

    /// <summary>Missing key in the current (non-English) culture falls back to <c>en</c> and logs a
    /// diagnostic-level warning; missing from <c>en</c> too returns the key itself so a UI never
    /// shows a blank string.</summary>
    string GetString(string key, params object[] args);

    event Action? CultureChanged;
}
