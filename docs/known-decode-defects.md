# Known decode defects

> **The work list lives in `production_audit.md`, not here.** This file records the measurements.
> What to actually do about them, and in what order, is in that file's "What to pick up next".

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

**Cheapest decisive probe (auditor, 2026-09-07), do this before anything expensive.** The Fault-A
model was run per RGB triple, so it never exercised the spatial layer — chroma subsampling, line
pairing, the encoder's own row averaging, and `ToRgb`'s clamp. Extend that same model to a whole
image: run a photographic source through the real encoder's channel layout, transport the channel
bytes ideally (no modulation, no filters, no demodulation), reassemble through the matching scanline
decoder, and measure green bias. Reading about +4.7 means Fault B is protocol or TX-side and closes
as a parity decision exactly like Fault A. Reading about +2.3 means the loss is in the DSP chain and
earns the full review cadence. No shipping code is touched either way.

**The impairment bench cannot attribute this**, because it encodes and decodes with our own code.
Settling it needs either a decoder instrumented to report raw Y / R-Y / B-Y against what the encoder
intended, or a genuine off-air recording whose source picture is known. A throwaway probe for the
second route was written and deleted unrun. The existing OTA set does not serve: YONIQ's own
`Hist*.bmp` decodes correlate at about 0.0 with ours, so they are decodes of different transmissions.

### Why the bench never caught it

Every golden-vector fixture was the same smooth gradient, mean horizontal pixel delta 0.24 against
43-54 for a real received picture. A chroma error has almost nothing to distort there. It was found
by eye, on a photographic source, and only after a narrow smoother made it obvious.

## 2. rm8 colour tilt — RETRACTED 2026-09-07, measurement artifact

**Not a defect.** RM8 is `CreateMonoAveragedMode` (`SstvModeRegistry.cs:602`), a monochrome mode with
a single `"Y"` scan segment, so a decoded RM8 image is R = G = B by construction and cannot carry a
channel-dependent tilt. Comparing a grey decode against a colour source produces exactly the reported
shape: R_err = Y - R below zero, B_err = Y - B above zero, G_err small and negative, invariant across
demodulators — which is why "consistent across all three" was observed and read as significant.

Original measurement, kept so the retraction is checkable: R -9.3, G -3.7, B +2.3 on a clean signal.

If anyone wants the falsifier: re-measure rm8 against a **desaturated** source. The tilt must collapse
to one common offset. Only that residual common offset — the 256/224 RM gain-correction path — could
be a real finding, and it is not what was recorded here.

## 3. PLL collapses at wide output cutoffs

**Status:** observed, not a shipping risk today. Found 2026-09-06.

At a 3600 Hz output cutoff on a CLEAN signal, the PLL demodulator reads R +26, G +43, B +21 and the
picture visibly breaks into a herringbone pattern. Hilbert and zero-crossing merely get grainy at the
same setting. PLL's loop cutoff defaults to 1500 Hz, so an output cutoff far above its own loop
bandwidth is the suspect.

Not reachable in shipped configuration — `pllOutputCutoffHz` defaults to 900 Hz and no UI exposes it.
**REOPENED 2026-09-07 — the "not reachable" premise above is FALSE.** An external review pass caught
it and I verified it. `OptionsWindowView.axaml:986` is a `NumericUpDown` bound to
`PllOutputCutoffHz` with `Minimum="1" Maximum="3900"`, and `OptionsWindowViewModel.cs:3413` applies
the value live through `RequestPllTuning`. `PllFmDemodulator`'s own clamp is
`Math.Clamp(cutoffHz, 1.0, _sampleRate * 0.45)`, which permits 4961 Hz at 11025 Hz — so 3600 passes.
A user can reach this from the Options UI today. Strike the sentence above claiming no UI exposes it.

**What is NOT established, and must not be overclaimed:** the experiment code behind the original
sweep was not located, so which demodulator cutoff that sweep actually changed is unconfirmed. Do not
assert guaranteed PLL collapse, and do not prescribe a new tuning range from this entry alone.
Advanced tuning may legitimately permit poor combinations.

**The surviving constraint stays true either way:** the safe range is not the same for all three
demodulators, so an output-cutoff control must range-limit per demodulator instead of sharing one
range. Tracked as an open item in `production_audit.md`.

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

**Localized to one variable (auditor, 2026-09-07).** MN140 and MP140 are structurally identical —
both `YCbCrLinePaired`, 320x256, 9.0 ms sync, 1.0 ms porch, four 270.0 ms segments
(`SstvModeRegistry.cs:418-438` against `:489-511`). The ONLY differences are sync 1900 against
1200 Hz, porch 2044 against 1500 Hz, and `LuminanceMin/MaxHz` 2044-2300 against the default
1500-2300. MP140 is clean and MN140 is not. MC is `RgbSequential` and is also affected, so colour
encoding, line geometry and segment count are all ruled out by observation. **The defect follows the
narrow frequency plan and nothing else.** That makes MN140 against MP140 on one source a ready-made
controlled probe, and it points at narrow band and sync-tone handling — a 256 Hz band is 3.1x more
level-sensitive per Hz than an 800 Hz one — rather than a generic end-of-line off-by-one, which would
have shown in MP140 too.

It is present with every DSP option off, so it is not caused by any optional filter. Earlier
candidates, neither checked and both now lower-ranked than the frequency-plan lead above: the end-of-line window running past the last pixel centre, or the sync
search consuming samples the last pixels need. The first step is to decode a clean, noise-free
signal and see whether the artefact survives — if it does, it is a pure timing bug rather than a
noise-sensitivity one.
