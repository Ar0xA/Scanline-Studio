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

        // Resolve() is the single source of truth for this clamp now (Loopback self-test plan-review
        // finding: Program.cs and the self-test's own decoder construction previously each carried an
        // independent copy of this exact ternary) -- calling the real method, not reproducing its
        // logic inline.
        Assert.Equal(DemodType.Hilbert, roundTripped.Resolve().DemodType);
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

        // Resolve() is the single source of truth for this clamp now -- see DemodType's own sibling
        // test above for why this calls the real method instead of reproducing its logic inline.
        Assert.Equal(RxBpfPreset.Wide, roundTripped.Resolve().RxBpfPreset);
    }

    [Fact]
    public void RoundTripsThroughItsJsonContext_RxBufferMode()
    {
        var settings = new SstvDecoderSettings { RxBufferMode = RxBufferMode.Extended };

        var json = JsonSerializer.Serialize(settings, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        var roundTripped = JsonSerializer.Deserialize(json, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);

        Assert.Equal(settings, roundTripped);
    }

    [Fact]
    public void MissingRxBufferModeDeserializesToNull_NotOn()
    {
        // Same STJ property-default-loss trap as AfcEnabled/DemodType/RxBpfPreset above -- an existing
        // settings.json predating this field must deserialize as "unset" (later defaulted to On at
        // the Program.cs read site), not silently materialize a non-null default here.
        var roundTripped = JsonSerializer.Deserialize("{}", SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);

        Assert.NotNull(roundTripped);
        Assert.Null(roundTripped.RxBufferMode);
    }

    [Fact]
    public void DefaultInstance_RxBufferModeFallsBackToOn_MatchingProgramCsReadSite()
    {
        // Same "freshly-defaulted instance" scenario as AfcEnabled/DemodType/RxBpfPreset's own sibling
        // tests above.
        var settings = new SstvDecoderSettings();

        Assert.Equal(RxBufferMode.On, settings.RxBufferMode ?? RxBufferMode.On);
    }

    [Fact]
    public void OutOfRangeRxBufferMode_DeserializesAsIs_ReachingProgramCsClampUnvalidated()
    {
        // Same discipline as DemodType/RxBpfPreset's own sibling tests above -- not a tautology
        // asserting a ternary this test writes itself, but proof that STJ's source-generated enum
        // converter does NOT itself reject/clamp an out-of-range integer on deserialize, so
        // Program.cs's Enum.IsDefined check is the only thing standing between a hand-edited
        // settings.json and an invalid enum value reaching the decoder constructor.
        var roundTripped = JsonSerializer.Deserialize("{\"RxBufferMode\":99}", SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);

        Assert.NotNull(roundTripped);
        Assert.NotNull(roundTripped.RxBufferMode);
        Assert.False(Enum.IsDefined(roundTripped.RxBufferMode!.Value));
        Assert.Equal((RxBufferMode)99, roundTripped.RxBufferMode!.Value);

        // Resolve() is the single source of truth for this clamp now -- see DemodType's own sibling
        // test above for why this calls the real method instead of reproducing its logic inline.
        Assert.Equal(RxBufferMode.On, roundTripped.Resolve().RxBufferMode);
    }

    [Fact]
    public void Resolve_DefaultInstance_MatchesEveryDocumentedDesiredDefault()
    {
        // Single source of truth for the whole absent-vs-out-of-range resolution (see this method's
        // own doc comment) -- covers every field, not just the four with a pre-existing
        // "DefaultInstance_XFallsBackToY" sibling test above (AfcEnabled/DemodType/RxBpfPreset/
        // RxBufferMode). SenseLevel's own desired absent-default (1) is distinct from its
        // present-but-out-of-range fallback (0) -- see SstvDecoderSettings' own class doc comment --
        // this test covers only the absent case; AnalogFmSstvDecoder's own constructor covers the
        // out-of-range one (SenseLevelForTests-backed tests).
        var resolved = new SstvDecoderSettings().Resolve();

        Assert.True(resolved.AfcEnabled);
        Assert.True(resolved.SyncRestartEnabled);
        Assert.True(resolved.AutoSyncEnabled);
        Assert.False(resolved.AutoStopEnabled);
        Assert.True(resolved.AutoSlantEnabled);
        Assert.Equal(1, resolved.SenseLevel);
        Assert.Equal(DemodType.Hilbert, resolved.DemodType);
        Assert.Equal(RxBpfPreset.Wide, resolved.RxBpfPreset);
        Assert.Equal(RxBufferMode.On, resolved.RxBufferMode);
    }

    [Fact]
    public void Resolve_ExplicitValues_PassThroughUnchanged()
    {
        var settings = new SstvDecoderSettings
        {
            AfcEnabled = false,
            SyncRestartEnabled = false,
            AutoSyncEnabled = false,
            AutoStopEnabled = true,
            AutoSlantEnabled = false,
            SenseLevel = 3,
            DemodType = DemodType.Pll,
            RxBpfPreset = RxBpfPreset.Narrow,
            RxBufferMode = RxBufferMode.Extended,
        };

        var resolved = settings.Resolve();

        Assert.False(resolved.AfcEnabled);
        Assert.False(resolved.SyncRestartEnabled);
        Assert.False(resolved.AutoSyncEnabled);
        Assert.True(resolved.AutoStopEnabled);
        Assert.False(resolved.AutoSlantEnabled);
        Assert.Equal(3, resolved.SenseLevel);
        Assert.Equal(DemodType.Pll, resolved.DemodType);
        Assert.Equal(RxBpfPreset.Narrow, resolved.RxBpfPreset);
        Assert.Equal(RxBufferMode.Extended, resolved.RxBufferMode);
    }
}
