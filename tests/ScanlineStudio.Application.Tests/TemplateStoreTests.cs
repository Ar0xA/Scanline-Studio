using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Application.Tests;

public sealed class TemplateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scanlinestudio-template-store-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeImageSourceWriter _imageSourceWriter = new();
    private readonly FakeImageFileLoader _imageFileLoader = new();
    private readonly FakeTransmitImagePreparer _preparer = new();

    private TemplateStore CreateStore() => new(_imageSourceWriter, _imageFileLoader, _preparer, NullLogger<TemplateStore>.Instance, _root);

    [Fact]
    public async Task SaveAsync_PartialManifestWriteFailurePreservesExistingTemplate()
    {
        var store = CreateStore();
        var id = store.CreateTemplateId("Original");
        await store.SaveAsync(id, "Original", new PersistedTemplateDocument([]));
        var manifestPath = Path.Combine(_root, id, "template.json");
        var original = await File.ReadAllBytesAsync(manifestPath);
        var failingStore = new TemplateStore(_imageSourceWriter, _imageFileLoader, _preparer, NullLogger<TemplateStore>.Instance, _root)
        {
            WriteManifestFileAsync = async (path, json, ct) =>
            {
                await File.WriteAllTextAsync(path, json[..10], ct);
                throw new IOException("Injected partial manifest write failure");
            },
        };

        await Assert.ThrowsAsync<IOException>(() => failingStore.SaveAsync(id, "Replacement", new PersistedTemplateDocument([])));

        Assert.Equal(original, await File.ReadAllBytesAsync(manifestPath));
        Assert.Equal("Original", Assert.Single(await store.ListAsync()).Name);
        Assert.Empty((await store.LoadAsync(id)).Elements);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, id), "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_SuccessfullyOverwritesExistingManifest()
    {
        var store = CreateStore();
        var id = store.CreateTemplateId("Original");
        await store.SaveAsync(id, "Original", new PersistedTemplateDocument([]));
        await store.SaveAsync(id, "Replacement", new PersistedTemplateDocument([]));
        Assert.Equal("Replacement", Assert.Single(await store.ListAsync()).Name);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, id), "*.tmp"));
    }

    [Fact]
    public async Task ExportAsync_SourceReadFailurePreservesExistingBundle()
    {
        var store = CreateStore();
        var id = store.CreateTemplateId("Original");
        await store.SaveAsync(id, "Original", new PersistedTemplateDocument([]));
        var destination = Path.Combine(_root, "previous.sstemplate");
        await store.ExportAsync(id, destination);
        var original = await File.ReadAllBytesAsync(destination);
        var assetPath = store.GetAssetPath(id, "locked.png");
        Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
        await File.WriteAllTextAsync(assetPath, "asset bytes");
        using (var locked = new FileStream(assetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => store.ExportAsync(id, destination));
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
        using var archive = ZipFile.OpenRead(destination);
        Assert.NotNull(archive.GetEntry("template.json"));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void CreateTemplateId_SanitizesName_LowercasesAndReplacesInvalidCharacters()
    {
        var store = CreateStore();

        var id = store.CreateTemplateId("My Contest Card!");

        // slug_guid8 -- exactly one underscore separating the two halves, guid8 is 8 hex chars.
        var parts = id.Split('_');
        Assert.Equal(2, parts.Length);
        Assert.Equal("my-contest-card", parts[0]);
        Assert.Equal(8, parts[1].Length);
        Assert.Matches("^[0-9a-f]{8}$", parts[1]);
    }

    [Fact]
    public void CreateTemplateId_NonLatinNameSanitizesToEmpty_FallsBackToFixedLiteral()
    {
        var store = CreateStore();

        var id = store.CreateTemplateId("こんにちは");

        Assert.StartsWith("template_", id);
    }

    [Fact]
    public void CreateTemplateId_CalledTwiceWithSameName_ReturnsDistinctIds()
    {
        var store = CreateStore();

        var first = store.CreateTemplateId("Field Day");
        var second = store.CreateTemplateId("Field Day");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsTextBoxAndImageElements()
    {
        var store = CreateStore();
        var templateId = store.CreateTemplateId("Contest");
        var imageAssetPath = store.GetAssetPath(templateId, "asset1.png");
        var imageSource = new FakeImageSource(4, 4, new Rgb24(10, 20, 30));
        _imageFileLoader.Sources[imageAssetPath] = imageSource;

        var document = new PersistedTemplateDocument([
            new PersistedTextElement(
                X: 0.5, Y: 0.2, Width: 0.3, Height: 0.1, Z: 0, Locked: false,
                Text: "{his_call}", FontSizeRelative: 0.08, Color: new Rgb24(255, 255, 255),
                FontFamily: "Barlow", StrokeColor: new Rgb24(0, 0, 0), StrokeThickness: 0.01,
                ShadowColor: new Rgb24(50, 50, 50), ShadowOffsetX: 0.03, ShadowOffsetY: 0.04, RotationDegrees: 15,
                GradientEnabled: true, GradientKind: TextGradientKind.Radial,
                GradientStartColor: new Rgb24(255, 0, 0), GradientEndColor: new Rgb24(0, 255, 0)),
            new PersistedBoxElement(
                X: 0.5, Y: 0.5, Width: 1, Height: 1, Z: -1, Locked: true,
                FillColor: new Rgb24(20, 20, 20), BorderColor: null, BorderThickness: 0, Opacity: 0.8,
                // TX editor gap-items plan (2026-09-01, box gradient fill) -- same 4 trailing
                // scalars as PersistedTextElement's own gradient, round-tripped the identical way.
                GradientEnabled: true, GradientKind: TextGradientKind.Vertical,
                GradientStartColor: new Rgb24(10, 20, 30), GradientEndColor: new Rgb24(40, 50, 60),
                // TX editor gap-items plan, item 3 (perspective transform, 2026-09-02) -- 9 flat
                // scalars, round-tripped the identical way as every other trailing addition here.
                PerspectiveEnabled: true,
                Corner0X: 0.1, Corner0Y: 0.1, Corner1X: 0.9, Corner1Y: 0.15,
                Corner2X: 0.85, Corner2Y: 0.9, Corner3X: 0.05, Corner3Y: 0.95),
            new PersistedImageElement(
                X: 0.7, Y: 0.7, Width: 0.2, Height: 0.2, Z: 1, Locked: true,
                AssetFileName: "asset1.png", Fit: ImageFitMode.Cover,
                OriginKind: PersistedImageSourceKind.File, OriginPayload: "/some/original.png",
                IsBackground: true,
                PerspectiveEnabled: true,
                Corner0X: 0.6, Corner0Y: 0.6, Corner1X: 0.8, Corner1Y: 0.6,
                Corner2X: 0.8, Corner2Y: 0.8, Corner3X: 0.6, Corner3Y: 0.8),
        ]);

        await store.SaveAsync(templateId, "Contest", document);
        var loaded = await store.LoadAsync(templateId);

        Assert.Equal(3, loaded.Elements.Count);
        var text = Assert.IsType<PersistedTextElement>(loaded.Elements[0]);
        Assert.Equal("{his_call}", text.Text);
        Assert.Equal("Barlow", text.FontFamily);
        Assert.Equal(new Rgb24(0, 0, 0), text.StrokeColor);
        // Phase 8 (YONIQ-style text-effects follow-up): shadow/rotation/gradient all round-trip as
        // plain scalars on PersistedTextElement -- no new JsonSerializerContext registration needed
        // since the gradient is the VM's own fixed 2-stop shape, not a variable-length list (see
        // PersistedTextElement's own doc comment for why that sidesteps the usual "gradient needs a
        // new record + registration" cost this class of change normally carries).
        Assert.Equal(new Rgb24(50, 50, 50), text.ShadowColor);
        Assert.Equal(0.03, text.ShadowOffsetX);
        Assert.Equal(0.04, text.ShadowOffsetY);
        Assert.Equal(15, text.RotationDegrees);
        Assert.True(text.GradientEnabled);
        Assert.Equal(TextGradientKind.Radial, text.GradientKind);
        Assert.Equal(new Rgb24(255, 0, 0), text.GradientStartColor);
        Assert.Equal(new Rgb24(0, 255, 0), text.GradientEndColor);

        var box = Assert.IsType<PersistedBoxElement>(loaded.Elements[1]);
        Assert.True(box.Locked);
        Assert.Equal(0.8, box.Opacity);
        Assert.True(box.GradientEnabled);
        Assert.Equal(TextGradientKind.Vertical, box.GradientKind);
        Assert.Equal(new Rgb24(10, 20, 30), box.GradientStartColor);
        Assert.Equal(new Rgb24(40, 50, 60), box.GradientEndColor);
        Assert.True(box.PerspectiveEnabled);
        Assert.Equal(0.1, box.Corner0X);
        Assert.Equal(0.1, box.Corner0Y);
        Assert.Equal(0.9, box.Corner1X);
        Assert.Equal(0.15, box.Corner1Y);
        Assert.Equal(0.85, box.Corner2X);
        Assert.Equal(0.9, box.Corner2Y);
        Assert.Equal(0.05, box.Corner3X);
        Assert.Equal(0.95, box.Corner3Y);

        var image = Assert.IsType<PersistedImageElement>(loaded.Elements[2]);
        Assert.Equal("asset1.png", image.AssetFileName);
        Assert.Equal(ImageFitMode.Cover, image.Fit);
        Assert.Equal(PersistedImageSourceKind.File, image.OriginKind);
        Assert.Equal("/some/original.png", image.OriginPayload);
        // Phase 6 (spec/15-template-designer.md): IsBackground IS persisted (plan-review blocker --
        // Locked already round-trips, so leaving IsBackground unpersisted would round-trip a
        // background element into a WORSE state than before Phase 6).
        Assert.True(image.IsBackground);
        Assert.True(image.PerspectiveEnabled);
        Assert.Equal(0.6, image.Corner0X);
        Assert.Equal(0.8, image.Corner2X);
    }

    [Fact]
    public async Task SaveAsync_RendersAndWritesAThumbnail_ViaApplyTemplate()
    {
        var store = CreateStore();
        var templateId = store.CreateTemplateId("Contest");
        var document = new PersistedTemplateDocument([
            new PersistedBoxElement(0.5, 0.5, 1, 1, 0, false, new Rgb24(1, 2, 3), null, 0, 1.0),
        ]);

        await store.SaveAsync(templateId, "Contest", document);

        Assert.Single(_preparer.ApplyTemplateDocuments);
        Assert.Single(_preparer.ApplyTemplateDocuments[0].Elements);
        var thumbnailPath = Path.Combine(_root, templateId, "thumbnail.png");
        Assert.True(_imageSourceWriter.Files.ContainsKey(thumbnailPath));
    }

    /// <summary>Code-review finding (2026-09-01, box gradient fill): <c>ToTemplateElementAsync</c>'s
    /// box case silently dropped Gradient, so a gradient box's Ready Rack/Template Library thumbnail
    /// rendered flat solid while the real template rendered the gradient correctly -- same bug class
    /// <c>PersistedTextElement</c>'s own case was already fixed for once, now fixed for box too.</summary>
    [Fact]
    public async Task SaveAsync_GradientBox_PassesTheGradientToTheThumbnailRenderer()
    {
        var store = CreateStore();
        var templateId = store.CreateTemplateId("Gradient thumbnail");
        var document = new PersistedTemplateDocument([
            new PersistedBoxElement(
                0.5, 0.5, 1, 1, 0, false, new Rgb24(1, 2, 3), null, 0, 1.0,
                GradientEnabled: true, GradientKind: TextGradientKind.Radial,
                GradientStartColor: new Rgb24(10, 20, 30), GradientEndColor: new Rgb24(40, 50, 60)),
        ]);

        await store.SaveAsync(templateId, "Gradient thumbnail", document);

        var thumbnailBox = Assert.IsType<TemplateBoxElement>(Assert.Single(_preparer.ApplyTemplateDocuments[0].Elements));
        if (thumbnailBox.Gradient is not { } gradient)
        {
            Assert.Fail("Expected a non-null Gradient.");
            return;
        }

        Assert.Equal(TextGradientKind.Radial, gradient.Kind);
        Assert.Equal(new Rgb24(10, 20, 30), gradient.Stops[0].Color);
        Assert.Equal(new Rgb24(40, 50, 60), gradient.Stops[1].Color);
    }

    /// <summary>TX editor gap-items plan, item 3 (perspective transform, 2026-09-02) -- same
    /// "ToTemplateElementAsync is a second hand-maintained switch" bug class the gradient/bitmap-fill
    /// tests above already pin, applied to Perspective. Also pins the RAW-corners bbox rule
    /// (PerspectiveCorners.ToBoundingBox on the un-projected corners, not the base X/Y/Width/Height
    /// fields) -- this document's own X/Y/Width/Height deliberately do NOT match the corners' real
    /// bbox, so a test that passed by coincidentally reading the base fields instead would fail here.</summary>
    [Fact]
    public async Task SaveAsync_PerspectiveBox_PassesTheCornersAndTheirOwnBoundingBoxToTheThumbnailRenderer()
    {
        var store = CreateStore();
        var templateId = store.CreateTemplateId("Perspective thumbnail");
        var document = new PersistedTemplateDocument([
            new PersistedBoxElement(
                X: 0.5, Y: 0.5, Width: 1, Height: 1, Z: 0, Locked: false,
                FillColor: new Rgb24(1, 2, 3), BorderColor: null, BorderThickness: 0, Opacity: 1.0,
                PerspectiveEnabled: true,
                Corner0X: 0.2, Corner0Y: 0.3, Corner1X: 0.7, Corner1Y: 0.25,
                Corner2X: 0.65, Corner2Y: 0.8, Corner3X: 0.15, Corner3Y: 0.75),
        ]);

        await store.SaveAsync(templateId, "Perspective thumbnail", document);

        var thumbnailBox = Assert.IsType<TemplateBoxElement>(Assert.Single(_preparer.ApplyTemplateDocuments[0].Elements));
        if (thumbnailBox.Perspective is not { } corners)
        {
            Assert.Fail("Expected a non-null Perspective.");
            return;
        }

        Assert.Equal(0.2, corners.Corner0X);
        Assert.Equal(0.3, corners.Corner0Y);
        Assert.Equal(0.65, corners.Corner2X);
        Assert.Equal(0.8, corners.Corner2Y);

        // bbox of the 4 corners above: X in [0.15, 0.7], Y in [0.25, 0.8] -- NOT the document's own
        // X:0.5/Y:0.5/Width:1/Height:1.
        Assert.Equal(0.15, thumbnailBox.Bounds.X, precision: 10);
        Assert.Equal(0.25, thumbnailBox.Bounds.Y, precision: 10);
        Assert.Equal(0.55, thumbnailBox.Bounds.Width, precision: 10);
        Assert.Equal(0.55, thumbnailBox.Bounds.Height, precision: 10);
    }

    /// <summary>TX editor gap-items plan, item 4b (picture fill, 2026-09-01) -- plan-review's own
    /// finding: <c>ToTemplateElementAsync</c> is the SECOND hand-maintained switch (the first being
    /// <c>TxImageEditorPaneViewModel.BuildTemplateElement</c>) that a new text-fill field must reach,
    /// same bug class the Gradient thumbnail test immediately above already pins for box.</summary>
    [Fact]
    public async Task SaveAsync_TextWithBitmapFill_PassesTheLoadedBitmapToTheThumbnailRenderer()
    {
        var store = CreateStore();
        var templateId = store.CreateTemplateId("Picture fill thumbnail");
        var assetPath = store.GetAssetPath(templateId, "fill.png");
        var fillSource = new FakeImageSource(4, 4, new Rgb24(9, 9, 9));
        _imageFileLoader.Sources[assetPath] = fillSource;
        var document = new PersistedTemplateDocument([
            new PersistedTextElement(
                0.5, 0.5, 0.4, 0.2, 0, false, "W1AW", 0.2, new Rgb24(0, 0, 0), "DejaVu Sans Mono", null, 0,
                BitmapFillEnabled: true, BitmapFillAssetFileName: "fill.png"),
        ]);

        await store.SaveAsync(templateId, "Picture fill thumbnail", document);

        var thumbnailText = Assert.IsType<TemplateTextElement>(Assert.Single(_preparer.ApplyTemplateDocuments[0].Elements));
        Assert.Same(fillSource, thumbnailText.BitmapFill);
    }

    /// <summary>Plan-review's own explicit decision: a missing/corrupt picture-fill asset must
    /// DEGRADE (log + render without the fill), never throw and abort the whole save/thumbnail
    /// render over one decorative field -- unlike <see cref="PersistedImageElement"/>'s own asset
    /// load, whose throw IS allowed to abort (an image element's picture is its core content, not
    /// decoration).</summary>
    [Fact]
    public async Task SaveAsync_TextWithBitmapFillButTheAssetIsMissing_DegradesInsteadOfThrowing()
    {
        var store = CreateStore();
        var templateId = store.CreateTemplateId("Picture fill missing asset");
        // Deliberately NOT registered in _imageFileLoader.Sources -- LoadOriginalAsync throws
        // FileNotFoundException, same as a real missing file on disk.
        var document = new PersistedTemplateDocument([
            new PersistedTextElement(
                0.5, 0.5, 0.4, 0.2, 0, false, "W1AW", 0.2, new Rgb24(0, 0, 0), "DejaVu Sans Mono", null, 0,
                BitmapFillEnabled: true, BitmapFillAssetFileName: "missing.png"),
        ]);

        await store.SaveAsync(templateId, "Picture fill missing asset", document);

        var thumbnailText = Assert.IsType<TemplateTextElement>(Assert.Single(_preparer.ApplyTemplateDocuments[0].Elements));
        Assert.Null(thumbnailText.BitmapFill);
    }

    [Fact]
    public async Task ListAsync_ReEnumeratesDirectoryEveryCall_SeesATemplateSavedAfterFirstList()
    {
        var store = CreateStore();
        Assert.Empty(await store.ListAsync());

        var templateId = store.CreateTemplateId("Contest");
        await store.SaveAsync(templateId, "Contest", new PersistedTemplateDocument([]));

        var afterSave = await store.ListAsync();
        Assert.Single(afterSave);
        Assert.Equal("Contest", afterSave[0].Name);
        Assert.Equal(templateId, afterSave[0].Id);
    }

    [Fact]
    public async Task ListAsync_OneCorruptManifest_SkipsItButStillListsTheRest()
    {
        // Closes a real coverage gap flagged by Tier A Batch 10 chunk 10b (docs/functional-audit-playbook.md):
        // File.WriteAllTextAsync in SaveAsync is not atomic, so a crash/power-loss mid-save can leave
        // a truncated/corrupt template.json -- previously this killed the ENTIRE rack listing (every
        // other template's manifest too), not just the one bad template, for a store whose whole
        // design explicitly supports hand-copying/moving folders around.
        var store = CreateStore();
        var goodId = store.CreateTemplateId("Good");
        await store.SaveAsync(goodId, "Good", new PersistedTemplateDocument([]));

        var corruptId = store.CreateTemplateId("Corrupt");
        var corruptDirectory = Path.Combine(_root, corruptId);
        Directory.CreateDirectory(corruptDirectory);
        await File.WriteAllTextAsync(Path.Combine(corruptDirectory, "template.json"), "{ this is not valid json");

        var results = await store.ListAsync();

        Assert.Single(results);
        Assert.Equal(goodId, results[0].Id);
    }

    [Fact]
    public async Task LoadAsync_ManifestWithNullElementsProperty_ReturnsEmptyDocumentInsteadOfThrowing()
    {
        // Tier B audit finding: a missing or explicitly-null "Elements" property in a hand-edited
        // template.json deserializes with no JsonException (this format explicitly supports
        // hand-copying/editing template folders, per this class's own doc comment) -- LoadAsync used
        // to NRE downstream (a caller reading .Count) instead of degrading gracefully.
        var store = CreateStore();
        var templateId = store.CreateTemplateId("Hand-edited");
        var directory = Path.Combine(_root, templateId);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "template.json"), $$"""{"Id":"{{templateId}}","Name":"Hand-edited","SavedAt":"2026-08-22T00:00:00+00:00"}""");

        var document = await store.LoadAsync(templateId);

        Assert.Empty(document.Elements);
    }

    [Fact]
    public async Task LoadAsync_ManifestWithANullElementInTheList_SkipsItInsteadOfThrowing()
    {
        // Tier B audit finding: a hand-edited "Elements":[null, {...}] used to NRE downstream -- this
        // file's own RenderThumbnailAsync/ToTemplateElementAsync dereferences each element directly
        // on a re-save (the switch's own `default:` arm calls element.GetType()). Derives the JSON
        // from a real save (not a hand-typed literal) so this test doesn't need to know Rgb24's own
        // exact JSON shape, then hand-injects the one malformed "null" element a real save could
        // never produce on its own.
        var store = CreateStore();
        var templateId = store.CreateTemplateId("Hand-edited");
        await store.SaveAsync(templateId, "Hand-edited", new PersistedTemplateDocument([
            new PersistedBoxElement(0.5, 0.5, 0.2, 0.2, 0, false, new Rgb24(1, 2, 3), null, 0, 1.0),
        ]));
        var manifestPath = Path.Combine(_root, templateId, "template.json");
        var json = await File.ReadAllTextAsync(manifestPath);
        var corrupted = json.Replace("\"Elements\":[", "\"Elements\":[null,");
        Assert.NotEqual(json, corrupted); // sanity: the replace actually matched something
        await File.WriteAllTextAsync(manifestPath, corrupted);

        var document = await store.LoadAsync(templateId);

        Assert.Single(document.Elements);
        Assert.IsType<PersistedBoxElement>(document.Elements[0]);
    }

    [Theory]
    [InlineData("../escaped.png")]
    [InlineData("sub/escaped.png")]
    [InlineData("/etc/passwd")]
    public void GetAssetPath_AssetFileNameEscapesTheAssetsFolder_Throws(string maliciousAssetFileName)
    {
        // Tier B audit finding: assetFileName comes straight from a persisted (and, per this
        // format's own "share by copying the folder" design, possibly hand-edited or downloaded)
        // template.json -- Path.Combine neither rejects "../" traversal nor a rooted path, so a
        // malicious/malformed shared template could point this app at an arbitrary file elsewhere on
        // disk. The write path is never at risk (BuildPersistedElementAsync always mints a fresh
        // Guid.NewGuid():N name), but the read path needed a guard.
        var store = CreateStore();
        var templateId = store.CreateTemplateId("Contest");

        Assert.Throws<InvalidOperationException>(() => store.GetAssetPath(templateId, maliciousAssetFileName));
    }

    [Fact]
    public void GetAssetPath_OrdinaryAssetFileName_StillWorks()
    {
        var store = CreateStore();
        var templateId = store.CreateTemplateId("Contest");

        var path = store.GetAssetPath(templateId, "3f9c2b1a.png");

        Assert.EndsWith(Path.Combine("assets", "3f9c2b1a.png"), path);
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheTemplateDirectory_SubsequentListDoesNotIncludeIt()
    {
        var store = CreateStore();
        var templateId = store.CreateTemplateId("Contest");
        await store.SaveAsync(templateId, "Contest", new PersistedTemplateDocument([]));
        Assert.Single(await store.ListAsync());

        await store.DeleteAsync(templateId);

        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task GetAssetPath_IsStableBeforeAndAfterSave_SameTemplateIdSameAssetName()
    {
        var store = CreateStore();
        var templateId = store.CreateTemplateId("Contest");

        var beforeSave = store.GetAssetPath(templateId, "a.png");
        await store.SaveAsync(templateId, "Contest", new PersistedTemplateDocument([]));
        var afterSave = store.GetAssetPath(templateId, "a.png");

        Assert.Equal(beforeSave, afterSave);
    }

    /// <summary>Plan-review-flagged historical .NET polymorphic-source-gen failure mode: a plain
    /// round-trip test (serialize then deserialize through the SAME context) would still pass even if
    /// the source-gen fast path silently dropped the <c>$type</c> discriminator and every element
    /// quietly deserialized as the base type. Asserts the literal string is present in the raw JSON,
    /// not just that round-tripping works.</summary>
    [Fact]
    public void PersistedTemplateElement_SerializesWithATypeDiscriminator()
    {
        PersistedTemplateElement text = new PersistedTextElement(
            0, 0, 0.1, 0.1, 0, false, "hi", 0.1, new Rgb24(1, 2, 3), "Barlow", null, 0);
        PersistedTemplateElement box = new PersistedBoxElement(0, 0, 0.1, 0.1, 0, false, new Rgb24(1, 2, 3), null, 0, 1);
        PersistedTemplateElement image = new PersistedImageElement(
            0, 0, 0.1, 0.1, 0, false, "a.png", ImageFitMode.Contain, PersistedImageSourceKind.File, null);
        PersistedTemplateElement line = new PersistedLineElement(
            0.5, 0.5, 0.4, 0, 0, false, 0.3, 0.5, 0.7, 0.5, new Rgb24(1, 2, 3), 0.01);

        foreach (var (element, discriminator) in new[] { (text, "text"), (box, "box"), (image, "image"), (line, "line") })
        {
            var json = JsonSerializer.Serialize(element, PersistedTemplateJsonContext.Default.PersistedTemplateElement);
            Assert.Contains("\"$type\"", json);
            Assert.Contains($"\"{discriminator}\"", json);
        }
    }

    [Fact]
    public void PersistedTemplateElement_RoundTripsThroughBaseTypeDeserialization_PreservesDerivedFields()
    {
        PersistedTemplateElement original = new PersistedTextElement(
            0.1, 0.2, 0.3, 0.4, 5, true, "K1ABC", 0.09, new Rgb24(9, 8, 7), "Barlow", new Rgb24(1, 1, 1), 0.02);

        var json = JsonSerializer.Serialize(original, PersistedTemplateJsonContext.Default.PersistedTemplateElement);
        var roundTripped = JsonSerializer.Deserialize(json, PersistedTemplateJsonContext.Default.PersistedTemplateElement);

        var text = Assert.IsType<PersistedTextElement>(roundTripped);
        Assert.Equal(original, text);
    }

    [Fact]
    public void PersistedLineElement_RoundTripsThroughBaseTypeDeserialization_PreservesEndpointsAndStroke()
    {
        PersistedTemplateElement original = new PersistedLineElement(
            0.5, 0.5, 0.4, 0, 3, true, 0.3, 0.5, 0.7, 0.5, new Rgb24(200, 10, 10), 0.015, 0.9);

        var json = JsonSerializer.Serialize(original, PersistedTemplateJsonContext.Default.PersistedTemplateElement);
        var roundTripped = JsonSerializer.Deserialize(json, PersistedTemplateJsonContext.Default.PersistedTemplateElement);

        var line = Assert.IsType<PersistedLineElement>(roundTripped);
        Assert.Equal(original, line);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsLineElement()
    {
        var store = CreateStore();
        var templateId = store.CreateTemplateId("Contest");
        var document = new PersistedTemplateDocument([
            new PersistedLineElement(
                X: 0.5, Y: 0.5, Width: 0.4, Height: 0, Z: 2, Locked: false,
                X1: 0.3, Y1: 0.5, X2: 0.7, Y2: 0.5, StrokeColor: new Rgb24(255, 0, 0), StrokeThickness: 0.02, Opacity: 0.75),
        ]);

        await store.SaveAsync(templateId, "Contest", document);
        var loaded = await store.LoadAsync(templateId);

        var line = Assert.IsType<PersistedLineElement>(Assert.Single(loaded.Elements));
        Assert.Equal(0.3, line.X1);
        Assert.Equal(0.5, line.Y1);
        Assert.Equal(0.7, line.X2);
        Assert.Equal(0.5, line.Y2);
        Assert.Equal(new Rgb24(255, 0, 0), line.StrokeColor);
        Assert.Equal(0.02, line.StrokeThickness);
        Assert.Equal(0.75, line.Opacity);
    }

    /// <summary>Line-element plan-review, Amendment F -- pins the actual fix, not just the schema
    /// mechanics: <c>TemplateStore.SaveAsync</c> must pass <c>CurrentSchemaVersion</c> EXPLICITLY at
    /// its own <c>new TemplateManifest(...)</c> call site, since the constructor's own default is
    /// deliberately the literal 1 (see <see cref="TemplateManifest"/>'s own doc comment), not
    /// <see cref="TemplateManifest.CurrentSchemaVersion"/> -- a regression here (reverting to relying
    /// on the constructor default) would silently write every NEW template as version 1 forever.</summary>
    [Fact]
    public async Task SaveAsync_WritesTheCurrentSchemaVersionOnEveryRealSave()
    {
        var store = CreateStore();
        var templateId = store.CreateTemplateId("Contest");

        await store.SaveAsync(templateId, "Contest", new PersistedTemplateDocument([]));

        var manifestPath = Path.Combine(_root, templateId, "template.json");
        var json = await File.ReadAllTextAsync(manifestPath);
        var manifest = JsonSerializer.Deserialize(json, PersistedTemplateJsonContext.Default.TemplateManifest);
        Assert.NotNull(manifest);
        Assert.Equal(TemplateManifest.CurrentSchemaVersion, manifest!.SchemaVersion);
    }

    /// <summary>Same bug class as <see cref="SaveAsync_GradientBox_PassesTheGradientToTheThumbnailRenderer"/>
    /// immediately below (a second reconstruction site silently missing a field/using the wrong
    /// bounds) -- <c>ToTemplateElementAsync</c>'s line case must use the ink-inflated bounds
    /// (<c>TemplateLineGeometry.ComputeInflatedBounds</c>), not the shared base-X/Y/Width/Height
    /// bounds every other case uses, or a horizontal/vertical line's own thumbnail would render as
    /// nothing at all (dropped by <c>ApplyTemplate</c>'s own degenerate-bbox skip rule).</summary>
    [Fact]
    public async Task SaveAsync_HorizontalLine_PassesANonDegenerateBoundsToTheThumbnailRenderer()
    {
        var store = CreateStore();
        var templateId = store.CreateTemplateId("Line thumbnail");
        var document = new PersistedTemplateDocument([
            new PersistedLineElement(
                X: 0.5, Y: 0.5, Width: 0.4, Height: 0, Z: 0, Locked: false,
                X1: 0.3, Y1: 0.5, X2: 0.7, Y2: 0.5, StrokeColor: new Rgb24(255, 0, 0), StrokeThickness: 0.02),
        ]);

        await store.SaveAsync(templateId, "Line thumbnail", document);

        var thumbnailLine = Assert.IsType<TemplateLineElement>(Assert.Single(_preparer.ApplyTemplateDocuments[0].Elements));
        Assert.True(thumbnailLine.Bounds.Width > 0, "Expected a non-degenerate (ink-inflated) bounds width.");
        Assert.True(thumbnailLine.Bounds.Height > 0, "Expected a non-degenerate (ink-inflated) bounds height.");
        Assert.Equal(0.3, thumbnailLine.X1);
        Assert.Equal(0.7, thumbnailLine.X2);
    }

    // ui_transition_plan.md step 13 (native template bundle export/import) --------------------------

    [Fact]
    public void TemplateManifest_DeserializeMissingSchemaVersion_DefaultsToVersion1()
    {
        // Verifies the exact claim TemplateManifest's own doc comment makes: a record's POSITIONAL
        // constructor-parameter default (unlike an init-only property initializer -- see
        // AudioDeviceSettings.TxVolumePercent's own doc comment for the confirmed-broken case) IS
        // honored by System.Text.Json for a JSON member absent from the payload. Every real
        // template.json saved before this field existed lacks "SchemaVersion" entirely -- this is
        // exactly that shape, not a hypothetical. Asserts the LITERAL 1, not CurrentSchemaVersion
        // (line-element plan-review, Amendment F): a pre-existing file that never had this field
        // really IS version 1, and that must stay true even after CurrentSchemaVersion moves past 1
        // -- an assertion against the constant itself would silently stop proving anything the moment
        // the two values diverge.
        var json = """{"Id":"old_12345678","Name":"Old Template","SavedAt":"2026-01-01T00:00:00+00:00","Elements":[]}""";

        var manifest = JsonSerializer.Deserialize(json, PersistedTemplateJsonContext.Default.TemplateManifest);

        Assert.NotNull(manifest);
        Assert.Equal(1, manifest!.SchemaVersion);
    }

    [Fact]
    public async Task ExportThenImport_MintsAFreshIdAndPreservesNameAndElements()
    {
        var store = CreateStore();
        var originalId = store.CreateTemplateId("Field Day Card");
        var elements = new List<PersistedTemplateElement>
        {
            new PersistedBoxElement(0.5, 0.5, 0.2, 0.2, 0, false, new Rgb24(10, 20, 30), null, 0, 1),
        };
        await store.SaveAsync(originalId, "Field Day Card", new PersistedTemplateDocument(elements));

        var zipPath = Path.Combine(_root, "export.sstemplate");
        await File.WriteAllTextAsync(zipPath, "Previous bundle");
        await store.ExportAsync(originalId, zipPath);

        var importedId = await store.ImportAsync(zipPath);

        Assert.NotEqual(originalId, importedId);
        var imported = await store.LoadAsync(importedId);
        Assert.Equal(elements, imported.Elements);
        var metadata = (await store.ListAsync()).Single(m => m.Id == importedId);
        Assert.Equal("Field Day Card", metadata.Name);
    }

    [Fact]
    public async Task ExportAsync_UnknownTemplateId_Throws()
    {
        var store = CreateStore();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ExportAsync("does-not-exist_11111111", Path.Combine(_root, "out.sstemplate")));
    }

    [Fact]
    public async Task ExportAsync_UnknownTemplateId_DoesNotTruncateAPreExistingFileAtTheDestinationPath()
    {
        // Auditor finding: the destination FileStream used to open with FileMode.Create (truncating
        // immediately) BEFORE the source template.json was ever read -- an unknown/vanished
        // templateId left a 0-byte .sstemplate behind at a path the user may have chosen to overwrite
        // a real, different file.
        var store = CreateStore();
        Directory.CreateDirectory(_root);
        var destinationPath = Path.Combine(_root, "pre-existing.sstemplate");
        await File.WriteAllTextAsync(destinationPath, "not a template bundle, just a pre-existing file");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ExportAsync("does-not-exist_11111111", destinationPath));

        Assert.Equal("not a template bundle, just a pre-existing file", await File.ReadAllTextAsync(destinationPath));
    }

    [Fact]
    public async Task ImportAsync_ZipMissingTemplateJson_Throws()
    {
        var store = CreateStore();
        var zipPath = BuildRawZip(("thumbnail.png", "not a real png"u8.ToArray()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportAsync(zipPath));
        Assert.Contains("template.json", ex.Message);
    }

    [Theory]
    [InlineData("../evil.png")]
    [InlineData("/etc/evil.png")]
    [InlineData("assets/../../evil.png")]
    [InlineData("assets\\evil.png")]
    public async Task ImportAsync_UnsafeEntryName_ThrowsAndCreatesNoTemplateFolder(string unsafeEntryName)
    {
        var store = CreateStore();
        var zipPath = BuildRawZip(
            ("template.json", MinimalManifestJson("Evil", schemaVersion: 1)),
            (unsafeEntryName, "x"u8.ToArray()));
        var directoriesBefore = Directory.Exists(_root) ? Directory.GetDirectories(_root).Length : 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportAsync(zipPath));

        var directoriesAfter = Directory.Exists(_root) ? Directory.GetDirectories(_root).Length : 0;
        Assert.Equal(directoriesBefore, directoriesAfter);
    }

    [Fact]
    public async Task ImportAsync_SchemaVersionNewerThanSupported_ThrowsBeforeDeserializingElements()
    {
        var store = CreateStore();
        // The elements array intentionally contains a "$type" this build's [JsonDerivedType] list
        // does not recognize -- proving the schema-version check runs (and rejects) BEFORE the typed,
        // polymorphic deserialize ever touches Elements, not just that some exception is thrown.
        var manifestJson = """{"Id":"future_12345678","Name":"Future","SavedAt":"2026-01-01T00:00:00+00:00","SchemaVersion":999,"Elements":[{"$type":"future-effect"}]}""";
        var zipPath = BuildRawZip(("template.json", Encoding.UTF8.GetBytes(manifestJson)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportAsync(zipPath));
        Assert.Contains("999", ex.Message);
        Assert.DoesNotContain(await store.ListAsync(), m => m.Name == "Future");
    }

    [Fact]
    public async Task ImportAsync_ManifestMissingName_ThrowsInvalidOperationExceptionNotNullReferenceException()
    {
        // Auditor finding: manifest.Name is untrusted (a hand-edited or attacker-crafted
        // template.json can omit "Name" entirely) -- CreateTemplateId's own Sanitize calls
        // name.Trim() unconditionally, which used to throw a raw NullReferenceException here instead
        // of this method's own documented "always InvalidOperationException" contract.
        var store = CreateStore();
        var manifestJson = """{"Id":"noname_12345678","SavedAt":"2026-01-01T00:00:00+00:00","SchemaVersion":1,"Elements":[]}""";
        var zipPath = BuildRawZip(("template.json", Encoding.UTF8.GetBytes(manifestJson)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportAsync(zipPath));
        Assert.Contains("name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportAsync_TooManyEntries_ThrowsWithoutExtractingAnything()
    {
        var store = CreateStore();
        var entries = new List<(string, byte[])> { ("template.json", MinimalManifestJson("Many", schemaVersion: 1)) };
        for (var i = 0; i < 1001; i++)
        {
            entries.Add(($"assets/{i}.png", "x"u8.ToArray()));
        }

        var zipPath = BuildRawZip(entries.ToArray());
        var directoriesBefore = Directory.GetDirectories(_root).Length;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportAsync(zipPath));

        Assert.Contains("too many entries", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(directoriesBefore, Directory.GetDirectories(_root).Length);
    }

    [Fact]
    public async Task ImportAsync_SingleEntryExceedsPerEntryCap_Throws()
    {
        // Auditor finding: this test writes an honest zip header (BuildRawZip goes through the real
        // ZipArchive writer), so it does NOT by itself prove the cap ignores ZipArchiveEntry.Length --
        // an implementation that trusted the header would pass this test identically. That specific
        // property (ReadEntryWithLimitAsync counts bytes off the actual inflate stream, never
        // consulting Length) is verified by reading TemplateStore.cs itself, not by this test; this
        // test only proves the cap actually triggers for genuinely oversized content, using an
        // all-zero buffer so the zip file on disk stays tiny despite the large decompressed size.
        var store = CreateStore();
        var oversizedAsset = new byte[(64 * 1024 * 1024) + 1024];
        var zipPath = BuildRawZip(
            ("template.json", MinimalManifestJson("Bomb", schemaVersion: 1)),
            ("assets/big.png", oversizedAsset));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportAsync(zipPath));
        Assert.Contains("maximum allowed size", ex.Message);
    }

    [Fact]
    public async Task ImportAsync_TotalAcrossEntriesExceedsCap_ThrowsEvenWhenEachEntryIsIndividuallyUnderThePerEntryCap()
    {
        var store = CreateStore();
        var underPerEntryCap = new byte[55 * 1024 * 1024];
        var zipPath = BuildRawZip(
            ("template.json", MinimalManifestJson("Bomb2", schemaVersion: 1)),
            ("assets/a.png", underPerEntryCap),
            ("assets/b.png", underPerEntryCap),
            ("assets/c.png", underPerEntryCap),
            ("assets/d.png", underPerEntryCap));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportAsync(zipPath));
        Assert.Contains("total", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportAsync_TruncatedZip_ThrowsAndCreatesNoTemplateFolder()
    {
        var store = CreateStore();
        var originalId = store.CreateTemplateId("Truncate Me");
        await store.SaveAsync(originalId, "Truncate Me", new PersistedTemplateDocument([]));
        var zipPath = Path.Combine(_root, "truncated.sstemplate");
        await store.ExportAsync(originalId, zipPath);

        var bytes = await File.ReadAllBytesAsync(zipPath);
        await File.WriteAllBytesAsync(zipPath, bytes[..(bytes.Length / 2)]);
        var directoriesBefore = Directory.GetDirectories(_root).Length;

        await Assert.ThrowsAnyAsync<Exception>(() => store.ImportAsync(zipPath));

        Assert.Equal(directoriesBefore, Directory.GetDirectories(_root).Length);
    }

    private static byte[] MinimalManifestJson(string name, int schemaVersion)
        => Encoding.UTF8.GetBytes($$"""{"Id":"placeholder","Name":"{{name}}","SavedAt":"2026-01-01T00:00:00+00:00","SchemaVersion":{{schemaVersion}},"Elements":[]}""");

    private string BuildRawZip(params (string EntryName, byte[] Content)[] entries)
    {
        var zipPath = Path.Combine(_root, $"raw-{Guid.NewGuid():N}.sstemplate");
        Directory.CreateDirectory(_root);
        using (var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            foreach (var (entryName, content) in entries)
            {
                var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
        }

        return zipPath;
    }
}
