# Legacy `.mtm`/`.mti` binary format

Reverse-engineered from `yoniq-old/YONIQ-main/Draw.cpp`/`Draw.h` (the authoritative source — every
field below is read from an actual `SaveToStream`/`LoadFromStream` method body, not inferred from a
hex dump) and validated against all 12 real sample files available locally (`def1-5.mtm`, `t1-5.mtm`,
`Current.mtm`, `Stock/List.mtm`): **12/12 parse to a byte-exact clean EOF** with the layout below. A
reference parser (Python, brace/field-accurate to the C++ source) was written and run against every
sample as part of this pass; see "Validated against real files" for what it proved and did not prove.

`.mtm` and `.mti` are the same format (confirmed against `Main.cpp`, see below) — a serialized
`CDrawGroup`: a flat container holding an ordered list of drawable elements (`CDraw` subtypes),
written by walking the list and calling each element's own polymorphic `SaveToStream`. There is no
file-level version field — **every element carries its own `m_Ver`**, written first (via the shared
`CDraw::SaveToStream` base) and read back before any version-gated field in that same element's
`LoadFromStream` override. A `CDrawGroup` can itself contain nested `CDrawGroup` elements (the format
is recursive), though no local sample does.

All integers are little-endian (native x86 byte order, unmarshaled). All multi-byte fields are
written with individual `TStream::Write(&field, sizeof(field))` calls — there is no struct-level
compiler padding to account for, since nothing is written as a packed struct.

## `CM_*` element type tags

Read at `Draw.h:79-90`. Sequential from 0, exact values:

| Name | Value | C# element it maps to (see "Proposed shape" in `spec/15-template-designer.md`) |
|---|---|---|
| `CM_SELECT` | 0 | never appears in a stream (an in-memory-only selection state) |
| `CM_GROUP` | 1 | nested `CDrawGroup` — no direct `TemplateElement` equivalent today |
| `CM_LINE` | 2 | closest to `TemplateBoxElement` (see "Skippable-without-loss" note below) |
| `CM_BOX` | 3 | `TemplateBoxElement` |
| `CM_TEXT` | 4 | `TemplateTextElement` |
| `CM_PIC` | 5 | `TemplateImageElement` |
| `CM_BOXS` | 6 | `TemplateBoxElement` (styled variant — see note below) |
| `CM_TITLE` | 7 | no existing equivalent — see CDrawTitle section |
| `CM_OLE` | 8 | **not importable** — see CDrawOle section |
| `CM_LIB` | 9 | **not importable, but cleanly skippable** — see CDrawLib section |
| `CM_TLIST` | 0x8000 | a flag bit ORed elsewhere in the app, never a stream tag on its own |

The tag is written as the very first field of every element's record, by `CDraw::SaveToStream`
(`m_Command`, `int`, 4 bytes) — but it is **read by the parent**, not by the element's own
`LoadFromStream`: `CDrawGroup::LoadFromStream` reads the 4-byte tag itself, uses it to pick which
concrete class to instantiate (`MakeItem(cmd)`), and only then calls that instance's
`LoadFromStream`, which starts reading at `m_Ver` (the second field). This is a caller/callee split
of one write, not an asymmetry — the byte layout is identical either way.

## Class hierarchy and which classes add fields to the stream

```
CDraw                     (Draw.h:109, Draw.cpp:408/426)  -- owns the base record layout
├── CDrawLine              (Draw.h:170) -- NO override, NO extra member fields: identical wire record to CDraw
├── CDrawBox               (Draw.h:188) -- NO override, NO extra member fields: identical wire record to CDraw
│   ├── CDrawBoxS          (Draw.h:207) -- NO override, NO extra member fields: identical wire record to CDraw
│   │   └── CDrawText      (Draw.h:255, Draw.cpp:3097/3143) -- overrides, adds fields
│   ├── CDrawTitle         (Draw.h:219, Draw.cpp:1337/1354) -- overrides, adds fields
│   ├── CDrawPic           (Draw.h:351, Draw.cpp:3729/3746) -- overrides, adds fields
│   ├── CDrawOle           (Draw.h:392, Draw.cpp:3922/3932) -- overrides, adds fields, NOT skippable
│   ├── CDrawLib           (Draw.h:450, Draw.cpp:4569/4584) -- overrides, adds fields, skippable
│   └── CDrawGroup         (Draw.h:524, Draw.cpp:4947/4963) -- overrides, adds fields, recursive
```

**`CDrawLine`, `CDrawBox`, `CDrawBoxS` declare zero extra member variables** (confirmed by reading
their full class bodies in `Draw.h` — nothing between the opening and closing brace besides method
declarations) and none of the three declares its own `SaveToStream`/`LoadFromStream`. The explicit
qualified call `CDrawBox::LoadFromStream(...)` seen in several subclasses' overrides is valid C++
even though `CDrawBox` never defines it — the compiler resolves the qualified name up the inheritance
chain to `CDraw::LoadFromStream`, the nearest ancestor that actually declares it. **So `CM_LINE`,
`CM_BOX`, and `CM_BOXS` all use the exact same wire record as the base `CDraw` type below — no
extra bytes, no extra behavior.** A `CDrawLine` on-screen is presumably rendered differently from a
`CDrawBox` using only the base fields already present (e.g. a zero-area box read as a line, or a
render-time interpretation keyed off `m_Command`) — that distinction lives in the *rendering* code,
not in anything additional on the wire, and rendering code was out of scope for this pass.

## `CDraw` base record (every element's leading bytes)

`Draw.cpp:408` (`SaveToStream`) / `Draw.cpp:426` (`LoadFromStream`). Fields in stream order:

| Field | C++ type | Wire size | Notes |
|---|---|---|---|
| `m_Command` | `int` | 4 | the `CM_*` tag — read by the PARENT, see above, not inside this method |
| `m_Ver` | `int` | 4 | this element's own format version, read first, gates every later conditional field in this element (including in subclass overrides) |
| `m_X1` | `int` | 4 | |
| `m_Y1` | `int` | 4 | |
| `m_X2` | `int` | 4 | |
| `m_Y2` | `int` | 4 | bounding box, in the mode's own 320×256-normalized coordinate space (see `CDrawGroup.m_SX`/`m_SY` below) |
| `m_LineColor` | `TColor` | 4 | **VCL `TColor`/Win32 `COLORREF` byte order: `0x00BBGGRR`, not RGB** — swap R and B when mapping to a `System.Drawing`/`Avalonia` color. This is a standard, well-documented Win32 convention, not something derived from this codebase — stated here because it is easy to get backwards during a port. |
| `m_LineStyle` | `TPenStyle` | **1** | **Empirically corrected, not a source-only read.** `TPenStyle` is a VCL "typed enum" whose Borland/CodeGear `sizeof` is 1 byte (`Byte`-backed), not the 4 bytes a naive C++→C# port would assume from "it looks like a plain enum." Assuming 4 bytes here misaligns every subsequent field in every sample file; assuming 1 byte parses all 12 samples to a clean end-of-file. Verified against real bytes, not just plausible from source. |
| *(boxstyle block, conditional — see below)* | | | |
| `m_LineWidth` | `int` | 4 | see the disambiguation rule below — this field's OWN presence/position depends on what immediately precedes it |

### The `m_BoxStyle` / `m_LineWidth` disambiguation (magic-number lookahead, not a version gate)

This is the one piece of the base record that is **not** length-prefixed or `m_Ver`-gated — it is a
lookahead trick, and getting it wrong desyncs every byte after it:

- **Save**: if `m_BoxStyle != 0`, write a 4-byte magic `DWORD` `0x55aa0000` (with `m_BoxStyle`'s own
  low 16 bits, per the source, though only the high 16 bits are ever tested back on read), then write
  `m_BoxStyle` (`int`, 4 bytes). Always write `m_LineWidth` (`int`, 4 bytes) last, whether or not the
  magic block was written.
- **Load**: unconditionally read one `DWORD`. If its top 16 bits equal `0x55aa`, treat it as the
  magic marker — read `m_BoxStyle` (`int`, 4) and then `m_LineWidth` (`int`, 4) as two further reads.
  If the top 16 bits do **not** match, the `DWORD` just read **is** `m_LineWidth` itself, and
  `m_BoxStyle` is set to 0 (no separate read).
- **Practical effect**: `m_BoxStyle == 0` is overwhelmingly the common case (every real sample file
  has `boxstyle=0`), so most records are 4 bytes shorter here than the "always write the magic block"
  reading would suggest. A parser must implement the lookahead, not assume either branch.

## `CDrawGroup` (`CM_GROUP`) — Draw.cpp:4947 (save) / :4963 (load)

Always sets `m_Ver = 2` on save (current format). Fields after the `CDraw` base, all gated on the
group's own `m_Ver` (so an old-format group written before these fields existed round-trips too):

| Field | C++ type | Wire size | Gate |
|---|---|---|---|
| `m_TransX` | `int` | 4 | `m_Ver >= 1` |
| `m_TransY` | `int` | 4 | `m_Ver >= 1` |
| `m_TransCol` | `TColor` | 4 | `m_Ver >= 1` — transparent/background color, `0x00BBGGRR` like `m_LineColor` |
| `m_SX` | `int` | 4 | `m_Ver >= 2` — canvas width this group was authored against (every sample: 320) |
| `m_SY` | `int` | 4 | `m_Ver >= 2` — canvas height (every sample: 256) |
| `m_Cnt` | `int` | 4 | always — element count that follows |
| *(`m_Cnt` × element)* | | | each element is `[CM_* tag: int][that class's own record]`, recursively `MakeItem(cmd)`-dispatched |

`m_TransX`/`m_TransY` are UI transform-handle coordinates (where the group's own resize/move handle
last sat), not a per-element property — cosmetic editor state, not needed for a faithful import of
element content. `m_SX`/`m_SY` being consistently 320×256 across every real sample matches the
narrow-mode picture geometry; a template authored against a different mode's frame size may carry
different values here, and an importer should read them rather than assume 320×256.

**Top-level file = one `CDrawGroup` record, full stop.** `LoadTemplate`/`SaveTemplate`
(`Draw.cpp`, grep `LoadTemplate`/`SaveTemplate`) wrap a raw `TFileStream` directly around
`pItem->LoadFromStream`/`SaveToStream` — there is no separate file header, magic number, or checksum
outside the `CDrawGroup` record itself. The "32-byte prologue observed identically across sampled
files" noted in this project's earlier (source-blind) research is now explained exactly: it is
`CDraw`'s own fixed leading fields (`m_Command`+`m_Ver`+4 bounds ints+`m_LineColor`+1-byte
`m_LineStyle`+the boxstyle-or-linewidth `DWORD`, when `m_BoxStyle == 0`) = 4+4+16+4+1+4 = **33
bytes**, not 32 — the earlier note's byte count was an approximation from a hex dump, not a boundary
this format actually treats as special. There is nothing to validate beyond the field layout itself.

## `CDrawTitle` (`CM_TITLE`) — Draw.cpp:1337 (save) / :1354 (load)

Sets `m_Ver = 3` on save. A colored title/banner bar with up to a 4-stop gradient and an optional
image or sound.

| Field | C++ type | Wire size | Gate |
|---|---|---|---|
| `m_Type` | `int` | 4 | `m_Ver >= 1` — `1` = plain color-bar (only case seen in samples), `3` = image-backed, `4` = sound-triggered |
| `m_ColVert` | `int` | 4 | `m_Ver >= 2` — gradient orientation flag |
| `m_Col1..m_Col4` | `TColor` ×4 | 16 | always once `m_Ver >= 1` — 4-stop gradient colors, `0x00BBGGRR` |
| `m_Sound` | string (see below) | variable | only if `m_Ver >= 3 && m_Type == 4` |
| bitmap | see "Bitmap payload" below | variable | only if `m_Type == 3` |

## `CDrawText` (`CM_TEXT`) — Draw.cpp:3097 (save) / :3143 (load)

Sets `m_Ver = 7` on save — the most version-layered record in the format (7 historical revisions).
Every field below is read in this exact order, each gated on the `m_Ver` shown; a field guarded by a
gate that fails is simply never touched (the in-memory default, or a value derived from an older
field, stands instead — see the `m_Ver <= 6` special case at the end).

| Field | C++ type | Wire size | Gate |
|---|---|---|---|
| `m_Grade` | `int` | 4 | always |
| `m_Shadow` | `int` | 4 | always |
| `m_Zero` | `int` | 4 | `m_Ver >= 1` |
| `m_Rot`, `m_X`, `m_Y` | `int` ×3 | 12 | `m_Ver >= 2` (else `m_X=m_X1`, `m_Y=m_Y1` from the base record) |
| `m_RightAdj` | `int` | 4 | `m_Ver >= 4` |
| `m_Stack`, `m_StackPara` | `int` ×2 | 8 | `m_Ver >= 5` |
| `m_PerSpect` | `int` | 4 | `m_Ver >= 5` |
| `m_sperspect` | `SPERSPECT` (10×`double`) | 80 | `m_Ver >= 5 && m_PerSpect != 0` — see struct layout below |
| `m_Vert`, `m_VertH` | `int` ×2 | 8 | `m_Ver >= 6` (else `m_Vert=0`, `m_VertH=-6`) |
| `m_Text` | string | variable | always — the literal text, may contain unresolved macro tokens like `%m`/`%c`/`%r`/`%v` (confirmed in every real sample) |
| `m_Col1..m_Col4` | `TColor` ×4 | 16 | always |
| `m_ColS` | `TColor` | 4 | always |
| `m_ColB` | `TColor` | 4 | `m_Ver >= 3` |
| font name | string | variable | always — see "Font record" below |
| font charset | `int`, low byte used | 4 | always |
| font height/size | `int` | 4 | always — negative = point-height convention, positive = point-size (see below) |
| font style code | `int` | 4 | always — bitmask, see `FontStyle2Code`/`Code2FontStyle` (`CItems/TextArt/Comlib.cpp:709`/`:745`) if reproducing exactly |
| dummy | `int` | 4 | always — written/read, value unused on load (reserved, always 0 in practice) |
| brush bitmap | see "Bitmap payload" | variable | only if `m_Grade == 3` |

**Font record** (inline, not a separate helper — written directly in `CDrawText::SaveToStream`):
name (length-prefixed string), then `Charset` truncated to a `BYTE` on load (`pFont->Charset =
BYTE(d)`), then height-or-size disambiguated purely by sign (`d < 0` → `pFont->Height = d`; `d >= 0`
→ `pFont->Size = d`) — **on save the height is always forced negative** (`if (d >= 0) d = -d;`
before writing), so a real `.mtm` file's height field is negative in every case that matters; the
positive branch on load exists for symmetry/robustness, not because save ever emits it.

**`m_Ver < 4` fixup**: `LoadFromStream` forces `m_X2 = 0` after the base record is read
(`Draw.cpp:3161-3166`) — the base record's own `X2` is still a real, present field on the wire for
every element (nothing here changes byte layout), but legacy discards whatever value it read for an
old-format text record. An importer that trusts the stored `m_X1`..`m_Y2` rect for width/height
(the modern port's own chosen approach, see `LegacyMtmImportAdapter`) must apply this same fixup, or
it will use a stale `X2` legacy itself never would have.

**`m_Ver <= 6` legacy-color special case**: `if ((m_Ver <= 6) && (m_Shadow == 6) && !m_Stack)
{ m_LineColor = m_ColS; }` — an old-format compatibility fixup applied AFTER all fields are read, not
a field of its own. An importer reproducing legacy behavior exactly needs this same post-read
adjustment for old-version records; every real local sample has `m_Ver` between 4 and 7, so this
branch was not exercised by validation.

## `CDrawPic` (`CM_PIC`) — Draw.cpp:3729 (save) / :3746 (load)

Sets `m_Ver = 5` on save.

| Field | C++ type | Wire size | Gate |
|---|---|---|---|
| `m_Type` | `int` | 4 | `m_Ver >= 1` — **`0` means "no image loaded," and the bitmap payload below is entirely absent when `m_Type == 0`** (confirmed: both `t3.mtm`/`def3.mtm`'s `CM_PIC` records are empty placeholders, `type=0`) |
| `m_Shape` | `int` | 4 | `m_Ver >= 2` (else defaults to 0) |
| `m_Adjust` | `int` | 4 | `m_Ver >= 4` |
| `m_TransPoint` | `int` | 4 | `m_Ver >= 5` |
| bitmap | see "Bitmap payload" | variable | only if `m_Type != 0` |
| polygon | `CPolygon` (see below) | variable | only if `m_Shape == 5` |

## `CPolygon` (embedded inside a `CDrawPic` when `m_Shape == 5`) — Draw.cpp:5417/:5431

Its own magic-number lookahead, same pattern as the base record's box-style trick, but a DIFFERENT
magic value:

| Field | C++ type | Wire size | Notes |
|---|---|---|---|
| magic-or-count | `int` | 4 | if exactly `0x55aa2233`, three more fields follow (below); otherwise this value itself IS the point count and `XW`/`YW` default to `256`/`200` |
| `Cnt` | `int` | 4 | only if the magic matched |
| `XW` | `int` | 4 | only if the magic matched — the coordinate space the points below are stored in |
| `YW` | `int` | 4 | only if the magic matched |
| points | `Cnt` × `POINT` (2×`int`, x then y) | `Cnt`×8 | the polygon vertices, in `XW`×`YW` space |

**A genuine legacy bug, found and worth flagging for a product decision — not fixed here.** After
reading the points, `LoadFromStream` rescales them into a fixed 320×256 space:
`if (XW != 320) p->x = p->x * 320 / XW;` then `if (YW != 256) p->y = p->y * 256 / XW;` — the Y-scale
line divides by `XW`, not `YW`. Any polygon-shaped picture (`m_Shape == 5`) authored at a non-320×256
`XW`/`YW` will have its Y-coordinates scaled by the wrong factor when legacy itself re-loads that
file. This is off-air interpretation only (never wire-observable — nothing about SSTV transmission
depends on how a template editor scales a stored polygon), so `CLAUDE.md` §0a makes reproducing this
bug exactly a choice, not a requirement — but choosing to FIX it during import means Scanline Studio
will render a legacy-authored polygon-shaped picture element differently than legacy itself would.
That is a real product decision for whoever reviews the importer's plan, not something to default
either way silently.

## `CDrawOle` (`CM_OLE`) — Draw.cpp:3922 (save) / :3932 (load) — **NOT importable, NOT cleanly skippable**

| Field | C++ type | Wire size | Notes |
|---|---|---|---|
| `m_Trans` | `int` | 4 | always |
| `m_Stretch` | `int` | 4 | always |
| OLE payload | opaque | **unknown, no length prefix visible at this call site** | `pContainer->SaveToStream(sp)`/`LoadFromStream(sp)` — `pContainer` is a `TOleContainer*`, a VCL framework class. Its stream format is implemented inside the VCL library itself, not in any `yoniq-old/` source file, so it cannot be reverse-engineered from what this project has access to. |

**This is the one record type a byte-accurate parser cannot skip over even in principle from source
alone.** `CDrawLib` (next) has an explicit length prefix and can be skipped without understanding its
payload; `CDrawOle` has none visible here, and VCL's compound-document OLE stream format is not a
simple length-prefixed blob (it is typically an `IStorage`-based structured stream). **Design
consequence for the importer**: encountering a `CM_OLE` tag mid-file cannot be handled by "skip this
one element, keep parsing" — the read position is unrecoverable without decoding the OLE stream.
The two real choices are (a) reject the whole file when a `CM_OLE` record is encountered (safe,
simple, matches the removed-features stance that OLE embedding is out of scope for the modern
editor), or (b) do the extra work to parse/skip the actual OLE compound-stream format, which is a
much larger undertaking than anything else in this document. No local sample contains a `CM_OLE`
element, so this path is unvalidated even for the read-then-detect-and-reject case — only the fields
before the opaque payload (`m_Trans`, `m_Stretch`) are confirmed correct.

## `CDrawLib` (`CM_LIB`) — Draw.cpp:4569 (save) / :4584 (load) — skippable

| Field | C++ type | Wire size | Notes |
|---|---|---|---|
| `m_Name` | string | variable | plugin/custom-item name |
| `size` | `ULONG` | 4 | length of the opaque payload that follows |
| payload | `size` raw bytes | `size` | an opaque blob handed to/from a `CItems`-style plugin's own `fCreateStorage`/`fCreateObject`; format is plugin-specific and out of scope, but **cleanly skippable** because of the explicit length prefix |

Unlike `CDrawOle`, a parser CAN safely skip a `CDrawLib` record it doesn't want to import: read the
name string, read the 4-byte `size`, advance the stream by `size` bytes, move on to the next element.
No local sample contains a `CM_LIB` element, so this is validated by source reading only, not against
real bytes — but the length-prefix mechanism itself is unambiguous from the source, unlike `CDrawOle`.

## String encoding — `CDraw::SaveString`/`LoadString` (Draw.cpp:501/510)

Every string field in every class above (`m_Text`, `m_Sound`, font name, `m_Name`) goes through this
one shared pair of methods:

| Field | Type | Wire size | Notes |
|---|---|---|---|
| length | `int` | 4 | byte count of the string content that follows, `0` means an empty string with NOTHING after it (not even a 0-byte marker) |
| content | raw bytes | `length` | **no null terminator on the wire** — `LoadString` allocates `length+1` bytes, copies `length` bytes from the stream, and appends the null itself for the in-memory `AnsiString`. A byte-exact reader must NOT expect or consume a trailing zero. |

**Encoding is CP932/Windows-31J**, per this project's own standing encoding rule — confirmed directly
by every real sample containing only ASCII-range macro tokens and Latin font names, so the local
sample set does not itself exercise a multi-byte CP932 sequence, but `AnsiString::c_str()` (what
`SaveString` writes) always emits the string in the process's current locale codepage, which
`CLAUDE.md`'s encoding rule already establishes as CP932 for this codebase's Japanese-origin legacy
sources. **Do not decode these bytes as UTF-8.**

## Bitmap payload — `CDraw::SaveBitmap`/`LoadBitmap` (Draw.cpp:456/471)

Used by `CDrawTitle` (when `m_Type == 3`), `CDrawPic` (when `m_Type != 0`), and `CDrawText`'s brush
bitmap (when `m_Grade == 3`) — one shared format:

| Field | Type | Wire size | Notes |
|---|---|---|---|
| width | `int` | 4 | `0` if no bitmap is present |
| height | `int` | 4 | `0` if no bitmap is present |
| bitmap stream | standard Windows BMP | variable | **only present if BOTH width and height are nonzero** — `pBitmap->SaveToStream(sp)` is VCL's own `TBitmap::SaveToStream`, which emits a complete, standard, self-describing `.bmp` file (14-byte `BITMAPFILEHEADER` starting `"BM"` with its own `bfSize` total-length field, followed by a DIB header, optional palette, and pixel data) — a parser can read the `bfSize` field at offset 2 of this block to know exactly how many bytes to consume, and any standard BMP decoder can decode the pixels without any legacy-specific knowledge. |

**No local sample contains a populated bitmap** — every `CDrawPic`/`CDrawTitle` record in the 12
real files has `m_Type == 0` (or the title's `m_Type == 1`, plain color, never `3`), so the
width/height-then-BMP layout above is confirmed by reading `TBitmap::SaveToStream`'s well-documented,
standard VCL behavior and by the reference parser's `struct.unpack`/brace-matching logic being
internally consistent with the surrounding fields it DID validate — but it has not been exercised
against a real embedded image. This is the single largest remaining gap for a confident importer, and
`spec/15-template-designer.md`'s note that "no local `.mtm` sample carries an image element" should
be treated as still true until a real image-bearing template is obtained.

## `SPERSPECT` struct (Draw.h, immediately before the `CM_*` enum)

Ten `double` fields (`ax, ay, px, py, pz, rz, rx, ry, v, s`), written/read as one 80-byte block via
`sizeof(m_sperspect)` — no per-field gating, no version sensitivity of its own (it's an all-or-
nothing 80 bytes gated only by `CDrawText`'s own `m_PerSpect` flag). Perspective transform is already
a stated non-goal of the current TX Template Editor (`spec/15-template-designer.md`'s "Non-goals for
1.1"), so this payload only needs to be correctly SKIPPED (advance the stream by exactly 80 bytes
when `m_PerSpect != 0`), not interpreted, unless perspective import becomes a goal later.

## The `.mti` variant — confirmed identical path

Per `docs/removed-features.md`'s prior (unverified-at-the-time) note, now confirmed directly against
`Main.cpp`: `TMmsstv::LoadTemplateMenu` (`Main.cpp:10089`) calls the exact same
`LoadTemplate(pItem, filename, NULL)` (`Draw.cpp`, the `int __fastcall LoadTemplate(CDrawGroup*,
LPCSTR, TCanvas*)` free function) regardless of which extension the user picked — `.mti` vs `.mtm` is
**only** a file-picker default-extension/filter difference (`Main.cpp:10101-10108`: `isw` selects
between `OpenDialog->DefaultExt = "mti"` with a combined `.mti|.mtm` filter, or `"mtm"` with an
`.mtm`-only filter). The filter strings themselves live in `ComLib.cpp:1395`/`:1399`
(`GetTempIFilter()`/`GetTempMFilter()`). **There is no structural difference between `.mti` and `.mtm`
content, no single-element constraint enforced by the format itself, and no separate load function.**
An `.mti` file that happens to contain a multi-element group would load exactly like an `.mtm` one —
"single-item" is a naming/UI convention for how the app's own Save dialog is used, not something the
binary format enforces or a parser needs to special-case. **No local `.mti` sample exists** to confirm
this empirically the way the 12 `.mtm` samples were confirmed — this finding rests on the source
reading only.

## `PARALIST.BIN` — resolved: it IS an `.mtm`-format file, for a different feature

`TextIn.cpp:32` (`#define PARANAME "PARALIST.BIN"`) is unrelated to file extension speculation —
grepping its actual use settles this directly: `TTextInDlg::SaveCMB()` (`TextIn.cpp:558-560`) calls
`SaveTemplate(&DrawPara, bf)` and the constructor (`TextIn.cpp:106-110`) calls
`LoadTemplate(&DrawPara, bf, PBox->Canvas)` — the exact same free functions used for `.mtm`/`.mti`
files, on a `CDrawGroup` named `DrawPara`. `TTextInDlg::MemBtnClick` (`TextIn.cpp:566-580`) shows what
`DrawPara` actually holds: a list of `CDrawText` elements, one per saved "quick phrase" preset for the
text-entry dialog's combo box. **`PARALIST.BIN` is a real `.mtm`-format file** (same binary format,
same code path), just used to persist a different feature's data (a phrase-preset list rather than a
picture template) under `StockDir` instead of `TemplateDir`. The earlier "shares an identical
prologue, format relationship unconfirmed" note is now fully resolved, not a coincidence of similar
byte shapes — confirmed by tracing the actual read/write call sites, not by comparing bytes.

## Validated against real files

The claims above were turned into a working reference parser (Python, one function per class,
following the exact read order and gates transcribed from source) and run against every `.mtm` file
present in the local `yoniq-old/YONIQ-main/` clone: `def1.mtm`–`def5.mtm`, `t1.mtm`–`t5.mtm`,
`Current.mtm`, `Stock/List.mtm` (12 files, both the `TemplateDir` and `Stock/` copies of the same
five `def`/`t` pairs plus the two singles). Result: **12 of 12 parse every byte, landing exactly on
end-of-file with zero bytes left over and zero parse errors**, after one correction was needed along
the way (`m_LineStyle`/`TPenStyle` is 1 byte, not the 4 a naive port would assume — confirmed by the
fact that assuming 4 bytes desyncs every file after the first record's line-style field, and assuming
1 byte produces clean, plausible values — real macro tokens like `%m`/`%c`/`%r`/`%v`, real font names
`Arial`, `Arial Black`, `Arial Rounded MT Bold`, `Comic Sans MS`, and sane bounding boxes/colors —
across every sample). This is the strongest evidence available locally: not "the source suggests
this layout," but "this exact byte-for-byte layout reproduces 12 real files with nothing left over."

## Unresolved — needs more than what's available locally

Per this project's "no assumptions" rule, stated plainly rather than glossed over:

1. **No local sample exercises a populated bitmap payload** (`CDrawPic`/`CDrawTitle` image, or
   `CDrawText`'s brush bitmap). The width/height-then-BMP-stream layout is well-founded (it's VCL's
   own standard, documented `TBitmap::SaveToStream` behavior) but has not been validated against real
   bytes the way everything else in this document has.
2. **No local sample contains a `CM_LIB` element.** The length-prefix skip mechanism is unambiguous
   from source, but untested against real bytes.
3. **No local sample contains a `CM_OLE` element**, so even the "detect it and reject the file"
   fallback path is unvalidated against a real file — only the two fields before its opaque payload
   (`m_Trans`, `m_Stretch`) are confirmed.
4. **No local `.mti` sample exists.** The "identical format, filter-only difference" finding rests on
   reading `Main.cpp`'s dialog-setup code, not on parsing a real `.mti` file.
5. **`FontStyle2Code`/`Code2FontStyle`'s exact bit mapping was not transcribed** (found at
   `CItems/TextArt/Comlib.cpp:709`/`:745`, only their call sites were read here) — needed only if the
   importer wants to reproduce bold/italic/underline exactly rather than treat the style code as an
   opaque round-trippable integer.
6. **`CDrawTitle`'s `m_Type == 2` and other unobserved type values** — only `m_Type == 1` appears in
   the local samples; the source shows `1` (plain gradient bar), `3` (image-backed), and `4`
   (sound-triggered) are the only branches with dedicated payload logic, so an unrecognized `m_Type`
   value most likely just means "no extra payload to read," but this is inferred from the absence of
   a branch, not confirmed by an example.

**Getting real files that cover gaps 1-4** (a template with an actual embedded photo, a `CItems`
plugin-backed element, an OLE-embedded object, and any genuine `.mti` save) would close the largest
remaining uncertainty in this spec. These would need to come from the user's own legacy YONIQ/MMSSTV
install or archive, alongside the samples already present in `yoniq-old/`.

## Companion pictures — a `.mtm` never embeds its OWN "background" photo for a stock slot

Added 2026-09-12, after the importer itself shipped. Not part of the binary format proper, but
directly relevant to anyone importing a `.mtm` and expecting the photo to come with it: legacy pairs
a NUMBERED stock-slot template with a same-numbered picture as two INDEPENDENT files sharing one
index, never embedded in the `.mtm` stream — confirmed directly against `Main.cpp`:

- `t{N}.mtm` (1-based, no zero-padding) pairs with `TxStock{N}.bmp` or `TxStock{N}.jpg` (whichever
  `sys.m_UseJPEG` selects at save time — both can legitimately exist on disk for the same slot if the
  setting changed between saves, `Main.cpp:11732-11765`'s `ChangeStockFormat` only converts slots
  below `STOCKMAX`, not every slot `MoveStockDir` itself covers).
- `Current.mtm` pairs with `Current.bmp` the same way, for the app's own "restore last session" state
  rather than a named slot (`Main.cpp:2650-2651` save side, `LoadCurrentTemp` load side).
- The pairing is loaded via TWO SEPARATE, independently gated calls
  (`TMmsstv::LoadStockTemp`/`LoadBitmapS`, e.g. `PBoxTXDragDrop`'s own two checkboxes,
  `Main.cpp:9375-9401`) — not a hard bundle. A user can recall the template alone, the picture alone,
  or both.
- Legacy's own two picture-draw paths (`Main.cpp:9394-9400`) both preserve every source pixel — a
  full-canvas aspect-distorting stretch, or a plain 1:1 top-left draw with no scaling at all. Neither
  crops. A port choosing one fit mode to approximate both should prefer a non-cropping one.
- A missing companion picture is not an error in legacy either — `LoadBitmapSN` fabricates a blank
  white 320×256 bitmap when the file is absent (`Main.cpp:10014-10019`).

## Off-scope notes

`fileview.cpp`/`PicRect.cpp`/`PicSel.cpp` define unrelated `SaveBitmap`/`LoadBitmap` overloads
(file-path-based, not stream-based) that came up during the initial function-name search and were
correctly set aside — not part of this format. `CItems/TextArt/Comlib.cpp`'s `FontStyle2Code`/
`Code2FontStyle` were only located, not transcribed (see Unresolved item 5) — that stays a follow-up,
not chased further here to keep this pass scoped to the binary layout itself.
