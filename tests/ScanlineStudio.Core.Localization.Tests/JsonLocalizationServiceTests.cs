using System.Globalization;
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
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("OnlyInEnglish"));
    }

    [Fact]
    public void GetString_MissingKeyEverywhere_ReturnsKeyItselfAndLogs()
    {
        WriteLocaleFile("en", """{ "Greeting": "Hello" }""");
        var logger = new FakeLogger<JsonLocalizationService>();
        var service = new JsonLocalizationService(_localeDirectory, logger);

        var result = service.GetString("NoSuchKey");

        Assert.Equal("NoSuchKey", result);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("NoSuchKey"));
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
}
