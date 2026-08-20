using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;

namespace ScanlineStudio.Core.Audio.MiniAudio;

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
internal sealed unsafe partial class MiniAudioCaptureSession : IDisposable
{
    private readonly ILogger _logger;

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
    /// <param name="drainThreadPriority">OS scheduling priority for this class's own drain thread.
    /// Null leaves it at the CLR default (<see cref="ThreadPriority.Normal"/>, whatever
    /// <see cref="Thread"/>'s own constructor already gives it -- unchanged from before this
    /// parameter existed). Does NOT affect the real-time native audio callback thread -- that one
    /// runs entirely inside the C shim and is never exposed to managed code, so it has no
    /// managed-settable priority at all; only this drain thread does.</param>
    /// <param name="periodSizeInFrames">Requested native hardware/backend period size (0 =
    /// miniaudio's own default -- unchanged from before this parameter existed). A separate,
    /// lower-level knob from <paramref name="ringCapacityFrames"/>, which only sizes this shim's
    /// own managed-drain-side ring, not the device's actual buffer.</param>
    /// <param name="periods">Requested native period count (0 = miniaudio's own default).</param>
    /// <param name="channelSource"><b>Not a confirmed legacy port</b> (see
    /// <see cref="AudioChannelSource"/>'s own doc comment). <see cref="AudioChannelSource.Mono"/>
    /// (default) reproduces today's exact pre-existing behavior -- the device opens with 1
    /// channel, unchanged. <see cref="AudioChannelSource.Left"/>/<see cref="AudioChannelSource.Right"/>
    /// open the device with 2 channels instead and extract only the named one.</param>
    public MiniAudioCaptureSession(
        string deviceId, int sampleRate, ILogger logger, int ringCapacityFrames = 16384,
        ThreadPriority? drainThreadPriority = null, int periodSizeInFrames = 0, int periods = 0,
        AudioChannelSource channelSource = AudioChannelSource.Mono)
    {
        // Functional-audit fix (Tier A Batch 1 re-audit round 2): validated BEFORE the native
        // device even opens, not just to avoid the leak below -- AudioDeviceSettings.CaptureThreadPriority
        // round-trips through System.Text.Json with no JsonStringEnumConverter registered
        // (AudioSettingsJsonContext.cs), and STJ's default numeric enum (de)serialization does NOT
        // range-validate, so a hand-edited settings file with an out-of-range value (e.g. 42) would
        // otherwise reach `_drainThread.Priority = priority` below as a real, silently-accepted
        // out-of-range ThreadPriority. Failing here, before MiniAudioContext.Acquire()/the native
        // open, is strictly better than the widened catch below: it never opens a device just to
        // immediately close it.
        if (drainThreadPriority is { } requestedPriority && !Enum.IsDefined(requestedPriority))
        {
            throw new ArgumentOutOfRangeException(nameof(drainThreadPriority), requestedPriority, "Not a defined ThreadPriority value.");
        }

        _logger = logger;

        // Opus-review fix: this session never held its own reference to the native context --
        // only MiniAudioDeviceEnumerator did, so a live capture session's continued correctness
        // depended entirely on some unrelated enumerator instance happening to still be
        // undisposed. Acquiring here means the context can never be torn down (and libpulse
        // dlclose'd) while this session's own device is still open.
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
                Channels = channelSource == AudioChannelSource.Mono ? 1 : 2,
                ChannelSelect = (int)channelSource,
            };
            _handle = NativeAudio.yoniq_audio_capture_session_open(deviceIdBytes, ref options);
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

        // Functional-audit fix (Tier A Batch 1 re-audit round 2): this try/catch used to end at the
        // native open above -- but the device is ALREADY LIVE at that point (its real-time callback
        // is running and writing into its ring), and nothing yet drains it. If thread construction
        // or Start() below throws (the drainThreadPriority range case is now pre-validated above,
        // but Thread's own constructor/Start() can still throw OutOfMemoryException), this object
        // never escapes the constructor, so nothing could ever close the still-open device or
        // release this session's own MiniAudioContext reference -- a permanent leak of both,
        // invisible to the caller since MiniAudioEngine.OpenCaptureSession's own catch filter
        // already translates the resulting exception into a clean AudioDeviceUnavailableException.
        // Mirrors Dispose's own bounded-close-thread pattern (CloseTimeout) rather than closing
        // inline here, for the same reason: a P/Invoke close can hang (see CloseTimeout's own doc
        // comment) and this constructor must not risk hanging on that same bug. No drain-thread
        // Join needed here, unlike Dispose -- the drain thread never started.
        try
        {
            _drainThread = new Thread(DrainLoop)
            {
                IsBackground = true,
                Name = "MiniAudioCaptureDrain",
            };
            if (drainThreadPriority is { } priority)
            {
                _drainThread.Priority = priority;
            }

            _drainThread.Start();
        }
        catch
        {
            // Code-review fix: set BEFORE anything else in this catch, purely as a future-code
            // hazard guard -- a no-op today (DrainLoop never started reading _handle on this path,
            // since the try above never reached Start() successfully), but if a future edit ever
            // added a statement after _drainThread.Start() above, this stops DrainLoop's own native
            // read racing the close below from becoming a real use-after-free. DrainLoop already
            // treats _stopping as its own authoritative exit signal (see that method's own comment).
            _stopping = true;

            var handle = _handle;
            var closeThread = new Thread(() => NativeAudio.yoniq_audio_capture_session_close(handle))
            {
                IsBackground = true,
                Name = "MiniAudioCaptureClose",
            };
            closeThread.Start();

            // Only release our context reference on a clean close -- same reasoning as
            // TimedOutDuringClose's own doc comment on Dispose: releasing after a timeout would let
            // a future MiniAudioContext teardown race a close thread that might still be inside the
            // native shim. A timed-out close here leaks the context reference (same accepted
            // tradeoff Dispose already makes), not the whole handle.
            if (closeThread.Join(CloseTimeout))
            {
                MiniAudioContext.Release();
            }
            else
            {
                // Code-review fix: this path used to have zero observability -- TimedOutDuringClose
                // (Dispose's own equivalent signal) is never set here, and the object never escapes
                // the constructor for a caller to read it even if it were. This is the only place
                // that can ever report it.
                Log.CaptureCloseTimedOutDuringConstruction(_logger);
            }

            throw;
        }
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
    /// code, not an external device/server.
    ///
    /// Band-1 fix (pre-Phase-2 audit): each subscriber in the invocation list is invoked and
    /// guarded independently (own try/catch per handler, see <see cref="DrainLoop"/>) -- one
    /// throwing subscriber no longer prevents every other subscriber, or every later chunk, from
    /// being delivered. Real scenario this fixes, not hypothetical: `spec/05-audio-engine.md`
    /// plans a VU-meter subscriber running alongside the DSP decode pipeline on this same stream --
    /// a throwing decoder would previously have silently frozen the level meter too. Each throw is
    /// still swallowed at the handler level (see <see cref="LastSubscriberException"/>/
    /// <see cref="SubscriberExceptionCount"/>), for the same process-survival reason as
    /// before.</summary>
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

    /// <summary>Test-only visibility into the drain thread's actual OS scheduling priority --
    /// production code has no need to read this back, only to set it via the constructor.</summary>
    internal ThreadPriority DrainThreadPriority => _drainThread.Priority;

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

    // Band-1 fix (pre-Phase-2 audit): DrainLoop's per-subscriber catch below used to be silent --
    // deliberately kept (a raw background Thread dying from an unhandled exception kills the whole
    // process) but with zero way for a caller to learn a subscriber ever threw. `volatile` (not a
    // plain auto-property like TimedOutDuringClose): that one is written once, under the write
    // lock, during Dispose, with _drainThread.Join() supplying the happens-before for its single
    // read site -- this is written repeatedly on a live drain thread with readers on arbitrary
    // other threads, so it needs its own visibility guarantee. _subscriberExceptionCount is
    // Interlocked-incremented/Volatile-read for the same reason, mirroring OverrunCount's "raw
    // counter for the caller to interpret" shape.
    private volatile Exception? _lastSubscriberException;
    private int _subscriberExceptionCount;

    /// <summary>The most recent exception thrown by a <see cref="SamplesAvailable"/> subscriber, or
    /// null if none has thrown. This is the enabling half of exception visibility only -- nothing in
    /// this assembly polls it today (only <see cref="MiniAudioEngine.CaptureLastSubscriberException"/>
    /// passes it through); the eventual real production caller wiring this session's output into
    /// decode is what owes the checking half. Readable even after <see cref="Dispose"/> (deliberately
    /// not gated on <c>_disposed</c>/<see cref="ObjectDisposedException"/>, unlike
    /// <see cref="OverrunCount"/> -- this is exactly the state you'd want to inspect right after a
    /// session dies). Note: in the self-dispose path (see <see cref="Dispose"/>'s own comment, no
    /// <c>Join()</c>) a caller reading this immediately after <see cref="Dispose"/> returns can miss
    /// a write from a subscriber that threw after calling Dispose -- stale-by-one, not
    /// corrupted.</summary>
    public Exception? LastSubscriberException => _lastSubscriberException;

    /// <summary>Cumulative count of subscriber exceptions across every <see cref="SamplesAvailable"/>
    /// invocation list (each throwing handler counted independently -- see that event's own doc
    /// comment: one throwing subscriber no longer prevents others in the same list from running).
    /// Mirrors <see cref="OverrunCount"/>'s own shape: a raw counter for the caller to interpret, not
    /// itself a verdict.</summary>
    public int SubscriberExceptionCount => Volatile.Read(ref _subscriberExceptionCount);

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
                // process -- one throwing subscriber must not be able to do that. Still no logger
                // dependency added (same boundary as before -- scope creep beyond what this fix
                // needs), but no longer silent: Band-1 fix records the fault via
                // LastSubscriberException/SubscriberExceptionCount instead of discarding it.
                //
                // Band-1 fix: invoke each subscriber independently (GetInvocationList(), not a
                // single SamplesAvailable?.Invoke(samples)) so one throwing subscriber can't starve
                // every other subscriber -- or every later chunk -- of delivery. See
                // SamplesAvailable's own doc comment for the real multi-subscriber scenario this
                // guards (a VU-meter subscriber alongside the DSP decode pipeline).
                var subscribers = SamplesAvailable;
                if (subscribers is not null)
                {
                    foreach (var handler in subscribers.GetInvocationList())
                    {
                        try
                        {
                            ((Action<ReadOnlyMemory<float>>)handler)(samples);
                        }
                        catch (Exception ex)
                        {
                            _lastSubscriberException = ex;
                            var count = Interlocked.Increment(ref _subscriberExceptionCount);

                            // Hot path (real-time drain thread, docs/logging-guidelines.md) --
                            // logged at the first occurrence, then only a periodic summary, never
                            // per-callback, to avoid turning a logging change into dropped RX audio.
                            if (count == 1)
                            {
                                Log.SubscriberThrew(_logger, ex);
                            }
                            else if (count % 100 == 0)
                            {
                                Log.SubscriberThrewRepeated(_logger, count);
                            }
                        }
                    }
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
        //
        // Round-1 functional-audit note: this write lock is held across the (self-join-guarded)
        // _drainThread.Join() below, which has no timeout -- if any OTHER member that takes the
        // read lock (HasStopped, OverrunCount) were called on a session that's mid-dispose from a
        // thread other than the drain thread itself, that call would block on this write lock for
        // as long as the join takes, and if IT were somehow called from the same logical caller
        // waiting on this Dispose, that's a two-way deadlock. NOT reachable today: the only
        // production caller, MiniAudioEngine.ClaimCaptureSessionLocked, nulls its own
        // `_captureSession` field BEFORE calling Dispose, so CaptureOverrunCount's
        // `_captureSession?.OverrunCount ?? 0` short-circuits to 0 without ever touching this
        // session's lock. That safety is held together by ordering in a DIFFERENT file, not
        // anything in this one -- if a future engine property read a claimed-but-not-yet-disposed
        // session, or a future caller held a direct reference to this session past the point the
        // engine disposes it, this becomes live. Worth a timeout-bounded join here if that ever
        // changes; not fixed now since it isn't reachable yet and CloseTimeout's own bounded-thread
        // pattern below would need to extend to the join too, not just the native close.
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

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Error, Message = "A SamplesAvailable subscriber threw on the capture drain thread")]
        public static partial void SubscriberThrew(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "A SamplesAvailable subscriber has now thrown {Count} times on the capture drain thread")]
        public static partial void SubscriberThrewRepeated(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Capture session's native close (during a failed construction) timed out -- the device may have been unplugged; its MiniAudioContext reference was deliberately not released")]
        public static partial void CaptureCloseTimedOutDuringConstruction(ILogger logger);
    }
}
