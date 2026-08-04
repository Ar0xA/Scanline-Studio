using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;
using Yoniq.Core.Imaging;

namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// End-to-end proof that <c>m_sint3</c> (ported as a second <see cref="SyncIntervalTracker"/>
/// instance, wired into <see cref="AnalogFmSstvDecoder"/>'s <c>TrySyncIntervalDetectionStep</c> loop
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

        // Round-1-review finding (auditor, SHOULD item 5's own review): missing
        // VisHeader.OutHeadNarrowDurationMs -- see SyncBypassDetectionTests' own identical fix
        // comment for the full explanation (narrow variant here, not normal, matching this family's
        // own OutHEAD tone sequence).
        var headerDurationMs = VisHeader.OutHeadNarrowDurationMs + VisHeader.NarrowHeaderTotalDurationMs;
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

        // Same reasoning as SyncBypassDetectionTests: the line-start anchor here is an observed,
        // filter-smoothed sync-envelope peak plus the same documented midpoint approximation, not
        // VIS decode's exact deterministic boundary. Mode identification is the behavior actually
        // being proven; pixel alignment is a known, bounded, secondary cost of the approximation.
        //
        // Round-2-review correction (auditor, SHOULD item 5's own review): the previously-documented
        // deltas (MN73 24.27, MN110 21.17, MN140 19.62, MC110 16.38, MC140 14.29, MC180 11.42) and
        // the "same 25.0 tolerance as m_sint2's own trusted-mode set" cross-reference both predate
        // the OutHEAD header-strip fix (see SyncBypassDetectionTests.cs's own identical fix comment)
        // -- that sibling tolerance is now 14.0, not 25.0, so the cross-reference was already stale
        // on its own terms. Pre-fix, this file's own strip (950ms against a real 1350ms header) left
        // ~400ms of leftover FSK-packet tail as junk at buffer start rather than a clean VIS/narrow
        // lock -- a different contamination shape than the normal-mode files' "locked via real VIS"
        // bug, but real: the FSK decoder could not have completed a packet from a body starting
        // ~128ms into the data bits. Freshly re-measured post-fix, with the header-strip offset now
        // correct: MN73 13.76, MN110 13.20, MN140 13.04, MC110 9.19, MC140 8.84, MC180 8.29 -- all
        // smaller than the old (contaminated) values, same direction as every other fixed file in
        // this fix's own batch. 18.0 gives real headroom above the new worst case (13.76).
        AssertImagesMatchWithinTolerance(sourceImage, decodedImage!, maxAveragePerChannelDelta: 18.0);
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
