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

**`SetLinkedQsoIdAsync` ("Open in Log") now wired, closed** — commit `13a8ca7`. User picked the
existing-QSO-picker-dialog option (not simple auto-create) when asked. New `QsoLinkWindowViewModel`/
`QsoLinkWindowView`: search-and-link an already-logged QSO, or a mini create-form to log one on the
spot, both writing `ReceiveHistoryEntry.LinkedQsoId` AND the `QsoRecord.ReceivedImageId` reverse FK
(the interface's own doc comment named that FK as "already designed for this exact link" but nothing
set it before this). Two auditor rounds each caught real bugs: plan-review caught a write-ordering
inversion (link the entry side first, so a missing entry never partially writes the QSO side) and a
double-log/double-QRZ-push risk on the create path; code-review caught a real UI-thread-affinity bug
(an over-applied `ConfigureAwait(false)` could let `Window.Close()`/bound-collection mutations run
off the UI thread — subtle, since Microsoft.Data.Sqlite completes synchronously today so it never
actually manifested in manual testing) plus a misleading error message on one exception path. Full
detail in `13a8ca7`'s own commit message. Verified end-to-end in a real running instance (not just
tests): seeded a DB row directly (no click-automation tool like `xdotool` was available in this
sandbox — used raw `python-xlib` `warp_pointer`/`fake_input` instead, works fine, screen-coordinate
math needs `_NET_FRAME_EXTENTS`' top value subtracted from window-relative Y before adding the
window's absolute Y, easy to get backwards), drove the dialog, confirmed the Gallery badge flips to
"Logged" live and both DB columns land correctly.

**Deliberately NOT built this pass** (acknowledged in the plan, not bugs): no relink/unlink UI (
relinking a frame overwrites the old QSO's stale reverse FK silently); no `CancellationToken`
threaded into the dialog's service calls; no busy-spinner beyond buttons graying out. Fine for a
"log this frame quickly" dialog; revisit only if a user actually hits one of these.

**QRZ.com callsign lookup now wired, closed** — same session. The old "OCR/QRZ lookup" backlog
line was WRONG (called it "genuinely new, no legacy precedent") — verified directly against
`yoniq-old/YONIQ-main`: OCR really has zero legacy precedent (only false-positive `#define OCRH`
include guards), but QRZ lookup is a real, fully-traced legacy feature (`qrzcom.cpp` + `Main.cpp`'s
login flow) that had just never been ported. Split into two roadmap items
(`spec/14-roadmap.md`): OCR moved to "Explicitly deferred beyond v1" (user: "eh, maybe one day"),
QRZ lookup built standalone (skipping the spec's original decorator-over-offline-Clublog-prefix-
lookup design entirely, per user instruction — that piece stays unbuilt/blocked, separate concern).
New `IQrzCallsignLookup`/`QrzCallsignLookup` (real `XDocument`/`XNamespace` XML parsing against
QRZ's actual namespace, not legacy's naive substring search), user-supplied username/password
(never legacy's hardcoded personal creds), a new Options "QRZ.com" tab with a Test button, and
Receive-tab wiring (Override callsign + Lookup QRZ button + new Name/QTH rows + real Grid).
Two auditor rounds: plan-review caught 4 real design issues (session-cache keyed on username alone
would've let a changed password silently pass Test via a still-valid stale session; a lookup-based
Test has a false-negative mode when the test callsign has no QRZ profile, fixed with a dedicated
login-only `TestCredentialsAsync`; unspecified lock scope; missing URL-escaping); code-review caught
a real bug (a pathological double-session-expiry returned `ErrorReason: null`, rendering a visible-
but-empty red error box) plus nits (placeholder-text consistency, since fixed). **Real-window
testing against the LIVE QRZ server caught one more real bug code review couldn't**: `IHttpClientFactory`'s
own built-in logging handler was logging the full request URI — including the plaintext password —
at Information level, regardless of what this app's own logging code did. Fixed with
`.RemoveAllLoggers()` on that specific named `HttpClient` registration (`Program.cs`); confirmed
via before/after log diffing against two real calls to `xmldata.qrz.com` (which also produced two
different real QRZ error strings — "Username/password incorrect" then a rate-limit variant — both
rendered correctly, proving the error-parsing path against the real server, not just synthetic
fixtures). 359 tests passing across the 3 affected test projects (208 UI + 73 Application + 78
Core.Logbook, including 10+5 new QRZ-specific tests).

**Current wiring totals** (`spec/16-gui-wiring-survey.md`, ~281 controls tracked, not yet re-audited
post-`13a8ca7`): was **~130 REAL, ~103 STUB, ~47 FAKE-LIVE, ~2 PARTIAL** before this session's fix;
"Open in Log" flips from STUB to REAL, survey doc itself not yet updated to reflect it. Densest
remaining gaps: Receive tab's Sync&Slant/Input-
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
- [x] **QRZ.com callsign lookup — done, see above.** OCR split out separately, deferred (user:
  "eh, maybe one day"), not on this list anymore — see `spec/14-roadmap.md`'s "Explicitly deferred
  beyond v1" section.
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
dialogs needs their scoping answer specifically; CW-ID/FSK is the next-biggest unblocked item
(`SetLinkedQsoIdAsync` and QRZ.com lookup are both done, see above). Small housekeeping item if
picking this back up: `spec/16-gui-wiring-survey.md`'s "Open in Log" row/wiring-totals table (stale
since `13a8ca7`) and its Frame-metadata "Grid/dist·QRZ"/"Lookup QRZ" rows (stale since the QRZ
lookup commit) haven't been updated to reflect either yet.
