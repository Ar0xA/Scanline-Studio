using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;

namespace ScanlineStudio.Core.Imaging;

/// <summary><see cref="IReceivedImageDecoder"/> for DRM-received files. QSSTV keeps whatever the sender named the
/// file and loads it by content (a ".jp2" may hold JPEG bytes), so the format is sniffed from the magic
/// bytes. JPEG 2000 goes to <see cref="IJpeg2000Codec"/>; JPEG, PNG, BMP and GIF go to ImageSharp with its own
/// <see cref="Configuration"/> limited to those four formats, one frame, and a size check before any pixel
/// decode. One decode runs at a time.</summary>
public sealed partial class DrmImageDecoder : IReceivedImageDecoder
{
    /// <summary>The ImageSharp size refusal, the same as the JPEG 2000 codec's.</summary>
    public const long MaxDecodePixels = 16_000_000;

    private static readonly byte[] Jp2Signature = [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A];
    private static readonly byte[] Jp2Magic = [0x0D, 0x0A, 0x87, 0x0A];
    private static readonly byte[] J2kMagic = [0xFF, 0x4F, 0xFF, 0x51];
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly IJpeg2000Codec _codec;
    private readonly ILogger<DrmImageDecoder> _logger;
    private readonly DecoderOptions _options;
    private readonly object _gate = new();

    public DrmImageDecoder(IJpeg2000Codec codec, ILogger<DrmImageDecoder> logger)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(logger);
        _codec = codec;
        _logger = logger;
        _options = new DecoderOptions
        {
            Configuration = new Configuration(
                new JpegConfigurationModule(),
                new PngConfigurationModule(),
                new BmpConfigurationModule(),
                new GifConfigurationModule()),
            MaxFrames = 1,
        };
    }

    private enum ContentKind
    {
        NotAnImage,
        Jpeg2000,
        Raster,
    }

    public IImageSource? TryDecode(ReadOnlySpan<byte> data)
    {
        ContentKind kind = Sniff(data);
        if (kind == ContentKind.NotAnImage)
        {
            Log.NotAnImage(_logger, data.Length);
            return null;
        }

        lock (_gate)
        {
            try
            {
                return kind == ContentKind.Jpeg2000 ? DecodeJpeg2000(data) : DecodeRaster(data);
            }
            catch (Exception ex)
            {
                Log.DecodeFailed(_logger, kind, data.Length, ex);
                return null;
            }
        }
    }

    private static ContentKind Sniff(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(Jp2Signature) || data.StartsWith(Jp2Magic) || data.StartsWith(J2kMagic))
            return ContentKind.Jpeg2000;
        if (data.StartsWith(JpegMagic) || data.StartsWith(PngMagic) || data.StartsWith("BM"u8)
            || data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8))
        {
            return ContentKind.Raster;
        }

        return ContentKind.NotAnImage;
    }

    private IImageSource? DecodeJpeg2000(ReadOnlySpan<byte> data)
    {
        if (!_codec.IsAvailable)
        {
            Log.CodecUnavailable(_logger, _codec.UnavailableReason ?? string.Empty);
            return null;
        }

        return _codec.Decode(data);
    }

    private ArrayImageSource? DecodeRaster(ReadOnlySpan<byte> data)
    {
        // ImageSharp reads streams; one copy of a received file (≤ 4 MiB by the DRM body cap) is acceptable.
        using var stream = new MemoryStream(data.ToArray(), writable: false);
        ImageInfo info = Image.Identify(_options, stream);
        if ((long)info.Width * info.Height > MaxDecodePixels)
        {
            Log.TooLarge(_logger, info.Width, info.Height, MaxDecodePixels);
            return null;
        }

        stream.Position = 0;

        // No AutoOrient: QSSTV shows a received JPEG as stored (QImage ignores EXIF orientation by default).
        using Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image = Image.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(_options, stream);
        return ImageSharpPixelConversion.ToImageSource(image);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Received file of {Length} bytes is not an image format we decode")]
        public static partial void NotAnImage(ILogger logger, int length);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Received {Kind} image of {Length} bytes could not be decoded; the raw file is kept")]
        public static partial void DecodeFailed(ILogger logger, ContentKind kind, int length, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Received JPEG 2000 image not decoded, the codec is unavailable: {Reason}")]
        public static partial void CodecUnavailable(ILogger logger, string reason);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Received image of {Width}x{Height} exceeds the {Limit} pixel limit and is not decoded")]
        public static partial void TooLarge(ILogger logger, int width, int height, long limit);
    }
}
