namespace ScanlineStudio.UI.Services;

/// <summary>Resolves which offline help-guide HTML file to open for a given interface language
/// (BACKLOG.md R7). English (<c>help/index.html</c>) is both the default and the fallback; a
/// translated guide lives at <c>help/&lt;languageCode&gt;/index.html</c>, using the same
/// <see cref="System.Globalization.CultureInfo.TwoLetterISOLanguageName"/> code
/// <c>JsonLocalizationService</c> already uses for <c>assets/locale/&lt;code&gt;.json</c>. A
/// missing translated file falls back to English rather than failing to open the guide.</summary>
public static class HelpGuidePathResolver
{
    public static string Resolve(string helpRootDirectory, string languageCode)
    {
        if (languageCode != "en")
        {
            var candidate = Path.Combine(helpRootDirectory, languageCode, "index.html");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(helpRootDirectory, "index.html");
    }
}
