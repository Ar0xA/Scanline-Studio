using Yoniq.Abstractions.Imaging;

namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Milestone-audit SHOULD item 11 (spec/14-roadmap.md): legacy clamps luma to [0,255] (`Limit256`)
/// before `YCtoRGB` at every RX site, but does NOT clamp chroma there -- confirmed directly against
/// source for each family below. <see cref="RgbSequentialScanlineDecoder"/> and
/// <see cref="MonoAveragedPairedScanlineDecoder"/> already matched this (every channel in those two
/// families IS luma-like/`Limit256`'d in legacy); <see cref="RobotScanlineDecoder"/>,
/// <see cref="YCbCrSequentialScanlineDecoder"/>, and <see cref="YCbCrLinePairedScanlineDecoder"/> did
/// not clamp anything at all, only bites on out-of-band/overdriven input no existing fixture exercises
/// (real transmissions never carry a frequency outside the nominal band), so this file drives each
/// decoder directly with a scripted out-of-band frequency rather than relying on a round-trip.
///
/// <see cref="YCbCrLinePairedScanlineDecoder"/> has a genuine, surprising legacy ASYMMETRY worth its
/// own test: Y1 is `Limit256`'d (`Main.cpp:4387`) but Y2 is NOT (`Main.cpp:4420-4422` feeds
/// `GetPictureLevel`'s output straight into `YCtoRGB` with no `Limit256` call at all) -- not a port gap
/// to close uniformly, a real legacy quirk to preserve.
/// </summary>
public class Limit256ClampTests
{
    // (2300-1500)/2 + 1500 = 1900 is the band midpoint; 2700 is 400Hz past LuminanceMaxHz (2300),
    // giving a raw pre-clamp value of (2700-1500)*256/800 = 384 -- comfortably past 255, and (for
    // Robot36's tone-selector) 800Hz past the 1900Hz midpoint, decisively selecting one chroma channel
    // deterministically (isEvenLine=false, Main.cpp:4291's `d>=0` branch) so this single constant
    // stub frequency drives every read in the line without needing to script per-call-index behavior.
    private const double OutOfBandFrequencyHz = 2700;
    private const double ExpectedRawValue = 384; // (2700-1500)*256/800, unclamped

    [Fact]
    public void RobotScanlineDecoder_LumaClampsButChromaDoesNot()
    {
        var mode = SstvModeRegistry.Robot36;
        var decoder = new RobotScanlineDecoder();
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        var reader = new PixelSampleReader(_ => OutOfBandFrequencyHz, ksbSamples: 1, lineEndSampleExclusive: int.MaxValue, luminanceMinHz: mode.LuminanceMinHz, neverPeakPicks: true);

        decoder.DecodeLine(mode, sampleRate: 44100, lineStartSample: 0, lineIndex: 0, reader, pixels);

        // Deviation from the tone-selector's own 1900Hz midpoint is +800Hz (decisive, >= 200Hz
        // threshold) -> Main.cpp:4291's `d>=0` branch -> B-Y selected, R-Y stays at its unwritten
        // default (0.0).
        var (expectedR, expectedG, expectedB) = YCbCr.ToRgb(255, 0, ExpectedRawValue);
        var actualPixel = pixels[0];
        Assert.Equal(expectedR, actualPixel.R);
        Assert.Equal(expectedG, actualPixel.G);
        Assert.Equal(expectedB, actualPixel.B);
    }

    [Fact]
    public void YCbCrSequentialScanlineDecoder_LumaClampsButChromaDoesNot()
    {
        var mode = SstvModeRegistry.Robot72;
        var decoder = new YCbCrSequentialScanlineDecoder();
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        var reader = new PixelSampleReader(_ => OutOfBandFrequencyHz, ksbSamples: 1, lineEndSampleExclusive: int.MaxValue, luminanceMinHz: mode.LuminanceMinHz, neverPeakPicks: true);

        decoder.DecodeLine(mode, sampleRate: 44100, lineStartSample: 0, lineIndex: 0, reader, pixels);

        var (expectedR, expectedG, expectedB) = YCbCr.ToRgb(255, ExpectedRawValue, ExpectedRawValue);
        var actualPixel = pixels[0];
        Assert.Equal(expectedR, actualPixel.R);
        Assert.Equal(expectedG, actualPixel.G);
        Assert.Equal(expectedB, actualPixel.B);
    }

    [Fact]
    public void YCbCrLinePairedScanlineDecoder_Y1ClampsButY2AndChromaDoNot()
    {
        var mode = SstvModeRegistry.Pd90;
        var decoder = new YCbCrLinePairedScanlineDecoder();
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight * 2];
        var reader = new PixelSampleReader(_ => OutOfBandFrequencyHz, ksbSamples: 1, lineEndSampleExclusive: int.MaxValue, luminanceMinHz: mode.LuminanceMinHz, neverPeakPicks: true);

        decoder.DecodeLine(mode, sampleRate: 44100, lineStartSample: 0, lineIndex: 0, reader, pixels);

        // Y1 (odd/first output row) clamps to 255; Y2 (even/second output row, real legacy asymmetry)
        // does NOT clamp, matching chroma's own unclamped 384.
        var (expectedR1, expectedG1, expectedB1) = YCbCr.ToRgb(255, ExpectedRawValue, ExpectedRawValue);
        var (expectedR2, expectedG2, expectedB2) = YCbCr.ToRgb(ExpectedRawValue, ExpectedRawValue, ExpectedRawValue);

        var actualY1Pixel = pixels[0];
        Assert.Equal(expectedR1, actualY1Pixel.R);
        Assert.Equal(expectedG1, actualY1Pixel.G);
        Assert.Equal(expectedB1, actualY1Pixel.B);

        var actualY2Pixel = pixels[mode.ImageWidth];
        Assert.Equal(expectedR2, actualY2Pixel.R);
        Assert.Equal(expectedG2, actualY2Pixel.G);
        Assert.Equal(expectedB2, actualY2Pixel.B);
    }
}
