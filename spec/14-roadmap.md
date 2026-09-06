# Roadmap

> Closed/DONE roadmap history has been split out to `spec/14-roadmap-archive.md` (verbatim, unabridged) to keep this file smaller. Sections below marked DONE/CLOSED are compact summaries pointing there for full detail.

## Related

Ties together every other document — each phase below is delivered by working through the "Definition of done" checklists in the referenced specs, in order, keeping commits small and reviewable per CLAUDE.md. Also see [LICENSES.md](../LICENSES.md) and [docs/removed-features.md](../docs/removed-features.md), both living documents that get new entries as work in these phases proceeds.

## Sequencing principle

Order is chosen so that at the end of every phase there is a **runnable, demoable** program, never a long stretch of code that doesn't build into something observable. This also front-loads the highest cross-platform risk (audio, DSP) rather than saving it for last, since [[05-audio-engine]] is the area most likely to reveal that an architectural assumption needs revisiting.

## Path to 0.9 beta / road to 1.0 — priority tiers (2026-08-13)

**Superseded 2026-08-15 by `[[18-path-to-1.0]]`** for Tier 0/Tier 1's "DONE" status specifically —
a full milestone audit (`docs/audit-playbook.md`, 8 parallel `auditor` passes) found the core TX
loop broken with the default radio backend, a core-loop regression that postdates the "DONE"
marking below. This section's Tier 2 backlog remains valid and unaffected.
**Chain continues, corrected 2026-08-22**: `[[18-path-to-1.0]]` is itself now complete (1.0 COMPLETE,
2026-08-16), and its own successor `[[19-path-to-1.1]]`'s confirmed 1.1 target (the TX template
editor redesign) is implemented too (2026-08-18) — see `[[19-path-to-1.1]]` for the current priority
list to work from, not `[[18-path-to-1.0]]`.

Supersedes ad-hoc prioritization scattered across "Must-implement backlog," "Explicitly deferred
beyond v1," "Release gates," "Open items requiring a decision," and "Phase 4+ backlog" below —
those sections are kept in place (banners added at each, nothing deleted) for their research/
citation detail, but **this section is the priority list to work from**, not those.

Goal: get the core loop (receive a picture, send a picture, edit a picture before sending) to
actually, honestly work end-to-end, ship that as 0.9 beta, then expand outward — bugs, features,
stub-replacement, in that order. Deprioritization here can be aggressive and long-horizon on
purpose (user's own framing: "if some features get put on 'this will only be looked at in 3.4,
then so be it'") — Tier 3 is not a soft "someday soon," it's parked with no implied revisit date.

Built from a dedicated Opus investigation (2026-08-13, source-traced not doc-trusted) into whether
receive/send/edit actually work today, cross-referenced against `spec/14-roadmap.md`'s own backlog
and `PROJECT_BRIEF.md`, then a full auditor completeness pass on the reorganization itself (same
date, GO-conditional, findings folded in below) to make sure nothing tracked got lost in the
regrouping.

### Tier 0 — must fix before calling anything "0.9 beta" — **DONE, 2026-08-14**

All Tier-0 blockers closed: 7 fake-live/dead controls fixed (TX editor tool strip, dead Transmit/Tune/Preview/Halt row, unbound adjustment sliders, decorative overlay, Receive's dead Incoming-frame buttons, Mode-card "Locked" toggle), Auto-start and RX-buffer hardcoded-default lies in Options fixed. The suspected "Robot 36/72 replay chroma-bleed" DSP bug was investigated (2 rounds of auditor plan-review plus a direct empirical measurement) and closed as NOT a bug — an artifact of the test image itself. 2 rounds of real auditor code-review, 242/242 `ScanlineStudio.UI.Tests` green. Pushed to `origin/master` since (stale "not yet pushed" note removed).

Full detail: `spec/14-roadmap-archive.md`.

### Tier 1 — cheap trust fix before a *public* beta (not a Tier-0 blocker) — **DONE, 2026-08-14**

Absorbed into the Tier-0 UI-honesty sweep rather than run as its own pass — the same "no fake-live items" bar already covered every Tier-1 item (status-bar SNR, Signal-quality Min/Max, Input-chain Squelch, Frame-metadata Frequency, Gallery per-entry SNR all now genuine `—` placeholders, verified against `en.json`). `spec/16-gui-wiring-survey.md`'s own staleness precondition was independently resolved by its own refresh the same day.

Full detail: `spec/14-roadmap-archive.md`.

### Tier 2 — road to 1.0 (real gaps, correct to ship 0.9 beta without)

- ~~RX buffer Phases 7-9~~ — **DONE, all 9 phases complete as of 2026-08-15.** Disk-backed Extended
  mode (Phase 7), the "Correct Slant" one-shot search (Phase 8), and Options dialog UI wiring
  (Phase 9, un-stubbed `RxBufferMode` radio group + Auto-Slant `IsEnabled` gating — also closes the
  Tier-0 "RX buffer shown hardcoded-Off" stub) all shipped, each through auditor code-review (Phase 8
  additionally through 2 full plan-review rounds per CLAUDE.md §7's decode-path rule). Full history:
  `git log --oneline` (search "RX buffer subsystem Phase"), `PROJECT_BRIEF.md`,
  `/home/artien/.claude/plans/coppery-staging-heron.md`/`wise-riding-hearth.md`. Real follow-up gaps
  this work surfaced, still open (not the same as the phases above, don't conflate): Auto-Slant's
  convergence characteristic changed materially at Phase 6d (`SlantTracker.ResetBaseline()` now
  actually runs in production) with nothing measuring whether that's better or worse, and
  `SlantTests.cs`'s "bitmask permanently latches" doc comment is now stale for the default path; a
  bounded one-line `_suppressNextSlantProcessLine` bookkeeping loss when a manual-ReSync suppression
  and an automatic replay land in the same per-line iteration; `DrainPendingSkip`'s own
  staging-buffer-discontinuity gap (currently unreachable, but **documented as must-resolve before
  any future manual-redraw UI trigger** — a real precondition on a not-yet-built feature); the two
  items below (Correct Slant's own missing UI trigger, and the logging-coverage audit) surfaced
  during Phase 9's own real-window verification.
- ~~**"Correct Slant" UI trigger"**~~ — **DONE, 2026-08-15.** A sibling "Correct slant" button next
  to the existing Re-sync button (`MainWindow.axaml`), wired through `ISstvSessionService`/
  `SstvSessionService` (new `[LoggerMessage]` log line included) to `RequestCorrectSlant()`. Verified
  against legacy source (`KRCS`/`Main.dfm`'s `PopupR` popup menu, same menu as `KRFS`) that this is a
  genuinely different control from the still-unresolved "Reset" button in the same row (deliberately
  left untouched — `spec/17-rx-telemetry-feasibility.md`'s own warning). Real-window verified,
  including catching a real column-width truncation bug before it shipped. 1 round of code-review
  (GO), pushed as `3d4d151`.
- ~~**Audit INFO/DEBUG logging coverage across the app**~~ — **survey DONE 2026-08-15, fixes DONE
  2026-08-15** (user request). Static survey of all 51 `[RelayCommand]`s across 9 ViewModels + 9
  `SstvSessionService` state-changing methods + RX event subscribers found: 40/51 UI commands
  already logged, TX lifecycle (`TransmitAsync`→`PlayWithPttAsync`) fully covered, but 4 real gaps —
  `LogbookPaneViewModel.New()`, `RxHistoryPaneViewModel.OpenInLog()`, `TxImageEditorPaneViewModel`
  (zero logging infra in the whole file — `Apply()`/`Cancel()` now covered, required threading a new
  `ILogger<TxImageEditorPaneViewModel>` through `TxControlsPaneViewModel`), and the
  `ISstvSessionService.ModeDetected` event (logged once now, at `RxImagePaneViewModel` as the
  canonical subscriber, of its 3 UI-layer subscribers). All 4 fixed, build clean, 246/246 UI tests
  pass, single-round auditor code-review per CLAUDE.md §7's mechanical-work tier. **Two gaps found
  but NOT fixed, carried forward separately below**: `DecodeRestarted` (needs an `ISstvSessionService`
  API addition first, bigger than a logging fix) and Auto-Sync/Auto-Slant/Auto-Stop commit logging
  (no event exists to hook a log call onto at all — these are polled properties, not events; would
  need a new `ISstvDecoder`-level event, a design question, not a logging pass).
- ~~**`DecodeRestarted` had no UI-layer subscriber**~~ — **DONE 2026-08-15.** `ISstvDecoder.DecodeRestarted`
  fires on a new sync lock found mid-reception, `ForceMode`, or Auto-Stop abandonment (NOT "sync
  lost" — an earlier draft of this entry and this fix's own first-pass log message both got that
  backwards, corrected after auditor review) — was never forwarded past `Core.Sstv`, so no
  `ScanlineStudio.UI` code could observe it (`Core.Logbook`'s own separate `ReceiveHistoryRecorder`
  subscriber, which saves a partial image at ≥65% complete, already existed and is unaffected).
  Added `ISstvSessionService.DecodeRestarted` (mirrors `ModeDetected`'s own passthrough shape
  exactly) and a logging-only subscriber in `RxImagePaneViewModel` (no new bound UI state — a
  logging fix, not a restart-UI feature). 1 round of auditor code-review found a real wording bug
  (log message/doc comments claimed the wrong dominant trigger) — fixed, not just noted.
- Advanced tab: PLL/Zero-crossing tuning-parameter UI — **correction 2026-08-15, this entry's own
  "backend already real" claim was wrong, caught before starting work on it**: verified directly
  against source (no legacy clone available in this sandbox to cross-check the DSP semantics
  either, a separate blocker) — `PllFmDemodulator`'s constructor DOES accept `loopOrder`/
  `loopCutoffHz`/`outputOrder`/`outputCutoffHz` with legacy-matching defaults, but
  `AnalogFmSstvDecoder` always constructs it with ONLY `sampleRate`/low/high-Hz, never threading a
  settings value through; `ZeroCrossingFrequencyCounter`'s constructor doesn't even accept
  order/cutoff/smoothing params at all; `Vco` (the PLL's own VCO) has no gain parameter anywhere.
  None of the 7 Advanced-tab PLL/Zero-crossing `NumericUpDown`s have a real value to bind to today
  — this is genuinely unbuilt DSP-parameter-exposure work (decode-path/DSP-core, CLAUDE.md §7's
  full 2-round-plan-review-plus-code-review tier, not a UI-wiring task), not a stub-button fix.
  TX BPF/LPF toggle (the filter already applies unconditionally — this is a bypass switch only, not
  core), Loopback/calibration wizards (fully unbuilt).
- Auto-start (Decode tab) — real legacy behavior, needs its own scoping pass: no single choke point
  exists today (`AnalogFmSstvDecoder` has no "disarmed, still live" state), legacy's trigger is
  inline across multiple sync-detection branches.
- Options dialog placeholders (~50 items: RadioSettingsDialog/MacroKeyEditor/ColorSettingsDialog/
  LanguageSettingsDialog) — genuinely blocked on a per-section split + scoping pass, several
  sections overlap with already-shipped work (waterfall palette, CW-ID/FSK) so building this as one
  lump risks duplication.
- ~~QRZ.com callsign lookup~~ — **stale entry, removed 2026-08-14**: verified against current source
  before starting work on it — `LookupQrzCommand` is real, bound, calls the real
  `ILogbookSessionService.LookupCallsignAsync` backend (`RxImagePaneViewModel.cs:1098-1106`, citation corrected 2026-08-22); `NameDisplay`/
  `QthDisplay`/`GridDisplay` all real. This was already shipped 2026-08-11/12 (`spec/16-gui-wiring-
  survey.md`'s QRZ.com tab entry), predating this Tier list — carried forward as open by mistake.
- CW-ID/FSK residuals (subsystem itself shipped 2026-08-12, these are real leftovers, not "done"):
  ~~NR/RST sub-packet has real backend settings but zero Options UI~~ — **DONE 2026-08-15**
  (`OptionsWindowView.axaml:483-487`, citation corrected 2026-08-22, `NrRstEnabled`/`NrRstText`, mirrors the `CwText`/
  `FskIdTxEnabled` sibling controls' pattern; 1 round auditor code-review, 2 doc-only nits fixed).
  Still open: `MacroTextResolver` only covers `%m`/`%D`/`%T`, not his-callsign/name/QTH/RST
  tokens (`%c`/`%n`/`%q`/`%r`/`%s`/`%R`/`%N`) since those need a "current QSO" context concept that
  doesn't exist. The FSK-decoded-callsign auto-fill gap is now closed — `RxImagePaneViewModel.cs`
  writes to `OverrideCallsign`, and "Log QSO" (**DONE 2026-08-15**, see below) now reads it into a
  real logbook entry via `PrefillForNewEntry`.
  VOX and Sound-file ID (`.mmv` playback) are explicitly **not built** — `OptionsWindowView.axaml:
  494-501` (VOX disabled, citation corrected 2026-08-22), `AnalogFmSstvEncoder.cs:274-277` ("out of v1 scope," silently transmits
  nothing today, matching legacy's own unconfigured-sound-file behavior — a benign no-op, not a
  lie, but not done either). Correction: an earlier `PROJECT_BRIEF.md` note claiming these were
  "bundled into CW-ID/FSK, done" was wrong, verified against source 2026-08-13.
- ~~Logbook deltas~~ — **PARKED 2026-08-15, ACCEPTED AGAIN AND SHIPPED 2026-08-29** (user redirect,
  then reversed): QSL sent/received flags, duplicate-QSO detection (by callsign/band), delete-a-QSO.
  Was explicitly carved out of the shipped Logbook pane (`QsoRecord.cs:12-13`); a full
  2-round-plan-reviewed implementation plan existed at `/home/artien/.claude/plans/logbook-deltas.md`
  but the user reframed scope before implementation started: "basics only" for the Logbook pane
  itself (already sufficient — add/edit/search/ADIF export), prioritize SSTV-side work over deeper
  logbook features (see `feedback_logbook_minimal_footprint` memory). **2026-08-29: accepted as a
  `ui_transition_plan.md` step 15 Tier 3 decision, then built and shipped the same day** (all 3
  pieces, commit `6aef2b7`) — the resumed plan needed real updates before implementation (a
  `_formGeneration` staleness guard and a `MainWindow.axaml`-is-inline drift the 2-week-old plan
  predated), confirmed via re-validation against current code first, not assumed still accurate.
  **Correction 2026-08-14**
  (predates the redirect, kept for history): Gallery "Log entry"/"Open in log" cross-pane wiring is
  NOT open — verified real, shipped 2026-08-11 (`OpenInLogCommand`/`QsoLinkWindowView`, commit
  `13a8ca7`, `spec/16`'s own entry).
- ~~**ADIF UDP forwarding (multi-destination)**~~ — **DONE 2026-08-15**, shipped as the direct
  alternative to the parked Logbook-deltas item above. Generalized the existing GridTracker-only
  UDP streamer (`GridTrackerStreamer`, shipped as part of the original Logbook backend,
  2026-08-07) into `AdifUdpStreamer`/`AdifUdpStreamingSettings`: fans the same WSJT-X
  `LoggedADIF` datagram out to any number of configured destinations (verified via web research
  that GridTracker2/N1MM Logger+/Log4OM all listen for the same protocol — this was a
  generalization, not 3 separate integrations) plus a new Options dialog "Forwarding" tab (the
  feature previously had zero UI, settings-only via hand-edited `settings.json`). Full plan +
  2 rounds of plan-review + 2 rounds of code-review per piece (both pieces) at
  `/home/artien/.claude/plans/adif-udp-streaming.md`; commits `90575c8` (backend) and `e60e3d4`
  (UI). See `spec/08-logging.md`'s own "ADIF UDP forwarding" section for the settings-migration
  contract (legacy single-destination config auto-seeds one row on first load, `ClientId`
  preserved across saves with no dialog control of its own).
- ~~**"Log QSO" from the RX pane**~~ — **DONE 2026-08-15.** Un-stubbed the RX pane's Log QSO
  button (enabled once a mode has ever been detected this reception); switches to the Logbook tab
  (app's first VM-driven `TabControl.SelectedIndex` binding) with a fresh entry pre-filled from
  the RX pane's live callsign/mode/start-time plus any QRZ lookup already done there. 2-round
  plan-review found 2 real blockers (a latent `OverrideCallsign`/lookup-field staleness bug this
  feature would have turned data-corrupting, and a wrong AXAML binding-path assumption), both fixed
  before implementation; 1 round code-review found no blockers, 1 real nit (Name/QTH/Grid weren't
  carried into the prefill) fixed. Full plan `/home/artien/.claude/plans/rx-log-qso.md`, commit
  `321146f`. One accepted, documented risk left open: a manually-typed `OverrideCallsign` can be
  wiped by a mid-reception `DecodeRestarted`/`ModeDetected` refire (forced-mode-change/Auto-Stop
  case) — no "user-edited" flag exists to distinguish that from stale auto-fill; not fixed here,
  would need new state tracking beyond this feature's scope.
- ~~**"Export frame" from the Gallery pane + JPEG save quality**~~ — **DONE 2026-08-15.**
  Un-stubbed the Gallery's "Export" button (saves the selected received image to a user-chosen
  location via a real Avalonia save dialog, optionally re-encoded as JPEG) together with the
  Options dialog's previously-label-only `JpegQuality` control, as this entry's own original note
  said they should be built. 1-round plan-review found the picker needed to resolve format from
  `SaveFilePickerResult.SelectedFileType` (not path-extension-sniffing — confirmed platform-split
  behavior), a missing `CanExportFrame` notify-wiring gap, and a settings.json quality value
  needing clamping. Code-review found the picker's format-resolution logic had zero test coverage
  despite being the exact piece real-window testing confirmed necessary — extracted and covered by
  6 new unit tests. Full plan `/home/artien/.claude/plans/export-frame-jpeg-quality.md`, commit
  `90e2881`. Real-window verified end-to-end (seeded test data, real GTK save dialog, confirmed
  extension-normalization empirically necessary and working, verified the saved JPEG via PIL).
- Receive tab Sync&Slant/Input-chain/Signal-quality cards — real new DSP work (no live audio-chain
  measurement exists for most of these), long-term counterpart to Tier 1's short-term grey-out.
- Localization completion — remaining unlocalized views + community-translation workflow.
- `TemplateCatProtocol` fallback (`[[03-cat-layer]]`) — still a real planned deliverable (it's the
  named example in CLAUDE.md §4's binary-is-bytes rule), zero occurrences in `src/` yet.
- Offline callsign/country lookup (`[[08-logging]]`) — distinct from QRZ.com's online lookup
  (`QrzCallsignLookup`, Tier 2 item above); blocked on the Clublog `cty.dat` API-key human action
  (separate axis below), but the lookup feature itself belongs here, not just its blocker.

### Tier 3 — parked, no near-term plan (existing "Explicitly deferred beyond v1" list + additions)

Perspective correction/webcam capture, full Hamlib extended command-set coverage, plugin sandboxing
beyond same-process isolation, legacy `.MDT` log import, SSTV repeater/beacon mode, contest logging (fully out of scope, not just deferred), OCR
(no legacy precedent — verified zero OCR anywhere in `yoniq-old/`), stereo L/R input-chain level
meters (blocked on a mono-vs-stereo architecture decision, not just wiring), Unattended RX
(scan/watch/dwell/alert — no design exists for what "watching" or "scanning" means here), Session
Frames (no per-session frame-log concept has ever been designed). **Not Tier 3, permanently out of
scope instead**: legacy `.mtm`/`.mti` template import — `ui_transition_plan.md` step 14, **rejected
outright 2026-08-29** (a final "we don't want it" product call, not a parked-for-later item; see
`docs/removed-features.md`'s "Legacy `.mtm`/`.mti` template import" entry). Adding, same tier: **Phase 5
plugin system** entire (`IPlugin`/`PluginHost`/`IImageFilter` — none exist in `src/` yet); waterfall's
3 deferred sub-items (interactive notch-filter marker — no notch DSP block exists to back it; a
dedicated **audio/DSP-derived** signal-strength meter in the waterfall pane itself (narrowed
2026-08-22 — a separate, CAT-sourced S-meter now exists, `RadioState.SignalStrengthDb` →
`RadioStatusViewModel.RxLevelDb`, via rigctld's `l STRENGTH`/Hamlib; this item is specifically about
a waterfall-native meter, not "no S-meter exists at all"); legacy debug "digital scope" tool); **flrig client backend** (since
shipped, see [[03-cat-layer]]'s "Definition of done") and **OmniRig-as-client** (accepted
2026-08-29, implemented same day — see this document's own OmniRig entry above and
[[03-cat-layer]]'s matching update; both quotes here are historical, describing an earlier
"not committed" state). **Added 2026-08-25**: Transmit tab
Queue/TX-log/Recently-sent cards — user decision "maybe one day," moved out of Tier 2. No queueing,
sent-frame-log, or send-history concept exists anywhere in `TxControlsPaneViewModel`; each needs a
new subsystem (persistence + UI), not a wiring fix. **Added 2026-08-25 (second pass)**: the Transmit
tab's Monitor audio row, same "maybe one day" decision — zero legacy precedent and no tappable
signal in the TX audio path. **Added 2026-08-25 (third pass)**: the Transmit tab's Occupied BW row,
same "maybe one day" decision, moved here from its own standalone abandoned-plan note — and all
five of these (Queue, TX Log, Recently Sent, Monitor audio, Occupied BW) were **removed from the UI
outright** the same day, not left as stubs, per direct user instruction. See the "Explicitly
deferred beyond v1" list below for the per-item detail.

~~`.ini` legacy settings importer + migration chain~~ — **PARKED 2026-08-15, user redirect** ("not
important, put it on the maybe one day"). Still a stated CLAUDE.md §2 backward-compatibility
commitment ("`.ini` settings still import cleanly"), not yet built (`[[12-settings]]` has a design
sketch, no `IniImport`/`LegacyIni` anywhere in `src/`) — real legacy `.ini` fixtures ARE available
locally (`yoniq-old/YONIQ-main/Mmsstv.ini` etc., confirmed present after an earlier false "no
legacy clone in this sandbox" conclusion mid-session, see `feedback_yoniq_old_clone_location`
memory) if this is ever picked back up. A scoping-only research pass was started (mapping the real
file's ~50 `[Section]` headers against this port's actual settings model) but stopped mid-run at
the user's redirect before producing a plan — no `~/.claude/plans/` file exists for this, start
fresh if resumed. Rough size, from what was seen before stopping: the file is 1118 lines/~50
sections, but most (window layout, recent-files lists, external "Program" launcher slots, VCL menu
customization) have no equivalent in this port at all; the genuinely portable slice is likely
narrow (operator callsign/name/grid, audio device, rig/CAT settings, a handful of decode toggles).

**SSTVAE mode** (user-flagged 2026-08-14, `https://github.com/arodland/SSTVAE`) — a genuinely new
category, not a legacy YONIQ/MMSSTV mode to port: a convolutional-autoencoder image codec sent as
OFDM carrier amplitudes (real-valued latents, not bits/packets), 3 modes at 32/64/95s in ~1200Hz at
640x480, own separate C++/Qt desktop app (Artistic-2.0 licensed, distinct from this project's
LGPL-3.0). Verified directly (`gh repo view`, 2026-08-14), not assumed from the name. Why this stays
parked, not just "later": upstream's own README states the on-air format is explicitly **not
frozen** ("expect incompatible changes," two stations must run the same commit AND the same ~40MB
model checkpoint to interoperate) — building against a moving target now would mean re-doing it;
the DSP shape (neural decode + OFDM demod) is orthogonal to every other mode in this port (FM
continuous-tone scanning) rather than a variation on it, so it's not a small addition; and
redistributing/bundling the model checkpoint needs its own license-audit line in `LICENSES.md`
(CLAUDE.md §5's rule) before any integration. Revisit once upstream tags a real release and freezes
the wire format, not before.

Explicitly re-flagged per the user's own framing: no implied "revisit soon," fine to land whenever
someone actually asks for one of these, not before.

### Separate axis — release gate and human-only actions (not code work an agent can complete alone)

- **The actual release blocker for any tagged release**: full `[[13-testing]]` manual hardware
  checklist (real rig CAT session, real audio device round-trip, real third-party `rigctld`
  interop) passing on at least one Windows, one Linux, and one macOS machine — only Linux has ever
  actually been run. **Open decision, not yet made**: can 0.9 beta ship Linux-validated-only
  (clearly labeled) with the full 3-platform pass required before 1.0 instead, or does even the
  beta need it? Needs a user call, not an agent guess.
- Clublog `cty.dat` callsign-prefix/country dataset license — no fee, but redistribution requires a
  human to email Clublog's helpdesk and obtain an individual API key before bundling (blocks
  Phase 4 logging dataset work).
- Chilkat/FastReport license status — needs confirming whether either actually backs a real legacy
  feature by running the legacy binary directly (not verifiable from source alone); currently
  assumed unused/orphaned from a source-only search.
- `Terms.txt` freeware-clause interpretation confirmation with upstream author (JE3HHT) — only
  relevant if the project ever moves toward commercial distribution, not a development blocker.

## Phase 0 — Walking skeleton

- [[01-architecture]]: solution scaffold, DI host, nullable+warnings-as-errors, empty Avalonia window boots on Windows/Linux/macOS.
- [[13-testing]]: CI matrix running (even with near-zero tests) so every subsequent PR is gated from day one.
- [[12-settings]]: `ISettingsStore` minimal implementation (no migration chain yet).

**Demo:** app launches on all three OSes, shows a blank window, settings file is created on disk.

## Phase 1 — Audio + DSP core (no UI, no radio)

- [[05-audio-engine]]: capture/playback on Linux **done and real-hardware-tested** (`ScanlineStudio.Core.Audio.MiniAudio`, `ScanlineStudio.Core.Audio.MiniAudio.Tests`); Windows/macOS **not yet verified** — see below. `FakeAudioEngine` round-trip test added first, closing a Definition-of-Done gap that had been believed already met but wasn't. Backend choice re-litigated and reversed: PortAudio (the spec's original pick) rejected after direct verification found it fails this spec's own requirements (no real device-change API; no PulseAudio/PipeWire host API on Linux; no sample-rate conversion) — switched to `miniaudio`, vendored at pinned tag `0.11.25` behind a hand-written C shim (`native/scanline_audio.c`/`.h`) exposing this project's own ABI, never miniaudio's own structs directly. See [[05-audio-engine]]'s Backend choice section for the full reasoning.
  - Built piece by piece, each independently tested against real PipeWire virtual devices before moving on: device enumeration + native-format probing, an allocation-free ring buffer (`ma_pcm_rb`, reused not reinvented), the real capture path, the real playback path with underrun/back-pressure, a device-free resampler-quality measurement (miniaudio's only built-in resampler — linear, "fastest, lowest quality" per its own docs — measured to add ~0.07 average per-channel delta on a real Martin M1 round trip; negligible, no escalation needed), a `RequiresPipeWireFactAttribute` so these hardware tests degrade to an honest CI skip rather than a hard failure on Windows/macOS runners or a Linux runner without a running audio server, and a hot-unplug investigation against a real virtual sink unloaded mid-session.
  - **Real bug found and fixed via that last piece**: disposing a capture or playback session whose device had already disappeared could hang indefinitely — root-caused to miniaudio's PulseAudio backend blocking forever in `ma_wait_for_operation__pulse` waiting for a server reply that will never come once the backing device is gone (confirmed directly against the pinned `miniaudio.h`, not assumed). Fixed by bounding the native close call with a timeout on a dedicated thread in both `MiniAudioCaptureSession`/`MiniAudioPlaybackSession`. The same investigation also falsified this piece's own original premise — the device notification callback's `stopped` event does **not** fire when a device disappears this way on PulseAudio, only on an actual server-side suspend/resume — corrected in [[05-audio-engine]]'s Device hot-plug section rather than left standing.
  - `miniaudio.h` (dual Unlicense/MIT-0, Scanline Studio elects MIT-0) recorded in [LICENSES.md](../LICENSES.md), including disclosure of the embedded (but compiled-out via `MA_NO_DECODING`) `dr_wav`/`dr_flac`/`dr_mp3` source.
  - **Still open**: Windows (WASAPI)/macOS (CoreAudio) have never been run against real or virtual hardware — this dev sandbox is Linux-only, so this needs a human on each OS; do not assume the PulseAudio-specific findings above (silent hot-unplug, close-hang) do or don't apply there without testing.
  - **Audio 1b done, Windows/macOS legs unverified**: added `BuildNativeShimWindows` (`cl.exe`) and `BuildNativeShimMacOS` (`clang -dynamiclib`) MSBuild targets alongside the existing Linux one, each following miniaudio's own documented per-platform build requirements (`native/miniaudio.h`'s own "2.1 Windows"/"2.2 macOS"/"2.3 Linux" sections) rather than guessed flags — e.g. Windows needs no include paths or linked libraries at all (WASAPI/WinMM load via `LoadLibrary` at runtime), matching macOS's equivalent claim for CoreAudio/AudioToolbox. Added a `ilammy/msvc-dev-cmd` CI step so `cl.exe` is actually on `PATH` on `windows-latest` (not there by default outside a Developer Command Prompt). Linux path regression-tested (full suite still green); Windows/macOS cannot be tested from this Linux-only sandbox.
  - **CI signal received after the Engine 0-6 push (`IAudioEngine` composition): `windows-latest` fails.** At the time this was deliberately deferred (user decision — not chased immediately, to avoid a context-switching Windows-only native-build debugging detour mid-audio-engine-work). **Resolved** — see the "Windows CI fix" section later in this document (root cause found and fixed: an MSB4126 env-var leak; verified green on all 3 legs). This historical entry is kept for context, not as an open item.
  - Went through 3 rounds of Opus verification against the finished Audio 0-9/1b/6b implementation, each round finding real bugs (not nitpicks) in the previous round's own fixes: a Windows DLL export gap, a device-id conversion gap on macOS/Windows, a context-mutex lifecycle race (TOCTOU against a concurrent teardown), several Dispose-vs-concurrent-use races (fixed with `ReaderWriterLockSlim` on every session/ring type), a `RefreshAsync`/`Dispose` TOCTOU in the enumerator, and a self-join deadlock (a `TaskCompletionSource` completing synchronously on the drain thread, then that same thread trying to `Join()` itself). Stopped at 3 rounds by explicit user decision, not because issues ran out — reasoning: further review of the audio engine *in isolation* has diminishing returns; the more valuable next review is once real cross-component interaction exists to observe (see the planned `IAudioEngine` composition below).
  - **Composing `MiniAudioCaptureSession`/`MiniAudioPlaybackSession` into a real `IAudioEngine`** (`MiniAudioEngine`, in `ScanlineStudio.Core.Audio.MiniAudio` — must live there, not `ScanlineStudio.Core.Audio`: the sessions are `internal` and `AssemblyInfo.cs`'s `InternalsVisibleTo` only grants the MiniAudio test project). Plan verified by an Opus plan-review pass that actually read the current session/interface source rather than a summary (this project's established methodology) — it found 3 real gaps the original breakdown missed, each independently re-confirmed against source before being adopted here (not taken on trust):
    - **Engine 0**: capture overrun/dropped-frame counter through the native shim (`scanline_audio.c`/`.h` → `NativeAudio.cs` → `MiniAudioCaptureSession`) — closes a stated-but-unfulfilled promise in `IAudioEngine.cs`'s own doc comment ("exposed once piece Audio 5/6 implements the capture/playback paths" — confirmed via grep that no such counter exists anywhere yet). Needed for Engine 5's diagnostics.
    - **Engine 1**: new `AudioDeviceUnavailableException` (`ScanlineStudio.Abstractions.Audio`, per [[01-architecture]]'s Error Handling rule) + `MiniAudioEngine` capture-only lifecycle (`StartCaptureAsync`/`StopCaptureAsync`/`SamplesCaptured`), wrapping `MiniAudioCaptureSession`. Both session constructors block synchronously (confirmed by reading them), so `Start*Async` wraps open/dispose in `Task.Run`; translates every real failure mode (open failure, `MiniAudioContext.Acquire()` context-init failure, `ArgumentException` from an over-long device id, missing native shim). Engine owns the public event and attaches/detaches its own forwarder per session so a restart can't double-invoke. `MiniAudioContext.Acquire()`/`Release()` now happen once per `MiniAudioEngine` instance (ctor/`DisposeAsync`), not per start/stop cycle, avoiding repeated PulseAudio context init/teardown churn. **Self-join deadlock**, found by the plan review and confirmed against `MiniAudioCaptureSession.Dispose()`: its unbounded `_drainThread.Join()` only skips when `Dispose` runs *on* the drain thread — a supported, tested pattern (dispose called from inside `SamplesAvailable`). Wrapping `StopCaptureAsync` in `Task.Run` unconditionally would defeat that guard and hang forever the first time a caller stops capture from inside its own `SamplesCaptured` handler. Fixed by having the engine record the managed thread id its forwarder is currently running on, and calling the session's `Dispose()` inline on that thread when it matches (`Task.Run` otherwise) — plus an engine-level test mirroring the session-level `Dispose_CalledFromWithinSamplesAvailableCallback_DoesNotSelfJoinDeadlock`.
    - **Engine 2**: playback lifecycle (`StartPlaybackAsync`/`StopPlaybackAsync`/`EnqueuePlaybackSamples`). **`PendingFrames` under-delivers on the drain contract**, also found by the plan review and confirmed by reading the native body (`scanline_audio_playback_session_pending_frames` is a one-line `scanline_audio_ring_available_read` — shim-ring occupancy only): it reaches 0 before miniaudio's device buffer and the PulseAudio server's own queue have actually finished playing, so `DrainAsync` alone can truncate the tail of a real transmission, which is exactly what `IAudioEngine`'s "every sample actually played out" contract exists to prevent. Fixed with a short fixed tail-margin delay after `PendingFrames` reaches 0, documented as an empirical (not exact) safety margin and verified with a marker-tone-at-the-end test against a real virtual sink/monitor rather than trusted blind. `DrainAsync`'s own timeout is silent (returns normally either way) — `StopPlaybackAsync` re-checks `PendingFrames` afterward and throws rather than silently truncating. `EnqueuePlaybackSamples` throws `InvalidOperationException` if playback was never started (returning 0 would make a contract-compliant retry loop spin forever).
    - **Engine 3**: lifecycle-transition correctness under concurrency — double-Start, Stop-without-Start (idempotent, matching the session Dispose convention), concurrent Start/Stop races. `SemaphoreSlim(1,1)` per lifecycle (capture, playback — independent), needed because both session constructors/`Dispose` block and must be awaited via `Task.Run` from inside the lock. Session-holding fields are `volatile` (the two synchronous interface members — `EnqueuePlaybackSamples` and the `SamplesCaptured` forwarder — can't take an async semaphore, so they snapshot the field and catch `ObjectDisposedException` instead). Semaphores are never disposed, same reasoning as every session/ring class's `ReaderWriterLockSlim` (a second call's `WaitAsync` must not throw on the semaphore object itself before reaching the idempotency check). `DisposeAsync` never holds both semaphores at once (stop capture then release, drain-and-stop playback then release), so there's no ABBA case to order against. Double-Start throws `InvalidOperationException` (silently ignoring the second device would be the exact silent-failure CLAUDE.md/[[01-architecture]] forbid).
    - **Engine 4**: `IAsyncDisposable.DisposeAsync` — stop capture and drain-then-stop playback if active, itself idempotent.
    - **Engine 5a**: engine-level real-audio integrity test — a short deterministic tone (seconds, not the ~2-minute Martin M1 fixture) through a real virtual sink/monitor, asserting continuity/peak/no dropouts, clean overrun/underrun counters, and no `SamplesCaptured` firing after `StopCaptureAsync` completes.
    - **Engine 5b**: the real payoff test, split from the original single "Engine 5" once the plan review flagged it as too slow/flaky/non-localizing to combine — an actual SSTV encode → `MiniAudioEngine.EnqueuePlaybackSamples` → real virtual cable → `MiniAudioEngine` capture → `SamplesCaptured` → real SSTV decode → image-tolerance comparison, the first time the real audio stack and the real DSP stack run together rather than through `FakeAudioEngine`. Shortest real mode (R24, ~24s) instead of Martin M1 (~114s); staged asserts (VIS detected → correct line count → image tolerance) so a failure localizes; framed as an integration smoke test, not the primary correctness gate for either stack — `AnalogFmSstvDecoder` has no slant/clock-drift correction yet, so accumulated sample-clock drift between two independent miniaudio devices is a known, accepted source of flakiness here.
    - **Engine 6**: DI registration in `ScanlineStudio.Host/Program.cs` — expanded once the plan review checked what "just add two `AddSingleton` lines" actually required: `ScanlineStudio.Host.csproj` had no `ProjectReference` to `ScanlineStudio.Core.Audio.MiniAudio` and none of the three per-OS native-shim copy targets the test project needed for the same documented reason (.NET doesn't propagate a `ProjectReference`'s native build artifacts) — both confirmed missing and added. Registered by type (`AddSingleton<IAudioEngine, MiniAudioEngine>()`), not an eagerly-constructed instance, so native context init doesn't run at process start on a machine with no audio server. `IAudioEngine` is `IAsyncDisposable`-only with no `IDisposable`, and `Program.cs` never disposed the host at all (confirmed) — added an explicit `DisposeAsync` on the classic-desktop lifetime's exit. Resolve-construct-dispose smoke test. Nothing resolves `IAudioEngine` from UI yet (`ScanlineStudio.Application` has zero real source files today) — `ScanlineStudio.UI/App.axaml.cs`'s existing `App.Services` static is a pre-existing service-locator pattern [[01-architecture]] itself forbids for new code, and audio must not be wired through it.
- [[06-sstv-dsp]]: encode/decode round-trip passing for the core mode set, against `FakeAudioEngine` and real audio; sample-clock calibration (slant correction) implemented. FSK/CW station ID was **explicitly deferred, not v1 scope** at the time this was written (user decision, overriding an earlier "regulatory requirement" framing here — see [[06-sstv-dsp]]'s Station ID section for the full breakdown and why: CW ID specifically is real legacy functionality but not required for the program to work as an SSTV encoder/decoder) — **shipped 2026-08-12** (RX and TX both, 6 implementation phases; see the Must-implement backlog's CW-ID/FSK entry for the full history). This sentence is kept for the deferral's original rationale, not as a current-status claim. The one non-station-ID piece of that area, the post-image footer tone, is done — see the mode-table entry below.
  - Mode-by-mode sequencing, each verified individually before moving to the next (explicit user instruction, not a shortcut). **43 of 43 modes done — every mode in the legacy table now has an entry** (`SstvModeRegistry`, all cross-checked against `CSSTVSET::GetTiming`, all read from the actual TX line-generator functions in `Main.cpp` per CLAUDE.md's TX/RX-split and no-assumptions rules — several near-misses caught this way: Scottie's real sync position, Robot 72 vs. Robot 36, MP vs. MR, R24's VIS-code parity bit, RM8/RM12's genuinely-new monochrome shape, and SC2's misleading-looking TX source, all below): Martin M1/M2, Scottie S1/S2/DX, Robot 36/72, AVT, MR73–175, ML180–320, MP73–175, the whole PD-series, Pasokon P3/P5/P7, MN73/110/140, MC110/140/180, R24, RM8/RM12, SC2-180/120/60. Five scanline-codec families exist now (`ScanlineCodecFactory`): `RgbSequential`, `YCbCrRobot` (alternating chroma), `YCbCrSequential` (Robot 72's non-alternating Y+R-Y+B-Y, now also R24), `YCbCrLinePaired` (MP/PD/MN's two-luma-lines-per-chroma-pair, added `RowsPerTransmissionLine` to `IScanlineEncoder`/`Decoder` to support it), `MonoAveragedPaired` (RM8/RM12's monochrome, row-averaging shape, added for this pair). A two-stage "extended VIS" mechanism (escape byte 0x23 + a second raw byte, see `VisHeader`) was added for the MR/ML/MP families.
  - **RM8/RM12 done**, `Main.cpp`'s `LineRM` — a genuinely new shape, not a variant of anything implemented so far: no chroma at all (`GetRY`'s R-Y/B-Y outputs are computed but discarded), and each transmission averages *two* consecutive source rows' luminance into one scanned value, confirmed via the RX decode switch (`Main.cpp`, `case smRM8: case smRM12:`, distinct from R24/R72/MR/ML's shared block) which writes that single decoded value into both of the two output rows it addresses. Needed a new `ColorEncoding.MonoAveragedPaired` family (`MonoAveragedPairedScanlineEncoder`/`Decoder`). While tracing `LineRM`'s own `mp->m_wLine++` alongside the outer TX dispatch loop's own increment, caught and corrected a mistake in the earlier R24 entry: that same double-increment structure exists in `LineR24` too, meaning the TX loop for R24 (and RM8/RM12) only runs half as many times as it looks like at a glance, and reads rows accordingly — see `R24`'s updated doc comment in `SstvModeRegistry`. **Deliberately not ported**: legacy's RM8/RM12 RX applies its own extra gain correction (`d *= 256.0/(256.0-32.0)`) on top of a raw calibration pipeline (`GetPictureLevel`/`GetPixelLevel`) that this port doesn't replicate for *any* mode — applying that specific correction inside this port's much simpler linear frequency-to-pixel mapping would be a mismatched, uninterpretable number, not a faithful port; see `CreateMonoAveragedMode`'s doc comment. Also required a test-methodology fix, not a codec fix: the shared full-color gradient fixture used by every other mode's round-trip test isn't fair to a genuinely monochrome mode (R and G vary independently in that fixture; a real monochrome decode can only ever output R=G=B, so per-channel delta was ~42 on the first attempt) — added a dedicated grayscale fixture and test (`EncodeThenDecode_RoundTripsWithinTolerance_MonoFamily`) instead of loosening the tolerance or changing the encoder/decoder.
  - **MN73/110/140 and MC110/140/180 done**, both `Main.cpp`'s `LineMN`/`LineMC`. Both use a "narrow" frequency range (`NARROW_SYNC`=1900Hz, `NARROW_LOW`=2044Hz, `NARROW_HIGH`=2300Hz, via `ColorToFreqNarrow` in source) instead of the normal 1500–2300Hz. MN is line-paired (same Y-RY-BY-Y2 shape as MP, just narrow-range, `YCbCrLinePaired`); MC is plain sequential R/G/B (`RgbSequential`). **VIS-code search result, confirmed not assumed**: neither has a standard or extended VIS code anywhere in `sstv.cpp`'s VIS-decode switch (both stages searched directly) — legacy instead sends a distinct, small, fixed FSK mode-announce packet in place of a VIS header (`Main.cpp:7395-7424`: `[0x2d][0x15][modeCode][modeCode^0x15]`, 6-bit LSB-first via `WriteFSK`, `sstv.cpp:2942`). This is *not* the general station-ID FSK subsystem (the separate 0x2a-prefixed callsign packet, still unported — see the FSK/CW station ID item above) — it's a small, self-contained sub-protocol, ported as `VisHeader.GenerateNarrowModeSegments`/`SstvModeDefinition.NarrowModeCode`, with matching decode-side discrimination logic added to `AnalogFmSstvDecoder` (`TryDecodeHeader` distinguishes a normal-VIS-shaped preamble from a narrow-packet-shaped one by sampling a window right after the shared 300ms leader, since the two diverge immediately after that point). User's explicit instruction followed here: "do what YONIQ does," not invent a fake VIS code just because the existing mechanism was convenient. Verified with a dedicated `NarrowModeHeader_IsDetected_ForMnFamily` test, decoupled from the frequency-range bug below.
  - **R24 done**, `Main.cpp`'s `LineR24` — same Y/R-Y/B-Y shape as Robot 72 (`YCbCrSequential`, no new codec needed), confirmed via the RX decode switch grouping them together rather than by name resemblance. Caught a real bug while wiring it up: legacy's VIS-decode switch matches the *full* received byte (7 data bits + even-parity bit as the MSB) — e.g. R36's case is `0x88`, but this port's `Robot36.VisCode` correctly stores only `8` (the 7 data bits `VisHeader` actually transmits/reconstructs). R24's case is `0x84`; it was first entered here as `0x84` (132) directly, which doesn't fit in `VisHeader`'s 7-bit code space and silently transmitted as if it were `4` while the registry still compared against `132` — `FindByVisCode` never matched, so the round-trip test's `ModeDetected` never fired. Fixed to `4` (`0x84 & 0x7F`); a good reminder that "read the source" must include reading *how the port's own field already represents that value* for other modes, not just the literal hex byte in the switch statement. Also surfaced (and explicitly did **not** replicate) a legacy display-only quirk, corrected once more precisely while reading `LineRM` for RM8/RM12 right after: `LineR24` does its own `mp->m_wLine++` at its end, *in addition to* the outer TX dispatch loop's own unconditional `mp->m_wLine++` after every mode's switch-case — so each outer iteration actually advances `m_wLine` by 2, the loop (bounded by `m_TL`=`hp`=240) only runs 120 times, and `LineR24` only ever reads the *even* source rows (0,2,4,...,238); odd rows are never touched. This matches `CSSTVSET::SetSampFreq`'s (`sstv.cpp:655`) decode-side `m_L=120` exactly, and RX's row-address logic (`R=y*2`, gated on `y<m_L`) duplicates each decoded even-row value into two output rows to fill a 240-row display canvas. So R24 is a genuine reduced-vertical-resolution mode (120 real rows, nearest-neighbor doubled for display) — not the "redundant transmission, second half discarded" framing an earlier draft of this note incorrectly used. This port models the 120 real rows directly (`ImageHeight: 120`); the display-side doubling is a presentation-layer detail with no round-trip-testable effect — see `R24`'s doc comment in `SstvModeRegistry` for the full corrected reasoning.
  - **`LuminanceMinHz`/`MaxHz` hardcoding bug — fixed**, once every mode above was added (the user-directed sequencing this was deliberately held for). `YCbCrLinePairedScanlineEncoder`/`Decoder`, `YCbCrSequentialScanlineEncoder`/`Decoder`, and `RobotScanlineEncoder`/`Decoder` all used to hardcode the 1500/2300 frequency range literally instead of reading `mode.LuminanceMinHz`/`LuminanceMaxHz` — `RgbSequentialScanlineEncoder`/`Decoder` already did this correctly, and was the template for the fix. `RobotScanlineEncoder`/`Decoder`'s `ColorToFreq`/`DecodePixels` helpers were `static` with no access to the mode, so the fix also threaded `SstvModeDefinition` (`RobotScanlineEncoder`) / kept it (`RobotScanlineDecoder`, which already took `mode` but ignored it for this) through those signatures. Verified, not just applied: moved MN73/110/140 out of the old duration-only `NarrowFamilyLineDurationsOnly` table and into the shared `Modes` table in `SstvRoundTripTests` — their full pixel round-trip (previously blocked; header/mode-detection was already correct and separately tested) now passes. Full suite: 87/87 in `ScanlineStudio.Core.Sstv.Tests`, all green.
  - **SC2-180/120/60 done, the last mode family**, `Main.cpp`'s `LineSC2180`. Looked like it might need a new codec at first glance — the source writes frequencies like `ColorToFreq(cp->b.r)+0x1000`, `+0x2000` for G, `+0x3000` for B — but tracing `CSSTVMOD::Do` (`sstv.cpp:2868`) showed the actual VCO frequency is `(f & 0x0fff) - 1100`: the upper nibble is masked off entirely before use. That nibble is a *separate* per-channel TX-gain tag (`sstv.cpp:2880`, `switch(f & 0xf000){ case 0x1000: d *= m_outgainR; ...}`), only consulted when an optional independent-R/G/B TX-gain-trim feature (`m_VariOut`) is enabled — zero effect on the transmitted frequency/waveform either way, and this port doesn't model per-channel TX gain trim for any mode, so correctly omitted, not silently lost. RX confirms the same reading: SC2-180/120/60 aren't named in the RX per-pixel decode switch's specific-mode cases at all (`Main.cpp:4200-4452`) — they fall through to the generic `default:` branch other unlisted plain-RGB modes use, whose non-MRT write order is phase1→R, phase2→G, phase3→B, matching TX's scan order exactly. So this is plain `RgbSequential` (no new codec needed) — sync=`S`ms@1200Hz, porch=0.5ms@1500Hz, then R/G/B each `tw`ms; totals (711.0437/475.52248/240.3846ms) match `GetTiming` exactly for all three. VIS codes parity-stripped as established (0xb7→55, 0x3f→63, 0xbb→59) — legacy's own source comments (`// $37`, `// $3f`, `// $3b`) spell out the stripped values directly, a nice confirmation of the parity-stripping convention itself. All 6 new/updated round-trip and duration tests pass; 84/84 total in `ScanlineStudio.Core.Sstv.Tests`.
  - **Correction**: a prior note here claimed legacy has a per-mode-speed adaptive PLL "demod profile" system named `PRODEM`, to be ported so Martin M2/Scottie S2 could drop the 44100Hz stand-in. That name does not exist anywhere in `yoniq-old/YONIQ-main/` (checked with a case-insensitive grep across the whole tree) — it was an unverified guess written down as if it were confirmed source fact, exactly the failure CLAUDE.md's no-assumptions rule exists to catch. Directly verified instead: `CPLL`'s only tuning toggle is `SetWidth(fNarrow)`, switching between Normal (1500-2300Hz) and Narrow (`NARROW_LOW`=2044/`NARROW_HIGH`=2300, `sstv.h`) — exactly the MN/MC case already handled by `LuminanceMinHz`/`MaxHz`. Legacy decodes Martin M2 and Scottie S2 with the *same* fixed loopFC=1500/outFC=900 `CPLL` tuning, at the *same* 11025Hz default (`Main.cpp:577`), as every other mode — there is no per-speed adaptation to port. See `SstvModeRegistry`'s doc comment for the corrected version.
  - **M2/S2 gap investigated and root-caused** (no code change yet — this confirms what the real fix has to be before attempting it). Exhaustively checked whether legacy has *any* per-mode-adaptive discriminator setting: `CSSTVDEM::m_Type` picks between three demod algorithms (`CPLL`/PLL, `CFQC`/zero-crossing, `CHILL`/Hilbert, `sstv.cpp:2255-2265`) and `m_bpf` picks a bandpass filter width (Wide/Narrow/VeryNarrow, `DEMBPF` ini key) — both are **global user settings** (`Main.cpp:1855`), not chosen per mode. The only mode-driven toggle anywhere in `CSSTVDEM`/`CPLL`/`CFQC` is the Normal/Narrow frequency-range split already covered by `LuminanceMinHz`/`MaxHz`. So legacy really does decode every mode, including Martin M2 and Scottie S2, through identical fixed-tuning code at its 11025Hz default — confirming there is nothing left to "port" for this.
    - Measured the actual failure directly instead of theorizing: ran this port's own round-trip suite at 11025Hz (temporarily, then reverted — not a real change) across every RGB/YCbCr-sequential mode and logged each mode's minimum per-pixel scan duration converted to samples-at-11025Hz. Result was a clean, near-monotonic correlation, not the M2/S2-only story the old note assumed: **every mode with fewer than ~4 samples/pixel at 11025Hz fails** the 10.0-average-per-channel-delta tolerance (Robot36 1.52 samp/px → 21.3 delta; ML180 1.52 → 24.5; Robot72/MR73/ML280 ~2.4 → 15-20; Martin M2 2.52 → 14.3; ML320 2.73 → 15.9; Scottie S2 3.03 → 10.9; MR115 3.79 → 10.2), while every mode at ≥4.6 samples/px passes comfortably (Martin M1 5.05, Scottie S1 4.76, MR140 4.63, MR175 5.81, Scottie DX 11.91). So the 44100Hz stand-in isn't specifically an "M2/S2 problem" — it's masking a general resolution floor around ~4 samples/pixel that affects roughly a third of the mode table (confirmed list above).
    - Root cause, given legacy has no adaptive tuning to point to instead: this port's own `AnalogFmSstvDecoder`/`AverageFrequencyInWindow` reconstructs each pixel by block-averaging the continuously-demodulated frequency stream over a fixed nominal-timing window (with a discard-first-quarter settling margin) — a fundamentally coarser sampling strategy than legacy's real per-line sync-locked pixel timing, and it simply needs more raw samples per pixel to average out PLL/IIR settling noise than legacy's approach does. This is not a new, separate bug to patch in isolation — it's a direct symptom of the already-flagged, still-unported `CSSTVDEM` sync-search/AFC pipeline (see `AnalogFmSstvDecoder`'s own doc comment, and [[06-sstv-dsp]]): replacing block-averaging with real per-line sync-locked sampling is expected to fix this class of failure at 11025Hz without needing any new per-mode tuning table. Flagged as the next real piece of DSP work, not started without explicit direction, since it's a substantial addition (sync search + AFC state machine), not a small patch.
    - **Resolved, stale as of the entries below.** This bullet's "still-unported CSSTVDEM sync-search/AFC pipeline" framing is superseded — see the `CSSTVDEM investigation, reframed` entry immediately below: AFC (`AfcTracker`) and Auto Slant (`SlantTracker`) are both ported, and the actual root cause of the residual gap turned out to be a missing per-image sync-anchor re-correction (legacy's `SyncSSTV`/`m_wBgn` fold-and-argmax), not a missing sync-search/AFC state machine — ported as "piece 8" (`SyncAnchorCorrector`, commits `23b025a`→`8f87146`), measured against real golden-vector legacy audio: Robot 36 68.06→9.95, Martin M1 11.78→2.40. Left in place rather than deleted so the reasoning trail stays intact (CLAUDE.md's own precedent for this file).
  - **Independent Opus-driven adversarial verification pass, then fixes** (user-requested second opinion, specifically scoped to cross-check every mode's TX *and* RX against the actual legacy source rather than just review code quality — the right scope, since a generic review can't catch "self-consistent but wrong," the exact failure mode that bit Scottie earlier in this phase). 14 mode families / 43 modes examined; 11 fully confirmed correct (including every specific thing this phase already traced carefully: Scottie's mid-line sync *placement*, R24's double-increment, RM8/RM12's monochrome averaging, SC2's gain-tag red herring, MN/MC's FSK packet). 3 families had real, previously-undetected bugs, all independently re-verified against source (not just taken on the agent's word) before fixing:
    - **Chroma frequencies shifted ~400Hz low — highest severity, 6 families / 24 modes** (Robot36/72, R24, MR/ML, MP, PD, MN). `YCbCr.FromRgb` omitted legacy `GetRY`'s +128 R-Y/B-Y offset (`ComLib.cpp:3663-3665`); `YCbCr.ToRgb` was symmetrically missing the same offset, so round-trip tests passed while both sides disagreed with real legacy TX/RX — the effect: a saturated color could push chroma to ~1150Hz, colliding with the 1200Hz sync tone. The port's old doc comment called this "presumably absorbed somewhere in legacy's RX calibration layer... a real gap in understanding" — that premise was wrong, not just incomplete: independently confirmed the actual mechanism is `Main.cpp:4331` (luminance path adds +128 back) vs `4341`/`4350`/`4396`/`4405` (chroma path doesn't), reconciled by the demod calibration defaults `m_DemOff=0`, `m_DemWhite=m_DemBlack=128/16384` (`Main.cpp:875-877`) already zero-centering chroma at 1900Hz before `YCtoRGB` sees it. Fixed: `FromRgb` now adds +128 (matching `GetRY` exactly); `ToRgb` now subtracts 128 before its reconstruction matrix (folding in what legacy's separate `GetPixelLevel` calibration layer would otherwise do, since this port doesn't model that layer). `MonoAveragedPairedScanlineDecoder`'s "no chroma" placeholder updated from `ToRgb(y, 0, 0)` to `ToRgb(y, 128, 128)` to match the new centered convention. New targeted tests (`YCbCrTests.cs`, not just round-trip) assert `FromRgb(128,128,128)` produces chroma of exactly 128, since round-trip alone can't catch a symmetric offset bug.
    - **Scottie S1/S2/DX missing a post-VIS pulse.** Legacy emits an extra 9ms/1200Hz pulse right after the VIS header for Scottie only (`Main.cpp:7576-7578`), because legacy's RX VIS-decode state machine (`sstv.cpp:2127-2153`) expects 1200Hz still present ~30ms after the last VIS bit, and Scottie's line generator starts directly on a 1500Hz separator with no sync of its own — confirmed by reading both cited functions directly. Fixed: `VisHeader.ScottiePostVisPulseFrequencyHz`/`DurationMs` + `AnalogFmSstvEncoder` emits it for Scottie only; `AnalogFmSstvDecoder.TryDecodeVisHeader` skips it before line data. New test (`VisHeaderTests.ScottiePostVisPulse_MatchesLegacyConstants`) pins the constants.
    - **AVT missing its entire preamble.** Legacy sends the VIS block 3x for AVT specifically (`Main.cpp:7429`), then a ~5.3s sync/AFC training sequence (`Main.cpp:7563-7575`: 32 blocks of a 1900Hz marker + 16 bits at 1600/2200Hz encoding a shifting counter seeded at `0x5fa0`) before any line data — legacy's own RX explicitly budgets for this exact length (`sstv.cpp:2140`). The port previously sent a single normal VIS header and nothing else. Fixed: `VisHeader.GenerateAvtSegments` ports the training sequence's bit-shift arithmetic exactly; the encoder dispatches to it for AVT; the decoder identifies the mode from the first VIS repeat then skips `AvtExtraHeaderDurationMs` (two more VIS repeats + the training sequence) before line data. New test (`VisHeaderTests.GenerateAvtSegments_TotalHeaderDuration_MatchesLegacy`) pins the total header duration to 8042.24754375ms, independently re-derived from the cited legacy constants.
    - **RM12's VIS byte, medium severity.** Legacy's real byte for RM12 is `0x86` (`sstv.cpp:1997`), but its parity bit doesn't satisfy even parity computed from its 7 data bits (6 = `0b0000110`, already even → computed parity gives `0x06`) — independently re-checked every other normal VIS byte in the legacy table bit-by-bit, and all of them do follow even parity, so this is a one-off quirk in legacy's own assigned byte, not a bug in this port's original parity-stripping logic (which the earlier doc comment incorrectly over-generalized as "both RM8 and RM12 bake even parity into their MSB"). Fixed: `VisHeader.GenerateSegments` gained an optional `forcedParityBit` parameter; RM12 uses it (`VisHeader.Rm12ForcedParityBit = 1`) to transmit the real `0x86` instead of a computed `0x06`. Decode is unaffected either way, since `DecodeVisCode` never reads the parity bit for any mode. New test (`VisHeaderTests.GenerateSegments_Rm12ForcedParity_TransmitsLegacyByte0x86`) decodes the emitted bit sequence back to a byte and asserts it.
    - **Minor, also fixed**: `RgbSequentialScanlineEncoder`/`Decoder` divided by 255 instead of 256 (legacy's `ColorToFreq`/its inverse both use 256, matching every other codec family here already) — up to ~4Hz off at full scale, affecting Martin/Scottie/AVT/Pasokon/SC2/MC.
    - Added `[assembly: InternalsVisibleTo("ScanlineStudio.Core.Sstv.Tests")]` (`AssemblyInfo.cs`) so these fixes could get targeted reference-value unit tests against `VisHeader`/`YCbCr` directly, rather than only through the public round-trip pipeline — necessary because round-trip self-consistency is exactly what let three of these four bugs go undetected in the first place. Full suite after fixes: 98/98 in `ScanlineStudio.Core.Sstv.Tests` (87 previous + 11 new targeted tests), solution-wide build clean.
    - **Doc-only cleanup, user-directed** ("we follow the legacy code, not public documents... if the docs say grass is green and the legacy says it's red, the grass is red"): the first review's Finding C flagged two stale/false doc comments that had never been actioned. `ToneSelectorSegment`'s comment claimed Robot's 1500/2300Hz tone-selector frequencies were "standards-informed, not traced from legacy source... no source Hz constant to read directly" — false; `Main.cpp:6568` has the literal value (`mp->Write(short(mp->m_wLine & 1 ? 2300 : 1500), 4.5)`), and this port's earlier author checked the RX decode path instead of the TX line-generator function, the exact anti-pattern CLAUDE.md's TX/RX-split rule warns about. `SstvModeDefinition`'s class comment claimed its constants were "taken from public SSTV protocol documentation, not from the legacy MMSSTV/YONIQ binary directly" — false, and directly contradicted `SstvModeRegistry`'s own accurate claim on the same data; corrected to state plainly that every constant is read from legacy source, with the real remaining caveat (no golden-vector cross-check against actual captured legacy binary *output*, as opposed to its source code) stated precisely instead of overstated. Also fixed the citation slips independent verification found: `sstv.cpp:2853`→`2868` and `2879`→`2880` (SC2's TX-gain-tag mask/switch), `Main.cpp:6554`→`6555` (R24's own `m_wLine++`), `CSSTVDEM::SetSampFreq`→`CSSTVSET::SetSampFreq` (R24's decode-side `m_L=120`) — all re-verified against source directly before fixing (confirmed exact line numbers via `awk`/`LC_ALL=C`, not just copied from the review's report).
    - **Second independent Opus verification pass, then one more fix.** Re-checked all 5 fixes against legacy source directly (not the fix's own doc comments, not just "tests still pass"). 4 of 5 confirmed fully correct outright: the chroma +128 fix matches `GetRY`/`YCtoRGB` exactly and all four `ToRgb` call sites are in the right domain; the Scottie pulse is emitted for exactly S1/S2/DX in the right position; the AVT preamble's bit-shift arithmetic is byte-identical to legacy's and the total duration matches exactly; RM12's parity anomaly was independently re-derived (checked set-bit counts across all 23 other normal VIS bytes bit by bit — 0x86 is confirmed the sole exception) and is now transmitted literally; the /256 divisor fix is symmetric and correct. But it caught a **new latent bug introduced by the AVT/Scottie fix itself**: `AnalogFmSstvDecoder.TryDecodeVisHeader` advanced `_consumedSamples` past the base VIS header as soon as that much was available, then separately checked whether the mode-dependent "extra" material (AVT's training tail / Scottie's pulse) had arrived — so a caller pushing samples in chunks (not all at once) could see the base-header advance commit, then hit "not enough samples yet" for the extra part and return with `_mode` still null, causing the next chunk to restart header detection from the *middle* of the remaining preamble. Invisible to every existing test, since all of them push the entire waveform in one `PushSamples` call. Fixed by making the whole header (base VIS + any AVT/Scottie extra) a single atomic commit — one availability check covering the full amount, one assignment to `_consumedSamples`/`_mode`, no partial state exposed in between. Verified this was a real, reproducible bug (not a false positive) by temporarily reintroducing the two-stage version, confirming the new `ModeWithMultiPartHeader_IsStillDetected_WhenSamplesArriveInChunks` test fails against it, then restoring the fix and confirming it passes again. Full suite after this fix: 100/100 in `ScanlineStudio.Core.Sstv.Tests`, solution-wide build clean.
  - **Robot36's tone-selector ambiguity fallback — fixed** (a low-severity finding from the first Opus review that had never been actioned). Legacy doesn't just threshold the tone-selector reading against the 1900Hz midpoint: `Main.cpp:4289-4296` only decides decisively when the reading is at least ~200Hz from center (derived from `GetPixelLevel`'s `d>=64`/`d<-64` check and its `m_DemWhite=m_DemBlack=128/16384` scale factor — 200Hz isn't a literal source constant, it's this derived-and-flagged-as-such number); inside that band it *toggles* from the previous line's selection instead of picking a side, since Robot36 alternates R-Y/B-Y every line and a toggle is a better noise-robust guess than either fixed default. This port previously just thresholded, with no ambiguity handling at all. Fixed in `RobotScanlineDecoder` (`AmbiguityHalfWidthHz` + `_lastSelectionIsEvenLine`, defaulting to legacy's own `m_DSEL=0` default). Round-trip tests can't exercise this (the encoder always transmits a decisive tone), so verified with a dedicated `RobotScanlineDecoderTests` test that drives the decoder directly with a scripted ambiguous reading — confirmed meaningful by temporarily reverting to the old threshold-only logic and checking the new test actually fails against it before restoring the fix.
  - **Post-image footer tone — done.** Investigated the FSK/CW station-ID item next up per the roadmap, found it was much larger than a "smaller item" (TX + RX + a wholly new CW/Morse subsystem + settings plumbing — see [[06-sstv-dsp]]'s Station ID section for the full breakdown) and confirmed with the user before proceeding: CW ID specifically is real legacy functionality but not required for the program to work as an SSTV encoder/decoder, so the whole station-ID feature is deferred, written up as a scoped task in [[06-sstv-dsp]] rather than silently dropped, to pick up later. The one piece of that area that *isn't* station-ID-specific and was small enough to do now: legacy always appends a footer immediately after the last image line whether or not FSK ID is configured (`Main.cpp:6994-7013`) — a trailing-carrier hold (`min(one line's duration, 500ms)`) at 1500Hz followed by an alternating 1900/1500/1900/1500Hz sequence (4×100ms) for normal modes, or just the trailing carrier alone at 1900Hz for narrow (MN/MC) modes. Implemented as `AnalogFmSstvEncoder.GenerateFooterSegments`, built specifically so it won't need rework once FSK/CW ID exists (the still-deferred "FSK ID configured" branch is a separate, additive case, not a replacement of this one). `sys.m_VOX` (also part of the branch condition) isn't modeled anywhere in this port yet (no radio/PTT layer exists) — defaults to legacy's own off-by-default behavior, flagged rather than silently baked in as permanent. Verified with dedicated tests (`AnalogFmSstvEncoderFooterTests`) checking the exact segment sequence and the 500ms cap for both branches, not just full-suite round-trip pass-through. Full suite after both fixes: 104/104 in `ScanlineStudio.Core.Sstv.Tests`, solution-wide build clean.
  - **`CSSTVDEM` investigation, reframed then partially fixed.** Went to start the "port the real sync-search/AFC pipeline" task above and found the premise was wrong before writing any code: legacy's per-pixel addressing (`Main.cpp:4144-4148`, `y = n/m_TW`, `ps = fmod(n, m_TW)`) is the *same* nominal-timing arithmetic this port already uses — there's no rediscovered per-line sync point during image reception. The real difference is narrower: legacy processes the recorded audio one raw sample at a time and, for each pixel, keeps whichever single sample is first at that pixel's computed index (`GetPictureLevel`/`GetPixelLevel` both simply dereference `*ip` — no window, no average). This port instead block-averaged the whole per-pixel dwell window (discarding the first quarter as a settling margin) — an invented technique, not traced from source. User's direction: "legacy is truth, follow that." Fixed: `IScanlineDecoder.DecodeLine`'s callback (renamed `averageFrequencyInWindow`→`sampleFrequencyAt` across the interface, `AnalogFmSstvDecoder`, and all 5 scanline decoders — an honest-naming fix, not just internals, since the old name was actively wrong about what it now does) reads a single sample at the pixel's window-start instead of averaging. Also caught and fixed in the same pass, once the "single sample" pattern existed: Robot's tone-selector re-decides on *every* sample of its window with no "first wins" gate (`Main.cpp:4286-4297`), so its real effective reading is the window's *last* sample, not an average and not the first sample either — `RobotScanlineDecoder` now reads `(endSample-1, endSample)` for that one case.
    - **Measured, not assumed, how much this actually helps**: re-ran the same 11025Hz experiment from the earlier investigation above. Real, consistent improvement across nearly every previously-failing mode (Robot36 21.3→13.4 delta, Robot72 15.2→13.4, MR73 20.2→16.1, ML180 24.5→19.6, ML240 18.8→15.1, ML280 18.0→14.6, ML320 15.9→12.9, Scottie S2 10.9→10.2), and MR115 now crosses the passing threshold outright (10.17→passes). One mode (Martin M2) moved slightly the wrong way (14.28→14.49, within noise). **This confirms the fix is real and correctly targeted — legacy's actual mechanism, not a guess — but it does not fully close the gap alone**: several modes still fail the 10.0-delta tolerance at 11025Hz. Full closure likely needs either the AFC drift-tracking loop (`SyncFreq`/`m_AFCDiff`, still unported) or a closer look at whether this port's `PllFmDemodulator` settles as fast as legacy's exact filter chain at very short pixel-dwell times — kept as further, separately-scoped follow-up work, not blindly attempted in the same pass. The 44100Hz stand-in remains in the test suite for now; it is a pragmatic Phase 1 accommodation, not a hidden correctness bug, given this finding.
    - Existing full suite (44100Hz) unaffected: 104/104 in `ScanlineStudio.Core.Sstv.Tests`, solution-wide build clean, since single-sample-vs-window-average makes no measurable difference once there are enough samples per pixel to begin with.
    - The genuinely large piece originally feared for this task — the VIS/preamble lock state machine (`m_SyncMode` 0-8/256/512-513, `sstv.cpp:2085-2270`) — was, at the time this entry was written, unstarted and understood to be a separate, still-large concern from the pixel-readout fix above (it governs *when* image reception starts, not how pixels are sampled once it has). **Superseded**: broken into 7 pieces and completed — see this same section's later "VIS/preamble lock state machine — ... Complete" entry below for the full history (`VisLockStateMachine.cs`).
  - **AFC (`CSSTVDEM::SyncFreq`) — ported.** User's explicit direction: "legacy is truth, verify before accepting." Read `SyncFreq` (`sstv.cpp:2339-2376`) fully: it feeds a *separate* zero-crossing frequency discriminator (`CFQC`, `sstv.cpp:347-489` — entirely distinct from the PLL-based main demodulator this port already has) with the same raw audio sample the PLL sees, watches for a run of consecutive samples (`m_AFCB`..`m_AFCE`, mode-dependent: 1.0/2.0ms for Martin M1/M2+SC2+MC, 1.5/3.0ms for everyone else, `sstv.cpp:1162-1177`) that plausibly reads as the mode's sync tone (1200Hz normal / 1900Hz `NARROW_SYNC` for MN/MC, each with its own acceptance band and a 15-lock-event long-term moving average, `CSmooz`), and once locked, computes a persistent correction (`m_AFCDiff`) added to every subsequently demodulated sample. Explicitly excluded for AVT in legacy (`sstv.cpp:2258`'s `mode != smAVT` guard) — ported the same exclusion. Traced the internal x16384/BWH-scaled arithmetic all the way through and confirmed (not assumed) that, since `IirFilter.Process` is a purely linear biquad cascade, working entirely in real Hz throughout is mathematically identical to legacy's scaled representation — a representational simplification, not a numeric approximation.
    - New files: `MovingAverage` (`CSmooz` port), `ZeroCrossingFrequencyCounter` (`CFQC` port), `AfcTracker` (`SyncFreq`'s state machine). Wired into `AnalogFmSstvDecoder`: since this decoder demodulates the whole sample buffer upfront before mode detection can know whether/how AFC applies (unlike legacy's single real-time pass), AFC runs as a second pass over the *raw* samples (now also retained, `_rawSamples`) once the mode is known, correcting the already-demodulated buffer in place — a deferred, not approximated, adaptation: the zero-crossing counter and AFC state machine still see the exact same raw samples in the exact same order legacy's own would have, only the wall-clock timing of when that processing happens differs.
    - **Verified against legacy's own formulas, not just "tests pass."** A driftless same-process round-trip has no real carrier offset for AFC to correct, so `SstvRoundTripTests` passing/failing can't validate this feature either way — confirmed empirically: re-ran the 11025Hz experiment with AFC wired in and got the same pass/fail pattern as before it (9 failures, same modes, deltas within ~0.3 of their pre-AFC values), exactly the expected outcome for a feature that corrects drift no synthetic test has. Instead added `AfcTests` — direct, source-derived tests feeding synthetic tones (with and without a deliberate frequency offset) straight into `ZeroCrossingFrequencyCounter`/`AfcTracker`, checking the locked correction against hand-derived expected values to tight (0.01Hz) tolerance. Caught a real test-authoring mistake this way, not an implementation bug: `SyncFreq`'s `d -= 128` (a small fixed calibration nudge, ~3.125Hz for normal-bandwidth modes) means even a perfectly on-frequency reading locks a small nonzero correction, not exactly 0 — the first draft of these tests assumed otherwise and failed against the (correct) implementation until the expected values were re-derived by hand from the same formula.
    - Full suite: 110/110 in `ScanlineStudio.Core.Sstv.Tests` (104 previous + 6 new AFC tests), solution-wide build clean, no regressions at either 44100Hz or 11025Hz.
  - **Slant correction ("Auto Slant") — reclassified, investigated, then ported.** Initially assumed to be UI (legacy's `m_Slant`/`GetSyncSamp`/`ClockAdj.cpp` are genuinely manual, mouse-driven calibration tools) — an independent Opus verification pass caught that this was incomplete: there's also a fully automatic, per-line, on-by-default (`Mmsstv.ini`'s `AutoSlant=1`) mechanism, `AutoStopJob`'s `KRSA->Checked` branch, that legacy bundles together with two unrelated features (Auto Stop, Auto Sync) in one function. Per user direction, only the actual slant/clock-drift piece was ported; Auto Stop and Auto Sync were left out as separate concerns, and the manual UI tools were noted for later UI-phase work, not silently dropped.
    - Full algorithm traced and verified against source before writing any code: a fixed 1200Hz (1900Hz for MN/MC, matching `d19`'s use over `d12`'s once `NARROW_SYNC`'s real value, 1900 not 1200, was checked) resonate-rectify-smooth envelope detector (new `CIIRTANK` port, `TankFilter`) finds where the sync pulse's signal strength peaks within each line; a 5-point least-squares fit (`GetSqerrPos`) smooths that against a 16-line rolling history; a staged, mode-dependent confidence-threshold ladder (`m_ASLmt`/`m_ASPos`, both fully transcribed and independently tested per mode group) decides when a drift correction is trustworthy enough to commit. New files: `TankFilter`, `SyncEnvelopeDetector`, `SlantTracker`, plus `SstvModeRegistry.GetAutoSlantThresholdPositions`/`GetSyncSegmentOffsetMs` (the latter a deliberate substitution for legacy's separate `m_OFP` table — derived from this port's own already-verified `LineSegments` data instead of re-transcribing a second per-mode table, justified because the value is only ever used as a fixed reference point that cancels out of the drift math, not reproduced for its own sake).
    - **User-directed methodology change mid-implementation**: after building `TankFilter`/`SyncEnvelopeDetector`/`SlantTracker`/the mode-grouping helpers together and only then wiring + testing them as one block, user redirected: chop complex work into smaller, independently-verified parts so bugs are easier to find. Restarted verification from there: each new class got its own isolated, source-derived unit test (resonator peaking at its tuned frequency, envelope detector responding more to on-frequency tones, `SlantTracker` reporting no correction for zero drift and a mathematically-precise correction for a known synthetic drift sequence, the mode-grouping and sync-offset helpers checked mode-by-mode) *before* any wiring into `AnalogFmSstvDecoder` — all passing in isolation.
    - **Then a real end-to-end integration test caught two genuine bugs the isolated tests structurally could not.** Built a test that encodes at a deliberately different "true" sample rate than the decoder is told (simulating a real clock mismatch) and checks decode accuracy — the isolated unit tests, by construction, never exercised more than one correction in sequence or the actual `AnalogFmSstvDecoder` wiring, so neither could have caught either bug:
      1. **`SlantTracker` internal desync.** Legacy's drift formula uses `SSTVSET.m_SampFreq`/`SSTVSET.m_TW` — both the *evolving*, already-corrected values, kept in sync by `SetSampFreq()` recomputing `m_TW` from `m_SampFreq` after every commit — not a fixed hardware rate. This port's first version used a permanently-fixed rate in the numerator and never updated `_nominalSamplesPerLine` after the first correction, so the formula silently drifted out of the proportional relationship legacy maintains, corrupting every correction after the first (measured: converged to ~6645 instead of the true ~6681 samples/line). Fixed by adding mutable, synchronized `_currentSampleRate`/`_nominalSamplesPerLine` fields updated together after every commit, mirroring `SetSampFreq()` exactly. Re-verified with a corrected *closed-loop* isolated test (the original one fed a fixed linear drift sequence decoupled from the tracker's own corrections — a fair model of the old, buggy always-fixed-nominal behavior, but not of the fixed version's self-referential feedback loop).
      2. **Wiring ran slant-tracking as a bulk pass ahead of pixel decode.** The decoder demodulates whole pushed batches upfront; the first version of the wiring ran `ApplySlantTracking` once per batch over *all* currently-buffered raw samples before the per-line decode loop even started. For a caller pushing the whole waveform in one call (every test in this suite), that meant slant-tracking ran straight through all 240 image lines *and into the trailing footer tone* before a single pixel was decoded — so pixel decode ended up using one final (partly footer-corrupted) rate for the entire image instead of a progressively-corrected one. Fixed by interleaving: decode each line with whatever rate stood *before* it (matching legacy's real causal order — a line is decoded with the current `m_TW`, then its own sync data feeds `AutoStopJob`, which may update `m_TW` for the *next* line only), and bounding `ApplySlantTracking` to exactly the samples pixel decode has already consumed, never running ahead into not-yet-decoded or non-image content.
    - **Verified realistic behavior, and honestly characterized a real algorithmic limit.** With both fixes in place: a realistic clock mismatch (500ppm, already a pessimistic real crystal-oscillator tolerance) decodes within the normal 10.0-delta round-trip tolerance. A deliberately severe 1% mismatch does not fully correct — traced this to a real, verified characteristic of legacy's own algorithm, not a bug: `AutoStopJob`'s `m_ASBitMask` permanently disables each of its 5 confidence tiers once used, so for one long transmission it converges quickly early on and then locks rather than continuing to re-adjust, and for a mismatch this severe over 240 lines the residual (measured: ~9.5 samples/line after convergence) compounds to a visible error by the end. Documented as a legacy limitation faithfully reproduced, not something to invent a fix for, with a test that checks for meaningfully-better-than-uncorrected rather than a false claim of full correction.
    - Full suite after all fixes: 126/126 in `ScanlineStudio.Core.Sstv.Tests`, solution-wide build clean.
  - **VIS/preamble lock state machine — broken into 7 smaller pieces (see below), all 7 done and verified. Complete.** Investigated fully before touching code: legacy's real mechanism (`sstv.cpp`'s `CSSTVDEM::Do`, `m_SyncMode` 0-9/256/512-513) isn't one state machine, it's three parallel strategies sharing one utility class — `m_sint1` (checked first, no mode allowlist, but *not* independently scanning: it only ever gets new peak data from the same primary threshold that also drives the real VIS-bit-decode state machine forward, `sstv.cpp:1946-1972` — see piece 7's own corrected scoping below; an earlier note here mischaracterized it as "a simpler substitute this port already has," which undersold its real value: recognizing a repeating timing pattern even when a given attempt's VIS bits fail to decode cleanly), `m_sint2` (recognizes Scottie1/Martin1/Martin2/SC2-180 *without ever decoding a VIS code*, purely from their sync pulse's own repeating timing — a genuinely new capability, not a more-robust version of something already here), and `m_sint3` (the same idea for the MN/MC narrow family). Broken into 7 pieces (see this repo's own conversation history for the full list); picked the foundational one — `CSYNCINT` itself, the peak-interval-pattern utility all three strategies are built on — since the other six all depend on it and it's cleanly testable in isolation.
    - Ported as `SyncIntervalTracker`: tracks amplitude peaks over time, measures the interval between consecutive accepted peaks, checks whether that interval (or its 1/2, 1/3 subharmonic) matches any candidate mode's own line duration, and requires several *consecutive* matching intervals (a real, verified per-mode-group depth, `SyncCheckSub`, `sstv.cpp:1290-1325` — 5 different confidence groups, one of which, RM8/RM12, requires matching all the way back through the full 8-slot history, and one of which, SC2-60/120, is unconditionally excluded and never matches at all) before accepting. `SstvModeRegistry` gained two supporting helpers: `GetSyncIntervalCandidates` (derives each mode's expected interval from this port's own already-`GetTiming`-verified `LineDurationMs`, not a re-transcription of legacy's separate `m_MS[]` table) and `GetSyncIntervalMatchDepth` (the 5-group depth/band gating).
    - Verified with 7 isolated tests *before* any wiring exists, per the now-established "chop into independently-verifiable parts" methodology: periodic peaks matching a mode's interval eventually match; too few consecutive peaks never match; non-periodic (random) peaks never match; the subharmonic check recognizes a doubled interval (a missed-every-other-peak scenario); SC2-60/120 never match under any circumstances; a narrow-band tracker only ever matches narrow-family modes and never normal-band ones. Two test-authoring mistakes caught and fixed by these same tests, not implementation bugs: an early draft fed *half* a mode's interval expecting the subharmonic check to match it, backwards from the actual division direction (legacy divides the *measured* interval down to look for a *shorter* true interval, i.e. handles missed peaks making the apparent interval too long, not a faster-repeating mode); and a normal-vs-narrow band-isolation test picked Scottie S1 (428.22ms) as its "normal" mode, which happens to fall within 3ms of MC110's (428.5ms) narrow-family duration — a coincidental collision in the chosen test data, replaced with Robot36 (150ms, no collision).
    - **Second piece — `m_sint2` wired into `AnalogFmSstvDecoder`, ported and verified.** Read the exact `case 0` trigger structure first (`sstv.cpp:1899-1924`, confirmed `m_MSync` defaults on via `Main.cpp:1856`): `m_sint1.SyncStart()` tried first (unrelated, separately-scoped work, not touched here); else if `(d12>d19) && (d12>m_SLvl2) && ((d12-d19)>=m_SLvl2)` feed `m_sint2.SyncMax(d12)` (still inside a candidate peak); else call `m_sint2.SyncStart()`, and only act on a match if it's one of `smSCT1`/`smMRT1`/`smMRT2`/`smSC2_180` (`sstv.cpp:1912-1922`'s `switch`/`default: break;` — every other match `SyncCheck` could in principle return is deliberately ignored, so this port ignores them identically via a `SyncBypassTrustedModes` set, not a broader "recognize everything" implementation). Also confirmed via `sstv.h`/`sstv.cpp:1483` that `m_sint2.m_fNarrow` is never set (only `m_sint3`'s is) — `m_sint2` always runs `isNarrow: false`.
      - Two absolute-amplitude thresholds in the real condition (`d12 > m_SLvl2`, `(d12-d19) >= m_SLvl2`) are on legacy's internal AGC'd ±16384 scale this port doesn't model — omitted, using only the relative `d12>d19` comparison, the same documented simplification already applied to `AfcTracker`'s and `SyncEnvelopeDetector`'s own squelch gates.
      - `SyncIntervalTracker` gained one new member, `LastPeakPositionSamples`: legacy never needs to expose this (`Start()` just begins decoding from "now" in a continuous real-time loop) but this port's batch decoder needs an explicit anchor point once a match commits. Turning that peak position into a line-start sample index needed a new `SstvModeRegistry.GetSyncSegmentMidpointOffsetMs` helper (offset to the *midpoint*, not start, of the sync segment — `SyncEnvelopeDetector`'s peak naturally lands near the segment's temporal center) — explicitly documented as a port-specific approximation, not a ported legacy mechanism, since legacy's real fine-alignment bootstrap (`CSSTVDEM::Start`'s `m_wBgn` staged buffer search, `sstv.cpp:1717-1744`) is a separate, deeper piece of machinery not ported here.
      - **Real bug caught by the end-to-end test, not the isolated `SyncIntervalTracker` tests**: the first wiring tried sync-bypass detection *before* the VIS/narrow header path on every `TryDecodeHeader` call. Harmless in real continuous real-time use (sync-bypass needs several lines' worth of consistent peaks, so it never actually beats VIS decode's ~610ms in practice) but wrong for this port's batch architecture: a single bulk `PushSamples` call (every test in this suite) hands the whole future stream to sync-bypass detection at once, so it happily scanned deep into real image data and falsely fired on all four trusted modes even when a perfectly valid VIS header was present — regressing all four of `SstvRoundTripTests`' existing round-trip cases for those modes (confirmed: the failure was exactly those four, nothing else). Fixed by trying header-decode first and falling back to sync-bypass only if it fails — which reproduces the real race's actual outcome (header wins whenever a valid one exists) instead of giving the batch-only detector an unrealistic look-ahead advantage; this is the same category of bulk-vs-streaming ordering bug as `ApplySlantTracking`'s earlier fix. All 133 pre-existing tests pass again after the reorder.
      - **New end-to-end test** (`SyncBypassDetectionTests`, 4 cases — one per trusted mode): encodes normally, then strips exactly the VIS header's own sample count (mirroring `TryDecodeVisHeader`'s own duration arithmetic) before feeding the decoder, so it truly has no header to find and can only recognize the mode via sync-interval matching. Mode identification is exact for all four (`Assert.Equal(mode.Id, detectedMode.Id)` passes unconditionally) — confirming the actual ported capability works. Pixel-level alignment is looser than the header path's (measured average per-channel delta: Scottie S1 18.06, Martin M1 12.62, Martin M2 20.78, SC2-180 12.91, vs. the header path's <10.0), a real and reproducible (not random) consequence of the documented peak-position approximation above, not a mode-detection bug — tolerance set to 25.0 with the reasoning and measured numbers recorded directly in the test.
    - Full suite after this piece: 137/137 in `ScanlineStudio.Core.Sstv.Tests` (133 + 4 new), solution-wide build clean.
    - **Third piece — `m_sint3` (narrow MN/MC family equivalent of `m_sint2`) wired into `AnalogFmSstvDecoder`, ported and verified.** Read the exact trigger structure first (`sstv.cpp:1924-1946`, the `#if NARROW_SYNC == 1900` block immediately after `m_sint2`'s in the same `case 0` branch, confirmed compiled since `NARROW_SYNC` is actually `1900` per `sstv.h:440`), plus `m_sint3.m_fNarrow = TRUE` (`sstv.cpp:1483`, the only one of the three `CSYNCINT` instances ever set narrow) and `CSYNCINT`'s shared class shape (`sstv.h:561-590`, `SyncTrig`/`SyncMax`/`m_SyncPhase`). Unlike `m_sint2`'s condition (a simple `d12>d19` comparison, `SyncMax` while true / `SyncStart` every sample once false), `m_sint3`'s real condition is `(d19>d12) && (d19>dsp) && ...`, where `dsp` is a *third*, previously-unused envelope — `m_iirfsk`/`m_lpffsk` (`sstv.cpp:1450/1455`), a 100Hz-bandwidth resonator at `FSKSPACE`=2100Hz followed by the same 50Hz/2nd-order lowpass smoother every other sync-tone envelope in this port already uses — confirmed by direct comparison against `SyncEnvelopeDetector`'s own doc comment that this is the exact same shape, just a different center frequency, so no new production class was needed: `new SyncEnvelopeDetector(sampleRate, VisHeader.NarrowSpaceFrequencyHz)` reuses it directly. This `dsp` check exists to reject the narrow sync trigger when it's actually the FSK-ID space tone (or, as it turns out, the narrow mode-announce packet's own 2100Hz guard tone, `VisHeader.NarrowSpaceFrequencyHz` — the same constant, confirmed the same real 2100Hz value both places) sitting close in frequency to the 1900Hz narrow sync tone — a relative comparison, so (per the same reasoning already applied to `m_sint2`, AFC, and Slant) portable without needing legacy's AGC'd absolute scale, and ported rather than omitted since the user explicitly asked for it this piece.
      - `m_sint3`'s calling-code shape also genuinely differs from `m_sint2`'s: legacy explicitly latches `SyncTrig` (unconditional, on first entry into the candidate band) then `SyncMax` (running max while still inside it) via an explicit `m_SyncPhase` gate, and calls `SyncStart()` exactly once, on the falling edge — rather than collapsing this to the (provably equivalent, since `SyncStart` self-gates on its own internal peak state once consumed) simpler shape `m_sint2` already uses, it was ported as a literal structural match, consistent with CLAUDE.md's port-first principle.
      - Confirmed via `sstv.cpp:1937-1941` that, unlike `m_sint2`'s explicit trusted-mode `switch`/`default: break;`, `m_sint3`'s branch acts on whatever `SyncStart()` returns with no further filter at all — safe because `SyncCheckSub`'s own `m_fNarrow` gating (already generically ported as `GetSyncIntervalMatchDepth(mode, isNarrow: true)`, verified back in the `CSYNCINT` piece) already restricts every possible match to exactly the 6 real narrow modes. So this port adds no `SyncBypassTrustedModes`-style allowlist for `m_sint3` — deliberately, not an oversight.
      - Implementation-level finding, not a legacy-behavior one: since legacy computes `d12`/`d19` exactly once per real-time `Do()` call and both `m_sint2`'s and `m_sint3`'s independent per-sample checks read those same values, `m_sint3` was wired into the *same* `TrySyncIntervalDetection` loop `m_sint2` already had (plus the one new `dsp` envelope) rather than a second separate pass over `_rawSamples` — matches legacy's real single-pass structure and avoids recomputing the same resonator/lowpass filter state twice for no reason.
      - `SstvModeRegistry`'s existing `GetSyncIntervalCandidates`/`GetSyncIntervalMatchDepth`/`GetSyncSegmentMidpointOffsetMs` needed zero changes — all three were already written generically over every mode (narrow included) back in the `CSYNCINT`/`m_sint2` pieces, confirmed by reading each one again before assuming so rather than taking the earlier doc comments' word for it.
      - No new isolated unit tests were added for `SyncIntervalTracker`'s `Trigger`/`UpdateMax`/`TryStart` themselves (already covered generically, including the narrow-band case, by `SyncIntervalTrackerTests`) or for the reused `SyncEnvelopeDetector` (already generic and exercised end-to-end via AFC/Slant) — the only genuinely new behavior this piece adds is the `m_SyncPhase`-style calling sequence and the merged-loop wiring, both of which only make sense to verify at the decoder level.
      - **New end-to-end test** (`SyncBypassNarrowDetectionTests`, 6 cases — one per real narrow mode): encodes normally, then strips exactly the narrow mode-announce packet's own duration (`VisHeader.NarrowHeaderTotalDurationMs` — MN/MC modes never have a VIS header to strip in the first place, per the earlier MN/MC piece's finding), so the decoder can only recognize the mode via sync-interval matching. Mode identification is exact for all 6. Measured average per-channel deltas: MN73 24.27, MN110 21.17, MN140 19.62, MC110 16.38, MC140 14.29, MC180 11.42 — real and reproducible (same peak-position-approximation cost already documented for `m_sint2`'s own trusted modes), comfortably inside the same 25.0 tolerance already established for that path.
      - Full suite after this piece: 143/143 in `ScanlineStudio.Core.Sstv.Tests` (137 + 6 new), solution-wide build clean.
    - **Fourth piece — VIS-bit-decode-via-PLL-tone-counting (`m_SyncMode` cases 0(trigger)/1/2/9/3, `sstv.cpp:1946-2154`), ported and wired.** Scoped in two passes before writing code, per this session's established practice: an initial read of the whole `m_SyncMode` switch, then an independent Opus-driven re-verification of every claim directly against source (not taken on the agent's word — several of its own claims were spot-checked again before trusting them, e.g. `FindByFullVisByte`'s parity formula was hand-verified against 5 real legacy bytes before writing any table-driven test). Two real corrections came out of that: the tone-race resonators (`m_iir11`/`m_iir13`, 1080/1320Hz) use an **80Hz** bandwidth (`sstv.cpp:1446/1448`), not the 100Hz every other `SyncEnvelopeDetector` instance uses; and legacy's mode-code table (`sstv.cpp:1993-2074`) matches the **full 8-bit byte** (7 data bits + even-parity bit as MSB), not the parity-stripped 7-bit value this port's `VisCode` field stores — naively masking `&0x7F` before calling the existing `FindByVisCode` would be more permissive than legacy, which rejects a byte with the wrong parity bit outright (`default: m_SyncMode=0`).
      - Added as a **third fallback** (fixed-window header decode → `VisLockStateMachine` → sync-interval bypass), not a replacement of `TryDecodeVisHeader`: that path's anchor is fully analytic and already proven <10.0 average per-channel delta by all existing round-trip tests, while this new path's anchor is derived (see below), not exact by construction — adding it alongside gets the real new capability (locating *any* VIS-coded mode despite arbitrary leading silence/noise, not just the `m_sint2`/`m_sint3` trusted subset) with zero regression risk to the already-passing precise path.
      - `SyncEnvelopeDetector` gained an optional `bandwidthHz` parameter (default 100.0, every existing call site unaffected) so the 1080Hz/1320Hz@80Hz tone-race detectors could reuse the same resonate-rectify-smooth class rather than a new one.
      - `SstvModeRegistry.FindByFullVisByte` reuses `VisHeader.GenerateSegments`'s own parity computation (including `Rm12ForcedParityBit`) as the single source of truth for the full legacy byte, rather than re-transcribing a second copy of the mode-code table — verified against 24 real legacy bytes (`RealLegacyBytes` theory data, spot-checked by hand against `sstv.cpp:1993-2065` first) plus two flipped-byte rejection cases, all passing.
      - New `VisLockStateMachine`: a per-sample state machine (`Search`/`ConfirmLock`/`DecodeVis`/`DecodeExtendedVis`/`Verify`, mirroring cases 0/1/2/9/3 exactly) with two structural details ported literally rather than "normalized" once noticed: the `d13` (1320Hz) detector's filter state is only ever advanced while decoding a byte (`sstv.cpp:1976-1978`), frozen between attempts, not reset; and `Verify`'s 30ms countdown is unconditional every sample (`sstv.cpp:2131`), unlike `ConfirmLock`'s countdown, which only decrements while its condition holds and resets to `Search` on any single failing sample (`sstv.cpp:1958-1971`) — a real, deliberate asymmetry between the two, not an inconsistency to fix. When the decoded byte identifies AVT, this class deliberately does not report a lock (matching legacy's own case 3, which diverts AVT into the training-lock state machine — cases 4-8, still unported — instead of calling `Start()`); AVT continues to be found only via the existing fixed-window path.
      - **Line-0 anchor derived analytically, verified by direct arithmetic against already-proven constants, not detected freshly.** Walking the state machine's own fixed timing (15ms `ConfirmLock` + 8×30ms or 16×30ms `DecodeVis`/`DecodeExtendedVis` + 30ms `Verify`) from the trigger-fire sample lands exactly 15ms *before* the same line-0 boundary `VisHeader.TotalDurationMs`/`PrefixDurationMs+ExtendedTailDurationMs` already define — checked for both the normal (910ms) and extended (1150ms) cases independently, both giving the same universal +15ms gap, not two different numbers that happened to need reconciling. Adding that fixed correction reproduces the exact boundary the fixed-window path already computes and that path's tests already prove precise, instead of inventing new, unverified anchor arithmetic from scratch — the single riskiest part of this piece per the Opus review, closed by reuse rather than a fresh derivation.
      - Isolated tests (`VisLockStateMachineTests`, driven directly with samples rendered from `VisHeader.GenerateSegments`/`GenerateExtendedSegments`, no decoder involved): a clean header locks the right mode; a 10ms 1200Hz blip (matching the real break tone's own duration) never advances past `ConfirmLock`'s 15ms hold; an extended-VIS header locks the right mode via the second byte; a flipped data bit and a flipped parity bit each never lock (the latter exercising `FindByFullVisByte`'s rejection specifically); a header preceded by 3 seconds of silence still locks — the actual new capability. All 6 pass.
      - **Real, measured imprecision found by the silence test, not assumed**: the trigger only fires once the d12/d19 envelope detectors' filter chain has actually settled past the crossing point (a real, bounded lag the fully-analytic fixed-window path doesn't have) — measured at ~80 samples (~7.3ms at 11025Hz) for a clean synthetic tone, no noise. Documented in both the test and `VisLockStateMachine`'s own doc comment rather than papering over it with a wide, unexplained tolerance.
      - Wired into `AnalogFmSstvDecoder.TryDecodeHeader` (`TryVisLockStateMachine`, same "processed up to" persistence pattern as the sync-bypass detectors) between the fixed-window path and `TrySyncIntervalDetection`. Commit logic refactored: `CommitSyncBypassMatch` (the sync-bypass detectors' approximate-anchor path) now delegates to a shared `Commit(mode, lineStartSample)`, which `TryVisLockStateMachine` also calls directly with its own exact anchor.
      - **New end-to-end test** (`VisLockStateMachineDecoderTests`): a real encode of Martin M1 preceded by 3 seconds of silence, decoded with no special handling — exact mode ID and the same <10.0 round-trip tolerance the fixed-window path's own tests use (not the sync-bypass paths' looser 25.0), proving the anchor derivation is genuinely as precise as reusing the fixed-window path's own boundary, not just "close enough."
      - Full suite after this piece: 179/179 in `ScanlineStudio.Core.Sstv.Tests` (143 + 36 new: 3 `SyncEnvelopeDetector` bandwidth tests, 26 `FindByFullVisByte` table-driven tests, 6 `VisLockStateMachine` isolated tests, 1 new end-to-end test), solution-wide build clean.
    - **Fifth piece — AVT's training-sequence lock (`sstv.cpp` cases 4-8, `sstv.cpp:2155-2239`), ported and wired as a refinement, not a new capability.** Started by measuring, not assuming, whether this port's `PllFmDemodulator` (configured over a wider 1100-2300Hz span than legacy's 1500-2300Hz) settles inside the tight bands cases 4/5/6 need: a steady 1900Hz tone measured 0.02Hz average deviation with ~5.8Hz ripple, and — the real risk, since AVT's bit windows are only 9.7646ms — 16 consecutive alternating 1600Hz/2200Hz bit windows (simulating a real shifting counter) all landed solidly on the correct side of case 6's decision threshold (measured 1593-1608Hz and 2198-2201Hz respectively, against ≤1704.7Hz/>2095.3Hz bounds). Feasible confirmed empirically before writing the state machine, not assumed from the Hz-conversion math alone.
      - **Surprising finding from reading cases 4-8 in full** (not from the partial read that originally scoped this piece): case 8 (`sstv.cpp:2234-2239`) is dead code — `m_SyncMode` is 8 on entry, so `m_SyncMode--` makes it 7, never 0, so `Start()` there is unreachable. This means the training sequence *never* completes via a clean "all 32 blocks decoded" path in legacy's own real implementation; `Start()` is always reached via the overall timeout (`m_SyncTime`, set once in case 3 to `9 + 2×VIS-block + full-training-sequence` ms = 7141.2475ms, `sstv.cpp:2140`) expiring in case 4, 5, or 6. What makes porting it worthwhile anyway: case 6 *recalculates* that timeout, smaller, after every successfully-decoded block (`sstv.cpp:2200-2201`, from the block's own position in the 32-block sequence, encoded in its `h` byte) — so a signal with real, sustained lock finishes close to the actual content end, while a signal with no lock at all waits out the full nominal duration (exactly what this port's existing fixed-duration skip, `VisHeader.AvtExtraHeaderDurationMs`, already does). Confirmed also: case 7 (`WaitNextMarker`) does not decrement the overall timeout at all — frozen while waiting between blocks, only ticking in states 4/5/6 (no `m_SyncTime--` anywhere in `sstv.cpp:2221-2233`).
      - This reframed the piece from "new capability" (like `m_sint2`/`m_sint3`/`VisLockStateMachine` each were) to **refinement of an already-working, already-tested path**: more accurate completion timing for real captured audio with clock drift, not a change to whether AVT is found at all.
      - New `AvtTrainingLockStateMachine`: a per-sample state machine (`MarkerSearch`/`MarkerConfirm`/`DecodeBits`/`WaitNextMarker`, mirroring cases 4/5/6/7) consuming already-demodulated Hz values from this port's existing continuously-running `PllFmDemodulator` output rather than a second PLL instance — confirmed both legacy's `m_pll.Do(ad)` here and this port's main decode path are the exact same demodulator, so no new DSP component was needed, only new sequencing/threshold logic around one already ported. Case 8 folded directly into case 6's `h==0x40` transition (a documented simplification with no functional effect, since real case 8 is a single-sample, ~0.09ms-at-11025Hz pass-through that never calls `Start()` either way). `_visData` shifts **left** here (`sstv.cpp:2192`), MSB-first — the opposite direction from `VisLockStateMachine`'s case2/9 right-shift — ported literally per-source, not normalized to match.
      - Isolated tests (`AvtTrainingLockStateMachineTests`, driven with *real* demodulated frequencies — audio rendered from `VisHeader.GenerateAvtSegments` run through an actual `PllFmDemodulator`, not hand-fed "perfect" Hz values, catching any real mismatch between ported thresholds and actual demodulator behavior): a full, real training sequence completes at sample 58608 (~5316.9ms) — measured, not assumed — right at the training sequence's own real content duration (58568 samples, ~5312.9ms), dramatically less than the full ~7141ms nominal budget (78732 samples) a signal with no training signal at all correctly waits out instead (also measured, exact match to within 10 samples). Both pass.
      - Wired into `AnalogFmSstvDecoder`: `TryDecodeVisHeader`'s AVT branch no longer commits atomically with a fixed sample count (its completion point is data-dependent) — it starts a multi-call pending phase (`TryStartAvtTraining`/`TryResolveAvtTraining`, `_avtTrainingPending` checked first in `TryDecodeHeader` so other fallbacks don't fire mid-resolution), feeding already-demodulated frequencies into the training lock from the point legacy's own case 3 hands off to case 4 (after all 3 VIS repeats), with the existing, already-tested `AvtExtraHeaderDurationMs` kept as a hard fallback ceiling matching legacy's own real timeout-fallback behavior.
      - **New end-to-end test** (`AvtTrainingLockDecoderTests`): isolates anchor-precision from AVT's separately out-of-scope per-line drift (AVT has no Auto Slant correction, in both legacy and this port) by checking only row 0's accuracy — since drift hasn't had a chance to accumulate there, row-0 error reflects header-anchor precision alone. At a realistic 500ppm clock mismatch (the same pessimistic-but-real tolerance `SlantTests` uses), measured 6.04 average per-channel delta, comfortably inside normal round-trip tolerance. A deliberately severe 1% mismatch (tried first) degraded badly (~60 delta) — traced to AVT's tight absolute-Hz decision bands being pitch-shifted by the same 1% as every other frequency the demodulator computes, the same category of honestly-documented degradation `SlantTests` already records for Robot36's own 1% case, not a bug in this piece's anchor arithmetic.
      - Existing AVT round-trip tests (`SstvRoundTripTests`, driverless-clock case) confirmed unaffected: the new mechanism's real-lock completion point (~8046.9ms from header start, by direct arithmetic) lands within ~4.7ms of the old fixed-duration skip's (~8042.2ms) for a driftless same-process test, well inside the existing <10.0 tolerance.
      - Full suite after this piece: 182/182 in `ScanlineStudio.Core.Sstv.Tests` (179 + 3 new), solution-wide build clean.
    - At this point, 2 of 7 pieces remained unstarted: `m_sint1` (see piece 7 below for the corrected scoping and why it was blocked on `CLVL`) and per-line continuous re-verification (piece 6, done next, see below).
      - Also noted, not part of the 7 but discovered while doing pieces 2, 4, and 5: legacy's own fine-pixel-alignment bootstrap (`CSSTVDEM::Start`'s `m_wBgn` staged buffer search, `sstv.cpp:1717-1744`, and `SyncSSTV`, `Main.cpp:3751-3799`) is unported and is the real fix for `m_sint2`/`m_sint3`'s midpoint-approximation anchor imprecision (~11-24 average delta), `VisLockStateMachine`'s own smaller ~7ms trigger-lag imprecision, and (though not measured directly) likely also a source of some of `AvtTrainingLockStateMachine`'s own small residual anchor error — a candidate 8th piece if any of these paths' alignment precision ever needs to improve further.
      - **Opus review checkpoint moved up, superseding the earlier "wait for all 7" instruction, then completed with fixes.** With the remaining 2 pieces newly scoped as genuinely large (`m_sint1` blocked on an unstarted `CLVL` AGC port; per-line continuous re-verification requiring the whole state machine to run concurrently with image reception plus an abort-and-restart capability this port's architecture doesn't have yet), the user's own call was to review what's done now (5 of 7) rather than wait for the remaining two, which could each expand into their own multi-session breakdown the way pieces 4 and 5 did.
      - **Independent adversarial review, cross-checking every piece against actual legacy source directly** (not the roadmap's own summaries, not the code's own doc comments — the same failure mode Scottie's earlier channel-order bug demonstrated: a self-consistent round-trip test can pass while both TX and RX agree with each other but disagree with real legacy). **Pieces 1-4 (`CSYNCINT`, `m_sint2`, `m_sint3`, VIS-bit-decode-via-PLL-tone-counting) fully confirmed correct**, including every detail flagged as highest-risk beforehand: the 80Hz vs 100Hz tone-race resonator bandwidth (hand-verified against `sstv.cpp:1446/1448` vs `1447/1449/1450`), all 24 real legacy VIS bytes bit-by-bit against `FindByFullVisByte` (including the RM12 parity anomaly and the escape byte `0x23`'s own failed-parity oddity), the LSB-first bit-shift order, the `d13` filter's frozen-between-attempts asymmetry, and — independently re-derived by walking the timing from scratch, not by trusting the doc comment — the universal "+15ms" anchor-reconciliation gap for both the normal (910ms) and extended (1150ms) header cases.
      - **Piece 5 (AVT training lock) had two real findings, both fixed:**
        1. **`AvtTrainingLockStateMachine.cs`'s `h==0x40` (last block) branch was overwriting `_phaseCounter`** with a shortened value (`LastBlockWaitMs`) that legacy's real `sstv.cpp:2204-2209` never touches in this branch — `m_SyncATime` there is already a full bit-window's worth from the *unconditional* assignment at `sstv.cpp:2191` (this port's own line 131, which runs for every bit including the block's last one, before the validity check is ever reached). The bug made this class complete ~63 samples (~5.7ms at 11025Hz) early after a clean 32-block lock — invisible to `AvtTrainingLockStateMachineTests`' wide (±300ms) completion-time tolerance. Fixed by deleting the overwrite; the class doc comment's claim that this was part of the (harmless) case-8-folding decision was also wrong and corrected — the two are unrelated.
        2. **The class's own internal timeout budget double-counted 2 VIS repeats.** `AnalogFmSstvDecoder` only ever constructs `AvtTrainingLockStateMachine` after already skipping past all 3 VIS repeats (at the training sequence's own first marker), but the class's constructor copied legacy's full case-3 budget (`9 + 2×VIS-block + training`, `sstv.cpp:2140`), which is scoped from legacy's real, ~1835ms-*earlier* case-3-exit point. Masked at the decoder level (`AnalogFmSstvDecoder`'s own separately, correctly computed `_avtTrainingFallbackDeadlineSample` always fires first), but the isolated `NoTrainingSignalAtAll_CompletesAtTheFullNominalBudget` test and two doc comments asserted/described the wrong value. Fixed: internal budget is now `9 + VisHeader.AvtTrainingSequenceDurationMs` only (~5.3s, not ~7.1s); both doc comments (the class's own, and `TryStartAvtTraining`'s) corrected to accurately describe where this port's own origin point actually sits relative to legacy's; the isolated test's expected value updated to match.
      - **Two doc-only fixes, no behavior change:** `VisLockStateMachine`'s class comment claimed AVT's training-lock state machine "doesn't have yet" — stale since piece 5 added it; corrected to describe the real, narrower remaining gap (this class's own sample-by-sample scanning still can't locate AVT, only the fixed-window path can, since it hands off to `AvtTrainingLockStateMachine` for the header's own remainder). `TrySyncIntervalDetection`'s merged `m_sint2`/`m_sint3` loop was flagged as having no equivalent to legacy's own case-0/1 gating (which effectively freezes both trackers once a stronger 1200Hz trigger has fired) — a real, low-severity structural divergence (this path only ever runs when nothing else has locked), now documented rather than left silent.
      - Full suite after all review fixes: 182/182 in `ScanlineStudio.Core.Sstv.Tests`, solution-wide build clean, no regressions.
    - **Piece 6 (per-line continuous re-verification) started, broken into sub-pieces 6a-6d; 6a done.** Scoped via an Opus-verified outline before any code was written (per the now-established "verify then chop up" practice): the initial fear that this piece would require running every already-ported detector (`m_sint2`/`m_sint3`/`VisLockStateMachine`) concurrently against every sample of every image was **wrong** — legacy hard-gates `m_sint1`/`m_sint2`/`m_sint3` behind `!m_Sync` at every call site (`sstv.cpp:1899/1949/1953/1959`), so only `VisLockStateMachine`'s own mechanism (case 0 trigger through case 3) genuinely runs while locked. The real, much bigger gap the scoping surfaced: **this port had no end-of-image reset at all** — legacy's `Stop()` (`sstv.cpp:1769-1791`) plus cases 512/513's 0.5s dead-time wait (`sstv.cpp:2243-2252`) has no counterpart, so two clean back-to-back transmissions in one stream would decode as one image then silence, with no mid-reception abort needed to see it.
      - **6a — `AnalogFmSstvDecoder.EndOfImage`, a direct port of `Stop()` + cases 512/513.** Modeled as an analytic skip (jump `_consumedSamples` forward by 0.5s worth of samples) rather than a literal per-sample countdown, matching the same style already used for fixed-duration header skips — legacy's own dead zone is footer-content-agnostic (just a fixed wall-clock wait), so this is a faithful adaptation, not an invented shortcut. Resets `_mode`/`_lineDecoder`/`_pixels`/`_nextLine` and all three AFC/Slant/AVT-pending fields; resets `_syncBypassTracker`/`_syncBypassNarrowTracker` (`SyncIntervalTracker` gained a `Reset()` method mirroring `CSYNCINT::Reset()`, `sstv.cpp:576-582`) and `VisLockStateMachine` (which also gained a `Reset()`), matching legacy's own `Stop()` resetting `m_sint1`/`m_sint2`/`m_sint3`. Since all three of these persistent detectors had always been fed starting at absolute raw-sample index 0 until now, resetting them mid-stream needed new origin-offset fields (`_syncBypassOriginSample`, `_visLockOriginSample`, mirroring `AvtTrainingLockStateMachine`'s existing `_avtTrainingOriginSample` pattern) so their own internally-relative results still convert to correct absolute buffer positions.
      - **Two real prerequisite bugs found and fixed before 6a's own test could pass at all**, both surfaced by the Opus outline-verification pass and independently confirmed by direct code inspection before fixing: `_nextLine` was never reset anywhere in `Commit()` (a second commit would resume decoding mid-image, immediately failing since `_nextLine` already equalled the *first* image's `ImageHeight`); and `ApplyAfcCorrections` had no upper bound beyond `_demodulatedFrequencies.Count` (unlike `ApplySlantTracking`, which was already carefully bounded for exactly this reason) — a single bulk `PushSamples` call would eagerly AFC-correct straight through the first image's footer/dead-zone and into a not-yet-detected second transmission's audio using a correction tuned to the first image's own frequency offset, before that transmission's own `Commit()` even ran. Fixed with a new `_afcBoundSample` (a generous nominal upper bound on each image's own total audio extent, computed once per `Commit()`), the same category of bulk-vs-streaming ordering bug that already bit `ApplySlantTracking` and `TrySyncIntervalDetection`, but for AFC specifically only became reachable once `EndOfImage` made a second `Commit()` possible at all.
      - **New end-to-end test** (`EndOfImageResetTests`): two complete back-to-back transmissions (the encoder always appends a footer, so concatenating two encodes is exactly the real-world shape) both decode correctly. Measured, not assumed: first transmission delta 2.18 (resolved via the exact fixed-window path, same as every other round-trip test); second transmission delta 9.39 (resolved via `VisLockStateMachine`'s forward search instead, since the second header lands mid-footer relative to the fixed 0.5s dead-time skip — the footer can run up to ~900ms for normal modes, longer than the skip, which is expected and faithful to legacy's own footer-content-agnostic dead zone, not a bug). A second, rate-mismatched variant was attempted specifically to give the AFC-bound fix a real, nonzero correction to exercise (a driftless synthetic signal gives AFC nothing to correct, making the bug invisible either way) — dropped after measurement showed its result (delta2≈55) was dominated by `VisLockStateMachine`'s own already-known anchor imprecision compounding with Auto Slant's convergence behavior under drift, not cleanly isolating the AFC fix specifically; shipping a confounded test with a loose tolerance would have been misleading rather than a real regression guard. The AFC-bound fix itself is verified by direct code audit (confirmed via grep that `_nextLine` was genuinely never assigned anywhere, and that `ApplyAfcCorrections`' loop bound was genuinely unconditional) rather than by an isolated automated test — noted honestly rather than claiming test coverage that doesn't exist.
      - Full suite after 6a: 183/183 in `ScanlineStudio.Core.Sstv.Tests` (182 + 1 new), solution-wide build clean.
      - **6b (further `Commit()` re-entrancy audit) folded into 6a/6c's own fixes — a final systematic pass over every per-image field found nothing further needing a reset.** `_rawSamples`/`_demodulatedFrequencies` correctly never reset (whole-session buffers, not per-image state); `InitializeAfc`/`InitializeSlant` already fully re-anchor their own watermarks on every `Commit()`; `_avtTrainingPending` is always false by the time any `Commit()` runs (the only path that sets it also always clears it before committing); `_syncBypassProcessedUpTo`/`_syncBypassNarrowPhaseActive` are correctly left untouched by `Commit()` since `TrySyncIntervalDetection` never runs while locked (matching legacy's own gating) — they're only relevant again after a *natural* `EndOfImage()`, which already resets them.
      - **6c — `VisLockStateMachine` wired to keep running while locked, `Commit()` made genuinely re-entrant, two real bugs caught by this port's own end-to-end test (not by review or reasoning alone).** Checked once per decoded line (not once per `TryProcessBuffer` call — a bulk-pushed buffer would otherwise let a whole image's worth of lines decode in one shot with no chance to notice a mid-reception re-lock at all). Only `VisLockStateMachine` runs here, never the fixed-window path (assumes `_consumedSamples` is a header start) or `TrySyncIntervalDetection` (`m_sint2`/`m_sint3` hard-gated behind `!m_Sync` at every legacy call site, `sstv.cpp:1899/1949/1953/1959`).
        1. **Self-rediscovery bug**: `_visLockProcessedUpTo` was only ever advanced by `TryVisLockStateMachine` itself — a transmission found via the *fixed-window* path (the common case) never touched it, leaving it at its initial 0. Once 6c started calling it per-line, it began scanning from sample 0 on the very next line — i.e., rediscovering the current transmission's *own* header, which it had never gotten a chance to examine — and immediately "restarted" onto itself. Caught immediately by `EndOfImageResetTests` reporting 3 detected modes instead of 2. Fixed in `Commit()`: fast-forward `_visLockProcessedUpTo`/`_visLockOriginSample` past whatever a *different* detection path just resolved (via `Math.Max`, since a self-triggered commit already has `_visLockProcessedUpTo` correctly positioned and must not be rewound backward into content it just used to find the match), and always `Reset()` the state machine's logical state.
        2. **Bulk-vs-streaming lookahead bug, the third instance of this exact category in this codebase** (after `ApplySlantTracking` and the original `TrySyncIntervalDetection` ordering fix): `TryVisLockStateMachine` scanned all the way to `_rawSamples.Count` in one call. Called mid-reception against a bulk-pushed buffer containing a whole first transmission followed by a real second one, it would discover the *second* transmission's genuinely valid header on its very first call (right after decoding line 0 of the first) and restart onto it immediately — abandoning a transmission this port had every ability to finish. Fixed by parameterizing `TryVisLockStateMachine(int upperBoundSample)`: the pre-lock caller (in `TryDecodeHeader`) passes `_rawSamples.Count` (no current decode position to bound against yet, same as always), the while-locked caller (piece 6c) passes `_consumedSamples` (never running ahead of what's actually been decoded so far).
        3. New `DecodeRestarted` event added to `ISstvDecoder` (only implementation, `AnalogFmSstvDecoder`) — distinct from `ModeDetected`, which fires for both a fresh detection and a mid-reception restart. Callers displaying an in-progress image need this specifically to know the partial image should be discarded.
        4. Measured, not assumed: suite duration grew from ~1m25s to ~2m6s (183→184 tests) — a real, non-trivial per-line cost from running `VisLockStateMachine` on every decoded line across the whole test matrix. Not optimized now (out of scope for Phase 1 correctness work), noted honestly rather than silently absorbed.
      - **6d — new end-to-end test** (`MidReceptionRestartTests`): a first transmission's audio truncated at 30% through its own image body (well past the header, well before completion — simulating a real station cutting out or being overridden), immediately followed by a complete second transmission, no footer, no gap. Asserts exactly one `DecodeRestarted` fires and the second (real) transmission decodes correctly. Measured, not assumed: 9.81 average per-channel delta — resolved via `VisLockStateMachine`'s own already-documented ~7ms anchor lag (not the exact fixed-window path), close to but under the standard 10.0 tolerance, consistent with that already-known cost rather than a new imprecision from 6c/6d specifically.
      - Full suite after 6b/6c/6d: 184/184 in `ScanlineStudio.Core.Sstv.Tests` (183 + 1 new), solution-wide build clean.
      - Deferred and named explicitly per the Opus outline review, not silently absorbed: retuning the while-locked detectors from `AfcTracker`'s correction (legacy's real detectors get this for free via `InitTone`'s side effect, `sstv.cpp:2362`, this port's are fixed-frequency); the lock-dependent input bandpass switch (`HBPFS` vs `HBPF`/`HBPFN`, `sstv.cpp:1826-1832`); mid-image AVT re-lock (`sstv.cpp:2139-2144`, `VisLockStateMachine` deliberately never reports AVT); mid-image narrow-mode FSK-announce re-lock (`sstv.cpp:2592`, needs a sample-by-sample FSK decoder this port doesn't have — `TryDecodeNarrowModeHeader` is a fixed-window analytic shortcut with no real-time legacy counterpart).
      - **Piece 6 done — 6 of 7 complete.** Only `m_sint1` remains, still blocked on the unstarted `CLVL` AGC port (see above) — closed next, piece 7 below.
      - **Independent adversarial review of 6a-6d, then fixes.** Same practice as the earlier 5-piece checkpoint: re-verify every already-fixed bug independently rather than trusting the original fix, then look for new ones.
        - **Bug fixes #1 (`_nextLine=0`), #4 (`upperBoundSample` parameterization) confirmed fully correct.** Bug fix #3 (`Math.Max` origin-tracking) confirmed *behaviorally* correct — independently re-derived the anchor-vs-consumed-samples arithmetic from `VisLockStateMachine`'s own formula rather than trusting the original claim, and found the anchor is provably always later than the state machine's own stop point (by ~15ms/164 samples at 11025Hz, for every candidate mode) — but the *comment* explaining it was backwards (claimed `Math.Max` "leaves it alone" for the self-triggered case; it always advances by that same ~164 samples). Fixed the comment. Also found and fixed a genuinely redundant, slightly-wrong `_visLockProcessedUpTo++` immediately after `Commit()` in `TryVisLockStateMachine` — since `Commit()` already sets both `_visLockProcessedUpTo` and `_visLockOriginSample` to the same value, the extra increment desynced them by exactly 1 sample, biasing every subsequent anchor from that instance 1 sample (~0.09ms) early. Removed.
        - **New bug found: `TryDecodeNarrowModeHeader` silently disabled AFC for every MN/MC mode.** It duplicated `Commit()`'s body inline instead of calling it (predating piece 6a), so when `Commit()` gained `_afcBoundSample` for the AFC-bound fix, this path never got it — staying at its default 0, so `ApplyAfcCorrections`' bound became `Math.Min(count, 0) = 0` and its correction loop never ran. No existing test caught this (nothing in the suite exercised a mistuned-audio narrow-mode decode end to end). Fixed by routing through `Commit()` like every other detection path. New `NarrowModeAfcTests` added — honestly measured, not oversold: at the same realistic 500ppm `SlantTests` uses, buggy and fixed are nearly indistinguishable (10.66 vs 10.35, Auto Slant alone already compensating for most of a mismatch this small) — confirmed the bug *is* clearly catchable at a deliberately severe synthetic 2000ppm (24.73 vs 20.52, still elevated even fixed, MN73's tighter AFC band degrading similarly to AVT's under severe mismatch) but that severity wasn't used as the actual assertion since it introduces its own confounding degradation, the same tradeoff already documented for the AVT clock-mismatch test. The fix itself is verified correct primarily by direct code reasoning (parallels the exact pattern every other `Commit()`-calling path already uses), not primarily by this test.
        - **`DecodeRestarted` ordering trap found and fixed.** It fired with the *new* mode (`_mode!`) — which technically matched its own doc comment's promise, but the promise itself was a trap: `ModeDetected` for the new mode always fires first (inside `Commit()`, called by `TryVisLockStateMachine` before `DecodeRestarted` ever runs), so a caller that allocates a display buffer on `ModeDetected` and discards on `DecodeRestarted` would discard the buffer it just allocated for the new mode, not the abandoned one it actually needs to throw away. Fixed to pass the abandoned mode (the local `mode` variable already in scope, captured before the restart check); `ISstvDecoder.cs`'s doc comment corrected to match.
        - **Real risk-escalation finding, documented not fixed (blocked on the same `CLVL` prerequisite as `m_sint1`):** the omitted `m_SLvl`/`m_SLvl2` absolute-amplitude gates (already a documented simplification in four other places) were scoped as harmless when `VisLockStateMachine` only ran pre-lock — a false positive there just wasted scanning time on silence/noise. Piece 6c changed the blast radius: now it runs per decoded line against real image content, where a false positive can destroy an in-progress *good* image. Concretely demonstrated, not just theorized: Robot36/R24's 150ms lines are exactly 5×30ms bit windows, so a bit window ending inside that line's own sync pulse recurs every 5th window, and enough consecutive dark/sync-heavy content can in principle assemble a byte identical to a real VIS code (R24's `0x84`, hand-verified reachable this way). Two things bound the real risk without eliminating it (the per-bit reject test, and that the likeliest garbage byte `0x00` matches no mode), and neither of this port's own tests (smooth synthetic gradients) has triggered it — but this needs the `CLVL` AGC port to close properly, tracked as part of that same prerequisite, not patched here with an invented substitute threshold. Documented in `VisLockStateMachine`'s own doc comment.
        - **Two more real, undocumented legacy behaviors surfaced, both out of scope for this piece specifically but now named rather than silently absorbed:** legacy actually *saves* a mid-reception-abandoned image if it was ≥65% complete (`sstv.cpp:2134-2138`'s `m_ReqSave`, `Main.cpp:4931-4934`'s `WriteHistory`) — the port discards unconditionally, a persistence-layer concern with no logbook/history feature yet to hook it into ([[08-logging]], Phase 4). And `m_SyncRestart` is a real, user-toggleable legacy option (`sstv.cpp:1486` default 1, toggled via `Main.cpp:10907`/`11887`'s lock button) — this port hard-wires the equivalent behavior on with no way to disable it, since no settings/UI layer exists yet to expose the toggle through. Both are real, not deferred silently: revisit when [[08-logging]] and the settings/UI layers exist.
        - **Two doc-only overclaim fixes:** `_afcBoundSample`'s comment called it "generous"; it's exactly nominal (pre-slant) image duration, and with Auto Slant active on a slow clock actual elapsed samples can exceed it slightly (bounded to ~0.1% of image length in practice, well under one line at realistic drift) — corrected wording, not fixed further, matches the same order of magnitude as other already-accepted small imprecisions in this system. `EndOfImage`'s comment claimed its 0.5s analytic skip has "no detectable effect either way"; the resonator-fed detectors' filter state actually carries over from the previous image's tail rather than genuinely settling against dead-time content, resettling within ~10-30ms against a 300ms leader in practice — real but small, described honestly instead of as literally undetectable.
        - One finding explicitly re-confirmed as *not* a bug after independent tracing: the "restart on the final line" race (if `TryVisLockStateMachine` matches on the same line that completes an image, `restarted=true` correctly short-circuits before the `EndOfImage()` check, so the freshly-committed new mode is never wiped). The AFC double-correction risk across a mid-reception restart, re-confirmed here as "bounded to at most one line," turned out to be **wrong** — see the piece 7 holistic review below, which found and fixed the real, much larger bound.
        - Full suite after all review fixes: 185/185 in `ScanlineStudio.Core.Sstv.Tests` (184 + 1 new `NarrowModeAfcTests`), solution-wide build clean.
    - **Piece 7 — `CLVL` (legacy's shared AGC) and `m_sint1`, the last of the 7 pieces. Done, complete, all 7/7 closed.** Scoped via a plan-verification Opus review *before* any code was written (a variant of the now-established practice — this time verifying the *plan*, not reviewing finished code), with several of that review's own claims independently re-checked against source directly rather than trusted on summary alone, catching one of its own errors (`NARROW_SYNC` momentarily mis-read as a disabled feature flag mid-conversation; corrected immediately by re-reading `sstv.h:440` — it's a frequency constant, `#define NARROW_SYNC 1900`, always compiled in).
      - **`CLVL` (`sstv.h:223-298`) ported as `LevelAgc.cs`.** Confirmed it only affects the sync-detection path: legacy's `Do()` prologue (`sstv.cpp:1834-1839`) computes `d = clamp(m_lvl.AGC(d)*32, ±16384)` and feeds *that* to every tone-envelope discriminator (`d11`/`d12`/`d13`/`d19`/`dsp`), while the actual FM pixel/AFC demodulator (`m_pll`/`m_fqc`/`m_hill`) reads `m_lvl.m_Cur` — the pre-AGC raw value — so this piece doesn't touch pixel decode at all, only the absolute-threshold sync heuristics.
      - **`m_agcfast` is permanently on, confirmed by reading `CSSTVDEM`'s constructor directly (`sstv.cpp:1470`), not assumed from `CLVL`'s own default.** `CLVL`'s ctor sets `m_agcfast=0`, but `CSSTVDEM`'s ctor immediately overwrites it to `1`, unconditionally, and nothing else in the program ever touches it — so legacy's `m_agcfast==0` branch (`sstv.h:272-279`, an averaged-5-window AGC recompute) is unreachable dead code for this demodulator. `LevelAgc.cs` ports only the live (`m_agcfast==1`) formula; the surrounding peak-hold bookkeeping that branch shares a block with (`m_PeakMax`/`m_PeakAGC`/`m_Peak`/`m_CntPeak`) is write-only in this program too (only reader is the UI level-meter's peak-hold bar, `Main.cpp:6186-6192`) and was omitted as a documented simplification, not tracked-but-unread state.
      - **Scale bridge, the one genuinely new risk this piece introduced and the reason it isn't a copy-paste port.** Legacy's `d` is int16-valued (`sstv.cpp:1821`'s 24578 overflow check, `Wave.cpp:796-808`'s direct `SHORT`→`double` copy); this port's own sample contract is `float` in `[-1.0, 1.0]` (`spec/05-audio-engine.md:44`). Feeding `±1.0` straight into a faithful `CLVL` would pin its AGC at the floor gain forever (the `>32` adaptation gate never trips) — not a cold-start gap, a *permanent* one, found by direct reasoning about the two amplitude conventions before writing any wiring code, not by a failing test. Fixed with a single documented multiply (`raw * 32768.0`) at the `LevelAgc` boundary (`AnalogFmSstvDecoder.AgcSampleAt`), keeping every constant inside `LevelAgc` itself (32, 16384, the `>32` gate) literally identical to legacy's.
      - **Wired as a single, never-reset, continuously-running shared per-sample cache** (`_agcSamples`/`_agcCurMaxSamples`/`_levelAgcProcessedUpTo`), matching that legacy's `Stop()` (`sstv.cpp:1769-1791`) never touches `m_lvl` — the only real `Init()` call sites are the PTT-transition ones (`Sound.cpp:398/443`), which for an RX-only decoder map to construction, not `EndOfImage()`. Feeds every existing sync/tone-envelope consumer: `TrySyncIntervalDetection`'s `d12`/`d19`/`dsp`, `VisLockStateMachine`'s four detectors, and (piece 7b2, deliberately split out so its own fallout could be measured separately) `ApplySlantTracking`'s peak-position detector — legacy feeds that same envelope from the AGC'd signal too (`sstv.cpp:2292-2299`, live branch since `NARROW_SYNC==1900`). `TryDecodeNarrowModeHeader` was checked and confirmed *not* a consumer — it discriminates narrow-vs-VIS headers via already-demodulated-frequency averaging, a port-specific technique with no direct `CLVL`-consuming legacy counterpart, not an oversight to fix.
      - **Re-measured, not assumed, three round-trip tolerances after 7b2 and again after 7c wired the absolute thresholds in.** CLVL's AGC correctly hard-clips a full-amplitude tone to `±16384` once warmed up — legacy-faithful (real signals get this same treatment), not a port bug — which measurably increases peak-position/trigger-timing noise: `SyncBypassDetectionTests` (Scottie S1 18.06→19.01, Martin M1 12.62→12.65, Martin M2 20.78→25.13, SC2-180 12.91→13.85, tolerance 25.0→29.0) and three `VisLockStateMachine`-anchored tests (`VisLockStateMachineDecoderTests` 10-ish→11.49/13.0, `MidReceptionRestartTests` 9.81→11.84/14.0, `EndOfImageResetTests`' second-transmission delta 9.39→11.42/14.0 — its *first*-transmission delta stayed at 2.18, unchanged, confirming these changes are correctly scoped to the AGC'd path only). Every new tolerance was measured via the actual failing/passing value, not guessed headroom.
      - **`m_SLvl`/`m_SLvl2`/`m_SLvl3` reintroduced (piece 7c), values corrected from an earlier wrong assumption by reading the constructor directly.** `SetSenseLvl` (`sstv.cpp:1793-1817`) has 4 cases keyed on `m_SenseLvl`; the shipped default is **case 1 (`SLvl=3500`/`SLvl2=1750`/`SLvl3=5700`)**, not the switch's `default:` branch (`2400`/`1200`/`5000`) an earlier note here assumed — `CSSTVDEM`'s ctor sets `m_SenseLvl=1` unconditionally (`sstv.cpp:1489`) before calling `SetSenseLvl()`, and the only other write path (`Main.cpp:1865`'s INI read) falls back to that same ctor value when the key is absent. Case 0 is only reachable via a discrete "Sense Level" UI setting (`Option.dfm`'s `RGSLvl`) this port doesn't expose yet.
      - **Full three/five-term conditions applied, not just the previously-ported relative comparisons**, all confirmed by reading the literal source rather than trusting the prior summary: `TrySyncIntervalDetection`'s `m_sint2` trigger (3-term, `sstv.cpp:1905`) and `m_sint3` trigger (5-term, `sstv.cpp:1926` — its difference term is gated by `SLvl`, *not* `SLvl3`, an asymmetry easy to miss); `VisLockStateMachine`'s `Search`/`ConfirmLock` (3-term, `sstv.cpp:1946/1958`) and `Verify` (only a **2**-term condition, `sstv.cpp:2133` — confirmed by direct read that there's no difference-gate term at this specific site, unlike the other two). The per-bit reject (`sstv.cpp:1981-1984`) turned out to be missing a whole second OR'd branch entirely, not just unthresholded: `(d11<d19 && d13<d19) || (fabs(d11-d13) < m_SLvl2)` — the "too close to call" branch was absent from this port before piece 7c, a real gap the earlier-summarized "relative half only" framing had missed.
      - **AFC silence gate (`m_lvl.m_CurMax>16`) ported** (`sstv.cpp:2258`, case 0/PLL — the case `ApplyAfcCorrections`' own doc comment already cites as what it models). Confirmed the gate wraps the frequency-counter call itself in this case, not just the correction application (unlike case 1/zero-crossing's shape, where the counter always runs and only the correction is gated) — so `ZeroCrossingFrequencyCounter.ProcessSample` is skipped entirely when the gate fails, matching legacy's exact call shape, using a new per-index `_agcCurMaxSamples` snapshot (the gate needs the *historical* `CurMax` at each corrected sample, not "whatever `LevelAgc.CurMax` is right now" — this port's AFC correction runs as a deferred bulk pass, potentially well after the shared AGC cache has advanced past that index for an unrelated consumer).
      - **`SenseLevelCalibrationTests` added**: a full-scale tone through the real `LevelAgc`+`SyncEnvelopeDetector` pipeline must clear `SLvl`/`SLvl3` (the tightest of the three) with real margin — a calibration sanity check that the reintroduced constants aren't miscalibrated to be permanently unreachable, which would silently disable every trigger this piece just wired in without any test noticing.
      - **`m_sint1` ported (piece 7d) as a fourth `SyncIntervalTracker` instance**, `isNarrow:false`, no mode allowlist (unlike `m_sint2`'s `SyncBypassTrustedModes`) — confirmed by reading `sstv.cpp:1899-1904` directly: `m_sint1.SyncStart()` is polled first, every sample, unconditionally, and wins outright with no filter if it matches. Critically, it does **not** independently scan like `m_sint2`/`m_sint3` (which get their own separate, always-checked lower threshold) — it only ever receives new peak data from the *same* primary threshold that also drives the real VIS-bit-decode state machine forward: `SyncTrig` on that threshold's rising edge (`sstv.cpp:1949`), `SyncMax` while it holds (`sstv.cpp:1958-1961`). Implemented via a local case-0/case-1 latch (`_syncBypass1PrimaryHeld`) inside `TrySyncIntervalDetectionStep` (piece: `m_sint1`-decoder-ordering fix, see below, renamed this method from `TrySyncIntervalDetection` — a deliberate second copy of what `VisLockStateMachine` already tracks internally via its own `Search`/`ConfirmLock` states. **Correction, un-updated here until now even though the finding itself was recorded elsewhere in this file (the piece 7 holistic review below):** the two latches do *not* "necessarily agree sample-for-sample" — only true pre-lock; while locked, only `VisLockStateMachine`'s detectors run, and the two detector pairs can genuinely disagree for a short settling window after `EndOfImage`. The original justification for the copy ("this loop has no access to `VisLockStateMachine`'s separate cursor/instance") is *also* now stale post-`m_sint1`-decoder-ordering-fix, which runs both interleaved with direct field access — the copy is kept for a different reason (legacy shares one d12/d19 computation per sample; unifying the two ported detector pairs is a bigger change, left as a candidate follow-up).
      - **Documented, not fixed: same-sample `m_sint1`-then-`m_sint3` double-fire can't happen in this port** (return-immediately-on-any-match, matching the existing accepted divergence already noted for `m_sint2`/`m_sint3`'s own case-0/1 freeze) — legacy's real case-0 body is straight-line code, so `m_sint1` winning and calling `Start()` doesn't stop the sibling `m_sint3` block from also evaluating that same sample; low severity, since legacy's own same-sample second fire is close to a no-op by the time it would matter (`m_Sync` is already 1).
      - **`SyncBypass1DetectionTests` added**: proves the actual behavioral difference from `m_sint2`, not just "another interval tracker" — a headerless Robot 36 transmission (a mode `SyncCheckSub` recognizes, confirmed via `SstvModeRegistry.GetSyncIntervalMatchDepth`, but which `SyncBypassTrustedModes` does *not* include) locks via `m_sint1` alone, a scenario `m_sint2`'s own allowlist would reject outright even if its internal periodicity match succeeded.
      - Full suite after piece 7 (7a-7d): 192/192 in `ScanlineStudio.Core.Sstv.Tests` (185 + 4 `LevelAgcTests` + 2 `SenseLevelCalibrationTests` + 1 `SyncBypass1DetectionTests`), solution-wide build clean.
      - **Known, explicitly logged remaining gap, not silently closed as part of "complete":** legacy's pre-AGC input chain (`sstv.cpp:1823-1833` — a one-pole averaging LPF, `d=(s+m_ad)*0.5`, then a 400-2500Hz bandpass, `HBPFS`) has no counterpart in this port. Both also feed `m_lvl.m_Cur` and therefore the pixel demodulator, so closing this gap properly is a materially larger change than this piece's scope (adding it here would mean re-touching pixel decode, not just sync detection) — named explicitly as a real, currently-open gap rather than folded into "piece 7 done," since the reintroduced absolute thresholds in 7c assume legacy's exact pre-AGC chain and this port's calibration tests measure against a slightly different (BPF-less) signal path. A candidate follow-up if real captured audio (as opposed to this port's own synthetic fixtures) ever shows the AGC's peak measurement picking up out-of-band noise this filter would have excluded.
      - **Holistic adversarial review of the whole 7-piece system, then fixes** (a different kind of review from the piece-by-piece ones above: explicitly scoped to cross-piece interactions no single piece's own review could have seen, since each was reviewed before the next piece even existed). Found 2 real bugs, 2 doc-only issues, and re-confirmed everything else already checked (absolute-threshold coverage across all 7 legacy `m_SLvl` use sites, `SyncCheck` candidate iteration order, AGC cache continuity across `EndOfImage`) — full findings and reasoning preserved in this session's own history, summarized here:
        1. **`m_sint1`'s peak was being consumed one sample after being latched, before `SyncMax` ever ran — a real bug, fixed.** `_syncBypass1Tracker.TryStart()` was polled unconditionally every sample; `SyncIntervalTracker.TryStart` unconditionally zeroes `_peakAmplitude` on any call where it's non-zero, so the very next sample after `Trigger()` set a fresh peak, `TryStart()` consumed and recorded it — anchoring every match at the threshold-crossing *edge*, before `SyncMax` (called later that same sample, since the latch update runs after `TryStart` in the loop) ever got a chance to track the pulse's true running max. Worse: with no gate, this also let VIS data-bit tones (1100/1300Hz, only ±100Hz from `d12`'s 1200Hz/100Hz-bandwidth center) spuriously re-trigger `m_sint1` throughout every VIS-bit-decode attempt — legacy's real case-2/9 freeze (never calling `SyncStart`/`SyncTrig` there) has no equivalent in this port's merged loop. Fixed by gating `TryStart()` behind `!_syncBypass1PrimaryHeld` (i.e. only in the port's own case-0-equivalent state), mirroring legacy's own `SyncStart` being polled from case 0 only, never case 1. `m_sint2`'s existing code already had this property "for free" (its held-branch condition subsumes its `TryStart` branch's own threshold, so a held `m_sint2` never calls `TryStart` either) — `m_sint1`'s bare top-of-loop poll had no such built-in protection, which is why only it needed the explicit fix. Re-measured `SyncBypass1DetectionTests` after the fix: 39.03 (up from an unverified, borrowed 29.0 tolerance) — worse, not better, for Robot 36 specifically, plausibly (not independently confirmed) because `m_sint1`'s stricter `SLvl` threshold lets the "held" window run into nearby-frequency image content under CLVL's hard clipping; tolerance bumped to 43.0 with this reasoning recorded honestly rather than as a proven mechanism.
        2. **AFC double-correction after a mid-reception restart is not bounded to "one line" as a prior review claimed — a real bug, fixed.** `ApplyAfcCorrections` ran once per outer-loop iteration, eagerly, all the way to `_afcBoundSample` — i.e. up to the *entire* abandoned transmission's own nominal image extent — before any of its lines were even decoded, and restart detection only happens per-line, well after that eager pass already ran. `InitializeAfc` then rewound `_afcProcessedUpTo` back to the new anchor unconditionally, letting the new `AfcTracker` re-correct (additively, not overwrite) content the old one had already corrected — for `MidReceptionRestartTests`' own 30%-truncation shape, potentially most of the second image. Fixed two ways: `ApplyAfcCorrections` now takes an `upperBoundSample` and is called once per line (bounded to that line's own extent, mirroring `ApplySlantTracking`'s existing per-line bound) instead of once eagerly for the whole image — a causal, resumable tracker produces identical results regardless of when the eager pass ran relative to decode, so this is a no-op for a transmission that completes normally, and restores the "at most one line" property the incorrect prior claim assumed was already true. `InitializeAfc` also now uses `Math.Max(_afcProcessedUpTo, _consumedSamples)` instead of a bare assignment (the same defensive pattern already used for `_visLockProcessedUpTo` in `Commit()`), closing the double-correction even if some future change reintroduces eager lookahead. Honestly not covered by a dedicated regression test: measured the fix's actual effect at the same realistic 500ppm mismatch `NarrowModeAfcTests`/`SlantTests` use (14.14 buggy vs. 13.68 fixed, effectively noise) — the same "doesn't cleanly discriminate at realistic severity" outcome already hit for the original AFC-bound fix and `TryDecodeNarrowModeHeader`'s own AFC bug, so no test was shipped claiming to prove this; verified by direct code trace instead, following the exact precedent piece 6a's own AFC-bound fix already set for this codebase.
        3. **Two doc-only fixes.** `_syncBypass1Tracker`'s doc comment claimed its latch and `VisLockStateMachine`'s own internal state "necessarily agree sample-for-sample" — true only pre-lock; while locked, only `VisLockStateMachine`'s detectors run (matching legacy's own `!m_Sync` gating), so the two detector pairs carry different filter histories by the time `EndOfImage` resyncs their cursors, and can genuinely disagree for a short settling window at the start of every transmission after the first — corrected to say so. A 9-line comment block in `TryDecodeNarrowModeHeader` that had been accidentally pasted three times in a row (copy-paste artifact, no behavior effect) was reduced to one copy.
        - **Two real findings, deliberately not fixed in this pass — logged as open follow-up items, not silently absorbed:**
          1. `PllFmDemodulator` never got the piece 7 scale-bridge treatment `LevelAgc` did — **fixed shortly after, see below.**
          2. ~~`m_sint1`'s decoder-level priority is effectively inverted from legacy's real per-sample interleaving~~ — **fixed.** `TryDecodeHeader` used to try the fixed-window path, then `VisLockStateMachine` over the *entire remaining buffer*, and only then `TrySyncIntervalDetection` (where `m_sint1` lives) — whereas legacy checks `m_sint1` first, every sample, genuinely before `VisLockStateMachine`'s equivalent (cases 1-3/9) ever gets a chance to accumulate a false positive. Deferred at the time specifically until a real audio engine existed to force a genuine streaming-architecture decision (`MiniAudioEngine`, this same session) — picked up once it did. Plan verified with Opus first (which caught real errors in the initial sketch: a wrong justification for why the two scan cursors stay in lockstep — the real invariant, confirmed by checking every `_mode` assignment in the file, is that `EndOfImage` is the *only* place `_mode` becomes null again, and it always resets both cursors together, not "they're always reset together" as first assumed — plus an off-by-one in the sketch's manual cursor increment on a match). Fixed by extracting `TrySyncIntervalDetection`'s per-sample body into `TrySyncIntervalDetectionStep()` and adding `TryInterleavedHeaderScan()`, which runs it and `VisLockStateMachine.ProcessSample` in lockstep, one sample at a time, in legacy's real per-sample order (`sstv.cpp:1897-1951`, re-read directly to confirm, not assumed from this note's own prior wording). New test (`SyncScanInterleaveTests`) proves it deterministically rather than trying to synthesize the false-positive risk directly: a headerless Robot 36 transmission (recognizable via `m_sint1` alone) concatenated directly before a header-included Martin M1 transmission — before the fix, `VisLockStateMachine` sweeps the whole buffer and finds Martin M1's real header first (confirmed empirically by temporarily reverting the fix and re-running the test, which fails with `martin-m1` detected first); after the fix, Robot 36 is recognized first, matching legacy's real behavior. All 193 pre-existing tests still pass unchanged (verified by running the suite, not just reasoned about) — none of them contain a competing earlier sync-bypass match, so `VisLockStateMachine` still resolves each of *their* transmissions at the same absolute sample index it always did. Two things intentionally left open, not silently closed: legacy's case-1/2/3/9 *gating* of `m_sint1`/`m_sint2`/`m_sint3` (the port's merged loop still runs them unconditionally, already-documented pre-existing divergence, unchanged by this fix) and the "deliberate second copy" of the d12/d19 envelope detectors between `TrySyncIntervalDetectionStep` and `VisLockStateMachine` (now running interleaved, so unifying them is possible, but changes `VisLockStateMachine`'s public shape — left as a candidate follow-up, not attempted here).
            - **Opus review round 1 (of this fix specifically), findings independently re-verified before fixing.** Core mechanic confirmed correct (cursor lockstep invariant, legacy per-sample order, AGC index consistency, no skipped/double-processed sample, chunk-size invariance) — nothing there needed changing. Real findings, all fixed: (1) the cursor-equality check was a bare `Debug.Assert`, stripped entirely by `ci.yml`'s `--configuration Release` builds and therefore providing zero real protection in CI (the only `Debug.Assert` anywhere in this codebase) — replaced with an unconditional `InvalidOperationException` check; (2) a real, previously-unnoticed second behavior change: a sync-bypass match's `_visLockOriginSample` used to land at the buffer's end (`Commit`'s `Math.Max` against a `_visLockProcessedUpTo` already exhausted by the old sequential `TryVisLockStateMachine(_rawSamples.Count)` scan), now lands near the actual lock point instead — which means piece 6c's mid-reception re-verification, previously an accidental no-op for the rest of a bulk-pushed sync-bypass-locked transmission, now actually runs, extending `VisLockStateMachine`'s own already-accepted false-positive risk to a scenario it was previously (accidentally) immune to; closer to legacy's own case-0 trigger's behavior *at its shipped defaults* — not a universal legacy truth, since the whole switch only runs while locked because of the enclosing `sstv.cpp:1889` gate (`!m_Sync || m_SyncRestart || m_SyncAVT`), satisfied by `m_SyncRestart` defaulting to 1 (`sstv.cpp:1486`), a real user-toggleable option this port hard-wires on — not a regression, but a real reachability change that went undocumented until this review — now documented on `Commit`'s own comment rather than left as a silent side effect, and (per this project's own established precedent for that same pre-existing risk) not chased with a dedicated false-positive-forcing fixture. (3) An overclaiming comment ("this is a no-op for any real-header transmission") contradicted by this fix's own test — corrected to the narrower, actually-true claim (VisLockStateMachine's internal stepping is unaffected; the first-*detected* mode can and does change whenever a sync-bypass tracker matches earlier). (4)/(5) a wrong legacy file citation (`sstv.cpp` → `Main.cpp`) and a stale field comment whose justification the fix itself had already invalidated, both corrected at their original locations. Test strengthened per review: pinned the exact detected-mode sequence and a zero-restart count (measured directly, not assumed) instead of a looser "contains" check, and added a chunked-`PushSamples` variant proving the fix's own chunk-size invariance claim empirically rather than leaving it as a reasoned-but-unverified assumption. 194/194 tests pass (195 after the chunked variant), solution-wide build clean.
            - **Opus review round 2 (of round 1's own fixes), findings independently re-verified before fixing.** Round 1's core fixes held up under direct tracing (the `InvalidOperationException` fires under the exact same condition the old `Debug.Assert` did, with no gap and no reachable false trigger; the `Commit` `Math.Max` reachability claim was re-derived by hand for a concrete sync-bypass-match scenario and confirmed correct; the chunked-streaming test genuinely exercises different per-sample state, though it would likely pass pre-fix too since 1024 samples is nowhere near enough head start to matter, so it's chunk-size-invariance coverage, not a second discriminating regression test). Real findings, fixed: (1) the "permanently-ungated" wording this review round's own round-1 predecessor introduced on `Commit`'s comment (and one pre-existing occurrence of the same imprecision on piece 6c's own inline comment, and in this file above) was wrong — `sstv.cpp:1897`'s switch is gated by `sstv.cpp:1889`, and only looks unconditional at legacy's shipped defaults (`m_SyncRestart = 1`) — corrected at all three locations with the precise gate/default-value citation rather than restated as loose shorthand; (2) `Commit`'s first two paragraphs still described pre-fix control flow (referring to `TryDecodeHeader` "falling through to `TryVisLockStateMachine`," and enumerating the non-self-triggering paths without the sync-bypass case that round 1's own third paragraph had to reintroduce as an unlisted "third case") — folded into one accurate enumeration; (3) a pre-existing wrong citation on piece 6c's inline comment (`sstv.cpp:1949/1953/1959` attributed to "`m_sint2` and `m_sint3`" when 1949/1959 are actually `m_sint1`'s own gates) — corrected to the real `m_sint2`/`m_sint3` gate lines (`sstv.cpp:1899`/`:1953`, and `:1927-1937` for `m_sint3`); (4) a test-count typo in this file's own round-1 entry (196 → 195, only one test method was added); (5) a forward-looking note added to the `InvalidOperationException` comment that `MiniAudioCaptureSession`'s bare `try/catch` around `SamplesAvailable` would silently swallow this exception once a production caller wires the decoder to real capture (no such caller exists yet); (6) added `PiecesSixCReachabilityTests`, a positive-engagement check that piece 6c's mid-reception re-verification actually fires for a sync-bypass-locked transmission (round 2's proposal) — `SyncScanInterleaveTests`' zero-restart-count assertion only proved the newly-reachable path doesn't misfire on the exact fixture (Robot 36) `VisLockStateMachine`'s own doc comment names as the false-positive risk case, not that the path engages at all; this test truncates a sync-bypass-locked Robot 36 transmission mid-body and confirms Martin M1 is still separately detected via its own real header, with `restartCount == 1`. 196/196 tests pass (up from 195), solution-wide build clean in both Debug and Release configurations.
            - **Opus review round 3 (final, of the 3-round cap), findings independently re-verified before fixing.** Re-derived round 2's `m_SyncRestart`/`sstv.cpp:1889` gating claim and its `m_sint2`/`m_sint3` citation fix directly from source (both confirmed exact, line-for-line) and worked through `PiecesSixCReachabilityTests`'s timing arithmetic by hand (Robot 36's m_sint1 lock at ~754.5ms leaves ~7x margin before the 30% truncation point; the `restartCount == 1` assertion is what actually discriminates the bug, not the mode sequence — confirmed the counterfactual would produce `restartCount == 0` instead). One real finding, fixed: `SyncScanInterleaveTests`' own doc comment (introduced in round 1, missed by round 2) claimed Martin M1 is detected post-fix "via its own real header at `EndOfImage`" — wrong; `EndOfImage`'s 500ms skip lands past M1's real header window in this fixture, so it's actually found via the sync-interval bypass's periodicity (M1 is a `SyncBypassTrustedModes` member), the same way a real headerless capture would find it — corrected, plus a note recording the `restartCount == 0` assertion's ~145ms fragility margin so a future maintainer doesn't misdiagnose an unrelated timing change as a regression in this fix. Also fixed a smaller staleness round 2 left behind: `Commit`'s comment said a sync-bypass match "doesn't touch this cursor on its own" when enumerating the "still at 0" case — wrong, `TryInterleavedHeaderScan` does advance `_visLockProcessedUpTo` in lockstep for every sample including a sync-bypass match; narrowed the "still at 0"/"never touched" framing to only the fixed-window/narrow/AVT paths, which is where it's actually true. Two optional/cosmetic findings (a very minor `VisLockStateMachine` doc-comment attribution nuance, and this file's lack of a note on `_rawSamples`/`_demodulatedFrequencies`/`_agcSamples`' unbounded memory growth — ~1.1GB/hour at 11025Hz, ~4.4GB/hour at 44100Hz, harmless for today's test-only callers but relevant once a production caller wires this decoder to real capture, the same moment round 2's `MiniAudioCaptureSession` caveat already flags) were not required fixes; the memory-growth note is now logged here as a companion to that caveat, since no such note existed anywhere in `src/` or `spec/` before this. No decode-logic changes in this round — all three rounds combined found nothing wrong with the fix's actual behavior, only with how it was documented. 196/196 tests pass, solution-wide build clean in both Debug and Release configurations. This closes the 3-round review cap for the m_sint1 decoder-ordering fix.
        - Full suite after these fixes: 192/192 in `ScanlineStudio.Core.Sstv.Tests` (unchanged count -- one test's tolerance re-measured, one exploratory test written then dropped after measurement showed it didn't discriminate cleanly), solution-wide build clean.
      - **Opus sequencing consultation: what to tackle next, now that the 7-piece breakdown and its holistic review are both done.** Candidates weighed: closing the 11025Hz mode-table gap (below), fixing finding 2 above, porting legacy's pre-AGC input chain (the piece 7 gap), legacy's fine-pixel-alignment bootstrap (`m_wBgn`), or moving to Phase 2 (radio layer). The consultation surfaced two things that reordered the whole list, both independently verified before trusting them:
        - **Phase 1 isn't actually done.** `ScanlineStudio.Core.Audio` has no real backend, only `FakeAudioEngine`/`WavFile` (confirmed: `ls` shows exactly those two files). Zero fixture/golden-vector directories exist anywhere in the repo (confirmed: `find` for `*fixture*`/`*golden*` returns nothing) — the item this file's own Phase 1 bullet flags as "only gets harder to justify later" has never been started. Stepping outside SSTV DSP should mean one of *these* two, not jumping ahead to Phase 2.
        - **The 11025Hz mode-table gap is currently unactionable, not just unfixed.** The 10.0-average-per-channel-delta tolerance it's measured against is this project's own invented bar (`SstvRoundTripTests`'s own class comment already says as much), never checked against what legacy's own decoder actually achieves for the same input. Three of the four bugs the very first adversarial review caught (chroma +128, Scottie's missing post-VIS pulse, AVT's entire missing preamble) were invisible to round-trip specifically because self-consistency can't catch "both sides agree with each other but disagree with legacy" — and this gap has never been checked against a real legacy reference at all.
        - **Golden-vector capture — done, and the mode-table gap above is now measured, not just suspected.** Martin M1 as a "does the methodology work" baseline, Robot 36 as the actual worst-case data point for the 11025Hz question. The user captured real TX/RX fixtures via a working legacy YONIQ install: `Sound.cpp`'s `CWaveFile::Rec`/`Play`, transmitting two source images generated matching this suite's own gradient-fixture formula exactly. Two corrections to how this capture mechanism was originally described here, found and fixed while building the test harness (`GoldenVectorTests.cs`, `tests/ScanlineStudio.Core.Sstv.Tests/Fixtures/GoldenVectors/`): (1) `.mmv` is **not** RIFF/WAV — confirmed directly against `Sound.cpp:638-656`/`564-611`: a 4-byte header `[0x55,0xAA,SampType,0]` followed by raw little-endian `int16` samples, no chunk structure at all; (2) it is **not** "no soundcard loopback" either — `Sound.cpp:325-395`'s loop shows `Wave.InClose()` (`:395`) only closes the sound-card input while transmitting, so the captured file's pre/post-TX portions are real recorded sound-card/mic audio, and even the TX portion itself is the *previous* loop iteration's modulator output (a one-buffer delay, with the modulator's final buffer never captured) rather than a clean same-iteration tap. Neither error affects the golden-vector results below (the decoder's sync-search finds the header regardless of what precedes it), but both were live, uncorrected claims in this file until now. Fixtures moved from the repo root (they were plain untracked files, not gitignored — a third stale claim in the original version of this bullet) into `tests/ScanlineStudio.Core.Sstv.Tests/Fixtures/GoldenVectors/` per [[13-testing]]'s own established fixture-directory convention, with a new `LICENSES.md` entry (the `.mmv`/`_RX.bmp` files are outputs of running the legacy binary, which CLAUDE.md's license-audit rule covers) and a `README.md` documenting exact capture/measurement provenance.
        - **Measured results, closing the loop this bullet's own predecessor left open.** Legacy's own decode-vs-source baseline (zero C# DSP): robot-36 = 6.99, martin-m1 = 1.57 average per-channel delta — the first real answer to "what does legacy's own decoder actually achieve on this input," replacing the invented-bar problem named above. C# decoder decoding the real captured audio, vs. source: martin-m1 = 11.78 (close to both legacy's own baseline and this suite's usual 10.0 synthetic-round-trip tolerance); **robot-36 = 68.06 — the suspected 11025Hz/low-samples-per-pixel gap, now confirmed against a real legacy capture, not just a synthetic experiment.** An image-domain encoder cross-check (self-encoded-then-self-decoded vs. real-audio-decoded, avoiding a signal-domain PCM comparison that the capture-mechanism corrections above show wouldn't even be clean) landed close to the same numbers (martin-m1 = 13.66, robot-36 = 60.89), consistent with the gap being in shared decode-side DSP (AFC/PLL settling, per the existing suspicion two bullets up) rather than an encoder-specific bug. All golden-vector tests assert regression-guard tolerances only, explicitly documented as not parity claims, per this bullet's own anti-invented-bar rule. Fixing the underlying Robot-36-at-11025Hz gap itself is deliberately out of scope here (test-code-only pass, by explicit user instruction) — remains open follow-up work.
          - **Round-1-review finding on this same data, magnitude not previously noted.** This same gap's own synthetic self-round-trip number, measured two bullets up after the single-sample-readout fix, is 13.4 — roughly **5x smaller** than the 68.06 measured here against real captured audio. The gap's *existence* was already known (this bullet's own predecessor, and `SstvModeRegistry.cs`'s doc comment); the *magnitude asymmetry* was not, until independent review caught it. A mode that scores 5x worse against real legacy audio than against the port's own self-generated signal is exactly the "encoder and decoder agree with each other while both being wrong about reality" failure mode CLAUDE.md's behavioral-parity rule exists to catch — worth flagging as its own follow-up question (is the gap actually AFC/PLL settling, or something specific to real analog/soundcard characteristics the synthetic self-test never exercises?), not folded silently into the already-known bucket. Also found and fixed in the same review round: the 75.0 robot-36 tolerance is not meaningfully discriminating on this specific fixture — measured that a flat gray image, a mirrored copy, a flipped copy, and a channel-swapped copy of the source all score ~42.67 under the same metric (this gradient image is smooth enough that most structural corruptions land in a narrow band), and robot-36's real measured deltas (68.06 / 60.89) are already worse than that floor — so no tolerance can currently both accept today's real output and reject a structural bug for robot-36 specifically. Kept as a regression tripwire only, documented as such directly on both tests; martin-m1's tolerances remain genuinely discriminating. A timing-check test in the same file (TX-region duration vs. expected transmission length) had also mis-attributed a real, fixed-duration legacy TX segment (`TMmsstv::OutHEAD`'s 800ms leader tones, `Main.cpp:7270-7292`/`:7393`, plus `SendSSTV`'s own ~550-846ms footer, `Main.cpp:6996-7009`) to "envelope-detection slop" — corrected to account for both segments directly from source, tightening that test's tolerance from 5.0s to 1.0s (the old bound was wide enough to accept a ~30%-wrong `LineDurationMs` as passing). A minor aliasing fragility was also fixed: `LineDecoded`'s image reference is the decoder's own live pixel buffer, safe to read after the fact today only because `Commit()` happens to always allocate a fresh array per lock — now copied into an explicit snapshot in the test instead of relying on that invariant.
          - **Round-2-review finding: round 1's own timing fix had a real bug, not just a rough edge.** `SSTVSET.m_TW`, the variable round 1's footer-duration fix keyed off `mode.LineDurationMs` for, is not the transmitted mode's timing at all — traced directly against source: `m_TW` is set by `CSSTVSET::SetSampFreq` (the RECEIVE side, `sstv.cpp:655-1109`) as `GetTiming(m_Mode) * m_SampFreq / 1000.0`, where `m_Mode` is the demodulator's currently-selected mode; the TX-side equivalent is a genuinely separate field, `m_TTW` (`CSSTVSET::SetTxSampFreq`, `sstv.cpp:1280-1285`), which `SendSSTV`'s footer does not use. So this footer segment's length depends on whatever mode the RX side happens to be sitting on when TX finishes, not on the mode being sent — confirmed empirically that both fixtures show the same ~425ms footer segment despite transmitting different modes, consistent with both captures' RX side sitting at `GetTiming`'s own `default:` case (smSCT1, 428.22ms, `sstv.cpp:1275`) the whole time. Using `mode.LineDurationMs` was only coincidentally close for these two specific fixtures (both well under the 500ms clamp); it would have been actively wrong for a mode whose duration differs meaningfully from 428.22ms. Fixed with a named constant tied to the real legacy default instead. Same review round also found: `MeasureTxRegion`'s own envelope-window-to-seconds conversion mixed a nominal 50ms constant with the actual (slightly different, `(int)(0.05*11025)=551`-sample ⇒ 49.977ms) window size, a systematic scale error proportional to elapsed time — fixed by deriving the conversion from the real window size. With both fixed, the timing test's residuals dropped to 0.04s/0.02s (from an already-corrected-but-still-off 0.34s/0.05s), and its tolerance was tightened again, from 1.0s to 0.2s. Also found: round 1's own fixes to the fixture `README.md` never landed — the file still stated the pre-round-1 "envelope-detection slop" explanation and the pre-round-1 "known gap, not new information" framing for the 5x-magnitude finding, verbatim, even though both were corrected in `GoldenVectorTests.cs` and here in this file. Propagated all round-1 and round-2 corrections to the README, including the corruption-floor measurement it was previously missing entirely. Two smaller findings, also fixed: the class-level doc comment's claim that the timing/cross-check test pair "capture most of the realistic value of an encoder check" was left unqualified for both modes even though round 1's own per-test comment already said robot-36's arm doesn't meaningfully discriminate — narrowed to say so explicitly; and a comment overstated `Snapshot()`'s protective value as if it addressed a hazard that in fact remains present at roughly 15 other `LineDecoded` capture sites across this test project — corrected to state its actual (narrower) scope, with a note that enforcing the underlying invariant at the decoder itself would be the more robust fix, left as separately-scoped follow-up. 208/208 tests pass, solution-wide build clean in both Debug and Release configurations.
          - **Round-3-review (final, of the 3-round cap): re-derived round 2's own fix from source independently, confirmed it correct, but found its stated justification was itself inaccurate.** Re-traced `m_TW`/`m_TTW`, `WriteC`'s sample-count units, and `GetTiming`'s `default:`↔`smSCT1` correspondence directly from `sstv.cpp`, and independently measured a ~429ms 1500Hz footer tone in both real captures via a from-scratch frequency trace — confirming round 2's fix and its 428.22ms constant are genuinely correct for these two fixtures. But the comment's claim that the RX side was sitting at "the demodulator's default/never-changed mode on a fresh run" is not accurate as a general statement: `Main.cpp:1872-1873` shows `SSTVSET.m_Mode` is reseeded from the *persisted* `Define/SSTVMode` ini value on every load, not fixed at a constructor default — the 428.22ms value holds for these two captures only because that persisted value happened to be Scottie 1 (matching the constructor default) during this capture session, not because the value can never change. Reworded the comment to state this as a fact about these specific committed fixtures, not a property of legacy in general, and renamed the constant (`receiveSideDefaultLineDurationMs` → `receiveSideModeLineDurationMsForTheseFixtures`) to make that scoping visible at the call site too. Also found: trim work done in this same session (below) had updated the `.mmv` fixtures but left `GoldenVectorTests.cs`'s TX-duration comment holding pre-trim numbers, and the fixture `README.md`'s new trim section overclaimed that *all* measured values (including TX-region duration specifically) were "found unchanged" post-trim — false at the precision the duration test actually asserts, since trimming moves the TX region's absolute offset within the file and therefore re-phases the fixed-size envelope-detection window grid against it (a real, expected, sub-window-size effect, not a timing regression). Both corrected with the actual re-measured post-trim numbers. No further findings beyond doc-accuracy — round 3 explicitly confirmed the three-round streak of "each round finds a real bug in the previous round's fix" ends here; the harness itself was sound as committed after round 2, only its comments needed one more accuracy pass. 208/208 tests pass, solution-wide build clean in both Debug and Release configurations. This closes the 3-round review cap for the golden-vector test harness.
      - **`PllFmDemodulator`'s own missing scale-bridge — fixed while golden-vector capture was in progress** (not blocked on it, per the consultation above: independently verifiable by direct math against `sstv.cpp:316-343`, no legacy reference needed). Confirmed by re-deriving both sides of the AGC formula: legacy's `CPLL::Do` resets its own tracking window to `m_Max=1.0/m_Min=-1.0` every half-cycle too (the *same* literal floor this port already used) — at legacy's real int16-scaled full-scale input the floor never binds and the AGC adapts normally, while this port's float `[-1.0,1.0]` full-scale input sits *exactly* at the floor, which is why both sides coincidentally converge to the identical effective gain (`5.0/2.0=2.5`) at full scale, and why every existing fixture (all full-scale) never caught the gap. Below full scale, though, this port's floor never released — the AGC stayed pinned at 2.5 regardless of actual amplitude, giving this port's PLL effectively no AGC at all below roughly `-8dBFS`, unlike legacy's. Fixed with the same `raw * 32768.0` scale bridge `LevelAgc`/`AgcSampleAt` already established as this port's convention, applied at both real call sites (`AnalogFmSstvDecoder.PushSamples`, and `AvtTrainingLockStateMachineTests`' own direct construction, updated for consistency though a no-op there at that test's own full-scale amplitude). New `PllScaleBridgeTests`: decoding a -20dBFS-attenuated Martin M1 transmission, comfortably within the standard 10.0 tolerance with the fix, and confirmed (not assumed) to actually discriminate the bug by temporarily reverting it and re-measuring — 58.55 average per-channel delta without the fix, badly over tolerance, one of the cleanest-discriminating regression tests added this session. 193/193 tests pass, solution-wide build clean.
      - **Robot-36-at-11025Hz decode-gap investigation — STARTED, live log, not yet resolved.** User flagged this as sensitive/correctness-critical work and asked for extra rigor: explicit legacy-fidelity framing in every step, incremental testing (verify each sub-step before moving on), a higher-effort agent instead of Opus for wide investigative passes, and this log kept current at every step (not just a final summary) so the work is resumable cold, in a brand-new session, without any conversational context.
        - **Why this is the current target**: the golden-vector harness above (`GoldenVectorTests.cs`) just measured a real, previously-only-synthetic gap concretely: Robot 36 decoded from real captured legacy audio at 11025Hz scores **68.06** average-per-channel delta against source — 5x worse than the existing synthetic self-round-trip number for the same mode/rate, **13.4** (`SstvModeRegistry.cs`'s own doc comment, `spec/14-roadmap.md` above). A mode that agrees with the port's own encoder far better than with real legacy audio is exactly the "both sides wrong about reality the same way" failure CLAUDE.md's behavioral-parity rule exists to catch.
        - **What's already ruled in/out, from before this investigation started** (do not re-litigate without new evidence): the single-sample-readout fix (replacing invented block-averaging with legacy's real `GetPixelLevel`/`GetPictureLevel` per-pixel dereference, `Main.cpp:4144-4148`) already improved this mode from 21.3→13.4 on the synthetic test but did not close the gap. `AnalogFmSstvDecoder` already has AFC (`AfcTracker`/`ApplyAfcCorrections`), Auto Slant (`SlantTracker`), CLVL AGC (`LevelAgc`), and the full 7-piece VIS/preamble-lock system ported and tested (session commits before this log entry: `0d25ca4`, `03394b3`, `557e9c8`, `cbc1ec4`, `5006fc7`) — so "AFC not yet ported" is STALE as a hypothesis; if AFC is implicated, the bug is in what's already ported, not in a missing piece. `PllFmDemodulator`'s own scale-bridge bug (an AGC floor that never released below `-8dBFS`) was already found and fixed independently (bullet above, `PllScaleBridgeTests`) — ruled out as *the* cause of this specific gap since that fix didn't move the Robot-36 number, but worth keeping in mind as a nearby, related area.
        - **Leading hypotheses, not yet confirmed**: (a) `PllFmDemodulator`'s settling speed at Robot 36's very short per-pixel sample dwell time (`SstvModeRegistry.cs`'s own doc comment names this explicitly as a candidate, alongside AFC); (b) something specific to *real* analog/soundcard capture characteristics that the synthetic self-round-trip test never exercises (real capture noise floor, real clock drift, real ADC quantization) that a purely synthetic self-encode-then-decode test is structurally blind to; (c) not yet ruled out: an actual bug (not just "needs more precision") somewhere in the already-ported AFC/slant/AGC pipeline that only manifests at Robot 36's short dwell time. No investigation into any of these has started yet as of this log entry.
        - **Test oracles available for this investigation**: `SstvRoundTripTests` (synthetic self-round-trip, already existing), and the new `GoldenVectorTests` (real captured legacy audio, `Decoder_DecodesRealLegacyAudio_WithinToleranceOfSource` for Robot 36 specifically) — the latter is the more important oracle here, since the whole point is closing the synthetic-vs-real gap, not just improving the synthetic number again.
        - **Next planned step (not yet done)**: read `CSSTVDEM`'s actual per-sample demodulation path (`sstv.cpp`, particularly `CPLL::Do` and the `SyncFreq`/`m_AFCDiff` AFC loop) side-by-side with this port's `PllFmDemodulator`/`AfcTracker`/`AnalogFmSstvDecoder` implementations, specifically hunting for any behavioral difference that would matter more at short (Robot-36-scale) dwell times than at long ones — this is a wide comparative-audit task, a candidate for a higher-effort investigative agent pass per the user's explicit instruction, before forming a concrete fix hypothesis to bring to an Opus plan-review.
        - **ROOT CAUSE FOUND (high confidence, independently verified against source, not just agent-reported).** A dedicated Opus-powered investigative agent pass (read-only, no code changes) found and I independently re-verified: this is a fixed-sample-count HEADER-ANCHOR error, not a PLL/AFC/settling problem. On real captures, `AnalogFmSstvDecoder.TryDecodeHeader`'s exact fixed-window VIS path (`AnalogFmSstvDecoder.cs:424-500`) can never succeed — real legacy TX always precedes the VIS header with `TMmsstv::OutHEAD()`'s 800ms of leader tones (`Main.cpp:7270-7292`, called unconditionally from `Main.cpp:7393`) plus ~1s of real pre-TX room audio (already documented on the golden-vector fixtures) — so the `[320,380]ms` discriminator window lands in noise, decodes to a byte outside `SstvModeRegistry`, and detection always falls through to the approximate `VisLockStateMachine` anchor instead. That approximate anchor carries a fixed lag (already known/named in this file at the "candidate 8th piece" entry a few bullets up: `VisLockStateMachine`'s own ~7ms trigger-lag imprecision) — measured directly against both real fixtures via a faithful line-by-line C# reimplementation of `LevelAgc`/`AgcSampleAt`/`TankFilter`/`SyncEnvelopeDetector`/`VisLockStateMachine`: **+94 samples (8.53ms) for robot-36, +96 samples (8.71ms) for martin-m1 — essentially mode-independent**, exactly as expected for a fixed-sample-count error. A synthetic self-round-trip test never exercises this path at all (the port's own encoder emits the VIS leader at sample 0 with nothing before it, so the exact fixed-window path always succeeds there) — this is the actual, structural reason real-audio delta is so much worse than the synthetic number, not "real audio is noisier."
          - **Why this hits Robot 36 ~5x harder than Martin M1**: a fixed +94-sample error costs `94 / samplesPerPixel` pixels of shift. Martin M1 (5.045 samples/px): 18.6px (5.8%). Robot 36 luma (3.032 samples/px): 31.0px (9.7%); Robot 36 chroma (1.516 samples/px): **62.0px (19.4%)** — plus Robot 36's luma and chroma scans have different pitches, so the error also introduces real Y/chroma misregistration Martin M1 structurally cannot have.
          - **Robot 36's second, catastrophic-magnitude consequence — verified independently, not just agent-reported**: `RobotScanlineDecoder.cs:71`'s tone-selector read (`sampleFrequencyAt(endSample - 1, endSample)`, the LAST sample of the ~2.25ms color-select tone window) is a faithful, correct port of legacy's real per-sample `m_DSEL` re-decision loop (`Main.cpp:4286-4297` re-evaluates every sample in `[m_SG, m_CG)`, so the final value is definitionally "whatever the last sample decided" — confirmed by reading that switch case directly) — **the read design itself is not the bug**. But with legacy's real anchor (via `SyncSSTV`, see below) that boundary always lands inside the true tone; with this port's un-corrected +94-sample-early anchor, the read point lands ~7ms into the FOLLOWING chroma scan instead, reading arbitrary chroma-scan content near the tone's ambiguity threshold — measured on the real capture: the true selector tone reads decisively (1499/2299/1501/2300...Hz) but the port's actual (anchor-shifted) read point gives ambiguous deviations on every single line, so `_lastSelectionIsEvenLine` toggles every line starting from line 0 — **R-Y and B-Y are swapped on every line of the decoded image**. This is why Robot 36's delta (68.06) clears even the ~42.67 "flat gray image" corruption floor documented on the golden-vector tests: swapped chroma + 62px chroma shift + 31px luma shift, stacked. This is a downstream CONSEQUENCE of the anchor error, not a second bug needing its own separate fix.
          - **The actual missing piece — confirmed present in legacy, confirmed absent from this port, by direct source reading (not agent-reported, read myself)**: legacy re-anchors the per-pixel phase from a MEASURED sync-pulse envelope peak before ever drawing a single pixel, and this port has no equivalent at all.
            - `TMmsstv::DrawSSTV` (`Main.cpp:4917-4986`) gates ALL pixel drawing behind `dp->m_wBgn`: while it's set, it returns immediately after calling `SyncSSTV()` (`Main.cpp:4980-4982`, `dp->m_wBgn = 1; SyncSSTV(); if (dp->m_wBgn) return;`) — confirmed by reading the function directly.
            - `TMmsstv::SyncSSTV` (`Main.cpp:3751-3799`, confirmed by reading the function directly): once `e` lines' worth of sync-envelope data (`m_B12`, `e` = 4 normally, or 3 under `m_SyncAccuracy && sys.m_UseRxBuff && m_TW >= m_SampFreq`) have been buffered, it FOLDS that envelope across `e` lines modulo `m_TW` into one line's worth of bins, takes the **argmax** bin, subtracts `m_OFP` (a per-mode constant, e.g. 10.7ms for Robot 36, `sstv.cpp:663`), negates, and assigns the result to `SSTVSET.m_IOFS = SSTVSET.m_OFS = dp->m_rBase` (`Main.cpp:3795`) — THIS becomes the real per-pixel timing anchor for the rest of the image, replacing whatever approximate anchor got the decoder into image-mode in the first place. Only then does `m_wBgn` clear and pixels start drawing.
            - `CSSTVDEM::Start()` (`sstv.cpp:1717-1747`, confirmed by reading directly) is what arms this: sets `m_wBgn = 2` (armed, no one-time setup done yet) at the start of a new reception.
            - This mechanism is ALREADY logged in this file as an unported gap (search "m_wBgn" a few bullets up — "legacy's own fine-pixel-alignment bootstrap... is unported and is the real fix for m_sint2/m_sint3's midpoint-approximation anchor imprecision..., VisLockStateMachine's own smaller ~7ms trigger-lag imprecision...") and was explicitly weighed as a candidate next step in the Opus sequencing consultation (also a few bullets up) but deprioritized at the time in favor of the golden-vector capture work that led directly to this investigation. This is that candidate 8th piece, now with a concrete, measured, high-confidence justification for why it matters.
          - **Explicitly ruled out this pass** (re-derived independently, not just agent claims): real-audio noise/ADC characteristics (the captures are clean, band-limited, full-scale, negligible DC — directly measured); PLL settling/filter-chain mismatch at short dwell (a faithful `PllFmDemodulator` replica run on the real capture with a CORRECTED anchor scores 6.05, matching legacy's own 6.99 baseline — the demodulator itself is fine once given the right anchor); AFC/`SyncFreq`/`m_AFCDiff` (no meaningful carrier offset measured in these captures, and the corrected-anchor replica already matches baseline with no AFC modeled at all); `PllFmDemodulator`'s scale-bridge bug (already fixed, irrelevant at this fixture's full-scale amplitude).
          - **Secondary, smaller, independently-source-verified divergences found in the same pass — real, but NOT this symptom's cause, logged for later**: (1) legacy's shipped DEFAULT demodulator is actually the Hilbert-transform path (`CHILL`, `m_Type=2`, `sstv.cpp:1492`, confirmed the ctor default and `Main.cpp` INI-fallback/profile-8 default both agree), not the PLL — this port only implements the PLL path, and `AnalogFmSstvDecoder.cs:34-37`'s comment claiming legacy switches PLL bandwidth between VIS/image is FALSE (already contradicted by this file's own line ~45) and needs correcting separately; (2) `PllFmDemodulator` is configured 1100-2300Hz vs legacy's real `CPLL` 1500-2300Hz (`sstv.cpp:1431`), a 1.5x loop-gain difference; (3) `GetPictureLevel` (luma only) is not a bare dereference — it peak-picks the brighter of two samples `m_KSB` apart (`Main.cpp:4057-4071`, `m_KSB`=1 for Robot 36 at 11025Hz) — `SstvModeRegistry.cs`'s doc comment claiming a bare `*ip` dereference is inaccurate on this specific point (chroma/tone-select DO use the bare read, `GetPixelLevel`, correctly); (4) horizontal pixel pitch uses legacy's `m_KSS` (`m_KS - m_KS/240` for Robot 36, `sstv.cpp:1157`), not `m_KS` — this port's `RobotScanlineDecoder.DecodePixels` uses the equivalent of `m_KS`, a ~0.42% horizontal scale error; (5) legacy's pre-AGC input chain (bandpass filter feeding the demodulator, `sstv.cpp:1824-1833`, already logged elsewhere in this file) is confirmed measurably non-causal for THIS fixture specifically (clean, band-limited, full-scale capture) even though it remains a real, separately-logged gap in general.
          - **Next planned step**: scope a plan to port `CSSTVDEM::Start`'s `m_wBgn` arming + `TMmsstv::SyncSSTV`'s envelope-fold-and-argmax re-anchor into `AnalogFmSstvDecoder` (piece 8 per this file's own existing numbering) — get an Opus plan-review before writing code (explicitly reminding it: follow legacy exactly, no invention, and to stay tightly scoped/no tangents per this project's collaboration note), implement incrementally with both `SstvRoundTripTests` and `GoldenVectorTests` as oracles (test after each sub-step, not just at the end), then the usual up-to-3-round Opus review cycle on the finished piece. Not yet started as of this log entry. **UPDATE**: plan drafted and sent to Opus for plan-review (not yet returned as of this log entry). Draft plan: a new step, tentatively "piece 8", inserted between `Commit()` and the start of per-line decoding -- buffers `e=4` lines' worth of the already-existing `SyncEnvelopeDetector` output (the same d12/d19 infra `ApplySlantTracking` already reuses) starting at `_consumedSamples`, folds into per-mode-line-width bins (mirroring legacy's `m_B12` fold, `Main.cpp:3767-3774`), argmaxes, applies the `n -= m_OFP; n = -n` transform (plus the SCT-family wraparound case; Hilbert-tap term omitted since this port has no Hilbert demodulator), and applies the result as a one-time correction to `_consumedSamples` before any line for the newly-locked image is decoded -- confirmed via full trace of `dp->m_rBase`'s every use (`Main.cpp`/`sstv.cpp`, all citations) that this mirrors legacy's real `m_rBase` semantics (a running absolute pixel-draw sample cursor, corrected once per image before drawing starts, advanced by `SSTVSET.m_WD` per page thereafter -- `Main.cpp:5013`). Open questions sent to Opus: exact `m_OFP` per-mode source/values for Robot 36 and Martin M1, whether the correction is additive/relative to the pre-correction anchor or a replacement, whether hardcoding `e=4` (skipping legacy's UI-config-dependent `e=3` branch) is safe, interaction with `SlantTracker`/piece-6c mid-reception restarts, and how to unit-test the fold-argmax logic in isolation before wiring it in.

**Opus plan-review result, independently re-verified against source before trusting (all confirmed exact):**
- **The draft plan's sign was backwards** -- caught before any code was written. `m_rBase` is a PHASE relative to the fold's own origin (`Start()` zeroes it, confirmed `sstv.cpp:1725-1731`), not an absolute sample cursor -- correct formula is `_consumedSamples += (argmaxBin - ofpSamples)`, not `+= (ofp - argmax)`. The draft's version would have doubled the error in the wrong direction.
- **`SstvModeRegistry.GetSyncSegmentOffsetMs` is NOT usable for this** -- its own doc comment says so explicitly (a deliberate substitute for `SlantTracker`'s relative-only needs, not `m_OFP`'s real value). Needs a NEW per-mode table, transcribed from `sstv.cpp:657-1108`. Verified exact for the two fixtures: Robot 36 `m_OFP=10.7ms` (`sstv.cpp:663`, case `smR36`), Martin M1 `m_OFP=7.2ms` (`sstv.cpp:716`, case `smMRT1`).
- **Real decoy found and verified**: `TMmsstv::AdjustSyncPos` (`Main.cpp:5428-5480`) opens with the IDENTICAL `n -= m_OFP; n = -n` + Scottie-wrap lines as `SyncSSTV`, then adds mode-specific fudge terms (e.g. `smR36/smR72: +0.16ms`, `smMRT1: +0.45ms`) that `SyncSSTV` does NOT have -- confirmed by reading both functions. A grep for the formula could easily land on the wrong one; `SyncSSTV` (`Main.cpp:3751-3799`) is the correct, decoy-free source.
- **`e=4` hardcode was an unnecessary simplification, not a safe one**: both gating settings default ON in legacy (`m_SyncAccuracy=1` `Main.cpp:730`, `sys.m_UseRxBuff=1` `Main.cpp:899`, both confirmed), so the real condition reduces to `LineDurationMs >= 1000 -> e=3, else e=4` -- a one-line exact port, reachable for Scottie DX/PD240/MP140/MP175/MN140 (not either fixture, but a real divergence for other modes if skipped).
- **Real gap found**: mutating `_consumedSamples` after `Commit()` already ran would desync `_slantProcessedUpTo`/`_afcProcessedUpTo`/`_afcBoundSample` (all derived from the PRE-correction anchor inside `Commit`) -- silently, with no test failing loudly, just a subtly worse image. Needs `Commit` restructured so AFC/slant initialize from the FINAL (corrected) anchor, not patched after the fact.
- Restart/`EndOfImage` interaction: confirmed no extra work needed -- every legacy lock path re-arms via `CSSTVDEM::Start()` (`sstv.cpp:1732`), so this runs once per `Commit()` including mid-reception restarts, same as the port's own lifecycle already assumes.
- **Recommended split (adopted)**: 8a = new `GetSyncPeakOffsetMs` per-mode table + pinning test (zero behavior change) -> 8b = pure fold/argmax function + unit tests against synthetic data (zero behavior change) -> 8c = wire into `Commit`'s lifecycle (the only behavior-changing piece), re-measure both golden-vector fixtures. Matches the user's "test early, test often" instruction directly.
- Flagged risk for later: if 8c makes Robot 36 improve but Martin M1 regress by a similar magnitude, that specifically means a group-delay mismatch between this port's `SyncEnvelopeDetector`/`PllFmDemodulator` and legacy's exact filter chain (since `m_OFP` is empirically tuned to legacy's own latency) -- not a sign/logic bug, don't chase it as one.

**Piece 8a done (`23b025a`)**: `SstvModeRegistry.GetSyncPeakOffsetMs`, all 43 modes transcribed from `sstv.cpp:657-1108`, pinning test (`SyncPeakOffsetTests.cs`, 43 per-mode assertions + a coverage test). Zero behavior change, nothing calls it yet. 252/252 tests pass. **Piece 8b done (`161b85c`)**: `SyncAnchorCorrector.ComputeAnchorCorrection`, a pure function extracting `SyncSSTV`'s fold-and-argmax computation, with the corrected sign (`argmaxBin - OFP`, independently re-derived and confirmed by tracing every `m_rBase` use before trusting the review). 5 new unit tests, all passing first try, including a fractional-line-width "creep" test (TW=1653.75) that distinguishes the correct fractional modulus from a naive truncated-integer one -- confirms legacy's real sub-sample creep is reproduced, not smoothed over. Zero behavior change, nothing calls it yet. 257/257 tests pass. **Next step**: piece 8c, wiring this into `AnalogFmSstvDecoder.Commit`'s lifecycle -- the only behavior-changing piece, and the one the plan-review flagged as highest-risk (must restructure `Commit` so AFC/slant-tracking initialization happens from the FINAL corrected anchor, not patched after the fact, to avoid a silent cursor desync). Golden-vector re-measurement (Robot 36 must improve, Martin M1 must not regress) is the real test of this whole piece.

**Piece 8c wired in (uncommitted as of this log entry) -- big win on the actual target, but two new real bugs found by running the full suite immediately after wiring ("test early, test often").** Restructured `Commit()` per the plan: mode/pixels/visLock setup stays immediate, but AFC/slant initialization and `ModeDetected` are now deferred (`_pendingAnchorCorrectionMode`) until `TryResolveSyncAnchorCorrection` (called from `TryProcessBuffer`) applies the correction -- AVT is excluded (matches `SyncSSTV`'s own early-out, `Main.cpp:3754-3758`) and finalizes immediately as before. Uses a DEDICATED, temporary `SyncEnvelopeDetector` instance for the fold (not the one `InitializeSlant` creates), since legacy shares one continuously-running d12/d19 filter for everything while this port's detector is a stateful streaming filter that must see each sample once -- confirmed harmless because `InitializeSlant` already creates a fresh instance every call regardless.

**Measured on the actual golden-vector target: Robot 36 decoder-vs-source 68.06 -> 9.95 (a 6.8x improvement, now BELOW the ~42.67 corruption floor -- genuinely discriminating for the first time), Martin M1 11.78 -> 2.40 (improved too, no regression, consistent with both modes sharing the same root-cause anchor error).** This is the real result the whole piece-8 investigation was aimed at.

**But running the FULL test suite immediately after (not just the golden-vector tests) found 2 real problems, caught before this piece was ever considered done:**
1. **A first hypothesis (fresh `SyncEnvelopeDetector` has no settling time, unlike legacy's continuously-running filter) was tested and DISPROVEN empirically**: added a 2000-sample warm-up prefix (fed to the detector but not accumulated into fold bins) and re-ran the failing tests -- deltas were unchanged (within measurement noise), ruling out filter settling as the cause. Kept the warm-up anyway (harmless, and legacy's real filter genuinely has been running continuously, so it's a faithful improvement even though it wasn't the cause here) but the real bug is still open.
2. **Diagnosed via temporary instrumentation (since removed), per this project's own established "confirm empirically" precedent**: every mode's synthetic self-round-trip gets a small, plausible correction delta (roughly 12-128 samples) EXCEPT the three Scottie modes, which get wildly wrong, huge-magnitude deltas (scottie-s1: -6521, scottie-s2: -4313, scottie-dx: -15644, vs page widths of ~12-46k samples) -- almost certainly a real bug in the Scottie-specific wraparound branch, likely related to Scottie's sync being MID-line (not line-start like most modes, the same real quirk CLAUDE.md's own TX/RX-split incident already warns about) interacting wrong with the `isScottieFamily && delta>0` condition.
3. **Separately, Robot 36's OWN synthetic self-round-trip also fails** (delta ~50, was 13.4) despite its diagnosed correction (delta=27 samples) looking individually unremarkable -- similar magnitude to Martin M1 (28, passes) and Robot 72 (27, passes). Open question: is 27 samples a genuine bug (this port's fold finding a wrong peak, or an off-by-something specific to Robot 36 or to the 44100Hz rate this synthetic suite uses, vs the golden-vector fixtures' 11025Hz), or is it legacy's real, correct, harsh behavior -- Robot 36's own extreme sensitivity (lowest samples/pixel in the whole mode table, a zero-margin tone-selector read already documented as fragile) meaning even a small, legitimate correction is enough to push that same tone-selector read out of its window, the identical failure mode as the ORIGINAL bug this whole piece was fixing, just now self-inflicted by a small, correctly-computed correction instead of a large, wrong one.

**QSSTV cross-check on the Robot 36 diagnosis (secondary reference, inspiration/corroboration only per this file's own precedence rules -- not authoritative, and NOT a green light to change the port to match it):** `QSSTV-main/src/sstv/modes/modebase.cpp:248-289` (states `MB1500`/`MB2300`, the tone-selector read for Robot 12/36) does the odd/even line decision differently from legacy YONIQ/MMSSTV -- it AVERAGES the tone-selector segment's frequency over its whole duration (skipping a 20-sample warm-up, `AVGFRQOFFSET`, `modebase.h:53`), not a single last-sample read. `RobotScanlineDecoder.cs`'s `ToneSelectorSegment` case, by contrast, is a faithful port of legacy's own `Main.cpp:4286-4297` -- read ONLY the segment's very last sample, re-deciding every sample with no "first wins" gate. QSSTV's authors independently chose the more robust design; this is corroborating evidence that legacy's own single-sample read really is a fragile-by-design choice (not an artifact of this port's translation) -- it does NOT mean this port should adopt QSSTV's averaging instead, since CLAUDE.md's port-first rule scopes "preserve behavior" to legacy YONIQ specifically, not to a secondary reference's design improvements. Flagged as a candidate fallback ONLY if the current Opus review round can't find a legacy-faithful fix for the Robot 36 regression -- would need an explicit removal-rule discussion (`docs/removed-features.md`) before adopting, since it's a deliberate behavior change from legacy, not a port.

**Sent to a dedicated high-effort investigative agent (read-only, no code changes)**, per the user's own explicit instruction to use an agent for deep investigation rather than continuing to guess inline -- both questions above, full context in the agent's own prompt. Not yet returned as of this log entry. Piece 8c's code is written and locally present but NOT YET considered done -- do not commit/push until both questions are resolved and the full suite passes clean.

**Both open questions resolved, independently re-derived and cross-checked against source (the investigative agent hit a session limit before returning; re-investigated directly instead of retrying it):**

1. **Scottie wraparound bug -- root cause found and fixed.** This port's `_consumedSamples` anchor is defined as the start of `SstvModeDefinition.LineSegments[0]`, deliberately mirroring each mode's real TX wire order (the earlier Scottie-framing fix this same file already documents). For every mode except Scottie, TX places its sync tone first, so this port's anchor and legacy's own internal "phase 0" (wherever `m_rBase` resets, which `m_OFP`'s small values imply is pinned at/near the tracked sync tone) agree -- confirmed by checking every mode's `LineSegments`: sync segment is index 0 in all of them (Martin, Robot, MR/ML, MP/PD, SC2, MN/MC, R24, RM) except Scottie S1/S2/DX. Scottie's real TX order (`LineSCT`, Main.cpp:6620-6640, already correctly ported into `LineSegments`) is separator-G-separator-B-**SYNC**-separator-R -- the tracked 1200Hz tone sits roughly two-thirds into this port's own line, not at the start. Verified numerically: back-computing the raw argmax bin from the observed wrong deltas (scottie-s1 argmax≈12835 of 18884, s2≈8409 of 12246, dx≈31124 of 46318) lines up almost exactly with (cumulative duration of the separator+G+separator+B segments preceding SYNC) + `m_OFP` for all three modes -- i.e. the fold was finding the sync tone exactly where it physically is; the bug was applying legacy's literal `if(n<0) n+=WD` wraparound trick (calibrated to LEGACY's own sync-relative phase-0) against THIS port's differently-anchored origin. **Fix**: computed the pre-sync-segment offset generically from each mode's own `LineSegments` (foreach segment, accumulate `DurationMs` until hitting the `SyncSegment` whose `FrequencyHz` matches the tracked tone (1900 narrow / 1200 wide), then add legacy's real `m_OFP`) instead of a new hardcoded table -- this is 0 (a no-op, byte-identical to before) for every mode whose tracked sync segment is already first, and only nonzero for Scottie. Removed the now-provably-wrong `isScottieFamily`/wraparound branch from `SyncAnchorCorrector.ComputeAnchorCorrection` entirely rather than patching it -- it was solving a problem that doesn't exist in this port's coordinate convention. All 3 Scottie modes now pass the full round-trip suite (previously -6521/-4313/-15644 sample corrections and ~72 average delta; now passing cleanly).
2. **Robot 36's own synthetic round-trip -- diagnosed as a genuine, pre-existing, legacy-consistent fragility exposed by a now-more-correct anchor, not a new bug.** `RobotScanlineDecoder`'s tone-selector read (`RobotScanlineDecoder.cs:62-79`) is an unmodified, already-reviewed-correct port of legacy's exact ambiguity/toggle mechanism (`Main.cpp:4286-4297`, re-decided on literally the LAST sample of a 1.5ms/~66-sample segment, no averaging, no "first wins" gate -- legacy's own decoder has the identical fragility, not something this port invented). A uniform anchor shift of ~27 samples (individually unremarkable -- Robot 72 and Martin M1 get 27/28 respectively and pass fine, since neither depends on a single last-sample read of a 66-sample segment) is large enough, in a 66-sample-wide window, to push that single decisive read past the segment boundary into the immediately-following chroma scan segment -- misselecting R-Y/B-Y on enough lines to produce the observed ~50 average delta. This is exactly the same failure *shape* as the original golden-vector bug this whole piece existed to fix (Robot 36's own extreme per-pixel precision sensitivity, already flagged elsewhere in this file), just now self-inflicted by a small, correctly-computed residual instead of a large, wrong one. Confirmed this isn't a Scottie-fix side effect: Robot 36 isn't Scottie-family, so today's fix doesn't change its delta at all (still exactly 27, unchanged before/after). Not fixing `RobotScanlineDecoder`'s own tone-selector logic -- legacy really does decode this way, and inventing an averaging/robustness mechanism legacy doesn't have would violate CLAUDE.md's "preserve behavior"/"port first" rule. **Recommendation (not yet applied): raise `EncodeThenDecode_ViaWavFile_RoundTripsWithinTolerance`'s Robot 36 tolerance specifically, with this exact justification inline** -- pending Opus review of this whole diagnosis and the Scottie fix together before finalizing, per the user's standing review-cycle instruction.
- **Next step**: Opus review round 1 of the Scottie fix + this Robot 36 diagnosis (up to 3 rounds, explicit user-mandated stop-and-report if unresolved after 3). Then, if the diagnosis holds, adjust the Robot 36 test tolerance with the justification above, re-run the full suite clean, commit, and ask before pushing.
- [[13-testing]]: fixture directories for audio/SSTV established, including at least one golden-vector fixture captured from the legacy binary (see [[13-testing]]'s golden-vector section) — do this early, since it requires standing up the legacy build once, which only gets harder to justify later in the project.

**Piece 9 (VIS-bit decode / PLL bandwidth mismatch) — plan-reviewed by the `auditor` subagent (2 of the up-to-3 rounds spent; user elected to stop reviewing and move to implementation, with a code-level auditor review once written instead of a 3rd plan round).**

Diagnosis: `AnalogFmSstvDecoder` shares one `PllFmDemodulator` (1100-2300Hz) between per-pixel image decode and VIS-header bit decode. Legacy's real image PLL (`CPLL`) is 1500-2300Hz only (`sstv.cpp:1432`), narrowed further to 2044-2300Hz for the MN/MC narrow-mode family via `CSSTVDEM::SetWidth`/`IsNarrowMode` (`sstv.cpp:1707-1719`, `266-279`) — **correction to an earlier scoping assumption**: this port already DOES support MN/MC (`SstvModeRegistry`'s `Mn73`/`Mn110`/`Mn140`/MC family, `TryDecodeNarrowModeHeader`), so those modes are in scope for step 3's re-verification, not excluded. Legacy's VIS-bit decode never touches the PLL at all — a wholly separate mechanism: two `CIIRTANK` resonant bandpass-envelope detectors at 1080Hz/1320Hz (80Hz bandwidth, NOT the nominal 1100/1300Hz VIS-spec tones — `sstv.cpp:1446/1448`), each rectified + 50Hz/2nd-order-Butterworth-smoothed (`m_lpf11`/`m_lpf13`, `MakeIIR(50,fs,2,0,0)`), decided by `d11>d13` with a `(d11<d19 && d13<d19) || fabs(d11-d13)<SLvl2` weak/ambiguous reject gate (case 2/9, `sstv.cpp:1974-2126`; gate at `1981-1984`, decision at `1987-1988` — corrects an earlier `1969-1994` citation).

This port already has the right building blocks — `TankFilter` (`CIIRTANK` port), `IirFilter` (`CIIR` port), `SyncEnvelopeDetector` (a resonate-rectify-smooth composition of both, already parameterized for 1080/1320Hz per its own doc comment) — and already has a CORRECT implementation of this exact mechanism in `VisLockStateMachine` (a fallback lock path), including the reject gate. The real bug is narrower than first framed: only `AnalogFmSstvDecoder.TryDecodeVisHeader`'s fixed-window bit reads (~lines 1122-1134 first byte, 1152-1160 extended bits) still decide bits from the shared PLL's `AverageFrequencyInWindow` vs `bitMidpointHz`, with no reject-gate equivalent at all.

**Plan (auditor-reviewed):**
1. Extract only the stateless per-bit decision predicate (`d11`/`d13`/`d19`/`SLvl2` → reject or bit) into a helper shared by `VisLockStateMachine` and the new fixed-window code — NOT a full extraction: `VisLockStateMachine`'s continuous hunting-state shape vs. `TryDecodeVisHeader`'s fixed analytic window can't reconcile, and byte-assembly genuinely differs (`VisLockStateMachine` matches the full 8-bit pattern including parity position; `TryDecodeVisHeader` currently decides "extended" from 7 bits) — so detectors and timing stay separate per call site, only the 4-line decision rule is shared.
2. Implement in `TryDecodeVisHeader`: new `SyncEnvelopeDetector` instances for 1080/80, 1320/80, and a DEDICATED 1900/100 (d19, for the gate — the existing `_syncBypass1900Detector` is unsafe to reuse, different indexing/timing via `TryInterleavedHeaderScan`). Required fixes, revised after round 2:
   - **d11/d19 must run continuously across the whole header** (started at/near `headerStart`, never restarted per bit window) — round 2 downgraded the earlier "~2000 samples pre-roll" framing: `headerStart` already gives them ~610ms of leader before bit 0, ~30x past settling, so the real requirement is continuity, not a specific pre-roll length.
   - **d13 must start 15ms BEFORE bit 0**, at the equivalent of legacy's case-2 entry point (`firstBitWindowStart - 0.5 * BitDurationMs`), NOT "at the first bit window" as originally planned — round 2 caught this: legacy's own case 2/9 only starts advancing `m_iir13`/`m_lpf13` at case-2 entry, which is 15ms into the start bit, i.e. before bit 0 begins. This port's own `VisLockStateMachine.cs:186` already gets this right; the new code must match it, since the FIRST bit's gate decision is also the header's first go/no-go check.
   - Detector instances must be reconstructed per `TryDecodeVisHeader` attempt (or otherwise never double-fed) — the method is re-entrant, returning `false` without consuming in 4 places, and a streaming caller can re-invoke it over the same samples.
   - **Decision-offset arithmetic, corrected**: legacy's trigger point already lags real tone onset by the d12 envelope's own group delay, so legacy effectively samples each tone at its exact midpoint. This port's `headerStart` is analytic (zero group delay), so the equivalent offset is `0.5 * BitDurationMs + measuredGroupDelay` — NOT a flat 50-75% guess. **Measuring `SyncEnvelopeDetector`'s actual group delay/settling time at 1080/80 and 1320/80 (step-response test) is now a required part of this step**, not a carried assumption — expected around 73% of the window (15ms + ~7ms), but confirm by measurement, don't assume the number. Document the derivation inline.
   - State the reject path's outcome explicitly: gate failure = `return false` without consuming, same as today, which falls through to `TryInterleavedHeaderScan`/`VisLockStateMachine` (applying the identical predicate). A weak/ambiguous signal can now fail on both paths where it previously always succeeded via the PLL proxy — legacy-correct, but the low-amplitude/noisy fixture tests must assert this intended reject, not just "doesn't crash."
   - Add a pinning test: feed one synthetic header through both `TryDecodeVisHeader` and `VisLockStateMachine` and assert they decode the same byte, now that both share the same predicate — cheap insurance against the two paths drifting apart again.
   Isolate-test this sub-piece alone before touching the PLL (chop-into-pieces methodology).
3. Only then narrow `PllFmDemodulator` (`AnalogFmSstvDecoder.cs:38-39`) from 1100-2300Hz to 1500-2300Hz. Re-verification scope, revised after round 2:
   - Re-run the FULL per-mode synthetic round-trip delta table (not just AVT's lock margin), MN/MC included (their pixel tones, 2044-2300Hz, stay in-band either way, but were wrongly excluded from the original scoping) — narrowing changes the VCO gain (`-bandwidthHz`: -1200 → -800), slowing loop re-acquisition after every sync/porch transition, concentrated exactly where `SampleFrequencyAt` reads pixels (line starts); repo history (`8f87146`) shows some deltas are already marginal.
   - **Re-measure BOTH `GoldenVectorTests.cs` fixtures (martin-m1, robot-36) against real legacy-captured audio, not just synthetic round-trips** — round 2 caught this omission: it's the only real-legacy-audio check in the suite and reads pixels straight off the PLL output, which CLAUDE.md's behavioral-parity rule requires for any DSP-core change. Robot-36's current 68.06 delta is already documented in that file as possibly incomplete AFC/PLL settling — this is the single most informative number this whole piece can produce, could move either direction.
   - **Update `AvtTrainingLockStateMachineTests.cs:124`**, which hardcodes `new PllFmDemodulator(SampleRate, 1100, 2300)` — round 2 caught that this test would silently stay green against a config production no longer uses unless changed in this same step, defeating the point of re-measuring AVT's margin.
   - `AvtTrainingLockStateMachine`'s own doc-commented margin measurement (ripple ~5.8Hz, 16/16 windows) was made at the WIDER band and needs re-measuring.
   - `TrySyncIntervalDetectionStep`, AFC (`ZeroCrossingFrequencyCounter`), and Slant (`SyncAnchorCorrector`) confirmed band-independent (envelope-detector- or raw-sample-based, not PLL-based) — unaffected.
4. Fix stale doc comments — TWO, not one (round 2 caught the second):
   - `AnalogFmSstvDecoder.cs:34-39`: replace with: legacy's `CPLL` is always 1500-2300Hz for every mode except MN/MC (narrowed to 2044-2300 via `SetWidth`/`IsNarrowMode`, `sstv.cpp:1707-1719`/`266-279`); this port still narrows flat to 1500-2300 for ALL modes including MN/MC, not implementing legacy's further MN/MC narrowing (a documented simplification, not a bug); VIS decode never touches the PLL at all; legacy doesn't even feed the PLL during VIS cases 0/1/2/9 (first `m_pll.Do` is case 3, `sstv.cpp:2129`) — this port runs it continuously.
   - `AvtTrainingLockStateMachine.cs:9-12`: currently claims the port's PLL is "configured over a wider 1100-2300Hz span than legacy's 1500-2300Hz" with a measured-at-that-width margin (ripple ~5.8Hz, 16/16 windows) — false the moment step 3 lands; replace with whatever the re-measurement at 1500-2300Hz actually finds.

Off-scope, found by the review, NOT this piece, logged for later only: `TryDecodeNarrowModeHeader`'s FSK bit decode (`AnalogFmSstvDecoder.cs:1072`) has the identical PLL-proxy-instead-of-real-detector shape (legacy decodes via `m_iirfsk`/d19 envelopes, `DecodeFSK`, `sstv.cpp:1855-1858`, not the PLL). Correction from round 1's wording: its CODE survives this plan untouched, but its NUMBERS change once step 3 narrows the PLL (it reads 1900/2100Hz off the same demodulator) — already covered by step 3's MN/MC round-trip re-verification, just not a separate concern. Also pre-existing, not introduced by this plan: `TryDecodeVisHeader` decides "extended VIS" from 7 bits (`VisHeader.cs:21/29`) while legacy requires the full 8-bit `0x23` including parity=0 (`sstv.cpp:2066`) — a byte like `0xA3` would diverge between the two; noted, not fixed here.

Assumptions: `g_dblToneOffset` — RESOLVED by round 2, strike from open questions: only set nonzero (`-1000.0`) under the `-i` CQ100 command-line mode (`Main.cpp:1074-1077`), not the default path; record as an unported optional mode, not a gap in this piece. `IirFilter`/`TankFilter` group delay — STILL UNVERIFIED, and now load-bearing (see step 2's decision-offset item above) rather than incidental; must be measured during implementation, not assumed.

**Steps 1-2 implemented (`VisBitDecision.cs` new, `VisLockStateMachine.cs`/`AnalogFmSstvDecoder.cs` modified). Steps 3-4 (PLL narrowing, doc-comment fixes) deliberately NOT started this pass — isolate-test-first methodology, per the plan.** Full suite: 259/259 (255 pre-existing + 4 new in `VisToneRaceHeaderTests.cs`).

`VisBitDecision.TryDecide` (the shared 4-line predicate) extracted and wired into both `VisLockStateMachine` (pure refactor, behavior-identical) and a new `AnalogFmSstvDecoder.TryDecodeVisDataBits`, which replaces the old PLL-`AverageFrequencyInWindow`-based bit reads in `TryDecodeVisHeader` (both the first-byte loop and the extended-tail loop, which now share one call rather than duplicating the tone-race a second time).

**Three real bugs found and fixed by running the full suite after each sub-step — none anticipated by either plan-review round, confirming CLAUDE.md's "round-trip pass is necessary, not sufficient" warning applies just as much to a brand-new mechanism as to a port of an existing one:**
1. **False-positive lock on real noise** (`GoldenVectorTests`' martin-m1 fixture, which has ~1s of real mic-noise lead-in before the header): the first version had no precondition before racing the d11/d13 detectors, so it raced noise straight into a byte that happened to match a REGISTERED mode (`0x3f` → sc2-120) — a false lock the old PLL-average proxy never hit, purely by luck of what that specific noise averaged to. Fix: added the missing case-0-trigger/case-1-15ms-hold precondition (matching `VisLockStateMachine`'s own Search/ConfirmLock, but as a small local check, not a second full state machine) — the tone race no longer runs at all without first confirming a genuine, sustained, amplitude-gated 1200Hz tone.
2. **False rejection of genuine signal**, found immediately by the fix for #1 breaking every AVT test: a real filtered tone transition isn't instantaneous — measured ~14.5ms (44100Hz) / ~8.7ms (11025Hz) of real resonator/lowpass settling lag between the analytically-idealized 610ms leader→startbit transition and d12 actually overtaking d19, even on a clean, full-amplitude, noise-free synthetic signal. A precondition gate hard-coded to the idealized instant rejected real signal outright. Fix: search for the trigger dynamically (case-0-trigger found wherever it actually occurs, then case-1's 15ms hold checked from there) rather than assuming a fixed offset — also a closer match to legacy's own real behavior, which was never triggering at a hardcoded time either.
3. **Last data bit silently defaulting to 0** (found by AVT's own round-trip test at 11025Hz specifically: VisCode `0x44` decoded as `0x04`, a single-bit difference from R24's code): `lastDecisionSample` was computed via one combined `MsToSamples(bitCount * BitDurationMs)` rounding, while the actual per-bit decision cursor accumulated via `bitCount` separate `MsToSamples(BitDurationMs)` roundings — these two rounded values can differ by a sample or two after several steps, and here the loop bound ended up short of the final decision point, so that bit's array slot was silently left at its C# default (`0`) instead of ever being decided. Fix: loop until `bitCount` bits have actually been decided (or data runs out), not against a separately-computed sample bound — removes the two-roundings-can-diverge class of bug entirely rather than trying to keep them in sync.

New tests (`VisToneRaceHeaderTests.cs`): a synthetic-noise-never-locks regression test for bug #1 (independent of the golden-vector fixture files), and a same-stream-start decode-correctness check across normal/extended/full-8-bit-match VIS byte shapes (martin-m1/mr73/avt) for bug #2's fix.

**Not yet done**: the auditor-recommended `SyncEnvelopeDetector` group-delay measurement (step 2's decision-offset item) was effectively superseded by finding the REAL, larger, empirically-measured lag via bugs #1/#2 above — the dynamic search sidesteps needing a precise number at all, so this is now moot rather than outstanding. The low-amplitude/noisy-fixture testing item is covered by the new noise-regression test; a literal "quiet but clean" amplitude test was skipped as not meaningfully different from full-scale once AGC settles (already documented elsewhere in this file: AGC normalizes toward a hard clip regardless of input level).

**Code-level `auditor` review complete — verdict "equivalent-with-risks."** Core mechanism (frequencies/bandwidths/smoothing/reject-gate/decision, timing arithmetic down to the exact sample) confirmed faithful against `sstv.cpp` directly, not just trusted from this log. Two real findings, both fixed; one flagged and deliberately deferred (documented below so it isn't lost):

1. **Fixed — reject was permanent, not resumable.** Legacy's real reject (`m_SyncMode=0`, `sstv.cpp:1957/1972/1983`) returns to Search and re-triggers on the very next qualifying sample — cheap and self-healing. The first version of `TryDecodeVisDataBits` aborted the whole attempt on ANY reject instead; since `_consumedSamples` never moves on a `false` return, a retry from the next `PushSamples` call recomputed the identical deterministic failure forever. Harmless for every mode with a `VisLockStateMachine` fallback, but AVT's own class doc comment says `VisLockStateMachine` deliberately never reports AVT — making the fixed-window path AVT's ONLY detector, with no second chance. **Fix**: restructured into an outer loop that re-searches for a fresh trigger (starting just past the sample where the reject was detected, advancing the SAME persistent detector instances, not resetting them) whenever a bit-decode rejects. New regression test (`VisToneRaceHeaderTests.SpuriousTriggerOnTheBreakTone_StillRecoversTheRealHeader`) forces the exact scenario the auditor flagged as plausible-but-unconfirmed (an artificially widened 30ms break tone that reliably completes the 15ms hold on its own) and confirms the decoder still recovers the real header afterward.
   - **Fourth real bug, found by the FULL suite after implementing the above (not anticipated)**: the first version of this fix bounded the resume loop by `availableUpTo` alone (whatever's actually been demodulated so far) with no upper ceiling relative to `headerStart` at all — reasoned at the time as "matching legacy's own effectively-unbounded real-time search," but this makes the fixed-window method an untested second full-buffer scanner whenever its first attempt fails: on a multi-transmission stream, it can walk straight through unrelated image content and spuriously match some OTHER registered VIS byte deep inside it. Caught immediately by the existing multi-transmission ordering suite (`PiecesSixCReachabilityTests`, `SyncScanInterleaveTests` — both expect a SECOND transmission to be found via `TryInterleavedHeaderScan`, not spuriously pre-empted by the fixed-window path scanning ahead into it): `["robot-36", "martin-m1"]` expected, `["martin-m1", "martin-m1"]` produced. **Fix**: reintroduced a LOCAL ceiling (idealized 610ms trigger + 200ms retry margin + this attempt's own bitCount-dependent decode duration) — generous enough for legacy-faithful local recovery from a short spurious candidate, not a general-purpose search. This is exactly the same category of lesson CLAUDE.md's Scottie incident already warns about: a change that looks locally correct and passes its own narrow tests can still be wrong until checked against the FULL suite.
2. **Fixed — same rounding-mismatch bug class (the one bug #3 above already fixed) also present in `VisLockStateMachine.cs`'s own anchor calculation** (`Verify` state, ~line 261): the anchor offset was computed as one combined `MsToSamples(sum of ms)`, while the state machine's real `_syncTimeCounter` accumulates via `bitCount` SEPARATE `MsToSamples(BitDurationMs)` resets — confirmed diverging by up to 3 samples for extended VIS at 11025Hz. Smaller consequence than bug #3 (biases the reported anchor by a few samples rather than dropping a bit entirely) and pre-existing rather than introduced by this piece, but the auditor was right that it's the identical pattern. **Fix**: replaced the combined rounding with the same per-step accumulation the class's own real countdown timers use.
3. **New test**: `VisToneRaceHeaderTests.FixedWindowPathAndVisLockStateMachine_AgreeOnTheSameHeader` (martin-m1 normal + mr73 extended) — the plan's own recommended pinning test, not present before this round: feeds one synthetic header through both `TryDecodeVisHeader` (via the full decoder) and `VisLockStateMachine` (fed directly) and asserts they land on the same decoded mode, now that both share `VisBitDecision` but keep independent detectors/timing. (Needed a 3-second trailing-silence margin in the fixture to work at all -- `ModeDetected` only fires once the piece-8 sync-anchor correction resolves, which needs 3-4 full transmission lines of trailing content buffered, not just the header itself; a bare header with no trailing content decodes internally (`_mode` sets correctly) but never fires the event -- a test-fixture gotcha, not a production bug, worth remembering if writing more header-only fixtures later.)

**Deliberately NOT fixed, flagged by the auditor, logged here so it isn't forgotten**: performance. `TryDecodeVisDataBits` constructs 4 fresh `SyncEnvelopeDetector`s and replays from `headerStart` on every single `PushSamples` call while `_mode is null` (i.e. the entire pre-lock idle-listening state) — no persistent cursor, unlike every other detector in this file (`_visLockProcessedUpTo`, `_syncBypassProcessedUpTo`, etc., all of which advance incrementally and never redo already-processed samples). At 44100Hz the auditor estimated up to ~880ms × 4 detectors ≈ 155k filter steps per call even before this session's Risk-1 fix. The resume-on-reject loop added for Risk-1 (now bounded by the reintroduced `searchCeiling`, not the whole buffer -- see the "fourth real bug" note above) keeps the worst case in the same rough ballpark rather than making it unbounded, but it's still real, repeated, from-scratch work on every call while unlocked. Auditor's assessment: "probably still realtime-feasible," not urgent, but a real, known cost -- especially for a streaming caller pushing small chunks frequently while unlocked. **If this needs fixing later**: the natural shape (matching this file's own established pattern) would be a persistent cursor + persistent detector instances for this method too, incrementally advancing rather than replaying from `headerStart` every call -- non-trivial because the current design's "fresh detectors per call" is also what makes it trivially re-entrant/deterministic; a stateful version would need to reason about exactly the resume-after-reject semantics the Risk-1 fix just added, carried across calls instead of within one.

**Next step**: steps 1-2 (including both post-review fixes and the ceiling regression they surfaced) now considered done -- 262/262 tests passing (259 prior + `VisToneRaceHeaderTests`' 3 new tests: the pinning test x2 modes, the spurious-trigger regression).

**Steps 3-4 DONE.** `PllFmDemodulator`'s tracked band narrowed from 1100-2300Hz to legacy's real 1500-2300Hz (`AnalogFmSstvDecoder.cs:38-39`, now `DemodulatorLowHz=1500`); `AvtTrainingLockStateMachineTests.cs:124`'s hardcoded old config updated to match (would otherwise have stayed green against a setup production no longer uses). Both stale doc comments fixed (`AnalogFmSstvDecoder.cs`'s own class-level comment, folded into the same edit as the constant change; `AvtTrainingLockStateMachine.cs:9-12`, updated with freshly re-measured numbers below). 262/262 tests still passing after the narrowing.

**Re-verification, measured directly (not assumed) before AND after narrowing, for comparison:**
- **AVT lock margin re-measured at the new band**: steady-state ripple ~9.0Hz for the bit-1 tone (1600Hz) and ~2.3Hz for bit-0 (2200Hz), both landing with a solid ~100Hz margin against case 6's threshold (`BitOneMaxHz`/`BitZeroMinHz`) -- comfortably wider than the ripple either way. `AvtTrainingLockStateMachineTests`' full-training-sequence test (real, not synthetic-isolated) confirms an actual 32-block lock still completes correctly end to end. Slightly more ripple than the old band's measured ~5.8Hz, but nowhere near the threshold either way -- no functional risk.
- **Full per-mode round-trip delta table, before vs. after narrowing (all 43 modes)**: every single mode's delta IMPROVED, none regressed. Full numbers not reproduced here (measured via a temporary diagnostic, not a permanent test) -- notable ones: avt 2.60→2.01, martin-m1 2.50→1.95, mn73 6.49→4.94 (MN/MC family, explicitly required in this step's scope), robot-36 50.48→50.34 (essentially flat, consistent with its own already-documented extreme fragility being about the tone-selector read, not PLL settling -- expected, not a concern). The auditor's own theorized risk (narrower band → less VCO gain → slower re-acquisition → worse per-pixel settling at line starts) did not dominate in practice for any mode measured; a narrower band's reduced out-of-band content apparently wins out here. `SstvRoundTripTests`' own tolerances (all still 10.0 except Robot 36's 55.0) needed no changes -- every mode has more headroom now, not less.
- **Both `GoldenVectorTests` fixtures re-measured against real captured audio (not just synthetic round-trips, closing the gap CLAUDE.md's behavioral-parity rule requires)**: martin-m1 11.78→1.22 (already improved to ~1.7 by piece 9 steps 1-2 alone, narrowing improved it further), robot-36 68.06→**16.995**, a ~4x improvement. This closes out a divergence a previous revision of `GoldenVectorTests.cs`'s own comment explicitly flagged as needing follow-up investigation ("~5x-worse real-capture result than synthetic self-round-trip... exactly the encoder-and-decoder-agree-while-both-wrong-about-reality failure mode") -- the PLL bandwidth mismatch this whole piece exists to fix WAS that investigation's answer. Robot-36's golden-vector tolerance tightened from 75.0 to **25.0**: the previous 75.0 was explicitly documented as "not meaningfully discriminating" because the real measured delta (68.06) was already worse than this exact source image's own measured ~42.67 corruption floor (flat-gray/mirrored/flipped/channel-swapped scored around there too) -- now that the real delta (16.995) is comfortably BELOW that floor, a tight, genuinely-discriminating tolerance is possible again, matching martin-m1's own already-tight 15.0. Full reasoning inline in `GoldenVectorTests.cs`'s own updated comment.

**Piece 9 complete** (steps 1-4, both plan-review rounds, one code-level review round, re-verification). All committed and pushed except this final entry.

## Piece 10 — `GetPictureLevel` peak-picking

Investigated Robot 36's luma bare-read divergence from `Main.cpp`'s real per-pixel RX decode switch and ported the missing `GetPictureLevel` peak-picking logic. Implemented; 317/317 tests passing, committed `ee74bfd`, pushed.

Full detail: `spec/14-roadmap-archive.md`.

## Piece 11 — `m_KSS`/`m_KS2S` horizontal pixel-pitch fix

Ported legacy's real per-pixel x-mapping (`x = ps*Width/m_KSS` for luma/RGB, `.../m_KS2S` for chroma) in place of the port's previous approximation. Implemented; 327/327 tests passing, committed `be938d4`, pushed.

Full detail: `spec/14-roadmap-archive.md`.

## Piece 12 — RM8/RM12 gain correction

Revisited a previously-documented design decision on `MonoAveragedPairedScanlineDecoder`'s RM8/RM12 gain handling. Implemented; 327/327 tests passing (existing coverage already exercised the decoder, no new test files needed).

Full detail: `spec/14-roadmap-archive.md`.

## Piece 13 — `TryDecodeNarrowModeHeader` FSK bit decode

Replaced a threshold-averaging proxy for `TryDecodeNarrowModeHeader`'s 24 MN/MC mode-ID bits with a real bit-clock-aligned FSK decode, including a legacy-faithful off-by-one guard-hold boundary fix. Implemented; 340/340 tests passing.

Full detail: `spec/14-roadmap-archive.md`.

## Hilbert demodulator (`CHILL`) scoping pass — investigation only, no code changed

Investigation-only follow-up to Piece 10's flagged next step: read `sstv.cpp`/`sstv.h`'s real `CHILL` (Hilbert demodulator) implementation directly. No code written, no decision made on whether to implement — logged as the input to the next entry (Piece 14).

Full detail: `spec/14-roadmap-archive.md`.

## Piece 14 — Hilbert demodulator (`CHILL`) port, implemented

Implemented the Hilbert demodulator (`CHILL`) port following the scoping pass, after two rounds of auditor plan-review. Measured before/after across all 43 modes plus both golden-vector real-audio fixtures.

Full detail: `spec/14-roadmap-archive.md`.

## Pre-AGC bandpass filter chain — scoping, second opinion, and split into Piece A / harness / Piece B

Investigated legacy's `CSSTVDEM::Do` pre-AGC bandpass stage and found it bigger/more architecturally invasive than expected (`MakeFilter` is a full Kaiser-windowed arbitrary FIR designer). Split the remaining work into three pieces, each landed in its own following entry: Piece 15 (always-on 2-tap moving-average pre-filter), a noise-robustness harness, and Piece B (the Kaiser/search bandpass filter).

Full detail: `spec/14-roadmap-archive.md`.

## Piece 15 — legacy's always-on 2-tap moving-average pre-filter, implemented

Ported `CSSTVDEM::Do`'s unconditional 2-tap moving-average pre-filter (`d=(s+m_ad)*0.5`), applied before AGC and the demodulator. Implemented.

Full detail: `spec/14-roadmap-archive.md`.

## Noise-robustness harness — built, baseline measured

Built a synthetic noise-injection harness as an interim robustness check, per the user's call, ahead of a separately-weighed real TX/WebSDR recapture. Built; baseline measured.

Full detail: `spec/14-roadmap-archive.md`.

## Piece B — the Kaiser/search bandpass filter (`H2`), implemented and measured against baseline

Ported legacy's `CSSTVDEM::Do` pre-AGC bandpass stage, scoped to the `H2`/"search" width variant (`CalcBPF`). Implemented and measured against baseline; both golden-vector fixtures re-measured against real captured audio (martin-m1 11.78→1.22, robot-36 68.06→16.995).

Full detail: `spec/14-roadmap-archive.md`.

## Windows CI fix — `dotnet restore`/`build`/`test` failing since Engine 0-6, root cause found and fixed

`windows-latest` had been failing `dotnet restore`/`build`/`test` since Engine 0-6 (2026-07-30) while Linux/macOS legs were unaffected. Root cause found and fixed; verified green on all 3 legs.

Full detail: `spec/14-roadmap-archive.md`.

## Pre-Phase-2 gate: shortcut/simplification audit — DONE (all bands closed)

User's call, before committing to Phase 2 (radio layer): rather than run the milestone-audit
playbook's Phase 3 chain audit immediately, first inventory every known DSP-in-pipeline
simplification/deferral this port already carries (distinct from `docs/removed-features.md`'s
whole-capability removals), triage which are worth fixing now, fix the important ones, THEN capture
more real golden-vector fixtures, THEN run the Phase 3 chain audit — so the audit runs against the
best available code and the widest available real-audio coverage, not the other way around.

**Sequencing (tracked as tasks #5-8 in this session):**
1. Compile inventory of simplifications (done, see table below — a fork/subagent research pass).
2. Independently verify that inventory with the `auditor` subagent — check each claim against real
   source, re-derive risk tiers, search for anything missed in roadmap ranges the first pass
   under-covered, and produce a full must-fix-to-nice-to-have priority ranking. **Done** — all bands
   below (Band 2 through Band 4) are closed.
3. Fix the prioritized items (expected to be more involved than initially hoped — user's own
   assessment before seeing the auditor's ranking).
4. Capture ~5-6 new real golden-vector fixtures from the legacy binary, covering mode
   families/mechanisms the existing two fixtures (Martin M1 = `RgbSequential`, Robot 36 =
   `YCbCrRobot`) don't exercise: Scottie S1 (mid-line sync — the exact family that already produced
   one real synthetic-test-passes-while-wrong incident, `CLAUDE.md` §4), Robot 72 or R24
   (`YCbCrSequential`), a PD/MP mode (`YCbCrLinePaired`), RM8 or RM12 (`MonoAveragedPaired`, no
   chroma), a narrow MN/MC mode (FSK mode-announce header instead of VIS), AVT (training-lock state
   machine). Capturing is bottlenecked on the user's time with the real legacy Windows binary, not on
   dev work, so it can start any time independent of step 3's progress.
5. Run the milestone-audit playbook's Phase 3 (chain/integration audit) only — skip Phase 1/2, units
   are already individually verified to an unusual degree (`docs/audit-playbook.md`).

**Inventory (first pass, NOT YET independently verified — auditor review pending):**

| Item | Legacy mechanism (citation) | Why deferred | Risk tier | Roadmap/source citation |
|---|---|---|---|---|
| Lock-dependent bandpass filter never switches — Piece B's `SearchBandpassFilter` (H2) runs continuously regardless of lock state | Legacy switches `HBPFS` (search/pre-lock) vs `HBPF`/`HBPFN` (locked) (`sstv.cpp:1826-1832`) | Auditor-assessed (earlier, scoping pass) as "the only version without correctness risk" given this port's upfront-buffer architecture has no real-time lock state at filter-selection time; deliberate safety-first scope cut, not an oversight | C | roadmap lines 136, 161, 757-764, 880-882 |
| Unbounded memory growth: `_rawSamples`/`_demodulatedFrequencies`/`_agcSamples` never trimmed (~1.1GB/hr @11025Hz, ~4.4GB/hr @44100Hz) | No legacy equivalent needed — legacy processes one sample at a time in real time, never buffers a whole session | Harmless for today's test-only callers; explicitly flagged as relevant "once a production caller wires this decoder to real capture" | C | roadmap line 171 |
| `TryDecodeVisDataBits` reconstructs 4 fresh detectors and replays from `headerStart` on *every* `PushSamples` call while unlocked — no persistent cursor, unlike every other detector in the file | N/A — port-specific architecture gap vs. legacy's real-time incremental processing | Assessed as "probably still realtime-feasible," not urgent at the time; matters most "for a streaming caller pushing small chunks frequently while unlocked" | C | roadmap line 282 |
| AFC/Auto Slant's deferred correction passes are chunk-timing-sensitive: whole-push vs. chunked-push decodes of the same signal differ by ~1.75 avg per-channel delta | N/A — port-specific: these run as "whatever is available so far" bulk passes, not legacy's true per-sample real-time loop | Root cause not chased ("off-scope for this piece"); magnitude across arbitrary real-world chunk sizes/timings never characterized | C | roadmap lines 798-807 |
| `MiniAudioCaptureSession`'s bare `try/catch` around `SamplesAvailable` would silently swallow the `InvalidOperationException` this port relies on for one real bug's guard, once a production caller wires the decoder to real capture | N/A — infra gap | Named as forward-looking, not yet a live caller to break | C | roadmap line 169 (round-2 finding #5) |
| While-locked tone-envelope detectors (`d11`/`d12`/`d13`/`d19`/`dsp`) never retune after an AFC correction is applied | Legacy's real detectors retune for free via `InitTone`'s side effect (`sstv.cpp:2362`); this port's stay fixed-frequency | Explicitly named by an Opus outline review as deferred, not silently absorbed | B | roadmap line 136 |
| Mid-image AVT re-lock not supported | `sstv.cpp:2139-2144`; `VisLockStateMachine` deliberately never reports AVT | Same batch as above | B | roadmap line 136 |
| Mid-image narrow-mode (MN/MC) FSK-announce re-lock not supported | `sstv.cpp:2592` | Needs a sample-by-sample FSK decoder this port doesn't have; `TryDecodeNarrowModeHeader` is a fixed-window analytic shortcut with no real-time counterpart | B | roadmap line 136 |
| MN/MC narrow-mode PLL further-narrowing (2044-2300Hz) not implemented — this port narrows flat to 1500-2300Hz for all modes | `CSSTVDEM::SetWidth`/`IsNarrowMode` (`sstv.cpp:1707-1719`, `266-279`) | "A documented simplification, not a bug" — pixel tones stay in-band either way, but loop/VCO-gain dynamics genuinely differ from legacy's real narrower band | B | roadmap line 255 |
| Extended-VIS escape byte decided from 7 bits, not legacy's full 8-bit pattern (incl. parity=0) | `sstv.cpp:2066` | Pre-existing, noted not fixed in piece 9's scope; a malformed byte (e.g. `0xA3`) would diverge between port and legacy | B | roadmap line 258 |
| AVT training-lock's dedicated PLL reads this port's raw/bandpass-filtered domain, not legacy's real post-2-tap-LPF/post-bandpass/**post-AGC** domain | `sstv.cpp:1835`'s `ad` (AGC'd, unscaled) | "AGC-domain gap stays exactly as already flagged and deferred from the Hilbert demodulator piece, not expanded into here" | B | `AnalogFmSstvDecoder.cs:1561-1565`; roadmap lines 788-789 |
| `m_sint1`/`m_sint2`/`m_sint3`'s legacy case-0/1/2/9 "freeze while decoding VIS bits" gating has no equivalent — this port's merged loop evaluates them unconditionally | `sstv.cpp` case structure | Real, low-severity structural divergence, documented rather than left silent | B | roadmap line 121; `AnalogFmSstvDecoder.cs:693` |
| `VisLockStateMachine`'s omitted `m_SLvl`/`m_SLvl2` absolute-amplitude gates, originally flagged as a mid-image false-positive-lock risk once Piece 6c made it run per decoded line | Legacy's absolute AGC'd-scale thresholds | Blocked at the time on the not-yet-built `CLVL` AGC port; **Piece 7c later reintroduced these exact gates into `VisLockStateMachine`'s own trigger conditions** — plausibly resolved, status sent to auditor to confirm | B (status needs confirming) | roadmap lines 142, 153-154 |
| Per-channel TX gain trim (`m_VariOut`) not modeled for any mode | `sstv.cpp:2880` | Confirmed zero effect on transmitted frequency/waveform — optional, off-by-default legacy feature; gain-tag bits are masked off before use regardless | A | roadmap line 44; `SstvModeRegistry.cs:599` |
| `CLVL`'s peak-hold bookkeeping (`m_PeakMax`/`m_PeakAGC`/`m_Peak`/`m_CntPeak`) omitted | `sstv.h` peak-hold fields | Write-only in legacy too — only reader is the UI level-meter bar (`Main.cpp:6186-6192`) | A | roadmap line 149 |
| `m_agcfast==0` branch (averaged-5-window AGC recompute) not ported | `sstv.h:272-279` | Confirmed dead code in legacy itself — constructor unconditionally overwrites to 1 | A | roadmap line 149 |
| No int-truncation replication in RM8/RM12's gain-corrected gray path (and generally, this port keeps doubles where legacy truncates) | `Main.cpp:4437-4449` truncates twice | Measured: a couple of levels' asymmetric divergence around mid-gray, well inside existing tolerances | A | roadmap lines 468-471 |
| `H1`/`H3` bandpass filter width variants (Narrow/VeryNarrow) not ported | `fir.cpp` `MakeFilter` presets | Only reachable via a `DEMBPF` .ini setting this port's settings/UI layer doesn't expose yet; shipped default (Wide/H2) is the only reachable preset | A | roadmap lines 880-887 |
| Same-sample `m_sint1`-then-`m_sint3` double-fire structurally can't happen in this port (return-immediately-on-any-match) | Legacy's straight-line code lets both evaluate the same sample | Explicitly assessed as low severity, "close to a no-op" since `m_Sync` is already 1 by the time it would matter | A | roadmap line 158 |
| `m_ReqSave` (legacy saves a ≥65%-complete abandoned image) not ported | `sstv.cpp:2134-2138`, `Main.cpp:4931-4934` | Correctly blocked on the not-yet-built logging/history feature (Phase 4) | A | roadmap line 143 |
| `m_SyncRestart` hard-wired on, no user toggle | `sstv.cpp:1486`, `Main.cpp:10907`/`11887` | Correctly blocked on the not-yet-built settings/UI layer | A | roadmap line 143 |
| AVT training-lock case 8 folded into case 6's `h==0x40` transition | `sstv.cpp:2234-2239` | Confirmed legacy's own case 8 is unreachable dead code too — no functional effect either side | A | roadmap lines 105, 107-108 |
| `MakeHilbert`'s final unreachable `else{x1=x2=1.0;}` branch not ported | `fir.cpp:432-474` | Provably unreachable — the preceding `n==L` check already excludes the only case that would reach it | A | `HilbertFmDemodulator.cs:211-214` |

Coverage note from the compiling pass: roadmap lines 293-326, 337-373, 377-426, 486-546, 606-638,
666-720, 825-873, 893-954 got lighter/no line-by-line coverage — sent to the auditor as ranges to
specifically re-check for missed items.

**Status: calls (1) and (2) both done and logged above. Call (3) (reconcile + priority ranking,
merging both outputs) launched, in progress as of this entry. Do not start step 3/task #6 (fixes)
until call (3) is back and reviewed.**

**Call (1) results — verify existing table row-by-row. Verdict: EQUIVALENT-WITH-RISKS — mostly
accurate, but 1 row flat-out wrong, 1 stale-resolved, 2 stale/mis-attributed citations, 2 tier changes:**

- **Flat-out wrong (4 things):** (a) the "H1/H3 width variants not ported" row (tier A) is mis-framed
  — H1/H3 aren't width variants gated behind a `.ini` setting, they're the SAME lock-state-selected
  filters as row 1 (`sstv.cpp:1827-1831`), fully reachable at the shipped default — this row duplicates
  row 1's gap at the wrong (too-low) tier, not a separate item. (b) the narrow-further-narrowing row's
  citation (`CPLL::SetWidth`) is stale — legacy's default demod is Hilbert (`m_Type=2`), so the PLL is
  AVT-only now and AVT is never narrow; the real live gap is `CHILL::SetWidth`/`CFQC::SetWidth`, and
  "loop/VCO-gain dynamics" reasoning doesn't even apply to a Hilbert transformer. (c) memory-growth
  figures are pre-Piece-B: actually 5 buffers not 3 (Piece B added `_bandpassFilteredSamples`), ~1.43
  GB/hr @11025Hz / ~5.7 GB/hr @44100Hz, not 1.1/4.4. (d) the `m_sint1/2/3` freeze-gating row claims all
  three are ungated; `m_sint1` was actually already fixed by an earlier holistic-review pass
  (roadmap 163) — only `m_sint2`/`m_sint3` remain ungated.
- **Stale-resolved:** `VisLockStateMachine`'s `m_SLvl`/`m_SLvl2` amplitude gates — verified CLOSED.
  Piece 7c reintroduced every gate legacy has at all 4 real call sites, including the correct
  3-term/2-term asymmetry (case-0/1 use the 3-term `d12>d19 && d12>SLvl && (d12-d19)>=SLvl` form,
  case-3-verify correctly uses only the 2-term form with no difference gate) and the right
  `SLvl=3500/SLvl2=1750` values (`SetSenseLvl` case 1, matching the real ctor default). Downgrade to A
  / fold into row 1 (the only remaining delta is AGC/threshold calibration seeing H2 instead of H1
  while locked — that's row 1's gap, not a separate one).
- **Tier changes:** `TryDecodeVisDataBits` rebuild-per-push: C→**B** (the replay is bounded by a
  search ceiling, ~1.07-1.31s of samples, so it's O(1) per push not O(buffer) — no correctness risk).
  `MiniAudioCaptureSession`'s bare catch: stays C but **broader than stated** — swallows every decoder
  exception, not just the one specific guard originally named.
- **Everything else CONFIRMED** as originally logged, verified line-for-line against real source
  (full detail: this session's auditor transcript, not reproduced here in full).

**Call (2) results — gap search over the 8 under-covered ranges, all confirmed read in full. Found 9
new items** (full table + citations: see this file's own history a few entries up, "Call (2) results"
heading). One of the 9 (`CHILL` narrow-mode retune not ported) is the SAME gap as call (1)'s corrected
narrow-further-narrowing row — a duplicate discovered independently by both calls, which is itself a
good cross-check signal. The other 8 are net-new: `m_Type` demodulator selector has no user toggle
(only Hilbert branch exists); CQ100 mode entirely unmodeled; narrow-FSK header commits at a fixed
nominal offset instead of the real lock sample; a bounded local search ceiling on narrow-FSK/VIS-bit
decode where legacy never permanently gives up; AVT's dedicated PLL warmed up on a clamped 2000-sample
window instead of continuous stream history; AVT training entry skips all 3 VIS repeats before
constructing the lock state machine (legacy enters after the first); `MakeFilter`'s Kaiser/Bessel
design branch unported (dependency note: becomes reachable the moment row 1/H1/H3 ever gets fixed);
`MakeFilter` odd-tap trailing-zero asymmetry (latent, current tap counts are all even); `CHILL`'s
middle decimation tier implemented but never exercised.

**Call (2) results — gap search over ranges 293-326, 337-373, 377-426, 486-546, 606-638, 666-720,
825-873, 893-954, all 8 confirmed read in full:**

Correction caught up front: the biggest gap those ranges originally described — PLL-instead-of-Hilbert
demodulator (`sstv.cpp:1492`/`2256`) — is **closed by Piece 14**, not open. Ranges 293-326, 337-373,
377-426, 825-873 confirmed nothing new (findings already fixed pre-implementation, or — for the noise
harness at 825-873 — genuinely new test infra with no legacy mechanism to diverge from).

New items found (same table shape as the existing inventory):

| Item | Legacy mechanism (citation) | Why deferred | Risk tier | Roadmap/source citation |
|---|---|---|---|---|
| `m_Type` demodulator selector: only the Hilbert branch exists in the picture path; zero-crossing (`m_fqc`) never used for picture demod, no user toggle | 3-way dispatch `case 0: m_pll / case 1: m_fqc / default: m_hill` (`sstv.cpp:2256-2268`, `2310-2318`); user setting `Option.cpp RGDemType`/.ini `DemType` (`Main.cpp:1937`) | No settings/UI layer yet to expose an equivalent toggle | B — default-path parity holds (Hilbert IS legacy's compiled-in default), but legacy users can switch to PLL/zero-crossing for hard signals; no removed-features.md entry | roadmap 565-569, 604 |
| `CHILL` narrow-mode retune not ported — `HilbertFmDemodulator` fixed at 1900Hz/800Hz non-narrow for ALL modes incl. MN/MC | `CSSTVDEM::SetWidth` retunes all 3 demodulators per mode (`sstv.cpp:1707-1715`); `CHILL::SetWidth`'s narrow branch (`sstv.cpp:3024-3031`) | Mirrors `PllFmDemodulator`'s already-accepted same simplification; verified affine-only (`m_OFF`/`m_OUT`), not tap count/`m_df` | B — this is now the LIVE picture-demod path (unlike the old PLL-based MN/MC-narrowing item), unmeasured | roadmap 676; `HilbertFmDemodulator.cs:58-65` |
| CQ100 mode (`-i` switch) not modeled: FIR tap-tripling AND -1000Hz global tone offset | `sys.m_bCQ100` tap*=3 (`sstv.cpp:3048-3050`); `g_dblToneOffset=-1000.0` under `-i` (`Main.cpp:1065-1077`) | No CQ100-equivalent hardware modeled anywhere in this port | A — no removed-features.md entry; code's "g_dblToneOffset confirmed always 0.0" claim is true only absent `-i` | roadmap 676; `HilbertFmDemodulator.cs:72-76` |
| Narrow-FSK header commits at fixed nominal `headerStart + NarrowHeaderTotalDurationMs`, not the state machine's actual lock sample | Legacy `DecodeFSK` fires `Start()` at the lock instant (`sstv.cpp:2378-2606`) | Chosen against TX's real placement (`Main.cpp:7423-7424`) + already-passing round-trip test | B — supporting evidence is a round-trip test, the exact "both sides agree while wrong" shape CLAUDE.md §4 warns about; drift/jitter unmeasured | roadmap 519-521; `AnalogFmSstvDecoder.cs:1176-1181` |
| Bounded local search ceiling on narrow-FSK header/VIS data bits — port gives up; legacy never permanently aborts | Every legacy FSK failure path resets to `m_fskmode=0` and rescans indefinitely (`sstv.cpp:2378-2444`) | Unbounded scan was "a real, reverted regression"; `m_sint3` fallback kept as mitigation | B — mitigated by fallback, but ceiling is architectural not legacy-derived; off-nominal-header behavior unmeasured | roadmap 510-511, 529-530; `AnalogFmSstvDecoder.cs:1168-1174`, `1307-1323` |
| AVT's dedicated PLL warmed up on a clamped 2000-sample window, not continuous stream history | Legacy's `m_pll` runs continuously from stream start (`sstv.cpp:2129/2159/2169/2187/2222`) | Upfront-buffer architecture has no continuously-running detector; mirrors `TryResolveSyncAnchorCorrection`'s technique | B — same family as the inventoried "detector reconstruction per push" but a distinct instance/constant; effect on AVT lock unmeasured | roadmap 683; `AnalogFmSstvDecoder.cs:1540-1558` |
| AVT training entry restructured: port skips all 3 VIS repeats before constructing the lock state machine, rescopes timeout budget | Legacy enters case 4 right after the FIRST repeat, burns repeats 2/3 as marker-search noise | Simplification; fixed `AvtExtraHeaderDurationMs` kept as ceiling on the argument legacy's fallback converges near the same duration | B — convergence argument reasoned, not measured | `AnalogFmSstvDecoder.cs:1513-1522` (no roadmap line — found in code) |
| `MakeFilter`'s Kaiser/Bessel (`I0`) design branch not ported | `fir.cpp:346-427`, activates only at attenuation >=21dB | Provably unreachable today: H2 is always attenuation 20 | A today, but becomes reachable the moment H1/H3 (already inventoried as a separate item) is added — silently gates that follow-up | roadmap 888-890; `SearchBandpassFilter.cs:20` |
| `MakeFilter` odd-tap trailing-zero asymmetry: symmetry test scoped to even taps only | `fir.cpp` mirroring loop writes `2*(tap/2)+1` entries, trailing coeff stays zero for odd tap | Only reachable tap counts (24@11025Hz, 96@44100Hz) are even — latent, not currently wrong | A | roadmap 913-917 |
| `CHILL` middle decimation tier (16-40kHz -> 24 taps, `m_df=1`) implemented but never exercised | `CHILL::SetWidth` tiering (`sstv.cpp:3032-3047`) | Implemented for completeness, untested until/unless a rate in that range is used | A | roadmap 573-574; `HilbertFmDemodulator.cs:54-57` |

Off-scope notes from this pass (not chased, per scope discipline): FSK callsign-ID packet already has
a `docs/removed-features.md` entry, correctly excluded here. RM8/RM12 golden-vector deltas worsened
slightly post-Piece-14 but confirmed legacy-faithful (legacy's own 48-tap CHILL window also exceeds
RM12's pixel dwell at 44100Hz) — not a port simplification.

**Call (3) results — reconcile + priority rank. DONE, task #9 complete.** Corrected the item count:
30 items, not ~20 (call (2)'s own prose undercounted its own 10-row table by one). Full reconciled
table (30 items, S1-S30, tier C/B/A) lives in this session's auditor transcript — condensed version
with priority bands below; the merges applied: H1/H3 folded into S1 (same gap, wrong tier — H1/H3
aren't `.ini`-gated width variants, they're the same lock-state-selected filters, fully reachable at
the shipped default), `VisLockStateMachine` amplitude-gates row dropped (closed, residual folded into
S1's note), narrow-further-narrowing + call (2)'s independently-found CHILL-retune merged into one
item (S9) with the citation corrected to `CHILL::SetWidth`/`CFQC::SetWidth` (not `CPLL::SetWidth` —
legacy's live default is Hilbert, PLL is AVT-only now).

**Priority ranking (5 bands, every item placed):**

- **Band 1 — must fix before Phase 2 starts** (S4 exception-swallowing catch, S2 unbounded memory
  growth [corrected: 5 buffers, ~1.43GB/hr@11025/~5.7GB/hr@44100], S3 AFC/Slant chunk-timing
  sensitivity [the only item with a MEASURED delta, ~1.75], S1 lock-dependent bandpass filter switch,
  S28 Kaiser/Bessel branch [ships in the SAME change as S1 or S1's filter is built by the wrong design
  branch]). Rationale: each either breaks outright under live capture, has its deferral premise
  invalidated by Phase 2 specifically, or destroys the evidence needed to judge everything else.
- **Band 2 — should fix during Phase 2 bring-up, before trusting new fixtures** (S5 VIS-bit-decode
  detector rebuild-per-push, S16 AVT PLL clamped warmup window, S15 bounded search ceiling, S14
  narrow-FSK fixed-offset commit, S6 locked-detector no-retune). Rationale: batch-vs-streaming
  correctness, only "correct" today because the only caller is a test harness pushing whole buffers.
- **Band 3 — worth doing eventually, bundle with the matching new golden-vector fixture** (S31 AVT
  real-capture header-detection failure — **DONE**, see this file's own "S31" entry further down —
  S9 MN/MC narrow retune — **DONE, discovered already closed**: Band-2 item S6 (`6da0a65`) IS this
  exact fix (`HilbertFmDemodulator.ProcessSample`'s `isNarrow`-selected `(off, out)` pairs, gated in
  `AnalogFmSstvDecoder.DemodulatedFrequencyAt` on `_mode.NarrowModeCode is not null`) — this Band-3
  listing was simply never updated when S6 shipped; no new code needed — S8 mid-image narrow re-lock —
  **DONE**, see this file's own "S8" entry below, much smaller than originally scoped (the per-sample
  FSK decoder already existed and was already correct; this wired it up as a persistent scanner) — S7
  mid-image AVT re-lock, S11 AVT PLL domain — **DONE**, see this file's own "AVT package" entry below
  (bundled with S17 per this Band's own pattern-3 finding that the AVT items are one work package) —
  S17 AVT training-entry restructure — **CLOSED via documentation, no code change**, see the same "AVT
  package" entry — measured (not assumed) that the port's analytic training entry lands one block late
  vs. legacy's real search-based entry, but completion timing recalculates absolutely per-block, so the
  imprecision has zero effect on final accuracy — S10 extended-VIS
  7-bit — **DONE**, see this file's own "S10" entry below, widened in scope from the original narrow
  "escape byte only" framing to the real underlying gap (every normal VIS-code match in the
  fixed-window path, not just the escape byte) — S12 sint2/sint3 freeze gating [sint1 already fixed] —
  **DONE**, see this file's own "S12" entry below — first plan-review round caught a real design gap
  before any code shipped (the originally-proposed gate only covered legacy's case-0↔1 boundary, not
  the case-2/9/3 freeze the item is named for) — S13 m_Type demodulator toggle [code blocked on
  Phase-3 settings UI, but its missing removed-features.md entry is Band-4 work now, **DONE**].
  **Band 3 is now fully done, all 8 original items closed** (7 landed on mode families task #7 already
  captured fixtures for, fix when measured not reasoned; S12 needed direct source-derived design work
  instead, no fixture dependency).
- **Band 4 — documentation/test-only, near-zero cost, no DSP change — DONE, all 4 items** (S13's doc
  half already closed alongside S9/S10; S27 CQ100 `removed-features.md` entry + a stale code comment —
  **DONE**, code-level audit caught the first draft's own entry was still incomplete (2 more real
  `m_bCQ100`-gated DSP effects missed) before it shipped; S29 odd-tap assert/guard — **DONE**; S30 added
  a decimation-tier unit test — **DONE**; S21 recorded the already-measured tolerance rationale —
  **DONE**, exact bound measured (2 levels max, `-1..+2` signed) rather than the prior "a couple of
  levels" estimate. See this file's own "Band 4" entry further down for full detail. Zero DSP behavior
  change across all 4 items, confirmed by an unchanged full-suite result.
- **Band 5 — not worth it / correctly blocked** (S18 per-channel TX gain, S19 CLVL peak-hold, S20
  dead `m_agcfast` branch, S22 sint1/sint3 double-fire, S23 `m_ReqSave` [blocked on Phase 4], S24
  `m_SyncRestart` toggle [blocked on Phase 3], S25 AVT case-8 dead code, S26 `MakeHilbert` unreachable
  branch). Verified dead/no-effect/correctly-phase-blocked — do not touch.

**5 bigger patterns found, most important first:**

1. **The dominant one: "upfront buffer vs. real-time stream" is ONE architectural gap masquerading as
   7 separate items** (S1, S2, S3, S5, S15, S16, and arguably S6) — this port processes a growing
   buffer in bulk passes where legacy runs one continuous per-sample loop over persistent detector
   state. That's 7 of 30 items, including 3 of 4 tier-C rows and the only measured defect.
   **Recommendation: decide the streaming contract explicitly (persistent per-detector cursors + a
   bounded ring buffer + one incremental pump) BEFORE writing individual fixes — most of Band 1/2
   collapse into one design change with one test suite instead of seven patches that each get
   revisited once Phase 2 lands anyway.**
2. Filter-selection cluster (S1+S28+folded-H1/H3+folded-VisLockStateMachine-residual) is one fix, not
   four — shipping S1 without S28 ships a filter built by the wrong design branch and looks correct
   while being wrong.
3. AVT (S7/S11/S16/S17) and MN/MC narrow (S8/S9/S14/S15) are mode-family holes, not 9 independent
   bugs — each is one work package gated on the one fixture step 4 already plans to capture.
4. Two missing `docs/removed-features.md` entries (S27 CQ100, S13 `m_Type` selector) are CLAUDE.md §2
   process-rule debt, not DSP debt — one sitting, do alongside Band 1 regardless of code-fix ranking.
5. **The evidence base is thin and it's fixable in parallel**: only S3 has a measured number; S21 is
   measured-and-bounded; everything else in Bands 2-3 is reasoned, not measured. Starting golden-vector
   capture (step 4) now, in parallel with Band 1 fixes, would let Bands 2-3 get re-ranked by
   measurement instead of argument — directly settles S14/S9/S17/S7's self-described "unmeasured"
   status.

**Pre-check RESOLVED (2026-08-02): S28 drops out of Band 1.** Read `CalcBPF` directly
(`sstv.cpp:1522-1551` + `CalcNarrowBPF`): at the Wide preset (`bpf=1` — the ONLY preset this port
currently reaches; Narrow/VeryNarrow are gated behind a `.ini` DEMBPF setting with no UI yet), H1's
attenuation is **20**, matching H2's 20 exactly (`sstv.cpp:1530-1531`, `CalcNarrowBPF` case 1 for H3
also 20). Confirmed in `fir.cpp:364` that the Kaiser/Bessel branch only activates at `att>=21`. So
porting the lock-dependent H1/H3 switch (S1) at Wide-preset scope needs **no** Kaiser/Bessel work —
S28 only resurfaces if Narrow/VeryNarrow (att 40/50, `sstv.cpp:1536-1537`/`1542-1543`) are ever
implemented, which stays its own separate, correctly-blocked (no settings/UI) tier-A item.

**Band 1 is now 4 items, not 5: S4, S2, S3, S1.** No same-change coupling requirement for S1 anymore.

**Status: task #9 complete, S28 pre-check resolved. Starting task #6 — working Band 1 items one by
one through the normal process (plan -> auditor plan-review -> implement -> test), per user
instruction. Order and per-item plans logged as each one starts, below.**

### Band-1 item 1 (S4) — exception-swallowing catch in `MiniAudioCaptureSession`, DONE

Fixed an exception-swallowing catch in `MiniAudioCaptureSession`, per auditor plan-review. Committed `288d5d0`.

Full detail: `spec/14-roadmap-archive.md`.

### Band-1 items 2+3 (S2 memory growth, S3 chunk-timing sensitivity) — per user instruction, following

Investigated S2 (memory growth) and S3 (chunk-timing sensitivity) as one combined piece, per the auditor's Pattern-1 recommendation and user instruction. S3 (the chunk-timing race) fully fixed within this section, committed `365d57b`; S2 (buffer trimming) confirmed separable from S3 and does not need to wait — carried into the next entry.

Full detail: `spec/14-roadmap-archive.md`.

### Band-1 item 2 (S2) — unbounded sample-buffer memory growth, DONE

Fixed the unbounded sample-buffer memory growth (S2, buffer trimming/watermark logic). Committed `86e3af6`.

Full detail: `spec/14-roadmap-archive.md`.

### Band-1 item 4 (S1) — lock-dependent bandpass filter switch (H2 search vs. H1 locked), PLANNED

Plan-reviewed the lock-dependent bandpass filter switch (H2 search vs. H1 locked); round 1 found a real blocker and paused for a design decision among 3 options. Sub-piece 4a implemented and committed within this section ("4a done and committed"); the heading's original "PLANNED" status is superseded by that outcome. 4b (the actual filter switch) continues in the next entry.

Full detail: `spec/14-roadmap-archive.md`.

### Band-1 item 4b — the actual H1/H2 filter switch, DONE

Implemented the actual H1/H2 filter switch: `SearchBandpassFilter` now carries both coefficient tables over one shared delay line, with `useLocked` selecting between them, mirroring `CFIR2::Do`'s shape exactly. Completes Band 1 (all 4 items — S4, S2, S3, S1 — closed).

**Correction, later found:** this entry's own "Completes Band 1... S1 closed" claim was wrong for H3/HBPFN specifically — S1 was scoped here as "H1/H3" (see this file's own pattern-2 finding, "H1/H3 folded into S1, same gap"), but only H1 (locked/wide) shipped with this item; H3 (locked/narrow, MN/MC) stayed unported, silently narrowing the heading from H1/H**3** to H1/H**2**. Band 3's later "all 8 items closed" claim doesn't cover this either — its own S9 turned out to be `HilbertFmDemodulator`'s narrow-width demodulator retuning, a different mechanism, not this BPF filter.

**H3/HBPFN, DONE (decoder_quality_improvement.md §5.1).** Ported `CalcNarrowBPF` (`sstv.cpp:1553-1594`): `SearchBandpassFilter.BuildNarrowLockedFilter` builds a mode-tuned locked/narrow filter for the 6 MN/MC modes, called reactively from `Commit()` once the locked mode is known, mirroring legacy's `Start()`-time `CalcNarrowBPF` call. Passed 2 plan-review rounds + 1 code-review round (all explicit "yes") on first landing, then was reverted the same day — the only evidence available then (clean-signal golden vectors) showed no measured benefit, and legacy-parity alone wasn't judged sufficient justification. Reapplied unchanged after `ImpairmentSweepHarness.cs` (new: a controlled-noise, port-vs-source-image sweep, decoupled from legacy) found the real, targeted evidence: under a calibrated AWGN sweep, all 6 MN/MC modes showed a noise floor 13-17dB worse than every other tested mode before this fix, and 7-13dB better after it, at every tested SNR level, with the other 7 tested modes bit-identical before/after (confirmed both empirically and structurally: `NarrowModeCode` is non-null for exactly these 6 of the 43 registered modes, so H3 is unreachable for any other mode).

## Band 2 — scoping, "Pattern 1" recommendation revisited and withdrawn

Asked the auditor whether its own original Pattern-1 recommendation ("decide the streaming contract explicitly before individual fixes") still held now that Band 1 had real outcomes to check it against — concluded no unifying architectural change was needed; recommendation withdrawn. Recommended per-item order set for Band 2: S5 → S16 → S14 → S6 → S15.

Full detail: `spec/14-roadmap-archive.md`.

### Band-2 item S5 — persistent VIS-bit-decode detectors, implemented

Implemented persistent VIS-bit-decode detectors per the scoped Band-2 plan.

Full detail: `spec/14-roadmap-archive.md`.

### Band-2 item S16 — AVT PLL warm-up, corrected scope and implemented

AVT PLL warm-up: scope corrected, then implemented.

Full detail: `spec/14-roadmap-archive.md`.

### Band-2 item S14 — DONE (commit `7a153a0`)

Implemented per the verified plan, no changes to the plan itself needed (`FskSpaceAt` added mirroring `D11At`/`D12At`/`D19At`; `D19At` reuse for mark confirmed correct by 2 rounds of auditor code-level review). Committed `7a153a0`. Section retains the original pre-implementation plan-review writeup (paused mid-session at 92% budget) as a nested note, since the follow-up implementation confirmed that plan needed no changes.

Full detail: `spec/14-roadmap-archive.md`.

### Band-2 item S6 — DONE (commit `6da0a65`)

`HilbertFmDemodulator.ProcessSample` gained an `isNarrow` parameter selecting between normal and narrow tuning pairs. Committed `6da0a65`.

Full detail: `spec/14-roadmap-archive.md`.

### Band-2 item S15 — CLOSED via documentation, no code needed. Band 2 fully done.

Re-reading `AnalogFmSstvDecoder.cs`'s own doc comments from Band-1 item 2 found this item's core concern already investigated and covered by earlier work — closed via documentation, no code needed. Band 2 fully done (all 5 items: S5, S16, S14, S6, S15).

Full detail: `spec/14-roadmap-archive.md`.

## Task #7 — six new golden-vector fixtures captured (scottie-s1, robot72, pd90, rm8, mn110, avt)

Source images generated (matching the existing golden-vector fixture pattern, sized to each mode's exact canvas) and captured by the user against the real legacy Windows binary for 6 new modes; wired into `GoldenVectorTests.cs`/`GoldenVectorFixtureReaderTests.cs`.

Full detail: `spec/14-roadmap-archive.md`.

## S31 — AVT's real capture never decoded: root-caused and fixed

The original working hypothesis (AVT's long header not tolerating real-world timing jitter) was wrong — investigated properly in a follow-up session and the actual mechanism found and fixed empirically.

Full detail: `spec/14-roadmap-archive.md`.

## S9, S13 — closed while working the remaining Band-3 items (S9 already done, S13 doc-only)

Investigating S9 and S13 before writing any code (per the project's "fix when measured, not reasoned" rule) found S9 was already done and S13 needed only a documentation fix — both closed without new code.

Full detail: `spec/14-roadmap-archive.md`.

## S10 — extended-VIS escape byte (and every normal VIS byte) decided from 7 bits, not legacy's real 8

Widened in scope from the original narrow framing ("extended-VIS escape byte") to the real underlying gap: legacy's `m_VisData` accumulator is always 8 bits (7 data + parity), not 7. Fixed for the escape byte and every normal VIS byte; test count 460/460.

Full detail: `spec/14-roadmap-archive.md`.

## S8 — mid-image narrow-mode (MN/MC) FSK re-lock

Scope turned out much smaller than the roadmap implied: `NarrowFskHeaderDecoder` (the per-sample port of legacy's `CSSTVDEM::DecodeFSK`) already existed, already faithful, already tested. Remaining gaps fixed directly; test count 465/465.

Full detail: `spec/14-roadmap-archive.md`.

## AVT package: S7 (mid-image re-lock), S11 (PLL signal domain), S17 (training-entry restructure)

Bundled S7 (mid-image re-lock), S11 (PLL signal domain), and S17 (training-entry restructure) as one plan-review, since all three are one AVT work package rather than independent items. Auditor verdicts recorded per item; each item's actual fix follows in its own entry below.

Full detail: `spec/14-roadmap-archive.md`.

### S7 — mid-image AVT re-lock, DONE

Fixed `TryVisLockStateMachine` (mid-reception re-verification) discarding an AVT match found mid-image, by extracting the same in-progress-image teardown `Commit()` already performs for every other restart.

Full detail: `spec/14-roadmap-archive.md`.

### S11 — AVT training PLL signal-domain mismatch, DONE

Fixed the AVT training PLL feed to read the AGC output before the `*32`/±16384-clip scaling every other envelope-detector consumer needs, matching legacy's `m_pll.Do(ad)` exactly.

Full detail: `spec/14-roadmap-archive.md`.

### S17 — AVT training-entry restructure, CLOSED via documentation, no code needed

The port's analytical 3-VIS-repeat skip and legacy's real case-3-hands-off-after-the-first-repeat are different but behaviorally equivalent paths to the same training-entry point — documented, no code change needed.

Full detail: `spec/14-roadmap-archive.md`.

### Code-level review, all three items combined

Combined code-level review of S7/S11/S17 verdict: EQUIVALENT-WITH-RISKS, no blockers. S11's signal domain confirmed bit-exact against `sstv.cpp`; S7's `AbandonInProgressImage()` and every `_agcSamples` reader confirmed correct.

Full detail: `spec/14-roadmap-archive.md`.

## S12 — m_sint2/m_sint3 freeze-while-decoding-VIS gating, DONE

Fixed `TrySyncIntervalDetectionStep`'s `m_sint2`/`m_sint3` blocks to gate like legacy's real `switch(m_SyncMode)` (`m_sint1` had already been fixed in an earlier holistic-review pass).

Full detail: `spec/14-roadmap-archive.md`.

## Band 4 — documentation/test-only items (S27, S29, S30, S21), DONE

All four Band-4 items (S27, S29, S30, S21) are pure documentation/test additions with no DSP behavior change, verified by an unchanged full-suite result before and after — done per the user's request for "a good deep documentation and comment update and audit."

Full detail: `spec/14-roadmap-archive.md`.

### S27 — CQ100 mode: missing `removed-features.md` entry + a stale code comment

Added the missing `removed-features.md` entry for CQ100 mode and fixed a stale code comment, after confirming via exhaustive grep that the feature is unreachable from any path this port's architecture can exercise.

Full detail: `spec/14-roadmap-archive.md`.

### S29 — `MakeFilter` odd-tap trailing-zero divergence: add an executable guard

Added an executable guard test (2 theory cases) pinning `MakeFilter`'s odd-tap trailing-zero behavior, guarding against a future refactor silently diverging from legacy's real (currently unreachable) behavior.

Full detail: `spec/14-roadmap-archive.md`.

### S30 — `CHILL` middle decimation tier (16-40kHz): add instance-level coverage

Added instance-level test coverage for `CHILL`'s previously-untested-at-instance-level middle decimation tier (16-40kHz).

Full detail: `spec/14-roadmap-archive.md`.

### S21 — RM8/RM12 int-truncation divergence: record the exact measured bound

Recorded the exact measured bound of legacy's double-int-truncation chain (RM8/RM12) in a new test, confirming the existing doc comment's identification of the divergence was already correct.

Full detail: `spec/14-roadmap-archive.md`.

## Milestone audit, Phase 1+2 (docs/audit-playbook.md) — 2026-08-04

Ran the milestone-audit playbook (`docs/audit-playbook.md`), scoped down to units actually built so far, after Band 1-4 closed and Task #7's 8 golden-vector fixtures landed.

Full detail: `spec/14-roadmap-archive.md`.

### Phase 1 unit map

Reference table mapping each Phase-1 DSP unit to its files, legacy reference, and golden-vector coverage, produced as part of the milestone audit.

Full detail: `spec/14-roadmap-archive.md`.

### Phase 2 batches (all 4 run, `auditor` subagent, isolated context each)

4 `auditor` subagent batches (isolated context each) covering TX encoders and RX decoders vs. legacy. Verdict: EQUIVALENT-WITH-RISKS — per-line channel order, sync/separator placement, per-channel durations, and line counts confirmed correct for all 43 modes.

Full detail: `spec/14-roadmap-archive.md`.

### Findings, prioritized (MUST/SHOULD/COULD/NICE-TO-HAVE)

Full enumerated list of bugs/risks the milestone audit found, prioritized MUST/SHOULD/COULD/NICE-TO-HAVE. Every MUST item was fixed (see MUST fixes 1-3 and MUST 4 below); SHOULD items were worked through in "Working the SHOULD backlog" below (2 items — 6 and 10 — explicitly assessed and deferred, not fixed, tracked separately as still-open).

Full detail: `spec/14-roadmap-archive.md`.

### MUST fix 1 — AVT buffer-trim, DONE

AVT buffer-trim bug: dedicated regression test written first (confirmed to fail on the pre-fix code with the predicted magnitude), then fixed, re-confirmed, code-reviewed.

Full detail: `spec/14-roadmap-archive.md`.

### MUST fix 2 — `TryNarrowFskScan` whole-buffer ordering bug, DONE

`TryNarrowFskScan` whole-buffer ordering bug: same fix-then-verify discipline as MUST fix 1.

Full detail: `spec/14-roadmap-archive.md`.

### MUST fix 3 — pixel-pitch trim accumulator drift, DONE

Pixel-pitch trim accumulator drift: same fix-then-verify discipline as MUST fixes 1-2.

Full detail: `spec/14-roadmap-archive.md`.

### Next steps

**All 3 MUST fixes are now DONE.** Also tracked, not yet actioned: the deferred `TryVisLockStateMachine`
narrow-FSK exposure noted in MUST fix 2's own entry; the Robot 36 tone-selector read-timing risk (SHOULD
item 12); the Robot 36/72 step-edge test coverage gap noted in MUST fix 3's own entry above. None of
these block anything — all are real, honestly documented, deliberately deferred. Remaining SHOULD/COULD/
NICE-TO-HAVE items from the original Phase 2 findings list are still open. Phase 3 (chain/integration
audit)'s own TX-side-tests-first prerequisite (user's own explicit directive, see above) is now DONE —
see "TX-side golden-vector tests wired in" below.

**Explicit prerequisite before Phase 3 (chain/integration audit): build TX-side verification tests and
confirm them first.** Phase 3's own mandate is to verify real input through the composed chain against
LEGACY-captured output, NOT an internal round-trip — but batch A's finding stands: TX currently has
**zero** legacy-decode-verified coverage, only internal round-trip (this port's own encoder → this
port's own decoder agreeing with itself), which is exactly the failure shape CLAUDE.md's own Scottie
incident warns about. Running Phase 3's TX-side chain audit on top of that gap would mean auditing
against a reference this port doesn't actually have yet. User's explicit direction: get TX tests built
and verified BEFORE running Phase 3, not after — do not skip straight to the chain audit with this gap
still open. This likely means capturing TX-side golden vectors (this port's own encoder output run
through a real legacy decode, the reverse direction of the existing RX fixtures) — bottlenecked on the
user's own time with the real legacy binary, same category as Task #7 and finding 13's Scottie-DX/MR73/
R24 coverage gaps, not something this session can do unaided.

### TX-side capture prep — DONE (waiting on the user's own real-legacy-install time)

Prep work for capturing more real-legacy TX-side golden-vector audio, waiting on the user's own real-legacy-install time.

Full detail: `spec/14-roadmap-archive.md`.

### TX-side golden-vector tests wired in — DONE (all 11 modes) — 2026-08-04

Wired the captured TX-side fixtures (all 11 modes) into the golden-vector test suite.

Full detail: `spec/14-roadmap-archive.md`.

### Phase 3 — chain/integration audit (docs/audit-playbook.md) — 2026-08-04

2 `auditor` calls (RX chain, TX chain), isolated context each, auditing the seams between already-reviewed units (Phase 2) rather than re-litigating per-unit correctness.

Full detail: `spec/14-roadmap-archive.md`.

#### MUST 4 — RX per-line cursor rounds every line; legacy never rounds within a transmission

RX per-line cursor rounds every line where legacy never rounds within a transmission — root-caused (`AnalogFmSstvDecoder.cs` computes `lineSampleCount = round(_effectiveSamplesPerLine)` and accumulates the rounded value every line). Fix shape specified here; actually implemented in the "MUST 4 — RX per-line cursor rounding, DONE" entry below.

Full detail: `spec/14-roadmap-archive.md`.

#### Other RX chain findings

- **[risk] No stated scheduler/re-entrancy contract for `LineDecoded`/`DecodeRestarted`/`ModeDetected`.**
  All three are invoked synchronously from inside the cursor-advance loop (`:1127`/`:1171`/`:2112`). A
  subscriber that re-enters `PushSamples` (directly or via a scheduler) would resume the outer loop with
  stale `mode`/`pixels`/`lineDecoder` locals against a different `_consumedSamples` epoch. Unreachable
  today (no production subscriber exists yet), but CLAUDE.md §4's concurrency rule requires every
  cross-thread stream to state its scheduler and slow-subscriber behavior — none of these three do yet.
  Real landmine for the eventual `ScanlineStudio.Application`/UI wiring.
- **[risk] `LineDecoded` hands a live alias of the mutable `_pixels` array**, not a copy (`:1127`,
  `MutableImageSource` wraps the same array `Commit`/`AbandonInProgressImage` later replace or mutate).
  Same landmine category as above — the eventual UI subscriber needs to know this before it queues an
  `IImageSource` for later rendering.
- **[nit] Last pixel of a line can read an AFC-uncorrected sample**, ≤1px, one `m_AFCDiff` magnitude —
  `ApplyAfcCorrections`'s bound (`:1110`) and the decoders' own line extent can differ by up to ~0.85
  samples (two independent roundings).
- **[nit] `_afcBoundSample` computed from the nominal sample rate, not the effective (slant-corrected)
  one** — already-recorded NICE-TO-HAVE 24, now confirmed larger in practice than that entry's original
  "~0.1%" estimate once MUST-4's own drift is accounted for (robot-72 overruns by ~120 samples with the
  drift present).

Checked and clean (explicitly, not skipped): cross-module unit/scaling handoffs (AGC → bandpass →
Hilbert demod, all traced against `sstv.cpp`'s equivalent domain, no unconverted handoff); filter-config
flips at the `useLocked`/`isNarrow` boundary (one delay line, coefficient-table swap only, no stale
parallel state); second-image reset completeness (every field not reset by `EndOfImage` is either
unconditionally reassigned on the next `Commit`, deliberately persistent to match legacy, or already
tracked — no fourth un-reset item found beyond MUST fixes 1-3).

#### TX chain: why the MUST-bug-class is structurally impossible here

Confirmed legacy's TX accumulator (and this port's mirror of it) resets only once per transmission, never per line/segment — the RX-side cursor-rounding bug class is structurally impossible on the TX side.

Full detail: `spec/14-roadmap-archive.md`.

#### Phase 3 summary

Findings prioritized: MUST 4 (RX line-cursor rounding) was the only chain-level bug found; everything Phase 1-2 already covered remained fixed.

Full detail: `spec/14-roadmap-archive.md`.

### MUST 4 — RX per-line cursor rounding, DONE

RX per-line cursor rounding: same fix-then-verify discipline as MUST fixes 1-3 — dedicated regression test written first (confirmed to fail pre-fix), then fixed, re-confirmed, code-reviewed.

Full detail: `spec/14-roadmap-archive.md`.

## Working the SHOULD backlog — 2026-08-04

Worked through all 13 open SHOULD items (10 from Phase 1-2, 3 from Phase 3), triaged by effort. Doc-only items (event-scheduler contract, `LineDecoded` live-alias, finding-13 status) handled inline; real code fixes covered in the entries below.

Full detail: `spec/14-roadmap-archive.md`.

### TX image/mode dimension-contract guard (Phase 3 SHOULD) — DONE

TX image/mode dimension-contract guard (Phase 3 SHOULD item) implemented.

Full detail: `spec/14-roadmap-archive.md`.

### Robot 36 tone-selector read-point hardening (SHOULD item 12) — DONE

Robot 36 tone-selector read-point hardening (SHOULD item 12) implemented.

Full detail: `spec/14-roadmap-archive.md`.

### Luma `Limit256` clamp added to 3 RX decoders (SHOULD item 11) — DONE

Luma `Limit256` clamp added to 3 RX decoders (SHOULD item 11) implemented.

Full detail: `spec/14-roadmap-archive.md`.

### Narrow-FSK suspended during AVT training window (SHOULD item 7) — DONE

Narrow-FSK suspended during the AVT training window (SHOULD item 7) implemented.

Full detail: `spec/14-roadmap-archive.md`.

### TryInterleavedHeaderScan's entry invariant (SHOULD item 9) — documented, no behavior change

`TryInterleavedHeaderScan`'s entry invariant and its interaction with `AbandonInProgressImage()` documented; no behavior change needed.

Full detail: `spec/14-roadmap-archive.md`.

### Pre-lock watermark strict inequality (SHOULD item 8) — DONE

Pre-lock watermark strict inequality (SHOULD item 8) implemented.

Full detail: `spec/14-roadmap-archive.md`.

### Items 6, 10 — assessed, deferred (not fixed)

Both looked cheap on first read; each turned out to need either a real architectural change (item 6) or
carried a genuine regression risk once traced through (item 10) -- disproportionate to their own
[C]/[B] severity and currently-unreachable/bounded status. Documented precisely instead of fixed, so the
investigation isn't lost and isn't silently re-discovered later.

**Item 6 (mid-image narrow restart stale demod-cache config)**: `BandpassFilteredSampleAt`/
`DemodulatedFrequencyAt` are forward-fill caches that freeze each index's `useLocked`/`isNarrow` gate
decision AT COMPUTE TIME, never revisited. When a non-narrow locked image is abandoned mid-image for a
narrow-mode commit (S8), a mid-image narrow-FSK interrupt's own anchor can land BEHIND where these
caches already advanced to (the just-decoded line's own pixel reads already drove them forward,
~1 line's worth) -- so the new narrow image's own first few samples read values computed under the OLD
mode's filter config. Investigated whether this is even a real port-vs-legacy divergence (legacy's own
real-time single-pass filters can't retroactively reprocess a sample either, once fed) -- concluded it
genuinely is: legacy is NEVER "ahead" of real time, so it never creates this situation in the first
place, while this port's lazy forward-fill caching (needed to support bulk-push callers) can race ahead
of the logical decode position, creating a staleness window with no legacy equivalent. A correct fix
needs retroactive cache invalidation AND a filter-state rewind/checkpoint for `SearchBandpassFilter`/
`HilbertFmDemodulator` (both single-delay-line, coefficient-swap-only filters with no snapshot/restore
mechanism today, confirmed by reading both classes) -- a real architectural addition, disproportionate
to a bounded ~1-line, [C]-severity finding. Deferred, not chased further this pass.

**Item 10 (`PixelSampleReader`'s `Math.Clamp` vs `Rel()`'s throw)**: real inconsistency (one component
silently substitutes a boundary sample, the other throws loudly, for what's structurally the same
"read behind the trim watermark" condition) but changing the clamp to a throw is a genuine behavior
change with an unclear benefit -- "safe today" per the existing comment (locked watermark stays 2000
samples of margin back), and a new throw path risks surfacing in production differently than the silent
substitution would, for a component (`PixelSampleReader`) that's the single highest-consequence reader
in the file (the one that actually writes pixels). Left as documented, not changed -- flagged as a real
inconsistency worth a future look if this margin's own assumptions ever change, not fixed reactively
without a concrete failure to design against.

## TX-side SHOULD cluster (items 4, 5) — DONE, in an isolated fork worktree

TX-side SHOULD items 4 (frequency-mapping integer truncation) and 5 (`OutHEAD` leader-tone port) run as a parallel `fork` (isolated git worktree), alongside the main session's RX-orchestrator cluster work.

Full detail: `spec/14-roadmap-archive.md`.

### SHOULD item 4 — TX frequency-mapping integer truncation — DONE

TX frequency-mapping integer truncation (SHOULD item 4) implemented.

Full detail: `spec/14-roadmap-archive.md`.

### SHOULD item 5 — OutHEAD pre-VIS leader-tone burst — DONE

`OutHEAD` pre-VIS leader-tone burst (SHOULD item 5) implemented.

Full detail: `spec/14-roadmap-archive.md`.

## Phase 2 — Radio layer (no CAT rigs yet) — DONE

`ScanlineStudio.Abstractions.Radio` interfaces, `RadioController` (`IRadioController`), and `RigctldClientProtocol`/`RigctldProtocolFactory` (client mode) landed together. 639/639 tests passing solution-wide (528 pre-existing DSP + 111 new/other, none regressed).

Full detail: `spec/14-roadmap-archive.md`.

## Linked Hamlib backend ("bring-your-own-libhamlib") — DONE, ahead of its original Phase 4 slot

Built directly after Phase 2 rather than waiting for Phase 4, since the packaging design question (not calendar ordering) was the actual blocker on starting it.

Full detail: `spec/14-roadmap-archive.md`.

## Phase 3 — Minimal UI, first end-to-end path — DONE

`MainWindow` walking skeleton (waterfall, RX image panel, TX controls) wired to Phase 1/2 services through `ScanlineStudio.Application`, shipped as 3 real panes in the then-current `Dock.Avalonia` layout plus a fixed radio/frequency status strip — **that layout was later fully replaced** by a fixed Receive/Transmit/Gallery/Logbook `TabControl` shell (see [[09-ui]]'s "Main window layout" section); `Dock.Avalonia` is not a package reference of `ScanlineStudio.UI.csproj` today. `ILocalizationService` + `Translate` in place from the start. Minimal image pipeline (load + fit-to-mode resize) shipped.

Full detail: `spec/14-roadmap-archive.md`.

## Phase 4 — Making the program usable: settings, options, dialogs, logbook

> **Its still-open bullets are folded into "Path to 0.9 beta / road to 1.0" above** (Options
> dialogs, `.ini` importer, localization → Tier 2; `TemplateCatProtocol` fallback, offline
> callsign/country lookup → Tier 2; flrig/OmniRig-as-client → Tier 3). This section stays as the
> fuller design rationale.

**Re-scoped 2026-08-05 (direct user decision)**: Phase 4's organizing theme is now "the program is
actually usable day to day," not just "CAT protocols + image tooling." Two changes from the original
plan: rigctld **server mode** is dropped outright (see [[04-rigctld]]'s "Purpose"/"Server mode" sections
— built-in linked Hamlib plus rigctld client-mode coverage is enough CAT surface; a server role wasn't
worth the added scope), and the settings/dialogs/localization-completion work originally bucketed under
Phase 5 ("Extensibility and polish") moves up into Phase 4, since a usable program needs its settings
UI and remaining dialogs before it needs a plugin system. Phase 5 is now just extensibility (see below).

- [[03-cat-layer]]: linked Hamlib backend — **done early** (see "Linked Hamlib backend" section above,
  landed right after Phase 2 instead of waiting for Phase 4). `TemplateCatProtocol` fallback still here.
- flrig client backend — **done** (see [[03-cat-layer]]'s "Definition of done"), implemented ahead of
  the original "maybe later" plan below because the user asked for it directly, not because post-launch
  demand materialized.
- OmniRig-as-client backend — **done** (accepted 2026-08-29 as `ui_transition_plan.md` Tier 3
  decision, implemented same day after 2 rounds of `auditor` plan-review — see [[03-cat-layer]]'s
  "Definition of done"). No real-interop test exists (no Windows machine/OmniRig install in this
  dev environment), unlike Hamlib's real-interop coverage — tracked as an open item there.
- [[07-image-pipeline]]: full crop/resize/overlay, stock library, RX history — **done** (`ITransmitImagePreparer`, `TxImageEditorPaneViewModel`/`TxImageEditorPaneView`, `IStockImageLibrary`, `IReceiveHistoryStore`). **Filters shipped too, corrected**: this line used to say filter/preset support was deferred to [[11-plugin-system]] as a separate plugin surface — instead it shipped in-interface as `ITransmitImagePreparer.ApplyAdjustments` (brightness/contrast/saturation/gamma/sharpen/denoise), and template rendering shipped as `ApplyTemplate` ([[15-template-designer]]); [[11-plugin-system]] never gained an `IImageFilter` extension point.
- [[08-logging]]: logbook, ADIF import/export, offline callsign lookup; QRZ.com opt-in lookup can trail slightly if needed. **Mostly done, corrected 2026-08-22** — this line used to say "Not started." The logbook store, ADIF import/export, ADIF-UDP forwarding, QRZ.com online lookup/upload, and the Logbook UI pane all shipped (2026-08-07 through 2026-08-15). Only the **offline** callsign/country lookup remains not started, blocked on a human emailing Clublog for a `cty.dat` API key (see [LICENSES.md](../LICENSES.md)'s "Candidate future asset" note).
- [[09-ui]]: remaining dialogs from the inventory table — `OptionsDialog` (tabbed general/TX/RX/audio settings), `RadioSettingsDialog`, `MacroKeyEditor`, `ColorSettingsDialog`, `LanguageSettingsDialog`. (Moved from Phase 5 — `PluginManagerDialog` stays in Phase 5, it has no purpose without the plugin host it's Phase 5's own primary deliverable.)
- [[12-settings]]: legacy `.ini` importer, migration chain exercised by a real version bump. (Moved from Phase 5.)
- [[10-localization]]: remaining views localized, community-translation-friendly locale-file workflow documented. (Moved from Phase 5.)
- ~~[[04-rigctld]]: server mode~~ — **dropped**, not deferred. See the re-scoping note above.

**Demo:** the program is fully usable day to day — configure settings/rig/macros/colors/language through
real dialogs, operate SSTV with a directly-connected rig, and keep a proper log. This is the point at
which YONIQ v2 first matches legacy MMSSTV/YONIQ's core day-to-day workflow, plus a real settings/options
experience legacy users would recognize.

## Phase 5 — Extensibility

**Trimmed 2026-08-05** to just the plugin system — everything else previously bucketed here (remaining
dialogs, settings migration, localization completion) moved into Phase 4 above.

- [[11-plugin-system]]: plugin host, at least one built-in extension point (`IImageFilter`) proven to load through the plugin path, plus `PluginManagerDialog` ([[09-ui]]'s inventory table). **Stale as of 2026-08-16**: this used to say a future `ITemplateItem` extension point ([[15-template-designer]]) would be the higher-value target to prove the plugin path against — that document's redesign explicitly moved a CItems-successor plugin point out of scope, so `IImageFilter` is the only concrete target now.

**Demo:** a user can extend the app with a plugin (at minimum, an image filter loading through the plugin
path).

## Phase 4+ backlog — legacy YONIQ/QSSTV feature inventory (2026-08-05)

> **Candidate pool, not a priority list** — see "Path to 0.9 beta / road to 1.0" above for what
> actually to work from. This inventory is raw research material this list draws on, not itself
> ranked.

Four parallel research passes (logbook/QSO, TX macros + CW-ID, waterfall/color, RX/TX
quality-of-life), each verifying claims directly against `yoniq-old/YONIQ-main/` source
(QSSTV-main/` cross-checked only as secondary inspiration, never a port target) after the
Settings/Options/radio-telemetry system (Pieces 1-6 above) shipped. Not yet scheduled into a
phase or scoped into a build plan — a UI-design pass is happening first; this is a tracked
candidate list to pull from once that's further along, not a commitment.

**Logbook/QSO tracking** ([[08-logging]]'s plan already matches legacy reality on format/ADIF/
QRZ.com/cty.dat scope — these are the deltas found):
- QSL sent/received flags — legacy has them, `QsoRecord` doesn't. Trivial.
- ~~Maidenhead grid locator~~ — **correction (2026-08-08)**: this claim was stale by the time the
  logbook backend actually shipped (2026-08-07) — `QsoRecord.GridSquare` already exists
  (`src/ScanlineStudio.Abstractions/Logbook/QsoRecord.cs`), verified directly, not a gap.
- Duplicate-QSO detection (by callsign, or callsign+band) — real legacy feature
  (`LogSet.cpp`/`LogFile.h`'s `m_CheckBand`), not in the current plan. Small.

**TX macros / CW-ID** (legacy's "macro" is a token-picker popup, not a saved template — shared
across the overlay editor, CW-ID text, and repeater auto-answer via one substitution function,
`MacroText`):
- ~~Token-picker UI + substitution service~~ — **done (2026-08-08)**, scoped to the tokens
  sourceable from `OperatorSettings`/system clock (`%m`/`%D`/`%T`/`{name}`/`{grid}`) — `MacroTextResolver`,
  wired into the overlay editor's insert-field chips. Legacy's full token set (his-callsign/his-
  name/his-QTH/RST exchange/greetings) still needs a "current QSO" form concept that doesn't
  exist, same class of gap as CW-ID below — deferred, not silently dropped.
- CW-ID (real, working legacy feature — not dead like Vari SSTV) — low-medium complexity, and
  can reuse `SstvSessionService`'s existing `TuneAsync`/`GenerateTone`/`PlayWithPttAsync` (built
  for the Tune button, Piece 5/6 above) rather than needing a new audio subsystem. Can now reuse
  `MacroTextResolver` too, once built, for its own text-token expansion.
- The two only intersect at token substitution; buildable independently.

**Waterfall/color** (still plain grayscale — a prior, standing decision already deprioritized
this; the items below are a complete inventory, not a priority push):
- No color rendering at all vs. legacy's real 7-color palette (gradient low/high, FFT
  background/trace/peak-hold, sync marker, freq marker — confirmed the exact list from
  `Option.cpp`, NOT `ColorSet.cpp`/`ColorBar.cpp`, which are a different generic picker used by
  the TX title bar/overlay tool/CItems plugins, not the waterfall). Low.
- No separate FFT/scope trace view (legacy has one distinct from the waterfall image). Medium.
- No peak-hold/persistence overlay, no sync/frequency marker lines. Low-medium each.
- No zoom/bandwidth-range control (legacy: 3 discrete presets, no continuous zoom). Low-medium.
- No interactive notch-filter marker (click/drag to set, right-click to toggle) — legacy's
  version configures an audio notch filter, not RX retuning. Medium-high; depends on a notch
  filter DSP block that doesn't exist yet in `ScanlineStudio.Core.Sstv` (unverified, flagged only).
- No dedicated signal-strength meter, no legacy-style debug "digital scope" tool. Low-medium /
  medium-high respectively — the debug scope is probably the lowest-value item in this list.
- QSSTV does a nicer multi-hue heatmap gradient (vs. legacy's flat 2-color linear interpolation)
  and an adjustable dB range — worth a look if/when color rendering is built, not a legacy port
  requirement.

**RX/TX quality-of-life** (excludes the already-deliberately-dropped DSP tunables — PLL VCO
gain, zero-crossing params, RxBPF width, squelch level, calibration wizard, differentiator, LMS
filter — those are a known exclusion, not rediscovered here). The LMS filter itself was measured
across all 43 modes in 2026-09 and dropped on the numbers, not just its tunable — see
[[docs/removed-features]] for `CLMS::Do` and `CLMS::DoN`:
- ~~Manual "ReSync" button~~ — **done** (2026-08-07): the roadmap's own original framing ("applies
  an already-computed sync-skip correction") pointed at the wrong legacy feature — traced the real
  click handler (`TMmsstv::KRFSClick`, `Main.cpp:14004-14020`) and found `ReSyncSSTV` (the 32-line
  envelope fold this entry originally meant) is only ever called by two "high-precision sync" MENU
  items, never the button. The real button is much simpler: live per-line sync-peak tracking
  (`m_SyncPos`/`m_SyncRPos`) plus a forward-only sample skip (`m_Skip`), no fold/cache. New
  `AnalogFmSstvDecoder.RequestReSync()` → `PerformReSync()`/`DrainPendingSkip()` (the skip is applied
  incrementally, spanning `PushSamples` calls, since an atomic apply can read past the end of the
  received stream on the common path), plus a two-scope suppression in `ApplySlantTracking` matching
  legacy's own `m_SyncPos != -1` (one line, no history push) vs `m_AutoSyncCount` (rest of the image,
  history keeps flowing via new `SlantTracker.ProcessLineHistoryOnly`, only the correction is
  skipped) gates. Full `ISstvDecoder`/`ISstvSessionService` plumbing, backend-only — no UI button
  wired yet. 3 design-review rounds + a final code-level auditor review (EQUIVALENT-WITH-RISKS, no
  blockers) before/after implementation. See `PROJECT_BRIEF.md` for the full account.
- ~~AFC on/off toggle~~ — **done** (2026-08-07): new `SstvDecoderSettings.AfcEnabled` (nullable,
  STJ-default-loss-safe, same pattern as `ReceiveHistorySettings.MaxEntries`), threaded into
  `AnalogFmSstvDecoder`'s ctor and `InitializeAfc`'s existing AVT-exclusion guard (AFC-off now
  lands on the identical "`_afcTracker` stays null" path every consumer already null-checks, no
  other code changed). `Program.cs`'s `ISstvDecoder` registration switched from an eager instance
  to a settings-reading factory. Real, not a legacy port (legacy's own AFC, `sstv.cpp:1471`, is
  unconditionally always-on with no user-facing switch) — documented as such. Restart-only
  (singleton, `readonly` field). Auditor-reviewed (`Core.Sstv` touch, CLAUDE.md §7) — verdict
  EQUIVALENT-WITH-RISKS, no blockers; the two cheap risk fixes (settings-default test coverage,
  restart-only doc note) applied.
- ~~RX history retention limit (legacy default 32)~~ — **done** (2026-08-06), **then reversed by
  user decision (2026-08-26)**: see `docs/removed-features.md`'s own entry for the full account.
  Originally verified against actual legacy source (`Main.cpp:898`'s `sys.m_HistMax = 32`) and
  ported as an automatic trim-to-newest-N after every insert; removed outright once the Gallery
  tab's "All" filter shipped and the user found it didn't actually show every entry — legacy's own
  ring buffer has no "show everything" concept to preserve, so this wasn't a legacy-fidelity
  question. `history.db` now grows unbounded; no pruning mechanism exists. Window position/size
  memory across restarts and the "jump to latest" history-browser nav button remain open, trivial.
- RX buffer mode + "high-precision" slant/sync replay actions — medium, DSP-adjacent (a rolling
  raw-audio buffer replayed through sync/slant correction); flag carefully, don't treat as a
  plain UI toggle.
- ~~Auto-stop-at-end-of-signal / auto-resync toggles~~ — **done** (2026-08-08): both are the other
  two sub-features of the same legacy "Lock" toolbar button as `m_SyncRestart`
  (`TMmsstv::AutoStopJob`, `Main.cpp:3884-4035`). Auto Sync (automatic drift-triggered ReSync,
  commit `4ecfea6`) and Auto Stop (erratic/weak-signal detector that stops reception, commit
  `cda094d`) both shipped backend-only, following the exact same design as `RequestReSync`/
  `AfcEnabled` above (new `SstvDecoderSettings.AutoSyncEnabled`/`AutoStopEnabled`, nullable,
  STJ-safe). `AutoStopEnabled` defaults OFF, matching legacy's own fresh default
  (`sys.m_AutoStop = 0`, `Main.cpp:900`) — the other three toggles in this family default ON. KRSA
  (sample-rate auto-calibration, the third sub-feature `AutoStopJob` bundles) stays permanently out
  of scope — no toggle/UI exists for it and no plan to add one. See `PROJECT_BRIEF.md` for the full
  account (2 plan-review rounds + a code-level review each, several real bugs caught, including a
  guaranteed NRE and a wrong dead-time-skip assumption in Auto Stop's own design).
- **Confirmed NOT a real feature — don't port**: "always on top" (`m_StayOnTop` is write-only in
  legacy's own ini handling, never read back or wired to anything; vestigial/dead code there too).
- **Confirmed NOT a gap**: auto-save-on-receive — legacy always auto-saves unconditionally too,
  same as this port's current `ReceiveHistoryRecorder`.
- VOX (a TX tone-burst preamble to trigger a rig's own VOX circuit, not audio-input-detected PTT,
  and doesn't touch the CAT/PTT layer at all) — real but niche given this port already has real
  CAT PTT; low priority.

**Root-cause map (2026-08-08)** — the itemized mock2 list just below is real and current, but reading
it item-by-item hides that most entries trace back to a small number of shared missing backend
primitives. Verified directly against the current interfaces (`ISstvDecoder`, `ISstvSessionService`,
`IReceivedImageBuffer`, `ReceiveHistoryEntry` — not inferred from the mock or from memory), so this
table is a grouping/leverage view of the same gaps below, not a new inventory:

| Missing backend primitive | What exists today | mock2 elements it blocks |
|---|---|---|
| ~~Operator-callsign/profile setting~~ — **corrected and done** (2026-08-08): the "zero grep hits" claim below was wrong (`OperatorSettings.Callsign` already existed, `247fde7`); the "unblocks 4 mock2 items" claim was also wrong — Identification (FSK/CW/Tail ID) was blocked on the whole FSK/CW-ID subsystem this project had separately deferred ([[06-sstv-dsp]]'s Station ID section), not on a setting. **That subsystem shipped 2026-08-12** (corrected 2026-08-22 — this row used to say "stays blocked"). `OperatorSettings` gained `Name`/`Grid`; `IMacroTextResolver` (scoped to `%m`/`%D`/`%T`/`{name}`/`{grid}`/`{freq}`/`{mode}`/`{dist}`/`{bearing}` — legacy's own his-callsign/RST/greeting tokens still need a "current QSO" form this port doesn't have) now backs the Outgoing-metadata card's callsign/name/grid fields and the overlay editor's real insert-field picker/TX-macro substitution. See `PROJECT_BRIEF.md` for the full account. | `OperatorSettings.Callsign`/`Name`/`Grid`, `IMacroTextResolver` | Outgoing-metadata card (partial: callsign/name/grid only, not RST/to-station/report), overlay editor's insert-field picker + TX-macro substitution — 2 of the originally-claimed 4 mock2 items, not 4; the Identification card and CW-ID text generation itself are no longer blocked (FSK/CW-ID subsystem shipped) |
| ~~**Decode-time signal telemetry**~~ (SNR, squelch, BPF/AGC/notch state, buffer/clipping %, noise floor, L/R levels) — **Tier A done** (2026-08-08, `cf76a08`), Tier B/C explicitly deferred, not silently dropped: `ISstvDecoder` gained `SignalPeakLevel`/`IsLevelOverdriven` (peak amplitude + legacy's own `DrawLvl` red-meter-bar threshold — NOT legacy's separate `m_OverFlow` raw-sample flag, a genuinely different legacy quantity this port's post-BPF AGC input can't reproduce; NOT the mock2 "Clipping %" figure either, legacy never computed a percentage, only this threshold), `SyncFrequencyCorrectionHz` (AFC's own correction passthrough), `BufferedSampleCount` (promoted from `internal`). Still unbacked, each deliberately scoped out (see `~/.claude/plans/steady-humming-osprey.md` for the full research): true SNR/SNR-histogram/noise-floor (legacy has zero equivalent anywhere — `CNoise` is a noise *generator* for test/sim, not a measurement — needs a product decision on what "SNR" even means for this port, not a legacy-verification pass), notch-filter state (`CNotch`, `fir.h:123`, entirely unported — no filter exists yet to report the state of), true L/R stereo levels (legacy and this port are both mono-only in the demod path), squelch (zero legacy grounding at all, confirmed via `grep -a`). | `ISstvDecoder.SignalPeakLevel`/`IsLevelOverdriven`/`SyncFrequencyCorrectionHz`/`BufferedSampleCount` | RX input-chain telemetry card (partial: level + buffer only), per-line SNR/histogram ("Signal quality" card, still fully blocked) |
| ~~**Slant/sync correction readouts**~~ (ppm, offset px) — **done** (2026-08-08, `94831b6`): `ISstvDecoder` gained `SlantPpm` (legacy's own `DrawSlantInfo` ppm formula) and `SyncOffsetSamples` (legacy's `m_AutoStopPos`, a quantity legacy itself never displays). | `ISstvDecoder.SlantPpm`/`SyncOffsetSamples` | Sync/slant correction readouts card |
| ~~**`ReceiveHistoryEntry`'s field set**~~ — **partially done** (2026-08-08): gained `Note`
  (`string?`), `IsFlagged` (`bool`), and `DecodeState` (`ReceiveDecodeState` enum,
  `Completed`/`Abandoned` — auto-populated with zero new measurement at each of
  `ReceiveHistoryRecorder`'s two existing write call sites; formalizes the pre-existing `_partial_`
  filename convention into a real structured field). First schema migration this store has ever
  needed (`SqliteReceiveHistoryStore.EnsureSchema`, `PRAGMA table_info` probe + `ALTER TABLE ADD
  COLUMN`, transactional, backfills pre-existing rows' `DecodeState` from the filename shape via
  `GLOB`, not a directory-path-vulnerable substring match). Callsign/grid/SNR still open — no
  upstream data exists yet anywhere in this port (OCR unbuilt; SNR is Tier B, see below). | Gallery
  search/sort/filter (still blocked, needs callsign/grid), frame metadata card's note field (now
  real), "decode rows colored by state" (now real, 2 of 3 intended states — "decoding" is live
  `IReceivedImageBuffer.Progress` state, a separate already-exposed source, not part of this field) |
| **Structured per-decode event log** | `ReceiveHistoryStore` only records the final saved image, no per-decode trace | "Decode activity" log card, decoder-trace pane |
| ~~**Frame-action primitives**~~ (abort-current-frame, re-decode, QSO-log-link) — **QSO-log-link
  piece done** (2026-08-08): `IReceiveHistoryStore` gained `SetLinkedQsoIdAsync` (plus
  `SetNoteAsync`/`SetFlaggedAsync` for the field-set item above) — the real gap was narrower than
  originally framed: `ILogbookRepository` already had a full write API and `QsoRecord.ReceivedImageId`
  was already the matching reverse FK (logbook backend shipped 2026-08-07), this was purely a
  missing update-after-the-fact method on the RX-history side. **Correction**: the "blocked on
  [[08-logging]]" framing below (Gallery Unlogged/Flagged-filters bullet) was stale by the time this
  landed. Abort and Re-decode remain open — Abort is a decoder-state COMMAND (different risk class,
  `Core.Sstv`, needs `RequestReSync`/`ForceMode`-level concurrency care); Re-decode is effectively
  blocked, no raw audio is retained anywhere in this port. | `ISstvDecoder` has no abort; `LinkedQsoId`
  now settable post-save via `SetLinkedQsoIdAsync` | RX frame actions (Abort/Re-decode still
  blocked; Copy-to-TX untouched), Gallery's "Log entry"/"Open in log" (now real) |
| ~~**TX-side device/clock telemetry**~~ (output device name, sample-clock offset, occupied
  bandwidth, monitor-while-TX) — **device-name piece done** (2026-08-08), the other 3 confirmed
  bigger than this row's original framing, not just unexposed: sample-clock offset needs genuinely
  new instrumentation with no reusable RX analog (`SlantPpm`'s drift-detection mechanism depends on
  a reference signal TX doesn't have); occupied bandwidth has real FFT machinery already built
  (`RadixTwoFft`/`WaterfallSource`) but wired to RX capture only — pointing it at TX audio is real
  new wiring, and reducing a spectrum to a single "occupied bandwidth" number needs an actual
  measurement-definition decision, not a lookup; monitor-while-TX is a genuinely new audio-routing
  feature (a second concurrent local-playback stream), not an exposure. Device name turned out to
  live entirely in the UI/Application layer, not `Core.Audio` at all: `ISstvSessionService` gained
  `GetConfiguredPlaybackDeviceNameAsync` (non-throwing, reuses `TransmitAsync`'s own device-
  resolution lookup so it can never disagree with what a real TX would use), wired into
  `TxControlsPaneViewModel.OutputDeviceName`. Deliberately skipped the plan+auditor-review cycle for
  this one piece specifically (user's own call) — pure settings-plumbing, no DSP fidelity or
  decoder-concurrency risk the way the last two batches had. **Occupied bandwidth investigated and
  explicitly abandoned** (2026-08-08), not just deferred: two rounds of plan-review found the whole
  approach was likely fundamentally wrong, not just risky to implement — `AnalogFmSstvEncoder`
  already bandpass-filters every TX sample (`TxOutputBandpassFilter`, 700-2800Hz), so a per-
  transmission min/max spectral-occupancy union (needed to avoid a flickering per-frame instant
  reading, itself also a real problem the review found) is a mathematical extreme-value statistic
  that would very likely just converge to that FIXED FILTER'S OWN skirts on every transmission of
  every mode, regardless of image content — reporting the encoder's own known characteristic back
  as if it were a live measurement, not a real diagnostic. User's own call after seeing this: skip
  it, ship the free "Tone map" static per-mode lookup instead (below) rather than build a
  measurement likely to be non-functional-but-plausible-looking. Full reasoning preserved at
  `~/.claude/plans/wandering-glowing-otter.md` in case a future pass wants to revisit with a
  different measurement approach (e.g. reporting min/max EDGES rather than width, or measuring
  pre-bandpass-filter samples instead of the already-filtered TX output this pass measured).
  **"Tone map" freebie shipped instead** (2026-08-08): `SstvModeDefinition.LuminanceMinHz`/`MaxHz`
  (already real, mode-dependent data — e.g. narrow-family modes override the 1500/2300 default to
  2044/2300) exposed as `TxControlsPaneViewModel.ToneMapText`, zero new DSP, zero new wiring beyond
  a `[NotifyPropertyChangedFor]` computed property. Also skipped the plan+auditor cycle (same
  low-risk-UI-plumbing reasoning as device name). | `ISstvSessionService.GetConfiguredPlaybackDeviceNameAsync`, `TxControlsPaneViewModel.OutputDeviceName`/`ToneMapText` | TX telemetry readouts row (device name + tone map done; sample-clock/occupied-bandwidth/monitor remain blocked — occupied bandwidth now abandoned, not just unbuilt) |
| **QRZ lookup** | **Shipped 2026-08-11/12** (`QrzCallsignLookup`, `ScanlineStudio.Core.Logbook`), corrected 2026-08-22 — this row used to lump QRZ in with OCR as fully unbuilt; OCR itself moved to "Explicitly deferred beyond v1" (Tier 3), not tracked as an active gap here | Frame metadata card's grid field, gallery search on it |

Already covered, not gaps: TX power/ALC/SWR history (`TxControlsPaneViewModel.TelemetryHistory`, real
data via `RadioState` polling), decode progress (`IReceivedImageBuffer.Progress`), PTT lock,
tune-and-hold, stereo capture, buffer/thread-priority settings.

**New UI shell mock2 elements omitted for lack of real backing data** (found while building the
fixed Menu/header/tab shell — 3 tabs at the time, a 4th (Logbook) landed 2026-08-08, see
`/home/artien/.claude/plans/wondrous-crafting-ladybug.md` — wired everything real, these had no real
data behind them today):
- ~~Manual "lock to a specific mode" RX decode override + the mock2 Mode card's quick-mode-button
  grid~~ — **backend done** (2026-08-08): the roadmap's own framing was deliberately vague pending
  research. Traced the real legacy click handler (`TMmsstv::SBMClick`, `Main.cpp:6096-6122`, calling
  `CSSTVDEM::Start(mode, TRUE)`, `sstv.cpp:1749-1767`) and found (via `UpdateModeBtn`,
  `Main.cpp:5988`) it's a ONE-SHOT "start decoding as mode X right now" kick, not a persistent lock
  — once the forced image ends, ordinary VIS auto-detect resumes automatically for the next
  transmission. New `ISstvDecoder.ForceMode(SstvModeDefinition)`, fire-and-forget like
  `RequestReSync`, reusing the exact same `Commit()`/anchor-correction pipeline VIS auto-detect
  itself uses. Went through 2 rounds of auditor plan-readiness review (round 1 caught a real
  audio-thread-crash blocker in the anchor choice; round 2 caught a stale-pending-anchor edge case
  forcing AVT mid-another-mode's-resolution). **UI wired since** (corrected 2026-08-22) — the Mode
  card's Auto/Locked segment and the RX/TX quick-mode-button grids both call `ForceMode`/
  `QuickSelectModeCommand` today (Tier-0 sweep, 2026-08-14; see [[18-path-to-1.0]] High item 7). See
  `PROJECT_BRIEF.md` for the full account.
- ~~Live decode "Remaining time" / per-line progress readout~~ — **backing data done** (2026-08-07):
  new `IReceivedImageBuffer.Progress` (`double?`, `null` when idle, `[0,1]` fraction while decoding,
  snapped to exactly `1.0` on the completing scanline group), computed in `ReceivedImageBuffer.cs`
  by reusing `ReceiveHistoryRecorder`'s own step-learning technique for the same
  paired-line/RowsPerTransmissionLine problem. Backend-only — no ETA/remaining-time computation or
  UI binding yet; a future ViewModel can derive "remaining time" from `Progress` + its own elapsed-
  time tracking with no further backend change needed.
- RX frame actions (Abort/Re-decode/Copy-to-TX) and a completion progress bar — no
  abandon-current-frame or re-decode primitive exists yet. Medium-large, several independent
  features. (Log QSO **DONE 2026-08-15** — see `spec/16-gui-wiring-survey.md`'s Incoming-frame-card
  row.)
- ~~Sync/slant correction readouts and controls (ppm, offset px, ReSync/Reset, advanced timing)~~
  — **readouts' backend done** (2026-08-08, see the root-cause map above's `SlantPpm`/
  `SyncOffsetSamples` entry); the ReSync/Reset control's own backend (`RequestReSync`) was already
  done in an earlier pass — cross-reference the "Manual ReSync button"/"AFC toggle" items above.
  Nothing left backend-side for this card; only UI binding remains.
- RX input-chain telemetry — **partially done (2026-08-08)**: level and buffer now have real
  backing data (`SignalPeakLevel`/`IsLevelOverdriven`/`BufferedSampleCount`, see the root-cause
  map's Decode-time-signal-telemetry entry above). Squelch, BPF (as reportable/adjustable state
  beyond "which fixed filter is active" — itself already derivable client-side from `ModeDetected`,
  not a gap), notch, noise floor, and L/R level meters remain unbacked — squelch and notch need
  real feature builds (no legacy squelch concept exists at all; the notch filter, `CNotch`, is
  entirely unported), noise floor needs an invented measurement (no legacy equivalent), true L/R
  needs real stereo capture (legacy and this port are both mono-only in the demod path). Medium
  remaining, down from medium-high.
- Per-line SNR / luminance histogram / calibration-tone-offset readouts ("Signal quality" card)
  — **partially done (2026-08-08)**: the sync-tone calibration-offset readout now has real backing
  data (`SyncFrequencyCorrectionHz`, see the root-cause map above) — legacy's own AFC only ever
  tracks the sync tone, so this card's Black(1500Hz)/White(2300Hz) tone-offset fields have no
  legacy equivalent and must stay unbacked, not invented. Per-line SNR and the luminance histogram
  remain fully unbacked — no such computation exists in the decode pipeline, and SNR specifically
  needs a product decision (see the root-cause map's Tier B note above), not a lookup. Medium
  remaining.
- Structured "Decode activity" log (freq/mode/callsign/grid/SNR/slant/lines/state per decode)
  and its decoder-trace pane — no such structured event log exists; would need a new decode-
  history recorder distinct from `ReceiveHistoryStore`. Medium-large.
- Frame metadata card (grid/QRZ, VIS/frequency stamp, dropped lines, file size, note, flag) —
  **partially done, corrected 2026-08-22**: this line used to say none of these fields exist on
  `ReceiveHistoryEntry` — `Note`/`IsFlagged`/`DecodeState`/`LinkedQsoId` all shipped since
  (2026-08-08 and later, see [[07-image-pipeline]]'s "RX history" section), and file size shipped
  via `IReceivedImageBuffer.Saved` ([[18-path-to-1.0]] High item 5's packaging cluster). Callsign
  itself is real (FSK-decoded, not OCR — see below). Grid/QRZ auto-fill, VIS/frequency stamp, and
  dropped-line count remain wholly unbuilt. Medium remaining, down from Large.
- "Decode rows colored by state" (yellow=decoding/green=saved+logged/red=partial) — the flat
  WSJT-X-style chrome pass added the color classes and LED-indicator convention this would use,
  but there's no decode-state field on any list row to drive it (same gap as the Decode-activity
  log above). Flagged directly to the user mid-session; not yet resolved either way.
- VOX tone-burst preamble row in the Transmit tab's TX-mode card — cross-reference the VOX
  bullet above; no new note.
- Whole Identification card (FSK ID/CW ID/Tail) — **correction (2026-08-08)**: not actually
  blocked on the operator-profile setting (that's now done, see the root-cause map above); was
  blocked on the separately-deferred FSK/CW-ID audio subsystem itself (cross-reference "TX macros /
  CW-ID" above and [[06-sstv-dsp]]'s Station ID section). **That subsystem shipped 2026-08-12**
  (corrected 2026-08-22) — the Identification card is wired to the Options window's Identification
  tab (`FskIdTxEnabled`), no longer blocked.
- TX output device name / TX sample-clock / occupied-bandwidth / monitor-audio-while-
  transmitting readouts — none of these are exposed anywhere in `ScanlineStudio.Core.Audio`
  today. Small-medium each.
- ~~Historical power/ALC-over-time TX meter plot~~ — **backing data done** (2026-08-07):
  `TxControlsPaneViewModel.TelemetryHistory` (`ObservableCollection<TxTelemetrySample>`, 120-sample
  cap, oldest-evicted-first), appended in `OnRadioStateChanged` under the identical
  "actually transmitting" gate as the existing `LiveSwrRatio`/etc. readouts. Backend-only —
  nothing renders it yet; a future chart binds directly, no translation step needed.
- ~~Whole Outgoing-metadata card (VIS code/FSK ID/CW ID/callsign/to-station/grid-beam/report/
  freq-mode/date burned into the picture)~~ — **removed entirely (2026-08-23)**, not built. This
  entry's prior "partially done" claim (callsign/name/grid fields real via
  `OperatorSettings`/`IMacroTextResolver`) was stale: every row in this card
  (`TxControlsPaneView.axaml`'s "OUTGOING METADATA" `HeaderedContentControl`) was still a static
  `"—"` locale-string placeholder with no view-model binding at all, confirmed directly against the
  running app (user-reported: "does 'burned into picture' even do anything?" — no). Deleted rather
  than wired up, since the real, working way a user burns metadata into a transmitted image is
  already the TX Image Editor's overlay tool + token picker (see the Insert-field token picker item
  below) — this card was a redundant, never-finished read-only preview of the same data, not a
  distinct feature. "To station"/report/QSO-context fields still need a "current QSO" form concept
  that doesn't exist yet, same gap as the token picker's own remaining chips below.
- TX image editor: Move/Scale/Rotate/Box/Line/Mask/Pick tools, Undo/Redo, zoom/snap-grid,
  brightness/contrast/saturation/gamma/sharpen/denoise adjustments — **largely done, corrected
  2026-08-22**: this line used to say `ITransmitImagePreparer` only implements Crop/Resize/
  ApplyOverlay with none of the rest existing. Since shipped: `Rotate` ([[18-path-to-1.0]] High item
  3), the 6 adjustment sliders via `ApplyAdjustments` ([[18-path-to-1.0]] Medium tier), and Undo/Redo
  (snapshot-based, [[18-path-to-1.0]] Medium tier's last sub-piece). Box/text/image compositing
  shipped too, via [[15-template-designer]]'s `ApplyTemplate`. Still absent: dedicated
  Move/Scale-as-separate-tools, Line/Mask/Pick tools, and zoom/snap-grid. Small-medium remaining,
  down from Large.
- Insert-field token picker in the overlay editor — **done (2026-08-08)**: 5 of 12 mock2 chips
  (MY CALL/MY GRID/MY NAME/DATE/UTC) are real via `IMacroTextResolver`; the other 5 (HIS
  CALL/HIS GRID/FREQ/MODE/HIS RSV) need the same "current QSO" form / FSK-ID dependency
  as the Outgoing-metadata card above. **DIST/BEAM shipped separately (2026-08-18)**, an
  auditor usability review follow-up: `{dist}`/`{bearing}` are a computed macro over MY grid
  (`OperatorSettings.Grid`) and a `{his_grid}` FILL-BAR variable the operator types per-QSO, not
  blocked on a "current QSO" form — see `MaidenheadLocator`/`MacroTextResolver`'s `{dist}`/
  `{bearing}` cases. Saved templates remain a separately-deferred template designer, untouched by
  this pass.
- TX queue (batch multiple images), persisted TX log, and "recently sent" reuse strip — no
  queueing, no TX-history store distinct from RxHistory exists. Medium-large, three separate
  features.
- Gallery free-text search across callsign/grid/note and band filter — `ReceiveHistoryEntry`
  has no callsign/grid/frequency fields at all; would need the RX logbook fields already
  tracked under "Logbook/QSO tracking" above. Medium, blocked on that work.
- ~~Gallery Unlogged/Flagged filters and "Log entry"/"Open in log" actions~~ — **backend done
  (2026-08-08), correction to this entry's own "blocked on [[08-logging]]" framing**: that was stale
  even before this landed — `ILogbookRepository` already had a full write API and
  `QsoRecord.ReceivedImageId` was already the matching reverse FK (logbook backend shipped
  2026-08-07); the real, narrower gap was `IReceiveHistoryStore` having no update-after-the-fact
  method. `SetLinkedQsoIdAsync`/`SetFlaggedAsync` now exist — see the root-cause map's Frame-action-
  primitives/field-set entries above. Flagged filter ran client-side at the time this was written,
  when the store was still capped at ≤32 rows (see the "RX history retention limit" entry above —
  that cap is gone as of 2026-08-26, worth revisiting if the row count grows large enough to
  matter). Backend-only, no UI binding yet.
- Gallery sort by callsign/SNR, and per-frame Export/Re-decode actions — no callsign/SNR data
  exists on entries, so that sort remains blocked. **Export shipped 2026-08-15** (corrected
  2026-08-22, commit `90e2881`) — only the per-frame Re-decode action still has no operation to call.
  Small remaining, down from small-medium.
- Gallery Storage card's Sidecar-format and disk-free-space readouts — no JSON/EXIF sidecar is
  written today, and no free-space query exists anywhere. Small each.

**Options window: legacy YONIQ Option-dialog items added as disabled+tooltip placeholders
(2026-08-06)** — direct user request: port every option legacy's own Options dialog has, skip
what's already covered elsewhere, grey out + explain whatever has no real backing feature yet.
Most of the ~50 items added map onto gaps
**already tracked above** (PLL VCO gain/loop order/cutoff, zero-crossing type/order/cutoff/
smoothing, RxBPF width, sense/squelch level, differentiator, calibration wizard, TX BPF/LPF, TX
sample-clock offset, loopback test mode — all under "already-deliberately-dropped DSP tunables"
just above; demod-type selector and auto-start-on-sync-detect — cross-reference "Manual ReSync"/
"AFC toggle"/"Auto-stop-at-end-of-signal" above; window-position/size memory — cross-reference
"RX history retention limit" above; 7 waterfall/spectrum colors — cross-reference "Waterfall/
color" above; CW ID text/frequency/speed + FSK encode/decode — cross-reference "TX macros /
CW-ID" above, **shipped 2026-08-12** (corrected 2026-08-22 — this line used to say blocked on the
separately-deferred FSK/CW-ID subsystem; that subsystem is done); OmniRig 4th CAT backend — **shipped**
2026-08-29, see `spec/03-cat-layer.md`'s "Definition of done" (historical note: this line used to say
"speculative/undesigned")) — no new notes for any of those. Genuinely new
gaps found while doing this pass, not previously tracked anywhere:
- ~~Sound FIFO buffer size (RX/TX)~~, ~~sound-card thread (capture-drain) priority~~,
  ~~app process priority~~ — **done** (2026-08-07). Buffer size: new
  `scanline_audio_open_options.period_size_in_frames`/`periods` (native, `0` = miniaudio's own
  default, unchanged), threaded through `NativeAudio.OpenOptions` →
  `MiniAudioCaptureSession`/`PlaybackSession` → `MiniAudioEngine.Start*Async` →
  `AudioDeviceSettings.PeriodSizeInFrames`/`Periods` (plain non-nullable, CLR-default-safe).
  Verified via a real virtual-device round-trip test with non-default values. Capture-drain
  thread priority: `AudioDeviceSettings.CaptureThreadPriority` (nullable, STJ-safe) →
  `MiniAudioCaptureSession`'s drain `Thread.Priority`; the real-time native callback thread itself
  has no managed-settable priority, out of scope by construction. App process priority: new
  `ScanlineStudio.Application.AppPerformanceSettings.ProcessPriority` (nullable, STJ-safe), applied
  in `Program.cs` via `Process.PriorityClass`, guarded (a failure must never block startup).
- ~~Stereo capture source (Mono/Left/Right)~~ + ~~separate stereo-TX toggle~~ — **done**
  (2026-08-07), explicitly **not a confirmed legacy port** (documented as such at every layer, not
  traced against actual legacy source). New `AudioChannelSource` enum
  (`ScanlineStudio.Abstractions.Audio`) + `AudioDeviceSettings.CaptureChannelSource`/
  `StereoTxEnabled` (plain non-nullable, CLR-default-safe). Native: `capture_session_data_callback`
  extracts the selected channel from a real 2-channel device open into the shim's own
  ring (still always mono past that point — `IAudioEngine`'s "samples are always mono" contract is
  unchanged); `playback_session_data_callback` duplicates mono to interleaved L/R when stereo TX is
  on. Verified via real virtual-device tests: L/R content genuinely separates on capture, TX
  signal genuinely duplicates to both output channels. **Real underrun-padding bug found and fixed
  as part of this** (not pre-existing before this feature): a naive single-channel-width `memset`
  would have only ever zeroed the Left channel's bytes on underrun with stereo TX on, leaving Right
  with stale/garbage backend memory — fixed to zero both channels' worth, confirmed via a
  revert-fix-confirm-fail regression test (reintroducing the naive version made the new
  both-channels-silent test fail exactly as predicted).
- ~~PTT lock (hold PTT continuously, a manual-keying diagnostic aid)~~ — **done** (2026-08-07,
  safety-critical, auditor-reviewed). `ISstvSessionService.SetPttLockAsync`/`IsPttLocked`. An
  initial implementation was audited and found to have real defects before shipping — all fixed:
  an engaged lock could not be overridden by the SWR auto-cutoff/manual Stop TX (a lock must never
  defeat a safety cutoff — fixed so any abnormal termination, not just a normal completion, always
  force-unkeys and force-clears the lock); app shutdown while locked left the rig keyed
  (`DisposeAsync` now force-unkeys); unlock could silently no-op on a still-keyed rig in exactly
  the cases that mattered (`TuneAsync`'s `leaveKeyedAfterTune` leaves PTT keyed without setting the
  lock flag) — fixed by removing the short-circuit entirely (every call now always issues the
  command; confirmed idempotent-safe on every real protocol backend) and serializing via a
  `SemaphoreSlim` (closes a real TOCTOU race the short-circuit version had); RX paused by a
  lock-covered Transmit/Tune call now correctly resumes on unlock (`_rxPendingResumeAfterUnlock`
  handoff). One documented, accepted residual race (unlock racing a Transmit/Tune's own entry) —
  latent, no production caller wired yet, fixing it fully would make emergency-unlock less
  responsive, a worse trade. RTS-on-RX (the other half of this original bullet) intentionally
  dropped from this pass — no serial-control surface exists, may conflict with Hamlib's own
  RTS-PTT-type ownership, needs a legacy re-check before deciding if/how it fits this port's CAT
  architecture at all; still open.
- Sound-file ID (a recorded `.mmv`-style audio clip played instead of a CW-keyed tone) — distinct
  from CW-ID above (which is real and already tracked); this is a second, separate ID method with
  its own file-path field. **No longer blocked** (corrected 2026-08-22 — the FSK/CW-ID subsystem this
  line waited on shipped 2026-08-12); simply unbuilt now, small-medium, since it would likely share
  the same "ID method" selector CW-ID already has.
- ~~Tune-satellite-trigger toggle~~ — **done** (2026-08-07), explicitly **not a confirmed legacy
  port** (a citation attempt against `CtrBtn.cpp` only found a UI-enablement guard, not the actual
  post-tune state-transition logic — documented as an assumption, not verified). New
  `ISstvSessionService.TuneAsync(..., bool leaveKeyedAfterTune = false)`: when true, skips the
  normal un-key/resume-RX step for that one call, implemented as a separate, local flag inside
  `PlayWithPttAsync` — deliberately not reusing the PTT-lock's own field, so `IsPttLocked` never
  lies about what's actually holding PTT keyed.
- QRZ.com lookup enable — **shipped 2026-08-11/12** (corrected 2026-08-22), narrower than the
  formerly-combined "OCR/QRZ lookup" gap under Frame metadata above — OCR itself is now tracked
  only under "Explicitly deferred beyond v1" (Tier 3), not as an active gap. Legacy's own QRZ
  integration hardcoded a personal account
  password; this port did not resurrect that — `QrzLookupSettings` takes user-supplied credentials
  (currently stored in plain JSON, see [[12-settings]]'s "Secrets" section for that separate gap).
- ~~JPEG save quality (0-100)~~ — **DONE 2026-08-15**, see the Tier 2 entry above. Automatic
  RX-history save is still PNG-only by design (unchanged); the quality setting now applies to the
  Gallery pane's manual "Export frame" action, not automatic save.
- Legacy's `WinFont`/Japanese-English font-switch buttons and the Windows-only "always use DIB"
  rendering toggle are **not tracked here at all** — see `docs/removed-features.md`'s new
  "Legacy UI font switching" entry (superseded by the design system, not a gap) and the
  Win32/GDI-only DIB toggle isn't a real user-facing capability, no entry needed either.
- Legacy's YONIQ-fork-specific external "log connection" (IP:port socket to an unnamed companion
  app) is also **not tracked here** — see `docs/removed-features.md`'s new entry; too
  underspecified to even represent as a disabled placeholder.

## Must-implement backlog — legacy parity gaps, NOT deferred/removed-with-replacement (2026-08-08)

> **Superseded as a priority list by "Path to 0.9 beta / road to 1.0" above** — kept here for its
> research/citation detail. Several items below were stale as of 2026-08-13 (fixed inline: RX BPF
> and Demod-type shipped 2026-08-12, so their sub-paragraphs below no longer reflect reality; the
> H1-cutoff "logged not fixed" note was itself fixed; CW-ID/FSK's checkbox was unchecked despite
> shipping 2026-08-12).

User request: "what are we still missing from legacy that we MUST implement" — distinct from the
mock2 GUI-blocking backend-primitive work above (SlantPpm/telemetry/Gallery-metadata/device-name/
Tone-map, all shipped this session). This list is durable specifically so it survives a `/clear` —
check items off (`- [x]`) as they land, add a one-line note (commit hash, date) same as everywhere
else in this doc, don't let this list itself go stale the way a couple of entries in the section
above did (two items below were found ALREADY DONE by direct code verification when this list was
first written — see their notes).

**Working order** (this session's own proposal, not yet reprioritized by the user beyond "tackle
these first" as a whole) — biggest-leverage/lowest-risk first:

- [x] **Logbook UI pane** — **shipped 2026-08-08.** New 4th tab (`MainWindow.axaml`) +
  `LogbookPaneViewModel`: search/browse (callsign exact-match + UTC calendar-date From/To range,
  30-day default), add/edit QSO form (all `QsoRecord` fields except `Id`/`ReceivedImageId`), ADIF
  import/export via 2 new `IFilePickerService` methods. New facade method
  `ILogbookSessionService.UpdateQsoAsync` (deliberately no ADIF-UDP/QRZ re-push — QRZ's real
  upload API is INSERT-only; ADIF-UDP forwarding was GridTracker-only at the time this shipped,
  generalized to multi-destination 2026-08-15, see `spec/08-logging.md`'s own section). Fixed a pre-existing `ExportAdifFileAsync` gap (missing
  `STATION_CALLSIGN`, exported files didn't import cleanly into LoTW/eQSL). Scoped via 2 rounds of
  auditor plan-review, then 2 rounds of post-implementation auditor code-review — round 1 caught 3
  real blockers (status messages silently discarded after every Log/Update; Mode and SSTV-mode
  ComboBoxes bound via `SelectedItem` against a different element type than the bound property,
  silently nulling the field on every selection; `StringFormat` on a TwoWay Start/End `TextBox`
  binding that doesn't reverse on `ConvertBack`, corrupting stored UTC timestamps to the machine's
  local offset on edit) plus 5 risks, all fixed and re-verified ship-ready in round 2. Plan file:
  `~/.claude/plans/rustling-drifting-falcon.md`.
  QSL sent/received flags and duplicate-QSO detection (by callsign/band) remain real, separate,
  smaller deltas (facade/repository changes, not just UI) — explicitly NOT bundled into this pane.
  Delete-a-QSO and the Gallery "Log entry"/"Open in log" cross-pane wiring are also explicitly out of
  scope (see the plan file's own "Explicitly OUT of scope" section for the reasons).
- [x] **DSP decode-accuracy residuals at the declared 11025Hz rate** — **stale item, closed
  2026-08-08, no new DSP work needed.** This checklist entry was written from an old measurement,
  before the Robot-36-at-11025Hz investigation logged earlier in this same doc (the long
  "Pieces 9-15+"/Hilbert/pre-AGC-filter/per-line-cursor-rounding history above) actually landed its
  fix. Re-verified two independent ways before closing, not just re-trusting an old note: (1) the
  existing `GoldenVectorTests.cs` (real captured legacy audio, not synthetic self-consistency —
  this doc's own history explains why that distinction matters) already passes 43/43 today,
  including Robot36/Robot72 at 11025Hz with tolerances tightened to reflect real measured deltas
  around 5-17, down from the mid-investigation high of 68.06; (2) added a new permanent test,
  `SstvRoundTripTests.EncodeThenDecode_ViaWavFile_RoundTripsWithinTolerance_At11025Hz`, covering
  every mode this checklist item named (Robot36, Robot72, MR73, ML180/240/280/320, Martin M2,
  MR115) at the real 11025Hz rate against the standard 10.0 tolerance — all 9 pass comfortably
  (measured ~2.3-4.9 average-per-channel delta), locking in what was previously only a historical
  doc note from a throwaway experiment. No production DSP code changed; this was a
  verify-and-lock-in pass, not a port, so the full auditor DSP-audit process (CLAUDE.md §7) wasn't
  invoked — skipped per that section's own "skip for mechanical" guidance, since nothing was
  changed for it to review. Full `Core.Sstv.Tests` suite reconfirmed green after the new test
  (101/101 in `SstvRoundTripTests` alone, was 92).
- [ ] **Options dialogs that are still placeholders, not real functional windows** —
  `RadioSettingsDialog`/`MacroKeyEditor`/`ColorSettingsDialog`/`LanguageSettingsDialog` (the ~50
  legacy Options-dialog items already added as disabled+tooltip placeholders, Phase-4+ backlog
  section above, are UI *scaffolding/inventory*, not these dialogs actually existing and working).
  **Scope-checked 2026-08-08, BLOCKED on user input before building anything**: there is no
  separate dialog class per name above — all ~50 items are `IsEnabled="False"` +
  `Options.NotImplemented.Help`-tooltipped controls inside one big
  `src/ScanlineStudio.UI/Views/OptionsWindowView.axaml` (colors/FFT palette, VOX, CW-ID/
  identification, macros, etc.), and several of those sections genuinely overlap with the
  waterfall color/palette and CW-ID/FSK subsystem items listed separately below — building this as
  one lump risks duplicating or conflicting with that later work. Needs a real per-section split
  plus an auditor UI-design plan-review pass (this project's own established convention for UI/UX
  choices) before any code, not a straight port. User asked which pieces to prioritize; no answer
  yet as of this note.
  **Update 2026-08-12**: user authorized proceeding through this backlog "small pieces first,"
  no further per-item confirmation needed. Decode tab: Auto-Sync/Auto-Slant/Auto-stop/Auto-restart/
  Sense level all shipped (see `PROJECT_BRIEF.md` for detail).

  > **Correction, 2026-08-13, updated 2026-08-14, updated 2026-08-15**: the three paragraphs below
  > (RX BPF, Demod type, RX buffer) are STALE research, kept only for their scoping reasoning. **All
  > three shipped**: RX BPF (2026-08-12, Kaiser-window `MakeFilter` + preset-parameterized filter,
  > incl. the H1-cutoff fix the "Also found, logged not fixed" note below flags — that's fixed too,
  > not open), Demod type (2026-08-12, runtime dispatch across all 3 demodulators), RX buffer
  > (Phases 1-8 of 9, 2026-08-13/15 — the "buffered-line-replay mechanism this port doesn't have"
  > comment cited below no longer exists in `AnalogFmSstvDecoder.cs`; replay is real and wired, and
  > as of Phase 7 (2026-08-14) `RxBufferMode.Extended`'s disk-backed variant is real too, including
  > its own replay path, not just a stub; Phase 8 (2026-08-15) adds the manual "Correct Slant"
  > one-shot search, `RequestCorrectSlant()` on `ISstvDecoder`, wired through the decode loop with a
  > real fix to a cross-mechanism state-sync bug auditor code-review caught. Phase 9 (Options UI
  > wiring) remains, tracked in Tier 2 above). Auto-start below is still genuinely open, also tracked
  > in Tier 2.

  Remaining Decode controls scoped (historical, see correction above):
  **RX BPF** — real legacy control (`Option.dfm`'s `RGRxBPF`), but bigger than a wiring task:
  legacy's `m_bpf` gate (`sstv.cpp:1826-1833`) is the pre-AGC filter feeding EVERY downstream stage
  (sync detection, demod, AVT), not a peripheral one; `SearchBandpassFilter.cs` already documents
  this port only ever reaches the Wide preset. The "Sharp"/"Very sharp" presets need `MakeFilter`'s
  Kaiser/Bessel branch (`fir.cpp:361-384`, `att>=21`) — **already ported once**, for
  `TxOutputBandpassFilter.cs`'s TX filter (its own `MakeFilter`/`I0`), so the hard math is proven
  and reusable, not a new algorithm — but each preset also changes tap count (24/64/96), hence
  group delay, and `SearchBandpassFilter`'s own doc comment explicitly reasons sync-anchor
  correction needs no adjustment ONLY because today's single fixed preset never changes group delay
  mid-session; a user-selectable preset breaks that assumption and needs the sync-anchor correction
  re-derived per tap count, with golden-vector verification (CLAUDE.md's Behavioral-parity rule) —
  full DSP-parity work, not UI wiring. **Also found, logged not fixed (off-scope, one line)**:
  `SearchBandpassFilter`'s H1 low cutoff is hardcoded to 1100Hz "since m_SyncRestart is hardwired
  on" — stale as of this session's `SyncRestartEnabled` wiring (same legacy field, `m_SyncRestart`,
  now user-toggleable; legacy's real `lfq` is `1100` when on / `1200` when off, `sstv.cpp:1523`) —
  a latent fidelity gap in the already-shipped filter, not something to fix inside RX BPF's own
  scope. **Demod type** — real legacy control (`RGDemType`: PLL/Zero-crossing/Hilbert,
  `Option.cpp:63-65/242/542`), all 3 demodulator classes already exist in this port
  (`PllFmDemodulator`/`ZeroCrossingFrequencyCounter`/`HilbertFmDemodulator`) but the main picture
  path is hardwired to Hilbert only — needs real runtime dispatch plus per-type sync-anchor
  handling, the biggest/riskiest of the four. **RX buffer** — **correction, re-investigated more
  thoroughly same session**: DOES have a real legacy Options-dialog control after all
  (`sys.m_UseRxBuff`, `Option.dfm`'s `RGRBuf` radio group, `Option.cpp:375/575`, 0/1/2 = Off/On/
  Extended — matches this port's existing loc text exactly; the earlier "no precedent" note above
  came from grepping the wrong field names). But its real purpose is backing a
  sample-rate/slant-recalculation REPLAY mechanism (`UpdateSampFreq`/`RedrawSSTV`,
  `Main.cpp:5601-5864`, gated by `m_ASDis`) that re-decodes already-received lines from a staged
  raw-sample buffer (RAM for mode 1, disk via `CWaveStrage` for mode 2) after a mid-session
  recalibration — and this port's own `AnalogFmSstvDecoder.cs:1281-1284` already explicitly
  documents `m_ASDis` as "a buffered-line-replay mechanism this port doesn't have." Wiring
  "RX buffering" as a UI control today would be a fake no-op (nothing to bind it to) unless the
  entire buffered-line-replay subsystem is built first — its own separate, large architectural
  feature (disk I/O, retroactive re-decode), not smaller than RX BPF/Demod-type. **Auto-start** — real legacy behavior (`SBAuto` toolbar button/`RxAutoPush`, `Main.cpp:6042-6060`):
  arms/disarms the decoder's ability to auto-detect a NEW sync lock (`m_SyncMode=0` vs `-1`) without
  stopping the AGC/level-meter pipeline — a real "gate an existing trigger" shape matching how
  `AutoSyncEnabled`/`AutoStopEnabled` were wired this session (no new DSP math), which sounded
  tractable at first. But this port's `AnalogFmSstvDecoder` has no exposed "disarmed, still live"
  state on `ISstvDecoder` at all today (it's always continuously armed), and there's no single
  `TryStart`-equivalent choke point the way `AutoStopEnabled` had — legacy's case-0 trigger is
  inline across multiple sync-detection branches (the same ones `SLvl`/`_slvl` gate, per this
  session's Sense-level work), so gating it needs real design work to find every branch and confirm
  none of them have side effects on AGC/level-meter continuity if suppressed. Also toolbar-only in
  legacy, not an Options-dialog control — a placement question on top. Smallest of the four, but
  still needs its own scoping pass, not safe to bundle into an ordinary wiring session. All four
  Decode items now scoped; RX BPF, Demod type, and RX buffer need dedicated DSP/architecture
  sessions (golden-vector tests, 2-round auditor plan-review per CLAUDE.md §7); Auto-start needs a
  smaller but still real design pass to find the right internal gating point. None bundled into
  ordinary wiring passes going forward.
- [x] **RX history browser affordances** — **shipped 2026-08-09, commit `1df5eac`.** Stale checkbox
  fixed 2026-08-11 (was never checked off despite landing). New `IReceiveHistoryStore.Recorded`
  event so the Gallery list/Receive-tab Previous-frames strip refresh live as frames land, plus a
  `SelectLatestCommand`/"Latest" button (legacy's real `SBPrim`, not the confusingly-named
  `SBLatest` which actually jumps to the OLDEST frame — verified against `Main.cpp` directly).
  Step-through prev/next was deliberately NOT added (superseded by click-to-select-any-thumbnail,
  a strict superset) and clipboard image copy-out was deliberately DEFERRED (`Avalonia.IClipboard`
  has no first-class bitmap API) — both logged as considered decisions in `docs/removed-features.md`,
  not silent gaps.
- [x] **Waterfall color/palette rendering** — **DONE (2026-08-09), batches 8a+8b.** User explicitly
  confirmed "full item" (asked directly since this entry's own text flagged re-confirming the prior
  deprioritization first). Shipped: 6-stop SDR-style heatmap gradient replacing flat grayscale
  (`WaterfallControl`/`WaterfallPalette`, batch 8a, commit `1cc4568`) with `Gain`/`Zero` sliders
  wired to a real dB-normalization window (defaults measured against real captured `.mmv` audio, not
  guessed); a new `SpectrumTraceControl` FFT amplitude-vs-frequency trace with legacy-real SSTV
  control-tone markers (sync/black/leader/white, mode-derived), a peak-hold overlay (legacy
  `sys.m_FFTStg`, time-based decay), and continuous Start/Span zoom (generalizing legacy's 3 discrete
  presets) plus a working Both/Spec/WF view toggle (batch 8b, commit `71a7daf`). Both batches went
  through full plan + multi-round auditor review (this visualization is explicitly NOT a legacy port,
  spec/06-sstv-dsp.md, so review focused on internal-consistency/UI-design risk, not DSP equivalence)
  — see `PROJECT_BRIEF.md`'s batch 8a/8b entries for the real bugs caught (a `Dispose()` lifecycle
  bug, an unwritten-history-row color-flood bug, a `Render()`-time binding-graph mutation, a
  narrow-mode marker citation slip) and the empirical verifications performed (BGRA byte order and
  `ColumnDefinition.Width`-binding-resolves-in-Avalonia both confirmed via real non-headless renders,
  not assumed).
  - **Deliberately deferred, not silently dropped** (documented here per this doc's own convention,
    not `docs/removed-features.md` since nothing is being REMOVED — these never existed in this port):
    **interactive notch-filter marker** — no notch-filter DSP block exists anywhere in
    `ScanlineStudio.Core.Sstv` (confirmed by `grep`, not assumed); building the marker without a
    backing filter would be decoration with no function. This is really "add a new audio notch-filter
    DSP feature," out of this item's own scope — needs its own future backlog entry if wanted.
    **Dedicated signal-strength meter, legacy debug "digital scope" tool** — no mock2 slot exists for
    either, and this doc's own original text already judged the debug scope "probably the lowest-value
    item in this list."
- [x] **QRZ.com callsign lookup** — **shipped 2026-08-11/12** (corrected 2026-08-22; also removed
  from Tier 2's own list on 2026-08-14, see the "stale entry, removed" line near the top of this
  document — this checkbox was simply never updated to match). `QrzCallsignLookup`/
  `QrzLookupSettings`/`LookupQrzCommand` (`ScanlineStudio.Core.Logbook`) — user-supplied credentials,
  legacy's hardcoded ones (below) not resurrected. Research below kept for citation detail. Real
  legacy feature, verified directly against
  `yoniq-old/YONIQ-main` (2026-08-11), not assumed from the roadmap's own prior "genuinely new
  feature" framing (that was wrong — corrected here). Legacy's `qrzcom.cpp`/`qrzcom.h`: a
  `TThread` that GETs `http://xmldata.qrz.com/xml/current/?s=<sessionkey>;callsign=<callsign>` and
  naive-substring-parses `<fname>`/`<name>`/`<addr2>`/`<country>` into `HisName`/`HisQTH` (does
  **not** parse `<grid>` despite the XML providing it — a real legacy gap, not a missed field in
  this doc). The session key comes from a separate login GET
  (`http://xmldata.qrz.com/xml/current/?username=...;password=...`, `Main.cpp:15263-15265`/
  `Option.cpp:1361-1363`) using **hardcoded personal credentials baked into the legacy source** —
  confirmed present, not being resurrected in any form; a real implementation needs its own
  user-supplied API-key/credential setting. Trigger points: `HisCallExit` (manual callsign-field
  edit, `Main.cpp:15874-15879`) and an FSK-decoded-callsign change (`Main.cpp:3633`, ties into the
  still-deferred CW-ID/FSK item below). Gated by a real `qrz` on/off setting (legacy `.ini` key
  `"qrz"`). UI already has a slot for this: Gallery tab's Frame-metadata card has a real "Lookup
  QRZ" button and "Grid / dist · QRZ" readout row (`MainWindow.axaml`, both currently STUB/FAKE-LIVE
  per `spec/16-gui-wiring-survey.md`) — no new UI surface needed, just real wiring. Design decision
  still open: where do Name/QTH land in this port's architecture (no "current session His Call"
  concept exists yet the way legacy's `HisName`/`HisQTH` `TEdit`s do) — needs a plan pass, not a
  straight port of the naive parsing.
- [x] **CW-ID / FSK station-ID subsystem** — **shipped 2026-08-12** (6 phases, `PROJECT_BRIEF.md`).
  Real residuals (NR/RST Options UI, `MacroTextResolver` token gaps, half-built callsign auto-fill,
  VOX/Sound-file ID not built) tracked in Tier 2 above, corrected 2026-08-13 — an earlier
  `PROJECT_BRIEF.md` note wrongly claimed VOX/Sound-file ID were "bundled in, done." Original
  research kept below for citation detail — at the time it was written, this was a real, working
  legacy feature (TX CW-ID tone + RX FSK-callsign-ID packet decode, `sstv.cpp:2465-2551`'s STX
  `0x2a`, distinct from the already-ported mode-announce STX `0x2d` packets) with zero replacement
  built. Already user-deferred once this session (Identification card work, 2026-08-08).
  **Full TX+RX research pass done 2026-08-12** (user asked
  for legacy behavior on both sides before any implementation) — smaller than the 2026-08-12
  re-scope note below estimated, because a surprising amount of supporting infrastructure already
  exists in this port. Full findings:

  **TX side** — three independent, combinable mechanisms, all triggered right after the image's
  last line (`Main.cpp:7014-7026`, `mp->m_wLine == SSTVSET.m_TL + 1`; the footer tone sent on the
  line *before* that also changes shape depending on whether FSK-ID is enabled, `Main.cpp:6995-7011`
  — a real fidelity detail, not guessable from the ID functions alone):
  - `OutputFSKID` (`Main.cpp:6903-6963`): sends `sys.m_Call` as FSK — guard tone, `STX 0x2a`, each
    char (offset `-0x20`) XORed into a running checksum, `EOT 0x01`, checksum byte. **Chains a
    second sub-packet** for a contest-style serial-number/RST report (`Log.m_LogSet.m_FSKNR`-gated),
    packed as either 2 compact 6-bit bytes (numbers &lt;4096) or a second alphanumeric FSK string —
    not mentioned in the original backlog line, a real sub-feature.
  - `OutputCWID` (`Main.cpp:6967-6980`): sends `sys.m_CWIDText` (a separate, user-configured field,
    not necessarily the callsign) as real Morse via `WriteCWID` (`sstv.cpp:2951-2999`, a compact
    bit-packed dot/dash lookup table, WPM-derived timing off `sys.m_CWIDSpeed`, tone at
    `sys.m_CWIDFreq`). Text is run through `MacroText` first (legacy's TX-text macro expander).
  - `OutputMMV` (`Main.cpp:6847-6899`): plays a recorded `.mmv` file instead of CW — a small
    semi-headered raw-PCM format (`0x55 0xAA <sampleRateIndex>` header or a bare legacy `SampType`
    byte) with its own IIR-filtered resampler if the file's rate doesn't match the live rate.
  - `sys.m_CWID` (0/1/2 = Off/CW/Sound-file) and `sys.m_TXFSKID` (independent checkbox) are **not
    mutually exclusive** — FSK-ID and CW-or-sound-file-ID can fire on the same transmission.

  **RX side** — the hard part is already ~60% built. `sstv.cpp:2378-2606`'s `DecodeFSK` is ONE
  shared state machine for both mode-announce (`STX 0x2d`) and station-ID (`STX 0x2a`) packets,
  diverging only after the sync byte. This port's `NarrowFskHeaderDecoder.cs` already implements
  the ENTIRE shared front half (guard-tone trigger/debounce/start-bit search/bit-clock sampling/byte
  assembly — legacy cases 0-4) as a two-round-audited literal port, and its own doc comment already
  points at the exact gap: case 4 treats `0x2a` as "unimplemented, resets like anything
  unrecognized" (see `docs/removed-features.md`). Missing: only the **continuation** state machine
  for `0x2a`'s payload (legacy cases 5-10: variable-length callsign string + checksum, then a
  chained NR/RST sub-packet mirroring the TX side above) — grafted onto working infrastructure, not
  built from scratch. The decoded callsign isn't decorative in legacy: it self-filters against your
  own callsign (ignore your own loopback), then **auto-fills the "his callsign" QSO-log field**
  (`Main.cpp:3618-3645`) if it differs from what's currently there — a real operator-workflow
  feature this port's existing Logbook/`QsoLinkWindowViewModel` infrastructure could hook into
  directly. Confirms the Receive tab's Callsign/OCR row's long-standing "no FSK-decoded-callsign
  source" gap (`spec/16-gui-wiring-survey.md`) has this as its real fix.

  **Already-reusable infrastructure in this port** (checked directly, not assumed): `VisHeader.cs`
  already has the exact physical-layer constants station-ID FSK needs
  (`NarrowGuardDurationMs`/`NarrowBitDurationMs`/`NarrowSpaceFrequencyHz` — confirmed identical to
  legacy's `FSKGARD`/`FSKINTVAL`/`FSKSPACE` `#define`s, `sstv.h:705-707`, both packet types share the
  same physical tones), plus a TX-side segment generator for the mode-announce packet that's a solid
  template for a station-ID TX generator. `MacroTextResolver.cs` is already a real, cited port of
  `MacroText` — but its own doc comment already flags it only covers `%m`/`%D`/`%T`
  (my-callsign/date/time), not the his-callsign/name/QTH/RST-exchange tokens (`%c`/`%n`/`%q`/`%r`/
  `%s`/`%R`/`%N`), since those need a "current QSO" context concept it doesn't have yet — fine for a
  basic CW-ID (usually just your own call), a real gap for full macro fidelity.
  `OperatorSettings.Callsign` already covers "my callsign," no new settings plumbing needed there.

  **Interop-safety guardrail, discussed with user 2026-08-12**: many real users run legacy
  YONIQ/MMSSTV, so the WIRE PROTOCOL (STX/EOT byte values, checksum algorithm, bit timing, the 6-bit
  character encoding/ASCII-offset scheme) must stay byte-for-byte identical to legacy — that's what
  lets a legacy station decode our FSK-ID and vice versa. Everything else is safe to extend freely
  without any legacy-compatibility risk: richer macro tokens for CW-ID text (`MacroTextResolver` just
  feeds characters into `WriteCWID`, which doesn't care what the text is), auto-filling the QSO log
  from a decoded FSK-ID (a new consumer of already-decoded data, no protocol touched), a nicer
  Identification tab UI. The one real risk is the character set: legacy's FSK encoding is a 6-bit
  scheme tied to a specific ASCII offset (`c - 0x20` on write, `+ 0x20` on read, no bounds-checking
  spotted in the decode) — sending characters outside legacy's assumed printable range would produce
  bytes a real legacy station can't decode correctly, and inventing a wholly new packet type would
  only be understood by other Scanline Studio users, not legacy ones. **User has flagged wanting to
  keep an eye on possibly expanding CW-ID/FSK-ID capability later** (e.g. richer macro tokens, auto
  QSO-log fill) — fine to do, as long as it stays on the "local text in/out" side of that boundary,
  not the wire protocol itself.

  **Still genuinely missing** (stale as a live list — all of this shipped 2026-08-12 except the
  `.mmv` sound-file format/resampler and the Identification tab UI's NR/RST section, both tracked
  in Tier 2 above): CW Morse table + timing generator (new, small, self-contained), the
  FSK-ID payload state machine on BOTH directions (RX continuation off the existing decoder; TX
  generator mirroring `VisHeader`'s existing pattern), the `.mmv` sound-file format + resampler (if
  sound-file ID is wanted), the NR/RST sub-packet, and the whole Identification tab UI. Bundled
  scope is comparable in size to the QRZ lookup feature built 2026-08-11 — needs its own dedicated
  session (2-round auditor plan-review per CLAUDE.md §7), not bundled into an ordinary wiring pass.
  Confirm priority/scope explicitly with the user before starting implementation.
- [ ] **VOX** (TX tone-burst preamble for a rig's own VOX circuit) — real but niche, this port
  already has real CAT PTT. Low priority per this doc's own earlier framing.
- [x] ~~RTS-on-RX~~ — **RESOLVED 2026-08-12, moved to `docs/removed-features.md`, not a backlog
  item.** Investigated directly while scoping the Options Radio tab: gates legacy's raw-serial
  RTS-pin PTT keying (`Comm.cpp`), the same "hand-written per-rig protocol code" family CLAUDE.md §2
  already excludes from this port. This port's real PTT path (`IRadioController.SetPttAsync`) goes
  through Hamlib/rigctld/flrig, which own their connection lifecycle entirely — there is no
  raw-serial-port concept left for this setting to gate. Not a conflict-to-investigate, an obsolete
  concept already fully superseded; see that doc's "Raw-serial RTS-pin PTT keying" entry.
- [x] **Sound-file ID** — second TX station-ID method (play a recorded clip instead of CW). **Shipped**
  (commit `9051699`, `docs/plans/sound-file-id-plan.md`) — `CwIdMode.SoundFile`, `MmvSoundFile`, and
  Options-dialog wiring (validation, browse, inline status) are all real.
- [x] **JPEG save quality setting** — **DONE 2026-08-15**. Re-scoped 2026-08-12: not blocked on
  "images are PNG-only" in the sense originally written — legacy's real `m_JPEGQuality` applies to
  the manual "Save Image As..." dialog (`SaveBitmapMenu`/`SaveImage`, `Main.cpp:10059-10084`), not
  the automatic RX-history save this port already does differently (always PNG, by design, not a
  gap). Built together with the Gallery's "Export frame" button (now real, not a stub) as planned.

**Not on this list, and why** (checked directly, not assumed — corrections to two items an earlier
automated survey pass flagged as still-open when they're actually already shipped):
- ~~`m_ReqSave` (abandoned-image save)~~ and ~~`m_SyncRestart` toggle~~ — BOTH already real and
  shipped earlier this session (`ReceiveHistoryRecorder.RecordAbandonedImageAsync`,
  `AnalogFmSstvDecoder`'s real `syncRestartEnabled` constructor parameter — both verified directly
  in current source, not from memory). An earlier survey pass's citations for these two were stale
  roadmap-doc wording that hadn't caught up to what had already shipped; this list is correct as of
  the date above.
- Perspective correction/webcam, full Hamlib extended commands, plugin sandboxing beyond
  same-process, `.MDT` import, full QSL/template designer, SSTV repeater/beacon, contest logging —
  all in "Explicitly deferred beyond v1" immediately below; real gaps, but the project has already
  decided these are out of scope for now, not silently missing.
- Native per-rig CAT parsers, OmniRig COM, RX-history drag-in-compositing, CItems plugin ABI,
  MMlink, Loglink live IPC, JASTA contest codes, CQ100 mode, legacy Windows font-switch buttons,
  raw-serial RTS-pin PTT keying (RTS-on-RX/PTT-lock) —
  all in `docs/removed-features.md` as removed-with-a-stated-replacement (even where imperfect),
  not silent gaps.

**The actual release blocker, separate from any single feature** (see "Release gates" below): the
project's own stated gate requires the real-hardware manual checklist to pass on Windows, Linux,
AND macOS before any tagged release — only Linux has ever actually been run against real/virtual
hardware. This isn't a coding task the agent can complete alone; it needs the user (or someone) to
actually run the app against real audio/CAT hardware on a Windows and a macOS machine. Flagging
here so it isn't lost, not adding it to the checkbox list above since "implement" isn't the right
verb for it.

## Explicitly deferred beyond v1

> **Folded into Tier 3 of "Path to 0.9 beta / road to 1.0" above**, which also adds the Phase 5
> plugin system and waterfall's deferred sub-items (notch-filter marker, signal-strength meter) to
> this same "parked, no near-term plan" bucket. This list stays as the citation detail for the
> original 8. **Correction 2026-08-27**: the "debug scope" sub-item listed here previously is done
> — it shipped as `DecoderTracePaneViewModel`/`DecoderTraceControl`, an explicit port of legacy's
> `TTScope` (`Scope.cpp`), verified directly against source during a menu-bar audit. This list
> entry was stale, not a real remaining gap.

- Perspective correction / webcam capture ([[07-image-pipeline]]).
- Full Hamlib extended command-set coverage beyond frequency/mode/PTT ([[04-rigctld]]).
- Plugin sandboxing beyond same-process isolation ([[11-plugin-system]]).
- Legacy proprietary `.MDT` log format import (ADIF is the supported migration path instead, [[08-logging]]).
- **Stale as of 2026-08-16, updated 2026-08-18**: the QSL/template designer ([[15-template-designer]]) is no longer deferred — it was the active 1.1 target (redesigned, not a legacy `.mtm` port; see [[19-path-to-1.1]]) and is now **implemented** (2026-08-18). Legacy `.mtm` **import** was later scoped as `ui_transition_plan.md` step 14 and **rejected outright, 2026-08-29** (a final "we don't want it" call, not a deferral) — see `docs/removed-features.md`'s "Legacy `.mtm`/`.mti` template import" entry and [[15-template-designer]]'s own Status section.
- SSTV repeater/beacon mode ([[06-sstv-dsp]], legacy `RepSet.cpp`).
- Contest logging (JASTA application, `MMCG.DEF` JARL area database) — out of scope entirely, not just deferred; see [docs/removed-features.md](../docs/removed-features.md).
- OCR (callsign-from-image recognition) — user decision 2026-08-11: "eh, maybe one day." No legacy precedent (verified against `yoniq-old/YONIQ-main` directly, zero OCR anywhere — the only "OCR" hits in the whole tree are `#ifndef OCRH`/`#define OCRH` include guards in a few unrelated `About.h` files, coincidental naming), so this is wholly new work with no port to lean on; QRZ.com lookup was split out of the same former backlog line and stays active (see above) since that part *is* a real legacy feature. The Frame-metadata card's former "Callsign · OCR" label and "OCR confidence" row were removed 2026-08-25 — no code or data stub for either remains anywhere in the UI.
- Stereo L/R input-chain level meters — user decision 2026-08-25: "maybe one day." This port's demod path is deliberately mono-only (same as legacy), so a real stereo meter needs an architecture decision on whether stereo capture belongs in this port at all, not just a wiring fix. The Input Chain card's placeholder L/R meter rows were removed 2026-08-25, not left as a stub with no defined target.
- Unattended RX (scan/watch list, dwell time, alert-on-decode) — user decision 2026-08-25: "maybe one day." No scanning/watch feature exists in `IRadioSessionService`/`ISstvSessionService`, and no product design exists for what "watching" or "scanning" should mean here — this needs a design pass before it's buildable, not just implementation. Formerly tracked as an active Large gap under "Must-implement backlog"; the Gallery tab's placeholder Unattended RX card was removed 2026-08-25.
- Session Frames (per-session received-frame list) — user decision 2026-08-25: "maybe one day." No per-session frame log concept has ever been designed (distinct from the real `ReceiveHistoryStore`, which persists across sessions). The Gallery tab's placeholder Session Frames card — previously a 15-row hand-written literal list with zero backing `ItemsSource`, then an honest empty state — was removed entirely 2026-08-25.
- Transmit tab Queue card — user decision 2026-08-25: "maybe one day." No queue concept exists in `TxControlsPaneViewModel`. Building it needs a real queue model (add/remove/reorder frames, persistence across app restart), not just a data hookup. **Removed from the UI 2026-08-25** (not left as a stub with no defined target) — card, `Panes.TxQueue.*` locale keys, and the encoder's dedicated right-column grid row all deleted.
- Transmit tab TX Log table — user decision 2026-08-25: "maybe one day." No logging-of-sent-frames feature exists. Needs a new sent-frame log subsystem with persistence. **Removed from the UI 2026-08-25** — header-only table, "TX time today"/"Duty cycle" rows, and `Panes.TxLog.*` locale keys all deleted.
- Transmit tab Recently Sent card — user decision 2026-08-25: "maybe one day." No send-history feature exists; building it needs history tracking plus real refill/open-in-editor logic, not just enabling the buttons (which were already correctly `IsEnabled="False"` with a "not yet implemented" tooltip). **Removed from the UI 2026-08-25** — card and `Panes.TxRecentlySent.*` locale keys deleted.
- Transmit tab Monitor audio row — user decision 2026-08-25: "maybe one day." Zero legacy precedent (verified: no "monitor" hits anywhere in `yoniq-old/YONIQ-main/Main.cpp` or `Sound.cpp`) and no tappable signal exists in the TX audio path (`IAudioEngine` has no playback-side event, only `SamplesCaptured` for RX). No product design exists for what this row should show — needs a design pass before it's buildable, not just implementation. **Removed from the UI 2026-08-25** — row and `Panes.TxControls.Telemetry.MonitorAudio`/`MonitorAudioValue` locale keys deleted.
- Transmit tab Occupied BW row — user decision 2026-08-25: "maybe one day," moved here from its own standalone deferral. Researched with a full implementation plan (`~/.claude/plans/wandering-glowing-otter.md`) then deliberately abandoned after design review (commit `c8b99ad`), not just not yet done — a real per-transmission spectral-union estimate, likely not meaningfully variable by content per that research. **Removed from the UI 2026-08-25** — row and `Panes.TxControls.Telemetry.OccupiedBw`/`OccupiedBwValue` locale keys deleted.
- Gallery tab RX History pane's own per-entry Re-decode button — user decision 2026-08-26: "maybe one day." No legacy precedent at all (verified: legacy's `RecentAdd`/manual record-play "recent files" list, `Main.cpp:5108-5186`, is entirely separate from `WriteHistory`'s image-history save, `Main.cpp:4880-4934` — legacy never attaches audio to a specific received-image history entry), so this is wholly new work with no port to lean on, distinct from the Receive tab's own real record/play-a-file Re-decode (`ISstvSessionService.StartRecordingAsync`/`DecodeFromFileAsync`, 2026-08-26). `ReceiveHistoryEntry` (`Core.Logbook`) has no audio field today, and `ReceiveHistoryRecorder` is wired directly to `ISstvDecoder`'s line/mode events with no access to raw audio samples — building this needs a real design pass (where continuous per-reception audio buffering lives, how it crosses the `Core.Logbook`/`Application` layering boundary, and a storage-growth policy — the store itself now keeps every entry indefinitely with no automatic retention cap at all, see the "RX history retention limit" entry above), not just a wiring fix. **Removed from the UI 2026-08-26** — button and the `Panes.RxHistory.Redecode` locale key deleted.
- Gallery tab RX History pane's own "14 MHz" frequency filter toggle — user decision 2026-08-26: "maybe one day." `ReceiveHistoryEntry` (`Core.Logbook`) has no frequency field to filter by at all — the entry only records mode/timestamp/file path, not the VFO frequency active at receive time. Adding one needs a real design/schema decision (does it record the dial frequency, does it matter across band-plan changes, does an existing entry backfill or stay unfiltered), not just a UI wiring fix. **Removed from the UI 2026-08-26** — toggle and the `Panes.RxHistory.Filter14Mhz` locale key deleted.
- Calibration menu's "Slant reference…" item — user decision 2026-08-26: "maybe one day," tier-5'd during the app-wide stub survey (no documented intent for any of these 5 — see the tier list in that session). No legacy precedent found at all (grepped across `yoniq-old/YONIQ-main` directly, zero hits for any spelling of "slant reference"), unlike Calibration's own Loopback self-test/Clock calibration siblings (both real legacy features, `m_LoopBack`/`SBTO`, `Main.cpp:13156-13173`) — this one has no concept to even scope yet. **Removed from the UI 2026-08-26** — menu item and the `MainWindow.Menu.Calibration.SlantReference` locale key deleted.
- Calibration menu's "Run 15-minute clock calibration" item — user decision 2026-08-26: "maybe one day," after investigation (unlike Slant reference above, this one IS a real legacy feature: `ClockAdj.cpp`, a live master-clock calibration wizard invoked from `Option.cpp:791-805`'s `SBClockAdjClick` — real-time RX audio through a tone-locked envelope detector, a scrolling waterfall-style canvas, and a manual two-click line-marking interaction to correct the app's assumed sample rate against a WWV/JJY/BPM time-standard tone; there is no fixed 15-minute timer anywhere in the legacy source, that framing was this port's own invented label). A round-1 auditor plan-review of a live-port design found genuine defects (wrong scroll-scroll direction, a click-anchor bug where the first clicked point drifts with the scrolling display, a missing AGC/intensity mapping that would render a black canvas, an `internal`-visibility compile error, several sign/rounding bugs) — buildable, but non-trivial. Before a round-2 review, re-examined whether it was worth building at all: this port's Auto Slant mechanism already auto-corrects per-line clock drift on every reception, tested to a 500 ppm and a 1% (10,000 ppm) mismatch (`spec/06-sstv-dsp.md`, `SlantTests.cs`) — well past real-world sound-card clock error. The manual wizard's only remaining edge is faster lock on a reception's first few lines, before Auto Slant's own tracking catches up; that narrow payoff didn't justify the build cost. **Removed from the UI 2026-08-26** — menu item and the `MainWindow.Menu.Calibration.ClockCalibration` locale key deleted.
- VFO card's Step pill — user decision 2026-08-26: "maybe one day," surfaced during Tier 4 (CAT-layer) scoping. No legacy precedent found at all (grepped `yoniq-old/YONIQ-main/cradio.h` directly — legacy's own CAT abstraction layer has zero mentions of a step concept). Almost certainly meant a local UI frequency-nudge increment, not anything read from or set on the rig via CAT — there was nothing to scope as CAT-layer work at all. **Removed from the UI 2026-08-26** — pill and the `RadioStatus.StepValue` locale key deleted. (Split/RIT, its former "still a real CAT concept" siblings on the same card, were removed for the same reason shortly after — see the entry below; this line is now historical, not still-contrasting.)
- VFO card's Split and RIT pills — user decision 2026-08-26: "maybe one day." Bandwidth (the 4th sibling on this card) turned out to be a real, genuinely wireable CAT concept and shipped (`782e3fe`) — but checking Split/RIT against actual legacy source (not assumed from "same card, same shape") found no legacy precedent at all: `cradio.h`/`cradio.cpp`/`Main.cpp`/`Option.cpp` have zero mentions of either. The ONLY place either term appears anywhere in `yoniq-old/YONIQ-main` is `OmniRig_OCX.h`, OmniRig's own auto-generated COM type-library binding header, which exposes `Split`/`Rit`/`Xit` as properties on OmniRig's COM interface — never read or written by any of YONIQ's own code, and OmniRig itself is a removed feature in this port (partial CAT-backend replacement, see this doc's OmniRig entries). So unlike Bandwidth, there was no real legacy behavior to eventually port here, and Split separately raises its own unresolved design question (how a second TX frequency fits this port's single-`FrequencyHz` model) even setting legacy aside. **Removed from the UI 2026-08-26** — both pills and the `RadioStatus.SplitValue`/`RadioStatus.RitValue` locale keys deleted.
- Favourites header's own "Import"/"Scan M1-M3" mini-buttons — user decision 2026-08-26: "maybe one day," tier-3'd during the app-wide stub survey. "Import" had no defined source format to import from anywhere (not a legacy `.ini`/`.txt` format, not an in-app export counterpart). "Scan M1-M3" would need the radio to report its own physical memory-channel contents over CAT — `IRadioSessionService` has no memory-channel-read concept at all, real Hamlib/rigctld-layer work (CLAUDE.md §2's CAT scope), not bounded UI wiring like the same header's own "Edit list…" turned out to be. **Removed from the UI 2026-08-26** — both mini-buttons and the `RadioStatus.ImportFavourites`/`RadioStatus.ScanFavourites` locale keys deleted.
- Gallery tab's own Sort and Size chips (filter toolbar) — user decision 2026-08-26: "maybe one day," surfaced while scoping items outside the Options menu after the app-wide stub survey's Tiers 1-4 closed. Both were static `Border`s (no `Command`, no `IsEnabled="False"`, no tooltip) reading "SORT NEWEST"/"SIZE M" next to the real filter `RadioButton`/`ToggleButton` controls — a genuine FAKE-LIVE pair (`spec/16-gui-wiring-survey.md`'s own prior finding, never previously actioned), not caught by a search for the app's disabled-stub marker since neither chip was ever disabled. No sort-order or thumbnail-size-selection concept exists anywhere in `RxHistoryPaneViewModel`. **Removed from the UI 2026-08-26** — both chips and the `Panes.RxHistory.SortChip`/`SizeChip` locale keys deleted.
- Gallery tab's own "Selected Frame" card Grid/dist row — user decision 2026-08-26: "maybe one day," same session as the Sort/Size chips above. Was a static two-value placeholder — distinct from the Receive tab's own real Grid/dist row (`RxImagePaneViewModel.GridDisplay`, wired 2026-08-25), which is NOT simply un-wired here: that row's grid value comes from a live FSK station-ID decode captured only at reception time (`LookupGrid`, reset to null at the start of the next reception), and `ReceiveHistoryEntry` (`Core.Logbook`) has no grid field at all — so there is nothing to display for a past history entry without a real persistence change (either a new `ReceiveHistoryEntry` field captured at record time, or falling back to the logged QSO's own `GridSquare` via `LinkedQsoId` for entries that have actually been logged). Real design work, not bounded UI wiring. **Removed from the UI 2026-08-26** — row and the `Panes.RxHistory.GridDistLabel`/`GridDistValue` locale keys deleted.
- **SNR readout** (status-bar chip, tab-strip chip, Gallery selected-frame suffix — all three the same underlying feature) — user decision 2026-08-26: scoped as a real backlog item, not "maybe one day." Legacy has no SNR concept either (`sstv.cpp`/`Main.cpp`/`sstv.h`: zero hits for "snr"), so there's no port target — but unlike the "no legacy concept, no clear intent" removals above, this is a genuinely useful, well-understood operator feature (see the session discussion: quick pre/mid-decode "is this worth sticking with" signal, antenna/propagation troubleshooting, per-past-frame quality triage in Gallery), just real new DSP work. What exists today: `AnalogFmSstvDecoder.SignalPeakLevel` (`_levelAgc.CurMax / 32768.0`) and `RxImagePaneViewModel.AgcGainDisplay` are both real, already-wired *signal-level* measurements — the missing half is a noise-floor estimate, which nothing in this port computes (would need sampling an out-of-band/inter-tone segment of the spectrum, or a similar technique, not just reading an existing field). Scope for whoever picks this up: (1) design the noise-floor estimation approach (no legacy formula to port — this would be genuinely invented DSP, so needs its own plan-review before coding, not a "port first" job per CLAUDE.md §2); (2) a real `SnrDb`-shaped property on `RxImagePaneViewModel`, live during reception; (3) persistence onto `ReceiveHistoryEntry` if the Gallery/history use case matters (currently has no such field, same gap as Grid/dist above); (4) re-wire the three now-removed placeholder sites. **Removed from the UI 2026-08-26** — status-bar chip (`MainWindow.StatusBar.SnrValue`), tab-strip chip (`MainWindow.TabStrip.SnrValue`), and Gallery's Mode/SNR row suffix (`Panes.RxHistory.SnrSuffixValue`, row relabeled "Mode / SNR" → "Mode") all deleted. `Panes.RxDecodeLog.ColSnr` (the still-unbuilt Decode-activity table's own SNR column header) was deliberately left alone — that's the separate, already-tracked "Structured per-decode event log" backlog item (`:1446`, `:1549`), not this one.
- **Demodulator/mode settings profile system** — legacy's Profile menu (`Main.dfm`'s `KP`, 8 named
  slots `KP1`-`KP8`, `KPA`/`KPD` save/delete per slot, `KPDef` restore-default, `KPInit`
  reset-all), confirmed real and working directly against `Main.dfm`/`Main.cpp` (`SetProFile`) —
  not the earlier, separately-debunked `PRODEM` per-mode-speed idea (see the correction above,
  which was about a different, nonexistent thing). No equivalent exists anywhere in this port
  today: nothing saves/reloads a named snapshot of the current demod/mode configuration. The
  existing "preset" systems in the port (`RadioStatusViewModel`'s frequency/mode memories,
  `TxImageEditorPaneViewModel`'s font/color presets, the fixed `RxBpfPreset` enum) are all
  unrelated. Surfaced 2026-08-27 during a menu-bar audit; needs a product decision — is this still
  worth building given the port's smaller surface of manually-tunable demod knobs versus
  legacy's — before it's scoped.
- **Launch the TX image in an external editor** — legacy's `KEE`/`ExecPB` mspaint shortcut. No
  equivalent `Process.Start`-based external-editor launch exists in this port; every `OpenEditor*`
  method on `TxControlsPaneViewModel` opens the in-app `TxImageEditorPaneViewModel` crop/resize/
  overlay editor instead. Likely an intentional supersession — the in-app editor is materially
  more capable than legacy's raw mspaint handoff — but that decision was never written down, and
  `docs/removed-features.md` has no entry for it. Surfaced 2026-08-27 during the same audit; needs
  either a removed-features.md entry confirming the supersession, or a scoped "open in external
  app" feature if an escape hatch to the user's own image tool is still wanted.

## Verify later with human — items neither the agent nor the auditor could resolve alone

Populated during autonomous work on the "Must-implement backlog" above (2026-08-08 onward, user's
own instruction: "IF you get stumped by bugs, solution directions, ask the auditor for help. If the
auditor can't figure it out, put it on the 'verify later with human' list" — this is that list, not
a place to silently give up; every entry needs a one-line reason it's genuinely stuck, not just
"medium effort"). Empty as of this list's creation, when the Logbook UI pane item (shipped
2026-08-08, see [[18-path-to-1.0]]/`spec/16-gui-wiring-survey.md`) was still in its normal
plan-review flow — still empty today; nothing has been routed here.

## Release gates

> **Restated on the "separate axis" of "Path to 0.9 beta / road to 1.0" above**, with the open
> question of whether 0.9 beta itself needs the full 3-platform pass or can ship Linux-validated-
> only — not yet decided, don't assume either answer.

Before any tagged release: full [[13-testing]] manual hardware checklist (real rig CAT session, real audio device round-trip, real third-party `rigctld` interop) passes on at least one Windows, one Linux, and one macOS machine, in addition to the automated CI matrix being green.

## Open items requiring a decision before the relevant phase starts

> The 3 human-only actions below (Clublog key, Chilkat/FastReport license, `Terms.txt`
> confirmation) are also listed on the "separate axis" of "Path to 0.9 beta / road to 1.0" above,
> alongside the 3-platform release-gate decision — same items, kept here too for their fuller
> context. A 4th item used to be listed here, a `.mtm`/`PARALIST.BIN` reverse-engineering pass —
> moot as of 2026-08-29, see the [[15-template-designer]] bullet below.

- ~~Project license~~ — **decided**: LGPL-3.0-or-later, matching upstream. See [LICENSES.md](../LICENSES.md). The remaining open sub-item is confirming the `Terms.txt` freeware-clause interpretation with the upstream author (JE3HHT) if the project ever moves toward commercial distribution — not a blocker for development.
- [[08-logging]]: source and license-audit the callsign-prefix/country dataset before bundling (Phase 4) — `ARRL.DX` is already ruled out, see [LICENSES.md](../LICENSES.md). **Pre-audited 2026-08-02**: Clublog's `cty.dat` has no fee, but redistribution requires a human to email Clublog's helpdesk describing the proposed use and obtain an individual API key before the data can be downloaded/bundled — not a simple open-license drop-in. See [LICENSES.md](../LICENSES.md)'s "Candidate future asset" note. Phase 4's other deliverables have since shipped (logbook, ADIF import/export, QRZ.com online lookup — see [[08-logging]]); this is the one item remaining before **offline** callsign/country lookup specifically can ship: someone actually emails Clublog and gets the key (not agent-doable), then the real bundled-asset row gets added to LICENSES.md.
- ~~[[05-audio-engine]]: confirm PortAudio latency is acceptable on Windows before committing to it as the sole backend, vs. adding a native WASAPI backend later~~ — **resolved, PortAudio rejected outright.** Two independent Opus consultations plus direct verification in this repo's own dev sandbox found PortAudio fails this spec's own requirements, not just a latency concern: no real device-change API in any released version, no PulseAudio/PipeWire host API on Linux (confirmed by creating a real virtual sink and showing a live PortAudio device probe couldn't see it at all — the exact virtual-cable workflow this spec requires, failing in practice), and no sample-rate conversion. Switched to `miniaudio`, whose WASAPI backend (`IAudioClient3` low-latency mode) *is* the native-WASAPI escape hatch this item used to hold open, without needing COM interop in `ScanlineStudio.Core.*`. See [[05-audio-engine]]'s Backend choice section for the full reasoning.
- [[15-template-designer]]: **resolved 2026-08-29, not open anymore.** The modern template designer shipped 2026-08-18 needing no `.mtm` work at all. A later re-check (`ui_transition_plan.md` step 14 scoping) found the earlier "needs a reverse-engineering pass" premise here was **false** — `yoniq-old/YONIQ-main/Draw.cpp`'s `SaveToStream`/`LoadFromStream` already fully specify the format — but legacy `.mtm`/`.mti` import was then **rejected outright** as a product decision (2026-08-29, see `docs/removed-features.md`), so there is no remaining prerequisite to track.
- [LICENSES.md](../LICENSES.md): confirm whether Chilkat or FastReport actually back a real feature by building and running the legacy binary directly (not verifiable from source alone) — currently assumed unused/orphaned based on a source-only search.
