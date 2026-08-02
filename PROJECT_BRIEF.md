# Project brief (resume point)

Scratch file for resuming after `/clear` — not a spec doc, delete or ignore once stale.

## What this repo is
Yoniq v2: cross-platform (.NET 8 + Avalonia) rewrite of YONIQ (MMSSTV fork). Specs in `spec/00`-`spec/15`.
Legacy source lives locally (gitignored) at `yoniq-old/YONIQ-main/` — read it directly, don't infer from memory.
Secondary reference QSSTV lives locally (gitignored) at `QSSTV-main/` — inspiration/cross-check only, never authoritative.
Full rules: `CLAUDE.md` (short, read it). Key ones: port legacy DSP exactly (no invention), golden-vector/round-trip
tests for every DSP change, small reviewable commits, ask before pushing to origin.

## Windows CI — fixed and closed out (2026-08-01), commit `8be35b7`

Root cause: `ilammy/msvc-dev-cmd@v1` set `Platform=x64` as a job-level env var; `Yoniq.sln` only has
"Any CPU" solution configs, so `dotnet restore` failed with MSB4126. Fixed with explicit
`/p:Platform="Any CPU"` on restore/build/test steps. Verified against two real consecutive CI runs, all
three legs green each time. Full writeup: `spec/14-roadmap.md`, search "Windows CI fix".
**All three CI legs (Windows/Linux/macOS) now green on `master` — nothing blocking.**

## Current task: Band 1 fixes, working one by one (task #6, in progress)

**Pre-Phase-2 gate: shortcut/simplification audit.** User's call: before running the milestone-audit
playbook's Phase 3 chain audit (see `docs/audit-playbook.md`) or moving to Phase 2, first inventory
every known DSP-in-pipeline simplification this port carries, triage/fix the important ones, THEN
capture more real golden-vector fixtures, THEN run Phase 3. Full writeup + 30-item table + priority
bands + patterns: `spec/14-roadmap.md`, search "Pre-Phase-2 gate" (results are near the very end).

**Band 1 = 4 items** (5th, Kaiser/Bessel filter design, dropped after confirming H1's attenuation is
20dB at the only reachable preset — no Kaiser/Bessel work needed). **3 of 4 DONE:**
1. **DONE** (commit `288d5d0`) — exception-swallowing bare `catch` in `MiniAudioCaptureSession`.
   `LastSubscriberException`/`SubscriberExceptionCount` added, plus a real second bug the auditor
   caught on its own initiative: one throwing subscriber used to starve every OTHER subscriber (and
   every later chunk) of delivery — fixed in the same change (per-handler `try/catch` via
   `GetInvocationList()`, not one catch around the whole multicast call).
2. **DONE** (commit `86e3af6`) — unbounded memory growth in the decoder's 5 sample buffers
   (~5.7GB/hr @44100Hz). Single `_bufferBase`+`Rel()` index-translation choke point (not ~25
   individually-rewritten sites), watermark computed from live cursors (different formula
   locked vs. pre-lock), amortized trimming. Real bug found DURING implementation (not caught by the
   plan-review): first working version's pre-lock watermark included `_consumedSamples`
   unconditionally, which never advances pre-lock except via `Commit()`/`EndOfImage()` — for a
   stream that never locks (the exact scenario this fix exists for), it stayed 0 forever and
   permanently blocked all trimming. A more ambitious re-anchoring fix was tried and reverted (didn't
   actually restore precision, added real race-reopening risk) in favor of a simpler skip-based fix
   with an identical practical outcome. Final code-level auditor review: "ready to commit," no
   blockers, 6 minor findings all addressed via documentation. Full suite 392/392, golden vectors 8/8
   unaffected.
3. **DONE** (commits `365d57b`, `765ba3c`) — chunk-timing sensitivity. Root cause was NOT AFC/Slant
   (both proven fully chunk-invariant) — a real race between two header-detection paths
   (`TryDecodeVisHeader`'s fixed-window path vs. `TryInterleavedHeaderScan`'s fallback), whose
   priority depended on call-boundary timing instead of absolute sample position. Empirically
   confirmed via temporary instrumentation (added and fully reverted same session), fixed with a
   one-shot `_fixedWindowExhausted` gate (an auditor plan-review caught the first draft's rolling-cap
   approach was wrong — drops stream tails, imposes needless permanent latency). Verified: decode is
   now provably deterministic regardless of chunking — exact pixel identity across chunk sizes
   {1, 500, 4096}, not just tolerance. Full suite 390/390, golden vectors 8/8 unaffected.
4. **NOT STARTED** — lock-dependent bandpass filter never switches (this port has never once run
   legacy's real locked-state filter, only the weaker pre-lock one). Last remaining Band-1 item.

Full per-item plan-review + implementation + code-review detail: `spec/14-roadmap.md`, search
"Band-1 item".

**Next after Band 1**: task #7 (capture ~5-6 new golden-vector fixtures for uncovered mode families:
Scottie S1, Robot72/R24, a PD/MP mode, RM8/RM12, a narrow MN/MC mode, AVT — several Band-3 items from
the 30-item audit are gated on these) → task #8 (Phase 3 chain audit).

**Correction (2026-08-01, still valid): "sync-search + AFC state machine" is DONE, not open work.** A
prior session's stale summary line in `spec/14-roadmap.md` Phase 1 (~line 48) called this "not yet
started"; it's actually fully ported (AFC/`AfcTracker`, Auto Slant/`SlantTracker`, the 7-piece
VIS/preamble-lock state machine, and piece 8's sync-anchor fix), golden-vector-tested, committed
(`23b025a`→`8f87146`). No known open DSP-correctness gap in Phase 1 beyond what the audit above finds.

## Other candidates (deprioritized behind the audit above, not urgent)

- **Phase 2 — Radio layer**: `IRadioController` reference implementation against a fake
  transport/protocol, then `rigctld` client mode ([[02-radio-layer]], [[04-rigctld]] in the roadmap).
- **Windows/macOS real-hardware audio verification** (`spec/14-roadmap.md` Phase 1, Audio 1b): the
  native-shim build now compiles in CI on both, but neither has been run against real/virtual hardware
  — needs a human on each OS, not agent-doable from this Linux sandbox.
- **Small license-audit items**: cty.dat (Clublog callsign-prefix dataset) audit before Phase 4 bundles
  it; confirm whether Chilkat/FastReport back a real legacy feature (needs the legacy binary, not just
  source). Both quick, low-risk, no dependencies.

## Completed work (full narratives in `spec/14-roadmap.md`, search "Piece N" — that's the durable log)
Before piece numbering started: AFC (`AfcTracker`, direct port of `CSSTVDEM::SyncFreq`), Auto Slant
(`SlantTracker`, clock-drift correction), and the full 7-piece VIS/preamble-lock state machine
(`SyncIntervalTracker`/`VisLockStateMachine`/`AvtTrainingLockStateMachine`) — commit `8dbe790` onward.

Piece 8 — **the actual root-caused fix for the Robot-36-at-11025Hz decode gap**: real golden-vector
capture from a working legacy install exposed a 5x-worse-than-synthetic gap; investigation found a
missing per-image sync-anchor re-correction (legacy's `SyncSSTV`/`m_wBgn` fold-and-argmax) — ported as
`SyncAnchorCorrector`, plus a real Scottie-family wraparound bug caught and fixed along the way.
Measured: Robot 36 68.06→9.95, Martin M1 11.78→2.40 average per-channel delta. Commits `23b025a`→`8f87146`.

Pieces 9-13 (all committed, all green): real VIS-bit dual-envelope tone race (piece 9), `GetPictureLevel`
peak-picking (piece 10), horizontal pixel-pitch trim (piece 11), RM8/RM12 RX gain correction (piece 12),
`DecodeFSK`'s real 5-phase FSK state machine (piece 13).

Recent filter-chain work (pieces 14/15/B + noise harness), just finished:
- **Piece 14**: Hilbert demodulator (`CHILL`) replaces PLL as the main picture demodulator (legacy's
  real default). AVT training lock got its own dedicated PLL instance (legacy always uses PLL for AVT
  regardless of picture-demod type). Commit `aeccfcc`.
- **Piece 15**: legacy's always-on 2-tap moving-average pre-filter (`d=(s+m_ad)*0.5`). Commit `14b4144`.
- **Noise-robustness harness** (`NoiseRobustnessTests.cs`) — new test infra (not a legacy port),
  measures decode quality across a calibrated-SNR noise sweep, reports a "noise floor." Commit `14b4144`.
- **Piece B**: `SearchBandpassFilter` — legacy's `H2`/"search" pre-AGC bandpass filter, continuous scope
  (no lock-state gating). Streaming delay-line design (a `Func`-based first draft regressed the full
  suite to 12min; fixed). **Measured, real improvement**: noise floor martin-m1 9.0dB→3.0dB, robot-36
  16.0dB→9.0dB. Commit `c3b9f46`.

All committed and pushed through Piece B (`c3b9f46`), 387/387 tests passing on all three CI legs
(Windows/Linux/macOS all green — Windows CI fixed 2026-08-01, see above).

## Working methodology (established across this project)
- Legacy is ground truth — verify against `yoniq-old/YONIQ-main/` source directly, no assumptions.
- Test early, test often — build + run tests after each sub-step, not just at the end.
- Get an Opus/`auditor` plan-review before writing code for non-trivial DSP ports (CLAUDE.md §7) — ask
  the auditor directly "is this ready to build now?" each round; soft 3-round backstop, then loop in the
  user. Restate the ADHD/scope rule and the ported-behavior framing in every subagent prompt.
- Independently re-verify Opus/agent findings against actual source before trusting/acting on them.
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
