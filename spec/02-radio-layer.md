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

public interface IRadioProtocol : IAsyncDisposable
{
    string RigId { get; }                       // e.g. "hamlib-native", "rigctld-client", "flrig-client"
    RadioCapabilities Capabilities { get; }
    Task<RadioState> PollAsync(CancellationToken ct);
    Task SetFrequencyAsync(long hz, CancellationToken ct);
    Task SetModeAsync(RadioMode mode, CancellationToken ct);
    Task SetPttAsync(bool tx, CancellationToken ct);
}

public interface IRadioProtocolFactory
{
    bool CanHandle(RadioConnectionSpec spec);
    IRadioProtocol Create(RadioConnectionSpec spec);
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

`RadioConnectionSpec` is a discriminated union via an open abstract record base — only `NoneConnectionSpec` and `RigctldConnectionSpec` exist today; future backends (linked Hamlib, flrig, OmniRig-as-client — [[03-cat-layer]]) each add their own sealed subtype without requiring existing code to change, since `IRadioController` never switches on the concrete subtype itself (only each registered `IRadioProtocolFactory`'s own `CanHandle` does — see its doc comment). `IRadioController` is the single facade the `Yoniq.Application` layer talks to regardless of which subtype is active. Selecting "no radio" is a first-class, fully supported spec — SSTV and logging must work with zero radios connected (see [[01-architecture]] error handling).

## PTT usage

PTT (push-to-talk) keying doubles as a hardware capability, not just a CAT command: some setups key PTT via RTS/DTR lines on the same serial port (legacy `usePTT` flag in `CRADIOPARA`) rather than a CAT command. For the TCP-based backends (`rigctld`, flrig — [[03-cat-layer]]), PTT is a wire command like any other `SetPttAsync` call. For linked Hamlib, PTT type (CAT command vs. RTS vs. DTR) is one of Hamlib's own per-rig configuration options, selected when opening the rig — Yoniq passes it through, it does not implement RTS/DTR toggling itself.

## Rig identification

No `IRigRegistry`/`RigDefinition` of declarative per-rig entries exists — there is nothing to register, since no protocol implementation is chosen per rig (see [[03-cat-layer]]'s backend list). A rig is identified to whichever backend is active by that backend's own model identifier (e.g. a Hamlib rig-model number, or nothing at all for `rigctld`/flrig, which already know what they're connected to). This directly replaces the `RADIO_POLL*` enum in `cradio.h`; exact migration of a legacy user's saved rig selection to a specific backend + model ID is deferred to implementation time (see [[12-settings]]) since it depends on which backend(s) ship first and how completely their own model list covers legacy's.

OmniRig (`OmniRig_OCX.cpp`, Windows-only ActiveX) is **not** ported as-is — bundling its own OCX/TLB into Yoniq is rejected, superseded by [[03-cat-layer]]'s OmniRig-as-*client* backend plus [[04-rigctld]]. Unlike the native-CAT-protocol replacement originally proposed here, OmniRig-as-client actually **does** restore OmniRig's original rig-sharing-arbitration value: Yoniq becomes just another OmniRig-aware application talking to the same already-running broker as a logger or other software, the same relationship [[04-rigctld]]'s client mode has to `rigctld`. See [docs/removed-features.md](../docs/removed-features.md) for the full accounting.

## Polling

`IRadioController` implementations poll on a configurable interval (default 250 ms, matching legacy `PollInterval`) using an internal background loop; poll failures increment a backoff counter and surface via `ConnectionEvents` rather than throwing out of the polling loop. Poll type/scan behavior (`PollType`, `PollScan` in legacy `CRADIOPARA`) becomes a `PollingStrategy` enum (`Continuous`, `OnDemand`, `Scan`) on `RadioConnectionSpec`.

## Testing hooks

`IRadioTransport` and `IRadioProtocol` are the seams for testing (see [[13-testing]]): for the TCP-based backends, a `FakeRadioTransport` replays scripted byte sequences and asserts on written bytes; for the call-based backends (linked Hamlib, OmniRig), a fake native-call/COM shim plays the same role. Either way, `IRadioController` orchestration (reconnect/backoff/state fan-out) is tested against a fake `IRadioProtocol` independent of any real rig or backend.

## Definition of done

- [x] `Yoniq.Abstractions.Radio` interfaces above compiled and documented via XML doc comments.
- [x] `IRadioController` reference implementation with connect/disconnect/backoff, unit-tested against a fake `IRadioProtocol` (protocols own their own transport, so the controller itself never touches `IRadioTransport` directly — see the "Core abstractions" code above). `RadioController` (`Yoniq.Core.Radio`), 12 orchestration tests in `RadioControllerTests`.
- [x] `IRadioProtocolFactory`-based backend resolution (exactly-one-match, typed error on zero/ambiguous
      match — see "Rig identification" above) unit-tested; no static rig registry exists to load.
      `NoneRadioProtocolFactory`/`RigctldProtocolFactory` are the two concrete implementations so far.
