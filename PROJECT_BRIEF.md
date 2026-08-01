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

All committed and pushed, 327/327 tests passing as of Piece 12.

## Piece 13 — `TryDecodeNarrowModeHeader` FSK bit decode — NOT STARTED, current task

**The bug**: `AnalogFmSstvDecoder.cs:1070-1126`'s `TryDecodeNarrowModeHeader` decodes each of the 24
MN/MC mode-ID bits by averaging the shared PLL's demodulated-frequency stream over a fixed 22ms window
and comparing to a fixed midpoint threshold (`AverageFrequencyInWindow`/`NarrowDiscriminatorThresholdHz`,
`:1090-1091`) — reading off the general PLL stream rather than replicating a dedicated detector. **Same
bug shape Piece 9 already fixed for VIS-bit decode** (a PLL-stream proxy that only worked by
coincidence, replaced with legacy's real dual-envelope-detector tone race).

**Legacy's real mechanism, located but not fully read yet**: `CSSTVDEM::DecodeFSK(int m, int s)`
(`sstv.cpp:2378` onward — read through the state machine's cases 0-4 entry only, not the full
byte/sync-check tail past `sstv.cpp:2450` — read the rest before implementing). Called every sample
(`sstv.cpp:1858`, `DecodeFSK(int(d19), int(dsp))`) with:
- `m = d19` — the SAME continuously-running 1900Hz envelope detector (`m_iir19`/`m_lpf19`) Piece 9
  already ported and uses for the VIS tone race — likely directly reusable, verify before rebuilding.
- `s = dsp` — a SEPARATE envelope detector on `m_iirfsk`, tuned to `FSKSPACE + g_dblToneOffset` =
  **2100Hz**, bandwidth param `100.0` (`sstv.cpp:1450/1702`) — NOT yet ported anywhere in this codebase;
  check whether `100.0` matches `SyncEnvelopeDetector`'s existing constructor shape (Piece 9's d11/d13
  detectors use 80Hz bandwidth — a different value, don't assume interchangeable).
- Decode is a difference-of-amplitudes race, not a frequency read: `d = ABS(m-s)`, gated on `d >= 2048`
  (amplitude threshold, not Hz) and which of `m`/`s` is larger — structurally the same shape as Piece
  9's `d11`/`d13`/`d19` VIS-bit race, just against a different tone pair (1900Hz mark / 2100Hz space).
- 5-phase state machine (`m_fskmode` 0-4+): guard-tone detection (debounced, `FSKGARD=100`ms),
  start-bit detection (debounced), then per-bit sampling at fixed `FSKINTVAL=22`ms intervals
  (`sstv.h:705-707`) — **`FSKINTVAL=22` already matches this port's `VisHeader.NarrowBitDurationMs=22`
  exactly**, so the timing constant is right; only the per-sample decision mechanism is the proxy.
- Byte sync check in the case-4 default path references `0x2a` ("First SYNC") — **this port's current
  `VisHeader.NarrowStxByte = 0x2d` doesn't match that number.** Not yet resolved whether this is a real
  mismatch or `0x2a` is something else entirely (a preamble marker distinct from what this port calls
  "STX") — verify by reading the rest of `DecodeFSK`'s tail before assuming either way.

**Scope note**: `AnalogFmSstvDecoder.cs:468-492`'s `TryDecodeHeader` narrow-vs-normal-VIS discriminator
also uses `AverageFrequencyInWindow`/`NarrowDiscriminatorThresholdHz` — **not part of this bug**, it's a
separate, already-justified coarse classification (which decode path to try, not decoding data bits).
Only the per-bit decode inside `TryDecodeNarrowModeHeader` (`:1084-1092`) is in scope.

**Suggested next steps**: read `DecodeFSK`'s full body and `m_iirfsk`'s full setup, resolve the
`0x2a`-vs-`0x2d` question, scope whether this reuses Piece 9's `SyncEnvelopeDetector`/tone-race
machinery directly or needs its own variant — likely warrants at least one auditor plan-review round
given the DSP/state-machine complexity and this session's track record of "small-looking" items turning
out bigger (pieces 11, 12).

## Other open items (after piece 13)
- **Hilbert demodulator (`CHILL`) research/scoping pass** — legacy's real shipped default demodulator
  is Hilbert, not PLL (`sstv.cpp:1492`/`2256`); this port only has PLL. Empirically measured to cause
  small golden-vector delta increases via slow/undershooting PLL settling on narrow-pitch modes (piece
  10's finding). Not committing to implementing Hilbert yet — read `CHILL`'s real implementation in
  `sstv.cpp`, get an actual settling-time comparison, scope the size of the piece.
- **Windows CI** — `windows-latest` fails as of the Engine 0-6 push, not investigated. User (2026-07-31):
  do this after piece 13 and the Hilbert scoping pass, not before, but "shouldn't wait too long either."

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
