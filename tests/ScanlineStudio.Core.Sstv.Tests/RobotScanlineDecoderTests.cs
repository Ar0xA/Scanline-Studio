using ScanlineStudio.Abstractions.Imaging;

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
