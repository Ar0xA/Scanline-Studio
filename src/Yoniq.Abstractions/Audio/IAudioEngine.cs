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
/// reordered, and this is counted as an overrun for diagnostics (exposed once piece Audio 5/6
/// implements the capture/playback paths; not yet surfaced as of this interface-only piece).
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
    /// retry/wait for the remainder rather than assuming everything was accepted.</summary>
    int EnqueuePlaybackSamples(ReadOnlyMemory<float> samples);
}
