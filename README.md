# Scanline Studio

Website: [scanlinestudio.app](https://scanlinestudio.app)

A ground-up rewrite of [YONIQ](https://github.com/w0eeemst/YONIQ) (a fork of MMSSTV) into a modern,
cross-platform amateur radio SSTV application — .NET 8 + Avalonia UI, targeting Windows, Linux, and macOS.

Design is written down in full before implementation, one document per concern
(`spec/`), so the build proceeds step by step against an agreed plan rather than as one giant
undifferentiated effort — the spec docs remain the design reference as implementation continues.

## Status

**Core loop working end-to-end**: receive an SSTV picture over a real audio device, send one (with
crop/rotate/overlay-text editing before transmit), and control a radio via Hamlib, rigctld, flrig, or
OmniRig (Windows-only) for frequency and PTT. Also built: RX history + logbook (SQLite, ADIF export,
ADIF UDP forwarding to GridTracker2/N1MM/Log4OM), QRZ lookup, a waterfall/spectrum display, and an
Options dialog covering audio devices, CAT backends, and station identification (CW-ID/FSK, NR/RST).

Only English ships as a locale today — the runtime language-switching infrastructure is in place
(see [spec/10-localization.md](spec/10-localization.md)) but no second translation has been written
yet.

See [spec/18-path-to-1.0.md](spec/18-path-to-1.0.md) for the current priority-tiered list of what's
left before a 1.0 tag (this supersedes `spec/14-roadmap.md`'s older phase-based status, which is now
historical).

## Start here

- [spec/00-project-overview.md](spec/00-project-overview.md) — vision, goals, non-goals, license.
- [spec/01-architecture.md](spec/01-architecture.md) through [spec/15-template-designer.md](spec/15-template-designer.md) — one document per subsystem (radio/CAT layer, rigctld, audio, SSTV DSP, imaging, logbook, UI, localization, plugins, settings, testing).
- [spec/18-path-to-1.0.md](spec/18-path-to-1.0.md) — current priority-tiered list of what's left
  before 1.0. [spec/14-roadmap.md](spec/14-roadmap.md) is the older phased delivery plan tying every
  spec document together; its Tier 2+ backlog (UI placeholders, CAT protocol work, localization,
  etc.) is still valid, only its Tier 0/1 "done" status is superseded.
- [docs/removed-features.md](docs/removed-features.md) — legacy capabilities dropped or only partially
  replaced in the rewrite, and why.
- [LICENSES.md](LICENSES.md) — why this project is LGPL-3.0-or-later, and what legacy assets were
  deliberately excluded from the port.

Project rules/agent instructions live in `CLAUDE.md` at the repo root — not linked above since it's
gitignored (project-local, not checked in) and won't resolve for anyone browsing a clone on GitHub.

## Building

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), plus a native C
toolchain for the audio backend's shim (`gcc`/`clang`/MSVC depending on OS — see the per-OS guide
below for exact prerequisites and troubleshooting):

- [build_linux.md](build_linux.md)
- [build_osx.md](build_osx.md)
- [build_windows.md](build_windows.md)

Once the toolchain is in place, the build itself is the same three commands on every OS:

```bash
dotnet build ScanlineStudio.sln
dotnet test ScanlineStudio.sln
dotnet run --project src/ScanlineStudio.Host
```

## Legacy source

The original C++Builder/VCL codebase this project is modernizing lives upstream at
[w0eeemst/YONIQ](https://github.com/w0eeemst/YONIQ) — it is intentionally not vendored into this
repository (see `.gitignore`); spec documents reference it by relative path (e.g. `cradio.cpp`) on the
assumption of a local clone at `yoniq-old/YONIQ-main/` for convenience during development.

## License

LGPL-3.0-or-later. See [COPYING](COPYING), [COPYING.LESSER](COPYING.LESSER), and [LICENSES.md](LICENSES.md)
for the full reasoning.
