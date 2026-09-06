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

## What the real-noise bench does NOT cover

Ordered by how much it matters.

1. **No fading.** No multipath, no Doppler spread, no selective nulls. The channel is flat and static.
   This is the largest gap. Watterson / ITU-R F.1487 is the right model for a 2.4 kHz channel and is
   noted in `decoder_quality_improvement.md` §16, not built.
2. **No frequency offset.** The encoder produces perfectly on-frequency signals
   (`sampleRateOffsetHz` defaults to 0). Anything whose job is to correct a frequency error is
   therefore invisible here. **Demonstrated:** the §5.3 AFC retune measured exactly `0.00` change on
   all 43 modes at every SNR — a null produced by an inapplicable test, not by an ineffective change.
   That same work had already produced one false null from a different methodology error.
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
