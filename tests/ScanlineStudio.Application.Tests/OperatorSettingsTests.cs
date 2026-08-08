using System.Text.Json;

namespace ScanlineStudio.Application.Tests;

public sealed class OperatorSettingsTests
{
    [Fact]
    public void RoundTripsThroughItsJsonContext()
    {
        var settings = new OperatorSettings { Callsign = "KD9TAW", Name = "Jane", Grid = "EN52" };

        var json = JsonSerializer.Serialize(settings, OperatorSettingsJsonContext.Default.OperatorSettings);
        var roundTripped = JsonSerializer.Deserialize(json, OperatorSettingsJsonContext.Default.OperatorSettings);

        Assert.Equal(settings, roundTripped);
    }

    [Fact]
    public void RoundTripsThroughItsJsonContext_WithNameAndGridUnset()
    {
        // The pre-existing shape (Callsign only, both new fields null) must still round-trip --
        // guards against a future STJ-safety regression on settings files saved before Name/Grid
        // existed.
        var settings = new OperatorSettings { Callsign = "KD9TAW" };

        var json = JsonSerializer.Serialize(settings, OperatorSettingsJsonContext.Default.OperatorSettings);
        var roundTripped = JsonSerializer.Deserialize(json, OperatorSettingsJsonContext.Default.OperatorSettings);

        Assert.Equal(settings, roundTripped);
        Assert.Null(roundTripped!.Name);
        Assert.Null(roundTripped.Grid);
    }
}
