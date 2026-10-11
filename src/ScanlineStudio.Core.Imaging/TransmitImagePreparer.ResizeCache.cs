using System.Collections.Concurrent;
using System.Numerics;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
// NOT "using SixLabors.ImageSharp.Drawing" -- SixLabors.ImageSharp.Drawing.Path collides with
// System.IO.Path (used throughout this file, e.g. the constructor's Path.Combine). Reference
// SixLabors.ImageSharp.Drawing.RectangularPolygon fully-qualified at each use site instead.
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Imaging;

public sealed partial class TransmitImagePreparer
{
    private IImageSource GetOrCreateResizedImage(IImageSource source, int width, int height, ImageFitMode fit)
    {
        var key = (width, height, fit);
        if (_imageResizeCache.TryGetValue(source, out var existing) && existing.Entries.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var resized = fit switch
        {
            ImageFitMode.Stretch => Resize(source, width, height, preserveAspect: false),
            ImageFitMode.Contain => Resize(source, width, height, preserveAspect: true),
            ImageFitMode.Cover => ResizeCover(source, width, height),
            _ => throw new ArgumentOutOfRangeException(nameof(fit), fit, message: null),
        };

        if ((long)width * height > MaxCachedResizedImagePixels)
        {
            return resized;
        }

        var entries = _imageResizeCache.GetValue(source, static _ => new PerSourceResizeCache()).Entries;
        if (entries.Count >= MaxCachedResizesPerSource)
        {
            entries.Clear();
        }

        entries[key] = resized;
        return resized;
    }

    private static ArrayImageSource ResizeCover(IImageSource source, int width, int height)
    {
        using var image = ImageSharpPixelConversion.FromImageSource(source);
        image.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Crop,
        }));
        return ImageSharpPixelConversion.ToImageSource(image);
    }

    private sealed class PerSourceResizeCache
    {
        public ConcurrentDictionary<(int Width, int Height, ImageFitMode Fit), IImageSource> Entries { get; } = new();
    }
}
