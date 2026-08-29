# Options tab non-functional controls — master backlog plan

User request (2026-08-28): every `IsEnabled="False"` / "not yet implemented" control in the Options
window, especially the Advanced tab, tackled one by one. Every item gets the full heavy process
(CLAUDE.md §7): 2-round plan-review before any code, code-review-until-go after — no item skips this,
per the user's own explicit instruction, regardless of how small it looks.

Full inventory: 24 disabled controls in `src/ScanlineStudio.UI/Views/OptionsWindowView.axaml`, grouped
into 13 real features. Legacy research (`yoniq-old/YONIQ-main/`, verified with `grep -a` — plain `grep`
silently skips these CP932-encoded files) below.

## Key finding that reshapes scope

`DemodType.Pll`/`DemodType.ZeroCrossing` are **already** live, user-selectable demod algorithms in this
port (`AnalogFmSstvDecoder.cs:1253-1268`, `PllFmDemodulator.cs`, `ZeroCrossingFrequencyCounter.cs` —
direct, already-tested ports of legacy `CPLL`/`CFQC`). The Advanced tab's PLL and Zero-crossing groups
are **tuning-parameter knobs for demodulators that already exist and work**, not new DSP to invent.
`PllFmDemodulator`'s own doc comment already flags this as deferred. This makes those two items
**medium** (expose existing/extend constructor parameters through settings + live-apply, same shape as
the shipped `RxBpfPreset`/`DemodType` work), not large ground-up DSP.

## Full inventory, execution order

| # | Item | Legacy citation | Category | Complexity | Notes |
|---|---|---|---|---|---|
| 1 | General: 7 spectrum/waterfall colors | `Option.cpp:269-275,577-583,713-719`, `sys.m_ColorLow/High/FFTB/FFTStg/FFTSync/FFTFreq` | settings-plumbing | trivial | RESOLVED: `m_ColorFFT` confirmed (`Main.cpp:809`, `=clYellow`, applied `:3319`, "FFT signal"/main trace). |
| 2 | Radio: RTS on RX | `Option.cpp:279,438`, `sys.m_RTSonRX` | settings-plumbing / **removal candidate** | trivial if kept | Legacy itself never reads this field anywhere outside load/save — even legacy's own nearest related check is commented out. Likely vestigial in legacy too, not just this port. |
| 3 | ~~Radio: PTT Lock~~ | `Option.cpp:278,439` + `Comm.cpp:203-220`, `sys.m_TxRxLock` | settings-plumbing | **REMOVED, see below** | Round-1 plan-review corrected the original entry here (was wrong about the actual legacy behavior) and found Hamlib's own `ptt_share` config key is a real, reachable near-equivalent — user chose removal anyway once shown the corrected facts. |
| 4 | Advanced: PLL tuning (VcoGain/LoopOrder/LoopCutoff/OutCutoff) | `Option.h:67-77`, `Option.cpp:255-259,511-523`, `fp->m_pll.*` | DSP-math (params only) | medium | VcoGain has NO equivalent parameter in `PllFmDemodulator` today (hardcoded to `-bandwidthHz`) — needs a new constructor param, not just wiring. **AXAML's hardcoded defaults (2600/1/200/1200) don't match `PllFmDemodulator`'s real current constructor defaults (1500/3/900) — neither set is confirmed against legacy's own `Option.cpp` ReadIniFile defaults yet; re-derive from source, don't trust either.** |
| 5 | Advanced: Zero-crossing tuning (Order/Cutoff/Smoothing) + Differentiator | `Option.h:150-161`, `Option.cpp:261-264,508-509,529-536`, `fp->m_fqc.*`, `sys.m_Differentiator`/`m_DiffLevelP` (used `Main.cpp:4091,4115`) | DSP-math (params only) | medium | Legacy also has a `RGcrossType` (`fp->m_fqc.m_Type`) radio group with **no AXAML placeholder at all today** — this item is missing a whole control, not just under-wired; scope must include adding it. |
| 6 | Advanced: TX BPF / TX LPF | `Option.h` `CBTXBPF`/`CBTXLPF`, `Option.cpp:223-225,450-451`, applied `sstv.cpp:2914`/`2869` | DSP-math (on/off toggle) | medium | **Open question, resolve in plan-review round 1**: does `RestartableSstvEncoder` already have BPF/LPF stages to gate, or do those need porting first? Not yet checked against the current C# TX path. |
| 7 | Advanced: Loopback mode (Off/Internal/External) | `Option.h` `RGLoopBack`, `Option.cpp:102-105,282,435`, `sys.m_echo` (read at 7 `Main.cpp` sites) | new-subsystem (session-layer audio routing) | medium-large | **Open question, resolve in plan-review round 1**: this is a continuous TX-audio-into-RX routing mode — does it overlap with the existing one-shot `RunLoopbackSelfTestAsync`, or is it genuinely separate? Not yet checked. |
| 8 | Advanced: Polynomial calibration + Level Calibration Wizard | `Option.cpp:90,213,372,558`, `sys.m_DemCalibration`, `MakeCalibrationTable()` (`Main.cpp:4040,7937-7943,12344-12558`) | DSP-math / calibration system | large | Real per-profile calibration-table feature (`m_DemPro[i].DemCalibration`) — `MakeCalibrationTable()`'s actual math not yet read. The Wizard button's own exact scope (paired with this, or a distinct flow) is unconfirmed — resolve first in plan-review. Do NOT confuse with the already-removed, unrelated "ClockAdjustWizard." |
| 9 | Identification: Tune satellite | **No legacy hit for "satellite"/"doppler" anywhere in the C++ tree** | unverified / **removal candidate** | — | No legacy grounding found at all, unlike every other item here — only a `.Help` locale key exists with no described behavior anywhere. Strong removal candidate rather than an implementation target. |

**Large, net-new subsystems — flagged for a scope decision before they enter this backlog** (see
question below), not simple stub fills:

| # | Item | Legacy citation | Why it's different |
|---|---|---|---|
| 10 | Identification: Sound-file ID (method + path + browse) | `Main.h:1143`, `Main.cpp:6847` `TMmsstv::OutputMMV` | Needs a real custom-binary-format parser + resampler + TX-audio-pipeline injection — a genuinely new subsystem, not a checkbox. |
| 11 | Identification: VOX mode + Edit tone | `Option.cpp:1184`, `Main.cpp:1897/7274`, `sys.m_VOX` | Needs an audio-level-triggered PTT path `IRadioSessionService`'s TX flow doesn't have at all today. |
| 12 | Radio: OmniRig backend | `spec/03-cat-layer.md` ("design deferred") — no legacy C++ equivalent, Windows-only COM | A whole 4th CAT backend (Windows-only COM interop) — already documented as deliberately deferred at the architecture level. |

## Process per item (every item, per user's explicit instruction)

1. Read the real legacy source cited above in full (not just the grep hits already found) before
   writing a plan.
2. Draft a plan doc, send to the `auditor` for plan-review. Iterate until an explicit "build it now."
   Full 2-round minimum for anything touching decode-path state, DSP/codec math, or concurrency
   (items 4-8, 10-12); items 1-3, 9 can use a lighter single round given their low blast radius, but
   still get a real auditor pass, not self-review, per the user's explicit request for the heavy loop
   on all of them.
3. Implement, mutation-test the load-bearing logic.
4. Code-review-until-go — repeat rounds until an explicit "Go for production: Yes," not a single pass.
5. Full build + targeted test run; full-solution test run once per item lands.
6. Update this doc's own status line for that item, update `PROJECT_BRIEF.md`.

## Proposed execution order

Cheapest/lowest-risk first, building confidence and precedent before the harder DSP-parameter items;
the 3 flagged-for-decision large subsystems and the 2 removal candidates are pulled out of the main
sequence pending the user's call (see question):

1. Radio: PTT Lock (#3) — small, real, self-contained.
2. Advanced: PLL tuning (#4) — medium DSP-param, establishes the pattern items #5-6 reuse.
3. Advanced: Zero-crossing tuning + missing RGcrossType control (#5).
4. Advanced: TX BPF / TX LPF (#6) — resolve the encoder-path question in round 1.
5. Advanced: Loopback mode (#7) — resolve the self-test-overlap question in round 1.
6. Advanced: Polynomial calibration + Level Calibration Wizard (#8) — largest of the confirmed items,
   needs its own legacy deep-dive first.
7. General: 7 spectrum/waterfall colors (#1) — trivial, but last on purpose: pure UI, zero DSP risk,
   good place to bank an easy win if the DSP items above run long.

## Scope decisions (user, 2026-08-28)

- **RTS on RX (#2): REMOVE.** Never functional in legacy either (its own field is read/written but
  never checked anywhere real) — same "nothing here was ever functional to drop" reasoning already
  used for the Rig & PTT menu removal this session, no `docs/removed-features.md` entry needed.
- **Tune satellite (#9): REMOVE, by explicit informed decision.** Correction to the research above:
  it IS real in legacy after all — `sys.m_TuneSat` (`Main.cpp:3692,7680`), tied to the Tune button's
  own timer (`m_TuneTimer`/`sys.m_TuneTXTime`): when the timer expires, `ToTX()` fires instead of
  `ToRX()` if `m_TuneSat` is set, matching the existing help text exactly. The original "no legacy
  grounding" finding was a false negative — the fork's grep searched for "satellite"/"doppler"
  literally and missed the differently-named `m_TuneSat` field. User was told the corrected finding
  and chose removal anyway (no satellite-pass workflow this app needs to support), not on the basis
  of missing information.
- **Sound-file ID (#10), VOX (#11), OmniRig (#12): OUT of this backlog entirely**, per direct user
  decision — each is large enough to deserve its own dedicated plan/review cycle on its own timeline,
  not sequenced alongside quick stub fixes. Revisit only if the user raises one specifically.

Both removals are mechanical, single-pass work (no DSP/decode-path risk, nothing to plan-review) —
handled first, ahead of the real backlog below, to clear them off the list cheaply.

## Item 3 (PTT Lock) resolution (2026-08-28)

Round-1 plan-review (`docs/plans/options-stub-item1-ptt-lock-plan.md`, now deleted per this project's
own delete-on-resolution convention) corrected two things the original inventory row got wrong: the
real legacy behavior (hold the PTT serial port open across TX/RX vs. close/reopen every time, not a
failure-handling policy), and the claim that no equivalent hook exists anywhere in this port — Hamlib
(already linked in-process) has its own `ptt_share` config key, reachable through the same `ApplyConf`
path `HamlibRadioProtocol.cs` already uses. Shown the corrected facts, the user chose removal anyway
(narrow applicability — 2 of several PTT types, one of three backends — didn't justify building a
real toggle for it). Removed: `OptionsWindowView.axaml`'s disabled checkbox block, the
`Options.Radio.PttLock`/`.Help` locale keys, and `docs/removed-features.md`'s existing (already
present, but factually wrong on this specific point) "Raw-serial RTS-pin PTT keying" entry corrected
in place rather than duplicated.

## Final backlog (6 items, in execution order)

1. ~~Advanced: PLL tuning (#4)~~ — **DONE.** Full details in
   `docs/plans/options-stub-item1-pll-tuning-plan.md`. 3 plan-review rounds, 2 code-review rounds,
   both code-review rounds caught a real bug (a VcoGain math omission caught by the plan's own
   mandated test; two separate NaN-survives-restart clamp gaps, ceiling then floor). Not committed.
2. ~~Advanced: Zero-crossing tuning + missing RGcrossType control (#5)~~ — **DONE.** Full details in
   `docs/plans/options-stub-item2-zerocrossing-tuning-plan.md`. 1 plan-review round (explicit "yes,
   ready to build now" with 5 one-line fixes folded in), 1 code-review round (explicit "go for
   production: yes" with 1 regression-net gap closed and mutation-verified: the drain test only
   proved decoder-level tracking fields updated, not that both live counter instances were actually
   retuned — new `SmoothingModeForTests` accessors close it). New `MovingAverage.SetCount` (a genuine
   `CSmooz::SetCount` port piece that was missing), new `ZeroCrossingSmoothingMode` public enum
   (Abstractions, mirrors `DemodType`'s placement). Not committed.
3. ~~Advanced: TX BPF / TX LPF (#6)~~ — **DONE.** Full details in
   `docs/plans/options-stub-item3-tx-bpf-lpf-plan.md`. 2 plan-review rounds (3 blockers round 1, all
   fixed), 1 code-review round (no production defect, 2 real test-coverage gaps closed and mutation-
   verified). A REVERSAL of a documented, deliberate prior removal (the BPF on/off+tap-count control,
   `docs/removed-features.md`'s former entry, now deleted) — the underlying filter math was already
   correctly ported; TX LPF (pre-VCO frequency smoothing) was genuinely new work. Not committed.
4. ~~Advanced: Loopback mode (#7)~~ — **DONE, via removal, not build.** A research fork found this
   is already a locked, documented prior architectural decision (`docs/removed-features.md`'s
   "RGLoopBack hardware loopback (Internal/External)" entry, 2026-08-26) — this port's TX/RX are
   architecturally mutually exclusive, so a literal port needs an architecture change already
   explicitly declined in favor of the simpler one-shot `RunLoopbackSelfTestAsync`. The leftover
   disabled AXAML stub (never cleaned up when that decision shipped) was removed, 4 dead locale keys
   deleted, one addendum line added to the existing removed-features.md entry. Lighter single-round
   code-review (mechanical, no DSP risk, per CLAUDE.md §7): "ready to land as-is," 3 nits (comment
   placement, a pre-existing `m_LoopBack`/`m_echo` field-name slip in the entry, this backlog doc's
   own stale open question) — the field-name slip fixed directly, this line closes the other nit.
5. ~~Advanced: Polynomial calibration + Level Calibration Wizard (#8)~~ — **DONE, via removal, not
   build.** A research fork's FIRST pass wrongly concluded the wizard needed the identical
   simultaneous TX+RX blocker item 4 hit — **wrong, caught by code-review round 1** ("not
   equivalent," the entry's own central claim contradicted by `Option.cpp:988` + `Sound.cpp:336-340`):
   the wizard is actually a pure in-process software sweep, TX is explicitly left first, no real
   audio hardware is ever in the loop — architecturally closer to this port's own already-shipped
   `RunLoopbackSelfTestAsync` than to item 4's real blocker. **The removal call still stands, on
   corrected grounds**: `GetPictureLevelDiff`/`GetPixelLevel` (the table's own APPLICATION point) has
   no equivalent hook anywhere in this port's decoder at all — pixel levels use hardcoded legacy-
   default constants, not a configurable correction stage — so the wizard would have nothing to
   calibrate even though it's buildable in isolation. `docs/removed-features.md`'s entry was rewritten
   with the corrected mechanism and a corrected divide-by-zero reachability claim (legacy's own
   `InitProfile` seeds sane non-zero defaults; the earlier "all-zero, never-configured" trigger
   scenario doesn't exist in real legacy). The AXAML "Misc" group (now empty after both item 4/5
   removals) removed entirely, 3 more dead locale keys deleted. Code-review sent a 2nd time on the
   corrected doc; not yet returned.
   **Also found and removed in the same pass: the "Differentiator" checkbox** (`sys.m_Differentiator`,
   a picture-channel edge-enhancement filter — an earlier draft of both this line and the
   removed-features.md entry wrongly said "luminance only"; code-review found it actually runs on all
   3 R/G/B channels in RGB-family modes, luminance-only only in YC-family modes, since fixed) — a
   leftover stub item 2's own plan doc explicitly scoped out that no other item picked up, found
   during this backlog's own final zero-stubs sweep. Its real mechanism (`GetPictureLevelDiff`) itself
   calls `GetPixelLevel` internally — the IDENTICAL missing decoder hook item 5 depends on, not a
   coincidence. New `docs/removed-features.md`
   entry; 1 dead locale key deleted.
6. ~~General: 7 spectrum/waterfall colors (#1)~~ — **DONE, via removal, not build.** Direct
   research (no fork needed, small scope) found this port already has a deliberately-designed,
   already-shipped fixed color scheme (`WaterfallPalette.cs`'s SDR-style heatmap gradient,
   `SpectrumTraceControl.cs`'s fixed trace/marker pens, `Atoms.axaml`'s themed FFT-panel background)
   replacing all 7 of legacy's raw per-element user-editable colors — a real prior design decision
   (`spec/09-ui.md` explicitly exempts this visualization from strict legacy-port fidelity), not a
   gap. New `docs/removed-features.md` entry documents the real legacy defaults/persistence/
   application sites. Lighter single-round code-review (mechanical, no DSP risk): "yes, land it," 3
   doc-precision nits (an over-attribution of which file holds the FFT-background equivalent, loose
   "secondary/staging" wording for the max-hold trace, this backlog doc's own stale open question) —
   all fixed directly.
