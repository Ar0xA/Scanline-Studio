using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// spec/18-path-to-1.0.md Medium item (TX send-progress feedback): <see
/// cref="AnalogFmSstvEncoder.EstimateSampleCount"/> must return the EXACT total sample count a
/// matching <see cref="AnalogFmSstvEncoder.EncodeAsync"/> call will emit, not an approximation --
/// used to compute a progress fraction, where even a one-sample-off total would eventually show
/// 101%/never-quite-100%. Plan-review's own central finding: naively summing <c>DurationMs</c> then
/// multiplying once is NOT guaranteed bit-identical to <c>EncodeAsyncCore</c>'s real running
/// accumulator (floating-point addition isn't associative over hundreds of thousands of segments),
/// so a single-mode spot check proves nothing about every other mode -- these tests deliberately
/// span several structurally different mode families (normal, narrow, AVT) and the station-ID
/// footer path (which varies real duration with resolved text), not just one.
/// </summary>
public class AnalogFmSstvEncoderEstimateSampleCountTests
{
    private const int SampleRate = 11025;

    private static ArrayImageSource CreateSolidImage(int width, int height) =>
        new(width, height, Enumerable.Repeat(new Rgb24(128, 64, 200), width * height).ToArray());

    private static async Task<long> CountRealSamplesAsync(AnalogFmSstvEncoder encoder, SstvModeDefinition mode, IImageSource image, StationIdTransmitOptions? stationId = null)
    {
        var count = 0L;
        await foreach (var _ in encoder.EncodeAsync(mode, image, stationId))
        {
            count++;
        }

        return count;
    }

    public static IEnumerable<object[]> ModesToVerify()
    {
        yield return [SstvModeRegistry.MartinM1, "normal Martin family"];
        yield return [SstvModeRegistry.ScottieS1, "normal Scottie family"];
        yield return [SstvModeRegistry.Mn73, "narrow-mode footer branch"];
        yield return [SstvModeRegistry.Avt, "AVT header branch"];
    }

    [Theory]
    [MemberData(nameof(ModesToVerify))]
    public async Task EstimateSampleCount_MatchesRealEncodeAsyncTotalExactly_NoStationId(SstvModeDefinition mode, string reason)
    {
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);

        var estimated = encoder.EstimateSampleCount(mode, image);
        var actual = await CountRealSamplesAsync(encoder, mode, image);

        Assert.True(estimated == actual, $"{reason}: estimated {estimated} vs actual {actual}");
    }

    [Fact]
    public async Task EstimateSampleCount_MatchesRealEncodeAsyncTotalExactly_WithCwAndFskStationId()
    {
        // The footer/station-ID path is where real duration varies most with resolved text length --
        // the case most likely to expose a divergence if the accumulator-replication were subtly wrong.
        var mode = SstvModeRegistry.Robot36;
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var stationId = new StationIdTransmitOptions
        {
            FskIdEnabled = true,
            Callsign = "W1AW",
            CwEnabled = true,
            CwResolvedText = "TEST DE W1AW",
            CwToneFrequencyHz = 1000,
            CwWpm = 28,
        };

        var estimated = encoder.EstimateSampleCount(mode, image, stationId);
        var actual = await CountRealSamplesAsync(encoder, mode, image, stationId);

        Assert.Equal(actual, estimated);
    }

    [Fact]
    public void EstimateSampleCount_ImageSmallerThanMode_ThrowsImmediately()
    {
        // Same synchronous-guard contract EncodeAsync has (AnalogFmSstvEncoderInputValidationTests)
        // -- ISstvEncoder's doc comment requires EstimateSampleCount to apply it too.
        var mode = SstvModeRegistry.Robot72; // 320x240
        var tooSmall = new ArrayImageSource(160, 120, new Rgb24[160 * 120]);
        var encoder = new AnalogFmSstvEncoder(SampleRate);

        var ex = Assert.Throws<ArgumentException>(() => encoder.EstimateSampleCount(mode, tooSmall));
        Assert.Contains("160x120", ex.Message);
        Assert.Contains("320x240", ex.Message);
    }

    [Fact]
    public void EstimateSampleCount_NoStationIdParam_MatchesExplicitNone()
    {
        var mode = SstvModeRegistry.Robot36;
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);

        var withoutParam = encoder.EstimateSampleCount(mode, image);
        var withExplicitNone = encoder.EstimateSampleCount(mode, image, StationIdTransmitOptions.None);

        Assert.Equal(withoutParam, withExplicitNone);
    }
}
