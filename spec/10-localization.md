# Localization

## Related

[[09-ui]] (consumes localized strings in every view) · replaces hardcoded VCL caption strings inline in `.cpp` sources and throughout the `.dfm` files — **not** `Mmsstv English.ini`/`Mmsstv Japanese.ini`, see the corrected migration section below

## Purpose

Implement CLAUDE.md's "no hardcoded UI strings" / "every user-visible string must be localized" and the project goal of **runtime** language switching (no restart), improving on the legacy approach where language was effectively baked in via a separately shipped INI file per language and picked up mostly at startup.

## Why not .resx

.NET's standard `.resx` + satellite assembly localization is the default choice for many .NET apps, but satellite assemblies are resolved via `CultureInfo.CurrentUICulture` at astartup/thread level and are awkward to hot-swap mid-session without restarting the app or juggling `AssemblyLoadContext` tricks. Since true runtime switching is an explicit design goal (not just multi-language support), this spec uses a JSON resource format with a live-bindable localization service instead.

## Resource format

```
/assets/locale/
  en.json
  ja.json
  de.json
  ...
```

Each file is a flat key → string map, keys namespaced by view (`"MainWindow.TxButton.Label"`, `"OptionsDialog.Tab.Audio"`), with ICU-style placeholders for interpolation (`"Logbook.ContactCount": "{count} contacts logged"`), covering plural forms via a small pluralization helper rather than hand-rolled `if (count == 1)` branches scattered through view-models.

```csharp
namespace ScanlineStudio.Abstractions.Localization;

public interface ILocalizationService
{
    IReadOnlyList<CultureInfo> AvailableCultures { get; }
    CultureInfo CurrentCulture { get; }
    Task SetCultureAsync(CultureInfo culture);          // triggers live re-bind, no restart
    string GetString(string key, params object[] args);
    event Action? CultureChanged;
}
```

## Binding in Avalonia

A markup extension backs every user-visible string in XAML, so `CultureChanged` propagates without manual re-binding per view:

```xml
<Button Content="{loc:Translate MainWindow.TxButton.Label}" />
```

`Translate` implements `IBinding`/subscribes to `ILocalizationService.CultureChanged` and re-evaluates on change, giving genuine runtime switching — this is the concrete mechanism satisfying the "Runtime language switching" design goal from [[00-project-overview]].

## Migration source — corrected after review

An earlier draft of this document assumed `Mmsstv English.ini`/`Mmsstv Japanese.ini` were a parallel string-table pair (one key set, two languages) that could be re-keyed wholesale into `en.json`/`ja.json`. **That's wrong, and the plan below replaces it.** Diffing the two files shows they are **general settings files** (waterfall colors, window positions, DSP parameters, etc.), not a translation corpus — they differ in exactly four lines: the configured UI font (`Name=ＭＳ ゴシック` in the Japanese file), its character set (`Charset=128`), and the default callsign field. There is no bilingual string table anywhere in the legacy `.ini` files to harvest.

Legacy UI language actually works differently: MMSSTV determines Japanese vs. English **once at startup**, from `GetThreadLocale()` (`Main.cpp:636-651`, into a flag conventionally called `MsgEng`), and then branches per-string at the call site — e.g. `CItems/TextArt/CodeVw.cpp:33`: `sys.m_MsgEng ? "Japanese(Shift-JIS)" : "日本語(シフトJIS)"`. Japanese string literals are inline in `.cpp` source files (encoded **CP932/Shift-JIS**, confirmed by byte-pattern inspection — do not assume UTF-8, per CLAUDE.md's encoding rule), and English captions live as VCL `Caption`/`Hint` properties inside the binary `.dfm` files (see [[09-ui]]'s note on `.dfm` files being binary, not text).

## Migration plan

Given the above, harvesting the legacy string set is **source-mining, not file-conversion**, and happens per-dialog as that dialog is ported in [[09-ui]]:

1. For each `.dfm` being ported, extract `Caption`/`Hint`/`Text` properties (via a DFM-to-text conversion step — see [[09-ui]]) as the English (`en.json`) source of truth for that view's namespace.
2. For the same view, grep the corresponding `.cpp`/`.h` for the `sys.m_MsgEng ? english : japanese` ternary pattern (and similar per-string branches) to recover the matching Japanese string, CP932-decoded, written into `ja.json`.
3. Where a legacy string has no discoverable Japanese counterpart (i.e. it was never branched — English-only in the legacy app), leave the `ja.json` key absent; the fallback-to-English behavior (below) covers it, and it's recorded as a genuine translation gap rather than invented.
4. This is manual, per-string work gated on the [[09-ui]] dialog port order in [[14-roadmap]] — there is no automatable bulk conversion, because the legacy key space is source-code call sites, not a data file.

No other legacy-supplied language exists to migrate beyond English/Japanese — any additional locale is new work, not a port.

## Adding a language

Dropping a new `xx.json` file into `assets/locale/` and adding it to a small manifest (`locales.json` listing available culture codes + display names) is sufficient — no code change or recompilation required, which also makes "contribute a translation" approachable for community translators (unlike the legacy INI files, which required manually mirroring every key MMSSTV's forms happened to expose).

## Fallback behavior

Missing keys in a non-English locale fall back to `en.json` and log a diagnostic-level warning (via app logging, [[01-architecture]]) rather than showing a raw key name or blank string to the user — this keeps incomplete community translations usable rather than broken.

## Testing

- A build-time (or CI) check asserts every locale file's key set is a subset of `en.json`'s (catches stale/renamed keys) and reports missing-key coverage percentage per locale.
- `ILocalizationService.SetCultureAsync` is unit-tested to confirm `CultureChanged` fires and `GetString` returns the new locale's values immediately after.
- No unit test can catch a hardcoded string left in a `.axaml` file directly (e.g. `Content="Send"` instead of the binding); this is instead caught by a lightweight static check (a small analyzer or grep-based CI step scanning `.axaml` for literal `Content=`/`Text=`/`Header=` attributes not using the `loc:Translate` extension), enforcing "no hardcoded UI strings" mechanically rather than by review discipline alone.

## Definition of done

- [x] `ILocalizationService` + `Translate` markup extension implemented and used by the [[01-architecture]] walking-skeleton window.
- [ ] `en.json` populated from `.dfm` caption/hint extraction and `ja.json` populated from `sys.m_MsgEng`-branch source mining (CP932-decoded) for every view as it's ported (tracked per-dialog in [[14-roadmap]]) — not from the legacy `.ini` files, which carry no string table. Only `en.json` exists so far (Phase 3's own strings, hand-authored, not `.dfm`-mined since `MainWindow`/the 3 Phase-3 panes have no legacy dialog counterpart to mine from); `ja.json` and the `.dfm`-mining workflow are still open.
- [x] CI check for orphaned/missing locale keys and un-localized literal strings in `.axaml` files.
- [ ] Manual verification: switching language in `LanguageSettingsDialog` updates all currently-open windows live, no restart.
