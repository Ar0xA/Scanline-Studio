using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;
using Yoniq.Core.Imaging;

namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// S8 fix (spec/14-roadmap.md): legacy's real narrow-mode (MN/MC) FSK-announce decoder
/// (<c>CSSTVDEM::DecodeFSK</c>) can lock even mid-image, on real live audio, at any offset -- this
/// port's own <see cref="NarrowFskHeaderDecoder"/> was already a faithful, tested per-sample port of
/// that state machine, but was only ever used in a one-shot, fixed-window way
/// (<c>AnalogFmSstvDecoder.TryDecodeNarrowModeHeader</c>, cold-started at <c>_consumedSamples</c>).
/// This fix wires the SAME decoder up as a persistent, continuously-fed scanner
/// (<c>AnalogFmSstvDecoder.TryNarrowFskScan</c>), reachable both pre-lock (via
/// <c>TryInterleavedHeaderScan</c>) and mid-reception while another mode is already locked (via
/// <c>TryVisLockStateMachine</c>) -- unlike S31's AVT restriction, a narrow-FSK match needs no
/// special-casing at the mid-reception call site, since it's immediately actionable via the same
/// <c>Commit()</c> every other restart already uses.
///
/// These tests prove: (1) detection works via the new noise-tolerant path specifically (leading
/// silence a fixed-window scan would miss); (2) the anchor the auditor's plan-review round redesigned
/// (bit-clock-origin-relative, not the jitter-prone guard-tone trigger) is accurate against a MEASURED
/// tolerance, not just the derived arithmetic; (3) a genuine mid-reception restart (narrow-FSK arriving
/// while another mode is already locked and mid-decode) actually restarts, unlike AVT's deliberate
/// non-restart; (4) the buffer stays bounded across multiple image cycles with the new,
/// never-reset <c>_narrowFskProcessedUpTo</c> cursor in the mix.
/// </summary>
public class NarrowFskNoiseTolerantDetectionTests
{
    private const int SampleRate = 11025;

    [Fact]
    public async Task HeaderAfterLeadingSilence_IsRecognizedAndDecodedViaPersistentScan()
    {
        var mode = SstvModeRegistry.Mn110;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var headerAndImageSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            headerAndImageSamples.Add(sample);
        }

        // Long enough that the fixed-window path (TryDecodeNarrowModeHeader, one-shot at
        // _consumedSamples) is exhausted before the real header arrives -- detection MUST go through
        // TryNarrowFskScan, the new path this test targets.
        var leadingSilence = new float[3 * SampleRate];
        var fullStream = leadingSilence.Concat(headerAndImageSamples).ToArray();

        var decoder = new AnalogFmSstvDecoder(SampleRate);
        SstvModeDefinition? detectedMode = null;
        IImageSource? decodedImage = null;
        decoder.ModeDetected += m => detectedMode = m;
        decoder.LineDecoded += update => decodedImage = update.Image;

        decoder.PushSamples(fullStream);

        Assert.NotNull(detectedMode);
        Assert.Equal(mode.Id, detectedMode!.Id);
        Assert.NotNull(decodedImage);

        var delta = ComputeAveragePerChannelDelta(sourceImage, decodedImage!);

        // 20.0 matches this port's other real/noise-tolerant-path tolerances (GoldenVectorTests'
        // mn110 entry, VisLockStateMachineDecoderTests) -- this isn't an accuracy-of-the-fixed-window-
        // path check, just confirms detection + a full, undistorted decode happened at all via the
        // new path.
        Assert.True(delta <= 20.0, $"Average per-channel delta {delta:F2} -- expected a clean decode once TryNarrowFskScan hands off correctly.");
    }

    [Fact]
    public async Task HeaderAfterLeadingSilence_AnchorMatchesExpectedWithinMeasuredTolerance()
    {
        var mode = SstvModeRegistry.Mn73;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var headerAndImageSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            headerAndImageSamples.Add(sample);
        }

        var silenceSampleCount = 3 * SampleRate;
        var leadingSilence = new float[silenceSampleCount];
        var fullStream = leadingSilence.Concat(headerAndImageSamples).ToArray();

        var decoder = new AnalogFmSstvDecoder(SampleRate);
        int? rawAnchor = null;
        decoder.LockAnchorCommitted += a => rawAnchor ??= a;
        SstvModeDefinition? detectedMode = null;
        decoder.ModeDetected += m => detectedMode = m;

        decoder.PushSamples(fullStream);

        Assert.NotNull(detectedMode);
        Assert.Equal(mode.Id, detectedMode!.Id);
        Assert.NotNull(rawAnchor);

        // Expected: silence + OutHEAD's own now-real leading burst (SHOULD item 5, spec/14-roadmap.md
        // -- the encoder emits this unconditionally before the narrow FSK header itself, so it's part
        // of the real offset the header starts at) + the packet's own fixed total duration
        // (VisHeader.NarrowHeaderTotalDurationMs) -- exactly what the fixed-window path
        // (TryDecodeNarrowModeHeader) would produce for a header starting at the same offset, and what
        // the auditor plan-review's own derivation targets.
        var expectedAnchor = silenceSampleCount
            + (int)Math.Round(VisHeader.OutHeadNarrowDurationMs / 1000.0 * SampleRate)
            + (int)Math.Round(VisHeader.NarrowHeaderTotalDurationMs / 1000.0 * SampleRate);
        var delta = rawAnchor!.Value - expectedAnchor;

        // Measured, not assumed (auditor plan-review's own explicit acceptance criterion for the
        // bit-clock-origin-relative anchor redesign): 104 samples (~9.4ms at 11025Hz), consistently
        // reproduced. Late, not early -- consistent with real d19/FskSpace envelope-detector settling
        // lag on the mode-2 trigger (both 100Hz-bandwidth resonators, the same category of delay
        // already documented elsewhere in this port: VisLockStateMachine's own ~7.3ms trigger lag,
        // TryDecodeVisDataBits' ~14.5ms measured settling lag), not a derivation error -- the auditor's
        // own tighter-bound estimate for this specific trigger ("within ~11ms by construction") already
        // anticipated a delay in this range. 150 gives real headroom above the measured value without
        // being a loose bound chosen just to make this pass.
        Assert.True(Math.Abs(delta) <= 150, $"Raw committed anchor {rawAnchor} vs. expected {expectedAnchor} (delta {delta}) -- exceeded measured tolerance.");
    }

    [Fact]
    public async Task MidReception_RealNarrowTransmissionAfterAnotherMode_RestartsAndDecodesCorrectly()
    {
        var firstMode = SstvModeRegistry.MartinM1;
        var firstImage = CreateGradientTestImage(firstMode.ImageWidth, firstMode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var firstSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(firstMode, firstImage))
        {
            firstSamples.Add(sample);
        }

        // Truncate well past the header but well before the image completes -- matches
        // MidReceptionRestartTests'/AvtNoiseTolerantDetectionTests' own established shape for making
        // the mid-reception re-verification path (TryVisLockStateMachine, now including
        // TryNarrowFskScan) deterministically sweep across real header content sitting past the
        // truncation point.
        var truncatedFirstSamples = firstSamples.Take(firstSamples.Count * 3 / 10).ToArray();

        var narrowMode = SstvModeRegistry.Mn110;
        var narrowImage = CreateGradientTestImage(narrowMode.ImageWidth, narrowMode.ImageHeight);
        var narrowSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(narrowMode, narrowImage))
        {
            narrowSamples.Add(sample);
        }

        var combined = truncatedFirstSamples.Concat(narrowSamples).ToArray();

        var decoder = new AnalogFmSstvDecoder(SampleRate);
        var detectedModes = new List<SstvModeDefinition>();
        var decodedImages = new List<IImageSource>();
        var restartCount = 0;
        decoder.ModeDetected += m =>
        {
            detectedModes.Add(m);
            decodedImages.Add(null!);
        };
        decoder.DecodeRestarted += _ => restartCount++;
        decoder.LineDecoded += update => decodedImages[^1] = update.Image;

        decoder.PushSamples(combined);

        // Unlike S31's deliberate AVT non-restart, a narrow-FSK match found mid-reception DOES
        // restart -- this is the actual new capability S8 adds.
        Assert.Equal(2, detectedModes.Count);
        Assert.Equal(firstMode.Id, detectedModes[0].Id);
        Assert.Equal(narrowMode.Id, detectedModes[1].Id);
        Assert.Equal(1, restartCount);

        var delta = ComputeAveragePerChannelDelta(narrowImage, decodedImages[1]);
        Assert.True(delta <= 20.0, $"Second (real, mid-reception) transmission average per-channel delta {delta:F2} exceeded tolerance.");
    }

    [Fact]
    public async Task BulkPush_EarlierNonNarrowTransmissionBeforeLaterNarrowOne_DetectsEarlierFirst()
    {
        // Milestone-audit MUST fix (spec/14-roadmap.md, "Milestone audit, Phase 1+2"):
        // TryNarrowFskScan used to run as a complete whole-scanBound pre-pass BEFORE
        // TryInterleavedHeaderScan's own sync-bypass/VIS-lock loop ever took a single step. On a bulk
        // push containing an EARLIER non-narrow transmission followed by a LATER narrow one, this let
        // the narrow scan discover and Commit() the later transmission first, since it swept the whole
        // buffer in one shot -- the earlier transmission's own header was never examined at all,
        // because narrow-FSK's Commit() call reset decoder state before the interleaved loop got a
        // turn. Reproduces the bug's own real shape: leading silence long enough to exhaust the
        // fixed-window header path (forcing detection through TryInterleavedHeaderScan's noise-
        // tolerant scan, not the separate one-shot fixed-window path), followed by a FULL first
        // transmission, followed immediately by a full narrow one, all pushed in ONE bulk call so
        // _fixedWindowExhausted flips true immediately and scanBound spans the whole buffer from the
        // very first call -- exactly the shape that exercised the old whole-buffer pre-pass.
        var firstMode = SstvModeRegistry.MartinM1;
        var firstImage = CreateGradientTestImage(firstMode.ImageWidth, firstMode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var firstSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(firstMode, firstImage))
        {
            firstSamples.Add(sample);
        }

        var narrowMode = SstvModeRegistry.Mn110;
        var narrowImage = CreateGradientTestImage(narrowMode.ImageWidth, narrowMode.ImageHeight);
        var narrowSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(narrowMode, narrowImage))
        {
            narrowSamples.Add(sample);
        }

        var leadingSilence = new float[3 * SampleRate];
        var combined = leadingSilence.Concat(firstSamples).Concat(narrowSamples).ToArray();

        var decoder = new AnalogFmSstvDecoder(SampleRate);
        var detectedModes = new List<SstvModeDefinition>();
        var decodedImages = new List<IImageSource>();
        var restartCount = 0;
        decoder.ModeDetected += m =>
        {
            detectedModes.Add(m);
            decodedImages.Add(null!);
        };
        decoder.DecodeRestarted += _ => restartCount++;
        decoder.LineDecoded += update => decodedImages[^1] = update.Image;

        decoder.PushSamples(combined);

        // Code-level review finding: this fix makes _narrowFskProcessedUpTo advance in lockstep with
        // the interleaved loop instead of racing ahead to consume the whole buffer up front -- meaning
        // TryVisLockStateMachine's own (separate, NOT touched by this fix) narrow-FSK pre-pass now has
        // real Martin M1 picture content to scan through once locked, where before this fix it was an
        // inert no-op (the old bug's own pre-lock pass had already exhausted the cursor). Asserting
        // zero restarts here directly guards against that newly-reachable false-positive-restart risk
        // (a real, if legacy-faithful, exposure -- see this file's own doc comment update and
        // spec/14-roadmap.md's MUST fix 2 entry for the full reasoning on why it's deferred, not fixed
        // in this same pass).
        Assert.Equal(0, restartCount);

        Assert.Equal(2, detectedModes.Count);
        Assert.Equal(firstMode.Id, detectedModes[0].Id);
        Assert.Equal(narrowMode.Id, detectedModes[1].Id);

        var firstDelta = ComputeAveragePerChannelDelta(firstImage, decodedImages[0]);
        Assert.True(firstDelta <= 20.0, $"First (earlier, non-narrow) transmission average per-channel delta {firstDelta:F2} exceeded tolerance.");

        var narrowDelta = ComputeAveragePerChannelDelta(narrowImage, decodedImages[1]);
        Assert.True(narrowDelta <= 20.0, $"Second (later, narrow) transmission average per-channel delta {narrowDelta:F2} exceeded tolerance.");
    }

    [Fact]
    public async Task BufferStaysBounded_AcrossMultipleImageCycles_WithPersistentNarrowFskCursor()
    {
        // S8 fix: _narrowFskProcessedUpTo is deliberately never reset/jumped (see its own field doc
        // comment) -- confirms this doesn't reopen BufferedSampleCount_StaysBounded_ForLongNeverLockingStream's
        // own failure shape across a realistic multi-image sequence (not just a single never-locking
        // stream, which BufferTrimTests already covers).
        var mode = SstvModeRegistry.MartinM1;
        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var allSamples = new List<float>();

        const int imageCount = 3;
        for (var i = 0; i < imageCount; i++)
        {
            var image = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
            await foreach (var sample in encoder.EncodeAsync(mode, image))
            {
                allSamples.Add(sample);
            }
        }

        var decoder = new AnalogFmSstvDecoder(SampleRate);
        var detectedCount = 0;
        decoder.ModeDetected += _ => detectedCount++;

        // Code-level auditor review: convert once, not once per chunk (was O(n^2)).
        var allSamplesArray = allSamples.ToArray();
        const int chunkSize = 4096;
        for (var offset = 0; offset < allSamplesArray.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, allSamplesArray.Length - offset);
            decoder.PushSamples(allSamplesArray.AsMemory(offset, length));
        }

        Assert.Equal(imageCount, detectedCount);

        // Code-level auditor review: a total-pushed-based bound (e.g. totalSeconds/2) only catches a
        // stall from image 1 onward -- a stall starting at image 2 or 3 would still pass with most of
        // the buffer already trimmed by then. Bound per-image instead: real trimming should never let
        // the buffer grow past roughly one image's own worth of content plus real margin.
        var oneImageSeconds = allSamplesArray.Length / (double)SampleRate / imageCount;
        var bufferedSeconds = decoder.BufferedSampleCount / (double)SampleRate;
        Assert.True(
            bufferedSeconds < oneImageSeconds * 1.5,
            $"Expected buffered sample count to stay within ~1.5x one image's own duration ({oneImageSeconds:F1}s) " +
            $"across {imageCount} images, but {decoder.BufferedSampleCount} samples ({bufferedSeconds:F1}s) are still held -- trimming did not keep up.");
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
