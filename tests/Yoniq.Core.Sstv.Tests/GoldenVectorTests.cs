using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;
using Yoniq.Core.Imaging;

namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Golden-vector tests per spec/13-testing.md's "Golden-vector testing (legacy parity)" section:
/// reference input/output pairs captured from a real, running legacy YONIQ/MMSSTV install (not
/// re-derived from source a second time), see Fixtures/GoldenVectors/README.md for exact capture
/// provenance. Every other test in this suite (<see cref="SstvRoundTripTests"/> and friends) is
/// self-consistency only -- this file is the first to check the C# port's decoder against actual
/// legacy binary *output*, closing the gap <see cref="SstvRoundTripTests"/>'s own doc comment names.
///
/// Deliberately test-only: nothing here changes <c>AnalogFmSstvDecoder</c>/<c>AnalogFmSstvEncoder</c>.
///
/// Deferred (not attempted here, per an Opus plan-review before implementation): a true
/// signal-domain check of the C# encoder's output against the real `.mmv` audio itself (e.g.
/// comparing instantaneous-frequency-over-time curves). Two reasons: (1) the `.mmv` capture is not
/// a clean tap of the modulator -- `Sound.cpp:334/370-395` shows the recording buffer is delayed by
/// exactly one audio buffer during TX and the final buffer is never written (also: outside the TX
/// window the file contains real recorded sound-card/mic input, not silence -- confirmed directly
/// against source, correcting an earlier, wrong "no soundcard loopback" assumption), so there's no
/// clean reference signal to align against sample-for-sample; (2) building an aligned
/// frequency-curve comparison would need access to decoder-internal state
/// (<c>_consumedSamples</c>/<c>_visLockOriginSample</c>) that's private today -- exposing it would
/// turn this "test-only" effort into a production-code change, which is explicitly out of scope for
/// this pass. <see cref="MmvFixture_TxRegionDuration_MatchesExpectedTransmissionTiming"/> and
/// <see cref="EncoderOutput_DecodesSimilarlyTo_RealLegacyAudioDecode"/> capture most of the
/// realistic value of an encoder check without either problem.
/// </summary>
public class GoldenVectorTests
{
    private const string FixtureDir = "Fixtures/GoldenVectors";

    public static readonly TheoryData<string, string, string, int> Fixtures = new()
    {
        // (mode id, source bmp, legacy RX bmp, picture height -- Robot 36's RX save is the full
        // 320x256 shared canvas per GetBitmapSize/GetPictureSize, sstv.cpp:607-653, confirmed
        // directly against the fixture: rows 0-239 are real content, 240-255 are white margin).
        { "robot-36", "robot36.bmp", "robot36_RX.bmp", 240 },
        { "martin-m1", "martin-m1.bmp", "martin-m1_RX.bmp", 256 },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void LegacyOwnDecode_MatchesSourceImage_EstablishesBaselineDelta(
        string modeId, string sourceBmp, string rxBmp, int pictureHeight)
    {
        // Zero C# DSP involved -- this measures legacy's OWN encode+decode round-trip on its own
        // hardware/software, which is the honest reference bar every other tolerance in this file
        // should be judged against, replacing an invented number. spec/14-roadmap.md never
        // previously answered "what delta does legacy's own decoder actually achieve on this input,"
        // per its own Phase-1-isn't-done note -- this is that answer, measured directly.
        var source = BmpFile.Read(Path.Combine(FixtureDir, sourceBmp));
        var rx = CropToTop(BmpFile.Read(Path.Combine(FixtureDir, rxBmp)), pictureHeight);

        var delta = MeasureAveragePerChannelDelta(source, rx, pictureHeight);

        // Measured directly (not assumed): robot-36 = 6.99, martin-m1 = 1.57 -- both real, nonzero,
        // legacy's own decode is visibly imperfect even on a synthetic gradient with no analog
        // noise source (spot-checked manually: robot-36's left edge shows a genuine multi-pixel
        // transient in legacy's own decode, B ramping 23/76/95/116 into the true 128, not a step).
        // 15.0 is a generous upper bound (~2x the larger measured value) that still fails loudly on
        // a corrupted capture or a wrong crop, without pretending either measured number is a
        // pre-known target.
        Assert.True(delta < 15.0, $"[{modeId}] legacy-own-decode baseline delta {delta:F2} unexpectedly large -- check fixture/crop.");
    }

    public static readonly TheoryData<string, string, string, int> DecoderFixtures = new()
    {
        { "robot-36", "robot36.mmv", "robot36.bmp", 240 },
        { "martin-m1", "martin-m1.mmv", "martin-m1.bmp", 256 },
    };

    [Theory]
    [MemberData(nameof(DecoderFixtures))]
    public void Decoder_DecodesRealLegacyAudio_WithinToleranceOfSource(
        string modeId, string mmvFile, string sourceBmp, int pictureHeight)
    {
        var source = BmpFile.Read(Path.Combine(FixtureDir, sourceBmp));
        var (decoded, mode, restartCount) = DecodeMmvFixture(modeId, mmvFile);

        var actual = CropToTop(decoded, pictureHeight);
        var delta = MeasureAveragePerChannelDelta(source, actual, pictureHeight);

        // Measured directly against the real captures (not assumed), restarts=0 and the correct
        // mode detected first-try for both:
        //   martin-m1: 11.78 -- close to the 10.0 tolerance SstvRoundTripTests uses for its
        //     synthetic self-round-trip, slightly worse as expected for real captured audio (real
        //     mic-preamp/soundcard noise floor, not a bit-exact synthetic signal).
        //   robot-36:  68.06 -- a real, substantial, PRE-EXISTING known gap, not new information:
        //     SstvModeRegistry.cs's own doc comment already documents that Robot 36 (and the rest
        //     of the low-samples-per-pixel family) fails the 10.0 tolerance at 11025Hz even after
        //     the single-sample-readout fix, and names the still-incompletely-effective AFC/PLL
        //     settling speed as the likely remaining cause. This test grounds that existing,
        //     source-derived suspicion in a real captured-audio measurement for the first time.
        //     The bound below is a "no worse than currently observed" regression guard, NOT a
        //     parity claim -- per this project's explicit rule (spec/14-roadmap.md) against
        //     silently picking whatever tolerance makes a bad number pass and calling it parity.
        var toleranceByModeId = new Dictionary<string, double>
        {
            ["martin-m1"] = 15.0,
            ["robot-36"] = 75.0,
        };
        var tolerance = toleranceByModeId[modeId];

        Assert.True(
            delta < tolerance,
            $"[{modeId}] decoder-vs-source delta {delta:F2} exceeded regression-guard tolerance {tolerance}. " +
            $"restarts={restartCount}, detected mode=[{mode.Id}]");
    }

    // Piece 2c-i: an independent, non-tautological timing check -- it verifies this port's timing
    // table against a real legacy BINARY's actual output, not against source a human read (unlike
    // SstvRoundTripTests.LineDuration_MatchesLegacyGetTiming, which cross-checks against
    // CSSTVSET::GetTiming's *source*). This class's own doc comment (above) explains why a full
    // signal-domain comparison was deferred instead -- this timing check is deliberately coarse
    // (envelope amplitude only, not frequency content), which is exactly why it stays in-scope for
    // test-only code: no decoder/encoder internals needed.
    public static readonly TheoryData<string, string> MmvFiles = new()
    {
        { "robot-36", "robot36.mmv" },
        { "martin-m1", "martin-m1.mmv" },
    };

    [Theory]
    [MemberData(nameof(MmvFiles))]
    public void MmvFixture_TxRegionDuration_MatchesExpectedTransmissionTiming(string modeId, string mmvFile)
    {
        var (samples, sampleRate) = MmvFile.Read(Path.Combine(FixtureDir, mmvFile));
        var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);

        var (txStartSeconds, txEndSeconds) = MeasureTxRegion(samples, sampleRate);
        var measuredDurationSeconds = txEndSeconds - txStartSeconds;

        // VisHeader.PrefixDurationMs + NormalTailDurationMs = 910ms for a normal (non-narrow,
        // non-AVT) VIS header -- both fixture modes use a normal header.
        var expectedHeaderSeconds = (VisHeader.PrefixDurationMs + VisHeader.NormalTailDurationMs) / 1000.0;
        var expectedBodySeconds = mode.LineDurationMs * mode.ImageHeight / 1000.0;
        var expectedTotalSeconds = expectedHeaderSeconds + expectedBodySeconds;

        // Measured directly (not assumed): robot-36 expected 36.91s, measured ~38.6s (+1.7s);
        // martin-m1 expected 115.20s, measured ~116.9s (+1.7s). The consistent ~1.5-2s overshoot
        // across both modes, rather than a per-mode-specific one, points to coarse
        // envelope-detection slop (50ms windows, a fixed amplitude threshold catching the
        // modulator's ramp-up/down tails) rather than a real timing bug -- a tight tolerance here
        // would be testing this test's own envelope detector, not the port. 5.0s is generous enough
        // to absorb that slop while still catching a real gross timing error (wrong sample rate,
        // wrong mode duration table entry, badly misdetected TX region).
        var deltaSeconds = Math.Abs(measuredDurationSeconds - expectedTotalSeconds);
        Assert.True(
            deltaSeconds < 5.0,
            $"[{modeId}] TX-region duration {measuredDurationSeconds:F2}s vs expected {expectedTotalSeconds:F2}s, " +
            $"delta {deltaSeconds:F2}s exceeded tolerance.");
    }

    private static (double StartSeconds, double EndSeconds) MeasureTxRegion(float[] samples, int sampleRate)
    {
        const double windowSeconds = 0.05;
        const float threshold = 15000f / 32768f;

        var windowSize = (int)(windowSeconds * sampleRate);
        var windowPeaks = new List<float>();
        for (var i = 0; i < samples.Length; i += windowSize)
        {
            var end = Math.Min(i + windowSize, samples.Length);
            float peak = 0;
            for (var j = i; j < end; j++)
            {
                peak = Math.Max(peak, Math.Abs(samples[j]));
            }

            windowPeaks.Add(peak);
        }

        var firstActive = windowPeaks.FindIndex(p => p > threshold);
        var lastActive = windowPeaks.FindLastIndex(p => p > threshold);
        Assert.True(firstActive >= 0, "No TX region found above the amplitude threshold.");

        var startSeconds = firstActive * windowSeconds;
        var endSeconds = (lastActive + 1) * windowSeconds;
        return (startSeconds, endSeconds);
    }

    // Piece 2c-ii: an image-domain encoder cross-check that avoids the signal-domain alignment
    // problems a raw PCM/frequency-curve comparison against the real .mmv would hit (see this
    // class's own doc comment, above, for the deferred-piece-3 reasoning -- the .mmv capture is
    // offset by one audio buffer during TX and its final buffer is dropped, per
    // Fixtures/GoldenVectors/README.md, so it isn't even a clean copy of the modulator stream to
    // compare against sample-for-sample). Instead: encode the same source image with THIS port's
    // own encoder, decode that with THIS port's own decoder, and compare the result to what THIS
    // port's decoder produced from the REAL captured legacy audio (already measured in
    // Decoder_DecodesRealLegacyAudio_WithinToleranceOfSource). If the C# encoder had a structural
    // bug like the Scottie channel-order/sync-placement issue CLAUDE.md's TX/RX-split rule warns
    // about, the two decodes would diverge from each other, not just from the source.
    [Theory]
    [MemberData(nameof(DecoderFixtures))]
    public void EncoderOutput_DecodesSimilarlyTo_RealLegacyAudioDecode(
        string modeId, string mmvFile, string sourceBmp, int pictureHeight)
    {
        var source = BmpFile.Read(Path.Combine(FixtureDir, sourceBmp));
        var (realAudioDecoded, mode, _) = DecodeMmvFixture(modeId, mmvFile);

        var encoder = new AnalogFmSstvEncoder(11025);
        var encodedSamples = new List<float>();
        foreach (var sample in EncodeSync(encoder, mode, source))
        {
            encodedSamples.Add(sample);
        }

        var selfDecoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        SstvModeDefinition? selfDetectedMode = null;
        IImageSource? selfDecoded = null;
        selfDecoder.ModeDetected += m => selfDetectedMode = m;
        selfDecoder.LineDecoded += update => selfDecoded = update.Image;

        selfDecoder.PushSamples(encodedSamples.ToArray());

        Assert.NotNull(selfDetectedMode);
        Assert.Equal(mode.Id, selfDetectedMode!.Id);
        Assert.NotNull(selfDecoded);

        var croppedSelf = CropToTop(selfDecoded!, pictureHeight);
        var croppedReal = CropToTop(realAudioDecoded, pictureHeight);
        var delta = MeasureAveragePerChannelDelta(croppedSelf, croppedReal, pictureHeight);

        // Measured directly: martin-m1 = 13.66, robot-36 = 60.89 -- both close to (not wildly
        // divergent from) Decoder_DecodesRealLegacyAudio_WithinToleranceOfSource's own
        // decode-vs-source deltas for the same modes (11.78 / 68.06), which is exactly what's
        // expected if the C# encoder and decoder broadly agree with each other AND with the real
        // legacy signal -- not what's expected if the encoder had a structural bug independent of
        // the decoder's own known Robot-36-at-11025Hz gap. Same "regression guard, not parity claim"
        // policy as the sibling test above, for the same documented reason.
        var toleranceByModeId = new Dictionary<string, double>
        {
            ["martin-m1"] = 18.0,
            ["robot-36"] = 75.0,
        };
        var tolerance = toleranceByModeId[modeId];

        Assert.True(
            delta < tolerance,
            $"[{modeId}] self-encoded-vs-real-audio-decode delta {delta:F2} exceeded tolerance {tolerance}.");
    }

    private static IEnumerable<float> EncodeSync(AnalogFmSstvEncoder encoder, SstvModeDefinition mode, IImageSource image)
    {
        var task = CollectAsync(encoder, mode, image);
        return task.GetAwaiter().GetResult();
    }

    private static async Task<List<float>> CollectAsync(AnalogFmSstvEncoder encoder, SstvModeDefinition mode, IImageSource image)
    {
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, image))
        {
            samples.Add(sample);
        }

        return samples;
    }

    private static (IImageSource Decoded, SstvModeDefinition Mode, int RestartCount) DecodeMmvFixture(string modeId, string mmvFile)
    {
        var (samples, sampleRate) = MmvFile.Read(Path.Combine(FixtureDir, mmvFile));

        var decoder = new AnalogFmSstvDecoder(sampleRate);
        var detectedModesInOrder = new List<SstvModeDefinition>();
        var restartCount = 0;
        IImageSource? lastImageBeforeSecondLock = null;
        var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);

        decoder.ModeDetected += m => detectedModesInOrder.Add(m);
        decoder.DecodeRestarted += _ => restartCount++;
        decoder.LineDecoded += update =>
        {
            // These captures carry real sound-card audio recorded before/after the transmission
            // itself (confirmed during scoping: legacy's "Rec" only taps the modulator while
            // transmitting -- Wave.InClose() during TX means the pre/post-TX portions are live mic
            // input, not silence), so the decoder legitimately keeps scanning afterward and could in
            // principle find a second, spurious lock in that extra audio. Freezing on the last
            // LineDecoded update seen while still on the FIRST detected mode -- rather than assuming
            // the last LineDecoded event overall is the real image -- means a later spurious lock
            // can't silently overwrite the image we actually want to compare. See
            // Fixtures/GoldenVectors/README.md for the measured TX-region timing.
            if (detectedModesInOrder.Count == 1)
            {
                lastImageBeforeSecondLock = update.Image;
            }
        };

        decoder.PushSamples(samples);

        Assert.NotEmpty(detectedModesInOrder);
        Assert.Equal(mode.Id, detectedModesInOrder[0].Id);
        Assert.NotNull(lastImageBeforeSecondLock);

        return (lastImageBeforeSecondLock!, mode, restartCount);
    }

    private static IImageSource CropToTop(IImageSource image, int pictureHeight)
    {
        if (pictureHeight == image.Height)
        {
            return image;
        }

        var pixels = new Rgb24[image.Width * pictureHeight];
        for (var y = 0; y < pictureHeight; y++)
        {
            var line = image.GetScanline(y);
            for (var x = 0; x < image.Width; x++)
            {
                pixels[(y * image.Width) + x] = line[x];
            }
        }

        return new ArrayImageSource(image.Width, pictureHeight, pixels);
    }

    private static double MeasureAveragePerChannelDelta(IImageSource expected, IImageSource actual, int height)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(height, actual.Height);

        double totalDelta = 0;
        var sampleCount = 0;

        for (var y = 0; y < height; y++)
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

        return totalDelta / sampleCount;
    }
}
