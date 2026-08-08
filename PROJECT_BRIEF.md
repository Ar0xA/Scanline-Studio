# Project brief (resume point)

Scratch file for resuming after `/clear` — not a spec doc, delete or ignore once stale. Pruned
2026-08-08 (was 2978 lines/46 entries; everything but the active entry below was either already in
a detailed commit message or already migrated into `spec/14-roadmap.md`/`CLAUDE.md` — see git
history for this file if older context is ever needed).

## Resume here (2026-08-08, latest, ACTIVE) — Slant/sync correction readouts (RX GUI-blocking backend primitive).

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
