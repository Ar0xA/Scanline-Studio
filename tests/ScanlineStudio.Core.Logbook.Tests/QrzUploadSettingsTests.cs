using System.Text.Json;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook.Tests;

public sealed class QrzUploadSettingsTests
{
    [Fact]
    public async Task ResolveAsync_NoSectionConfigured_ReturnsDisabledWithNoApiKey()
    {
        var settings = await QrzUploadSettings.ResolveAsync(new FakeSettingsStore());

        Assert.Null(settings.Enabled);
        Assert.Null(settings.ApiKey);
    }

    [Fact]
    public async Task ResolveAsync_SectionConfigured_RoundTripsEveryField()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                QrzUploadSettings.SectionKey,
                new QrzUploadSettings { Enabled = true, ApiKey = "ABCD-0A0B-1C1D-2E2F" },
                QrzUploadSettingsJsonContext.Default.QrzUploadSettings),
        };

        var settings = await QrzUploadSettings.ResolveAsync(settingsStore);

        Assert.True(settings.Enabled);
        Assert.Equal("ABCD-0A0B-1C1D-2E2F", settings.ApiKey);
    }

    [Fact]
    public async Task ResolveAsync_SectionPresentButEmpty_FallsBackToNullNotClrDefaults()
    {
        var sections = new Dictionary<string, JsonElement>
        {
            [QrzUploadSettings.SectionKey] = JsonDocument.Parse("{}").RootElement,
        };
        var settingsStore = new FakeSettingsStore { Settings = new AppSettings { Sections = sections } };

        var settings = await QrzUploadSettings.ResolveAsync(settingsStore);

        Assert.Null(settings.Enabled);
        Assert.Null(settings.ApiKey);
    }
}
