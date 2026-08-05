namespace Yoniq.Abstractions.Sstv;

/// <summary>See spec/06-sstv-dsp.md. Rolling FFT magnitude frames for the waterfall/scope view,
/// **structurally independent of <see cref="ISstvDecoder"/>** — this interface has no knowledge of
/// decode state and no decoder ever depends on it, satisfying spec/06's Definition-of-done
/// requirement that decode continue correctly with the waterfall UI closed (and vice versa) by
/// construction rather than by a runtime flag. Shaped like <see cref="ISstvDecoder.PushSamples"/>
/// deliberately (a plain push method, not a direct subscription to
/// <c>Yoniq.Abstractions.Audio.IAudioEngine.SamplesCaptured</c>) — the same "DSP layer has no
/// knowledge of ... files/hardware" purity spec/06 already states for the decoder applies here too;
/// something at the <c>Yoniq.Application</c> orchestration layer feeds both from the same audio
/// stream, isolated from each other (see the Phase-3 plan's decision #3 — a slow/throwing waterfall
/// must never starve the decoder, which is why this is a separate push target, not a shared
/// subscription).
///
/// <b>Concurrency/scheduler contract</b> (CLAUDE.md requires every cross-thread stream state its
/// scheduler and slow-subscriber behavior): <see cref="PushSamples"/> computes its FFT synchronously
/// and inline — cheap at the window sizes this is built for (microseconds, not a real-time-budget
/// concern) — and <see cref="Frames"/> pushes synchronously to subscribers from whatever thread called
/// <see cref="PushSamples"/>, exactly mirroring <c>Yoniq.Abstractions.Radio.IRadioController.StateChanges</c>'s
/// already-established pattern in this codebase. A slow subscriber therefore delays the calling
/// thread (in practice, the audio drain thread via the orchestrator's fan-out) — subscribers doing
/// real UI work must marshal to their own scheduler (e.g. Avalonia's Dispatcher), not block here.</summary>
public interface IWaterfallSource
{
    void PushSamples(ReadOnlyMemory<float> samples);

    IObservable<WaterfallFrame> Frames { get; }
}
