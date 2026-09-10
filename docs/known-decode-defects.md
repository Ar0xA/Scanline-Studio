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

**PROBE RUN 2026-09-10. Result: about +2.4, so Fault B is in the DSP chain, NOT protocol or TX-side.**
The probe is `tests/ScanlineStudio.Core.Sstv.Tests/IdealTransportGreenBiasProbe.cs`, gated behind
`SCANLINE_SOURCE_BMP`. It runs the real encoder's per-line frequency sequence straight into the
matching decoder — the decoder's "demodulated Hz at sample i" is exactly the Hz the encoder asked for
— so modulation, filtering and demodulation are all removed while the colour maths, the channel
layout, chroma subsampling, line pairing and `ToRgb`'s clamp are all still exercised. Source:
`photo-city`, the same photographic source §1's table used.

| mode | colour encoding | green bias, ideal transport | this table's real-chain figure |
|---|---|---|---|
| robot-36 | `YCbCrRobot` | **+2.4** | +4.7 |
| r24 | `YCbCrSequential` | **+2.5** | +4.8 |
| robot-72 | `YCbCrSequential` | **+2.4** | +4.4 |
| pd90 | `YCbCrLinePaired` | **+2.4** | +4.4 |
| mn110 | `YCbCrLinePaired` | **+1.4** | +3.8 |
| martin-m1 | `RgbSequential` | **+0.0** | +0.1 |
| scottie-s1 | `RgbSequential` | **+0.1** | +0.1 |

**The two RGB-sequential rows are the harness's own falsifier and they land on this table's own
measured +0.1.** That is what makes the YCbCr rows trustworthy rather than an artefact of the probe.

Reading: ideal transport reproduces Fault A and nothing more (+2.4 against the per-triple model's
+1.91 — the gap is photographic source against random triples, plus real per-pixel sampling). The
remaining ~+2.3 that the real chain shows appears only once modulation, filtering and demodulation
are in the path. **So this does not close as a parity decision.** It is a real DSP-chain defect and
earns the full review cadence.

What the probe does NOT rule out, stated so the next step is not over-scoped: it uses no windowed
averaging and an exact frequency at each sample, so anything the demodulator's own settling,
group delay or amplitude response does to chroma is still unexamined — and the chroma segments are
where the shortest dwell times live. That is the first place to look, not the last.

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

It is present with every DSP option off, so it is not caused by any optional filter.

**FIRST STEP RUN 2026-09-10. The stripe does NOT survive ideal transport — it is in the DSP chain,
not the scanline codecs.** Probe:
`tests/ScanlineStudio.Core.Sstv.Tests/NarrowModeEdgeColumnProbe.cs`, gated behind
`SCANLINE_SOURCE_BMP`. Ideal transport is stronger than the "clean, noise-free signal" this section
originally asked for: it removes modulation, filtering and demodulation entirely, so what remains is
only the colour maths, the channel layout and the codecs' own pixel geometry.

Mean absolute per-channel delta by column, `photo-city`, ideal transport:

| mode | band | mid-image | col -2 | col -1 | col -1 / mid |
|---|---|---|---|---|---|
| mn140 | 256 Hz | 2.2 | 1.9 | 2.7 | **1.2x** |
| mp140 | 800 Hz | 2.9 | 2.8 | 3.7 | **1.3x** |
| mn73 | 256 Hz | 4.2 | 6.8 | 5.4 | **1.3x** |
| martin-m1 | 800 Hz | 8.7 | 10.2 | 10.9 | **1.2x** |

**Narrow and wide are indistinguishable at the edge here.** Every mode sits at 1.2-1.3x, including
the clean wide-band control. Against the real chain's 4.7x for mn73 (23.2 mid against 108.8), that
relocates the defect completely: no end-of-line off-by-one, no sync-search sample theft, no pixel
geometry error. Those earlier candidates are ruled OUT, not merely lower-ranked.

**The frequency-plan lead survives, but through a different mechanism than assumed.** It is not that
the narrow plan makes the codec mis-index. It is that the narrow plan makes the DSP chain misbehave
near a line boundary. The physically coherent version: MN's sync is 1900 Hz with a picture band
starting at 2044, a 144 Hz gap, where MP's sync is 1200 against a band starting at 1500, a 300 Hz
gap. Filter transition and group delay let the approaching NEXT-line sync transient reach the picture
band early, and the right edge is exactly where "early" lands. That would explain the side of the
image affected, the family selectivity, and why every DSP option being off does not help — the RX
bandpass and demodulator are not optional.

**Unexplained and not chased:** mc180 read 0.7 mid with 0.0 at both edge columns under ideal
transport, far cleaner than its siblings. Either the mode is genuinely trivial to reconstruct at this
source, or the probe mishandles it. Worth one look before relying on any mc180 number.

**Next step is not another ideal-transport run.** It needs the real chain with the sync tone
manipulated — for instance decoding an MN line whose following sync is replaced by silence or by
MP's 1200 Hz, and watching whether the edge error follows.
