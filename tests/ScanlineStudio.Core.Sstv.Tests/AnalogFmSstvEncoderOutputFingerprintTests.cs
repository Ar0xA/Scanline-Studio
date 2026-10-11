using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Fingerprints of the production TX encoder output (<see cref="AnalogFmSstvEncoder.EncodeBatchedAsync"/>
/// and the bench-only <c>EncodeWithCarrierOffsetAsync</c>). These prove "unchanged", not "matches
/// legacy": legacy TX parity still waits on the TX re-capture (<c>GoldenVectorTests</c>). They are
/// the only pin on production at a non-zero sample-rate or carrier offset, where the
/// <c>RenderSegments</c> copy cannot model the nominal-rate filters plus effective-rate tone.
///
/// Two theories over one case list:
/// (i) exact sample count + SHA-256 over the little-endian float bits; linux-x64 only, because
/// <c>Math.Sin</c> is not guaranteed bit-identical across platforms/libm.
/// (ii) exact sample count + SHA-256 over little-endian int32 <c>(int)Math.Round(sample * 32768)</c>;
/// runs everywhere. A platform whose <c>Math.Sin</c> moves a sample across a 1/32768 rounding
/// boundary fails here; there is deliberately no per-platform hash table.
///
/// Recorded from commit 1c6acd9a plus the uncommitted visibility change to
/// <c>GenerateFrequencySegments</c>, on linux-x64, glibc 2.39 (Ubuntu 2.39-0ubuntu8.9).
/// Both theories verified passing on Microsoft.NETCore.App 10.0.12 (net8 assembly rolled forward)
/// and 8.0.31.
/// Never re-record a hash to make a refactor pass: a changed hash means the transmitted audio changed.
/// </summary>
public class AnalogFmSstvEncoderOutputFingerprintTests
{
    private const int SampleRate = 11025;

    private static readonly StationIdTransmitOptions CwId =
        new() { CwEnabled = true, CwResolvedText = "TEST", CwToneFrequencyHz = 1000, CwWpm = 28 };

    private static readonly StationIdTransmitOptions FskId =
        new() { FskIdEnabled = true, Callsign = "W1AW", NrRstText = "599" };

    private static readonly float[] SoundFile = CreateSoundFileSamples(3000);

    private static readonly FingerprintCase[] AllCases =
    [
        new("robot-36-default", SstvModeRegistry.Robot36, 421816,
            "081A28DEF5E8745C8A6CE37BFD0DA2FE6629F67A94B9C8DA98BC4A1B85545483",
            "4A6F699344CFBB1D3B569AAC9B1136885EEE72F9250D6C704F89F22155395D7F"),
        new("scottie-s1-default", SstvModeRegistry.ScottieS1, 1236691,
            "7C44E28D0B958BE6A1DE307C2FEBB86BDB62F0BBBF722BBA3E4142BCE65917BA",
            "AD15C70B04F4BCA461899EA951907F45999A8536A7FA1082DD9D057AB4132881"),
        new("martin-m1-lpf-on", SstvModeRegistry.MartinM1, 1288234,
            "6B397CF487C155F0D3DAF1FB1B955150F783D0AD1A42FBB138ABC8DDCCC1C7E0",
            "78750BE84A4EBE7F7105593C955F7B11C87C47E56582C0F269ECE65D4361D5FF") { TxLpfEnabled = true },
        new("pd90-bpf-off", SstvModeRegistry.Pd90, 1020905,
            "9BDBAC21BD5802BE28CEBC68235812E87AEECDEFC61B228B739A7E4941F5E9D7",
            "D790C48B05BB394044D71C5669F52035ED5F2C930A6DFE7AAFF71025CFF486AE") { TxBpfEnabled = false },
        new("robot-36-bpf-off-lpf-on", SstvModeRegistry.Robot36, 421816,
            "AE7830DFA293691DCC42AFF5BEBA0C705FF70AF8A542F1968607584FB7C7EDF8",
            "6CF34BE301042A124EC721A85965D835A6A97570B6120C86090EDB193EE4D258") { TxBpfEnabled = false, TxLpfEnabled = true },
        new("robot-36-tap-48", SstvModeRegistry.Robot36, 421816,
            "110441EAF72BD41866066412558D1AC45942F9DB4151A58F836C0AA3F87D874A",
            "BDF6CE9E68F1A6B1C46EA6940FD2B36A47A6DECFAA2D9D01CE846DF9D2ADDB62") { TxBpfTapCount = 48 },

        // LPF window 5 at the nominal rate vs 6 at the effective rate.
        new("robot-36-lpf-2005-offset-plus-12.5", SstvModeRegistry.Robot36, 422294,
            "5CEF1697197F1CB9299D1098673E988AD57506A21FF45BC6DE34BC073A3B6CD0",
            "526FBAC8F05E2143ECEB2AD681FA9CE17FD25ECC590B1ECB35A6CB739FE42CD2")
            { TxLpfEnabled = true, TxLpfFrequencyHz = 2005.0, SampleRateOffsetHz = 12.5 },

        // LPF window 6 at the nominal rate vs 5 at the effective rate.
        new("robot-36-lpf-2004-offset-minus-7.3", SstvModeRegistry.Robot36, 421537,
            "9A1ADCA04F43CBC9DAAE91419FA2AEF1BE793FB836A74877925C8CFD9A9A3682",
            "EFA23D974F1A4C4924ACBD543251A8B631D95525EF8CB562CB0D735ACE677243")
            { TxLpfEnabled = true, TxLpfFrequencyHz = 2004.0, SampleRateOffsetHz = -7.3 },

        new("robot-36-bpf-off-offset-plus-12.5", SstvModeRegistry.Robot36, 422294,
            "64533A557DB73FFBA0F5921676A521A484862E8DD7DA2B6AAE125C8474DD4450",
            "F2A7B1EE688E383E1A074FAB519373B1AEA95175A69A9DFAE4D430818B1524A8")
            { TxBpfEnabled = false, SampleRateOffsetHz = 12.5 },
        new("mn73-offset-plus-12.5", SstvModeRegistry.Mn73, 825715,
            "92F83625D277AD01932927FC604C7E0AC3A60652FD652A4FA132FAA4480342A2",
            "005A1644A77E62B208D85B7761F73C4A8AA7DEBB09A2A3E82D0E580BDB813631") { SampleRateOffsetHz = 12.5 },
        new("avt-offset-minus-7.3", SstvModeRegistry.Avt, 1097552,
            "22D562D9EE958DECC37E665B8901A802B3F605FCD722764BA73BF9256004367D",
            "AA97E22171B46CDCD3F014B4BA7E54735039C661E7C752FD3E54230AD66302C8") { SampleRateOffsetHz = -7.3 },
        new("robot-36-carrier-offset-plus-30", SstvModeRegistry.Robot36, 421816,
            "F612146E133D2B1C07F786CC0DB2BFBC72351B31B38F966C06B1A29C3CEA3BBA",
            "EF0729371199B3428B4E438B3AC9AC99909229C50A47E74FFE2088BA7FCC8669") { CarrierOffsetHz = 30.0 },
        new("robot-36-cw-id", SstvModeRegistry.Robot36, 435062,
            "65A58AA006A603091CCEA831BA2B95EC4BB9655206EE6952A4EDB8CEB7B2D577",
            "8E128EF3AE02BC642081DFE8A3F9AF6D0DEDE677B5784793A864A2BDFFBB9297") { StationId = CwId },
        new("robot-36-fsk-id", SstvModeRegistry.Robot36, 431694,
            "3BE55B58A5E2AE5268D6CD9A2196F15478EE492E1A5C3746E547E47F3C05BF6A",
            "5299DFE1A76579A8D31BEE023F08B2AB59701DD33D66F283023D3AE7BFB703D1") { StationId = FskId },
        new("robot-36-sound-file-bpf-on", SstvModeRegistry.Robot36, 424816,
            "947A60747A03FC70C5DE60F38E012D43557DFC897B316DDF00E8292409C00E01",
            "5BC0A3959E0206332FC746D60BAD9DA97C79EA2C9EEBAE56BCCCF4CF68F3AD81")
            { StationId = new StationIdTransmitOptions { SoundFileSamples = SoundFile } },
        new("robot-36-sound-file-bpf-off", SstvModeRegistry.Robot36, 424816,
            "BB295C0A679355E0CC2E0B844EEE9DA2AA4E1AF858BD2D8A4E01D01AFA26327C",
            "40AA23BF9256806CE8B1EE65A6C4F077907DCC6D09CCED8F8E81E7DEE29F8A80")
            { StationId = new StationIdTransmitOptions { SoundFileSamples = SoundFile }, TxBpfEnabled = false },

        // CW wins when both are set (AnalogFmSstvEncoder's `stationId.CwEnabled ? null : ...` guard).
        new("robot-36-cw-and-sound-file", SstvModeRegistry.Robot36, 435062,
            "65A58AA006A603091CCEA831BA2B95EC4BB9655206EE6952A4EDB8CEB7B2D577",
            "8E128EF3AE02BC642081DFE8A3F9AF6D0DEDE677B5784793A864A2BDFFBB9297")
            { StationId = CwId with { SoundFileSamples = SoundFile } },
    ];

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in AllCases)
        {
            data.Add(c.Name);
        }

        return data;
    }

    [LinuxX64FingerprintTheory]
    [MemberData(nameof(CaseNames))]
    public async Task ExactFloatBits_MatchRecordedCountAndSha256(string caseName)
    {
        var c = AllCases.Single(x => x.Name == caseName);
        var samples = await EncodeAsync(c);

        Assert.Equal(c.ExpectedCount, samples.Length);
        Assert.Equal(c.ExpectedFloatSha256, HashFloatBits(samples));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Quantized16BitValues_MatchRecordedCountAndSha256(string caseName)
    {
        var c = AllCases.Single(x => x.Name == caseName);
        var samples = await EncodeAsync(c);

        Assert.Equal(c.ExpectedCount, samples.Length);
        Assert.Equal(c.ExpectedQuantizedSha256, HashQuantized(samples));
    }

    [Fact]
    public async Task SameEncoderInstance_TwoEncodes_HashIdentically()
    {
        // BPF and LPF both on, so state leaking through either filter between calls would show.
        var mode = SstvModeRegistry.Robot36;
        var image = AnalogFmSstvEncoderLoopParityTests.CreatePatternImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);

        var first = await CollectBatchesAsync(encoder.EncodeBatchedAsync(mode, image, txBpfEnabled: true, txLpfEnabled: true));
        var second = await CollectBatchesAsync(encoder.EncodeBatchedAsync(mode, image, txBpfEnabled: true, txLpfEnabled: true));

        Assert.Equal(first.Length, second.Length);
        Assert.Equal(HashFloatBits(first), HashFloatBits(second));
    }

    private static async Task<float[]> EncodeAsync(FingerprintCase c)
    {
        var image = AnalogFmSstvEncoderLoopParityTests.CreatePatternImage(c.Mode.ImageWidth, c.Mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);

        if (c.CarrierOffsetHz != 0.0)
        {
            var list = new List<float>();
            await foreach (var sample in encoder.EncodeWithCarrierOffsetAsync(c.Mode, image, c.CarrierOffsetHz))
            {
                list.Add(sample);
            }

            return list.ToArray();
        }

        return await CollectBatchesAsync(encoder.EncodeBatchedAsync(
            c.Mode, image, c.StationId, c.SampleRateOffsetHz, c.TxBpfEnabled, c.TxBpfTapCount,
            c.TxLpfEnabled, c.TxLpfFrequencyHz));
    }

    private static async Task<float[]> CollectBatchesAsync(IAsyncEnumerable<ReadOnlyMemory<float>> batches)
    {
        var list = new List<float>();
        await foreach (var batch in batches)
        {
            list.AddRange(batch.ToArray());
        }

        return list.ToArray();
    }

    private static string HashFloatBits(float[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(float)];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), samples[i]);
        }

        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string HashQuantized(float[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(int)];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * sizeof(int)), (int)Math.Round((double)samples[i] * 32768.0));
        }

        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    /// <summary>Closed-form sawtooth-like raw samples within +/-0.79, never <c>Random</c>.</summary>
    private static float[] CreateSoundFileSamples(int length)
    {
        var samples = new float[length];
        for (var i = 0; i < length; i++)
        {
            samples[i] = (((i * 37) % 201) - 100) / 128f;
        }

        return samples;
    }

    private sealed record FingerprintCase(
        string Name, SstvModeDefinition Mode, long ExpectedCount, string ExpectedFloatSha256, string ExpectedQuantizedSha256)
    {
        public StationIdTransmitOptions StationId { get; init; } = StationIdTransmitOptions.None;

        public double SampleRateOffsetHz { get; init; }

        public bool TxBpfEnabled { get; init; } = true;

        public int TxBpfTapCount { get; init; } = TxOutputBandpassFilter.DefaultTapCount;

        public bool TxLpfEnabled { get; init; }

        public double TxLpfFrequencyHz { get; init; } = 2000.0;

        public double CarrierOffsetHz { get; init; }
    }
}

/// <summary>Runs the theory only on linux-x64, where the exact float hashes were recorded; skips the
/// whole theory elsewhere (xunit 2.5.3 applies <c>Skip</c> to every row, see
/// <see cref="StaleFixtureTheoryAttribute"/>).</summary>
public sealed class LinuxX64FingerprintTheoryAttribute : TheoryAttribute
{
    public LinuxX64FingerprintTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Skip = "Exact float-bit TX fingerprints are recorded on linux-x64 only; "
                + "the cross-platform quantized fingerprint theory still runs here.";
        }
    }
}
