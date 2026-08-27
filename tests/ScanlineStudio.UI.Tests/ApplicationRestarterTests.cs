using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.UI.Services;

namespace ScanlineStudio.UI.Tests;

public sealed class ApplicationRestarterTests
{
    [Fact]
    public void BuildRestartStartInfo_WhenProcessPathIsNull_ReturnsNull()
    {
        var startInfo = ApplicationRestarter.BuildRestartStartInfo(null, ["ScanlineStudio.Host", "--log-level", "Debug"]);

        Assert.Null(startInfo);
    }

    [Fact]
    public void BuildRestartStartInfo_WhenProcessPathIsEmpty_ReturnsNull()
    {
        var startInfo = ApplicationRestarter.BuildRestartStartInfo(string.Empty, ["ScanlineStudio.Host"]);

        Assert.Null(startInfo);
    }

    [Fact]
    public void BuildRestartStartInfo_UnderTheApphost_UsesProcessPathAndDropsTheDuplicateFirstArg()
    {
        // Apphost case: commandLineArgs[0] duplicates processPath -- must not appear twice in the
        // rebuilt argument list.
        var startInfo = ApplicationRestarter.BuildRestartStartInfo(
            "/opt/scanline/ScanlineStudio.Host",
            ["/opt/scanline/ScanlineStudio.Host", "--log-level", "Debug"]);

        Assert.NotNull(startInfo);
        Assert.Equal("/opt/scanline/ScanlineStudio.Host", startInfo!.FileName);
        Assert.Equal(["--log-level", "Debug"], startInfo.ArgumentList);
    }

    [Theory]
    [InlineData("/usr/lib/dotnet/dotnet")]
    [InlineData("dotnet.exe")]
    public void BuildRestartStartInfo_UnderTheDotnetMuxer_RePrependsTheManagedDllPath(string dotnetProcessPath)
    {
        // Muxer case: processPath is `dotnet` itself, not this app -- commandLineArgs[0] is the
        // managed dll path and must be re-prepended, or the relaunch would drop it entirely.
        var startInfo = ApplicationRestarter.BuildRestartStartInfo(
            dotnetProcessPath,
            ["/opt/scanline/ScanlineStudio.Host.dll", "--log-level", "Debug"]);

        Assert.NotNull(startInfo);
        Assert.Equal(dotnetProcessPath, startInfo!.FileName);
        Assert.Equal(["/opt/scanline/ScanlineStudio.Host.dll", "--log-level", "Debug"], startInfo.ArgumentList);
    }

    [Fact]
    public void BuildRestartStartInfo_WithNoExtraArguments_ProducesAnEmptyArgumentList()
    {
        var startInfo = ApplicationRestarter.BuildRestartStartInfo(
            "/opt/scanline/ScanlineStudio.Host", ["/opt/scanline/ScanlineStudio.Host"]);

        Assert.NotNull(startInfo);
        Assert.Empty(startInfo!.ArgumentList);
    }

    [Fact]
    public void RestartRequested_DefaultsToFalse()
    {
        var restarter = new ApplicationRestarter(NullLogger<ApplicationRestarter>.Instance);

        Assert.False(restarter.RestartRequested);
    }

    [Fact]
    public void RestartRequested_IsAPlainSettableProperty()
    {
        var restarter = new ApplicationRestarter(NullLogger<ApplicationRestarter>.Instance);

        restarter.RestartRequested = true;

        Assert.True(restarter.RestartRequested);
    }

    [Fact]
    public void ApplicationRestarter_IsNotDisposable()
    {
        // Round-3 finding: the DI container would otherwise capture and tear this down as part of
        // Program.cs's own DisposeAsync, before StartNewInstance is ever called from lifetime.Exit.
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(ApplicationRestarter)));
        Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(typeof(ApplicationRestarter)));
    }
}
