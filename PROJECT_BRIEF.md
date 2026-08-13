# Project brief (resume point)

Scratch file for resuming after `/clear` — not a spec doc, delete or ignore once stale. Pruned
2026-08-08, four times on 2026-08-11, and again 2026-08-12 twice (once after CW-ID/FSK closed, again
after demod-type + RX BPF closed) — completed subsystems are fully captured elsewhere: each shipped
control/phase has its own detailed commit message (`git log --oneline`), and durable per-control
state lives in `spec/16-gui-wiring-survey.md` (wiring inventory) and `spec/14-roadmap.md` (backlog +
research). Nothing lost — see git history for this file if older narrative is ever needed.

## CW-ID / FSK station-ID subsystem — DONE, committed (2026-08-12)

All 6 phases complete and auditor-reviewed. Wire protocol independently confirmed byte-for-byte
legacy-faithful. Real-window verified. Nothing left on this subsystem's plan. See git log
(`26f674f`/`18338d8`/`1185d91`/`bee6c6d`/`d6b0d6d` and Phase 2/6's own commits) for the full citation
trail if ever needed again.

## Demod-type runtime-dispatch subsystem — DONE, committed (2026-08-12)

All 4 phases complete and auditor-reviewed (2 plan-review rounds before implementation, per-phase
code review after): Phase 1 `ec7a989` (PLL/Zero-crossing narrow-mode support), Phase 2 `9d9fccb`
(`AnalogFmSstvDecoder` runtime dispatch between the 3 already-ported demodulator classes), Phase 3
`e6c83a9` (settings-layer + `RestartableSstvDecoder` threading), Phase 4 `a689930` (Options dialog UI
wiring). Real-window verified (toggled to PLL, saved, reopened, confirmed persistence). Full
`ScanlineStudio.Core.Sstv.Tests` suite green throughout.

## RX BPF (bandpass-filter preset) subsystem — DONE, committed (2026-08-12)

Started as a "just wire the UI" backlog item; direct legacy-source verification (per CLAUDE.md's
no-assumptions rule) found it needed net-new DSP math first — Narrow/VeryNarrow presets require
attenuation ≥21dB, which routes legacy's shared filter-designer through its Kaiser-Bessel-window
branch (`fir.cpp`'s `MakeFilter`/`I0`), previously unported and provably unreachable at this port's
prior Wide-only default. User authorized full scope (2 rounds of plan-review before implementation,
matching demod-type's own rigor).

All 4 phases complete, each auditor code-reviewed clean (no blockers in any round):
- Phase 1 `c8134fb`: `RxBpfPreset` enum (`ScanlineStudio.Abstractions.Sstv`) + Kaiser-window
  `MakeFilter` port + preset-parameterized `SearchBandpassFilter` (Wide/Narrow/VeryNarrow, plus
  `syncRestartEnabled`-driven H1 `fcl`, a live bug fix in the prior Wide-only port that had hardcoded
  1100Hz regardless of sync-restart state). Independently-computed (Python) coefficient fixtures for
  both Kaiser alpha-threshold arms.
- Phase 2 `ba72714` (the crux): `AnalogFmSstvDecoder` wiring — `_searchBandpassFilter` nullable, null
  for `RxBpfPreset.Off` (a true bypass matching legacy's `if(m_bpf)` gate, not a discard-output
  filter). Round-1 plan-review blocker fixed: the Off-bypass path keeps the existing buffer-trim fill
  loop intact via a null-coalesce, rather than an early return that would have silently pinned the
  trim cursor and caused unbounded memory growth — a dedicated regression test
  (`BufferedSampleCount_StaysBounded_ForLongNeverLockingStream_WithRxBpfOff`) proves the fix, traced
  to confirm it would have caught the originally-planned buggy shape. Golden-vector regression
  fixtures (6 real-audio decodes, framed explicitly as regression checks, not legacy-parity claims,
  since no legacy-RX reference exists at non-Wide presets).
- Phase 3 `a223116`: settings-layer + `RestartableSstvDecoder` threading, a faithful mechanical
  mirror of `DemodType`'s own already-proven shape (absent/out-of-range both clamp to Wide,
  `InnerRxBpfPresetForTests` accessor so a dropped `CreateInner()` argument can't silently revert a
  user after the ~12h periodic rebuild).
- Phase 4 `16df5fe`: Options dialog UI wiring. Un-stubbed the pre-existing disabled placeholder radio
  group (fixed a real bug in the stub itself: hardcoded `IsChecked="True"` on item 0/"Normal", which
  is a true bypass — real default is Wide). Added the missing help-glyph this row lacked vs. its
  `SenseLevel`/`DemodType` siblings. Real-window verified: Wide checked by default (not the old
  stub's Normal), toggled to Sharp/Narrow, saved, reopened, confirmed persistence.

**Status: feature complete.** Full `ScanlineStudio.Core.Sstv.Tests` suite green (806/806) after
Phase 3; `ScanlineStudio.UI.Tests` (242/242) and `ScanlineStudio.Application.Tests` (93/93) green
after Phase 4. Nothing left on this subsystem's plan
(`/home/artien/.claude/plans/stateless-hopping-rain.md`).

**Not yet pushed** — all of the above (CW-ID/FSK, demod-type, RX BPF) committed locally to `master`,
push not yet requested/confirmed this session.

## RX buffer subsystem — IN PROGRESS (started 2026-08-13)

Full plan at `/home/artien/.claude/plans/coppery-staging-heron.md` — 2 rounds of plan-review before
implementation started (round 1 found real blockers: a missing decode-path gating site the port had
already hardcoded with a now-stale comment, an unscoped prerequisite routine, and a wrong staging-buffer
data model; round 2 confirmed the revised plan ready). User confirmed full scope (all 5 legacy pieces:
RAM staging buffer, replay mechanism, decode-path Auto-Sync gating, disk-backed Extended mode, the
"Correct Slant" search algorithm) — biggest subsystem in this project's history, 9 phases.

- **Phase 1-2 — DONE, committed (`aa67fc5`)**: `RxBufferMode` enum (Off/On/Extended, mirrors
  `RxBpfPreset`/`DemodType`'s shape) threaded through `SstvDecoderSettings`/`RestartableSstvDecoder`/
  `AnalogFmSstvDecoder`'s constructor. Deliberately inert at this point — no decode-path logic read it
  yet. 1 auditor code-review round, clean.
- **Phase 3 — DONE, committed (`ca873cd`)**: the decode-path gating fix. Two sites in
  `AnalogFmSstvDecoder` previously assumed `sys.m_UseRxBuff` was always true (`TryAutoSync`'s branch
  1/branch 2, `Main.cpp:3907`/`:3945`; `TryResolveSyncAnchorCorrection`'s averaging-depth selection,
  `Main.cpp:3760`) now gate on the real `RxBufferMode` value — a genuine decode-behavior change reachable
  the moment `Off` is selectable, independent of the still-unbuilt buffer/replay. 3 auditor code-review
  rounds (round 1: loop-exit bug + missing branch-1 test; round 2: the added test didn't actually
  isolate branch 1 — structurally preempted by branch 2, caught and documented rather than chased
  further; round 3: softened an overclaimed "not constructible" comment). New test file
  `RxBufferModeGatingTests.cs`. Full suite green throughout (814/814 final).
- **Phase 4 — DONE, committed (`36f276b`)**: `RxLineStagingBuffer`, a flat, capacity-capped,
  chronologically-ordered store of two parallel `double` sample streams, NOT per-line chunks (this was
  round-1 plan-review's key correction — legacy's replay re-splits a flat stream using the CURRENT
  corrected timing, not the capture-time stride). 2 auditor code-review rounds — round 1 found a real
  admission-boundary off-by-one (legacy's `<` rejects an exact-fill line, an earlier version accepted
  it) AND a wrong "legacy integer-overflow bug" doc-comment claim (legacy's own `SampFreq` is `double`,
  not `int` — no such legacy bug exists; the port's own `(long)` cast is still needed, but only for a
  real C#-side reason). Isolated, no decoder wiring — 15 new tests, zero coupling to decode internals.
- **Phase 5 — DONE, committed (`b77bd10`)**: wired capture into `ApplySlantTracking`'s own per-sample
  loop (not "after it returns" as originally planned — `envelope` is a local, discarded immediately,
  had to be tapped at computation time). Gated on `RxBufferMode.On` specifically (`Extended`'s own
  disk-backed capture is Phase 7, still unbuilt). 3 auditor code-review rounds. **Found and fixed a
  real blocker along the way, not just a wiring bug**: this phase's own plan item "resolve the
  `GetPictureLevel`/`GetPictureLevelDiff` pixel-lookahead question" had been pre-judged out of scope
  with wrong reasoning ("C++ pointer-arithmetic safety detail") — legacy actually disables peak-picking
  for every mode under `RxBufferMode.Extended`, in the LIVE decode path, a real currently-reachable
  pixel-level divergence since `Extended` has been selectable since Phase 3. Fixed by folding into
  `PixelSampleReader`'s existing `neverPeakPicks` flag (previously Scottie-DX-only). Full suite green
  throughout (839/839 final, zero regressions at every round — the "capture changes nothing observable"
  guarantee held end to end).
- **Phase 6 (the crux) — DONE, all sub-phases (6a-6d) committed.** Replay mechanism. 3 rounds of Phase-6-specific plan-review
  (on top of the general 2 rounds) resolved: flat-stream staging model, uniform-vs-accumulated stride,
  `_nextLine`-vs-`m_AY` unit mismatch (row-doubling for paired-channel modes), `_bufferBase` clamp
  hazard (closed for data reads; `_nextLine` propagation handled separately), reentrancy (deferred-
  request pattern, `_reSyncRequested`-shaped), and the Auto-Slant suppressed-replay shape (needed a
  third `SlantTracker` method, not either existing one). Broken into sub-phases 6a-6d:
  - **6a — DONE, committed (`334cd80`)**: `ReplayOriginCalculator` (43-mode `AdjustSyncPos` port +
    histogram-fold origin derivation) + `RxLineStagingBuffer` per-line boundary tracking
    (`LineCount`/`SampleCountThroughLine`). 1 auditor round found a real per-line-stride-uniformity
    bug (this port's staged lines aren't uniform-width like legacy's, unlike legacy's real
    `m_WD`) — fixed by resolving fold bounds from real per-line boundaries, not a division.
  - **6b — DONE, committed (`0869f16`)**: extracted `ApplySlantTracking`'s data-source-agnostic core
    into `ProcessSlantTrackingSample(envelope, isReplay)` (RX-buffer capture hooks stay live-only,
    outside the core); threaded `isReplay` through `TryAutoSync` (legacy's `!m_ASDis`, ported onto
    exactly the 3 trigger conditions — branch 1, Auto Stop, branch 2 — bookkeeping stays
    unconditional); added `SlantTracker.ProcessLineSuppressed`/`ResetBaseline` (legacy's
    `m_ASDis`-suppressed re-feed: same fit/baseline/bitmask logic, withholds only the final rate
    write). Mechanical extraction verified zero-regression (isReplay is always false on the only
    current call site) — 862/862 full suite green throughout. 1 auditor round: EQUIVALENT/GO, nits
    (wrong line citation, thin bitmask test coverage) fixed inline before commit.
  - **6c — DONE, committed (`ff4f93f`)**: the replay engine (`AnalogFmSstvDecoder.PerformReplay`),
    exercised only via a test-only `PerformReplayForTests()` wrapper (Phase 6d, the automatic trigger,
    is what makes it reachable from production). **The heaviest-weight round of this whole session** —
    5 auditor rounds (this project's normal soft cap is 3; continued past it with explicit user
    sign-off each time, given genuine DSP-coordinate-phase complexity, not scope creep). Round 1: a
    positive-origin crash (Scottie-family modes) + a row-misalignment bug. Round 2: the misalignment
    "fix" was actually a sub-sample no-op (`_consumedSamples`/`_idealLineStartSample` are already
    within 0.5 samples by invariant) + a NEW phase-offset bug in the sync-bookkeeping re-feed loop.
    Round 3: both of THOSE fixed correctly, but a deeper defect surfaced — the forward cursor-jump
    (which sacrifices one row so bookkeeping realigns) left the staging buffer physically discontinuous
    across repeated replay passes. Asked the auditor to propose the exact fix (truncate the buffer at
    the jump + a new `_rxBufferBaseTransmissionLine` field tracking the truncation point) rather than
    design it myself, per this project's own "ask the auditor to propose the fix after repeated rounds"
    convention. Round 4: implementing that fix, self-caught a FURTHER bug the auditor's own proposal
    missed (the `_nextLine` reconciliation used a LOCAL buffer-relative index without converting to
    absolute image-row terms — caught via a real `gap2=-10` test failure, not by inspection) — fixed,
    plus discovered and explicitly deferred (not silently dropped) a real, separate finding:
    `RobotScanlineDecoder`'s cross-line chroma cache doesn't survive a sacrificed/redrawn replay row
    (confirmed via reproduction; the regression test now runs against a stateless decoder, R24,
    instead). Round 5: **GO**, plus one more real (if minor) fix applied before commit — `ComputeOrigin`'s
    `stagedLineCount` parameter needs the running base offset added too (same local/absolute bug class,
    third instance) — and `DrainPendingSkip`'s own analogous staging-buffer-discontinuity gap documented
    as a known, deferred item (currently unreachable, must be resolved before any future manual-redraw
    UI trigger). Full suite green throughout every round (869/869 final).
  - **6d — DONE, committed (`965e4df`)**: automatic trigger plumbing (`_pendingReplayRequested`, set at
    a commit branch + a once-per-image latch, both inside `TryProcessBuffer`'s own per-line loop) — the
    piece that finally makes 6c's replay engine reachable from a real decode, not just
    `PerformReplayForTests()`. 3 audit rounds + a dedicated deep-dive fork: initial wiring drained at
    the top of `PushSamples` (mirroring `_reSyncRequested`), which broke chunk-size determinism
    (`PerformReplay` is destructive — a caller-chunk-boundary drain point made the DECODED IMAGE a
    function of how `PushSamples` calls were sliced); a deep-dive moved the drain to a decode-position-
    deterministic point instead, and separately fixed a second bug (Auto-Sync/Auto-Stop permanently
    starved after a 2nd replay pass — legacy rebuilds against the FULL running line count on every
    pass, this port's own truncate-on-jump divergence means a later pass can't). Round 1 (auditor)
    found two real blockers once wired: the once-per-image latch could fire across a staging-buffer
    hole left by a manual ReSync (closed by gating on `!_slantCorrectionsDisabledForRestOfImage`); and
    the latch's own literal-legacy unconditional trigger now guarantees a small visible defect (a
    sacrificed row) in every default decode — **escalated to the user as a product decision** (not
    resolved unilaterally), who chose to narrow the latch to require an actual committed correction
    first. Round 2 found one of the new automatic-trigger tests vacuous (suppressed the exact thing it
    was supposed to prove); round 3: **GO**, closed. Full suite green throughout (872/872 final).
  Full plan at `/home/artien/.claude/plans/coppery-staging-heron.md`.

  **Whole-subsystem review (Phases 1-6 together), GO on round 1 (2026-08-13)** — integration holds
  together end to end, no new silently-wrong cross-phase composition found. Confirmed via a NEW
  end-to-end test (`RxBufferModeOn_DecodesMeasurablyBetterThanOff_UnderARealClockMismatch`,
  `ReplayEngineTests.cs`) that the subsystem's own core value proposition actually holds: a real
  drifting-clock reception genuinely decodes better with `RxBufferMode.On` than `Off` — every other
  test up to this point verified LOCAL correctness of one piece at a time, none had verified this
  GLOBAL outcome. Follow-up items from the review, triaged:
  - **Addressed**: `RobotScanlineDecoder`'s cross-line chroma cache (known since Phase 6c) — the review
    found its REACHABILITY escalated from "test-only" to "any default-settings Robot-family reception
    with real clock drift" once Phase 6d made replay automatic. Re-confirmed as an accepted, still-
    deferred limitation (not silently carried forward) — `PerformReplay`'s own doc comment now states
    the escalation explicitly. Real fix (candidates: skip replay's row-sacrifice for stateful-decoder
    modes, or reset the decoder's own cross-line cache at a truncation boundary) still not built.
  - **Not yet addressed, tracked**: Auto-Slant's convergence characteristic changed materially at
    Phase 6d (`SlantTracker.ResetBaseline()` now actually runs in production on every replay pass,
    unlike before 6d when it had no caller) — more legacy-faithful, but `SlantTests.cs`'s own "bitmask
    permanently latches" doc comment is now stale for the default (`RxBufferMode.On`) path, and nothing
    measures whether this changed convergence behavior for the better or worse. A one-line
    `_suppressNextSlantProcessLine` bookkeeping loss when a manual-ReSync suppression and an automatic
    replay land in the same per-line iteration (bounded to one line, not chased further).
- **Phase 7** — disk-backed Extended mode. **Phase 8** — "Correct Slant" (`KRCS`) one-shot search.
  **Phase 9** — Options dialog UI wiring (`RGRBuf` stub already exists, wrong default like RX BPF's own
  stub had).

Not yet pushed, same as CW-ID/FSK/demod-type/RX BPF above.

## Other deferred items (scoped, not started — full citations in the docs named)

- **RX buffer** — superseded, see the dedicated in-progress section above, not a backlog item anymore.
- **Auto-start** (Decode tab) — needs a design pass to find the right internal "disarmed, still
  live" gating point across multiple sync-detection branches.
- **Advanced tab** — PLL/Zero-crossing tuning controls now have a real backend (demod-type subsystem)
  but the tab's own tuning-parameter UI isn't wired yet; TX BPF/LPF toggles are a small, bounded
  change but have real over-the-air spectral consequences; Loopback/calibration wizards are fully
  unbuilt.

Resolved, not open items: RTS-on-RX (obsolete, removed feature, `docs/removed-features.md`); VOX/
Sound-file ID (bundled into CW-ID/FSK, done); JPEG save quality (bundled with the Gallery's
still-stub "Export frame" button, not a standalone Options control — separate small item, not on
this list); RX BPF and Demod type (both done, see above).

## Current wiring snapshot

`spec/16-gui-wiring-survey.md`: **~149 REAL, ~101 STUB** (plus FAKE-LIVE/PARTIAL, see that doc's own
summary table for exact current counts — this file doesn't duplicate it live). Densest remaining
gaps: Receive tab's Sync&Slant/Input-chain/Signal-quality cards (no live audio-chain measurement
exists for most of these — real new DSP work); the deferred items above; Transmit tab's
Queue/TX-log/Recently-sent cards (100% stub, no such features exist); TX image editor's
canvas-overlay safe-area/callsign/report-plate text (FAKE-LIVE, the most deceptive placeholder in
the app).

## Established process (proven across many prior batches, reuse it)

research → plan → auditor plan-review (2 rounds for anything touching decode-path/concurrency/
schema; skip for pure UI-plumbing with no DSP/concurrency risk) → implement → auditor code-review
(soft cap ~3 rounds — round 1 finds real blockers, round 2 catches an incomplete fix, round 3
usually closes it, loop in the user rather than a round 4) → verify (build + full relevant test
suite, real-window screenshot/DB-level check if UI-visible) → commit → push. Escalation path if
stuck: ask the auditor; if the auditor also can't resolve it, log to `spec/14-roadmap.md`'s "Verify
later with human" section rather than stalling.

Real-window testing keeps catching things build+tests never would — this session (RX BPF Phase 4)
it caught that the app's own menu bar doesn't render until the window is focused/clicked once after
launch (a pre-existing WM/rendering quirk, not a code bug, but real-window screenshots taken
immediately after `dotnet run` will miss it). Screenshot toolchain: no `xdotool`/`ydotool` in this
sandbox; a `pip`-installed `pyautogui` + `mss` + `pillow` venv in the scratchpad directory works
(ephemeral, session-scoped — recreate if needed in a future session). Screenshot ONLY the
secondary/HDMI-1 monitor region (`left:3840,top:0,width:2560,height:1440`), never full-screen, never
"active window" mode — a real incident during CW-ID/FSK work briefly exposed the user's primary-
monitor windows via "active window" capture; that mode must never be used again.
