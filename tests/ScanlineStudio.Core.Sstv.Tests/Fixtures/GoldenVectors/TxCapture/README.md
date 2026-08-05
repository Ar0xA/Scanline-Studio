# TX-side golden-vector capture — pending

Per `spec/14-roadmap.md`'s "Milestone audit, Phase 1+2" → "Explicit prerequisite before Phase 3":
this port's own `.mmv` fixtures all currently validate RX only (real legacy-encoded audio decoded by
this port). Nothing validates this port's own TX output against a real legacy decode — the files here
close that gap.

## What these files are

Each `<mode-id>_TX.mmv` is **this port's own `AnalogFmSstvEncoder` output**, for the exact source
image already in `../<mode-id-or-bmp-name>.bmp` (or newly generated, for the 3 modes below that had no
existing fixture — see "New source images" below), written in legacy's real `.mmv` format
(`MmvFile.Write`, mirrors the existing `MmvFile.Read` reader byte-for-byte — verified correct via an
Opus code-level review before any of these were generated, including a real bug caught and fixed in an
earlier draft: a full-scale +1.0 sample used to silently wrap to -1.0 instead of saturating).

All 11 files are 11025Hz mono, matching every other fixture in this repo.

| File | Mode | Source image |
|---|---|---|
| `robot-36_TX.mmv` | Robot 36 | `../robot36.bmp` (existing) |
| `martin-m1_TX.mmv` | Martin M1 | `../martin-m1.bmp` (existing) |
| `scottie-s1_TX.mmv` | Scottie S1 | `../scottie-s1.bmp` (existing) |
| `robot-72_TX.mmv` | Robot 72 | `../robot72.bmp` (existing) |
| `pd90_TX.mmv` | PD90 | `../pd90.bmp` (existing) |
| `rm8_TX.mmv` | RM8 | `../rm8.bmp` (existing) |
| `mn110_TX.mmv` | MN110 | `../mn110.bmp` (existing) |
| `avt_TX.mmv` | AVT | `../avt.bmp` (existing) |
| `scottie-dx_TX.mmv` | Scottie DX | `../scottie-dx.bmp` (**new**) |
| `mr73_TX.mmv` | MR73 | `../mr73.bmp` (**new**) |
| `r24_TX.mmv` | R24 | `../r24.bmp` (**new**) |

**New source images**: Scottie DX, MR73, and R24 had no existing RX-direction fixture at all — each is
the sole mode exercising a specific code path (Scottie DX: the only `NeverPeakPicks` mode; MR73: the
only mode with different luma/chroma trim divisors; R24: the only mode using legacy's row-doubling
substitution), flagged by the milestone audit as a real coverage gap. Their source `.bmp`s use the
exact same gradient formula as every other color-mode fixture (R ramps 0→255 left-to-right, G ramps
0→255 top-to-bottom, B fixed at 128), sized to each mode's own canvas.

## What to do with each one

1. **Before playing anything**, set the legacy install's sample rate to **11025 Hz** in its Setup
   dialog — same step the existing RX fixtures' own capture required. If you skip this, legacy will
   prompt to resample through a lowpass filter on `File → Play`, which defeats the whole point (you'd
   be testing a resampled signal, not this port's real TX output).
2. `File → Play`, select the `.mmv` file, let it run to completion in real time (durations range from
   ~9s for `rm8_TX.mmv` to several minutes for `scottie-dx_TX.mmv` — Scottie DX is a genuinely slow
   mode).
3. Save/export the decoded image the same way the existing `*_RX.bmp` fixtures were captured (legacy's
   History feature auto-save, per the main `README.md` one level up — no timestamp overlay).
4. Name the result `<mode-id>_TX_RX.bmp` (e.g. `martin-m1_TX_RX.bmp`) and put it back in this
   `TxCapture/` folder (or hand the files back however's convenient — just keep the `_TX_RX` naming so
   it's unambiguous which is which).

No rush — do a few at a time. Once any come back, they can be wired into a new
`GoldenVectorTests.cs`-style TX-direction test (comparing legacy's own decode of `<mode-id>_TX_RX.bmp`
against the original source `.bmp`) without waiting for the rest.
