namespace Yoniq.Core.Localization;

/// <summary>One row of <c>locales.json</c> — see spec/10-localization.md's "Adding a language"
/// section. Dropping a new <c>xx.json</c> file plus a manifest row is the entire cost of adding a
/// locale; no recompilation.</summary>
public sealed record LocaleManifestEntry(string Code, string DisplayName);
