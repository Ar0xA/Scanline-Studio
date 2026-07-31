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
/// realistic value of an encoder check without either problem -- for martin-m1. Round-2-review
/// correction: this summary used to make that claim unqualified for both fixture modes, which
/// <see cref="EncoderOutput_DecodesSimilarlyTo_RealLegacyAudioDecode"/>'s own per-test comment
/// already contradicts for robot-36 (its corruption-floor finding: that test's robot-36 arm is a
/// regression tripwire only, not meaningfully discriminating, until the Robot-36-at-11025Hz DSP gap
/// documented on <see cref="Decoder_DecodesRealLegacyAudio_WithinToleranceOfSource"/> is fixed) --
/// this summary should have said so too instead of leaving an unqualified claim standing at the
/// file's most-read location.
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

        // Re-measured after Piece 9 (spec/14-roadmap.md) narrowed PllFmDemodulator from 1100-2300Hz
        // to legacy's real 1500-2300Hz image-decode band, restarts=0 and the correct mode detected
        // first-try for both:
        //   martin-m1: 1.22 -- was 11.78 pre-piece-9 (already close to SstvRoundTripTests' own 10.0
        //     synthetic-self-round-trip tolerance); the tighter PLL band's better-settled per-pixel
        //     reads brought real-capture accuracy roughly in line with synthetic self-consistency.
        //   robot-36:  16.995 -- was 68.06 pre-piece-9, a ~4x improvement. The narrower-band-PLL
        //     hypothesis from spec/14-roadmap.md's re-verification plan (VCO gain change affecting
        //     line-start settling) turned out not to dominate here -- EVERY mode's round-trip delta
        //     improved after narrowing (measured directly, not assumed; see spec/14-roadmap.md's
        //     Piece 9 entry for the full per-mode table), consistent with the narrower band's real
        //     benefit (less out-of-band content bleeding into the tracked frequency range) winning
        //     out over the slower-reacquisition risk in practice, at least for these two fixtures'
        //     actual content and rates. This closes out the "5x-worse-than-synthetic" divergence a
        //     previous revision of this comment flagged as needing its own follow-up investigation --
        //     the PLL bandwidth mismatch WAS that investigation's target all along (Piece 9's own
        //     starting diagnosis), not a coincidence.
        //     Previous revision's "not meaningfully discriminating" framing for robot-36's tolerance
        //     no longer applies: the corruption-floor measurement it relied on (~42.67 for this exact
        //     source image under this exact metric -- a flat gray image, a horizontal mirror, a
        //     vertical flip, and an R<->B channel swap all score around there) is now comfortably
        //     ABOVE robot-36's real measured delta (16.995), not below it. Tightened from 75.0 to
        //     25.0 -- real margin over the measured value (comparable proportionally to martin-m1's
        //     own margin below), while staying safely under the corruption floor, so this bound is a
        //     genuine discriminating check again, not just a regression tripwire.
        // Re-measured again after the m_KSS/m_KS2S pixel-pitch fix (spec/14-roadmap.md's item 3,
        // GetPixelPitchTrimFactor): martin-m1 1.22 -> 1.438 (still well inside tolerance), robot-36
        // 16.995 -> 14.809 (improved). Both fixtures are group E (239/240 trim on both axes), so the
        // ~0.4% horizontal correction moved each fixture a small, opposite-signed amount -- expected
        // given it's a scale fix, not a settling-time fix like Piece 9's was.
        var toleranceByModeId = new Dictionary<string, double>
        {
            ["martin-m1"] = 15.0,
            ["robot-36"] = 25.0,
        };
        var tolerance = toleranceByModeId[modeId];

        Assert.Equal(0, restartCount);
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

        // Round-1-review finding: an earlier version of this test omitted two real, fixed-duration
        // legacy TX segments and misattributed the resulting ~1.7s gap to "envelope-detection
        // slop" -- wrong, and caught by re-deriving the envelope by hand (both edges are a single
        // 50ms window wide, not a ramp, so slop is bounded at about +-0.1s per edge, not 1.7s).
        // Confirmed directly against source instead: TMmsstv::OutHEAD (Main.cpp:7270-7292) writes
        // 8x100ms leader tones (800ms) unconditionally before the VIS header at legacy's shipped
        // defaults (sys.m_VOX==0, non-narrow) -- called from SendSSTV at Main.cpp:7393, right
        // before the VIS header itself. TMmsstv::SendSSTV's own footer (Main.cpp:6996-7009, same
        // shipped defaults, sys.m_TXFSKID==0 per Main.cpp:903) writes
        // WriteC(1500, min(SSTVSET.m_TW, SampFreq/2)) + 4x100ms (400ms).
        //
        // Round-2-review correction: the first version of this fix read m_TW as "one line's
        // duration in samples" for the TRANSMITTED mode -- wrong, caught by tracing SSTVSET.m_TW's
        // actual assignment. m_TW is set by CSSTVSET::SetSampFreq (sstv.cpp:655-1109, the RECEIVE
        // side), as `GetTiming(m_Mode) * m_SampFreq / 1000.0` -- m_Mode is the demodulator's
        // currently-selected mode, not the mode being transmitted. The TX-side equivalent is a
        // genuinely separate field, CSSTVSET::SetTxSampFreq's `m_TTW = GetTiming(m_TxMode) *
        // m_TxSampFreq / 1000.0` (sstv.cpp:1280-1285) -- SendSSTV's footer does not use it. So this
        // footer segment's length depends on whatever mode the RX side happens to be sitting on at
        // the moment TX finishes, not on the mode actually being sent -- confirmed empirically: both
        // fixtures measure the same ~425ms WriteC portion despite transmitting different modes
        // (150ms vs 446ms LineDurationMs), consistent with both captures' RX side sitting at
        // GetTiming's own `default:` case (smSCT1, 428.22ms, sstv.cpp:1275) during capture.
        //
        // Round-3-review correction: an earlier version of this comment called that RX-side state
        // "the demodulator's default/never-changed mode on a fresh run" -- not quite right, and not
        // a general invariant. SSTVSET.m_Mode is NOT constructor-fixed: on every ini load,
        // Main.cpp:1872-1873 reads the *persisted* TX mode (`Define/SSTVMode`) and immediately calls
        // `SSTVSET.SetMode()` with it, seeding the RX side from whatever TX mode was last saved --
        // which happens to have been Scottie 1 (smSCT1, matching CSSTVSET's own constructor default
        // at sstv.cpp:570) for these two specific captures, not because the value can never change.
        // Re-capturing after a session that saved a different persisted TX mode would change this
        // constant. Documented here as a fact about these two committed fixtures, not a property of
        // the .mmv format or of legacy in general -- modeled as a named constant rather than derived
        // from the transmitted mode, since it demonstrably isn't derived from that mode either way.
        const double receiveSideModeLineDurationMsForTheseFixtures = 428.22; // GetTiming's default: case (smSCT1)
        var headSeconds = 0.8;
        var footerSeconds = (Math.Min(receiveSideModeLineDurationMsForTheseFixtures, 500.0) / 1000.0) + 0.4;
        var expectedTotalSeconds = headSeconds + expectedHeaderSeconds + expectedBodySeconds + footerSeconds;

        // Measured directly (not assumed), after also fixing MeasureTxRegion's own window-duration
        // unit mismatch (see that method's comment): robot-36 expected 38.5382s, measured 38.5325s
        // (delta 0.0057s); martin-m1 expected 116.8284s, measured 116.7970s (delta 0.0314s) -- both
        // sub-50ms residuals (envelope-window granularity), a very different picture from the
        // pre-review 1.7s-per-mode gap this test used to carry. Round-3-review note: these exact
        // numbers (and their sign) shift slightly whenever the underlying .mmv files are
        // re-trimmed/re-captured, since trimming re-phases the fixed-size envelope-window grid
        // against the TX region -- re-measure rather than assume these stay exact after any future
        // fixture change. 0.2s is comfortably above the larger residual either way: generous enough
        // to absorb real envelope-window granularity, while still tight enough to catch a real gross
        // timing error (wrong sample rate, wrong mode duration table entry, badly misdetected TX
        // region) -- a 25x tighter bound than the original 5.0s, which was wide enough to accept
        // anything from ~129ms to ~171ms as "correct" for robot-36's own 150ms line duration and so
        // couldn't actually catch the error class it claimed to guard.
        var deltaSeconds = Math.Abs(measuredDurationSeconds - expectedTotalSeconds);
        Assert.True(
            deltaSeconds < 0.2,
            $"[{modeId}] TX-region duration {measuredDurationSeconds:F2}s vs expected {expectedTotalSeconds:F2}s, " +
            $"delta {deltaSeconds:F2}s exceeded tolerance.");
    }

    private static (double StartSeconds, double EndSeconds) MeasureTxRegion(float[] samples, int sampleRate)
    {
        const double nominalWindowSeconds = 0.05;
        const float threshold = 15000f / 32768f;

        var windowSize = (int)(nominalWindowSeconds * sampleRate);
        // Round-2-review fix: windowSize is an integer sample count, e.g. (int)(0.05*11025) = 551
        // samples = 49.977ms, not exactly the nominal 50ms used to compute it -- converting window
        // INDICES back to seconds using the nominal constant instead of this actual window duration
        // introduced a systematic +0.045% scale error, proportional to elapsed time (~17ms on
        // robot-36's ~38s capture, ~45ms on martin-m1's ~117s one -- both confirmed by an
        // independent re-implementation of this same algorithm). Deriving the conversion from the
        // actual windowSize/sampleRate instead removes that error rather than just tolerating it.
        var actualWindowSeconds = windowSize / (double)sampleRate;

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

        var startSeconds = firstActive * actualWindowSeconds;
        var endSeconds = (lastActive + 1) * actualWindowSeconds;
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
    public async Task EncoderOutput_DecodesSimilarlyTo_RealLegacyAudioDecode(
        string modeId, string mmvFile, string sourceBmp, int pictureHeight)
    {
        var source = BmpFile.Read(Path.Combine(FixtureDir, sourceBmp));
        var (realAudioDecoded, mode, _) = DecodeMmvFixture(modeId, mmvFile);

        var encoder = new AnalogFmSstvEncoder(11025);
        var encodedSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, source))
        {
            encodedSamples.Add(sample);
        }

        var selfDecoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        SstvModeDefinition? selfDetectedMode = null;
        IImageSource? selfDecoded = null;
        // Deliberately simpler than DecodeMmvFixture's "pin the first lock" logic: encodedSamples
        // is exactly one clean encode with no trailing audio, so unlike the real .mmv captures
        // there is no extra content for a second, spurious lock to ever occur against -- keeping
        // the last (== only) ModeDetected/LineDecoded update is equivalent here, not an
        // inconsistency with the sibling helper.
        selfDecoder.ModeDetected += m => selfDetectedMode = m;
        // Round-1-review fix: snapshot immediately rather than aliasing the decoder's own live
        // pixel buffer -- see DecodeMmvFixture's own comment on Snapshot for why this matters even
        // though, for this single-lock case, the live reference happens to be safe to read after
        // PushSamples returns too.
        selfDecoder.LineDecoded += update => selfDecoded = Snapshot(update.Image);

        selfDecoder.PushSamples(encodedSamples.ToArray());

        Assert.NotNull(selfDetectedMode);
        Assert.Equal(mode.Id, selfDetectedMode!.Id);
        Assert.NotNull(selfDecoded);

        var croppedSelf = CropToTop(selfDecoded!, pictureHeight);
        var croppedReal = CropToTop(realAudioDecoded, pictureHeight);
        var delta = MeasureAveragePerChannelDelta(croppedSelf, croppedReal, pictureHeight);

        // Measured directly: martin-m1 = 13.66, robot-36 = 60.89.
        //
        // martin-m1's number is close to (not wildly divergent from)
        // Decoder_DecodesRealLegacyAudio_WithinToleranceOfSource's own decode-vs-source delta for
        // the same mode (11.78), and comfortably below its own ~42.66 corruption floor (see below)
        // -- genuinely consistent with the encoder and decoder broadly agreeing with each other and
        // with the real legacy signal, not just with a loose tolerance happening to pass.
        //
        // robot-36's number is NOT meaningfully evidence of agreement -- round-1-review finding,
        // confirmed by measurement: a flat gray image, a horizontally mirrored copy, a vertically
        // flipped copy, and an R<->B channel swap of the source all score ~42.67 under this exact
        // metric on this exact (very smooth) gradient image, and 60.89 is WORSE than all of them.
        // The earlier version of this comment read 60.89 as "close to" 68.06 and took that as
        // evidence of encoder/decoder agreement -- overclaimed: being close to another already-bad
        // number is not evidence of correctness when both numbers are already worse than trivial
        // structural corruption would score. Kept only as a regression tripwire for robot-36, same
        // as its sibling test's own tolerance -- not a discriminating check until the underlying
        // Robot-36-at-11025Hz DSP gap (documented on the sibling test) is fixed.
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

    // Round-1-review fix: AnalogFmSstvDecoder.LineDecoded hands out a MutableImageSource wrapping
    // the decoder's own LIVE _pixels array by reference, not a copy (confirmed directly against
    // AnalogFmSstvDecoder.cs: Commit() allocates a fresh _pixels array on every lock, and the only
    // writer, DecodeLine, re-reads _pixels from the field at the top of every outer-loop
    // iteration). Capturing that reference and reading it later works TODAY only because of that
    // "always a fresh array" invariant -- if Commit() were ever changed to reuse a same-sized
    // buffer instead of reallocating (a plausible future allocation optimization), a captured
    // reference would silently keep mutating after being "frozen," with no visible symptom. Copying
    // into a real snapshot here removes the dependency on that invariant instead of relying on it,
    // for the two capture sites in THIS file specifically. Round-2-review note: this same pattern
    // (bare `update.Image` capture, no snapshot) is used at roughly 15 other LineDecoded sites
    // across this test project (SstvRoundTripTests, PllScaleBridgeTests, SlantTests,
    // MidReceptionRestartTests, EndOfImageResetTests, and others) -- this fix does not protect any
    // of those, and fixing it here should not be read as having addressed the pattern project-wide.
    // Left as-is rather than touched here: this pass is scoped to the golden-vector harness, and a
    // repo-wide sweep is a separate, explicitly-scoped follow-up (or, more robustly, enforcing the
    // "always allocate fresh" invariant at the decoder itself, with a doc comment or test asserting
    // it, rather than every caller having to defensively copy).
    private static ArrayImageSource Snapshot(IImageSource image)
    {
        var pixels = new Rgb24[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            var line = image.GetScanline(y);
            for (var x = 0; x < image.Width; x++)
            {
                pixels[(y * image.Width) + x] = line[x];
            }
        }

        return new ArrayImageSource(image.Width, image.Height, pixels);
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
            // These captures originally carried real sound-card audio recorded before/after the
            // transmission itself (confirmed during scoping: legacy's "Rec" only taps the modulator
            // while transmitting -- Wave.InClose() during TX means the pre/post-TX portions are live
            // mic input, not silence); now trimmed to the TX region plus a 1.0s margin (see
            // Fixtures/GoldenVectors/README.md), so the hazard this logic guards against is much
            // smaller in practice than when it was written, but the decoder still legitimately keeps
            // scanning past the end of the real transmission regardless of how much trailing audio
            // remains, so the defensive logic stays worth keeping. Snapshotting (see
            // Snapshot's own comment for why a bare reference isn't safe to rely on) on the last
            // LineDecoded update seen while still on the FIRST detected mode -- rather than assuming
            // the last LineDecoded event overall is the real image -- means a later spurious lock
            // can't silently overwrite the image we actually want to compare. See
            // Fixtures/GoldenVectors/README.md for the measured TX-region timing.
            if (detectedModesInOrder.Count == 1)
            {
                lastImageBeforeSecondLock = Snapshot(update.Image);
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
