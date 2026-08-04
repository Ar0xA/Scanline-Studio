using Yoniq.Core.Radio.Hamlib;

namespace Yoniq.Core.Radio.Tests;

/// <summary>Confirms <see cref="HamlibRuntime"/>'s caching behavior -- discovery + the version gate
/// run exactly once, at construction, never re-triggered by repeated <see cref="IHamlibRuntime.Native"/>
/// access (spec/03-cat-layer.md's "the whole probe runs once at startup ... not re-run per connect
/// attempt"; see also the implementation plan's round-2 resolution on why this had to be eager, not
/// lazy).</summary>
public class HamlibRuntimeTests
{
    private const string LinuxSoname = "libhamlib.so.4";

    [Fact]
    public void Constructor_SupportedVersion_RunsDiscoveryAndVersionGateExactlyOnce()
    {
        var loader = new FakeNativeLibraryLoader();
        loader.Succeed(LinuxSoname, 1);
        var native = new FakeHamlibNative { Version = "Hamlib 4.5.5 date arch" };
        var factory = new CountingHamlibNativeFactory(native);

        var sut = new HamlibRuntime(loader, overridePath: null, factory);

        Assert.True(sut.IsAvailable);
        Assert.Equal(1, factory.CreateCallCount);

        _ = sut.Native;
        _ = sut.Native;

        Assert.Equal(1, factory.CreateCallCount);
        Assert.Equal(1, native.CallLog.Count(c => c == "rig_version"));
    }

    [Fact]
    public void Constructor_UnsupportedVersion_IsAvailableFalse_NativeThrows()
    {
        var loader = new FakeNativeLibraryLoader();
        loader.Succeed(LinuxSoname, 1);
        var native = new FakeHamlibNative { Version = "Hamlib 5.0.0 date arch" };
        var factory = new CountingHamlibNativeFactory(native);

        var sut = new HamlibRuntime(loader, overridePath: null, factory);

        Assert.False(sut.IsAvailable);
        var ex = Assert.Throws<HamlibUnavailableException>(() => sut.Native);
        Assert.Contains(ex.Attempts, a => a.Contains("5.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public void Constructor_LocatorFails_IsAvailableFalse_NeverConstructsNative()
    {
        var loader = new FakeNativeLibraryLoader(); // nothing loadable
        var factory = new CountingHamlibNativeFactory(new FakeHamlibNative());

        var sut = new HamlibRuntime(loader, overridePath: null, factory);

        Assert.False(sut.IsAvailable);
        Assert.Equal(0, factory.CreateCallCount);
        Assert.Throws<HamlibUnavailableException>(() => sut.Native);
    }

    private sealed class CountingHamlibNativeFactory(IHamlibNative native) : IHamlibNativeFactory
    {
        public int CreateCallCount { get; private set; }

        public IHamlibNative Create(INativeLibraryLoader loader, nint handle)
        {
            CreateCallCount++;
            return native;
        }
    }
}
