using System.Text.Json;
using System.Text.RegularExpressions;
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
    private readonly string? _templatesRootOverride;

    public TemplateStore(IImageSourceWriter imageSourceWriter, IImageFileLoader imageFileLoader, ITransmitImagePreparer preparer)
        : this(imageSourceWriter, imageFileLoader, preparer, templatesRootOverride: null)
    {
    }

    /// <summary><paramref name="templatesRootOverride"/> is a test-only seam (no settings/UI exposes
    /// it in this phase, unlike <c>StockImageLibrary</c>'s own settings-driven directory override) --
    /// lets a test point this store at a real temp directory instead of the real user's Pictures
    /// folder, without needing a fake filesystem abstraction this codebase has no precedent for.</summary>
    public TemplateStore(IImageSourceWriter imageSourceWriter, IImageFileLoader imageFileLoader, ITransmitImagePreparer preparer, string? templatesRootOverride)
    {
        _imageSourceWriter = imageSourceWriter;
        _imageFileLoader = imageFileLoader;
        _preparer = preparer;
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
        => Path.Combine(GetTemplateDirectory(templateId), "assets", assetFileName);

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

        var manifest = new TemplateManifest(templateId, name, DateTimeOffset.Now, document.Elements);
        var json = JsonSerializer.Serialize(manifest, PersistedTemplateJsonContext.Default.TemplateManifest);
        await File.WriteAllTextAsync(Path.Combine(directory, "template.json"), json, ct).ConfigureAwait(false);
    }

    public async Task<PersistedTemplateDocument> LoadAsync(string templateId, CancellationToken ct = default)
    {
        var manifest = await ReadManifestAsync(GetTemplateDirectory(templateId), ct).ConfigureAwait(false);
        return new PersistedTemplateDocument(manifest.Elements);
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

            var json = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize(json, PersistedTemplateJsonContext.Default.TemplateManifest);
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
                return new TemplateTextElement(
                    bounds, text.Z, text.Text, new FontSpec(text.FontFamily, text.FontSizeRelative), text.Color,
                    text.StrokeColor, text.StrokeThickness);
            case PersistedBoxElement box:
                return new TemplateBoxElement(bounds, box.Z, box.FillColor, box.BorderColor, box.BorderThickness, box.Opacity);
            case PersistedImageElement image:
                var assetPath = GetAssetPath(templateId, image.AssetFileName);
                var source = await _imageFileLoader.LoadOriginalAsync(assetPath, ct).ConfigureAwait(false);
                return new TemplateImageElement(bounds, image.Z, source, image.Fit);
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
}
