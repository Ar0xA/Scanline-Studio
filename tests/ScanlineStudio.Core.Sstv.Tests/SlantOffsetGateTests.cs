using System.Globalization;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>A standing regression gate for slant / sample-clock tracking, and the companion to
/// <see cref="AfcOffsetGateTests"/>.
///
/// Same vacuous-gate problem: the impairment bench encodes at exactly the decoder's own rate, so the
/// slant tracker has no clock error to measure and reports a null result however it behaves. "Helped",
/// "did nothing" and "stopped tracking entirely" are indistinguishable, which makes the standing
/// "zero degradation across all 43 modes" bar unable to fail for anything slant-related.
///
/// Unlike the AFC gate this needs no new production code at all: the encoder's `sampleRateOffsetHz`
/// already exists and is already plumbed. Encoding at 11025+d while the decoder assumes 11025 is
/// precisely a clock mismatch, worth about 91 ppm per Hz.
///
/// Why it matters in the field: a clock error accumulates across the whole image rather than
/// damaging a pixel, so an untracked one skews the picture progressively — the classic diagonal
/// tear. Two sound cards nominally at the same rate routinely differ by tens to hundreds of ppm.</summary>
public sealed class SlantOffsetGateTests
{
    private const int SampleRate = 11025;

    // The decoder assumes SampleRate; encoding at SampleRate+d makes each line arrive d/SampleRate
    // longer than expected.
    private static double ExpectedPpm(double offsetHz) => offsetHz / SampleRate * 1_000_000.0;

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]    // ~45 ppm
    [InlineData(-0.5)]
    [InlineData(1.0)]    // ~91 ppm
    [InlineData(-1.0)]
    public async Task Slant_TracksAnInjectedClockError(double offsetHz)
    {
        var mode = SstvModeRegistry.All.Single(m => m.Id == "martin-m1");
        var source = ImpairmentSweepHarness.CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
        var samples = await EncodeAsync(mode, source, offsetHz);

        var decoder = new AnalogFmSstvDecoder(SampleRate);
        double? ppm = null;
        // Sampled DURING the reception: the readout is null between images, so reading it after the
        // whole transmission has been pushed finds the tracker already gone. That exact mistake made
        // the AFC gate look like a decoder fault when it was a test fault.
        decoder.LineDecoded += _ => ppm = decoder.SlantPpm ?? ppm;
        decoder.PushSamples(samples);

        Assert.NotNull(ppm);

        var expected = ExpectedPpm(offsetHz);
        Console.WriteLine($"offset {offsetHz:F2}Hz -> expected ~{expected:F1} ppm, measured {ppm!.Value:F1} ppm");

        // Tolerance is deliberately loose. This gate exists to catch a tracker that stops tracking,
        // not to pin its precision: the assertion is that the measurement follows the injected error
        // in the right direction and rough magnitude.
        if (Math.Abs(offsetHz) < 0.01)
        {
            Assert.InRange(ppm.Value, -25.0, 25.0);
        }
        else
        {
            Assert.True(
                Math.Sign(ppm.Value) == Math.Sign(expected) || Math.Abs(ppm.Value) < 10.0,
                $"slant ppm {ppm.Value:F1} does not follow the sign of an injected {offsetHz:F2}Hz clock error");
            Assert.InRange(Math.Abs(ppm.Value), Math.Abs(expected) * 0.4, Math.Abs(expected) * 2.5);
        }
    }

    /// <summary>The gate's real purpose: a clock error must be TRACKED, not merely survived. Without
    /// this the bench cannot tell a working tracker from one that has silently stopped.</summary>
    [Fact]
    public async Task Slant_IsNotFlatAcrossDifferentClockErrors()
    {
        var mode = SstvModeRegistry.All.Single(m => m.Id == "martin-m1");
        var source = ImpairmentSweepHarness.CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var readings = new List<double>();
        foreach (var offsetHz in new[] { -1.0, 0.0, 1.0 })
        {
            var decoder = new AnalogFmSstvDecoder(SampleRate);
            double? ppm = null;
            decoder.LineDecoded += _ => ppm = decoder.SlantPpm ?? ppm;
            decoder.PushSamples(await EncodeAsync(mode, source, offsetHz));
            Assert.NotNull(ppm);
            readings.Add(ppm!.Value);
        }

        Console.WriteLine($"ppm across -1Hz / 0Hz / +1Hz: {string.Join(" / ", readings.Select(r => r.ToString("F1", CultureInfo.InvariantCulture)))}");

        // A tracker that has stopped reports the same value regardless of the error it is fed.
        Assert.True(
            readings.Max() - readings.Min() > 50.0,
            $"slant ppm barely moved across a 2Hz clock-error span: {string.Join(", ", readings.Select(r => r.ToString("F1", CultureInfo.InvariantCulture)))}");
    }

    private static async Task<float[]> EncodeAsync(SstvModeDefinition mode, IImageSource source, double offsetHz)
    {
        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var samples = new List<float>();
        await foreach (var s in encoder.EncodeAsync(mode, source, sampleRateOffsetHz: offsetHz))
        {
            samples.Add(s);
        }

        return samples.ToArray();
    }
}
