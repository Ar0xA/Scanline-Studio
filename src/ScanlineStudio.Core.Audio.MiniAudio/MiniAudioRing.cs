namespace ScanlineStudio.Core.Audio.MiniAudio;

/// <summary>
/// Managed wrapper around the native shim's standalone SPSC ring buffer (`ma_pcm_rb`-backed, see
/// `native/yoniq_audio.c`'s own doc comment on <c>yoniq_audio_ring_create</c> for why this exists
/// independent of any real capture/playback device -- piece Audio 3, before pieces Audio 5/6 wire
/// a real device's native callback to write into one of these).
///
/// <see cref="Write"/>/<see cref="Read"/> are allocation-free: they pin the caller's own
/// <see cref="Span{T}"/> via <c>fixed</c> and pass the raw pointer straight to the native shim,
/// rather than marshaling a <c>float[]</c> parameter (which would copy/allocate on every single
/// call) -- the whole point of this piece.
/// </summary>
internal sealed unsafe class MiniAudioRing : IDisposable
{
    private readonly IntPtr _handle;
    private readonly int _channels;

    // Third-opus-review fix: see MiniAudioPlaybackSession's identical field/fix for the full
    // reasoning -- an Interlocked-guarded int only made Dispose-vs-Dispose safe, not Dispose
    // racing a concurrent Write/Read on another thread (a real use-after-free, not just a missed
    // exception). A ReaderWriterLockSlim closes both. (This class currently has no production
    // caller besides its own tests -- piece Audio 5/6's sessions each keep their own ring
    // entirely inside the native shim -- but fixed here anyway rather than leaving latent
    // undefined behavior in a type explicitly meant to be a reusable, standalone building block.)
    private readonly ReaderWriterLockSlim _lifetimeLock = new(LockRecursionPolicy.NoRecursion);
    private bool _disposed;

    /// <param name="capacityFrames">Ring capacity in frames (not samples) -- each frame is
    /// <paramref name="channels"/> interleaved float samples.</param>
    public MiniAudioRing(int capacityFrames, int channels)
    {
        _channels = channels;
        _handle = NativeAudio.yoniq_audio_ring_create(capacityFrames, channels);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create native ring buffer.");
        }
    }

    /// <summary>Writes as many frames from <paramref name="data"/> as fit; never blocks. Returns
    /// the number of frames actually written (0..the input's own frame count), which may be less
    /// than requested if the ring is full -- the same partial-acceptance contract as
    /// <c>IAudioEngine.EnqueuePlaybackSamples</c> (piece Audio 2).</summary>
    public int Write(ReadOnlySpan<float> data)
    {
        _lifetimeLock.EnterReadLock();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Opus-review fix: a caller passing a span whose length isn't a whole number of frames
            // (e.g. an odd sample count for a stereo ring) previously had the trailing partial frame
            // silently dropped by this integer division, with no signal that anything was wrong.
            if (data.Length % _channels != 0)
            {
                throw new ArgumentException($"Data length {data.Length} is not a whole number of {_channels}-channel frames.", nameof(data));
            }

            var frameCount = data.Length / _channels;
            fixed (float* ptr = data)
            {
                return NativeAudio.yoniq_audio_ring_write(_handle, ptr, frameCount);
            }
        }
        finally
        {
            _lifetimeLock.ExitReadLock();
        }
    }

    /// <summary>Reads as many frames into <paramref name="destination"/> as are available; never
    /// blocks. Returns the number of frames actually read (0..the destination's own frame
    /// count), which may be less than requested if the ring doesn't have that much buffered.</summary>
    public int Read(Span<float> destination)
    {
        _lifetimeLock.EnterReadLock();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (destination.Length % _channels != 0)
            {
                throw new ArgumentException($"Destination length {destination.Length} is not a whole number of {_channels}-channel frames.", nameof(destination));
            }

            var frameCount = destination.Length / _channels;
            fixed (float* ptr = destination)
            {
                return NativeAudio.yoniq_audio_ring_read(_handle, ptr, frameCount);
            }
        }
        finally
        {
            _lifetimeLock.ExitReadLock();
        }
    }

    public void Dispose()
    {
        // Third-opus-review fix (caught by ConcurrentWriteAndDispose_NeverThrowsAnythingOtherThanObjectDisposedException,
        // not just reasoned about): an earlier version of this method disposed _lifetimeLock
        // itself after a successful teardown, which meant a *second* Dispose call's very first
        // statement -- EnterWriteLock() -- threw ObjectDisposedException on the lock object,
        // before ever reaching the _disposed idempotency check below. _lifetimeLock is
        // deliberately never disposed: a ReaderWriterLockSlim left for the GC to finalize is a
        // harmless, tiny cost, and it's what makes Dispose() safely re-enterable any number of
        // times, which IDisposable.Dispose is documented to require.
        _lifetimeLock.EnterWriteLock();
        try
        {
            if (!_disposed)
            {
                _disposed = true;
                NativeAudio.yoniq_audio_ring_destroy(_handle);
            }
        }
        finally
        {
            _lifetimeLock.ExitWriteLock();
        }
    }
}
