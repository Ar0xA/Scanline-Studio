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

## Tier A Batch 3 -- CLOSED (2026-08-21), started 2026-08-20

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

**Status (updated 2026-08-21)**: all 3 chunks CLOSED -- chunk 3a after 32 rounds, chunk 3b after 4
rounds, chunk 3c after 2 rounds (see each chunk's own "CLOSED" entry, and this batch's own "Tier A
Batch 3 -- CLOSED" summary near the end of this section). All three by explicit user decision on an
auditor go-for-production verdict, none under the formal 2-consecutive-clean-round gate.

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

**Chunk 3a round 5 fix applied** (2026-08-20, commit `cbeff77`). New `private int _pttKeyEpoch;`
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

**Chunk 3a round 6** (2026-08-20, independent re-verification agent, fresh context). Continued the
widened sweep, this time hunting for the BROADER "snapshot state, await, act on stale state" pattern
class (not just Dekker's memory-fence gaps) since round 5 revealed the bug class isn't limited to
memory-fence issues. **VERDICT: NOT CLEAN -- 3 real risk findings + 1 nit.** Still not chunk 3a's
first clean round; the rounds 2-5 pattern has not yet terminated.

Part B (round 5's `_pttKeyEpoch` fix) re-verified as correct: every `SetPttAsync(true)` call site
increments it (none missed), `Volatile.Read`/`Interlocked.Increment` is the right pairing for THIS
pattern (unlike rounds 2-4's Dekker's gaps, this one genuinely doesn't need a `MemoryBarrier` --
the auditor traced why precisely), wraparound/double-un-key are non-issues.

Part A's broader sweep found:
1. **`SetPttLockAsync`'s own unlock-path clears had the IDENTICAL lost-update shape round 5 fixed
   in `UnkeyForCleanupAsync`** -- round 5 fixed only that twin location and missed this one (the
   same fix-one-location-miss-the-twin repeat rounds 3→4 already produced in this same method).
   Leaked-keyed-transmitter class; the false-positive direction (spurious backstop/false Critical)
   is more reachable than the false-negative leak direction. Same zero-production-caller latency
   status as prior rounds' findings -- fixed anyway.
2. **Overlapping `PlayWithPttAsync` calls erase each other's blocker-3 registration**: the publish
   side (`Interlocked.Exchange`) unconditionally overwrites, while the clear side explicitly
   anticipated overlap as "pathological" -- but it isn't: `RadioStatusViewModel.TuneAsync` has no
   TX-in-progress `CanExecute` gate, so clicking Tune during a transmit produces two genuinely live
   calls in production. Auditor could not construct an actual leaked-transmitter OUTCOME from this
   (whichever call finishes first still physically un-keys, and the flags DisposeAsync checks still
   cover the skip-un-key cases) -- defense-in-depth erosion of the shutdown-wait mechanism itself,
   not a demonstrated leak. Fixed anyway: it's the mechanism blocker 3 exists to guarantee.
3. **The `_pttKeyEpoch` fix's own atomicity has two residual sub-gaps**: (a) avoidable -- the
   increment ran AFTER `_pttLocked = locked` instead of before, letting a concurrent un-keyer
   observe the lock flag before the epoch bump; (b) irreducible without a new lock -- an
   instruction-scale window between an un-keyer's epoch recheck and its clears. Fixed (a); (b)
   deliberately left open and honestly documented (matching Risk B's own precedent) rather than
   adding a new synchronization primitive for an already-astronomically-narrow residual.
4. **[nit]** The round-5 regression test only asserted `IsPttLocked`, not all three guarded clears
   -- a partial revert (guard `_pttLocked` only, leave the other two unconditional) would still pass
   green. Not separately re-tested this round (the new round-6 tests provide adjacent coverage for
   `_pttLeftKeyedByCall`'s own clear-guard via a different code path); logged, not exhaustively
   closed.

Re-confirmed NOT findings (traced clean): `pttLockedAtEntry`/`pttLockedAtCleanup` single-read
snapshots, `DisposeAsync`'s own multi-step chain (re-reads state after each await rather than
reusing stale locals), `wasReceiving` (RX-class only), `_rxPendingResumeAfterUnlock` (still
lower-severity, hasn't drifted into the PTT class). All previously-open nits re-confirmed accurate.

**Chunk 3a round 6 fix applied** (2026-08-20, commit `ce57521`). Finding 1: `SetPttLockAsync` now
snapshots the epoch before its own `SetPttAsync` await (same pattern `UnkeyForCleanupAsync` already
uses) and gates its unlock-path clears (`_pttLeftKeyedByCall`/`_pttUnkeyFailedOnRealRig`) on the
epoch being unchanged, logging `PttUnkeyRaceLostToNewerKey` otherwise -- exact same mechanism as
round 5, just carried to the second location. Finding 2: new `private int _keyedTransmitCount;`,
incremented alongside every `_keyedTransmitCompletion` publish and decremented alongside every
clear -- `DisposeAsync`'s "is anything still keyed" check now reads the count, not the field's
nullness, so it survives being overwritten by an overlapping call. `AwaitInFlightKeyedTransmitAsync`
itself still only waits on whichever TCS is most recently published (best-effort, unchanged) but the
OR-condition it feeds is now correct even when the wait has nothing to observe. Finding 3(a): the
epoch increment in `SetPttLockAsync` moved to run before `_pttLocked = locked`. Finding 3(b):
documented as an accepted, narrow, deliberately-unfenced residual in `_pttKeyEpoch`'s own doc
comment, matching Risk B's own honest-documentation precedent -- no new lock added. Also corrected
a stale "(pathological) overlapping PlayWithPttAsync" comment to state plainly this is a real
production interleaving.

New regression tests `Round6_SetPttLockAsync_Unlock_LostUpdateRace_DoesNotWipeAConcurrentNewerKey`
(finding 1, same technique as round 5's own test) and
`Round6_DisposeAsync_OverlappingTransmits_StillBackstopsWhenNewerCallClearsOlderCallsRegistration`
(finding 2 -- deterministically nests an entire second `TuneAsync` call, plus a mid-race
`DisposeAsync()` call, inside the first call's own un-key `BeforeSetPtt` hook; asserts exactly 5 PTT
commands land, since the fix restores DisposeAsync's own backstop attempt that the old
nullness-based check would have skipped entirely). Both mutation-verified by reverting each fix and
confirming the exact predicted failure signature, then restored. All 181
`ScanlineStudio.Application.Tests` passing, full solution suite (all projects) clean.

Round 6 found real fixed findings in the leaked-keyed-transmitter class -- round 6 does NOT count as
chunk 3a's 1st clean round. Round 7 (independent re-verification, continuing the widened full-sweep
approach -- 5 consecutive rounds have now found something) is the earliest round that can count as
chunk 3a's 1st clean round.

**Chunk 3a round 7** (2026-08-20, independent re-verification agent, fresh context). A genuinely
comprehensive, unscoped final sweep of the whole PTT lifecycle -- full state-machine re-derivation
(every field, every writer, every reader), every method's assumptions re-traced from scratch, hunt
for 3-way overlaps and combinations rounds 1-6 hadn't considered, cross-check of the 4 fix
mechanisms' own interactions. **VERDICT: NOT CLEAN -- 2 real findings + 1 nit.** Still zero
consecutive clean rounds after 6 rounds.

1. **[blocker-class, latent] `SetPttLockAsync(true)` records NOTHING when its own `SetPttAsync` call
   throws AFTER the rig may have already been physically keyed.** `PlayWithPttAsync` already handles
   this exact situation correctly (captures `pttKeyedOnRealRig = true` BEFORE the await, erring
   toward "assume keyed" on failure -- see its own comment: "erring true costs at most one spurious
   Warning; erring false is the blocker-2 silent swallow"), but `SetPttLockAsync` never adopted the
   same pattern -- its `_pttLocked = locked` write only happens AFTER a successful await, so a throw
   leaves every shutdown-backstop flag false. Confirmed reachable at the protocol layer, not
   theoretical: `RigctldClientProtocol.SendSetCommandAsync` writes the PTT command then READS the
   reply -- a read timeout/dropped connection after the write throws with the rig keyed;
   `HamlibRadioProtocol.SetPttAsync` can likewise throw after `RigSetPtt` asserted PTT. Same
   zero-production-caller latency status as every other `SetPttLockAsync` finding fixed this chunk --
   fixed anyway per this chunk's own established standard.
2. **[risk] The `_pttLeftKeyedByCall = pttKeyedOnRealRig` write in `PlayWithPttAsync`'s
   `leaveKeyedAfterCall` branch was the one remaining unguarded post-`await` write to a keyed-state
   flag** -- the third instance of the lost-update shape rounds 5/6 already closed at two other sites,
   just at a different write (not a stale-epoch read, a plain unconditional overwrite that could write
   `false` when THIS call's own key was skipped, over a CONCURRENT call's genuine `true`). Compound-
   latent (needs `leaveKeyedAfterTune: true` -- no production caller -- plus two overlapping such
   calls plus a RigId transition). Writing `false` here was never load-bearing -- the only correct
   owner of clearing this flag is a confirmed un-key, which the two epoch-guarded sites already
   handle.
3. **[nit]** Doc-comment drift: `PlayWithPttAsync`'s own doc comment claimed a cancellation/fault
   "ALWAYS un-keys PTT and force-clears the lock" -- true of the un-key COMMAND, no longer true of the
   CLEAR since round 5's epoch guard can deliberately skip it.

Re-derived and confirmed sound (not findings): `_pttLocked = locked`'s own write is correctly
unguarded (its only `true`-writer is `SetPttLockAsync` itself, serialized by `_pttLockGate`, so no
concurrent `true` exists to race); `_keyedTransmitCount`/`_pttKeyEpoch` don't need cross-protection
from each other or from the `_disposed` fence -- each field's own Dekker pair is independently sound
(traced precisely: the increment side is a full fence, the `_disposed`-fence side is a full fence,
so at least one side always observes the other, for every pairing); Risk B and round-6's finding-3(b)
residual are both still correctly classified, neither shifted by anything this round found. Both
spot-checked round-6 tests confirmed genuinely mutation-sensitive.

**Chunk 3a round 7 fix applied** (2026-08-20, commit `bdb9b00`). Finding 1: `SetPttLockAsync` now
captures `rigIsRealAtKeyTime` once, before the key command (the same blocker-2 rule
`PlayWithPttAsync` already follows), wraps the `SetPttAsync` call in a `try`/`catch (Exception) when
(rigIsRealAtKeyTime)`, and on that path sets `_pttLeftKeyedByCall = true` + logs a new Critical
(`PttKeyCommandFailedMayHaveKeyed`) before rethrowing -- `_pttLeftKeyedByCall` is reused as the
marker (its own doc comment widened to cover this second producer) rather than adding a new field.
Finding 2: the write now only ever sets `true`, guarded on `pttKeyedOnRealRig` -- never writes
`false` (that direction stays owned exclusively by the two epoch-guarded confirmed-un-key sites).
Finding 3 (nit): doc comment corrected to describe the un-key-command-vs-clear distinction
precisely.

New regression tests `Round7_SetPttLockAsync_KeyCommandThrows_StillRecordsPossiblyKeyedForBackstop`
(finding 1 -- simulates a `TimeoutException` from the key command itself, confirms the Critical log
and that `DisposeAsync`'s backstop still fires) and
`Round7_PlayWithPttAsync_LeaveKeyedWrite_NeverWritesFalseWhenThisCallDidNotKey` (finding 2 --
sequential, not concurrent: a real `leaveKeyedAfterTune:true` call establishes a genuine keyed state,
then a SECOND such call with no radio configured must not clear it). Both mutation-verified by
reverting each fix and confirming the exact predicted failure signature, then restored. All 183
`ScanlineStudio.Application.Tests` passing, full solution suite (all projects) clean.

Round 7 found real fixed findings in the leaked-keyed-transmitter class -- round 7 does NOT count as
chunk 3a's 1st clean round. Round 8 (independent re-verification) is the earliest round that can
count as chunk 3a's 1st clean round. Six consecutive rounds (2-7) have now each found something
real in this file.

**Chunk 3a round 8** (2026-08-20, independent re-verification agent, fresh context). Same rigor as
round 7: a systematic side-by-side comparison of `SetPttLockAsync` vs. `PlayWithPttAsync`'s full
hardening shape (given the recurring pattern "`SetPttLockAsync` missing something `PlayWithPttAsync`
already has, one spot at a time"), re-verification of round 7's specific fix, a fresh full
writer/reader re-derivation, and a brief check of UI-side assumptions. **VERDICT: NOT CLEAN -- 2
real findings (both blocker-class, both latent) + 4 nits.** Still zero consecutive clean rounds
after 7 rounds.

1. **[blocker-class, latent] Round 7's own new write was ITSELF a fourth instance of the lost-update
   shape** -- the catch block's `_pttLeftKeyedByCall = true` (a key command that threw after possibly
   physically keying the rig) never bumped `_pttKeyEpoch`, so a CONCURRENT `UnkeyForCleanupAsync` call
   whose own epoch snapshot predates this write could still wipe it moments later, believing nothing
   new happened -- silently reintroducing the exact leaked-keyed-transmitter outcome round 7 itself
   just closed. Confirmed the two code paths genuinely overlap (`PlayWithPttAsync`'s cleanup un-key is
   not serialized under `_pttLockGate`). Fixed by bumping the epoch before the flag write, matching
   round 6's own established ordering rule.
2. **[blocker-class, latent] `SetPttLockAsync`'s key command was invisible to `DisposeAsync`'s bounded
   shutdown WAIT** -- the systematic side-by-side comparison's main finding: `PlayWithPttAsync`
   publishes `_keyedTransmitCompletion`/`_keyedTransmitCount` BEFORE its key command specifically so
   "DisposeAsync can never tear IRadioSessionService down out from under an in-flight un-key" (its own
   comment) -- `SetPttLockAsync` never adopted this. Consequence: `DisposeAsync` could run to
   completion, dispose `IRadioSessionService`, and only THEN would `SetPttLockAsync`'s own post-await
   disposal-race recovery (rounds 3/4's fix) get a chance to run -- against a radio session that no
   longer exists. Same root cause covers an even worse variant: if `SetPttAsync` never returns at all
   (a real UI caller passing `CancellationToken.None`), `SetPttLockAsync` records NOTHING, ever. Fixed
   by mirroring `PlayWithPttAsync`'s exact mechanism: publish before the key command, clear in an
   outermost `finally` that wraps the whole method (restructured into a nested try/finally to make
   this correct now that both `_pttLockGate.Release()` and the new TCS cleanup need to run
   unconditionally).

Re-derived and confirmed sound (not findings): round 7's `catch (Exception) when (rigIsRealAtKeyTime)`
shape itself (filter correctness, exception identity preservation, no double-fire with the
`if (locked && _disposed)` block, the `try` block containing only the one awaited call so the filter
can't mis-catch anything else); round 7's finding-2 write integrates correctly downstream against all
3 readers. Both round-7 tests confirmed genuinely mutation-sensitive.

**Chunk 3a round 8 fix applied** (2026-08-20, commit `0d8c409`). Finding 1: the catch block now
bumps `_pttKeyEpoch` before setting `_pttLeftKeyedByCall = true` -- `_pttKeyEpoch`'s own doc comment
widened to state the invariant is "every key command that succeeded OR may have physically keyed the
rig before throwing," not just "every successful" one. Finding 2: `SetPttLockAsync` restructured
with a nested try/finally -- publishes a TCS + increments `_keyedTransmitCount` before the key
command (guarded on `rigIsRealAtKeyTime`, matching `PlayWithPttAsync`'s own shape exactly), clears/
`TrySetResult`s in the new outermost `finally` (CompareExchange-guarded, same pattern as
`PlayWithPttAsync`'s own inner finally). Re-indented the whole method for the added nesting level.

One pre-existing round-4 test's expectations needed updating as a direct, correct CONSEQUENCE of
finding 2's fix (not a regression): `Round4_SetPttLockAsync_DisposeRacesTheSetPttAsyncAwait_...`
nests `DisposeAsync` synchronously inside the SAME call's own key command -- now that the key command
is visible to `DisposeAsync`'s wait, that reentrant nesting makes the wait genuinely engage (and
always time out, since nothing can complete until the synchronous callback itself returns), so
`DisposeAsync`'s own backstop now ALSO fires an extra (redundant, documented-harmless) un-key before
the original call's own key even returns -- exactly the existing "whether the wait succeeds or times
out, DisposeAsync's own backstop un-key runs next either way" contract already established for
`PlayWithPttAsync`. `PttCalls` expectation updated from `[true, false]` to `[false, true, false]`
with an explanatory comment; the property under test (throws `ObjectDisposedException`, ends
correctly un-locked) is unchanged.

New regression tests: `Round8_SetPttLockAsync_KeyCommandThrows_EpochBump_SurvivesConcurrentUnkeyersStaleClear`
(finding 1, same nested-BeforeSetPtt technique as prior rounds) and
`Round8_DisposeAsync_WaitsForAnInFlightSetPttLockAsyncsOwnCompletion` (finding 2 -- required adding a
genuine async `Gate` hook to `FakeRadioSessionService`, mirroring `FakeAudioDeviceEnumerator.Gate`,
since the existing synchronous `BeforeSetPtt` hook can't create a real in-flight window without
reentrant nesting; mirrors the existing `Blocker3_DisposeAsync_WaitsForAnInFlightKeyedTransmitsOwnUnkey`
test's shape for `TuneAsync`, applied to `SetPttLockAsync`). Both mutation-verified by reverting each
fix and confirming the exact predicted failure signature, then restored. All 185
`ScanlineStudio.Application.Tests` passing, full solution suite (all projects) clean.

Round 8 found real fixed findings in the leaked-keyed-transmitter class -- round 8 does NOT count as
chunk 3a's 1st clean round. Round 9 (independent re-verification) is the earliest round that can
count as chunk 3a's 1st clean round. Seven consecutive rounds (2-8) have now each found something
real in this file.

**Chunk 3a round 9** (2026-08-20, independent re-verification agent, fresh context). Primary task:
independently verify round 8's own claim that `SetPttLockAsync` "mirrors `PlayWithPttAsync`'s exact
mechanism" -- a full 18-row, technique-by-technique symmetry table built from current source (not
prior narrative). **VERDICT: NOT CLEAN -- 1 real risk finding + 3 nits.** Still zero consecutive
clean rounds after 8 rounds.

The symmetry table found round 8's claim **mostly** true (16 of 18 rows match, and every asymmetry
except one was traced and confirmed deliberate/justified -- e.g. the pre-await `_disposed` guard
being engage-only is correct because unlock must stay an escape hatch; the epoch-bump-on-failure
asymmetry is correct because `PlayWithPttAsync`'s failed key falls into its own immediate
`abnormalTermination` un-key, leaving nothing for a concurrent un-keyer to wipe). **One row was a
real gap**:

1. **[risk] `SetPttLockAsync` had no publish-then-recheck before its key command -- `PlayWithPttAsync`
   does.** `PlayWithPttAsync` publishes its shutdown-wait registration and IMMEDIATELY rechecks
   `_disposed` before ever issuing its key command (preventing the key from being issued at all once
   disposal has started); `SetPttLockAsync` published the registration (round 8's fix) but went
   straight to the key command, with its only `_disposed` recheck AFTER the rig was already keyed (the
   existing round-3/4 recovery block). Consequence: DisposeAsync could read the registration as empty,
   find nothing to do, and return -- then `SetPttLockAsync` publishes and keys anyway, self-detects via
   the existing recovery, but that recovery then races `IRadioSessionService`'s own concurrent DI
   teardown. Window is instruction-scale (no `await` between the publish and the key command), and
   self-detecting/loud rather than silent -- hence risk, not blocker -- but a real, closable gap that
   qualifies round 8's "exact mechanism" claim: the publish/clear/refcount machinery was mirrored: the
   *prevention* step was not.

Also 3 nits: the unlock direction never registers with the shutdown-wait mechanism at all (traced as
benign -- whenever an unlock is meaningful, some other flag is already true, so `DisposeAsync`'s
backstop still fires); the round-8 `Round8_DisposeAsync_WaitsForAnInFlightSetPttLockAsyncsOwnCompletion`
test's mutation-sensitivity rested on a `Task.Delay`-based timing assertion that could theoretically
false-pass on a heavily loaded runner; the new `FakeRadioSessionService.Gate` infrastructure had a
stale doc comment on `BeforeSetPtt` plus two latent (not currently triggered) test-infra footguns for
future tests (`Gate` applies to every `SetPttAsync` call including un-keys; `PttCalls` is a
non-thread-safe `List<bool>`).

Re-verified round 8's own two fixes from scratch: the epoch bump precedes the flag write correctly;
the nested try/finally's cleanup guarantees hold in every traced case (gate release can't throw
reachably; the `ObjectDisposedException` recovery path still signals the TCS correctly; a call that
never published skips the clear cleanly; `_pttLockGate`'s own serialization is unchanged by the
restructure). No new instance of any prior bug class (Dekker's gap / lost-update / overlap-erosion)
found in round 8's own new code.

**Chunk 3a round 9 fix applied** (2026-08-20, commit `f6d4fca`). Finding 1: `SetPttLockAsync` now
rechecks `_disposed` (via `ObjectDisposedException.ThrowIf`) immediately after publishing the
registration, before the key command -- 3 lines, strictly additive, matching `PlayWithPttAsync`'s
own already-tested pattern exactly. No dedicated regression test for the exact instruction-scale
window: unlike every prior round's finding, this window has no natural async boundary a test hook
can land on (the publish and the new recheck are two adjacent synchronous statements with no
`await` between them) -- constructing one would require adding a production-code-only test seam
between two adjacent lines, which this project avoids. Judged low-risk to leave untested given the
fix is a straightforward guard mirroring an already-covered pattern (matching round 6's own
precedent for its similarly-narrow, accepted residual). Also fixed nit 2 (test-quality): rewrote
`Round8_DisposeAsync_WaitsForAnInFlightSetPttLockAsyncsOwnCompletion` to drop the `Task.Delay`
entirely -- in this exact test scenario, without the fix `DisposeAsync` completes fully
synchronously (nothing it does after the empty-registration check ever awaits an incomplete Task),
so checking `IsCompleted` immediately is deterministic, not a race; re-verified this catches the
original finding-2 mutation just as reliably (confirmed via a fresh mutation pass). Also fixed nit
3a (the `BeforeSetPtt` doc-comment drift). Nits 1 and 3b/3c left queued, not fixed (benign/
speculative-for-future-tests respectively).

Full `ScanlineStudio.Application.Tests` 185/185 passing (no new test added; existing round-8 test
strengthened in place), full solution suite (all projects) clean.

Round 9 found a real fixed finding in the leaked-keyed-transmitter class -- round 9 does NOT count
as chunk 3a's 1st clean round. Round 10 (independent re-verification) is the earliest round that can
count as chunk 3a's 1st clean round. Eight consecutive rounds (2-9) have now each found something
real in this file.

**Chunk 3a round 10** (2026-08-20, independent re-verification agent, fresh context). Re-verified
round 9's fix from scratch (fence correctness confirmed, mutual exclusion with the existing recovery
block confirmed, not a 5th lost-update/3rd Dekker's-gap instance), then rebuilt the
`SetPttLockAsync`/`PlayWithPttAsync` symmetry table one more time -- **now 18/18, genuinely
exhausted, no keyed-transmitter leak constructible against current code.** **VERDICT: NOT CLEAN --
1 real risk finding + 1 nit, but for the first time in this chunk, the new finding is OUTSIDE the
leaked-keyed-transmitter failure class** that dominated rounds 1-9. Still zero consecutive clean
rounds after 9 rounds.

1. **[risk] `_pttLockGate` held across an unbounded RX resume, stranding the PTT lock/unlock escape
   hatch itself.** `SetPttLockAsync`'s deferred-RX-resume-after-unlock step ran under the caller's
   own `ct` (default `CancellationToken.None`, unbounded) instead of a fresh CTS -- unlike
   `PlayWithPttAsync`'s own equivalent, which always uses a fresh `rxResumeCts`. A wedged capture
   device (device enumeration, settings I/O, or `StartCaptureAsync` itself hanging) could park this
   call inside `_pttLockGate` indefinitely -- and since `_pttLockGate` serializes EVERY
   `SetPttLockAsync` call, that includes a future emergency unlock, stranding the one API this whole
   method exists to keep working. Confirmed NOT the leaked-keyed-transmitter class: the resume only
   runs on `!locked`, after the un-key already succeeded, so the rig is provably off whenever this
   runs; `DisposeAsync` and `PlayWithPttAsync`'s own un-key never touch this gate, so both safety
   backstops still work regardless. This finding directly resolves the already-tracked "unlock's RX
   resume uses caller's ct" nit -- same line, same fix, not a separate change.
2. **[nit]** Round 9's own fix has no dedicated regression test -- correctly so (the window has no
   `await` between the publish and the recheck, so no test hook can land there), but the round-9
   playbook entry/commit already recorded this rationale explicitly; this round's audit confirms
   that disposition is still the right one, not silence.

Confirmed sound (not findings): `TryUnkeyPttAsync`/`UnkeyForCleanupAsync`/`StopPlaybackWithWatchdogAsync`/
`AwaitInFlightKeyedTransmitAsync`/`DisposeAsync`'s full sequence/`StartReceivingAsync`/
`StopReceivingAsync` all re-derived clean; the 4 fix mechanisms (`_disposed`+fences, `_pttKeyEpoch`,
`_keyedTransmitCompletion`+`_keyedTransmitCount`) interact correctly as a whole system; round 9's own
new code (`ObjectDisposedException.ThrowIf`) writes no tracked field and can't participate in the
lost-update/Dekker's-gap/overlap-erosion bug classes; the round-9-modified test
(`Round8_DisposeAsync_WaitsForAnInFlightSetPttLockAsyncsOwnCompletion`, now using an immediate
`IsCompleted` check) is deterministic in BOTH directions -- traced precisely why zero-delay can't
false-read `true` even with the fix present (nothing signals the wait until the gate is released).

**Chunk 3a round 10 fix applied** (2026-08-20, commit `40b1c73`). `SetPttLockAsync`'s
deferred-RX-resume step now uses a fresh `CancellationTokenSource(_cleanupTimeout)`, matching
`PlayWithPttAsync`'s own `rxResumeCts` pattern exactly. Required updating
`FakeAudioDeviceEnumerator.RefreshAsync`'s test infra to actually respect its `ct` parameter (via
`Task.WaitAsync(ct)`, not a plain `await`) so a test can prove the bound takes effect -- previously
the fake ignored cancellation entirely. New regression test
`Round10_SetPttLockAsync_Unlock_RxResumeBounded_DoesNotStrandTheLockGate`: engages the lock during a
transmit (deferring the RX resume), wedges device enumeration permanently, and confirms the unlock
still completes within the bounded budget AND that a subsequent lock call isn't stuck behind the
stranded resume. Mutation-verified by reverting to the caller's `ct` -- the test **hung** (not just
failed an assertion) exactly as predicted, the strongest possible confirmation of a genuine
unbounded-wait bug, then restored. All 186 `ScanlineStudio.Application.Tests` passing, full solution
suite (all projects) clean.

Round 10 found a real fixed finding, but for the first time in this chunk it was OUTSIDE the
leaked-keyed-transmitter failure class -- the `SetPttLockAsync`/`PlayWithPttAsync` symmetry
comparison that dominated rounds 3-9 is now genuinely exhausted. Round 10 does NOT count as chunk
3a's 1st clean round (a real fix still landed this round). Round 11 (independent re-verification) is
the earliest round that can count as chunk 3a's 1st clean round. Nine consecutive rounds (2-10) have
now each found something real in this file -- but round 10's own verdict is the first explicit signal
that the CORE safety property (no leaked keyed transmitter) may have actually converged.

**Chunk 3a round 11** (2026-08-21, independent re-verification agent, fresh context). A completely
fresh, ground-up sweep of the whole PTT lifecycle -- not scoped to the now-exhausted
`SetPttLockAsync`/`PlayWithPttAsync` symmetry comparison, and not scoped to any single prior round's
bug pattern. **VERDICT: NOT CLEAN -- 1 real risk (back in the leaked-keyed-transmitter class,
proving that comparison's exhaustion doesn't mean the whole failure class is exhausted) + 3 nits.**
Still zero consecutive clean rounds after 10 rounds.

1. **[risk] `SetPttLockAsync`'s key-command-failure catch (round 7's fix) records "may be keyed" but
   never attempts a recovery un-key -- `PlayWithPttAsync` always does.** Consequence: the failure is
   recorded for `DisposeAsync`'s eventual shutdown backstop and a Critical log, but the rig can sit
   physically keyed for the ENTIRE remaining process lifetime (hours) unless something else happens
   to touch PTT first. **Deliberately NOT auto-fixed this round** -- the auditor's own analysis (and
   independent re-confirmation) found that adding an immediate recovery un-key here would introduce
   a NEW instance of this exact method's own pre-existing, explicitly "Known, accepted race": a
   concurrent, genuinely on-air `PlayWithPttAsync` transmission could have its carrier dropped
   mid-frame by this call's own recovery un-key, since `_pttKeyEpoch`'s guard protects the FLAG
   CLEARS other calls perform, not the un-key COMMAND itself -- there is no shared gate between
   `SetPttLockAsync` and `PlayWithPttAsync`'s own key/un-key, by the same deliberate design choice
   already documented on `_pttLockGate` itself (closing it fully needs a shared-gate redesign that
   would also make an emergency unlock wait behind an in-flight transmission's own gate hold -- a
   worse safety property for what unlock is meant to be, an escape hatch). Same zero-production-
   caller latency status as every other `SetPttLockAsync` finding fixed this chunk, but this is the
   first one whose "obvious" fix is not actually safe -- a genuine, considered engineering tradeoff,
   not corner-cutting. Documented explicitly in both the class-level `_pttLockGate` doc comment (a
   new "second producer" paragraph) and the catch block itself, matching this method's own existing
   precedent for how it records accepted-but-unfixed races rather than leaving them silently
   implicit. Revisit if/when a real caller actually needs this closed -- same disposition as the
   sibling race.
2. **[nit, severity correction to an existing comment, not new code]** The `StartReceivingAsync`
   double-subscription gap (already tracked, out-of-class) is NOT recoverable by toggling RX,
   contrary to what an earlier comment implied -- `StopReceivingAsync`'s `-=` removes only one copy
   of each duplicated handler, so the extra subscription survives a Stop RX/Start RX cycle and keeps
   doubling every captured chunk into the decoder for the rest of the process's life. Comment
   corrected; the out-of-class judgment itself still holds (RX corruption, not leaked PTT).
3. **[nit]** `StopReceivingAsync` leaves `_isReceiving == true` with handlers already detached if
   `StopCaptureAsync` throws -- correct today only because `MiniAudioEngine.StopCaptureAsync` doesn't
   realistically throw; an `IAudioEngine`-contract fragility, not a live bug. Not fixed.
4. **[nit]** `RaiseCapturePausedForTransmitChanged` is invoked while `_pttLockGate` is held -- the
   residual sibling of round 10's own finding (round 10 bounded the async step inside the gate; an
   arbitrary synchronous subscriber callback is still inside it with no timeout possible). Harmless
   today (the sole production subscriber does a non-blocking `Dispatcher.UIThread.Post`). Not fixed.

Also fixed a test-quality issue surfaced this round: the round-10 regression test's mutation
manifests as a HANG, not a clean failure (no timeout guard), so a future regression would wedge the
whole test run rather than failing loudly. Wrapped both bounded awaits in `.WaitAsync(TimeSpan)`;
re-verified via a fresh mutation pass that it now fails in ~5s with a clean `TimeoutException`
instead of hanging.

Re-verified round 10's fix from scratch as correct (CTS scoping, `ct` usage split between the outer
gate/key-command and the RX-resume step, `FakeAudioDeviceEnumerator`'s new cancellation behavior
doesn't disturb the one other test using that hook). Re-derived every known nit from current source
(not rubber-stamped) -- all still accurately classified. Re-checked `DisposeAsync`'s full teardown
budget arithmetic: still 8s worst case (3s wait + 5s backstop) against `Program.cs`'s 10s bound;
nothing added across rounds 5-10 introduced a new sequential wait on that path. Disproved two
candidate findings during the sweep (a `StopReceivingAsync`/drain-thread self-join deadlock that
turned out not to be one; a real backend assumption that checked out against actual source).

**Chunk 3a round 11 fix applied** (2026-08-21, commit `b0a028b`). Finding 1: documented as an
explicit, considered accepted risk (not code-fixed) -- new prose in `_pttLockGate`'s own doc comment
and the round-7/11 catch block, both cross-referencing the existing sibling race. Findings 2: comment
corrected. Findings 3-4: logged, not fixed (nits). Test-quality issue: `WaitAsync(TimeSpan)` guards
added to the round-10 test. No new production-code regression tests this round (finding 1 is a
documentation decision, not a code change with new behavior to cover). Full
`ScanlineStudio.Application.Tests` 186/186 passing, full solution suite (all projects) clean.

Round 11 found and disposed of one real risk (documented as accepted, matching this method's own
established precedent for genuinely intractable-without-a-larger-redesign races) plus fixed 2 lower-
severity items. Round 11 does NOT count as chunk 3a's 1st clean round -- a real (if
documentation-resolved) finding still landed. Round 12 (independent re-verification, confirming the
finding-1 disposition holds and continuing the fresh full-lifecycle sweep) is the earliest round that
can count as chunk 3a's 1st clean round. Ten consecutive rounds (2-11) have now each found something
real in this file.

**Chunk 3a round 12** (2026-08-21, independent re-verification agent, fresh context). Asked
specifically to verify round 11's accepted-risk disposition AND to consider whether a safer
conditional fix exists (using `_keyedTransmitCount`) rather than accepting the round-11 framing at
face value. **VERDICT: NOT CLEAN -- round 11's disposition should CHANGE (a genuinely safe guarded
fix exists) + 1 NEW real risk finding (the first in this chunk that is NOT about a leaked keyed
transmitter -- it's about an unrelated live transmission getting destroyed) + 1 test nit.** Still
zero consecutive clean rounds after 11 rounds.

1. **[disposition change] Round 11's "deliberately not fixed" reasoning was wrong on two counts.**
   First, `PlayWithPttAsync`'s own finally ALREADY accepts the identical harm today, unguarded, on a
   MORE reachable path (two overlapping `PlayWithPttAsync` calls) -- so a recovery here isn't a new
   risk class, just a second place accepting the same one. Second, and more useful: a genuinely SAFE
   guarded recovery is available. `_keyedTransmitCount` was already incremented for this call at the
   publish just above the catch, so it reads exactly 1 if and only if nothing else currently holds a
   registration. Checking `== 1` (not `== 0`, which would be dead code -- this call's own increment
   already happened) before attempting the recovery makes the skip path byte-for-byte identical to
   round 11's behavior (zero regression on the concurrent case), while the recovery path is strictly
   safe when nothing else is registered. **Fixed**, not just documented -- see below.
2. **[risk, NEW] Tune-during-Transmit un-keys the in-flight transmission and tears down its playback
   session.** The first finding in this chunk NOT about a leaked keyed transmitter -- PTT correctly
   ends up OFF, but a genuinely in-flight, correctly-behaving transmission gets silently killed by an
   unrelated second call. Confirmed production-reachable: `RadioStatusViewModel`'s Tune command has
   no `CanExecute` gate, so clicking Tune during a `TransmitAsync` re-keys, `StartPlaybackAsync`
   throws "already started" against the FIRST call's own live session (verified against the real
   `MiniAudioEngine`), the generic catch classifies `abnormalTermination`, and the finally's urgent
   un-key fires immediately -- dropping the FIRST call's carrier mid-frame -- followed by disposing
   its playback session out from under it. Round 6 examined this exact interleaving but only checked
   for *leak* potential, never the *premature-un-key* direction. **Fixed** with a single-flight guard.
3. **[nit, severity note]** `StopReceivingAsync`'s throw-leaves-`_isReceiving`-stuck-true issue
   (already tracked as a nit) was re-flagged as worth upgrading: the failure is invisible --
   `StartReceivingAsync` silently early-returns forever afterward, with no error surfaced anywhere.
   **Fixed** (cheap, one `finally`).
4. **[test nit]** The round-10 test's `.WaitAsync(5s)` wrapper made one of its own assertions
   (`stopwatch.Elapsed < 5s`) permanently unable to fail. **Fixed** (tightened to the actual budget).

Re-derived and confirmed clean (not findings): the `OperationCanceledException`-caught-by-round-7's-
filter behavior is the CORRECT conservative choice, not a bug; `OnDecoderRestartCriticallyOverdue`'s
synchronous drain-thread call does not self-join (the capture session has an explicit guard);
`SetPttLockAsync`'s unlock direction never publishing a registration is covered by `DisposeAsync`'s
`_pttLocked` backstop; gate acquire/release balance across every throw path in `SetPttLockAsync` is
correct. `DisposeAsync`'s full teardown budget re-confirmed unaffected by anything since round 5.

**Chunk 3a round 12 fix applied** (2026-08-21, commit `eb4f0a4`). Finding-1 disposition change:
`SetPttLockAsync`'s catch now attempts the recovery un-key when `Volatile.Read(ref
_keyedTransmitCount) == 1`, matching `UnkeyForCleanupAsync`'s epoch-based clears (unchanged,
correctly still suppressed if a genuinely newer key raced it). Both the class-level `_pttLockGate`
doc comment and the catch block's own comment rewritten to describe the corrected reasoning.
Finding 2: new `private int _transmitInFlight;` field, `Interlocked.CompareExchange`-gated at
`PlayWithPttAsync`'s very entry (before RX pause, device resolution, or PTT is ever touched) --
rejects a second overlapping call with a clear `InvalidOperationException` instead of letting it
silently interfere; released unconditionally in a new outermost `finally` wrapping the entire
existing method body (required re-indenting the whole ~300-line method, same mechanical approach as
round 8's `SetPttLockAsync` restructure). Deliberately a SEPARATE field from `_keyedTransmitCount`
(that one is also incremented by `SetPttLockAsync`, a different narrower concern, and isn't
incremented at all when `RigId == "none"` -- the no-radio case still needs this same playback-session
exclusivity). Finding 3: `StopReceivingAsync`'s `_isReceiving = false` moved into a `finally` around
the `StopCaptureAsync` call. Finding 4: test assertion tightened.

Two PRE-EXISTING tests needed updating as direct, correct consequences of the finding-1/finding-2
fixes (not regressions): `Round6_DisposeAsync_OverlappingTransmits_...` previously relied on
constructing genuine `PlayWithPttAsync`-vs-`PlayWithPttAsync` overlap, which finding 2 makes
impossible -- rewritten to verify the overlap is now rejected outright instead of surviving an
overwrite. `Round7_SetPttLockAsync_KeyCommandThrows_...` previously asserted no un-key happened
until `DisposeAsync`'s eventual backstop -- now the guarded recovery fires immediately, so updated
to assert that and confirm `DisposeAsync`'s own backstop then finds nothing left to do.

New regression tests: `Round12_PlayWithPttAsync_RejectsOverlappingCall_BeforeTouchingPttOrPlayback`
(finding 2, direct) and `Round12_SetPttLockAsync_KeyCommandThrows_RecoverySkippedWhenAnotherTransmitIsRegistered`
(finding 1's SAFETY property -- confirms the guard correctly SKIPS recovery when a genuine
concurrent registration exists, not just that it fires when alone). Both findings' fixes
mutation-verified: finding 1 by reverting to no-recovery-at-all and confirming the exact predicted
failure signature; finding 2 by disabling the guard and observing the mutated test spin into
unbounded recursion (the test's own nested-call design has no depth limit once nothing rejects the
second call) -- a qualitatively stronger confirmation than a clean assertion failure, though messier
to run (the process had to be force-stopped rather than completing). No dedicated test added for
finding 3 (`StopReceivingAsync`) -- would need new decorator infrastructure for a
`StopCaptureAsync`-throws scenario that doesn't exist yet; judged low-risk to skip given the fix's
own simplicity (a one-line `finally` reordering) and this round's already-large scope. All 188
`ScanlineStudio.Application.Tests` passing, full solution suite (all projects) clean.

Round 12 both changed an existing disposition AND found a new, differently-classed real risk --
round 12 does NOT count as chunk 3a's 1st clean round. Round 13 (independent re-verification) is the
earliest round that can count as chunk 3a's 1st clean round. Eleven consecutive rounds (2-12) have
now each found something real in this file.

**Chunk 3a round 13** (2026-08-21, independent re-verification agent, fresh context, task
`a2a49050f31937155`). Re-derived round 12's two fixes from current source and confirmed both
CORRECT on every axis checked: the guarded recovery's `_keyedTransmitCount == 1` read timing, its
non-interaction with the epoch mechanism, the single-flight guard's acquire/release balance across
every exit path (including the re-indented ~300-line body, verified brace-by-brace), and
`DisposeAsync`'s interaction with both. Continuing the fresh sweep, found ONE new real risk, a
THIRD distinct failure class beyond "leaked PTT" and "transmission destroyed": `EnqueueAllAsync`'s
own "buffer full, wait 10ms, retry" loop has no bound -- a wedged playback device
(`EnqueuePlaybackSamples` persistently returning 0) stalls the sample-pump loop forever. This is
NOT a new instance of the leaked-keyed-transmitter class by itself (PTT stays keyed only as long as
the stall lasts, same as any other slow step) -- but round 12's own `_transmitInFlight` single-flight
guard turns it into something worse: since nothing throws, the guard is never released, so a single
wedged device permanently locks out every future transmit/tune for the life of the process. An
amplification of round 12's own fix, not a flaw in it. Secondary, smaller-window instance flagged
but not required: `StartPlaybackAsync`'s own await has the same class of unbounded wait on the
`ct == None` Tune path; deliberately deferred (see below).

**Chunk 3a round 13 fix applied** (2026-08-21, commit `1eef3d3`). New `private static readonly TimeSpan
PlaybackStallTimeout = TimeSpan.FromSeconds(5);` constant plus a `_playbackStallTimeout` instance
field (threaded through both constructors; the internal test constructor gained a
`playbackStallTimeoutForTests` parameter, matching the existing budget-injection pattern for
`cleanupTimeout`/`playbackStopWaitBudget`/`inFlightKeyedTransmitWait`). `EnqueueAllAsync` now tracks
elapsed stall time via `Environment.TickCount64` (chosen to avoid a new `System.Diagnostics` using)
across consecutive zero-accepted retries, resetting on any real progress, and throws
`TimeoutException` once the stall exceeds `_playbackStallTimeout`. This routes through
`PlayWithPttAsync`'s existing generic `catch (Exception)` -> `abnormalTermination = true` -> urgent
un-key BEFORE `StopPlayback` path -- already-bounded, already-tested machinery from earlier rounds,
so no new cleanup logic was needed, only a way to stop waiting. **Deliberately deferred, not fixed
this round:** `StartPlaybackAsync`'s own smaller-window unbounded wait on the same Tune path --
given this round's already-substantial scope, judged safe to defer since it doesn't carry the same
"permanent process-wide lockout" severity as the `EnqueueAllAsync` case (a device wedged during
`StartPlaybackAsync` still eventually surfaces as an ordinary unbounded-await risk, not amplified by
the single-flight guard into total lockout the way a wedge in the pump loop is). Flag for a future
round.

New regression test: `Round13_EnqueueAllAsync_WedgedPlaybackDevice_TimesOutRatherThanStallingForever`,
using a new `WedgedPlaybackAudioEngine` test decorator (`EnqueuePlaybackSamples` always returns 0)
matching the file's existing `ThrowOnStartPlaybackAudioEngine`/`GatedStopPlaybackAudioEngine`
decorator pattern. Asserts: `TransmitAsync` throws `TimeoutException` mentioning "wedged"; PTT went
on then urgently back off (`[true, false]`, never left keyed by the stall); the failure logs at
Error; and a second `TransmitAsync` call afterward reaches the same wedge again (proving
`_transmitInFlight` was genuinely released, not left stuck by the aborted call). Mutation-verified
by commenting out the timeout throw (constant-boolean conditionals trigger CS0162/CS1718 under this
project's warnings-as-errors build, per the established mutation-testing technique) -- the mutated
test **hung outright** (bounded by a 20s external `timeout` wrapper) rather than failing an
assertion, confirming the exact predicted unbounded-stall failure mode. Restored, rebuilt clean,
re-ran the test to confirm it passes again, confirmed no stray test-host processes survived. All 189
`ScanlineStudio.Application.Tests` passing (188 pre-existing + 1 new), full solution suite (all
projects) clean.

Round 13 re-verified round 12's fixes as sound but found a new real risk (an amplification of round
12's own fix) -- round 13 does NOT count as chunk 3a's 1st clean round. Round 14 is now the earliest
round that can count as chunk 3a's 1st clean round. Twelve consecutive rounds (2-13) have now each
found something real in this file.

**Chunk 3a round 14** (2026-08-21, independent agent, fresh context, agent `afad1729e3b721b71`).
Re-derived round 13's `EnqueueAllAsync` fix and confirmed it correct on every axis checked (stall
tracking, `TickCount64` clock choice, propagation through the generic catch, `_transmitInFlight`
release on the new path). Evaluated round 13's own deferred item (`StartPlaybackAsync`'s smaller-
window unbounded wait) and found round 13's severity call WRONG: it carries the identical
leaked-keyed-transmitter exposure AND the identical permanent-lockout amplification, not a smaller
variant. Continuing a genuinely fresh unscoped sweep of the whole file, found: **(1) [blocker]**
`_radioSession.SetPttAsync(true, ct)` (the key command itself) has no bound of its own --
`RigctldClientProtocol` bounds only its initial connect, not the per-command reply read, so a
half-open CAT connection blocks this forever WHILE THE COMMAND HAS ALREADY PHYSICALLY KEYED THE RIG.
**(2) [blocker]** `_audioEngine.StartPlaybackAsync`, one step later, same shape, same severity (PTT
already keyed by this point) -- confirms round 13's deferral was the wrong call. **(3) [risk]**
structural: three MORE unbounded awaits remain inside the `_transmitInFlight`-guarded region
(`StopReceivingAsync`, `ResolveDeviceAsync`/device enumeration, `GetTxVolumePercentAsync`/
`LoadAudioSettingsAsync`) -- these run BEFORE the key command, so NOT the leaked-keyed-transmitter
class, but still cause the same permanent process-wide lockout via `_transmitInFlight` never
releasing. **(4) [risk]** `StopPlaybackWithWatchdogAsync`'s own doc comment is factually wrong --
verified against `MiniAudioEngine.cs` directly: `StopPlaybackAsync` nulls its session field at CLAIM
time (synchronously, before the actual drain/dispose that can block), so an immediately-following
transmit does NOT fail loudly against a still-wedged device as the comment claimed -- it opens a
SECOND native session concurrently instead. **(5)/(6)/(9) [nits]**: the stall-timeout doc comment
overclaimed its own scope (catches only a fully-wedged device, not a slow trickle); two
`Task.WhenAny`+uncancelled-`Task.Delay` timer leaks; two XML doc comments misattached to the wrong
member (`SetPttLockAsync`'s 53-line doc comment was on the `_pttLockGate` field, `PlayWithPttAsync`'s
was on the `CleanupTimeout` constant). **(7) [risk, deferred]**: `DisposeAsync` housekeeping gaps
(`_pttLockGate` never disposed -- undocumented until this round, `ISstvDecoderMaintenance`
subscriptions never unsubscribed, no re-entrancy guard) -- not fixed this round beyond documenting
the `_pttLockGate` non-dispose as deliberate. **(8)**: re-derived the already-tracked
`StartReceivingAsync` double-subscription risk from scratch, confirmed still open and still correctly
characterized as a different (RX corruption, not PTT) class -- no new action.

**Chunk 3a round 14 fixes applied** (2026-08-21, commit `ec95ab0`). Findings 1/2: both unbounded awaits now wrapped
with `.WaitAsync(_cleanupTimeout)` (NOT a fresh standalone `CancellationTokenSource`, unlike
`UnkeyForCleanupAsync`'s own pattern, deliberately -- `Task.WaitAsync(TimeSpan)` preserves a genuine
caller cancellation of `ct` as `OperationCanceledException` (the benign, Information-logged arm),
while only an exceeded budget with no cancellation surfaces as `TimeoutException` (the generic catch
-> Error log -> abnormalTermination -> urgent un-key path) -- matching the exact log-level
distinction round 13's `EnqueueAllAsync` fix was designed around. The underlying call is left running
in the background either way, same accepted trade-off as round 13's own fix. Finding 3 (the 3 pre-key
unbounded awaits): explicitly NOT fixed this round -- genuinely lower severity than findings 1/2 (no
physical-transmitter exposure, only an availability/lockout bug), a materially different judgment
than round 13's mistake (which deferred an item that WAS in the safety-critical class). Flagged for a
future round. Finding 4: doc comment corrected to describe the actual behavior and flag the residual
concurrent-double-open risk as out of this chunk's scope (belongs in `MiniAudioEngine`, a different
project). Findings 5/6/9: all fixed (comment tightened; both timer leaks closed with a `using`
`CancellationTokenSource` cancelled on the winning-branch path; both doc comments moved to their
correct member, with a short one-line replacement left on the field/constant they'd been squatting
on -- incidentally also documents finding 7's `_pttLockGate` non-dispose as deliberate). Finding 7
(unsubscribe/re-entrancy guard): not fixed, logged as a queued nit.

New regression tests: `Round14_PlayWithPttAsync_KeyCommandHangs_TimesOutAndStillUnkeysRatherThanLeavingPttKeyedForever`
(finding 1) and `Round14_StartPlaybackAsync_Hangs_TimesOutAndStillUnkeysRatherThanLeavingPttKeyedForever`
(finding 2). Required two new test-infrastructure additions: `FakeRadioSessionService.HangOnCallNumber`
(hangs one specific 1-based call number forever, distinct from the existing `Gate` which hangs EVERY
call equally and so cannot let a test observe a LATER cleanup un-key call succeed) and
`GatedStartPlaybackAudioEngine` (mirrors the existing `GatedStopPlaybackAudioEngine`, but only gates
its OWN first call -- an abandoned, permanently-parked first call must not later resume and collide
with a second, deliberately un-gated call a test needs to observe succeeding). Both tests mutation-
verified: removing each `.WaitAsync(_cleanupTimeout)` in turn and re-running just that test produced
an outright hang under a 20s external `timeout` bound both times, confirming the exact predicted
unbounded-wait failure mode. Restored, rebuilt clean, both tests re-confirmed passing, no stray
test-host processes survived either mutation. All 191 `ScanlineStudio.Application.Tests` passing (189
pre-existing + 2 new), full solution suite (all projects) clean.

Round 14 fixed 2 new blockers in the leaked-keyed-transmitter class (an amplification/correction of
round 13's own severity call, the same pattern as round 13 was itself an amplification of round 12's)
-- round 14 does NOT count as chunk 3a's 1st clean round. Round 15 is now the earliest round that can
count as chunk 3a's 1st clean round. Thirteen consecutive rounds (2-14) have now each found something
real in this file -- finding 3 (the 3 remaining pre-key unbounded awaits) is a known, deliberately
deferred item for round 15 or later to pick up.

**Chunk 3a round 15** (2026-08-21, independent agent, fresh context, agent `a6635b5e4576e96c6`).
Re-derived round 14's two `.WaitAsync(_cleanupTimeout)` fixes and confirmed both correct (genuinely
bounded regardless of callee behavior, `TimeoutException` routes cleanly through the generic catch
with no special-casing, a genuine caller `ct` cancellation still surfaces as `OperationCanceledException`,
`_transmitInFlight` releases on both paths). Also independently confirmed round 14's CHOICE of
`WaitAsync` over a fresh `CancellationTokenSource` was correct for a reason round 14 itself didn't
state: a fresh linked CTS on the key command would cancel at the protocol semaphore gate, making a
timed-out key indistinguishable from an operator Stop TX at the catch site -- `WaitAsync` keeps them
distinct. Evaluated round 14's own deferred finding 3 (the 3 pre-key unbounded awaits) and found round
14's severity call correct on the PTT axis (no physical-transmitter exposure) but recommended fixing
NOW anyway: the actual consequence is not "TX unavailable" but **TX and RX both dead for the process
lifetime** (RX was already paused above these awaits; nothing resumes it on this path), from a single
wedged device enumeration or a settings read against a hung network mount -- severe enough on its own
merits regardless of the PTT distinction. Found 4 new items: **(1) [risk]** `PlayWithPttAsync`'s own
key-command catch never bumped `_pttKeyEpoch` -- the FIFTH site of the lost-update pattern rounds 5-8
closed everywhere else (the twin site in `SetPttLockAsync`, round-8 finding, got this treatment;
`PlayWithPttAsync`'s own key-command catch never did). Round 14's `WaitAsync` fix made this newly
reachable in practice (a hung key command now reliably throws instead of hanging forever). **(2)
[risk]** the round-14 comment's claim that the urgent un-key is a working recovery after a key-command
timeout is misleading -- both shipped CAT backends (`RigctldClientProtocol`/`HamlibRadioProtocol`)
serialize every request behind a single semaphore held ACROSS the reply read, so the abandoned,
still-running key command still holds it, and the cleanup un-key attempt is *expected* to itself time
out and log Critical, not silently recover. Still net-better than the pre-round-14 unbounded hang (the
operator is now told, loudly), but the comment overstated it as a working fallback. **(3)** as above,
recommended fixing the 3 deferred pre-key awaits now (of the 4, `StopReceivingAsync` specifically
needs a DIFFERENT fix shape -- it takes no `CancellationToken` at all and a naive bound would abandon
a drain-thread join mid-flight, permanently stranding `_isReceiving` instead of fixing anything).
**(4)-(9) [nits]**: the cancellation-preservation claim only holds if the callee polls `ct` promptly
(Hamlib doesn't, once its native call has started); abandoned `WaitAsync` tasks have no fault-observer
continuation (contrast `StopPlaybackWithWatchdogAsync`'s own `ContinueWith`, Program.cs's global
`UnobservedTaskException` handler still catches these so no crash, just detached-from-context
logging); `StartPlaybackAsync`'s timeout path leaves the abandoned open holding `MiniAudioEngine`'s
own playback lock, so the following watchdog can burn its own full budget too (self-healing, not a
new failure mode); `StopReceivingAsync` sitting outside the guarded region skips some housekeeping if
`StopCaptureAsync` throws; a stale/overstated "confirmed safe" comment on
`OnDecoderRestartCriticallyOverdue`'s synchronous re-entrant `GetResult()` call, whose actual safety
depends on `MiniAudioEngine`-internal mechanics this chunk doesn't own (out of scope, flagged not
re-verified); `_maintenanceWarningActive` was a plain `bool` despite being written from the audio
drain thread, unlike every other cross-thread flag in the class.

**Chunk 3a round 15 fixes applied** (2026-08-21, commit `c59d526`). Finding 1: `PlayWithPttAsync`'s key
command wrapped in its own `try`/`catch` bumping `_pttKeyEpoch` on ANY exception before rethrowing,
mirroring `SetPttLockAsync`'s own round-8 fix exactly. Finding 2: comment corrected to describe the
actual, expected outcome (a Critical-logged failed recovery, not a silent success) rather than
presenting it as a working fallback. Finding 3: the three `ct`-taking pre-key awaits
(`ResolveDeviceAsync`, `GetTxVolumePercentAsync`, `LoadAudioSettingsAsync`) all wrapped with
`.WaitAsync(_cleanupTimeout, ct)`; `StopReceivingAsync` deliberately left unfixed with an explicit
comment recording the different fix shape it needs and why a naive bound would make things worse, not
better. While building finding 3's test, discovered (not from the auditor's report) that
`TransmitAsync`'s own preamble (`GetStationIdTransmitOptionsAsync`) reads settings unbounded too,
BEFORE `PlayWithPttAsync`/`_transmitInFlight` is ever reached -- fixed with the same `WaitAsync`
treatment, lower severity noted (no lockout amplification, since the guard isn't held yet) but still a
real unbounded wait worth closing. Nits 4-9: nit 4 effectively closed as a side effect of passing `ct`
to `WaitAsync` itself (see below), not just caveated; nits 6/7/8/9 addressed with comment-only fixes
(playbook-adjacent code comments, not behavior changes) except 9, which is a real one-word field fix
(`_maintenanceWarningActive` made `volatile`); nit 5 (fault-observer continuations) logged as a queued
item, not fixed this round -- Program.cs's existing global handler already prevents a crash, so this
is a diagnostics-quality improvement, not a safety fix, and adding it to 6 call sites (2 from round 14
+ 4 new from round 15) was judged disproportionate scope for this round.

CA2016 (forward `ct` to `WaitAsync`) fired on the new `GetStationIdTransmitOptionsAsync` call site,
which prompted passing `ct` to ALL SIX `WaitAsync(_cleanupTimeout)` call sites (round 14's original
two included) as `.WaitAsync(_cleanupTimeout, ct)` instead -- a genuine improvement, not just a lint
fix: `Task.WaitAsync(TimeSpan, CancellationToken)` observes the passed token independently of the
awaited task, so a genuine caller cancellation is now noticed and classified correctly even when the
callee itself never polls `ct` promptly (closing round-15's own nit 4 for these six sites, not just
caveating it).

New regression tests: `Round15_PlayWithPttAsync_KeyCommandThrows_EpochBump_SurvivesConcurrentUnkeyersStaleClear`
(finding 1, mirrors the round-8 test's own structure), `Round15_PlayWithPttAsync_ResolveDeviceAsync_Hangs_TimesOutRatherThanStrandingTransmitInFlightForever`
(finding 3, required adding a `Gate` mechanism to `FakeSettingsStore` matching `FakeAudioDeviceEnumerator.Gate`'s
own round-10 ct-respecting pattern -- and required using `TuneAsync` rather than `TransmitAsync`, since
gating the shared settings store also hits the newly-discovered `GetStationIdTransmitOptionsAsync` bound
first if `TransmitAsync`'s own preamble runs at all), and
`Round15_GetStationIdTransmitOptionsAsync_SettingsReadHangs_TimesOutRatherThanHangingForever` (the
newly-discovered fix). All three mutation-verified: each fix reverted in turn, each mutated test
produced an outright hang under an external `timeout` bound (finding 1's test instead failed a clean
assertion -- the missing-backstop signature -- since disabling only the epoch bump doesn't itself
create a hang, just a stale-state race), confirming the exact predicted failure mode each time.
Restored, rebuilt clean, all three re-confirmed passing, no stray test-host processes survived any
mutation. All 194 `ScanlineStudio.Application.Tests` passing (191 pre-existing + 3 new), full solution
suite (all projects) clean.

Round 15 fixed 1 new blocker-class risk (finding 1, a genuine gap in an already-4-times-fixed pattern)
plus a genuinely new bug discovered outside the auditor's own report (`GetStationIdTransmitOptionsAsync`)
-- round 15 does NOT count as chunk 3a's 1st clean round. Round 16 is now the earliest round that can
count as chunk 3a's 1st clean round. Fourteen consecutive rounds (2-15) have now each found something
real in this file.

**Chunk 3a round 16** (2026-08-21, independent agent, fresh context, agent `a71f21caef3ca99ad`).
Re-derived and confirmed all of round 15's fixes correct, including independently re-verifying
finding 2's claim (that the urgent un-key after a timed-out key command is expected to itself fail)
directly against `RigctldClientProtocol.cs`/`HamlibRadioProtocol.cs`'s own semaphore code rather than
trusting the prior round's assertion. Found: **(1) [blocker]** the RX-resume `StartReceivingAsync`
calls (one in `SetPttLockAsync`, three inside `PlayWithPttAsync`'s cleanup) were STILL effectively
unbounded despite round 10's own fresh-CTS "fix" -- passing `rxResumeCts.Token` as the CALLEE's own
`ct` parameter does not bound anything, since `MiniAudioDeviceEnumerator.RefreshAsync`/
`MiniAudioEngine.StartCaptureAsync` only check `ct` at their own start/lock-acquire boundary, never
during the blocking native call itself. This is the exact same mistaken assumption every round from
10 through 15 made about this specific CTS, at a site none of them re-examined once it "looked
already-sufficient." A hang here strands `_transmitInFlight` (permanent TX lockout) or `_pttLockGate`
(permanent PTT-escape-hatch lockout), depending on which of the 4 sites hangs. **(2) [risk]**
`StopReceivingAsync`'s wait on `IAudioEngine.StopCaptureAsync()` (no `CancellationToken` parameter at
all) was genuinely unbounded, previously deferred by round 15 as needing a "paired fix" it didn't yet
have a design for -- round 16 designed one: capture the `Task` before awaiting, bound the wait with
`WaitAsync` on the TASK itself (mirroring `StopPlaybackWithWatchdogAsync`'s own established shape for
the playback side), force `_isReceiving = false` on any outcome (the handlers are already detached
before the stop attempt, so this is truthful regardless), and use `WaitAsync` specifically instead of
`Task.WhenAny`+`Task.Delay` so the pre-existing synchronous drain-thread-inline fast path (relied on
by `OnDecoderRestartCriticallyOverdue`'s own `GetAwaiter().GetResult()` call) stays genuinely
synchronous with no forced state-machine hop. **(3) [risk]** `TxVolumePercent` flowed straight into
`PlayWithPttAsync`'s `gain` multiplier with no range check at all, read OR write -- a corrupted/hand-
edited `settings.json` (the identical threat model `GetStationIdTransmitOptionsAsync`'s own WPM/tone-
frequency validation already codes against) could put an arbitrary multiplier on the transmitted
audio (hard-clipping splatter on a positive out-of-range value, phase inversion on a negative one).
**(4)-(7) [nits]**: `TransmitAsync`'s `Task.Run` sample-count estimate is the one remaining unbounded
step in the preamble (self-terminating, CPU-bound, not urgent); round 15's own settings-read bound
changed `GetStationIdTransmitOptionsAsync`'s public preview-path contract to allow `TimeoutException`
where none could occur before (worth a one-line caller check, not chased here); `Log.TxStarting` logs
before the single-flight guard, so a rejected overlapping transmit still logs a "TX starting" with no
matching completion; four more unbounded read-only settings/device-name reads exist at lower
severity (none holds a shared guard, so a hang there is self-contained, not a lockout).

**Chunk 3a round 16 fixes applied** (2026-08-21, commit `2a2311b`). Finding 1: all four
`StartReceivingAsync(rxResumeCts.Token)` call sites now also wrapped with
`.WaitAsync(rxResumeCts.Token)` -- reusing the SAME CTS's token for both purposes (rather than adding
yet another `_cleanupTimeout` reference) since that CTS already carries the exact timeout needed and
`Task.WaitAsync(CancellationToken)` observes a token's cancellation independently of whether the
awaited task itself polls it, closing the gap regardless of the callee's own behavior. Finding 2:
`StopReceivingAsync` rewritten per the design above -- new `Log.CaptureStopWatchdogFired` message,
fault-observer `ContinueWith` on the abandoned task (matching `StopPlaybackWithWatchdogAsync`'s own
precedent), swallow-and-log rather than propagate (also matching that precedent), sync-throw-from-
`StopCaptureAsync()`-itself still handled as its own early-return arm. The stale round-15 deferral
comment at the `PlayWithPttAsync` call site was updated to point at this fix instead of describing a
still-open gap. Finding 3: both `GetTxVolumePercentAsync` (read) and `SetTxVolumePercentAsync` (write)
now clamp to `[0, 100]` via `Math.Clamp` -- clamping at both boundaries (not just read) keeps what's
actually stored on disk consistent with what every reader promises, rather than relying on the
read-side clamp to mask an unclamped value forever. Nits 4-7 logged, not fixed this round --
genuinely lower severity/self-contained, judged disproportionate scope alongside the three real fixes
above.

New regression tests, one per finding, required two new test-double additions:
`GatedStartCaptureAudioEngine` (hangs a specific 1-based call number, IGNORING its own `ct` entirely
-- deliberately unlike `FakeAudioDeviceEnumerator.Gate`, which DOES respect `ct` and so could never
have caught finding 1's specific gap, explaining why round 10's own original test for this exact code
path passed even before this fix existed) and `GatedStopCaptureAudioEngine` (the capture-side mirror
of the existing `GatedStopPlaybackAudioEngine`). All three mutation-verified: findings 1 and 2 each
hung outright under an external `WaitAsync(TimeSpan.FromSeconds(5))`/`timeout` bound when their fix
was reverted; finding 3's test initially had a real gap of its own (asserting the write-side clamp by
reading back through the STILL-clamped getter, which passed even with the write-side clamp
completely removed, since the read-side clamp alone was already enough to mask it) -- caught by
actually running the mutation and observing the test stayed green, fixed by asserting against the
raw stored settings value directly instead, re-mutated to confirm the corrected test now fails with
the exact predicted unclamped value, then restored. All three fixes reverted, confirmed, and restored
in turn; no stray test-host processes survived any mutation. All 197
`ScanlineStudio.Application.Tests` passing (194 pre-existing + 3 new), full solution suite (all
projects) clean.

Round 16 fixed 1 new blocker (a gap in a mechanism every round since round 10 had assumed was already
closed) plus 2 more real risks -- round 16 does NOT count as chunk 3a's 1st clean round. Round 17 is
now the earliest round that can count as chunk 3a's 1st clean round. Fifteen consecutive rounds (2-16)
have now each found something real in this file.

**Chunk 3a round 17** (2026-08-21, independent agent, fresh context, agent `ae95d3b5040f6dc28`).
Re-derived and confirmed all three of round 16's fixes correct, verifying the drain-thread synchronous
fast path end-to-end (not assumed) and confirming no downstream double-clamp on the TX gain fix.
Applying round 16's own lesson exhaustively -- re-examining EVERY remaining `CancellationTokenSource`/
token site in the file for the same "callee-`ct`-without-outer-`WaitAsync`" gap -- found two more
instances of the identical mechanism, both on the PTT command itself, never bounded in 16 rounds:
**(1) [BLOCKER]** `SetPttLockAsync`'s own `_radioSession.SetPttAsync(locked, ct)` call had no bound of
its own -- the same gap `PlayWithPttAsync`'s key command had before round 14's fix, never given the
same treatment here. Verified against both shipped backends directly: `HamlibRadioProtocol.SetPttAsync`'s
`CallAsync` wrapper takes NO `CancellationToken` at all (airtight -- once the blocking native call
starts, nothing can interrupt it); `RigctldClientProtocol`'s reply read has no timeout of its own
either. A wedge here (either direction) hangs forever WHILE HOLDING `_pttLockGate`, permanently
blocking every future `SetPttLockAsync` call including a future emergency unlock -- the one escape
hatch this whole method exists to be. **(2) [BLOCKER, the single most safety-critical await in the
file]** `TryUnkeyPttAsync`'s own `_radioSession.SetPttAsync(false, ct)` call -- the UN-KEY -- had the
identical gap. `UnkeyForCleanupAsync`'s fresh `unkeyCts.Token` was passed as the callee's own `ct`
parameter, which round 16 already proved does not bound either backend's native/protocol call. This
method has 5 call sites: `PlayWithPttAsync`'s finally (hang here strands `_transmitInFlight` --
permanent TX/Tune lockout, PTT keyed, and `Log.PttStillKeyedAfterFailedUnkey` never fires since
nothing ever returns), `SetPttLockAsync`'s recovery, and worst of all **`DisposeAsync`'s own
backstop** -- which would never return, letting the host's ~10s teardown bound expire and the process
exit with the transmitter physically keyed and the Critical "PTT MAY STILL BE KEYED" log never even
emitted. The exact scenario this entire chunk exists to make impossible, silently skipped, 16 rounds
in. Also found: **(3) [risk]** `.WaitAsync(...)` structurally cannot bound a callee's SYNCHRONOUS
PREFIX (e.g. `JsonSettingsStore.LoadAsync`'s blocking `File.Exists`/`File.OpenRead` before its first
`await`) -- every settings-backed `WaitAsync` bound in this file (rounds 15/16's fixes, transitively
the RX-resume chain too) inherits this hole on a hung network-mounted settings path. **(4) [risk]**
`DisposeAsync`'s tail (`StopReceivingAsync`/`Waterfall.Dispose()`/`_decoder.Dispose()`) had no guard at
all -- one throw skips whatever comes after, leaking that resource; `MiniAudioEngine.DisposeAsync`
already has the equivalent fix for the identical shape. **(5) [risk]** `TuneAsync` does no range
validation on `duration`/`frequencyHz` -- the strictly stronger case of round 16's own `TxVolumePercent`
precedent, since `duration` directly controls how long PTT stays keyed and the only production caller
(`RadioStatusViewModel`) passes `CancellationToken.None` with no Stop command. **(6)-(9) [nits]**:
`StopReceivingAsync`'s sync-throw arm skipped `ResetAgc()`/`Log.RxStopped`; its own round-16 comment
overclaimed the watchdog bounds anything on the drain-thread fast path (it bounds nothing there --
the stop is already synchronous and complete by the time `WaitAsync` is reached); the capture-side
twin of `StopPlaybackWithWatchdogAsync`'s own documented "known consequence" was undocumented (verified
benign, unlike the playback-side residual); a round-11 comment claiming a double-subscription bug
"survives... for the rest of the process's life" may be overstated against the CURRENT `MiniAudioEngine`
(not fully re-verified, out of this chunk's own failure class either way).

**Chunk 3a round 17 fixes applied** (2026-08-21, commit `39e4d58`). Findings 1/2: both wrapped with
`WaitAsync` -- finding 1 uses `.WaitAsync(_cleanupTimeout, ct)` (matching `PlayWithPttAsync`'s own
key-command fix exactly); finding 2 uses `.WaitAsync(ct)` alone, since `ct` here IS already
`unkeyCts.Token` (a fresh CTS carrying `_cleanupTimeout`) -- reusing it rather than adding a second,
redundant timeout, matching round 16's own RX-resume fix pattern. `UnkeyForCleanupAsync`'s own doc
comment (which claimed the fresh CTS alone "is blocker 1's actual fix") corrected to note that giving
it its own budget and actually BOUNDING the await are two different things -- the first was already
true, the second was missing until now. Finding 4: `DisposeAsync`'s three tail steps each wrapped in
their own try/catch + `Log.CleanupStepFailed`, matching `MiniAudioEngine.DisposeAsync`'s own
established fix for the identical shape. Nit 6: the sync-throw arm no longer early-returns past
`ResetAgc()`/`Log.RxStopped` (falls through with `stopTask = Task.CompletedTask`, letting the rest of
the method run unchanged rather than duplicating the tail logic). Nits 7-9: comment corrections only.
Findings 3 and 5 deliberately NOT fixed this round -- both require a broader design decision
(finding 3: `Task.Run`-wrapping every settings read vs. a `JsonSettingsStore`-level fix; finding 5:
what validation/clamp shape and limits are appropriate for direct caller arguments, as opposed to a
persisted setting) that shouldn't be rushed alongside two blockers in the same round -- flagged for a
dedicated future round, not silently dropped.

New regression tests for findings 1 and 2 (the two blockers), using the existing
`FakeRadioSessionService.HangOnCallNumber` mechanism (round 14) -- no new test infrastructure needed.
Finding 2's test targets the SECOND `SetPttAsync` call specifically (the cleanup un-key, forced via
`ThrowOnStartPlaybackAudioEngine`'s deterministic abnormal termination), leaving the first (the key
itself) to succeed normally. Both mutation-verified: each hung outright under an external `timeout`
bound when their `WaitAsync` bound was reverted -- finding 2's especially so, being the single most
safety-critical await in the file. Finding 4 (DisposeAsync tail guard) intentionally NOT given a
dedicated test this round -- would need new throw-injection hooks on `FakeSstvDecoder`/
`FakeWaterfallSource` that don't exist yet, and the fix itself mirrors an already-proven pattern
(`MiniAudioEngine.DisposeAsync`'s own equivalent, from the Batch 1 audit) closely enough to judge safe
to ship without one, given this round's already-large scope. Restored, rebuilt clean, both new tests
re-confirmed passing, no stray test-host processes survived either mutation. All 199
`ScanlineStudio.Application.Tests` passing (197 pre-existing + 2 new), full solution suite (all
projects) clean.

Round 17 fixed 2 new blockers -- including the single most safety-critical await in the entire file,
unbounded for all 16 prior rounds -- does NOT count as chunk 3a's 1st clean round. Round 18 is now the
earliest round that can count as chunk 3a's 1st clean round. Sixteen consecutive rounds (2-17) have
now each found something real in this file.

**Chunk 3a round 18** (2026-08-21, independent agent, fresh context, agent `add089e3d47863f4a`). Round
17's fixes were each correct as far as they went, but one (`TryUnkeyPttAsync`) was found incomplete in
exactly the same way rounds 16/17 kept finding elsewhere: it fixed the awaiting side, not the callee
side. **(1) [BLOCKER]** round 17's `.WaitAsync(ct)` fix bounded the WAIT on the un-key command but also
left `ct` (the same `unkeyCts.Token`) passed to the command ITSELF -- once the 5s budget expired, the
un-key was CANCELLED AT THE BACKEND'S REQUEST GATE and never actually reached the rig, rather than
staying queued behind whatever wedged it and reaching the rig once that clears. Verbatim the failure
`PlayWithPttAsync`'s own blocker-1 doc comment already names as the scenario that matters most ("PTT-off
was never even attempted on the exact hardware failure where it matters most"). **(2) [BLOCKER]**
`UnkeyForCleanupAsync`'s own bool return (whether the un-key was actually CONFIRMED) was discarded at
both `PlayWithPttAsync` cleanup call sites -- an urgent un-key that failed against a momentarily-busy
backend (SWR cutoff / manual Stop TX racing a backend that frees up moments later) got exactly ONE
attempt, then deferred entirely to `DisposeAsync`, which may not run for hours. Also picked up BOTH of
round 17's deliberately-deferred items and fixed them: **(3) [risk, round-17 deferral b]** `TuneAsync`
validated neither `frequencyHz` nor `duration` -- NaN/infinity frequency reaches
`PumpToPlaybackAsync`'s unclamped gain multiplication as NaN samples WITH PTT KEYED; an absurd
duration (any legal `TimeSpan`, no upper bound) keys PTT for a correspondingly absurd time with no
caller-side cancellation available on the one production path. **(4) [risk, round-17 deferral a]**
confirmed `WaitAsync` structurally cannot bound a callee's synchronous prefix -- `JsonSettingsStore.LoadAsync`
does blocking `File.Exists`/`File.OpenRead` before its own first `await`, so every settings-backed
`WaitAsync` bound in this file (rounds 15/16/17's fixes, transitively the RX-resume chain) never even
reaches a genuinely-pending `Task` on a hung network-mounted settings path. Also found: **(5) [risk]**
`SetPttLockAsync`'s UNLOCK-direction failure had literally no catch arm at all (the existing one is
filtered on `rigIsRealAtKeyTime`, always false for `locked == false`) -- a failed EMERGENCY unlock on a
genuinely keyed rig produced not even a Warning, though the underlying safety net (`_pttLocked` staying
true) was never actually broken. **(6) [risk]** `DisposeAsync`'s first two steps
(`AwaitInFlightKeyedTransmitAsync`, the backstop `UnkeyForCleanupAsync` call) had no guard at all --
the identical "one throw skips everything after" shape round 17 fixed 30 lines below, just two steps
earlier (realistic source: a failing logging provider inside either step's own log calls). Nits (not
fixed): abandoned `CancellationTokenSource`s disposed while a task still holds their token (partly
closed by finding 1's own fix); the abandoned un-key's eventual outcome isn't observed/logged, unlike
its two direct siblings; `EnqueueAllAsync` doesn't validate `accepted` against `IAudioEngine`'s own
contract (a negative value would drive a tight synchronous spin with PTT keyed, but only reachable via
a contract-violating engine implementation, not external wedging); 3 `ISstvDecoderMaintenance`
handlers never unsubscribed in `DisposeAsync`; `StopReceivingAsync`'s own `ResetAgc()`/`Log.RxStopped`
tail (and `PlayWithPttAsync`'s call to it) still sits outside its own guarded region.

**Chunk 3a round 18 fixes applied** (2026-08-21, commit `c1b4796`). Finding 1: `TryUnkeyPttAsync` restructured
to pass `CancellationToken.None` to the actual `SetPttAsync` call (so it can never be cancelled, and
stays queued until the backend genuinely frees up) while `WaitAsync(ct)` still bounds only the WAIT.
Finding 2: `PlayWithPttAsync`'s finally now captures `UnkeyForCleanupAsync`'s bool return and retries
once (guarded on `pttKeyedOnRealRig && !unkeyConfirmed`, so the benign `RigId=="none"` case never gets
a pointless second attempt) after `StopPlaybackWithWatchdogAsync`'s own wait gives a wedged backend a
second, independent window to clear. Finding 3: `TuneAsync` now validates both parameters and THROWS
(not clamps, unlike the `TxVolumePercent` precedent -- there's a live caller that already surfaces a
failure, so it needs to know its own value was wrong) before `Log.TuneStarting`/`PlayWithPttAsync`, so
PTT is never touched on an invalid call; a new `MaxTuneDuration` constant (5 minutes, a generous
backstop not a UX limit) caps the worst case. Finding 4: every settings-backed `WaitAsync` call site (8
of them: the `GetStationIdTransmitOptionsAsync` read, the 3 pre-key awaits, and the 4 RX-resume sites)
now wraps its own callee in `Task.Run(...)` first, offloading the synchronous prefix onto a pool
thread so the outer `WaitAsync` bound becomes real regardless of what the settings store/device
enumerator does internally. Finding 5: a second catch arm added to `SetPttLockAsync`'s key command,
filtered on a new `rigIsRealAtUnlockTime` capture (mirrors `rigIsRealAtKeyTime`'s own blocker-2
discipline -- captured once, before the command), deliberately simpler than the engage-direction arm
(no epoch bump, no recovery attempt -- just makes the failure loudly visible via the same Critical log
`UnkeyForCleanupAsync` already uses for the identical condition). Finding 6: both `DisposeAsync` steps
wrapped in the same try/catch + `Log.CleanupStepFailed` pattern round 17 already established for the
three steps just below them.

New regression tests for all 4 fixable findings (1, 2, 3, 5) -- finding 1's test required upgrading
`FakeRadioSessionService`'s `Gate` to respect `ct` (matching `FakeAudioDeviceEnumerator.Gate`'s own
round-10 upgrade; without it, the fake couldn't distinguish a cancelled-at-the-gate command from a
merely-slow one, and the FIRST version of this test passed even with the bug still present -- caught
by actually running the mutation and observing no failure, not assumed) and a new `GateOnCallNumber`
property (a gate that CAN be resolved later, unlike `HangOnCallNumber`'s permanent hang, needed to
prove an abandoned call eventually completes rather than being cancelled). Finding 2's test interacts
observably with finding 1's: the retry it proves is what makes finding 1's own gated call arrive as
the SECOND `false` in `PttCalls`, not the first -- both tests' assertions account for this rather than
treating it as noise. All 4 mutation-verified: findings 1 and 2 each produced their exact predicted
failure signature (an abandoned call that never completes; a retry count of 1 instead of 2); findings
3 and 5 each failed a clean assertion (no exception thrown; no Critical logged) when their guard was
disabled. Findings 4 and 6 (mechanical structural additions, no new failure mode to demonstrate
distinctly from what's already covered) intentionally not given dedicated tests, matching this
chunk's own established precedent for proportionate scope. Restored, rebuilt clean, all 4 new tests
re-confirmed passing, no stray test-host processes survived any mutation. All 203
`ScanlineStudio.Application.Tests` passing (199 pre-existing + 4 new), full solution suite (all
projects) clean.

Round 18 fixed 2 new blockers plus both of round 17's own deferred items -- does NOT count as chunk
3a's 1st clean round. Round 19 is now the earliest round that can count as chunk 3a's 1st clean round.
Seventeen consecutive rounds (2-18) have now each found something real in this file.

**Chunk 3a round 19** (2026-08-21, independent agent, fresh context, agent `aff515a7f413b3b58`).
Independently re-derived every round-18 fix with real scrutiny (not a skim), given three rounds in a
row (16, 17, 18) had each found the PRIOR round's own "callee doesn't observe ct" fix was itself
incomplete. Confirmed all 8 `Task.Run` sites correct, `TuneAsync`'s Nyquist/NaN bounds correct, the
`DisposeAsync` guards correct, `TryUnkeyPttAsync`'s own decoupling correct. But found: **(1) [BLOCKER]**
`PlayWithPttAsync`'s entire cleanup region (urgent un-key / StopPlayback / retry / RX-resume) had a
`finally` but NO `catch` -- `UnkeyForCleanupAsync`/`TryUnkeyPttAsync` are documented as never throwing,
but that guarantee was never actually enforced, and a throwing logging provider (the same realistic
source rounds 17/18 already treated as in-scope for `DisposeAsync`'s own per-step guards) is enough to
violate it. Without a guard, that throw skips EVERY step below it -- StopPlayback, the round-18 retry,
RX-resume -- with the un-key failure never recorded anywhere: `_pttUnkeyFailedOnRealRig` never gets
set, `DisposeAsync`'s four-state check finds nothing to do, and the process can exit with the
transmitter physically keyed and no Critical log ever emitted. Worse than `DisposeAsync`'s own
equivalent gap (which round 17 already fixed), since this is the PTT-off record itself, not just
teardown. **(2) [risk]** one surviving instance of round 18's exact "`ct` wired to both the command and
the wait" bug -- `SetPttLockAsync`'s own PTT command, line 439, still passed `ct` to the UNLOCK
direction's command itself (round 18 fixed this in `TryUnkeyPttAsync` but this sibling site, the same
class of bug, survived). A caller cancelling `ct` while queued behind a wedged prior command would
abort the emergency-unlock escape hatch at the backend's own request gate, never reaching the rig --
latent today (zero production callers), but a real trap for whoever wires the PTT-lock button.
**(3) [risk]** abandoned RX-resume `Task.Run` tasks (round 16/18's own fix) are unobserved and can
complete AFTER `DisposeAsync`, using a disposed decoder/engine -- `StartReceivingAsync`'s `_disposed`
check only runs at its own first line, not immediately before the actual publish (subscribing
handlers, setting `_isReceiving = true`), and unlike `StopPlaybackWithWatchdogAsync`/`StopReceivingAsync`'s
own abandoned-task handling, no fault-observer continuation exists for these. Nits: round 18's own
retry comment overstated the mechanism (the retry queues BEHIND the abandoned first command, not
beside it on an "independent window", since round 18 also made that first command uncancellable); a
round-18 catch arm (finding 5's own fix) latched `_pttUnkeyFailedOnRealRig`/logged Critical even for a
rig this class never believed was keyed at all (`SetPttLockAsync`'s own documented always-issue-the-
command policy makes this reachable); the queued `EnqueueAllAsync` accepted-validation note was
mis-described (a negative value throws via array-slice bounds, not a synchronous spin).

**Chunk 3a round 19 fixes applied** (2026-08-21, commit `f015bdf`). Finding 1: each step in `PlayWithPttAsync`'s
cleanup region (the urgent `UnkeyForCleanupAsync` call, `StopPlaybackWithWatchdogAsync`, the round-18
retry) now wrapped in its own try/catch + `Log.CleanupStepFailed`, matching `DisposeAsync`'s own
already-established per-step shape. Also reordered `UnkeyForCleanupAsync`'s own
`_pttUnkeyFailedOnRealRig = true` write to run BEFORE its own `Log.PttStillKeyedAfterFailedUnkey` call
(not after) -- so even if THAT log call itself throws, the state is still correctly latched before the
exception (now caught by the new wrapper) propagates. This does not claim to make logging fully safe
against itself (an inherent limit -- you cannot use logging to guard against logging failures without
eliminating logging from the failure path entirely), but closes the actual blocker-tier consequence:
every OTHER cleanup step still gets its chance to run regardless of what caused the throw. Finding 2:
`SetPttLockAsync`'s own PTT command decoupled exactly as round 18 did for `TryUnkeyPttAsync` --
`locked ? ct : CancellationToken.None` for the command, `WaitAsync(_cleanupTimeout, ct)` still bounds
the wait in both directions. Finding 3: new shared `ResumeReceivingBoundedAsync` helper centralizes
all 4 RX-resume call sites' `Task.Run`+`WaitAsync` shape and adds a fault-observer `ContinueWith` on
the background task (attached unconditionally, not just in a timeout branch, since this is now the
single choke point for the `Task.Run` creation -- `OnlyOnFaulted` makes it a no-op on the common
success path regardless of when attached); `StartReceivingAsync` now rechecks `_disposed` a second
time, immediately before its own publish block, not only at its own first line. Nits: the round-18
retry comment corrected to describe the actual queued-behind-not-beside mechanism; the round-18 catch
arm (finding 5's own site) gated on `_pttLocked || _pttLeftKeyedByCall || _pttUnkeyFailedOnRealRig`
before latching/logging, so a failed unlock of a rig this class never believed was keyed produces no
false alarm; `EnqueueAllAsync`'s queued note left uncorrected in-file (it was only ever tracked in this
playbook/PROJECT_BRIEF, not as an in-source comment -- corrected here instead).

New regression tests for all 3 fixable findings. Finding 1's test required a new
`RecordingLogger.ThrowOnMessageContaining` hook (simulates a broken logging provider) and a dedicated
`SimulatedLoggingProviderFailureException` type -- the FIRST version of this test used the same
exception type (`InvalidOperationException`) for both the simulated logging failure and the original
forced-abnormal-termination exception, so it passed even with the fix reverted (both exceptions looked
identical to the assertion); caught by actually running the mutation and observing the wrong exception
type surface, fixed by giving the simulated failure its own distinct type. Finding 2's test similarly
needed a redesign mid-round: the first version used an ALREADY-cancelled token, which `SetPttLockAsync`'s
own `_pttLockGate.WaitAsync(ct)` at its very first line rejects before ever reaching this round's fix,
so the mutation had no observable effect (caught the same way, by actually running it); the corrected
version uses `GateOnCallNumber` (parking only the unlock command) plus `CancelAfter` (guaranteeing
genuine mid-flight cancellation without needing to detect "now parked" directly) so the token is still
valid past the gate but cancels while the command itself is genuinely in flight. All 3 mutation-verified
after their redesigns: finding 1 produced the exact predicted wrong-exception-type signature; finding 2
produced the exact predicted "abandoned command never completes" signature; finding 5's nit-turned-test
(the false-Critical-alarm gate) failed a clean assertion when disabled. Finding 3 (the RX-resume
fault-observer + `_disposed` recheck) intentionally not given a dedicated test this round -- both are
structural/defensive additions with no new isolable failure mode distinct from what round 16/18's own
existing coverage already exercises, matching this chunk's established precedent for proportionate
scope. Restored, rebuilt clean, all 3 re-confirmed passing, no stray test-host processes survived any
mutation. All 206 `ScanlineStudio.Application.Tests` passing (203 pre-existing + 3 new), full solution
suite (all projects) clean.

Round 19 fixed 1 new blocker plus 2 more real risks, including catching two genuine gaps in its own
regression tests before they shipped (not just in the production code) -- does NOT count as chunk 3a's
1st clean round. Round 20 is now the earliest round that can count as chunk 3a's 1st clean round.
Eighteen consecutive rounds (2-19) have now each found something real in this file.

**Chunk 3a round 20** (2026-08-21, independent agent, fresh context, agent `aaf3a45d4d13a1e89`). Round
19's three fixes verified structurally correct, but round 19's own hunt pattern -- "caller trusts
callee's *never throws* doc" -- was not actually closed. **(1) [BLOCKER]** `TryUnkeyPttAsync`'s
documented "never throws" contract was itself FALSE, and the throw (from its own log calls, when the
logging provider fails) lands BEFORE `UnkeyForCleanupAsync` ever reaches its state-latching write --
round 19's own "write before the log" reorder didn't help, because the throw this time originates one
frame DEEPER, inside the callee round 19 was trusting. Net effect: a real un-key failure on a keyed
rig produces ZERO recorded state -- `_pttUnkeyFailedOnRealRig` never gets set, `DisposeAsync`'s
four-state check finds nothing to do, and the process can exit silently on-air. The exact class this
whole chunk exists to close, reachable again through the vector round 19 itself explicitly declared
in-scope one round earlier. **(2) [risk]** `SetPttLockAsync` still trusted "`UnkeyForCleanupAsync`
never throws" at its own two call sites (one literally has a comment asserting the claim) -- if it
throws, the ORIGINAL exception (a key-command failure, or the disposal signal) gets replaced by
whatever the logging failure's own exception was. **(3) [risk]** round 19's own per-step guarding in
`PlayWithPttAsync`'s cleanup stopped at the retry -- the entire RX-resume region of the SAME `finally`
(3 `TryCleanupAsync` calls, 4 `RaiseCapturePausedForTransmitChanged` calls) remained unguarded, and
since a throw inside a `finally` REPLACES whatever exception was already propagating, this could mask
the real cancellation/fault reason a caller like `TxControlsPaneViewModel` needs to distinguish on.
**(4) [risk]** abandoned `SetPttAsync` tasks (the key command, the un-key, `SetPttLockAsync`'s own
command) have no fault-observer continuation, unlike every OTHER abandoned task in this file
(`StopPlaybackWithWatchdogAsync`, `StopReceivingAsync`, `ResumeReceivingBoundedAsync`) -- for the
un-key specifically this discards exactly the information an operator staring at a Critical "MAY
STILL BE KEYED" needs (did it eventually succeed?). Nits: `ResumeReceivingBoundedAsync` (round 19's
own fix) double-logs an ORDINARY (non-timeout) resume failure, since its fault-observer was attached
unconditionally rather than only on the genuine-abandonment path; the normal-completion un-key path
gets only one attempt, no retry (round 18's retry only applies on the abnormal-termination path);
`StartReceivingAsync`'s second `_disposed` recheck (round 19) is correctly placed but a session opened
just before a late-disposed throw is never closed by this class; a stale-interleaving-comment concern
was re-examined and NOT confirmed -- the cited comments correctly use past-tense/historical framing or
describe a genuinely still-live DIFFERENT interleaving (`PlayWithPttAsync` racing `SetPttLockAsync`,
not round-12's already-closed `PlayWithPttAsync`-vs-`PlayWithPttAsync` case), so no correction applied.

**Chunk 3a round 20 fixes applied** (2026-08-21, commit `d35417d`). New shared `SafeLog` helper (swallows
any exception the log call itself throws -- deliberately silent, since there is nothing safe left to
log TO if the logger itself is broken) applied at every log call inside this class's PTT-safety-
critical catch/cleanup paths: both `TryUnkeyPttAsync` catch arms, `UnkeyForCleanupAsync`'s 3 log
calls, `TryCleanupAsync`'s own catch, `RaiseCapturePausedForTransmitChanged`'s own catch (closing all
3 `TryCleanupAsync`/4 `RaiseCapturePausedForTransmitChanged` call sites in one place, rather than
wrapping each of the 7 individually), and round 19's own 2 wrapper catches in `PlayWithPttAsync`'s
cleanup. Finding 1's other half: `UnkeyForCleanupAsync` now latches `_pttUnkeyFailedOnRealRig` BEFORE
its own attempt (assume-failed-until-confirmed), not just before its own log call -- the state record
no longer depends on `TryUnkeyPttAsync` returning at all. Finding 2: both `SetPttLockAsync` call sites
now wrap their own `UnkeyForCleanupAsync` call in try/catch (logging via `SafeLog`, then continuing to
the original `throw;`/`throw new ObjectDisposedException(...)`) so the ORIGINAL exception always
reaches the caller. Nit fix: `ResumeReceivingBoundedAsync` restructured so its fault-observer is only
attached when the wait genuinely gives up while the background task is STILL running (checked via
`!resumeTask.IsCompleted`), not unconditionally at creation time -- closing the double-log. Findings 3
and 4 (RX-resume region guarding closed via the shared-helper fix above rather than 7 separate
wrappers; abandoned-task fault observers at the 4 remaining `SetPttAsync` call sites) -- finding 3 is
fully closed by the `SafeLog` fix; finding 4 (fault observers, a diagnostics-quality improvement, not
a safety gap given findings 1/2 already handle state recording) deliberately NOT implemented this
round given the round's already-substantial scope -- flagged for a future round, not silently dropped.

New regression test for finding 1 -- the blocker -- using a broad `ThrowOnMessageContaining = "PTT
off"` filter (deliberately matching every "PTT off"-related log call this whole path can reach, not a
narrow one) to prove the state record survives total logging failure across the entire chain, verified
via `DisposeAsync`'s own backstop still firing Critical afterward. Mutation-verified: reverting BOTH
halves of finding 1 (the pre-latch AND `TryUnkeyPttAsync`'s own `SafeLog`) reproduced the exact
predicted failure (the Critical log never fires, since the state was genuinely never recorded).
Finding 2 deliberately NOT given a dedicated test -- its own concrete reachability mechanism was "an
`UnkeyForCleanupAsync` throw", which finding 1's own fix (this same round) closes via `SafeLog`, so no
currently-known way exists to make `UnkeyForCleanupAsync` actually throw and a test built on that
mechanism would be vacuous; the fix itself remains genuine defense-in-depth (protects against a FUTURE
change reintroducing a throwing path), documented as such rather than shipped with a test proving
nothing. Restored, rebuilt clean, the new test re-confirmed passing, no stray test-host processes
survived the mutation. All 207 `ScanlineStudio.Application.Tests` passing (206 pre-existing + 1 new),
full solution suite (all projects) clean.

Round 20 fixed 1 new blocker plus 3 more real risks/nits -- including finding that round 19's own
"never throws" hunt, dispatched specifically to find MORE instances of this exact pattern, had itself
missed the deepest instance one frame further down -- does NOT count as chunk 3a's 1st clean round.
Round 21 is now the earliest round that can count as chunk 3a's 1st clean round. Nineteen consecutive
rounds (2-20) have now each found something real in this file.

**Chunk 3a round 21** (2026-08-21, independent agent, fresh context, agent `a2cf0dbdb926305de`).
Explicitly dispatched to re-verify round 20's own `SafeLog` coverage claim, given that rounds 19 and 20
had each already made and then disproved the SAME "comprehensive" claim one round apart. Round 21 did a
full enumeration of all 57 `Log.*` call sites in the file rather than trusting round 20's account of
where it had applied `SafeLog`, and found round 20's own fix had reached only a "hand-picked subset".
**(1) [BLOCKER]** `DisposeAsync`'s own backstop region -- `AwaitInFlightKeyedTransmitAsync`'s catch, the
PTT backstop's catch, `StopReceiving (dispose)`/`Waterfall.Dispose`/`Decoder.Dispose`'s catches, plus
`Log.WaitingForKeyedTransmitAtShutdown`/`Log.KeyedTransmitCleanupWaitTimedOut` -- 7 log calls on the
process's OWN final safety net, still plain. **(2) [BLOCKER]** `PlayWithPttAsync`'s normal-completion
`StopPlayback` catch, plus 3 log calls inside `StopPlaybackWithWatchdogAsync` itself (both
`CleanupStepFailed("StopPlayback", ...)` arms and the watchdog-fired log) -- still plain. **(3)
[BLOCKER]** `SetPttLockAsync`'s pre-recovery Critical "PTT MAY STILL BE KEYED" log itself (the message
the whole `SafeLog` mechanism exists to guarantee reaches the operator) -- still plain. **(4)/(5)
[risk]** 3 more log calls in `SetPttLockAsync` (`PttStillKeyedAfterFailedUnkey`,
`PttUnkeyRaceLostToNewerKey`, `PttLockChanged`) plus its own "Resume RX (after unlock)" cleanup catch --
still plain. **(6) [risk]** 4 log calls inside `StopReceivingAsync` (`StopCapture` catches x2,
`CaptureStopWatchdogFired`, `RxStopped`) -- still plain; also, `_decoder.ResetAgc()` itself (not a log
call -- a real try/catch gap, not a `SafeLog` gap) sits fully unguarded in both `StartReceivingAsync`
and `StopReceivingAsync`, and a throw there propagates out of `StopReceivingAsync` even though
`_isReceiving` is already correctly latched false by that point. **(6b) [risk]** that same unguarded
`ResetAgc()`, reached via `PlayWithPttAsync`'s own bare `await StopReceivingAsync()` at its entry
(before PTT is ever touched) -- a throw there propagates out of `PlayWithPttAsync` itself. **(7)
[risk]** `SafeLog` itself is silent-only -- a totally broken logging provider (this whole mechanism's
own threat model) leaves an operator with ZERO indication a transmitter might still be keyed, which is
not an acceptable trade for "safe". **(8) [risk]** 6 more log calls outside the PTT path narrowly
construed but still on this class's own maintenance/decode/waterfall fault paths
(`DecoderPushSamplesFailed`, `WaterfallPushSamplesFailed`, `MaintenanceHandlerFailed` x3,
`TransmitProgressHandlerFailed`) -- still plain. **Nit 9**: `ResumeReceivingBoundedAsync`'s own
fault-observer attachment (round 20's own fix) has a narrow race between `WaitAsync` throwing and the
`!resumeTask.IsCompleted` filter evaluating -- judged harmless in practice (.NET Core does not crash on
an unobserved task fault by default; a exception in that narrow window instead falls through to the
caller's own already-established "Resume RX" log, not lost). **Nit 10**:
`StopPlaybackWithWatchdogAsync`/`StopReceivingAsync`'s own fault-observer continuations don't use
`SafeLog` internally, unlike `ResumeReceivingBoundedAsync`'s (round 20) -- inconsistent, same underlying
gap class. **Nit 11**: `SafeLog`'s own doc comment, given the established 2-round pattern of every
"comprehensive" claim being subsequently found incomplete, should say so explicitly rather than risk
round 22+ trusting it the same way.

**Chunk 3a round 21 fixes applied** (2026-08-21, commit `f8e7f8e`). All 3 blockers, both risk-6/6b
findings, nit 10, and finding 7 fixed as real code changes; nit 9 and 11 handled as documented,
reasoned deferrals (not silently dropped). ~28 individual `Log.*` call sites wrapped in `SafeLog` across
findings 1/2/3/4/5/8 (`DisposeAsync`'s region, `PlayWithPttAsync`'s StopPlayback path,
`StopPlaybackWithWatchdogAsync`, `SetPttLockAsync`'s Critical log plus 3 more, `StopReceivingAsync`'s 4
log calls, the decoder/waterfall/maintenance/transmit-progress handler chain). Finding 6/6b: both
`_decoder.ResetAgc()` call sites (`StartReceivingAsync`, `StopReceivingAsync`) now wrapped in a real
try/catch (logged via `SafeLog`, swallowed) rather than left to propagate -- this closes 6b as a direct
side effect, since `PlayWithPttAsync`'s own `StopReceivingAsync()` call site no longer has anything
inside that method able to throw uncaught. Finding 7: `SafeLog` gained a last-resort
`Console.Error.WriteLine` fallback (itself wrapped, in case stderr is also broken) so a totally broken
`ILogger` still surfaces something instead of nothing. Nit 10: both remaining unwrapped fault-observer
continuations now use `SafeLog` internally, matching `ResumeReceivingBoundedAsync`'s own shape. Nit 9:
deferred, reasoned harmless above -- no fix applied. Nit 11: `SafeLog`'s own doc comment rewritten with
an explicit hedge naming rounds 19/20's own repeated false "comprehensive" claims, instructing future
rounds to keep checking rather than trust the comment.

Also fixed, same round: the RE-RAISED finding from round 20's own re-verification pass --
`StartReceivingAsync`'s second `_disposed` recheck (round 19's own fix) threw with no cleanup of its
own, orphaning the native capture session `StartCaptureAsync` had already opened just above it (
`_isReceiving` is still false at that point, so a later `StopReceivingAsync` call unconditionally
early-returns via its own `if (!_isReceiving) return;` guard and the session is never closed). Rounds
18/19 made an abandoned/timed-out RX-resume (via `Task.Run`) the NORMAL way to reach this branch, not an
exotic race, so this was a real session/device/thread leak at every shutdown racing a resume this way.
Fixed: best-effort closes the just-opened session (bounded by `_cleanupTimeout`, logged via `SafeLog` on
failure) before throwing `ObjectDisposedException`.

New regression test: `Round21_StartReceivingAsync_DisposedMidFlightAfterCaptureStarts_...` -- gates
`FakeSettingsStore.LoadAsync` (a step `StartReceivingAsync` awaits before `StartCaptureAsync`), starts
`StartReceivingAsync`, calls `DisposeAsync` concurrently (which sets `_disposed = true` as its own first
line and completes without touching the audio engine, since `_isReceiving` is still false), then
releases the gate so `StartReceivingAsync` proceeds through `StartCaptureAsync` and reaches its own
second `_disposed` check with `_disposed` already true -- asserts the wrapped `IAudioEngine`'s
`StopCaptureAsync` was called exactly once before the expected `ObjectDisposedException`. Mutation-
verified: reverted to the pre-fix shape (`ObjectDisposedException.ThrowIf` with no cleanup), reproduced
the exact predicted failure (`stopCaptureCount` 0 instead of 1), restored, rebuilt clean, re-confirmed
passing. All 208 `ScanlineStudio.Application.Tests` passing (207 pre-existing + 1 new), full solution
suite run in progress at time of writing -- confirm clean before treating this round as closed.

Round 21 fixed 3 blockers, 2 real risks (6/6b, closed together), 1 more nit (10), and gave `SafeLog`
itself a safety improvement (finding 7) plus an honesty improvement (nit 11) -- including finding that
round 20's own "applied SafeLog everywhere" claim, made after round 19 ALSO made and disproved the same
claim, was itself only a "hand-picked subset". Does NOT count as chunk 3a's 1st clean round -- round 22
is now the earliest round that can count as chunk 3a's 1st clean round. Twenty consecutive rounds
(2-21) have now each found something real in this file.

**Chunk 3a round 22** (2026-08-21, independent agent, fresh context, agent `a088b1dc4c6eb8d5c`). Verdict
EQUIVALENT-WITH-RISKS. Round 21's own "applied SafeLog everywhere" claim disproven a THIRD time --
round 21's own doc comment on `SafeLog` predicted this and told the reader not to trust it, correctly. A
fresh, independent enumeration of all 63 `Log.*` call sites found 21 still unwrapped, 4 inside a catch
whose entire documented purpose is defeated by a throwing log call. No finding in failure class 1
(leaked keyed transmitter); one is class-2-adjacent. **(1) [risk]** `PlayWithPttAsync`'s
`catch (OperationCanceledException)` log call (`Log.PlaybackCancelled`) was unwrapped -- a throwing log
call there REPLACES the OCE as what propagates out of the method, and `TxControlsPaneViewModel`'s own
`catch (OperationCanceledException)` branches on that exact exception identity to distinguish an
SWR-cutoff abort from a generic failure (verbatim the harm `RaiseCapturePausedForTransmitChanged`'s own
doc comment names, which round 20 already guarded for that method while leaving this one bare). PTT
itself unaffected -- the `finally` still un-keys either way. **(2) [risk]** same shape on the shutdown
path: `PlayWithPttAsync`'s `catch (ObjectDisposedException) when (_disposed)` log call
(`Log.PlaybackAbortedByDispose`) was unwrapped, similarly able to substitute an unrelated exception for
the `ObjectDisposedException` this class's own established convention has callers branch on. **(3)
[risk]** `CaptureOverrunCount`'s own `catch (ObjectDisposedException)` log call
(`Log.CaptureOverrunCountRaceObserved`) was unwrapped -- that getter's own doc comment states this catch
exists because it's polled every 250ms by `RxImagePaneViewModel`'s telemetry timer and a
`DispatcherTimer` tick exception has nowhere safe to land; a throwing log call voided that guarantee one
frame deeper. Not PTT, but the same "catch's stated safety property defeated one frame deeper" pattern
rounds 19-21 each chased. Nits: `PlayWithPttAsync`'s generic `catch (Exception)` log call
(`Log.PlaybackFailed`) unwrapped, same shape, no independent PTT hazard since `abnormalTermination` is
already set first; round 21's own `_decoder.ResetAgc()` fix in `StartReceivingAsync` stopped one line
short of its own stated justification -- `Log.RxStarted` immediately below it was still unwrapped, so a
throwing provider still propagated out of that method after capture had genuinely started (swallowed on
the RX-resume path, not on the direct UI Start-RX path); the 3 maintenance-handler log calls
(`MaintenanceCriticalStop`, `MaintenanceWarningRaised`, `MaintenanceWarningCleared`) all run BEFORE their
own event `Invoke()` call, so a throwing log skips the UI notification even though the underlying
RX-state change already happened (outer catch swallows the throw into `MaintenanceHandlerFailed`); the
`InFlightKeyedTransmitWait` sizing comment's "8s worst case" arithmetic is stale -- round 16 added a 5s
`StopCapture` watchdog inside `StopReceivingAsync`, which `DisposeAsync` also calls, bringing the real
worst case to ~13s (no PTT consequence today, but could mislead a future round sizing against it); 3
decoder-command dispatch logs (`ReSyncRequested`/`CorrectSlantRequested`/`ForceMode`) run before the
action they describe, outside any catch and outside all 3 failure classes -- noted, explicitly not
chased.

Explicitly checked and confirmed already-handled, stated rather than left silent: `Log.PttKeyed`'s own
unwrapped call is safe (a throw there routes to the generic catch, which sets `abnormalTermination` FIRST,
so the `finally` still un-keys); `SetPttAsync` has no blocking synchronous prefix on any of the 4 backend
implementations (verified directly against `RadioSessionService`/`RadioController`/
`HamlibRadioProtocol`/`RigctldClientProtocol`), so round 18's sync-prefix hazard doesn't apply to the
key/un-key commands; `StopReceivingAsync`'s handler `-=` unsubscribes cannot throw (`MiniAudioEngine`'s
`SamplesCaptured` is a field-like event, lock-free `CompareExchange`), confirming round 21's claim that
`PlayWithPttAsync`'s own `StopReceivingAsync()` call site no longer has anything able to throw uncaught;
`DisposeAsync`'s "four states only reachable on a non-`none` rig" claim still holds
(`NoneRadioProtocol.SetPttAsync` always throws synchronously); no other unguarded non-log operation
remains in any catch/cleanup/`finally` region (walked `PlayWithPttAsync`'s full cleanup,
`SetPttLockAsync`'s two finallys, and all of `DisposeAsync`); round 21's nit 9
(`ResumeReceivingBoundedAsync`'s fault-observer race) remains harmless with one correction to the stated
rationale -- if `resumeTask` FAULTS (not just completes) in the narrow window, that fault is genuinely
lost, not "just not double-observed" as round 21 put it, but the consequence is one missing log line
(.NET's default unobserved-task-exception policy doesn't escalate), so it stays a deferred nit, not
promoted.

Assumptions the agent flagged as unverified (informational, not findings): `OnDecoderRestartCriticallyOverdue`'s
drain-thread reentrancy claim (already flagged as unverified by round 15's own comment, out of chunk
scope) was not independently re-checked; `Console.Error.WriteLine` inside `SafeLog` is assumed
non-blocking (a redirected-to-full-pipe stderr could in principle stall a cleanup path) but was not
investigated. Off-scope note: `Waterfall.Dispose()`/`Decoder.Dispose()` in `DisposeAsync` remain
unbounded synchronous calls inside a method sized against a 10s host-teardown budget -- explicitly noted
by the agent as off-scope for this chunk, not a finding.

**Chunk 3a round 22 fixes applied** (2026-08-21, commit `5954d85`). All 3 risks and all applicable nits
fixed as real code changes; the 3 decoder-command dispatch logs (out of all 3 failure classes) and the
2 unverified assumptions deliberately left untouched -- explicitly out of scope, not silently missed.
Findings 1/2/3 and the `PlaybackFailed` nit: all 4 `PlayWithPttAsync`/`CaptureOverrunCount` log calls
wrapped in `SafeLog`. The 3 maintenance-handler log calls each wrapped in `SafeLog` too, rather than
reordered relative to their own `Invoke()` call -- the log call itself can no longer throw, which
neutralizes the log-before-invoke hazard without touching event-firing order (simpler, and matches this
file's established SafeLog-only convention rather than introducing a second mitigation shape).
`Log.RxStarted` in `StartReceivingAsync`
wrapped in `SafeLog`, completing round 21's own stated intent for that fix. `InFlightKeyedTransmitWait`'s
sizing comment corrected to state the real ~13s current worst case, with the original "8s" figure kept
for historical context.

New regression test:
`Round22_PlayWithPttAsync_CancellationLoggingFails_OperationCanceledExceptionStillPropagates` -- exercises
finding 1 end-to-end via `TuneAsync`/`cts.Cancel()` (the same shape `TxControlsPaneViewModel.Dispose()`
uses), with `RecordingLogger.ThrowOnMessageContaining = "Playback cancelled"`; asserts the ORIGINAL
`OperationCanceledException` reaches the caller, not the simulated logging-provider failure, and that PTT
is still correctly un-keyed regardless. Mutation-verified: reverted the `SafeLog` wrap on
`Log.PlaybackCancelled`, reproduced the exact predicted failure (`SimulatedLoggingProviderFailureException`
propagating instead of `OperationCanceledException`), restored, rebuilt clean, re-confirmed passing.
209/209 `ScanlineStudio.Application.Tests` passing (208 pre-existing + 1 new), full solution suite run in
progress at time of writing -- confirm clean before treating this round as closed.

Round 22 fixed 3 real risks plus 4 nits, all of the "log call inside a catch/cleanup/log-before-invoke
site defeats the surrounding safety property one frame deeper" pattern that has now recurred in every
round from 19 through 22 -- each round's own "exhaustive" sweep claim disproven by the next round's fresh
enumeration. Does NOT count as chunk 3a's 1st clean round -- round 23 is now the earliest round that can.
Twenty-one consecutive rounds (2-22) have now each found something real in this file.

**Chunk 3a round 23** (2026-08-21, independent agent, fresh context, agent `a5548695f41255f4e`). Verdict
EQUIVALENT-WITH-RISKS. A fresh, independent enumeration of all 67 `Log.*` call sites found round 22's own
completeness claim disproven a fourth time, but only barely -- 2 sites remained, both in the same short
`if (RigId != "none") { ...key... } else { ...skip... }` block. **(1) [risk]** `Log.PttKeyed(_logger)`
(the key-succeeded branch) is THE ONE `Log.*` call in the entire file that executes while a real
transmitter is physically keyed -- more sensitive than any site fixed in rounds 20-22, all of which sit
either before key-up or inside a cleanup/catch region. State (`_pttKeyEpoch`, `pttKeyedOnRealRig`) is
latched BEFORE this line, so a throw here was never a leaked-transmitter (failure class 1) risk -- but it
WAS a transmission-destruction (failure class 2) risk: a transient logging-provider failure landing in
this exact window used to abort an otherwise-healthy transmit immediately after key-up, before any audio
was ever sent -- a bare carrier key-up/key-down burst on air, with the very `PlaybackFailed` log that
would explain why also swallowed by the same broken provider. Explicitly noted as risk, not blocker,
given the state-latching order. **(2) [nit]** `Log.PttSkippedNoRadio(_logger)` (the no-radio branch) --
same shape, but PTT is never touched on this path, so the only harm is a transient logging failure
aborting a no-radio-configured transmit with a bogus exception type instead of completing normally.

Explicitly checked and confirmed already-handled this round, stated rather than left silent: no unguarded
non-log operation remains in any of the file's 36 catch/finally bodies (walked all of them -- every one is
`Interlocked.*`/a plain field write/`SafeLog`/`ContinueWith`/a guarded `await`; the sole non-`SafeLog`
throwable, `new CancellationTokenSource(_cleanupTimeout)`, is unreachable with the production 5s value);
`StopReceivingAsync`'s "can no longer throw" claim (relied on by `PlayWithPttAsync`'s bare `await`) holds,
re-verified directly against `MiniAudioEngine`'s current source (field-like event, lock-free unsubscribe);
`StopReceivingAsync`'s 5s `StopCapture` watchdog is confirmed a REAL bound for non-drain-thread callers
(`MiniAudioEngine.cs` offloads the blocking `Dispose()` via `Task.Run`); `DisposeAsync`'s 4-state
rig-reachability claim re-verified directly against `NoneRadioProtocol`/`RadioController` source, not
assumed; `SetPttLockAsync`'s UNLOCK direction publishing no `_keyedTransmitCompletion` registration
(unlike the LOCK direction) traced through and confirmed NOT a leak -- `DisposeAsync`'s 4-state backstop
still always fires and queues its own bounded un-key FIFO behind any in-flight one; `_pttLocked`'s
unlock-direction write is safe despite not being epoch-guarded, since `_pttLockGate` already serializes
it and `PlayWithPttAsync` never writes `_pttLocked` true; every await reachable while
`_transmitInFlight == 1` re-confirmed bounded. Round-22's 2 open items (nit 9,
the 3 decoder-command dispatch logs) re-examined with no new reasoning found -- left as previously judged.

Assumptions the agent flagged as unverified (informational, not findings): whether
`CancellationToken.Register` on an already-disposed `CancellationTokenSource` (a narrow abandoned-task
edge case) is a no-op or throws was not confirmed against runtime source -- if it does throw, the agent
traced that the consequence is confined to an already-timed-out abandoned resume task with no PTT/
`_transmitInFlight` impact; `OnDecoderRestartCriticallyOverdue`'s drain-thread deadlock-freedom claim
(already flagged unverified by round 15, out of chunk scope) not independently re-checked further.
Off-scope notes: an abandoned RX-resume completing during a LATER transmit can start capture while PTT is
keyed (own-signal into the decoder) -- inherent to the abandoned-task design rounds 18-21 accepted, not a
finding; `DisposeAsync` has no idempotency early-return, but a double call is benign (every step re-runs
harmlessly or early-returns).

**Chunk 3a round 23 fixes applied** (2026-08-21, commit `26e34f1`). Both findings fixed via `SafeLog` --
`Log.PttKeyed` and `Log.PttSkippedNoRadio` both wrapped, closing the physically-keyed-window gap.

New regression test:
`Round23_PlayWithPttAsync_PttKeyedLoggingFails_TransmitStillCompletesNormally` -- sets
`ThrowOnMessageContaining = "PTT keyed"` and asserts a normal `TransmitAsync` call now completes
successfully (not aborted) with the correct key-then-unkey PTT sequence. Mutation-verified: reverted the
`SafeLog` wrap on `Log.PttKeyed` (fresh backup taken immediately before this specific mutation, per the
round-22 process-hygiene lesson -- see `PROJECT_BRIEF.md`), reproduced the exact predicted failure
(`SimulatedLoggingProviderFailureException` propagating and aborting the transmit instead of it
completing normally), restored from that fresh backup (confirmed via `git diff --stat` showing only the
2 intended fix hunks, not a full revert), rebuilt clean, re-confirmed passing. 210/210
`ScanlineStudio.Application.Tests` passing (209 pre-existing + 1 new), full solution suite run in progress
at time of writing -- confirm clean before treating this round as closed.

Round 23 fixed 1 risk plus 1 nit -- the smallest round since round 20 in raw finding count, but the risk
finding is arguably the most sensitive site in the whole `SafeLog` sweep (the only log call inside the
physically-keyed window itself), and the round's own thorough re-verification of rounds 17-22's prior
trust-boundary claims (all confirmed still holding, not just asserted) is real audit value beyond the 2
new findings. Does NOT count as chunk 3a's 1st clean round -- round 24 is now the earliest round that can.
Twenty-two consecutive rounds (2-23) have now each found something real in this file.

**Chunk 3a round 24** (2026-08-21, independent agent, fresh context, agent `ac059513c3dffa1bc`). Verdict
EQUIVALENT-WITH-RISKS. Explicitly steered to broaden beyond the SafeLog-coverage angle (4 rounds running)
into the PTT state machine's own field interactions, cancellation-token wiring, and cross-timeout budget
composition. **[risk]** `rigIsRealAtUnlockTime` (`SetPttLockAsync`'s unlock-direction catch filter) is a
FRESH `RigId` read used to classify a failed emergency unlock -- the exact blocker-2 anti-pattern round
18 finding 5 reintroduced while fixing the missing arm in the first place. Verified against
`RadioController.DisconnectAsync` (sets `RigId` to `"none"` WITHOUT un-keying) and `RequireProtocol`
(throws synchronously once disconnected): if the CAT link drops between key and unlock, the rig is still
genuinely keyed but this filter reads `"none"` and NEVER MATCHES AT ALL -- not even the inner
belief-based gate (`_pttLocked || _pttLeftKeyedByCall || _pttUnkeyFailedOnRealRig`, which is already the
CORRECT test) ever runs, so the emergency unlock's own failure produces no log whatsoever until
`DisposeAsync`'s shutdown backstop, possibly hours later. Not a blocker: `_pttLocked` correctly stays
true regardless, so the backstop still eventually fires. Reachability caveat stated honestly: grepped all
of `src/` -- `SetPttLockAsync` currently has zero production callers (no ViewModel wires it up yet),
same latent status round 18 accepted for its own half of this fix. **[nit]** `DisposeAsync`'s backstop
un-key can burn its whole budget queueing FIFO behind an abandoned in-flight command on the backend's
single request gate (the same trade-off already documented at `PlayWithPttAsync`'s urgent un-key and its
retry) -- documentation-only, the backstop comment didn't carry the same caveat those two sibling sites
already do.

Explicitly checked and confirmed already-handled this round, stated rather than left silent (this round
put unusual weight on this section, given its explicit mandate to broaden beyond logging): a fresh `Log.*`
enumeration (65 exact invocations by this agent's counting method) found nothing new beyond the 3
decoder-dispatch logs already out of scope -- explicitly calls the SafeLog-coverage angle "mined out",
recommending no further round spend effort there; no unguarded non-log operation remains in any of the
file's 23 catch/cleanup/finally blocks (enumerated all of them); the PTT state machine's TCS-overwrite
interleaving traced both directions (`SetPttLockAsync` finishing first vs. last against an overlapping
`PlayWithPttAsync`) with the count/nullness split and Dekker fencing both holding; all cancellation-token
wiring re-verified correct at all 4 `SetPttAsync` sites; `StopReceivingAsync`'s synchronous prefix
specifically re-examined for a round-18-shaped `Task.Run` gap (the one angle explicitly flagged as
worth checking) and found NOT a real unbounded prefix for any caller that holds `_transmitInFlight`/
`_pttLockGate` -- traced through `MiniAudioEngine.ClaimCaptureSessionAsync`/`DisposeCaptureSessionAsync`
directly; budget composition re-checked and found no combination producing an unbounded hold beyond
finding 2 above. Two open items (nit 9, the 3 decoder-dispatch logs) re-examined with no new reasoning.

**Chunk 3a round 24 fixes applied** (2026-08-21, commit `46c326e`). Risk fixed: the unlock-direction catch
filter changed from `when (rigIsRealAtUnlockTime)` (a fresh RigId read) to `when (!locked)` -- the inner
belief-based gate is already the correct test and needed no device-identity filter layered on top;
`rigIsRealAtUnlockTime` removed as dead code. Nit fixed: `DisposeAsync`'s backstop comment now states the
same FIFO-queueing-behind-an-abandoned-command caveat its two sibling sites already document.

New regression test:
`Round24_SetPttLockAsync_UnlockFailsAfterCatLinkDrops_StillLatchesCriticalImmediately` -- locks
successfully on a real rig, flips `RigId` to `"none"` (simulating a CAT link drop without un-keying, the
exact `RadioController.DisconnectAsync` behavior), makes the emergency unlock itself fail, asserts the
Critical "MAY STILL BE KEYED" log fires immediately rather than only surfacing at shutdown.
Mutation-verified: reverted to the pre-fix `rigIsRealAtUnlockTime` filter (fresh backup taken
immediately before this specific mutation, continuing the round-23 process-hygiene fix), reproduced the
exact predicted failure (Critical log absent, only the initial lock's own Information entry present),
restored from that fresh backup (confirmed via `git diff --stat` showing only the 2 intended fix hunks),
rebuilt clean, re-confirmed passing. 211/211 `ScanlineStudio.Application.Tests` passing (210 pre-existing
+ 1 new), full solution suite run in progress at time of writing -- confirm clean before treating this
round as closed.

Round 24 fixed 1 risk plus 1 documentation nit -- notably the FIRST round since 19 to find something
OTHER than an unwrapped `SafeLog` site, confirming the agent's own read that the logging-coverage angle
is genuinely close to exhausted while the broader file is not yet clean. Does NOT count as chunk 3a's 1st
clean round -- round 25 is now the earliest round that can. Twenty-three consecutive rounds (2-24) have
now each found something real in this file.

**Chunk 3a round 25** (2026-08-21, independent agent, fresh context, agent `ab1124ed5a3c48f5f`). Verdict
EQUIVALENT-WITH-RISKS. Explicitly steered to keep pivoting away from SafeLog-coverage (round 24's own
verdict: exhausted) into deeper PTT state-machine interleavings not yet traced -- both findings landed
exactly there. **(1) [risk]** `pttKeyedOnRealRig` (`PlayWithPttAsync`'s own local) stayed `false` whenever
`RigId == "none"` AT THIS CALL'S OWN key time, even when a PTT lock was already engaged on a real rig
BEFORE the CAT link dropped -- the `if (_disposed)` sub-branch already correctly reasons about exactly
this ("only an already-engaged lock could have the rig keyed independent of this call, and
`pttLockedAtEntry` covers exactly that"), but that correction only applied INSIDE
`if (rigIsRealAtKeyTime)`, never reached when `RigId` was already `"none"` at entry. Concrete sequence:
`SetPttLockAsync(true)` succeeds on a real rig -> CAT link drops (`RadioController.DisconnectAsync` sets
`RigId` to `"none"` WITHOUT un-keying) -> operator starts a Transmit/Tune, which correctly skips its OWN
key command (rig already keyed) but ALSO now incorrectly believes the rig was never real -> an SWR
cutoff/manual Stop TX fires the urgent un-key with `pttKeyedOnRealRig` false -> `_pttUnkeyFailedOnRealRig`
never latches, the un-key's own failure logs at Debug ("no radio backend configured") instead of Critical,
and the round-18 retry gate never fires either. Not a shutdown leak (`_pttLocked` still correctly stays
true, so `DisposeAsync`'s backstop still eventually fires), but the operator's own immediate signal for a
still-keyed transmitter was silently downgraded to a routine Debug line. **(2) [risk]**
`pttLockedAtCleanup` (the flag deciding whether this call's own cleanup skips un-keying/RX-resume because
a lock is engaged) was snapshotted at the very TOP of the cleanup `finally`, BEFORE
`StopPlaybackWithWatchdogAsync`'s own drain wait (up to `playbackStopWaitBudget`) -- a
`SetPttLockAsync(true)` completing DURING that drain (a genuine operator lock engaged seconds after this
transmit's own body finished) was invisible to the stale snapshot, so this transmit's own
normal-completion cleanup silently un-keyed the just-engaged lock moments later. `UnkeyForCleanupAsync`'s
own epoch guard cannot catch this either -- that guard protects against a NEWER key completing WHILE the
un-key call itself is in flight, not one that already completed before the un-key call was even entered.
A genuinely different interleaving/victim than the already-documented "Known, accepted race" (that one is
unlock-racing-transmit-ENTRY, victim the transmission; this is lock-engage-racing-transmit-CLEANUP,
victim the lock), and the window is seconds wide (a full playback drain), not instruction-scale like the
residuals this file already accepts elsewhere. Safe direction (rig ends off, not on), so risk not
blocker.

Explicitly checked and confirmed already-handled this round, stated rather than left silent: a fresh
`Log.*` enumeration (64 sites by this agent's count) reconfirmed nothing new -- round 24's "mined out"
call holds; both orderings of two overlapping `SetPttLockAsync` calls traced through `_pttLockGate` and
found safe; `DisposeAsync` racing `SetPttLockAsync` specifically (as opposed to `PlayWithPttAsync`, which
round 24 already covered) traced through 4 sub-cases, all converging on the rig ending un-keyed with
belief flags correct or conservative; `_rxPendingResumeAfterUnlock`'s non-atomic test-and-clear
independently re-verified as touching no PTT field at all, confirming round 24's off-scope classification
(RX double-subscription only); cross-timeout budget composition re-checked -- max `_pttLockGate` hold
~10s, `_transmitInFlight`/`_pttLockGate` never both held by the same call, no starvation cycle found
beyond finding 2 above; `WaitAsync` bounds re-verified real (no `JsonSettingsStore`-style blocking
synchronous prefix) on all 4 `SetPttAsync` implementations directly against `HamlibRadioProtocol`/
`RigctldClientProtocol`/`RadioController` source; unguarded non-log operations in catch/cleanup/finally
re-enumerated across all such blocks, none found beyond the already-accepted `CancellationTokenSource`
constructors/`Cancel()` calls (only registration is `Task.Delay`'s own); cancellation wiring on non-PTT
operations (device resolution, settings, RX-resume) re-checked -- unwrapped public-API callers hold
neither `_transmitInFlight` nor `_pttLockGate`, so a hang there cannot cause permanent lockout.

Nits: `_keyedTransmitCount` decremented AFTER `_pttLockGate.Release()`, so round 12's `== 1` recovery
guard can transiently read a stale registration -- traced as instruction-scale and benign either way, not
fixed; `RaiseCapturePausedForTransmitChanged(false)` invokes arbitrary subscriber code while holding
`_pttLockGate` -- safe today (the one production subscriber posts to the UI thread non-blocking), flagged
as a latent class-3 risk if a future subscriber ever blocks synchronously, not fixed (no reachable bug
today); the 2 stale-arithmetic sizing comments (round 22's `InFlightKeyedTransmitWait`, round 24's
`DisposeAsync` backstop) reconfirmed still accurate as flags, no new staleness found.

**Chunk 3a round 25 fixes applied** (2026-08-21, commit `f104208`). Finding 1 fixed: `pttKeyedOnRealRig`
now baselined on `pttLockedAtEntry` immediately after that value is computed, BEFORE the
`rigIsRealAtKeyTime` branch -- the branch's own unconditional `pttKeyedOnRealRig = true` for the
this-call-keys-it case remains a strict superset, and the `if (_disposed)` sub-branch's own
`pttKeyedOnRealRig = pttLockedAtEntry` write becomes a restatement of the same value rather than the ONLY
place it was previously ever applied. Finding 2 fixed: the `pttLockedAtCleanup`/`skipUnkeyAndRxResume`
snapshot moved from the top of the cleanup `finally` to immediately before its own first actual use (right
after `StopPlaybackWithWatchdogAsync`'s await returns, just before the un-key retry decision) -- still
exactly ONE read shared by every decision point below it (round 19's own "read once" property, the reason
that fix existed in the first place, is preserved), just taken as late as the drain allows rather than
long before it. Shrinks finding 2's race to the same instruction-scale residual class this file already
accepts at its other epoch-guarded sites, not eliminated outright (documented as such in the moved
comment).

2 new regression tests:
`Round25_PlayWithPttAsync_LockEngagedThenCatLinkDrops_AbnormalTerminationStillLatchesCritical` (finding 1
-- locks on a real rig, drops RigId to `"none"`, makes an abnormal-termination transmit's own un-key
fail, asserts the Critical "MAY STILL BE KEYED" log fires rather than a benign Debug line) and
`Round25_PlayWithPttAsync_LockEngagedDuringStopPlaybackDrain_CleanupDoesNotUnkeyIt` (finding 2 -- needed a
new test double, `SignalingGatedStopPlaybackAudioEngine`, giving a deterministic happens-before signal the
instant `StopPlaybackAsync` is entered, since the fake engine has no real-time pacing and the original
loose synchronization via "has the key command landed yet" did not reliably distinguish fixed-vs-broken
behavior -- caught via the SAME mutation-testing discipline this whole chunk relies on, not by inspection;
the test was redesigned mid-round after its first version passed against the deliberately-broken code).
Both mutation-verified with a fresh per-mutation backup each: finding 1 reverted, reproduced the exact
predicted failure (Debug "no radio backend configured" instead of Critical), restored, re-confirmed;
finding 2 reverted (via a mutation-only shadow variable simulating the pre-fix early-read timing, since
the real fix's own code had already been restructured), reproduced the exact predicted failure (the
custom assertion message fired, confirming the lock was defeated), restored, re-confirmed. 213/213
`ScanlineStudio.Application.Tests` passing (211 pre-existing + 2 new), full solution suite run in progress
at time of writing -- confirm clean before treating this round as closed.

Round 25 fixed 2 real risks, both genuine PTT-state-machine races distinct from the SafeLog-coverage
pattern that dominated rounds 20-23 -- confirming round 24's own pivot was the right call and that this
angle still has real bugs left in it. Does NOT count as chunk 3a's 1st clean round -- round 26 is now the
earliest round that can. Twenty-four consecutive rounds (2-25) have now each found something real in this
file.

**Chunk 3a round 26** (2026-08-21, independent agent, fresh context, agent `a27a3c0995fddba61`). Verdict
EQUIVALENT-WITH-RISKS -- no blocker (no leaked-keyed-transmitter, transmission-destruction, or lockout
path found), 4 findings all in the state-belief/signal layer rather than the physical-PTT layer. Steered
to keep pulling on rounds 24-25's "stale read of shared state after a real-time-costing await" thread,
with `SetPttLockAsync`'s own body named as an unchecked target -- that method's own body came back clean
(every read there is fresh, taken after its own await, not a pre-await snapshot), but the SAME pattern
turned up twice more in `PlayWithPttAsync`. **(1) [risk]** `_pttLeftKeyedByCall = true`
(`PlayWithPttAsync`'s own `leaveKeyedAfterCall` write) had a guard against a NEWER KEY racing it (round 7)
but none against a CONFIRMED UN-KEY racing it -- `pttKeyedOnRealRig` is captured at key time, and this
write runs after the entire transmission plus `StopPlaybackWithWatchdogAsync`'s drain, potentially minutes
later. Concrete sequence: `TuneAsync(leaveKeyedAfterTune: true)` keys a real rig -> mid-tone the operator
calls `SetPttLockAsync(false)`, genuinely un-keying it (rig confirmed OFF) -> the tune completes normally,
`skipUnkeyAndRxResume` is `true` via `leaveKeyedAfterCall` alone (independent of the now-stale
`pttLockedAtCleanup`), so the un-key that would have re-confirmed reality never runs -> this write still
fires unconditionally, latching a permanent false "still keyed" belief on a rig that is demonstrably off.
`_pttKeyEpoch` cannot catch this -- it is deliberately bumped ONLY on the key direction, so a confirmed
UN-key is invisible to it. Round 25's own baseline (`pttKeyedOnRealRig = pttLockedAtEntry`) also widened
reachability slightly. Latent today (`leaveKeyedAfterTune: true` has no production caller). **(2) [risk]**
`TryUnkeyPttAsync`'s own abandoned `unkeyTask` (when `WaitAsync` gives up) had NO fault-observer, unlike
every OTHER abandoned task in this class (`StopReceivingAsync`, `StopPlaybackWithWatchdogAsync`,
`ResumeReceivingBoundedAsync`) -- on the single most safety-critical await in the file (its own doc
comment's words). Round 18's own design deliberately gives the command `CancellationToken.None` so it
stays queued and eventually reaches the rig once the gate frees -- but with no observer, an eventual late
FAILURE behind a wedged command surfaced only as an unobserved task exception, never logged; the whole
rationale for keeping the command uncancellable was unverifiable from the log. **(3) [nit, introduced by
round 25]** the moved `pttLockedAtCleanup` snapshot opened a SYMMETRIC window to the one it closed: an
operator's own `SetPttLockAsync(false)` completing during the SAME drain now reads `pttLockedAtCleanup` as
`false` too, so the transmit runs a REDUNDANT cleanup un-key on a rig the operator's own unlock already
confirmed off -- if that redundant attempt fails for any unrelated reason, it falsely latches
`_pttUnkeyFailedOnRealRig`. Net trade is still correct (round 25 removed a failure-class-1 bug and added a
signal-erosion nit), explicitly not a revert candidate -- same root cause as finding 1 (no
"confirmed-un-key" signal existed anywhere in the file before this round). **(4) [nit]** round 25's
`pttKeyedOnRealRig` baseline (lock engaged on a real rig, then RigId goes to `"none"`) is not matched by a
shutdown registration -- `keyedCompletion`/`_keyedTransmitCount` stay gated on `rigIsRealAtKeyTime` alone,
so `DisposeAsync` won't wait for that call's own cleanup un-key. Harmless while RigId stays `"none"`; if
`RadioController` reconnects mid-transmit, the cleanup un-key could race DI teardown, the exact hazard the
round-8 publish exists to prevent. Narrow, not a live bug today.

Explicitly checked and confirmed already-handled this round, stated rather than left silent:
`SetPttLockAsync`'s own body walked read-by-read -- no round-24/25-shaped stale read found, every
mutable-state read there is fresh; round 25's move vs. downstream `_pttLocked` readers re-verified, no new
inconsistency (the only write between the old and new read points is the urgent un-key's own
`_pttLocked = false`, and `pttLockedAtCleanup` is only consumed on paths where no urgent un-key ran);
round 25's baseline vs. double-logging re-checked, no new duplicate; unguarded non-log operations in
catch/cleanup/finally re-enumerated (the round-21 `ResetAgc()` class), none found -- all
`SamplesCaptured +=`/`-=` calls confirmed lock-free field-like events on both `MiniAudioEngine` and the
test fake; the blocking-synchronous-prefix hazard (the round-18 `Task.Run` bug class) checked directly
against both shipped backends for the PTT command specifically -- genuinely bounded, no wrapper needed,
this round's leading blocker hypothesis did not hold; synchronous throws from `SetPttAsync` (a
non-async `RequireProtocol()` expression body) confirmed inside a `try` at all 3 call sites; a fresh
`Log.*` enumeration (baseline only) reconfirmed the angle mined out; `DisposeAsync`/
`AwaitInFlightKeyedTransmitAsync`'s own state reads confirmed fresh, no stale read; `_transmitInFlight`
lockout re-confirmed bounded on every path.

Assumptions flagged by the agent (informational): `MiniAudioEngine`'s claim/dispose re-entrancy for
`OnDecoderRestartCriticallyOverdue` not independently re-verified (already flagged unverified by round 15,
out of chunk scope); finding 2's severity assumed `ThrowUnobservedTaskExceptions` is not enabled in the
host's runtime config (not checked) -- if it is, finding 2 would have upgraded to a process-crash-at-GC
risk rather than a silent-loss risk. Off-scope notes: `IsPttLocked` has no change notification despite
`_pttLocked` being flippable from three internal paths -- UI/state desync, outside the 3 failure classes.

**Chunk 3a round 26 fixes applied** (2026-08-21, commit `30d2bd6`). Findings 1-3 all closed via ONE new
mechanism: a companion `_pttUnkeyEpoch` field (the mirror of `_pttKeyEpoch`, bumped on every CONFIRMED
un-key at both existing epoch-guarded sites -- `UnkeyForCleanupAsync`'s and `SetPttLockAsync`'s own
success branches), snapshotted once by `PlayWithPttAsync` at entry (`unkeyEpochAtEntry`, alongside
`pttLockedAtEntry`). Finding 1: the `_pttLeftKeyedByCall = true` write now guarded on
`Volatile.Read(ref _pttUnkeyEpoch) == unkeyEpochAtEntry` -- skips the write (logging why) if a confirmed
un-key happened anywhere since entry. Finding 3: the retry-gated cleanup un-key call site gained the same
guard -- skips the redundant attempt entirely (treating it as already confirmed) rather than just
narrowing finding 1's own window, closing finding 3 as a side effect of the same mechanism rather than a
separate fix. Finding 2: `TryUnkeyPttAsync`'s catch gained a fault-observer `ContinueWith`
(`OnlyOnFaulted`, matching this class's 3 other established sites exactly), gated on
`!unkeyTask.IsCompleted` the same way those sites gate it. Finding 4: documented as a flagged-not-fixed
nit at the relevant code (narrow, requires a lock-then-disconnect-then-reconnect-mid-transmit sequence,
not a live bug today) -- consistent with this file's established "known consequence, accepted" pattern for
similar narrow residuals.

2 new regression tests:
`Round26_TuneAsync_LeaveKeyedAfterTune_ConfirmedUnkeyDuringDrain_DisposeDoesNotDoubleUnkey` (finding 1 --
also exercises finding 3's fix implicitly, since both share the same guard mechanism) and
`Round26_TryUnkeyPttAsync_AbandonedCommandLaterFails_ObservedNotSilentlyLost` (finding 2 -- gates the
cleanup un-key call specifically via `FakeRadioSessionService`'s `GateOnCallNumber`, lets
`TryUnkeyPttAsync`'s own `WaitAsync` time out against a short `cleanupTimeout`, then releases the gate so
the abandoned command fails, asserting the fault-observer's own log line appears). Both mutation-verified
with a fresh per-mutation backup each, reproducing the exact predicted failure in both cases (a redundant
third backstop un-key call; the fault-observer's log line never appearing), restored, re-confirmed.
215/215 `ScanlineStudio.Application.Tests` passing (213 pre-existing + 2 new), full solution suite run in
progress at time of writing -- confirm clean before treating this round as closed.

Round 26 fixed 2 real risks plus 1 nit closed as a side effect of the same fix, plus 1 more nit documented
-- both risks are genuine PTT-state-machine gaps (not logging masking), continuing rounds 24-25's pivot,
and notably the first round to explicitly verify `SetPttLockAsync`'s own body against the exact bug shape
rounds 24-25 found elsewhere and find it clean, narrowing where the remaining risk actually lives. Does
NOT count as chunk 3a's 1st clean round -- round 27 is now the earliest round that can. Twenty-five
consecutive rounds (2-26) have now each found something real in this file.

**Chunk 3a round 27** (2026-08-21, independent agent, fresh context, agent `a42ce7356bea53355`). Verdict
EQUIVALENT-WITH-RISKS -- explicitly framed as a possible inflection point (no blocker in rounds 24-26) and
asked to give round 26's own brand-new `_pttUnkeyEpoch` mechanism fresh, skeptical scrutiny. That scrutiny
found the mechanism has a real asymmetry with `_pttKeyEpoch`, and a second, independent finding surfaced
in round 25's own baseline. Not a clean round. **(1) [risk]** `_pttUnkeyEpoch`'s guard answers "did a
confirmed un-key happen since entry", not "is the rig off NOW" -- a key landing AFTER that confirmed
un-key but BEFORE cleanup is invisible to it. This is NOT the same instruction-scale residual
`_pttKeyEpoch`'s own field comment documents and accepts: `_pttKeyEpoch`'s own consumers snapshot
immediately before their own un-key await (one-await-wide window); `_pttUnkeyEpoch`'s consumer snapshots
at `PlayWithPttAsync`'s entry and acts in the cleanup `finally` -- a window spanning the ENTIRE
transmission, potentially minutes. Concrete leak: a transmit keys -> a concurrent `SetPttLockAsync(false)`
confirms the rig off (`_pttUnkeyEpoch` bumps) -> a LATER `SetPttLockAsync(true)` re-keys it (successfully,
or fails after physically keying -- either way `_pttKeyEpoch` bumps again, per round-7/8's own established
rule) -> the ORIGINAL transmit's own cleanup sees `_pttUnkeyEpoch` moved and skips its un-key entirely,
leaving a genuinely re-keyed rig un-attended, caught only by `DisposeAsync`'s backstop "which may not run
for hours" (this file's own round-18 wording). A weaker successful-re-key variant instead suppresses the
round-18 immediate retry on a cancelled/SWR-cutoff transmit specifically. Reachability: production-
unreachable today (`SetPttLockAsync` has zero production callers) -- **becomes blocker-class the moment
`SetPttLockAsync` is wired to UI.** The sibling `_pttLeftKeyedByCall` consumer has the identical structural
gap, but the agent could not construct a harmful case for it (whoever re-keys after a confirmed un-key
sets either `_pttLocked` or `_pttLeftKeyedByCall` itself, so nothing is lost) -- reported as "covered by
luck," not an independent bug. **(2) [risk]** round 25's own `pttKeyedOnRealRig` baseline is taken AFTER
the three bounded device/settings awaits, not before -- a failure in any of them (device not found, no
device configured, or the `WaitAsync` timeout, all realistic) reproduces verbatim the harm round 25 exists
to prevent: no Critical, no retry, just a Debug "no radio configured" line, because the code never
reaches the late baseline assignment at all. `_pttLocked` stays true, so `DisposeAsync`'s backstop still
catches it -- same residual safety net, same lost-immediacy harm. **(3) [nit]** `unkeyConfirmed = true` in
the retry-gated cleanup block is dead -- nothing rechecks that block's own outer `if` condition
afterward.

Explicitly checked and confirmed clean this round: independent `Log.*` enumeration (74 sites, 65 wrapped,
the 9 unwrapped all confirmed off any PTT/cleanup/fault-observer path, consistent with `SafeLog`'s own
stated criterion -- no new coverage gap); `_pttUnkeyEpoch` bump-site coverage -- exactly 2
`SetPttAsync(false)` sites exist in the class and BOTH bump it correctly nested inside their own
`_pttKeyEpoch` guard, no missing site of the round-21/23 "gap" kind; unguarded non-log operations in
catch/cleanup/finally re-enumerated (the round-21 `ResetAgc()` class), none found; a full
permanent-lockout sweep on `_transmitInFlight` re-confirmed every await bounded, including a specific
re-check of the round-18 "blocks before its first await" class against `_audioEngine.StopCaptureAsync()`
(genuinely offloaded via `Task.Run` inside `MiniAudioEngine`, so `WaitAsync` is a real bound); shared
mutable state BEYOND the PTT flags (`_isReceiving`, `_maintenanceWarningActive`, `_disposed`,
`_transmitInFlight`, the `_keyedTransmitCount`/`_keyedTransmitCompletion` pairing) all found balanced on
every path with no stale-cache surface (every path re-reads, nothing is cached).

Assumptions flagged (informational): `MiniAudioEngine`'s drain-thread-inline re-entrancy question
(already flagged unverified by round 15, out of chunk scope) not independently re-verified;
`ISstvEncoder.EncodeAsync`'s enumerable assumed not to stall indefinitely (the one unbounded PRODUCER
feeding the pump while PTT is keyed -- `EnqueueAllAsync`'s own stall timer only catches a wedged sink, not
a wedged source) -- not inspected this round, worth a future round's attention if this angle keeps
recurring. Off-scope note: a user-initiated `StopReceivingAsync` during a transmit is silently undone by
the cleanup's own RX resume -- UX wart, outside the 3 failure classes.

**Chunk 3a round 27 fixes applied** (2026-08-21, commit `963a1cd`). Finding 1: both `_pttUnkeyEpoch`
consumer sites (the retry-gated cleanup un-key skip, and -- for defense-in-depth, not because a harmful
case was found -- the `_pttLeftKeyedByCall` write skip) now ALSO require a new `keyEpochAfterOwnKeyAttempt`
snapshot (`_pttKeyEpoch`, taken right after this call's own key phase completes) to be UNCHANGED before
trusting the "already off" signal -- erring toward attempting the un-key (this file's own established
rule) whenever anyone has re-keyed since that point. Finding 2: `pttKeyedOnRealRig`'s baseline read AND
assignment both moved to this call's own true entry (before the three bounded awaits), as a bare
`_pttLocked` read separate from `pttLockedAtEntry` (which stays late, deliberately, for its own
"don't double-key an already-engaged lock" purpose -- the two locals can now genuinely differ if
`_pttLocked` changes during those awaits, which is correct). First version of this fix moved only the
READ early but left the ASSIGNMENT at its old late position -- caught immediately by the new regression
test failing against the "fixed" code, corrected before mutation-testing. Finding 3: the dead
`unkeyConfirmed = true` write removed, replaced with a comment explaining why.

2 new regression tests:
`Round27_PlayWithPttAsync_ReKeyFailsAfterConfirmedUnkeyDuringDrain_CleanupStillAttemptsUnkey` (finding 1
-- uses a re-key that FAILS after physically keying specifically, since a successful re-key would already
be caught by `pttLockedAtCleanup`'s own round-25 mechanism, needing `_pttLocked` to stay false so only the
new epoch cross-check can catch it) and
`Round27_PlayWithPttAsync_LockEngagedThenDeviceResolutionFails_AbnormalTerminationStillLatchesCritical`
(finding 2 -- an empty output-device list forces `ResolveDeviceAsync` to fail; the cleanup un-key is ALSO
made to fail, since a succeeding un-key looks identical either way and only a failing one exposes the
Critical-vs-Debug classification difference). Both mutation-verified with a fresh per-mutation backup
each, reproducing the exact predicted failure (a skipped cleanup un-key on a re-keyed rig; a Debug
"no radio configured" line instead of Critical), restored, re-confirmed. 217/217
`ScanlineStudio.Application.Tests` passing (215 pre-existing + 2 new), full solution suite run in progress
at time of writing -- confirm clean before treating this round as closed.

Round 27 fixed 2 real risks plus 1 nit -- both risks in code that is EITHER brand new this session
(`_pttUnkeyEpoch`, round 26) OR from the immediately preceding round (round 25's `pttKeyedOnRealRig`
baseline), continuing the pattern that this file's newest code is consistently where the next round's
findings live. Does NOT count as chunk 3a's 1st clean round -- round 28 is now the earliest round that
can. Twenty-six consecutive rounds (2-27) have now each found something real in this file.

**Chunk 3a round 28** (2026-08-21, independent agent, fresh context, agent `a9c1cd650326601c8`). Verdict
NOT EQUIVALENT -- 1 **blocker** (the first since round 19), plus 2 nits. Explicitly pointed at round 27's
own two fixes specifically (continuing rounds 24-27's own pattern of each round finding a bug in the
preceding round's newest code), and it worked again. **[blocker]** The two-epoch check round 27 added has
a THIRD staleness neither round 26 nor round 27 closed: `unkeyEpochAtEntry` was snapshotted at
`PlayWithPttAsync`'s own METHOD ENTRY -- before this call's own key command -- so a confirmed un-key that
happened BEFORE this call keyed the rig gets misread, at cleanup time, as "confirmed AFTER I keyed". The
pairing with `keyEpochAfterOwnKeyAttempt` (round 27's own fix) only proves "nobody re-keyed since MY key
phase" -- it says nothing about whether the confirmed un-key it's being compared against actually postdates
that key, unless BOTH epochs are read at the SAME point in time. Concrete leak: a `SetPttLockAsync(false)`
is in flight (real serial/TCP round trip) when a `PlayWithPttAsync` call (X) enters and snapshots
`unkeyEpochAtEntry` at its OLD early point -- before the unlock's own command has reached the rig. X then
issues its own key command, which queues behind the unlock's on the backend's single request gate. The
unlock reaches the rig first (rig OFF, `_pttUnkeyEpoch` bumps, all 4 shutdown-backstop flags cleared) --
then X's own key command reaches the rig (rig ON, `_pttKeyEpoch` bumps, X's own
`keyEpochAfterOwnKeyAttempt` snapshot correctly captures this). X's transmission then completes normally:
`_pttUnkeyEpoch != unkeyEpochAtEntry` is true (the unlock's bump landed after X's OLD early snapshot) AND
`_pttKeyEpoch == keyEpochAfterOwnKeyAttempt` is true (nothing re-keyed since X's own key) -- both
conditions the round-27 guard requires are satisfied, so the cleanup un-key is SKIPPED entirely, with
every shutdown-backstop flag already cleared by the unlock. **Leaked keyed transmitter with an actively
misleading log line claiming the rig is "already known to be off" -- failure class 1, the first blocker
since round 19.** The `_pttLeftKeyedByCall` consumer has the identical structural gap in the same sequence.
Reachability: requires a `SetPttLockAsync(false)` genuinely in flight at another `PlayWithPttAsync` call's
entry -- latent today (`SetPttLockAsync` has zero production callers), same status as every finding
sitting on top of it since round 24, but goes live the moment the PTT-lock UI is wired.

**[nit]** Round 27's own early `pttKeyedOnRealRig = _pttLocked` baseline (moved to fix round 27's own
finding 2) widened a DIFFERENT false-positive-Critical window from instruction-scale to the full
three-bounded-await width: lock engaged → during the awaits a confirmed unlock genuinely turns the rig off
AND `RigId` separately goes to `"none"` → this call never keys but `pttKeyedOnRealRig` stays stuck true
from the now-early baseline → cleanup attempts a doomed un-key against the null-object backend and logs a
false Critical on a rig already confirmed off elsewhere. The round-28 blocker fix does not rescue this --
that confirmed un-key's own epoch bump lands BEFORE `unkeyEpochAtEntry`'s own (also now-later) read, so the
pair sees no change and doesn't skip the doomed attempt. Narrow (needs
lock-engaged-then-confirmed-unlock-then-disconnect, all within the awaits) and `SetPttLockAsync`-gated
(latent) -- a real fix would need the BASELINE itself to be downgradeable by a confirmed un-key observed
since its own line, not just guardable at the two consumer sites the way `_pttUnkeyEpoch` already is; not
fixed this round. **[nit]** `unkeyEpochAtEntry`'s own placeholder-safety comment went stale the moment
round 27 moved `pttKeyedOnRealRig`'s own assignment earlier -- the comment's claim that the placeholder is
"false-by-default until the try reaches its own snapshot line" is no longer generally true, though the
placeholder remains safe for a different reason (every consumer requires `pttKeyedOnRealRig` true, which
requires `_pttKeyEpoch >= 1`, combined with the second epoch term, still erring toward attempting the
un-key).

Explicitly checked and confirmed clean this round: a fresh independent `Log.*` enumeration (unwrapped
sites all outside the PTT/cleanup chain, rounds 26/27's "exhausted" claim holds); unguarded non-log
operations in catch/cleanup/finally re-enumerated (the round-21 `ResetAgc()` class), none found;
`_transmitInFlight` lockout sweep re-confirmed every await bounded, release unconditional, no new
lockout; transmission-destruction sweep confirmed nothing new touches a second call's playback session;
`keyEpochAfterOwnKeyAttempt`'s OWN snapshot point specifically re-verified correct (no await between the
key bump and the read, so it can't miss this call's own bump; a concurrent bump absorbed in that
instruction window is benign) -- the genuine defect was specifically the PAIRING with the entry-time
`unkeyEpochAtEntry` snapshot, not `keyEpochAfterOwnKeyAttempt` itself.

**Chunk 3a round 28 fixes applied** (2026-08-21, commit `53d7a7a`). Blocker fixed: `unkeyEpochAtEntry`'s
read moved from method entry to the SAME point as `keyEpochAfterOwnKeyAttempt` (immediately after this
call's own key phase, no await between the two reads) -- the pair now correctly answers "since MY OWN
key: did anyone confirm off, and did anyone re-key", not two questions anchored to different, unrelated
points in time. This preserves both of round 26's own original motivating scenarios (an unlock mid-tone,
an unlock during `StopPlaybackWithWatchdogAsync`'s own drain), both of which land after this point.
First nit documented at the relevant code (flagged, not fixed -- narrow, latent, and a real fix needs a
different mechanism than the one already in place). Second nit fixed: the stale comment corrected to
explain the placeholder's real current safety argument.

New regression test:
`Round28_PlayWithPttAsync_ConfirmedUnkeyBeforeOwnKeyCommand_CleanupStillAttemptsUnkey` -- needed a new
test-double hook (`FakeRadioSessionService.OnCallStarted`, invoked with the 1-based call number the
instant `SetPttAsync` is entered, before any gate/hang check) to get a deterministic happens-before point
for racing an independent `SetPttLockAsync(false)` against a gated first call, the same problem round 25's
`SignalingGatedStopPlaybackAudioEngine` solved for the audio engine. Mutation-verified: reverted
`unkeyEpochAtEntry`'s read to its old entry-time position (fresh backup taken immediately before this
specific mutation), reproduced the exact predicted failure (the cleanup un-key call missing from the
expected sequence, `[false, true]` instead of `[false, true, false]`), restored, rebuilt clean,
re-confirmed passing. 218/218 `ScanlineStudio.Application.Tests` passing (217 pre-existing + 1 new), full
solution suite run in progress at time of writing -- confirm clean before treating this round as closed.

Round 28 fixed the file's first BLOCKER since round 19 -- a real leaked-keyed-transmitter path in code
that was only 1 round old (round 27's own fix), continuing the pattern that this file's newest code is
consistently where the next round's findings live, now proven at blocker severity too, not just
risk/nit. Does NOT count as chunk 3a's 1st clean round -- round 29 is now the earliest round that can.
Twenty-seven consecutive rounds (2-28) have now each found something real in this file.

**Chunk 3a round 29** (2026-08-21, independent agent, fresh context, agent `a64020af236f435ba`). Verdict
EQUIVALENT-WITH-RISKS -- no blocker. Traced round 28's own fix in detail (no await between the key bump
and the two epoch reads; every snapshot-skipping exit path leaves both epoch locals at safe placeholder
defaults, because `abnormalTermination` on those paths forces `skipUnkeyAndRxResume` false, which makes
the un-key-skip consumer unreachable with a placeholder, and the `_pttLeftKeyedByCall`-skip consumer's own
skip requires `pttKeyedOnRealRig` true, which requires `_pttLocked` having been true, which requires
`_pttKeyEpoch >= 1` -- so the skip is never wrongly taken) -- **round 28's fix holds, confirmed sound.**
**[risk]** `SetPttLockAsync`'s own `pttCommand` (its key/un-key command) had no fault-observer when
`WaitAsync` gives up on it -- a fourth instance of the exact round-26 finding, at the one site round 26's
own enumeration comment claimed didn't exist ("every OTHER abandoned task... StopReceivingAsync,
StopPlaybackWithWatchdogAsync, ResumeReceivingBoundedAsync" -- this one wasn't in that list). On the
UNLOCK direction specifically, round 18's own design deliberately gives the command
`CancellationToken.None` so it "stays queued and eventually reaches the rig" -- meaning a late failure is
a real, expected outcome, and nothing observed or logged it: an operator hits emergency unlock during a
wedged CAT link, gets the immediate Critical (correct), but is never told whether the retry that reaches
the rig 30 seconds later actually succeeded or failed. Not a leak (state flags latch correctly, and
`DisposeAsync`'s backstop still fires) -- observability only, on the one escape-hatch path this whole
chunk exists to keep observable. **[nit]** `_pttUnkeyEpoch`'s own field doc comment (and 2 consumer
comments) still described the PRE-round-28 semantics ("snapshotted once... at entry") even though round 28
deliberately moved that read -- exactly the kind of stale safety-argument drift round 28's own comment (at
its declaration) already flagged as a known risk class in this file. The local's own name
(`unkeyEpochAtEntry`) no longer matched its sibling's accurate name (`keyEpochAfterOwnKeyAttempt`) either.
**[nit, re-confirmed]** round 28's own documented-not-fixed nit (the early `pttKeyedOnRealRig` baseline's
own false-Critical window) independently re-derived and confirmed still exactly as round 28 characterized
it -- correctly left deferred, not something round 29 found a clean way to close either. **[nit]** the
baseline's own belief test (`_pttLocked` alone) is narrower than the file's own equivalent "did this class
believe something was keyed" test used at the unlock-direction catch (`_pttLocked || _pttLeftKeyedByCall
|| _pttUnkeyFailedOnRealRig`) -- a prior `TuneAsync(leaveKeyedAfterTune: true)` leaving the rig keyed via
`_pttLeftKeyedByCall` alone, followed by a RigId disconnect, would baseline a new transmit's
`pttKeyedOnRealRig` false, downgrading its own cleanup Critical to Debug. Verified not a leak
(`_pttLeftKeyedByCall` itself survives, so `DisposeAsync`'s backstop still fires) -- same signal-quality
class round 25 exists to protect, one flag short.

Independent re-enumerations, both clean: all `Log.*` call sites (9 unwrapped, all on normal
entry/propagation paths, none in a catch/cleanup/finally/fault-observer, none reachable with PTT keyed);
unguarded non-log operations in catch/cleanup/finally (the round-21 `ResetAgc` class) across all 34
catch/finally blocks, nothing production-reachable found. Also explicitly considered and rejected as a
finding: the lag between an un-key CONFIRMING at the rig and its own `_pttUnkeyEpoch` bump (both sites) --
traced and confirmed awaitless/instruction-scale, same accepted-residual class as `_pttKeyEpoch`'s own
documented window; and `_pttLocked = locked;`'s own unconditional write outside the epoch guard at the
unlock site -- confirmed safe, since `_pttLockGate` serializes all `SetPttLockAsync` calls and
`PlayWithPttAsync` never writes `_pttLocked = true` itself, so no concurrent writer can stomp it.

**Chunk 3a round 29 fixes applied** (2026-08-21, commit `ab2637f`). Risk fixed: `pttCommand`'s declaration
moved outside the `try` (a local declared inside a `try` is not in scope in that try's own `catch`
blocks, so this was needed regardless) as a nullable `Task?`, with the identical `ContinueWith`
fault-observer pattern (`OnlyOnFaulted`, gated on `!IsCompleted`) added to BOTH catch arms (engage and
unlock direction), matching the class's other 4 established sites exactly. First nit fixed: the field doc
and both consumer comments corrected to describe the actual round-28 semantics, and the local renamed
`unkeyEpochAtEntry` → `unkeyEpochAfterOwnKeyAttempt` throughout, matching its sibling's naming so a future
round can't be misled by the name alone the way round 28's own bug partly stemmed from a mismatch between
the old name and the old (wrong) semantics. Second nit: no action (already correctly deferred, re-stated).
Third nit fixed: baseline widened from `_pttLocked` alone to the full established belief test
(`_pttLocked || _pttLeftKeyedByCall || _pttUnkeyFailedOnRealRig`).

New regression test:
`Round29_SetPttLockAsync_AbandonedCommandLaterFails_ObservedNotSilentlyLost` -- gates the PTT command via
`FakeRadioSessionService`'s existing `Gate`/`GateOnCallNumber`, lets `SetPttLockAsync`'s own `WaitAsync`
time out against a short `cleanupTimeout`, then releases the gate so the abandoned command fails,
asserting the fault-observer's own log line appears. Mutation-verified: reverted the fault-observer
attachment on the engage-direction catch arm (fresh backup taken immediately before this specific
mutation), reproduced the exact predicted failure (the log line never appears, `WaitForAsync` times out),
restored, rebuilt clean, re-confirmed passing. **Also found and fixed, during this verification pass, a
genuine pre-existing race in this file's own shared `RecordingLogger<T>` test double**: its `Entries`
property was a plain `List<T>`, and this class's own round-26/28/29 fault-observer fixes all log from a
`ThreadPool` continuation concurrently with a test's `WaitForAsync` polling loop enumerating that same
list on the test thread -- an intermittent `InvalidOperationException` ("Collection was modified"),
caught by this exact new test failing once on a full-suite run despite passing standalone. Fixed by
switching `Entries` to `ConcurrentBag<(LogLevel, string)>` (drop-in compatible with every existing
`.Contains`/`.Any`/`.Clear()` call site in this file) -- confirmed fixed by 3 consecutive clean full runs
of `ScanlineStudio.Application.Tests` afterward, not just 1. 219/219 `ScanlineStudio.Application.Tests`
passing (218 pre-existing + 1 new), full solution suite run in progress at time of writing -- confirm
clean before treating this round as closed.

Round 29 confirmed round 28's blocker fix is genuinely sound (the first round this chunk has spent
explicitly re-verifying a PRIOR round's blocker fix rather than finding a new one), found one more real
risk in the same "abandoned task with no fault-observer" class round 26 introduced -- proving that
enumeration was ALSO incomplete, matching the earlier round-21/22/23 `SafeLog`-coverage pattern one more
time -- plus caught and fixed a genuine test-infrastructure race as a side effect of its own
mutation-verification discipline. Does NOT count as chunk 3a's 1st clean round -- round 30 is now the
earliest round that can. Twenty-eight consecutive rounds (2-29) have now each found something real in
this file.

**Chunk 3a round 30** (2026-08-21, independent agent, fresh context, agent `a024384d074169c6a`). Verdict
NOT CLEAN -- 1 risk + 2 nits, no blocker, no leaked-transmitter/transmission-destruction/stall found. All
three findings are in or directly downstream of round 29's own two fixes; the rest of the file swept
clean against the three failure classes. **[risk]** round 29's engage-arm fault-observer for
`SetPttLockAsync`'s own `pttCommand` was attached AFTER the recovery-un-key's own await (up to
`_cleanupTimeout`, ~5s) -- but both shipped backends serialize on a single approximately-FIFO request gate
(this file's own round-19/24 comments state this), so the recovery un-key cannot make progress until the
abandoned key command clears that SAME gate. That makes "the key command completes during the recovery
await" not an unlucky interleaving but the EXPECTED ordering whenever the recovery does anything at all --
so by the time the old placement's own `IsCompleted` check ran, it was almost always already `true`, and
the observer almost never actually attached on the exact path it was written for. This is the ONE
fault-observer site in the class with an await between the triggering throw and the check; every sibling
site attaches immediately with no intervening await and is correct as-is. Harm is diagnostic-only, not
state (the epoch bump/flag latch/Critical log already ran before the recovery), and the lost fault still
reaches `Program.cs`'s own `UnobservedTaskException` handler (verified: logs and calls `SetObserved()`, no
crash) -- just detached from context and without round 29's own attribution. **[nit]** round 29's widened
`pttKeyedOnRealRig` baseline (`_pttLocked || _pttLeftKeyedByCall || _pttUnkeyFailedOnRealRig`) was silently
re-narrowed back to `pttLockedAtEntry` alone 130 lines later, in the `if (_disposed)` branch -- exactly the
signal-erosion scenario round 29's own widening exists to prevent, occurring at the specific moment
(shutdown-adjacent) the signal matters most. Not a leak (`_pttLeftKeyedByCall` itself survives, so
`DisposeAsync`'s backstop still fires). **[nit]** round 29's widening also feeds the sticky,
non-self-clearing `_pttUnkeyFailedOnRealRig` back in as an input -- once one real un-key failure has
latched it and the operator later disconnects (every un-key against the null-object backend then throws
synchronously, so it can never be CONFIRMED to clear the flag), every SUBSEQUENT transmit's cleanup
re-attempts a doomed un-key and re-emits a fresh Critical, indefinitely, not just once. Defensible as
correct-by-belief (the underlying condition — a rig that failed to un-key and is now unreachable — is
genuinely still unresolved, unlike the actual false positives rounds 4/19/29 fixed), but flagged as the
same signal-erosion SHAPE nonetheless.

Explicitly checked and confirmed already-handled this round: round 29's unlock-direction fault-observer
arm (no intervening await, correct as-is); the one catch-arm coverage hole
(`locked == true && rigIsRealAtKeyTime == false`, i.e. `RigId == "none"`) where neither catch arm matches
at all -- verified benign, since both no-radio paths throw synchronously and produce no Task to observe;
round 29's widened baseline re-checked for a new double-attempt scenario -- none found, widening only
makes the `pttKeyedOnRealRig`-qualified arms reachable more often, and every one of those already errs
toward attempting the un-key; a fresh independent `Log.*` enumeration (69 sites, the same 9 unwrapped sites
as prior rounds, all on normal entry/propagation paths outside any catch/cleanup/finally); unguarded
non-log operations in catch/cleanup/finally (the round-21 `ResetAgc()` class) re-enumerated across all 34
catch/finally blocks, nothing production-reachable found; `_keyedTransmitCount` balance re-verified
(increment/decrement both gated on `keyedCompletion is not null`, decrement always precedes
`TrySetResult`); budget composition re-checked (`_pttLockGate` max hold ~15s, `_transmitInFlight` bounded
on every await except the sample pump which is itself bounded by `ct`/`MaxTuneDuration`/
`PlaybackStallTimeout`, no cross-acquisition deadlock); round 28's epoch-snapshot relocation re-verified
independently and still holds.

**Chunk 3a round 30 fixes applied** (2026-08-21, commit `9ddb5d3`). Risk fixed: the whole
`if (pttCommand is { IsCompleted: false })` fault-observer block moved from AFTER the recovery-un-key
attempt to immediately after the catch's own state-latching and Critical log, BEFORE the recovery block --
`pttCommand` is fully assigned by that point (this catch only runs once the command has been issued), so
nothing about the earlier placement depended on the recovery step. First nit fixed: the `if (_disposed)`
branch's own assignment widened to `pttLockedAtEntry || _pttLeftKeyedByCall || _pttUnkeyFailedOnRealRig`,
matching the entry baseline's own three-flag test while keeping `pttLockedAtEntry`'s own late/fresh read
for the `_pttLocked` term specifically (that branch's whole reason to exist). Second nit: documented as a
considered-and-accepted trade-off at the entry baseline's own comment, not changed -- a real fix would
need to distinguish "the same still-latent failure repeating" from "a genuinely new event" (e.g.
de-duplicating by `_pttUnkeyEpoch`'s own value at latch time), which trades a repeated-but-true alarm for a
real risk of under-warning if implemented wrong; a persistent Critical was judged the more conservative
failure mode for a possibly-still-transmitting rig.

New regression test:
`Round30_SetPttLockAsync_KeyCommandFailsDuringRecoveryUnkey_ObservedNotSilentlyLost` -- needed a SECOND
independent test-double gate (`FakeRadioSessionService.Gate2`/`GateOnCallNumber2`, new this round) to
control the key command's and the recovery un-key's own release timing separately and deterministically.
An EARLIER version of this test shared one gate between both calls and relied on their natural completion
order after a simultaneous release -- **that version passed reliably against the UN-FIXED code across 5
repeated runs**, caught only because mutation-testing revealed it wasn't actually distinguishing
fixed-from-broken; redesigned with the two-gate mechanism before mutation-testing was retried, which then
correctly reproduced the exact predicted failure (`WaitForAsync` timing out, the log line never appearing)
reliably across 3 repeated runs. Restored, rebuilt clean, re-confirmed passing across 3 repeated runs
(both the isolated filter and the full `Application.Tests` suite) given the test's own timing-sensitive
design. 220/220 `ScanlineStudio.Application.Tests` passing (219 pre-existing + 1 new) across 3 repeated
runs. Full solution suite hit a pre-existing "Test host process crashed" flake on BOTH of 2 attempts,
positioned around `ScanlineStudio.Core.Sstv.Tests` under heavy parallel load alongside
`Core.Audio.MiniAudio.Tests`/`UI.Tests` -- confirmed unrelated to this round's changes (which only touch
`SstvSessionService.cs`/its own test files) by running `Core.Sstv.Tests` standalone, which passed cleanly
with its full 1022/1023 tests in 10m8s; `Application.Tests` itself also passed cleanly (220/220) in both
full-suite attempts despite the unrelated crash elsewhere. Worth a separate look at CI/local resource
contention if it recurs on a future round; not blocking this one.

Round 30 fixed 1 more risk in round 29's own newest code (continuing the established pattern) plus 2
nits, one of which was silently re-undoing round 29's own fix in a second location -- and along the way
demonstrated, first-hand and via its OWN mutation-testing discipline, exactly why a test that merely
"passes" isn't sufficient proof of anything: a plausible-looking but non-deterministic regression test
would have shipped silently broken if the process hadn't caught it before commit. Does NOT count as
chunk 3a's 1st clean round -- round 31 is now the earliest round that can. Twenty-nine consecutive rounds
(2-30) have now each found something real in this file.

**Chunk 3a round 31** (2026-08-21, independent agent, fresh context, agent `aff56af46b173c70f`). Verdict
NOT CLEAN -- 1 risk + 3 nits, no blocker. **[risk]** `PlayWithPttAsync`'s own PTT KEY command (the
direct twin of `SetPttLockAsync`'s `pttCommand`, rounds 29/30) had no fault-observer -- and this is
strictly more reachable than every prior instance of this class: `SetPttLockAsync` has zero production
callers, while this line sits on `TransmitAsync`/`TuneAsync`'s live path. It is also, by this method's own
established reasoning, the ONE abandoned command in the file most likely to have physically keyed the rig
before failing. Round 30's own new enumeration comment explicitly lists the class's sibling sites and
asserts completeness -- this line wasn't among them, the same shape as round 26's enumeration being
incomplete → round 29. Harm is diagnostic/attribution only (state handling was already correct: epoch
bump, `abnormalTermination` → urgent un-key). The auditor also flagged 5 more sites in the same class as
worth fixing in the same pass (`StartPlaybackAsync`'s own abandoned open, `StopCaptureAsync` in the
disposed-abandoned-resume branch, 3 `Task.Run(...)` settings/device wrappers) -- deliberately NOT pursued
this round (see below). **[nit]** round 30's own new comment claimed `pttCommand` is "fully assigned by
this point (this catch only runs once the command has been issued)" -- false on the synchronous-throw
path, which round 29's OWN comment at the same variable's declaration already correctly states. The CODE
was always correct (the null-tolerant pattern already handles it), only the comment was wrong -- but given
this chunk's own history of stale claims causing later confusion (round 28's blocker, round 29's stale
`_pttUnkeyEpoch` doc), worth correcting on sight. **[nit]** `pttLockedAtEntry` is named for a read that is
deliberately NOT at method entry (round 27 documented this as intentional) -- the genuinely early
`_pttLocked` read is a separate, unnamed-by-comparison value feeding `pttKeyedOnRealRig`'s own baseline.
Exactly the naming-drift class round 29 already fixed once for `unkeyEpochAtEntry` →
`unkeyEpochAfterOwnKeyAttempt`. **[nit]** `SetPttLockAsync` released `_pttLockGate` BEFORE clearing its own
`_keyedTransmitCount` registration -- an instruction-scale window where a new `SetPttLockAsync(true)` call
could acquire the gate, publish, and increment the count while the previous call's own registration was
still counted, making round-12's own recovery guard (`Volatile.Read(ref _keyedTransmitCount) == 1`) read a
spurious `2` and SKIP the recovery un-key on a rig its own key command may have physically keyed -- the
precise harm round 12's fix exists to prevent. Same accepted-residual CLASS `_pttKeyEpoch`'s own field doc
already documents (instruction-scale, undocumented until now) -- verified closeable for free by reordering
rather than just flagging.

Explicitly checked and confirmed already-handled this round: round 30's own fix A (observer moved before
the recovery await) re-verified line-by-line -- `pttCommand` provably either null or fully assigned on
every path reaching that catch (the guarded try has exactly two statements), moving it earlier interacts
with nothing else in the catch (state-latch/Critical log precede it, the guarded recovery await follows
and depends on none of it); round 30's own fix B (widened `if (_disposed)` baseline) re-checked for a new
false-positive/double-attempt -- none found, every reachable arm already errs toward attempting the
un-key; a fresh independent `Log.*` enumeration (9 unwrapped sites, identical to rounds 29/30, all outside
any catch/cleanup/finally/fault-observer); unguarded non-log operations in catch/cleanup/finally (the
round-21 `ResetAgc()` class) re-enumerated, nothing production-reachable; `EnqueueAllAsync`'s own loop
checked against `IAudioEngine.EnqueuePlaybackSamples`'s own documented `0..samples.Length` contract --
not a finding against this file; both event-raise sites (`TransmitProgressChanged`,
`CapturePausedForTransmitChanged`) checked against their own documented "must not block"/threading
contracts in `ISstvSessionService.cs` -- contract satisfied; budget composition and
`_keyedTransmitCount`/epoch-guard balance re-derived independently, still erring toward attempting the
un-key on every interleaving. One open question the agent flagged but did NOT resolve: whether these
abandoned `.WaitAsync` source tasks additionally surface as `UnobservedTaskException` (round 30's own
write-up asserted they don't, citing `Program.cs`'s handler; the agent's own reading of .NET's
`CancellationPromise` completion action suggests the same conclusion but wasn't independently confirmed)
-- doesn't change any finding's severity or fix, only a secondary-harm detail.

**Chunk 3a round 31 fixes applied** (2026-08-21, commit `92dc638`). Risk fixed: `PlayWithPttAsync`'s own key
command hoisted into a `Task? keyCommand` local, with the identical `ContinueWith`/`OnlyOnFaulted`/
`!IsCompleted`-gated fault-observer pattern used at the other sites. The 5 additional lower-weight sites
the auditor flagged were deliberately NOT pursued this round -- different abandoned-task shapes (audio
engine opens, settings/device `Task.Run` wrappers) needing individual assessment of whether the SAME
"PTT command" fault-observer pattern even applies, kept as a noted off-scope item rather than expanding
this round's scope; a future round can revisit if the enumeration keeps recurring. First nit fixed: the
inaccurate "fully assigned" claim corrected. Second nit fixed: `pttLockedAtEntry` renamed to
`pttLockedBeforeKeyDecision` throughout, with a comment explaining why. Third nit fixed: the
`_keyedTransmitCount` decrement moved to run inside the SAME `finally` as `_pttLockGate.Release()`,
immediately before it (previously a separate OUTER `finally`, running after) -- this also let the now-
redundant outer `try`/`finally` wrapper be removed entirely, since a single `finally` already guarantees
the same thing the two nested ones did.

New regression test:
`Round31_PlayWithPttAsync_AbandonedKeyCommandLaterFails_ObservedNotSilentlyLost` -- gates the key command,
lets `PlayWithPttAsync`'s own `WaitAsync` time out against a short `cleanupTimeout`, then releases the gate
so the abandoned command fails, asserting the fault-observer's own log line appears. Mutation-verified:
reverted the fault-observer attachment (fresh backup taken immediately before this specific mutation),
reproduced the exact predicted failure (`WaitForAsync` timing out, the log line never appearing), restored,
rebuilt clean, re-confirmed passing across 3 repeated runs. The `_keyedTransmitCount` reordering fix
deliberately NOT given a dedicated regression test -- documented as an instruction-scale race needing
test-only hooks well beyond what a pure reordering fix justifies (it cannot introduce a NEW single-threaded
bug; verified by inspection plus the existing suite continuing to pass, matching this file's own
established treatment of `_pttKeyEpoch`'s identical residual class). 221/221
`ScanlineStudio.Application.Tests` passing (220 pre-existing + 1 new) across 3 repeated runs, full solution
suite run in progress at time of writing -- confirm clean before treating this round as closed.

Round 31 fixed the highest-reachability instance yet of the "abandoned task with no fault-observer" class
(rounds 26/29/30's own pattern) -- the first instance of this specific class with LIVE production callers,
not a latent `SetPttLockAsync`-gated one -- plus a genuine correctness fix (the `_keyedTransmitCount`/gate
reordering) closing an instruction-scale residual for free rather than just documenting it. Does NOT count
as chunk 3a's 1st clean round -- round 32 is now the earliest round that can. Thirty consecutive rounds
(2-31) have now each found something real in this file.

## Chunk 3a round 32 (2026-08-21) -- CLEAN of blockers/risks; 4 nits, fixed; chunk closed by user decision

**VERDICT: EQUIVALENT-WITH-RISKS -- no blocker, no risk in the three tracked failure classes (leaked
keyed transmitter, transmission destruction, unbounded stall/lockout); 4 nits.** This is the first round
in 32 (round 1 through round 32) to find nothing in the three failure classes this chunk exists to close.

Re-verification: round 31's fault-observer placement, the `_keyedTransmitCount`/gate-release reordering,
and the outer try/finally removal were all independently re-traced and confirmed correct on every axis
(fault-observer attaches with no intervening await; the reordering's 8 exit paths all leave
`DisposeAsync`'s backstop correctly informed; the try/finally collapse is syntactically and semantically
sound -- nothing entered/left the guarded region, only the two finallys' relative order changed, which IS
the fix). Round 31's own deferred list (5 lower-weight "abandoned task" sibling sites) was assessed one by
one: all 5 are genuinely diagnostic-only (settings/device reads, capture-side cleanup, no rig-state side
effects) -- none belong to the three failure classes, all remain nits.

Findings, all nits, all fixed:
1. Indentation left unreflowed by round 31's try/finally collapse (`SstvSessionService.cs:387-820`, now
   corrected) -- the try body was one indent level too deep (col 16 instead of col 12) throughout, with
   one comment block split across two indent levels mid-sentence. Fixed: reflowed the entire try/finally
   body one level shallower, uniformly.
2. Stale local name in a test comment (`SstvSessionServicePttSafetyTests.cs:1950`) -- still said
   `pttLockedAtEntry` after round 31's production-code rename to `pttLockedBeforeKeyDecision`. Fixed.
3. Stale "outermost finally" wording (`SstvSessionService.cs:369`) -- described the two-finally shape
   round 31 removed. Fixed: "the finally below."
4. Round 30's "verified benign" claim for the engage-arm catch's device-identity filter
   (`rigIsRealAtKeyTime`) was not unconditional -- `RadioController.ConnectAsync` assigns `_protocol`
   before `_rigId` (`RadioController.cs:93-94`), so a `RigId == "none"` window with a real protocol
   already assigned is genuinely reachable, and a key command failing in that window would leave every
   shutdown-backstop flag untouched with no log at all -- the same anti-pattern round 24 already fixed
   on the twin unlock arm. Zero production callers reach `SetPttLockAsync`'s engage direction today, so
   this stayed a nit (explicitly flagged by the auditor to upgrade to a risk the moment it gets one).
   Fixed by filtering on `locked` instead, mirroring round 24's identical fix to the unlock arm exactly;
   `rigIsRealAtKeyTime` itself is unchanged and still used for the registration-publish gate a few lines
   above, which genuinely needs the device-identity check.

No dedicated regression test for finding 4 (currently unreachable -- zero production callers, same
judgment call as several earlier accepted-but-untested residuals in this file, e.g. round 9's). Findings
1-3 are pure mechanical/textual changes with no new correctness surface, verified by a clean build (0
errors, 0 warnings) and the full `Application.Tests` suite passing unchanged (221/221, no new test).

## Chunk 3a CLOSED (2026-08-21) -- by explicit user decision, not the formal 2-consecutive-clean-round gate

**32 rounds, ~30 real fixes** (leaked-keyed-transmitter-class blockers/risks in the large majority,
narrowing to instruction-scale residuals and documentation nits by the end). Round 32 is the first round
to find zero blockers/risks in the three tracked failure classes -- a genuine convergence signal after 31
straight rounds each finding something real (rounds 2-31 unbroken). Per CLAUDE.md SS7, formally closing
this chunk requires 2 CONSECUTIVE clean rounds; round 32 would be the 1st. The user, informed of round
32's clean verdict and the length of the chain, explicitly chose to fix round 32's 4 nits and close now
rather than dispatch a round 33 purely to satisfy the formal gate -- **a deliberate, recorded exception**,
matching the precedent already set for Batch 1's own re-audit closure (`PROJECT_BRIEF.md`, "Batch 1
RE-AUDIT" section: closed 4 rounds deep on a clean-but-not-doubly-confirmed round, by explicit user
choice). If `SstvSessionService.cs`'s PTT lifecycle is revisited later, start a fresh re-audit rather than
assuming the formal gate was met.

Also decided at this point: future substantive-fix rounds in this project (not just this chunk) should
ask the auditor to draft the actual fix code directly, rather than the calling session re-deriving a fix
from the auditor's prose description -- rounds 24-31 were 8 straight rounds where a hand-derived fix for
one round's finding introduced the bug the next round caught. See `feedback_ask_auditor_for_code_fixes`
in auto-memory. Round 32's own 4 nits were mechanical enough (renames, comment text, indentation, a
one-line filter swap mirroring an already-established pattern) not to need this treatment.

Chunk 3b (`RadioController.cs`+`TcpTransport.cs`) and chunk 3c (`RigctldClientProtocol.cs`+
`HamlibRadioProtocol.cs`) remain open, not started.

## Chunk 3b round 1 (2026-08-21)

First round on `RadioController.cs`+`TcpTransport.cs`. Both process changes chunk 3a's close decided
applied from this round: the auditor drafted the actual fix code directly, and the round closed with an
explicit go-for-production question (answer: no, not as-is). No legacy counterpart (CAT/rig control is a
pure client of external backends, CLAUDE.md SS2) -- judged purely on internal-invariant correctness.

**2 blockers, 3 risks, several nits.** Both blockers are silent-wrong-state bugs on realistic paths (no
exotic timing needed):

1. **[blocker]** `DisconnectAsync`'s whole teardown was gated on `_protocol is not null`, false for the
   entire reconnect-backoff window (`SafeDisposeProtocolAsync` nulls it, and it can stay null
   indefinitely if the factory keeps failing). A disconnect landing there skipped the whole second
   block: `RigId` stayed at the live rig's id, no `Disconnected` event, `LastKnownState` never cleared --
   and it never healed, since every later `DisconnectAsync` saw the same null `_protocol`. Concretely: a
   user pressing Disconnect during a reconnect-backoff window could no longer transmit at all
   (`SetPttAsync` throws via `RequireProtocol` reading the stale non-`"none"` `RigId`) until a
   *successful* reconnect happened first. Fixed with a new `_sessionActive` bool, set independently of
   `_protocol`'s own nullness, gating the teardown block instead.
2. **[blocker]** A cancel landing mid-`stream.ReadAsync` in `TcpTransport.ReadAsync` left the socket
   connected with the read cursor exactly where the in-flight response's bytes would land -- the next
   `ReadLineAsync` call (the following poll) silently consumed that stale response instead of its own,
   producing a plausible-looking but permanently wrong frequency/PTT readback with no exception ever
   raised, and `RadioController` never sees a transport error to reconnect on. Fixed by catching
   `OperationCanceledException` around the read and aborting the connection (closing the socket,
   resetting the buffer) instead of trying to preserve it -- turns a silent, permanent desync into a
   loud, self-healing reconnect on the next `OpenAsync`. `IRadioTransport`'s own buffer-survival-contract
   doc comment updated to state this explicitly (cancellation is NOT the "caller got its line and
   stopped" case that contract protects), and `FakeRadioTransport` updated to reproduce the same
   connection-abort-on-cancel semantics, per the interface's own "no more forgiving approximation" rule.

**3 risks**, all fixed: (a) a failed `OpenAsync` could leave `_client` set with `_stream` null if
`GetStream()` threw after `ConnectAsync` succeeded -- `RigctldClientProtocol.EnsureConnectedAsync`'s
retry-on-the-same-instance pattern would then leak the live socket; fixed by moving `GetStream()` inside
the same try as `ConnectAsync`. (b) The poll loop wrote `_protocol` unconditionally on both the dispose
and reconnect paths, with no lock against a concurrent `ConnectAsync`/`DisconnectAsync` -- latent today
(single caller) but the precondition for an orphaned unstoppable poll loop; fixed with
`Interlocked.CompareExchange` claiming the slot on both paths. (c) `DisconnectAsync`'s
`await loopTask` was unbounded, and `HamlibRadioProtocol`'s own doc comment states cancellation is
honored only at its semaphore boundary -- a wedged native CAT call could block `DisconnectAsync` (and
therefore `DisposeAsync`, and therefore app shutdown) indefinitely; fixed with a 10s
`WaitAsync(PollLoopShutdownTimeout)`, safe only because of fix (b)'s CompareExchange guard against a
straggler clobbering a later session.

Nits fixed alongside: `ConnectAsync`'s `ct` was never checked; a `ResolveProtocol` failure during
`ConnectAsync` never published `Failed` (subscribers latched on `Connecting` forever); `ReadAsync` never
observed `ct` while draining already-buffered bytes; `CloseAsync` didn't reset the read-buffer offset/
length (folded into a new shared `AbortConnection()` helper both `CloseAsync` and the cancel-path use).
Left queued, not fixed: `_disposed` check/set not atomic in either class's `DisposeAsync`; `LastKnownState`
throws `ObjectDisposedException` after disposal instead of returning null.

Both blockers got new regression tests (`RadioControllerTests.DisconnectAsync_DuringReconnectBackoff
NullProtocolWindow_StillTearsDownFully`, `TcpTransportTests.ReadAsync_CancelledMidRead_AbortsConnection_
RatherThanDesyncingTheBuffer`), each mutation-verified independently (reverted the fix, confirmed the
exact predicted failure -- old gate stranding `RigId`/never publishing `Disconnected`; `IsOpen` staying
`true` after a cancelled read -- then restored).

**Real flake found and fixed along the way, not part of the audit itself:** the new cancellation test
(one more concurrently-open loopback socket, in a different xUnit collection than
`RigctldDummyRigIntegrationTests`) made a pre-existing race in that file's dummy-`rigctld`-process
lifecycle newly visible -- roughly 15-20% of full `ScanlineStudio.Core.Radio.Tests` runs failed with
"rigctld connection closed by remote host" (a real external subprocess quirk, reproduced with
`CancellationToken.None` throughout, so unrelated to either blocker fix's own cancellation-path code).
Confirmed by isolation testing (22 runs with the new test excluded: 0 failures; 25 runs with it included:
~5 failures, always the 2nd/3rd test in the sequential class, never the 1st) that this was a pre-existing
test-infrastructure fragility exposed rather than a defect in the audited production code. User chose to
harden it now rather than defer: `RigctldDummyRigIntegrationTests` refactored around a
`RunAgainstDummyRigAsync` helper that retries the whole dummy-process-plus-assertion body (fresh process,
fresh port) up to 3 times on `IOException`. Confirmed clean across 25 consecutive full
`ScanlineStudio.Core.Radio.Tests` runs after the fix (0 failures).

Full solution suite green: `ScanlineStudio.Core.Radio.Tests` 123/123, `ScanlineStudio.Core.Sstv.Tests`
1022/1022 (1 unrelated intentional skip), every other project passing. Committed as `710236f`.

Round 1 found real blockers, so it does not count toward the 2-consecutive-clean-round gate. Round 2 is
next -- the earliest round that can start that count.

## Chunk 3b round 2 (2026-08-21)

Fresh, independent re-derivation against the post-round-1 code (not a re-read of round 1's writeup),
per this project's standing per-round discipline. Found real gaps -- one of them in round 1's OWN fix,
not something round 1 missed elsewhere.

1. **[risk]** Round 1's `PollLoopShutdownTimeout` bound (10s) on `DisconnectAsync`'s wait for the poll
   loop task was defeated one line later: the very next statement, `await protocol.DisposeAsync()`, was
   still unbounded, and `HamlibRadioProtocol.DisposeAsync` takes its own semaphore with no token/timeout
   -- held for the whole duration of a wedged native call. A CAT device wedged mid-poll still hung app
   shutdown indefinitely; round 1's own fix bought exactly 10 seconds and a log line before hitting this.
   Fixed with a second bounded wait, `ProtocolDisposeTimeout` (10s).
2. **[risk]** `ConnectAsync`'s `ct.ThrowIfCancellationRequested()` sat OUTSIDE its own try block -- a
   cancelled token was the one failure between `Connecting` and `Connected` that published no terminal
   event at all, latching every subscriber on `Connecting` forever. Fixed by moving the check inside the
   try, alongside reordering `_sessionActive = true` before the `RigId` read (a throwing `RigId` getter
   used to leave `_protocol` non-null with `_sessionActive` false -- the one state `DisconnectAsync`'s
   teardown skips entirely; both shipped `RigId` getters are constants, so this half was unreachable
   today, fixed anyway since it was free in the same edit).
3. **[risk]** `TcpTransport.ReadAsync`'s EOF branch (`bytesRead == 0`) threw without calling
   `AbortConnection()`, unlike the cancellation branch 15 lines above it -- so `IsOpen` kept reporting
   `true` on a dead socket, and `IsOpen` is exactly what `RigctldClientProtocol.EnsureConnectedAsync`
   uses to decide whether to reopen. A later `Set*Async` (the PTT-keying path, not just the
   self-healing poll path) on the same protocol instance would write into a dead socket. Fixed to match
   the cancellation branch's own handling. `FakeRadioTransport`'s script-exhaustion path updated to
   abort too, per `IRadioTransport`'s own "no more forgiving approximation" rule for test doubles.
4. **[nit]** `TcpTransport.WriteAsync` re-read the `_stream` field after its own null check -- a racing
   `AbortConnection()` (from a concurrent cancelled read, `CloseAsync`, or `DisposeAsync`) could turn a
   guarded `InvalidOperationException` into a `NullReferenceException`. Fixed by capturing into a local
   first, same pattern `ReadAsync` already uses.

Three of the four got fast regression tests (`RadioControllerTests.ConnectAsync_CancelledToken_
PublishesFailed_NotStuckOnConnecting`, an extension of `TcpTransportTests.ReadAsync_ThrowsIOException_
WhenRemoteClosesConnection` asserting `IsOpen` is false afterward), each mutation-verified (reverted the
fix, confirmed the exact predicted failure, restored). The first (unbounded protocol dispose) mirrors
round 1's own `PollLoopShutdownTimeout` fix, left untested for the same reason: a real 10s-hang scenario
isn't cheap to test without injecting the timeout as a constructor parameter, which felt like scope
creep beyond the auditor's given fix.

**Explicitly deferred, per the auditor's own go/no-go guidance** (narrow-risk, not blocking, don't spend
another full round chasing them): a CompareExchange-ordering gap in the poll loop's reconnect path
needing a ~10s preemption window between a timed-out `DisconnectAsync` teardown and the reconnect's own
claim to actually bite; and the complete absence of any lock serializing `ConnectAsync`/
`DisconnectAsync`/`DisposeAsync` against each other (arguably a contract-documentation gap on
`IRadioController` rather than a code defect, per the auditor's own framing).

Full solution suite green: `ScanlineStudio.Core.Radio.Tests` 124/124, `ScanlineStudio.Core.Sstv.Tests`
1022/1022 (1 unrelated intentional skip), every other project passing. Committed as `1889fa8`.

Round 2 found real issues, so it does not count toward the 2-consecutive-clean-round gate either. Round
3 is next -- the earliest round that can start that count.

## Chunk 3b round 3 (2026-08-21)

Fresh independent re-derivation against the post-round-2 code, plus round 2's own two explicitly-
deferred findings weighed in on fresh.

1. **[blocker]** `TcpTransport.ReadAsync`'s loop-top `ct.ThrowIfCancellationRequested()` -- the branch
   that fires whenever a response line is ALREADY buffered, the routine case, since
   `RigctldClientProtocol.ReadLineAsync` opens a fresh enumeration per line and a command like `m`
   legitimately leaves a second line buffered between them -- threw without calling `AbortConnection()`,
   unlike the `stream.ReadAsync` catch round 2 already fixed 15 lines below it. `IsOpen` stayed `true`
   on a desynced buffer, so the next enumeration silently returned the stale buffered tail as its own
   response, with `RadioController` never seeing a transport error to reconnect on. Reachable from a
   LIVE production cancellation path, not just teardown: `SstvSessionService.cs:502` passes a real `ct`
   into `SetPttAsync`, and the protocol instance stays alive afterward. Fixed to match the already-fixed
   branch. Mutation-verified with a new deterministic test (`ReadAsync_CancelledWithBufferedBytesPending_
   AbortsConnection`: write two lines, read one to leave the second buffered, cancel a fresh enumeration,
   assert `IsOpen` is false) -- reverted, confirmed the exact predicted failure, restored.
2. **[risk]** `WriteAsync` didn't abort on a cancelled write -- same desync class, opposite direction (a
   half-written command leaves the peer seeing a truncated line; a fully-written one leaves a response
   nobody will read). Fixed to match the read side. `IRadioTransport`'s contract doc and
   `FakeRadioTransport` updated on both the read and write side.
3. **[risk]** An abandoned poll loop (the `PollLoopShutdownTimeout`/`ProtocolDisposeTimeout` bounds from
   rounds 1-2 expiring while a call stayed wedged) could still publish `StateChanges`/`ConnectionEvents`
   AFTER `DisconnectAsync` already published `Disconnected` once the wedged call finally returned --
   `LastKnownState` non-null on a session-less controller, or a status strip latched on `Reconnecting`
   for a radio that isn't connected. Harmless in today's shape (the only real `DisconnectAsync` caller is
   `DisposeAsync` at app exit) but live the moment a user-initiated reconnect exists. Fixed with early
   returns guarded on `ct.IsCancellationRequested` at both catch blocks and before the success-path
   `PublishState` call.
4. **Round 2's deferred item 10** (a `CompareExchange`-ordering gap in the reconnect path, needing a
   ~10s preemption window to bite) -- confirmed narrow by round 3's own independent re-derivation, but
   the fix is free (4 lines): re-check `ct.IsCancellationRequested` AFTER claiming the protocol slot via
   `CompareExchange`, not only before. Taken.
5. **Round 2's deferred item 11** (nothing serializes `ConnectAsync`/`DisconnectAsync`/`DisposeAsync`
   against each other) -- round 3 traced every production caller (`Program.cs`'s single blocking startup
   connect and its host-dispose-at-exit call; `RadioSessionService` is a pure pass-through; no UI
   view-model calls connect/disconnect at all) and confirmed round 2's own assessment: a genuine
   contract-documentation gap, not a live code defect today. Fixed with an explicit
   "lifecycle calls are not internally serialized" paragraph added to `IRadioController.ConnectAsync`'s
   doc comment (referenced from `DisconnectAsync`'s), stating the real hazard (a `DisconnectAsync`
   landing inside a concurrent `ConnectAsync`'s own teardown-then-resolve window can no-op while the
   session it meant to stop keeps polling) so a future caller with a real concurrent-lifecycle need
   knows to add its own serialization.

Also fixed alongside, mechanical: `ReadAsync`'s null-check-then-capture of `_stream` swapped to
capture-then-null-check (matching `WriteAsync`'s own round-2 fix -- a racing `AbortConnection` between
the two statements would otherwise throw `NullReferenceException` instead of the guarded
`InvalidOperationException`); `FakeRadioTransport.CloseAsync` now routes through `AbortConnection()`
(previously left the read buffer un-reset, unlike the real transport); `ConnectAsync`'s success-path
logging now reads the local `resolved` instead of re-reading the `_protocol` field (a synchronous
`ConnectionEvents` subscriber reacting to `Connected` by calling `DisconnectAsync` inline could otherwise
null the field before the log call, throwing `NullReferenceException`).

One real fast regression test added and mutation-verified (finding 1, above). Findings 2-5 and the
mechanical fixes are either doc-only, narrow-race hardening, or matched to an already-covered sibling
pattern -- judged correct by code review, consistent with this project's practice of not chasing a slow
test for every finding when a faster equivalent already exists nearby (see round 2's own
`ProtocolDisposeTimeout` precedent).

Full solution suite green: `ScanlineStudio.Core.Radio.Tests` 125/125 (confirmed clean across 5
consecutive runs), `ScanlineStudio.Core.Sstv.Tests` 1022/1022 (1 unrelated intentional skip), every
other project passing. Committed as `33eb82a`.

Round 3 found a real blocker, so it does not count toward the 2-consecutive-clean-round gate. Round 4 is
next -- the earliest round that can start that count.

## Chunk 3b round 4 (2026-08-21)

Fresh independent re-derivation against the post-round-3 code. Given the pattern across rounds 1-3 (each
round finding a sibling branch the previous round's own fix missed, in the exact same
cancellation/abort-consistency failure class), this round was explicitly asked to hunt for exactly that
shape of gap -- and found two.

1. **[risk]** The poll loop has FOUR publish sites, not three -- round 3 guarded `CommandFailed`,
   `Reconnecting`, and the success-path `PublishState`, but missed the reconnect-attempt-failed catch's
   `Failed` publish. Reachable in exactly the scenario the other three guards exist for: an abandoned
   loop (`PollLoopShutdownTimeout` expired) resumes after `DisconnectAsync`'s teardown already published
   `Disconnected`, and this path would publish a terminal `Failed` afterward that nothing ever supersedes
   -- every subscriber latches on it permanently. Fixed with the same `ct.IsCancellationRequested` early
   return the other three sites already use.
2. **[risk]** `TcpTransport`'s read/write try blocks only caught `OperationCanceledException` -- a
   generic socket-level failure (`IOException`/`SocketException`, e.g. a peer RST or broken pipe) fell
   straight through with `_stream` left non-null, the identical "`IsOpen` lies" hazard round 2 already
   fixed for the graceful-EOF case. Fixed by widening both catches to `catch (Exception)`.

Both mutation-verified with new regression tests, one per direction
(`ReadAsync_AbortsConnection_OnAResetCloseNotJustAGracefulOne`,
`WriteAsync_AbortsConnection_OnAResetClose`) -- **the read-side test needed a second iteration to get
right**: the first attempt used `Socket.LingerState = new LingerOption(true, 0)` before a normal
`Dispose()`, following the standard "force an RST" recipe, but a standalone probe (written specifically
to check this before trusting the test) showed that combination still produces a graceful 0-byte read on
this environment, not an exception -- the mutation-test run against the deliberately-reverted code
PASSED when it should have failed, the tell that the test wasn't exercising the intended path at all.
Switched to `Socket.Close(0)`, which reliably forces a real RST; re-ran the mutation test and it failed
exactly as expected, then passed clean once restored. A real instance of this project's own
"verify, don't assume a hand-written test is testing what you think it is" discipline.

Also fixed opportunistically (auditor's own nits, cheap, directly parallel to the two findings above):
the poll loop's success-path `ct.IsCancellationRequested` guard now runs before its own "reconnected"
log line, not after (an abandoned loop's late-arriving success no longer logs a false recovery message
after `Disconnected` was already logged); `FakeRadioTransport.DisposeAsync` now routes through
`AbortConnection()` instead of only clearing `_open`; `FakeRadioTransport.ReadAsync`'s cancellation check
moved to the loop top, before a buffer refill, matching `TcpTransport`'s own real ordering (a
pre-cancelled token no longer lets the fake consume one replay chunk first).

**Auditor's own go/no-go verdict: apply these two findings, then it's a go -- no further review round
needed.** Findings 3-8 in the auditor's report (guard-log ordering, a wording nit on a rare "inconsistent
state" exception message surfaced verbatim as a UI-visible reason, `_rigId`/RigId-getter-throw handling
in the reconnect path not mirroring `ConnectAsync`'s own deliberate handling, `LastKnownState` throwing
post-disposal instead of returning null) were left as queued, not chased -- explicitly nit-severity per
the auditor's own framing, not blocking.

Full solution suite green: `ScanlineStudio.Core.Radio.Tests` 127/127 (confirmed clean across 5
consecutive runs), `ScanlineStudio.Core.Sstv.Tests` 1022/1022 (1 unrelated intentional skip), every other
project passing. Committed as `4ee250a`.

Round 4 found 2 real risks, so it does not count toward the 2-consecutive-clean-round gate either --
but the auditor's own conditional go-ahead (fix these two, then ship, no further round needed) is, in
substance, the same "explicit go signal, don't chase more rounds" this project's standing practice
already accepts from a direct go-for-production question. Whether to treat that as sufficient to close
the chunk now, or to spend a round 5 seeking the formal 2-consecutive-clean-round gate, is the user's
call -- flagged, not decided unilaterally.

## Chunk 3b CLOSED (2026-08-21) -- by explicit user decision, not the formal 2-consecutive-clean-round gate

**4 rounds, ~14 real findings fixed** (2 blockers + 3 risks in round 1; 3 risks + 1 nit in round 2,
including a gap in round 1's own fix; 1 blocker + several nits in round 3, including a live blocker in
the same failure class rounds 1-2 already fixed, plus round 2's own 2 deferred items resolved; 2 risks
in round 4, the 4th of four poll-loop publish sites and a socket-error abort gap). Every round after the
first found something the previous round's own fix missed, in the same recurring failure class:
cancellation/EOF/socket-error handling scattered across several similar-looking branches
(`TcpTransport.ReadAsync`'s two separate cancellation-check sites, `WriteAsync`, the poll loop's four
publish sites), each needing the same abort/guard applied consistently. Round 4 closed with the
auditor's own explicit conditional go-for-production verdict ("fix these two, then it's a go, no
further round needed") rather than 2 consecutive clean rounds -- the user, informed of that verdict and
asked directly how to close, chose to accept it and move on rather than dispatch a round 5 purely to
chase the formal gate. Same deliberate-exception precedent as chunk 3a's own closure and Batch 1's
re-audit closure above.

All 4 rounds' commits: `710236f` (round 1 code) / `4153adb` (round 1 docs), `1889fa8` / `eb5487e`
(round 2), `33eb82a` / `d43be26` (round 3), `4ee250a` / `10c9908` (round 4). Full solution suite
confirmed green after every round; `ScanlineStudio.Core.Radio.Tests` last confirmed at 127/127 across 5
consecutive runs, `ScanlineStudio.Core.Sstv.Tests` at 1022/1022 (1 unrelated intentional skip).

**Chunk 3c (`RigctldClientProtocol.cs`+`HamlibRadioProtocol.cs`) is next, not started.**

## Chunk 3c round 1 (2026-08-21)

First round on the two CAT backend protocol implementations. No legacy counterpart (CLAUDE.md §2 --
never ported, pure client of external backends) -- judged purely on internal-invariant
correctness/exception-safety/concurrency, with a known off-scope lead from chunk 3b's own audits
(`RigctldClientProtocol.DisposeAsync` possibly disposing `_requestLock` without draining an in-flight
transaction) handed over explicitly to confirm or refute, not take as given.

**3 blockers, 1 confirmed risk, 2 nits.**

1. **[blocker, Hamlib]** A hard error during `ProbeCapabilities` (the SECOND `CallAsync` in
   `EnsureConnectedAsync`, running AFTER `rig_open` already succeeded) left the rig OPEN with
   `_connected` still `false` -- `DisposeAsync`'s own `if (_connected)` guard then skipped teardown
   entirely, and the next `EnsureConnectedAsync` overwrote `_rig` with a fresh `rig_init`, leaking the
   previous struct AND holding the serial port for the process lifetime. Most reachable path is entirely
   ordinary, no race: `RadioSessionService.TestConnectionAsync` creates a throwaway protocol, polls, and
   disposes in a `finally` -- a user clicking "Test connection" with a wrong baud rate hits a hard
   `RIG_ETIMEOUT`/`RIG_EIO` on the probe and leaks the port, breaking every later Test *and* the real
   Connect until the app restarts. Fixed with a dedicated cleanup arm (needs `rig_close` THEN
   `rig_cleanup`, since the pre-open cleanup arm above only ever needs `rig_cleanup`) and switched
   `DisposeAsync`'s own guard from `_connected` to `_rig != nint.Zero` (handle ownership, not connection
   state, is what decides whether cleanup is owed).
2. **[blocker, Hamlib]** `DisposeAsync`'s own `finally { _lock.Release(); }` handed the semaphore slot
   straight to whatever call queued behind it, without re-checking `_disposed` -- `SemaphoreSlim.Release()`
   completes a pending `WaitAsync` inside the release, and `Dispose()` does not revoke that grant. A
   caller already queued on `_lock.WaitAsync(ct)` BEFORE `DisposeAsync` ran fell into
   `EnsureConnectedAsync` with `_connected` already reset to `false`, and `rig_init`/`rig_open`'d a BRAND
   NEW rig on a disposed protocol. For `SetPttAsync(true)` that is the exact harm class this whole batch
   exists for: a transmitter physically keyed on a handle nothing will ever close again, while the
   caller sees only `ObjectDisposedException` out of its own `Release()` call and concludes the key
   failed. Fixed with a new `AcquireAsync` helper (used by all 4 public methods) that re-checks
   `_disposed` AFTER acquiring the lock, and `DisposeAsync` no longer disposes the semaphore at all
   (`SemaphoreSlim.Dispose()` doesn't fault pending waiters, and disposing it made a concurrent
   transaction's own release throw `ObjectDisposedException` -- masking whatever real exception was
   actually in flight). **Mutation-verified with a genuine repro**: a deterministic
   queued-behind-a-slow-call test (`DisposeAsync_QueuedBehindASlowCall_ThenALaterQueuedCaller_
   ThrowsObjectDisposedException_NeverResurrectsTheRig`) reverted to the old code and the rig was
   confirmed to actually resurrect (`rig_init` called twice) with no exception thrown for the queued
   caller -- restored, and the fixed code throws `ObjectDisposedException` with exactly one `rig_init`
   call, as expected.
3. **[blocker, rigctld]** An unparseable PTT (`t`) readback silently defaulted to "not transmitting"
   instead of throwing, unlike the frequency read 8 lines above (which correctly throws on an
   unparseable line). "Not transmitting" is the one wrong guess with physical consequences here, and it
   also silently suppresses that poll's SWR/ALC/power reads (gated on `isTransmitting`) -- a garbled `t`
   response would disarm the SWR cutoff on a rig that IS actually keyed, with nothing anywhere surfacing
   the discrepancy. Fixed to throw `RadioProtocolException`, matching the frequency read's own handling
   (command-level, so `RadioController` keeps cadence rather than tearing the connection down).
4. **[risk, rigctld -- the chunk-3b lead, CONFIRMED]** `DisposeAsync` disposed `_requestLock` outright,
   without ever taking it. Two consequences: an in-flight transaction's own `finally { _requestLock
   .Release(); }` would throw `ObjectDisposedException`, masking the real in-flight exception (usually
   the `IOException` from the socket `DisposeAsync` just aborted); and, worse, a caller already queued
   on `WaitAsync` at that moment would wait forever, since `Release()` throws before incrementing the
   count and `Dispose()` doesn't fault pending waiters -- a `SetPttAsync(false, CancellationToken.None)`
   unkey call landing in that exact window would leak, permanently pending, never reaching the wire.
   Detectable by the caller today only because chunk 3a's own bounded waits catch the resulting hang
   from the outside, not because this class does anything correct on its own. Fixed with the same
   `AcquireAsync` pattern as Hamlib (minus the `DisposeAsync` restructure, since this class never held
   the lock during its own teardown to begin with) -- left without a dedicated test, since the
   reachable-today severity is narrower (`TcpTransport.OpenAsync`'s own `ObjectDisposedException` guard
   already catches the more severe resurrection case "by accident," one layer down).
5. **[nit]** Culture-sensitive integer parsing on 3 wire-protocol values (frequency, PTT, the `RPRT`
   response code) -- switched to `NumberStyles.Integer, CultureInfo.InvariantCulture`, matching the
   existing float-meter parser's own already-documented reasoning for the same class of bug.

3 of the 4 substantive findings got new regression tests (Hamlib's two, one for the rigctld PTT
blocker), each mutation-verified (reverted the fix, confirmed the exact predicted failure, restored).

Full solution suite green: `ScanlineStudio.Core.Radio.Tests` 130/130 (confirmed clean across 5
consecutive runs, including the new timing-sensitive concurrency test), `ScanlineStudio.Core.Sstv.Tests`
1022/1022 (1 unrelated intentional skip), every other project passing. Committed as `40349f3`.

Round 1 found real blockers, so it does not count toward the 2-consecutive-clean-round gate. Round 2 is
next -- the earliest round that can start that count.

## Chunk 3c round 2 (2026-08-21)

Fresh independent re-derivation against the post-round-1 code. **No blockers -- round 1's four fixes
all re-derived and confirmed correct.** Explicit auditor go-for-production verdict: **"Yes, ship
as-is."** 3 hardening risks found anyway (all reachable only under rig/peer misbehavior, not the
happy or ordinary-error paths), plus nits -- one taken because its worst case is this whole batch's
core harm class, two left queued per the auditor's own unconditional go.

1. **[risk, taken]** `RigctldClientProtocol` had NO per-request I/O timeout -- only `OpenAsync` was
   bounded. A peer that accepts the connection and then stops answering (half-open TCP after a remote
   crash, a stopped `rigctld`) blocks the reading caller forever WHILE HOLDING `_requestLock` -- every
   later caller, including a PTT unkey command, queues behind it and never reaches the wire, leaving a
   physically keyed transmitter with no path to unkey it. The auditor's own framing: "the only one whose
   worst case is a stuck transmitter." Fixed with a `WithRequestTimeoutAsync` wrapper (5s) around each
   public method's post-connect body, plus a separate wrap around `EnsureConnectedAsync`'s own
   7-round-trip capability probe (same wedge exposure, kept independent of the transport-open step's own
   configurable timeout to avoid the two bounds fighting each other). A timeout surfaces as
   `TimeoutException` (transport-level -- `RadioController` backs off and rebuilds from scratch), and the
   cancelled read that produces it also aborts the socket per `IRadioTransport`'s own contract. Left
   without a dedicated test, same precedent as this project's other untested timeout fixes (a fast test
   needs the timeout injected as a constructor parameter, felt like scope creep beyond the given fix).
2. **[risk, queued]** Hamlib's optional-meter probes (SWR/ALC/RFPOWER_METER/STRENGTH) use the SAME
   hard-error-fails-connect logic as the required freq/mode/ptt probes -- a rig that *has* a meter but
   hard-errors reading it (vs. the common soft `-RIG_ENAVAIL` "rig has no such meter" case) fails the
   WHOLE connect permanently (every reconnect re-probes the same level), denying frequency readout and
   PTT keying for a rig whose link demonstrably works. Diverges from rigctld's own sibling, which treats
   every `RPRT` code on `l <LEVEL>` as "absent." Not taken this round -- verified narrow (needs a
   specific rig-backend misbehavior, not a design defect in the happy path), auditor's own unconditional
   go covers it.
3. **[risk, queued]** Both Hamlib connect-failure catch arms log BEFORE cleaning up -- a throwing
   `ILogger` (a real, guarded-against class of bug elsewhere in this exact codebase --
   `RadioController.DisconnectAsync`'s own comment explicitly reasons about it) would skip the native
   teardown entirely, re-opening round 1's own blocker 1 (leaked handle + held serial port). Not taken
   this round -- narrower than round 1's original finding (needs a throwing logger, not just a hard
   native error), auditor's own unconditional go covers it.
4. **[nit, taken]** `_disposed` on `RigctldClientProtocol` marked `volatile` -- unlike Hamlib's own
   `_disposed` (ordered by their shared semaphore, since `DisposeAsync` there takes the same lock),
   `DisposeAsync` here never takes `_requestLock` at all, so there was no other happens-before edge
   ordering `AcquireAsync`'s post-wait read against it.
5. **[nit, queued]** `ReadLineAsync` has no line-length bound (a peer streaming bytes with no `\n` grows
   the buffer unbounded); `PollAsync` ignores the `ReadFrequency` capability flag it probed (a rig
   without `get_freq` throws every poll forever, where Hamlib gates on the flag and yields `hz = 0`) --
   documented as deliberate, flagged only as a cross-backend divergence.
6. Round 1's own left-untested finding (rigctld's `AcquireAsync` fix having no dedicated regression
   test) was independently re-derived and confirmed still correctly narrow: both `TcpTransport` and
   `FakeRadioTransport` already hard-guard the resurrection path with their own `ObjectDisposedException`
   checks, so a queued caller cannot key a rig on a disposed protocol regardless of this class's own
   fix -- defense-in-depth for a future transport (flrig) that might not guard the same way.

Full solution suite green: `ScanlineStudio.Core.Radio.Tests` 130/130, `ScanlineStudio.Core.Sstv.Tests`
1022/1022 (1 unrelated intentional skip), every other project passing. Committed as `687116e`.

Round 2 found real (if narrow) hardening risks, so it does not formally count as clean under this
project's own definition -- but the auditor's own verdict was an UNCONDITIONAL "yes, ship as-is," a
stronger signal than chunk 3b round 4's own conditional go. Whether that's sufficient to close chunk 3c
now, same as chunk 3b's own closure, is the user's call.

## Chunk 3c CLOSED (2026-08-21) -- by explicit user decision, not the formal 2-consecutive-clean-round gate

**2 rounds, 8 real findings fixed** (3 blockers + 1 confirmed risk in round 1, including a genuine
mutation-verified repro of a Hamlib post-dispose rig resurrection; 1 risk taken in round 2, an
unbounded rigctld request/response transaction that could wedge a PTT unkey command forever behind a
half-open TCP peer). Round 2's own independent re-derivation confirmed all of round 1's fixes correct
and found NO blockers, closing with an UNCONDITIONAL auditor go-for-production verdict ("Yes, ship
as-is") -- stronger than chunk 3b round 4's own conditional go. The user, asked directly how to close,
chose to accept that verdict and move on rather than dispatch a round 3 purely to chase the formal
2-consecutive-clean-round gate. Same deliberate-exception precedent as chunk 3a's and chunk 3b's own
closures above.

Two round-2 hardening risks were left queued, not chased, per the auditor's own unconditional go: a
Hamlib optional-meter-probe hard-error permanently failing the whole connect (diverging from rigctld's
own more forgiving handling of the same case), and both Hamlib connect-catch arms logging before
cleanup (a throwing `ILogger` could re-open round 1's own blocker 1).

All 2 rounds' commits: `40349f3` (round 1 code) / `eaf8982` (round 1 docs), `687116e` / `bcdee7e`
(round 2). Full solution suite confirmed green after every round; `ScanlineStudio.Core.Radio.Tests`
last confirmed at 130/130, `ScanlineStudio.Core.Sstv.Tests` at 1022/1022 (1 unrelated intentional
skip).

## Tier A Batch 3 -- CLOSED (2026-08-21)

**All 3 chunks closed** (PTT/transmit sequencing across `SstvSessionService.cs`, `RadioController.cs`+
`TcpTransport.cs`, and the two CAT backend protocols) -- the real-world harm class this whole batch was
opened for (a leaked keyed transmitter, silent wrong-state) found and fixed repeatedly across all
three:

- **Chunk 3a** (`SstvSessionService.cs`'s PTT lifecycle): 32 rounds, ~30 real fixes. Closed by
  explicit user decision at round 32's first fully clean round, choosing not to dispatch a round 33
  purely to satisfy the formal 2-consecutive-clean-round gate. Established this batch's two standing
  process changes (auditor drafts substantive fix code directly; each finished-code round closes with
  an explicit go-for-production question) that carried through chunks 3b and 3c.
- **Chunk 3b** (`RadioController.cs`+`TcpTransport.cs`): 4 rounds, ~14 real findings. Every round after
  the first found something the previous round's own fix missed, in the same recurring
  cancellation/EOF/socket-error-handling failure class. Closed by explicit user decision on round 4's
  conditional auditor go-ahead.
- **Chunk 3c** (`RigctldClientProtocol.cs`+`HamlibRadioProtocol.cs`): 2 rounds, 8 real findings,
  including a genuine mutation-verified repro of a post-dispose rig resurrection bug. Closed by
  explicit user decision on round 2's unconditional auditor go-ahead.

No chunk in this batch closed under the formal 2-consecutive-clean-round gate -- all three closed by
explicit user decision on an auditor go-for-production verdict, a pattern now well-established across
this project (see also Batch 1's own re-audit closure). If any of these three files/chunks is
revisited later, start a fresh re-audit rather than assuming the formal gate was ever met.

Full round-by-round detail for all three chunks lives in this section's own per-round entries above,
not reproduced here.

## Tier A Batch 4 -- IN PROGRESS, started 2026-08-21

Pixel math: TX/RX scanline codecs (the batch plan's own "biggest correction" -- see the approved
batch plan table above). Unlike Batch 3, this batch's files DO have a legacy counterpart, and
legacy-parity/golden-vector fidelity is fully in scope, not just internal-invariant correctness.
Chunked per the plan: **4a** shared substrate (`PixelSampleReader.cs`, `YCbCr.cs`,
`ScanlineCodecFactory.cs`) -- in progress, see below. **4b** `YCbCrSequentialScanlineEncoder/Decoder.cs`
+ `YCbCrLinePairedScanlineEncoder/Decoder.cs`. **4c** `RgbSequentialScanlineEncoder/Decoder.cs` +
`MonoAveragedPairedScanlineEncoder/Decoder.cs`. **4d** `RobotScanlineEncoder/Decoder.cs` -- chunk 4a's
own off-scope note flagged Robot 36's `m_DSEL` chroma-selector polarity/toggle-on-ambiguous-fallback
(`Main.cpp:4286-4305`) as the closest remaining analogue to the Scottie incident (CLAUDE.md §4) in
this file family, not yet audited -- worth its own chunk, restate explicitly when 4d is delegated.

## Chunk 4a round 1 (2026-08-21)

No blocker. One real coverage gap closed, not a behavioral bug: `YCbCr.FromRgb`/`ToRgb` carried no
independent legacy-reference test -- only one pinned gray level plus a self round-trip, coverage an
ordering/offset error can pass (exactly how `FromRgb`'s missing `+128` shipped once, per this file's
own doc history). Fixed with `YCbCrLegacyReferenceParityTests.cs` (new file): an exhaustive full-RGB-cube
sweep of `FromRgb` against a direct transcription of legacy `ComLib.cpp`'s `GetRY` (`:3653-3668`), and
a Y/RY/BY sweep of `ToRgb` against `YCtoRGB`/`Limit256` (`:3475-3482`/`:3461-3473`), including their real
truncate-then-clamp order. `YCbCr.cs` itself verified correct against legacy -- not modified.

Also fixed: a wrong legacy citation in `PixelSampleReader.cs` (`sstv.cpp:4062` -> the real line is
`Main.cpp:4062`), in both the doc comment and the matching test's comment; and a missing constructor
guard -- legacy guarantees `ksbSamples >= 1` (`sstv.cpp:1179`'s `if(!m_KSB) m_KSB++`), and a 0 here
would silently degrade every peak-pick to a bare read with no test failing, since every real caller
today always passes 1 or more. Added `ArgumentOutOfRangeException.ThrowIfLessThan` plus a
mutation-verified test (reverted the guard, confirmed "No exception was thrown," restored).
`ScanlineCodecFactory.cs` read and verified correct (all 5 `ColorEncoding` members map to exactly one
encoder and one decoder) -- not modified.

Two lower-priority nits on `YCbCr.cs` left unfixed per the auditor's own "don't spend another round"
verdict: a comment-precision issue on `FromRgb`'s bit-exactness argument, and `ReadPeakPicked`'s
boundary-guard asymmetry. Auditor's verdict: unconditional go -- ship as-is, don't spend another round
on this chunk.

Commit `4c6f1f4`. Full solution suite confirmed green: `ScanlineStudio.Core.Sstv.Tests` at 1025/1025
(1 unrelated intentional skip, up from 1022 before this round's 3 new tests), every other project's
test suite green.

## Chunk 4a CLOSED (2026-08-21) -- by explicit user decision, not the formal 2-consecutive-clean-round gate

**1 round, 3 real findings fixed** (a missing `ksbSamples >= 1` constructor guard, a wrong legacy
citation, and a real test-coverage gap on `YCbCr.FromRgb`/`ToRgb` closed with an exhaustive
legacy-reference sweep). Round 1 itself closed with an UNCONDITIONAL auditor go-for-production
verdict ("ship as-is, don't spend another round on this chunk"). The user, asked directly how to
close, chose to accept that verdict and move on to chunk 4b rather than dispatch a round 2 purely to
chase the formal 2-consecutive-clean-round gate. Same deliberate-exception precedent as chunks
3a/3b/3c's own closures.

Two nits left queued, not chased, per the auditor's own unconditional go: a comment-precision issue
on `FromRgb`'s bit-exactness argument, and `ReadPeakPicked`'s boundary-guard asymmetry -- both on
`YCbCr.cs`, neither behavioral.

Commit `4c6f1f4` (code) / `02791a9` (docs). Full solution suite confirmed green:
`ScanlineStudio.Core.Sstv.Tests` at 1025/1025 (1 unrelated intentional skip), every other project's
test suite green.

## Chunk 4b round 1 (2026-08-21)

No functional-equivalence bug found. TX and RX verified independently (never inferred one from the
other, per CLAUDE.md §4's own Scottie warning) against `Main.cpp`'s `LineR72`/`LineR24`/`LineMR`
(-> `YCbCrSequentialScanlineEncoder`), `LinePD`/`LineMP`/`LineMN` (-> `YCbCrLinePairedScanlineEncoder`),
and the matching per-pixel RX decode switch (`:4315-4430`). Channel order, chroma array indices,
peak-pick-vs-bare, Limit256 asymmetries, per-channel trim factors, and segment offsets all confirmed
to match, including two easy-to-miss legacy asymmetries (sequential chroma uses `m_KS2S`, line-paired
chroma uses `m_KSS` -- correct, not a copy-paste slip; Y2 in line-paired is genuinely unclamped in
legacy, not a port gap).

Two real gaps closed, both non-behavioral:

- **Documentation**: MR/ML's three 0.1ms hold gaps -- legacy's own RX (`sstv.cpp:871-874`/`:966-969`)
  computes these as 0.1 *samples*, not 0.1ms (the only unscaled term in those blocks), while legacy's
  own TX (`Main.cpp:6772`) emits a real 0.1ms gap -- legacy's RX is self-inconsistent with its own TX
  here by ~1-2 samples of chroma shift. This port matches legacy's TX/real on-air signals, not
  legacy's inconsistent RX constant -- a deliberate, verified choice, now documented at
  `YCbCrSequentialScanlineDecoder.cs`'s hold-segment branch so a future reader doesn't "correct" it
  toward the wrong legacy value.
- **Coverage gap, the closest remaining Scottie-class trap in this chunk**: no test pinned that
  PD/MP/MN's line-paired encoder sources its one transmitted chroma pair from the ODD (first) row,
  not the even row (`Main.cpp:6694`/`:6703` etc.) -- round-trip and the smooth-gradient golden TX
  fixtures can't catch a row swap here, since the decoder applies the same chroma pair to both output
  rows either way. New `YCbCrLinePairedScanlineEncoderTests.cs`, mutation-verified (swapping the
  source row makes the new test fail as predicted).

Also fixed: two wrong `Main.cpp` line citations (`YCbCrSequentialScanlineDecoder.cs`'s
`:4338/4347` -> `:4341/4350`; `YCbCrLinePairedScanlineDecoder.cs`'s `:4393/4402` -> `:4396/4405`), and
`YCbCrLinePairedScanlineEncoder.cs`'s class doc, which omitted the MN family entirely. One off-scope
note, not chased: `RgbSequentialScanlineDecoder.cs` truncates after its `+128` bias rather than
before, unlike this chunk's three siblings -- not in chunk 4b's file list.

No RX golden vector exists for MR/ML (`mr73` has a TX fixture but no `.mmv` capture) -- the sequential
decoder's group-C/D trim factors and 0.1ms gap handling are verified against source only for that
family; Robot-72 does cover the sequential RX chroma path with a real capture.

Auditor's verdict: unconditional go -- ship as-is, don't spend another round on this chunk.

Commit `9da8a45`. Full solution suite confirmed green: `ScanlineStudio.Core.Sstv.Tests` at 1026/1027
(1 unrelated intentional skip), every other project's test suite green.

## Chunk 4b CLOSED (2026-08-21) -- by explicit user decision, not the formal 2-consecutive-clean-round gate

**1 round, no functional bug, 2 real gaps closed** (a legacy RX/TX self-inconsistency documented so
it isn't "corrected" the wrong way later, and a genuine Scottie-class coverage gap -- line-paired
chroma sourced from the odd row -- closed with a mutation-verified test). Round 1 closed with an
UNCONDITIONAL auditor go-for-production verdict. The user, asked directly how to close, chose to
accept that verdict and move on to chunk 4c rather than dispatch a round 2 purely to chase the
formal 2-consecutive-clean-round gate. Same deliberate-exception precedent as chunks 3a/3b/3c/4a's
own closures.

Commit `9da8a45` (code) / this entry (docs). Full solution suite green throughout.

## Chunk 4c round 1 (2026-08-21)

**A real functional bug found and fixed** -- unlike chunks 4a/4b, this round did NOT close with an
unconditional go. TX (both encoders) and RX verified independently against `Main.cpp`'s
`LineMRT`/`LineSCT`/`LineSC2180`/`LineP`/`LineAVT`/`LineMC` and `LineRM`, plus the matching per-pixel
RX decode switch (`:4227-4503`). Channel order confirmed correct for every RGB-sequential mode
including Scottie (the documented Scottie incident's own real fix, re-confirmed, not just
self-consistent); `MonoAveragedPairedScanlineEncoder`'s two-row averaging (vs. YCbCr line-paired's
single-row-reused chroma -- a genuinely different shape, verified against `LineRM` directly, not
assumed) confirmed correct.

**Blocker found and fixed**: `RgbSequentialScanlineDecoder` truncated AFTER the `+128` bias instead
of before, unlike its three sibling decoders (ultracode audit finding #28, previously scoped only to
Y/R-Y/B-Y). Legacy's RGB-sequential RX branch (`Main.cpp:4459-4461`, Scottie's own copy at
`:4230-4233`) reads through the same `int`-returning `GetPictureLevel`/`GetPixelLevel` every
Y/R-Y/B-Y site does, so truncation happens in the raw zero-centered domain, before the bias. The
prior floor-after-bias shape made every sub-mid-gray channel value exactly one level too dark,
systematically, across 15 of the 43 registered modes (Martin, Scottie, SC2, Pasokon, AVT, MC). Fixed
to match the sibling decoders' established pattern; mutation-verified (reverting to floor-after-bias
reproduces the predicted 100-vs-101 off-by-one exactly).

**Coverage gap closed, the closest remaining Scottie-class trap in this chunk**: no test pinned that
`MonoAveragedPairedScanlineEncoder` genuinely averages BOTH source rows (legacy's `LineRM`), not one
-- gradient-based round-trip/golden-vector fixtures can't see a "reads only one row" regression
(~0.5-level difference). New `MonoAveragedPairedScanlineEncoderTests.cs`, mutation-verified (forcing
a single-row read makes the new test fail as predicted).

One nit fixed: `RgbSequentialScanlineEncoder.cs`'s class doc said "Martin/Scottie-family," omitting
AVT/Pasokon/SC2/MC. One off-scope note, not chased: `MonoAveragedPairedScanlineDecoder` deliberately
doesn't replicate legacy's two int truncations (measured bound 2) the way 3 sibling decoders now
replicate theirs (bound 1) -- a documented, tested inconsistency in principle, not a bug; worth one
deliberate project-level decision rather than four independent ones.

No RX golden vector exists for MR/ML specifically (noted in chunk 4b). Only `martin-m1`/`scottie-s1`
have a real RX golden vector through `RgbSequentialScanlineDecoder` -- `scottie-dx` and `rm8` are
either TX-only or decode through a different, unchanged decoder, so they're structurally immune to
this fix and weren't re-measured. Actually measured (not assumed) after the fix: martin-m1 1.94 ->
1.44, scottie-s1 1.80 -> 1.10, both improving as predicted (the fix moves values toward the source).
Recorded in `GoldenVectorTests.cs`'s own measured-value ledger.

Commit `b89b4ac`. Full `ScanlineStudio.Core.Sstv.Tests` suite re-confirmed at 1029/1030 (1 unrelated
intentional skip). Auditor's verdict: NOT a go as originally found -- fix-and-reverify required (now
done); round 2 needed to independently confirm the fix before closing, per the standard process for
a round that found a real functional bug rather than only nits/gaps.

## Chunk 4c round 2 (2026-08-21, independent agent, fresh context)

Independent re-derivation of round 1's fix, not a re-read of round 1's own reasoning -- re-derived the
truncation-domain identity directly from `sstv.cpp`'s `CFQC::Do`/`CSSTVDEM`'s buffer-negation,
`Main.cpp`'s `GetPixelLevel`, and `Limit256`, arriving at the same place independently, plus confirmed
it for the narrow MC110/140/180 sub-family round 1 hadn't separately checked (`NARROW_CENTER`/
`NARROW_BWH`, `sstv.h:440-445`) -- identical 1-level truncation bug, same fix applies. Re-derived both
new tests' expected numeric values independently (confirmed `1814.0625Hz` -> raw `100.5` exactly, no
FP fuzz on the truncation boundary; confirmed the mono-averaged test's `125` = `floor((16+234)/2)`
against `GetRY`/`LineRM` directly). Fresh full check of all 4 files found no new behavioral defect.

Corrected round 1's own "~2x tolerance headroom" claim -- actual pre-fix margins were 1.55x/1.67x, not
~2x -- but independently established a stronger, more precise safety argument round 1 hadn't made:
`ColorToFreq` floors, so the fix's truncation change moves values toward the source for roughly half
of all pixels and never away from it, meaning deltas should improve or stay flat by construction, not
merely "probably fit." Confirmed by this round's own actual re-measurement (see round 1's entry, now
updated with real post-fix numbers rather than an assumed "~2x headroom").

Two nits, both fixed this round (not by round 2 itself, which flagged them as non-blocking): the
`GoldenVectorTests.cs` measured-value ledger was stale for `martin-m1`/`scottie-s1` (now records the
real re-measured 1.94->1.44 / 1.80->1.10 deltas); round 1's own docs entry above overstated which
fixtures were RX-affected (corrected to just the two that actually decode through the changed
decoder). One off-scope note independently re-confirmed, not chased: `MonoAveragedPairedScanlineDecoder`'s
own documented 2-truncation divergence (measured bound 2) is now the last undone case of this pattern
in the file family -- a deliberate, already-tested, already-documented choice, not a new finding.

Auditor's verdict: unconditional GO -- ship chunk 4c as-is, close now, do not dispatch a round 3.

Full `ScanlineStudio.Core.Sstv.Tests` suite re-confirmed green after the ledger/docs fixes.

## Chunk 4c CLOSED (2026-08-21) -- round 2 clean, round 1 was not (a real bug found and fixed)

**2 rounds, 1 real functional bug found and fixed** (`RgbSequentialScanlineDecoder`'s truncation-domain
divergence, affecting 15 of the 43 registered modes), plus 2 real coverage gaps closed (the mono-averaged
row-averaging test, and the RGB truncation-domain pin) and 2 nits (a class-doc omission, a stale
measured-value ledger). Round 1 was NOT clean (a real blocker), round 2 -- an independent, fresh-context
re-derivation, not a rubber-stamp -- was clean and gave an unconditional go. Strictly, this doesn't meet
the formal "2 CONSECUTIVE clean rounds" gate (round 1 wasn't clean), but this is the same shape as
chunk 3c's own closure (round 1 real findings, round 2 clean + unconditional go). The user, asked
directly how to close, chose to accept round 2's verdict rather than dispatch a round 3 purely to
manufacture a second consecutive clean round after the fact.

Commits `b89b4ac` (round 1 code) / `3db5adc` (round 1 docs) / `0eb76ff` (round 2 docs + ledger).
Full solution suite green throughout; `ScanlineStudio.Core.Sstv.Tests` last confirmed at 1029/1030
(1 unrelated intentional skip).

## Chunk 4d round 1 (2026-08-21) -- the last chunk in Batch 4

Highest-priority item, flagged since chunk 4a as the closest remaining Scottie-class risk in this
file family: Robot 36's `m_DSEL` chroma-selector polarity and its ambiguous-fallback toggle
(`Main.cpp:4286-4305`). **Correctly ported** -- verified against both the actual TX generator
(`LineR36`, `Main.cpp:6568-6577`) and the actual RX decode switch independently, never inferred one
from the other: even row -> 1500Hz -> R-Y, odd row -> 2300Hz -> B-Y, decisive threshold at
`|d| >= 64`, ambiguous case toggles from the previous line's selection, cold default R-Y (matching
`m_DSEL`'s own `Main.cpp:712` default) reset per image (matching `RobotScanlineDecoder`'s own
per-session construction). Chunk 4c's truncation-domain fix pattern (truncate before the `+128` bias)
independently re-confirmed still correct here -- this file was cited as one of finding #28's ORIGINAL
three correct siblings, and that claim held up under direct re-verification, not just assumed true
because it was the reference implementation.

**One real, deliberate divergence found, not fixed as code -- comment corrected instead.** Legacy
re-runs the decisive/ambiguous decision on every sample in its `[m_SG, m_CG)` window; the port
evaluates one sample near the window's end. For a decisive final sample this is exact. For an
ambiguous final sample, legacy's real behavior is a per-sample TOGGLE CHAIN whose outcome depends on
the ambiguous run's length in samples (a sample-rate-dependent artifact of legacy's own algorithm,
not a stable target); the port's single toggle instead reproduces legacy's outcome at legacy's own
native 11025Hz rate exactly. Kept as-is (an exact port would need per-sample iteration, rewriting all
four existing call-index-based tests, for a fallback case with no real fixture exercising it, and
would risk the robot-36 golden-vector tolerance's own thin 0.89 headroom) -- the auditor's own
recommendation, not a shortcut. Comment corrected to state this precisely instead of overclaiming
equivalence.

Real coverage gap closed: no test pinned the tone-selector's actual READ POSITION (SHOULD item 12) --
all four existing tests are call-index-driven, not sample-position-driven, so they'd pass unchanged
even if the read regressed back to the segment's nominal end (squarely in the following porch's
contamination zone). New sample-position-driven test, mutation-verified (reverting to a segment-end
read makes it fail as predicted).

3 more nits fixed: finding #27's "cold-start" premise corrected (legacy's per-picture reset clears
`m_DSEL`/`m_AX` but NOT `m_D36`, so only the very first image is a true cold start -- the port's
128.0 neutral default is a deliberate improvement, not a literal legacy port, now stated as such); a
garbled unbalanced-paren sentence; `isEvenLine`'s initial value now reads from
`_lastSelectionIsEvenLine` instead of a hardcoded literal (unreachable today, removes a latent trap
for self-consistency). One nit left unfixed (pure perf, no functional issue): the encoder recomputes
`FromRgb` per pixel instead of caching the Y-pass's own R-Y/B-Y the way legacy's `LineR36` does.

Auditor's verdict: unconditional GO -- if this round is clean, the whole batch closes; it was.

Commit `99da537`. Full solution suite confirmed green: `ScanlineStudio.Core.Sstv.Tests` at 1030/1031
(1 unrelated intentional skip), every other project's test suite green.

## Chunk 4d CLOSED (2026-08-21) -- by explicit user decision, not the formal 2-consecutive-clean-round gate

**1 round, no functional bug, 1 real coverage gap closed** (the tone-selector read-position test),
4 comment/doc nits fixed. The highest-priority item this chunk existed to check -- Robot 36's
`m_DSEL` chroma-selector polarity, the closest remaining analogue to the Scottie incident in this
file family -- verified correctly ported. Round 1 closed with an UNCONDITIONAL auditor
go-for-production verdict. The user, asked directly how to close, chose to accept that verdict and
close both the chunk and the batch rather than dispatch a round 2 purely to chase the formal
2-consecutive-clean-round gate. Same deliberate-exception precedent as chunks 3a/3b/3c/4a/4b's own
closures.

Commit `99da537` (code) / this entry (docs). Full solution suite green throughout.

## Tier A Batch 4 -- CLOSED (2026-08-21)

**All 4 chunks closed** (pixel math: TX/RX scanline codecs -- `PixelSampleReader.cs`/`YCbCr.cs`/
`ScanlineCodecFactory.cs`, the YCbCr sequential/line-paired pair, the RGB sequential/mono-averaged
pair, and the Robot pair). Unlike Batch 3, every file in this batch has a real legacy counterpart, and
legacy-parity fidelity was fully in scope throughout -- this batch found ONE real functional bug
(chunk 4c) plus multiple genuine Scottie-class coverage gaps closed across chunks 4b/4c/4d, none of
them actual channel-order/polarity bugs (unlike the original Scottie incident this whole batch was
prioritized for), but each one a plausible near-miss of that same shape, now protected by a
mutation-verified test:

- **Chunk 4a** (shared substrate): 1 round. No blocker. A missing `ksbSamples >= 1` guard, a wrong
  citation, and an exhaustive legacy-reference parity test for `YCbCr.FromRgb`/`ToRgb` closing a real
  coverage gap. Closed by explicit user decision on round 1's unconditional go.
- **Chunk 4b** (YCbCr sequential/line-paired): 1 round. No functional bug. Documented a genuine
  legacy RX/TX self-inconsistency in MR/ML's hold gaps (this port deliberately matches legacy's TX,
  not legacy's own inconsistent RX constant) and closed a real Scottie-class gap (line-paired chroma
  sourced from the odd row, mutation-verified). Closed by explicit user decision on round 1's
  unconditional go.
- **Chunk 4c** (RGB sequential/mono-averaged): 2 rounds. **The one real functional bug in this
  batch** -- `RgbSequentialScanlineDecoder` truncated after its `+128` bias instead of before,
  systematically darkening ~half of every decoded pixel value across 15 of the 43 registered modes.
  Fixed to match its sibling decoders' established pattern (extends ultracode audit finding #28),
  mutation-verified, independently re-derived and confirmed correct by a fresh round 2 (including for
  the narrow MC sub-family round 1 hadn't separately checked). Also closed a real Scottie-class gap
  (mono-averaged paired genuinely averages both source rows). Closed by explicit user decision on
  round 2's unconditional go -- round 1 wasn't clean, so this one doesn't strictly meet the formal
  gate, same shape as chunk 3c's own closure.
- **Chunk 4d** (Robot): 1 round. No functional bug -- the batch's own highest-priority item, Robot
  36's `m_DSEL` chroma-selector polarity (the closest remaining Scottie-class risk in this file
  family, flagged since chunk 4a), verified correctly ported against both TX and RX independently.
  Closed a real coverage gap (tone-selector read position). Closed by explicit user decision on round
  1's unconditional go.

No chunk in this batch closed under the formal 2-consecutive-clean-round gate -- all four closed by
explicit user decision on an auditor go-for-production verdict, the same well-established pattern as
every Batch 3 chunk (see also Batch 1's own re-audit closure). If any of these four files/chunks is
revisited later, start a fresh re-audit rather than assuming the formal gate was ever met.

Full round-by-round detail for all four chunks lives in this section's own per-round entries above,
not reproduced here.

## Tier A Batch 5 -- IN PROGRESS, started 2026-08-21

Mode tables & constants (approved batch plan, row 5). Different rubric from Batch 4 -- this batch
mixes real data-table verification (does each mode's row match its legacy constants) with real
control-flow logic, so rigor is set per-chunk, not uniformly at full Tier A weight.

Chunked as follows. File sizes confirmed against the actual current files (the approved-plan table's
`VisHeader.cs` estimate of 392 lines is stale -- the real file is 464 lines; noted here so a future
reader doesn't treat the plan table as ground truth):

- **5a** `SstvModeRegistry.cs` lines 1-655 (data/table half) -- verify each registered mode's
  constants (frequencies, timings, channel order) against `sstv.h`/`Main.cpp`'s legacy mode table.
  Table-verification rubric per the plan: single pass, escalate to full Tier A rigor only if it finds
  a real constant mismatch. No dedicated test file found for `SstvModeRegistry.cs` under
  `tests/ScanlineStudio.Core.Sstv.Tests/` -- coverage is indirect (golden-vector tests exercise
  individual modes); note this as a possible coverage gap if 5a finds anything.
- **5b** `SstvModeRegistry.cs` lines 656-1045 (real per-mode math half, the plan's own "higher risk"
  flag) -- full Tier A rigor (fresh round -> fix -> fresh round again, 2 consecutive clean rounds).
- **5c** `VisHeader.cs` (464 lines, 1 method + `VisHeaderTests.cs`) -- table-verification rubric per
  the plan ("downgraded... table-verification pass"), single pass, escalate only on a real finding.
- **5d** `VisBitDecision.cs` (30 lines + `VisBitDecisionTests.cs`) + `SyncAnchorCorrector.cs` (126
  lines + `SyncAnchorCorrectorTests.cs`) -- both real control-flow/state logic, not tables. Full Tier
  A rigor.

Starting with 5a.

## Chunk 5a round 1 (2026-08-21) -- CLOSED, single-pass table-verification, no round 2 needed

Verified all 43 `SstvModeRegistry.cs` mode entries (lines 98-654) against legacy source:
`CSSTVSET::GetTiming` (`sstv.cpp:1188-1278`) for line-duration totals; each family's real TX
line-generator function (`Main.cpp:6535-6845`) for scan/sync/porch split, channel order, and sync
placement -- not inferred from RX branch widths, the exact failure mode of the original Scottie
incident (CLAUDE.md §4); the two-stage VIS-decode switch (`sstv.cpp:1993-2074`, `:2078-2121`)
cross-checked against the TX byte table (`Main.cpp:7436-7548`) for all 24 normal + 13 extended VIS
codes; the narrow-mode FSK packet bytes (`Main.cpp:7402-7421`) for all 6 narrow codes;
`GetBitmapSize`/`GetPictureSize` (`sstv.cpp:607-653`) plus each family's TX loop bound for every
`ImageWidth`/`ImageHeight`; and the legacy mode enum (`sstv.h:450-495`) for mode-set completeness
(exactly 43, matches `All`).

**Zero constant mismatches across all 43 modes.** Specific confirmations: Scottie's known-prior-bug
framing (separator-G-separator-B-sync-in-middle-separator-R, `LineSCT`) is still correct; Robot 72
is genuinely NOT a slow Robot 36 (fixed markers + both chroma scans every line vs. one alternating
selector tone + one chroma scan, per `LineR72`/`LineR36` directly, matching CLAUDE.md §3's own
example of this exact false-inference risk); RM12's real non-parity VIS byte `0x86` independently
re-derived bit-by-bit as the sole parity violator among all 24 legacy bytes; MR/ML hold segments
confirmed as a genuine last-transmitted-frequency hold (`LineMR`'s hoisted `short d;`), not a fixed
tone; every odd-looking height (PD160 512x400, PD290 800x616, MP/MN 320x256, MC 320x256, ML
640x496) confirmed against `m_L` and the TX loop bound.

Two non-blocking nits (not fixed): `R24`'s `ImageHeight: 120` vs. `Rm8`/`Rm12`'s `240` for the same
underlying legacy row-doubling structure is an inconsistent (though each independently already
documented, deliberate) presentation-layer choice; `CreateMonoAveragedMode`'s hardcoded 2.0ms porch
matches legacy's `ts/3.0` derivation numerically today but hides the derivation.

**Real coverage gap closed with a new test, not just noted.** No test asserted the table's own
fields directly -- `SstvRoundTripTests.LineDuration_MatchesLegacyGetTiming` only pins duration
totals (a transposed VIS code between two same-duration modes, e.g. MP140/MN140 both 1090.0ms,
would pass unchanged), and `VisHeaderTests.cs` pins exactly one VIS byte. Added
`SstvModeRegistryTests.cs`: a `[Theory]` over all 43 modes pinning `VisCode`/`ExtendedVisCode`/
`NarrowModeCode`/`ImageWidth`/`ImageHeight` against values independently re-transcribed from the
legacy switches/tables above (not copied from the port's own already-passing values), plus a
`Fact` confirming the expected-value table itself covers every registered mode (so a newly added
mode can't silently skip this pin). Mutation-verified: reverting Martin M1's `VisCode` from 44 to
40 makes the new theory fail as predicted, confirming the test is load-bearing, not vacuous.

Auditor's verdict: unconditional go for production on the table as shipped -- no round 2 needed for
a single-pass table-verification chunk that found no real mismatch, same rigor rule as this batch's
own plan.

Full `ScanlineStudio.Core.Sstv.Tests` suite confirmed green: 1074/1075 (1 unrelated intentional
skip, `TxCaptureFixtureGenerator.RegenerateAllTxCaptureFixtures`).

## Chunk 5b round 1 (2026-08-21) -- CLOSED, unconditional go, no round 2 needed

Full Tier A rigor round (this chunk's own risk level) on `SstvModeRegistry.cs` lines 656-1044 --
`FindByFullVisByte`'s parity-aware match, `IsScottieFamily`, `IsFastAfcGroup`, the 5-way
`GetPeakPickParameters`/`GetKsbSamples` trim-group table (the piece already flagged in-file as
having caught one real bug during its own 4-round plan review, "Piece 10" -- re-verified still
correct today, not just trusted from the comment), `GetAutoSlantThresholdPositions` (including the
documented PD120/180/240 row-doubling special case), the two derived (non-legacy-literal)
sync-segment-offset helpers, the 43-entry `GetSyncPeakOffsetMs` `m_OFP` literal table (independently
re-transcribed byte-for-byte from `sstv.cpp:657-1108`, the single highest-value check in this
chunk), and `GetSyncIntervalCandidates`/`GetSyncIntervalMatchDepth`'s per-group narrow-gating table.

**Zero functional bugs found** -- every value and every mode's group assignment reproduces legacy
exactly, independently re-derived rather than trusted from the file's own already-extensive doc
comments. One narrow, low-impact divergence found and fixed (not a bug in shipped output, a
genuine parity gap): `GetSyncIntervalCandidates` iterated `All`'s own declaration order instead of
legacy's real `smXXX` enum scan order (`sstv.h:450-494`) -- for two same-duration mode pairs within
`SyncCheckSub`'s match window (Martin/MRT1 vs MR115, and ML280 vs MP73), legacy's first-match-wins
semantics could pick a different mode than this port's declaration order for a ~0.5%-off-rate
signal on the no-VIS sync-bypass path only. Independently confirmed the proposed fix's `sm*` order
against `sstv.h:450-494` directly (not just trusted the auditor's transcription) before applying:
extracted the real enum, matched exactly. Fixed with an explicit `SyncIntervalScanOrder` list.

Five doc/citation nits fixed: three `GetPeakPickParameters` group citations off by 1-3 lines (B/C/D
ranges corrected against `sstv.cpp` directly); one wrong file name in a citation (`Main.cpp` ->
`sstv.cpp` for PD120/180/240's `m_L=248`); `GetSyncSegmentOffsetMs`'s doc comment claimed AVT was
"handled correctly" when it actually throws for AVT (now states the real caller-exclusion contract,
matching `GetSyncPeakOffsetMs`'s own established phrasing); `GetSyncIntervalMatchDepth`'s doc
comment inverted its own semantics (a LARGER return value means FEWER prior-interval checks, not
more -- corrected with the exact `MSYNCLINE - 1 - e` relationship). One nit left unfixed (pure
numeric-fidelity technicality, no observable effect for any real mode/rate pair):
`GetKsbSamples`'s `* 239.0/240.0` vs. legacy's literal `m_KS - m_KS/240.0` form.

**Two real coverage gaps closed with new tests, not just noted.** `IsFastAfcGroup`'s 8-mode fast
group had no direct test (`AfcTests.cs` always passed explicit 1.5/3.0 literals) -- added a
43-mode `[Theory]` in `AfcTests.cs`, mutation-verified (dropping Martin M2 from the fast group makes
it fail as predicted). `GetSyncIntervalMatchDepth`'s 4-group + narrow-gating table had only Robot
36 and generic band-gating covered indirectly -- added an 84-case (43 modes minus AVT, x2
narrow/normal) `[Theory]` in `SyncIntervalTrackerTests.cs`, mutation-verified (changing the default
group's depth from 5 to 4 fails 27 of 84 cases as predicted).

Auditor's verdict: unconditional go for production as-is -- no round 2 needed for a round that
found no functional bug, same rigor rule as this batch's own precedent (chunks 3a/3b/3c/4a/4b/4d
all closed the same way, per explicit prior user decisions to accept a clean round 1 rather than
manufacture a round 2 purely for the formal gate).

Full `ScanlineStudio.Core.Sstv.Tests` suite confirmed green: 1201/1202 (1 unrelated intentional
skip).

Chunks 5c (`VisHeader.cs`) and 5d (`VisBitDecision.cs` + `SyncAnchorCorrector.cs`) remain open.

## Chunk 5c round 1 (2026-08-21) -- CLOSED, single-pass table-verification, no round 2 needed

Verified all four sections of `VisHeader.cs` (464 lines) against legacy source: the normal VIS
header (leader/break/leader/start/7-data-bits/parity/stop, `sstv.cpp:1948-2153`), extended VIS
(16-raw-bit two-byte format for MR/MP/ML, `Main.cpp:7549-7561`), the MN/MC narrow mode-announce FSK
packet (`Main.cpp:7395-7424`, `sstv.cpp:2942-2949`, `sstv.h:705-707`), and the Scottie post-VIS
pulse + AVT triple-VIS-repeat/32-block training sequence (`Main.cpp:7563-7579`). This file had
never been audited as its own dedicated target before, though it had already absorbed several real
fixes from a separate, earlier sweep (cited in-file as "S8 fix", "S10 fix", "D6 round 3/4/9",
"Band-1 S3 fix") that targeted `AnalogFmSstvDecoder.cs`'s VIS-decode region and touched these
constants as a side effect -- all re-verified fresh rather than trusted from that history.

**Zero constant/behavioral mismatches.** All 24 normal VIS bytes independently re-derived from
`Main.cpp:7437-7547` and cross-checked against parity; the AVT training shift register (`sd` seeded
`0x5fa0`, MSB-first bit read, byte-split increment/decrement) confirmed bit-exact against
`Main.cpp:7564-7574` including the no-overflow claim across all 32 iterations; every derived
search-ceiling/duration constant confirmed arithmetically self-consistent with its own doc comment.

Three doc/citation nits fixed: the class-level comment claiming LSB-first bit order was "not yet
cross-checked" (it now is, both directions -- comment updated with the real TX/RX citations);
`GenerateExtendedSegments`' doc comment had no real line citation for its TX-code claim (added
`Main.cpp:7549-7554`/`:7561`); `AvtExtraHeaderDurationMs`'s section comment could read as if this
port's formula were missing legacy's leading 9ms slack term -- clarified that legacy's 9ms is a
different quantity (an RX sync-search timeout ceiling, not a TX header-skip deadline) so a future
reader doesn't "fix" a non-bug.

**Three real coverage gaps closed with new tests, not just noted** -- the AVT bit-pattern gap in
particular is exactly the Scottie-incident failure class (CLAUDE.md §4): the existing
`GenerateAvtSegments_TotalHeaderDuration_MatchesLegacy` test only pinned total duration, which is
pattern-independent (a scrambled shift register with the same iteration count would still pass).
Added to `VisHeaderTests.cs`: `GenerateAvtSegments_TrainingBitPattern_MatchesLegacyShiftRegister`
(hand-derived expected tone sequences for the first and last of the 32 training blocks, computed
independently via a throwaway Python re-implementation of the shift register, not copied from the
port); `GenerateExtendedSegments_Mr73_TransmitsLegacyRawWord0x4523` (pins escape/code byte order);
`GenerateNarrowModeSegments_Mn73_TransmitsLegacyPacketBytes` (pins packet byte order and the XOR
checksum formula). All three mutation-verified: reversing the shift register's seed, swapping the
extended-VIS byte order, and dropping the checksum's XOR term each make the corresponding new test
fail as predicted.

Auditor's verdict: unconditional go for production as-is -- no round 2 needed for a single-pass
table-verification chunk that found no real mismatch, same rigor rule as chunk 5a.

Full `ScanlineStudio.Core.Sstv.Tests` suite confirmed green: 1204/1205 (1 unrelated intentional
skip).

## Chunk 5d round 1 (2026-08-21) -- CLOSED, unconditional go, no round 2 needed -- last chunk of Batch 5

Full Tier A rigor round on `VisBitDecision.cs` (30 lines, the stateless VIS-bit decision predicate
shared by `VisLockStateMachine` and `AnalogFmSstvDecoder`'s fixed-window header path) and
`SyncAnchorCorrector.cs` (126 lines, the fold-and-argmax sync-anchor correction, `TMmsstv::SyncSSTV`
port) -- both real control-flow/DSP logic, `SyncAnchorCorrector.cs` in particular flagged for the
same class of sign-error risk this file's own doc comment already documents an earlier draft got
backwards (caught by a prior Opus plan-review before any code shipped).

**Zero functional bugs found.** `VisBitDecision.TryDecide`'s reject/accept predicate confirmed
character-for-character against `sstv.cpp:1974-1988`, including independently re-confirmed variable
correspondence (`d11`=1080Hz, `d13`=1320Hz, `d19`=1900Hz, `slvl2`=`m_SLvl2`) and that BOTH call sites
(`VisLockStateMachine.cs`, `AnalogFmSstvDecoder.cs`) honor the "abort to case 0, resume on next
sample" contract. `SyncAnchorCorrector`'s two highest-risk claims independently re-derived from
first principles rather than trusted from the doc comment: the sign-flip (`argmaxBin -
syncPeakOffsetSamples`, opposite of legacy's own literal `n`) re-traced through `m_rBase`'s zeroing
(`sstv.cpp:1725-1731`) and its per-sample use in `DrawSSTVNormal` (`Main.cpp:4123-4148`), confirmed
correct; the page-strided-vs-flat-array indexing equivalence claim confirmed genuinely equivalent
(not just plausible) by tracing `IncWP`'s real per-page sample count. The dropped Scottie wraparound
branch's replacement (folding a per-mode line-segment offset into the caller's own
`syncPeakOffsetSamples`) confirmed against the actual caller, not just this file's own claim about
it -- verified `GetSyncSegmentOffsetMs` really is generic (no hardcoded table) and really does
produce 0 for every mode except Scottie. No `int` overflow risk at any real (mode, rate) pair.

One doc-only nit fixed (both files' own claim that `SyncAnchorCorrector` wasn't wired into the
decoder yet -- it has been, since piece 8c; corrected in both `SyncAnchorCorrector.cs`'s own class
doc and `SyncAnchorCorrectorTests.cs`'s header comment).

**Two real coverage gaps closed with new tests, not just noted.** `SyncAnchorCorrectorTests.cs`
previously had no test for the callback-ordering contract the production caller actually depends on
(`envelopeAt` invoked exactly once per index in strictly ascending order -- the real caller passes a
STATEFUL streaming `SyncEnvelopeDetector`, so a future fold-loop refactor that re-reads or skips an
index would silently corrupt it with nothing to catch it) or for the truncate-toward-zero-not-floor
distinction on a negative fractional delta (every existing test used integral or positive-fractional
results, where floor and truncation agree; production deltas are routinely negative-fractional).
Added `EnvelopeCallback_IsInvokedExactlyOncePerIndex_InAscendingOrder`,
`NegativeFractionalDelta_TruncatesTowardZero_NotFloor`, and
`AllZeroEnvelope_DefaultsToBinZero_MatchingLegacysInitialMax`. Mutation-verified: floor-instead-of-
truncate fails the first as predicted; resetting the fold's sample counter per page (a genuine
ordering violation -- reversing page/inner-loop iteration direction alone does NOT violate the
contract, since the counter itself stays monotonic regardless of loop nesting order, confirmed the
hard way after two mutation attempts that didn't actually break anything) fails the
ordering test as predicted.

Auditor's verdict: unconditional go for production as-is -- no round 2 needed for a round that found
no functional bug, same rigor rule as this batch's own precedent.

Full `ScanlineStudio.Core.Sstv.Tests` suite confirmed green: 1207/1208 (1 unrelated intentional
skip).

## Tier A Batch 5 -- CLOSED (2026-08-21)

**All 4 chunks closed, zero functional bugs found across the whole batch** (mode tables & constants:
`SstvModeRegistry.cs`'s data table and math halves, `VisHeader.cs`, `VisBitDecision.cs` +
`SyncAnchorCorrector.cs`) -- unlike Batch 4, no chunk in this batch needed a fix-and-reverify round 2
for a real bug; every chunk closed on a clean round 1. What this batch DID find and fix: one real
parity gap (chunk 5b's sync-interval scan-order divergence from legacy's real enum order, affecting
two narrow same-duration mode pairs on the no-VIS sync-bypass path only), and a substantial number of
genuine coverage gaps across all four chunks -- table entries that were previously unpinned beyond
duration totals, per-mode boolean/table lookups with no direct test, and (chunk 5c/5d) pattern-blind
tests that would pass even if the underlying bit sequence were scrambled, exactly the Scottie-incident
failure class this whole sweep exists to catch. Every coverage gap found was closed with a
mutation-verified test in the same round it was found, not just noted for later:

- **Chunk 5a** (`SstvModeRegistry.cs` data table, lines 1-655): 1 round, single-pass table-verification
  rigor. All 43 modes' VIS/extended/narrow codes and dimensions confirmed against legacy. Closed a
  coverage gap (no test pinned table entries beyond duration totals) with a new 44-case theory in a
  new `SstvModeRegistryTests.cs`.
- **Chunk 5b** (`SstvModeRegistry.cs` math half, lines 656-1044): 1 round, full Tier A rigor. The
  43-entry `m_OFP` literal table, the 5-way peak-pick trim grouping, AFC fast-group, Auto Slant
  thresholds, full-byte VIS matching, and sync-interval matching all confirmed against legacy. Fixed
  the one real parity gap (sync-interval scan order) and closed two coverage gaps
  (`IsFastAfcGroup`, `GetSyncIntervalMatchDepth`).
- **Chunk 5c** (`VisHeader.cs`, all 464 lines): 1 round, single-pass table-verification rigor. First
  dedicated audit of this file (previously only touched incidentally by an earlier, separate sweep on
  `AnalogFmSstvDecoder.cs`'s VIS-decode region). Closed three coverage gaps, most notably the AVT
  training sequence's shift-register bit pattern (previously duration-only, the Scottie-class risk
  named above).
- **Chunk 5d** (`VisBitDecision.cs` + `SyncAnchorCorrector.cs`): 1 round, full Tier A rigor. The
  sign-derivation math in `SyncAnchorCorrector` -- flagged in-file as the single most failure-prone
  detail, with a documented history of an earlier draft getting it backwards -- independently
  re-derived from first principles and confirmed correct. Closed two coverage gaps (callback-ordering
  contract, truncate-vs-floor).

No chunk in this batch closed under the formal 2-consecutive-clean-round gate -- all four closed by
explicit auditor unconditional-go verdicts on a clean round 1, the same well-established pattern as
every prior batch's own closures (per the user's standing decision to accept a clean round 1 rather
than manufacture a round 2 purely to satisfy the formal gate).

Full round-by-round detail for all four chunks lives in this section's own per-round entries above,
not reproduced here.

## Tier A Batch 6 -- IN PROGRESS, started 2026-08-21

Header/lock state machines (approved batch plan, row 6). All five files are real control-flow/DSP
state-machine logic (unlike Batch 5's mix of tables and logic) -- full Tier A rigor throughout, no
table-verification downgrade. Every file already has a dedicated test file.

Chunked one file per chunk (line counts: 77/223/350/240/482), ordered by dependency --
`SyncEnvelopeDetector.cs` is the shared envelope-detector primitive the plan's own row note flags
as "feeds all four" of its siblings, so it goes first:

- **6a** `SyncEnvelopeDetector.cs` (77 lines + `SyncEnvelopeDetectorTests.cs`) -- resonate-rectify-
  smooth AM envelope detector (`sstv.cpp`'s `d12`/`d19`), shared by `SlantTracker` and this batch's
  other state machines.
- **6b** `SyncIntervalTracker.cs` (223 lines + `SyncIntervalTrackerTests.cs`) -- note: this file's
  own `GetSyncIntervalMatchDepth`/`GetSyncIntervalCandidates` *inputs* were already audited as part
  of Batch 5 chunk 5b (`SstvModeRegistry.cs`); this chunk covers the tracker class itself, not that
  overlap.
- **6c** `VisLockStateMachine.cs` (350 lines + `VisLockStateMachineTests.cs` +
  `VisLockStateMachineDecoderTests.cs`).
- **6d** `AvtTrainingLockStateMachine.cs` (240 lines + `AvtTrainingLockStateMachineTests.cs`).
- **6e** `NarrowFskHeaderDecoder.cs` (482 lines + `NarrowFskHeaderDecoderTests.cs` +
  `NarrowFskHeaderDecoderStationIdTests.cs`) -- largest file in this batch, last.

Starting with 6a.

## Chunk 6a round 1 (2026-08-21) -- CLOSED, unconditional go, no round 2 needed

Full Tier A rigor round on `SyncEnvelopeDetector.cs` (77 lines): a resonate-rectify-smooth AM
envelope detector (`TankFilter` -> `Math.Abs` -> 50Hz/2nd-order Butterworth `IirFilter`), the
shared primitive the plan's own row note flags as feeding all four of this batch's other state
machines. Verified against `sstv.cpp`'s `d12`/`d19` computation (`CSSTVDEM::Do`) and `InitTone`
(`sstv.cpp:1695-1705`).

**Zero functional bugs.** All five audited points confirmed correct: mode-dependent center-
frequency selection (1900Hz for MN/MC narrow, 1200Hz otherwise, verified against BOTH this file's
own claim and every real call site that constructs an instance -- not just the comment); the
80Hz-vs-100Hz bandwidth split between the VIS-bit tone-race detectors and every other use;
`ProcessSample`'s exact resonate-then-rectify-then-smooth order; `Retune`'s AFC-offset gating
(confirmed the sole caller is only ever reached while actually synced, never unconditionally); and
that no filter state resets on retune (confirmed `TankFilter.SetFreq` only recomputes coefficients,
matching legacy's own `InitTone` never calling `Clear()`).

Two doc-comment nits fixed in `SyncEnvelopeDetector.cs`: the class doc's "AVT excluded entirely"
claim was wrong -- legacy does NOT skip AVT here, it still writes a scaled raw signal into the same
buffer (`sstv.cpp:2299/2303`); this port's own real AVT sync-envelope gap lives at a different call
site, not in legacy's actual behavior (corrected to say so precisely). A stale line citation on
`Retune`'s doc comment (`sstv.cpp:2362` pointed at `InitTone`'s caller, not the `SetFreq` calls
themselves at `:1698-1702`) corrected. A third nit fixed in `AfcTests.cs`: a comment claiming
"legacy never retunes" the sync-bypass-1200 detector was backwards -- legacy's one real `m_iir12`
IS retuned during a lock and reset to nominal between images (`Stop()`'s `InitTone(0)`,
`sstv.cpp:1769-1780`); this port's two-separate-instances design (one retuned, one permanently
nominal) is behaviorally equivalent for the single-image observation the test makes, just for a
different reason than originally stated -- corrected.

**Two real `[risk]`-level coverage gaps closed with mutation-verified tests, not just noted** --
flagged above nit severity because `Retune`'s sign has already shipped backwards once in this exact
call chain (the AFC correction call site) before being caught. `SyncEnvelopeDetectorTests.cs` had
zero coverage of whether `Retune` has any DSP effect at all (only `AppliedCenterFrequencyHzForTests`
was ever read, a value `Retune` assigns independently of the actual `_resonator.SetFreq` call --
deleting that call, or flipping only its sign, silently passed the whole suite). Added three tests:
`Retune_ActuallyMovesTheResonator_NotJustTheObservationHook` (mutation-verified against both a
deleted and a sign-flipped `SetFreq` call), `Retune_IsAbsoluteFromTheConstructedCentre_NotCumulative`,
`Retune_DoesNotResetFilterState`. Also found and fixed one test-integrity defect while applying
this: an existing test (`NarrowBandwidthDetector_RespondsMoreStronglyToOnFrequencyTone`) had a
copy-paste bug (both compared detectors constructed at the same 1080Hz center) that the auditor
flagged -- my first attempted fix (retuning the second detector to 1320Hz) actually broke the
test's real intent (comparing two DIFFERENT filters' on-frequency response to each other isn't
"on-frequency vs off-frequency" for one filter), caught by the test itself failing after the change;
corrected by renaming the variable instead of retuning it, preserving the original (correct)
same-tuning-different-input-tone comparison. Added a new, distinctly-named
`NarrowBandwidth_IsMoreSelectiveThanTheDefault` test for the actual `bandwidthHz` parameter, which
nothing previously exercised (an 80-vs-100 swap would have passed unchanged).

Second real coverage gap: zero direct test pinned the mode-dependent 1200Hz-vs-1900Hz tone selection
(point 1 above) -- a regression flipping that ternary would only degrade MN/MC slant tracking, which
nothing asserted. The needed test hooks (`InitializeSlantForTests`, `SyncEnvelopeDetectorForTests`)
already existed on `AnalogFmSstvDecoder`; added `InitializeSlant_PicksTheLegacySyncBufferTone`
(4-case theory: Robot 36 and Scottie S1 at 1200Hz, MN73 and MC180 at 1900Hz) and
`InitializeSlant_Avt_LeavesNoSyncEnvelopeDetector` to `AfcTests.cs`. Mutation-verified: flipping the
ternary at the real call site (`AnalogFmSstvDecoder.cs`'s `InitializeSlant`) fails all 4 theory cases
as predicted.

Auditor's verdict: unconditional go for production as-is -- no round 2 needed for a round that found
no functional bug, same rigor rule as this batch's own precedent.

Full `ScanlineStudio.Core.Sstv.Tests` suite confirmed green: 1216/1217 (1 unrelated intentional
skip).

## Chunk 6b round 1 (2026-08-21) -- CLOSED, unconditional go, no round 2 needed

Full Tier A rigor round on `SyncIntervalTracker.cs` (223 lines): the peak-interval pattern tracker
(legacy's `CSYNCINT`, `sstv.cpp:1290-1411`) behind all three of legacy's parallel sync-acquisition
strategies. This chunk specifically targeted `CheckConsecutiveHistory`'s history-window loop bound
(`for (var i = HistorySize - 2; i >= depth.Value; i--)`) as the highest-risk line in the file --
exactly the shape of bug class (off-by-one window bounds, local-vs-absolute-index confusion) this
project has hit multiple times before in RX buffer work.

**Zero functional bugs -- every audited point matches legacy exactly**, verified line-by-line
against `sstv.cpp`'s real `SyncInc`/`SyncTrig`/`SyncMax`/`SyncStart`/`SyncCheck`/`SyncCheckSub`,
including the exact order of operations in `SyncStart` (history push happens unconditionally,
`_lastAcceptedPosition` also updates unconditionally inside the separation gate even when the
min-interval check fails, the peak is consumed either way) and every constant (`HistorySize=8`,
tolerance 3ms, min-separation 50ms, min-interval 63ms, max-interval 1390*3ms). The
`HistorySize-2` loop bound this chunk specifically targeted is confirmed correct: legacy's own
`for(i--; i>=e; i--)` after a `MSYNCLINE-1` init is the same pre-decrement, scanning `[e,
MSYNCLINE-2]`, not `[e, MSYNCLINE-1]` -- deliberately never re-checking the entry `Check()` itself
just evaluated.

One doc-comment nit fixed in both `SyncIntervalTracker.cs` and `SyncIntervalTrackerTests.cs`: both
claimed the class's three decoder usage sites were "later, separately-scoped work," not yet wired
in -- they have been for some time (`_syncBypass1Tracker`/`_syncBypassTracker`/
`_syncBypassNarrowTracker` in `AnalogFmSstvDecoder.cs`, driven from `TrySyncIntervalDetectionStep`).
Corrected to name the real wiring and its own separate test files.

**Two real coverage gaps closed with mutation-verified tests, not just noted** -- both exactly the
shape flagged as this project's recurring bug class. `SyncStart`'s min-separation gate (a peak
within 50ms of the last accepted one must be rejected outright, not recorded into history, without
advancing `_lastAcceptedPosition`) had no test at all -- deleting the guard entirely previously
survived the whole suite. Added
`PeakTooCloseToLastAccepted_IsRejected_AndDoesNotDisruptTheNextMatch`, mutation-verified (removing
the guard makes the eventual match fail, since the too-close peak's rejected position would
otherwise corrupt the next real interval). `CheckConsecutiveHistory`'s `HistorySize-2` bound itself
had no test distinguishing it from an off-by-one `HistorySize-1` start -- every existing test used a
fully-periodic sequence where both bounds produce the same result. Added
`NarrowTracker_PriorHistoryUsesLegacysSubharmonicCap_CurrentEntryCanStillUseK3`: a sequence where
the current (most recent) entry only matches via its 3x subharmonic (allowed for narrow bands by
`Check()`'s own limit of 3, but NOT allowed by the history-window check's narrow-band cap of 2) --
mutation-verified that reverting the loop start to `HistorySize-1` makes this fail exactly as
predicted (the off-by-one bound would re-check the current 3x entry against the wrong, stricter cap
and reject it).

Auditor's verdict: unconditional go for production as-is -- no round 2 needed for a round that found
no functional bug, same rigor rule as this batch's own precedent.

Full `ScanlineStudio.Core.Sstv.Tests` suite confirmed green: 1218/1219 (1 unrelated intentional
skip).

## Chunk 6c round 1 (2026-08-21) -- CLOSED, unconditional go, no round 2 needed

Full Tier A rigor round on `VisLockStateMachine.cs` (350 lines): the real-time VIS-lock/bit-decode
state machine (`sstv.cpp`'s `CSSTVDEM::Do`, `m_SyncMode` cases 0/1/2/9/3). Already carries a real
fix history (S31/S12/S7/S10, pieces 6c/7b/7c/9, "ultracode audit finding #11") -- every prior fix
re-verified fresh rather than trusted, none re-litigated without new evidence.

**Zero functional bugs -- every state transition matches legacy exactly**, verified against the
literal source rather than the file's own (extensive, and in this case accurate) doc comments:
Search's 3-term trigger; ConfirmLock's sustained-hold semantics (any single failing sample resets
immediately, countdown only decrements on a passing sample); DecodeVis/DecodeExtendedVis's d13-
frozen-between-attempts behavior (grepped every `m_iir13`/`m_lpf13` reference repo-wide to confirm),
the bit-shift accumulation, the escape-code check being reachable only from the first byte
(confirmed the extended switch has no `0x23` arm), and clean null-match dispatch with no
`_resolvedMode` contamination; Verify's genuinely-different 2-term condition (independently
re-verified against the literal source, not the comment's own claim); the AVT caller-branch
contract (grepped the actual call sites); `Reset()`'s scope; and `MsToSamples`' truncation
semantics (confirmed `m_SampFreq`'s real C++ type is `double`, matching the truncate-toward-zero
narrowing this port's own `(int)` cast produces).

One doc-comment nit fixed: the anchor-reconciliation comment claimed a specific "confirmed: 3
samples" rounding-mismatch figure that doesn't reproduce under independent re-derivation (measured
at 11025Hz: ~7.5 samples normal, ~13.5 extended). Auditor explicitly recommended NOT changing the
code -- the ~80-sample envelope-detector group delay dominates by an order of magnitude and this
truncation's own deficit happens to cancel some of it, so combining-then-rounding would make the
composite anchor marginally worse, not better. Comment corrected to state the real relationship and
explicitly warn against "fixing" it.

**One real coverage gap closed with a mutation-verified test; two others attempted but abandoned
after proving genuinely impossible to construct reliably via natural-signal timing --  a real,
positive finding about the state machine's own behavior, not a shortcut.** Added
`LockAnchor_AddsScottiePostVisPulseForScottieFamilyOnly` (4-case theory: MartinM1 non-Scottie, all
three Scottie sub-modes), mutation-verified (zeroing the Scottie pulse term fails all 3 Scottie
cases as predicted, the non-Scottie case correctly unaffected).

The other two proposed tests (proving a null-VIS-byte match resets `_state` to `Search` rather than
wedging, for both the normal and extended decode paths) were built, and initially appeared to pass
under both correct and mutated code -- investigated rather than accepted at face value, since a
test that can't fail is worse than no test. Root cause, confirmed via reflection-based per-sample
tracing: after a null match, `_syncTimeCounter` has ALREADY been reassigned to a fresh
`BitDurationMs` window one line earlier (as part of accumulating the just-decoded bit, before the
mode-lookup runs) -- so even without the `_state = Search` reset, the machine doesn't truly wedge.
It keeps cycling through 330-sample "phantom bit" windows against whatever content follows (typically
near-silence), and VisBitDecision's own reject condition (`sstv.cpp:1981-1984`'s "too close to
call"/"both weaker than reference" branches) reliably fires on that ambiguous content within a cycle
or two, resetting to `Search` via a DIFFERENT, still-intact code path -- masking the missing reset
almost every time. This is a genuine, legacy-consistent property of the state machine (both resets
being present makes the distinction unobservable in practice), not a bug -- but it means a
natural-signal test cannot reliably distinguish "this specific reset is present" from "some other
reset caught it a cycle later." Confirmed empirically across 5 different constructions (data-bit
flip, parity flip on two different base modes, unmodified real headers) before concluding this
wasn't a fixable test-construction mistake. This specific coverage gap (the null-match branch's own
reset, independent of the bit-reject path's reset) remains open -- closing it properly would need
either an internal test-only hook to drive the state machine past natural signal timing, or
accepting the gap given both real resets are independently present and correct. Not chased further
this round per this batch's own precedent (auditor's own two `[risk]` items were explicitly
"opportunistic," not blocking).

Auditor's verdict: unconditional go for production as-is -- no round 2 needed for a round that found
no functional bug, same rigor rule as this batch's own precedent.

Full `ScanlineStudio.Core.Sstv.Tests` suite confirmed green: 1222/1223 (1 unrelated intentional
skip).

## Chunk 6d round 1 (2026-08-21) -- CLOSED, unconditional go, no round 2 needed

Full Tier A rigor round on `AvtTrainingLockStateMachine.cs` (240 lines): the AVT training-sequence
lock (`sstv.cpp`'s `CSSTVDEM::Do`, `m_SyncMode` cases 4/5/6/7). This file's own doc comments already
document TWO real bugs found and fixed by a prior independent review (a timeout-scoping double-
count bug, and a `_phaseCounter` overwrite that shifted completion timing by ~63 samples, invisible
to the test suite's own wide tolerance) -- both re-verified fresh, not re-litigated.

**Zero functional bugs.** Every state (MarkerSearch/MarkerConfirm/DecodeBits/WaitNextMarker)
confirmed against the literal legacy source, including independently re-deriving the 40.96 Hz-to-
raw-units conversion factor from `PllFmDemodulator`'s and legacy's own gain constants (not trusted
from the file's own comment), the asymmetric strict/inclusive dead-zone bounds, the MSB-first shift
(confirmed as the genuine opposite of `VisLockStateMachine`'s LSB-first shift, both independently
verified), the block-validity checksum, and both previously-fixed bugs' current correctness
(re-derived from the caller's real construction point and from legacy's own case 8 dead-code
mechanics, not re-read from the prior fix's own reasoning).

One real nit fixed in production code: the `h==0x40` last-block branch's guard compared against
`MsToSamples(BitWindowMs)` (truncated to 107), where legacy's own comparison
(`sstv.cpp:2205`) promotes the un-truncated 107.654 -- a divergence only at the single value 107,
unreachable on the clean-lock path (~5.6ms effect on a pathological partial lock). Fixed to compare
against the un-truncated value directly, matching legacy's own promotion.

**One real `[risk]`-level test finding closed, not just noted -- the third time this specific file
has been bitten by a timing regression its own tests couldn't see.** The sole end-to-end test's
`+/-300ms` (`+/-3308` sample) completion-time tolerance was ~58x too loose to prove a real lock had
happened: a signal with NO lock at all completes only ~57 samples away from a real lock's own
completion point, comfortably inside that old band -- meaning the entire `DecodeBits` checksum
check and per-block timeout recalculation could be deleted and this test would still pass. This is
exactly the mechanism that let the second documented bug (the ~63-sample `_phaseCounter` overwrite)
ship undetected. Independently re-measured the real completion sample against the CURRENT code
before pinning it as a golden value -- the auditor's own proposed figure (58608) turned out to be
26 samples off the actual measured value (58634); shipping the auditor's pasted number without
re-measuring would have made the tightened test fail immediately. Tightened to a real range
(58626-58642) plus an explicit "must complete strictly before the no-lock timeout" comparison.
Mutation-verified by reintroducing the exact historical `_phaseCounter`-overwrite bug: the tightened
test correctly fails (measuring exactly 58608, a 26-sample shift) where the old `+/-300ms` band
would have passed it silently. Also fixed a stale comment in the sibling test that still cited the
pre-rescoping-fix nominal-budget figure.

Auditor's verdict: unconditional go for production as-is -- no round 2 needed for a round that found
no functional bug, same rigor rule as this batch's own precedent.

Full `ScanlineStudio.Core.Sstv.Tests` suite confirmed green: 1222/1223 (1 unrelated intentional
skip).

## Chunk 6e round 1 (2026-08-21) -- CLOSED, unconditional go, no round 2 needed -- last chunk of Batch 6

Full Tier A rigor round on `NarrowFskHeaderDecoder.cs` (482 lines, the largest and most complex file
in this batch): a direct, literal 18-mode port of `CSSTVDEM::DecodeFSK` (`sstv.cpp:2378-2606`)
decoding both the MN/MC mode-announce packet and the FSK station-ID packet through one shared state
machine. Already documented as having been through "two rounds of independent auditor review" with
real transcription errors caught in round 1 of that earlier effort, plus several already-fixed
code-review findings recorded inline -- all re-verified fresh, none re-litigated without new
evidence.

**Zero functional bugs.** Every one of modes 0-3's distinct shapes (mode 0's trigger, mode 1's the-
only-true-hold semantics, mode 2's timeout-not-hold, mode 3's single-recheck-not-hold) confirmed
against the literal legacy source; the fractional bit-boundary drift correction confirmed to
genuinely diverge from naive integer accumulation by ~10 samples over a 24-bit packet; both
already-documented station-ID fixes (mode 4's dual `m_fskcnt` reset, mode 7's compact-NR-marker
*non*-reset) re-confirmed against source rather than re-read from their own prior fix comments; the
mode-6-vs-mode-8 checksum-failure asymmetry and mode 10's `StationIdDecodeEnabled` asymmetry both
confirmed real in legacy, with mode 10's own unreachability argument independently re-traced through
the actual state graph (grepped every `m_fskmode` assignment site) rather than accepted from the
comment.

**Three real coverage gaps closed with mutation-verified tests -- two of which required real
investigation after an initial construction failed to discriminate, the same lesson chunk 6c's own
investigation already established for this batch.** Nothing previously distinguished mode 2's
TIMEOUT shape (no condition check on the timeout sample itself, an else-if against mode 1's
sustained-hold shape) from a hold, nor mode 3's single-recheck failure path, nor mode 7's
nonzero-carried-count marker-byte edge case. A first attempt at a "mode 2 genuinely resets to mode 0"
test passed even under a real mutation (mode 2 stuck forever) two separate times, for two different
reasons: first, because mode 2's own trigger condition is literally what a real subsequent start bit
looks like, so a stuck decoder locks anyway; second, because the natural mode-code byte's own bit
pattern happened to contain 4 consecutive space-dominant bits, long enough to accidentally complete a
full guard-hold-resync cycle regardless of correctness. Both attempts were verified to fail via
mutation testing before being trusted, not assumed to pass -- the second attempt was dropped and
documented honestly as an open gap rather than shipped. A third, narrower construction (mode 2's
timeout taking priority over a start bit arriving on the exact timeout sample) DID mutation-verify
successfully once redesigned with a bare-minimum subsequent guard budget (no slack for a wasted
detour to self-heal from). Added `Mode2Timeout_TakesPriorityOverAStartBitArrivingOnTheExactTimeoutSample`,
`Mode3Recheck_FailsIfMarkDropsBeforeTheElevenMsMidpoint_ThenResetsCleanly`, and
`CompactNrMarker_WithNonZeroCarriedSubPacketCount_ConsumesOnlyOneHalf_NotTwo` -- all three
mutation-verified against the exact defect shape they target. Also fixed one existing test's
misleading (though harmless, since both constants share the same value) constant usage.

Auditor's verdict: unconditional go for production as-is -- no round 2 needed for a round that found
no functional bug, same rigor rule as this batch's own precedent.

Full `ScanlineStudio.Core.Sstv.Tests` suite confirmed green: 1225/1226 (1 unrelated intentional
skip).

## Tier A Batch 6 -- CLOSED (2026-08-21)

**All 5 chunks closed, zero functional bugs found across the whole batch** (header/lock state
machines: `SyncEnvelopeDetector.cs`, `SyncIntervalTracker.cs`, `VisLockStateMachine.cs`,
`AvtTrainingLockStateMachine.cs`, `NarrowFskHeaderDecoder.cs`) -- every chunk closed on a clean
round 1, no fix-and-reverify round 2 needed anywhere in this batch. What this batch DID find and fix:
zero real behavioral bugs in shipped production code, but a substantial number of genuine coverage
gaps across all five files, several nit-level doc/citation corrections, and -- twice in this batch
(chunks 6c and 6e) -- a real, load-bearing finding about the state machines' own self-healing
behavior that made a naively-constructed "does this specific reset actually fire" test pass under a
real mutation, caught only because every new test in this batch was mutation-verified before being
trusted rather than assumed correct from its own passing run:

- **Chunk 6a** (`SyncEnvelopeDetector.cs`, the shared primitive "feeding all four" of this batch's
  other files): 1 round. No functional bug. Closed two real `[risk]`-level coverage gaps (whether
  `Retune` has any DSP effect at all, and the mode-dependent 1200Hz/1900Hz tone selection) --
  flagged above nit severity specifically because `Retune`'s sign had already shipped backwards
  once in this exact call chain before being caught.
- **Chunk 6b** (`SyncIntervalTracker.cs`): 1 round. No functional bug -- this chunk's own
  highest-risk target, `CheckConsecutiveHistory`'s history-window loop bound (exactly this
  project's recurring off-by-one-window-bound bug class), confirmed correct against legacy's real
  pre-decrement loop. Closed two coverage gaps (the min-separation rejection gate, the
  history-window bound itself).
- **Chunk 6c** (`VisLockStateMachine.cs`): 1 round. No functional bug. Closed one coverage gap
  (the Scottie-only post-VIS-pulse anchor term). Two other proposed tests were built, found not to
  discriminate their intended mutation, investigated rather than shipped anyway, and the real root
  cause (a re-primed countdown masking a missing reset via a different, still-intact reject path)
  recorded honestly as an open gap.
- **Chunk 6d** (`AvtTrainingLockStateMachine.cs`): 1 round. No functional bug -- re-confirmed both
  of this file's own previously-documented bug fixes independently. Found and closed a real
  `[risk]`-level test defect (the sole end-to-end test's tolerance was ~58x too loose to prove a
  real lock had happened, the same mechanism that let this file's own second documented bug ship
  undetected before); the auditor's own proposed golden value was independently re-measured and
  found to be off by 26 samples before being trusted.
- **Chunk 6e** (`NarrowFskHeaderDecoder.cs`, the batch's largest file): 1 round. No functional bug.
  Closed three coverage gaps; a first attempt at a fourth failed mutation-verification twice for two
  different reasons before being dropped and honestly documented as an open gap, the same discipline
  chunk 6c's own investigation established.

No chunk in this batch closed under the formal 2-consecutive-clean-round gate -- all five closed by
explicit auditor unconditional-go verdicts on a clean round 1, the same well-established pattern as
every prior batch's own closures.

Full round-by-round detail for all five chunks lives in this section's own per-round entries above,
not reproduced here.

## Tier A Batch 7 -- IN PROGRESS, started 2026-08-21

DSP primitives, numeric fidelity float-vs-double (approved batch plan, row 7). Full Tier A rigor
throughout -- every file here is real DSP math, none of Batch 5's table-verification downgrade.
Per the plan's own row note, the three demodulator types (Hilbert/PLL/zero-crossing) are user-
selectable via `DemodType` and must all be audited, not just one sibling skipped.

Triage: `AfcTracker.cs` has coverage via `AfcTests.cs`; `TankFilter.cs`/`MovingAverage.cs` have
coverage via `SlantTests.cs`; `IirFilter.cs` has coverage via `HilbertFmDemodulatorTests.cs`.
`Vco.cs` (46 lines) has NO real test coverage anywhere -- only referenced in a DI composition-root
test, not a behavioral one. A real coverage gap, flagged for whichever chunk covers it.

Chunked by relationship, ordered by dependency (foundational primitives first, since they feed
everything else in this batch):

- **7a** Foundational primitives: `TankFilter.cs` (41 lines), `IirFilter.cs` (88), `MovingAverage.cs`
  (58), `Vco.cs` (46) -- 233 lines total. Small individually but shared building blocks; `Vco.cs`'s
  real coverage gap lives here.
- **7b** `HilbertFmDemodulator.cs` (314 lines) -- this port's main picture demodulator (largest file
  in the batch), audited alone given its size.
- **7c** `PllFmDemodulator.cs` (143) + `ZeroCrossingFrequencyCounter.cs` (129) -- the other two
  `DemodType` options, audited together per the plan's own "don't skip a sibling" note.
- **7d** `SearchBandpassFilter.cs` (269) + `TxOutputBandpassFilter.cs` (169) -- the RX-search/TX-
  output bandpass filter pair.
- **7e** `AfcTracker.cs` (147) + `LevelAgc.cs` (123) -- AFC/AGC tracking utilities.
- **7f** `RadixTwoFft.cs` (78) -- standalone FFT, last chunk of this batch.

Starting with 7a.

## Chunk 7a round 1 (2026-08-21) -- CLOSED, unconditional go, no round 2 needed

Full Tier A rigor round on the four foundational primitives (`TankFilter.cs`, `IirFilter.cs`,
`MovingAverage.cs`, `Vco.cs`, 233 lines total). `TankFilter.cs` was already independently verified
byte-for-byte during Batch 6 chunk 6a's own audit -- re-spot-checked fresh, not re-derived from
scratch, and the earlier finding held. `IirFilter.cs` and `Vco.cs` had NO legacy line citations at
all in their own comments; both fully re-derived from the real legacy source (`fir.cpp`'s free
`MakeIIR`/`CIIR::MakeIIR`/`CIIR::Do`, `sstv.cpp`'s `CVCO::SetGain`/`SetFreeFreq`/`Do`) rather than
trusted from any prior claim.

**Zero functional bugs.** `IirFilter.Design`'s bilinear-transform coefficient math and `Process`'s
cascade application confirmed exact against the real `MakeIIR`/`CIIR::Do`, including the specific
claim that legacy's Chebyshev branch is real but genuinely unused by every `sstv.cpp` demod-path
call site (verified by finding every `bc=` call argument, not trusted from the comment).
`MovingAverage`'s three methods confirmed against `CSmooz`, including the specific `SetCount(n)`-
equals-existing-capacity condition claimed to trigger the "clear to empty" branch (found the real
call site, `sstv.cpp:1660`'s `InitAFC`, and confirmed `n` really does equal existing capacity
there). `Vco`'s table-lookup oscillator confirmed exact against `CVCO`, including that a truncated
ctor-default field legacy has is correctly NOT reproduced (legacy's own real callers always
overwrite it before use).

Two real nits fixed in `Vco.cs`, both cheap and legacy-faithful even though currently unobservable
(every real caller sets gain/frequency before `Process`): the sine-table build now precomputes the
angle increment once before the loop, matching legacy's own FP operation order (previously ~1 ULP
off per table entry from a different association); the constructor now sets the same default gain
legacy's own ctor does, as defense-in-depth for a future caller that forgets `SetGain`. Two doc-
citation fixes: `MovingAverage.cs`'s `Clear` doc pointed at a call site that doesn't unambiguously
prove the claimed branch; repointed to the real exact-match call site. A test comment in
`SlantTests.cs` named the wrong method (said `Reset()`, meant `Clear()` -- the two have opposite
seeding behavior).

**Four real `[risk]`-level coverage gaps closed with mutation-verified tests, not just noted** --
every one of this chunk's four files had either no direct test or a test loose enough to pass under
a materially wrong implementation. `TankFilter`'s only prior coverage asserted `onFreq >
offFreq*5`, loose enough to pass with legacy's own unused `#if 0` coefficient variant or a missing
denormal flush. `IirFilter` and `MovingAverage` had zero direct tests. `Vco` had zero test coverage
anywhere in the suite. Added `TankFilterTests.cs`, `IirFilterTests.cs` (covering both the even-order
and the odd-order-with-tail code paths, the latter otherwise unexercised anywhere in the suite),
`VcoTests.cs`, and `MovingAverageTests.cs` -- all pinning actual output values independently
computed (Python, double precision, replicating each formula's exact operation order) rather than
read back from the port's own output. All four mutation-verified: a coefficient-formula change in
each of the four files makes its corresponding new test fail, confirmed one mutation per file before
trusting the set.

Auditor's verdict: unconditional go for production as-is -- no round 2 needed for a round that found
no functional bug, same rigor rule as this batch's own precedent.

Full `ScanlineStudio.Core.Sstv.Tests` suite confirmed green: 1235/1236 (1 unrelated intentional
skip).

## Chunk 7b round 1 (2026-08-21) -- CLOSED, unconditional go, no round 2 needed

Full Tier A rigor round on `HilbertFmDemodulator.cs` (314 lines, this port's main picture
demodulator, the largest and most heavily pre-scrutinized file in this batch -- already through
"two rounds of independent auditor plan-review" plus several documented and fixed subtle bugs, all
re-verified fresh rather than trusted).

**Zero functional bugs across all eight audited points**, each independently re-derived from real
legacy source rather than accepted from the file's own (extensive, and in this case accurate) doc
comments: the tier-selection thresholds and multiplier formula; `DoFir`'s delay-line shift
direction, including a from-scratch re-derivation of WHY the reversed pairing is what makes the
overall sign convention correct (traced the actual antisymmetry of the Hilbert kernel, confirmed
`H[tap-n] = -H[n]`, confirmed the resulting `atan2` sign chain algebraically); `MakeHilbert`'s
Hamming-window sinc-difference design and the claimed-unreachable branch; `ComputePhaseDifference`'s
`lag = 2^df` shift-register mechanism, hand-simulated fresh for both df=1 and df=2; the scaled-not-
Hz smoothing-filter domain, confirmed by verifying `IirFilter`'s own DC gain is exactly 1 for both
the even and odd-tail cascade shapes; and the `real == 0.0` atan2-skip special case.

**One genuine, high-value correction to the class's own "representationally inert" claim** -- the
file asserted the `isNarrow` selection's cancellation holds "at steady state AND dynamically," but
independent algebraic re-derivation found only the multiplicative `out`/`bandwidthHz` term passes
through the linear smoothing filter unchanged at every sample; the additive `off`/`centerHz` term
only cancels once the filter's own step response has settled, meaning `isNarrow` genuinely affects
output during any transient -- not just "right at a mid-stream flip" as the comment claimed, but
also at cold start (a freshly constructed narrow instance reads near 2172Hz on sample 0, not
1900Hz). Not a behavioral bug (the code is legacy-faithful either way, and this port's decoder
constructs the demodulator once at stream start before any mode is known, so this window is
already accounted for elsewhere) -- but the comment was self-inconsistent with the transient it
documents six lines later, and this file's comments are load-bearing for future reviewers. Doc
corrected to state the real, narrower boundary.

**One real `[risk]`-level vacuous test closed, not just noted -- exactly the "passes its own tests
while wrong" failure class this whole sweep exists to catch.** The one test naming the `real ==
0.0` atan2-skip guard fed an all-zero input, so `quadrature` and `real` were both exactly zero --
but `Math.Atan2(+0.0, +0.0)` already returns `+0.0` on .NET, meaning the guard and its absence
produce the identical result for that specific input; deleting the guard entirely would not have
failed the test. Renamed the original test to describe what it actually proves (silence settles to
0Hz, a real and separate claim) and added a genuinely discriminating test using an impulse: at
tap=12/htap=6 (11025Hz), the delayed real component stays exactly 0 for the first 6 calls while the
FIR's quadrature output does not, so a guard-free impulse response diverges from a guard-free
silence response starting at call 0 -- mutation-verified (removing the guard makes the new test
fail exactly as predicted). Also fixed a nit: a warm-up-lag test's name overclaimed what it
actually asserts (only the steady-state region, not the warm-up calls themselves) -- corrected the
name and added a note, no logic change.

Auditor's verdict: unconditional go for production as-is -- no round 2 needed for a round that found
no functional bug, same rigor rule as this batch's own precedent.

Full `ScanlineStudio.Core.Sstv.Tests` suite confirmed green: 1236/1237 (1 unrelated intentional
skip).

## Chunk 7c round 1 (2026-08-21) -- CLOSED, unconditional go, no round 2 needed

Full Tier A rigor round on `PllFmDemodulator.cs` (143 lines) + `ZeroCrossingFrequencyCounter.cs`
(129 lines) -- the other two `DemodType` options besides `HilbertFmDemodulator`, both with real
documented fix history from prior review (`ZeroCrossingFrequencyCounter`'s two round-1/round-2
`SetWidth` fixes; `PllFmDemodulator`'s own documented AGC-floor characterization). Also closed the
item chunk 7b's own audit flagged off-scope: whether `PllFmDemodulator` omits legacy's
`g_dblToneOffset` the same way `HilbertFmDemodulator` was confirmed to safely omit it.

**Zero functional bugs across every audited point**, each independently re-derived from real
legacy source: `PllFmDemodulator`'s AGC tracking, clamp placement (confirmed `±1.5` applies
in-place immediately after the loop filter, before both the VCO and output-filter calls -- not
after), and `SetWidth`'s confirmed-true claim that legacy touches only `SetFreeFreq`/`SetVcoGain`,
never resetting filter state; `g_dblToneOffset` independently re-confirmed 0.0 on every path both
files can reach (re-traced the actual global, not assumed transferred from chunk 7b's finding);
`ZeroCrossingFrequencyCounter`'s sub-sample crossing interpolation, both previously-fixed
`SetWidth` behaviors re-derived fresh from `sstv.cpp:367-383` (not re-read from their own fix
history), and `Clear()`'s scope.

**One genuine correction to an overstated "mathematically identical" claim**, the same class of
finding as chunk 7b's "representationally inert" correction -- algebraically re-derived (confirmed
`IirFilter`'s DC gain is exactly 1 for both cascade shapes it's used in) that filtering real Hz
directly versus legacy's internal normalized scale is identical ONLY once the output filter has
settled, not unconditionally: legacy's filter Z-state lives in normalized units, so a `SetWidth`
implicitly rescales it too (legacy's reported Hz jumps ~272Hz instantly at a width flip), while
this port's Hz-domain state glides toward the new value instead over ~17 samples. Bounded,
accepted, and the port arguably behaves better than legacy here -- but the doc was overstating
unconditional equivalence. Corrected in both `ZeroCrossingFrequencyCounter.cs` and the same
overstated claim it was echoed into in `AfcTracker.cs`. Also fixed a stale class-doc claim
(`ZeroCrossingFrequencyCounter` said it was used "exclusively" for AFC, when it's also the main
picture demodulator under `DemodType.ZeroCrossing`) and collapsed `PllFmDemodulator`'s AGC-floor
comment, which read as describing a live defect, into an accurate statement of the invariant (every
current call site already feeds int16-domain samples, confirmed by tracing all three).

**Two real coverage gaps closed with mutation-verified tests.** `PllFmDemodulator`'s existing
silence test never actually reached the AGC's own division (an all-zero input never crosses zero,
so `_prevInput`'s `< 0` gate never fires) -- added a sub-floor alternating-signal test that does.
`ZeroCrossingFrequencyCounter`'s `count >= 1.0` sanity gate (discarding a sub-sample-spaced zero
crossing rather than reporting an above-Nyquist frequency) had no direct test at all -- added one
driven from a fresh counter with fully known state (an initial attempt reusing a settled counter
produced ambiguous sample-index assumptions and had to be rebuilt from scratch after mutation
testing showed it didn't discriminate), mutation-verified: reverting the gate to `>= 0.0` makes it
wrongly clamp to 2400Hz, caught exactly as predicted.

Auditor's verdict: unconditional go for production as-is -- no round 2 needed for a round that found
no functional bug, same rigor rule as this batch's own precedent.

Full `ScanlineStudio.Core.Sstv.Tests` suite run: Passed - Failed: 0, Passed: 1238, Skipped: 1, Total: 1239.

## Chunk 7c CLOSED

## Chunk 7d round 1: SearchBandpassFilter.cs, TxOutputBandpassFilter.cs

Verdict: EQUIVALENT-WITH-RISKS, unconditional go for production as-is. Every coefficient, constant,
branch condition, convergence-break ordering, normalization guard, mirroring bound, and convolution
direction in both files checked out against the real legacy source (`sstv.cpp:1522-1550,1596-1613,
1819-1834,2755-2830,2903-2930`, `fir.cpp:310-325,346-427,1079-1153`) -- zero functional bugs.

Two real findings fixed in this round:
- **Stale/backwards doc comment**: `TxOutputBandpassFilter.cs`'s doc comment claimed reusing
  `SearchBandpassFilter.MakeFilter` at `att=40` would "silently produce a rectangular-window filter,"
  premised on that class's Kaiser branch being provably unreachable -- true before Band-1 item 4b,
  false since (Narrow/VeryNarrow's H1 now use `att=40/50`, hitting Kaiser). The auditor confirmed the
  two implementations are operation-for-operation bit-identical at `tap=24, att=40`. Corrected the
  class doc comment and the `MakeFilter` inline comment to state the real (now-shared) reachability,
  not the stale hazard claim.
- **Real coverage gap**: `SearchBandpassFilter`'s existing `Constructor_PerPreset_TapCountReachesBuiltFilter`
  test only ever constructs at 11025Hz, where `(int)(multiplier * sampleRate / 11025.0)` degenerates to
  `multiplier` -- a regression dropping the sample-rate scaling entirely would pass undetected. Added
  `Constructor_PerPreset_TapCountScalesWithSampleRate_At44100Hz` (same causal-impulse-response
  technique, at 44100Hz so expected tap is 4x the base multiplier: 96/256/384). Mutation-verified:
  reverting `_tap` to just `multiplier` makes all three rows fail exactly as predicted.

Also fixed: two stale legacy line-number citations (`sstv.cpp:1530,1536,1542` actually cites H1's
lines; H2's real lines are `1531,1537,1543`, and the tap-assignment comment had the same off-by-one).
Added `Constructor_OffPreset_ThrowsArgumentOutOfRange` (previously-unpinned invariant, mutation-
verified: adding an `Off` arm to the preset switch makes it fail exactly as predicted). Filed
`docs/removed-features.md`'s new "TX output bandpass filter toggle/tap setting" entry for the
auditor's nit that legacy's `m_bpf` checkbox and user-editable `m_bpftap` are silently dropped (this
port always applies the filter at a fixed 24 taps) -- unaffected at shipped defaults, but a real
dropped capability per CLAUDE.md's removal rule.

Auditor's verdict: unconditional go for production as-is -- no round 2 needed for a round that found
no functional bug, same rigor rule as this batch's own precedent.

Full `ScanlineStudio.Core.Sstv.Tests` suite run: Passed - Failed: 0, Passed: 1242, Skipped: 1, Total: 1243.

## Chunk 7d CLOSED

## Chunk 7e round 1: AfcTracker.cs (full audit), LevelAgc.cs

Verdict: EQUIVALENT-WITH-RISKS, unconditional go for production as-is. Zero functional bugs -- the
auditor independently re-derived the two things most likely to be silently wrong (the `d -= 128`
calibration sign and the `m_AFCDiff` correction sign) from the real demodulator scaling rather than
trusting the existing comments, and both check out. AfcTracker's full `SyncFreq` state machine
(in-band/out-of-band branching, `_gardRemaining` countdown-then-reset, `_disabledSamplesRemaining`
cooldown, lock-average Reset-vs-Add selection) matched legacy statement-for-statement; LevelAgc's
`Fix()` gain formula, threshold, and cadence matched `CLVL::Fix` literally. (Note: chunk 7c had
already touched `AfcTracker.cs`, but only to correct one doc comment echoed in from
`ZeroCrossingFrequencyCounter.cs` -- this round is the first full audit of its own state machine.)

Fixes applied, all doc-comment/citation-level or coverage, no functional change except one exact-
expression-order correction:
- AfcTracker's opening doc sentence overclaimed "every sample" (contradicting its own later, correct
  gated-feed description) and omitted that the PLL demod path is the only one actually fed by
  `ZeroCrossingFrequencyCounter` -- corrected.
- Two stale legacy line citations (`sstv.cpp:1659`->`1669` for `m_AFCGard`'s InitAFC line, in both
  `AfcTracker.cs` and `AfcTests.cs`; `LevelAgc.cs`'s `1824-1834`->`1824-1833` for the LPF/BPF block).
- Reordered `_afcBeginSamples`/`_afcEndSamples`/`_shortAverage`'s window-size arithmetic to multiply
  before dividing, matching legacy's literal `AFCB = 1.5*SampFreq/1000.0` expression exactly (was
  divide-then-multiply -- algebraically equal at every currently-reachable sample rate, confirmed by
  re-running the full affected-test filter clean after the change, but not literally the same
  expression, so a future sample rate could round differently).
- `LevelAgc.cs`'s TX<->RX reset doc overstated that the accompanying bandpass-filter flush runs at
  both transition directions -- it's TX->RX only (`Sound.cpp:441`); the `Init()` call itself is at
  both, which is what this class's own contract actually depends on. Corrected.
- Softened `LevelAgc.cs`'s `Fix()` cadence claim: legacy's real UI-paint-timer interval isn't
  determinable from source (confirmed `Main.dfm` is binary, so even the VCL 1000ms default isn't a
  verified figure) -- this port's per-sample-cadence 100ms design-window choice makes its AGC gain
  and `CurMax` refresh materially faster than legacy's real runtime behavior, a real accepted
  divergence, not the value-neutral "more faithful" framing the comment previously used.
- Added a note on legacy's `m_AFCFlag = 15` (UI activity-lamp only, confirmed via `Main.cpp`, no
  DSP/decode effect) to `AfcTracker.cs`, matching `LevelAgc.cs`'s own precedent of documenting (not
  silently dropping) an omitted write-only field.
- Fixed a stale `LevelAgcTests.cs` doc claim that the class wasn't wired into any decoder call site
  yet -- it is (`AnalogFmSstvDecoder.cs:922-937`).

**One real coverage gap closed with a mutation-verified test.** `_disabledSamplesRemaining` (the
100ms post-lock cooldown) had no test ever driving it down to a genuine re-lock attempt. Added
`AfcTracker_CooldownActive_SuppressesRelockUntilExpired`: locks once, drops out of band for less
than the full cooldown, holds a genuinely different in-band reading well past the arm-to-lock window
(must NOT relock while cooldown is active), then exhausts the remaining cooldown and confirms the
same reading DOES relock. Sample-exact hand-derived expected values (mutation run against the
gate-removed version independently reproduced the predicted -3.7917 exactly, confirming the by-hand
math), mutation-verified: dropping the `_disabledSamplesRemaining == 0` gate makes the "during
cooldown" assertion fail exactly as predicted.

Auditor's verdict: unconditional go for production as-is -- no round 2 needed for a round that found
no functional bug, same rigor rule as this batch's own precedent.

Full `ScanlineStudio.Core.Sstv.Tests` suite run: Passed - Failed: 0, Passed: 1243, Skipped: 1, Total: 1244.

## Chunk 7e CLOSED

## Chunk 7f round 1: RadixTwoFft.cs

NOT a legacy port (the file's own doc comment states this explicitly, and CLAUDE.md's port-first rule
is scoped to DSP/codec math that affects decoded-image correctness -- a waterfall spectrogram's exact
FFT doesn't). Reviewed instead for standalone standard-algorithm correctness: does this radix-2
decimation-in-time Cooley-Tukey FFT correctly implement the textbook algorithm.

Verdict: EQUIVALENT, unconditional go for production as-is. Zero functional bugs -- the auditor
hand-traced the bit-reversal permutation at n=8 (every index reaches its correct bit-reversed
partner, no missed/double swaps) and the butterfly/twiddle structure (confirmed the forward,
non-conjugated kernel `e^(-2*pi*i/len)`, correct complex product, correct DIT combine order, correct
twiddle recurrence). Both degenerate sizes (n=1, n=2) independently confirmed as genuinely-correct
no-ops/base-cases, not accidental. The only call site (`WaterfallSource.cs`) can't alias the two
spans or hit either throw path. Float-precision twiddle-recurrence error at this file's realistic
sizes (≤2048) is ~80-100dB below anything a spectrogram display resolves -- real but immaterial,
not a defect.

Fixes applied: documented the previously-unstated non-aliasing requirement between `real`/`imag`
(safe today, only matters for a future second caller).

**Real coverage gap closed with a mutation-verified test.** Every existing test fed a real-valued
(all-zero `imag`) input and asserted only magnitudes -- a conjugated (wrong-sign) FFT kernel would
have passed every one of them unchanged (the cosine test even explicitly accepts either a bin or its
mirror). Added `Forward_ComplexExponentialAtBinK_PeaksExactlyAtThatBin_NotItsMirror`, feeding a
genuine complex exponential input (the one signal that distinguishes the two sign conventions) and
asserting the peak lands at the exact bin, not its mirror -- also closes the "non-zero `imag` input
never exercised" gap in the same test. Mutation-verified: flipping the twiddle sign to the conjugated
convention makes the peak land at bin 59 instead of 5 (n=64, targetBin=5), exactly as predicted.
Also added `Forward_LengthOne_IsANoOp` and `Forward_LengthTwo_MatchesHandComputedTransform`, pinning
the two previously-unasserted degenerate sizes with exact hand-computed values.

Auditor's verdict: unconditional go for production as-is -- no round 2 needed for a round that found
no functional bug, same rigor rule as this batch's own precedent.

Full `ScanlineStudio.Core.Sstv.Tests` suite run: Passed - Failed: 0, Passed: 1246, Skipped: 1, Total: 1247.

## Chunk 7f CLOSED

## Tier A Batch 7 -- CLOSED

DSP primitives (numeric fidelity, float-vs-double): `HilbertFmDemodulator.cs`, `PllFmDemodulator.cs`,
`ZeroCrossingFrequencyCounter.cs`, `SearchBandpassFilter.cs`, `TxOutputBandpassFilter.cs`,
`AfcTracker.cs`, `LevelAgc.cs`, `IirFilter.cs`, `MovingAverage.cs`, `TankFilter.cs`, `Vco.cs`,
`RadixTwoFft.cs` -- all 6 chunks (7a-7f) closed. Zero functional bugs found across the entire batch.
Real findings across the batch: one real coverage gap and two doc-overstatement corrections in 7a
(chunk 7a also added 4 new golden-value test files for previously-untested primitives); a vacuous
test replaced with a genuinely-discriminating one plus a doc-comment scope correction in 7b; one
overstated "mathematically identical" claim corrected (twice, echoed into a second file) plus two
coverage gaps closed in 7c; one stale/backwards cross-class doc comment plus one coverage gap plus a
dropped-setting `docs/removed-features.md` entry in 7d; the batch's most substantive round (7e, full
`AfcTracker.cs` audit) found zero functional bugs but several doc/citation corrections, one legacy-
fidelity expression-order fix, and one coverage gap; 7f (non-port, standalone-correctness review)
found zero functional bugs, one doc nit, and closed a real sign-convention coverage gap. Commits:
8e9ffdf, c451c1f, b2bb210, 51b2c82, e150259, 71c621f.

## Tier A Batch 8 -- IN PROGRESS, started 2026-08-21

Binary/encoding boundaries (approved batch plan, row 8). Six files, ~1021 lines total -- real sizes
confirmed by reading (not assumed from the plan table's own file list):
`src/ScanlineStudio.Core.Audio/WavFile.cs` (152, note: `Core.Audio`, not `Core.Sstv` -- the plan
table's own path was implicit), `FskStationIdEncoder.cs` (159), `FskStationIdWireFormat.cs` (101),
`StationIdCallsignNormalizer.cs` (40), `AnalogFmSstvEncoder.cs` (418, the TX encode core -- by far
the largest and highest-risk file in this batch), `CwMorseGenerator.cs` (151).

Chunking, smallest/most-isolated first:
- **8a** `WavFile.cs` (152) -- RIFF chunk-walking, standalone binary-format parsing, no legacy
  YONIQ/QSSTV counterpart to compare against (own port-equivalence question: does it correctly parse
  the WAV/RIFF spec, not "does it match legacy").
- **8b** `FskStationIdEncoder.cs` (159) + `FskStationIdWireFormat.cs` (101) +
  `StationIdCallsignNormalizer.cs` (40) -- the station-ID encode pipeline, three small related files
  (300 lines combined).
- **8c** `AnalogFmSstvEncoder.cs` (418) -- the TX encode core. Largest/highest-risk file in the
  batch; full Tier A rigor, budget for a possible round 2 given the file's size and that it's real
  TX-path DSP/codec logic (CLAUDE.md's port-first scope center).
- **8d** `CwMorseGenerator.cs` (151) -- CW/Morse code generation, last chunk of this batch.

## Chunk 8a round 1: WavFile.cs

No legacy counterpart (legacy has no WAV file I/O at all -- confirmed by the existing test file's own
doc comment). Reviewed as standalone RIFF/WAV binary-format correctness, including safety against
malformed/adversarial input (this parses files that could come from anywhere).

Verdict: EQUIVALENT-WITH-RISKS, go for production as-is (test-only usage today; fix before any
user-facing "open a WAV" feature). The writer's full-scale-edge clamp-before-narrow was independently
re-verified correct (the `(short)32768f` wrap claim matches real .NET/ECMA-335 narrowing-cast
semantics, and the post-scale clamp genuinely closes the gap for every input in [-1,1], not just the
tested cases). The extensible-format GUID wire-layout claim was independently re-verified correct
against the real Windows on-wire mixed-endian layout. Both existing guards (`data`-before-`fmt`,
unsupported bit-depth/channel-count) confirmed reachable and correctly gated.

**Two real risk findings fixed, both malformed-input robustness (not memory-unsafe -- the existing
`chunkSize` bound already prevented any OOB read/unbounded allocation/infinite loop):**
- A `fmt ` chunk declaring fewer than its required 16 format-field bytes was read past its own
  declared end without complaint (silent mis-parse/desync, not a crash). Added an explicit
  `chunkSize < 16` guard.
- 1-7 stray trailing bytes at EOF (a truncated download, a writer that appended junk) let the next
  header read leak a bare `EndOfStreamException` -- unlike every other malformed-input path in this
  method, which throws `InvalidDataException`/`NotSupportedException`. Added an explicit
  "8 bytes remaining" guard before each chunk-header read.

Also split a too-short WAVE_FORMAT_EXTENSIBLE extension (malformed data) from a genuinely non-PCM
SubFormat (valid-but-unsupported) into their own distinct exception types/messages -- they previously
shared one `NotSupportedException` message describing only the SubFormat case. Replaced the unknown-
chunk skip's `ReadBytes(chunkSize)` with `Seek`, avoiding a throwaway allocation for a large skipped
chunk (nit, not a functional bug).

**Six real coverage gaps closed, all mutation-verified** (each new/changed-behavior test confirmed to
fail under the specific code mutation it claims to catch, then the file restored to exactly the
intended fix set via `git diff --stat`): the `fmt`-too-small guard, the truncated-extension exception-
type split, the truncated-header-at-EOF guard, the last-chunk-odd-size-no-pad-byte case (the mirror of
an existing mid-file test), the unsupported-bit-depth/channel-count guard (both halves, 8-bit and
stereo), and a GUID wire-layout test using hardcoded literal bytes independent of `Guid.ToByteArray()`
(the existing extensible tests built their SubFormat bytes via `ToByteArray()`, which is self-
consistent with this class's own `Guid` parsing by construction and so couldn't have caught a wrong
wire layout -- the "both halves wrong the same way" pattern CLAUDE.md's behavioral-parity rule warns
about).

Auditor's verdict: go for production as-is (test-only usage today) -- no round 2 needed for this
non-legacy-port correctness review, same rigor rule as this batch's own precedent.

Full `ScanlineStudio.Core.Audio.Tests` filtered run: 24 passed, 0 failed. Full
`ScanlineStudio.Core.Sstv.Tests` suite run (WavFile is referenced from `SstvRoundTripTests.cs`):
Passed - Failed: 0, Passed: 1246, Skipped: 1, Total: 1247.

## Chunk 8a CLOSED

## Chunk 8b round 1: FskStationIdEncoder.cs, FskStationIdWireFormat.cs, StationIdCallsignNormalizer.cs

Real legacy port -- FSK station-ID wire protocol; byte-exactness matters since real legacy YONIQ/
MMSSTV stations must be able to decode this port's packets and vice versa.

Verdict: EQUIVALENT-WITH-RISKS, go for production as-is. Zero functional bugs -- every claimed-
verified behavior was independently re-derived from the real legacy source and confirmed correct: the
EOT-not-XORed claim on both the callsign and NR/RST-string checksum paths (verified independently,
not assumed transitively); the -0x20 offset's scope (text chars only, not STX/EOT/checksum bytes);
the NR/RST compact-eligibility predicate's full 5-condition logic (traced against `Main.cpp:6940`
statement-by-statement, including the `strlen`-vs-`sscanf` length/parse asymmetry); the compact form's
checksum SEED (0x02) vs the string form's checksum RESET (0), confirmed genuinely different constants
with only the string form sending EOT; the callsign normalizer's cap-then-uppercase-then-trim ORDER
(worked example: 20 leading spaces + callsign caps away the entire real callsign under this order,
matching legacy exactly); the signed-char filter's `>=0x30 and <=0x7F` reproduction of legacy's signed-
char comparison.

**Two doc-comment overstatements fixed** (both garbage-either-way for real input, not reachable bugs):
`FskStationIdEncoder.cs`'s claim that callers do "printable-range validation" was false --
`StationIdCallsignNormalizer` only caps/uppercases/trims, so a non-ASCII callsign character reaches
this class's own UTF-16 cast, diverging from legacy's CP932-byte-based offset the same way
`FskStationIdWireFormat.FilterNrRstChars`'s own doc comment already (correctly) acknowledges for the
NR/RST field -- just not previously admitted here. `StationIdCallsignNormalizer.cs`'s "faithful port"
claim was correct about ORDER but overstated about `.Trim()`'s character set (Unicode whitespace vs.
legacy's real space/tab-only trim) -- unreachable from a single-line settings field, but corrected.

**One contract nit fixed**: `IsCompactEligible`'s `value` out-param was left holding a real parsed
number on some `false`-return paths (an inconsistent "false means value=0" contract) -- no current
caller reads it when false, but fixed for correctness's own sake, not just caller discipline.

**Four real coverage gaps closed, all mutation-verified**: the `value==1000` inclusive round-trip
boundary (only 999/1234/0999 were tested, not the boundary itself); the `strlen`-vs-`sscanf`
trailing-garbage asymmetry at `l>=4` (only tested at `l<4` before -- `"999:"` false vs `"1234:"` true
is the exact case that changes outcome based on the PARSED value, not the full string's length); and
`FilterNrRstChars`'s two inclusive boundary bytes (0x30, 0x7F) which were only tested from the dropped
side before.

Auditor's verdict: go for production as-is -- no round 2 needed for a round that found no functional
bug, same rigor rule as this batch's own precedent.

Full `ScanlineStudio.Core.Sstv.Tests` suite run: Passed - Failed: 0, Passed: 1250, Skipped: 1, Total: 1251.

## Chunk 8b CLOSED

## Chunk 8c round 1: AnalogFmSstvEncoder.cs

Largest/highest-risk file in this batch -- the TX encode core, real TX-path DSP/codec logic. Full
Tier A rigor, pre-budgeted for a possible round 2.

Verdict: EQUIVALENT-WITH-RISKS, go for production as-is, **round 2 NOT warranted** (auditor's own
explicit call: "the pre-budgeted second round was insurance against this file's size/risk; the risk
didn't materialize"). Zero functional bugs -- all 7 checked claims held against direct legacy
verification: the running-accumulator truncation (traced by hand across consecutive segments,
confirmed the +/-1-sample-per-boundary bound), silence/VCO-phase-freeze behavior (confirmed
`m_vco.Do()` is genuinely skipped, not zeroed-and-restarted, in legacy), the output filter's
unconditional application (confirmed it's gated on `m_bpf`, which defaults to 1 -- unconditional
matches the shipped default), the OutHEAD-before-AVT-branch ordering and the AVT 3x-VIS-repeat
citation (both independently re-verified, not taken on the comment's own word), all four footer
branch combinations with an exact (not approximate) ms-to-samples unit conversion, the filter-then-
cap NR/RST ordering, and the FSK-then-CW independent-gating order.

**Several doc-comment overstatements fixed, none affecting output:**
- `EstimateSampleCount`'s doc comment and the accumulator's own inline comment both claimed matching
  legacy's TX-capture bit-exactness was a real goal of this port's specific operand order -- corrected:
  legacy's own accumulator uses a DIFFERENT operand order, and legacy's VCO is a quantized sine
  lookup table plus integer gain/mask stages, not this port's exact `Math.Sin` -- sample-exact legacy
  diffing was never actually reachable through this choice alone. Truncation is kept because it's the
  real ported behavior, not because it enables a diffing goal that doesn't exist.
- A genuine internal contradiction: one comment claimed the accumulator's rounding error is "bounded
  to +/-0.5 sample forever" (a round-to-nearest figure) while the code truncates (bounding error to
  [0,1), a one-sided lag) -- a second comment nearby already had this right. Corrected the wrong one.
- The silence branch's "phase must be skipped, not zeroed-and-restarted" framing was a no-op as
  written: for this branch's only reachable trigger (`frequencyHz` exactly 0), `phaseIncrement` is
  itself 0, so "skip the advance" and "advance by 0" are behaviorally identical -- the real-world
  bug this branch actually guards against is an explicit phase RESET, not the increment mechanism.
  Corrected the framing without changing the `<= 0` condition itself (kept for a hypothetical future
  negative-frequency segment).
- `CapNrRstTextForStationId`'s "exact (not just safe-but-lossy) bound" claim overstated what
  filter-then-cap guarantees -- capping the FILTERED string can still, in principle, flip a
  legacy-compact NR to this port's string form for a sufficiently long all-digit remainder (legacy
  has no TX-side cap at all to match against either way). Unreachable with any realistic RST/NR
  exchange, but corrected the "exact" claim.

**Real coverage gap closed with a mutation-verified test.** All of `AnalogFmSstvEncoderSilenceTests`'
silence coverage exercises `RenderSegments`, a hand-duplicated test-only copy of `EncodeAsyncCore`'s
own sample loop -- nothing pinned the two together, so a regression in the REAL production async
iterator's silence branch could have survived undetected. Added
`EncodeAsync_CwIdLeadingSilence_ProducesExactZero_OnTheRealEncodeAsyncPath`, using
`CwMorseGenerator`'s fixed 250ms leading `'@'` silence (comfortably past the output filter's 25-sample
ring-down settle time) on the real `EncodeAsync` path. Mutation-verified: disabling the silence branch
(forcing the tone branch unconditionally) produces a nonzero DC leak (`-0.0183...`) from the frozen-
but-nonzero phase, caught exactly as predicted. Building this test also caught a REAL self-inflicted
regression during the fix: an earlier edit to the silence branch's doc comment had accidentally deleted
the `sample = 0.0;` statement itself (a genuine build break, `CS0165`), caught immediately by the
build step before it ever reached a commit.

Auditor's verdict: go for production as-is, round 2 not needed -- explicit, reasoned call given the
file's pre-budgeted risk, not the default single-round precedent.

Full `ScanlineStudio.Core.Sstv.Tests` suite run: Passed - Failed: 0, Passed: 1251, Skipped: 1, Total: 1252.

## Chunk 8c CLOSED

## Chunk 8d round 1: CwMorseGenerator.cs

Last chunk of Batch 8. Real legacy port -- `WriteCWID`'s bit-packed dot/dash Morse table.

Verdict: EQUIVALENT-WITH-RISKS, go for production as-is. Zero functional bugs -- the auditor hand-
decoded and independently verified all 43 table entries against real ITU Morse (not a spot check),
hand-traced the bit-scan direction against 3 characters plus the auditor's own additional checks, and
confirmed the `'.'->'R'` remap, the `'/'` literal pattern, the `'@'` leading-silence special case, and
the raw-char-before-uppercasing non-ASCII ordering all match legacy exactly.

**Doc-comment overstatements fixed, all pre-existing-behavior-accurate, framing-only:**
- The "WPM UI value is never actually applied" legacy-bug claim was too absolute -- `sys.m_CWIDSpeed`
  is a GLOBAL legacy field legacy's OTHER CW-send call site (`SendCWID`) DOES write from the
  configured WPM, so a session that has sent CW manually even once picks up the WPM-derived dot
  length for subsequent post-image CW-ID too. The real, narrower bug: `OutputCWID` itself never
  performs that conversion, so only a session that never manually sent CW is stuck at legacy's
  hardcoded default. This port's fix is still correct either way; only the framing was overstated.
- `MillisecondsPerDotFromWpm`'s doc comment and its own test (`MillisecondsPerDotFromWpm_IsExactInverseOfLegacysOwnFormula`,
  renamed) both overclaimed exact inversion of legacy's real WPM-to-dot conversion -- the cited legacy
  line was actually the DOT-to-WPM display direction; legacy's real WPM-to-dot conversion lives
  elsewhere, rounds differently, and quantizes to a whole millisecond (WPM 28: legacy 40ms exactly vs.
  this port's ~39.64ms, inaudible but not bit-identical). Also fixed a sentence with the "~8% fast"
  direction stated backwards.
- A test comment claiming a non-ASCII char "masks to 0x40, one below 'A'" was numerically true but
  misleading about which code path it actually aliases into (0x40 is `'@'`, the special-cased
  250ms-silence branch, not an aliased table letter) -- corrected.

**One real coverage gap closed with a mutation-verified test.** Only `'A'` (plus `'/'` and `'.'`, the
two special-cased-outside-the-table characters) had any regression protection before this round -- 40
of the table's 43 hand-typed hex constants had zero coverage; a single-digit typo could silently
produce a plausible-sounding wrong letter. Added `Generate_EveryTableEntry_MatchesItsRealItuMorsePattern`,
a golden-vector `[Theory]` covering all 40 reachable non-empty entries (`0`-`9`, `=`, `>`, `?`, `A`-`Z`),
independently derived from real ITU Morse, not from the table itself. Mutation-verified: swapping the
`F`/`L` table entries (the auditor's own suggested example) makes both letters' rows fail exactly as
predicted.

Auditor's verdict: go for production as-is -- no round 2 needed for a round that found no functional
bug, same rigor rule as this batch's own precedent.

Full `ScanlineStudio.Core.Sstv.Tests` suite run: Passed - Failed: 0, Passed: 1290, Skipped: 1, Total: 1291.

## Chunk 8d CLOSED

## Tier A Batch 8 -- CLOSED

Binary/encoding boundaries: `WavFile.cs`, `FskStationIdEncoder.cs`, `FskStationIdWireFormat.cs`,
`StationIdCallsignNormalizer.cs`, `AnalogFmSstvEncoder.cs`, `CwMorseGenerator.cs` -- all 4 chunks
(8a-8d) closed. Zero functional bugs found across the entire batch. Real findings across the batch:
8a (not a legacy port -- standalone RIFF/WAV correctness) found and fixed two real malformed-input
robustness gaps plus six coverage gaps; 8b (real FSK wire-protocol port) found zero functional bugs,
two doc overstatements, one contract nit, and four coverage gaps; 8c (the batch's largest/highest-risk
file, the TX encode core) found zero functional bugs and four doc overstatements, closed one real
coverage gap that also caught a real self-inflicted regression during editing before it reached a
commit, and the auditor explicitly ruled out the pre-budgeted round 2; 8d (last chunk, the CW Morse
generator) found zero functional bugs, three doc overstatements, and closed one real coverage gap
(40 of 43 table entries previously untested) with a mutation-verified golden-vector test. Commits:
6242ce0, 16e4aa6, 434c5ca, c516591.

## Tier A Batch 9 -- IN PROGRESS, started 2026-08-22

Cross-thread publishing & native loading (approved batch plan, row 9). Real sizes confirmed by
reading: `src/ScanlineStudio.Core.Sstv/WaterfallSource.cs` (113), `src/ScanlineStudio.Core.Audio.MiniAudio/MiniAudioDeviceEnumerator.cs`
(279), `MiniAudioContext.cs` (86), `MiniAudioResampler.cs` (50), `src/ScanlineStudio.Core.Radio.Hamlib/HamlibNative.cs`
(171), `HamlibRuntime.cs` (76), `HamlibLibraryLocator.cs` (96), `HamlibVersionGate.cs` (35),
`NativeLibraryLoader.cs` (14) -- 920 lines total.

Note: `WaterfallSource.cs`'s scheduler/slow-subscriber contract (CLAUDE.md's concurrency rule) is
already explicitly documented on `IWaterfallSource`'s own doc comment (synchronous inline push,
mirrors `IRadioController.StateChanges`'s established pattern) -- this chunk verifies that claim is
ACCURATE against the real implementation, not that it's missing.

Chunking:
- **9a** `WaterfallSource.cs` (113) -- cross-thread publishing/scheduler contract, standalone.
- **9b** `MiniAudioDeviceEnumerator.cs` (279) -- largest file in the batch; also carries a specific
  carry-over item from Batch 1 round-4 (`ReleaseIfCompletedInTime`'s "timed-out close deliberately
  leaks the context reference, no log emitted" gap, flagged off-scope there -- check whether it needs
  the same fix Batch 1 already applied for the two session types).
- **9c** `MiniAudioContext.cs` (86) + `MiniAudioResampler.cs` (50) -- the rest of the MiniAudio native
  audio layer (136 lines combined).
- **9d** `HamlibNative.cs` (171) + `HamlibRuntime.cs` (76) + `HamlibLibraryLocator.cs` (96) +
  `HamlibVersionGate.cs` (35) + `NativeLibraryLoader.cs` (14) -- all 5 Hamlib native-loading files as
  one chunk (392 lines combined), last chunk of this batch.

## Chunk 9a round 1: WaterfallSource.cs

Verdict: EQUIVALENT-WITH-RISKS, go for production as-is. Zero functional bugs -- the chunk's primary
reason for existing (verify the concurrency/scheduler contract claim) came back CONFIRMED: traced
`PushSamples` -> `EmitFrame` -> `_frames.OnNext` and found no `Task.Run`/`ObserveOn`/queue/lock
anywhere -- the interface doc's "synchronous and inline" claim is literally true, not just plausible.
The sliding-window buffering (multi-chunk fills, multi-window-per-call, exact-fill boundary), the
NaN/Inf guard (confirmed applied per-element, not just index 0 -- also confirmed `_hannWindow[size-1]`
is independently exactly 0f, the same hazard at the other window edge), the Hann window formula, and
the dB floor all checked out correct.

**Doc-comment fixes, all pre-existing-behavior-accurate, framing-only:**
- `IWaterfallSource.cs`'s "exactly mirroring `IRadioController.StateChanges`" overstated in two real
  ways: `StateChanges` catches and contains a throwing subscriber (this class doesn't -- contained in
  practice by the production fan-out's own try/catch, not by this class), and `StateChanges` is a
  `BehaviorSubject` (replays the last value) while `Frames` is a plain `Subject` (the right choice for
  a rolling display, just not "the same pattern"). Corrected the framing.
- Added the explicit single-producer-only requirement to `IWaterfallSource.cs`'s contract -- the
  previous wording ("from whatever thread called PushSamples") could be read as "any thread is fine,"
  when the internal accumulator has no synchronization of its own and two concurrent callers would
  silently interleave. Safe today (single documented drain thread), now stated as a real requirement
  for a future second caller.
- `WaterfallSource.cs`'s "legacy clamps... so a non-finite raw sample can never reach its FFT" claim
  was inaccurate for NaN specifically -- legacy's clamp is a plain `>`/`<` comparison, false for NaN,
  so legacy actually lets NaN pass through UNCLAMPED (only +-Inf gets clamped). This port's guard is
  strictly wider (Inf AND NaN), so the practical outcome is unaffected; only the citation was wrong.

**Two real coverage gaps closed, both mutation-verified.** Every existing multi-frame test asserted
only frame COUNT, never frame CONTENT -- a bug retaining the wrong half of the window across a hop
boundary would have passed every one of them. Added
`PushSamples_OverlapRetention_CarriesForwardTheCorrectHalf_NotTheWrongOne` (a tone confined to the
first hop only, followed by silence -- the correctly-retained second frame must show near-floor
silence, not leaked tone energy), mutation-verified: retaining the wrong (first) half instead of the
correct (second) half makes the second frame show real energy (~-13dB instead of near-floor), caught
exactly as predicted. Also added `PushSamples_HopSizeEqualsWindowSize_NoOverlap_EmitsBackToBackFrames`
for the previously-unexercised no-overlap boundary (`keep=0`), mutation-verified against a plausible
future off-by-one in the hop-size validity check.

Auditor's verdict: go for production as-is -- no round 2 needed for a round that found no functional
bug, same rigor rule as this batch's own precedent.

Full `ScanlineStudio.Core.Sstv.Tests` suite run: Passed - Failed: 0, Passed: 1292, Skipped: 1, Total: 1293.

## Chunk 9a CLOSED

## Chunk 9b round 1: MiniAudioDeviceEnumerator.cs

Not a legacy port (legacy has no cross-platform native-audio abstraction) -- standalone concurrency/
lifecycle/native-interop review. Carries this batch's flagged carry-over item from Batch 1 round-4.

Verdict: EQUIVALENT-WITH-RISKS, **no-go until the carry-over gap was fixed, then go**. Zero functional
bugs, but the auditor gave an explicit conditional verdict (fix finding 2 first) rather than an
unconditional go, since this chunk's whole reason for existing was that specific item. The `_gate`
lock's TOCTOU-closing claim was verified correct for the historical race it documents (traced by
hand, confirmed no deadlock, confirmed no lock-ordering hazard with `MiniAudioContext`'s own lock).
The torn-pair `InputDevices`/`OutputDevices` doc claim was independently verified accurate on both
halves (the pair CAN tear, but `volatile DeviceSnapshot` really does keep each individual property
internally consistent -- traced the actual mechanism, not just trusted the comment). The `count<0`
vs `count<=0` enumeration/probe-failure distinction, native-buffer truncation safety, and the
`AggregateException`-means-faulted-not-timed-out reasoning in `Dispose()` all checked out correct.

**Carry-over gap confirmed real and fixed.** `ReleaseIfCompletedInTime` silently leaked this
instance's `MiniAudioContext` reference on a `RefreshAsync` timeout with zero observability -- unlike
`MiniAudioCaptureSession`/`PlaybackSession`, which expose a `TimedOutDuringClose` property
`MiniAudioEngine` checks and logs, this class had no surviving object for anyone else to read a
signal off, and its own `_logger` field went unused for this case. Fixed by logging directly at the
point of detection (matching `MiniAudioCaptureSession`'s own construction-failure close path, which
does the same for the identical "nobody left to ask" reason) -- new `Log.RefreshTimedOutDuringDispose`
warning, `[LoggerMessage]`-pattern per this project's mandatory CA1848 rule. Also closed a related
asymmetry the auditor flagged while fixing this: `DisposeAsync` (confirmed the actual production
dispose path for a DI singleton implementing both `IDisposable`/`IAsyncDisposable`) never logged a
faulted in-flight refresh at all, unlike the synchronous `Dispose()` path -- now logs via the same
existing `RefreshFaultedDuringDispose` message.

**Doc-comment overstatement fixed.** The `_gate` doc comment's TOCTOU-closing claim didn't state that
`_refreshTask` is a single slot, not a set -- two concurrent `RefreshAsync` calls leave the earlier
task untracked, so `Dispose` only waits on (and bases its release decision on) the later one.
Confirmed low-risk by tracing into the native shim directly (the same mutex guards enumerate/probe
AND context-uninit, so an orphaned earlier refresh can't be mid-call when the context tears down --
it just fails cleanly with an Error-level log), but the doc's implied "no in-flight work survives
Dispose" guarantee was corrected to the narrower true one.

**One real coverage gap closed with a mutation-verified test.** `TryClaimDispose`'s `if (_disposed)`
guard is the only thing preventing a double `MiniAudioContext.Release()` call, which would corrupt
the process-wide refcount for every other enumerator/session sharing it -- previously untested. Added
`DisposeThenDisposeAsync_SecondDisposalIsANoOp_DoesNotDoubleReleaseTheContext`, mutation-verified:
disabling the guard produces a real `InvalidOperationException: Release() called without a matching
Acquire()`, caught exactly as predicted. (This environment has a live PipeWire server -- all
`[RequiresPipeWireFact]`-gated tests, including the new one, actually ran, not skipped.)

Not fixed (auditor's own explicit recommendation): the timeout branch of the new log line itself has
no test, since covering it needs a real design change (an injectable timeout + a stall hook) solely
for that coverage -- shipping the log without a test, documented honestly, per the auditor's own
"don't add a production seam solely for this" call. Two other low-priority coverage gaps (a
concurrent-`RefreshAsync` test, a synchronous-`Dispose()`-race test) also left open on the same
reasoning.

Auditor's verdict: go for production as-is once the carry-over fix landed -- no round 2 needed.

Full `ScanlineStudio.Core.Audio.MiniAudio.Tests` run: 84 passed, 0 failed (real run, not skipped).

## Chunk 9b CLOSED

## Chunk 9c round 1: MiniAudioContext.cs, MiniAudioResampler.cs

Not legacy ports (no legacy cross-platform native-audio abstraction) -- standalone concurrency/
native-interop review. Neither file has a dedicated test file; coverage was entirely indirect before
this round.

Verdict: EQUIVALENT-WITH-RISKS, go for production as-is. Zero functional bugs -- `MiniAudioContext`'s
ref-counting was independently re-verified correct (a failed native init throws BEFORE incrementing
the ref count, so no phantom reference requiring an unmatched `Release()`; the class's own claim that
"everything is serialized under `Lock`" was verified true for BOTH reads and writes of both fields,
not just writes, by grepping every reference in the assembly). `MiniAudioResampler`'s documented
overflow-guard fix was re-verified correct as written; the 1:1-sample-rate path and the native
`written <= capacity` contract were both independently confirmed safe by reading the actual native
shim/miniaudio source, not assumed.

**One real latent-bug-shaped gap fixed** (unreachable today -- `internal`, single caller, constant
real sample rates -- but validated explicitly rather than left as an implicit assumption): the
overflow guard only ever caught an over-large output. A non-positive sample rate makes the internal
capacity arithmetic go negative, which previously passed that guard silently and would have thrown an
opaque `OverflowException`/native-failure `InvalidOperationException` instead of a clear, actionable
one -- exactly the failure mode the existing guard's own comment says it exists to prevent. Added an
explicit `sampleRateIn <= 0 || sampleRateOut <= 0` guard.

**Two real coverage gaps closed with mutation-verified tests**, both in a new dedicated (device-free,
always-CI-runnable) test file `MiniAudioResamplerTests.cs`: the pre-existing overflow guard itself had
never been tested at all (mutation-verified: disabling it reproduces the exact `OverflowException` the
guard exists to prevent), and the new rate-validation guard (mutation-verified across all 4
sign/zero combinations: removing it reproduces `OverflowException` for negative rates and a native-
failure `InvalidOperationException` for zero, never a clean `ArgumentException`).

Noted but not fixed, per the auditor's own explicit reasoning: `MiniAudioContext` has zero CI-executed
coverage (every test touching it is gated on a live PipeWire/PulseAudio server this project's own CI
matrix doesn't provision -- a pre-existing, already-documented environment fact about the whole audio
suite, not a defect introduced by this chunk); empty-input `Resample` calls currently throw rather
than returning an empty array (unreachable today, undocumented-but-not-a-bug); `Release()`-without-
`Acquire()`'s error path and the failed-`Acquire()`-leaves-no-phantom-ref claim both have no direct
test (the latter has no seam to test at all without the DI refactor this class's own doc comment
already declines).

Auditor's verdict: go for production as-is -- no round 2 needed for a round that found no functional
bug, same rigor rule as this batch's own precedent.

Full `ScanlineStudio.Core.Audio.MiniAudio.Tests` run: 89 passed, 0 failed.

## Chunk 9c CLOSED

## Chunk 9d round 1: all 5 Hamlib native-loading files

Last chunk of Batch 9. Not legacy ports (CAT/rig control is explicitly not ported) -- real P/Invoke
boundary code: a manually-laid-out C union struct, explicit UTF-8-with-null-terminator string
marshaling, per-OS library discovery, eager-constructor version gating.

Verdict: EQUIVALENT-WITH-RISKS, go for production as-is. Zero functional bugs -- every claim was
independently re-verified against the real Hamlib source (a local clone, not the pinned spec headers
alone): the `value_t` union's 16-byte layout and float/int arm offsets confirmed against `rig.h`
directly; the UTF-8 null-terminator marshaling confirmed to never overrun for any input including
surrogate pairs and embedded nulls (`GetByteCount`/`GetBytes` use the same encoding instance, so they
always agree); the partial-construction-then-throw scenario in `Resolve<TDelegate>` confirmed
genuinely unreachable by C# constructor semantics (`this` never escapes, all fields `readonly`); the
`HamlibRuntime` eager-constructor rationale traced through the real `RadioController.ConnectAsync`
call chain and confirmed accurate; the version-gate parsing confirmed against real `rig_version()`
output shapes from `rig.c` directly.

**Real, confirmed doc-comment/citation drift fixed across three files** (the auditor's own framing:
"the kind of drift that makes the next reader mis-implement tier 3"): `spec/03-cat-layer.md` numbers
discovery as 3 tiers (1=override, 2=soname, 3=extra-dirs), but `HamlibProtocolFactory.cs` called the
override "tier-3" (should be tier-1) and `HamlibLibraryLocator.cs`'s own `BuildTier1And2Candidates`
method built tiers 2+3, not 1+2 (renamed to `BuildTier2And3Candidates`). Also corrected: the macOS-only
extra-directory tier confirmed as the spec's actual intended design (its own "e.g." wording), not an
accidental Windows/Linux omission; `HamlibRuntime.cs`'s "~6 TryLoad attempts" overstated the real
per-OS counts (1 on Linux, 2 on macOS/Windows).

**One real latent-bug-shaped gap fixed** (unreachable today -- no caller passes an override path yet
-- but a real footgun for a future Settings wiring pass): `HamlibLibraryLocator`'s override check used
`is not null`, which treats an empty/whitespace-persisted override the same as a real path, disabling
auto-detection for nothing and failing with a confusing message. Changed to
`!string.IsNullOrWhiteSpace`.

**Three real coverage gaps closed, two mutation-verified, one honestly NOT mutation-verified with the
reason stated:**
- The missing-export-during-construction path (`Resolve<TDelegate>` throwing inside
  `HamlibNativeFactory.Create`) had no test -- added `Constructor_MissingNativeExport_IsAvailableFalse_AttemptsPreserved`,
  mutation-verified (moving `factory.Create()` outside the exception-handling try/catch makes the
  exception propagate unhandled, caught exactly as predicted).
- The empty/whitespace-override fix had no test -- added
  `Locate_OverridePathIsEmptyOrWhitespace_FallsBackToAutoDetection`, mutation-verified (reverting to
  `is not null` reproduces the confusing "not found" failure for both empty and whitespace-only
  inputs, exactly as predicted).
- The real integration suite (`HamlibDummyRigIntegrationTests.cs`, real libhamlib, no fakes) never
  exercised `HamlibNative.cs`'s only string-marshaling call sites (`rig_token_lookup`/`rig_set_conf`)
  at all -- every existing test constructs `HamlibRadioProtocol` with no serial port, so the
  `value is null` guard skips both calls entirely. Added
  `SerialPortConfigured_RealRigTokenLookupAndSetConf_UtfMarshalingRoundTripsAgainstRealHamlib`
  (confirmed to actually run against this machine's real libhamlib, not skip -- 149-169ms, not an
  instant no-op). **Honest limitation, not swept under the rug**: attempted to mutation-verify this
  against a missing-null-terminator bug (removing the trailing `0x00` byte) and the mutation did NOT
  reliably fail the test -- a missing null terminator is native heap-buffer-overread undefined
  behavior, not something a managed-code assertion can deterministically catch (it may or may not
  manifest as an observable difference depending on adjacent heap contents at runtime). The test's
  real value is proving the round-trip doesn't crash/misbehave against real Hamlib -- a genuine,
  previously-entirely-missing integration-coverage gain -- not serving as a reliable regression guard
  for that one specific defect class.

Also fixed two unrealistic test-fixture strings (`x86_64-pc-linux-gnu` -> `64-bit`, matching real
Hamlib's actual `rig_version()` output format) in `HamlibVersionGateTests.cs`/`FakeHamlibNative.cs`.

Noted but not fixed, per the auditor's own explicit reasoning (a CI/infra decision, not a code
change): the real P/Invoke layer (union marshaling, all 14 delegate resolutions) has zero CI-executed
coverage on this project's actual CI matrix, since no workflow leg installs libhamlib and the
integration tests self-skip-as-passed when it's unavailable -- a pre-existing, already-documented
environment fact (same shape as the MiniAudio/PipeWire gap chunk 9c already noted), not a defect
introduced by this chunk.

Auditor's verdict: go for production as-is -- no round 2 needed for a round that found no functional
bug, same rigor rule as this batch's own precedent.

Full `ScanlineStudio.Core.Radio.Tests` run: 134 passed, 0 failed.

## Chunk 9d CLOSED

## Tier A Batch 9 -- CLOSED

Cross-thread publishing & native loading: `WaterfallSource.cs`, `MiniAudioDeviceEnumerator.cs`,
`MiniAudioContext.cs`, `MiniAudioResampler.cs`, all 5 Hamlib native-loading files -- all 4 chunks
(9a-9d) closed. Zero functional bugs found across the entire batch. Real findings across the batch:
9a (WaterfallSource's cross-thread scheduler contract) independently CONFIRMED accurate, found 3 doc
overstatements and closed 2 real coverage gaps; 9b closed this batch's own flagged carry-over item
from Batch 1 round-4 (a silent context-reference leak with zero observability, now logged) plus one
real coverage gap; 9c found one real latent-bug-shaped gap (a one-sided overflow guard) and closed 2
coverage gaps in a brand-new always-CI-runnable test file; 9d (the P/Invoke boundary, real native
interop) found zero functional bugs but real doc/citation drift across 3 files, one real latent-bug
fix, and closed 3 coverage gaps (one honestly reported as not mutation-verifiable, a native-UB
limitation stated rather than hidden). Commits: 8c27f07, 1563148, 39eea92, a4c46b6.

## Tier A Batch 10 -- IN PROGRESS, started 2026-08-22

Orchestration/utility (approved batch plan, row 10 -- the plan table's own note: "consider demoting
to Tier B rigor"). This is the LAST batch on the approved plan table -- Tier A is done once this
closes. Real sizes confirmed by reading:
`src/ScanlineStudio.Application/TemplateStore.cs` (249), `MacroTextResolver.cs` (175),
`MaidenheadLocator.cs` (120, plan table's own note: "pure function, one round is plenty"),
`OptionsSettingsService.cs` (285), `LogbookSessionService.cs` (150), `RadioSessionService.cs` (139) --
1118 lines total.

Rigor note: per the plan table's own downgrade guidance and this batch's actual content (settings/
template/macro orchestration, not DSP/concurrency/control-flow), each chunk gets ONE audit round with
no round-2 budget by default -- escalate only if a round actually finds something real, matching this
sweep's own established "single clean round closes a chunk" precedent, applied from the start here
rather than needing a clean round to earn it.

Chunking:
- **10a** `MaidenheadLocator.cs` (120) -- pure function (grid-square <-> lat/lon conversion), no
  side effects, standalone.
- **10b** `TemplateStore.cs` (249) + `MacroTextResolver.cs` (175) -- related template/macro-text
  subsystem (424 lines combined).
- **10c** `OptionsSettingsService.cs` (285) + `LogbookSessionService.cs` (150) +
  `RadioSessionService.cs` (139) -- orchestration/session services (574 lines combined), last chunk.

## Chunk 10a round 1: MaidenheadLocator.cs

Not a legacy port (confirmed via a real grep across the whole legacy tree -- zero DIST/BEAM
references anywhere). Pure function, single round per this batch's own downgraded rigor.

Verdict: go for production as-is. Real functional bug found and fixed -- for an exactly antipodal
grid pair, the haversine intermediate `a` is mathematically 1.0 but the floating-point sum of the two
squared terms can land 1 ulp above 1.0, making `1 - a` negative and `Math.Sqrt` produce NaN, silently
rendering "NaN km" in a TX overlay instead of a real distance. Antipodal grid pairs are genuinely
reachable (arbitrary operator-typed grids, e.g. JN58/AE51). Fixed with `Math.Max(0.0, 1 - a)`. All
other math (Maidenhead field/square/subsquare cell conversion, haversine distance, forward-azimuth
bearing, the 360-wrap in `FormatBearing`) independently hand-traced and confirmed correct against the
standard definitions, not a legacy comparison since none exists.

**Fixed two doc-comment inaccuracies and one dead-code nit**: the subsquare-centering comment
incorrectly claimed the field/square math above it "already centers" (it computes the SW corner, not
a centered value -- all centering happens in the two branches below it); a real-world grid-label
comment misidentified JN58tc as Frankfurt (it's Munich) and FN31pr as "New York area" (it's Hartford,
CT, ~150km away) -- doesn't affect the test's wide assertion ranges, but misleads anyone re-deriving
expected values by hand; removed a redundant, unreachable `g.Length % 2 != 0` check (4 and 6 are both
already even).

**Real coverage gaps closed, all mutation-verified except one honestly reported as not
discriminating**: `TryToLatLon` previously had zero direct lat/lon value assertions -- the entire
parsing/centering math was validated only through a wide indirect distance range, so swapping the
4-character centering constants (+0.5 lon/+1.0 lat instead of the correct +1.0/+0.5) would have left
every existing test green. Added a value-pinning test with four independently hand-derived expected
values (re-verified by hand against the standard Maidenhead cell-size definitions, not copied from
the implementation), mutation-verified: the swapped-constant mutation fails exactly the predicted case
(`"JN58"` expects lat 48.5, mutant produces 49). Added the exact-360-wrap case for `FormatBearing`
(the one behavior its own doc comment specifically argues for), mutation-verified: removing the
`% 360` wrap produces `"360°"` instead of `"000°"` for both 359.5 and 359.6, exactly as predicted.
**Honest limitation**: the antipodal-NaN regression test (`JN58`/`AE51`) does NOT reliably fail when
the `Math.Max` clamp is removed -- on this platform, `sin²(48.5°)+cos²(48.5°)` happens to round to
<=1.0 via the Pythagorean identity, so the specific IEEE-754 overflow the fix guards against didn't
manifest for this pair in this test run. The underlying hazard is real and the fix is still correct
defensive code; the test pins current correct output but isn't proven to catch a regression removing
the clamp.

Auditor's verdict: go for production as-is -- no round 2 needed, per this batch's own single-round
default.

Full `ScanlineStudio.Application.Tests` run: 228 passed, 0 failed.

## Chunk 10a CLOSED

## Chunk 10b round 1: TemplateStore.cs, MacroTextResolver.cs

`MacroTextResolver.cs` is PARTLY a real legacy port (its `%`-token resolution is a scoped-down C#
port of legacy `MacroText`, `Main.cpp:10679-10833`); `TemplateStore.cs` is entirely new (no legacy
template-designer precedent). Single round per this batch's own downgraded rigor.

Verdict: go for production as-is. Zero functional bugs -- every legacy citation independently
re-verified line-by-line against the real source, including the two claims most likely to be hand-
waved: legacy's `p++` after `%` really is unconditional (a trailing lone `%` resolves to `%%` in both
legacy and this port, confirmed by tracing what happens to a NUL byte read at end-of-string in both),
and `LogConv.cpp`'s `MONT1[]` month-abbreviation array matches this port's own array in all 13
entries including the unused index-0 slot. .NET regex non-rescan semantics (a fill value containing
`{something}` is never re-resolved) and the `TemplateStore.SaveAsync` thumbnail-before-manifest
ordering guarantee (a thumbnail failure genuinely leaves zero manifest, not a partial one) were both
independently confirmed, not just trusted from their own doc comments.

**One real robustness gap found and fixed.** `ListAsync` had no guard against a `JsonException` from
a corrupt/truncated `template.json` -- since `File.WriteAllTextAsync` in `SaveAsync` isn't atomic (no
temp-file-then-rename), a crash/power-loss mid-save can leave a truncated manifest, and this
previously killed the ENTIRE rack listing (every other template too), not just the one bad template,
for a store whose whole design explicitly supports hand-copying/moving folders around. Fixed by
catching `JsonException` around just the read+deserialize, logging, and skipping only that one
template -- required adding `ILogger<TemplateStore>` to the class (CLAUDE.md's mandatory
`[LoggerMessage]` logging rule for changed I/O code), updated at all 3 call sites (DI resolves the new
constructor automatically; the 2 test-helper factories and 1 direct test construction needed an
explicit `NullLogger<TemplateStore>.Instance`).

**Fixed one doc-comment overstatement and one real doc omission (with a `docs/removed-features.md`
entry, per CLAUDE.md's removal rule):** the claim that "real amateur-radio callsigns cannot contain
`{`/`}`" was true by ITU/FCC convention but overstated as an enforced guarantee --
`OperatorSettings.Callsign` is explicitly unvalidated, so a user COULD type `{name}` into the callsign
box (still benign either way, just not literally unreachable). Also documented that legacy's `%D`/`%T`
apply the user's own clock-offset correction setting (`GetUTC`/`m_TimeOffset`, `ComLib.cpp:249-323`)
which this port has no equivalent for -- a real, previously-undeclared scope reduction, now recorded
in `docs/removed-features.md`'s new "Macro %D/%T clock-offset correction" entry.

**Two real coverage gaps closed, both mutation-verified.** `%T` -- one of only three ported legacy
token cases -- had zero test coverage; added a shape-only test (avoiding an hour/minute-rollover
race, same discipline as the existing `%D` test), mutation-verified: a wrong separator character
fails exactly as predicted. The corrupt-manifest fix itself had no test; added one exercising a real
truncated JSON file alongside a valid template, mutation-verified: removing the try/catch guard makes
the whole `ListAsync` call throw instead of skipping just the bad template, caught exactly as
predicted.

Noted but not fixed, per the auditor's own "currently unreachable" framing and this batch's
minimalism-first downgraded rigor: `DeleteAsync`'s recursive delete has no traversal guard on
`templateId`, but every current id source (`CreateTemplateId`, `Path.GetFileName`) structurally
cannot produce a path-traversal string -- speculative hardening against a caller that doesn't exist,
not currently justified.

Auditor's verdict: go for production as-is -- no round 2 needed, per this batch's own single-round
default.

Full `ScanlineStudio.Application.Tests` run: 230 passed, 0 failed. `ScanlineStudio.UI.Tests`
(TxImageEditorPaneViewModel tests, one call site there also updated): 310 passed, 0 failed.

## Chunk 10b CLOSED

## Chunk 10c round 1: OptionsSettingsService.cs, LogbookSessionService.cs, RadioSessionService.cs

Last chunk of Batch 10 and of the whole Tier A approved plan table. None of the three files are
legacy ports (all new orchestration/session-service code); single round per this batch's own
downgraded rigor.

Verdict: go for production as-is -- no round 2 needed.

`OptionsSettingsService.cs` was the chunk's primary risk area: the auditor traced all 37
`OptionsSnapshot` fields end-to-end across `Defaults`/`LoadAsync`/`SaveAsync` and confirmed zero
mismatches, zero dropped fields, zero inconsistent fallbacks. Two apparent asymmetries were checked
and confirmed correct, not bugs: `CwText ?? string.Empty` has no `NrRstText` counterpart because the
two fields have different read-path semantics, and the `SaveAsync` sample-rate fallback (lines
~159-161) matches real legacy behavior (`Option.cpp:421-424`, `CLOCKMAX=48500` from `ComLib.h:53`).
Two minor nits (a `SampleRate`-normalization divergence from legacy for out-of-range persisted
values, and a silent `ProcessPriority` downgrade for non-High persisted values) were explicitly NOT
flagged as needing fixes -- both are hardening-only or already contract-consistent. No fix needed;
zero functional bugs in this file.

**Two real bugs found and fixed in `RadioSessionService.TestConnectionAsync`, both mutation-verified.**
This method's own interface doc comment (`IRadioSessionService.TestConnectionAsync`) promises it
always returns a result, never throws.
1. `matches[0].Create(spec)` ran OUTSIDE the try/catch -- a throwing factory (e.g. a misconfigured
   backend) propagated straight out, breaking that contract. Fixed by moving `Create` inside an outer
   try that also wraps the existing inner try/catch/finally.
2. The `finally` block's `await protocol.DisposeAsync()` had no guard of its own -- a throwing
   `DisposeAsync` REPLACED whatever the inner try/catch had already decided to return, masking a real
   poll failure behind an unrelated teardown error. This is the identical hazard
   `RadioController.DisconnectAsync` already guards against for the same `protocol.DisposeAsync()`
   call; applied the same fix here (catch, log via new `Log.TestConnectionDisposeFailed`, don't
   rethrow). New test `TestConnectionAsync_PollThrowsAndDisposeAlsoThrows_ReportsTheOriginalPollFailure`
   double-faults on purpose (poll throws AND dispose throws) -- a single-fault test can't distinguish
   "the finally block's exception propagates" from "it's caught, the original result stands." New
   test `TestConnectionAsync_FactoryCreateThrows_ReturnsFailure_DoesNotPropagate` covers finding 1.
   Both mutation-verified: reverting each fix in turn reproduced the predicted unhandled-exception/
   masked-result failure, then the file was restored and confirmed via `git diff --stat` to match the
   intended fix exactly.

**One real bug found and fixed in `LogbookSessionService.LogQsoAsync`, mutation-verified.** The
method's own doc comment claims persistence "happens first" and "everything after this line is
best-effort and must never undo or block on it" -- but that wasn't actually enforced. Everything
after `_repository.AddAsync` (settings load, ADIF export, ADIF-UDP send, QRZ upload) ran unguarded,
so a throw there (e.g. a permissions error reading `settings.json`) propagated out of `LogQsoAsync`
AFTER the QSO record was already committed. Both real UI callers (`LogbookPaneViewModel`,
`QsoLinkWindowViewModel`) treat any thrown exception here as "logging failed" and re-enable their own
retry affordance -- a user retrying then creates a genuine DUPLICATE QSO record, since the first
attempt's persistence already succeeded. QRZ upload itself was already exception-safe
(`QrzLogbookUploader.UploadAsync` catches everything but cancellation); only the settings/export/
ADIF-UDP span needed the same treatment. Fixed by wrapping that span in a try/catch that logs via new
`Log.PostPersistStepFailed` and returns a degraded-but-honest `LogQsoResult` (record persisted and
returned, telemetry zeroed) instead of throwing. New test
`LogQsoAsync_PostPersistStepThrows_StillReturnsThePersistedRecord_DoesNotThrow` mutation-verified:
narrowing the catch clause to `OperationCanceledException` only reproduced the predicted unhandled
`IOException` propagating out of `LogQsoAsync`, then the file was restored and confirmed via
`git diff --stat` (43 insertions/19 deletions) to match the intended fix exactly.

Auditor's verdict: go for production as-is -- no round 2 needed, per this batch's own single-round
default.

Full `ScanlineStudio.Application.Tests` run: 233 passed, 0 failed (up from 230 at chunk 10b close --
3 new tests, all mutation-verified). No `ScanlineStudio.UI.Tests` re-run needed: neither
`RadioSessionService`'s nor `LogbookSessionService`'s constructor signature changed, and a grep
confirmed no UI test constructs either class directly.

## Chunk 10c CLOSED

## Tier A Batch 10 -- CLOSED

All three chunks (10a `MaidenheadLocator.cs`, 10b `TemplateStore.cs`/`MacroTextResolver.cs`, 10c
`OptionsSettingsService.cs`/`LogbookSessionService.cs`/`RadioSessionService.cs`) closed with a clean
single-round "go for production" verdict each, per this batch's downgraded rigor. Real bugs found and
fixed across the batch: MaidenheadLocator's antipodal-NaN bug, TemplateStore's corrupt-manifest
robustness gap, RadioSessionService's two TestConnectionAsync exception-contract violations, and
LogbookSessionService's post-persist exception-safety gap. All fixes mutation-verified; all real
coverage gaps closed in the same round they were found.

## TIER A -- FULLY CLOSED

All 10 batches of the approved plan table are closed. Summary across the whole sweep: ~50 files
audited across roughly 30 chunks, using the `auditor` subagent per chunk at either full 2-round rigor
(real control-flow/DSP/concurrency files) or single-round rigor (data tables, pure functions,
orchestration code per each batch's own downgrade note), with every real coverage gap closed by a
mutation-verified test in the same round it was found.

Real functional bugs found and fixed along the way (not an exhaustive list, the highlights): Batch
5's sync-interval scan-order bug, MaidenheadLocator's antipodal-NaN bug (Batch 10), TemplateStore's
corrupt-manifest robustness gap (Batch 10), RadioSessionService's TestConnectionAsync
exception-contract violations (Batch 10), LogbookSessionService's post-persist exception-safety gap
(Batch 10), WavFile's malformed-input handling gaps, and MiniAudioDeviceEnumerator's carry-over
logging gap from Batch 1. The large majority of individually-audited files came back clean --
zero functional bugs, only doc-comment/citation-drift corrections and mutation-verified coverage-gap
closures.

---

## Tier B -- IN PROGRESS, started 2026-08-22

Scope per this doc's own Tier table: remaining `Application`, `UI/ViewModels`, `Core.Logbook`,
`Core.Imaging` -- real state, no DSP/native code. Rigor: one round + one confirmation round per
file/group, cap 3. Step 0 triage done so far for `Core.Logbook` only (18 files: 11 real-logic, 7
trivial). Grouped-round plan and live status tracked in `PROJECT_BRIEF.md`.

## Chunk 1 (Core.Logbook): AdifExporter.cs, AdifImporter.cs, AdifRadioModeMapping.cs

**Round 1** -- NOT GO. Two blockers in the shared ADIF MODE-token mapping:
1. Export emitted four ADIF `MODE` tokens (`USB`, `LSB`, `DATA`, `PKTUSB`) that are not real ADIF
   3.x Mode-enumeration values -- the same text is POSTed to QRZ and broadcast over UDP to
   Log4OM/GridTracker, so those tools reject or mis-file the QSO.
2. Import mapped any unrecognized `MODE` token to `RadioMode.Unknown` AND unconditionally excluded
   `"MODE"` from the unmapped-fields notes bag, so the original token was unrecoverable -- a plain
   `MODE=SSB` from a third-party logbook (the single most common mode) lost its mode irrecoverably
   on import-then-re-export, contradicting spec/08-logging.md's "preserved, not dropped" requirement.

Fixed: `AdifRadioModeMapping.ToAdif` now returns `(Mode, Submode)` -- `Usb`/`Lsb` -> `SSB`+`SUBMODE`,
`Data`/`DataR` -> bare `SSB` (documented one-way-lossy default, no ADIF equivalent exists), `Pkt` ->
`PKT` (was `PKTUSB`). `AdifImporter.MapFields` now tracks a per-record `consumedModeFields` set and
only excludes MODE-family fields from the notes bag when a value was actually recovered from them.
Also fixed in the same round: a negative `<TAG:length>` crash (`ParseRecord` now guards
`byteLength < 0`), and `MODE=SSTV` with neither `SUBMODE` nor the APP field now also preserves the
raw token in notes instead of vanishing. Added Theory-based coverage for the full
`RadioMode`<->ADIF mapping table (all 12 values) and a non-ASCII UTF-8-byte-count regression test.
Two bugs in this session's own first-pass fix were caught by the new tests before round 2: a
companion `SUBMODE` field leaking into notes alongside `APP_SCANLINESTUDIO_SSTVMODE`, and a wrong
byte-length in one test's own ADIF fixture (self-inflicted, not a production bug).
107/107 `Core.Logbook.Tests` pass, clean build.

**Round 2** (fresh agent, full re-scan) -- NOT GO. One new blocker of the same failure class, this
time on export: `AdifExporter.WriteRecord`'s `if (SstvModeId is {}) / else if (Mode is {})` chain
was mutually exclusive, so `QsoRecord.Mode` was silently dropped whenever `SstvModeId` was also
set (the app's primary/most common record shape) -- and the existing round-trip test enshrined the
loss as correct (`Assert.Null(imported.Mode)`). Also flagged: `MODE=SSB` with an unrecognized
`SUBMODE` was silently marked "consumed" (hiding the stray token from notes) even though the `Usb`
fallback never actually used it.

Fixed: added a non-standard `APP_SCANLINESTUDIO_RADIOMODE` field (mirrors the existing
`APP_SCANLINESTUDIO_SSTVMODE` escape-hatch pattern) so `Mode` round-trips even when `SstvModeId` is
also set. `AdifRadioModeMapping.FromAdif` now returns `(RadioMode, bool SubmodeRecognized)` so only
an actually-used submode gets marked consumed. Added 3 new tests; flipped the round-trip test's
`Mode` assertion. 110/110 tests pass, clean build, no new bugs caught applying this round's fix.

**Round 3** (fresh agent, full re-scan) -- **GO.** Re-derived the full MODE/SUBMODE/APP_SSTVMODE/
APP_RADIOMODE consumption matrix from scratch; both round-1/round-2 fixes confirmed correct and
complete, no remaining silent-drop or double-count on any path this app's own exporter can produce.
Found 3 further instances of round 2's "value present, branch didn't take it, notes bag never sees
it" shape, all outside the MODE family and all third-party/hand-edited-input-only (not reachable
from this app's own export->import path, not a regression from this chunk's fixes): unparsable
`FREQ` silently dropped, `STATION_CALLSIGN` always discarded on import (pre-existing, not introduced
by this chunk), and imported `SstvModeId` values not matching `SstvModeRegistry`'s real hyphenated
ids for third-party (non-`APP_SCANLINESTUDIO_SSTVMODE`) SUBMODE values. Logged as a deferred backlog
item, not fixed in this chunk -- narrower and non-corrupting compared to round 2's finding, which hit
100% of this app's own real user data.

**Chunk 1 CLOSED (2026-08-22)** -- 3 rounds, real bugs found and fixed each of the first two, clean
GO on the third. Committed.

## Chunk 2 (Core.Logbook): AdifUdpStreamer.cs, AdifUdpStreamingSettings.cs

**Round 1** -- GO (unconditional). Explicitly chased chunk 1's failure class ("a value the caller
set is silently dropped or never round-trips") given it's now a proven recurring pattern in this
subsystem -- found none. The `MigrateIfNeeded` legacy-GridTracker migration trigger was verified
structurally correct (keyed on section-key presence, not `Destinations` emptiness; the section key
can never be removed once written, by exhaustive grep of every `AppSettings.Sections` write site in
`src/`, so re-migration onto an explicitly-emptied list is impossible by construction, not just
untested). `ClientId`/all destination fields confirmed to survive the migration and every save path.

Findings were all reachability-limited (auditor's own words: "don't spend a round chasing the
nits") -- a hand-edited `null` array element could NRE out of the documented "never throws" contract
(one destination in a list), IPv6-preferred `addresses[0]` has no fallback to a working A record on
a dual-stacked hostname / v4-only host, and a non-IOException settings-load failure reads
identically to "nothing configured" in the UI. Per this project's established "accept a direct yes,
apply only clearly-warranted trivial fixes, don't dispatch a confirmation round to double-confirm
it" pattern: applied the one-line null-guard (`d?.Enabled == true`) and de-staled a test comment
that claimed an escaped non-ASCII literal that wasn't actually escaped in the code (now is). The
dual-stack fallback, load-failure ambiguity, and two missing-test gaps (ClientId default-fallback,
cancellation-vs-timeout discrimination) are logged as deferred backlog, not fixed this chunk.
110/110 Core.Logbook.Tests pass, clean build.

**Chunk 2 CLOSED (2026-08-22)** -- 1 round, clean GO, two trivial fixes applied in the same round
per standing practice. Committed.

## Chunk 3 (Core.Logbook): QrzCallsignLookup.cs, QrzLogbookUploader.cs

**Round 1** -- NOT GO. Two blockers:
1. `HttpClient.Timeout` expiry throws `OperationCanceledException` in .NET 5+ (not a distinct
   `TimeoutException`), and both classes unconditionally rethrew any `OperationCanceledException`.
   A QRZ server hang threw out of `UploadAsync` AFTER `LogbookSessionService` had already persisted
   the QSO locally, surfacing as "log failed" on an already-saved record -- a user retry risks a
   duplicate QSO. Same failure class the Batch 10 chunk 10c fix addressed, on a premise ("QRZ
   uploader catches everything but cancellation") this round proved false for timeouts specifically.
2. The `QrzLogbookApi` named `HttpClient` was requested by name in `QrzLogbookUploader` but never
   registered in `Program.cs`, so it silently ran on the default 100s timeout instead of
   `QrzXmlLookup`'s established 15s pattern.

Fixed: added `catch (OperationCanceledException) when (!ct.IsCancellationRequested)` ahead of the
rethrow in both classes (mirrors `AdifUdpStreamer.cs`'s existing correct pattern), registered
`QrzLogbookApi` with a 15s timeout in `Program.cs`, corrected a stale comment there. Added timeout +
genuine-cancellation tests for both classes; strengthened the ADIF=/KEY= assertion from
key-presence-only to a full percent-encoded value round-trip (chunk 1's failure class again).
115/115 `Core.Logbook.Tests` pass, clean build.

**Round 2** (fresh agent, full re-scan) -- **GO.** Confirmed round 1's fix correct and complete
across both files: verified every `OperationCanceledException` source in each class (HttpClient
timeout, caller's token, `_sessionLock.WaitAsync`) now classifies correctly, no other swallowed
value/state found, legacy `qrzcom.cpp` name/QTH composition parity confirmed. Flagged one
same-failure-class risk: `QrzLogbookUploader`'s form-value parser did not trim whitespace, so a
`RESULT=OK` value with trailing whitespace (e.g. a stray newline after the last pair) would read as
a failed upload despite QRZ having accepted it -- inviting a retry and a duplicate QSO at QRZ. Low
probability (QRZ's documented examples put `RESULT` first) but exactly this sweep's pattern, so
fixed: `.Trim()` added to `UnescapeFormValue`. Three cosmetic/hygiene nits (no `EnsureSuccessStatusCode`,
stale cached session kept after a failed forced re-login, undisposed `HttpResponseMessage`/
`FormUrlEncodedContent`) logged as deferred backlog, not fixed -- none can produce a false success or
wrong data. 115/115 tests pass, clean build.

**Chunk 3 CLOSED (2026-08-22)** -- 2 rounds, real bug found and fixed round 1, clean GO round 2 with
one additional trivial fix applied per standing practice. Committed.

## Chunk 4 (Core.Logbook): ReceiveHistoryRecorder.cs, ReceiveHistorySettings.cs

**Round 1** -- NOT GO. Blocker: `RecordCompletedImageAsync` awaited `ResolveImagesDirectoryAsync()`
(real settings-file I/O) BEFORE calling `IReceivedImageBuffer.SaveAsync(filePath)`, which read the
LIVE `Current` buffer at that later point. A `DecodeRestarted` firing for the just-completed image in
that gap -- a real, test-proven-reachable ordering (`AnalogFmSstvDecoder`'s restart check has no
guard against having just decoded the final line) -- had already reset `Current` to a 1x1 black
placeholder, so the completed image saved as a 1x1 black PNG with a `Completed` history row pointing
at it, and the real picture was lost with no second chance (the abandoned-save path correctly
declines to double-save).

Fixed: `OnLineDecoded` hoists the already-captured `_lastImage` snapshot into a local BEFORE the
`Task.Run` closure and passes it directly into `RecordCompletedImageAsync`, which now writes it via
the existing `SaveSnapshotAsync` helper (same one the abandoned-image path already used) instead of
reading the live buffer. Also fixed the same round: a filename-collision risk on the completed path
(no uniqueness token, second-granularity), matched to the abandoned path's existing millisecond +
GUID-token scheme. Since `IReceivedImageBuffer` appeared to be fully unused by the class after this
change, its constructor parameter was removed (later found to be premature -- see round 2). Test
`DecodeRestarted_ImmediatelyAfterTheFinalLine_...` strengthened with a distinguishable-color fake
image source and a real-PNG pixel/dimension assertion, closing the gap that let the original bug
ship unnoticed (the old fake stubbed `SaveAsync` to a no-op, so pixel content was never checked).
115/115 (net +0, new test replaces coverage in the same slot) `Core.Logbook.Tests` pass, clean build.

**Round 2** (fresh agent, full re-scan) -- NOT GO. Round 1's pixel fix was correct, but removing the
`IReceivedImageBuffer` dependency also removed the only production trigger of
`IReceivedImageBuffer.Saved` for the completed-image path (previously raised inside `SaveAsync`,
which round 1 stopped calling). Two live UI features in `RxImagePaneViewModel.cs` depend on that
event exclusively and silently broke: the "Size on disk" readout, and the ability to edit Note/Flag
on a received frame (gated on a correlation key only `Saved`'s handler sets). Neither was covered by
any test -- every existing `Saved` assertion raised the event by hand via a fake, none exercised the
real recorder-to-buffer-to-pane path.

Fixed: added `IReceivedImageBuffer.NotifySaved(path, generation)` -- raises `Saved` directly for a
caller that already wrote its own snapshot without going through `SaveAsync`. `ReceiveHistoryRecorder`
re-added `IReceivedImageBuffer` as a constructor dependency, used ONLY for reading `.Generation` and
calling `.NotifySaved(...)` (never `.SaveAsync`/`.Current` again -- that part of round 1's fix stays).
`Generation` is now hoisted synchronously in `OnLineDecoded` alongside the pixel snapshot (safe
because `Generation` only changes on `ModeDetected`/`DecodeRestarted`, never `LineDecoded`, so
subscription order between the two classes can't matter) and threaded through to a
`NotifySaved` call placed BEFORE `RecordAsync`, preserving the pre-existing Saved-before-Recorded
ordering `RxImagePaneViewModel.OnHistoryRecorded` depends on. Updated all 4 `IReceivedImageBuffer`
implementers (the real class plus 3 test fakes) and fixed 4 now-stale doc comments across
`Abstractions`, `Core.Imaging`, and `UI` that claimed `SaveAsync` was the sole path. Added
`CompletedImage_NotifiesReceivedImageBufferOfTheSave`, asserting the notification actually fires
with the real saved path and the captured generation. 116/116 `Core.Logbook.Tests`, 233/233
`Application.Tests`, 673/673 `UI.Tests` pass, clean solution-wide build.

**Round 3** (fresh agent, final round, cap 3) -- **GO.** Verified the `NotifySaved` fix end-to-end
against real code, not inference: confirmed via DI that `ReceiveHistoryRecorder` and
`RxImagePaneViewModel` observe the exact same singleton `IReceivedImageBuffer` instance, confirmed
the Saved-before-Recorded dispatcher-FIFO ordering genuinely holds, and confirmed the `Generation`
hoist point is correct for every event ordering (not just the tested one) by tracing the decoder's
synchronous event dispatch. No blocker found. Flagged one risk applied as a trivial fix in the same
pass: `NotifySaved` was uncaught at its call site, so a throwing `Saved` subscriber (safe only by
accident, because the sole production implementer happens to swallow everything internally) would
skip the history-row write for an already-safely-saved file -- wrapped in its own try/catch/log,
matching this class's existing isolation pattern for every other fan-out call. Also fixed one stale
doc comment in `RxImagePaneViewModel.cs` describing a residual ordering gap round 2's fix had already
closed. Two remaining nits logged as deferred backlog, not fixed: the new ordering test doesn't
deterministically pin Saved-before-Recorded (races rather than fails on a hypothetical reorder), and
a pre-existing `MaxEntries: 0` hand-edited-settings edge case wipes untouched history (not
UI-writable today). 116/116 `Core.Logbook.Tests` pass, clean solution-wide build.

**Chunk 4 CLOSED (2026-08-22)** -- 3 rounds (cap reached). Round 1 found and fixed a real pixel-loss
bug; round 2 found and fixed a real regression round 1's own fix introduced; round 3 clean GO with
one trivial hardening fix applied. Scope necessarily expanded beyond the original two files to
`Core.Imaging` and `UI` to fix the round-2 regression -- not scope creep, a direct consequence of
this chunk's own fix. Committed.

## Chunk 5 (Core.Logbook): SqliteLogbookRepository.cs, SqliteReceiveHistoryStore.cs

**Round 1** -- GO (unconditional). Explicitly chased chunk 1-4's tracked failure class plus SQL/
concurrency-specific risks: SQL injection (none -- every dynamic `CommandText` fragment is constant,
all values go through `AddWithValue`), `AddWithValue` type-inference mismatches against declared
column types (none), the retention-trim exemption logic under a concurrent `SetNoteAsync`/
`SetFlaggedAsync`/`SetLinkedQsoIdAsync` race (sound -- single atomic `DELETE`, SQLite serializes it),
the in-place schema migration's concurrency claim (verified correct -- `BEGIN IMMEDIATE` takes the
write lock before the `PRAGMA table_info` probe runs, so a racing process can't act on stale schema),
and the `GLOB '*_partial_????????.png'` backfill pattern against `ReceiveHistoryRecorder`'s actual
current abandoned-image filename format (still matches exactly).

One risk found, same failure class as every prior chunk: `SqliteLogbookRepository.SearchAsync` used
a plain `Enum.Parse<RadioMode>` on the stored `Mode` column with no fallback, unlike
`SqliteReceiveHistoryStore.ParseDecodeState`'s own defensive pattern just a few hundred lines away in
the sibling file -- one unrecognized value (a future/older app version, hand-edited or
restored-from-backup DB) would throw out of a read-only query and take down the ENTIRE logbook list,
not just that row. Unreachable with today's writers, but cheap and exactly on-pattern to fix. Fixed:
added a `ParseMode` helper mirroring `ParseDecodeState`'s `TryParse`-falls-back-to-a-safe-default
shape (`RadioMode.Unknown`, whose own doc comment already requires this). Added
`SearchAsync_UnrecognizedModeValue_FallsBackToUnknown_InsteadOfThrowing`, which writes a bad `Mode`
value directly via raw SQL (bypassing `AddAsync`'s own serialization, which can never itself produce
an unrecognized value) to prove the fallback path. Remaining findings (all nit-tier, no test-coverage
implication, logged as deferred backlog, not fixed): `SearchAsync` has no failure logging unlike its
sibling writes, `MaxEntries: 0` from a hand-edited settings file still wipes every untouched row
(already-tracked chunk-4 backlog item, confirmed but not re-fixed here), `SqliteLogbookRepository`
has no migration path at all for a future schema change (asymmetric with its sibling, not a bug
today -- there's only ever been one schema version), unused `SqliteCommand` objects are never
explicitly disposed (safe in practice via connection close), and a stale test-fixture comment in
`SqliteReceiveHistoryStoreTests.cs` still describes the pre-chunk-4 completed-image filename shape.
117/117 `Core.Logbook.Tests` pass, clean build.

**Chunk 5 CLOSED (2026-08-22)** -- 1 round, clean GO, one on-pattern trivial fix applied with a new
regression test, per standing practice. Committed. **This closes the entire `Core.Logbook` sweep**
(chunks 1-5, `docs/functional-audit-playbook.md`'s own Tier B scope table) -- next up per that
table: `Core.Imaging`, then `UI/ViewModels`.

## Core.Imaging chunk 1: ImageFileLoader.cs, ImageSourceWriter.cs, ReceivedFrameExporter.cs, StockImageLibrary.cs

Triage (9 `Core.Imaging` files): `ArrayImageSource.cs`, `ImageLibrarySettings.cs`,
`ImageLibrarySettingsJsonContext.cs` trivial (skipped). 6 real-logic files, grouped into 3 rounds:
this chunk (4 shared-pattern ImageSharp wrapper classes), `ReceivedImageBuffer.cs` (its own chunk --
already got 3 rounds of scrutiny on its `Saved`/`Generation`/`NotifySaved` path via Core.Logbook
chunk 4, this pass will cover the rest), `TransmitImagePreparer.cs` (65KB, its own chunk).

**Round 1** -- NOT GO. Blocker: `StockImageLibrary` never called ImageSharp's `AutoOrient()` on any
of its three load methods, while its sibling `ImageFileLoader` always does. The two classes are
interchangeable branches of one caller switch in `TxControlsPaneViewModel.cs` (stock-picker vs
file-picker source selection for TX) -- a phone JPEG with EXIF orientation loaded sideways when
picked from the stock folder but correctly via Browse of the identical file. This project had
already fixed this exact bug class once, in `ImageFileLoader` only (`spec/18-path-to-1.0.md` High
item 3) -- the fix never propagated to its sibling.

Fixed: added `AutoOrient()` to all three `StockImageLibrary` load methods, ordered before any
resize/fit computation (fit must measure POST-orient dimensions, since orientation can swap width
and height). Also fixed in the same pass: `CopyToImageSource` used to trust caller-supplied
`(width, height)` instead of reading the image's own actual dimensions post-mutation (unlike its
sibling in `ImageFileLoader`) -- closed the divergence risk by matching that shape exactly. Added a
missing test file for `ImageSourceWriter` (a third independent hand-written pixel-copy loop that had
zero direct channel-order coverage) plus 3 new `StockImageLibrary` EXIF-orientation tests mirroring
the existing `ImageFileLoaderTests.cs` pattern. 100/100 `Core.Imaging.Tests` pass (was 95, +5 new),
clean solution-wide build.

**Round 2** (fresh agent, full re-scan) -- **GO.** Confirmed the `AutoOrient` fix is correct and
complete across all three methods with the right ordering, confirmed the `CopyToImageSource` change
is safe (all 3 call sites updated, no observable behavior change on any reachable path -- the one
divergence-risk case, `LoadFullAsync` with a `0` dimension, has no production caller at all), and an
independent fresh sweep of all four files found no new instance of the sweep's tracked
silently-dropped-value failure class. No blockers.

**Chunk 1 CLOSED (2026-08-22)** -- 2 rounds. Real cross-sibling bug found and fixed round 1, clean
GO round 2. Committed.

## Core.Imaging chunk 2: ReceivedImageBuffer.cs

Deliberately a LIGHT single round, not the full 2-round Tier B default: this file's
`SaveAsync`/`Saved`/`NotifySaved`/`Generation` interaction with `ReceiveHistoryRecorder` already got
3 full rounds of scrutiny in the just-closed Core.Logbook chunk 4 (2 real bugs found and fixed
there). This round covered the rest of the file -- `OnModeDetected`/`OnLineDecoded`/
`OnDecodeRestarted`/`ComputeProgress`, which implement `Current`/`Progress`/`Updated`.

**Round 1** -- GO (unconditional). Chased the sweep's tracked failure classes plus a specific
concern: whether `ComputeProgress`'s step-learning has an analogous bug to a real one
`ReceiveHistoryRecorder` had (an early version defaulted the unlearned step to the full image
height, making the first event of every image look complete) -- confirmed absent here; the
progress-fraction consequence of a similar bug would be less severe anyway (a wrong fraction, not a
false-complete), and no such bug exists. Also chased whether `AnalogFmSstvDecoder`'s replay path
(re-emitting `LineDecoded` for already-decoded rows, confirmed real via that class's own ≥16-line
replay latch) could corrupt step-learning -- confirmed it can't, since the step is only ever learned
once and the replay latch requires the step to already be learned by the time any replay can occur.

Found no blocker. Two risk-tier findings, both latent-only today and explicitly not worth another
round per the auditor's own call: `Updated` is fired with no exception isolation (unlike `Saved`'s
`RaiseSaved`), safe only because there is exactly one production subscriber that never throws; and
`OnModeDetected` doesn't reset `_current` (only `_progress`), so `Current` briefly shows the
*previous* completed image stamped with the *new* generation during the window before the next
image's first line -- a narrow overlap with already-closed Core.Logbook chunk-4 ground (affects
`SaveAsync` supersession detection specifically), not re-litigated here. Logged as deferred backlog,
not fixed: locking/threading has no dedicated test, no test pins `DecodeRestarted` not resetting
`_previousLine`/`_observedStep` (verified safe today only because of an external ordering guarantee
in `AnalogFmSstvDecoder`, not enforced by this file itself).

One nit fixed in the same pass (per the auditor's own explicit recommendation, "fold it into
whatever touches this file next" rather than dispatch a round for it alone): the snap-to-1.0 branch
in `ComputeProgress` was provably dead code with an actively wrong comment -- `Math.Clamp`'s own
upper bound already produces exactly `1.0` on the true completing event for both step-1 and
step-2 families, so the dedicated `if (Line + step >= Height) return 1.0;` branch never changed the
result. Removed the redundant branch and corrected the comment; corrected the now-inaccurate test
comment that claimed to pin a "snap" behavior that was never load-bearing. Added the two cheap
missing tests the auditor flagged: `Updated` firing on `ModeDetected` (folded into the existing
`Updated_Fires...` test, which previously covered only `LineDecoded`/`DecodeRestarted`), and a
replay-goes-backwards regression test proving a replayed `Line=0` after the step is already learned
doesn't re-derive/corrupt it. 101/101 `Core.Imaging.Tests` pass, clean solution-wide build.

**Chunk 2 CLOSED (2026-08-22)** -- 1 round, clean GO, trivial fixes applied per standing practice.
Committed.

## Core.Imaging chunk 3: TransmitImagePreparer.cs

Last `Core.Imaging` file, and the largest in the whole Tier B sweep so far (1012 lines) --
TX-side template text/image/box rendering (color, shadow, gradient, rotation, bold/italic on
template text elements). Full Tier B rigor given the size. Depth split: Area B (template text/
geometry/glyph rendering) got full re-derivation from scratch each round; Area A (Crop/Resize/
ApplyAdjustments/Rotate/pixel copy) got a solid but lighter pass.

**Round 1** -- NOT GO. One blocker, one risk, both fixed:

- **[blocker]** `DrawTemplateImage` clamped the resize TARGET SIZE independently on each axis to
  the destination canvas's own `Width`/`Height`. `DrawImage` has no scale-to-rect overload (it
  composites at native pixel size), so that clamp silently squashed the element's RENDERED SCALE to
  1:1 for any oversized element, and independently-per-axis clamping could distort its ASPECT too.
  A full-frame "set as background" element combined with a tighter-than-working-copy active crop
  rect routinely projects `Bounds` several times larger than the canvas -- a completely normal,
  reachable case, not a pathological one -- and the bug could squash it to 1x scale or move it fully
  off-canvas (nothing rendered, no diagnostic) when `Bounds.X/Y` also went negative. Existing test
  coverage couldn't catch it: the one oversized-bounds test used a uniform-color source, which looks
  pixel-identical whether correctly scaled or squashed to 1:1.

  Fixed: `DrawImage` naturally clips to the destination canvas (standard image-compositing
  behavior), so resizing to the full correct, unclamped size and letting `DrawImage` clip produces
  exactly correct scale/aspect for any oversized element -- no manual crop/intersect math needed.
  Only a single, uniform (same factor both axes) safety ceiling remains, to bound worst-case
  allocation for a genuinely oversized `Bounds` value. New test
  `ApplyTemplate_ImageWithOversizedBounds_PreservesTheElementsCorrectScaleAndAspect` (four
  distinct quadrant colors, not uniform) proves correct scale; went through two failed geometry
  attempts before landing on one that actually discriminates -- ImageSharp's default Bicubic
  resampler needs real "interior" source pixels away from a color boundary, so a 2x2 checkerboard
  source blends at every sample point regardless of margin from the destination-space boundary; an
  8px-per-quadrant block source fixed it.

- **[risk]** `ShrinkFitBoxForEffects` applied `ShrinkFitBoxForRotation` LAST, computing rotation's
  own `k` factor from an already-stroke/shadow/stack-shrunk box -- but the real pre-rotation ink is
  that shrunk box PLUS the effect allowances drawn back around it, which the old order didn't
  account for, so the real ink could exceed what `k` was derived to keep inside `Bounds` when
  rotated. Not a bleed (the unconditional `Bounds` clip in `DrawTemplateText` prevents that) but a
  hard-clip of otherwise-valid content -- hand-derived example (100x20px bounds, 90° rotation, a
  20px shadow offset) could clip roughly half the glyph run.

  Fixed: reordered so rotation runs FIRST, on the raw (unshrunk) bounds, and stroke/shadow/stack are
  subtracted from THAT afterward -- makes the fit-plus-allowances sum reconstruct exactly
  `k*(boundsWidth, boundsHeight)`, the size `k` was derived to keep safely inside bounds when
  rotated. New test `MeasureFittedFontSize_RotationCombinedWithShadowOffset_ShrinksToTheFloor_...`
  hand-derives and RUNS both orders' fit boxes for the same repro inputs -- old order gives (19,5),
  new gives (1,4) -- and asserts the new order's floor-forcing result.

103/103 `Core.Imaging.Tests` pass (was 101, +2), clean solution-wide build.

**Round 2** (fresh agent, full re-scan) -- NOT GO. Confirmed both round-1 fixes correct via
independent re-derivation (including hand-verifying the new tests' geometry end to end, not just
trusting green). Found ONE new regression round 1's own fix introduced: removing
`DrawTemplateImage`'s destination-size clamp also removed an ACCIDENTAL memory bound on
`_imageResizeCache` (a 64-entry cache capped by entry COUNT, not bytes) -- every cached entry used
to be capped at canvas resolution by the old (buggy) clamp; unclamped, a routine ~5x crop-zoom on an
ordinary SSTV-mode canvas (not pathological) could fill all 64 slots with tens-of-MB-each images,
retaining up to ~1.5-2.6GB. Also flagged, not fixed, explicitly pre-existing/off-scope:
`DrawTemplateText`'s rotation path allocates an `Image<Rgba32>` sized directly from `Bounds` with no
safety ceiling at all -- same failure class, different method.

Fixed: replaced the flat 4096px ceiling with a destination-RELATIVE one (`8x` the canvas's own
`Width`/`Height`, still capped at an absolute 4096px), and added a pixel-budget check to
`GetOrCreateResizedImage` that skips the CACHE WRITE (not the resize itself -- correctness
unaffected) for any resize over ~4M pixels/~12MB, so a genuinely large element is recomputed each
frame instead of permanently occupying one of the 64 cache slots. Worst-case retained cache memory
now bounded to roughly `64 * 12MB ~= 768MB`. 103/103 tests pass (no new tests -- internal
memory-behavior change with no externally-observable output difference), clean build.

**Round 3** (fresh agent, FINAL round, cap 3) -- **GO.** Verified the destination-relative ceiling
arithmetic against every real `SstvModeRegistry` canvas size (160-640px wide) and confirmed the
768MB worst-case bound is real and correct (down from 3.15GB after round 1 alone), confirmed the
pixel-budget cache-skip has no reference-identity hazard (the cache's only caller reads-copies-
disposes the returned image, never retains/compares it), and confirmed round 1-2's fixes hold under
a third independent re-derivation. Found two remaining risks, both explicitly judged NOT
blocker-severity and NOT worth a 4th round (the auditor's own words: "another round would just
re-derive the same numbers"): (1) the correctness threshold for the "still draws at 1:1 wrong scale"
regime regressed slightly on 320-wide modes (12.8x oversize before round 2's fix engaged, 8.0x
after) -- reachable only at a crop tighter than 12.5% of an already-2x-scaled working copy, a regime
where the image is already visually mush; (2) the cache goes permanently cold above ~7x zoom on a
320-wide canvas, meaning non-crop edits (a brightness slider, typing text) at that zoom level no
longer hit cache and redo a full resize+copy pass every frame -- a real perf regression, not a
correctness one. Both share one root cause and one real fix (clip `Bounds` against the destination
FIRST, then resize only the visible sub-region, added to the cache key) that would subsume both
risks plus the round-2 `DrawTemplateText` off-scope item's sibling concern -- logged as backlog with
that fix sketch, not attempted in this sweep (implementation work, not audit work).

**Chunk 3 CLOSED (2026-08-22)** -- 3 rounds (cap reached). Round 1 found and fixed a real blocker
(wrong TX scale/aspect for oversized elements) and a real risk (text hard-clipping under combined
rotation+shadow); round 2 found and fixed a real memory regression round 1's own fix introduced;
round 3 clean GO with two risk-tier findings deferred to backlog (redesign-sized, not a review-round
fix). Committed. **This closes the entire `Core.Imaging` sweep** (chunks 1-3) -- next up per the
Tier B scope table: `UI/ViewModels`.

## UI/ViewModels sweep -- IN PROGRESS, started 2026-08-22

Scope: 20 files, 12,260 lines -- by far the largest remaining Tier B area (vs. ~3,300 lines total
for the just-closed `Core.Logbook` + `Core.Imaging` sweeps combined). Triage: 3 trivial (skipped) --
`ViewModelBase.cs` (empty base class), `WaterfallViewMode.cs` (enum), `ITemplateElementViewModel.cs`
(pure interface, no method bodies -- read as context when auditing its 3 implementers, not audited
standalone). Grouped rounds, smallest/lowest-risk first; full chunk list and live status tracked in
`PROJECT_BRIEF.md`.

## Chunk 1: MainViewModel.cs, AboutWindowViewModel.cs, WaterfallPaneViewModel.cs

**Round 1** -- GO (unconditional). Chased this project's tracked failure class plus
UI-sweep-specific concerns: `WaterfallPaneViewModel.OnFrame`'s "at most one Dispatcher.UIThread.Post
in flight, latest-wins" coalescing logic (re-derived from scratch -- correct; the load-bearing
detail is `_postScheduled = false` being set INSIDE the lock, BEFORE `LatestFrame` is assigned, so a
throwing `PropertyChanged` subscriber can't wedge the flag), cross-thread `[ObservableProperty]`
mutation (clean everywhere -- both off-UI-thread entry points across all three files correctly
marshal via `Dispatcher.UIThread.Post` before touching an observable), and
`MainViewModel`'s two constructor fire-and-forget async calls (`LoadCallsignAsync` already has its
own try/catch; `OpenBlankEditorCommand.ExecuteAsync(null)` did not, and the risky block it reaches
in `TxControlsPaneViewModel` isn't fully covered by that command's own try/catch -- an exception
there would surface only as a nondeterministic, context-free "unobserved task exception" log at GC
time, not a crash, but with no diagnostic pointing at what actually failed).

Two risk-tier findings, both fixed as trivial same-round hardening per standing practice: (1) the
missing try/catch on `OpenBlankEditorCommand.ExecuteAsync(null)`, wrapped to match
`LoadCallsignAsync`'s own pattern with a new targeted log message; (2) a first-run-only ordering
hazard -- on a fresh install with no `settings.json` yet, `JsonSettingsStore.LoadAsync` returns an
already-completed task, so the whole auto-open-blank-editor chain used to run synchronously INLINE
in the constructor, before `RadioStatus` was assigned two lines later (harmless today since nothing
on that path reads `RadioStatus`, but a real hazard as the constructor grows) -- fixed by moving the
call to after the `RadioStatus` assignment. Added
`WaterfallPaneViewModel_MultipleFramesBeforeUiThreadRuns_CoalescesToOnlyTheLatest`, closing a real
coverage gap the auditor flagged: no existing test pushed more than one frame before
`Dispatcher.UIThread.RunJobs()`, so the coalescing claim itself (not just the happy path) was
previously untested. 674/674 `UI.Tests` pass (was 673, +1), clean solution-wide build.

Logged as deferred backlog, not fixed: `MainViewModel` has zero dedicated test coverage at all (no
test anywhere constructs one) -- the `EditorOpened`/`EditorClosed` -> `ActiveEditor` wiring and the
`CallsignDisplay` fallback are both untested; a silent no-op (no log) if `AvailableModes` is ever
empty at startup; `AboutWindowViewModel`'s null-only (not blank-aware) attribute fallbacks, not
reachable via current MSBuild defaults.

**Chunk 1 CLOSED (2026-08-22)** -- 1 round, clean GO, trivial fixes + one coverage gap closed per
standing practice. Committed.

## Chunk 2: ImageElementViewModel.cs, BoxElementViewModel.cs, TemplateVariableRowViewModel.cs

**Round 1** -- GO (unconditional), no fixes needed. Chased the three strongest bug hypotheses
against these files' own claims and found each verifiably correct: (1) property-notification
completeness between `ImageElementViewModel`/`BoxElementViewModel` -- built the full derived-pixel-
property dependency closure by hand for both classes (plus cross-checked the third sibling,
`OverlayElementViewModel`) and found no gap; the `OnImageHeightChanged`-fires-4 vs
`OnImageWidthChanged`-fires-2 asymmetry in `BoxElementViewModel` is correct by design
(`CanvasBorderThicknessPixels`/`CanvasCornerRadiusPixels` are genuinely height-only). (2)
`ImageElementViewModel.CanvasBitmap` staleness -- traced every real construction/assignment site;
`Source` is set exactly twice in the whole solution (constructor, then never again for any real
element), and the one mutable `IImageSource` implementation in the tree (`AnalogFmSstvDecoder`'s
private `MutableImageSource`) cannot reach this class. (3)
`TemplateVariableRowViewModel.ResetDisplayValueWithoutNotifying`'s try/finally under a hypothetical
reentrant call -- traced both nesting orders and the throws-inside-try case, all restore correctly.
Also verified `BlocksHitTesting` has no bypass (every real caller reads it, not `Locked`/
`IsBackground` separately) and `HasBorder`/`BorderColorForPicker`'s color-loss-on-toggle-off is
deliberate and byte-for-byte sibling-identical to `OverlayElementViewModel`'s already-established
pattern, backed by an existing test.

No blocker, no risk-tier finding. All findings nit-tier: derived-pixel-property PropertyChanged
RAISES are untested in both element VMs (values are tested, the cascade firing isn't -- a real gap
class, but on code already proven correct here, not a live bug); `CanvasBitmap`/other
`ImageSourceBitmapConverter.ToBitmap` results are never disposed anywhere in the codebase (traced
9 call sites across 5 files -- systemic, not chunk-2's to fix); a couple of other pre-existing,
sibling-identical patterns. Auditor's own explicit call: "do not spend another review round on this
chunk" -- the raise-assertion test gap is optional hardening, not a gate, logged as deferred
backlog rather than added now to keep pace through the remaining large `UI/ViewModels` scope.

**Chunk 2 CLOSED (2026-08-22)** -- 1 round, clean GO, no fixes needed.

## Chunk 3: QsoLinkWindowViewModel.cs, ReadyRackViewModel.cs (+ TemplateListRowViewModel, ReadyRackSlotViewModel)

**Round 1** -- GO (unconditional). Chased 5 specific race/state-machine hypotheses, all cleared:
`QsoLinkWindowViewModel`'s manual re-entrancy guards (no TOCTOU -- `AsyncRelayCommand.ExecuteAsync`
runs synchronously up to its first `await`, and both guarded methods set `IsBusy = true` before
that point), the `_createdQsoId` double-QSO-creation guard (airtight, three layers deep), the
two-store non-transactional write order in both link paths (correct, and a swallowed reverse-FK
failure is genuinely inert -- traced every `ReceivedImageId` consumer, nothing reads it), the
`ReadyRackViewModel.CanPin` full-rack-no-op fix's completeness (complete -- `TogglePinAsync`
re-reads the pin list fresh every call, so a stale `CanPin` can never over-pin), and the delete
arm/confirm state machine surviving an unrelated `RefreshAsync` (safe -- template ids are
GUID-suffixed, never reused).

Found no blocker. Two risk-tier UI-state-staleness findings, both fixed as trivial same-round
hardening (the auditor's own words: "worth taking opportunistically," not gating): (1) a failed
`DeleteAsync` used to clear `_pendingDeleteId` unconditionally BEFORE the delete attempt, so a
failure left the row's own `IsPendingDelete` still true (still rendering "confirm delete") while
the tracking field was already null -- the next click on that same row read as a fresh arm instead
of a confirm, taking three clicks to actually retry; now stays armed on failure so the very next
click retries directly. (2) `RefreshAsync`'s error banner (`StatusMessage`) was only ever cleared
by `DeleteAsync`'s own success path -- a transient failure (e.g. a locked settings file) left it up
forever even after later successful refreshes; now cleared on every successful refresh. Also fixed
the same class of asymmetry in `QsoLinkWindowViewModel.SearchAsync`, which never cleared
`ErrorMessage` on success unlike its sibling command methods. Added
`DeleteAsync_ConfirmClickThrows_StaysArmedSoTheNextClickRetriesInsteadOfReArming` -- the auditor
flagged the missing delete-failure test as precisely what let the first bug survive. 675/675
`UI.Tests` pass (was 674 after chunk 1, +1), clean solution-wide build.

Logged as deferred backlog, not fixed (all narrow, self-healing, or design-level, not one-line
fixes): a Gallery-originated QSO's QRZ-upload rejection is silently dropped from the UI (logged at
Warning server-side, but the dialog closes on success with no on-screen indication -- unlike the
sibling `LogbookPaneViewModel` call site for the same API, which does surface it; needs a design
decision since the dialog currently always closes on success, not a one-liner); the constructor's
own fire-and-forget initial `SearchAsync()` call can interleave with a user-triggered search (rare,
self-heals on the next search); a hand-edited settings file with a duplicate pinned template id
survives the dangling-id sweep (unreachable via the UI); two overlapping `RefreshAsync` calls could
theoretically clobber each other's pin-list write (narrow, self-correcting on the next pin).

**Chunk 3 CLOSED (2026-08-22)** -- 1 round, clean GO, trivial fixes + one regression test per
standing practice.

## Chunk 4: LogbookPaneViewModel.cs (+ RadioModeOption)

**Round 1** -- NOT GO. Multiple real bugs, all in the exact recurring class this sweep has already
found twice in this exact chunk 3: "one code path clears an error/status message on success, a
sibling path doesn't," plus a genuine missing re-entrancy guard:

1. Plain `RefreshAsync` never cleared `StatusMessage` on success -- a failed Search's error banner
   persisted forever, even after a later successful search.
2. `ExportAdifAsync` discarded its own pre-export refresh's success/failure signal and proceeded to
   export + report a stale `Entries.Count` as "Exported N" even when that refresh had failed --
   a provably wrong number shown to the user, with the real error silently discarded. (The exported
   *file* itself was always correct -- `ExportAdifFileAsync` re-queries independently.)
3. `ImportAdifAsync`'s partial-import-failure recovery refresh (the service persists record-by-
   record non-transactionally, so a mid-import exception can leave some rows already committed) also
   discarded its own success/failure -- if that recovery refresh ALSO failed, the resulting "here's
   what did import" message silently won over the second failure.
4. `LogAsync`/`UpdateAsync` set their own success status BEFORE their trailing refresh, so a refresh
   failure's `SearchFailed` message clobbered the real outcome -- the QSO (and any QRZ upload) had
   already genuinely succeeded or failed, and the user never saw which.
5. No re-entrancy guard existed at all (unlike chunk 3's `QsoLinkWindowViewModel`) around
   `LogAsync`'s real, multi-second QRZ HTTPS upload -- a user selecting a DIFFERENT row while the
   original call was still in flight had that call's own eventual `ResetForm()` silently wipe
   whatever the user had since navigated to, once the original call finally completed.

Fixed: (1) `RefreshAsync` command wrapper now clears `StatusMessage` on success without touching the
shared `RefreshInternalAsync` helper's own contract (its other 4 callers need "don't touch
StatusMessage on success," unchanged). (2) `ExportAdifAsync` now bails out if its pre-export refresh
fails. (3) `ImportAdifAsync`'s recovery-refresh-failed case now leaves that refresh's own error
standing instead of overwriting it. (4) `LogAsync`/`UpdateAsync` now set their own status AFTER the
trailing refresh, so it always wins. (5) A new `_formGeneration` counter, bumped by every action
that changes what the form represents (row selection, `New()`, `PrefillForNewEntry`) but
deliberately NOT by `ResetForm()` itself, gates every post-await write in both methods -- captured
before the first await, compared after, skipping the write if the user has since navigated away.
Four new regression tests, plus a new `ThrowOnSearch` on `FakeLogbookSessionService` (didn't exist
before -- the specific gap that made these bugs untestable) and a dedicated `TaskCompletionSource`
gate for `LogQsoAsync` (this project's own "deterministic gates, not a shared race" convention).
679/679 `UI.Tests` pass (was 675, +4), clean solution-wide build.

**Round 2** (fresh agent, full re-scan) -- **GO.** Verified the `_formGeneration` guard is complete
(every identity-dependent write in both methods gated, monotonic counter with no ABA hole,
UI-thread-confined) and traced a subtle correctness dependency worth documenting: `ResetForm()`
itself sets `SelectedEntry = null`, which re-enters `OnSelectedEntryChanged(null)` -- that handler's
own early-return-on-null (BEFORE the bump) is what stops `LogAsync`'s own internal `ResetForm()`
call from invalidating its own just-captured generation. Confirmed the status-ordering reorder (fix
4) has no consequential new window (`StatusMessage` has exactly one consumer in the whole codebase,
a single AXAML binding -- nothing else reads it). Found one more real, pre-existing bug in the same
failure class: `UpdateAsync` always passed a hardcoded `null` for `ReceivedImageId` (never carried
over by `LoadIntoForm` in the first place), silently destroying the reverse FK to a linked
RX-history frame on every edit of an already-linked QSO -- not a blocker (the entry-side link is
authoritative and survives, per `QsoLinkWindowViewModel`'s own doc comment; nothing currently reads
the reverse FK), but the auditor's own call was to fold in the 3-line fix now rather than backlog
it, since the file was already open. Fixed: `LoadIntoForm` now captures the loaded record's
`ReceivedImageId` into a new `_editingReceivedImageId` field (cleared by `ResetForm`),
`BuildRecordFromForm` takes it as a parameter instead of a hardcoded `null`. One new regression
test. 680/680 `UI.Tests` pass, clean build.

**Chunk 4 CLOSED (2026-08-22)** -- 2 rounds. Round 1 found and fixed 5 real bugs (the exact
recurring status-message-clobber class this sweep tracks, twice already in chunk 3, plus a genuine
missing re-entrancy guard); round 2 clean GO with one more real bug found and fixed in the same
pass per the auditor's own explicit recommendation.

## Chunk 5: OverlayElementViewModel.cs

**Round 1** -- NOT GO. `OverlayElementViewModel.cs` ITSELF was verified clean -- exhaustive
property-notification cascade re-derivation from every getter body (`RotationTransform`/
`ForegroundBrush`/`ShadowRenderTransform`/`CanvasStackStepX/YPixels`/`CanvasFontWeight`/
`CanvasFontStyle`/`ResolvedText`) found no gaps, and the `HasStroke`/`HasShadow`/`HasStack`/
`*ForPicker` "un-nulling" fix (a documented prior real bug: a `ColorPicker` bound directly to a
nullable color silently un-nulled it, baking a black outline/shadow into every fresh text element by
default) is symmetric across all three. But investigating `ResolvedText`'s dual-trigger design (one
of this round's assigned questions) surfaced two real bugs in the OWNING view-model,
`TxImageEditorPaneViewModel.cs` (not its own audited chunk yet -- chunk 11-13, much later):

1. **[blocker]** `KnownMacroTokenNames` (excludes real MACRO tokens from being treated as
   user-fillable template VARIABLES) was never updated when `MacroTextResolver` added `{dist}`/
   `{bearing}` (2026-08-18) -- so those tokens grew phantom fill-bar rows whose typed value the
   resolver's own switch silently discarded (it intercepts `dist`/`bearing` before ever reaching the
   variables dictionary). Reachable directly via the TEXT STYLE tab's own DIST/BEARING chips.
2. **[risk]** `{dist}`/`{bearing}` resolve FROM the `his_grid` variable, not a literal `{his_grid}`
   token -- so `OnTemplateVariableValueChanged`'s literal-token-match check never notified an
   element referencing only `{dist}`/`{bearing}` when `his_grid` was edited, leaving its canvas
   `TextBlock` stale while the mini-preview (which recomputes independently) updated correctly.

Fixed in the same pass (cheap, well-scoped, directly tied to what this round investigated -- not
deferred to chunk 11-13): added `"dist"`, `"bearing"` to `KnownMacroTokenNames`;
`OnTemplateVariableValueChanged` now also notifies any element containing `{dist}`/`{bearing}`
specifically when the changed key is `his_grid`. Extended the existing
`RescanTemplateVariables_KnownMacroTokens_NeverProduceAFillBarRow` test's own doc comment/scope
(closing the exact drift it had already warned about) and added
`OnTemplateVariableValueChanged_HisGridEdited_RefreshesDistAndBearingElementsToo` (two separate
elements, isolating the fix from the pre-existing literal-token-match path). 681/681 `UI.Tests`
pass, clean solution-wide build.

**Round 2** (fresh agent, full re-scan) -- **GO.** Re-read `MacroTextResolver.ResolveBraceTokens`'s
switch fresh and confirmed `KnownMacroTokenNames` now matches exactly (6 cases, no 7th missing);
confirmed the `his_grid`-edit fix's scope is correct (an element referencing both `{his_grid}` and
`{dist}`/`{bearing}` gets exactly one notify, no double-fire) and that `dist`/`bearing` are the ONLY
switch cases reading the variables dictionary, so no sibling instance of the same bug shape exists
elsewhere. Found one more real (pre-existing, exposed rather than caused by this round's fixes) gap:
`RescanTemplateVariables` only ever created a fill-bar row for a key LITERALLY referenced in some
element's `Text` -- a template using ONLY the DIST/BEARING chips (which insert `{dist}`/`{bearing}`,
never a literal `{his_grid}`) got no row at all, so the operator had no way to type HIS grid in and
both tokens silently resolved to empty forever. Fixed in the same pass: `RescanTemplateVariables`
now also adds `his_grid` to the referenced-keys set whenever `{dist}` or `{bearing}` is found. This
changed correct, intended behavior for the ORIGINAL `RescanTemplateVariables_KnownMacroTokens_...`
test (which had `{dist}`/`{bearing}` folded into its "produces zero rows" assertion from round 1's
own fix) -- reverted that test to its original 4-macro scope (name/grid/freq/mode, which truly
produce zero rows) and added a new, dedicated
`RescanTemplateVariables_DistOrBearingReferenced_CreatesAHisGridRow` test for the indirect-resolution
case. 682/682 `UI.Tests` pass, clean build.

Deferred backlog, not fixed (explicitly out of scope both rounds -- a project-wide convention issue
shared with an already-passed `Core.Imaging`/chunk-2 sibling, not a chunk-5 regression):
`ShadowRenderTransform`/`CanvasStackStepX/YPixels` scale off the whole working-copy `ImageHeight`
rather than the crop-aware target height `CanvasFontSize`/`CanvasStrokeThicknessPixels` correctly
use, rendering up to 2x too large under an active crop (preview-only, never feeds the real
pipeline); `RotationTransform` has no NaN/Infinity guard unlike the real pipeline (canvas-only,
reachable via a plain rotation TextBox); the hand-reflected `BitmapPatternGrid`'s claimed match to
ImageSharp.Drawing's real `Brushes.Percent20` tile could not be independently re-verified read-only
(binary-only package); missing raise-tests for several derived properties (chunk 2's own flagged
test-coverage-gap class, persisting here). Also noted for whoever eventually audits
`TxImageEditorPaneViewModel.cs`'s own chunk: a stale doc comment claiming `OnTemplateVariableValueChanged`
is "the ONLY place `_templateVariables` gains a new key" (constructor seeding and `ApplyState` both
also do).

**Chunk 5 CLOSED (2026-08-22)** -- 2 rounds. Round 1 found and fixed 2 real bugs in the owning VM
(a dead fill-bar control silently discarding operator input, and a canvas/preview divergence);
round 2 clean GO with one more real bug found and fixed in the same pass.

## Chunk 6: RadioStatusViewModel.cs (+ FrequencyPresetButtonViewModel, FrequencyPresetEditorRowViewModel)

Densest chunk so far in prior-scrutiny terms -- many doc comments cite specific already-fixed
"auditor-caught"/"code-review"/"plan-review" findings from before this sweep started. Treated as a
reason to look harder, not a reason to assume clean.

**Round 1** -- NOT GO. One real blocker plus several precedented risk-tier findings:

- **[blocker]** `StoreCurrentPresetAsync` appended a row to `EditorRows` then called the
  `SavePresetsCommand`, ignoring its outcome -- a failed save left the row in place with no shipped
  UI able to remove it (`EditorRows`/`RemovePresetRowCommand` are deliberately unmapped), so a retry
  after the failure appended a SECOND row, persisting a duplicate, permanently undeletable preset
  once the save eventually succeeded. Same failure shape as the already-documented 2026-08-11
  `_currentFrequencyHz > 0` fix ("would silently persist an unremovable preset"), just triggered by
  retry-after-failure instead of never-polled state.
- **[risk]** None of the 3 reentrancy-suppression flags (`_suppressModeCommand`/
  `_suppressReceivingCommand`/`_suppressVolumePersist`) was exception-safe -- a throw from a
  property setter's own `PropertyChanged` fan-out would leave a flag stuck `true` for the process
  lifetime, permanently and silently dead-ing the Receiving toggle / mode command / volume
  persistence with zero trace.
- **[risk]** `SetModeSafeAsync` was the one command method diverging from every sibling's
  `ErrorMessage` on-entry-null pattern -- a stale error could survive a later successful mode
  change. `SetFrequencyAsync` still had the bare-cast truncation bug `SavePresetsAsync` already
  fixed (auditor-caught 2026-08-11) but the fix was never applied to this sibling.
- **[risk]** The constructor's own "retry `StartReceivingAsync`" logic set `IsReceiving = true`
  inline, synchronously -- round 1 traced this to `JsonSettingsStore.LoadAsync`'s synchronous
  `File.Exists`/`File.OpenRead` prefix running on the constructor's OWN calling thread (the UI
  thread) before the first real `await`, a real startup-stall risk the file's own comment had
  previously argued away, directly parallel to `SstvSessionService.StartReceivingAsync`'s own
  established `Task.Run` wrap for the identical prefix.

Fixed: split `SavePresetsAsync` into a thin `[RelayCommand]` wrapper around a new
`SavePresetsInternalAsync() -> Task<bool>` (same command-wraps-internal-bool pattern as chunk 4's
`LogbookPaneViewModel.RefreshAsync`/`RefreshInternalAsync`), letting `StoreCurrentPresetAsync` roll
back its own appended row on failure; all 5 suppression-flag set/reset sites now wrapped in
`try/finally`; `SetModeSafeAsync` now nulls `ErrorMessage` on entry; `SetFrequencyAsync` now uses
`Math.Round` matching its sibling; the constructor now defers the retry's property set via
`Dispatcher.UIThread.Post` instead of setting it inline. Two tests added/extended (a rollback
assertion on the existing failure test, a new fail-then-retry-succeeds test proving exactly one
preset persists, not two). 683/683 `UI.Tests` pass, clean solution-wide build.

**Round 2** (fresh agent, full re-scan) -- **GO.** Verified the `StoreCurrentPresetAsync`/
`SavePresetsInternalAsync` split is behaviorally identical to the pre-split command for its own
direct "Save presets" button callers, confirmed the rollback can't remove the wrong row (reference
identity -- `FrequencyPresetEditorRowViewModel` is a class, not a record, unlike its
`FrequencyPresetButtonViewModel` sibling; explicitly flagged this as load-bearing on that type
staying a class), confirmed both new/updated tests are genuine discriminators (fail against the
pre-fix code, not tautologies), confirmed the constructor's deferred property set has no observable
consumer that could see a transiently-wrong value (only two `Classes.` AXAML bindings read
`IsReceiving`, both re-evaluate on the deferred set; net effect is strictly better than before --
one dispatcher-turn "not receiving" flicker at startup instead of the PRE-fix behavior's own
documented false "Receiving" for a possibly-long window), and confirmed all 5 try/finally wraps are
structurally correct (flag-set and property-set both inside `try`, reset alone in `finally`, no
reordering). Explicit "do not dispatch a round 3" call. Fixed the one cheap nit in the same pass: a
stale doc comment claiming `ErrorMessage` has "5 write sites" (now 10, growing as sibling methods
get fixed to match each other -- corrected to describe the actual, still-evolving count rather than
re-pin a specific stale number).

Deferred backlog, not fixed (explicitly judged not worth blocking on both rounds): no
`IDisposable`/teardown path for the VM at all (benign -- DI-singleton lifetime -- except a pending
400ms volume-debounce write is silently dropped with no flush if the app closes inside that
window); asymmetric cancellation-vs-failure logging inside `PersistVolumeDebouncedAsync`;
`SavePresetsInternalAsync` silently drops an unparseable-frequency row (unreachable, no shipped
editor UI); `_catLinked`'s initial value can read `true` during a `RadioController`-side
reconnect-backoff window; `FrequencyDisplay`/`ModeDisplay` deliberately not cleared on link-drop
(self-consistent today, coupled to `CanStoreCurrentPreset`'s guard in a way that's undocumented as
such); `StoreCurrentPresetAsync` pairs the rig's real frequency with the UI's `SelectedRadioMode`
even if a prior `SetModeAsync` call actually failed (single-property scope, pre-existing).

**Chunk 6 CLOSED (2026-08-22)** -- 2 rounds. Round 1 found and fixed a real blocker (duplicate,
undeletable presets on retry-after-failure) plus 4 precedented risk-tier fixes; round 2 clean GO
with one more trivial fix (a stale doc comment) applied per standing practice.

## Chunk 7: RxHistoryPaneViewModel.cs

**Round 1** -- NOT GO. 2 blockers plus 2 risk-tier findings:

- **[blocker]** `RefreshAsync`'s catch block logged and returned with no `ErrorMessage` set --
  since this method auto-fires on every incoming frame during an active session (`OnRecorded`), a
  persistent failure (a locked/corrupt SQLite file) left the Gallery frozen on stale contents
  forever with zero user-visible indication anything was wrong. Same set-on-failure/clear-on-success
  gap `LogbookPaneViewModel.RefreshAsync` already had fixed (chunk 4 of this sweep) -- the tracked
  sibling-inconsistency failure class recurring again.
- **[blocker]** `ExportFrameAsync`'s `_filePicker.PickSaveImageFileAsync` call sat OUTSIDE the
  method's try block -- an exception from the platform storage provider escaped uncaught, with no
  log line and no `ErrorMessage`; the Export button just appeared to silently do nothing. Sibling
  `RxImagePaneViewModel.SaveFrameAsync` already puts its own identical picker call inside its try.
- **[risk]** `_suppressSelectedEntryEdits` was set/reset bare (no `try/finally`) at 2 sites -- a
  throwing `PropertyChanged` subscriber could leave it stuck `true` for the VM's lifetime,
  permanently and silently breaking every future note/flag edit. Same failure shape
  `_isRepopulating`'s own guarded sites already avoid.
- **[risk]** `LoadFramesTodayCountAsync` had no ordering guard against overlapping calls, unlike its
  co-dispatched sibling `RefreshAsync` (both fire un-awaited from the same `OnRecorded` callback) --
  a burst of `Recorded` events (e.g. a bulk-decoded WAV import) could race N overlapping queries,
  with the LAST completer winning even if it started FIRST and is now describing a stale count.

Also fixed in the same pass: a misleading `[LoggerMessage]` text ("history list stays empty" --
false, the method returns before `Entries.Clear()`; corrected to "stays at its last-loaded
contents"). New locale key `Panes.RxHistory.Error.RefreshFailed` added to `assets/locale/en.json`
(only locale file in the tree, so no parity gap). `LoadFramesTodayCountAsync` gained its own
`_framesTodayGeneration` field, same bump-before-await/compare-after pattern as `_refreshGeneration`.
Three regression tests added (`RxHistoryPaneViewModel_RefreshAsync_QueryThrows_...`,
`..._SucceedsAfterAPriorFailure_ClearsErrorMessage`, `..._ExportFrameAsync_PickerThrows_...`), backed
by two new `Fakes.cs` throw-hooks (`FakeReceiveHistoryStore.ThrowOnQuery`,
`FakeFilePickerService.ThrowOnPickSaveImageFile`). No dedicated regression test added for the two
risk-tier fixes (try/finally, generation guard) -- both are small, established-pattern, correct-by-
inspection defensive fixes; round 2 explicitly judged this gap acceptable. 686/686 `UI.Tests` pass
(was 683, +3), clean solution-wide build.

**Round 2** (fresh agent, confirmation-only) -- **GO.** Verified all 4 fixes correct and complete at
their exact line numbers, confirmed the `ExportFrameAsync` cancellation path (`picked is not { }
result`) still exits cleanly as a plain `return` inside the try with no `finally` to interact with,
confirmed both `_suppressSelectedEntryEdits` sites reset unconditionally to `false` in `finally`,
confirmed the new tests are genuine discriminators (both `Fakes.cs` throw-hooks throw synchronously
from non-async methods -- exactly the case the old out-of-try picker call let escape). One new
narrow finding: the success-path `ErrorMessage = null` sat BEFORE the `_refreshGeneration`
stale-discard check, so an already-superseded refresh could null out an error a NEWER, still-in-
flight refresh had just set (reachable only under overlapping refreshes from an `OnRecorded` burst;
self-healing on the next failing refresh, no data corruption) -- judged risk-tier, not blocking, but
cheap enough to fix in the same pass: moved the `ErrorMessage = null` line to after the
stale-discard check. Rebuilt and reran the full `UI.Tests` suite clean (686/686) after the move.
Explicit "close the chunk, don't spend another round on it" call.

Deferred backlog, not fixed: no regression test for the `_suppressSelectedEntryEdits` try/finally
(needs a deliberately-throwing `PropertyChanged` subscriber to exercise) or the
`_framesTodayGeneration` guard (needs a TCS-gated fake query) -- both explicitly judged not worth
the added test-infrastructure for a defensive, correct-by-inspection fix.

**Chunk 7 CLOSED (2026-08-22)** -- 2 rounds. Round 1 found and fixed 2 real blockers (silent
refresh-failure, escaped picker exception) plus 2 precedented risk-tier fixes; round 2 clean GO with
one more trivial ordering fix (error-clear vs. stale-discard sequencing) applied per standing
practice.

## Chunk 8: OptionsWindowViewModel.cs (+ AdifUdpDestinationRowViewModel.cs)

**Round 1** -- NOT GO. 1 real blocker plus 4 risk-tier findings:

- **[blocker]** `LoadSafeAsync`'s single catch-all try could fail partway (a locked/corrupt
  `settings.json`, or the audio device enumerator's `RefreshAsync` throwing when the audio backend
  is unavailable) leaving every field at its hardcoded constructor default with no gate anywhere --
  `SaveAsync` would then persist those defaults over the user's real settings with zero warning. A
  user who opens Options while their audio backend happens to be down, changes an unrelated field
  (callsign, JPEG quality), and hits Save silently loses their capture/playback device IDs,
  RememberWindowPosition, and JpegQuality. Same "state a caller depends on gets silently dropped"
  class this whole sweep keeps finding, just at dialog-load scope instead of a single field.
- **[risk]** `ResetGeneralToDefault`'s culture fallback was missing -- `OptionsSettingsService
  .Defaults.CultureCode` is always `null` (the record default), so a bare `FirstOrDefault` by that
  code always missed and blanked the Language ComboBox on every Reset. `ApplyFromSnapshot` already
  has the `?? _localization.CurrentCulture` fallback for the identical reason; Reset didn't.
- **[risk]** `TestRigctldConnectionAsync`'s two `Dispatcher.UIThread.Post` lambdas ran their
  `GetString` calls unguarded -- the lambda executes AFTER the method's own outer try/catch has
  already exited. A locale file with a mismatched format placeholder throwing `FormatException`
  here escaped uncaught onto the dispatcher loop AND left `IsTestingConnection` stuck `true` forever
  (Test Connection button permanently disabled for the dialog's life). Sibling
  `TestQrzLookupAsync` wraps its equivalent call inside its own try/catch/finally; this one didn't.
- **[risk]** `ApplyFromSnapshot`'s loop over `snapshot.AdifUdpDestinations` dereferenced
  `destination.Enabled` unconditionally -- a JSON-deserialized `"Destinations": [null]` produces a
  null list element at runtime (despite the compile-time non-nullable element type) and NREs,
  caught by `LoadSafeAsync`'s outer try but taking the WHOLE load down with it, not just the
  destinations. `AdifUdpStreamer.SendAsync` already tolerates the identical data shape via its own
  `d?.Enabled == true` guard; this read site didn't have the equivalent.

Fixed: a new `_loadSucceeded` flag, set only as the LAST statement in `LoadSafeAsync`'s try block
(after every step that could silently corrupt persisted data has completed without throwing) now
gates `SaveCommand` via `[RelayCommand(CanExecute = nameof(CanSave))]`, with
`SaveCommand.NotifyCanExecuteChanged()` fired in `LoadSafeAsync`'s `finally` on both the success and
failure paths -- the general-case fix (Save is simply unavailable until a load has actually
completed), not a per-field patch, since the original failure wasn't specific to any one field.
`ResetGeneralToDefault` now has the same culture fallback as `ApplyFromSnapshot`.
`TestRigctldConnectionAsync`'s both posted lambdas now wrap their `GetString` call in
try/catch/finally -- catch logs via a new `Log.TestRigctldConnectionStatusDisplayFailed`
`[LoggerMessage]` and falls back to `TestConnectionStatusMessage = null` (deliberately not another
`GetString` call, since a broken locale key can't be trusted to safely produce anything); finally
always resets `IsTestingConnection = false`. `ApplyFromSnapshot`'s destination loop now skips a null
element via `continue` rather than dereferencing it. Also fixed in the same pass (precedented nit):
`ResetRadioToDefault` now clears `TestConnectionStatusMessage = null`, matching sibling
`ResetQrzToDefault`'s existing `TestQrzLookupStatus = null` clear (a stale "Connected to IC-7300"
success line used to stay visible after Reset Radio blanked the host field).

Explicitly NOT fixed, judged a reasonable scope boundary (round 2 agreed): if the audio enumerator
succeeds but the PERSISTED device ID just isn't in the currently-enumerated list (e.g. a USB sound
card is temporarily unplugged, no exception thrown), `SelectedCaptureDevice`/`SelectedPlaybackDevice`
still resolve to null and Save will persist null over the old ID -- indistinguishable at this layer
from a deliberate `ResetAudioToDefault` call (which also produces null), and the only non-fragile
fix (retaining the unmatched ID as a "device not currently present" ghost entry) is a design change,
not a bug fix.

Seven regression tests added (`Constructor_LoadSucceeds_SaveCommandIsEnabled`,
`Constructor_SettingsStoreLoadThrows_SaveCommandStaysDisabled`,
`Constructor_AudioEnumeratorRefreshThrows_SaveCommandStaysDisabled`,
`ResetGeneralToDefaultCommand_LeavesSelectedCultureSetInsteadOfBlankingIt`,
`TestRigctldConnectionCommand_GetStringThrowsFormattingTheResult_
StillResetsIsTestingConnectionWithoutCrashingTheDispatcherLoop` (this one genuinely reproduced the
original unhandled-`FormatException`-from-`Dispatcher.RunJobs` crash before the fix landed -- caught
via test, not just inspection), `Constructor_NullElementInPersistedAdifUdpDestinations_
SkipsItInsteadOfThrowing`, `ResetRadioToDefaultCommand_ClearsAStaleTestConnectionStatusMessage`),
backed by 3 new `Fakes.cs` hooks (`FakeAudioDeviceEnumerator.RefreshAsyncException`,
`FakeLocalizationService.ThrowOnGetString`/`ThrowOnGetStringForKey`). 693/693 `UI.Tests` pass (was
686, +7), clean solution-wide build.

**Round 2** (fresh agent, confirmation-only) -- **GO.** Verified `_loadSucceeded` is set at the
correct point (after every corruptible step), confirmed `SaveCommand` has exactly one invocation
site app-wide (a `Button` respecting `CanExecute` via `IsEnabled`) so nothing bypasses the gate,
confirmed the VM is `AddTransient` and resolved fresh per dialog open so a transient load failure
can't permanently lock Save for the process lifetime, confirmed both `TestRigctldConnectionAsync`
lambdas are fully guarded with no remaining escape path, confirmed the null-destination skip
preserves ordering/content of real entries, and explicitly agreed the unplugged-device limitation is
a reasonable scope boundary rather than re-flagging it. Two residual nits, neither actioned: the
"Testing..." status `GetString` call (zero args) is still technically outside its try but provably
unreachable for `FormatException` since `JsonLocalizationService.Format` short-circuits to the
template on an empty args array; `TestQrzLookupAsync`'s own catch-block `GetString(...,
ex.Message)` call has the identical one-arg-can-throw shape as the fixed rigctld one but is lower
severity (escapes into the command's own `Task`, not the dispatcher loop) and was out of round-1
scope. Explicit "close chunk 8" call.

**Chunk 8 CLOSED (2026-08-22)** -- 2 rounds. Round 1 found and fixed 1 real blocker (Save could
silently persist post-load-failure defaults over real settings, for any field) plus 4 precedented
risk-tier/nit fixes; round 2 clean GO, no further action.

## Chunk 9: RxImagePaneViewModel.cs

Already touched once before, during Core.Logbook chunk 4 (the `Saved`/`NotifySaved` ordering fix in
`ReceiveHistoryRecorder.cs`) -- round 1 explicitly re-verified that fix is still correct in context
before auditing anything new, per standing practice.

**Round 1** -- NOT GO. 2 risk-tier findings plus 2 smaller precedented gaps, all four in the same
tracked failure class:

- **[risk]** `_suppressFrameMetadataEdits` was set/reset bare (no `try/finally`) at 2 sites
  (`OnModeDetected`, `OnHistoryRecorded`) -- same flag shape `RxHistoryPaneViewModel
  ._suppressSelectedEntryEdits` already had fixed earlier in this sweep (chunk 7), but strictly
  worse here: a throw from the `Note`/`IsFlagged` property setters' `PropertyChanged` fan-out would
  skip every statement AFTER it in the same lambda too, including the `OverrideCallsign`/`Lookup*`
  resets a few lines below `OnModeDetected`'s own guarded block -- letting a previous station's
  callsign leak into the next reception and, via `LogQsoCommand`, into a real logbook row.
- **[risk]** `ApplyStationIdDecodedAsync`'s `OverrideCallsign` write ran after
  `await _sstvSession.GetOperatorCallsignAsync()` -- a genuinely uncached settings-file disk read
  (`JsonSettingsStore.LoadAsync` has no cache) -- with no staleness guard. During a bulk-WAV-decode's
  back-to-back transmissions (the SAME interleaving `OnSaved`'s own doc comment already calls "the
  likely interleaving, not an edge case"), a new reception's `ModeDetected`/`Generation` bump can
  land in that exact window, after which the write would silently re-apply the OLD reception's
  decoded callsign onto the NEW one now on screen. `OnSaved` already guards the identical class via
  `IReceivedImageBuffer.Generation`; this write site didn't have the equivalent.
- **[one-liner]** `DecodedNrRst` is the same per-RECEPTION "who is this station" category as
  `OverrideCallsign`/`Lookup*` (also decoded from the previous station's FSK sub-packet) but was the
  one left out of `OnModeDetected`'s reset block -- latent only because no view currently binds it
  yet.
- **[nit, fixed anyway]** `QrzLookupErrorMessage`/`FrameMetadataErrorMessage`/`SaveFrameErrorMessage`
  are per-RECEPTION status text, same category as `Note`/`IsFlagged` (already reset), but weren't
  cleared -- a QRZ lookup failure or a stale "entry no longer exists" from the PREVIOUS frame kept
  showing on the RxFrameMeta card after a new reception blanked the fields the error text was
  actually about. Same shape `RxHistoryPaneViewModel_ExportFrameAsync_
  SwitchingSelectionAfterward_ClearsStaleExportStatusMessage` already fixed for the sibling pane
  (chunk 7).

Fixed: both `_suppressFrameMetadataEdits` sites wrapped in try/finally, matching the sibling's own
fix shape. `ApplyStationIdDecodedAsync` now captures `_receivedImage.Generation` immediately before
the await and bails if it changed by the time the await resumes, before both the self-filter check
and the `OverrideCallsign` write -- the `CompactNr`/`NrText` branches call `ApplyDecodedNrRst`
synchronously with no intervening await, so correctly left unguarded. `OnModeDetected`'s reset block
now also nulls `DecodedNrRst` and all three error-message properties. Three regression tests added
(`RxImagePaneViewModel_StationIdDecodedEvent_NewReceptionStartsWhileAwaitingOwnCallsign_
DropsTheStaleWrite`, `..._ModeDetectedEvent_ClearsStaleDecodedNrRst`, `..._ModeDetectedEvent_
ClearsStalePerReceptionErrorMessages`), backed by a new `FakeSstvSessionService
.OperatorCallsignGate` `TaskCompletionSource` hook that holds the callsign-lookup await open so a
test can bump `FakeReceivedImageBuffer.Generation` mid-flight -- genuinely reproduces the race, not
just inspection. No dedicated test for the try/finally fix (same defensive/correct-by-inspection
judgment call as the sibling's own precedent). 696/696 `UI.Tests` pass (was 693, +3), clean
solution-wide build.

**Round 2** (fresh agent, confirmation-only) -- **GO.** Verified both try/finally sites reset the
flag unconditionally and place every previously-skippable statement after the guarded block,
confirmed the generation capture is genuinely before the await and the check genuinely after (and
before the write), and traced the guard's soundness against the REAL `ReceivedImageBuffer`'s own
generation-bump sites (not just the fake) -- `ModeDetected`/`DecodeRestarted` bump `_generation`
synchronously on the decode thread, always before this VM's own posted reset can run, so there's no
window where the VM's reset has already fired but the guard still sees an unchanged generation.
Confirmed `DecodedNrRst` and the three error messages aren't cleared from any other event, and that
a mid-reception QRZ failure correctly persists until the NEXT `ModeDetected` (the stated intent, not
a bug). One nit noted, not actioned: `finally` resets the suppression flag to `false` rather than
restoring a prior value, which is safe only because neither site can currently be reentered
synchronously (both run only as `Dispatcher.UIThread.Post` lambdas) -- would need save/restore if a
third, potentially-nesting site is ever added. Explicit "close the chunk" call.

**Chunk 9 CLOSED (2026-08-22)** -- 2 rounds. Round 1 found and fixed 2 real risk-tier bugs
(non-exception-safe suppression flag with a worse blast radius than its sibling; missing generation
guard on a callsign write after a genuinely-slow uncached disk-read await) plus 2 smaller precedented
gaps; round 2 clean GO, no further action.

## Chunk 10: TxControlsPaneViewModel.cs

**Round 1** -- NOT GO. 1 real blocker plus 6 risk-tier findings:

- **[blocker]** `OnSelectedModeChanged`'s blank-editor branch `return`ed right after reopening the
  blank editor at the new mode's size -- which ALSO skipped the reflow logic below it (re-crop/
  resize/adjust/template `_editState.Original` at the new mode's dimensions into
  `_loadedImage`/`PreviewImage`). A blank/untouched editor being open says nothing about whether
  `_editState` is null -- it survives from an earlier Apply (Apply an image, then click "Open blank
  editor" directly, gated only on `!IsEditorOpen`, not on `_editState` being null). With the early
  return, `_loadedImage` kept the OLD mode's pixel dimensions while `SelectedMode` moved to the new
  mode -- a mismatch `AnalogFmSstvEncoder` throws `ArgumentException` on when Transmit is clicked,
  surfacing only as a context-free "Transmit failed". Same defect class spec/18-path-to-1.0.md High
  item 2 was closed on, reopened by the blank-editor-relaxation feature.
- **[risk]** `_suppressSafetyPersist` set/reset bare (no `try/finally`) inside a `Dispatcher
  .UIThread.Post` lambda -- same shape chunk 9's `_suppressFrameMetadataEdits` finding, immediately
  preceding this chunk.
- **[risk]** `AutoFollowRxMode` read on the audio drain thread BEFORE the `Dispatcher.UIThread.Post`
  marshal, while its sibling `IsEditorOpen` check was explicitly moved INSIDE the `Post` for the
  exact same cross-thread-visibility reason (this method's own doc comment cites the precedent). A
  stale read could perform an unwanted `SelectedMode` change right after the operator un-ticked
  auto-follow.
- **[risk, explicitly NOT fixed]** No unsubscribe path for `ModeDetected`/`TransmitProgressChanged`,
  and the `radioSession.StateChanges.Subscribe(...)` `IDisposable` is discarded; `Dispose()` only
  cancels `_transmitCts`. Round 1 flagged this as a leak risk, but `TxControlsPaneViewModel` is
  `AddSingleton` in `Program.cs` -- the IDENTICAL shape chunk 9's own round 1 examined for
  `RxImagePaneViewModel` (also `AddSingleton`, also multiple undisposed subscriptions) and judged
  "correct as written; adding IDisposable would be noise" (not even a finding there). Applying this
  chunk's own finding would have created an inconsistency with that direct, one-chunk-old
  precedent for the identical pattern -- skipped, and round 2 explicitly confirmed the skip is
  sound.
- **[risk]** `SwrCutoffThreshold`'s TextBox is TwoWay/PropertyChanged-triggered, so typing "12" fired
  TWO overlapping, un-awaited `PersistSafetySettingsAsync` calls, each capturing its own value
  before its own await -- if the stale "1" call's `SaveAsync` completed AFTER the fresh "12" call's,
  the persisted SWR safety cutoff would silently end up at 1.0 (an always-trips value) while the UI
  showed 12.
- **[risk]** `SelectImageAsync`'s picker-failure catch was log-only, unlike every sibling failure
  path (`OpenEditorForSourceAsync`, `OpenEditorWithLoadedSourceAsync`, `EditCurrentImageAsync`),
  which all set `ErrorMessage` -- a picker failure was indistinguishable from the user pressing
  Cancel.
- **[risk]** Three editor-construction-failure catch blocks left `_currentEditor`/
  `_currentEditorIsBlank` stale and never fired `EditorClosed`, unlike every OTHER close path in
  this class (`CloseBlankEditorForReplacement`, `OnEditorCancelled`, `OnEditorApplied` all reset the
  same trio). `MainViewModel`'s own `EditorClosed` subscriber is what nulls `ActiveEditor` (the
  docked editor view's real content) -- skipping it left the View out of sync with `IsEditorOpen`
  now being false.

Fixed: removed the early `return` so the blank-editor branch falls through into the shared clear/
reflow logic (the blank-editor reopening touches different fields than the reflow, so nothing
double-runs or collides). `_suppressSafetyPersist` wrapped in try/finally. `AutoFollowRxMode`'s
check moved inside the `Post` lambda alongside `IsEditorOpen`'s existing one.
`PersistSafetySettingsAsync` split into `SchedulePersistSafetySettings()` +
`PersistSafetySettingsDebouncedAsync(spec, ct)`, matching `RadioStatusViewModel
.PersistVolumeDebouncedAsync`'s established debounce-and-cancel-supersedes shape exactly (400ms
delay, `TaskCanceledException` as normal control flow) -- both `OnSwrCutoffEnabledChanged` and
`OnSwrCutoffThresholdChanged` now schedule through it. `SelectImageAsync`'s catch now sets
`ErrorMessage` with the same key its siblings use. All three editor-construction-failure catches
now reset `_currentEditor`/`_currentEditorIsBlank` and fire `EditorClosed` (confirmed harmless even
when fired redundantly -- the event's one subscriber, `MainViewModel`'s `ActiveEditor = null`, is
idempotent).

Five regression tests added/updated (`TxControlsPaneViewModel_
ModeChangeWhileABlankEditorIsOpenOverAnAlreadyAppliedEdit_StillReflowsLoadedImage` for the blocker,
`..._SelectImageCommand_PickerThrows_SetsErrorMessage`, `..._SwrCutoffThresholdChangedRapidly_
OnlyPersistsTheLatestValue`, extended `..._SettingsLoadThrowsWhileOpeningTheEditor_
ResetsIsEditorOpen_InsteadOfStayingStuckOpen` with an `EditorClosed` counter, and
`TxControlsTelemetryAndCutoffTests.cs`'s `TogglingSwrCutoffEnabled_Persists` updated to
`..._PersistsAfterDebounceDelay` to match the new debounce behavior), backed by a new
`FakeFilePickerService.ThrowOnPickImageFile` hook. No dedicated test for the try/finally fix or the
`AutoFollowRxMode` cross-thread-read fix (same defensive/correct-by-inspection judgment as chunk
9's precedent -- a cross-thread race can't be proven from a single-threaded test without new
infrastructure). 699/699 `UI.Tests` pass (was 696, +3 net-new, one pre-existing test adapted),
clean solution-wide build.

**Round 2** (fresh agent, confirmation-only) -- **GO.** Verified the blank-editor fall-through can't
double-run (the reopened blank editor touches disjoint fields from the reflow) and can't dangle
(traced that `_loadedImage != null` always implies `_editState != null`, so the new null-clear is a
provable no-op on the `_editState is null` path). Confirmed both try/finally and moved-check fixes
are correct. Explicitly confirmed the `AddSingleton` registration and agreed the R3 skip is sound,
citing the same chunk-9 precedent with no new reasoning to distinguish this file. Traced the
debounce fix's value-capture timing against CommunityToolkit's generated setter (field write happens
before the changed-partial-method fires, so the captured `RadioSafetySpec` is always the fresh
post-edit value, never stale) and confirmed the updated test is a legitimate behavior adaptation,
not a weakened assertion. Confirmed all three `EditorClosed`-on-failure sites and that firing it
redundantly is harmless. One trivial nit actioned in the same pass: a stale doc comment
("Error, not Warning...") had been left on `SchedulePersistSafetySettings` after the log call it
described moved to the new `PersistSafetySettingsDebouncedAsync` -- moved the comment to follow the
code it documents. Two more nits noted, not actioned (both precedent-matching or cosmetic-only): the
superseded `_safetyPersistCts` is cancelled but never disposed (same as
`RadioStatusViewModel._volumePersistCts`'s own precedent); a debounce supersede landing AFTER the
400ms delay but mid-`SaveSafetySettingsAsync` logs at Error via the generic catch (cosmetic --
persisted end state is still correct, the superseding call does its own full write). Explicit "close
the chunk" call.

**Chunk 10 CLOSED (2026-08-22)** -- 2 rounds. Round 1 found and fixed 1 real blocker (mode change
while a blank editor sat over a previously-applied edit skipped the mode/image-size reflow, feeding
a mismatched image straight to the encoder) plus 5 precedented risk-tier fixes, explicitly declined
1 risk-tier finding as inconsistent with an established one-chunk-old precedent; round 2 clean GO
with one more trivial fix (a misplaced doc comment) applied per standing practice.

## Chunk 11: TxImageEditorPaneViewModel.cs, Area A (sources/elements/templates/presets)

`TxImageEditorPaneViewModel.cs` (3911 lines, the largest file in the whole Tier B sweep) split into
3 area-based sub-chunks, matching `TransmitImagePreparer.cs`'s own earlier Area A/B split precedent.
Area A: constructor/properties/zoom, adding content to the canvas (file/clipboard/RX-history/stock
sources), template save/load, text/field insertion presets -- roughly lines 1-2227. Areas B/C (element
manipulation/reordering/Apply-Cancel; rotate/crop math/undo-redo/state) are chunks 12/13, not yet
started.

**Round 1** -- NOT GO. 1 real blocker plus 3 risk-tier findings, all in the tracked failure class:

- **[blocker]** `SaveTemplateAsync`'s overwrite path (saving under an already-used name) deleted
  the PRE-EXISTING template's whole folder up front, before writing anything new -- `SaveAsync`
  itself does a thumbnail render + 2 file writes, any of which can throw (disk full, a network-
  backed MyPictures, an AV lock, a permissions change), and the catch block's own cleanup-delete
  then fired AGAIN on the SAME id unconditionally, permanently destroying the operator's real,
  pre-existing template with no recovery path -- `StatusMessage` just read "Save template failed."
  Real, unrecoverable user-data loss on a branch the code already anticipated enough to have written
  a cleanup handler for (the handler was just scoped to the wrong case).
- **[risk]** `existingId` (used to decide overwrite-vs-create) was resolved from
  `ReadyRack.AllTemplates`, a SEPARATE VM's own in-memory projection populated by a fire-and-forget
  `RefreshAsync()` call at editor-open time -- saving before that refresh completed (or after it
  silently swallowed a transient failure) read a stale, possibly-EMPTY list, so an overwrite of a
  real existing template could silently degrade into creating a duplicate instead -- exactly the bug
  the overwrite-in-place feature exists to fix.
- **[risk]** `ReadyRackViewModel`'s Load/RecallSlot commands are plain synchronous `[RelayCommand]`s
  that just raise `TemplateSelected` into `OnReadyRackTemplateSelected` (an `async void`) --
  CommunityToolkit's default no-concurrent-execution gate never applies here, so nothing serializes
  two overlapping template loads. Clicking template A (slow asset load) then quickly clicking
  template B (fast) let B populate the canvas first, then A's slower continuation overwrite it right
  back with A -- the operator ends up looking at the template they did NOT just ask for.
- **[risk]** `RefreshRxHistoryPickerAsync` was the one sibling among the image-source add/refresh
  paths with no `StatusMessage` on failure -- log-only, so a failure (e.g. an unreadable RX history
  SQLite file) left the "From RX history" flyout silently empty with no explanation.

Fixed: removed the up-front delete entirely (`SaveAsync` already overwrites `template.json`/
`thumbnail.png` in place -- confirmed against the real `TemplateStore.cs`, not just its doc
comment); the catch's cleanup-delete now only fires `if (existingId is null && templateId is {
} freshlyMintedId)` -- never deletes a folder that pre-existed this save attempt. Accepted
trade-off, round 2 explicitly agreed it's reasonable: repeated overwrite-saves of the SAME template
will now slowly accumulate orphaned GUID-named asset PNGs in that template's own `assets/` folder
(image-element asset filenames are always freshly minted, by design, never reused in place) --
disk-space-only, not data loss, and a proper fix would need `ITemplateStore` to expose per-asset
deletion, which doesn't exist today. Also fixed in the same pass: `IsSavingTemplate` moved fully
inside try/finally (was outside the try, risking getting stuck true on a throw during id
resolution). `existingId` now resolved from `_templateStore.ListAsync()` directly -- the actual
persistence-layer source of truth, independent of `ReadyRack`'s own refresh timing. A new
`_templateLoadGeneration` counter, bumped per-selection (after the arm/confirm gate, so a mere
arming click doesn't invalidate an in-flight load) and checked immediately before the synchronous
canvas-mutating call, discards a stale load rather than clobbering a newer one.
`RefreshRxHistoryPickerAsync` now clears `StatusMessage` on entry and sets it on failure, matching
its 3 siblings -- `RxHistoryPickerEntries` deliberately left untouched on failure (round 2 confirmed
this matches `ReadyRackViewModel.RefreshAsync`'s own established "best-effort, never destroy good
state on a transient failure" doctrine, not a gap).

Four regression tests added (`SaveTemplateAsync_OverwriteFailsPartway_
LeavesThePreExistingTemplateIntact` for the blocker -- genuinely mutation-detecting, the fake's
`SaveAsync` throws before mutating its own store so the old up-front-delete code would have emptied
it; `SaveTemplateAsync_ResolvesExistingIdFromTheTemplateStore_NotTheReadyRacksOwnPossiblyStaleList`;
`LoadTemplate_OlderSlowerLoadCompletesAfterANewerFasterOne_DoesNotClobberTheNewerResult`, using a
new `FakeTemplateStore.LoadGates` per-templateId `TaskCompletionSource` dictionary to hold one load
open while another completes first; `RefreshRxHistoryPickerAsync_QueryThrows_SetsStatusMessage`),
backed by new `FakeTemplateStore.SaveExceptionToThrow`/`LoadGates`/`Templates` (read-only peek)
members. 316/316 `UI.Tests` pass for this file (was 312, +4), 703/703 full suite (was 699, +4),
clean solution-wide build.

**Round 2** (fresh agent, confirmation-only) -- **GO.** Verified the up-front delete is genuinely
gone (grepped every remaining `_templateStore.*` call site in the file), traced the real
`TemplateStore.SaveAsync`/`ImageSourceWriter` implementations to confirm an in-place overwrite
genuinely needs no delete, confirmed the catch guard can't fire on the overwrite path under any
throw location (including a throw during `existingId`/`templateId` resolution itself, since both
are declared nullable outside the try), confirmed `IsSavingTemplate` can no longer get stuck under
any throw location, confirmed the generation guard's bump/check placement is correct relative to
the pre-existing arm/confirm gate (bump happens AFTER arming, so an arm-only click doesn't
invalidate an in-flight load) and that the check sits immediately before the synchronous
canvas-mutating call with no await in between (check-then-mutate is atomic w.r.t. the UI thread),
and explicitly agreed both the orphaned-asset trade-off and the "leave RxHistoryPickerEntries
untouched on failure" precedent match are the right calls, not gaps. Two nits noted, not actioned:
`RefreshRxHistoryPickerAsync`'s new `StatusMessage = null` on entry can erase an unrelated armed
"press again to confirm" recall message if the flyout opens mid-arm (pre-existing class shared by
all 3 sibling methods, not new blast radius); the generation-guard test asserts an unchanged value
on both sides of the gate completion, which would also pass vacuously if the continuation simply
never resumed (test-hardening suggestion, not a production-code gap). Explicit "close chunk 11 Area
A" call.

**Chunk 11 (Area A) CLOSED (2026-08-22)** -- 2 rounds. Round 1 found and fixed 1 real blocker
(overwrite-save-then-failure permanently destroyed a real, pre-existing template with no recovery)
plus 3 precedented risk-tier fixes (stale-list overwrite-detection race, unserialized overlapping
template loads, missing error surface); round 2 clean GO, no further action. Areas B/C remain --
chunks 12/13.

## Chunk 12: TxImageEditorPaneViewModel.cs, Area B (element manipulation/reordering/Apply-Cancel)

Second of 3 area-based sub-chunks for this 3911-line file. Area B: tab selection, element
alignment, plate-behind-text, duplicate/copy/cut/paste, template variables (fill-bar values, not
save/load -- that was Area A), element list reordering/removal, Apply/Cancel -- roughly lines
2228-2887. Area C (rotate/crop math/undo-redo/state snapshot) is chunk 13, not yet started.

**Round 1** -- NOT GO. 5 risk-tier findings, all in the tracked failure class ("sibling has the
fix/guard, near-identical sibling doesn't"):

- **[risk]** `AlignSelectedElementToCrop` pushed TWO undo steps per click -- its own explicit
  `PushUndoSnapshot()` plus a second, near-identical one from the unguarded X/Y assignment's own
  `OnXChanging`/`OnYChanging` → `PushUndoSnapshotForGeometryChange` hook. The first Undo appeared to
  do nothing (it popped the redundant duplicate); only the second Undo actually moved the element
  back. The method's OWN doc comment justified the single-push claim on a false premise ("assigns
  exactly one property per call, never both X and Y at once") -- that reasoning is about property
  *count*, not the per-property hook, so it never held. Worse: the existing regression test
  (`AlignSelectedElementToCrop_PushesExactlyOneUndoStep`) only compared `UndoCommand.CanExecute` (a
  bool) before/after one Undo, which stays `true` regardless of push count once a prior action has
  already made Undo available -- it passed against the actual bug.
- **[risk]** `SendToBack` was missing the already-at-the-back no-op guard its siblings
  (`BringToFront`/`MoveElementUp`/`MoveElementDown`) already have, in TWO sub-cases: an element
  already at collection index 0 with no background present (fell through to the unconditional
  branch, decrementing its own Z and pushing a bogus undo step on every click forever -- Z drift,
  cosmetically invisible since `Move(0,0)` is a no-op, but real undo-stack/`HasUnsavedEdits`
  pollution); an element already sitting immediately after the background (`target == index`,
  `Move(index, index)` is a no-op but the undo step and a full `RecomputePreview()` pass weren't).
- **[risk]** Blanking a SINGLE fill-bar field (typing, not the bulk "Clear fields" command) stored
  an empty string in `_templateVariables` unconditionally -- `MacroTextResolver`'s unfilled-token-
  resolves-verbatim branch only fires when the key is ABSENT, so a present-but-empty value made
  e.g. `{his_call}` silently resolve to `""` instead of showing the placeholder token again. This is
  the EXACT `"DE "` state `ClearTemplateVariablesCommand`'s own doc comment already classifies as a
  code-review blocker for the BULK-clear path ("easy to transmit by mistake") -- the single-field-
  edit path had the identical bug the bulk-clear path was specifically fixed to avoid, just never
  patched on this sibling.
- **[risk]** `RemoveOverlayElement` only null-checked its `element` parameter, not whether it's a
  stale reference no longer in `OverlayElements` (e.g. a queued click racing an Undo, which replaces
  every element wholesale via `ApplyState`) -- `ObservableCollection.Remove` already no-ops silently
  on an absent element, so the only visible effect of a stale click was a bogus undo step.
  `SetAsBackground`/`MoveElementUp`/`MoveElementDown`/`BringToFront`/`SendToBack` all already check
  "is this element still really here" BEFORE `PushUndoSnapshot()`; `RemoveOverlayElement` didn't.
- **[risk, deferred to backlog]** Fill-bar value edits don't push an undo step at all, but
  `ClearTemplateVariables` does -- an unrelated Undo (e.g. undoing an element move) can silently
  revert a fill-bar value the operator just typed, recoverable only via Redo (and only until the
  next new edit clears the redo stack). Judged genuine but non-corrupting (recoverable, not data
  loss), and "should template-variable edits become undo-tracked at all" is a UX design call, not a
  clear-cut bug fix -- a per-keystroke coalesced push would also interleave typing with element-
  geometry undo history in a way that needs a decision, not a patch. Round 2 explicitly agreed this
  is a reasonable scope boundary for this round.

Fixed: `AlignSelectedElementToCrop`'s X/Y switch wrapped in `_suspendPreview = true; try { ... }
finally { _suspendPreview = false; }` (matching `SetAsBackground`'s established pattern),
`RecomputePreview()` still runs exactly once, after the wrap; the method's doc comment corrected to
name the real suppression mechanism instead of the false premise. `SendToBack` gained
`if (backgroundIndex < 0 ? index == 0 : backgroundIndex + 1 == index) { return; }` right after the
backward background scan, before `PushUndoSnapshot()` -- verified round 2: since the scan only
searches indices below `index`, `backgroundIndex + 1 <= index` always holds, so equality is exactly
the "already immediately after the background" case, with no legitimate send-to-back rejected.
`OnTemplateVariableValueChanged` now does `if (value.Length == 0) { _templateVariables.Remove(key);
}` instead of an unconditional dictionary write -- the row's own displayed `Value` (an independent
property on `TemplateVariableRowViewModel`, not read from the dictionary) stays visually blank
either way, only the resolution behavior changes. `RemoveOverlayElement` gained
`!OverlayElements.Contains(element)` to its existing null guard, checked before the push, matching
the established sibling convention.

Four regression tests added/rewritten (`AlignSelectedElementToCrop_PushesExactlyOneUndoStep`
REWRITTEN to use the same "count total undo depth, Undo exactly twice, assert nothing left" pattern
`SetAsBackground_PushesExactlyOneUndoStep` already established -- a bare `CanExecute` bool comparison
can't distinguish "1 push" from "2 identical pushes" either, same reasoning that test's own comment
already documents; `SendToBack_OnAnAlreadyBottommostElement_WithNoBackgroundPresent_IsATrueNoOp` +
`SendToBack_OnAnElementAlreadyImmediatelyAfterTheBackground_IsATrueNoOp`; `OnTemplateVariableValueChanged_
BlankedToEmpty_RemovesTheKeyInsteadOfStoringAnEmptyValue`, asserting `element.ResolvedText` -- the
only observable that actually distinguishes "empty value" from "absent key", same technique the
existing bulk-clear test already uses; `RemoveOverlayElement_StaleReferenceNoLongerInTheCollection_
IsATrueNoOp`). 320/320 `UI.Tests` pass for this file (was 316, +4), 707/707 full suite (was 703, +4),
clean solution-wide build.

**Round 2** (fresh agent, confirmation-only) -- **GO.** Traced the full suppression path for the
Align fix end-to-end across all three element types (`OverlayElementViewModel`/
`BoxElementViewModel`/`ImageElementViewModel`'s own `OnXChanging`/`OnYChanging` → 
`PushUndoSnapshotForGeometryChange` → `PushUndoSnapshotCoalesced` → early-returns on
`_suspendPreview`), confirmed the wrap covers all six switch arms and doesn't suppress the
(ungated) `SelectionReadoutText` live-update raise. Confirmed the `SendToBack` guard's logic holds
for every reachable case (no false rejection of a legitimate send-to-back at any other index) and
doesn't interfere with the separate, pre-existing background-itself guard. Confirmed the fill-bar
key-removal doesn't corrupt `CanClearTemplateVariables`'s re-notification or the undo-snapshot
round-trip (`ApplyState` clears-and-reseeds from the captured dictionary, so a removed key correctly
round-trips as "absent," not silently re-introduced by `RescanTemplateVariables`, which never seeds
keys). Confirmed `RemoveOverlayElement`'s guard placement and that reference-equality `Contains` is
correct for these `sealed partial class` element VMs (not records). Explicitly agreed the R5
deferral is a defensible design-decision boundary, not a hidden defect. Two nits noted, not
actioned: the old `SendToBack` fall-through incidentally repaired a Z-inversion in a pathological,
already-documented-out-of-scope background/Z-ordering edge case (undocumented, accidental, not worth
chasing); `AlignSelectedElementToCrop`'s bare `finally` would clear an outer suspension if ever
nested, unreachable today and identical to the accepted `SetAsBackground`/`AddPlateBehindText`
convention. Explicit "close chunk 12 (Area B)" call.

**Chunk 12 (Area B) CLOSED (2026-08-22)** -- 2 rounds. Round 1 found and fixed 4 real risk-tier bugs
(duplicate undo push with a self-defeating test, missing already-at-back no-op guard in 2 sub-cases,
a fill-bar single-field-edit path with the identical bug its own bulk-clear sibling was already
fixed to avoid, a stale-reference undo-stack pollution gap), deliberately deferred 1 finding
(fill-bar edits not undo-tracked) as a genuine but non-corrupting UX design question rather than a
clear-cut fix; round 2 clean GO, no further action. Area C remains -- chunk 13.

## Chunk 13: TxImageEditorPaneViewModel.cs, Area C (rotate/crop math/undo-redo/state snapshot)

Third and last of 3 area-based sub-chunks for this 3911-line file (the largest in the whole Tier B
sweep) -- and the last chunk of the entire `UI/ViewModels` sweep. Area C: `Rotate`/`RotateImageOnly`,
crop-rect manipulation (`ApplyCropMove`/`ApplyCropResize`/`ApplyCropResizeAspectLocked`),
`RecomputePreview`, `Undo`/`Redo`/`Revert`/`PushUndoSnapshot`/`PushUndoSnapshotCoalesced`,
`ApplyState`, `OnOverlayElementPropertyChanged`, the `Log` class -- roughly lines 2888-3911, to the
end of the file.

**Round 1** -- unconditional GO, no blockers, 3 risk-tier findings (auditor explicitly stated
neither of the two worth-fixing findings needs a second round to land):

- **[risk]** `Undo`/`Redo` (both funnel through `ApplyState`) never reset `_pendingCoalesceProperty`,
  unlike `PushUndoSnapshot`/`PushUndoSnapshotCoalesced`, which both do. An Undo landing inside an
  open coalescing window (e.g. right after a slider edit, before that edit's own coalesce window
  naturally closes) left the marker pointing at the just-reverted property -- the VERY NEXT edit to
  that SAME property then hit `PushUndoSnapshotCoalesced`'s own `_pendingCoalesceProperty ==
  propertyName -> return` early-out and silently pushed NOTHING. The edit was still applied (the
  value really did change) but became invisibly non-undoable, with `HasUnsavedEdits` reading false
  while the document was actually dirty.
- **[risk]** `OnOverlayElementPropertyChanged`'s recompute-trigger filter was missing `IsSelected`/
  `IsEditingText` -- both are pure interaction state, the same tier as `Locked`/`IsBackground`/
  `BlocksHitTesting` (already filtered), but weren't filtered themselves. Clicking a different
  element on the canvas flips `IsSelected` false-then-true across two elements
  (`OnSelectedOverlayElementChanged`'s own loop), firing TWO extra full `Crop->Resize->
  ApplyAdjustments->ApplyTemplate` passes, two extra `RescanTemplateVariables` sweeps, and two extra
  `PreviewImage` bitmap allocations for a change that can never affect pipeline output. Output stays
  correct -- this is a perf-only fix, the exact class the filter's own doc comment exists to
  prevent.
- **[risk, folded into the same fix]** `Undo`/`Redo` also never disarmed `IsCancelArmed`/
  `_pendingRecallTemplateId`, unlike every real-edit path (`PushUndoSnapshot`/
  `PushUndoSnapshotCoalesced` both do) -- `IsCancelArmed`'s own doc comment already states the
  invariant ("a stale arm from long before would silently skip the warning on a LATER, unrelated
  Cancel click"); an Undo is a real state change and wasn't treated as one.

No blocker: round 1 could not construct a scenario in Area C that produces wrong pipeline output, a
crash, or lost document state. `Undo`/`Redo` stack symmetry, `ApplyState`'s field-by-field restore
completeness (including deliberately NOT re-sorting elements by Z on re-add, which is correct here
unlike the constructor/template-load paths -- an undo must restore the exact prior collection order,
including a legitimately Z-tied order), the rotate/crop-transform math (re-derived by hand against
this codebase's own top-left-origin/Y-down convention, not assumed from comments), and every
`_suspendPreview` site's try/finally correctness were all independently verified clean.

Fixed: `ApplyState` now resets `IsCancelArmed = false; _pendingRecallTemplateId = null;
_pendingCoalesceProperty = null;` as its first three statements, before `_suspendPreview = true` --
covers `Undo`, `Redo`, and `Revert` (which just calls `Undo` repeatedly) in one place, matching
`PushUndoSnapshot`'s own established reset list. `OnOverlayElementPropertyChanged`'s filter gained
`ITemplateElementViewModel.IsSelected`/`OverlayElementViewModel.IsEditingText` alongside the existing
`Locked`/`IsBackground`/`BlocksHitTesting` pure-interaction-state group.

Three regression tests added: `Undo_LandingInsideAnOpenCoalescingWindow_
TheNextEditToTheSamePropertyStillPushesAFreshStep` -- a direct, non-timing-dependent discriminator
(`Brightness = 20` opens a coalescing window with no `RunJobs()`, Undo reverts to 0, `Brightness =
30` must push a fresh step; pre-fix this silently pushed nothing, so `UndoCommand.CanExecute`
read false where the fix makes it true); `SwitchingSelection_DoesNotTriggerAPipelineRecompute`,
asserting `FakeTransmitImagePreparer.ApplyTemplateCallCount` is unchanged across a selection swap;
`Cancel_ArmedThenUndoHappens_DisarmsTheConfirm`, mirroring the existing `Cancel_
ArmedThenARealEditHappens_DisarmsTheConfirm` test's own pattern with Undo as the real-edit trigger.
323/323 `UI.Tests` pass for this file (was 320, +3), 710/710 full suite (was 707, +3), clean
solution-wide build.

**No round 2 dispatched** -- round 1's own verdict was an unconditional GO with no blockers, and the
auditor explicitly stated neither of the two findings worth fixing needed a second round to land
(precedented by Tier A's own "unconditional go, no round 2 needed" chunks, e.g. 7a/7b/7c). The two
fixes were applied with full end-to-end tracing of their own suppression/reset mechanisms (not just
pattern-matched from the auditor's suggestion) and verified via genuinely discriminating regression
tests before closing.

**Chunk 13 (Area C) CLOSED (2026-08-22)** -- 1 round (unconditional GO), 2 real risk-tier fixes
applied without a confirmation round. This closes ALL 3 areas of `TxImageEditorPaneViewModel.cs`
(chunks 11-13) and, with it, the ENTIRE `UI/ViewModels` sweep (13/13 chunks).

## Remaining Application files sweep -- IN PROGRESS, started 2026-08-22

Step 0 triage (19 files in `src/ScanlineStudio.Application/`): trivial (skip, read as context
only) -- pure interfaces (`ISstvSessionService.cs`, `IRadioSessionService.cs`,
`ILogbookSessionService.cs`, `ITemplateStore.cs`), pure DTO records (`OptionsSnapshot.cs`,
`PersistedTemplateElement.cs`, `OperatorSettings.cs`, `ReadyRackSettings.cs`,
`AppPerformanceSettings.cs`, `LogQsoResult.cs`), generated `JsonSerializerContext` partials
(`OperatorSettingsJsonContext.cs`, `AppPerformanceSettingsJsonContext.cs`), `AssemblyInfo.cs`. Real
logic (audit): `MaidenheadLocator.cs` (129), `MacroTextResolver.cs` (186), `RadioSessionService.cs`
(167), `LogbookSessionService.cs` (174), `OptionsSettingsService.cs` (285), `TemplateStore.cs`
(275), `SstvSessionService.cs` (3188).

**Cadence decision (user, 2026-08-22):** `SstvSessionService.cs` handles PTT keying/unkeying,
transmit watchdogs, and cleanup races -- concurrency-sensitive code. Rather than Tier B's default
"one round + one confirmation, cap 3," the user chose CLAUDE.md's own §7 non-negotiable
concurrency cadence for this one file specifically: a full 2-round plan-review (likely needing its
own chunk-split plan first, given its size) plus up-to-3-round code-review per chunk. The other 6
real-logic files are NOT concurrency-hot in the same way and stay on standard Tier B cadence.
Sequencing: the 6 smaller files first (keeps momentum, matches this sweep's own established
rhythm), `SstvSessionService.cs`'s heavier plan-review process last, as its own prepared
undertaking.

## Chunk 1: MaidenheadLocator.cs + MacroTextResolver.cs

Both confirmed NEW functionality with no legacy YONIQ/MMSSTV precedent (their own doc comments,
verified via a real grep across the legacy tree) -- pure new-code correctness review, not a
legacy-parity port audit.

**Round 1** -- unconditional GO, no blockers, 2 risk-tier findings (auditor explicitly did not
require a second round):

- **[risk]** `MaidenheadLocator.TryToLatLon`'s invalid-subsquare false-return path left
  `latitude`/`longitude` at the SW-corner values already computed by the field/square math above it,
  while every OTHER false-return path in the same method correctly leaves them at their pre-zeroed
  defaults -- a stale, non-zero out-param on a false return from a public static API. Harmless today
  (the sole caller pre-zeroes its own outs and checks the bool first), but the exact "one path
  missing a guard its siblings already have" shape this whole sweep keeps finding.
- **[risk]** 8-character extended Maidenhead locators (a normal, widely-used VHF/microwave-grade
  precision) were rejected outright by the length check (`4 or 6` only) -- strictly MORE precise
  than the accepted 6-character form, with no reason to reject it. Repro: an operator types
  `FN31pr12` into the `his_grid` fill-bar row -- `TryToLatLon` fails, `TryComputeDistanceBearing`
  fails, `MacroTextResolver` maps that to `string.Empty`, and the DIST/BEAM overlay fields render
  blank in the transmitted image with no error and no indication anything went wrong -- the sweep's
  "silently dropped value" class hitting a real user path.

Both math files themselves were independently verified clean and precisely correct, not just
"probably fine": the haversine/bearing formulas hand-derived against real-world coordinates (not
assumed from the implementation), the antipodal-NaN clamp and pole-singularity unreachability both
re-confirmed, `MacroTextResolver`'s token-fallback tiers (known-macro-blank vs.
unknown-variable-verbatim) confirmed internally consistent and exception-safe end to end, and the
`{dist}`/`{bearing}` → `his_grid` cross-file wiring (already fixed in earlier UI/ViewModels rounds)
re-confirmed still correct.

Fixed: the invalid-subsquare branch now re-zeros `latitude`/`longitude` before returning false.
`TryToLatLon`'s length check now accepts 8 (`4 or 6 or 8`), validating the trailing digit pair and
then dropping it (same output precision as the 6-character form, no new precision-tier math added)
rather than hard-rejecting a strictly-more-precise valid input. Four regression tests added
(`TryToLatLon_EightCharacterLocator_ResolvesToTheSameCellAsItsSixCharacterPrefix`,
`TryToLatLon_InvalidEightCharacterLocators_ReturnsFalse` (2 theory cases: non-digit trailing pair,
7-character invalid length), `TryToLatLon_InvalidSubsquare_LeavesOutParamsAtZero_
NotTheStaleSwCornerValues`). 25/25 `Application.Tests` pass for this file (was 17, +8 test
executions across the new theory cases), 237/237 full `Application.Tests` suite, clean
solution-wide build.

**No round 2 dispatched** -- round 1's own verdict was an unconditional GO with no blockers, same
precedent as chunk 13's own "no round 2 needed" close. Both fixes were small, fully traced against
the actual math/control-flow (not just pattern-matched from the auditor's suggestion), and verified
via genuinely discriminating regression tests before closing.

**Chunk 1 CLOSED (2026-08-22)** -- 1 round (unconditional GO), 2 real risk-tier fixes applied
without a confirmation round.

## Chunk 2: RadioSessionService.cs + LogbookSessionService.cs

`SstvSessionService.cs` (the same directory's much larger, concurrency-sensitive file) deliberately
excluded from this chunk's scope -- it gets its own heavier plan-review process later, per the
user's own cadence decision (see the sweep's own intro note above).

**Round 1** -- unconditional GO, no blockers, 3 risk-tier findings (auditor explicitly recommended
2 of the 3 as follow-ups to apply, not blockers requiring another round):

- **[risk]** `LogbookSessionService.LookupCallsignAsync` violates its own interface doc comment's
  "never throws, always returns a result" contract (the SAME contract `LogQsoAsync` already has,
  fixed in Tier A Batch 10 chunk 10c) -- unlike `LogQsoAsync`'s own settings read, which IS wrapped
  in a try/catch for the identical reason, this one ran unguarded. Two reachable throws: a
  hand-edited `settings.json` with a malformed `QrzLookup` section throws `JsonException` out of
  `GetSection`; an unreadable settings file throws `UnauthorizedAccessException` out of `LoadAsync`
  (not caught by `JsonSettingsStore`'s own whole-file-parse catch, which only covers `IOException` --
  literally the scenario this file's own OWN chunk-10c fix comment names). Contained today by
  `RxImagePaneViewModel.LookupQrzAsync`'s own catch-all (a UI-layer net, not user-visible data
  loss), but the contract violation itself is real.
- **[risk, deferred to backlog]** Unserialized read-modify-write on `settings.json` (Load → 
  `WithSection` → Save with an await between, no lock) in `RadioSessionService`'s
  `SaveSafetySettingsAsync`/`SaveFrequencyPresetsAsync` can silently drop a concurrent write from an
  unrelated setting saved in the same window. Real, but codebase-wide (the correct fix is a
  serializing `UpdateAsync(Func<AppSettings,AppSettings>)` on `ISettingsStore` itself, not a
  per-caller patch) and explicitly recommended as backlog, not this chunk's fix -- a worse variant
  already exists off this chunk's own scope in `OptionsSettingsService.cs` (saves from a
  dialog-open-time snapshot, discarding ANY concurrent write made while the dialog is open), noted
  for when that file's own chunk comes up.
- **[risk, coverage gap, fixed via tests]** Zero test coverage existed anywhere for the safety-
  settings round-trip (`GetSafetySettingsAsync`/`SaveSafetySettingsAsync`) -- correct by inspection
  (exactly 2 fields, both mapped in both directions; a missing section correctly yields the
  documented default `SwrCutoffThreshold = 3.0`), but nothing would catch a future regression (a
  third field added and wired only one direction).

Also verified clean (not findings): `TestConnectionAsync`'s exception containment is airtight
across factory-create/poll/dispose, all independently tested including a double-fault case;
`LogQsoAsync`'s persist-first ordering and post-persist guard correct and tested; QRZ
enabled+non-empty-credential gating symmetric between upload and lookup; no secrets reach any log
call; no cross-call state cached incorrectly in either service (the one real cache,
`QrzCallsignLookup`'s session, is correctly keyed and invalidated on credential change).

Fixed: `LookupCallsignAsync`'s settings read now wrapped in `try/catch (Exception ex) when (ex is
not OperationCanceledException)`, logging via a new `Log.LookupCallsignSettingsReadFailed` and
returning `new QrzCallsignLookupResult(false, null, null, null, ex.Message)` -- matching
`LogQsoAsync`'s own established shape for the identical class of failure. Two regression tests
added (`LookupCallsignAsync_SettingsLoadThrows_ReturnsFailureInsteadOfThrowing`, using a new
`FakeSettingsStore.LoadAsyncException` hook; `GetSafetySettingsAsync_NoSectionConfigured_
ReturnsDefaults` + `SaveSafetySettingsAsync_ThenGet_RoundTripsBothFields` closing the coverage
gap). 31/31 `Application.Tests` pass for this pair, 240/240 full suite (one unrelated,
non-reproducing timing flake in `SstvSessionServicePttSafetyTests.cs` on the first full-suite run,
confirmed pre-existing and not caused by this chunk -- passed both in isolation and on a clean
re-run of the full suite), clean solution-wide build.

**No round 2 dispatched** -- round 1's own verdict was an unconditional GO with no blockers, and
the auditor explicitly recommended the applied fixes as follow-ups rather than requiring another
round, same precedent as chunk 1 and Tier A's own "unconditional go" chunks. The one deferred
finding (unserialized settings read-modify-write) was explicitly recommended as backlog by the
auditor itself, not a gap in this round's own coverage.

**Chunk 2 CLOSED (2026-08-22)** -- 1 round (unconditional GO), 1 real risk-tier bug fixed
(contract-violating unguarded settings read) plus a coverage gap closed with 2 new tests; 1 finding
explicitly deferred to backlog per the auditor's own recommendation.

## Chunk 3: OptionsSettingsService.cs

The Options-dialog UI wrapper around this exact service (`OptionsWindowViewModel.cs`) already had
its own sibling-inconsistency fixes applied earlier in this sweep (UI/ViewModels chunk 8) -- this
chunk audits the SERVICE layer underneath it independently, not a re-litigation of that already-
closed VM-layer work.

**Round 1** -- unconditional GO, no blockers, 2 risk-tier findings (auditor explicitly said not to
dispatch another round):

- **[risk, deferred to backlog]** `SaveAsync` never re-reads `settings.json` -- it always builds its
  save from `_loadedSettings`, captured once at dialog-open time. Chunk 2's own off-scope note
  flagged this as "any SWR/preset write made while the Options dialog is open is discarded on
  Save"; round 1 confirmed the CODE but corrected the REACHABILITY -- the Options dialog is modal,
  so no OTHER UI-driven save can interleave while it's open (checked all 5 other `ISettingsStore
  .SaveAsync` call sites; all are UI-driven, all blocked by the modal). One real, narrower race
  survives: dragging the TX volume slider (400ms debounce, `RadioStatusViewModel
  .PersistVolumeDebouncedAsync`) then opening Options and hitting Save inside that 400ms window
  reverts the volume write. A local fix (re-read fresh before Save) was considered and rejected --
  `JsonSettingsStore.LoadAsync` silently falls back to `new AppSettings()` on a corrupt/unreadable
  file, so a transient IO blip at the wrong instant would trade one narrow field-loss window for a
  much worse whole-file-defaults window. Same reasoning chunk 2 already used to defer its own
  read-modify-write finding: the correct fix is a serializing `ISettingsStore.MutateAsync` at the
  store layer (already backlogged there), not a per-caller patch here.
- **[risk, fixed]** Four sections (`LocalizationSettings`/`AppPerformanceSettings`/
  `OperatorSettings`/`QrzLookupSettings`) built their saved value via a fresh `new X { ... }`
  instead of `previous with { ... }` like every OTHER section in this method (`AudioDeviceSettings`/
  `RadioConnectionSettings`/`SstvDecoderSettings`/`StationIdSettings`/`AdifUdpStreamingSettings` all
  already preserve non-dialog fields this way -- `AfcEnabled`/`ClientId` specifically). Harmless
  TODAY only because none of these four records currently has a field the dialog doesn't own
  (verified field-by-field) -- but it's the exact sibling-inconsistency shape this whole sweep keeps
  finding in latent form: the day a non-dialog field is added to any of these four (a QRZ
  session-cache token, an operator default-power field, a locale date-format toggle), every Options
  Save would silently reset it, with no existing test able to catch it.

Also verified clean (not findings): full bidirectional census of all 36 `OptionsSnapshot` fields
across both `LoadAsync`/`SaveAsync` -- no orphan field either direction; `Defaults` matches
`LoadAsync`'s own absent-section fallbacks value-for-value on all 36 fields, including the 3
deliberately-different ones; the `SampleRate` load-vs-save fallback asymmetry (normalize-to-11025 on
Load, preserve-previous-valid on Save) is deliberate and correctly cites its own legacy source line;
`CwText`'s `?? string.Empty` vs `NrRstText`'s raw passthrough is NOT a sibling inconsistency once
each field's own resurrection-on-reload semantics are checked (genuinely different, not an
oversight); ADIF migration symmetric on both Load and Save; save-then-commit ordering correct (store
write succeeds before `_loadedSettings` is updated, so a failed save can't leave a phantom-committed
base); a malformed section can throw `JsonException` out of `LoadAsync` but `_loadedSettings` is
assigned before any section read, so it's never left transiently empty.

Fixed: all four sections now read their own `previousX` from `_loadedSettings` up front and build
via `previousX with { ... }`, matching the other five sections' already-established pattern. No
dedicated regression test added -- the fix only has an observable effect once a non-dialog field is
added to one of these four record types, which doesn't exist today; there is nothing to assert
against yet (same "correct by inspection, defensive fix, nothing to observe" judgment call used
elsewhere in this sweep for preventative sibling-consistency fixes). 240/240 `Application.Tests`
pass (no change, confirming no regression), 68/68 `OptionsWindowViewModelTests` (the VM-layer
consumer) still pass unchanged, clean solution-wide build.

**No round 2 dispatched** -- round 1's own verdict was an unconditional GO with no blockers, and the
auditor's own closing line was explicit: "Don't dispatch another review round on this file." The one
deferred finding (stale-settings-base race) was independently re-derived and re-confirmed narrower
than chunk 2's own version of the same class, with the SAME reasoning for why a local fix would be a
net-negative trade -- not a gap in this round's own coverage.

**Chunk 3 CLOSED (2026-08-22)** -- 1 round (unconditional GO), 1 real risk-tier sibling-
inconsistency bug fixed preventatively; 1 finding re-confirmed and re-deferred to the same
`ISettingsStore`-level backlog item chunk 2 already opened.

## Chunk 4: TemplateStore.cs

This file was already extensively read and traced during UI/ViewModels chunk 11
(`TxImageEditorPaneViewModel.cs` Area A), which found and fixed a real blocker in the CALLER's own
overwrite-save logic (`SaveTemplateAsync` used to delete a pre-existing template up front, then
delete it again unconditionally on failure). This chunk audits `TemplateStore.cs` itself -- the
actual persistence-layer implementation -- independently, not a re-audit of that already-closed
caller-side fix.

**Round 1** -- unconditional GO, no blockers, 3 risk-tier findings (auditor explicitly said none
justified dispatching another round, though recommended fixing 2 as "highest value"):

- **[risk]** `ListAsync`'s per-template corrupt-manifest guard caught only `JsonException` -- but
  `IOException` (a file locked mid-sync by OneDrive/Dropbox, or a concurrent writer),
  `UnauthorizedAccessException`, and a TOCTOU race against the `File.Exists` check just above (a
  fire-and-forget `RefreshAsync` running concurrently with a `DeleteAsync` on that exact template)
  all hit the IDENTICAL "one bad template must not blank the WHOLE rack" scenario the
  `JsonException` catch was already added to fix (Tier A Batch 10 chunk 10b) -- just via a
  different exception type the guard never covered.
- **[risk]** `LoadAsync` returned `manifest.Elements` directly with no null guard -- a missing or
  explicitly-null `"Elements"` property in a hand-edited `template.json` deserializes with NO
  `JsonException` (this format explicitly supports hand-copying/editing template folders, per its
  own doc comment), so the caller's `.Count` read NREs instead of degrading gracefully. Same root
  cause, same fix needed for an individual `null` entry WITHIN the list (`"Elements":[null, {...}]`)
  -- this file's OWN `RenderThumbnailAsync`/`ToTemplateElementAsync` (used on every re-save)
  dereferences each element directly, NREing on the switch's own `default:` arm.
- **[risk, security hardening]** `GetAssetPath` builds `Path.Combine(templateDir, "assets",
  assetFileName)` with no validation that `assetFileName` is a bare file name -- `Path.Combine`
  rejects neither `../` traversal nor a rooted path. The WRITE side is never at risk (asset
  filenames are always freshly minted GUIDs, never persisted/attacker-influenced), but the READ
  side (loading a shared/downloaded template folder -- this format's own documented distribution
  mechanism) could point the app at an arbitrary file elsewhere on disk via a malicious/malformed
  manifest. Auditor's own characterization: "hardening, not exploitation" (a non-image target just
  fails to decode; the wrong image loading into the visible editor canvas is not silent).

Also verified clean (not findings): `SaveAsync`'s write order (thumbnail then manifest) is the SAFE
order -- a thumbnail failure aborts before `template.json` exists, so `ListAsync`'s own gate never
shows a manifest-less template; `DeleteAsync` is a correct no-op on a non-existent id (a caller's
own cleanup path explicitly depends on this) and removes `assets/` too via `recursive: true`;
`CreateTemplateId`'s slug-sanitization whitelists `[a-z0-9-]`, making path traversal via a
user-typed NAME impossible (as opposed to a persisted asset filename, the actual finding above);
`LoadAsync` never touches asset files itself, so a missing/deleted asset can't take down a template
load from this layer.

Fixed: `ListAsync`'s catch broadened to `catch (Exception ex) when (ex is JsonException or
IOException or UnauthorizedAccessException)` (still lets `OperationCanceledException` propagate --
a real cancellation must still abort the whole call, not be treated as "one bad template").
`LoadAsync` now filters `manifest.Elements ?? []` through an explicit loop that skips any null
entry, so every downstream consumer -- this file's own `RenderThumbnailAsync` and the caller's own
snapshot mapping -- always sees a clean, non-null list. `GetAssetPath` now throws
`InvalidOperationException` when `Path.GetFileName(assetFileName) != assetFileName` (rejects both
traversal and rooted paths) -- kept as a throw (not a per-element skip) since this file's own
`SaveAsync`/`RenderThumbnailAsync` path already treats a thumbnail-render failure as "abort the
whole save cleanly" by design, and a throw from the UI-layer load path (a different, already-closed
chunk) surfaces as a clear, loud load-failure error rather than a silent partial-degrade -- a
reasonable, conservative outcome for a deliberately-malicious-manifest scenario specifically, not
weakened by attempting a more invasive per-element skip across a file this chunk doesn't own.

Six regression tests added: `LoadAsync_ManifestWithNullElementsProperty_
ReturnsEmptyDocumentInsteadOfThrowing`, `LoadAsync_ManifestWithANullElementInTheList_
SkipsItInsteadOfThrowing` (derives its JSON from a REAL save, then hand-injects the one malformed
`null` entry a real save could never produce, rather than hand-typing a JSON literal that would
need to guess `Rgb24`'s own exact JSON shape), `GetAssetPath_AssetFileNameEscapesTheAssetsFolder_
Throws` (3 theory cases: `../`, a nested traversal, a rooted path) + `GetAssetPath_
OrdinaryAssetFileName_StillWorks` (a legitimate GUID-shaped name must still pass). No dedicated
test added for the broadened `ListAsync` catch -- reliably triggering a real `IOException`/
`UnauthorizedAccessException` (as opposed to `JsonException`, already tested) from a portable xunit
test without a platform-dependent trick (file locking behaves differently between Windows and Linux)
was judged not worth the added fragility for a fix that's correct by inspection and mirrors an
already-tested sibling catch exactly. 20/20 `Application.Tests` pass for this file (was 14, +6),
246/246 full suite (was 240, +6), clean solution-wide build.

**No round 2 dispatched** -- round 1's own verdict was an unconditional GO with no blockers, and the
auditor explicitly said none of the findings justified dispatching another round. All 3 fixes were
applied with full reasoning about blast radius (specifically: whether to throw vs. skip-per-element
for the security-hardening fix, deliberately choosing the more conservative throw given the
attacker-crafted-manifest scenario it protects against) rather than pattern-matched from the
auditor's own suggested one-liner.

**Chunk 4 CLOSED (2026-08-22)** -- 1 round (unconditional GO), 3 real risk-tier fixes applied
(broadened exception handling matching an already-established sibling guard, null-safety on a
hand-editable manifest format, and a path-traversal hardening fix) without a confirmation round.

## Chunk 5: SstvSessionService.cs -- full §7 concurrency cadence

The last file in the Tier B "remaining Application files" scope, and the largest file in the whole
Tier B sweep (3188 lines, larger than `TxImageEditorPaneViewModel.cs`, which needed a 3-way split).
PTT keying/unkeying, transmit playback watchdogs, RX start/stop, cleanup races -- real concurrency,
per CLAUDE.md §7 this is non-negotiable for a full plan-review + up-to-3-round code-review per area,
not this sweep's usual lighter "1 round + 1 confirmation" default. User confirmed this cadence
explicitly (2026-08-22), as an alternative to Tier B's default or skipping the file entirely.

**Plan-review (2026-08-22)** -- proposed a 5-area split by concurrency-risk concentration in
naive top-to-bottom order (fields/PTT-lock → RX/TX entry → `PlayWithPttAsync` → cleanup/watchdog →
disposal). Plan-review found this order backwards: `SetPttLockAsync` (the fields/PTT-lock area) and
`PlayWithPttAsync` both call into the cleanup helpers repeatedly, and this file's OWN documented
history (round 20: "`TryUnkeyPttAsync`'s own 'never throws' contract turned out to be false") is
exactly what happens when a caller is reviewed against an unverified callee contract. Reshaped to
callee-first: helpers audited before their two caller areas. Two area-boundary doc-comment offsets
also corrected (a boundary landing between a field/method and its own explanatory comment).
Plan-review also produced 8 cross-cutting invariants (epoch-pair protocol, "believed keyed" belief
triple, `_keyedTransmitCompletion` publish-clear pairing, an un-fenced-by-design handoff flag,
`_disposed` fencing, timeout-budget arithmetic, `SafeLog` coverage, `_isReceiving` check-then-act)
that this file has gotten wrong multiple times before (6, 3, and other repeat-finding counts cited
per-invariant) precisely because a per-area reviewer only sees their own slice -- these are now
carried into every area's own audit prompt explicitly, not left implicit. Full plan (5 areas, line
ranges, all 8 invariants with site line numbers) recorded durably in `PROJECT_BRIEF.md` for
cold-start resume, since this is a multi-session undertaking.

### Area 1: shared cleanup/pump/device helpers (lines 2336-2709 + 2888-3070)

`UnkeyForCleanupAsync`, `StopPlaybackWithWatchdogAsync`, `TryUnkeyPttAsync`, `SafeLog`,
`TryCleanupAsync`, `ResumeReceivingBoundedAsync`, `GenerateTone`, `PumpToPlaybackAsync`,
`ReportTransmitProgress`, `EnqueueAllAsync`, `ResolveDeviceAsync`, `TryResolveDeviceAsync`,
`Get*DeviceNameAsync`, `LoadAudioSettingsAsync`. Audited first per the plan-review's callee-first
ordering.

**Round 1** -- unconditional GO, no blockers, 1 risk-tier finding plus a required whole-file
`SafeLog` coverage enumeration (cross-cutting invariant #7):

- **[risk]** `Log.UsingDefaultDevice` sat unwrapped on the SUCCESS path in `TryResolveDeviceAsync`
  -- a throwing logging provider (this file's own stated threat model) would throw AFTER a device
  was already successfully resolved into `fallback`, taking down every caller:
  `StartReceivingAsync` (RX dead), `TransmitAsync`/`TuneAsync` (TX dead), and both
  `GetConfigured*DeviceNameAsync` readouts. Textbook "one method has the guard, a near-identical
  sibling doesn't" -- `Log.RxStarted`/`Log.RxStopped` were `SafeLog`-wrapped for VERBATIM this
  shape in an earlier audit round ("a throwing provider still propagated out of this method after
  capture had genuinely started"), but the device-resolution success-path log call never got the
  same treatment.
- **Whole-file `SafeLog` coverage enumeration** (required this round, since `SafeLog` itself lives
  in Area 1, and 2 PRIOR rounds each separately claimed "applied everywhere" and were each wrong
  per the file's own doc comment): 71 `Log.*` call sites total, 62 already `SafeLog`-wrapped, 9
  unwrapped -- 6 of the 9 are on command-preamble/UI-command paths before any PTT state is latched
  (correctly unwrapped, not findings), the 3 device-resolution ones are the finding above plus 2
  nits (both immediately precede a `throw`, so a logging fault there only substitutes the
  exception identity, no state is skipped). **Zero** unwrapped calls remain inside any
  `catch`/`finally`/cleanup step/fault-observer/disposal path anywhere in the whole file -- this
  result is recorded here for later areas' reviewers to rely on, not re-derive.

Also verified clean (not findings, cross-cutting invariants #1/#2/#3/#5 as they touch Area 1):
epoch-pair snapshot/recheck/bump ordering in `UnkeyForCleanupAsync` matches the documented rule and
its sibling in Area 2 exactly, with no `_pttKeyEpoch` bump site in Area 1 (correct -- Area 1 never
issues a keying command, so nothing here can key-and-fail-to-record); the "believed keyed" triple's
only Area-1 site is the three-flag clear, done together under the epoch guard; Area 1 touches
neither `_keyedTransmitCompletion` nor `_keyedTransmitCount` at all (verified via whole-file grep);
`_disposed` fencing not applicable to Area 1 (no recheck sites here); cancellation mid-cleanup
cannot abandon a partial unkey/stop on any Area-1 path; capture/playback device-resolution branches
symmetrically on `forCapture` with identical fallback rules both directions.

Fixed: `Log.UsingDefaultDevice`'s call site now wrapped in `SafeLog(() => ...)`, matching the
`RxStarted`/`RxStopped` precedent exactly. The two nit-tier unwrapped device-resolution log calls
(`NoDeviceConfigured`/`ConfiguredDeviceNotFound`, both immediately preceding a `throw`) also wrapped
in the same pass for consistency, per the auditor's own "wrap for consistency when touching this"
recommendation. One regression test added (a throwing-logger scenario using the existing
`RecordingLogger.ThrowOnMessageContaining` hook, configured with a default-device enumerator +
`PlaybackDeviceId: null` settings so `TryResolveDeviceAsync`'s success path is actually exercised,
asserting `TransmitAsync` still completes and enqueues real playback samples despite the logging
fault). 247/247 `Application.Tests` pass (was 246, +1; one unrelated, non-reproducing timing flake
in an already-known-flaky PTT-safety test on the first run, confirmed pre-existing on a clean
re-run), clean solution-wide build.

**No round 2 dispatched** -- round 1's own verdict was an unconditional GO with an explicit "do not
dispatch another Area-1 round; move to the next area" call.

**Area 1 CLOSED (2026-08-22)** -- 1 round (unconditional GO), 1 real risk-tier bug fixed plus the
required whole-file `SafeLog` enumeration completed and recorded for later areas. Areas 2-5 remain.

### Area 2: fields/constructor/PTT-lock/RX-decode plumbing (lines 1-1066)

`SetPttLockAsync` (~470 lines), RX-decode event plumbing (`ModeDetected`/`DecodeRestarted`/
`StationIdDecoded`), decoder property pass-throughs, maintenance-warning handlers, `ForceMode`/
`RequestReSync`/`RequestCorrectSlant`. Second area per the plan-review's callee-first ordering --
Area 1's own contracts (already verified) are trusted here, not re-audited.

**Round 1** -- GO, no blockers, but 2 real risk-tier regressions found in `SetPttLockAsync`'s own
engage-failure catch, both introduced by the SAME prior widening (the catch's own filter changed
from `when (rigIsRealAtKeyTime)` to `when (locked)`, which correctly fixed one gap but silently
broke two OTHER invariants that used to be true "for free" under the narrower filter) -- plus 1
lower-risk finding, plus nits:

- **[risk]** With `RigId == "none"` (the default, no-radio-configured state), the whole production
  chain (`RadioSessionService` -> `RadioController` -> `NoneRadioProtocol`) throws SYNCHRONOUSLY,
  before `pttCommand` is ever assigned -- nothing was dispatched to a real backend, so nothing
  could have been physically keyed. But the catch's own state-latching write (bumping
  `_pttKeyEpoch`, setting `_pttLeftKeyedByCall = true`) was unconditional inside the `when
  (locked)` filter, so this benign no-radio case ALSO latched "possibly keyed" -- permanently
  violating `_pttLeftKeyedByCall`'s own documented invariant ("never fires for the benign
  `RigId=='none'` path"), since neither clear site can ever run without a confirmed un-key, which
  needs a real rig that was never involved. `DisposeAsync`'s backstop then fires a false Critical
  "PTT MAY STILL BE KEYED" on a machine with no radio at all -- and the erosion compounds: every
  SUBSEQUENT transmit's own baseline then reads this same stale true, repeating the false Critical
  for the rest of the process (the same signal-erosion class an earlier audit round's own fix
  targeted, now reachable with no radio configured at all).
- **[risk]** The immediate-recovery guard a few lines below (`if (_keyedTransmitCount == 1)`) has
  its own doc comment's premise -- "`_keyedTransmitCount` was already incremented for THIS call at
  the publish above (guarded by `rigIsRealAtKeyTime`, same as this catch)" -- which stopped being
  true the moment the catch's filter widened to `when (locked)`: the publish itself is STILL gated
  on `rigIsRealAtKeyTime` alone, so this catch can now run on paths where THIS call never
  published at all. A bare count-equals-1 check can then misfire in both directions: reading a
  DIFFERENT concurrent call's own registration as "safe to recover" and un-keying THAT call's
  genuinely in-flight, on-air transmission mid-frame (the exact harm this whole guard exists to
  prevent), or reading 0 and skipping a recovery this call itself should have run.
- **[risk, lower]** The emergency-unlock escape hatch's own gate-wait (`_pttLockGate.WaitAsync(ct)`)
  was still unconditionally `ct`-cancellable, one level above where an earlier audit round already
  fixed the identical shape at the command-dispatch call site itself (keeping the UNLOCK direction
  un-cancellable at the backend gate specifically, "it must stay queued and eventually reach the
  rig, not be cancellable away"). A caller cancelling while queued behind a wedged prior command
  (up to the cleanup timeout) could abort the emergency unlock before it ever reached the command
  dispatch that was ALREADY protected.
- **Test-fidelity caveat, closed as part of this fix**: the shared test fake's `SetPttAsync` is
  itself `async Task`, so its own `RigId=="none"` throw becomes a FAULTED Task, not a synchronous
  one -- a test written against the fake as-is could not reproduce the exact production shape
  (`pttCommand` staying null) the first fix above depends on.

Also verified clean (cross-cutting invariants #1/#2/#3/#5, #4 as they touch Area 2): epoch-pair
bump/snapshot/consume sites internally consistent and consistent with Area 1's own already-verified
consume-side behavior; the "believed keyed" triple's Area-2 site is byte-identical to its three
other copies, no 4th divergent copy introduced; `_keyedTransmitCompletion`/`_keyedTransmitCount`
publish/clear pairing correct on every exit path, decrement correctly precedes the lock-gate
release; `_disposed` recheck sites all pair with a full fence, not a bare volatile read;
`_rxPendingResumeAfterUnlock`'s Area-2 consumer correctly assumes no stronger ordering than the
deliberately-un-fenced design provides; the three RX-decode events are pure pass-throughs with no
raise site in this service at all (the actual raise/isolation logic lives in the decoder, already
correctly isolates a throwing subscriber); maintenance-warning handlers cannot strand a flag across
rapid restarts (single-threaded relative to each other by construction).

Fixed: the engage-failure catch's state-latching write now gated on `pttCommand is not null` (a
genuine dispatch to a real backend actually happened) instead of unconditional inside `when
(locked)`. The immediate-recovery guard now also requires `keyedCompletion is not null` (THIS
call's own publish genuinely ran) alongside the count check, restoring call-scoping the count alone
lost. The gate-wait now uses the same `locked ? ct : CancellationToken.None` conditional the
command-dispatch call site already uses, closing the one-level-up cancellation gap. The shared test
fake gained an opt-in `ThrowSynchronouslyOnNoneRig` flag (default false, every existing test
unaffected) that makes the fake's own `RigId=="none"` throw happen before its async state machine
starts, matching the real production shape precisely enough to test the `pttCommand is not null`
fix faithfully. One regression test added, mirroring an existing sibling test's own structure
(same shape, opposite conclusion: the sibling proves a REAL key-command failure DOES latch
"possibly keyed"; this one proves the benign no-radio case does NOT). No dedicated test added for
the second fix (the guard's own concurrency scenario needs a hung, genuinely-in-flight transmit
running concurrently with the no-radio call -- correct by inspection given the guard's logic is
now provably call-scoped, and disproportionate test-infrastructure cost for a fix on a method with
zero production callers today). 248/248 `Application.Tests` pass (was 247, +1), clean solution-wide
build.

**No round 2 dispatched** -- round 1's own verdict was GO with an explicit "do not spawn another
Area 2 review pass for R3 or the nits" call, and the auditor explicitly recommended folding the two
higher risk-tier findings into this same round rather than treating them as needing a separate
round of their own. Both were applied with full tracing of the actual regression mechanism (a
shared root cause -- the catch filter's own prior widening -- silently invalidating two OTHER
invariants' premises), not pattern-matched from the auditor's own suggested one-liners.

**Area 2 CLOSED (2026-08-22)** -- 1 round (GO, no blockers), 3 real risk-tier bugs fixed (2 of
them regressions from the SAME prior widening, both latent behind `SetPttLockAsync` having zero
production callers today but real and precisely characterized). Areas 3-5 remain.
