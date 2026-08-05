using System.Text.Json;

namespace ScanlineStudio.Application.Tests;

public sealed class OperatorSettingsTests
{
    [Fact]
    public void RoundTripsThroughItsJsonContext()
    {
        var settings = new OperatorSettings { Callsign = "KD9TAW" };

        var json = JsonSerializer.Serialize(settings, OperatorSettingsJsonContext.Default.OperatorSettings);
        var roundTripped = JsonSerializer.Deserialize(json, OperatorSettingsJsonContext.Default.OperatorSettings);

        Assert.Equal(settings, roundTripped);
    }
}
