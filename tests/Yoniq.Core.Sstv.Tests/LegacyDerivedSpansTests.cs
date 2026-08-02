using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;
using Yoniq.Core.Imaging;

namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Pins legacy-derived spans/behaviors that recent Band-2 fixes rely on, flagged by an auditor
/// code-level review as a systemic gap: several recent items (Band-1 4b's lock-anchor gate, Band-2
/// S5's detector persistence, Band-2 S16's AVT PLL warm-up span) each changed a real behavior that
/// stayed invisible to the existing test suite -- decode OUTCOMES stayed identical (correctly), but
/// nothing pinned the underlying MECHANISM, so a regression back to the old (wrong) mechanism would
/// have silently passed every existing test. One test per still-uncovered item (4b's own gate is
/// already covered by <c>BandpassCacheChunkInvarianceTests.FirstLockedFilterSample_EqualsTheLockAnchor</c>,
/// not duplicated here).
/// </summary>
public class LegacyDerivedSpansTests
{
    [Fact]
    public async Task AvtPllWarmupSpan_MatchesLegacyDerivedDuration()
    {
        // Band-2 item S16: _avtPllWarmupStartSample is derived from legacy's real case-3/4 handoff
        // (sstv.cpp:2127-2144), not the old arbitrary AnchorWarmupSamples constant. The gap between it
        // and _avtTrainingOriginSample is a PURE CONSTANT, independent of where in the stream the
        // header actually starts (headerStart cancels out) -- exactly the property a regression back
        // to the old clamped-2000-sample window would violate.
        var mode = SstvModeRegistry.Avt;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(180, 90, 40));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        var encoder = new AnalogFmSstvEncoder(44100);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        SstvModeDefinition? detectedMode = null;
        decoder.ModeDetected += m => detectedMode = m;

        decoder.PushSamples(samples.ToArray());

        Assert.NotNull(detectedMode);
        Assert.Equal(mode.Id, detectedMode!.Id);

        // Same two independent Math.Round calls the source performs (case-4 entry's own 2-VIS-block
        // skip, plus case-3's own 30ms), not a single combined rounding -- matching AnalogFmSstvDecoder's
        // exact arithmetic so this can't drift by a rounding artifact unrelated to the real derivation.
        var expectedGap = (int)Math.Round(2 * VisHeader.AvtVisBlockDurationMs / 1000.0 * encoder.SampleRate)
            + (int)Math.Round(VisHeader.BitDurationMs / 1000.0 * encoder.SampleRate);
        var actualGap = decoder.AvtTrainingOriginSample - decoder.AvtPllWarmupStartSample;

        Assert.Equal(expectedGap, actualGap);
    }

    [Fact]
    public async Task VisDataD11Cursor_NeverResets_AcrossBackToBackTransmissions()
    {
        // Band-2 item S5: D11At's cursor (_visDataD11ProcessedUpTo) is a persistent, decoder-lifetime
        // field, deliberately NOT reset in EndOfImage (matching legacy's own never-reset m_iir11
        // lifecycle -- see D11At's own doc comment). A regression back to a fresh-per-call detector
        // wouldn't have a shared cursor to observe at all; this test instead pins the structural
        // guarantee that actually matters: the cursor strictly advances across a full image boundary,
        // never resets to 0 or to the second transmission's own headerStart.
        var mode = SstvModeRegistry.MartinM1;
        var sourceImage1 = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight, offset: 0);
        var sourceImage2 = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight, offset: 64);

        var encoder = new AnalogFmSstvEncoder(11025);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage1))
        {
            samples.Add(sample);
        }

        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage2))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        var cursorAtEachLock = new List<int>();
        decoder.ModeDetected += _ => cursorAtEachLock.Add(decoder.VisDataD11ProcessedUpTo);

        decoder.PushSamples(samples.ToArray());

        Assert.Equal(2, cursorAtEachLock.Count);
        Assert.True(
            cursorAtEachLock[1] > cursorAtEachLock[0],
            $"Expected the D11 cursor to keep advancing across the image boundary (never reset), " +
            $"but it was {cursorAtEachLock[0]} at the first lock and {cursorAtEachLock[1]} at the second.");
    }

    private static ArrayImageSource CreateGradientTestImage(int width, int height, int offset)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = new Rgb24(
                    R: (byte)((x * 255 / Math.Max(1, width - 1) + offset) % 256),
                    G: (byte)((y * 255 / Math.Max(1, height - 1) + offset) % 256),
                    B: 128);
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }
}
