# What the decode-quality benches do and do not cover

Read this before quoting a number from either harness. Both measure something real and neither
measures reception. A result from them is true **within the conditions modelled**, and this file
exists so that boundary travels with the number instead of being rediscovered later.

Two things prompted it, both on 2026-09-06. A change measured exactly `0.00` across all 43 modes and
looked like a null result, when the real cause was that the bench cannot exercise that mechanism at
all. And an automated "which decode is cleaner" score ranked a **mis-framed** picture as the better
one, because its blank regions scored as clean. Both were caught by looking, not by measuring.

## The two instruments

| | `ImpairmentSweepHarness` / `RealNoiseImpairmentSweepHarness` | `OtaBaselineHarness` |
|---|---|---|
| signal | our own encoder, from a known source image | real received audio |
| noise | synthetic AWGN, or real recorded HF noise | whatever was on the band |
| ground truth | yes — the source image | **no** |
| answers | "is this decode more correct?" | "did anything change?" |

They are complements. Only the first can say a decode got *more correct*. Only the second sees a real
channel.

## The variance confound, and how to read a real-versus-Gaussian comparison

**Do not quote a real-versus-Gaussian variance ratio as a decoder property.** The real arm gives each
seed a different `(capture day, receiver)` stratum by design, so its realizations are several
different physical noise environments. The Gaussian arm's realizations are repeated draws of one
stationary process. A larger spread on the real arm is therefore expected by construction, and is a
property of the corpus and of the stratification, not of the decoder.

A second, independent confound points the same way. Calibration fixes **total** H2 (400–2500 Hz)
power. A clip whose in-band energy is partly a stationary carrier therefore receives less *broadband*
noise at the same nominal SNR, by `-10*log10(1-f)` dB for tonal fraction `f`. Worse, tonal energy
between 400 and 1100 Hz counts fully toward calibration but is removed by H1 before it can damage a
pixel. Both effects make carrier-rich realizations decode better for reasons that have nothing to do
with the decoder.

**What is unaffected:** any comparison **paired at fixed seeds** — before/after a code change, or the
A/B/C/D ablation. Both confounds are common-mode within a seed and cancel in the difference. Absolute
floors on the H2 axis, cross-mode comparisons, and any variance claim are affected.

**Sample size.** Five seeds cannot attribute anything. At n=5 a rank correlation needs |rho| >= 0.90
to reach 5% significance, and every per-seed covariate (carrier content, kurtosis, crest, clip-level
spread, day, receiver) is a descriptor of the same five mutually-confounded strata. Attribution
requires replication *within* one stratum, which `SCANLINE_IMPAIRMENT_SEEDS` supports.

Related: a shipped filter's benefit may be partly "it rejects real HF carriers" rather than "it lowers
a broadband noise floor". State which one the measurement supports.

## What the real-noise bench does NOT cover

Ordered by how much it matters.

1. **No fading.** No multipath, no Doppler spread, no selective nulls. The channel is flat and static.
   This is the largest gap. Watterson / ITU-R F.1487 is the right model for a 2.4 kHz channel and is
   noted in `decoder_quality_improvement.md` §16, not built.
2. **No frequency offset.** The encoder produces perfectly on-frequency signals
   (`sampleRateOffsetHz` defaults to 0), so anything whose job is to correct a frequency error is
   invisible here. **Demonstrated:** the §5.3 AFC retune measured exactly `0.00` change on all 43
   modes at every SNR — a null produced by an inapplicable test, not by an ineffective change. That
   same work had already produced one false null from a different methodology error.
   Note also that AFC's correct output on a clean signal is **not** zero: a calibration term in
   `AfcTracker` makes a perfect reading yield -3.125 Hz wide / -1.0 Hz narrow, asserted by an existing
   test. Scoring AFC against an assumed 0 is wrong; score deviation from a clean-signal baseline.
3. **Narrow noise provenance.** The corpus is 3 capture days, 36 receivers, one region of Europe.
   Real HF noise varies with band, hour, season, latitude and solar conditions. Measured
   realization-to-realization kurtosis already ranges 2.4 to 8.2 *within* this corpus.
4. **No transmitter-side impairment.** No over-deviation, no clipping, no non-flat transmit audio, no
   distant station's AGC pumping.
5. **One test image per mode.** Eight modes use a real golden-vector picture; the other 35 use a
   synthetic gradient. A filter's cost at edges depends heavily on edge content, so a single image
   under-samples exactly the failure mode a smoothing change would have.
6. **44100 Hz only.** Much real use is at 11025 Hz, where filter tap counts and the demodulator's
   decimation tier differ.
7. **No co-channel or adjacent-channel QRM as a controlled variable.** Some corpus clips contain real
   carriers and digital signals, which is a bonus, but interference is not swept.

## What the OTA bench does not cover

- **No ground truth.** Nobody knows what was transmitted, so it can only show that two decodes
  differ, never which is right. Judge by eye.
- **21 recordings plus whatever has been added**, of a handful of modes, on whatever conditions
  happened to occur.

## How to quote a result

- Say what was modelled. "A 9–13 dB floor improvement on MN/MC under real HF noise, no fading" is a
  claim. "H3 improves reception by 9–13 dB" is not.
- **A mean cannot see blur.** A change that softens every edge slightly can lower mean image delta
  while making pictures visibly worse. Gate on edge sharpness and p95 per-line delta as well, per
  `decoder_quality_improvement.md` §6.2.
- **Look at the pictures.** Every over-claim so far was caught by looking, not by a metric.
- A null result means "no effect **under these conditions**." Before believing it, check the bench
  can exercise the mechanism at all. See gap 2.

## Why changes ship default-off

The bench cannot model every real HF condition, and no bench could. So a DSP or filter change gets a
visible UI control and ships **default off** unless it shows a clear improvement **and** zero
degradation to lock or decode across all 43 modes. That converts "we could not test everything" into
"the operator can switch it off where we were wrong."

Field reports outrank bench results when they disagree. The bench says a change helps under the
conditions it models; only an operator can say it helps on the air.
