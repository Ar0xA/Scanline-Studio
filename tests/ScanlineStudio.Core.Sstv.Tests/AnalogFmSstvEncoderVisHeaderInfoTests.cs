using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>Un-stub-TX-tab pieces 5 (VIS header row) and 9 (VOX tone row):
/// <see cref="AnalogFmSstvEncoder.GetVisHeaderInfo"/>/<see cref="AnalogFmSstvEncoder.GetLeaderToneDurationMs"/>,
/// the two new public entry points <c>ScanlineStudio.Application.ISstvSessionService</c> exposes to
/// the UI so it doesn't need an architecturally-barred reference to this assembly.</summary>
public class AnalogFmSstvEncoderVisHeaderInfoTests
{
    /// <summary>The returned value is the real transmitted byte (7 data bits plus a computed even
    /// parity bit), NOT the bare <see cref="SstvModeDefinition.VisCode"/> -- Martin M1's VisCode
    /// (44 = 0b0101100, odd bit-count) needs parity bit 1, so its real on-air byte is 172
    /// (0b10101100), matching <see cref="VisHeader.GetTransmittedByte"/> directly (this test's own
    /// point: prove <see cref="AnalogFmSstvEncoder.GetVisHeaderInfo"/> actually calls that, not
    /// something that happens to equal the bare VisCode).</summary>
    [Fact]
    public void GetVisHeaderInfo_StandardMode_ReturnsTheRealTransmittedByte_IncludingComputedParity()
    {
        var (kind, value) = AnalogFmSstvEncoder.GetVisHeaderInfo(SstvModeRegistry.MartinM1);

        Assert.Equal(VisHeaderKind.Standard, kind);
        Assert.Equal(VisHeader.GetTransmittedByte(SstvModeRegistry.MartinM1.VisCode), value);
        Assert.Equal(172, value);
    }

    /// <summary>Legacy's real RM12 VIS byte is <c>0x86</c> (<c>VisHeader.Rm12ForcedParityBit</c>'s
    /// own doc comment) -- NOT its bare <see cref="SstvModeDefinition.VisCode"/> of 6, because RM12's
    /// parity bit is forced rather than computed. This is the exact case plan-review flagged: a
    /// naive "just show VisCode" implementation would silently show the wrong on-air byte for this
    /// one mode.</summary>
    [Fact]
    public void GetVisHeaderInfo_Rm12_ReturnsTheRealForcedParityByte_NotTheBareVisCode()
    {
        var (kind, value) = AnalogFmSstvEncoder.GetVisHeaderInfo(SstvModeRegistry.Rm12);

        Assert.Equal(VisHeaderKind.Standard, kind);
        Assert.Equal(0x86, value);
        Assert.NotEqual(SstvModeRegistry.Rm12.VisCode, value);
    }

    [Fact]
    public void GetVisHeaderInfo_ExtendedFamilyMode_ReturnsTheExtendedVisCode()
    {
        var (kind, value) = AnalogFmSstvEncoder.GetVisHeaderInfo(SstvModeRegistry.Mr73);

        Assert.Equal(VisHeaderKind.Extended, kind);
        Assert.Equal(SstvModeRegistry.Mr73.ExtendedVisCode, value);
    }

    [Fact]
    public void GetVisHeaderInfo_NarrowFamilyMode_ReturnsTheNarrowModeCode()
    {
        var (kind, value) = AnalogFmSstvEncoder.GetVisHeaderInfo(SstvModeRegistry.Mn73);

        Assert.Equal(VisHeaderKind.Narrow, kind);
        Assert.Equal(SstvModeRegistry.Mn73.NarrowModeCode, value);
    }

    /// <summary>AVT is checked FIRST in <c>GenerateFrequencySegments</c>'s real branch order, ahead
    /// of narrow/extended/standard -- <see cref="AnalogFmSstvEncoder.GetVisHeaderInfo"/> must mirror
    /// that precedence exactly, not just "whichever nullable field happens to be set" (AVT's own
    /// <see cref="SstvModeDefinition.NarrowModeCode"/>/<see cref="SstvModeDefinition.ExtendedVisCode"/>
    /// are both null, so this only proves the branch order for AVT specifically if AVT is asserted
    /// against Avt, not inferred from the other three tests).</summary>
    [Fact]
    public void GetVisHeaderInfo_Avt_ReturnsAvtKindWithTheRealTransmittedByte()
    {
        var (kind, value) = AnalogFmSstvEncoder.GetVisHeaderInfo(SstvModeRegistry.Avt);

        Assert.Equal(VisHeaderKind.Avt, kind);
        Assert.Equal(VisHeader.GetTransmittedByte(SstvModeRegistry.Avt.VisCode), value);
    }

    [Fact]
    public void GetLeaderToneDurationMs_NarrowMode_MatchesTheRealEmittedNarrowToneSequence()
    {
        var expected = VisHeader.GenerateOutHeadSegments(narrow: true).Sum(s => s.DurationMs);

        Assert.Equal(expected, AnalogFmSstvEncoder.GetLeaderToneDurationMs(SstvModeRegistry.Mn73));
        Assert.Equal(400, expected);
    }

    [Fact]
    public void GetLeaderToneDurationMs_NormalMode_MatchesTheRealEmittedNormalToneSequence()
    {
        var expected = VisHeader.GenerateOutHeadSegments(narrow: false).Sum(s => s.DurationMs);

        Assert.Equal(expected, AnalogFmSstvEncoder.GetLeaderToneDurationMs(SstvModeRegistry.MartinM1));
        Assert.Equal(800, expected);
    }
}
