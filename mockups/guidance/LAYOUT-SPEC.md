# SSTV Console — Avalonia layout spec

Authoritative measurements for porting mockup **2a** (`SSTV Console.dc.html`, `<div class="dv-opt" id="2a">`, lines 78–627) to Avalonia 11. Every number here is read out of that mockup's CSS or its inline styles — where the two disagree, the inline style wins, because it is what renders.

Rule for the porter: **do not invent a value.** If a number is not in this file, go read the corresponding line of the mockup. If it is in this file, use it verbatim — no rounding to a "nice" 4px grid. The mockup is deliberately off-grid (9px, 11px, 9.5px, 12.5px) because that is what the reference apps do.

---

## 0. What is currently wrong

`Styles/Tokens.axaml` still carries a **different, earlier design** (`ScanlineStudio*`, dark #131316 panels, #3FA7D6 accent, 2/4/6px spacing scale). That palette is not the mockup. It must be replaced by §2's tokens before any pixel-matching is possible — right now every panel colour, border colour, accent and spacing step is wrong at the source, and no amount of per-view tweaking will close the gap.

Second global error to check first: the app must render at **1920×1080 with no window chrome scaling**, base font size 12, and DPI scaling 1.0 while you compare. A 1.25 scale factor makes every measurement below off by 25% and will send you chasing ghosts.

---

## 1. Window frame

```
1920 × 1080, background #F2F2F3, vertical stack, no scrollbars, overflow clipped.
```

Five bands, top to bottom. Only band 4 is flexible; the other four are fixed-height and must not grow.

| # | Band | Height | Notes |
|---|---|---|---|
| 1 | Menu bar | **26** | 1px bottom border |
| 2 | Radio header (VFO / Favourites / Transceiver) | **81** | 1px bottom border; height is driven by the 40px frequency readout, see §5 |
| 3 | Workspace tab strip | **26** | 1px bottom border |
| 4 | Workspace body | fill (**≈921**) | padding `11,9,9,8` (L,T,R,B → CSS `11px 9px 8px` = T11 R9 B8 L9) |
| 5 | Status bar | **25** | 1px top border |

Avalonia: `Grid RowDefinitions="26,81,26,*,25"`. Do not use `Auto` for the fixed bands — Auto lets Barlow's metrics drift the band by 1–3px depending on the installed font version, and every downstream row inherits the error.

---

## 2. Tokens

Replace the `ScanlineStudio*` colour set with these. Alpha values are given both as Avalonia `#AARRGGBB` and as the pre-blended opaque hex over the #F2F2F3 ground — **prefer the opaque hex** for hairlines and label text: Avalonia composites translucent 1px borders differently from the browser and a translucent hairline over a panel edge reads doubled.

### Colour

| Token | Value | Opaque over ground | Used for |
|---|---|---|---|
| `Bg` | `#F2F2F3` | — | window, group-box interior, "notch" behind group titles |
| `Surface` | `#E9E9EA` | — | text input fill |
| `Text` | `#1D1F20` | — | body text, values |
| `Accent` | `#5980A6` | — | slider fill, active underline, active chip fill |
| `Divider` | `#291D1F20` | **`#D0D0D1`** | every 1px border |
| `TextMuted62` | `#9E1D1F20` | **`#6E6F70`** | `.lbl` row labels |
| `TextMuted55` | `#8C1D1F20` | **`#7D7E7F`** | secondary chrome text |
| `TextMuted50` | `#801D1F20` | **`#88898A`** | thumbnail meta lines |
| `TextMuted42` | `#6B1D1F20` | **`#99999A`** | the `.000` tail of the frequency |
| `TextMuted72` | `#B81D1F20` | **`#595A5B`** | decoder trace text |
| `RowRule7` | `#121D1F20` | **`#E3E3E4`** | `.row + .row` separator |
| `RowRule6` | `#0F1D1F20` | **`#E5E5E6`** | `.r2 + .r2` separator |
| `TableRule8` | `#141D1F20` | **`#E1E1E2`** | table `td` bottom rule |
| `Neutral200` | `#E7E7EA` | — | editor canvas backing |
| `Neutral300` | `#D4D4D7` | — | slider track (unfilled) |
| `Accent100` | `#EEF6FF` | — | hover fill |
| `Accent700` | `#416180` | — | group-box titles, "ON/locked" values, stepper glyphs |
| `Accent800` | `#2C455D` | — | slider thumb |
| `Accent900` | `#1D2D3D` | — | spectrum / waterfall / image backing |

Hatch fill (`.ph-fill`, every image placeholder): 135° stripes, **6px on / 6px off** (12px period), stripe colour `#DDE2E8` on `#F2F2F3`. In Avalonia use a `DrawingBrush` with `TileMode="Tile"` and `SourceRect="0,0,12,12"` — not a `LinearGradientBrush`, which cannot repeat at that pitch cleanly.

### Type

| Role | Family | Notes |
|---|---|---|
| Heading | **Barlow Condensed**, weight **600** | all uppercase chrome: group titles, tab labels, buttons, stepper glyphs |
| Body | **Barlow**, weight 400 | labels, menu items, prose |
| Mono | **Cascadia Mono**, then Consolas / DejaVu Sans Mono / Menlo | every numeric readout, every chip, every table cell |

Bundle Barlow and Barlow Condensed as embedded resources — do not rely on them being installed. If they fall back to system-ui the whole layout widens by roughly 6% and nothing lines up. Mono is allowed to fall back to the OS.

Mono readouts carry `letter-spacing: -.02em`. Avalonia: `LetterSpacing="-0.22"` at 11px, `-0.24` at 12px, `-1.2` at 40px (Avalonia's `LetterSpacing` is in device pixels, not em). Also set `FontFeatures="+tnum"` on anything that ticks (clock, line counter, SNR).

### Spacing

The mockup's real steps are **2, 3, 4, 5, 6, 7, 9, 11**. The current 2/4/6 scale cannot express the layout. Define exactly those eight and use them literally; the two that carry the layout are **9** (grid gutters, band padding) and **11** (group-box internal top padding, column gaps).

---

## 3. The group box — get this right first

`.gb` appears ~40 times. It is the single highest-leverage control in the port: if it is off by 2px the whole board is off by 2px, cumulatively.

```
border      : 1px #D0D0D1, square corners, transparent interior
padding     : top 11, right 7, bottom 6, left 7      (default)
child gap   : 3
title       : absolutely positioned, top -6, left 6
              Barlow Condensed 600, 9.5px, letter-spacing .14em, UPPERCASE
              colour #416180
              horizontal padding 5, background #F2F2F3  ← the notch that breaks the border
```

Avalonia recipe — a `Grid` with a negative-margin title over the border, **not** a `HeaderedContentControl`:

```xml
<Grid>
  <Border BorderBrush="{DynamicResource Divider}" BorderThickness="1" CornerRadius="0"
          Padding="7,11,7,6">
    <StackPanel Spacing="3"> … </StackPanel>
  </Border>
  <Border Background="{DynamicResource Bg}" Padding="5,0" Margin="6,-6,0,0"
          HorizontalAlignment="Left" VerticalAlignment="Top">
    <TextBlock Classes="GbTitle" Text="MODE" />
  </Border>
</Grid>
```

Three group boxes override the default padding — apply the override, don't normalise them:

| Instance | Padding (T,R,B,L) | Gap |
|---|---|---|
| default | 11,7,6,7 | 3 |
| Spectrum·waterfall, Incoming frame, Decode activity, Received, top-level Rx/Tx panes | 11,8,6,8 (Incoming frame & Editor: bottom **7**) | 3 |
| Favourites, Transceiver | 11,8,5,8 | 5 (Favourites), 3 (Transceiver) |
| nested (Previous frames, Insert field, Text style, Saved templates) | 10,6,5,6 | 4 / 2 / 2 / 3 |

Four group boxes additionally wear blueprint corner marks (`.blueprint` + four `+` corners): the VFO block, **Incoming frame**, and the TX **Editor**. Corner marks are 1px accent-coloured crosses inset at the four corners — implement once as a reusable decorator, not per-view.

---

## 4. Atoms — exact metrics

Build each of these once, as a styled control or `ControlTheme`, and reuse. Most of the current mismatch is that each view re-improvises them.

| Atom | Metrics |
|---|---|
| `.mini` chip | mono 500 **10px**, padding `1,5`, 1px `#D0D0D1` border, no fill, no wrap. Height lands at **17**. |
| `.mini` active variant | fill `#5980A6`, text `#F2F2F3`, border `#5980A6` |
| `.mini` accent-outline variant | border `#5980A6`, text `#416180`, no fill |
| `.chip` | mono 500 **10.5px**, padding `2,7`, 1px border |
| `.sbc` | mono 500 **11px**, padding `2,9`, 1px border |
| `.lbl` | Barlow 400, inherits size (11 in `.r2`, 12 in `.row`), colour `#6E6F70`, no wrap |
| `.v2` value | mono **500 11px**, line-height 1.5 (**16.5**), letter-spacing −.02em, no wrap |
| `.val` value | mono **500 12px**, line-height 1.2 (**14.4**), letter-spacing −.02em |
| `.r2` row | horizontal, centre-aligned, space-between, gap 6, font 11, line-height 1.5 → **row height 17**; 1px `#E5E5E6` top rule on every row **except the first** |
| `.row` row | same but font 12, padding `0,2` → height **23**; rule `#E3E3E4` |
| `.stepper` | 1px bordered strip, total height **22**. `−`/`+` buttons **18 wide × 22 high**, transparent, Barlow Condensed 600 12px, colour `#416180`, hover fill `#EEF6FF`. Value cell: mono 500 12px, line-height 22, right-aligned, padding `0,6`, 1px left+right border, `min-width` **52** default (overridden to 46 / 34 / 24 at the sites noted in §5). |
| `.trk` slider | track height **4** default (8 in Transceiver, 6 in meters), fill `#D4D4D7`, filled portion `#5980A6` from the left. Thumb: **3 wide × 12 high**, `#2C455D`, vertically centred (CSS `top:-4` on a 4px track). Not a Fluent `Slider` — a 3-element `Grid`. |
| `.btn-primary` | fill `#5980A6`, text `#F2F2F3`, Barlow Condensed 600, square, no shadow |
| `.btn-secondary` | transparent, 1px `#D0D0D1`, text `#1D1F20`; hover `#121D1F20`, press `#241D1F20` |
| `.input` | fill `#E9E9EA`, 1px `#D0D0D1`, height **24** (26 for the mode `<select>`), padding `0,6`, font 11 (12 for select) |
| `.seg` segmented | 1px bordered row, options padding `2,10` font 11 (or `3,6` font 11 stretched when full-width); selected option = accent fill, paper text |
| `.table` | header: Barlow 400 **11px**, letter-spacing .08em, UPPERCASE, colour `#6E6F70`, cell padding `2,5` (`2,4` in the narrow right-column tables), 1px `#D0D0D1` bottom rule. Body cells: `.v2` mono 11px, padding `2,5`, 1px `#E1E1E2` bottom rule. |
| Button heights | These are all the heights that exist: **36** (favourite memories, Store current), **26** (Transceiver Receiving/Halt, Transmit row, mode select), **25** (Incoming-frame action row), **24** (Gallery detail actions), **22** (Re-sync/Reset, Lookup/Flag, Refill/Open), **21** (Fill all/Clear, Fill&send/Save current). Pick the right one per site; do not unify them. |

Button font sizes track the height: 36→12.5 mono + 9.5 caption, 26→12, 25→12, 24→11, 22→11, 21→10.5.

---

## 5. Band-by-band placement

### Band 1 — menu bar (h 26)

Padding `6,1`; items in a row with **1px** gaps; each item padding `2,7`, Barlow 400 12px. Hover: fill `#EEF6FF`, text `#2C455D`.

Order: brand `SSTV/CONSOLE` (Barlow Condensed, letter-spacing .05em, right padding 12, the `/` in `#5980A6`) · File · Configurations · Rig & PTT · Calibration · View · Tools · Help. Right-aligned group, gap 8: `cfg: 20m-sstv-ic7300` (mono, `#7D7E7F`) then a `.mini` in accent-outline at **12px** with padding `1,8`, letter-spacing .08em: `DL2QSK · JO31`.

### Band 2 — radio header (h 81)

Row, padding `9,7`, gap **10**, three children:

1. **VFO block** — blueprint-framed, auto width, padding `5,14`, internal gap **14**, four sub-columns:
   - Column A: kicker `VFO A · RX · M1` (mono 10px, letter-spacing .16em, `#416180`, no border/padding) above the frequency: mono **40px**, line-height 1.02, letter-spacing −.03em → `14.230` in `#1D1F20`, `.000` in `#99999A`. **This readout is what sets the 81px band height.**
   - Column B (gap 4): USB/LSB/FM segmented (options padding `2,10`, font 11); then two rows of `.mini` chips, gap 3: `BW 2.7k` `SPLIT OFF` / `STEP 500 Hz` `RIT 0`.
   - Column C: 1px left border, padding-left **14**, right-aligned, gap 3: clock mono **15px** `14:22:07` + ` UTC` as `.lbl` 10px; `CAT LINKED` (accent-outline mini); `S6 · SWR 1.3`.
2. **Favourites** group box — flexes to fill; padding `8,11,8,5`, gap 5. Seven memory buttons + `Store current`, wrapping row, gap 4, each **36 high**, padding `0,10`, two stacked lines (line-height 1.15): mono **12.5px** `14.230 USB`, then Barlow 400 **9.5px** uppercase letter-spacing .06em name. Bottom row: hint text (mono 10px, `#88898A`) `M1…M7 recall · ⇧M store · double-click to rename`, right-aligned `EDIT LIST` `IMPORT` `SCAN M1–M3` minis, gap 3.
3. **Transceiver** group box — **width 272**, padding `8,11,8,5`. Two buttons h26 gap 4 (`Receiving` primary, `Halt` secondary), then two `.r2` rows with a **118 × 8** track: `RX lvl` fill 63% thumb 78% value `−14.2`; `TX lvl` fill 74% thumb 74% value `−6.0`.

### Band 3 — tab strip (h 26)

Padding `6,0`; tabs are `.tabbtn2`: Barlow Condensed 600 **11.5px**, letter-spacing .09em, uppercase, padding `11,5,11,4`, transparent, no border. Active tab: **2px `#5980A6`** underline flush to the bottom edge, full tab width. Hover: text `#416180`.

Right-aligned status chip run, gap 5, all `.mini`: `AUTO-DETECT` · **`SCOTTIE 1 · VIS 60 · 98.4%` (accent-filled)** · `SYNC LOCK 41 s` · `SLANT +3.4 ppm` · `SNR 21.6 dB` · `AUTOSAVE ON` (accent-outline).

### Band 4a — Receive body

```
Grid ColumnDefinitions="236,*,312"  gap 9   padding 11,9,8,9 (T,R,B,L)
```

**Left column (236 wide)** — vertical, gap **11**: `Mode` · `Sync & slant` · `Input chain` · `Signal quality` (last one flexes).
- Mode: full-width Auto/Lock segmented (options stretched, padding `3,6`, font 11, bottom margin 2); then a **4-column** grid of 16 mode minis, gap **2**, each centred; then three `.r2` rows.
- Sync & slant: `.r2` rows; the two steppers use value `min-width` **46**. Two h22 buttons, gap 3, stretched. Then a collapsible `＋ ADVANCED TIMING` header (Barlow Condensed 600 9.5px, letter-spacing .14em, `#416180`, top padding 4) over four more rows.
- Input chain: 8 `.r2` rows, then an L/R meter pair (**6px** tracks, gap 3), then 2 rows.
- Signal quality: kicker (mono 10px letter-spacing .14em `#416180`), hatch panel flex min-height 26, row, kicker, hatch panel, then 4 rows.

**Centre column** — `Grid RowDefinitions="150,*,147"` gap **11**:
1. **Spectrum · waterfall** (h 150), inner `Grid ColumnDefinitions="*,*,146"` gap **7**. Spectrum and waterfall panels: 1px border, `#1D2D3D` fill, corner labels in mono **9.5px** (`#B5D9FD` top-left, `#D6EBFF` bottom-right). Waterfall overlays: 1px vertical line at **12.5%** (`#8CEEF6FF`), and a dashed band from **31.25%** to **81.25%** (both edges dashed, `#73EEF6FF`), labels `1200` at 13.5% and `1500–2300` right-inset 19.5%, mono 9.5px `#EEF6FF`. Right sub-column, gap **1**: three steppers (value min-widths **24**, **34**, **34**), a Gain/Zero row with two **38-wide** tracks, and a `BOTH`/`SPEC`/`WF` mini group (gap 2, `BOTH` accent-filled).
2. **Incoming frame** (flex) — blueprint-framed, padding `8,11,8,7`, title `Incoming frame — line 168 / 256`. Body row gap **9**: the frame itself is `flex:none; height:100%; aspect-ratio:5/4` — i.e. **height-driven**, width = height × 1.25, fill `#1D2D3D`. Right of it, `Previous frames` nested group box (padding `6,10,6,5`, gap 4) holding a **2-column** grid, rows `1fr`, gap **6**, of six thumbnail figures: 1px border, hatch fill flexing, mode label mini 9px pinned top-left inset 3 on a `#F2F2F3` plate, caption padding `2,4` with 1px top border — call `.v2` 10px left, time mono 9px `#88898A` right. Action row (top padding 4, gap 4): `Save frame` primary h25 padding `0,9`, then `Abort` `Re-decode` `Copy to TX` `Log QSO` secondary h25, then a 6px track filled 66% with left margin 6, then `168 / 256` as `.v2`.
3. **Decode activity** (h 147) — row gap 9. Table flexes: 9 columns `UTC · Freq · Mode · Call · OCR · Grid · QRZ · SNR · Slant · Lines · State`, font 11, cell padding `2,5`, 5 rows. Right pane **250 wide**, 1px left border, padding-left **9**: kicker `DECODER TRACE`, then 6 lines of mono **10.5px**, line-height **1.6**, colour `#595A5B`.

**Right column (312 wide)** — vertical, gap **11**: `Frame metadata` (11 rows + two labelled 24-high inputs + two h22 buttons) · `Unattended RX` (5 rows) · `Session frames` (flexes, 15 `.r2` rows) · `Macros` (6 wrapping minis, gap 3).

### Band 4b — Transmit body

Same `236,*,312` grid, same gaps and padding.
- Left column: `TX mode` (segmented, row, 4×4 mode minis, a **26-high** select, 5 rows) · `Identification` (3 rows) · `Output` (flexes; 9 rows including a 70×6 track, then a `POWER / ALC METER` kicker + hatch panel min-height 18) · `Outgoing metadata` (two kicker-separated groups: 3 rows then 6 rows).
- Centre: the **Editor** group box, blueprint-framed, padding `8,11,8,7`. Toolbar row gap 3, bottom padding 5: `MOVE` (accent-filled) `CROP` `SCALE` `ROTATE` `TEXT` `BOX` `LINE` `MASK` `PICK`, a **1×16** divider with 3px side margins, `UNDO` `REDO` `FIT` `100%`, then right-aligned `SNAP GRID` `SAFE AREA` `640×496`. Body row gap 8: canvas column flexes (canvas 1px border, `#E7E7EA` fill, hatch overlay, centred mono 12px placeholder text, a **1px dashed accent-55% inset-14 safe-area rectangle**, a `DL2QSK` plate top-left inset 10 with padding `3,8` mono 14px letter-spacing .06em, and a bottom-right plate inset 10 padding `2,7` mono 11px); below it a **3-column** grid of six adjustment rows, gap `12` column / `5` row, each with a **70-wide** track. Right sub-column **186 wide**, gap 6: `Insert field` (12 token minis wrapping gap 3, a row, two h21 buttons) · `Text style` (3 rows) · `Saved templates` (flexes, scrolling **2-column** grid gap 5 of 4:3 figures, then `Fill & send` primary + `Save current`, h21). Footer row (top padding 4, gap 4): `Transmit` primary h26 padding `0,10`, `Tune 5 s` `Preview audio` `Halt` h26 padding `0,9`, a 6px track at 0%, `idle` as `.v2`.
- Right column: `PTT` (4 rows) · `Queue` (3 rows) · `Mode timing reference` (4-col table, cell padding `2,4`, 5 rows) · `TX log` (6-col table, 6 rows, then 2 rows) · `Recently sent` (flexes, scrolling **3-column** grid gap 6 of 4:3 figures, then two h22 buttons).

### Band 4c — Gallery body

```
Grid ColumnDefinitions="*,312"  gap 9   same padding
```
Left group box `Received — 1 284 frames` (padding `8,11,8,6`): filter row gap 5 — a **24-high** input capped at **230 wide**, then `ALL` / `TODAY` (accent-filled) / `14 MHz` / `UNLOGGED` / `FLAGGED` minis, right-aligned `SORT NEWEST` `SIZE M`. Then a **6-column** grid, `grid-auto-rows: 1fr`, gap **7**, top padding 4, clipped — figures as in Previous frames but caption padding `3,5`, call at full `.v2` 11px, meta mono 9.5px.

Right column, gap 11: `Selected frame` (flexes; a **190-high** hatch panel with 1px border, 5 rows, three h24 buttons gap 4) · `Storage` (3 rows).

### Band 5 — status bar (h 25)

Padding `8,4`, gap **3**, all `.mini`: `RECEIVING` (accent-filled) · `14.230.00 USB · M1` · `SCOTTIE 1` · `LINE 168 / 256` · `SNR 21.6 dB` · `SLANT +3.4 ppm` · `BUF 512 · 0 XRUN` · `DISK 14.2 GB` · then right-aligned `FRAMES TODAY 26` · `LOG 4412 ENTRIES`. 1px top border.

---

## 6. Avalonia pitfalls that cost us time already

1. **Template-priority beats styles.** FluentTheme's `ButtonSpinner` sets sizes inside its own template, so a `Selector="ButtonSpinner /template/ Button"` setter loses. Replace the whole `ControlTheme` (we did: 13px-wide spinner buttons). Same trap on `Slider`, `TabItem` header, and `ComboBox` toggle glyph — if a setter appears to do nothing, it's this.
2. **Comma selectors need one type.** `Selector="TextBox, ComboBox"` cannot set a property the two don't share (`VerticalContentAlignment`). Split per type.
3. **No `Auto` on the five window bands.** See §1.
4. **Aspect ratio** — the incoming frame is height-driven 5:4. Do it in a `MeasureOverride` (as `PlotView` does), never a self-referential `Width="{Binding Bounds.Height…}"`, which oscillates.
5. **Thumbnail grids** need `UniformGridLayout` with `MinItemWidth`/`MinItemHeight` in the target ratio and `ItemsStretch="None"` — Previous frames and Gallery thumbnails are **5:4**, Saved templates and Recently sent are **4:3**. Getting `ItemsStretch` wrong is what makes them square.
6. **`DynamicResource` runs no type converter.** A bare `x:Double` resource bound to a `Thickness` property throws at runtime. Keep the paired `x:Double` + `Thickness` resources.
7. **Group-box title notch** must sit *above* the border in z-order with an opaque `#F2F2F3` background, or the border draws through the text.
8. **`LetterSpacing` is device pixels, not em.** Convert: px = em × font-size.
9. **Barlow's line box** is taller than the CSS line-height at these sizes. Set `LineHeight` explicitly on `.r2`/`.v2` text (16.5) rather than letting it default, or every 17px row becomes 19–20px and the columns drift apart by the bottom of the board.

---

## 7. Verification checklist

Measure, don't eyeball. Run at 1920×1080, DPI 1.0, and check:

- [ ] Bands measure 26 / 81 / 26 / * / 25.
- [ ] Left column exactly 236 wide, right column exactly 312, gutters exactly 9.
- [ ] Centre column rows measure 150 / * / 147.
- [ ] A default `.r2` row is 17 tall; ten stacked rows span 170 + 9 separators.
- [ ] A `.mini` chip is 17 tall.
- [ ] A stepper is 22 tall with 18-wide buttons.
- [ ] The frequency readout's cap height matches a 40px Barlow-derived mono; the `.000` tail is visibly lighter.
- [ ] Group-box titles sit on the border line, break it, and are 9.5px `#416180`.
- [ ] Incoming frame is exactly 1.25 × its own height wide.
- [ ] Previous-frames thumbnails are 5:4, two per row, 6px gaps.
- [ ] Gallery is 6 columns, 7px gaps, items 5:4, clipped not scrolled.
- [ ] No rounded corner anywhere; no shadow anywhere; no gradient except the 135° hatch.
- [ ] Only one accent-filled chip per band (the active one).

Establish one measured control as the token and derive the rest from it. If the `.mini` chip measures 17 tall on screen, everything else in §4 is trustworthy; if it measures 19, the font metrics are off and §2's type section is where to fix it — not the individual views.

