# Project brief (resume point)

Scratch file for resuming after `/clear` — not a spec doc, delete or ignore once stale. Pruned
2026-08-08, four times on 2026-08-11, and again 2026-08-12 twice (once after CW-ID/FSK closed, again
after demod-type + RX BPF closed) — completed subsystems are fully captured elsewhere: each shipped
control/phase has its own detailed commit message (`git log --oneline`), and durable per-control
state lives in `spec/16-gui-wiring-survey.md` (wiring inventory) and `spec/14-roadmap.md` (backlog +
research). Nothing lost — see git history for this file if older narrative is ever needed.

## CW-ID / FSK station-ID subsystem — DONE, committed (2026-08-12)

All 6 phases complete and auditor-reviewed. Wire protocol independently confirmed byte-for-byte
legacy-faithful. Real-window verified. Nothing left on this subsystem's plan. See git log
(`26f674f`/`18338d8`/`1185d91`/`bee6c6d`/`d6b0d6d` and Phase 2/6's own commits) for the full citation
trail if ever needed again.

## Demod-type runtime-dispatch subsystem — DONE, committed (2026-08-12)

All 4 phases complete and auditor-reviewed (2 plan-review rounds before implementation, per-phase
code review after): Phase 1 `ec7a989` (PLL/Zero-crossing narrow-mode support), Phase 2 `9d9fccb`
(`AnalogFmSstvDecoder` runtime dispatch between the 3 already-ported demodulator classes), Phase 3
`e6c83a9` (settings-layer + `RestartableSstvDecoder` threading), Phase 4 `a689930` (Options dialog UI
wiring). Real-window verified (toggled to PLL, saved, reopened, confirmed persistence). Full
`ScanlineStudio.Core.Sstv.Tests` suite green throughout.

## RX BPF (bandpass-filter preset) subsystem — DONE, committed (2026-08-12)

Started as a "just wire the UI" backlog item; direct legacy-source verification (per CLAUDE.md's
no-assumptions rule) found it needed net-new DSP math first — Narrow/VeryNarrow presets require
attenuation ≥21dB, which routes legacy's shared filter-designer through its Kaiser-Bessel-window
branch (`fir.cpp`'s `MakeFilter`/`I0`), previously unported and provably unreachable at this port's
prior Wide-only default. User authorized full scope (2 rounds of plan-review before implementation,
matching demod-type's own rigor).

All 4 phases complete, each auditor code-reviewed clean (no blockers in any round):
- Phase 1 `c8134fb`: `RxBpfPreset` enum (`ScanlineStudio.Abstractions.Sstv`) + Kaiser-window
  `MakeFilter` port + preset-parameterized `SearchBandpassFilter` (Wide/Narrow/VeryNarrow, plus
  `syncRestartEnabled`-driven H1 `fcl`, a live bug fix in the prior Wide-only port that had hardcoded
  1100Hz regardless of sync-restart state). Independently-computed (Python) coefficient fixtures for
  both Kaiser alpha-threshold arms.
- Phase 2 `ba72714` (the crux): `AnalogFmSstvDecoder` wiring — `_searchBandpassFilter` nullable, null
  for `RxBpfPreset.Off` (a true bypass matching legacy's `if(m_bpf)` gate, not a discard-output
  filter). Round-1 plan-review blocker fixed: the Off-bypass path keeps the existing buffer-trim fill
  loop intact via a null-coalesce, rather than an early return that would have silently pinned the
  trim cursor and caused unbounded memory growth — a dedicated regression test
  (`BufferedSampleCount_StaysBounded_ForLongNeverLockingStream_WithRxBpfOff`) proves the fix, traced
  to confirm it would have caught the originally-planned buggy shape. Golden-vector regression
  fixtures (6 real-audio decodes, framed explicitly as regression checks, not legacy-parity claims,
  since no legacy-RX reference exists at non-Wide presets).
- Phase 3 `a223116`: settings-layer + `RestartableSstvDecoder` threading, a faithful mechanical
  mirror of `DemodType`'s own already-proven shape (absent/out-of-range both clamp to Wide,
  `InnerRxBpfPresetForTests` accessor so a dropped `CreateInner()` argument can't silently revert a
  user after the ~12h periodic rebuild).
- Phase 4 `16df5fe`: Options dialog UI wiring. Un-stubbed the pre-existing disabled placeholder radio
  group (fixed a real bug in the stub itself: hardcoded `IsChecked="True"` on item 0/"Normal", which
  is a true bypass — real default is Wide). Added the missing help-glyph this row lacked vs. its
  `SenseLevel`/`DemodType` siblings. Real-window verified: Wide checked by default (not the old
  stub's Normal), toggled to Sharp/Narrow, saved, reopened, confirmed persistence.

**Status: feature complete.** Full `ScanlineStudio.Core.Sstv.Tests` suite green (806/806) after
Phase 3; `ScanlineStudio.UI.Tests` (242/242) and `ScanlineStudio.Application.Tests` (93/93) green
after Phase 4. Nothing left on this subsystem's plan
(`/home/artien/.claude/plans/stateless-hopping-rain.md`).

**Not yet pushed** — all of the above (CW-ID/FSK, demod-type, RX BPF) committed locally to `master`,
push not yet requested/confirmed this session.

## Other deferred items (scoped, not started — full citations in the docs named)

- **RX buffer** (Decode tab) — needs a whole buffered-line-replay subsystem built first; wiring the
  UI control alone today would be a fake no-op.
- **Auto-start** (Decode tab) — needs a design pass to find the right internal "disarmed, still
  live" gating point across multiple sync-detection branches.
- **Advanced tab** — PLL/Zero-crossing tuning controls now have a real backend (demod-type subsystem)
  but the tab's own tuning-parameter UI isn't wired yet; TX BPF/LPF toggles are a small, bounded
  change but have real over-the-air spectral consequences; Loopback/calibration wizards are fully
  unbuilt.

Resolved, not open items: RTS-on-RX (obsolete, removed feature, `docs/removed-features.md`); VOX/
Sound-file ID (bundled into CW-ID/FSK, done); JPEG save quality (bundled with the Gallery's
still-stub "Export frame" button, not a standalone Options control — separate small item, not on
this list); RX BPF and Demod type (both done, see above).

## Current wiring snapshot

`spec/16-gui-wiring-survey.md`: **~149 REAL, ~101 STUB** (plus FAKE-LIVE/PARTIAL, see that doc's own
summary table for exact current counts — this file doesn't duplicate it live). Densest remaining
gaps: Receive tab's Sync&Slant/Input-chain/Signal-quality cards (no live audio-chain measurement
exists for most of these — real new DSP work); the deferred items above; Transmit tab's
Queue/TX-log/Recently-sent cards (100% stub, no such features exist); TX image editor's
canvas-overlay safe-area/callsign/report-plate text (FAKE-LIVE, the most deceptive placeholder in
the app).

## Established process (proven across many prior batches, reuse it)

research → plan → auditor plan-review (2 rounds for anything touching decode-path/concurrency/
schema; skip for pure UI-plumbing with no DSP/concurrency risk) → implement → auditor code-review
(soft cap ~3 rounds — round 1 finds real blockers, round 2 catches an incomplete fix, round 3
usually closes it, loop in the user rather than a round 4) → verify (build + full relevant test
suite, real-window screenshot/DB-level check if UI-visible) → commit → push. Escalation path if
stuck: ask the auditor; if the auditor also can't resolve it, log to `spec/14-roadmap.md`'s "Verify
later with human" section rather than stalling.

Real-window testing keeps catching things build+tests never would — this session (RX BPF Phase 4)
it caught that the app's own menu bar doesn't render until the window is focused/clicked once after
launch (a pre-existing WM/rendering quirk, not a code bug, but real-window screenshots taken
immediately after `dotnet run` will miss it). Screenshot toolchain: no `xdotool`/`ydotool` in this
sandbox; a `pip`-installed `pyautogui` + `mss` + `pillow` venv in the scratchpad directory works
(ephemeral, session-scoped — recreate if needed in a future session). Screenshot ONLY the
secondary/HDMI-1 monitor region (`left:3840,top:0,width:2560,height:1440`), never full-screen, never
"active window" mode — a real incident during CW-ID/FSK work briefly exposed the user's primary-
monitor windows via "active window" capture; that mode must never be used again.
