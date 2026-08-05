using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Localization;

namespace ScanlineStudio.Core.Localization;

/// <summary>JSON-file-backed <see cref="ILocalizationService"/> — see spec/10-localization.md.
///
/// <b>Startup contract</b>: always constructs into English (<c>en</c>), loaded eagerly and
/// synchronously in the constructor (a tiny local file, read once at composition-root build time —
/// same rationale as this codebase's other eager-in-constructor infrastructure, e.g.
/// <c>HamlibRuntime</c>'s discovery). Restoring a user's previously-persisted non-English culture is
/// the composition root's job (call <see cref="SetCultureAsync"/> once after the DI container is
/// built), not this class's — it has no dependency on <c>ScanlineStudio.Settings</c>.
///
/// <b>Directory layout</b>: <paramref name="localeDirectory"/> contains one flat-key-map
/// <c>{code}.json</c> per culture plus a <c>locales.json</c> manifest (culture code + display name
/// per spec/10's "Adding a language" section). A missing manifest falls back to English-only,
/// treated as a valid (if minimal) configuration, not an error — this class must not require the
/// operator to hand-author a manifest just to run with one language.</summary>
public sealed partial class JsonLocalizationService : ILocalizationService
{
    private const string FallbackCultureCode = "en";

    private readonly string _localeDirectory;
    private readonly ILogger<JsonLocalizationService> _logger;
    private readonly Dictionary<string, Dictionary<string, string>> _loadedCultures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _fallbackMap;

    public JsonLocalizationService(string localeDirectory, ILogger<JsonLocalizationService> logger)
    {
        _localeDirectory = localeDirectory;
        _logger = logger;

        AvailableCultures = LoadManifest(localeDirectory);
        _fallbackMap = LoadCultureMap(FallbackCultureCode);
        _loadedCultures[FallbackCultureCode] = _fallbackMap;
        CurrentCulture = CultureInfo.GetCultureInfo(FallbackCultureCode);
    }

    public IReadOnlyList<CultureInfo> AvailableCultures { get; }

    public CultureInfo CurrentCulture { get; private set; }

    public event Action? CultureChanged;

    public async Task SetCultureAsync(CultureInfo culture, CancellationToken ct = default)
    {
        if (!_loadedCultures.ContainsKey(culture.TwoLetterISOLanguageName))
        {
            var map = await LoadCultureMapAsync(culture.TwoLetterISOLanguageName, ct).ConfigureAwait(false);
            _loadedCultures[culture.TwoLetterISOLanguageName] = map;
        }

        CurrentCulture = culture;
        CultureChanged?.Invoke();
    }

    public string GetString(string key, params object[] args)
    {
        var cultureCode = CurrentCulture.TwoLetterISOLanguageName;
        if (_loadedCultures.TryGetValue(cultureCode, out var map) && map.TryGetValue(key, out var value))
        {
            return Format(value, args);
        }

        if (!string.Equals(cultureCode, FallbackCultureCode, StringComparison.OrdinalIgnoreCase)
            && _fallbackMap.TryGetValue(key, out var fallbackValue))
        {
            Log.KeyMissingFallingBackToEnglish(_logger, key, CurrentCulture.Name);
            return Format(fallbackValue, args);
        }

        Log.KeyMissingInFallbackLocale(_logger, key);
        return key;
    }

    private static string Format(string template, object[] args)
        => args.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, args);

    private Dictionary<string, string> LoadCultureMap(string code)
    {
        var path = Path.Combine(_localeDirectory, $"{code}.json");
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>();
        }

        using var stream = File.OpenRead(path);
        var map = JsonSerializer.Deserialize(stream, LocalizationJsonContext.Default.DictionaryStringString);
        return map ?? new Dictionary<string, string>();
    }

    private async Task<Dictionary<string, string>> LoadCultureMapAsync(string code, CancellationToken ct)
    {
        var path = Path.Combine(_localeDirectory, $"{code}.json");
        if (!File.Exists(path))
        {
            Log.LocaleFileNotFound(_logger, code, path);
            return new Dictionary<string, string>();
        }

        await using var stream = File.OpenRead(path);
        var map = await JsonSerializer.DeserializeAsync(stream, LocalizationJsonContext.Default.DictionaryStringString, ct)
            .ConfigureAwait(false);
        return map ?? new Dictionary<string, string>();
    }

    private static List<CultureInfo> LoadManifest(string localeDirectory)
    {
        var manifestPath = Path.Combine(localeDirectory, "locales.json");
        if (!File.Exists(manifestPath))
        {
            return [CultureInfo.GetCultureInfo(FallbackCultureCode)];
        }

        using var stream = File.OpenRead(manifestPath);
        var entries = JsonSerializer.Deserialize(stream, LocalizationJsonContext.Default.ListLocaleManifestEntry);
        if (entries is null || entries.Count == 0)
        {
            return [CultureInfo.GetCultureInfo(FallbackCultureCode)];
        }

        return entries.Select(e => CultureInfo.GetCultureInfo(e.Code)).ToList();
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Localization key '{Key}' missing for culture '{Culture}'; falling back to English.")]
        public static partial void KeyMissingFallingBackToEnglish(ILogger logger, string key, string culture);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Localization key '{Key}' missing in the English fallback locale.")]
        public static partial void KeyMissingInFallbackLocale(ILogger logger, string key);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Locale file for culture '{Culture}' not found at '{Path}'.")]
        public static partial void LocaleFileNotFound(ILogger logger, string culture, string path);
    }
}
