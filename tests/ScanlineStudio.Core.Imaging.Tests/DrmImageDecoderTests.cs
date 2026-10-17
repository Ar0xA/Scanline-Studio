using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace ScanlineStudio.Core.Imaging.Tests;

public sealed class DrmImageDecoderTests
{
    private static DrmImageDecoder NewDecoder(FakeJpeg2000Codec codec) => new(codec, NullLogger<DrmImageDecoder>.Instance);

    [Theory]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A, 0 }, true)]
    [InlineData(new byte[] { 0x0D, 0x0A, 0x87, 0x0A, 0 }, true)]
    [InlineData(new byte[] { 0xFF, 0x4F, 0xFF, 0x51, 0 }, true)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, false)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, false)]
    [InlineData(new byte[] { (byte)'B', (byte)'M', 0, 0 }, false)]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, false)]
    [InlineData(new byte[] { (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' }, false)]
    [InlineData(new byte[] { }, false)]
    public void OnlyJpeg2000MagicBytes_ReachTheCodec(byte[] data, bool toCodec)
    {
        var codec = new FakeJpeg2000Codec { Result = new ArrayImageSource(1, 1, [default]) };

        NewDecoder(codec).TryDecode(data);

        Assert.Equal(toCodec ? 1 : 0, codec.DecodeCalls);
    }

    [Fact]
    public void NotAnImage_ReturnsNull_WithoutCallingTheCodec()
    {
        var codec = new FakeJpeg2000Codec();

        Assert.Null(NewDecoder(codec).TryDecode("plain text file"u8));
        Assert.Equal(0, codec.DecodeCalls);
    }

    [Fact]
    public void Jpeg2000_GoesToTheCodec()
    {
        var expected = new ArrayImageSource(1, 1, [new ScanlineStudio.Abstractions.Imaging.Rgb24(1, 2, 3)]);
        var codec = new FakeJpeg2000Codec { Result = expected };

        Assert.Same(expected, NewDecoder(codec).TryDecode([0xFF, 0x4F, 0xFF, 0x51, 0]));
        Assert.Equal(1, codec.DecodeCalls);
    }

    [Fact]
    public void Jpeg2000_CodecUnavailable_ReturnsNull()
    {
        var codec = new FakeJpeg2000Codec { IsAvailable = false };

        Assert.Null(NewDecoder(codec).TryDecode([0xFF, 0x4F, 0xFF, 0x51, 0]));
        Assert.Equal(0, codec.DecodeCalls);
    }

    [Fact]
    public void Jpeg2000_CodecRefuses_ReturnsNull()
    {
        var codec = new FakeJpeg2000Codec { Throw = new InvalidDataException("corrupt") };

        Assert.Null(NewDecoder(codec).TryDecode([0xFF, 0x4F, 0xFF, 0x51, 0]));
    }

    [Fact]
    public void Png_DecodesEveryPixel()
    {
        using var source = new Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(5, 3);
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 5; x++)
                source[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24((byte)(x * 50), (byte)(y * 80), 7);
        }

        IImageSource? image = NewDecoder(new FakeJpeg2000Codec()).TryDecode(Encode(source, new PngEncoder()));

        Assert.NotNull(image);
        Assert.Equal(5, image.Width);
        Assert.Equal(3, image.Height);
        Assert.Equal(new ScanlineStudio.Abstractions.Imaging.Rgb24(200, 160, 7), image.GetScanline(2)[4]);
    }

    [Fact]
    public void Jpeg_And_Bmp_Decode()
    {
        using var source = new Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(16, 8, new SixLabors.ImageSharp.PixelFormats.Rgb24(10, 200, 30));
        var decoder = NewDecoder(new FakeJpeg2000Codec());

        IImageSource? jpeg = decoder.TryDecode(Encode(source, new JpegEncoder { Quality = 95 }));
        IImageSource? bmp = decoder.TryDecode(Encode(source, new BmpEncoder()));

        Assert.NotNull(jpeg);
        Assert.Equal((16, 8), (jpeg.Width, jpeg.Height));
        Assert.InRange(jpeg.GetScanline(4)[8].G, 190, 210);
        Assert.NotNull(bmp);
        Assert.Equal(new ScanlineStudio.Abstractions.Imaging.Rgb24(10, 200, 30), bmp.GetScanline(7)[15]);
    }

    [Fact]
    public void MultiFrameGif_DecodesOnlyTheFirstFrame()
    {
        using var gif = new Image<Rgba32>(4, 4, new Rgba32(255, 0, 0));
        using (var second = new Image<Rgba32>(4, 4, new Rgba32(0, 0, 255)))
            gif.Frames.AddFrame(second.Frames.RootFrame);
        Assert.Equal(2, gif.Frames.Count);

        IImageSource? image = NewDecoder(new FakeJpeg2000Codec()).TryDecode(Encode(gif, new GifEncoder()));

        Assert.NotNull(image);
        Assert.Equal(new ScanlineStudio.Abstractions.Imaging.Rgb24(255, 0, 0), image.GetScanline(0)[0]);
    }

    [Fact]
    public void OverSixteenMegapixels_IsRefusedFromTheHeaderAlone()
    {
        // A 5000×5000 24-bit BMP header with no pixel data: Identify reads only the header.
        byte[] bmp = new byte[54];
        "BM"u8.CopyTo(bmp);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), 54 + (5000 * 5000 * 3));
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 5000);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), 5000);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 24);

        var logger = new CapturingLogger();
        Assert.Null(new DrmImageDecoder(new FakeJpeg2000Codec(), logger).TryDecode(bmp));
        Assert.Contains(logger.Messages, m => m.Contains("exceeds the 16000000 pixel limit", StringComparison.Ordinal));
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<DrmImageDecoder>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public void CorruptRaster_ReturnsNull_NeverThrows()
    {
        var decoder = NewDecoder(new FakeJpeg2000Codec());

        Assert.Null(decoder.TryDecode([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]));
        Assert.Null(decoder.TryDecode([0xFF, 0xD8, 0xFF, 0x00]));
        Assert.Null(decoder.TryDecode("GIF89a"u8));
    }

    [Fact]
    public async Task Decodes_RunOneAtATime()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var codec = new FakeJpeg2000Codec
        {
            Result = new ArrayImageSource(1, 1, [default]),
            OnDecode = () =>
            {
                entered.Set();
                release.Wait();
            },
        };
        var decoder = NewDecoder(codec);
        byte[] j2k = [0xFF, 0x4F, 0xFF, 0x51, 0];

        var first = Task.Run(() => decoder.TryDecode(j2k));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var second = Task.Run(() => decoder.TryDecode(j2k));
        Assert.NotSame(second, await Task.WhenAny(second, Task.Delay(200)));
        Assert.Equal(1, codec.DecodeCalls);
        release.Set();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, codec.DecodeCalls);
        Assert.Equal(1, codec.MaxConcurrent);
    }

    private static byte[] Encode(Image image, SixLabors.ImageSharp.Formats.IImageEncoder encoder)
    {
        using var ms = new MemoryStream();
        image.Save(ms, encoder);
        return ms.ToArray();
    }

    private sealed class FakeJpeg2000Codec : IJpeg2000Codec
    {
        private int _inside;
        private int _calls;
        private int _max;

        public bool IsAvailable { get; init; } = true;

        public string? UnavailableReason => IsAvailable ? null : "fake unavailable";

        public IImageSource? Result { get; init; }

        public Exception? Throw { get; init; }

        public Action? OnDecode { get; init; }

        public int DecodeCalls => Volatile.Read(ref _calls);

        public int MaxConcurrent => Volatile.Read(ref _max);

        public byte[] EncodeRgb24(ReadOnlySpan<byte> rgb24, int width, int height, int stride, int compressionRatio) =>
            throw new NotSupportedException();

        public IImageSource Decode(ReadOnlySpan<byte> data)
        {
            int now = Interlocked.Increment(ref _inside);
            Interlocked.Increment(ref _calls);
            int seen;
            while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen)
            {
            }

            try
            {
                OnDecode?.Invoke();
                if (Throw is not null)
                    throw Throw;
                return Result ?? throw new InvalidDataException("no result");
            }
            finally
            {
                Interlocked.Decrement(ref _inside);
            }
        }
    }
}
