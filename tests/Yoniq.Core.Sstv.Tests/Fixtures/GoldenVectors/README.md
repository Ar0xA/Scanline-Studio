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
- **C# decoder vs source, decoding the real captures**: martin-m1 = 11.78 (close to legacy's own
  baseline, comfortably below the ~42.67 corruption floor — genuinely discriminating). robot-36 =
  68.06 — a real, substantial gap whose *existence* is pre-documented (`SstvModeRegistry.cs`'s own
  doc comment already names Robot 36, and the rest of the low-samples-per-pixel family, as failing
  the 10.0 tolerance at 11025Hz against a *synthetic* self-round-trip, suspecting incomplete AFC/PLL
  settling) but whose *magnitude* is not: that same doc comment's own synthetic self-round-trip
  number for Robot 36 post-fix is 13.4 (`spec/14-roadmap.md`), roughly **5x smaller** than this
  real-capture number. And 68.06 is already worse than the ~42.67 corruption floor above — so the
  robot-36 golden-vector tests currently function only as regression tripwires (no worse than what's
  observed today), not as discriminating parity checks, until the underlying gap is fixed.

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
