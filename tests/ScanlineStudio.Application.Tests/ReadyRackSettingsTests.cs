using System.Text.Json;

namespace ScanlineStudio.Application.Tests;

public sealed class ReadyRackSettingsTests
{
    [Fact]
    public void RoundTripsThroughItsJsonContext()
    {
        var settings = new ReadyRackSettings { PinnedTemplateIds = ["contest_ab12cd34", "sotad_ef56gh78"] };

        var json = JsonSerializer.Serialize(settings, ReadyRackSettingsJsonContext.Default.ReadyRackSettings);
        var roundTripped = JsonSerializer.Deserialize(json, ReadyRackSettingsJsonContext.Default.ReadyRackSettings);

        Assert.Equal(settings.PinnedTemplateIds, roundTripped!.PinnedTemplateIds);
    }

    [Fact]
    public void DefaultInstance_HasAnEmptyPinList()
    {
        var settings = new ReadyRackSettings();

        Assert.Empty(settings.PinnedTemplateIds);
    }
}
