# Settings

## Related

[[01-architecture]] (`Yoniq.Settings` project) · configuration consumed by every `Yoniq.Core.*` module · replaces `Mmsstv.ini`, `MmsstvV.ini`, `Config.cfg`, `Repeater.txt`, and INI sections scattered across `Option.cpp`/`RadioSet.cpp`/`LogSet.cpp`/`ColorSet.cpp`

## Purpose

A single, coherent, JSON-backed configuration system replacing the legacy scatter of INI files and one giant mutable global struct per subsystem (`CRADIOPARA RADIO`, etc. — see [[01-architecture]]'s "no static mutable state" rule).

## Storage location

Per-OS conventional app-data directory, resolved via `Environment.GetFolderPath(SpecialFolder.ApplicationData)` (Windows: `%AppData%\Yoniq`; Linux: `~/.config/yoniq` via XDG conventions; macOS: `~/Library/Application Support/Yoniq`):

```
<app-data>/Yoniq/
  settings.json
  rigs.json              # user-added/edited rig definitions and template CAT protocols (03-cat-layer.md)
  macros.json
  logbook.sqlite          # 08-logging.md
  images.sqlite-index      # or merged into logbook.sqlite, see 07-image-pipeline.md
  plugins/
  locale/                 # user-added translation overrides, see 10-localization.md
```

## Schema

`settings.json` is strongly typed via a root `AppSettings` record deserialized with `System.Text.Json` source generators (`JsonSerializerContext`, avoids reflection-based (de)serialization and keeps startup fast and AOT-friendly):

```csharp
namespace Yoniq.Settings;

public sealed record AppSettings(
    AudioSettings Audio,
    RadioConnectionSettings Radio,
    SstvSettings Sstv,
    LogbookSettings Logbook,
    LocalizationSettings Localization,
    RigctldServerSettings RigctldServer,
    UiSettings Ui);

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct);
    Task SaveAsync(AppSettings settings, CancellationToken ct);
    IObservable<AppSettings> Changes { get; }   // live-reload on external edit, e.g. hand-editing settings.json
}
```

Every module's typed settings section (`AudioSettings`, `RadioConnectionSettings`, etc.) is defined in that module's own `Yoniq.Core.*` project (not centralized in `Yoniq.Settings`, which only owns the load/save/versioning/migration machinery) — keeps each module's config schema next to the code it configures.

## Versioning and migration

`AppSettings` carries a `SchemaVersion` field. On load, if the on-disk version is older than the current app's expected version, a chain of `ISettingsMigration` steps (one per version bump, each a pure function `JsonNode → JsonNode`) is applied before deserializing into the current `AppSettings` shape — the same pattern as a database migration chain, applied to a config file. This avoids ever needing a "delete your settings and start over" support answer after a schema change.

## Legacy INI import

A one-shot importer (`tools/legacy-config-importer/`, a small console app, and/or a first-run wizard step in `Yoniq.UI`) reads the legacy `Mmsstv.ini` and produces a best-effort `settings.json` + `rigs.json`, using the `RADIO_POLL*` → `rigId` mapping table from [[02-radio-layer]] for rig settings specifically. Import is best-effort and additive — it never overwrites an existing `settings.json`, and any INI key with no modern equivalent is logged (app diagnostics logging, [[01-architecture]]) as skipped rather than silently dropped, so users can tell what didn't carry over.

## Live settings changes

`ISettingsStore.Changes` lets subsystems react to a settings change without restart where practical (e.g. changing the waterfall color palette applies immediately) — settings that require a reconnect to take effect (e.g. changing serial port) are applied lazily, on next connect, rather than forcing a live reconnect the user didn't ask for.

## Secrets

Any credential-shaped setting (e.g. optional QRZ.com credentials for [[08-logging]]'s online lookup) is never stored in plain JSON in `settings.json`; it is stored via the OS credential store (`Windows Credential Manager` / `libsecret` on Linux / `Keychain` on macOS) through a small `ICredentialStore` abstraction, with only a "credential present: yes/no" flag in `settings.json` itself.

## Testing

- `ISettingsStore` round-trip tested against a temp directory: save → load → equality.
- Each `ISettingsMigration` step unit-tested against a fixture "old shape" JSON, asserting the "new shape" output.
- Legacy INI importer tested against a fixture legacy `Mmsstv.ini` (checked into `tests/` fixtures), asserting expected `AppSettings`/`rigs.json` output and that unmapped keys are reported, not dropped silently.

## Definition of done

- [ ] `ISettingsStore` implemented with source-generated JSON (de)serialization, atomic file writes (write-to-temp + rename, to avoid a crash mid-write corrupting `settings.json`).
- [ ] Migration chain mechanism implemented and exercised by at least one real version bump before v1 ships.
- [ ] Legacy INI importer covers the fields exercised by the default legacy install; documented list of intentionally-unsupported legacy keys.
- [ ] `ICredentialStore` implemented per OS, verified no credential ever appears in `settings.json`.
