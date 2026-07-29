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
        // Opus-review fix: this session never held its own reference to the native context --
        // only MiniAudioDeviceEnumerator did, so a live capture session's continued correctness
        // depended entirely on some unrelated enumerator instance happening to still be
        // undisposed. Acquiring here means the context can never be torn down (and libpulse
        // dlclose'd) while this session's own device is still open.
        MiniAudioContext.Acquire();
        try
        {
            var deviceIdBytes = NativeAudio.EncodeFixedString(deviceId, NativeAudio.IdSize);
            _handle = NativeAudio.yoniq_audio_capture_session_open(deviceIdBytes, sampleRate, ringCapacityFrames);
            if (_handle == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Failed to open capture device '{deviceId}' at {sampleRate}Hz.");
            }
        }
        catch
        {
            MiniAudioContext.Release();
            throw;
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
    /// code (see class doc comment). The memory handed to each invocation is a fresh,
    /// independently-owned array (opus-review fix: previously a view into a scratch buffer this
    /// class overwrites on its very next drain iteration, so a subscriber that didn't copy
    /// synchronously could observe silently-corrupted data) -- safe to store or process
    /// asynchronously.</summary>
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
    public bool HasStopped
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return NativeAudio.yoniq_audio_capture_session_check_and_clear_stopped(_handle) != 0;
        }
    }

    /// <summary>True if the most recent <see cref="Dispose"/> call's native close timed out
    /// (see <see cref="CloseTimeout"/>'s doc comment) rather than completing normally. Exposed so
    /// callers/tests can detect and report this rare condition instead of it silently vanishing --
    /// the close call itself is abandoned on its own background thread when this happens, a
    /// deliberate, accepted leak far preferable to hanging the disposing thread forever. When this
    /// is true, this session's <see cref="MiniAudioContext"/> reference is *also* deliberately
    /// never released (opus-review fix): the abandoned thread may still be inside a blocking
    /// native call that dereferences the shared context, so releasing here could let the context
    /// be torn down (and libpulse dlclose'd) out from under that still-running thread -- a second,
    /// smaller accepted leak alongside the abandoned thread itself.</summary>
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
                // A fresh copy, not a view into the reused scratch buffer -- see SamplesAvailable's
                // own doc comment for why. This allocates once per drain iteration, which is fine
                // here: this thread has no hard real-time constraint (only the native callback
                // feeding the ring does, see the class doc comment's Real-time constraint
                // reasoning).
                var samples = new float[framesRead];
                Array.Copy(buffer, samples, framesRead);
                SamplesAvailable?.Invoke(samples);
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

            // Self-join guard, found and fixed during the opus-review fix pass: a subscriber to
            // SamplesAvailable that synchronously completes a TaskCompletionSource without
            // TaskCreationOptions.RunContinuationsAsynchronously can end up running its own
            // continuation -- and anything awaited after it, including a call back into this
            // Dispose -- synchronously on THIS drain thread (this is TaskCompletionSource's own
            // documented default behavior, not a bug in the TCS itself). Joining ourselves from
            // our own thread blocks forever with no timeout at all, worse than anything
            // CloseTimeout guards against below (confirmed reproducible: observed as an
            // unresponsive test process that only SIGKILL could stop). _stopping is already set
            // above, so skipping the join here is safe -- DrainLoop will exit on its own the
            // moment this call (itself running from inside a SamplesAvailable invocation) returns.
            if (Thread.CurrentThread != _drainThread)
            {
                _drainThread.Join();
            }

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

            // Only release our context reference on a clean close -- see TimedOutDuringClose's own
            // doc comment for why releasing after a timeout would be unsafe.
            if (!TimedOutDuringClose)
            {
                MiniAudioContext.Release();
            }

            _disposed = true;
        }
    }
}
