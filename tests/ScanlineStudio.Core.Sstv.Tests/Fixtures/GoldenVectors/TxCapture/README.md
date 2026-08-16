# TX-side golden-vector capture

Per `spec/14-roadmap.md`'s "Milestone audit, Phase 1+2" → "Explicit prerequisite before Phase 3":
this port's own `.mmv` fixtures all currently validate RX only (real legacy-encoded audio decoded by
this port). Nothing validates this port's own TX output against a real legacy decode — the files here
close that gap.

**Status (spec/18-path-to-1.0.md Medium item, re-capture)**: the `*_TX.mmv` files below were
regenerated against the CURRENT `AnalogFmSstvEncoder` via `TxCaptureFixtureGenerator.cs`
(`tests/ScanlineStudio.Core.Sstv.Tests/`, gated behind `SCANLINE_REGENERATE_TX_FIXTURES=1` — see
its own doc comment for the exact invocation). `TxCaptureFixturesTests.cs`'s
`LiveEncoderOutput_MatchesCheckedInFixture_WithinQuantizationTolerance` test now asserts the live
encoder still matches these files on every ordinary test run (a change-detector, not itself a
legacy-validation claim — see that test's own doc comment), so this file family can no longer go
silently stale the way it did before this item.

**The `*_TX_RX.bmp` files in this same directory (all 11, from a real legacy Wine install, already
wired into `GoldenVectorTests.cs`'s `LegacyDecode_OfThisPortsEncoderOutput_MatchesSourceImage`) are
now STALE relative to this re-capture** — they were captured against an OLDER `.mmv` snapshot,
before the encoder fixes noted in `GoldenVectorTests.cs`'s own code comments (BPF/truncation-fix
history). Refreshing them needs a human with a real legacy install (see "What to do" below) — this
is a real, open, human-dependent follow-up, not silently dropped (matches
`GoldenVectorTests.cs:750-766`'s own honest framing of the same gap).

## What these files are

Each `<mode-id>_TX.mmv` is **this port's own `AnalogFmSstvEncoder` output**, for the exact source
image already in `../<mode-id-or-bmp-name>.bmp`, written in legacy's real `.mmv` format
(`MmvFile.Write`, mirrors the existing `MmvFile.Read` reader byte-for-byte — verified correct via an
Opus code-level review before any of these were first generated, including a real bug caught and
fixed in an earlier draft: a full-scale +1.0 sample used to silently wrap to -1.0 instead of
saturating).

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

## What to do with each one (refreshing the now-stale `*_TX_RX.bmp` files)

1. **Before playing anything**, set the legacy install's sample rate to **11025 Hz** in its Setup
   dialog — same step the original capture required. If you skip this, legacy will prompt to
   resample through a lowpass filter on `File → Play`, which defeats the whole point (you'd be
   testing a resampled signal, not this port's real TX output).
2. `File → Play`, select the `.mmv` file, let it run to completion in real time (durations vary by
   mode — Scottie DX is the slowest, several minutes; Rm8 the fastest, well under a minute).
3. Save/export the decoded image the same way the existing `*_RX.bmp` fixtures were captured (legacy's
   History feature auto-save, per the main `README.md` one level up — no timestamp overlay).
4. Name the result `<mode-id>_TX_RX.bmp` (e.g. `martin-m1_TX_RX.bmp`), OVERWRITING the existing
   (now-stale) file of the same name in this `TxCapture/` folder.

No rush — do a few at a time. `GoldenVectorTests.cs`'s
`LegacyDecode_OfThisPortsEncoderOutput_MatchesSourceImage` already reads these files; refreshing any
subset immediately tightens that test's real-legacy-validation coverage for that subset, without
needing to wait for the rest.
