# Padding/spacing normalization — mockup CSS → Avalonia Thickness

**Status (2026-08-30): implemented.** This table was Phase 1's cited source for exact Avalonia
`Padding`/`Thickness` values (`PHASE1-ATOM-DESIGN.md` §1/item 1 cites it directly), and the
Industry redesign it feeds has since shipped — the named composite `Thickness` resources in
§1/§1b (`IndustryGbPadding`, `IndustryGbPaddingWide`, `IndustryGbPaddingWideDeep`,
`IndustryGbPaddingFav`, `IndustryGbPaddingNested`, `IndustryMiniPadding`,
`IndustryTableCellPadding`/`…Narrow`) all exist verbatim in
`src/ScanlineStudio.UI/Styles/AtomsTokens.axaml` with the exact values cited below. Kept as the
CSS→Avalonia conversion reference, not an open task.

Re-derived directly from `SSTV Console.dc.html`'s local `&lt;style&gt;` block (lines 14-72) and the
`&lt;div class="dv-opt" id="2a"&gt;` markup (lines 78-618) — **not** from `LAYOUT-SPEC.md`'s prose,
which mixes CSS-order and Avalonia-order transcriptions inconsistently within the same tables
(confirmed by 2 rounds of auditor review). Every value below cites its source line in
`SSTV Console.dc.html`. Use this file going forward instead of re-deriving from the mockup each
time.

---

## 0. The conversion rules (read this before using any table row below)

**CSS padding shorthand is clockwise from the top, always:**
- 1 value `V`: all four sides = V
- 2 values `V1 V2`: **top/bottom = V1, left/right = V2** (vertical, then horizontal)
- 3 values `V1 V2 V3`: top = V1, left/right = V2, bottom = V3
- 4 values `V1 V2 V3 V4`: top = V1, right = V2, bottom = V3, left = V4

**Avalonia `Thickness` string order is NOT the same axis order as CSS, at both arities:**
- 4 values: `Left,Top,Right,Bottom` — different SEQUENCE than CSS's T,R,B,L, same four numbers.
- 2 values: **`Left/Right,Top/Bottom`** — i.e. **horizontal, then vertical**. This is the *opposite*
  axis order from CSS's 2-value form (vertical, then horizontal). A literal copy of a CSS 2-value
  shorthand into an Avalonia 2-value `Thickness` string is silently wrong on BOTH arities you might
  naively try: same-order-different-meaning at 2 values, different-order-same-meaning at 4 values.
- 1 value: uniform on both systems — the only shorthand that's ever safe to copy directly.

**Rule for every atom/site below: always write the explicit 4-value `Left,Top,Right,Bottom` form in
XAML.** Never use Avalonia's 1- or 2-value shorthand when the source value came from CSS — the
2-value axis flip above is a second, independent trap beyond the T,R,B,L-vs-L,T,R,B one the round-1
auditor review already caught, and it wasn't caught by that review (found while building this
table). Confirmed against `color-mix`/inline-style values throughout `2a`; no exceptions found.

---

## 1. Local `&lt;style&gt;` block — atom defaults (lines 14-72)

Only atoms **actually instantiated inside `id="2a"`** are load-bearing for this port; others in the
shared style block belong to different `dv-opt` design variants in the same file and are flagged
NOT USED IN 2A — building them is optional future-proofing, not required for pixel parity.

| Class | Raw CSS | Expansion | Avalonia `Padding` (L,T,R,B) | Used in 2a? | Line |
|---|---|---|---|---|---|
| `.gb` | `padding:11px 7px 6px` | T=11, L=R=7, B=6 | `7,11,7,6` | **YES** — every group box | 50/62 |
| `.gbt` | `padding:0 5px` | T=B=0, L=R=5 | `5,0,5,0` | **YES** — every group-box title | 51/63 |
| `.mini` | `padding:1px 5px` | T=B=1, L=R=5 | `5,1,5,1` | **YES** — the §7 calibration chip | 58/69 |
| `.stepper span` | `padding:0 6px` | T=B=0, L=R=6 | `6,0,6,0` | **YES** (stepper value cell) | 38 |
| `.stepper button` | `padding:0` | uniform 0 | `0,0,0,0` | **YES** | 36 |
| `.tabbtn2` | `padding:5px 11px 4px` | T=5, L=R=11, B=4 | `11,5,11,4` | **YES** (Receive/Transmit/Gallery tab strip) | 55/67 |
| `.mbar` | `padding:1px 6px` | T=B=1, L=R=6 | `6,1,6,1` | **YES** (menu bar row) | 47/59 |
| `.mbar span` | `padding:2px 7px` | T=B=2, L=R=7 | `7,2,7,2` | **YES** (menu items) | 48/60 |
| `.r2` | *(no padding — gap/line-height only)* | — | — | **YES** | 52/64 |
| `.row` | `padding:2px 0` | T=B=2, L=R=0 | `0,2,0,2` | **NOT USED IN 2A** — 2a uses `.r2` throughout, never `.row` | 30 |
| `.chip` | `padding:2px 7px` | T=B=2, L=R=7 | `7,2,7,2` | **NOT USED IN 2A** — 2a never writes `class="chip"`; LAYOUT-SPEC §4 documents this atom anyway, but it's not part of 2a's own pixel target | 45 |
| `.sbc` | `padding:2px 9px` | T=B=2, L=R=9 | `9,2,9,2` | **NOT USED IN 2A** — the status bar (line 607-618) uses `.mini` exclusively, never `.sbc`. Same flag as `.chip`. | 57 |
| `.pnl` | `padding:9px 11px 11px` | T=9, L=R=11, B=11 | `11,9,11,11` | **NOT USED IN 2A** (different design variant's panel) | 28 |
| `.dv-tid`/`.dv-oid` | `padding:3px 7px` | T=B=3, L=R=7 | `7,3,7,3` | dev-tool chrome around the mockup itself, not app UI | 20/24 |
| `.knob` | *(no padding — radial control)* | — | — | **NOT USED IN 2A** | 70 |

**Finding worth flagging (not fixing here):** LAYOUT-SPEC.md's atom table (§4) includes `.chip` and
`.sbc` as if they're part of 2a's vocabulary. Neither actually appears in the `2a` markup. Building
them in Phase 1 is harmless (cheap, may be useful for Phase 6's extrapolated Options/Logbook work)
but they are not required for 2a pixel-parity and no Phase 2-5 site should expect to find one.

---

## 1b. Buttons, `.input`, `.seg-opt` — CORRECTED, previously missing from this file

Auditor-caught gap (Phase 0 code-review): these all use CSS **horizontal-only 2-value shorthand**
(`padding: 0 Npx`), which is exactly the axis the §0 2-value trap inverts — the single highest-risk
omission this file could have had, since LAYOUT-SPEC §4/§5 write several of these in bare CSS
2-value order ("`.input` padding `0,6`", "stepper padding `0,6`", "Favourites buttons padding
`0,10`").

**Buttons** (`.btn-primary`/`.btn-secondary`, always `padding: 0 Npx` — horizontal only, vertical
centred by `height`/`line-height` instead):

| Height | Horizontal pad | Avalonia `Padding` | Representative sites | Lines |
|---|---|---|---|---|
| 36 | 10px | `10,0,10,0` | Favourite recall buttons | 118 |
| 36 | 11px | `11,0,11,0` | Store current | 123 |
| 26 | 9-10px | `10,0,10,0` (Receiving/Halt/Transmit), `9,0,9,0` (Tune/Preview audio/Halt) | Transceiver Receiving/Halt (134-135), TX Editor footer Transmit (494), Tune/Preview/Halt (495-497) | 134-135, 494-497 |
| 25 | 9px | `9,0,9,0` | Save frame/Abort/Re-decode/Copy to TX/Log QSO | 275-279 |
| 24 | 8px | `8,0,8,0` | Gallery Open in log/Export/Re-decode | 593-595 |
| 22 | 6-8px | `6,0,6,0` (Re-sync/Reset), `7,0,7,0` (Lookup QRZ/Flag), `8,0,8,0` (Refill&queue/Open in editor) | 181-182, 340-341, 555-556 |
| 21 | 6px | `6,0,6,0` | Fill all/Clear, Fill&send/Save current | 465-466, 487-488 |

**Do not assume one padding value per height** — 36px alone has two different horizontal values
(10 vs. 11) depending on site; always cite the actual inline value, don't interpolate from height.

**`.input`/`select`** — every site uses `padding:0 6px` regardless of height (24px fields and the
26px TX-mode `&lt;select&gt;` alike): Avalonia `6,0,6,0`. Sites: Frame-metadata Note/Override-callsign
(337-338), TX-mode select (382), Gallery search input (567).

**`.seg-opt`** (inline override on each segmented-control label, two distinct values found, both
confirmed against LAYOUT-SPEC §4's own "`.seg` … options padding `2,10` … (or `3,6` … stretched
when full-width)" description):
| Site | Raw CSS | Avalonia `Padding` | Line |
|---|---|---|---|
| VFO sideband (USB/LSB/FM) | `padding:2px 10px` | `10,2,10,2` | 100-102 |
| Mode Auto/Lock (stretched, full-width) | `padding:3px 6px` | `6,3,6,3` | 162-163 |
| TX-mode Auto/Manual (stretched, full-width) | `padding:3px 6px` | `6,3,6,3` | 373-374 |

**Menu-bar callsign `.mini` override** (Band 1, `DL2QSK · JO31` chip) — this is a SEPARATE override
from the brand-label span's `padding-right:12px` noted in §2's Band-1 section below; both exist on
the same menu bar. `padding:1px 8px` (also bumps `font-size` to 12px) → Avalonia `8,1,8,1`, distinct
from `.mini`'s own class default `5,1,5,1`. Line 87.

**Kicker resets** (a `.mini` instance with `border:0;padding:0` — turns the chip into a bare
accent-colored label with letter-spacing, no border/background/padding at all): Avalonia `0,0,0,0`.
Sites: VFO kicker (95), Favourites row hint (126), Signal-quality "SNR PER LINE"/"LUMINANCE
HISTOGRAM" (213, 216), Decoder-trace "DECODER TRACE" (310), Session-frames row's mode tag (354),
Output "POWER / ALC METER" (409), Outgoing-metadata "IN THE SIGNAL"/"BURNED INTO THE PICTURE"
(414, 418).

---

## 2. Inline overrides, in document order (band → card → site)

Format: **site** — raw CSS `padding:` (or equivalent multi-property override) → Avalonia `L,T,R,B`
→ other layout values (gap/width/height) on the same element. Line = `SSTV Console.dc.html`.

### Band 1 — menu bar (line 82-89)
No inline padding override on the `.mbar` div itself (uses class default `6,1,6,1` above). Two
separate inline overrides exist on this band, not one: the brand label span's `padding-right:12px`
(single-side, layer over an otherwise-zero padding on that specific span — implement as a
dedicated right-side value, not a shared resource, line 83), AND the right-aligned callsign `.mini`
chip's `padding:1px 8px` (→ Avalonia `8,1,8,1`, see §1b, line 87) — do not implement only one of
these two and assume the band is done.

### Band 2 — radio header (line 91-140)
| Site | Raw CSS | Avalonia `Padding` | Gap | Line |
|---|---|---|---|---|
| Outer row | `padding:7px 9px` | `9,7,9,7` | 10 | 91 |
| VFO block (`.blueprint`) | `padding:5px 14px` | `14,5,14,5` | 14 | 92 |
| Favourites `.gb` | `padding:11px 8px 5px` | `8,11,8,5` | 5 | 114 |
| Favourite button | `padding:0 10px` (height 36 is a separate, additional constraint — this row IS padding-driven, corrected from an earlier pass of this table that mischaracterized it) | `10,0,10,0` | line-height 1.15 | 118 |
| Transceiver `.gb` | `padding:11px 8px 5px` | `8,11,8,5` | *(default gb gap 3)* | 131 |
| Transceiver RX-lvl row | `.r2` default + `padding-top:3px` on first row only | `0,3,0,0` added on top of `.r2`'s own zero padding | — | 137 |

Confirms LAYOUT-SPEC §5's "Favourites, Transceiver: padding 8,11,8,5" claim is correct **only if
read as its own stated (T,R,B,L) convention** (T=11,R=8,B=5,L=8 → their notation "11,8,5,8"; my
independently-derived Avalonia L,T,R,B is `8,11,8,5` — same four numbers, genuinely different
column order. Both are internally consistent; use the Avalonia-order column above when writing XAML,
never LAYOUT-SPEC's own (T,R,B,L)-labelled numbers directly into a `Padding=` attribute.

### Band 3 — tab strip (line 142-154)
Outer row: `padding:0 6px` → `6,0,6,0` (mirrors `.mbar`'s own horizontal-only pattern). No
additional per-tab override beyond `.tabbtn2`'s class default above. Right-aligned status-chip run
gap = 5. Line 142.

### Receive tab — left column (line 156-222)
| Card | Raw CSS | Avalonia `Padding` | Gap | Line |
|---|---|---|---|---|
| Outer 3-col grid | `padding:11px 9px 8px` | `9,11,9,8` | 9 | 157 |
| Left column stack | *(no padding, `gap:11px` only)* | — | 11 | 159 |
| Mode `.gb` | *(class default)* | `7,11,7,6` | 3 | 160 |
| Mode-grid pills | `gap:2px` | — | 2 | 165 |
| Sync&slant `.gb` | *(class default)* | `7,11,7,6` | 3 | 175 |
| Advanced-timing block | `padding-top:3px` (single side, over the `&lt;details&gt;`'s own zero) | `0,3,0,0` added | — | 185 |
| Input-chain `.gb` | *(class default)* | `7,11,7,6` | 3 | 193 |
| L/R meter block | `padding-top:4px` (single side) | `0,4,0,0` added | 3 | 202 |
| Signal-quality `.gb` | `flex:1;min-height:0` — no padding override | `7,11,7,6` | 3 | 212 |
| "LUMINANCE HISTOGRAM" kicker | `padding-top:3px` (single side) | `0,3,0,0` added | — | 216 |

### Receive tab — centre column (line 225-322)
| Card | Raw CSS | Avalonia `Padding` | Gap | Line |
|---|---|---|---|---|
| Centre grid | *(no padding — inherits outer 9,11,9,8)*, `grid-template-rows:150px 1fr 147px` | — | 11 | 225 |
| Spectrum·waterfall `.gb` | `padding:11px 8px 6px` | `8,11,8,6` | *(default 3)* | 227 |
| Spec/WF/controls inner grid | *(no padding)*, `gap:7px` | — | 7 | 228 |
| Incoming-frame `.gb.blueprint` | `padding:11px 8px 7px` | `8,11,8,7` | — | 252 |
| Incoming-frame body row | `gap:9px` | — | 9 | 254 |
| Previous-frames nested `.gb` | `padding:10px 6px 5px;gap:4px` | `6,10,6,5` | 4 | 259 |
| Previous-frames thumbnail grid | `gap:6px` | — | 6 | 260 |
| Thumbnail figcaption | `padding:2px 4px` | `4,2,4,2` | 4 (row gap) | 264 |
| Incoming-frame action row | `padding-top:4px` (single side), `gap:4px` | `0,4,0,0` added | 4 | 274 |
| Decode-activity `.gb` | `padding:11px 8px 6px` | `8,11,8,6` | — | 285 |
| Decode-activity body row | `gap:9px` | — | 9 | 286 |
| Table header cell | `padding:2px 5px` | `5,2,5,2` | — | 289 |
| Table body cell | `padding:2px 5px` | `5,2,5,2` | — | 296 |
| Decoder-trace side panel | `padding-left:9px` (single side) | `9,0,0,0` added | — | 309 |

**Confirms LAYOUT-SPEC §3's "Spectrum·waterfall, Incoming frame, Decode activity, Received,
top-level Rx/Tx panes: 11,8,6,8 (Incoming frame & Editor: bottom 7)" claim** — my independently
derived values match exactly under the same (T,R,B,L)-vs-Avalonia(L,T,R,B) reordering already
noted above: Spectrum/Decode-activity really are T=11,R=8,B=6,L=8 (Avalonia `8,11,8,6`), and
Incoming-frame/Editor really do override bottom to 7 (Avalonia `8,11,8,7`). No discrepancy found.

### Receive tab — right column (line 324-364)
| Card | Raw CSS | Avalonia `Padding` | Gap | Line |
|---|---|---|---|---|
| Right column stack | *(no padding)*, `gap:11px` | — | 11 | 324 |
| Frame-metadata `.gb` | *(class default)* | `7,11,7,6` | 3 | 325 |
| Note/Override-callsign field | `padding-top:4px` (single side, first field only) | `0,4,0,0` added | — | 337 |
| Frame-metadata action row | `padding-top:2px` (single side), `gap:4px` | `0,2,0,0` added | 4 | 339 |
| Unattended-RX `.gb` | *(class default)* | `7,11,7,6` | 3 | 344 |
| Session-frames `.gb` | `flex:1;min-height:0` — no padding override | `7,11,7,6` | 3 | 351 |
| Macros `.gb` | *(class default)* | `7,11,7,6` | 3 | 358 |
| Macros chip wrap | `gap:3px` | — | 3 | 359 |

### Transmit tab — left column (line 369-425)
| Card | Raw CSS | Avalonia `Padding` | Gap | Line |
|---|---|---|---|---|
| Outer 3-col grid | `padding:11px 9px 8px` | `9,11,9,8` | 9 | 369 |
| TX-mode `.gb` | *(class default)* | `7,11,7,6` | 3 | 371 |
| Mode-grid pills | `gap:2px`, `padding-top:2px` on the grid itself | `0,2,0,0` added | 2 | 377 |
| Identification `.gb` | *(class default)* | `7,11,7,6` | 3 | 393 |
| Output `.gb` | `flex:1;min-height:0` — no padding override | `7,11,7,6` | 3 | 398 |
| Output drive row | `gap:3px` on the stacked label+track sub-row | — | 3 | 400 |
| Power/ALC meter block | `padding-top:5px` (single side), `gap:3px` | `0,5,0,0` added | 3 | 408 |
| Outgoing-metadata `.gb` | *(class default)* | `7,11,7,6` | 3 | 413 |
| "BURNED INTO THE PICTURE" kicker | `padding-top:3px` (single side) | `0,3,0,0` added | — | 418 |

### Transmit tab — Editor (line 428-501)
| Site | Raw CSS | Avalonia `Padding` | Gap | Line |
|---|---|---|---|---|
| Editor `.gb.blueprint` | `padding:11px 8px 7px` | `8,11,8,7` | — | 428 |
| Toolbar row | `padding-bottom:5px` (single side), `gap:3px` | `0,0,0,5` added | 3 | 430 |
| Toolbar divider | `margin:0 3px` (this is `Margin`, not `Padding`) | Avalonia `Margin="3,0,3,0"` | — | 434 |
| Body row | `gap:8px` | — | 8 | 438 |
| Canvas column | `gap:5px` | — | 5 | 439 |
| Canvas safe-area inset | `inset:14px` (uniform, not padding — a positioning offset) | `Margin="14"` equivalent on the overlay | — | 443 |
| Callsign plate (top-left) | `padding:3px 8px` | `8,3,8,3` | — | 444 |
| Report plate (bottom-right) | `padding:2px 7px` | `7,2,7,2` | — | 445 |
| Adjustments grid | `gap:5px 12px` (row-gap, column-gap — CSS `gap` shorthand is ROW then COLUMN, opposite axis order convention from padding) | Avalonia `Grid.RowSpacing="5" ColumnSpacing="12"` | — | 447 |
| Insert-field nested `.gb` | `padding:10px 6px 5px;gap:2px` | `6,10,6,5` | 2 | 457 |
| Insert-field chip wrap | `gap:3px` | — | 3 | 458 |
| Insert-field action row | `padding-top:2px` (single side), `gap:3px` | `0,2,0,0` added | 3 | 464 |
| Text-style nested `.gb` | `padding:10px 6px 5px;gap:2px` | `6,10,6,5` | 2 | 469 |
| Saved-templates nested `.gb` | `padding:10px 6px 5px;gap:3px` | `6,10,6,5` | 3 | 474 |
| Saved-templates grid | `gap:5px` | — | 5 | 475 |
| Saved-templates figcaption | `padding:2px 4px` | `4,2,4,2` | — | 479 |
| Saved-templates action row | `padding-top:2px` (single side), `gap:3px` | `0,2,0,0` added | 3 | 486 |
| Editor footer row | `padding-top:4px` (single side), `gap:4px` | `0,4,0,0` added | 4 | 493 |

**Note:** the nested-group-box row's `6,10,6,5` (Avalonia) — cross-checked against LAYOUT-SPEC §3's
"nested (Previous frames, Insert field, Text style, Saved templates): 10,6,5,6" claim — again the
same four numbers under their (T,R,B,L) convention (T=10,R=6,B=5,L=6 → Avalonia `6,10,6,5`), no
discrepancy. Gaps differ per nested box though (Previous frames=4, Insert field=2, Text style=2,
Saved templates=3) — LAYOUT-SPEC's own "4 / 2 / 2 / 3" already got this right; confirmed against
raw CSS here too (lines 259, 457, 469, 474 respectively).

### Transmit tab — right column (line 503-559)
| Card | Raw CSS | Avalonia `Padding` | Gap | Line |
|---|---|---|---|---|
| Right column stack | *(no padding)*, `gap:11px` | — | 11 | 503 |
| PTT `.gb` | *(class default)* | `7,11,7,6` | 3 | 504 |
| Queue `.gb` | *(class default)* | `7,11,7,6` | 3 | 510 |
| Mode-timing `.gb` | *(class default)* | `7,11,7,6` | 3 | 515 |
| Table header/body cell | `padding:2px 4px` (narrower than Receive's `2px 5px`) | `4,2,4,2` | — | 517/519 |
| TX-log `.gb` | *(class default)* | `7,11,7,6` | 3 | 527 |
| TX-log table cell | `padding:2px 4px` | `4,2,4,2` | — | 529/531 |
| TX-log footer rows | `padding-top:4px` (single side, first row only) | `0,4,0,0` added | — | 539 |
| Recently-sent `.gb` | `flex:1;min-height:0` — no padding override | `7,11,7,6` | 3 | 542 |
| Recently-sent grid | `gap:6px` | — | 6 | 543 |
| Recently-sent figcaption | `padding:2px 4px` | `4,2,4,2` | — | 547 |
| Recently-sent action row | `padding-top:3px` (single side), `gap:4px` | `0,3,0,0` added | 4 | 554 |

**Confirms LAYOUT-SPEC's implicit narrow-table-cell claim** (§4: "cell padding 2,5 (2,4 in the
narrow right-column tables)") — my direct read confirms Receive's Decode-activity table uses `2px
5px` (Avalonia `5,2,5,2`) while BOTH Transmit right-column tables (Mode-timing, TX-log) use `2px
4px` (Avalonia `4,2,4,2`). Matches exactly.

### Gallery tab (line 564-604)
| Card | Raw CSS | Avalonia `Padding` | Gap | Line |
|---|---|---|---|---|
| Outer 2-col grid | `padding:11px 9px 8px` | `9,11,9,8` | 9 | 564 |
| Received `.gb` | `padding:11px 8px 6px` | `8,11,8,6` | — | 565 |
| Filter row | `gap:5px` | — | 5 | 566 |
| Thumbnail grid | `gap:7px`, `padding-top:4px` (single side) | `0,4,0,0` added | 7 | 572 |
| Thumbnail figcaption | `padding:3px 5px` (wider than Previous-frames' `2px 4px` — Gallery's own figcaption is bigger) | `5,3,5,3` | — | 576 |
| Right column stack | *(no padding)*, `gap:11px` | — | 11 | 584 |
| Selected-frame `.gb` | *(class default, `flex:1`)* | `7,11,7,6` | — | 585 |
| Selected-frame action row | `padding-top:4px` (single side), `gap:4px` | `0,4,0,0` added | 4 | 592 |
| Storage `.gb` | *(class default)* | `7,11,7,6` | 3 | 598 |

**Flag:** Gallery's thumbnail figcaption padding (`3,5,3,5` Avalonia) genuinely differs from
Previous-frames' figcaption (`2,4,2,4` Avalonia, line 264) and Saved-templates/Recently-sent's
figcaption (`2,4,2,4`, lines 479/547) — three of the four thumbnail-figure sites share one caption
padding, Gallery alone uses a slightly larger one. Not a contradiction in the mockup; a genuine,
deliberate(-looking) per-site difference — implement the thumbnail-figure atom (Phase 1) with an
overridable caption-padding parameter, not one hardcoded value.

### Status bar (line 607-618)
Outer row: `padding:4px 8px` → Avalonia `8,4,8,4`, `gap:3px`. All chips are `.mini` (default
`5,1,5,1`) — confirms `.sbc` is genuinely unused here (see §1 above), matching LAYOUT-SPEC's own
prose despite its atom table listing `.sbc` as if relevant.

---

## 3. Gap-vs-padding axis order (CSS `gap` shorthand — separate landmine from padding)

CSS `gap: ROW COLUMN` (row-gap first, column-gap second) — confirmed at line 447
(`gap:5px 12px` → `Grid.RowSpacing="5"`, `Grid.ColumnSpacing="12"`, NOT the reverse). Every other
`gap:` value found in `2a` is a single uniform number (safe on both systems) except this one site.
Flag this explicitly in Phase 1/3-5 implementation: a single-number `gap` never needs this, only
the one two-value case above.

---

## 4. Summary for implementers

- **~100 distinct padding/spacing sites** catalogued above across §1 (atom defaults), §1b (buttons/
  inputs/segmented controls — added after an auditor-caught gap in an earlier pass of this file,
  see that section's own note), and §2 (every inline override actually used in `2a`, band by band),
  plus 3 unused-in-2a atoms (`.row`/`.chip`/`.sbc`) flagged for awareness rather than omitted
  silently. Exact row count isn't load-bearing; completeness of coverage is — if a padding site in
  `2a` isn't here, treat that as a gap in this file, not as "not part of the design."
- **Two independent CSS→Avalonia order traps**, not one: (a) 4-value clockwise-T,R,B,L vs.
  Avalonia's L,T,R,B (round-1/2 auditor finding), and (b) 2-value CSS (vertical,horizontal) vs.
  Avalonia 2-value (horizontal,vertical) — found while building this table, not previously flagged.
  Both are avoided by the same discipline: **always write explicit 4-value `L,T,R,B` in XAML.**
- **One CSS `gap` axis-order site** (row,column vs. Avalonia's RowSpacing/ColumnSpacing — same
  numbers, different property names, no real ambiguity once named explicitly) at line 447.
- **No internal contradiction found** in the mockup's own CSS (no class declared with two
  different padding values) — LAYOUT-SPEC.md's §3/§5 group-box padding-variant tables both check
  out exactly once their stated (T,R,B,L) convention is accounted for.
- **`.chip` and `.sbc`** are real atoms in the shared style block but are not instantiated anywhere
  inside `id="2a"` — LAYOUT-SPEC.md's atom table documents them as if part of this design's target
  vocabulary; they aren't required for 2a pixel-parity.
