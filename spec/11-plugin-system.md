# Plugin System

## Related

[[02-radio-layer]]/[[03-cat-layer]] (plugin-contributed CAT backend clients) · [[07-image-pipeline]] (plugin-contributed filters) · **correction after review**: legacy MMSSTV *does* have an extension mechanism for its QSL/template designer. `CItems/ECUSTOM.TXT` documents it explicitly: *"the functions provided in the custom item are offered as a DLL file... MMSSTV merely loads the DLL on-the-fly."* PERIMG, QSLBox, TextArt, and TEXTBOX are the four reference implementations of that ABI, each built as its own DLL (`CItems/QSLBox/qslbox.bpr` → `qslbox.dll`), not compiled-in fixed features. This is a real, previously-published plugin surface — potentially with third-party DLLs in the wild built against it — and this document previously and incorrectly claimed no such precedent existed. **User decision 2026-08-16: no CItems-successor extension point is planned** — [[15-template-designer]]'s redesign explicitly drops that scope (see its own "Non-goals" section); this stays a permanent, acknowledged capability gap, not a deferred-until-X one. See [docs/removed-features.md](../docs/removed-features.md) for the full accounting.

## Purpose

Provide a stable, versioned extension surface so functionality (rig protocols, image filters, macro actions, log export formats) can be added without forking or modifying `ScanlineStudio.Core.*`, satisfying the "Plugin architecture" design goal in [[00-project-overview]].

## Extension points (v1 scope)

| Extension point | Interface | Backing module | Status (current codebase) |
|---|---|---|---|
| CAT backend client | `IRadioProtocol` | [[03-cat-layer]] | exists (`src/ScanlineStudio.Abstractions/Radio/IRadioProtocol.cs`) |
| Image filter | `IImageFilter` | [[07-image-pipeline]] | not yet defined — [[07-image-pipeline]] explicitly defers it ("deliberately NOT part of this interface yet") |
| Log export format | `ILogExporter` | [[08-logging]] | not yet defined — [[08-logging]] currently only defines `IAdifExporter`/`IAdifImporter` |
| Macro action | `IMacroAction` | [[09-ui]] | not yet defined — [[09-ui]] describes a `MacroKeyEditor` dialog but no such interface |

Each extension point is meant to be simply a `ScanlineStudio.Abstractions` interface defined by its owning spec — plugins do not get a separate, parallel API; they implement the same interfaces the built-in implementations do, discovered and registered the same way. As of this writing only `IRadioProtocol` actually exists (see Status column); the rest are still planned. This keeps "plugin" and "built-in" symmetric rather than second-class, once built.

## Why the legacy CItems ABI isn't ported directly

CItems DLLs are native Win32 code, built against a C++Builder-specific struct/vtable layout, loaded with `LoadLibrary`/`GetProcAddress` into the same process — none of that is meaningful on a cross-platform managed `AssemblyLoadContext`-based host, and there's no reasonable shim that makes a native Win32 DLL run unmodified on Linux/macOS. This is a genuine capability gap for any third-party CItems DLL that exists in the wild (unknown how many), not a transparent replacement — see [docs/removed-features.md](../docs/removed-features.md). Unlike this document's other legacy-gap items, this one has no planned successor at all: **user decision 2026-08-16 explicitly moved a CItems-equivalent extension point out of scope** for [[15-template-designer]]'s redesign, rather than carrying the concept forward as a future `ITemplateItem`-style extension point. This is a permanent, acknowledged gap, not a migration still pending.

## Plugin packaging

A plugin is a directory containing a manifest and one or more assemblies:

```
/plugins/my-rig-pack/
  plugin.json
  MyRigPack.dll
```

```json
{
  "id": "example.my-rig-pack",
  "displayName": "Example Rig Pack",
  "version": "1.0.0",
  "scanlineStudioApiVersion": "1.x",
  "entryPoint": "MyRigPack.dll",
  "extensionPoints": ["radio-protocol"]
}
```

`scanlineStudioApiVersion` is checked against the host's `ScanlineStudio.Abstractions` semantic version at load time; a plugin declaring an incompatible major version is refused with a clear error rather than loaded and risking a runtime crash — `ScanlineStudio.Abstractions` itself follows semver strictly for this reason.

## Isolation and loading

Each plugin loads into its own `System.Runtime.Loader.AssemblyLoadContext`, collectible, so a plugin can be unloaded/reloaded without restarting the host — this also contains a misbehaving plugin's dependency versions from colliding with the host's or another plugin's. Plugins only receive references to `ScanlineStudio.Abstractions` (interfaces/DTOs) at load time, never to `ScanlineStudio.Core.*` or `ScanlineStudio.UI` concrete assemblies, enforced the same way as the [[09-ui]] layering rule (an architecture test asserting the plugin host only exposes `ScanlineStudio.Abstractions` types across the load-context boundary).

```csharp
namespace ScanlineStudio.Abstractions.Plugins;

public interface IScanlineStudioPlugin
{
    string Id { get; }
    void RegisterServices(IServiceCollection services);   // plugin registers its IRadioProtocol/IImageFilter/etc. implementations
}

public interface IPluginHost
{
    Task<IReadOnlyList<PluginDescriptor>> DiscoverAsync();
    Task LoadAsync(PluginDescriptor descriptor, CancellationToken ct);
    Task UnloadAsync(string pluginId);
    IReadOnlyList<PluginDescriptor> Loaded { get; }
}
```

## Trust model

Plugins run with the same process privileges as the host (no sandboxing/capability restriction in v1 — full sandboxing, e.g. via a separate process + IPC, is a larger effort tracked as a possible post-v1 hardening item in [[14-roadmap]] rather than a v1 requirement). Until then, the `PluginManagerDialog` ([[09-ui]]) surfaces plugin source/publisher info from the manifest and requires explicit user action to enable a newly discovered plugin — no plugin auto-loads from a folder without at least one explicit user opt-in the first time it's seen, similar in spirit to how browsers gate newly installed extensions.

## Discovery locations

`IPluginHost.DiscoverAsync` scans a user plugins directory (`<app-data>/plugins/`, see [[12-settings]] for path resolution) plus optionally a directory specified via settings for development/testing — no network-based plugin marketplace in v1.

## Testing

- `IPluginHost` load/unload lifecycle tested against a fixture plugin assembly built as part of the test project, verifying services registered by `RegisterServices` are resolvable and that `UnloadAsync` actually releases the `AssemblyLoadContext` (verified via `WeakReference`+GC in a dedicated test, a known-tricky pattern to get right and worth its own explicit test).
- API version compatibility checks tested with fixture manifests declaring compatible/incompatible `scanlineStudioApiVersion` values.

## Definition of done

- [ ] `IPluginHost` implemented with collectible `AssemblyLoadContext` isolation, load/unload verified not to leak.
- [ ] At least one built-in extension point (recommend: `IImageFilter`, lowest-risk) refactored to load through the same plugin registration path as third-party plugins, proving the symmetry claim above.
- [ ] `PluginManagerDialog` implemented with explicit enable/disable and manifest info display.
- [ ] Architecture test enforcing plugins only see `ScanlineStudio.Abstractions` types.
