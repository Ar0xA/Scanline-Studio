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

    // Injectable only inside the assembly so failure tests can stop a write after real bytes
    // reach the staging file. Production always uses the standard asynchronous file writer.
    internal Func<string, string, CancellationToken, Task> WriteManifestFileAsync { get; init; } = File.WriteAllTextAsync;

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
        var manifestPath = Path.Combine(directory, "template.json");
        var temporaryPath = manifestPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteManifestFileAsync(temporaryPath, json, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(temporaryPath, manifestPath, overwrite: true);
            Log.TemplateSaved(_logger, templateId);
        }
        finally
        {
            DeleteTemporaryFile(temporaryPath);
        }
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
            // Older builds wrote manifests directly, so a crash mid-save could leave a
            // truncated/corrupt template.json -- a
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

        var temporaryPath = destinationZipPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            // Finish the central directory and close the file before publishing the ZIP.
            await using (var zipStream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write))
            {
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true);
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

            ct.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationZipPath, overwrite: true);
            Log.TemplateExported(_logger, templateId, destinationZipPath);
        }
        finally
        {
            DeleteTemporaryFile(temporaryPath);
        }
    }

    private void DeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.TemporaryFileCleanupFailed(_logger, path, ex);
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

    // Code-review finding: the reader's own length-field hardening only bounds allocations DRIVEN BY
    // fields inside the file -- it can't guard the initial whole-file read itself. Generous relative
    // to any real .mtm (KB-scale text/boxes plus at most a handful of embedded photos), tight enough
    // to reject a multi-gigabyte file (mistaken or hostile) before it ever touches memory.
    private const long MaxLegacyMtmFileSizeBytes = 50L * 1024 * 1024;

    /// <inheritdoc/>
    public async Task<LegacyMtmImportResult> ImportLegacyMtmAsync(string mtmPath, CancellationToken ct = default)
    {
        var fileLength = new FileInfo(mtmPath).Length;
        if (fileLength > MaxLegacyMtmFileSizeBytes)
        {
            throw new LegacyMtmFormatException(
                $"'{Path.GetFileName(mtmPath)}' is {fileLength:N0} bytes, larger than any real .mtm/.mti file should be "
                + $"(max {MaxLegacyMtmFileSizeBytes:N0}) -- not imported.");
        }

        var fileBytes = await File.ReadAllBytesAsync(mtmPath, ct).ConfigureAwait(false);

        // Throws LegacyMtmFormatException/LegacyMtmOleNotImportableException on anything not a valid,
        // importable file -- deliberately BEFORE minting a template id or creating any directory, so
        // a rejected file never leaves an empty template folder behind.
        var root = LegacyMtmReader.ReadTemplate(fileBytes);

        var name = Path.GetFileNameWithoutExtension(mtmPath);
        var templateId = CreateTemplateId(name);
        var directory = GetTemplateDirectory(templateId);
        Directory.CreateDirectory(directory);

        try
        {
            var conversion = await LegacyMtmImportAdapter.ConvertAsync(
                root, (bitmap, token) => WriteLegacyBitmapAssetAsync(templateId, bitmap, token), ct).ConfigureAwait(false);

            var elements = conversion.Elements;
            var notes = conversion.Notes;
            var companionImagePath = FindLegacyCompanionImagePath(mtmPath);
            if (companionImagePath is not null)
            {
                // Code-review finding: the companion image is a BONUS, same design premise as
                // FindLegacyCompanionImagePath's own null-is-normal contract -- an unreadable/corrupt
                // companion must degrade the same way an ABSENT one does (skip it, note it), not fail
                // an otherwise-valid .mtm import that would have succeeded before this lookup existed.
                try
                {
                    var backgroundElement = await ImportLegacyCompanionImageAsBackgroundAsync(templateId, companionImagePath, ct)
                        .ConfigureAwait(false);
                    // Prepended, not appended -- Z=-1 (below every element the adapter itself numbers
                    // starting at 0) guarantees this renders first regardless of list position,
                    // matching "background" render order without needing to renumber anything else.
                    elements = [backgroundElement, .. elements];
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.CompanionImageImportFailed(_logger, companionImagePath, ex);
                    notes = [.. notes, $"Found the paired legacy picture '{Path.GetFileName(companionImagePath)}' but couldn't read it -- imported without a background."];
                }
            }

            await SaveAsync(templateId, name, new PersistedTemplateDocument(elements), ct).ConfigureAwait(false);
            return new LegacyMtmImportResult(templateId, notes);
        }
        catch
        {
            // Same freshly-minted-id-only cleanup convention as ImportAsync's own catch block above.
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
    }

    // t{N}.mtm (any digit count, no zero-padding -- Main.cpp's own sprintf("%st%d.mtm", StockDir,
    // n+1)) -- .mtm ONLY. Code-review finding: legacy's sprintf never produces "t{N}.mti" (.mti is a
    // DIFFERENT legacy feature, "Template items", ComLib.cpp:1395/:1399) -- an earlier version of
    // this pattern accepted .mti too, which could false-positive a user's own unrelated t1.mti file
    // into looking like a numbered stock slot.
    private static readonly System.Text.RegularExpressions.Regex StockSlotFileNamePattern =
        new(@"^t(\d+)\.mtm$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Legacy pairs a numbered stock-slot template with a same-numbered picture file, saved
    /// and loaded as two INDEPENDENT files sharing one index -- never embedded in the `.mtm` itself
    /// (confirmed directly: `TMmsstv::LoadStockTemp`/`LoadBitmapS`/`Main.cpp`'s `PBoxTXDragDrop`
    /// handler load `t{n+1}.mtm` and `TxStock{n+1}.bmp`/`.jpg` side by side, gated on two SEPARATE
    /// checkboxes -- a user can recall either alone). `Current.mtm`/`Current.bmp` is the same
    /// base-name pairing, used for the app's own "restore last session" state instead of a named
    /// slot. Returns <see langword="null"/> when the picked file doesn't match either naming pattern,
    /// or no sibling image exists next to it -- both are normal, not errors: most `.mtm` files are
    /// not numbered stock slots at all, and even a real stock-slot template may have been saved with
    /// no picture recalled alongside it.</summary>
    private static string? FindLegacyCompanionImagePath(string mtmPath)
    {
        var directory = Path.GetDirectoryName(mtmPath);
        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        var fileName = Path.GetFileName(mtmPath);
        string baseName;
        if (string.Equals(fileName, "Current.mtm", StringComparison.OrdinalIgnoreCase))
        {
            baseName = "Current";
        }
        else
        {
            var match = StockSlotFileNamePattern.Match(fileName);
            if (!match.Success)
            {
                return null;
            }

            baseName = $"TxStock{match.Groups[1].Value}";
        }

        // Code-review finding: the .mtm match above is case-insensitive (legacy's own authoring
        // filesystem, Windows, is too), but File.Exists is NOT case-insensitive on Linux/macOS -- an
        // all-uppercase legacy folder ("T1.MTM" + "TXSTOCK1.BMP") would match the template name and
        // then silently find no companion. Enumerate the directory once and compare names
        // case-insensitively instead of probing exact-case candidate paths. Built by hand, not
        // ToDictionary, since a case-SENSITIVE filesystem can legally hold two entries that only
        // differ by case (e.g. "TxStock1.bmp" and "txstock1.bmp" both present) -- first one wins
        // rather than throwing on a duplicate key.
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            entries.TryAdd(Path.GetFileName(path), path);
        }

        // .bmp checked first -- legacy's own LoadBitmapSN prefers whichever format sys.m_UseJPEG
        // selects, defaulting to .bmp; this import has no access to that live setting, so it checks
        // both and prefers the format legacy defaults to.
        foreach (var extension in new[] { ".bmp", ".jpg" })
        {
            if (entries.TryGetValue(baseName + extension, out var candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Imports an already-real companion image file (found by
    /// <see cref="FindLegacyCompanionImagePath"/>) as a full-canvas background element -- unlike
    /// <see cref="WriteLegacyBitmapAssetAsync"/>, no temp-file bridge is needed, since this path is
    /// already a real file on disk, not bytes extracted from inside a `.mtm` stream.</summary>
    private async Task<PersistedImageElement> ImportLegacyCompanionImageAsBackgroundAsync(
        string templateId, string imagePath, CancellationToken ct)
    {
        var source = await _imageFileLoader.LoadOriginalAsync(imagePath, ct).ConfigureAwait(false);
        var assetFileName = $"{Guid.NewGuid():N}.png";
        Directory.CreateDirectory(Path.Combine(GetTemplateDirectory(templateId), "assets"));
        await _imageSourceWriter.WritePngAsync(source, GetAssetPath(templateId, assetFileName), ct).ConfigureAwait(false);

        // Code-review finding: legacy's own two draw paths for this picture (Main.cpp:9394-9400) are
        // KSIS-checked -> StretchCopyBitmapHW (aspect-distorting full-canvas stretch) or unchecked ->
        // a plain 1:1 Draw(0,0) -- BOTH preserve every source pixel. Stretch is the only ImageFitMode
        // that matches either branch; Cover is the one mode that would crop content neither branch
        // ever does. Also matches this same importer's own embedded-bitmap path
        // (LegacyMtmImportAdapter.cs), which already uses Stretch for the identical reason.
        return new PersistedImageElement(
            X: 0.5, Y: 0.5, Width: 1.0, Height: 1.0, Z: -1, Locked: true,
            assetFileName, ImageFitMode.Stretch, PersistedImageSourceKind.File, OriginPayload: null,
            IsBackground: true);
    }

    /// <summary>Bridges a raw embedded legacy bitmap into this store's own asset convention.
    /// <see cref="IImageFileLoader"/> is path-only (no in-memory byte-array overload), so the bytes
    /// touch a scratch temp file once before <see cref="IImageSourceWriter"/> can re-encode them as
    /// this template's own PNG asset -- the temp file is always deleted afterward, success or
    /// failure, since it is scratch, not part of the template.</summary>
    private async Task<string> WriteLegacyBitmapAssetAsync(string templateId, MtmBitmap bitmap, CancellationToken ct)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bmp");
        try
        {
            await File.WriteAllBytesAsync(tempPath, bitmap.Bytes, ct).ConfigureAwait(false);
            var source = await _imageFileLoader.LoadOriginalAsync(tempPath, ct).ConfigureAwait(false);

            var assetFileName = $"{Guid.NewGuid():N}.png";
            Directory.CreateDirectory(Path.Combine(GetTemplateDirectory(templateId), "assets"));
            await _imageSourceWriter.WritePngAsync(source, GetAssetPath(templateId, assetFileName), ct).ConfigureAwait(false);
            return assetFileName;
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort: a scratch temp file, not template content -- never worth failing or
                // even logging the import over. Code-review finding: catching only IOException let a
                // delete-permission failure (UnauthorizedAccessException) escape this finally and
                // mask whatever real exception was already propagating from the try above.
            }
        }
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
                var bitmapFill = await LoadTextBitmapFillAsync(templateId, text, ct).ConfigureAwait(false);
                return new TemplateTextElement(
                    bounds, text.Z, text.Text, new FontSpec(text.FontFamily, text.FontSizeRelative, text.Bold, text.Italic), text.Color,
                    text.StrokeColor, text.StrokeThickness,
                    text.ShadowColor, text.ShadowOffsetX, text.ShadowOffsetY, text.RotationDegrees,
                    text.GradientEnabled
                        ? new TextGradient(text.GradientKind, [new GradientColorStop(0f, text.GradientStartColor ?? text.Color), new GradientColorStop(1f, text.GradientEndColor ?? text.Color)])
                        : null,
                    text.StackColor, text.StackStepX, text.StackStepY,
                    bitmapFill);
            case PersistedBoxElement box:
                // Code-review finding (2026-09-01, box gradient fill): same thumbnail-render gap
                // PersistedTextElement's own case above was already fixed for once -- this path
                // silently dropped Gradient, rendering the Ready Rack/Template Library thumbnail
                // flat solid while the real template rendered the gradient correctly.
                //
                // TX editor gap-items plan, item 3 (perspective transform, 2026-09-02) -- corners
                // are RAW (this thumbnail-reconstruction path has no crop-relative projection
                // concept at all, unlike the live editor's own TxImageEditorPaneViewModel), and
                // Bounds is rebuilt from their own bbox (the shared ToBoundingBox helper every
                // caller uses, per that struct's own doc comment), not the generic X/Y/Width/Height
                // fields -- same "don't trust the redundant flat fields, corners are the sole
                // truth" convention PersistedLineElement's own endpoints already established below.
                var boxPerspective = box.PerspectiveEnabled
                    ? new PerspectiveCorners(box.Corner0X, box.Corner0Y, box.Corner1X, box.Corner1Y, box.Corner2X, box.Corner2Y, box.Corner3X, box.Corner3Y)
                    : (PerspectiveCorners?)null;
                return new TemplateBoxElement(
                    boxPerspective?.ToBoundingBox() ?? bounds, box.Z, box.FillColor, box.BorderColor, box.BorderThickness, box.Opacity, box.CornerRadius,
                    box.GradientEnabled
                        ? new TextGradient(box.GradientKind, [new GradientColorStop(0f, box.GradientStartColor ?? box.FillColor), new GradientColorStop(1f, box.GradientEndColor ?? box.FillColor)])
                        : null,
                    boxPerspective, box.FillEnabled);
            case PersistedImageElement image:
                var assetPath = GetAssetPath(templateId, image.AssetFileName);
                var source = await _imageFileLoader.LoadOriginalAsync(assetPath, ct).ConfigureAwait(false);
                var imagePerspective = image.PerspectiveEnabled
                    ? new PerspectiveCorners(image.Corner0X, image.Corner0Y, image.Corner1X, image.Corner1Y, image.Corner2X, image.Corner2Y, image.Corner3X, image.Corner3Y)
                    : (PerspectiveCorners?)null;
                return new TemplateImageElement(imagePerspective?.ToBoundingBox() ?? bounds, image.Z, source, image.Fit, imagePerspective);
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

    /// <summary>TX editor gap-items plan, item 4b (picture fill) -- deliberately degrades to "no
    /// picture fill" (null, same as the element's own <c>BitmapFillEnabled: false</c> shape) and
    /// LOGS rather than throwing, unlike <see cref="PersistedImageElement"/>'s own asset load two
    /// cases up (whose throw is allowed to abort this whole reconstruction, an accepted existing
    /// behavior this method doesn't change). A picture fill is a decorative text effect, not the
    /// element's own core content the way an image element's picture IS its content -- a missing or
    /// corrupt fill asset (a copied-but-not-fully-synced template folder, a manually edited
    /// template.json referencing a filename that was never written) should render the text with its
    /// Gradient/solid fallback instead of taking down the ENTIRE Ready Rack/Template Library
    /// thumbnail render (or, via <see cref="RenderThumbnailAsync"/>'s own throw-aborts-SaveAsync
    /// behavior, the whole template save) over one cosmetic field. <see cref="GetAssetPath"/> itself
    /// can throw synchronously (its own traversal guard) -- covered by the same try, not a second
    /// one.</summary>
    private async Task<IImageSource?> LoadTextBitmapFillAsync(string templateId, PersistedTextElement text, CancellationToken ct)
    {
        if (!text.BitmapFillEnabled || string.IsNullOrEmpty(text.BitmapFillAssetFileName))
        {
            return null;
        }

        try
        {
            var assetPath = GetAssetPath(templateId, text.BitmapFillAssetFileName);
            return await _imageFileLoader.LoadOriginalAsync(assetPath, ct).ConfigureAwait(false);
        }
        // Round-1 code-review finding: must not swallow OperationCanceledException -- see
        // ListAsync's own doc comment two rows up in this file for why a real cancellation must
        // still abort the whole call, not be treated as "one bad asset."
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.BitmapFillAssetLoadFailed(_logger, templateId, text.BitmapFillAssetFileName, ex);
            return null;
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
        [LoggerMessage(Level = LogLevel.Information, Message = "Template {TemplateId} saved")]
        public static partial void TemplateSaved(ILogger logger, string templateId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Template {TemplateId} exported to {Path}")]
        public static partial void TemplateExported(ILogger logger, string templateId, string path);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to remove temporary template file {Path}")]
        public static partial void TemporaryFileCleanupFailed(ILogger logger, string path, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Template manifest at '{ManifestPath}' is corrupt or truncated; skipping this template, the rest of the rack listing is unaffected")]
        public static partial void CorruptManifestSkipped(ILogger logger, string manifestPath, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Cleanup of partially-imported template '{TemplateId}' failed")]
        public static partial void ImportCleanupFailed(ILogger logger, string templateId, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Legacy .mtm companion image '{ImagePath}' could not be read; imported the template without a background")]
        public static partial void CompanionImageImportFailed(ILogger logger, string imagePath, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Template '{TemplateId}' text picture-fill asset '{AssetFileName}' could not be loaded; rendering that element without its picture fill")]
        public static partial void BitmapFillAssetLoadFailed(ILogger logger, string templateId, string assetFileName, Exception ex);
    }
}
