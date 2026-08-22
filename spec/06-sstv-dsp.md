# SSTV DSP / Modem Core

## Related

[[05-audio-engine]] (sample source/sink) · feeds → [[07-image-pipeline]] · replaces `sstv.cpp`/`sstv.h`, `Fft.cpp`, `Scope.cpp`, `fir.cpp`

## Purpose

This is the heart of the application: encoding an image into an audio waveform for transmission, and decoding a received audio waveform back into an image, plus the visualization (waterfall/scope) operators use to tune in a signal. This layer has no knowledge of radios, UI, or files — pure sample-stream-in/sample-stream-or-image-out.

## Mode definitions

Every SSTV mode (Martin M1/M2, Scottie S1/S2/DX, Robot 36/72, PD-series, etc.) is data-shaped, not
one-class-per-mode — but it's **compiled-in C#, not an external data file**: each mode is a
`static readonly SstvModeDefinition` field in `ScanlineStudio.Core.Sstv.SstvModeRegistry`. Adding a
mode is a recompile, not a data change; there is no `modes.json`/embedded-resource table. Real
shape (`ScanlineStudio.Abstractions/Sstv/SstvModeDefinition.cs`), corrected from an earlier draft
that named two types (`SyncPulseSpec`/`ChannelSpec`) that were never actually built this way:

```csharp
namespace ScanlineStudio.Abstractions.Sstv;

public sealed record SstvModeDefinition(
    string Id,                          // "martin-m1"
    string DisplayName,
    int VisCode,
    int ImageWidth,
    int ImageHeight,
    ColorEncoding ColorEncoding,        // RgbSequential, YCbCrRobot, YCbCrSequential, YCbCrLinePaired, MonoAveragedPaired
    IReadOnlyList<LineSegment> LineSegments,
    double LuminanceMinHz = 1500,
    double LuminanceMaxHz = 2300,
    int? ExtendedVisCode = null,        // non-null for the MR/MP/ML family (two-byte "extended VIS")
    int? NarrowModeCode = null)         // non-null for the MN/MC family (no VIS at all -- a fixed 4-byte FSK packet instead)
{
    public double LineDurationMs => LineSegments.Sum(s => s.DurationMs);   // computed, not stored
}

// One timed segment of a scanline -- a fixed-frequency sync/porch/separator pulse, a
// pixel-data-varying color-channel scan, or (Robot family) a tone selecting which of two
// alternating chroma channels follows:
public abstract record LineSegment(double DurationMs);
public sealed record SyncSegment(double DurationMs, double FrequencyHz) : LineSegment(DurationMs);
public sealed record ScanSegment(string ChannelName, double DurationMs) : LineSegment(DurationMs);
public sealed record ToneSelectorSegment(double DurationMs, double LowFrequencyHz, double HighFrequencyHz) : LineSegment(DurationMs);
public sealed record HoldPreviousFrequencySegment(double DurationMs) : LineSegment(DurationMs);

public interface ISstvEncoder
{
    IAsyncEnumerable<float> EncodeAsync(SstvModeDefinition mode, IImageSource image, StationIdTransmitOptions stationId, CancellationToken ct);
}

public interface ISstvDecoder
{
    int SampleRate { get; }
    void PushSamples(ReadOnlyMemory<float> samples);   // synchronous, decode-thread-blocking -- no ring buffer/marshaling inside this call itself
    event Action<DecodedImageUpdate>? LineDecoded;     // incremental, so partial images render live like the legacy RxView
    event Action<SstvModeDefinition>? ModeDetected;    // auto mode detection via VIS decode
    event Action<SstvModeDefinition>? DecodeRestarted; // fires on the periodic decoder-restart cycle -- see "Decoder lifecycle" below
    event Action<FskStationIdDecodedInfo>? StationIdDecoded;
    void ResetAgc();
    void RequestReSync();
    void ForceMode(SstvModeDefinition mode);
    void RequestCorrectSlant();
    double? SlantPpm { get; }
    double SignalPeakLevel { get; }
    bool IsLevelOverdriven { get; }
    bool AutoSlantEnabled { get; }
    bool StationIdDecodeEnabled { get; set; }
    // + a handful more manual-correction/telemetry members -- see the interface's own doc comments
    // for the full, current list rather than trusting this snippet to stay exhaustive.
}
```

## Decoder lifecycle

The DI-registered `ISstvDecoder` is not `AnalogFmSstvDecoder` directly — it's
`RestartableSstvDecoder` (`ScanlineStudio.Core.Sstv`), which periodically discards and reconstructs
the whole decoder object graph under a lock. This exists to work around `AnalogFmSstvDecoder`'s
absolute sample index being `int` (ultracode audit finding #34): left unbounded, a sustained live
decode session would eventually overflow it. `RestartableSstvDecoder` restarts before that happens
(warning around ~12h, critical around ~13h of continuous decode) and raises `RestartOverdue`/
`RestartCriticallyOverdue` so the caller can react (`SstvSessionService` force-stops capture on the
critical signal). Every consumer only ever holds `ISstvDecoder`, so the swap is transparent except
for the `DecodeRestarted` event above.

## Signal chain

```
Encode:  IImageSource → per-mode scanline sampler → FM/AFSK tone synthesizer → float[] samples → IAudioEngine.EnqueuePlaybackSamples
Decode:  IAudioEngine.SamplesCaptured → FIR bandpass filter (SearchBandpassFilter) → FM demodulator (Hilbert/PLL) → sync detector → scanline reconstructor → DecodedImageUpdate
```

- **Tone detection / FM demodulation** (`sstv.cpp`'s `CHILL`/`CPLL`/`CVCO`/`CFQC` equivalent): **implemented, ported directly** — per CLAUDE.md's "port first, invent second" rule for DSP/codec math, `ScanlineStudio.Core.Sstv` contains three demodulators selected at runtime by `DemodType`: `HilbertFmDemodulator` (a port of legacy's `CHILL`, `sstv.cpp:3005-3087` — legacy's actual compiled-in default, `m_Type=2`, and now this port's default for the main picture path too), `PllFmDemodulator` (a port of legacy's closed-loop PLL discriminator `CPLL`, still used for the `DemodType.Pll` option and independently for AVT training-lock), and `ZeroCrossingFrequencyCounter`. An earlier revision of this port used the PLL discriminator exclusively for the main picture path; it was superseded by the Hilbert port once profiling showed the PLL's slower/undershooting settling causing real golden-vector delta regressions on narrow-pitch modes (see `HilbertFmDemodulator`'s own doc comment for the full derivation history, verified across two rounds of auditor plan-review). An earlier from-scratch open-loop quadrature-mixing discriminator (unrelated to either of the above) was scrapped even before that, for the same reason. See `PllFmDemodulator`'s and `HilbertFmDemodulator`'s doc comments for their exact structures. There is no separate FFT/Goertzel tone-detection stage in the decode chain — the FFT in this codebase (`RadixTwoFft`) backs the waterfall visualization only, not decode (see below).
- **IIR filtering** (`fir.cpp`'s standalone `MakeIIR`/biquad-cascade application): ported as `IirFilter`, used internally by `PllFmDemodulator`/`HilbertFmDemodulator`/`ZeroCrossingFrequencyCounter`/`TankFilter`/`SyncEnvelopeDetector`/`AnalogFmSstvDecoder` — this is demodulator-internal machinery, not the RX/TX bandpass stage.
- **FIR bandpass filtering** — a *separate* FIR port (`CFIR2`/`MakeFilter`, own coefficient tables and delay lines, distinct from the IIR machinery above): a general-purpose pre-demodulator bandpass stage (`SearchBandpassFilter`, a direct port of `CSSTVDEM::Do`'s `m_BPF.Do(...)` call, `sstv.cpp:1826-1833`, selectable via `RxBpfPreset` Off/Wide/Narrow/VeryNarrow) and a TX-side output bandpass (`TxOutputBandpassFilter`, a port of `CSSTVMOD`'s always-on `m_BPF`, `sstv.cpp:2759-2772,2914`). Both are now implemented.
- **Waterfall/scope** (`Scope.cpp` equivalent): implemented in Phase 3, `ScanlineStudio.Core.Sstv.WaterfallSource` — rolling FFT magnitude frames (a new standard radix-2 FFT + Hann window, not a legacy port — see [[09-ui]]'s Aesthetic Directive note and `RadixTwoFft`'s own doc comment for why CLAUDE.md's port-first rule doesn't apply to a visualization feature), independent of decode state by construction (see the Definition-of-done item below). Rendered by `ScanlineStudio.UI.Controls.WaterfallControl`/`SpectrumTraceControl`, colorized/live-trace as of 2026-08-09 batches 8a/8b (a 6-stop heatmap gradient, mode-derived SSTV tone markers, peak-hold, continuous zoom/bandwidth — see [[14-roadmap]]'s now-DONE "Waterfall color/palette rendering" item and `PROJECT_BRIEF.md`) — the earlier "deliberately kept visually simple, flat grayscale" deprioritization was explicitly re-confirmed and then lifted by direct user request ("full item"), not silently reversed.
- **VIS auto-detection**: implemented — `VisHeader` holds the shared constants/TX generator; RX decode itself lives in `VisLockStateMachine` + `AnalogFmSstvDecoder.TryDecodeVisHeader` (+ `SstvModeRegistry.FindByFullVisByte`), decoding the standard leader-break-leader/start-bit/7-data-bit/parity/stop-bit sequence.
- **Scanline reconstruction**: implemented across every mode family currently in `SstvModeRegistry` (Martin, Scottie, Robot 36/72, R24, the MR/ML/MP/MN/MC half-scan-chroma families, PD-series, Pasokon P3/P5/P7, RM8/RM12, SC2, AVT — see the Definition-of-done table below for the full list and remaining gaps). Legacy's AFC (`CSSTVDEM::SyncFreq`) and its `CSYNCINT` peak-interval sync-acquisition tracker are both ported (`AfcTracker`, `SyncIntervalTracker`), along with a `SyncSSTV`-style fold-and-argmax anchor correction (`SyncAnchorCorrector`) wired into `AnalogFmSstvDecoder` — this is a materially more complete port of legacy's real sync-search/clock-drift machinery than a nominal-timing-only decode.

## Auto mode detection

Legacy MMSSTV auto-detects mode from the leading VIS (Vertical Interval Signaling) code tone sequence. `ISstvDecoder` implements the same VIS-code detection as its primary mode-detection path (`ModeDetected` fires once VIS is decoded), with manual mode override always available in the UI for weak-signal cases where VIS decode fails — matching legacy behavior of allowing forced mode selection.

## Sample clock calibration (slant correction)

**Correctness requirement, not an enhancement** — omitted from the original draft of this document, added after review. Received images slant when the transmitting and receiving stations' sound card sample clocks disagree even slightly, because SSTV timing is derived entirely from the audio sample rate. Legacy MMSSTV addresses this **three** ways — an earlier version of this section only found two, both manual, and classified the whole feature as UI/out-of-scope for "legacy is truth" DSP work; an independent Opus verification pass caught that there's also a fully automatic path, which is a genuine DSP correctness feature and has now been ported:

- **Auto Slant — DONE** (`Main.cpp`'s `AutoStopJob`, `KRSA->Checked` branch, on by default in the shipped `Mmsstv.ini`: `AutoSlant=1`): fully automatic, per-line, no user interaction — a resonant sync-tone envelope detector finds where the sync pulse falls within each line, a least-squares trend fit plus a staged confidence-threshold ladder decides when observed drift is real, and a corrected effective sample rate is applied going forward. Ported as `TankFilter`/`SyncEnvelopeDetector`/`SlantTracker`, wired into `AnalogFmSstvDecoder`. See [[14-roadmap]]'s Phase 1 entry for the full port story, including two real bugs a mistuned-rate integration test caught (an internal formula desync after the first correction, and slant-tracking running ahead of pixel decode into non-image content) that no isolated unit test could have found. Verified: a realistic clock mismatch (500ppm) decodes within normal round-trip tolerance; a deliberately severe 1% mismatch converges but doesn't fully correct, which was traced to and confirmed as a real characteristic of legacy's own algorithm (it locks after 5 confidence tiers fire, rather than continuing to re-adjust for the rest of a long transmission), not a gap in the port.
- **Manual calibration — deferred, UI work** (`ClockAdj.cpp`, `SampFreq`/`m_SampFreq` in `sstv.h`, default `1.102500e+04`): a settings dialog that nudges the effective sample rate against a known reference (e.g. WWV/time-standard tone) and persists the correction. Genuinely a UI workflow (a dialog + persisted setting), not DSP math to port faithfully — pick up when building the corresponding settings UI, not before.
- **Manual "drag the visible slant" tool — deferred, UI work** (`Main.cpp`'s `m_Slant`/`GetSyncSamp`/`DrawSync`/`PBoxD12MouseMove`/`PBoxD12MouseDown`, `Main.cpp:5887-5913, 6415-6455`): a diagnostic "Sync" view where the user visually sees the sync pulse drift across lines and drags a reference line to compute a correction — the same underlying proportional math as Auto Slant, but manually triggered and manually measured via mouse coordinates. The interaction itself (a legacy VCL canvas + mouse-drag design) is squarely UI, not something to replicate literally; when this gets built, treat legacy's version as one reference point, not a spec to match pixel-for-pixel, per the user's explicit scoping of "legacy is truth" to protocol/DSP work only, not UI.
- **Independent TX offset** (`m_TxSampFreq`, `TxSampOffset` setting): TX and RX clocks are calibrated separately, since a sound card's playback and capture clocks can themselves disagree. Not yet ported; relevant once TX/radio integration (Phase 2+) exists, not before.

## Station identification (FSK/CW ID)

**Status correction (explicit user decision, see [[14-roadmap]]'s Phase 1 entry)**: this section used to say FSK/CW ID was "required for v1... unlike the deferred items below," on a regulatory-requirement rationale, and later described it as deferred. The user had explicitly decided otherwise for this port: CW ID specifically is real legacy functionality but not required for the program to function as an SSTV encoder/decoder, so it was not a v1 blocker — though it has since been implemented anyway (see below). General principle going forward, not just for this feature: if something is present in legacy but not load-bearing for basic SSTV operation, flag it and ask before treating it as in-scope, rather than assuming regulatory or "legacy had it" framing makes it required.

**Status update: the CW-ID/FSK station-ID subsystem is now fully implemented** (six phases, see `git log` on `src/ScanlineStudio.Core.Sstv/FskStationIdEncoder.cs`/`CwMorseGenerator.cs`/`NarrowFskHeaderDecoder.cs`), superseding the "Deferred" list this section used to carry. What follows records what was built and against which legacy source, not a backlog:

- **FSK ID TX** (`OutputFSKID`, `Main.cpp:6903-6965`) — **done** (`FskStationIdEncoder`): reuses the `WriteFSK` mechanism already ported for the MN/MC narrow-mode-announce packet (`VisHeader.GenerateFskBits`), but with its own distinct preamble (no leading 300ms leader tone, just guard tone + start-bit pulse) — `WriteFSK(0x2a)` (STX, distinct from the narrow packet's `0x2d`), each callsign character as `WriteFSK(char - 0x20)`, `WriteFSK(0x01)` (EOT, not XORed into the checksum), then an XOR checksum byte.
- **FSK ID's optional signal-report appendix** (`Main.cpp:6926-6963`, gated on `Log.m_LogSet.m_FSKNR`) — **done**, in `FskStationIdEncoder`/`FskStationIdWireFormat`: a second, separately-checksummed packet appending a signal report, with the real compact-3-byte-numeric-vs-raw-text-fallback branch ported.
- **FSK ID RX** — **done** (`NarrowFskHeaderDecoder`): decodes the `0x2a`-prefixed station-ID continuation (callsign and both NR/RST sub-forms) from the same shared state machine as the `0x2d` mode-announce packet, exposed via `ISstvDecoder.StationIdDecoded`/`StationIdDecodeEnabled` (legacy `m_fskdecode`).
- **CW ID** (`OutputCWID`/`WriteCWID`, `Main.cpp:6967-6981`, `sstv.h:832`) — **done** (`CwMorseGenerator`): dit/dah Morse table with the '.'→'R' and '/' special cases, configurable WPM/tone, `MacroText`-style expansion of the configured CW ID text before encoding.
- **The "FSK ID configured" footer branch** (`Main.cpp:7010-7012`: `mp->Write(fTxNarrow ? 1900 : 1500, 300)`) — **done**: `AnalogFmSstvEncoder.GenerateFooterSegments` now takes an `fskIdEnabled` flag and emits this branch instead of the non-FSK footer when set.
- **Settings/config surface** — **done**: `StationIdSettings` (`ScanlineStudio.Core.Sstv`, persisted callsign/CW/FSK/NR-RST configuration) is resolved by `ScanlineStudio.Application.SstvSessionService` into `StationIdTransmitOptions` (`ScanlineStudio.Abstractions.Sstv`, one already-resolved value per transmission) right before each call to `ISstvEncoder.EncodeAsync` — the pure DSP encoder itself never references settings/macro-resolution types, matching the layering this section originally called for. `OutputMMV` (voice-ID via sound file, `sys.m_CWID==2`) is out of scope, as anticipated — `CwIdMode` has no field for it, so selecting it transmits nothing (matching legacy's own unconfigured-sound-file behavior).
- **VOX-mode footer/ID variant** (`sys.m_VOX`, default off — `Main.cpp:822`): still not modeled by this port (no radio/PTT layer exists yet) — this one item remains accurately "deferred." The footer always takes the non-VOX branch, matching legacy's own default.

The **post-image footer tone** (`Main.cpp:6994-7013`, the `!sys.m_TXFSKID` branch — a trailing-carrier hold plus, for non-narrow modes, an alternating 1900/1500Hz tone sequence) is **done** (`AnalogFmSstvEncoder.GenerateFooterSegments`) — it's not station-ID-specific (legacy emits it whether or not FSK ID is configured).

## Explicitly deferred (not v1)

SSTV repeater/beacon mode (`RepSet.cpp`, `Repeater.txt`, unattended relay/beacon transmission) is real legacy functionality with no spec coverage here — deferred to post-v1 rather than silently dropped; see [[14-roadmap]] (not yet in [docs/removed-features.md](../docs/removed-features.md) — add an entry there per CLAUDE.md §2's removal rule if this stays deferred past v1 rather than picked up).

## Real-time budget

Decode must keep up with live audio in real time on modest hardware (this was true of the original Pentium-era MMSSTV and must remain true). The processing thread consuming the audio ring buffer (see [[05-audio-engine]]) runs the filter → detect → reconstruct pipeline per incoming block; the real drain granularity is `MiniAudioCaptureSession.DrainBufferFrames` (4096 frames, handed to `PushSamples` with no re-chunking) — roughly 372ms at 11025Hz, longer than a single scanline in every supported mode. Decode correctness does not depend on sub-scanline block latency (partial-line rendering is driven by `LineDecoded`'s own incremental updates, not by how finely PushSamples is chunked), but this is worth knowing if a future change ever needs a tighter live-preview latency bound.

## Testing

This layer is the most amenable to deterministic testing in the whole system. Illustrative shape
below, not a literal transcription of the real test (the actual `SstvRoundTripTests` uses a WAV
round-trip with a per-channel average-delta tolerance across every mode via `[MemberData]`, not a
single-image `MatchWithinTolerance(maxDeltaE:)` call):

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

Additional targeted tests: FIR filter frequency response (verify passband/stopband against known coefficients), FM demodulator tone detection against synthetic tone bursts with added noise (verify detection holds down to a defined SNR threshold), VIS code detection against all standard VIS codes plus corrupted/partial ones (must not false-positive), and a deliberately clock-offset fixture verifying slant correction (see above).

**Golden vectors, not just round-trip tests.** Per CLAUDE.md's behavioral-parity rule: an encode→decode round-trip test can pass while both the encoder and decoder are wrong in the same compensating way — it does not prove the new implementation matches the *legacy* implementation's actual output, which matters because the legacy DSP core is `double`-precision throughout (`m_SampFreq`, `m_TxSampFreq`, all filter/FFT intermediates) while this spec proposes `float` samples. At minimum, capture intermediate outputs from a real run of the legacy binary (FIR filter output for a known input, FFT magnitude frame for a known tone, final decoded pixel values for a known transmitted image) and check the new implementation against them within a documented tolerance — this is the only way to catch a systematic numeric drift that a self-consistent round-trip test structurally cannot detect.

## Definition of done additions

- [x] Slant correction implemented (Auto Slant: `TankFilter`/`SyncEnvelopeDetector`/`SlantTracker`) and covered by clock-offset fixture tests (`SlantTests.cs` and friends — 500ppm and 1% mismatch scenarios; see the "Sample clock calibration" section above).
- [x] Post-image footer tone implemented (`AnalogFmSstvEncoder.GenerateFooterSegments`) — the non-station-ID part of what this item used to cover.
- [x] FSK/CW station ID: implemented (see the "Station identification" section above) — FSK ID TX/RX, the signal-report appendix, CW ID, and the settings surface are all done. Not a v1 blocker per the user's original decision, but built anyway.
- [x] At least one golden-vector test comparing against legacy-binary-captured output exists for the filter stage and the full decode path, with a documented tolerance (`GoldenVectorTests.cs`/`GoldenVectorFixtureReaderTests.cs`, `tests/ScanlineStudio.Core.Sstv.Tests/Fixtures/GoldenVectors/`, covering robot-36/martin-m1/scottie-s1/scottie-dx/robot-72/pd90/rm8/mr73/r24/mn110/avt against real legacy-binary-captured `.bmp` output — 11 modes as of this writing, grown from the original 8).

## Definition of done

- [x] `SstvModeDefinition` table now covers essentially the full legacy mode set — Martin M1/M2, Scottie S1/S2/DX, Robot 36/72, R24, AVT, Pasokon P3/P5/P7, the PD-series (PD50/90/120/160/180/240/290), the half-scan-chroma MR/ML/MP/MN/MC families, RM8/RM12, and SC2-180/120/60 (`SstvModeRegistry.All`) — each independently cross-checked against legacy `CSSTVSET::GetTiming`'s total-line-duration return value, not just self-consistent (`SstvRoundTripTests.LineDuration_MatchesLegacyGetTiming`/`_MonoFamily`). Channel order and VIS codes read from the matching legacy source per CLAUDE.md's TX/RX-split rule: **TX line-generator functions** (`Main.cpp`'s `LineMRT`/`LineSCT`/`LineR36`/etc.), not RX decode branches — an earlier Scottie entry built by inferring TX order from RX branch widths had the right *duration* but the wrong *channel order and sync placement* (fixed once `LineSCT` was read directly; see `SstvModeRegistry`'s doc comment for the full story).
- [x] Encode → decode round-trip test passes within tolerance for nearly every mode in the table above (`SstvRoundTripTests.EncodeThenDecode_RoundTripsWithinTolerance`, `[MemberData(nameof(Modes))]`; RM8/RM12 use a separate grayscale-fixture variant since they're genuinely monochrome) — **caveat, corrected**: an earlier version of this note claimed legacy "likely adapts filter tuning per mode speed" via a user-configurable "demod profile" system named `PRODEM` — that name doesn't exist anywhere in legacy source; it was an unverified guess written down as fact. Directly verified instead: legacy's `CPLL` uses one fixed tuning for every mode (only a Normal/Narrow band-range toggle exists, already handled via `LuminanceMinHz`/`MaxHz`), at the same 11025Hz default, for every mode including Martin M2/Scottie S2. Some modes still need 44100Hz here rather than legacy's 11025Hz default; see `SstvModeRegistry`'s doc comment and [[14-roadmap]]'s Phase 1 entry for the current measured state now that the `CSSTVDEM` sync-search/AFC pipeline (`AfcTracker`/`SyncIntervalTracker`/`SyncAnchorCorrector`) has been ported.
- [x] **Architecture**: per-family codec composition, not a single generic interpreter — `IScanlineEncoder`/`IScanlineDecoder` strategies selected via `ScanlineCodecFactory` based on `SstvModeDefinition.ColorEncoding`, sharing common infrastructure (VIS header, phase accumulation, demodulation) in `AnalogFmSstvEncoder`/`AnalogFmSstvDecoder`. Five families are now implemented (verified against each mode's own `ColorEncoding` in `SstvModeRegistry.cs`, not assumed from naming similarity — the registry's own comments flag MR/ML vs. MP as a real near-miss here): `RgbSequentialScanlineEncoder`/`Decoder` (Martin/Scottie/AVT/Pasokon P3-P7/MC), `RobotScanlineEncoder`/`Decoder` (Robot 36, alternating chroma; `YCbCr.cs`'s `ToRgb`/`FromRgb` — `ToRgb` a direct port of legacy `YCtoRGB`, `FromRgb` the exact mathematical inverse of that same matrix since legacy's forward `GetRY` function turned out to use a *different*, independently-chosen coefficient set, not simply the inverse of its own decode matrix), `YCbCrSequentialScanlineEncoder`/`Decoder` (Robot 72, R24, and the MR/ML family — every-line chroma, no alternation), `YCbCrLinePairedScanlineEncoder`/`Decoder` (MP-family and PD-series, chroma shared across two luma lines) and `MonoAveragedPairedScanlineEncoder`/`Decoder` (RM8/RM12, genuinely monochrome). Note: the MN family (half-scan-chroma, distinct from MP despite the name) also decodes via `YCbCrLinePaired` per the registry. New families add a new strategy pair, not changes to the shared shell.
- [ ] Decoder sustains real-time throughput on a defined reference machine spec (documented, benchmarked in CI where feasible) — not yet measured. (`RxLineStagingBuffer`/`RestartableSstvDecoder` exist to support sustained live decode, but no benchmark asserting real-time throughput was found.)
- [x] Waterfall rendering verified decoupled from decode (decode continues correctly with waterfall UI closed) — true by construction: `IWaterfallSource` has no dependency on `ISstvDecoder` and vice versa (see `IWaterfallSource`'s own doc comment), not just tested as a coincidence.
