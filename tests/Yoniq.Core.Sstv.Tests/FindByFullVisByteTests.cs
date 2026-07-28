using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Table-driven verification of <see cref="SstvModeRegistry.FindByFullVisByte"/> against real
/// legacy bytes read directly from `sstv.cpp:1993-2074`'s <c>switch(m_VisData)</c>.
/// </summary>
public class FindByFullVisByteTests
{
    public static readonly TheoryData<int, SstvModeDefinition> RealLegacyBytes = new()
    {
        { 0x82, SstvModeRegistry.Rm8 },
        { 0x86, SstvModeRegistry.Rm12 },
        { 0x84, SstvModeRegistry.R24 },
        { 0x88, SstvModeRegistry.Robot36 },
        { 0x0c, SstvModeRegistry.Robot72 },
        { 0x44, SstvModeRegistry.Avt },
        { 0x3c, SstvModeRegistry.ScottieS1 },
        { 0xb8, SstvModeRegistry.ScottieS2 },
        { 0xcc, SstvModeRegistry.ScottieDx },
        { 0xac, SstvModeRegistry.MartinM1 },
        { 0x28, SstvModeRegistry.MartinM2 },
        { 0xb7, SstvModeRegistry.Sc2180 },
        { 0x3f, SstvModeRegistry.Sc2120 },
        { 0xbb, SstvModeRegistry.Sc260 },
        { 0xdd, SstvModeRegistry.Pd50 },
        { 0x63, SstvModeRegistry.Pd90 },
        { 0x5f, SstvModeRegistry.Pd120 },
        { 0xe2, SstvModeRegistry.Pd160 },
        { 0x60, SstvModeRegistry.Pd180 },
        { 0xe1, SstvModeRegistry.Pd240 },
        { 0xde, SstvModeRegistry.Pd290 },
        { 0x71, SstvModeRegistry.P3 },
        { 0x72, SstvModeRegistry.P5 },
        { 0xf3, SstvModeRegistry.P7 },
    };

    [Theory]
    [MemberData(nameof(RealLegacyBytes))]
    public void RealLegacyByte_ResolvesToTheRightMode(int legacyByte, SstvModeDefinition expectedMode)
    {
        var mode = SstvModeRegistry.FindByFullVisByte(legacyByte);

        Assert.NotNull(mode);
        Assert.Equal(expectedMode.Id, mode!.Id);
    }

    [Theory]
    [InlineData(0x08)] // R36's real byte is 0x88 (parity=1); 0x08 is the same 7 data bits with a flipped parity bit.
    [InlineData(0x83)] // R24's real byte is 0x84; 0x83 shares R24's parity bit but wrong data bits.
    public void FlippedParityOrWrongByte_ResolvesToNull(int wrongByte)
    {
        Assert.Null(SstvModeRegistry.FindByFullVisByte(wrongByte));
    }
}
