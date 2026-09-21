# Scanline Studio

Website: [scanlinestudio.app](https://scanlinestudio.app)

A ground-up rewrite of [YONIQ](https://github.com/w0eeemst/YONIQ) (a fork of MMSSTV) into a modern,
cross-platform amateur radio SSTV application — .NET 8 + Avalonia UI, targeting Windows, Linux, and macOS.

Design is written down in full before implementation, one document per concern (`spec/`), so the
build proceeds step by step against an agreed plan rather than as one giant undifferentiated effort.
Like `CLAUDE.md` below, `spec/` and `docs/` are kept locally during development and are gitignored —
they won't resolve for anyone browsing this repo on GitHub.

## Status

**Core loop working end-to-end**: receive an SSTV picture over a real audio device, send one (with
crop/rotate/overlay-text editing before transmit), and control a radio via Hamlib, rigctld, flrig, or
OmniRig (Windows-only) for frequency and PTT. Also built: RX history + logbook (SQLite, ADIF export,
ADIF UDP forwarding to GridTracker2/N1MM/Log4OM), QRZ lookup, a waterfall/spectrum display, and an
Options dialog covering audio devices, CAT backends, and station identification (CW-ID/FSK, NR/RST).

Only English ships as a locale today — the runtime language-switching infrastructure is in place but
no second translation has been written yet.

1.0 and the confirmed 1.1 target (the TX Template Editor redesign) are both complete. No milestone
past 1.1 is open yet — [BACKLOG.md](BACKLOG.md) tracks current work, and the commit history tracks
what has shipped since.

## Start here

- [BACKLOG.md](BACKLOG.md) — the current work list: what's in progress, what's next.
- [build_linux.md](build_linux.md), [build_osx.md](build_osx.md), [build_windows.md](build_windows.md)
  — per-OS build prerequisites and troubleshooting.
- The offline in-app user guide lives in `assets/help/`, reachable from Help > User guide inside the
  running app.
- [LICENSES.md](LICENSES.md) — why this project is LGPL-3.0-or-later, and what legacy assets were
  deliberately excluded from the port.
- [scanlinestudio.app](https://scanlinestudio.app) — end-user docs and downloads.

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
