# Project brief (resume point)

Scratch file for resuming after `/clear` — not a spec doc, delete or ignore once stale.

## What this repo is
Yoniq v2: cross-platform (.NET 8 + Avalonia) rewrite of YONIQ (MMSSTV fork). Specs in `spec/00`-`spec/15`.
Legacy source lives locally (gitignored) at `yoniq-old/YONIQ-main/` — read it directly, don't infer from memory.
Secondary reference QSSTV lives locally (gitignored) at `QSSTV-main/` — inspiration/cross-check only, never authoritative.
Full rules: `CLAUDE.md` (short, read it). Key ones: port legacy DSP exactly (no invention), golden-vector/round-trip
tests for every DSP change, small reviewable commits, ask before pushing to origin.

## DO NOT PUSH until the user has checked GitHub Actions minutes

As of 2026-08-02 the user is at 1,806/2,000 monthly Actions minutes (90%) — every `git push` triggers a
full 3-leg CI run (~10-13 min). A cron job resuming this session tonight (fires 23:45 CEST) is instructed
to commit locally but NOT push, for exactly this reason. If you're resuming this work and see local
commits ahead of `origin/master`, do not push them without checking with the user first — they intend to
review Actions usage and push everything in one batch themselves. Check `git log origin/master..HEAD` to
see what's local-only.

## Resume here (2026-08-02, session paused at 92% budget) — S14, plan verified, NOT implemented yet

**Do this first, before anything else**: implement Band-2 item S14. The plan below is fully verified
by an auditor plan-review (round done, no code written yet) — go straight to implementing, no need to
re-derive or re-verify the plan itself. Full spec: `spec/14-roadmap.md`, search "Band-2 item S14" (has
the complete, ready-to-execute detail — read that section, not just this summary, before coding).

**The fix, in short**: `TryDecodeNarrowModeHeader`'s `markDetector`/`spaceDetector` (~line 1774 as of
last commit `b95366a`) are cold-started fresh every call, same shape S5 already fixed for d11/d12/d19.
1. Mark detector (1900Hz): **reuse `D19At`** (S5's existing persistent cache) — do NOT add a third
   1900Hz instance. Legacy's mark tone literally IS `d19`, same object. Requires updating `D19At`'s
   `TrimBuffers` exclusion comment to name both readers (still correct, just needs the citation fixed).
2. Space detector (2100Hz, `VisHeader.NarrowSpaceFrequencyHz`): genuinely new — add a persistent field +
   `FskSpaceAt(int index)` lazy forward-fill cache + cursor, mirroring `D11At`/`D12At`/`D19At` exactly,
   plus its own `TrimBuffers` catch-up + `RemoveRange` + diagnostic entry.
3. `NarrowFskHeaderDecoder`'s own bit-accumulation state machine stays fresh-per-call — confirmed
   correctly out of scope (same already-accepted shape as `TryDecodeVisDataBits`'s own untouched search
   logic in S5).
4. The "anchor-precision half" of S14 (fixed-nominal-duration commit point) is **already closed** —
   verified against `Main.cpp:7422-7424` (TX places image data at a fixed offset, not RX's lock sample).
   Nothing to do there.

Auditor's own sizing: **small, smaller than S5** — one new detector+cache+cursor, one catch-up, one
`RemoveRange`, one diagnostic extension, two comment updates. Comfortably a single session.

**Normal process from here**: implement → full suite + golden vectors → final code-level auditor review
→ commit → push → update `spec/14-roadmap.md` + this file. Then continue Band 2: S6 next (needs its OWN
fresh legacy read, `HilbertFmDemodulator.SetWidth` changes tap count/lag unlike H1/H2's constant-tap
swap — don't reuse 4b reasoning), then S15 last (coupled to Band 1's `TrimBuffers` pre-lock watermark).

## Current status: Band 1 DONE (all 4 items); Band 2 in progress (2 of 5 done)

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

**Band 2 (should-fix-during-Phase-2-bring-up) — 2 of 5 DONE, order: S5 → S16 → S14 → S6 → S15.**
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
- **S14 NEXT, plan verified, not yet implemented** (session paused here at 92% budget) — see the
  "Resume here" section at the top of this file for the full ready-to-execute plan.
- **S6, S15 not started.** S6 needs its OWN fresh legacy read before reusing any 4b reasoning (Hilbert's
  `SetWidth` changes tap count/lag, unlike H1/H2's constant-tap swap — a flagged trap, not yet hit).
  S15 is coupled to Band 1's `TrimBuffers` pre-lock watermark (`_fixedWindowExhausted`) — do LAST,
  re-verify that watermark as part of it, per the auditor's own explicit warning.

**Test count**: 417/417 `Yoniq.Core.Sstv.Tests`, solution-wide build clean, golden-vector tests
unaffected throughout, noise-robustness tests unaffected (existing tolerance).

Full per-item plan-review + implementation + code-review detail: `spec/14-roadmap.md`, search
"Band-1 item" or "Band-2 item".

## Next up

1. **S14** (Band 2, next in order) — plan already verified (see "Resume here" at top), go straight to
   implementing: implement → full suite + golden vectors → auditor code-level review → commit → push.
2. **Task #7 — capture ~5-6 new real golden-vector fixtures** from the legacy binary, covering mode
   families the existing two fixtures (Martin M1, Robot 36) don't exercise: Scottie S1 (mid-line sync —
   the exact family that already produced one real synthetic-test-passes-while-wrong incident,
   `CLAUDE.md` §4), Robot 72 or R24, a PD/MP mode, RM8 or RM12, a narrow MN/MC mode, AVT. Bottlenecked
   on the user's time with the real legacy Windows binary, not on dev work — can start any time,
   independent of Band 2's own progress.
3. **Task #8 — Phase 3 chain/integration audit** (milestone-audit playbook, `docs/audit-playbook.md`) —
   skip Phase 1/2, units are already individually verified to an unusual degree. Should follow #7, not
   precede it, so the audit runs against the widest available real-audio coverage.
4. Band 3/4/5 items from the 30-item DSP-simplification inventory (S1-S30) are cataloged in
   `spec/14-roadmap.md` but not yet scheduled — several Band-3 items are gated on task #7's new
   fixtures (mode-family gaps get fixed when measured, not reasoned, per the auditor's own
   recommendation).

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
