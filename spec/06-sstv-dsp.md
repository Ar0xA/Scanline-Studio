# SSTV DSP / Modem Core

## Related

[[05-audio-engine]] (sample source/sink) · feeds → [[07-image-pipeline]] · replaces `sstv.cpp`/`sstv.h`, `Fft.cpp`, `Scope.cpp`, `fir.cpp`

## Purpose

This is the heart of the application: encoding an image into an audio waveform for transmission, and decoding a received audio waveform back into an image, plus the visualization (waterfall/scope) operators use to tune in a signal. This layer has no knowledge of radios, UI, or files — pure sample-stream-in/sample-stream-or-image-out.

## Mode definitions

Every SSTV mode (Martin M1/M2, Scottie S1/S2/DX, Robot 36/72, PD-series, etc.) is data, not code, wherever possible:

```csharp
namespace Yoniq.Abstractions.Sstv;

public sealed record SstvModeDefinition(
    string Id,                     // "martin-m1"
    string DisplayName,
    int ImageWidth,
    int ImageHeight,
    double LineDurationMs,
    ColorEncoding ColorEncoding,   // RGB sequential, YCrCb, etc.
    IReadOnlyList<SyncPulseSpec> SyncPulses,
    IReadOnlyList<ChannelSpec> ScanChannels);

public interface ISstvEncoder
{
    IAsyncEnumerable<float> EncodeAsync(SstvModeDefinition mode, IImageSource image, CancellationToken ct);
}

public interface ISstvDecoder
{
    // Fed continuously from the audio engine's ring buffer; raises events as sync/lines are detected.
    void PushSamples(ReadOnlyMemory<float> samples);
    event Action<DecodedImageUpdate>? LineDecoded;   // incremental, so partial images render live like the legacy RxView
    event Action<SstvModeDefinition>? ModeDetected;  // auto mode detection via sync pulse analysis
}
```

Mode timing/frequency tables are ported from the legacy per-mode constants scattered through `sstv.cpp` into a single `modes.json` (or embedded resource) table read by `SstvModeDefinition`, so adding a mode is a data change, not a recompile — this is the concrete DSP-layer expression of "prefer composition over inheritance" from CLAUDE.md: modes are configured, not subclassed.

## Signal chain

```
Encode:  IImageSource → per-mode scanline sampler → FM/AFSK tone synthesizer → float[] samples → IAudioEngine.EnqueuePlaybackSamples
Decode:  IAudioEngine.SamplesCaptured → FIR bandpass filter → FFT/Goertzel tone detector → sync detector → scanline reconstructor → DecodedImageUpdate
```

- **FIR filtering** (`fir.cpp` equivalent): `Yoniq.Core.Sstv.Filters.FirFilter`, coefficient tables generated at startup (windowed-sinc design) rather than the legacy precomputed constant tables, so filter bandwidth is tunable without recompiling.
- **Tone detection** (`Fft.cpp` equivalent): a hybrid approach — sliding-window Goertzel algorithm for the narrow-band sync/color-subcarrier tracking (cheaper than full FFT for a handful of known frequencies) plus a real FFT (via `System.Numerics.Tensors`-friendly library or a small dependency-free radix-2 FFT) for the waterfall display's full-spectrum view.
- **Waterfall/scope** (`Scope.cpp` equivalent): `IWaterfallSource` produces rolling FFT magnitude frames independent of decode state, purely for visualization — decode does not depend on waterfall rendering being active, and waterfall rendering does not depend on a decode being in progress. This split fixes a legacy coupling where the scope view and the decoder shared buffer/timing state.

## Auto mode detection

Legacy MMSSTV auto-detects mode from the leading VIS (Vertical Interval Signaling) code tone sequence. `ISstvDecoder` implements the same VIS-code detection as its primary mode-detection path (`ModeDetected` fires once VIS is decoded), with manual mode override always available in the UI for weak-signal cases where VIS decode fails — matching legacy behavior of allowing forced mode selection.

## Sample clock calibration (slant correction)

**Correctness requirement, not an enhancement** — omitted from the original draft of this document, added after review. Received images slant when the transmitting and receiving stations' sound card sample clocks disagree even slightly, because SSTV timing is derived entirely from the audio sample rate. Legacy MMSSTV addresses this two ways, both of which must be ported, not just the DSP math:

- **Manual calibration** (`ClockAdj.cpp`, `SampFreq`/`m_SampFreq` in `sstv.h`, default `1.102500e+04`): a user-facing dialog that nudges the effective sample rate against a known reference (e.g. WWV/time-standard tone) and persists the correction.
- **Independent TX offset** (`m_TxSampFreq`, `TxSampOffset` setting): TX and RX clocks are calibrated separately, since a sound card's playback and capture clocks can themselves disagree.

In the rewrite, this is a property of `ISstvDecoder`/`ISstvEncoder` (an effective-sample-rate correction factor, applied before the FIR/tone-detection stage), exposed as a user-adjustable setting via [[12-settings]], not hardcoded to the device's nominal sample rate. A decoder implementation that ignores this will decode correctly against synthetic test fixtures (which have no clock drift) while producing visibly slanted images against any real off-nominal sound card — which is exactly why this needs its own explicit test (a fixture waveform encoded with a deliberately offset sample rate, asserting the decoder's calibration correctly straightens it), not just the nominal-rate round-trip test above.

## Station identification (FSK/CW ID)

Legacy support for transmitting station ID as FSK (`OutputFSKID`, `Main.h:1144`, `fskid.txt`) or CW (`OutputCWID`, `sstv.h:832`, `WriteCWID`) alongside/after an SSTV transmission — in some jurisdictions a regulatory requirement, not optional polish. `ISstvEncoder` (or a small sibling `IStationIdEncoder`) generates the corresponding tone sequence for the configured callsign, appended to the TX sample stream. Required for v1 (see [[14-roadmap]]) given the regulatory angle, unlike the deferred items below.

## Explicitly deferred (not v1)

SSTV repeater/beacon mode (`RepSet.cpp`, `Repeater.txt`, unattended relay/beacon transmission) is real legacy functionality with no spec coverage here — deferred to post-v1 rather than silently dropped; see [[14-roadmap]] and [docs/removed-features.md](../docs/removed-features.md).

## Real-time budget

Decode must keep up with live audio in real time on modest hardware (this was true of the original Pentium-era MMSSTV and must remain true). The processing thread consuming the audio ring buffer (see [[05-audio-engine]]) runs the filter → detect → reconstruct pipeline per incoming block; block size is chosen (default ~256 samples at the DSP sample rate) to bound decode latency to well under one scanline duration for every supported mode.

## Testing

This layer is the most amenable to deterministic testing in the whole system:

```csharp
[Theory]
[InlineData("martin-m1")]
[InlineData("scottie-s1")]
[InlineData("robot-36")]
public async Task Encode_Then_Decode_RoundTrips_Image(string modeId)
{
    var mode = ModeRegistry.Find(modeId);
    var sourceImage = TestImages.LoadFixture("smpte-bars-320x256.png");

    var samples = await encoder.EncodeAsync(mode, sourceImage, CancellationToken.None).ToArrayAsync();
    decoder.PushSamples(samples);

    var decoded = await WaitForDecodedImage(decoder);
    decoded.Should().MatchWithinTolerance(sourceImage, maxDeltaE: 4.0);
}
```

Additional targeted tests: FIR filter frequency response (verify passband/stopband against known coefficients), Goertzel tone detection against synthetic tone bursts with added noise (verify detection holds down to a defined SNR threshold), VIS code detection against all standard VIS codes plus corrupted/partial ones (must not false-positive), and a deliberately clock-offset fixture verifying slant correction (see above).

**Golden vectors, not just round-trip tests.** Per CLAUDE.md's behavioral-parity rule: an encode→decode round-trip test can pass while both the encoder and decoder are wrong in the same compensating way — it does not prove the new implementation matches the *legacy* implementation's actual output, which matters because the legacy DSP core is `double`-precision throughout (`m_SampFreq`, `m_TxSampFreq`, all filter/FFT intermediates) while this spec proposes `float` samples. At minimum, capture intermediate outputs from a real run of the legacy binary (FIR filter output for a known input, FFT magnitude frame for a known tone, final decoded pixel values for a known transmitted image) and check the new implementation against them within a documented tolerance — this is the only way to catch a systematic numeric drift that a self-consistent round-trip test structurally cannot detect.

## Definition of done additions

- [ ] Slant correction implemented and covered by a clock-offset fixture test.
- [ ] FSK/CW station ID implemented for v1.
- [ ] At least one golden-vector test comparing against legacy-binary-captured output exists for the filter stage and the full decode path, with a documented tolerance.

## Definition of done

- [ ] `SstvModeDefinition` table covers at minimum Martin M1/M2, Scottie S1/S2/DX, Robot 36, PD90/120 — the modes actually exercised by the legacy default mode list.
- [ ] Encode → decode round-trip test passes for every mode in the table within defined tolerance.
- [ ] Decoder sustains real-time throughput on a defined reference machine spec (documented, benchmarked in CI where feasible).
- [ ] Waterfall rendering verified decoupled from decode (decode continues correctly with waterfall UI closed).
