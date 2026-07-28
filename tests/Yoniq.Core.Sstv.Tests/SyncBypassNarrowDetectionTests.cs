using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;
using Yoniq.Core.Imaging;

namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// End-to-end proof that <c>m_sint3</c> (ported as a second <see cref="SyncIntervalTracker"/>
/// instance, wired into <see cref="AnalogFmSstvDecoder"/>'s <c>TrySyncIntervalDetection</c> loop
/// alongside <c>m_sint2</c>) recognizes a narrow-family (MN/MC) transmission with no header at all.
/// Mirrors <see cref="SyncBypassDetectionTests"/> exactly, except MN/MC modes never have a VIS
/// header to strip in the first place -- they use the distinct FSK mode-announce packet
/// (<see cref="VisHeader.GenerateNarrowModeSegments"/>), so the full packet duration
/// (<see cref="VisHeader.NarrowHeaderTotalDurationMs"/>) is stripped instead. Unlike
/// <see cref="AnalogFmSstvDecoder.SyncBypassTrustedModes"/>'s explicit four-mode allowlist for
/// m_sint2, all 6 real narrow modes are expected to match here -- legacy's own m_sint3 branch
/// (sstv.cpp:1937-1941) has no further switch/case filter beyond SyncCheckSub's own m_fNarrow
/// gating, which <see cref="SyncIntervalTrackerTests.NarrowTracker_OnlyMatchesNarrowFamilyModes"/>
/// already covers at the isolated-unit-test level.
/// </summary>
public class SyncBypassNarrowDetectionTests
{
    public static readonly TheoryData<SstvModeDefinition> NarrowModes = new()
    {
        SstvModeRegistry.Mn73,
        SstvModeRegistry.Mn110,
        SstvModeRegistry.Mn140,
        SstvModeRegistry.Mc110,
        SstvModeRegistry.Mc140,
        SstvModeRegistry.Mc180,
    };

    [Theory]
    [MemberData(nameof(NarrowModes))]
    public async Task HeaderlessNarrowTransmission_IsRecognizedAndDecodedViaSyncIntervalBypass(SstvModeDefinition mode)
    {
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(44100);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        var headerSampleCount = (int)Math.Round(VisHeader.NarrowHeaderTotalDurationMs / 1000.0 * encoder.SampleRate);
        var bodySamples = samples.Skip(headerSampleCount).ToArray();

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        SstvModeDefinition? detectedMode = null;
        IImageSource? decodedImage = null;
        decoder.ModeDetected += m => detectedMode = m;
        decoder.LineDecoded += update => decodedImage = update.Image;

        decoder.PushSamples(bodySamples);

        Assert.NotNull(detectedMode);
        Assert.Equal(mode.Id, detectedMode!.Id);
        Assert.NotNull(decodedImage);

        // Same reasoning and same order-of-magnitude tolerance as SyncBypassDetectionTests: the
        // line-start anchor here is an observed, filter-smoothed sync-envelope peak plus the same
        // documented midpoint approximation, not VIS decode's exact deterministic boundary. Mode
        // identification is the behavior actually being proven; pixel alignment is a known,
        // bounded, secondary cost of the approximation. Measured average deltas: MN73 24.27,
        // MN110 21.17, MN140 19.62, MC110 16.38, MC140 14.29, MC180 11.42 -- real, bounded, and
        // reproducible (rerunning does not change them), same 25.0 tolerance as m_sint2's own
        // trusted-mode set gives comfortable headroom above the worst of these.
        AssertImagesMatchWithinTolerance(sourceImage, decodedImage!, maxAveragePerChannelDelta: 25.0);
    }

    private static ArrayImageSource CreateGradientTestImage(int width, int height)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = new Rgb24(
                    R: (byte)(x * 255 / Math.Max(1, width - 1)),
                    G: (byte)(y * 255 / Math.Max(1, height - 1)),
                    B: 128);
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }

    private static void AssertImagesMatchWithinTolerance(IImageSource expected, IImageSource actual, double maxAveragePerChannelDelta)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);

        double totalDelta = 0;
        var sampleCount = 0;

        for (var y = 0; y < expected.Height; y++)
        {
            var expectedLine = expected.GetScanline(y);
            var actualLine = actual.GetScanline(y);

            for (var x = 0; x < expected.Width; x++)
            {
                totalDelta += Math.Abs(expectedLine[x].R - actualLine[x].R);
                totalDelta += Math.Abs(expectedLine[x].G - actualLine[x].G);
                totalDelta += Math.Abs(expectedLine[x].B - actualLine[x].B);
                sampleCount += 3;
            }
        }

        var averageDelta = totalDelta / sampleCount;
        Assert.True(averageDelta <= maxAveragePerChannelDelta, $"Average per-channel delta {averageDelta:F2} exceeded tolerance {maxAveragePerChannelDelta}.");
    }
}
