# Building on Windows

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- The MSVC C++ build tools, for `cl.exe` (the native audio shim's compiler on this platform):
  - Easiest: install **Visual Studio** (any edition, including Community) with the "Desktop
    development with C++" workload, then build from a **Developer Command Prompt for VS** (or
    **Developer PowerShell for VS**) — this puts `cl.exe` on `PATH` automatically.
  - Alternatively, install just the **Build Tools for Visual Studio** (no full IDE) with the same
    "Desktop development with C++" workload, and build from its own Developer Command Prompt.
  - A plain `cmd.exe`/PowerShell window without one of the above will not have `cl.exe` on `PATH` —
    `dotnet build` will fail at the native shim step.
- (Optional, runtime only — not needed to build) `hamlib-4.dll` (or `libhamlib-4.dll`) if you want
  CAT/rig control via Hamlib rather than `rigctld`/flrig. `ScanlineStudio.Core.Radio.Hamlib` loads it
  dynamically at runtime (`HamlibLibraryLocator.cs`); its absence does not affect building, and the
  app runs fine without it. Grab a Windows Hamlib build from the
  [Hamlib releases page](https://github.com/Hamlib/Hamlib/releases) if you want it, and place the
  DLL somewhere on `PATH` or next to the built executable.

## Build

From a **Developer Command Prompt/PowerShell for VS**, at the repo root:

```powershell
dotnet build ScanlineStudio.sln
```

The native audio shim is built automatically as part of this — `BuildNativeShimWindows` runs before
the managed build and produces `yoniqaudio.dll` alongside the managed output (via `cl.exe /LD`). You
do not need to build it separately.

## Run

```powershell
dotnet run --project src/ScanlineStudio.Host
```

## Test

```powershell
dotnet test ScanlineStudio.sln
```

To run a single test project or filter to one test, see the exact invocations in `CLAUDE.md` §6 (if
present in your checkout — it's gitignored, project-local).

## Troubleshooting

- **`cl.exe` not found / native shim build fails**: you're building from a plain shell, not a
  Developer Command Prompt/PowerShell for VS. Open one of those instead (Start menu → "Developer
  Command Prompt for VS 2022", or similar), `cd` back to the repo, and re-run `dotnet build`.
- **`error MSB4126: The specified solution configuration "Debug|x64" is invalid`**: this project's
  `.sln` only defines "Any CPU" solution configurations. If you opened a Developer Command Prompt
  (which sets an ambient `Platform=x64` environment variable so `cl.exe`'s own toolchain resolves
  correctly), that variable can leak into `dotnet build`/`dotnet restore` as a default MSBuild
  property and collide with the solution's own configurations. Pass the platform explicitly to work
  around it:

  ```powershell
  dotnet restore ScanlineStudio.sln /p:Platform="Any CPU"
  dotnet build ScanlineStudio.sln --no-restore /p:Platform="Any CPU"
  ```

  This is exactly what this project's own CI does on `windows-latest` runners for the same reason
  (see `.github/workflows/ci.yml`).
- **Warnings treated as errors**: this project builds with nullable reference types and
  warnings-as-errors on. A build failure that lists warnings (not just errors) is still a real build
  failure — fix the warning, don't suppress it.
