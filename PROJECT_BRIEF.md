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

## Piece 9 — VIS-bit decode / PLL bandwidth mismatch — COMPLETE (steps 1-4)
Full narrative (diagnosis, both plan-review rounds, all bugs found, code-review round, re-verification
numbers) is in `spec/14-roadmap.md`, search "Piece 9" — that's the authoritative log; this is just a
pointer + status. Steps 1-2 committed/pushed (`d448586`). **Steps 3-4 done this session, not yet
committed as of this brief — see `git status`.**

- Diagnosis: `AnalogFmSstvDecoder` used to decide VIS header bits by reading the shared PLL's
  demodulated-frequency stream — a proxy that only worked by coincidence. Legacy's real mechanism
  (`sstv.cpp` case 2/9) is a completely separate tone race between two dedicated 1080Hz/1320Hz envelope
  detectors. Steps 1-2 replaced the proxy with the real mechanism; step 3 then narrowed the PLL itself
  to legacy's real 1500-2300Hz image-decode band, now safe since nothing reads VIS bits off it anymore.
- New file: `src/Yoniq.Core.Sstv/VisBitDecision.cs` (the shared stateless decision predicate).
- Modified: `src/Yoniq.Core.Sstv/VisLockStateMachine.cs`, `src/Yoniq.Core.Sstv/AnalogFmSstvDecoder.cs`
  (new `TryDecodeVisDataBits` method, narrowed `DemodulatorLowHz`/`DemodulatorHighHz`),
  `src/Yoniq.Core.Sstv/AvtTrainingLockStateMachine.cs` (doc comment only, re-measured numbers).
- New/modified tests: `tests/Yoniq.Core.Sstv.Tests/VisToneRaceHeaderTests.cs` (new),
  `AvtTrainingLockStateMachineTests.cs` (hardcoded old PLL config updated),
  `GoldenVectorTests.cs` (robot-36 tolerance tightened 75.0→25.0 with updated reasoning).
- **Four real bugs found and fixed purely by running the full suite after each change during steps 1-2**
  (none anticipated by either plan-review round or the code-level review) — full detail in the roadmap
  entry, one-line summaries: (1) false-positive lock on real mic noise (missing trigger precondition),
  (2) false-reject of genuine signal (fixed-timing assumption too strict for real filter settling lag —
  fixed via dynamic trigger search), (3) last data bit silently defaulting to 0 (two independently-
  rounded sample bounds diverging — fixed by looping on bit count instead), (4) the dynamic-search fix
  from #2, once made resumable-on-reject (a code-review finding), had no upper bound and could scan into
  unrelated content on multi-transmission streams — fixed by reintroducing a local search ceiling.
- Got a full `auditor` code-level review after steps 1-2 landed. Verdict "equivalent-with-risks": core
  mechanism confirmed faithful against `sstv.cpp` directly (frequencies, bandwidths, smoothing, gate,
  decision, timing arithmetic down to the sample). Two real risks found and fixed (Risk 1: reject was
  permanent instead of resumable, a real regression for AVT specifically since `VisLockStateMachine`
  never reports AVT and has no fallback for it — fixing this surfaced bug #4 above; Risk 2: the same
  rounding-mismatch bug class as #3, also present in `VisLockStateMachine`'s own anchor calc). Also
  added the auditor's recommended pinning test (`VisLockStateMachine` and the fixed-window path agree
  on the same synthetic header).
- **Step 3 re-verification result: unambiguously positive, no regressions anywhere.** Measured every
  mode's round-trip delta before/after narrowing (temporary diagnostic, not a permanent test) — EVERY
  single mode improved, none regressed (MN/MC family included, per scope). Both `GoldenVectorTests`
  fixtures re-measured against real captured audio: martin-m1 11.78→1.22, robot-36 **68.06→16.995** (a
  ~4x improvement) — this closes out a divergence `GoldenVectorTests.cs`'s own comment had flagged as
  needing follow-up ("~5x-worse real-capture result than synthetic self-round-trip"); the PLL bandwidth
  mismatch this whole piece exists to fix WAS that investigation's answer. Robot-36's golden-vector
  tolerance tightened 75.0→25.0 accordingly (was "not meaningfully discriminating" per its own old
  comment, now is). AVT lock margin re-measured at the new band too (steady-state ripple ~9.0Hz/2.3Hz,
  ~100Hz margin to threshold either way) — full training-sequence test still locks correctly end to end.
- **Logged, deliberately NOT fixed**: a real performance concern the auditor flagged — the new
  `TryDecodeVisDataBits` rebuilds 4 envelope detectors and replays from `headerStart` on every single
  `PushSamples` call while unlocked, no persistent cursor (unlike every other detector in this file).
  Bounded (not unbounded, since bug #4's fix), but still real, repeated, from-scratch work. Full writeup
  + what a future fix would look like is in the roadmap entry — don't forget this exists.
- Full suite: **262/262 passing** as of this brief.

### Before committing steps 3-4
- Review the diff (`AnalogFmSstvDecoder.cs`, `AvtTrainingLockStateMachine.cs`,
  `AvtTrainingLockStateMachineTests.cs`, `GoldenVectorTests.cs`, `spec/14-roadmap.md`) — ask before
  pushing, per standing instruction.

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
