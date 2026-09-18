using System.Text.Json;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>User-directed default flip (2026-09-18): <see cref="StationIdSettings.FskIdRxEnabled"/>/
/// <see cref="StationIdSettings.CwIdRxEnabled"/> moved from plain non-nullable <c>bool</c> (default
/// <see langword="false"/>, matching the CLR default -- no serialization hazard) to a real default of
/// <see langword="true"/>, which does NOT match the CLR default. A non-null <c>bool</c> property
/// initializer was tried first and found to be silently ignored by the source-generated
/// <see cref="JsonSerializerContext"/> for an absent JSON key -- confirmed with a standalone repro
/// against this exact source-gen shape, matching this project's own documented
/// <see cref="ScanlineStudio.Core.Audio.AudioDeviceSettings.CaptureThreadPriority"/> precedent -- so
/// both fields became nullable instead, resolved via "?? Default..." at every read site. This test
/// proves that specific mechanism through the REAL source-gen context, not the in-memory
/// object-construction path (which never exercises serialization and would not have caught the
/// original bug), mirroring <c>NewSettingsSectionsSerializationTests</c>'
/// <c>FrequencyPreset_WrittenBeforeTheBandwidthFieldExisted_DeserializesToNullNotZero</c> precedent
/// in <c>ScanlineStudio.Core.Radio.Tests</c>.</summary>
public sealed class StationIdSettingsSerializationTests
{
    [Fact]
    public void AbsentSection_DeserializesFskIdRxEnabledAndCwIdRxEnabledToNull_NotTheirIntendedTrueDefault()
    {
        // "{}" -- an absent StationId section, or one written before these two fields existed --
        // must NOT silently resolve to the CLR default (false) via the property itself; every real
        // consumer applies "?? StationIdSettings.DefaultFskIdRxEnabled"/"DefaultCwIdRxEnabled" at
        // its own read site instead (OptionsSettingsService.cs, SstvSessionService.cs).
        const string json = "{}";

        var result = JsonSerializer.Deserialize(json, StationIdSettingsJsonContext.Default.StationIdSettings);

        Assert.Null(result!.FskIdRxEnabled);
        Assert.Null(result.CwIdRxEnabled);
    }

    [Fact]
    public void ExplicitFalse_RoundTripsAsFalse_NotOverwrittenByTheNewDefault()
    {
        // The other half of the same guarantee: a settings.json a user already saved (explicitly
        // "false", from before this default flip) must keep meaning false forever, not silently
        // flip to the new true default on next load.
        const string json = """{"FskIdRxEnabled":false,"CwIdRxEnabled":false}""";

        var result = JsonSerializer.Deserialize(json, StationIdSettingsJsonContext.Default.StationIdSettings);

        Assert.False(result!.FskIdRxEnabled);
        Assert.False(result.CwIdRxEnabled);
    }

    [Fact]
    public void ExplicitTrue_RoundTripsAsTrue()
    {
        const string json = """{"FskIdRxEnabled":true,"CwIdRxEnabled":true}""";

        var result = JsonSerializer.Deserialize(json, StationIdSettingsJsonContext.Default.StationIdSettings);

        Assert.True(result!.FskIdRxEnabled);
        Assert.True(result.CwIdRxEnabled);
    }
}
