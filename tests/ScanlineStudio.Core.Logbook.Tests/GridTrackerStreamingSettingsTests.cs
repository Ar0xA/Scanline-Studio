using System.Text.Json;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook.Tests;

public sealed class GridTrackerStreamingSettingsTests
{
    [Fact]
    public async Task ResolveAsync_NoSectionConfigured_ReturnsDefaultsWithEnabledNull()
    {
        var settings = await GridTrackerStreamingSettings.ResolveAsync(new FakeSettingsStore());

        Assert.Null(settings.Enabled);
        Assert.Null(settings.Host);
        Assert.Null(settings.Port);
        Assert.Null(settings.ClientId);
    }

    [Fact]
    public async Task ResolveAsync_SectionConfigured_RoundTripsEveryField()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                GridTrackerStreamingSettings.SectionKey,
                new GridTrackerStreamingSettings { Enabled = true, Host = "192.168.1.50", Port = 12237, ClientId = "TestClient" },
                GridTrackerStreamingSettingsJsonContext.Default.GridTrackerStreamingSettings),
        };

        var settings = await GridTrackerStreamingSettings.ResolveAsync(settingsStore);

        Assert.True(settings.Enabled);
        Assert.Equal("192.168.1.50", settings.Host);
        Assert.Equal(12237, settings.Port);
        Assert.Equal("TestClient", settings.ClientId);
    }

    [Fact]
    public async Task ResolveAsync_SectionPredatesEveryField_FallsBackToNullNotClrDefaults()
    {
        // Simulates a settings.json saved before this section existed at all: the section key is
        // present in Sections (as it would be once ANY GridTracker field is ever saved) but with
        // an empty object -- the exact shape STJ silently defaults bool?/int?/string? to `null`
        // for (which happens to be the desired default here), but this test exists so a future
        // change to a non-nullable field would be caught the same way
        // ReceiveHistorySettings.MaxEntries' own regression test catches it.
        var sections = new Dictionary<string, JsonElement>
        {
            [GridTrackerStreamingSettings.SectionKey] = JsonDocument.Parse("{}").RootElement,
        };
        var settingsStore = new FakeSettingsStore { Settings = new AppSettings { Sections = sections } };

        var settings = await GridTrackerStreamingSettings.ResolveAsync(settingsStore);

        Assert.Null(settings.Enabled);
        Assert.Null(settings.Host);
        Assert.Null(settings.Port);
    }
}
