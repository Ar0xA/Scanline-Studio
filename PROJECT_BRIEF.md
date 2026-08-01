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

## Next task: not yet chosen — candidates below, pick one to start a session on

**Correction (2026-08-01): "sync-search + AFC state machine" is DONE, not a candidate.** A prior
session's summary line in `spec/14-roadmap.md` Phase 1 (~line 48) called this "not yet started" and
that stale line got quoted here and to the user before the rest of the roadmap (below that line) was
read — AFC (`AfcTracker`), Auto Slant (`SlantTracker`), the full 7-piece VIS/preamble-lock state
machine, and the actual root cause of the residual decode gap (a missing per-image sync-anchor
re-correction, "piece 8"/`SyncAnchorCorrector`) are all ported, tested against real golden-vector
legacy audio, and committed (`23b025a`→`8f87146`, predates piece 9). Both roadmap and this file are now
corrected. There is no known open DSP-correctness gap in Phase 1.

- **Phase 2 — Radio layer**: `IRadioController` reference implementation against a fake
  transport/protocol, then `rigctld` client mode ([[02-radio-layer]], [[04-rigctld]] in the roadmap).
  Natural next phase now that Phase 1's DSP/audio core is essentially complete (43/43 modes, filter
  chain, Windows CI).
- **Real TX/WebSDR recapture** (backburnered 2026-08-01): would give a real golden-vector fixture
  beyond the synthetic noise-injection harness. Not urgent — Piece B was already measured against the
  synthetic harness instead.
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
