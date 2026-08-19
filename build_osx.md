# Building on macOS

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) — the Arm64 or x64 installer,
  matching your Mac.
- Xcode Command Line Tools, for `clang` (the native audio shim's compiler on this platform):

  ```bash
  xcode-select --install
  ```

  If already installed, this command just reports so and exits — safe to run either way. No full
  Xcode install is required, just the command-line tools.
- (Optional, runtime only — not needed to build) `libhamlib.4.dylib` if you want CAT/rig control via
  Hamlib rather than `rigctld`/flrig. `ScanlineStudio.Core.Radio.Hamlib` loads it dynamically at
  runtime (`HamlibLibraryLocator.cs`); its absence does not affect building, and the app runs fine
  without it. Install via Homebrew if you want it: `brew install hamlib`.

## Build

From the repo root:

```bash
dotnet build ScanlineStudio.sln
```

The native audio shim is built automatically as part of this — `BuildNativeShimMacOS` runs before
the managed build and produces `libyoniqaudio.dylib` alongside the managed output (via
`clang -dynamiclib`, linked against `-lpthread -lm`). You do not need to build it separately.

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

- **`xcrun: error: invalid active developer path` / native shim build fails**: the Command Line
  Tools either aren't installed or got removed by an Xcode update — re-run
  `xcode-select --install`.
- **Gatekeeper/notarization prompts when running a built binary**: this project's own CI/build
  process does not code-sign or notarize local Debug/Release builds — running via
  `dotnet run`/`dotnet build` output directly (not a distributed `.app` bundle) does not trigger
  Gatekeeper the same way a downloaded, quarantined app would. If you hit a Gatekeeper block on a
  packaged build, that's a packaging/distribution concern, not a build-from-source one.
- **Warnings treated as errors**: this project builds with nullable reference types and
  warnings-as-errors on. A build failure that lists warnings (not just errors) is still a real build
  failure — fix the warning, don't suppress it.
