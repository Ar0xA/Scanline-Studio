# Project brief (resume point)

Scratch file for resuming after `/clear` — not a spec doc, delete or ignore once stale.

## What this repo is
Yoniq v2: cross-platform (.NET 8 + Avalonia) rewrite of YONIQ (MMSSTV fork). Specs in `spec/00`-`spec/15`.
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
`tests/Yoniq.Core.Sstv.Tests/GoldenVectorTests.cs` (11 cases) — compares each source `.bmp` against a
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
anywhere) into `tests/Yoniq.Core.Sstv.Tests/Fixtures/GoldenVectors/TxCapture/`. **Then the user asked a
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
`spec/14-roadmap.md`, `src/Yoniq.Core.Sstv/AnalogFmSstvDecoder.cs`, `src/Yoniq.Core.Sstv/VisLockStateMachine.cs`,
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

**Test count**: 423/423 `Yoniq.Core.Sstv.Tests`, solution-wide build clean, golden-vector tests
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
as a job-level env var, and `Yoniq.sln` only has "Any CPU" configs — fixed with explicit
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
dotnet build src/Yoniq.Core.Sstv -c Debug
dotnet test tests/Yoniq.Core.Sstv.Tests -c Debug
dotnet test tests/Yoniq.Core.Sstv.Tests -c Debug --filter "FullyQualifiedName~<substring>"
```
