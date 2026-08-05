using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio;

namespace ScanlineStudio.Core.Radio.Tests;

public sealed class RadioConnectionSettingsTests
{
    [Fact]
    public void ToConnectionSpec_DefaultSettings_ReturnsNoneConnectionSpec()
    {
        var settings = new RadioConnectionSettings();

        var spec = settings.ToConnectionSpec();

        Assert.IsType<NoneConnectionSpec>(spec);
    }

    [Fact]
    public void ToConnectionSpec_RigctldWithHostAndPort_ReturnsRigctldConnectionSpec()
    {
        var settings = new RadioConnectionSettings { BackendId = "rigctld", Host = "localhost", Port = 4532 };

        var spec = settings.ToConnectionSpec();

        var rigctldSpec = Assert.IsType<RigctldConnectionSpec>(spec);
        Assert.Equal("localhost", rigctldSpec.Host);
        Assert.Equal(4532, rigctldSpec.Port);
    }

    [Fact]
    public void ToConnectionSpec_RigctldMissingHost_FallsBackToNone()
    {
        var settings = new RadioConnectionSettings { BackendId = "rigctld", Port = 4532 };

        var spec = settings.ToConnectionSpec();

        Assert.IsType<NoneConnectionSpec>(spec);
    }

    [Fact]
    public void ToConnectionSpec_UnknownBackendId_FallsBackToNone()
    {
        var settings = new RadioConnectionSettings { BackendId = "hamlib" };

        var spec = settings.ToConnectionSpec();

        Assert.IsType<NoneConnectionSpec>(spec);
    }
}
