# Radio Layer

## Related

[[01-architecture]] · consumed by → [[03-cat-layer]], [[04-rigctld]] · replaces `cradio.cpp`/`cradio.h`, `RadioSet.cpp`, `OmniRig_OCX.cpp`

## Purpose

Define the hardware- and protocol-agnostic abstraction that everything above it (UI, logbook, SSTV session) programs against. Nothing outside `Yoniq.Core.Radio` may know whether a rig is reached over a serial CAT cable, TCP, or rigctld — that detail lives entirely in [[03-cat-layer]] and [[04-rigctld]].

## Why a new abstraction is needed

The legacy `CCradio` (`cradio.h`) mixes three concerns in one `TThread` subclass: transport (Win32 `HANDLE`/`DCB` serial config), protocol framing (per-rig `Freq*` parser methods switched on a `PollType` enum), and UI notification (`PostMessage` to a window handle). This makes adding a rig mean editing a single 1000+ line file and makes the radio layer untestable without real hardware. The rewrite separates these into three independently testable layers:

1. **Transport** — bytes in, bytes out (this document)
2. **Protocol** — bytes ↔ typed radio state ([[03-cat-layer]], [[04-rigctld]])
3. **Orchestration** — connection lifecycle, polling cadence, capability negotiation (this document)

## Core abstractions

```csharp
namespace Yoniq.Abstractions.Radio;

public enum RadioMode { Lsb, Usb, Cw, CwR, Am, Fm, Rtty, RttyR, Data, DataR, Pkt, Unknown }

public readonly record struct RadioState(
    long FrequencyHz,
    RadioMode Mode,
    bool IsTransmitting,
    int? SignalStrengthDb,
    DateTimeOffset ObservedAt);

public interface IRadioTransport : IAsyncDisposable
{
    Task OpenAsync(CancellationToken ct);
    Task CloseAsync();
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    IAsyncEnumerable<byte> ReadAsync(CancellationToken ct);
    bool IsOpen { get; }
}

public interface IRadioProtocol
{
    string RigId { get; }                       // e.g. "yaesu-ft-991a"
    RadioCapabilities Capabilities { get; }
    Task<RadioState> PollAsync(IRadioTransport transport, CancellationToken ct);
    Task SetFrequencyAsync(IRadioTransport transport, long hz, CancellationToken ct);
    Task SetModeAsync(IRadioTransport transport, RadioMode mode, CancellationToken ct);
    Task SetPttAsync(IRadioTransport transport, bool tx, CancellationToken ct);
}

[Flags]
public enum RadioCapabilities
{
    None = 0,
    ReadFrequency = 1 << 0,
    SetFrequency  = 1 << 1,
    ReadMode      = 1 << 2,
    SetMode       = 1 << 3,
    PttControl    = 1 << 4,
    SignalMeter   = 1 << 5,
}

public interface IRadioController
{
    RadioState? LastKnownState { get; }
    RadioCapabilities Capabilities { get; }
    IObservable<RadioState> StateChanges { get; }
    IObservable<RadioConnectionEvent> ConnectionEvents { get; }

    Task ConnectAsync(RadioConnectionSpec spec, CancellationToken ct);
    Task DisconnectAsync();
    Task SetFrequencyAsync(long hz, CancellationToken ct);
    Task SetModeAsync(RadioMode mode, CancellationToken ct);
    Task SetPttAsync(bool tx, CancellationToken ct);
}
```

`RadioConnectionSpec` is a discriminated union (via an abstract record with subtypes `SerialCatConnectionSpec`, `RigctldConnectionSpec`, `NoneConnectionSpec`) describing *how* to reach the rig; `IRadioController` is the single facade the `Yoniq.Application` layer talks to regardless of which subtype is active. Selecting "no radio" is a first-class, fully supported spec — SSTV and logging must work with zero radios connected (see [[01-architecture]] error handling).

## PTT usage

PTT (push-to-talk) keying doubles as a hardware capability, not just a CAT command: some setups key PTT via RTS/DTR lines on the same serial port (legacy `usePTT` flag in `CRADIOPARA`) rather than a CAT command. `IRadioTransport` for serial exposes an optional `ISerialLineControl` (RTS/DTR toggle) that `IRadioProtocol` implementations may use instead of `SetPttAsync` framing, selected per rig definition.

## Rig registry

Each supported rig is described by a declarative `RigDefinition` (id, display name, manufacturer, default baud rate, supported capability flags, protocol implementation type) collected into an `IRigRegistry`. This directly replaces the `RADIO_POLL*` enum in `cradio.h`. Concrete protocol registrations live in [[03-cat-layer]]; this layer only defines the registry contract:

```csharp
public interface IRigRegistry
{
    IReadOnlyList<RigDefinition> All { get; }
    RigDefinition? Find(string rigId);
}
```

Migration mapping from the legacy `RADIO_POLL*` enum (kept 1:1 so existing users' saved rig selection can be migrated automatically — see [[12-settings]]):

| Legacy enum | New `rigId` |
|---|---|
| `RADIO_POLLYAESUHF` | `yaesu-hf-legacy` |
| `RADIO_POLLYAESUVU` | `yaesu-vu-legacy` |
| `RADIO_POLLICOM` / `RADIO_POLLICOMN` | `icom-civ`, `icom-civ-negative` |
| `RADIO_POLLOMNIVI` / `RADIO_POLLOMNIVIN` | `ten-tec-omni-vi`, `ten-tec-omni-vi-negative` |
| `RADIO_POLLKENWOOD` / `RADIO_POLLKENWOODN` | `kenwood`, `kenwood-negative` |
| `RADIO_POLLFT1000D`, `RADIO_POLLFT920`, `RADIO_POLLFT9000`, `RADIO_POLLFT2000`, `RADIO_POLLFT950`, `RADIO_POLLFT450` | `yaesu-ft1000d`, `yaesu-ft920`, `yaesu-ft9000`, `yaesu-ft2000`, `yaesu-ft950`, `yaesu-ft450` |
| `RADIO_POLLJST245` / `RADIO_POLLJST245N` | `jrc-jst245`, `jrc-jst245-negative` |

OmniRig (`OmniRig_OCX.cpp`, Windows-only ActiveX) is **not** ported as-is — it is superseded by native CAT protocol implementations plus native [[04-rigctld]] support for the single-application-controls-the-rig case. This is a *partial*, not full, replacement, and CLAUDE.md's removal rule requires saying so precisely: OmniRig's actual distinguishing feature was **rig-sharing arbitration** — letting several applications (e.g. YONIQ and a separate logger) share one serial-connected rig through a single broker process, without each app fighting to open the same COM port. Native CAT cannot replicate this at all (two OS processes cannot open the same serial port concurrently). [[04-rigctld]] replicates it *only if every application on the machine is reconfigured* to talk through the same `rigctld` instance instead of opening the port directly — a real, user-visible migration step, not a transparent swap. `Config.cfg` in the legacy tree (`omnirig=0`) shows this was a user-facing toggle, i.e. real users depend on it today. See [docs/removed-features.md](../docs/removed-features.md) for the full accounting and the recommended user migration path.

## Polling

`IRadioController` implementations poll on a configurable interval (default 250 ms, matching legacy `PollInterval`) using an internal background loop; poll failures increment a backoff counter and surface via `ConnectionEvents` rather than throwing out of the polling loop. Poll type/scan behavior (`PollType`, `PollScan` in legacy `CRADIOPARA`) becomes a `PollingStrategy` enum (`Continuous`, `OnDemand`, `Scan`) on `RadioConnectionSpec`.

## Testing hooks

`IRadioTransport` and `IRadioProtocol` are the seams for testing (see [[13-testing]]): a `FakeRadioTransport` replays scripted byte sequences and asserts on written bytes, allowing every rig's `Freq*`-equivalent parser to be unit tested without hardware, and allowing `IRadioController` orchestration (reconnect/backoff/state fan-out) to be tested against a fake protocol independent of any real rig.

## Definition of done

- [ ] `Yoniq.Abstractions.Radio` interfaces above compiled and documented via XML doc comments.
- [ ] `IRadioController` reference implementation with connect/disconnect/backoff, unit-tested against a fake transport + fake protocol.
- [ ] Rig registry loads from a static list, resolvable by legacy-migrated `rigId`.
