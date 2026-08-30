# Options stub backlog item 2: Zero-crossing demodulator tuning

Status: **DONE, committed (`9051699`).** Part of `docs/plans/options-advanced-stub-backlog-plan.md`.
Proactively applied every lesson item 1 (PLL tuning) needed 2 code-review rounds to discover.

## What shipped

Exposes `CFQC`'s output-smoothing-stage tuning (`sstv.h:399-438`, `sstv.cpp:347-489`) — a
completely separate concept from the overall PLL/ZeroCrossing/Hilbert `DemodType` dispatcher — as
live, persisted Options settings on `ZeroCrossingFrequencyCounter`: a 3-way `SmoothingMode`
(Iir=0/Fir=1/Off=2, matching legacy's numeric values) plus order/cutoff/smoothing-frequency knobs,
applied to both `_zeroCrossingDemodulator` and `_afcZeroCrossingCounter` via
`RequestZeroCrossingTuning` (same `RequestNotch`-style live-apply shape as item 1).

Real defaults: `Type=0 (IIR), OutOrder=3, OutCutoff=900, SmoozFq=2200`. Real ranges: order `[1,32]`,
IIR cutoff `[1.0, sampleRate*0.45]` (both directions, stricter than legacy's floor-only check —
deliberate, prevents a settings-file value from ever destabilizing the filter), smoothing frequency
`[500, 8000]` (legacy's own confirmed two-sided range).

## Non-obvious facts worth preserving

- **The FIR branch needed a genuinely new port.** `CSmooz` (a boxcar moving-average) had no C#
  equivalent; `MovingAverage.cs` already existed as a `CSmooz` port for `AfcTracker`, extended with
  a `SetCount(int n)` matching a real legacy quirk: it resets the buffer even when `n` is unchanged
  (`sstv.h:125-127`'s `else` branch) — a same-size guard would silently drop that reset.
- **Out-of-range `SmoothingMode` falls back to `Off`, not `Iir`.** `sstv.cpp:482`'s `default:` case
  IS the off/passthrough branch for `CFQC::m_Type` — a different field from `CSSTVDEM::m_Type`
  (whose own `default:`-is-Hilbert convention does NOT transfer here).
- **`Clear()` must not touch smoothing state.** `CFQC::Clear` resets only the frequency-tracking
  fields, never `m_fir`/`m_iir` — the port's `Clear()`-equivalent at every Start/Stop must leave the
  new moving-average/output-IIR state alone.
- **AFC gating**: the zero-crossing AFC counter's smoothing path is only active when PLL is the
  active demod type (`SyncFreq` is fed from `m_fqc.Do(...)` only in that case,
  `sstv.cpp:2256-2268`) — tests must not assert it runs under every demod type.
- `TxBpfTap`/`crossSmooz` preset lists in `Option.dfm` are unreadable binary data; both this item
  and item 3 build free-entry `NumericUpDown`s clamped to the confirmed numeric range instead of
  guessing a preset list.
