# Options stub backlog item 1: PLL demodulator tuning

Status: **DONE, committed (`9051699`).** Part of `docs/plans/options-advanced-stub-backlog-plan.md`.
Full build clean; full-solution tests green except the pre-existing, unrelated
`SstvCompositionRootTests` sample-rate-type flakes tracked elsewhere.

## What shipped

Exposes `CPLL`'s 5 legacy tuning knobs (VcoGain/LoopOrder/LoopCutoff/OutOrder/OutputCutoff,
`sstv.cpp:147,241-286`) as live, persisted Options settings on `PllFmDemodulator`, applied to both
the picture-path and AVT-training PLL instances via `RequestPllTuning` (mirrors the existing
`RequestNotch` live-apply pattern — `Interlocked.Exchange`, drained in the decode loop, no idle
gate). Real legacy defaults: `VcoGain=1.0, LoopOrder=1, LoopCutoff=1500.0, OutOrder=3,
OutCutoff=900.0` — the AXAML's old hardcoded values (2600/1/200/1200) were wrong and are fixed.

## Non-obvious facts worth preserving (not written anywhere else)

- **VcoGain output-scale cancellation.** Legacy's `SetVcoGain` sets `vco.SetGain(-bandwidthHz * g)`
  AND `outgain = 32768 * g` — the two `g` factors cancel at lock, so VcoGain only changes loop
  dynamics (lock speed/damping), never the reported frequency. Applying `g` to only one side
  silently corrupts every demodulated frequency by a factor of `g` — invisible to a round-trip
  test, same failure class as the Scottie incident (CLAUDE.md §4). `PllFmDemodulator.SetTuning`
  applies the multiplier in both places; a dedicated invariance test
  (`ProcessSample_DemodulatedFrequency_InvariantAcrossVcoGain`) guards it.
- **Two-sided clamps are load-bearing, not defensive padding.** A stale persisted cutoff at or
  above the configured sample rate's Nyquist frequency, or at/below zero, produces a permanently
  NaN decoder surviving every restart (`Math.Tan` diverges). Both the ceiling and floor clamp live
  in `PllFmDemodulator`'s constructor AND `SetTuning` — code review caught both gaps separately
  (round 1: ceiling only; round 2: floor only), both reproduced as a real crash before the fix.
- **Both PLL instances need tuning, including a freshly-constructed AVT instance.** Legacy has one
  `m_pll` shared by the picture path and AVT training; this port has two separate instances, and
  `_avtPllDemodulator` is rebuilt fresh per AVT attempt. The decoder holds the current tuning in
  fields (updated on drain) and passes them as constructor args at the AVT instance's own
  construction site — draining the pending request alone would miss a not-yet-constructed instance.
- Real range: order `[1,32]` (legacy's `IIRMAX=16` counts biquad sections, not order — order 32 is
  in bounds). Cutoff clamps against the sample rate ACTUALLY configured, not a static ceiling.
