using System.Text.Json;

namespace ScanlineStudio.Application.Tests;

public sealed class OperatorSettingsTests
{
    [Fact]
    public void RoundTripsThroughItsJsonContext()
    {
        var settings = new OperatorSettings { Callsign = "KD9TAW", Name = "Jane", Grid = "EN52", DefaultRst = "579" };

        var json = JsonSerializer.Serialize(settings, OperatorSettingsJsonContext.Default.OperatorSettings);
        var roundTripped = JsonSerializer.Deserialize(json, OperatorSettingsJsonContext.Default.OperatorSettings);

        Assert.Equal(settings, roundTripped);
    }

    /// <summary>RST default plan (2026-09-01): DefaultRst deliberately has NO non-null property
    /// initializer (see its own doc comment for the confirmed STJ trap that would otherwise cause) --
    /// a pre-existing settings.json missing this key must deserialize it as null, same as
    /// Name/Grid, NOT as "595". The "595" backfill happens one layer up, at
    /// <c>OptionsSettingsService.Defaults</c>/<c>LoadAsync</c>'s own "?? DefaultRstFallback" read
    /// sites -- covered by <c>OptionsWindowViewModelTests.Constructor_WithNoDefaultRstKeyOnDisk_FallsBackTo595</c>,
    /// not here.</summary>
    [Fact]
    public void RoundTripsThroughItsJsonContext_WithNameGridAndDefaultRstUnset()
    {
        // The pre-existing shape (Callsign only, every other field null) must still round-trip --
        // guards against a future STJ-safety regression on settings files saved before Name/Grid/
        // DefaultRst existed.
        var settings = new OperatorSettings { Callsign = "KD9TAW" };

        var json = JsonSerializer.Serialize(settings, OperatorSettingsJsonContext.Default.OperatorSettings);
        var roundTripped = JsonSerializer.Deserialize(json, OperatorSettingsJsonContext.Default.OperatorSettings);

        Assert.Equal(settings, roundTripped);
        Assert.Null(roundTripped!.Name);
        Assert.Null(roundTripped.Grid);
        Assert.Null(roundTripped.DefaultRst);
    }
}
