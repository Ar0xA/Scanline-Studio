# Yoniq v2

A ground-up rewrite of [YONIQ](https://github.com/w0eeemst/YONIQ) (a fork of MMSSTV) into a modern,
cross-platform amateur radio SSTV application — .NET 8 + Avalonia UI, targeting Windows, Linux, and macOS.

This repository is currently **spec-first**: the design is written down in full before implementation,
one document per concern, so the build proceeds step by step against an agreed plan rather than as one
giant undifferentiated effort.

## Status

**Phase 0 — walking skeleton.** The solution scaffold builds cleanly (0 warnings, `TreatWarningsAsErrors`
enabled) and `Yoniq.Host` boots a DI container and shows a blank Avalonia window. See
[spec/14-roadmap.md](spec/14-roadmap.md) for what's next (Phase 1: audio capture/playback and the SSTV
encode/decode round-trip).

## Start here

- [CLAUDE.md](CLAUDE.md) — project rules and guardrails (also doubles as agent instructions).
- [spec/00-project-overview.md](spec/00-project-overview.md) — vision, goals, non-goals, license.
- [spec/01-architecture.md](spec/01-architecture.md) through [spec/15-template-designer.md](spec/15-template-designer.md) — one document per subsystem (radio/CAT layer, rigctld, audio, SSTV DSP, imaging, logbook, UI, localization, plugins, settings, testing).
- [spec/14-roadmap.md](spec/14-roadmap.md) — phased delivery plan tying every spec document together.
- [docs/removed-features.md](docs/removed-features.md) — legacy capabilities dropped or only partially
  replaced in the rewrite, and why.
- [LICENSES.md](LICENSES.md) — why this project is LGPL-3.0-or-later, and what legacy assets were
  deliberately excluded from the port.

## Building

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet build Yoniq.sln
dotnet test Yoniq.sln
dotnet run --project src/Yoniq.Host
```

## Legacy source

The original C++Builder/VCL codebase this project is modernizing lives upstream at
[w0eeemst/YONIQ](https://github.com/w0eeemst/YONIQ) — it is intentionally not vendored into this
repository (see `.gitignore`); spec documents reference it by relative path (e.g. `cradio.cpp`) on the
assumption of a local clone at `yoniq-old/YONIQ-main/` for convenience during development.

## License

LGPL-3.0-or-later. See [COPYING](COPYING), [COPYING.LESSER](COPYING.LESSER), and [LICENSES.md](LICENSES.md)
for the full reasoning.
