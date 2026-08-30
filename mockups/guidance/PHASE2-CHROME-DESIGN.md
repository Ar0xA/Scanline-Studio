# Phase 2 — Chrome markup-port design (menu bar, radio header, tab strip, status bar)

**Historical — already implemented.** The Industry redesign (all 8 phases) shipped; `Cards.axaml`
(the "OLD" markup mapped from below) has since been deleted outright, and the chrome now draws from
`Atoms.axaml`/`AtomsTokens.axaml` — see `spec/09-ui.md`'s "Visual design direction". Kept for the
mapping rationale, not as a pending task.

Maps OLD (`Cards.axaml`-styled, currently shipped) markup to NEW (`Atoms.axaml`-styled) markup,
band by band, preserving every real binding. Every atom cited already exists in
`Styles/Atoms.axaml`/`AtomsTokens.axaml` (Phase 0/1, shipped) unless flagged "**NEW**".

---

## 0. Window frame — the one real architectural decision this phase makes

Current `MainWindow.axaml` outer `Grid RowDefinitions="Auto,Auto,*,Auto"` (4 rows: menu, header,
`<TabControl>` — which bundles ITS OWN header+content together inside the single `*` row — and
status bar). LAYOUT-SPEC §1 wants 5 fixed/fill bands: `26,81,26,*,25`, with the tab-STRIP as its
own fixed 26px band, separate from the tab-BODY fill band.

**Recommendation: keep Avalonia's real `TabControl`/`TabItem` (preserve all real selection/
content-switching behavior, zero ViewModel changes — nothing today binds to a "selected tab"
property, Avalonia manages it internally) but fully replace `TabControl`'s own `ControlTemplate`
via a **NEW** named `ControlTheme x:Key="IndustryTabControlTheme" TargetType="TabControl"`**, same
technique already established for `Slider`/`NumericUpDown` in Phase 1 (full template replacement,
not `Style` setters — LAYOUT-SPEC §6 pitfall 1 explicitly names `TabItem` header as one of
FluentTheme's known template-priority traps). The replaced template is `Grid RowDefinitions="26,*"`:
row 0 = `Grid ColumnDefinitions="*,Auto"` holding the real `ItemsPresenter` (left, the 4 tab
headers) and a hand-built `.mini` status-chip `StackPanel` (right — NOT per-tab data, bound to
sibling `MainViewModel` properties via ambient `DataContext` inheritance, e.g.
`{Binding RxImage.DetectedModeDisplay}`, same as any other control in this template); row 1 = the
real `ContentPresenter` for the selected tab's body. `TabItem` itself gets its own **NEW**
`ControlTheme x:Key="IndustryTabItemTheme"` matching `.tabbtn2` (Barlow Condensed 600 11.5px,
letter-spacing .09em uppercase, padding `11,5,11,4`, `:selected` = 2px accent bottom border flush
to the tab's own full width, hover = `IndustryAccent700` text).

Outer `MainWindow.axaml` `Grid` becomes `RowDefinitions="26,81,26,*"` — wait, **only 4 literal
rows are still needed at the outer level**: menu(26)/header(81)/`TabControl`(fill, containing its
OWN internal 26+* split)/statusbar(25) — i.e. `RowDefinitions="26,81,*,25"`, and the `TabControl`
occupies the 3rd row with `Grid.Row="2"`, filling it, with its OWN template producing the 26px
strip + fill split internally. This is mathematically identical to a literal 5-row outer grid
(nothing outside the `TabControl` needs to address bands 3/4 by separate `Grid.Row` index) and
avoids duplicating tab-body-area sizing logic in two places.

**This is the single highest-risk item in this phase — auditor plan-review findings folded in
below (verdict EQUIVALENT-WITH-RISKS, 2 blockers, fixed here on paper; no second review round
needed).**

**[Blocker, FIXED] `Cards.axaml`'s bare `TabItem` selectors are missing the `:not(.Industry)`
exclusion.** `Cards.axaml:343` (`Selector="TabItem"`) and its `:selected` sibling were never
narrowed during Phase 1's collision-avoidance edit — every OTHER legacy bare-type selector got
`:not(.Industry)` (`Tokens.axaml:98` even has it for `TabItem` specifically), but `Cards.axaml`'s
own copy was missed because Phase 1's atom inventory never included `TabControl`/`TabItem` at all
(see the Summary section below). Without this fix, the old selector's `Height="20"`/`Padding
14,0`/`#E4E4E4` fill would win over `IndustryTabItemTheme`'s own setters (empirically-confirmed
Style-beats-ControlTheme precedence, `Atoms.axaml:12-14`), and band 3 would silently fail to
reach its mandated 26px. **Fix, part of this phase's own implementation work**: narrow both
`Cards.axaml:343`/`:selected` selectors to `TabItem:not(.Industry)` — same one-line pattern as
every sibling fix in that file. Safe for the Options window's own `TabControl` (Phase 6), which
won't carry `.Industry` until that phase ports it.

**[Blocker, FIXED] `RadioHeaderView.axaml`'s `ErrorMessage`/`MaintenanceMessage` bindings were
unaccounted for.** `RadioHeaderView.axaml:204-214` — two real, live, visibility-gated `TextBlock`s
sit BELOW the 3-card `Grid`, in the same outer `StackPanel`, not covered by §2 below (which only
maps the 3 cards). In a fixed-81px band these have nowhere to go without either overflowing into
the tab strip or being silently dropped. **Decision**: keep band 2 sized to the 3-card content only
(81px, per LAYOUT-SPEC); render `ErrorMessage`/`MaintenanceMessage` as an OVERLAY row, not a normal
document-flow sibling — a `Grid` wrapping the whole radio-header content with the message row in
the SAME cell as the 3-card row, `VerticalAlignment="Bottom"`, only visible (hence only consuming
space) when a message is actually set. This preserves the real feature without permanently growing
the fixed band, and matches how the current app already gates these (`IsVisible` bound to
non-null/non-empty).

**[Risk, documented] Required `TabControl`/`TabItem` template part names — must-verify empirically
at implementation time, not assumed.** Per plan-review: a replaced `TabControl` template needs an
`ItemsPresenter` named exactly `PART_ItemsPresenter` AND a `ContentPresenter` named exactly
`PART_SelectedContentHost` bound to `SelectedContent`/`SelectedContentTemplate` (`TabControl`
implements `IContentPresenterHost` and looks up its content host BY NAME — a wrong name silently
produces a blank tab body, no exception). `TabItem`'s own header presenter must bind
`Header`/`HeaderTemplate`, NOT `Content` (binding `Content` renders the whole tab BODY inside the
26px strip). Existing in-repo evidence of Avalonia's real named-part convention:
`ChromeOverrides.axaml:248,253`, `Cards.axaml:360`. This is the same class of surprise Phase 1's
`NumericUpDown`/`ButtonSpinner` implementation review already hit once — budget a real headless (or
real-window) verification pass for tab selection + content switching before considering this
phase's code-review "done," not just "it compiled."

**[Risk, resolved] The status-chip row must NOT live inside the shared `IndustryTabControlTheme`
template.** Originally planned as baked into the replaced template (bound to sibling
`MainViewModel` properties via ambient `DataContext`). Plan-review found this couples a REUSABLE
atom to one specific ViewModel — `OptionsWindowView.axaml`'s own separate `TabControl` (Phase 6)
would inherit the exact same chip row, bound against the WRONG `DataContext` (silent empty chips,
no error). **Fix**: `IndustryTabControlTheme` stays fully app-agnostic (strip + body split only,
no MainViewModel-specific content). The status-chip row is built as a plain sibling element
directly in `MainWindow.axaml` (not inside `Atoms.axaml`), layered in the SAME outer `Grid.Row` the
`TabControl` occupies, `HorizontalAlignment="Right" VerticalAlignment="Top" Height="26"` so it
visually sits in the strip without being part of the reusable theme:
```xml
<Grid Grid.Row="2">
  <TabControl Classes="Industry" Theme="{StaticResource IndustryTabControlTheme}"> ... </TabControl>
  <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" VerticalAlignment="Top"
              Height="26" Classes="Industry" Margin="0,0,6,0" Spacing="5"> <!-- status chips --> </StackPanel>
</Grid>
```
This also resolves the outer-grid-row-count question cleanly: `RowDefinitions="26,81,*,25"` (4
literal rows; the `TabControl`'s own template still internally splits its row into 26+* — see the
recommendation above, unchanged by this fix, just no longer sharing that internal template with
the chip row).

**[Risk, documented] Ambient `DataContext` resolution — confirmed sound, no longer a concern for
the chip row (moved out per the fix above) but still relevant for anything else built inside
`IndustryTabControlTheme`'s own template.** Plan-review reasoning: the chip row (now a
`MainWindow.axaml` sibling, not a template child) inherits `MainViewModel` directly from the
`Window` with no `ItemsPresenter`/`ContentPresenter` in the ancestor chain to break it, and no
compiled-binding mode is enabled in this project (`{Binding}` resolves via reflection at runtime,
not XAML-compile-time), so there's no additional binding-compilation hazard either.

---

## 1. Menu bar (band 1, h 26)

**NEW atom needed** (Phase 1 explicitly deferred this): `ControlTheme x:Key="IndustryMenuTheme"
TargetType="Menu"` + `ControlTheme x:Key="IndustryMenuItemTheme" TargetType="MenuItem"` — full
template replacement (Fluent's `MenuItem` has its own icon-column/chevron/min-height resources,
same pitfall class as everything else). Metrics: `Menu` padding `6,1,6,1`; each `MenuItem` header
padding `7,2,7,2` (`AtomsTokens.axaml` needs 2 more composite Thicknesses:
`IndustryMenuPadding`=`6,1,6,1`, `IndustryMenuItemPadding`=`7,2,7,2`), Barlow 400 12px, hover fill
`IndustryAccent100`/text `IndustryAccent800`, 1px gap between items.

Preserve EXACTLY: all **7** top-level `MenuItem`s — corrected count, plan-review caught this: File/
Configurations/Rig & PTT/Calibration/Tools/Help (6) PLUS the `Options...` item (`MainWindow.axaml:72`,
real `OpenOptionsCommand`) as its own 7th top-level entry, not nested under another menu — and every
child `MenuItem`'s `Header`/`Command`/`InputGesture`/`IsEnabled`/`ToolTip.Tip` binding, unchanged —
only `Classes`/visual template changes. Real commands (`TxControls.SelectImageCommand`,
`ExitCommand`, `OpenOptionsCommand`) and every disabled-placeholder
`ToolTip.Tip="{loc:Translate Options.NotImplemented.Help}"` item carry over verbatim. The `File`
menu's `<Separator/>` (`MainWindow.axaml:46`) needs its own themed look (not left at Fluent's
default) or it'll visually clash with the new menu chrome around it.

**Preserved product decision, restated for completeness**: LAYOUT-SPEC's Band-1 order includes a
`View` menu; this app deliberately dropped it (`MainWindow.axaml:34-40`'s own comment: nothing
closable exists to reopen). Same class of deliberate mockup-deviation as the `cfg:` text below —
keep dropped, don't resurrect for mockup-fidelity's sake alone.

Brand label "SSTV/CONSOLE" (mockup) — **current app has NO brand label at all**, just menus
directly. Add it fresh (Barlow Condensed, letter-spacing .05em, right-padding 12, the `/` in
`IndustryAccent`) — a new, non-databound literal, matching LAYOUT-SPEC exactly, no binding
conflict since nothing currently occupies that space.

**Real discrepancy, do not paper over**: the mockup's right-aligned group is `cfg: 20m-sstv-ic7300`
(mono, muted) + a `.mini` accent-outline callsign chip `DL2QSK · JO31`. The CURRENT app's own menu
bar comment (`MainWindow.axaml:75-80`) explicitly says the `cfg:`/grid-locator suffix were
DELIBERATELY DROPPED per direct user request ("neither has a real backing concept yet") — only the
plain callsign pill (`{Binding CallsignDisplay}`) remains. **Keep that decision**: port only the
callsign chip (re-skinned to `.mini` accent-outline, padding `8,1,8,1` per
`PADDING-NORMALIZATION.md` §1b's menu-bar override, font-size 12, letter-spacing .08em), do NOT
resurrect the `cfg:` text — this is a preserved product decision, not a wiring gap.

---

## 2. Radio header (band 2, h 81)

Outer `Grid` in `RadioHeaderView.axaml`: padding `9,7,9,7`, gap 10 (currently `Auto,*,260` columns
— LAYOUT-SPEC doesn't mandate exact column widths beyond "VFO auto-width, Favourites flex,
Transceiver 272 wide" — current `260` should become `272` to match exactly).

**VFO card** → `IndustryBlueprintBorderTheme` (Phase 1's plain-bordered blueprint-corner atom —
this is one of the design's 3 real blueprint sites), padding `14,5,14,5`, gap 14:
- Kicker "VFO A · RX · M1" → `TextBlock Classes="IndustryKicker wide"` (`.16em` variant — this is
  the ONE `.16em` site per Phase 1's own corrected site list) — **currently literal text with no
  real "A/RX/M1" state binding**; port as-is (literal), no regression, matches current scope.
- Frequency readout (`{Binding FrequencyDisplay}`, real) → mono 40px, `IndustryText`, `.000`
  tail-dimming deferred exactly as the current code's own comment already defers it (format
  mismatch, pre-existing, not a Phase 2 concern) — keep single-color for now.
- USB/LSB/FM → `IndustrySegOpt` (default `10,2,10,2` variant) — **currently plain `ToggleButton`s
  with no real sideband concept** (per the current file's own comment); port the SAME literal/
  unwired state onto the new atom, do not invent a binding.
- BW/SPLIT, STEP/RIT rows → `.mini` Border form ×4, literal (matches current — no backing concept).
- UTC clock (`{Binding UtcClockDisplay}`, real) → mono 15px + `.lbl`-styled " UTC" suffix.
- CAT link (`{Binding CatLinked}`, real, **one-way** `softPill`/`alertPill` swap — corrected, plan-
  review caught this was mislabeled two-way) → `.mini` accent-outline when linked / plain when not
  (matches current visual logic, re-skinned).
- Rig-meters pill → `.mini` Border, literal (matches current — no concept exists).
- **Dropped in current app, stays dropped**: the vertical divider border between columns B/C
  (explicit prior user feedback, per the file's own comment) — LAYOUT-SPEC's own Column C IS
  described with a 1px left border; this is a genuine, already-made product decision overriding
  the mockup — keep dropped, note the deviation in a code comment, don't silently resurrect it.

**Favourites card** → `IndustryGroupBoxTheme` (`IndustryGbPaddingFav` = `8,11,8,5`, gap 5). Preset
buttons (`{Binding Presets}` `ItemsControl`, real, `SelectCommand`/`FrequencyWithMode`/`Label`, AND
`CommandParameter="{Binding Preset}"` — plan-review caught this was missing from the binding list;
dropping it breaks preset recall entirely, not just a cosmetic gap) → `IndustryBtn36` +
`IndustryBtnSecondary`, two-line content (mono 12.5px freq/mode, Barlow 9.5px uppercase name, per
LAYOUT-SPEC — currently `Classes="value"`/`Classes="caption"`, re-skin only).

**Overflow behavior, stated explicitly (plan-review flagged this as unaddressed)**: `Presets` is an
unbounded, user-configured `ObservableCollection` (`RadioStatusViewModel.cs:131`, persisted,
default empty) — LAYOUT-SPEC's own 81px band budget assumes the mockup's fixed 7-preset example.
**Decision**: preserve the CURRENT app's existing `WrapPanel` overflow behavior (wraps to additional
rows past the mockup's assumed single row) rather than inventing new clip/scroll behavior for this
phase — a real user with many presets already sees band 2 grow past 81px today in the OLD design;
that's pre-existing product behavior, not a regression this phase introduces, and forcing a hard
clip to match the mockup's exact 7-preset screenshot would be a real feature loss, out of scope for
a pure visual-chrome port.
"Store current" → `IndustryBtn36` + `IndustryBtnPrimary` (accent-filled per mockup, matches current
`Classes="success"` intent) — **currently has NO `Command=` bound at all** (a pre-existing STUB per
the GUI wiring survey); port the same unwired state, do not invent a binding. Hint caption + Edit
list/Import/Scan → `.mini` Border form, literal caption + 3 STUB `.mini` buttons — **all 3 already
have no `Command=`** in the current file (matches survey's STUB finding), port as-is.

**Transceiver card** → `IndustryGroupBoxTheme`, width **272** (was 260 — align to spec exactly).
Receiving/Halt (`{Binding IsReceiving}` two-way toggle + `HaltReceivingCommand`, both real) →
`IndustryBtn26` + `IndustryBtnPrimary` (Receiving, checked state) / `IndustryBtnSecondary` (Halt) —
current file's own comment says Receiving intentionally uses the SAME blue-filled palette as a
primary button, not a green state color, sampled directly against mockup pixels; preserve that
exact choice, do not "improve" it toward a green/red status-pair (a DIFFERENT part of this same
codebase, `Cards.axaml`'s `ToggleButton.receiving`, uses green deliberately for a DIFFERENT reason
per ITS OWN comment — these are two intentionally different prior decisions in different places,
don't conflate them). RX level slider (literal `Value="-14.2"`, no binding — matches current STUB
state) and TX level (`{Binding TxVolumePercent}`, real, two-way) → **both are display-only meters
per LAYOUT-SPEC's own real example (fill 63% ≠ thumb 78%)**, so use Phase 1's Grid-based
`IndustryMeter`/`IndustryMeterTrack`/`IndustryMeterFill`/`IndustryMeterMarker` pattern for RX
(display), but TX level is REAL and interactively bound two-way (`{Binding TxVolumePercent}`) —
**use `IndustrySliderTheme` for TX specifically** (it's a genuine interactive control, not a
display-only meter, despite both looking visually similar) — this is the one place in this band
where the display-vs-interactive split matters and must not be flattened to "both are meters." TX
slider's `Minimum="0" Maximum="100"` (`RadioHeaderView.axaml:198`) are load-bearing local values —
the current file has its own explicit warning comment (`:374-379`) that dropping/overriding this
range silently clamps and writes back a wrong value to the VM; preserve both attributes verbatim
under the new `IndustrySliderTheme`, don't let the theme's own defaults (if any) replace them.

---

## 3. Tab strip (band 3, h 26) — see §0 for the `TabControl`/`TabItem` template mechanism

Outer strip padding `6,0,6,0` (`PADDING-NORMALIZATION.md` §3 Band-3 — plan-review caught this was
never stated).

4 tabs: Receive/Transmit/Gallery/Logbook — LAYOUT-SPEC's own mockup only shows 3 (Receive/Transmit/
Gallery); Logbook is real, shipped, established functionality with no mockup coverage. **Mechanical
extension**: Logbook gets the exact same `IndustryTabItemTheme` styling as the other 3, one more
`TabItem Header="{loc:Translate MainWindow.Tabs.Logbook}"` — no special-casing needed, the atom
doesn't care how many tabs there are.

Right-aligned status-chip run (as a `MainWindow.axaml` sibling per §0's fix, NOT baked into the
shared `IndustryTabControlTheme`): `AUTO-DETECT` · mode-detect chip (accent-filled `.mini` —
`SCOTTIE 1 · VIS 60 · 98.4%` pattern) · `SYNC LOCK` · `SLANT` · `SNR` · `AUTOSAVE ON`
(accent-outline). **Real discrepancy**: none of these currently exist ANYWHERE in the live app's
tab-strip area — new UI surface, not a re-skin.

**Data-source resolution (plan-review verified each field against `spec/16-gui-wiring-survey.md`,
resolving the original hedge)**:
- Mode-detect: the MODE portion is REAL (`RxImage.DetectedModeDisplay`); the VIS-code portion is
  REAL; the **confidence percentage (`98.4%`) does NOT exist anywhere** — wire mode+VIS to the real
  bindings, render confidence as a literal placeholder (or omit the `%` clause entirely if that
  reads better without a fake trailing number — implementer's call at build time).
- `SYNC LOCK`: lock STATE is inferable from the real `AutoCorrectDisplay`'s 4-way Locked readout;
  the lock DURATION (`41 s`) has no backing property — state real, duration literal.
- `SLANT`: fully REAL (`RxImage.SlantPpmStatusBarDisplay`).
- `SNR`: fully FAKE-LIVE, confirmed no per-line SNR computation exists anywhere in this codebase
  (status bar's own `SnrValue` literal is the same acknowledged gap) — literal placeholder, no
  invented binding.
- `AUTOSAVE ON`: no autosave feature/state exists to check — literal placeholder.

This duplicates SLANT/SNR that ALSO appear on the status bar (band 5) — not a mistake, LAYOUT-SPEC's
own mockup shows both (§5 lines 179 and 218 both carry slant/SNR), just worth noting the visible
surface doubles up. Reuse-real-else-literal-placeholder is this codebase's established FAKE-LIVE
convention (`spec/16-gui-wiring-survey.md`'s own classification scheme), not a new policy invented
for this phase.

---

## 4. Status bar (band 5, h **25** — current file hardcodes `Height="24"`, corrected here)

Outer `Border` (top border only) → padding `8,4,8,4`, gap 3, ALL children become `.mini` Border-form
chips (currently a mix of `Classes="readout"`/`"pill"`/`"accentPill"`/`Ellipse.led` — re-skin every
one to `.mini`, preserving every binding):
- Receiving pill (`{Binding RadioStatus.IsReceiving}` → `accentPill`/plain) → `.mini.active` toggle.
- TX-inhibit (`Ellipse.led` + `{Binding TxControls.ErrorMessage}` converter) → LAYOUT-SPEC's status
  bar doesn't show a TX-inhibit chip at all in its static render — **keep it anyway**, real wired
  alert state per direct prior user request (current file's own comment: "the concept/wiring is
  real and kept per direct user request"), same class of deliberate-deviation-from-mockup as the
  VFO card's dropped divider — note in a code comment, don't silently drop a real feature to match
  a static mockup screenshot.
- **Deviation, keep current behavior**: LAYOUT-SPEC merges frequency+mode+memory into ONE chip
  (`14.230.00 USB · M1`); the current app renders these as 3 separate `TextBlock`s. Plan-review
  flagged the design doc's original 1:1 mapping to 3 separate `.mini` chips as an unflagged
  deviation from the mockup — it's the RIGHT call (merging live-updating independent bindings into
  one interpolated string is a real behavior change, not a pure re-skin), just needs to be stated
  explicitly rather than silently ported: keep 3 separate chips, don't merge.
- Frequency/Mode (`RadioStatus.FrequencyDisplay`/`ModeDisplay`, real) → `.mini` chips.
- `MemoryTag` (literal) → `.mini`, unchanged.
- `RxImage.DetectedModeText`/`LineProgressText`/`SlantPpmStatusBarDisplay`/
  `BufferedSampleCountStatusBarDisplay` (all real) → `.mini` chips.
- `SnrValue` (literal, FAKE-LIVE per the file's own comment — "true per-line SNR still doesn't
  exist") → `.mini`, unchanged, still literal.
- **Missing from current app entirely**: LAYOUT-SPEC's `DISK 14.2 GB` chip — no disk-space binding
  exists anywhere in this codebase today. Add as a literal placeholder (matching the FAKE-LIVE
  precedent already established for `SnrValue`/`MemoryTag` in this exact bar), do not invent a real
  disk-space feature in this phase (out of scope — pure visual chrome port).
- Right-docked: `RxHistory.FramesTodayDisplay`/`Logbook.LogSizeDisplay` (both real) → `.mini` chips.

---

## Localization workload (plan-review caught this was unaddressed — `NoHardcodedAxamlStringsTests`
applies to every `.axaml` under `src/ScanlineStudio.UI` INCLUDING `Styles/`, no allowlist)

Every new literal chip/label this phase introduces needs an `en.json` key via `{loc:Translate}`,
same as everywhere else in this codebase — none of these are already localized because none of
them exist yet: brand label `SSTV/CONSOLE`, tab-strip's `AUTO-DETECT`/`SYNC LOCK`/`AUTOSAVE ON`,
status bar's `DISK 14.2 GB` placeholder. (Real-bound values like frequency/mode/callsign already go
through their existing ViewModel-formatted strings, no new key needed there — this list is only the
literal chrome text this phase adds.) Budget this as real Phase 2 work, not an afterthought — the
phase's own `dotnet test` gate (`NoHardcodedAxamlStringsTests`) fails outright on a bare
`Text="AUTO-DETECT"`.

## Summary of NEW atoms this phase must build (beyond Phase 1's inventory)

1. `IndustryMenuTheme`/`IndustryMenuItemTheme` (explicitly deferred by Phase 1 to this phase).
2. `IndustryTabControlTheme`/`IndustryTabItemTheme` (NOT deferred by name in Phase 1's doc — Phase
   1's own atom list never included `TabControl`/`TabItem` at all; this is a genuinely new gap
   found while drafting this design, not a previously-flagged deferral — flag this explicitly for
   plan-review, since it means Phase 1's "18 atoms" inventory was incomplete by one control family).
3. Two new composite `Thickness` resources for the menu (`IndustryMenuPadding`,
   `IndustryMenuItemPadding`), sourced from LAYOUT-SPEC §5 Band 1 / `PADDING-NORMALIZATION.md`.

## Preserved product decisions (deliberate deviations from the mockup — keep, don't "fix")

- Menu bar: no `cfg:`/grid-locator text, callsign-only chip.
- VFO card: no vertical divider between columns B/C.
- Transceiver: Receiving uses primary-blue, not green (a different, deliberate choice from
  `Cards.axaml`'s own separately-used green `.receiving` convention elsewhere in the app).
- Status bar: TX-inhibit LED shown even though the mockup's static render doesn't happen to show one.

## One pre-existing inconsistency to be aware of while porting, not to silently "fix"

`RadioHeaderView.axaml:188`'s Halt button uses `Classes="halt"` (the old design's red destructive-
action class) while the adjacent code comment (`:180-181`) says Halt is deliberately a plain gray
secondary button, not a red one. This design doc's own §2 mapping follows the COMMENT (→
`IndustryBtnSecondary`), not the literal current `Classes="halt"` markup — flagging this explicitly
since it's a real live contradiction in the file being ported, not this phase inventing a change;
worth a one-line note in the ported code too so it isn't rediscovered as a mystery later.
