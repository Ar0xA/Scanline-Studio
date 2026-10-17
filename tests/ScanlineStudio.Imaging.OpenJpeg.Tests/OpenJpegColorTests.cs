using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Imaging.OpenJpeg.Tests;

/// <summary>The color.c port against reference output of OpenJPEG 2.5.4's own color.c (Fixtures/Ycc/README.md).
/// Tolerance: none, every sample must match exactly.</summary>
public sealed class OpenJpegColorTests
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ycc");

    public static TheoryData<string> ConverterCases()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.GetFiles(FixtureDir, "*.ycc").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileNameWithoutExtension(path));
        return data;
    }

    public static TheoryData<string> DecodeCases()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.GetFiles(FixtureDir, "*.rgb").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileNameWithoutExtension(path));
        return data;
    }

    [Fact]
    public void Fixtures_CoverEveryLayoutAndBothSignsOfChroma()
    {
        string[] names = [.. ConverterCases().Select(row => (string)row[0])];
        Assert.Equal(20, names.Length);
        Assert.Contains(names, n => n.StartsWith("sycc444", StringComparison.Ordinal));
        Assert.Contains(names, n => n.StartsWith("sycc422", StringComparison.Ordinal) && n.EndsWith("_x1", StringComparison.Ordinal));
        Assert.Contains(names, n => n.StartsWith("sycc420", StringComparison.Ordinal) && n.EndsWith("_x1y1", StringComparison.Ordinal));
        Assert.Contains("esycc_p8_signed_9x7", names);
        Assert.Contains("esycc_p12_signed_9x7", names);
        Assert.Contains("sycc444_p12_9x7", names);
        Assert.Equal(13, DecodeCases().Count());
    }

    [Theory]
    [MemberData(nameof(ConverterCases))]
    public void Converter_MatchesColorC(string name)
    {
        YccCase c = YccCase.Load(Path.Combine(FixtureDir, name + ".ycc"));
        Plane y = c.Planes[0], cb = c.Planes[1], cr = c.Planes[2];

        bool convertible = c.Kind == 0
            ? OpenJpegColor.LayoutOf(y.Dx, y.Dy, cb.Dx, cb.Dy, cr.Dx, cr.Dy) != OpenJpegColor.SyccLayout.CannotConvert
            : OpenJpegColor.CanConvertEsycc(y.Dx, y.Dy, cb.Dx, cb.Dy, cr.Dx, cr.Dy);
        Assert.Equal(c.Converted, convertible);
        if (!c.Converted)
            return;

        var sink = new IntSink(c.OutW * c.OutH);
        if (c.Kind == 1)
        {
            OpenJpegColor.Esycc(y.Prec, y.Data, cb.Data, cr.Data, cb.Sgnd, cr.Sgnd, y.W * y.H, ref sink);
        }
        else
        {
            switch (OpenJpegColor.LayoutOf(y.Dx, y.Dy, cb.Dx, cb.Dy, cr.Dx, cr.Dy))
            {
                case OpenJpegColor.SyccLayout.Yuv420:
                    OpenJpegColor.Sycc420(y.Prec, y.Data, cb.Data, cr.Data, y.W, y.H, cb.W, c.X0, c.Y0, ref sink);
                    break;
                case OpenJpegColor.SyccLayout.Yuv422:
                    OpenJpegColor.Sycc422(y.Prec, y.Data, cb.Data, cr.Data, y.W, y.H, cb.W, c.X0, ref sink);
                    break;
                default:
                    OpenJpegColor.Sycc444(y.Prec, y.Data, cb.Data, cr.Data, y.W, y.H, ref sink);
                    break;
            }
        }

        Assert.Equal(c.Out[0], sink.R);
        Assert.Equal(c.Out[1], sink.G);
        Assert.Equal(c.Out[2], sink.B);
        Assert.All(sink.Written, written => Assert.True(written));
    }

    [Theory]
    [InlineData("wide_sycc444_p8_all")]
    [InlineData("wide_sycc444_p12")]
    [InlineData("wide_esycc_p8_unsigned_all")]
    [InlineData("wide_esycc_p8_signed_all")]
    [InlineData("wide_esycc_p12_signed")]
    public void Converter_OverTheWholeValueRange_MatchesColorC(string name)
    {
        // The 8-bit cases enumerate every (Cb, Cr) pair, so a double/float width change shows up.
        using var gz = new System.IO.Compression.GZipStream(File.OpenRead(Path.Combine(FixtureDir, name + ".wide.gz")), System.IO.Compression.CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        byte[] b = ms.ToArray();
        Assert.Equal("YCW1"u8.ToArray(), b[..4]);
        int kind = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4));
        int prec = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8));
        bool sgnd = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(12)) != 0;
        int formula = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(16));
        int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(20));
        Assert.Equal(24 + (n * 6), b.Length);

        int[] y = new int[n], cb = new int[n], cr = new int[n];
        int m = 1 << prec;
        for (int i = 0; i < n; i++)
        {
            if (formula == 0)
            {
                (y[i], cb[i], cr[i]) = ((i * 37) & 255, i & 255, (i >> 8) & 255);
            }
            else
            {
                (y[i], cb[i], cr[i]) = ((int)((long)i * 7919 % m), (int)((long)i * 104729 % m), (int)((long)i * 1299709 % m));
            }

            if (sgnd)
            {
                cb[i] -= m / 2;
                cr[i] -= m / 2;
            }
        }

        var sink = new IntSink(n);
        if (kind == 1)
            OpenJpegColor.Esycc(prec, y, cb, cr, sgnd, sgnd, n, ref sink);
        else
            OpenJpegColor.Sycc444(prec, y, cb, cr, 256, n / 256, ref sink);

        int[][] got = [sink.R, sink.G, sink.B];
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < n; i++)
            {
                int expected = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(24 + (((c * n) + i) * 2)));
                if (expected != got[c][i])
                    Assert.Fail($"{name} component {c} sample {i}: y={y[i]} cb={cb[i]} cr={cr[i]} expected {expected}, got {got[c][i]}");
            }
        }
    }

    [Fact]
    public void Converter_ShortChroma_ThrowsIndexOutOfRange_NotAnOverread()
    {
        var sink = new IntSink(9 * 7);
        int[] y = new int[9 * 7];
        int[] chroma = new int[3];

        Assert.Throws<IndexOutOfRangeException>(() =>
        {
            var s = sink;
            OpenJpegColor.Sycc420(8, y, chroma, chroma, 9, 7, 5, 0, 0, ref s);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void Converter_PrecisionOutsideOneToThirty_IsInvalidData(int prec)
    {
        var sink = new IntSink(1);
        int[] one = [0];

        Assert.Throws<InvalidDataException>(() =>
        {
            var s = sink;
            OpenJpegColor.Sycc444(prec, one, one, one, 1, 1, ref s);
        });
    }

    [Theory]
    [MemberData(nameof(DecodeCases))]
    public void Decode_MatchesOpjDecompressPipeline(string name)
    {
        string file = File.Exists(Path.Combine(FixtureDir, name + ".jp2"))
            ? Path.Combine(FixtureDir, name + ".jp2")
            : Path.Combine(FixtureDir, name + ".j2k");
        byte[] expected = File.ReadAllBytes(Path.Combine(FixtureDir, name + ".rgb"));
        Assert.Equal("RGB1"u8.ToArray(), expected[..4]);
        bool isImage = BinaryPrimitives.ReadInt32LittleEndian(expected.AsSpan(4)) != 0;
        var codec = new OpenJpegJpeg2000Codec(NullLogger<OpenJpegJpeg2000Codec>.Instance);

        if (!isImage)
        {
            Assert.Throws<InvalidDataException>(() => codec.Decode(File.ReadAllBytes(file)));
            return;
        }

        IImageSource image = codec.Decode(File.ReadAllBytes(file));
        int w = BinaryPrimitives.ReadInt32LittleEndian(expected.AsSpan(8));
        int h = BinaryPrimitives.ReadInt32LittleEndian(expected.AsSpan(12));
        Assert.Equal(w, image.Width);
        Assert.Equal(h, image.Height);
        int o = 16;
        for (int row = 0; row < h; row++)
        {
            ReadOnlySpan<Rgb24> line = image.GetScanline(row);
            for (int x = 0; x < w; x++, o += 3)
                Assert.True(new Rgb24(expected[o], expected[o + 1], expected[o + 2]) == line[x], $"{name} pixel ({x},{row})");
        }
    }

    [Fact]
    public void Decode_Expectations_CoverTheColourRules()
    {
        Assert.True(IsImage("e2e_sycc420_p8_33x17"));
        Assert.True(IsImage("e2e_srgb_subsampled420_p8_33x17")); // 3 subsampled components not flagged sYCC → sYCC
        Assert.True(IsImage("e2e_sycc420_p8_j2k_33x17")); // raw codestream, colour space unspecified
        Assert.True(IsImage("e2e_eycc_subsampled_19x11")); // the literal predicate re-flags it as sYCC 4:2:0
        Assert.True(IsImage("e2e_grey2_p8_19x11"));
        Assert.False(IsImage("e2e_sycc_cannot_convert_19x11"));
    }

    [Fact]
    public void Decode_SignedChromaEycc_UsesComponentZeroPrecisionUnsigned()
    {
        // With per-component re-centring the signed chroma planes would be shifted by 128 again.
        byte[] expected = File.ReadAllBytes(Path.Combine(FixtureDir, "e2e_eycc_p8_signed_19x11.rgb"));
        IImageSource image = new OpenJpegJpeg2000Codec(NullLogger<OpenJpegJpeg2000Codec>.Instance)
            .Decode(File.ReadAllBytes(Path.Combine(FixtureDir, "e2e_eycc_p8_signed_19x11.jp2")));
        Rgb24 first = image.GetScanline(0)[0];

        Assert.Equal(new Rgb24(expected[16], expected[17], expected[18]), first);
    }

    [Fact]
    public void Decode_Precision_12Bit4BitAndSigned_GiveFullRangePixels()
    {
        // QSSTV keeps the low byte (jp2io.cpp:329-331): 12-bit wraps, 4-bit is dark, signed is offset by 128.
        // The make_image generator puts the minimum, maximum and mid value in samples 0, 1 and 2.
        foreach (string name in new[] { "e2e_srgb_p12_19x11", "e2e_srgb_p4_19x11", "e2e_srgb_signed8_19x11" })
        {
            IImageSource image = new OpenJpegJpeg2000Codec(NullLogger<OpenJpegJpeg2000Codec>.Instance)
                .Decode(File.ReadAllBytes(Path.Combine(FixtureDir, name + ".jp2")));
            ReadOnlySpan<Rgb24> row = image.GetScanline(0);
            Assert.Equal(0, row[0].R);
            Assert.Equal(name.Contains("p4", StringComparison.Ordinal) ? 240 : 255, row[1].R);
            Assert.Equal(128, row[2].R);
        }
    }

    private static bool IsImage(string name) =>
        BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(Path.Combine(FixtureDir, name + ".rgb")).AsSpan(4)) != 0;

    private sealed record Plane(uint Dx, uint Dy, int W, int H, int Prec, bool Sgnd, int[] Data);

    private sealed record YccCase(int Kind, uint X0, uint Y0, Plane[] Planes, bool Converted, int OutW, int OutH, int[][] Out)
    {
        public static YccCase Load(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            Assert.Equal("YCC1"u8.ToArray(), b[..4]);
            int at = 4;
            int Next()
            {
                int v = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
                at += 4;
                return v;
            }

            int kind = Next();
            uint x0 = (uint)Next(), y0 = (uint)Next();
            int n = Next();
            var meta = new (uint Dx, uint Dy, int W, int H, int Prec, bool Sgnd)[n];
            for (int i = 0; i < n; i++)
                meta[i] = ((uint)Next(), (uint)Next(), Next(), Next(), Next(), Next() != 0);
            var planes = new Plane[n];
            for (int i = 0; i < n; i++)
            {
                int[] data = new int[meta[i].W * meta[i].H];
                for (int k = 0; k < data.Length; k++)
                    data[k] = Next();
                planes[i] = new Plane(meta[i].Dx, meta[i].Dy, meta[i].W, meta[i].H, meta[i].Prec, meta[i].Sgnd, data);
            }

            bool converted = Next() != 0;
            int outW = 0, outH = 0;
            int[][] output = [];
            if (converted)
            {
                outW = Next();
                outH = Next();
                output = new int[3][];
                for (int i = 0; i < 3; i++)
                {
                    output[i] = new int[outW * outH];
                    for (int k = 0; k < output[i].Length; k++)
                        output[i][k] = Next();
                }
            }

            Assert.Equal(b.Length, at);
            return new YccCase(kind, x0, y0, planes, converted, outW, outH, output);
        }
    }

    private struct IntSink(int length) : IRgbSink
    {
        public int[] R { get; } = new int[length];

        public int[] G { get; } = new int[length];

        public int[] B { get; } = new int[length];

        public bool[] Written { get; } = new bool[length];

        public readonly void Set(int index, int r, int g, int b)
        {
            R[index] = r;
            G[index] = g;
            B[index] = b;
            Written[index] = true;
        }
    }
}
