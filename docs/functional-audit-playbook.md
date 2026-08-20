# Functional Bug Sweep Playbook

Run this when you want an exhaustive, disciplined `auditor` pass across production
code hunting **functional/C# correctness bugs** — not legacy parity. Complementary
to [`docs/audit-playbook.md`](audit-playbook.md), not a replacement: that one verifies
the ported chain matches YONIQ end-to-end against golden vectors; this one hunts
bugs in the C# itself, including files with no legacy counterpart at all (pure
Application/UI code is fully in scope here).

**Why a separate playbook:** per-feature audits during normal work stop as soon as
one big finding lands and the immediate task moves on. This sweep exists specifically
to NOT do that — every file gets rounds until it's genuinely clean, not until the
first blocker is found and fixed.

Trigger: *"run the functional audit from the playbook."*

---

## Scope

- **In scope**: all of `src/` (production code, including **native C**, not just
  C# — `src/ScanlineStudio.Core.Audio.MiniAudio/native/yoniq_audio.c` is our own
  code and the audio-thread callback; a C#-only triage heuristic will silently
  miss it, confirmed the hard way on the first Tier A triage pass) and its paired
  `tests/` files.
- **Out of scope**: `yoniq-old/`, `QSSTV-main/`, `hamlib/` (external reference clones,
  not our code), `native/miniaudio.h` (vendored third-party, not ours), generated/
  `obj`/`bin` output, `mockups/`.
- **Test files get a different rubric than production files** — see "The gate" below.
  The goal for a test file is never "find a bug in the test," it's "would this test
  actually fail if the behavior it claims to cover broke" (the vacuous-test pattern:
  `FakeReceiveHistoryStore.SaveFrameCommand` test asserted only side-effect-free state
  and would have passed even if `SaveAsync` were never called).

## Tiers (risk-based rigor)

Real counts as of 2026-08-18, **before** Step 0 triage below (this is the raw
candidate pool, not the real target list):

| Tier | Scope | Raw `.cs` count |
|---|---|---|
| **A** | `Core.Sstv`, `Core.Radio*`, `Core.Audio*` (DSP/codec math, CAT/native-interop, concurrency-sensitive state machines), plus the orchestration-heavy files in `Application` (`SstvSessionService.cs`, `RadioSessionService.cs`, and similar) | ~100 |
| **B** | Remaining `Application`, `UI/ViewModels`, `Core.Logbook`, `Core.Imaging` (real state, no DSP/native code) | ~65 |
| **C** | AXAML code-behind, `Converters`, `Settings`, `Core.Localization`, `Host`, `Plugins` (mechanical/plumbing) | ~33 |

**Start with Tier A.** Do not commit to B/C upfront — checkpoint after A and decide.

---

## How to run

### Step 0 — Triage (cheap, not Opus-effort, always first)

For the tier's candidate file list: a fast pass (`Explore` agent or direct
`wc -l`/grep for branching) sorts every file into:
- **Real logic** — actual algorithms, state machines, non-trivial conditionals →
  goes into the tiered rounds below.
- **Trivial** — pure DTO/record/interface/enum, near-zero branching → skipped
  entirely, not even a single round.

For each real-logic production file, also identify its paired test file(s) (if
any) — these ride along into the same round, not a separate pass.

Output a table (file, tier, real-logic vs trivial, paired test file) and STOP for
review before any Opus-effort round runs. This is the main cost lever — approve
the trimmed list, not the raw candidate count above.

### Phase 1 — Rounds

For each real-logic file (+ paired tests), fan out to the `auditor` agent in its
own isolated context. Restate the ADHD scope rule in every payload — auditor.md's
own system prompt already carries it, but the fan-out prompt itself needs the
scope boundary for THIS specific file, same as every other auditor delegation in
this project.

**Auditor's task per round:**
1. The existing 8-item functional/C# checklist (`auditor.md`) against the
   production file.
2. **Test-integrity check** against the paired test file(s): does each test
   actually verify the claimed behavior? Would it fail if the behavior broke?
   Are assertions vacuous or tautological? Do fakes/mocks get configured in a way
   that could mask a real bug (e.g. a fake that silently no-ops instead of
   recording a call)?

**Rigor by tier:**
- **Tier A**: fresh round → fix → fresh round again. A file is NOT done after one
  clean round — needs **two consecutive clean rounds**. Soft cap 4 rounds, then
  stop and escalate to the user rather than loop forever.
- **Tier B**: one round + one confirmation round (fresh agent). Stop once the
  confirmation round is also clean. Cap 3 rounds.
- **Tier C**: single pass. Escalates to Tier B rigor only if that pass finds a
  blocker.

**"Fresh round" = a new Agent call, not a resumed conversation.** Independent
re-derivation is what catches what the previous round missed — a same-context
agent re-checking its own prior "looks fine" tends to anchor on it.

**Coupled files get ONE combined round, not N independent ones.** When files
share a real cross-file invariant (a ring buffer's SPSC contract spanning
producer/consumer files, a P/Invoke layer underlying several managed wrappers,
sibling decoders that must agree on a shared index convention), auditing each
file in isolation is how the cross-file break gets missed. Give the auditor the
whole coupled set in one round; only split once the auditor itself confirms the
files are genuinely independent.

**Files too large for one round get chunked, not skipped or force-fit.**
`AnalogFmSstvDecoder.cs` (5968 lines) is the reference case — see "Chunking a
mega-file" below. Don't lower a huge file's rigor just because chunking it is
more work; that's exactly backwards from risk-based tiering.

### Chunking a mega-file

1. Ask the `auditor` itself to propose chunk boundaries (method-group/subsystem,
   not arbitrary line ranges) as a planning task — it can read the file's real
   structure faster and more accurately than a heuristic can guess it.
2. Chunks that mutate the same field cluster stay on **one sequential agent
   lineage** (same reasoning as "coupled files" above) — splitting a shared-state
   subsystem across independent chunk reviews is how a cross-chunk invariant
   break gets missed. Genuinely separable chunks (different state, different
   subsystem) can run in parallel.
3. Add **one final whole-file pass** after every chunk lands, scoped ONLY to field
   lifecycle: for every mutable field, who writes it, and is it correctly reset/
   preserved at every teardown/reset path (`Dispose`, abandon, force-mode, error
   recovery, etc.). Chunked reviews structurally cannot see "field X is reset in 4
   of the 5 teardown paths" — only a whole-file pass can. This pass counts as one
   of the file's two required clean rounds.
4. State the total round-count cost explicitly before starting (a chunked mega-file
   can be 20-40 agent calls on its own) — this is a real budget item, not a rounding
   error, and deserves its own approval separate from the rest of the batch.

### The gate — every single round ends with this

> "Would you sign off on this file (and its tests) as production-ready right now?
> Yes/no, and if no, exactly what blocks it."

A round only counts as **clean** if it has zero blocker/risk findings AND an
explicit yes covering both the production code and its test coverage. A round
that finds even one real issue is not a stopping point — after the fix, the next
round re-scans the WHOLE file, not just the fixed spot (this is what catches
sibling bugs a narrower re-check would miss).

### Phase 2 — Checkpoint

Consolidated table: file | rounds taken | final verdict | findings fixed |
test-integrity notes. STOP for review before starting the next tier.

---

## Execution mechanics

- Different files run in **parallel** (independent). Rounds *within* one file are
  sequential (round 2 needs round 1's report + the fix already applied).
- Fixes are applied by the orchestrating session, not the auditor — auditor stays
  read-only, matching its existing contract.
- Checkpoint **per tier**, not per file.

## Cost throttles

- **Gate on Step 0.** Approve the triaged real-logic list before any round runs —
  the biggest token lever, same principle as `docs/audit-playbook.md`'s own
  "gate on Phase 1."
- **Batch rounds.** Fan out 4-5 files at a time, not the whole tier at once.
- **Escalate on cap, don't loop forever.** A file that's still not clean after the
  soft cap (4 for A, 3 for B) stops and comes back to the user, rather than
  burning more rounds hoping it converges.

## Scope options

- Narrow to one workstream: *"Run the functional audit for `Core.Sstv` only."*
- Skip straight to a specific tier if another's already been swept: *"Tier A is
  done, start Tier B."*

---

## Tier A — approved batch plan (2026-08-18)

A cheap line-count/branch-keyword triage first cut Tier A's ~112 raw candidates
down to 35 "real logic" files. Checked that list with the `auditor` itself before
spending any Opus-effort rounds (per Step 0's own gate) — it found real gaps a
C#-only, prose-blind heuristic couldn't see, corrected the list to **~50 files**,
and reordered by actual risk instead of raw line count. **Always re-verify this
table is still current before reusing it** — it reflects one point-in-time triage
pass, not a permanent inventory.

**Biggest correction**: the 8 scanline encoder/decoder file pairs (`YCbCr*`,
`Rgb*`, `MonoAveraged*`, `Robot*`) plus `PixelSampleReader.cs`,
`ScanlineCodecFactory.cs`, `YCbCr.cs` were missing entirely — precisely the file
family where the documented Scottie channel-order bug (CLAUDE.md §4) lived. Small
files, easy to under-rank by line count alone, historically the highest-value
target in this codebase.

| Batch | Focus | Files |
|---|---|---|
| — | `AnalogFmSstvDecoder.cs` (5967 lines) | Chunked separately — see "Chunking a mega-file" above. 10 chunks (D1-D9 + D0 field-lifecycle pass), D3+D8+D9 sequential/coupled, D1 first alone, D2 last. ~20-40 agent calls, own approval. Exact chunk boundaries (auditor-proposed, 2026-08-18 — re-verify line ranges before reuse if the file has since changed): **D1 — done** (2026-08-18, 3 rounds, see below). **D2** public surface/push/retention, 1216-1376 + 1953-2271 (run LAST — depends on knowing every cursor D1-D9 established). **D3** ReSync/AutoSync/AutoStop, 1377-1952. **D4** main loop + image teardown (`TryProcessBuffer`/`EndOfImage`), 2272-2689 — "orchestration hub, doubles as the map," run next. **D5** header search & dispatch, 2690-3470. **D6** commit/anchor/VIS decode, 3471-4234. **D7** AVT resolve + AFC, 4235-4750. **D8** slant tracking, 4751-5228. **D9** replay + `CorrectSlant`, 5229-5967. **D0** final whole-file field-lifecycle-only pass (every mutable field's reset/preserve correctness across every teardown path), run last, counts as one of the 2 required clean rounds. D3+D8+D9 mutate the same field cluster (`_slantLinePeakPosition`, `_lastLineSyncPeakPosition`, `_pendingSkipSamples`, `_slantCorrectionsDisabledForRestOfImage`, `_pendingReplayRequested`, `_rxBufferAnchorSample`, `_rxBufferBaseTransmissionLine`, `_effectiveSamplesPerLine` — the documented "local-vs-absolute index, 3 separate times" bug nest, CLAUDE.md §4) so they stay on ONE sequential agent lineage, never independent parallel reviews. Execution order: ~~D1 alone~~ → ~~D4~~ → ~~D3+D8+D9~~ → ~~D5~~/~~D6~~/~~D7~~ → ~~D2 (closed, round 11, 2026-08-19)~~ → **D0 (final field-lifecycle pass, next)**.<br><br>**D5 (header search & dispatch) — done, 4 rounds** (2026-08-19; real boundary 2829-3603). Rounds 1-2 found no production bug, only 2 deliberately-deferred test-coverage gaps for load-bearing, already-correct behavior (a call-order dependency between `TrySyncIntervalDetectionStep`/`_visLockStateMachine.ProcessSample`, and the decoder's own S12-gate-property assignment) — both left documented-and-open rather than rushed, a judgment call rounds 2-4 each independently re-endorsed. Round 2 found one real thing round 1 missed: `m_MSync`, a real persisted legacy user option (`Option.cpp`, default on) that gates legacy's ENTIRE sync-bypass detection block — this port silently implements only half that gate (`!m_Sync`, never `&& m_MSync`), with comments that literally quoted the full legacy condition next to code implementing half of it. Fixed via a `docs/removed-features.md` entry (behaviorally inert at legacy's own default; a user who explicitly disabled it has no equivalent here) rather than built out — CLAUDE.md §2's removal rule explicitly allows this, and rounds 3-4 both independently agreed it was the right-sized response, not a shortcut. Rounds 3-4 each did a genuinely fresh, independent full re-derivation against legacy source (not just re-reading the prior round) and came back clean both times — zero blockers, zero risks, only wording nits. Full suite: 954/954 passing throughout.<br><br>**D7 (AVT training + AFC) — done, 3 rounds** (2026-08-19). Round 1: no blocker, only missing test coverage on `_afcBoundSample` (a review-introduced field with no legacy counterpart that had already shipped one silent total-AFC-disable regression once) and the unconditional `+= CorrectionHz` applying outside the AFC gate (deliberate, legacy-matching — `sstv.cpp:2270`'s own `d += m_AFCDiff` is unconditional too). Fixed the first with two new test accessors + a real mistuned-decode test proving AFC correction keeps pace through the last line of a real image — this test itself needed two rounds of self-correction before it was right (first version asserted the wrong thing entirely and failed on real data; second version was off-by-one against `LineDecoded`'s own pre-increment firing point) — both caught by actually running the test, not assumed correct after writing it. The second gap was left explicitly documented-not-tested (constructing the right signal is a real design task). Round 2: explicit yes, zero blockers/risks — D7's 1st clean round; independently quantified the new test's discriminating power (the exact historical regression shape would produce a ~794,000-sample lag vs. ~400 correct) and found one nit (the test's vacuity: it never asserted decode reached the real end, only that ITS OWN LAST snapshot looked fine) — fixed, though the fix itself needed two follow-up corrections (event-counting was invalidated by Auto-Slant's own replay redraws inflating the count; the direct `NextLineForTests` check needed a `-1` for its own pre-increment lag) before landing right. Round 3: fully independent re-derivation of the whole chunk against `sstv.cpp`, explicit yes, zero blockers/risks — D7's 2nd clean round, closed. 5 nits queued (a 9ms AVT fallback-deadline discrepancy vs. legacy, a dead test accessor, an `InitTone` int-narrowing gap, a retune-scope comment that's gone stale since two more resonators were added, the `-1` test coupling to `RowsPerTransmissionLine`) — none affect decoded output.<br><br>**D6 (commit/anchor/VIS decode) — done, 10 rounds** (2026-08-19; real boundary ~3553-4490, grown from its own fixes; soft cap of 4 exceeded with the user's own explicit go-ahead, re-confirmed twice more at rounds 8 and 9). This chunk found a real, distinct issue in EVERY round through round 6 — a real streak, not a fluke, and genuinely instructive about this playbook's own discipline: rounds 5 and 6 each caught the PRIOR round's own new doc-comment overclaiming something it hadn't actually verified (round 5 caught round 4 falsely claiming "no wrong output" for a path that was actually live in a narrow delta band; round 6 caught round 5's own fix using the word "verified" for an "exact same value" claim a pre-existing test's own measured tolerance directly contradicts). Round 4's dead-code finding (below) was escalated to the user rather than resolved unilaterally, per this playbook's own soft-cap rule — **user's decision (2026-08-19): leave both fixed-window header-detection paths as documented dead code, no further code changes.** Saved as project memory `narrow-vis-header-paths-left-dead` (also tracks the DISTINCT, still-open 0-185ms live-band bug round 5 found in `TryDecodeVisHeader` specifically, not covered by that decision). Rounds so far:
> - **Round 1** (4 fixes): `MsToSamples` rounding-vs-legacy-truncation documented (not changed — rounding is the closer approximation); a stale, backwards-reasoned invariant doc comment fixed + a defensive clear added (`_pendingAnchorCorrectionMode`); `ModeDetected` reordered to fire only after that same flag clears (closing a throwing-subscriber replay-duplication bug); a silently-falling-through duplicate sync-segment scan replaced with the shared, already-tested `SstvModeRegistry.GetSyncSegmentOffsetMs`. Also added `VisBitDecisionTests.cs`, 7 new direct unit tests for the shared VIS-bit accept/reject rule.
> - **Round 2** found 2 MORE real gaps round 1 missed: the extended-VIS escape byte was never re-verified after a mid-word reject (`TryDecodeVisDataBits` has internal retry logic that can silently swap in a different header occurrence's bits) — fixed with an explicit re-check; and one of round 1's own new tests (`TryDecide_ExactlyAtTheSeparationThreshold_Rejects`) didn't actually test the `<`-vs-`<=` boundary it claimed to (diff=9 passes under either operator) — fixed by splitting into two tests, the real boundary case using diff=slvl2 exactly.
> - **Round 3** found YET ANOTHER real gap: `TryDecodeNarrowModeHeader`'s search ceiling omitted the 300ms leader duration entirely, making its own "+200ms retry margin" comment false (the omission coincidentally summed to the same 950ms as the packet's own bare-minimum duration, leaving ~0 real margin) — fixed by adding the leader term, widening the ceiling to 1250ms.
> - **Round 4 found round 3's OWN fix was built on a false premise, and a second, related latent risk**: `AnalogFmSstvEncoder.cs` emits an unconditional ~400-800ms `OutHEAD` burst before every VIS/narrow packet that `headerStart` never accounts for — meaning BOTH fixed-window header paths (`TryDecodeVisHeader` too, not just the narrow one) are dead code for any realistic transmission, including this port's own encoder output, regardless of how wide the search ceiling gets (round 3's widening moved the tolerance from ~6ms to ~306ms — still nowhere near 400-800ms). **Claimed** "no wrong output today (the fallback scanners cover every real case)" for BOTH paths — **round 5 later found this false for `TryDecodeVisHeader` specifically** (see below), so read that claim as superseded, not accurate. Separately, round 3's own widening had moved the search window's real margin closer to a LATENT anchor-correctness trap: the commit anchor used a delta-blind fixed-offset formula that would silently commit an anchor up to ~400ms wrong if the window were ever widened further (or `OutHEAD`'s own duration changed) — **fixed this round**, ported `TryNarrowFskScan`'s own already-correct delta-robust anchor technique (`sample - SamplesSinceBitClockOrigin`) into `TryDecodeNarrowModeHeader`, making a wrong anchor structurally impossible regardless of window width. Also fixed: `VisHeader.NarrowSearchCeilingMs` (a shared constant meant to be this method's single source of truth, matching its two siblings' own established "can never silently desync" convention) had gone stale against round 3's own hand-transcribed inline formula — reconciled, method now reads the shared constant directly. **Left as an open architectural question at the time** (whether to make both fixed-window paths delta-robust or retire them as dead code along with `MaxSearchCeilingMs`'s own first-refusal gate) — **stopped here per the playbook's own soft-cap rule** (4 rounds, a real issue in every one) instead of dispatching a round 5 blind, and escalated to the user instead. Full suite: 954/954 passing (one existing test, `VisHeaderTests.SearchCeilings_MatchIndependentlyHandDerivedValues`, updated to match the corrected 1250ms narrow-ceiling value).
> - **User decision (2026-08-19)**: leave both fixed-window paths exactly as documented, no further code changes to make them delta-robust or to retire them. Saved as project memory `narrow-vis-header-paths-left-dead`.
> - **Round 5** (dispatched after the user's decision, to confirm rounds 1-4's fixes and the now-closed decision are correctly reflected) found the round-4 dead-code claim was ITSELF wrong: `TryDecodeVisHeader` is only *partially* dead — for a real 0 < delta ≤ ~185ms band (narrower than the fully-dead >185ms band the user's decision covers), this VIS path DOES fire and commits an anchor `delta` samples too early, the same non-delta-robust bug class already fixed for the narrow sibling. Impact bounded (`TryResolveSyncAnchorCorrection`'s fold re-phases it modulo line width — later refined in round 6 to note this bound doesn't hold for AVT's own unfolded fallback commit, and is mode-dependent for the folded case, not a flat "1-2 lines"). This is a DISTINCT, still-open finding, explicitly NOT covered by the user's own decision (which only addressed the >185ms fully-dead band) — round 5 itself said fixing the comment (not the code) was sufficient to close its own blocker.
> - **Round 6** fixed those comments — but found the round-5 fix's OWN new comment had overclaimed: it called the delta-robust and old fixed-offset narrow-anchor formulas "the exact same value... verified" at delta=0, which a pre-existing test's own measured tolerance (`NarrowFskNoiseTolerantDetectionTests.cs`, ~104-sample/~9.4ms real settling-lag bias) directly contradicts. Corrected to state the measured bias honestly instead of claiming a no-op. Also fixed the AVT-fallback-fold and mode-dependent-line-count nits round 6 itself found in the round-5 VIS comment, and two stale "identical note"/"OPEN decision" cross-references. **This is the fourth time in six rounds a round's own new documentation overclaimed something — round 5 caught round 4's, round 6 caught round 5's own. Treat any future round's "verified"/"exact"/"no wrong output" claims in this chunk with real skepticism, not just a rubber-stamp re-read.** Comment/doc-only, zero code or test changes.
> - **Round 7** independently re-verified rounds 5-6's substance line by line against the actual cited sources (`NarrowFskNoiseTolerantDetectionTests.cs`, `SstvModeRegistry.cs`, `Main.cpp`) and found it ALL correct — but caught a FIFTH instance of the same failure class in round 6's own new AVT paragraph: two wrong in-file line citations (`:4458`→ the real call was at `:4473` pre-edit; `:4438` pointed at an unrelated `if (mode is null)` early return, not AVT's real fallback commit, which is `Commit(SstvModeRegistry.Avt, _avtTrainingFallbackDeadlineSample)` in a DIFFERENT method, `TryResolveAvtTraining`) plus a factually wrong "extended-VIS-style AVT training-announce path" characterization (AVT is VIS code 68, a normal byte, never goes through the 0x23 extended-escape path at all). The underlying claim (AVT skips the fold, so the bound doesn't apply) was true and load-bearing throughout — only the citations/description were wrong, consistent with round 6 having computed them against the pre-edit file and not re-checking after inserting its own new lines above them. Fixed: both citations corrected to their real current line numbers (`:4477`, and a plain-language pointer to `TryResolveAvtTraining` instead of a wrong line number), the "extended-VIS-style" phrase replaced with the accurate AVT-is-VIS-68 explanation, and the dangling "TX-placement citation below" cross-reference at the top of `TryDecodeNarrowModeHeader`'s own comment (which pointed at nothing) resolved to point at `VisHeader.cs:340` instead. **Fifth overclaim/citation-drift in seven rounds — every round from 4 through 7 has found something wrong in its immediate predecessor's own new prose, even when the predecessor's underlying technical claim was correct.** Comment/doc-only, zero code or test changes; scoped 133-test filter passed clean after.
> - **Round 8** independently re-derived all 4 of round 7's fixes against the actual current files (`VisHeader.cs`, `SstvModeRegistry.cs`, `Main.cpp`, `TryResolveAvtTraining`) and confirmed every one correct — the streak of "predecessor round's own new prose was wrong" broke, but a DIFFERENT, older stale claim survived undetected through rounds 3-7: `TryDecodeNarrowModeHeader`'s own top-of-method doc comment still restated the search-ceiling formula inline as "guard(100ms) + timeout(100ms) + start-bit(22ms) + 24 data bits(24×22ms) + 200ms retry margin" = 950ms, omitting the 300ms leader term — this is the exact false claim round 3 fixed in the *code* (which has read `VisHeader.NarrowSearchCeilingMs` = 1250ms directly since round 3/4) but never swept from this OTHER comment restating the same formula by hand a second time nearby. Compounded by `VisHeader.cs:108-109` pointing readers at "that method's doc comment" as the formula's home — a circular reference to the wrong number. Also caught a stale in-file citation, pre-existing from an earlier round: "mirrors TryDecodeVisDataBits' own (:1207-1212)" pointed at unrelated `FskSpaceAt`/`D11At` code; the real search-ceiling logic is at `:4209-4211`. Fixed both: the top doc comment now says "Ceiling = VisHeader.NarrowSearchCeilingMs" with a round-8 note explaining the prior inline restatement was wrong and pointing to the constant's own doc comment as the real breakdown, and the citation corrected to `:4209-4211`. Comment-only. **Eighth round, eighth finding — but this is the first round where the finding was a STALE PRE-EXISTING claim missed by a sweep, not a NEW overclaim introduced by the immediately preceding round's own fix — a related but distinct failure mode (comment drift/incomplete sweeps vs. fresh overclaiming) worth distinguishing for D0's own future field-lifecycle pass.** **Round 9 — D6's FIRST CLEAN ROUND, explicit yes, zero blockers/zero risks.** Independently re-verified rounds 1-8's whole surviving claim set (not just round 8's two fixes) against legacy source and the actual current file, and re-ran the full 8-item checklist as a genuine confirmation pass, not a fishing expedition — found no new code bug (none since round 4). Did find 5 more documentation nits, all pre-existing or citation-drift, none behavioral: a mislabeled `VisHeader.cs` doc term ("guard-tone hold" vs. the real guard tone's own TX duration); the `:4209-4211` citation round 8 itself introduced was off by one line at both ends; a second surviving stale "~950ms" reference (should be ~1250ms) in the station-ID early-continue rationale, missed by all 8 prior rounds; round 8's own new prose said the code has read the shared ceiling constant "directly since round 3" when the in-body comment two paragraphs below says round 3 fixed the hand-transcribed value and round 4 replaced the hand-transcription with a direct constant read — a fresh self-contradiction; and a "~306ms" tolerance figure matching neither the plain 300ms arithmetic nor any other derivation. The auditor's own root-cause read: ~700 lines of accumulated audit-round prose in a chunk where every round adds more and shifts every in-file self-citation below it — recommended fixing the nits and stopping, or at minimum dropping in-file `:NNNN` self-citations for method-name pointers (legacy-file citations, checked broadly this round, have never once drifted). **User's explicit choice (2026-08-19, asked directly given round 9's clean verdict): fix the nits, drop remaining in-file self-citations for the durable fix, then still run round 10 for the second required clean confirmation** (DSP/decode-path chunk, CLAUDE.md §7's 2-consecutive-clean-round bar stays non-negotiable even this deep into the round count). All 5 nits fixed + every remaining in-file self-citation in the chunk (`:4209-4211`, both `:4477` instances) replaced with method-name/statement-name pointers; the two legacy-citation cross-references (`Main.cpp:7423-7424`, `VisHeader.cs:340/108-123`) were independently re-verified accurate by round 9 and left as-is. Comment/doc-only, zero code or test changes.
> - **Round 10 — D6's SECOND CLEAN ROUND, explicit yes, zero blockers/zero risks. D6 closed.** Independently re-verified all 5 of round 9's fixes against the actual current file (including re-deriving the legacy `sstv.cpp`/`Main.cpp` line numbers behind the `VisHeader.cs` guard-tone relabel, confirming both terms are independently correct rather than one clarified at the other's expense) and confirmed no new in-file self-citation was introduced. Did a full fresh 8-item sweep against legacy source one more time — no new code bug (none since round 4, across 10 rounds total). 4 nits found, all queued, none blocking: a stale cross-file citation (`VisHeader.cs:340` should be `:343` — the same drift failure this session eliminated for in-file citations, just one file over); a 7-line "below"/"above" directional inconsistency describing the same statement; a previously-undocumented ~1-sample (~0.09ms) hold-length difference from legacy's real trigger-to-lock timing (this port's confirm-hold counts the trigger sample itself, legacy's doesn't) — smaller than the `MsToSamples` rounding divergence this chunk already documents and deliberately accepts, not worth a code change; and one comment whose stated legacy-parity rationale doesn't quite match legacy's real (persistent, not per-attempt) state shape, though the resulting behavior is still correct since this whole path is documented dead code. None fixed — queued as nits per this playbook's own established precedent (D1/D4/D5/D7/D3+D8+D9 all closed the same way). **Final tally for D6: 10 rounds, 5 real code/architecture fixes (rounds 1-4, one escalated to and decided by the user), then 6 rounds (5-10) of pure documentation churn — every round from 4 through 9 found something wrong in a predecessor's own prose, a genuinely instructive case study in this playbook's own "fresh round, independent re-derivation" discipline paying for itself, and in why the playbook now recommends symbolic (method-name) over line-number self-citations for any comment likely to keep growing under repeated audit.** Full `tests/ScanlineStudio.Core.Sstv.Tests` suite re-run at closure: 954/954 passing (1 unrelated intentional skip).<br><br>**D2 (public surface/push/retention) — IN PROGRESS, round 5 fixed** (2026-08-19; last of the 10 mega-file chunks, run with the context of all 9 others already closed). The original 2026-08-18 boundary estimate (1216-1376 + 1953-2271) was stale in SHAPE, not just offsets — round 1 found the real public surface scattered across 8 separate regions (class decl ~44; diagnostic props + `Rel` ~206-296; `IsIdle` ~365; station-ID event ~684-708; **constructor** ~797-875, missed by the original estimate; events/props/`PushSamples` ~1239-1429; `TrimBuffers` ~1983-2327; `LockAnchorCommitted` ~3560-3567, missed; `Dispose` ~6304-6338, missed), plus the paired `ISstvDecoder.cs` interface (whole file) and `BufferTrimTests.cs`. Legacy-equivalence caveat stated rather than forced: `TrimBuffers`/`_rawSamples`/`Rel()` have NO legacy counterpart at all (`CSSTVDEM::Do` streams over a fixed ring, never accumulates raw samples) — judged on internal-invariant correctness, not parity; ctor defaults DO have real legacy counterparts and were verified against them. Round 1 found 3 real risk findings, all fixed: (1) `SyncOffsetSamples`'/`SlantPpm`'s own doc comments overclaimed a TOCTOU fix "removes that window entirely" — corrected to state precisely what's fixed (the throw) vs. not (true atomicity of a `Nullable<double>` struct read is a stale-value risk only, never a throw, never wrong pixel data) and to note the underlying "never torn" guarantee for plain `double` fields is an ECMA-335 64-bit-native-word-size guarantee, not universal, previously unstated; (2) `TrimBuffers`' pre-lock branch comment asserted "AFC/Slant trackers are always null pre-lock" — false during the AVT training-pending window (`AbandonInProgressImage` deliberately keeps both alive) — corrected to state the REAL safety mechanism (`InitializeAfc`/`InitializeSlant` re-base their own cursors forward on the next lock, so a stale abandoned-image tracker position never needs protecting from trimming); (3) the public `IDisposable` surface had NO use-after-dispose guard anywhere, with genuinely inconsistent behavior across `RxBufferMode`s (silent no-op on most paths, a real `ObjectDisposedException` from deep inside one disk-backed replay path) — fixed with an explicit `ObjectDisposedException.ThrowIf(_disposed, this)` guard at the top of `PushSamples`, documented on both the implementation and the `ISstvDecoder` interface. Also fixed the nit D6 round 10 explicitly deferred here: `ModeDetected`/`DecodeRestarted` had no CLAUDE.md §4 concurrency contract on the interface (`ISstvDecoder.cs`), unlike siblings `LineDecoded`/`StationIdDecoded` — both now documented. Full solution build clean (0 warnings/errors); scoped tests (`BufferTrimTests`, `AvtNoiseTolerantDetectionTests`, `RestartableSstvDecoderTests`) 40/40 passing. Queued nits (not fixed, round 1 precedent): ctor's `sampleRate` param is the one unvalidated argument (unreachable today, `RestartableSstvDecoder` pins 11025); `SenseLevelPresets` is a mutable-array-reference static field (never written, CLAUDE.md §3 technicality only); `BufferTrimTests.cs`'s own 15s-bound comment omits the `MinTrimSamples` amortization-slack term from its stated reasoning (the number is still correct); no subscriber-exception isolation or documentation of it on any event; a 2-line ctor-comment/code separation. Round 2 needed.
> - **Round 2** independently re-verified all 5 of round 1's fixes against actual current file state (including re-deriving the ECMA-335 native-word-size atomicity claim, tracing `InitializeAfc`/`InitializeSlant`'s re-base ordering line-by-line, and confirming the `ObjectDisposedException.ThrowIf` placement precedes all state mutation) — all held up. Found 2 real risk findings: (1) the round's one executable change (`PushSamples`' dispose guard) shipped with ZERO test coverage, against both CLAUDE.md §3 and this repo's own established pattern of a dedicated test for every other such guard; (2) the new `ISstvDecoder.PushSamples` doc now asserts an interface-wide post-dispose contract that `RestartableSstvDecoder` — the actual DI-registered implementation — did NOT honor: its `PushSamples` had no `_disposed` guard of its own, so a post-dispose call landing in the `Swap()` branch would construct a fresh, undisposed inner decoder and push to it successfully, silently violating the just-written contract AND leaking that new instance. Same overclaim shape this file's D6 track hit repeatedly, this time one layer up (a NEW doc comment asserting something not yet true of a DIFFERENT class). Both fixed: `ObjectDisposedException.ThrowIf(_disposed, this)` added as the first statement inside `RestartableSstvDecoder.PushSamples`' own lock (before the `Swap()` branches that would otherwise leak); two new tests (`BufferTrimTests.PushSamples_ThrowsObjectDisposedException_AfterDispose`, `RestartableSstvDecoderTests.PushSamples_ThrowsObjectDisposedException_AfterDispose`). Also fixed 2 nits: a dangling `<see cref="IDisposable.Dispose"/>` in the `ISstvDecoder.PushSamples` doc pointed at a member not reachable through that interface (`ISstvDecoder` doesn't extend `IDisposable`) — reworded to reference implementations that separately implement it; the `TrimBuffers` pre-lock comment stated only half its own safety argument (the re-base) without the other half (neither AFC/Slant cursor is ever read while `_mode is null`, which is what makes the re-base alone sufficient) — both halves now stated. Full solution build clean; scoped tests (`BufferTrimTests`, `RestartableSstvDecoderTests`, `AvtNoiseTolerantDetectionTests`) 32/32 passing, including the 2 new tests. Round 1's other queued nits re-derived by round 2 and confirmed as characterized, none more serious.
> - **Round 3** independently re-verified all 4 of round 2's fixes (re-checked every OTHER public mutator on `RestartableSstvDecoder` for the same Swap-leak hazard -- confirmed `PushSamples` really is the only one that matters, none of the others touch the disposable staging buffer) and found 2 real risk findings, both of the exact "new comment overclaims" shape this file's D6 track hit repeatedly, this time one layer removed: (1) round 2's own new `RestartableSstvDecoderTests` test used the PUBLIC ctor (12h/13h-sample thresholds), so its single 16-sample post-dispose push could never reach the `Swap()`-calling branch it claimed to pin -- it fell through to the already-disposed INNER decoder's own (round-1) guard instead, and would have passed even with round 2's wrapper guard fully reverted; (2) the reworded `ISstvDecoder.PushSamples` doc's "every implementation this codebase ships also implements IDisposable" claim was false for 3 of the 5 in-tree implementations -- two test-double `FakeSstvDecoder`s don't implement `IDisposable` at all, and a third does but deliberately doesn't enforce the throw. Both fixed: the test now uses the internal short-threshold ctor, primes the inner's sample count above the critical threshold before disposing, and asserts `RestartCountForTests` never moves (the actual leaked-`Swap()` signature) -- independently verified mutation-sensitive by temporarily reverting the wrapper guard and confirming the test fails with exactly the predicted "no exception thrown" signature, then restoring and re-confirming pass; the interface doc now scopes the dispose contract explicitly to the two production implementations, stating plainly that test doubles under `tests/` make no such promise. Full solution build clean; scoped tests 32/32 passing.
> - **Round 4** independently re-verified both of round 3's fixes (hand-traced the corrected test's actual current threshold values through `PushSamples`' actual current code and confirmed it genuinely reaches the `Swap()` branch this time; re-read all 5 in-tree `ISstvDecoder` implementations' actual dispose code directly, not round 3's characterization, and confirmed the re-scoped doc is accurate for all 5) — both held. Found 1 more real risk finding, the same "verified against one implementation, not both production ones" shape one layer further out: `SlantPpm`'s (and, via "same guarantee as SlantPpm above," 5 sibling properties') threading doc said "a plain field read... never throws" -- true only of `AnalogFmSstvDecoder`. `RestartableSstvDecoder` -- the actual DI-registered implementation the UI receives from `Program.cs` and polls on a timer -- takes an internal lock for every one of these getters; "never throws"/"never torn" both still hold, but the doc never mentioned a poll can genuinely BLOCK if it lands mid-`Swap()`, a worst case `RestartableSstvDecoder.Swap`'s own comment already accepts one layer down. Fixed: `SlantPpm`'s doc now describes both mechanisms explicitly and cross-references `Swap`'s own accepted blocking-duration comment; the 5 siblings inherit the fix automatically via their existing "same guarantee as SlantPpm above" phrasing, no separate edit needed. Comment-only. 4 more nits queued (not fixed, per this playbook's established precedent): a stale "Monitor chosen for re-entrancy" rationale in `RestartableSstvDecoder`'s own class-doc, contradicted by the round-3 fix two sentences later in the same paragraph (no reachable path still needs re-entrancy, though the lock stays harmless either way); `AutoSlantEnabled`'s "same reasoning as every constructor-injected toggle" is false for `StationIdDecodeEnabled`, explicitly not restart-only per the very next property -- same "every X" shape as round 3's finding, one property over; `StationIdDecodeEnabled`'s "kept in sync by every write path" comment omits the constructor as a third path (benign -- unpublished instance, `CreateInner` immediately re-applies); a critical-branch `Swap` mid-image fires `Restarted` but not `DecodeRestarted`, undocumented, reachable only after ~13h of continuous non-idle decoding. Full solution build clean; scoped tests 26/26 passing. Round 5 used the actual `/home/artien/.claude/agents/auditor.md` contract (fresh read-only agent, all 8 checks, prescribed verdict/gate) and found 2 blockers plus several real risks. Fixed the blockers: (1) a completed critical swap could lose both mandatory maintenance notifications if forwarding the triggering chunk threw -- `PushSamples` now attempts maintenance events from nested `finally` blocks, including `Restarted` if the critical handler itself throws, with a regression test that injects a disposed replacement; (2) `Swap` unsubscribed the outgoing decoder before constructing its replacement, so an Extended-mode scratch-backend construction failure left the installed decoder disconnected -- replacement construction/subscription is now transactional, with a fault-injected test proving the outgoing decoder remains installed and observable. Also fixed: the round-4 wrapper-lock doc overclaim (the lock stabilizes inner selection, not concurrent inner-field mutation); explicit scheduler/slow-subscriber semantics for all maintenance events; non-positive `sampleRate` validation + tests; full bounded-retention observation for the 4 previously-unchecked core caches; and an end-of-image assertion in the decode-after-trim test. Focused build clean; D2 tests 30/30 passing. Queued for later scoped work: failure-robust teardown inside `RxDiskLineStagingBuffer.Dispose` (Batch 2's buffer/concurrency scope), remaining constructor-forwarding coverage gaps, the internal-only throwing `LockAnchorCommitted` test hook, and the already-known ~13h critical mid-image swap's lack of `DecodeRestarted`. Round 6 needed.<br><br>**D3+D8+D9 (ReSync/AutoSync/AutoStop + slant tracking + replay/CorrectSlant, coupled) — done, 4 rounds** (2026-08-18; the historically-tricky "local-vs-absolute index" bug nest, CLAUDE.md §4). **This round genuinely reproduced the bug class it exists to catch** — worth reading in full if working near this field cluster again. Round 1 built the priority coordinate-space table across the 8 shared fields and found one real gap: `_rxBufferAnchorSample` (defines a raw-sample↔staging-buffer-local-index map) wasn't compensated when `TryAppendLine` rejected a line at RAM capacity. Round 1's OWN fix was itself wrong — it advanced the anchor on every rejection, based on a "count invariant" phrasing in the field's own (then-current) doc comment, which round 2 proved was never actually this field's contract: the anchor defines a FIXED coordinate map (unchanged between re-anchor events), not a running staged-content tally, and `PerformReplay`'s own `resumeDest` computation explicitly needs the live cursor's true position, not the staged extent. Round 2 caught this with a full worked numeric trace showing round 1's fix would silently re-stamp already-correct image rows with stale audio and leave the bottom of the image undrawn. Reverted the fix, rewrote the field's doc comment to state the coordinate-map definition as primary, and replaced the (also-wrong) regression test with one that asserts the actual consequence (`PerformReplay` resumes from the true live position, not the staged extent) rather than a bookkeeping proxy — independently verified mutation-sensitive by temporarily re-introducing the round-1 bug and confirming the new test fails with the exact predicted signature. Round 3 did a genuinely fresh, from-scratch re-derivation of the whole revert (not just re-reading round 2's summary) and found one more thing: a stale doc comment on the `RxBufferAnchorSampleForTests` test accessor still asserted the repudiated invariant, directly contradicting the field's own already-corrected comment — the exact resurrection vector for the original bug. Fixed (2-line doc edit) plus two test nits (a wrong "headroom" comment, and a one-sided assertion tightened to two-sided). Round 4 (clean round, soft cap reached at 4) independently re-derived the coordinate-map semantics AGAIN from scratch, rebuilt the full 13-quantity coordinate table, and reconfirmed the revert, the doc fixes, and the test's mutation-sensitivity in both directions — explicit yes, zero blockers/risks. Also fixed in this track: F2 (the automatic replay drain was the only one of 3 entry points missing the `!_slantCorrectionsDisabledForRestOfImage` gate its siblings have — added); F4 (`LineDecoded` had no CLAUDE.md §4 concurrency contract, unlike its sibling `StationIdDecoded` — added); F5 (`HasWriteFailed`'s doc comment claimed an Application-layer observability path that doesn't actually exist — corrected to state plainly it's not observable today). Full `tests/ScanlineStudio.Core.Sstv.Tests` suite: 945/945 passing (1 unrelated intentional skip) throughout. Explicitly deferred (not fixed, flagged in code): F3 (`TryCorrectSlant`'s entry gate uses cumulative line count, its internal scan uses local-only count — usually benign, not proven safe in all cases, no test); a rejected-line-then-later-shorter-accepted-line physical buffer discontinuity (same class as the already-deferred `DrainPendingSkip` mid-buffer-hole gap). Queued nits: one more (weaker, historical-narration-only) instance of the repudiated invariant phrasing; an undocumented-but-provably-harmless clamp asymmetry between the replay row loop and its own reconciliation step.<br><br>**D4 (main loop + image teardown, `TryProcessBuffer`/`EndOfImage`) — done, 4 rounds** (2026-08-18; real boundary ~2290-2745, drifted from earlier chunks' own comment growth). Round 1 found 3 blocking items, all real: (1) an undocumented `Math.Clamp` lower bound in the live per-line reader silently substituted the oldest retained sample for any trimmed-away index instead of letting `Rel()` throw as designed — fixed by dropping the lower bound (`Math.Min` only; the upper bound stays, load-bearing); (2) `EndOfImage` fires 1-2 transmission lines earlier than legacy's own `m_AY > SSTVSET.m_L` overshoot check — no pixel divergence, judged not worth matching (perturbs every back-to-back-transmission tolerance for zero benefit; the encoder footer this exposes to the next header search has no 1200Hz sync structure or 300ms+ leader to false-lock on) — resolved by correcting the doc comment to state the real divergence instead of falsely claiming a byte-exact port; (3) two real test-coverage gaps (the 0.5s dead-time constant and `applyDeadTime:false` were both only reachable indirectly, via tests whose own comments admit a wrong value would likely still pass) — fixed with a new `EndOfImageForTests` internal test-only wrapper (same "internal for testability" convention as D1) + two new tests pinning both values directly. Round 2 re-scanned fresh and caught a real bug in round 1's OWN fix: the corrected doc-comment's numbers were wrong (understated Martin M1's gap 3.2x, self-contradicted its own overshoot-count sentence for Scottie) — round 2 independently re-derived the correct figures from `SstvModeRegistry.cs`'s own `LineSegments` (Martin M1: 446.446ms×2=0.89s; Scottie DX: 1050.3ms×1=1.05s) and required the fix before the gate could pass; also caught that round 1's justification for keeping the upper clamp bound was itself factually wrong (not KSB-peek-ahead-related; `PixelSampleReader.ReadPeakPicked` already guards that case). Both fixed, doc-only, zero behavior change. Round 3 (1st clean round) and round 4 (2nd clean round) each independently re-derived every numeric claim from legacy source and the registry rather than trusting the prior round's summary — round 3 additionally spot-checked 2 more mode families (Robot 36, PD90) to confirm the overshoot framing generalizes; round 4 refined one bound estimate (Scottie DX's intra-line rounding error is the worst case in the registry, 0.53 samples not Martin's 0.22, still comfortably sub-pixel). Both explicit yes, zero blockers/risks. Full `tests/ScanlineStudio.Core.Sstv.Tests` suite: 944/944 passing (1 unrelated intentional skip). Queued nits (comment-wording/citation only, no behavior impact): `_nextLine >= ImageHeight` comment says "transmission lines" but counts bitmap rows; a legacy line-number citation points at the wrong (but equivalent) branch for two named example modes; several stale in-file line-number self-references from comment growth across rounds; "by construction" overstates an intra-line rounding bound that's actually bounded, not exact.<br><br>**D1 (cache/index substrate) — done, 3 rounds.** Round 1 (real boundary turned out to be lines 279-289 `Rel()` + 856-1179 the `*At` family, not the originally-guessed 44-1210) found 2 real risk findings, both about missing test coverage for real silent-failure paths (not wrong production logic): `Rel()`'s below-base throw guard (the dangerous failure class this file's own doc comment names is a *silent wrong answer*, not a throw) had zero direct test coverage since `Rel` was `private`; the narrow-mode `isNarrow` demod gate had no diagnostic or test, unlike its structurally-identical sibling H1/H2 bandpass gate (which got one after an earlier finding) — and round 1 flagged as an unverified assumption whether the demod cursor genuinely trails the lock anchor for narrow modes the way the sibling gate's fix relies on. Fixed: `Rel` bumped `private`→`internal` (matches this class's existing "internal for direct testability" convention) + new test `BufferTrimTests.Rel_Throws_WhenAbsoluteIndexIsBehindTheTrimWatermark`; new `FirstNarrowDemodIndex` diagnostic property (mirrors `FirstLockedBandpassIndex`) + new test `BandpassCacheChunkInvarianceTests.FirstNarrowDemodIndex_EqualsTheLockAnchor_ForANarrowMode`. Round 2 verified both fixes, independently re-derived from legacy `sstv.cpp`/`VisHeader.cs` constants that the narrow-gate premise genuinely holds (not just "the test happened to pass") — explicit yes, D1's 1st clean round. Round 3 (2nd required clean round) re-derived everything independently rather than trusting round 2's summary, re-verified the legacy-parity claims from scratch, and actually corrected round 2's own margin estimate upward (the narrow-gate cursor trails the anchor by ≥1 full retention window + `AnchorWarmupSamples`, not the ~570ms round 2 estimated) — explicit yes, zero blockers/risks. D1 closed. Full `tests/ScanlineStudio.Core.Sstv.Tests` suite: 942/942 passing (1 unrelated intentional skip). Queued nits: no "locked-UNTIL" bound on either gate (no observable effect, verified); `Rel` throw-test asserts on a message substring (brittle to reword, `Assert.Throws` alone already carries the guarantee); duplicated 30s-noise test setup (cosmetic); one newly-surfaced comment gap (port's pre-lock demod feed window vs. legacy's `if(m_Sync)`-gated feed is a deliberate, documented-elsewhere divergence that isn't cross-referenced at this call site) — none block, none require a production-code change. |
|  | **D2 round 6 continuation (2026-08-19)** | A fresh audit found a real app-wide sample-rate split: capture honored persisted `AudioDeviceSettings.SampleRate`, but decoder/waterfall and restart thresholds assumed 11025 Hz. After 9 read-only plan reviews (final verdict ready) and 3 read-only code reviews (final verdict `EQUIVALENT`, zero findings, explicit sign-off), the coupled fix now provides a shared whole-Hz 5000..48500 policy; legacy's distinct startup-vs-Options invalid-value behavior; decoder-authoritative capture/encoder/waterfall wiring; rate-aware warning/critical/safe-index thresholds; transactional rate-validated factories; safe-ceiling preflight; a named, mutation-pinned composed absolute-index horizon; and manual Correct Slant representability/projection guards. The direct low-level decoder still permits positive out-of-policy rates for focused tests/tools, explicitly without the wrapper's long-session guarantee. Legacy fractional rates remain unsupported. Endpoint 5000/48500 round trips are labeled self-consistency only, not legacy golden vectors; 5000 Hz Robot 36 uses a documented 20-delta tolerance because current filter edges meet/exceed Nyquist. Verification: solution build clean; focused rate/restart/manual tests 58/58; actual Host registration 1/1; session 52/52; Options UI 61/61; golden-vector plus round-trip filter 163/163. Round 6 found/fixed a real issue, so it is not a clean confirmation; round 7 is next. |
|  | **D2 round 7 continuation (2026-08-19; supersedes the stale round-7-pending phrase in the mega-row above)** | The fresh full audit found a real decode-event failure boundary bug: a throwing subscriber could interrupt partially committed decoder state while the production session caught the exception and continued. It also found failure-fragile disk teardown, overclaimed telemetry coherence, and missing mutation-sensitive Host registration coverage. After 3 read-only plan reviews and the full cap of 3 read-only code reviews, direct and restartable pushes now reject recursive/concurrent overlap; all decode/maintenance subscribers are attempted; subscriber failures are deferred to a stable operation boundary; and internal decoder failures take precedence. Disk teardown attempts/logs each stage independently and drains pooled buffers still queued when timed-out consumers later exit through disposed semaphores. Interface telemetry is explicitly best-effort and may be stale, torn, or cross-epoch. Actual Host registrations pin invalid persisted-rate fallback and logger propagation to initial/replacement Extended decoders. Code review 1 was clean; review 2 exposed logger-propagation and real-resource/timeout test gaps; review 3 exposed the queued-buffer leak. All findings were fixed, but round 7 is not a clean confirmation and no fourth code review is permitted. Final verification: Core.Sstv 1010 passed + 1 intentional skip; Host composition 4/4; session 52/52; solution build clean with 0 warnings/errors. **User-requested pause:** stop here. A fresh session starts with full D2 round 8, the first possible clean confirmation; rounds 8+9 are the minimum remaining if both are clean, then D0. |
|  | **D2 round 8 continuation (2026-08-19)** | Fresh full re-derivation (not a re-read of round 7's summary) confirmed every round-7 claim holds against current code and found no new production bug or stale citation. One real test-integrity gap: `CreateInner` forwards `afcEnabled`/`syncRestartEnabled`/`autoSyncEnabled`/`autoStopEnabled`/`senseLevel` exactly like every other constructor-injected toggle, but none of the five had an `Inner*ForTests` accessor able to prove any of them survive a periodic restart rebuild — the identical silent-revert-to-default regression class the existing `InnerDemodTypeForTests`/`InnerRxBpfPresetForTests`/`InnerRxBufferModeForTests` accessors already exist to catch. Fixed: 5 new `AnalogFmSstvDecoder.*ForTests` properties + 5 matching `RestartableSstvDecoder.Inner*ForTests` passthroughs (same mirrored pattern), plus one new combined mutation-pinning test exercising all five non-default through a forced swap. Also fixed a `using` leak nit (`RxBufferMode_ConstructorValue_SurvivesAPeriodicSwap` never disposed its `RxBufferMode.Extended` instance, leaking 2 scratch files). Comment/accessor/test-only, zero production behavior change — treated as mechanical/pattern-mirrored per CLAUDE.md §7's own carve-out rather than a full plan+code review cycle, since the mirrored pattern was already independently reviewed 3 times over in earlier rounds. Verification: scoped filter (`RestartableSstvDecoderTests`/`BufferTrimTests`/`AvtNoiseTolerantDetectionTests`) 51/51; full solution build clean, 0 warnings/errors. Round 8 found/fixed a real issue, so it is not a clean confirmation; round 9 is next, still the first possible clean confirmation. |
|  | **D2 round 9 continuation (2026-08-19)** | Fresh full re-derivation confirmed every round-8 claim held (all 5 new accessor pairs, the new `_senseLevel` field, the new combined test's non-vacuousness) and found no new production bug. Found the same bug class one layer further out and with a bigger blast radius: `ScanlineStudio.Host/Program.cs`'s actual DI composition root (`CreateSstvDecoder`) forwards 8 `SstvDecoderSettings` fields into `RestartableSstvDecoder`, but `SstvCompositionRootTests` only pinned `sampleRate` and `rxBufferMode` — dropping any of `afcEnabled`/`syncRestartEnabled`/`autoSyncEnabled`/`autoStopEnabled`/`autoSlantEnabled`/`senseLevel`/`demodType`/`rxBpfPreset` compiled clean and left the whole suite green while a persisted Options > Decode setting would never reach the decoder at all (not just after a periodic restart, the narrower risk round 8 fixed). Fixed: widened `Core.Sstv`'s `InternalsVisibleTo` to also grant `ScanlineStudio.Host.Tests` (needed to read round 8's `Inner*ForTests` accessors from that assembly), and added one new composition-root test asserting all 8 settings survive real DI resolution with non-default values. Also fixed 2 nits in round 8's own new test: a comment overclaim ("every … call site … passes afcEnabled: true" — 2 sites actually pass nothing at all, relying on the public ctor default) corrected to state precisely what's proven (nothing in the file ever pins `false`), and a missing `using` (harmless today — default `RxBufferMode.On` uses the no-op-`Dispose` RAM buffer — fixed for consistency with this file's other tests). Verification: scoped Host.Tests 5/5, scoped Core.Sstv filter 51/51, full solution build clean. Round 9 found/fixed a real issue, so it is not a clean confirmation; round 10 is next, still the first possible clean confirmation. |
|  | **D2 round 10 — D2's FIRST CLEAN ROUND** (2026-08-19) | Fresh full re-derivation of every legacy-backed ctor default, the `TrimBuffers` 9-buffer retention proof, the `RestartableSstvDecoder` swap/overflow invariant, `RxDiskLineStagingBuffer` teardown timing, and all 11 composition-root arguments — explicit yes, zero blockers, zero risks. Only finding: round 9's own new comments miscounted/misnamed the call sites they described (a citation-drift nit, no behavioral or coverage consequence, same class this file's D6 track saw repeatedly) — fixed by replacing the specific-site enumeration with the durable load-bearing claim itself, so the comment can't drift the same way again. Verification: full solution build clean. D2 has its first of the required 2 consecutive clean rounds; round 11 is next. |
|  | **D2 round 11 — D2's SECOND CLEAN ROUND, D2 CLOSED** (2026-08-19) | Genuinely fresh full re-derivation of the ENTIRE D2 surface (not just round 10's diff) — all 9 legacy-backed ctor defaults, the 9-buffer `TrimBuffers` retention proof, `IsIdle`'s uncommitted-state sweep, `RestartableSstvDecoder` overflow/swap-transactionality, the full composition-root argument trace, concurrency/scheduler contracts, and test-integrity for every paired test — came back explicit yes, zero blockers, zero risks. Two comment-only nits, both fixed: round 10's own replacement comment miscounted (said "the other 3", meant "the other 5"); two pre-existing stale doc citations (`TrimBuffers`' intro claiming a fixed "5 growing sample buffers" when it now trims 9, reworded to not hardcode a count that can drift again; an `AvtNoiseTolerantDetectionTests.cs` comment citing a `PixelSampleReader` lower-bound clamp a D4 round-1 fix had already removed, reworded to state the current throw-not-clamp behavior). Verification: full `tests/ScanlineStudio.Core.Sstv.Tests` suite 1011/1011 passing (1 unrelated intentional skip), solution build clean. **D2 closed — its second of the 2 required consecutive clean rounds.** Per this playbook's own "Chunking a mega-file" structure, D0 (final whole-file field-lifecycle-only pass) is next; after D0 closes, the entire D1-D9+D0 `AnalogFmSstvDecoder.cs` track is complete. |
|  | **D0 round 1 — NOT clean** (2026-08-19) | Whole-file field-lifecycle sweep (every mutable field enumerated, checked against every reset entry point: `Dispose`, `EndOfImage`, `AbandonInProgressImage`, `Commit`, `PerformForceMode`, `InitializeAfc`/`InitializeSlant`, `PerformReplay`, `TryResolveAvtTraining`'s abort paths, `DrainPendingSkip`, the `PushSamples` exception/deferral unwind). Two real risks found and fixed: (1) `_zeroCrossingDemodulator` (main-demod-role instance, `DemodType.ZeroCrossing`) was never `Clear()`ed at any teardown path, unlike its sibling `_afcZeroCrossingCounter` — added the matching `SetWidth`/`Clear()` pair at both `EndOfImage` and `InitializeAfc`; (2) `SlantPpm`/`SyncFrequencyCorrectionHz` leaked an abandoned image's stale tracker values, attributed to the NEW mode, for the whole pending-anchor-correction window after a non-AVT mid-reception restart (~1.3-3.2s) — both getters now also gate on `_pendingAnchorCorrectionMode is null`; corrected an `ISstvDecoder.SyncFrequencyCorrectionHz` doc overclaim that this could "never leak." Also fixed a legacy-fidelity nit: `PerformForceMode` was missing the sync-interval-tracker reset legacy's own force-mode-specific `Start(int,int)` performs (behaviorally inert today, fixed for fidelity and to remove a silent gap). Both real fixes got new regression tests (`FieldLifecycleTests.cs`), each independently mutation-verified (temporarily reverted, confirmed the exact predicted failure signature, restored). A third finding — most of `EndOfImage`'s ~25 reset fields and all of `AbandonInProgressImage` have no direct test coverage — was NOT fully addressed this round; queued for D0 round 2 to assess. Verification: full `tests/ScanlineStudio.Core.Sstv.Tests` suite 1013/1013 passing (1 unrelated intentional skip), solution build clean. Round 1 found real issues, so it is not a clean confirmation; round 2 is next, still the first possible clean confirmation. |
|  | **D0 round 2 — NOT clean, found a real regression IN round 1's own fix** (2026-08-19) | Fresh whole-file field-lifecycle re-derivation. Round 1's `SlantPpm`/`SyncFrequencyCorrectionHz`/`PerformForceMode` fixes all re-verified correct. Round 1's zero-crossing fix was HALF wrong: the new `_zeroCrossingDemodulator.SetWidth(...)` calls at `EndOfImage`/`InitializeAfc` were out-of-band from the edge-tracked `_mainPathIsNarrow` flag `DemodulatedFrequencyAt` owns, desyncing the flag from the object's real width state and letting a later edge-check wrongly skip a needed `SetWidth` call for a state-dependent window of pre-anchor samples. Fixed by DELETING both `SetWidth` calls, keeping only `Clear()` -- verified algebraically equivalent (rescaling an already-cleared value through `SetWidth` lands on the identical final state either way) and restores the pre-D0 self-correcting design D1's own audit already covered. Also fixed 2 nits matching this file's own established defensive-clear convention: `TryResolveAvtTraining`'s two commit exits now null `_avtTrainingLock`/`_avtPllDemodulator` too (matching its two narrow-FSK exits); `EndOfImage` now clears `_pendingAnchorCorrectionMode` too (matching `AbandonInProgressImage`'s identical clear). One finding queued, not fixed: `InitializeAfc`'s `Clear()` can apply while `_demodulatedFrequenciesProcessedUpTo` still trails the anchor by up to ~16k samples, so it clears against a cursor that then processes chronologically-earlier audio -- bounded (filter re-settles within a few samples, never read as picture data), documented in place, not chased further. Also queued: no NEW regression test was added for the exact `_mainPathIsNarrow`-desync scenario itself (would need new internal accessors into `ZeroCrossingFrequencyCounter`'s width-clamp state to test precisely) -- the fix is a deletion restoring already-D1-audited behavior, and the existing `SstvRoundTripTests` narrow-mode `DemodType.ZeroCrossing` round-trip test provides some indirect coverage, but round 3 should independently judge whether that's sufficient. Round 2 also independently re-judged round 1's own queued test-coverage question (most of `EndOfImage`'s ~25 reset fields, all of `AbandonInProgressImage`, untested directly) and concluded it does NOT block a clean verdict on its own -- existing composed-chain tests would likely catch a missing-reset regression, and neither of round 1's 2 real bugs was of a shape those tests would have caught anyway. Verification: full `tests/ScanlineStudio.Core.Sstv.Tests` suite 1013/1013 passing (1 unrelated intentional skip), solution build clean. Round 2 found a real issue (introduced by round 1 itself), so it is not a clean confirmation; round 3 is next, still the first possible clean confirmation. |
|  | **D0 round 3 — D0's FIRST CLEAN ROUND** (2026-08-19) | Fresh full field-lifecycle re-derivation (~75 mutable fields against every teardown path) — explicit yes, zero blockers, zero risks. Independently re-derived round 2's "algebraically equivalent" SetWidth-deletion claim from the actual `ZeroCrossingFrequencyCounter` arithmetic and found it bit-exact, not just approximately equal. Confirmed `_mainPathIsNarrow` is now a genuine single-writer invariant (exactly one `SetWidth` call site per demod instance, both edge-guarded, matching legacy's own `m_fNarrow` guard). Independently re-judged both of round 1/2's open questions and reached the same "does not block" conclusions, but for sharper, independently-derived reasons (in particular: `TryInterleavedHeaderScan`'s own unconditional `_syncBypassProcessedUpTo == _visLockProcessedUpTo` runtime throw is a live production assertion that would itself catch a missing cursor reset, stronger than any test). 3 nits found, all comment-accuracy on already-inert paths (an understated bound in round 2's own new comment, a round-1 comment that doesn't name the actual reachability argument for its own "behaviorally inert" claim, and a pre-existing slightly-loose invariant description) — queued, not fixed, per this playbook's own established precedent for optional wording nits on a clean round. D0 has its first of the required 2 consecutive clean rounds; round 4 is next — the 4th round, at (not past) the soft cap. |
|  | **D0 round 4 — NOT clean, real bug found at the soft cap** (2026-08-19) | Fresh full field-lifecycle re-derivation found one real, reachable defect: `_pendingSkipSamples` (an Auto-Sync-triggered read-cursor skip, `ApplySyncCorrection`) had exactly one drain site, `DrainPendingSkip()` at the top of `PushSamplesCore` — correct for a streaming caller (any chunk shorter than one line), but a bulk caller pushing a whole transmission in one `PushSamples` call (WAV/file decode, `GoldenVectorTests`, `AutoSyncTests`' own bulk-push tests) never lets a second `PushSamplesCore` call arrive to drain a skip `TriggerAutoSync` requests mid-decode — so it sat pending, unapplied, until `EndOfImage`'s `ResetReSyncState()` silently zeroed it, while `ApplySyncCorrection`'s OTHER side-effects (`_slantCorrectionsDisabledForRestOfImage`, the Auto-Sync cooldown) still applied regardless — paying the suppression cost without the realignment it exists to buy. The exact same caller-chunk-boundary bug class `_pendingReplayRequested`'s own round-1 fix already exists to prevent, left unfixed for this sibling flag. Fixed by adding a second `DrainPendingSkip()` call inside `TryProcessBuffer`'s per-line loop, at the same decoded-line-boundary statement position `_pendingReplayRequested`/`_correctSlantRequested` already drain at (immediately after `ApplySlantTracking()`, before those two drains so `PerformReplay`'s own cursor-bounded inputs see the post-skip-drain state, matching legacy's own real-time interleaved order). New regression test in `AutoSyncTests.cs` reusing the existing splice-trigger technique, observing `PendingSkipSamplesForTests` (a pre-existing accessor) across every `LineDecoded` event rather than just the trigger count — independently mutation-verified (reverted, confirmed the exact predicted "stuck at 152 across multiple lines" failure signature, restored). Verification: full `tests/ScanlineStudio.Core.Sstv.Tests` suite 1014/1014 passing (1 unrelated intentional skip), solution build clean. Round 4 found a real issue at the 4-round soft cap — per the playbook's own rule, stopping here rather than auto-dispatching round 5; whether to spend a 5th round confirming this fix is the user's call. |
|  | **D0 round 5 — clean, past the soft cap with the user's explicit go-ahead** (2026-08-19) | Fresh full field-lifecycle re-derivation, explicit yes, zero blockers, zero risks. Specifically re-derived round 4's own fix (the second `DrainPendingSkip()` call site) against all 5 fields it touches, the loop's own `_consumedSamples == round(_idealLineStartSample)` invariant, buffer-end safety from the new mid-loop position, and its interaction with the two pre-existing `_correctSlantRequested`/`_pendingReplayRequested` drains it runs before — all correct. Also independently re-derived round 2's algebraic-equivalence claim again (still bit-exact) and confirmed no unfixed sibling gap exists for `_pllDemodulator`/`_demodulator` (legacy genuinely never resets either, confirmed directly against `sstv.cpp`). 2 nits found and queued, not fixed: round 4's own new comment overclaimed why the drain must run before the replay drains (the real reason is simpler — the combination is structurally unreachable, not that the drain changes replay's inputs); a deferred-gap comment's stated reachability argument is weaker than what the code actually guarantees. D0's clean streak restarted here (broken by round 4) — round 6 is needed to actually close D0. |
|  | **D0 round 6 — NOT clean, 2 new findings (streak broken again)** (2026-08-19) | Fresh full field-lifecycle re-derivation found 2 real risks, neither raised in rounds 1-5, both bounded (no wrong pixels/corruption/crash on any reachable path today). (1) `PerformForceMode`'s round-1 sync-interval-tracker resets were incomplete: they reset the trackers' internal counters without the paired `_syncBypassOriginSample`/`_syncBypassProcessedUpTo` fields `EndOfImage`'s matching reset block always resets alongside them — leaving a worse-formed pairing than before the reset, the opposite of round 1's own stated defensive intent. Provably unreachable today (`Commit()` makes `_mode` non-null on the very next line), but round 2 already fixed a sibling half of this same round-1 change block and didn't re-check this half — the exact recurrence pattern this chunk's discipline exists to catch. Fixed by adding the two paired field resets, matching `EndOfImage`'s already-correct pattern exactly. (2) `ResetAgc()` was the one public mutator on this class with no stated threading contract, unlike its three deferred-flag siblings (`RequestReSync`/`RequestCorrectSlant`/`ForceMode`) — it writes `LevelAgc` state directly and synchronously, and the production wrapper (`RestartableSstvDecoder.ResetAgc`) genuinely calls it outside its own lock by design, so a caller invoking it at the wrong moment can race the decode thread (bounded: a torn read briefly under-clamps AGC'd samples for one ~100ms window, never wrong pixel data). Fixed by documenting the contract explicitly on `ISstvDecoder.ResetAgc`, the base implementation, and the wrapper forward. Verification: full `tests/ScanlineStudio.Core.Sstv.Tests` suite 1014/1014 passing (1 unrelated intentional skip), solution build clean. D0's clean streak is broken again — round 7 is next, the first candidate for a fresh 2-consecutive-clean-round close (this is now past the already-user-approved soft-cap extension; continuing without a fresh ask since the user's prior go-ahead covered "run round 5" as the specific unblock, and this is a routine continuation of the normal 2-clean-round requirement, not a new cap threshold). |
|  | **D0 round 7 — NOT clean, 1 real bounded risk + 3 nits (streak broken again)** (2026-08-19) | Fresh full re-derivation, including a systematic sweep for any OTHER half-applied fix from rounds 1-6 (the recurring failure shape this chunk keeps hitting) — swept clean, no other paired-field gap found. One real finding: `PerformReplay`'s cursor jump never feeds the skipped raw-sample span through `_syncEnvelopeDetector` (unlike `DrainPendingSkip`'s own cursor jump, which explicitly does, for history continuity), so the detector resumes with a discontinuous input after a replay — a real, previously-undocumented port-internal divergence (legacy has no cursor-jump equivalent to compare against). Bounded and no concrete failure could be constructed: the ~3-10ms resonator transient affects at most one post-replay line's own sync measurement, and `TryAutoSync`'s own small-step gate structurally filters a lone settling-transient outlier. A real fix would need `DrainPendingSkip`'s own incremental/deferred shape (the jump can exceed `TotalSamplesReceived` in one step) — a real architectural addition, not a one-liner. Documented in place as an accepted divergence rather than built out speculatively; also corrected the adjacent comment's factually wrong "feeds it through a second time" claim (it's never fed a first time). 2 comment nits fixed (a stale `ReplayEngineTests.cs` comment citing `DrainPendingSkip`'s now-superseded single-call-site limitation; a ctor comment sitting one statement above the line it actually describes). 1 nit queued, not fixed: a genuine but tiny (~0.09ms, fully absorbed by the anchor-correction fold) one-sample bias between `_syncBypassOriginSample`'s anchor convention and `SyncIntervalTracker`'s pre-increment counter — consistent everywhere it's used, explicitly NOT a half-applied fix. Verification: full `tests/ScanlineStudio.Core.Sstv.Tests` suite 1014/1014 passing (1 unrelated intentional skip), solution build clean. Streak broken again — round 8 is next. |
|  | **D0 round 8 — NOT clean, 1 real bounded risk + citation-drift nits (streak broken again)** (2026-08-19) | Fresh full re-derivation confirmed every prior round's fix and claim correct, including a full numeric re-verification of round 2's algebraic identity and round 7's PerformReplay/DrainPendingSkip feed-count derivation. One real finding: `TryCorrectSlant` is the only writer of `_effectiveSamplesPerLine` that commits MID-LINE (the automatic path always commits at a line boundary), and relied entirely on `PerformReplay`'s own downstream reseed to keep `_slantIdealSamplesSoFarInLine` within the new line width — if `PerformReplay` bails early on a `RxBufferMode.Extended` disk-write failure, that reseed never runs, leaving the accumulator holding a value valid against the OLD width but not the new one, producing one spurious immediate line-boundary. Bounded (`RxBufferMode.Extended` + a write failure only, one bogus observation, no pixel corruption) but cheap to close — fixed by normalizing the accumulator (`%= _effectiveSamplesPerLine`) immediately after the width commit, making correctness independent of what the caller does next. Also fixed a cluster of stale in-file line-number self-citations (5 in `AnalogFmSstvDecoder.cs`, 1 in `AutoSyncTests.cs`, all 250-690 lines off) by dropping them for symbolic/positional descriptions — the exact citation-drift hazard this playbook already recommends avoiding, and one this specific D0 chunk has now hit multiple times. Verification: scoped `CorrectSlantTests`/`CorrectSlantRequestTests` 16/16, full `tests/ScanlineStudio.Core.Sstv.Tests` suite 1014/1014 passing (1 unrelated intentional skip), solution build clean. Streak broken again — round 9 is next. |
|  | **D0 round 9 — NOT clean, real gap in round 8's own fix (streak broken again)** (2026-08-19) | Fresh full re-derivation, including a full re-verification of round 8's `%=` normalization (confirmed the operand is always non-negative, the divisor always `>= 1`, and modulo — not a clamp or absolute recompute — is the mathematically correct operation, matching both legacy's own phase math and this file's two existing `PerformReplay` reseed sites; also caught and corrected an inaccurate `[0,1)` bound claim in round 8's own new comment, per `SlantTests.cs`'s own history of that exact bound being tried and failing). Real finding: round 8's fix normalized ONLY `_slantIdealSamplesSoFarInLine`, but this file's four other re-anchor sites (`InitializeSlant`, `ProcessSlantTrackingSample`'s own line-boundary tail, and both of `PerformReplay`'s own resets) always move that accumulator TOGETHER with 4 paired per-line envelope fields (`_slantLineEnvelopeSeeded`/`_slantLineMaxEnvelope`/`_slantLineMinEnvelope`/`_slantLinePeakPosition`) — on the exact `HasWriteFailed`-bail path round 8's fix targets, those 4 fields stayed un-re-anchored, so the line completing after the bail could measure its sync peak against a value expressed in pre-wrap coordinates. Fixed by adding the same 4 resets, matching all 5 sibling sites' shape exactly. Also fixed a same-window nit the auditor flagged as worth folding in: `RecomputeAutoSyncThresholds()`'s only call site was `PerformReplay`'s own top, so a bail also left Auto-Sync's cluster/step thresholds derived from the pre-correction line width — now called at the same fix point too (harmless redundant call on the non-bail path). One nit queued, not addressed: no direct test covers this exact scenario, since the precise `HasWriteFailed`-after-commit interleaving has no current injection seam to force deterministically (matches the auditor's own inability to construct a failing fixture); the code fix itself mirrors 4 already-tested sibling sites exactly. Verification: scoped `CorrectSlantTests`/`CorrectSlantRequestTests`/`AutoSyncTests` 23/23, full `tests/ScanlineStudio.Core.Sstv.Tests` suite 1014/1014 passing (1 unrelated intentional skip), solution build clean. Streak broken again — round 10 is next. |
|  | **D0 round 10 — NOT clean, third consecutive round finding a gap in the immediately-preceding round's own fix (streak broken again)** (2026-08-19) | Fresh full re-derivation. Real finding, one layer up from round 9's own fix: round 9 correctly re-anchored the accumulator/envelope fields to cover `PerformReplay`'s checkpoint-1 bail, but `TryCorrectSlant` itself still commits `_effectiveSamplesPerLine` and calls `_slantTracker.AdoptCorrectedRate(...)` UNCONDITIONALLY before `PerformReplay` ever runs — on that exact bail, those commits are left stranded, correctly re-anchored accumulator/envelope state now paired with a rate that was never actually applied. Same bug class rounds 8/9 kept patching, one layer higher. This is the third round in a row (8→9→10) finding a gap in the immediately-preceding round's own fix — escalated to the user via `AskUserQuestion` given that pattern rather than auto-continuing with a 4th incremental patch. **User explicitly chose a structural fix** over a minimal patch or pausing: restructure `TryCorrectSlant`/`PerformReplay` so a disk-write-failure bail reverts the WHOLE correction, matching legacy's own commit-or-revert shape (`Main.cpp:5415-5423`) exactly, instead of patching around the gap again. |
|  | **D0 structural fix, implementing round 10's finding** (2026-08-19) | Full CLAUDE.md §7 cadence, given decode-path/concurrency scope: 2 rounds of plan-review before any code — round 1 found a real blocker (the plan's proposed deletion of the accumulator-normalization line would have reintroduced round 8's original bug on the checkpoint-2-bail path; fix: relocate the line into `PerformReplay`'s own reset block instead of deleting it) plus 2 smaller amendments (an overclaimed "full tracker revert" framing; two `*SafetyCheckCountForTests` diagnostic counters that must stay excluded from any snapshot/revert). Round 2 confirmed the amended plan closes the gap with only 2 wording-only pins, explicit go. Implementation: `PerformReplay()` now returns `bool` (`false` only from its 3 pre-mutation early exits; `true` from checkpoint 2's bail — pre-existing "resets already ran, not rolled back," unchanged — and normal completion); `TryCorrectSlant()` no longer touches the accumulator/envelope/Auto-Sync-threshold fields at all, only `_effectiveSamplesPerLine` and the tracker's rate; new private helper `TryCorrectSlantAndApply()` wraps both calls as one transaction — snapshots everything `TryCorrectSlant` can mutate, calls it, calls `PerformReplay()`, and on a `false` result restores every snapshotted field verbatim plus the tracker's rate pair (not its baseline/history — that `Reset()` already fired on the forward call and is unrecoverable, a documented accepted gap vs. legacy, whose own revert arm has no `InitAutoStop` and so keeps its baseline). Manual-path caller now calls this one helper; automatic `_pendingReplayRequested` caller needed no change (confirmed by both plan-review rounds: `ProcessSlantTrackingSample` already self-normalizes independent of `PerformReplay`'s outcome). 2 rounds of code-review followed: round 1 EQUIVALENT/go with 2 comment-only nits (stale in-file line citations; an overclaimed "defensive superset" doc comment on the new helper); round 2 confirmed both nits closed, zero new findings, EQUIVALENT/go — no round 3 needed. Known accepted gap, explicitly out of scope for this fix per both plan-review rounds: no test exercises the revert path itself, since no injection seam exists to force `HasWriteFailed` latching in the exact window between `TryCorrectSlant`'s commit and `PerformReplay`'s checkpoint 1 (the existing `CorruptWriteStreamForTests` seam only forces it before `TryCorrectSlant`'s own entry gate). Verification: scoped `CorrectSlantTests`/`CorrectSlantRequestTests`/`AutoSyncTests`/`ReplayEngineTests`/`FieldLifecycleTests`/`SlantTests` 76/76 passing, solution build clean. Full-suite run pending. This closes the "gap in the predecessor round's own fix" chain that ran rounds 8→9→10 — round 11 is the first candidate for D0's second required clean round (round 5 was the first). |
|  | **D0 round 11 — NOT clean, 1 real blocker + 1 coupled risk in a genuinely NEW area** (2026-08-19) | Fresh full field-lifecycle re-derivation. Independently re-verified the rounds-8/9/10 structural-fix area from the actual current code (not the prior writeups) and found it fully correct: `TryCorrectSlant` mutates exactly `_effectiveSamplesPerLine` + the tracker's rate; all 3 `PerformReplay` `false` returns precede its first mutation; checkpoint 2's bail leaves a self-consistent state via the relocated modulo. Real finding, a NEW area entirely: `_syncRestartEnabled` (the user's Lock/Restart toggle, port of legacy's `m_SyncRestart`) gated the WHOLE `TryVisLockStateMachine` call at its mid-reception caller in `TryProcessBuffer` — but that call's own first step, `TryNarrowFskScan`, has no legacy equivalent of that gate: legacy's real `DecodeFSK` call (`sstv.cpp:1858`) runs unconditionally, 31 lines ABOVE the `!m_Sync||m_SyncRestart||m_SyncAVT` block (`sstv.cpp:1889`) `_syncRestartEnabled` actually corresponds to; legacy applies `m_SyncRestart` only to the narrow-mode COMMIT itself, deep inside `DecodeFSK` (`sstv.cpp:2592`). With Lock/Restart disabled — a real, shipped, user-toggleable setting (`SstvDecoderSettings.SyncRestartEnabled` → `RestartableSstvDecoder.cs`) — narrow FSK was starved of the entire sample stream for a locked image's whole duration: never delivering `StationIdDecoded`, never even scanning for a narrow-mode match, instead of just having its mode-commit correctly suppressed (the actual legacy behavior). Fixed by hoisting `TryNarrowFskScan(_consumedSamples)` out of the `_syncRestartEnabled &&` gate (now called unconditionally every decoded line), and moving the real suppression inside `TryNarrowFskScan` itself, onto its `Commit()` call only (`if (_syncRestartEnabled || _mode is null) { Commit(...); return true; } continue;`) — matching legacy's real gate exactly, `_mode is null` standing in for `!m_Sync`. `TryVisLockStateMachine` still calls `TryNarrowFskScan` internally too (unchanged) — confirmed harmless, since the hoisted call already advances the shared cursor to the same bound, making the internal call a no-op. Coupled risk found in the same sweep: `TrimBuffers`' locked-branch watermark included `_visLockProcessedUpTo` unconditionally, but that cursor (the ONLY thing `TryVisLockStateMachine`'s own VIS-lock half advances, still correctly gated behind `_syncRestartEnabled`) is genuinely frozen — not merely under-fed — for an image's whole duration whenever Lock/Restart is disabled, pinning the watermark and retaining tens to hundreds of MB across 5 buffers for a long locked reception — the exact same unbounded-retention failure class an earlier AVT-specific fix in this same method already closed, here reopened by a runtime setting instead of a mode. Fixed by only including that cursor in the watermark computation when `_syncRestartEnabled` is true (verified safe: `EndOfImage`/`Commit` always re-anchor it forward past `_bufferBase` at the next transmission boundary regardless, so excluding it here can never let trimming pass a value it will need to read again) — `_narrowFskProcessedUpTo`'s own already-unconditional watermark inclusion needed no change, since the blocker fix above means it's no longer frozen under this setting either. Also fixed a stale-comment nit: an earlier claim that `EndOfImage` is the ONLY place `_mode is null` becomes true again once a transmission has started was false (`AbandonInProgressImage` also nulls it directly, standalone, from the S7 mid-reception AVT hand-off) — corrected without over-claiming a full alternative mechanism, staying to only what was directly verified (`TryDecodeHeader`'s own `_avtTrainingPending` short-circuit means the affected method is simply never entered during that specific unsynced-cursor window, by a different mechanism than `EndOfImage`'s own direct resync). 1 nit left unfixed, arguably intended: `FirstLockedBandpassIndex`/`FirstNarrowDemodIndex` (diagnostic-only) never reset across images. **Known gap, deliberately not closed this round:** no regression test added for the blocker fix itself — while locked, `TryNarrowFskScan` reads narrow-FSK tones through the LOCKED mode's own bandpass filter, not the wideband search filter the existing pre-lock `StationIdDoesNotAbortScanTests` pattern relies on, and verifying that combination reliably demodulates a real station-ID burst needs more investigation than fit this round's scope — queued rather than risking a fragile/misleading test; the fix's correctness instead rests on the auditor's own detailed re-derivation plus the next round's independent re-verification. Verification: scoped 23-file filter (header/lock/narrow-FSK/station-ID/trim: `AnalogFmSstvEncoderStationIdWiringTests`/`AutoStopTests`/`AutoSyncTests`/`AvtNoiseTolerantDetectionTests`/`EndOfImageResetTests`/`FieldLifecycleTests`/`MidReceptionRestartTests`/`NarrowFskHeaderDecoderStationIdTests`/`PiecesSixCReachabilityTests`/`NarrowFskHeaderDecoderTests`/`NarrowFskNoiseTolerantDetectionTests`/`SenseLevelCalibrationTests`/`NarrowFskDuringAvtTrainingTests`/`SyncBypassDetectionTests`/`SearchBandpassFilterTests`/`StationIdDoesNotAbortScanTests`/`VisLockStateMachineTests`/`RestartableSstvDecoderTests`/`VisLockStateMachineDecoderTests`/`VisBitDecisionTests`/`SyncEnvelopeDetectorTests`/`SyncScanInterleaveTests`/`VisToneRaceHeaderTests`/`BufferTrimTests`) 225/225 passing, solution build clean. Full-suite run pending. Streak restarted at zero — round 12 is next. |
|  | **D0 round 12 — NOT clean, 0 blockers, real finding surfaced via test-construction (streak broken again)** (2026-08-19) | Fresh full re-derivation independently confirmed round 11's fix (the `TryNarrowFskScan` hoist and its inner commit gate) correct and complete, including the now-redundant internal `TryVisLockStateMachine` call to the same method (confirmed a genuine no-op via the shared bound-idempotent cursor). Pushed back on round 11's own deferred-test decision: round 11 had queued the regression test rather than write it, citing an unverified concern that narrow-FSK tones might not survive the locked-mode bandpass filter; round 12 checked the filter's own cutoff constants (`SearchBandpassFilter.cs`) and judged the concern didn't hold, recommending the test be written using the existing `StationIdDoesNotAbortScanTests` pattern. Building the test surfaced a genuinely separate, deeper finding: the same construction fails identically with `syncRestartEnabled: true` (the pre-round-11 code path, entirely unaffected by that fix) -- narrow-FSK/station-ID apparently never decodes while the decoder is genuinely locked mid-reception through the locked (`useLocked`) filter selection, REGARDLESS of the Lock/Restart setting, and this exact scenario (mid-image, still locked) has no existing test coverage anywhere in the suite -- every existing `StationIdDecoded` test is either pre-lock or a post-image footer burst that plays only after `EndOfImage` has already nulled `_mode` again. This is real but explicitly out of scope for a one-round follow-up (a DSP-filter/demodulation question, not a field-lifecycle one) -- the failing test was removed, the finding documented precisely in its place (a comment in `MidReceptionRestartTests.cs`), and flagged for a dedicated future investigation rather than chased inline. Also fixed 2 real nits from the same sweep: `_syncSegmentOffsetSamples` was the one `InitializeSlant` field left stale on an AVT lock (its 11 siblings all reset unconditionally before the AVT early-return; this one sat after it, provably unread for AVT either way) -- moved ahead to match the method's own established convention; `TryNarrowFskScan`'s commit gate got a 1-line comment noting legacy's one gate term with no port equivalent (`m_SyncMode >= 0`), confirmed unreachable rather than silently unremarked. Closed a 3rd, already-queued nit as not-a-defect: `FirstLockedBandpassIndex`/`FirstNarrowDemodIndex` are diagnostic-only "first ever" fields by documented design, correctly never reset across images. This is a genuine example of the review cadence earning its keep in an unexpected direction -- not by finding a bug in the fix itself (round 12 confirmed that was already correct), but by pushing back on a deferred-test judgment call and, in the course of actually writing that test, surfacing a real, previously-invisible gap neither round 11 nor round 12's own static analysis had caught. Verification: scoped `SlantTests`/`CorrectSlantTests`/`CorrectSlantRequestTests`/`AvtNoiseTolerantDetectionTests`/`NarrowFskDuringAvtTrainingTests`/`SyncBypassDetectionTests`/`StationIdDoesNotAbortScanTests`/`MidReceptionRestartTests` 68/68 passing, full-suite run pending, solution build clean. Streak restarted at zero — round 13 is next. |
|  | **D0 round 13 — CLEAN, first of the required 2 consecutive clean rounds** (2026-08-19) | Fresh full field-lifecycle re-derivation (79 mutable fields + 3 mutable auto-properties + ~28 stateful sub-objects), explicit yes, zero blockers, zero risks -- not a diff against prior rounds' claims. Independently re-verified every area rounds 8-12 touched as part of the normal sweep, not a special-cased re-check: the slant/replay commit-or-revert transaction (`TryCorrectSlant`'s mutation set confirmed exactly `_effectiveSamplesPerLine` + the tracker's rate + the two excluded counters; all 3 `PerformReplay` `false` returns confirmed to precede its first mutation), the narrow-FSK hoist + trim-watermark gating (confirmed `_visLockProcessedUpTo`'s conditional exclusion and `_narrowFskProcessedUpTo`'s unconditional inclusion are both individually correct given their respective advancers' own gating), and `_syncSegmentOffsetSamples`'s corrected reset ordering. Also independently re-derived several subtler existing-design points and confirmed each legacy-correct by reading `sstv.cpp`/`Main.cpp` directly rather than trusting in-file comments: `_autoSyncCooldown`'s deliberate absence from `ResetAutoSyncDetectionState` (legacy's own `InitAutoStop` genuinely never touches `m_AutoSyncDis`), an automatic slant commit under `RxBufferMode.Off` not recomputing Auto-Sync thresholds (legacy-correct, `UpdateSampFreq`'s own `InitAutoStop()` call is gated on a real staging buffer existing), and `EndOfImage`'s 4-cursor equalization being load-bearing (not cosmetic) for both a pre-lock scan-bound edge case and `TryInterleavedHeaderScan`'s own runtime invariant check. 3 nits found, none required before round 14 (auditor's own words): a diagnostic getter (`SyncOffsetSamples`) missing a guard its two siblings carry, correct today only via an undocumented coupling, no pixel impact; `PerformForceMode` re-arming the anchor-correction flag could suspend `TrimBuffers` indefinitely under a pathological rapid-calling caller, not reachable from a real button click; no test pins `EndOfImage`'s ~25-field reset list field-by-field (concrete gap example given, not just an assertion). Round 14 is the next dispatch -- the second required consecutive clean round to actually close D0. |
|  | **D0 round 14 — CLEAN, second consecutive clean round. D0 CLOSED.** (2026-08-19) | Fresh full independent field-lifecycle re-derivation (~80 mutable fields, ~29 stateful sub-objects), explicit yes, zero blockers, zero risks -- genuinely re-derived from source, not a confirmation pass over round 13. Re-swept every paired-field site this chunk's history flagged as a recurring hazard class (the 5 slant/envelope-field sibling re-anchor sites; sync-bypass/VIS-lock cursor-origin pairs; RX-buffer anchor/base-transmission-line pair) and found every one still correctly paired. Re-verified `TryCorrectSlantAndApply`'s snapshot completeness by reading `TryCorrectSlant`'s whole body directly, not the doc comment. Confirmed `FieldLifecycleTests.cs`'s 2 tests are genuinely mutation-sensitive (static argument: both assert a diverged mid-decode value AND the post-teardown cleared value, so neither passes vacuously). 3 tiny nits found and queued, none required: a redundant-but-inert double-assignment in `TryDecodeNarrowModeHeader`; the revert transaction reconstructs the pre-commit sample rate arithmetically (≤1 ULP drift) instead of snapshotting the tracker's own value, only on the already-untested `RxBufferMode.Extended` write-failure revert path; a test-only diagnostic field never reset across images. **D0 is now CLOSED -- 14 rounds + 1 structural fix, the second of the 2 required consecutive clean rounds.** This also closes the entire D1-D9+D0 `AnalogFmSstvDecoder.cs` functional-audit-sweep track (all 10 chunks now closed). One real, deliberately-unresolved finding remains outside D0's own scope: narrow-FSK/station-ID apparently never decodes while genuinely locked mid-reception through the locked bandpass filter, regardless of `syncRestartEnabled` (documented in `MidReceptionRestartTests.cs`, "D0-audit round-11/12") -- a DSP-filter question, not a field-lifecycle one, left for a dedicated future investigation. |
| **1** | Native/managed boundary (highest blast radius — memory corruption, not just wrong pixels) | `MiniAudioEngine.cs`, `MiniAudioCaptureSession.cs`, `MiniAudioPlaybackSession.cs`, `MiniAudioRing.cs` (`unsafe`/`fixed`/SPSC), `NativeAudio.cs` (P/Invoke vs `native/yoniq_audio.c`) |
| **2** | RX buffer/replay arithmetic (this project's own recurring bug class) | `RxDiskLineStagingBuffer.cs`, `RxLineStagingBuffer.cs` (review together — one shared interface), `ReplayOriginCalculator.cs`, `SlantTracker.cs`, `RestartableSstvDecoder.cs`. Run AFTER decoder chunks D8/D9. |
| **3** | PTT/transmit sequencing (real-world harm — a leaked keyed transmitter, not just bad output) | `SstvSessionService.cs`, `RadioController.cs`, `TcpTransport.cs`, `RigctldClientProtocol.cs`, `HamlibRadioProtocol.cs`. Point the auditor at `TryUnkeyPttAsync`/`TryCleanupAsync` exception paths specifically. |
| **4** | Pixel math: TX/RX scanline codecs (the big triage gap above) | `PixelSampleReader.cs`, `YCbCrSequentialScanlineDecoder/Encoder.cs`, `YCbCrLinePairedScanlineDecoder/Encoder.cs`, `RgbSequentialScanlineDecoder/Encoder.cs`, `MonoAveragedPairedScanlineDecoder/Encoder.cs`, `RobotScanlineDecoder/Encoder.cs`, `ScanlineCodecFactory.cs`, `YCbCr.cs`. Review encoder+decoder pairs together, 3-4 files/round. |
| **5** | Mode tables & constants (different rubric — data verification, not control-flow) | `SstvModeRegistry.cs` split: lines 1-655 = data/table audit, 656-1045 = real per-mode math (higher risk half). `VisHeader.cs` downgraded (392 lines, 1 method, rest constants — table-verification pass). `VisBitDecision.cs`, `SyncAnchorCorrector.cs`. |
| **6** | Header/lock state machines | `NarrowFskHeaderDecoder.cs`, `VisLockStateMachine.cs`, `AvtTrainingLockStateMachine.cs`, `SyncIntervalTracker.cs`, `SyncEnvelopeDetector.cs` (feeds all four). |
| **7** | DSP primitives (numeric fidelity, float-vs-double) | `HilbertFmDemodulator.cs`, `PllFmDemodulator.cs`, `ZeroCrossingFrequencyCounter.cs` (all 3 demod types — don't audit one and skip its siblings, they're user-selectable via `DemodType`), `SearchBandpassFilter.cs`, `TxOutputBandpassFilter.cs`, `AfcTracker.cs`, `LevelAgc.cs`, `IirFilter.cs`, `MovingAverage.cs`, `TankFilter.cs`, `Vco.cs`, `RadixTwoFft.cs`. |
| **8** | Binary/encoding boundaries | `WavFile.cs` (RIFF chunk walking), `FskStationIdEncoder.cs` + `FskStationIdWireFormat.cs`, `StationIdCallsignNormalizer.cs`, `AnalogFmSstvEncoder.cs`, `CwMorseGenerator.cs`. |
| **9** | Cross-thread publishing & native loading | `WaterfallSource.cs` (needs an explicit scheduler/slow-subscriber verdict per the concurrency rule), `MiniAudioDeviceEnumerator.cs` (Batch 1 round-4 flagged `ReleaseIfCompletedInTime` off-scope: same "timed-out close deliberately leaks the context reference, no log emitted" gap Batch 1 just fixed for the two session types — check whether it needs the same fix), `MiniAudioContext.cs`, `MiniAudioResampler.cs`, all 5 `Core.Radio.Hamlib` native-loading files (`HamlibNative.cs`, `HamlibRuntime.cs`, `HamlibLibraryLocator.cs`, `HamlibVersionGate.cs`, `NativeLibraryLoader.cs`). |
| **10** | Orchestration/utility — **consider demoting to Tier B rigor** | `TemplateStore.cs`, `MacroTextResolver.cs`, `MaidenheadLocator.cs` (pure function, one round is plenty), `OptionsSettingsService.cs`, `LogbookSessionService.cs`, `RadioSessionService.cs`. |

Also flagged: `FakeRadioTransport.cs`/`FakeAudioEngine.cs` live oddly in
`src/` (production tree, not `tests/`) despite being test doubles — the
playbook's own vacuous-fake rubric applies to them directly, batch with
whichever production file's rounds touch them.

**Status**: **Batch 1 done** (2026-08-18), 4 rounds; **RE-AUDITED 2026-08-19/20, 8 more rounds, 8
more real findings fixed, DONE by explicit user decision (not the formal 2-clean-round gate) -- see
"Batch 1 re-audit -- DONE" below this table's own history for the full account.** Round 1 found 4 real
findings (2 blocking: `MiniAudioRing`/`MiniAudioPlaybackSession.Write`/`Read`
silently returning -1 for a zero-length span instead of 0, breaking their own
documented contract; `MiniAudioEngine.OnCaptureSamplesAvailable`'s single bare
`?.Invoke` letting one throwing `SamplesCaptured` subscriber starve every
subscriber registered after it — 2 should-fix: undocumented SPSC
single-producer/consumer contract, `MiniAudioCaptureSession.Dispose`'s
write-lock-held-across-unbounded-`Join`). Round 2 caught a real regression
*introduced by round 1's own fix* (isolating subscriber exceptions in
`OnCaptureSamplesAvailable` meant they never reached the session's own
`LastSubscriberException`/`SubscriberExceptionCount`, silently killing those
two documented diagnostics) plus a sub-microsecond publish-before-subscribe
ordering bug — this is the exact "own bug class recurring" pattern CLAUDE.md
§4 warns about, caught specifically because the process re-scans the whole
file set every round instead of stopping at the first fix. Round 3 verified
both closed, explicit "yes" gate answer, one new [risk] found (dispose funnel
never logged `TimedOutDuringClose`, a real hot-unplug case). Fixed rather than
queued since it was one-line; round 4 re-confirmed it, explicit "yes" gate
answer, zero blockers/zero risks, auditor recommended closing rather than
spending a 5th round. 10 new tests added across the batch (3 zero-length-span
regressions, 1 multi-subscriber-isolation regression, 10 `NativeAudio`
`EncodeFixedString`/`DecodeFixedString` boundary tests — `MiniAudioRingTests`,
`MiniAudioPlaybackSessionTests`, `MiniAudioEngineTests`, new
`NativeAudioTests.cs`). Full suite: 71/71 passing on real PipeWire hardware,
none skipped. Queued nits (not blocking, left for a future pass if this file
set is revisited): test-name overreach on the multi-subscriber test, missing
`EncodeFixedString`/`DecodeFixedString` null/invalid-UTF-8 edge cases,
`yoniq_audio_context_init`'s missing `[Out]` (cosmetic, confirmed harmless on
CoreCLR twice), a diagnostic double-read race in the two new
`TimedOutDuringClose` log guards, zero test coverage for the two new log
messages themselves, an `internal` type name leaking into one
`ObjectDisposedException` message. Update this table's status inline as
batches complete rather than maintaining a separate tracking doc.

**Batch 1 RE-AUDIT (2026-08-19), process note first:** the orchestrating session offered "start
Batch 1" to the user as if it were an unstarted option, without checking this status line first --
a real process miss (this section already said "done" at the time). The user picked it anyway;
what followed found a genuine new bug, so it's recorded here as an unplanned re-audit, not
conflated with the original 4-round closure above. Re-audit round 1: NOT clean, 3 real risks.
**Real deadlock**, the most significant finding: `MiniAudioEngine.DisposeAsync()`'s second-caller
branch had no reentrancy awareness at all, unlike `StopCaptureAsync`'s own already-fixed self-join
guard (from the ORIGINAL Batch 1 closure above) -- a `SamplesCaptured` subscriber reentrantly
calling `DisposeAsync()` synchronously on the capture drain thread (a pattern the class's own doc
comment explicitly advertises as supported) could lose the `_disposed` `Interlocked.Exchange` race
to a concurrent external caller and block forever on `_disposedSignal`, while that caller's own
`Task.Run(session.Dispose)` blocked forever joining the very drain thread now stuck on the signal.
Given CLAUDE.md's concurrency-review cadence: 2 rounds of plan-review (round 1 found the original
proposed mechanism's own "same critical section" rationale wrong -- the reader never takes
`_captureLock`, so WRITE ORDER combined with `volatile`'s release/acquire ordering is what actually
matters, not lock-based mutual exclusion; round 2 confirmed the corrected write-order mechanism is
sound), then implementation (`_captureSessionBeingDisposed` field, write-before-null ordering in
`ClaimCaptureSessionLocked`, exact-reverse read order in `DisposeAsync`'s own second-caller branch),
then 2 rounds of code review (round 1: go, 3 comment-accuracy nits, fixed; round 2: confirmed, zero
new findings). 2 new regression tests, mutation-verified by the implementing session (temporarily
disabled the fix -- the forced-interleaving test correctly failed with a 15s timeout, the
opportunistic-probe test correctly passed regardless, exactly as its own honest doc comment
predicts it should). Also fixed: a native-side integer-overflow guard in
`yoniq_audio_ring_create` (`ma_pcm_rb_init`'s own `ma_uint32` multiplication can wrap before its
internal overflow guard ever sees it -- not reachable today, closed before a future settings-driven
buffer-size knob could make it live; mechanical fix, no separate review round needed). Documented,
not fixed: every `[RequiresPipeWireFact]`-gated test -- including ALL of this cluster's hardest
concurrency guarantees -- silently skips on any CI runner other than a Linux one with a live
PipeWire/PulseAudio server, and this project's own CI matrix doesn't document running one; recorded
as an accepted, durable position in `RequiresPipeWireFactAttribute.cs`'s own doc comment per the
audit's own gate (a stated decision, not silence). Verification: scoped `MiniAudioRingTests` 15/15,
full `ScanlineStudio.Core.Audio.MiniAudio.Tests` 73/73 passing (PipeWire IS available in this
sandbox -- the reentrant-dispose tests genuinely ran, not skipped), full solution build clean.
Commits `dbf4b43` (deadlock fix + overflow guard), `e0594dd` (CI-coverage-risk documentation).
Re-audit round 2 (2026-08-20): NOT clean, 2 real risks (no blockers). `MiniAudioCaptureSession`'s
constructor `try`/`catch` ended at the native device open, but two statements after it (thread
creation, priority set, `Start()`) could still throw with the device already live -- orphaning a
running device plus a permanent `MiniAudioContext` reference, since the object never escapes the
constructor on that path. Fixed with `Enum.IsDefined` pre-validation of `drainThreadPriority`
before the native open at all (root cause: `AudioDeviceSettings.CaptureThreadPriority` round-trips
through JSON with no `JsonStringEnumConverter`, so STJ's default numeric enum handling doesn't
range-validate a hand-edited settings file's out-of-range value) plus widening the catch to mirror
`Dispose()`'s own bounded-close-thread pattern (`CloseTimeout`) for any other failure mode
(`OutOfMemoryException` from `Start()`). Also: round 1's own native overflow guard
(`yoniq_audio_ring_create`) had zero test coverage -- added a `MiniAudioRingTests` case using a
capacity whose byte product genuinely wraps mod 2^32 (`0x40000100` frames, not `int.MaxValue`,
which would pass with or without the guard and be vacuous). Both fixes mutation-verified (native
guard: temporarily disabled, test correctly failed with no exception, restored; constructor fix:
covered by a new hardware-free `[Fact]` since the validation runs before any device is touched).
Single code-review pass (mechanical, mirrors `Dispose()`'s own already-reviewed pattern) per
CLAUDE.md §7's carve-out: go, 2 more small risks + 2 nits found -- 3 addressed in the same commit
(`_stopping = true` as a future-code-hazard guard in the new catch, a timed-out-close log message
for a previously-zero-observability path, a distinct `MiniAudioEngine.OpenCaptureSession` catch for
`ArgumentOutOfRangeException` so it surfaces as "invalid settings" instead of a misleading "device
unavailable" message), 1 left as an accepted documented tradeoff (a catch-in-catch OOM exposure
matching `Dispose()`'s own identical existing exposure -- no better pattern available). Full
`ScanlineStudio.Core.Audio.MiniAudio.Tests` 75/75 passing, full solution build clean. Commit
`2af5e59`.

Re-audit round 3 (2026-08-20): NOT clean, 1 real risk (a sibling gap to round 2's own fix), 2 nits.
`channelSource` is the OTHER settings-fed enum in `MiniAudioCaptureSession`'s constructor, and had
the exact same unvalidated-JSON-round-trip root cause round 2 fixed for `drainThreadPriority` --
but unlike that case, this one fails SILENTLY rather than loudly: an out-of-range value opens the
device stereo and extracts Left (the native shim's own `ChannelSelect` default, `channel_select ==
2 ? 1 : 0`), so RX would go silently dead if the real signal is on Right, with no exception and no
log. Fixed with the identical `Enum.IsDefined` guard, same placement, before any native resource is
touched -- confirmed `AudioDeviceSettings` now has zero unguarded enums (its only other fields are
`bool`/numeric). Also fixed a nit: `DisposeAsync`'s `TrySetException` created a faulted `Task` that
goes permanently unobserved in the common single-caller case (the real exception still correctly
reaches that caller via `DisposeAsync`'s own `throw` -- this was host-level noise only,
`TaskScheduler.UnobservedTaskException` on finalization, not a correctness bug) -- fixed by
attaching a no-op `OnlyOnFaulted` continuation in the constructor. Both mutation-verified (the
`channelSource` guard: temporarily disabled, the new test correctly failed with
`InvalidOperationException` instead of `ArgumentOutOfRangeException`, restored). Single code-review
pass (mechanical, mirrors round 2's own already-reviewed pattern) per CLAUDE.md §7's carve-out: go,
3 nits -- one folded in (a comment clarifying why `ExecuteSynchronously` is currently inert, for
when `_disposedSignal`'s own `RunContinuationsAsynchronously` construction might change), two left
matching an already-accepted pattern shape (the new test asserts exception type only, not
`ParamName`, consistent with its round-2 sibling; a UI-coerces-to-Mono/engine-throws asymmetry, the
same shape as the already-accepted `drainThreadPriority` case). Full
`ScanlineStudio.Core.Audio.MiniAudio.Tests` 76/76 passing, full solution build clean. Commit
`d706ed7`.

Re-audit round 4 (2026-08-20, the soft cap): NOT clean, but production code itself came back
genuinely clean this round. Fresh full independent re-derivation found nothing new in any
production file -- all three prior fixes (DisposeAsync deadlock, constructor leak, channelSource
validation) re-verified correct from scratch, and the auditor explicitly noted it gave the cluster
"a genuinely fair shot at being clean" rather than manufacturing a finding. The one real finding was
narrower than the previous three: `MiniAudioEngine.OpenCaptureSession`'s `ArgumentOutOfRangeException`
catch (added in round 2's own code review) had zero test coverage anywhere in the repo -- its two
sibling guards from that same fix pass each got a dedicated test, this one didn't, and deleting it
would be silent (it derives from `ArgumentException`, so removal falls through to the generic
message with nothing failing). Auditor's own honest framing: "a reasonable reviewer could grade
this a nit -- it changes a message, not an output." Per the playbook, stopped here and checked in
with the user rather than auto-dispatching a round 5 -- user chose to fix it and continue past the
cap. Fixed: a hardware-free engine-level regression test mirroring the pattern of its two session-
level siblings, mutation-verified (temporarily removed the catch branch, confirmed the test failed
with the generic "device unavailable" message instead of "invalid settings", restored). Also fixed
a related nit found in the same round: that branch's own log call still went through
`Log.CaptureOpenFailed` ("Failed to open capture device"), directly contradicting its own corrected
exception message ("Invalid capture settings") -- added a distinct `CaptureSettingsInvalid` log
message. Full `ScanlineStudio.Core.Audio.MiniAudio.Tests` 77/77 passing, full solution build clean.
Commit `8ff1cff`.

Re-audit round 5 (2026-08-20, past the soft cap with the user's explicit go-ahead): NOT clean, 2
real risks (comment/test only, no production logic bugs) + several nits. (1)
`MiniAudioCaptureSession.Dispose()`'s own comment falsely claimed a write-lock-held-across-Join
hazard was "NOT reachable today" -- the reasoning assumed `MiniAudioEngine.CaptureOverrunCount`'s
`?.` operator was atomic with a concurrent session claim; it isn't (`?.` reads `_captureSession`
into a temp, then calls on the temp, with no re-check before the call). Real chain, confirmed
end-to-end: Avalonia's UI thread (`RxImagePaneViewModel`'s 250ms `DispatcherTimer`) polls
`CaptureOverrunCount` through `ISstvSessionService`, can read a session an instant before
`ClaimCaptureSessionLocked` claims it for disposal, then block on that session's own `Dispose()`
write lock for as long as Dispose holds it -- a real UI stall (bounded by `CloseTimeout` ~5s,
typically near-instant, unbounded if a `SamplesAvailable` subscriber hangs elsewhere), not merely
the caught-and-recovered `ObjectDisposedException` the existing doc comments described. Not a
deadlock (Dispose never waits on the poller) and not corruption -- documentation-only fix across
all 4 affected doc comments (`MiniAudioCaptureSession.Dispose`, `MiniAudioEngine`/`IAudioEngine`/
`ISstvSessionService`'s own `CaptureOverrunCount`), naming the real victim thread and the real
bound. Deliberately NOT attempting a timeout-bounded-Join redesign as a byproduct of a comment fix
-- that needs the same "abandoned thread may still touch the handle after we give up waiting"
analysis `CloseTimeout`'s own doc comment already works through for the native close specifically,
a real design task on its own. (2) `MiniAudioCaptureSession`'s own `_lifetimeLock`-based post-dispose
guard (protecting `HasStopped`/`OverrunCount`) had zero test coverage, unlike both siblings
(`MiniAudioRing` has a simple pair plus a stress test; `MiniAudioPlaybackSession` has the stress
test) -- added both, mirroring the exact sibling patterns. Mutation-verified in a notably dramatic
way: temporarily removing the guard CRASHED the test host process outright (a native P/Invoke call
against an already-freed handle), not just a failed assertion -- confirms the guard is genuinely
load-bearing for memory safety, not just a nice-to-have. Also added missing negative/zero coverage
for `yoniq_audio_ring_create`'s pre-existing `<= 0` guard (a round-5 nit; the guard itself is
unchanged, pre-existing code). Code-review pass: go, folded in 2 more risks + several nits before
merging (the correction hadn't propagated to `ISstvSessionService`'s own doc comment, which still
asserted the class-level "safe to read from any thread" contract was fully upheld; UI-thread
identity was missing from both corrected comments; "block rather than throw" corrected to
"block-then-throw" -- additive, not exclusive, since `_disposed` is set inside the same write lock;
a wrong-reasoning comment on the new stress test claiming its 10s bound "absorbs" the stall, when
`session.Dispose()` is synchronous on the same thread so any block resolves before the bound is
ever measured; ring test naming corrected to cover negative values, the more discriminating case,
not just zero). Full `ScanlineStudio.Core.Audio.MiniAudio.Tests` 83/83 passing, full solution build
clean. Commit `d542715`.

Re-audit round 6 (2026-08-20): NOT clean, 2 real risks + 3 nits. (1) `string_to_device_id` (the
reverse of `device_id_to_string`, used by every `yoniq_audio_get_native_formats`/
`capture_session_open`/`playback_session_open` call) was missing a case for `ma_backend_jack` --
the exact same bug shape as an already-fixed `coreaudio` gap in the same function, whose own
comment already warns "enumeration alone would have looked fine, masking this": `ma_backend_jack`
IS one of the three backends this shim's own `backends[]` array requests, and
`device_id_to_string` already handles jack correctly going the OTHER direction (`snprintf(buf,
buf_size, "%d", id->jack)`) -- but on a host where PulseAudio and ALSA context-init both fail while
JACK succeeds, enumeration would work fine (masking the bug) while every single session-open/
format-probe call failed unconditionally with a generic "device unavailable" error for a device
that's actually fine. Fixed by adding `case ma_backend_jack: out_id->jack = atoi(device_id); return
0;`, parsing the id back the same way it was rendered. Verified against the pinned `miniaudio.h`
(not assumed): `ma_device_id.jack` is a plain `int`; `ma_context_enumerate_devices__jack` always
hands back a zeroed id (`jack == 0`); both native consumers (`ma_context_get_device_info__jack`,
`ma_device_init__jack`) accept only `jack == 0` -- so the round-trip lands on the sole accepted
value in every reachable case. (2) A comment gave a WRONG happens-before rationale for
`TimedOutDuringClose`: claimed `_drainThread.Join()` supplies its memory-ordering guarantee. It
doesn't -- `Join()` orders the disposing thread against the DRAIN thread specifically, unrelated to
`TimedOutDuringClose`'s own write (which happens on whichever thread executes `Dispose()`'s body,
sometimes the drain thread itself via the self-dispose path, where `Join()` is deliberately
SKIPPED). Code correctness was never in question, only the comment's own reasoning -- corrected to
state the real mechanism: written once under the write lock, with every production read ordered
either by same-thread execution (self-dispose path) or by `await Task.Run(session.Dispose)`'s own
task-completion semantics (the pool-thread path), not by `Join()`. Checked in with the user again
at this point given the length of this re-audit chain (6 consecutive rounds finding something real)
-- user chose to continue rather than stop. Also fixed 3 nits from the same round: extended round
5's block-vs-throw documentation to the analogous playback-side gap (`EnqueuePlaybackSamples`,
lower severity -- no UI poller on that side) and to `SstvSessionService`'s own `CaptureOverrunCount`
implementation comment, which hadn't picked up round 5's correction; dropped a stale in-file line
citation for a symbolic reference. Code-review pass: go, a few more precision nits folded in (the
corrected happens-before comment over-corrected its own read-site count in the opposite direction
from the original error; a real native-lifetime-decision read mischaracterized as "diagnostics"; an
ambiguous cross-reference between `CaptureOverrunCount`/`PlaybackUnderrunCount`). Full
`ScanlineStudio.Core.Audio.MiniAudio.Tests` 83/83 passing, full solution build clean. Commit
`32ddec8`.

Re-audit round 7 (2026-08-20): CLEAN -- the first of the 2 required consecutive clean rounds. Fresh
full independent re-derivation, zero blockers, zero risks. Re-verified every round 1-6 fix from
scratch, not taken on trust -- including round 6's JACK backend fix, confirmed correct directly
against the pinned `miniaudio.h` (`ma_device_id.jack` is a plain, unconditional `int`; both native
consumers `ma_context_get_device_info__jack`/`ma_device_init__jack` accept only `jack == 0`;
`ma_context_enumerate_devices__jack` always yields a zeroed id; so the `"%d"`/`atoi` round-trip
lands on the sole accepted value in every reachable case) and its corrected happens-before comment.
7 nits found, all comment-precision or accepted-coverage-gap, none behavioral -- queued, not fixed,
matching this project's own established precedent for clean rounds. Worth naming one specifically:
round 6's own corrected happens-before comment still slightly over-quantified its own claim (says
every production read happens "after Dispose() has returned to its caller", which is false for one
of the two reads it names -- that read happens inside Dispose, before it returns, but is
behaviorally inert since it's same-thread and sequential under the same write lock) -- a smaller
instance of the same "comment drift" class this whole re-audit chain keeps finding, though this
particular instance doesn't mislead anyone into a wrong conclusion. Test-integrity check passed
across all 8 paired files, including hand-re-checking mutation-sensitivity for the round 2/3/4
guards.

Re-audit round 8 (2026-08-20, the second attempted clean confirmation): NOT clean, 1 real risk
(documentation-only, no functional bug), resets the streak. `yoniq_audio.h`'s own doc comment for
`yoniq_audio_playback_session_underrun_count` said it returns a count of padded FRAMES -- wrong; the
implementation (`yoniq_audio.c`'s own field comment, and every C# doc) counts padded CALLBACKS, once
per callback regardless of how many frames it padded. A native-side consumer computing "seconds of
audio dropped" from this count would be wrong by roughly the period size. The capture-side header's
own "mirrors ... exactly" cross-reference compounded it -- self-contradictory against its own
correct preceding sentence about the same pair. Fixed: corrected the header comment to match the
implementation; added a discriminating assertion to the existing 2-second continuous-underrun test
(a per-frame counter would read ~88,200 over that window; a per-callback counter reads nowhere close
-- 20,000 as a generous, order-of-magnitude-separated bound). Mutation-verified: temporarily changed
the native increment to per-frame, confirmed the new assertion failed at 89,208 -- matching the
frame-count prediction almost exactly -- then restored. Full
`ScanlineStudio.Core.Audio.MiniAudio.Tests` 83/83 passing, full solution build clean. Commit
`647df48`.

**Batch 1 re-audit -- DONE (2026-08-20), by explicit user decision, not the formal 2-clean-round
gate.** 8 rounds, 8 real findings fixed (deadlock, native overflow guard, 2 settings-validation
gaps, false-reachability comment + missing tests, missing JACK backend case, wrong happens-before
comment, wrong ABI-header comment) plus one already-closed CI-coverage-risk documented as an
accepted position. Every fix mutation-verified where testable. The user was checked in with 3
times over the course of this re-audit (rounds 4, 6, 8) and chose to continue each time except the
last, where round 8's finding was judged narrow enough -- and the chain long enough -- to fix and
stop rather than chase the formal gate. Round 7 was clean; round 8 broke that streak; no further
round was dispatched after round 8's fix landed. This is a deliberate, explicitly recorded exception
to the playbook's own 2-consecutive-clean-round closing rule, not an oversight -- if this file
cluster is revisited later, start a fresh re-audit rather than assuming the formal gate was met.

**Status**: **Batch 2 CLOSED** (2026-08-20) -- chunk 2a CLOSED (3 rounds), chunk 2b CLOSED
(5 rounds), chunk 2c CLOSED (3 rounds). See "Tier A Batch 2 -- CLOSED" below this table's own
history for the full account. Chunk 2a
(`RxLineStagingBuffer.cs` + `RxDiskLineStagingBuffer.cs`,
reviewed together per the playbook's own "one shared interface" note), round 1 fixed
(2026-08-20). Round 1 found 1 real blocker plus several risks, all against
`yoniq-old/YONIQ-main/sstv.cpp:1615-1644`, `Main.cpp:4956-5013`/`:5234-5270`/`:5415-5423`/
`:5491-5537`, confirmed directly (not from in-file comments): legacy's `m_WD` is assigned exactly
once (`sstv.cpp:594`, `SetMode`) and never touched by `CorrectSlant`'s own mid-reception
`SetSampFreq()` calls, so legacy's per-line admission test
(`((m_wStgLine+1)*m_WD) < m_RxBufAllocSize`, `Main.cpp:4999`/`:5242`) is monotonic -- once it fails
it fails for every later line until a reset (`CopyStgBuf`'s own `else { break; }`,
`Main.cpp:5247-5249`), so legacy's staged stream is structurally gap-free. This port's own
`RxLineStagingBuffer.TryAppendLine` re-evaluated the check per call, so a wide line's rejection
could be followed by a narrower line's silent admission past the same slack -- the exact
local-vs-absolute/off-by-one bug class this project has hit 3 times before (D3+D8+D9). **Fixed**:
added a `_capacityReached` latch (set on first rejection, cleared by `Clear()` matching legacy's
`m_wStgLine = 0`), applied the same latch to `HasHeadroomForSamples` (legacy's `CorrectSlant` entry
gate at `Main.cpp:5268-5270` uses the identical expression, so "appends stopped" and "slant
correction refused" must be one fact, not two), corrected the class doc comment's admission-test
paragraph, and added a latch clause to `IRxLineStagingBuffer.TryAppendLine`'s shared contract doc.
Also replaced the one existing test that asserted the wrong (pre-fix) contract by name
(`TryAppendLine_AfterRejection_..._FurtherFittingAppendsStillWork` -> two new tests asserting the
latch and its `Clear()` reset) and added one more for the `HasHeadroomForSamples` latch. Scoped
filter (`RxLineStagingBufferTests`/`CorrectSlantTests`/`ReplayEngineTests`) 51/51 passing, full
`ScanlineStudio.Core.Sstv` build clean. Queued risks/nits for a later round (not yet fixed): RAM
and disk implementations still disagree on post-`Dispose` behavior (interface silent on which is
normative); disk mode latches `HasWriteFailed` permanently on a transient write stall with no
diagnostic surface; `ConsumeAsync`'s outer `finally` drain doesn't bump the flush counters (latent,
only reachable post-`Dispose` today); a `Clear()` partial-failure desync between the two disk
files; `InvalidateSnapshot` bypasses the `_returnSnapshot` test seam `Dispose` uses;
`DrainToCurrentPoint` ignores the test-shortened dispose-drain timeout on the `Clear()` path; `int`
sample-count overflow past ~268M staged samples (unreachable today). Round 2 needed before this
chunk can close (CLAUDE.md §7's 2-consecutive-clean-round bar, non-negotiable for buffer/
concurrency work).

**Round 2 — chunk 2a's 1st clean round** (2026-08-20). Independently re-derived the monotonicity
claim from legacy source itself, not from round 1's citations: confirmed via a full-tree `grep -a`
that `m_WD` has exactly one assignment site in the whole codebase (`sstv.cpp:594`, `SetMode`), and
additionally checked something round 1 didn't -- whether a mid-run `SetMode` call could break
monotonicity -- and found every mid-run call site is immediately followed by `Start()` (which
resets `m_wStgLine` via `Main.cpp:4958`), except one (`sstv.cpp:2148`) that reaches its own
`Start()` on the very next demod sample, far short of one staged line; monotonicity holds within
any window between resets. Hand-verified all 3 new/edited tests are genuinely mutation-sensitive
(each would pass without the latch, by concrete arithmetic on the exact numbers used). Also did a
fresh full 8-item sweep of both files and found 4 more nits (none blocking, none fixed this round,
read-only pass): `IRxLineStagingBuffer.cs:108-110`'s `HasHeadroomForSamples` RAM-contract doc still
describes the pre-fix plain-arithmetic contract, now the one place round 1's latch doc update
didn't reach; unbounded `SemaphoreSlim` permit accumulation in `RxDiskLineStagingBuffer` (not a
correctness bug -- the drain loop re-checks after every `Wait` -- but a pathological case could
burn the whole `DrainTimeout` and falsely latch `HasWriteFailed`); no negative-argument guard on
`HasHeadroomForSamples` (unreachable from both live call sites); a zero-length line advances
`LineCount` with 0 samples in both implementations (no legacy analogue, unreachable from Phase 5's
capture hook). All 7 of round 1's queued risks/nits re-confirmed still present, unchanged. Zero
blockers. Round 3 needed for the 2nd required clean round before this chunk can close.

**Round 3 -- chunk 2a's 2nd clean round. Chunk 2a CLOSED** (2026-08-20). Independently re-derived
rounds 1-2's whole legacy basis again from scratch (not from their summaries): re-confirmed
`m_WD`'s single assignment site (`sstv.cpp:594`) and `m_wStgLine`'s monotonic-or-reset write set,
and additionally traced every RX-path `SetMode` call site (`sstv.cpp:1902`/`:1917`/`:1940`/`:1758`/
`:2148`) to confirm each is followed by `Start()` (directly or, for `:2148`'s deferred case, on the
very next demod sample) -- closing the one gap round 2's own check left open. Fresh full 8-item
sweep of all 3 production files found 2 new nits, both unreachable from any live call site:
`HasHeadroomForSamples`'s `Count + additionalSamples` addition itself could overflow-wrap to a
false "true" for a very large argument (distinct from round 1's already-queued `_count`-overflow
item); the disk implementation's in-flight capacity pre-check is one line more conservative than
the channel bound (verified correct, not a bug). Independently re-bounded round 2's semaphore-
permit nit and found it overstated: the drain loop is counter-authoritative
(`Interlocked.Read(ref _flushed)`, re-checked after every `Wait`), so stale permits can never cause
a false drain success, and the real cost is a sub-millisecond fast-path spin, not the full 5s
`DrainTimeout` round 2 estimated -- downgraded from risk to accepted-non-issue. **Fixed** the 2
recommended-safe items: `IRxLineStagingBuffer.cs`'s `HasHeadroomForSamples` doc now states the
latch explicitly (was the one place round 1's doc update didn't reach); `RxLineStagingBuffer.
HasHeadroomForSamples` rewritten overflow-safe (`additionalSamples >= 0 && CapacitySamples - Count
> additionalSamples`, no addition, so no wrap) -- closes both round 2's negative-argument nit and
this round's new overflow nit in one provably-equivalent-on-every-reachable-input change. Left
queued, explicitly NOT fixed (auditor's own judgment call, endorsed): the semaphore-permit
non-issue (comment-only, not worth touching a concurrency path for a sub-ms spin); the
zero-length-line nit (unreachable, and any early-return would be invention -- legacy's `m_WD` is
never 0, so there's no port target). All 7 of round 1's queued risks/nits re-confirmed present,
still not fixed, still not newly urgent -- carried forward as accepted risk for chunk 2a's closure
(post-`Dispose` asymmetry between implementations, permanent `HasWriteFailed` latch on disk with no
diagnostic, `ConsumeAsync`'s finally-drain not bumping flush counters, `Clear()` partial-failure
desync, `InvalidateSnapshot` bypassing the `_returnSnapshot` test seam, `DrainToCurrentPoint`
ignoring the test-shortened dispose timeout, `int` sample-count overflow past ~268M samples).
Scoped filter (`RxLineStagingBufferTests`/`CorrectSlantTests`/`ReplayEngineTests`) 51/51 passing
after the round-3 fixes, full `ScanlineStudio.Core.Sstv` build clean. **Chunk 2a: 3 rounds, 1 real
blocker fixed (round 1) + 2 small round-3 hardening fixes, CLOSED under the formal
2-consecutive-clean-round gate** (rounds 2 and 3 both zero-blocker).

**Chunk 2b (`ReplayOriginCalculator.cs` + `SlantTracker.cs`) round 1 fixed** (2026-08-20). Both
files already carried dense audit history from earlier RX buffer subsystem phases (Phase 6a/6b/6c/
8c plan-review and code-review, an "ultracode audit" that found findings #7 and #9) -- round 1
treated that as settled context and did a genuinely fresh pass, re-verifying the still-true claims
against current code and legacy source rather than re-litigating them. `ReplayOriginCalculator`
(`AdjustSyncPos`/`ReSyncSSTV` ports) came back fully clean -- all 43 `SstvModeRegistry` mode
definitions checked branch-for-branch against legacy's switch, truncation order and the two-step
`ReSyncSSTV` fold/argmax bound both confirmed correct, and confirmed the port did NOT conflate
`ReSyncSSTV` with its near-twin `SyncSSTV` (a different bound, different Scottie wrap, no
mode-offset table -- a real risk for two similarly-shaped legacy functions). `SlantTracker` found 2
real risk findings (no blocker): (1) a numeric-fidelity gap -- legacy's position history/fit is
entirely `int`-typed (`Main.h:1351`'s `m_AutoStopAPos[16]`, `GetSqerrPos`'s own `int
__fastcall GetSqerrPos(int)` truncating its `double`-computed result on return), while this port's
`_history`/`GetSqerrPos` were `double`, feeding a genuinely fractional value in and returning an
untruncated one out -- a type-level divergence that can flip the `maxDelta < 8*_mult` jitter gate's
own boolean outcome, not just shift a magnitude, so a documented tolerance wasn't the right
instrument; (2) `AdoptCorrectedRate` unconditionally `Reset()`s, including on the manual
Correct-Slant REVERT path (`AnalogFmSstvDecoder.TryCorrectSlantAndApply`) -- legacy's own revert
arm (`Main.cpp:5420-5423`) is a bare `SetSampFreq()` with no `InitAutoStop` call, so Auto Slant's
baseline/history/average/bitmask genuinely survive a reverted manual correction in legacy, while
this port wiped them, forcing Auto Slant to restart from zero after every reverted manual attempt.
**Fixed both**: quantized `_history` to `int[]`, `GetSqerrPos` to `int`-truncating, and pushed the
truncation back to its correct legacy position (`AnalogFmSstvDecoder.cs`'s own `relative`
computation, BEFORE the subtraction -- `trunc(a)-trunc(b) != trunc(a-b)` whenever both have
fractional parts, verified against `Main.cpp:3887`-`:3889`'s own three separate truncation points);
split `AdoptCorrectedRate` into a new `RestoreRateWithoutReset` (rate pair only, no `Reset()`) used
by BOTH the manual forward-commit and revert call sites, while `PerformReplay`'s own existing
`ResetBaseline()` call supplies legacy's `InitAutoStop`-inside-`UpdateSampFreq` placement exactly
(verified: sits after every one of `PerformReplay`'s pre-mutation early-exit `return false`s and
before every `return true`, so the automatic/success-path reset still fires at the right point,
while a revert genuinely preserves tracker state now). Added 4 new tests (2 pinning the truncation
rule specifically -- toward-zero, not floor, not banker's-rounding, at a deliberately chosen .5
boundary; 2 proving `RestoreRateWithoutReset` preserves state where `AdoptCorrectedRate` still
resets it, a positive/negative control pair) plus 12 mechanical `double`->`int` call-site updates
in `SlantTests.cs`. Scoped filter (`SlantTests`/`CorrectSlantTests`/`CorrectSlantRequestTests`/
`ReplayEngineTests`/`AutoSyncTests`) 78/78 passing, full `ScanlineStudio.Core.Sstv` build clean.
Full `tests/ScanlineStudio.Core.Sstv.Tests` suite (run because this fix touches
`AnalogFmSstvDecoder.cs` directly, not just the 2 chunk files): 1020/1020 passing (1 unrelated
intentional skip). Round 2 needed for chunk 2b's own 2-consecutive-clean-round gate.

**Round 2 -- NOT clean, 1 new real risk found** (2026-08-20). Independently re-derived both
round-1 fixes from legacy source (`Main.h:1340-1360`, `Main.cpp:3867-3889,4198,5415-5423,5581-5600`)
without relying on round-1's citations -- both held, including hand-recomputing the two new
truncation tests' exact fit values (-1.4 truncating to -1, floor would give -2; 5.8 truncating to 5,
`Math.Round` would give 6) and independently verifying `PerformReplay`'s `ResetBaseline()` call
sits after all 3 of its early-exit `return false`s and before both `return true`s. Also did a fresh
full sweep of `ReplayOriginCalculator.cs` (found nothing wrong -- all 6 `AdjustSyncPos` mode groups
+ default re-verified arm-for-arm against legacy's switch, `ReSyncSSTV`'s fold/argmax/Hilbert term
all re-confirmed) and `MovingAverage` (re-verified against `CSmooz` exactly). Found 1 NEW real risk
(not a blocker): `AdoptCorrectedRate`'s `Reset()` on the AUTOMATIC commit path is unconditional, but
legacy's own `InitAutoStop()` call (`Main.cpp:5600`) is gated on a staging buffer existing
(`Main.cpp:5597`'s `if( (dp->m_StgBuf != NULL) || WaveStg.IsOpen() )`) -- under `RxBufferMode.Off`
legacy leaves Auto Slant's baseline/history/average/bitmask intact after every automatic commit
(`m_ASCurY` keeps growing, the latched bitmask keeps tightening), while this port resets
unconditionally, re-opening the coarse +-25Hz tier every time. `ultracode` finding #9 (which
introduced this `Reset()` call originally) cited `InitAutoStop`'s body without its own `:5597`
guard. 3 more nits queued (not fixed): the round-1 truncation fix has no test pinning the
truncate-BEFORE-subtract ordering specifically (both new tests feed already-integer history, so a
revert of the decoder-side ordering fix wouldn't be caught); a test name overstates what it pins
(doesn't actually test a .5 boundary); `ReplayOriginCalculator.ComputeOrigin`'s `double[]` histogram
bins vs legacy's `int[]` (harmless in practice, argmax is scale-invariant, but a near-tie could
theoretically resolve differently). Round 3 needed after the buffer-existence-gate fix lands --
clean-round counter restarts from there, not from round 2.

**Round 2 fix applied** (2026-08-20). Confirmed legacy's real shape independently: the automatic
commit's rate write (`Main.cpp:4015-4016`) is genuinely unconditional (nothing resets there), and
the guard (`Main.cpp:5597`) sits one layer downstream, in `UpdateSampFreq`, wrapping ONLY
`InitAutoStop` (`:5600`) -- also confirmed the buffer-off path is reachable in real legacy too, not
a port-only artifact (`Main.cpp:11903`'s `KRSA->Enabled = sys.m_UseRxBuff ? TRUE : FALSE` only
disables the MENU ITEM, never clears `KRSA->Checked`, and `:3968` reads only `Checked`). Threaded a
new `hasStagingBuffer` parameter through `ProcessLine`/`ProcessLineCore`/`TryComputeCorrection`/
`AdoptCorrectedRate` (gating only the `Reset()` call, never the rate write) -- one bool, no default
value (a defaulted commit-semantics flag was already flagged as a hazard by
`RestoreRateWithoutReset`'s own doc comment). `ProcessLineSuppressed` hardcodes `true` (structurally
unreachable there anyway -- `suppressCommit` returns before the flag is read -- and factually
correct, since a suppressed pass only exists during replay, which only exists when a buffer does).
`ProcessLineHistoryOnly` needed no change (doesn't call `ProcessLineCore` at all). Decoder call site
(`AnalogFmSstvDecoder.cs`'s `else` arm around line 5817) hoisted to ONE shared local
(`hasStagingBuffer = _rxLineStagingBuffer is not null`) feeding both the tracker call and the
existing replay-request gate, matching legacy's own single `:5597` guard controlling both effects.
Added 1 new test (`SlantTracker_AutomaticCommit_ResetsBaselineOnlyWhenAStagingBufferExists`, a
same-input two-tracker positive/negative-control pair -- hand-derived expected values, confirmed
exact on first run) plus 16 mechanical call-site updates across `SlantTests.cs`. Scoped filter
(`SlantTests`/`CorrectSlantTests`/`CorrectSlantRequestTests`/`ReplayEngineTests`/`AutoSyncTests`)
79/79 passing. Full `tests/ScanlineStudio.Core.Sstv.Tests` suite (run because this fix touches
`AnalogFmSstvDecoder.cs` directly): 1021/1021 passing (1 unrelated intentional skip).

**Round 3 -- NOT clean, 1 new real risk found; round-2 fix confirmed correct** (2026-08-20).
Independently re-derived round-2's fix from legacy source (`Main.cpp:4011-4017,3670-3680,5581-5601,
11903,1863,3968`) without relying on round-2's citations -- confirmed the rate write is genuinely
unconditional, the guard sits one layer downstream wrapping only `InitAutoStop`, and the buffer-off
automatic-commit path really is reachable in legacy (`KRSA->Enabled` only disables the menu item,
never clears `KRSA->Checked`). Independently recomputed the round-2 test's every hand-derived value
from scratch (mult 20, baseline 100, commit at line 8 with d=-133.33, drift +3024ppm) and got the
same numbers. Fresh full sweep of `SlantTracker.cs`, `ReplayOriginCalculator.cs`, and the touched
`AnalogFmSstvDecoder.cs` regions found 1 NEW real risk (not a blocker), missed by all 3 prior
rounds: `_mult` (legacy's `m_Mult`, `Main.cpp:3860`) was `readonly`, frozen at construction --
legacy's `InitAutoStop` re-derives it from the CURRENT (possibly just-corrected) line width every
time it runs, which is exactly the same "only when hasStagingBuffer" reachability round 2 just
fixed for the rest of `Reset()`'s field set. A frozen `_mult` silently disagreed with legacy's
jitter-gate boundary (`8*mult`) by up to 8 samples after any commit that crossed a 320-sample
line-width boundary, and disagreed with this port's OWN separately-maintained Auto Sync copy of the
same legacy variable (`AnalogFmSstvDecoder.cs`), which already re-derives correctly. **Fixed this
round** (auditor gave the exact fix directly, applied without a separate fix-request round): dropped
`readonly` on `_mult`, added its recompute as the last statement of `Reset()` (the exact place
legacy re-derives it, only reached when `hasStagingBuffer` per round 2's own gate). Added 2 new test
accessors (`MultForTests`, `NominalSamplesPerLineForTests`) and 1 new test asserting the recompute
against a fresh formula evaluation (not a hand-derived constant) plus that it actually differs from
the frozen value -- mutation-verified by temporarily reverting the fix and confirming the test fails
with the exact predicted before/after values (21 expected, 20 actual), then restored. 3 more nits
found (not fixed): `AnalogFmSstvDecoder.cs`'s wrap-comment claims a two-sided shape "matches" legacy's
one-sided `Main.cpp:3889` wrap, directly contradicted by another comment 40 lines up in the same
file that correctly states it's one-sided -- code is fine, benign either way (`SlantTracker` only
ever consumes differences), only the comment is wrong; sync-envelope quantization upstream of
`ReplayOriginCalculator`'s histogram (`RxLineStagingBuffer` stores `double`, legacy folds `short`)
refines round 2's already-queued bin-type nit -- harmless, argmax is scale-invariant, only a
near-tie could theoretically resolve differently; the `Math.Max(1, ...)` clamp on `_mult` has no
legacy counterpart (unreachable for real modes). Scoped filter 80/80 passing. Full `tests/ScanlineStudio.Core.Sstv.Tests` suite: 1022/1022 passing
(1 unrelated intentional skip). Clean-round counter restarts again from 0 -- round 4 needed as the
(once more restarted) 1st clean round.

**Round 4 -- clean, 1st of the (re-restarted) 2 required** (2026-08-20). Independently re-derived
round 3's fix from legacy source: corrected round 3's own summary phrasing (which said `SetSampFreq`
and `InitAutoStop` run "within the same guarded block" -- actually `SetSampFreq` at `Main.cpp:5596`
is OUTSIDE the guard, `InitAutoStop` at `:5600` is inside it) but confirmed round 3's underlying
CONCLUSION was correct regardless (`m_TW` is already corrected by the time `InitAutoStop`'s
`m_Mult` recompute runs either way) -- and confirmed `SlantTracker.cs`'s own doc comment already
stated the OUTSIDE/INSIDE split correctly, so only the round-3 prose summary was imprecise, not the
code or its in-file documentation. Enumerated every `_effectiveSamplesPerLine`/tracker-rate writer
(4 sites) and confirmed each pairs with a rate write before any `Reset()` can observe it -- no path
re-derives `_mult` from a stale nominal, including the revert path (verified `_mult` is never
mutated on a reverted manual correction, since all of `PerformReplay`'s early-exit `return false`s
precede its `ResetBaseline()` call). Independently re-derived the round-3 test's 20->21 crossing
from scratch (baseline 750 at line 5, d=-1000 at line 8, corrected nominal 6765, `(int)(6765/320)`)
and got the identical numbers. Fresh full sweep of both files plus all 3 touched
`AnalogFmSstvDecoder.cs` regions found 1 new NIT (not a risk, not fixed): the envelope-seed branch
(`AnalogFmSstvDecoder.cs:5698`) assigns `_slantLinePeakPosition` where legacy's matching branch
(`Main.cpp:4189-4198`) does not -- legacy only ever writes `m_SyncPos` from its `else if` arm, so on
a flat/monotone-falling envelope (deep fade, dead air) legacy reports the PREVIOUS line's peak while
this port reports ~0; only reachable on that fade scenario, both settle to a constant after the
transition line, and `SlantTracker` consumes only differences -- judged not a risk. Also re-confirmed
both already-queued nits (wrap-shape comment, `_mult` clamp asymmetry with `_autoSyncBaseMult`)
still present, unchanged, none escalated. Round 5 needed for the 2nd required clean round.

**Round 5 -- 2nd consecutive clean round. Chunk 2b CLOSED** (2026-08-20). Independently re-verified
round 4's own findings from scratch (not trusted): re-confirmed the exact legacy ordering
(`SetSampFreq` unconditional at `Main.cpp:5596`, `InitAutoStop` -- and its `m_Mult` recompute --
inside the staging guard at `:5597`/`:5600`, so round 3's fix is genuinely legacy-faithful);
re-enumerated all 4 `_effectiveSamplesPerLine` writer sites and all 4 matching tracker-rate writer
sites by grep (not by trusting round 4's count) and confirmed every pair uses an identical formula,
so stride and tracker rate can never desync; re-derived the round-3 test's 20->21 mult crossing one
more time from the current code/constants and got the same numbers, including confirming the sign
matters (a decreasing drift would NOT cross the boundary, only the test's increasing one does).
Fresh full 8-item sweep of both files and all 4 `AnalogFmSstvDecoder.cs` regions (including
`PerformReplay`'s `ResetBaseline()` call site, newly in scope this round) found zero new blockers
and zero new real risks -- only nit-level refinements: the envelope-seed divergence round 4 flagged
has a second, parallel half round 4 missed (`AnalogFmSstvDecoder.cs`'s own per-line
`_slantLinePeakPosition = 0` reset, which legacy also has no counterpart for -- deleting only round
4's cited line would not restore legacy behavior on its own), still judged not a risk (the port's
value fails the jitter gate conservatively rather than corrupting anything); and a new nit noting
`ReplayOriginCalculator.ComputeOrigin`'s bin array is `double[]` while the legacy line it cites
allocates `int[]` (comment-accuracy only, behaviorally inert -- argmax-only, non-negative envelope).
Both previously-queued nits (wrap-shape comment, `_mult` clamp asymmetry) re-confirmed unchanged,
neither escalated across 5 rounds. **Final tally for chunk 2b: 5 rounds, 3 real fixes (round 1:
numeric fidelity + revert-preserves-state; round 2: buffer-existence-gated automatic reset; round
3: jitter-gate multiplier re-derivation), each restarting the 2-consecutive-clean-round counter,
then 2 genuinely clean rounds (4-5) to close.** `ReplayOriginCalculator.cs` was fully clean across
all 5 rounds -- zero findings, ever. 5 nits remain queued, none blocking, none escalated: the
wrap-shape comment, the `_mult` clamp/`_autoSyncBaseMult` asymmetry, the envelope-seed/peak-reset
divergence pair, and the histogram bin-type comment. Full `tests/ScanlineStudio.Core.Sstv.Tests`
suite last confirmed at 1022/1022 (1 unrelated intentional skip) after round 3's fix; no production
code changed in rounds 4-5 (doc-only rounds).

**Chunk 2c (`RestartableSstvDecoder.cs`, 845 lines, whole file) round 1 fixed** (2026-08-20). This
class already received substantial scrutiny during the earlier, separate `AnalogFmSstvDecoder.cs`
functional-audit sweep's D2 chunk (dispose guard, transactional `Swap()`, maintenance-event-loss
fix, all already in the current code) -- round 1 was scoped to chunk 2c's own lens (RX
buffer/replay arithmetic: overflow safety, swap/threshold arithmetic, local-vs-absolute index
math) instead of re-litigating D2's settled findings. No legacy counterpart exists for this class
(a pure port-side engineering decision -- legacy is a single long-lived object, unbounded reception
time apparently wasn't a legacy concern) -- judged on internal-invariant correctness, not parity.
**No blocker.** Hand-verified the threshold arithmetic (`ComputeDefaultThresholds`) at 3 sample
rates against independently recomputed values, all exact; confirmed the classic stale-capture bug
is absent (`current = _inner` re-read AFTER `Swap()`, so a swap-triggering chunk correctly reaches
the FRESH decoder's zeroed counter); confirmed swap-vs-in-flight-replay safety is structural (a
replay can never be on the stack when `Swap()` disposes the outgoing staging buffer, since both are
serialized through the same `_pushActive`/`_gate` pair). Found 1 real risk (proof-completeness gap,
not a live bug): `ComputeMaximumComposedProjectionSamples`'s reserve is scaled by the AUTOMATIC
Auto-Slant clamp only: the MANUAL Correct Slant path has no such clamp by design and is guarded
independently, not by this reserve -- unreachable in practice at most rates (tens-of-times
headroom) but at the top of the supported rate range `critical == maximumSafeSampleIndex` exactly,
so the reserve IS the entire remaining headroom there. **Fixed**: added a doc-comment note on
`ComputeMaximumComposedProjectionSamples` recording the assumption explicitly, so a future clamp
change on either guard gets flagged rather than silently invalidating the other's proof. Doc-only,
zero behavior change. 3 more nits queued (not fixed): an idle swap can still drop up to ~1.3s of
pre-lock raw samples if a header started inside that window and hasn't committed yet (undocumented
consequence of the discard-and-reconstruct design, negligible in practice); `RequestReSync`/
`RequestCorrectSlant`/`ForceMode`'s doc comments frame a dropped deferred command as a
"race" when it also happens with zero concurrency (comment wording only); 2 dead defensive branches
in `ComputeDefaultThresholds` (unreachable given how their operands are already constrained
upstream) plus the pure function being evaluated 3x in the public ctor chain (correctness-neutral).
Scoped filter (`RestartableSstvDecoderTests`/`DecoderSubscriberFailureTests`) 46/46 passing. Round
2 needed for chunk 2c's own 2-consecutive-clean-round gate.

**Round 2 -- clean, 1st of 2 required** (2026-08-20). Independently re-derived round 1's threshold
arithmetic by hand at all 3 checked rates (5000/11025/48500Hz) before looking at the test's own
`InlineData`, matched exactly; confirmed the "reserve is the entire remaining headroom at the top
rate" claim, and additionally derived the actual crossover point round 1 didn't state --
`critical == maximumSafeSampleIndex` for every rate >= ~42,495Hz, which includes 44100 and 48000,
the two most common real capture rates, not just some exotic top-of-range value. Independently
re-verified the stale-capture-bug-absence and swap-vs-in-flight-replay structural-safety claims by
reading `PushSamplesCore`/`Swap()` directly. Fresh full sweep of the WHOLE file (including parts
round 1 didn't focus on: `CreateInner`'s full constructor-forwarding list -- confirmed all 11
parameters forwarded by name, none dropped; event subscribe/unsubscribe symmetry; the
`StationIdDecodeEnabled` live-setter's concurrency contract; `Dispose()`'s idempotency) found zero
new blockers, zero new real risks. 3 new nits (none fixed, none escalated): round 1's doc fix
undersold its own scope (says "top of the supported rate range," actually engages at ~42.5kHz+,
covering 44100/48000); the class doc's `_pushActive` guard claim covers "swap" but not "dispose" --
a forwarded-decode-event handler calling `Dispose()` re-entrantly genuinely can dispose mid-decode,
unreachable in production (`SstvSessionService` only disposes after `StopReceivingAsync`) but
undocumented; post-`Dispose()` telemetry getters (`SlantPpm` etc.) return stale last-known values
rather than throwing, arguably correct for a UI poll timer racing shutdown but undocumented. Round
1's 3 queued nits re-confirmed still open, unchanged. Round 3 needed for the 2nd required clean
round.

**Round 3 -- 2nd consecutive clean round. Chunk 2c CLOSED. Tier A Batch 2 (chunks 2a+2b+2c)
FULLY DONE** (2026-08-20). Independently re-derived the threshold arithmetic at all 3 checked rates
one more time from the current formula (all 6 values matched the pinned test rows exactly) and
independently derived the exact crossover point itself (round 2 had stated it approximately) --
solved `46800r + 3600*(1100/1060)r >= 2^31-1` directly and verified both integer neighbours by
hand: the exact crossover is **42,495 Hz**, confirming `critical == maximumSafeSampleIndex` for
every rate at or above it, including 44100 and 48000 -- the two most common real capture rates, not
an exotic edge case. Re-verified the stale-capture-bug-absence and swap-vs-in-flight-replay claims
by reading `PushSamplesCore`/`Swap()` fresh. Fresh full 8-item sweep added coverage neither prior
round had done: verified all 18 `ISstvDecoder` interface members are genuinely implemented (no
silent no-op); re-derived `CreateInner`'s constructor-forwarding correctness against the actual
callee signature (all 11 parameters, all defaults matching); confirmed the class doc's own 4-step
state-machine description maps exactly onto the real code; confirmed the cross-thread concurrency
contract (synchronous, no buffering, slow-subscriber-blocks-caller) is both correctly documented
and correctly implemented. Zero new blockers, zero new real risks -- only 2 more trivial nits (one
diagnostic accessor doesn't lock `_gate` unlike its 8 siblings; a swap increments its restart
counter after disposing the outgoing decoder, safe only because that dispose is documented as
unable to throw). **Fixed** the one cheap, now-exactly-verified doc-wording nit round 2 flagged:
`ComputeMaximumComposedProjectionSamples`'s note said "at the top of the supported rate range" --
replaced with the precise, independently-derived "at 42,495 Hz and above (including 44,100 and
48,000)". Scoped filter 46/46 passing. **Final tally for chunk 2c: 3 rounds, 1 real risk fixed
(round 1, doc-only proof-completeness gap) + 1 wording-precision fix (round 3), 2 clean rounds (2-3)
to close.** 5 nits remain queued across the chunk, none blocking, none escalated (idle-swap
pre-lock discard window, "race" wording on zero-concurrency drops, 2 dead defensive branches +
triple-evaluated pure function, an unlocked test accessor, dispose-ordering-depends-on-Dispose-
never-throwing).

## Tier A Batch 2 -- CLOSED (2026-08-20)

**13 rounds total across 3 chunks, 7 real fixes, every chunk closed under the formal
2-consecutive-clean-round gate:**
- **Chunk 2a** (`RxLineStagingBuffer.cs`+`RxDiskLineStagingBuffer.cs`): 3 rounds, 1 blocker (RAM
  capacity-rejection latch missing) + 2 hardening fixes.
- **Chunk 2b** (`ReplayOriginCalculator.cs`+`SlantTracker.cs`): 5 rounds, 3 real fixes (numeric
  fidelity + revert-state, buffer-existence-gated reset, jitter-gate multiplier re-derivation) --
  `ReplayOriginCalculator.cs` itself was clean across all 5 rounds. The chunk with this batch's
  clearest illustration of "review cadence scales with risk, not with wanting to go faster": 3
  consecutive real findings, each one restarting the clean-round counter, before 2 genuinely clean
  rounds closed it.
- **Chunk 2c** (`RestartableSstvDecoder.cs`): 3 rounds, 1 real fix (a proof-completeness doc gap) +
  1 wording-precision fix.

All commits: `e59cabf`/`29ef022`/`80a5a6b` (2a), `a77fc68`/`7321714`/`7c4e0ff`/`6a66e69`/`73e4e46`
(2b), `bb0ee82`/`0a665f4`/(this closure) (2c). Full `tests/ScanlineStudio.Core.Sstv.Tests` suite
last confirmed at 1022/1022 (1 unrelated intentional skip).

## Tier A Batch 3 -- IN PROGRESS, started 2026-08-20

PTT/transmit sequencing (real-world harm class -- a leaked keyed transmitter, not just bad
output). ~2663 lines across 5 files (`SstvSessionService.cs` 1098, `RadioController.cs` 405,
`TcpTransport.cs` 150, `RigctldClientProtocol.cs` 477, `HamlibRadioProtocol.cs` 533) -- chunked
into 3, following Batch 2's precedent:
- **Chunk 3a**: `SstvSessionService.cs`'s PTT lifecycle (the table's own explicit pointer:
  `TryUnkeyPttAsync`/`TryCleanupAsync` exception paths) -- `SetPttLockAsync`, `StartReceivingAsync`/
  `StopReceivingAsync`/`TransmitAsync`/`TuneAsync`, `PlayWithPttAsync`/`TryUnkeyPttAsync`/
  `TryCleanupAsync`/`DisposeAsync`. Already carries dense audit history in its own comments
  ("Auditor-caught round 1/2", `spec/18-path-to-1.0.md` Critical item 1) from an earlier,
  DIFFERENT audit track -- treat as settled context, not open findings; this chunk's own lens is
  chunk-3-specific (PTT-unkey guarantee under every exception path), not a re-litigation.
- **Chunk 3b**: `RadioController.cs` + `TcpTransport.cs` (review together).
- **Chunk 3c**: `RigctldClientProtocol.cs` + `HamlibRadioProtocol.cs` (the two CAT backend
  protocol implementations).

**Status**: chunk 3a round 1 returned -- 3 real blockers found, fix-request in progress. 3b/3c not
yet started.

**Chunk 3a round 1** (2026-08-20). No legacy counterpart for CAT/PTT control (CLAUDE.md §2 --
never ported, pure client of external backends), so this chunk skips legacy-parity checklist items
and judges purely on internal-invariant correctness/exception-safety/concurrency, with the single
top-priority failure mode named explicitly in the delegation: does every path guarantee the
transmitter gets un-keyed? Answer: **no, not on 3 reachable paths.**

1. **[blocker]** The 5s `cleanupCts` cleanup budget starts before `StopPlayback` runs, and the real
   `MiniAudioEngine.StopPlaybackAsync` backend can take up to ~10s worst case (`DrainTimeout` 5s +
   `CloseTimeout` 5s + a 200ms tail margin) -- so on a stuck/unplugged output device, `cleanupCts`
   is ALREADY CANCELLED by the time `TryUnkeyPttAsync` runs, and both real CAT protocols honor the
   token at their lock-acquire gate. PTT-off is never sent to the rig at all; the failure is caught,
   logged Warning, and nothing retries. This reproduces the exact hazard the bounded-timeout
   pattern exists to prevent, with a different token.
2. **[blocker]** `TryUnkeyPttAsync`'s `RigId == "none"` catch-time check treats a genuinely stuck-
   keyed rig as benign whenever `RadioController.DisconnectAsync`/`DisposeAsync` runs between the
   key and the cleanup un-key attempt (both set `_rigId = "none"` without ever un-keying PTT
   themselves) -- a physically keyed transmitter gets logged as "no radio to un-key" and silently
   swallowed. The file's own doc comment claims a mid-cleanup disconnect "still gets the real
   Warning" -- true only for the poll-loop backoff window, false for an explicit
   disconnect/dispose.
3. **[blocker]** `DisposeAsync`'s shutdown backstop is gated on `_pttLocked` only -- nothing tracks
   "a `PlayWithPttAsync` currently has PTT keyed," and `DisposeAsync` neither cancels nor awaits an
   in-flight transmit. A concrete reachable sequence on window-close during TX: the UI's
   `TxControlsPaneViewModel.Dispose()` cancels the transmit token WITHOUT awaiting it, DI singleton
   teardown proceeds, `RadioController` disposes (triggering finding #2's silent-swallow) while the
   transmit's own `finally`/un-key is still in flight, and the process can exit within its 10s
   teardown bound with the transmitter still keyed and nothing logged as wrong.

Also found: a failed un-key has no retry/escalation/user-visible state (only a log line, given the
blast radius of a keyed transmitter); `_pttLocked` is read twice for one branch decision with a
narrow window where a racing `SetPttLockAsync(false)` can strand RX stopped forever (not a PTT
leak); the shutdown un-key doesn't take the PTT-lock gate and an already-cancelled `ct` makes
`SetPttLockAsync(false)`'s "emergency escape hatch" throw without attempting an un-key. Nit:
`StopReceivingAsync` detaches handlers before awaiting the stop call, sitting on `PlayWithPttAsync`'s
own pre-guard path. Everything else in the audited regions (the `finally` block's own
exception-safety up to the un-key call, `abnormalTermination` coverage on every exit path, the
concurrent-double-transmit interleavings) verified clean -- confirmed no other path leaks. Fix
proposal requested next.

**Chunk 3a round 1 fix applied** (2026-08-20). All 3 blockers confirmed against current source by
the fix-proposal agent before any code changed (including re-deriving `MiniAudioEngine`'s real
~10.2s worst-case `StopPlaybackAsync` duration, `RadioController.DisconnectAsync`/`DisposeAsync`'s
real `_rigId="none"` reset, and `TxControlsPaneViewModel.Dispose()`'s real fire-and-forget cancel).
Found a 4th instance of the same failure class not in round 1's original report: `DisposeAsync` ran
`StopReceivingAsync` FIRST, which ends in a drain-thread `Join()` with NO timeout at all if a
`SamplesCaptured` subscriber never returns -- so a wedged drain thread could hang shutdown before
the PTT backstop was ever reached. Fixed alongside the other 3 (free, in-scope, same file).

**Fix summary** (full reasoning/diffs in commit message, not restated here):
1. **Blocker 1**: `PlayWithPttAsync`'s un-key now runs FIRST on abnormal termination (no audio tail
   worth preserving on a cutoff -- "off now" is the entire point), and gets a fresh, independent
   `CancellationTokenSource` for both call sites so a slow/wedged `StopPlayback` can never starve
   its budget. `StopPlayback` itself gets a bounded watchdog (`Task.WhenAny` against
   `_playbackStopWaitBudget`) instead of an unbounded await, since `IAudioEngine.StopPlaybackAsync`
   takes no cancellation token at all -- on a healthy drain, order and behavior are bit-for-bit
   unchanged (drain-then-unkey, preserving `MiniAudioEngine.DrainTailMargin`'s own reason for
   existing).
2. **Blocker 2**: `TryUnkeyPttAsync` no longer re-reads `RigId` at catch time -- a `pttKeyedOnRealRig`
   flag captured ONCE, at the moment PTT was actually keyed, is now the only input to "was this a
   real stuck-keyed rig or the benign no-radio case," closing the window where a mid-cleanup
   `RadioController` disconnect/dispose could report a physically keyed transmitter as "nothing to
   unkey."
3. **Blocker 3**: a new `TaskCompletionSource? _keyedTransmitCompletion` field, published before
   every real key command and cleared only once that call's own cleanup finishes, lets
   `DisposeAsync` WAIT (bounded, `_inFlightKeyedTransmitWait`) for an in-flight transmit's own
   un-key before proceeding, then force-un-keys itself if that wait times out. A new
   `_pttLeftKeyedByCall` flag closes the `TuneAsync(leaveKeyedAfterTune: true)` gap
   (`_pttLocked` never covered it). `DisposeAsync`'s own step order changed: PTT backstop now runs
   BEFORE `StopReceivingAsync` (closing the 4th finding above).
4. Also fixed: a failed un-key now logs `Critical` (was `Warning`), gated on the captured real-rig
   flag so the fresh-install no-radio path stays quiet; `_pttLocked` is now read exactly once per
   cleanup decision (was twice, at two different points, opening a window where a racing
   `SetPttLockAsync(false)` could strand RX stopped forever) plus a publish-then-recheck pattern
   closing the other half of that same race a first fix attempt would have missed.
   Deliberately NOT fixed this round (queued): un-key retry (costs real budget against a
   ~10s host-teardown ceiling, needs its own scoped timeout re-derivation); a user-visible
   escalation UI for a still-possibly-keyed rig (cross-layer, needs a VM + localized string).

**New test infrastructure**: an `internal` constructor overload (public ctor delegates to it,
matching `RxDiskLineStagingBuffer`'s own `disposeDrainTimeoutForTests` precedent) lets tests shrink
the 5s/5s/3s production budgets so they can actually EXPIRE without a multi-second-per-test suite --
required a new `ScanlineStudio.Application/AssemblyInfo.cs` (`InternalsVisibleTo`
`ScanlineStudio.Application.Tests`, this project's first). `FakeRadioSessionService` gained a
`BeforeSetPtt` hook so a test can mutate `RigId` from inside a `SetPttAsync` call, reproducing the
exact blocker-2 race. New file `SstvSessionServicePttSafetyTests.cs`, 12 tests: 3 for blocker 1
(includes a negative test proving the drain-then-unkey order survives on a healthy path), 2 for
blocker 2 (plus a negative test proving the fresh-install no-radio path stays quiet), 4 for blocker 3
(includes the DisposeAsync-order fix and the `leaveKeyedAfterTune` gap), 1 for risk B, and 1
verifying the real DI container resolves the public constructor, not the internal test-only one (a
stated-but-unverified assumption in the fix proposal -- confirmed for real, not assumed). One test's
own final assertion was found and fixed during verification (asserted a cancellation exception that
could no longer fire once the fix made cleanup faster) -- a test-authoring bug, not a production one.
Blocker 2's regression test mutation-verified by temporarily reverting the fix and confirming it
fails with the exact predicted signature (a benign "unkey skipped" log instead of the Critical
escalation), then restored.

All 78 pre-existing `SstvSessionServiceTests` still pass unmodified -- hand-traced 4 of the highest-
risk ones (`SwrCutoffStyleCancellation_ForceUnkeysAndClearsLock_EvenWhileLocked`,
`TuneAsync_LeaveKeyedAfterTuneTrue_...`, `SetPttLockAsync_False_AlwaysAttemptsUnkey_...`,
`DisposeAsync_WhilePttLocked_...`) step-by-step through the rewritten code before running anything,
to catch a logic error the test suite itself might not surface. Full `ScanlineStudio.Application.Tests`
171/171 passing, full solution build clean.

**One of the 12 new tests was itself flaky, caught by a full-solution run** (not the isolated
scoped run, which passed): `Blocker1_AbnormalTermination_...` originally raced a 10ms
`CancellationToken` against `GenerateTone`'s own CPU-bound sample-generation speed (the same
technique the pre-existing `TuneAsync_TokenCancelledMidTone_...` regression test uses). On a fast/
idle machine, 240,000 samples of sine synthesis can finish well under 10ms, so cancellation never
fired before the tone completed NORMALLY -- confirmed by running the isolated test 5x standalone
(no other test load), which hung for its full 5s timeout every time, versus passing reliably as
part of a larger, more heavily-loaded batch run. Fixed by replacing the timing race with a
deterministic trigger: a new `ThrowOnStartPlaybackAudioEngine` test decorator that throws from
`StartPlaybackAsync` (reached after PTT is keyed, before any sample is ever generated) -- no
machine-speed dependency at all. Re-ran 5x standalone after the fix, all passed. A test-authoring
bug caught by exactly the kind of rigor this fix's own severity demanded, not a production issue.

Full solution test run (build + all test projects) confirmed clean after this fix, including the
corrected flaky test.

**Chunk 3a round 2** (2026-08-20, independent re-verification agent, fresh context). Round 1's three
blockers re-derived independently from current source (not trusted from the write-up) and confirmed
genuinely closed. **Found one new real blocker, same leaked-keyed-transmitter class**: no `_disposed`
gate existed anywhere in `SstvSessionService`. `DisposeAsync`'s backstop only guards
`_keyedTransmitCompletion` state published BEFORE it runs -- a `PlayWithPttAsync` call still resolving
its playback device (before it has published anything or keyed PTT at all) could let `DisposeAsync`
run, find nothing to wait on/un-key, and return -- and only then would the call go on to key PTT, with
no shutdown backstop left. Confirmed reachable on a real production path, not just synthetic:
`RadioStatusViewModel.TuneAsync` calls `_sstvSession.TuneAsync(1750, 5s)` with **no `CancellationToken`**
(defaults to `CancellationToken.None`), so nothing can cancel this window away. Also found 4 nits (log
severity misclassification on one early-throw path, a stale `_rxPendingResumeAfterUnlock` flag,
non-idempotent `DisposeAsync`, an unlinked shutdown-wait timer) -- not fixed this round, none in the
leaked-keyed-transmitter class.

**Chunk 3a round 2 fix applied** (2026-08-20). New `volatile bool _disposed` field, same threading
shape as `_pttLocked`. `DisposeAsync` sets it as the very FIRST line (before even
`AwaitInFlightKeyedTransmitAsync`'s read of `_keyedTransmitCompletion`). `PlayWithPttAsync` re-checks
it immediately after publishing `_keyedTransmitCompletion` (publish-then-recheck, same shape as Risk
B's own pattern already in this file) -- if `_disposed` is now true, throws `ObjectDisposedException`
*before* issuing the actual key command, so PTT is provably never keyed by a call whose device
resolution only finished after disposal already ran. `SetPttLockAsync` also rejects the ENGAGE
direction post-disposal (`ObjectDisposedException.ThrowIf`) while still allowing unlock through --
disposal must never remove the one remaining way to un-key an already-keyed rig.

New regression test `Round2_DisposeAsync_RacesATuneStillInItsPreKeyWindow_NeverKeysAfterDisposeReturns`
in `SstvSessionServicePttSafetyTests.cs`: gates `FakeAudioDeviceEnumerator.RefreshAsync` (new `Gate`
hook) to park a `TuneAsync` call inside its pre-key window, disposes the service while it's parked,
releases the gate, and asserts PTT is never commanded ON (`Assert.DoesNotContain(true, radio.PttCalls)`)
and the call throws `ObjectDisposedException`. Mutation-verified by temporarily reverting the
disposed-check in `PlayWithPttAsync` -- test failed with the exact predicted signature ("No exception
was thrown"/`ObjectDisposedException` expected), then restored. Full `ScanlineStudio.Application.Tests`
172/172 passing, full solution suite (all projects) clean.

Round 2 found a real blocker -- **not** a clean round. Round 3 (independent re-verification of this
fix) required next before chunk 3a can start counting toward the 2-consecutive-clean-round gate.

**Chunk 3a round 3** (2026-08-20, independent re-verification agent, fresh context). **VERDICT: no
new blockers** -- round 2's fix independently re-derived and confirmed to genuinely close the hole
it claims to close, for every interleaving reachable through normal .NET scheduling. Found 4 real
risks (still the same failure class, none rising to blocker) plus re-confirmed round 2's 4 already-
queued nits are still accurate:
1. **Memory-model gap**: the publish-then-recheck between `PlayWithPttAsync` and `DisposeAsync` used
   plain `Volatile.Write`/`Volatile.Read` -- release-store/acquire-load, which does NOT forbid
   StoreLoad reordering (a Dekker's-algorithm-shaped gap). Real on x86-64, where .NET emits both as
   plain `mov`s; incidentally safe on ARM64 (`stlr`/`ldar` is sequentially consistent). Window is
   store-buffer-drain scale (tens of ns) vs. round 2's pre-fix window (full device enumeration +
   settings I/O) -- a ~6-order-of-magnitude reduction, not a reopened blocker. The pre-existing Risk
   B pattern (`_rxPendingResumeAfterUnlock`/`_pttLocked`) has the identical shape/gap -- round 2
   inherited it rather than introducing something new; that copy's failure mode is a stranded RX
   pause, not a leaked keyed transmitter, so it wasn't given the same treatment.
2. **A 4th "keyed at shutdown" state `DisposeAsync` doesn't cover**: a transmit whose own cleanup
   un-key was attempted and FAILED records nothing today (`_pttLocked` already false by then,
   `_pttLeftKeyedByCall` never set for this path, `_keyedTransmitCompletion` unconditionally cleared
   regardless of the un-key's outcome) -- so a rig already logged Critical about ("MAY STILL BE
   KEYED") is read by `DisposeAsync`'s existing three-state check as "nothing to do." Most valuable
   when the failure was `CleanupTimeout` (5s) expiring against a slow-but-healthy backend, where a
   fresh attempt at shutdown would likely succeed.
3. **`PlayWithPttAsync`'s cleanup can restart RX after `DisposeAsync` has disposed the
   decoder/waterfall**: if `AwaitInFlightKeyedTransmitAsync`'s bound expires while a transmit's own
   cleanup is inside `TryCleanupAsync("Resume RX", ...)`, `DisposeAsync` proceeds, disposes the
   decoder/waterfall, then the parked `StartReceivingAsync` completes -- subscribing capture
   handlers to a live engine and calling `ResetAgc()` on a disposed decoder. Pre-existing since
   round 1's `DisposeAsync` reorder; round 2's new throw made the dispose-race path deterministic
   rather than incidental.
4. **`SetPttLockAsync`'s disposal gate checks only before the `SetPttAsync` await, never after** --
   that await is a real serial/TCP round-trip, so `DisposeAsync` can run start-to-finish inside it
   and miss the engage (reads `_pttLocked` while still false, `_keyedTransmitCompletion` null).
   **Latent only**: grep-confirmed zero production callers of `SetPttLockAsync` (matches the
   method's own doc comment).
No new bug from the `ObjectDisposedException` throw itself -- traced fully clean (correct
`abnormalTermination` classification, `keyedCompletion` still cleared/completed so a concurrent
`DisposeAsync` wait can't hang). One nit: the new throw path logs at Error with a full stack trace
on every ordinary close-during-tune -- should be Information, same treatment as the existing
`OperationCanceledException` arm. Test mutation-sensitivity spot-checked (traced, not run) for
`Round2_...NeverKeysAfterDisposeReturns` and 2 round-1 tests -- all genuinely sensitive. One nit:
`SetPttLockAsync`'s disposal gate had zero test coverage in either direction.

**Chunk 3a round 3 fix applied** (2026-08-20, commit `fd6441c`). All 4 risks fixed (judged worth
fixing now despite "risk" not "blocker" severity: finding 1 undermines the correctness of round 2's
own core safety mechanism, the others are cheap one-liners to the same file already under review):
1. `Interlocked.Exchange` (publish side) + `Interlocked.MemoryBarrier` (dispose side) replace the
   plain volatile write/read -- a genuine full fence, closing the Dekker's gap.
2. New `volatile bool _pttUnkeyFailedOnRealRig` field, set in `UnkeyForCleanupAsync` alongside the
   existing Critical log, cleared on every confirmed un-key; added to `DisposeAsync`'s condition
   (now checks 4 states, not 3) so a previously-failed un-key gets retried at shutdown instead of
   silently walked away from.
3. `ObjectDisposedException.ThrowIf(_disposed, this)` added to the top of `StartReceivingAsync` --
   every caller already routes through `TryCleanupAsync`/its own try/catch, so this degrades to a
   logged Warning rather than propagating unhandled.
4. `SetPttLockAsync` gained a post-await recheck: if disposal raced the `SetPttAsync` round-trip and
   won, the call now un-keys its own just-engaged lock and throws, rather than leaving `_pttLocked`
   true with nothing left to ever clear it.
Also fixed the log-severity nit: a new `catch (ObjectDisposedException) when (_disposed)` arm (before
the generic `catch (Exception)`) logs `PlaybackAbortedByDispose` at Information, matching the
existing `OperationCanceledException` treatment -- guarded on `_disposed` so a GENUINE
`ObjectDisposedException` from some other dependency still logs as a real failure via the generic
catch. The 3 remaining previously-known nits (early-throw log-severity misclassification, stale
`_rxPendingResumeAfterUnlock`, non-idempotent `DisposeAsync`, unlinked shutdown-wait timer) remain
queued, not fixed this round -- none in the leaked-keyed-transmitter class.

New regression tests: `Round3_DisposeAsync_RetriesAnUnkeyThatFailedDuringItsOwnCleanup` (finding 2 --
simulates a `TimeoutException` on the transmit's own cleanup un-key, then confirms `DisposeAsync`
retries and succeeds), `Round3_SetPttLockAsync_Engage_PostDispose_ThrowsAndNeverKeys` and
`Round3_SetPttLockAsync_Unlock_PostDispose_StillWorks` (closing the round-3-flagged test-coverage gap
on `SetPttLockAsync`'s engage/unlock disposal asymmetry). Finding 2's test mutation-verified by
temporarily reverting `DisposeAsync`'s 4-state condition back to 3 -- failed with the exact predicted
signature (`[True]` instead of `[True, False]`), then restored. All 175 `ScanlineStudio.Application.Tests`
passing, full solution suite (all projects) clean.

Round 3 was clean of new BLOCKERS but found real risks that were fixed -- per this project's own
established convention (see chunk 2b), a fix restarts the clean-round count. Round 4 (independent
re-verification of round 3's fix) is the earliest round that can count as chunk 3a's 1st clean round.

**Chunk 3a round 4** (2026-08-20, independent re-verification agent, fresh context). **VERDICT:
clean of new blockers, but 1 new real risk** -- so this does NOT count as chunk 3a's 1st clean
round either. All 4 of round 3's fixes independently re-derived and confirmed correct on their own
terms (including a full trace of the `Interlocked.Exchange`/`Interlocked.MemoryBarrier` fence pair,
confirmed a genuine sufficient Dekker fence, and confirmed every `StartReceivingAsync` call site
handles the new `ObjectDisposedException`). But round 3's own finding-4 fix (the `SetPttLockAsync`
post-await recheck) introduced a new instance of the SAME memory-model gap finding 1 had just
closed elsewhere: the recheck's `_pttLocked = locked` write and `_disposed` read were plain
volatile, not fenced -- by the file's own stated criterion (a pattern gets a fence specifically
because its failure mode is a leaked keyed transmitter, not the lower-severity stranded-RX-pause
class), this pattern qualified and should have gotten one. Also found the round-3 fix for finding 2
(`_pttUnkeyFailedOnRealRig`) wasn't cleared by `SetPttLockAsync`'s successful-unlock branch, despite
that branch's own comment claiming it clears "every" still-keyed belief -- a stale flag from an
earlier failed transmit cleanup would survive a genuinely successful unlock and cause `DisposeAsync`
to fire a spurious backstop un-key, risking a **false Critical** ("PTT MAY STILL BE KEYED") on a rig
that is demonstrably off. Plus nits: the Risk B comment's "closes that window" claim directly
contradicted finding 1's own comment on the exact same gap (fixed only in the fenced copy, not the
deliberately-unfenced Risk B original); two `ObjectDisposedException` throw sites used different
`ObjectName` values (`nameof(SstvSessionService)` vs. `ThrowIf`'s `this`-derived
`GetType().FullName`); the round-3 test for finding 4 (`Round3_SetPttLockAsync_Engage_
PostDispose_ThrowsAndNeverKeys`) disposed BEFORE calling `SetPttLockAsync`, so it only exercised the
cheap pre-await guard and stayed green even with the post-await recheck deleted entirely --
genuinely mutation-insensitive to the fix it was meant to cover; `StartReceivingAsync`'s new
disposed guard had zero test coverage. One pre-existing, out-of-class internal-invariant issue
noted off-scope: `StopReceivingAsync` can leave `_isReceiving` permanently `true` if
`StopCaptureAsync` throws (handlers already detached, `_isReceiving` flip happens after the
throwing call) -- not a PTT leak, logged here, not chased.

**Chunk 3a round 4 fix applied** (2026-08-20, commit `93ef0b5`). Both real findings fixed:
1. `SetPttLockAsync` gained its own `Interlocked.MemoryBarrier()` immediately after the
   `_pttLocked = locked` write (and after clearing `_pttLeftKeyedByCall`/`_pttUnkeyFailedOnRealRig`
   in the unlock branch), before the `if (locked && _disposed)` recheck -- the missing other half of
   the fence `DisposeAsync` already carries.
2. `_pttUnkeyFailedOnRealRig = false;` added to `SetPttLockAsync`'s `if (!locked)` block, alongside
   the existing `_pttLeftKeyedByCall = false;` -- a confirmed un-key now genuinely invalidates every
   still-keyed belief, matching that block's own comment.
Also fixed the 3 nits: Risk B's comment corrected to "narrows, does not close" with a pointer to the
now-fenced pattern's own comment for the mechanism and an explicit statement of the fencing
criterion; both manual `throw new ObjectDisposedException(...)` sites switched from
`nameof(SstvSessionService)` to `GetType().FullName` to match `ThrowIf`'s `ObjectName`; the
`when (_disposed)` catch-guard comment tightened to state precisely what it does and does not
distinguish.

New/fixed regression tests: `Round4_SetPttLockAsync_DisposeRacesTheSetPttAsyncAwait_
ThrowsAndUnkeysRatherThanLeavingLockedTrue` (drives `DisposeAsync` from inside the `SetPttAsync`
round-trip itself via `FakeRadioSessionService.BeforeSetPtt`, the actual race the post-await recheck
exists to close -- replaces the round-3 test's mutation-insensitive coverage), `Round4_
StartReceivingAsync_PostDispose_Throws` (closes the finding-3 coverage gap), `Round4_
SetPttLockAsync_SuccessfulUnlock_ClearsStaleUnkeyFailedFlag_NoSpuriousBackstop` (closes the clearing
gap). Both new fixes mutation-verified by temporarily reverting each and confirming the exact
predicted failure signature (a spurious extra un-key call, and "No exception was thrown"
respectively), then restored. All 178 `ScanlineStudio.Application.Tests` passing, full solution
suite (all projects) clean.

Round 4 was clean of new blockers but found 1 real risk that was fixed -- round 4 does NOT count as
chunk 3a's 1st clean round either. Round 5 (independent re-verification of round 4's fix) is now the
earliest round that can count as chunk 3a's 1st clean round.

**Chunk 3a round 5** (2026-08-20, independent re-verification agent, fresh context). Given the
whack-a-mole pattern across rounds 2-4 (each fix introduced/left a new instance of the same
unfenced-Dekker's-pattern gap), this round's prompt was deliberately WIDENED to a comprehensive
sweep of every cross-field publish-then-recheck pattern in the whole file, not just a re-check of
round 4's specific fix. **VERDICT: NOT CLEAN -- 2 new risk-level findings.**

The sweep enumerated 6 such patterns total. 2 are correctly fenced (`_keyedTransmitCompletion`↔
`_disposed` in `PlayWithPttAsync`/`DisposeAsync`; `_pttLocked`↔`_disposed` in
`SetPttLockAsync`/`DisposeAsync`) -- confirmed `DisposeAsync`'s single `Interlocked.MemoryBarrier()`
correctly serves BOTH pairs (a full fence orders its preceding store against every subsequent load
on that thread, not one designated load -- no second fence needed). 2 are deliberately unfenced,
consistent with the file's own stated policy (Risk B; a no-recheck-needed acquire/release chain for
the shutdown flags). **2 are new:**
1. **Lost-update race in `UnkeyForCleanupAsync`**: its post-success state clears
   (`_pttLocked`/`_pttLeftKeyedByCall`/`_pttUnkeyFailedOnRealRig` = `false`) ran unconditionally on
   the continuation AFTER the un-key succeeded -- but a CONCURRENT, NEWER key command
   (`SetPttLockAsync(true)` or another transmit) can complete in that exact window and record its
   own "still keyed" state, which the stale continuation then wipes out from under it: transmitter
   genuinely re-keyed, every shutdown-backstop flag reads false, `DisposeAsync`'s four-state check
   finds nothing to do. Leaked-keyed-transmitter class. Same latency status as rounds 3/4's findings
   (`SetPttLockAsync` has zero production callers today) -- fixed anyway per this chunk's own
   established standard for that situation.
2. **`StartReceivingAsync`'s `_isReceiving` check-then-act can double-subscribe capture handlers
   under concurrency**, which falsifies the "harmless duplicate resume" justification the Risk B
   comment relies on to defend leaving that pattern unfenced. NOT leaked-keyed-transmitter class
   (failure mode is corrupted RX decode from double-subscription, a real but different bug) -- out
   of this chunk's failure class, so the underlying concurrency gap itself is queued, not fixed;
   only the now-inaccurate justification comment needed correcting.

Test mutation-sensitivity re-confirmed for all 3 round-4 tests (traced, not run) -- all genuinely
sensitive, with an explicit honest note: none of the tests in this file can detect removal of the
FENCES themselves (only the recheck logic they protect); fence correctness rests on the source-level
Dekker argument, not the suite. All previously-open nits re-confirmed accurate and still nits.

**Chunk 3a round 5 fix applied** (2026-08-20, commit `<pending>`). New `private int _pttKeyEpoch;`
field, incremented via `Interlocked.Increment` immediately after every successful
`SetPttAsync(true)` (both in `PlayWithPttAsync` and `SetPttLockAsync`). `UnkeyForCleanupAsync`
snapshots the epoch (`Volatile.Read`) BEFORE its own un-key attempt, and on success only performs
the state clears if the epoch is unchanged -- if it moved, a newer key command's state is left
standing instead (new Debug-level log `PttUnkeyRaceLostToNewerKey` marks the skip for
observability). Also corrected the Risk B comment to state the double-subscription gap explicitly
and stop claiming the duplicate-resume interleaving is unconditionally harmless.

New regression test `Round5_UnkeyForCleanupAsync_LostUpdateRace_DoesNotWipeAConcurrentNewerKey`:
uses the established `ThrowOnStartPlaybackAudioEngine` deterministic trigger (no real-time race) to
force a tune's abnormal-termination cleanup, and drives a concurrent `SetPttLockAsync(true)` call
from inside `FakeRadioSessionService.BeforeSetPtt` -- landing exactly in the epoch-snapshot-to-
recheck window. Mutation-verified by temporarily reverting to the unconditional clears -- failed
with the exact predicted signature ("a concurrent newer key must not be wiped"), then restored. All
179 `ScanlineStudio.Application.Tests` passing, full solution suite (all projects) clean.

Round 5 found 1 real fixed finding in the leaked-keyed-transmitter class -- round 5 does NOT count
as chunk 3a's 1st clean round. Round 6 (independent re-verification of round 5's fix, ideally
continuing the widened full-sweep approach given 4 consecutive rounds have now found something) is
the earliest round that can count as chunk 3a's 1st clean round.
