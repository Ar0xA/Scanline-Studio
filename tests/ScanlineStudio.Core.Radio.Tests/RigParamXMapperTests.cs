using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio.OmniRig;

namespace ScanlineStudio.Core.Radio.Tests;

public sealed class RigParamXMapperTests
{
    [Theory]
    [InlineData(RigParamX.PM_SSB_U, RadioMode.Usb)]
    [InlineData(RigParamX.PM_SSB_L, RadioMode.Lsb)]
    [InlineData(RigParamX.PM_FM, RadioMode.Fm)]
    [InlineData(RigParamX.PM_CW_U, RadioMode.Cw)]
    [InlineData(RigParamX.PM_CW_L, RadioMode.CwR)]
    [InlineData(RigParamX.PM_AM, RadioMode.Am)]
    [InlineData(RigParamX.PM_DIG_U, RadioMode.Data)]
    [InlineData(RigParamX.PM_DIG_L, RadioMode.DataR)]
    public void ToRadioMode_KnownFlag_MapsCorrectly(RigParamX flag, RadioMode expected) =>
        Assert.Equal(expected, RigParamXMapper.ToRadioMode(flag));

    [Theory]
    [InlineData(RigParamX.PM_UNKNOWN)]
    [InlineData(RigParamX.PM_TX)]
    [InlineData(RigParamX.PM_RX)]
    [InlineData(RigParamX.None)]
    public void ToRadioMode_UnrecognizedFlag_ResolvesToUnknown_NeverThrows(RigParamX flag) =>
        Assert.Equal(RadioMode.Unknown, RigParamXMapper.ToRadioMode(flag));

    [Theory]
    [InlineData(RadioMode.Usb, RigParamX.PM_SSB_U)]
    [InlineData(RadioMode.Lsb, RigParamX.PM_SSB_L)]
    [InlineData(RadioMode.Fm, RigParamX.PM_FM)]
    [InlineData(RadioMode.Cw, RigParamX.PM_CW_U)]
    [InlineData(RadioMode.CwR, RigParamX.PM_CW_L)]
    [InlineData(RadioMode.Am, RigParamX.PM_AM)]
    [InlineData(RadioMode.Rtty, RigParamX.PM_DIG_U)]
    [InlineData(RadioMode.Data, RigParamX.PM_DIG_U)]
    [InlineData(RadioMode.Pkt, RigParamX.PM_DIG_U)]
    [InlineData(RadioMode.RttyR, RigParamX.PM_DIG_L)]
    [InlineData(RadioMode.DataR, RigParamX.PM_DIG_L)]
    public void ToRigParamX_KnownMode_MapsCorrectly(RadioMode mode, RigParamX expected) =>
        Assert.Equal(expected, RigParamXMapper.ToRigParamX(mode));

    [Fact]
    public void ToRigParamX_Unknown_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RigParamXMapper.ToRigParamX(RadioMode.Unknown));
}
