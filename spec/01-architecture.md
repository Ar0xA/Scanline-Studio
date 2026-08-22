# Architecture

## Related

[[00-project-overview]] · feeds → all other spec documents

## Technology Stack

| Concern | Choice | Rationale |
|---|---|---|
| Runtime | .NET 8 (LTS), C# 12 | Cross-platform (win-x64, linux-x64, osx-x64/arm64), long support window |
| UI framework | Avalonia UI 11 (Fluent theme) | XAML + MVVM, true cross-platform desktop rendering, closest migration path from VCL forms |
| MVVM toolkit | CommunityToolkit.Mvvm | Source-generated `ObservableProperty`/`RelayCommand`, no reflection overhead |
| DI container | `Microsoft.Extensions.DependencyInjection` via generic `Host` | Constructor injection everywhere, no service locator |
| Configuration | `System.Text.Json` (source-generated) behind `ISettingsStore` | Single user JSON file under `%AppData%`/`ScanlineStudio`; defaults come from `AppSettings` record defaults, not a config-layering system — see [[12-settings]] |
| Logging (app diagnostics) | `Microsoft.Extensions.Logging` + a minimal in-repo `FileLoggerProvider` (`ScanlineStudio.Host`) | Append-only file alongside the default console provider; no third-party sink dependency (see [[08-logging]] for QSO logging, a separate concern) |
| Rig transport | Byte-stream transports behind `IRadioTransport` (TCP only); serial CAT is handled inside linked Hamlib | No in-house serial port code — see [[03-cat-layer]] |
| Networking | `System.Net.Sockets`, wrapped | Used for rigctld and TCP-attached rigs ([[04-rigctld]]) |
| Audio | Cross-platform native backend behind `IAudioEngine` | See [[05-audio-engine]] |
| Testing | xUnit, coverlet, hand-written fakes (no mocking/assertion library by choice) | See [[13-testing]] |
| Packaging | `dotnet publish` self-contained, per-OS | No installer dependency on .NET being preinstalled |

.NET (not Electron/web) was chosen because the DSP and audio paths are CPU- and latency-sensitive; a native runtime avoids the GC/JIT unpredictability of a JS engine for real-time sample processing.

## Layering

Strict one-directional dependency flow. Each layer only depends on layers below it and on abstractions (interfaces), never on concrete types from sibling or higher layers.

```
┌─────────────────────────────────────────────────────────────┐
│ ScanlineStudio.UI               (Avalonia views, ViewModels)          │  see 09-ui.md
├─────────────────────────────────────────────────────────────┤
│ ScanlineStudio.Plugins           (scaffolded, empty -- no plugin host │  see 11-plugin-system.md
│                                    implemented yet)                    │
├─────────────────────────────────────────────────────────────┤
│ ScanlineStudio.Application       (use-case orchestration, services)   │
│  ├─ Sstv session orchestration                               │
│  ├─ Radio session orchestration                              │
│  └─ Logbook orchestration                                    │
├─────────────────────────────────────────────────────────────┤
│ ScanlineStudio.Core.Sstv    │ ScanlineStudio.Core.Radio   │ ScanlineStudio.Core.Logbook  │  see 06, 02/03/04, 08
│ ScanlineStudio.Core.Audio   │ ScanlineStudio.Core.Imaging │ ScanlineStudio.Core.Localization│ see 05, 07, 10
├─────────────────────────────────────────────────────────────┤
│ ScanlineStudio.Settings           (config load/save/migrate)          │  see 12-settings.md
├─────────────────────────────────────────────────────────────┤
│ ScanlineStudio.Abstractions       (interfaces + DTOs shared by all;   │
│                                    the true base layer -- zero        │
│                                    project references of its own)     │
└─────────────────────────────────────────────────────────────┘
```

Rule: `ScanlineStudio.UI` never references rig-specific protocol types or audio backend types directly — never a `ScanlineStudio.Core.*` concrete assembly, only `ScanlineStudio.Application` service interfaces, `ScanlineStudio.Settings` (for UI-owned preference sections), and view-model-friendly DTOs. Enforced by `UiLayeringArchitectureTests` (`tests/ScanlineStudio.UI.Tests/UiLayeringArchitectureTests.cs`), which also bans direct `Microsoft.Data.Sqlite`/`SixLabors.*` package references from `ScanlineStudio.UI`. This directly encodes the CLAUDE.md rule "UI must never directly communicate with radio drivers."

## Solution structure

```
/src
  ScanlineStudio.Abstractions/
  ScanlineStudio.Settings/
  ScanlineStudio.Core.Radio/
  ScanlineStudio.Core.Radio.Cat/            # 03 -- reserved for the TemplateCatProtocol fallback
                                             # only; NOT per-rig protocols (currently empty)
  ScanlineStudio.Core.Radio.Rigctld/        # 04
  ScanlineStudio.Core.Radio.Hamlib/         # 03, in-process Hamlib CAT backend
  ScanlineStudio.Core.Audio/                # 05
  ScanlineStudio.Core.Audio.MiniAudio/      # 05, miniaudio native backend
  ScanlineStudio.Core.Sstv/                 # 06
  ScanlineStudio.Core.Imaging/              # 07
  ScanlineStudio.Core.Logbook/              # 08
  ScanlineStudio.Core.Localization/         # 10
  ScanlineStudio.Application/
  ScanlineStudio.Plugins/                   # 11, scaffolded in the solution -- no source files yet
  ScanlineStudio.UI/                        # 09, Avalonia app + views + view-models
  ScanlineStudio.Host/                      # composition root: Program.cs, DI registration, hosting
/tests
  ScanlineStudio.Core.Radio.Tests/
  ScanlineStudio.Core.Sstv.Tests/
  ScanlineStudio.Core.Audio.Tests/
  ScanlineStudio.Core.Audio.MiniAudio.Tests/
  ScanlineStudio.Core.Imaging.Tests/
  ScanlineStudio.Core.Localization.Tests/
  ScanlineStudio.Core.Logbook.Tests/
  ScanlineStudio.Settings.Tests/
  ScanlineStudio.Application.Tests/
  ScanlineStudio.Host.Tests/
  ScanlineStudio.UI.Tests/                  # view-model tests, headless Avalonia
  ScanlineStudio.UI.FontTests/
/spec
/tools
  legacy-config-importer/          # one-shot INI → JSON migration CLI, see 12-settings.md — planned, not yet built
```

Each `ScanlineStudio.Core.*` project is a bounded module: it may be extracted to its own NuGet package later without touching other modules. This is what "composition over inheritance" and SOLID look like structurally — modules compose via interfaces registered in the DI container, not via shared base classes.

## Composition root

`ScanlineStudio.Host/Program.cs` is the only place allowed to call `new` on concrete infrastructure types (transports, audio backends, protocol implementations) or to register services. Everything else receives dependencies through constructor injection — "current radio," "current settings," etc. are always resolved through DI, never through a `Program.CurrentRadio`-style global (this replaces the legacy pattern of `extern CRADIOPARA RADIO;` global structs throughout `cradio.h`/`Option.h`), with one documented exception: `App.Services` (a static `IServiceProvider?`, `ScanlineStudio.UI/App.axaml.cs`) exists because Avalonia instantiates XAML markup extensions and window code-behind itself, with no constructor-injection route available there. Sanctioned readers only: `App`'s own bootstrap resolve, `TranslateExtension`, `ViewLocator`, and window code-behind (e.g. `MainWindow`). Everything else uses constructor injection.

## Concurrency model

- All hardware I/O (serial, TCP, audio callbacks) is asynchronous (`async`/`await`, `Task`-based), never blocking the UI thread. This directly satisfies the CLAUDE.md rule "All hardware communication must be asynchronous."
- Audio callback threads (see [[05-audio-engine]]) are real-time-priority and must not allocate or call into `async` machinery; they hand samples off to lock-free ring buffers consumed by the DSP pipeline on a dedicated processing thread.
- Radio polling loops (see [[02-radio-layer]]) run on a background `Task` per connected radio, publishing state changes via `IObservable<RadioState>` (System.Reactive — decided and implemented, `RadioController`'s `StateChanges`/`ConnectionEvents`), never via raw thread + Win32 message posting (replacing `PostMessage`/`WM_*` pattern in `cradio.cpp`).
- UI updates marshal back to the UI thread via Avalonia's `Dispatcher`, applied only in the `ScanlineStudio.UI` layer.

## Error handling

- Infrastructure layers (radio, audio, serial) surface failures as typed results or exceptions specific to the operation (`RadioProtocolException`, `AudioDeviceUnavailableException`), never silent failure or error codes returned as `int`.
- The `ScanlineStudio.Application` layer translates these into user-facing, localized notifications (see [[10-localization]]) — never a raw exception message shown to the user.
- A disconnected or misbehaving radio must degrade gracefully: SSTV encode/decode and logging continue to function with radio control unavailable, mirroring "never remove existing radio support unless replaced" by never making radio control a hard dependency of the DSP/logging core.

## Nullable reference types & warnings

- `<Nullable>enable</Nullable>` and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` are set at the solution level (`Directory.Build.props`), non-negotiable per CLAUDE.md. New code must not use `!` null-forgiving operator except at documented FFI/interop boundaries (e.g. native audio backend P/Invoke).

## Definition of done for this document

- [x] `Directory.Build.props` / `.editorconfig` created enforcing nullable + warnings-as-errors + analyzers.
- [x] Empty solution scaffolded matching the project list above, each project building with zero warnings.
- [x] `ScanlineStudio.Host` boots, resolves a real (not empty) DI container, and shows a working Avalonia window — the walking-skeleton milestone from [[14-roadmap]] Phase 3, exceeded (real service graph, not a blank window).
