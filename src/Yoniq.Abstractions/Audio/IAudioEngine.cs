namespace Yoniq.Abstractions.Audio;

/// <summary>
/// See spec/05-audio-engine.md. Samples are always mono <see cref="float"/> in [-1, 1].
///
/// <see cref="SamplesCaptured"/> fires on a normal-priority drain thread, NOT the audio backend's
/// own real-time callback thread (corrected from an earlier revision of this doc comment, per
/// piece Audio 2's contract review) — the real-time callback itself runs entirely inside the
/// native `miniaudio` shim (see `Yoniq.Core.Audio.MiniAudio`'s own doc comments), writing into a
/// lock-free ring buffer there; a separate managed thread drains that ring and raises this event.
/// This still satisfies "handlers must not allocate or call into async machinery" as a matter of
/// good practice (a slow handler here still delays RX processing), but the hard real-time
/// constraint (no GC transitions, no blocking) applies to the native callback, which no managed
/// code — including this event's handlers — ever runs on.
///
/// Overrun policy (stated per CLAUDE.md's concurrency/scheduler rule: every cross-thread stream
/// must say what happens under a slow consumer): if the drain thread falls behind and the
/// underlying ring buffer fills, the real-time capture callback drops the newest incoming frames
/// (never blocks, never overwrites older undrained data) — RX samples are lost, not corrupted or
/// reordered. Counted as an overrun for diagnostics (piece Engine 0/5a) — exposed as a
/// implementation-specific diagnostic member (e.g. `MiniAudioEngine.CaptureOverrunCount`), not part
/// of this interface itself, since no other backend implementation exists yet to confirm the same
/// shape generalizes.
///
/// Memory lifetime: the <see cref="ReadOnlyMemory{T}"/> handed to each invocation is a fresh,
/// independently-owned array, safe to store or process asynchronously — never a view into a
/// buffer a future invocation might overwrite. `FakeAudioEngine` already satisfies this trivially
/// (it hands out caller-owned memory), and the real `Yoniq.Core.Audio.MiniAudio` implementation's
/// drain thread makes a fresh copy per callback specifically to match this contract (an
/// opus-review fix — its first cut handed out a view into a reused scratch buffer instead).
///
/// Lifecycle-error contract (round-1-engine-review addition, since a prior revision left this
/// entirely to each implementation to decide, and the real and fake implementations disagreed):
/// <list type="bullet">
/// <item>Calling <see cref="StartCaptureAsync"/>/<see cref="StartPlaybackAsync"/> while that same
/// lifecycle is already started throws <see cref="InvalidOperationException"/> — silently ignoring
/// the second call (or the second device) would be the exact silent-failure spec/01-architecture.md's
/// Error Handling rule forbids.</item>
/// <item>Calling <see cref="StopCaptureAsync"/>/<see cref="StopPlaybackAsync"/> when that lifecycle
/// was never started (or already stopped) is an idempotent no-op -- including after
/// <see cref="IAsyncDisposable.DisposeAsync"/> has completed (round-2-engine-review correction: an
/// earlier revision of this doc comment claimed every member throws post-dispose, which was never
/// actually true of <c>Stop*Async</c> and was corrected here rather than tightening the real
/// implementation to match an overly-broad claim -- an idempotent no-op is the more useful
/// behavior for a caller doing teardown, matching every session type's own Dispose convention).</item>
/// <item>Calling <see cref="EnqueuePlaybackSamples"/> before <see cref="StartPlaybackAsync"/> (or
/// after <see cref="StopPlaybackAsync"/> or <see cref="IAsyncDisposable.DisposeAsync"/>) throws
/// <see cref="InvalidOperationException"/> or <see cref="ObjectDisposedException"/> respectively —
/// returning 0 would make a caller's contract-compliant partial-acceptance retry loop (see below)
/// spin forever instead of surfacing the real problem.</item>
/// <item>Calling <see cref="StartCaptureAsync"/>/<see cref="StartPlaybackAsync"/> after
/// <see cref="IAsyncDisposable.DisposeAsync"/> has completed throws
/// <see cref="ObjectDisposedException"/>.</item>
/// </list>
/// </summary>
public interface IAudioEngine : IAsyncDisposable
{
    Task StartCaptureAsync(AudioDeviceInfo device, int sampleRate, CancellationToken ct = default);

    Task StopCaptureAsync();

    Task StartPlaybackAsync(AudioDeviceInfo device, int sampleRate, CancellationToken ct = default);

    /// <summary>Stops playback only once every previously-enqueued sample has actually been played
    /// out — not immediately. An SSTV transmission is real audio a rig's PTT stays keyed for; a
    /// caller that could stop while samples are still buffered would truncate the last scanlines
    /// on air. Real backends must block (asynchronously) until their playback ring genuinely
    /// drains before completing this task.</summary>
    Task StopPlaybackAsync();

    event Action<ReadOnlyMemory<float>>? SamplesCaptured;

    /// <summary>Enqueues samples for playback and returns how many of them were actually accepted
    /// (0..<c>samples.Length</c>) — a simple, synchronous back-pressure signal (the same shape as
    /// <see cref="System.IO.Stream.Write"/> returning bytes written), not a void fire-and-forget
    /// call. A ~114-second SSTV transmission is on the order of megabytes of samples; a caller
    /// (the TX pump) that ignored a partial-acceptance return would silently drop the tail of a
    /// real transmission into an already-full buffer. Callers must check the return value and
    /// retry/wait for the remainder rather than assuming everything was accepted. See the class doc
    /// comment's Lifecycle-error contract for what happens before <see cref="StartPlaybackAsync"/>
    /// has been called.</summary>
    int EnqueuePlaybackSamples(ReadOnlyMemory<float> samples);
}
