# UI transition plan — from ui_findings.md (second pass), source-audited 2026-08-28

Working plan. Each item: change → files → verification. Findings references (T1-n/T2-n) map to
ui_findings.md tiers. Audit corrections that change the plan are folded in where they apply.

## Sequencing

1. Workspace usable below 1920 DIP (T1-1)
2. SEND panel honesty + contextual Ctrl+S (T1-2, T1-3)
3. Full-size RX/history viewer + make Previous-Frames thumbnails live (T1-5 + T2-6 pulled forward)
4. Per-item RX history delete, optional manual prune (T1-4, reframed — see item)
5. QSO handoff: RX → TX → Logbook (T1-6 + T2-8 merged — same files)
6. Latch per-frame metadata (T2-4) — **auditor cadence required**
7. Options: Apply-without-close, Connect against form values (T2-3, corrected scope)
8. Sound-file ID validation at selection/save time (T2-2)
9. Label/help cleanup + collapse Decode Activity + right-click discoverability (T2-9, T2-10, T2-11, T2-7 naming)
10. Persistent RX mode lock (T2-5) — **auditor cadence required**
11. Frequency entry / honest read-only header (T2-1)
12. Auto-save RX audio (new — Options flag + per-frame WAV + retention) — **auditor cadence required**
13. Scanline template bundle export/import (T1-7, half A)
14. Legacy `.mtm` template import (T1-7, half B) — **deferred 2026-08-29, "maybe one day," see below**

Changes vs. the findings' own order and why:

- T2-6 (passive thumbnails) pulled forward into step 3: it is the same wiring as the viewer
  (thumbnail click → open entry in viewer), cheaper together than apart.
- T1-7 (.mtm import) is **restored to the active queue** as steps 13-14 (an earlier pass of this
  plan had demoted it to a scope decision on the premise that spec/15-template-designer.md's
  deferral was justified by the `.mtm` format being unreverse-engineered — that premise is false.
  `yoniq-old/YONIQ-main/Draw.cpp` (`Save`/`LoadFromStream`, lines 408-524 and 4963-5385) fully
  specifies the format: this is implementable work, not a research prerequisite. Tail position
  reflects it has no dependents and is the largest item here, not a lower tier. spec/15's stale
  "not reverse-engineered" claim (lines 267-282) should be corrected as part of step 14.
- QSO **delete** half of T1-4 is demoted to a scope decision: parked by explicit user decision
  2026-08-15 (spec/14-roadmap.md "Logbook deltas — PARKED", `feedback_logbook_minimal_footprint`
  memory; a reviewed implementation plan already exists at `~/.claude/plans/logbook-deltas.md`).
  RX-history delete remains real Tier-1 work (docs/removed-features.md leaves manual prune open).
- T2-8 (logbook shape) merged into step 5 — prefill, MHz display, and the entry form are the same
  files; doing them separately means touching LogbookPaneViewModel twice.
- Step 12 (audio) must follow steps 4 and 6: it shares step 4's delete path (a deleted frame's WAV
  must go with it) and step 6's `ReceiveHistory` schema migration — do both `ADD COLUMN`s in one
  pass, since the store's migration code requires column order to match `CREATE TABLE` and that
  order becomes permanent once shipped.
- Step 14 **deferred again, 2026-08-29** (explicit user decision, not a reversion to the earlier
  false "unreverse-engineered" premise above — that premise stays corrected): parked as "maybe one
  day," a plain prioritization call, no docs/removed-features.md entry (that doc is for a legacy
  capability permanently NOT ported, not an open deferral). Fable was consulted on both open Tier-3
  sub-questions before the deferral (verified against `Draw.cpp`/`Main.cpp`, not guessed): `.mti`
  import would ride the same parser as `.mtm` (`LoadTemplate` calls the identical
  `CDrawGroup::LoadFromStream` for both extensions, differing only in the file-dialog filter string
  — Main.cpp:10090/10134), and the legacy `def1-5.mtm`/`Stock/*.mtm`/`Current.mtm` sample files
  should NOT be committed as test fixtures (YONIQ's own `License.TXT` calls the *program* freeware
  under the author's copyright, distinct from the LGPL source license, and the files embed
  author-created bitmap artwork — the project's own "when in doubt, exclude" license-audit rule
  applies); fresh fixtures saved from the running legacy binary on originally-authored content, the
  same precedent already used for the committed `.mmv`/`.bmp` golden vectors, would be the safe
  alternative if/when this is picked back up.
- Step 13 (native template bundle) is fully independent of steps 1-12 (touches only
  `TemplateStore`/`PersistedTemplateElement`/`TxImageEditorPaneViewModel`/`FilePickerService`/
  `en.json`) and can be pulled forward anywhere dependency order doesn't force otherwise.
- Step 14 (`.mtm` import) must follow step 13 — it lands on step 13's import file-picker surface
  and reuses its plumbing.

## Tier 1 (+ pulled-forward Tier 2)

### 1. Workspace below 1920 DIP (T1-1)

Change: drop `MinWidth="1920"` (src/ScanlineStudio.UI/Views/MainWindow.axaml:18) to something a
1366-wide screen fits (e.g. 1280×640), then make the three tab layouts survive it:

- Receive tab (MainWindow.axaml, Receive TabItem): the left controls column and right details
  column already have fixed widths; wrap the middle column's content so the Viewbox-scaled RX
  image shrinks (it already scales) and let the Previous-Frames strip collapse (make its column
  `MinWidth`+star, or hide the strip under a breakpoint via a bound panel visibility).
- Transmit tab: TxControlsPaneView sidebar is fixed-width; TxImageEditorPaneView already scrolls
  (`EditorScrollViewer`). Verify the bottom QSO-FILL/SEND bar (TxImageEditorPaneView.axaml
  ~1905-2000) reflows or scrolls rather than clipping.
- Do this as layout-only work: no VM changes. Prefer `ScrollViewer` + `MinWidth` relaxation over a
  full responsive redesign in the first pass; dockable/compact panels are a later enhancement, not
  the acceptance bar.

Verify: run the app with the window at 1280, 1366, and 1600 wide; every tab's controls reachable
(scrollbar acceptable, clipped/overlapping controls not). Check 125%/150% display scaling on the
1920 monitor. Screenshot each tab at 1366.

### 2. SEND panel honesty + contextual Ctrl+S (T1-2, T1-3)

Confirmed in source: SEND header contains only Cancel/Apply (TxImageEditorPaneView.axaml
1975-1997); the XAML comment at ~1918-1920 already records the missing piece — "Apply" and
"Transmit" are separate commands on two VMs with no chain.

Change (a): add an "Apply & Transmit" primary button to the SEND row.
- TxImageEditorPaneViewModel.cs: add an `ApplyAndTransmitRequested` event (or async request
  delegate) fired after a successful Apply.
- MainViewModel.cs: wire it to TxControlsPaneViewModel's existing transmit command, same
  cross-pane event pattern `LogQsoRequested` already uses (RxImagePaneViewModel.cs:1792).
- Respect TxControls' CanExecute (no transmit while already transmitting / TX ERROR); button
  binds its IsEnabled to the same gate. Keep plain Apply as the secondary action; rename the
  header key `Panes.TxImageEditor.SendHeader` only if Apply-only remains the sole action.
- assets/locale/en.json: new button string + tooltip.

Change (b): contextual Ctrl+S.
- MainWindow.axaml:78 binds Ctrl+S to `RxImage.SaveFrameCommand` unconditionally. Move RX export
  to Ctrl+Shift+E ("Export received frame…" naming, see step 9) and make Ctrl+S a no-op or
  editor-Apply while the Transmit tab is active — simplest: a MainViewModel command that
  dispatches on the selected tab index, bound once.

Verify: manual — edit a card, click Apply & Transmit, confirm one action applies and starts TX
(with audio device on loopback/null); confirm the button is disabled during an active TX. Press
Ctrl+S on the Transmit tab and confirm the RX save dialog does NOT appear. Add a unit test for the
dispatching command's tab routing if it lands in MainViewModel.

### 3. Full-size viewer + live thumbnails (T1-5 + T2-6)

Confirmed: no DoubleTapped/zoom/viewer anywhere in Views/ViewModels; Previous-Frames strip
(MainWindow.axaml 702-758) is a passive ItemsControl whose caption callsign is a literal "—".

Change: one new `ImageViewerWindowView(+Model)` (Views/ + ViewModels/), opened with a
ReceiveHistoryEntry list + start index:
- Fit-to-window default, 100%, zoom +/- (mouse wheel), pan by drag, Left/Right = previous/next,
  Esc = close. Copy-to-clipboard and Open-file-location actions.
- Entry points: double-tap on the live RX image (RxImagePaneView area in MainWindow.axaml),
  double-tap a Previous-Frames thumbnail, double-tap a Gallery entry (Gallery tab section of
  MainWindow.axaml / RxHistoryPaneViewModel.SelectedEntry).
- Thumbnails: wrap each item Border in a Button/gesture, command on RxImagePaneViewModel taking
  the RxHistoryEntryViewModel; drop the dead "—" callsign caption (ReceiveHistoryEntry has no
  callsign field — don't fake one).
- Image loading: reuse whatever RxHistoryPaneViewModel already uses to materialize thumbnails
  from stored PNG paths; load full-size lazily per navigation step.

Verify: manual — from all three entry points; wheel-zoom, pan, arrow-key navigation across ≥3
history entries; delete-safety after step 4 (viewer handles an entry deleted underneath it).
Headless-test the VM's navigation/zoom-state logic only (per project memory, don't trust headless
pixel round-trips).

### 4. RX history delete + manual prune (T1-4 reframed)

Scope correction baked in: the retention-cap removal was a deliberate, documented user decision
(docs/removed-features.md "RX history retention limit", 2026-08-26) — do NOT reintroduce an
automatic cap. QSO delete is parked (see Sequencing). What remains Tier 1: per-item manual delete.

Change:
- ScanlineStudio.Core.Logbook/SqliteReceiveHistoryStore.cs + its interface
  (IReceiveHistoryStore): `DeleteAsync(entryId)` — remove row and the referenced PNG file
  (file-missing is not an error). Emit a `Deleted` event mirroring the existing `Recorded` one so
  the Gallery list and RxImagePaneViewModel.PreviousFrames both drop the entry live.
- RxHistoryPaneViewModel.cs: Delete command on the selected entry, gated behind the existing
  ConfirmActionDialogView pattern; multi-select delete optional second pass.
- Guard: an entry with a note/flag/QSO-link gets a stronger confirmation message (the old
  retention code exempted those rows — same signal of user investment).
- Optional (cheap, same files): "Delete older than…" bulk action in the Gallery toolbar. Not
  required for the tier-1 bar.
- `[LoggerMessage]` logging for the delete path (mandatory per CLAUDE.md).

Verify: unit tests in tests/ScanlineStudio.Core.Logbook.Tests (delete removes row + file,
missing file tolerated, event fires, flagged-entry path). Manual: delete from Gallery, confirm
Previous-Frames strip and any open viewer update.

### 5. QSO handoff (T1-6 + T2-8)

Confirmed state: `PrefillForNewEntry(callsign, sstvModeId, startUtc, name, qth, gridSquare)`
(LogbookPaneViewModel.cs:299) already carries station identity — the findings' "received data not
flowing" is overstated there — but frequency/radio-mode are explicitly excluded
(LogbookPaneViewModel.cs:289 "no radio-state auto-fill"), and the form takes raw Hz
(FormFrequencyHz, line 100).

Change (a) — prefill: extend `PrefillForNewEntry` with `frequencyHz`/`radioMode` sourced from the
latched frame metadata (step 6), falling back to live RadioStatusViewModel values only when the
frame has none, clearly editable in the form. Caller: RxImagePaneViewModel's LogQso path via
MainViewModel.

Change (b) — form shape: FormFrequency displayed/edited in MHz (string-convert at the VM edge,
store stays Hz); persistent field labels instead of watermark-only (Logbook section of
MainWindow.axaml); sortable result columns and a callsign/date filter if cheap — otherwise defer
those two to a Tier-3 decision, they are not the handoff.

Change (c) — TX side: give `IMacroTextResolver` (fixed token set, see
MacrosReferenceWindowViewModel.cs) a "current contact" source: a small CurrentQsoContext in
ScanlineStudio.Application populated by the RX pane's decoded/override callsign+grid, read by
macro tokens (%c-style HIS CALL/HIS GRID) and by the TX editor's QSO-FILL row. This is the piece
that makes "reply card populated with HIS CALL" real.

Verify: unit tests — prefill carries frequency/mode; MHz round-trip conversion; macro resolver
returns current-contact values and blanks when none. Manual: decode a frame (WAV playback), Log
QSO → form shows callsign/grid/freq/mode; TX editor QSO-FILL shows the same station.

### 6. Latch per-frame metadata (T2-4) — **flag: decode-path-adjacent state**

RxImagePaneViewModel's per-reception reset block (lines ~1190-1240) has a documented history of
cross-station leak bugs; the frequency shown for an old frame drifting with live VFO is real (code
comments call it "display staleness").

Change: at reception completion, snapshot frequency/radio-mode/SSTV-mode/UTC into the
ReceiveHistoryEntry (SqliteReceiveHistoryStore schema addition + migration) and into the RX
details card's own latched properties; details card binds to the latch, never live
RadioStatusViewModel, once a frame is complete. Unknown stays "—".

**This touches decode-driven per-reception state in RxImagePaneViewModel — per CLAUDE.md §7 this
is NOT a casual UI change: full auditor plan-review + code-review cadence.**

Verify: regression test in the pattern of the existing reset-leak tests (station A frame, retune,
station B frame — A's stored frequency unchanged); manual retune-after-decode check.

### 7. Options Apply/Connect loop (T2-3, corrected)

Correction: Test buttons already validate the CURRENT form values (TestRigctldConnectionAsync,
OptionsWindowViewModel.cs:988 — live host/port properties). Only Connect uses persisted settings
(`ToggleRadioConnectionAsync`, line ~812, documented contract "Save first"), and Save closes the
dialog (`RequestClose` at line 2574). So the fix is narrower than the finding implies:

Change:
- Add an Apply button: calls the existing `SaveCoreAsync()` (already split out at line 2585
  precisely so a save can happen without closing) without `RequestClose`. OK = current Save
  behavior. OptionsWindowView.axaml button row + en.json strings.
- Connect: after Apply exists, either leave Connect's persisted-settings contract (now one click
  away, no reopen) or make Connect implicitly run `SaveCoreAsync()` first when dirty — prefer the
  implicit-apply with the tooltip updated, it kills the loop entirely.
- Save failure surfacing: SaveCoreAsync failure currently signals only by not closing; surface
  the exception message in a visible error TextBlock near the button row.

Verify: manual — change rigctld port, click Connect without touching Save, connection uses the
new port; Apply leaves dialog open with values persisted (reopen to confirm); force a save
failure (read-only settings file) and confirm a visible message. Unit test SaveCore failure path
surfacing if practical.

### 8. Sound-file ID validation (T2-2)

Confirmed: BrowseSoundFileAsync only sets the path (OptionsWindowViewModel.cs:1455); picker offers
All-files (FilePickerService.cs:305 `FilePickerFileTypes.All`); parse/size failures surface only
inside TX (SstvSessionService.cs `TryResolveSoundFileSamples`, ~2902+), silently skipping the ID.

Change:
- Extract the validation core (exists/size-cap/`MmvSoundFile.ParseHeader`) into a shared method
  reachable from the Options VM (Application-layer service method, since UI must not call
  Core.Sstv directly — expose e.g. `ISstvSessionService.ValidateStationIdSoundFile(path)`
  returning ok/duration or a failure reason).
- OptionsWindowViewModel: validate on browse-select, on manual path edit (debounced or on
  lost-focus), and in `SaveCoreAsync` when sound-file mode is enabled; show result inline
  (duration on success, reason on failure). Block Save only on enabled-but-invalid, with the
  message saying why.
- FilePickerService.cs: drop `FilePickerFileTypes.All` from the .mmv picker (keep typed-path
  entry as the escape hatch); en.json help text already correctly describes .mmv — extend with
  the size/format constraint.
- TX-time failure (file deleted after save): keep the existing skip, but surface it — reuse the
  TX error/status surface TxControlsPaneViewModel already has, so an enabled-ID-not-played is
  visible, not silent.

Verify: unit tests against the extracted validator (missing, oversized, garbage header, valid
sample file); manual — pick a .wav renamed .mmv, see inline failure; valid file shows duration.

### 9. Labels, empty scaffolding, discoverability (T2-9, T2-10, T2-11, T2-7)

All string edits in assets/locale/en.json unless noted. Confirmed stale/current as listed:

- `Options.Tx.Callsign.Help` (en.json:611): delete "CW-ID still doesn't exist yet" — CW ID is
  implemented (Options.Identification.Cw* rows, StationIdSettings).
- `Options.Advanced.Caption` (:733): "Most of these are not wired" — reword to describe only the
  rows still actually dead (verify each against OptionsWindowViewModel before writing the text).
- `Panes.TxControls.VoxToneLabel` (:302): tooltip already corrected 2026-08-28; rename the
  visible label itself to "Leader tone" and drop the VOX word (the separate legacy VOX feature
  was removed by user decision — docs/removed-features.md).
- `MainWindow.Menu.Tools.RedecodeFromWav` (:14) → "Decode WAV file…"; `Panes.RxImage.
  RedecodeAction` (:86) likewise, until per-frame WAV attachment exists (Tier 3).
- `MainWindow.Menu.Tools.ExportSessionLog` (:15) → "Export logbook as ADIF…".
- Save/archive naming (T2-7): `Panes.RxImage.SaveFrame`/`MainWindow.Menu.File.SaveFrameAs` →
  export-flavored wording ("Export received frame…"), since every frame is already auto-archived;
  add the archive folder to the Gallery header or an "Open storage folder" action
  (RxHistoryPaneViewModel + IAppLocationsService already knows the path).
- Decode Activity (T2-10): the card is header-only by design comment (MainWindow.axaml 762-809,
  "no ItemsSource"). Collapse it: remove the card (smallest honest change) or move it behind a
  View-menu toggle default-off. Do NOT invent a row source now.
- Right-click discoverability (T2-11): tooltips on RX/TX quick-mode buttons ("Right-click to
  reassign") — QuickModeGridViewModels.cs / the quick-mode button templates; plus a context-menu
  duplicate of the action for keyboard access.

Verify: build (locale key changes ripple), manual pass over each renamed label, screenshot the
Receive tab confirming Decode Activity is gone/hidden.

### 10. Persistent RX mode lock (T2-5) — **flag: decode-path**

Confirmed missing: RxImagePaneViewModel.cs:288 "no PERSISTENT lock feature exists" (the disabled
"Locked" segment still ships as decoration — either wire it or remove it as part of this item).

Change: Auto/Locked policy on the RX controls where quick modes live: Locked pins the decoder to
the chosen mode across receptions (survives end-of-frame reset) until unlocked; state visibly
sticky on the segmented control. Plumb through ISstvSessionService to the decoder's mode
selection.

**This changes decoder mode-selection behavior across receptions — decode-path state. Full
auditor plan-review + code-review cadence per CLAUDE.md §7, and check legacy YONIQ's fixed-mode
RX behavior before designing (protocol behavior, not UI, is legacy-ground-truth).**

Verify: service-level tests — locked mode ignores VIS for a different mode, unlock restores auto;
manual WAV decode of a mode different from the locked one.

### 11. Frequency entry / honest header (T2-1)

Confirmed: 40px readout is display-only (RadioHeaderView.axaml:64); kicker hardcodes "VFO A"
(RadioStatusViewModel VfoKickerDisplay, en.json:52-53); Favourites presets already apply
freq+mode on click — so a set-frequency path exists, just not general entry.

Change (choose at implementation time, smallest first): click on the readout opens an inline MHz
entry (TextBox swap-in, Enter applies via the same service call ApplyPresetCommand uses, Esc
cancels, validation vs. plausible HF/VHF range); drop "VFO A" from the kicker unless the CAT
layer actually reports the active VFO (it doesn't today — don't claim it).

Verify: manual with rigctld dummy rig — type a frequency, Enter, rig moves; invalid entry
rejected inline; kicker no longer names a VFO.

### 12. Auto-save RX audio (new) — **flag: decode-path-adjacent state**

Not new end-to-end: a manual record path already exists — `ISstvSessionService.
StartRecordingAsync`/`StopRecordingAsync` (ISstvSessionService.cs:453-472), tapped pre-filter to
match legacy `Sound.cpp:334`, implemented in SstvSessionService.cs:2224-2335 with a
`RxImagePaneViewModel.ToggleRecordingAsync` Record/Stop button and `WavFile.Write` (whole-buffer,
no streaming writer). What's new: an always-on bounded buffer, a settings flag, per-frame
association, and retention.

Change (a) — setting: **Options › General › Storage**, alongside the existing RX images folder row
(OptionsWindowViewModel.cs:2241-2287, same Browse/Apply pattern). Add `bool?
AutoSaveAudioEnabled`/`string? AudioDirectory` to `ReceiveHistorySettings`
(ScanlineStudio.Core.Logbook) — **nullable bool, not a defaulted `bool`**, matching the
System.Text.Json init-only-default trap already documented at AudioDeviceSettings.cs:49-55; `null`
reads as off. Expose via `IReceiveHistoryStore` get/set methods (UI must not reference
Core.Logbook types directly, per the existing `SetImagesDirectoryAsync` layering comment).

Change (b)/(c) — capture and correlation: **superseded by
[docs/plans/step12-auto-save-rx-audio-plan.md](docs/plans/step12-auto-save-rx-audio-plan.md) — read
that doc, not this paragraph, before implementing.** The original assumption here (`Saved` always
fires before `Recorded` for the same frame, correlate on that ordering) turned out false across 4
rounds of plan-review with two independent reviewers: `Saved` routinely fires BEFORE its own
reception's audio slice even closes, abandoned images never raise `Saved` at all, and a manual
"Save frame as…" raises the same event with no way to tell it apart. The plan doc's design instead
assigns a `long ReceptionSequence` identity once per reception (on `ISstvDecoder`, incremented
before event fan-out) and keys a `Recorded`+`AudioSliceReady` join dictionary on that identity —
`Saved` is not part of the correlator at all. `TrySaveLastReceptionAudioAsync(path)` is superseded
by `TrySaveReceptionAudioAsync(receptionId, path)` over a small retained set of scratch files, not
a single "last" file. This also resolves whether a stronger co-channel signal interrupting a
reception is handled (yes — verified against `AnalogFmSstvDecoder`'s actual interference-handling
code, see the plan doc's "Interference/shadowing" section).

Change (d) — retention and UI: audio is much larger than PNGs, and the RX-history retention cap
was deliberately removed (docs/removed-features.md) — do not reintroduce an automatic cap here.
Step 4's per-entry delete removes the linked WAV alongside the PNG (missing file tolerated); its
optional bulk "delete older than…" counts audio too. Show audio bytes in the Gallery storage
figure. Gallery/RX details gets "Decode this frame's audio" (reuses `DecodeFromFileAsync`) and
"Open audio file location" when `AudioFilePath` is set. Resolves T2-9's Re-decode/Decode-WAV
question as two actions, not one rename: keep "Decode WAV file…" for the arbitrary-file entry
point (step 9's rename stands regardless of this item), add a separate "Re-decode this frame"
enabled only when the entry has attached audio.

**This adds a 5th cross-thread `SamplesCaptured` fan-out target on the audio drain thread and a
new identity-keyed correlation — decode-adjacent per-reception state on a hot path. Full auditor
plan-review + code-review cadence per CLAUDE.md §7, not a lighter UI pass — already run: 4 rounds
of plan-review across two independent reviewers, closed. See the plan doc for the full Verification
list (superseded the bullet list that used to be here); it's substantially longer than a typical
item's because of how many real cross-attach bugs earlier drafts of this design produced.**

**Pre-roll is settled, not a "before building" open item anymore:** `AudioPreRollMs = 10_000` (10 s),
covering the measured ~8.8 s AVT worst case — derivation in the plan doc's "Pre-roll and capacity"
section.

### 13. Scanline template bundle export/import (T1-7, half A)

Change: a single-file, versioned `.sstemplate` bundle = the existing per-template folder, zipped.
`TemplateStore` already writes exactly the right shape (TemplateStore.cs:95-99: `template.json`
with relative asset paths + `assets/` + `thumbnail.png`) — export is "zip the folder," import is
"unzip into a freshly minted id."

- `PersistedTemplateElement.cs:107`: add `int SchemaVersion` to `TemplateManifest` (absent/0 in an
  existing `template.json` = version 1 — not a non-nullable field with an initializer; same
  System.Text.Json init-only-default trap as AudioDeviceSettings.cs:49-55).
- `ITemplateStore`/`TemplateStore.cs`: `ExportAsync(templateId, destinationZipPath, ct)` and
  `Task<string> ImportAsync(sourceZipPath, ct)` returning the newly minted templateId. Import
  always re-mints via the existing `CreateTemplateId(name)` and never trusts the archive's own
  id/folder name (a collision would otherwise silently overwrite an existing template).
- Zip-entry guard: reuse the asset-filename check at TemplateStore.cs:67 per `ZipArchiveEntry.
  FullName` — reject absolute/`..`/nested-beyond-`assets/` entries, cap total uncompressed size
  (zip-bomb guard). Reject an unknown `SchemaVersion` greater than current with a clear message
  rather than partially deserializing. `System.IO.Compression` only — no new package, no
  `System.Drawing`/P-Invoke.
- `IFilePickerService`/`FilePickerService.cs`: `PickOpenTemplateBundleAsync()`/
  `PickSaveTemplateBundleAsync(suggestedFileName)` typed to `.sstemplate` — no `FilePickerFileTypes.
  All` (the same mistake step 8 fixes for `.mmv`).
- `TxImageEditorPaneViewModel.cs`: Export/Import commands beside the existing
  `SaveTemplateCommand`/`LoadTemplateAsync` (lines 1630, 1777), surfaced in the template library
  panel; import refreshes the library list. `en.json`: button/tooltip/status/error strings.
  `[LoggerMessage]` logging on both paths (file I/O).

Verify: unit tests in tests/ScanlineStudio.Application.Tests — round-trip save→export→delete→
import reproduces an identical manifest and identical asset bytes; a bundle whose `template.json`
names `../../evil.png` is rejected; `SchemaVersion` 999 is rejected distinctly; a truncated zip
fails without leaving a half-written template folder (mirror `SaveAsync`'s cleanup-on-failure at
TxImageEditorPaneViewModel.cs:1713). Manual: export, import on a second profile directory, confirm
thumbnail and fonts resolve and the unavailable-font warning still fires where expected.

### 14. Legacy `.mtm` template import (T1-7, half B)

`.mtm` is import-only. spec/15's Decisions and Deferred sections already spec a read-only legacy
importer plus the separate native bundle above — there is no `.mtm` export in the spec, and none
is recommended: Scanline's element model has no group/OLE/line equivalent and explicitly excludes
perspective/vertical/gradient text (spec/15 Non-goals), so a `.mtm` writer would be lossy in the
direction where fidelity would matter most. "Both directions" is satisfied by `.mtm` import plus
full `.sstemplate` export/import (step 13).

Change:
- Spec correction (doc, not code): update spec/15-template-designer.md:267-282 — the format is
  fully specified by `yoniq-old/YONIQ-main/Draw.cpp`'s `CDrawGroup::SaveToStream`/
  `LoadFromStream` (lines 5344-5385 file-is-one-group, 4963-5004 container loop with `int32`
  command dispatch against `CM_*` in Draw.h:80-88, 408-454 base record including a
  `0x55aa0000`-tagged optional `m_BoxStyle` block that must be disambiguated exactly as legacy
  does, 501-524 length-prefixed strings with no encoding tag, 456-486 an embedded VCL `TBitmap`
  stream). Drop the "needs a reverse-engineering pass" gate; keep "write the layout into the spec"
  as the remaining doc task.
- `ScanlineStudio.Host/Program.cs`: register `System.Text.CodePagesEncodingProvider.Instance` at
  startup — confirmed absent from `src/` by grep, and CLAUDE.md §4 requires it for any legacy text
  read; `.mtm` strings decode as CP932, never UTF-8/`Encoding.Default`.
- New `ScanlineStudio.Application/LegacyMtmReader.cs`: byte-level little-endian reader implementing
  each element subtype's `m_Ver`-gated branch as written in `Draw.cpp` (`CDrawText`, `CDrawPic`,
  etc.) — an unrecognized version or command fails the file cleanly rather than guessing. Legacy
  `TColor` is `0x00BBGGRR` (convert, don't copy).
- Element mapping: `CM_TEXT`→`TemplateTextElement`, `CM_PIC`→`TemplateImageElement`, `CM_BOX`/
  `CM_BOXS`→`TemplateBoxElement`, `CM_GROUP`→flatten children with the group's translate applied.
  `CM_OLE`/`CM_LIB`/`CM_LINE`/`CM_TITLE` and unsupported text effects (perspective, vertical,
  gradient brush) are reported per-element as unsupported — never silently dropped, never blocking
  the rest of the import.
- `CDrawPic` bitmaps: decode the embedded `TBitmap` blob via ImageSharp's BMP decoder, write as a
  normal asset through `ITemplateStore`. **Spike first**: VCL's `TBitmap::SaveToStream` can emit a
  16bpp/DDB-flavored variant ImageSharp may reject (`Draw.cpp:3773` has its own 16bpp special
  case) — confirm against a real `def1.mtm` before committing to this sub-item's shape; if
  ImageSharp rejects it, treat as an unsupported-element report, never a crash.
- Fonts: family name (CP932) + charset byte + signed size (negative = `Font.Height`, positive =
  `Font.Size`, Draw.cpp:3204-3212) → px, then run through the existing unavailable-font warning
  path (TxImageEditorPaneViewModel.cs:2243-2262).
- Import lands as a normal saved template via `ITemplateStore.SaveAsync` — never a second
  persistence path. UI: reuse step 13's Import entry point with a second file type (`.mtm`; decide
  explicitly whether `.mti`, the single-item variant, is in scope too — see Tier 3), plus an
  import-report dialog listing dropped/unsupported elements.
- License-audit gate: using legacy `.mtm` files (e.g. `def1-5.mtm`, `Stock/*.mtm`) as committed
  test fixtures needs a LICENSES.md entry per CLAUDE.md §5; if not acceptable, generate fixtures
  locally and keep them uncommitted, gating those tests on the file being present.
- `[LoggerMessage]` logging for parse failures and per-element drops.

Verify: unit tests over sampled legacy files (`def1-5.mtm`, `t1-5.mtm`, `Stock/*.mtm`,
`Current.mtm`) — each parses to completion or fails with a specific named reason; known embedded
strings round-trip byte-correct through the CP932 path; a truncated file and a file containing a
`CM_OLE` element both produce a report, not an exception, and not a partially written template
folder; one fixture exercising each side of the `0x55aa0000` branch. Manual: import a `.mtm`,
compare the rendered TX canvas against a legacy screenshot of the same template.

## Tier 3 — scope decisions for the user (accept / defer / reject, one line each)

- QSO delete + duplicate detection + QSL flags: parked 2026-08-15, reviewed plan already exists
  (`~/.claude/plans/logbook-deltas.md`) — resume or keep parked.
- Camera/webcam TX source: accept/defer.
- Copy / open-externally / print for received images: copy+open cheap to bundle into the step-3
  viewer; print separate — decide.
- Legacy `.mti` (single template item) import alongside `.mtm` in step 14: **moot for now** — step
  14 itself deferred 2026-08-29 (see the Sequencing section's own note); would accept alongside
  `.mtm` if/when step 14 is picked back up (same parser, confirmed against `Draw.cpp`/`Main.cpp`).
- Legacy `.mtm` **export** (write, not read): recommend reject — lossy against the shipped
  element model, not specced, no stated user need.
- Richer Gallery filters/sort/bulk actions: accept/defer.
- External logger / live current-QSO integration beyond ADIF UDP: memory says logbook stays
  minimal-footprint — presumably reject, confirm.
- OmniRig client backend: spec/03 lists it, en.json calls it speculative — decide platform scope.
- Waterfall/spectrum palette customization: accept/defer.
- QSSTV digital SSTV / DRM / hybrid FTP / repeater: record explicit reject/defer in
  docs/removed-features.md if rejected.

## DSP/concurrency call-outs (per task instruction)

- Step 6 (metadata latch), step 10 (RX mode lock), and step 12 (auto-save audio) touch decode-path
  or decode-adjacent per-reception state — auditor plan-review + code-review cadence, not lighter
  UI review. Step 12 specifically adds a 5th cross-thread `SamplesCaptured` fan-out target on the
  audio drain thread plus a new `Saved`/`Recorded` correlation — a hot-path handler, not a settings
  toggle, despite the user-facing surface being one checkbox.
- Step 4's store `Deleted` event and step 2's cross-VM transmit event are new cross-thread event
  paths — state the scheduler/dispatch rule explicitly (CLAUDE.md §4 concurrency rule); events
  raised from store/background contexts must marshal via Dispatcher like `Recorded` already does.
- Steps 13-14 (template bundle/`.mtm` import) are file I/O and parsing, not decode-path — lighter
  UI-style review is fine, but step 14's parser needs golden-fixture tests against real `.mtm`
  files, not a review-only pass, given the format's version-gated branching.
- Everything else is view/VM/locale-level and can use the lighter single code-review pass, batched.
