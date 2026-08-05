# Settings

## Related

[[01-architecture]] (`ScanlineStudio.Settings` project) · configuration consumed by every `ScanlineStudio.Core.*` module · replaces `Mmsstv.ini`, `MmsstvV.ini`, `Config.cfg`, `Repeater.txt`, and INI sections scattered across `Option.cpp`/`RadioSet.cpp`/`LogSet.cpp`/`ColorSet.cpp`

## Purpose

A single, coherent, JSON-backed configuration system replacing the legacy scatter of INI files and one giant mutable global struct per subsystem (`CRADIOPARA RADIO`, etc. — see [[01-architecture]]'s "no static mutable state" rule).

## Storage location

Per-OS conventional app-data directory, resolved via `Environment.GetFolderPath(SpecialFolder.ApplicationData)` (Windows: `%AppData%\Scanline Studio`; Linux: `~/.config/yoniq` via XDG conventions; macOS: `~/Library/Application Support/Scanline Studio`):

```
<app-data>/Scanline Studio/
  settings.json
  rigs.json              # user-added/edited rig definitions and template CAT protocols (03-cat-layer.md)
  macros.json
  logbook.sqlite          # 08-logging.md
  images.sqlite-index      # or merged into logbook.sqlite, see 07-image-pipeline.md
  plugins/
  locale/                 # user-added translation overrides, see 10-localization.md
```

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
