# Project brief (resume point)

Scratch file for resuming after `/clear` — not a spec doc, delete or ignore once stale. Pruned
2026-08-08, four times on 2026-08-11, and again 2026-08-12 (was ~291 lines) — the "wire what you
can" Options-dialog pass that filled most of the previous version is DONE and fully captured
elsewhere: each shipped control has its own detailed commit message (`git log --oneline` shows them
in order: `13a8ca7` Open in Log, `4f14396` QRZ lookup, `e132044` Auto-stop/Auto-restart,
`a73b21e` Sense level, `5d7a4cf` Remember-window-position, `33774e6` Audio tab,
`0d90b58`/`3caee2c`/`cf20a61` Radio/CW-ID/Advanced scoping), and the durable per-control state lives
in `spec/16-gui-wiring-survey.md` (wiring inventory) and `spec/14-roadmap.md` (backlog + research).
Nothing lost — see git history for this file if older narrative is ever needed.

## CW-ID / FSK station-ID subsystem — ALL 6 PHASES DONE, committed and pushed-pending (2026-08-12)

All 6 phases complete, each with its own commit and (Phases 2-6) 2-round auditor code review:
Phase 1 `26f674f` (silence representation), Phase 2 (FSK wire format), Phase 3 `18338d8` (RX
continuation decoder), Phase 4 `1185d91` (settings + TX/RX pipeline wiring), Phase 5 `bee6c6d` (RX
consumer/auto-fill), Phase 6 `d6b0d6d` (Options dialog + Transmit-tab UI wiring). Wire protocol
independently confirmed byte-for-byte legacy-faithful across multiple auditor rounds — see git log
for each phase's full commit message if the detailed citation trail is ever needed again.

**Phase 6 close-out (last piece of this feature):**
- UI wiring: Options dialog's Identification tab (ID method radio group, CW text/frequency/speed,
  FSK encode/decode checkboxes, per-section + all-sections reset) and the Transmit pane's read-only
  Identification summary card, both bound to the already-tested settings/encoder layer.
- 2 auditor code-review rounds: round 1 found one real blocker (Reset-All silently skipped the new
  Identification section) plus narrower-than-legacy WPM/tone-frequency UI bounds and a missing
  Transmit-summary test; round 2 independently re-verified every fix against source and closed
  clean (only minor hardening left: explicit-empty-string persistence for a cleared CW text field,
  a doc overclaim, a test blind spot — all fixed same session).
- Full end-to-end test added (`tests/ScanlineStudio.UI.Tests/StationIdEndToEndTests.cs`): REAL
  `AnalogFmSstvEncoder` TX-encodes CW+FSK+NR/RST, REAL `AnalogFmSstvDecoder` decodes it, events
  relayed into a real `RxImagePaneViewModel` — proves operator B's `OverrideCallsign`/`DecodedNrRst`
  auto-fill from a genuine wire round-trip (not fabricated event objects), and that operator A's own
  self-loopback correctly self-filters the callsign while still applying NR/RST (the one real
  asymmetry in that method — no self-filter on the NR/RST branch, intentional, asserted explicitly).
- Real-window (non-headless) verification done: launched the actual app, visually confirmed the
  Transmit tab's Identification card and the Options dialog's Identification tab (ID method
  toggle, CW sub-panel visibility, "DE %m"/1000 Hz/28 WPM defaults) all render and bind correctly.
  Screenshot mishap during this step (see below) — resolved, not still open.
  All 3 affected test suites green: UI.Tests 237/237, Application.Tests 93/93, Core.Sstv.Tests
  742/742. Full solution build clean throughout.

**Real incident this session, resolved, noted for future screenshot work**: `gnome-screenshot -w`
("grab a window") captured the ACTIVE window, not a specific one — after a `pyautogui` click that
(due to a coordinate-space bug, see below) landed on the primary monitor and shifted focus there,
one capture briefly exposed the user's primary-monitor windows (a Firefox/Mastodon session, an
unrelated terminal). Screenshot deleted immediately, user notified, and the approach fixed:
switched to `mss` region-grab bounded to the HDMI-1 rectangle only (`left:3840,top:0,width:2560,
height:1440`) — captures that geometry regardless of window focus, can never pull in the primary
monitor. Root cause of the mis-click: `pyautogui.click()` coordinates read off a *window-relative*
screenshot were used directly as *global* screen coordinates, missing the window's own origin
offset (window origin + scaled-image-coordinate = correct global click point). User's explicit
follow-up instruction: **screenshot ONLY the second/secondary monitor (HDMI-1) region going
forward** — never full-screen, never "active window" mode. No `xdotool`/`ydotool` available in this
sandbox (no passwordless sudo); the working toolchain is a `pip`-installed `pyautogui` + `mss` venv
at `/tmp/.../scratchpad/venv` (ephemeral, session-scoped — recreate if needed in a future session,
don't assume it persists).

**Status: feature complete.** Nothing left on this subsystem's plan
(`/home/artien/.claude/plans/abundant-weaving-lark.md`) unless the user raises new scope (they've
flagged possible interest in richer macro tokens / auto QSO-log fill later, not committed).

## Other deferred items (scoped, not started — full citations in the docs named)

Each of these was investigated this session and needs its own dedicated session (DSP/architecture
rigor, not ordinary wiring) — see `spec/16-gui-wiring-survey.md`'s per-tab sections and
`spec/14-roadmap.md`'s "Options dialogs" bullet for full citations on each:
- **RX BPF** (Decode tab) — Kaiser/Bessel filter math is reusable from `TxOutputBandpassFilter`, but
  needs per-tap-count sync-anchor-correction re-derivation with golden-vector tests.
- **Demod type** (Decode tab) — runtime dispatch between 3 already-ported demodulators; biggest/
  riskiest of the DSP items.
- **RX buffer** (Decode tab) — needs a whole buffered-line-replay subsystem built first; wiring the
  UI control alone today would be a fake no-op.
- **Auto-start** (Decode tab) — needs a design pass to find the right internal "disarmed, still
  live" gating point across multiple sync-detection branches.
- **Advanced tab** — PLL/Zero-crossing tuning is gated on Demod-type above; TX BPF/LPF toggles are a
  small, bounded change but have real over-the-air spectral consequences; Loopback/calibration
  wizards are fully unbuilt.

Resolved, not open items: RTS-on-RX (obsolete, removed feature, `docs/removed-features.md`); VOX/
Sound-file ID (bundled into CW-ID/FSK above); JPEG save quality (bundled with the Gallery's
still-stub "Export frame" button, not a standalone Options control — separate small item, not on
this list).

## Current wiring snapshot

`spec/16-gui-wiring-survey.md` (~286 controls tracked): **~147 REAL, ~94 STUB, ~46 FAKE-LIVE,
~1 PARTIAL**. Densest remaining gaps: Receive tab's Sync&Slant/Input-chain/Signal-quality cards (no
live audio-chain measurement exists for most of these — real new DSP work); the 5 deferred items
above; Transmit tab's Queue/TX-log/Recently-sent cards (100% stub, no such features exist); TX image
editor's canvas-overlay safe-area/callsign/report-plate text (FAKE-LIVE, the most deceptive
placeholder in the app). Remaining PARTIAL: RxFrameMeta's Note `TextBox` (needs a real backing field
on the frame/session model).

## Established process (proven across many prior batches, reuse it)

research → plan → auditor plan-review (2 rounds for anything touching decode-path/concurrency/
schema; skip for pure UI-plumbing with no DSP/concurrency risk) → implement → auditor code-review
(soft cap ~3 rounds — round 1 finds real blockers, round 2 catches an incomplete fix, round 3
usually closes it, loop in
the user rather than a round 4) → verify (build + full relevant test suite, real-window
screenshot/DB-level check if UI-visible) → commit → push. Escalation path if stuck: ask the
auditor; if the auditor also can't resolve it, log to `spec/14-roadmap.md`'s "Verify later with
human" section rather than stalling.

Real-window testing has caught bugs build+tests never would this session (twice, both in the
window-geometry work: a startup deadlock and a cross-thread UI-property crash) — keep testing the
actual running app for anything touching lifecycle/threading, not just unit tests.
