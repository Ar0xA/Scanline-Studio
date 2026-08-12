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

**Auto-stop/Auto-restart wired + Tx-tab stale checkbox removed, closed** — same session, right after
QRZ lookup. Checked `Options.Decode.AutoStop`/`AutoRestart`'s loc text against what
`SstvDecoderSettings.AutoStopEnabled`/`SyncRestartEnabled`'s own doc comments actually say the
fields gate (legacy line-cited): both labels were factually wrong ("Auto-stop when sync looks
stable" and "Auto-restart sync on loss" — backwards/vague vs. the real behavior). Fixed the wording
first (now "Auto-stop on erratic/weak signal" / "Restart onto a stronger sync mid-reception"), then
wired both checkboxes through the same `OptionsSnapshot`/`OptionsSettingsService` pattern as
Auto-Sync/Auto-Slant. Also removed the TX tab's `Options.Tx.QrzLookup` checkbox — a stale duplicate
placeholder that predated the real QRZ.com tab (commit `4f14396`) and was now just confusing/
redundant next to it; removed outright (loc keys deleted too), not left stub. 4 existing
`OptionsWindowViewModelTests` extended (2 renamed to drop the now-inaccurate "AutoSync/AutoSlant"
naming), 208/208 UI tests still passing. `spec/16-gui-wiring-survey.md` fully updated for this
entry, QRZ lookup, and Open in Log — no more stale rows/counts anywhere in it as of this commit.

**Options Decode "Sense level" (squelch) wired, closed** — same session, per user's "wire what you
can... then start on the items that need functionality" instruction. Investigated all 4 remaining
Decode-tab controls (Sense level, RX BPF, Demod type, RX buffer) directly against `Option.dfm`/
`sstv.cpp` before picking one: RX BPF and Demod type ARE real legacy Options controls but each needs
its own larger DSP-scoping pass (RX BPF = a `CalcBPF` FIR-filter-preset port; Demod type = enabling
runtime dispatch between 3 already-ported-but-not-switchable demodulator classes); RX buffer has no
identified legacy Options-dialog precedent at all. User picked "small pieces first," so this batch
covers Sense level only — a real port of legacy's `CSSTVDEM::SetSenseLvl` (`sstv.cpp:1793-1817`)
4-preset absolute-amplitude sync-threshold table, previously hardcoded to preset 1 (the shipped
default) only. Auditor plan-review round caught: an out-of-range persisted value (e.g. a hand-edited
`7`) must fall back to preset 0 (legacy's own `SetSenseLvl` switch `default:`), deliberately
DIFFERENT from an absent-key value's fallback (preset 1, legacy's ctor default) — both wired and
tested; two `RestartableSstvDecoder` constructors both needed the new parameter (only one assigns
fields, easy to miss); field-assignment ordering inside `AnalogFmSstvDecoder`'s ctor mattered (must
happen before `VisLockStateMachine` construction reads it). Calibration-verified for all 4 presets
against a real synthesized full-amplitude tone, including preset 3 ("Very high")'s tightest
margin — clears with real headroom, confirming this port's AGC scale is faithful rather than just
transcribed. A dedicated wiring test (constructor argument reaches the decoder's internal threshold
fields) was needed since an amplitude-based behavioral test proved unreliable here (`LevelAgc` is a
true AGC that normalizes toward a target level regardless of input amplitude once settled). Existing
loc text was already accurate, no label fix needed this time (unlike Auto-stop/Auto-restart).
Restart-only, like every other decoder setting — most user-visible instance of that limitation so
far, since squelch is the control most likely to be adjusted while actively chasing a signal.

**All 4 remaining Decode-tab DSP controls investigated and scoped, 3 deferred** — same session,
right after Sense level. **RX BPF**: legacy's `m_bpf` gate (`sstv.cpp:1826-1833`) is the pre-AGC
filter feeding EVERY downstream stage (sync/demod/AVT), not peripheral; the Kaiser/Bessel FIR branch
its Sharp/Very-sharp presets need is ALREADY ported once (`TxOutputBandpassFilter.cs`'s own
`MakeFilter`/`I0`, reusable), but each preset also changes tap count → group delay, and
`SearchBandpassFilter`'s own doc comment explicitly reasons sync-anchor correction needs no
adjustment ONLY because today's single fixed preset never changes group delay — a user-selectable
preset breaks that, needing golden-vector-verified re-derivation per tap count. **Demod type**: all
3 demodulator classes (PLL/ZeroCrossing/Hilbert) already exist, but the main picture path is
hardwired to Hilbert only — needs real runtime dispatch plus per-type sync-anchor handling, the
biggest/riskiest of the four. **RX buffer**: DOES have a real legacy control after all
(`sys.m_UseRxBuff`/`RGRBuf` — an earlier "no precedent" note in this doc was from grepping the wrong
field names) but backs a sample-rate/slant-recalibration REPLAY mechanism
(`Main.cpp:5601-5864`) this port's own code already documents it doesn't have
(`AnalogFmSstvDecoder.cs:1281-1284`'s `m_ASDis` comment) — wiring the UI control today would be a
fake no-op. **Auto-start**: gates an existing trigger (no new DSP math, looked tractable) but this
port's `ISstvDecoder` has no "disarmed, still live" state exposed and legacy's trigger is inline
across multiple branches, not one choke point — needs its own design pass. All 4 are logged in
`spec/14-roadmap.md`'s "Options dialogs" bullet with full citations; none bundled into ordinary
wiring sessions going forward — each needs its own dedicated DSP/architecture session.

**Options General tab's "Remember window position and size" wired, closed** — same session. Real
port of legacy's `sys.m_MemWindow` (`Option.cpp:250/616`): new `WindowGeometrySettings`
(`ScanlineStudio.UI/Settings/`, deliberately bypasses `OptionsSnapshot`/`OptionsSettingsService`
since it's a UI-owned section — routing it through `Application` would invert the layering; matches
`TxPaneUiSettings`' existing precedent), `MainWindow.axaml.cs` restores/saves Position/Width/Height
gated the same two ways legacy is (checkbox AND `WindowState == Normal`). **Two real bugs, both
caught only by actually launching and closing the real app, not by build/tests**: a naive
synchronous `.GetAwaiter().GetResult()` on the settings load deadlocked the app on startup (unlike
`Program.cs`'s same-shaped precedent, which runs before Avalonia's UI-thread `SynchronizationContext`
exists — this call site runs after; fixed with `Task.Run`), then reading `Width`/`Position` from
inside that `Task.Run`'s pool-thread delegate crashed the app on close with "Call from invalid
thread" (Avalonia `Layoutable` properties are UI-thread-only; fixed by capturing them into locals
first). Full round-trip verified in the real app: toggled, saved, moved/resized, closed, relaunched,
confirmed restored; Reset section correctly clears the flag without touching stored geometry.
**JPEG quality re-scoped, not wired**: legacy's `m_JPEGQuality` applies to the manual "Save Image
As..." dialog (`SaveBitmapMenu`/`SaveImage`, `Main.cpp:10059-10084`), not the automatic RX-history
save this port already does differently (always PNG) — real scope is bundled with the Gallery's
still-STUB "Export frame" button, not a standalone Options control.

**Options Audio tab's 3 already-wired-backend controls exposed, closed** — same session, per the
"keep going the list" instruction. Checked all 5 remaining Audio-tab stub controls against real
backend code before wiring anything: `AudioDeviceSettings.CaptureChannelSource`/`StereoTxEnabled`
and `AppPerformanceSettings.ProcessPriority` were ALL already fully implemented and already consumed
by real call sites (`SstvSessionService.StartReceivingAsync`'s TX/RX paths, `Program.cs`'s startup
priority-set) — genuinely real backend capability with zero Options UI path, exactly the same shape
as the QRZ/Gallery-Note discoveries earlier this session. Wired all 3 (Application process priority
Normal/High, Stereo capture source Mono/Left/Right, Stereo TX checkbox) with no new backend code at
all. The other 2 stub controls were investigated and confirmed to correctly STAY stub, not gaps:
**RX/TX FIFO size** configures Win32 `waveIn`/`waveOut` buffer-queue depth, a concept with no analog
in this port's MiniAudio engine (superseded by its own automatic buffering). **Sound card thread
priority** — legacy's own `sys.m_SoundPriority` is read/persisted but never actually applied to
anything anywhere in legacy's own source (a dead control even there); this port's real analog
(`AudioDeviceSettings.CaptureThreadPriority`, already wired) uses a different 5-value enum that
doesn't map cleanly onto this control's existing 4 legacy-shaped options — needs its own
correctly-labeled control in a future pass rather than a forced relabel. 5 new tests, 218/218 UI
tests passing. Real-window verified: set all 3 controls, saved, confirmed `settings.json`
(`CaptureChannelSource: 2`, `StereoTxEnabled: true`, `AppPerformance.ProcessPriority: 128`), reopened
dialog to confirm reload, Reset section correctly restored all defaults.

**Current wiring totals** (`spec/16-gui-wiring-survey.md`, ~286 controls tracked, fully current as
of this commit): **~147 REAL, ~94 STUB, ~46 FAKE-LIVE, ~1 PARTIAL**. Densest remaining gaps: Receive
tab's Sync&Slant/Input-chain/Signal-quality cards (SNR/squelch/notch/noise-floor — no live
audio-chain measurement exists in `Core.Audio`/`Core.Sstv` for most of these, real new DSP work not
just wiring); Options window's Decode's remaining 4 controls (RX BPF/Demod type/RX buffer/Auto-start,
all scoped and deferred, see above) plus Audio's RX/TX FIFO + Sound-card-priority (2, correctly
staying stub, see above) plus Identification/Advanced tabs (~53 controls, still stub); Transmit
tab's Queue/TX-log/Recently-sent cards (100% stub, no such features exist); TX image editor's
canvas-overlay safe-area/callsign/report-plate text (FAKE-LIVE — reads as real burned-in TX content,
arguably the most deceptive placeholder in the app). Remaining PARTIAL: RxFrameMeta's Note `TextBox`
only (needs a real backing field on the frame/session model — Override-callsign's twin issue closed
via the QRZ lookup wiring).

**Options Radio tab fully scoped, nothing to wire** — same session. All 3 remaining stub controls
investigated and confirmed to correctly stay stub, not gaps: OmniRig radio button is a real
future backend, already fully documented in `docs/removed-features.md`. RTS-on-RX and PTT-lock both
gate legacy's raw-serial RTS-pin PTT keying (`Comm.cpp`) — the same "hand-written per-rig protocol
code" family CLAUDE.md §2 already excludes; this port's real PTT path (`IRadioController.SetPttAsync`)
goes through Hamlib/rigctld/flrig, which own their own connection lifecycle, so there's no
raw-serial-port concept left for these two settings to gate. New `docs/removed-features.md` entry
added ("Raw-serial RTS-pin PTT keying"). Doc-only change, no code touched, no tests affected.

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

**Next action on resume**: user said "just keep going the normal list, we have the priority list
just keep going no need to ask me until the list is finished" (2026-08-12) — standing authorization
to proceed through the Must-implement backlog autonomously, no per-item confirmation needed. Still
following the established process (research → auditor plan-review for anything DSP/architecture →
implement → verify → commit) at each step, just not pausing between items. Working order so far:
Decode tab wiring (done) → Decode tab's 4 DSP items (all scoped, deferred to dedicated sessions,
see above) → Options General tab (window-geometry done, JPEG re-scoped) → Options Audio tab (3
already-wired-backend controls exposed, 2 confirmed-correctly-stub, done, see above) → next up:
**Options Radio tab** (OmniRig/RTS-on-RX/PTT-lock — check each against `Option.dfm`'s
`OmniCheck`/`CBRTS`/`PTTLock` controls the same way Audio's controls turned out mostly
already-backed; don't assume they need new work without checking first), then
**Identification tab + CW-ID/FSK** (scope together, real overlap), then **Advanced tab** (likely
overlaps the deferred Demod-type/RX-BPF work), then the smaller items (VOX, Sound-file ID, JPEG —
now known to belong with Export-frame). Task-tracker IDs 31-41 hold the full breakdown if resuming
mid-list after a `/clear`.
