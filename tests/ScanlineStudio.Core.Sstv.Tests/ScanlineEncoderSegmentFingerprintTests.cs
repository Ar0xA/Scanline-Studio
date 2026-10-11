using System.Buffers.Binary;
using System.Security.Cryptography;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Pins the exact (FrequencyHz, DurationMs) segment sequence every registered mode's line encoder
/// emits for a whole frame. These prove "unchanged", not "matches legacy": an encoder and decoder can
/// agree with each other while both are wrong about the wire (CLAUDE.md §4, the Scottie incident).
///
/// The path has only + − × ÷, <c>Math.Floor</c> and byte widening (<see cref="YCbCr"/>), no
/// transcendentals, so the hashes are platform-independent.
///
/// Recorded from commit 3f3284b2 (before the shared line-loop base class).
/// Never re-record a hash to make a refactor pass: a changed hash means the transmitted audio changed.
/// </summary>
public class ScanlineEncoderSegmentFingerprintTests
{
    private static readonly Dictionary<string, (int PairCount, string Sha256)> Expected = new()
    {
        ["avt"] = (230400, "16CF72411448D3C871782C13A50EF953097017AAEDC8D7ECFCD21EDE26F3B756"),
        ["martin-m1"] = (247040, "2B6E0F1747F044BF718E354DCC3E61B69A797AF3853799C62DA91644C143FD03"),
        ["martin-m2"] = (247040, "D6E9D3ACE9FD386413CF00F72EDED584947381F5B8C1F5C21ACD5287F0514094"),
        ["mc110"] = (246272, "FBCBDC622F31B3BF5EA632B7AC713B13EEE77D8DEB2C55FF32E36A9DE38A25E9"),
        ["mc140"] = (246272, "FD95C994E9E5F0C1AC24B30418C039FEC31045F6F26CF3B44A97515B351D5604"),
        ["mc180"] = (246272, "AC13168B455383EB2CB572DC2717048A00177107B028BFCE7D110582B363CB19"),
        ["ml180"] = (954800, "A451EAF4029357BB52171FCC7D5544A91995A166B566E5F4D573E46402B9010D"),
        ["ml240"] = (954800, "B0ABE215D9F76A386B3FE32BB0BC7AF5D8D14735056C68C5B85FD42EEE66B7B4"),
        ["ml280"] = (954800, "C6701DCD9E4949AFC4AB4B91841739E59CFAF208A11094844E1BBB6FD359DC11"),
        ["ml320"] = (954800, "F98002C7B1122EF202615041593C22A0B0A0EE46F303F886582F47426B4781D3"),
        ["mn110"] = (164096, "0159D06C7E30FFE71287AB2ADDF0182970BD895610F0505FFD8734862C1D5C76"),
        ["mn140"] = (164096, "5C7955D289CB509F74F306795A2002B45F3881DF6EBE022FC69F86358725EA04"),
        ["mn73"] = (164096, "B5B596D4E6D948EBBA6C20D17444A75A801D6184758898D16FB5F647EC182E57"),
        ["mp115"] = (164096, "2E5029CEAB7F1FBF6278F4EF495EB0971D3C69D8D6C0EC47F2DE2DFE44F973A7"),
        ["mp140"] = (164096, "D99B609FA088383255A878D45467837CBF40F632EA2DE2EF4E5F8FEF3BC4AF04"),
        ["mp175"] = (164096, "547EEB06F1F0ECF78682ED6D03E86F6F00EF203C8466253A5FCD118AE119D852"),
        ["mp73"] = (164096, "E1715F3CEA8182ED0FFCE4DAAE148D0D61E0075C432A811F71D439CE49714ED1"),
        ["mr115"] = (247040, "E0268C0D6FABB758429FFE2150510E0F1C921E2DAB34EF33BC753D60981BAA86"),
        ["mr140"] = (247040, "0A3418FF847CE5924BDA2507F3BB904B32BABF5C13A17BCF3E2799021B99A4F4"),
        ["mr175"] = (247040, "0E3498CFACFF2F7C92D72A46D21B7942A652D06D62781360ABCEE984C9096DF4"),
        ["mr73"] = (247040, "A106E3A811111BBB522E850856E83E6C3B6DEA4D491D39DBF2DA46FC21108E68"),
        ["mr90"] = (247040, "C4EB9F091931C8BE6A9BDCB29B372EE1DC9358B1A90DFDF97D399FA94CB3AFC3"),
        ["p3"] = (954800, "8D74AD9F6D35333CD108CA68C5638CA48396104D86BE5CCBCA9CD81806D089C5"),
        ["p5"] = (954800, "6C94296A44C1FD915345B3D7BFD08F618E7B1ECABB656C2D02A6EEBD3FBE6FC2"),
        ["p7"] = (954800, "AC4EC429B6B3C380FEBD8DECEE09223CEE72E4A9B8610D0032B8865C712CFD29"),
        ["pd120"] = (635376, "6BBDCD27346608D21B37DD53A4B2BDABE590A6DE571AA7F69BF4C50391ED8E9E"),
        ["pd160"] = (410000, "E8FA20F79A904873FFBE64A19A103E90DDE0C76A63CB2179C2524651DA5BB8B0"),
        ["pd180"] = (635376, "A2BCAD29F3F841964EDE0610FE10740994E266F346DB949D1E93C0CF25E60DC4"),
        ["pd240"] = (635376, "B959B18BFC61DDCEAA25837C510A23F5449BFB97EFA3DA6D4E656A0328AD9CCF"),
        ["pd290"] = (986216, "C6CF584971FB6D66A0B1D4848EE9099081989ED56E3102FBA19558F9FE004E8B"),
        ["pd50"] = (164096, "D51C0FEE63C1BE277ADC1FC7690DD71955757A9E3C304E5B39B6E2AF6D5D5548"),
        ["pd90"] = (164096, "B1BE883D26EE1AB007D56BF77CCD72653D6568A60010CC58179F43164CB777B6"),
        ["r24"] = (115920, "AD4555C9CD6866B09842F54D0BB528D7919568AFF247F6109B94C11455FEED70"),
        ["rm12"] = (38640, "674A316D1A1A0040CC1AFE06AA0B2757B06DCF6983B906096A0914137945DBC8"),
        ["rm8"] = (38640, "025B4F71881A0587FB2ABC5ACECFA25BD8347A1DF253E3B9E6560CF99699A662"),
        ["robot-36"] = (154560, "27668A2CD436428E3C90DAB5AD915AFF77D83B18D0D350FEFD3104D6A3D0AF81"),
        ["robot-72"] = (231840, "0E576D0C73F5BF6C4E6F48BF02AF67CFE6817B89D7DECF722194777FCAEA917E"),
        ["sc2-120"] = (246272, "2144E5FF43A9C444915860797CDEED08FC60D32C2BD7448C6FE09068A56917F9"),
        ["sc2-180"] = (246272, "D03B9798C1495AFA407901121964B0F78F7913A4E1D7A06646CAA1D42140AECD"),
        ["sc2-60"] = (246272, "3428EA9AA700854F858C65297E8AEB0DDBF680D9AF4705FE9F462656798282B0"),
        ["scottie-dx"] = (246784, "49C085ED5922078C31A53933009EF1027FB9D1C65EF280739BBA337EDAA802B4"),
        ["scottie-s1"] = (246784, "A6E52CAF6882C2210C7CBCA8DFDAB94C29B16A2EB89BC07780E26338EC63B12A"),
        ["scottie-s2"] = (246784, "6C118D48A931B8A8300194694A6D0C4111F776614604B619511572581C57437C"),
    };

    public static TheoryData<string> AllModeIds()
    {
        var data = new TheoryData<string>();
        foreach (var mode in SstvModeRegistry.All)
        {
            data.Add(mode.Id);
        }

        return data;
    }

    [Fact]
    public void Registry_HasFortyThreeModes_AllFingerprinted()
    {
        Assert.Equal(43, SstvModeRegistry.All.Count);
        Assert.Equal(SstvModeRegistry.All.Select(m => m.Id).Order(), Expected.Keys.Order());
    }

    [Theory]
    [MemberData(nameof(AllModeIds))]
    public void WholeFrameSegmentSequence_MatchesRecordedFingerprint(string modeId)
    {
        var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);
        var (pairCount, sha256) = Fingerprint(mode);

        Assert.True(Expected.TryGetValue(modeId, out var expected), $"No recorded fingerprint for {modeId}: ({pairCount}, \"{sha256}\")");
        Assert.Equal(expected.PairCount, pairCount);
        Assert.Equal(expected.Sha256, sha256);
    }

    [Theory]
    [InlineData(ColorEncoding.RgbSequential, "Q")]
    [InlineData(ColorEncoding.RgbSequential, "Y")]
    [InlineData(ColorEncoding.YCbCrSequential, "Q")]
    [InlineData(ColorEncoding.YCbCrSequential, "Y1")]
    [InlineData(ColorEncoding.YCbCrLinePaired, "Q")]
    [InlineData(ColorEncoding.YCbCrLinePaired, "Y")]
    public void UnknownChannelName_ThrowsNotSupported_WithTodaysMessage(ColorEncoding encoding, string channelName)
    {
        var mode = SyntheticMode(encoding, [new ScanSegment(channelName, DurationMs: 10.0)]);
        var encoder = ScanlineCodecFactory.CreateEncoder(encoding);

        var ex = Assert.Throws<NotSupportedException>(() => encoder.GenerateLine(mode, SyntheticImage(), 0).ToList());
        Assert.Equal($"Unknown channel '{channelName}'.", ex.Message);
    }

    [Fact]
    public void Mono_UnknownChannelName_DoesNotThrow_AndMatchesY()
    {
        var encoder = ScanlineCodecFactory.CreateEncoder(ColorEncoding.MonoAveragedPaired);
        var image = SyntheticImage();

        var q = encoder.GenerateLine(SyntheticMode(ColorEncoding.MonoAveragedPaired, [new ScanSegment("Q", 10.0)]), image, 0).ToList();
        var y = encoder.GenerateLine(SyntheticMode(ColorEncoding.MonoAveragedPaired, [new ScanSegment("Y", 10.0)]), image, 0).ToList();

        Assert.Equal(3, q.Count);
        Assert.Equal(Bits(y), Bits(q));
    }

    [Theory]
    [InlineData(ColorEncoding.RgbSequential, "R")]
    [InlineData(ColorEncoding.YCbCrSequential, "Y")]
    [InlineData(ColorEncoding.YCbCrLinePaired, "Y1")]
    [InlineData(ColorEncoding.MonoAveragedPaired, "Y")]
    public void ToneSelectorSegment_YieldsNothing_AndHoldRepeatsLastScanPixel(ColorEncoding encoding, string channelName)
    {
        var mode = SyntheticMode(
            encoding,
            [
                new ScanSegment(channelName, DurationMs: 10.0),
                new ToneSelectorSegment(DurationMs: 4.5, LowFrequencyHz: 1500, HighFrequencyHz: 2300),
                new HoldPreviousFrequencySegment(DurationMs: 0.1),
            ]);
        var encoder = ScanlineCodecFactory.CreateEncoder(encoding);

        var segments = encoder.GenerateLine(mode, SyntheticImage(), 0).ToList();

        Assert.Equal(mode.ImageWidth + 1, segments.Count);
        var lastScan = segments[mode.ImageWidth - 1];
        Assert.NotEqual(1500.0, lastScan.FrequencyHz);
        Assert.NotEqual(2300.0, lastScan.FrequencyHz);
        Assert.NotEqual(1900.0, lastScan.FrequencyHz);
        Assert.Equal(BitConverter.DoubleToInt64Bits(lastScan.FrequencyHz), BitConverter.DoubleToInt64Bits(segments[^1].FrequencyHz));
        Assert.Equal(0.1, segments[^1].DurationMs);
    }

    private static (int PairCount, string Sha256) Fingerprint(SstvModeDefinition mode)
    {
        IScanlineEncoder encoder = ScanlineCodecFactory.CreateEncoder(mode.ColorEncoding);
        var image = FingerprintImage(mode.ImageWidth, mode.ImageHeight);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[16];
        var count = 0;

        for (var y = 0; y < mode.ImageHeight; y += encoder.RowsPerTransmissionLine)
        {
            foreach (var (frequencyHz, durationMs) in encoder.GenerateLine(mode, image, y))
            {
                BinaryPrimitives.WriteInt64LittleEndian(buffer, BitConverter.DoubleToInt64Bits(frequencyHz));
                BinaryPrimitives.WriteInt64LittleEndian(buffer[8..], BitConverter.DoubleToInt64Bits(durationMs));
                hash.AppendData(buffer);
                count++;
            }
        }

        return (count, Convert.ToHexString(hash.GetHashAndReset()));
    }

    // Rows with y%4 in {2,3} are grey (R=G=B), which lands chroma on the 128 truncation boundary in
    // YCbCr.FromRgb; both parities of grey row are covered for LinePaired chroma and Robot R-Y lines.
    private static ArrayImageSource FingerprintImage(int width, int height)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = y % 4 is 2 or 3
                    ? new Rgb24((byte)((x + y) & 255), (byte)((x + y) & 255), (byte)((x + y) & 255))
                    : new Rgb24((byte)(((7 * x) + (13 * y)) & 255), (byte)(((11 * x) + (3 * y) + 5) & 255), (byte)(((5 * x) + (17 * y) + 9) & 255));
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }

    private static SstvModeDefinition SyntheticMode(ColorEncoding encoding, IReadOnlyList<LineSegment> segments) => new(
        Id: "test-synthetic",
        DisplayName: "Test",
        VisCode: 0,
        ImageWidth: 3,
        ImageHeight: 2,
        ColorEncoding: encoding,
        LineSegments: segments);

    private static ArrayImageSource SyntheticImage() => new(
        3,
        2,
        [
            new Rgb24(40, 90, 200), new Rgb24(120, 30, 70), new Rgb24(10, 220, 160),
            new Rgb24(200, 60, 20), new Rgb24(90, 90, 250), new Rgb24(30, 140, 60),
        ]);

    private static long[] Bits(List<(double FrequencyHz, double DurationMs)> segments) =>
        segments.SelectMany(s => new[] { BitConverter.DoubleToInt64Bits(s.FrequencyHz), BitConverter.DoubleToInt64Bits(s.DurationMs) }).ToArray();
}
