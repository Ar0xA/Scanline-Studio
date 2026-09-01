using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Application;

/// <summary>Folder-per-template <see cref="ITemplateStore"/> — spec/15-template-designer.md Phase 5.
/// <c>%MyPictures%/ScanlineStudio/Templates/&lt;template-id&gt;/</c>, matching this codebase's own
/// existing MyPictures convention (<c>StockImageLibrary</c>'s <c>Stock</c> folder,
/// <c>ReceiveHistoryRecorder</c>'s <c>History</c> folder): user-browsable/shareable content lives
/// under MyPictures, machine-state/index data lives under ApplicationData. Each template folder is
/// fully self-contained (<c>template.json</c> with only RELATIVE paths + <c>thumbnail.png</c> +
/// <c>assets/*.png</c>) — an operator can already share a template today by copying the folder, the
/// deferred export/import bundle's own stated justification.
///
/// Deliberately never touches ImageSharp/SixLabors directly (plan-review decision) — every pixel
/// operation goes through <see cref="IImageSourceWriter"/>/<see cref="IImageFileLoader"/>/
/// <see cref="ITransmitImagePreparer"/>, keeping this Application-layer class as ImageSharp-agnostic
/// as <c>MacroTextResolver</c>/<c>TemplateStore</c>'s own siblings.</summary>
public sealed partial class TemplateStore : ITemplateStore
{
    private const int ThumbnailWidth = 160;
    private const int ThumbnailHeight = 120;

    private readonly IImageSourceWriter _imageSourceWriter;
    private readonly IImageFileLoader _imageFileLoader;
    private readonly ITransmitImagePreparer _preparer;
    private readonly ILogger<TemplateStore> _logger;
    private readonly string? _templatesRootOverride;

    public TemplateStore(IImageSourceWriter imageSourceWriter, IImageFileLoader imageFileLoader, ITransmitImagePreparer preparer, ILogger<TemplateStore> logger)
        : this(imageSourceWriter, imageFileLoader, preparer, logger, templatesRootOverride: null)
    {
    }

    /// <summary><paramref name="templatesRootOverride"/> is a test-only seam (no settings/UI exposes
    /// it in this phase, unlike <c>StockImageLibrary</c>'s own settings-driven directory override) --
    /// lets a test point this store at a real temp directory instead of the real user's Pictures
    /// folder, without needing a fake filesystem abstraction this codebase has no precedent for.</summary>
    public TemplateStore(IImageSourceWriter imageSourceWriter, IImageFileLoader imageFileLoader, ITransmitImagePreparer preparer, ILogger<TemplateStore> logger, string? templatesRootOverride)
    {
        _imageSourceWriter = imageSourceWriter;
        _imageFileLoader = imageFileLoader;
        _preparer = preparer;
        _logger = logger;
        _templatesRootOverride = templatesRootOverride;
    }

    /// <summary><c>{slug}_{guid8}</c> — matches <c>ReceiveHistoryRecorder</c>'s own existing
    /// <c>{human-readable}_{entryId[..8]}</c> id shape (same reasoning: a bare slug collides on
    /// re-save-with-same-name, a bare GUID is unreadable to this app's own technically-inclined
    /// ham-radio user base browsing the folder directly). The id is minted once, here, and the
    /// CALLER (<c>TxImageEditorPaneViewModel.SaveTemplateAsync</c>) stores it — renaming a
    /// template's display name later never renames its folder.</summary>
    public string CreateTemplateId(string name)
    {
        var slug = Sanitize(name);
        var guid8 = Guid.NewGuid().ToString()[..8];
        return $"{slug}_{guid8}";
    }

    public string GetAssetPath(string templateId, string assetFileName)
    {
        // Tier B audit finding: assetFileName comes straight from a persisted (and, per this
        // format's own "share by copying the folder" design, possibly hand-edited or downloaded-
        // from-elsewhere) template.json -- Path.Combine neither rejects "../" traversal nor a
        // rooted path (a rooted AssetFileName like "/etc/hosts" would silently discard the template
        // directory entirely). The WRITE path is never at risk (BuildPersistedElementAsync always
        // mints a fresh Guid.NewGuid():N name, never a persisted/attacker-influenced one) -- this
        // guard is specifically for the READ path, where a malicious/malformed shared template
        // could otherwise point this app at an arbitrary file elsewhere on disk.
        if (Path.GetFileName(assetFileName) != assetFileName)
        {
            throw new InvalidOperationException($"Asset file name '{assetFileName}' is not a bare file name.");
        }

        return Path.Combine(GetTemplateDirectory(templateId), "assets", assetFileName);
    }

    public async Task SaveAsync(string templateId, string name, PersistedTemplateDocument document, CancellationToken ct = default)
    {
        var directory = GetTemplateDirectory(templateId);
        Directory.CreateDirectory(directory);

        // Thumbnail rendered/written BEFORE the manifest (code-review finding, order reversed from
        // an earlier draft): RenderThumbnailAsync can throw (e.g. an image element's asset failed to
        // load), and a thumbnail is never recomputed later (see ITemplateStore.SaveAsync's own doc
        // comment) -- writing the manifest first would leave a template permanently LISTED with no
        // thumbnail.png on any such failure, since ListAsync only checks for template.json. Doing it
        // this way round means a thumbnail failure aborts the whole save cleanly instead (no
        // manifest ever gets written), matching this store's own "never silently mostly-succeed"
        // doctrine.
        var thumbnail = await RenderThumbnailAsync(templateId, document, ct).ConfigureAwait(false);
        await _imageSourceWriter.WritePngAsync(thumbnail, Path.Combine(directory, "thumbnail.png"), ct).ConfigureAwait(false);

        // SchemaVersion passed explicitly (line-element plan-review, Amendment F) -- the
        // constructor's own default is deliberately the LITERAL 1, not CurrentSchemaVersion (see
        // TemplateManifest's own doc comment), so a REAL write must state the current version itself
        // rather than rely on a default that no longer means "current."
        var manifest = new TemplateManifest(templateId, name, DateTimeOffset.Now, document.Elements, TemplateManifest.CurrentSchemaVersion);
        var json = JsonSerializer.Serialize(manifest, PersistedTemplateJsonContext.Default.TemplateManifest);
        await File.WriteAllTextAsync(Path.Combine(directory, "template.json"), json, ct).ConfigureAwait(false);
    }

    public async Task<PersistedTemplateDocument> LoadAsync(string templateId, CancellationToken ct = default)
    {
        var manifest = await ReadManifestAsync(GetTemplateDirectory(templateId), ct).ConfigureAwait(false);

        // Tier B audit finding: manifest.Elements can be null (a missing or explicitly-null
        // "Elements" property in a hand-edited template.json deserializes with no JsonException --
        // this format explicitly supports hand-copying/editing template folders, per this class's
        // own doc comment), and any individual entry in the list can itself be null (a hand-edited
        // "Elements":[null]). Both used to NRE downstream -- a caller reading .Count, or this file's
        // own RenderThumbnailAsync/ToTemplateElementAsync dereferencing a null element on a re-save
        // (the switch's own `default:` arm calls element.GetType()). Filtered here, once, at the
        // read boundary, so every downstream consumer -- this file's and the caller's -- always
        // sees a clean, non-null list, matching the "one bad element must not blank the whole
        // template" convention already established at the UI layer.
        var elements = new List<PersistedTemplateElement>();
        foreach (var element in manifest.Elements ?? [])
        {
            if (element is not null)
            {
                elements.Add(element);
            }
        }

        return new PersistedTemplateDocument(elements);
    }

    public async Task<IReadOnlyList<TemplateMetadata>> ListAsync(CancellationToken ct = default)
    {
        var root = GetTemplatesRoot();
        if (!Directory.Exists(root))
        {
            return [];
        }

        var results = new List<TemplateMetadata>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var manifestPath = Path.Combine(directory, "template.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            // Round-1 code-review finding (Tier A Batch 10 chunk 10b, real robustness gap fixed):
            // File.WriteAllTextAsync in SaveAsync is not atomic (no temp-file-then-rename), so a
            // crash/power-loss mid-save can leave a truncated/corrupt template.json -- a
            // JsonException here previously killed the ENTIRE rack listing (every other template's
            // manifest too), not just this one, for a store whose whole design explicitly supports
            // hand-copying/moving folders around (real-world corruption risk, not theoretical). Skip
            // just the one bad template and log it, matching the existing null-manifest skip below.
            //
            // Tier B audit finding: the catch used to cover JsonException only -- IOException (the
            // file locked mid-sync by OneDrive/Dropbox, or a concurrent writer) and
            // UnauthorizedAccessException/FileNotFoundException (a TOCTOU race against the
            // File.Exists check above -- e.g. a fire-and-forget RefreshAsync running concurrently
            // with a DeleteAsync call on this exact template) hit the identical "one bad template
            // must not blank the WHOLE rack" scenario the JsonException catch was already added to
            // fix, just via a different exception type. OperationCanceledException still propagates
            // (a real cancellation must still abort the whole ListAsync call, not be swallowed as
            // "one bad template").
            TemplateManifest? manifest;
            try
            {
                var json = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
                manifest = JsonSerializer.Deserialize(json, PersistedTemplateJsonContext.Default.TemplateManifest);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Log.CorruptManifestSkipped(_logger, manifestPath, ex);
                continue;
            }

            if (manifest is null)
            {
                continue;
            }

            // The DIRECTORY's own name is the authoritative id, not manifest.Id (code-review
            // finding) -- every other method (LoadAsync/DeleteAsync/GetAssetPath) resolves a
            // template's location purely from `Path.Combine(root, templateId)`, so an id here that
            // disagrees with its own folder name would make Load/Delete silently operate on a
            // DIFFERENT template than the one this row represents, and two folders that happen to
            // share a manifest.Id (e.g. one copied/renamed by hand -- the whole point of this
            // format's own "share by copying the folder" design) would throw inside
            // ReadyRackViewModel's ToDictionary. Directory names are unique by construction
            // (the filesystem itself enforces it), so keying on the folder name instead can never
            // collide.
            results.Add(new TemplateMetadata(Path.GetFileName(directory), manifest.Name, manifest.SavedAt, Path.Combine(directory, "thumbnail.png")));
        }

        return results.OrderByDescending(m => m.SavedAt).ToList();
    }

    public Task DeleteAsync(string templateId, CancellationToken ct = default)
    {
        var directory = GetTemplateDirectory(templateId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    // Zip-bomb guards (ui_transition_plan.md step 13) -- both caps are enforced against ACTUAL bytes
    // read during extraction (CopyWithLimitAsync below), never against a zip entry's own declared
    // `Length` header alone, since that header is untrusted metadata a malicious archive can lie
    // about (System.IO.Compression's inflate stream keeps producing bytes for as long as the
    // compressed data says to, regardless of what the header claimed).
    private const long MaxEntryUncompressedBytes = 64L * 1024 * 1024;
    private const long MaxTotalUncompressedBytes = 200L * 1024 * 1024;

    // Third leg of the zip-bomb defense, alongside the two size caps above -- bounds entry COUNT
    // (inode/file-handle exhaustion, unreasonable extraction time), which no size cap alone catches.
    private const int MaxEntryCount = 1000;

    public async Task ExportAsync(string templateId, string destinationZipPath, CancellationToken ct = default)
    {
        var directory = GetTemplateDirectory(templateId);
        var manifestPath = Path.Combine(directory, "template.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"Template '{templateId}' does not exist.");
        }

        // Checked BEFORE opening destinationZipPath (auditor finding): FileMode.Create truncates an
        // existing file at that path immediately, so failing later (e.g. the manifest read below)
        // would otherwise leave a 0-byte .sstemplate behind at a path the user may have chosen to
        // overwrite a real, different file.
        await using var zipStream = new FileStream(destinationZipPath, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

        await AddFileEntryAsync(archive, manifestPath, "template.json", ct).ConfigureAwait(false);

        var thumbnailPath = Path.Combine(directory, "thumbnail.png");
        if (File.Exists(thumbnailPath))
        {
            await AddFileEntryAsync(archive, thumbnailPath, "thumbnail.png", ct).ConfigureAwait(false);
        }

        var assetsDirectory = Path.Combine(directory, "assets");
        if (Directory.Exists(assetsDirectory))
        {
            foreach (var assetPath in Directory.EnumerateFiles(assetsDirectory))
            {
                await AddFileEntryAsync(archive, assetPath, $"assets/{Path.GetFileName(assetPath)}", ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task AddFileEntryAsync(ZipArchive archive, string sourcePath, string entryName, CancellationToken ct)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await using var sourceStream = File.OpenRead(sourcePath);
        await sourceStream.CopyToAsync(entryStream, ct).ConfigureAwait(false);
    }

    public async Task<string> ImportAsync(string sourceZipPath, CancellationToken ct = default)
    {
        using var zipStream = File.OpenRead(sourceZipPath);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        // Auditor finding: the per-entry/total-size caps alone don't bound entry COUNT -- a bundle of
        // e.g. 500k one-byte assets/*.png entries would pass both size caps yet exhaust inodes/file
        // handles in the user's Pictures folder and take an unreasonably long time to extract. A real
        // template never has more than a handful of assets; this cap is generous, not tight.
        if (archive.Entries.Count > MaxEntryCount)
        {
            throw new InvalidOperationException($"Bundle contains too many entries ({archive.Entries.Count}, max {MaxEntryCount}).");
        }

        // Pass 1: validate every entry's NAME before extracting anything, so a malicious entry name
        // is caught before any file I/O touches it. Size caps are enforced separately, DURING
        // extraction (ReadEntryWithLimitAsync/CheckedAddWithinTotalCap below) against actual bytes
        // read -- not here, and not against ZipArchiveEntry.Length (untrusted header metadata a
        // malicious archive can understate) -- so a rejected-for-size bundle can still have partially
        // extracted content on disk; the try/catch around the extraction loop below is what actually
        // guarantees no partial template folder survives, not this pass alone.
        ZipArchiveEntry? manifestEntry = null;
        ZipArchiveEntry? thumbnailEntry = null;
        var assetEntries = new List<(ZipArchiveEntry Entry, string FileName)>();

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                // A directory entry some zip tools emit for "assets/" itself -- no content of its
                // own, safe to skip.
                continue;
            }

            ValidateBundleEntryName(entry.FullName);

            if (entry.FullName == "template.json")
            {
                manifestEntry = entry;
            }
            else if (entry.FullName == "thumbnail.png")
            {
                thumbnailEntry = entry;
            }
            else if (entry.FullName.StartsWith("assets/", StringComparison.Ordinal))
            {
                var assetFileName = entry.FullName["assets/".Length..];
                if (Path.GetFileName(assetFileName) != assetFileName || assetFileName.Length == 0)
                {
                    throw new InvalidOperationException($"Bundle contains an unsafe asset entry '{entry.FullName}'.");
                }

                assetEntries.Add((entry, assetFileName));
            }
            else
            {
                throw new InvalidOperationException($"Bundle contains an unrecognized entry '{entry.FullName}'.");
            }
        }

        if (manifestEntry is null)
        {
            throw new InvalidOperationException("Bundle is missing 'template.json'.");
        }

        // Pass 2: read the manifest bytes (size-limited) and peek at its raw SchemaVersion BEFORE
        // running it through the typed, polymorphic deserializer -- a future schema version could
        // contain an element "$type" this build's [JsonDerivedType] list doesn't recognize, which
        // would otherwise throw a raw JsonException instead of this method's own clear, user-safe
        // rejection message.
        var manifestBytes = await ReadEntryWithLimitAsync(manifestEntry, ct).ConfigureAwait(false);
        using (var manifestDocument = JsonDocument.Parse(manifestBytes))
        {
            var schemaVersion = manifestDocument.RootElement.TryGetProperty(nameof(TemplateManifest.SchemaVersion), out var schemaVersionProperty)
                && schemaVersionProperty.ValueKind == JsonValueKind.Number
                    ? schemaVersionProperty.GetInt32()
                    : 0;

            if (schemaVersion > TemplateManifest.CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Bundle schema version {schemaVersion} is newer than this application supports (max {TemplateManifest.CurrentSchemaVersion}).");
            }
        }

        var manifest = JsonSerializer.Deserialize(manifestBytes, PersistedTemplateJsonContext.Default.TemplateManifest)
            ?? throw new InvalidOperationException("Bundle's template.json is empty or invalid.");

        // Auditor finding: manifest.Name is untrusted input (a hand-edited or attacker-crafted
        // template.json can omit "Name" entirely, deserializing it to null) -- CreateTemplateId's own
        // Sanitize calls name.Trim() unconditionally, which threw a raw NullReferenceException here
        // instead of this method's own documented "always InvalidOperationException" contract.
        if (string.IsNullOrEmpty(manifest.Name))
        {
            throw new InvalidOperationException("Bundle's template.json is missing a name.");
        }

        // Never trust the archive's own id/folder name (code-review precedent: SaveTemplateAsync's
        // own overwrite-by-name lookup already established that only a freshly resolved id is safe
        // to write under) -- a collision with an existing template would otherwise silently
        // overwrite it.
        var templateId = CreateTemplateId(manifest.Name);
        var directory = GetTemplateDirectory(templateId);
        Directory.CreateDirectory(directory);

        try
        {
            long totalBytes = manifestBytes.Length;

            if (thumbnailEntry is not null)
            {
                var thumbnailBytes = await ReadEntryWithLimitAsync(thumbnailEntry, ct).ConfigureAwait(false);
                totalBytes = CheckedAddWithinTotalCap(totalBytes, thumbnailBytes.Length);
                await File.WriteAllBytesAsync(Path.Combine(directory, "thumbnail.png"), thumbnailBytes, ct).ConfigureAwait(false);
            }

            if (assetEntries.Count > 0)
            {
                Directory.CreateDirectory(Path.Combine(directory, "assets"));
                foreach (var (assetEntry, assetFileName) in assetEntries)
                {
                    var assetBytes = await ReadEntryWithLimitAsync(assetEntry, ct).ConfigureAwait(false);
                    totalBytes = CheckedAddWithinTotalCap(totalBytes, assetBytes.Length);
                    await File.WriteAllBytesAsync(Path.Combine(directory, "assets", assetFileName), assetBytes, ct).ConfigureAwait(false);
                }
            }

            // Re-minted id, current schema version -- this build's own SaveAsync-equivalent shape,
            // never the archive's own (possibly older or foreign) manifest.Id/SchemaVersion.
            var rewritten = manifest with { Id = templateId, SchemaVersion = TemplateManifest.CurrentSchemaVersion };
            var json = JsonSerializer.Serialize(rewritten, PersistedTemplateJsonContext.Default.TemplateManifest);
            await File.WriteAllTextAsync(Path.Combine(directory, "template.json"), json, ct).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup: never leave a half-imported template folder behind (same
            // freshly-minted-id-only cleanup convention as TxImageEditorPaneViewModel.SaveTemplateAsync's
            // own catch block -- this id was minted by THIS call, so deleting it can never destroy a
            // pre-existing template).
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception cleanupEx)
            {
                Log.ImportCleanupFailed(_logger, templateId, cleanupEx);
            }

            throw;
        }

        return templateId;
    }

    /// <summary>Rejects an absolute path, a Windows-style separator (zip entry names are
    /// forward-slash per the ODA/.NET convention, but a maliciously hand-built archive can still
    /// embed one), and any <c>..</c> segment — the same path-traversal shape
    /// <see cref="GetAssetPath"/>'s own doc comment already guards against for a persisted
    /// <c>AssetFileName</c>, applied here one layer earlier (the zip entry name itself, before it
    /// ever becomes an <c>AssetFileName</c>).</summary>
    private static void ValidateBundleEntryName(string entryName)
    {
        if (entryName.Length == 0
            || entryName.Contains('\\', StringComparison.Ordinal)
            || entryName.StartsWith('/')
            || entryName.Split('/').Contains(".."))
        {
            throw new InvalidOperationException($"Bundle contains an unsafe entry name '{entryName}'.");
        }
    }

    private static long CheckedAddWithinTotalCap(long runningTotal, long addedBytes)
    {
        var total = runningTotal + addedBytes;
        if (total > MaxTotalUncompressedBytes)
        {
            throw new InvalidOperationException("Bundle exceeds the maximum allowed total uncompressed size.");
        }

        return total;
    }

    /// <summary>Reads <paramref name="entry"/>'s content fully into memory, enforcing
    /// <see cref="MaxEntryUncompressedBytes"/> against the ACTUAL bytes read from the inflate stream
    /// -- not <see cref="ZipArchiveEntry.Length"/>, which is untrusted header metadata a malicious
    /// archive can understate (see this file's own zip-bomb-guard comment above
    /// <see cref="MaxEntryUncompressedBytes"/>).</summary>
    private static async Task<byte[]> ReadEntryWithLimitAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var entryStream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await entryStream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxEntryUncompressedBytes)
            {
                throw new InvalidOperationException($"Bundle entry '{entry.FullName}' exceeds the maximum allowed size.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task<TemplateManifest> ReadManifestAsync(string directory, CancellationToken ct)
    {
        var manifestPath = Path.Combine(directory, "template.json");
        var json = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, PersistedTemplateJsonContext.Default.TemplateManifest)
            ?? throw new InvalidOperationException($"Template manifest at '{manifestPath}' is empty or invalid.");
    }

    /// <summary>Renders the ready-rack thumbnail via the SAME <see cref="ITransmitImagePreparer.ApplyTemplate"/>
    /// pipeline the real editor uses (plan-review decision: once, here, at save time — never
    /// recomputed on <see cref="ListAsync"/>). <see cref="ThumbnailWidth"/>/<see cref="ThumbnailHeight"/>
    /// is a fixed small size, not the actual SSTV mode's own dimensions (this store has no mode
    /// context) — every element's <see cref="TemplateElement.Bounds"/> is already normalized against
    /// the full frame, so it composites correctly at any target resolution; a purely cosmetic
    /// aspect-ratio mismatch against the real mode is an acceptable tradeoff for a rack preview
    /// card.</summary>
    private async Task<IImageSource> RenderThumbnailAsync(string templateId, PersistedTemplateDocument document, CancellationToken ct)
    {
        var elements = new List<TemplateElement>(document.Elements.Count);
        foreach (var element in document.Elements)
        {
            elements.Add(await ToTemplateElementAsync(templateId, element, ct).ConfigureAwait(false));
        }

        var baseCanvas = new BlankImageSource(ThumbnailWidth, ThumbnailHeight);
        var templateDocument = new TemplateDocument(Name: null, elements);
        return _preparer.ApplyTemplate(baseCanvas, templateDocument);
    }

    private async Task<TemplateElement> ToTemplateElementAsync(string templateId, PersistedTemplateElement element, CancellationToken ct)
    {
        var bounds = new NormalizedRect(element.X - (element.Width / 2), element.Y - (element.Height / 2), element.Width, element.Height);
        switch (element)
        {
            case PersistedTextElement text:
                // Off-scope finding from an earlier session pass (2026-08-17), fixed here while
                // already touching this exact line for Bold/Italic (2026-08-18): this thumbnail-
                // render path used to silently drop ShadowColor/ShadowOffsetX/ShadowOffsetY/
                // RotationDegrees/Gradient -- a template using any of those rendered its Ready
                // Rack/Template Library thumbnail flat and unrotated while the real template (and
                // the live editor) rendered the effect correctly. TxImageEditorPaneViewModel's own
                // BuildPersistedElementAsync/BuildTemplateDocument already forwarded all of these
                // correctly; only this one reconstruction path was missing them.
                return new TemplateTextElement(
                    bounds, text.Z, text.Text, new FontSpec(text.FontFamily, text.FontSizeRelative, text.Bold, text.Italic), text.Color,
                    text.StrokeColor, text.StrokeThickness,
                    text.ShadowColor, text.ShadowOffsetX, text.ShadowOffsetY, text.RotationDegrees,
                    text.GradientEnabled
                        ? new TextGradient(text.GradientKind, [new GradientColorStop(0f, text.GradientStartColor ?? text.Color), new GradientColorStop(1f, text.GradientEndColor ?? text.Color)])
                        : null,
                    text.StackColor, text.StackStepX, text.StackStepY);
            case PersistedBoxElement box:
                // Code-review finding (2026-09-01, box gradient fill): same thumbnail-render gap
                // PersistedTextElement's own case above was already fixed for once -- this path
                // silently dropped Gradient, rendering the Ready Rack/Template Library thumbnail
                // flat solid while the real template rendered the gradient correctly.
                return new TemplateBoxElement(
                    bounds, box.Z, box.FillColor, box.BorderColor, box.BorderThickness, box.Opacity, box.CornerRadius,
                    box.GradientEnabled
                        ? new TextGradient(box.GradientKind, [new GradientColorStop(0f, box.GradientStartColor ?? box.FillColor), new GradientColorStop(1f, box.GradientEndColor ?? box.FillColor)])
                        : null);
            case PersistedImageElement image:
                var assetPath = GetAssetPath(templateId, image.AssetFileName);
                var source = await _imageFileLoader.LoadOriginalAsync(assetPath, ct).ConfigureAwait(false);
                return new TemplateImageElement(bounds, image.Z, source, image.Fit);
            case PersistedLineElement line:
                // Deliberately NOT the shared `bounds` local above -- that's derived from the base
                // X/Y/Width/Height fields, which for a line are write-time-only convenience values
                // (see PersistedLineElement's own doc comment: endpoints are the sole truth on read).
                // A horizontal/vertical line's own base Height/Width is legitimately 0, and the
                // shared ApplyTemplate skip rule would drop it outright without the ink-inflated
                // bounds TemplateLineGeometry computes here.
                // Named width/height args (code-review finding): two adjacent doubles a swap
                // compiles for, and the whole point of the shared helper is that every caller agrees
                // on the same formula -- a silent width/height swap here would defeat that.
                var lineBounds = TemplateLineGeometry.ComputeInflatedBounds(
                    line.X1, line.Y1, line.X2, line.Y2, line.StrokeThickness,
                    imageWidthPx: ThumbnailWidth, imageHeightPx: ThumbnailHeight);
                return new TemplateLineElement(lineBounds, line.Z, line.X1, line.Y1, line.X2, line.Y2, line.StrokeColor, line.StrokeThickness, line.Opacity);
            default:
                throw new NotSupportedException($"Unrecognized {nameof(PersistedTemplateElement)}: {element.GetType()}.");
        }
    }

    private string GetTemplatesRoot()
        => _templatesRootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScanlineStudio", "Templates");

    private string GetTemplateDirectory(string templateId) => Path.Combine(GetTemplatesRoot(), templateId);

    /// <summary>Lowercase <c>[a-z0-9-]</c>, collapse repeated separators, trim, cap ~40 chars, fall
    /// back to a fixed literal if a non-Latin name (Japanese, Cyrillic, ...) sanitizes to empty
    /// (plan-review decision) — the mandatory <c>guid8</c> suffix (see <see cref="CreateTemplateId"/>)
    /// also defuses a Windows reserved device name (<c>con</c>, <c>prn</c>, <c>com1</c>, ...) a bare
    /// slug could otherwise collide with, so no separate reserved-name check is needed here.</summary>
    private static string Sanitize(string name)
    {
        var lowered = name.Trim().ToLowerInvariant();
        var replaced = NonSlugCharacterPattern().Replace(lowered, "-");
        var collapsed = RepeatedSeparatorPattern().Replace(replaced, "-").Trim('-');
        var capped = collapsed.Length > 40 ? collapsed[..40].Trim('-') : collapsed;
        return string.IsNullOrEmpty(capped) ? "template" : capped;
    }

    [GeneratedRegex("[^a-z0-9-]")]
    private static partial Regex NonSlugCharacterPattern();

    [GeneratedRegex("-{2,}")]
    private static partial Regex RepeatedSeparatorPattern();

    /// <summary>Flat black <see cref="IImageSource"/> — the thumbnail render's own base canvas
    /// (<see cref="ITransmitImagePreparer.ApplyTemplate"/> requires an <c>existingBase</c>). Written
    /// by hand rather than via <see cref="IImageFileLoader"/>/ImageSharp — no source file exists to
    /// load, and this class must stay ImageSharp-agnostic (see this file's own doc comment).</summary>
    private sealed class BlankImageSource : IImageSource
    {
        private readonly Rgb24[] _row;

        public BlankImageSource(int width, int height)
        {
            Width = width;
            Height = height;
            _row = new Rgb24[width];
        }

        public int Width { get; }

        public int Height { get; }

        public ReadOnlySpan<Rgb24> GetScanline(int y) => _row;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Template manifest at '{ManifestPath}' is corrupt or truncated; skipping this template, the rest of the rack listing is unaffected")]
        public static partial void CorruptManifestSkipped(ILogger logger, string manifestPath, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Cleanup of partially-imported template '{TemplateId}' failed")]
        public static partial void ImportCleanupFailed(ILogger logger, string templateId, Exception ex);
    }
}
