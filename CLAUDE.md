# CLAUDE.md

Yoniq v2: cross-platform (.NET 8 + Avalonia) rewrite of YONIQ (MMSSTV fork), planned in `spec/00`-`spec/15`.

Legacy source: https://github.com/w0eeemst/YONIQ (not in this repo). Specs reference legacy files assuming a local clone at `yoniq-old/YONIQ-main/` (gitignored, never committed) — clone it separately to inspect.

## Collaboration

User has ADHD: stay tightly scoped, no unrequested tangents/scope creep — note off-scope findings in one line instead of chasing them. Restate this explicitly in every Opus/subagent prompt; don't rely on them reading this file.

## License

LGPL-3.0-or-later (see [LICENSES.md](LICENSES.md), [COPYING](COPYING), [COPYING.LESSER](COPYING.LESSER)) — matches upstream, since specs port legacy algorithms/constants, not clean-room.

**License-audit rule**: any ported/bundled legacy asset (source, data table, bitmap, `.mtm`, prefix table, dataset) needs a new `LICENSES.md` entry (what, source, license). When in doubt, exclude and find an unambiguously licensed alternative.

## "Preserve behavior" means

Full rewrite (C++Builder/VCL → C#/Avalonia), not incremental refactor. "Preserve behavior"/"backward compatibility" = observable behavior, user data, feature scope (a decode still produces the same image, `.ini` settings still import cleanly) — **not** source/binary compatibility with the old codebase. "Prefer refactor over rewrite" applies *within* the new C# codebase once built, not to the initial port.

**Removal rule**: dropping a capability instead of porting it needs a [docs/removed-features.md](docs/removed-features.md) entry (legacy files, replacement if any, affected users). "Superseded"/"replaced" claims must state what's *not* covered — see that doc's OmniRig/CItems/MMlink/Loglink entries for examples of partial, not full, replacement.

## Primary objectives

- **No assumptions** — verify against actual legacy code; don't infer from similarly-named/shaped cases or general domain knowledge (e.g. "Robot 72 is probably Robot 36 but slower" is false — it's grouped with a different family in `Main.cpp`). Flag anything unverified as an assumption, don't present it as confirmed.
- **Port first, invent second (DSP/codec math)** — for SSTV encode/decode, filters, demodulators, CAT framing (`sstv.cpp`, `Fft.cpp`, `fir.cpp`, `cradio.cpp`): port the exact legacy algorithm (same filter structure/discriminator/constants), don't invent-and-tune. Doesn't extend to the surrounding GUI/architecture — that's intentionally being modernized.
- Preserve behavior whenever practical.
- Keep commits small and reviewable.
- Never remove existing radio support unless replaced; log partial replacement in `docs/removed-features.md`.
- Write tests for all new functionality.
- Dependency injection everywhere; avoid static mutable state.
- All hardware communication must be async.
- UI never talks to radio/audio/DSP directly — only via `Yoniq.Application` service interfaces (`spec/01-architecture.md`).
- No hardcoded UI strings — localize everything (`spec/10-localization.md`; the legacy language `.ini` files are **not** a string table).
- Nullable reference types; treat warnings as errors.
- SOLID principles; composition over inheritance.

## Legacy-source-specific rules

C++Builder/VCL conventions that silently produce wrong results if ported naively:

- **Encoding rule**: every legacy file read (`.ini`/`.txt`/`.DEF`/`.dfm`/`.mtm`/`.MDT`/`.bin`/`.cpp`/`.h` mined for strings or constants) must specify its source encoding explicitly. Assume **Windows-31J/CP932** for Japanese-origin content (confirmed in `Mmsstv Japanese.ini`, `MMCG.DEF`, inline literals) — never assume UTF-8. Register `System.Text.CodePagesEncodingProvider` at startup. No legacy text enters the codebase without a documented decode step.
- **Binary-is-bytes rule**: legacy `AnsiString`/`char[]` protocol/binary fields (e.g. `cradio.h`'s `CmdInit`/`CmdRx`/`CmdTx`/`cmdGNR`) must be modeled as `byte[]`/`ReadOnlyMemory<byte>`, never `string`/JSON — they carry raw bytes ≥0x80 and embedded nulls that a text-based migration would corrupt.
- **Behavioral-parity rule**: any DSP-core (`sstv.cpp`/`Fft.cpp`/`fir.cpp`) or CAT-layer (`cradio.cpp`) port needs golden-vector tests — real legacy-captured reference input/output with a documented tolerance/rounding rule (`spec/13-testing.md`). A round-trip test alone is insufficient (both halves can be wrong the same way); legacy is `double`, the new DSP core is `float` — that needs a stated, tested tolerance, not an assumed "close enough."
- **TX/RX are separate legacy code paths — read the matching one.** Legacy splits TX (`Main.cpp`'s `TMmsstv::Line*` — `LineMRT`, `LineSCT`, `LineR36`, etc.) from RX (a separate per-pixel decode switch) into different functions; never infer one from the other. Real incident: an earlier Scottie entry inferred TX channel order from RX branch widths — got duration right (matched `GetTiming`) but order/sync placement wrong (real order is separator-G-separator-B-sync-in-middle-separator-R, not sync-first R,G,B) — and its round-trip test passed anyway, since encoder and decoder agreed with each other while both were wrong about reality. Duration match + round-trip pass are both necessary, neither sufficient — always check the actual matching source function.
- **Concurrency/scheduler rule**: every cross-thread `IObservable`/event stream must state its scheduler and its slow-subscriber behavior (buffer/drop/block). Legacy's `PostMessage` never blocked the producer; a naive `Subject` replacement can — a real regression on hot paths like radio polling.
- **No Win32/COM outside an explicit optional module**: no `System.Drawing`, P/Invoke, or COM interop in `Yoniq.Core.*`/`Yoniq.UI`. A Windows-only integration gets its own optional project, never a hard dependency of cross-platform code.
