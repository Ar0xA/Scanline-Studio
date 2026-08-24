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
        var settings = new RadioConnectionSettings { BackendId = "omnirig" };

        var spec = settings.ToConnectionSpec();

        Assert.IsType<NoneConnectionSpec>(spec);
    }

    [Fact]
    public void ToConnectionSpec_HamlibWithModel_ReturnsHamlibConnectionSpecWithAllFields()
    {
        var settings = new RadioConnectionSettings
        {
            BackendId = "hamlib",
            HamlibModel = 1035,
            SerialPort = "/dev/ttyUSB0",
            BaudRate = 4800,
            PttType = "RIG_PTT_RIG",
        };

        var spec = settings.ToConnectionSpec();

        var hamlibSpec = Assert.IsType<HamlibConnectionSpec>(spec);
        Assert.Equal(1035u, hamlibSpec.Model);
        Assert.Equal("/dev/ttyUSB0", hamlibSpec.SerialPort);
        Assert.Equal(4800, hamlibSpec.BaudRate);
        Assert.Equal("RIG_PTT_RIG", hamlibSpec.PttType);
    }

    [Fact]
    public void ToConnectionSpec_HamlibMissingModel_FallsBackToNone()
    {
        var settings = new RadioConnectionSettings { BackendId = "hamlib" };

        var spec = settings.ToConnectionSpec();

        Assert.IsType<NoneConnectionSpec>(spec);
    }

    [Fact]
    public void ToConnectionSpec_FlrigWithHostAndPort_ReturnsFlrigConnectionSpec()
    {
        var settings = new RadioConnectionSettings { BackendId = "flrig", FlrigHost = "localhost", FlrigPort = 12345 };

        var spec = settings.ToConnectionSpec();

        var flrigSpec = Assert.IsType<FlrigConnectionSpec>(spec);
        Assert.Equal("localhost", flrigSpec.Host);
        Assert.Equal(12345, flrigSpec.Port);
    }

    [Fact]
    public void ToConnectionSpec_FlrigMissingHost_FallsBackToNone()
    {
        var settings = new RadioConnectionSettings { BackendId = "flrig", FlrigHost = null, FlrigPort = 12345 };

        var spec = settings.ToConnectionSpec();

        Assert.IsType<NoneConnectionSpec>(spec);
    }
}
