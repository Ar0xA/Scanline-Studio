using System.Text.Json;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Application.Tests;

public sealed class TemplateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scanlinestudio-template-store-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeImageSourceWriter _imageSourceWriter = new();
    private readonly FakeImageFileLoader _imageFileLoader = new();
    private readonly FakeTransmitImagePreparer _preparer = new();

    private TemplateStore CreateStore() => new(_imageSourceWriter, _imageFileLoader, _preparer, _root);

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
                FontFamily: "Barlow", StrokeColor: new Rgb24(0, 0, 0), StrokeThickness: 0.01),
            new PersistedBoxElement(
                X: 0.5, Y: 0.5, Width: 1, Height: 1, Z: -1, Locked: true,
                FillColor: new Rgb24(20, 20, 20), BorderColor: null, BorderThickness: 0, Opacity: 0.8),
            new PersistedImageElement(
                X: 0.7, Y: 0.7, Width: 0.2, Height: 0.2, Z: 1, Locked: false,
                AssetFileName: "asset1.png", Fit: ImageFitMode.Cover,
                OriginKind: PersistedImageSourceKind.File, OriginPayload: "/some/original.png"),
        ]);

        await store.SaveAsync(templateId, "Contest", document);
        var loaded = await store.LoadAsync(templateId);

        Assert.Equal(3, loaded.Elements.Count);
        var text = Assert.IsType<PersistedTextElement>(loaded.Elements[0]);
        Assert.Equal("{his_call}", text.Text);
        Assert.Equal("Barlow", text.FontFamily);
        Assert.Equal(new Rgb24(0, 0, 0), text.StrokeColor);

        var box = Assert.IsType<PersistedBoxElement>(loaded.Elements[1]);
        Assert.True(box.Locked);
        Assert.Equal(0.8, box.Opacity);

        var image = Assert.IsType<PersistedImageElement>(loaded.Elements[2]);
        Assert.Equal("asset1.png", image.AssetFileName);
        Assert.Equal(ImageFitMode.Cover, image.Fit);
        Assert.Equal(PersistedImageSourceKind.File, image.OriginKind);
        Assert.Equal("/some/original.png", image.OriginPayload);
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

        foreach (var (element, discriminator) in new[] { (text, "text"), (box, "box"), (image, "image") })
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
}
