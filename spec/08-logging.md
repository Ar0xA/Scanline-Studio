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
    Guid Id,
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
    string? Country,           // resolved via ICallsignLookup at entry time
    string? Notes,
    Guid? ReceivedImageId);    // FK into IReceiveHistoryStore, see 07-image-pipeline.md

public interface ILogbookRepository
{
    Task<QsoRecord> AddAsync(QsoRecord record, CancellationToken ct);
    Task UpdateAsync(QsoRecord record, CancellationToken ct);
    Task<IReadOnlyList<QsoRecord>> SearchAsync(LogbookQuery query, CancellationToken ct);
}
```

## ADIF import/export

`IAdifImporter`/`IAdifExporter` handle the [ADIF](https://adif.org) format — the actual interoperability need behind legacy `LogConv.cpp`'s conversion routines. Export produces standard ADIF 3.x; import accepts ADIF from any external logger, mapping fields onto `QsoRecord` with unmapped/nonstandard fields preserved in a `Notes`/raw-fields bag rather than silently dropped.

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

## Auto-fill from radio and DSP state

When a QSO is started from the UI, `FrequencyHz`/`Mode` default-populate from the current `IRadioController.LastKnownState` ([[02-radio-layer]]) if a radio is connected, and `SstvModeId` defaults from the mode last used by [[06-sstv-dsp]] — both remain user-editable, and both are optional (logging must work with no radio connected, consistent with [[01-architecture]]'s "radio control is never a hard dependency" rule).

## Testing

- `ILogbookRepository` tested against a temp SQLite file: add/update/search round-trips.
- ADIF import/export round-tripped against real-world ADIF fixture files (including at least one file exported by a well-known third-party logger, to catch real-world quirks beyond the spec).
- `ICallsignLookup` tested against a table of known prefixes with expected country/zone results, including edge cases (portable/maritime prefixes, reassigned prefixes).

## Definition of done

- [ ] SQLite-backed `ILogbookRepository` implemented and unit-tested.
- [ ] ADIF import and export implemented, round-trip tested against ≥2 real-world ADIF samples.
- [ ] Offline `ICallsignLookup` implemented with a licensed, versioned prefix table.
- [ ] QRZ.com online lookup implemented as opt-in only, off by default.
- [ ] Legacy `.MDT` log import path evaluated; documented as either supported (best-effort one-shot converter in `tools/`) or explicitly unsupported with ADIF as the recommended migration path.
