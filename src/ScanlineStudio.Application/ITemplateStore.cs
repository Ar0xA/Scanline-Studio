namespace ScanlineStudio.Application;

/// <summary>Persisted TX template storage — spec/15-template-designer.md Phase 5. Speaks ONLY
/// <see cref="PersistedTemplateElement"/>/<see cref="PersistedTemplateDocument"/>/
/// <see cref="TemplateMetadata"/> (all Application-layer types) — never a UI-layer
/// <c>RawElementSnapshot</c>; the Raw&lt;-&gt;Persisted mapping lives on
/// <c>TxImageEditorPaneViewModel</c> itself (the one place both shapes are reachable, plan-review
/// blocker 1's fix).</summary>
public interface ITemplateStore
{
    /// <summary>Pure, no I/O — mints a new <c>{slug}_{guid8}</c> template id (see
    /// <see cref="TemplateStore"/>'s own doc comment for the exact sanitization rules) so a caller
    /// can resolve <see cref="GetAssetPath"/> and write image assets BEFORE calling
    /// <see cref="SaveAsync"/> — <see cref="SaveAsync"/> itself only ever writes the manifest +
    /// thumbnail, never a per-element asset (that stays the caller's own responsibility, since only
    /// the caller holds the live, resolved <c>IImageSource</c> pixels).</summary>
    string CreateTemplateId(string name);

    /// <summary>Pure, no I/O — the absolute path an asset with <paramref name="assetFileName"/>
    /// would live at under <paramref name="templateId"/>'s own folder. Safe to call before the
    /// template's directory exists; <see cref="SaveAsync"/> creates it.</summary>
    string GetAssetPath(string templateId, string assetFileName);

    /// <summary>Writes <c>template.json</c> and renders + writes a pre-composited
    /// <c>thumbnail.png</c> (once, here — never recomputed on <see cref="ListAsync"/>). Every
    /// <see cref="PersistedImageElement.AssetFileName"/> referenced by <paramref name="document"/>
    /// must already exist at <see cref="GetAssetPath"/> by the time this is called.</summary>
    Task SaveAsync(string templateId, string name, PersistedTemplateDocument document, CancellationToken ct = default);

    Task<PersistedTemplateDocument> LoadAsync(string templateId, CancellationToken ct = default);

    /// <summary>Re-enumerates the template storage directory on every call — a template folder
    /// copied in by hand must show up without an app restart (plan-review finding).</summary>
    Task<IReadOnlyList<TemplateMetadata>> ListAsync(CancellationToken ct = default);

    Task DeleteAsync(string templateId, CancellationToken ct = default);

    /// <summary>ui_transition_plan.md step 13 (native template bundle export/import) — zips
    /// <paramref name="templateId"/>'s own folder (<c>template.json</c> + <c>thumbnail.png</c> +
    /// <c>assets/*</c>) to <paramref name="destinationZipPath"/> (a <c>.sstemplate</c> file by
    /// convention, enforced by the file picker, not this method). Overwrites an existing file at
    /// that path. Throws <see cref="InvalidOperationException"/> if <paramref name="templateId"/>
    /// does not exist.</summary>
    Task ExportAsync(string templateId, string destinationZipPath, CancellationToken ct = default);

    /// <summary>ui_transition_plan.md step 13 — imports a bundle written by <see cref="ExportAsync"/>
    /// (or hand-built to the same shape) from <paramref name="sourceZipPath"/>, always minting a
    /// FRESH template id via <see cref="CreateTemplateId"/> from the bundle's own recorded name —
    /// never trusts the archive's own id/folder name, since that could otherwise silently overwrite
    /// an existing template of the same id. Returns the newly minted id. Validates every zip entry
    /// NAME and the total entry COUNT before extracting anything; per-entry and total UNCOMPRESSED
    /// SIZE are capped separately, during extraction, against actual bytes read (never trusting a
    /// zip entry's own declared, spoofable length) — see <see cref="TemplateStore"/>'s own
    /// implementation comments for the exact caps. Also rejects a
    /// <see cref="TemplateManifest.SchemaVersion"/> newer than this build's
    /// <see cref="TemplateManifest.CurrentSchemaVersion"/>. All rejections are
    /// <see cref="InvalidOperationException"/> with a message safe to surface to the user, and a
    /// failure at any point — whether the up-front name/count validation or a size cap tripped mid-
    /// extraction — never leaves a partially-written template folder behind (a cleanup-on-failure
    /// step removes it, scoped ONLY to the id THIS call itself just minted).</summary>
    Task<string> ImportAsync(string sourceZipPath, CancellationToken ct = default);
}
