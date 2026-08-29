# Options stub backlog item 2: Zero-crossing demodulator tuning

**Item 2 (Zero-crossing tuning): DONE.** 1 plan-review round (explicit "yes, ready to build now"),
1 code-review round (explicit "go for production: yes"). One real regression-net gap found and
closed: the drain test asserted only decoder-level tracking fields (documented as non-load-bearing),
not that `_zeroCrossingDemodulator`/`_afcZeroCrossingCounter` were actually retuned — new
`SmoothingModeForTests` accessors close it, mutation-verified (dropping both `SetTuning` calls now
fails the strengthened test). Not committed.

Part of `docs/plans/options-advanced-stub-backlog-plan.md`'s 6-item backlog. Full heavy loop per the
user's standing instruction ("don't ask, just go until they are all done"). This plan proactively
bakes in every lesson item 1 (PLL tuning) took 2 code-review rounds to discover — see
`docs/plans/options-stub-item1-pll-tuning-plan.md`'s own "Round 1"/"Round 2" sections — rather than
re-discovering them here: both-direction (floor AND ceiling) clamps on every cutoff/order field from
the start, the `RequestNotch`-style live-apply mechanism (proven, not re-litigated), and applying
tuning to every live instance that shares the legacy field.

## Legacy behavior, read directly from source (not delegated, cross-checked by a fork for depth)

`CFQC` (`yoniq-old/YONIQ-main/sstv.h:399-438`, `sstv.cpp:347-489`) — the zero-crossing frequency
counter, already ported as `ZeroCrossingFrequencyCounter`. `m_Type` (driven by the Options dialog's
`RGcrossType` — labels "IIR"/"FIR"/"OFF", corroborated by the in-source `sstv.cpp:476/479/482`
comments, though `Option.dfm`'s own `Items.Strings` couldn't be read directly, a binary-DFM
limitation — treat the exact label casing/text as a lean, not a locked fact) selects the OUTPUT
SMOOTHING STAGE only — a completely separate concept from `CSSTVDEM::m_Type`
(the overall PLL/ZeroCrossing/Hilbert demod dispatcher, already fully ported as this port's
`DemodType` enum; do not conflate the two, they are different fields on different classes):

```
switch(m_Type) {
    case 0: m_out = m_iir.Do(m_fq); break;          // IIR lowpass, tuned by m_outOrder/m_outFC
    case 1: m_out = m_fir.Avg(m_fq); break;          // FIR moving average, tuned by m_SmoozFq
    default: m_out = m_fq; break;                    // OFF -- raw, unsmoothed passthrough
}
```

**IIR branch is reuse-only** — `CalcLPF`'s `m_iir.MakeIIR(m_outFC, m_SampFreq, m_outOrder, 0, 0)` uses
the exact same Butterworth-only (`bc=0, rp=0`) call shape `CPLL`'s own filters already use, already
faithfully ported as this codebase's `IirFilter` class. No new filter-design code needed.

**FIR branch (`CSmooz`) needs a genuinely new port** — this port's `ZeroCrossingFrequencyCounter`
currently hardcodes the IIR path only (`_outputFilter.Design(900, sampleRate, 3)` in its constructor)
and has no FIR or OFF path at all. `CSmooz` (`sstv.h:80-145`), read in full:

```
class CSmooz {
    double* bp; int Wp; int Max; int Cnt;
    CSmooz(int max = 2) { Max=max; bp=new double[max]; Cnt=0; Wp=0; }
    void SetCount(int n) {
        if (!n) n = 1;
        if (Max != n) { reallocate to size n; Cnt = Wp = 0; }
        else { Cnt = Wp = 0; }   // reset even if size unchanged -- a real quirk, replicate exactly
    }
    double Avg(double d) {       // write-then-read: this is the ONLY public entry point that both
        bp[Wp] = d; IncWp();      // updates AND reads -- there's no separate "write" call
        if (Cnt < Max) Cnt++;
        return Avg();             // private, parameterless: mean of the first Cnt slots
    }
}
```

A plain ring-buffer boxcar (moving-average) filter — port as a new small class, `MovingAverageFilter`,
matching this exactly (including the "reset on every `SetCount`, even same size" quirk).
`CalcLPF`'s own call: `m_fir.SetCount(m_SampFreq/m_SmoozFq)` — note the window size is DERIVED from
sample rate and the tunable `m_SmoozFq`, an `int` division of two doubles (C++ implicit truncation on
the parameter conversion).

**`m_Limit`**: confirmed dead for the decoder's own `CFQC` instance — set once in `CFQC`'s own
constructor (`sstv.cpp:351`, `=1`), never toggled in `Main.cpp`/`Option.cpp`. (One other reference
exists — `Fft.cpp:76` sets `m_Limit=0` on `CFFT`'s own SEPARATE `CFQC` instance used for the
spectrum display, not the decoder's; confirms `CFQC` tuning is per-instance and legacy deliberately
does not push Options-tab tuning to the spectrum-display instance — out of scope here, decoder-only.)
Never persisted, never dialog-exposed for the decoder's instance. This port's existing `ProcessSample`
already always clamps — legacy-faithful, no change needed.

## Real, cross-verified defaults (3 sites, zero drift — same discipline as item 1)

`m_Type=0` (IIR), `m_outOrder=3`, `m_outFC=900`, `m_SmoozFq=2200` — confirmed at the `CFQC` constructor
(`sstv.cpp:347-364`), the `Main.cpp` ini-load site (`:1938-1941`, which itself defaults to the
already-constructed in-memory value, so it cannot independently drift), and the demod-profile system's
own slots 1-2 re-assert (`Main.cpp:12252-12262` — "Zero crossing" and "Zero crossing with
Differentiator", not slot 6/7 as an earlier draft of this doc said — `crossOutOrder=3, crossOutFC=900`,
matching exactly).

**`crossSmooz` preset list — UNCONFIRMED, treat as a lean, not a fact.** `Option.dfm` is a binary DFM;
`crossSmooz`/`RGcrossType` are confirmed present in it but their `Items.Strings` could not be read
(auditor plan-review round 1 tried ~8 targeted probes, no readable text extracted). The candidate list
`1000, 1200, 1400, 1600, 1800, 2000, 2200, 2400, 2500, 2600, 2700, 3000, 3500, 4000, 8000` (Hz) is
this plan's own earlier lean, NOT source-confirmed, and sits slightly at odds with `Option.cpp:536-540`
being an `sscanf`+range-check shape (the shape legacy uses for a free-entry, editable combo, not a
locked preset list). Given the uncertainty, build the AXAML control as a free-entry `NumericUpDown`
clamped to `[500, 8000]` (source-confirmed range, `Option.cpp:536-540`) instead of a fixed preset
dropdown — matches the confirmed dialog-side validation shape exactly, doesn't require trusting an
unconfirmed preset list, and is consistent with how PLL's own cutoff fields are already built. `500`
is legacy's own real floor (`if (m_SmoozFq < 500) m_SmoozFq = 500.0;`, `CalcLPF`), `8000` the real
ceiling (`Option.cpp:536-540`) — both enforced as a clamp in the C# port (legacy rejects-and-keeps
instead; this port already treats every other legacy dialog-side reject as a clamp, consistently).

## Persistence (same shape as PLL, same shared version-migration risk — noted, not re-solved)

Ini keys confirmed for all 4 fields: `fqcOutOrder`/`fqcOutFC`/`fqcType`/`fqcSmooth`, load
(`Main.cpp:1938-1941`) and save (`:2437-2440`), both under `[Define]`. Also part of the demod-profile
system (save `Main.cpp:12335-12339`, load `:12380-12384`). The SAME version-migration gate item 1
found (`Main.cpp:2111`, a `PLLVer`/`ProVER`/`LCVer` version bump re-asserting profile slot 8's own
values) covers these 4 fields too, in the identical call — not a new, separate risk, already flagged
generally for the whole demod-profile system in item 1's own plan doc. `InitProfile()` (`Main.cpp:12209`,
`for (i = 0; i <= 8; i++)`) DOES explicitly set all 4 cross fields for every slot including slot 8
(`:12217-12220`) — an earlier draft of this doc wrongly flagged this as an unresolved edge case; there
is none, and no importer exists in this port regardless so the whole migration path is unreachable
here.

## Design (mirrors item 1's final, twice-corrected shape — applied from round 1 this time)

Corrected after auditor plan-review round 1 (explicit "yes, ready to build now," 5 one-line fixes,
several prose corrections folded in below — no rework needed):

1. **Reuse and extend `MovingAverage.cs`, no second `CSmooz` port.** This class already exists
   (`src/ScanlineStudio.Core.Sstv/MovingAverage.cs`) as a direct `CSmooz` port backing `AfcTracker` —
   a second, divergent port (`MovingAverageFilter`) would be exactly the drift hazard this project
   keeps flagging. Add the one piece `MovingAverage` is missing: a resizing `SetCount(int n)` matching
   `CSmooz::SetCount` (`sstv.h:118-128`) — if `n` differs from the current buffer length, reallocate
   to size `n` and reset `_count = _writeIndex = 0`; if `n` equals the current length, reset
   `_count = _writeIndex = 0` anyway (the real, deliberate "clear even when unchanged" `else` branch —
   already correctly implemented as `MovingAverage.Clear()`, so `SetCount` can just call `Clear()`
   after any needed reallocation). `_buffer` must become non-`readonly` to support reallocation.
2. **`ZeroCrossingFrequencyCounter.cs`**: new `SmoothingMode` enum (`Iir=0, Fir=1, Off=2`, matching
   legacy's own numeric values, same convention `DemodType`/`RxBpfPreset` already use). New
   constructor params: `smoothingMode = SmoothingMode.Iir, outputOrder = 3, outputCutoffHz = 900,
   smoothingFrequencyHz = 2200`. `ProcessSample` dispatches on the current mode (IIR -> existing
   `_outputFilter.Process`, FIR -> `_movingAverage.Add(...)`, Off -> raw `_currentFrequencyHz`
   passthrough, no filtering at all — confirmed `default: m_out = m_fq;` at `sstv.cpp:482`). New
   `SetTuning(SmoothingMode mode, int outputOrder, double outputCutoffHz, double
   smoothingFrequencyHz)` live mutator, mirroring `PllFmDemodulator.SetTuning`'s final (twice-
   corrected) shape:
   - Order clamped `[1, 32]` (legacy's own real range: `Option.cpp:529-531`,
     `(dd>0)&&(dd<=32)`) — applied in BOTH the constructor and `SetTuning`, not just one (item 1's
     round-1 blocker, avoided here from the start).
   - IIR cutoff clamped `[1.0, sampleRate * 0.45]` — BOTH floor and ceiling, in BOTH constructor and
     `SetTuning` (item 1's round-1 AND round-2 blockers, both avoided here from the start). Legacy's
     own dialog-side check (`Option.cpp:532-534`) is floor-only (`d>0.0`, no ceiling) — this port
     deliberately keeps the stricter Nyquist ceiling anyway, matching item 1's own accepted
     divergence for the same reason (a decoder-side value must never be allowed to destabilize the
     filter regardless of what a settings file contains).
   - Smoothing frequency clamped to **`[500, 8000]`** (legacy's real two-sided range, confirmed at
     `Option.cpp:536-540`: `if ((d >= 500.0) && (d <= 8000.0))` — legacy *rejects* an out-of-range
     entry and keeps the previous value rather than clamping; this port clamps instead, consistent
     with how it already treats every other legacy dialog-side reject-and-keep as a clamp) before
     computing window size (`sampleRate / smoothingFrequencyHz`, `int` truncation, matching
     `CalcLPF`'s own `m_fir.SetCount(m_SampFreq/m_SmoozFq)`); window size itself floored to `>= 1`
     (`MovingAverage.SetCount`'s own `if (!n) n = 1`, matching `CSmooz::SetCount`). Applied in BOTH
     the constructor and `SetTuning`, and ALSO added to `SstvDecoderSettings.Resolve()`'s own clamp
     (design step 5 below) — the original draft only clamped order+cutoff there, an omission the
     ceiling fix above must not repeat.
   - **`SetTuning` calls `SetCount` unconditionally on every invocation**, never guarded by "only if
     the window size actually changed" — legacy's own Options-OK handler always runs `CalcLPF()` ->
     `m_fir.SetCount(n)` -> buffer reset, even when `n` is unchanged (`sstv.h:125-127`'s `else`
     branch). A same-size guard would silently drop this reset; state it explicitly so the
     implementation doesn't add one out of a natural instinct to avoid unnecessary work.
   - **`NaN` guard**: `Math.Clamp(double.NaN, lo, hi)` returns `NaN` — clamp helpers must explicitly
     substitute a safe default (e.g. the field's own legacy default) before clamping, not rely on
     `Math.Clamp` alone, matching the same latent gap item 1's own clamp helpers still have (flagged
     here as a genuine, cheap fix rather than repeated as an unstated gap).
   - `SetWidth` does NOT need to re-apply smoothing tuning (unlike PLL's own `SetWidth` ->
     `SetVcoGain` chain) — confirmed from source: legacy's `CFQC::SetWidth` (`sstv.cpp:367-383`) only
     touches center/BWH/limit-clamp fields, never `m_Type`/`m_iir`/`m_fir`. No equivalent bug class
     exists here.
   - **`Clear()` must NOT touch the new smoothing state.** `CFQC::Clear` (`sstv.cpp:385-394`) resets
     only `m_d`/`m_Count`/`m_ACount`/`m_fq`/`m_out`/`m_Timer` — never `m_fir`/`m_iir`. The decoder
     calls `ZeroCrossingFrequencyCounter.Clear()`-equivalent logic at every Start/Stop
     (`AnalogFmSstvDecoder.cs` around lines 3781/3801/5781/5793) — the new `_movingAverage`/output-IIR
     state must be left alone by whatever this counter's own `Clear()` method does, not reset
     alongside the frequency-tracking fields.
   - **Out-of-range `SmoothingMode` falls back to `Off`, not `Iir`.** Confirmed: `sstv.cpp:482`'s
     `default:` case is the OFF/passthrough branch — this belongs to `CFQC::m_Type`, a completely
     different field from `CSSTVDEM::m_Type` (whose own `default:`-is-Hilbert reasoning does NOT
     transfer here, the original draft's lean was wrong). Absent value -> `Iir` (the real ctor
     default, 0); present-but-out-of-range value -> `Off` (the real dispatch default).
3. **`AnalogFmSstvDecoder.cs`**: BOTH `_zeroCrossingDemodulator` (main-picture-demod role) and
   `_afcZeroCrossingCounter` (AFC sync-frequency-measurement role) need the same tuning applied. Real
   legacy gating, corrected from an earlier wrong draft claim: `SyncFreq` is fed from `m_fqc.Do(...)`
   ONLY in the PLL demod case (`sstv.cpp:2256-2268`, `case 0`) — the Zero-crossing and Hilbert demod
   cases feed `SyncFreq` from the picture-path value directly, not through `CFQC::Do`. This port
   already gates that correctly (`AnalogFmSstvDecoder.cs:6171-6196`). This does NOT change the design:
   both instances still need the tuning pushed to them (whenever the AFC counter's smoothing path IS
   active, it must use the same configured smoothing legacy would), it only means a test must not
   assert the AFC counter runs its smoothing under every demod type — only when PLL is the active
   demod type. Simpler than PLL's own AVT case: BOTH instances are constructed ONCE, in the
   constructor (`:1015-1016`), never freshly reconstructed mid-session (no `_avtPllDemodulator`-style
   fresh-per-attempt complication) — a single `RequestZeroCrossingTuning` drain reaching both existing
   instances is sufficient, no held-fields-for-a-future-construction-site pattern needed.
   New `RequestZeroCrossingTuning(SmoothingMode, int outputOrder, double outputCutoffHz, double
   smoothingFrequencyHz)` on `ISstvDecoder`, same `RequestNotch`/`RequestPllTuning` deferred-request
   shape (`Interlocked.Exchange`, drained at the same `PushSamplesCore` point).
4. **`RestartableSstvDecoder.cs`**: same store-forward-and-re-seed shape as `RequestPllTuning` —
   wrapper-level fields + new ctor params, forwarded to `_inner` AND passed as `CreateInner`'s own
   `AnalogFmSstvDecoder` ctor args (unconditional re-seed, no off-state to gate on).
5. **`SstvDecoderSettings.cs`**: 4 new nullable fields (`ZeroCrossingSmoothingMode`,
   `ZeroCrossingOutputOrder`, `ZeroCrossingOutputCutoffHz`, `ZeroCrossingSmoothingFrequencyHz`).
   `Resolve()` clamps order `[1,32]`, cutoff floor `>=1.0` (no Nyquist ceiling here — same reasoning
   as PLL: no sample-rate context at this layer, the real ceiling enforcement lives decoder-side), AND
   smoothing frequency `[500, 8000]` (BOTH bounds — an earlier draft of this doc only clamped order
   and cutoff at this layer and left smoothing frequency unclamped here entirely; fixed so `Resolve()`
   guards all 3 numeric fields consistently, not 2 of 3).
6. **`ISstvSessionService`/`SstvSessionService.cs`/`ConfigurationPresetService.cs`**: same
   pass-through/live-push shape as `RequestPllTuning`'s own wiring.
7. **`OptionsSnapshot.cs`/`OptionsSettingsService.cs`/`OptionsWindowViewModel.cs`**: same 4-field
   load/save/live-push wiring shape.
8. **`OptionsWindowView.axaml`**: the currently-entirely-missing `RGcrossType` 3-way radio group
   (IIR/FIR/OFF — this port's AXAML never had ANY control for this at all, not even a disabled
   placeholder) PLUS the existing disabled Order/Cutoff/Smoothing group, now wired. Enable-gate,
   corrected from an earlier wrong draft claim: legacy's own real behavior IS grey-out
   (`->Enabled`, `Option.cpp:197-212`), not hide/show — `Visible` toggling at that dialog belongs to a
   different pair (`GBPLL` vs `GBCROSS`, `:180-196`), not `GBCOI`/`GBCOF`. This port's planned grey-out
   design already matches legacy directly, not a deliberate divergence from a hide/show original.
   `GBCOI` ("Output LPF (IIR)") holds BOTH the Order field AND the Cutoff field (`Option.h:155-156`) —
   both grey together, not Order alone as an earlier draft said. `GBCOF` ("Output LPF (FIR)") holds
   only the Smoothing-frequency field. Show both groups always in this port (per item 1's own
   established precedent of showing PLL/Zero-crossing simultaneously rather than replicating legacy's
   tab-switch-driven visibility); grey out whichever group isn't the currently-selected smoothing mode
   (both greyed when OFF is selected). Smoothing-frequency control is a free-entry `NumericUpDown`
   clamped `[500, 8000]` (see the corrected, now-unconfirmed-preset-list note above), not a preset
   `ComboBox`.
9. **`Program.cs`/loopback self-test construction/composition test**: same forwarding shape as PLL's
   own 4 sites.
10. Locale: new keys for the RGcrossType radio labels + the (currently entirely absent) Order/Cutoff/
    Smoothing labels' own real wiring (the AXAML controls exist today but are hardcoded-disabled with
    a "not implemented" tooltip only — confirm their existing locale keys, e.g.
    `Options.Advanced.Crossing.Order`, are already correct/reusable or need adjusting).

## Testing (mirrors item 1's final testing discipline, applied proactively)

- `MovingAverageTests.cs` additions (extending the existing file, not a new one — see design step 1):
  `SetCount` to a new size reallocates and clears; `SetCount` to the SAME size still clears (legacy's
  real "reset even if unchanged" quirk); the classic boxcar-average-of-a-known-sequence check.
- `ZeroCrossingFrequencyCounterTests.cs` additions, each an exact parity assertion with a stated
  tolerance, not a vague "behavior differs" check: FIR-mode output equals the arithmetic mean of the
  last N clamped-Hz samples (tolerance `1e-9`); OFF-mode output equals the clamped sample-and-hold
  value bit-exact; a dedicated window-size test pinning the truncation exactly (e.g. sample rate
  `11025`, smoothing frequency `2200` -> window size exactly `5`, not `5.01` or `6`); `SetTuning`
  applied live, no intervening `SetWidth`, still smooths correctly; order/cutoff/smoothing-frequency
  floor+ceiling clamps at BOTH the constructor and `SetTuning`; a `NaN` cutoff/smoothing-frequency
  input does not propagate NaN through to the filter design or window-size calculation.
- `RequestZeroCrossingTuningTests.cs` (new, mirrors `RequestPllTuningTests.cs`): decoder-level wiring,
  reaches BOTH `_zeroCrossingDemodulator` and `_afcZeroCrossingCounter`.
- `RestartableSstvDecoderTests.cs`: periodic-rebuild re-seed, mutation-tested by hand (same shape as
  `RequestPllTuning_SurvivesAPeriodicSwap`).
- `SstvCompositionRootTests.cs`: extend the existing composition test with the 4 new fields.
- `ConfigurationPresetServiceTests.cs`: extend the existing bundle-push test with the 4 new fields.

## Plan-review status

Round 1 (auditor): explicit "yes, ready to build now." 5 one-line fixes (reuse `MovingAverage`,
out-of-range `SmoothingMode` -> `Off`, `[500,8000]` smoothing-frequency ceiling, unconditional
`SetCount` in `SetTuning`, `Clear()` leaves FIR/IIR state alone) plus several wrong legacy-prose
claims (`m_Limit`, slot numbering, `InitProfile` wrinkle, Enabled-vs-Visible, AFC gating, preset-list
unconfirmed) — all folded into this doc above. No round 2 required; proceeding to implementation.
Also add `RequestZeroCrossingTuning` to the 3 `FakeSstvDecoder.cs` test fakes
(`Core.Logbook.Tests`/`Core.Imaging.Tests`/`Application.Tests`) and `FakeSstvSessionService`
(`UI.Tests/Fakes.cs`), same as item 1's own fan-out, plus a NaN-guard in every new clamp helper
(explicit safe-default substitution before `Math.Clamp`, since `Math.Clamp(NaN, lo, hi)` returns NaN).
