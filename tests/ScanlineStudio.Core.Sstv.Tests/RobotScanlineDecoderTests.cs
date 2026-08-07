using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Targeted regression test for Robot36's tone-selector ambiguity fallback (<c>Main.cpp:4289-4296</c>):
/// on a genuinely ambiguous reading, legacy toggles from the *previous* line's selection rather than
/// defaulting to one side. A synthetic round-trip can't exercise this (the encoder always transmits
/// a decisive tone), so this drives <see cref="RobotScanlineDecoder"/> directly with a scripted
/// frequency sequence instead.
/// </summary>
public class RobotScanlineDecoderTests
{
    [Fact]
    public void DecodeLine_LineZero_UndecodedChromaChannel_DefaultsToNeutralGray_NotSaturatedGreen()
    {
        // ultracode audit finding #27: hand-computed oracle from the audit itself. Legacy's
        // equivalent state (m_D36) is zero-initialized in an ALREADY zero-centered domain, so its
        // un-decoded chroma reads as neutral; this port's chroma domain is 128-centered (YCbCr.ToRgb
        // subtracts 128), so a 0.0 default (the pre-fix bug) is full-negative chroma, not neutral.
        // Line 0's tone selector decisively selects R-Y (even line) here, leaving B-Y at whatever its
        // default is -- exactly the condition this finding is about.
        var mode = SstvModeRegistry.Robot36;
        var decoder = new RobotScanlineDecoder();
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];

        const double yFreq = 1900; // Y=128 in the byte domain: (1900-1500)*256/800 = 128
        const double decisiveLowFreq = 1500; // deviation -400Hz, decisive -> selects R-Y (even line)
        const double rMinusYFreq = 1900; // R-Y decoded to 128 (neutral) too, so ONLY B-Y's default is under test

        var callIndex = 0;
        double Stub(int index)
        {
            callIndex++;
            return callIndex switch
            {
                <= 320 => yFreq, // Y scan
                321 => decisiveLowFreq, // tone selector
                _ => rMinusYFreq, // R-Y chroma scan
            };
        }

        var reader = new PixelSampleReader(Stub, ksbSamples: 1, lineEndSampleExclusive: int.MaxValue, luminanceMinHz: mode.LuminanceMinHz, neverPeakPicks: true);
        decoder.DecodeLine(mode, sampleRate: 44100, lineStartSample: 0, lineIndex: 0, reader, pixels);

        // Expected: Y=128 (neutral), R-Y=128 (neutral, just decoded), B-Y=128.0 (the FIXED default,
        // never decoded this line) -> YCbCr.ToRgb(128,128,128) should be neutral gray, matching
        // legacy's cold-start behavior, not the saturated (0,255,0)-style result a 0.0 default gives.
        var (expectedR, expectedG, expectedB) = YCbCr.ToRgb(128.0, 128.0, 128.0);
        var actualPixel = pixels[0];

        Assert.Equal(expectedR, actualPixel.R);
        Assert.Equal(expectedG, actualPixel.G);
        Assert.Equal(expectedB, actualPixel.B);
        // Belt-and-braces: fail loudly and specifically if the old 0.0 default ever comes back,
        // rather than only failing on a possibly-confusing generic mismatch.
        Assert.NotEqual((byte)0, actualPixel.G);
    }

    [Fact]
    public void DecodeLine_ToneSelectorThreshold_IsAsymmetricLikeLegacysTruncatedComparison()
    {
        // ultracode audit finding #30: legacy compares the TRUNCATED d = deviation*0.32 against +/-64,
        // not the raw deviation against a symmetric +/-200Hz. deviation=-200 gives d=-64 exactly --
        // NOT "< -64" -- so legacy calls this AMBIGUOUS (toggles), whereas a symmetric-200Hz check
        // (the pre-fix behavior) would have called it decisive. deviation=-203.13 gives d~=-65.0002,
        // truncating to -65, which IS "< -64" -- decisive.
        var mode = SstvModeRegistry.Robot36;

        // Decisive-low (deviation<=-203.13) must select R-Y == even line; ambiguous (deviation=-200,
        // and every value in (-200,0)) must toggle from whatever the decoder's own default/previous
        // state was (starts even/R-Y per Main.cpp:712's m_DSEL=0 default) -- so an ambiguous
        // first-ever call toggles to ODD (B-Y).
        var decisiveDecoder = new RobotScanlineDecoder();
        var decisivePixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        AssertToneSelection(decisiveDecoder, mode, decisivePixels, deviation: -203.13, expectEvenLineRMinusYSelected: true);

        var ambiguousDecoder = new RobotScanlineDecoder();
        var ambiguousPixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        AssertToneSelection(ambiguousDecoder, mode, ambiguousPixels, deviation: -200.0, expectEvenLineRMinusYSelected: false);
    }

    private static void AssertToneSelection(RobotScanlineDecoder decoder, SstvModeDefinition mode, Rgb24[] pixels, double deviation, bool expectEvenLineRMinusYSelected)
    {
        const double midpoint = 1900;
        const double chromaFreq = 2200; // distinctly non-neutral so R-Y vs B-Y selection is visible
        var callIndex = 0;
        double Stub(int index)
        {
            callIndex++;
            return callIndex switch
            {
                <= 320 => 1900.0, // Y scan
                321 => midpoint + deviation, // tone selector
                _ => chromaFreq, // chroma scan
            };
        }

        var reader = new PixelSampleReader(Stub, ksbSamples: 1, lineEndSampleExclusive: int.MaxValue, luminanceMinHz: mode.LuminanceMinHz, neverPeakPicks: true);
        decoder.DecodeLine(mode, sampleRate: 44100, lineStartSample: 0, lineIndex: 0, reader, pixels);

        double ToByteDomain(double freq) => (freq - mode.LuminanceMinHz) * 256.0 / (mode.LuminanceMaxHz - mode.LuminanceMinHz);
        var decodedChroma = ToByteDomain(chromaFreq);
        var (_, expectedG, expectedB) = expectEvenLineRMinusYSelected
            ? YCbCr.ToRgb(128.0, decodedChroma, 128.0) // R-Y decoded, B-Y still default
            : YCbCr.ToRgb(128.0, 128.0, decodedChroma); // B-Y decoded, R-Y still default

        Assert.Equal(expectedG, pixels[0].G);
        Assert.Equal(expectedB, pixels[0].B);
    }

    [Fact]
    public void DecodeLine_FractionalFrequency_TruncatesInTheZeroCenteredDomain_NotTheShiftedOne()
    {
        // ultracode audit finding #28: legacy truncates Y/R-Y/B-Y in the RAW zero-centered domain
        // (GetPixelLevel's own `int d`), BEFORE the +128 bias -- not after. This is a real,
        // demonstrable difference, not just a domain-purity nitpick: freq=1751 gives raw value
        // 80.32 (=(1751-1500)*256/800). Truncating the CORRECT way: Truncate(80.32-128)+128 =
        // Truncate(-47.68)+128 = -47+128 = 81. Truncating the WRONG way (the audit's own first-pass
        // mistake, Truncate(value) directly): Truncate(80.32) = 80 -- one level off, in a
        // content-dependent direction, not a fixed bias.
        var mode = SstvModeRegistry.Robot36;
        var decoder = new RobotScanlineDecoder();
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        const double fractionalFreq = 1751.0;
        var reader = new PixelSampleReader(_ => fractionalFreq, ksbSamples: 1, lineEndSampleExclusive: int.MaxValue, luminanceMinHz: mode.LuminanceMinHz, neverPeakPicks: true);

        decoder.DecodeLine(mode, sampleRate: 44100, lineStartSample: 0, lineIndex: 0, reader, pixels);

        // Tone-selector deviation here is 1751-1900=-149Hz -> d=-149*0.32=-47.68 -> truncates to -47,
        // which is neither >=64 nor <-64 -> ambiguous -> toggles from the default (even/R-Y) to
        // odd/B-Y. So R-Y stays at its #27 neutral default (128); B-Y gets the fractional value.
        var (expectedR, expectedG, expectedB) = YCbCr.ToRgb(81.0, 128.0, 81.0);
        var actualPixel = pixels[0];

        Assert.Equal(expectedR, actualPixel.R);
        Assert.Equal(expectedG, actualPixel.G);
        Assert.Equal(expectedB, actualPixel.B);

        // Negative check: reject the audit's own first-pass mistake (truncating the already-shifted
        // value directly) landing back in by accident.
        var (wrongR, wrongG, wrongB) = YCbCr.ToRgb(80.0, 128.0, 80.0);
        Assert.False(actualPixel.R == wrongR && actualPixel.G == wrongG && actualPixel.B == wrongB,
            "Decoded pixel matches the WRONG-domain truncation (80), not the correct one (81) -- truncation regressed to truncating the already-128-shifted value.");
    }

    [Fact]
    public void DecodeLine_AmbiguousToneReading_TogglesFromPreviousSelection()
    {
        var mode = SstvModeRegistry.Robot36;
        var decoder = new RobotScanlineDecoder();
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];

        const double yFreq = 1900;
        const double decisiveHighFreq = 2300; // deviation +400Hz >= 200Hz threshold -> decisive B-Y (Main.cpp:4291)
        const double ambiguousFreq = 1900; // deviation 0Hz, inside the +/-200Hz ambiguous band -> toggle (Main.cpp:4294)
        const double chromaFreqLine0 = 2000;
        const double chromaFreqLine1 = 1800;

        // RobotScanlineDecoder calls the reader in a fixed, fully sequential order: 320 Y-scan calls
        // (ReadBare, unaffected by piece 10 -- Robot's luma DOES peak-pick in legacy, but this test
        // only exercises the tone-selector's ambiguity fallback, so a plain call-count stub is still
        // valid: ReadBare and ReadPeakPicked both resolve to the same underlying source here since
        // ksbSamples is irrelevant to a source that returns a fixed value per call index, not per
        // sample index), then exactly 1 tone-selector call, then 320 chroma-scan calls, per line.
        // Scripting by call index (rather than by sample window) avoids needing to replicate the
        // decoder's own sample-accounting internally just to drive this test.
        var callIndex = 0;
        double AverageFrequencyInWindow(int index)
        {
            callIndex++;
            return callIndex switch
            {
                <= 320 => yFreq, // line 0 Y scan
                321 => decisiveHighFreq, // line 0 tone selector: decisive -> selects B-Y
                <= 641 => chromaFreqLine0, // line 0 chroma scan (written to B-Y)
                <= 961 => yFreq, // line 1 Y scan
                962 => ambiguousFreq, // line 1 tone selector: ambiguous -> must toggle to R-Y
                _ => chromaFreqLine1, // line 1 chroma scan (should be written to R-Y after the toggle)
            };
        }

        // neverPeakPicks: true -- this test only exercises the tone-selector/chroma ambiguity logic
        // (both already-bare reads, unaffected by piece 10), and forcing ReadPeakPicked to degrade to
        // ReadBare keeps this call-count-based stub valid without needing to also fake a peak-pick
        // comparison across two different call indices.
        var reader = new PixelSampleReader(AverageFrequencyInWindow, ksbSamples: 1, lineEndSampleExclusive: int.MaxValue, luminanceMinHz: mode.LuminanceMinHz, neverPeakPicks: true);

        decoder.DecodeLine(mode, sampleRate: 44100, lineStartSample: 0, lineIndex: 0, reader, pixels);
        decoder.DecodeLine(mode, sampleRate: 44100, lineStartSample: 0, lineIndex: 1, reader, pixels);

        double ToByteDomain(double freq) => (freq - mode.LuminanceMinHz) * 256.0 / (mode.LuminanceMaxHz - mode.LuminanceMinHz);

        // Expected line-1 pixel: Y from yFreq, R-Y from line 1's chroma reading (toggle picked R-Y),
        // B-Y still line 0's value (Robot's cross-line chroma persistence -- the *other* channel is
        // never re-read on a line that doesn't carry it).
        var (expectedR, expectedG, expectedB) = YCbCr.ToRgb(
            ToByteDomain(yFreq),
            ToByteDomain(chromaFreqLine1),
            ToByteDomain(chromaFreqLine0));

        var actualPixel = pixels[mode.ImageWidth]; // line 1, x=0 (all x identical: the stub ignores the sample window)
        Assert.Equal(expectedR, actualPixel.R);
        Assert.Equal(expectedG, actualPixel.G);
        Assert.Equal(expectedB, actualPixel.B);
    }
}
