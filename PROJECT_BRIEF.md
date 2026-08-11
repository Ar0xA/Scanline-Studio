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

**Current wiring totals** (`spec/16-gui-wiring-survey.md`, ~279 controls tracked): **~122 REAL,
~105 STUB, ~48 FAKE-LIVE, ~4 PARTIAL**. Densest remaining gaps: Receive tab's Sync&Slant/Input-
chain/Signal-quality cards (SNR/squelch/notch/noise-floor — no live audio-chain measurement exists
in `Core.Audio`/`Core.Sstv` for most of these, real new DSP work not just wiring); Options window's
Decode/Identification/Advanced tabs (~55 controls, 100% stub); Transmit tab's Queue/TX-log/
Recently-sent cards (100% stub, no such features exist); TX image editor's canvas-overlay safe-
area/callsign/report-plate text (FAKE-LIVE — reads as real burned-in TX content, arguably the most
deceptive placeholder in the app).

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
