using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Core.Drm.Tx;

namespace ScanlineStudio.Imaging.OpenJpeg.Tests;

public sealed class OpenJpegJpeg2000CodecTests
{
    private static OpenJpegJpeg2000Codec NewCodec() => new(NullLogger<OpenJpegJpeg2000Codec>.Instance);

    [Fact]
    public void NativeLibrary_LoadsTheBundledVersion()
    {
        var codec = NewCodec();

        Assert.True(codec.IsAvailable, codec.UnavailableReason);
        Assert.Null(codec.UnavailableReason);
        Assert.Equal(OpenJpegNative.RequiredVersion, codec.LibraryVersion);
    }

    [Fact]
    public void MissingLibrary_ReportsUnavailable_AndOnlyThrowsWhenUsed()
    {
        var codec = new OpenJpegJpeg2000Codec(NullLogger<OpenJpegJpeg2000Codec>.Instance, Path.Combine(Path.GetTempPath(), "no-such-dir", "libopenjp2.so.7"));

        Assert.False(codec.IsAvailable);
        Assert.Contains("not found", codec.UnavailableReason, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => codec.EncodeRgb24(new byte[12], 2, 2, 6, 0));
        Assert.Throws<InvalidOperationException>(() => codec.Decode(EncodeCard(NewCodec(), 8, 8, 0)));
    }

    [Fact]
    public void Lossless_RoundTripsEveryPixel()
    {
        var codec = NewCodec();
        const int w = 67, h = 41, stride = (w * 3) + 5;
        byte[] rgb = new byte[stride * h];
        var rng = new Random(7);
        rng.NextBytes(rgb);

        byte[] jp2 = codec.EncodeRgb24(rgb, w, h, stride, compressionRatio: 0);
        IImageSource decoded = codec.Decode(jp2);

        Assert.Equal(w, decoded.Width);
        Assert.Equal(h, decoded.Height);
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<Rgb24> row = decoded.GetScanline(y);
            for (int x = 0; x < w; x++)
            {
                int o = (y * stride) + (x * 3);
                Assert.Equal(new Rgb24(rgb[o], rgb[o + 1], rgb[o + 2]), row[x]);
            }
        }
    }

    [Fact]
    public void Lossy_StaysNearTargetSize_WithBoundedError()
    {
        var codec = NewCodec();
        const int w = 64, h = 48;
        int ratio = DrmJp2Ratio.FromTargetSize(w, h, 400);
        byte[] rgb = Card(w, h);

        byte[] jp2 = codec.EncodeRgb24(rgb, w, h, w * 3, ratio);
        IImageSource decoded = codec.Decode(jp2);

        Assert.Equal(23, ratio);
        Assert.InRange(jp2.Length, 300, 500);

        // 400 bytes for a 64×48 checkerboard is coarse; a lighter ratio must do clearly better.
        double meanAbs = MeanAbsError(rgb, w, decoded);
        double lighter = MeanAbsError(rgb, w, codec.Decode(codec.EncodeRgb24(rgb, w, h, w * 3, 4)));
        Assert.InRange(meanAbs, 1.0, 40.0);
        Assert.InRange(lighter, 0.0, meanAbs / 2);
    }

    [Fact]
    public void QsstvParameters_OnTheHarnessCard_DecodeToTheHarnessPixels()
    {
        var codec = NewCodec();
        byte[] harness = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "card.jp2"));

        byte[] ours = EncodeCard(codec, 64, 48, DrmJp2Ratio.FromTargetSize(64, 48, 400));

        // Gate: decoded pixels equal. Bytes are compared only for the Linux binary the harness was checked against.
        IImageSource a = codec.Decode(ours);
        IImageSource b = codec.Decode(harness);
        if (OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64)
            Assert.Equal(harness, ours);
        Assert.Equal(b.Width, a.Width);
        Assert.Equal(b.Height, a.Height);
        for (int y = 0; y < a.Height; y++)
            Assert.True(a.GetScanline(y).SequenceEqual(b.GetScanline(y)), $"row {y}");
    }

    [Fact]
    public void Header_OverPixelLimit_IsRefusedBeforeDecode()
    {
        byte[] j2k = Codestream(NewCodec());
        WriteSiz(j2k, xsiz: 5000, ysiz: 5000, tileW: 5000, tileH: 5000);

        var ex = Assert.Throws<InvalidDataException>(() => NewCodec().Decode(j2k));
        Assert.Contains("pixel limit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_OverTileLimit_IsRefusedBeforeDecode()
    {
        byte[] j2k = Codestream(NewCodec());
        WriteSiz(j2k, xsiz: 2048, ysiz: 2048, tileW: 32, tileH: 32);

        var ex = Assert.Throws<InvalidDataException>(() => NewCodec().Decode(j2k));
        Assert.Contains("4096 tiles", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_OverComponentLimit_IsRefusedBeforeDecode()
    {
        byte[] j2k = Codestream(NewCodec());

        // Csiz 3 → 5: two more 8-bit unsigned, unsubsampled component entries; COD/QCD cover all components.
        const int cSizOffset = 40;
        int lsiz = BinaryPrimitives.ReadUInt16BigEndian(j2k.AsSpan(4));
        var patched = new List<byte>(j2k[..(cSizOffset + 2 + 9)]);
        patched.AddRange([0x07, 0x01, 0x01, 0x07, 0x01, 0x01]);
        patched.AddRange(j2k[(cSizOffset + 2 + 9)..]);
        byte[] five = [.. patched];
        BinaryPrimitives.WriteUInt16BigEndian(five.AsSpan(4), (ushort)(lsiz + 6));
        BinaryPrimitives.WriteUInt16BigEndian(five.AsSpan(cSizOffset), 5);

        var ex = Assert.Throws<InvalidDataException>(() => NewCodec().Decode(five));
        Assert.Contains("5 components", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_NotJpeg2000_IsInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => NewCodec().Decode("\x89PNG\r\n\x1a\n...."u8));
    }

    [Fact]
    public void Siz_SixteenMillionTiles_IsRefusedWithoutOpenJpegAllocatingThem()
    {
        // 4000×4000 is inside the pixel limit; 1×1 tiles would make opj_read_header allocate 16M tile records.
        byte[] j2k = Codestream(NewCodec());
        WriteSiz(j2k, xsiz: 4000, ysiz: 4000, tileW: 1, tileH: 1);

        var ex = Assert.Throws<InvalidDataException>(() => NewCodec().Decode(j2k));
        Assert.Contains("16000000 tiles", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Siz_InsideJp2Box_IsCheckedToo()
    {
        byte[] jp2 = EncodeCard(NewCodec(), 32, 32, 0);
        int cs = jp2.AsSpan().IndexOf("jp2c"u8) + 4;
        BinaryPrimitives.WriteUInt32BigEndian(jp2.AsSpan(cs + 8), 5000);
        BinaryPrimitives.WriteUInt32BigEndian(jp2.AsSpan(cs + 12), 5000);

        var ex = Assert.Throws<InvalidDataException>(() => NewCodec().Decode(jp2));
        Assert.Contains("pixel limit", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("zero-tile")]
    [InlineData("zero-subsampling")]
    [InlineData("bad-lsiz")]
    [InlineData("truncated")]
    [InlineData("short-segment")]
    public void Siz_Malformed_IsInvalidData(string defect)
    {
        byte[] j2k = Codestream(NewCodec());
        switch (defect)
        {
            case "zero-tile":
                WriteSiz(j2k, xsiz: 32, ysiz: 32, tileW: 0, tileH: 32);
                break;
            case "zero-subsampling":
                j2k[42 + 1] = 0;
                break;
            case "bad-lsiz":
                BinaryPrimitives.WriteUInt16BigEndian(j2k.AsSpan(4), 40);
                break;
            case "short-segment":
                // The SIZ segment ends at 4 + Lsiz; cut one byte inside the last component entry.
                j2k = j2k[..(4 + BinaryPrimitives.ReadUInt16BigEndian(j2k.AsSpan(4)) - 1)];
                break;
            default:
                j2k = j2k[..30];
                break;
        }

        var ex = Assert.Throws<InvalidDataException>(() => NewCodec().Decode(j2k));
        Assert.DoesNotContain("opj_", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A palette box with 255 channels would make opj_jp2_apply_pclr allocate 255 × w × h samples inside
    /// opj_decode; the pre-parse refuses it first (its own message, not OpenJPEG's).</summary>
    [Fact]
    public void Jp2_PaletteWithTooManyChannels_IsRefusedBeforeOpenJpeg()
    {
        byte[] jp2 = EncodeCard(NewCodec(), 32, 32, 0);
        int jp2h = jp2.AsSpan().IndexOf("jp2h"u8) - 4;
        uint jp2hLength = BinaryPrimitives.ReadUInt32BigEndian(jp2.AsSpan(jp2h));
        byte[] pclr = [0, 0, 0, 11, (byte)'p', (byte)'c', (byte)'l', (byte)'r', 0, 1, 255];
        var patched = new List<byte>(jp2[..(jp2h + (int)jp2hLength)]);
        patched.AddRange(pclr);
        patched.AddRange(jp2[(jp2h + (int)jp2hLength)..]);
        byte[] bomb = [.. patched];
        BinaryPrimitives.WriteUInt32BigEndian(bomb.AsSpan(jp2h), jp2hLength + (uint)pclr.Length);

        var ex = Assert.Throws<InvalidDataException>(() => NewCodec().Decode(bomb));
        Assert.Contains("palette has 255 channels", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>OpenJPEG also reads a pclr box at the top level, between jp2h and jp2c (jp2.c:2314-2325).</summary>
    [Fact]
    public void Jp2_TopLevelPaletteWithTooManyChannels_IsRefusedBeforeOpenJpeg()
    {
        byte[] jp2 = EncodeCard(NewCodec(), 32, 32, 0);
        int jp2c = jp2.AsSpan().IndexOf("jp2c"u8) - 4;
        byte[] pclr = [0, 0, 0, 11, (byte)'p', (byte)'c', (byte)'l', (byte)'r', 0, 1, 255];
        byte[] bomb = [.. jp2[..jp2c], .. pclr, .. jp2[jp2c..]];

        var ex = Assert.Throws<InvalidDataException>(() => NewCodec().Decode(bomb));
        Assert.Contains("palette has 255 channels", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>OpenJPEG never checks the jp2c length and decodes to EOF, so a too-long jp2c box still decodes.</summary>
    [Fact]
    public void Jp2_CodestreamLengthPastTheEnd_StillDecodes()
    {
        byte[] jp2 = EncodeCard(NewCodec(), 32, 32, 0);
        int jp2c = jp2.AsSpan().IndexOf("jp2c"u8) - 4;
        BinaryPrimitives.WriteUInt32BigEndian(jp2.AsSpan(jp2c), (uint)(jp2.Length - jp2c + 1000));

        IImageSource image = NewCodec().Decode(jp2);
        Assert.Equal((32, 32), (image.Width, image.Height));
    }

    [Fact]
    public void Jp2_WithoutCodestreamBox_IsInvalidData()
    {
        byte[] jp2 = EncodeCard(NewCodec(), 32, 32, 0);
        int at = jp2.AsSpan().IndexOf("jp2c"u8);
        "xxxx"u8.CopyTo(jp2.AsSpan(at));

        var ex = Assert.Throws<InvalidDataException>(() => NewCodec().Decode(jp2));
        Assert.Contains("codestream box", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The harness's synthetic RGB card (qsstv-drm-harness main.cpp runJp2Enc).</summary>
    private static byte[] Card(int w, int h)
    {
        byte[] rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = ((y * w) + x) * 3;
                rgb[o] = (byte)((x * 255) / (w - 1));
                rgb[o + 1] = (byte)((y * 255) / (h - 1));
                rgb[o + 2] = (byte)((((x / 8) + (y / 8)) & 1) != 0 ? 220 : 40);
            }
        }

        return rgb;
    }

    private static byte[] EncodeCard(OpenJpegJpeg2000Codec codec, int w, int h, int ratio) =>
        codec.EncodeRgb24(Card(w, h), w, h, w * 3, ratio);

    private static double MeanAbsError(byte[] rgb, int w, IImageSource decoded)
    {
        long sum = 0;
        for (int y = 0; y < decoded.Height; y++)
        {
            ReadOnlySpan<Rgb24> row = decoded.GetScanline(y);
            for (int x = 0; x < decoded.Width; x++)
            {
                int o = ((y * w) + x) * 3;
                sum += Math.Abs(rgb[o] - row[x].R) + Math.Abs(rgb[o + 1] - row[x].G) + Math.Abs(rgb[o + 2] - row[x].B);
            }
        }

        return sum / (double)(decoded.Width * decoded.Height * 3);
    }

    /// <summary>The raw J2K codestream inside our JP2 of a 32×32 card (the "jp2c" box contents).</summary>
    private static byte[] Codestream(OpenJpegJpeg2000Codec codec)
    {
        byte[] jp2 = EncodeCard(codec, 32, 32, 0);
        int at = jp2.AsSpan().IndexOf("jp2c"u8);
        Assert.True(at > 0);
        byte[] j2k = jp2[(at + 4)..];
        Assert.Equal([0xFF, 0x4F, 0xFF, 0x51], j2k[..4]);
        return j2k;
    }

    private static void WriteSiz(byte[] j2k, uint xsiz, uint ysiz, uint tileW, uint tileH)
    {
        BinaryPrimitives.WriteUInt32BigEndian(j2k.AsSpan(8), xsiz);
        BinaryPrimitives.WriteUInt32BigEndian(j2k.AsSpan(12), ysiz);
        BinaryPrimitives.WriteUInt32BigEndian(j2k.AsSpan(24), tileW);
        BinaryPrimitives.WriteUInt32BigEndian(j2k.AsSpan(28), tileH);
    }
}
