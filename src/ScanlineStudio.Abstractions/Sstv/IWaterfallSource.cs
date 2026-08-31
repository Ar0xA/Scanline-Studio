namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>See spec/06-sstv-dsp.md. Rolling FFT magnitude frames for the waterfall/scope view,
/// **structurally independent of <see cref="ISstvDecoder"/>** — this interface has no knowledge of
/// decode state and no decoder ever depends on it, satisfying spec/06's Definition-of-done
/// requirement that decode continue correctly with the waterfall UI closed (and vice versa) by
/// construction rather than by a runtime flag. Shaped like <see cref="ISstvDecoder.PushSamples"/>
/// deliberately (a plain push method, not a direct subscription to
/// <c>ScanlineStudio.Abstractions.Audio.IAudioEngine.SamplesCaptured</c>) — the same "DSP layer has no
/// knowledge of ... files/hardware" purity spec/06 already states for the decoder applies here too;
/// something at the <c>ScanlineStudio.Application</c> orchestration layer feeds both from the same audio
/// stream, isolated from each other (see the Phase-3 plan's decision #3 — a slow/throwing waterfall
/// must never starve the decoder, which is why this is a separate push target, not a shared
/// subscription).
///
/// <b>Concurrency/scheduler contract</b> (CLAUDE.md requires every cross-thread stream state its
/// scheduler and slow-subscriber behavior): <see cref="PushSamples"/> computes its FFT synchronously
/// and inline — cheap at the window sizes this is built for (microseconds, not a real-time-budget
/// concern) — and <see cref="Frames"/> pushes synchronously to subscribers from whatever thread called
/// <see cref="PushSamples"/>, matching the synchronous-push, no-scheduler-indirection half of
/// <c>ScanlineStudio.Abstractions.Radio.IRadioController.StateChanges</c>'s already-established
/// pattern (round-1 code-review finding, Tier A Batch 9 chunk 9a: "exactly mirroring" overstated it --
/// unlike <c>StateChanges</c>, which catches and contains a throwing subscriber, an exception from a
/// <see cref="Frames"/> subscriber propagates out of <see cref="PushSamples"/> uncaught; and
/// <c>StateChanges</c> is a <c>BehaviorSubject</c> that replays the last value to a late subscriber,
/// while <see cref="Frames"/> is a plain <c>Subject</c> that emits nothing until the next frame -- the
/// right choice for a rolling display, just not "the same pattern"). A slow subscriber therefore
/// delays the calling thread (in practice, the audio drain thread via the orchestrator's fan-out) --
/// subscribers doing real UI work must marshal to their own scheduler (e.g. Avalonia's Dispatcher),
/// not block here. The production caller (<c>SstvSessionService</c>) wraps its own call to
/// <see cref="PushSamples"/> as a whole in a try/catch (not each subscriber call individually --
/// there is no per-subscriber isolation here, unlike <c>StateChanges</c>), containing both the
/// exception-propagation gap above and a `Dispose()`-during-in-flight-push race
/// (`ObjectDisposedException` from `OnNext`) -- a future direct consumer of this interface (bypassing
/// that caller) would need to add its own containment. T1-2 (production_audit.md): a throwing
/// subscriber can never leave the just-completed WINDOW's own slide-forward state stale/un-advanced
/// for the NEXT <see cref="PushSamples"/> call, regardless of whether anything catches the throw --
/// see <c>WaterfallSource.PushSamples</c>'s own comment for the ordering guarantee this depends on
/// (a real bug this fixed: an un-advanced accumulator used to silently re-emit a stale duplicate
/// frame on the very next call). This does NOT cover a throw mid-way through a single
/// <see cref="PushSamples"/> call that would otherwise have filled more than one window -- any
/// samples past the point of the throw are simply never accumulated (dropped, not corrupted; a
/// display-only time gap in the waterfall, pre-existing and unchanged by this guarantee).
///
/// <b><see cref="PushSamples"/> is single-producer-only</b> (round-1 code-review finding): its
/// internal accumulator is mutated with no synchronization of its own -- two threads calling it
/// concurrently can interleave and silently produce torn/duplicated windows. Safe today because the
/// only production caller (<c>SstvSessionService</c>, fed by
/// <c>ScanlineStudio.Abstractions.Audio.IAudioEngine.SamplesCaptured</c>) is a single documented
/// drain thread -- a future second caller would need its own serialization, not an assumption that
/// "any thread is fine" (which the previous wording here could be read as implying).</summary>
public interface IWaterfallSource
{
    void PushSamples(ReadOnlyMemory<float> samples);

    IObservable<WaterfallFrame> Frames { get; }
}
