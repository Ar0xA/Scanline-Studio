# Project brief (resume point)

Scratch file for resuming after `/clear` — not a spec doc, delete or ignore once stale. Pruned
2026-08-08 (was 2978 lines/46 entries; everything but the active entry below was either already in
a detailed commit message or already migrated into `spec/14-roadmap.md`/`CLAUDE.md` — see git
history for this file if older context is ever needed).

## Resume here (2026-08-08, latest, ACTIVE) — Operator-profile setting + minimal TX-macro engine.
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
