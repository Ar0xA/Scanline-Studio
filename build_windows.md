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
dotnet restore ScanlineStudio.sln /p:Platform="Any CPU"
dotnet build ScanlineStudio.sln --no-restore /p:Platform="Any CPU"
```

**Why `/p:Platform="Any CPU"` up front, not just on failure**: opening a Developer Command Prompt
(needed below for `cl.exe`) sets an ambient `Platform=x64` environment variable so `cl.exe`'s own
toolchain resolves correctly. MSBuild reads that same variable as a default property, and
`ScanlineStudio.sln` only defines "Any CPU" solution configurations — so a plain `dotnet build
ScanlineStudio.sln` from that same prompt fails immediately with `error MSB4126: The specified
solution configuration "Debug|x64" is invalid`. Passing the flag explicitly avoids hitting that on
your very first build. This is exactly what this project's own CI does on `windows-latest` runners
for the same reason (see `.github/workflows/ci.yml`).

The native audio shim is built automatically as part of this — `BuildNativeShimWindows` runs before
the managed build and produces `scanlineaudio.dll` alongside the managed output (via `cl.exe /LD`,
linked against `Ole32.lib` — added 2026-08-24 for a real OS device-mute-state query the shim now
makes via direct WASAPI `IAudioEndpointVolume` COM calls). Ole32.lib ships with the MSVC Build
Tools' "Desktop development with C++" workload already required above — no extra install needed.
The three GUID symbols that query needs (`CLSID_MMDeviceEnumerator`/`IID_IMMDeviceEnumerator`/
`IID_IAudioEndpointVolume`) are defined directly in `scanline_audio.c` itself via `DEFINE_GUID` — an
earlier revision tried linking `Uuid.lib` for these instead, which a real `dotnet publish` showed
does not actually provide their storage (LNK2019). You do not need to build the shim separately.

## Run

```powershell
dotnet run --project src/ScanlineStudio.Host
```

No `/p:Platform` needed here — this targets the single `.csproj` directly, not the `.sln`, so the
solution-configuration mismatch above doesn't apply.

## Test

```powershell
dotnet test ScanlineStudio.sln
```

To run a single test project or filter to one test, see the exact invocations in `CLAUDE.md` §6 (if
present in your checkout — it's gitignored, project-local).

### Opt-in tests, and what each one costs the machine

Some Windows tests are gated behind an environment variable, because running them takes something
real: an audio device, a serial port, or the machine's own mute state. A plain `dotnet test` skips
every one of them, so an ordinary run never disturbs anything.

The tier is visible in each test's attribute, so you can tell what it costs without reading the body:

| attribute | cost |
|---|---|
| `[WindowsFact]` | nothing — no audio, no serial, no hardware |
| `[WindowsAudioReadOnlyFact]` | enumerates devices or reads their state; **opens no stream**, inaudible |
| `[WindowsAudioExclusiveFact]` | **opens a device or changes system state** — opt-in |
| `[OmniRigInstalledFact]` | **may open the rig's serial port** — opt-in |
| `[RequiresDecodeMeasurementFact]` | no hardware, but slow — these are measurement harnesses |

#### Audio: `SCANLINE_WINDOWS_AUDIO_EXCLUSIVE=1`

```powershell
$env:SCANLINE_WINDOWS_AUDIO_EXCLUSIVE = "1"
dotnet test tests\ScanlineStudio.Core.Audio.MiniAudio.Tests
```

Runs the mute oracle and the WASAPI loopback tests.

The mute oracle **mutes your default output device**, reads the query, then restores whatever state
it found — in a `finally`, so a failed assertion still puts the machine back. A mute query cannot be
verified without a known mute state, and Windows offers no virtual endpoint to use instead. Two
things worth knowing: muting fires a system-wide notification, so the volume OSD appears; and if the
test process is killed between the mute and the restore, the machine stays muted.

Loopback opens a capture stream against an output endpoint. It does not silence that output or take
it from another application — loopback is explicitly a non-exclusive observer — but it is a real
device open rather than an enumeration.

#### OmniRig: `SCANLINE_OMNIRIG_COM=1`

```powershell
$env:SCANLINE_OMNIRIG_COM = "1"
dotnet test tests\ScanlineStudio.Core.Radio.Tests --filter FullyQualifiedName~OmniRigRealComObject
```

**Do not run this while operating.** Connecting starts OmniRig's COM server, and if OmniRig is
configured with a real rig that server may open the serial port and begin polling, exactly as if you
had launched OmniRig yourself. The test cannot prevent that — it is what instantiating OmniRig means.

Every operation is a READ. Nothing sets a frequency, a mode or PTT. It skips rather than fails when
OmniRig is not registered, since that is third-party software this project does not ship.

Two results are written to test output rather than asserted, because they depend on your machine
rather than on the code. Look for them:

- `NOT COVERED: no rig online` — the frequency and mode checks did not run.
- `NOT COVERED on this machine: all N device names are ASCII` — the multi-byte branch of the WASAPI
  device-id conversion did not run. Renaming an audio endpoint in Sound settings exercises it.

#### Decode measurements: `SCANLINE_RUN_DECODE_MEASUREMENTS=1`

These report their findings through the failure message, so they always "fail" when run — that is how
the numbers surface. They also account for roughly 28 of the suite's 35 minutes, which is why a plain
run skips them.

## Standalone build (no .NET runtime required on the target machine)

For a build you can hand to someone (or run on a machine) without installing the .NET 8 runtime
first — a self-contained, single-file publish:

```powershell
dotnet publish src\ScanlineStudio.Host -c Release -r win-x64 -o publish\win-x64
```

`ScanlineStudio.Host.csproj` turns on `SelfContained`/`PublishSingleFile` automatically whenever
`-r <runtime identifier>` is passed — no extra `-p:` flags to remember. Same as `dotnet run` above,
no `/p:Platform` is needed either, since this targets the single project, not the solution.

This produces one `ScanlineStudio.Host.exe` (self-contained: the .NET runtime itself is bundled in,
not just the app) in `publish\win-x64\`, plus a handful of loose native `.dll`s alongside it
(`scanlineaudio.dll` — this project's own audio shim — and Avalonia's own Skia/HarfBuzz rendering
libraries) that are **not** folded into the `.exe` and must ship with it. **Copy the whole
`publish\win-x64\` folder**, not just the `.exe`, if moving the build elsewhere.

**Verified end-to-end on Linux** (a real self-contained `linux-x64` publish, run with `dotnet`
stripped from `PATH` entirely, confirmed to start clean and resolve a real Hamlib install with no
runtime-resolution errors) — the `win-x64` path uses the exact same MSBuild mechanism
(`SelfContained`/`PublishSingleFile`, standard SDK properties, no custom per-OS logic), so it's
expected to behave identically, but **has not itself been run on a real Windows machine yet**. If
you hit anything odd building or running this specific standalone `win-x64` publish, that's useful,
concrete signal — first real-world data point for this exact path.

## Troubleshooting

- **`cl.exe` not found / native shim build fails**: you're building from a plain shell, not a
  Developer Command Prompt/PowerShell for VS. Open one of those instead (Start menu → "Developer
  Command Prompt for VS 2022", or similar), `cd` back to the repo, and re-run `dotnet build`.
- **`error MSB4126: The specified solution configuration "Debug|x64" is invalid`**: see the Build
  section above — pass `/p:Platform="Any CPU"` on both `restore` and `build`. This should not come up
  if you followed the commands above as written; this entry exists for anyone who ran a plain
  `dotnet build ScanlineStudio.sln` first and hit it.
- **Warnings treated as errors**: this project builds with nullable reference types and
  warnings-as-errors on. A build failure that lists warnings (not just errors) is still a real build
  failure — fix the warning, don't suppress it.
