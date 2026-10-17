using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Imaging.OpenJpeg;

/// <summary><see cref="IJpeg2000Codec"/> over the bundled libopenjp2 2.5.4. The library is loaded on first
/// use from the application directory; a load failure is logged once and leaves the codec unavailable.
/// Each call creates and destroys its own OpenJPEG codec, stream and image, so calls are thread-safe.</summary>
public sealed unsafe partial class OpenJpegJpeg2000Codec : IJpeg2000Codec
{
    /// <summary>Decode refusal limits, checked on the header before any pixel decode.</summary>
    public const long MaxDecodePixels = 16_000_000;
    public const int MaxDecodeComponents = 4;
    public const long MaxDecodeTiles = 1024;

    private static readonly byte[] Jp2Signature = [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A];
    private static readonly byte[] Jp2Magic = [0x0D, 0x0A, 0x87, 0x0A];
    private static readonly byte[] J2kMagic = [0xFF, 0x4F, 0xFF, 0x51];

    private readonly ILogger<OpenJpegJpeg2000Codec> _logger;
    private readonly string _libraryPath;
    private readonly Lazy<OpenJpegNative?> _native;
    private string? _unavailableReason;

    public OpenJpegJpeg2000Codec(ILogger<OpenJpegJpeg2000Codec> logger)
        : this(logger, Path.Combine(AppContext.BaseDirectory, OpenJpegNative.PlatformFileName))
    {
    }

    internal OpenJpegJpeg2000Codec(ILogger<OpenJpegJpeg2000Codec> logger, string libraryPath)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(libraryPath);
        _logger = logger;
        _libraryPath = libraryPath;
        _native = new Lazy<OpenJpegNative?>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsAvailable => _native.Value is not null;

    public string? UnavailableReason => _native.Value is null ? _unavailableReason : null;

    /// <summary>The loaded library's version, or null.</summary>
    public string? LibraryVersion => _native.Value?.Version;

    public byte[] EncodeRgb24(ReadOnlySpan<byte> rgb24, int width, int height, int stride, int compressionRatio)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * 3);
        ArgumentOutOfRangeException.ThrowIfNegative(compressionRatio);
        if (rgb24.Length < ((long)stride * (height - 1)) + (width * 3))
            throw new ArgumentException("The pixel buffer is shorter than width, height and stride require.", nameof(rgb24));

        OpenJpegNative native = RequireNative();
        byte* parameters = (byte*)NativeMemory.AllocZeroed(OpenJpegNative.EncoderParametersBufferSize);
        try
        {
            // jp2IO::encode: defaults, then createImage, then the rate fields (jp2io.cpp:408-433).
            native.opj_set_default_encoder_parameters(parameters);
            using OpenJpegHandle image = CreateRgbImage(native, rgb24, width, height, stride);

            parameters[OpenJpegNative.TcpMctOffset] = 1; // numcomps >= 3
            *(float*)(parameters + OpenJpegNative.TcpRatesOffset) = compressionRatio;
            *(int*)(parameters + OpenJpegNative.TcpNumLayersOffset) += 1;
            *(int*)(parameters + OpenJpegNative.CpDistoAllocOffset) = 1;

            using var messages = new OpenJpegMessages();
            using var codec = new OpenJpegHandle(native.opj_create_compress(OpenJpegNative.CodecJp2), native.opj_destroy_codec);
            if (codec.IsInvalid)
                throw new InvalidOperationException("opj_create_compress failed.");
            messages.Attach(native, codec);
            nint c = codec.DangerousGetHandle();
            nint img = image.DangerousGetHandle();
            if (native.opj_setup_encoder(c, parameters, img) == 0)
                throw new InvalidOperationException(messages.Describe("opj_setup_encoder"));

            using OpenJpegMemoryStream stream = OpenJpegMemoryStream.ForWriting(native);
            nint s = stream.Handle.DangerousGetHandle();
            if (native.opj_start_compress(c, img, s) == 0)
                throw new InvalidOperationException(messages.Describe("opj_start_compress"));
            if (native.opj_encode(c, s) == 0)
                throw new InvalidOperationException(messages.Describe("opj_encode"));
            if (native.opj_end_compress(c, s) == 0)
                throw new InvalidOperationException(messages.Describe("opj_end_compress"));
            return stream.ToArray();
        }
        finally
        {
            NativeMemory.Free(parameters);
        }
    }

    public IImageSource Decode(ReadOnlySpan<byte> data)
    {
        int format = data.StartsWith(Jp2Signature) || data.StartsWith(Jp2Magic) ? OpenJpegNative.CodecJp2
            : data.StartsWith(J2kMagic) ? OpenJpegNative.CodecJ2k
            : throw new InvalidDataException("Not a JPEG 2000 file or codestream.");

        OpenJpegNative native = RequireNative();
        CheckSizLimits(data, format);
        byte* parameters = (byte*)NativeMemory.AllocZeroed(OpenJpegNative.DecoderParametersBufferSize);
        try
        {
            native.opj_set_default_decoder_parameters(parameters);
            using var messages = new OpenJpegMessages();
            using var codec = new OpenJpegHandle(native.opj_create_decompress(format), native.opj_destroy_codec);
            if (codec.IsInvalid)
                throw new InvalidOperationException("opj_create_decompress failed.");
            messages.Attach(native, codec);
            nint c = codec.DangerousGetHandle();
            if (native.opj_setup_decoder(c, parameters) == 0)
                throw new InvalidDataException(messages.Describe("opj_setup_decoder"));

            using OpenJpegMemoryStream stream = OpenJpegMemoryStream.ForReading(native, data);
            nint s = stream.Handle.DangerousGetHandle();
            nint imagePtr = 0;
            int headerOk = native.opj_read_header(s, c, &imagePtr);
            using var image = new OpenJpegHandle(imagePtr, native.opj_image_destroy);
            if (headerOk == 0 || image.IsInvalid)
                throw new InvalidDataException(messages.Describe("opj_read_header"));

            CheckHeaderLimits(native, c, (byte*)imagePtr);

            if (native.opj_decode(c, s, imagePtr) == 0 || native.opj_end_decompress(c, s) == 0)
                throw new InvalidDataException(messages.Describe("opj_decode"));
            return ToRgb24((byte*)imagePtr);
        }
        finally
        {
            NativeMemory.Free(parameters);
        }
    }

    private static OpenJpegHandle CreateRgbImage(OpenJpegNative native, ReadOnlySpan<byte> rgb24, int width, int height, int stride)
    {
        // jp2IO::createImage (jp2io.cpp:508-558): 3 components, 8-bit unsigned, subsampling 1, sRGB.
        var componentParameters = stackalloc OpenJpegNative.ImageComponentParameters[3];
        for (int i = 0; i < 3; i++)
        {
            componentParameters[i] = new OpenJpegNative.ImageComponentParameters
            {
                Dx = 1,
                Dy = 1,
                W = (uint)width,
                H = (uint)height,
                Prec = 8,
                Bpp = 8,
                Sgnd = 0,
            };
        }

        var image = new OpenJpegHandle(native.opj_image_create(3, componentParameters, OpenJpegNative.ColorSpaceSrgb), native.opj_image_destroy);
        if (image.IsInvalid)
            throw new InvalidOperationException("opj_image_create failed.");

        byte* img = (byte*)image.DangerousGetHandle();
        *(uint*)(img + OpenJpegNative.ImageX0Offset) = 0;
        *(uint*)(img + OpenJpegNative.ImageY0Offset) = 0;
        *(uint*)(img + OpenJpegNative.ImageX1Offset) = (uint)width;
        *(uint*)(img + OpenJpegNative.ImageY1Offset) = (uint)height;

        byte* comps = *(byte**)(img + OpenJpegNative.ImageCompsOffset);
        int* r = *(int**)(comps + OpenJpegNative.CompDataOffset);
        int* g = *(int**)(comps + OpenJpegNative.CompSize + OpenJpegNative.CompDataOffset);
        int* b = *(int**)(comps + (2 * OpenJpegNative.CompSize) + OpenJpegNative.CompDataOffset);
        int index = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = rgb24.Slice(y * stride, width * 3);
            for (int x = 0; x < width; x++, index++)
            {
                r[index] = row[x * 3];
                g[index] = row[(x * 3) + 1];
                b[index] = row[(x * 3) + 2];
            }
        }

        return image;
    }

    private static void CheckHeaderLimits(OpenJpegNative native, nint codec, byte* image)
    {
        long width = (long)*(uint*)(image + OpenJpegNative.ImageX1Offset) - *(uint*)(image + OpenJpegNative.ImageX0Offset);
        long height = (long)*(uint*)(image + OpenJpegNative.ImageY1Offset) - *(uint*)(image + OpenJpegNative.ImageY0Offset);
        uint components = *(uint*)(image + OpenJpegNative.ImageNumCompsOffset);
        if (width <= 0 || height <= 0 || width * height > MaxDecodePixels)
            throw new InvalidDataException($"JPEG 2000 image of {width}x{height} exceeds the {MaxDecodePixels} pixel limit.");
        if (components is 0 or > MaxDecodeComponents)
            throw new InvalidDataException($"JPEG 2000 image has {components} components; at most {MaxDecodeComponents} are accepted.");

        nint info = native.opj_get_cstr_info(codec);
        if (info == 0)
            throw new InvalidDataException("OpenJPEG returned no codestream information.");
        try
        {
            long tiles = (long)*(uint*)((byte*)info + OpenJpegNative.CstrInfoTwOffset) * *(uint*)((byte*)info + OpenJpegNative.CstrInfoThOffset);
            if (tiles > MaxDecodeTiles)
                throw new InvalidDataException($"JPEG 2000 image has {tiles} tiles; at most {MaxDecodeTiles} are accepted.");
        }
        finally
        {
            native.opj_destroy_cstr_info(&info);
        }
    }

    /// <summary>RGB from components 0-2, or grey replicated from component 0 for one or two components.
    /// The colour-space rule and the sYCC/e-YCC conversion follow opj_decompress 2.5.4 (:1620-1634), the
    /// same as QSSTV (jp2io.cpp:265-278). Converted samples are unsigned at component 0's precision; others
    /// are re-centred if signed. Both are scaled to 8 bits and clamped.</summary>
    private static ArrayRgb24Image ToRgb24(byte* image)
    {
        uint components = *(uint*)(image + OpenJpegNative.ImageNumCompsOffset);
        int colorSpace = *(int*)(image + OpenJpegNative.ImageColorSpaceOffset);
        byte* comps = *(byte**)(image + OpenJpegNative.ImageCompsOffset);
        Component c0 = Component.Read(comps, 0);

        if (colorSpace != OpenJpegNative.ColorSpaceSycc && components == 3 && c0.Dx == c0.Dy
            && Component.Read(comps, 1).Dx != 1)
        {
            colorSpace = OpenJpegNative.ColorSpaceSycc;
        }
        else if (components <= 2)
        {
            colorSpace = OpenJpegNative.ColorSpaceGray;
        }

        if (colorSpace == OpenJpegNative.ColorSpaceSycc)
            return SyccToRgb24(image, c0, Component.Read(comps, 1), Component.Read(comps, 2));
        if (colorSpace == OpenJpegNative.ColorSpaceEycc)
            return EsyccToRgb24(c0, Component.Read(comps, 1), Component.Read(comps, 2));

        int used = components >= 3 ? 3 : 1;
        for (int i = 0; i < used; i++)
        {
            Component c = Component.Read(comps, i);
            if (c.Dx != 1 || c.Dy != 1 || c.W != c0.W || c.H != c0.H || c.Data == null)
                throw new InvalidDataException("Subsampled JPEG 2000 components are not supported.");
        }

        int width = (int)c0.W;
        int height = (int)c0.H;
        var pixels = new Rgb24[(long)width * height];
        Component r = c0;
        Component g = used == 3 ? Component.Read(comps, 1) : c0;
        Component b = used == 3 ? Component.Read(comps, 2) : c0;
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Rgb24(Sample(r, i), Sample(g, i), Sample(b, i));
        return new ArrayRgb24Image(width, height, pixels);
    }

    private static ArrayRgb24Image SyccToRgb24(byte* image, Component y, Component cb, Component cr)
    {
        OpenJpegColor.SyccLayout layout = OpenJpegColor.LayoutOf(y.Dx, y.Dy, cb.Dx, cb.Dy, cr.Dx, cr.Dy);
        if (layout == OpenJpegColor.SyccLayout.CannotConvert)
            throw new InvalidDataException("This sYCC JPEG 2000 subsampling cannot be converted to RGB.");

        uint x0 = *(uint*)(image + OpenJpegNative.ImageX0Offset);
        uint y0 = *(uint*)(image + OpenJpegNative.ImageY0Offset);
        uint x1 = *(uint*)(image + OpenJpegNative.ImageX1Offset);
        uint y1 = *(uint*)(image + OpenJpegNative.ImageY1Offset);
        uint chromaW = layout == OpenJpegColor.SyccLayout.Yuv444 ? y.W : CeilDiv2(x1) - CeilDiv2(x0);
        uint chromaH = layout == OpenJpegColor.SyccLayout.Yuv420 ? CeilDiv2(y1) - CeilDiv2(y0) : y.H;
        if (y.W != x1 - x0 || y.H != y1 - y0 || cb.W != chromaW || cb.H != chromaH || cr.W != chromaW || cr.H != chromaH
            || y.Data == null || cb.Data == null || cr.Data == null)
        {
            throw new InvalidDataException("sYCC JPEG 2000 chroma dimensions do not match the image.");
        }

        int width = (int)y.W;
        int height = (int)y.H;
        var sink = new ScaledRgbSink(new Rgb24[(long)width * height], (int)y.Prec);
        try
        {
            switch (layout)
            {
                case OpenJpegColor.SyccLayout.Yuv420:
                    OpenJpegColor.Sycc420((int)y.Prec, y.Span, cb.Span, cr.Span, width, height, (int)cb.W, x0, y0, ref sink);
                    break;
                case OpenJpegColor.SyccLayout.Yuv422:
                    OpenJpegColor.Sycc422((int)y.Prec, y.Span, cb.Span, cr.Span, width, height, (int)cb.W, x0, ref sink);
                    break;
                default:
                    OpenJpegColor.Sycc444((int)y.Prec, y.Span, cb.Span, cr.Span, width, height, ref sink);
                    break;
            }
        }
        catch (IndexOutOfRangeException ex)
        {
            throw new InvalidDataException("sYCC JPEG 2000 chroma is shorter than the image needs.", ex);
        }

        return new ArrayRgb24Image(width, height, sink.Pixels);
    }

    private static ArrayRgb24Image EsyccToRgb24(Component y, Component cb, Component cr)
    {
        if (!OpenJpegColor.CanConvertEsycc(y.Dx, y.Dy, cb.Dx, cb.Dy, cr.Dx, cr.Dy))
            throw new InvalidDataException("This e-YCC JPEG 2000 subsampling cannot be converted to RGB.");

        // color.c converts subsampled e-YCC at the reduced size; this decoder only renders full-size components.
        if (y.Dx != 1 || y.Dy != 1 || cb.W != y.W || cb.H != y.H || cr.W != y.W || cr.H != y.H
            || y.Data == null || cb.Data == null || cr.Data == null)
        {
            throw new InvalidDataException("Subsampled JPEG 2000 components are not supported.");
        }

        int width = (int)y.W;
        int height = (int)y.H;
        var sink = new ScaledRgbSink(new Rgb24[(long)width * height], (int)y.Prec);
        OpenJpegColor.Esycc((int)y.Prec, y.Span, cb.Span, cr.Span, cb.Sgnd, cr.Sgnd, width * height, ref sink);
        return new ArrayRgb24Image(width, height, sink.Pixels);
    }

    private static uint CeilDiv2(uint value) => (uint)(((ulong)value + 1) / 2);

    private static byte Sample(Component comp, int index)
    {
        int prec = (int)comp.Prec;
        int value = comp.Data[index];
        if (comp.Sgnd && prec > 0)
            value += 1 << (prec - 1);
        return ScaleTo8(value, prec);
    }

    private static byte ScaleTo8(int value, int prec)
    {
        if (prec > 8)
            value >>= prec - 8;
        else if (prec is > 0 and < 8)
            value <<= 8 - prec;
        return (byte)Math.Clamp(value, 0, 255);
    }

    private readonly struct Component
    {
        public uint Dx { get; init; }

        public uint Dy { get; init; }

        public uint W { get; init; }

        public uint H { get; init; }

        public uint Prec { get; init; }

        public bool Sgnd { get; init; }

        public int* Data { get; init; }

        public ReadOnlySpan<int> Span => new(Data, checked((int)((long)W * H)));

        public static Component Read(byte* comps, int index)
        {
            byte* comp = comps + (index * OpenJpegNative.CompSize);
            return new Component
            {
                Dx = *(uint*)(comp + OpenJpegNative.CompDxOffset),
                Dy = *(uint*)(comp + OpenJpegNative.CompDyOffset),
                W = *(uint*)(comp + OpenJpegNative.CompWOffset),
                H = *(uint*)(comp + OpenJpegNative.CompHOffset),
                Prec = *(uint*)(comp + OpenJpegNative.CompPrecOffset),
                Sgnd = *(uint*)(comp + OpenJpegNative.CompSgndOffset) != 0,
                Data = *(int**)(comp + OpenJpegNative.CompDataOffset),
            };
        }
    }

    /// <summary>Maps converted values (unsigned at component 0's precision) to 8 bits.</summary>
    private struct ScaledRgbSink(Rgb24[] pixels, int prec) : IRgbSink
    {
        public Rgb24[] Pixels { get; } = pixels;

        public void Set(int index, int r, int g, int b) =>
            Pixels[index] = new Rgb24(ScaleTo8(r, prec), ScaleTo8(g, prec), ScaleTo8(b, prec));
    }

    private OpenJpegNative RequireNative() =>
        _native.Value ?? throw new InvalidOperationException(_unavailableReason ?? "OpenJPEG is unavailable.");

    private OpenJpegNative? Load()
    {
        OpenJpegNative? native = OpenJpegNative.TryLoad(_libraryPath, out string? reason);
        if (native is null)
        {
            _unavailableReason = reason;
            Log.LoadFailed(_logger, _libraryPath, reason ?? string.Empty);
            return null;
        }

        Log.Loaded(_logger, native.Version, _libraryPath);
        return native;
    }

    private sealed class ArrayRgb24Image(int width, int height, Rgb24[] pixels) : IImageSource
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public ReadOnlySpan<Rgb24> GetScanline(int y) => pixels.AsSpan(y * Width, Width);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "OpenJPEG {Version} loaded from '{Path}'")]
        public static partial void Loaded(ILogger logger, string version, string path);

        [LoggerMessage(Level = LogLevel.Warning, Message = "OpenJPEG could not be loaded from '{Path}'; JPEG 2000 (DRM image) encode and decode are unavailable: {Reason}")]
        public static partial void LoadFailed(ILogger logger, string path, string reason);
    }
}
