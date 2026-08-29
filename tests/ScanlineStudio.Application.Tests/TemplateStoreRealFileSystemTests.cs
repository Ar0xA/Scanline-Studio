using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Application.Tests;

/// <summary>Phase 5 code-review finding: every other <c>TemplateStoreTests</c> fixture uses
/// <see cref="FakeImageSourceWriter"/>/<see cref="FakeTransmitImagePreparer"/>, which is why a real,
/// serious bug (the template's own <c>assets/</c> subdirectory was never created before an image
/// element's PNG was written to it -- every save of an image-bearing template failed silently) went
/// undetected by the full green suite. This file wires the REAL <see cref="ImageSourceWriter"/>,
/// <see cref="ImageFileLoader"/>, and <see cref="TransmitImagePreparer"/> against a real temp
/// directory -- no fakes anywhere in the image-writing path -- specifically to catch this class of
/// bug going forward.</summary>
public sealed class TemplateStoreRealFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scanlinestudio-template-store-realfs-tests", Guid.NewGuid().ToString("N"));
    private readonly TransmitImagePreparer _preparer = new(FontPath);

    private TemplateStore CreateStore() => new(new ImageSourceWriter(), new ImageFileLoader(), _preparer, NullLogger<TemplateStore>.Instance, _root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_TemplateWithImageElement_ActuallyWritesTheAssetFileToRealDisk()
    {
        // Sanity check that the real ImageSourceWriter/ImageFileLoader/TemplateStore trio round-trips
        // an image element through real disk and real ImageSharp compositing at all -- NOT a
        // regression test for the directory-creation bug itself (this test creates the assets/
        // directory up front, unlike the real app's own call order). That specific regression is
        // covered at the layer the actual fix lives in --
        // TxImageEditorPaneViewModelTests.SaveTemplateAsync_ImageElement_CreatesTheAssetsDirectoryOnRealDisk_
        // BeforeWritingTheAsset (ScanlineStudio.UI.Tests), since BuildPersistedElementAsync's own
        // Directory.CreateDirectory call is UI-layer code this project can't reference.
        var store = CreateStore();
        var templateId = store.CreateTemplateId("Real Photo");
        var assetPath = store.GetAssetPath(templateId, "photo.png");
        Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
        await new ImageSourceWriter().WritePngAsync(new FakeImageSource(4, 4, new Rgb24(10, 20, 30)), assetPath);

        await store.SaveAsync(templateId, "Real Photo", new PersistedTemplateDocument([
            new PersistedImageElement(0.5, 0.5, 0.3, 0.3, 0, false, "photo.png", ImageFitMode.Cover, PersistedImageSourceKind.File, null),
        ]));

        Assert.True(File.Exists(assetPath), $"Expected the asset PNG to exist on real disk at '{assetPath}'.");
        Assert.True(File.Exists(Path.Combine(_root, templateId, "template.json")));
        Assert.True(File.Exists(Path.Combine(_root, templateId, "thumbnail.png")));

        var loaded = await store.LoadAsync(templateId);
        var image = Assert.IsType<PersistedImageElement>(Assert.Single(loaded.Elements));
        Assert.Equal("photo.png", image.AssetFileName);
    }

    [Fact]
    public async Task SaveAsync_ThumbnailRenderFailure_LeavesNoManifestBehind_NotAListedButPreviewlessTemplate()
    {
        // Code-review finding: an earlier draft wrote template.json BEFORE rendering the thumbnail,
        // so a thumbnail-render failure (e.g. an image element's asset file is missing) left a
        // template permanently LISTED with no thumbnail.png -- ListAsync only checks for
        // template.json, and a thumbnail is never recomputed later. Rendering first means this
        // failure now aborts the whole save cleanly instead.
        var store = CreateStore();
        var templateId = store.CreateTemplateId("Broken");
        // Deliberately do NOT write the referenced asset file -- LoadOriginalAsync inside
        // RenderThumbnailAsync's own element-resolution step will throw FileNotFoundException.
        var document = new PersistedTemplateDocument([
            new PersistedImageElement(0.5, 0.5, 0.3, 0.3, 0, false, "missing.png", ImageFitMode.Cover, PersistedImageSourceKind.File, null),
        ]);

        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(templateId, "Broken", document));

        Assert.False(File.Exists(Path.Combine(_root, templateId, "template.json")));
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task ListAsync_FolderCopiedToADifferentNameByHand_UsesTheFoldersOwnNameNotTheStaleManifestId()
    {
        // Code-review finding: ListAsync used to trust manifest.Id (baked in at save time) instead
        // of the directory it actually found the manifest in. TemplateStore's own doc comment
        // advertises "share a template by copying the folder" as the real, supported export
        // mechanism -- copying (or renaming) a folder is exactly the scenario that used to produce a
        // TemplateMetadata whose Id disagreed with where it actually lives on disk, which crashed
        // ReadyRackViewModel.RefreshAsync's own ToDictionary on a duplicate key, and made
        // Delete/Load on the copy silently operate on the ORIGINAL folder instead.
        var store = CreateStore();
        var originalId = store.CreateTemplateId("Field Day");
        await store.SaveAsync(originalId, "Field Day", new PersistedTemplateDocument([]));

        var copiedDirectory = Path.Combine(_root, "field-day-copy_11111111");
        CopyDirectory(Path.Combine(_root, originalId), copiedDirectory);

        var all = await store.ListAsync();

        Assert.Equal(2, all.Count);
        var ids = all.Select(m => m.Id).ToList();
        Assert.Contains(originalId, ids);
        Assert.Contains("field-day-copy_11111111", ids);
        // The copy's metadata must resolve to ITS OWN folder, not the original's.
        var copyMetadata = all.Single(m => m.Id == "field-day-copy_11111111");
        Assert.Equal(Path.Combine(copiedDirectory, "thumbnail.png"), copyMetadata.ThumbnailPath);
    }

    [Fact]
    public async Task ExportThenImport_TemplateWithRealThumbnailAndAsset_BytesAreIdenticalAfterRoundTrip()
    {
        var store = CreateStore();
        var templateId = store.CreateTemplateId("Photo Card");
        var assetPath = store.GetAssetPath(templateId, "photo.png");
        Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
        await new ImageSourceWriter().WritePngAsync(new FakeImageSource(4, 4, new Rgb24(50, 60, 70)), assetPath);
        await store.SaveAsync(templateId, "Photo Card", new PersistedTemplateDocument([
            new PersistedImageElement(0.5, 0.5, 0.3, 0.3, 0, false, "photo.png", ImageFitMode.Cover, PersistedImageSourceKind.File, null),
        ]));
        var originalThumbnailBytes = await File.ReadAllBytesAsync(Path.Combine(_root, templateId, "thumbnail.png"));
        var originalAssetBytes = await File.ReadAllBytesAsync(assetPath);

        var zipPath = Path.Combine(_root, "photo-card.sstemplate");
        await store.ExportAsync(templateId, zipPath);
        var importedId = await store.ImportAsync(zipPath);

        Assert.NotEqual(templateId, importedId);
        var importedThumbnailBytes = await File.ReadAllBytesAsync(Path.Combine(_root, importedId, "thumbnail.png"));
        var importedAssetBytes = await File.ReadAllBytesAsync(Path.Combine(_root, importedId, "assets", "photo.png"));
        Assert.Equal(originalThumbnailBytes, importedThumbnailBytes);
        Assert.Equal(originalAssetBytes, importedAssetBytes);

        var importedDocument = await store.LoadAsync(importedId);
        var importedImage = Assert.IsType<PersistedImageElement>(Assert.Single(importedDocument.Elements));
        Assert.Equal("photo.png", importedImage.AssetFileName);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var subDirectory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(subDirectory, Path.Combine(destination, Path.GetFileName(subDirectory)));
        }
    }

    private static string FontPath { get; } = Path.Combine(FindRepoRoot(), "assets", "fonts", "DejaVuSansMono.ttf");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScanlineStudio.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (ScanlineStudio.sln) from test output directory.");
    }
}
