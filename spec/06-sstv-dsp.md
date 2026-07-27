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

- **Tone detection / FM demodulation** (`sstv.cpp`'s `CPLL`/`CVCO`/`CFQC` + `fir.cpp`'s `CIIR`/`MakeIIR` equivalent): **implemented, ported directly** — per CLAUDE.md's "port first, invent second" rule for DSP/codec math, `Yoniq.Core.Sstv` contains a faithful port of the legacy closed-loop PLL FM discriminator (`PllFmDemodulator`, `Vco`, `IirFilter`), not an invented alternative. An earlier attempt at a from-scratch open-loop quadrature-mixing discriminator with a hand-tuned smoothing constant was scrapped after review — it had no track record, needed trial-and-error tuning, and (unsurprisingly in hindsight) performed worse than the legacy design, which is specifically tuned around per-pixel windows as short as ~5 samples at 11025Hz. See `PllFmDemodulator`'s doc comment for the exact structure (AGC → loop IIR filter → VCO → multiplying phase detector → output IIR filter) and the Hz-conversion note (legacy returns an internal-scale value; this port converts to Hz for this codebase's own pixel-mapping, not yet cross-checked against a captured legacy golden vector — see [[13-testing]]).
- **FIR filtering** (`fir.cpp`'s standalone `MakeIIR`/biquad-cascade application, distinct from the PLL's internal loop/output filters): ported as `IirFilter`, used directly by `PllFmDemodulator`; a separate general-purpose bandpass stage ahead of the demodulator (as opposed to the PLL's own internal filtering) is not yet implemented.
- **Waterfall/scope** (`Scope.cpp` equivalent): not yet implemented. `IWaterfallSource` (rolling FFT magnitude frames, independent of decode state per the legacy-coupling fix described below) remains a Phase 3 UI-adjacent concern — see [[14-roadmap]].
- **VIS auto-detection**: implemented (`VisHeader`), decoding the standard leader-break-leader/start-bit/7-data-bit/parity/stop-bit sequence: see the caveat below.
- **Scanline reconstruction**: implemented for Martin M1 only (see [[14-roadmap]] for remaining modes), using the mode's *nominal* timing rather than independently re-detecting each line's sync pulse — the legacy AFC/sync state machine (`CSSTVDEM`) that does real sync-search and clock-drift tracking is a separate, larger piece of work not yet ported. Fine for a same-process, no-channel-noise proof; real captured audio needs that plus the slant correction below before this is usable on the air.

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

- [x] `SstvModeDefinition` table covers Martin M1/M2, Scottie S1/S2/DX — each independently cross-checked against legacy `CSSTVSET::GetTiming`'s total-line-duration return value, not just self-consistent. Channel order (Scottie R,G,B vs. Martin G,B,R) and VIS codes read directly from `Main.cpp`'s RX decode switch and VIS lookup table, not recalled from general SSTV knowledge. — [ ] remaining modes not yet added: Robot 36/72 (BW+chroma scheme), the whole PD-series (YCbCr, two-lines-per-transmission-line structure), Pasokon P3/P5/P7, the "half-scan-chroma" MR/ML/MP/MN/MC/R24/RM8/RM12 families, AVT — see [[14-roadmap]].
- [x] Encode → decode round-trip test passes for Martin M1/M2, Scottie S1/S2/DX within tolerance (10.0 average per-channel delta, file-based via `WavFile`) — **caveat**: Martin M1/Scottie S1/Scottie DX pass at the legacy-matching default 11025Hz; Martin M2/Scottie S2 (shorter per-pixel scan time) need 44100Hz with this port's current fixed PLL filter tuning. Legacy likely adapts filter tuning per mode speed (a user-configurable "demod profile" system, `PRODEM` in `Main.cpp`) that isn't ported yet — see `SstvModeRegistry`'s doc comment. Not silently worked around: flagged as a follow-up to find and port that adaptive tuning, rather than accepting 44100Hz as a permanent requirement for fast modes.
- [ ] Decoder sustains real-time throughput on a defined reference machine spec (documented, benchmarked in CI where feasible) — not yet measured.
- [ ] Waterfall rendering verified decoupled from decode (decode continues correctly with waterfall UI closed) — no waterfall implementation yet to verify against.
