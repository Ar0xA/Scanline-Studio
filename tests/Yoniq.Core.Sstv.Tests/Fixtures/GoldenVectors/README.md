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

- **TX-region timing** (50ms-window amplitude envelope, threshold ~15000/32768): robot-36 active from
  ~5.4s to ~44.0s (38.6s duration) against an expected 36.91s (910ms header + 240 lines × 150ms);
  martin-m1 active from ~10.7s to ~127.6s (116.9s duration) against an expected 115.20s (910ms header
  + 256 lines × 446.446ms). The consistent ~1.7s overshoot on both, not a per-mode-specific amount,
  points to coarse envelope-detection slop rather than a real timing bug.
- **Robot 36 canvas**: legacy's RX save is the full 320×256 shared canvac (`GetBitmapSize`), not the
  240-line picture (`GetPictureSize`) — rows 0-239 of `robot36_RX.bmp` are real decoded content, rows
  240-255 are pure white (255,255,255) fill. Verified the paste is 1:1, not a 240→256 stretch (row 239
  decodes G≈255 matching source row 239, not a stretched G≈239).
- **Legacy's own decode-vs-source baseline** (zero C# DSP involved): robot-36 = 6.99, martin-m1 = 1.57
  average per-channel delta. This is the honest reference bar for judging every other tolerance in
  `GoldenVectorTests.cs` — legacy's own decode is visibly imperfect even on a synthetic, noise-free
  gradient.
- **C# decoder vs source, decoding the real captures**: martin-m1 = 11.78 (close to legacy's own
  baseline and to this suite's usual 10.0 synthetic-round-trip tolerance). robot-36 = 68.06 — a real,
  substantial, **pre-existing known gap**, not new information: `SstvModeRegistry.cs`'s own doc
  comment already documents Robot 36 (and the rest of the low-samples-per-pixel family) failing the
  10.0 tolerance at 11025Hz, suspecting incomplete AFC/PLL settling. This is that suspicion, now
  grounded in a real captured-audio measurement instead of a synthetic experiment.

## What was captured, and what wasn't (privacy note)

Because the `.mmv` files contain real recorded sound-card/mic audio outside the TX window (see
above), the *contents* of that non-TX audio were not reviewed or transcribed as part of this work —
only its amplitude envelope was measured, to find the TX region. If that's a concern, the affected
segments are the leading ~5-11s and trailing ~20-35s of each file.
