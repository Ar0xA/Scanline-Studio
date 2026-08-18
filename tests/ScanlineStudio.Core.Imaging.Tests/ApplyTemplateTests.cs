using SixLabors.ImageSharp;
using ImageSharpRgb24 = SixLabors.ImageSharp.PixelFormats.Rgb24;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Imaging.Tests;

/// <summary>Phase 0 (spec/15-template-designer.md, /home/artien/.claude/plans/wondrous-splashing-harbor.md)
/// — <see cref="ITransmitImagePreparer.ApplyTemplate"/>/<see cref="ITransmitImagePreparer.MeasureFittedFontSize"/>.
/// Mirrors <c>TransmitImagePreparerTests</c>'s own fixture/assertion style deliberately (self-contained
/// per-file helpers is this test project's established pattern, not shared base-class plumbing).</summary>
public sealed class ApplyTemplateTests
{
    [Fact]
    public async Task ApplyTemplate_EmptyDocument_ReturnsTheSameInstanceUnchanged()
    {
        var path = await WriteFixturePngAsync(4, 4, (_, _) => new ImageSharpRgb24(50, 60, 70));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 4, 4);
            var preparer = new TransmitImagePreparer(FontPath);

            var result = preparer.ApplyTemplate(source, new TemplateDocument(null, []));

            Assert.Same(source, result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyTemplate_AllElementsSkipped_ReturnsPixelIdenticalCopyNotTheSameInstance()
    {
        // A document that's non-empty but whose only element has zero size is a distinct case from
        // a truly empty document (Phase 0 plan-review round-2 finding) -- this pins the actual
        // implemented choice (a copy, since only the IsEmpty fast path returns the same instance).
        var path = await WriteFixturePngAsync(4, 4, (_, _) => new ImageSharpRgb24(10, 20, 30));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 4, 4);
            var preparer = new TransmitImagePreparer(FontPath);
            var document = new TemplateDocument(null, [
                new TemplateBoxElement(new NormalizedRect(0, 0, 0, 0.5), Z: 0, new Rgb24(255, 0, 0), null, 0),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            Assert.NotSame(source, result);
            AssertPixel(result, 0, 0, 10, 20, 30);
            AssertPixel(result, 3, 3, 10, 20, 30);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyTemplate_HigherZDrawsOnTopOfLowerZ()
    {
        // Blue (Z=1, higher) is listed FIRST and red (Z=0, lower) SECOND -- deliberately the
        // opposite of list order, so this can only pass if Z genuinely controls draw order, not an
        // accident of list position (code-review round-1 finding: the original version had both
        // Z-order and list-order agree, so it couldn't tell the two apart).
        var path = await WriteFixturePngAsync(8, 8, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 8, 8);
            var preparer = new TransmitImagePreparer(FontPath);
            var document = new TemplateDocument(null, [
                new TemplateBoxElement(new NormalizedRect(0, 0, 1, 1), Z: 1, new Rgb24(0, 0, 255), null, 0),
                new TemplateBoxElement(new NormalizedRect(0, 0, 1, 1), Z: 0, new Rgb24(255, 0, 0), null, 0),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            AssertPixel(result, 4, 4, 0, 0, 255);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyTemplate_EqualZ_StableSortPreservesListOrder()
    {
        // Two full-cover elements at the SAME Z: OrderBy's documented stability means list order is
        // preserved among ties, so the SECOND (blue) still ends up drawn last / on top -- this is
        // the "(Z, list index)" contract's own tie-break, not an accident of Z alone.
        var path = await WriteFixturePngAsync(8, 8, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 8, 8);
            var preparer = new TransmitImagePreparer(FontPath);
            var document = new TemplateDocument(null, [
                new TemplateBoxElement(new NormalizedRect(0, 0, 1, 1), Z: 0, new Rgb24(255, 0, 0), null, 0),
                new TemplateBoxElement(new NormalizedRect(0, 0, 1, 1), Z: 0, new Rgb24(0, 0, 255), null, 0),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            AssertPixel(result, 4, 4, 0, 0, 255);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyTemplate_NegativeZ_DrawsAboveTheBaseButBelowZeroElements()
    {
        // Z=-1 (blue) drawn first, above the base but below Z=0 (red) -- final visible pixel is
        // red, proving negative Z is included (not skipped/errored) and ordering holds.
        var path = await WriteFixturePngAsync(8, 8, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 8, 8);
            var preparer = new TransmitImagePreparer(FontPath);
            var document = new TemplateDocument(null, [
                new TemplateBoxElement(new NormalizedRect(0, 0, 1, 1), Z: 0, new Rgb24(255, 0, 0), null, 0),
                new TemplateBoxElement(new NormalizedRect(0, 0, 1, 1), Z: -1, new Rgb24(0, 0, 255), null, 0),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            AssertPixel(result, 4, 4, 255, 0, 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyTemplate_Box_FillsAndDrawsBorder()
    {
        var path = await WriteFixturePngAsync(20, 20, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 20, 20);
            var preparer = new TransmitImagePreparer(FontPath);
            // Border thickness relative to image HEIGHT (Phase 0 plan's stated convention) -- 0.2 *
            // 20px = 4px, comfortably thick enough to sample a pixel confidently inside it.
            var document = new TemplateDocument(null, [
                new TemplateBoxElement(new NormalizedRect(0.25, 0.25, 0.5, 0.5), Z: 0,
                    new Rgb24(0, 200, 0), new Rgb24(200, 0, 0), BorderThickness: 0.2),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            AssertPixel(result, 10, 10, 0, 200, 0); // center -- fill
            // Border-inset fix (below): the stroke is centered on a rect inset by half the 4px
            // thickness, so it spans x=[5,9) on the left edge -- x=7 sits solidly in the middle of
            // that band, away from both its outer edge (x=5, now anti-aliased right at Bounds) and
            // inner edge (x=9).
            AssertPixel(result, 7, 10, 200, 0, 0); // left edge of the box -- border
            // Border-inset fix (code-review round-1 finding): Draw(pen, path) centers the stroke on
            // the path, which would otherwise bleed BorderThickness/2 outside Bounds on every side.
            AssertNoNonBackgroundPixelOutsideBounds(result, new NormalizedRect(0.25, 0.25, 0.5, 0.5), background: (255, 255, 255));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyTemplate_BoxCornerRadius_RoundsTheCornersWhileKeepingCenterAndEdgeMidpointsFilled()
    {
        var path = await WriteFixturePngAsync(40, 40, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 40, 40);
            var preparer = new TransmitImagePreparer(FontPath);
            // A 20x20px box (0.5*40) with a 0.1*40=4px corner radius -- big enough that a corner
            // pixel is unambiguously outside the rounded path (the box's own literal corner is
            // 4*sqrt(2)~=5.66px from the arc's center, more than a full radius past the arc's own
            // edge, clear of the ~1px anti-aliasing band), small enough (well under half the box's
            // own 20px side) that real straight-edge sections remain in the middle of each edge for
            // the midpoint checks below. A radius of exactly half the box's shorter side was tried
            // first and rejected: it degenerates the whole box into an inscribed CIRCLE (all 4 arcs
            // meet exactly at the edge midpoints, zero straight-edge length left), which put the
            // midpoint checks right on the anti-aliased boundary -- caught by this test actually
            // failing against that geometry, not assumed. A smaller 20x20 image with the same
            // relative radius was ALSO tried and rejected -- its 2px radius put the box's own
            // corner pixel only ~0.83px past the arc's edge, inside the anti-aliasing band and
            // partially covered (184/255, neither clean fill nor clean background) -- also caught by
            // the test failing, not assumed.
            var document = new TemplateDocument(null, [
                new TemplateBoxElement(new NormalizedRect(0.25, 0.25, 0.5, 0.5), Z: 0,
                    new Rgb24(0, 200, 0), null, 0, CornerRadius: 0.1),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            AssertPixel(result, 20, 20, 0, 200, 0); // center -- fill
            AssertPixel(result, 20, 10, 0, 200, 0); // top edge midpoint -- inside the straight edge
            AssertPixel(result, 10, 20, 0, 200, 0); // left edge midpoint -- inside the straight edge
            AssertPixel(result, 10, 10, 255, 255, 255); // top-left corner -- rounded away, background
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyTemplate_BoxCornerRadiusZero_RendersIdenticallyToTheDefaultSquareCornerBox()
    {
        var path = await WriteFixturePngAsync(20, 20, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 20, 20);
            var preparer = new TransmitImagePreparer(FontPath);
            var squareDocument = new TemplateDocument(null, [
                new TemplateBoxElement(new NormalizedRect(0.25, 0.25, 0.5, 0.5), Z: 0, new Rgb24(0, 200, 0), null, 0),
            ]);
            var explicitZeroDocument = new TemplateDocument(null, [
                new TemplateBoxElement(new NormalizedRect(0.25, 0.25, 0.5, 0.5), Z: 0, new Rgb24(0, 200, 0), null, 0, CornerRadius: 0),
            ]);

            var squareResult = preparer.ApplyTemplate(source, squareDocument);
            var explicitZeroResult = preparer.ApplyTemplate(source, explicitZeroDocument);

            AssertPixel(squareResult, 5, 5, 0, 200, 0); // sharp corner -- filled, unlike the rounded test above
            AssertPixel(explicitZeroResult, 5, 5, 0, 200, 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyTemplate_BoxCornerRadius_AppliesToTheBorderTooInsetBySameHalfThickness()
    {
        var path = await WriteFixturePngAsync(20, 20, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 20, 20);
            var preparer = new TransmitImagePreparer(FontPath);
            var document = new TemplateDocument(null, [
                new TemplateBoxElement(new NormalizedRect(0.25, 0.25, 0.5, 0.5), Z: 0,
                    new Rgb24(0, 200, 0), new Rgb24(200, 0, 0), BorderThickness: 0.1, CornerRadius: 0.1),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            AssertPixel(result, 10, 10, 0, 200, 0); // center -- fill, unaffected by the rounded border
            AssertPixel(result, 10, 6, 200, 0, 0); // top edge midpoint -- inside the straight-edge border band
            AssertNoNonBackgroundPixelOutsideBounds(result, new NormalizedRect(0.25, 0.25, 0.5, 0.5), background: (255, 255, 255));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyTemplate_BoxOpacity_BlendsFillWithTheBaseRatherThanFullyReplacingIt()
    {
        var path = await WriteFixturePngAsync(4, 4, (_, _) => new ImageSharpRgb24(0, 0, 0));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 4, 4);
            var preparer = new TransmitImagePreparer(FontPath);
            var document = new TemplateDocument(null, [
                new TemplateBoxElement(new NormalizedRect(0, 0, 1, 1), Z: 0, new Rgb24(255, 255, 255), null, 0, Opacity: 0.5),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            var pixel = result.GetScanline(2)[2];
            Assert.True(pixel.R is > 0 and < 255, $"Expected a half-opacity white-on-black blend strictly between 0 and 255, got {pixel.R}.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyTemplate_Image_DrawsTheResizedSourceInsideItsOwnBounds()
    {
        // 8x8 white base; a 2x2 solid-red source image, stretched to fill the right half (bounds
        // X=0.5..1.0). Left half must stay white; right half must show red.
        var basePath = await WriteFixturePngAsync(8, 8, (_, _) => new ImageSharpRgb24(255, 255, 255));
        var elementPath = await WriteFixturePngAsync(2, 2, (_, _) => new ImageSharpRgb24(255, 0, 0));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(basePath, 8, 8);
            var elementSource = await new ImageFileLoader().LoadAsync(elementPath, 2, 2);
            var preparer = new TransmitImagePreparer(FontPath);
            var document = new TemplateDocument(null, [
                new TemplateImageElement(new NormalizedRect(0.5, 0, 0.5, 1), Z: 0, elementSource, ImageFitMode.Stretch),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            // Bounds X=0.5..1.0 on an 8px-wide image -- boundary at x=4. Sample right at the
            // boundary on each side (code-review round-1 nit: x=1/x=6 were far enough from the
            // x=4 boundary to not actually pin it).
            AssertPixel(result, 3, 4, 255, 255, 255); // last untouched column, left of the boundary
            AssertPixel(result, 4, 4, 255, 0, 0); // first drawn column, at the boundary
        }
        finally
        {
            File.Delete(basePath);
            File.Delete(elementPath);
        }
    }

    [Fact]
    public async Task ApplyTemplate_ImageWithOversizedBounds_ClampsResizeToTheDestinationInsteadOfAllocatingUnbounded()
    {
        // Code-review finding (Scanline Studio TX template designer Phase 2): an image element's
        // Bounds is normalized against the CROP rect
        // (TxImageEditorPaneViewModel.ProjectRectToCropRelative), so a full-frame element ("set as
        // background") combined with a small crop rect -- or simply a manually resized element
        // bigger than the working copy -- can project to a Bounds many times larger than 0..1.
        // Resizing the source to that raw (unclamped) pixel size would try to allocate gigabytes.
        // This pins that DrawTemplateImage completes without throwing/hanging and clamps the
        // resize target to the destination's own resolution regardless of how oversized Bounds is.
        var basePath = await WriteFixturePngAsync(8, 8, (_, _) => new ImageSharpRgb24(0, 255, 0));
        var elementPath = await WriteFixturePngAsync(2, 2, (_, _) => new ImageSharpRgb24(255, 0, 0));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(basePath, 8, 8);
            var elementSource = await new ImageFileLoader().LoadAsync(elementPath, 2, 2);
            var preparer = new TransmitImagePreparer(FontPath);
            // Width=50/Height=50 on an 8x8 base is a 400x400px resize target unclamped -- clamped,
            // it must stay at 8x8 (the destination's own size) and complete instantly.
            var document = new TemplateDocument(null, [
                new TemplateImageElement(new NormalizedRect(0, 0, 50, 50), Z: 0, elementSource, ImageFitMode.Stretch),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            // The whole 8x8 destination is covered by the (clamped, still oversized-in-intent)
            // element -- every pixel red, none of the green base showing through.
            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    AssertPixel(result, x, y, 255, 0, 0);
                }
            }
        }
        finally
        {
            File.Delete(basePath);
            File.Delete(elementPath);
        }
    }

    [Fact]
    public async Task ApplyTemplate_ImageCoverFit_CropsAwayContentAStretchWouldHaveKept()
    {
        // Code-review round-2 finding: the original fixture (uniform-per-row stripes) was
        // horizontally uniform, so Cover's horizontal crop was invisible -- Cover and Stretch
        // produced pixel-identical output, giving zero real coverage of the Cover/Stretch
        // distinction despite the test's name. Fixed with an ASYMMETRIC horizontal marker instead:
        // a 4x2 source, column 0 yellow, columns 1-3 blue, into a 4x4 (1:1) square Bounds.
        //   - Stretch (independent per-axis scale, no crop): column 0 stays a distinct yellow
        //     column at the LEFT EDGE of the final output.
        //   - Cover (uniform scale-to-cover then center-crop): scale factor is 2 (driven by the
        //     2:1 height ratio), so the source scales to 8x4 before a symmetric center-crop back to
        //     4x4 removes the leftmost AND rightmost 2 columns. Column 0 (yellow) maps to scaled
        //     columns [0,2) -- exactly the columns the crop removes -- so Cover's real output must
        //     be entirely blue, with NO yellow anywhere. That's the one outcome a Stretch (or a
        //     no-op/uncropped) implementation could never produce, which is what makes this fixture
        //     actually discriminating.
        var basePath = await WriteFixturePngAsync(8, 8, (_, _) => new ImageSharpRgb24(0, 255, 0));
        var elementPath = await WriteFixturePngAsync(4, 2, (x, _) => x == 0
            ? new ImageSharpRgb24(255, 255, 0)
            : new ImageSharpRgb24(0, 0, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(basePath, 8, 8);
            var elementSource = await new ImageFileLoader().LoadAsync(elementPath, 4, 2);
            var preparer = new TransmitImagePreparer(FontPath);
            var bounds = new NormalizedRect(0.25, 0.25, 0.5, 0.5); // 4x4 square region on an 8x8 base
            var document = new TemplateDocument(null, [
                new TemplateImageElement(bounds, Z: 0, elementSource, ImageFitMode.Cover),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            AssertNoNonBackgroundPixelOutsideBounds(result, bounds, background: (0, 255, 0));
            for (var y = 2; y < 6; y++)
            {
                for (var x = 2; x < 6; x++)
                {
                    var pixel = result.GetScanline(y)[x];
                    Assert.False(
                        pixel.R == 0 && pixel.G == 255 && pixel.B == 0,
                        $"Expected Cover to fill pixel ({x},{y}) inside Bounds completely; found background green showing through.");
                    Assert.False(
                        pixel.R == 255 && pixel.G == 255 && pixel.B == 0,
                        $"Expected Cover to have cropped away the source's leftmost (yellow) column entirely; found it at ({x},{y}).");
                }
            }
        }
        finally
        {
            File.Delete(basePath);
            File.Delete(elementPath);
        }
    }

    [Fact]
    public void MeasureFittedFontSize_OversizedText_ReturnsASmallerSizeThanRequested()
    {
        var preparer = new TransmitImagePreparer(FontPath);
        var font = new FontSpec("DejaVu Sans Mono", Size: 0.5); // 50% of image height -- deliberately huge
        const int imageHeightPx = 200;

        var fitted = preparer.MeasureFittedFontSize("A REASONABLY LONG CALLSIGN STRING", font, imageHeightPx, boundsWidthPx: 40, boundsHeightPx: 20);

        Assert.True(fitted < font.Size * imageHeightPx, $"Expected shrink-to-fit to reduce below the requested {font.Size * imageHeightPx}px, got {fitted}.");
    }

    [Fact]
    public void MeasureFittedFontSize_TextThatAlreadyFits_ReturnsTheRequestedSize()
    {
        var preparer = new TransmitImagePreparer(FontPath);
        var font = new FontSpec("DejaVu Sans Mono", Size: 0.05); // 5% of a 200px-tall image = 10px
        const int imageHeightPx = 200;

        var fitted = preparer.MeasureFittedFontSize("W", font, imageHeightPx, boundsWidthPx: 300, boundsHeightPx: 100);

        Assert.Equal(font.Size * imageHeightPx, fitted, precision: 3);
    }

    [Fact]
    public async Task ApplyTemplate_TextShrinksToFitASmallBox_NoPixelDrawnOutsideBounds()
    {
        // Code-review round-1 blocker: the original version of this test asserted ONLY absence
        // outside Bounds, which a regression that draws NOTHING AT ALL would also pass. Now also
        // asserts (a) the fit search actually shrank below the requested size and (b) something was
        // genuinely drawn inside Bounds.
        var path = await WriteFixturePngAsync(64, 64, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 64, 64);
            var preparer = new TransmitImagePreparer(FontPath);
            // FontSpec.Size 0.5 (huge, relative to the 64px-tall image) squeezed into a small box --
            // the fit search must shrink it down, and drawing must stay clipped to the box either way.
            var font = new FontSpec("DejaVu Sans Mono", 0.5);
            var bounds = new NormalizedRect(0.25, 0.25, 0.25, 0.1);
            var document = new TemplateDocument(null, [
                new TemplateTextElement(bounds, Z: 0, "HELLO", font, new Rgb24(255, 0, 0)),
            ]);

            var fitted = preparer.MeasureFittedFontSize("HELLO", font, imageHeightPx: 64, boundsWidthPx: 16, boundsHeightPx: 6);
            Assert.True(fitted < font.Size * 64, $"Expected the fit search to shrink below the requested {font.Size * 64}px, got {fitted}.");

            var result = preparer.ApplyTemplate(source, document);

            AssertNoNonBackgroundPixelOutsideBounds(result, bounds, background: (255, 255, 255));
            AssertAtLeastOneNonBackgroundPixelInsideBounds(result, bounds, background: (255, 255, 255));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyTemplate_RenderTimeOverflow_ClipsToBoundsRatherThanOverflowing()
    {
        // Text that cannot possibly fit even at the minimum font-size floor -- the overflow policy
        // (clip to Bounds, Phase 0 plan-review blocker 4) must still hold, not spill outside. Also
        // asserts something was actually drawn (clipped), not that rendering silently no-op'd
        // (code-review round-1 blocker -- same vacuous-test gap as the shrink test above).
        var path = await WriteFixturePngAsync(64, 64, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 64, 64);
            var preparer = new TransmitImagePreparer(FontPath);
            var bounds = new NormalizedRect(0.3, 0.3, 0.05, 0.03); // tiny box
            var document = new TemplateDocument(null, [
                new TemplateTextElement(bounds, Z: 0,
                    "THIS TEXT IS FAR TOO LONG TO EVER FIT IN THIS TINY BOX NO MATTER THE FONT SIZE",
                    new FontSpec("DejaVu Sans Mono", 0.2), new Rgb24(255, 0, 0)),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            AssertNoNonBackgroundPixelOutsideBounds(result, bounds, background: (255, 255, 255));
            AssertAtLeastOneNonBackgroundPixelInsideBounds(result, bounds, background: (255, 255, 255));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyTemplate_TextWithLargeStroke_RendersInsideBoundsWithoutThrowing()
    {
        // Phase 4: empirically verified (a direct pixel dump comparing WITH and WITHOUT the
        // fit-box-shrink correction, both run against this exact fixture) that
        // AssertNoNonBackgroundPixelOutsideBounds CANNOT distinguish correct from incorrect
        // behavior here -- DrawTemplateText's clip is unconditional, so stroke ink that would
        // otherwise extend past Bounds is silently CLIPPED there, not leaked outside it, with or
        // without the correction. The real, observable effect of the fix is a SMALLER fitted font
        // size (more room for the stroke before clipping kicks in) -- see
        // MeasureFittedFontSize_WithStroke_ShrinksFurtherThanWithoutOne below, which asserts that
        // directly and IS mutation-tested (this test and that one exercise the same shared
        // ShrinkFitBoxForStroke helper, so one direct test covers both call sites). This test stays
        // as a basic smoke check (renders without throwing, produces visible ink) -- a real
        // safety net, just not a bleed-detection one.
        var path = await WriteFixturePngAsync(64, 64, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 64, 64);
            var preparer = new TransmitImagePreparer(FontPath);
            var bounds = new NormalizedRect(0.2, 0.35, 0.3, 0.12);
            var document = new TemplateDocument(null, [
                new TemplateTextElement(
                    bounds, Z: 0, "W1AW", new FontSpec("DejaVu Sans Mono", 0.5), new Rgb24(0, 0, 255),
                    StrokeColor: new Rgb24(255, 0, 0), StrokeThickness: 0.15),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            AssertNoNonBackgroundPixelOutsideBounds(result, bounds, background: (255, 255, 255));
            AssertAtLeastOneNonBackgroundPixelInsideBounds(result, bounds, background: (255, 255, 255));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyTemplate_TextWithStroke_FillColorStillVisibleInside_NotFullyOverdrawnByStroke()
    {
        // Real-window verification finding (Phase 5): a Phase 5 ready-rack thumbnail rendered as
        // solid black -- zero non-background pixels anywhere -- for a plain white-fill/black-
        // stroke text element using the app's OWN SHIPPED DEFAULTS (FontSizeRelative 0.1,
        // StrokeThickness 0.02). Root cause, confirmed via an isolated ImageSharp-only repro
        // against the exact installed package version: the combined
        // `DrawText(options, text, Brush, Pen)` overload draws fill+stroke in one pass, and a Pen
        // centers its stroke ON the glyph outline (inward as well as outward) -- at this ratio the
        // inward growth from the stroke pass fully overdraws the fill for every glyph, leaving
        // nothing but a handful of antialiased edge pixels. Fixed by splitting into two draw calls
        // (stroke first, fill painted last on top -- see DrawGlyphs's own doc comment).
        // AssertAtLeastOneNonBackgroundPixelInsideBounds alone did NOT catch this (the
        // ApplyTemplate_TextWithLargeStroke_RendersInsideBoundsWithoutThrowing test above still
        // passed even with the bug present, since the STROKE's own outward ring is itself
        // "non-background" regardless of whether the fill survived) -- this test instead asserts a
        // pixel matching the exact FILL color specifically, which the bug's own symptom (fill fully
        // overdrawn) makes fail.
        var path = await WriteFixturePngAsync(120, 120, (_, _) => new ImageSharpRgb24(0, 0, 0));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 120, 120);
            var preparer = new TransmitImagePreparer(FontPath);
            var bounds = new NormalizedRect(0.35, 0.41, 0.3, 0.18);
            var fill = new Rgb24(255, 255, 255);
            var document = new TemplateDocument(null, [
                new TemplateTextElement(
                    bounds, Z: 0, "Text", new FontSpec("DejaVu Sans Mono", 0.1), fill,
                    StrokeColor: new Rgb24(0, 0, 0), StrokeThickness: 0.02),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            AssertAtLeastOnePixelOfColorInsideBounds(result, bounds, fill);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MeasureFittedFontSize_WithStroke_ShrinksFurtherThanWithoutOne()
    {
        // Companion to the bleed test above -- pins that the CANVAS-side measurement (this method)
        // applies the same fit-box-shrink correction as the real pipeline (DrawTemplateText), not
        // just the pipeline alone. A regression here is exactly the "canvas silently disagrees with
        // the transmitted image" bug class Phase 4 plan-review flagged as its second blocker.
        var preparer = new TransmitImagePreparer(FontPath);
        var font = new FontSpec("DejaVu Sans Mono", 0.3);

        var withoutStroke = preparer.MeasureFittedFontSize("W1AW", font, imageHeightPx: 64, boundsWidthPx: 40, boundsHeightPx: 20);
        var withStroke = preparer.MeasureFittedFontSize(
            "W1AW", font, imageHeightPx: 64, boundsWidthPx: 40, boundsHeightPx: 20, strokeThicknessRelative: 0.15);

        Assert.True(withStroke < withoutStroke, $"Expected the stroke allowance to shrink the fitted size below {withoutStroke}, got {withStroke}.");
    }

    [Fact]
    public void MeasureFittedFontSize_WithStroke_ShrinksTheFitBoxByTheFullStrokeThickness_NotHalf()
    {
        // Code-review finding: the "smaller than without a stroke" assertion above would still pass
        // if a future change shrunk the fit box by HALF the stroke thickness instead of the full
        // amount (a plausible-looking but wrong "just the outward growth per side" mistake) -- this
        // pins the actual magnitude by comparing against an equivalent bounds box shrunk by hand.
        // ImageSharp's Pen centers the stroke ON the glyph outline, so ink grows strokeThicknessPx/2
        // per side = strokeThicknessPx total across each axis, which is what ShrinkFitBoxForStroke
        // must subtract.
        var preparer = new TransmitImagePreparer(FontPath);
        var font = new FontSpec("DejaVu Sans Mono", 0.3);
        const int imageHeightPx = 64;
        const int boundsWidthPx = 40;
        const int boundsHeightPx = 20;
        const double strokeThicknessRelative = 0.15;
        var strokeThicknessPxRounded = (int)MathF.Round((float)(strokeThicknessRelative * imageHeightPx));

        var withStroke = preparer.MeasureFittedFontSize(
            "W1AW", font, imageHeightPx, boundsWidthPx, boundsHeightPx, strokeThicknessRelative);
        var equivalentPreShrunkBounds = preparer.MeasureFittedFontSize(
            "W1AW", font, imageHeightPx, boundsWidthPx - strokeThicknessPxRounded, boundsHeightPx - strokeThicknessPxRounded);

        // Both paths reach ComputeFittedFontSizePx with identical bounded-width/height integers, so
        // this is the same deterministic binary search both times -- exact equality, not a tolerance
        // comparison.
        Assert.Equal(equivalentPreShrunkBounds, withStroke);
    }

    [Fact]
    public async Task ApplyTemplate_TextWithShadow_ShadowColorVisibleInside_NoInkOutsideBounds()
    {
        // Phase 8: the shadow pre-pass draws a plain filled copy offset by (ShadowOffsetX,
        // ShadowOffsetY) BEFORE stroke/fill -- this pins both halves of that mechanism: the shadow
        // color is actually visible (not silently skipped or fully overdrawn by the later fill pass,
        // the same class of bug Phase 5's stroke-overdraw incident found), and the fit-box shrink for
        // shadow offset keeps ALL of it (shadow ink included, not just the fill) inside Bounds -- a
        // large offset relative to the bounds size is deliberately chosen so an unshrunk fit box
        // would visibly bleed the shadow copy outside Bounds if the shrink were missing or wrong.
        var path = await WriteFixturePngAsync(120, 120, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 120, 120);
            var preparer = new TransmitImagePreparer(FontPath);
            var bounds = new NormalizedRect(0.3, 0.3, 0.4, 0.4);
            var fill = new Rgb24(0, 0, 255);
            var shadow = new Rgb24(255, 0, 0);
            var document = new TemplateDocument(null, [
                new TemplateTextElement(
                    bounds, Z: 0, "W1AW", new FontSpec("DejaVu Sans Mono", 0.2), fill,
                    ShadowColor: shadow, ShadowOffsetX: 0.08, ShadowOffsetY: 0.08),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            AssertAtLeastOnePixelOfColorInsideBounds(result, bounds, shadow);
            AssertNoNonBackgroundPixelOutsideBounds(result, bounds, background: (255, 255, 255));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MeasureFittedFontSize_WithShadowOffset_ShrinksFurtherThanWithoutOne()
    {
        var preparer = new TransmitImagePreparer(FontPath);
        var font = new FontSpec("DejaVu Sans Mono", 0.3);

        var withoutShadow = preparer.MeasureFittedFontSize("W1AW", font, imageHeightPx: 64, boundsWidthPx: 40, boundsHeightPx: 20);
        var withShadow = preparer.MeasureFittedFontSize(
            "W1AW", font, imageHeightPx: 64, boundsWidthPx: 40, boundsHeightPx: 20,
            shadowOffsetXRelative: 0.15, shadowOffsetYRelative: 0.15);

        Assert.True(withShadow < withoutShadow, $"Expected the shadow allowance to shrink the fitted size below {withoutShadow}, got {withShadow}.");
    }

    // Auditor usability review follow-up (2026-08-18): the "3D"/stack text effect (legacy YONIQ's
    // CBStack/m_StackPara, TextIn.cpp/Draw.cpp -- a stepped stack of offset solid-color copies, NOT a
    // real 3D transform). Same test shapes as the shadow tests just above, since it's the same class
    // of "offset copy must stay inside Bounds" fit-box-shrink requirement.

    [Fact]
    public async Task ApplyTemplate_TextWithStack_StackColorVisibleInside_NoInkOutsideBounds()
    {
        var path = await WriteFixturePngAsync(120, 120, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 120, 120);
            var preparer = new TransmitImagePreparer(FontPath);
            var bounds = new NormalizedRect(0.3, 0.3, 0.4, 0.4);
            var fill = new Rgb24(0, 0, 255);
            var stack = new Rgb24(0, 255, 0);
            var document = new TemplateDocument(null, [
                new TemplateTextElement(
                    bounds, Z: 0, "W1AW", new FontSpec("DejaVu Sans Mono", 0.2), fill,
                    StackColor: stack, StackStepX: 0.08, StackStepY: 0.08),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            AssertAtLeastOnePixelOfColorInsideBounds(result, bounds, stack);
            AssertNoNonBackgroundPixelOutsideBounds(result, bounds, background: (255, 255, 255));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MeasureFittedFontSize_WithStackStep_ShrinksFurtherThanWithoutOne()
    {
        var preparer = new TransmitImagePreparer(FontPath);
        var font = new FontSpec("DejaVu Sans Mono", 0.3);

        var withoutStack = preparer.MeasureFittedFontSize("W1AW", font, imageHeightPx: 64, boundsWidthPx: 40, boundsHeightPx: 20);
        var withStack = preparer.MeasureFittedFontSize(
            "W1AW", font, imageHeightPx: 64, boundsWidthPx: 40, boundsHeightPx: 20,
            stackStepXRelative: 0.15, stackStepYRelative: 0.15);

        Assert.True(withStack < withoutStack, $"Expected the stack allowance to shrink the fitted size below {withoutStack}, got {withStack}.");
    }

    [Fact]
    public async Task ApplyTemplate_TextWithZeroStackStep_DrawsNoExtraCopies()
    {
        // StackColor set but StackStepX/Y both 0 -- legacy's own m_StackPara-truthiness guard has a
        // real equivalent here: the copy-count computation floors at 0 (MaxStackCopies clamp's own
        // Math.Round(0) branch), so the for-loop body never runs -- a no-op, not a divide-by-zero.
        var path = await WriteFixturePngAsync(120, 120, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 120, 120);
            var preparer = new TransmitImagePreparer(FontPath);
            var bounds = new NormalizedRect(0.3, 0.3, 0.4, 0.4);
            var document = new TemplateDocument(null, [
                new TemplateTextElement(
                    bounds, Z: 0, "W1AW", new FontSpec("DejaVu Sans Mono", 0.2), new Rgb24(0, 0, 255),
                    StackColor: new Rgb24(0, 255, 0), StackStepX: 0, StackStepY: 0),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            AssertNoNonBackgroundPixelOutsideBounds(result, bounds, background: (255, 255, 255));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyTemplate_RotatedText_VisibleInside_NoInkOutsideBounds()
    {
        // Phase 8 plan-review blocker: rotation-vs-clip policy is "shrink the fit box," not "widen
        // the clip" -- ShrinkFitBoxForRotation's own doc comment derives the exact formula; this is
        // the regression test that formula is pinned against (written before trusting the derivation
        // on paper alone, same discipline as the stroke-bleed test). 45 degrees on a non-square
        // bounds box is deliberately the case the formula's own doc comment calls out as the
        // genuinely-conservative (not just trivially-safe) case.
        var path = await WriteFixturePngAsync(120, 120, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 120, 120);
            var preparer = new TransmitImagePreparer(FontPath);
            var bounds = new NormalizedRect(0.25, 0.35, 0.5, 0.3);
            var fill = new Rgb24(0, 200, 0);
            var document = new TemplateDocument(null, [
                new TemplateTextElement(
                    bounds, Z: 0, "W1AW", new FontSpec("DejaVu Sans Mono", 0.2), fill, RotationDegrees: 45),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            AssertAtLeastOnePixelOfColorInsideBounds(result, bounds, fill);
            AssertNoNonBackgroundPixelOutsideBounds(result, bounds, background: (255, 255, 255));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyTemplate_RotatedText_DirectionAndMagnitudeAffectInkPosition_NotJustANoOp()
    {
        // Code-review finding: the test above (ink-inside/none-outside) cannot distinguish an actual
        // rotation from an accidental no-op, a wrong-sign rotation, or a composite point computed
        // from the ORIGINAL (pre-rotation) size instead of the rotated one -- all of those would
        // still pass "some ink inside Bounds, none outside." This test instead compares the LEFT-half
        // vs RIGHT-half ink centroid (row index) of a wide horizontal text run across 0/+45/-45
        // degrees: an unrotated run has both halves roughly level (near-zero skew); a real rotation
        // tilts the run, so the two halves' centroids diverge, and +45 vs -45 must diverge in
        // OPPOSITE directions. Deliberately convention-agnostic about which sign is "clockwise" (that
        // was flagged as an unverified assumption during code-review, not something to hard-code a
        // guess for) -- only that +45 and -45 are each other's mirror image, which any correct
        // sign-respecting rotation must produce regardless of which way is "positive."
        var path = await WriteFixturePngAsync(200, 120, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 200, 120);
            var preparer = new TransmitImagePreparer(FontPath);
            var bounds = new NormalizedRect(0.1, 0.35, 0.8, 0.3);
            var fill = new Rgb24(0, 200, 0);

            double SkewAt(double degrees)
            {
                var document = new TemplateDocument(null, [
                    new TemplateTextElement(
                        bounds, Z: 0, "WWWWWWWW", new FontSpec("DejaVu Sans Mono", 0.15), fill, RotationDegrees: degrees),
                ]);
                var result = preparer.ApplyTemplate(source, document);
                return LeftMinusRightCentroidY(result, bounds, fill);
            }

            var skewAt0 = SkewAt(0);
            var skewAtPlus45 = SkewAt(45);
            var skewAtMinus45 = SkewAt(-45);

            Assert.True(
                Math.Abs(skewAtPlus45) > Math.Abs(skewAt0) + 1,
                $"Expected +45° to visibly tilt the text (skew={skewAtPlus45}) more than unrotated (skew={skewAt0}).");
            Assert.True(
                Math.Abs(skewAtMinus45) > Math.Abs(skewAt0) + 1,
                $"Expected -45° to visibly tilt the text (skew={skewAtMinus45}) more than unrotated (skew={skewAt0}).");
            Assert.True(
                Math.Sign(skewAtPlus45) != Math.Sign(skewAtMinus45),
                $"Expected +45° (skew={skewAtPlus45}) and -45° (skew={skewAtMinus45}) to tilt in opposite directions.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Average row index of LEFT-half fill-colored pixels minus the same for the RIGHT half,
    /// within <paramref name="bounds"/> -- near zero for level (unrotated) text, non-zero and
    /// direction-sensitive once the text run is tilted. Returns 0 (no signal) if either half has no
    /// matching ink, rather than throwing -- a real result of some rotation angles/fit sizes, not
    /// something callers need to special-case.</summary>
    private static double LeftMinusRightCentroidY(IImageSource image, NormalizedRect bounds, Rgb24 color)
    {
        var minX = Math.Clamp((int)(bounds.X * image.Width), 0, image.Width - 1);
        var minY = Math.Clamp((int)(bounds.Y * image.Height), 0, image.Height - 1);
        var maxX = Math.Clamp((int)((bounds.X + bounds.Width) * image.Width), 0, image.Width);
        var maxY = Math.Clamp((int)((bounds.Y + bounds.Height) * image.Height), 0, image.Height);
        var midX = (minX + maxX) / 2;

        double leftSumY = 0, rightSumY = 0;
        var leftCount = 0;
        var rightCount = 0;

        for (var y = minY; y < maxY; y++)
        {
            var row = image.GetScanline(y);
            for (var x = minX; x < maxX; x++)
            {
                if (row[x].R != color.R || row[x].G != color.G || row[x].B != color.B)
                {
                    continue;
                }

                if (x < midX)
                {
                    leftSumY += y;
                    leftCount++;
                }
                else
                {
                    rightSumY += y;
                    rightCount++;
                }
            }
        }

        return leftCount == 0 || rightCount == 0 ? 0 : (leftSumY / leftCount) - (rightSumY / rightCount);
    }

    [Fact]
    public void MeasureFittedFontSize_WithRotation_ShrinksFurtherThanWithoutOne()
    {
        var preparer = new TransmitImagePreparer(FontPath);
        var font = new FontSpec("DejaVu Sans Mono", 0.3);

        var withoutRotation = preparer.MeasureFittedFontSize("W1AW", font, imageHeightPx: 64, boundsWidthPx: 40, boundsHeightPx: 20);
        var withRotation = preparer.MeasureFittedFontSize(
            "W1AW", font, imageHeightPx: 64, boundsWidthPx: 40, boundsHeightPx: 20, rotationDegrees: 45);

        Assert.True(withRotation < withoutRotation, $"Expected the rotation allowance to shrink the fitted size below {withoutRotation}, got {withRotation}.");
    }

    [Fact]
    public void MeasureFittedFontSize_ZeroRotation_IsANoOp()
    {
        // ShrinkFitBoxForRotation's own doc comment claims k=1 exactly at 0 degrees -- pins that
        // claim directly (every existing, pre-Phase-8 element implicitly passes rotationDegrees: 0,
        // so this is also what proves Phase 8 didn't quietly shrink every non-rotated element too).
        var preparer = new TransmitImagePreparer(FontPath);
        var font = new FontSpec("DejaVu Sans Mono", 0.3);

        var withoutRotationParam = preparer.MeasureFittedFontSize("W1AW", font, imageHeightPx: 64, boundsWidthPx: 40, boundsHeightPx: 20);
        var withZeroRotation = preparer.MeasureFittedFontSize(
            "W1AW", font, imageHeightPx: 64, boundsWidthPx: 40, boundsHeightPx: 20, rotationDegrees: 0);

        Assert.Equal(withoutRotationParam, withZeroRotation);
    }

    [Fact]
    public async Task ApplyTemplate_TextWithHorizontalGradient_LeftInkIsReddishRightInkIsBluish_NoInkOutsideBounds()
    {
        // Confirms the gradient brush is actually applied across the glyph run (not silently falling
        // back to a solid Color) by checking the LEFTMOST ink pixel is redder than blue and the
        // RIGHTMOST ink pixel is bluer than red -- a directional/relative check, not an exact-stop-
        // color match: the fitted glyph run is centered within Bounds and doesn't necessarily reach
        // Bounds' own edges (side bearing, fit-to-box padding), so the true endpoint stop colors
        // (pure red at offset 0, pure blue at offset 1) aren't guaranteed to land on actual ink --
        // this was confirmed empirically (an earlier version of this test asserting exact stop colors
        // failed even though the gradient was rendering correctly, per manual pixel inspection).
        var path = await WriteFixturePngAsync(160, 120, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 160, 120);
            var preparer = new TransmitImagePreparer(FontPath);
            var bounds = new NormalizedRect(0.15, 0.3, 0.7, 0.4);
            var stopA = new Rgb24(255, 0, 0);
            var stopB = new Rgb24(0, 0, 255);
            var gradient = new TextGradient(TextGradientKind.Horizontal, [new GradientColorStop(0f, stopA), new GradientColorStop(1f, stopB)]);
            var document = new TemplateDocument(null, [
                new TemplateTextElement(
                    bounds, Z: 0, "WWWWWW", new FontSpec("DejaVu Sans Mono", 0.25), new Rgb24(0, 0, 0), Gradient: gradient),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            AssertLeftmostInkPixelIsRedderThanRightmostInkPixel(result, bounds, background: (255, 255, 255));
            AssertNoNonBackgroundPixelOutsideBounds(result, bounds, background: (255, 255, 255));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyTemplate_TextWithEmptyGradientStops_FallsBackToElementColor()
    {
        // TextGradient's own doc comment: empty Stops is a degenerate one-color gradient, not an
        // error -- falls back to the element's own solid Color.
        var path = await WriteFixturePngAsync(120, 120, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 120, 120);
            var preparer = new TransmitImagePreparer(FontPath);
            var bounds = new NormalizedRect(0.3, 0.3, 0.4, 0.4);
            var fill = new Rgb24(0, 200, 0);
            var gradient = new TextGradient(TextGradientKind.Horizontal, []);
            var document = new TemplateDocument(null, [
                new TemplateTextElement(bounds, Z: 0, "W1AW", new FontSpec("DejaVu Sans Mono", 0.2), fill, Gradient: gradient),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            AssertAtLeastOnePixelOfColorInsideBounds(result, bounds, fill);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Auditor usability review follow-up (2026-08-18): "bitmap mask" text fill -- legacy YONIQ's real
    // RGGrade radio-group option (TextIn.cpp/BitMask.cpp), a tiled 2-color pattern brush for text
    // fill, NOT a glyph-shaped stencil mask despite the name -- same TextGradient/GradientKind slot
    // as Horizontal/Vertical above (see TextGradientKind.BitmapPattern's own doc comment).

    [Fact]
    public async Task ApplyTemplate_TextWithBitmapPatternFill_BothPatternColorsPresentInsideBounds_NoInkOutsideBounds()
    {
        var path = await WriteFixturePngAsync(160, 120, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 160, 120);
            var preparer = new TransmitImagePreparer(FontPath);
            var bounds = new NormalizedRect(0.15, 0.3, 0.7, 0.4);
            var foreColor = new Rgb24(255, 0, 0);
            var backColor = new Rgb24(0, 0, 255);
            var gradient = new TextGradient(TextGradientKind.BitmapPattern, [new GradientColorStop(0f, foreColor), new GradientColorStop(1f, backColor)]);
            var document = new TemplateDocument(null, [
                new TemplateTextElement(
                    bounds, Z: 0, "WWWWWWWW", new FontSpec("DejaVu Sans Mono", 0.25), new Rgb24(0, 0, 0), Gradient: gradient),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            // A real tiled pattern shows BOTH colors on the glyph run -- a fallback that silently
            // degenerated to a solid brush would only ever show one.
            AssertAtLeastOnePixelOfColorInsideBounds(result, bounds, foreColor);
            AssertAtLeastOnePixelOfColorInsideBounds(result, bounds, backColor);
            AssertNoNonBackgroundPixelOutsideBounds(result, bounds, background: (255, 255, 255));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MeasureFittedFontSize_NegativeStrokeThickness_DoesNotGrowTheFitBoxPastUnstrokedSize()
    {
        // Code-review finding: nothing upstream (the AXAML TextBox binding) validates
        // StrokeThickness, so a negative value must not be allowed to GROW the fit box past what an
        // unstroked element would get -- ShrinkFitBoxForStroke's own arithmetic
        // (bounds - strokeThicknessPxRounded) would otherwise ADD magnitude for a negative input,
        // letting the fit search pick a size larger than Bounds while DrawGlyphs's own
        // strokeThicknessPx > 0 gate draws no outline to fill that extra allowance -- silently
        // hard-clipped, oversized transmitted text with no outline, the worst combination.
        var preparer = new TransmitImagePreparer(FontPath);
        var font = new FontSpec("DejaVu Sans Mono", 0.3);

        var withoutStroke = preparer.MeasureFittedFontSize("W1AW", font, imageHeightPx: 64, boundsWidthPx: 40, boundsHeightPx: 20);
        var withNegativeStroke = preparer.MeasureFittedFontSize(
            "W1AW", font, imageHeightPx: 64, boundsWidthPx: 40, boundsHeightPx: 20, strokeThicknessRelative: -0.15);

        Assert.True(
            withNegativeStroke <= withoutStroke,
            $"Expected a negative stroke thickness to be clamped (never exceed the unstroked fitted size {withoutStroke}), got {withNegativeStroke}.");
    }

    [Fact]
    public async Task ApplyTemplate_TextWithNoStroke_RendersIdenticallyToBeforeThisFeature()
    {
        // Regression guard: StrokeColor defaults to null on TemplateTextElement, so an existing
        // (pre-Phase-4) caller that never sets it must render through the SAME plain Color-overload
        // DrawText call as before -- not a behavior change hiding behind a default-null stroke.
        var path = await WriteFixturePngAsync(32, 32, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 32, 32);
            var preparer = new TransmitImagePreparer(FontPath);
            var bounds = new NormalizedRect(0.1, 0.1, 0.8, 0.4);
            var document = new TemplateDocument(null, [
                new TemplateTextElement(bounds, Z: 0, "HI", new FontSpec("DejaVu Sans Mono", 0.3), new Rgb24(0, 0, 255)),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            AssertNoNonBackgroundPixelOutsideBounds(result, bounds, background: (255, 255, 255));
            AssertAtLeastOneNonBackgroundPixelInsideBounds(result, bounds, background: (255, 255, 255));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Auditor usability review follow-up (2026-08-18): "missing yoniq-style text effects" (from the
    // user's original 2026-08-16 checklist: color/shadow/gradient/rotation shipped in Phase 4/8,
    // bold/italic never started). Bold/Italic glyph VARIANTS require real, separately-bundled font
    // FILES (SixLabors.Fonts.FontFamily.CreateFont(size, style) throws if the requested style has no
    // matching registered file for that family -- verified via reflection + a real render before this
    // was built, not assumed) -- these tests pin that DejaVu Sans Mono AND Barlow both actually have
    // all 4 variants registered and reachable end-to-end, not just that the enum value compiles.

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ApplyTemplate_TextBoldItalic_DefaultFamily_RendersWithoutThrowingForEveryCombination(bool bold, bool italic)
    {
        var path = await WriteFixturePngAsync(64, 64, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 64, 64);
            var preparer = new TransmitImagePreparer(FontPath);
            var bounds = new NormalizedRect(0.1, 0.1, 0.8, 0.4);
            var document = new TemplateDocument(null, [
                new TemplateTextElement(bounds, Z: 0, "HI", new FontSpec(preparer.AvailableFontFamilies[0], 0.3, Bold: bold, Italic: italic), new Rgb24(0, 0, 255)),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            AssertAtLeastOneNonBackgroundPixelInsideBounds(result, bounds, background: (255, 255, 255));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ApplyTemplate_TextBoldItalic_BarlowFamily_RendersWithoutThrowingForEveryCombination(bool bold, bool italic)
    {
        var path = await WriteFixturePngAsync(64, 64, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 64, 64);
            var preparer = new TransmitImagePreparer(FontPath);
            var bounds = new NormalizedRect(0.1, 0.1, 0.8, 0.4);
            var document = new TemplateDocument(null, [
                new TemplateTextElement(bounds, Z: 0, "HI", new FontSpec("Barlow", 0.3, Bold: bold, Italic: italic), new Rgb24(0, 0, 255)),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            AssertAtLeastOneNonBackgroundPixelInsideBounds(result, bounds, background: (255, 255, 255));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyTemplate_TextWithFamilyName_ResolvesTheRequestedFontNotJustTheDefault()
    {
        // Phase 4: ResolveFontFamily became a real per-family lookup -- this pins that requesting
        // "Barlow" (the second bundled family) actually renders something (doesn't throw/no-op),
        // proving the FontCollection.TryGet path is reachable end-to-end, not just compiling.
        var path = await WriteFixturePngAsync(64, 64, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 64, 64);
            var preparer = new TransmitImagePreparer(FontPath);
            Assert.Contains("Barlow", preparer.AvailableFontFamilies);
            var bounds = new NormalizedRect(0.1, 0.1, 0.8, 0.4);
            var document = new TemplateDocument(null, [
                new TemplateTextElement(bounds, Z: 0, "HI", new FontSpec("Barlow", 0.3), new Rgb24(0, 0, 255)),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            AssertAtLeastOneNonBackgroundPixelInsideBounds(result, bounds, background: (255, 255, 255));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyTemplate_TextWithUnknownFamilyName_FallsBackToTheDefaultFontRatherThanThrowing()
    {
        var path = await WriteFixturePngAsync(64, 64, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 64, 64);
            var preparer = new TransmitImagePreparer(FontPath);
            var bounds = new NormalizedRect(0.1, 0.1, 0.8, 0.4);
            var document = new TemplateDocument(null, [
                new TemplateTextElement(bounds, Z: 0, "HI", new FontSpec("Nonexistent Font Family", 0.3), new Rgb24(0, 0, 255)),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            AssertAtLeastOneNonBackgroundPixelInsideBounds(result, bounds, background: (255, 255, 255));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyTemplate_OutOfBoundsElement_DoesNotThrowAndLeavesImageUnchanged()
    {
        // Mirrors ApplyOverlay_FarOutOfBoundsPosition_DoesNotThrowAndLeavesImageUnchanged's own
        // precedent (TransmitImagePreparerTests.cs) -- element Bounds are deliberately NOT clamped
        // into [0,1] (Phase 0's own stated design), so an element positioned entirely off-canvas
        // must be a silent no-op, not a throw.
        var path = await WriteFixturePngAsync(16, 16, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 16, 16);
            var preparer = new TransmitImagePreparer(FontPath);
            var document = new TemplateDocument(null, [
                new TemplateBoxElement(new NormalizedRect(-5, -5, 1, 1), Z: 0, new Rgb24(255, 0, 0), null, 0),
                new TemplateTextElement(new NormalizedRect(10, 10, 1, 1), Z: 1, "X", new FontSpec("DejaVu Sans Mono", 0.5), new Rgb24(255, 0, 0)),
            ]);

            var result = preparer.ApplyTemplate(source, document);

            Assert.Equal(16, result.Width);
            Assert.Equal(16, result.Height);
            AssertPixel(result, 0, 0, 255, 255, 255);
            AssertPixel(result, 8, 8, 255, 255, 255);
            AssertPixel(result, 15, 15, 255, 255, 255);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyOverlayAndApplyTemplate_ShareTheSameRenderPath_ForNonWrappingText()
    {
        // Code-review round-1 finding: the existing ApplyOverlay tests are necessary but not
        // sufficient evidence the DrawGlyphs refactor didn't change ApplyOverlay's own rendering --
        // they only check "some non-white pixel near the center," not real pixel parity. For SHORT
        // text that wouldn't wrap under EITHER WrappingLength regime (source.Width vs -1), the two
        // call sites should produce IDENTICAL output -- this is a real cross-check that doesn't
        // depend on git history to compare against a pre-refactor version.
        var overlayPath = await WriteFixturePngAsync(32, 32, (_, _) => new ImageSharpRgb24(255, 255, 255));
        var templatePath = await WriteFixturePngAsync(32, 32, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var overlaySource = await new ImageFileLoader().LoadAsync(overlayPath, 32, 32);
            var templateSource = await new ImageFileLoader().LoadAsync(templatePath, 32, 32);
            var preparer = new TransmitImagePreparer(FontPath);

            var overlayResult = preparer.ApplyOverlay(overlaySource, new ImageOverlay([
                new ImageOverlayElement("W", 0.5, 0.5, FontSizeRelative: 0.3, new Rgb24(255, 0, 0)),
            ]));

            // Bounds large enough that the fit search never shrinks below FontSpec.Size, so the
            // rendered font size matches ApplyOverlay's FontSizeRelative * height exactly, and
            // "W" is short enough that WrappingLength=-1 vs. WrappingLength=32 both leave it
            // unwrapped -- the one condition under which a byte-for-byte comparison is actually a
            // valid test, not an assertion that two deliberately-different behaviors coincide.
            var templateResult = preparer.ApplyTemplate(templateSource, new TemplateDocument(null, [
                new TemplateTextElement(new NormalizedRect(0, 0, 1, 1), Z: 0, "W", new FontSpec("DejaVu Sans Mono", 0.3), new Rgb24(255, 0, 0)),
            ]));

            // Span-typed locals (ReadOnlySpan<Rgb24> from GetScanline) cannot be declared inside an
            // async method body -- same C# restriction this test project's own established
            // AssertPixel comment already documents -- so the comparison is a synchronous helper.
            AssertImagesAreIdentical(overlayResult, templateResult);
        }
        finally
        {
            File.Delete(overlayPath);
            File.Delete(templatePath);
        }
    }

    private static void AssertPixel(IImageSource image, int x, int y, byte r, byte g, byte b)
    {
        var pixel = image.GetScanline(y)[x];
        Assert.Equal(r, pixel.R);
        Assert.Equal(g, pixel.G);
        Assert.Equal(b, pixel.B);
    }

    private static void AssertImagesAreIdentical(IImageSource expected, IImageSource actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);

        for (var y = 0; y < expected.Height; y++)
        {
            var expectedRow = expected.GetScanline(y);
            var actualRow = actual.GetScanline(y);
            for (var x = 0; x < expected.Width; x++)
            {
                Assert.True(
                    expectedRow[x].R == actualRow[x].R && expectedRow[x].G == actualRow[x].G && expectedRow[x].B == actualRow[x].B,
                    $"Expected identical rendering at ({x},{y}); expected=({expectedRow[x].R},{expectedRow[x].G},{expectedRow[x].B}), actual=({actualRow[x].R},{actualRow[x].G},{actualRow[x].B}).");
            }
        }
    }

    private static void AssertAtLeastOneNonBackgroundPixelInsideBounds(IImageSource image, NormalizedRect bounds, (byte R, byte G, byte B) background)
    {
        var minX = Math.Clamp((int)Math.Floor(bounds.X * image.Width), 0, image.Width - 1);
        var minY = Math.Clamp((int)Math.Floor(bounds.Y * image.Height), 0, image.Height - 1);
        var maxX = Math.Clamp((int)Math.Ceiling((bounds.X + bounds.Width) * image.Width), 0, image.Width);
        var maxY = Math.Clamp((int)Math.Ceiling((bounds.Y + bounds.Height) * image.Height), 0, image.Height);

        for (var y = minY; y < maxY; y++)
        {
            var row = image.GetScanline(y);
            for (var x = minX; x < maxX; x++)
            {
                if (row[x].R != background.R || row[x].G != background.G || row[x].B != background.B)
                {
                    return;
                }
            }
        }

        Assert.Fail($"Expected at least one non-background pixel inside Bounds ({bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}); found none.");
    }

    private static void AssertAtLeastOnePixelOfColorInsideBounds(IImageSource image, NormalizedRect bounds, Rgb24 color)
    {
        var minX = Math.Clamp((int)Math.Floor(bounds.X * image.Width), 0, image.Width - 1);
        var minY = Math.Clamp((int)Math.Floor(bounds.Y * image.Height), 0, image.Height - 1);
        var maxX = Math.Clamp((int)Math.Ceiling((bounds.X + bounds.Width) * image.Width), 0, image.Width);
        var maxY = Math.Clamp((int)Math.Ceiling((bounds.Y + bounds.Height) * image.Height), 0, image.Height);

        for (var y = minY; y < maxY; y++)
        {
            var row = image.GetScanline(y);
            for (var x = minX; x < maxX; x++)
            {
                if (row[x].R == color.R && row[x].G == color.G && row[x].B == color.B)
                {
                    return;
                }
            }
        }

        Assert.Fail($"Expected at least one pixel of color ({color.R},{color.G},{color.B}) inside Bounds ({bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}); found none.");
    }

    /// <summary>Scans left-to-right and right-to-left within Bounds for the first non-background
    /// (ink) pixel from each side, then asserts the LEFT one is redder-than-blue and the RIGHT one is
    /// bluer-than-red -- see <see cref="ApplyTemplateTests.ApplyTemplate_TextWithHorizontalGradient_LeftInkIsReddishRightInkIsBluish_NoInkOutsideBounds"/>'s
    /// own doc comment for why this is a relative/directional check rather than an exact-color one.</summary>
    private static void AssertLeftmostInkPixelIsRedderThanRightmostInkPixel(IImageSource image, NormalizedRect bounds, (byte R, byte G, byte B) background)
    {
        var minX = Math.Clamp((int)Math.Floor(bounds.X * image.Width), 0, image.Width - 1);
        var minY = Math.Clamp((int)Math.Floor(bounds.Y * image.Height), 0, image.Height - 1);
        var maxX = Math.Clamp((int)Math.Ceiling((bounds.X + bounds.Width) * image.Width), 0, image.Width);
        var maxY = Math.Clamp((int)Math.Ceiling((bounds.Y + bounds.Height) * image.Height), 0, image.Height);

        (byte R, byte G, byte B)? leftmost = null;
        (byte R, byte G, byte B)? rightmost = null;
        var leftmostX = int.MaxValue;
        var rightmostX = int.MinValue;

        for (var y = minY; y < maxY; y++)
        {
            var row = image.GetScanline(y);
            for (var x = minX; x < maxX; x++)
            {
                var pixel = row[x];
                if (pixel.R == background.R && pixel.G == background.G && pixel.B == background.B)
                {
                    continue;
                }

                if (x < leftmostX)
                {
                    leftmostX = x;
                    leftmost = (pixel.R, pixel.G, pixel.B);
                }

                if (x > rightmostX)
                {
                    rightmostX = x;
                    rightmost = (pixel.R, pixel.G, pixel.B);
                }
            }
        }

        if (leftmost is not { } left || rightmost is not { } right)
        {
            Assert.Fail("Expected at least one non-background (ink) pixel inside Bounds; found none.");
            return;
        }

        Assert.True(left.R > left.B, $"Expected the leftmost ink pixel to be redder than blue, got ({left.R},{left.G},{left.B}).");
        Assert.True(right.B > right.R, $"Expected the rightmost ink pixel to be bluer than red, got ({right.R},{right.G},{right.B}).");
    }

    private static void AssertNoNonBackgroundPixelOutsideBounds(IImageSource image, NormalizedRect bounds, (byte R, byte G, byte B) background)
    {
        var minX = (int)Math.Floor(bounds.X * image.Width);
        var minY = (int)Math.Floor(bounds.Y * image.Height);
        var maxX = (int)Math.Ceiling((bounds.X + bounds.Width) * image.Width);
        var maxY = (int)Math.Ceiling((bounds.Y + bounds.Height) * image.Height);

        for (var y = 0; y < image.Height; y++)
        {
            if (y >= minY && y < maxY)
            {
                continue;
            }

            var row = image.GetScanline(y);
            for (var x = 0; x < image.Width; x++)
            {
                if (x >= minX && x < maxX)
                {
                    continue;
                }

                Assert.True(
                    row[x].R == background.R && row[x].G == background.G && row[x].B == background.B,
                    $"Expected pixel ({x},{y}) outside Bounds to remain background {background}, got ({row[x].R},{row[x].G},{row[x].B}).");
            }
        }
    }

    private static string FontPath { get; } = Path.Combine(FindRepoRoot(), "assets", "fonts", "DejaVuSansMono.ttf");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScanlineStudio.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root (ScanlineStudio.sln) from test output directory.");
        }

        return dir.FullName;
    }

    private static async Task<string> WriteFixturePngAsync(int width, int height, Func<int, int, ImageSharpRgb24> pixelAt)
    {
        using var image = new Image<ImageSharpRgb24>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    row[x] = pixelAt(x, y);
                }
            }
        });
        var path = Path.Combine(Path.GetTempPath(), $"scanline-studio-apply-template-test-{Guid.NewGuid()}.png");
        await image.SaveAsPngAsync(path);
        return path;
    }
}
