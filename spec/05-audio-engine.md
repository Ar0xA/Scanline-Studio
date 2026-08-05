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

`miniaudio` addresses all three directly: PulseAudio is its highest-priority Linux backend (dlopen'd at runtime, reaching PipeWire via `pipewire-pulse` the same way `pavucontrol` does — virtual sinks and monitor sources included); one backend is selected per context, so Windows enumerates WASAPI's device list once each rather than the same physical device 3-5 times across MME/DirectSound/WASAPI/WDM-KS; its WASAPI backend uses `IAudioClient3` low-latency shared mode on Windows 10+ (this *is* the "native WASAPI backend" escape hatch the older revision of this section held open, without needing COM interop in `ScanlineStudio.Core.*`); and requesting mono `f32` at any rate lets its own data converter handle resampling/downmixing from whatever the device's native format is.

Real costs, accepted knowingly: no official prebuilt native binaries (built from a pinned upstream tag via a small CI matrix instead — see [[13-testing]]/build docs), and a real P/Invoke design constraint that shaped the implementation: `ma_device`'s layout varies both by platform *and* by which `MA_NO_*` compile-time flags this port uses, so no C# struct marshaled directly against miniaudio's own types could ever be safely pinned to a layout (confirmed: there is no `ma_device_sizeof()` to allocate against even if one tried). Resolved by never marshaling miniaudio's structs at all — `ScanlineStudio.Core.Audio.MiniAudio` builds a small C shim (`native/yoniq_audio.c`) alongside the vendored, pinned `miniaudio.h`, exposing this project's own designed ABI (opaque handles, flat POD structs whose offsets the C compiler computes) — P/Invoke targets only that.

`miniaudio`'s real default backend-selection behavior is *not* safe to rely on either, confirmed directly against the pinned header rather than assumed from documentation: the default per-platform priority list tries `sndio`/`audio4`/`oss` *before* `pulseaudio` on some platforms, and always includes a silent `ma_backend_null` as the lowest-priority fallback — meaning an unspecified "default" backend list can silently succeed against the wrong backend, or a fake device that never produces real audio, with no error at all. The shim always passes an explicit, per-platform backend list to `ma_context_init` and exposes the resolved backend's name for diagnostics.

## Core abstractions

```csharp
namespace ScanlineStudio.Abstractions.Audio;

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

    // Blocks (asynchronously) until every previously-enqueued sample has actually played out --
    // not immediate. A caller that stopped mid-buffer would truncate the last scanlines of a real
    // on-air transmission.
    Task StopPlaybackAsync();

    // Fires on a normal-priority drain thread, NOT the audio backend's real-time callback thread --
    // that callback runs entirely inside the native miniaudio shim, writing into a lock-free ring
    // buffer there (see Real-time constraint below). Overrun policy: if this drain thread falls
    // behind and the ring fills, the real-time callback drops the newest incoming frames (never
    // blocks, never overwrites undrained data) -- RX samples are lost, not corrupted or reordered.
    event Action<ReadOnlyMemory<float>>? SamplesCaptured;

    // Pull-based playback: DSP layer enqueues samples; engine drains them on its own callback.
    // Returns how many samples were actually accepted (0..samples.Length) as a synchronous
    // back-pressure signal (same shape as Stream.Write's return) -- callers must retry/wait for
    // the remainder, not assume everything was accepted.
    int EnqueuePlaybackSamples(ReadOnlyMemory<float> samples);
}
```

Samples are always `float` in `[-1.0, 1.0]`, mono, at a DSP-chosen sample rate. **Default is 44100 Hz, not legacy's 11025 Hz** — corrected from an earlier revision of this section during piece Audio 2's contract review: [[14-roadmap]] documents that roughly a third of the SSTV mode table still fails the standard round-trip tolerance at 11025Hz (a pixel-readout/demodulator-settling gap, tracked separately, not this engine's concern), and the existing `ScanlineStudio.Core.Sstv.Tests` suite already runs at 44100Hz as a stated "pragmatic Phase 1 accommodation" for exactly that reason — this section's stated default should match the rate that actually ships, not legacy's rate on principle alone. 11025Hz stays available (any rate up to 48000Hz is) and remains the legacy-authentic choice once the underlying DSP gap closes. Format conversion from whatever the device natively supports happens inside the engine, never in `ScanlineStudio.Core.Sstv`.

`AudioDeviceInfo.Id` stays a plain string despite `miniaudio`'s own device id being a large backend-specific union (a wide string for WASAPI, a GUID for DirectSound, a plain integer for JACK, etc.) — the native shim converts every backend's id into a stable string at the boundary (see Backend choice above), so this interface itself needs no change. The remaining open question is settings persistence ([[12-settings]]): a stored device id is not guaranteed stable across reboots on every backend, so the settings layer should store the device's name alongside its id and fall back to matching by name if the stored id no longer resolves -- not yet implemented, a Phase 3 (UI/settings) concern, not this piece's.

## Real-time constraint

Per [[01-architecture]]'s concurrency model: the actual real-time audio callback runs entirely inside the native `miniaudio` shim (`ScanlineStudio.Core.Audio.MiniAudio`), never inside managed code — no C# delegate is ever invoked from the backend's own real-time thread, sidestepping the GC-transition-on-every-callback hazard a direct P/Invoke callback would otherwise introduce. The callback writes into `miniaudio`'s own lock-free single-producer/single-consumer ring buffer (`ma_pcm_rb`, already implemented and used internally by the library), owned by the shim; a separate, normal-priority managed thread drains that ring and raises `SamplesCaptured`/pulls from the playback ring on `EnqueuePlaybackSamples`'s behalf. Handlers of `SamplesCaptured` should still avoid needless allocation/blocking as good practice (a slow handler still delays RX processing), but the *hard* real-time constraint (no GC transitions, no blocking, ever) applies only to the native callback, which no managed code — including event handlers — runs on.

This is the mechanism by which "all hardware communication must be asynchronous" is reconciled with audio's hard real-time constraints: the *callback* is synchronous by necessity (that's how every native audio API works), but nothing downstream of the ring buffer blocks it, and all actual processing happens off that thread, in managed code, outside the native callback entirely.

## Device hot-plug

Corrected from an earlier revision of this section, which claimed "PortAudio supports device change callbacks on Windows/macOS" — false for any released PortAudio (see Backend choice above); this section originally described a capability the chosen backend at the time didn't actually have. With `miniaudio`, the currently-open device's own notification callback (`ma_device_notification_proc`) is wired in on both the capture and playback session (`MiniAudioCaptureSession`/`MiniAudioPlaybackSession`, `ScanlineStudio.Core.Audio.MiniAudio`).

**Corrected a second time**, this time from empirical testing rather than documentation reading (the roadmap's own piece-7 lesson: a claim can pass a self-consistency check and still be wrong). This section previously claimed the notification callback "covers 'device disappeared mid-session'" — tested directly against a real PulseAudio/PipeWire-pulse virtual sink unloaded while a session was open on it, and found **false**: on this backend, the `stopped` notification only fires from an actual server-side suspend/resume (`ma_device_on_suspended__pulse` in the pinned `miniaudio.h`), never from the device genuinely disappearing. Real hot-unplug on Linux instead shows up as capture going silent (no more `SamplesCaptured` events) or playback's underrun counter climbing — callers must watch for that, not the notification, to detect this specific case. The same testing also found disposing a session whose device had already disappeared could hang indefinitely (miniaudio's PulseAudio backend blocks forever waiting for an operation the server will never complete once the device is gone); both session types now bound their native close call with a timeout instead of trusting it to return. A full `IAudioDeviceEnumerator.RefreshAsync` (re-enumerating the whole device list) can still be called at any time without disrupting an active stream. UI ([[09-ui]]) subscribes to device list changes and marks the current device unavailable rather than crashing if it disappears mid-session — replacing the legacy behavior of `Sound.cpp` failing silently or requiring app restart on device changes.

Not yet verified: whether WASAPI (Windows) and CoreAudio (macOS) behave the same way (silent notification, no timeout) or differently on real device removal — this sandbox is Linux-only, so this is an open item for a human on those OSes, not something to assume from the PulseAudio finding above.

## Loopback / virtual cable support

Many SSTV operators route audio through a virtual audio cable (VB-Cable, BlackHole, snd-aloop) to bridge YONIQ and a separate rig-control audio path. No special code is needed for this — a virtual cable simply appears as a normal `AudioDeviceInfo` — but it's called out here because it constrains device selection UX: the device picker must not filter out devices with unusual channel counts or names, since virtual cables often look like exotic devices to enumeration APIs.

## Level metering

TX/RX audio level metering (VU-meter style, visible in the legacy `Scope`/level bar UI) is derived from the same sample stream already flowing through `SamplesCaptured`/`EnqueuePlaybackSamples` — a lightweight peak/RMS calculator subscribes alongside the DSP pipeline, not a separate audio tap, to avoid opening two capture streams.

## Testing

`IAudioEngine` is faked in tests via `FakeAudioEngine`, which lets a test synchronously "play" a `float[]` fixture through `SamplesCaptured` and capture whatever was enqueued via `EnqueuePlaybackSamples` — this is how [[06-sstv-dsp]] round-trip (encode → simulated channel → decode) tests run without any real sound card, and how CI (headless, no audio hardware) exercises the DSP pipeline end-to-end.

## Definition of done

- [x] Capture and playback implemented against `miniaudio` and verified on Linux (real PipeWire null-sink virtual cable, both directions, `ScanlineStudio.Core.Audio.MiniAudio.Tests`) — device enumeration, capture, playback, resampler-quality, hot-unplug dispose-timeout all covered by real-hardware tests (gated by `RequiresPipeWireFactAttribute` so they degrade to an honest skip, not a hard failure, where no PipeWire/Pulse server is reachable).
- [ ] **Not yet verified: Windows (WASAPI) and macOS (CoreAudio).** This project's dev sandbox is Linux-only — the WASAPI/CoreAudio backend selection compiles (see Backend choice above) but neither has ever actually been run against real or virtual hardware. A human on each OS needs to confirm: capture+playback round-trip works at all; whether the "stopped" notification and the Dispose-hang-on-hot-unplug finding documented under Device hot-plug above are PulseAudio-specific quirks or apply there too (do not assume either way without testing).
- [x] Native `miniaudio` binaries built from a pinned upstream tag (`0.11.25`) via a documented shim (`native/yoniq_audio.c`/`.h`) — license recorded in [LICENSES.md](../LICENSES.md). One MSBuild target per OS (`BuildNativeShimLinux`/`Windows`/`MacOS`) builds with that platform's own toolchain, following miniaudio's own documented per-platform build requirements exactly (no guessed flags). Linux confirmed working (regression-tested every piece in this dev sandbox); Windows (`cl.exe`, plus a CI step to put it on `PATH`) and macOS (`clang`, `-dynamiclib`) are unverified here — real confirmation is the CI matrix itself once pushed, per the open item above.
- [x] Ring-buffer hand-off verified allocation-free on the *drain* side (`MiniAudioRingTests.WriteAndRead_AreAllocationFree_AtSteadyState`, via `GC.GetAllocatedBytesForCurrentThread()` deltas after JIT/tiering warm-up) — reworded from an earlier revision of this bullet, which assumed a managed real-time callback; there isn't one (see Real-time constraint above), so the native callback itself has no managed allocations to measure by construction.
- [x] `FakeAudioEngine` implemented and used by at least one [[06-sstv-dsp]] round-trip test.
- [x] Device hot-plug (unplug during active capture) verified not to crash the app, via a real `pactl unload-module` mid-capture/mid-playback — found and fixed a real Dispose-hang bug in the process (see Device hot-plug above). Windows/macOS equivalent still open, as above.
