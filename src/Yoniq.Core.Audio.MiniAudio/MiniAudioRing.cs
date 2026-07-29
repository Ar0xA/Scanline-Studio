namespace Yoniq.Core.Audio.MiniAudio;

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

    // Second-opus-review fix: an int, not a bool -- see MiniAudioCaptureSession's identical field
    // and doc comment for why (Interlocked.Exchange makes "check and mark disposed" atomic).
    private int _disposed;

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
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
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

    /// <summary>Reads as many frames into <paramref name="destination"/> as are available; never
    /// blocks. Returns the number of frames actually read (0..the destination's own frame
    /// count), which may be less than requested if the ring doesn't have that much buffered.</summary>
    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
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

    public void Dispose()
    {
        // See _disposed's own doc comment for why this is Interlocked.Exchange, not a plain
        // `if (!_disposed)` check.
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            NativeAudio.yoniq_audio_ring_destroy(_handle);
        }
    }
}
