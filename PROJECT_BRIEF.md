# Project brief (resume point)

Scratch file for resuming after `/clear` — not a spec doc, delete or ignore once stale.

## What this repo is
Yoniq v2: cross-platform (.NET 8 + Avalonia) rewrite of YONIQ (MMSSTV fork). Specs in `spec/00`-`spec/15`.
Legacy source lives locally (gitignored) at `yoniq-old/YONIQ-main/` — read it directly, don't infer from memory.
Secondary reference QSSTV lives locally (gitignored) at `QSSTV-main/` — inspiration/cross-check only, never authoritative.
Full rules: `CLAUDE.md` (short, read it). Key ones: port legacy DSP exactly (no invention), golden-vector/round-trip
tests for every DSP change, small reviewable commits, ask before pushing to origin.

## Current task: Band 1 fixes, one by one — STOPPED after 3 of 4, resume with item 4

Session paused here deliberately (hit ~90% of session budget) rather than start item 4 rushed —
everything below is committed and pushed; `master` is a clean, safe resume point.

**Pre-Phase-2 gate: shortcut/simplification audit.** User's call: before running the milestone-audit
playbook's Phase 3 chain audit (see `docs/audit-playbook.md`) or moving to Phase 2, first inventory
every known DSP-in-pipeline simplification this port carries, triage/fix the important ones, THEN
capture more real golden-vector fixtures, THEN run Phase 3. Full writeup + 30-item table + priority
bands + patterns: `spec/14-roadmap.md`, search "Pre-Phase-2 gate".

**Band 1 = 4 items** (5th, Kaiser/Bessel filter design, dropped after confirming H1's attenuation is
20dB at the only reachable preset — no Kaiser/Bessel work needed). **3 of 4 done:**
1. **DONE** (commit `288d5d0`) — exception-swallowing bare `catch` in `MiniAudioCaptureSession`.
   `LastSubscriberException`/`SubscriberExceptionCount` added, plus a real second bug the auditor
   caught on its own initiative: one throwing subscriber used to starve every OTHER subscriber (and
   every later chunk) of delivery — fixed in the same change.
2. **DONE** (commit `86e3af6`) — unbounded memory growth in the decoder's 5 sample buffers
   (~5.7GB/hr @44100Hz). Single `_bufferBase`+`Rel()` index-translation choke point, watermark
   computed from live cursors. Real bug found DURING implementation (not caught by the plan-review):
   first working version's pre-lock watermark included `_consumedSamples` unconditionally, which
   never advances pre-lock except via `Commit()`/`EndOfImage()` — for a stream that never locks (the
   exact scenario this fix exists for), it stayed 0 forever and permanently blocked all trimming. A
   more ambitious re-anchoring fix was tried and reverted (didn't actually restore precision, added
   real race-reopening risk) in favor of a simpler skip-based fix with an identical practical outcome.
   Final code-level auditor review: "ready to commit," no blockers.
3. **DONE** (commits `365d57b`, `765ba3c`) — chunk-timing sensitivity. Root cause was NOT AFC/Slant
   (both proven fully chunk-invariant) — a real race between two header-detection paths
   (`TryDecodeVisHeader`'s fixed-window path vs. `TryInterleavedHeaderScan`'s fallback), whose
   priority depended on call-boundary timing instead of absolute sample position. Fixed with a
   one-shot `_fixedWindowExhausted` gate (an auditor plan-review caught the first draft's rolling-cap
   approach was wrong). Verified: decode is now provably deterministic regardless of chunking — exact
   pixel identity across chunk sizes {1, 500, 4096}, not just tolerance.
4. **NOT STARTED — next task.** Lock-dependent bandpass filter never switches: this port has never
   once run legacy's real locked-state filter (`HBPF`/`H1`), only the weaker pre-lock/search one
   (`HBPFS`/`H2`, `SearchBandpassFilter`). Expected to be a real DSP change, not a quick patch —
   likely needs: a second filter instance for H1 (confirmed no Kaiser/Bessel branch needed at the
   Wide preset — H1's attenuation is 20dB, same as H2), a decision on how the switch interacts with
   `BandpassFilteredSampleAt`'s lazy forward-fill cache (the lock transition point matters — samples
   before lock need H2, after need H1, but computation is lazy/index-based, not necessarily in lock-
   state order), filter-state continuity (H1 needs to be "warmed up" by the time it's actually used,
   same category of issue as the anchor-correction/AVT warm-ups elsewhere in this file), and the
   auditor flagged a real group-delay-skew risk between the picture-demod and sync/envelope paths if
   the filter differs between them — needs explicit discussion, not just implementation. Follow the
   normal process: plan → auditor plan-review → implement → test incrementally → full suite + golden
   vectors → final code-level auditor review, matching items 2/3's own pattern.

Full per-item plan-review + implementation + code-review detail: `spec/14-roadmap.md`, search
"Band-1 item".

**Test count as of this entry**: 392/392 `Yoniq.Core.Sstv.Tests`, solution-wide build clean,
golden-vector tests (real captured legacy audio) 8/8 unaffected throughout.

**Next after Band 1**: task #7 (capture ~5-6 new golden-vector fixtures for uncovered mode families:
Scottie S1, Robot72/R24, a PD/MP mode, RM8/RM12, a narrow MN/MC mode, AVT — several Band-3 items from
the 30-item audit are gated on these) → task #8 (Phase 3 chain audit).

## Other candidates (deprioritized behind Band 1, not urgent)

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
pre-AGC bandpass filter, continuous scope, no lock-state gating (piece B, commit `c3b9f46`) — **this
is the exact filter Band-1 item 4 above now needs to extend with the locked-state H1 counterpart.**
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
  review before it) is worth doing before calling it done — caught real issues in Band-1 items 2/3
  that plan-review alone couldn't (bugs only visible once the code actually exists).
- Independently re-verify Opus/agent findings against actual source before trusting/acting on them.
- When investigating a suspected bug, confirm the mechanism empirically (temporary instrumentation,
  added and fully reverted, `git status`/`git diff` confirmed clean) before designing a fix — don't
  fix blind. Used successfully for Band-1 item 3's root cause and item 2's mid-implementation bug.
- If a fix's design doesn't demonstrably achieve what it's meant to (measured via a real test, not
  assumed), revert to the simpler alternative rather than keep the more complex one "just in case" —
  done for item 2's re-anchoring attempt.
- Document steps + results durably in `spec/14-roadmap.md` as you go; keep this file trimmed to
  "what's needed to resume," not a running history (that's the roadmap's job).
- Cloud-scheduled routines (RemoteTrigger/`/schedule`) run in an isolated environment with a fresh git
  checkout — no access to `yoniq-old/YONIQ-main/` or `QSSTV-main/` (both gitignored, local-only).

## Build/test commands
```
dotnet build src/Yoniq.Core.Sstv -c Debug
dotnet test tests/Yoniq.Core.Sstv.Tests -c Debug
dotnet test tests/Yoniq.Core.Sstv.Tests -c Debug --filter "FullyQualifiedName~<substring>"
```
