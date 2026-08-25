using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;

namespace ScanlineStudio.Core.Audio.MiniAudio;

/// <summary>
/// Composes <see cref="MiniAudioCaptureSession"/>/<see cref="MiniAudioPlaybackSession"/> into a
/// real <see cref="IAudioEngine"/> implementation -- see spec/14-roadmap.md's "Composing
/// MiniAudioCaptureSession/MiniAudioPlaybackSession into a real IAudioEngine" bullet for the full,
/// Opus-verified breakdown this class is built up across (Engine 0-6).
///
/// Public (not `internal`): <see cref="MiniAudioCaptureSession"/>/<see cref="MiniAudioPlaybackSession"/>
/// are `internal`, and `AssemblyInfo.cs`'s `InternalsVisibleTo` only grants this project's own test
/// project -- so this is the only type in this project a DI composition root
/// (`ScanlineStudio.Host/Program.cs`, piece Engine 6) can actually reference.
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
///
/// Tier A Batch 1 functional-audit fix: the re-entrant self-dispose pattern above extends to
/// <see cref="DisposeAsync"/> too, with the same shape of deadlock (a second, concurrent, non-drain-
/// thread caller can block <see cref="DisposeAsync"/>'s own teardown on this session's drain thread
/// exiting, while a reentrant subscriber call on that same drain thread blocks waiting for that
/// teardown to finish) -- see <see cref="_captureSessionBeingDisposed"/>'s own doc comment for the
/// fix, and <see cref="DisposeCaptureSessionAsync"/>'s own doc comment for the one real boundary on
/// what counts as "reentrant" for this purpose (synchronous, on-thread only -- not `Task.Run(...).Wait()`).
/// </summary>
public sealed partial class MiniAudioEngine : IAudioEngine
{
    private readonly ILogger<MiniAudioEngine> _logger;
    private readonly ILoggerFactory? _loggerFactory;

    // Piece Engine 3: guards Start/StopCaptureAsync's multi-step "check nothing started, open,
    // publish" as one atomic critical section, closing the TOCTOU an earlier piece deliberately
    // left open (two concurrent StartCaptureAsync calls could otherwise both pass the check before
    // either published its session). `volatile` on the session fields themselves is still needed
    // independent of this lock -- see EnqueuePlaybackSamples's own doc comment for the one reader
    // that deliberately does not take a lock at all.
    private readonly SemaphoreSlim _captureLock = new(1, 1);
    private readonly SemaphoreSlim _playbackLock = new(1, 1);

    private volatile MiniAudioCaptureSession? _captureSession;

    // Functional-audit fix (Tier A Batch 1, native/managed boundary): tracks the specific capture
    // session a DisposeAsync-initiated teardown is currently responsible for, from the moment its
    // own claim (ClaimCaptureSessionLocked, called with trackForDisposeAsync: true) removes it from
    // _captureSession above through the moment its own disposal actually finishes -- separate from
    // _captureSession because that field is nulled at CLAIM time, before disposal starts (see
    // ClaimCaptureSessionLocked's own doc comment for why that early nulling exists and must not
    // change). Exists ONLY to let DisposeAsync's own second-caller branch (below) recognize "the
    // calling thread IS the drain thread of the session currently being torn down" even after
    // _captureSession has already gone null -- without it, a SamplesCaptured subscriber that
    // re-entrantly self-disposes (see DisposeCaptureSessionAsync's own doc comment for that
    // supported pattern) can lose the _disposed Interlocked.Exchange race to a concurrent external
    // caller and then block forever on _disposedSignal below, while that external caller's own
    // Task.Run(session.Dispose) blocks forever joining THIS thread's own drain loop -- a real,
    // reachable deadlock this field closes. Single-writer by construction: only DisposeAsync ever
    // passes trackForDisposeAsync: true, and the _disposed latch below admits at most one
    // DisposeAsync-initiated teardown per engine instance -- do NOT widen tracking to
    // StopCaptureAsync to close a real but narrower residual gap: if a concurrent, untracked
    // StopCaptureAsync claims the session FIRST, DisposeAsync's own claim returns null and this
    // field never gets set for that session, so a reentrant drain-thread caller falls through to
    // awaiting _disposedSignal after all -- code-review-confirmed as latency, not deadlock, though:
    // DisposeAsync's remaining teardown (playback only, at that point) has no dependency on this
    // drain thread, so the signal still fires and the reentrant caller unblocks, delayed by up to
    // DrainTimeout + DrainTailMargin + the playback session's own CloseTimeout. Widening tracking to
    // close even that latency would require Interlocked.CompareExchange instead of a plain clear
    // (multiple concurrent StopCaptureAsync calls are possible, unlike DisposeAsync's single-writer
    // guarantee) and would conflict with `volatile` (CS0420) -- not worth it for a bounded stall.
    private volatile MiniAudioCaptureSession? _captureSessionBeingDisposed;

    private int _disposed;

    // Round-1 functional-audit fix: OnCaptureSamplesAvailable used to be a single
    // `SamplesCaptured?.Invoke(samples)` -- MiniAudioCaptureSession.SamplesAvailable's own doc
    // comment documents per-handler isolation (GetInvocationList(), one throwing subscriber can't
    // starve another), but that isolation only ever covered the session's own single registered
    // handler (this forwarder). Real production code has TWO downstream subscribers on THIS event
    // (SstvSessionService's decoder + waterfall handlers) -- a single multicast Invoke here meant
    // the first one throwing skipped the second for that chunk, silently. Only not a live bug
    // because SstvSessionService happens to self-isolate both its own handlers already; any future
    // subscriber that doesn't would reintroduce it. Same rate-limited hot-path logging shape as
    // MiniAudioCaptureSession's own _subscriberExceptionCount (docs/logging-guidelines.md).
    //
    // Round-2 functional-audit fix: catching every SamplesCaptured subscriber's exception here
    // means one never propagates back out of OnCaptureSamplesAvailable into the claimed session's
    // own DrainLoop try/catch -- before this round, that propagation was the ONLY reason
    // MiniAudioCaptureSession's LastSubscriberException/SubscriberExceptionCount (which
    // CaptureLastSubscriberException/CaptureSubscriberExceptionCount below pass through to) ever
    // observed a SamplesCaptured subscriber throwing, since this forwarder is the session's one and
    // only registered handler. Round 1's isolation fix silently made those two documented
    // diagnostics permanently report "nothing ever threw" for every real subscriber exception.
    // _lastCapturedSubscriberException/_capturedSubscriberExceptionCount below are now the engine's
    // own record of exactly that, and CaptureLastSubscriberException/CaptureSubscriberExceptionCount
    // read from these instead of the session's (which now only ever reflects a hypothetical future
    // subscriber registered directly on the session, bypassing this engine).
    private volatile Exception? _lastCapturedSubscriberException;
    private int _capturedSubscriberExceptionCount;

    // Round-1-engine-review fix: signals a second concurrent DisposeAsync caller that teardown has
    // actually finished, rather than letting it return immediately once _disposed is latched (the
    // original behavior let a second caller's `await DisposeAsync()` complete while the first
    // caller's teardown -- native close, MiniAudioContext.Release() -- was still in progress).
    //
    // Functional-audit fix (Tier A Batch 1): NOT an unconditional contract for every second caller.
    // A second caller that IS the active-or-being-torn-down capture session's own drain thread
    // (reentering synchronously via a SamplesCaptured subscriber, not via Task.Run(...).Wait() --
    // see _captureSessionBeingDisposed's own doc comment) returns immediately WITHOUT awaiting this
    // signal, since waiting is exactly what would deadlock the first caller's own teardown. Every
    // OTHER second caller still awaits this signal and observes true completion as documented above.
    private readonly TaskCompletionSource _disposedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ILoggerFactory optional, defaulting to null: when supplied (as Program.cs does today via DI,
    // which registers ILoggerFactory automatically), MiniAudioCaptureSession gets its own
    // correctly-categorized logger instead of sharing this type's ILogger<MiniAudioEngine> category
    // -- keeps per-category level filtering meaningful. Falls back to the shared logger when omitted
    // (e.g. in tests constructing this directly).
    public MiniAudioEngine(ILogger<MiniAudioEngine> logger, ILoggerFactory? loggerFactory = null)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;

        // Tier A Batch 1 re-audit round 3 nit fix: without this, a single (non-concurrent)
        // DisposeAsync caller whose own teardown throws leaves _disposedSignal.TrySetException's
        // faulted Task permanently unobserved -- the real exception IS still correctly surfaced to
        // that caller via DisposeAsync's own `throw` (see its own try/catch/finally), so this isn't
        // a correctness issue, only host-level noise: an unobserved faulted Task fires
        // TaskScheduler.UnobservedTaskException on finalization, which some hosts treat as fatal
        // (ThrowUnobservedTaskExceptions) or at least log. Attaching a no-op OnlyOnFaulted
        // continuation here marks it observed regardless of whether a second concurrent
        // DisposeAsync caller ever awaits _disposedSignal.Task itself.
        //
        // Code-review note: ExecuteSynchronously is requested but currently inert -- _disposedSignal
        // is constructed with TaskCreationOptions.RunContinuationsAsynchronously (below), which
        // forces every continuation, including this one, to run on TaskScheduler.Default regardless
        // of this flag. Kept anyway as a statement of intent (cheapest possible continuation, run
        // wherever, no marshaling needed) -- if RunContinuationsAsynchronously were ever removed
        // from _disposedSignal's own construction, this would start inlining on whatever thread
        // calls TrySetException (potentially the capture drain thread, see that call site's own
        // context) -- still harmless today, since the body is a single non-throwing property read
        // with no lock held, but worth knowing if that assumption ever needs re-checking.
        _ = _disposedSignal.Task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            MiniAudioContext.Acquire();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            // Round-1-engine-review fix: this used to catch only InvalidOperationException, but
            // MiniAudioContext.Acquire() calls straight into a P/Invoke (scanline_audio_context_init)
            // that throws DllNotFoundException/EntryPointNotFoundException if the native shim is
            // missing or mismatched -- the same real failure mode OpenCaptureSession/
            // OpenPlaybackSession already translate, confirmed by reading MiniAudioContext.cs.
            // Without this, a broken ScanlineStudio.Host native-shim copy target (see Engine 6's own commit)
            // would surface as a raw DllNotFoundException from `new MiniAudioEngine()` instead of
            // the typed exception AudioDeviceUnavailableException's own doc comment promises.
            Log.ContextInitFailed(_logger, ex);
            throw new AudioDeviceUnavailableException("Failed to initialize the audio backend.", ex);
        }
    }

    public event Action<ReadOnlyMemory<float>>? SamplesCaptured;

    /// <summary>Piece Engine 5a: diagnostic pass-through to the active capture session's own
    /// <see cref="MiniAudioCaptureSession.OverrunCount"/> (piece Engine 0). Now promoted onto
    /// <see cref="IAudioEngine.CaptureOverrunCount"/> itself (spec/17-rx-telemetry-feasibility.md's
    /// status-bar "Buffer · XRUN" readout) -- an earlier revision of this comment claimed this was
    /// deliberately NOT part of the interface; that's no longer true, see the interface's own doc
    /// comment for why. 0 when capture isn't started, matching "nothing to report" rather than
    /// throwing -- except for the same narrow, documented race
    /// <see cref="EnqueuePlaybackSamples"/> has (round-3-engine-review honesty fix): a concurrent
    /// <see cref="StopCaptureAsync"/> disposing the session between this property's field read and
    /// the underlying native call can still surface the session's own
    /// <see cref="ObjectDisposedException"/>, since this is a diagnostic-only member and
    /// deliberately does not take <see cref="_captureLock"/> either. Tier A Batch 1 re-audit round 5
    /// addition: the SAME race can ALSO make a caller (e.g. a timer poll -- concretely, in this
    /// port's own real call graph, Avalonia's UI thread, see
    /// <see cref="IAudioEngine.CaptureOverrunCount"/>'s own doc comment for the full chain) BLOCK
    /// before it throws, not instead of throwing -- <c>?.</c> reads <see cref="_captureSession"/>
    /// into a temp before the property call, so a concurrent claim-and-dispose landing in that
    /// window means the property call proceeds against a session that's mid-<c>Dispose()</c>, which
    /// holds its own write lock across an unbounded drain-thread join plus a bounded (~5s, typically
    /// near-instant) native-close join before the read finally proceeds and observes the
    /// now-<c>true</c> disposed flag -- a UI-thread stall, not just a caught exception (see
    /// <see cref="MiniAudioCaptureSession.Dispose"/>'s own doc comment for the full reasoning and
    /// why it's an accepted, not fixed, tradeoff).</summary>
    public int CaptureOverrunCount => _captureSession?.OverrunCount ?? 0;

    /// <summary>Piece Band-1 (pre-Phase-2 audit): the most recent exception thrown by a
    /// <see cref="SamplesCaptured"/> subscriber, or null if none has thrown. Round-2 functional-
    /// audit fix: this used to pass through to the active capture session's own
    /// <see cref="MiniAudioCaptureSession.LastSubscriberException"/>, which only ever observed a
    /// <see cref="SamplesCaptured"/> subscriber's exception by accident, via it propagating out of
    /// <see cref="OnCaptureSamplesAvailable"/> uncaught -- now that per-handler isolation catches it
    /// there instead (round-1 fix), the session's own field would otherwise always read null. This
    /// property now reads the engine's own <see cref="_lastCapturedSubscriberException"/>, set at
    /// the point <see cref="OnCaptureSamplesAvailable"/> actually catches it -- not gated on
    /// <see cref="_captureSession"/> being non-null, since the exception may have been recorded by a
    /// chunk still in flight from a session this call races with being stopped (a deliberately
    /// looser diagnostic-only contract, same reasoning as <see cref="CaptureOverrunCount"/>'s own
    /// doc comment). <b>Lifetime differs from <see cref="CaptureOverrunCount"/>:</b> that property
    /// resets to 0 on every fresh <see cref="StartCaptureAsync"/> (per-session); this one and
    /// <see cref="CaptureSubscriberExceptionCount"/> are cumulative for the whole engine's lifetime
    /// and never reset by a new capture session, since they now live on the engine itself rather
    /// than being read from whichever session happens to be active (round-3 functional-audit
    /// clarification -- the reset-on-restart behavior implicitly changed when this stopped being a
    /// pass-through, and was previously undocumented).</summary>
    public Exception? CaptureLastSubscriberException => _lastCapturedSubscriberException;

    /// <summary>Piece Band-1 (pre-Phase-2 audit): cumulative count of times any
    /// <see cref="SamplesCaptured"/> subscriber has thrown. See
    /// <see cref="CaptureLastSubscriberException"/>'s own doc comment for why this now reads the
    /// engine's own <see cref="_capturedSubscriberExceptionCount"/> instead of passing through to
    /// the active capture session, and for its engine-lifetime (not per-session) reset
    /// semantics.</summary>
    public int CaptureSubscriberExceptionCount => Volatile.Read(ref _capturedSubscriberExceptionCount);

    /// <summary>Piece Engine 5a: diagnostic pass-through to the active playback session's own
    /// <see cref="MiniAudioPlaybackSession.UnderrunCount"/>. See <see cref="CaptureOverrunCount"/>'s
    /// own doc comment for the same reasoning.</summary>
    public int PlaybackUnderrunCount => _playbackSession?.UnderrunCount ?? 0;

    public async Task StartCaptureAsync(
        AudioDeviceInfo device, int sampleRate, ThreadPriority? drainThreadPriority = null,
        int periodSizeInFrames = 0, int periods = 0, AudioChannelSource channelSource = AudioChannelSource.Mono,
        CancellationToken ct = default)
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
            var session = await Task.Run(() => OpenCaptureSession(device, sampleRate, drainThreadPriority, periodSizeInFrames, periods, channelSource), ct).ConfigureAwait(false);
            // Round-2 functional-audit fix: publish _captureSession before subscribing, not after.
            // The drain thread starts running inside the session's own constructor, so the previous
            // subscribe-then-publish order left a window where a chunk delivered between the two
            // statements would find _captureSession still null -- ClaimCaptureSessionAsync's
            // IsRunningOnDrainThread check would then take the WaitAsync() path instead of the
            // thread-blocking Wait() path a same-thread reentrant StopCaptureAsync call needs (see
            // ClaimCaptureSessionAsync's own doc comment), risking the exact deadlock that check
            // exists to avoid. Publishing first means the worst case of the same race is instead
            // "one early chunk is never forwarded" -- consistent with the drop-newest overrun policy
            // already documented on IAudioEngine.SamplesCaptured, not a new failure mode. Both
            // statements remain under _captureLock, which any claimer must also acquire, so this
            // reordering introduces no new race with ClaimCaptureSessionLocked's own unsubscribe.
            _captureSession = session;
            session.SamplesAvailable += OnCaptureSamplesAvailable;
            Log.CaptureOpened(_logger, device.Id, sampleRate);
        }
        finally
        {
            _captureLock.Release();
        }
    }

    public async Task StopCaptureAsync()
    {
        var session = await ClaimCaptureSessionAsync(trackForDisposeAsync: false).ConfigureAwait(false);
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
    /// immediately as a no-op, instead of ever contending with an in-progress disposal.
    ///
    /// <paramref name="trackForDisposeAsync"/>: <see langword="true"/> only from <see cref="DisposeAsync"/>'s
    /// own call site -- see <see cref="_captureSessionBeingDisposed"/>'s own doc comment for why.
    /// <see cref="StopCaptureAsync"/> always passes <see langword="false"/>: it has no
    /// <see cref="_disposedSignal"/>-style "second caller waits for true completion" contract to
    /// protect (a second concurrent <see cref="StopCaptureAsync"/> already just finds
    /// <see cref="_captureSession"/> null and no-ops, per this method's own reasoning above), so it
    /// is never at risk of the deadlock this tracking exists to prevent -- deliberately not widened,
    /// see <see cref="_captureSessionBeingDisposed"/>'s own doc comment for why.</summary>
    private async Task<MiniAudioCaptureSession?> ClaimCaptureSessionAsync(bool trackForDisposeAsync)
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
                return ClaimCaptureSessionLocked(trackForDisposeAsync);
            }
            finally
            {
                _captureLock.Release();
            }
        }

        await _captureLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return ClaimCaptureSessionLocked(trackForDisposeAsync);
        }
        finally
        {
            _captureLock.Release();
        }
    }

    /// <summary>Must be called with <see cref="_captureLock"/> already held.</summary>
    private MiniAudioCaptureSession? ClaimCaptureSessionLocked(bool trackForDisposeAsync)
    {
        var session = _captureSession;

        // Functional-audit fix (Tier A Batch 1): write order here is load-bearing, NOT stylistic --
        // do not reorder these two statements, and do not "simplify" by moving this write after
        // `_captureSession = null` below. See _captureSessionBeingDisposed's own doc comment for the
        // deadlock this exists to prevent; the mechanism only works because DisposeAsync's own
        // second-caller check (which reads _captureSession, then _captureSessionBeingDisposed, in
        // that order, WITHOUT taking _captureLock) is guaranteed -- by volatile's release/acquire
        // ordering, not by this lock -- to observe THIS write once it observes _captureSession
        // having gone null. Swapping the order reopens a real interleaving where a reader can
        // observe both fields as stale/null and deadlock exactly as before this fix.
        if (trackForDisposeAsync)
        {
            _captureSessionBeingDisposed = session;
        }

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
    /// contract's "fresh, independently-owned array" guarantee.
    ///
    /// Round-1 functional-audit fix: invokes each <see cref="SamplesCaptured"/> subscriber
    /// independently (<c>GetInvocationList()</c>, not a single multicast call) so one throwing
    /// subscriber can't starve another for that chunk -- same per-handler isolation
    /// <see cref="MiniAudioCaptureSession.SamplesAvailable"/> already provides for ITS single
    /// registered handler (this forwarder); without this, that isolation never reached
    /// <see cref="SamplesCaptured"/>'s own real multi-subscriber case.</summary>
    private void OnCaptureSamplesAvailable(ReadOnlyMemory<float> samples)
    {
        var subscribers = SamplesCaptured;
        if (subscribers is null)
        {
            return;
        }

        foreach (var handler in subscribers.GetInvocationList())
        {
            try
            {
                ((Action<ReadOnlyMemory<float>>)handler)(samples);
            }
            catch (Exception ex)
            {
                // Hot path (capture drain thread, once per chunk) -- logged at the first
                // occurrence, then only a periodic summary, same convention as
                // MiniAudioCaptureSession's own identical subscriber-exception logging.
                _lastCapturedSubscriberException = ex;
                var count = Interlocked.Increment(ref _capturedSubscriberExceptionCount);
                if (count == 1)
                {
                    Log.CapturedSubscriberThrew(_logger, ex);
                }
                else if (count % 100 == 0)
                {
                    Log.CapturedSubscriberThrewRepeated(_logger, count);
                }
            }
        }
    }

    /// <summary>Disposes a claimed capture session -- inline, on the calling thread, if and only if
    /// that thread is THIS session's own drain thread (<see cref="MiniAudioCaptureSession.IsRunningOnDrainThread"/>,
    /// round-1-engine-review fix: previously tracked via a single engine-wide "which thread is
    /// currently forwarding" field, which could not tell one session's drain thread apart from
    /// another's if a subscriber stopped one session and started a new one from within the same
    /// callback invocation -- fixed by asking the session itself). Dispatches to a pool thread via
    /// <c>Task.Run</c> otherwise, matching every other session-closing call in this class.
    ///
    /// A <see cref="SamplesCaptured"/> subscriber re-entrantly calling <see cref="DisposeAsync"/>
    /// (this class's own top-of-file doc comment's "supported pattern") is only actually safe when
    /// it calls it SYNCHRONOUSLY, directly on this drain thread (functional-audit fix, Tier A Batch
    /// 1) -- see <see cref="_captureSessionBeingDisposed"/>'s own doc comment for the mechanism that
    /// makes it safe. A subscriber that instead dispatches via <c>Task.Run(() =&gt; engine.DisposeAsync()).Wait()</c>
    /// moves the caller onto a POOL thread, where <see cref="MiniAudioCaptureSession.IsRunningOnDrainThread"/>
    /// is false -- that variant is NOT covered and can still deadlock the same way; this is a
    /// pre-existing limitation of the reentrant-self-dispose pattern generally, not a regression
    /// this fix introduces or a gap it closes.</summary>
    private async Task DisposeCaptureSessionAsync(MiniAudioCaptureSession session)
    {
        // Checked once here, at the single point every capture-stop path (explicit StopCaptureAsync
        // and engine-teardown DisposeAsync) funnels through -- never polled live on the drain thread
        // itself (see docs/logging-guidelines.md's hot-path rule). A non-zero count means RX samples
        // were silently dropped during this session; today that was completely invisible.
        if (session.OverrunCount > 0)
        {
            Log.CaptureOverrunsDetected(_logger, session.OverrunCount);
        }

        if (session.IsRunningOnDrainThread)
        {
            session.Dispose();
        }
        else
        {
            await Task.Run(session.Dispose).ConfigureAwait(false);
        }

        if (session.TimedOutDuringClose)
        {
            Log.CaptureCloseTimedOut(_logger);
        }

        Log.CaptureStopped(_logger);
    }

    private MiniAudioCaptureSession OpenCaptureSession(
        AudioDeviceInfo device, int sampleRate, ThreadPriority? drainThreadPriority, int periodSizeInFrames,
        int periods, AudioChannelSource channelSource)
    {
        try
        {
            var sessionLogger = _loggerFactory?.CreateLogger<MiniAudioCaptureSession>() ?? (ILogger)_logger;
            return new MiniAudioCaptureSession(
                device.Id, sampleRate, sessionLogger, drainThreadPriority: drainThreadPriority,
                periodSizeInFrames: periodSizeInFrames, periods: periods, channelSource: channelSource);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // Code-review fix (Tier A Batch 1 re-audit round 2/3): caught separately from, and
            // BEFORE, the general ArgumentException branch below -- ArgumentOutOfRangeException
            // derives from it, so without this it would fall through and surface as "Failed to open
            // capture device", which is misleading: the device is fine, this is a bad SETTINGS
            // value (see MiniAudioCaptureSession's own constructor doc comment -- drainThreadPriority
            // reaches that constructor with no upstream range validation, e.g. a hand-edited
            // settings file with an out-of-range enum value).
            Log.CaptureSettingsInvalid(_logger, device.Id, sampleRate, ex);
            throw new AudioDeviceUnavailableException($"Invalid capture settings for device '{device.Id}': {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            // Every real failure mode MiniAudioCaptureSession's constructor can throw, confirmed by
            // reading it: InvalidOperationException (native open failure, or MiniAudioContext
            // context-init failure), ArgumentException (device id too long for the native shim's
            // fixed buffer -- NativeAudio.EncodeFixedString; ArgumentOutOfRangeException, a subtype,
            // is caught separately above), DllNotFoundException/EntryPointNotFoundException (native
            // shim missing/mismatched). Anything outside this set is left untranslated --
            // deliberately not treated as "device unavailable" when it might be a genuine, unrelated
            // bug.
            Log.CaptureOpenFailed(_logger, device.Id, sampleRate, ex);
            throw new AudioDeviceUnavailableException($"Failed to open capture device '{device.Id}' at {sampleRate}Hz.", ex);
        }
    }

    // Piece Engine 2. Bound on StopPlaybackAsync's DrainAsync poll -- MiniAudioPlaybackSession's
    // own default ring capacity is 16384 frames (~0.37s at 44100Hz), so a normal drain finishes in
    // a small fraction of a second; this is a safety net against a genuinely stuck device, not the
    // expected case.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    // Piece Engine 2, found by this project's Opus plan-review pass and confirmed by reading
    // scanline_audio_playback_session_pending_frames' native body (a one-line
    // scanline_audio_ring_available_read): PendingFrames reaching 0 only means the shim's own ring is
    // empty, NOT that miniaudio's device buffer or the PulseAudio server's own output queue have
    // actually finished playing -- closing the session right at that point can truncate the very
    // tail of a real transmission, exactly what IAudioEngine.StopPlaybackAsync's contract exists to
    // prevent. This is an empirical safety margin, not a precisely derived value (no shim API
    // currently exposes real device/server latency) -- verified against a real virtual sink/monitor
    // by MiniAudioEngineTests, not trusted blind.
    private static readonly TimeSpan DrainTailMargin = TimeSpan.FromMilliseconds(200);

    private volatile MiniAudioPlaybackSession? _playbackSession;

    public async Task StartPlaybackAsync(
        AudioDeviceInfo device, int sampleRate, int periodSizeInFrames = 0, int periods = 0,
        bool stereoTx = false, CancellationToken ct = default)
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
            var session = await Task.Run(() => OpenPlaybackSession(device, sampleRate, periodSizeInFrames, periods, stereoTx), ct).ConfigureAwait(false);
            _playbackSession = session;
            Log.PlaybackOpened(_logger, device.Id, sampleRate);
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
    /// here (a genuine, narrow, documented gap -- not silently swallowed). Tier A Batch 1 re-audit
    /// round 6 addition: the same mechanism round 5 documented for
    /// <see cref="CaptureOverrunCount"/> applies here too -- the racing `Write` call can BLOCK
    /// (on <see cref="MiniAudioPlaybackSession"/>'s own read lock, held by a concurrent
    /// <see cref="StopPlaybackAsync"/>'s `Dispose()` across up to its own `CloseTimeout`) before it
    /// throws, not merely throw. Lower-severity than the capture case: this call's only real
    /// production caller (<c>SstvSessionService.EnqueueAllAsync</c>) runs on a background TX task,
    /// not the UI thread, and there is no UI poller for <see cref="PlaybackUnderrunCount"/> the way
    /// <see cref="CaptureOverrunCount"/> has one for capture. Reads the `volatile`
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
    private async Task DrainAndDisposePlaybackSessionAsync(MiniAudioPlaybackSession session)
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
            if (session.TimedOutDuringClose)
            {
                Log.PlaybackCloseTimedOut(_logger);
            }

            throw new AudioDeviceUnavailableException(
                $"Playback did not drain within {DrainTimeout.TotalSeconds}s -- the device may have stopped responding.");
        }

        // See DrainTailMargin's own doc comment: PendingFrames==0 does not mean "fully played out"
        // for the device/server buffers this shim has no visibility into.
        await Task.Delay(DrainTailMargin).ConfigureAwait(false);

        // Checked once here, the single point every playback-stop path funnels through -- same
        // reasoning as DisposeCaptureSessionAsync's own OverrunCount check.
        if (session.UnderrunCount > 0)
        {
            Log.PlaybackUnderrunsDetected(_logger, session.UnderrunCount);
        }

        await Task.Run(session.Dispose).ConfigureAwait(false);
        if (session.TimedOutDuringClose)
        {
            Log.PlaybackCloseTimedOut(_logger);
        }

        Log.PlaybackStopped(_logger);
    }

    private MiniAudioPlaybackSession OpenPlaybackSession(
        AudioDeviceInfo device, int sampleRate, int periodSizeInFrames, int periods, bool stereoTx)
    {
        try
        {
            return new MiniAudioPlaybackSession(
                device.Id, sampleRate, periodSizeInFrames: periodSizeInFrames, periods: periods, stereoTx: stereoTx);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            // Same real failure-mode set as OpenCaptureSession -- see its own doc comment.
            Log.PlaybackOpenFailed(_logger, device.Id, sampleRate, ex);
            throw new AudioDeviceUnavailableException($"Failed to open playback device '{device.Id}' at {sampleRate}Hz.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            // Functional-audit fix (Tier A Batch 1): checked BEFORE awaiting the completion signal
            // below -- a real, reachable deadlock otherwise. If the calling thread is the drain
            // thread of the capture session that a WINNING concurrent DisposeAsync caller is
            // currently tearing down (this thread reentered via a SamplesCaptured subscriber calling
            // DisposeAsync synchronously -- this class's own top-of-file doc comment's "supported
            // pattern", with the one boundary DisposeCaptureSessionAsync's own doc comment states),
            // blocking here would wait on a signal that caller can only set AFTER its own
            // Task.Run(session.Dispose) finishes joining THIS thread's own drain loop -- which
            // cannot happen while this thread is blocked right here. Returning immediately breaks
            // that cycle.
            //
            // Read order matters and must NOT change: _captureSession first, THEN
            // _captureSessionBeingDisposed -- the exact reverse of the write order
            // ClaimCaptureSessionLocked uses (see that method's own doc comment for why the reverse
            // order is what makes this safe under volatile's release/acquire ordering, without
            // taking _captureLock here). Both members read below (IsRunningOnDrainThread) are
            // deliberately lock-free AND never throw ObjectDisposedException on an already-disposed
            // session -- a future addition to this check must preserve both properties, or it can
            // reintroduce this exact deadlock (a lock-taking member would block against the write
            // lock DisposeCaptureSessionAsync's own inline/Task.Run Dispose() call holds for its
            // entire duration).
            if (_captureSession?.IsRunningOnDrainThread == true || _captureSessionBeingDisposed?.IsRunningOnDrainThread == true)
            {
                Log.DisposeAsyncReenteredFromDrainThread(_logger);
                return;
            }

            // Round-1-engine-review fix: this used to return immediately here, before the first
            // caller's teardown (native close, MiniAudioContext.Release()) had necessarily
            // finished -- a second concurrent `await engine.DisposeAsync()` could complete while
            // the device was still open. Awaiting the completion signal instead matches the
            // session types' own "Dispose blocks a second caller until the first is done"
            // convention (there via a write lock; here via a TaskCompletionSource, since disposal
            // itself must never be blocked by a Start/Stop racing in ahead of it the way a shared
            // lock would force). Every caller other than the reentrant-drain-thread case just
            // handled above still goes through this normal path and observes true completion.
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
                var captureSession = await ClaimCaptureSessionAsync(trackForDisposeAsync: true).ConfigureAwait(false);
                if (captureSession is not null)
                {
                    await DisposeCaptureSessionAsync(captureSession).ConfigureAwait(false);
                }
            }
            finally
            {
                // Functional-audit fix (Tier A Batch 1): cleared here, at the top of this existing
                // finally block (not restructured into a new one -- this block's own try/finally
                // shape is itself a round-2 fix, see this method's own comment above), so it runs
                // whether or not DisposeCaptureSessionAsync threw. By the time this statement runs,
                // that call has already fully returned (successfully or not) -- meaning any
                // Task.Run(session.Dispose) it started has already completed its own drain-thread
                // Join -- so no reentrant caller can still be relying on this field at this point.
                _captureSessionBeingDisposed = null;

                var playbackSession = await ClaimPlaybackSessionAsync().ConfigureAwait(false);
                if (playbackSession is not null)
                {
                    try
                    {
                        await DrainAndDisposePlaybackSessionAsync(playbackSession).ConfigureAwait(false);
                    }
                    catch (AudioDeviceUnavailableException ex)
                    {
                        // Best-effort cleanup, unlike StopPlaybackAsync's own explicit-caller-request
                        // path (which surfaces this) -- DrainAndDisposePlaybackSessionAsync already
                        // disposed the session on this path before throwing, so there is nothing left
                        // to clean up here. A caller that cares whether playback actually finished
                        // draining should call StopPlaybackAsync explicitly before disposing, not rely
                        // on DisposeAsync for that.
                        Log.PlaybackDrainCleanupSwallowed(_logger, ex);
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
            // ScanlineStudio.Host/Program.cs's own Exit handler comment names as a real possibility), the
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

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to initialize the MiniAudio native context -- the native shim may be missing or mismatched")]
        public static partial void ContextInitFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "A SamplesCaptured subscriber threw on the capture drain thread")]
        public static partial void CapturedSubscriberThrew(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "A SamplesCaptured subscriber has now thrown {Count} times on the capture drain thread")]
        public static partial void CapturedSubscriberThrewRepeated(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to open capture device '{DeviceId}' at {SampleRate}Hz")]
        public static partial void CaptureOpenFailed(ILogger logger, string deviceId, int sampleRate, Exception ex);

        // Round-4 re-audit nit fix: distinct from CaptureOpenFailed above -- that message says
        // "Failed to open capture device", which is exactly what the ArgumentOutOfRangeException
        // catch's own thrown-exception message was corrected to NOT say (the device is fine, a
        // settings value isn't). Sharing CaptureOpenFailed for both would leave the log line
        // contradicting the exception message a reader sees moments later.
        [LoggerMessage(Level = LogLevel.Error, Message = "Invalid capture settings for device '{DeviceId}' at {SampleRate}Hz")]
        public static partial void CaptureSettingsInvalid(ILogger logger, string deviceId, int sampleRate, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Capture opened: device='{DeviceId}' @ {SampleRate}Hz")]
        public static partial void CaptureOpened(ILogger logger, string deviceId, int sampleRate);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Capture session had {OverrunCount} buffer overrun(s) -- RX samples were dropped")]
        public static partial void CaptureOverrunsDetected(ILogger logger, int overrunCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "Capture stopped")]
        public static partial void CaptureStopped(ILogger logger);

        // Functional-audit fix (Tier A Batch 1): NOT a hot-path/per-chunk event -- fires when a
        // SamplesCaptured subscriber's own reentrant DisposeAsync call loses the race to a
        // concurrent external caller, which only happens once the caller latches _disposed itself
        // (code-review correction: not literally "at most once" -- an unusual subscriber that calls
        // DisposeAsync on every chunk, rather than latching its own single attempt like this file's
        // established pattern, could hit this on a few consecutive chunks within the claim/Task.Run
        // window before _stopping takes effect; still bounded, still not a steady-state hot path).
        // See _captureSessionBeingDisposed's own doc comment for why this branch exists and returns
        // without waiting for true completion.
        [LoggerMessage(Level = LogLevel.Debug, Message = "DisposeAsync re-entered from the capture drain thread -- returning without waiting for the concurrent teardown already in progress to finish")]
        public static partial void DisposeAsyncReenteredFromDrainThread(ILogger logger);

        // Round-3 functional-audit addition: TimedOutDuringClose was already set by
        // MiniAudioCaptureSession/MiniAudioPlaybackSession's own Dispose() on a genuine hot-unplug
        // (the native close ran on its own thread and never rejoined within CloseTimeout), but
        // nothing here ever read it -- a real, documented failure mode that also deliberately leaks
        // a MiniAudioContext reference (see TimedOutDuringClose's own doc comment for why) produced
        // zero log output distinguishing it from an ordinary clean stop.
        [LoggerMessage(Level = LogLevel.Warning, Message = "Capture session's native close timed out -- the device may have been unplugged; its MiniAudioContext reference was deliberately not released")]
        public static partial void CaptureCloseTimedOut(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Playback session's native close timed out -- the device may have been unplugged; its MiniAudioContext reference was deliberately not released")]
        public static partial void PlaybackCloseTimedOut(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to open playback device '{DeviceId}' at {SampleRate}Hz")]
        public static partial void PlaybackOpenFailed(ILogger logger, string deviceId, int sampleRate, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Playback opened: device='{DeviceId}' @ {SampleRate}Hz")]
        public static partial void PlaybackOpened(ILogger logger, string deviceId, int sampleRate);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Playback session had {UnderrunCount} buffer underrun(s)")]
        public static partial void PlaybackUnderrunsDetected(ILogger logger, int underrunCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "Playback stopped")]
        public static partial void PlaybackStopped(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Playback failed to drain during best-effort dispose cleanup; session was already closed by the drain path itself")]
        public static partial void PlaybackDrainCleanupSwallowed(ILogger logger, Exception ex);
    }
}
