# External CAT Backends

## Related

[[02-radio-layer]] (implements `IRadioProtocol`) · sibling of → [[04-rigctld]] · replaces `cradio.cpp`/`cradio.h`, `RadioSet.cpp`, `ExtCmd.cpp`, `OmniRig_OCX.cpp`

## Decision: no hand-written per-rig CAT protocols

Scanline Studio does not implement per-rig CAT command sets in-house. Legacy `cradio.cpp` hand-wrote a
`Freq*` parser per rig family (Yaesu HF/VU/newer, Icom CI-V, Kenwood, Ten-Tec Omni VI, JRC JST-245) —
maintaining an equivalent set for the full range of rigs hams actually use is a large, ongoing
maintenance burden that existing external tools already carry. Instead, Scanline Studio is a **pure client of
external CAT backends**, the same relationship WSJT-X has to Hamlib. See
[docs/removed-features.md](../docs/removed-features.md) for the formal removal accounting.

This is a deliberate reversal of the earlier `spec/04-rigctld.md` draft, which rejected bundling
Hamlib ("that duplication is exactly what client mode avoids") on the assumption the alternative was
hand-writing protocols anyway. It wasn't — the real alternative was never writing them at all.

## Backends, in priority order

1. **Hamlib, linked in-process** (`ScanlineStudio.Core.Radio.Hamlib`, P/Invoke against `libhamlib`) — broadest rig
   coverage of any option, no separate daemon for the user to run. Isolated in its own optional project
   per CLAUDE.md §4's Win32/COM/P-Invoke rule — never referenced by the cross-platform `Core.Radio`/`Application`/`UI`
   assemblies (only `ScanlineStudio.Host`, the composition root, and its own test project reference it). Packaging
   decision below ("Linked Hamlib: bring-your-own-libhamlib").
2. **`rigctld` client** ([[04-rigctld]]) — talks to an already-running Hamlib daemon (local or
   networked) instead of linking Hamlib directly. Complementary to (1), not redundant: lets multiple
   applications share one rig through a single `rigctld` instance (the arbitration case native/linked
   CAT structurally cannot do — see [docs/removed-features.md](../docs/removed-features.md)'s OmniRig
   entry), and works when a linked-Hamlib build isn't available for the user's platform/architecture.
3. **`TemplateCatProtocol`** (fallback, unimplemented — `ScanlineStudio.Core.Radio.Cat` currently
   contains no source files at all) — legacy `RadioSet`'s free-text `CmdInit`/`CmdRx`/`CmdTx`/
   `cmdGNR` escape hatch, kept as a last resort for a rig none of the above cover, driven by a
   user-editable template of raw hex command bytes (see [[12-settings]]) instead of raw C-string
   format specifiers. Per CLAUDE.md §4's binary-is-bytes rule (which names this exact template
   mechanism), the command fields themselves must be modeled as `byte[]`/`ReadOnlyMemory<byte>`, not
   string/JSON-literal, when this is built — a plain JSON-string encoding would corrupt any byte
   ≥0x80 or an embedded null, both legitimate frame content. This is user-authored configuration, not
   a project-maintained protocol implementation, so it doesn't reopen the "no hand-written CAT code"
   decision above.

**Maybe later, not committed** (see [[14-roadmap]]'s Phase 4 entry — revisit once Hamlib/rigctld
coverage lands and there's real user demand, not a guess made now):

4. **flrig client** (`ScanlineStudio.Radio.Flrig`) — XML-RPC client against a running flrig instance. flrig has
   a real, still-actively-used user base distinct from plain Hamlib/rigctld users. Wire protocol not
   yet designed.
5. **OmniRig client** (`ScanlineStudio.Radio.OmniRig`, Windows-only optional module, COM) — talks to an
   already-running OmniRig instance as a client, the same relationship as (2)/flrig above. This is
   distinct from legacy's own OmniRig integration (bundling OmniRig's OCX/TLB into the app itself,
   rejected — see [docs/removed-features.md](../docs/removed-features.md)): here Scanline Studio is just another
   OmniRig client alongside a logger, which actually restores the rig-sharing arbitration that entry
   flagged as not-fully-replaced. Wire/COM interop not yet designed.

Each backend implements `IRadioProtocol` from [[02-radio-layer]] — from the rest of the app's point of
view a linked-Hamlib session, a `rigctld` connection, an flrig connection, and an OmniRig connection are
four equally-valid, interchangeable `IRadioProtocol` instances, same pattern [[04-rigctld]] already
established for `RigctldClientProtocol`.

## Linked Hamlib: bring-your-own-libhamlib

**Decision**: Scanline Studio never builds, forks, vendors, or ships any Hamlib source or binary. At runtime,
`ScanlineStudio.Core.Radio.Hamlib` P/Invokes against whatever Hamlib the user's own OS/package manager already
has installed. This was chosen over WSJT-X's actual approach (a private Hamlib fork, statically linked
via a "superbuild" CMake step — researched directly, not assumed) on cost/maintenance grounds specific to
this project: WSJT-X pays that tax to carry patches upstream hasn't merged; Scanline Studio carries none, and a
native compile step across a 3-OS CI matrix on a constrained Actions-minutes budget isn't worth it to
replace a dependency `apt`/Homebrew/an existing Windows installer already satisfies. An
Opus `auditor` review (2026-08-04) confirmed Hamlib's public API is reachable without declaring a single
native struct in C# (`rig_get_caps_int`/`rig_get_caps_cptr`, string-token config via
`rig_set_conf`/`rig_token_lookup`), so no C shim project is needed either — unlike
[[05-audio-engine]]'s MiniAudio integration, which does need one because `ma_device`'s layout isn't
struct-free.

### Discovery order

Three tiers, each falling through to the next; the whole probe (see "Version gate" below) runs once at
startup and its result is cached for the process lifetime, not re-run per connect attempt:

1. **User-provided override** — a path the user supplies in Settings ([[12-settings]]). **The
   mechanism is plumbed but not yet exposed**: `HamlibLibraryLocator` accepts an override path
   parameter and honors it correctly, but no caller passes one yet — `Program.cs`'s own
   `HamlibProtocolFactory.Create` call doesn't wire a Settings-sourced value through, so this tier is
   currently unreachable in practice. When set, this is tried *exclusively* — it bypasses tiers 2/3
   entirely rather than being a last-resort fallback, since a user who explicitly configured a path
   wants exactly that library used, not silently substituted with whatever auto-detection happens to
   find first. This is the cheap hook that keeps a later "swap in your own compiled libhamlib" story
   alive without any packaging work now.
2. **Bare soname load** — only attempted when no override is configured. `NativeLibrary.TryLoad` against
   the platform's default candidate name, letting the OS's own dynamic linker search its normal paths:
   `libhamlib.so.4` (Linux, via the ldconfig cache), `libhamlib.4.dylib` (macOS), `hamlib-4.dll` then
   `libhamlib-4.dll` (Windows, via PATH/next-to-exe). Covers anyone who installed Hamlib through their
   platform's normal channel.
3. **Known extra install directories** — a short, hand-maintained per-OS fallback list (e.g.
   `/opt/homebrew/lib` on Apple Silicon, which Homebrew doesn't always put on the default linker path).
   No CI cost, just a wider search before giving up.

**Planned, not yet implemented**: if no tier succeeds, or the version gate below rejects what was
found, auto-resolution should demote quietly to the already-working `rigctld` client backend with a
diagnostic log line (which candidate paths were tried, and why each failed/was rejected), surfaced
in a Settings → Diagnostics view once one exists. As shipped today, `HamlibProtocolFactory.CanHandle`
unconditionally claims `HamlibConnectionSpec` regardless of whether discovery/the version gate
actually succeeded, so an unavailable Hamlib currently **always fails loudly** rather than demoting —
which happens to already satisfy the next sentence's requirement for explicit selection, but not the
"auto-resolution demotes quietly" case this paragraph describes. **Explicit user selection of Hamlib
fails loudly, today and by design** — a user who specifically picked "linked Hamlib" in Settings
should see why it didn't work, not silently end up on a different backend.

### Version gate

After a candidate library loads, probe it by calling `rig_version()` (**not** `hamlib_version2` — that
name is a `const char *` *data export*, not a function; P/Invoking it as a function delegate jumps into
`.data` and crashes, exactly the failure this probe exists to avoid — `rig_version()` is a real exported
function returning the identical string) and parsing the major version out of its
`"Hamlib <major>.<minor>.<patch> <date> <arch>"` format (split on whitespace, take token `[1]`). Only
major version **4** is accepted; anything else (including a 5.x build, since the local reference clone at
`hamlib/` is itself 5.0-dev with a confirmed deliberate ABI break — `rig_get_conf()` is removed there) is
treated as "unavailable," not crash-and-see. A null/empty result or a missing `rig_version` symbol is
caught the same way (unavailable, not fatal). This does **not** protect against a library built with Hamlib's
optional `BUILTINFUNC` flag, which silently changes the arity of `rig_set_freq`/`rig_get_freq`/
`rig_set_vfo` (an extra trailing `const char*`) at the *same* soname with no detectable version signal —
documented as a known, accepted gap (an exotic non-default Hamlib build), not something the version gate
can catch.

### Frozen P/Invoke surface

Pinned against **released Hamlib 4.x headers**, not the local 5.0-dev reference clone. Bound at the
non-`BUILTINFUNC` (default-build) arity per the risk above.

| Function | Signature (C) | Notes |
|---|---|---|
| `rig_init` | `RIG *rig_init(rig_model_t)` | `rig_model_t` = `uint32` |
| `rig_open` / `rig_close` / `rig_cleanup` | `int f(RIG *)` | |
| `rig_token_lookup` | `hamlib_token_t f(RIG *, const char *)` | resolves e.g. `"rig_pathname"` |
| `rig_set_conf` | `int f(RIG *, hamlib_token_t, const char *)` | serial port path, baud, etc. |
| `rig_set_freq` / `rig_get_freq` | `int f(RIG *, vfo_t, freq_t [, freq_t *])` | `freq_t` = `double`. These three (plus `rig_set_vfo`) are the only functions with a `BUILTINFUNC` arity variant — bind the default (non-`BUILTINFUNC`) arity above. |
| `rig_set_mode` / `rig_get_mode` | `int f(RIG *, vfo_t, rmode_t, pbwidth_t)` / `int f(RIG *, vfo_t, rmode_t *, pbwidth_t *)` | `rmode_t` = `ulong` (bit-flag type, but `rig_get_mode` always returns exactly one flag — compare by exact value, never decompose bits). No `BUILTINFUNC` variant exists for these. |
| `rig_set_ptt` / `rig_get_ptt` | `int f(RIG *, vfo_t, ptt_t)` / `int f(RIG *, vfo_t, ptt_t *)` | `ptt_t` = `int` (C enum); `vfo_t` = `uint`. No `BUILTINFUNC` variant. |
| `rig_version` | `const char *f(void)` | version-gate probe — **not** `hamlib_version2`, which is a data export, not a function (see "Version gate" above). Return marshaled as `nint`, read via `Marshal.PtrToStringUTF8`, never declared as a `string` return (the marshaler would try to free Hamlib's static string). |
| `rig_get_level` | `int f(RIG *, vfo_t, setting_t, value_t *)` | Added for [[14-roadmap]]'s Piece 6 (SWR/ALC/power/S-meter telemetry). `setting_t` is `typedef uint64_t setting_t` (verified directly against rig.h) — a plain fixed-width `ulong`, **not** `CLong` (unlike `pbwidth_t`/`hamlib_token_t` above, `setting_t` has no 32-vs-64-bit platform ambiguity to guard against). `value_t` is a C union (largest arm: a nested `{int l; unsigned char *d;}` struct, 16 bytes on both LP64/LLP64) — marshaled as a `[StructLayout(LayoutKind.Explicit, Size = 16)]` struct exposing **both** arms at offset 0 (`float FloatValue`, `int IntValue`), because the levels this project reads are NOT uniformly float-typed: `RIG_LEVEL_SWR`/`RIG_LEVEL_ALC`/`RIG_LEVEL_RFPOWER_METER` are documented "arg float," but `RIG_LEVEL_STRENGTH` (S-meter) is documented "arg int" — reading the wrong union arm for a given level silently reinterprets raw bytes with no compiler-catchable failure. Two call-site methods on the frozen seam pick the right arm per level: `RigGetLevel` (float) and `RigGetLevelInt` (int). `rig_get_level` is outside every `BUILTINFUNC` block, so its arity is stable. |

Every declaration uses `CallingConvention.Cdecl`. **`pbwidth_t` and `hamlib_token_t` are marshaled as
`System.Runtime.InteropServices.CLong`, never a C# `long`** — both are C `signed long`/`long`, which is
64-bit on Linux/macOS (LP64) but 32-bit on Windows (LLP64); a bare `long` marshal is silently wrong on
Windows only, exactly the "works everywhere except the one platform nobody tested" failure shape a
hobby project with no Windows dev box is worst-positioned to catch.

### `IHamlibNative` seam

`ScanlineStudio.Core.Radio.Hamlib` never calls `DllImport`-style static P/Invoke directly from
`HamlibRadioProtocol`. An internal `IHamlibNative` interface wraps the frozen surface above;
`HamlibNative` is the real implementation (resolves the library per "Discovery order," caches delegates
via `NativeLibrary.GetExport`); a `FakeHamlibNative` — the "fake native-call shim" this document's own
Testing section below already promises — backs unit tests without a real Hamlib install. This is the seam that
makes `HamlibRadioProtocol` testable at all; a static `DllImport` surface is not injectable, so this
decision has to precede writing `HamlibRadioProtocol` itself, not follow it.

### Threading contract

Hamlib's C API is not thread-safe per `RIG *` handle, and `rig_*` calls block on serial I/O for up to
hundreds of milliseconds. `RadioController`'s poll loop (`PollAsync`, on its own background task) and a
caller's `Set*Async` calls (on whatever thread invokes `IRadioController`) can both reach the same
`HamlibRadioProtocol` instance concurrently — so `HamlibRadioProtocol` must serialize every native call
against one `RIG *` handle itself (e.g. a private single-concurrency work queue or
`SemaphoreSlim(1)` guarding each `IHamlibNative` call), matching CLAUDE.md §4's concurrency-contract rule.
This is *not* inherited from `RadioController`/`RigctldClientProtocol` — it has no equivalent shared
mutable native handle, so this contract is new to this backend and must be stated explicitly, not assumed
transferable.

### License provenance

Hamlib's library is LGPL-2.1-or-later — compatible with, but distinct from, Scanline Studio's own
LGPL-3.0-or-later. No Hamlib source, binary, or header-derived data table (e.g. `riglist.h` rig-model
numbers — Scanline Studio has no `IRigRegistry`, so none is copied, per [[02-radio-layer]]'s "Rig identification")
is bundled; `LICENSES.md` gets a runtime-dependency disclosure row, not a bundled-asset row.

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
      plus real-interop-tested against Hamlib's own Dummy rig backend (4 tests).
- [x] Hamlib packaging story designed — "bring-your-own-libhamlib" (above), decided 2026-08-04 after an
      Opus `auditor` review of 4 candidate approaches plus researched WSJT-X precedent.
- [x] Linked Hamlib implemented (`ScanlineStudio.Core.Radio.Hamlib`) — `HamlibRadioProtocol`/`IHamlibNative`/
      `HamlibNative`/`HamlibLibraryLocator`/`HamlibVersionGate`/`HamlibRuntime`/`HamlibProtocolFactory`,
      2 rounds of `auditor` plan-review before any code (4 blockers found and resolved on paper each
      round — see the implementation plan, `/home/artien/.claude/plans/temporal-launching-valiant.md`),
      40 fixture/fake-driven unit tests plus 4 real-interop tests against a real system-installed
      `libhamlib` (this dev machine has 4.5.5) driving Hamlib's own hardware-free Dummy rig backend —
      the whole discovery→version-gate→P/Invoke pipeline verified against genuine native code, not just
      fakes. `TemplateCatProtocol` fallback and Application-layer cross-backend demotion still open
      (see "Explicitly out of scope" in the implementation plan).
- [ ] `TemplateCatProtocol` implemented for the fallback case.
- [ ] flrig and OmniRig client backends: design deferred, tracked in [[14-roadmap]].
