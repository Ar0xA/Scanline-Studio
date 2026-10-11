using System.Collections.Concurrent;
using System.Numerics;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
// NOT "using SixLabors.ImageSharp.Drawing" -- SixLabors.ImageSharp.Drawing.Path collides with
// System.IO.Path (used throughout this file, e.g. the constructor's Path.Combine). Reference
// SixLabors.ImageSharp.Drawing.RectangularPolygon fully-qualified at each use site instead.
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Imaging;

public sealed partial class TransmitImagePreparer
{
    private void DrawTemplateImage(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image, TemplateImageElement element, PixelBounds bounds)
    {
        // Defensive floor at 1px -- element.Bounds.Width/Height > 0 is already guaranteed by the
        // caller's skip check, but rounding a very thin bounds rect to pixels can still floor to 0.
        //
        // Tier B functional-audit blocker, fixed here: this used to clamp targetWidth/targetHeight
        // INDEPENDENTLY to the destination image's own Width/Height. DrawImage below has no
        // scale-to-rect overload -- it composites at native size -- so clamping the resize TARGET
        // size silently clamped the element's RENDERED SCALE too, and clamping each axis separately
        // also silently distorted its ASPECT RATIO whenever only one axis overflowed. A full-frame
        // element ("set as background") combined with a smaller active crop rect
        // (TxImageEditorPaneViewModel.ProjectRectToCropRelative) routinely projects Bounds several
        // times larger than the destination -- a completely normal, reachable case, not a
        // pathological one -- and the old clamp squashed it down to 1x scale (or, with independent
        // per-axis clamping, a squashed/stretched wrong aspect), sometimes moving it fully off-canvas
        // (nothing rendered, no diagnostic) when Bounds.X/Y also went negative.
        //
        // DrawImage naturally clips to the destination canvas (standard image-compositing behavior,
        // same as every other element type in this file relies on for a partially-off-canvas Bounds)
        // -- so resizing to the FULL correct, unclamped size and letting DrawImage clip produces
        // exactly correct scale and aspect for any oversized element, with no manual crop/intersect
        // math needed. Only a SINGLE, UNIFORM (same factor on both axes, so aspect is always
        // preserved even here) safety ceiling remains, to bound worst-case allocation for a truly
        // oversized Bounds value -- see GetOrCreateResizedImage's own pixel-budget cache-skip for
        // the other half of this: even a resize under this ceiling must not permanently occupy a
        // slot in the per-source cache (8 entries) if it's large enough to matter for retained memory.
        var rawWidthPx = MathF.Max(1f, MathF.Round(bounds.Width));
        var rawHeightPx = MathF.Max(1f, MathF.Round(bounds.Height));
        var ceiling = MathF.Min(MaxElementResizeDimensionPxCeiling, MaxElementResizeDestinationMultiplier * MathF.Max(image.Width, image.Height));
        var safetyScale = MathF.Min(1f, ceiling / MathF.Max(rawWidthPx, rawHeightPx));
        var targetWidth = Math.Max(1, (int)MathF.Round(rawWidthPx * safetyScale));
        var targetHeight = Math.Max(1, (int)MathF.Round(rawHeightPx * safetyScale));

        // TX editor gap-items plan, item 3 (perspective transform) -- takes priority over the plain
        // axis-aligned render below when set. A warp always stretches the FULL source to exactly
        // fill the quad, ignoring element.Fit (v1 scope decision: "warp to fill" has no obvious
        // Contain/Cover equivalent for a non-axis-aligned quad -- matches how corner-drag/free-
        // transform tools in other image editors already behave).
        if (element.Perspective is { } corners && TryWarpElementContent(image, element.Source, corners, ceiling))
        {
            return;
        }

        // Element rotation (2026-09-20) -- Perspective already returned above when set, so this only
        // runs when the two are NOT both non-null/non-zero (TemplateImageElement.RotationDegrees's
        // own Perspective-wins precedence). Same `% 360`/NaN/Infinity handling as DrawTemplateText's
        // own isUnrotated check.
        var isUnrotated = element.RotationDegrees % 360 == 0 || double.IsNaN(element.RotationDegrees) || double.IsInfinity(element.RotationDegrees);
        if (!isUnrotated)
        {
            RotateElementContent(image, element, bounds, targetWidth, targetHeight, (float)element.RotationDegrees);
            return;
        }

        var resized = GetOrCreateResizedImage(element.Source, targetWidth, targetHeight, element.Fit);

        using var resizedImage = ImageSharpPixelConversion.FromImageSource(resized);
        var location = new Point((int)MathF.Round(bounds.X), (int)MathF.Round(bounds.Y));
        image.Mutate(ctx => ctx.DrawImage(resizedImage, location, 1f));
    }

    /// <summary>Element rotation (2026-09-20) -- deliberately does NOT mirror
    /// <see cref="TryWarpElementContent"/>'s shape: that path clones the RAW source and lets the
    /// homography <c>Transform</c> itself be the render, which its own comment states is deliberate
    /// ("warp to fill", ignores <see cref="TemplateImageElement.Fit"/>) -- a straight copy here would
    /// silently convert every rotated Contain/Cover image to Stretch (round-1 plan-review finding).
    /// Instead: resize via <see cref="GetOrCreateResizedImage"/> with the element's own
    /// <see cref="TemplateImageElement.Fit"/> (the SAME call the plain non-rotated path below uses,
    /// with the SAME already-ceiling-clamped <paramref name="targetWidth"/>/<paramref name="targetHeight"/>
    /// the caller computed), THEN convert via <see cref="ImageSharpPixelConversion.FromImageSourceRgba32"/> -- NOT the plain
    /// <see cref="ImageSharpPixelConversion.FromImageSource"/>, which returns <c>Image&lt;Rgb24&gt;</c> and would fill the rotated
    /// AABB's expanded corners with opaque black (same reason <c>DrawTemplateText</c>'s own rotation
    /// sub-bitmap must be Rgba32). No clip to <paramref name="bounds"/> -- same no-clip/overflow
    /// policy as <see cref="RotateBoxContent"/>'s own doc comment.</summary>
    private void RotateElementContent(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image, TemplateImageElement element, PixelBounds bounds, int targetWidth, int targetHeight, float rotationDegrees)
    {
        var resized = GetOrCreateResizedImage(element.Source, targetWidth, targetHeight, element.Fit);
        using var subBitmap = ImageSharpPixelConversion.FromImageSourceRgba32(resized);
        subBitmap.Mutate(ctx => ctx.Rotate(rotationDegrees));

        var centerX = bounds.X + (bounds.Width / 2f);
        var centerY = bounds.Y + (bounds.Height / 2f);
        var location = ComputeRecenteredCompositeLocation(centerX, centerY, subBitmap.Width, subBitmap.Height);
        image.Mutate(ctx => ctx.DrawImage(subBitmap, location, 1f));
    }

    /// <summary>Opacity goes through <see cref="GraphicsOptions.BlendPercentage"/>, NOT the fill
    /// color's own alpha channel -- verified empirically (this project's own "no assumptions"
    /// discipline caught it, not a documentation read): on an <see cref="Image{TPixel}"/> of
    /// <see cref="SixLabors.ImageSharp.PixelFormats.Rgb24"/> (no alpha channel), <c>Fill</c>/
    /// <c>Draw</c> silently ignore a semi-transparent <c>Rgba32</c>/<c>Color</c> argument's own
    /// alpha and paint fully opaque regardless -- confirmed by a standalone repro before writing
    /// this, not assumed from how it works on an <c>Rgba32</c>-format destination (where color
    /// alpha DOES blend correctly). <c>BlendPercentage</c> works on either destination format, so
    /// it's the one real opacity control here.</summary>
    private static void DrawTemplateBox(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image, TemplateBoxElement element, PixelBounds bounds, int imageHeightPx)
    {
        // TX editor gap-items plan, item 3 (perspective transform, 2026-09-02) -- same priority
        // order as DrawTemplateImage's own Perspective check: takes over from the plain axis-aligned
        // fill/border render below when set.
        if (element.Perspective is { } corners && TryWarpBoxContent(image, element, corners, imageHeightPx))
        {
            return;
        }

        // Element rotation (2026-09-20) -- Perspective already returned above when set, so this only
        // runs when the two are NOT both non-null/non-zero (TemplateBoxElement.RotationDegrees's own
        // Perspective-wins precedence). Same `% 360`/NaN/Infinity handling as DrawTemplateText's own
        // isUnrotated check, for the same reasons (visual no-op at a full turn, no undefined
        // ctx.Rotate(NaN) call).
        var isUnrotated = element.RotationDegrees % 360 == 0 || double.IsNaN(element.RotationDegrees) || double.IsInfinity(element.RotationDegrees);
        if (!isUnrotated)
        {
            RotateBoxContent(image, element, bounds, imageHeightPx, (float)element.RotationDegrees);
            return;
        }

        image.Mutate(ctx => DrawBoxContent(ctx, element, bounds, imageHeightPx));
    }

    /// <summary>Element rotation (2026-09-20) -- mirrors <see cref="TryWarpBoxContent"/>'s SHAPE (own
    /// ceiling, offscreen Rgba32 sub-bitmap via the same <see cref="DrawBoxContent"/>) but NOT its
    /// geometry: <c>ctx.Rotate(angle)</c> (auto-expands the sub-bitmap canvas to the rotated AABB)
    /// instead of <see cref="TryComputeWarpGeometry"/>'s homography-specific sizing, which is not
    /// reusable here (round-1/round-2 plan-review findings). Deliberately does NOT clip to
    /// <paramref name="bounds"/>, unlike <see cref="DrawTemplateText"/>'s own rotation path -- a
    /// rotated box has no "shrink the content" analogue (a 90°-rotated 100×50 box should present as
    /// 50×100, not a shrunken inscribed quad), so the rotated AABB is left to overflow, matching the
    /// warp path's own no-clip-beyond-canvas behavior. <paramref name="imageHeightPx"/> stays the
    /// DESTINATION image's own height (not the sub-bitmap's) -- <see cref="DrawBoxContent"/>'s own
    /// <c>BorderThickness</c>/<c>CornerRadius</c> are height-relative and must not scale off a
    /// (possibly safety-clamped) sub-bitmap size.</summary>
    private static void RotateBoxContent(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image, TemplateBoxElement element, PixelBounds bounds, int imageHeightPx, float rotationDegrees)
    {
        var ceiling = MathF.Min(MaxElementResizeDimensionPxCeiling, MaxElementResizeDestinationMultiplier * MathF.Max(image.Width, image.Height));
        var rawWidthPx = MathF.Max(1f, MathF.Round(bounds.Width));
        var rawHeightPx = MathF.Max(1f, MathF.Round(bounds.Height));
        var safetyScale = MathF.Min(1f, ceiling / MathF.Max(rawWidthPx, rawHeightPx));
        var subBitmapWidth = Math.Max(1, (int)MathF.Round(rawWidthPx * safetyScale));
        var subBitmapHeight = Math.Max(1, (int)MathF.Round(rawHeightPx * safetyScale));

        using var contentBitmap = new Image<Rgba32>(subBitmapWidth, subBitmapHeight);
        contentBitmap.Mutate(ctx => DrawBoxContent(ctx, element, new PixelBounds(0, 0, subBitmapWidth, subBitmapHeight), imageHeightPx));
        contentBitmap.Mutate(ctx => ctx.Rotate(rotationDegrees));

        var centerX = bounds.X + (bounds.Width / 2f);
        var centerY = bounds.Y + (bounds.Height / 2f);
        var location = ComputeRecenteredCompositeLocation(centerX, centerY, contentBitmap.Width, contentBitmap.Height);
        image.Mutate(ctx => ctx.DrawImage(contentBitmap, location, 1f));
    }

    /// <summary>Extracted so <see cref="TryWarpBoxContent"/> can draw the SAME fill/border/corner-
    /// radius/gradient content into an offscreen local-coordinate sub-bitmap before warping it, not a
    /// second, driftable copy of this logic. <paramref name="bounds"/> is destination-image space
    /// for the plain (unwarped) path, or sub-bitmap-LOCAL space (always starting at (0,0)) for the
    /// warp path -- same "wherever it's actually being drawn" convention <see cref="BuildGradientBrush"/>
    /// already documents for text's own rotation path.</summary>
    private static void DrawBoxContent(IImageProcessingContext ctx, TemplateBoxElement element, PixelBounds bounds, double imageHeightPx)
    {
        var cornerRadiusPx = MathF.Max(0f, (float)(element.CornerRadius * imageHeightPx));
        var rect = BuildBoxPath(bounds.X, bounds.Y, bounds.Width, bounds.Height, cornerRadiusPx);
        var opacity = Math.Clamp((float)element.Opacity, 0f, 1f);
        var options = new DrawingOptions { GraphicsOptions = new GraphicsOptions { BlendPercentage = opacity } };

        // Legacy `.mtm` import -- FillEnabled false is legacy's own outline-only CM_BOX (NULL_BRUSH,
        // nothing painted underneath the border). Skips the fill entirely rather than drawing at
        // alpha 0: cheaper, and matches the border-drawing block below, which does not depend on this.
        if (element.FillEnabled)
        {
            var fillColor = new Rgba32(element.FillColor.R, element.FillColor.G, element.FillColor.B, 255);
            // Box gradient fill (TX editor gap-items plan, 2026-09-01) -- SAME BuildGradientBrush call
            // DrawTemplateText already uses; that method's own bounds/fallback-color params carry no
            // text-specific assumption, confirmed before reuse.
            Brush fillBrush = element.Gradient is { } gradient
                ? BuildGradientBrush(gradient, bounds, element.FillColor)
                : Brushes.Solid(fillColor);

            ctx.Fill(options, fillBrush, rect);
        }

        if (element.BorderColor is { } borderColor && element.BorderThickness > 0)
        {
            var requestedThicknessPx = (float)(element.BorderThickness * imageHeightPx);
            // ImageSharp's Draw(pen, path) CENTERS the stroke on the path -- confirmed via the
            // same empirical check as the opacity fix above, not assumed -- so drawing directly
            // on `rect` would bleed BorderThickness/2 outside Bounds on every side (code-review
            // round-1 finding). Inset the stroked rect by half the (clamped) thickness so the
            // stroke's OUTER edge lands exactly at Bounds, keeping the whole border inside it --
            // matches the same "clip to Bounds" philosophy DrawTemplateText already uses.
            var maxThicknessPx = MathF.Max(0f, MathF.Min(bounds.Width, bounds.Height) - 0.5f);
            var borderThicknessPx = MathF.Min(requestedThicknessPx, maxThicknessPx);
            if (borderThicknessPx > 0)
            {
                var half = borderThicknessPx / 2f;
                // Corner radius insets by the same half-thickness the straight-edge case already
                // uses, floored at 0 -- a thick border on a small radius would otherwise go
                // negative, which BuildBoxPath itself also clamps (MathF.Min against half the
                // shorter side), but doing it here too keeps this call site's own intent explicit.
                var borderRadiusPx = MathF.Max(0f, cornerRadiusPx - half);
                var borderRect = BuildBoxPath(
                    bounds.X + half, bounds.Y + half, bounds.Width - borderThicknessPx, bounds.Height - borderThicknessPx, borderRadiusPx);
                var borderColorRgba = new Rgba32(borderColor.R, borderColor.G, borderColor.B, 255);
                ctx.Draw(options, borderColorRgba, borderThicknessPx, borderRect);
            }
        }
    }

    /// <summary>Round 1/3 plan-review finding (line element design): the shared degenerate-bbox skip
    /// in <c>ApplyTemplateInto</c> only catches an AXIS-ALIGNED zero-thickness line -- its own
    /// un-inflated bbox collapses one axis to zero area, which the skip rule already drops. A
    /// DIAGONAL line's bbox stays non-degenerate even at zero thickness (both axes have real
    /// extent), so this method needs its own guard, mirroring <see cref="DrawTemplateBox"/>'s own
    /// <c>element.BorderThickness &gt; 0</c> gate. Ignores <c>bounds</c> entirely for geometry --
    /// unlike every other element kind, a line's actual shape comes from its own endpoints, not its
    /// (already ink-inflated, see <see cref="TemplateLineElement"/>'s own doc comment) bounding
    /// box.</summary>
    private static void DrawTemplateLine(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image, TemplateLineElement element, int imageHeightPx)
    {
        var thicknessPx = MathF.Max(0f, (float)(element.Thickness * imageHeightPx));
        if (thicknessPx <= 0)
        {
            return;
        }

        var pointA = new PointF((float)(element.X1 * image.Width), (float)(element.Y1 * imageHeightPx));
        var pointB = new PointF((float)(element.X2 * image.Width), (float)(element.Y2 * imageHeightPx));
        // Exact PointF equality alone only catches a TRUE zero-length line -- a future endpoint-drag
        // gesture will routinely produce a sub-pixel non-equal segment mid-drag. Squared-length
        // epsilon check catches both cases the same way -- "genuinely invisible" reasoning as the
        // thickness guard above.
        var dx = pointB.X - pointA.X;
        var dy = pointB.Y - pointA.Y;
        if ((dx * dx) + (dy * dy) < 1e-6f)
        {
            return;
        }

        var pb = new SixLabors.ImageSharp.Drawing.PathBuilder();
        pb.AddLine(pointA, pointB);
        var path = pb.Build();

        var opacity = Math.Clamp((float)element.Opacity, 0f, 1f);
        var options = new DrawingOptions { GraphicsOptions = new GraphicsOptions { BlendPercentage = opacity } };
        var strokeColor = new Rgba32(element.StrokeColor.R, element.StrokeColor.G, element.StrokeColor.B, 255);
        // ctx.Draw(pen, path) CENTERS the stroke on the path (see DrawTemplateBox's own doc comment
        // -- verified empirically, not assumed from the API shape) -- exactly the expected behavior
        // for a line's own centerline, no inset needed the way the box border case required.
        image.Mutate(ctx => ctx.Draw(options, strokeColor, thicknessPx, path));
    }

    /// <summary>Plain axis-aligned rect when <paramref name="cornerRadiusPx"/> is 0 (the byte-for-byte
    /// unchanged fast path every existing template hits) or a hand-built rounded-rectangle path
    /// otherwise -- 4 <c>PathBuilder.AddArc</c> quarter-arcs (each 90°, centered on the corresponding
    /// corner, radius clamped to half the shorter side so a large radius on a small/thin box degrades
    /// to a stadium/pill shape rather than a self-intersecting path) connected by 4 straight edges.
    /// Verified against a real rendered PNG before use (not assumed from the API shape alone) -- see
    /// this method's own call sites' doc comments for why: ImageSharp.Drawing has no built-in
    /// rounded-rectangle primitive at the version this project is pinned to.</summary>
    private static SixLabors.ImageSharp.Drawing.IPath BuildBoxPath(float x, float y, float width, float height, float cornerRadiusPx)
    {
        if (cornerRadiusPx <= 0)
        {
            return new SixLabors.ImageSharp.Drawing.RectangularPolygon(x, y, width, height);
        }

        var r = MathF.Min(cornerRadiusPx, MathF.Min(width, height) / 2f);
        var pb = new SixLabors.ImageSharp.Drawing.PathBuilder();
        pb.StartFigure();
        pb.AddArc(new PointF(x + r, y + r), r, r, 0f, 180f, 90f);
        pb.AddLine(new PointF(x + r, y), new PointF(x + width - r, y));
        pb.AddArc(new PointF(x + width - r, y + r), r, r, 0f, 270f, 90f);
        pb.AddLine(new PointF(x + width, y + r), new PointF(x + width, y + height - r));
        pb.AddArc(new PointF(x + width - r, y + height - r), r, r, 0f, 0f, 90f);
        pb.AddLine(new PointF(x + width - r, y + height), new PointF(x + r, y + height));
        pb.AddArc(new PointF(x + r, y + height - r), r, r, 0f, 90f, 90f);
        pb.AddLine(new PointF(x, y + height - r), new PointF(x, y + r));
        pb.CloseFigure();
        return pb.Build();
    }
}
