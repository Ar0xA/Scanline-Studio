# rigctld Integration

## Related

[[02-radio-layer]] (implements `IRadioProtocol` / provides an alternate `IRadioController`) · [[03-cat-layer]] (sibling protocol family) · new capability, no direct legacy equivalent (legacy used per-rig direct serial only, plus OmniRig as a third-party broker)

## Purpose

`rigctld` is the Hamlib daemon that speaks a simple line-based TCP protocol to control a huge range of rigs via Hamlib's own backend drivers. Supporting it natively serves two distinct use cases, both in scope:

1. **Client mode** — YONIQ controls a rig indirectly through an already-running `rigctld` (or `rigctld`-compatible) process, instead of speaking CAT directly. This instantly extends rig support to everything Hamlib supports, without YONIQ implementing every protocol itself.
2. **Server mode** — YONIQ exposes its own currently-connected radio (via [[03-cat-layer]] or client mode) as a `rigctld`-compatible TCP server, so third-party tools (loggers, digital-mode software, contest tools) already speaking Hamlib's protocol can read YONIQ's frequency/mode without a second serial connection to the rig.

Both modes reuse the `IRadioController`/`IRadioProtocol` contracts from [[02-radio-layer]] — rigctld is "just another `IRadioProtocol`" from the rest of the app's point of view.

## Client mode

```csharp
namespace Yoniq.Core.Radio.Rigctld;

public sealed class RigctldClientProtocol : IRadioProtocol
{
    public string RigId => "rigctld-client";
    public RadioCapabilities Capabilities { get; }  // negotiated via `dump_caps` on connect

    public Task<RadioState> PollAsync(IRadioTransport transport, CancellationToken ct);
    public Task SetFrequencyAsync(IRadioTransport transport, long hz, CancellationToken ct);
    // ...
}
```

Wire protocol (subset implemented first, matching Hamlib's `rigctld` "easy" command set):

| Command | Direction | Purpose |
|---|---|---|
| `f` | query | get frequency (Hz) |
| `F <hz>` | set | set frequency |
| `m` | query | get mode + passband |
| `M <mode> <passband>` | set | set mode |
| `t` | query | get PTT state |
| `T <0\|1>` | set | set PTT |
| `\dump_caps` | query | capability negotiation on connect, maps to `RadioCapabilities` flags |
| `\chk_vfo` | query | VFO support check |

Transport is a plain `TcpTransport : IRadioTransport` (host/port, default `localhost:4532`), reusing the same interface as any other radio transport — no special-casing needed in `IRadioController`.

## Server mode

`Yoniq.Core.Radio.Rigctld.RigctldServer` listens on a configurable TCP port (default `4532`, configurable to avoid clashing with a real `rigctld` also running — see [[12-settings]]) and answers the same command subset above by delegating to whatever `IRadioController` is currently active in the app (regardless of whether *that* controller is itself a CAT rig, a rigctld client, or "no radio," in which case the server reports "no rig" rather than refusing connections).

```csharp
public interface IRigctldServer
{
    Task StartAsync(int port, CancellationToken ct);
    Task StopAsync();
    bool IsRunning { get; }
}
```

Server mode is opt-in (disabled by default) and read-mostly by default: frequency/mode/PTT *set* commands from remote clients are honored only if the user has explicitly enabled "allow remote control" in settings, to avoid a surprise where a third-party tool keys PTT on a user's rig without them expecting it.

## Discovery and fallback

On connect, client mode attempts `\dump_caps` first; if it doesn't respond within a short timeout, it falls back to the Hamlib "backward compatible" line protocol (single-letter commands only, no leading `\`), covering older `rigctld` builds. This mirrors the resilience Hamlib clients generally implement.

## Non-goals

- YONIQ does not vendor or bundle Hamlib itself — client mode assumes the user (or a bundled optional installer step) runs `rigctld` separately, or points YONIQ at one on the network. Bundling Hamlib's rig backends directly is out of scope; that duplication is exactly what client mode avoids.
- Full Hamlib protocol coverage (extended command set, all `rigctl` verbs) is not a v1 goal — only the subset needed for frequency/mode/PTT, matching what YONIQ's own domain model (`RadioState`) can represent.

## Testing

- Client mode: unit-tested against a fake TCP transport replaying scripted `rigctld` responses (same `FakeRadioTransport` pattern as [[03-cat-layer]]).
- Server mode: integration-tested by running `RigctldServer` against an in-process fake `IRadioController` and asserting on raw socket responses; optionally cross-checked in CI against the real `rigctl` CLI if Hamlib is available in the build image (best-effort, not required for the test suite to pass).

## Definition of done

- [ ] Client mode implements `f`/`F`/`m`/`M`/`t`/`T`/`\dump_caps`, fixture-tested.
- [ ] Server mode implements the same subset, integration-tested via raw sockets.
- [ ] Server mode is off by default and gated by a "read-only" vs "allow remote control" setting.
- [ ] Documented interop check against at least one real Hamlib `rigctld` build (manual verification step, tracked in [[14-roadmap]]).
