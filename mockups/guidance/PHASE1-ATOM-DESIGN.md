# Phase 1 — Atom design (review draft, round 2 — no code yet)

Design for `src/ScanlineStudio.UI/Styles/Atoms.axaml` (new, additive, loaded after
`ChromeOverrides.axaml`), the reusable primitive layer every later phase composes. Sources:
`mockups/guidance/LAYOUT-SPEC.md` §3-4, `mockups/guidance/PADDING-NORMALIZATION.md` (exact
Avalonia paddings — cited, not re-derived), `src/ScanlineStudio.UI/Styles/AtomsTokens.axaml`
(Phase 0's resource vocabulary), `src/ScanlineStudio.UI/Styles/Cards.axaml` +
`ChromeOverrides.axaml` (existing patterns/collision list, existing FluentTheme pitfalls already
paid for once).

**Round-2 revision note:** round-1 auditor plan-review verdict was NOT EQUIVALENT — 5 blockers,
all resolved below with two empirically-confirmed probes (Avalonia `Style`-vs-`ControlTheme`
precedence, and `:nth-child` on plain container children — both ground-truth, not inferred) backing
the two biggest reversals (item 0b's collision mitigation, item 3's row-separator mechanism).
Corrections are marked inline at each item; items 4, 6, 6-12, 17 not called out as "REVISED" had
only smaller fixes or were confirmed sound as originally drafted.

**Off-scope note (not fixed, flagged only, per round-2 review):** `Cards.axaml:103`'s `Style
Selector="Control:disabled"` (`ToolTip.ShowOnDisabled`) likely matches nothing at all — Avalonia
type selectors are exact-type and no bare `Control` instances exist anywhere in this app, so the
Options window's "hover a disabled control to see why" tooltips may already be silently non-functional
today. Unrelated to this design; worth a one-line fix whenever that area is next touched, not chased
here.

---

## 0. Governing architecture decision — read this before any item below

**`ChromeOverrides.axaml` proves two DIFFERENT Avalonia mechanisms are already in use in this
codebase, and they have opposite additive-safety properties:**

1. **`Classes`-scoped named `ControlTheme`** (e.g. `Cards.axaml`'s `RadioButton.seg`,
   `Tokens.axaml`'s `x:Key="ScanlineStudioTxToggleButtonTheme"` applied via
   `Theme="{StaticResource ScanlineStudioTxToggleButtonTheme}"` at
   `TxControlsPaneView.axaml:238`) — **additive-safe.** Only elements that opt in (via a `Classes`
   selector match, or an explicit `Theme=` attribute) get the new look; every other instance of
   that control type anywhere else in the app is untouched.
2. **`TargetType`-keyed implicit `ControlTheme`** (`ChromeOverrides.axaml`'s
   `<ControlTheme x:Key="{x:Type Slider}" TargetType="Slider">` and the `{x:Type ButtonSpinner}`
   one right after it) — **globally applies to every instance of that control type in the whole
   app, with no opt-in.** Later-loaded wins.

**Consequence for Phase 1: any atom whose new look must coexist with old-design instances that
still need the OLD look (true for every control the old design also uses — `Slider`,
`ButtonSpinner`/`NumericUpDown`, `Expander`, `MenuItem`, `ScrollBar`, `CheckBox`, `RadioButton`)
MUST use mechanism 1, never mechanism 2, or Phase 1 silently re-skins every live old-design
instance the moment it loads — the exact "uncontrolled look-flip" the additive strategy exists to
prevent, just via `TargetType`-keying instead of a colliding resource name (which round-1 review
already caught for names; this is the same failure mode for the OTHER thing FluentTheme lets you
key globally).**

Concretely, live old-design `Slider` instances that would break: `RadioHeaderView.axaml`'s RX/TX
level sliders, `TxControlsPaneView.axaml`'s Drive slider, `TxImageEditorPaneView.axaml`'s 6
adjustment sliders. Live `NumericUpDown` instances: waterfall Bins/px, Start/Span steppers and
others. All still on the old look until their own phase (2, 4, 5) ports them.

**Rule adopted for every item below that needs a different look than the old design currently has:
define a NAMED `ControlTheme` (`x:Key="Industry*Theme"`), never a bare `{x:Type X}` key. Every
NEW-design call site sets `Theme="{StaticResource Industry*Theme}"` explicitly** — this makes each
of Phases 2-6's future markup changes an explicit, auditable opt-in per control instance, exactly
mirroring the codebase's own already-proven `ScanlineStudioTxToggleButtonTheme` pattern. This is a
correction to the plan's Phase 1 one-liner ("Full ControlTemplate replacements... wherever
FluentTheme's template-priority pitfall applies") — replacement is right, *global* replacement is
not.

### 0b. The OTHER direction — old→new leakage (round-1 auditor finding, confirmed real by a live probe)

The analysis above only covers new-design ControlThemes leaking onto old-design instances. The
reverse direction is real too and hits MORE atoms: `Cards.axaml`/`Tokens.axaml`/
`ChromeOverrides.axaml` are full of **bare-type `Style` selectors** loaded into
`Application.Styles`, and a `Style` setter and a `ControlTheme` setter are NOT the same precedence
tier in Avalonia.

**Empirically confirmed, not assumed**: a throwaway headless probe (`Window` + a bare `Style
Selector="Button" { Height=19 }` + a second `Button` with an explicit `Theme=` pointing at a named
`ControlTheme` setting `Height=17`) showed BOTH buttons resolve to `Height=19` after a real layout
pass (`window.Show()` + `Dispatcher.UIThread.RunJobs()`). **The old bare `Style` wins over the new
named `ControlTheme`, every time.** This means naming a `ControlTheme` (per 0a above) is necessary
but not sufficient — the old selector must also be prevented from matching new-design instances.

**Collision table** (every old bare-type selector a new-design atom would otherwise inherit from):

| Old selector | File:approx line | Sets | New atom it would silently override |
|---|---|---|---|
| `Button` | `Cards.axaml:107` | Height 19, Padding 10,0, Background, BorderBrush, BorderThickness, CornerRadius | `.mini` Button chips (want 17 tall), all 6 button heights |
| `Button:pointerover/:pressed /template/ ContentPresenter` | `Cards.axaml:116,120` | old hover/press blue | every new button's hover/press |
| `ToggleButton` | `Cards.axaml:158` | Height 19, Padding, Background, CornerRadius | `.mini` ToggleButton chips, item 16's toggle |
| `ToggleButton:checked /template/ ContentPresenter` | `Cards.axaml:171` | old pressed-blue fill | every checked `.mini` ToggleButton chip (implementation-phase code-review gap — missing from this table's original pass, fixed alongside the implementation itself) |
| `Slider` | `Cards.axaml:294` | MinHeight 18, Foreground `#0F62A8` | `IndustrySliderTheme` (MinHeight 18 breaks the 17px `.r2` row; old blue can land on the fill if the template uses `{TemplateBinding Foreground}`) |
| `TextBox, ComboBox, NumericUpDown` | `Cards.axaml:260`, `ChromeOverrides.axaml:187` | Height 19, Padding 4,0, FontSize 11.5, Background, BorderBrush | `.input` (24/26), any `NumericUpDown`-based stepper |
| `NumericUpDown` | `Cards.axaml:275`, `ChromeOverrides.axaml:202-214` | mono font, Height 19, right-align | stepper (once item 5 below reuses `NumericUpDown`) |
| `Menu` | `Cards.axaml:378` | old gray background | `IndustryMenuTheme` (Phase 2) |
| `Button`/`ToggleButton`/`ComboBox`/`ComboBoxItem`/`TextBox`/`CheckBox`/`TabItem` | `Tokens.axaml:66,70,74,78,82,85,93` | CornerRadius, Padding | same set as above + CheckBox |
| `Expander` | `Cards.axaml:400` | old `ControlFaceColor` background, old border | harmless as currently scoped — item 16 abandons `Expander` for the "＋ ADVANCED TIMING" header in favor of a plain disclosure-toggle pattern using `.gbt` typography, so nothing in Phase 1 targets `Expander` — listed here (round-2-of-round-2 finding N7) so it isn't silently rediscovered if a later phase reconsiders using it |

**Mitigation adopted**: narrow every one of these old selectors to exclude new-design instances via
a marker class, e.g. `Style Selector="Button:not(.Industry)"`. **Every control instance built by
this phase's atoms (and every later phase's call sites using them) carries an `Industry` class.**
This is a small, mechanical, reviewable edit to the 9 selectors in the table above, done as part of
Phase 1 (touches old files, but only adds a `:not(.Industry)` suffix — doesn't change any old-design
behavior, since no old-design element carries that class). Concretely: `IndustryGroupBox`,
`IndustryMini*`, `IndustryStepper`, `IndustrySliderTheme`'s target elements, `IndustryBtn*`,
`.IndustryInput`-classed fields, and every other new atom below add `Classes="Industry ..."` (or,
for a `ControlTheme`'s own `TargetType` root, the `x:Key`-referencing instance in later-phase XAML
adds the class) so the `:not(.Industry)` exclusion actually protects it.

Avalonia type selectors are exact-type (not polymorphic), so this table is bounded — it does NOT
also catch `RepeatButton` (inside a stepper template), `RadioButton`, `MenuItem`, or `ScrollBar`,
none of which have a colliding bare-type `Style` in the old files today.

**`:nth-child` on plain (non-`ItemsControl`) container children — also empirically confirmed**
(relevant to item 3 below): a second throwaway probe (`StackPanel` with 3 hand-added `Border`
children, `Style Selector="StackPanel.test > Border:not(:nth-child(1))"`) showed child 1 unaffected
and children 2/3 both getting the rule — `:nth-child` works on a plain `StackPanel`'s direct
children, not just `ItemsControl`-generated containers. See item 3.

---

## 1. Group box (`.gb`)

**Mechanism — corrected (round-2 auditor finding):** LAYOUT-SPEC §3's own recipe — a `Grid` with a
bordered `Border` plus a second `Border` (background `IndustryBg`, negative-margin,
`HorizontalAlignment="Left" VerticalAlignment="Top"`) holding the title `TextBlock`. Package as a
**`TemplatedControl` deriving `HeaderedContentControl`** (for free `Header`/`Content`/
`HeaderTemplate` plumbing), NOT a `UserControl` as originally drafted — a `UserControl`'s XAML root
IS its `Content`, so a caller writing `<IndustryGroupBox>payload</IndustryGroupBox>` would overwrite
the chrome entirely rather than wrapping it. Deriving `HeaderedContentControl` and replacing ITS
template with the notch-over-border `Grid` recipe (title bound to `Header`, body to `Content`) gets
the caller-facing API right while still fully controlling the visual (the original objection — "its
default template has no notch mechanism" — is moot once the template is replaced, same as every
other `ControlTheme` replacement in this design). `Classes="blueprint"` opt-in for corner marks
(item below).

**Resources:** `IndustryDivider` (border), `IndustryBg` (interior + notch background),
`IndustryAccent700` (title text), `IndustryHeadingFontFamily` (title, Barlow Condensed 600).

**NEW composite `Thickness` resources needed** (from `PADDING-NORMALIZATION.md` §1/§2 — these are
the "deferred to Phase 1" resources Phase 0 flagged, sourced here, not invented):
- `IndustryGbPadding` = `7,11,7,6` (default — §1's `.gb` row)
- `IndustryGbPaddingWide` = `8,11,8,6` (Spectrum·waterfall/Decode-activity/Received — §2 Band-2/
  centre-column/Gallery rows; bottom stays 6 here, NOT 7)
- `IndustryGbPaddingWideDeep` = `8,11,8,7` (Incoming-frame/Editor — same family, bottom
  overridden to 7 per §2's own explicit note)
- `IndustryGbPaddingFav` = `8,11,8,5` (Favourites/Transceiver — §2 Band-2 rows, gap 5/3
  respectively, gap is a separate `Spacing` value not part of this Thickness)
- `IndustryGbPaddingNested` = `6,10,6,5` (Previous frames/Insert field/Text style/Saved
  templates — §2's nested-group-box rows; note their `Spacing` differs per site — 4/2/2/3 — so
  `Spacing` stays a per-instance property set at each call site, not baked into this shared
  Thickness)
- `IndustryGbtPadding` = `5,0,5,0` (title notch — §1's `.gbt` row)

**Blueprint corner-mark decorator — corrected across round-2 (color/geometry) AND round-2-of-round-2
(site count, N1) auditor review, all against the raw CSS source (`_ds/.../styles.css:83-95`), not
LAYOUT-SPEC's own prose (which has the same errors):**
1. **Color**: `IndustryTextMuted55` (`#7D7E7F`), NOT `IndustryAccent`. LAYOUT-SPEC.md:123 says
   "accent-coloured" — wrong against the source CSS (`color-mix(text 55%)`).
2. **Site count**: exactly **3** real sites inside `2a` — the VFO block (`SSTV Console.dc.html:92`,
   opens `class="blueprint"` and line 93 IS the four `<i class="corner tl/tr/bl/br">` children —
   an earlier pass of this doc wrongly asserted the VFO block has none, auditor-caught, corrected
   here), Incoming-frame (`:252`), and Editor (`:428`). LAYOUT-SPEC.md:123 says "Four... VFO block,
   Incoming frame, and the TX Editor" — the site LIST is right, only its count word ("Four") is
   wrong; corrected here to 3.
3. **Geometry**: marks are 11×11px, 1px stroke, positioned OUTWARD at `-6px` from the box edge (the
   crossing point sits at `5px` past the border) — not flush/inset as the original draft implied.

Build as a standalone `IndustryBlueprintCorners` control (4 small `Path`/`Line` "+" crosses) —
`Classes="blueprint"` opts a container in at all 3 real sites: `IndustryGroupBox` for the group
box's own 2 uses (Incoming-frame, Editor) AND the plain bordered `TemplatedControl` used for the
VFO block (item 1's group-box `TemplatedControl` note covers the general shape; the VFO block
itself is NOT a group box — no `.gbt` title notch — just a plain bordered container that also
wants the same corner-mark decorator, so `Classes="blueprint"` must be usable independently of
`IndustryGroupBox`, not hardcoded as one of its own internal parts).

**Risk to carry into implementation, not resolved here:** the corner marks' `-6px` outward
positioning means they extend BEYOND the group box's own layout bounds. If an ancestor container
(e.g. a `Grid` column, a `ScrollViewer`) has `ClipToBounds="True"` or Avalonia's own default
clipping behavior for the relevant panel type, the marks could be silently clipped off — verify in
a real (non-headless) window render once Incoming-frame/Editor are actually ported (Phases 3-4),
not assumed safe here.

**Pitfall:** none of the FluentTheme template-priority class — this is bespoke composition, no
existing control being reskinned.

---

## 2. `.mini`/`.chip`/`.sbc` chips

**Decision, revised (round-2 auditor finding — Phase 1 needs a stated verification path, and
5 atoms including `.chip`/`.sbc` had no Phase 1-5 call site at all): build `.mini` fully (all 3
forms below) here; DEFER `.chip`/`.sbc` to Phase 6**, which is the first phase that might actually
need them (Options/Logbook extrapolation, no direct mockup). Building either now means shipping an
atom nothing exercises until Phase 6 anyway — same "don't build what nothing consumes yet"
discipline Phase 0 already used once for the composite-Thickness/hatch-brush deferral. If Phase 6
never needs a denser/wider chip than `.mini`, they're simply never built — no wasted work either
way.

**`.mini`'s 3 forms, all sharing the 17px-tall metric** (`5,1,5,1` padding per
`PADDING-NORMALIZATION.md` §1, mono 500 10px). Every instance carries BOTH `Industry` (item 0b's
old-style-exclusion marker, required for the `:not(.Industry)` mitigation to actually fire on
`Button`/`ToggleButton`-based forms) AND `IndustryMini` (the metrics selector target) —
`Classes="Industry IndustryMini"`, not `IndustryMini` alone:
- **Static `Border`** (`Classes="IndustryMini"` — no `Industry` needed here specifically, since
  `Border` isn't one of item 0b's collision-table selectors, but harmless to include for
  consistency) — status displays (status bar, Sync-lock chip, etc.).
- **`Button`** (`Classes="Industry IndustryMini"`, needs its own `ControlTheme` since `Button`'s
  default Fluent template doesn't produce a plain bordered chip at 17px without one, AND needs the
  `Industry` marker since `Button` IS one of item 0b's collision-table selectors) — mode-grid
  pills, macros, BOTH/SPEC/WF toggle-as-button sites, EDIT LIST/IMPORT/SCAN M1-M3.
- **`RadioButton`/`ToggleButton`** (`Classes="Industry IndustryMini"`, `ControlTheme`, `:checked`
  state = accent fill per LAYOUT-SPEC's "active variant"; `Industry` needed since `ToggleButton` is
  also in item 0b's collision table) — segmented-style single-select chip groups (e.g. filter
  ALL/TODAY/14 MHz/UNLOGGED/FLAGGED in Gallery, which LAYOUT-SPEC's Gallery section implies is a
  `.mini`-styled filter row, not a `.seg`).

Since `Classes="IndustryMini"` must apply IDENTICAL metrics across 3 different base control types,
implement as 3 separate `ControlTheme`s sharing the SAME literal values (no cross-type style
inheritance in Avalonia) rather than trying to force one shared style across heterogeneous
`TargetType`s — accept the duplication, it's ~6 lines per form.

**Variants** (LAYOUT-SPEC §4): active (`Background="{StaticResource IndustryAccent}"`,
`Foreground="{StaticResource IndustryBg}"`), accent-outline (`BorderBrush="{StaticResource
IndustryAccent}"`, `Foreground="{StaticResource IndustryAccent700}"`, no fill) — both as additional
`Classes` (`IndustryMini.active`, `IndustryMini.accentOutline`) layered on the base form.

**Menu-bar callsign override** (single site, `PADDING-NORMALIZATION.md` §1b): the `DL2QSK · JO31`
chip at Band 1 is a `.mini` instance with `padding:1px 8px` (Avalonia `8,1,8,1`, not the class
default `5,1,5,1`), font-size bumped to 12, letter-spacing `.08em`. One-off — apply as a per-instance
`Padding`/`FontSize`/`LetterSpacing` override at that single Phase 2 call site, not a named
resource (matches the per-instance-override policy stated in items 7/10/11 for genuinely one-off
site variations — see the Summary section's explicit statement of this policy).

**Resources:** `IndustryDivider`, `IndustryMonoFontFamily`, `IndustryAccent`,
`IndustryAccent700`, `IndustryBg`. **New:** `IndustryMiniPadding` = `5,1,5,1` (Thickness, from
`PADDING-NORMALIZATION.md` §1).

**Pitfall — corrected diagnosis (round-2 auditor finding):** the real height-fight risk is NOT a
Fluent `MinHeight` resource — it's `Cards.axaml:107`'s explicit `Height="19"` `Setter` on the bare
`Button`/`ToggleButton` selectors (see item 0b's collision table). This is fully subsumed by item
0b's `:not(.Industry)` mitigation: once these `Button`/`ToggleButton`-based `.mini` forms carry the
`Industry` class, `Cards.axaml`'s `Height="19"` selector no longer matches them at all, and the
named `ControlTheme`'s own `Height="17"` applies cleanly. No separate fix needed here beyond
applying item 0b's rule.

---

## 3. Rows (`.r2`)

**Mechanism:** NOT an `ItemsControl`/`ItemsRepeater` with an item-separator template — every real
`.r2` site in the mockup is a handful of individually-authored, non-uniform rows (different
label/value pairs, some with a stepper or track instead of plain text), not a homogeneous
data-bound list. Matches how the CURRENT app already does `.r2`-equivalent rows (`Grid.kv` pattern
in `Cards.axaml`, individually authored `Grid` rows per `PaneView.axaml`).

Design: `IndustryRow` as a `Classes`-scoped `Style` on a container (`Grid` or `DockPanel`,
`HorizontalAlignment` space-between semantics via `ColumnDefinitions="Auto,*,Auto"` or a
`DockPanel` with the label docked left and value docked right) — a `Style`, not a `ControlTheme`
(no template replacement needed, it's a layout/typography convention on plain containers, same
class of primitive as `Cards.axaml`'s existing `Grid.kv`).

**Row-separator mechanism — REVISED (round-2 auditor finding + empirical probe, reverses this
item's original recommendation).** CSS's `.r2 + .r2` adjacent-sibling selector genuinely has no
direct Avalonia equivalent, but the original draft's rejection of `:nth-child` was based on a wrong
premise. **Empirically confirmed**: a throwaway probe (`StackPanel` with 3 hand-added `Border`
children — no `ItemsControl`, no data templating — plus `Style Selector="StackPanel.test >
Border:not(:nth-child(1))"`) showed the rule correctly applies to children 2 and 3 only, not child
1. **`:nth-child` works on a plain, hand-authored container's direct children** — it does NOT
require converting group-box bodies into `ItemsControl`s, which was the entire objection to option
(b) in the original draft.

**Adopted: `Style Selector="StackPanel.IndustryRows > Border.IndustryRow:not(:nth-child(1))"`
(or the `Grid`/`DockPanel` equivalent) as the DEFAULT mechanism** — a group box whose body is
uniformly `.r2` rows gets the top-rule automatically, zero per-row authoring.

**Real semantic caveat, stated explicitly (not just noted) because several actual `2a` group boxes
need it**: CSS `.r2 + .r2` means "rule iff the immediately preceding sibling is ALSO `.r2`", while
`:nth-child` counts ALL children regardless of type or visibility — so for any group box whose body
MIXES `.r2` rows with non-row content (a chip grid, a button row, a `<details>` block — e.g. the
Mode card's row stack after its mode-grid pills, or Sync&slant's rows interleaved with its Re-sync/
Reset button row and the Advanced-timing disclosure), blind `:nth-child` numbering gives WRONG
results (a non-`.r2` sibling shifts every subsequent index, and a rule can land on a row that's
actually first-after-a-non-row-sibling but not literally child index 1). **Mitigation: every
`IndustryRow` also accepts an explicit `Classes="ruled"`/`Classes="unruled"` override** that a
markup author sets at the specific rows in a mixed-content card where the automatic `:nth-child`
count would be wrong — this keeps the common case (pure row stacks) zero-effort while giving mixed
stacks an explicit, auditable override rather than a silently wrong automatic rule.

**Resources:** `IndustryTextMuted62` (`.lbl`), `IndustryText`, `IndustryMonoFontFamily` (`.v2`),
`IndustryRowRule6`/`IndustryRowRule7` (top-rule brush — LAYOUT-SPEC distinguishes `.r2`'s
`RowRule6` from `.row`'s `RowRule7`; `.row` is confirmed unused in `2a` per
`PADDING-NORMALIZATION.md`, so only `IndustryRowRule6` is actually load-bearing — **both already
exist in `AtomsTokens.axaml` since Phase 0, nothing new to build here**, corrected from the
original draft's suggestion to add `IndustryRowRule7`). `IndustrySpacingBase` (gap 6).

**Pitfall:** LAYOUT-SPEC §6 pitfall #9 (Barlow's line-box is taller than CSS line-height at these
sizes) — set `LineHeight="16.5"` explicitly on the row's `TextBlock`s, confirmed still relevant,
not specific to any one atom.

---

## 4. Kicker text

**Mechanism:** pure `Style Selector="TextBlock.IndustryKicker"` (mono 10px, letter-spacing
computed per LAYOUT-SPEC §2's px-per-em formula, `IndustryAccent700`, uppercase — note the mockup's
kicker text is already-uppercase in source, e.g. "SNR PER LINE", so no `TextTransform` needed if
markup authors just type it uppercase; if a bound/localized value might arrive mixed-case, add
`Style Selector="TextBlock.IndustryKicker"` `Typography`/manual `.ToUpperInvariant()` at the
ViewModel boundary instead of a text-transform hack — Avalonia `TextBlock` has no native
`text-transform` equivalent).

**Resources:** `IndustryAccent700`, `IndustryMonoFontFamily`. LetterSpacing — **sites corrected**
(round-2 auditor finding: the original draft had the two variants backwards). Inside `2a`: `.16em`
at exactly ONE site — the VFO kicker (`SSTV Console.dc.html:95`) — and `.14em` at 6 sites, NOT 7
(round-2-of-round-2 finding N5, corrected): Signal-quality "SNR PER LINE"/"LUMINANCE HISTOGRAM"
(`:213`/`:216`), Decoder-trace "DECODER TRACE" (`:310`), Output "POWER / ALC METER" (`:409`),
Outgoing-metadata "IN THE SIGNAL"/"BURNED INTO THE PICTURE" (`:414`/`:418`). The Session-frames
row's mode tag (`:354`, `<span class="mini" style="border:0;padding:0">`) is NOT a kicker site —
it has no `letter-spacing`/accent-color styling at all, just a borderless `.mini`; applying
`IndustryKicker` there would wrongly recolor/space it. Build `IndustryKicker` at `.14em` (the
common case) and `IndustryKicker.wide` at `.16em` (the VFO-only case) — both variants ARE needed,
not "add only if a site needs it" as the original draft hedged. (The "＋ ADVANCED TIMING"
disclosure header is NOT a kicker-text site — see item 16's correction, it uses `.gbt`
group-box-title typography instead.)

**Pitfall:** none — pure text style, no template.

---

## 5. Stepper — REVERSED: reuse `NumericUpDown`, not a new control

**Round-2 auditor finding: the original draft's conclusion was wrong, disproved by this very
codebase's own existing code.** `ChromeOverrides.axaml:83-84` already applies a NAMED theme
(`Theme="{StaticResource ScanlineStudioSquareThumb}"`) to a `Thumb` sitting INSIDE a replaced
`ControlTemplate` — i.e., per-instance theming of a template-internal control is already a proven,
working pattern in this codebase. The same shape solves the stepper: a named `ControlTheme
x:Key="IndustryStepperTheme" TargetType="NumericUpDown"` whose replaced template either (a) hosts
`<ButtonSpinner Name="PART_Spinner" Theme="{StaticResource IndustrySpinnerTheme}">` or (b) drops
`ButtonSpinner` entirely in favor of two `RepeatButton`s wired to `NumericUpDown`'s standard
increment/decrement command surface. Either way, `NumericUpDown` itself is REUSED, not replaced.

**Decision: `IndustryStepperTheme`, a named `ControlTheme` on `NumericUpDown`** (per item 0's
opt-in rule — never the bare `{x:Type NumericUpDown}`/`{x:Type ButtonSpinner}` global keys), applied
via `Theme="{StaticResource IndustryStepperTheme}"` at every new-design stepper site, `Classes="Industry"`
only — never `IndustryInput` (item 8's `.input` selector target; see item 8's own N3 correction for
why applying both would fight `IndustryStepperTheme`'s 22px template) — so old bare-type
`NumericUpDown`/`TextBox, ComboBox, NumericUpDown` selectors in `Cards.axaml`/`ChromeOverrides.axaml`
don't also apply. Template: a
`Border` (1px `IndustryDivider`) containing a `Grid` of `[− button] [TextBlock/PART_TextBox value]
[+ button]`, exact metrics: total height 22, buttons 18×22 (Barlow Condensed 600 12px,
`IndustryAccent700`, hover fill `IndustryAccent100`), value cell `6,0,6,0` horizontal padding, 1px
left+right borders. **Min-width numbers corrected post-implementation (code-review round)**: the
52/46/34/24 values from LAYOUT-SPEC §5 are the VALUE CELL's own min-width, not the whole control's
— `IndustryStepperTheme`'s `MinWidth` is set on the whole `NumericUpDown` (per-site override is a
plain local `MinWidth` on the instance, same mechanism, different number), so each site's real
value is value-cell-min-width + 38 (2×18px buttons + ~2px border): **90 default** (was written as
52 here), **84/72/62** per-site (was written as 46/34/24) — Sync&slant's two steppers = 84,
Spectrum·waterfall's Bins/px = 62, Start/Span = 72. Confirm these three exact site-to-width
mappings against `PADDING-NORMALIZATION.md`'s own citations at build time; the +38 offset is fixed
by the template's `18,*,18` column grid, not per-site.

**Consequence, reversed from the original draft:** every Phase 2-6 site currently using
`NumericUpDown` (waterfall Bins/px, Start/Span, Sync&slant's Slant-ppm/Offset-px) stays a
PURE RE-SKIN when ported — same `Value`/`Minimum`/`Maximum`/`Increment` bindings, only the
`Theme=` attribute changes. This removes the "real extra work" the original draft flagged for
Phase 3's Sync&slant/Spectrum·waterfall sub-batches; no binding rewrite is needed there.

**Resources:** `IndustryDivider`, `IndustryAccent700`, `IndustryAccent100`,
`IndustryHeadingFontFamily` (button glyphs), `IndustryMonoFontFamily` (value). **New:**
`IndustryStepperValuePadding` = `6,0,6,0` (composite Thickness — `IndustryPaddingBase` is `6`
uniform per Phase 0's paired-scale definition, i.e. `6,6,6,6`, NOT `6,0,6,0`, so this genuinely new
resource is needed; the same value also covers item 8's `.input` padding — see item 8's own note
on sharing this one resource under a single name rather than declaring a duplicate).

**Pitfall:** confirmed real (this is the class of bug `ChromeOverrides.axaml`'s own header warns
about) — do not let `IndustryStepperTheme`'s template hardcode `Height="18"` on the spinner
buttons or `MinWidth="90"` on the whole control as bare literals; per-site overrides (84/72/62,
corrected above) must reach the template via `TemplateBinding`/the control's own inherited
`MinWidth`, not a literal baked into the `ControlTemplate`. Also confirm at implementation time
which template parts
`NumericUpDown.OnApplyTemplate` hard-requires (`PART_TextBox` at minimum; `PART_Spinner` if the
`ButtonSpinner`-hosting variant (a) is used) — a replaced template must still name these parts
correctly or `NumericUpDown`'s own value/increment logic breaks silently.

---

## 6. `.trk` track/slider

**Mechanism, per item 0's rule:** `IndustrySliderTheme` — a NAMED `ControlTheme` (`x:Key=
"IndustrySliderTheme"`, `TargetType="Slider"`), applied via explicit `Theme="{StaticResource
IndustrySliderTheme}"` at each interactive site (Gain/Zero in the Spectrum·waterfall card, the 6 TX
editor adjustment sliders) — NEVER a bare `{x:Type Slider}` key, which would immediately re-skin
`RadioHeaderView`'s RX/TX level sliders and `TxControlsPaneView`'s Drive slider before their own
phases (2, 4) even start.

Template structure mirrors `ChromeOverrides.axaml`'s existing `{x:Type Slider}` theme closely (same
general shape: `Border` track well + `Track`/`Thumb`), but with `TemplateBinding`-driven track
height (styled property `TrackHeight`, default 4, overridden to 6/8 per site) and track width where
fixed-width (118/70/38 — these are `Width` on the `Slider` itself at each call site, not a
template concern). Thumb: 3×12, `IndustryAccent800`.

**Display-only meters** (RX/TX level readouts, L/R meters, POWER/ALC meter) are NOT `Slider`s at
all — LAYOUT-SPEC's own distinction ("`.trk` … Not a Fluent `Slider` — a 3-element `Grid`" for the
generic atom, further split per this plan's own Phase 1 scope note into interactive-vs-display) —
build `IndustryMeter` as a plain 3-element `Grid`/`Border` (track background `IndustryNeutral300`,
fill `Border` bound to a `FillPercent` property, no `Thumb`/no interactivity) for these, confirmed
correct by LAYOUT-SPEC §5's own observation that fill% and thumb% are independently different
values in a live example ("RX lvl fill 63% thumb 78%") — a real interactive `Slider`'s
`Value`/`Track` can't represent two independent percentages at once, so display-only meters
needing BOTH a fill bar and a separate thumb-position marker (if any real site needs that
combination) get it via `IndustryMeter` having both a `FillPercent` and an optional
`MarkerPercent` property, not by mis-using `Slider`.

**Resources:** `IndustryNeutral300` (unfilled track), `IndustryAccent` (fill),
`IndustryAccent800` (thumb). **New:** none beyond the styled properties above — `TrackHeight`
default matches `IndustrySpacingSnug`=4 numerically but should be its OWN styled property default,
not a `DynamicResource` binding to the spacing scale (track height and spacing are conceptually
unrelated even if numerically coincident at the default case).

**Pitfall:** the exact one `ChromeOverrides.axaml`'s own header warns about — track
height/width/thumb size must be `TemplateBinding`s, never literals, or the 3 different track
heights (4/6/8) used across the mockup silently collapse to one.

---

## 7. Buttons at 6 heights

**Mechanism:** `Classes` combination, matching `Cards.axaml`'s own existing convention
(`Button.primary`, `Button.small` compose independently) rather than one `ControlTheme` per height
— e.g. `Classes="IndustryBtn IndustryBtnPrimary IndustryBtn36"` or a shorter composed form. Height/
padding/font-size vary by a `IndustryBtn{N}` class (36/26/25/24/22/21 per LAYOUT-SPEC §4's own
table), fill/border/text-color vary by `IndustryBtnPrimary`/`IndustryBtnSecondary`. This needs a
`ControlTheme` (not bare `Style`) ONLY if Fluent's `Button.accent`-style ContentPresenter-painting
conflict (documented in `ChromeOverrides.axaml`'s own header comment: "Setting Background on the
Button... loses to it") recurs for the new design's primary variant — likely yes, same underlying
Fluent behavior, so `IndustryBtnPrimary`/`IndustryBtnSecondary` need the SAME `/template/
ContentPresenter` targeting trick already proven in `ChromeOverrides.axaml`'s
`Button.primary /template/ ContentPresenter` selector — this one IS `Classes`-scoped already
(opt-in), so no item-0 collision risk, just reuse the known-working selector shape with new colors/
metrics.

**Per-height padding, NOT one-size** — `PADDING-NORMALIZATION.md` §1b's own finding: 36px alone has
TWO different horizontal paddings (10 vs 11) depending on site (Favourite recall vs Store current),
and 26px/22px each have multiple values too (see §1b's full button table). This means
`IndustryBtn36` etc. can't hardcode ONE padding per height class. **Adopted policy (formalized in
the Summary section below, per round-2 auditor finding that this needed an explicit, stated
resolution rather than being left ambiguous): button padding is a deliberate, documented EXEMPTION
from the "no ad-hoc literals" rule** — `Padding` is set per-instance in later-phase XAML, citing
`PADDING-NORMALIZATION.md` §1b's exact value for that site, not baked into a shared class/resource.
Height/font-size/fill/border stay class-driven (`IndustryBtn36` etc.); padding does not.

**Resources:** `IndustryAccent`/`IndustryBg` (primary fill/text), `IndustryDivider`/`IndustryText`
(secondary), `IndustryHeadingFontFamily` — **font family resolved (round-2 auditor finding,
verified against `_ds/.../styles.css:143-145`): Barlow Condensed 600**, confirmed via `.btn {
font-family: var(--font-heading); font-weight: var(--font-heading-weight) }` where
`--font-heading: "Barlow Condensed"` / weight 600, and corroborated by LAYOUT-SPEC.md:71/145. No
inline override anywhere in `2a`'s actual button instances. The original draft's uncertainty here
is resolved — all button text uses `IndustryHeadingFontFamily`, never body Barlow.

**Pitfall:** the already-proven `/template/ ContentPresenter` targeting for primary-variant fill
color (Fluent `Button.accent` conflict) — reuse, don't rediscover.

---

## 8. `.input`

**Mechanism (corrected, round-2-of-round-2 finding N3):** `Style Selector="TextBox.IndustryInput,
ComboBox.IndustryInput, NumericUpDown.IndustryInput"` — a SEPARATE `IndustryInput` class, distinct
from the bare `Industry` exclusion marker. An earlier draft doubled `Industry` as both the item-0b
collision-exclusion marker AND this selector's target; that's wrong because item 0b/item 5 require
EVERY new-design atom to carry `Industry` for the `:not(.Industry)` old-style exclusion to work —
including the stepper's own internal `NumericUpDown`. If `.input`'s selector also just targeted
`.Industry`, every stepper's `NumericUpDown` would ALSO match `.input` and get height 24/26 +
`IndustrySurface` fill, fighting `IndustryStepperTheme`'s own 22px template. Every `.input`-styled
control instance carries BOTH classes (`Classes="Industry IndustryInput"`); the stepper's internal
`NumericUpDown` carries only `Industry`. Per-type Height/Padding/FontSize setters (height 24, 26
for the mode `<select>` via a `.tall`/`.select` modifier class), matching `ChromeOverrides.axaml`'s
own existing pattern of NOT trusting a bare comma-list selector for properties the 3 types don't
share (`VerticalContentAlignment` needed per-type per that file's own comment — same caveat applies
here).

**Resources:** `IndustrySurface` (fill), `IndustryDivider` (border), `IndustryBodyFontFamily` (11px
body text) or `IndustryMonoFontFamily` if the field shows a numeric/token value (confirm per-site
in Phase 2-6, not a blanket rule — LAYOUT-SPEC doesn't state `.input` is universally mono). Padding
reuses `IndustryStepperValuePadding` (`6,0,6,0`, defined in item 5) directly — no separate
`IndustryInputPadding` resource, same Thickness value, one canonical name.

**Pitfall:** `TextControlThemeMinHeight`/`TextControlThemePadding` (`ChromeOverrides.axaml`'s
existing global resource overrides, currently 19/`4,0,4,0`) are ALSO Application-level resources,
not `Classes`-scoped — per item 0's rule, do NOT override these two specific keys again in
`Atoms.axaml` (that WOULD be a global collision, silently changing every old-design `TextBox`'s
height too); reach the new 24/26px heights via `Height`/`MinHeight` setters on the
`.IndustryInput`-classed selector instead, which properly stays opt-in.

---

## 9. `.seg` segmented control

**Mechanism:** `Classes`-scoped, matching `Cards.axaml`'s existing `RadioButton.seg` structural
pattern exactly (a `ControlTemplate` on a NAMED `RadioButton.IndustrySeg` selector — already
`Classes`-scoped in the existing code, confirmed safe, just reproduce the same technique with new
metrics/colors, not a new architectural decision).

**Two padding variants** (`PADDING-NORMALIZATION.md` §1b, confirmed against LAYOUT-SPEC §4's own
"padding `2,10` … (or `3,6` … stretched when full-width)"): named `Thickness` resources
`IndustrySegOptPadding` (default, `10,2,10,2`) and `IndustrySegOptPaddingStretched` (`6,3,6,3`,
paired with `HorizontalAlignment="Stretch"` + `HorizontalContentAlignment="Center"` on the stretched
variant's own `Classes="stretched"` selector) — round-2-of-round-2 finding N6: an earlier draft
named these `IndustrySegOpt`/`IndustrySegOpt.stretched`, which isn't a legal `Thickness` `x:Key`
(a resource key can't carry a class-selector-style dot modifier); renamed here to plain,
unambiguous resource names. The stretched variant is used specifically where the segment fills its
container width (Mode Auto/Lock, TX-mode Auto/Manual), matching the 2 real sites
`PADDING-NORMALIZATION.md` found.

**Resources:** `IndustryDivider` (border), `IndustrySurface`/`IndustryBg` (unselected fill),
`IndustryAccent` (selected fill), `IndustryBg` (selected text — LAYOUT-SPEC's active-chip pattern:
paper text on accent fill).

**Pitfall:** none new — this exact `Classes`-scoped `RadioButton` `ControlTemplate` technique is
already proven working in this codebase.

---

## 10. `.table` — and the DataGrid decision

**Recommend: drop `Avalonia.Controls.DataGrid` for the ported views, use a plain
`Grid`/`ItemsControl`-based table.** Reasoning: LAYOUT-SPEC's own tables are 4-9 columns × 5-6
STATIC rows (Decode-activity, Mode-timing reference, TX log) — not a scrolling/virtualized/
sortable/resizable real grid, just a fixed small table, which is exactly what the mockup's actual
`<table>` markup is (per `PADDING-NORMALIZATION.md`'s citations — plain `<table>`/`<thead>`/
`<tbody>`, no grid-specific interaction). Fighting `DataGrid`'s own considerable chrome system a
SECOND time (its column-header sort glyph, resize grip, row-selection highlight machinery, cell
editing infrastructure — none of which the mockup uses) for a 5-row static table is more work than
a `Grid RowDefinitions="Auto,*"` header row + `ItemsControl` body with a `DataTemplate`, and gets
closer to genuine pixel parity (LAYOUT-SPEC §4's exact header/cell padding/rule specs apply
directly to plain `Grid`/`Border` elements with no template-priority fight at all). This DOES mean
dropping the `Avalonia.Controls.DataGrid` package reference and its `avares://.../Fluent.xaml`
style include from `App.axaml` once the two current DataGrid sites (Decode-activity, TX log) are
actually re-implemented — that removal itself belongs to whichever phase ports those two views
(Phase 3 for Decode-activity, Phase 4 for TX log), NOT Phase 1, since the package is still needed
by the old-design instances until then.

**Mechanism:** `IndustryTableHeader` (`Style Selector="TextBlock.IndustryTableHeader"` — Barlow 400
11px, letter-spacing **corrected to device pixels** (round-2 auditor finding: LAYOUT-SPEC §6
pitfall 8 — Avalonia `LetterSpacing` is device pixels, not em — `.08em × 11px = 0.88`, NOT the raw
`.08` em value the original draft carried forward unconverted), uppercase, `IndustryTextMuted62`) +
a `Border`-based bottom-rule on the header row (`IndustryDivider`) + `IndustryTableCell` (`Style
Selector="TextBlock.IndustryTableCell"` — `.v2` mono 11px) + a `Border`-based bottom-rule per body
row (`IndustryTableRule8`). Two cell-padding variants confirmed by `PADDING-NORMALIZATION.md`:
`5,2,5,2` (Decode-activity, the wider table) vs. `4,2,4,2` (Mode-timing/TX log, "narrow
right-column tables") — **named as composite Thickness resources** (`IndustryTableCellPadding` =
`5,2,5,2`, `IndustryTableCellPaddingNarrow` = `4,2,4,2`), unlike item 7's buttons: only 2 fixed
variants exist here (not a combinatorial per-site spread), so naming them is cheap and matches
item 9's `.seg`-opt precedent (2 named variants) rather than item 7's per-instance-literal
exemption (6+ values).

**Resources:** `IndustryTextMuted62`, `IndustryDivider`, `IndustryTableRule8`,
`IndustryBodyFontFamily`, `IndustryMonoFontFamily`.

**Pitfall:** none template-priority-related (plain `Grid`/`ItemsControl`, no FluentTheme control
being reskinned) — the pitfall is scope/sequencing (package removal timing), not Avalonia mechanics.

---

## 11. Thumbnail figure

**Mechanism:** `IndustryThumbnail` `UserControl` (`Mode`/`Caption`/`Meta`/`AspectRatio`/
`CaptionPadding` properties) — border (`IndustryDivider`) + hatch-fill placeholder body (item 12) +
mode-badge chip (`IndustryMini`, static Border form, pinned top-left via `Grid`/`Canvas`
positioning on an opaque `IndustryBg`-background "plate") + caption row (top rule
`IndustryDivider`, call/name `TextBlock` left via `.v2`, time/meta `TextBlock` right via
`readoutSmall`-equivalent, muted color `IndustryTextMuted50`).

**Two aspect ratios**: 5:4 (Previous frames, Gallery) and 4:3 (Saved templates, Recently sent) —
`AspectRatio` as a styled property (bindable to a `double`, applied via the same `MeasureOverride`
technique LAYOUT-SPEC §6 pitfall #4 already mandates for the Incoming-frame's own height-driven
aspect ratio — reuse that mechanism here too, don't invent a second one).

**Per-site caption padding** (confirmed genuinely different, not a copy error, per
`PADDING-NORMALIZATION.md`'s own flag): default `4,2,4,2` (Previous frames/Saved templates/
Recently-sent), Gallery override `5,3,5,3` — expose `CaptionPadding` as an overridable property
defaulting to the common value.

**Resources:** `IndustryDivider`, `IndustryBg`, `IndustryTextMuted50`, hatch brush (item 12).

**Pitfall:** LAYOUT-SPEC §6 pitfall #5 — needs `UniformGridLayout` with explicit
`MinItemWidth`/`MinItemHeight` matching the target ratio and `ItemsStretch="None"` at each GRID
call site (Phase 3-5's concern, not this atom itself, but the atom's own `AspectRatio` property
must be honored correctly by `MeasureOverride` for the surrounding grid's stretch behavior to work
at all — the two are coupled, flag it here so Phase 3-5 doesn't rediscover pitfall #5 from scratch).

---

## 12. Hatch panel

**Brush:** `IndustryHatchStripeBrush`, a `DrawingBrush` with `TileMode="Tile"`. Per the plan's own
already-derived correction: CSS's `repeating-linear-gradient(135deg, ... 0 6px, transparent 6px
12px)` has its 12px period measured ALONG the gradient axis (perpendicular to the visible stripes),
so an axis-aligned tile reproducing the same visual period via simple translation needs side length
= 12×√2 ≈ 16.97, NOT a naive `SourceRect="0,0,12,12"` (confirmed by direct trigonometric derivation
in the plan, not re-derived here). Stripe color: `IndustryAccent` at 14% opacity over `IndustryBg`
— per Phase 0's own translucency rule (§2's "opaque for hairlines, translucent where it composites
over live content"), this brush is a LARGE fill, not a hairline, and is used over BOTH the paper
`IndustryBg` ground AND the editor canvas's `IndustryNeutral200` — a single pre-blended opaque
value can't be correct for both grounds, so this MUST stay a genuinely translucent
`Color="#245980A6"`-style brush (14% = `0x24` alpha), not a Phase-0-style opaque pre-blend.

**Panel:** `IndustryHatchPanel` — a `Border` (`IndustryDivider` border, `IndustryHatchStripeBrush`
fill, `MinHeight` per-site override — LAYOUT-SPEC §5 cites min-height 26 for Signal-quality's two
plots, 18 for Output's POWER/ALC meter) wrapping the hatch fill. Used standalone (placeholder
plots) and inside `IndustryThumbnail` (item 11) as the image-placeholder body.

**Resources:** `IndustryDivider`. **New:** `IndustryHatchStripeBrush` (DrawingBrush, not in
`AtomsTokens.axaml` yet — Phase 0 deferred it here explicitly).

**Pitfall:** confirmed real geometry error avoided (see above) — this is the one atom where getting
the brush wrong produces a visually-plausible-but-wrong result (stripes at the wrong density) that
could pass a casual look, so the √2 factor is worth a code-review-round callout specifically.

---

## 13. `MenuItem`/`Menu` — DEFERRED to Phase 2

**Round-2 auditor finding (verification-path blocker): Phase 1 had no stated way to verify any
atom before Phase 2-6 consume them, and 5 atoms had no Phase 1-5 call site at all.** `MenuItem`/
`Menu` is one — its only real consumer is Phase 2 (the menu bar). Rather than build it inert in
Phase 1 and hope Phase 2's opt-in wiring is correct months later with no test in between, **this
atom moves into Phase 2's own scope**, built and wired (and verified against a real running window)
in the same phase that actually uses it. The design reasoning below stays valid and should carry
forward into Phase 2's own plan-review, not be redone from scratch:

Since `MainWindow.axaml` has exactly ONE `Menu` in the whole app (no per-view duplicated menu
bars), Phase 2 replacing the entire menu bar as one unit means a `IndustryMenuItemTheme`/
`IndustryMenuTheme` NAMED `ControlTheme` (per item 0's rule — never a bare `{x:Type MenuItem}` key,
even though no old-design `MenuItem` instance would survive the swap, for consistency with every
other item in this design) applied via `Theme=` at Phase 2's menu-bar markup. Template: strip
Fluent's icon column and chevron-reservation width (LAYOUT-SPEC §5: padding `7,2,7,2`, Barlow 400
12px, hover fill `IndustryAccent100`/text `IndustryAccent800` — no submenu chevron visible in the
mockup's flat single-level menu). Pitfall: same class as `ComboBox`'s chevron trim already done in
`ChromeOverrides.axaml` — `MenuItem`'s icon/chevron columns are template-internal, need a targeted
override inside the new template, not a bare `Padding` setter.

---

## 14. `ScrollBar` — DEFERRED to Phase 3 (first real consumer)

**Same verification-path reasoning as item 13.** No Phase 1 site uses `ScrollBar` at all; its real
consumers are Phase 3-5's scrolling grids (Saved templates, Recently sent, Gallery thumbnail grid).
Move to whichever of those phases lands first (Phase 3, if Receive tab's Previous-frames grid scrolls
— confirm at that phase's own plan stage). Design reasoning to carry forward: `App.axaml:17-19`
already sets `ControlCornerRadius`/`OverlayCornerRadius` to 0 globally, so Fluent's rounding may
already be handled without any template work at all — check this FIRST before assuming a full
`ScrollBar` template replacement is even needed. If it isn't, this whole atom may not require any
Phase 1-3 work beyond that already-shipped Phase 0 global setting.

---

## 15. `CheckBox`/`RadioButton` base styling — DEFERRED to Phase 6

**Same verification-path reasoning as items 13-14.** Options window's ~100 controls are the only
real consumer, and that's Phase 6. Building `IndustryCheckBoxTheme` in Phase 1 with zero call sites
until Phase 6 is exactly the "5 atoms with no Phase 1-5 call site" problem the round-2 review
flagged. Design reasoning to carry forward: named theme only (`IndustryCheckBoxTheme`, per item 0's
rule — Options' live `CheckBox`/`RadioButton` instances must keep the OLD look until Phase 6
explicitly opts them in), square check mark, `IndustryDivider` border, `IndustryAccent`
checked-fill (LAYOUT-SPEC §7's "no rounded corner anywhere"). Pitfall: Fluent's `CheckBox` glyph is
a `PathIcon` with its own default checkmark path data at a fixed size — template-internal, needs a
targeted override, same class of problem as `MenuItem`'s chevron.

---

## 16. Disclosure header ("＋ ADVANCED TIMING")

**Mechanism — recommend NOT using `Expander` at all.** The mockup's actual rendering (a bare
`<details><summary>` with no border/card chrome around the whole disclosure, just a styled text
line with a disclosure glyph, content indented below) doesn't match Fluent `Expander`'s visual
model (a bordered/backgrounded header+body box) even after `ChromeOverrides.axaml`'s existing
resource overrides. A plain `ToggleButton` (styled borderless, content = the header text + a
rotating chevron glyph bound to `IsChecked`) driving an `IsVisible` binding on a sibling content
`Panel` reproduces the mockup exactly, with zero FluentTheme template-priority risk — this
mechanism conclusion stands from the original draft.

**Typography — corrected (round-2 auditor finding, the original draft misidentified this atom's
own text style):** this is `.gbt` group-box-title typography, NOT item 4's kicker text. Confirmed
against `SSTV Console.dc.html:184`: `font: 600 9.5px var(--font-heading)` — **Barlow Condensed 600,
9.5px**, letter-spacing `.14em`, `text-transform:uppercase`, `IndustryAccent700`, padding-top 4 —
matching LAYOUT-SPEC.md:189 exactly. The source text itself is mixed-case ("Advanced timing") with
the CSS `text-transform:uppercase` doing the work, so — unlike item 4's kicker text, which is
already-uppercase in source — this atom DOES need an actual uppercase step (either markup authors
type it uppercase, matching item 4's own convention, or a converter/`.ToUpperInvariant()` at the
binding boundary if the text ever comes from a bound/localized mixed-case string). **Glyph**: use
ASCII `+` (not the fullwidth `＋` U+FF0B glyph the mockup's raw text happens to use) — Barlow
Condensed's charset almost certainly doesn't include the fullwidth form; a plain `Path`/small glyph
is an equally safe alternative if `+` doesn't read cleanly at 9.5px.

**Resources:** `IndustryAccent700`, `IndustryHeadingFontFamily`, `IndustryGbtPadding` (item 1's
`5,0,5,0` — note this atom's own padding-top-4 is a DIFFERENT, additional value layered on top,
not a replacement for the group-box-title notch padding; the two apply to different elements —
`IndustryGbtPadding` to a group box's own title notch, padding-top-4 to this disclosure toggle's
position within its parent's row stack).

**Pitfall:** none — this sidesteps `Expander`'s pitfalls entirely rather than solving them, which
is the point.

---

## 17. Focus ring

**Mechanism:** Avalonia's `FocusAdorner` — set once via `Style Selector=":focus-visible /template/
*"`-style targeting is NOT how Avalonia's adorner mechanism works; the actual mechanism is a
`FocusAdorner`-templated resource keyed by control type or a shared `Classes`-based
`AdornerLayer.Adorner` attachment. Concretely: define one shared `IndustryFocusAdornerTemplate`
(a 2px `IndustryAccent`-bordered rectangle, no fill, inset -2 so it overflows the target's own
bounds) and reference it from each NEW interactive atom's `ControlTheme` via Avalonia's standard
`FocusAdorner`-provider mechanism (the exact API surface — `AdornerLayer.SetAdorner` vs. a
`FocusVisualStyle`-equivalent resource — needs confirming against Avalonia 11.3's actual
API during implementation; this design pass identifies WHAT is needed and WHY a plain border
`:focus` pseudo-class setter is insufficient, not the exact Avalonia API incantation, which should
be a small implementation-time spike rather than guessed here).

**Resources:** `IndustryAccent`.

**Sequencing constraint (round-2 auditor finding — this deferral is acceptable as an open API
question, but NOT a free deferral):** item 18 needs this ring on ~10 atoms. If the mechanism is
nailed down AFTER those atoms are built, they get built twice. **Pin the exact Avalonia API
(`FocusAdorner`/`AdornerLayer` surface) before building the FIRST interactive atom in Phase 1's
actual implementation order**, not at the end. Also: the mockup's offset is NOT uniform across
atoms — `_ds/.../styles.css:130` gives a global `outline-offset: 2px`, but `:176` overrides `.input`
to `outline-offset: 0` and `:203` overrides `.seg-opt` to `outline-offset: -2px` — so a single
shared adorner template with one fixed `Margin="-2"` is WRONG for at least 2 of the atoms it needs
to cover. The shared `IndustryFocusAdornerTemplate` must expose the offset as a parameter, not hardcode one value.

**Pitfall:** confirmed real risk (already in the plan) — a ring drawn IN-LAYOUT (a bare border
setter reacting to `:focus-visible`) on a `.mini` chip packed into a 3px-gap row would overlap its
neighbors, since the chip's own bounds have no room for a 2px outward ring; the adorner-layer
approach avoids this specifically because adorners render outside normal layout bounds.

---

## 18. Hover/focus/disabled states — REWRITTEN as a per-atom table (round-2 auditor finding)

**The original draft's single blanket rule was wrong per-atom.** Real values, verified against
`_ds/.../styles.css` and `LAYOUT-SPEC.md:146`:

| Atom | Hover | Press/active | Disabled | Notes |
|---|---|---|---|---|
| `.btn-primary` (item 7) | `IndustryAccent600` (NEW, see below) | `IndustryAccent700` | Opacity 0.45 | `styles.css:153-154` |
| `.btn-secondary` (item 7) | text-7% mix = `#121D1F20` | text-14% mix = `#241D1F20` | Opacity 0.45 | Already given literally in `LAYOUT-SPEC.md:146` — the original draft didn't cite this |
| `.seg-opt` (item 9) | text-7% mix = `#121D1F20`, **suppressed when the option is `:checked`** (round-2-of-round-2 finding N4: source is `.seg-opt:not(:has(input:checked)):hover`, `styles.css:202` — Avalonia's `:pointerover` and `:checked` both apply simultaneously to the same element, so an unconditional hover setter would wash 7% ink over the accent-filled selected segment; the `ControlTheme`'s hover `Style` selector must exclude `:checked`) | selected state is `IndustryAccent` fill (not a press color — see item 9's own selected-state design) | Opacity 0.45 | `styles.css:202` |
| Stepper glyph buttons (item 5) | `IndustryAccent100` | — (RepeatButton has no separate press color in the source) | Opacity 0.45 | `SSTV Console.dc.html:37`, this one IS `IndustryAccent100` as the original draft assumed — the blanket rule happened to be right here |
| `.mini` Button/ToggleButton forms (item 2) | **`#121D1F20` (text-7% mix), NOT `IndustryAccent100`** (round-2-of-round-2 finding N2, corrected from an earlier draft's wrong analogy: `.mini` is a divider-bordered, ink-colored mono chip — structurally like `.btn-secondary`/`.seg-opt`, both of which hover to text-7%, not like the stepper's borderless accent-colored glyph buttons. `IndustryAccent100` also actively breaks the active/selected variant — `IndustryMini.active` is `Background=IndustryAccent`+`Foreground=IndustryBg`, so hovering it with an `IndustryAccent100` fill leaves near-white text on a near-white fill, unreadable) | `IndustryAccent700`/`800` family | Opacity 0.45 | `styles.css:156`/`:202` (analogy, no `.mini`-specific `:hover` rule exists in source — none of the 52 `.mini` occurrences in `2a` are interactive elements, only the Button/ToggleButton FORMS this design adds are) |
| `IndustrySliderTheme` (item 6) | — (Fluent `Slider` doesn't expose a comparable hover-fill concept in the mockup's static rendering) | — | Opacity 0.45 | no source CSS states beyond disabled |
| `.input` (item 8) | border → text-45% mix | focus-visible only (no separate press state for a text field) | Opacity 0.45 | `styles.css` `.input:hover` |

Applied consistently: `:pointerover`/`:pressed`/`:disabled` `Style` selectors added to each
interactive atom's own `ControlTheme` at build time (not a standalone control). Call out explicitly
in Phase 1's code-review round whether each atom actually got its full row from this table — it's
easy to build the resting-state look correctly and forget interaction states across ~7 atoms.

**New color, confirmed needed and sourced (not TBD as the original draft left it):**
`IndustryAccent600` = `#597EA3`, verified against `_ds/.../styles.css:29`
(`--color-accent-600: #597ea3`) — Phase 0 only pulled Accent100/700/800/900 from LAYOUT-SPEC §2's
own table, which doesn't list a 600 step, but the source CSS ramp has one and `.btn-primary`'s
hover state genuinely needs it (not interchangeable with Accent700, which is already used for
group-box titles and would be visually ambiguous reused as a hover state on the same screen). This
is a small, safe ADDITIVE amendment to the already-shipped `AtomsTokens.axaml` (Phase 0 stays
otherwise unchanged) — the actual `.axaml` edit is made alongside this design doc, not deferred to
implementation.

---

## Padding exemption policy (round-2 auditor finding — stated explicitly, not left ambiguous)

Two different resolutions apply to per-site padding variance across this design, both deliberate:

1. **Named composite `Thickness` resources** — used when a value has a SMALL, FIXED number of
   variants that recur across sites (group box's 5 padding families in item 1; `.mini`'s single
   value; `.seg-opt`'s 2 variants in item 9; the table's 2 cell-padding variants in item 10; the
   stepper/`.input`'s shared value cell padding in items 5/8). These get real `x:Key` resources,
   word-suffixed per the established `NoThicknessSpacingMismatchTests` convention (no digit in the
   key).
2. **Deliberate per-instance `Padding` literal, cited from `PADDING-NORMALIZATION.md`** — used when
   a value genuinely varies per SITE within one class/height with no small closed set (item 7's
   buttons: 36px alone has 2 different values depending on which specific button; 26px has more;
   item 2's menu-bar `.mini` override is a true one-off). This is a deliberate, STATED exemption
   from round-1 blocker 3's "no ad-hoc literals scattered through Phase 1's atoms" rule — the
   exemption is scoped specifically to control-instance `Padding` where naming every combination
   would mean more named resources than actual reuse, not a general license to skip the
   normalization table. Every such literal must still cite its `PADDING-NORMALIZATION.md` line at
   the call site (a code comment), not be typed from memory.

---

## Summary — new resources this phase must add to `AtomsTokens.axaml` (or a Phase-1-local resource
block in `Atoms.axaml` itself)

**Composite Thicknesses:** `IndustryGbPadding`, `IndustryGbPaddingWide`, `IndustryGbPaddingWideDeep`,
`IndustryGbPaddingFav`, `IndustryGbPaddingNested`, `IndustryGbtPadding`, `IndustryMiniPadding`,
`IndustryStepperValuePadding` (shared with `.input`, one name, per item 8's correction),
`IndustrySegOptPadding`/`IndustrySegOptPaddingStretched` (item 9),
`IndustryTableCellPadding`/`IndustryTableCellPaddingNarrow` (item 10, NEW in this revision).
**Brushes:** `IndustryHatchStripeBrush` (translucent, NOT opaque-prebaked, correct √2 tile
geometry). **Colors:** `IndustryAccent600` = `#597EA3` (NEW, confirmed needed, added to
`AtomsTokens.axaml` alongside this doc — see item 18). **Named `ControlTheme`s built in Phase 1:**
`IndustrySliderTheme`, `IndustryStepperTheme` (REVISED — now a `NumericUpDown` theme, not a new
`TemplatedControl`, per item 5), `IndustryGroupBox` (REVISED — `TemplatedControl` deriving
`HeaderedContentControl`, not `UserControl`, per item 1), `IndustryBlueprintCorners`/
`IndustryThumbnail`/`IndustryHatchPanel`, plus the `Classes`-scoped items (mini×3 forms — `.mini`
only, `.chip`/`.sbc` deferred — rows, kicker×2 variants, buttons×6, input, seg×2 variants, table,
disclosure toggle) as `Style`/named-`ControlTheme` selectors. **DEFERRED out of Phase 1** (round-2
finding — no Phase 1-5 call site): `.chip`/`.sbc` chips → Phase 6; `MenuItem`/`Menu` → Phase 2;
`ScrollBar` → first phase with a scrolling grid (Phase 3+); `CheckBox`/`RadioButton` base styling →
Phase 6. **Mandatory collision-avoidance edit, also part of Phase 1** (item 0b): narrow the 9 old
bare-type selectors in `Cards.axaml`/`Tokens.axaml`/`ChromeOverrides.axaml` (see item 0b's table)
to `:not(.Industry)`, and ensure every new atom instance carries the `Industry` marker class.
