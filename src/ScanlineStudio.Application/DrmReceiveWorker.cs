using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Drm;

namespace ScanlineStudio.Application;

/// <summary>Runs one <see cref="IDrmReceiver"/> on its own long-running task, fed from the capture callback through a
/// bounded drop-oldest queue. One instance per receive session, built at the capture rate.</summary>
/// <remarks>
/// <para><b>Producer</b> (<see cref="Handler"/>, the audio drain thread): never blocks beyond a short lock and never
/// copies; each block keeps the <c>SamplesCaptured</c> memory, which is fresh per callback. When more than
/// <see cref="QueueSeconds"/> of audio is queued the oldest blocks are dropped. After <see cref="Retire"/> a write is a
/// no-op.</para>
/// <para><b>Consumer</b>: blocks carry their cumulative start offset, so a drop shows up as a gap. A gap up to
/// <see cref="ZeroFillMaxSeconds"/> is zero-filled, which keeps the OFDM symbol timing (measured best in
/// <c>DrmReceiverTests.GapInjection</c>); a longer gap resets the receiver. A throw from the receiver or a callback is
/// logged (rate-limited), the receiver is reset and the worker continues.</para>
/// <para><b>Callbacks</b> run on the worker thread. A slow callback backs audio up into the queue, so drop-oldest and
/// zero-fill apply. <c>onSignalState</c> is suppressed from <see cref="Retire"/> on; <c>onFile</c> is not, so a retired
/// worker still delivers files completed while it drains.</para>
/// </remarks>
internal sealed partial class DrmReceiveWorker
{
    public const int QueueSeconds = 2;
    public const int ZeroFillMaxSeconds = 1;
    private const int LogEveryN = 200;

    private readonly IDrmReceiver _receiver;
    private readonly ILogger _logger;
    private readonly Action<DrmReceivedFile> _onFile;
    private readonly Action<DrmSignalState> _onSignalState;
    private readonly Action<string> _onAlreadyReceived;
    private readonly long _capacitySamples;
    private readonly long _zeroFillMaxSamples;
    private readonly object _lock = new();
    private readonly Queue<(long Offset, ReadOnlyMemory<float> Samples)> _queue = new();
    private Task _completion = Task.CompletedTask;
    private long _queuedSamples;
    private long _nextOffset;
    private bool _completed;
    private volatile bool _retired;
    private volatile bool _cancelled;
    private int _dropCount;
    private int _failureCount;
    private int _callbackFailureCount;

    public DrmReceiveWorker(IDrmReceiver receiver, long generation, ILogger logger, Action<DrmReceivedFile> onFile,
        Action<DrmSignalState> onSignalState, Action<string> onAlreadyReceived)
    {
        _receiver = receiver;
        Generation = generation;
        _logger = logger;
        _onFile = onFile;
        _onSignalState = onSignalState;
        _onAlreadyReceived = onAlreadyReceived;
        _capacitySamples = (long)receiver.CaptureRate * QueueSeconds;
        _zeroFillMaxSamples = (long)receiver.CaptureRate * ZeroFillMaxSeconds;
        Handler = Enqueue;
        _receiver.FileReceived += f => Invoke(_onFile, f);
        _receiver.AlreadyReceived += n => Invoke(_onAlreadyReceived, n);
        _receiver.SignalStateChanged += OnSignalState;
    }

    /// <summary>The session's generation of this worker; its signal states carry it.</summary>
    public long Generation { get; }

    /// <summary>The <c>SamplesCaptured</c> handler bound to this instance: a late call reaches only this queue.</summary>
    public Action<ReadOnlyMemory<float>> Handler { get; }

    /// <summary>Completes when the consumer task has exited.</summary>
    public Task Completion => _completion;

    /// <summary>Starts the consumer task. Call once.</summary>
    public void Start() =>
        _completion = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Completes the queue and suppresses signal states. Synchronous, never blocks beyond the queue lock,
    /// idempotent. The worker drains what is already queued, then exits.</summary>
    public Task Retire()
    {
        _retired = true;
        lock (_lock)
        {
            _completed = true;
            Monitor.PulseAll(_lock);
        }

        return _completion;
    }

    /// <summary><see cref="Retire"/>, then stop the drain at the next block. Idempotent.</summary>
    public Task Cancel()
    {
        _cancelled = true;
        return Retire();
    }

    /// <summary><see cref="Cancel"/>, then wait for the consumer up to <paramref name="timeout"/>. Idempotent.</summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        try
        {
            await Cancel().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            TryLog(() => Log.StopTimedOut(_logger, Generation, timeout));
        }
    }

    private void Enqueue(ReadOnlyMemory<float> samples)
    {
        if (samples.IsEmpty)
            return;
        int dropped = 0;
        long droppedSamples = 0;
        lock (_lock)
        {
            if (_completed)
                return;
            _queue.Enqueue((_nextOffset, samples));
            _nextOffset += samples.Length;
            _queuedSamples += samples.Length;
            while (_queuedSamples > _capacitySamples && _queue.Count > 1)
            {
                var old = _queue.Dequeue();
                _queuedSamples -= old.Samples.Length;
                droppedSamples += old.Samples.Length;
                dropped++;
            }

            Monitor.Pulse(_lock);
        }

        if (dropped > 0)
        {
            int count = Interlocked.Increment(ref _dropCount);
            if (count == 1 || count % LogEveryN == 0)
                TryLog(() => Log.Dropped(_logger, Generation, droppedSamples, count));
        }
    }

    private void Run()
    {
        long expected = 0;
        while (!_cancelled)
        {
            (long Offset, ReadOnlyMemory<float> Samples) block;
            lock (_lock)
            {
                while (_queue.Count == 0 && !_completed)
                    Monitor.Wait(_lock);
                if (_queue.Count == 0 || _cancelled)
                    break;
                block = _queue.Dequeue();
                _queuedSamples -= block.Samples.Length;
            }

            try
            {
                long gap = block.Offset - expected;
                if (gap > _zeroFillMaxSamples)
                    _receiver.Reset();
                else if (gap > 0)
                    _receiver.ZeroFill((int)gap);
                _receiver.Push(block.Samples.Span);
            }
            catch (Exception ex)
            {
                int count = Interlocked.Increment(ref _failureCount);
                if (count == 1 || count % LogEveryN == 0)
                    TryLog(() => Log.ReceiverFailed(_logger, Generation, count, ex));
                try
                {
                    _receiver.Reset();
                }
                catch (Exception resetEx)
                {
                    TryLog(() => Log.ReceiverFailed(_logger, Generation, count, resetEx));
                }
            }

            expected = block.Offset + block.Samples.Length;
        }

        TryLog(() => Log.Exited(_logger, Generation));
    }

    private void OnSignalState(DrmSignalState state)
    {
        if (!_retired)
            Invoke(_onSignalState, state with { Generation = Generation });
    }

    // A subscriber's throw must not reset the receiver, so callbacks are isolated here, not in Run's catch.
    private void Invoke<T>(Action<T> callback, T value)
    {
        try
        {
            callback(value);
        }
        catch (Exception ex)
        {
            int count = Interlocked.Increment(ref _callbackFailureCount);
            if (count == 1 || count % LogEveryN == 0)
                TryLog(() => Log.CallbackFailed(_logger, Generation, count, ex));
        }
    }

    // A throwing logging provider must not fault the worker (or the drain thread for Dropped).
    private static void TryLog(Action log)
    {
        try
        {
            log();
        }
        catch (Exception)
        {
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "DRM RX worker {Generation}: queue full, dropped {Samples} samples (drop event {Count})")]
        public static partial void Dropped(ILogger logger, long generation, long samples, int count);

        [LoggerMessage(Level = LogLevel.Warning, Message = "DRM RX worker {Generation}: receiver threw ({Count} time(s)); receiver reset")]
        public static partial void ReceiverFailed(ILogger logger, long generation, int count, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "DRM RX worker {Generation}: a callback threw ({Count} time(s))")]
        public static partial void CallbackFailed(ILogger logger, long generation, int count, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "DRM RX worker {Generation}: stop did not finish within {Timeout}")]
        public static partial void StopTimedOut(ILogger logger, long generation, TimeSpan timeout);

        [LoggerMessage(Level = LogLevel.Debug, Message = "DRM RX worker {Generation}: exited")]
        public static partial void Exited(ILogger logger, long generation);
    }
}
