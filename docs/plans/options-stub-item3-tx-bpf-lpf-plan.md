# Options stub backlog item 3: TX BPF / TX LPF

Status: **DONE, committed (`9051699`).** Part of `docs/plans/options-advanced-stub-backlog-plan.md`.
Code review: "no correctness defect found in any shipped code path." `docs/removed-features.md`'s
former BPF-toggle entry deleted (capability restored, not removed).

## What shipped

**TX BPF**: the filter math (`TxOutputBandpassFilter.cs`, a complete `CFIR2` port) already existed
and was already correct, applied unconditionally at a fixed 24 taps. This item restored the
user-facing on/off toggle and tap-count control that `docs/removed-features.md` had documented as a
deliberate-but-reconsidered removal — a reversal of that decision, not a stub fill. `Tap` became an
instance field (constructor parameter, default 24), clamped `[2,512]` and rounded to even.

**TX LPF**: genuinely new — legacy's `avgLPF`/`m_lpffq` (pre-VCO frequency smoothing, `CSSTVMOD`,
`sstv.cpp:2760-2761,2869,2929`) had no C# equivalent at all. Built inline in
`AnalogFmSstvEncoder.EncodeAsyncCore`'s per-sample loop using the same `MovingAverage` class item 2
ported. Default off (`m_lpf=0`); real range `[100, 3000]` Hz, default 2000 Hz when enabled.

## Non-obvious facts worth preserving

- **Odd tap counts are real, reachable undefined behavior in legacy — not just theoretical.**
  `MakeFilter` under-fills its allocation for an odd tap count, leaving one array slot garbage that
  `Do`'s loop then reads. The clamp restricts this port to even tap counts only; round 1's own
  first draft had the odd/even direction backwards before correction.
- **TX LPF smoothing is fed the raw Hz value directly, not legacy's normalized `(f-1100)/1200`
  intermediate** — `MovingAverage`/`CSmooz` is a linear operator, so `mean(a·x+b) = a·mean(x)+b`;
  smoothing raw Hz and skipping the normalize/denormalize round trip is algebraically identical,
  not a divergence.
- **Window-size formula rounds, not truncates**: `SetCount((int)(sampleRate/lpfFrequencyHz + 0.5))`
  — easy to confuse with `ZeroCrossingFrequencyCounter`'s sibling formula, which truncates. The
  `+0.5` had no test that could fail if dropped until code review added one (the original test
  frequency happened to make rounding and truncation agree).
- **TX LPF state is fresh-per-transmission, a deliberate divergence from legacy** (whose `avgLPF`
  state persists across transmissions, cleared only by `SetCount`). Simpler, deterministic, and
  practically indistinguishable — a partially-filled boxcar converges within one window's worth of
  a constant leading tone regardless.
- Real legacy validation ranges (`Option.cpp:452-459`): `m_lpffq` accepted only in `[100,3000]`;
  `m_bpftap` accepted only in `[2, TAPMAX=512]`.
