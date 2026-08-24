using System.Runtime.InteropServices;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio.Hamlib;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>Covers the Options-dialog-facing Hamlib path probe + rig-list feature (auditor
/// plan-review, 2026-08-23) -- see <see cref="HamlibDiscoveryService"/>'s own doc comment for why
/// <c>rig_load_all_backends</c>/<c>rig_list_foreach_model</c> calls are serialized behind a
/// process-wide gate.</summary>
public class HamlibDiscoveryServiceTests
{
    private const string LinuxSoname = "libhamlib.so.4";

    private sealed class StaticHamlibNativeFactory(IHamlibNative native) : IHamlibNativeFactory
    {
        public IHamlibNative Create(INativeLibraryLoader loader, nint handle) => native;
    }

    [Fact]
    public async Task ProbeAsync_LibraryUnavailable_ReturnsNotAvailable_NoRigModels()
    {
        var loader = new FakeNativeLibraryLoader(); // nothing loadable
        var sut = new HamlibDiscoveryService(loader, new StaticHamlibNativeFactory(new FakeHamlibNative()));

        var result = await sut.ProbeAsync(overridePath: null);

        Assert.False(result.IsAvailable);
        Assert.Empty(result.RigModels);
        Assert.NotEmpty(result.Attempts);
    }

    [Fact]
    public async Task ProbeAsync_LibraryAvailable_ListsRigModels_SortedByManufacturerThenModel()
    {
        var loader = new FakeNativeLibraryLoader();
        loader.Succeed(LinuxSoname, 1);
        var native = new FakeHamlibNative { Version = "Hamlib 4.5.5 2024-01-01T00:00:00Z 64-bit" };
        native.ModelIds.AddRange([2, 1]);
        native.CapsMfgNames[1] = "Yaesu";
        native.CapsModelNames[1] = "FT-991";
        native.CapsMfgNames[2] = "Icom";
        native.CapsModelNames[2] = "IC-7300";
        var sut = new HamlibDiscoveryService(loader, new StaticHamlibNativeFactory(native));

        var result = await sut.ProbeAsync(overridePath: null);

        Assert.True(result.IsAvailable);
        Assert.Equal(["Icom", "Yaesu"], result.RigModels.Select(m => m.Manufacturer));
        Assert.Contains("rig_load_all_backends", native.CallLog);
        Assert.Contains("rig_list_foreach_model", native.CallLog);
    }

    [Fact]
    public async Task ProbeAsync_ModelMissingCapsName_IsSkippedNotThrown()
    {
        var loader = new FakeNativeLibraryLoader();
        loader.Succeed(LinuxSoname, 1);
        var native = new FakeHamlibNative { Version = "Hamlib 4.5.5 2024-01-01T00:00:00Z 64-bit" };
        native.ModelIds.Add(1); // no CapsMfgNames/CapsModelNames entry for model 1 -> both null
        var sut = new HamlibDiscoveryService(loader, new StaticHamlibNativeFactory(native));

        var result = await sut.ProbeAsync(overridePath: null);

        Assert.True(result.IsAvailable);
        Assert.Empty(result.RigModels);
    }

    [Fact]
    public async Task ProbeAsync_OverridePath_ReportedAsResolvedPath()
    {
        var loader = new FakeNativeLibraryLoader();
        loader.Succeed("/custom/libhamlib.so.4", 1);
        var native = new FakeHamlibNative { Version = "Hamlib 4.5.5 2024-01-01T00:00:00Z 64-bit" };
        var sut = new HamlibDiscoveryService(loader, new StaticHamlibNativeFactory(native));

        var result = await sut.ProbeAsync("/custom/libhamlib.so.4");

        Assert.True(result.IsAvailable);
        Assert.Equal("/custom/libhamlib.so.4", result.ResolvedPath);
    }

    [Fact]
    public async Task ProbeAsync_UnavailableStillReportsResolvedPath_WhenLoadSucceededButNativeShimDidNot()
    {
        // Auditor plan-review finding: a loadable-but-non-Hamlib file loads fine (Locate() succeeds)
        // but HamlibNative's real constructor would throw resolving an export -- must still surface
        // WHICH path was found, distinguishing "wrong file" from "no file" for the status message.
        var loader = new FakeNativeLibraryLoader();
        loader.Succeed("/custom/not-hamlib.so", 1);
        var factory = new ThrowingHamlibNativeFactory(new HamlibUnavailableException(["rig_get_level: export not found in loaded library"]));
        var sut = new HamlibDiscoveryService(loader, factory);

        var result = await sut.ProbeAsync("/custom/not-hamlib.so");

        Assert.False(result.IsAvailable);
        Assert.Equal("/custom/not-hamlib.so", result.ResolvedPath);
    }

    private sealed class ThrowingHamlibNativeFactory(HamlibUnavailableException exception) : IHamlibNativeFactory
    {
        public IHamlibNative Create(INativeLibraryLoader loader, nint handle) => throw exception;
    }

    [Fact]
    public async Task ProbeAsync_ConcurrentCalls_NeverOverlapInsideTheGatedRigListCall()
    {
        // Confirms the auditor-flagged race mitigation: rig_load_all_backends/rig_list_foreach_model
        // touch a process-global, unlocked Hamlib registry (see HamlibDiscoveryService's own doc
        // comment) -- two Options-dialog probes running back-to-back (or a double-click) must never
        // enter that section at the same time. RigVersion is deliberately NOT tracked here -- it runs
        // outside the gate (HamlibRuntime's constructor calls it before ListRigModels), and in real
        // usage each concurrent probe gets its own independently-resolved native shim, so only the
        // gated section is the thing under test.
        var tracker = new OverlapTracker();
        var loader = new FakeNativeLibraryLoader();
        loader.Succeed(LinuxSoname, 1);
        var factory = new PerCallHamlibNativeFactory(tracker);
        var sut = new HamlibDiscoveryService(loader, factory);

        var probe1 = sut.ProbeAsync(overridePath: null);
        var probe2 = sut.ProbeAsync(overridePath: null);
        var results = await Task.WhenAll(probe1, probe2);

        Assert.All(results, r => Assert.True(r.IsAvailable));
        Assert.False(tracker.Overlapped);
    }

    private sealed class OverlapTracker
    {
        private int _depth;

        public bool Overlapped { get; private set; }

        public void Enter()
        {
            if (Interlocked.Increment(ref _depth) > 1)
            {
                Overlapped = true;
            }

            Thread.Sleep(30);
            Interlocked.Decrement(ref _depth);
        }
    }

    private sealed class PerCallHamlibNativeFactory(OverlapTracker tracker) : IHamlibNativeFactory
    {
        public IHamlibNative Create(INativeLibraryLoader loader, nint handle) => new TrackingHamlibNative(tracker);
    }

    /// <summary>Minimal <see cref="IHamlibNative"/> stub for the concurrency test above -- every
    /// member this discovery-service code path doesn't call throws, so an accidental future call
    /// into an untracked member fails loudly instead of silently returning a meaningless default.</summary>
    private sealed class TrackingHamlibNative(OverlapTracker tracker) : IHamlibNative
    {
        public nint RigInit(uint model) => throw new NotSupportedException();
        public int RigOpen(nint rig) => throw new NotSupportedException();
        public int RigClose(nint rig) => throw new NotSupportedException();
        public int RigCleanup(nint rig) => throw new NotSupportedException();
        public CLong RigTokenLookup(nint rig, string name) => throw new NotSupportedException();
        public int RigSetConf(nint rig, CLong token, string value) => throw new NotSupportedException();
        public int RigSetFreq(nint rig, uint vfo, double freq) => throw new NotSupportedException();
        public int RigGetFreq(nint rig, uint vfo, out double freq) => throw new NotSupportedException();
        public int RigSetMode(nint rig, uint vfo, ulong mode, CLong width) => throw new NotSupportedException();
        public int RigGetMode(nint rig, uint vfo, out ulong mode, out CLong width) => throw new NotSupportedException();
        public int RigSetPtt(nint rig, uint vfo, int ptt) => throw new NotSupportedException();
        public int RigGetPtt(nint rig, uint vfo, out int ptt) => throw new NotSupportedException();
        public string? RigVersion() => "Hamlib 4.5.5 2024-01-01T00:00:00Z 64-bit";
        public void RigSetDebug(int level) => throw new NotSupportedException();
        public int RigGetLevel(nint rig, uint vfo, ulong level, out float value) => throw new NotSupportedException();
        public int RigGetLevelInt(nint rig, uint vfo, ulong level, out int value) => throw new NotSupportedException();

        public int RigLoadAllBackends()
        {
            tracker.Enter();
            return 0;
        }

        public IReadOnlyList<uint> RigListModelIds()
        {
            tracker.Enter();
            return [1u];
        }

        public string? RigGetCapsMfgName(uint model) => "Yaesu";
        public string? RigGetCapsModelName(uint model) => "FT-991";
    }
}
