using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.Processing;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Imaging;

/// <summary>ImageSharp-backed <see cref="IImageFileLoader"/> — see spec/07-image-pipeline.md. Pixel
/// conversion (see <see cref="ImageSharpPixelConversion"/> for why it's never a
/// <c>MemoryMarshal.Cast</c> between the two unrelated <c>Rgb24</c> types) is shared with
/// <see cref="StockImageLibrary"/>/<see cref="ReceivedImageBuffer"/>/<see cref="ImageSourceWriter"/>
/// (T1-15, production_audit.md) — this class stays responsible only for <c>AutoOrient</c>/resize
/// ordering, which varies per caller.
/// <para>Size guard: the header is read first; more than <see cref="MaxPixels"/> pixels is refused before
/// any decode. A picture longer than <see cref="MaxLongSide"/> px is scaled down while decoding to fit a
/// <see cref="MaxLongSide"/>-px square (aspect kept), far more than any SSTV or DRM frame needs. Only the
/// first frame of an animated or multi-page file is decoded. Every failure except cancellation is
/// logged and thrown as <see cref="ImageLoadException"/>.</para></summary>
public sealed partial class ImageFileLoader : IImageFileLoader
{
    public const long MaxPixels = 250_000_000;
    public const int MaxLongSide = 4096;

    private readonly ILogger<ImageFileLoader> _logger;

    public ImageFileLoader(ILogger<ImageFileLoader>? logger = null)
    {
        _logger = logger ?? NullLogger<ImageFileLoader>.Instance;
    }

    public async Task<IImageSource> LoadAsync(string path, int targetWidth, int targetHeight, CancellationToken ct = default)
    {
        using var image = await DecodeAsync(path, ct).ConfigureAwait(false);
        // spec/18-path-to-1.0.md High item 3: a phone photo with EXIF orientation used to load
        // sideways with no in-app remedy. AutoOrient reads the EXIF Orientation tag, physically
        // rotates/flips the pixel data to match, and resets the tag to Normal -- must run BEFORE
        // Resize, or the target width/height below gets applied to the still-sideways raw pixel
        // dimensions.
        image.Mutate(x => x.AutoOrient().Resize(targetWidth, targetHeight));
        return ImageSharpPixelConversion.ToImageSource(image);
    }

    public async Task<IImageSource> LoadOriginalAsync(string path, CancellationToken ct = default)
    {
        using var image = await DecodeAsync(path, ct).ConfigureAwait(false);
        image.Mutate(x => x.AutoOrient());
        return ImageSharpPixelConversion.ToImageSource(image);
    }

    private async Task<Image<SixLabors.ImageSharp.PixelFormats.Rgb24>> DecodeAsync(string path, CancellationToken ct)
    {
        try
        {
            // Sync Identify on a seekable file reads only the header; the async overload may buffer the whole file first.
            ImageInfo info = await Task.Run(() => Image.Identify(new DecoderOptions { MaxFrames = 1 }, path), ct).ConfigureAwait(false);
            if ((long)info.Width * info.Height > MaxPixels)
            {
                Log.TooLarge(_logger, path, info.Width, info.Height, MaxPixels);
                throw new ImageLoadException(ImageLoadFailure.TooLarge, path);
            }

            var options = new DecoderOptions { MaxFrames = 1 };
            if (Math.Max(info.Width, info.Height) > MaxLongSide)
            {
                options = new DecoderOptions { MaxFrames = 1, TargetSize = new Size(MaxLongSide, MaxLongSide) };
            }

            var image = await Image.LoadAsync<SixLabors.ImageSharp.PixelFormats.Rgb24>(options, path, ct).ConfigureAwait(false);
            if (options.TargetSize is not null)
            {
                Log.ScaledDown(_logger, path, info.Width, info.Height, image.Width, image.Height);
            }

            return image;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ImageLoadException)
        {
            var reason = Classify(ex);
            Log.LoadFailed(_logger, path, reason, ex);
            throw new ImageLoadException(reason, path, ex);
        }
    }

    private static ImageLoadFailure Classify(Exception ex) => ex switch
    {
        UnknownImageFormatException or NotSupportedException => ImageLoadFailure.NotAnImage,
        InvalidMemoryOperationException or OutOfMemoryException => ImageLoadFailure.TooLarge,
        { InnerException: InvalidMemoryOperationException } => ImageLoadFailure.TooLarge,
        IOException or UnauthorizedAccessException => ImageLoadFailure.Unreadable,
        _ => ImageLoadFailure.Corrupt,
    };

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Image {Path} refused: {Width}x{Height} is more than {MaxPixels} pixels")]
        public static partial void TooLarge(ILogger logger, string path, int width, int height, long maxPixels);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Image {Path} scaled down while loading: {Width}x{Height} -> {LoadedWidth}x{LoadedHeight}")]
        public static partial void ScaledDown(ILogger logger, string path, int width, int height, int loadedWidth, int loadedHeight);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Image {Path} could not be loaded ({Reason})")]
        public static partial void LoadFailed(ILogger logger, string path, ImageLoadFailure reason, Exception ex);
    }
}
