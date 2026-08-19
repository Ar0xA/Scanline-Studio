using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// RX buffer subsystem Phase 7 -- disk-backed <see cref="IRxLineStagingBuffer"/> for
/// <see cref="RxBufferMode.Extended"/>, giving it a real store (today it has none; capture is
/// skipped entirely). Design/rationale fully tracked in
/// `/home/artien/.claude/plans/wise-riding-hearth.md`, through 2 rounds of mandatory auditor
/// plan-review per this project's CLAUDE.md &#167;7 (decode-path/concurrency work).
///
/// <b>Two files, not legacy's one interleaved file</b> (`Main.cpp:5007-5008`'s <c>WaveStg</c>):
/// legacy's interleave only works because its per-reception line width (<c>m_WD</c>) is constant;
/// this port's own per-line width varies (Auto-Slant commits change it mid-reception, see
/// <see cref="RxLineStagingBuffer"/>'s own doc comment), and this class's read contract is
/// random-access, not legacy's sequential cursor -- a deliberate, stated divergence.
///
/// <b>No <see cref="System.IO.FileOptions.DeleteOnClose"/></b> on the write handle: code-review
/// correction of an earlier (likely factually wrong) claim about Linux unlink-at-open timing --
/// the real, platform-independent reason is that <c>DeleteOnClose</c> requires every OTHER opener of
/// the same path to include <c>FileShare.Delete</c>, and every read snapshot (below) opens with
/// <c>FileShare.ReadWrite</c> -- a sharing-violation risk on Windows specifically not worth taking.
/// Files are deleted explicitly in <see cref="Dispose"/> instead, after every stream referencing them
/// is closed. Trade-off: this removes the "delete survives even a leaked, never-disposed instance"
/// backstop <c>DeleteOnClose</c> would have given (no finalizer exists here either) -- the real
/// cleanup guarantee is <see cref="Dispose"/> actually being called, wired through the disposal-chain
/// sub-piece (decoder -&gt; <c>RestartableSstvDecoder</c> -&gt; <c>SstvSessionService</c>).
///
/// <b>Writes</b>: one bounded <see cref="Channel{T}"/> per stream (capacity
/// <see cref="DefaultChannelCapacityLines"/> in production; constructor-injectable for tests --
/// round-2 code-review nit fixed: this doc used to state the capacity flatly, before that overload
/// existed), each drained in order by its own dedicated background
/// <see cref="Task"/> via a synchronous <see cref="System.IO.FileStream.Write(ReadOnlySpan{byte})"/>
/// -- the consumer task itself already runs off the decode/drain thread (the actual requirement),
/// so its own I/O doesn't need to be `async` on top of that. <see cref="TryAppendLine"/> rents a
/// buffer per stream from <see cref="ArrayPool{T}.Shared"/>, copies the caller's span into it (the
/// caller's own per-line accumulator is cleared/reused immediately after this call, so the copy is
/// required regardless of backend), and enqueues `(buffer, logicalLength)` -- the logical length
/// travels alongside the buffer because <see cref="ArrayPool{T}.Rent"/> routinely returns an array
/// LARGER than requested (power-of-two buckets); the consumer writes exactly `logicalLength`
/// samples, never the rented array's full physical length, before returning it to the pool.
///
/// <b>The channel-capacity check happens BEFORE writing to either channel</b>, using this class's own
/// enqueued/flushed counters (in-flight = enqueued - flushed), not <c>Channel.Reader.Count</c> --
/// self-contained, and correct under the single-producer assumption (only <see cref="TryAppendLine"/>
/// ever calls <c>TryWrite</c>, so a full-check-then-write can't race against another writer). This
/// keeps the two files' line boundaries in lockstep: a line is admitted to BOTH channels or neither,
/// never just one.
///
/// <b>The drain barrier is a flush-sentinel wait on per-stream counters, never
/// <see cref="ChannelWriter{T}.Complete()"/></b> -- an earlier draft of this design used `Complete()`
/// for this and was caught in round-2 plan-review: a completed channel is permanently closed, so
/// reusing it after <see cref="Clear"/> (which fires far more often than once per reception -- every
/// fresh lock, and the tail of every replay pass) would silently kill capture on the very next line.
/// `Complete()` is reserved for <see cref="Dispose"/> only, where the channel is genuinely done.
///
/// <b>Reads</b> (<see cref="DemodulatedAt"/>/<see cref="SyncEnvelopeAt"/>): NOT a persistently-open
/// <c>MemoryMappedFile</c> (round-1 plan-review finding: unsafe here because capture resumes after
/// every replay pass, and a live memory-mapped view pins the file length on Windows). Instead, the
/// first read after any new writes drains both channels (the same flush-sentinel barrier as
/// <see cref="Clear"/>) and bulk-reads each file's current full contents into a pooled snapshot,
/// invalidated by the next <see cref="TryAppendLine"/> or <see cref="Clear"/> call. This makes
/// Extended's "no RAM cap" promise a CAPTURE-time guarantee, not a read-time one -- peak read RAM is
/// proportional to whatever's staged since the last replay pass (accepted tradeoff, round-2
/// confirmed).
/// </summary>
internal sealed partial class RxDiskLineStagingBuffer : IRxLineStagingBuffer
{
    internal enum DisposeStage
    {
        CompleteDemodChannel,
        CompleteSyncChannel,
        WaitDemodConsumer,
        WaitSyncConsumer,
        DisposeDemodStream,
        DisposeSyncStream,
        DisposeDemodSignal,
        DisposeSyncSignal,
        ReturnDemodSnapshot,
        ReturnSyncSnapshot,
        DeleteDemodFile,
        DeleteSyncFile,
    }

    // The production default -- generous slack against transient I/O hiccups without meaningfully
    // capping "no RAM cap" intent (a small, fixed overhead, 128 lines' worth of in-flight pooled
    // buffers, regardless of reception length).
    private const int DefaultChannelCapacityLines = 128;

    // An instance field, not just the const above: the internal test-only constructor overload lets a
    // test shrink this to deterministically exercise the "writer can't keep up" path without racing a
    // real background writer task at the production default.
    private readonly int _channelCapacityLines;

    // Bounds the two deliberate blocking points in this class (Clear()'s drain, and the first read
    // after new writes) -- both are bounded by "lines since the last truncation" (Phase 6c's own
    // truncate-on-replay already bounds this), not by reception length, so this timeout should never
    // realistically fire in normal operation.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private readonly string _demodPath;
    private readonly string _syncPath;
    private readonly FileStream _demodWriteStream;
    private readonly FileStream _syncWriteStream;
    private readonly Channel<(double[] Buffer, int Length)> _demodChannel;
    private readonly Channel<(double[] Buffer, int Length)> _syncChannel;
    private readonly Task _demodConsumerTask;
    private readonly Task _syncConsumerTask;

    private long _demodEnqueued;
    private long _demodFlushed;
    private readonly SemaphoreSlim _demodFlushSignal = new(0, int.MaxValue);
    private long _syncEnqueued;
    private long _syncFlushed;
    private readonly SemaphoreSlim _syncFlushSignal = new(0, int.MaxValue);

    // Optimistic bookkeeping (round-1/round-2 verified safe): advances the moment a line is
    // admitted to both channels, not once its write actually lands on disk. Safe because capture,
    // replay reads, and Clear() all run on the same drain thread inside PushSamples -- there is no
    // second reader of these counters from another thread.
    private readonly List<int> _lineBoundaries = new();
    private int _count;

    private double[]? _demodSnapshot;
    private double[]? _syncSnapshot;

    private volatile bool _hasWriteFailed;
    private bool _disposed;
    private readonly ILogger<RxDiskLineStagingBuffer> _logger;
    private readonly Action<DisposeStage>? _disposeStageFaultForTests;
    private readonly Action<double[]> _returnSnapshot;
    private readonly Action<double[]> _returnConsumerBuffer;
    private readonly Action<bool>? _beforeConsumerWriteForTests;
    private readonly TimeSpan _disposeDrainTimeout;

    public RxDiskLineStagingBuffer(ILogger<RxDiskLineStagingBuffer>? logger = null)
        : this(DefaultChannelCapacityLines, logger)
    {
    }

    /// <summary>Test-only: lets a test construct with a deliberately tiny channel capacity so the
    /// "writer can't keep up" admission-rejection path (<see cref="TryAppendLine"/>'s own capacity
    /// check) can be exercised deterministically, without racing a real background writer task at the
    /// production default of <see cref="DefaultChannelCapacityLines"/>.</summary>
    internal RxDiskLineStagingBuffer(
        int channelCapacityLines,
        ILogger<RxDiskLineStagingBuffer>? logger = null,
        Action<DisposeStage>? disposeStageFaultForTests = null,
        Action<double[]>? returnSnapshotForTests = null,
        Action<double[]>? returnConsumerBufferForTests = null,
        Action<bool>? beforeConsumerWriteForTests = null,
        TimeSpan? disposeDrainTimeoutForTests = null)
    {
        // Round-2 code-review nit: the leak-prevention try/catch below only covers the temp-file/
        // stream creation steps, not the later Channel.CreateBounded calls -- a 0-or-negative capacity
        // would throw ArgumentOutOfRangeException from BoundedChannelOptions AFTER those files/streams
        // already exist, leaking exactly what that try/catch exists to prevent. Guarding here (this
        // constructor overload is test-only; no production caller can pass an invalid value) closes
        // the one realistic trigger without needing to widen that try/catch's scope.
        ArgumentOutOfRangeException.ThrowIfLessThan(channelCapacityLines, 1);
        _channelCapacityLines = channelCapacityLines;
        _logger = logger ?? NullLogger<RxDiskLineStagingBuffer>.Instance;
        _disposeStageFaultForTests = disposeStageFaultForTests;
        _returnSnapshot = returnSnapshotForTests ?? (snapshot => ArrayPool<double>.Shared.Return(snapshot));
        _returnConsumerBuffer = returnConsumerBufferForTests ?? (buffer => ArrayPool<double>.Shared.Return(buffer));
        _beforeConsumerWriteForTests = beforeConsumerWriteForTests;
        _disposeDrainTimeout = disposeDrainTimeoutForTests ?? DrainTimeout;

        // Code-review nit: if any step here throws (a second GetTempFileName/FileStream open
        // failing after the first succeeded is the realistic case -- e.g. a genuinely full temp
        // directory), whatever was already created before the throw would otherwise leak (no
        // finalizer exists on this class, and a constructor that never returns means Dispose() can
        // never be called on it either). Local variables + a catch-and-cleanup-then-rethrow, only
        // assigned to the real fields once every step has succeeded.
        string? demodPath = null;
        string? syncPath = null;
        FileStream? demodWriteStream = null;
        FileStream? syncWriteStream = null;
        try
        {
            demodPath = Path.GetTempFileName();
            syncPath = Path.GetTempFileName();
            demodWriteStream = new FileStream(demodPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            syncWriteStream = new FileStream(syncPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        }
        catch
        {
            demodWriteStream?.Dispose();
            syncWriteStream?.Dispose();
            if (demodPath is not null)
            {
                try
                {
                    File.Delete(demodPath);
                }
                catch
                {
                }
            }

            if (syncPath is not null)
            {
                try
                {
                    File.Delete(syncPath);
                }
                catch
                {
                }
            }

            throw;
        }

        _demodPath = demodPath;
        _syncPath = syncPath;
        _demodWriteStream = demodWriteStream;
        _syncWriteStream = syncWriteStream;

        _demodChannel = Channel.CreateBounded<(double[], int)>(new BoundedChannelOptions(_channelCapacityLines) { SingleReader = true, SingleWriter = true });
        _syncChannel = Channel.CreateBounded<(double[], int)>(new BoundedChannelOptions(_channelCapacityLines) { SingleReader = true, SingleWriter = true });

        _demodConsumerTask = Task.Run(() => ConsumeAsync(_demodChannel.Reader, _demodWriteStream, isDemod: true));
        _syncConsumerTask = Task.Run(() => ConsumeAsync(_syncChannel.Reader, _syncWriteStream, isDemod: false));
    }

    public int LineCount => _lineBoundaries.Count;

    public int Count => _count;

    public bool HasWriteFailed => _hasWriteFailed;

    /// <summary>RX buffer subsystem Phase 8. No real capacity notion for a disk-backed buffer
    /// (Phase 7's own design -- unbounded during capture, bounded only by disk space) -- always
    /// <see langword="true"/> unless <see cref="HasWriteFailed"/> is already set, matching legacy's
    /// own real behavior (`m_StgBuf == NULL`, disk mode, skips the capacity check entirely at both
    /// of `CorrectSlant`'s call sites, `Main.cpp:5268`/`:5416`). See
    /// <see cref="IRxLineStagingBuffer.HasHeadroomForSamples"/>'s own doc comment for the full
    /// contract.</summary>
    public bool HasHeadroomForSamples(int additionalSamples) => !_hasWriteFailed;

    /// <summary>Test-only visibility into the demodulated-stream scratch file's path -- lets a test
    /// assert the file is actually deleted after <see cref="Dispose"/>.</summary>
    internal string DemodPathForTests => _demodPath;

    /// <summary>Test-only visibility into the sync-envelope-stream scratch file's path -- same
    /// reasoning as <see cref="DemodPathForTests"/>.</summary>
    internal string SyncPathForTests => _syncPath;

    internal bool DemodStreamClosedForTests => _demodWriteStream.SafeFileHandle.IsClosed;

    internal bool SyncStreamClosedForTests => _syncWriteStream.SafeFileHandle.IsClosed;

    internal bool DemodSignalDisposedForTests => IsDisposed(_demodFlushSignal);

    internal bool SyncSignalDisposedForTests => IsDisposed(_syncFlushSignal);

    internal bool ConsumersCompletedForTests => _demodConsumerTask.IsCompleted && _syncConsumerTask.IsCompleted;

    /// <summary>Test-only fault injection: disposes the demodulated-stream write handle out from
    /// under the background consumer task, deterministically exercising the real write-failure
    /// detection path (the consumer's own <c>catch</c> in <see cref="ConsumeAsync"/>) without needing
    /// to simulate an actual full-disk/IO-error condition. The object is intentionally left otherwise
    /// alive afterward so a test can keep asserting against it (<see cref="HasWriteFailed"/>,
    /// further <see cref="TryAppendLine"/> calls) -- not a substitute for a real
    /// <see cref="Dispose"/>.</summary>
    internal void CorruptWriteStreamForTests() => _demodWriteStream.Dispose();

    public bool TryAppendLine(ReadOnlySpan<double> demodulated, ReadOnlySpan<double> syncEnvelope)
    {
        if (demodulated.Length != syncEnvelope.Length)
        {
            throw new ArgumentException(
                $"{nameof(demodulated)} and {nameof(syncEnvelope)} must be the same length (got {demodulated.Length} and {syncEnvelope.Length}) -- both streams advance one entry per staged sample together.",
                nameof(syncEnvelope));
        }

        if (_hasWriteFailed)
        {
            return false;
        }

        // Both channels must have room, checked BEFORE writing to either -- single-producer (only
        // this method ever calls TryWrite), so this check-then-write can't race against another
        // writer. Keeps the two files' line boundaries in lockstep: admitted to both or neither.
        var demodInFlight = Interlocked.Read(ref _demodEnqueued) - Interlocked.Read(ref _demodFlushed);
        var syncInFlight = Interlocked.Read(ref _syncEnqueued) - Interlocked.Read(ref _syncFlushed);
        if (demodInFlight >= _channelCapacityLines || syncInFlight >= _channelCapacityLines)
        {
            // Writer can't keep up -- treated exactly like a write failure (round-1 fix): capture
            // stops, nothing throws.
            _hasWriteFailed = true;
            return false;
        }

        var demodRented = ArrayPool<double>.Shared.Rent(demodulated.Length);
        demodulated.CopyTo(demodRented);
        var syncRented = ArrayPool<double>.Shared.Rent(syncEnvelope.Length);
        syncEnvelope.CopyTo(syncRented);

        if (!_demodChannel.Writer.TryWrite((demodRented, demodulated.Length)))
        {
            // Unreachable given the capacity check above under the single-producer assumption --
            // handled defensively rather than assumed.
            _returnConsumerBuffer(demodRented);
            _returnConsumerBuffer(syncRented);
            _hasWriteFailed = true;
            return false;
        }

        Interlocked.Increment(ref _demodEnqueued);

        if (!_syncChannel.Writer.TryWrite((syncRented, syncEnvelope.Length)))
        {
            // Asymmetric failure: demod already admitted, sync didn't. Fail loud rather than let the
            // two files silently drift out of lockstep -- this is exactly the corruption the
            // capacity pre-check above exists to prevent, so reaching here means that invariant
            // broke somewhere.
            _returnConsumerBuffer(syncRented);
            _hasWriteFailed = true;
            return false;
        }

        Interlocked.Increment(ref _syncEnqueued);

        _count += demodulated.Length;
        _lineBoundaries.Add(_count);
        InvalidateSnapshot();
        return true;
    }

    public int SampleCountThroughLine(int lineCount)
    {
        if (lineCount <= 0 || _lineBoundaries.Count == 0)
        {
            return 0;
        }

        return _lineBoundaries[Math.Min(lineCount, _lineBoundaries.Count) - 1];
    }

    public double DemodulatedAt(int index)
    {
        // Code-review finding: without this, a post-Dispose call would fall through to
        // EnsureSnapshot -> DrainToCurrentPoint, which waits on the (by then disposed)
        // SemaphoreSlim fields and throws ObjectDisposedException from deep in that call stack --
        // technically not wrong, but an incidental exception from an implementation detail rather
        // than a clean, predictable one at the actual API boundary.
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (index < 0 || index >= _count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        EnsureSnapshot();
        return _demodSnapshot![index];
    }

    public double SyncEnvelopeAt(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (index < 0 || index >= _count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        EnsureSnapshot();
        return _syncSnapshot![index];
    }

    public void Clear()
    {
        // Must never throw -- called from the decode path (InitializeSlant at every fresh lock, and
        // the tail of every PerformReplay pass).
        if (_disposed)
        {
            return;
        }

        if (!DrainToCurrentPoint())
        {
            // Timeout: latch HasWriteFailed and skip the truncate -- truncating anyway risks a
            // still-in-flight pre-Clear write landing at post-truncate offsets, the exact corruption
            // this drain-before-truncate ordering exists to prevent. Capture is already stopping
            // (HasWriteFailed gates TryAppendLine), so bookkeeping is left as-is rather than reset
            // against a file that wasn't actually truncated.
            _hasWriteFailed = true;
            return;
        }

        try
        {
            _demodWriteStream.SetLength(0);
            _demodWriteStream.Position = 0;
            _syncWriteStream.SetLength(0);
            _syncWriteStream.Position = 0;
        }
        catch
        {
            _hasWriteFailed = true;
            return;
        }

        _count = 0;
        _lineBoundaries.Clear();
        InvalidateSnapshot();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        TryDisposeStage(DisposeStage.CompleteDemodChannel, () => _demodChannel.Writer.TryComplete());
        TryDisposeStage(DisposeStage.CompleteSyncChannel, () => _syncChannel.Writer.TryComplete());
        TryDisposeStage(DisposeStage.WaitDemodConsumer, () => WaitForConsumer(_demodConsumerTask, "demodulated"));
        TryDisposeStage(DisposeStage.WaitSyncConsumer, () => WaitForConsumer(_syncConsumerTask, "sync-envelope"));
        TryDisposeStage(DisposeStage.DisposeDemodStream, _demodWriteStream.Dispose);
        TryDisposeStage(DisposeStage.DisposeSyncStream, _syncWriteStream.Dispose);
        TryDisposeStage(DisposeStage.DisposeDemodSignal, _demodFlushSignal.Dispose);
        TryDisposeStage(DisposeStage.DisposeSyncSignal, _syncFlushSignal.Dispose);

        var demodSnapshot = _demodSnapshot;
        _demodSnapshot = null;
        if (demodSnapshot is not null)
        {
            TryDisposeStage(DisposeStage.ReturnDemodSnapshot, () => _returnSnapshot(demodSnapshot));
        }

        var syncSnapshot = _syncSnapshot;
        _syncSnapshot = null;
        if (syncSnapshot is not null)
        {
            TryDisposeStage(DisposeStage.ReturnSyncSnapshot, () => _returnSnapshot(syncSnapshot));
        }

        // Explicit delete (not FileOptions.DeleteOnClose -- see this class's own doc comment on why).
        // Each path is independent so one failed close/delete cannot skip the other file's cleanup.
        TryDisposeStage(DisposeStage.DeleteDemodFile, () => File.Delete(_demodPath));
        TryDisposeStage(DisposeStage.DeleteSyncFile, () => File.Delete(_syncPath));
    }

    private void WaitForConsumer(Task consumerTask, string streamName)
    {
        var completed = consumerTask.Wait(_disposeDrainTimeout);
        if (!completed)
        {
            throw new TimeoutException($"Timed out draining the {streamName} RX staging consumer.");
        }
    }

    private void TryDisposeStage(DisposeStage stage, Action action)
    {
        try
        {
            // The real action remains guaranteed even when the test seam injects a stage failure;
            // this prevents a test from claiming a resource-dispose attempt that it actually skipped.
            try
            {
                _disposeStageFaultForTests?.Invoke(stage);
            }
            finally
            {
                action();
            }
        }
        catch (Exception ex)
        {
            TryLogDisposeStageFailure(stage, ex);
        }
    }

    private void TryLogDisposeStageFailure(DisposeStage stage, Exception exception)
    {
        try
        {
            Log.DisposeStageFailed(_logger, stage, exception);
        }
        catch
        {
            // A logger provider is external teardown infrastructure. It must not defeat the cleanup
            // isolation this method exists to provide.
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "RX disk staging cleanup stage {Stage} failed")]
        public static partial void DisposeStageFailed(ILogger logger, DisposeStage stage, Exception exception);
    }

    private async Task ConsumeAsync(ChannelReader<(double[] Buffer, int Length)> reader, FileStream stream, bool isDemod)
    {
        // Code-review finding: the per-item try/catch/finally below doesn't cover a failure in
        // Release() itself (e.g. the semaphore was already disposed by Dispose()'s own bounded-wait
        // timeout racing this loop) -- that would fault OUT of the finally block and abort the
        // `await foreach`, breaking this method's own stated invariant ("the task itself always
        // completes successfully" -- Clear()'s Task.Wait() must never see a faulted task). This outer
        // try/catch is the actual enforcement of that invariant; the inner one is just where a write
        // failure gets attributed to HasWriteFailed specifically.
        try
        {
            await foreach (var item in reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    _beforeConsumerWriteForTests?.Invoke(isDemod);
                    if (!_hasWriteFailed)
                    {
                        stream.Write(MemoryMarshal.AsBytes(item.Buffer.AsSpan(0, item.Length)));
                    }
                }
                catch
                {
                    // Write failed -- latch HasWriteFailed (checked by the next TryAppendLine call)
                    // and stop attempting further writes on this stream (the `if (!_hasWriteFailed)`
                    // guard above), matching legacy's own unchecked mmioWrite (no retry). Lines
                    // already enqueued before this point but not yet written are lost, not retried.
                    _hasWriteFailed = true;
                }
                finally
                {
                    _returnConsumerBuffer(item.Buffer);

                    // Always increment/release, even on failure or when skipped above -- otherwise a
                    // drain barrier waiting for this item would hang forever.
                    if (isDemod)
                    {
                        Interlocked.Increment(ref _demodFlushed);
                        _demodFlushSignal.Release();
                    }
                    else
                    {
                        Interlocked.Increment(ref _syncFlushed);
                        _syncFlushSignal.Release();
                    }
                }
            }
        }
        catch
        {
            _hasWriteFailed = true;
        }
        finally
        {
            // If teardown timed out, Dispose can close the signal while this consumer is blocked.
            // The current item's Release then throws and exits the loop; explicitly drain every
            // still-queued rental so the pool does not leak the remainder of the channel.
            while (reader.TryRead(out var pending))
            {
                try
                {
                    _returnConsumerBuffer(pending.Buffer);
                }
                catch
                {
                    _hasWriteFailed = true;
                }
            }
        }
    }

    private bool DrainToCurrentPoint()
    {
        var demodTarget = Interlocked.Read(ref _demodEnqueued);
        var syncTarget = Interlocked.Read(ref _syncEnqueued);
        var stopwatch = Stopwatch.StartNew();

        while (Interlocked.Read(ref _demodFlushed) < demodTarget)
        {
            var remaining = DrainTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero || !_demodFlushSignal.Wait(remaining))
            {
                return false;
            }
        }

        while (Interlocked.Read(ref _syncFlushed) < syncTarget)
        {
            var remaining = DrainTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero || !_syncFlushSignal.Wait(remaining))
            {
                return false;
            }
        }

        // FileStream buffers writes internally -- the flushed counters above only prove the
        // consumer's Write() CALL returned, not that the bytes actually left .NET's own userland
        // buffer via the OS write syscall. A separate FileStream handle (every read snapshot opens
        // one) would not see unflushed data. Safe to call from this thread here specifically because
        // the counters just confirmed the consumer is idle inside ReadAllAsync's await, not mid-Write
        // -- same reasoning that makes Clear()'s direct SetLength/Position calls on these same
        // streams safe. Wrapped: this method is also called from Clear(), which must never throw --
        // a flush failure (e.g. the stream was already disposed by a write failure elsewhere) is
        // reported the same way a drain timeout is, not propagated.
        try
        {
            _demodWriteStream.Flush();
            _syncWriteStream.Flush();
        }
        catch
        {
            return false;
        }

        return true;
    }

    private void EnsureSnapshot()
    {
        if (_demodSnapshot is not null)
        {
            return;
        }

        if (!DrainToCurrentPoint())
        {
            // Stuck writer -- best-effort read of whatever's actually on disk rather than hang or
            // throw; HasWriteFailed is already latching capture to a stop regardless.
            _hasWriteFailed = true;
        }

        var count = _count;

        // Round-2 code-review correction: an earlier version of this method caught OutOfMemoryException
        // here and degraded to empty (zero-length) snapshots while leaving _count > 0 -- that doesn't
        // actually avoid crashing the decode/replay thread, it just swaps a clear OutOfMemoryException
        // for a confusing IndexOutOfRangeException on the very next read (index 0 against a 0-length
        // array), since there is no way to "degrade" a fixed-size read buffer without allocating
        // something of comparable size in the first place. A genuine OOM here is left to propagate
        // honestly, matching standard .NET practice (catching OOM to keep running is rarely correct --
        // the process may already be in a degraded state).
        var demodSnapshot = ArrayPool<double>.Shared.Rent(count);
        var syncSnapshot = ArrayPool<double>.Shared.Rent(count);

        // A drain timeout above (rare -- see DrainToCurrentPoint's own bound), or the file genuinely
        // being shorter than `count` samples implies for any other reason, is caught inside
        // ReadSnapshot as a read failure -- code-review finding: an earlier version of this method
        // then still installed the snapshot as-is, silently serving whatever STALE bytes happened to
        // sit in the freshly-rented (not zeroed) array past whatever prefix WAS actually read. Fixed:
        // ReadSnapshot now explicitly zero-fills its destination on any failure, so a caller reading
        // an index past the actually-available data gets deterministic zeros, not garbage/NaN/Inf
        // fed into replay's slant math.
        var failed = false;
        ReadSnapshot(_demodPath, demodSnapshot, count, ref failed);
        ReadSnapshot(_syncPath, syncSnapshot, count, ref failed);

        if (failed)
        {
            _hasWriteFailed = true;
        }

        _demodSnapshot = demodSnapshot;
        _syncSnapshot = syncSnapshot;
    }

    private static void ReadSnapshot(string path, double[] destination, int count, ref bool failed)
    {
        if (count == 0)
        {
            return;
        }

        try
        {
            using var readStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            readStream.ReadExactly(MemoryMarshal.AsBytes(destination.AsSpan(0, count)));
        }
        catch
        {
            // Deterministic zeros, not whatever stale data a previous rental of this pooled array
            // happened to leave behind -- see this method's own caller for why that distinction
            // matters (a code-review-caught blocker: silently serving garbage into replay's math).
            Array.Clear(destination, 0, count);
            failed = true;
        }
    }

    private void InvalidateSnapshot()
    {
        if (_demodSnapshot is not null)
        {
            ArrayPool<double>.Shared.Return(_demodSnapshot);
            _demodSnapshot = null;
        }

        if (_syncSnapshot is not null)
        {
            ArrayPool<double>.Shared.Return(_syncSnapshot);
            _syncSnapshot = null;
        }
    }

    private static bool IsDisposed(SemaphoreSlim signal)
    {
        try
        {
            _ = signal.AvailableWaitHandle;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }
}
