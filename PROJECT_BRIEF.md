# Project brief (resume point)

Scratch file for resuming after `/clear` — not a spec doc, delete or ignore once stale.

## What this repo is
Yoniq v2: cross-platform (.NET 8 + Avalonia) rewrite of YONIQ (MMSSTV fork). Specs in `spec/00`-`spec/15`.
Legacy source lives locally (gitignored) at `yoniq-old/YONIQ-main/` — read it directly, don't infer from memory.
Secondary reference QSSTV lives locally (gitignored) at `QSSTV-main/` — inspiration/cross-check only, never authoritative.
Full rules: `CLAUDE.md` (short, read it). Key ones: port legacy DSP exactly (no invention), golden-vector/round-trip
tests for every DSP change, small reviewable commits, ask before pushing to origin.

## Previous work (piece 8, DONE, all committed)
Robot-36-at-11025Hz sync-anchor fix (`TMmsstv::SyncSSTV` port), Scottie wraparound bug, a
`_visLockProcessedUpTo` catch-up gap, and the Robot 36 test tolerance — commits `b9e3512`, `2444343`,
`8f87146`. Full history in `spec/14-roadmap.md` (search "piece 8"). Not the current task — background only.

## Current task: Piece 9 — VIS-bit decode / PLL bandwidth mismatch
Full narrative (diagnosis, both plan-review rounds, all bugs found, both code-review fixes) is in
`spec/14-roadmap.md`, search "Piece 9" — that's the authoritative log; this is just a pointer + status.

**Steps 1-2 DONE, tested, code-reviewed, all fixes applied. Not yet committed to git (uncommitted as of
this brief — see `git status`).** Steps 3-4 NOT STARTED.

- Diagnosis: `AnalogFmSstvDecoder` used to decide VIS header bits by reading the shared PLL's
  demodulated-frequency stream — a proxy that only worked by coincidence. Legacy's real mechanism
  (`sstv.cpp` case 2/9) is a completely separate tone race between two dedicated 1080Hz/1320Hz envelope
  detectors. Steps 1-2 replaced the proxy with the real mechanism.
- New file: `src/Yoniq.Core.Sstv/VisBitDecision.cs` (the shared stateless decision predicate).
- Modified: `src/Yoniq.Core.Sstv/VisLockStateMachine.cs` (refactored to use the shared predicate; its own
  anchor-rounding bug also fixed, see below), `src/Yoniq.Core.Sstv/AnalogFmSstvDecoder.cs` (new
  `TryDecodeVisDataBits` method replaces the old PLL-average bit reads in `TryDecodeVisHeader`).
- New tests: `tests/Yoniq.Core.Sstv.Tests/VisToneRaceHeaderTests.cs`.
- **Four real bugs found and fixed purely by running the full suite after each change** (none
  anticipated by either of the two plan-review rounds or the code-level review) — full detail in the
  roadmap entry, one-line summaries: (1) false-positive lock on real mic noise (missing trigger
  precondition), (2) false-reject of genuine signal (fixed-timing assumption too strict for real filter
  settling lag — fixed via dynamic trigger search), (3) last data bit silently defaulting to 0 (two
  independently-rounded sample bounds diverging — fixed by looping on bit count instead), (4) the
  dynamic-search fix from #2, once made resumable-on-reject (a code-review finding, see below), had no
  upper bound and could scan into unrelated content on multi-transmission streams — fixed by
  reintroducing a local search ceiling.
- Got a full `auditor` code-level review after steps 1-2 landed (not just the two plan-review rounds).
  Verdict "equivalent-with-risks": core mechanism confirmed faithful against `sstv.cpp` directly
  (frequencies, bandwidths, smoothing, gate, decision, timing arithmetic down to the sample). Two real
  risks found and fixed (Risk 1: reject was permanent instead of resumable, a real regression for AVT
  specifically since `VisLockStateMachine` never reports AVT and has no fallback for it; Risk 2: the
  same rounding-mismatch bug class as #3 above, also present in `VisLockStateMachine`'s own anchor
  calc). Fixing Risk 1 is what surfaced bug #4 above. Also added the auditor's recommended pinning test
  (`VisLockStateMachine` and the fixed-window path agree on the same synthetic header).
- **Logged, deliberately NOT fixed**: a real performance concern the auditor flagged — the new
  `TryDecodeVisDataBits` rebuilds 4 envelope detectors and replays from `headerStart` on every single
  `PushSamples` call while unlocked, no persistent cursor (unlike every other detector in this file).
  Bounded now (not unbounded, since bug #4's fix), but still real, repeated, from-scratch work. Full
  writeup + what a future fix would look like is in the roadmap entry — don't forget this exists.
- Full suite: **262/262 passing** as of this brief.

### Steps 3-4, not started
3. Narrow `PllFmDemodulator`'s tracked band (`AnalogFmSstvDecoder.cs:38-39`) from 1100-2300Hz to legacy's
   real 1500-2300Hz (image-decode only) — now safe since no VIS-bit decision path depends on the PLL
   anymore. Re-verification scope (already detailed in the roadmap entry): re-run the full per-mode
   round-trip delta table including MN/MC, re-measure BOTH `GoldenVectorTests` fixtures (not just
   synthetic round-trips), update `AvtTrainingLockStateMachineTests.cs:124`'s hardcoded old PLL config.
4. Fix two stale doc comments: `AnalogFmSstvDecoder.cs:34-39` and `AvtTrainingLockStateMachine.cs:9-12`
   (exact replacement text already drafted in the roadmap entry).

### Before starting steps 3-4
- **Commit steps 1-2 first** (currently uncommitted) — ask before pushing, per standing instruction.
- Steps 3-4 need real legacy-source re-verification during re-measurement (golden-vector deltas can move
  either direction) — this is NOT purely mechanical, don't treat it as a quick pass.

## Other still-open items from the original piece-8 investigation (not started, for context/prioritization)
From `spec/14-roadmap.md`'s "Secondary, smaller, independently-source-verified divergences" list:
2. `GetPictureLevel` (luma) peak-picks the brighter of two samples `m_KSB` apart (`Main.cpp:4057-4071`),
   not a bare dereference — `RobotScanlineDecoder` uses a bare read. Real behavioral gap, smaller scope
   than piece 9.
3. Horizontal pixel pitch should use legacy's `m_KSS` (`m_KS - m_KS/240` for Robot 36, `sstv.cpp:1157`),
   not `m_KS` — ~0.42% horizontal scale error.
5. Legacy's shipped **default** demodulator is actually the Hilbert path (`CHILL`), not PLL at all —
   this port only has PLL. Bigger, separately-scoped question, not attempted here.
- New, from piece 9's own investigation: `TryDecodeNarrowModeHeader`'s FSK bit decode
  (`AnalogFmSstvDecoder.cs:1072`) has the identical PLL-proxy-instead-of-real-detector shape as piece
  9's original bug — survives piece 9 untouched, logged for later.

## Working methodology (established across this project, apply here too)
- Legacy is ground truth — verify against `yoniq-old/YONIQ-main/` source directly, no assumptions.
- Test early, test often — build + run tests after each sub-step, not just at the end. Piece 9 is a live
  demonstration of why: 4 real bugs, all caught this way, none anticipated by planning/review alone.
- Get an Opus/`auditor` plan-review before writing code for non-trivial DSP ports (CLAUDE.md §7) — up to
  3 rounds on a plan; user can elect to stop early and get a code-level review instead once implemented.
  Restate the ADHD/scope rule and the ported-behavior framing in every subagent prompt.
- Independently re-verify Opus/agent findings against actual source before trusting/acting on them.
- Document steps + results durably in `spec/14-roadmap.md` as you go, so this file (and that log) are
  enough to resume cold.
- Cloud-scheduled routines (RemoteTrigger/`/schedule`) run in an isolated environment with a fresh git
  checkout — no access to `yoniq-old/YONIQ-main/` or `QSSTV-main/` (both gitignored, local-only). Not
  usable for anything needing fresh legacy-source verification; only for mechanical work already fully
  specified in committed docs. User declined to set one up for this piece (2026-07-31).

## Build/test commands
```
dotnet build src/Yoniq.Core.Sstv -c Debug
dotnet test tests/Yoniq.Core.Sstv.Tests -c Debug
dotnet test tests/Yoniq.Core.Sstv.Tests -c Debug --filter "FullyQualifiedName~<substring>"
```

## Housekeeping note
Untracked, unexplained files present in the working tree — not created by this session's work, left
alone, investigate before touching: `CLAUDE.old`, `docs/audit-playbook.md`. `.gitignore` and
`CLAUDE.md` show as modified in git status; not yet reviewed/explained this session either.
