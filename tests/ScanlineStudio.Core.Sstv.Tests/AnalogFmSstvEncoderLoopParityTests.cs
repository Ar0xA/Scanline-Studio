using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Pins the test-only <see cref="AnalogFmSstvEncoder.RenderSegments"/> copy to the production
/// <see cref="AnalogFmSstvEncoder.EncodeBatchedAsync"/> loop, bit for bit, at zero sample-rate and
/// carrier offset. Every other <c>RenderSegments</c>-based test (LPF, silence, golden vectors) only
/// says something about production because of this equality. At a non-zero offset the copy cannot
/// model production (it uses one rate for filters and tone), so offsets are pinned by
/// <see cref="AnalogFmSstvEncoderOutputFingerprintTests"/> instead.
/// Tolerance is 0: float bit patterns are compared, so -0.0 vs 0.0 or NaN would also fail.
/// </summary>
public class AnalogFmSstvEncoderLoopParityTests
{
    private const int SampleRate = 11025;
    private const double DefaultLpfFrequencyHz = 2000.0;

    private static readonly StationIdTransmitOptions CwId =
        new() { CwEnabled = true, CwResolvedText = "TEST", CwToneFrequencyHz = 1000, CwWpm = 28 };

    private static readonly Dictionary<string, SstvModeDefinition> Modes = new()
    {
        ["robot-36"] = SstvModeRegistry.Robot36, // YCbCrRobot
        ["rm12"] = SstvModeRegistry.Rm12, // MonoAveragedPaired, forced VIS parity
        ["martin-m1"] = SstvModeRegistry.MartinM1, // RgbSequential
        ["robot-72"] = SstvModeRegistry.Robot72, // YCbCrSequential
        ["pd90"] = SstvModeRegistry.Pd90, // YCbCrLinePaired
        ["avt"] = SstvModeRegistry.Avt, // AVT header
        ["mn73"] = SstvModeRegistry.Mn73, // narrow header
    };

    public static TheoryData<string, bool, bool, bool> Cases()
    {
        var data = new TheoryData<string, bool, bool, bool>();
        foreach (var modeId in new[] { "robot-36", "rm12" })
        {
            foreach (var bpf in new[] { true, false })
            {
                foreach (var lpf in new[] { false, true })
                {
                    data.Add(modeId, bpf, lpf, false);
                }
            }
        }

        foreach (var modeId in new[] { "martin-m1", "robot-72", "pd90", "avt", "mn73" })
        {
            data.Add(modeId, true, false, false);
        }

        data.Add("robot-36", true, false, true);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EncodeBatchedAsync_AtZeroOffset_IsBitIdenticalToRenderSegmentsOfGenerateFrequencySegments(
        string modeId, bool txBpfEnabled, bool txLpfEnabled, bool cwId)
    {
        var mode = Modes[modeId];
        var image = CreatePatternImage(mode.ImageWidth, mode.ImageHeight);
        var stationId = cwId ? CwId : StationIdTransmitOptions.None;
        var encoder = new AnalogFmSstvEncoder(SampleRate);

        var production = await CollectBatchesAsync(encoder.EncodeBatchedAsync(
            mode, image, stationId, sampleRateOffsetHz: 0.0, txBpfEnabled: txBpfEnabled,
            txBpfTapCount: TxOutputBandpassFilter.DefaultTapCount, txLpfEnabled: txLpfEnabled,
            txLpfFrequencyHz: DefaultLpfFrequencyHz));

        var segments = AnalogFmSstvEncoder.GenerateFrequencySegments(
            mode, image, ScanlineCodecFactory.CreateEncoder(mode.ColorEncoding), stationId);
        var reference = AnalogFmSstvEncoder.RenderSegments(
            segments, encoder.SampleRate, applyFilter: txBpfEnabled,
            tapCount: TxOutputBandpassFilter.DefaultTapCount, applyLpf: txLpfEnabled,
            lpfFrequencyHz: DefaultLpfFrequencyHz).ToArray();

        Assert.True(production.Length > 0);
        Assert.Equal(reference.Length, production.Length);
        var firstMismatch = FirstBitMismatch(reference, production);
        Assert.True(firstMismatch < 0,
            $"First bit mismatch at sample {firstMismatch}: reference {(firstMismatch < 0 ? 0 : reference[firstMismatch])}, " +
            $"production {(firstMismatch < 0 ? 0 : production[firstMismatch])}.");
    }

    private static int FirstBitMismatch(float[] expected, float[] actual)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            if (BitConverter.SingleToInt32Bits(expected[i]) != BitConverter.SingleToInt32Bits(actual[i]))
            {
                return i;
            }
        }

        return -1;
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

    /// <summary>Closed-form, non-flat image so every channel of every line carries a different
    /// value; never <c>Random</c>.</summary>
    internal static ArrayImageSource CreatePatternImage(int width, int height)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = new Rgb24(
                    (byte)(((7 * x) + (13 * y)) & 255),
                    (byte)(((3 * x) + (5 * y) + 80) & 255),
                    (byte)(((11 * x) + (2 * y) + 160) & 255));
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }
}
