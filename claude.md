# CLAUDE.md

This repository modernizes YONIQ (a fork of MMSSTV) into Yoniq v2: a cross-platform (.NET 8 + Avalonia) rewrite, planned in `spec/00` through `spec/15`.

Legacy source: **https://github.com/w0eeemst/YONIQ** (upstream, not included in this repository). Spec documents reference legacy files by relative path (e.g. `cradio.cpp`, `Draw.h`) on the assumption of a local clone at `yoniq-old/YONIQ-main/` for convenience during development — that path is gitignored here and never committed; clone upstream separately if you need to inspect it.

## License

Yoniq v2 is **LGPL-3.0-or-later** — see [LICENSE provenance](LICENSES.md), [COPYING](COPYING), [COPYING.LESSER](COPYING.LESSER). This follows upstream YONIQ/MMSSTV's own stated license, which matters because the specs plan to port algorithms and constants from the legacy source, not reimplement clean-room.

**License-audit rule**: no legacy asset (source file, data table, bitmap, `.mtm` template, prefix table, dataset) may be ported or bundled into Yoniq v2 without a new entry in `LICENSES.md` recording what it is, where it came from, and under what license it's included. When in doubt, exclude it and find an unambiguously licensed alternative rather than guessing.

## What "preserve behavior" means here

This is a full technology rewrite (C++Builder/VCL → C#/Avalonia), not an incremental refactor of the existing codebase — the two cannot both be true, so to be explicit: **"preserve behavior" and "backward compatibility" refer to observable behavior, user data, and feature scope** (a saved rig configuration still works after migration, an SSTV decode still produces the same image, existing `.ini` settings import cleanly), **not to source or binary compatibility with the C++Builder codebase**, which is being replaced wholesale. "Prefer incremental refactoring over rewrites" applies *within* the new C# codebase once it exists — don't restructure `Yoniq.Core.Sstv` on a whim once it's built — not to the initial C++Builder → C# transition itself.

**Removal rule**: dropping a legacy capability instead of porting it requires a new entry in [docs/removed-features.md](docs/removed-features.md) naming the legacy files, the replacement (if any), and exactly which users are affected. A claim that something is "superseded" or "replaced" must say what it does *not* cover — see that document for examples (OmniRig's rig-sharing arbitration, the CItems plugin ABI, MMlink, Loglink) where the honest answer was a partial, not full, replacement.

## Primary objectives

- Preserve behavior whenever practical (see above for what that means in a rewrite).
- Keep commits small and reviewable.
- Never remove existing radio support unless replaced — and if only partially replaced, it must be logged in `docs/removed-features.md`, not silently narrowed.
- Write tests for all new functionality.
- Use dependency injection; avoid static mutable state.
- All hardware communication must be asynchronous.
- UI must never directly communicate with radio, audio, or DSP drivers/types — only through `Yoniq.Application` service interfaces (see `spec/01-architecture.md`).
- No hardcoded UI strings; every user-visible string must be localized (see `spec/10-localization.md` — and note its migration section was corrected after review; the legacy language `.ini` files are *not* a string table, don't treat them as one).
- Use nullable reference types; treat compiler warnings as errors.
- Follow SOLID principles; prefer composition over inheritance.

## Legacy-source-specific rules (added after cross-checking the specs against the actual old code)

These exist because the legacy codebase is C++Builder/VCL, and several of its conventions will silently produce wrong results if carried over naively into C#.

- **Encoding rule**: every read of a legacy file (`.ini`, `.txt`, `.DEF`, `.dfm`, `.mtm`, `.MDT`, `.bin`, or legacy `.cpp`/`.h` source being mined for strings or constants) must specify its source encoding explicitly. Assume **Windows-31J/CP932** for anything Japanese-origin (confirmed present in `Mmsstv Japanese.ini`, `MMCG.DEF`, and inline Japanese string literals in `.cpp` sources) — never assume UTF-8. Register `System.Text.CodePagesEncodingProvider` at startup for any tooling that needs to decode these. No legacy text may enter the new codebase without a documented decode step.
- **Binary-is-bytes rule**: any legacy value declared `AnsiString` or `char[]` that carries protocol frames or other binary data (e.g. `cradio.h`'s `CmdInit`/`CmdRx`/`CmdTx`/`cmdGNR`) must be modeled as `byte[]`/`ReadOnlyMemory<byte>` in C#, never as `string`/JSON text. These fields carry raw bytes including values ≥0x80 and embedded nulls that a text-based migration would silently corrupt.
- **Behavioral-parity rule**: any algorithm ported from the DSP core (`sstv.cpp`, `Fft.cpp`, `fir.cpp`) or the CAT layer (`cradio.cpp`) must ship with golden-vector tests — reference input/output pairs captured from the actual legacy implementation, with a documented tolerance and rounding rule (see `spec/13-testing.md`). An encode-then-decode round-trip test alone is not sufficient: it can pass while both halves are wrong in the same direction. This matters especially because the legacy code is `double`-precision throughout and the new DSP core is speced as `float` — a real numeric difference that needs a stated, tested tolerance, not an assumption that "close enough" happens automatically.
- **Concurrency/scheduler rule**: every `IObservable`/event stream that crosses a thread boundary (e.g. radio state changes, audio device changes) must state its scheduler and its behavior under a slow subscriber (buffer? drop? block the producer?). The legacy `PostMessage`-based cross-thread notification never blocked the producing thread; a naive `Subject`-based replacement can, and that's a behavior regression if it happens on a hot path like radio polling.
- **No Win32/COM outside an explicit optional module**: no `System.Drawing`, no Win32 P/Invoke, no COM interop anywhere in `Yoniq.Core.*` or `Yoniq.UI`. If a Windows-only integration is ever justified, it lives in its own clearly-optional project, never a hard dependency of cross-platform code.
