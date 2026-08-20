namespace ScanlineStudio.Core.Audio.MiniAudio.Tests;

/// <summary>
/// Isolated tests for <see cref="MiniAudioRing"/> (piece Audio 3) -- produce/consume correctness
/// and wraparound, entirely independent of any real audio device, before piece Audio 5 wires a
/// real capture device's native callback to write into one of these.
/// </summary>
public class MiniAudioRingTests
{
    [Fact]
    public void WriteThenRead_RoundTripsExactData()
    {
        using var ring = new MiniAudioRing(capacityFrames: 1024, channels: 1);

        var written = new float[100];
        for (var i = 0; i < written.Length; i++)
        {
            written[i] = i * 0.01f;
        }

        var writeCount = ring.Write(written);
        Assert.Equal(written.Length, writeCount);

        var readBack = new float[100];
        var readCount = ring.Read(readBack);

        Assert.Equal(written.Length, readCount);
        Assert.Equal(written, readBack);
    }

    [Fact]
    public void Read_ReturnsZero_WhenRingIsEmpty()
    {
        using var ring = new MiniAudioRing(capacityFrames: 64, channels: 1);

        var destination = new float[16];
        var readCount = ring.Read(destination);

        Assert.Equal(0, readCount);
    }

    [Fact]
    public void Write_ZeroLengthSpan_ReturnsZero_DoesNotThrow()
    {
        // Round-1 functional-audit regression test: Span<T>.GetPinnableReference returns a null
        // ref for a zero-length span, so `fixed` pins NULL -- the native shim's own NULL guard
        // then returned -1, silently violating this method's own documented "0..the input's own
        // frame count" contract. This exact gap survived three prior review passes specifically
        // because no test exercised a zero-length input.
        using var ring = new MiniAudioRing(capacityFrames: 64, channels: 1);

        var writeCount = ring.Write(ReadOnlySpan<float>.Empty);

        Assert.Equal(0, writeCount);
    }

    [Fact]
    public void Read_ZeroLengthDestination_ReturnsZero_DoesNotThrow()
    {
        using var ring = new MiniAudioRing(capacityFrames: 64, channels: 1);
        ring.Write(new float[] { 1f, 2f, 3f });

        var readCount = ring.Read(Span<float>.Empty);

        Assert.Equal(0, readCount);
    }

    [Fact]
    public void Read_ReturnsOnlyWhatWasWritten_WhenLessThanRequested()
    {
        using var ring = new MiniAudioRing(capacityFrames: 64, channels: 1);

        ring.Write(new float[] { 1f, 2f, 3f });

        var destination = new float[10];
        var readCount = ring.Read(destination);

        Assert.Equal(3, readCount);
        Assert.Equal([1f, 2f, 3f], destination[..3]);
    }

    [Fact]
    public void Write_ReturnsPartialCount_WhenRingCannotFitEverything()
    {
        // Small ring, deliberately overfilled -- measured, not assumed, exactly how many frames a
        // ma_pcm_rb-backed ring with this nominal capacity actually accepts (some ring
        // implementations reserve a slot to disambiguate full-vs-empty, so "capacity" and "usable
        // capacity" aren't always identical).
        const int capacityFrames = 16;
        using var ring = new MiniAudioRing(capacityFrames, channels: 1);

        var attemptedWrite = new float[capacityFrames * 2];
        for (var i = 0; i < attemptedWrite.Length; i++)
        {
            attemptedWrite[i] = i;
        }

        var writeCount = ring.Write(attemptedWrite);

        Assert.True(writeCount > 0, "Expected at least some frames to be accepted.");
        Assert.True(writeCount <= capacityFrames, $"Expected at most {capacityFrames} frames to be accepted (ring capacity), got {writeCount}.");

        // Whatever was accepted must read back correctly, in order, from the front.
        var readBack = new float[writeCount];
        var readCount = ring.Read(readBack);
        Assert.Equal(writeCount, readCount);
        Assert.Equal(attemptedWrite[..writeCount], readBack);
    }

    [Fact]
    public void RepeatedSmallWritesAndReads_AcrossManyWraparounds_PreserveOrderAndValues()
    {
        // Ring capacity is deliberately small relative to the total data pushed through it, so
        // this genuinely exercises multiple wraparounds of the underlying buffer, not just a
        // single fill.
        const int capacityFrames = 32;
        const int chunkFrames = 10;
        const int totalChunks = 50; // 500 frames total through a 32-frame ring -- >15 wraparounds

        using var ring = new MiniAudioRing(capacityFrames, channels: 1);

        var nextExpectedValue = 0f;
        for (var chunk = 0; chunk < totalChunks; chunk++)
        {
            var toWrite = new float[chunkFrames];
            for (var i = 0; i < chunkFrames; i++)
            {
                toWrite[i] = chunk * chunkFrames + i;
            }

            var writeCount = ring.Write(toWrite);
            Assert.Equal(chunkFrames, writeCount); // small enough relative to capacity to always fully fit

            var readBack = new float[chunkFrames];
            var readCount = ring.Read(readBack);
            Assert.Equal(chunkFrames, readCount);

            for (var i = 0; i < chunkFrames; i++)
            {
                Assert.Equal(nextExpectedValue, readBack[i]);
                nextExpectedValue += 1f;
            }
        }
    }

    [Fact]
    public void WriteAndRead_AreAllocationFree_AtSteadyState()
    {
        // The whole point of piece Audio 3: MiniAudioRing.Write/Read pin the caller's own Span via
        // `fixed` and pass the raw pointer straight to the native shim, never marshaling a float[]
        // parameter (which would copy/allocate every single call). Measured via
        // GC.GetAllocatedBytesForCurrentThread() deltas, not GC.CollectionCount (too weak to prove
        // zero allocations in a short run -- a few hundred bytes per call may never trigger a
        // Gen0 collection at all).
        using var ring = new MiniAudioRing(capacityFrames: 256, channels: 1);
        var buffer = new float[64];

        // Warm up: let JIT tiering/any one-time initialization happen before measuring, so the
        // measurement reflects steady-state behavior, not first-call overhead.
        for (var i = 0; i < 1000; i++)
        {
            ring.Write(buffer);
            ring.Read(buffer);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            ring.Write(buffer);
            ring.Read(buffer);
        }

        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }

    [Fact]
    public void StereoRing_InterleavedSamplesRoundTripCorrectly()
    {
        using var ring = new MiniAudioRing(capacityFrames: 64, channels: 2);

        // 4 frames, interleaved L/R.
        var written = new float[] { 1f, -1f, 2f, -2f, 3f, -3f, 4f, -4f };
        var writeCount = ring.Write(written);
        Assert.Equal(4, writeCount);

        var readBack = new float[8];
        var readCount = ring.Read(readBack);
        Assert.Equal(4, readCount);
        Assert.Equal(written, readBack);
    }

    // Second-opus-review fix: these four were flagged as new behavior (the partial-frame guard,
    // and the disposed guard/idempotent-Dispose Interlocked fix) added with zero test coverage --
    // both are deterministic and hardware-free, so there was no excuse for that.

    [Fact]
    public void Write_Throws_WhenDataLengthIsNotWholeNumberOfFrames()
    {
        using var ring = new MiniAudioRing(capacityFrames: 64, channels: 2);

        // 3 floats cannot form a whole number of 2-channel frames.
        Assert.Throws<ArgumentException>(() => ring.Write(new float[] { 1f, 2f, 3f }));
    }

    [Fact]
    public void Read_Throws_WhenDestinationLengthIsNotWholeNumberOfFrames()
    {
        using var ring = new MiniAudioRing(capacityFrames: 64, channels: 2);

        Assert.Throws<ArgumentException>(() => ring.Read(new float[3]));
    }

    [Fact]
    public void Write_Throws_AfterDispose()
    {
        var ring = new MiniAudioRing(capacityFrames: 64, channels: 1);
        ring.Dispose();

        Assert.Throws<ObjectDisposedException>(() => ring.Write(new float[4]));
    }

    [Fact]
    public void Read_Throws_AfterDispose()
    {
        var ring = new MiniAudioRing(capacityFrames: 64, channels: 1);
        ring.Dispose();

        Assert.Throws<ObjectDisposedException>(() => ring.Read(new float[4]));
    }

    [Fact]
    public void Dispose_IsIdempotent_WhenCalledTwice()
    {
        var ring = new MiniAudioRing(capacityFrames: 64, channels: 1);

        ring.Dispose();
        var exception = Record.Exception(() => ring.Dispose());

        Assert.Null(exception);
    }

    // Third-opus-review fix: the previous round's "idempotent Dispose" test only exercised
    // Dispose-vs-Dispose, which the Interlocked.Exchange fix already handled -- it did not exercise
    // Dispose racing a concurrent Write/Read on another thread at all (the ReaderWriterLockSlim
    // fix's actual target, and the more reachable of the two races). This hammers both from real
    // concurrent threads across many iterations: the only acceptable outcome on the writer/reader
    // side is either a normal successful call or an ObjectDisposedException -- anything else
    // (a crash, a native-level use-after-free, a hang) fails the test.
    [Fact]
    public void ConcurrentWriteAndDispose_NeverThrowsAnythingOtherThanObjectDisposedException()
    {
        for (var iteration = 0; iteration < 200; iteration++)
        {
            using var ring = new MiniAudioRing(capacityFrames: 256, channels: 1);
            var buffer = new float[16];
            var readBuffer = new float[16];
            using var start = new Barrier(3);

            var writerThread = new Thread(() =>
            {
                start.SignalAndWait();
                try
                {
                    while (true)
                    {
                        ring.Write(buffer);
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Expected once Dispose wins the race -- the only acceptable outcome besides
                    // the loop simply being interrupted by the disposing thread finishing first.
                }
            });

            var readerThread = new Thread(() =>
            {
                start.SignalAndWait();
                try
                {
                    while (true)
                    {
                        ring.Read(readBuffer);
                    }
                }
                catch (ObjectDisposedException)
                {
                }
            });

            writerThread.IsBackground = true;
            readerThread.IsBackground = true;
            writerThread.Start();
            readerThread.Start();

            start.SignalAndWait();
            ring.Dispose();

            Assert.True(writerThread.Join(TimeSpan.FromSeconds(5)), "Writer thread did not observe Dispose within the expected bound.");
            Assert.True(readerThread.Join(TimeSpan.FromSeconds(5)), "Reader thread did not observe Dispose within the expected bound.");
        }
    }

    // Tier A Batch 1 re-audit round 2/3: yoniq_audio_ring_create's own overflow guard
    // (native/yoniq_audio.c) was added with no paired test -- this closes that gap. Round-2 finding,
    // followed exactly: capacityFrames must be a value whose byte product (capacityFrames *
    // channels * sizeof(float)) actually WRAPS mod 2^32 to something small, not just anything large
    // -- int.MaxValue would throw with OR without the guard (0x7FFFFFFF * 4 wraps to 0xFFFFFFFC,
    // still > 0x7FFFFFFF, so ma_rb_init_ex's own downstream guard rejects it regardless), which
    // would make a test using it pass vacuously. 0x40000100 frames * 1 channel * 4 bytes =
    // 0x1_00000400, which wraps to 0x00000400 (1024 bytes -- a silent 256-frame ring) without this
    // guard; WITH it, the 64-bit pre-check (0x40000100 * 4 = 0x1_00000400, comfortably >
    // 0x7FFFFFFF) correctly rejects it before that wrap can ever happen.
    [Fact]
    public void Constructor_CapacityWhoseByteProductWrapsMod2To32_ThrowsInsteadOfSilentlyUndersizing()
    {
        var exception = Record.Exception(() => new MiniAudioRing(capacityFrames: 0x40000100, channels: 1));

        Assert.IsType<InvalidOperationException>(exception);
    }

    // Tier A Batch 1 re-audit round 5 nit: yoniq_audio_ring_create's `<= 0` guard (native/yoniq_audio.c,
    // right above the overflow guard the test above covers) had no test either. Real, if a lower one:
    // a channels <= 0 value that somehow slipped past this guard would reach Write/Read's own
    // `data.Length % _channels` (MiniAudioRing.cs) as a DivideByZeroException, a much less
    // diagnosable failure than a clean constructor-time InvalidOperationException. Code-review
    // correction: zero alone doesn't clearly discriminate the guard for `channels` (0 is plausibly
    // rejected downstream regardless) -- negative is the value where the guard is actually
    // load-bearing (a naive cast, e.g. to `ma_uint32`, would turn `-1` into 4294967295), so both are
    // covered, not just zero despite the original "NonPositive" naming.
    [Fact]
    public void Constructor_ZeroChannels_Throws()
    {
        var exception = Record.Exception(() => new MiniAudioRing(capacityFrames: 64, channels: 0));

        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void Constructor_NegativeChannels_Throws()
    {
        var exception = Record.Exception(() => new MiniAudioRing(capacityFrames: 64, channels: -1));

        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void Constructor_ZeroCapacityFrames_Throws()
    {
        var exception = Record.Exception(() => new MiniAudioRing(capacityFrames: 0, channels: 1));

        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void Constructor_NegativeCapacityFrames_Throws()
    {
        var exception = Record.Exception(() => new MiniAudioRing(capacityFrames: -1, channels: 1));

        Assert.IsType<InvalidOperationException>(exception);
    }
}
