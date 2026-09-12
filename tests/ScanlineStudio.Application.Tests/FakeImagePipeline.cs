using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Application.Tests;

/// <summary>In-memory <see cref="IImageSourceWriter"/> -- writes go into <see cref="Files"/> instead
/// of real disk, matching this project's own hand-rolled `Fake*` test-double convention.</summary>
internal sealed class FakeImageSourceWriter : IImageSourceWriter
{
    public Dictionary<string, IImageSource> Files { get; } = [];

    public Task WritePngAsync(IImageSource source, string path, CancellationToken ct = default)
    {
        Files[path] = source;
        return Task.CompletedTask;
    }
}

/// <summary>In-memory <see cref="IImageFileLoader"/> counterpart to <see cref="FakeImageSourceWriter"/>
/// -- <see cref="LoadOriginalAsync"/> reads back whatever <see cref="Sources"/> has under the same
/// path, so a save-then-load round trip in a test doesn't touch real disk at all.
/// <paramref name="writer"/> is optional and, when supplied, is checked as a FALLBACK after
/// <see cref="Sources"/> -- a caller-chosen path (e.g. a test's own `store.GetAssetPath(id,
/// "asset1.png")`) still needs an explicit `Sources` entry as before, but a path this store minted
/// INTERNALLY (a random GUID the test could never predict in advance, e.g.
/// `TemplateStore.ImportLegacyMtmAsync`'s own companion-image asset) now round-trips automatically
/// through whatever the SAME test's <see cref="FakeImageSourceWriter"/> already wrote there --
/// closing a real testability gap (a save path that writes-then-immediately-reads-back its own new
/// asset, e.g. for a thumbnail render) without the test needing to know a GUID ahead of time.</summary>
internal sealed class FakeImageFileLoader(FakeImageSourceWriter? writer = null) : IImageFileLoader
{
    public Dictionary<string, IImageSource> Sources { get; } = [];

    public Task<IImageSource> LoadAsync(string path, int targetWidth, int targetHeight, CancellationToken ct = default)
        => LoadOriginalAsync(path, ct);

    public Task<IImageSource> LoadOriginalAsync(string path, CancellationToken ct = default)
    {
        if (Sources.TryGetValue(path, out var source))
        {
            return Task.FromResult(source);
        }

        if (writer is not null && writer.Files.TryGetValue(path, out var written))
        {
            return Task.FromResult(written);
        }

        throw new FileNotFoundException($"No fake source configured for '{path}'.");
    }
}

/// <summary>Minimal real <see cref="IImageSource"/> -- a flat single-color image, enough for
/// <see cref="TemplateStore"/>'s thumbnail-render call to have something real to composite.</summary>
internal sealed class FakeImageSource : IImageSource
{
    private readonly Rgb24[] _row;

    public FakeImageSource(int width, int height, Rgb24 color)
    {
        Width = width;
        Height = height;
        _row = Enumerable.Repeat(color, width).ToArray();
    }

    public int Width { get; }

    public int Height { get; }

    public ReadOnlySpan<Rgb24> GetScanline(int y) => _row;
}

/// <summary>Records <see cref="ApplyTemplate"/> calls instead of doing real compositing -- enough for
/// <see cref="TemplateStore"/>'s thumbnail-render tests, which only need to confirm it was called with
/// the right element count/shape, not exercise real ImageSharp drawing (that's
/// <c>ApplyTemplateTests</c>' own job in <c>ScanlineStudio.Core.Imaging.Tests</c>).</summary>
internal sealed class FakeTransmitImagePreparer : ITransmitImagePreparer
{
    public List<TemplateDocument> ApplyTemplateDocuments { get; } = [];

    public IImageSource Crop(IImageSource source, NormalizedRect region) => source;

    public IImageSource Resize(IImageSource source, int width, int height, bool preserveAspect)
        => new FakeImageSource(width, height, new Rgb24(0, 0, 0));

    public IImageSource ApplyAdjustments(IImageSource source, ImageAdjustments adjustments) => source;

    public IImageSource ApplyOverlay(IImageSource source, ImageOverlay overlay) => source;

    public IImageSource ApplyTemplate(IImageSource existingBase, TemplateDocument document)
    {
        ApplyTemplateDocuments.Add(document);
        return existingBase;
    }

    public double MeasureFittedFontSize(
        string text, FontSpec font, int imageHeightPx, int boundsWidthPx, int boundsHeightPx, double strokeThicknessRelative = 0,
        double shadowOffsetXRelative = 0, double shadowOffsetYRelative = 0, double rotationDegrees = 0,
        double stackStepXRelative = 0, double stackStepYRelative = 0)
        => font.Size * imageHeightPx;

    public IReadOnlyList<string> AvailableFontFamilies { get; } = ["DejaVu Sans Mono"];

    public IImageSource Rotate(IImageSource source) => new FakeImageSource(source.Height, source.Width, new Rgb24(0, 0, 0));

    // TX workflow modernization plan, Phase 7 -- flat fakes, same "return a plausibly-shaped result,
    // don't try to reproduce real pixel behavior" convention as every other method above.
    public List<(int X, int Y, int Width, int Height)> CropPixelsCalls { get; } = [];

    public IImageSource CropPixels(IImageSource source, int x, int y, int width, int height)
    {
        CropPixelsCalls.Add((x, y, width, height));
        return new FakeImageSource(width, height, new Rgb24(0, 0, 0));
    }

    public List<(IImageSource Background, IImageSource Overlay, int X, int Y)> CompositeCalls { get; } = [];

    public IImageSource Composite(IImageSource background, IImageSource overlay, int x, int y)
    {
        CompositeCalls.Add((background, overlay, x, y));
        return new FakeImageSource(background.Width, background.Height, new Rgb24(0, 0, 0));
    }
}
