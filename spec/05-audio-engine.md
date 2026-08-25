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

Real costs, accepted knowingly: no official prebuilt native binaries (built from a pinned upstream tag via a small CI matrix instead — see [[13-testing]]/build docs), and a real P/Invoke design constraint that shaped the implementation: `ma_device`'s layout varies both by platform *and* by which `MA_NO_*` compile-time flags this port uses, so no C# struct marshaled directly against miniaudio's own types could ever be safely pinned to a layout (confirmed: there is no `ma_device_sizeof()` to allocate against even if one tried). Resolved by never marshaling miniaudio's structs at all — `ScanlineStudio.Core.Audio.MiniAudio` builds a small C shim (`native/scanline_audio.c`) alongside the vendored, pinned `miniaudio.h`, exposing this project's own designed ABI (opaque handles, flat POD structs whose offsets the C compiler computes) — P/Invoke targets only that.

`miniaudio`'s real default backend-selection behavior is *not* safe to rely on either, confirmed directly against the pinned header rather than assumed from documentation: the default per-platform priority list tries `sndio`/`audio4`/`oss` *before* `pulseaudio` on some platforms, and always includes a silent `ma_backend_null` as the lowest-priority fallback — meaning an unspecified "default" backend list can silently succeed against the wrong backend, or a fake device that never produces real audio, with no error at all. The shim always passes an explicit, per-platform backend list to `ma_context_init` and exposes the resolved backend's name for diagnostics.

## Core abstractions

```csharp
namespace ScanlineStudio.Abstractions.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, int MaxInputChannels, int MaxOutputChannels, IReadOnlyList<int> SupportedSampleRates, bool IsDefault = false);

public interface IAudioDeviceEnumerator
{
    IReadOnlyList<AudioDeviceInfo> InputDevices { get; }
    IReadOnlyList<AudioDeviceInfo> OutputDevices { get; }
    Task RefreshAsync(CancellationToken ct = default);
}

public interface IAudioEngine : IAsyncDisposable
{
    // drainThreadPriority/periodSizeInFrames/periods/channelSource/stereoTx (2026-08-07,
    // spec/14-roadmap.md's Phase 4+ backlog) are all-optional, default-preserving-prior-behavior
    // knobs -- capture-drain-thread OS priority, native hardware/backend buffer period tuning
    // (0 = miniaudio's own default), and stereo capture-source-select/TX-duplicate (NOT a
    // confirmed legacy port -- documented as an assumption at every layer that touches it, see
    // AudioChannelSource's own doc comment). Samples stay mono on both sides of this interface
    // regardless -- stereo, where requested, only ever exists between the native device and
    // ScanlineStudio.Core.Audio.MiniAudio's own ring buffers, never crossing this boundary.
    Task StartCaptureAsync(
        AudioDeviceInfo device, int sampleRate, ThreadPriority? drainThreadPriority = null,
        int periodSizeInFrames = 0, int periods = 0, AudioChannelSource channelSource = AudioChannelSource.Mono,
        CancellationToken ct = default);
    Task StopCaptureAsync();

    // Overrun count since the last StartCaptureAsync -- drives the status-bar XRUN readout. Can
    // both throw AND block on a call that races a concurrent stop; see the interface's own doc
    // comment for the exact caveat, not restated here to avoid the two copies drifting apart.
    int CaptureOverrunCount { get; }

    Task StartPlaybackAsync(
        AudioDeviceInfo device, int sampleRate, int periodSizeInFrames = 0, int periods = 0,
        bool stereoTx = false, CancellationToken ct = default);

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

Samples are always `float` in `[-1.0, 1.0]`, mono, at one process-lifetime DSP sample rate shared by capture, decoder, waterfall, encoder, and playback. The default is legacy's **11025 Hz**; the persisted whole-Hz range is **5000 through 48500 inclusive**, matching legacy's `5000.0..CLOCKMAX` boundary while retaining this port's already-established integer audio APIs (legacy also accepted fractional rates, which this port does not). A saved change is restart-required because the decoder/filter object graph is constructed for one immutable rate. Format conversion from whatever the device natively supports happens inside the engine, never in `ScanlineStudio.Core.Sstv`.

Historical test context: the full-mode round-trip matrix remains locked at 44100 Hz as a regression configuration, but the old decoder failures at 11025 Hz were fixed (`AfcTracker`/`SyncIntervalTracker`/`SyncAnchorCorrector` plus per-line cursor rounding), and dedicated 11025 Hz coverage remains. Endpoint self-round-trips also exercise 5000 and 48500 Hz. They are port self-consistency checks, not legacy golden vectors: actual legacy-captured parity at both endpoints remains unverified. At 5000 Hz, Robot 36 currently measures a 16.77 average per-channel delta (pinned under a 20.0 endpoint tolerance rather than the normal 10.0), consistent with RX/TX filter edges at or above the 2500 Hz Nyquist boundary; this limitation is documented rather than silently narrowing the legacy-supported configuration range or changing DSP math in the sample-rate wiring work.

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

Two independent RX level concepts now exist, reading different points in the pipeline for different
consumers — this section previously described only one of them, and (before a 2026-08-22 correction)
had that one's own mechanism wrong:

- **Decoder-internal AGC level**, computed *inside* the decoder from the ported `LevelAgc`, measured
  post-bandpass-filter (not on the raw captured stream), exposed as
  `ISstvDecoder.SignalPeakLevel`/`IsLevelOverdriven` (see [[06-sstv-dsp]]) — feeds the decoder's own
  gain-control math, not a UI meter directly.
- **Raw input peak level** (added 2026-08-24, backs `RadioHeaderView`'s WSJT-X-style RX level meter
  — a plain incoming-audio-level bar, green/red by threshold band, replacing what used to be a
  rig-signal-strength readout there): a genuinely separate, THIRD subscriber to `SamplesCaptured`
  (`SstvSessionService`'s own `_levelMeterHandler`, alongside the existing `_decoderHandler`/
  `_waterfallHandler`), reading the RAW captured buffer's own peak amplitude directly — before any
  bandpass filtering or decode-specific processing — exposed as `ISstvSessionService.RawInputPeakLevel`
  (a `[0.0, 1.0]`-range `volatile float` field, `0.0` whenever capture isn't running so it never shows
  a stale reading), polled by `RadioStatusViewModel` on a 250 ms `DispatcherTimer`, same pattern
  `RxImagePaneViewModel`'s own telemetry timer already used.

TX level metering (VU-meter style, visible in the legacy `Scope`/level bar UI) still **does not
exist at all** — no peak/RMS calculator subscribes to `EnqueuePlaybackSamples`'s stream anywhere in
this codebase. What DOES exist on the TX side is unrelated to level metering: `TxVolumePercent`
("Pwr") is a user-set app-internal GAIN multiplier applied before playback, not a readout of the
signal actually going out.

## Testing

`IAudioEngine` is faked in tests via `FakeAudioEngine`, which lets a test synchronously "play" a `float[]` fixture through `SamplesCaptured` and capture whatever was enqueued via `EnqueuePlaybackSamples` — this is how [[06-sstv-dsp]] round-trip (encode → simulated channel → decode) tests run without any real sound card, and how CI (headless, no audio hardware) exercises the DSP pipeline end-to-end.

## Definition of done

- [x] Capture and playback implemented against `miniaudio` and verified on Linux (real PipeWire null-sink virtual cable, both directions, `ScanlineStudio.Core.Audio.MiniAudio.Tests`) — device enumeration, capture, playback, resampler-quality, hot-unplug dispose-timeout all covered by real-hardware tests (gated by `RequiresPipeWireFactAttribute` so they degrade to an honest skip, not a hard failure, where no PipeWire/Pulse server is reachable).
- [ ] **Not yet verified: Windows (WASAPI) and macOS (CoreAudio).** This project's dev sandbox is Linux-only — the WASAPI/CoreAudio backend selection compiles (see Backend choice above) but neither has ever actually been run against real or virtual hardware. A human on each OS needs to confirm: capture+playback round-trip works at all; whether the "stopped" notification and the Dispose-hang-on-hot-unplug finding documented under Device hot-plug above are PulseAudio-specific quirks or apply there too (do not assume either way without testing).
- [x] Native `miniaudio` binaries built from a pinned upstream tag (`0.11.25`) via a documented shim (`native/scanline_audio.c`/`.h`) — license recorded in [LICENSES.md](../LICENSES.md). One MSBuild target per OS (`BuildNativeShimLinux`/`Windows`/`MacOS`) builds with that platform's own toolchain, following miniaudio's own documented per-platform build requirements exactly (no guessed flags). Linux confirmed working (regression-tested every piece in this dev sandbox); Windows (`cl.exe`, plus a CI step to put it on `PATH`) and macOS (`clang`, `-dynamiclib`) are unverified here — real confirmation is the CI matrix itself once pushed, per the open item above.
- [x] Ring-buffer hand-off verified allocation-free on the *drain* side (`MiniAudioRingTests.WriteAndRead_AreAllocationFree_AtSteadyState`, via `GC.GetAllocatedBytesForCurrentThread()` deltas after JIT/tiering warm-up) — reworded from an earlier revision of this bullet, which assumed a managed real-time callback; there isn't one (see Real-time constraint above), so the native callback itself has no managed allocations to measure by construction.
- [x] `FakeAudioEngine` implemented and used by at least one [[06-sstv-dsp]] round-trip test.
- [x] Device hot-plug (unplug during active capture) verified not to crash the app, via a real `pactl unload-module` mid-capture/mid-playback — found and fixed a real Dispose-hang bug in the process (see Device hot-plug above). Windows/macOS equivalent still open, as above.
