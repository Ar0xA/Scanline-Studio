# Audio Engine

## Related

[[01-architecture]] · feeds → [[06-sstv-dsp]] · replaces `Sound.cpp`/`Sound.h` (Windows `waveIn`/`waveOut`/DirectSound)

## Purpose

Cross-platform capture/playback of the audio stream that carries SSTV (and, incidentally, the sound card's role as the PTT-adjacent TX audio path). This is the highest-risk cross-platform port: the legacy `Sound.cpp` is built directly on Windows multimedia APIs, which have no equivalent on Linux/macOS.

## Backend choice

A native cross-platform audio library is used behind the abstraction below rather than P/Invoking OS-specific APIs three times. **`miniaudio`** (public domain / MIT-0, single-file C library, actively maintained) is the chosen backend — reached by process, not by default: PortAudio was the original candidate, but was rejected after direct, independent verification found it fails this spec's own requirements, not just a style preference:

- **No real device-change API.** `PaDevicesChangedCallback` has never shipped in any PortAudio release (it exists only as an unimplemented design proposal); the only way to refresh the device list is `Pa_Terminate()`/`Pa_Initialize()`, which tears down every open stream — incompatible with "device hot-plug must not crash an active capture," and (per [PortAudio issue #564](https://github.com/PortAudio/portaudio/issues/564)) `Pa_Initialize()` alone has been observed disrupting audio in *other* running applications on Windows.
- **No PulseAudio/PipeWire host API, confirmed by direct experiment, not just documentation.** PortAudio 19.x's Linux support is ALSA (+ JACK) only. Verified concretely: a virtual sink was created (`pactl load-module module-null-sink sink_name=sstv_test_cable`), clearly visible via `pactl list short sinks` — and a PortAudio device probe against the exact same system saw no trace of it at all, exposing only a single generic `pipewire`/`pulse` catch-all ALSA PCM alias with no way to select that specific cable. This is the *literal, explicitly-required* virtual-audio-cable workflow (see below) failing in practice, not in theory.
- **No sample-rate conversion.** With PortAudio, "format conversion happens inside the engine" (below) would mean hand-writing a resampler; WASAPI shared mode won't open at 11025Hz without a Windows-specific `PaWasapiStreamInfo` extension (a second platform-specific binding beyond ordinary host-API work), or falling back to legacy MME's much higher latency — precisely the failure mode this section's older revision worried about.

`miniaudio` addresses all three directly: PulseAudio is its highest-priority Linux backend (dlopen'd at runtime, reaching PipeWire via `pipewire-pulse` the same way `pavucontrol` does — virtual sinks and monitor sources included); one backend is selected per context, so Windows enumerates WASAPI's device list once each rather than the same physical device 3-5 times across MME/DirectSound/WASAPI/WDM-KS; its WASAPI backend uses `IAudioClient3` low-latency shared mode on Windows 10+ (this *is* the "native WASAPI backend" escape hatch the older revision of this section held open, without needing COM interop in `Yoniq.Core.*`); and requesting mono `f32` at any rate lets its own data converter handle resampling/downmixing from whatever the device's native format is.

Real costs, accepted knowingly: no official prebuilt native binaries (built from a pinned upstream tag via a small CI matrix instead — see [[13-testing]]/build docs), and a larger, more failure-prone P/Invoke surface than PortAudio's tiny flat structs would have been (`ma_device_config`/`ma_device_info` are large, and there is no `ma_device_sizeof()` to allocate against safely — mitigated by vendoring known-good generated struct layouts pinned to the exact miniaudio version used, plus deliberate over-allocation for `ma_device` itself).

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

Corrected from an earlier revision of this section, which claimed "PortAudio supports device change callbacks on Windows/macOS" — false for any released PortAudio (see Backend choice above); this section originally described a capability the chosen backend at the time didn't actually have. With `miniaudio`: the currently-open device's own notification callback (`ma_device_notification_proc`) fires `stopped`/`rerouted`/interruption events on the live device without needing to tear down the stream, covering "device disappeared mid-session" and default-device changes directly. A full `IAudioDeviceEnumerator.RefreshAsync` (re-enumerating the whole device list, not just reacting to the current device) can be called at any time without disrupting an active stream — unlike PortAudio, this genuinely doesn't require restarting anything. UI ([[09-ui]]) subscribes to device list changes and marks the current device unavailable rather than crashing if it disappears mid-session — replacing the legacy behavior of `Sound.cpp` failing silently or requiring app restart on device changes.

## Loopback / virtual cable support

Many SSTV operators route audio through a virtual audio cable (VB-Cable, BlackHole, snd-aloop) to bridge YONIQ and a separate rig-control audio path. No special code is needed for this — a virtual cable simply appears as a normal `AudioDeviceInfo` — but it's called out here because it constrains device selection UX: the device picker must not filter out devices with unusual channel counts or names, since virtual cables often look like exotic devices to enumeration APIs.

## Level metering

TX/RX audio level metering (VU-meter style, visible in the legacy `Scope`/level bar UI) is derived from the same sample stream already flowing through `SamplesCaptured`/`EnqueuePlaybackSamples` — a lightweight peak/RMS calculator subscribes alongside the DSP pipeline, not a separate audio tap, to avoid opening two capture streams.

## Testing

`IAudioEngine` is faked in tests via `FakeAudioEngine`, which lets a test synchronously "play" a `float[]` fixture through `SamplesCaptured` and capture whatever was enqueued via `EnqueuePlaybackSamples` — this is how [[06-sstv-dsp]] round-trip (encode → simulated channel → decode) tests run without any real sound card, and how CI (headless, no audio hardware) exercises the DSP pipeline end-to-end.

## Definition of done

- [ ] `IAudioEngine`/`IAudioDeviceEnumerator` implemented against `miniaudio` on Windows, Linux, macOS; manually verified capture+playback round-trip on each OS (Linux verified via a real PipeWire null-sink loopback in CI/dev sandboxes; Windows/macOS need a human on that OS — this environment cannot close those out).
- [ ] Native `miniaudio` binaries built from a pinned upstream tag with a documented, reproducible build recipe (not opaque prebuilt blobs of unrecorded provenance) — recorded in [LICENSES.md](../LICENSES.md).
- [ ] Ring-buffer hand-off verified allocation-free (`GC.GetAllocatedBytesForCurrentThread()` deltas around the callback body at steady state, after JIT/tiering warm-up — a real profiler if one becomes available in the build environment, this measurement otherwise).
- [x] `FakeAudioEngine` implemented and used by at least one [[06-sstv-dsp]] round-trip test.
- [ ] Device hot-plug (unplug during active capture) verified not to crash the app — partially verifiable here via `pactl unload-module` mid-capture; not equivalent to a real USB unplug on Windows/macOS.
