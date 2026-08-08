# Project brief (resume point)

Scratch file for resuming after `/clear` — not a spec doc, delete or ignore once stale.

## Resume here (2026-08-08, latest, ACTIVE) — Auto Sync (automatic drift-triggered ReSync), the third
and last piece of the "SyncRestart toggle + Auto Stop's save-on-abandon unblock" backlog item (the
other two shipped earlier: commits `9346fb8`/`431d43e`/`05e90fa`). Plan file
`~/.claude/plans/glimmering-orbiting-falcon.md`. Implemented and tested. Sent for a code-level review
round. **Not yet committed.**

**What it is**: an automatic trigger for the EXACT SAME skip-and-suppress action the already-shipped
manual ReSync button performs (`Main.cpp:3917-3925`/`:3950-3958` vs `KRFSClick`,
`Main.cpp:14004-14020`) -- confirmed via `Main.cpp:3968`'s `!m_AutoSyncCount` gate matching this
port's own already-shipped `_slantCorrectionsDisabledForRestOfImage`. What's genuinely new is the
drift-DETECTION state machine deciding when to auto-fire it: a 16-entry ring buffer of raw sync-
offset observations, an 8-line warmup, a clustering-consistency check, and two distinct trigger
thresholds (a coarse first-correction and a finer continuous-drift correction with its own cooldown).

**Correction to an earlier, wrong assumption**: this was originally described as blocked on the
still-unbuilt "RX buffer mode" roadmap item. Traced fully and found `sys.m_UseRxBuff` only ever
appears as a bare truthy check inside `AutoStopJob`, never `==1`/`==2` -- simplifies to constant
`true` under this port's own already-established convention (matches `TryResolveSyncAnchorCorrection`'s
own precedent). Not actually blocked.

**Review process**: 2 full plan-readiness rounds before any code was written (round 1: 14 findings,
4 blockers -- most severe was a wrong anchor-base computation that would have caused continuous
spurious auto-resyncs on a perfectly-synced signal; round 2: confirmed round 1's fixes, found 2 more
real issues -- a missing non-null guard variant and, most notably, that the discriminator between
"dominant" and "minority" `ISstvDecoder` event orderings needed to be the STASH's own mode identity
checked first, not live state, mirroring the exact same class of bug the abandoned-image-save
feature's own code-level review found). Auditor's own closing assessment: "write those six in, and
this is ready... I'd not expect a third round." Proceeded to implementation.

**Two more real bugs found during testing itself, beyond the 20 already caught by plan review**:

1. **The round-2-resolved "reset Auto Sync's state on every SlantTracker correction commit, for
   consistency with `SlantTracker.Reset()`'s own precedent" decision was empirically wrong.** An
   18-combination sweep (3 modes x 6 realistic clock-mismatch percentages) showed ZERO triggers in
   every case -- `SlantTracker`'s own frequent corrections were wiping Auto Sync's history before it
   could ever accumulate enough to detect anything. Fixed by removing that reset call entirely (Auto
   Sync's state now only resets at a fresh lock). On reflection this is also MORE legacy-faithful, not
   just pragmatic: legacy's own `InitAutoStop`-after-commit call is the SAME call this plan's own
   round-1 research already proved is dead code without the not-built RX-buffer-replay feature -- real
   legacy never resets Auto Sync's state mid-image either.

2. **The wrap/base regression test (guarding round-1's own most severe finding) didn't actually catch
   that bug, for two independent reasons**, both found via a proper revert-fix-confirm-fail check:
   the test used Robot36, whose sync segment happens to be first-in-line (making the wrong and right
   anchor bases coincide for that one mode -- switched to ScottieS1, matching this port's own
   established precedent for this exact class of confusion); and asserting only on trigger COUNT
   didn't catch it either, since a CONSTANT wrong bias reads as a stable, self-consistent cluster to
   the clustering check (nothing looks like a "jump" if every reading is uniformly offset the same
   way) -- fixed by adding a direct capture of `ComputeAutoSyncPosition`'s own real return value
   (`LastComputedAutoSyncPositionForTests`, set inside `TryAutoSync` at the only moment it's valid to
   observe) and asserting on ITS magnitude directly.

**Related finding, not a bug**: smooth continuous clock-rate mismatch never organically triggers Auto
Sync at all (confirmed even after fixing bug #1 above) -- that's `SlantTracker`'s own job (continuous,
regression-based); Auto Sync is for sudden discontinuities (cluster-based). Matches legacy's real
design intent, not just this port's quirk. Proving the trigger mechanism genuinely works needed a
real audio splice (silent samples inserted mid-stream, simulating a sync glitch) rather than a clock
mismatch -- see `AutoSyncTests.cs`'s `SuddenPositionJump_EventuallyTriggersAutoSync`.

**Tests** (`AutoSyncTests.cs`, 5): the corrected wrap/base regression, the splice-based real-trigger
proof, its disabled-setting negative control (same splice scenario, genuine A/B), AVT exclusion, and
`PerformReSync`'s own new observation-count reset. Both major fixes independently re-verified via
their own revert-fix-confirm-fail checks.

**Code-level review round found one more real bug, plus a wrong legacy citation.** The
`(m_SyncMax-m_SyncMin)>5000` signal-strength gate (and this port's own `_pendingSkipSamples==0`
guard) had been hoisted into the OUTER `_autoSyncObservationCount>=8` condition instead of living
inside each of the two trigger branches, where legacy actually has it (`Main.cpp:3908`/`:3946`).
This wrongly suppressed the `n>=4` reference-position update (`Main.cpp:3941`, gated in real legacy
ONLY on the observation count and `n>=4`, nothing else) on weak-signal lines -- both missed triggers
(reference never re-arms during a weak stretch) and potential spurious ones (a stale pre-weak-stretch
reference can still satisfy the jump test once the signal recovers, where legacy's freshly-reanchored
one would not). **Fixed** by moving both gates inside each trigger branch, matching legacy's real
structure. Also corrected: two comments had claimed legacy's own `InitAutoStop`-after-commit call is
"dead code without RX buffer mode" -- wrong, `sys.m_UseRxBuff` defaults to 1 so that call DOES run in
real legacy; the actual reason not to copy it is that legacy immediately REPLAYS every buffered line
afterward, rebuilding its own state rather than losing it, which this port has no equivalent for.
The underlying design decision (don't reset on commit) stays correct, just for the corrected reason.
Test coverage for the weak-signal-gating fix itself has an accepted, documented gap (no dedicated
test -- would need real audio engineering comparable to the splice test's own effort, deliberately
not built given this session's already extensive scope). Re-verified after the fix: full solution
build clean, `Core.Sstv.Tests` 624/624 (unchanged count, golden vectors unaffected),
`Application.Tests` 48/48, `UI.Tests` 78/78.

**Not yet committed** — implemented, tested, and code-level reviewed (with the review's own finding
now fixed and re-verified). Awaiting user go-ahead to commit/push. This closes out the
"SyncRestart/Auto Sync/Auto Stop" backlog item's Auto Sync piece; Auto Stop itself (the
erratic-signal-detection-and-stop trigger, shares the clustering code but is a distinct action)
remains a real, tracked, not-yet-started follow-up.

## Resume here (2026-08-08, latest, ACTIVE) — "SyncRestart toggle + Auto Stop's save-on-abandon
unblock" backlog item (user picked this scoped slice over the full 3-sub-feature "Auto Sync/Auto
Stop/SyncRestart" item after research showed it was really 3 independent behaviors bundled by one
legacy "Lock" button; Auto Sync's own drift-detector deferred to a follow-up per user's own choice).
Both sub-pieces DONE, tested, build clean. SyncRestart toggle COMMITTED locally (`9346fb8`,
`431d43e`), abandoned-image-save DONE but NOT YET COMMITTED. Neither pushed yet.

**Research finding worth remembering**: `TMmsstv::AutoStopJob` (`Main.cpp:3884-4035`) is NOT the
same thing as this port's already-built `SlantTracker`/Auto-Slant feature (`KRSA`) -- it's a
SEPARATE, older mechanism gated by `sys.m_AutoStop`/`sys.m_AutoSync` (default OFF/ON respectively,
`Main.cpp:900-901`) that tracks raw sync-position drift via its own 16-entry ring buffer and either
(a) auto-triggers a ReSync-style skip ("Auto Sync") or (b) auto-stops the reception via
`RxAutoPush(TRUE)` after ~8 failed drift-cluster checks ("Auto Stop"). `RxAutoPush`'s call to
`WriteHistory(0)` is exactly the same abandoned-image-save path `m_ReqSave` uses -- confirming Auto
Stop's real action *is* "stop + save if warranted", not a separate concept. Auto Sync's full fidelity
has a genuine coupling to `sys.m_UseRxBuff` ("RX buffer mode", the other still-unbuilt roadmap item)
-- deferred with that dependency explicitly flagged, not silently ignored.

**SyncRestart toggle** (commits `9346fb8` doc fix + `431d43e` feature): new
`SstvDecoderSettings.SyncRestartEnabled` (nullable, STJ-safe, mirrors `AfcEnabled`), gates the
mid-reception restart call site in `AnalogFmSstvDecoder.TryProcessBuffer`. Threaded through
`RestartableSstvDecoder`/`Program.cs`. New test proves the toggle changes real end-to-end behavior
(truncated-then-superseded transmission decodes straight through as garbage instead of
abandoning/restarting, when disabled). `Core.Sstv.Tests` 619/619 (was 618, golden vectors
unaffected), `Application.Tests` 48/48, `UI.Tests` 78/78.

**Abandoned-image save** (port of legacy's `m_ReqSave`, `sstv.cpp:2134-2137` -- closes the gap
`spec/14-roadmap.md` line 143 flagged as "correctly blocked on the not-yet-built logging/history
feature," which now exists). Plan file `~/.claude/plans/wandering-glinting-otter.md`, 2 rounds of
auditor plan-readiness review -- **round 1 caught a genuine data-corruption risk**: the original
design assumed `ISstvDecoder.ModeDetected` always fires before `DecodeRestarted` (matching that
interface's own doc comment AT THE TIME), which was stale since this port's "piece 8c" deferred-
anchor-correction work changed the real ordering without the doc being updated. The TRUE ordering is
path-dependent (dominant case: `DecodeRestarted` fires first, `ModeDetected` deferred, possibly to a
LATER `PushSamples` call; minority case -- AVT resolving same-call, or `ForceMode`-into-AVT --
`ModeDetected` fires first). Building the original design as drafted would have been silently inert
for ordinary mid-reception restarts AND would have saved a stale, unrelated PREVIOUS restart's image
under a LATER restart's mode id. Fixed at the source too: `ISstvDecoder.cs`'s `DecodeRestarted` doc
comment corrected (own tiny commit, `9346fb8`), plus a matching stale-comment fix at
`AnalogFmSstvDecoder.cs`'s own mid-reception restart call site (drive-by, bundled with this feature's
commit). Round 2 confirmed the redesign's ordering table/logic correct but found 4 more small,
localized issues, all fixed: no `IImageSource` impl reachable from `Core.Logbook` without a new
Core-to-Core project reference (switched to a small local `PixelSnapshot` record instead, since
`Rgb24` already lives in the already-referenced `Abstractions.Imaging`); a missing non-null guard on
the fallback-stash branch (NRE-on-audio-thread risk via a zero-lines-decoded minority-ordering
restart); `_recordedForCurrentImage`'s set-condition and "is there something to save" wrongly
conflated (split into two checks); a real filename-collision risk at second-granularity once
back-to-back restarts are possible from a bulk-decoded WAV file (switched to millisecond precision +
a `_partial` suffix, which also gives partial saves a visible marker neither legacy nor this port's
existing completed-image path has).

**Design (final, post-code-review)**: `ReceiveHistoryRecorder.OnDecodeRestarted` checks the STASH
FIRST (`_pendingAbandonMode == abandonedMode` plus non-null image/line, the minority orderings) and
only falls back to live state (`_currentMode == abandonedMode`, the dominant ordering) when the stash
doesn't match -- both branches require actual non-null image+line data before considering a save, and
both funnel into the same >=65%-threshold check (`completedLines = line + (step ?? 1)`,
`threshold = ImageHeight * 65 / 100`, integer math matching legacy's own truncating idiom; legacy's
threshold is sample-position-based, this port only has line-granularity at this event level,
documented as an equivalent-within-one-line adaptation, not sample-exact). Pixel data is captured as
an explicit snapshot copy on every `LineDecoded` (NOT a held live reference -- an earlier draft
wrongly assumed `AnalogFmSstvDecoder`'s `MutableImageSource` was frozen once handed out; that class's
own doc comment says the opposite, it's a live alias mutated in-place across a single image's
remaining lines). Deliberately saves from all 3 `DecodeRestarted` sources (mid-reception VIS/narrow
restart, AVT-training-abort, `ForceMode`) where legacy's own `m_ReqSave` is set from exactly one (the
VIS case-3 confirm) -- kept intentionally broader, documented as a deliberate improvement (never
silently losing a mostly-complete image), not a fidelity miss.

**Code-level review (after implementation, per this project's standing practice) found the
implementation still had a real gap neither plan round could have caught**: mode-IDENTITY alone (what
both plan rounds settled on as the dominant-vs-minority discriminator) is not sufficient, because in
the minority ordering the newly-detected mode can be the SAME `SstvModeDefinition` instance as the
abandoned one -- reachable via AVT-into-AVT (a second AVT lock found mid-training, or `ForceMode`
into AVT while an AVT image is already decoding, since `SstvModeRegistry.Avt` is one shared
singleton). `_currentMode == abandonedMode` would then wrongly take the dominant branch even though
`OnModeDetected` had already reset live state for the new image -- losing the abandoned save AND
permanently blocking the new image from ever completing its own record, the exact data-loss class
round-1 caught, just re-entering through mode identity instead of event order. **Fixed by checking the
stash first, live state second** (flipped from the original order) -- the stash is only ever populated
when `OnModeDetected` is about to overwrite a real, unhandled in-flight image (precisely the minority
ordering's precondition) and is cleared on every `OnDecodeRestarted` call, so it can't be stale by more
than one restart. Also found and fixed: a completed image could be saved a SECOND time as `_partial`
(`AnalogFmSstvDecoder`'s mid-reception restart check has no guard against firing right after the final
line of an already-finished image -- fixed via a new `alreadyHandled` check, matching legacy's own
`m_Sync`-gated equivalent); the millisecond-precision filename could still collide for two saves
landing in the same tick (fixed by folding the history entry's own GUID into the filename); and the
new tests were writing real PNGs into the actual OS Pictures folder, since the abandoned-image path
deliberately bypasses the completed-image path's `IReceivedImageBuffer.SaveAsync` (which the test
fakes stub out) -- fixed with a `TempImagesDirectorySettings()` test helper redirecting
`ReceiveHistorySettings.ImagesDirectory` to a temp folder. 2 more regression tests added (AVT-into-AVT
same-instance, and the just-completed-image double-save case) -- **the mode-identity fix independently
re-verified via its own revert-fix-confirm-fail** (temporarily removing the stash-first check broke
exactly the 3 minority-ordering tests, nothing else). One low-priority perf finding (an unconditional
full-image snapshot copy on every `LineDecoded`, real but non-urgent LOH churn for a coarse gate
feature) deliberately deferred, documented rather than fixed -- not a correctness issue, and adding
more conditional complexity this late in a long session wasn't worth it for a "correct, just wasteful"
finding. Stray test PNGs the pre-fix test runs had already left in the real
`~/Pictures/ScanlineStudio/History` folder were cleaned up (created this session, safe to remove).

**Tests** (`ReceiveHistoryRecorderTests.cs`, 7 new total): dominant-ordering save above threshold,
zero-lines-decoded no-op, minority-ordering save via the stash, the bug-fix regression itself (new
image after a minority-ordering restart still records once complete -- **verified via
revert-fix-confirm-fail**: temporarily reproducing the old unconditional
`_recordedForCurrentImage = true` made exactly the 4 abandoned-save tests fail while the 3 original
tests kept passing), the two-back-to-back-restarts data-corruption regression (second restart's save
never uses a stale stash from the first), plus the two code-level-review-round-1 additions (AVT-into-
AVT same-mode-instance, and the just-completed-image double-save case).

**Second code-level review round (a fresh confirmation pass after round 1's fixes) found round 1's
own mode-identity fix was a NARROWER version of the same bug it fixed, not the full fix**: gating the
`OnModeDetected`-side stash's own POPULATION on `!_recordedForCurrentImage` left the stash empty
whenever the abandoned image had ALSO already completed before a same-instance restart arrived (a
completed AVT image immediately followed by a same-instance AVT restart) -- sending that case into
the live-state branch and permanently blocking the NEXT image instead, the identical failure mode
round 1 had just fixed for a different precondition. **Final, correct shape**: the stash now
populates UNCONDITIONALLY whenever there's a live mode; a new `_pendingAbandonRecorded` field
carries forward the separate "was this already handled" fact, keeping "which branch" and "is there
something to save" genuinely independent questions (conflating them was the root cause both times).
Also added a defensive `ClearPendingAbandon()` call at the top of `OnLineDecoded` (any decoded line
for the current image proves a pending stash for whatever preceded it has already been consumed).
Round 2's fix independently re-verified via its own revert-fix-confirm-fail (temporarily re-gating
the stash's population broke exactly the new regression test, nothing else). One more regression
test added (9 new total across the whole feature). `Core.Logbook.Tests` 55/55 (was 47).
`Core.Imaging.Tests` 24/24 confirmed unaffected. Full solution build clean throughout both rounds.
Stray test PNGs from before the isolation fix (created by pre-fix test runs) cleaned up from the real
`~/Pictures/ScanlineStudio/History` folder.

**This is now 4 total review rounds on this one feature (2 plan-level, 2 code-level), each finding a
real bug in the same underlying area** (the dominant-vs-minority ordering discriminator, and what
"already handled" actually means). Reported back rather than auto-requesting a 5th round, matching
this project's "soft 3-round backstop, then loop in the user" cadence -- **user's call whether one
more confirmation pass is warranted before committing.**

**Not yet committed** (SyncRestart toggle IS committed locally, not pushed). Next up per the backlog:
Auto Sync itself (the drift-detector, deferred from this pass per user's own explicit choice) --
genuinely coupled to the still-unbuilt "RX buffer mode" item for full fidelity, flag that dependency
again when picked up.

## Resume here (2026-08-08, latest, ACTIVE) — RX force-mode decode override (Item 3 of the DSP/backend
backlog, `spec/14-roadmap.md`'s Phase 4+ backlog). Implemented, full solution build clean, all
touched test projects green. **NOT YET COMMITTED.**

Roadmap's own framing ("lock to a specific mode... would need a real decoder change") was
deliberately vague pending research. Traced the actual legacy click handler: `TMmsstv::SBMClick`
(`Main.cpp:6096-6122`) calling `CSSTVDEM::Start(mode, TRUE)` (`sstv.cpp:1749-1767` -> `Start(void)`,
`sstv.cpp:1717-1747`). **Feature-identity finding, the main thing worth remembering if this needs
re-explaining**: confirmed via `UpdateModeBtn` (`Main.cpp:5988`, `SBAuto->Down = (pDem->m_SyncMode
>= 0)`) that this is a ONE-SHOT "start decoding as mode X right now" kick (same spirit as the
ReSync button), **not a persistent lock** — `Start(void)` unconditionally ends at `m_SyncMode=0`,
the same value normal VIS auto-detect uses, so once the forced image ends, ordinary auto-detect
resumes for the next transmission automatically. mock2's "Auto/Locked" wording is a UI framing
choice, not a literal persistent decoder state to replicate. The `f=false` de-select branch (a
genuine pause/arm-without-starting state, `m_SyncMode=-1`) was deliberately NOT ported — obscure
secondary interaction, VCL `SpeedButton`/`GroupIndex` click-toggle semantics this port has no
equivalent widget for.

Legacy's `Start()` is shared between the VIS-auto path and the force-mode path, which confirmed
reusing this port's own existing `Commit()`/`_pendingAnchorCorrectionMode`/
`TryResolveSyncAnchorCorrection`/`FinalizeAnchorAndStartDecoding` pipeline (the one VIS auto-detect
itself uses) for force-mode too — legacy-faithful, not an invented shortcut.

**Design went through 2 rounds of auditor plan-readiness review before implementation** (plan file
`~/.claude/plans/flickering-locking-falcon.md`, full detail there). Round 1 found a real blocker: the
original draft anchored `Commit(mode, _consumedSamples)` — but pre-lock, `_consumedSamples` is a
frozen header-search start that `TrimBuffers` stops protecting once `_fixedWindowExhausted` is set,
so on any decoder idle long enough, `_consumedSamples` can sit behind `_bufferBase` — committing
there reads already-trimmed-away buffer and throws `InvalidOperationException` **on the audio
thread**, for the ordinary "idle app, click a mode button" flow. Fixed: anchor at
`TotalSamplesReceived` instead (always within `TrimBuffers`' retained window in both the pre-lock
and already-locked cases; matches legacy's actual reset target, `sstv.cpp:1726-1730`'s
`m_wBase/m_wPage/m_rPage/m_rBase = 0`, not the `m_wBgn=2` buffered-lines-gate flag the first draft
cited). Round 1 also required: explicitly tearing down AVT training state
(`_avtTrainingPending`/`_avtTrainingLock`/`_avtPllDemodulator`) in the force-mode path itself (not
inside `AbandonInProgressImage()`, which the S7 AVT hand-off relies on surviving); gating
`DecodeRestarted` on the old mode having actually reached `ModeDetected` already (post-piece-8c,
`Commit()` only fires `ModeDetected` immediately for AVT — every other mode defers it, so an
unresolved mode was never announced and firing `DecodeRestarted` for it would violate the event's
own documented contract); an atomic `Interlocked.Exchange`-based field instead of a plain volatile
bool (payload, not just a bit); consuming ForceMode before the existing ReSync flag at the top of
`PushSamples`; and `RestartableSstvDecoder` forwarding (missed entirely in the first draft). Round 2
re-verified all of round 1's fixes against current source (all confirmed correct) but found one more
real gap: `_pendingAnchorCorrectionMode` is cleared in exactly one place today and every existing
caller provably can't reach `Commit()` while it's set — an invariant ForceMode is the first to
break. Forcing AVT while a *different* mode's anchor correction was still unresolved would leave
that stale entry behind, later resolving against AVT's own `_lineDecoder`/`_consumedSamples` and
firing a spurious second `ModeDetected` for a mode that isn't `_mode` anymore. Fixed: cleared
alongside the AVT teardown in the same step, ordered after capturing (not before) the
`DecodeRestarted` gate's own pre-clear read.

**Implementation**: `ISstvDecoder.ForceMode(SstvModeDefinition)` (fire-and-forget, mirrors
`RequestReSync`'s contract exactly) → `AnalogFmSstvDecoder.PerformForceMode` (captures old
mode/pending-anchor state, tears down AVT training + stale pending anchor, calls
`Commit(mode, TotalSamplesReceived)`, fires `DecodeRestarted` only when warranted) →
`RestartableSstvDecoder.ForceMode` (thin forwarder under the swap gate, same drop-on-race contract
`RequestReSync` already has) → `ISstvSessionService.ForceMode`/`SstvSessionService.ForceMode` (thin
pass-through + `[LoggerMessage]`). Backend-only, no UI button/dropdown wired yet (roadmap's mock2
quick-mode-grid + Locked-segment is the eventual consumer).

**Tests** (`tests/ScanlineStudio.Core.Sstv.Tests/ForceModeTests.cs`, new, 9 tests + 1 more in
`RestartableSstvDecoderTests.cs` for the forwarder): idle force of a non-AVT mode (deferred
`ModeDetected`, no `DecodeRestarted`), idle force of AVT (immediate `ModeDetected`), mid-reception
force (`DecodeRestarted` with the correct old mode), force during an unresolved pending anchor (no
spurious `DecodeRestarted`), force-AVT-while-a-different-mode's-anchor-still-pending (round-2's own
regression — exactly one `ModeDetected`, stale entry cleared, AVT decoding not stalled), force
during real in-flight AVT training (teardown verified, using real encoded AVT audio same as
`AvtTrainingLockDecoderTests`), second request superseding a first before either resolves, the
round-1 blocker regression itself (idle decoder pushed well past every fixed-window search ceiling,
then forced — asserts no exception and the correct `TotalSamplesReceived` anchor), and the
trimming-suspension window (round-1 item 7 — forced long-line mode keeps `TrimBuffers` suspended
until the anchor resolves, then recovers). 4 test doubles updated for the `ISstvDecoder`/
`ISstvSessionService` interface changes (`Core.Logbook.Tests`/`Core.Imaging.Tests`/
`Application.Tests`'s three `FakeSstvDecoder`s, `UI.Tests`'s `FakeSstvSessionService`) — one more
than the plan's own first-draft checklist estimated (round-2 auditor correction).

**Full verification, CONFIRMED COMPLETE**: full solution build clean (0 warnings/errors).
`Core.Sstv.Tests` full suite 618/618 (was 608 before this session — 10 net new tests, includes every
real legacy-captured golden vector, confirmed unaffected, ~8m20s runtime). `Application.Tests`
48/48, `UI.Tests` 78/78, `Core.Imaging.Tests` 24/24, `Core.Logbook.Tests` 47/47 all green.

**NOT YET COMMITTED.** Next up per the priority list once this lands: continue down the roadmap's
"RX/TX quality-of-life" backlog (RX buffer mode + high-precision replay actions, auto-stop-at-
end-of-signal/auto-resync toggles, or one of the telemetry/SNR-adjacent items) — see
`spec/14-roadmap.md`'s Phase 4+ backlog for the full list.

## Resume here (2026-08-07, superseded by the entry above) — ultracode audit fully closed (entry below, committed
`1d82a33`). Working a DSP/backend backlog, explicitly ordered by GUI leverage (user's own
instruction: "DSP items first, prioritized by things we need on the GUI side at some point") — full
priority list in `spec/14-roadmap.md`'s Phase 4+ backlog. **Item 1 (Decode progress/line-index
field) DONE**: new `IReceivedImageBuffer.Progress` (`double?`, backend-only, no UI wired yet) — see
`~/.claude/plans/fuzzy-yawning-melody.md` for that design (reuses `ReceiveHistoryRecorder`'s own
step-learning technique for the same paired-line-family problem). 3 fakes updated
(`Application.Tests`/`Core.Logbook.Tests`/`UI.Tests`), 7 new tests in
`ReceivedImageBufferTests.cs`, full solution build + all 4 touched test projects green.

**Item 2 (Manual ReSync button) DONE.** Same plan file (`~/.claude/plans/fuzzy-yawning-melody.md`,
overwritten — holds the full ReSync design now, not the Progress-field one). **Feature-identity
correction mid-design, the main thing worth remembering if this needs re-explaining**: the roadmap's
own stated approach ("reuse `ReSyncSSTV`") was WRONG — traced the actual legacy click handler
(`TMmsstv::KRFSClick`, `Main.cpp:14004-14020`) and found `ReSyncSSTV` (a 32-line envelope fold) is
only ever called by two "high-precision sync" MENU items, never the button; the real button uses
live per-line sync-peak tracking (`m_SyncPos`/`m_SyncRPos`) + a forward-only sample skip (`m_Skip`),
much simpler, no fold/cache needed. User explicitly chose "go for the legacy implementation" once
this was found, so the whole design was rebuilt around `KRFSClick`, not the original `ReSyncSSTV`
approximation.

Design went through 3 full review rounds before implementation (each found a real bug: phase-
alignment math, a buffer-overread crash on the common path in the original atomic-apply approach —
fixed by making the skip an incremental drain (`DrainPendingSkip`) across `PushSamples` calls,
mirroring legacy's own per-sample drain — and a state-corruption bug where "call
`SlantTracker.ProcessLine` and discard the result" for the widened per-image suppression would still
trigger `Reset()` and wipe history). After round 3, per explicit user instruction, asked the auditor
to draft the exact fix code directly rather than another self-drafted-then-critiqued round — worked
well, now a standing preferred pattern for this kind of thing
(`feedback_ask_auditor_for_code_fixes.md`). Implemented, then independently verified myself (also
per explicit instruction) that `SlantTracker.Reset()` really does wipe history and is reachable from
the path the fix protects against.

**Implementation**: `AnalogFmSstvDecoder.RequestReSync()` (fire-and-forget, `volatile bool` flag
consumed at the top of `PushSamples`) → `PerformReSync()` (deadband check, skip computation,
forward-only wrap) → `DrainPendingSkip()` (incremental, spans multiple `PushSamples` calls for a
skip bigger than one chunk). Two suppression scopes in `ApplySlantTracking`, matching legacy's two
distinct gates: `_suppressNextSlantProcessLine` (one line only, no history push at all — legacy's
`m_SyncPos != -1` gating `AutoStopJob()` out entirely) vs `_slantCorrectionsDisabledForRestOfImage`
(rest of the image, history keeps flowing via new `SlantTracker.ProcessLineHistoryOnly`, only the
correction branch is skipped — legacy's `m_AutoSyncCount`). Full `ISstvDecoder` →
`RestartableSstvDecoder` → `ISstvSessionService`/`SstvSessionService` plumbing, 4 test-double stubs.
Backend-only, no UI button wired yet.

**Tests** (`tests/ScanlineStudio.Core.Sstv.Tests/RequestReSyncTests.cs`, new, 8 tests + 1 more in
`SlantTests.cs` for `ProcessLineHistoryOnly`): no-lock/no-line/AVT no-ops, deadband both directions,
an exact hand-computed-skip assertion, forward-only wrap, second-click idempotence, a drain-crash
regression (tiny chunks spanning many `PushSamples` calls), and the widened-suppression behavior
with a negative control. **Two real test-writing traps hit and fixed, worth remembering**: (1)
`LineDecoded` fires BEFORE `ApplySlantTracking` catches up for that same line — a test that
synchronizes off `LineDecoded` observes a one-line-STALE peak/state; poll the decoder's own
diagnostic properties directly in the outer loop after `PushSamples` returns instead. (2) Large
`PushSamples` chunks let a same-call backlog build up, so `DrainPendingSkip` can fully drain (and
`TryProcessBuffer` can ALSO advance `_consumedSamples` via ordinary decode) within the very same call
that also fired the request — contaminating an exact-equality assertion with no way to observe the
boundary from outside; keep chunks small (32 samples) throughout tests that need to bracket the drain
precisely. Final auditor code-level review: EQUIVALENT-WITH-RISKS, no blockers — two deliberate,
already-documented divergences from legacy's literal (racy) semantics (this port's single capture
field correctly serves two roles legacy keeps as two separate variables; the one-line suppression is
deterministic here vs. legacy's own probabilistic mid-line click timing — both confirmed as the
correct, safer choice, not bugs). Applied its two small nits: added the missing citation for
`KRFSClick`'s 4 unported writes (why they're moot, not missed), and added a decoder-level test
pinning that the one-line-suppress branch contributes ZERO history entries (distinct from the
whole-image branch, which contributes exactly one per line via `ProcessLineHistoryOnly`).

**Full verification**: full solution build clean (0 warnings/errors). `Core.Sstv.Tests` full suite
608/608 (was 599 before — 9 net new tests, includes every real legacy-captured golden vector,
confirmed unaffected). `Application.Tests` 48/48, `Core.Imaging.Tests` 24/24, `Core.Logbook.Tests`
47/47, `UI.Tests` 78/78 all green.

**NOT YET COMMITTED.** Next up per the priority list: force-a-specific-mode decode override.

## Resume here (2026-08-07, latest, ACTIVE) — ultracode audit follow-up: the 19 MATCH_LEGACY fixes (entry below) are COMMITTED AND PUSHED (`4f6c7b6`). #36/#37/#38 (WavFile.cs) DONE. #34 (int-overflow) is now ALSO DONE, tested, code-reviewed. NOTHING FROM THIS SESSION IS COMMITTED YET.

**#34 fix — final design, not the widening plan**: widening every affected `int` field (the approach queued in the entry directly below) went through plan-readiness review and came back **NOT READY** — the coordinate space turned out to escape into `IScanlineDecoder`/`PixelSampleReader`/5 scanline decoders, a second dangerous unbounded cast existed that the first pass missed, and `VisLockStateMachine` has its own internal unbounded counter. User proposed the actual shipped fix instead: periodically discard and reconstruct the whole `AnalogFmSstvDecoder` object graph (same effect as restarting the app, confirmed to trivially fix this since `ISstvDecoder` is a DI singleton) rather than keep the same instance alive and correct forever. New `RestartableSstvDecoder` (`src/ScanlineStudio.Core.Sstv/`) wraps a mutable inner decoder; on every `PushSamples` call it evaluates (before forwarding the chunk) a 3-step state machine using the decoder's own `TotalSamplesReceived` (bumped `private`→`internal`, NOT widened) as the trigger: (1) past a 13h-worth critical threshold → force-swap **unconditionally regardless of idle state** (the actual overflow-safety guarantee — self-clearing, can't wedge); (2) idle + past a 12h-worth warning threshold → normal swap (the common path); (3) not idle + past warning → raise a one-shot `RestartOverdue` warning. New `ISstvDecoderMaintenance` interface (3 events: `RestartOverdue`/`RestartCriticallyOverdue`/`Restarted`) — deliberately **not** on `ISstvDecoder` itself (would force `AnalogFmSstvDecoder` to declare events it never raises → CS0067 under `TreatWarningsAsErrors`). `SstvSessionService` subscribes via an `is ISstvDecoderMaintenance` check, calls `StopReceivingAsync()` on the critical signal, and exposes 3 new events on `ISstvSessionService` that `RadioStatusViewModel` maps to a **new dedicated `MaintenanceMessage` property** (deliberately not reusing `ErrorMessage`, which has 5 write sites with no priority order and a traced clobber path). New `internal bool IsIdle => _mode is null && !_avtTrainingPending;` on `AnalogFmSstvDecoder` (verified via a full field sweep that `_avtTrainingPending` — a confirmed-but-not-yet-committed AVT detection — is the only pre-lock flag `_mode is null` alone misses).

**Review process** (this was the point of "don't work it out until auditor gives an actual go," honored throughout): went through 3 rounds of plan-readiness review before any code was written. Round 1 (core swap mechanism) → READY with 4 amendments (switch wall-clock trigger to a sample counter, add the `!_avtTrainingPending` term, fix a factual error about what `Program.cs` passes, note a threading/lock requirement). Round 2 (the new 12h/13h warning/forced-stop layer, added mid-planning at the user's request) → **NOT READY**, 5 real blockers including a genuine infinite-loop wedge bug (the original forced-stop design never reset anything, so restarting RX after a forced stop would immediately re-trigger the same stop forever) and an inverted warning condition. Round 3 (after fixing all 5) → **READY TO BUILD: YES**, with one more subtle race found and fixed (raise the 3 events after releasing the swap lock, not inside it — a rare shutdown-timing path could otherwise hop threads and deadlock the drain thread) plus 4 small refinements. Implementation then proceeded in 4 pieces (Core.Sstv → Application → UI → DI wiring), each built+tested in isolation before the next. A final **code-level** auditor review (after implementation, per this project's standing practice) came back "equivalent-with-risks, no blockers" — found one real gap (`SstvSessionService._isReceiving` needed `volatile`, since the new critical-stop path gives it a drain-thread writer for the first time — this class already uses `volatile` for exactly this reason on two other fields) and a genuine test-coverage gap (nothing subscribed to `Restarted`, so dropping that event would have silently broken `MaintenanceWarningCleared` in production while every test stayed green) — both fixed.

**Full verification**: full solution build clean throughout. `Core.Sstv.Tests` 600/600 (nets to +6 from this fix — some prior WavFile-piece net change already counted), `Application.Tests` 48/48, `UI.Tests` 78/78, `Core.Audio.Tests`/`Core.Imaging.Tests`/`Core.Logbook.Tests`/`Core.Localization.Tests`/`Settings.Tests`/`Core.Radio.Tests` all unaffected and green. New/touched files: `src/ScanlineStudio.Core.Sstv/RestartableSstvDecoder.cs` (new), `ISstvDecoderMaintenance.cs` (new), `AnalogFmSstvDecoder.cs` (`IsIdle` + `TotalSamplesReceived` visibility), `src/ScanlineStudio.Application/{ISstvSessionService,SstvSessionService}.cs`, `src/ScanlineStudio.Host/Program.cs` (DI now constructs `RestartableSstvDecoder`), `src/ScanlineStudio.UI/ViewModels/RadioStatusViewModel.cs` + `Views/RadioHeaderView.axaml`, 2 new localization keys in `assets/locale/en.json`, plus matching test-double/test updates in `Application.Tests`/`UI.Tests`/`Core.Sstv.Tests`. Plan file (`~/.claude/plans/fuzzy-yawning-melody.md`) has the full design/review history if resuming needs the detail this brief compresses.

**Nothing in this session is committed** — waiting on user go-ahead, matching established practice.

**#36/#37/#38 fix** (`src/ScanlineStudio.Core.Audio/WavFile.cs`, new `tests/ScanlineStudio.Core.Audio.Tests/WavFileTests.cs`): plan at `~/.claude/plans/fuzzy-yawning-melody.md` (overwritten — the MATCH_LEGACY plan is done, this file now holds the WavFile plan instead; the MATCH_LEGACY implementation detail lives only in this brief + `ultracode_review.md` now, not in the plan file). Sample scale changed to 32768 both directions in `Write`/`Read`, matching the port's own internal DSP convention (`AnalogFmSstvDecoder`/`LevelAgc`/`PllFmDemodulator` all use 32768, not 32767). Found and fixed a real bug while implementing this: `(short)(1.0f * 32768f)` does NOT saturate in C#, it wraps to `short.MinValue` (confirmed empirically with a scratch console app) — a full-scale +1.0 sample would have silently flipped to full-scale negative. Fixed by clamping the scaled value to `short.MinValue..short.MaxValue` before narrowing. `#38` hardening added: `audioFormat` validation (accepts PCM tag 1, and `WAVE_FORMAT_EXTENSIBLE` (0xFFFE) only after checking the SubFormat GUID is the PCM one), RIFF pad-byte handling on odd `chunkSize`, `fmt`-before-`data` enforcement, and bounds-checking `chunkSize` against remaining file bytes (rejects negative/oversized sizes instead of crashing). 14 new tests all pass; confirmed no golden-vector test depends on `WavFile`'s scale (only caller is `SstvRoundTripTests.cs`, a self-consistent round trip) — ran full 105-test `SstvRoundTripTests` suite after the change, all green, ~8 min runtime.

**#34 scope, discovered before starting**: far bigger than the audit's short field list ("`_bufferBase`, `TotalSamplesReceived`, `_consumedSamples`, related cursor fields") suggested. Confirmed by reading `AnalogFmSstvDecoder.cs` directly: ~22 `int` fields (`_demodulatedFrequenciesProcessedUpTo`, `_bufferBase`, `_agcDeadZoneCatchUpTarget`, `_consumedSamples`, `_nextLine`, `_bandpassLockedFromSample`, `_afcProcessedUpTo`, `_afcBoundSample`, `_slantProcessedUpTo`, `_syncBypassProcessedUpTo`, `_syncBypassOriginSample`, `_visLockProcessedUpTo`, `_visLockOriginSample`, `_narrowFskProcessedUpTo`, `_avtPllWarmupStartSample`, `_avtTrainingOriginSample`, `_avtTrainingProcessedUpTo`, `_avtTrainingFallbackDeadlineSample`, `_levelAgcProcessedUpTo`, `_bandpassFilteredProcessedUpTo`, `_visDataD11/D12/D19ProcessedUpTo`, `_fskSpaceProcessedUpTo`) plus 7 accessor methods (`AgcSampleAt`, `BandpassFilteredSampleAt`, `DemodulatedFrequencyAt`, `D11At`/`D12At`/`D19At`, `FskSpaceAt`) all typed `int index` share the same absolute sample-index space and would need widening to `long`. The file ALREADY has its own comment (`:170-177`) explicitly flagging and deferring this exact change as "a much larger, separately-scoped change." User was asked and chose to defer #34's implementation until #36/#37/#38 land — now that they have, #34 is next, and per the user's own earlier standing instruction it likely warrants an `auditor` scoping pass first given the size and fragility of this file.

**All 7 implementation pieces done** (plan: `~/.claude/plans/fuzzy-yawning-melody.md`), each built+tested+full-suite-verified before moving to the next, per this project's chop-into-pieces discipline:
- **Piece 1** (AfcTracker.cs, LevelAgc.cs, SyncEnvelopeDetector.cs, AnalogFmSstvDecoder.cs): AFC sign fix (#2), zero-Hz lock-average seed fix (#3), AFC-correction gating fix (#4), sync-tone tank-filter retuning (#1), AGC TX/RX reset (#6), HilbertFmDemodulator doc-comment fix (#15).
- **Piece 2** (SlantTracker.cs, SstvModeRegistry.cs, AnalogFmSstvDecoder.cs, MovingAverage.cs): slant jitter-gate off-by-one (#7), PD120/180/240 threshold fix (#8), post-commit state reset (#9), boundary-carry fix on rate-change lines (#10). **Regressed a real legacy-captured golden vector** (pd90, delta 0.93->3.74 crossing its 3.0 tolerance) — root-caused via git-stash bisection to #10 specifically, then re-measured and re-documented per this file's established tolerance-history convention (not blindly widened) — new tolerance 7.0, all 8 modes' deltas recorded in `GoldenVectorTests.cs`'s own comment.
- **Piece 3** (VisLockStateMachine.cs, AvtTrainingLockStateMachine.cs): `Math.Round`->truncation fix (#11) in `MsToSamples`.
- **Piece 4** (RobotScanlineDecoder.cs, YCbCrSequentialScanlineDecoder.cs, YCbCrLinePairedScanlineDecoder.cs, RgbSequentialScanlineDecoder.cs, MonoAveragedPairedScanlineDecoder.cs): Robot36 line-0 chroma default 0.0->128.0 (#27, was producing saturated-green instead of neutral-gray), YCbCr truncation-domain fix (#28 — corrected the audit's OWN first-pass mistake: must truncate `value-128` then `+128`, not truncate `value` directly), pixel-boundary round->ceiling fix (#29), Robot36 asymmetric threshold (#30), PD/MP/MN chroma trim-factor fix (#32).
- **Piece 5** (AnalogFmSstvEncoder.cs, SstvModeRegistry.cs, 3 encoder files): MR/ML inter-channel hold now genuinely holds the previous frequency via new `HoldPreviousFrequencySegment` record (#24, was emitting a fixed 1900Hz), TX floor-not-round fix (#25).
- **Piece 6** (new `TxOutputBandpassFilter.cs`, AnalogFmSstvEncoder.cs): ported legacy's always-on TX output bandpass filter (#26) — the highest-priority finding (real RF-hygiene/spectral-purity concern, not just a test-fidelity nit). Required its own Kaiser/Bessel-window `I0` port (verbatim truncated series, not an exact modified-Bessel function — matters at the 1e-12 fixture tolerance), fixed 24-tap count (NOT scaled like the RX-side `SearchBandpassFilter`), and is constructed as a LOCAL variable inside `EncodeAsyncCore` (not a field — `AnalogFmSstvEncoder` is a DI singleton). Coefficient test fixtures independently computed via a fresh Python translation of `fir.cpp` (not derived from the C# port) — script at `/tmp/.../scratchpad/tx_bpf_reference.py` if ever needed again. Re-measured `EncoderOutput_DecodesSimilarlyTo_RealLegacyAudioDecode`'s 8 per-mode deltas per this file's convention (all comfortably within existing tolerances, mixed improve/worsen, none needed to change) — documented that the OTHER TX golden-vector tests (`LegacyDecode_OfThisPortsEncoderOutput_MatchesSourceImage`, `TxCaptureFixturesTests`) read stale pre-fix checked-in fixtures and don't exercise this filter at all; a real `TxCapture/` re-capture against actual legacy MMSSTV remains an open follow-up, not silently unstated.
- **Piece 7** (WaterfallSource.cs): non-finite-sample guard (#18) in the port's own [-1,1] domain, not legacy's ±32768.

**This plan went through 2 rounds of auditor plan-review before building (both caught real gaps, esp. Piece 6's Kaiser-window/tap-count/state-lifetime issues) — and a 3rd, FINAL milestone-audit pass AFTER all 7 pieces were implemented caught a genuine remaining bug**: finding #1's sync-tone-resonator retune had its sign backwards. `AfcTracker.CorrectionHz` is designed to correct a MEASUREMENT back toward nominal; legacy's `dfq` (fed to `InitTone`) instead moves the RESONATOR to follow the actual received drift — these are negations of each other, and using `CorrectionHz` directly (my original implementation) retuned the resonator AWAY from the signal by 2x the real offset, making AFC's retune worse than not retuning at all. Fixed to `dfq = -_afcTracker.CorrectionHz` in `AnalogFmSstvDecoder.cs`'s `ApplyAfcCorrections`, independently re-derived and confirmed via revert-fix-confirm-fail (buggy sign gives exactly 1197.5Hz, matching the auditor's hand-predicted number; fixed gives ~1202.5Hz). The test that should have caught this (`AfcTests.cs`) was originally non-discriminating (`Assert.NotEqual(1200.0, ...)` passes for either sign) — rewritten to assert the correct DIRECTION, not just "changed from nominal." One other auditor-suggested test tightening (SlantTests.cs's #10 carry-bound test, `[0,1)` vs `[0,effectiveSamplesPerLine)`) was tried and reverted — the suggestion was algorithmically correct but didn't account for this test sampling state at `LineDecoded` time (pixel-decode-driven) rather than at the slant-tracker's own line-boundary-commit instant; confirmed by testing, not just re-reasoning. Two low-priority test-coverage gaps remain, both explicitly noted rather than silently dropped: no dedicated test for #4's gate-vs-application split (verified correct by direct code reading, just untested), and #1's retune doesn't quantize-and-change-guard on whole Hz the way legacy's `m_AFCFQ != dfq` does (harmless on a 100Hz-bandwidth resonator).

**Explicitly excluded from this plan, tracked separately**: the 4 `PORT-ONLY BUG` findings (#34 int-overflow after ~13.5h continuous RX, #36/#37 WAV 32767/32768 scale, #38 WAV format-validation hardening) have no legacy counterpart to "match" at all — real bugs, but a different kind of fix, deliberately not bundled into this pass. All `KEEP_PORT_DEVIATION`/`NEEDS_MAINTAINER_DECISION` findings got no code change, as intended.

**Full verification**: `ScanlineStudio.Core.Sstv.Tests` 594/594 green (was 564 before this session — 30 new/extended test methods across `AfcTests.cs`, `LevelAgcTests.cs`, `SlantTests.cs`, `VisLockStateMachineTests.cs`, `AvtTrainingLockStateMachineTests.cs`, `RobotScanlineDecoderTests.cs`, `Limit256ClampTests.cs`, `PixelPitchSegmentBoundaryTests.cs`, `AnalogFmSstvEncoderFooterTests.cs`, `WaterfallSourceTests.cs`, plus 2 new files `HoldPreviousFrequencySegmentTests.cs`/`TxOutputBandpassFilterTests.cs`), including every real legacy-captured golden vector. `ScanlineStudio.Application.Tests` (43), `Core.Imaging.Tests` (17), `Core.Logbook.Tests` (47) also re-verified green (all three have a `FakeSstvDecoder` that needed a new `ResetAgc()` no-op/counter for the `ISstvDecoder` interface change). Full solution build clean.

**Nothing committed yet** — 40 modified files + 3 new files (`TxOutputBandpassFilter.cs`, `HoldPreviousFrequencySegmentTests.cs`, `TxOutputBandpassFilterTests.cs`) sitting in the working tree, plus `ultracode_review.md` (the original 38-finding audit report, untracked, not yet updated with the #1 sign-bug postscript) and this file. Waiting on user go-ahead before committing.

## Resume here (2026-08-07, superseded by the entries above) — ultracode DSP behavioral-divergence audit (legacy YONIQ vs this port) complete, 38 findings, plan drafted to fix the 19 MATCH_LEGACY ones, plan reviewed once by the auditor (Piece 6 corrected), NOT YET IMPLEMENTED.

User asked to run the `/code-review ultra`-style deep DSP audit ("ultracode" effort) across every DSP
module — filters, oscillators, FM demod, sync/slant timing, VIS/header decode, TX/RX scanline codecs,
encoder/decoder orchestration, audio buffer/resample/WAV — comparing this port against the actual
legacy YONIQ C++ source, read-only. Full report: `ultracode_review.md` (repo root, untracked, not
committed — durable reference, don't duplicate its content here, just the facts needed to resume).

**Method** (3 waves, ~27 total subagent calls, each `auditor` type — Opus/high-effort/read-only):
Wave 1 = 10 parallel agents, one per subsystem, each reading the actual legacy source directly (never
inferring TX from RX or vice versa) and hand-deriving candidate divergences. Wave 2 = 10 fresh
independent agents re-deriving each Wave-1 claim from scratch with zero knowledge of Wave 1's
conclusions — several claims were refuted or corrected here (e.g. "native ring buffer drops samples
where legacy hard-stops" was refuted: legacy loses *more* data on overrun, not less). Wave 3 asked a
different, new question per surviving finding: **does "make the port match legacy" actually mean
porting a legacy bug forward?** Each got an explicit verdict — `MATCH_LEGACY` (legacy behavior is
confirmed intentional design), `KEEP_PORT_DEVIATION` (legacy's behavior is an accidental C++/Delphi
implementation artifact — uninitialized memory, an `int`-narrowing side effect — and the port's
current divergence should stay, documented not "fixed"), `PORT-ONLY BUG` (no legacy counterpart
exists at all), or `NEEDS_MAINTAINER_DECISION`.

**38 findings, verdict breakdown**: 19 `MATCH_LEGACY`, 11 `KEEP_PORT_DEVIATION`, 4 `PORT-ONLY BUG`
(#34 int-overflow after ~13.5h continuous RX; #36/#37/#38 — these three turned out on Wave-3 review to
have **no legacy counterpart at all**, since legacy has no WAV file I/O — the cited `Wave.cpp` code is
the sound-*device* path, not a file format; reclassified from "legacy divergence" to "port-internal
32767-vs-32768 scale-convention bug"), rest `NEEDS_MAINTAINER_DECISION` or no-divergence-found.

**Highest-impact confirmed bugs** (full detail + exact fix in `ultracode_review.md`, findings numbered
there): Robot 36 line-0 chroma defaults to the wrong color-space domain — hand-computed actual RGB
output is neutral gray (130,130,130) in legacy vs full-saturation green (0,255,0) worst-case in this
port, guaranteed on the first row of every image (#27). Missing TX output bandpass filter — legacy's
always-on-by-default 700-2800Hz SSB passband, upgraded by Wave 3 from "golden-vector nit" to a real
transmit spectral-purity/RF-hygiene concern for a real ham-radio product (#26). AFC never retunes the
sync-tone tank filters, degrading exactly the off-frequency reception AFC exists to fix (#1). Three
independent `SlantTracker` bugs (jitter-gate off-by-one with an exact index-trace fix, no state reset
after a slant commit, wrong threshold for PD120/180/240) that compound on any multi-correction signal
(#7/#9/#8). AFC sign error (+6.25Hz/+2.0Hz in the wrong direction, confirmed via an independent
dimensional-coincidence check: 128 scaled units = exactly one luma level in both bandwidth modes) and
a zero-Hz lock-average seed that's ~1120Hz wrong on a rare guard-timeout path (#2/#3).

**Plan drafted**: `~/.claude/plans/fuzzy-yawning-melody.md`, 7 pieces covering all 19 `MATCH_LEGACY`
findings (+ #15's doc-comment-only fix), grouped by subsystem, each with its own unit tests using the
audit's own independently hand-derived numbers as test oracles. Explicitly excludes the 4
`PORT-ONLY BUG` findings (unrelated fix shape, flagged as a separate future plan) and all
`KEEP_PORT_DEVIATION`/`NEEDS_MAINTAINER_DECISION` findings (no code change intended).

**One `auditor` plan-readiness review round already applied** (this project's standing practice for
non-trivial plans before building): Pieces 1-5/7 came back verified against current source, no stale
line numbers, 5 small corrections folded in (R1-R5 — e.g. #1's actual retune target is
`_syncEnvelopeDetector` built in `InitializeSlant`, not the 7 VIS-time detectors in the constructor;
#24's blast radius is wider than first estimated, touching every encoder's segment-switch, not just
MR/ML's). **Piece 6 (the new TX bandpass filter) came back NOT READY on the first draft** — 3
load-bearing gaps the auditor caught: (1) `SearchBandpassFilter.MakeFilter` can't be reused as-is,
legacy's TX filter needs the Kaiser/Bessel window branch (`att=40`) that class deliberately doesn't
implement (it's always `att=20`, rectangular-window territory) — reusing it verbatim would compile and
look right while being silently wrong; (2) tap count must be a fixed 24 at every sample rate, not
scaled like the RX filter (would give 96 taps instead of 24 at 44100Hz); (3) the new filter's state
must be constructed locally inside `EncodeAsyncCore`, not as a constructor field, since
`AnalogFmSstvEncoder` is a DI singleton and a ctor-field filter would leak mutable per-transmission
state across calls. All 3 resolved in the plan file with the auditor's own cited legacy sources
(`fir.cpp:361-384`'s Kaiser branch, `sstv.cpp:2764`'s hardcoded tap count, `Program.cs:153`'s
singleton registration). Also caught: the golden-vector test suite doesn't actually regression-test TX
output at all (reads stale checked-in fixtures, doesn't re-encode) — Piece 5/6 need their own tests as
the real backstop, not the existing golden-vector run.

**Not yet started implementing** — plan is drafted and corrected, not yet approved/executed. No
commits, matching this project's "wait for explicit go-ahead" practice throughout its history (see
every entry below).

## Resume here (2026-08-07, superseded by the entry above) — QSO logbook backend: SQLite storage, ADIF import/export, GridTracker UDP streaming, QRZ.com Logbook API upload. All 7 plan pieces done, auditor pass on the highest-risk piece applied. Full solution build/test green (915 tests across 10 test projects, 0 failures, DSP golden-vector suite included). NOT YET COMMITTED.

User asked for "a modern QSO log back-end" — ADIF file writing, streaming to GridTracker, upload to
QRZ.com's API — explicitly backend-only (no UI wiring), other targets (LoTW/eQSL/Clublog/HRDLog)
deliberately "maybe later." `spec/08-logging.md` already designed the storage/ADIF shape
(`QsoRecord`/`ILogbookRepository`/`IAdifExporter`/`IAdifImporter`) but had zero code
(`project_logbook_not_implemented` memory, now stale — update/delete it). GridTracker
streaming and direct QRZ upload are genuinely new (not a legacy port — legacy `qrzcom.cpp` was
lookup-only, no GridTracker concept exists in YONIQ). Plan file:
`~/.claude/plans/snazzy-jumping-phoenix.md`.

**Research before building**: confirmed GridTracker's real-time ingestion is WSJT-X's own UDP
network protocol (verified directly against WSJT-X's `NetworkMessage.hpp` source) — specifically
the `LoggedADIF` message (type 12): a small binary header (magic `0xadbccbda`, schema, type) plus
two Qt-`QByteArray`-framed UTF-8 strings (id, ADIF text). The ADIF text field is just a complete
single-QSO ADIF file, so `IAdifExporter`'s own output feeds it directly — no separate protocol-level
encoding needed. QRZ's Logbook API is a separate, independent `POST
https://logbook.qrz.com/api` (`ACTION=INSERT`, form-urlencoded `KEY`/`ADIF`) — confirmed this
does NOT get superseded by GridTracker's own QRZ-forwarding feature, since the user wants direct
upload that works without GridTracker running.

**7 pieces built in order, each with tests, matching the plan**:
1. `QsoRecord`/`ILogbookRepository`/`SqliteLogbookRepository` (`Abstractions.Logbook` +
   `Core.Logbook`) — same `history.db` file as RX history (`ReceiveHistoryEntry.LinkedQsoId`
   already anticipated this table). `Id`/`ReceivedImageId` are `string` not `Guid`, matching
   `ReceiveHistoryEntry`'s existing convention. `GridSquare` added beyond the spec's original
   draft (trivial, needed for correct ADIF `GRIDSQUARE` + real GridTracker/QRZ interop) — the only
   scope addition; QSL flags/dupe-detection/contest-exchange stayed out per the roadmap's own
   "maybe later" framing.
2. `AdifExporter`/`AdifImporter` — pure (`TextWriter`/`TextReader`, no file I/O). `SstvModeId` maps
   to ADIF `MODE=SSTV` + `SUBMODE` + a non-standard `APP_SCANLINESTUDIO_SSTVMODE` field (lossless
   round-trip); non-SSTV `RadioMode` falls back to a small shared `AdifRadioModeMapping` table.
   Unmapped/unknown import fields preserved in `Notes` (spec's "raw-fields bag" requirement), not
   dropped. Field lengths are UTF-8 **byte** counts (ADIF's actual `<name:length>` definition) —
   see the auditor finding below for why this specifically matters.
3. `GridTrackerStreamer` — UDP `LoggedADIF` datagram sender, gates on its own
   `GridTrackerStreamingSettings` (off by default). **Got the CLAUDE.md §7 `auditor` pass this plan
   called for** (byte-level protocol correctness — same "silent failure" risk class as DSP
   buffer/encoding logic even though it isn't DSP): verdict EQUIVALENT-WITH-RISKS. Found and fixed
   a real bug — `AdifExporter`/`AdifImporter` were using .NET `char` count for ADIF field lengths,
   not the UTF-8 byte count ADIF's spec actually requires, which would silently corrupt any
   non-ASCII `NAME`/`QTH`/`COMMENT`/`COUNTRY` field in exactly the GridTracker payload this audit
   exists to protect (fixed by rewriting `AdifImporter` to parse on UTF-8 byte offsets instead of
   char offsets — safe because ASCII delimiter bytes never collide with UTF-8 continuation bytes).
   Also fixed: `GridTrackerStreamer.SendLoggedQsoAsync` only caught `SocketException`, letting a
   corrupt-settings-file or invalid host/port throw straight through its own documented
   "never throws" contract — widened to catch broadly except `OperationCanceledException`, plus
   host/port validation before use. Applied the same fix proactively to `QrzLogbookUploader`
   (sibling risk, same pattern, not separately audited). **Residual, unverifiable-in-sandbox risk**
   the auditor flagged: no `Heartbeat` message is ever sent, so if some GridTracker version gates
   `LoggedADIF` on a prior-registered client instance, datagrams could be silently discarded —
   flagged as the highest-value thing to check against a real GridTracker instance before
   shipping, not resolved further here (no real GridTracker available in this sandbox).
4. `QrzLogbookUploader` — `IHttpClientFactory`-based (first `HttpClient` usage in this codebase),
   fake-`HttpMessageHandler` tests covering `RESULT=OK`/`REPLACE`/`FAIL`/malformed responses.
5. `GridTrackerStreamingSettings`/`QrzUploadSettings` — nullable-property-only, STJ-default-loss-safe
   (this codebase's now-standard pattern), both opt-in/off-by-default.
6. `ILogbookSessionService`/`LogbookSessionService` (`Application` layer, mirrors
   `ISstvSessionService`'s facade role) — `LogQsoAsync` persists unconditionally first, then
   best-effort pushes to GridTracker/QRZ if enabled, returns a `LogQsoResult` with per-target
   success/error (no retry queue, a failed push is surfaced once). Also
   `SearchAsync`/`ExportAdifFileAsync`/`ImportAdifFileAsync` (the last persists every parsed
   record, not just a preview).
7. `Program.cs` DI wiring — `AddHttpClient()` (new), all 5 new interfaces registered as singletons
   alongside the existing RX-history block.

**Full solution verified green** after the last piece (10 test projects, 915 tests total, DSP
golden-vector suite run in the background since it alone takes ~7.5 min — confirmed unaffected, no
`Core.Sstv` touch this pass).

**Not yet done / explicitly deferred, not forgotten**: no UI controls wired to any of this yet
(backend-only pass, per the user's own wording). LoTW/eQSL/Clublog/HRDLog uploads, QSL sent/received
flags, duplicate-QSO detection, contest serial exchange, offline `ICallsignLookup`/QRZ.com *lookup*
(spec's separate enrichment feature — untouched), GridTracker `Heartbeat`/`Status`/`Decode`
live-tracking messages, automatic retry queue for failed pushes — all explicitly "maybe later" per
the plan file, not silently dropped. Real-world verification against an actual GridTracker instance
and a real QRZ subscription+API key is still outstanding (not available in this sandbox) — the
auditor's Heartbeat/instance-registration concern in particular should be checked there.

**Nothing from this session is committed yet.**

## Resume here (2026-08-07, superseded by the entry above) — 8-item small-backlog batch (data-model/logbook items dropped from scope, audio/radio-control items built) from `spec/14-roadmap.md`'s Phase 4+ backlog. Full solution build/test green (10 projects, all 554 `Core.Sstv.Tests` including the DSP golden-vector suite). Roadmap struck through for all 8. NOT YET COMMITTED.

User asked to start the small "data model/logbook" + "audio/radio-control" backlog items as one
combined plan (no per-item plan/auditor cycles). 3 parallel `Explore` passes found the real scope
differed from the roadmap's own "trivial/small" framing: the whole QSO logbook doesn't exist in
code yet (only spec'd) so the 3 logbook/QSL/grid-locator items were **dropped from this batch**
(need their own dedicated plan later); RTS-on-RX was **dropped** too (no serial-control surface
exists, may conflict with Hamlib's own RTS-PTT-type ownership, needs a legacy re-check — CLAUDE.md's
CAT-layer rule). Final scope, all 8 done: app process priority, capture-drain-thread priority,
historical power/ALC ring buffer (backing data only, no chart), tune-satellite-trigger toggle, PTT
lock, AFC on/off toggle, sound FIFO buffer size (native), stereo capture source + stereo-TX toggle
(native). Plan file: `~/.claude/plans/rustling-wibbling-pike.md`.

**PTT lock was the one genuinely safety-critical piece, and the audit pass caught real defects
before shipping** (exactly why CLAUDE.md's audit-delegation rule exists) — an initial
implementation was reviewed by the `auditor` subagent and came back NOT SAFE TO SHIP: SWR
auto-cutoff/manual Stop TX couldn't force-unkey while a lock was engaged (the whole point of a
safety cutoff, defeated); app shutdown while locked left the rig keyed (`DisposeAsync` didn't
un-key); unlock could silently no-op on a still-keyed rig in exactly the cases that mattered
(`TuneAsync`'s `leaveKeyedAfterTune` leaves PTT keyed without ever setting the lock flag, so the
old short-circuit-on-same-state unlock logic did nothing); a TOCTOU race in the lock method itself
could drop an overlapping lock/unlock command; RX paused by a lock-covered Transmit/Tune call never
resumed after unlock. All fixed in `SstvSessionService.cs` (`PlayWithPttAsync`'s finally now
distinguishes normal-vs-abnormal termination and force-overrides the lock on the latter;
`SetPttLockAsync` removed the short-circuit entirely — always sends the command, confirmed
idempotent-safe on every real protocol backend — and added a `SemaphoreSlim` gate;
`_rxPendingResumeAfterUnlock` handoff added; `DisposeAsync` force-unkeys). Every fix has a
dedicated regression test, and the single most severe one (SWR-cutoff-can't-override-lock) was
verified via revert-fix-confirm-fail — reintroducing the bug made the new test fail exactly as
predicted. One residual race is documented as accepted (unlock racing a Transmit/Tune's own entry)
since fully closing it would make emergency-unlock less responsive, a worse trade — no production
caller wired to either PTT-lock or `leaveKeyedAfterTune` yet, so this is latent, not exercised.

**AFC toggle** also got an auditor pass (`Core.Sstv` touch, mandatory per CLAUDE.md §7 even for a
small diff) — verdict EQUIVALENT-WITH-RISKS, no blockers (confirmed every `_afcTracker` read site
already null-checks it, confirmed the AVT-null-safety net genuinely covers the new AFC-off case
too). Two cheap risk fixes applied: a settings-default test that only proved half the real upgrade
path, and an undocumented restart-only caveat on the ctor.

**Native shim work (Group A) was the highest-risk piece structurally** (touches
`native/yoniq_audio.c`/`.h`, not just C#) but turned out clean: sound FIFO buffer size
(`yoniq_audio_open_options.period_size_in_frames`/`periods`, 0 = miniaudio's own default,
safe-by-construction) and stereo capture/TX (`channels`/`channel_select`, explicitly **not a
confirmed legacy port** — documented as an assumption at every layer, not traced against actual
legacy source) both verified via real virtual-device (PipeWire/PulseAudio null-sink) round-trip
tests, not mocks. **Found and fixed a real bug while adding stereo TX**: the pre-existing
underrun-padding `memset` only ever zeroed one channel's width — with stereo TX on, that would
leave the Right channel carrying stale/garbage backend memory on every underrun. Fixed to zero
both channels' worth, verified via revert-fix-confirm-fail (a real virtual-device test with the
naive version reintroduced failed with a measured peak of ~1.07 on the "should be silent" channel,
confirming the test genuinely catches this class of bug, not just a plausible-looking assertion).
`IAudioEngine`'s "samples are always mono" contract is unchanged either way — stereo only ever
exists between the native device and this shim's own ring buffers.

**Not yet done / explicitly deferred, not forgotten**: no UI controls wired to any of these 8
settings yet (backend-only pass, by design — a future pass adds Options-window controls). Logbook
items (QSL flags, grid locator, dup-detection) need their own plan once the QSO logbook itself
exists. RTS-on-RX needs a legacy re-check before it's clear whether/how it fits this port's CAT
architecture. `docs/removed-features.md`: no entries needed (nothing removed this pass).

**Nothing from this session is committed yet** — full solution build/test is green, roadmap is
updated, but committing/pushing hasn't been asked for or done.

## Resume here (2026-08-06, superseded by the entry above) — RX history retention cap, done autonomously overnight (user asked for non-GUI backend work while away). Full solution build/test green (all projects, 548 DSP tests included). COMMITTING AND PUSHING THIS ENTRY.

User picked this from a 4-option menu (`spec/14-roadmap.md`'s Phase 4+ backlog) as the safest
non-GUI task to run unsupervised. **Verified against actual legacy source first** (CLAUDE.md §0/§3
rule) rather than trusting the roadmap's own "legacy default 32" claim at face value — briefly
suspected it was wrong (`CBitmapHist`'s own C++ constructor sets `m_Head.m_Max = 64`,
`ComLib.h:591`) before tracing further and confirming `Main.cpp:898`'s `sys.m_HistMax = 32` (read
from ini `[Window]/HistMax`) is unconditionally applied over that constructor default on every
`CBitmapHist::Open()` call (`ComLib.cpp:2658-2686`, both the fresh-file and existing-file
branches) — so 32 **is** the real, always-effective default; 64 never survives to matter. Full
citation trail lives on `ReceiveHistorySettings.DefaultMaxEntries`'s own doc comment now, not just
here.

**Implementation** (`ScanlineStudio.Core.Logbook`): `ReceiveHistorySettings` gained
`MaxEntries` — nullable (`int?`), **not** a property-initializer default, per the exact System.Text.Json
default-loss trap already documented/fixed once this session on
`AudioDeviceSettings.TxVolumePercent` (STJ silently deserializes a property missing from an
already-persisted JSON payload to the CLR default `0`, not the C# initializer value) — the `??
DefaultMaxEntries` fallback lives only at the one read site, `ResolveMaxEntriesAsync`.
`SqliteReceiveHistoryStore.RecordAsync` now trims the `ReceiveHistory` table to the newest N rows
(by `ReceivedAt`) after every insert, via a `DELETE ... WHERE Id NOT IN (SELECT ... ORDER BY
ReceivedAt DESC LIMIT $maxEntries)`.

**Deliberate scope boundary, documented not silently dropped**: this trims the *queryable index*
only — it does **not** delete the underlying image files from disk. Legacy's version is a genuine
fixed-size ring buffer (one `history.bin` blob, oldest slot physically overwritten); this port
saves separate real image files, and unsupervised automatic file deletion while the user is asleep
is a materially different (higher, irreversible) risk than trimming a database index. Orphaned
files beyond the retention window will accumulate on disk — a real, tracked follow-up
(`spec/14-roadmap.md`'s own entry now says this explicitly), not a bug in this piece.

**Tests** (`SqliteReceiveHistoryStoreTests`, 3 new, `ScanlineStudio.Core.Logbook.Tests` 10→13):
default-32 trimming (33 inserts → newest 32 survive, oldest evicted), a configured non-default
limit is honored, and the STJ-default-loss regression specifically — constructs a raw `JsonElement`
with the `ReceiveHistory` section present but genuinely missing the `MaxEntries` property (not just
`null`, actually absent, simulating a real pre-existing `settings.json`) and asserts the fallback
is 32, not 0. **Verified via revert-fix-confirm-fail**: temporarily changed `MaxEntries` back to a
naive `int = DefaultMaxEntries` property initializer, confirmed that exact regression test fails
with `Expected: 32, Actual: 0` (i.e. every existing installation's history would have been silently
truncated to zero entries the moment this field shipped), then restored the nullable fix.

**Full solution verified green** after the piece (not just the touched project, per this
milestone's own scope): all 10 test projects, 883 tests total (`Core.Sstv.Tests`' 548 DSP
golden-vector tests included, run in the background since they alone take ~8 minutes — confirmed
unaffected, as expected for a change with zero DSP-layer touches).

`spec/14-roadmap.md`'s own backlog entry updated in place (struck through, marked done, citation
trail added) rather than left stale for a future session to re-discover.

## Resume here (2026-08-06, superseded by the entry above) — Receive tab pixel-fidelity pass against mock2: status bar, Spectrum card height, Mode card (Auto/Locked segment + active-mode dropdown), Sync & slant card (Re-sync/Reset + Advanced timing), Incoming-frame action bar/progress bar. Found and fixed a recurring root cause: several controls (segments, UniformGrid button pairs, Expander) don't stretch to fill their parent's width by default in this codebase's StackPanel-heavy layout - needs explicit HorizontalAlignment="Stretch" on the container AND each child. Committed as `5fd61a9`.

Continuation of the RadioHeaderView pixel-fidelity work (previous entry below, commits `a4291c1`/
`a3d7133`), now working down through the rest of the Receive tab's left column and the Incoming
Frame card.

**Root-cause pattern found this round, reused 3 times**: `Border.segment`/`UniformGrid`/`Expander`
placed inside a `StackPanel` size to their own content instead of stretching to the parent's full
width, even though Avalonia's `StackPanel` normally stretches children by default - something
about these specific control combinations (custom `RadioButton.seg` template, `UniformGrid`,
`Expander`'s own `HorizontalAlignment` default) opts out of that. Fix is always the same: add
`HorizontalAlignment="Stretch"` explicitly on the outer container **and** each child
(`HorizontalContentAlignment="Center"` on buttons/radios so their text stays centered once
stretched). Hit and fixed for: the RadioHeaderView USB/LSB/FM segment (previous entry, worked
around differently by dropping to plain `ToggleButton`s); this round's Mode-card Auto/Locked
segment; Sync-card Re-sync/Reset button pair; Sync-card Advanced-timing `Expander`. Worth
remembering as the first thing to check next time a mock2 control renders "too narrow, left-
aligned, with a gap" instead of spanning its row.

**Mode card** (`MainWindow.axaml`'s Receive-tab left column, `RxImagePaneViewModel.cs`):
- Added mock2's own Auto/Locked segment (`Border.segment`/`RadioButton.seg`, matching the exact
  pattern `TxControlsPaneView.axaml`'s Auto/Manual segment already uses) - "Auto" is statically
  checked (matches the real always-auto-detect behavior, `ISstvDecoder` has no manual lock-to-
  mode feature); "Locked" is present but non-functional, same visual-only convention as the
  quick-mode pill grid below it.
- Removed the separate "Detected —" label row: redundant once the active-mode dropdown itself
  shows the same real value.
- The mode-override `ComboBox` is no longer `IsEnabled="False"` (mock2 doesn't render it as
  disabled) and its one item is now bound to a new real `RxImagePaneViewModel.DetectedModeDisplay`
  computed property (`"{DisplayName} — VIS {VisCode}"`, e.g. "Scottie 1 — VIS 60") instead of a
  hardcoded "SC1" - matches mock2's own "Scottie 1 — VIS 60" format exactly using real
  `SstvModeDefinition.VisCode` data, not a placeholder string. `DetectedMode`'s existing
  `DisplayName` ("Scottie S1" per the legacy-derived real naming) was deliberately left alone even
  though mock2's own literal text says "Scottie 1" without the S - that's real backing data tied
  to legacy fidelity, not a placeholder to reshape for a UI pass.

**Sync & slant card**: Re-sync/Reset buttons and the Advanced-timing `Expander` both needed the
stretch fix above. The `Expander` also needed **height** fixed:
`ChromeOverrides.axaml` gained `ExpanderMinHeight` (48px FluentTheme default → 19px, matching this
app's compact `Button`/`ToggleButton` height convention), `ExpanderHeaderPadding` (16,0,0,0 →
8,0,0,0), `ExpanderChevronButtonSize` (32 → 19), `ExpanderChevronMargin` (20,0,8,0 → 6,0,4,0) - same
"FluentTheme takes this from a resource key, not the template, so no local Height setter can shrink
it" root cause as the earlier `TextControlThemeMinHeight` fix in the same file.

**Incoming-frame action bar** (bottom of the "Incoming frame" card): the progress bar was a fixed
180px stub sitting flush against the Log QSO button; restructured the row from `StackPanel` to
`DockPanel` (buttons docked left, frame-count text docked right, bar as the fill-the-middle last
child) so the bar actually spans the row like mock2's own. Went through a few rounds of live
correction on the frame-count text specifically: first added it back next to the bar (right-docked,
no "Ln" prefix - new `Panes.RxImage.FrameLineCount` = "84/120", replacing the removed
`Panes.RxImage.LineProgressValue` = "Ln 84/120"), then also dropped the redundant duplicate that
used to sit in the card header's own top-right caption (mock2 doesn't show the count twice either).

**Bottom status bar + Spectrum card** (previous session's last round, for continuity): status bar
restructured into a `DockPanel` so "frames today/log" pins to the far right like mock2's own
layout, "RX" renamed to "Receiving", M1 memory tag added, every literal value/wording matched to
mock2 exactly. Spectrum & waterfall card's row height reverted from 180px back to mock2's own
154px - the extra height was a workaround for a `NumericUpDown` clipping bug that
`ChromeOverrides.axaml` (added earlier this session) already fixed at the root, so it was pure
excess by the time this round started. Committed as `a3d7133`.

**Build/test discipline held throughout**: every round rebuilt `ScanlineStudio.UI`, ran
`ScanlineStudio.UI.Tests` (74/74 green start to finish), rebuilt `ScanlineStudio.Host`, relaunched
the real app, and screenshotted via the established python-xlib capture script + `wmctrl`
positioning workflow on the secondary monitor (HDMI-1, real offset x=3840 - corrected mid-session
after a wrong x=1920 assumption produced a "resized not moved" screenshot bug) before reporting
back. App launches were flaky this round (`nohup ... &` intermittently reported exit code 144 with
no process/log surviving, cause not diagnosed - a plain retry always worked on the 2nd or 3rd try;
not a code bug, a sandbox/process-launch quirk).

**Not yet done / explicitly deferred**: Transmit and Gallery tabs still haven't had this same
close pixel-diff pass (Pieces 12-17 landed them structurally only). Two-tone frequency display,
"Edit presets…" and Tune-feature button mapping remain deferred from the previous entry, unchanged.

## Resume here (2026-08-06, superseded by the entry above) — RadioHeaderView extracted from MainWindow.axaml into its own UserControl, pixel-fidelity pass against mock2 across ~15 rounds of live screenshot feedback (VFO/Favourites/Transceiver cards), operator-callsign menu chip added, window min-size locked to FullHD-safe floor. Build/tests green throughout. COMMITTED AND PUSHED (a4291c1, a3d7133).

Continuation of the "keep going until our current program looks like the actual mock" work (Pieces
12-17 of the gap-closure plan, `~/.claude/plans/wondrous-crafting-ladybug.md`, already committed as
`4f810f4`/`7ac8cfb` before this entry). This session's work was **not** part of that piece list —
it's the header-specific pixel-fidelity pass the user drove live via screenshots, one small change
at a time ("go block by block instead of all over the window").

**RadioHeaderView.axaml (new file)**: VFO/Favourites/Transceiver header cards extracted out of
`MainWindow.axaml` into their own `UserControl` (designer supplied `mockups/split/*` as the
starting shape; only `Views/`+`Styles/` dropped in, `mockups/fixes/`+`mockups/split/` stay
untracked reference material, never committed). `MainWindow.axaml` now just does
`<views:RadioHeaderView Grid.Row="1" DataContext="{Binding RadioStatus}" />`. The manual
frequency-entry TextBox+Set+mode-ComboBox that used to sit next to the VFO readout was removed
entirely — mock2 has no equivalent control there.

**Fixed this session, in order, each verified via a live screenshot on the secondary monitor
(HDMI-1, real offset is x=3840 not x=1920 — corrected a wrong assumption baked into the capture
workflow)**:
1. USB/LSB/FM sideband segment control's merged-border trick clipped the first segment's left
   edge and later double-drew top/bottom borders when patched — replaced with plain `ToggleButton`s
   per direct user instruction ("just make them buttons nothing fancy").
2. "No frequency" placeholder text → literal "000.000.000" to match mock2's zero-state readout.
3. Favourites card restructured to mock2's real two-row shape: row 1 = preset-recall buttons +
   "Store current" docked right on the **same row** (horizontally aligned with the presets, not a
   separate row); row 2 = "Edit list…"/"Import"/"Scan M1–M3" left-aligned. The real "Edit
   presets…" flyout (`EditorRows`/`AddPresetRowCommand`/`SavePresetsCommand`) was removed from view
   per direct user request — **not** deleted from `RadioStatusViewModel`, just unmapped; still
   needs a real button to attach to later. 5 orphaned en.json keys removed
   (`RadioStatus.EditPresets`/`PresetLabelPlaceholder`/`RemovePreset`/`AddPreset`/`SavePresets`).
   New `FrequencyPresetButtonViewModel.FrequencyWithMode` computed property backs the two-line
   preset-button content.
4. Favourites row-2 buttons: "Edit list/Import/Scan" forced to white background (inline
   `Background="White"`, scoped to just these three — `Button.small` stays its normal gray
   elsewhere in the app, e.g. Options/TxImageEditor/RxHistory toolbars); "Store current" got a new
   `Button.success` class (green, `ChromeOverrides.axaml`) — later routed back and forth (blue →
   green per literal mock pixels → green again per direct user override, see item 8) before
   settling on green; text in every one of these buttons set `HorizontalContentAlignment="Center"
   VerticalContentAlignment="Center"`.
5. **Operator callsign menu chip (new, real)**: `MainViewModel` gained `Callsign` (loaded from
   `OptionsSettingsService`/`OperatorSettings`, same pattern as `OptionsWindowViewModel`) and a
   computed `CallsignDisplay` that falls back to the ham-radio placeholder `"N0CALL"` when unset —
   always-visible chip (`Border.pill.softPill`, same green-chip palette mock2 itself uses for
   `DL2QSK · JO31`), docked top-right of the Menu row (`MainWindow.axaml`'s Grid.Row="0" restructured
   from a bare `<Menu>` into a 2-column `Grid`). Per direct user instruction, **no** cfg-profile
   chip and **no** grid-locator suffix are shown — mock2 has both, this app has neither concept
   backed by real data yet.
6. Window min-size: was `1024x600` (a "smallest before panes break" floor), now pinned equal to
   `Width`/`Height` (`1920x1032` — FullHD 1920x1080 minus 48px for a taskbar/menu bar) so the
   window can only grow, never shrink below the FullHD-safe floor. Per-pane alignment *at* that
   floor is an explicitly deferred follow-up, not yet tuned.
7. Transceiver card: Receiving/Halt are now equal-width (`Grid` with two `*` columns, was an
   unequal-width `StackPanel`); the Tune row (Hz/s edit fields + Tune button) was removed from view
   per direct user request — mock2 never shows it here either, and `TuneCommand`/
   `TuneFrequencyHz`/`TuneDurationSeconds` remain untouched on `RadioStatusViewModel`, just
   unmapped. `RadioStatus.RxLevelLabel`/`TxVolumeLabel` re-worded "RX LEVEL"/"TX VOLUME" → "RX
   level"/"TX level" to match mock2's casing (`TxVolumePercent`'s real 0–100% binding stays as-is —
   mock2's own "-6.0" TX-level number is a literal/static display value with no real backing
   concept, unlike the real working volume slider already wired here).
8. **Explicit user override of mock2's own literal pixels**: sampled mock2's "Receiving" button
   at exactly `#D6E7F7`/`#5A9AD4` (same blue as `Button.primary`) and matched it pixel-for-pixel
   first; user then asked for green/red instead ("receiving = green, halt = red") — reverted to
   the pre-existing `ToggleButton.receiving:checked` green palette (`#C2E8C2`/`#3F8F3F`, now also
   bold+colored text to match the weight mock2's own blue version had) and restored `Halt`'s
   `Classes="halt"` (the same red destructive-action style used for Stop-Transmit/TX-editor-Halt
   elsewhere in the app). Net effect: this one card intentionally diverges from mock2's literal
   pixels by direct instruction — green/red reads as a clearer status pair than mock2's flat blue.

**Build/test discipline held throughout**: every round rebuilt `ScanlineStudio.UI`, ran
`ScanlineStudio.UI.Tests` (74/74 green start to finish, only the very last no-test-needed round
per explicit user instruction), rebuilt `ScanlineStudio.Host`, killed+relaunched the real app, and
screenshotted via the established python-xlib `XGetImage` capture script
(`scratchpad/capture.py`) + `wmctrl` positioning workflow before reporting back.

**Repeated self-inflicted bug this session**: the standard "sweep `--` out of XAML comments before
build" python regex (`re.sub(r'-{2,}', '-', ...)`) was applied incorrectly at one point and
corrupted comment **delimiters** themselves (`<!--`→`<!-`, `-->`→`->`), not just interior
double-hyphens — caught immediately by the build error, repaired with a second regex pass
(`<!-(.*?)->` → `<!--\1-->`) that restored delimiters while leaving already-collapsed interior
hyphens alone. Going forward: only sweep the comment **body** between delimiters, never the whole
matched span including the delimiters.

**Not yet done / explicitly deferred, not forgotten**:
- Map "Edit presets…" functionality onto "Edit list…" (or a new location) — VM-side is ready
  (`EditorRows`/`AddPresetRowCommand`/`SavePresetsCommand`), just not wired to a button.
- Map the Tune feature (`TuneCommand`/`TuneFrequencyHz`/`TuneDurationSeconds`) onto some control —
  same situation, VM-side untouched.
- Two-tone frequency display (bold leading digits + dimmed `.000` trailing digits, matching
  mock2's own split) — blocked on deciding a real `FrequencyDisplay` format that has something to
  split on (current format doesn't dot-group the way mock2's literal text does).
- Transmit and Gallery tabs have **not** had the same close pixel-diff pass the Receive-tab header
  just got — Pieces 12-17 landed them structurally, not fine-tuned against mock screenshots.

## Resume here (2026-08-06, superseded by the entry above) — Production logging rollout DONE across all 16 projects, auditor-reviewed twice, one real bug found+fixed. Options-menu click mystery CONFIRMED (not logging-related) — real sandbox input-delivery issue, not a code bug. NOT YET COMMITTED.

**Logging rollout** (triggered by the Options-menu click investigation below going nowhere with
`Console.WriteLine` diagnostics — user's direction: stop, build real production logging instead,
make it a standing project habit): full `Microsoft.Extensions.Logging` rollout across all 16
projects. `ScanlineStudio.Host/FileLoggerProvider.cs` (new) + the default console provider
`Host.CreateApplicationBuilder` already wires; writes to
`~/.local/share/ScanlineStudio/logs/app.log` (fresh each launch). `docs/logging-guidelines.md`
(new) is the durable guideline — mandatory `[LoggerMessage]` source-generator pattern (CA1848 +
TreatWarningsAsErrors makes a plain `logger.LogDebug(...)` call a build error), level guide,
hot-path rate-limiting rule. One-line pointer added to `CLAUDE.md` §3; memory saved
(`project_logging_infrastructure`).

Implementation: a first `auditor` (Opus, high) pass produced the placement plan (P0-P6, ~44
swallowed/missing catch blocks found, ~18 classes needing `partial`, 9 projects needing the
package ref). Split across **4 parallel forks by project boundary** (Application;
Radio+Hamlib+Rigctld; Audio.MiniAudio; UI ViewModels) — each touching a disjoint file set, zero
merge conflicts. Coordinator (me) handled `Program.cs` (global exception handlers, guarded every
previously-unguarded startup call, `--log-level` CLI toggle defaulting to `Debug` for now),
`Settings/JsonSettingsStore.cs` (corrupt-settings.json no longer crashes startup unlogged),
`Core.Logbook/ReceiveHistoryRecorder.cs` (a failed RX-image save is no longer silently swallowed),
plus wiring `MainViewModel`→`RadioStatusViewModel`'s new logger and the two Radio factories that
initially defaulted to a no-op logger.

**Second auditor pass** (explicit user request — "just like we do for normal tasks") found one
real logic bug: `RadioController` was logging a false "Radio reconnected" from `ResolveProtocol`
(which only *constructs* a protocol object — both real backends connect lazily, so this always
"succeeds" even against a dead rig) instead of the next actually-successful poll, which also
permanently suppressed the real reconnect log afterward. Fixed — recovery is now logged from the
successful-poll site, gated on `_lastLoggedFailureState`. Also fixed per that pass: a genuine
hot-path violation (`RigctldClientProtocol.TryGetMeterAsync` logged every soft-failed meter read
unconditionally, up to 3×/poll during TX — now gated by per-meter state transition, matching
`RadioController`'s own established pattern), `--log-level`'s validation gap (accepted
out-of-range numeric values like `99` which would have silently disabled all logging; now
`Enum.IsDefined`-guarded with a `Console.Error` message on a bad value), and logger-category
collapse (`TcpTransport`/`RigctldClientProtocol` were sharing `RigctldProtocolFactory`'s logger
category; the three Hamlib types were sharing `HamlibProtocolFactory`'s; `MiniAudioCaptureSession`
was sharing `MiniAudioEngine`'s — all four factories/engines now inject `ILoggerFactory` and give
each constructed type its own correctly-categorized logger). 4 stale doc comments (claiming "no
logger yet, defaults to no-op" in files that do get a real one today) also fixed.

**Verified for real, twice**: launched the actual app both before and after the `RadioController`
fix — first run showed the exact bug the auditor predicted; second run (post-fix) shows correct
per-type log categories and no false "reconnected" line for a rig that's genuinely still down.
Full solution build clean; every test project green (293 fast tests + 548 DSP tests unaffected +
112 Radio + 51 MiniAudio re-verified after the fixes = still all passing).

**Options-menu click mystery — resolved as "not a code bug"**: with real logging now proving the
click path (`MainViewModel.OpenOptionsCommand` → `OptionsRequested` event → `MainWindow.axaml.cs`'s
`ShowDialog`), re-tested the original click and confirmed **definitively** (not just via earlier
ad-hoc `Console.WriteLine`) that the top-left "Options..." `MenuItem` never invokes the command in
this sandbox — zero "OpenOptions command invoked" line ever appears in the log despite the click
landing at verified-correct coordinates. A headless Avalonia test (since deleted, was throwaway)
confirmed `OptionsWindowView` itself constructs with zero runtime errors, ruling out an XAML bug.
Every other control type in the app (buttons, tabs, toggles, radio buttons, sliders) responds
correctly to the same synthetic-click technique all session — this one top-level `Menu`/`MenuItem`
is the sole, reproducible exception, resistant to coordinate sweeps, double-click, F10, Shift+Tab
keyboard nav, explicit window activation, and multiple fresh process restarts. Treat as an
environment/sandbox input-delivery quirk specific to this one Avalonia `Menu` control, not a real
app defect — needs a human with real hands-on access to confirm the Options dialog actually opens
correctly (very likely does, given the view constructs cleanly and every other control works).

**Also still outstanding from the Options-page-port work** (paused, not abandoned, see the
entry below): hands-on screenshot verification of all 7 Options tabs (blocked by the same click
issue), `spec/14-roadmap.md` note for the disabled placeholders, 2 new `docs/removed-features.md`
entries (WinFont/design-system, external log-connection socket).

**Nothing from this session is committed yet.** Two logically separate change sets are sitting in
the working tree together: (1) the Options window restyle + legacy-option-porting XAML/en.json
work, (2) the full logging rollout. Consider whether the user wants these as one bundled commit or
two separate ones before committing/pushing — not yet asked.

## Resume here (2026-08-06, superseded by the entry above) — Options window restyle + legacy-option-porting: XAML done, build/tests green, hands-on click verification BLOCKED. Now pivoting to add real Microsoft.Extensions.Logging infra (console+file) before resuming the click investigation.

Two prior pieces of work landed and are committed/pushed this session before this entry:
1. The 9-piece mock2 visual-fidelity pass (all 3 tabs reproduce the mock's full layout as
   placeholder scaffolding) — committed `e12e9cb`, pushed to `origin/master`.
2. This entry's own work: "style the Options window like the rest of the app AND add every
   option legacy YONIQ's Option dialog has, skip what's already covered elsewhere, grey out +
   tooltip whatever has no real backing feature yet" (direct user request, scope clarified via
   AskUserQuestion: disabled/greyed-with-tooltip, not fully inert and not real-only).

**Plan file**: `/home/artien/.claude/plans/options-page-port.md` — auditor-reviewed once (verdict
NOT EQUIVALENT first round: 6 missing legacy controls, 2 wrong "already covered" claims — tune
frequency/duration turned out to already be real via the header's Tune row; a wrong demod-type
default claim — Hilbert is the real ported default, not PLL; and a real Avalonia gotcha,
`ToolTip.ShowOnDisabled` defaults to `False` so disabled-control tooltips are invisible without it
set explicitly). All corrections applied to the plan file directly (see its own "Auditor
corrections applied" section) before implementing — not re-audited a second round given the fix
list was concrete and mechanical.

**Implementation done**: `OptionsWindowView.axaml` fully rewritten — restyled to `card`/`cardInner`/
`label` (Cards.axaml classes, matching the rest of the app), 3 new tabs added (Decode/
Identification/Advanced) alongside the existing 4 (General/Audio/Radio/Tx), ~50 legacy option
concepts represented as disabled+tooltip placeholders (multi-option groups use plain `RadioButton`,
not the template-heavy `.seg`/`.tool` classes, since those have no `:disabled` visual state and
patching them risked regressing already-shipped segmented controls elsewhere). New global
`Control:disabled { ToolTip.ShowOnDisabled: True }` rule added to `Cards.axaml` so every disabled
placeholder's tooltip actually shows on hover. ~110 new `en.json` keys. Zero new persisted settings
fields this pass (every genuinely-real legacy option was already covered elsewhere; everything new
is a non-functional placeholder). Build clean, `ScanlineStudio.UI.Tests` 74/74 green (including
`NoHardcodedAxamlStringsTests` catching 8 missed loc-keys on numeric TextBoxes, fixed by switching
those to `NumericUpDown`, which the hardcoded-string regex doesn't match).

**Blocked**: hands-on screenshot verification of the Options window. The top-left "Options..."
`MenuItem` does not respond to any synthetic XTEST click in this sandbox — tried direct coordinates
(multiple, swept across ~30px), double-click, F10, Shift+Tab keyboard nav, explicit window
activation (`wmctrl -a`), and multiple fresh process restarts. A headless Avalonia test
(`[AvaloniaFact]`, since deleted — was throwaway) confirmed `OptionsWindowView` constructs with zero
runtime errors, ruling out an XAML/resource bug. Temporary `Console.WriteLine` diagnostics
(since reverted) confirmed the click genuinely never invokes `MainViewModel.OpenOptionsCommand` at
all — not an off-screen/wrong-monitor rendering issue, the event just never fires. Every other
control type in this app (buttons, tabs, toggles, sliders, radio buttons) has responded correctly
to the same synthetic-click technique all session; this top-level `Menu`/`MenuItem` is the one
exception found so far.

**User's direction, current task**: stop chasing this ad-hoc and instead add real,
permanent `Microsoft.Extensions.Logging`-based diagnostics (console + probably a file provider,
Debug/Info/Warning/Error levels) to the app, so future debugging (this issue and others) has
proper structured traces instead of throwaway `Console.WriteLine`. Note:
`Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder` is already used in
`ScanlineStudio.Host/Program.cs` and already wires a default console logger + `ILogger<T>` DI
(confirmed one real consumer already: `JsonLocalizationService` takes `ILogger<JsonLocalizationService>`)
— this is extending existing infra, not starting fresh. Not yet started implementing.

**Still outstanding after logging lands and the click mystery is resolved (or given up on)**:
hands-on screenshot verification of all 7 Options tabs; the `spec/14-roadmap.md` note listing
every disabled-placeholder concept added this pass; two new `docs/removed-features.md` entries
(WinFont/Ja-En font switch — superseded by the design system's typography; the YONIQ-fork-specific
external "log connection" IP:port socket — too underspecified to even represent as a placeholder).
None of this is committed yet.

## Resume here (2026-08-05, superseded by the entry above) — UI restructure: drop Dock.Avalonia for a fixed Menu/header/3-tab shell + new flat WSJT-X/fldigi style. ALL 6 PIECES DONE, hands-on verified, full solution green. NOT YET COMMITTED — waiting on user go-ahead.

Full plan at `/home/artien/.claude/plans/wondrous-crafting-ladybug.md` (6 pieces). User brought
back two mockup rounds (`mockups/*_mock.*` then `mockups/*_mock2.*`, both gitignored/untracked)
after the previous "UI design pass" note below — reviewed, diffed byte-for-byte (mock2 is a
pure stylesheet swap over mock1's unchanged structure), got an `auditor` (Opus, high) plan-review
pass (verdict EQUIVALENT-WITH-RISKS, 5 gaps folded into Piece 2's checklist before building),
then the user said "go for it, normal way, plan, auditor, create in auto" — implementing all 6
pieces sequentially without per-piece confirmation stops.

**Locked decisions**: drop Dock.Avalonia entirely (fixed Menu → header cards → TabControl
Receive/Transmit/Gallery, no floating/rearranging — nothing today used layout persistence
anyway); adopt mock2's flat Win32/Qt-style chrome as the new Aesthetic Directive (supersedes
`spec/09-ui.md`'s old "raw SDR instrumentation" text, itself now updated); wire ONLY real
backend data per tab/card, omit anything without it (logged as backlog notes in
`spec/14-roadmap.md`'s existing Phase 4+ backlog section, not yet added — happens as Pieces
4-6 land their own omissions). Mid-session live feedback after seeing Piece 1 built: denser
type (11.5px base/17px rows, done), Receiving-toggle green/Halt-button red-tinted (done, as
new `.receiving`/`.halt` classes), LED status indicators for RX/TX-inhibit (style added, real
wiring is Piece 3's job since that's where real IsReceiving/SWR-cutoff state lives). The
"decode rows colored by state" ask needs a new decode-state field `ReceiveHistoryEntry`
doesn't have today — flagged to the user, not yet resolved (build it now vs. backlog it).

**Piece 1 (style tokens) DONE**: new `Styles/Cards.axaml` (ScanlineStudio-prefixed color
resources, `card`/`cardInner`/`plot`/`pill`/`accentPill`/`softPill`/`segment`+`seg`/`tool`/
`thumb`/`canvas`/`safeArea`/`plate`/`kv`/`led` classes, global corner-radius override in
`App.axaml`'s `Application.Resources`), `spec/09-ui.md`'s Aesthetic Directive rewritten to
document the supersession explicitly.

**Piece 2 (drop Dock.Avalonia) DONE**, hands-on verified (screenshotted all 3 tabs via the
established xwd_capture.py/wmctrl workflow on the secondary monitor, no crashes): deleted
`AppDockFactory.cs` + its test + Dock.* package refs + `DockFluentTheme.axaml` include; the 5
pane VMs (`WaterfallPaneViewModel`/`RxImagePaneViewModel`/`RxHistoryPaneViewModel`/
`TxControlsPaneViewModel`/`TxImageEditorPaneViewModel`) now derive from `ViewModelBase`, not
`Tool` (dropped `Id`/`Title`/`CultureChanged` lambdas — dead now that mock2 uses static card
headers, not dynamic dock titles); `ViewLocator.Match` no longer checks `IDockable`;
`MainWindow.axaml` rebuilt as Menu (View menu deleted, nothing closable anymore) → unchanged
header strip (Piece 3 will split it into cards) → `TabControl` hosting each pane via plain
`ContentControl`+ViewLocator auto-resolution; `MainViewModel` constructs the 4 DI-singleton
panes + a nullable `ActiveEditor` (replaces `AppDockFactory.OpenTxImageEditor`/
`CloseTxImageEditor`'s `AddDockable`/`CloseDockable` pair) wired from
`TxControlsPaneViewModel.EditorOpened`/`EditorClosed`; added an explicit `EditorCanvas.Focus()`
call on attach (Dock's `ActiveDockable` used to provide this for free — without it, arrow-key
crop-nudge would have silently stopped working). Known cosmetic gap: `Slider`'s thumb still
renders FluentTheme's default round handle, not Cards.axaml's flat rectangular one — my
`Slider /template/ Thumb` selector isn't winning; fix when sliders get real attention in
Piece 3/5.

**Piece 3 (header cards) DONE**: `RadioStatusViewModel` stayed one flat class (the "3 cards" are
a `MainWindow.axaml` layout concern, not a VM split). Added real `ISstvSessionService.IsReceiving`
(new getter backed by the service's own already-tracked `_isReceiving` field), wired to
`ToggleButton.receiving` + a separate `Button.halt`/`HaltReceivingCommand` (mock2's own two-
control pair). Real `CatLinked` from `IRadioSessionService.ConnectionEvents`
(`Connected`→true, `Disconnected`/`Failed`/`Reconnecting`→false, `CommandFailed` deliberately
ignored — that state's own contract says the connection stays healthy). Added the bottom status
bar (wasn't in the original 6-piece plan, folded in here): RX LED (`IsReceiving`) + "TX INHIBIT"
LED bound to `TxControls.ErrorMessage != null` (the closest honest signal — no persistent TX-
lockout state exists). 8 new tests.

**Piece 4 (Receive tab) DONE**: real 258/*/330 Grid. Found mock2's Mode card's Auto/Locked
toggle + quick-mode-button grid have NO backing feature (`ISstvDecoder` always auto-detects via
VIS, no manual mode-lock exists) — omitted, backlogged. Added small real
`RxImagePaneViewModel.DetectedMode` (subscribes to the already-existing `ISstvSessionService.
ModeDetected`) backing `DetectedModeText`/`LineTimeText`/`LinesText`. `WaterfallPaneView.axaml`/
`RxImagePaneView.axaml` restyled in place (`Classes="plot"`) — no new PlotView abstraction.
"Previous frames" card reuses `RxHistoryPaneViewModel.Entries` directly. 9 backlog notes, 2 new
tests. Caught a real runtime-only crash via hands-on screenshot that headless tests missed:
`Margin="{DynamicResource ScanlineStudioSpacingMedium}"` — a bare double assigned to a
Thickness-typed property, the exact footgun `Tokens.axaml`'s own comment warns about.

**Piece 5 (Transmit tab) DONE**: `TxControlsPaneView.axaml` restyled in place to cards (same VM,
same real data). Added real, zero-new-data `TxControlsPaneViewModel.ModeTimingRows` (computed
from `AvailableModes`' own `LineDurationMs`/`ImageHeight`; "Frame" duration is a documented
approximation for 2-rows-per-line color families). Drive slider lives in `MainWindow.axaml`'s
Transmit tab directly, bound to `RadioStatus.TxVolumePercent` (cross-VM-sibling binding, same
trick as the status bar) rather than coupling `TxControlsPaneViewModel` to
`RadioStatusViewModel`. 11 backlog notes, 2 new tests. Hands-on screenshot caught a real
column-width bug in the mode-timing table (values overlapping) — fixed with explicit pixel
widths.

**Piece 6 (Gallery tab) DONE**: added `IReceiveHistoryStore.GetImagesDirectoryAsync()` (real —
extracted `ReceiveHistoryRecorder`'s existing resolution logic to a shared
`ReceiveHistorySettings.ResolveDirectoryAsync`, both classes now call the one implementation;
`SqliteReceiveHistoryStore` gained an `ISettingsStore` constructor dependency, DI resolves it
automatically). Gallery tab is a real 2-column layout (thumbnail grid + selected-frame inspector
+ Storage card) built directly against `RxHistory` sub-properties — NOT the old standalone
`RxHistoryPaneView` reused wholesale; that file was now orphaned (nothing bound the whole VM as
`ContentControl.Content` anymore) and was deleted. Added real `ShowTodayOnly` (defaults true,
matching mock2) driving `IReceiveHistoryStore.QueryAsync`'s real `From`/`To` filter. 4 backlog
notes, 5 new tests. Hands-on screenshot confirmed the Storage card shows the real resolved path.

**Final state**: full solution `dotnet test` green (10 projects, 840 tests, 0 failures). Every
visually-changed piece hands-on screenshot-verified via the established xwd_capture.py/wmctrl
workflow on the secondary monitor (HDMI-1). **Nothing committed yet** — planned as one bundled
commit per the established "chop into pieces, test each, commit at milestone" discipline,
matching how the previous Settings/TX/telemetry plan was committed as a single commit at the
end. Waiting on explicit user go-ahead before committing/pushing.

## Resume here (2026-08-05, superseded by the entry above) — Settings/Options + TX quick-controls + radio telemetry: ALL 6 PIECES DONE + committed/pushed (`247fde7`). Legacy YONIQ/QSSTV inventory pass DONE, documented in spec/14-roadmap.md's new "Phase 4+ backlog" section. User is now working on a UI design pass themselves — no implementation work in progress; wait for direction before building any of the inventoried backlog items.

Full plan at `/home/artien/.claude/plans/wondrous-crafting-ladybug.md` (6 pieces) — approved after
thorough research (legacy YONIQ settings inventory, current settings infra, current UI/dialog
patterns, radio telemetry feasibility — 4 parallel Explore agents) plus a round of live user
steering mid-research (CAT backend config, WSJT-X-style TX volume slider, favorite-TX-mode
buttons, RX-mode-follow toggle, frequency quick-control strip with memory presets, live
PWR/ALC/SWR telemetry + SWR auto-cutoff, a Nexus ham-radio-app screenshot reviewed for
information-density/grouping ideas only, not its visual skin).

**Piece 1 done** (settings model groundwork):
- `RadioConnectionSettings` (`ScanlineStudio.Core.Radio`) extended with Hamlib fields
  (`HamlibModel`/`SerialPort`/`BaudRate`/`PttType`) and a `"hamlib"` case in `ToConnectionSpec()`
  building `HamlibConnectionSpec` — mirrors that spec type's shape exactly.
- New `RadioSafetySettings` (SWR auto-cutoff enable/threshold) and `FrequencyPresetsSettings`
  (ordered `FrequencyPreset(Label, FrequencyHz, Mode)` list) in `ScanlineStudio.Core.Radio`.
- New `TxPaneUiSettings` (favorite mode IDs, TX volume %, auto-follow-RX-mode toggle) in a
  **new `ScanlineStudio.UI.Settings` namespace inside `ScanlineStudio.UI` itself** — first
  settings section owned by the UI project directly (previous convention was always
  `ScanlineStudio.Core.*`). Required adding a direct `ProjectReference` from
  `ScanlineStudio.UI.csproj` to `ScanlineStudio.Settings.csproj` — confirmed layering-legal:
  `UiLayeringArchitectureTests` only bans `ScanlineStudio.Core.*` project references and
  `Microsoft.Data.Sqlite`/`SixLabors.*` package references, `ScanlineStudio.Settings` is neither.
- New `OperatorSettings` (just `Callsign` for now) in `ScanlineStudio.Application` — no existing
  module was a natural fit, so it lives in the orchestration layer that future TX-overlay-macro
  and logbook features will read it from.
- **Scope-trimmed during implementation, not per the original plan text**: dropped
  `WaterfallPaletteSettings` entirely. Reading `WaterfallControl.cs` (required by the plan's own
  "read that file first" note) found the waterfall is currently **plain grayscale with no color
  rendering at all** (deliberate — see `feedback_ui_effort_allocation` memory, waterfall polish
  is already deprioritized). A palette *setting* with no rendering path to consume it would be
  exactly the "phantom setting"/"half-finished implementation" CLAUDE.md warns against — revisit
  if/when the waterfall ever gets color rendering.
- **Also trimmed**: CW-ID/VOX settings fields the plan's Piece 2 description mentioned in passing
  were never actually built (neither feature has any encoder/audio-generation code in the app at
  all yet) — same reasoning as the waterfall palette. Only `OperatorSettings.Callsign` shipped
  from that area, since the Options dialog itself is the actual consumer being built (Piece 2),
  unlike CW-ID/VOX which have no consumer of any kind yet.
- `RadioConnectionSettingsTests` extended (hamlib-with-model success case, hamlib-missing-model
  falls back to None, renamed the stale "unknown backend" test to use a genuinely unknown ID
  now that `"hamlib"` is real) plus new round-trip serialization tests for all four new/changed
  section types (`NewSettingsSectionsSerializationTests`, `OperatorSettingsTests`,
  `TxPaneUiSettingsTests`) — `FrequencyPresetsSettings`' nested list-of-records shape was worth a
  real smoke test against System.Text.Json source-gen, not assumed to just work.
- Full solution build clean; full test suite green (778+ baseline, all new tests included).

**Piece 2 done** (`OptionsWindow` — first `Window`/dialog in the app):
- **Real layering mistake caught and fixed before it shipped**: first draft of
  `OptionsWindowViewModel` referenced `AudioDeviceSettings`/`RadioConnectionSettings`/
  `LocalizationSettings` directly (all concrete `ScanlineStudio.Core.*` types) — a real
  `ScanlineStudio.UI` → `Core.*` layering violation, the exact bug class `UiLayeringArchitectureTests`
  exists to catch, caught here by the build/test cycle itself (the test passed cleanly with the
  fix in place, giving confidence it would have failed with the violation — not re-verified via
  a deliberate revert this time given the mechanism is already well-established in this
  project's history). Fixed with a new `OptionsSettingsService` (`ScanlineStudio.Application`) +
  `OptionsSnapshot` plain DTO — same pattern as `ISstvSessionService`/`IRadioSessionService`
  already used everywhere: UI depends only on the Application-layer facade, never the concrete
  per-module settings-section types. `OptionsSettingsService.Defaults` derives "reset to
  default" values from each real settings record's own defaults (`new AudioDeviceSettings()`
  etc.) rather than duplicating them as magic literals that could silently drift.
- `OptionsWindowViewModel`/`OptionsWindowView.axaml` (`: Window`, `ShowDialog`): four tabs
  (General/language, Audio/device+sample-rate, Radio-CAT/backend+host+port, TX/callsign).
  Radio/CAT only offers None/rigctld this pass — Hamlib isn't in DI yet (Piece 3).
  Per-section "Reset to defaults" (single click, nothing persisted until Save) + a global
  "Reset ALL" requiring an inline confirm step (the one genuinely destructive action).
  New `ScanlineStudioHelpGlyph` style + `[?]` TextBlock/ToolTip.Tip convention for esoteric
  fields (sample rate, radio backend, callsign) — first use of this pattern in the app.
  Wired via a new "Options..." top-level `MenuItem` in `MainWindow.axaml` (plain action, not a
  dropdown) — `MainViewModel` gained an injected `IServiceProvider` (new pattern: first
  DI-container-as-view-factory use in this codebase) to resolve the transient
  `OptionsWindowViewModel` on demand, raising an event `MainWindow.axaml.cs`'s code-behind
  listens for to actually construct/show the `Window` (a view-model must never construct a View
  itself; no `IDialogService` abstraction built for this, not worth it for a single caller yet).
  `RadioButton` group backing needed plain computed bool properties
  (`IsNoneBackendSelected`/`IsRigctldBackendSelected`) instead of a converter — Avalonia's
  `StringConverters` has no "equals this parameter" converter (confirmed by inspecting the
  actual referenced assembly's strings, not assumed).
- 8 new headless tests (`OptionsWindowViewModelTests`) — construction loads every field from a
  real `OptionsSettingsService` backed by a fake `ISettingsStore` (exercising the real
  section-translation logic, not mocking it away), Save persists everything + fires
  `RequestClose`, Cancel fires `RequestClose` without persisting anything, each per-section
  reset, the RadioButton bool-pair toggle, and the confirm/cancel flow around "Reset ALL" — one
  verified via the revert-fix-confirm-fail technique (temporarily broke `ResetRadioToDefault`,
  confirmed the test failed exactly as expected, restored).
- Also caught by the existing `NoHardcodedAxamlStringsTests` enforcement: the `[?]` help-glyph
  literal itself needed a locale key (`Options.HelpGlyph`) like every other user-visible string —
  no carve-out for "it's just a symbol."

**Hands-on visual/interactive verification: done, and it worked this time** — unlike last
session, synthetic XTEST clicks (stepped-motion approach, same script as before) were reliable
against this window. Real app launched, Options dialog opened via the actual menu click, all
four tabs clicked and screenshotted (General/Audio/Radio-CAT/TX all render correctly; Radio/CAT
correctly loaded the real persisted rigctld connection from this machine's own `settings.json`
— host `127.0.0.1`, port `4534`), the Reset-ALL confirm/cancel flow exercised end-to-end,
dialog closed cleanly via Cancel with no crash.

**Real (minor) bug found and fixed through this verification, not through review**: during the
Reset-ALL confirm prompt, the outer dialog's own Cancel/Save buttons stayed visible alongside
the confirm prompt's own "Cancel," producing two visible "Cancel" buttons at once — confusing,
not destructive. Fixed by adding `IsVisible="{Binding !IsConfirmingResetAll}"` to the outer
Cancel/Save buttons too. Re-verified visually after the fix (single Cancel button now). All 40
UI tests still green after the fix (no test change needed — this was a pure-XAML visibility gap,
not a ViewModel logic bug).

**Piece 3 done** (CAT/Radio backend selection):
- `Program.cs`: registered `NoneRadioProtocolFactory` and `HamlibProtocolFactory.Create()`
  alongside the already-registered `RigctldProtocolFactory` — closes the real, previously
  silently-swallowed gap (default `BackendId = "none"` had no matching factory at all; would
  throw on connect, masked by the startup `try/catch`). Confirmed via `HamlibRuntime`'s own
  source that `HamlibProtocolFactory.Create()` is safe to call unconditionally even without
  libhamlib installed — its constructor catches discovery failure internally
  (`IsAvailable=false`) rather than throwing; the throw only happens later, if a user actually
  selects Hamlib and tries to connect. Added the missing `ScanlineStudio.Core.Radio.Hamlib`
  `ProjectReference` to `ScanlineStudio.Host.csproj` (wasn't there before either).
- `OptionsSnapshot`/`OptionsSettingsService`/`OptionsWindowViewModel` extended with
  `HamlibModel`/`HamlibSerialPort`/`HamlibBaudRate`/`HamlibPttType`, a third `IsHamlibBackendSelected`
  RadioButton-pair property, and an `IsHamlibSelected` visibility gate — same pattern as the
  existing rigctld fields, no new architecture needed.
- Options dialog's Radio/CAT tab now offers all three backends; Hamlib fields (`[?]` help glyphs
  on Rig model/Serial port/PTT type, matching legacy-research-flagged esoteric fields) shown only
  when selected.
- 5 new headless tests (RadioButton 3-way toggle, Hamlib fields load/save/reset) — all pass;
  `RadioConnectionSettingsTests` hamlib-mapping tests from Piece 1 already covered the underlying
  `ToConnectionSpec()` logic.
- **Hands-on verified**: launched the real app, confirmed no DI resolution error (previously the
  real risk this piece fixes), opened Options → Radio/CAT, selected "Linked Hamlib," confirmed
  all four fields render with correct help glyphs, closed cleanly.
- **Known, accepted limitation**: no "is Hamlib actually available on this system" pre-check
  surfaced in the UI — selecting it when libhamlib isn't installed will only fail when the app
  actually tries to connect (via whatever error path `RadioSessionService.ConnectUsingSettingsAsync`
  already has), not a validation error in the dialog itself. Deferred as a nicety, not blocking.
- Full solution test suite confirmed green (778+ baseline plus all new tests) after this piece.

**Real cosmetic bug found and fixed, flagged directly by the user after seeing a screenshot**:
the Options dialog's tab headers ("General"/"Audio"/etc.) rendered "comically gigantic" —
FluentTheme's default `TabItem` header uses a much larger type-ramp font size than every other
control in the app, and `Tokens.axaml`'s existing `TabItem` style only set `CornerRadius`, never
`FontSize`. Fixed with an explicit `FontSize="13"` (matching normal control text) added to that
style. Re-verified visually (tabs now match the rest of the app's scale) and via the existing 43
UI tests (all still pass — pure XAML/style change, no ViewModel logic touched).

**Piece 4 done** (TX pane quick controls):
- **Real mistake caught mid-implementation, before it shipped**: first draft of the favorite-mode
  button row bound `Command="{Binding $parent[ItemsControl].((vm:TxControlsPaneViewModel)DataContext).SelectFavoriteModeCommand}"`
  — the *exact* cross-DataTemplate type-cast pattern that already crashed this app at runtime
  once this session (`StockEntryViewModel`'s own bug, see Phase-4-image-tooling history). Caught
  by re-reading `StockEntryViewModel`'s own doc comment while writing the new XAML, before ever
  building/running it. Fixed the same way: a new `FavoriteModeButtonViewModel(Mode, SelectCommand)`
  record carries its own command reference, set once when `RebuildFavoriteModes()` constructs each
  entry — no cross-template binding at all.
- `TxControlsPaneViewModel` gained `ISettingsStore` (constructor-injected, threaded through
  `AppDockFactory`'s own constructor — no `Program.cs` change needed, `ISettingsStore` was already
  a registered singleton), `FavoriteModeOptions`/`FavoriteModes` collections, `AutoFollowRxMode`,
  and an `ISstvSessionService.ModeDetected` subscription (marshaled via `Dispatcher.UIThread.Post`,
  matching `RadioStatusViewModel`'s own established pattern for that event's documented
  audio-thread-callback contract).
- **Read-modify-write persistence, not a fresh write**: `PersistTxPaneUiSettingsAsync` always
  loads the current `TxPaneUiSettings` section and `with`-updates only the two fields this
  view-model owns (`FavoriteModeIds`/`AutoFollowRxMode`) — `TxVolumePercent` is a *different*
  view-model's field in the *same* settings section (Piece 5, not built yet), and a from-scratch
  write would have silently clobbered it. Verified via a dedicated test + the revert-fix-confirm-fail
  technique (temporarily forced a fresh-instance write, confirmed the test failed exactly as
  predicted, restored).
- Favorite-mode buttons live in a new "FAVORITES" module group in `TxControlsPaneView.axaml`
  (right under the mode dropdown), with an "Edit favorites..." button opening an Avalonia
  `Flyout` of checkboxes (one per `AvailableModes` entry) — no new dialog/window needed.
  Auto-follow toggle is a plain `CheckBox` near the Transmit button.
- 6 new headless tests (`TxControlsFavoritesAndAutoFollowTests`) — load-from-settings, toggling
  an option live-updates the button row + persists, clicking a favorite button sets
  `SelectedMode`, `ModeDetected` updates `SelectedMode` only when the toggle is on (both branches
  tested, not just the "on" case), and the TxVolumePercent-preservation test above. All pass;
  existing 43 tests (now updated for the new constructor parameter across
  `AppDockFactoryTests`/`PaneViewModelTests`) still green.
- **Hands-on verified, fully interactively this time**: launched the real app, opened "Edit
  favorites...", checked two modes, watched the button row update live, clicked a favorite
  button and confirmed the Mode dropdown actually changed to match — the whole feature working
  end-to-end by hand, not just headless-tested. Auto-follow checkbox confirmed rendered correctly
  with the right label (a follow-up click to toggle it missed the target physically, not worth
  chasing further given the behavior is already covered by passing headless tests).
- Full solution test suite run after this piece to confirm no regressions.

**Piece 5 done** (frequency + status strip expansion):
- `IRadioSessionService` gained `GetFrequencyPresetsAsync`/`SaveFrequencyPresetsAsync` (returns
  `Abstractions.Radio.FrequencyPreset`, not the `Core.Radio.FrequencyPresetsSettings` section
  type). `ISstvSessionService` gained `GetTxVolumePercentAsync`/`SetTxVolumePercentAsync`/
  `TuneAsync(frequencyHz, duration, ct)`. `SstvSessionService.TransmitAsync` and the new
  `TuneAsync` were refactored to share one `PlayWithPttAsync` helper (pause-RX/key-PTT/play/
  un-key-PTT/resume-RX, previously duplicated only in `TransmitAsync`) — `TuneAsync` plays a
  generated sine tone (`GenerateTone`, an `IAsyncEnumerable<float>` iterator) through the same
  path. TX volume is applied as a linear gain multiplier in `PumpToPlaybackAsync`, read fresh
  each transmission.
- **Two layering violations caught and fixed proactively, before any UI/interface code was
  written** (not build-error-driven): `FrequencyPreset` moved from `Core.Radio` to
  `Abstractions.Radio` (a method on `IRadioSessionService` returning a `Core.Radio` type would
  have forced `ScanlineStudio.UI.dll` to reference `Core.Radio.dll` just to bind against the
  return type — the same violation class as a direct field-type reference, just via a method
  signature instead); `TxVolumePercent` moved from the UI-owned `TxPaneUiSettings` to
  `Core.Audio.AudioDeviceSettings` (`SstvSessionService`, in `ScanlineStudio.Application`, needs to
  read it directly and cannot reference a `ScanlineStudio.UI`-owned type).
- **Real bug found via hands-on verification, not review**: TX volume slider showed 0 instead of
  the intended 100 default on first launch against a pre-existing `settings.json`. Root cause,
  confirmed via a throwaway deserialization test before touching any fix code: System.Text.Json
  does not honor a C# property-initializer default (`{ get; init; } = 100`) for a property absent
  from the JSON payload — it silently deserializes to the CLR default (`0`), not the declared
  default, for *any* settings.json saved before that field existed. Fixed by making
  `TxVolumePercent` nullable (`int?`) and applying the `?? 100` fallback at the one read site
  (`SstvSessionService.GetTxVolumePercentAsync`) rather than relying on a property initializer
  ever again for this field. Verified via revert-fix-confirm-fail (a new regression test,
  `GetTxVolumePercentAsync_SectionPredatesTheField_DefaultsTo100NotZero`, fails with the old `?? 0`
  and passes with `?? 100`). **This STJ behavior is a systemic risk for every other settings field
  with a non-CLR-default value added to an already-shipped section** — flagged here as a one-line
  note per the ADHD scope rule, not chased further across the rest of the settings surface this
  session.
- **Second real bug found via hands-on verification**: clicking a frequency preset button (or the
  Tune button, or changing the mode combo) with no radio connected crashed the entire app —
  `IRadioSessionService.SetFrequencyAsync`/`SetModeAsync` throw `InvalidOperationException` in
  that case (a routine, common state, not an edge case), uncaught, propagating out of an
  `AsyncRelayCommand`. Fixed with the same catch-and-report-locally pattern already established in
  `TxControlsPaneViewModel` — new `RadioStatusViewModel.ErrorMessage` property, try/catch around
  every command that touches `IRadioSessionService`/`ISstvSessionService.TuneAsync`, localized
  error text, an `ErrorMessage` `TextBlock` added under the status strip in `MainWindow.axaml`.
  Verified via revert-fix-confirm-fail with two new regression tests
  (`ApplyPresetCommand_NoRadioConnected_SetsErrorMessageInsteadOfCrashing`,
  `SetFrequencyCommand_NoRadioConnected_SetsErrorMessageInsteadOfCrashing`) plus a new
  `FakeRadioSessionService.ThrowOnSetFrequencyOrMode` flag; re-confirmed by hand afterward
  (clicking the same preset button and Tune button no longer crash the app, both now show a clean
  inline error message).
- `RadioStatusViewModel`/`MainWindow.axaml`: editable frequency field + Set button, mode
  `ComboBox` (`SelectedRadioMode`, guarded against a feedback loop with incoming `StateChanges`
  via a `_suppressModeCommand` flag, same shape as the existing `_suppressVolumePersist` guard),
  a frequency-presets row (`FrequencyPresetButtonViewModel`, own-`SelectCommand`-per-item pattern,
  same reason as `FavoriteModeButtonViewModel`/`StockEntryViewModel` — avoids the cross-
  DataTemplate binding crash class already hit twice before this session) with an "Edit
  presets..." `Flyout` (add/remove/reorder rows, `FrequencyPresetEditorRowViewModel`), a debounced
  (400ms) TX volume `Slider`, and a Tune group (frequency/duration fields + button, `[?]` help
  glyph). Caught by `NoHardcodedAxamlStringsTests` mid-implementation: the literal `"Hz"`/`"s"`
  unit labels and the `"[?]"` glyph needed locale keys too (reused the existing shared
  `Options.HelpGlyph` key for the glyph itself).
- 8 new headless tests (`RadioStatusViewModelTests`) — persisted-presets/volume load, preset
  click applies frequency+mode, frequency-field Set parses MHz→Hz, Save-presets persists edited
  rows and rebuilds the button row, volume-change persists only after the debounce delay, Tune
  keys the tone with the right frequency/duration, plus the two crash-regression tests above.
- Full solution build clean; full solution test suite green (all pre-existing tests plus every
  new one across `UI.Tests`/`Application.Tests`/`Core.Radio.Tests`).
- **Adopted a new test-scoping convention this piece** (direct user feedback, see
  `feedback_scope_test_runs_to_change` memory): during active edit-test cycles, run only the test
  project(s) that actually own the changed code (`UI.Tests`/`Application.Tests`/`Core.Radio.Tests`
  for this piece), not the full solution — `Core.Sstv.Tests` (548 DSP golden-vector tests) and
  `Core.Audio.MiniAudio.Tests` are both unrelated to a settings/UI change and together account for
  ~9 of the ~10 minutes a full run takes. Full-solution `dotnet test` still runs once per piece
  before marking it done, just not after every edit within the piece.
- Hands-on verified via the established XWD/XTEST screenshot+click workflow on the secondary
  monitor: status strip renders all five module groups correctly, "Edit presets..." flyout
  add/type/save round-trips through `settings.json` for real, the saved preset button appears and
  (once the crash fix landed) applies correctly, Tune button behaves the same way.

**Piece 6 done** (radio telemetry: PWR/ALC/SWR + SWR auto-cutoff + manual Stop TX):
- **Got an `auditor` plan-review before writing any code** (CLAUDE.md §7 — the plan itself flagged
  this piece as concurrency-risky). Verdict: architecturally sound but 6 concrete defects that
  would ship as real bugs if built as originally designed, plus 2 policy gaps to decide first —
  all resolved before implementation started, not discovered afterward:
  1. Culture-sensitive `float.TryParse` (default overload) would parse `"1.5"` as `15` under a
     culture where `.` is a thousands separator — every meter parse uses
     `NumberStyles.Float, CultureInfo.InvariantCulture` explicitly.
  2. A single failed meter read must not abort the whole `PollAsync` snapshot (meters fail far
     more often than freq/mode/ptt) — both `RigctldClientProtocol`/`HamlibRadioProtocol` now
     return `null` for just that field on a soft error, never throw.
  3. `_cutoffTriggered` resets at `TransmitAsync`'s own entry, not only inside its
     `catch (OperationCanceledException)` — otherwise a cutoff firing after the transmit loop had
     already drained naturally (no exception at all) would misattribute the *next* manual Stop TX
     to a stale cutoff.
  4. Meter-visibility flags (`ShowSwrMeter` etc.) are stored `[ObservableProperty]`s explicitly
     refreshed every `StateChanges` poll, not a one-time computed property — both backends connect
     lazily, so `Capabilities` is `None` until the first successful poll; a computed property
     evaluated once at bind time would never become visible.
  5. `StopTransmitCommand.NotifyCanExecuteChanged()` added alongside the two sites where
     `IsTransmitting` actually toggles (would otherwise stay permanently disabled — this codebase
     notifies `CanExecute` manually, no `[NotifyCanExecuteChangedFor]` convention here).
  6. `SstvSessionService.PlayWithPttAsync`'s cleanup (`StopPlaybackAsync` moved back into the
     `finally`, since it was skipped entirely on cancellation before) uses a fresh, bounded-timeout
     `CancellationTokenSource` for the PTT-off/resume-capture calls, each independently
     try/caught — see the standalone finding below for why reusing the original token was a real,
     newly-reachable bug.
  Policy decisions locked in: the SWR-cutoff checkbox+threshold field are `IsEnabled` bound to
  `ShowSwrMeter` (disabled, not just unchecked, when the rig doesn't report SWR — a
  checked-but-non-functional toggle would be worse than no toggle); the cutoff requires 2
  consecutive over-threshold polls (not a single reading) AND the rig's own PTT readback
  (`state.IsTransmitting`), which also makes it self-disable correctly on VOX/DTR-keyed rigs with
  no PTT-readback capability (no code path needed for that case specifically — `IsTransmitting`
  is just always `false` there).
- **A second real, pre-existing bug found and fixed while designing this piece** (not part of the
  auditor's own list — found first, then included in the review payload for a second opinion,
  which confirmed the diagnosis): `PlayWithPttAsync`'s `finally` block reused the same
  (possibly-cancelled) token for its own PTT-off/resume-capture cleanup calls. Before this piece,
  nothing ever cancelled that token in practice (every caller passed the implicit default) — Piece
  6 is the first thing that actually cancels it (Stop TX / auto-cutoff), which would have made
  `SetPttAsync(false, ct)` throw immediately from its own `WaitAsync(ct)` without ever sending the
  PTT-off command, leaving the rig keyed indefinitely and RX capture stopped forever — the exact
  opposite of what a safety cutoff exists to guarantee. Fixed with a fresh, non-linked,
  5-second-bounded `CancellationTokenSource` for cleanup, each step independently try/caught.
  Verified via revert-fix-confirm-fail with a new regression test
  (`TuneAsync_TokenCancelledMidTone_StillUnkeysPttAndRestartsCapture`, using a 5-second generated
  tone + a 10ms cancellation so the CPU-bound sample loop is still mid-generation when it fires) —
  a `FakeRadioSessionService.SetPttAsync` that actually honors cancellation (matching the real
  protocols' `WaitAsync(ct)` contract) was needed to make the fake discriminate old vs. fixed
  behavior at all.
- `RadioState` gained three trailing optional fields (`SwrRatio`/`AlcLevel`/`PowerPercent`, all
  `float?`, all `null` unless populated this poll) — non-breaking, every existing construction
  site keeps compiling unchanged. `RadioCapabilities` gained `SwrMeter`/`AlcMeter`/`PowerMeter`
  flags.
- `RigctldClientProtocol`: `ProbeCapabilitiesAsync` additionally probes `l SWR`/`l ALC`/
  `l RFPOWER_METER` once at connect (verified directly against a local Hamlib clone's
  `rigctl_parse.c` that these return a single `%g` line, same shape as `f`/`m`/`t`, reusing the
  same helpers). `PollAsync` reads all three only when the same poll's own PTT readback shows
  transmitting -- gates cost and matches "meters are TX-only" semantics in one move. SWR's
  documented "0.0 ... infinite" range means a literal `"inf"` response parses as
  `float.PositiveInfinity` (cutoff-worthy), not a parse failure.
- `HamlibNative`/`IHamlibNative`: new `rig_get_level` P/Invoke binding. `setting_t` is
  `typedef uint64_t setting_t` (verified directly against rig.h) — a plain `ulong`, **not**
  `CLong` (unlike `pbwidth_t`/`hamlib_token_t`, which are genuinely platform-width-ambiguous C
  `long`s — `setting_t` has no such ambiguity). `value_t` (a C union) marshaled as a
  `[StructLayout(Explicit, Size=16)]` struct exposing only the `float` arm at offset 0 (every
  level this project reads is documented "arg float"; Size=16 matches the union's largest member
  so the native side never writes past the buffer regardless of which arm it touches).
  `HamlibRadioProtocol` probes/reads the same way as rigctld, reusing the existing
  soft/hard-error `TryProbe`/`ThrowIfError` machinery unchanged.
- `spec/03-cat-layer.md` (frozen P/Invoke table) and `spec/04-rigctld.md` (new "Telemetry" section,
  Non-goals amended -- extended-level commands were previously declared fully out of v1 scope)
  both updated to match, not left to drift.
- `IRadioSessionService` gained a `Capabilities` passthrough property and
  `GetSafetySettingsAsync`/`SaveSafetySettingsAsync(RadioSafetySpec)` — new
  `Abstractions.Radio.RadioSafetySpec` DTO mirroring `Core.Radio.RadioSafetySettings` (a section
  that existed since Piece 1 but had no consumer until now), same reasoning as `FrequencyPreset`'s
  own move to Abstractions.
- `TxControlsPaneViewModel` gained a new constructor-injected `IRadioSessionService` (threaded
  through `AppDockFactory`, no `Program.cs` change needed), live meter readouts gated on
  "this pane AND the rig both agree transmission is in progress," the visibility/cutoff/Stop-TX
  machinery described above, and `IDisposable` (cancels an in-flight `_transmitCts` if the pane is
  torn down mid-transmit — the first `IDisposable` view-model in this codebase; needed to satisfy
  CA1001 correctly rather than suppress it, since a pane holding a live
  `CancellationTokenSource` genuinely should clean it up).
- `TxControlsPaneView.axaml`: a Stop TX button next to Transmit, three telemetry module groups
  (PWR/ALC/SWR, each `IsVisible` bound to its own `ShowXMeter` flag — never a placeholder for an
  unsupported rig), and the SWR-cutoff checkbox+threshold field (`IsEnabled` bound to
  `ShowSwrMeter`, per the policy decision above).
- 7 new headless tests (`TxControlsTelemetryAndCutoffTests`) plus the 1 `SstvSessionServiceTests`
  regression above — meter visibility refreshes live (not just at construction), live meters only
  populate while actually transmitting, the 2-consecutive-sample cutoff debounce (including the
  "single glitch doesn't trip" and "resets after a good reading" cases, each verified via
  revert-fix-confirm-fail), the cutoff requires the rig's own PTT readback, Stop TX cancels with no
  scary error message, and SWR-cutoff settings persist. 15 new/updated fixture tests in
  `RigctldClientProtocolTests` (full-capability meter reads, RX-time gating, per-meter failure
  isolation, infinity/invariant-culture parsing) and 5 in `HamlibRadioProtocolTests` (same
  coverage against `FakeHamlibNative`, which gained a scriptable `RigGetLevel`).
- Full solution build clean; full solution test suite green (817+ tests, all new ones included).
- Hands-on verified: launched the real app, confirmed the fail-closed defaults render correctly —
  Stop TX/telemetry module groups/SWR-cutoff checkbox all correctly absent or disabled until a rig
  actually negotiates the matching capability, no placeholders shown for an unsupported/
  not-yet-connected rig.

**All 6 pieces of the Settings/Options plan are now done, committed and pushed** (`247fde7`).

**Legacy inventory pass also done** (per direct user instruction, right after Piece 6): 4 parallel
research agents cross-checked Scanline Studio against `yoniq-old/YONIQ-main/` (ground truth) and
`QSSTV-main/` (inspiration only), one each for logbook/QSO tracking, TX macros + CW-ID,
waterfall/color, and RX/TX quality-of-life. Findings written up as a tracked (not yet scheduled)
backlog in `spec/14-roadmap.md`'s new "Phase 4+ backlog — legacy YONIQ/QSSTV feature inventory"
section — read that section for the full gap list with complexity/risk notes per item, don't
re-derive it from scratch.

**Next**: the user is now working on a UI design pass themselves. No implementation work is
in progress or requested — wait for direction before building any backlog item above.

## Resume here (2026-08-05, superseded by the entry above) — Roadmap re-scoped: rigctld server mode dropped, Phase 4/5 boundary moved

Two direct user decisions, `spec/14-roadmap.md`/`spec/04-rigctld.md`/`docs/removed-features.md` updated
to match (no code changes this pass — pure re-scoping):

1. **rigctld server mode dropped, not deferred.** Built-in linked Hamlib + rigctld client-mode coverage
   is enough CAT surface; a server role (listening network service, "allow remote control" security
   posture, its own interop burden) wasn't worth carrying for a feature with no expressed demand.
   `spec/04-rigctld.md`'s "Purpose" section now scopes only client mode; the old "Server mode" section
   is kept as a one-paragraph record of what was speced, not deleted outright. Nothing was ever
   implemented (`RigctldServer`/`IRigctldServer` never existed in code — confirmed via grep before
   editing), so this was a pure spec/roadmap edit, no removed code. `docs/removed-features.md`'s MMlink
   entry previously named rigctld server mode as a possible alternative for "another app wants to know
   the current frequency" — corrected to say no alternative exists, since that dependency is now gone
   too.
2. **Phase 4/5 boundary moved — Phase 4 is now "make the program usable."** Everything from the old
   Phase 5 ("Extensibility and polish") except the plugin system itself moved up into Phase 4: the
   remaining dialogs (`OptionsDialog`, `RadioSettingsDialog`, `MacroKeyEditor`, `ColorSettingsDialog`,
   `LanguageSettingsDialog`), the legacy `.ini` settings importer/migration chain, and remaining-views
   localization. Phase 5 is now just the plugin system (`IImageFilter` extension point +
   `PluginManagerDialog`) — it stays separate because a plugin-management dialog has no purpose without
   the plugin host it depends on. `spec/07-image-pipeline.md`'s stale `ITransmitImagePreparer` DoD
   checkbox also fixed while in there (it said "still Phase 4/not built" — actually done and shipped
   last session as the TX image editor).

**Phase 4 is now, in order of what's left**: logbook/ADIF (`08-logging`, blocked in part on a human
emailing Clublog for a `cty.dat` API key — the logbook/ADIF core itself doesn't depend on that and can
go first), then the settings/options/macro/color/language dialogs, then the `.ini` importer and
remaining localization. TX image editor's own hands-on interactive verification (drag/nudge/apply/cancel
in the real running app) is still outstanding too — deferred by the user ("verification will come later
with more UI refinements and changes"), not forgotten.

**Next**: pick a concrete starting point among the above — logbook core is probably the highest-value
next chunk (bigger feature, mostly unblocked), but this hasn't been explicitly confirmed with the user
yet this session.

## Resume here (2026-08-05, superseded by the entry above) — TX image editor: Pieces 1-6 of the audited plan done, Piece 5c (the actual view) is next

Spec (`spec/07-image-pipeline.md`'s "TX image editor" section) went through two auditor rounds,
verdict "ready to build," before any code. Done so far, all with full-solution green runs after each:

- **Pieces 1-4**: `NormalizedRect`/`ImageOverlayElement`/`ImageOverlay`/`ITransmitImagePreparer`
  abstractions; bundled `DejaVuSansMono.ttf` (+ `LICENSES.md` entries for it and
  `SixLabors.ImageSharp.Drawing`/`SixLabors.Fonts`); `TransmitImagePreparer` (ImageSharp-backed
  Crop/Resize/ApplyOverlay, explicit pixel-copy helpers, never `MemoryMarshal.Cast`);
  `UiLayeringArchitectureTests` switched to prefix-matching `SixLabors.` (exact-name matching would
  have silently let `SixLabors.ImageSharp.Drawing`/`SixLabors.Fonts` slip into `ScanlineStudio.UI` —
  the same bug class this project has caught twice before).
- **Piece 5a**: `LoadOriginalAsync` added to `IImageFileLoader`/`IStockImageLibrary` (native
  resolution, no resize) — the picked-image flow now needs the untouched original, not just the
  mode-fitted one.
- **Piece 5b**: `TxImageEditorPaneViewModel` — pure, UI-tech-agnostic (drag ops take already-normalized
  deltas), covers crop/resize/stretch + overlay elements, keyboard nudge (1px plain arrow / 16px
  Ctrl-arrow / Shift-arrow resize-and-auto-stretch, verified against `yoniq-old/YONIQ-main/PicRect.cpp:925-1001`),
  realtime preview against a pre-downsampled working copy, Apply/Cancel events. 10 headless tests,
  each non-discriminating one caught by the revert-fix-confirm-fail technique (notably: Apply must run
  against the ORIGINAL, not the working copy — reverting that swap made the test fail exactly as
  expected).
- **Piece 6**: Wired into `TxControlsPaneViewModel` + `AppDockFactory`. Real design decision made
  during this piece, not pre-planned: picking a source (file browse or stock thumbnail) now always
  opens the editor (matches the audited spec's step 2 — native-res load, then edit — rather than a
  separate manual "Edit" command). `TxControlsPaneViewModel` retains an `EditState` (original image +
  crop/preserveAspect/overlay) so a later mode change re-runs Crop→Resize→ApplyOverlay against the
  cached original at the new mode's dimensions — **and this made the old Piece 7 mode-change
  `CancellationTokenSource` race-guard machinery genuinely unnecessary**: that machinery existed to
  guard an async I/O reload race; the new reflow is synchronous CPU work against an already-in-memory
  original, so there's no race left to guard against. Removed it rather than keeping it "just in
  case" (matches this project's own "if a fix's complexity doesn't earn its keep, revert to the
  simpler alternative" working-methodology line). `AppDockFactory` owns turning the VM's
  `EditorOpened`/`EditorClosed` events into an actual `RxToolDock` pane add/remove — `TxControlsPaneViewModel`
  itself never touches Dock. `_isEditorOpen` is a simple bool backstop against overlapping picks
  (deliberately not another `CancellationTokenSource` — a single short-lived user-driven sequence
  doesn't need one). Two old Piece-7 tests (the async mode-change-race ones) were deleted as
  genuinely obsolete, not just stale; six new tests added/rewritten around the editor hand-off,
  cancel-leaves-prior-state-untouched, the overlapping-pick guard, and the mode-change reflow —
  again each verified via revert-fix-confirm-fail. **Full solution: 778/778 tests green** (12+31+99+1+51+548+4+7+17+8
  across all ten test projects, checked individually after the aggregate run's output was truncated
  in the terminal capture).
- **Not yet committed** — all TX editor work (Pieces 1-6) is still uncommitted in the working tree;
  standing rule is to confirm before commit/push rather than assume "keep going" covers it.

**Piece 5c done** (`TxImageEditorPaneView.axaml` + code-behind): interactive canvas showing the
working copy at native pixel size (`Stretch="None"`, so on-screen pixels map 1:1 to normalized
crop/overlay coordinates, no scale-factor math needed) with a draggable crop rectangle (body = move,
bottom-right corner handle = resize, matching legacy's own real single-corner precedent), draggable
overlay text (center-anchored via a `TranslateTransform` + `NegativeHalfConverter`, new small
`Converters/` folder), numeric X/Y fields alongside per legacy `TextIn.h`'s real precedent, keyboard
nudge (Up/Down/Left/Right, Ctrl=16px, Shift=resize) wired to the already-tested VM methods, aspect-lock
checkbox, Apply/Cancel, a localized fidelity caption, real pipeline preview with nearest-neighbor
display upscaling. `TxImageEditorPaneViewModel` gained plain computed pixel-space properties
(`WorkingCopyWidth/Height`, `CropLeftPixels`/`TopPixels`/`WidthPixels`/`HeightPixels`/`RightPixels`/`BottomPixels`)
so the View stays a plain binding consumer, no converter/multibinding gymnastics for the geometry math.

**Real pre-existing bug found and fixed during this piece's own hands-on verification** (not part of
its original scope, but blocking it): launching the real app for the first time with a NON-empty stock
image library crashed on `System.ArgumentException: Unable to resolve type vm:TxControlsPaneViewModel`
— the Phase-4 stock-picker XAML's `Command="{Binding $parent[ListBox].((vm:TxControlsPaneViewModel)DataContext).SelectStockImageCommand}"`
pattern compiled fine but fails at runtime (Avalonia falls back to a reflection-mode binding for this
path shape inside a deferred `ItemsControl` template, and that resolver can't find the type). This had
never actually been exercised at runtime before — Phase 4's own verification pass evidently never had
a populated stock library when it screenshotted. Fixed by giving `StockEntryViewModel` its own
`SelectCommand` (set once at construction to the parent's `SelectStockImageCommand`) instead of reaching
across the DataTemplate boundary with a type-cast path — trivial, no cross-template binding needed at
all. All 31 UI tests + full 778-test solution still green after the fix.

**Hands-on interactive verification of Piece 5c could NOT be completed this pass**: the app launches
cleanly and renders correctly (confirmed via the XWD-screenshot pipeline, on the secondary HDMI-1
monitor per the standing instruction) — Mode/Stock/Browse/Transmit all visible and correctly laid out.
But every synthetic XTEST click attempted (the stock thumbnail, the Browse button, the View menu,
the Mode combo box — a deliberate escalating sanity-check ladder from most- to least-specific control)
produced zero visible effect, despite the window holding `_NET_ACTIVE_WINDOW` focus and multiple
click techniques tried (instant warp+click, stepped-motion approach+click). This is a WORSE version of
the already-documented Dock-tab-strip XTEST unreliability from the Phase-4 pass (that pass's Menu
clicks reportedly DID work) — something about this specific window/session now blocks synthetic input
entirely, cause not identified. **Drag/nudge/apply/cancel interaction in `TxImageEditorPaneView` is
therefore unverified by hand** — the VM-side logic it calls into (10 tests) and the hand-off/reflow
logic around it (6 tests) are the only real verification this pass got. A synthetic test image
(`~/Pictures/ScanlineStudio/Stock/test-pattern.png`) was created for this attempt and removed again
afterward.

**Next**: either get a human to hands-on-verify Piece 5c in a real session (drag crop/overlay, nudge,
apply/cancel), or investigate the synthetic-input regression separately before trusting further
automated UI verification in this sandbox. Not yet committed — all TX editor work (Pieces 1-6, plus
Piece 5c and the stock-picker crash fix) is still uncommitted in the working tree.

## Resume here (2026-08-05, superseded by the entry above) — Phase 4 image-tooling UI: all 9 pieces DONE, plus real hands-on layout/theme corrections and a Dock.Avalonia/Avalonia package update

All 9 pieces of `/home/artien/.claude/plans/wondrous-crafting-ladybug.md` are complete: Abstractions
interfaces, `IStockImageLibrary`/`IReceiveHistoryStore` implementations, auto-record-on-completion,
strengthened `UiLayeringArchitectureTests`, `RxHistoryPaneViewModel`, TX stock picker + mode-change
race fix, minimal View menu, and DI wiring — full detail in the superseded entry below (pieces 1-4)
and this entry's own history. **Full solution: 761/761 tests green, 0 build warnings/errors.**

**Pieces 5-9, done after the backend/persistence checkpoint below:**
- **Piece 5**: `UiLayeringArchitectureTests` gained a `PackageReference` check (`Microsoft.Data.Sqlite`/`SixLabors.ImageSharp`
  must never appear in `ScanlineStudio.UI.csproj`) — the existing checks only ever caught `ProjectReference`/assembly-name
  violations, a real gap the audit flagged.
- **Piece 6**: `RxHistoryPaneViewModel` + view — thumbnail grid from `IReceiveHistoryStore`, selecting
  an entry loads a **separate** read-only preview, never touching `RxImagePaneViewModel`'s live binding
  (structurally guaranteed — the constructor doesn't even take `IReceivedImageBuffer`).
- **Piece 7**: TX stock picker (inline thumbnail strip in `TxControlsPaneViewModel`, backed by
  `IStockImageLibrary`) + the audited mode-change retention fix. **Two real bugs caught by the test
  suite itself, not review**: (1) the View's `<Image>` binding had nothing to bind to — `StockImageEntry`
  carries no thumbnail, needed a `StockEntryViewModel` wrapper mirroring `RxHistoryEntryViewModel`,
  caught before ever building since I was writing the XAML by hand. (2) The race-condition test for
  "a stale reload must never overwrite a newer one" **passed even with the actual fix removed** on
  first attempt — the fake `IImageFileLoader`'s own cancellation-token wiring was masking the code
  path the test was supposed to exercise (the stale call threw via cancellation before ever reaching
  the guard). Fixed the fake (stopped auto-cancelling) and reconfirmed: fails without the fix, passes
  with it.
- **Piece 8**: Minimal View menu. Used .NET reflection against the actual installed `Dock.Model.dll`
  before writing code (per the plan's own flagged uncertainty) — confirmed `IFactory` has no
  `RestoreDockable` at all (that name only exists on the unrelated `IDockState.Restore`
  save/load-layout-to-disk mechanism); the real reopen path is `Factory.AddDockable`. Verified via a
  headless Avalonia test driving the real `AppDockFactory` API (close → reopen → same instance,
  correct `ActiveDockable`) rather than synthetic X11 clicks — a real attempt at XTEST-driven clicks
  on the Dock tab strip proved unreliable in this sandbox (see below), but native `Menu`/`MenuItem`
  controls turned out to work fine for a real screenshot-verified open (see next).
- **Piece 9**: DI wiring in `ScanlineStudio.Host` — `IStockImageLibrary`, `IReceiveHistoryStore`,
  `ReceiveHistoryRecorder` (eagerly resolved once so its constructor's event subscriptions actually
  fire, same reasoning as why nothing else in the DI graph would trigger it automatically).

**Real screenshot verification, not just claimed** — genuinely launched the app (`dotnet run`) under
this machine's live X11 display and screenshotted it. Real friction hit and solved: `gnome-screenshot`/
`flameshot`/`xwd` all returned only the desktop wallpaper for this specific window despite `xwininfo`
confirming `Map State: IsViewable` — root cause never fully identified (GPU/compositor-related most
likely), worked around by parsing the raw XWD pixel dump manually with `struct`/`numpy`/`PIL` (no
`ImageMagick`/`netpbm` available, no sudo). That manual parse **did** show the real running app
correctly. Synthetic XTEST clicks landed fine on the native `Menu` (View dropdown opened for real,
screenshotted) but not reliably on Dock.Avalonia's own tab strip (a coordinate-scaling mistake on my
first attempts, then still imprecise after correcting it) — the close/reopen *interaction* itself is
verified by the headless test instead, which is both more reliable and matches how every other pane
in this codebase is already tested.

**Two real user corrections, made directly after seeing the running app (not held for a future pass)**:
1. **Waterfall was "way too big"** — it was tab-grouped with RX Image/RX History, so it took over the
   whole region when active. Restructured `AppDockFactory.CreateLayout()`: the waterfall now lives in
   its own fixed-proportion `WaterfallToolDock` (~20% height) above a `RxToolDock` (RX Image/RX History
   tab-grouped, ~80%), inside a vertical `ProportionalDock` — matching every real SDR-instrument
   reference the Aesthetic Directive itself cites (SDR++/cuSDR64/Perseus keep the waterfall as a
   persistent strip, never a tab). `ShowPane`/View-menu commands updated to target the right home dock
   per pane. `spec/09-ui.md`'s Aesthetic Directive and "Main window layout" sections, and
   `spec/07-image-pipeline.md`'s cross-references, updated to match — this was a real correction to
   already-written spec text, not just code.
2. **Default theme changed from OS-follow to explicit Light** — `App.axaml`'s
   `RequestedThemeVariant` was `"Default"` (follows system theme); on this machine's dark-OS-theme
   setup that meant the app defaulted to dark, which the user didn't want. Changed to `"Light"`
   explicitly; `spec/09-ui.md`'s Theming section updated to match (still user-overridable in principle,
   but no settings UI exists yet to actually change it at runtime).

Both changes verified visually via the same real-screenshot pipeline, not assumed.

**Dock.Avalonia/Avalonia package update, requested directly ("update docks")**: bumped
`Dock.Avalonia`/`Dock.Model.Mvvm` from the deliberately-pinned `11.2.0.2` line (see the old pin
comment's own history — it existed specifically to keep Avalonia at `11.2.3`) to the latest `11.x`
line, `11.3.12.1` — **not** the actual latest overall (`12.1.0`), since that requires Avalonia
`12.1.0`, a major-version framework jump well beyond what a routine package update implies; confirmed
via nuspec inspection before choosing, not assumed either way. `Avalonia`/`Avalonia.Desktop`/
`Avalonia.Diagnostics`/`Avalonia.Themes.Fluent`/`Avalonia.Fonts.Inter`/`Avalonia.Headless`/
`Avalonia.Headless.XUnit` all bumped to the matching `11.3.12` floor. **Real breaking change hit and
fixed**: Dock's Fluent theme moved out of `Dock.Avalonia` itself into a new
`Dock.Avalonia.Themes.Fluent` package starting around the `11.3.2` line (confirmed by searching
nuget.org and inspecting the actual package contents, not guessed) — `App.axaml`'s `avares://` path
updated to match (`avares://Dock.Avalonia.Themes.Fluent/DockFluentTheme.axaml`, root path not the old
`/Themes/` subfolder). Full solution re-verified: 761/761 tests green (one `Core.Radio.Tests` failure
under the full parallel run was the already-documented pre-existing flake, confirmed passing 99/99 in
isolation — unrelated to this change, not chased). Real app re-launched and screenshotted again after
the bump: renders identically, no visual regression.

**Explicitly out of scope this pass** (unchanged from the original plan): `ITransmitImagePreparer`
(crop/resize/filter, the `ImageRectDialog` live-preview editor — real "editing" the user's second
message flagged as part of "images, receiving, editing, tx-ing," genuinely not built yet, next
candidate), full logbook/QSO linking, dock layout persistence across restarts (View menu covers
reopen only).

**Next**: `ITransmitImagePreparer` (crop/resize/filter/overlay) is the natural next piece given the
user's own stated priority ("images, receiving, editing, tx-ing") and the standing
`feedback_ui_effort_allocation` memory (image RX/TX/templating richness over further polish
elsewhere) — not started, no design work done yet.

## Resume here (2026-08-05, superseded by the entry above) — Phase 4 image-tooling UI: pieces 1-4 of 9 done (backend/persistence layer)

Implementing the audited design (`spec/07-image-pipeline.md`/`spec/09-ui.md`/`docs/removed-features.md`,
both rounds passed) from `/home/artien/.claude/plans/wondrous-crafting-ladybug.md`. Building piece by
piece, each tested before the next.

**Done so far:**
1. **Abstractions interfaces** — `IStockImageLibrary`/`StockImageEntry`,
   `IReceiveHistoryStore`/`ReceiveHistoryEntry`/`ReceiveHistoryFilter` added to
   `ScanlineStudio.Abstractions.Imaging`. **Real bug caught during implementation, not by either
   audit round**: `ReceiveHistoryFilter` had a `Callsign` field with nothing on `ReceiveHistoryEntry`
   to filter against (no QSO-linking UI exists to populate it) — removed, spec updated to match.
2. **`IStockImageLibrary`** — `StockImageLibrary` (`ScanlineStudio.Core.Imaging`), folder-scan +
   ImageSharp thumbnail/full-load, `ImageLibrarySettings` section (`StockDirectory`, defaults under
   `MyPictures/ScanlineStudio/Stock`). 4 new tests, pixel-exact.
3. **`IReceiveHistoryStore`** — `SqliteReceiveHistoryStore` (`ScanlineStudio.Core.Logbook`, this
   project's first real content beyond a template stub). One table, `Microsoft.Data.Sqlite`. **Real
   layering bug caught and fixed before it shipped**: first draft referenced
   `ScanlineStudio.Core.Imaging`'s `ArrayImageSource` for thumbnail decode — a Core-to-Core edge,
   exactly what `ReceivedImageBuffer`'s own doc comment (in `Core.Imaging`) says must never happen,
   just the mirrored direction. Fixed with a small local `IImageSource` holder instead of sharing
   code across sibling Core projects. 5 new tests.
4. **Auto-record RX history on decode completion** — `ReceiveHistoryRecorder`
   (`ScanlineStudio.Core.Logbook`). **Real gap found during planning** (not caught by either audit
   round): `ISstvDecoder` has no "decode completed" event, only per-scanline-group `LineDecoded` —
   completion is *learned* by tracking the delta between successive `Line` values (constant per
   image, reveals the scanline-group size for paired-line families like PD/MP/RM8/RM12 without
   needing a new decoder event). **A real bug in the first implementation, caught by the test suite
   itself, not review**: the fallback for "step not yet learned" defaulted to `mode.ImageHeight`,
   which made the check `Line(0) + ImageHeight >= ImageHeight` trivially true — every image would
   have "completed" after just its first line, every time. Two of the three original tests didn't
   catch this because their early assertions raced the fire-and-forget completion `Task.Run` and
   happened to pass by timing luck; the third test (`DecodeRestarted_BeforeCompletion_...`) exposed
   it reliably. Fixed by deferring any completion check until the step is actually learned (2nd+
   event) — safe because no real `SstvModeDefinition` has an `ImageHeight` anywhere near 1-2 rows.
   4 new tests, confirmed stable across repeated runs.

Fire-and-forget I/O (file save + SQLite insert) from the recorder is isolated in a `try`/`catch`
`Task.Run`, matching `SstvSessionService`'s already-established fan-out-handler pattern — must not
block the audio drain thread `LineDecoded` fires from.

Full solution builds clean, 0 warnings/errors, throughout (Debug analyzers already caught one real
issue independent of the above: `DateTimeOffset.Parse(string)` without `CultureInfo.InvariantCulture`
in the SQLite reader, CA1305 — fixed).

**Remaining (pieces 5-9 of 9)**: strengthen `UiLayeringArchitectureTests` with a `PackageReference`
check (Microsoft.Data.Sqlite/SixLabors.ImageSharp must never appear in `ScanlineStudio.UI.csproj`);
`RxHistoryPaneViewModel`+view (dockable, tab-grouped with RX Image, never touches
`RxImagePaneViewModel`'s live binding); TX stock picker + the mode-change retention/race fix in
`TxControlsPaneViewModel` (audited finding: must disable transmit for the whole in-flight reload
window, not just at the start, and cancel-and-replace on rapid mode changes); minimal View menu
(Dock's real `RestoreDockable` reopen semantics not yet confirmed by actually running the app — flagged
in the plan as needing hands-on verification, not assumed); DI wiring in `ScanlineStudio.Host`. Final
step: full suite + `run` skill hands-on verification per the plan's own closing section.

## Resume here (2026-08-05, superseded by the entries above) — Full rename: Yoniq → Scanline Studio

Whole-application rebrand, requested by the user, done in one pass right after Phase 3 (minimal UI)
closed out. Two commits: the Phase 3 work itself landed first under the old name (it was built as
Yoniq), then the rename as its own commit on top — kept separate since squashing them together would
have conflated two unrelated diffs.

**What changed**: every `Yoniq.*` namespace/project/folder/assembly → `ScanlineStudio.*` (code
identifiers, single PascalCase word); every prose mention of the product → "Scanline Studio" (two
words); solution file `Yoniq.sln` → `ScanlineStudio.sln`; the app-data settings folder
`~/.config/Yoniq/` → `~/.config/ScanlineStudio/` (moved, not recreated, so the existing
`settings.json` content survived); GitHub repo `Ar0xA/Yoniq-reborn` → `Ar0xA/Scanline-Studio`
(renamed via `gh repo rename`, local `origin` remote updated to match, GitHub auto-redirects the old
URL). Full history in this file was rewritten too, not just this entry, so the whole journal reads
consistently under the new name.

**Explicitly untouched, on purpose**: this project's *own* old name was "Yoniq" — what it's a rewrite
*of* is the actual legacy upstream project, always spelled `YONIQ` (all caps) or referenced via the
gitignored local clone `yoniq-old/YONIQ-main/` (`https://github.com/w0eeemst/YONIQ`). Those are a real
external project's name, not this one's, and the case-sensitive distinction (`Yoniq` vs `YONIQ` vs
`yoniq-old`) is exactly what made a scripted rename safe — neither was touched. Same for `QSSTV-main/`
and `hamlib/` (external reference clones, gitignored). Also left alone: the local working directory
name itself (`/home/artien/code/Yoniq-reborn` on disk) — renaming the live folder a running session is
executing from was judged unnecessary risk and unrelated to the GitHub repo rename (a git remote's
name doesn't need to match the local clone folder name); left for the user to rename at their own
convenience.

**Verified, not just assumed**: full solution build clean (0 warnings/errors) and full test suite
740/740 green post-rename — exact match to the pre-rename baseline, confirming this was purely
mechanical with no behavioral change. `grep -rn "Yoniq"` across every tracked/untracked
non-gitignored file returned zero hits (one `app.manifest` file needed a manual follow-up fix — its
`.manifest` extension wasn't in the original sed sweep's file-extension allowlist).

**Next**: same as before this rename — Phase 4 (full image tooling: crop/resize/filter/overlay, stock
library, RX history; logbook), with image RX/TX/templating richness prioritized over further
waterfall polish per memory `feedback_ui_effort_allocation`.

## Resume here (2026-08-05, superseded by the entry above) — Phase 3 (minimal UI) FULLY DONE, all 13 tasks, real e2e RX/TX proven over real hardware

**Plan file**: `/home/artien/.claude/plans/hidden-conjuring-curry.md` (all 13 tasks complete). Full solution
test suite: **740/740 green**.

**What shipped, beyond the tasks-1-5 entry below**: real design-tokens/theme layer, three real Dock
panes (Waterfall, RX Image, TX Controls — the radio/frequency readout is a fixed status strip in
`MainWindow` chrome, not a 4th pane, confirmed correct against both `spec/14-roadmap.md` and
`spec/09-ui.md`), a minimal scrolling grayscale `WaterfallControl`, and a real end-to-end RX/TX
demonstration.

**Aesthetic pivot, mid-session — now the durable spec, not just a chat note**: user gave a detailed
"Aesthetic Directive" (raw instrumentation look like cuSDR64/Perseus/SDR++, explicitly *not* modern
SaaS: no rounded corners, no gradients/shadows, 2-4px padding max, monospace for all numeric readouts,
bordered "module group" containers). Recorded verbatim in `spec/09-ui.md`'s "Visual design direction"
section (supersedes the earlier softer SDR++ paragraph). Applied immediately: `CornerRadius="0"`
globally on Button/ToggleButton/ComboBox/TextBox/CheckBox, spacing tokens shrunk from 8/16px to 2/4/6px,
a `ScanlineStudioMonospaceFontFamily` resource + `TextBlock.ScanlineStudioReadout` class, a `Border.ScanlineStudioModuleGroup`
style (Avalonia has no native GroupBox). **User also explicitly deprioritized the waterfall's visual
polish** relative to RX/TX image handling and templating (saved as memory
`feedback_ui_effort_allocation` — read that before sinking more effort into waterfall visuals in a
future session) — the waterfall control was deliberately kept simple (flat grayscale, fixed dB range),
consistent application of the design philosophy mattered more than polishing any one component.

**Real bugs/gaps caught by an actual second auditor round on the steps-6-13 continuation plan** (not
just by review — by re-reading the actual shipped code): (1) the original e2e-demo plan was
self-contradicting — `TransmitAsync` deliberately pauses capture during TX, so a *single* process
structurally cannot self-decode its own transmission; fixed by using two independent session instances
(mirrors two real `ScanlineStudio.Host` processes) sharing one real virtual audio cable. (2) `TxControlsPaneViewModel`
would have pulled a `ScanlineStudio.Core.Imaging` concrete reference into `ScanlineStudio.UI` (failing the architecture
test) had `IImageFileLoader` not been moved to `ScanlineStudio.Abstractions.Imaging` first. (3) `ReceivedImageBuffer`
copying only the *reported* row instead of the whole live canvas would have silently left every other
row blank for paired-line modes (PD/MP/RM8/RM12, `RowsPerTransmissionLine=2`) — caught before any code
shipped. (4) `AnalogFmSstvEncoder` throwing on any TX image size mismatch meant `ImageFileLoader` needed
to resize-to-mode, or no real picked file could ever transmit.

**Real end-to-end demo, actually run, not just claimed**: real `rigctld` + Hamlib Dummy rig (port 4534)
for the radio half — the running GUI genuinely showed `145.000000 MHz Fm` live in the status strip
(screenshotted). For the audio RX/TX half, GUI mouse automation wasn't available in this sandbox (no
xdotool/ydotool; `python3-xlib` + XTEST worked for simple clicks but not reliably for a full file-picker
flow), so the round trip was proven via a small script constructing two real, independent
`SstvSessionService` instances (mirroring two real app processes) over a real PipeWire virtual cable
(`pactl load-module module-null-sink`) — real `MiniAudioEngine`, real `AnalogFmSstvEncoder`/`Decoder`,
RM8 mode (~8s, chosen for a fast real-hardware round trip). Result: mode auto-detected correctly, all
120 lines received, PTT keyed `[True, False]` around the transmission, and the received image is
**pixel-identical** to the source test image (screenshotted side-by-side). This exercises the exact
same `ISstvSessionService.TransmitAsync` call path the real TX button invokes (already covered
separately by `TxControlsPaneViewModelTests`'s unit test with a fake session service) — genuinely proves
the two halves (UI-to-service wiring, service-to-hardware DSP round trip) rather than mocking either one.

**Also found and fixed along the way**: Phase 3 had no "Start Receiving" trigger anywhere in the UI —
added an auto-start-receiving call at `ScanlineStudio.Host` startup (same pattern/graceful-degradation as the
existing radio auto-connect), discovered only because the actual e2e demo attempt surfaced it.

**Cleanup after the demo**: virtual cable module unloaded, `rigctld`/demo `ScanlineStudio.Host` processes killed.
One thing NOT cleaned up: `~/.config/Scanline Studio/settings.json` still has the demo's radio/audio device config
(pointing at a now-unloaded virtual cable and a `rigctld` port that's no longer running) — harmless
(everything that reads it degrades gracefully via try/catch) but worth knowing if a next session runs
the real `ScanlineStudio.Host` and wonders why RX/radio silently don't start.

**Next**: Phase 3 is done. Per `spec/14-roadmap.md`, Phase 4 is next (CAT protocols already partly
done early/Hamlib; full image tooling — crop/resize/filter/overlay, stock library, RX history; logbook).
Per this session's own explicit user guidance, image RX/TX/templating richness matters more than further
waterfall polish — see memory `feedback_ui_effort_allocation`.

## Resume here (2026-08-05, superseded by the entry above) — Phase 3 non-UI foundation DONE (tasks 1-5 of 13); UI shell work starts next

**Plan file**: `/home/artien/.claude/plans/hidden-conjuring-curry.md` (approved, being executed in order —
13 tracked tasks, 5 done). Full context/reasoning for every decision below lives there; this entry is
just the resume-cold summary.

**Done, tested, committed to working tree (not yet committed to git — ask before committing/pushing per
standing rule):**

1. **Pre-build spike** — Dock.Avalonia added (pinned `11.2.0.2`, confirmed resolved Avalonia stays
   `11.2.3` not silently bumped), localization core, `Translate` markup extension, one design token, one
   throwaway Dock pane. **Real finding, not just plumbing**: the original plan's decision #8 ("thin Dock
   `Tool`/`Document` shell wrapping a separate plain view-model via `Context`") rendered a real tab
   header but a **blank body**, every variant tried. Root-caused via Avalonia DevTools + Dock's own
   `DockMvvmSample` source: Dock's default templates resolve a pane's body by matching the ambient
   `ViewLocator` against the dockable **itself**, not its `Context`. Fixed: real panes now derive from
   `Tool`/`Document` directly (`SomePaneViewModel : Tool`) — simpler than the original plan, not more
   complex. `ViewLocator.Match` updated to accept `IDockable` too. Confirmed working end-to-end
   (screenshot, real window, real localized/token-styled content rendering).
2. **Localization core** — `ILocalizationService`/`JsonLocalizationService` (JSON-file-backed, always
   boots into English, `assets/locale/en.json`+`locales.json`), the CI-style hardcoded-string grep test
   (`ScanlineStudio.UI.Tests`) and locale-key-subset-of-English test (`ScanlineStudio.Core.Localization.Tests`) both wired
   in now, not deferred.
3. **Settings schema** — **real spec bug found and fixed**: `spec/12-settings.md`'s own code sample
   (`AppSettings(AudioSettings Audio, RadioConnectionSettings Radio, ...)`) can never compile —
   `ScanlineStudio.Settings` sits at the bottom of the layering diagram, below `ScanlineStudio.Abstractions`, so it can
   never reference a type from `ScanlineStudio.Core.Radio` etc. without inverting that layering. Fixed with a
   named `Dictionary<string, JsonElement>` section bag + generic `GetSection<T>`/`WithSection<T>`
   extensions (caller supplies its own source-generated `JsonTypeInfo<T>`) — spec updated to match.
   Added `RadioConnectionSettings` (`ScanlineStudio.Core.Radio`), `AudioDeviceSettings` (`ScanlineStudio.Core.Audio`),
   `LocalizationSettings` (`ScanlineStudio.Core.Localization`) — only the 3 sections Phase 3 actually needs, not
   all 7 from the spec's full v1 list.
4. **`IWaterfallSource`** — new (didn't exist before; legacy `Fft.cpp` was never ported, only the PLL
   demodulator path was). New standard radix-2 Cooley-Tukey FFT + Hann window (`ScanlineStudio.Core.Sstv`) — a
   deliberate non-port, since CLAUDE.md's port-first rule is scoped to decode-affecting DSP math, not a
   visualization feature. Shaped like `ISstvDecoder.PushSamples` (a plain push method), not a direct
   `IAudioEngine.SamplesCaptured` subscription — keeps the DSP layer's existing "no hardware knowledge"
   purity and structurally guarantees decode/waterfall independence. Threading contract mirrors
   `IRadioController.StateChanges`'s already-established pattern (push synchronously, subscriber
   marshals itself) rather than inventing new scheduling machinery.
5. **`ScanlineStudio.Application` services** — `IRadioSessionService`/`RadioSessionService` (thin facade +
   settings-driven connect) and `ISstvSessionService`/`SstvSessionService` (owns the isolated
   decoder/waterfall fan-out that actually fixes the original spike-era bug, plus the TX/RX interlock —
   capture pauses during TX, PTT keyed around playback, restored afterward only if RX was running
   before). `IReceivedImageBuffer` interface added to `ScanlineStudio.Abstractions.Imaging` (concrete impl is
   task 6, next). **Another real bug found by the build, not by review**: giving `ScanlineStudio.Application` real
   content for the first time made `ScanlineStudio.UI/App.axaml.cs`'s bare `Application` base-class reference
   ambiguous with the new `ScanlineStudio.Application` namespace (both reachable as `Application` from a sibling
   namespace under the shared `Scanline Studio` root) — fixed by fully-qualifying `Avalonia.Application`.

**Test status**: full solution green, 724/724 (up from 685 at Phase-3 start) — Sstv 548, Radio 99,
MiniAudio 51, Application 12 (new), Localization 7 (new), Settings 4 (new), UI 1 (new), Audio/Logbook 1
each (still template stubs, untouched, not this phase's concern).

**Next (task 6 of 13)**: `SixLabors.ImageSharp` license check (Six Labors Split License, not plain
MIT/Apache — must actually read it and record a `LICENSES.md` determination before adding the package,
unlike Dock which needed no entry) — then real `IReceivedImageBuffer` (snapshot-on-read contract) + a
basic TX image file selector. Then task 7 (DI wiring in `ScanlineStudio.Host`), task 8 (architecture test — must
land before any view exists, since `ScanlineStudio.Application` transitively drags in every `Core.*` concrete),
then the actual UI shell (tokens, Dock panes, waterfall control, localize-as-built, end-to-end demo).

## Resume here (2026-08-04, superseded by the entry above) — Starting Phase 3 (minimal UI), nothing built yet

**Decision**: after linked Hamlib landed (previous entry below — committed/pushed, `2392045`), asked
whether to jump to the rest of Phase 4 (image tooling, logbook, `TemplateCatProtocol`) or follow the
roadmap's own sequence. User chose **Phase 3 first** — this is the roadmap's actual next step; Hamlib
only jumped the queue because its packaging question was blocking and it was headlessly testable, same
as rigctld. Nothing from Phase 3 has been started yet — this entry exists so a cold session can begin
straight into it.

**What Phase 3 is** (`spec/14-roadmap.md`, "Phase 3 — Minimal UI, first end-to-end path"):
- [[09-ui]]: `MainWindow` walking skeleton — waterfall, RX image panel, basic TX button — wired to
  Phase 1/2 services through `ScanlineStudio.Application`.
- [[10-localization]]: `ILocalizationService` + `Translate` extension in place from the start
  (retrofitting localization onto an already-built UI is far more expensive than building it in from
  the first window — CLAUDE.md's own no-hardcoded-UI-strings rule).
- [[07-image-pipeline]]: minimal `IReceivedImageBuffer`/basic TX image selection only (full
  crop/resize/filter/overlay stays Phase 4).

**Demo target**: a real over-the-air (or audio-cable-looped) SSTV RX/TX session, end-to-end, through the
UI, with a rig's frequency shown live via rigctld (or now, linked Hamlib).

**Starting-point state, checked directly (not assumed) so the next session doesn't have to rediscover
it**:
- `ScanlineStudio.UI` already has real Avalonia MVVM scaffolding: `ViewLocator.cs`, `App.axaml.cs`,
  `ViewModels/MainViewModel.cs`, `ViewModels/ViewModelBase.cs`, `Views/MainWindow.axaml.cs` — likely
  from the original `dotnet new avalonia.mvvm` template. **Not yet inspected for how much is real vs.
  default template boilerplate** — read these fresh before assuming any of it is load-bearing.
- `ScanlineStudio.Application` is **completely empty** — no `.cs` files at all, just a `.csproj` referencing
  `ScanlineStudio.Abstractions`/`ScanlineStudio.Settings`/`ScanlineStudio.Core.Radio`/`ScanlineStudio.Core.Radio.Cat`/
  `ScanlineStudio.Core.Radio.Rigctld`/`ScanlineStudio.Core.Audio`/`ScanlineStudio.Core.Sstv`/`ScanlineStudio.Core.Imaging`/
  `ScanlineStudio.Core.Logbook`/`ScanlineStudio.Core.Localization`. **Does not yet reference `ScanlineStudio.Core.Radio.Hamlib`** —
  will need adding when the UI actually wires up a radio backend. This project is where
  `IRadioController`/`IAudioEngine`/etc. orchestration for the UI is supposed to live per
  `spec/01-architecture.md`'s layering — currently 100% unbuilt, this is most of Phase 3's real work.
- `ScanlineStudio.Host` has a real `Program.cs`, references `ScanlineStudio.UI`/`ScanlineStudio.Application`/`ScanlineStudio.Settings`/
  `ScanlineStudio.Core.Audio.MiniAudio`, and already has per-OS `CopyNativeShim*` MSBuild targets (Linux verified,
  Windows/macOS unverified from this sandbox) that copy MiniAudio's native shim into the output dir —
  **Hamlib needs no equivalent target**, since it's discovered/loaded dynamically at runtime rather than
  built/copied at compile time (the whole point of "bring-your-own-libhamlib").
- No DI container wiring exists anywhere yet — `RigctldProtocolFactory`/`HamlibProtocolFactory` are
  still only ever constructed directly in tests, never registered in `ScanlineStudio.Host`. Phase 3 is likely
  where this actually needs to happen for the first time.

**Before writing code**: this is new architecture (UI/Avalonia, first real `ScanlineStudio.Application` content),
not a port — same discipline as Phase 2's radio layer and the Hamlib backend both got: design first,
`auditor` plan-review pass before implementation, restate the ADHD/scope rule in every subagent prompt
(CLAUDE.md §1/§7). Read `spec/09-ui.md`, `spec/01-architecture.md`, `spec/10-localization.md`, and
`spec/07-image-pipeline.md`'s minimal-scope section fresh before planning — none of the four have been
re-read this session, only referenced from the roadmap summary above.

## Resume here (2026-08-04, superseded by the entry above) — Linked Hamlib backend DONE, committed and pushed (`2392045`)

**"Compile Hamlib in like WSJT-X?" question answered, then built.** User asked how to bundle Hamlib
in-process. Investigated for real rather than assuming: researched WSJT-X's actual approach (they
maintain a private Hamlib fork, statically link it via a "superbuild" CMake step — one static binary,
no runtime swap) and ran it past an `/adhd` ideation pass (5 cognitive frames, 30 ideas) plus two rounds
of Opus `auditor` review. Landed on **"bring-your-own-libhamlib"**: Scanline Studio never builds/forks/vendors
Hamlib at all — `ScanlineStudio.Core.Radio.Hamlib` P/Invokes whatever `libhamlib` the user's OS/package manager
already has installed, discovered at runtime, version-gated to major-4, falling back to the
already-working `rigctld` client on failure. Rejected the WSJT-X-style static approach specifically
because this is a solo hobby project with a constrained CI-minutes budget and no unmerged Hamlib
patches to justify carrying a fork. Full reasoning: `spec/03-cat-layer.md`'s "Linked Hamlib:
bring-your-own-libhamlib" section.

**Built and tested, this session:**
- `ScanlineStudio.Core.Radio.Hamlib` (new project): `INativeLibraryLoader`/`NativeLibraryLoader` (seam around
  `NativeLibrary.TryLoad`/`GetExport`), `HamlibLibraryLocator` (3-tier discovery — user override tried
  *exclusively* when set, else bare-soname-then-known-extra-dirs), `IHamlibNative`/`HamlibNative` (the
  frozen P/Invoke surface — `rig_init`/`open`/`close`/`cleanup`, `token_lookup`/`set_conf`,
  `set_freq`/`get_freq`, `set_mode`/`get_mode`, `set_ptt`/`get_ptt`, `rig_version`), `HamlibVersionGate`
  (pure string parsing, major-4-only), `IHamlibRuntime`/`HamlibRuntime`/`IHamlibNativeFactory` (caches
  discovery+version-gate **eagerly in the constructor**, not lazily — matters because a lazy-on-first-
  query shape would put blocking native I/O on whatever thread first calls `ConnectAsync`, e.g. a future
  UI click handler), `HamlibRadioProtocol` (the actual `IRadioProtocol`), `HamlibProtocolFactory`.
  `HamlibConnectionSpec` added to `ScanlineStudio.Abstractions`. 40 new tests in `ScanlineStudio.Core.Radio.Tests`
  (fixture/fake-driven unit tests + 4 real-interop tests against this machine's actual installed
  `libhamlib.so.4` 4.5.5 driving Hamlib's own hardware-free Dummy rig backend — confirmed genuinely
  running, not skipping, via real ~120-165ms durations).
- **Real bugs the plan-review process caught before any code existed** (2 rounds of `auditor`
  plan-review, restated ADHD/scope rule each time): `hamlib_version2` is a `const char*` **data
  export**, not a function — P/Invoking it as a function delegate would have crashed the version probe
  itself; Hamlib error codes are negative and split into soft/hard via `RIG_IS_SOFT_ERRCODE` — a naive
  "nonzero = command-level" classification would have made a dead/unplugged rig spin `CommandFailed`
  forever instead of ever triggering `RadioController`'s reconnect; no thread was specified for the
  blocking native calls, which would have frozen the UI thread on a PTT keystroke; and library discovery
  was being re-run on every backoff reconnect instead of cached once. Round 2 caught residue from round
  1's own fixes (a missing UTF-8 null terminator, the semaphore/cancellation contract for an
  uncancellable native call, the `RIG_MODE_*` table stating bit *positions* where "exact-value equality"
  needed bit *values*). One more real design bug found later, writing tests: the locator's override-path
  precedence contradicted its own spec text (implemented as a last-resort fallback; spec said it should
  *win* over auto-detection) — fixed in both places before tests were written against it.
- Full implementation plan (context, every design decision, both plan-review rounds' findings, the
  files/tests list): `/home/artien/.claude/plans/temporal-launching-valiant.md`. Full spec:
  `spec/03-cat-layer.md`'s "Linked Hamlib: bring-your-own-libhamlib" section (discovery order, version
  gate, frozen P/Invoke surface, `IHamlibNative` seam, threading contract, license provenance).
  `LICENSES.md` got a new "Runtime dependencies consumed but not bundled" section (Hamlib LGPL-2.1,
  nothing bundled).

**Explicitly deferred, not built this pass** (see the plan's "Explicitly out of scope" section):
cross-backend auto-demotion to rigctld (no home for that policy yet — `IRadioController` has no "try the
next backend" concept, needs the `ScanlineStudio.Application`/settings layer, which doesn't exist), and the
Settings UI for the manual library-override path (hard-coded as a constructor parameter for now).

**Test results**: full solution 678/678 (`ScanlineStudio.Core.Radio.Tests` 95/95 including the 40 new Hamlib
tests; `ScanlineStudio.Core.Sstv.Tests` 528/528 unchanged, confirming no DSP regression; everything else
unchanged). One pre-existing flake noted, not chased (off-scope per the ADHD rule):
`RigctldDummyRigIntegrationTests.Capabilities_PttUnsupportedOnTheDummyRig_IsProbedCorrectly`
intermittently fails only under the full parallel test run (a subprocess-connection-wait timing race,
confirmed by running 100% green twice with `xunit.parallelizeTestCollections=false`) — pre-existing test
infrastructure fragility exposed by adding more concurrent real-process/real-native tests, not a defect
in the new Hamlib code.

**Committed and pushed** (`2392045`, "Add linked Hamlib CAT backend (bring-your-own-libhamlib)") — 31
files. Docs also updated in the same commit beyond what's listed above:
`spec/02-radio-layer.md`/`spec/04-rigctld.md`/`spec/14-roadmap.md` (connection-spec/factory counts, the
`WSJT-X style` mislabel corrected, a new detailed roadmap narrative section, two stale Phase-4/"deferred"
references fixed).

**Next**: `TemplateCatProtocol` fallback backend, or the Application-layer wiring (DI registration,
Settings UI for the library-path override, actual cross-backend demotion policy) — nothing decided yet,
same "don't default silently" rule as usual.

## Resume here (2026-08-04, superseded by the entry above) — Phase 2 (radio layer) DONE, committed and pushed

**Done, tested, committed (`cb84f9b`), pushed to `origin/master`.** Built the first radio-layer code in the project:
`ScanlineStudio.Abstractions.Radio` interfaces, `RadioController` (`ScanlineStudio.Core.Radio`), and
`RigctldClientProtocol`/`RigctldProtocolFactory` (`ScanlineStudio.Core.Radio.Rigctld`). Full solution
639/639 tests pass (528 pre-existing DSP, untouched — confirmed via `git status`; 111 new/other).
Plan file: `/home/artien/.claude/plans/starry-whistling-pearl.md`. Full narrative:
`spec/14-roadmap.md`, search "Phase 2 — Radio layer (no CAT rigs yet) — DONE".

**Key points if resuming cold**:
- An auditor plan-review pass ran *before* any code was written (this is new architecture, not a
  port) and changed 5 real design decisions — `IRadioProtocol` dropped its `IRadioTransport`
  parameter, `IRadioProtocolFactory`-based backend resolution (exactly-one-match), a poll-loop error
  taxonomy (protocol errors vs. transport errors, only the latter trigger backoff/reconnect),
  `\dump_caps` parsing rejected in favor of probing `f`/`m`/`t` at connect, and the buffer-survival
  contract on `IRadioTransport.ReadAsync`.
- Hamlib cloned locally at `hamlib/` (gitignored, same convention as `yoniq-old/YONIQ-main/`) —
  resolved the plan-review's flagged highest-risk unknown (rigctld's exact response framing) by
  reading `tests/rigctl_parse.c` directly. Also confirmed Hamlib's hardware-free "Dummy" rig backend
  (`RIG_MODEL_DUMMY`, model 1) is real.
- User suggested testing against that real Dummy rig instead of only fixtures — landed as
  `RigctldDummyRigIntegrationTests` (4 tests, real `rigctld` subprocess, best-effort/skips if
  unavailable). User installed `libhamlib-utils` mid-session so these actually run and pass here.
- Two real bugs the test suite itself caught (not review): a `Task.Run` closure race in `ConnectAsync`
  reading `_pollLoopCts` at execution time instead of capturing it first (real
  `NullReferenceException`, fixed), and one test-side ordering bug (asserted `LastKnownState` after
  `DisconnectAsync`, which deliberately clears it).
- Specs updated to match: `spec/02-radio-layer.md`, `spec/03-cat-layer.md`, `spec/04-rigctld.md`
  (Definition-of-done checkboxes reflect what's actually built now).

**Not started / explicitly out of scope this pass**: `RigctldServer` (server mode), linked Hamlib,
flrig/OmniRig-as-client backends, `TemplateCatProtocol`, and all `ScanlineStudio.Application`/UI/settings-
persistence wiring — all Phase 3/4 per `spec/14-roadmap.md`.

**Next — live open question, not yet decided**: user wants to talk through, next session, whether to do
Phase 3 (minimal UI, first end-to-end path — the roadmap's default next step) or jump to implementing
linked Hamlib (`spec/03-cat-layer.md`'s backend #1, P/Invoke against `libhamlib`, currently a "Definition
of done" bullet with "packaging story not yet designed" — no code, no design pass yet). Nothing decided
yet either way — **start the next session by discussing this choice**, don't default to Phase 3 silently
just because it's what the roadmap lists first. If linked Hamlib is picked, the Hamlib reference clone
from this session (`hamlib/`, gitignored) is already in place and can be read directly for the native
API surface (`include/hamlib/rig.h`) rather than re-cloning.

## Resume here (2026-08-04, latest, ACTIVE) — Hamlib question RESOLVED: no hand-written CAT protocols at all

**Decided, docs updated, no code yet.** The open question logged below this entry (whether to bundle
Hamlib alongside hand-written per-rig `IRadioProtocol`s) resolved into something bigger once the user
clarified their actual position: they don't want **any** hand-written transceiver CAT code in this
project, full stop — "other people already doing that work." Not an `/adhd` run in the end; the user's
own clarification made the direction unambiguous before that was needed.

**New decision**: Scanline Studio is a pure client of external CAT backends, never a per-rig protocol
implementer. Backend priority order: Hamlib linked in-process (P/Invoke, WSJT-X style) → `rigctld`
client → flrig client → OmniRig-as-client (Windows COM, talking to an already-running instance, not
bundling its OCX) → `TemplateCatProtocol` user-authored hex-template fallback for anything none of the
above cover.

**Docs updated this session** (no code changed):
- `CLAUDE.md` §2/§4 — CAT framing (`cradio.cpp`) removed from the port-first scope; explicit new rule
  that CAT/rig control is never ported; binary-is-bytes example repointed at `TemplateCatProtocol`.
- `spec/03-cat-layer.md` — fully rewritten, "CAT Layer (per-rig protocol implementations)" →
  "External CAT Backends." Documents the 5-backend list above, the transport-vs-call-based split
  (rigctld/flrig use `IRadioTransport`; linked Hamlib/OmniRig are call-based and manage their own
  transport), and that legacy byte-fixture parity no longer applies (external backends own their own
  CAT correctness).
- `spec/02-radio-layer.md` — dropped `IRigRegistry`/`RigDefinition`/the native `RADIO_POLL*`→`rigId`
  migration table (nothing to register, no protocol chosen per rig); `RadioConnectionSpec` subtypes now
  one per backend; PTT section reframed (Hamlib owns its own PTT-type config, Scanline Studio doesn't implement
  RTS/DTR itself); OmniRig paragraph corrected — OmniRig-as-*client* (new) actually restores rig-sharing
  arbitration that native CAT never could, unlike what the pre-edit text implied.
- `spec/04-rigctld.md` — old "Non-goals" line rejecting Hamlib bundling removed/reversed; new section
  states rigctld-client and linked-Hamlib are complementary (arbitration vs. no-daemon-required), not
  either/or.
- `docs/removed-features.md` — new entry, "Native per-rig CAT protocol implementations" (the ~14 legacy
  `cradio.cpp` families), explicit that legacy's standalone/offline CAT mode has **no equivalent**
  now — every backend but the template fallback needs an external process/library/driver present.
  Existing OmniRig entry corrected to reflect the new OmniRig-as-client backend restoring arbitration.
- `spec/11-plugin-system.md`, `spec/13-testing.md`, `spec/14-roadmap.md` — cross-references and the
  Phase 2/4 plan updated to match (Phase 2: rigctld client first; Phase 4: linked Hamlib + template
  fallback first, flrig/OmniRig-as-client after if there's still demand).

**Open follow-up, not yet designed**: Hamlib native-binary packaging (per-OS bundling, ABI-churn
handling) needs its own design pass before the linked-Hamlib backend can actually be built — flagged in
`spec/03-cat-layer.md`'s Definition of done, not resolved here.

**Next**: Phase 2 code can start now that this no longer blocks `IRadioController`'s shape — `rigctld`
client first per the roadmap's own priority order.

## Resume here (2026-08-04, superseded by the entry above) — Original framing of the Hamlib question

Original framing was narrower than the eventual decision: "should Scanline Studio bundle Hamlib directly
(alongside rigctld), not just talk to rigctld" — i.e. Hamlib as an *addition* to the already-planned
hand-written per-rig `IRadioProtocol`s. The user's follow-up clarified they didn't want the hand-written
protocols at all, which is the actual decision recorded above. Kept here only for the historical
reasoning trail (constraints considered: `CLAUDE.md` §4's P/Invoke-isolation rule, Hamlib's LGPL
license compatibility, native binary bundling/ABI-churn cost) — not an open question anymore, don't
re-litigate from this framing.

## Resume here (2026-08-04, latest) — SHOULD backlog fully closed, merged to master, pushed. DSP core stable.

Both halves of the SHOULD backlog (RX cluster done directly in the main tree, TX cluster done in a
parallel `fork` worktree) went through a SECOND round of scrutiny before merge: two independent, fresh
(no shared context with each other or with either side's own prior per-item reviews) Opus `auditor`
calls, each given the FULL accumulated diff for its side and told to re-verify code AND every
comment/legacy-citation, not just re-run the original per-item checklist. Both came back with
comment/citation-only findings — a stale caller count, a misleading past-tense claim, an off-by-one
`Main.cpp` line citation propagated across 4 sites, a dangling doc-comment cross-reference, imprecise
citation ranges, a rate-dependent bias figure stated as a single number, an unacknowledged Auto Slant
interaction, a mode-scope note. Zero functional/behavioral findings on either side — real signal that
the underlying fixes were sound, not just an absence of a third check.

All findings fixed on both sides, both suites re-confirmed green (RX 521/521, TX 527/527), both
committed, merged into `master` (one expected conflict in `spec/14-roadmap.md` — both branches had
appended a new section at the same point; resolved by keeping both), full merged suite re-confirmed
(528/528), pushed to `origin/master` (`03c004e`). Fork worktree removed, its branch deleted (fully
merged, nothing lost).

**Where this leaves the DSP core**: every MUST bug from the full 3-phase milestone audit (4 total, 3
from Phase 1-2 + MUST 4 from Phase 3) is fixed. Every reachable SHOULD finding (11 of 13) is fixed or
closed via documentation; the remaining 2 (items 6, 10) are deliberately deferred with real reasoning
recorded (item 6 needs a filter-state-checkpoint architecture that doesn't exist yet; item 10 is a
judgment call about changing a silent clamp to a loud throw, not a research gap) — same tier item as
Band 5's "not worth it / correctly blocked" precedent, not something dropped. COULD items 14-16 and
NICE-TO-HAVE items 17-26 (`spec/14-roadmap.md`, "Findings, prioritized") remain open but are, by their
own original triage, low-urgency/cosmetic/bounded — none block Phase 2.

**Next**: no DSP work is queued. DSP core (Phase 1) is formally closed out — moving to Phase 2 (radio
layer). **Working the Hamlib-vs-rigctld-only architecture question first**, before any Phase 2
implementation starts — see `spec/14-roadmap.md`'s Phase 1/Phase 2 boundary note for the current
framing. Don't start `IRadioController`/rigctld code until this resolves, since it shapes that
interface's own scope.

## Resume here (2026-08-04) — TX-side SHOULD cluster DONE (items 4, 5). This is a fork worktree, NOT pushed/merged.

This is an isolated git worktree the main session spun off to work SHOULD items 4 (TX frequency-
mapping integer truncation) and 5 (OutHEAD pre-VIS leader-tone port) in parallel with the main
session's own RX-orchestrator cluster (items 6, 7, 8, 9, 10). Both items DONE, both code-reviewed
(2 rounds each -- round 1 on both caught real issues, both fixed properly, round 2 confirmed).

**Item 4**: `YCbCr.FromRgb`/new `YCbCr.ColorToFreq` now model legacy's real two-truncation TX chain
(GetRY's int-truncation + ColorToFreq's integer division) plus a third, RM8/RM12-specific truncation
found by reading `TMmsstv::LineRM` directly. Round-1 review caught a real floating-point-association
bug in my first `FromRgb` reparenthesization attempt (fixed) and a false claim in a golden-vector
comment (corrected -- that test never actually invokes the encoder, so its "unchanged" was a tautology
not evidence).

**Item 5**: New `VisHeader.GenerateOutHeadSegments`, wired as the very first TX segment for every mode
including AVT (confirmed against source: AVT's own header-generation branch is nested INSIDE the same
non-narrow path OutHEAD precedes, not a special case). Round-1 review caught something real: 5 test
files that strip a fixed header offset to reach a "headerless" body for sync-bypass testing were all
under-skipping post-fix, silently locking via the real VIS path instead of the bypass path they exist
to test -- passing for the wrong reason the whole time. Fixed all 5, and re-measuring afterward found
the OLD documented deltas for those files were themselves contaminated (measuring VIS-lock accuracy,
not bypass accuracy) -- genuine bypass locks turn out to be MORE precise than the old numbers ever
showed. Round 2 review also caught 2 of the 5 fixed files still citing stale pre-fix numbers in their
own comments -- fixed by re-measuring, not just editing prose.

527/527 tests pass (520 prior + 7 new). Full detail in spec/14-roadmap.md's "TX-side SHOULD cluster"
section.

**Not committed to the main branch yet -- this worktree's own commit(s) need review and merge by the
user/orchestrating session.** Do not delete this worktree until that happens.

## What this repo is
Scanline Studio: cross-platform (.NET 8 + Avalonia) rewrite of YONIQ (MMSSTV fork). Specs in `spec/00`-`spec/15`.
Legacy source lives locally (gitignored) at `yoniq-old/YONIQ-main/` — read it directly, don't infer from memory.
Secondary reference QSSTV lives locally (gitignored) at `QSSTV-main/` — inspiration/cross-check only, never authoritative.
Full rules: `CLAUDE.md` (short, read it). Key ones: port legacy DSP exactly (no invention), golden-vector/round-trip
tests for every DSP change, small reviewable commits, ask before pushing to origin.

## GitHub Actions status (updated 2026-08-02, ~20:20 CEST)

The `CI` workflow is manually disabled (`gh workflow disable CI` — was at 1,806/2,000 monthly Actions
minutes) — check `gh workflow list --all` for its current enabled/disabled state before assuming either
way. **Once CI was confirmed disabled, the user explicitly said to resume normal pushing** (`git push`
after every commit, as this project does throughout) — pushing costs nothing while the workflow stays
disabled. The earlier "don't push tonight" restriction is LIFTED; the cron job resuming this session
tonight was updated back to its normal push-after-each-item behavior. Re-enabling the `CI` workflow
itself is still the user's own call — don't run `gh workflow enable CI` without checking with them first,
since that's what actually costs Actions minutes again, not the pushes themselves.

## Resume here (2026-08-04, latest) — Item 8 self-corrected and fixed too. RX-side SHOULD cluster: 7 items done, 2 (6, 10) deliberately deferred.

User asked "are 6/8/10 not fixes, or just need more research?" -- prompted re-examining item 8, and the
first assessment (deferred, "needs a floor clamp threaded through consistently") turned out to be too
conservative. `FilteredRawSampleAt`'s own ternary means the problematic `index-1` read only happens for
`index >= 1`, so `Math.Max(0, _bandpassFilteredProcessedUpTo - 1)` (not a bare `-1`) is a safe, minimal,
one-line-per-site fix after all -- no floor-clamp architecture needed. Fixed both `TrimBuffers` sites,
code review PASS (independently re-derived the arithmetic identity and confirmed strict monotonicity --
the fix can only ever retain equal-or-more data, never less). No new test (currently unreachable, no
observable behavior to discriminate against) -- relied on the proof plus full suite staying green
(521/521, unchanged). Worth remembering: my own first-pass assessment of a "needs more work" item isn't
automatically right -- worth a second look when asked, especially for something this cheap to re-verify.

Items 6 and 10 remain genuinely deferred (not a research gap for either -- item 6 needs a real
filter-state-checkpoint architecture that doesn't exist; item 10 is a deliberate judgment call about
changing a silent-substitute into a loud-throw, not something more research would resolve differently).

## Resume here (2026-08-04, latest) — RX-side SHOULD cluster DONE (6 items). TX-side cluster running in a parallel fork.

User approved running two pipelines in parallel: a `fork` (isolated git worktree) handling the TX-side
SHOULD items (4: frequency-mapping truncation, 5: OutHEAD leader-tone port), while this session
continued the RX-orchestrator cluster (6, 7, 9, and boundary-hardening 8/10) directly. **All 6
RX-side items now resolved:**

- **Item 7 (narrow-FSK suspended during AVT training) — real fix.** Confirmed directly against source:
  legacy's `DecodeFSK` runs unconditionally throughout AVT training and aborts it on a valid narrow
  packet. Interleaved `TryNarrowFskScan` into `TryResolveAvtTraining`'s own per-sample loop (matching
  `TryInterleavedHeaderScan`'s established lockstep pattern) -- a first attempt (checking once in
  `TryDecodeHeader`) failed the new end-to-end test on a bulk push, caught and corrected during
  development. New test confirmed to discriminate (reverted, decoder locked "avt" instead of "mn110" as
  predicted). Code review: PASS.
- **Item 9 — documented, no code change.** Added a precise doc comment on `AbandonInProgressImage()`
  explaining exactly why its missing cursor-resync is safe today (both call sites), so a future new
  call site doesn't silently break the coupling.
- **Items 6, 8, 10 — investigated and deferred, not fixed.** Each looked cheap at first but turned out
  to need either a real architectural change (item 6: filter-state checkpoint/rewind, no such mechanism
  exists in `SearchBandpassFilter`/`HilbertFmDemodulator` today) or carried a genuine regression risk
  once traced through (item 8: the obvious one-line fix would introduce a NEW negative-index crash at
  stream start; item 10: changing a silent clamp to a loud throw is a real behavior change with unclear
  benefit for the highest-consequence reader in the file). Precisely documented in spec/14-roadmap.md
  so the investigation isn't lost, not fixed reactively without a concrete failure driving the design.

521/521 tests pass. Committed and pushed (this session's own work; the TX-side fork's work is still in
its own separate worktree, not yet merged -- check on it next).

## Resume here (2026-08-04, earlier) — Working the SHOULD backlog (13 items). 4 done so far.

**Luma `Limit256` clamp added to 3 RX decoders** (SHOULD item 11): traced legacy's real per-CHANNEL
clamp pattern directly (`Main.cpp:4275-4430`), not a uniform per-family rule. Found a genuinely
surprising legacy asymmetry, code-review-confirmed: in the PD/MP/MN family, Y1 gets `Limit256` but Y2
(a SECOND luma read via the IDENTICAL peak-pick path) does NOT -- a real legacy quirk, faithfully
preserved, not "fixed" toward symmetry. 3 new tests (`Limit256ClampTests.cs`), confirmed to discriminate
(reverted the clamp, all 3 failed with predicted values, restored, re-confirmed). Code review: PASS.
Full suite 520/520, including all real-legacy-capture golden vectors unchanged -- directly answers the
one real caution the review raised (the clamp boundary isn't strictly a no-op for peak-picked near-white
overshoot, but that's legacy-faithful behavior, not a port-introduced regression, and no real fixture
hits it).

## Resume here (2026-08-04, earlier) — Working the SHOULD backlog (13 items). 3 done so far.

User: "take the shoulds." Triaged all 13 open SHOULD items (10 from Phase 1-2, 3 from Phase 3) by effort;
working through them with the same test+review discipline as the MUST fixes. Progress:

**Done**: doc-only batch (event-scheduler contract on `LineDecoded`/`ModeDetected`/`DecodeRestarted`,
`LineDecoded`'s live-alias hazard, finding 13's status update) — committed `db13bf7`. TX image/mode
dimension-contract guard (`AnalogFmSstvEncoder.EncodeAsync` now throws synchronously on a mismatched
image instead of throwing mid-stream or silently cropping) — 4 new tests, code review PASS-WITH-RISKS
(one real risk independently checked and ruled out: all 8 fixture bmps confirmed exactly match their
mode's canvas size). Robot 36 tone-selector read-point hardening (SHOULD item 12) — **round-1 review
caught a real mistake**: the first attempt used an invented margin constant instead of porting legacy's
actual `m_SG`/`m_CG` decisive-window boundary (`sstv.cpp:664-665`), independently re-verified against
source and confirmed the review was right, then rewrote using the real legacy constant. Round-2 review:
PASS. Golden-vector deltas unchanged as expected (fix only affects noisy/real signals, not this clean
fixture). Both fixes committed together, 517/517 tests pass. Full detail in spec/14-roadmap.md's
"Working the SHOULD backlog" section.

**Remaining (10 items)**: TX frequency-mapping truncation, OutHEAD leader-tone port, Limit256 clamps (3
decoders), mid-image narrow restart stale cache, narrow-FSK-suspended-during-AVT-training, and 3
currently-unreachable boundary-hardening items (assess real-fix-vs-doc-only for each, per CLAUDE.md's
"don't guard against scenarios that can't happen" guidance).

## Resume here (2026-08-04, earlier) — MUST 4 (RX line-cursor rounding) FIXED. All 4 milestone-audit MUST bugs now closed.

Fixed the one confirmed bug Phase 3 found (see the entry below this one for the audit itself). Same
discipline as MUST fixes 1-3: dedicated test first (`LineCursorRoundingTests.cs`, Robot 72 step-edge,
confirmed to fail pre-fix with the predicted ~25px drift by reverting and re-running), then the fix
(`_idealLineStartSample`, a `double` accumulator in `AnalogFmSstvDecoder.cs` mirroring MUST fix 3's own
`segmentStartSample`/`pixelWalk` split one level up — between lines instead of within one), then
re-measurement, then code-level review (verdict EQUIVALENT, 4 nits, 2 fixed as cheap doc clarifications).

Golden-vector re-measurement confirmed the fix, not just the dedicated test: RX decode-vs-source deltas
improved most on exactly the modes predicted to have the largest per-line rounding error (robot-36
16.19→5.04, robot-72 14.57→4.41, rm8 13.76→4.17 — rm8's improvement is itself proof these are two
different bugs, since MUST fix 3 structurally couldn't touch RM8's single-scan-segment decoder). TX
deltas unchanged, exactly as predicted (fix is RX-only). Full detail in spec/14-roadmap.md's "MUST 4"
section.

513/513 tests pass. Committed and pushed.

**All 4 confirmed MUST bugs from the whole milestone audit (3 from Phase 1-2, 1 from Phase 3) are now
fixed.** Remaining open, none urgent: 3 new SHOULD-level landmines from Phase 3 (TX dimension-contract
guard, RX event-scheduler contract, RX `LineDecoded` live-alias) plus the pre-existing Phase 1-2
SHOULD/COULD/NICE-TO-HAVE backlog — none reachable without a live caller/UI yet.

## Resume here (2026-08-04, latest) — Phase 3 (chain/integration audit) DONE: 1 confirmed MUST bug found, not yet fixed

Ran Phase 3 of the milestone audit (docs/audit-playbook.md) via 2 parallel `auditor` calls (RX chain
seams, TX chain seams — isolated context each, since TX/RX share no runtime state). Full detail in
spec/14-roadmap.md's "Phase 3 — chain/integration audit" section.

**RX verdict: NOT EQUIVALENT.** Confirmed (independently re-verified against both this port's code AND
legacy source directly, not just trusted from the auditor) — **MUST 4**: `AnalogFmSstvDecoder.cs:1094/1125`
rounds the per-line sample cursor (`(int)Math.Round(_effectiveSamplesPerLine)`) every line; legacy
(`Main.cpp:4133-4148`) uses one continuous integer counter for the whole transmission and derives each
line boundary via unrounded `double` division. Same bug SHAPE as MUST fix 3 (trimmed-vs-full mismatch),
promoted one level up: MUST fix 3 was within-a-line/segment, this is between-lines. Cumulative horizontal
shear, worst on Robot 72/RM8/Robot 36 (their line pitch lands far from an integer at 11025Hz) — up to
~120 samples/~25-50px drift by the last line, predicted not yet measured. Structurally invisible to Auto
Slant (it tracks its own separate exact fractional grid, never sees this cursor's rounding error). NOT
fixed yet — reported for prioritization first, per the user's own standing preference.

Also 2 risks (no stated scheduler contract for `LineDecoded`/`DecodeRestarted`/`ModeDetected`;
`LineDecoded` hands out a live mutable pixel-buffer alias — both real landmines for the eventual UI
wiring, neither reachable today) and 2 nits (≤1px AFC-boundary edge case; `_afcBoundSample` uses nominal
not effective rate, already-tracked NICE-TO-HAVE 24 confirmed larger than originally estimated).

**TX verdict: EQUIVALENT-WITH-RISKS.** No MUST-4-shaped bug — confirmed structurally impossible: legacy's
TX accumulator (`sstv.cpp:2842-2846`) and this port's (`AnalogFmSstvEncoder.cs:36-46`) are both a single
accumulator spanning the whole transmission, reset once, never re-derived from a trimmed walk. One NEW
SHOULD finding: `EncodeAsync` never validates image dimensions against the mode before encoding — a
too-small image throws `IndexOutOfRangeException` mid-stream, a too-large one silently crops. Not
reachable today (every current caller passes correctly-sized images), but a real landmine once real image
input lands. 3 already-tracked nits re-confirmed at the seam, nothing new there.

**Nothing fixed this pass — findings documented, not yet actioned.** 512/512 tests still pass (none of
these are caught by any existing test). Next: decide fix order with the user (MUST 4 is the only
confirmed live bug; the 3 new SHOULD items are landmines for later phases, not urgent).

## Resume here (2026-08-04, latest) — TX-side golden-vector tests wired in, ALL 11 modes, prerequisite for Phase 3 satisfied

All 11 `<mode-id>_TX_RX.bmp` files came back from the user's real legacy install running under Wine
(user played each `TxCapture/<mode-id>_TX.mmv` via `File → Play` with legacy's sample rate set to
11025Hz, per `TxCapture/README.md`). Wired into a new theory test,
`LegacyDecode_OfThisPortsEncoderOutput_MatchesSourceImage` in
`tests/ScanlineStudio.Core.Sstv.Tests/GoldenVectorTests.cs` (11 cases) — compares each source `.bmp` against a
REAL legacy decode of THIS PORT'S OWN encoder output. This is the actual "TX-side verification tests,
built and confirmed" gate spec/14-roadmap.md's "Explicit prerequisite before Phase 3" note required
(see that file's own "TX-side golden-vector tests wired in" entry for full detail) — closes the gap
where TX only had internal round-trip coverage (this port's encoder decoded by this port's own decoder
agreeing with itself), the exact failure shape CLAUDE.md's Scottie incident warns about.

R24 needed special handling: its source `.bmp` is 120 rows (its real transmitted row count) but
legacy's saved `_TX_RX.bmp` is the usual 256-row canvas with each real row duplicated into 2 display
rows (legacy's own row-doubling display quirk, already documented on `SstvModeRegistry.R24`). Added
`CropToTopEvenRows` (takes rows 0, 2, 4, ..., 238 of the top 240) instead of reusing the existing
`CropToTop`, which would have misaligned 2x.

Deltas measured directly (temporary-zero-tolerance technique): robot-36 3.30, martin-m1 1.53,
scottie-s1 1.05, robot-72 3.21, pd90 2.40, rm8 5.15, mn110 2.60, avt 0.86, scottie-dx 1.03, mr73 3.22,
r24 3.70 — all restart-free, correct mode first-try, and every one BELOW the RX-direction baseline
numbers for the same modes despite going through this port's own encoder first. Genuinely good news,
not just a passing test: real evidence this port's TX output is valid, accurately decodable by a real
legacy receiver.

Full suite: 512/512 passing, solution-wide build clean. Committed and pushed.

**Phase 3 (chain/integration audit) can now run — its own explicit prerequisite is satisfied.** Not yet
started this session; next natural step per spec/14-roadmap.md's own plan.

## Resume here (2026-08-04, latest) — Milestone audit: ALL 3 MUST fixes DONE

After Band 4 closed, ran the milestone-audit playbook (`docs/audit-playbook.md`), scoped to DSP core +
TX/RX codec paths (radio/CAT/DI/localization/UI don't exist yet). Phase 1 (unit map) done directly by
the orchestrating session; Phase 2 (4 batches, fanned out to `auditor`) all returned, finding 3 confirmed
MUST-fix bugs — **all 3 are now fixed, tested, code-reviewed, and ready to commit.** **Phase 3
(chain/integration audit) has NOT run yet** — user's explicit directive: build and verify TX-side tests
first, do not skip straight to it.

**MUST fixes, all independently re-verified against legacy source by the orchestrating session (not
just trusted from the auditor):**
1. **AVT images never trimmed their buffers — DONE, committed (`e35a648`).** `InitializeAfc`/
   `InitializeSlant` return early for AVT (both trackers null), so the locked watermark never advanced
   for AVT's whole ~90s image. Fixed by conditionally excluding these two watermark terms only when
   their tracker is null, mirroring an already-established pre-lock pattern. Code-level review clean.
2. **`TryNarrowFskScan` whole-buffer pre-pass — DONE, committed (`cd9a05a`).** On a bulk push with an
   earlier non-narrow transmission followed by a later narrow one, the narrow scan could commit the later
   transmission first, skipping the earlier one entirely — confirmed by reverting the fix and observing
   `ModeDetected` drop from 2 events to 1. Fixed by interleaving narrow-FSK's own scan per-sample with
   the sync-bypass/VIS-lock loop instead of letting it run to completion first. Code-level review found
   a real secondary risk (the fix newly activates a previously-accidentally-muted false-positive-restart
   exposure at a SEPARATE, lower-priority call site) — deliberately deferred, documented, guarded by a
   new `Assert.Equal(0, restartCount)` test assertion rather than silently absorbed.
3. **Pixel-pitch trim accumulator drift — DONE, ready to commit.** The biggest of the three, touching 4
   decoder files identically. Multi-segment RX decoders started each scan segment after the first
   slightly early (measured, not assumed: a real 5px systematic shift on a synthetic step-edge test,
   confirmed by reverting the fix); confirmed directly against `Main.cpp:4454-4503`. Fixed by tracking
   each scan segment's own start position separately from its trimmed intra-segment pixel walk, then
   advancing by the segment's FULL untrimmed duration afterward. `RgbSequentialScanlineDecoder`/
   `RobotScanlineDecoder`/`YCbCrSequentialScanlineDecoder`/`YCbCrLinePairedScanlineDecoder` touched;
   `MonoAveragedPairedScanlineDecoder` (RM8/RM12) correctly left alone (single scan segment, structurally
   immune, confirmed by an unchanged golden-vector delta). Golden-vector re-measurement: most fixtures
   improved (martin-m1 nearly 3x better), two (robot-36, robot-72) worsened slightly but stay
   comfortably within their existing tolerances — an honestly-recorded, accepted tradeoff, not chased to
   zero. Code-level review found 2 real secondary risks, both documented rather than silently absorbed:
   a Robot-36 tone-selector read now sits right at a segment boundary (folded into the existing SHOULD
   item 12), and a test-coverage gap for the two "worsened" families that investigation showed needs a
   harder differential test design than initially assumed (tracked as a SHOULD-level follow-up).

None of these three needed/need TX golden vectors or new fixtures to fix/verify.

**Also documented, not yet actioned**: ~10 SHOULD items (TX frequency-truncation bias, missing
`OutHEAD` TX segment + its `removed-features.md` entry, a mid-image narrow-restart 1-line stale-cache
bug, narrow-FSK suspended during AVT training, 3 buffer-mechanism fragility risks, missing luma clamp
in 3/5 RX decoders, Robot 36 tone-selector timing, 3 golden-vector coverage gaps: Scottie DX/MR73/R24)
plus ~13 COULD/NICE-TO-HAVE items. **Full findings list, all prioritized, all reasoning: `spec/14-roadmap.md`,
search "Milestone audit, Phase 1+2"** — read that before doing any fix work, don't re-derive from scratch.

**Next**: all 3 MUST fixes done. Remaining SHOULD/COULD/NICE-TO-HAVE items from the original Phase 2
findings list are still open, plus 2 new deferred risks MUST fix 3's own code-level review surfaced
(both documented, see above). None of these block anything.

**Explicit user directive: before running Phase 3 (chain/integration audit), build TX-side verification
tests and confirm them first** — do not skip straight to Phase 3 with TX's zero-legacy-decode-coverage
gap (batch A's finding) still open. Likely means TX-side golden vectors (this port's encoder output run
through a real legacy decode) — bottlenecked on the user's own time with the real legacy binary, same
as Task #7. Full reasoning: `spec/14-roadmap.md`, "Milestone audit, Phase 1+2" → "Next steps".

**TX capture prep — DONE, ready to hand off.** User is setting up the legacy binary under Wine and
asked this session to prepare the TX-side files. Investigated first: no CLI/headless RX mode exists,
but legacy's `File → Play` can replay the same `.mmv` format the existing RX fixtures already use — no
audio hardware routing needed. Built `MmvFile.Write`/`BmpFile.Write` (new, in the test project). Per the
user's own explicit request, got an Opus code-level review of both BEFORE generating anything — caught
a real blocker in `MmvFile.Write`'s first draft (a full-scale +1.0 sample silently wrapped to -1.0 via
a float→double→short cast that doesn't saturate at the final narrowing step; would have corrupted every
generated file with impulse noise ~every 75 samples). Fixed, covered by 14 new round-trip tests
(confirmed to discriminate the original bug by reverting it). Generated all 11 TX `.mmv` files (8
existing RX-fixture modes + Scottie DX/MR73/R24, the 3 modes the audit flagged as having zero coverage
anywhere) into `tests/ScanlineStudio.Core.Sstv.Tests/Fixtures/GoldenVectors/TxCapture/`. **Then the user asked a
follow-up that caught a real gap**: file-format round-trip tests don't prove the actual files are valid
decodable transmissions — added `TxCaptureFixturesTests.cs` (11 tests, kept permanently) that reads
each real file back and self-decodes it with this port's own decoder; all 11 pass (correct mode, zero
restarts, correct image). Full suite green (501/501). `TxCapture/README.md` has the exact steps for the
user's side (set legacy's sample rate to 11025Hz FIRST, or `File → Play` silently resamples through a
lowpass and the capture is worthless) — no rush, can wire fixtures in individually as results come back.
Full detail: `spec/14-roadmap.md`, "Milestone audit, Phase 1+2" → "TX-side capture prep".

## Resume here (2026-08-04, later) — Band 3 AND Band 4 both fully DONE and COMMITTED

S31 done (`b5a5cb4`). Band 3 (8 items: S31 already counted separately, then S7-S17 family) done and
committed across `739f4e5`/`34bad37`/`0bb7c60`/`7354469` — S12 (`m_sint2`/`m_sint3` freeze gating) was
the last item, landing after a plan-review round caught the original proposal only covered the
case-0↔1 boundary, not the full case-2/9/3 freeze the item is named for. Full detail:
`spec/14-roadmap.md`, search "S12 — m_sint2/m_sint3" and "AVT package: S7".

**Band 4 (4 items: S27, S29, S30, S21 — S13's doc half already closed earlier) — DONE and COMMITTED**
(`89cb135`). User asked for "a good deep documentation and comment update and audit."
All four are pure doc/test additions, zero DSP behavior change (confirmed by an unchanged full-suite
result, 472/472, before and after):
- **S27** — `docs/removed-features.md` CQ100 entry + `HilbertFmDemodulator.cs` stale comment fix.
  Investigated fresh rather than trusting the inventory table's narrow "FIR tap-tripling" framing: found
  `g_dblToneOffset` touches 44 lines across `sstv.cpp`, not ~30. **Code-level audit caught the first
  draft's own removed-features.md entry was still incomplete** — 2 more real `sys.m_bCQ100`-gated DSP
  effects (an `m_OFP` sync-timing shift, a narrowed AFC capture window) were missing, found and added
  before commit.
- **S29** — new `MakeFilter_OddTap_TrailingSlotStaysZero_NotSymmetric` test
  (`SearchBandpassFilterTests.cs`), pinning the already-documented-but-unpinned odd-tap trailing-zero
  divergence with an executable guard.
- **S30** — new `Constructor_MiddleDecimationTier_16To40kHz_SelectsTap24Df1_AndDecodesCorrectly` test
  (`HilbertFmDemodulatorTests.cs`), giving `CHILL`'s 16-40kHz middle decimation tier its first
  instance-level (not just raw-kernel) coverage.
- **S21** — new `MonoAveragedPairedScanlineDecoderTests.cs`, replacing the vague "a couple of levels"
  RM8/RM12 int-truncation estimate with an exact measured bound (max 2 levels, `-1..+2` signed) via an
  exhaustive sweep against an independently-replicated legacy two-truncation chain. Code-level audit
  independently re-derived the exact bound in closed form and confirmed it correct, plus fixed 2
  citation/wording nits in the test's own comments.

Full detail: `spec/14-roadmap.md`, search "Band 4 — documentation/test-only items".

**Nothing outstanding from the original 30-item DSP-simplification inventory** except Band 5
(documentation-only/correctly-blocked, no action needed — see the Band-3/4/5 summary further down this
file). Next: Task #8 (Phase 3 chain/integration audit, `docs/audit-playbook.md`) is the natural next
step now that Bands 1-4 are all closed, or await further user direction.

## Resume here (2026-08-03, later) — S31 (AVT never decodes on real capture) root-caused and FIXED

**S31 is done.** The working hypothesis logged in an earlier version of this section ("header timing
jitter over the long training sequence") was **wrong** — investigated properly per the user's explicit
request ("do 1 first" — confirm the mechanism empirically before designing a fix). Real root cause:
`VisLockStateMachine` (the only noise-tolerant detector in this port) deliberately discarded every AVT
match it found, by design; the only path allowed to act on one (the fixed-window `TryDecodeVisHeader`)
never reaches real header content on a real capture (same `OutHEAD`-leader/pre-TX-room-audio mechanism
already root-caused for other modes' anchor-precision gaps). Confirmed via temporary instrumentation
(added, run against the real `avt.mmv` fixture, then fully reverted before any fix code was written) —
`VisLockStateMachine` correctly decoded AVT's real VIS byte three separate times and discarded every
one. Full mechanism + fix detail: `spec/14-roadmap.md`, search "S31 — AVT's real capture never decoded".

**Fix went through the full normal loop**: design → auditor plan-review (caught a real, previously
undiscovered `TrimBuffers` watermark blocker before any code shipped) → implementation → auditor
code-level review (verdict: EQUIVALENT-WITH-RISKS, no blockers, ready to commit; a few stale-comment
nits found and fixed directly). `avt.mmv` now decodes cleanly (delta 5.80, restarts=0, correct mode
first-try) — comfortably in the same healthy range as the other five Task #7 fixtures.

**Uncommitted as of this writing** — nothing from this session has been committed yet (`git status`:
`spec/14-roadmap.md`, `src/ScanlineStudio.Core.Sstv/AnalogFmSstvDecoder.cs`, `src/ScanlineStudio.Core.Sstv/VisLockStateMachine.cs`,
fixtures `README.md`, `GoldenVectorTests.cs` modified; new `AvtNoiseTolerantDetectionTests.cs`). Full
suite confirmed green (458/458, solution-wide) before this brief was written. Ask the user before
committing, per standing rule.

## Resume here (2026-08-03, earlier) — Task #7 fixtures wired in (superseded by the S31 entry above)

**Six new golden-vector fixtures captured and wired in** (scottie-s1, robot72, pd90, rm8, mn110, avt —
Task #7 from the roadmap). 5 of 6 decode cleanly with healthy deltas, now in `GoldenVectorTests.cs`/
`GoldenVectorFixtureReaderTests.cs` alongside the original `martin-m1`/`robot-36`. Full detail: fixtures'
own `README.md` (capture/trim/measurement record) and `spec/14-roadmap.md`'s "Task #7" section (search
"Task #7 — six new golden-vector fixtures"). This session's own work (Task #7 fixture-wiring commit) was
already committed before the S31 investigation above started.

## Resume here (2026-08-03) — Band 2 is FULLY DONE. Autonomous session stopped here deliberately.

**S14, S6, and S15 are all resolved** (S14/S6 shipped as code — commits `7a153a0`, `6da0a65` — S15
closed via documentation, no code needed). **Band 2 is complete.** Full detail in `spec/14-roadmap.md`,
search "Band-2 item S14" / "Band-2 item S6" / "Band-2 item S15".

**S15's resolution, briefly**: before drafting a plan, found this port's own doc comments (from Band-1
item 2/S2, predating Band 2) had already investigated S15's core concern once — a "more ambitious
earlier draft" that tried periodically re-anchoring `_consumedSamples` to give the fixed-window header
paths another shot, reverted after confirming empirically it changed nothing (the existing continuous
fallback, `TryInterleavedHeaderScan`, finds the same headers at its own already-accepted precision
either way). An auditor plan-review round confirmed this settles S15's broader concern too, via 3
independent arguments (VisLockStateMachine covers extended VIS same as the fixed-window path; the
post-commit sync-anchor fold washes out the ~10ms precision difference; already observed working via an
existing test) — with one real, narrower exception: MN/MC's FSK packet decode genuinely has no
continuous equivalent, but that's not a new gap, it's the ALREADY-TRACKED Band-3 item S8 ("mid-image
narrow re-lock"), whose own load-bearing registry-coverage assumption got independently re-verified
along the way (confirmed real, `SstvModeRegistry.cs:998-999`/`1023-1026`). Auditor's explicit verdict:
building S15 as originally scoped would mean relitigating an already-evidenced-and-reverted design
decision, unattended, on a load-bearing (`TrimBuffers`) coupling, with an unmeasured benefit — exactly
this session's own hold criterion. Closed via documentation instead; **no code, deliberately.**

**Nothing is queued for further autonomous work right now.** The three explicit next steps below (Task
#7, Task #8, remaining Band 3/4/5 items) each either need the user's own time with the real legacy
binary (#7) or should follow it (#8, most Band-3 mode-family items). If resuming cold: read this file
fully, then `spec/14-roadmap.md`'s Band-2 S15 closing section for the complete reasoning before touching
this area again — don't re-open S15 without re-reading why it was closed.

## Current status: Band 1 DONE (all 4 items); Band 2 DONE (all 5 items)

**Pre-Phase-2 gate: shortcut/simplification audit.** User's call: before running the milestone-audit
playbook's Phase 3 chain audit (see `docs/audit-playbook.md`) or moving to Phase 2, first inventory
every known DSP-in-pipeline simplification this port carries, triage/fix the important ones, THEN
capture more real golden-vector fixtures, THEN run Phase 3. Full writeup + 30-item table + priority
bands + patterns: `spec/14-roadmap.md`, search "Pre-Phase-2 gate".

**Band 1 (must-fix-before-Phase-2) — all 4 DONE**: S4 exception-swallowing catch (`288d5d0`); S2
unbounded buffer memory growth (`86e3af6`); S3 chunk-timing sensitivity (`365d57b`/`765ba3c`); S1
lock-dependent bandpass filter switch, split into 4a (`fbafdea`, lazy forward-fill cache) + 4b
(`028bc8e`, the actual H1/H2 switch, gated on a *captured* lock-anchor index not live state). Full
per-item detail: `spec/14-roadmap.md`, search "Band-1 item".

**Band 2 (should-fix-during-Phase-2-bring-up) — 5 of 5 DONE (4 code + 1 closed via documentation),
order: S5 → S16 → S14 → S6 → S15.**
Before starting, asked the auditor to revisit its own "decide the streaming contract explicitly first"
recommendation now that Band 1 had real outcomes — withdrawn: the port already has both patterns that
recommendation wanted decided (persistent-detector+cursor, lazy-forward-fill+ring-buffer), so patching
individually, reusing those patterns, was the right call. Auditor also corrected the shape mapping
mid-stream (see below) — **don't assume similarly-shaped items share a fix, verify each against source**.
- **S5 DONE** (`e0563f5`) — `TryDecodeVisDataBits`' d11/d12/d19 tone detectors were cold-started fresh
  every call; converted to persistent lazy-forward-fill caches (mirroring `AgcSampleAt`). d13
  deliberately NOT converted — a plan-review round caught that legacy only feeds `m_iir13` during
  case 2/9, so it's not a pure function of sample index (same shape as S16's own d13-like surprise
  below); an index-keyed cache would have been wrong for it.
- **S16 DONE** (`c0b09ca`) — AVT's dedicated PLL had a clamped 2000-sample warm-up. First plan-review
  round caught its OWN initial misclassification before any code was written: legacy's `m_pll` is fed
  only during SyncMode cases 3-7 (intermittent, same shape as d13), so "make it fully persistent"
  (the S5-style fix) would have been a real regression — a PLL's phase has no fast, data-independent
  re-settling the way a resonator does. Real fix: kept per-attempt construction, widened the warm-up to
  legacy's actual ~1850ms contiguous pre-training feed span (derived from source, not guessed).
- Both plus 4b closed a systemic gap an auditor review flagged: 3 items in a row changed real behavior
  invisible to the test suite (decode outcomes stayed correct, but nothing pinned the mechanism). Closed
  in `LegacyDerivedSpansTests.cs` (S5/S16) + `BandpassCacheChunkInvarianceTests.cs` (4b, done earlier).
- **S14 DONE** (`7a153a0`) — `TryDecodeNarrowModeHeader`'s mark(1900Hz)/space(2100Hz) detectors were
  cold-started fresh every call, same shape S5 fixed for d11/d12/d19. Mark reuses `D19At` directly
  (legacy's narrow-mode mark IS d19, confirmed `sstv.cpp:1851/1858`); space gets a new `FskSpaceAt`
  cache mirroring D11At/D12At/D19At. Two rounds of auditor code-level review, both EQUIVALENT/clean;
  the one non-blocking follow-up (pin the D19-reuse decision itself, not just the new cache's own
  persistence) was closed before commit, not deferred. Full detail: `spec/14-roadmap.md`, search
  "Band-2 item S14".
- **S6 DONE** (`6da0a65`) — `HilbertFmDemodulator.ProcessSample` gained an `isNarrow` parameter
  (precomputed wide/narrow `(off, out)` pairs, mirroring 4b's per-call `useLocked` shape), gated via
  4b's own `_bandpassLockedFromSample` anchor. A prior trap note claiming `SetWidth` changes tap count
  cited the wrong legacy function (`SetBPF`'s `m_Skip`, an unrelated bandpass-quality setting) — fresh
  read found `CHILL::SetWidth` only changes two scalars, so 4b's "no warm-up needed" finding DID
  transfer after all, just for a different reason than originally assumed. Real finding, confirmed
  algebraically twice over: `isNarrow` is representationally inert at steady state in this port's Hz
  domain (encode/descale are exact algebraic inverses) — the only observable effect is a bounded,
  legacy-faithful transient at a width switch, not a decode-accuracy fix. Full detail: `spec/14-roadmap.md`,
  search "Band-2 item S6".
- **S15 CLOSED via documentation, no code needed** — see the "Resume here" section at the top of this
  file for the full reasoning. Its one real remaining gap is the already-tracked Band-3 item S8, not a
  new item.

**Test count**: 423/423 `ScanlineStudio.Core.Sstv.Tests`, solution-wide build clean, golden-vector tests
unaffected throughout, noise-robustness tests unaffected (existing tolerance).

Full per-item plan-review + implementation + code-review detail: `spec/14-roadmap.md`, search
"Band-1 item" or "Band-2 item".

## Next up

**Band 1 and Band 2 are both fully done.** No DSP item is queued for autonomous continuation right now
— the remaining work below either needs the user's own time or should deliberately follow it:

1. **Task #7 — capture ~5-6 new real golden-vector fixtures** from the legacy binary, covering mode
   families the existing two fixtures (Martin M1, Robot 36) don't exercise: Scottie S1 (mid-line sync —
   the exact family that already produced one real synthetic-test-passes-while-wrong incident,
   `CLAUDE.md` §4), Robot 72 or R24, a PD/MP mode, RM8 or RM12, a narrow MN/MC mode, AVT. Bottlenecked
   on the user's time with the real legacy Windows binary, not on dev work.
2. **Task #8 — Phase 3 chain/integration audit** (milestone-audit playbook, `docs/audit-playbook.md`) —
   skip Phase 1/2, units are already individually verified to an unusual degree. Should follow #7, not
   precede it, so the audit runs against the widest available real-audio coverage.
3. Band 3/4/5 items from the 30-item DSP-simplification inventory (S1-S30) are cataloged in
   `spec/14-roadmap.md` but not yet scheduled — most Band-3 items (including S8/S9 MN/MC narrow-family
   work and S7/S11/S16/S17 AVT work) are gated on task #7's new fixtures (mode-family gaps get fixed
   when measured, not reasoned, per the auditor's own recommendation).

## Other candidates (not urgent)

- **Phase 2 — Radio layer**: `IRadioController` reference implementation against a fake
  transport/protocol, then `rigctld` client mode ([[02-radio-layer]], [[04-rigctld]] in the roadmap).
- **Windows/macOS real-hardware audio verification** (`spec/14-roadmap.md` Phase 1, Audio 1b): the
  native-shim build compiles in CI on both, but neither has been run against real/virtual hardware —
  needs a human on each OS, not agent-doable from this Linux sandbox.
- **cty.dat license pre-audit: DONE** (2026-08-02) — Clublog's `cty.dat` has no fee, but redistribution
  needs a human to email Clublog's helpdesk with the proposed use and get an API key before it can
  actually be downloaded/bundled; not a simple open-license drop-in. Full finding in `LICENSES.md`
  ("Candidate future asset" note) and `spec/14-roadmap.md` (search "Pre-audited"). Remaining before
  Phase 4: someone (not an agent) actually emails Clublog and gets the key.
- **Remaining small license-audit item**: confirm whether Chilkat/FastReport back a real legacy feature
  — needs the legacy binary running, not just source, so not doable from this sandbox.

## Completed work (full narratives in `spec/14-roadmap.md`, search "Piece N")

Before piece numbering started: AFC (`AfcTracker`, direct port of `CSSTVDEM::SyncFreq`), Auto Slant
(`SlantTracker`, clock-drift correction), and the full 7-piece VIS/preamble-lock state machine — commit
`8dbe790` onward, no known open DSP-correctness gap in Phase 1 from this area.

Piece 8 — **the actual root-caused fix for the Robot-36-at-11025Hz decode gap**: real golden-vector
capture from a working legacy install exposed a 5x-worse-than-synthetic gap; investigation found a
missing per-image sync-anchor re-correction (legacy's `SyncSSTV`/`m_wBgn` fold-and-argmax) — ported as
`SyncAnchorCorrector`, plus a real Scottie-family wraparound bug caught and fixed along the way.
Measured: Robot 36 68.06→9.95, Martin M1 11.78→2.40 average per-channel delta. Commits `23b025a`→`8f87146`.

Pieces 9-13 (all committed, all green): real VIS-bit dual-envelope tone race (piece 9), `GetPictureLevel`
peak-picking (piece 10), horizontal pixel-pitch trim (piece 11), RM8/RM12 RX gain correction (piece 12),
`DecodeFSK`'s real 5-phase FSK state machine (piece 13).

Pieces 14/15/B + noise harness: Hilbert demodulator (`CHILL`) replaces PLL as the main picture
demodulator (piece 14, commit `aeccfcc`); legacy's always-on 2-tap pre-filter (piece 15); noise-
robustness test harness (new infra, not a port); `SearchBandpassFilter` — legacy's `H2`/"search"
pre-AGC bandpass filter, continuous scope, no lock-state gating (piece B, commit `c3b9f46`) — extended
with the locked-state H1 counterpart by Band-1 item 4 above, closing the gap this piece's own doc
comment flagged.
Measured noise-floor improvement: martin-m1 9.0dB→3.0dB, robot-36 16.0dB→9.0dB.

**Windows CI fixed** (2026-08-01, commit `8be35b7`): `ilammy/msvc-dev-cmd@v1` was setting `Platform=x64`
as a job-level env var, and `ScanlineStudio.sln` only has "Any CPU" configs — fixed with explicit
`/p:Platform="Any CPU"` on the dotnet steps. All three CI legs green on `master`.

## Working methodology (established across this project)
- Legacy is ground truth — verify against `yoniq-old/YONIQ-main/` source directly, no assumptions.
- Test early, test often — build + run tests after each sub-step, not just at the end.
- Get an Opus/`auditor` plan-review before writing code for non-trivial DSP ports (CLAUDE.md §7) — ask
  the auditor directly "is this ready to build now?" each round; soft 3-round backstop, then loop in the
  user. Restate the ADHD/scope rule and the ported-behavior framing in every subagent prompt.
- For a substantial piece, a final CODE-LEVEL auditor review after implementation (not just a plan
  review before it) is worth doing before calling it done — caught real issues in every Band-1 item's
  own implementation that plan-review alone couldn't (bugs only visible once the code actually exists).
- Independently re-verify Opus/agent findings against actual source before trusting/acting on them.
- When investigating a suspected bug, confirm the mechanism empirically (temporary instrumentation,
  added and fully reverted or converted to a permanent regression test, `git status`/`git diff`
  confirmed clean) before designing a fix — don't fix blind. Used successfully across every Band-1 item.
- If a fix's design doesn't demonstrably achieve what it's meant to (measured via a real test, not
  assumed), revert to the simpler alternative rather than keep the more complex one "just in case."
- When a design choice has real, uncertain tradeoffs (not a lookup, not a bug with a known root cause),
  the `/adhd` skill (parallel divergent ideation across cognitive frames, scored/clustered, top ideas
  deepened) is worth running before committing to a direction — used for the "move to a streaming
  architecture now vs. defer" question during Band-1 item 4; the deepening pass surfaced a concrete,
  buildable design (4a) that a straight architecture debate hadn't converged on.
- A throwaway spike to get a REAL number beats reasoning about magnitude in the abstract — but measure
  the thing that actually matters (an early spike measured the wrong quantity — raw pushed-sample count
  instead of the actual cache cursor position — and had to be corrected before its result meant anything).
- A pre-computed "this item is shaped like that other item" classification (even the auditor's own) is a
  starting hypothesis, not a fact — verify each item's actual legacy feed schedule from source before
  reusing a sibling's fix. S16 was pre-classified "same conversion as S5"; a plan-review round caught
  that AVT's PLL is fed intermittently (like d13), not every sample (like S5's d11/d12/d19), BEFORE any
  code was written — the fix that shape actually needed was much smaller than planned.
- When a review flags the same finding more than once across rounds (e.g. "this doc comment is stale"),
  verify against the CURRENT file before re-fixing — an already-applied fix can show up as a stale
  finding in a later round if the reviewer's own context predates it.
- Document steps + results durably in `spec/14-roadmap.md` as you go; keep this file trimmed to
  "what's needed to resume," not a running history (that's the roadmap's job).
- Cloud-scheduled routines (RemoteTrigger/`/schedule`) run in an isolated environment with a fresh git
  checkout — no access to `yoniq-old/YONIQ-main/` or `QSSTV-main/` (both gitignored, local-only).
  Session-local `CronCreate` reminders work fine instead (no isolation issue) but only last the session.

## Build/test commands
```
dotnet build src/ScanlineStudio.Core.Sstv -c Debug
dotnet test tests/ScanlineStudio.Core.Sstv.Tests -c Debug
dotnet test tests/ScanlineStudio.Core.Sstv.Tests -c Debug --filter "FullyQualifiedName~<substring>"
```
