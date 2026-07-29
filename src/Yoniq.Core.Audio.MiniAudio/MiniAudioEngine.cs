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
/// Piece Engine 1 (this piece): capture-only lifecycle. Playback members throw
/// <see cref="NotSupportedException"/> until piece Engine 2 lands.
///
/// Context lifetime: acquires <see cref="MiniAudioContext"/> once for this engine instance's whole
/// lifetime (ctor/<see cref="DisposeAsync"/>), on top of whatever a live
/// <see cref="MiniAudioCaptureSession"/>/<see cref="MiniAudioPlaybackSession"/> acquires on its
/// own -- deliberately, so repeated Start/Stop cycles don't repeatedly tear down and reinitialize
/// the process-wide native context (PulseAudio context init/teardown churn is exactly what pushed
/// this project off PortAudio originally, see spec/05-audio-engine.md's Backend choice section).
///
/// Concurrency note: this piece does not yet close the TOCTOU between "check nothing is started"
/// and "record the new session" in <see cref="StartCaptureAsync"/> -- two concurrent
/// <see cref="StartCaptureAsync"/> calls can both pass the check before either publishes its
/// session. Deliberately deferred to piece Engine 3, which adds a `SemaphoreSlim(1,1)` per
/// lifecycle -- this piece is proven happy-path/translation/self-join correct first, in isolation,
/// matching this project's own established "layer correctness across pieces" precedent (the
/// session classes' own Dispose-vs-concurrent-use races were likewise closed in a later, dedicated
/// pass, not the piece that first introduced Dispose).
/// </summary>
public sealed class MiniAudioEngine : IAudioEngine
{
    private MiniAudioCaptureSession? _captureSession;

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

    public async Task StartCaptureAsync(AudioDeviceInfo device, int sampleRate, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        if (Volatile.Read(ref _captureSession) is not null)
        {
            throw new InvalidOperationException("Capture is already started -- call StopCaptureAsync first.");
        }

        // MiniAudioCaptureSession's constructor blocks synchronously (opens the native device,
        // starts a managed drain thread) -- confirmed by reading it, not assumed. Task.Run keeps
        // that off the caller's thread, matching CLAUDE.md's "all hardware communication must be
        // asynchronous" rule.
        var session = await Task.Run(() => OpenCaptureSession(device, sampleRate), ct).ConfigureAwait(false);
        session.SamplesAvailable += OnCaptureSamplesAvailable;
        Volatile.Write(ref _captureSession, session);
    }

    public async Task StopCaptureAsync()
    {
        var session = Interlocked.Exchange(ref _captureSession, null);
        if (session is null)
        {
            return; // idempotent no-op, matching every session type's own Dispose convention
        }

        await DisposeCaptureSessionAsync(session).ConfigureAwait(false);
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

    public Task StartPlaybackAsync(AudioDeviceInfo device, int sampleRate, CancellationToken ct = default) =>
        throw new NotSupportedException("Playback is not yet implemented -- see piece Engine 2 in spec/14-roadmap.md.");

    public Task StopPlaybackAsync() =>
        throw new NotSupportedException("Playback is not yet implemented -- see piece Engine 2 in spec/14-roadmap.md.");

    public int EnqueuePlaybackSamples(ReadOnlyMemory<float> samples) =>
        throw new NotSupportedException("Playback is not yet implemented -- see piece Engine 2 in spec/14-roadmap.md.");

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // idempotent
        }

        var session = Interlocked.Exchange(ref _captureSession, null);
        if (session is not null)
        {
            await DisposeCaptureSessionAsync(session).ConfigureAwait(false);
        }

        MiniAudioContext.Release();
    }
}
