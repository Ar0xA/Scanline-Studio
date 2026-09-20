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
    private readonly object _loadedCulturesLock = new();
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
        bool alreadyLoaded;
        lock (_loadedCulturesLock)
        {
            alreadyLoaded = _loadedCultures.ContainsKey(culture.TwoLetterISOLanguageName);
        }

        if (!alreadyLoaded)
        {
            // Tier C audit finding: NOT ConfigureAwait(false) -- ILocalizationService.CultureChanged's
            // own doc comment promises this fires synchronously, on whatever thread called this
            // method (expected to always be the UI thread). ConfigureAwait(false) resumed this
            // continuation (the dictionary write and the event raise below) on a thread-pool thread
            // instead whenever the file read genuinely went async, breaking that contract and every
            // subscriber that assumes it (e.g. a XAML binding source raising PropertyChanged with no
            // dispatcher marshaling) -- and inconsistently, since the already-loaded fast path above
            // never awaited at all and always ran on the caller's own thread either way.
            var map = await LoadCultureMapAsync(culture.TwoLetterISOLanguageName, ct);
            lock (_loadedCulturesLock)
            {
                _loadedCultures[culture.TwoLetterISOLanguageName] = map;
            }
        }

        CurrentCulture = culture;
        RaiseCultureChanged();
    }

    /// <summary>A bare multicast <c>CultureChanged?.Invoke()</c> stops dead at the first subscriber
    /// that throws, silently skipping every subscriber registered after it -- the only production
    /// caller, <c>OptionsWindowViewModel.SaveCoreAsync</c>, catches and only logs, so this can happen
    /// with no visible trace. One subscriber's own bug must not mute every other bound control in the
    /// app for the rest of the session -- same "one field's failure must not abort the other unrelated
    /// writes" convention this codebase already applies elsewhere (e.g. that same Save method's own
    /// per-field try/catch blocks).</summary>
    private void RaiseCultureChanged()
    {
        if (CultureChanged is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                Log.CultureChangedSubscriberThrew(_logger, ex);
            }
        }
    }

    public string GetString(string key, params object[] args)
    {
        var cultureCode = CurrentCulture.TwoLetterISOLanguageName;

        // Tier C audit finding: guarded by the same lock SetCultureAsync now uses to publish into
        // this dictionary -- GetString runs on every binding evaluation, concurrently with a rare
        // but real SetCultureAsync write from a culture-switch command; a plain Dictionary read
        // racing an insert is a genuine data race (torn bucket state), not just a stale read.
        Dictionary<string, string>? map;
        lock (_loadedCulturesLock)
        {
            _loadedCultures.TryGetValue(cultureCode, out map);
        }

        // Tier C audit finding: an empty STORED value (a translator left the key blank, a real and
        // expected state for an in-progress community translation per spec/10) used to be returned
        // as-is here -- a silent blank UI label with no diagnostic trail, breaking
        // ILocalizationService's own documented "a UI never shows a blank string" guarantee. Treated
        // the same as a genuinely missing key: falls through to the English fallback, then to the
        // raw key itself -- both of which the interface's contract already covers.
        if (map is not null && map.TryGetValue(key, out var value)
            && TryFormat(key, cultureCode, value, args, out var formatted))
        {
            return formatted;
        }

        if (!string.Equals(cultureCode, FallbackCultureCode, StringComparison.OrdinalIgnoreCase)
            && _fallbackMap.TryGetValue(key, out var fallbackValue)
            && TryFormat(key, FallbackCultureCode, fallbackValue, args, out var fallbackFormatted))
        {
            Log.KeyMissingFallingBackToEnglish(_logger, key, CurrentCulture.Name);
            return fallbackFormatted;
        }

        Log.KeyMissingInFallbackLocale(_logger, key);
        return key;
    }

    private bool TryFormat(string key, string culture, string? template, object[] args, out string formatted)
    {
        formatted = string.Empty;
        if (string.IsNullOrEmpty(template))
        {
            return false;
        }

        try
        {
            formatted = args.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, args);
            return true;
        }
        catch (FormatException ex)
        {
            Log.InvalidTranslationFormat(_logger, key, culture, ex);
            return false;
        }
    }

    // Tier C audit finding (blocker): this method and its two siblings below run from the
    // constructor with no exception handling at all -- a corrupt/malformed locale file (this
    // project's own established "single bad byte bricked startup" failure class, already fixed for
    // JsonSettingsStore's own settings.json one Tier C group ago) crashed the whole app at DI
    // resolution with no log trace, on a file class MORE exposed to hand-editing than settings.json
    // (spec/10's own "Adding a language" workflow explicitly sells locale files as drop-in,
    // no-recompile, community-editable). Same catch filter as JsonSettingsStore.LoadAsync, same
    // "log and fall back" pattern. Also fixes a smaller asymmetry: this sync (ctor-path) overload
    // used to silently return an empty map on a missing file with no log at all, unlike its async
    // twin below -- a missing English fallback locale rendered every UI string as its raw key with
    // zero startup diagnostic.
    private Dictionary<string, string> LoadCultureMap(string code)
    {
        var path = Path.Combine(_localeDirectory, $"{code}.json");
        if (!File.Exists(path))
        {
            Log.LocaleFileNotFound(_logger, code, path);
            return new Dictionary<string, string>();
        }

        try
        {
            using var stream = File.OpenRead(path);
            var map = JsonSerializer.Deserialize(stream, LocalizationJsonContext.Default.DictionaryStringString);
            return map ?? new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.LocaleFileLoadFailed(_logger, code, path, ex);
            return new Dictionary<string, string>();
        }
    }

    private async Task<Dictionary<string, string>> LoadCultureMapAsync(string code, CancellationToken ct)
    {
        var path = Path.Combine(_localeDirectory, $"{code}.json");
        if (!File.Exists(path))
        {
            Log.LocaleFileNotFound(_logger, code, path);
            return new Dictionary<string, string>();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var map = await JsonSerializer.DeserializeAsync(stream, LocalizationJsonContext.Default.DictionaryStringString, ct)
                .ConfigureAwait(false);
            return map ?? new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.LocaleFileLoadFailed(_logger, code, path, ex);
            return new Dictionary<string, string>();
        }
    }

    private List<CultureInfo> LoadManifest(string localeDirectory)
    {
        var manifestPath = Path.Combine(localeDirectory, "locales.json");
        if (!File.Exists(manifestPath))
        {
            return [CultureInfo.GetCultureInfo(FallbackCultureCode)];
        }

        List<LocaleManifestEntry>? entries;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            entries = JsonSerializer.Deserialize(stream, LocalizationJsonContext.Default.ListLocaleManifestEntry);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.ManifestLoadFailed(_logger, manifestPath, ex);
            return [CultureInfo.GetCultureInfo(FallbackCultureCode)];
        }

        if (entries is null || entries.Count == 0)
        {
            return [CultureInfo.GetCultureInfo(FallbackCultureCode)];
        }

        // A malformed individual entry (e.g. a typo'd culture code) must not take down the whole
        // manifest -- skip and log just that entry, matching the file-level fallback's own
        // "degrade, don't crash" reasoning above.
        //
        // Tier C audit finding (round-2 confirmation): a null/absent Code -- LocaleManifestEntry is
        // a positional record, and System.Text.Json supplies default(T) (null, for a string) for any
        // ctor param with no matching JSON property; nullable reference types are NOT enforced at
        // runtime -- reached CultureInfo.GetCultureInfo(null) and threw ArgumentNullException, which
        // this catch's original CultureNotFoundException-only filter did not catch, escaping the same
        // way the original blocker did (a wrong JSON field name, e.g. "language" instead of "code",
        // or a literal `null` array element). Guarded explicitly, and the catch widened to
        // ArgumentException (CultureNotFoundException's own base type, and ArgumentNullException's).
        var cultures = new List<CultureInfo>();
        foreach (var entry in entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Code))
            {
                Log.ManifestEntryInvalid(_logger, entry?.Code ?? "<null>", new InvalidOperationException("Manifest entry has a null or blank culture code."));
                continue;
            }

            try
            {
                cultures.Add(CultureInfo.GetCultureInfo(entry.Code));
            }
            catch (ArgumentException ex)
            {
                Log.ManifestEntryInvalid(_logger, entry.Code, ex);
            }
        }

        return cultures.Count > 0 ? cultures : [CultureInfo.GetCultureInfo(FallbackCultureCode)];
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Localization key '{Key}' has an invalid format for culture '{Culture}'; using fallback.")]
        public static partial void InvalidTranslationFormat(ILogger logger, string key, string culture, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "A CultureChanged subscriber threw; other subscribers still ran.")]
        public static partial void CultureChangedSubscriberThrew(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Localization key '{Key}' missing for culture '{Culture}'; falling back to English.")]
        public static partial void KeyMissingFallingBackToEnglish(ILogger logger, string key, string culture);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Localization key '{Key}' missing in the English fallback locale.")]
        public static partial void KeyMissingInFallbackLocale(ILogger logger, string key);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Locale file for culture '{Culture}' not found at '{Path}'.")]
        public static partial void LocaleFileNotFound(ILogger logger, string culture, string path);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to load locale file for culture '{Culture}' from '{Path}'; using an empty map.")]
        public static partial void LocaleFileLoadFailed(ILogger logger, string culture, string path, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to load locale manifest from '{Path}'; falling back to English only.")]
        public static partial void ManifestLoadFailed(ILogger logger, string path, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Locale manifest entry with code '{Code}' is not a recognized culture; skipped.")]
        public static partial void ManifestEntryInvalid(ILogger logger, string code, Exception ex);
    }
}
