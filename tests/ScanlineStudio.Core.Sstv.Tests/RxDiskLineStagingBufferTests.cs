using Microsoft.Extensions.Logging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// RX buffer subsystem Phase 7 -- isolated tests for <see cref="RxDiskLineStagingBuffer"/>, before
/// any decoder wiring exists (a later sub-piece). Mirrors <see cref="RxLineStagingBufferTests"/>'s
/// own structure for the append/read/clear contract it shares with the RAM implementation via
/// <see cref="IRxLineStagingBuffer"/>, plus tests for what's genuinely different about a disk-backed
/// implementation: unbounded growth, write-failure handling, and disposal.
/// </summary>
public class RxDiskLineStagingBufferTests
{
    [Fact]
    public void TryAppendLine_SucceedsAndValuesAreReadableInOrder()
    {
        using var buffer = new RxDiskLineStagingBuffer();
        double[] demod = [1.0, 2.0, 3.0];
        double[] sync = [10.0, 20.0, 30.0];

        var accepted = buffer.TryAppendLine(demod, sync);

        Assert.True(accepted);
        Assert.Equal(3, buffer.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(demod[i], buffer.DemodulatedAt(i));
            Assert.Equal(sync[i], buffer.SyncEnvelopeAt(i));
        }
    }

    [Fact]
    public void TryAppendLine_MultipleLines_AccumulatesInChronologicalOrder()
    {
        using var buffer = new RxDiskLineStagingBuffer();

        Assert.True(buffer.TryAppendLine([1.0, 2.0], [-1.0, -2.0]));
        Assert.True(buffer.TryAppendLine([3.0, 4.0], [-3.0, -4.0]));

        Assert.Equal(4, buffer.Count);
        Assert.Equal([1.0, 2.0, 3.0, 4.0], new[] { buffer.DemodulatedAt(0), buffer.DemodulatedAt(1), buffer.DemodulatedAt(2), buffer.DemodulatedAt(3) });
        Assert.Equal([-1.0, -2.0, -3.0, -4.0], new[] { buffer.SyncEnvelopeAt(0), buffer.SyncEnvelopeAt(1), buffer.SyncEnvelopeAt(2), buffer.SyncEnvelopeAt(3) });
    }

    [Fact]
    public void TryAppendLine_LinesOfVaryingWidth_ReadBackExactly()
    {
        // A rented ArrayPool buffer is routinely larger than requested (power-of-two buckets) --
        // this pins that the WRITTEN stream never includes the rented array's padding, only the
        // logical length, for lines of genuinely different widths (the real-world case: fractional-
        // carry rounding, mid-reception Auto-Slant commits -- see RxLineStagingBuffer's own doc
        // comment on why per-line width isn't constant in this port).
        using var buffer = new RxDiskLineStagingBuffer();
        buffer.TryAppendLine([1.0, 2.0, 3.0], [0.0, 0.0, 0.0]); // 3 samples
        buffer.TryAppendLine([4.0, 5.0], [0.0, 0.0]); // 2 samples -- narrower than the rented buffer for the first line
        buffer.TryAppendLine([6.0, 7.0, 8.0, 9.0], [0.0, 0.0, 0.0, 0.0]); // 4 samples

        Assert.Equal(9, buffer.Count);
        Assert.Equal([1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0], Enumerable.Range(0, 9).Select(buffer.DemodulatedAt));
    }

    [Fact]
    public void TryAppendLine_MismatchedStreamLengths_Throws()
    {
        using var buffer = new RxDiskLineStagingBuffer();

        Assert.Throws<ArgumentException>(() => buffer.TryAppendLine([1.0, 2.0, 3.0], [1.0, 2.0]));
    }

    [Fact]
    public void LineCount_TracksSuccessfulAppendsOnly()
    {
        using var buffer = new RxDiskLineStagingBuffer();

        Assert.Equal(0, buffer.LineCount);
        buffer.TryAppendLine([1.0, 2.0], [3.0, 4.0]);
        Assert.Equal(1, buffer.LineCount);
        buffer.TryAppendLine([5.0], [6.0]);
        Assert.Equal(2, buffer.LineCount);
    }

    [Fact]
    public void SampleCountThroughLine_ReflectsVaryingPerLineWidths()
    {
        using var buffer = new RxDiskLineStagingBuffer();
        buffer.TryAppendLine([1.0, 2.0, 3.0], [0.0, 0.0, 0.0]); // 3 samples
        buffer.TryAppendLine([4.0, 5.0], [0.0, 0.0]); // 2 samples
        buffer.TryAppendLine([6.0, 7.0, 8.0, 9.0], [0.0, 0.0, 0.0, 0.0]); // 4 samples

        Assert.Equal(3, buffer.LineCount);
        Assert.Equal(0, buffer.SampleCountThroughLine(0));
        Assert.Equal(3, buffer.SampleCountThroughLine(1));
        Assert.Equal(5, buffer.SampleCountThroughLine(2));
        Assert.Equal(9, buffer.SampleCountThroughLine(3));
        Assert.Equal(9, buffer.SampleCountThroughLine(100)); // clamped to LineCount, matches full Count
    }

    [Fact]
    public void Clear_ResetsCountAndLineCountToZero()
    {
        using var buffer = new RxDiskLineStagingBuffer();
        buffer.TryAppendLine([1.0, 2.0], [3.0, 4.0]);

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Equal(0, buffer.LineCount);
        Assert.Equal(0, buffer.SampleCountThroughLine(5));
    }

    [Fact]
    public void Clear_ThenAppend_StartsFreshAtIndexZero_NotAppendingToStaleData()
    {
        // Exercises the drain-then-truncate ordering (round-1/round-2 plan-review's biggest finding)
        // end to end: without draining before truncating, a still-in-flight pre-Clear write could
        // land at post-truncate offsets. If that ordering were broken, this test would either read
        // back stale/corrupted data or throw.
        //
        // Code-review finding: a single 2-sample line before Clear() is too weak to actually pin this
        // -- the consumer has almost certainly already flushed it before Clear() even runs, so a
        // MISSING drain would likely still pass. Queuing many lines first makes writes genuinely still
        // in flight when Clear() is called.
        using var buffer = new RxDiskLineStagingBuffer();
        for (var i = 0; i < 100; i++)
        {
            buffer.TryAppendLine([i], [i]);
        }

        buffer.Clear();
        buffer.TryAppendLine([5.0], [6.0]);

        Assert.Equal(1, buffer.Count);
        Assert.Equal(5.0, buffer.DemodulatedAt(0));
        Assert.Equal(6.0, buffer.SyncEnvelopeAt(0));
        Assert.False(buffer.HasWriteFailed);
    }

    [Fact]
    public void DemodulatedAt_AfterAppendFollowingARead_ReturnsFreshData_NotAStaleCachedSnapshot()
    {
        // The read snapshot is invalidated by the next TryAppendLine (InvalidateSnapshot, called from
        // TryAppendLine) -- this is the exact bug class the snapshot-caching design introduces, and
        // the exact production pattern (a replay read pass, then capture resumes). Unpinned by every
        // other test here, which only reads once per buffer.
        using var buffer = new RxDiskLineStagingBuffer();
        buffer.TryAppendLine([1.0], [2.0]);
        Assert.Equal(1.0, buffer.DemodulatedAt(0)); // builds and caches the first snapshot

        buffer.TryAppendLine([3.0], [4.0]); // must invalidate the cached snapshot above

        Assert.Equal(2, buffer.Count);
        Assert.Equal(1.0, buffer.DemodulatedAt(0));
        Assert.Equal(3.0, buffer.DemodulatedAt(1)); // only reachable if the snapshot was rebuilt, not served stale
        Assert.Equal(4.0, buffer.SyncEnvelopeAt(1));
    }

    [Fact]
    public void Clear_CalledRepeatedly_NeverKillsCaptureOnTheNextLine()
    {
        // Round-2 plan-review's core finding: an earlier draft used ChannelWriter.Complete() as the
        // drain mechanism, which permanently closes a channel -- the very next TryAppendLine after
        // ANY Clear() would have silently failed. This pins Clear() firing several times in a row
        // (matching real usage: InitializeSlant fires it at every fresh lock, PerformReplay fires it
        // at the tail of every pass) never latches HasWriteFailed on its own.
        using var buffer = new RxDiskLineStagingBuffer();

        for (var i = 0; i < 5; i++)
        {
            buffer.Clear();
            Assert.True(buffer.TryAppendLine([i], [i]));
            Assert.False(buffer.HasWriteFailed, $"HasWriteFailed latched after Clear() call #{i + 1} -- capture should never die from Clear() alone.");
        }
    }

    [Fact]
    public void DemodulatedAt_OutOfRange_Throws()
    {
        using var buffer = new RxDiskLineStagingBuffer();
        buffer.TryAppendLine([1.0], [2.0]);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.DemodulatedAt(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.DemodulatedAt(-1));
    }

    [Fact]
    public void SyncEnvelopeAt_OutOfRange_Throws()
    {
        using var buffer = new RxDiskLineStagingBuffer();
        buffer.TryAppendLine([1.0], [2.0]);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.SyncEnvelopeAt(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.SyncEnvelopeAt(-1));
    }

    [Fact]
    public void HasWriteFailed_InitiallyFalse()
    {
        using var buffer = new RxDiskLineStagingBuffer();

        Assert.False(buffer.HasWriteFailed);
    }

    [Fact]
    public void HasHeadroomForSamples_AlwaysTrue_NoRealCapacityNotion()
    {
        // RX buffer subsystem Phase 8. Disk-backed capture has no real capacity notion (Phase 7's
        // own "no RAM cap" design) -- matches legacy's own real behavior (disk mode skips the
        // capacity check entirely at both of CorrectSlant's call sites, since m_StgBuf == NULL there).
        using var buffer = new RxDiskLineStagingBuffer();

        Assert.True(buffer.HasHeadroomForSamples(0));
        Assert.True(buffer.HasHeadroomForSamples(int.MaxValue));
    }

    [Fact]
    public void HasHeadroomForSamples_FalseOnceWriteHasFailed()
    {
        using var buffer = new RxDiskLineStagingBuffer();
        Assert.True(buffer.TryAppendLine([1.0], [2.0]));
        _ = buffer.DemodulatedAt(0); // forces a drain -- line 1 is now guaranteed flushed
        buffer.CorruptWriteStreamForTests();
        buffer.TryAppendLine([3.0], [4.0]);
        buffer.Clear(); // drains and observes the failure

        Assert.True(buffer.HasWriteFailed);
        Assert.False(buffer.HasHeadroomForSamples(0));
    }

    [Fact]
    public async Task Growth_PastTheRamFormulasOwnCapacity_StillSucceeds()
    {
        // The entire point of Phase 7: RxLineStagingBufferTests.CapacitySamples_MatchesLegacyFormula
        // pins the RAM buffer's own cap at rate=1000 to exactly 282,700 SAMPLES (that test's own
        // comment: `257*1100*1000/1000 = 282,700`) -- the smallest realistic fixture in that file.
        // This appends MORE total samples than that concrete number (code-review-caught correction:
        // an earlier version of this test appended only 32,000 samples total, nowhere near even that
        // smallest RAM cap, so its own claim of "past the RAM cap" was false), confirming the
        // disk-backed implementation keeps accepting far past the point a RAM buffer at that rate
        // would start rejecting.
        //
        // Periodic yields (not a tight CPU-bound loop): the channel capacity is sized to absorb
        // TRANSIENT I/O hiccups in a real, audio-real-time-paced production caller (one line roughly
        // every 100ms-1s depending on mode) -- a genuinely unpaced tight loop can legitimately outrun
        // the background writer task and hit that bound, which is real, correct "writer can't keep
        // up" behavior (round-1 plan-review's own contract, exercised deliberately and deterministically
        // by ChannelFull_WriterCannotKeepUp_LatchesHasWriteFailed_WithoutThrowing above), not a bug --
        // it just isn't what THIS test is trying to prove. Fewer, larger lines (vs. many tiny ones)
        // keeps total iterations low enough that periodic yields reliably let the consumer keep pace.
        using var buffer = new RxDiskLineStagingBuffer();
        const int lines = 150;
        const int samplesPerLine = 2000; // 150 * 2000 = 300,000 > 282,700 (the RAM fixture's own cap at rate=1000)
        var line = new double[samplesPerLine];

        for (var i = 0; i < lines; i++)
        {
            Array.Fill(line, (double)i);
            Assert.True(buffer.TryAppendLine(line, line), $"Line {i} was rejected -- disk-backed capture should not have a RAM-sized cap.");
            if (i % 8 == 0)
            {
                await Task.Delay(1);
            }
        }

        Assert.Equal(lines, buffer.LineCount);
        Assert.Equal(lines * samplesPerLine, buffer.Count);
        Assert.True(buffer.Count > 282_700, "Test setup problem: this run should exceed RxLineStagingBufferTests's own smallest RAM-capacity fixture (282,700 samples at rate=1000) to actually prove the point.");
        Assert.False(buffer.HasWriteFailed);

        // Spot-check the read-back is correct after a genuinely large capture, not just the small
        // cases the other tests use.
        Assert.Equal(0.0, buffer.DemodulatedAt(0));
        Assert.Equal((double)(lines - 1), buffer.DemodulatedAt(buffer.Count - 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveChannelCapacity_Throws(int channelCapacityLines)
    {
        // Round-2 code-review nit: the leak-prevention try/catch in the test-only constructor
        // overload only covers the temp-file/stream creation steps, not the later
        // Channel.CreateBounded calls -- guarding against an invalid capacity here closes the one
        // realistic way to trigger that gap (this overload is test-only, no production caller can
        // pass an invalid value).
        Assert.Throws<ArgumentOutOfRangeException>(() => new RxDiskLineStagingBuffer(channelCapacityLines));
    }

    [Fact]
    public void WriteFailure_DetectedByConsumer_LatchesHasWriteFailed_AndStopsFurtherCapture()
    {
        // Code-review-caught flakiness fix: the FIRST append must be drained (via a read, which
        // internally drains) BEFORE corrupting the stream -- without that, nothing guarantees the
        // consumer has actually processed line 1 before CorruptWriteStreamForTests() disposes the
        // stream out from under it, so the corrupted disposal could race a concurrent Write() on the
        // consumer thread (throwing on the wrong thread) instead of cleanly failing the NEXT write.
        using var buffer = new RxDiskLineStagingBuffer();
        Assert.True(buffer.TryAppendLine([1.0], [2.0]));
        _ = buffer.DemodulatedAt(0); // forces a drain -- line 1 is now guaranteed flushed

        // Deterministic fault injection (see CorruptWriteStreamForTests's own doc comment) -- the
        // background consumer's next write attempt on the demodulated stream will now throw.
        buffer.CorruptWriteStreamForTests();

        // Whether THIS specific append is admitted before the consumer observes the corruption is
        // itself a race (the corruption is only guaranteed OBSERVED after the Clear() drain below) --
        // deliberately not asserted either way.
        buffer.TryAppendLine([3.0], [4.0]);

        // Clear()'s own drain barrier forces this test to wait for the consumer to actually observe
        // and process the corrupted write before asserting -- without this, the assertion below
        // would race the background consumer task.
        buffer.Clear();

        Assert.True(buffer.HasWriteFailed);
        Assert.False(buffer.TryAppendLine([5.0], [6.0]), "TryAppendLine must reject every call once HasWriteFailed is latched -- capture simply stops, matching the RAM buffer's own capacity-exhaustion contract.");
    }

    [Fact]
    public void ChannelFull_WriterCannotKeepUp_LatchesHasWriteFailed_WithoutThrowing()
    {
        // The design's own explicit contract (round-1 plan-review): a full channel is treated exactly
        // like a write failure, not an exception. Deterministic via the test-only tiny-capacity
        // constructor overload -- appending capacity+1 lines with NO yield in between reliably beats
        // the background consumer task to the channel (the consumer needs at least one thread-pool
        // scheduling round-trip to process even the first item), unlike trying to force this at the
        // real production capacity of 128.
        using var buffer = new RxDiskLineStagingBuffer(channelCapacityLines: 1);

        var results = new bool[8];
        for (var i = 0; i < results.Length; i++)
        {
            results[i] = buffer.TryAppendLine([i], [i]);
        }

        Assert.Contains(false, results); // at least one line was rejected once the tiny channel filled
        Assert.True(buffer.HasWriteFailed);

        var exception = Record.Exception(() => buffer.TryAppendLine([99.0], [98.0]));
        Assert.Null(exception); // never throws, even while latched
        Assert.False(buffer.TryAppendLine([99.0], [98.0]));
    }

    [Fact]
    public void Dispose_DeletesBothScratchFiles()
    {
        var buffer = new RxDiskLineStagingBuffer();
        buffer.TryAppendLine([1.0], [2.0]);
        var demodPath = buffer.DemodPathForTests;
        var syncPath = buffer.SyncPathForTests;
        Assert.True(File.Exists(demodPath), "Test setup problem: the scratch file should exist before Dispose().");
        Assert.True(File.Exists(syncPath), "Test setup problem: the scratch file should exist before Dispose().");

        buffer.Dispose();

        Assert.False(File.Exists(demodPath));
        Assert.False(File.Exists(syncPath));
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        // Round-2 plan-review finding: RestartableSstvDecoder is a container-created DI singleton, so
        // the DI container can dispose it at host shutdown IN ADDITION to SstvSessionService's own
        // explicit disposal call, in unspecified order -- Dispose() must be idempotent.
        var buffer = new RxDiskLineStagingBuffer();
        buffer.TryAppendLine([1.0], [2.0]);

        buffer.Dispose();
        var exception = Record.Exception(buffer.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_FirstStreamStageFailure_LogsAndAttemptsEveryLaterStageExactlyOnce()
    {
        var logger = new RecordingLogger<RxDiskLineStagingBuffer>();
        var stages = new List<RxDiskLineStagingBuffer.DisposeStage>();
        var returnedSnapshots = new List<double[]>();
        var buffer = new RxDiskLineStagingBuffer(
            channelCapacityLines: 8,
            logger,
            disposeStageFaultForTests: stage =>
            {
                stages.Add(stage);
                if (stage == RxDiskLineStagingBuffer.DisposeStage.DisposeDemodStream)
                {
                    throw new IOException("Injected dispose-stage failure.");
                }
            },
            returnSnapshotForTests: snapshot =>
            {
                returnedSnapshots.Add(snapshot);
                System.Buffers.ArrayPool<double>.Shared.Return(snapshot);
            });
        buffer.TryAppendLine([1.0], [2.0]);
        _ = buffer.DemodulatedAt(0); // materializes both pooled snapshots so both return stages run
        var demodPath = buffer.DemodPathForTests;
        var syncPath = buffer.SyncPathForTests;

        var exception = Record.Exception(buffer.Dispose);

        Assert.Null(exception);
        Assert.Equal(Enum.GetValues<RxDiskLineStagingBuffer.DisposeStage>(), stages);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("DisposeDemodStream", StringComparison.Ordinal));
        Assert.True(buffer.DemodStreamClosedForTests);
        Assert.True(buffer.SyncStreamClosedForTests);
        Assert.True(buffer.DemodSignalDisposedForTests);
        Assert.True(buffer.SyncSignalDisposedForTests);
        Assert.Equal(2, returnedSnapshots.Count);
        Assert.False(File.Exists(demodPath));
        Assert.False(File.Exists(syncPath));

        buffer.Dispose();
        Assert.Equal(Enum.GetValues<RxDiskLineStagingBuffer.DisposeStage>(), stages);
    }

    [Fact]
    public void Dispose_ThrowingLogger_CannotEscapeOrSkipCleanupTail()
    {
        var stages = new List<RxDiskLineStagingBuffer.DisposeStage>();
        var buffer = new RxDiskLineStagingBuffer(
            channelCapacityLines: 8,
            logger: new ThrowingLogger<RxDiskLineStagingBuffer>(),
            disposeStageFaultForTests: stage =>
            {
                stages.Add(stage);
                if (stage == RxDiskLineStagingBuffer.DisposeStage.DisposeDemodStream)
                {
                    throw new IOException("Injected dispose-stage failure.");
                }
            });
        var syncPath = buffer.SyncPathForTests;

        var exception = Record.Exception(buffer.Dispose);

        Assert.Null(exception);
        Assert.Contains(RxDiskLineStagingBuffer.DisposeStage.DeleteSyncFile, stages);
        Assert.True(buffer.DemodStreamClosedForTests);
        Assert.True(buffer.SyncStreamClosedForTests);
        Assert.True(buffer.DemodSignalDisposedForTests);
        Assert.True(buffer.SyncSignalDisposedForTests);
        Assert.False(File.Exists(syncPath));
    }

    [Fact]
    public void Dispose_ConsumerWaitTimeout_IsLoggedAndDoesNotSkipLaterCleanup()
    {
        var logger = new RecordingLogger<RxDiskLineStagingBuffer>();
        var stages = new List<RxDiskLineStagingBuffer.DisposeStage>();
        var returnedConsumerBuffers = 0;
        using var releaseConsumers = new ManualResetEventSlim(false);
        using var consumersEntered = new CountdownEvent(2);
        var buffer = new RxDiskLineStagingBuffer(
            channelCapacityLines: 8,
            logger,
            disposeStageFaultForTests: stages.Add,
            returnConsumerBufferForTests: buffer =>
            {
                Interlocked.Increment(ref returnedConsumerBuffers);
                System.Buffers.ArrayPool<double>.Shared.Return(buffer);
            },
            beforeConsumerWriteForTests: _ =>
            {
                consumersEntered.Signal();
                releaseConsumers.Wait();
            },
            disposeDrainTimeoutForTests: TimeSpan.FromMilliseconds(20));
        Assert.True(buffer.TryAppendLine([1.0], [2.0]));
        Assert.True(consumersEntered.Wait(TimeSpan.FromSeconds(2)), "Test setup problem: both real consumers must be blocked inside their write path.");
        Assert.True(buffer.TryAppendLine([3.0], [4.0]), "Each channel needs a second queued rental to verify the timeout tail returns more than the current item.");

        var exception = Record.Exception(buffer.Dispose);

        Assert.Null(exception);
        Assert.False(buffer.ConsumersCompletedForTests, "Both consumer waits must have timed out while the real tasks remained blocked.");
        Assert.Contains(RxDiskLineStagingBuffer.DisposeStage.DeleteSyncFile, stages);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("WaitDemodConsumer", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("WaitSyncConsumer", StringComparison.Ordinal));
        Assert.True(buffer.DemodStreamClosedForTests);
        Assert.True(buffer.SyncStreamClosedForTests);
        Assert.True(buffer.DemodSignalDisposedForTests);
        Assert.True(buffer.SyncSignalDisposedForTests);

        releaseConsumers.Set();
        Assert.True(SpinWait.SpinUntil(() => buffer.ConsumersCompletedForTests, TimeSpan.FromSeconds(2)),
            "Consumers must exit cleanly after the test releases the injected write barrier.");
        Assert.Equal(4, Volatile.Read(ref returnedConsumerBuffers));
    }

    [Fact]
    public void Clear_NeverThrows_EvenAfterAWriteFailure()
    {
        // Clear() is called from the decode path (InitializeSlant, and the tail of every
        // PerformReplay pass) -- it must never throw, even once the buffer is already in a failed
        // state.
        using var buffer = new RxDiskLineStagingBuffer();
        buffer.TryAppendLine([1.0], [2.0]);
        buffer.CorruptWriteStreamForTests();
        buffer.TryAppendLine([3.0], [4.0]);
        buffer.Clear(); // drains and observes the failure

        Assert.True(buffer.HasWriteFailed);
        var exception = Record.Exception(buffer.Clear);

        Assert.Null(exception);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class ThrowingLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("Injected logger failure.");
    }
}
