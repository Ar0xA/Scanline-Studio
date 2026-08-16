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

    /// <summary>Brightness/Contrast/Saturation map the slider's -50..50 range to ImageSharp's own
    /// 1.0-centered multiplier convention (0..2, 1.0 = identity). Gamma is hand-rolled via
    /// <c>ProcessPixelRowsAsVector4</c> -- ImageSharp 3.1.12/4.0.0 have no <c>GammaCorrection</c>
    /// operation at all (verified directly against the package's own API surface, not assumed) --
    /// applying <c>MathF.Pow</c> per RGB channel directly against the already sRGB-encoded pixel
    /// values (no linearization step: this is the GIMP/Photoshop midtone-gamma convention, not a
    /// physically-correct display-gamma one). Exponent is negated relative to the raw
    /// <c>out = in^gamma</c> formula (where an exponent above 1 DARKENS) so that positive slider
    /// values brighten, consistent with Brightness/Contrast's own sign convention. Gamma and
    /// Sharpen/Denoise are all conditionally SKIPPED at 0 rather than always invoked -- Gamma's
    /// hand-rolled per-pixel loop is real synchronous work (code-review finding: skipping it
    /// matters concretely since <c>RecomputePreview</c> calls this on every interactive
    /// pointer-move frame, and the common case once a user touches ANY slider is that most of the
    /// other five stay at 0); Sharpen/Denoise are skipped for a correctness reason instead --
    /// ImageSharp's Gaussian operations are not documented to throw at <c>sigma=0</c>, but a zero
    /// sigma collapses to a degenerate kernel (silent NaN/corrupted output, worse than a throw).
    /// Denoise runs BEFORE Sharpen (not declaration order) so sharpening doesn't amplify noise
    /// denoise was meant to remove. All non-skipped operations run in one <c>Mutate</c> chain (one
    /// pipeline pass, not N). **Precondition, found while testing**: ImageSharp's Gaussian
    /// convolution throws <see cref="ArgumentOutOfRangeException"/> when the kernel radius derived
    /// from sigma (up to ~9px at Sharpen/Denoise=100's sigma=3.0) exceeds the SOURCE image's own
    /// width/height -- not reachable via this app's real call site (only ever invoked on an
    /// already-<see cref="Resize"/>d image at a target mode's exact dimensions; the smallest real
    /// mode is 320x120), but a genuine constraint for any other caller passing a smaller
    /// image.</summary>
    public IImageSource ApplyAdjustments(IImageSource source, ImageAdjustments adjustments)
    {
        if (adjustments.IsIdentity)
        {
            return source;
        }

        using var image = ToImageSharp(source);

        image.Mutate(ctx =>
        {
            ctx.Brightness(1.0f + (float)(adjustments.Brightness / 50.0));
            ctx.Contrast(1.0f + (float)(adjustments.Contrast / 50.0));
            ctx.Saturate(1.0f + (float)(adjustments.Saturation / 50.0));

            // Code-review finding: unlike Brightness/Contrast/Saturate (genuinely cheap ImageSharp
            // built-ins), this hand-rolled per-pixel loop is real synchronous work -- skipping it
            // at Gamma=0 (same shape as the Sharpen/Denoise guards below, not just relying on
            // MathF.Pow(x, 1f) being a mathematical identity) avoids ~950k MathF.Pow calls on every
            // interactive preview frame (RecomputePreview fires on every pointer-move) whenever
            // ANY other slider is non-zero, which is the common case once a user touches a slider
            // at all.
            if (adjustments.Gamma != 0)
            {
                var gammaExponent = MathF.Pow(10f, -(float)(adjustments.Gamma / 100.0));
                ctx.ProcessPixelRowsAsVector4(row =>
                {
                    for (var i = 0; i < row.Length; i++)
                    {
                        var pixel = row[i];
                        row[i] = new System.Numerics.Vector4(
                            MathF.Pow(pixel.X, gammaExponent),
                            MathF.Pow(pixel.Y, gammaExponent),
                            MathF.Pow(pixel.Z, gammaExponent),
                            pixel.W);
                    }
                });
            }

            // Denoise BEFORE Sharpen (code-review finding) -- conventional order, so a user
            // adjusting both doesn't have Sharpen amplify noise that Denoise was meant to remove.
            if (adjustments.Denoise > 0)
            {
                var denoiseSigma = (float)(adjustments.Denoise / 100.0) * 3.0f;
                ctx.GaussianBlur(denoiseSigma);
            }

            if (adjustments.Sharpen > 0)
            {
                var sharpenSigma = 0.3f + ((float)(adjustments.Sharpen / 100.0) * 2.7f);
                ctx.GaussianSharpen(sharpenSigma);
            }
        });

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

    public IImageSource Rotate(IImageSource source)
    {
        using var image = ToImageSharp(source);
        image.Mutate(ctx => ctx.Rotate(RotateMode.Rotate90));
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
