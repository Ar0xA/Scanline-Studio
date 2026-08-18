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
| — | `AnalogFmSstvDecoder.cs` (5967 lines) | Chunked separately — see "Chunking a mega-file" above. 10 chunks (D1-D9 + D0 field-lifecycle pass), D3+D8+D9 sequential/coupled, D1 first alone, D2 last. ~20-40 agent calls, own approval. Exact chunk boundaries (auditor-proposed, 2026-08-18 — re-verify line ranges before reuse if the file has since changed): **D1 — done** (2026-08-18, 3 rounds, see below). **D2** public surface/push/retention, 1216-1376 + 1953-2271 (run LAST — depends on knowing every cursor D1-D9 established). **D3** ReSync/AutoSync/AutoStop, 1377-1952. **D4** main loop + image teardown (`TryProcessBuffer`/`EndOfImage`), 2272-2689 — "orchestration hub, doubles as the map," run next. **D5** header search & dispatch, 2690-3470. **D6** commit/anchor/VIS decode, 3471-4234. **D7** AVT resolve + AFC, 4235-4750. **D8** slant tracking, 4751-5228. **D9** replay + `CorrectSlant`, 5229-5967. **D0** final whole-file field-lifecycle-only pass (every mutable field's reset/preserve correctness across every teardown path), run last, counts as one of the 2 required clean rounds. D3+D8+D9 mutate the same field cluster (`_slantLinePeakPosition`, `_lastLineSyncPeakPosition`, `_pendingSkipSamples`, `_slantCorrectionsDisabledForRestOfImage`, `_pendingReplayRequested`, `_rxBufferAnchorSample`, `_rxBufferBaseTransmissionLine`, `_effectiveSamplesPerLine` — the documented "local-vs-absolute index, 3 separate times" bug nest, CLAUDE.md §4) so they stay on ONE sequential agent lineage, never independent parallel reviews. Execution order: ~~D1 alone~~ → ~~D4~~ → ~~D3+D8+D9~~ → **D5/D6/D7 in parallel next** (genuinely separable) → D2 last.<br><br>**D3+D8+D9 (ReSync/AutoSync/AutoStop + slant tracking + replay/CorrectSlant, coupled) — done, 4 rounds** (2026-08-18; the historically-tricky "local-vs-absolute index" bug nest, CLAUDE.md §4). **This round genuinely reproduced the bug class it exists to catch** — worth reading in full if working near this field cluster again. Round 1 built the priority coordinate-space table across the 8 shared fields and found one real gap: `_rxBufferAnchorSample` (defines a raw-sample↔staging-buffer-local-index map) wasn't compensated when `TryAppendLine` rejected a line at RAM capacity. Round 1's OWN fix was itself wrong — it advanced the anchor on every rejection, based on a "count invariant" phrasing in the field's own (then-current) doc comment, which round 2 proved was never actually this field's contract: the anchor defines a FIXED coordinate map (unchanged between re-anchor events), not a running staged-content tally, and `PerformReplay`'s own `resumeDest` computation explicitly needs the live cursor's true position, not the staged extent. Round 2 caught this with a full worked numeric trace showing round 1's fix would silently re-stamp already-correct image rows with stale audio and leave the bottom of the image undrawn. Reverted the fix, rewrote the field's doc comment to state the coordinate-map definition as primary, and replaced the (also-wrong) regression test with one that asserts the actual consequence (`PerformReplay` resumes from the true live position, not the staged extent) rather than a bookkeeping proxy — independently verified mutation-sensitive by temporarily re-introducing the round-1 bug and confirming the new test fails with the exact predicted signature. Round 3 did a genuinely fresh, from-scratch re-derivation of the whole revert (not just re-reading round 2's summary) and found one more thing: a stale doc comment on the `RxBufferAnchorSampleForTests` test accessor still asserted the repudiated invariant, directly contradicting the field's own already-corrected comment — the exact resurrection vector for the original bug. Fixed (2-line doc edit) plus two test nits (a wrong "headroom" comment, and a one-sided assertion tightened to two-sided). Round 4 (clean round, soft cap reached at 4) independently re-derived the coordinate-map semantics AGAIN from scratch, rebuilt the full 13-quantity coordinate table, and reconfirmed the revert, the doc fixes, and the test's mutation-sensitivity in both directions — explicit yes, zero blockers/risks. Also fixed in this track: F2 (the automatic replay drain was the only one of 3 entry points missing the `!_slantCorrectionsDisabledForRestOfImage` gate its siblings have — added); F4 (`LineDecoded` had no CLAUDE.md §4 concurrency contract, unlike its sibling `StationIdDecoded` — added); F5 (`HasWriteFailed`'s doc comment claimed an Application-layer observability path that doesn't actually exist — corrected to state plainly it's not observable today). Full `tests/ScanlineStudio.Core.Sstv.Tests` suite: 945/945 passing (1 unrelated intentional skip) throughout. Explicitly deferred (not fixed, flagged in code): F3 (`TryCorrectSlant`'s entry gate uses cumulative line count, its internal scan uses local-only count — usually benign, not proven safe in all cases, no test); a rejected-line-then-later-shorter-accepted-line physical buffer discontinuity (same class as the already-deferred `DrainPendingSkip` mid-buffer-hole gap). Queued nits: one more (weaker, historical-narration-only) instance of the repudiated invariant phrasing; an undocumented-but-provably-harmless clamp asymmetry between the replay row loop and its own reconciliation step.<br><br>**D4 (main loop + image teardown, `TryProcessBuffer`/`EndOfImage`) — done, 4 rounds** (2026-08-18; real boundary ~2290-2745, drifted from earlier chunks' own comment growth). Round 1 found 3 blocking items, all real: (1) an undocumented `Math.Clamp` lower bound in the live per-line reader silently substituted the oldest retained sample for any trimmed-away index instead of letting `Rel()` throw as designed — fixed by dropping the lower bound (`Math.Min` only; the upper bound stays, load-bearing); (2) `EndOfImage` fires 1-2 transmission lines earlier than legacy's own `m_AY > SSTVSET.m_L` overshoot check — no pixel divergence, judged not worth matching (perturbs every back-to-back-transmission tolerance for zero benefit; the encoder footer this exposes to the next header search has no 1200Hz sync structure or 300ms+ leader to false-lock on) — resolved by correcting the doc comment to state the real divergence instead of falsely claiming a byte-exact port; (3) two real test-coverage gaps (the 0.5s dead-time constant and `applyDeadTime:false` were both only reachable indirectly, via tests whose own comments admit a wrong value would likely still pass) — fixed with a new `EndOfImageForTests` internal test-only wrapper (same "internal for testability" convention as D1) + two new tests pinning both values directly. Round 2 re-scanned fresh and caught a real bug in round 1's OWN fix: the corrected doc-comment's numbers were wrong (understated Martin M1's gap 3.2x, self-contradicted its own overshoot-count sentence for Scottie) — round 2 independently re-derived the correct figures from `SstvModeRegistry.cs`'s own `LineSegments` (Martin M1: 446.446ms×2=0.89s; Scottie DX: 1050.3ms×1=1.05s) and required the fix before the gate could pass; also caught that round 1's justification for keeping the upper clamp bound was itself factually wrong (not KSB-peek-ahead-related; `PixelSampleReader.ReadPeakPicked` already guards that case). Both fixed, doc-only, zero behavior change. Round 3 (1st clean round) and round 4 (2nd clean round) each independently re-derived every numeric claim from legacy source and the registry rather than trusting the prior round's summary — round 3 additionally spot-checked 2 more mode families (Robot 36, PD90) to confirm the overshoot framing generalizes; round 4 refined one bound estimate (Scottie DX's intra-line rounding error is the worst case in the registry, 0.53 samples not Martin's 0.22, still comfortably sub-pixel). Both explicit yes, zero blockers/risks. Full `tests/ScanlineStudio.Core.Sstv.Tests` suite: 944/944 passing (1 unrelated intentional skip). Queued nits (comment-wording/citation only, no behavior impact): `_nextLine >= ImageHeight` comment says "transmission lines" but counts bitmap rows; a legacy line-number citation points at the wrong (but equivalent) branch for two named example modes; several stale in-file line-number self-references from comment growth across rounds; "by construction" overstates an intra-line rounding bound that's actually bounded, not exact.<br><br>**D1 (cache/index substrate) — done, 3 rounds.** Round 1 (real boundary turned out to be lines 279-289 `Rel()` + 856-1179 the `*At` family, not the originally-guessed 44-1210) found 2 real risk findings, both about missing test coverage for real silent-failure paths (not wrong production logic): `Rel()`'s below-base throw guard (the dangerous failure class this file's own doc comment names is a *silent wrong answer*, not a throw) had zero direct test coverage since `Rel` was `private`; the narrow-mode `isNarrow` demod gate had no diagnostic or test, unlike its structurally-identical sibling H1/H2 bandpass gate (which got one after an earlier finding) — and round 1 flagged as an unverified assumption whether the demod cursor genuinely trails the lock anchor for narrow modes the way the sibling gate's fix relies on. Fixed: `Rel` bumped `private`→`internal` (matches this class's existing "internal for direct testability" convention) + new test `BufferTrimTests.Rel_Throws_WhenAbsoluteIndexIsBehindTheTrimWatermark`; new `FirstNarrowDemodIndex` diagnostic property (mirrors `FirstLockedBandpassIndex`) + new test `BandpassCacheChunkInvarianceTests.FirstNarrowDemodIndex_EqualsTheLockAnchor_ForANarrowMode`. Round 2 verified both fixes, independently re-derived from legacy `sstv.cpp`/`VisHeader.cs` constants that the narrow-gate premise genuinely holds (not just "the test happened to pass") — explicit yes, D1's 1st clean round. Round 3 (2nd required clean round) re-derived everything independently rather than trusting round 2's summary, re-verified the legacy-parity claims from scratch, and actually corrected round 2's own margin estimate upward (the narrow-gate cursor trails the anchor by ≥1 full retention window + `AnchorWarmupSamples`, not the ~570ms round 2 estimated) — explicit yes, zero blockers/risks. D1 closed. Full `tests/ScanlineStudio.Core.Sstv.Tests` suite: 942/942 passing (1 unrelated intentional skip). Queued nits: no "locked-UNTIL" bound on either gate (no observable effect, verified); `Rel` throw-test asserts on a message substring (brittle to reword, `Assert.Throws` alone already carries the guarantee); duplicated 30s-noise test setup (cosmetic); one newly-surfaced comment gap (port's pre-lock demod feed window vs. legacy's `if(m_Sync)`-gated feed is a deliberate, documented-elsewhere divergence that isn't cross-referenced at this call site) — none block, none require a production-code change. |
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

**Status**: **Batch 1 done** (2026-08-18), 4 rounds. Round 1 found 4 real
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
