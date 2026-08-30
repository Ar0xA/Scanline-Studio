# Golden-vector fixtures

Real reference data captured from a real, running legacy YONIQ/MMSSTV install, per
[spec/13-testing.md](../../../../spec/13-testing.md)'s golden-vector methodology. Used by
`GoldenVectorTests.cs`/`GoldenVectorFixtureReaderTests.cs`.

## Files

| File | What it is |
|---|---|
| `robot36.bmp` / `martin-m1.bmp` | Synthetic 24bpp gradient test images (R ramps 0→255 left-to-right, G ramps 0→255 top-to-bottom, B fixed at 128), generated to match the exact fixture formula this test suite's other tests already use. Not a legacy asset. |
| `robot36.mmv` / `martin-m1.mmv` | Real audio captured via legacy's own `File → Rec`, transmitting the corresponding `.bmp` above. Sample rate set to 11025 Hz in legacy's Setup dialog before capture. |
| `robot36_RX.bmp` / `martin-m1_RX.bmp` | Legacy's own decode of the corresponding `.mmv`, played back via `File → Play` and auto-saved by legacy's History feature (confirmed: no timestamp overlay burned in). |
| `scottie-s1.bmp` / `robot72.bmp` / `pd90.bmp` / `mn110.bmp` / `avt.bmp` | Same synthetic gradient formula as above, one per mode, sized to that mode's exact `SstvModeRegistry` canvas. |
| `rm8.bmp` | Grayscale gradient (R=G=B=(x+y) ramp) instead of the color formula above — RM8 is genuinely monochrome (no chroma channels at all), so the color gradient isn't a fair fixture for it; matches `SstvRoundTripTests`' own `CreateGrayscaleGradientTestImage` convention. |
| `scottie-s1.mmv` / `robot72.mmv` / `pd90.mmv` / `rm8.mmv` / `mn110.mmv` / `avt.mmv` | Task #7 (spec/14-roadmap.md) additions — same real-capture methodology as `robot36.mmv`/`martin-m1.mmv` above. |
| `scottie-s1_RX.bmp` / `robot72_RX.bmp` / `pd90_RX.bmp` / `rm8_RX.bmp` / `mn110_RX.bmp` / `avt_RX.bmp` | Legacy's own decode of the corresponding `.mmv` above, same capture method as `robot36_RX.bmp`. |
| `scottie-dx.bmp` / `mr73.bmp` / `r24.bmp` | Same synthetic gradient formula, generated for TX-direction fixture prep (see `TxCapture/README.md`) — these 3 modes had no existing RX-direction fixture at all before that, each flagged by the milestone audit as the sole mode exercising a specific code path. Not yet paired with a real legacy `_RX.bmp` decode. |

## TX-direction fixtures (pending, see `TxCapture/README.md`)

All of the above validates RX only — real legacy-encoded audio decoded by this port. `TxCapture/`
contains this port's own encoder output (`.mmv` format, ready to feed into a real legacy install via
`File → Play`) for all 11 modes above, closing the corresponding TX-direction gap. See that folder's
own README for the exact capture steps and current status.

## `.mmv` format (confirmed directly against `yoniq-old/YONIQ-main/Sound.cpp`, not RIFF/WAV)

4-byte header `[0x55, 0xAA, SampType, 0x00]` followed by a flat stream of little-endian `int16` mono
samples. `SampType` indexes legacy's own `SampTable` (`ComLib.cpp:68`):
`{11025, 8000, 6000, 12000, 16000, 18000, 22050, 24000, 44100, 48000}` — both fixtures here have
`SampType=0` (11025 Hz), confirmed by reading the header bytes directly.

**Not a clean tap of the modulator.** Traced directly against `Sound.cpp`'s sound-processing loop
(`Sound.cpp:325-395`): `Wave.InClose()` (`:395`) closes the sound-card input only while transmitting,
so outside the TX window the file contains **real recorded sound-card/mic input**, not silence.
During TX, the recording write (`Sound.cpp:334`) happens *before* the modulator fills the same buffer
(`Sound.cpp:370-375` in the same loop iteration) — so the written TX audio is the *previous*
iteration's modulator output, delayed by exactly one audio buffer, and the modulator's final buffer
is never captured. None of this matters for image-domain comparisons (the decoder's sync-search finds
the header wherever it lands), but it rules out a literal sample-for-sample PCM comparison against a
freshly-generated encode — see `GoldenVectorTests.cs`'s class doc comment for what was deferred
because of this and why.

## Measured facts (not assumed) used to set test tolerances

- **TX-region timing** (amplitude envelope, threshold ~15000/32768): robot-36 measured ~38.58s,
  martin-m1 measured ~116.85s. **Round-2-review correction**: an earlier version of this note
  compared these against an incomplete expected duration (VIS header + image body only) and
  attributed the resulting ~1.7s gap on both modes to "envelope-detection slop" — wrong. Confirmed
  directly against `Main.cpp` instead: legacy's real TX writes two more fixed-duration segments at
  shipped defaults — `TMmsstv::OutHEAD` (`Main.cpp:7270-7292`, called from `SendSSTV` at `:7393`)
  writes 800ms of leader tones before the VIS header, and `SendSSTV`'s own footer
  (`Main.cpp:6996-7009`) writes `WriteC(1500, min(SSTVSET.m_TW, SampFreq/2)) + 4×100ms` after the
  image body — where `SSTVSET.m_TW` is the *demodulator's currently-selected mode's* line duration
  (`CSSTVSET::SetSampFreq`, `sstv.cpp:655-1109`), not the transmitted mode's (a separate field,
  `m_TTW`, is used for that — `CSSTVSET::SetTxSampFreq`, `sstv.cpp:1280-1285`). Both fixtures were
  captured with the RX side sitting at its default mode (`GetTiming`'s own `default:` case, smSCT1,
  428.22ms, `sstv.cpp:1275`), confirmed empirically: both captures show the same ~425ms footer
  segment despite transmitting different modes. With head+footer accounted for correctly, expected
  totals are robot-36 38.54s (measured 38.58s, 0.04s residual) and martin-m1 116.83s (measured
  116.85s, 0.02s residual) — both now sub-50ms, consistent with envelope-window granularity, not a
  real timing bug.
- **Robot 36 canvas**: legacy's RX save is the full 320×256 shared canvas (`GetBitmapSize`), not the
  240-line picture (`GetPictureSize`) — rows 0-239 of `robot36_RX.bmp` are real decoded content, rows
  240-255 are pure white (255,255,255) fill. Verified the paste is 1:1, not a 240→256 stretch (row 239
  decodes G≈255 matching source row 239, not a stretched G≈239).
- **Legacy's own decode-vs-source baseline** (zero C# DSP involved): robot-36 = 6.99, martin-m1 = 1.57
  average per-channel delta. This is the honest reference bar for judging every other tolerance in
  `GoldenVectorTests.cs` — legacy's own decode is visibly imperfect even on a synthetic, noise-free
  gradient.
- **Corruption floor** on this exact source image and delta metric: a flat gray image, a horizontally
  mirrored copy, a vertically flipped copy, and an R↔B channel-swapped copy of the source all score
  ~42.67 — this gradient is smooth enough that most structural corruptions land in a narrow band
  around that number. Load-bearing for interpreting the numbers below: a tolerance above ~42.67
  cannot reject a structural bug, only something worse than near-random.
- **C# decoder vs source, decoding the real captures**: superseded by a long chain of DSP fixes
  since first measured — see `GoldenVectorTests.cs`'s own `Decoder_DecodesRealLegacyAudio_
  WithinToleranceOfSource` comment history for the full progression (narrower PLL band, the
  m_KSS/m_KS2S pixel-pitch trim fix, the Hilbert demodulator port, the per-line cursor
  rounding fix, and others). Robot-36 in particular started far worse than every other mode here
  (an initial real-capture delta of 68.06, worse than the ~42.67 corruption floor above — a real,
  substantial gap flagged by `SstvModeRegistry.cs`'s doc comment and tracked in
  `spec/14-roadmap.md`) and is now, as of the most recent re-measurement, comfortably inside its
  tolerance and below the corruption floor like every other mode: martin-m1 = 1.44 (tolerance
  3.0), robot-36 = 7.11 (tolerance 10.0). The golden-vector tests are genuinely discriminating
  parity checks again, not regression tripwires only.

## Trimmed to remove incidental room/mic audio

Both `.mmv` files were trimmed (at the user's explicit request) from their original captures, which
contained real recorded sound-card/mic audio outside the TX window (confirmed via `Sound.cpp`'s
sound-processing loop, see above). Trimmed to the detected TX region (amplitude envelope, threshold
~15000/32768, 5ms windows for tight bounds) plus a 1.0s safety margin on each side, to avoid clipping
any real signal content while removing as much of the non-TX audio as reasonably possible:

| file | original | trimmed | removed |
|---|---|---|---|
| `robot36.mmv` | 866304 samples (78.58s) | 446870 samples (40.53s) | ~38.05s |
| `martin-m1.mmv` | 1630208 samples (147.86s) | 1309600 samples (118.78s) | ~29.08s |

Confirmed the trim didn't touch any TX content: the decode deltas and encoder-cross-check deltas in
`GoldenVectorTests.cs` (martin-m1 11.78/13.66, robot-36 68.06/60.89) were re-measured against the
trimmed files and found byte-for-byte unchanged from the pre-trim measurements. The TX-region
**duration** test's own numbers did shift slightly (round-3-review correction to an earlier,
overclaiming version of this paragraph that called all of these numbers "unchanged" without
re-checking this one at the precision the test actually asserts): trimming moves the TX region to a
different absolute offset within the file, which re-phases the fixed-size (551-sample)
envelope-detection window grid against it, changing exactly where a window boundary falls relative
to the true TX start/end — a real, expected, sub-window-size (<50ms) effect on the measured
duration, not evidence of any actual timing change. See `GoldenVectorTests.cs`'s own updated comment
for the exact current numbers; re-measure again (don't assume) after any future re-trim or
re-capture. The *contents* of the removed non-TX audio were never reviewed or transcribed at any
point, before or after trimming — only its amplitude envelope was measured, to find the region to
remove.

## Task #7: six new fixtures (scottie-s1, robot72, pd90, rm8, mn110, avt)

Same capture methodology as `robot36`/`martin-m1` above (`File → Rec` at 11025 Hz, transmit the
source `.bmp`, `File → Play` + History save for the `_RX.bmp`), captured in one session. One file
was captured under the wrong name (`atv.mmv`/`atv_RX.bmp`) and renamed to `avt.mmv`/`avt_RX.bmp` —
legacy has no "ATV" mode (only `"AVT 90"`, confirmed directly against `sstv.h`'s enum/`sstv.cpp:494`'s
`SSTVModeList`), so this was a letter-swap typo, not a different capture.

**Legacy's mode-picker label ≠ this port's own `DisplayName`/`Id` for three of these** — confirmed
directly against `sstv.h`'s mode enum (whose declaration order matches `sstv.cpp:494`'s
`SSTVModeList` string array index-for-index, i.e. `SSTVModeList[smXxx]` gives the exact label):

| This port's mode | Legacy's dropdown label |
|---|---|
| Scottie S1 (`scottie-s1`) | `Scottie 1` |
| Robot 72 (`robot-72`) | `Robot 72` |
| PD90 (`pd90`) | `PD90` |
| RM8 (`rm8`) | **`B/W 8`** |
| MN110 (`mn110`) | **`MP110-N`** — legacy groups the narrow MN family under an "MP...-N" label |
| AVT (`avt`) | `AVT 90` |

### Trim table

Same method as the original two fixtures (amplitude envelope, ~15000/32768 threshold, 5ms windows,
1.0s margin each side):

| file | original | trimmed | removed |
|---|---|---|---|
| `scottie-s1.mmv` | 1435648 samples (130.22s) | 1258395 samples (114.14s) | 16.08s |
| `robot72.mmv` | 903168 samples (81.92s) | 843090 samples (76.47s) | 5.45s |
| `pd90.mmv` | 1118208 samples (101.42s) | 1040815 samples (94.40s) | 7.02s |
| `rm8.mmv` | 176128 samples (15.98s) | 139365 samples (12.64s) | 3.33s |
| `mn110.mmv` | 1284096 samples (116.47s) | 1248220 samples (113.22s) | 3.25s |
| `avt.mmv` | 1245184 samples (112.94s) | 1120290 samples (101.61s) | 11.33s |

### Measured deltas

| mode | legacy-own-decode baseline | this port's decoder vs. real audio (as first measured) | encoder self-consistency |
|---|---|---|---|
| scottie-s1 | 1.24 | 2.74 (restarts=0, correct mode) | 0.58 |
| robot-72 | 7.03 | 13.46 (restarts=0, correct mode) | 4.53 |
| pd90 | 4.08 | 1.99 (restarts=0, correct mode) | 1.65 |
| rm8 | 5.45 | 13.76 (restarts=0, correct mode) | 3.37 |
| mn110 | 3.45 | 12.79 (restarts=0, correct mode) | 12.03 |
| avt | 4.23 | 5.80 (restarts=0, correct mode) | 9.92 |

All six modes landed in the same healthy range as `martin-m1`/`robot-36`'s own first-measured
numbers above. **The middle column is now stale** — the same DSP fix chain referenced above
(narrower PLL band, pixel-pitch trim, Hilbert demodulator port, per-line cursor rounding fix, etc.)
moved every one of these deltas since this table was first built; as of the most recent
re-measurement in `GoldenVectorTests.cs`'s own comment history: scottie-s1 = 1.10, robot-72 = 6.83,
pd90 = 3.74, rm8 = 5.12, mn110 = 3.57, avt = 6.74 — all improved or unchanged, all comfortably
inside their current tolerances. mn110's self-consistency delta (12.03) sitting close to its
*original* decode-vs-source delta (12.79) is no longer a live concern now that the decode-vs-source
number has dropped to 3.57, well below self-consistency.

### Real finding: AVT's real capture never decoded -- fixed (S31)

At the time this fixture was first wired in, this port's decoder produced **zero `ModeDetected`
events across the entire ~100s `avt.mmv` capture** — not a tolerance/quality gap like robot-36's, a
total detection failure. Investigated before concluding this was a decoder bug, not a bad capture:
hand-traced `avt.mmv`'s raw frequency content (short-window FFT spot checks) and confirmed the first
~2.7s matched legacy's exact expected sequence — `OutHEAD`'s 800ms 8×100ms leader pattern
(1900/1500/1900/1500/2300/1500/2300/1500Hz), then a proper 300ms 1900Hz VIS leader, break, and
1100/1300Hz data bits — and later content was consistent with real image-body transmission, not
silence or corruption. So the capture was legitimate; the gap was in this port's AVT header detection
when fed real (non-synthetic) audio.

**Root cause, confirmed empirically (not the timing-jitter hypothesis originally guessed here):**
`VisLockStateMachine` — the only mechanism in this port with real-world noise tolerance (finds a VIS
header anywhere in a stream, not just at a fixed offset) — deliberately discarded every AVT match it
found, by design. Temporary instrumentation run against this exact fixture proved it correctly
decoded AVT's real VIS byte (`0x44`) from the real capture three separate times (once per real VIS
repeat) and threw every one away. The only path allowed to act on an AVT match was the fixed-window
`TryDecodeVisHeader` path — a single one-shot attempt anchored at the very start of the buffer, which
on a real capture lands in `OutHEAD`'s leader tones / pre-TX room audio, never the real header, and
(being one-shot per epoch) never gets another chance. Net effect: the only mechanism that could find
AVT discarded it; the only mechanism allowed to act on it never saw real content.

**Fix**: `VisLockStateMachine` now lets AVT flow through the same `Verify` state every other mode
already uses (legacy's own case 3, `sstv.cpp:2127-2153`, verifies every mode identically; the
AVT-specific diversion only happens after that shared verification succeeds), and
`AnalogFmSstvDecoder.TryInterleavedHeaderScan` hands an AVT match off to the same
`TryStartAvtTraining` entry point the fixed-window path already used. See `spec/14-roadmap.md`'s S31
entry for the full root-cause trace, auditor plan-review (which caught a real `TrimBuffers` watermark
invariant violation before any code shipped), and fix detail.

### Real finding: mn110's footer has no measurable trailing carrier

Legacy's real footer (`Main.cpp:6994-7013`, confirmed directly against source this session) is
`if(!sys.m_VOX && !SSTVSET.m_fTxNarrow) WriteC(1500,...)+4×100ms; else WriteC(1900,...)` — narrow
modes (MN/MC) get only the trailing carrier, no alternating tail, matching what this port's own
encoder already does. For the five other new captures, the measured post-body residual (~0.71–0.91s)
is consistent with the same ~428ms-RX-default hypothesis the original two fixtures already
established (`receiveSideModeLineDurationMsForTheseFixtures`). `mn110`'s residual is only ~0.076s —
and a direct look at the raw envelope (50ms windows over the file's last 3 seconds) shows a sharp
cutoff from full amplitude straight to noise floor, with no extended trailing tone at all. Genuinely
unexplained (the RX-side `SSTVSET.m_TW` state during this specific capture session is unknown and
unrecoverable after the fact) — modeled in `GoldenVectorTests.cs` as a real, documented zero-footer
case for this one fixture rather than forced to fit the other five's shared formula.

### AVT's RX-save margin isn't pure white

Robot36/Robot72/RM8's RX bitmaps all share the same convention: legacy's RX save is the full 320×256
shared canvas, and rows 240-255 (below `GetPictureSize`'s real 240-row content) are pure white
(255,255,255) fill. AVT also has `hp=240` per `CSSTVSET::GetPictureSize` (confirmed directly against
`sstv.cpp:638-653`), so the same convention should apply — but `avt_RX.bmp`'s margin rows are near-
black, not white. Pinned as a fact via `BmpFile_ReadsAvtRx_With256TallCanvas_ButMarginIsNotPureWhite`
(`GoldenVectorFixtureReaderTests.cs`) rather than asserted as an invariant; not investigated further,
since every comparison in this suite already crops to the real 240-row picture height regardless.
