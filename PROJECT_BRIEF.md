# Project brief (resume point)

Scratch file for resuming after `/clear` — not a spec doc, delete or ignore once stale.

## What this repo is
Yoniq v2: cross-platform (.NET 8 + Avalonia) rewrite of YONIQ (MMSSTV fork). Specs in `spec/00`-`spec/15`.
Legacy source lives locally (gitignored) at `yoniq-old/YONIQ-main/` — read it directly, don't infer from memory.
Secondary reference QSSTV lives locally (gitignored) at `QSSTV-main/` — inspiration/cross-check only, never authoritative.
Full rules: `CLAUDE.md` (short, read it). Key ones: port legacy DSP exactly (no invention), golden-vector/round-trip
tests for every DSP change, small reviewable commits, ask before pushing to origin.

## Next task: fix Windows CI (`windows-latest` failing since Engine 0-6, 2026-07-30)

**Root cause found (2026-08-01), not yet fixed.** `.github/workflows/ci.yml`'s Windows leg runs
`ilammy/msvc-dev-cmd@v1` (added for `Yoniq.Core.Audio.MiniAudio`'s `BuildNativeShimWindows`/`cl.exe`
target) BEFORE `dotnet restore Yoniq.sln`, same job. That action sets `Platform=x64` as a job-level env
var (confirmed in the failing run's own env dump). MSBuild/`dotnet restore` implicitly reads ambient
`Platform`/`Configuration` env vars as default property values — `Yoniq.sln` only has an "Any CPU"
solution configuration (no "Debug|x64"), so restore fails:
`error MSB4126: The specified solution configuration "Debug|x64" is invalid.`
Build/Test steps never run — every Windows failure since Engine 0-6 shows this identical error at the
Restore step (checked via `gh run view <id> --log-failed` across several runs), so this is very likely
the sole root cause, not one of several.

**Likely fix, not yet applied or verified — pick the cleanest, then confirm against a real CI run
(`gh run view <id> --log-failed`), don't just eyeball the YAML:**
(a) pass `/p:Platform="Any CPU"` explicitly to the `dotnet restore`/`build`/`test` steps, overriding the
ambient env var; or
(b) unset `Platform` (and check `Configuration`) between the msvc-dev-cmd step and restore; or
(c) scope `ilammy/msvc-dev-cmd` down so it only wraps the native-shim build target, not the whole job.

Useful commands: `gh run list --branch master --limit 15`, `gh run view <id>`, `gh run view <id> --log-failed`.

## Backburnered (not blocking, come back to later)
- **Real TX/WebSDR recapture** for noise-robustness evidence (would double as a new golden-vector
  fixture) — deprioritized 2026-08-01 in favor of the Windows CI fix. No longer urgent: Piece B already
  shipped and was measured against the synthetic noise-injection harness instead (see below).

## Completed work (full narratives in `spec/14-roadmap.md`, search "Piece N" — that's the durable log)
Pieces 8-13 (all committed, all green): Robot-36/Scottie sync fixes (piece 8), real VIS-bit dual-envelope
tone race (piece 9), `GetPictureLevel` peak-picking (piece 10), horizontal pixel-pitch trim (piece 11),
RM8/RM12 RX gain correction (piece 12), `DecodeFSK`'s real 5-phase FSK state machine (piece 13).

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

All committed and pushed through Piece B (`c3b9f46`), 387/387 tests passing on Linux/macOS CI legs
(Windows leg failing — see "Next task" above).

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
