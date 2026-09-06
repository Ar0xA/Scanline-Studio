# Known decode defects

Measured, reproducible defects in the decode path that are **not yet fixed**. Distinct from
[impairment-bench-coverage.md](impairment-bench-coverage.md), which records what the bench does and
does not exercise. A defect leaves this file only when it is fixed or proven not to be a defect.

## 1. Green cast on every YCbCr colour mode

**Status:** confirmed, decomposed into two faults, neither fixed. Found 2026-09-06.

Every YCbCr mode decodes with a systematic green cast. It is present on a **clean** signal, so it is
not a noise effect.

Measured as `G_err - (R_err + B_err)/2`, where each `_err` is the mean per-channel difference between
the decode and the source picture. Photographic source, Hilbert at the default 1800 Hz cutoff:

| mode | colour encoding | green bias, clean |
|---|---|---|
| robot-36 | `YCbCrRobot` | +4.7 |
| r24 | `YCbCrSequential` | +4.8 |
| robot-72 | `YCbCrSequential` | +4.4 |
| pd90 | `YCbCrLinePaired` | +4.4 |
| mn110 | narrow family | +3.8 |
| martin-m1 | `RgbSequential` | **+0.1** |
| scottie-s1 | `RgbSequential` | **+0.1** |

R and B fall together while G stays near zero. RGB-sequential modes show nothing — all three channels
move together there, a brightness shift with no hue change.

**Flat against SNR** (robot-36, Hilbert): 60 dB +4.7, 40 dB +4.7, 20 dB +4.7, 12 dB +3.4.
**Flat against the output smoother cutoff**: 600 Hz +4.0, 1200 +4.4, 1800 +4.7, 2400 +4.8, 3600 +5.0.
So it is neither a noise artifact nor chroma smearing.

### Fault A — fixed offset, about +2.3, TX-side colour maths, LEGACY-FAITHFUL

Present even on a fully desaturated source, which has no chroma content at all. Reproduced with no
signal path whatsoever by modelling `YCbCr.FromRgb` -> `YCbCr.ColorToFreq` -> ideal demodulation ->
`YCbCr.ToRgb` over 120k random RGB triples:

| variant | green bias |
|---|---|
| as shipped (both `Math.Floor` calls) | +1.91 |
| without `ColorToFreq`'s truncation | +1.50 |
| without `FromRgb`'s truncation | +0.42 |
| without either | 0.00 |

Truncation pushes every channel down by about half a level, but asymmetrically: both
colour-difference channels fall while luma barely moves, and green is the DERIVED channel, so the
residual lands almost entirely on green.

**This is a parity question, not a bug.** Legacy truncates identically — `GetRY` writes into `int&`
out-parameters (`ComLib.cpp:3653-3669`) and `ColorToFreq`'s `d*800/256` is integer division
(`ComLib.cpp:3491-3495`). Both are deliberately ported and cited in `YCbCr.cs`'s own doc comment.
Rounding instead of truncating would zero this fault and diverge from legacy TX. It also affects only
pictures WE transmit.

### Fault B — saturation-dependent, about +2.4, side NOT established

Appears only when the picture has colour, and stops growing once saturation is high (robot-36,
Hilbert, clean): grey source +2.3, normal photo +4.7, heavily saturated +4.9. That is a chroma
AMPLITUDE error — colours return less saturated than sent — and Fault A's maths does not explain it.

Ruled out: the output smoother (flat against cutoff), the demodulator (Hilbert +4.7 against
zero-crossing +5.6), and any single mode or encoding.

**The impairment bench cannot attribute this**, because it encodes and decodes with our own code.
Settling it needs either a decoder instrumented to report raw Y / R-Y / B-Y against what the encoder
intended, or a genuine off-air recording whose source picture is known. A throwaway probe for the
second route was written and deleted unrun. The existing OTA set does not serve: YONIQ's own
`Hist*.bmp` decodes correlate at about 0.0 with ours, so they are decodes of different transmissions.

### Why the bench never caught it

Every golden-vector fixture was the same smooth gradient, mean horizontal pixel delta 0.24 against
43-54 for a real received picture. A chroma error has almost nothing to distort there. It was found
by eye, on a photographic source, and only after a narrow smoother made it obvious.

## 2. rm8 colour tilt

**Status:** observed, not investigated. Found 2026-09-06.

`rm8` shows no green bias (-0.2) but a channel-dependent tilt on a clean signal: R -9.3, G -3.7,
B +2.3, consistent across all three demodulators. A different fault from defect 1, and unexplained.

## 3. PLL collapses at wide output cutoffs

**Status:** observed, not a shipping risk today. Found 2026-09-06.

At a 3600 Hz output cutoff on a CLEAN signal, the PLL demodulator reads R +26, G +43, B +21 and the
picture visibly breaks into a herringbone pattern. Hilbert and zero-crossing merely get grainy at the
same setting. PLL's loop cutoff defaults to 1500 Hz, so an output cutoff far above its own loop
bandwidth is the suspect.

Not reachable in shipped configuration — `pllOutputCutoffHz` defaults to 900 Hz and no UI exposes it.
It matters only if an output-cutoff control is ever added: **the safe range is not the same for all
three demodulators.**

## 4. Right-edge column error on MN and MC modes

**Found 2026-09-06, during the LMS line-enhancer sweep. Not investigated.**

The last two pixel columns of every MN and MC decode carry a much larger error than the rest of the
line. Measured on `photo-city` at 20 dB SNR, mean absolute per-pixel delta by column:

| mode | mid-image | column -2 | column -1 |
|---|---|---|---|
| mn73 | 23.2 | 69.4 | 108.8 |
| mc180 | 15.8 | 77.0 | 84.6 |

It renders as a coloured stripe down the right edge of the picture. `mp140` shows no such artefact
(12.6 mid-image, 11.2 and 10.7 at the edge), so it is specific to these families and not a general
end-of-line effect.

It is present with every DSP option off, so it is not caused by any optional filter. Likely
candidates, none checked: the end-of-line window running past the last pixel centre, or the sync
search consuming samples the last pixels need. The first step is to decode a clean, noise-free
signal and see whether the artefact survives — if it does, it is a pure timing bug rather than a
noise-sensitivity one.
