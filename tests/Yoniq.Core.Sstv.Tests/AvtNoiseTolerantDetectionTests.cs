using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;
using Yoniq.Core.Imaging;

namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// S31 fix (spec/14-roadmap.md): before this fix, AVT could ONLY ever be detected via the
/// fixed-window <c>AnalogFmSstvDecoder.TryDecodeVisHeader</c> path (a single one-shot attempt
/// anchored at the very start of the buffer) -- <see cref="VisLockStateMachine"/>, the only
/// mechanism in this port with real-world noise tolerance, deliberately discarded every AVT match it
/// found. On a real capture (always preceded by <c>OutHEAD</c>'s 800ms leader tones plus room audio,
/// same root cause already documented for other modes' anchor-precision gaps) that one-shot window
/// never reaches real header content, so AVT could never be found at all -- confirmed empirically
/// against the real `avt.mmv` fixture (0 <c>ModeDetected</c> events across ~100s; see
/// `spec/14-roadmap.md`'s S31 entry).
///
/// These tests prove the fix's three separate claims: (1) AVT is now actually reachable via
/// <see cref="VisLockStateMachine"/>'s noise-tolerant scanning, end-to-end through a full decode;
/// (2) the sample handed to <c>TryStartAvtTraining</c> from that path is the SAME boundary the
/// fixed-window path would have computed, not a coincidentally-close approximation; (3) [S7,
/// spec/14-roadmap.md, supersedes the original S31-era claim here] an AVT match found via
/// <c>AnalogFmSstvDecoder.TryVisLockStateMachine</c> (mid-reception re-verification, while some other
/// mode is already locked) now correctly abandons the in-progress image and restarts into AVT
/// training -- S31 originally left this discarding such a match (no in-progress-image teardown
/// mechanism existed yet); S7 closed that gap by extracting <c>Commit()</c>'s own teardown into a
/// shared <c>AbandonInProgressImage()</c> helper.
///
/// A fourth test, <see cref="ChunkedStreamingPush_DuringLongAvtTrainingWindow_DoesNotCrashTrimBuffers"/>,
/// covers a real, previously undiscovered defect an auditor plan-review round caught before any code
/// shipped -- item (3) above sets <c>_fixedWindowExhausted</c> before its own AVT match can run, which
/// (before <c>AnalogFmSstvDecoder.TrimBuffers</c>'s matching fix) could in principle let the buffer
/// trim past <c>_avtPllWarmupStartSample</c> before <c>TryResolveAvtTraining</c>'s own warm-up loop
/// reads it, on a long-running chunked/streaming push (never exercised by this suite's usual bulk
/// single-`PushSamples` shape). That test's own doc comment records an honest empirical limit: a
/// deliberate sweep across leading-silence lengths and chunk sizes, with the `TrimBuffers` fix
/// temporarily disabled, never actually reproduced a crash (the OTHER watermark terms, particularly
/// the lazily-driven AGC/bandpass cursors, stayed conservative enough in every tried configuration)
/// -- so this test is real regression coverage for the code path (a long chunked AVT decode still
/// completes correctly), not a proven repro of the exact crash. The fix itself is kept regardless: it
/// restores an invariant `TrimBuffers`'s own comment explicitly claims and the auditor found a real,
/// structural way for item (3) to have broken -- narrow reachability in practice doesn't make the
/// underlying invariant violation any less real.
/// </summary>
public class AvtNoiseTolerantDetectionTests
{
    [Fact]
    public async Task HeaderAfterLeadingSilence_IsRecognizedAndDecodedViaVisLockStateMachine()
    {
        var mode = SstvModeRegistry.Avt;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(180, 90, 40));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        var encoder = new AnalogFmSstvEncoder(11025);
        var headerAndImageSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            headerAndImageSamples.Add(sample);
        }

        // Long enough that the fixed-window path's own one-shot search ceiling (VisHeader.
        // MaxSearchCeilingMs, ~1.3s) is exhausted well before the real header arrives -- the same
        // shape the real avt.mmv capture's OutHEAD leader + pre-TX room audio produces, forcing
        // detection through VisLockStateMachine or not at all.
        var leadingSilence = new float[3 * encoder.SampleRate];
        var fullStream = leadingSilence.Concat(headerAndImageSamples).ToArray();

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        SstvModeDefinition? detectedMode = null;
        IImageSource? decodedImage = null;
        decoder.ModeDetected += m => detectedMode = m;
        decoder.LineDecoded += update => decodedImage = update.Image;

        decoder.PushSamples(fullStream);

        Assert.NotNull(detectedMode);
        Assert.Equal(mode.Id, detectedMode!.Id);
        Assert.NotNull(decodedImage);

        var delta = ComputeAveragePerChannelDelta(sourceImage, decodedImage!);

        // A flat-color source image (not the gradient GoldenVectorTests/VisLockStateMachineDecoderTests
        // use) -- deliberately, so this test isolates "did detection + training-lock handoff work at
        // all" from AVT's own known decode-accuracy characteristics (already covered by
        // GoldenVectorTests/AvtTrainingLockDecoderTests). 10.0 matches this port's other synthetic
        // self-round-trip tolerances (SstvRoundTripTests). Code-level auditor review note: a flat
        // image makes this delta nearly insensitive to anchor error (even a several-pixel line-start
        // shift would still pass) -- this is deliberately NOT an anchor-precision check, only a
        // detection/handoff-worked-at-all one; HandoffOrigin_MatchesFixedWindowPath below and
        // GoldenVectorTests' real avt.mmv fixture are what actually check accuracy.
        Assert.True(delta <= 10.0, $"Average per-channel delta {delta:F2} -- expected a clean decode once VisLockStateMachine hands off to AVT training correctly.");
    }

    [Fact]
    public async Task HandoffOrigin_MatchesFixedWindowPath_RegardlessOfLeadingSilence()
    {
        var mode = SstvModeRegistry.Avt;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(180, 90, 40));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        var encoder = new AnalogFmSstvEncoder(11025);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        // No silence: header sits at sample 0, so TryDecodeVisHeader's fixed-window path wins the
        // race and calls TryStartAvtTraining directly -- this is the ALREADY-PROVEN-CORRECT origin
        // (AvtTrainingLockDecoderTests/GoldenVectorTests exercise this path today).
        var fixedWindowDecoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        fixedWindowDecoder.PushSamples(samples.ToArray());
        var fixedWindowOrigin = fixedWindowDecoder.AvtTrainingOriginSample;

        // With leading silence: the fixed-window path's one-shot ceiling is exhausted before the
        // real header arrives, so VisLockStateMachine's own noise-tolerant match (via
        // TryInterleavedHeaderScan's new AVT branch, S31 fix) is what calls TryStartAvtTraining
        // instead. The two paths must agree on the SAME boundary (headerStart + totalHeaderSampleCount),
        // not just land close by coincidence.
        var silenceSampleCount = 3 * encoder.SampleRate;
        var leadingSilence = new float[silenceSampleCount];
        var withSilence = leadingSilence.Concat(samples).ToArray();

        var silenceDecoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        silenceDecoder.PushSamples(withSilence);
        var silenceOrigin = silenceDecoder.AvtTrainingOriginSample;

        // VisLockStateMachine's own documented envelope-group-delay lag (~80 samples/~7.3ms at
        // 11025Hz, see its ProcessSample doc comment) is the only expected difference -- the
        // fixed-window path's fully analytic placement vs. this path's detected-trigger-derived one.
        var observedShift = (silenceOrigin - silenceSampleCount) - fixedWindowOrigin;
        Assert.InRange(observedShift, 0, 150);
    }

    [Fact]
    public async Task MidReception_RealAvtTransmissionAfterAnotherMode_RestartsAndDecodesCorrectly()
    {
        // S7 fix (spec/14-roadmap.md): this test used to pin the OLD (S31-era) deliberate discard
        // behavior -- an AVT match found via mid-reception re-verification (TryVisLockStateMachine)
        // was thrown away. S7 closed that gap (AbandonInProgressImage() + TryStartAvtTraining(),
        // mirroring how a normal VIS-lock mid-reception match already restarts via Commit()), so this
        // test now asserts the OPPOSITE: the trailing real AVT transmission DOES restart and decode.
        var firstMode = SstvModeRegistry.MartinM1;
        var firstImage = CreateGradientTestImage(firstMode.ImageWidth, firstMode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(11025);
        var firstSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(firstMode, firstImage))
        {
            firstSamples.Add(sample);
        }

        // Truncate well past the header but well before the image completes -- matches
        // MidReceptionRestartTests' own established shape for making the mid-reception
        // re-verification path (TryVisLockStateMachine) deterministically sweep across real header
        // content sitting past the truncation point, once per-line decoding of the (now-misinterpreted)
        // remaining audio marches _consumedSamples far enough forward.
        var truncatedFirstSamples = firstSamples.Take(firstSamples.Count * 3 / 10).ToArray();

        var avtMode = SstvModeRegistry.Avt;
        var avtPixels = new Rgb24[avtMode.ImageWidth * avtMode.ImageHeight];
        Array.Fill(avtPixels, new Rgb24(10, 20, 30));
        var avtImage = new ArrayImageSource(avtMode.ImageWidth, avtMode.ImageHeight, avtPixels);
        var avtSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(avtMode, avtImage))
        {
            avtSamples.Add(sample);
        }

        var combined = truncatedFirstSamples.Concat(avtSamples).ToArray();

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        var detectedModes = new List<SstvModeDefinition>();
        var decodedImages = new List<IImageSource>();
        var restartedModes = new List<SstvModeDefinition>();
        decoder.ModeDetected += m =>
        {
            detectedModes.Add(m);
            decodedImages.Add(null!);
        };
        decoder.DecodeRestarted += m => restartedModes.Add(m);
        decoder.LineDecoded += update => decodedImages[^1] = update.Image;

        decoder.PushSamples(combined);

        // Auditor code-level review's own requested pin: DecodeRestarted must fire with the OLD
        // (abandoned) mode -- TryProcessBuffer's own pre-captured local, not a stale _mode read --
        // and ModeDetected must fire with Avt, in that order.
        Assert.Equal(2, detectedModes.Count);
        Assert.Equal(firstMode.Id, detectedModes[0].Id);
        Assert.Equal(avtMode.Id, detectedModes[1].Id);
        Assert.Single(restartedModes);
        Assert.Equal(firstMode.Id, restartedModes[0].Id);

        var delta = ComputeAveragePerChannelDelta(avtImage, decodedImages[1]);
        Assert.True(delta <= 20.0, $"Second (real, mid-reception AVT) transmission average per-channel delta {delta:F2} exceeded tolerance.");
    }

    [Fact]
    public async Task MidReception_RealAvtTransmissionAfterAnotherMode_ChunkedPush_StaysBounded()
    {
        // S7 fix: auditor code-level review's own requested coverage -- a chunked push (spanning
        // multiple PushSamples calls across the up-to-~7.1s AVT training pending window) is the only
        // shape that actually exercises TrimBuffers' _avtPllWarmupStartSample protection and the
        // accepted pending-window retention tradeoff (see TryVisLockStateMachine's own new AVT branch
        // doc comment) -- a single bulk push proves the restart mechanism works, but not that it
        // survives real streaming.
        var firstMode = SstvModeRegistry.MartinM1;
        var firstImage = CreateGradientTestImage(firstMode.ImageWidth, firstMode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(11025);
        var firstSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(firstMode, firstImage))
        {
            firstSamples.Add(sample);
        }

        var truncatedFirstSamples = firstSamples.Take(firstSamples.Count * 3 / 10).ToArray();

        var avtMode = SstvModeRegistry.Avt;
        var avtPixels = new Rgb24[avtMode.ImageWidth * avtMode.ImageHeight];
        Array.Fill(avtPixels, new Rgb24(10, 20, 30));
        var avtImage = new ArrayImageSource(avtMode.ImageWidth, avtMode.ImageHeight, avtPixels);
        var avtSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(avtMode, avtImage))
        {
            avtSamples.Add(sample);
        }

        var combined = truncatedFirstSamples.Concat(avtSamples).ToArray();

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        var detectedModes = new List<SstvModeDefinition>();
        decoder.ModeDetected += m => detectedModes.Add(m);

        var maxBufferedSamples = 0;
        const int chunkSize = 500;
        for (var offset = 0; offset < combined.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, combined.Length - offset);
            decoder.PushSamples(combined.AsMemory(offset, length));
            maxBufferedSamples = Math.Max(maxBufferedSamples, decoder.BufferedSampleCount);
        }

        Assert.Equal(2, detectedModes.Count);
        Assert.Equal(firstMode.Id, detectedModes[0].Id);
        Assert.Equal(avtMode.Id, detectedModes[1].Id);

        // Measured, not assumed: the accepted pending-window retention tradeoff (see
        // TryVisLockStateMachine's own new AVT branch doc comment) is real and larger than a single
        // image's own duration -- the abandoned first image's audio is retained for the WHOLE
        // up-to-~7.1s pending window, on top of AVT's own long (~8s) header, so a generous peak bound
        // alone doesn't distinguish "bounded but retention-heavy" from "unbounded." The meaningful
        // check is that the buffer eventually SETTLES well below its own peak once AVT locks and
        // normal locked-branch trimming resumes -- proving this is bounded retention, not permanent
        // growth, without needing to predict the exact peak value.
        var totalSeconds = combined.Length / (double)encoder.SampleRate;
        var maxBufferedSeconds = maxBufferedSamples / (double)encoder.SampleRate;
        var finalBufferedSeconds = decoder.BufferedSampleCount / (double)encoder.SampleRate;
        Assert.True(
            maxBufferedSeconds < totalSeconds,
            $"Expected peak buffered sample count to stay below the full {totalSeconds:F1}s pushed (some trimming must occur), " +
            $"but peaked at {maxBufferedSamples} samples ({maxBufferedSeconds:F1}s).");
        Assert.True(
            finalBufferedSeconds < maxBufferedSeconds * 0.5,
            $"Expected buffered sample count to settle well below its own peak ({maxBufferedSeconds:F1}s) once AVT locks and normal " +
            $"trimming resumes, but ended at {decoder.BufferedSampleCount} samples ({finalBufferedSeconds:F1}s) -- looks like unbounded retention, not a bounded pending-window cost.");
    }

    [Fact]
    public async Task LockedAvtImage_BuffersActuallyTrimMidDecode_NotJustAtTheVeryEnd()
    {
        // Milestone-audit MUST fix (spec/14-roadmap.md, "Milestone audit, Phase 1+2"): TrimBuffers'
        // locked branch used to include _afcProcessedUpTo/_slantProcessedUpTo unconditionally, but AVT
        // is the one mode where both trackers are permanently null (InitializeAfc/InitializeSlant both
        // return early for AVT, matching legacy's own `mode != smAVT` guard on both features) -- so
        // neither cursor ever advances again once frozen at commit time, pinning the whole image's
        // buffer for its entire ~90s duration (240 lines x 375ms). The test above
        // (ChunkedPush_StaysBounded) does NOT catch this: it only samples BufferedSampleCount at the
        // very end of the push, after the image's own final EndOfImage has already reset everything --
        // this test instead samples repeatedly DURING the locked AVT decode itself, well before the
        // image completes, to prove active mid-image trimming is actually happening, not just a single
        // cleanup once the whole image is done.
        var avtMode = SstvModeRegistry.Avt;
        var avtPixels = new Rgb24[avtMode.ImageWidth * avtMode.ImageHeight];
        Array.Fill(avtPixels, new Rgb24(10, 20, 30));
        var avtImage = new ArrayImageSource(avtMode.ImageWidth, avtMode.ImageHeight, avtPixels);

        var encoder = new AnalogFmSstvEncoder(11025);
        var avtSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(avtMode, avtImage))
        {
            avtSamples.Add(sample);
        }

        var samples = avtSamples.ToArray();
        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        var locked = false;
        decoder.ModeDetected += _ => locked = true;
        IImageSource? decodedImage = null;
        decoder.LineDecoded += update => decodedImage = update.Image;

        var bufferedAfterLock = new List<int>();
        const int chunkSize = 20000;
        for (var offset = 0; offset < samples.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, samples.Length - offset);
            decoder.PushSamples(samples.AsMemory(offset, length));
            if (locked)
            {
                bufferedAfterLock.Add(decoder.BufferedSampleCount);
            }
        }

        Assert.True(bufferedAfterLock.Count >= 10, $"Expected enough post-lock samples to compare growth over time, got {bufferedAfterLock.Count}.");

        // Code-level review finding: a buffer-shrinks assertion alone doesn't prove decode is still
        // CORRECT -- PixelSampleReader's index lambda clamps rather than throwing on an out-of-range
        // read (AnalogFmSstvDecoder.cs:1102), so an over-aggressive watermark would silently corrupt
        // pixels rather than crash, and this test would stay green without this check.
        Assert.NotNull(decodedImage);
        var delta = ComputeAveragePerChannelDelta(avtImage, decodedImage!);
        Assert.True(delta <= 20.0, $"AVT decode average per-channel delta {delta:F2} exceeded tolerance -- mid-image trimming may have released data a live reader still needed.");

        // With the bug: buffered count grows roughly proportionally to pushed content for the WHOLE
        // image, since the locked watermark never advances past the lock anchor. With the fix: once
        // enough content has accumulated past the trim threshold, it plateaus regardless of how much
        // MORE content is pushed afterward. Compare the max seen in the first half of post-lock samples
        // against the max in the second half -- unbounded growth would make the second half's peak
        // dramatically larger (roughly double, since twice as much content has been pushed by then);
        // bounded trimming keeps the two peaks in the same rough range.
        //
        // Code-level review finding: a relative ratio alone has a hole -- it would also pass a SLOWER,
        // still-unbounded leak (anything growing less than 2x per half). Paired with an absolute
        // ceiling (a generous multiple of one line's own worth of samples, well above the handful of
        // samples any live AVT reader actually needs behind the lock anchor -- see the production
        // comment's own reader trace) so the check is monotone, not just relative.
        var half = bufferedAfterLock.Count / 2;
        var firstHalfMax = bufferedAfterLock.Take(half).Max();
        var secondHalfMax = bufferedAfterLock.Skip(half).Max();

        Assert.True(
            secondHalfMax < firstHalfMax * 1.5,
            $"Expected buffered sample count to plateau (bounded mid-image trimming) rather than keep growing through the AVT image -- " +
            $"first-half peak {firstHalfMax} samples, second-half peak {secondHalfMax} samples.");

        var oneLineSamples = avtMode.LineDurationMs / 1000.0 * encoder.SampleRate;
        var absoluteCeiling = (int)(oneLineSamples * 20); // generous: real margin needed is a handful of samples, not a whole line
        Assert.True(
            secondHalfMax < absoluteCeiling,
            $"Expected buffered sample count to stay within a small, bounded multiple of one line's worth of samples ({absoluteCeiling}), " +
            $"but saw {secondHalfMax} -- looks like unbounded (or just very slow) growth, not a true plateau.");
    }

    [Fact]
    public async Task ChunkedStreamingPush_DuringLongAvtTrainingWindow_DoesNotCrashTrimBuffers()
    {
        var mode = SstvModeRegistry.Avt;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(180, 90, 40));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        var encoder = new AnalogFmSstvEncoder(11025);
        var headerAndImageSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            headerAndImageSamples.Add(sample);
        }

        // Long enough that the fixed-window path is exhausted before the real header arrives --
        // detection MUST go through VisLockStateMachine/TryInterleavedHeaderScan, the new AVT
        // hand-off path this test targets.
        var leadingSilence = new float[30 * encoder.SampleRate];
        var fullStream = leadingSilence.Concat(headerAndImageSamples).ToArray();

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        SstvModeDefinition? detectedMode = null;
        IImageSource? decodedImage = null;
        decoder.ModeDetected += m => detectedMode = m;
        decoder.LineDecoded += update => decodedImage = update.Image;

        // Small chunks force AnalogFmSstvDecoder.TrimBuffers to run many times across the whole
        // silence + header + 2 extra VIS repeats + training-sequence span (~1.8s-7.1s of real time
        // once _avtTrainingPending is set) -- exactly the shape a real streaming caller (not this
        // suite's usual bulk single-PushSamples pattern) would produce, and the shape the auditor's
        // plan-review round identified as the one that could reach the pre-fix crash.
        const int chunkSize = 500;
        for (var offset = 0; offset < fullStream.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, fullStream.Length - offset);
            decoder.PushSamples(fullStream.AsMemory(offset, length));
        }

        Assert.NotNull(detectedMode);
        Assert.Equal(mode.Id, detectedMode!.Id);
        Assert.NotNull(decodedImage);
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

    private static double ComputeAveragePerChannelDelta(IImageSource expected, IImageSource actual)
    {
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

        return totalDelta / sampleCount;
    }
}
