# Project brief (resume point)

Scratch file for resuming after `/clear` — not a spec doc, delete or ignore once stale. Pruned
2026-08-08 (was 2978 lines/46 entries; everything but the active entry below was either already in
a detailed commit message or already migrated into `spec/14-roadmap.md`/`CLAUDE.md` — see git
history for this file if older context is ever needed).

## Resume here (2026-08-11, latest, ACTIVE) — pixel-perfect UI redesign, Phase 4 in progress

**Pruned 2026-08-11** (this section had grown to ~460 lines across one very long session; full
detail for anything marked SHIPPED below lives in its own commit message — `git log --oneline` and
`git show <hash>` to recover it, not this file).

**What this is**: full pixel-perfect port of the app's UI from the old "Aesthetic Directive" look
(`spec/09-ui.md`) to a new "Industry" wireframe design system (`mockups/guidance/`: steel-blue
accent, Barlow/Barlow Condensed, square corners, hairline borders, blueprint corner marks). Full
plan: `~/.claude/plans/transient-jumping-bentley.md` — 8 phases (0 fonts/tokens, 1 atoms, 2 chrome,
3 Receive tab, 4 Transmit tab, 5 Gallery tab, 6 Logbook+Options extrapolated, 7 cleanup).

**User's process directions, still governing all work in this subject:**
- Priority order: **containment (fits, nothing clipped) > closeness to mockup > pixel-perfect**
  (concessions accepted where Avalonia's layout model genuinely can't match CSS).
- Try every finding, cap at **3 try/review/adjust cycles**, then log what's left rather than
  grinding on it.
- Move fast: lighter self-checks (build+test+screenshot per card) instead of a full `design-fidelity`
  agent round every time; reserve that agent for spot-checks when something looks off or after a
  whole multi-card section, not per-card.
- "Rework it into the new design, do this for each new card that already has an old design" — i.e.
  keep executing the plan's remaining phases (4→7), not just polish what's already ported.

### Status: Phases 0-4 SHIPPED. Phase 5 (Gallery tab) NOT STARTED.

**Phase 0 (fonts/tokens), Phase 1 (atoms), Phase 2 (chrome: menu/header row/tab strip/status bar):
SHIPPED, verified, committed** — see git log before this session if detail is ever needed; nothing
outstanding.

**Phase 3 (Receive tab): FULLY SHIPPED**, all 4 sections (header row, left/centre/right columns),
each independently build+test+screenshot verified, several real bugs found and fixed along the way
(see "Real bugs found this session" below — most were discovered here, since this was the first
section where many Phase-1 atoms got a real consumer for the first time).

**Phase 4 (Transmit tab): IN PROGRESS.**
- Left column (`TxControlsPaneView.axaml`: TX mode/Identification/Output/Stock/Outgoing metadata)
  — **SHIPPED**, commit `ba0c200`. Transmit/Stop toggle button deliberately left untouched:
  `ScanlineStudioTxToggleButtonTheme` was flagged in the original plan's own round-1 auditor review
  as a landmine (a `StaticResource` on an old-design key that would crash the pane at XAML load if
  the old style files were ever deleted while still referenced) — needs a real dedicated
  `IndustryTxToggleTheme` design pass, not a drive-by re-skin.
- Right column (`MainWindow.axaml`'s Transmit tab, `Grid.Column="2"`: Queue/Mode-timing-reference/
  TX-log/Recently-sent) — **SHIPPED**, commit `57acc42`, build+test (173/173 UI, 7/7 Localization)
  +real-window screenshot self-check. Same `Grid RowDefinitions="Auto,Auto,Auto,*"`/no-outer-
  ScrollViewer structure as the Receive tab's right column. Real bug caught during the screenshot
  check (not by build/test): Mode timing reference renders ~40 real `SstvModeDefinition` rows (mock2
  only shows 5 as a static sample), and since that card sits in an EARLY "Auto" grid row (not the
  column's own last/`"*"` row), the unbounded real list consumed the whole column's height and
  pushed TX log/Recently sent off-screen with **no way to reach them** — fixed with a bounded
  `ScrollViewer MaxHeight="150"` around just that card's `ItemsControl`. Queue/TX-log/Recently-sent
  are placeholder cards (no queueing/TX-logging/send-history feature exists anywhere in this app),
  so their row set literally mirrors mock2's own text, same precedent as Queue's pre-existing
  "Between frames" row; added 2 new locale keys (`Panes.TxLog.TxTimeToday`/`DutyCycle`) for 2 rows
  mock2 has that the old design never did. Also noticed, NOT fixed (pre-existing, unrelated,
  off-scope): the app window at this dev box's current size/position leaves a band of desktop
  wallpaper visible below the status bar in screenshots — same in both the pre- and post-fix
  screenshot, not something this pass's markup change caused.
- Centre column (`TxImageEditorPaneView.axaml`, the Editor group box) — **SHIPPED**. Was flagged in
  the original plan as "the most complex single card" (toolbar, image canvas, 6 adjustment sliders,
  186px side panel of 3 nested sub-cards) — mostly a big-surface-area re-skin, not a new design
  problem, since the old markup already tracked mock2's own layout closely. One genuinely new atom:
  `IndustryMiniRadioTheme` (Atoms.axaml) — the Move/Crop/Scale/.../Pick tool strip is a REAL
  RadioButton `GroupName` exclusive-select group in the old design (not inert chips), and
  `ToggleButton` has no Avalonia `GroupName` equivalent, so `IndustryMiniToggleTheme` couldn't
  substitute. Toolbar uses one `WrapPanel` (not the old design's non-wrapping single row) so a
  narrower runtime column degrades to extra lines instead of clipping. Header merges the old split
  Title+right-aligned-Caption TextBlocks into one string (new `Panes.TxImageEditor.CardHeader` key)
  to match the title-notch's single-ContentPresenter constraint, same precedent as every other
  ported card. New `Panes.TxImageEditor.TextStyleHeader` key: the old design's "Text style" card had
  no title at all (a pre-existing gap vs. mock2's own titled card), added while re-skinning.
  Safe-area guide is a dashed `Rectangle` (`StrokeDashArray`), not a solid-line concession — Border
  has no dash support in Avalonia but Rectangle does, confirmed by real-window render.

  **Real pre-existing bug caught via real-window verification (not build/test — this predates the
  port, present verbatim in the old markup):** `RenderTransform="translate(-5,-5)"` on the crop-
  resize-handle Border is invalid Avalonia transform syntax (needs unit suffix, e.g.
  `translate(-5px, -5px)`) — throws a `FormatException` at View construction. Since this exception
  fires inside an `async Task` `[RelayCommand]` (`SelectStockImageAsync`/`SelectImageAsync` →
  `OpenEditorForSourceAsync`), it was silently swallowed with no visible error and no UI change,
  which is almost certainly why the Transmit-tab editor has been unopenable via Stock/Browse this
  whole time (`_isEditorOpen` never got reset either, though a fresh app launch clears that). Fixed
  the transform syntax; confirmed by opening a real stock image end-to-end in the running Host app
  post-fix — editor now opens and renders correctly. Discovered via a throwaway real-window harness
  (`AppBuilder.Configure<HarnessApp>()` subclassing `App`, bypassing `MainViewModel`/DI, real
  `TransmitImagePreparer`+`MacroTextResolver`+a synthetic checkerboard `ArrayImageSource`) built
  specifically because clicking the real app's Stock thumbnail via synthetic X11 input produced no
  visible effect and no error — this harness isolated View-construction from the click/picker flow
  entirely and pinpointed the exact exception. Not saved anywhere (scratchpad-only, deleted after
  use) — rebuild from this description if the same isolation technique is needed again.

  Verified: full solution build clean, `UI.Tests` 173/173, real-window screenshot in both the
  throwaway harness AND the actual running Host app (post-fix, via a real Stock image click-through).

  **Phase 4 design-fidelity spot-check: run, findings fixed.** Per the standing rule (now saved as
  a memory: run this check automatically at the end of a whole phase, before starting the next one
  — don't wait to be asked), a `design-fidelity` agent pass covered all 3 Transmit-tab columns
  against mock2. Found 7 visible + 5 minor + 3 nit mismatches, real-window-verified (live screenshots,
  not static XAML reading). All fixed this pass except the 2 nits explicitly logged below.
  **Visible, all fixed:** (1) the Transmit tab's own outer Grid never got the 258,*,330→236,*,312 /
  `IndustrySpacingGutter` / `9,11,9,8`-margin token update the Receive tab already has — copied
  verbatim from Receive. (2) Editor card's title notch was clipped (no headroom margin) — needed
  BOTH the outer-Grid margin fix above AND a dedicated `Margin="0,6,0,0"` on the card's own
  `HeaderedContentControl` (unlike Receive's Incoming-frame blueprint card, this one has no sibling
  row above it for free RowSpacing clearance) — confirmed via a direct pixel-crop comparison against
  the already-crisp "MODE TIMING REFERENCE" title before/after. (3) Editor's side panel rendered
  ~608 DIP wide instead of mock2's 186 — a `Grid ColumnDefinitions="2*,Auto"` Auto column measures
  to the WrapPanel's own unconstrained desired width; fixed to a literal `"*,186"`. (4) 8
  `IndustryBtn22` call sites were missing `Padding="7,0"`, clipping labels — same bug class the
  Receive tab already fixed once (RxFrameMeta). (5) Phase 4's new card titles weren't uppercased,
  inconsistent with the Receive tab's own convention — 8 locale keys uppercased. (6) Recently-sent
  thumbnails stretched to fill the column height (ScrollViewer's default Stretch content alignment)
  — fixed with `VerticalAlignment="Top"` on the UniformGrid + a fixed (not Min) `Height="70"` hatch
  panel. (7) Output card's SWR-cutoff row overflowed its column — a non-wrapping horizontal
  StackPanel replaced with a WrapPanel.
  **Minor, all fixed:** Mode-timing table's `ScrollViewer MaxHeight` (150→145) landed mid-row,
  clipping the 9th row in half — re-verified the new value lands on a clean row boundary via
  pixel-crop. Table alignment/units brought in line with the Receive tab's own already-verified
  Decode-activity table (left-aligned, not right — mock2's own CSS is left-aligned and Phase 3
  already matches that; the Mode-timing table was the one inconsistent outlier), Frame column
  gained its "s" unit suffix. Added 4 rows the port had dropped vs. mock2 (TX mode card's
  Selected/VIS header — Selected is a REAL binding, `SelectedMode.DisplayName`, not literal; Output
  card's Tune drive; Editor's Text-style Plate row) plus reordered the TX mode card's ComboBox to
  after the quick-mode grid, matching mock2. Stock-picker thumbnail button re-skinned off default
  Fluent chrome (flat hairline border, no rounding) — the only remaining non-Industry control in
  the left column.
  **Nits: 2 fixed (numbered Queue rows, "Save"→"Save current"), 1 logged not fixed** — Output
  card's Drive value renders as a bare `100` where mock2 shows `−6.0 dBFS`; not a formatting nit,
  the app's real quantity is a 0–100 percent (`RadioStatus.TxVolumePercent`) while mock2's is a
  dBFS level — different units entirely, mislabeling one as the other would be wrong, and no real
  dBFS conversion exists to compute the correct value. Needs a real product decision, not a
  drive-by fix; logged for later, not chased further this pass.

  Re-verified after fixes: full solution build clean, `UI.Tests` 173/173,
  `Core.Localization.Tests` 7/7, every fix individually confirmed via real-window pixel-crop
  screenshots (not just "looks fine at a glance" — several fixes needed a second iteration after
  the first attempt still showed the issue at zoom, e.g. the title-notch clipping).
- After Phase 4: **Phase 5 (Gallery tab)**, **Phase 6 (Logbook tab + Options window** — no direct
  mockup, extrapolate from the atom set, own lighter design-review pass since it's new composition
  not a pixel port), **Phase 7 (cleanup**: delete dead old-design resources once nothing references
  them, update `spec/09-ui.md`).

### Real bugs found + fixed this session (not just cosmetic — worth knowing about for future atom work)

1. **`IndustryStepperTheme`'s decrement glyph never rendered** (first real consumer of the atom,
   built in Phase 1 but never actually rendered until Phase 3's Spectrum & waterfall card). Root
   cause: the `"−"` Path geometry (`M 0 0 L 8 0`) is a horizontal line with a **zero-height bounding
   box**, degenerate for the default `Stretch="Uniform"` scale computation. `Stretch="None"` alone
   did NOT fix it (tried first, confirmed empirically) — needed an explicit non-degenerate
   `Width="8" Height="8"` box with the line recentered to local y=4. Applied to both glyphs for
   symmetry. **Every stepper in the app had an invisible/unusable "−" button before this fix.**
2. **`PART_DecreaseButton`/`PART_IncreaseButton` RepeatButtons inside `IndustrySliderTheme`'s Track
   template weren't inheriting `Stretch` `HorizontalAlignment`**, so Avalonia's `Track` control's own
   correctly-computed proportional Arrange width was discarded in favor of their empty-content
   `DesiredSize` (0) — the TX-level slider's fill never tracked its own bound value at all. Root-
   caused via a runtime `Bounds` dump + decompiling `Avalonia.Controls.Primitives.Track` (confirmed
   the percent MATH itself was correct; the bug was purely in the RepeatButtons' own alignment).
   Fixed with explicit `HorizontalAlignment="Stretch"` on both.
3. **The base Fluent `Thumb` ControlTheme doesn't paint a bare `Background` property setter** — the
   TX slider's thumb rendered fully invisible. Fixed with an explicit inline `Thumb.Template`
   (`<Border Background="{TemplateBinding Background}"/>`).
4. **`StackPanel.IndustryRows` never set a `Spacing`** — CSS's `.gb{gap:3px}` applies to `.r2` rows
   too, not just non-row card children; every row in every card using this atom sat ~2 logical px
   tighter than the mockup. Fixed with `Spacing="3"`.
5. **Same-class bug, 4 separate places this session**: a literal pixel `Width`/`Margin` on an
   `IndustryMeterFill`/`IndustryMeterMarker` Border only "matches" the mockup's stated percentage by
   coincidence at one specific container width. Fixed each time with nested star-column Grids
   (`ColumnDefinitions="63*,37*"` etc.) instead of a literal pixel value. Check for this pattern
   (`Width="<number>"` on a meter-fill Border inside a non-fixed-width column) before assuming any
   remaining old-style meter is fine.
5b. Right column's L/R meters (Input-chain card, left column) had this same bug; fixed identically.
6. **`Border.IndustryMini > TextBlock` was a direct-child selector**, but the CAT-linked chip nests
   its 2 TextBlocks inside a `Panel` — unreachable by `>`. Changed to a descendant selector
   (`Border.IndustryMini TextBlock`, `.active`, `.accentOutline` variants too).
7. **A `DockPanel` with only ONE child ignores that child's `Dock=` value** (since `LastChildFill`
   defaults to `True` and that one child is also the *last* child) — Decode activity's table header
   row stretched to fill the whole card body instead of sitting under the column labels. Fix:
   `LastChildFill="False"`. **Worth checking for this exact shape (single-child DockPanel relying on
   `Dock=`) anywhere else old code gets touched.**
8. **A `Margin` fix for ScrollViewer-clips-the-first-card's-title-notch has to live on the scrolled
   CONTENT (the root `StackPanel` inside the `ContentControl`/`UserControl`), not on the
   `ScrollViewer` element itself** — tried the ScrollViewer-level Margin first for the Transmit tab's
   left column, it visibly did NOT fix the clipping; moving the same `Margin="0,6,0,0"` onto
   `TxControlsPaneView.axaml`'s own root `StackPanel` (matching the Receive tab's left column's
   already-working pattern exactly) did. **Any new ScrollViewer-wrapped tab-column content needs
   this same margin placement, not a ScrollViewer-level one.**

### Established atoms available (Atoms.axaml) — check here before inventing new markup

`IndustryGroupBoxTheme` (+ `Classes="blueprint"` for corner marks), `IndustryBlueprintBorderTheme`
(plain bordered, no title notch), `IndustryMiniButtonTheme`/`IndustryMiniToggleTheme` (chip
Button/ToggleButton forms — **use `IndustryMiniToggleTheme` for any checkbox-like toggle with no
mockup equivalent**, established pattern for Peak-hold/SWR-cutoff), `IndustryStepperTheme` (+
`IndustrySpinnerTheme` internally), `IndustrySliderTheme` (+ `.h6`/`.h8` height variants, default
= 4px), `IndustrySegTheme`, `IndustryDisclosureToggleTheme`, `IndustryPlot` (new this session — dark
plot-canvas frame), `ProgressBar.Industry` (new this session — re-skins Fluent's own percent-sizing
logic, doesn't replace it), `IndustryTableHeader`/`HeaderRule`/`Cell`/`RowRule` (plain-Grid table,
not `DataGrid`), `IndustryThumbnail`/`ThumbnailCaption`/`ThumbnailCall`/`ThumbnailMeta`,
`IndustryHatchPanel`, `IndustryMeter`/`MeterTrack`/`MeterFill`/`MeterMarker` (use nested star-column
Grids for the fill/marker, never a literal pixel Width — see bug #5 above), `IndustryRows`/`IndustryRow`/
`RowLabel`/`RowValue`, `IndustryKicker` (accent-mono-uppercase — **NOT** for plain form-field labels
like "Note"/"Override callsign", those are `IndustryRowLabel`, confirmed against the mockup's own
CSS which has zero text-transform/letter-spacing/color on those specific labels), `IndustryInput`
(TextBox/ComboBox/NumericUpDown). **No `IndustryCheckBox` atom exists** — the mockup itself never
defines one (every mockup toggle is either `.seg` or `.mini`), so don't build one ad hoc; if Phase 6
(Options, ~100 controls, many real checkboxes) needs one, that's the right place to design it
properly, not a drive-by add during Phase 4/5.

### Deferred / logged, not fixed — for later triage, not forgotten

- **Mockup's "Macros" card** (F1-F6 mini-chip buttons, Receive tab right column) doesn't exist
  anywhere in this app today — no backing command/feature. Building it is new scope, not a re-skin;
  needs a real product decision on what F1-F6 actually invoke first.
- **`ScanlineStudioTxToggleButtonTheme`** (Transmit/Stop button) needs its own dedicated
  `IndustryTxToggleTheme` — large, high-visibility control, deliberately not touched yet (see bug
  list item on why).
- **User raised, mid-session**: the Receive tab's left column scrolls even though the static HTML
  mockup (viewed at its own reference resolution) doesn't need to. User's own conclusion when asked:
  "at least with scrollbar the content is reachable" — i.e. **keep the ScrollViewer**, not a bug to
  fix, just a known deviation from the static mockup's own untested-at-smaller-sizes assumption.
- Centre-column review's remaining nits not chased (all logged in commit `1378087`/`5728901`/
  `dcd7433`/`25c05e7`'s own history, recoverable via `git log`): stepper button-cell width/height
  asymmetry (mockup wants 18×22 both sides, currently asymmetric), stepper internal separator height
  (10px stub vs full 22px), disabled-stepper partial grey background leak (Fluent's own disabled
  TextBox background showing through), a ~40% oversized stepper `MinWidth`, minor color-mix rounding
  nits (62% vs 60% muted text), a few DIP-level gutter/margin drifts. None block containment or
  fidelity in a way that's visible without a ruler.
- App-wide (not this session's scope, logged repeatedly across multiple reviews): page background is
  `#F0F0F0` (`Cards.axaml`'s `ScanlineStudioPanelBackgroundColor`) vs the Industry design system's own
  `#F2F2F3` — systemic token question, affects every not-yet-ported AND already-ported view equally
  (both sample the SAME literal background since neither owns it), needs its own explicit decision
  before Phase 7 cleanup, not a per-card fix.

### Process notes for whoever resumes this

- **The `--` (double-hyphen) XML-comment bug bit this session repeatedly** — plain English em-dash
  usage ("done -- confirmed") breaks Avalonia's XAML comment parser (`An XML comment cannot contain
  '--'`). Write comments without literal double-hyphens from the start (use `,`/`;`/em-dash `—`
  instead) rather than fixing it after every failed build.
- **Tab-switching without `xdotool`** (not installed, no passwordless sudo to install it): this
  sandbox has `python3-xlib` available — use `Xlib.ext.xtest.fake_input` to synthesize a real X11
  click at absolute screen coordinates (window position + tab's on-screen offset). Working snippet
  used throughout this session:
  ```python
  from Xlib import X, display
  from Xlib.ext import xtest
  import time
  d = display.Display()
  x, y = 3840 + int(104*1.15), int(176*1.15)  # window origin + tab offset, scaled
  xtest.fake_input(d, X.MotionNotify, x=x, y=y); d.sync(); time.sleep(0.2)
  xtest.fake_input(d, X.ButtonPress, 1); d.sync(); time.sleep(0.05)
  xtest.fake_input(d, X.ButtonRelease, 1); d.sync()
  ```
- App window goes on the HDMI-1 monitor at `(3840,0)`, size `1400x900`
  (`wmctrl -r "Scanline Studio" -e 0,3840,0,1400,900`) — established convention this whole session.
- Kill stale app instances before relaunching (`pkill -9 -f "ScanlineStudio.Host.dll"`) — multiple
  instances accumulated silently more than once this session from failed/retried launch commands,
  and screenshots from the wrong (stale) instance wasted real debugging time more than once.

### Verification status

Full solution build clean, `UI.Tests` 173/173, every commit this session. **Commits ahead of
`origin/master`, not pushed** — verify with the user before pushing.

**Phase 4 (Transmit tab) is now fully SHIPPED** (left/centre/right columns all ported). **Next
action on resume**: Phase 5 (Gallery tab) — not started yet, still on old `Cards.axaml` styling.

## Previously (2026-08-10) — GUI wiring survey refreshed, RX telemetry work closed

**Current status**: Must-implement backlog items 1-2 shipped/closed. Full GUI wiring survey done
(`spec/16-gui-wiring-survey.md`), **and just refreshed to current (2026-08-10, see below)**. RX
telemetry feasibility slice (`spec/17-rx-telemetry-feasibility.md`) fully shipped, batches 1-6. RX
history browser affordances shipped (batch 7). **Waterfall color/palette rendering shipped and
CLOSED** (batches 8a/8b, commits `1cc4568`/`71a7daf` — see below for detail).
No further task is currently authorized by the user — the last exchange offered OCR/QRZ lookup or
CW-ID/FSK subsystem as the next `spec/14-roadmap.md` backlog items and is awaiting a reply. **Read
this file top-to-bottom is not required to resume** — the batch entries below (8a, 8b, 7, 6...) are
kept for "what happened and why" detail; skip straight to whatever the user's next message asks for
and consult a specific batch entry only if its reasoning becomes directly relevant again.

**GUI wiring survey refresh (2026-08-10):** user asked how many UI items are now mapped to real
functionality; the survey (`spec/16-gui-wiring-survey.md`) turned out to be a stale 2026-08-09
snapshot that never got the row-by-row edit after RX-telemetry batches 1-8b shipped, despite an
inline note flagging the staleness. Re-verified every affected row against current `.axaml`/VM code
(a fork did the file-by-file work), refreshed ~28 rows (RX-telemetry batches 1-7, the macro-engine
commit, waterfall 8a/8b), and found 2 controls the original survey missed (Gallery's "Latest"
button, waterfall's Peak-hold checkbox). Auditor independently re-checked the refresh itself
(TRUSTWORTHY-WITH-FIXES): every classification held up, but it caught a **false claim** (an
"off-scope" note accusing `RxHistoryPaneViewModel` of a hardcoded-string bug that doesn't actually
exist — wrong line range, the real code correctly localizes; removed), a **math gap** (the Summary
table's counts hadn't folded in batch 8a/8b's changes at all), and several stale `RadioStatusViewModel.cs`/
`MainWindow.axaml` line citations left over from before the refresh — all fixed. Updated totals:
**~114 REAL, ~108 STUB, ~48 FAKE-LIVE, ~4 PARTIAL** (out of ~282 tracked; was ~94/~110/~71/~5 at the
original 2026-08-09 snapshot). **Committed, not yet pushed.**

User instruction governing all work in this subject: **"throughout the night keep working on the
total list using the same process flow. IF you get stumped by bugs, solution directions, ask the
auditor for help. If the auditor cant figure it out, put it the 'verify later with human' list."**
Work autonomously through `spec/14-roadmap.md`'s "## Must-implement backlog" checklist, item by
item, using the established research→plan→auditor(2 rounds)→implement→audit(2 rounds)→commit→push
flow, minimal check-ins. Escalation path: stuck on a bug → ask the auditor; auditor also can't
resolve it → log to `spec/14-roadmap.md`'s "## Verify later with human" section (added, currently
empty) rather than stalling.

**Item 1, Logbook UI pane: SHIPPED.** New 4th tab + `LogbookPaneViewModel` (search/browse, add/edit
QSO form, ADIF import/export). Plan: `~/.claude/plans/rustling-drifting-falcon.md`. New facade
method `ILogbookSessionService.UpdateQsoAsync` (no GridTracker/QRZ re-push — QRZ's upload API is
INSERT-only). Fixed a pre-existing `ExportAdifFileAsync` gap (missing `STATION_CALLSIGN`).

Two rounds of auditor plan-review caught 4 real bugs before any code was written (Mode-as-free-text
enum crash risk, `.ToUniversalTime()` date corruption, exact-match callsign-filter trap, wrong
file-picker API shape). Two rounds of POST-implementation code audit then caught 3 more real
blockers actually shipped in the first pass: (1) every success status message was set then
immediately nulled by a shared `New()` reset helper — the user got zero feedback after logging or
updating a QSO; (2) the Mode and SSTV-mode `ComboBox`es were bound via `SelectedItem` against a
different element type than the bound property (`RadioMode?`/`string?` vs. the `ItemsSource`'s
actual element type) — silently nulled the field on every selection, a real data-loss bug on edit;
(3) `StringFormat` on a TwoWay Start/End `TextBox` binding doesn't reverse on `ConvertBack`, so
edited timestamps were parsed using the machine's LOCAL offset instead of UTC — corrupting stored
instants and breaking the repository's lexicographic date-range queries. All three fixed (new
`RadioModeOption` wrapper + `SelectedValueBinding`/`SelectedValue`; new `UtcTimestampTextConverter`;
`ResetForm()` split from the `New` command so it no longer clobbers a just-set status line), plus 5
lower-severity risks (FromDate wasn't UTC-normalized the way ToDate was; re-selecting the same row
after "New" was a dead end; Export could report a stale count; Import's success message could mask
a failed follow-up refresh; a shared log line hardcoded the wrong method name). Round 2 audit:
ship-ready, only 2 accepted nits left (Export's stale-count window on a *failed* refresh — narrow,
no data loss; blank Start-field text is silently discarded rather than shown as invalid — documented
intentional). Full solution build clean, full test suite green (Application 65, UI 97, Logbook 64,
Radio 112, Audio 14+56, Sstv 653 — all passing). **Committed and pushed (`2fcb99d`).**

**Item 2, DSP decode-accuracy residuals at 11025Hz: CLOSED, stale item, no new DSP work needed.**
Research (a fork's synthetic-round-trip measurement) initially looked like it fully closed the gap,
but that alone wasn't trustworthy enough to act on directly — `spec/14-roadmap.md` has an extensive
prior "Robot-36-at-11025Hz decode-gap investigation" live log (this doc's own Piece 9-15+ history)
establishing that synthetic self-consistency can hide real legacy-parity bugs (three of the first
four adversarial-review bugs were invisible to round-trip alone), so golden-vector tests against
REAL captured legacy audio are the authoritative check for this project. Verified against those
instead: `GoldenVectorTests.cs` already passes 43/43 today, with Robot36/Robot72 tolerances
tightened to reflect real measured deltas (down from a mid-investigation high of 68.06 to ~5-17
now) — the fix already landed as a side effect of earlier DSP work, this checklist entry just never
got updated. Added one new permanent test locking this in rather than leaving it as a historical doc
note: `SstvRoundTripTests.EncodeThenDecode_ViaWavFile_RoundTripsWithinTolerance_At11025Hz`, covering
every mode the checklist item named (Robot36, Robot72, MR73, ML180/240/280/320, Martin M2, MR115) at
the real 11025Hz rate — all 9 pass comfortably (~2.3-4.9 delta vs. the 10.0 tolerance). No production
DSP code touched; full auditor DSP-audit process (CLAUDE.md §7) skipped since nothing changed for it
to review. `SstvRoundTripTests` 101/101 (was 92), full `Core.Sstv.Tests` 662/662 (was 653, +9 new),
both confirmed green. **Committed and pushed (`44c7ca7`).**

**Item 3, real Options dialogs: BLOCKED on user scoping input — do not just start building.**
Checked the actual scope before touching anything (not just trusting the checklist's one-line
framing): there is no separate `RadioSettingsDialog`/`MacroKeyEditor`/`ColorSettingsDialog`/
`LanguageSettingsDialog` class anywhere — it's ~50+ individually `IsEnabled="False"` +
`Options.NotImplemented.Help`-tooltipped controls spread across one big
`src/ScanlineStudio.UI/Views/OptionsWindowView.axaml` (colors/FFT palette, VOX, CW-ID/
identification, macros, etc.). Several of those sections overlap with OTHER items already listed
separately further down this same must-implement checklist (waterfall color/palette, CW-ID/FSK
subsystem) — building "Options dialogs" as one lump would either duplicate or conflict with that
later work. Real scoping needs an actual per-section split (which controls belong to THIS item vs.
which are really the color-palette/CW-ID items in disguise), plus an auditor UI-design plan-review
pass before any building (per this project's own "audit UI design before building" convention — UI
design choices need that pass, not just DSP ports). Surfaced this to the user rather than silently
picking a scope and diving in for potentially hours of unreviewed UI work. **User has not yet
answered which pieces to tackle first** — next session should re-ask rather than assume, or check if
an answer arrived after this brief was written.

**Subject pivot (2026-08-09): full GUI wiring survey, done and auditor-verified.** User asked
directly: is the DSP/radio core solid enough to shift focus to the GUI, finding every identifier/
parameter/button and wiring real functionality behind it? Answer given: yes, with one real
exception (CW-ID/FSK is a whole unbuilt subsystem, not just wiring — still its own separate backlog
item). User then asked for "a full and complete survey per screen, card, dialog, have auditor check
this, then re-verify" — done, not the narrower item-3-only scope from above (item 3 is effectively
subsumed into this broader survey now).

**Survey**: `spec/16-gui-wiring-survey.md` (new, committed `85397e2`). Every control across all 7
Views (Receive/Transmit/Gallery/Logbook tabs, menu/status bar, Radio header, Options window's 7
tabs) traced from its `.axaml` binding back to the ViewModel and classified: **REAL** (genuinely
wired), **STUB** (`IsEnabled="False"`/no Command), **FAKE-LIVE** (looks like live data, is actually
a hardcoded literal — often hiding in `assets/locale/en.json` rather than the `.axaml` itself, e.g.
status bar's `"SNR 21.6 dB"`/`"slant +3.4 ppm"`), **PARTIAL** (real wiring, incomplete). ~280
controls classified: ~94 REAL, ~110 STUB, ~71 FAKE-LIVE, ~5 PARTIAL.

**Densest placeholder concentration**: Receive tab's Sync&Slant/Input-chain/Signal-quality/
Frame-metadata/Unattended-RX/Session-frames cards (nearly top-to-bottom fake/stub — no live
audio-chain measurement, no SNR/histogram computation, no structured decode-event log exist
anywhere in `Core.Audio`/`Core.Sstv` today, so these six cards are pure future-DSP-work, not just UI
wiring). Options window's Decode/Identification/Advanced tabs (100% stub, ~55 controls — this is
the old item-3 scope, now mapped precisely instead of guessed at). Transmit tab's right-column
Queue/TX-log/Recently-sent cards (100% stub, no such features exist).

**Auditor round: TRUSTWORTHY AS MASTER INVENTORY, 4 fixes applied.** Spot-checked ~40 citations +
every REAL/PARTIAL claim sampled, confirmed all quoted locale literals verbatim. Found and fixed:
(1) a missed FAKE-LIVE — TX image editor's canvas safe-area/callsign/report plate text overlay,
arguably the single most deceptive one in the app since it reads as text burned into the actual
outgoing TX image; (2) a reclassification — Receive tab's "Previous-frames strip" was marked REAL
but the VM subscribes to no live session event (its own doc comment says so), so it never updates
during an actual receive session, only at construction/manual-refresh/filter-change — moved to
PARTIAL; (3)-(4) two label-consistency fixes (PARTIAL category had zero matching rows in the body;
a `DataContext`-inheritance claim was imprecise about scope). All applied directly, re-verified for
internal consistency (grep confirms no stale leftover text), committed together with the survey.

**Follow-up (2026-08-09): RX telemetry feasibility doc, done and auditor-verified.** User asked to
"write out the DSP telemetry we need to add first. Verify if it's actually technically possible."
`yoniq-old/YONIQ-main/` cloned locally (was already gitignored, not committed) for direct legacy
verification. Reused the already-established Tier A/B/C investigation (`~/.claude/plans/steady-
humming-osprey.md`, commit `cf76a08`) rather than re-deriving it, then researched 16 NEW fields the
GUI survey found that Tier A/B/C never covered (Resync button, RX device name, AGC gain, luminance
histogram, sync/black/white tone, line progress, buffer/XRUN, RX gain, squelch, dropped lines, etc.)
against both this port's source and cloned legacy source.

**Doc**: `spec/17-rx-telemetry-feasibility.md` (new, committed `46cd53a`). Classifies each field
REAL-EASY (already computed, just needs wiring — e.g. line progress is literally already on every
`LineDecoded` event, zero backend work), needs-a-decision-then-cheap, TIER-B-STYLE (no legacy
concept, needs a product call), or NOT-POSSIBLE (architecturally blocked, e.g. RX gain — no software
gain stage exists anywhere, legacy's own RX chain is hardware-gain-only too). Includes a recommended
build order (cheapest/highest-value REAL-EASY items first).

**Auditor round**: found and fixed one real error — the original squelch verdict ("zero legacy
grounding") was wrong, caused by a mismatched `grep -a` search term (legacy spells it `SQ`, doc
searched for "Squelch"/"Sense") that missed a real, narrower repeater-scoped legacy squelch feature
(`m_RepSQ`, `CLMS::Sig`). Corrected the verdict and reasoning. Four smaller fixes also applied: XRUN
counters need a real (small) `IAudioEngine` interface extension, not a pure existing-property read;
AGC gain turned out cheaper than first claimed (zero backend change at all, not "promote to
public"); two citation line-number slips; and a missed detail that legacy has two selectable RX
level-meter types, of which this port only ports one.

**Unblocked (2026-08-09): user said "go ahead and start wiring the cheap wins, do have auditor do
verification though."** Working `spec/17-rx-telemetry-feasibility.md`'s REAL-EASY list in the
recommended build order, each batch getting 2 rounds of auditor review (plan-free — these are
wiring/plumbing changes, not DSP ports, so no plan-review round, just post-implementation code
audit) before commit+push, matching this session's established process elsewhere.

**Batch 1 SHIPPED (`17162c1`)**: Sync&Slant card (Slant ppm, Sync offset, Auto-correct "locked"
half, Re-sync button), Input-chain card (Buffer, Clipping), Signal-quality card (Sync tone), status
bar Slant. 2 new `ISstvSessionService` pass-through properties. Auditor caught 2 real bugs in the
sync-tone math (a sign inversion that would've shown a station's frequency offset mirrored, and a
missed +3.125Hz/+1.0Hz legacy calibration-offset term) before shipping — both fixed, regression
tests added tracing the exact worked example already documented in `AnalogFmSstvDecoder.cs`.

**Batch 2 SHIPPED (`0b5d291`)**: Status bar line-progress readout (from `IReceivedImageBuffer.Progress`,
already-computed, zero new backend), Frame-metadata card's "Started" timestamp (new client-side
capture at `ModeDetected`), and status bar Buffer readout (opportunistic, reused a batch-1 property
against a second display site). No blockers either round; one real risk fixed (missing
property-change notification could show a stale/wrong line total for one frame after a fresh mode
detection).

**Batch 3 SHIPPED (`2c047a4`)**: RX capture device name (new `GetConfiguredCaptureDeviceNameAsync`,
exact mirror of the existing TX pattern), Signal-quality card's Clip lo/hi readout (new
`LuminanceClipStatistics` utility — deliberately non-legacy, pure image-domain pixel math, not
audio DSP). Round 1 caught a real bug: computing clip stats over the WHOLE mode-sized canvas
mid-decode measured the not-yet-decoded (zeroed/black) rows, showing a wildly wrong "mostly clipped
black" reading for the entire time a user watched a live decode. Fixed by row-limiting to
`Progress * Height` rows, placeholder shown while idle.

**Batch 4 SHIPPED**: UTC clock (`RadioStatusViewModel.UtcClockDisplay`, 1s `DispatcherTimer`), AGC
gain (`RxImagePaneViewModel.AgcGainDisplay`, pure client-side derivation from `SignalPeakLevel`,
auditor-verified exact match to legacy's `LevelAgc.cs:103` formula), status bar's "frames today"/
"log size" (new independent counts on `RxHistoryPaneViewModel`/`LogbookPaneViewModel`), and
Frame-metadata's "Size on disk" (new `IReceivedImageBuffer.Saved`/`Generation` API — turned out to
need real new plumbing, not a one-line wire, since no save-completion hook existed anywhere a live
pane could reach).

**Two real bugs caught across 3 rounds of auditor review, both fixed**: (1) a genuine UTC-vs-local
blocker in the frames-today query — `ReceiveHistoryRecorder` actually writes `DateTimeOffset.Now`
(local), not UTC as this code originally assumed, and the SQLite store's date filter is a
lexicographic TEXT compare that only stays correct when the query's offset matches; fixed to match
`ShowTodayOnly`'s own local convention, new store-level regression test added
(`SqliteReceiveHistoryStoreTests.QueryAsync_DateRangeCompareIsLexicographicOnStoredOffset_NotInstantBased`).
(2) A save/restart ordering race in the new "Size on disk" readout took 2 fix attempts: round 1's
fix (a same-class counter) only narrowed the window; round 2 moved generation-tracking into
`IReceivedImageBuffer` itself (new `Generation` property, captured under the same lock as the
buffer's own image snapshot at `SaveAsync`'s true invocation), which round 3 confirmed genuinely
closes it. Also fixed: a `Saved` subscriber's own exception could fault the save `Task` that
`ReceiveHistoryRecorder` awaits, silently losing a history row for an otherwise-successful save
(isolated with try/catch + new `ILogger<ReceivedImageBuffer>`). Full details: `spec/17`.

**Verified**: full solution build clean, full suite green — 1135 tests total (Core.Imaging.Tests
28/28 was 24, Core.Logbook.Tests 65/65 was 64, UI.Tests 119/119 was 118 pre-batch-4, Core.Sstv.Tests
662/662 unaffected). **COMMITTED and pushed (`bbe766c`).**

**Batch 5 SHIPPED (2026-08-09), Buffer·XRUN only** — session paused mid-investigation, resumed via a
scheduled cron trigger, user chose "Buffer·XRUN only, now" over also taking on Auto-correct's on/off
half. Promoted `MiniAudioEngine.CaptureOverrunCount` onto `IAudioEngine` itself (was deliberately
concrete-class-only), threaded a pass-through through `ISstvSessionService`/`SstvSessionService`,
recombined the status bar's/Input-chain's Buffer readout back into mock2's original single
"buffer 512 samples · 0 XRUN" format now that both halves are real (batches 1/2 had split them apart
specifically because XRUN was still a hardcoded fake — that reason no longer applies).

**3 real risks caught across 2 rounds of auditor review, all fixed**: (1) the new pass-through was
reachable from `RxImagePaneViewModel`'s 250ms polling timer with no guard against a documented
`MiniAudioEngine` race — a concurrent Stop RX disposing the capture session mid-read throws
`ObjectDisposedException`, which a `DispatcherTimer` tick has nowhere safe to land (the global
unhandled-exception handler only logs) — fixed by absorbing the exception at `SstvSessionService` and
returning `0` (already the documented "not running" contract value). (2) The interface doc comments
claimed "0 when not running"/"safe to read from any thread" while the real implementation could
throw — fixed by making the `ISstvSessionService`-layer claim genuinely true and correcting the lower
`IAudioEngine`-layer claim to honestly document the exception risk. (3) `FakeAudioEngine.CaptureOverrunCount`
was a bare settable property that didn't enforce the real engine's own contract (0 when not
capturing, reset on a fresh `StartCaptureAsync`) — fixed to match. Full details: `spec/17`.

**Verified**: full solution build clean, full suite green — 1141 tests total (Core.Audio.Tests 17/17
was 14, MiniAudio.Tests 57/57 was 56, Application.Tests 69/69 was 68, UI.Tests 120/120 was 119,
Core.Sstv.Tests 662/662 unaffected). **COMMITTED and pushed (`27df200`).**

**Batch 6 SHIPPED (2026-08-09): Auto-correct's "on/off" half.** Full plan + auditor plan-review
process (not the lighter batches 1-5 review) — this genuinely touches `AnalogFmSstvDecoder`'s decode
path. New `SstvDecoderSettings.AutoSlantEnabled` mirroring the 4 already-shipped sibling toggles'
exact pattern, threaded through `AnalogFmSstvDecoder`/`RestartableSstvDecoder`/`ISstvDecoder`/
`ISstvSessionService`, gating `ApplySlantTracking`'s commit branch.
`RxImagePaneViewModel.AutoCorrectDisplay` rewritten from 2-way to 4-way (AVT / Off / Locked /
on-not-locked).

**Plan-review found a real, un-scoped legacy-parity bug**: `KRSA->Checked` is ALSO read at
`Main.cpp:3910`/`:3917`, inside Auto Sync's own branch-1 threshold (`(KRSA->Checked ? 5 : 2) * m_Mult`)
— a completely separate call site from the slant-commit block this batch originally set out to gate.
This port had hardcoded the `5` side unconditionally, correct only because no toggle existed yet to
make the `2` side reachable — fixed as part of this batch, with a dedicated ratio-based regression
test (not an empirically-tuned audio splice, which can't cleanly isolate two thresholds that move a
trigger window in opposite directions at once).

**A second real design misconception surfaced mid-implementation**, caught by a test actually
failing, not by inspection: both the plan and the auditor's own plan-review assumed "with the toggle
off, `SlantPpm` stays null." Wrong — `SlantTracker.DriftPpm` defaults to `0.0` (non-null) from
construction, unaffected by the toggle. Took TWO correction attempts to get right: the first narrowed
it to "only reachable in a brief startup window," which the post-implementation code-review round
caught as ALSO wrong (`SlantPpm` is null for a decoder's entire idle period between receptions, not
just a startup window). Two rounds of post-implementation auditor review also fixed a
`RestartableSstvDecoderTests` forwarding test that could have passed vacuously (added a positive
control), an untested `OnPropertyChanged` re-raise, and an AVT test that never exercised the states
its own name claimed. Full details: `spec/17`.

**Verified**: full solution build clean, full suite green — Core.Sstv.Tests 666/666 (was 662, +4),
UI.Tests 124/124 (was 120, +4), everything else unaffected. **COMMITTED and pushed (`ec0734d`).**

**§17's REAL-EASY list is now fully closed.** Remaining items on that page all need explicit product
decisions first (Source labels, Advanced timing, Reset button semantics, squelch), or are real new
DSP/feature builds, or are architecturally not possible — none are "quick wiring" candidates.

**Batch 7 SHIPPED (2026-08-09): RX history browser affordances.** User: "work on RX history and keep
in mind to map it to gui items when/if need or required" — checked `MainWindow.axaml`'s actual
Gallery-tab layout and mock2's own draft before adding anything; deliberately did NOT add a
step-through prev/next spinner (superseded by the existing click-to-select-any-thumbnail grid, a
strict superset) and deliberately DEFERRED clipboard image copy-out (Avalonia's `IClipboard` has no
first-class bitmap API, confirmed via `strings` on `Avalonia.Base.dll` — only generic
`IDataObject`/format-string plumbing, needing its own cross-platform research pass) — both logged as
considered scope decisions in `docs/removed-features.md`, not silent omissions. What shipped: new
`IReceiveHistoryStore.Recorded` event (`SqliteReceiveHistoryStore` fires it after insert+retention-trim,
isolated with try/catch so a subscriber's exception can't fault the write) wired into
`RxHistoryPaneViewModel`, so the Gallery list and the Receive tab's Previous-frames strip (same shared
VM) now refresh live as frames land, not just at construction/manual-refresh/filter-change — closes
`spec/16-gui-wiring-survey.md`'s own PARTIAL finding. New `SelectLatestCommand`/"Latest" button ports
legacy's real `SBPrim` speed button (`Main.cpp:15851-15857`) — **not** `SBLatest`, which despite its
English name actually jumps to the OLDEST buffered frame (`Main.cpp:6407-6413`, traced via
`UpdateHist`'s ring-buffer mapping at `Main.cpp:6277-6281`) — a citation error the auditor caught and
I independently re-verified against the cloned legacy source before fixing it everywhere (including a
matching slip in a test comment the auditor's own review didn't cover).

**Three rounds of auditor review, the third catching something no isolated-VM test could have found.**
Round 1: a vacuous selection-preservation test (fixed to assert object-identity against the freshly
rebuilt list, not just `Entry.Id` equality), a preview-flicker/redundant-redecode bug (new
`_previewedEntryId` tracking field), a stale-refresh race (new monotonic `_refreshGeneration` token),
and `SqliteReceiveHistoryStore`'s new `ILogger` param made required instead of optional-with-fallback
to match established DI precedent (all 23 test call sites bulk-updated). Round 2: traced the SBPrim/
SBLatest citation precisely against `Main.cpp`, and flagged that the flicker fix was a **no-op in the
real app** — confirmed by reflecting directly against the real `Avalonia.Controls.dll`
(`SelectingItemsControl.SelectedItemProperty`'s `DefaultBindingMode` is `TwoWay`), meaning
`Entries.Clear()` pushes a transient `null` into the VM's `SelectedEntry` — and hence nulls the
preview — *before* the live-refresh re-select line runs, defeating the round-1 fix despite it passing
every isolated-VM test. Fixed with an `_isRepopulating` guard flag plus an explicit post-refresh
reconcile step. Added a genuine regression test using a real Avalonia `ListBox` with the same TwoWay
binding `MainWindow.axaml` uses — verified non-vacuous by disabling the fix and confirming the test
fails (2 thumbnail decodes instead of 1) before re-enabling it. Round 3: one closing nit (the
post-refresh reconcile needed to also bump the new `_previewGeneration` token so a slow in-flight
decode can't resurrect a just-cleared preview) — fixed, confirmed EQUIVALENT-WITH-RISKS/ready to
commit.

**Verified**: full solution build clean (0 warnings, 0 errors), full suite green — 1657/1657 tests
(UI.Tests 129/129 was 124, Core.Logbook.Tests 67/67 unaffected, everything else unaffected).
**COMMITTED (`1df5eac`)** — not yet pushed.

**Waterfall color/palette: user confirmed "Full item"** despite this doc's own prior deprioritization
note (asked explicitly via AskUserQuestion since the roadmap entry says "re-confirm before starting,
don't silently reprioritize"). Full plan + 2 rounds of auditor plan-review before any code (UI design
touching a "worth real design attention" visualization surface, spec/09-ui.md) -- plan-review caught
3 real blockers on paper before implementation: the proposed dB normalization range would have
saturated every real signal to solid red (fixed by actually measuring real captured audio -- fed all
8 `.mmv` golden-vector fixtures through the real `WaterfallSource`, not guessed); the mocked
Bins/px+Start+Span controls were over-determined (3 independent knobs for 2 real degrees of freedom,
fixed by making Bins/px a read-only computed readout); and a naive `IsVisible`-based Both/Spec/WF
toggle would have left a half-empty card (fixed by driving `ColumnDefinition.Width` directly).
Scoped down from the roadmap's full inventory to what's realistic: interactive notch-filter marker
deferred (no notch-filter DSP block exists anywhere in `Core.Sstv`, confirmed by grep -- that's really
"build a new audio DSP feature," its own future backlog item) and the debug "digital scope" tool/
dedicated signal meter deferred (no mock2 slot for either, and the roadmap's own text already judges
the debug scope "probably the lowest-value item in this list") -- to be documented in
spec/14-roadmap.md once the shippable pieces (batches A+B) are both done.

**Batch 8a SHIPPED (2026-08-09): waterfall gradient palette + Gain/Zero wiring.** Replaced flat
grayscale with a 6-stop SDR-style heatmap (WSJT-X/SDR++/GQRX convention, not legacy's flat
black/white -- spec/06 exempts this visualization from strict port fidelity), preserving legacy's
real Low=weak/High=strong semantic (verified against `Main.cpp`/`ComLib.cpp`'s `InitColorTable` --
legacy's own internal color-table index order is inverted, the port keeps the EXTERNAL semantic, not
that inversion). Wired mock2's previously-fake "Gain"/"Zero" sliders to real
`WaterfallControl.GainDb`/`ZeroDb`, defaults measured (not guessed) from real captured audio: real
signal peaks 38-46dB, noise floor -50 to -65dB (this port's FFT magnitude is unnormalized, not a
conventional -100..0 dBFS scale).

**3 rounds of auditor code review caught 2 real bugs**, both non-vacuously tested (verified by
temporarily disabling each fix and confirming the regression test actually failed first): (1)
`Dispose()` left stale `_colorHistory`/`_bins` behind, so switching away from the Receive tab (whose
non-selected `TabItem` content detaches, calling `Dispose`) permanently blanked the waterfall for the
rest of the session on switching back -- the next frame's init-guard silently skipped recreating the
bitmap. (2) A new parallel `_dbHistory` buffer (needed so a slider drag recolors already-rendered
rows, not just future ones) zero-filled on allocation, indistinguishable from a real 0dB reading --
the first slider move on a fresh session could flood never-written rows solid red; fixed with a
`float.NegativeInfinity` sentinel. Also discovered mid-review: Avalonia's HEADLESS test renderer does
NOT reliably round-trip raw pixel bytes through `WriteableBitmap.Lock()` (confirmed via a throwaway
diagnostic -- a real, environment-specific artifact, not a production bug), so pixel-level tests read
the control's own managed buffer via reflection instead of the bitmap; BGRA byte order itself was
separately confirmed via one real (non-headless) Avalonia render on this dev box's real second
monitor -- R=247/G=115/B=34 for a 46dB signal, matching the calculated expected value and ruling out
a swapped R/B (which would have shown blue, not orange-red).

**Verified**: full solution build clean (0 warnings, 0 errors), `UI.Tests` 145/145 (was 129).
**COMMITTED and pushed (`1cc4568`).**

**Batch 8b SHIPPED (2026-08-09): FFT spectrum trace view + Both/Spec/WF toggle + peak-hold + zoom.**
New `SpectrumTraceControl` fills mock2's previously-empty "Spectrum" plot placeholder: real
amplitude-vs-frequency line trace, markers at legacy's real SSTV control-tone frequencies derived
from the currently-locked `SstvModeDefinition` (reuses `SstvModeRegistry.cs`'s own already-verified
`NarrowModeCode is not null ? 1900.0 : 1200.0` sync-tone pattern, not re-derived), an optional
peak-hold overlay (legacy `sys.m_FFTStg`, time-based not frame-based decay so it's frame-rate
independent), sharing `WaterfallControl`'s `ZeroDb`/`GainDb` normalization window for its vertical
scale so both plots stay visually consistent. Wired mock2's remaining fake controls: Bins/px became
a read-only computed readout (was three independent knobs for two real degrees of freedom, per
auditor plan-review); Start/Span drive a continuous frequency window (legacy's own zoom is 3 discrete
presets, a deliberate generalization); the Both/Spec/WF segment now really toggles plot visibility via
a new `WaterfallViewModeToColumnWidthConverter` driving `ColumnDefinition.Width` directly (not
`IsVisible` alone, which would leave the collapsed plot's own Star column at half-width). New
Peak-hold checkbox (no mock2 slot, same class of justified addition as batch 7's "Latest" button).

**3 rounds of plan-review + 2 rounds of code-review caught real issues at both stages.** Plan-review:
the proposed dB range would have saturated every real signal to solid red (fixed in batch 8a via
actual measurement against real `.mmv` captures); Bins/px/Start/Span were over-determined; a naive
`IsVisible` toggle would leave a half-empty card. Code-review: `ModeDetected` needed explicit
UI-thread marshaling (fires on the audio drain thread, same class of bug as batch 4's telemetry
work); `BinsPerPixel` was being computed inside `Render()`, mutating the binding graph mid-render --
moved to `ArrangeOverride`, verified with a REAL headless layout pass (`Window.Show()`+`RunJobs()`,
confirmed empirically that a bare property set does NOT exercise real Avalonia layout, so the fix
needed a genuinely different test technique than every other control test in this codebase); a `Pen`
was being allocated per line segment in the trace's hot path (~300 allocations/render at 21fps); a
narrow-mode marker-set citation slip (1200Hz must be in the fixed base set unconditionally to match
legacy's real narrow-mode markers, `Main.cpp:3269-3270`) -- the auditor's own round-2 "matches legacy
exactly" claim had an arithmetic error, self-caught and corrected in round 3.

**The auditor also raised a real Avalonia-vs-WPF uncertainty they could not resolve from their own
environment** (does `ColumnDefinition.Width` even bind against a `DataContext`, a classic WPF
failure mode) -- verified empirically rather than assumed either way: two throwaway harnesses in a
real (non-headless) window, the second using the ACTUAL production converter class with the exact
XAML syntax from `MainWindow.axaml`, confirmed Avalonia 11's `ColumnDefinition` genuinely does bind
correctly (unlike WPF). Same "verify, don't guess" discipline used for BGRA byte order in batch 8a.

**Verified**: full solution build clean (0 warnings, 0 errors), full suite green -- 1698/1698 tests
(`UI.Tests` 171/171, was 145; everything else unaffected). **COMMITTED and pushed (`71a7daf`).**

**Waterfall color/palette item is now fully shipped.** `spec/14-roadmap.md` updated: interactive
notch-filter marker and the debug "digital scope"/signal-strength-meter sub-items explicitly deferred
(documented reasoning, not silently dropped) -- no notch-filter DSP block exists anywhere in
`Core.Sstv` (confirmed by grep), and no mock2 slot exists for the other two.

## Previously (2026-08-08) — Occupied bandwidth investigated and abandoned; "Tone map" freebie shipped instead.

Picked "occupied bandwidth" next (user's own choice over the cheaper static alternative, after
device name shipped). Research first: confirmed via `grep -a` that legacy has zero equivalent
(legacy's one FFT instance only ever runs on RX input, never TX — genuinely an "invent" not "port"
task). Drafted a plan, sent for auditor plan-review.

**Round 1** caught 4 real design bugs in the initial per-frame approach: wrong quantity entirely (a
per-STFT-frame instantaneous reading would flicker 40-300Hz values 21x/sec next to a static-looking
"2.31 kHz" mock field), no exception isolation on the new TX push (a display bug could abort a live
transmission), wrong push site (inside a back-pressure retry loop, would corrupt the FFT
accumulator via duplicate pushes), missing View/locale wiring (would ship a VM property nothing
displays). Redesigned around a per-transmission running union (min/max Hz edges merged across every
frame, reset at transmission start) to fix the "wrong quantity" problem specifically.

**Round 2** found the redesign introduced 2 NEW bugs (the RX `WaterfallPaneViewModel` coalescing
pattern silently drops frames — fine for a live display, wrong for an accumulator that must see
every frame; the un-reset FFT accumulator would splice a hard discontinuity across every
transmission boundary, permanently poisoning that transmission's whole reading) — but more
importantly, traced through `AnalogFmSstvEncoder`'s own `TxOutputBandpassFilter` (a real, confirmed
700-2800Hz bandpass already applied to every TX sample) and reasoned that a min/max union over an
entire transmission is an extreme-value statistic that would very likely just converge to that
FIXED FILTER'S OWN skirts (~2-2.5kHz) on every transmission regardless of image content —
reporting the encoder's own known, constant characteristic back as if it were a live per-image
measurement. Recommended: measure first with real code before committing further design work, or
reconsider the whole approach.

**User's call, presented with that finding**: skip occupied bandwidth entirely (not worth building
something likely non-functional-but-plausible-looking), ship the free "Tone map" field instead —
`SstvModeDefinition.LuminanceMinHz`/`MaxHz` already has real, mode-dependent data (confirmed: narrow-
family modes override the 1500/2300 default to 2044/2300, not a constant) with zero new DSP or
wiring needed. Full plan/reasoning preserved at `~/.claude/plans/wandering-glowing-otter.md` for a
future pass that might want to revisit with a different measurement point (e.g. pre-bandpass-filter
samples) or report min/max edges instead of width.

**Shipped**: `TxControlsPaneViewModel.ToneMapText` (computed, `[NotifyPropertyChangedFor(nameof(SelectedMode))]`-
driven, reacts live to mode selection), a new localized format string
(`Panes.TxControls.Telemetry.ToneMapFormat` = `"{0:0}–{1:0} Hz"`, replacing the old hardcoded
`ToneMapValue` placeholder — real prose/format strings go through the locale layer, not baked into
XAML, learned directly from round 2's parallel finding about the (abandoned) Occupied BW field's own
Hz-formatting question). Skipped the plan+auditor cycle for this specific shipped piece (same
low-risk-UI-plumbing reasoning as the device-name item) — no DSP correctness or concurrency risk,
just a static per-mode lookup.

**Verified**: full solution build clean, `UI.Tests` 88/88 (was 84, +4 new),
`Core.Localization.Tests` 7/7 unaffected. **NOT YET committed** — about to commit.

**Next up**: sample-clock offset and monitor-while-TX remain deferred (genuinely new
instrumentation/audio-routing features, not exposures) — no immediate plan to build either. Otherwise
continue down the roadmap's root-cause map: `ReceiveHistoryEntry`'s remaining callsign/grid/SNR
fields (blocked on OCR/Tier B), structured per-decode event log, decoder Abort (frame-action
primitives' other piece, `Core.Sstv`, needs `RequestReSync`/`ForceMode`-level concurrency care), or
OCR/QRZ lookup (large, separately-scoped).

## Previously (2026-08-08) — TX output device name (RX GUI-blocking backend primitive, deliberately no-ceremony).

Next roadmap item after the Gallery/logbook batch below: TX-side device/clock telemetry. Research
found the roadmap's framing overstated the size — only "device name" is a cheap exposure; the other
3 sub-items (sample-clock offset, occupied bandwidth, monitor-while-TX) are all real new
instrumentation/features, explicitly deferred with their own reasons (see `spec/14-roadmap.md`'s
updated root-cause map row). User's own call: skip the plan+2-round-auditor-review cycle for this
one piece specifically, since it's pure UI/Application-layer settings plumbing with no DSP fidelity
or decoder-concurrency risk (unlike the last two batches) — implemented directly.

**Shipped**: `ISstvSessionService` gained `GetConfiguredPlaybackDeviceNameAsync` (non-throwing;
extracted `SstvSessionService.ResolveDeviceAsync`'s device-lookup into a shared
`TryResolveDeviceAsync` so this can never disagree with what a real `TransmitAsync` call would
actually use — same lookup, not a second independently-fallible one). Wired into
`TxControlsPaneViewModel.OutputDeviceName`, loaded once at construction via the same best-effort
fire-and-forget convention `LoadTxPaneUiSettingsAsync`/`LoadSafetySettingsAsync` already use.
Confirmed the existing `ResolveDeviceAsync` throwing-behavior tests (`StartReceivingAsync_NoCaptureDeviceConfigured_Throws`,
`TransmitAsync_NoPlaybackDeviceConfigured_Throws`) still pass unchanged — the refactor preserved
real TX/RX device-resolution behavior exactly, only added a non-throwing sibling path.

**Verified**: full solution build clean, `Application.Tests` 62/62 (was 59, +3 new),
`UI.Tests` 84/84 (was 82, +2 new). **NOT YET committed** — about to commit.

**Next up**: occupied bandwidth (user's own pick for after this) — the FFT/waterfall machinery
already exists (`RadixTwoFft`/`WaterfallSource`) but is wired to RX capture only; needs real new
wiring to point at TX audio, plus an actual measurement-definition decision (what threshold counts
as "occupied"?) before scoping further — likely worth the full plan+auditor cycle given that open
design question, unlike this device-name piece.

## Previously (2026-08-08) — Gallery/logbook metadata batch (Note/Flagged/DecodeState fields + QSO-log-link).

User asked to work through the "similar expose/small build" backlog (root-cause map: `ReceiveHistoryEntry`'s
field set, structured per-decode event log, frame-action primitives, TX-side device/clock telemetry)
and combine into one plan if genuinely small, with the auditor specifically asked to double-check
what's safe to combine. Research found 4 items split unevenly: 3 fields (`Note`/`IsFlagged`/
`DecodeState`) + a QSO-log-link update method all touch the exact same table/store/record — bundled
into one plan (`~/.claude/plans/mellow-drifting-lynx.md`). The other 3 sub-items (per-decode event
log, Abort, Re-decode, TX telemetry) stayed explicitly deferred, each with its own reason.

**Different risk class from the last two shipped items**: not decoder concurrency, but SQLite schema
migration (the first this store has ever needed) + data-loss risk on real users' existing
`history.db` files. Two rounds of plan-readiness review caught real, code-breaking SQL bugs on
paper: round 1 found an invalid `ALTER TABLE ... NOT NULL` sequence with no `DEFAULT` (can never
work in SQLite), a `LIKE '%_partial_%'` backfill pattern that's a genuine data-corruption bug (`_`
is a SQL wildcard, LIKE is case-insensitive, so it matches against the FULL path — a user's images
folder merely containing "partial" would misclassify every completed image), and an uncredited
retention-trim data-loss issue (the existing 32-entry ring buffer would have silently deleted
user-typed notes/flags/QSO-links every ~32 receptions). Round 2 caught one more: the narrower
`instr()`-based backfill fix still matched the full path, not just the filename — fixed to `GLOB`
anchored on the real filename shape. Round 2 verdict: "yes, start building."

**Shipped**: `ReceiveHistoryEntry` gained `Note` (`string?`), `IsFlagged` (`bool`), `DecodeState`
(new `ReceiveDecodeState` enum, `Completed`/`Abandoned`, required positional param — no default, so
the compiler enumerates every construction site rather than letting a future one silently inherit a
wrong value). `IReceiveHistoryStore` gained `SetNoteAsync`/`SetFlaggedAsync`/`SetLinkedQsoIdAsync`
(all `Task<bool>`, `false` = `entryId` no longer exists — a reachable case precisely because of the
retention trim). `SqliteReceiveHistoryStore.EnsureSchema` now migrates an existing pre-migration DB
in place (`PRAGMA table_info` probe + `ALTER TABLE ADD COLUMN`, whole sequence transactional via
`BeginTransaction(deferred: false)`), backfilling `DecodeState` for pre-existing rows from the
`_partial_` filename convention via `GLOB '*_partial_????????.png'`. `TrimToRetentionLimitAsync`
gained a `WHERE` exemption so a noted/flagged/logged row survives past the 32-entry window instead
of being silently destroyed. **Real correction found along the way**: the roadmap's "QSO-log-link
blocked on [[08-logging]]" framing was stale — the logbook backend (`ILogbookRepository`,
`QsoRecord.ReceivedImageId`) already existed, shipped 2026-08-07; the real gap was just a missing
update-after-the-fact method on the RX-history side.

**Code-level audit after implementation**: no blockers, but caught one real "fix now or never" issue
— the migrated schema's column order would have permanently diverged from a fresh DB's (SQLite
`ADD COLUMN` always appends) the moment this shipped, contradicting the code's own "byte-identical
schemas" doc-comment claim. Harmless today (no `SELECT *` anywhere in the codebase) but permanent
once baked into real user DBs — fixed by reordering the `ALTER TABLE` sequence to match `CREATE
TABLE`'s column order, and softened the doc comment (true byte-identical SQL text isn't achievable
either way — `sqlite_master` stores `CREATE TABLE` vs `ALTER TABLE` text differently — the real
invariant is identical column set/order/defaults). Also made `BeginTransaction(deferred: false)`
explicit rather than relying on the parameterless overload's documented-but-unverified-by-audit
default, and strengthened two tests that were checking a weaker property than their names claimed
(a "run twice" test that only proved "doesn't throw," a migration test that didn't check the other
2 columns' defaults).

**Verified**: full solution build clean, `Core.Logbook.Tests` 64/64 (was 55, +9 new — including a
migration test with a deliberately-adversarial `rx_partial_saves`-named directory to prove the GLOB
fix doesn't false-positive the way the rejected LIKE/instr approaches would have), UI.Tests 82/82,
Application.Tests 59/59, all unaffected by this change and confirmed unbroken. `Core.Sstv.Tests`
not re-run (unrelated assembly, unchanged since its last full green run this session). **NOT YET
committed** — about to commit.

**Next up**: per the roadmap, the remaining deferred sub-items are Abort (a decoder-state COMMAND,
`Core.Sstv`, needs `RequestReSync`/`ForceMode`-level concurrency care — different risk class,
separate plan), Re-decode (effectively blocked, no raw audio retained anywhere in this port),
structured per-decode event log (real open design question on granularity — one row per image vs.
a live per-line decoder-trace pane), and TX-side device/clock telemetry (`Core.Audio`, fully
independent assembly — device name might already be available client-side with zero backend
change, worth a quick check before assuming a gap).

## Previously (2026-08-08) — Decode-time signal telemetry, Tier A (RX GUI-blocking backend primitive).

Second item off `spec/14-roadmap.md`'s root-cause map, right after the slant/sync readouts below.
That item's own roadmap note flagged the next one ("Decode-time signal telemetry": SNR, squelch,
BPF/AGC/notch state, buffer/clipping%, noise floor, L/R levels) as bigger and needing a plan-review
pass first — this session did that: a research fork found the item is NOT homogeneous, split into
3 tiers (`~/.claude/plans/steady-humming-osprey.md` has the full breakdown), only Tier A built now.

**Tier A shipped, backend-only (no UI wired yet)**: `ISstvDecoder` gained `SignalPeakLevel`
(double, peak amplitude `LevelAgc.CurMax/32768.0`), `IsLevelOverdriven` (bool, legacy's own
`DrawLvl` red-meter-bar threshold `>= 24578.0`, NOT legacy's separate `m_OverFlow` raw-sample flag —
a real, different legacy quantity this port's post-BPF `LevelAgc` input can't reproduce),
`SyncFrequencyCorrectionHz` (double?, AFC's own `CorrectionHz` passthrough), and
`BufferedSampleCount` (int, promoted from `internal`). Implemented on both `ISstvDecoder`
implementers + 3 test `FakeSstvDecoder`s.

**Tier B/C explicitly scoped OUT, not silently dropped**: true SNR/SNR-histogram/noise-floor
(legacy has zero equivalent — `CNoise` is a noise *generator* for test/sim, not a measurement;
inventing one needs a product decision, not a port); notch-filter state (`CNotch`, `fir.h:123`,
entirely unported — no filter exists yet to report the state of); true L/R stereo levels (legacy
and this port are both mono-only in the demod path; this port's `AudioChannelSource` is a channel
*selector*, not simultaneous dual-channel capture); squelch (zero legacy grounding at all, `grep -a`
confirmed). All four are real, separately-scoped future items, not silently skipped.

**Process**: full two-round auditor plan-readiness review before any code (round 1: READY WITH
FIXES, 3 real corrections — `IsClipping`→`IsLevelOverdriven` rename fixing a conflated-legacy-
quantity mistake, a self-contradictory normalization/threshold pairing, and a false "0 when idle"
claim for `BufferedSampleCount`; round 2: "yes, start building", one more fix — the
`RestartableSstvDecoder`'s AGC-backed properties don't reset cleanly on a restart swap the way
`SlantPpm`/`SyncOffsetSamples` do, since the swap forwards the triggering chunk to the fresh inner).
Then a code-level audit after implementation: verdict EQUIVALENT-WITH-RISKS, no production bugs,
but caught a real test-hygiene gap — two "unit" tests re-derived the `/32768.0`/`>= 24578.0` math
independently instead of reading the actual `SignalPeakLevel`/`IsLevelOverdriven` properties, so a
wrong divisor or a `>`-vs-`>=` bug would have shipped green; notably, no test anywhere asserted
`IsLevelOverdriven == true` even once before this fix. Fixed: added `LevelAgcForTests` (matching the
existing `SlantTrackerForTests` pattern) so tests drive the decoder's real AGC instance and assert
against the real production properties; added a real end-to-end amplitude test (0.9/0.3 amplitude
1200Hz tones through the actual BPF/AGC pipeline, not just direct `LevelAgc` driving); added the one
missing post-swap assertion (`BufferedSampleCount == 50` after a swap, the actual property the
round-2 fix was about).

**Verified**: full solution build clean, `Core.Sstv.Tests` 653/653 (was 637 after the prior item;
+16 net across both audit-driven fix rounds), Application/Imaging/Logbook.Tests all green.
**COMMITTED** (`cf76a08`).

**Doc sync (2026-08-08)**: `spec/14-roadmap.md`'s root-cause map table and the itemized mock2 list
below it were stale after this item and the prior one (`94831b6`) — both had gone in still listing
the two shipped items as open gaps. Updated both to `~~strikethrough~~` + **done**/**partially
done** with commit citations, matching this doc's own established convention (see the
Operator-callsign row for the pattern). Verified against the actual `ISstvDecoder` interface
(`grep`'d the real member list), not written from memory.

**Next up**: same as before this item started — Tier B (SNR/noise-floor) needs a `/adhd`-style
product decision on what "SNR" even means for an FM-demodulated SSTV signal with no clean reference,
not a legacy-verification pass. Otherwise, continue down the roadmap's root-cause map for the next
GUI-blocking backend gap — `ReceiveHistoryEntry`'s missing field set, structured per-decode event
log, frame-action primitives, and TX-side device/clock telemetry are the remaining "expose/small
build"-shaped items; OCR/QRZ lookup and the TX image editor are larger, separately-scoped builds.

## Previously (2026-08-08) — Slant/sync correction readouts (RX GUI-blocking backend primitive).

Working through `spec/14-roadmap.md`'s "Root-cause map" table, starting with the item flagged there
as cheapest: **Slant/sync correction readouts (ppm, offset px)** — the numbers existed only as
internal/test-only fields on `AnalogFmSstvDecoder`, never exposed on `ISstvDecoder`, blocking the
mock2 "Sync & slant" RX readout card (`Panes.RxSync.SlantPpm`/`OffsetPx` in `mockups/`).

**Done, backend-only (no UI wired yet)**: `ISstvDecoder` gained `SlantPpm` (double?, ppm drift,
legacy's `DrawSlantInfo` formula, `Main.cpp:5537`) and `SyncOffsetSamples` (int?, legacy's
`m_AutoStopPos`, `Main.cpp:3887-3889`, never itself displayed by legacy — this port's own new
readout). Implemented on both `ISstvDecoder` implementers (`AnalogFmSstvDecoder`,
`RestartableSstvDecoder`'s forwarding wrapper) plus null/no-op stubs added to all 3 test
`FakeSstvDecoder`s (Application/Imaging/Logbook.Tests).

**Two real bugs caught by auditor review, both fixed before landing** (see git history for the
diff): (1) `SlantPpm` originally only null-checked `_slantTracker`, not `_mode` — during the AVT
mid-reception training hand-off (`AbandonInProgressImage`, up to ~7.1s pending window, chunked-push
only), the abandoned mode's tracker stays alive while `_mode` goes null, so the naive version leaked
the abandoned image's stale ppm the whole window instead of returning null. Fixed by checking both,
read into locals once. (2) `SyncOffsetSamples` had a cross-thread TOCTOU bug: read
`_lastLineSyncPeakPosition.HasValue` then separately `.Value` — a decode-thread write racing between
those two reads (a GUI polling this on a timer, the documented intended use, is exactly this
scenario) could throw `InvalidOperationException` instead of returning null. Fixed by reading each
field into a local exactly once per getter call.

New tests: `SlantTests.cs` gained 7 (was 24 relevant, now 31) — `SlantTracker.DriftPpm` formula
tests (2), decoder-level null-before-lock/AVT-stays-null tests (2), wiring-correctness tests tying
the public properties to the already-audited internal fields (2), and a regression test for bug #1
above (`AnalogFmSstvDecoder_DuringAPendingAvtTrainingWindow_...`) — confirmed via revert-fix-confirm-
fail that it actually fails against the pre-fix code (returned stale `0` instead of `null`), not
just superficially exercising the code path.

**Verified**: full solution build clean (0 warnings/errors). `Core.Sstv.Tests` in progress at time
of writing (was 636/636 before the two auditor-driven fixes were added — re-running now). Auditor
review: VERDICT EQUIVALENT-WITH-RISKS, both risk-level findings fixed above; two remaining nits
accepted as documented limitations, not fixed (re-wrap uses live vs. capture-time line width,
sub-sample/display-only; and this port's ppm readout resets to null between images unlike legacy's
persistent-until-reset one — already stated in the interface doc comment, not a new gap). **NOT YET
committed** — pending final full-suite confirmation.

**Next up**: per the roadmap's root-cause map, the next-biggest GUI-blocking gap after this one is
**Decode-time signal telemetry** (SNR, squelch, BPF/AGC/notch state, buffer/clipping %, noise floor)
— currently nothing measured anywhere in `Core.Audio`/`Core.Sstv`, blocking the RX input-chain
telemetry card and per-line SNR/histogram ("Signal quality") card. Larger than this item (several
new DSP measurements, not just exposing existing state) — worth a plan-review pass before starting,
not a straight "add a property" job like this one was.

## Previously (2026-08-08) — Operator-profile setting + minimal TX-macro engine.
**Implemented, full solution build clean, all test projects green (630/630 Core.Sstv.Tests
unaffected, 82/82 UI.Tests (was 78, +4 new), 59/59 Application.Tests). COMMITTED (`8556738`) and
pushed.**

**Scope correction made before writing any code, worth remembering**: the gap-analysis session that
originally framed this item was wrong on two counts, both caught by fresh research rather than
trusted at face value. (1) `OperatorSettings.Callsign` already existed (shipped in `247fde7`,
already wired into ADIF export) — the "confirmed via full-tree grep, zero real hits" claim was
false. (2) The "Identification card (FSK/CW/Tail ID)" is NOT actually unblocked by a profile
setting at all — it's blocked on the whole FSK/CW-ID audio subsystem, which THIS project already
investigated and explicitly deferred earlier (see `spec/14-roadmap.md`'s own note: "much larger
than a smaller item... TX + RX + a wholly new CW/Morse subsystem"). Building a setting doesn't
change that; Identification stays blocked regardless. **Real scope actually delivered**: `Outgoing-
metadata`'s callsign/name/grid fields, and the overlay editor's `TX-macro token substitution` +
`insert-field picker` — 2 of the original 4 claimed unblocks, not 4.

**Second surprise, also found before coding**: legacy's real macro engine (`MacroText`,
`Main.cpp:10679-10833`) is not "insert my callsign" — it's a full QSO-context templating engine
with tokens for HIS callsign/name/QTH, RST exchange both directions, and time-of-day greeting
phrases, all sourced from live "current contact" form fields (`HisCall`/`HisName`/`HisQTH`/
`HisRST`) this port has no UI concept for at all. Scoped down (user's explicit choice, "minimal
macro engine now") to only the tokens sourceable from `OperatorSettings` + the system clock: `%m`
(callsign, legacy-exact), `%D`/`%T` (UTC date/time, legacy-exact), `{name}`/`{grid}` (new,
non-legacy — legacy has no "my name"/"my QTH" token at all; added deliberately per user's own
real-world observation of other stations' overlays showing exactly this, and matching mock2's own
"MY NAME"/"MY GRID" insert-field chips already in the mockup). His-*/RST/greeting tokens
deliberately deferred, need a "current QSO" form this port doesn't have — same class of deferral as
CW-ID itself, documented not silently dropped.

**Also resolved, a real ham-radio-knowledge exchange worth keeping**: user initially thought
QTH/name/notes seen on other stations' received pictures were transmitted digitally (guessed VIS
header, then FSK-ID). Neither is right in the way assumed: VIS is a fixed few-bit mode-announce
code with no room for free text by design (part of the cross-software interop standard); FSK-ID
(`OutputFSKID`, `Main.cpp:6904-6965`) IS technically a generic ASCII-over-FSK packet (not
callsign-limited at the protocol level — worth remembering if FSK-ID is ever built later, no
protocol ceiling to work around) but isn't what casual operators use for this. Real answer: it's
overlay text baked directly into the image pixels, requiring no special decode support on the
receiving end — exactly the mechanism this piece extends.

**Implementation**: `OperatorSettings` (`ScanlineStudio.Application`) gained `Name`/`Grid` (nullable
strings, matching `Callsign`'s existing pattern) — threaded through `OptionsSnapshot`/
`OptionsSettingsService`/`OptionsWindowViewModel`/`OptionsWindowView.axaml` alongside the existing
Callsign field, following that plumbing's own established shape exactly. New `IMacroTextResolver`/
`MacroTextResolver` (`ScanlineStudio.Application`, pure/stateless, DI-registered singleton) resolves
`%`-tokens char-by-char (matching legacy's real semantics including an unrecognized-token-or-literal-
`%%`-both-resolve-to-`%%` quirk, `Main.cpp:10817-10819`) then `{}`-tokens via plain `.Replace`.
`OverlayElementViewModel` gained a `ResolveMacros` delegate (same "set once at creation time"
pattern as its existing `RemoveCommand`) and a computed `ResolvedText` property — the canvas preview
binds to `ResolvedText` (so typing `%m` shows your actual callsign live, not the literal token), and
`ToImageOverlayElement()` now bakes `ResolvedText` into the image sent to `ApplyOverlay`, NOT raw
`Text` — this was almost a real bug (an early version would have transmitted literal `%m` text in
the actual TX'd image) caught by writing `Overlay_BakesResolvedTextIntoTheAppliedImage_...` before
considering the feature done. Overlay editor's 12 mock2 "insert field" chips: only the 5 backed by
real data now have a `Command` (MY CALL/MY GRID/MY NAME/DATE/UTC → `InsertFieldCommand`, appends the
token to the selected element's raw text) — the other 7 (HIS CALL/HIS GRID/FREQ/MODE/HIS RSV/DIST/
BEAM) stay exactly as they were, literal non-functional scaffolding, since they need the deferred
QSO-form concept.

**Tests**: `MacroTextResolverTests` (new, 10), `OperatorSettingsTests` (extended, 2, including an
explicit old-settings-file-still-round-trips backward-compatibility case), 4 new
`TxImageEditorPaneViewModelTests` (resolved-text-not-raw-template live preview, the
bakes-into-applied-image regression proof above, insert-field append, insert-field no-op with
nothing selected). All existing `TxControlsPaneViewModel`/`TxImageEditorPaneViewModel` test call
sites updated for the new constructor parameter (`IMacroTextResolver`) via a `CreateEditor`/
`CreateViewModel` helper pattern already established in those test files.

**Next up** per user's own priority ordering: the other item flagged alongside this one, slant/sync
correction readouts (exposing already-computed internal fields as public properties on
`ISstvDecoder` — flagged earlier this session as "closer to add a public property than add new
DSP", likely the cheapest remaining item in the RX/DSP-adjacent backlog). RX buffer mode was
investigated in full and shelved (NOT worth building now — auditor-confirmed priority call, plan
kept at `~/.claude/plans/rippling-anchoring-heron.md` for whenever it becomes worth it); see
`spec/14-roadmap.md`'s root-cause map for the current state of that decision.
