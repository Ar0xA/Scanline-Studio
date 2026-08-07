using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Milestone-audit MUST fix 3 (spec/14-roadmap.md, "Milestone audit, Phase 1+2"): isolated,
/// deterministic proof that scan-segment boundaries no longer drift early across a decoded line.
/// Legacy's real per-channel segment TRANSITIONS use the full, untrimmed nominal channel span --
/// only the pixel-index-within-a-segment mapping is trimmed, with any leftover tail time simply
/// discarded, never folded into where the next segment starts. An earlier version of
/// RgbSequentialScanlineDecoder/RobotScanlineDecoder/YCbCrSequentialScanlineDecoder/
/// YCbCrLinePairedScanlineDecoder accumulated the running position by the TRIMMED total across each
/// scan segment instead, so every segment after the first started early -- compounding across
/// channels. The real .mmv golden-vector fixtures (GoldenVectorTests) already show this fix's
/// real-world effect, but their content is a smooth gradient, which doesn't sharply reveal a few
/// pixels of horizontal misalignment the way a hard step edge does -- this file uses a hard step
/// edge specifically to make the drift directly measurable, isolated from any other DSP path
/// (AGC/AFC/Slant/noise) a real capture would also exercise.
/// </summary>
public class PixelPitchSegmentBoundaryTests
{
    private const int SampleRate = 11025;

    [Fact]
    public void RgbSequentialScanlineDecoder_PixelStartSample_UsesCeiling_NotRoundToNearest()
    {
        // ultracode audit finding #29: legacy's real pixel-boundary selection (the first integer
        // sample index where the mapped pixel index changes, given a monotone walk + "first wins"
        // gate) is effectively a CEILING, not round-to-nearest -- round-to-nearest can read up to
        // half a pixel into the PREVIOUS pixel's territory. Searches for a sample rate where
        // Math.Ceiling and Math.Round(MidpointRounding.ToEven) actually disagree for this mode's real
        // per-pixel step (avoiding a fragile hand-picked constant), then drives the real decoder and
        // asserts it used the CEILING value, not the round value, at that exact pixel.
        var mode = SstvModeRegistry.ScottieS1;
        var scan = mode.LineSegments.OfType<ScanSegment>().First();
        var trimFactor = SstvModeRegistry.GetPixelPitchTrimFactor(mode, scan.ChannelName);

        // segmentStartSample must match the REAL decoder's accumulation: the sum (in samples, at
        // whatever candidateRate is being tried) of every LineSegment's FULL duration before this
        // scan segment -- Scottie's real per-line order has a leading separator before its first
        // scan (G), so this is NOT 0.
        double SegmentStartSample(int rate) => mode.LineSegments.TakeWhile(s => s != scan).Sum(s => s.DurationMs / 1000.0 * rate);

        int? discriminatingPixelIndex = null;
        int sampleRate = 0;
        double expectedCeilBoundary = 0;
        double expectedRoundBoundary = 0;

        for (var candidateRate = 11025; candidateRate < 11225 && discriminatingPixelIndex is null; candidateRate++)
        {
            var segmentStartSample = SegmentStartSample(candidateRate);
            var perPixelDurationMs = scan.DurationMs / mode.ImageWidth * trimFactor;
            var pixelWalk = 0.0;
            for (var x = 1; x < mode.ImageWidth; x++)
            {
                pixelWalk += perPixelDurationMs / 1000.0 * candidateRate;
                var ceilBoundary = Math.Ceiling(segmentStartSample + pixelWalk);
                var roundBoundary = Math.Round(segmentStartSample + pixelWalk);
                if (ceilBoundary != roundBoundary)
                {
                    discriminatingPixelIndex = x;
                    sampleRate = candidateRate;
                    expectedCeilBoundary = ceilBoundary;
                    expectedRoundBoundary = roundBoundary;
                    break;
                }
            }
        }

        Assert.NotNull(discriminatingPixelIndex); // sanity: the search itself must find a real discriminating case
        Assert.NotEqual(expectedCeilBoundary, expectedRoundBoundary); // sanity: the two candidates really do differ

        var decoder = new RgbSequentialScanlineDecoder();
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        var recordedStartSamples = new List<int>();
        double RecordingStub(int index)
        {
            recordedStartSamples.Add(index);
            return 1900.0;
        }

        var reader = new PixelSampleReader(RecordingStub, ksbSamples: 1, lineEndSampleExclusive: int.MaxValue, luminanceMinHz: mode.LuminanceMinHz, neverPeakPicks: true);
        decoder.DecodeLine(mode, sampleRate, lineStartSample: 0, lineIndex: 0, reader, pixels);

        // The first scan segment's calls are recorded first, one per pixel, in order -- index
        // discriminatingPixelIndex (0-based pixel x) is recordedStartSamples[discriminatingPixelIndex].
        var actualStartSample = recordedStartSamples[discriminatingPixelIndex!.Value];
        Assert.Equal((int)expectedCeilBoundary, actualStartSample);
        Assert.NotEqual((int)expectedRoundBoundary, actualStartSample);
    }

    [Fact]
    public async Task MartinM1_RChannel_StepEdgeLandsAtCorrectColumn_NotShiftedByAccumulatedDrift()
    {
        // Martin M1's real per-line channel order is G, B, R (TX-verified this session's own
        // milestone-audit batch A) -- R is the THIRD scan segment, so it accumulates drift from
        // BOTH G's and B's own trimmed segments before it, making it the most sensitive of the
        // three channels to this bug.
        var mode = SstvModeRegistry.MartinM1;
        var editColumn = mode.ImageWidth / 2;
        var image = CreateStepEdgeImage(mode.ImageWidth, mode.ImageHeight, editColumn, channel: 'R');

        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, image))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(SampleRate);
        IImageSource? decodedImage = null;
        decoder.LineDecoded += update => decodedImage = update.Image;
        decoder.PushSamples(samples.ToArray());

        Assert.NotNull(decodedImage);

        var detectedColumn = FindStepEdgeColumn(decodedImage!, lineIndex: mode.ImageHeight / 2, channel: 'R');

        // Measured, not assumed (code-level review asked for the real numbers, not just a pass/fail):
        // true edit at column 160; pre-fix this detected at 165 (a 5px shift -- reverting the fix and
        // re-running confirms this exactly); post-fix, 162 (2px off, ordinary detector-threshold/
        // settling noise, not the old systematic drift). 2 gives just enough margin to pass the
        // measured post-fix value while still catching a regression back toward the old ~5px shift.
        Assert.InRange(detectedColumn, editColumn - 2, editColumn + 2);
    }

    [Fact]
    public async Task Pd90_Y2Channel_StepEdgeLandsAtCorrectColumn_NotShiftedByAccumulatedDrift()
    {
        // PD90's real per-line channel order is Y1, RY, BY, Y2 -- Y2 is the FOURTH scan segment,
        // measured (spec/14-roadmap.md, this fix's own milestone-audit finding) as the single
        // worst-case drift among all affected decoders (~4px) before this fix, since it accumulates
        // drift from three preceding trimmed segments.
        var mode = SstvModeRegistry.Pd90;
        var editColumn = mode.ImageWidth / 2;
        var image = CreateStepEdgeImage(mode.ImageWidth, mode.ImageHeight, editColumn, channel: 'Y');

        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, image))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(SampleRate);
        IImageSource? decodedImage = null;
        decoder.LineDecoded += update => decodedImage = update.Image;
        decoder.PushSamples(samples.ToArray());

        Assert.NotNull(decodedImage);

        // PD90 is line-paired (RowsPerTransmissionLine = 2) -- the ODD output row (lineIndex+1) is
        // Y2's own decode, matching YCbCrLinePairedScanlineDecoder.cs's own pixels[(lineIndex+1)...]
        // write. Y is a luma channel here, so detect via any RGB channel (a step in Y moves all
        // three together at neutral chroma).
        var oddRowIndex = (mode.ImageHeight / 2) | 1; // nearest odd row to the vertical middle
        var detectedColumn = FindStepEdgeColumn(decodedImage!, oddRowIndex, channel: 'R');

        // Measured, not assumed: true edit at column 160; pre-fix this detected at 165 (a 5px shift,
        // reverting the fix and re-running confirms this exactly -- close to, if not exactly, the
        // ~4px prediction, real settling noise accounts for the small difference); post-fix, 161 (1px
        // off, ordinary noise). 2 gives real margin above that noise floor while still catching a
        // regression back toward the old ~5px shift.
        Assert.InRange(detectedColumn, editColumn - 2, editColumn + 2);
    }

    // Attempted (not committed): dedicated step-edge tests for RobotScanlineDecoder/
    // YCbCrSequentialScanlineDecoder's own chroma channels, to close a code-level-review-flagged
    // coverage gap (only RgbSequential/YCbCrLinePaired are covered above). Investigation finding,
    // worth recording so it isn't rediscovered: Robot 72's chroma segments are only 69ms wide across
    // the full 320-pixel width (~0.215ms/pixel, ~2.4 samples at 11025Hz) vs. Martin M1's/PD90's own
    // ~0.45-0.53ms/pixel (~5-6 samples) -- a sharp step edge on a channel this narrow is dominated by
    // ordinary envelope-detector settling smear (the SAME real-time delay spans far more PIXELS when
    // each pixel represents so little time), not by this fix's own segment-START placement. A correct
    // test for these two families needs a differential (pre-fix-vs-post-fix column shift) design, not
    // an absolute-position check -- out of scope for this pass. The auditor's own code-level review
    // already verified these two families' segment-boundary formulas match legacy exactly via direct
    // source cross-check (spec/14-roadmap.md's MUST fix 3 entry has the full boundary-formula table),
    // and the golden-vector re-measurement shows both stay comfortably within their existing
    // tolerances -- this gap is tracked as a SHOULD-level follow-up, not left silently unaddressed.

    private static ArrayImageSource CreateStepEdgeImage(int width, int height, int editColumn, char channel)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = (byte)(x < editColumn ? 32 : 224); // a hard step, well clear of clamp/noise floors
                var pixel = new Rgb24(128, 128, 128);
                pixel = channel switch
                {
                    'R' => pixel with { R = value },
                    'G' => pixel with { G = value },
                    'B' => pixel with { B = value },
                    'Y' => new Rgb24(value, value, value), // neutral chroma -> R=G=B=Y
                    _ => throw new ArgumentOutOfRangeException(nameof(channel)),
                };
                pixels[y * width + x] = pixel;
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }

    private static int FindStepEdgeColumn(IImageSource image, int lineIndex, char channel)
    {
        var scanline = image.GetScanline(lineIndex).ToArray();
        var midpoint = (32 + 224) / 2;
        const int lineStartSkip = 10; // skip early-line demod/AGC settling transients, unrelated to this fix
        const int sustainCount = 5; // require several CONSECUTIVE high samples, not one settling spike

        int Value(int x) => channel switch
        {
            'R' => scanline[x].R,
            'G' => scanline[x].G,
            'B' => scanline[x].B,
            _ => throw new ArgumentOutOfRangeException(nameof(channel)),
        };

        for (var x = lineStartSkip; x < image.Width - sustainCount; x++)
        {
            if (Value(x) < midpoint)
            {
                continue;
            }

            var sustained = true;
            for (var k = 1; k < sustainCount; k++)
            {
                if (Value(x + k) < midpoint)
                {
                    sustained = false;
                    break;
                }
            }

            if (sustained)
            {
                return x;
            }
        }

        return image.Width; // never crossed -- caller's Assert.InRange will fail loudly
    }
}
