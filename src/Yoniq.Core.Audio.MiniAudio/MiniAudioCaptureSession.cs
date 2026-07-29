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

    // Third-opus-review fix: second-opus-review's Interlocked.Exchange on _disposed only made
    // Dispose-vs-Dispose safe, not Dispose racing a concurrent HasStopped call on another thread --
    // see MiniAudioPlaybackSession's identical field/fix for the full reasoning (same class of bug,
    // same fix: a ReaderWriterLockSlim around every externally-callable member, with Dispose as
    // the writer). DrainLoop's OWN native read call is deliberately NOT put behind this lock --
    // see DrainLoop's own comment for why that's still safe and why adding it there would risk
    // reintroducing a self-deadlock in the self-dispose path.
    private readonly ReaderWriterLockSlim _lifetimeLock = new(LockRecursionPolicy.NoRecursion);
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
    /// asynchronously.
    ///
    /// Slow-subscriber behavior (CLAUDE.md's concurrency/scheduler rule): a handler that blocks
    /// blocks this drain thread, which back-pressures into the native ring -- the real-time
    /// callback feeding it drops the newest incoming frames once full (never blocks, never
    /// corrupts/reorders, see the class doc comment's overrun policy), so RX samples are lost, not
    /// corrupted. A handler that blocks indefinitely also blocks <see cref="Dispose"/>'s
    /// <c>_drainThread.Join()</c> indefinitely -- unlike every native call this class makes, that
    /// join has no timeout, because the only thing that can make it hang is managed subscriber
    /// code, not an external device/server.</summary>
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
            _lifetimeLock.EnterReadLock();
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return NativeAudio.yoniq_audio_capture_session_check_and_clear_stopped(_handle) != 0;
            }
            finally
            {
                _lifetimeLock.ExitReadLock();
            }
        }
    }

    /// <summary>Round-1-engine-review fix: whether the calling thread is this session's own drain
    /// thread. Exposed so <see cref="MiniAudioEngine"/> can decide whether to dispose this specific
    /// session inline (matching its self-join guard, see <see cref="Dispose"/>'s own comment)
    /// without relying on an engine-wide "which thread is currently forwarding" field, which cannot
    /// tell one session's drain thread apart from another's across a stop-then-restart within the
    /// same callback invocation. No lock needed -- <c>_drainThread</c> is assigned once in the
    /// constructor and never reassigned.</summary>
    internal bool IsRunningOnDrainThread => Thread.CurrentThread == _drainThread;

    /// <summary>Piece Engine 0: cumulative count of real-time callbacks in which the ring could not
    /// hold everything captured (the managed drain side fell behind, and the newest incoming
    /// frames were dropped -- see the class doc comment's overrun policy). Mirrors
    /// <see cref="MiniAudioPlaybackSession.UnderrunCount"/>'s own shape: a raw counter for the
    /// caller to interpret, not itself a verdict.</summary>
    public int OverrunCount
    {
        get
        {
            _lifetimeLock.EnterReadLock();
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return NativeAudio.yoniq_audio_capture_session_overrun_count(_handle);
            }
            finally
            {
                _lifetimeLock.ExitReadLock();
            }
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
        // Third-opus-review note: this native read call is deliberately NOT guarded by
        // _lifetimeLock. It doesn't need to be -- the normal-case ordering (Dispose sets
        // _stopping, then Join()s this very thread before ever touching the native handle again)
        // already guarantees this call and the close can never run concurrently, with or without
        // the lock. Adding the lock here would only introduce risk: in the self-dispose path (see
        // Dispose's own comment), Dispose runs ON this thread, from inside the SamplesAvailable
        // invocation below -- if this read call held the read lock across that invocation, Dispose
        // trying to take the write lock from the same thread would self-deadlock (ReaderWriterLockSlim
        // has no support for a thread upgrading its own read lock to a write lock). Keeping the
        // lock scoped to *only* the externally-callable members (HasStopped, Dispose) avoids that
        // entirely, since this loop already releases any interest in _handle before ever invoking
        // a subscriber.
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

                // Second-opus-review fix: an unhandled exception on this thread (a plain
                // background Thread, not a thread-pool work item) would terminate the whole
                // process -- one throwing subscriber must not be able to do that. Swallowed
                // deliberately: this class has no logger of its own to report through, and adding
                // one now would be scope creep beyond what this fix needs; subscribers are
                // expected not to throw, this is a last-resort backstop, not a reporting channel.
                try
                {
                    SamplesAvailable?.Invoke(samples);
                }
                catch
                {
                }

                // Second-opus-review fix: defends the invariant Dispose's self-join guard relies
                // on -- nothing in this loop may touch _handle after SamplesAvailable fires,
                // because a subscriber is allowed to call Dispose() synchronously from inside that
                // callback (see Dispose's own comment). Returning immediately if _stopping was set
                // during the callback (rather than falling through to the `while` condition after
                // more loop-body code) keeps that true even if code is added below in the future.
                if (_stopping)
                {
                    return;
                }
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
        // Third-opus-review fix: _lifetimeLock is deliberately never disposed -- see
        // MiniAudioRing.Dispose's identical fix and comment for why (a second Dispose call's
        // EnterWriteLock would otherwise throw on the lock object itself, before ever reaching the
        // _disposed idempotency check below, which is exactly the bug a new concurrent stress test
        // caught in the sibling classes). A ReaderWriterLockSlim left for the GC to finalize is a
        // harmless, tiny cost.
        _lifetimeLock.EnterWriteLock();
        try
        {
            if (!_disposed)
            {
                _disposed = true;
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
                // moment this call (itself running from inside a SamplesAvailable invocation, and
                // therefore not itself holding _lifetimeLock -- see DrainLoop's own comment) returns.
                if (Thread.CurrentThread != _drainThread)
                {
                    _drainThread.Join();
                }

                // Run the native close on its own thread and bound the wait -- see CloseTimeout's doc
                // comment for why this is necessary (a confirmed, reproducible hang otherwise). A P/Invoke
                // call in progress cannot be safely cancelled/aborted once started, so a timeout here
                // means abandoning that thread (and the underlying native handle) rather than actually
                // stopping it; in the overwhelmingly common case (device still present) this thread
                // finishes near-instantly and joins normally. Held under the write lock the whole
                // time -- see _lifetimeLock's own doc comment for why.
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
            }
        }
        finally
        {
            _lifetimeLock.ExitWriteLock();
        }
    }
}
