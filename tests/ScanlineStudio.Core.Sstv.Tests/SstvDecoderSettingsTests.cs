using System.Text.Json;

namespace ScanlineStudio.Core.Sstv.Tests;

public sealed class SstvDecoderSettingsTests
{
    [Fact]
    public void RoundTripsThroughItsJsonContext()
    {
        var settings = new SstvDecoderSettings { AfcEnabled = false };

        var json = JsonSerializer.Serialize(settings, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        var roundTripped = JsonSerializer.Deserialize(json, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);

        Assert.Equal(settings, roundTripped);
    }

    [Fact]
    public void MissingAfcEnabledDeserializesToNull_NotFalse()
    {
        // Guards the exact STJ property-default-loss trap this field's nullability exists to avoid
        // (see SstvDecoderSettings.AfcEnabled's own doc comment) -- an existing settings.json whose
        // "SstvDecoder" section is present but predates this specific field must deserialize the
        // section as "unset" (later defaulted to true at the read site), not silently disable AFC.
        var roundTripped = JsonSerializer.Deserialize("{}", SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);

        Assert.NotNull(roundTripped);
        Assert.Null(roundTripped.AfcEnabled);
    }

    [Fact]
    public void DefaultInstance_AfcEnabledFallsBackToTrue_MatchingProgramCsReadSite()
    {
        // The OTHER real upgrade scenario an auditor pass flagged as not fully covered by the test
        // above: a settings.json predating SstvDecoderSettings entirely has no "SstvDecoder" key at
        // all (not just a section missing this one field). That case resolves through
        // AppSettingsSectionExtensions.GetSection returning null for an absent section (already
        // covered generically by ScanlineStudio.Settings.Tests' own GetSection_WhenKeyAbsent_ReturnsDefault),
        // then Program.cs's `?? new SstvDecoderSettings()`, then `?? true` -- this test proves that
        // last link: a freshly-defaulted instance's AfcEnabled still resolves to the desired `true`.
        var settings = new SstvDecoderSettings();

        Assert.True(settings.AfcEnabled ?? true);
    }
}
