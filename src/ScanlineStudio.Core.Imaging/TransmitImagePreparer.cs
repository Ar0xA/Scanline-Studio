using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Imaging;

/// <summary>ImageSharp-backed <see cref="ITransmitImagePreparer"/> — see spec/07-image-pipeline.md's
/// "TX image editor" section. Mirrors <see cref="ImageFileLoader"/>'s/<see cref="StockImageLibrary"/>'s
/// explicit-field-copy pixel pattern (never <c>MemoryMarshal.Cast</c> between the two unrelated
/// <c>Rgb24</c> types).</summary>
public sealed class TransmitImagePreparer : ITransmitImagePreparer
{
    private readonly FontFamily _fontFamily;

    public TransmitImagePreparer(string? fontFilePath = null)
    {
        var path = fontFilePath ?? Path.Combine(AppContext.BaseDirectory, "assets", "fonts", "DejaVuSansMono.ttf");
        var collection = new FontCollection();
        _fontFamily = collection.Add(path, System.Globalization.CultureInfo.InvariantCulture);
    }

    public IImageSource Crop(IImageSource source, NormalizedRect region)
    {
        using var image = ToImageSharp(source);

        var x = Math.Clamp((int)Math.Round(region.X * source.Width), 0, source.Width - 1);
        var y = Math.Clamp((int)Math.Round(region.Y * source.Height), 0, source.Height - 1);
        var width = Math.Clamp((int)Math.Round(region.Width * source.Width), 1, source.Width - x);
        var height = Math.Clamp((int)Math.Round(region.Height * source.Height), 1, source.Height - y);

        image.Mutate(ctx => ctx.Crop(new Rectangle(x, y, width, height)));
        return FromImageSharp(image);
    }

    public IImageSource Resize(IImageSource source, int width, int height, bool preserveAspect)
    {
        using var image = ToImageSharp(source);

        if (preserveAspect)
        {
            // Letterbox with solid black -- matches this app's raw-instrumentation aesthetic
            // direction, not a "smart" fill. ResizeMode.Pad does exactly this in one call: resize
            // to the largest size that fits within width x height preserving aspect, then pad the
            // remainder onto a width x height canvas.
            image.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(width, height),
                Mode = ResizeMode.Pad,
                PadColor = Color.Black,
            }));
        }
        else
        {
            // Stretch to fill exactly -- the user's own explicit ask for "stretch" as a distinct
            // option from aspect-preserving resize. Same resampler ImageFileLoader/StockImageLibrary
            // already use (ImageSharp's implicit default, Bicubic) -- named here so it's a stated
            // choice, not a repeated silent default.
            image.Mutate(ctx => ctx.Resize(width, height));
        }

        return FromImageSharp(image);
    }

    public IImageSource ApplyOverlay(IImageSource source, ImageOverlay overlay)
    {
        using var image = ToImageSharp(source);

        foreach (var element in overlay.Elements)
        {
            var fontSize = (float)(element.FontSizeRelative * source.Height);
            var font = _fontFamily.CreateFont(fontSize);
            var color = new Rgba32(element.Color.R, element.Color.G, element.Color.B, 255);

            var origin = new PointF((float)(element.X * source.Width), (float)(element.Y * source.Height));
            var options = new RichTextOptions(font)
            {
                Origin = origin,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                WrappingLength = source.Width, // clip at the image's own width -- v1 overflow rule
            };

            image.Mutate(ctx => ctx.DrawText(options, element.Text, color));
        }

        return FromImageSharp(image);
    }

    private static Image<SixLabors.ImageSharp.PixelFormats.Rgb24> ToImageSharp(IImageSource source)
    {
        var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(source.Width, source.Height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < source.Height; y++)
            {
                var sourceRow = source.GetScanline(y);
                var destinationRow = accessor.GetRowSpan(y);
                for (var x = 0; x < source.Width; x++)
                {
                    var pixel = sourceRow[x];
                    destinationRow[x] = new SixLabors.ImageSharp.PixelFormats.Rgb24(pixel.R, pixel.G, pixel.B);
                }
            }
        });
        return image;
    }

    private static ArrayImageSource FromImageSharp(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image)
    {
        var pixels = new Abstractions.Imaging.Rgb24[image.Width * image.Height];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < image.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < image.Width; x++)
                {
                    var pixel = row[x];
                    pixels[(y * image.Width) + x] = new Abstractions.Imaging.Rgb24(pixel.R, pixel.G, pixel.B);
                }
            }
        });

        return new ArrayImageSource(image.Width, image.Height, pixels);
    }
}
