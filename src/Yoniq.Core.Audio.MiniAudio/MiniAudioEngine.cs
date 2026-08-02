using Yoniq.Abstractions.Audio;

namespace Yoniq.Core.Audio.MiniAudio;

/// <summary>
/// Composes <see cref="MiniAudioCaptureSession"/>/<see cref="MiniAudioPlaybackSession"/> into a
/// real <see cref="IAudioEngine"/> implementation -- see spec/14-roadmap.md's "Composing
/// MiniAudioCaptureSession/MiniAudioPlaybackSession into a real IAudioEngine" bullet for the full,
/// Opus-verified breakdown this class is built up across (Engine 0-6).
///
/// Public (not `internal`): <see cref="MiniAudioCaptureSession"/>/<see cref="MiniAudioPlaybackSession"/>
/// are `internal`, and `AssemblyInfo.cs`'s `InternalsVisibleTo` only grants this project's own test
/// project -- so this is the only type in this project a DI composition root
/// (`Yoniq.Host/Program.cs`, piece Engine 6) can actually reference.
///
/// Piece Engine 1: capture-only lifecycle. Piece Engine 2: playback lifecycle, including the
/// drain-before-stop contract <see cref="IAudioEngine.StopPlaybackAsync"/> documents.
///
/// Context lifetime: acquires <see cref="MiniAudioContext"/> once for this engine instance's whole
/// lifetime (ctor/<see cref="DisposeAsync"/>), on top of whatever a live
/// <see cref="MiniAudioCaptureSession"/>/<see cref="MiniAudioPlaybackSession"/> acquires on its
/// own -- deliberately, so repeated Start/Stop cycles don't repeatedly tear down and reinitialize
/// the process-wide native context (PulseAudio context init/teardown churn is exactly what pushed
/// this project off PortAudio originally, see spec/05-audio-engine.md's Backend choice section).
///
/// Piece Engine 3: lifecycle-transition correctness under concurrency. One `SemaphoreSlim(1,1)`
/// per lifecycle (capture, playback -- independent, so a slow capture open never blocks a playback
/// stop), chosen over a `ReaderWriterLockSlim` specifically because `Start*Async` needs to `await`
/// (the `Task.Run` wrapping each session's blocking constructor) while holding it, and
/// `ReaderWriterLockSlim` cannot have its lock scope span an `await`. Neither semaphore is ever
/// disposed -- same reasoning as every session/ring type's own never-disposed
/// `ReaderWriterLockSlim` (a second call's `WaitAsync` must not throw on the semaphore object
/// itself before reaching this class's own idempotency checks). <see cref="DisposeAsync"/> never
/// holds both semaphores at once (capture teardown fully released before playback teardown
/// begins), so there is no ABBA case to order against.
///
/// Round-2/3-engine-review history, kept for anyone re-deriving this: round 2 found that
/// `_captureLock`'s wait (in <see cref="ClaimCaptureSessionAsync"/>) was only guaranteed to keep a
/// caller on its own original thread when the lock was uncontended -- when contended, the `await`
/// could resume on a different (pool) thread once the lock became available. If that happened to a
/// <see cref="SamplesCaptured"/> subscriber's own re-entrant self-dispose call (see
/// <see cref="DisposeCaptureSessionAsync"/>'s doc comment for that supported pattern) racing another
/// Start/Stop call for the lock, the session's own
/// <see cref="MiniAudioCaptureSession.IsRunningOnDrainThread"/> check would then run on the wrong
/// (post-hop) thread and dispatch to `Task.Run` instead of inline -- reintroducing the same class of
/// self-join deadlock this whole mechanism exists to avoid, gated behind lock contention (which,
/// per round 3's own tracing, is not as narrow a window as first assumed: `StartCaptureAsync` holds
/// `_captureLock` across a real native device open, milliseconds to hundreds of milliseconds, not
/// just brief field writes). Round 2 documented this as needing a fix inside
/// <see cref="MiniAudioCaptureSession"/> itself (bounding its `_drainThread.Join()`) and deliberately
/// did not apply one, correctly identifying that doing so naively risks a native use-after-free (the
/// drain thread could still be reading from the native handle when a timed-out Join's caller
/// proceeds to close it anyway) -- round 3 confirmed that risk analysis is right, but found the
/// "needs to live in the session class" framing was not: <see cref="ClaimCaptureSessionAsync"/> now
/// fixes this at the engine level instead, by never letting the hop happen in the first place (a
/// caller already on the active session's own drain thread takes a genuinely thread-blocking
/// `SemaphoreSlim.Wait()`, which cannot hop, rather than the async `WaitAsync()` a Start call or
/// another claim could contend against) -- see that method's own doc comment for the full
/// reasoning and why the fix carries no native-lifetime risk.
/// </summary>
public sealed class MiniAudioEngine : IAudioEngine
{
    // Piece Engine 3: guards Start/StopCaptureAsync's multi-step "check nothing started, open,
    // publish" as one atomic critical section, closing the TOCTOU an earlier piece deliberately
    // left open (two concurrent StartCaptureAsync calls could otherwise both pass the check before
    // either published its session). `volatile` on the session fields themselves is still needed
    // independent of this lock -- see EnqueuePlaybackSamples's own doc comment for the one reader
    // that deliberately does not take a lock at all.
    private readonly SemaphoreSlim _captureLock = new(1, 1);
    private readonly SemaphoreSlim _playbackLock = new(1, 1);

    private volatile MiniAudioCaptureSession? _captureSession;

    private int _disposed;

    // Round-1-engine-review fix: signals a second concurrent DisposeAsync caller that teardown has
    // actually finished, rather than letting it return immediately once _disposed is latched (the
    // original behavior let a second caller's `await DisposeAsync()` complete while the first
    // caller's teardown -- native close, MiniAudioContext.Release() -- was still in progress).
    private readonly TaskCompletionSource _disposedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MiniAudioEngine()
    {
        try
        {
            MiniAudioContext.Acquire();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            // Round-1-engine-review fix: this used to catch only InvalidOperationException, but
            // MiniAudioContext.Acquire() calls straight into a P/Invoke (yoniq_audio_context_init)
            // that throws DllNotFoundException/EntryPointNotFoundException if the native shim is
            // missing or mismatched -- the same real failure mode OpenCaptureSession/
            // OpenPlaybackSession already translate, confirmed by reading MiniAudioContext.cs.
            // Without this, a broken Yoniq.Host native-shim copy target (see Engine 6's own commit)
            // would surface as a raw DllNotFoundException from `new MiniAudioEngine()` instead of
            // the typed exception AudioDeviceUnavailableException's own doc comment promises.
            throw new AudioDeviceUnavailableException("Failed to initialize the audio backend.", ex);
        }
    }

    public event Action<ReadOnlyMemory<float>>? SamplesCaptured;

    /// <summary>Piece Engine 5a: diagnostic pass-through to the active capture session's own
    /// <see cref="MiniAudioCaptureSession.OverrunCount"/> (piece Engine 0) -- not part of
    /// <see cref="IAudioEngine"/> itself (no interface change), same pattern as the session types'
    /// own extra diagnostic members (e.g. <c>TimedOutDuringClose</c>) that go beyond what any
    /// interface requires. 0 when capture isn't started, matching "nothing to report" rather than
    /// throwing -- except for the same narrow, documented race
    /// <see cref="EnqueuePlaybackSamples"/> has (round-3-engine-review honesty fix): a concurrent
    /// <see cref="StopCaptureAsync"/> disposing the session between this property's field read and
    /// the underlying native call can still surface the session's own
    /// <see cref="ObjectDisposedException"/>, since this is a diagnostic-only member and
    /// deliberately does not take <see cref="_captureLock"/> either.</summary>
    public int CaptureOverrunCount => _captureSession?.OverrunCount ?? 0;

    /// <summary>Piece Band-1 (pre-Phase-2 audit): diagnostic pass-through to the active capture
    /// session's own <see cref="MiniAudioCaptureSession.LastSubscriberException"/> -- same shape
    /// and same race caveat as <see cref="CaptureOverrunCount"/>'s own doc comment. Null when
    /// capture isn't started or no subscriber has ever thrown.</summary>
    public Exception? CaptureLastSubscriberException => _captureSession?.LastSubscriberException;

    /// <summary>Piece Band-1 (pre-Phase-2 audit): diagnostic pass-through to the active capture
    /// session's own <see cref="MiniAudioCaptureSession.SubscriberExceptionCount"/>. See
    /// <see cref="CaptureOverrunCount"/>'s own doc comment for the same reasoning.</summary>
    public int CaptureSubscriberExceptionCount => _captureSession?.SubscriberExceptionCount ?? 0;

    /// <summary>Piece Engine 5a: diagnostic pass-through to the active playback session's own
    /// <see cref="MiniAudioPlaybackSession.UnderrunCount"/>. See <see cref="CaptureOverrunCount"/>'s
    /// own doc comment for the same reasoning.</summary>
    public int PlaybackUnderrunCount => _playbackSession?.UnderrunCount ?? 0;

    public async Task StartCaptureAsync(AudioDeviceInfo device, int sampleRate, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // ct only prevents this call from starting/proceeding while waiting for the lock -- once
        // the native open below is actually running, there is no way to cancel it partway through
        // (mirrors MiniAudioDeviceEnumerator.RefreshAsync's own documented ct semantics).
        await _captureLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (_captureSession is not null)
            {
                throw new InvalidOperationException("Capture is already started -- call StopCaptureAsync first.");
            }

            // MiniAudioCaptureSession's constructor blocks synchronously (opens the native device,
            // starts a managed drain thread) -- confirmed by reading it, not assumed. Task.Run
            // keeps that off the caller's thread, matching CLAUDE.md's "all hardware communication
            // must be asynchronous" rule.
            var session = await Task.Run(() => OpenCaptureSession(device, sampleRate), ct).ConfigureAwait(false);
            session.SamplesAvailable += OnCaptureSamplesAvailable;
            _captureSession = session;
        }
        finally
        {
            _captureLock.Release();
        }
    }

    public async Task StopCaptureAsync()
    {
        var session = await ClaimCaptureSessionAsync().ConfigureAwait(false);
        if (session is not null)
        {
            await DisposeCaptureSessionAsync(session).ConfigureAwait(false);
        }
    }

    /// <summary>Atomically claims (and un-publishes) the active capture session, if any, holding
    /// <see cref="_captureLock"/> only for that -- never across the disposal itself. Round-1-
    /// engine-review fix: the previous version held the lock for the ENTIRE disposal, including
    /// <see cref="DisposeCaptureSessionAsync"/>'s <c>Task.Run(session.Dispose)</c> branch, which
    /// blocks on the session's own unbounded <c>_drainThread.Join()</c>. If a second caller (e.g.
    /// this session's own drain thread, calling back in from inside <see cref="SamplesCaptured"/>)
    /// was waiting on the same lock at that moment, it would never get to run and therefore never
    /// let <c>DrainLoop</c> exit -- deadlocking the thread the Join is waiting for. Releasing the
    /// lock as soon as the session reference is claimed means a second concurrent caller (drain
    /// thread or otherwise) always finds <see cref="_captureSession"/> already null and returns
    /// immediately as a no-op, instead of ever contending with an in-progress disposal.</summary>
    private async Task<MiniAudioCaptureSession?> ClaimCaptureSessionAsync()
    {
        // Round-3-engine-review fix: closes the residual race this class's own doc comment used to
        // describe as "not safely fixable without touching MiniAudioCaptureSession" -- that framing
        // was wrong. The actual problem was letting a drain-thread-originated claim potentially hop
        // onto a different physical thread via a contended WaitAsync; the fix is to never let that
        // hop happen in the first place, not to detect/recover from one afterward (which
        // DisposeCaptureSessionAsync's own per-session check cannot do -- it only ever sees
        // whichever thread ends up calling it). When the caller IS the active session's own drain
        // thread, this uses a genuinely thread-blocking SemaphoreSlim.Wait() instead of
        // WaitAsync() -- Wait() never hops threads under any circumstance, unlike a contended
        // WaitAsync's continuation, which can resume on a pool thread once the lock frees up. The
        // only holders of _captureLock a drain thread could ever contend against are
        // StartCaptureAsync (opening a NEW, unrelated session -- never waits on this drain thread)
        // and other claim calls (brief field writes) -- neither can deadlock against a bounded
        // synchronous wait here. If the speculative check below is stale by the time the lock is
        // actually acquired (the field changed to a different session, or null, between the check
        // and the wait), that's harmless: DisposeCaptureSessionAsync re-evaluates
        // IsRunningOnDrainThread fresh against whatever session was ACTUALLY claimed, so
        // correctness never depends on this check being atomic with the claim -- it only ever picks
        // which wait strategy to use.
        if (_captureSession?.IsRunningOnDrainThread == true)
        {
            _captureLock.Wait();
            try
            {
                return ClaimCaptureSessionLocked();
            }
            finally
            {
                _captureLock.Release();
            }
        }

        await _captureLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return ClaimCaptureSessionLocked();
        }
        finally
        {
            _captureLock.Release();
        }
    }

    /// <summary>Must be called with <see cref="_captureLock"/> already held.</summary>
    private MiniAudioCaptureSession? ClaimCaptureSessionLocked()
    {
        var session = _captureSession;
        _captureSession = null;

        // Round-2-engine-review fix: unsubscribe as soon as a session is claimed, not only
        // once Dispose() actually finishes closing it. Without this, a claimed-but-not-yet-
        // disposed S1 (its native close can take up to CloseTimeout, run on a background
        // thread) could still be forwarding SamplesAvailable while a StartCaptureAsync racing
        // in right behind this claim opens S2 -- interleaving two devices' audio on one
        // SamplesCaptured event with no marker between them, silent stream corruption for
        // whatever's downstream (the SSTV decoder). Safe to call concurrently with an
        // in-flight SamplesAvailable invocation: C# multicast delegate invocation captures its
        // own snapshot of the list, so `-=` here cannot affect a call already in progress, only
        // ones that haven't started yet.
        if (session is not null)
        {
            session.SamplesAvailable -= OnCaptureSamplesAvailable;
        }

        return session;
    }

    /// <summary>Fires on <see cref="MiniAudioCaptureSession"/>'s own drain thread -- see
    /// <see cref="IAudioEngine.SamplesCaptured"/>'s documented threading contract, which this
    /// forwarder preserves by construction (it never marshals to another thread). The
    /// <see cref="ReadOnlyMemory{T}"/> is passed through unmodified, preserving that same
    /// contract's "fresh, independently-owned array" guarantee. Exceptions thrown by subscribers
    /// are swallowed by the session's own drain loop (documented there); this forwarder inherits
    /// that, a deliberate, pre-existing deviation from "never silent failure" this class does not
    /// attempt to fix.</summary>
    private void OnCaptureSamplesAvailable(ReadOnlyMemory<float> samples) => SamplesCaptured?.Invoke(samples);

    /// <summary>Disposes a claimed capture session -- inline, on the calling thread, if and only if
    /// that thread is THIS session's own drain thread (<see cref="MiniAudioCaptureSession.IsRunningOnDrainThread"/>,
    /// round-1-engine-review fix: previously tracked via a single engine-wide "which thread is
    /// currently forwarding" field, which could not tell one session's drain thread apart from
    /// another's if a subscriber stopped one session and started a new one from within the same
    /// callback invocation -- fixed by asking the session itself). Dispatches to a pool thread via
    /// <c>Task.Run</c> otherwise, matching every other session-closing call in this class.</summary>
    private static async Task DisposeCaptureSessionAsync(MiniAudioCaptureSession session)
    {
        if (session.IsRunningOnDrainThread)
        {
            session.Dispose();
        }
        else
        {
            await Task.Run(session.Dispose).ConfigureAwait(false);
        }
    }

    private static MiniAudioCaptureSession OpenCaptureSession(AudioDeviceInfo device, int sampleRate)
    {
        try
        {
            return new MiniAudioCaptureSession(device.Id, sampleRate);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            // Every real failure mode MiniAudioCaptureSession's constructor can throw, confirmed by
            // reading it: InvalidOperationException (native open failure, or MiniAudioContext
            // context-init failure), ArgumentException (device id too long for the native shim's
            // fixed buffer -- NativeAudio.EncodeFixedString), DllNotFoundException/
            // EntryPointNotFoundException (native shim missing/mismatched). Anything outside this
            // set is left untranslated -- deliberately not treated as "device unavailable" when it
            // might be a genuine, unrelated bug.
            throw new AudioDeviceUnavailableException($"Failed to open capture device '{device.Id}' at {sampleRate}Hz.", ex);
        }
    }

    // Piece Engine 2. Bound on StopPlaybackAsync's DrainAsync poll -- MiniAudioPlaybackSession's
    // own default ring capacity is 16384 frames (~0.37s at 44100Hz), so a normal drain finishes in
    // a small fraction of a second; this is a safety net against a genuinely stuck device, not the
    // expected case.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    // Piece Engine 2, found by this project's Opus plan-review pass and confirmed by reading
    // yoniq_audio_playback_session_pending_frames' native body (a one-line
    // yoniq_audio_ring_available_read): PendingFrames reaching 0 only means the shim's own ring is
    // empty, NOT that miniaudio's device buffer or the PulseAudio server's own output queue have
    // actually finished playing -- closing the session right at that point can truncate the very
    // tail of a real transmission, exactly what IAudioEngine.StopPlaybackAsync's contract exists to
    // prevent. This is an empirical safety margin, not a precisely derived value (no shim API
    // currently exposes real device/server latency) -- verified against a real virtual sink/monitor
    // by MiniAudioEngineTests, not trusted blind.
    private static readonly TimeSpan DrainTailMargin = TimeSpan.FromMilliseconds(200);

    private volatile MiniAudioPlaybackSession? _playbackSession;

    public async Task StartPlaybackAsync(AudioDeviceInfo device, int sampleRate, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _playbackLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (_playbackSession is not null)
            {
                throw new InvalidOperationException("Playback is already started -- call StopPlaybackAsync first.");
            }

            // MiniAudioPlaybackSession's constructor blocks synchronously (opens the native
            // device), confirmed by reading it -- same reasoning as StartCaptureAsync's Task.Run.
            var session = await Task.Run(() => OpenPlaybackSession(device, sampleRate), ct).ConfigureAwait(false);
            _playbackSession = session;
        }
        finally
        {
            _playbackLock.Release();
        }
    }

    /// <summary>Enqueues samples for playback -- the direct backing for
    /// <see cref="IAudioEngine.EnqueuePlaybackSamples"/>. Throws <see cref="InvalidOperationException"/>
    /// if playback was never started (returning 0 would make a caller's contract-compliant
    /// partial-acceptance retry loop spin forever, per <see cref="IAudioEngine.EnqueuePlaybackSamples"/>'s
    /// own doc comment). This is a synchronous interface member -- it cannot take an async lock, so
    /// unlike Start/Stop it is deliberately NOT covered by piece Engine 3's `_playbackLock`: a
    /// concurrent <see cref="StopPlaybackAsync"/> racing this call can still surface the underlying
    /// session's own <see cref="ObjectDisposedException"/> if it wins the race, left untranslated
    /// here (a genuine, narrow, documented gap -- not silently swallowed). Reads the `volatile`
    /// <see cref="_playbackSession"/> field directly, which is sufficient for this method's own
    /// correctness (a torn read is impossible for a reference-type field, and `volatile` guarantees
    /// this thread observes the most recent publish from <see cref="StartPlaybackAsync"/>).</summary>
    public int EnqueuePlaybackSamples(ReadOnlyMemory<float> samples)
    {
        // Round-1-engine-review fix: this had no disposed check at all -- after DisposeAsync,
        // _playbackSession is null, so this used to throw InvalidOperationException telling the
        // caller to call StartPlaybackAsync, which would then throw ObjectDisposedException.
        // Checking here directly gives a caller the real reason immediately, matching every other
        // public member's post-dispose behavior.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var session = _playbackSession;
        if (session is null)
        {
            throw new InvalidOperationException("Playback is not started -- call StartPlaybackAsync first.");
        }

        return session.Write(samples.Span);
    }

    public async Task StopPlaybackAsync()
    {
        var session = await ClaimPlaybackSessionAsync().ConfigureAwait(false);
        if (session is not null)
        {
            await DrainAndDisposePlaybackSessionAsync(session).ConfigureAwait(false);
        }
    }

    /// <summary>Atomically claims (and un-publishes) the active playback session, if any -- see
    /// <see cref="ClaimCaptureSessionAsync"/>'s own doc comment for why this class holds each
    /// lifecycle's lock only for the claim step, never across the disposal/drain itself.</summary>
    private async Task<MiniAudioPlaybackSession?> ClaimPlaybackSessionAsync()
    {
        await _playbackLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var session = _playbackSession;
            _playbackSession = null;
            return session;
        }
        finally
        {
            _playbackLock.Release();
        }
    }

    /// <summary>Drains, then disposes, a playback session -- shared by <see cref="StopPlaybackAsync"/>
    /// (an explicit caller request, which must surface a failed drain rather than hide it) and
    /// <see cref="DisposeAsync"/> (best-effort cleanup, which swallows the same condition -- see its
    /// own comment for why).</summary>
    private static async Task DrainAndDisposePlaybackSessionAsync(MiniAudioPlaybackSession session)
    {
        // DrainAsync itself is already a non-blocking async poll loop (PendingFrames reads +
        // Task.Delay(10)) -- no Task.Run needed here, unlike the session's own constructor/Dispose.
        await session.DrainAsync(DrainTimeout).ConfigureAwait(false);

        if (session.PendingFrames != 0)
        {
            // DrainAsync's own timeout is silent (it just returns once the deadline passes,
            // whether or not the ring actually emptied) -- re-checking PendingFrames here and
            // throwing rather than silently closing is piece Engine 2's fix for that (found by the
            // Opus plan-review pass): a genuinely stuck device must not result in a silently
            // truncated transmission, per spec/01-architecture.md's Error Handling rule.
            await Task.Run(session.Dispose).ConfigureAwait(false);
            throw new AudioDeviceUnavailableException(
                $"Playback did not drain within {DrainTimeout.TotalSeconds}s -- the device may have stopped responding.");
        }

        // See DrainTailMargin's own doc comment: PendingFrames==0 does not mean "fully played out"
        // for the device/server buffers this shim has no visibility into.
        await Task.Delay(DrainTailMargin).ConfigureAwait(false);

        await Task.Run(session.Dispose).ConfigureAwait(false);
    }

    private static MiniAudioPlaybackSession OpenPlaybackSession(AudioDeviceInfo device, int sampleRate)
    {
        try
        {
            return new MiniAudioPlaybackSession(device.Id, sampleRate);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            // Same real failure-mode set as OpenCaptureSession -- see its own doc comment.
            throw new AudioDeviceUnavailableException($"Failed to open playback device '{device.Id}' at {sampleRate}Hz.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            // Round-1-engine-review fix: this used to return immediately here, before the first
            // caller's teardown (native close, MiniAudioContext.Release()) had necessarily
            // finished -- a second concurrent `await engine.DisposeAsync()` could complete while
            // the device was still open. Awaiting the completion signal instead matches the
            // session types' own "Dispose blocks a second caller until the first is done"
            // convention (there via a write lock; here via a TaskCompletionSource, since disposal
            // itself must never be blocked by a Start/Stop racing in ahead of it the way a shared
            // lock would force).
            await _disposedSignal.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            // Round-2-engine-review fix: this used to be one try/finally spanning both teardown
            // steps -- if DisposeCaptureSessionAsync threw, the entire block (including the
            // playback teardown below) was skipped, permanently leaking the playback session's
            // native handle and its own MiniAudioContext reference. Each lifecycle now gets its own
            // try/finally so a failure in one never prevents the other from being attempted.
            try
            {
                var captureSession = await ClaimCaptureSessionAsync().ConfigureAwait(false);
                if (captureSession is not null)
                {
                    await DisposeCaptureSessionAsync(captureSession).ConfigureAwait(false);
                }
            }
            finally
            {
                var playbackSession = await ClaimPlaybackSessionAsync().ConfigureAwait(false);
                if (playbackSession is not null)
                {
                    try
                    {
                        await DrainAndDisposePlaybackSessionAsync(playbackSession).ConfigureAwait(false);
                    }
                    catch (AudioDeviceUnavailableException)
                    {
                        // Best-effort cleanup, unlike StopPlaybackAsync's own explicit-caller-request
                        // path (which surfaces this) -- DrainAndDisposePlaybackSessionAsync already
                        // disposed the session on this path before throwing, so there is nothing left
                        // to clean up here. A caller that cares whether playback actually finished
                        // draining should call StopPlaybackAsync explicitly before disposing, not rely
                        // on DisposeAsync for that.
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Round-3-engine-review fix: without this, a faulted teardown was invisible to a second
            // concurrent DisposeAsync caller -- the finally block below always called
            // _disposedSignal.TrySetResult() (unconditional success) regardless of whether the try
            // block above actually threw, so caller 1 would observe the real exception while caller
            // 2's `await _disposedSignal.Task` (see the branch at the top of this method) completed
            // as if nothing went wrong. The same silent-failure shape this project's own Program.cs
            // Exit-handler fix (round 2) closed for the WhenAny case. TrySetException here, then
            // rethrow for this (the first) caller; TrySetResult in the finally below becomes a
            // harmless no-op once the signal is already resolved.
            _disposedSignal.TrySetException(ex);
            throw;
        }
        finally
        {
            // Round-2-engine-review fix: MiniAudioContext.Release() and _disposedSignal.TrySetResult()
            // used to be two statements in a row with no synchronization between them -- if Release()
            // itself threw (it does, on a refcount imbalance -- exactly the failure mode
            // Yoniq.Host/Program.cs's own Exit handler comment names as a real possibility), the
            // signal was never set, and any second concurrent DisposeAsync caller waiting on it
            // (see the branch above) would hang forever -- the exact class of bug this signal
            // exists to prevent. The signal must fire regardless of whether Release() itself
            // succeeds.
            try
            {
                MiniAudioContext.Release();
            }
            finally
            {
                _disposedSignal.TrySetResult();
            }
        }
    }
}
