using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio.Hamlib;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>
/// Restart-required-settings backlog item 5 (2026-08-28): <see cref="HamlibProtocolFactory.ReloadLibraryAsync"/>
/// applies a new Hamlib library path LIVE, without an app restart. Two rounds of plan-review verified
/// the load-bearing safety claim this whole feature rests on: an already-constructed
/// <see cref="HamlibRadioProtocol"/> captures its own <c>IHamlibNative</c> at construction time, with
/// zero live reference back to <see cref="HamlibProtocolFactory"/>, so swapping which runtime a
/// FUTURE <see cref="HamlibProtocolFactory.Create"/> call uses can never disturb an already-open
/// connection.
/// </summary>
public sealed class HamlibProtocolFactoryTests
{
    private const string LinuxSoname = "libhamlib.so.4";
    private const string AlternatePath = "/opt/homebrew/lib/libhamlib.4.dylib";

    private static HamlibProtocolFactory CreateFactory(FakeNativeLibraryLoader loader, IHamlibNative initialNative)
    {
        var nativeFactory = new ScriptedHamlibNativeFactory(initialNative);
        var runtime = new HamlibRuntime(loader, overridePath: null, nativeFactory);
        return new HamlibProtocolFactory(runtime, loader, nativeFactory);
    }

    [Fact]
    public async Task ReloadLibraryAsync_CandidateAvailable_SwapsRuntime()
    {
        var loader = new FakeNativeLibraryLoader();
        loader.Succeed(LinuxSoname, 1);
        var initialNative = new FakeHamlibNative { Version = "Hamlib 4.5.5 date arch" };
        var nativeFactory = new ScriptedHamlibNativeFactory(initialNative);
        var runtime = new HamlibRuntime(loader, overridePath: null, nativeFactory);
        var factory = new HamlibProtocolFactory(runtime, loader, nativeFactory);

        loader.Succeed(AlternatePath, 2);
        var newNative = new FakeHamlibNative { Version = "Hamlib 4.6.0 date arch" };
        nativeFactory.NextNative = newNative;

        var result = await factory.ReloadLibraryAsync(AlternatePath);

        Assert.True(result.Applied);
        Assert.Equal(AlternatePath, result.ResolvedPath);
        Assert.Equal("Hamlib 4.6.0 date arch", result.Version);

        // Proves the swap actually took: a NEW Create() call now reaches into newNative, not
        // initialNative.
        var protocol = ((IRadioProtocolFactory)factory).Create(new HamlibConnectionSpec(Model: 1) { SerialPort = "/dev/ttyS0", BaudRate = 9600 });
        await protocol.PollAsync(CancellationToken.None);

        Assert.Contains("rig_init", newNative.CallLog);
        Assert.DoesNotContain("rig_init", initialNative.CallLog);
    }

    [Fact]
    public async Task ReloadLibraryAsync_CandidateUnavailable_DoesNotSwap_OldRuntimeStillUsed()
    {
        var loader = new FakeNativeLibraryLoader();
        loader.Succeed(LinuxSoname, 1);
        var initialNative = new FakeHamlibNative { Version = "Hamlib 4.5.5 date arch" };
        var factory = CreateFactory(loader, initialNative);

        // AlternatePath is never registered as loadable -- the candidate fails discovery entirely.
        var result = await factory.ReloadLibraryAsync(AlternatePath);

        Assert.False(result.Applied);
        Assert.NotEmpty(result.Attempts);

        var protocol = ((IRadioProtocolFactory)factory).Create(new HamlibConnectionSpec(Model: 1) { SerialPort = "/dev/ttyS0", BaudRate = 9600 });
        await protocol.PollAsync(CancellationToken.None);

        // Still the ORIGINAL native -- the rejected candidate never got installed.
        Assert.Contains("rig_init", initialNative.CallLog);
    }

    [Fact]
    public async Task ReloadLibraryAsync_AfterSuccess_AlreadyConstructedProtocol_KeepsUsingItsOwnCapturedNative()
    {
        // The load-bearing safety claim this whole feature rests on (verified by 2 plan-review
        // rounds): an already-constructed HamlibRadioProtocol is provably unaffected by a LATER
        // runtime swap.
        var loader = new FakeNativeLibraryLoader();
        loader.Succeed(LinuxSoname, 1);
        var initialNative = new FakeHamlibNative { Version = "Hamlib 4.5.5 date arch" };
        var nativeFactory = new ScriptedHamlibNativeFactory(initialNative);
        var runtime = new HamlibRuntime(loader, overridePath: null, nativeFactory);
        var factory = new HamlibProtocolFactory(runtime, loader, nativeFactory);

        var earlyProtocol = ((IRadioProtocolFactory)factory).Create(new HamlibConnectionSpec(Model: 1) { SerialPort = "/dev/ttyS0", BaudRate = 9600 });

        loader.Succeed(AlternatePath, 2);
        var newNative = new FakeHamlibNative { Version = "Hamlib 4.6.0 date arch" };
        nativeFactory.NextNative = newNative;
        var result = await factory.ReloadLibraryAsync(AlternatePath);
        Assert.True(result.Applied);

        // The protocol built BEFORE the reload must still work, against its OWN originally-captured
        // native -- never the new one.
        await earlyProtocol.PollAsync(CancellationToken.None);

        Assert.Contains("rig_init", initialNative.CallLog);
        Assert.DoesNotContain("rig_init", newNative.CallLog);
    }

    [Fact]
    public async Task ReloadLibraryAsync_InitialRuntimeUnavailable_BecomesAvailableAfterSuccessfulReload()
    {
        var loader = new FakeNativeLibraryLoader(); // nothing loadable -- initial construction fails discovery
        var nativeFactory = new ScriptedHamlibNativeFactory(new FakeHamlibNative());
        var runtime = new HamlibRuntime(loader, overridePath: null, nativeFactory);
        Assert.False(runtime.IsAvailable); // sanity: genuinely starts unavailable

        var factory = new HamlibProtocolFactory(runtime, loader, nativeFactory);

        loader.Succeed(AlternatePath, 1);
        var newNative = new FakeHamlibNative { Version = "Hamlib 4.5.5 date arch" };
        nativeFactory.NextNative = newNative;

        var result = await factory.ReloadLibraryAsync(AlternatePath);

        Assert.True(result.Applied);
        var protocol = ((IRadioProtocolFactory)factory).Create(new HamlibConnectionSpec(Model: 1) { SerialPort = "/dev/ttyS0", BaudRate = 9600 });
        await protocol.PollAsync(CancellationToken.None); // does not throw HamlibUnavailableException
        Assert.Contains("rig_init", newNative.CallLog);
    }

    [Fact]
    public async Task ReloadLibraryAsync_TwoConcurrentCalls_GateSerializesThem_LeavesOneDeterministicWinner()
    {
        // Code-review round-1 finding: the original version of this test asserted "exactly one of
        // the two wins," which is true regardless of whether _reloadGate does anything at all (_runtime
        // is a single reference; Create() reads it once). This version proves SERIALIZATION itself --
        // it fails if _reloadGate is deleted -- by tracking concurrent entries into
        // HamlibRuntime's own construction path (the thing the gate actually protects), with a small
        // delay to give an unserialized pair a real window to overlap in.
        //
        // Code-review round-2 finding: on a constrained (e.g. 1-vCPU CI) runner with a small
        // ThreadPool.minWorkerThreads, the pool can take longer than CallDelay to INJECT a second
        // worker thread, so the two Task.Run work items serialize themselves through pool starvation
        // alone -- independent of whether _reloadGate does anything. That would make a broken/deleted
        // gate pass this test spuriously on such a runner. Explicitly raising minWorkerThreads removes
        // injection latency as a confound, so MaxObservedConcurrency==1 is provably about the gate, not
        // about how fast the pool happens to spin up a second thread on this machine.
        ThreadPool.GetMinThreads(out var minWorker, out var minIoCompletion);
        // Round-3 finding: self-verifying -- SetMinThreads can return false (target above
        // maxWorkerThreads), which would silently reintroduce the round-2 false-negative risk.
        Assert.True(ThreadPool.SetMinThreads(Math.Max(minWorker, 4), minIoCompletion));

        var loader = new FakeNativeLibraryLoader();
        loader.Succeed(LinuxSoname, 1);
        var initialNative = new FakeHamlibNative { Version = "Hamlib 4.5.5 date arch" };
        var nativeFactory = new ConcurrencyTrackingHamlibNativeFactory(initialNative) { CallDelay = TimeSpan.FromMilliseconds(50) };
        var runtime = new HamlibRuntime(loader, overridePath: null, nativeFactory);
        var factory = new HamlibProtocolFactory(runtime, loader, nativeFactory);

        const string pathA = "/path/a.so";
        const string pathB = "/path/b.so";
        loader.Succeed(pathA, 2);
        loader.Succeed(pathB, 3);
        var nativeA = new FakeHamlibNative { Version = "Hamlib 4.5.5 date arch" };
        var nativeB = new FakeHamlibNative { Version = "Hamlib 4.6.0 date arch" };
        nativeFactory.ScriptedByCallOrder.Enqueue(nativeA);
        nativeFactory.ScriptedByCallOrder.Enqueue(nativeB);

        var taskA = factory.ReloadLibraryAsync(pathA);
        var taskB = factory.ReloadLibraryAsync(pathB);
        var results = await Task.WhenAll(taskA, taskB);

        Assert.All(results, r => Assert.True(r.Applied));
        Assert.Equal(1, nativeFactory.MaxObservedConcurrency); // the load-bearing assertion -- fails under a deleted/broken gate
        Assert.Equal(3, nativeFactory.TotalCalls); // sanity: the initial runtime construction (1) + both reloads (2) genuinely ran

        // Code-review round-2 finding: "LeavesOneDeterministicWinner" was only ever proven as
        // "exactly one, don't-care-which" (aWon ^ bWon). The winner IS actually deterministic here,
        // not just uniquely one of the two: taskA's own WaitAsync(...) call runs synchronously (line
        // above), on the calling thread, BEFORE taskB is even constructed -- SemaphoreSlim(1, 1) has
        // exactly one permit, so A acquires it first, unconditionally. B is left as the ONLY queued
        // waiter. Round-3 correction: this does NOT rely on SemaphoreSlim's async-waiter ordering
        // being FIFO -- that's an implementation detail, not a documented contract. It doesn't need
        // to be: with exactly one waiter queued, ANY release-ordering policy hands the permit to B
        // next, so B always finishes second and always installs last -- B, not A, is the one a later
        // Create() call reaches. Assert the actual winner, not just its uniqueness.
        var protocol = ((IRadioProtocolFactory)factory).Create(new HamlibConnectionSpec(Model: 1) { SerialPort = "/dev/ttyS0", BaudRate = 9600 });
        await protocol.PollAsync(CancellationToken.None);

        var aWon = nativeA.CallLog.Contains("rig_init");
        var bWon = nativeB.CallLog.Contains("rig_init");
        Assert.False(aWon, "A acquired the gate first (synchronously, before B was even created) so A must finish and get overwritten first");
        Assert.True(bWon, "B is the only queued waiter, so the gate's FIFO release hands it the permit next -- B always installs last");
    }

    /// <summary>Scriptable <see cref="IHamlibNativeFactory"/> -- <see cref="NextNative"/> is used
    /// when <see cref="ScriptedByCallOrder"/> is empty (the common single-reload case); when calls
    /// are queued via <see cref="ScriptedByCallOrder"/>, each <see cref="Create"/> call dequeues the
    /// next one, letting a test script two overlapping reloads deterministically.</summary>
    private sealed class ScriptedHamlibNativeFactory(IHamlibNative initialNative) : IHamlibNativeFactory
    {
        public IHamlibNative NextNative { get; set; } = initialNative;

        public Queue<IHamlibNative> ScriptedByCallOrder { get; } = new();

        public IHamlibNative Create(INativeLibraryLoader loader, nint handle) =>
            ScriptedByCallOrder.Count > 0 ? ScriptedByCallOrder.Dequeue() : NextNative;
    }

    /// <summary>Same scripting shape as <see cref="ScriptedHamlibNativeFactory"/>, plus concurrency
    /// tracking -- same idiom <see cref="FakeHamlibNative"/>'s own <c>CallDelay</c>/<c>Reentered</c>
    /// already use to prove <c>HamlibRadioProtocol</c>'s own semaphore. Tracks the MAXIMUM number of
    /// overlapping <see cref="Create"/> calls ever observed, not just whether one happened -- the
    /// only assertion that actually falls over if a caller's own serialization (here,
    /// <see cref="HamlibProtocolFactory"/>'s <c>_reloadGate</c>) is removed or broken.</summary>
    private sealed class ConcurrencyTrackingHamlibNativeFactory(IHamlibNative initialNative) : IHamlibNativeFactory
    {
        private int _current;
        private int _maxObserved;
        private int _totalCalls;

        public IHamlibNative NextNative { get; set; } = initialNative;

        public Queue<IHamlibNative> ScriptedByCallOrder { get; } = new();

        public TimeSpan CallDelay { get; set; } = TimeSpan.Zero;

        public int MaxObservedConcurrency => _maxObserved;

        public int TotalCalls => _totalCalls;

        public IHamlibNative Create(INativeLibraryLoader loader, nint handle)
        {
            var depth = Interlocked.Increment(ref _current);
            InterlockedMax(ref _maxObserved, depth);
            Interlocked.Increment(ref _totalCalls);
            try
            {
                if (CallDelay > TimeSpan.Zero)
                {
                    Thread.Sleep(CallDelay);
                }

                lock (ScriptedByCallOrder)
                {
                    return ScriptedByCallOrder.Count > 0 ? ScriptedByCallOrder.Dequeue() : NextNative;
                }
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }

        private static void InterlockedMax(ref int target, int candidate)
        {
            int initial, computed;
            do
            {
                initial = target;
                computed = Math.Max(initial, candidate);
            }
            while (Interlocked.CompareExchange(ref target, computed, initial) != initial);
        }
    }
}
