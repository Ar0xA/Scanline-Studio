using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Drm;

namespace ScanlineStudio.Application.Tests;

/// <summary>The DRM receive worker's queue, gap policy, exception isolation and shutdown, on a gated fake receiver.</summary>
public sealed class DrmReceiveWorkerTests
{
    // 1000 Hz: the queue holds 2000 samples and a gap up to 1000 samples is zero-filled.
    private const int Rate = 1000;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class Sink
    {
        public List<DrmReceivedFile> Files { get; } = [];

        public List<DrmSignalState> States { get; } = [];

        public List<string> Already { get; } = [];

        public Action<DrmSignalState>? OnState { get; set; }
    }

    private static (DrmReceiveWorker Worker, FakeDrmReceiver Receiver, Sink Sink) Create(ILogger? logger = null)
    {
        var receiver = new FakeDrmReceiver(Rate);
        var sink = new Sink();
        var worker = new DrmReceiveWorker(receiver, 7, logger ?? NullLogger.Instance,
            f => { lock (sink) sink.Files.Add(f); },
            s => { sink.OnState?.Invoke(s); lock (sink) sink.States.Add(s); },
            n => { lock (sink) sink.Already.Add(n); });
        worker.Start();
        return (worker, receiver, sink);
    }

    private static void Until(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, Wait), "condition not reached");

    [Fact]
    public async Task Producer_NeverBlocks_WhileTheWorkerIsGated_AndDropsTheOldestBySampleCount()
    {
        var logger = new SstvSessionServicePttSafetyTests.RecordingLogger<DrmReceiveWorkerTests>();
        var (worker, receiver, _) = Create(logger);
        using var gate = new SemaphoreSlim(0);
        receiver.PushGate = gate;
        worker.Handler(new float[100]);
        Assert.True(receiver.PushEntered.Wait(Wait));

        // 30 more blocks (3000 samples) while the worker is stuck: every call returns, the queue keeps the newest 2000.
        for (int i = 0; i < 30; i++)
            worker.Handler(new float[100]);

        gate.Release(1000);
        await worker.Retire().WaitAsync(Wait);

        // Block 0, then blocks 1..10 dropped (1000 samples = exactly the zero-fill limit), then blocks 11..30.
        string[] calls = [.. receiver.Calls];
        Assert.Equal("push:100", calls[0]);
        Assert.Equal("zero:1000", calls[1]);
        Assert.Equal(Enumerable.Repeat("push:100", 20), calls[2..]);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("queue full, dropped 100 samples", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGapAboveOneSecond_ResetsInsteadOfFilling()
    {
        var (worker, receiver, _) = Create();
        using var gate = new SemaphoreSlim(0);
        receiver.PushGate = gate;
        worker.Handler(new float[100]);
        Assert.True(receiver.PushEntered.Wait(Wait));
        for (int i = 0; i < 31; i++)
            worker.Handler(new float[100]);

        gate.Release(1000);
        await worker.Retire().WaitAsync(Wait);

        string[] calls = [.. receiver.Calls];
        Assert.Equal(["push:100", "reset"], calls[..2]);
        Assert.DoesNotContain(calls, c => c.StartsWith("zero:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReceiverExceptions_AreCaughtLoggedAndReset_AndTheWorkerContinues()
    {
        var logger = new SstvSessionServicePttSafetyTests.RecordingLogger<DrmReceiveWorkerTests>();
        var (worker, receiver, _) = Create(logger);
        receiver.ThrowOnNextPush = new InvalidOperationException("rs_buffer overflow");
        worker.Handler(new float[10]);
        Until(() => receiver.Calls.Contains("reset"));
        receiver.ThrowOnNextPush = new InvalidOperationException("handler");
        worker.Handler(new float[10]);
        worker.Handler(new float[10]);
        await worker.Retire().WaitAsync(Wait);

        Assert.Equal(["push:10", "reset", "push:10", "reset", "push:10"], receiver.Calls.ToArray());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("receiver threw", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACallbackThrow_IsIsolated_AndDoesNotResetTheReceiver()
    {
        var (worker, receiver, sink) = Create();
        sink.OnState = _ => throw new InvalidOperationException("subscriber");
        receiver.OnPush = r => r.RaiseState(FakeDrmReceiver.State());
        worker.Handler(new float[10]);
        worker.Handler(new float[10]);
        await worker.Retire().WaitAsync(Wait);

        Assert.Equal(["push:10", "push:10"], receiver.Calls.ToArray());
    }

    [Fact]
    public async Task Retire_DrainsTheQueue_DeliversFiles_SuppressesStates_AndIsIdempotent()
    {
        var (worker, receiver, sink) = Create();
        using var gate = new SemaphoreSlim(0);
        receiver.PushGate = gate;
        receiver.OnPush = r =>
        {
            r.RaiseState(FakeDrmReceiver.State());
            r.RaiseFile(FakeDrmReceiver.File("a.jp2", [1, 2, 3]));
        };
        worker.Handler(new float[10]);
        Assert.True(receiver.PushEntered.Wait(Wait));
        worker.Handler(new float[10]);

        Task first = worker.Retire();
        Task second = worker.Retire();
        worker.Handler(new float[10]);
        gate.Release(10);
        await first.WaitAsync(Wait);
        await second.WaitAsync(Wait);
        await worker.StopAsync(Wait);
        await worker.StopAsync(Wait);

        // Both queued blocks drained, the one after Retire was refused; files still delivered, states not.
        Assert.Equal(["push:10", "push:10"], receiver.Calls.ToArray());
        Assert.Equal(2, sink.Files.Count);
        Assert.Empty(sink.States);
    }

    [Fact]
    public async Task States_CarryTheWorkerGeneration()
    {
        var (worker, receiver, sink) = Create();
        receiver.OnPush = r => r.RaiseState(FakeDrmReceiver.State());
        worker.Handler(new float[10]);
        Until(() => { lock (sink) return sink.States.Count == 1; });
        await worker.Retire().WaitAsync(Wait);

        Assert.Equal(7, sink.States[0].Generation);
    }

    [Fact]
    public async Task StopAsync_IsBounded_WhileTheReceiverIsStuck()
    {
        var (worker, receiver, _) = Create();
        using var gate = new SemaphoreSlim(0);
        receiver.PushGate = gate;
        worker.Handler(new float[10]);
        Assert.True(receiver.PushEntered.Wait(Wait));
        worker.Handler(new float[10]);

        await worker.StopAsync(TimeSpan.FromMilliseconds(50)).WaitAsync(Wait);
        Assert.False(worker.Completion.IsCompleted);

        // Cancel stops at the next block: the queued one is never pushed.
        gate.Release();
        await worker.Completion.WaitAsync(Wait);
        Assert.Equal(["push:10"], receiver.Calls.ToArray());
    }
}
