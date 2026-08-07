using System.Text.Json;

namespace ScanlineStudio.Application.Tests;

public sealed class AppPerformanceSettingsTests
{
    [Fact]
    public void RoundTripsThroughItsJsonContext()
    {
        var settings = new AppPerformanceSettings { ProcessPriority = System.Diagnostics.ProcessPriorityClass.High };

        var json = JsonSerializer.Serialize(settings, AppPerformanceSettingsJsonContext.Default.AppPerformanceSettings);
        var roundTripped = JsonSerializer.Deserialize(json, AppPerformanceSettingsJsonContext.Default.AppPerformanceSettings);

        Assert.Equal(settings, roundTripped);
    }

    [Fact]
    public void MissingProcessPriorityDeserializesToNull_NotSomeNonNullDefault()
    {
        // Guards the exact STJ property-default-loss trap this field's nullability exists to avoid
        // (see AppPerformanceSettings.ProcessPriority's own doc comment) -- a settings.json saved
        // before this field existed must deserialize the section as "unset," not silently apply some
        // priority the user never chose.
        var roundTripped = JsonSerializer.Deserialize("{}", AppPerformanceSettingsJsonContext.Default.AppPerformanceSettings);

        Assert.NotNull(roundTripped);
        Assert.Null(roundTripped.ProcessPriority);
    }
}
