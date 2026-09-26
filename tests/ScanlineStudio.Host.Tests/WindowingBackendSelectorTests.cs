namespace ScanlineStudio.Host.Tests;

public class WindowingBackendSelectorTests
{
    private static Func<string, string?> Env(string? optIn, string? waylandDisplay) => name => name switch
    {
        WindowingBackendSelector.OptInVariable => optIn,
        WindowingBackendSelector.WaylandDisplayVariable => waylandDisplay,
        _ => null,
    };

    [Theory]
    [InlineData(true, "1", "wayland-0", WindowingBackendChoice.Wayland)]
    [InlineData(true, "1", null, WindowingBackendChoice.WaylandRequestedWithoutSession)]
    [InlineData(true, "1", "", WindowingBackendChoice.WaylandRequestedWithoutSession)]
    [InlineData(true, null, "wayland-0", WindowingBackendChoice.PlatformDetect)]
    [InlineData(true, "0", "wayland-0", WindowingBackendChoice.PlatformDetect)]
    [InlineData(true, "true", "wayland-0", WindowingBackendChoice.PlatformDetect)]
    [InlineData(true, null, null, WindowingBackendChoice.PlatformDetect)]
    [InlineData(false, "1", "wayland-0", WindowingBackendChoice.PlatformDetect)]
    public void Choose_OnlyOptsIntoWaylandWithBothVariablesOnLinux(bool isLinux, string? optIn, string? waylandDisplay, WindowingBackendChoice expected)
        => Assert.Equal(expected, WindowingBackendSelector.Choose(isLinux, Env(optIn, waylandDisplay)));
}
