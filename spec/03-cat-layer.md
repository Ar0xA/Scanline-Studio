# External CAT Backends

## Related

[[02-radio-layer]] (implements `IRadioProtocol`) · sibling of → [[04-rigctld]] · replaces `cradio.cpp`/`cradio.h`, `RadioSet.cpp`, `ExtCmd.cpp`, `OmniRig_OCX.cpp`

## Decision: no hand-written per-rig CAT protocols

Yoniq does not implement per-rig CAT command sets in-house. Legacy `cradio.cpp` hand-wrote a
`Freq*` parser per rig family (Yaesu HF/VU/newer, Icom CI-V, Kenwood, Ten-Tec Omni VI, JRC JST-245) —
maintaining an equivalent set for the full range of rigs hams actually use is a large, ongoing
maintenance burden that existing external tools already carry. Instead, Yoniq is a **pure client of
external CAT backends**, the same relationship WSJT-X has to Hamlib. See
[docs/removed-features.md](../docs/removed-features.md) for the formal removal accounting.

This is a deliberate reversal of the earlier `spec/04-rigctld.md` draft, which rejected bundling
Hamlib ("that duplication is exactly what client mode avoids") on the assumption the alternative was
hand-writing protocols anyway. It wasn't — the real alternative was never writing them at all.

## Backends, in priority order

1. **Hamlib, linked in-process** (`Yoniq.Radio.Hamlib`, P/Invoke against `libhamlib`) — WSJT-X-style:
   broadest rig coverage of any option, no separate daemon for the user to run. Isolated in its own
   optional project per CLAUDE.md §4's Win32/COM/P-Invoke rule — never a dependency of
   `Yoniq.Core.*`/`Yoniq.UI`. Packaging (per-OS native binary bundling, Hamlib's own backend-ABI churn
   across versions) is an open implementation question, not yet designed.
2. **`rigctld` client** ([[04-rigctld]]) — talks to an already-running Hamlib daemon (local or
   networked) instead of linking Hamlib directly. Complementary to (1), not redundant: lets multiple
   applications share one rig through a single `rigctld` instance (the arbitration case native/linked
   CAT structurally cannot do — see [docs/removed-features.md](../docs/removed-features.md)'s OmniRig
   entry), and works when a linked-Hamlib build isn't available for the user's platform/architecture.
3. **`TemplateCatProtocol`** (fallback) — legacy `RadioSet`'s free-text `CmdInit`/`CmdRx`/`CmdTx`/
   `cmdGNR` escape hatch, kept as a last resort for a rig none of the above cover, driven by a
   user-editable JSON template (see [[12-settings]]) instead of raw C-string format specifiers. This
   is user-authored configuration, not a project-maintained protocol implementation, so it doesn't
   reopen the "no hand-written CAT code" decision above.

**Maybe later, not committed** (see [[14-roadmap]]'s Phase 4 entry — revisit once Hamlib/rigctld
coverage lands and there's real user demand, not a guess made now):

4. **flrig client** (`Yoniq.Radio.Flrig`) — XML-RPC client against a running flrig instance. flrig has
   a real, still-actively-used user base distinct from plain Hamlib/rigctld users. Wire protocol not
   yet designed.
5. **OmniRig client** (`Yoniq.Radio.OmniRig`, Windows-only optional module, COM) — talks to an
   already-running OmniRig instance as a client, the same relationship as (2)/flrig above. This is
   distinct from legacy's own OmniRig integration (bundling OmniRig's OCX/TLB into the app itself,
   rejected — see [docs/removed-features.md](../docs/removed-features.md)): here Yoniq is just another
   OmniRig client alongside a logger, which actually restores the rig-sharing arbitration that entry
   flagged as not-fully-replaced. Wire/COM interop not yet designed.

Each backend implements `IRadioProtocol` from [[02-radio-layer]] — from the rest of the app's point of
view a linked-Hamlib session, a `rigctld` connection, an flrig connection, and an OmniRig connection are
four equally-valid, interchangeable `IRadioProtocol` instances, same pattern [[04-rigctld]] already
established for `RigctldClientProtocol`.

## Transport implications

`IRadioTransport` (bytes in, bytes out — [[02-radio-layer]]) cleanly fits the TCP-based backends
(`rigctld`, flrig). Linked Hamlib and OmniRig are call-based (P/Invoke / COM), not byte-stream-based —
they manage their own transport (including, for Hamlib, the serial port itself) internally. Their
`IRadioProtocol` implementations do not use `IRadioTransport` at all; `PollAsync`/`Set*Async` call
straight into the native/COM API. This is a real asymmetry in the interface's usage, not an oversight —
noted here so a future implementer doesn't try to force a transport abstraction where none applies.

## Rig identification

Legacy's `RADIO_POLL*` enum (`cradio.h`) no longer maps to a native protocol type (see
[[02-radio-layer]]'s migration table, updated accordingly). Instead it maps to a backend + that
backend's own model identifier where one is known (e.g. a Hamlib rig-model number) — resolving the
exact mapping per legacy enum value is deferred to implementation time, since it depends on which
backend(s) actually ship first.

## Testing

Each backend client is tested against fixture responses for its own wire format (line-based text for
`rigctld`/flrig's XML-RPC, a fake P/Invoke/COM shim for linked Hamlib/OmniRig) — same
`FakeRadioTransport`-style pattern [[04-rigctld]] uses, extended with a fake native-call shim for the
two call-based backends. No legacy byte-fixture/golden-vector parity work applies here (that requirement
belonged to the hand-written-protocol plan this document replaces) — correctness of the underlying CAT
command set is the external backend's own responsibility, not this port's.

## Definition of done

- [x] `rigctld` client ([[04-rigctld]]) — `RigctldClientProtocol` implemented, fixture-tested (24 tests)
      plus real-interop-tested against Hamlib's own Dummy rig backend (4 tests). Linked Hamlib not
      started — [ ] remains open for it.
- [ ] `TemplateCatProtocol` implemented for the fallback case.
- [ ] Hamlib native-binary packaging story (per-OS bundling, versioning) documented before the linked
      backend ships.
- [ ] flrig and OmniRig client backends: design deferred, tracked in [[14-roadmap]].
