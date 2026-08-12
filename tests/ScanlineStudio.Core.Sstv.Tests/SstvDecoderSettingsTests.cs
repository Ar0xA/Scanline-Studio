using System.Text.Json;
using ScanlineStudio.Abstractions.Sstv;

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

    [Fact]
    public void RoundTripsThroughItsJsonContext_DemodType()
    {
        var settings = new SstvDecoderSettings { DemodType = DemodType.Pll };

        var json = JsonSerializer.Serialize(settings, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        var roundTripped = JsonSerializer.Deserialize(json, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);

        Assert.Equal(settings, roundTripped);
    }

    [Fact]
    public void MissingDemodTypeDeserializesToNull_NotHilbert()
    {
        // Same STJ property-default-loss trap as AfcEnabled above -- an existing settings.json
        // predating this field must deserialize as "unset" (later defaulted to Hilbert at the
        // Program.cs read site), not silently materialize a non-null default here.
        var roundTripped = JsonSerializer.Deserialize("{}", SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);

        Assert.NotNull(roundTripped);
        Assert.Null(roundTripped.DemodType);
    }

    [Fact]
    public void DefaultInstance_DemodTypeFallsBackToHilbert_MatchingProgramCsReadSite()
    {
        // Same "freshly-defaulted instance" scenario as AfcEnabled's own sibling test above.
        var settings = new SstvDecoderSettings();

        Assert.Equal(DemodType.Hilbert, settings.DemodType ?? DemodType.Hilbert);
    }

    [Fact]
    public void OutOfRangeDemodType_DeserializesAsIs_ReachingProgramCsClampUnvalidated()
    {
        // Round-1 code-review finding: an earlier version of this test asserted a ternary it wrote
        // itself (a tautology -- deleting Program.cs's real clamp would still leave it green) instead
        // of exercising anything real. The one leg actually worth proving, per SstvDecoderSettings.
        // DemodType's own doc comment (a present-but-out-of-range value must clamp to Hilbert, the
        // SAME fallback an absent value gets -- unlike SenseLevel's "different fallback" shape): that
        // STJ's source-generated enum converter does NOT itself reject/clamp an out-of-range integer
        // on deserialize, so Program.cs's `Enum.IsDefined` check is the only thing standing between a
        // hand-edited settings.json and an invalid enum value reaching the decoder constructor. If
        // STJ ever started throwing or clamping on its own, this test would fail, flagging that
        // Program.cs's own clamp logic needs re-examining.
        var roundTripped = JsonSerializer.Deserialize("{\"DemodType\":99}", SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);

        Assert.NotNull(roundTripped);
        Assert.NotNull(roundTripped.DemodType);
        Assert.False(Enum.IsDefined(roundTripped.DemodType!.Value));
        Assert.Equal((DemodType)99, roundTripped.DemodType!.Value);

        // Program.cs's exact clamp expression (decoderSettings.DemodType is { } dt &&
        // Enum.IsDefined(dt) ? dt : DemodType.Hilbert), applied to this real deserialized value.
        var resolved = roundTripped.DemodType is { } dt && Enum.IsDefined(dt) ? dt : DemodType.Hilbert;
        Assert.Equal(DemodType.Hilbert, resolved);
    }

    [Fact]
    public void RoundTripsThroughItsJsonContext_RxBpfPreset()
    {
        var settings = new SstvDecoderSettings { RxBpfPreset = RxBpfPreset.Narrow };

        var json = JsonSerializer.Serialize(settings, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        var roundTripped = JsonSerializer.Deserialize(json, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);

        Assert.Equal(settings, roundTripped);
    }

    [Fact]
    public void MissingRxBpfPresetDeserializesToNull_NotWide()
    {
        // Same STJ property-default-loss trap as AfcEnabled/DemodType above -- an existing
        // settings.json predating this field must deserialize as "unset" (later defaulted to Wide at
        // the Program.cs read site), not silently materialize a non-null default here.
        var roundTripped = JsonSerializer.Deserialize("{}", SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);

        Assert.NotNull(roundTripped);
        Assert.Null(roundTripped.RxBpfPreset);
    }

    [Fact]
    public void DefaultInstance_RxBpfPresetFallsBackToWide_MatchingProgramCsReadSite()
    {
        // Same "freshly-defaulted instance" scenario as AfcEnabled/DemodType's own sibling tests above.
        var settings = new SstvDecoderSettings();

        Assert.Equal(RxBpfPreset.Wide, settings.RxBpfPreset ?? RxBpfPreset.Wide);
    }

    [Fact]
    public void OutOfRangeRxBpfPreset_DeserializesAsIs_ReachingProgramCsClampUnvalidated()
    {
        // Same discipline as DemodType's own sibling test above -- not a tautology asserting a ternary
        // this test writes itself, but proof that STJ's source-generated enum converter does NOT
        // itself reject/clamp an out-of-range integer on deserialize, so Program.cs's Enum.IsDefined
        // check is the only thing standing between a hand-edited settings.json and an invalid enum
        // value reaching the decoder constructor.
        var roundTripped = JsonSerializer.Deserialize("{\"RxBpfPreset\":99}", SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);

        Assert.NotNull(roundTripped);
        Assert.NotNull(roundTripped.RxBpfPreset);
        Assert.False(Enum.IsDefined(roundTripped.RxBpfPreset!.Value));
        Assert.Equal((RxBpfPreset)99, roundTripped.RxBpfPreset!.Value);

        // Program.cs's exact clamp expression (decoderSettings.RxBpfPreset is { } bpf &&
        // Enum.IsDefined(bpf) ? bpf : RxBpfPreset.Wide), applied to this real deserialized value.
        var resolved = roundTripped.RxBpfPreset is { } bpf && Enum.IsDefined(bpf) ? bpf : RxBpfPreset.Wide;
        Assert.Equal(RxBpfPreset.Wide, resolved);
    }
}
