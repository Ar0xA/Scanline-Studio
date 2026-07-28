using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;
using Yoniq.Core.Imaging;

namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// End-to-end proof that <c>m_sint2</c> (ported as <see cref="SyncIntervalTracker"/>, wired into
/// <see cref="AnalogFmSstvDecoder"/> via its <c>TrySyncIntervalDetection</c> method) actually
/// recognizes a transmission with no VIS header at all -- the only way to meaningfully exercise
/// this path, since every round-trip test in <see cref="SstvRoundTripTests"/> always includes a
/// valid VIS header, through which VIS decode resolves the mode long before sync-interval
/// matching could ever accumulate enough consecutive peaks (see the ordering note on
/// <c>AnalogFmSstvDecoder.TryDecodeHeader</c>). Only the four modes legacy itself trusts for
/// VIS-bypass switching are covered here (<c>AnalogFmSstvDecoder.SyncBypassTrustedModes</c>,
/// `sstv.cpp:1912-1922`) -- every other mode is deliberately unrecognizable via this path,
/// matching legacy exactly, which is separately covered by
/// <see cref="SyncIntervalTrackerTests.Sc260AndSc2120_NeverMatchEvenWithPerfectInterval"/> and
/// <see cref="SyncIntervalTrackerTests.NarrowTracker_OnlyMatchesNarrowFamilyModes"/> at the
/// isolated-unit-test level.
/// </summary>
public class SyncBypassDetectionTests
{
    public static readonly TheoryData<SstvModeDefinition> TrustedModes = new()
    {
        SstvModeRegistry.ScottieS1,
        SstvModeRegistry.MartinM1,
        SstvModeRegistry.MartinM2,
        SstvModeRegistry.Sc2180,
    };

    [Theory]
    [MemberData(nameof(TrustedModes))]
    public async Task HeaderlessTransmission_IsRecognizedAndDecodedViaSyncIntervalBypass(SstvModeDefinition mode)
    {
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(44100);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        // Strip exactly the VIS header this mode's own encoder prepends -- the same duration
        // TryDecodeVisHeader itself computes (VisHeader.PrefixDurationMs + NormalTailDurationMs,
        // plus Scottie's extra 9ms/1200Hz post-VIS pulse) -- leaving only the raw, periodic
        // sync+image-line data a real headerless transmission (or one with an unrecognized/
        // corrupted VIS code) would present to the decoder.
        var headerDurationMs = VisHeader.PrefixDurationMs + VisHeader.NormalTailDurationMs
            + (SstvModeRegistry.IsScottieFamily(mode) ? VisHeader.ScottiePostVisPulseDurationMs : 0.0);
        var headerSampleCount = (int)Math.Round(headerDurationMs / 1000.0 * encoder.SampleRate);
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

        // Looser than SstvRoundTripTests' 10.0: this path's line-start anchor is derived from an
        // observed, filter-smoothed sync-envelope peak plus a documented midpoint approximation
        // (SstvModeRegistry.GetSyncSegmentMidpointOffsetMs), not the VIS path's exact,
        // deterministic header-boundary arithmetic. Mode *identification* above is exact for all
        // four modes (that's the actual behavior being ported/verified here) -- this tolerance only
        // covers the secondary, expected cost of the approximate anchor: SyncEnvelopeDetector's
        // 50Hz lowpass smoother has real group delay, which biases the observed peak position later
        // than the sync segment's true midpoint by a fixed, per-mode amount. Legacy doesn't have
        // this problem because it never anchors pixel decode off this peak position at all -- once
        // m_sint2 flips m_Sync=1, a separate, deeper real-time bootstrap (CSSTVDEM::Start's m_wBgn
        // staged buffer search, sstv.cpp:1717-1744, not ported here, see
        // GetSyncSegmentMidpointOffsetMs's doc comment) finds the actual fine pixel alignment from
        // scratch. Measured average deltas with the current approximation: Scottie S1 18.06,
        // Martin M1 12.62, Martin M2 20.78, SC2-180 12.91 -- real, bounded, and reproducible, not
        // random noise (rerunning does not change them). 25.0 gives headroom above the worst of
        // these without masking an actual regression back toward "wrong mode" territory.
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
