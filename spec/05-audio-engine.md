# Audio Engine

## Related

[[01-architecture]] · feeds → [[06-sstv-dsp]] · replaces `Sound.cpp`/`Sound.h` (Windows `waveIn`/`waveOut`/DirectSound)

## Purpose

Cross-platform capture/playback of the audio stream that carries SSTV (and, incidentally, the sound card's role as the PTT-adjacent TX audio path). This is the highest-risk cross-platform port: the legacy `Sound.cpp` is built directly on Windows multimedia APIs, which have no equivalent on Linux/macOS.

## Backend choice

A native cross-platform audio library is used behind the abstraction below rather than P/Invoking OS-specific APIs three times. Candidates evaluated: **PortAudio** (mature, C, has maintained .NET bindings, genuinely cross-platform low-latency I/O) vs. NAudio (Windows-only) vs. per-OS native (WASAPI/ALSA/CoreAudio) triple implementation. PortAudio is the recommended default — it satisfies "cross-platform" without a triple-maintenance burden — with the door left open to a native WASAPI backend later if PortAudio's latency on Windows proves insufficient for real-time waterfall display.

## Core abstractions

```csharp
namespace Yoniq.Abstractions.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, int MaxInputChannels, int MaxOutputChannels, IReadOnlyList<int> SupportedSampleRates);

public interface IAudioDeviceEnumerator
{
    IReadOnlyList<AudioDeviceInfo> InputDevices { get; }
    IReadOnlyList<AudioDeviceInfo> OutputDevices { get; }
    Task RefreshAsync();
}

public interface IAudioEngine : IAsyncDisposable
{
    Task StartCaptureAsync(AudioDeviceInfo device, int sampleRate, CancellationToken ct);
    Task StopCaptureAsync();
    Task StartPlaybackAsync(AudioDeviceInfo device, int sampleRate, CancellationToken ct);
    Task StopPlaybackAsync();

    // Real-time callback: fires on the audio thread. Must not allocate or block.
    event Action<ReadOnlyMemory<float>>? SamplesCaptured;

    // Pull-based playback: DSP layer enqueues samples; engine drains them on its own callback.
    void EnqueuePlaybackSamples(ReadOnlyMemory<float> samples);
}
```

Samples are always `float` in `[-1.0, 1.0]`, mono, at a DSP-chosen sample rate (default 11025 Hz to match legacy SSTV processing rate, configurable up to 48000 Hz) — format conversion from whatever the device natively supports happens inside the engine, never in `Yoniq.Core.Sstv`.

## Real-time constraint

Per [[01-architecture]]'s concurrency model: the `SamplesCaptured` callback and the internal playback-drain callback run on audio-backend-owned real-time threads. Handlers must:
- not allocate (no LINQ, no boxing, no new arrays per callback — reuse pooled buffers)
- not call `async`/`await` or block on any `Task`
- only ever write into a lock-free single-producer/single-consumer ring buffer (`System.Threading.Channels` bounded channel configured for this, or a hand-rolled ring buffer if `Channels` overhead proves too high) that a normal-priority processing thread drains

This is the mechanism by which "all hardware communication must be asynchronous" is reconciled with audio's hard real-time constraints: the *callback* is synchronous by necessity (that's how every native audio API works), but nothing downstream of the ring buffer blocks it, and all actual processing happens off that thread.

## Device hot-plug

`IAudioDeviceEnumerator.RefreshAsync` is called on a device-change notification from the backend (PortAudio supports device change callbacks on Windows/macOS; Linux/ALSA hot-plug detection is best-effort). UI ([[09-ui]]) subscribes to device list changes and marks the current device unavailable rather than crashing if it disappears mid-session — replacing the legacy behavior of `Sound.cpp` failing silently or requiring app restart on device changes.

## Loopback / virtual cable support

Many SSTV operators route audio through a virtual audio cable (VB-Cable, BlackHole, snd-aloop) to bridge YONIQ and a separate rig-control audio path. No special code is needed for this — a virtual cable simply appears as a normal `AudioDeviceInfo` — but it's called out here because it constrains device selection UX: the device picker must not filter out devices with unusual channel counts or names, since virtual cables often look like exotic devices to enumeration APIs.

## Level metering

TX/RX audio level metering (VU-meter style, visible in the legacy `Scope`/level bar UI) is derived from the same sample stream already flowing through `SamplesCaptured`/`EnqueuePlaybackSamples` — a lightweight peak/RMS calculator subscribes alongside the DSP pipeline, not a separate audio tap, to avoid opening two capture streams.

## Testing

`IAudioEngine` is faked in tests via `FakeAudioEngine`, which lets a test synchronously "play" a `float[]` fixture through `SamplesCaptured` and capture whatever was enqueued via `EnqueuePlaybackSamples` — this is how [[06-sstv-dsp]] round-trip (encode → simulated channel → decode) tests run without any real sound card, and how CI (headless, no audio hardware) exercises the DSP pipeline end-to-end.

## Definition of done

- [ ] `IAudioEngine`/`IAudioDeviceEnumerator` implemented against PortAudio on Windows, Linux, macOS; manually verified capture+playback round-trip on each OS.
- [ ] Ring-buffer hand-off verified allocation-free under a profiler (no Gen0 allocations during steady-state capture).
- [ ] `FakeAudioEngine` implemented and used by at least one [[06-sstv-dsp]] round-trip test.
- [ ] Device hot-plug (unplug during active capture) verified not to crash the app.
