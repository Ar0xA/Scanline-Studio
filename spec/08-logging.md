# Logging (QSO Logbook)

## Related

[[02-radio-layer]] (frequency/mode auto-fill) · [[07-image-pipeline]] (linked RX images) · replaces `Hamlog5.cpp`/`Hamlog5.h`, `LogFile.cpp`, `LogList.cpp`, `LogPic.cpp`, `LogConv.cpp`, `LogSet.cpp`, `country.cpp`/`country.h`, `qrzcom.cpp` — **not** `Loglink.cpp`, see the dedicated note below and [docs/removed-features.md](../docs/removed-features.md)

Note: this document is about the **QSO log** (contacts made), distinct from application diagnostic logging covered under "Logging (app diagnostics)" in [[01-architecture]].

## Purpose

Record contacts, import/export in industry-standard formats, and provide callsign/country lookup — matching legacy `Hamlog5`'s role, without its bespoke binary log format.

## Storage

QSO records are stored in SQLite (`Microsoft.Data.Sqlite`), the same embedded-database choice as [[07-image-pipeline]]'s RX history (and in fact the same database file, so a QSO row can foreign-key to a received-image row directly). This replaces the legacy proprietary `.MDT`/log binary formats (`TEMP.MDT`, `TEMP_BAK.MDT`) with something any external tool can also read via standard SQL, while ADIF remains the interchange format for other logging software.

```csharp
namespace ScanlineStudio.Abstractions.Logbook;

public sealed record QsoRecord(
    string Id,
    string Callsign,
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc,
    long? FrequencyHz,
    RadioMode? Mode,
    string? SstvModeId,
    string? RstSent,
    string? RstReceived,
    string? Name,
    string? Qth,
    string? GridSquare,
    string? Country,           // resolved via ICallsignLookup at entry time
    string? Notes,
    string? ReceivedImageId);  // FK into IReceiveHistoryStore, see 07-image-pipeline.md

public interface ILogbookRepository
{
    Task<QsoRecord> AddAsync(QsoRecord record, CancellationToken ct);
    Task UpdateAsync(QsoRecord record, CancellationToken ct);
    Task<IReadOnlyList<QsoRecord>> SearchAsync(LogbookQuery query, CancellationToken ct);
}
```

## ADIF import/export

`IAdifImporter`/`IAdifExporter` handle the [ADIF](https://adif.org) format — the actual interoperability need behind legacy `LogConv.cpp`'s conversion routines. Export produces standard ADIF 3.x; import accepts ADIF from any external logger, mapping fields onto `QsoRecord` with unmapped/nonstandard fields preserved in a `Notes`/raw-fields bag rather than silently dropped.

## ADIF UDP forwarding (real-time, to external logging software)

**Shipped 2026-08-15**, superseding an earlier GridTracker-only design (2026-08-07). Rather than
this app becoming a full-featured logbook, logged QSOs are forwarded in real time to whatever
external "expert" logging software the operator already uses — GridTracker2, N1MM Logger+,
Log4OM, or anything else — over UDP, using the WSJT-X network protocol's own `LoggedADIF` message
(a small binary header — magic/schema/type — followed by a client-id string and a complete
single-QSO ADIF-text payload, the same text `IAdifExporter` produces for one record). This is the
one wire format all of the above already listen for, verified via web research rather than
assumed, so this is genuinely one encoder fanned out to N destinations, not three separate
protocol integrations:

```csharp
namespace ScanlineStudio.Abstractions.Logbook;

public sealed record AdifUdpDestination
{
    public bool? Enabled { get; init; }
    public string? Name { get; init; }
    public string? Host { get; init; }
    public int? Port { get; init; }
}

public sealed record AdifUdpSendResult(int SentCount, int EnabledCount);

public interface IAdifUdpStreamer
{
    Task<AdifUdpSendResult> SendLoggedQsoAsync(string adifText, CancellationToken ct = default);
}
```

Best-effort, per destination — one slow/unreachable/misconfigured destination never blocks another
or blocks the QSO from being persisted locally; each destination gets its own 2-second timeout and
its own log line on success/failure. No pre-populated destinations (opt-in, off by default, no
silent UDP traffic to a service the user never configured) — the Options dialog's "Forwarding" tab
is a plain list-editable set of `{Enabled, Name, Host, Port}` rows.

**Settings migration**: an existing user's already-working single-destination GridTracker config
(the pre-2026-08-15 shape) is auto-seeded as one destination row the first time the new
multi-destination section is read, keyed on the new settings-section being genuinely *absent* (not
merely empty — an explicitly-saved empty destination list must stay empty, never re-migrate).
`ClientId` (the WSJT-X protocol's own "who is this" field, one value shared by every destination)
has no dedicated Options-dialog control and is preserved across saves via read-modify-write, the
same "no dialog control yet, preserve as-is" pattern this project already uses for
`SstvDecoderSettings.AfcEnabled`.

Deliberately NOT in scope: per-service branded settings or auto-detection (the destination list is
generic on purpose — see `IAdifUdpStreamer`'s own doc comment), retrying a failed send, or
per-destination failure detail surfaced in the UI (`AdifUdpSendResult.SentCount`/`EnabledCount` is
the full UI-facing granularity — per-destination detail lives in the log only).

## Loglink (Turbo HAMLOG live IPC) — dropped, not replaced

`Loglink.cpp` is **not** superseded by the SQLite logbook above, and an earlier draft of this document incorrectly implied it was. Loglink is live inter-process communication (`WM_COPYDATA`) with Turbo HAMLOG, a separate third-party Windows application — a different feature from *having* a logbook: it's about keeping someone else's already-running logbook synced in real time. The rewrite's ADIF import/export is a **batch** integration, not a live one; there is no v1 plan to replicate `WM_COPYDATA`-style live sync with a third-party logger, and it wouldn't be cross-platform even if there were. See [docs/removed-features.md](../docs/removed-features.md) for the full accounting and the recommended path for affected users (switch to Scanline Studio's own logbook, or periodic ADIF export/import instead of live sync).

## Callsign / country lookup

`country.cpp`'s `CCountry` class did a linear/binary lookup against a compiled-in prefix table (`CTL[CTMAX]`) to resolve a callsign prefix to country/continent/timezone. Rewritten as:

```csharp
public interface ICallsignLookup
{
    CallsignInfo? Resolve(string callsign);
}

public sealed record CallsignInfo(string Country, string Continent, TimeSpan UtcOffset, string? CqZone, string? ItuZone);
```

backed by a bundled, versioned prefix table (JSON, sourced from a maintained public callsign-prefix dataset such as Clublog's `cty.dat` — table itself, not code, so it can be refreshed independently of app releases). `ARRL.DX` (present in the legacy tree) is **not** used as the seed data source: ARRL data carries its own usage restrictions independent of MMSSTV's LGPL code license and was never itself covered by that license header — see [LICENSES.md](../LICENSES.md) for the full reasoning. Any prefix table actually bundled must get its own entry in `LICENSES.md` before it ships, per CLAUDE.md's license-audit rule.

Pre-checked Clublog's actual terms (see [LICENSES.md](../LICENSES.md)'s "Candidate future asset" note): no fee, but redistribution requires a human to first email Clublog's helpdesk with the proposed use and obtain an individual API key — this is a real Phase-4 blocker step, not just a license-text formality, so budget time for it before implementing `ICallsignLookup`.

## QRZ.com lookup (optional, online)

Legacy `qrzcom.cpp` integrated with QRZ.com's XML lookup API for enriching QSO records (name, address, etc.) from a callsign. This is kept as an **optional**, explicitly opt-in `IOnlineCallsignLookup` decorator over `ICallsignLookup`, disabled by default (respecting the project's "no cloud dependencies" non-goal from [[00-project-overview]] as a default posture) — when the user supplies their own QRZ credentials and opts in, it augments rather than replaces the offline prefix-table lookup.

**Shipped, under different names than this section's draft**: the offline `ICallsignLookup` base doesn't exist yet (see "Callsign / country lookup" above), so what actually shipped is a standalone `IQrzCallsignLookup` (`src/ScanlineStudio.Abstractions/Logbook/IQrzCallsignLookup.cs`, username/password auth against QRZ's XML Callbook API), not a decorator over `ICallsignLookup`. It is opt-in/off-by-default via `QrzLookupSettings.Enabled`, matching this section's intent. A separate, also-shipped `IQrzLogbookUploader` pushes a logged QSO to the user's own QRZ Logbook via API-key auth — a different QRZ product from this section's read-only enrichment lookup.

## Auto-fill from radio and DSP state

When a QSO is started from the UI, `FrequencyHz`/`Mode` default-populate from the current `IRadioController.LastKnownState` ([[02-radio-layer]]) if a radio is connected, and `SstvModeId` defaults from the mode last used by [[06-sstv-dsp]] — both remain user-editable, and both are optional (logging must work with no radio connected, consistent with [[01-architecture]]'s "radio control is never a hard dependency" rule).

## Testing

- `ILogbookRepository` tested against a temp SQLite file: add/update/search round-trips.
- ADIF import/export round-tripped against real-world ADIF fixture files (including at least one file exported by a well-known third-party logger, to catch real-world quirks beyond the spec).
- `ICallsignLookup` tested against a table of known prefixes with expected country/zone results, including edge cases (portable/maritime prefixes, reassigned prefixes).

## Definition of done

- [x] SQLite-backed `ILogbookRepository` implemented and unit-tested (`SqliteLogbookRepository`, add/update/search round-trips in `SqliteLogbookRepositoryTests`).
- [ ] ADIF import and export implemented, round-trip tested against ≥2 real-world ADIF samples. `AdifImporter`/`AdifExporter` are implemented and round-trip tested, but current tests round-trip synthetic in-code `QsoRecord`s, not real-world third-party exports.
- [ ] Offline `ICallsignLookup` implemented with a licensed, versioned prefix table.
- [x] QRZ.com online lookup implemented as opt-in only, off by default (`IQrzCallsignLookup`/`QrzLookupSettings` — see the corrected note above on the actual interface name).
- [ ] Legacy `.MDT` log import path evaluated; documented as either supported (best-effort one-shot converter in `tools/`) or explicitly unsupported with ADIF as the recommended migration path.
