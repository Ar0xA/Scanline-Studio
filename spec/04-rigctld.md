# rigctld Integration

## Related

[[02-radio-layer]] (implements `IRadioProtocol` / provides an alternate `IRadioController`) · [[03-cat-layer]] (sibling protocol family) · new capability, no direct legacy equivalent (legacy used per-rig direct serial only, plus OmniRig as a third-party broker)

## Purpose

`rigctld` is the Hamlib daemon that speaks a simple line-based TCP protocol to control a huge range of rigs via Hamlib's own backend drivers. Supporting it as a **client** serves the use case in scope:

- **Client mode** — Scanline Studio controls a rig indirectly through an already-running `rigctld` (or `rigctld`-compatible) process, instead of speaking CAT directly. This instantly extends rig support to everything Hamlib supports, without this project implementing every protocol itself.

This reuses the `IRadioController`/`IRadioProtocol` contracts from [[02-radio-layer]] — rigctld is "just another `IRadioProtocol`" from the rest of the app's point of view.

**Server mode is out of scope, dropped (not deferred) — direct user decision, 2026-08-05.** An earlier
draft of this spec planned a `RigctldServer` exposing Scanline Studio's own currently-connected radio as
a `rigctld`-compatible TCP server, for third-party tools to share the rig without a second serial
connection. Explicit call: linked Hamlib ([[03-cat-layer]]) plus rigctld client-mode coverage is
sufficient CAT surface for this project; a server role is a distinct, larger commitment (a listening
network service, an "allow remote control" security posture, its own test/interop burden) not worth
carrying for a use case with no expressed demand. See the "Server mode" section below for what was
speced and dropped, and [docs/removed-features.md](../docs/removed-features.md)'s MMlink entry, which
previously named this as a possible alternative and has been corrected.

## Client mode

```csharp
namespace ScanlineStudio.Core.Radio.Rigctld;

public sealed class RigctldClientProtocol : IRadioProtocol, IAsyncDisposable
{
    public string RigId => "rigctld-client";
    public RadioCapabilities Capabilities { get; }  // negotiated by probing f/m/t on connect — see below

    public Task<RadioState> PollAsync(CancellationToken ct);
    public Task SetFrequencyAsync(long hz, CancellationToken ct);
    // ... (no IRadioTransport parameter — this protocol owns its transport internally, per
    // [[02-radio-layer]]'s interface; constructed with a TcpTransport by RigctldProtocolFactory)
}
```

Wire protocol (subset implemented first, matching Hamlib's `rigctld` "easy"/backward-compatible command
set — line-based, `\n`-terminated, verified directly against Hamlib's own `tests/rigctl_parse.c` in a
local reference clone, not just prose):

| Command | Direction | Purpose |
|---|---|---|
| `f` | query | get frequency (Hz) |
| `F <hz>` | set | set frequency |
| `m` | query | get mode + passband |
| `M <mode> <passband>` | set | set mode |
| `t` | query | get PTT state |
| `T <0\|1>` | set | set PTT |
| `\chk_vfo` | query | VFO support check |

**Response framing** (source-verified — this was flagged as the single highest-risk unknown in an
earlier plan-review pass, resolved by reading `rigctl_parse.c` directly rather than reasoning about it):
`get` commands (`f`/`m`/`t`) reply with their raw value line(s) only on success — **no** trailing
`RPRT` line (Hamlib's dispatcher only emits `RPRT` for commands *without* its internal `ARG_OUT` flag,
which every getter in this subset has). `set` commands (`F`/`M`/`T`) always reply `RPRT 0` on success.
**Both** get and set commands reply `RPRT -<n>` (a single line, negative code) on any error, replacing
the normal success output entirely. Exact per-command shape:

| Command | Success response | Error response |
|---|---|---|
| `f` | one line: integer Hz | `RPRT -<n>` |
| `F <hz>` | `RPRT 0` | `RPRT -<n>` |
| `m` | two lines: mode token (e.g. `USB`, `FM`, `PKTUSB`), then integer passband Hz | `RPRT -<n>` |
| `M <mode> <passband>` | `RPRT 0` | `RPRT -<n>` |
| `t` | one line: integer PTT state (`0`=off, `1`=on, `2`/`3`=mic/data-keyed variants — treat any nonzero as transmitting, never match the literal `"1"`) | `RPRT -<n>` |
| `T <0\|1>` | `RPRT 0` | `RPRT -<n>` |

A response's *shape* (does the first line look like an `RPRT` line or a value?) is what the parser
branches on, not a fixed line count per command — this is what makes get vs. set framing genuinely
different, not a simplification.

Mode tokens are Hamlib's own (`USB`, `LSB`, `CW`, `CWR`, `AM`, `FM`, `RTTY`, `RTTYR`, `PKTUSB`,
`PKTLSB`, `PKTFM`, …) and are not 1:1 with [[02-radio-layer]]'s `RadioMode` enum (`Data`/`DataR`/`Pkt`
in particular need an explicit mapping decision, not an assumed correspondence) — an unrecognized-but-
valid token maps to `RadioMode.Unknown`, never throws.

Transport is a plain `TcpTransport : IRadioTransport` (host/port, default `localhost:4532`), owned
internally by `RigctldClientProtocol` (see [[02-radio-layer]] — `IRadioProtocol` no longer takes a
transport parameter, each protocol owns its own).

## Server mode (dropped — kept here only as a record of what was speced)

The dropped design: `ScanlineStudio.Core.Radio.Rigctld.RigctldServer` would have listened on a
configurable TCP port (default `4532`) and answered the same command subset above by delegating to
whatever `IRadioController` is currently active in the app. It would have been opt-in (disabled by
default) and read-mostly by default: frequency/mode/PTT *set* commands from remote clients honored
only if the user explicitly enabled "allow remote control," to avoid a surprise where a third-party
tool keys PTT on a user's rig without them expecting it. None of this was implemented — see the
"Purpose" section above for the drop decision.

## Discovery and capability negotiation

**Amended after plan review — no longer `\dump_caps`-first.** The original plan probed `\dump_caps` on
connect and fell back to the backward-compatible line protocol on timeout. Reversed: `\dump_caps`'s
output is a long human-readable block whose exact format has drifted across Hamlib versions, is fragile
to parse by construction, and its grammar can't be verified without a real running instance.

Instead, `RigctldClientProtocol` speaks the backward-compatible single-letter protocol unconditionally
(it's the only thing this document specifies a verified wire format for — see the framing table above)
and negotiates capabilities by **probing**: issuing `f`, `m`, and `t` once at connect and setting the
corresponding `RadioCapabilities` flags based on which ones return a value vs. an `RPRT` error, rather
than parsing a capability-description blob. This is version-independent, uses only commands already
fixture-tested, and is fully testable without a real `rigctld` to check grammar against.

`\dump_caps` may still be issued opportunistically for its rig display name (informational UI use only)
but is never load-bearing for `RadioCapabilities` or connection success.

## Relationship to linked Hamlib

This document previously rejected bundling Hamlib directly, on the assumption that the alternative was
hand-writing per-rig protocols — reversed, see [[03-cat-layer]]. Scanline Studio now also supports Hamlib linked
in-process (P/Invoke against a system-installed `libhamlib` — "bring-your-own-libhamlib," **not**
WSJT-X's actual approach of statically linking a private Hamlib fork, see [[03-cat-layer]]'s "Linked
Hamlib" section for why that shape was rejected for this project) as a separate backend, **done** (see
[[14-roadmap]]). The two are complementary, not redundant:
`rigctld` client mode lets multiple applications share one rig through a single daemon (an arbitration
case linked-in-process CAT structurally cannot do — two processes can't open the same serial port), and
works without a platform-specific linked-Hamlib build; linked Hamlib avoids requiring the user to run a
separate daemon at all. Both remain in scope; which one a given user picks is a deployment choice, not
an architectural one.

## Telemetry (SWR/ALC/power meters)

[[14-roadmap]]'s Piece 6 needed live PWR/ALC/SWR readout for a TX safety cutoff — the one deliberate
exception to the extended-command Non-goal below. `ProbeCapabilitiesAsync` additionally probes `l
SWR`/`l ALC`/`l RFPOWER_METER` once at connect, same RPRT-error-means-absent convention as `f`/`m`/`t`.
Verified directly against a local Hamlib clone's `tests/rigctl_parse.c`
(`declare_proto_rig(get_level)`): rigctld always runs with `interactive=1`/`prompt=0`, so a supported
float-typed level (SWR/ALC/RFPOWER_METER are all in `RIG_LEVEL_FLOAT_LIST`, `rig.h`) responds with
exactly one `%g` line — the same single-line shape as `f`/`m`/`t`, reusing the same read helpers.

Meters are read only while the same poll's own `t` readback shows the rig transmitting (a real
Hamlib meter reading is TX-only and meaningless at RX time; gating avoids doubling every poll's
round-trip count for a reading nobody looks at outside an active transmit). A per-meter read failure
(RPRT error, or an unparseable line) yields `null` for that field only — it must never abort the
whole poll the way a bad `f` response does, since meters are far more likely than `f`/`m`/`t` to
intermittently error. Meter values parse with `CultureInfo.InvariantCulture` (a real bug caught
before shipping: the culture-sensitive default overload would parse "1.5" as 15 under a culture
where '.' is a thousands separator, turning a normal SWR reading into an instant false cutoff trip).
SWR's documented range is "0.0 ... infinite" (`rig.h`) — a literal `"inf"` response parses as
`float.PositiveInfinity` (a real, cutoff-worthy value), not a parse failure; `"nan"` maps to `null`
(not a known-bad direction).

This is a convenience backstop riding the existing ~250ms poll cadence plus a PTT-off round trip —
never a substitute for the rig's own hardware SWR protection.

## Non-goals

- Full Hamlib protocol coverage (extended command set, all `rigctl` verbs) beyond the `l SWR`/`l ALC`/`l RFPOWER_METER` telemetry probes above is not a v1 goal — only the subset needed for frequency/mode/PTT/telemetry, matching what Scanline Studio's own domain model (`RadioState`) can represent.

## Testing

- Client mode: unit-tested against a fake TCP transport replaying scripted `rigctld` responses (same `FakeRadioTransport` pattern as [[03-cat-layer]]), covering both the value-response and `RPRT`-error-response shapes for every command.
- Client mode, real interop (best-effort, upgraded from a manual checklist item): if Hamlib's own hardware-free "Dummy" rig backend is available (a local Hamlib reference clone plus a `rigctld` binary on PATH or built from it), an integration test runs a real `rigctld` against it and drives `RigctldClientProtocol` over a real loopback socket both directions — this is what actually retires the "unverified-grammar" caveat on the fixtures above, not just documentation. Gated to skip cleanly, not fail, when unavailable.

## Definition of done

- [x] Client mode implements `f`/`F`/`m`/`M`/`t`/`T`, fixture-tested (both response shapes per command) —
      `RigctldClientProtocol`/`RigctldProtocolFactory` (`ScanlineStudio.Core.Radio.Rigctld`), 35 fixture tests in
      `RigctldClientProtocolTests` (20 original + 15 covering the `l SWR`/`l ALC`/`l RFPOWER_METER`
      telemetry probes: full-capability reads, RX-time gating, per-meter failure isolation,
      infinity/invariant-culture parsing). `\chk_vfo`/VFO support is not implemented — not needed by
      `RadioState`'s current domain model, left for a future pass if a real need shows up.
- [x] Real interop verified against a real Hamlib `rigctld` build — automated via
      `RigctldDummyRigIntegrationTests` (4 tests, real `rigctld -m 1` against Hamlib's own hardware-free
      Dummy rig backend, best-effort/skips cleanly if `rigctld` isn't installed). Confirmed the exact
      response-framing rule documented above by hand before writing the test (real `rigctld` output
      matched the source-derived prediction exactly on the first try).
