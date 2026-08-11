# Project brief (resume point)

Scratch file for resuming after `/clear` — not a spec doc, delete or ignore once stale. Pruned
2026-08-08 (was 2978 lines/46 entries), 2026-08-11 four times (was ~460, then ~1060, then ~1030,
then ~205 lines) — everything but the active entry below was already in a detailed commit message
or migrated into `spec/14-roadmap.md`/`spec/16-gui-wiring-survey.md`/`spec/09-ui.md`/`CLAUDE.md` —
see git history for this file if older context is ever needed. This pass migrated the last two
non-duplicated "worth remembering" gotchas into `spec/09-ui.md`'s "Visual design direction" section
and 4 small logged-not-fixed cosmetic gaps into `spec/16-gui-wiring-survey.md`'s new closing
section — nothing lost, just relocated to where it'll actually get found again.

## Resume here (2026-08-11, latest, ACTIVE) — wiring real functionality behind GUI controls

**What this is**: the Industry UI redesign is fully closed (every view visually final, see
`spec/09-ui.md`'s "Visual design direction" for the current system, `Styles/Atoms.axaml` for the
atom inventory — don't re-derive either, they're current). Subject is wiring the GUI's controls to
already-real backend functionality, per `spec/14-roadmap.md`'s "Must-implement backlog" and
`spec/16-gui-wiring-survey.md`'s per-control inventory.

**This session shipped 2 commits, both pushed** (`ca9b269`, `88fa1f5`) — 7 controls wired total:
TxControls Output-device row, RadioHeaderView "Store current" (new command, not a reuse — see that
commit's message), Options Decode tab's Auto-Sync/Auto-Slant checkboxes, Gallery's "Log entry"
status row, and a new Gallery Note/Flagged feature (backed by `IReceiveHistoryStore.SetNoteAsync`/
`SetFlaggedAsync` — real, SQLite-backed methods that had zero call sites anywhere in the UI before
this). Full details, including the Gallery feature's 3-round auditor saga (a live-refresh-clobbers-
your-typing bug and its full fix history) are in `88fa1f5`'s own commit message — don't re-derive,
`git show 88fa1f5` has it. One logged-not-fixed risk from that feature: a refresh mid-typing may
steal keyboard focus from the Note box (UX-only, no data loss, in
`spec/16-gui-wiring-survey.md`'s Note row).

**Still open from that same investigation**: `IReceiveHistoryStore.SetLinkedQsoIdAsync` ("Open in
Log") remains unwired — needs a QSO picker/auto-create UI decision, bigger scope than the other two
methods. Good next candidate if continuing this same vein.

**Current wiring totals** (`spec/16-gui-wiring-survey.md`, ~281 controls tracked): **~130 REAL,
~103 STUB, ~47 FAKE-LIVE, ~2 PARTIAL**. Densest remaining gaps: Receive tab's Sync&Slant/Input-
chain/Signal-quality cards (SNR/squelch/notch/noise-floor — no live audio-chain measurement exists
in `Core.Audio`/`Core.Sstv` for most of these, real new DSP work not just wiring); Options window's
Decode's remaining 7 controls plus Identification/Advanced tabs (~53 controls, still stub);
Transmit tab's Queue/TX-log/Recently-sent cards (100% stub, no such features exist); TX image
editor's canvas-overlay safe-area/callsign/report-plate text (FAKE-LIVE — reads as real burned-in
TX content, arguably the most deceptive placeholder in the app). Remaining PARTIAL: RxFrameMeta
Note/Override-callsign `TextBox`es (needs a real backing field on the frame/session model — checked
directly this session, genuinely harder than the Gallery-side Note fix, not just a stale note).

**`spec/14-roadmap.md`'s "Must-implement backlog" is the prioritized list to work from**, not the
survey directly — the survey tells you WHAT is stub/fake, the backlog tells you what order to
tackle it in and why. Status:
- [x] Logbook UI pane, DSP decode-accuracy residuals, waterfall color/palette, RX history browser
  affordances — all shipped and closed.
- [ ] **Options dialogs (real functionality behind the ~50+ disabled controls)** — **BLOCKED on
  user scoping input, ask again before touching.** The Options window is a real, Industry-styled
  7-tab dialog, but its actual functional wiring is unchanged — still `IsEnabled="False"` +
  `Options.NotImplemented.Help` placeholders underneath. Several of its stub sections overlap with
  the CW-ID/FSK and other items below — a real per-section split plus an auditor UI-design
  plan-review pass is needed before building anything (not a straight port). **User was asked which
  pieces to prioritize once already and never answered — re-ask, don't assume.**
- [ ] OCR/QRZ lookup — large, genuinely new feature (no OCR anywhere; QRZ needs new API-key
  config, legacy's hardcoded personal password isn't being resurrected).
- [ ] CW-ID / FSK station-ID subsystem — real legacy feature (`sstv.cpp:2465-2551`'s STX `0x2a`),
  zero replacement built. User-deferred once already; confirm priority before starting.
- [ ] VOX, RTS-on-RX, Sound-file ID (blocked on CW-ID landing first), JPEG save quality (blocked
  on a JPEG save path existing at all, images are PNG-only today) — smaller, lower-priority items,
  see the roadmap doc's own backlog section for the one-line reason each is still open.

**Established process for this kind of work** (proven across many prior batches, reuse it):
research → plan → auditor plan-review (2 rounds for anything touching decode-path/concurrency/
schema; skip for pure UI-plumbing with no DSP/concurrency risk) → implement → auditor code-review
(soft cap ~3 rounds, seen this session: round 1 finds real blockers, round 2 catches an incomplete
fix, round 3 usually closes it — loop in the user rather than a round 4) → verify (build + full
relevant test suite, real-window screenshot/DB-level check if UI-visible) → commit → push.
Escalation path if stuck: ask the auditor; if the auditor also can't resolve it, log to
`spec/14-roadmap.md`'s "Verify later with human" section rather than stalling.

**Next action on resume**: ask the user which Must-implement backlog item to start with — Options
dialogs needs their scoping answer specifically; `SetLinkedQsoIdAsync`/OCR-QRZ/CW-ID/FSK are the
next-biggest unblocked items.
