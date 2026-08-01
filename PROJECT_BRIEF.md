# Project brief (resume point)

Scratch file for resuming after `/clear` — not a spec doc, delete or ignore once stale.

## What this repo is
Yoniq v2: cross-platform (.NET 8 + Avalonia) rewrite of YONIQ (MMSSTV fork). Specs in `spec/00`-`spec/15`.
Legacy source lives locally (gitignored) at `yoniq-old/YONIQ-main/` — read it directly, don't infer from memory.
Secondary reference QSSTV lives locally (gitignored) at `QSSTV-main/` — inspiration/cross-check only, never authoritative.
Full rules: `CLAUDE.md` (short, read it). Key ones: port legacy DSP exactly (no invention), golden-vector/round-trip
tests for every DSP change, small reviewable commits, ask before pushing to origin.

## Completed work (full narratives in `spec/14-roadmap.md`, search "Piece N" — that's the durable log)
- **Piece 8**: Robot-36-at-11025Hz sync-anchor fix, Scottie wraparound bug, visLock catch-up gap. Commits `b9e3512`, `2444343`, `8f87146`.
- **Piece 9**: VIS-bit decode replaced a PLL-stream proxy with legacy's real dual-envelope tone race; PLL narrowed to the real 1500-2300Hz image band. Commits `d448586`, `55b8cf5`.
- **Piece 10**: `GetPictureLevel` peak-picking ported across all 5 scanline decoders (`SstvModeRegistry.GetPeakPickParameters`/`PixelSampleReader`). Commit `ee74bfd`.
- **Piece 11**: `m_KSS`/`m_KS2S` horizontal pixel-pitch trim (`GetPixelPitchTrimFactor`). Commit `be938d4`.
- **Piece 12**: RM8/RM12 RX gain correction, bypassing `YCbCr.ToRgb` to match legacy's direct-gray-write branch. Commit `1bbcfc7`.
- **Piece 13**: `TryDecodeNarrowModeHeader` FSK bit decode replaced a PLL-stream-average proxy with a
  literal port of `CSSTVDEM::DecodeFSK`'s real 5-phase state machine (new `NarrowFskHeaderDecoder`
  class + 13 isolated unit tests). Two rounds of auditor plan-review, both caught real issues before
  code was written. Commit `469e44f`.
- **Piece 14**: Hilbert demodulator (`CHILL`) port — new `HilbertFmDemodulator` replaces
  `PllFmDemodulator` as this port's main picture-decode demodulator, matching legacy's real compiled-in
  default (`m_Type=2`). Bundled AFC re-sourcing (legacy's PLL/Hilbert branches feed AFC differently) and
  a second scope expansion: AVT training lock always uses PLL in legacy regardless of the picture
  demodulator, so it now gets its own dedicated `PllFmDemodulator` instance. Two rounds of auditor
  plan-review (4 blockers round 1, 1 new blocker + 2 small fixes round 2, all independently
  re-verified). Full before/after measurement across all 43 modes + both golden-vector fixtures: 43/45
  improved, only RM8/RM12 worsened slightly (the exact narrow-pitch modes already flagged as marginal
  in the scoping pass) — both stay well inside existing tolerances, none needed changing. 23 new
  isolated unit tests. Commit `aeccfcc`.

All committed and pushed through Piece 14 (`aeccfcc`), 363/363 tests passing.

## Other open items (after piece 14)
- **Pre-AGC/pre-demodulator bandpass filter chain** (`sstv.cpp:1824-1833`) — deliberately deferred out
  of Piece 14's scope per user instruction ("Hilbert first, filter chain after"). QSSTV cross-check
  (spec/14-roadmap.md's scoping-pass section) suggests this matters more for real-world noise
  robustness than `HilbertFmDemodulator` alone provides — not started.
- **Windows CI** — `windows-latest` fails as of the Engine 0-6 push, not investigated. User (2026-07-31):
  do this after the Hilbert work, not before, but "shouldn't wait too long either."

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
