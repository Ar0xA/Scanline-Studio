# Settings

## Related

[[01-architecture]] (`ScanlineStudio.Settings` project) · configuration consumed by every `ScanlineStudio.Core.*` module · replaces `Mmsstv.ini`, `MmsstvV.ini`, `Config.cfg`, `Repeater.txt`, and INI sections scattered across `Option.cpp`/`RadioSet.cpp`/`LogSet.cpp`/`ColorSet.cpp`

## Purpose

A single, coherent, JSON-backed configuration system replacing the legacy scatter of INI files and one giant mutable global struct per subsystem (`CRADIOPARA RADIO`, etc. — see [[01-architecture]]'s "no static mutable state" rule).

## Storage location

Per-OS conventional app-data directory, resolved via `Environment.GetFolderPath(SpecialFolder.ApplicationData)`, with a literal `ScanlineStudio` (no space) subfolder appended by every store that resolves its own default path (`JsonSettingsStore`, `SqliteLogbookRepository`, `SqliteReceiveHistoryStore`) — Windows: `%AppData%\ScanlineStudio`; Linux: `~/.config/ScanlineStudio` via XDG conventions; macOS: `~/Library/Application Support/ScanlineStudio`:

```
<app-data>/ScanlineStudio/
  settings.json
  history.db              # 08-logging.md's QSO logbook, merged with 07-image-pipeline.md's RX history (single SQLite file, not separate logbook.sqlite/images.sqlite-index files)
```

**Corrected — only these two files actually exist** (`JsonSettingsStore.cs`/`SqliteLogbookRepository.cs`/
`SqliteReceiveHistoryStore.cs`, all resolving under this same `<app-data>/ScanlineStudio/` root).
`rigs.json`/`macros.json`/`plugins/`/a user-override `locale/` folder, all originally sketched here,
were never built and don't match the "Schema" section below anyway — that section's real,
buildable design keeps every module's settings as a NAMED SECTION inside the one `settings.json`
(via `AppSettings.Sections`), not as separate per-topic files, so a standalone `rigs.json`/
`macros.json` would have been the wrong shape even if built. [[11-plugin-system]]'s plugin host
(a `<app-data>/plugins/` discovery directory) and [[10-localization]]'s user-override locale folder
are both still unbuilt design sketches from their own specs, not files this document should have
implied already exist.

## Schema

**Corrected during Phase 3 implementation** — an earlier draft of this section showed `AppSettings`
directly typed with each module's own settings record (`AppSettings(AudioSettings Audio,
RadioConnectionSettings Radio, ...)`). That shape cannot actually compile: [[01-architecture]]'s
layering diagram puts `ScanlineStudio.Settings` at the very *bottom*, below even `ScanlineStudio.Abstractions`, so it
can never reference a type defined in `ScanlineStudio.Core.Radio` or any other module above it without
inverting that layering — the two specs were never cross-checked against each other on this point.
The real, buildable shape uses a named bag of raw `JsonElement` sections instead, with each module
still owning its own typed section record (the *intent* of the original text is preserved) via a pair
of generic extension methods that take the caller's own source-generated `JsonTypeInfo<T>`:

```csharp
namespace ScanlineStudio.Settings;

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Dictionary<string, JsonElement> Sections { get; init; } = new();
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
    IObservable<AppSettings> Changes { get; }   // live-reload on external edit, e.g. hand-editing settings.json
}

public static class AppSettingsSectionExtensions
{
    public static T? GetSection<T>(this AppSettings settings, string key, JsonTypeInfo<T> typeInfo);
    public static AppSettings WithSection<T>(this AppSettings settings, string key, T value, JsonTypeInfo<T> typeInfo);
}
```

Every module's typed settings section (`AudioDeviceSettings` in `ScanlineStudio.Core.Audio`,
`RadioConnectionSettings`/`RadioSafetySettings`/`FrequencyPresetsSettings` in `ScanlineStudio.Core.Radio`,
`LocalizationSettings` in `ScanlineStudio.Core.Localization`, `OperatorSettings` in `ScanlineStudio.Application`, etc.)
is still defined in that module's own project (not centralized in `ScanlineStudio.Settings`, which only owns
the load/save/versioning/migration machinery plus the section bag itself) — keeps each module's config
schema next to the code it configures, while `ScanlineStudio.Settings` never needs to know any
module-specific type. Phase 4's Settings/Options work (see [[14-roadmap]]) also established a new
pattern: a section owned directly by `ScanlineStudio.UI` itself is layering-legal too (e.g.
`TxPaneUiSettings` in a new `ScanlineStudio.UI.Settings` namespace), since `ScanlineStudio.Settings` is not a
`ScanlineStudio.Core.*` project and carries no hardware/DSP/image-library access. Sections not yet needed
(`SstvSettings`, `LogbookSettings`, `WaterfallPaletteSettings`) are added the same way when those modules
actually gain configuration needs, not speculatively now.

## Versioning and migration

`AppSettings` carries a `SchemaVersion` field. On load, if the on-disk version is older than the current app's expected version, a chain of `ISettingsMigration` steps (one per version bump, each a pure function `JsonNode → JsonNode`) is applied before deserializing into the current `AppSettings` shape — the same pattern as a database migration chain, applied to a config file. This avoids ever needing a "delete your settings and start over" support answer after a schema change.

## Legacy INI import

A one-shot importer (`tools/legacy-config-importer/`, a small console app, and/or a first-run wizard step in `ScanlineStudio.UI`) reads the legacy `Mmsstv.ini` and produces a best-effort `settings.json` + `rigs.json`, using the `RADIO_POLL*` → `rigId` mapping table from [[02-radio-layer]] for rig settings specifically. Import is best-effort and additive — it never overwrites an existing `settings.json`, and any INI key with no modern equivalent is logged (app diagnostics logging, [[01-architecture]]) as skipped rather than silently dropped, so users can tell what didn't carry over.

## Live settings changes

`ISettingsStore.Changes` lets subsystems react to a settings change without restart where practical (e.g. changing the waterfall color palette applies immediately) — settings that require a reconnect to take effect (e.g. changing serial port) are applied lazily, on next connect, rather than forcing a live reconnect the user didn't ask for.

## Secrets

Any credential-shaped setting (e.g. optional QRZ.com credentials for [[08-logging]]'s online lookup) is meant to never be stored in plain JSON in `settings.json`; the design is an OS credential store (`Windows Credential Manager` / `libsecret` on Linux / `Keychain` on macOS) through a small `ICredentialStore` abstraction, with only a "credential present: yes/no" flag in `settings.json` itself. **Not actually built — a real gap, not just an unimplemented design**: `ICredentialStore` does not exist anywhere in the codebase, and the one credential-shaped setting that has actually shipped, `QrzLookupSettings.Password` (`ScanlineStudio.Core.Logbook`, backing [[08-logging]]'s QRZ.com lookup), is a plain `string?` persisted directly in `settings.json`'s `"QrzLookup"` section via the ordinary `GetSection`/`WithSection` path — today's real behavior contradicts this section's own design, not just an open DoD checkbox.

## Testing

- `ISettingsStore` round-trip tested against a temp directory: save → load → equality.
- Each `ISettingsMigration` step unit-tested against a fixture "old shape" JSON, asserting the "new shape" output.
- Legacy INI importer tested against a fixture legacy `Mmsstv.ini` (checked into `tests/` fixtures), asserting expected `AppSettings`/`rigs.json` output and that unmapped keys are reported, not dropped silently.

## Definition of done

- [x] `ISettingsStore` implemented with source-generated JSON (de)serialization, atomic file writes (write-to-temp + rename, to avoid a crash mid-write corrupting `settings.json`) — `JsonSettingsStore`.
- [ ] Migration chain mechanism implemented and exercised by at least one real version bump before v1 ships.
- [ ] Legacy INI importer covers the fields exercised by the default legacy install; documented list of intentionally-unsupported legacy keys.
- [ ] `ICredentialStore` implemented per OS, verified no credential ever appears in `settings.json` — currently the reverse is true: `QrzLookupSettings.Password` is the one shipped credential-shaped setting and it IS stored in plain JSON in `settings.json` today (see "Secrets" above).
