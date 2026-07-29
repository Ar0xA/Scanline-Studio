namespace Yoniq.Core.Audio.MiniAudio;

/// <summary>
/// Piece Audio 6: the real playback path, as its own standalone, directly-testable unit -- the
/// mirror image of <see cref="MiniAudioCaptureSession"/> (piece Audio 5). The real-time native
/// pull callback runs entirely inside the shim, reading from an internal ring buffer that this
/// class's own <see cref="Write"/> populates; on underrun (nothing buffered when the device wants
/// more) the native side pads with silence rather than emitting garbage.
/// </summary>
internal sealed class MiniAudioPlaybackSession : IDisposable
{
    // Piece Audio 8: the capture session's mirror-image fix. Both sessions close through the same
    // shared native path (miniaudio's PulseAudio ma_device_uninit__pulse), so the same confirmed
    // hang risk applies here too -- see MiniAudioCaptureSession.CloseTimeout's doc comment for the
    // full root-cause explanation (ma_wait_for_operation__pulse's unconditional wait loop).
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly IntPtr _handle;
    private bool _disposed;

    /// <param name="deviceId">A playback device id, as returned by
    /// <see cref="MiniAudioDeviceEnumerator"/>.</param>
    /// <param name="sampleRate">Requested mono sample rate -- miniaudio's own data converter
    /// handles resample/upmix to whatever the device's real native format is.</param>
    /// <param name="ringCapacityFrames">Sizes the buffer between <see cref="Write"/> and the
    /// real-time pull callback.</param>
    public MiniAudioPlaybackSession(string deviceId, int sampleRate, int ringCapacityFrames = 16384)
    {
        var deviceIdBytes = NativeAudio.EncodeFixedString(deviceId, NativeAudio.IdSize);
        _handle = NativeAudio.yoniq_audio_playback_session_open(deviceIdBytes, sampleRate, ringCapacityFrames);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to open playback device '{deviceId}' at {sampleRate}Hz.");
        }
    }

    /// <summary>Enqueues samples for playback, returning how many were actually accepted
    /// (0..<c>data.Length</c>) -- the direct backing for
    /// <c>IAudioEngine.EnqueuePlaybackSamples</c>'s own "returns accepted count" contract
    /// (piece Audio 2). Never blocks.</summary>
    public unsafe int Write(ReadOnlySpan<float> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        fixed (float* ptr = data)
        {
            return NativeAudio.yoniq_audio_playback_session_write(_handle, ptr, data.Length);
        }
    }

    /// <summary>How many already-enqueued frames have not yet actually been played. Zero means
    /// everything handed to <see cref="Write"/> has genuinely played out -- the condition
    /// <c>IAudioEngine.StopPlaybackAsync</c>'s own documented contract (piece Audio 2) requires
    /// before it's safe to stop, since closing early would truncate the tail of a real
    /// transmission.</summary>
    public int PendingFrames => NativeAudio.yoniq_audio_playback_session_pending_frames(_handle);

    /// <summary>Cumulative count of times the real-time callback had to pad output with silence
    /// because nothing was buffered. Not itself a verdict of "something went wrong" -- an
    /// underrun after the last real sample has genuinely played is expected and harmless; only the
    /// caller (which knows how many frames it actually enqueued and expected to play) can tell an
    /// expected end-of-transmission underrun apart from an unwanted mid-transmission one.</summary>
    public int UnderrunCount => NativeAudio.yoniq_audio_playback_session_underrun_count(_handle);

    /// <summary>True if the underlying device's own notification callback reported the stream
    /// stopped since this was last checked (clears the flag on read). Per the same real finding
    /// documented on <see cref="MiniAudioCaptureSession.HasStopped"/> (piece Audio 8): on the
    /// PulseAudio backend this only fires from an actual server-side suspend/resume, not from the
    /// device disappearing outright -- callers needing to detect the latter should watch
    /// <see cref="UnderrunCount"/> climbing instead.</summary>
    public bool HasStopped => NativeAudio.yoniq_audio_playback_session_check_and_clear_stopped(_handle) != 0;

    /// <summary>True if the most recent <see cref="Dispose"/> call's native close timed out
    /// rather than completing normally -- see <see cref="MiniAudioCaptureSession.CloseTimeout"/>'s
    /// doc comment for the full root-cause explanation shared by both session types.</summary>
    public bool TimedOutDuringClose { get; private set; }

    /// <summary>Blocks (asynchronously) until <see cref="PendingFrames"/> reaches zero or
    /// <paramref name="timeout"/> elapses -- the drain-on-stop mechanism
    /// <c>IAudioEngine.StopPlaybackAsync</c>'s contract requires. A timeout is a real safety net,
    /// not part of the documented contract: something stuck mid-drain forever (e.g. a genuinely
    /// dead device) must not hang the caller indefinitely.</summary>
    public async Task DrainAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var deadline = DateTime.UtcNow + timeout;
        while (PendingFrames > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            // See MiniAudioCaptureSession.Dispose's identical pattern and doc comment: the native
            // close can hang indefinitely if the underlying device disappeared, so it runs on its
            // own thread with a bounded join instead of being awaited directly.
            var handle = _handle;
            var closeThread = new Thread(() => NativeAudio.yoniq_audio_playback_session_close(handle))
            {
                IsBackground = true,
                Name = "MiniAudioPlaybackClose",
            };
            closeThread.Start();
            TimedOutDuringClose = !closeThread.Join(CloseTimeout);

            _disposed = true;
        }
    }
}
