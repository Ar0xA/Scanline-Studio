# Building on Linux

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- A C compiler toolchain for the native audio shim
  (`src/ScanlineStudio.Core.Audio.MiniAudio/native/yoniq_audio.c`): `gcc` and `ld` — on Debian/Ubuntu,
  `sudo apt install build-essential`; on Fedora, `sudo dnf install gcc make`; on Arch,
  `sudo pacman -S base-devel`. The MSBuild target that builds this shim
  (`BuildNativeShimLinux` in `src/ScanlineStudio.Core.Audio.MiniAudio/ScanlineStudio.Core.Audio.MiniAudio.csproj`)
  links against `-ldl -lpthread -lm`, all part of glibc — no extra `-dev` packages needed beyond a
  working `gcc`.
- (Optional, runtime only — not needed to build) `libhamlib.so.4` if you want CAT/rig control via
  Hamlib rather than `rigctld`/flrig. `ScanlineStudio.Core.Radio.Hamlib` loads it dynamically at
  runtime (`HamlibLibraryLocator.cs`); its absence does not affect building, and the app runs fine
  without it (rig control via `rigctld`/flrig still works, or you can run with no rig connected at
  all). Install via your distro's package manager (e.g. `sudo apt install libhamlib4` on
  Debian/Ubuntu) if you want it.

## Build

From the repo root:

```bash
dotnet build ScanlineStudio.sln
```

The native audio shim is built automatically as part of this — `BuildNativeShimLinux` runs before
the managed build and produces `libyoniqaudio.so` alongside the managed output. You do not need to
build it separately.

## Run

```bash
dotnet run --project src/ScanlineStudio.Host
```

## Test

```bash
dotnet test ScanlineStudio.sln
```

To run a single test project or filter to one test, see the exact invocations in `CLAUDE.md` §6 (if
present in your checkout — it's gitignored, project-local).

## Troubleshooting

- **`gcc: command not found` / native shim build fails**: install a C toolchain (see Prerequisites
  above), then re-run `dotnet build`. The native build step is gated on
  `$([MSBuild]::IsOSPlatform('Linux'))` and only runs on Linux.
- **`dotnet build` succeeds but audio devices never enumerate at runtime**: this is a runtime/audio
  backend issue (PipeWire/PulseAudio/ALSA), not a build problem — check that your user session has a
  working audio server (`pactl info` is a quick sanity check on a PipeWire-pulse or PulseAudio
  system).
- **Warnings treated as errors**: this project builds with nullable reference types and
  warnings-as-errors on. A build failure that lists warnings (not just errors) is still a real build
  failure — fix the warning, don't suppress it.
