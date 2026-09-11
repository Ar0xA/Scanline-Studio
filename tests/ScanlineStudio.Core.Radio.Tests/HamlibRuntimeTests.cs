using ScanlineStudio.Core.Radio.Hamlib;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>Confirms <see cref="HamlibRuntime"/>'s caching behavior -- discovery + the version gate
/// run exactly once, at construction, never re-triggered by repeated <see cref="IHamlibRuntime.Native"/>
/// access (spec/03-cat-layer.md's "the whole probe runs once at startup ... not re-run per connect
/// attempt"; see also the implementation plan's round-2 resolution on why this had to be eager, not
/// lazy).</summary>
public class HamlibRuntimeTests
{
    // The locator's OWN first candidate on this platform, not a hardcoded soname. Pinning
    // "libhamlib.so.4" made every one of these tests fail on Windows, where the locator yields
    // hamlib-4.dll -- so nothing the fake registered was ever attempted.
    private static string PrimarySoname => HamlibLibraryLocator.PrimaryCandidateForCurrentPlatform;

    [Fact]
    public void Constructor_SupportedVersion_RunsDiscoveryAndVersionGateExactlyOnce()
    {
        var loader = new FakeNativeLibraryLoader();
        loader.Succeed(PrimarySoname, 1);
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
        loader.Succeed(PrimarySoname, 1);
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

    [Fact]
    public void Constructor_MissingNativeExport_IsAvailableFalse_AttemptsPreserved()
    {
        // Closes a coverage gap flagged by Tier A Batch 9 chunk 9d (docs/functional-audit-playbook.md):
        // the real HamlibNative constructor throws HamlibUnavailableException if any one of its 14
        // P/Invoke exports is missing from the loaded library (a partially-resolved instance is never
        // observable -- HamlibNative.cs's own Resolve<TDelegate>). Nothing previously scripted the
        // factory to reproduce that path -- the library loads fine, but the native shim it produces
        // is unusable.
        var loader = new FakeNativeLibraryLoader();
        loader.Succeed(PrimarySoname, 1);
        var factory = new CountingHamlibNativeFactory(
            new FakeHamlibNative(),
            throwOnCreate: new HamlibUnavailableException(["rig_get_level: export not found in loaded library"]));

        var sut = new HamlibRuntime(loader, overridePath: null, factory);

        Assert.False(sut.IsAvailable);
        Assert.Equal(1, factory.CreateCallCount);
        var ex = Assert.Throws<HamlibUnavailableException>(() => sut.Native);
        Assert.Contains(ex.Attempts, a => a.Contains("rig_get_level", StringComparison.Ordinal));
    }

    private sealed class CountingHamlibNativeFactory(IHamlibNative native, HamlibUnavailableException? throwOnCreate = null) : IHamlibNativeFactory
    {
        public int CreateCallCount { get; private set; }

        public IHamlibNative Create(INativeLibraryLoader loader, nint handle)
        {
            CreateCallCount++;
            if (throwOnCreate is not null)
            {
                throw throwOnCreate;
            }

            return native;
        }
    }
}
