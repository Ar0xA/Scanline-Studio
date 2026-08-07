using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Targeted tests for the post-image footer tone (<c>Main.cpp:6994-7013</c>'s "no FSK ID configured"
/// branch — see <see cref="AnalogFmSstvEncoder.GenerateFooterSegments"/>'s doc comment for what's
/// deliberately deferred). Checked directly against the segment sequence rather than only through a
/// full round-trip, since the decoder never needs to interpret the footer (it stops once it has
/// <c>mode.ImageHeight</c> lines) — a round-trip test alone couldn't distinguish "footer emitted
/// correctly" from "footer missing entirely."
/// </summary>
public class AnalogFmSstvEncoderFooterTests
{
    [Fact]
    public void GenerateFooterSegments_NormalMode_EmitsTrailingCarrierThenAlternatingTones()
    {
        // Main.cpp:6999-7005 (!VOX && !fTxNarrow): WriteC(1500, min(m_TW, SampFreq/2)), then
        // Write(1900,100), Write(1500,100), Write(1900,100), Write(1500,100).
        var mode = SstvModeRegistry.Robot36; // LineDurationMs = 150, well under the 500ms cap
        var segments = AnalogFmSstvEncoder.GenerateFooterSegments(mode).ToList();

        Assert.Equal(5, segments.Count);
        Assert.Equal((1500.0, mode.LineDurationMs), segments[0]);
        Assert.Equal((1900.0, 100.0), segments[1]);
        Assert.Equal((1500.0, 100.0), segments[2]);
        Assert.Equal((1900.0, 100.0), segments[3]);
        Assert.Equal((1500.0, 100.0), segments[4]);
    }

    [Fact]
    public void GenerateFooterSegments_NormalMode_CapsTrailingCarrierAt500Ms()
    {
        // Main.cpp:6998/7000: SSTVSET.m_TW > tw ? tw : SSTVSET.m_TW, tw = SampFreq/2 samples = 500ms.
        var mode = SstvModeRegistry.ScottieDx; // LineDurationMs = 1050.3, well over the cap
        var segments = AnalogFmSstvEncoder.GenerateFooterSegments(mode).ToList();

        Assert.Equal((1500.0, 500.0), segments[0]);
    }

    [Fact]
    public void GenerateFooterSegments_NarrowMode_EmitsSingleTrailingCarrierOnly()
    {
        // Main.cpp:7006-7008 (fTxNarrow): WriteC(1900, min(m_TW, SampFreq/2)), nothing else.
        var mode = SstvModeRegistry.Mn73; // LineDurationMs = 570, over the cap
        var segments = AnalogFmSstvEncoder.GenerateFooterSegments(mode).ToList();

        Assert.Equal([(1900.0, 500.0)], segments);
    }

    [Fact]
    public async Task EncodeAsync_TotalSampleCount_FloorsTheIdealTotal_NotRoundToNearest()
    {
        // ultracode audit finding #25: legacy's CSSTVMOD::Do floors its running ideal-sample-position
        // accumulator (`int(m_dPos)`), not rounds it. The running-accumulator design makes every
        // INTERMEDIATE segment boundary's rounding choice cancel out in the grand total (a telescoping
        // sum: total emitted = floor_or_round(ideal total duration in samples), using only the LAST
        // segment's own rounding) -- so this test isolates whether that final rounding rule is floor,
        // by searching for a sample rate where the mode's own fixed (rate-independent) total duration
        // lands the total-sample-count arithmetic near a half-sample boundary, where floor and
        // round(ToEven) disagree. A minimal 1x1-pixel custom mode keeps total duration short enough
        // (dominated by its own tiny VIS header + one pixel + footer) that a high-precision reference
        // measurement stays fast.
        var mode = new SstvModeDefinition(
            Id: "test-floor",
            DisplayName: "Test",
            VisCode: SstvModeRegistry.Robot36.VisCode,
            ImageWidth: 1,
            ImageHeight: 1,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment(ChannelName: "R", DurationMs: 20.0)]);
        var image = new ArrayImageSource(1, 1, [new Rgb24(128, 0, 0)]);

        // Measure the mode's total ideal duration precisely (floor/round differ by at most 1 sample
        // out of ~1e7 here, negligible for finding a good candidate rate below).
        const int referenceRate = 10_000_000;
        var referenceEncoder = new AnalogFmSstvEncoder(referenceRate);
        var referenceSampleCount = 0L;
        await foreach (var _ in referenceEncoder.EncodeAsync(mode, image))
        {
            referenceSampleCount++;
        }

        var totalDurationMs = referenceSampleCount / (double)referenceRate * 1000.0;

        var found = false;
        for (var candidateRate = 11000; candidateRate < 11200 && !found; candidateRate++)
        {
            var idealTotalSamples = totalDurationMs / 1000.0 * candidateRate;
            var fractional = idealTotalSamples - Math.Floor(idealTotalSamples);
            if (Math.Abs(fractional - 0.5) > 0.05)
            {
                continue; // not close enough to a discriminating half-sample boundary
            }

            var expectedFloor = (long)idealTotalSamples;
            var expectedRound = (long)Math.Round(idealTotalSamples);
            if (expectedFloor == expectedRound)
            {
                continue; // ToEven happened to floor here too -- keep searching for one that doesn't
            }

            var encoder = new AnalogFmSstvEncoder(candidateRate);
            var actualSampleCount = 0L;
            await foreach (var _ in encoder.EncodeAsync(mode, image))
            {
                actualSampleCount++;
            }

            Assert.Equal(expectedFloor, actualSampleCount);
            Assert.NotEqual(expectedRound, actualSampleCount);
            found = true;
        }

        Assert.True(found, "Search never found a sample rate where floor and round(ToEven) disagree for this mode's total duration -- test cannot discriminate as written.");
    }
}
