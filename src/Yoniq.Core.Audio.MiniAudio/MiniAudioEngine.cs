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

    // Piece Engine 1: records which managed thread is currently running the capture forwarder
    // (MiniAudioCaptureSession's own drain thread) below, so StopCaptureAsync/DisposeAsync can
    // detect being called re-entrantly from inside a SamplesCaptured subscriber and dispose the
    // session INLINE on that same thread instead of via Task.Run.
    //
    // Why this matters: MiniAudioCaptureSession.Dispose() has an unbounded _drainThread.Join()
    // that only skips when Dispose() itself runs ON the drain thread (confirmed by reading
    // MiniAudioCaptureSession.cs -- dispose-called-from-inside-SamplesAvailable is an explicitly
    // supported, tested pattern at the session level). If StopCaptureAsync always offloaded
    // Dispose() to a pool thread via Task.Run, a subscriber that calls
    // `await engine.StopCaptureAsync()` from inside its own SamplesCaptured handler would deadlock
    // forever: the drain thread (blocked in the subscriber, awaiting the pool-thread task) would
    // never return to let DrainLoop exit, so the pool thread's Join() would never complete either.
    // Confirmed via this project's Opus plan-review pass, not assumed.
    private int _captureForwarderThreadId;

    private int _disposed;

    public MiniAudioEngine()
    {
        try
        {
            MiniAudioContext.Acquire();
        }
        catch (InvalidOperationException ex)
        {
            throw new AudioDeviceUnavailableException("Failed to initialize the audio backend.", ex);
        }
    }

    public event Action<ReadOnlyMemory<float>>? SamplesCaptured;

    /// <summary>Piece Engine 5a: diagnostic pass-through to the active capture session's own
    /// <see cref="MiniAudioCaptureSession.OverrunCount"/> (piece Engine 0) -- not part of
    /// <see cref="IAudioEngine"/> itself (no interface change), same pattern as the session types'
    /// own extra diagnostic members (e.g. <c>TimedOutDuringClose</c>) that go beyond what any
    /// interface requires. 0 when capture isn't started, matching "nothing to report" rather than
    /// throwing.</summary>
    public int CaptureOverrunCount => _captureSession?.OverrunCount ?? 0;

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
        await _captureLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var session = _captureSession;
            _captureSession = null;
            if (session is null)
            {
                return; // idempotent no-op, matching every session type's own Dispose convention
            }

            await DisposeCaptureSessionAsync(session).ConfigureAwait(false);
        }
        finally
        {
            _captureLock.Release();
        }
    }

    /// <summary>Fires on <see cref="MiniAudioCaptureSession"/>'s own drain thread -- see
    /// <see cref="IAudioEngine.SamplesCaptured"/>'s documented threading contract, which this
    /// forwarder preserves by construction (it never marshals to another thread). The
    /// <see cref="ReadOnlyMemory{T}"/> is passed through unmodified, preserving that same
    /// contract's "fresh, independently-owned array" guarantee. Exceptions thrown by subscribers
    /// are swallowed by the session's own drain loop (documented there); this forwarder inherits
    /// that, a deliberate, pre-existing deviation from "never silent failure" this class does not
    /// attempt to fix.</summary>
    private void OnCaptureSamplesAvailable(ReadOnlyMemory<float> samples)
    {
        Volatile.Write(ref _captureForwarderThreadId, Environment.CurrentManagedThreadId);
        try
        {
            SamplesCaptured?.Invoke(samples);
        }
        finally
        {
            Volatile.Write(ref _captureForwarderThreadId, 0);
        }
    }

    private async Task DisposeCaptureSessionAsync(MiniAudioCaptureSession session)
    {
        if (Environment.CurrentManagedThreadId == Volatile.Read(ref _captureForwarderThreadId))
        {
            // Re-entrant: we're being called from inside this very session's SamplesAvailable
            // forwarder, on its own drain thread. Dispose inline so MiniAudioCaptureSession's own
            // self-join guard (Thread.CurrentThread != _drainThread) sees the correct thread -- see
            // this class's own doc comment for the deadlock this avoids.
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
        var session = _playbackSession;
        if (session is null)
        {
            throw new InvalidOperationException("Playback is not started -- call StartPlaybackAsync first.");
        }

        return session.Write(samples.Span);
    }

    public async Task StopPlaybackAsync()
    {
        await _playbackLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var session = _playbackSession;
            _playbackSession = null;
            if (session is null)
            {
                return; // idempotent no-op, matching every session type's own Dispose convention
            }

            await DrainAndDisposePlaybackSessionAsync(session).ConfigureAwait(false);
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
            return; // idempotent
        }

        // Piece Engine 3: takes each lifecycle's own lock in turn (never both at once -- see this
        // class's own doc comment on why that rules out an ABBA deadlock by construction), so a
        // Start*Async call that's already in flight when DisposeAsync begins is waited out rather
        // than raced.
        await _captureLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var captureSession = _captureSession;
            _captureSession = null;
            if (captureSession is not null)
            {
                await DisposeCaptureSessionAsync(captureSession).ConfigureAwait(false);
            }
        }
        finally
        {
            _captureLock.Release();
        }

        await _playbackLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var playbackSession = _playbackSession;
            _playbackSession = null;
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
        finally
        {
            _playbackLock.Release();
        }

        MiniAudioContext.Release();
    }
}
