using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;
using Yoniq.Core.Imaging;

namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Milestone-audit Phase 3 MUST 4 (spec/14-roadmap.md, "Phase 3 -- chain/integration audit"): isolated,
/// deterministic proof that the per-line decode cursor no longer accumulates rounding error across a
/// whole image. Legacy (Main.cpp:4133-4148, DrawSSTVNormal) keeps ONE continuous integer sample counter
/// for the whole transmission and derives every line's boundary via unrounded `double` division
/// (`y = int(double(n)/SSTVSET.m_TW)`) -- a single, non-compounding rounding against k*m_TW every
/// line, not a rounded-then-accumulated step. An earlier version of
/// AnalogFmSstvDecoder.TryProcessBuffer instead advanced `_consumedSamples` by
/// `(int)Math.Round(_effectiveSamplesPerLine)` every line, so line k started at k*round(E) -- the same
/// trimmed-vs-full mismatch MUST fix 3 fixed within a scan segment, one level up: between lines instead
/// of within one. This drift COMPOUNDS across the whole image (unlike legacy's own per-line rounding,
/// or this fix's own single non-compounding rounding of the running double accumulator) and is
/// invisible to Auto Slant, which tracks its own separate exact fractional grid.
///
/// Robot 72's Y channel is used because it has the largest predicted per-line rounding error at 11025Hz
/// among the fixture modes (~0.50 samples/line, ~120 samples/~25px accumulated drift by the last line)
/// and Y is the FIRST scan segment in its line shape, isolating this bug from any within-line
/// segment-order effect MUST fix 3 already covers (RY/BY, later in the same line, would also show this
/// bug but confound it with segment order -- Y does not).
/// </summary>
public class LineCursorRoundingTests
{
    private const int SampleRate = 11025;

    [Fact]
    public async Task Robot72_YChannel_LastLineStepEdgeDoesNotDriftFromFirstLine()
    {
        var mode = SstvModeRegistry.Robot72;
        var editColumn = mode.ImageWidth / 2;
        var image = CreateStepEdgeImage(mode.ImageWidth, mode.ImageHeight, editColumn);

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

        // Line 2, not line 0 -- gives the AGC/demod chain a couple of lines to settle past any
        // sync-anchor/AFC warm-up transient, per this project's own established practice
        // (PixelPitchSegmentBoundaryTests's own lineStartSkip note), before the per-line cursor has
        // had any real chance to accumulate rounding drift (2 lines * 0.5 samples/line is negligible).
        var earlyLineColumn = FindStepEdgeColumn(decodedImage!, lineIndex: 2);
        var lastLineColumn = FindStepEdgeColumn(decodedImage!, lineIndex: mode.ImageHeight - 1);

        // Measured, not assumed (pre-fix run of this exact test, reverting the fix and re-running
        // confirms this exactly): earlyLineColumn=161, lastLineColumn=136 -- a 25px drift by the last
        // line, matching the ~25px prediction from 0.50 samples/line * 239 lines / 4.756 samples/px
        // (Y channel's own samples-per-pixel) almost exactly. Post-fix (this test, current code):
        // earlyLineColumn=161, lastLineColumn=161 -- both identical, the drift is gone, and the
        // remaining 1px offset from the true editColumn (160) is ordinary settling noise, matching
        // PixelPitchSegmentBoundaryTests' own noise floor. 3px gives real margin above that noise
        // floor while still catching a regression back toward the old 25px drift.
        Assert.InRange(earlyLineColumn, editColumn - 3, editColumn + 3);
        Assert.InRange(lastLineColumn, editColumn - 3, editColumn + 3);
    }

    private static ArrayImageSource CreateStepEdgeImage(int width, int height, int editColumn)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = (byte)(x < editColumn ? 32 : 224); // a hard step, well clear of clamp/noise floors
                pixels[(y * width) + x] = new Rgb24(value, value, value); // neutral chroma -> R=G=B=Y
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }

    private static int FindStepEdgeColumn(IImageSource image, int lineIndex)
    {
        var scanline = image.GetScanline(lineIndex).ToArray();
        var midpoint = (32 + 224) / 2;
        const int lineStartSkip = 10; // skip early-line demod/AGC settling transients, unrelated to this fix
        const int sustainCount = 5; // require several CONSECUTIVE high samples, not one settling spike

        for (var x = lineStartSkip; x < image.Width - sustainCount; x++)
        {
            if (scanline[x].R < midpoint)
            {
                continue;
            }

            var sustained = true;
            for (var k = 1; k < sustainCount; k++)
            {
                if (scanline[x + k].R < midpoint)
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
