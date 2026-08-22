using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Localization;

namespace ScanlineStudio.Core.Localization.Tests;

public sealed class JsonLocalizationServiceTests : IDisposable
{
    private readonly string _localeDirectory = Directory.CreateTempSubdirectory("yoniq-locale-tests-").FullName;

    public void Dispose() => Directory.Delete(_localeDirectory, recursive: true);

    private void WriteLocaleFile(string code, string json)
        => File.WriteAllText(Path.Combine(_localeDirectory, $"{code}.json"), json);

    private void WriteManifest(string json)
        => File.WriteAllText(Path.Combine(_localeDirectory, "locales.json"), json);

    [Fact]
    public void Constructor_LoadsEnglishEagerly_GetStringWorksImmediately()
    {
        WriteLocaleFile("en", """{ "Greeting": "Hello" }""");

        ILocalizationService service = new JsonLocalizationService(_localeDirectory, NullLogger<JsonLocalizationService>.Instance);

        Assert.Equal("Hello", service.GetString("Greeting"));
        Assert.Equal("en", service.CurrentCulture.TwoLetterISOLanguageName);
    }

    [Fact]
    public async Task SetCultureAsync_SwitchesCultureAndRaisesCultureChanged()
    {
        WriteLocaleFile("en", """{ "Greeting": "Hello" }""");
        WriteLocaleFile("de", """{ "Greeting": "Hallo" }""");
        WriteManifest("""[{"code":"en","displayName":"English"},{"code":"de","displayName":"Deutsch"}]""");
        var service = new JsonLocalizationService(_localeDirectory, NullLogger<JsonLocalizationService>.Instance);

        var raised = 0;
        service.CultureChanged += () => raised++;
        await service.SetCultureAsync(CultureInfo.GetCultureInfo("de"));

        Assert.Equal(1, raised);
        Assert.Equal("de", service.CurrentCulture.TwoLetterISOLanguageName);
        Assert.Equal("Hallo", service.GetString("Greeting"));
    }

    [Fact]
    public async Task GetString_MissingKeyInNonEnglishCulture_FallsBackToEnglishAndLogs()
    {
        WriteLocaleFile("en", """{ "Greeting": "Hello", "OnlyInEnglish": "English only" }""");
        WriteLocaleFile("de", """{ "Greeting": "Hallo" }""");
        var logger = new FakeLogger<JsonLocalizationService>();
        var service = new JsonLocalizationService(_localeDirectory, logger);
        await service.SetCultureAsync(CultureInfo.GetCultureInfo("de"));

        var result = service.GetString("OnlyInEnglish");

        Assert.Equal("English only", result);
        // Tier C audit finding: was `e.Message.Contains("OnlyInEnglish")` -- both this log template
        // and the "missing everywhere" template below embed the key, so that assertion alone
        // couldn't distinguish "fell back to English" from "missing everywhere." Asserting on the
        // distinguishing wording instead.
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("falling back to English", StringComparison.Ordinal));
    }

    [Fact]
    public void GetString_MissingKeyEverywhere_ReturnsKeyItselfAndLogs()
    {
        WriteLocaleFile("en", """{ "Greeting": "Hello" }""");
        var logger = new FakeLogger<JsonLocalizationService>();
        var service = new JsonLocalizationService(_localeDirectory, logger);

        var result = service.GetString("NoSuchKey");

        Assert.Equal("NoSuchKey", result);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("missing in the English fallback locale", StringComparison.Ordinal));
    }

    [Fact]
    public void GetString_WithPositionalArgs_FormatsTemplate()
    {
        WriteLocaleFile("en", """{ "Logbook.ContactCount": "{0} contacts logged" }""");
        var service = new JsonLocalizationService(_localeDirectory, NullLogger<JsonLocalizationService>.Instance);

        Assert.Equal("3 contacts logged", service.GetString("Logbook.ContactCount", 3));
    }

    [Fact]
    public void Constructor_MissingManifest_FallsBackToEnglishOnlyWithoutThrowing()
    {
        WriteLocaleFile("en", """{ "Greeting": "Hello" }""");

        var service = new JsonLocalizationService(_localeDirectory, NullLogger<JsonLocalizationService>.Instance);

        Assert.Single(service.AvailableCultures);
        Assert.Equal("en", service.AvailableCultures[0].TwoLetterISOLanguageName);
    }

    // ------------------------------------------------------------------ Tier C audit findings

    [Fact]
    public void Constructor_CorruptEnglishLocaleFile_FallsBackToEmptyMapInsteadOfThrowing()
    {
        // Tier C audit finding (blocker): a corrupt/malformed locale file used to throw
        // uncaught out of the constructor -- the same "single bad byte bricked startup" class
        // already fixed for JsonSettingsStore.LoadAsync one Tier C group ago, on a file class MORE
        // exposed to hand-editing (spec/10's own "Adding a language" workflow sells locale files as
        // drop-in, no-recompile, community-editable).
        WriteLocaleFile("en", "{ this is not valid json");
        var logger = new FakeLogger<JsonLocalizationService>();

        var service = new JsonLocalizationService(_localeDirectory, logger);

        Assert.Equal("Greeting", service.GetString("Greeting"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Failed to load locale file", StringComparison.Ordinal));
    }

    [Fact]
    public void Constructor_CorruptManifest_FallsBackToEnglishOnlyInsteadOfThrowing()
    {
        // Tier C audit finding (blocker): same class as above, for locales.json specifically.
        WriteLocaleFile("en", """{ "Greeting": "Hello" }""");
        WriteManifest("[ this is not valid json");
        var logger = new FakeLogger<JsonLocalizationService>();

        var service = new JsonLocalizationService(_localeDirectory, logger);

        Assert.Single(service.AvailableCultures);
        Assert.Equal("en", service.AvailableCultures[0].TwoLetterISOLanguageName);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Failed to load locale manifest", StringComparison.Ordinal));
    }

    [Fact]
    public void Constructor_ManifestEntryWithInvalidCultureCode_SkipsThatEntryButKeepsTheOthers()
    {
        // Tier C audit finding (blocker, manifest-entry case): a single typo'd culture code (e.g. a
        // translator fat-fingering "locales.json") used to throw CultureNotFoundException out of the
        // WHOLE manifest load, taking every OTHER correctly-configured language down with it.
        WriteLocaleFile("en", """{ "Greeting": "Hello" }""");
        WriteLocaleFile("de", """{ "Greeting": "Hallo" }""");
        WriteManifest("""[{"code":"en","displayName":"English"},{"code":"not a real code","displayName":"Bogus"},{"code":"de","displayName":"Deutsch"}]""");
        var logger = new FakeLogger<JsonLocalizationService>();

        var service = new JsonLocalizationService(_localeDirectory, logger);

        Assert.Equal(2, service.AvailableCultures.Count);
        Assert.Contains(service.AvailableCultures, c => c.TwoLetterISOLanguageName == "en");
        Assert.Contains(service.AvailableCultures, c => c.TwoLetterISOLanguageName == "de");
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not a real code", StringComparison.Ordinal));
    }

    [Fact]
    public void Constructor_ManifestEntryWithNullOrBlankCode_SkipsThatEntryButKeepsTheOthers()
    {
        // Tier C audit finding (round-2 confirmation): LocaleManifestEntry is a positional record --
        // System.Text.Json supplies default(T) (null, for a string) for any ctor param with no
        // matching JSON property; nullable reference types are NOT enforced at runtime. A wrong
        // field name (e.g. "language" instead of "code") or a literal `null` array element used to
        // reach CultureInfo.GetCultureInfo(null), throwing ArgumentNullException/NullReferenceException
        // uncaught -- the per-entry try/catch's original CultureNotFoundException-only filter did NOT
        // catch either, so this specific shape still crashed the whole manifest load (and the
        // constructor) even after the general invalid-code fix above.
        WriteLocaleFile("en", """{ "Greeting": "Hello" }""");
        WriteManifest("""[{"code":"en","displayName":"English"},{"language":"de","displayName":"Deutsch"},null,{"code":"","displayName":"Blank"}]""");
        var logger = new FakeLogger<JsonLocalizationService>();

        var service = new JsonLocalizationService(_localeDirectory, logger);

        Assert.Single(service.AvailableCultures);
        Assert.Equal("en", service.AvailableCultures[0].TwoLetterISOLanguageName);
        Assert.Equal(3, logger.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task GetString_EmptyStringValueInCurrentCulture_TreatedAsMissingFallsBackToEnglish()
    {
        // Tier C audit finding (risk): an empty STORED value (a translator left the key blank -- a
        // real, expected state for an in-progress community translation per spec/10) used to be
        // returned as-is -- a silent blank UI label with no diagnostic trail, breaking
        // ILocalizationService's own documented "a UI never shows a blank string" guarantee.
        WriteLocaleFile("en", """{ "Greeting": "Hello" }""");
        WriteLocaleFile("de", """{ "Greeting": "" }""");
        var service = new JsonLocalizationService(_localeDirectory, NullLogger<JsonLocalizationService>.Instance);
        await service.SetCultureAsync(CultureInfo.GetCultureInfo("de"));

        var result = service.GetString("Greeting");

        Assert.Equal("Hello", result);
    }

    [Fact]
    public void GetString_EmptyStringValueEvenInEnglishFallback_ReturnsKeyItself()
    {
        // Tier C audit finding (risk): same property as above, one level further down the fallback
        // chain -- an empty English fallback value must not be treated as "found" either.
        WriteLocaleFile("en", """{ "Greeting": "" }""");
        var service = new JsonLocalizationService(_localeDirectory, NullLogger<JsonLocalizationService>.Instance);

        var result = service.GetString("Greeting");

        Assert.Equal("Greeting", result);
    }

    [Fact]
    public void Constructor_NonAsciiLocaleFileWithUtf8Bom_LoadsCorrectly()
    {
        // Tier C audit finding: this class's entire purpose is non-English text, but nothing in this
        // file previously exercised non-ASCII content or a UTF-8 BOM (a real, common artifact of a
        // translator saving from an editor that adds one) -- confirmed those aren't accidentally
        // inheriting a legacy CP932 assumption from elsewhere in this codebase (this file/format is
        // new code, not a port), and that the stream-based JsonSerializer overloads used here
        // correctly strip a BOM rather than choking on it.
        var path = Path.Combine(_localeDirectory, "en.json");
        File.WriteAllText(path, """{ "Greeting": "こんにちは / café" }""", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var service = new JsonLocalizationService(_localeDirectory, NullLogger<JsonLocalizationService>.Instance);

        Assert.Equal("こんにちは / café", service.GetString("Greeting"));
    }

    [Fact]
    public async Task SetCultureAsync_MissingLocaleFile_LogsAndFallsBackToEmptyMapInsteadOfThrowing()
    {
        // Tier C audit finding: LoadCultureMapAsync's own "file not found" log was previously
        // untested -- SetCultureAsync to a manifest-listed culture whose {code}.json was never
        // dropped in (a real, easy-to-hit misconfiguration, not exotic) must degrade gracefully.
        WriteLocaleFile("en", """{ "Greeting": "Hello" }""");
        WriteManifest("""[{"code":"en","displayName":"English"},{"code":"fr","displayName":"Français"}]""");
        var logger = new FakeLogger<JsonLocalizationService>();
        var service = new JsonLocalizationService(_localeDirectory, logger);

        await service.SetCultureAsync(CultureInfo.GetCultureInfo("fr"));

        Assert.Equal("fr", service.CurrentCulture.TwoLetterISOLanguageName);
        // The fr map loaded empty (file missing), so this falls through to the English fallback --
        // "Hello," not the raw key itself, since English still has the key.
        Assert.Equal("Hello", service.GetString("Greeting"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not found", StringComparison.Ordinal));
    }
}
