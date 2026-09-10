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

    [Fact]
    public void FrequencyPreset_WrittenBeforeTheBandwidthFieldExisted_DeserializesToNullNotZero()
    {
        // Proves the upgrade path rather than assuming it. A misleading 0 here would be actively
        // harmful: 0 IS Hamlib's RIG_PASSBAND_NORMAL, so it would tell the rig to pick its own
        // width -- the exact behaviour per-Favourite bandwidth exists to replace. The guarantee
        // comes from the parameter being int? (default(int?) and the declared default agree), NOT
        // from the constructor default itself, so this must go through the source-gen context and
        // not a reflection-based Deserialize.
        const string legacyJson = """{"Presets":[{"Label":"40m SSTV","FrequencyHz":7171000,"Mode":0}]}""";

        var roundTripped = JsonSerializer.Deserialize(legacyJson, FrequencyPresetsSettingsJsonContext.Default.FrequencyPresetsSettings);

        var preset = Assert.Single(roundTripped!.Presets);
        Assert.Null(preset.BandwidthHz);
        Assert.Equal(RadioMode.Lsb, preset.Mode);
    }

    [Fact]
    public void FrequencyPreset_BandwidthRoundTripsThroughItsJsonContext()
    {
        var settings = new FrequencyPresetsSettings
        {
            Presets = [new FrequencyPreset("10m FM", 29_600_000, RadioMode.Fm, 15_000)],
        };

        var json = JsonSerializer.Serialize(settings, FrequencyPresetsSettingsJsonContext.Default.FrequencyPresetsSettings);
        var roundTripped = JsonSerializer.Deserialize(json, FrequencyPresetsSettingsJsonContext.Default.FrequencyPresetsSettings);

        Assert.Equal(15_000, Assert.Single(roundTripped!.Presets).BandwidthHz);
    }
}
