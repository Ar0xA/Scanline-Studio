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
- **Waterfall/scope** (`Scope.cpp` equivalent): implemented in Phase 3, `Yoniq.Core.Sstv.WaterfallSource` — rolling FFT magnitude frames (a new standard radix-2 FFT + Hann window, not a legacy port — see [[09-ui]]'s Aesthetic Directive note and `RadixTwoFft`'s own doc comment for why CLAUDE.md's port-first rule doesn't apply to a visualization feature), independent of decode state by construction (see the Definition-of-done item below). Rendered by `Yoniq.UI.Controls.WaterfallControl`, deliberately kept visually simple (flat grayscale, fixed dB range) — the user explicitly deprioritized waterfall polish relative to RX/TX image handling and templating.
- **VIS auto-detection**: implemented (`VisHeader`), decoding the standard leader-break-leader/start-bit/7-data-bit/parity/stop-bit sequence: see the caveat below.
- **Scanline reconstruction**: implemented for Martin M1 only (see [[14-roadmap]] for remaining modes), using the mode's *nominal* timing rather than independently re-detecting each line's sync pulse — the legacy AFC/sync state machine (`CSSTVDEM`) that does real sync-search and clock-drift tracking is a separate, larger piece of work not yet ported. Fine for a same-process, no-channel-noise proof; real captured audio needs that plus the slant correction below before this is usable on the air.

## Auto mode detection

Legacy MMSSTV auto-detects mode from the leading VIS (Vertical Interval Signaling) code tone sequence. `ISstvDecoder` implements the same VIS-code detection as its primary mode-detection path (`ModeDetected` fires once VIS is decoded), with manual mode override always available in the UI for weak-signal cases where VIS decode fails — matching legacy behavior of allowing forced mode selection.

## Sample clock calibration (slant correction)

**Correctness requirement, not an enhancement** — omitted from the original draft of this document, added after review. Received images slant when the transmitting and receiving stations' sound card sample clocks disagree even slightly, because SSTV timing is derived entirely from the audio sample rate. Legacy MMSSTV addresses this **three** ways — an earlier version of this section only found two, both manual, and classified the whole feature as UI/out-of-scope for "legacy is truth" DSP work; an independent Opus verification pass caught that there's also a fully automatic path, which is a genuine DSP correctness feature and has now been ported:

- **Auto Slant — DONE** (`Main.cpp`'s `AutoStopJob`, `KRSA->Checked` branch, on by default in the shipped `Mmsstv.ini`: `AutoSlant=1`): fully automatic, per-line, no user interaction — a resonant sync-tone envelope detector finds where the sync pulse falls within each line, a least-squares trend fit plus a staged confidence-threshold ladder decides when observed drift is real, and a corrected effective sample rate is applied going forward. Ported as `TankFilter`/`SyncEnvelopeDetector`/`SlantTracker`, wired into `AnalogFmSstvDecoder`. See [[14-roadmap]]'s Phase 1 entry for the full port story, including two real bugs a mistuned-rate integration test caught (an internal formula desync after the first correction, and slant-tracking running ahead of pixel decode into non-image content) that no isolated unit test could have found. Verified: a realistic clock mismatch (500ppm) decodes within normal round-trip tolerance; a deliberately severe 1% mismatch converges but doesn't fully correct, which was traced to and confirmed as a real characteristic of legacy's own algorithm (it locks after 5 confidence tiers fire, rather than continuing to re-adjust for the rest of a long transmission), not a gap in the port.
- **Manual calibration — deferred, UI work** (`ClockAdj.cpp`, `SampFreq`/`m_SampFreq` in `sstv.h`, default `1.102500e+04`): a settings dialog that nudges the effective sample rate against a known reference (e.g. WWV/time-standard tone) and persists the correction. Genuinely a UI workflow (a dialog + persisted setting), not DSP math to port faithfully — pick up when building the corresponding settings UI, not before.
- **Manual "drag the visible slant" tool — deferred, UI work** (`Main.cpp`'s `m_Slant`/`GetSyncSamp`/`DrawSync`/`PBoxD12MouseMove`/`PBoxD12MouseDown`, `Main.cpp:5887-5913, 6415-6455`): a diagnostic "Sync" view where the user visually sees the sync pulse drift across lines and drags a reference line to compute a correction — the same underlying proportional math as Auto Slant, but manually triggered and manually measured via mouse coordinates. The interaction itself (a legacy VCL canvas + mouse-drag design) is squarely UI, not something to replicate literally; when this gets built, treat legacy's version as one reference point, not a spec to match pixel-for-pixel, per the user's explicit scoping of "legacy is truth" to protocol/DSP work only, not UI.
- **Independent TX offset** (`m_TxSampFreq`, `TxSampOffset` setting): TX and RX clocks are calibrated separately, since a sound card's playback and capture clocks can themselves disagree. Not yet ported; relevant once TX/radio integration (Phase 2+) exists, not before.

## Station identification (FSK/CW ID)

**Status correction (explicit user decision, see [[14-roadmap]]'s Phase 1 entry)**: this section used to say FSK/CW ID was "required for v1... unlike the deferred items below," on a regulatory-requirement rationale. The user has explicitly decided otherwise for this port: CW ID specifically is real legacy functionality but not required for the program to function as an SSTV encoder/decoder, so it's deferred like the items in the section below, not treated as a v1 blocker. General principle going forward, not just for this feature: if something is present in legacy but not load-bearing for basic SSTV operation, flag it and ask before treating it as in-scope, rather than assuming regulatory or "legacy had it" framing makes it required.

The **post-image footer tone** (`Main.cpp:6994-7013`, the `!sys.m_TXFSKID` branch — a trailing-carrier hold plus, for non-narrow modes, an alternating 1900/1500Hz tone sequence) is **done** (`AnalogFmSstvEncoder.GenerateFooterSegments`) — it's not station-ID-specific (legacy emits it whether or not FSK ID is configured) and was small enough to implement directly rather than deferring, so long as it's implemented in a way that composes with the deferred pieces once they exist (see below).

**Deferred** (real legacy functionality, explicitly scoped out for now, to be picked up as its own task later — not silently dropped, per CLAUDE.md's removal rule):

- **FSK ID TX** (`OutputFSKID`, `Main.cpp:6904-6965`): reuses the `WriteFSK` mechanism already ported for the MN/MC narrow-mode-announce packet (`VisHeader.GenerateFskBits`) — leader/guard tone, `WriteFSK(0x2a)` (STX, distinct from the narrow packet's `0x2d`), each callsign character as `WriteFSK(char - 0x20)`, `WriteFSK(0x01)` (EOT), then an XOR checksum byte, then a trailing guard tone.
- **FSK ID's optional signal-report appendix** (`Main.cpp:6926-6963`, gated on `Log.m_LogSet.m_FSKNR`): a second, separately-checksummed packet appending a signal report, with a real branch in the format — a compact 3-byte numeric encoding for a plain RST-style report vs. a raw-text fallback for anything else. Non-trivial parsing logic (`IsAlphas`, `sscanf` pattern), worth scoping as its own sub-task rather than assuming it's simple once the base FSK ID exists.
- **FSK ID RX**: legacy's `DecodeFSK` (`sstv.cpp`) — the `0x2a`-prefixed case (distinct from the narrow-mode packet's `0x2d` case, already ported and cross-referenced in `VisHeader.NarrowStxByte`'s doc comment). Not yet traced in detail.
- **CW ID** (`OutputCWID`/`WriteCWID`, `Main.cpp:6967-6981`, `sstv.h:832`): a wholly separate Morse-code generator — dit/dah timing from a configured speed, its own tone, and `MacroText` expansion (date/callsign placeholders) applied to the configured CW ID text before encoding. Nothing already in this port is reusable here; this is new DSP work, not an extension of `WriteFSK`.
- **The "FSK ID configured" footer branch** (`Main.cpp:7010-7012`: `mp->Write(fTxNarrow ? 1900 : 1500, 300)`), which only runs once FSK ID exists — the currently-implemented footer only covers the `!sys.m_TXFSKID` case (see above).
- **Settings/config surface**: legacy drives all of the above from `sys.m_Call` (callsign), `sys.m_CWIDText`, `sys.m_TXFSKID`, `sys.m_CWID` (0/1/2 selecting none/CW/`OutputMMV`, the last of which is a voice-ID variant likely out of scope entirely). Per the layered architecture ([[01-architecture]]), these belong in `Yoniq.Application`/[[12-settings]], not `Yoniq.Core.Sstv` — `ISstvEncoder`/a sibling `IStationIdEncoder` should take an already-resolved callsign/ID string, not own the settings surface itself.
- **VOX-mode footer/ID variant** (`sys.m_VOX`, default off — `Main.cpp:822`): not modeled by this port at all (no radio/PTT layer exists yet). The currently-implemented footer always takes the non-VOX branch, matching legacy's own default; if VOX support is ever added at the radio layer, the footer's branch condition needs revisiting alongside it.

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
- [x] Post-image footer tone implemented (`AnalogFmSstvEncoder.GenerateFooterSegments`) — the non-station-ID part of what this item used to cover.
- [ ] FSK/CW station ID: **explicitly deferred**, not a v1 blocker (user decision, see the "Station identification" section above) — CW ID specifically is real legacy functionality but not required for the program to function as an SSTV encoder/decoder. Broken into sub-tasks there (FSK ID TX/RX, the signal-report appendix, CW ID, settings surface) for whenever it's picked up.
- [ ] At least one golden-vector test comparing against legacy-binary-captured output exists for the filter stage and the full decode path, with a documented tolerance.

## Definition of done

- [x] `SstvModeDefinition` table covers Martin M1/M2, Scottie S1/S2/DX, Robot 36 — each independently cross-checked against legacy `CSSTVSET::GetTiming`'s total-line-duration return value, not just self-consistent. Channel order and VIS codes read from the matching legacy source per CLAUDE.md's TX/RX-split rule: **TX line-generator functions** (`Main.cpp`'s `LineMRT`/`LineSCT`/`LineR36`), not RX decode branches — an earlier Scottie entry built by inferring TX order from RX branch widths had the right *duration* but the wrong *channel order and sync placement* (fixed once `LineSCT` was read directly; see `SstvModeRegistry`'s doc comment for the full story). — [ ] remaining modes not yet added: Robot 72, the whole PD-series (YCbCr, two-lines-per-transmission-line structure), Pasokon P3/P5/P7, the "half-scan-chroma" MR/ML/MP/MN/MC/R24/RM8/RM12 families, AVT — see [[14-roadmap]].
- [x] Encode → decode round-trip test passes for Martin M1/M2, Scottie S1/S2/DX, Robot 36 within tolerance (10.0 average per-channel delta, file-based via `WavFile`) — **caveat, corrected**: an earlier version of this note claimed legacy "likely adapts filter tuning per mode speed" via a user-configurable "demod profile" system named `PRODEM` — that name doesn't exist anywhere in legacy source; it was an unverified guess written down as fact. Directly verified instead: legacy's `CPLL` uses one fixed tuning for every mode (only a Normal/Narrow band-range toggle exists, already handled via `LuminanceMinHz`/`MaxHz`), at the same 11025Hz default, for every mode including Martin M2/Scottie S2. The actual reason those modes (and roughly a third of the full mode table) need 44100Hz here is this port's own decoder using block-averaging over a fixed nominal-timing window instead of legacy's real per-line sync-locked sampling — a resolution floor around ~4 samples/pixel at 11025Hz, not a missing per-mode tuning table. See `SstvModeRegistry`'s doc comment and [[14-roadmap]]'s Phase 1 entry for the full measured investigation. Fixing this for real means porting the `CSSTVDEM` sync-search/AFC pipeline, not finding a nonexistent adaptive-tuning feature.
- [x] **Architecture**: per-family codec composition, not a single generic interpreter — `IScanlineEncoder`/`IScanlineDecoder` strategies selected via `ScanlineCodecFactory` based on `SstvModeDefinition.ColorEncoding`, sharing common infrastructure (VIS header, phase accumulation, PLL demodulation) in `AnalogFmSstvEncoder`/`AnalogFmSstvDecoder`. Added when Robot 36's YCbCr-with-alternating-chroma scheme proved it couldn't be expressed as the same fixed-shape channel-scan sequence Martin/Scottie use. `RgbSequentialScanlineEncoder`/`Decoder` (Martin/Scottie family) and `RobotScanlineEncoder`/`Decoder` (Robot family, with `YCbCr.cs`'s `ToRgb`/`FromRgb` — `ToRgb` a direct port of legacy `YCtoRGB`, `FromRgb` the exact mathematical inverse of that same matrix since legacy's forward `GetRY` function turned out to use a *different*, independently-chosen coefficient set, not simply the inverse of its own decode matrix) are the two families implemented so far. New families (PD, Pasokon, etc.) add a new strategy pair, not changes to the shared shell.
- [ ] Decoder sustains real-time throughput on a defined reference machine spec (documented, benchmarked in CI where feasible) — not yet measured.
- [x] Waterfall rendering verified decoupled from decode (decode continues correctly with waterfall UI closed) — true by construction: `IWaterfallSource` has no dependency on `ISstvDecoder` and vice versa (see `IWaterfallSource`'s own doc comment), not just tested as a coincidence.
