using System.Collections.Concurrent;
using ScanlineStudio.Abstractions.Drm;

namespace ScanlineStudio.Application.Tests;

/// <summary>A scriptable <see cref="IDrmReceiver"/>: records every call, can block inside <see cref="Push"/> on a
/// gate the test releases, can throw once, and raises its events on demand on the calling thread.</summary>
internal sealed class FakeDrmReceiver(int captureRate) : IDrmReceiver
{
    public int CaptureRate { get; } = captureRate;

    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>When set, each <see cref="Push"/> waits on it after recording the call.</summary>
    public SemaphoreSlim? PushGate { get; set; }

    /// <summary>Set by every <see cref="Push"/> before it waits on <see cref="PushGate"/>.</summary>
    public ManualResetEventSlim PushEntered { get; } = new(false);

    public Exception? ThrowOnNextPush { get; set; }

    /// <summary>Runs inside each <see cref="Push"/>, after the gate.</summary>
    public Action<FakeDrmReceiver>? OnPush { get; set; }

    public long PushedSamples => Interlocked.Read(ref _pushedSamples);

    private long _pushedSamples;

    public event Action<DrmReceivedFile>? FileReceived;

    public event Action<string>? ReceivingNameChanged;

    public event Action<string>? AlreadyReceived;

    public event Action<ReadOnlyMemory<byte>>? BsrReceived;

    public event Action<DrmSignalState>? SignalStateChanged;

    public void Push(ReadOnlySpan<float> samples)
    {
        Calls.Enqueue($"push:{samples.Length}");
        Interlocked.Add(ref _pushedSamples, samples.Length);
        PushEntered.Set();
        PushGate?.Wait();
        if (ThrowOnNextPush is { } ex)
        {
            ThrowOnNextPush = null;
            throw ex;
        }

        OnPush?.Invoke(this);
    }

    public void ZeroFill(int samples) => Calls.Enqueue($"zero:{samples}");

    public void Reset() => Calls.Enqueue("reset");

    public void RaiseFile(DrmReceivedFile file) => FileReceived?.Invoke(file);

    public void RaiseState(DrmSignalState state) => SignalStateChanged?.Invoke(state);

    public void RaiseAlreadyReceived(string name) => AlreadyReceived?.Invoke(name);

    public void RaiseBsr(byte[] bsr) => BsrReceived?.Invoke(bsr);

    public void RaiseReceivingName(string name) => ReceivingNameChanged?.Invoke(name);

    public static DrmReceivedFile File(string name, byte[] data, string callsign = "PA0TST", bool hybrid = false) =>
        new(name, name, name, data, 1, callsign, 10000, 1, 1, 1, 0, 0, 2, 1, data.Length, 1, 1, 0, 0, false, hybrid);

    public static DrmSignalState State(bool frameSync = true) =>
        new(true, true, true, frameSync, 1, 1, 1, "PA0TST", 3, 10, "x.jp2", 0);
}

/// <summary>Builds <see cref="FakeDrmReceiver"/>s; <see cref="BeforeCreate"/> lets a test block inside Start's
/// publish window.</summary>
internal sealed class FakeDrmReceiverFactory : IDrmReceiverFactory
{
    public ConcurrentQueue<FakeDrmReceiver> Created { get; } = new();

    public Func<int, bool> Supported { get; set; } = r => r is >= 8000 and <= 192000;

    public Action<int>? BeforeCreate { get; set; }

    public Action<FakeDrmReceiver>? Configure { get; set; }

    public FakeDrmReceiver? Last => Created.LastOrDefault();

    public bool IsSupportedRate(int captureRate) => Supported(captureRate);

    public IDrmReceiver Create(int captureRate)
    {
        BeforeCreate?.Invoke(captureRate);
        var r = new FakeDrmReceiver(captureRate);
        Configure?.Invoke(r);
        Created.Enqueue(r);
        return r;
    }
}
