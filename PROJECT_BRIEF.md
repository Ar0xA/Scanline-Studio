# Project brief (resume point)

Scratch file for resuming after `/clear` — not a spec doc, delete or ignore once stale. Pruned
2026-08-08 (was 2978 lines/46 entries), 2026-08-11 three times (was ~460, then ~1060, then ~1030
lines) — everything but the active entry below was already in a detailed commit message or
migrated into `spec/14-roadmap.md`/`spec/16-gui-wiring-survey.md`/`spec/09-ui.md`/`CLAUDE.md` — see
git history for this file if older context is ever needed.

## Resume here (2026-08-11, latest, ACTIVE) — back to wiring real functionality behind GUI controls

**What this is**: the Industry UI redesign (below) is fully closed — every view is visually final.
Subject switches back to what it was before that redesign detour: making the GUI's controls
actually DO something, per `spec/14-roadmap.md`'s "Must-implement backlog" and
`spec/16-gui-wiring-survey.md`'s per-control inventory (both current as of 2026-08-11 — the survey
was just freshly re-verified against the redesigned code, citations and all).

**This session (2026-08-11) wired 4 quick-win controls** (all had real backend data/settings
already existing one property away, no product decision or new DSP needed — the survey's own
PARTIAL/"genuinely low-hanging fruit" notes flagged exactly these): TxControls "Output device" row
(bound to real `OutputDeviceNameDisplay`), RadioHeaderView "Store current" preset button (new
`StoreCurrentPresetCommand`, NOT a reuse of pre-existing `SavePresetsCommand` — that one only
re-persists `EditorRows`, which has no visible editor UI; had to actually append the current
freq/mode as a new row first), Options window Decode tab's Auto-Sync/Auto-Slant checkboxes (real
`SstvDecoderSettings` fields, already consumed by the decoder, just never had a UI path — new
`ResetDecodeToDefaultCommand` added to match every other tab's pattern). Deliberately did NOT wire
Decode tab's Auto-stop/Auto-restart despite also having real backing fields — their loc text
doesn't match what those fields actually gate (logged in the survey, not fixed). All 4 build+test+
runtime verified in the live app (screenshots, settings.json before/after) before being marked REAL
in the survey — not just wired-and-assumed-working. 4 new/extended tests added
(`OptionsWindowViewModelTests.cs`). **Auditor code-review round 1 done, all findings fixed**: one
real blocker (`StoreCurrentPresetCommand` had no `CanExecute` guard — clicking it before the first
`RadioState` ever arrives, e.g. no radio connected, silently persisted an unremovable
"0.000000 USB" preset since no in-app UI exposes `EditorRows`/`RemovePresetRowCommand` to delete
it; fixed with a `_currentFrequencyHz > 0` guard + `NotifyCanExecuteChanged()` from
`OnStateChanged`, runtime-verified the button now renders disabled with no radio connected) plus
several risk/nit fixes: `SavePresetsAsync` now sets `ErrorMessage` on failure (new
`RadioStatus.Error.SavePresetsFailed` loc key — was silently swallowing failures, a real gap now
that "Store current" made this a reachable path for the first time, `SavePresetsCommand` itself was
previously unreachable from any shipped view); a `Math.Round` fix for a narrow FP-truncation edge
case in the Hz cast; a stale doc comment and stale survey narrative paragraph both corrected; 3 new
`RadioStatusViewModelTests` (CanExecute gating, successful append, failure→ErrorMessage) + 1
extended `PaneViewModelTests` assertion. Full solution rebuilds clean, 180/180 UI tests pass. **Not
yet committed or pushed — ask before doing either.**

**5th quick win + a new Gallery feature, same session**: Gallery tab's "Log entry" status row (was
FAKE-LIVE literal, now reads the real `ReceiveHistoryEntry.LinkedQsoId` field via an
`ObjectConverters.IsNull`/`IsNotNull` two-`TextBlock` swap, same pattern as `RadioHeaderView`'s
CAT-linked pill) — found while investigating whether `spec/16-gui-wiring-survey.md`'s remaining
STUB/PARTIAL rows had more one-property-away wins. That investigation surfaced a bigger find:
`IReceiveHistoryStore.SetNoteAsync`/`SetFlaggedAsync`/`SetLinkedQsoIdAsync` were fully implemented,
real, SQLite-backed, doc-commented as built specifically for the Gallery UI, but had **zero call
sites anywhere in `ScanlineStudio.UI`**. Two of the three are now wired: new **Note** `TextBox`
(debounced 600ms persist) and **Flagged** `CheckBox` (immediate persist) added to the Gallery
Selected-frame panel — genuinely new UI (no mock2 slot, same precedent as `SelectLatestCommand`),
not a rebind, since neither control existed before. Both runtime-verified end-to-end against the
real SQLite `history.db` (inserted a real test row + real PNG, typed a note, watched the DB column
update after the debounce window, toggled the flag, watched `IsFlagged` flip 0→1). `SetLinkedQsoIdAsync`
("Open in Log") still deliberately unwired — needs a QSO picker/auto-create UI decision, a bigger
scope than the other two.

**Took 3 auditor rounds to close, not the usual 1-2** — worth remembering the shape of what was
wrong, not just that it's fixed now. Round 1 found 2 real blockers: (a) a live `Recorded` refresh
(fires on every completed/abandoned frame during active RX) reloaded the Note/Flag fields
unconditionally on same-Id re-select, silently clobbering whatever the user was mid-typing —
annotating a frame while RX keeps running is the feature's entire point, so this was reachable on
essentially every real use, not an edge case; (b) a successful persist never got written back into
the in-memory `Entries` list (`ReceiveHistoryEntry` is an immutable record), so switching away and
back showed the stale pre-edit value. Round 1's fix for (a) gated the reload on `_previewedEntryId`
— round 2 caught that this field is ALSO nulled on a failed preview load (unrelated concern, same
field reused for two things), reopening the identical clobber for any entry with an unreadable
image file; round 2 also flagged an unverified hazard (does `Entries[index] = updated`'s `Replace`
null the ListBox's real `SelectedItem` binding, same class of thing `_isRepopulating` already
exists to guard for `Clear()`) and a Task-chain poisoning risk in the flag-persist ordering fix.
Round 3 closed all of it: a genuinely separate `_loadedEditsEntryId` field (independent of
`_previewedEntryId`), reused the `_isRepopulating` guard for the `Replace` too (plus a
`previousSelection` fallback for the non-selected-index case), and moved the flag-persist chain's
`Dispatcher.Post`/`await previous` fully inside its own try so the chain can't wedge itself. One
real-`ListBox`-binding test added specifically to empirically settle the unverified-Avalonia-
behavior questions rather than reason about them on paper (matches this project's own "verify
Avalonia binding with a real control" convention). Auditor's own round-3 verdict: genuinely closed,
don't run a round 4 — one narrow logged-not-fixed risk remains (a refresh mid-edit may steal
keyboard focus from the Note `TextBox` via its `IsEnabled` binding disabling-then-re-enabling it;
UX-only, no data loss, unverified whether Avalonia actually does this).

10 tests total for this feature now (`PaneViewModelTests.cs`), 2 new call-tracking lists + a
configurable-delay queue added to `FakeReceiveHistoryStore`. Full solution builds clean, 190/190 UI
tests pass. Smoke-tested once more end-to-end against the real `history.db` after all three rounds
of fixes (typed a note, confirmed the DB column updated). **Committed this piece — see git log; not
yet pushed.**

**Current wiring totals** (`spec/16-gui-wiring-survey.md`, ~281 controls tracked, +2 for the new
Note/Flagged controls): **~130 REAL, ~103 STUB, ~47 FAKE-LIVE, ~2 PARTIAL** (was ~122/105/48/4 at
session start).
Densest remaining gaps: Receive tab's Sync&Slant/Input-chain/Signal-quality cards (SNR/squelch/
notch/noise-floor — no live audio-chain measurement exists in `Core.Audio`/`Core.Sstv` for most of
these, real new DSP work not just wiring); Options window's Decode's remaining 7 controls plus
Identification/Advanced tabs (~53 controls, still stub); Transmit tab's Queue/TX-log/Recently-sent
cards (100% stub, no such features exist); TX image editor's canvas-overlay safe-area/callsign/
report-plate text (FAKE-LIVE — reads as real burned-in TX content, arguably the most deceptive
placeholder in the app). Remaining PARTIAL: RxFrameMeta Note/Override-callsign `TextBox`es (needs a
real backing field on the frame/session model, not just a rebind).

**`spec/14-roadmap.md`'s "Must-implement backlog" is the prioritized list to work from**, not the
survey directly — the survey tells you WHAT is stub/fake, the backlog tells you what order to
tackle it in and why. Status as of this pruning pass:
- [x] Logbook UI pane, DSP decode-accuracy residuals, waterfall color/palette, RX history browser
  affordances — all shipped and closed (checkboxes/notes in the roadmap doc itself are current,
  verified against `git log` while pruning this file, don't re-verify again).
- [ ] **Options dialogs (real functionality behind the ~50+ disabled controls)** — **BLOCKED on
  user scoping input, ask again before touching.** The Options window is now a real, Industry-
  styled 7-tab dialog (shipped this session, `OptionsWindowView.axaml`), but its actual
  functional wiring is unchanged from before the redesign — still the same `IsEnabled="False"` +
  `Options.NotImplemented.Help` placeholders underneath, just re-skinned. Several of its stub
  sections overlap with the CW-ID/FSK and other items below — a real per-section split plus an
  auditor UI-design plan-review pass is needed before building anything (this project's own
  "audit UI design before building" convention), not a straight port. **User was asked which
  pieces to prioritize in an earlier session and never answered — re-ask, don't assume.**
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
(2 rounds) → verify (build + full relevant test suite, real-window screenshot if UI-visible) →
commit → push. Escalation path if stuck: ask the auditor; if the auditor also can't resolve it, log
to `spec/14-roadmap.md`'s "Verify later with human" section rather than stalling.

**Next action on resume**: ask the user which Must-implement backlog item to start with — Options
dialogs needs their scoping answer specifically; OCR/QRZ and CW-ID/FSK are the two next-biggest
items if Options stays blocked.

---

### Industry UI redesign — CLOSED, all 4 commits pushed (`c392ac8`, `658088f`, `01da615`, `4c3e777`)

Full pixel-perfect port of every view from the old "Aesthetic Directive" look to the new "Industry"
wireframe design system (steel-blue accent, Barlow/Barlow Condensed, square corners, hairline
borders, blueprint corner marks). 8 phases (fonts/tokens → atoms → chrome → Receive/Transmit/
Gallery/Logbook tabs → Options window → cleanup), all shipped across several sessions ending
2026-08-11. **For any future work touching visual design**: `spec/09-ui.md`'s "Visual design
direction" section describes the current system accurately; `Styles/Atoms.axaml`/`AtomsTokens.axaml`
are the atom/token inventory (each atom has its own inline doc comment); the old `Tokens.axaml`/
`Cards.axaml`/`ChromeOverrides.axaml` files are deleted, don't reference them. Detailed phase-by-
phase history (atoms built, bugs found, screenshots taken) is in each phase's own commit message —
`git log --oneline` and `git show <hash>`, not this file.

**Worth remembering, not obvious from the code alone**:
- `IndustryCheckBoxTheme` (14x14 box + tick-glyph `Path`) is the first real checkbox atom — every
  mockup toggle before it was `.seg`/`.mini`. `IndustryBtnDanger`/`IndustryTxToggle`/
  `Ellipse.IndustryLed`/`TextBlock.IndustryReadoutSmall` were added in the final cleanup phase to
  port the last few controls that still referenced now-deleted old-design classes.
- **`MainWindow.axaml`'s root `Window` has an explicit `Background="{StaticResource IndustryBg}"`**
  — it silently relied on old `Cards.axaml`'s bare `Window` selector for this before, which broke
  (whole page went pure white) the moment that file was deleted. If a future full-app background
  change is ever needed, this is the one place to change it; don't assume a per-card Background is
  enough, most of the page relies on this root fill showing through uncovered areas.
- The Transmit tab's centre column (TX Image Editor) is correctly blank until an image is
  selected (`MainViewModel.ActiveEditor` is null-until-populated by design, confirmed working
  end-to-end) — don't mistake this for a bug if it comes up again.
- 3 stub menu items (`Configurations > Station`/`Audio devices`, `Rig & PTT > CAT interface`) were
  pruned as exact duplicates of real Options tabs; everything else in those menus was deliberately
  left alone (different concept, live diagnostic action, or no Options equivalent exists yet — see
  `spec/16-gui-wiring-survey.md`'s menu-bar section for the per-item reasoning if this comes up
  again).

**Established atoms available** (`Atoms.axaml`) — check here before inventing new markup:
`IndustryGroupBoxTheme` (+`Classes="blueprint"` for corner marks) / `IndustryBlueprintBorderTheme`
(plain bordered, always corner-marked, no title notch) / `IndustryMiniButtonTheme`/
`IndustryMiniToggleTheme`/`IndustryMiniRadioTheme` (chip forms) / `IndustryCheckBoxTheme` /
`IndustryStepperTheme` / `IndustrySliderTheme` (+`.h6`/`.h8`) / `IndustrySegTheme` /
`IndustryDisclosureToggleTheme` / `IndustryPlot` / `ProgressBar.Industry` / `IndustryTableHeader`/
`HeaderRule`/`Cell`/`RowRule` (plain-`Grid` table) / `IndustryThumbnail`+captions /
`IndustryListBoxItemTheme` / `IndustryHatchPanel` / `IndustryMeter`+`Track`/`Fill`/`Marker` /
`IndustryRows`/`Row`/`RowLabel`/`RowValue` / `IndustryKicker` / `IndustryInput` (TextBox/ComboBox/
NumericUpDown, not DatePicker — no Industry DatePicker theme exists) / `IndustryBtn21`/`22`/`24`/
`25`/`26` (+`Primary`/`Secondary`/`Danger`, usually needs `Padding="7,0"`) / `IndustryTxToggle` /
`Ellipse.IndustryLed`+`.active`/`.alert` / `TextBlock.IndustryReadoutSmall`/`IndustryHelpGlyph`.

**Still open, logged not fixed** (low priority, revisit only if it becomes relevant):
- Mockup's "Macros" card (Receive tab) has no backing feature — needs a product decision on F1-F6.
- Transmit tab Output card's Drive shows a bare percent where the mockup shows dBFS — needs a
  product decision, not a relabel.
- Gallery's "File" row shows full-path end-trim instead of the mockup's filename-only middle-trim
  — needs a converter/VM property.
- Centre-column (Transmit Editor) stepper nits (button-cell sizing, disabled-state background
  leak) — cosmetic, recoverable via `git log` if ever prioritized.

### Pre-redesign work — CLOSED, all pushed (see `spec/14-roadmap.md`/`spec/16-gui-wiring-survey.md`/`spec/17-rx-telemetry-feasibility.md` for detail, not this file)

Before the redesign, this project spent several sessions wiring real backend functionality behind
GUI controls, closing most of `spec/17-rx-telemetry-feasibility.md`'s REAL-EASY list (slant/sync
readouts, decode-time signal telemetry Tier A, TX device name, Tone map, Gallery/logbook metadata,
operator-profile + minimal TX-macro engine, Auto-correct on/off, Buffer·XRUN, RX history browser
affordances) plus a full GUI wiring survey (`spec/16-gui-wiring-survey.md`) and the waterfall
color/palette rendering (6-stop SDR heatmap + spectrum trace). Every item is either checked off in
`spec/14-roadmap.md`'s backlog or fully described in `spec/17`'s own doc — this file no longer
carries the batch-by-batch history, it's all in commit messages (`git log --oneline` covers
2026-08-08/09) and those two spec docs.
