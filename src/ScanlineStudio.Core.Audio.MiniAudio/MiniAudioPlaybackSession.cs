namespace ScanlineStudio.Core.Audio.MiniAudio;

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

    // Third-opus-review fix: second-opus-review's Interlocked.Exchange on _disposed only made
    // Dispose-vs-Dispose safe -- it did nothing for Dispose racing an in-progress Write/
    // PendingFrames/etc. call on another thread, which could still read _disposed as 0, pass the
    // guard, and then use _handle after Dispose's close thread has already freed it (a genuine
    // use-after-free, not just an exception). A ReaderWriterLockSlim closes both races at once:
    // every public member below takes the read lock around its _disposed check and native call;
    // Dispose takes the write lock, which cannot be granted until every in-flight call has
    // released its read lock, and blocks any new one from starting until the close (bounded by
    // CloseTimeout below) finishes. The cost is a lock acquisition per call instead of a bare field
    // read -- fine here: this managed-code path is explicitly not the hard-real-time one (see the
    // class doc comment's Real-time constraint reasoning), and Write's own natural call cadence
    // (per audio buffer, not per sample) makes the overhead negligible.
    private readonly ReaderWriterLockSlim _lifetimeLock = new(LockRecursionPolicy.NoRecursion);
    private bool _disposed;

    /// <param name="deviceId">A playback device id, as returned by
    /// <see cref="MiniAudioDeviceEnumerator"/>.</param>
    /// <param name="sampleRate">Requested mono sample rate -- miniaudio's own data converter
    /// handles resample/upmix to whatever the device's real native format is.</param>
    /// <param name="ringCapacityFrames">Sizes the buffer between <see cref="Write"/> and the
    /// real-time pull callback.</param>
    /// <param name="periodSizeInFrames">Requested native hardware/backend period size (0 =
    /// miniaudio's own default -- unchanged from before this parameter existed). A separate,
    /// lower-level knob from <paramref name="ringCapacityFrames"/>, which only sizes this shim's
    /// own managed-write-side ring, not the device's actual buffer.</param>
    /// <param name="periods">Requested native period count (0 = miniaudio's own default).</param>
    /// <param name="stereoTx"><b>Not a confirmed legacy port</b> -- stereo-TX-toggle backlog item.
    /// When <see langword="false"/> (default), reproduces today's exact pre-existing behavior (a
    /// mono device open, unchanged). When <see langword="true"/>, opens the device with 2 channels
    /// and duplicates the same mono <see cref="Write"/>n signal to both output channels -- there is
    /// no "which channel" choice on the output side, unlike capture's Left/Right selection.</param>
    public MiniAudioPlaybackSession(
        string deviceId, int sampleRate, int ringCapacityFrames = 16384, int periodSizeInFrames = 0,
        int periods = 0, bool stereoTx = false)
    {
        // See MiniAudioCaptureSession's identical fix and doc comment: this session must hold its
        // own reference to the native context, not rely on some unrelated enumerator instance
        // happening to still be undisposed.
        MiniAudioContext.Acquire();
        try
        {
            var deviceIdBytes = NativeAudio.EncodeFixedString(deviceId, NativeAudio.IdSize);
            var options = new NativeAudio.OpenOptions
            {
                SampleRate = sampleRate,
                RingCapacityFrames = ringCapacityFrames,
                PeriodSizeInFrames = periodSizeInFrames,
                Periods = periods,
                Channels = stereoTx ? 2 : 1,
            };
            _handle = NativeAudio.yoniq_audio_playback_session_open(deviceIdBytes, ref options);
            if (_handle == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Failed to open playback device '{deviceId}' at {sampleRate}Hz.");
            }
        }
        catch
        {
            MiniAudioContext.Release();
            throw;
        }
    }

    /// <summary>Enqueues samples for playback, returning how many were actually accepted
    /// (0..<c>data.Length</c>) -- the direct backing for
    /// <c>IAudioEngine.EnqueuePlaybackSamples</c>'s own "returns accepted count" contract
    /// (piece Audio 2). Never blocks.</summary>
    public unsafe int Write(ReadOnlySpan<float> data)
    {
        _lifetimeLock.EnterReadLock();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            fixed (float* ptr = data)
            {
                return NativeAudio.yoniq_audio_playback_session_write(_handle, ptr, data.Length);
            }
        }
        finally
        {
            _lifetimeLock.ExitReadLock();
        }
    }

    /// <summary>How many already-enqueued frames have not yet actually been played. Zero means
    /// everything handed to <see cref="Write"/> has genuinely played out -- the condition
    /// <c>IAudioEngine.StopPlaybackAsync</c>'s own documented contract (piece Audio 2) requires
    /// before it's safe to stop, since closing early would truncate the tail of a real
    /// transmission.</summary>
    public int PendingFrames
    {
        get
        {
            _lifetimeLock.EnterReadLock();
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return NativeAudio.yoniq_audio_playback_session_pending_frames(_handle);
            }
            finally
            {
                _lifetimeLock.ExitReadLock();
            }
        }
    }

    /// <summary>Cumulative count of times the real-time callback had to pad output with silence
    /// because nothing was buffered. Not itself a verdict of "something went wrong" -- an
    /// underrun after the last real sample has genuinely played is expected and harmless; only the
    /// caller (which knows how many frames it actually enqueued and expected to play) can tell an
    /// expected end-of-transmission underrun apart from an unwanted mid-transmission one.</summary>
    public int UnderrunCount
    {
        get
        {
            _lifetimeLock.EnterReadLock();
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return NativeAudio.yoniq_audio_playback_session_underrun_count(_handle);
            }
            finally
            {
                _lifetimeLock.ExitReadLock();
            }
        }
    }

    /// <summary>True if the underlying device's own notification callback reported the stream
    /// stopped since this was last checked (clears the flag on read). Per the same real finding
    /// documented on <see cref="MiniAudioCaptureSession.HasStopped"/> (piece Audio 8): on the
    /// PulseAudio backend this only fires from an actual server-side suspend/resume, not from the
    /// device disappearing outright -- callers needing to detect the latter should watch
    /// <see cref="UnderrunCount"/> climbing instead.</summary>
    public bool HasStopped
    {
        get
        {
            _lifetimeLock.EnterReadLock();
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return NativeAudio.yoniq_audio_playback_session_check_and_clear_stopped(_handle) != 0;
            }
            finally
            {
                _lifetimeLock.ExitReadLock();
            }
        }
    }

    /// <summary>True if the most recent <see cref="Dispose"/> call's native close timed out
    /// rather than completing normally -- see <see cref="MiniAudioCaptureSession.CloseTimeout"/>'s
    /// doc comment for the full root-cause explanation shared by both session types. When true,
    /// this session's <see cref="MiniAudioContext"/> reference is also deliberately never
    /// released, for the same reason documented on
    /// <see cref="MiniAudioCaptureSession.TimedOutDuringClose"/>.</summary>
    public bool TimedOutDuringClose { get; private set; }

    /// <summary>Blocks (asynchronously) until <see cref="PendingFrames"/> reaches zero or
    /// <paramref name="timeout"/> elapses -- the drain-on-stop mechanism
    /// <c>IAudioEngine.StopPlaybackAsync</c>'s contract requires. A timeout is a real safety net,
    /// not part of the documented contract: something stuck mid-drain forever (e.g. a genuinely
    /// dead device) must not hang the caller indefinitely.</summary>
    public async Task DrainAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        // PendingFrames' own getter re-enters _lifetimeLock and re-checks disposal on every poll,
        // so no separate guard is needed here.
        var deadline = DateTime.UtcNow + timeout;
        while (PendingFrames > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        // Third-opus-review fix: _lifetimeLock is deliberately never disposed -- see
        // MiniAudioRing.Dispose's identical fix and comment for why (a second Dispose call's
        // EnterWriteLock would otherwise throw on the lock object itself, before ever reaching the
        // _disposed idempotency check below, which is exactly the bug a new concurrent stress test
        // caught). A ReaderWriterLockSlim left for the GC to finalize is a harmless, tiny cost.
        _lifetimeLock.EnterWriteLock();
        try
        {
            if (!_disposed)
            {
                _disposed = true;

                // See MiniAudioCaptureSession.Dispose's identical pattern and doc comment: the
                // native close can hang indefinitely if the underlying device disappeared, so it
                // runs on its own thread with a bounded join instead of being awaited directly.
                // Holding the write lock for this entire bounded wait is deliberate: it's what
                // guarantees no reader can be mid-native-call when the close actually frees
                // _handle, at the cost of blocking new callers for up to CloseTimeout in the rare
                // case a close is actually slow.
                var handle = _handle;
                var closeThread = new Thread(() => NativeAudio.yoniq_audio_playback_session_close(handle))
                {
                    IsBackground = true,
                    Name = "MiniAudioPlaybackClose",
                };
                closeThread.Start();
                TimedOutDuringClose = !closeThread.Join(CloseTimeout);

                // Only release our context reference on a clean close -- see TimedOutDuringClose's
                // own doc comment for why releasing after a timeout would be unsafe.
                if (!TimedOutDuringClose)
                {
                    MiniAudioContext.Release();
                }
            }
        }
        finally
        {
            _lifetimeLock.ExitWriteLock();
        }
    }
}
