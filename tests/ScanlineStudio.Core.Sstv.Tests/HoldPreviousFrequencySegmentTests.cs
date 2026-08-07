using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Targeted tests for <see cref="HoldPreviousFrequencySegment"/> (ultracode audit finding #24):
/// legacy's MR/ML inter-channel gaps (<c>TMmsstv::LineMR</c>, `Main.cpp:6766-6782`) hold the LAST
/// TRANSMITTED PIXEL'S FREQUENCY, not a fixed tone. <see cref="YCbCrSequentialScanlineEncoder"/> is
/// the only encoder any registered mode actually routes this segment kind through today (MR/ML);
/// the other three generic encoders' support is defensive-only (no mode reaches it), covered here
/// too so a future mode reusing this segment kind doesn't silently regress to a duration-only skip.
/// </summary>
public class HoldPreviousFrequencySegmentTests
{
    [Fact]
    public void YCbCrSequentialScanlineEncoder_HoldSegment_EmitsThePreviousScanPixelsFrequency_NotAFixedTone()
    {
        var mode = SstvModeRegistry.Mr73;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        // Distinct, non-1900 R,G,B so each channel's last-pixel frequency is unambiguous.
        Array.Fill(pixels, new Rgb24(40, 90, 200));
        var image = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        var encoder = new YCbCrSequentialScanlineEncoder();
        var segments = encoder.GenerateLine(mode, image, lineIndex: 0).ToList();

        // Mr73's LineSegments: sync, porch, Y-scan(320px), hold, RY-scan(320px), hold, BY-scan(320px), hold.
        // Segment list after expansion: [sync, porch, <320 Y samples>, hold, <320 RY samples>, hold, <320 BY samples>, hold].
        var yLastIndex = 2 + mode.ImageWidth - 1;
        var holdAfterY = yLastIndex + 1;
        var ryLastIndex = holdAfterY + mode.ImageWidth;
        var holdAfterRy = ryLastIndex + 1;
        var byLastIndex = holdAfterRy + mode.ImageWidth;
        var holdAfterBy = byLastIndex + 1;

        Assert.Equal(segments[yLastIndex].FrequencyHz, segments[holdAfterY].FrequencyHz);
        Assert.Equal(segments[ryLastIndex].FrequencyHz, segments[holdAfterRy].FrequencyHz);
        Assert.Equal(segments[byLastIndex].FrequencyHz, segments[holdAfterBy].FrequencyHz);

        // Negative check: reject the pre-fix fixed-1900Hz behavior. A uniform (40,90,200) image
        // gives a real Y/R-Y/B-Y that isn't exactly 1900Hz (verified: none of these three channel
        // values map to the 1900Hz free-running center for this non-gray color).
        Assert.NotEqual(1900.0, segments[holdAfterY].FrequencyHz, tolerance: 0.01);
        Assert.NotEqual(1900.0, segments[holdAfterRy].FrequencyHz, tolerance: 0.01);
        Assert.NotEqual(1900.0, segments[holdAfterBy].FrequencyHz, tolerance: 0.01);

        // Each hold's own duration (0.1ms) must be preserved -- only the frequency changes.
        Assert.Equal(0.1, segments[holdAfterY].DurationMs, tolerance: 0.0001);
        Assert.Equal(0.1, segments[holdAfterRy].DurationMs, tolerance: 0.0001);
        Assert.Equal(0.1, segments[holdAfterBy].DurationMs, tolerance: 0.0001);
    }

    [Fact]
    public void RgbSequentialScanlineEncoder_HoldSegment_EmitsThePreviousFrequency_DefensiveCoverage()
    {
        // No registered mode reaches this today -- a synthetic mode with an unused hold segment,
        // covering the defensive case per finding #24's plan.
        var mode = new SstvModeDefinition(
            Id: "test-hold",
            DisplayName: "Test",
            VisCode: 0,
            ImageWidth: 1,
            ImageHeight: 1,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments:
            [
                new ScanSegment(ChannelName: "R", DurationMs: 10.0),
                new HoldPreviousFrequencySegment(DurationMs: 0.1),
            ]);
        var pixels = new[] { new Rgb24(64, 0, 0) };
        var image = new ArrayImageSource(1, 1, pixels);

        var encoder = new RgbSequentialScanlineEncoder();
        var segments = encoder.GenerateLine(mode, image, lineIndex: 0).ToList();

        Assert.Equal(2, segments.Count);
        Assert.Equal(segments[0].FrequencyHz, segments[1].FrequencyHz);
        Assert.Equal(0.1, segments[1].DurationMs, tolerance: 0.0001);
    }
}
