namespace Yoniq.Core.Audio.MiniAudio;

/// <summary>
/// Piece Audio 5: the real capture path, as its own standalone, directly-testable unit -- not yet
/// wired into a full <c>IAudioEngine</c> implementation, since that also needs the playback side
/// (piece Audio 6) before it makes sense to compose the two into one class.
///
/// The real-time native callback runs entirely inside the shim (`native/yoniq_audio.c`'s
/// <c>yoniq_audio_capture_session_open</c>), writing into an internal ring buffer; this class owns
/// a normal-priority managed thread that drains that ring and raises <see cref="SamplesAvailable"/>
/// -- matching <c>IAudioEngine.SamplesCaptured</c>'s own documented threading contract (piece
/// Audio 2: fires on a drain thread, never the real-time callback thread) and overrun policy
/// (drop-newest when the ring fills, never block, never corrupt/reorder -- already the ring's own
/// behavior from piece Audio 3, reused here unchanged).
/// </summary>
internal sealed unsafe class MiniAudioCaptureSession : IDisposable
{
    // Read granularity for the drain loop -- not the ring's own capacity, just how much this class
    // asks for per poll.
    private const int DrainBufferFrames = 4096;

    // Piece Audio 8: measured directly in this project's own dev sandbox -- disposing a capture
    // session whose virtual sink had already been unloaded (a real hot-unplug) can hang the
    // closing call indefinitely (observed past 8 minutes with no timeout of its own). Root cause,
    // confirmed by reading the pinned miniaudio.h directly: the PulseAudio backend's blocking
    // ma_wait_for_operation__pulse loops on `pa_operation_get_state(...) != RUNNING` with no
    // timeout of its own, and the server never transitions that operation out of RUNNING once its
    // backing sink is gone. See Dispose's own comment for the mitigation.
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly IntPtr _handle;
    private readonly Thread _drainThread;
    private volatile bool _stopping;
    private bool _disposed;

    /// <param name="deviceId">A capture device id, as returned by
    /// <see cref="MiniAudioDeviceEnumerator"/>.</param>
    /// <param name="sampleRate">Requested mono sample rate -- miniaudio's own data converter
    /// handles resample/downmix from whatever the device's real native format is.</param>
    /// <param name="ringCapacityFrames">Sizes the buffer between the real-time callback and this
    /// class's own drain thread.</param>
    public MiniAudioCaptureSession(string deviceId, int sampleRate, int ringCapacityFrames = 16384)
    {
        var deviceIdBytes = NativeAudio.EncodeFixedString(deviceId, NativeAudio.IdSize);
        _handle = NativeAudio.yoniq_audio_capture_session_open(deviceIdBytes, sampleRate, ringCapacityFrames);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to open capture device '{deviceId}' at {sampleRate}Hz.");
        }

        _drainThread = new Thread(DrainLoop)
        {
            IsBackground = true,
            Name = "MiniAudioCaptureDrain",
        };
        _drainThread.Start();
    }

    /// <summary>Fires on this instance's own normal-priority drain thread -- never the real-time
    /// audio callback thread, which runs entirely inside the native shim and never enters managed
    /// code (see class doc comment).</summary>
    public event Action<ReadOnlyMemory<float>>? SamplesAvailable;

    /// <summary>True if the underlying device's own notification callback reported the stream
    /// stopped since this was last checked (clears the flag on read). Piece Audio 8 verified this
    /// against a real device disappearing mid-capture (a virtual sink unloaded while its monitor
    /// was open) and found this does NOT become true in that case: on the PulseAudio backend, the
    /// "stopped" notification is only ever raised from the stream's suspend callback (a literal
    /// server-side suspend/resume, e.g. `pa_stream_is_suspended`), never from the stream silently
    /// dying because its backing device is gone -- confirmed directly against the pinned
    /// miniaudio.h (`ma_device_on_suspended__pulse` is the only caller of
    /// `ma_device__on_notification_stopped` for this backend). Real hot-unplug on Linux instead
    /// shows up as <see cref="SamplesAvailable"/> simply going quiet forever -- callers needing to
    /// detect that should track time since their last received chunk, not rely on this flag.</summary>
    public bool HasStopped => NativeAudio.yoniq_audio_capture_session_check_and_clear_stopped(_handle) != 0;

    /// <summary>True if the most recent <see cref="Dispose"/> call's native close timed out
    /// (see <see cref="CloseTimeout"/>'s doc comment) rather than completing normally. Exposed so
    /// callers/tests can detect and report this rare condition instead of it silently vanishing --
    /// the close call itself is abandoned on its own background thread when this happens, a
    /// deliberate, accepted leak far preferable to hanging the disposing thread forever.</summary>
    public bool TimedOutDuringClose { get; private set; }

    private void DrainLoop()
    {
        var buffer = new float[DrainBufferFrames];
        while (!_stopping)
        {
            int framesRead;
            fixed (float* ptr = buffer)
            {
                framesRead = NativeAudio.yoniq_audio_capture_session_read(_handle, ptr, buffer.Length);
            }

            if (framesRead > 0)
            {
                SamplesAvailable?.Invoke(buffer.AsMemory(0, framesRead));
            }
            else
            {
                // Nothing available right now -- a short sleep avoids a hot spin loop. SSTV audio
                // has no hard sub-millisecond latency requirement (unlike, say, game audio), so
                // this granularity is a reasonable, deliberate trade-off, not an oversight.
                Thread.Sleep(5);
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _stopping = true;
            _drainThread.Join();

            // Run the native close on its own thread and bound the wait -- see CloseTimeout's doc
            // comment for why this is necessary (a confirmed, reproducible hang otherwise). A P/Invoke
            // call in progress cannot be safely cancelled/aborted once started, so a timeout here
            // means abandoning that thread (and the underlying native handle) rather than actually
            // stopping it; in the overwhelmingly common case (device still present) this thread
            // finishes near-instantly and joins normally.
            var handle = _handle;
            var closeThread = new Thread(() => NativeAudio.yoniq_audio_capture_session_close(handle))
            {
                IsBackground = true,
                Name = "MiniAudioCaptureClose",
            };
            closeThread.Start();
            TimedOutDuringClose = !closeThread.Join(CloseTimeout);

            _disposed = true;
        }
    }
}
