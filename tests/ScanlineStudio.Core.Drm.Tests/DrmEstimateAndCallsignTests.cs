using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Core.Drm.Tx;
using ScanlineStudio.Core.Drm.Util;
using PublicSettings = ScanlineStudio.Abstractions.Drm.DrmTxSettings;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>P4b: the side-effect-free frame count behind the TX estimate, and the display-side callsign rule
/// checked against what the FAC encoder really sends.</summary>
public class DrmEstimateAndCallsignTests
{
    private static readonly DateTimeOffset CountStamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SendStamp = new(2026, 1, 1, 12, 7, 0, TimeSpan.Zero);
    private static readonly byte[] Jp2 = File.ReadAllBytes(Path.Combine(GoldenFixtures.GoldenDir, "inputs", "card.jp2"));

    public static TheoryData<DrmRobustnessMode, DrmQamOrder, DrmReedSolomonLevel, bool> Profiles => new()
    {
        { DrmRobustnessMode.B, DrmQamOrder.Qam4, DrmReedSolomonLevel.Rs2, false },
        { DrmRobustnessMode.B, DrmQamOrder.Qam16, DrmReedSolomonLevel.Rs1, true },
        { DrmRobustnessMode.E, DrmQamOrder.Qam64, DrmReedSolomonLevel.None, false },
        { DrmRobustnessMode.A, DrmQamOrder.Qam16, DrmReedSolomonLevel.Rs4, true },
    };

    [Theory]
    [MemberData(nameof(Profiles))]
    public void CountFrames_EqualsPreparesCount_AndLeavesTheStampAndBsrCounterUntouched(
        DrmRobustnessMode mode, DrmQamOrder qam, DrmReedSolomonLevel rs, bool vox)
    {
        var settings = new PublicSettings(mode, DrmBandwidth.Hz2500, qam, DrmProtectionLevel.High, DrmInterleaving.LongInterleaver, rs, "PA0TST", vox, 20_000);
        var clock = new FixedTimeProvider(CountStamp);
        var counter = new BsrTransportIdCounter();
        var transmitter = new DrmTransmitter(new DrmTxNaming(clock), counter);
        int counterBefore = counter.Current;

        DrmFrameCount count = transmitter.CountFrames(Jp2, "/home/op/holiday photo.png", settings);

        Assert.Equal(counterBefore, counter.Current);

        // newTxFileName: false keeps whatever stamp exists; it must still be empty, so it is taken now.
        clock.Now = SendStamp;
        IDrmTransmission tx = transmitter.Prepare(Jp2, "/home/op/holiday photo.png", isImage: true, settings, newTxFileName: false);
        Assert.StartsWith("20260101120700-", tx.OnAirName, StringComparison.Ordinal);
        Assert.Equal(tx.FrameCount, count.FrameCount);
        Assert.Equal(tx.SampleCount, count.SampleCount);
    }

    [Fact]
    public void CountFrames_DependsOnTheNameLength_AsTheRealSendDoes()
    {
        var settings = new PublicSettings(DrmRobustnessMode.E, DrmBandwidth.Hz2500, DrmQamOrder.Qam64, DrmProtectionLevel.Low, DrmInterleaving.ShortInterleaver, DrmReedSolomonLevel.None, "PA0TST", false, 20_000);
        var transmitter = new DrmTransmitter(new DrmTxNaming(new FixedTimeProvider(CountStamp)), new BsrTransportIdCounter());
        string longName = new string('n', 200) + ".png";

        DrmFrameCount count = transmitter.CountFrames(Jp2, longName, settings);
        IDrmTransmission tx = transmitter.Prepare(Jp2, longName, isImage: true, settings, newTxFileName: true);

        Assert.Equal(tx.FrameCount, count.FrameCount);
    }

    public static TheoryData<string> Callsigns => new()
    {
        "PA0TST",
        "DLÄ1ABC",
        "DL😀1A",
        "PA0TST/P/QRP",
        "PA0\0TST",
        "\u0100X",
        string.Empty,
    };

    // 0x80 masks to a 0 label character mid-label; the parity holds, but such a label is not idempotent.
    public static TheoryData<string> CallsignsWithMaskedNul => new() { "Ä\u0080Z" };

    [Theory]
    [MemberData(nameof(Callsigns))]
    [MemberData(nameof(CallsignsWithMaskedNul))]
    public void Normalize_MatchesTheFacLabelBits(string callsign)
    {
        var settings = new PublicSettings(DrmRobustnessMode.B, DrmBandwidth.Hz2500, DrmQamOrder.Qam16, DrmProtectionLevel.High, DrmInterleaving.LongInterleaver, DrmReedSolomonLevel.None, callsign, false, 1000);
        var core = DrmTransmitter.ToCoreSettings(settings);
        var name = new DrmTxNaming(new FixedTimeProvider(CountStamp)).GetTxFileName("a.png", binary: false);
        var generator = new DrmSourceFrameGenerator(DrmTxObject.ForFile(Jp2, name, core.ReedSolomon), core, new BsrTransportIdCounter());
        byte[][] fac = generator.Frames().Select(f => f.FacBits).OfType<byte[]>().Take(3).ToArray();

        // Frame k carries label characters 3k..3k+2, 7 bits each from bit 10 (FAC.cpp:152-171).
        var label = new char[DrmCallsign.MaxLength];
        for (int i = 0; i < label.Length; i++)
            label[i] = (char)Bits(fac[i / 3], 10 + (7 * (i % 3)), 7);

        string expected = DrmCallsign.Normalize(callsign).PadRight(DrmCallsign.MaxLength, '\0');
        Assert.Equal(expected, new string(label));
    }

    [Theory]
    [InlineData("DLÄ1ABC", "DLD1ABC")]
    [InlineData("DL😀1A", "DL??1A")]
    [InlineData("PA0TST/P/QRP", "PA0TST/P/")]
    [InlineData("PA0\0TST", "PA0")]
    [InlineData(null, "")]
    public void Normalize_Examples(string? callsign, string expected) => Assert.Equal(expected, DrmCallsign.Normalize(callsign));

    [Theory]
    [MemberData(nameof(Callsigns))]
    public void Normalize_IsIdempotent(string callsign)
    {
        string once = DrmCallsign.Normalize(callsign);
        Assert.Equal(once, DrmCallsign.Normalize(once));
    }

    [Fact]
    public void Normalize_AgreesWithTheLatin1NulRule()
    {
        // Same cut point as the bytes the transmitter hands the FAC encoder.
        Assert.Equal(DrmLatin1.EncodeNulTerminated("AB\0CD").Length, DrmCallsign.Normalize("AB\0CD").Length);
    }

    private static int Bits(byte[] bits, int start, int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
            value = (value << 1) | bits[start + i];
        return value;
    }
}
