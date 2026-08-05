using System.Text.Json;
using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>Confirms the new settings sections' source-generated <c>JsonSerializerContext</c>s
/// actually round-trip -- <see cref="FrequencyPresetsSettings"/> in particular nests a list of
/// records, worth a real smoke test rather than assuming source-gen handles it.</summary>
public sealed class NewSettingsSectionsSerializationTests
{
    [Fact]
    public void RadioSafetySettings_RoundTripsThroughItsJsonContext()
    {
        var settings = new RadioSafetySettings { SwrCutoffEnabled = true, SwrCutoffThreshold = 2.5 };

        var json = JsonSerializer.Serialize(settings, RadioSafetySettingsJsonContext.Default.RadioSafetySettings);
        var roundTripped = JsonSerializer.Deserialize(json, RadioSafetySettingsJsonContext.Default.RadioSafetySettings);

        Assert.Equal(settings, roundTripped);
    }

    [Fact]
    public void FrequencyPresetsSettings_RoundTripsAListOfPresetsThroughItsJsonContext()
    {
        var settings = new FrequencyPresetsSettings
        {
            Presets =
            [
                new FrequencyPreset("40m SSTV", 7_171_000, RadioMode.Lsb),
                new FrequencyPreset("20m SSTV", 14_230_000, RadioMode.Usb),
            ],
        };

        var json = JsonSerializer.Serialize(settings, FrequencyPresetsSettingsJsonContext.Default.FrequencyPresetsSettings);
        var roundTripped = JsonSerializer.Deserialize(json, FrequencyPresetsSettingsJsonContext.Default.FrequencyPresetsSettings);

        Assert.Equal(settings.Presets, roundTripped!.Presets);
    }
}
