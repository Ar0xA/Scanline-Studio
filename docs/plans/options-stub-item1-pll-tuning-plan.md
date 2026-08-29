# Options stub backlog item 1: PLL demodulator tuning

Part of `docs/plans/options-advanced-stub-backlog-plan.md`'s 6-item backlog. Full heavy loop per the
user's explicit instruction. **Round 2** — round 1 found a real DSP correctness bug in the original
proposal (caught on paper, before any code) plus a false claim about legacy persistence; both fixed
below, along with the architecture/mechanism decisions round 1 flagged as unresolved.

## Legacy behavior (corrected)

`CPLL` (`sstv.cpp:147,241-263`) — the already-ported PLL FM demodulator (`PllFmDemodulator.cs`, live
as `DemodType.Pll`). Real constructor defaults, confirmed at 3 independent legacy sites
(`sstv.cpp:246,251-254`; `sstv.cpp:1431-1436`; `Main.cpp:12211-12215`):

```
m_vcogain = 1.0, m_loopOrder = 1, m_loopFC = 1500.0, m_outOrder = 3, m_outFC = 900.0
```

**Correction: legacy DOES persist these**, contrary to round 1's own draft — `Main.cpp:1955-1961`
reads `[Define]` ini keys `pllVcoGain`/`pllLoopOrder`/`pllLoopFC`/`pllOutOrder`/`pllOutFC` straight
onto `pDem->m_pll` at startup (then calls `MakeLoopLPF()`/`MakeOutLPF()`), `Main.cpp:2442-2446` writes
them back on exit. `Option.cpp:255-259`/`:511-527` is only the Options-dialog's own live-edit path,
not the whole persistence story. **This resolves the "design question" round 1 raised as trivial: no
tension exists — persisting matches BOTH legacy's real behavior AND this port's own established
convention.** These 5 fields are ALSO part of legacy's per-profile demod-preset system
(`Main.cpp:12211-12215,12329-12333,12372-12378,12430-12434`) — out of scope for this item, noted for
whoever eventually tackles backlog items #5/#8, which touch the same profile system.

## Blocker found and fixed: VcoGain output-scale corruption

`SetVcoGain` (`sstv.cpp:281-286`) sets TWO things, not one: `vco.SetGain(-m_Shift * g)` AND
`m_outgain = 32768.0 * g`. `CPLL::Do` returns `outLPF.Do(m_out) * m_outgain` (`sstv.cpp:342`) — the
`× g` on the output exactly cancels the `× g` baked into the VCO gain, so the demodulated frequency
scale is **g-invariant by design**; VcoGain only changes loop dynamics (lock speed/damping), never
the reported frequency.

`PllFmDemodulator.cs`'s current Hz conversion is `_centerFrequencyHz - filteredOut * _bandwidthHz`
(`:141`), fed by `_vco.SetGain(-_bandwidthHz)` (`:107`, currently hardcoded, no `g`). The original
round-1 proposal added `g` to the VCO gain call ONLY — that would have made every demodulated
frequency wrong by a factor of `g` for any VcoGain ≠ 1.0 (silent pixel-mapping/sync corruption,
invisible to a round-trip test, same failure class as the Scottie incident CLAUDE.md §4 warns about).
**Fix: apply the SAME `vcoGain` multiplier in both places** —
`_vco.SetGain(-_bandwidthHz * _vcoGain)` in `SetWidth` (misnamed `SetFreeFreq` in round 1's draft;
the real method is `SetWidth`, `:99-108`) AND
`_centerFrequencyHz - filteredOut * _bandwidthHz * _vcoGain` in the Hz-conversion step — the two
multiplications cancel at lock, exactly mirroring legacy's own VCO-gain/output-gain pair. Needs a
DIRECT test: demodulate a steady tone at several VcoGain values, assert the reported Hz is invariant
(not just "does the loop still lock") — a round-trip test alone would NOT catch this bug.

## Architecture decisions (resolved this round)

**AVT PLL instance: wire both.** Legacy has ONE `m_pll`, used for both the picture path
(`sstv.cpp:2259`, `m_Type==0`) and AVT training (`sstv.cpp:2129/2159/2169/2187/2222`, unconditional of
`m_Type`) — so legacy's 5 knobs affect AVT training too. This port has two separate instances,
`_pllDemodulator` (picture path) and `_avtPllDemodulator` (AVT training) — both
`AnalogFmSstvDecoder.cs`. Decision: apply the same 5 tuning values to both, matching legacy's single-
instance-affects-both-paths behavior. A future AVT-specific override is out of scope (legacy has none
either).

**Live-apply mechanism: in-place update via the `RequestNotch` pattern, NOT the idle-gated
Swap/`PendingReconfiguration` mechanism.** Round 1 correctly flagged two problems with the
Swap-based approach (`RestartableSstvDecoder`'s `RequestRxBpfPreset`-style path): (1) legacy applies
these in-place, mid-reception, with no decoder reset (`Option.cpp:512-527`) — the Swap path is
idle-gated (`_inner.IsIdle`) and would silently refuse to apply mid-image, discarding all in-flight
decode state, a real behavioral regression vs. legacy; (2) `PendingReconfiguration` is a positional
4-field record hand-enumerated at 6 call sites (`RestartableSstvDecoder.cs` `:1049,1074,1106,1362-1377,
1411,1429`) — appending 5 more fields risks a silent positional mis-map at any of those sites
(`new PendingReconfiguration(null, null, null, preservedRate)`-style calls), a real footgun for a
low-value gain (these params have no interaction with RxBpfPreset/DemodType/RxBufferMode that would
need preserving, unlike that record's existing fields).

This port ALREADY has a lighter, precedented mechanism for exactly this shape — live, in-decode-loop
parameter updates with no idle-gate and no decoder swap: `RequestNotch(bool, double?)`
(`AnalogFmSstvDecoder.cs:1481`), an `Interlocked.Exchange` into a pending-request field, drained
inside the decode loop. Adopt the same shape: a new `RequestPllTuning(double vcoGain, int loopOrder,
double loopCutoffHz, int outputOrder, double outputCutoffHz)` on `ISstvDecoder`/
`AnalogFmSstvDecoder`, `Interlocked.Exchange`d into a new `_pendingPllTuningRequest` field, drained at
the same decode-loop point `RequestNotch` already drains at.

**`RestartableSstvDecoder` (round 2 correction): store-forward-and-re-seed, NOT a bare pass-through.**
`RequestNotch`'s own real shape there is store-at-wrapper (fields, `RestartableSstvDecoder.cs:149-150`)
PLUS a re-seed inside `CreateInner` (`:1437-1443`, with its own dedicated `InnerNotchEnabledForTests`
test hook, whose doc comment explicitly says a dropped re-seed would otherwise be undetectable) — a
literal pass-through would let PLL tuning silently revert to defaults on this decoder's own periodic
rebuild. Port the identical shape: `RequestPllTuning` stores the 5 values on
`RestartableSstvDecoder`'s own fields (in addition to forwarding to `_inner`), and `CreateInner`
re-seeds them on every rebuild, with an equivalent `InnerPllTuningForTests`-style hook.

**AVT PLL instance (round 2 correction): the decoder must hold current tuning in fields, applied AT
the AVT instance's own construction site, not just at the drain point.** `_avtPllDemodulator` is
nullable and constructed FRESH per AVT-training attempt (`AnalogFmSstvDecoder.cs:5510`, default ctor
args, nulled at several sites — `:2613/3746/5625/5641/5658/5668`). Draining
`_pendingPllTuningRequest` only reaches whichever PLL instances already exist at that moment — a
freshly-(re)constructed `_avtPllDemodulator` between drains would silently get default tuning,
defeating the whole "wire both instances" decision above. Fix: hold the current (applied) tuning
values in decoder-level fields (updated whenever `_pendingPllTuningRequest` is drained), and pass
them as constructor arguments at `_avtPllDemodulator`'s own construction site (`:5510`), not just to
the picture-path instance.

**`PllFmDemodulator.cs` needs real runtime mutators, not just a VcoGain ctor parameter (round 2
correction).** `_loopFilter`/`_outputFilter` are private, `Design`d only once in the constructor
(`:83-84`) — there is currently no way to change LoopOrder/LoopCutoff/OutputOrder/OutputCutoff after
construction at all. Add a `SetTuning(double vcoGain, int loopOrder, double loopCutoffHz, int
outputOrder, double outputCutoffHz)` method mirroring legacy's own `SetVcoGain` +
`MakeLoopLPF`/`MakeOutLPF` shape: updates `_vcoGain`, **immediately pushes it into the VCO**
(`_vco.SetGain(-_bandwidthHz * _vcoGain)`, round-3 correction — legacy's own `SetVcoGain` does this
synchronously, `sstv.cpp:283`; a version that only updates the field and waits for the next
`SetWidth` breaks the g-cancellation the whole blocker fix depends on, in the same failure class
round 1 caught, just inverted), re-`Design`s both filters (needs its own `_sampleRate` field, not
currently present — ctor-param-only today), and clamps LoopCutoff/OutputCutoff against
`_sampleRate`'s own Nyquist frequency HERE, decoder-side (round-3 correction — `Resolve()` is
parameterless and called at composition-root/preset-switch sites only, never on the live
Options>Save push path, so clamping there would leave the live path unclamped against a
user-typed sub-8000Hz sample rate). `SetWidth` must re-apply the CURRENT `_vcoGain` when it
(re)computes `_bandwidthHz`, matching legacy's own `SetWidth` → `SetVcoGain(m_vcogain)` chain
(`sstv.cpp:278`).

**Filter-state reset on re-`Design` (round 2 risk, decide explicitly, don't leave implicit)**:
`IirFilter.Design` allocates a fresh zeroed `_z` (`IirFilter.cs:22`) — a live re-tune momentarily
unlocks the PLL loop in this port. Legacy's `MakeIIR` recomputes coefficients only; `CIIR::Clear` is
a SEPARATE call never invoked on this path, so legacy's own live-edit does NOT reset filter state.
Decision: accept the momentary-unlock divergence as a documented, acceptable cost of a live in-place
retune (matches this port's own general tolerance for brief settle-time on other live-apply settings,
e.g. RxBpfPreset) rather than adding state-preservation machinery `IirFilter` doesn't have today — but
state this as a deliberate, documented choice in the code, not a silent gap.

## Real defaults vs. the AXAML's current (wrong) hardcoded values

| Field | AXAML today | Real value (3-site-verified) |
|---|---|---|
| VcoGain | 2600 | **1.0** |
| LoopOrder | 1 | 1 (already correct) |
| LoopCutoff | 200 | **1500.0** |
| OutOrder | *(control missing entirely)* | 3 |
| OutCutoff | 1200 | **900.0** |

`PllFmDemodulator.cs`'s own constructor defaults (1/1500/3/900) are already correctly ported from
`CPLL`'s real constructor — only the AXAML's hardcoded UI values and VcoGain (no parameter exists yet)
need fixing/adding.

## Range ceilings (round 2's "16, not legacy's buggy 32" was itself wrong — corrected here)

Round 2 claimed `IIRMAX=16` (`fir.h:152`) is a MAX ORDER and that legacy's own `(0,32]` guard
overflows `CIIR`'s fixed arrays. **False, caught in round-2 plan-review**: `IIRMAX` counts biquad
SECTIONS, not order — `MakeIIR` advances 2 orders per section (`fir.cpp:967`), so order 32 = 16
sections = exactly `A[48]/B[32]/Z[32]` (`fir.cpp:1009-1011`), fully in bounds. Legacy's `<=32` guard
is internally consistent with `IIRMAX`, not a bug. **Final decision: restore `Maximum=32`, matching
legacy exactly** (`IirFilter.Design` over-allocates relative to what it needs, so 32 is safe here
too) — no justification remains for deviating from legacy's own range.

- **LoopOrder / OutOrder**: `Minimum=1, Maximum=32` (legacy-faithful).
- **LoopCutoff / OutCutoff**: `Minimum` just above 0 (never exactly 0 — `Math.Tan(π·fc/fs)` in
  `IirFilter.cs:24` is undefined at fc=0, unstable at/above Nyquist). Legacy has NO cutoff ceiling at
  all (`Option.cpp:517-524`, only `d > 0.0`) — this port's own deviation, stated plainly as
  deliberate, not a legacy citation: clamp at the RESOLVE layer (not just a static AXAML `Maximum`,
  since the sample-rate ComboBox is user-editable, `OptionsWindowViewModel.cs:118-130`) against the
  ACTUAL configured sample rate's own Nyquist frequency, with margin (e.g. reject/clamp at
  `sampleRate * 0.45`). The AXAML control still needs a sane static `Maximum` too (for the stepper's
  own UI range) — set it against the lowest selectable preset (8000 Hz → Nyquist 4000 Hz), e.g.
  `Maximum=3900`, understanding the REAL enforcement is the resolve-layer clamp against whatever rate
  is actually active.
- **VcoGain**: legacy has no ceiling either; `Maximum=10` (10x default) signed off round 3 — no
  legacy anchor, but at g=10 the capture window (±12000 Hz) is sloppy, not unstable, so it's a UI
  bound not a correctness bound. `Minimum`: `PllFmDemodulator`'s own `±1.5` clamp on the loop-filter
  output (`PllFmDemodulator.cs:136`, mirrors `sstv.cpp:332-337`) means the capture window is
  `±1.5·bandwidthHz·vcoGain` Hz around the 1900 Hz center; **round-3 correction** — covering the
  full 1500-2300 Hz SSTV tone range needs `g ≥ 0.334` (1.5·800·0.334 ≈ 400 Hz either side), not the
  0.1 originally proposed (which only covers ±120 Hz and cannot lock across the actual tone range at
  all). `Minimum=0.35`.

The current AXAML also has `Minimum="0"` on 3 of the 4 existing controls (`OptionsWindowView.axaml`
lines ~870/874/876) — all must move to a value `> 0`, matching legacy's own `> 0.0`/`(dd > 0)` guards.

## Enable-gate rule (round 1's own uncertain guess, now resolved)

Full switch read at `Option.cpp:179-196`: index 0 (PLL selected) → `GBPLL.Enabled=TRUE`; index 1
(Zero-crossing) → PLL group untouched (its own visibility hides it); `default` (index 2, Hilbert,
legacy's actual compiled-in default per `sstv.cpp:1492`) → `GBPLL.Enabled=FALSE`. Driven by
`RGDemTypeClick`'s live PENDING radio selection inside the dialog (`Option.cpp:1130-1134`), not the
already-committed `DemodType` (which only updates on Save, `Option.cpp:542`).

Port as: `IsEnabled` on the PLL tuning group bound to the ViewModel's own pending `DemodType`
selection (the existing radio-button-bound property, not the committed settings value) —
`IsDemodTypePllSelected`. Do NOT port legacy's Visible/hide-show churn — this port already shows both
PLL and Zero-crossing groups simultaneously; a greyed-out inactive group reads better than one
vanishing, and nothing about porting the DSP behavior requires copying the old dialog's layout
mechanics too.

## Full change surface (round 1 caught this undercounted at all; round 2 caught it still undercounted at 10 — 14 items, verified against real call sites this time)

1. `PllFmDemodulator.cs`: `vcoGain` field + constructor param; new `SetTuning(...)` mutator (real
   runtime setter, not just a ctor param — round 2 correction above); `SetWidth` re-applies current
   `_vcoGain`; apply the multiplier in both `SetWidth`'s VCO-gain call and the Hz-conversion step
   (blocker fix); update the doc comment that currently states VcoGain isn't ported.
2. `AnalogFmSstvDecoder.cs`: new `RequestPllTuning(...)` + `_pendingPllTuningRequest` field (mirrors
   `RequestNotch`), drained at the same decode-loop point; decoder-level fields holding the CURRENT
   applied tuning (round 2 correction — needed so a freshly-constructed `_avtPllDemodulator` at
   `:5510` gets real tuning, not defaults); at drain time, call `SetTuning` on `_pllDemodulator` AND
   (round-3 correction) `_avtPllDemodulator?.SetTuning(...)` if an AVT attempt is already in flight —
   legacy's single `m_pll` instance means a retune mid-AVT-attempt affects it too, not just a
   not-yet-started one; ALSO pass the held fields as constructor args at `_avtPllDemodulator`'s own
   construction site for the not-yet-started case.
3. `ISstvDecoder.cs`: new `RequestPllTuning` interface member.
4. `RestartableSstvDecoder.cs`: store-forward-and-re-seed (round 2 correction — NOT a bare
   pass-through): new optional constructor parameters (round-3 correction — `Program.cs:886`
   forwards settings as ctor args, not post-construction field sets) that initialize new fields,
   forwarded to `_inner` on `RequestPllTuning` AND re-seeded inside `CreateInner` on every rebuild.
   The re-seed must be UNCONDITIONAL (round-3 correction — unlike `RequestNotch`'s own re-seed,
   which is gated on `_notchEnabled` because a fresh decoder defaults to notch-off, PLL tuning has no
   analogous off-state; an unconditional re-seed is the correct mirror here, not a literal copy of
   notch's own gated shape). Plus an `InnerPllTuningForTests`-style hook mirroring
   `InnerNotchEnabledForTests`'s own existing precedent.
5. `SstvDecoderSettings.cs`: 5 new fields, `Resolve()`/`ResolvedSstvDecoderSettings` updated, real
   defaults from the table above.
6. `ISstvSessionService`/`SstvSessionService.cs`: new `RequestPllTuning` pass-through (mirrors however
   `RequestNotch`'s own session-layer call is already shaped).
7. `ConfigurationPresetService.cs` (`:293-298`'s decoder-settings push): without the 5 new fields
   here, switching to a configuration with different PLL tuning silently fails to apply it live.
8. `OptionsWindowViewModel.cs`: 5 new bound properties; loads the 5 persisted values when the dialog
   opens (matching legacy's own `Option.cpp:255-259` dialog-load path) AND pushes a live
   `RequestPllTuning` call from `SaveCoreAsync` (round 2 correction — without this, only preset
   switching would ever apply the values live, never Options > Save, the primary real entry point);
   confirm `IsDemodTypePllSelected` already exists for the enable-gate (cited at
   `OptionsWindowView.axaml:716` per round-2 verification) or add it.
9. `OptionsWindowView.axaml`: remove `IsEnabled="False"` AND the `ToolTip.Tip="{loc:Translate
   Options.NotImplemented.Help}"` on the same `Grid` (round-3 catch — a now-working group would
   otherwise still say "not implemented" on hover), bind the group's `IsEnabled` to the pending-
   DemodType gate, add the missing OutOrder control, fix all 5 controls' Minimum/Maximum/defaults per
   the ranges above (LoopOrder's `Maximum=32` is already correct in the AXAML today — only the new
   OutOrder control and the cutoff/VcoGain fields actually need their values changed).
10. Locale: new key for the OutOrder label.
11. `Host/Program.cs` (`:886` area): `RestartableSstvDecoder`'s own construction must forward the
    persisted PLL settings, same as every other decoder-construction setting already forwarded there
    (round 2 finding — missed in the original 10-item list).
12. `SstvSessionService.cs` (`:2520` area, the one-shot loopback self-test's own separate decoder
    construction): must also receive the persisted PLL settings — `SstvDecoderSettings.cs:127-130`'s
    own doc comment already flags a drift between this path and the main decoder construction path as
    a real, previously-identified hazard class, not a new concern invented for this item.
13. Test hook: `InnerPllTuningForTests`-shaped accessor on `RestartableSstvDecoder`, mirroring item 4.
14. Composition-root test: `tests/ScanlineStudio.Host.Tests/SstvCompositionRootTests.cs:254-299`
    already asserts 8 forwarded fields via `Inner*ForTests` hooks (enabled by
    `Core.Sstv/AssemblyInfo.cs:11-17`) — EXTEND that existing test with the 5 new PLL fields
    (round-3 correction) rather than writing a new one.

## Testing

- **Load-bearing, must NOT be skipped**: a direct demodulated-frequency-invariant-across-VcoGain
  test (the blocker fix above), with a STATED numeric tolerance (CLAUDE.md's tolerance rule —
  "invariant" alone isn't a testable assertion), using tone/VcoGain combinations chosen to stay
  INSIDE the `±1.5·bandwidthHz·vcoGain` capture window (see the VcoGain `Minimum` reasoning above) —
  a combination outside that window fails even against a CORRECT implementation, which would misread
  as the bug still being present. Mutation-test it by reverting to a single-multiplier (VCO-gain-only)
  version and confirming the test fails. Round-3 addition: a live `SetTuning` VcoGain change with NO
  intervening `SetWidth` call must still report the same Hz — the mutation guard for `SetTuning`
  forgetting to call `_vco.SetGain` immediately (round-3-caught inverted mirror of the same bug).
- Round-3 addition: a cutoff-clamp test on the LIVE push path (`SetTuning`/drain), not only at
  `Resolve()` — confirms the Nyquist clamp actually lives decoder-side where it's reachable from
  Options>Save, not only from composition-root/preset-switch paths.
- `RequestPllTuning` reaching both PLL instances (picture + AVT) — a test that arms AVT training and
  confirms the AVT instance's own tuning changed too, not just the picture-path one, specifically
  covering a FRESH `_avtPllDemodulator` construction (not just an already-existing instance at drain
  time) per the round-2 AVT-instance correction.
- `RestartableSstvDecoder`'s own periodic-rebuild re-seed — the `InnerNotchEnabledForTests` precedent
  this item's own `InnerPllTuningForTests` hook mirrors — proving a rebuild does NOT silently revert
  to default tuning.
- `ConfigurationPresetService`: switching between two presets with different PLL tuning actually
  changes live decoder output (same shape as the existing `PushDecoderChanges` tests).
- Composition-root forwarding: `Program.cs`'s decoder construction AND the loopback self-test's own
  separate construction (`SstvSessionService.cs:2520` area) both receive the persisted PLL settings —
  two tests, not one, given `SstvDecoderSettings.cs:127-130`'s own documented drift hazard between
  those two construction paths.
- Range validation at the ViewModel/settings-resolve layer: order clamped to [1,32] (legacy-faithful,
  corrected from round 2's wrong 16), cutoffs clamped against the sample rate ACTUALLY configured
  (not just a static AXAML ceiling), VcoGain clamped to the stated [0.1, 10] range.
- Enable-gate: toggling the pending DemodType radio selection flips the PLL group's `IsEnabled`
  without needing to Save first.
- `OptionsWindowViewModel`: opening the dialog loads the 5 persisted values (not just defaults), and
  Save pushes a live `RequestPllTuning` call — both are real entry points per the corrected change
  surface, neither should be assumed to "come for free" from the other.

## Implementation status (post-build)

All 3 rounds' findings implemented. **Real bug caught by the plan's own mandated test, before
shipping**: the initial implementation pass added the `* _vcoGain` multiplier to `SetWidth` and
`SetTuning`'s VCO-gain call correctly, but the author simply forgot to also add it to
`ProcessSample`'s own Hz-conversion return line — the exact blocker this whole plan exists to
prevent, reintroduced by omission, not by a wrong idea. `ProcessSample_DemodulatedFrequency_InvariantAcrossVcoGain`
failed immediately and precisely (results matched the UN-fixed formula's predicted values exactly),
caught and fixed before any commit. This is the load-bearing test doing its job, not a footnote.

Tests written: `PllFmDemodulatorTests.cs` (6 new — invariance theory + dedicated mutation-guard +
no-intervening-SetWidth + Nyquist clamp), `RequestPllTuningTests.cs` (new file, 3 tests — decoder-
level wiring), `RestartableSstvDecoderTests.cs` (+1 — periodic-rebuild re-seed, mutation-tested by
hand: reverted the ctor-arg forwarding, confirmed the test fails with the exact expected/actual
mismatch, restored), `SstvCompositionRootTests.cs` (extended the existing 8-field composition test
with the 5 new fields, non-default values per that test's own established discipline).

**Deferred to code review, not written in this pass** (flagged explicitly, not silently skipped):
a full end-to-end "AVT training reaches a freshly-constructed `_avtPllDemodulator` with live tuning"
test (the decoder-level `RequestPllTuningTests` prove the WIRING; a genuine AVT-timing-dependent test
was judged disproportionately fragile/complex for this pass); `OptionsWindowViewModel` load/save
round-trip and enable-gate tests.

## Round 1 code review — 1 blocker, 1 risk, 2 vacuous tests, all fixed

- **Blocker**: the Nyquist clamp lived ONLY in `SetTuning`, not `PllFmDemodulator`'s own constructor
  — every production construction site (composition root, periodic rebuild, live sample-rate
  rebuild, fresh AVT attempt) goes through the constructor, not `SetTuning`, so a low sample rate +
  a stale persisted cutoff produced a permanently NaN decoder (picture + AVT) surviving every
  restart with no error. Fixed: extracted `ClampCutoffBelowNyquist`, called from both places.
  Mutation-tested (a real crash reproduced, not hypothetical — see below).
- **Risk**: filter order had no validation anywhere below the AXAML — `IirFilter.Design`'s own
  `new double[order*3]` throws `OverflowException` for a negative order (reproduced directly: a
  hand-edited settings.json/preset with `PllLoopOrder: -1` crashed decoder construction). Fixed two
  places: `SstvDecoderSettings.Resolve()` now clamps to legacy's own real (0,32] range (matching the
  existing `SenseLevel`/`DemodType`/`RxBpfPreset` precedent it had been missing), AND
  `PllFmDemodulator` itself clamps via a new `ClampFilterOrder` helper (defense in depth — `RequestPllTuning`
  is a public `ISstvDecoder` member reachable from a live push that never goes through `Resolve()` at
  all). Mutation-tested: reverted the constructor's own clamp call, confirmed `OverflowException` at
  order=-1 exactly as the auditor predicted, restored.
- **2 vacuous tests, fixed**: `SetTuning_VcoGainChange_WithNoInterveningSetWidth_StillReportsCorrectFrequency`
  and `SetTuning_CutoffAboveNyquist_ClampsInsteadOfDestabilizing` both used a 1900 Hz test tone — the
  PLL's own center frequency, where `loopOut` converges to exactly 0 at lock regardless of VcoGain or
  filter design, so both would have passed even with the bug fully reintroduced. Both switched to an
  off-center 2200 Hz tone. Added 2 more tests: `Constructor_CutoffAboveNyquist_ClampsInsteadOfDestabilizing`,
  `Constructor_OutOfRangeFilterOrder_ClampsInsteadOfThrowing`.
- Also fixed: 2 stale locale strings (`Options.Advanced.Caption` still said "None of these are wired
  to real behavior yet"; `Options.Advanced.PllHeader.Help` said "used for AVT modes today," now
  correctly also mentions the picture-demodulator selection). Extended
  `SwitchToPresetAsync_DecoderBundleChanged_PushesTheWholeBundle` (`ConfigurationPresetServiceTests.cs`)
  with the 5 PLL fields, same non-default-values discipline as its own existing fields.
- **Not fixed, noted as pre-existing and out of this item's scope**: the loopback self-test's own
  decoder construction (`SstvSessionService.cs:~2538`) has NO existing forwarding test for ANY of the
  8 decoder-settings fields it already forwards (DemodType/RxBpfPreset/etc.), not just the 5 new PLL
  ones — building one would need a new observability hook into a fully-encapsulated, disposed-before-
  return decoder instance, a real side project beyond this item.

## Round 2 code review — 1 more blocker (same class, the lower bound), explicit go after the fix

The Nyquist clamp (round 1's fix) only guarded the CEILING (`Math.Min`) — a zero or negative cutoff,
reachable the identical way (hand-edited settings.json/preset), hit the exact same failure class:
`Math.Tan`'s argument goes negative, the filter pole diverges, permanently NaN decoder surviving
every restart. Legacy guards this too (`Option.cpp:517-518,523-524`, `> 0.0`), just at its own edit
site rather than centrally. Fixed: `ClampCutoffBelowNyquist` is now `Math.Clamp(cutoffHz, 1.0, ...)`,
and `SstvDecoderSettings.Resolve()` gained a matching lower-only clamp (`Math.Max(x, 1.0)` — no
Nyquist context available at that layer, by design, see round 3's own original decision). Mutation-
tested by hand: reverted to `Math.Min`, reproduced a real `NaN`/`Infinity` result exactly as
predicted, restored. 3 new tests: `Constructor_CutoffAtOrBelowZero_ClampsInsteadOfDestabilizing`,
`Constructor_ZeroFilterOrder_ClampsToOne_StillDemodulatesCorrectly` (also closes a round-2 nit — the
old order=0 InlineData case only asserted "didn't throw," not that it demodulates correctly), and
`Constructor_OutOfRangeFilterOrder_ClampsInsteadOfThrowing`'s InlineData trimmed to just the 2 cases
that actually assert something (-1, 33 — 0 got its own dedicated behavioral test instead).

Round 2 explicitly said the remaining 4 findings (an untested VM Options-Save path for these 5
fields; `Resolve()`'s own order clamp itself untested; a documentation nit about the clamp's shape
vs. its neighbours) do not block and do not need a round 3 — left as-is, not silently forgotten,
just genuinely lower priority than the two real NaN-survives-restart bugs both rounds found and
fixed.

**Item 1 (PLL tuning): DONE. Full solution build clean; full-solution test suite green (`Core.Sstv.Tests`
1450/1451, 1 deliberately skipped; `Application.Tests`/`UI.Tests`/`Host.Tests` all green except the
same pre-existing, unrelated 3 `SstvCompositionRootTests` sample-rate-type failures flagged
throughout this session). Not committed yet — commit only on explicit request, matching this
session's standing convention.**
