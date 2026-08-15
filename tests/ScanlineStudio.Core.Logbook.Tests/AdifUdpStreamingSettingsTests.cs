using System.Text.Json;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook.Tests;

public sealed class AdifUdpStreamingSettingsTests
{
    [Fact]
    public async Task ResolveAsync_NoSectionAtAllConfigured_ReturnsEmptyDestinationsAndNullClientId()
    {
        var settings = await AdifUdpStreamingSettings.ResolveAsync(new FakeSettingsStore());

        Assert.Null(settings.Destinations);
        Assert.Null(settings.ClientId);
    }

    [Fact]
    public async Task ResolveAsync_SectionConfigured_RoundTripsTheDestinationListAndClientId()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AdifUdpStreamingSettings.SectionKey,
                new AdifUdpStreamingSettings
                {
                    Destinations =
                    [
                        new AdifUdpDestination { Enabled = true, Name = "GridTracker", Host = "192.168.1.50", Port = 2237 },
                        new AdifUdpDestination { Enabled = false, Name = "N1MM", Host = "192.168.1.51", Port = 2333 },
                    ],
                    ClientId = "TestClient",
                },
                AdifUdpStreamingSettingsJsonContext.Default.AdifUdpStreamingSettings),
        };

        var settings = await AdifUdpStreamingSettings.ResolveAsync(settingsStore);

        Assert.NotNull(settings.Destinations);
        Assert.Equal(2, settings.Destinations!.Count);
        Assert.Equal("GridTracker", settings.Destinations[0].Name);
        Assert.True(settings.Destinations[0].Enabled);
        Assert.Equal("192.168.1.50", settings.Destinations[0].Host);
        Assert.Equal(2237, settings.Destinations[0].Port);
        Assert.Equal("N1MM", settings.Destinations[1].Name);
        Assert.False(settings.Destinations[1].Enabled);
        Assert.Equal("TestClient", settings.ClientId);
    }

    [Fact]
    public void MigrateIfNeeded_NewSectionAbsentAndLegacyEnabled_SeedsOneGridTrackerDestination()
    {
        var sections = new Dictionary<string, JsonElement>
        {
            [LegacyGridTrackerStreamingSettings.SectionKey] = JsonSerializer.SerializeToElement(
                new LegacyGridTrackerStreamingSettings { Enabled = true, Host = "192.168.1.99", Port = 9999, ClientId = "OldClient" },
                AdifUdpStreamingSettingsJsonContext.Default.LegacyGridTrackerStreamingSettings),
        };

        var settings = AdifUdpStreamingSettings.MigrateIfNeeded(new AppSettings { Sections = sections });

        Assert.NotNull(settings.Destinations);
        var destination = Assert.Single(settings.Destinations!);
        Assert.True(destination.Enabled);
        Assert.Equal("GridTracker", destination.Name);
        Assert.Equal("192.168.1.99", destination.Host);
        Assert.Equal(9999, destination.Port);
        Assert.Equal("OldClient", settings.ClientId);
    }

    [Fact]
    public void MigrateIfNeeded_NewSectionAbsentAndLegacySparse_FallsBackToStandardGridTrackerHostAndPort()
    {
        var sections = new Dictionary<string, JsonElement>
        {
            // Enabled=true but Host/Port never set -- the exact shape a user who only ever flipped
            // the (never UI-exposed) Enabled flag by hand-editing settings.json would have.
            [LegacyGridTrackerStreamingSettings.SectionKey] = JsonSerializer.SerializeToElement(
                new LegacyGridTrackerStreamingSettings { Enabled = true },
                AdifUdpStreamingSettingsJsonContext.Default.LegacyGridTrackerStreamingSettings),
        };

        var settings = AdifUdpStreamingSettings.MigrateIfNeeded(new AppSettings { Sections = sections });

        var destination = Assert.Single(settings.Destinations!);
        Assert.Equal(LegacyGridTrackerStreamingSettings.DefaultHost, destination.Host);
        Assert.Equal(LegacyGridTrackerStreamingSettings.DefaultPort, destination.Port);
    }

    [Fact]
    public void MigrateIfNeeded_NewSectionAbsentAndLegacyNotEnabled_DoesNotMigrate()
    {
        var sections = new Dictionary<string, JsonElement>
        {
            [LegacyGridTrackerStreamingSettings.SectionKey] = JsonSerializer.SerializeToElement(
                new LegacyGridTrackerStreamingSettings { Enabled = false, Host = "192.168.1.99", Port = 9999 },
                AdifUdpStreamingSettingsJsonContext.Default.LegacyGridTrackerStreamingSettings),
        };

        var settings = AdifUdpStreamingSettings.MigrateIfNeeded(new AppSettings { Sections = sections });

        Assert.Null(settings.Destinations);
    }

    [Fact]
    public void MigrateIfNeeded_NeitherSectionPresent_ReturnsEmptyDefaults()
    {
        var settings = AdifUdpStreamingSettings.MigrateIfNeeded(new AppSettings());

        Assert.Null(settings.Destinations);
        Assert.Null(settings.ClientId);
    }

    /// <summary>Round-2 auditor blocker: the new section being genuinely PRESENT (even with an
    /// explicitly empty `Destinations` list -- the user deleted every destination on purpose) must
    /// NEVER re-trigger the legacy migration. Migrating on "empty" instead of "absent" would
    /// silently resurrect GridTracker forwarding on every later load with no UI way to turn it off,
    /// since deleting the migrated row would just recreate it on the next read.</summary>
    [Fact]
    public void MigrateIfNeeded_NewSectionPresentButExplicitlyEmpty_DoesNotReMigrateFromLegacy()
    {
        var sections = new Dictionary<string, JsonElement>
        {
            [AdifUdpStreamingSettings.SectionKey] = JsonSerializer.SerializeToElement(
                new AdifUdpStreamingSettings { Destinations = [] },
                AdifUdpStreamingSettingsJsonContext.Default.AdifUdpStreamingSettings),
            [LegacyGridTrackerStreamingSettings.SectionKey] = JsonSerializer.SerializeToElement(
                new LegacyGridTrackerStreamingSettings { Enabled = true, Host = "192.168.1.99", Port = 9999 },
                AdifUdpStreamingSettingsJsonContext.Default.LegacyGridTrackerStreamingSettings),
        };

        var settings = AdifUdpStreamingSettings.MigrateIfNeeded(new AppSettings { Sections = sections });

        Assert.NotNull(settings.Destinations);
        Assert.Empty(settings.Destinations!);
    }
}
