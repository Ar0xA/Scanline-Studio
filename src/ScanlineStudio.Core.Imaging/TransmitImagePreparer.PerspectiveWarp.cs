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
    /// <summary>TX editor gap-items plan, item 3 (perspective transform, 2026-09-02) -- real
    /// implementation, overriding the interface's own graceful default (see that default's doc
    /// comment for the full contract: <paramref name="element"/>'s corners are in the SAME space as
    /// its own <c>Bounds</c>, need only be mutually consistent, not full-canvas-normalized). Falls
    /// back to the SAME plain (unwarped)/solid-fill behavior the interface default uses whenever
    /// <c>Perspective</c> is null or the quad turns out degenerate -- this class can't call the
    /// default implementation from inside its own override (C# has no way to reach a DIM once a
    /// class provides its own), so that small amount of fallback logic is intentionally duplicated,
    /// not shared.</summary>
    public BgraPixelBuffer RenderWarpedElementPreview(TemplateElement element, int targetWidthPx, int targetHeightPx, double? styleImageHeightPx = null)
    {
        targetWidthPx = Math.Max(1, targetWidthPx);
        targetHeightPx = Math.Max(1, targetHeightPx);

        switch (element)
        {
            case TemplateImageElement { Perspective: { } corners } image:
                return RenderWarpedImagePreview(image, corners, targetWidthPx, targetHeightPx);
            case TemplateImageElement image:
                return BgraPixelBuffer.FromOpaqueSource(Resize(image.Source, targetWidthPx, targetHeightPx, preserveAspect: false));
            case TemplateBoxElement { Perspective: { } corners } box:
                return RenderWarpedBoxPreview(box, corners, targetWidthPx, targetHeightPx,
                    styleImageHeightPx is > 0 && double.IsFinite(styleImageHeightPx.Value) ? styleImageHeightPx.Value : targetHeightPx);
            case TemplateBoxElement box:
                return box.FillEnabled
                    ? BgraPixelBuffer.FromSolidColor(box.FillColor, targetWidthPx, targetHeightPx)
                    : BgraPixelBuffer.FromTransparent(targetWidthPx, targetHeightPx);
            default:
                return BgraPixelBuffer.FromSolidColor(new Abstractions.Imaging.Rgb24(0, 0, 0), targetWidthPx, targetHeightPx);
        }
    }

    private BgraPixelBuffer RenderWarpedImagePreview(TemplateImageElement image, PerspectiveCorners corners, int targetWidthPx, int targetHeightPx)
    {
        if (!TryComputeLocalWarpGeometry(corners, targetWidthPx, targetHeightPx, out var localCorners))
        {
            return BgraPixelBuffer.FromOpaqueSource(Resize(image.Source, targetWidthPx, targetHeightPx, preserveAspect: false));
        }

        using var sourceImage = ImageSharpPixelConversion.FromImageSourceRgba32(image.Source);
        var matrix = SolveHomography(localCorners, sourceImage.Width, sourceImage.Height);
        if (!IsWellConditioned(matrix, sourceImage.Width, sourceImage.Height))
        {
            return BgraPixelBuffer.FromOpaqueSource(Resize(image.Source, targetWidthPx, targetHeightPx, preserveAspect: false));
        }

        using var warped = sourceImage.Clone(ctx => ctx.Transform(
            new Rectangle(0, 0, sourceImage.Width, sourceImage.Height), matrix, new Size(targetWidthPx, targetHeightPx), KnownResamplers.Bicubic));
        return ToPremultipliedBgra(warped);
    }

    private static BgraPixelBuffer RenderWarpedBoxPreview(TemplateBoxElement box, PerspectiveCorners corners, int targetWidthPx, int targetHeightPx, double styleImageHeightPx)
    {
        if (!TryComputeLocalWarpGeometry(corners, targetWidthPx, targetHeightPx, out var localCorners))
        {
            return box.FillEnabled
                ? BgraPixelBuffer.FromSolidColor(box.FillColor, targetWidthPx, targetHeightPx)
                : BgraPixelBuffer.FromTransparent(targetWidthPx, targetHeightPx);
        }

        using var contentBitmap = new Image<Rgba32>(targetWidthPx, targetHeightPx);
        // Styles are relative to the full output image, converted by the caller to this local
        // bitmap's resolution; the element bounding-box height is not the style reference.
        contentBitmap.Mutate(ctx => DrawBoxContent(ctx, box, new PixelBounds(0, 0, targetWidthPx, targetHeightPx), styleImageHeightPx));

        var matrix = SolveHomography(localCorners, targetWidthPx, targetHeightPx);
        if (!IsWellConditioned(matrix, targetWidthPx, targetHeightPx))
        {
            return box.FillEnabled
                ? BgraPixelBuffer.FromSolidColor(box.FillColor, targetWidthPx, targetHeightPx)
                : BgraPixelBuffer.FromTransparent(targetWidthPx, targetHeightPx);
        }

        using var warped = contentBitmap.Clone(ctx => ctx.Transform(
            new Rectangle(0, 0, targetWidthPx, targetHeightPx), matrix, new Size(targetWidthPx, targetHeightPx), KnownResamplers.Bicubic));
        return ToPremultipliedBgra(warped);
    }

    /// <summary>Unlike <see cref="TryComputeWarpGeometry"/> (which maps corners into a LARGER
    /// destination image's own pixel space), this element-preview render's target canvas IS exactly
    /// the corners' own bounding box -- so the bbox maps 1:1 onto <c>(0,0)-(targetWidthPx,targetHeightPx)</c>,
    /// no separate destination-image scale conversion needed. Corners can be in ANY consistent unit
    /// (normalized, crop-relative, raw pixels) since only their RELATIVE positions matter here.</summary>
    private static bool TryComputeLocalWarpGeometry(PerspectiveCorners corners, int targetWidthPx, int targetHeightPx, out PerspectiveCorners localCorners)
    {
        var bbox = corners.ToBoundingBox();
        if (!double.IsFinite(bbox.X) || !double.IsFinite(bbox.Y)
            || !double.IsFinite(bbox.Width) || !double.IsFinite(bbox.Height)
            || bbox.Width <= 0 || bbox.Height <= 0)
        {
            localCorners = default;
            return false;
        }

        var scaleX = targetWidthPx / bbox.Width;
        var scaleY = targetHeightPx / bbox.Height;
        localCorners = new PerspectiveCorners(
            (corners.Corner0X - bbox.X) * scaleX, (corners.Corner0Y - bbox.Y) * scaleY,
            (corners.Corner1X - bbox.X) * scaleX, (corners.Corner1Y - bbox.Y) * scaleY,
            (corners.Corner2X - bbox.X) * scaleX, (corners.Corner2Y - bbox.Y) * scaleY,
            (corners.Corner3X - bbox.X) * scaleX, (corners.Corner3Y - bbox.Y) * scaleY);
        // The shared edge-length floor is in pixels, so apply it after mapping out of the
        // caller's arbitrary (often normalized) coordinate space.
        return localCorners.IsConvexAndWellFormed();
    }

    /// <summary>ImageSharp's own <see cref="Rgba32"/> is STRAIGHT (unpremultiplied) alpha;
    /// <see cref="BgraPixelBuffer"/>'s own contract requires PREMULTIPLIED -- verified empirically
    /// (not assumed) that a freshly-<c>Clone()</c>d destination canvas starts zero-initialized
    /// (0,0,0,0), so pixels the warp never touches (outside the quad, inside the target rectangle)
    /// correctly come out fully transparent with no extra fill step needed.</summary>
    private static BgraPixelBuffer ToPremultipliedBgra(Image<Rgba32> image)
    {
        var pixels = new byte[image.Width * image.Height * 4];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < image.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var rowOffset = y * image.Width * 4;
                for (var x = 0; x < image.Width; x++)
                {
                    var pixel = row[x];
                    var offset = rowOffset + (x * 4);
                    var alpha = pixel.A / 255f;
                    pixels[offset] = (byte)MathF.Round(pixel.B * alpha);
                    pixels[offset + 1] = (byte)MathF.Round(pixel.G * alpha);
                    pixels[offset + 2] = (byte)MathF.Round(pixel.R * alpha);
                    pixels[offset + 3] = pixel.A;
                }
            }
        });

        return new BgraPixelBuffer { Pixels = pixels, Width = image.Width, Height = image.Height };
    }

    /// <summary>TX editor gap-items plan, item 3 (perspective transform) -- warps
    /// <paramref name="source"/> to fill <paramref name="corners"/> (destination-image-NORMALIZED
    /// space, same convention as <see cref="TemplateElement.Bounds"/>) and composites it onto
    /// <paramref name="image"/>. Returns false (does nothing, caller falls back to the plain
    /// axis-aligned render) when the quad is degenerate -- a hand-edited/imported template can carry
    /// self-intersecting or near-collinear corners the UI's own real-time drag clamp never validated;
    /// this must degrade gracefully, never throw or propagate NaN/Inf pixels, same convention as
    /// every other decorative/edge-case render path this session's own features already established
    /// (picture-fill's missing-asset handling, etc.).
    /// <para>Offscreen sub-bitmap is <see cref="Rgba32"/> (not <see cref="Rgb24"/>), matching
    /// <c>DrawTemplateText</c>'s own rotation-path precedent exactly -- transparent outside the
    /// mapped quad, so the composite below only paints inside it, same "render effects to an
    /// offscreen transparent sub-bitmap, then composite" shape.</para></summary>
    private static bool TryWarpElementContent(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image, IImageSource source, PerspectiveCorners corners, float destinationSizeCeiling)
    {
        if (!TryComputeWarpGeometry(corners, image.Width, image.Height, destinationSizeCeiling, out var bboxPx, out var subBitmapWidth, out var subBitmapHeight, out var localCorners))
        {
            return false;
        }

        using var sourceImage = ImageSharpPixelConversion.FromImageSourceRgba32(source);
        var matrix = SolveHomography(localCorners, sourceImage.Width, sourceImage.Height);
        if (!IsWellConditioned(matrix, sourceImage.Width, sourceImage.Height))
        {
            return false;
        }

        using var subBitmap = sourceImage.Clone(ctx => ctx.Transform(
            new Rectangle(0, 0, sourceImage.Width, sourceImage.Height), matrix, new Size(subBitmapWidth, subBitmapHeight), KnownResamplers.Bicubic));

        var location = new Point((int)MathF.Round(bboxPx.X), (int)MathF.Round(bboxPx.Y));
        image.Mutate(ctx => ctx.DrawImage(subBitmap, location, 1f));
        return true;
    }

    /// <summary>TX editor gap-items plan, item 3 (perspective transform) -- box elements have no
    /// natural source image to warp, so this draws the SAME fill/border/corner-radius/gradient
    /// content <see cref="DrawBoxContent"/> already renders directly, into an offscreen sub-bitmap
    /// sized to the corners' own (safety-clamped) bounding box, then warps THAT via the shared
    /// homography path -- "what the box would look like unwarped, at its own bbox size" is the
    /// natural pre-warp content, consistent with how turning <c>PerspectiveEnabled</c> ON seeds the 4
    /// corners from the box's current axis-aligned bbox in the first place.</summary>
    private static bool TryWarpBoxContent(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image, TemplateBoxElement element, PerspectiveCorners corners, int imageHeightPx)
    {
        var ceiling = MathF.Min(MaxElementResizeDimensionPxCeiling, MaxElementResizeDestinationMultiplier * MathF.Max(image.Width, image.Height));
        if (!TryComputeWarpGeometry(corners, image.Width, image.Height, ceiling, out var bboxPx, out var subBitmapWidth, out var subBitmapHeight, out var localCorners))
        {
            return false;
        }

        using var contentBitmap = new Image<Rgba32>(subBitmapWidth, subBitmapHeight);
        contentBitmap.Mutate(ctx => DrawBoxContent(ctx, element, new PixelBounds(0, 0, subBitmapWidth, subBitmapHeight), imageHeightPx));

        var matrix = SolveHomography(localCorners, subBitmapWidth, subBitmapHeight);
        if (!IsWellConditioned(matrix, subBitmapWidth, subBitmapHeight))
        {
            return false;
        }

        using var subBitmap = contentBitmap.Clone(ctx => ctx.Transform(
            new Rectangle(0, 0, subBitmapWidth, subBitmapHeight), matrix, new Size(subBitmapWidth, subBitmapHeight), KnownResamplers.Bicubic));

        var location = new Point((int)MathF.Round(bboxPx.X), (int)MathF.Round(bboxPx.Y));
        image.Mutate(ctx => ctx.DrawImage(subBitmap, location, 1f));
        return true;
    }

    /// <summary>Shared by <see cref="TryWarpElementContent"/> (image) and <see cref="TryWarpBoxContent"/>
    /// (box) -- computes the destination-pixel-space corners, their bounding box, and the
    /// safety-clamped sub-bitmap size + LOCAL (bbox-relative) corners every warp render needs, so the
    /// two content-acquisition paths (load a source image vs. draw box fill/border fresh) can't
    /// independently compute a slightly different bbox and drift apart. Returns false (all out params
    /// default) when the destination quad itself is degenerate -- caller falls back to the plain
    /// axis-aligned render, same convention as every other decorative/edge-case path in this file.</summary>
    private static bool TryComputeWarpGeometry(
        PerspectiveCorners corners, int imageWidth, int imageHeight, float destinationSizeCeiling,
        out PixelBounds bboxPx, out int subBitmapWidth, out int subBitmapHeight, out PerspectiveCorners localCorners)
    {
        var destCorners = new PerspectiveCorners(
            corners.Corner0X * imageWidth, corners.Corner0Y * imageHeight,
            corners.Corner1X * imageWidth, corners.Corner1Y * imageHeight,
            corners.Corner2X * imageWidth, corners.Corner2Y * imageHeight,
            corners.Corner3X * imageWidth, corners.Corner3Y * imageHeight);

        if (!destCorners.IsConvexAndWellFormed())
        {
            bboxPx = default;
            subBitmapWidth = 0;
            subBitmapHeight = 0;
            localCorners = default;
            return false;
        }

        var bboxNormalized = corners.ToBoundingBox();
        bboxPx = ToPixelBounds(bboxNormalized, imageWidth, imageHeight);
        var rawWidthPx = MathF.Max(1f, MathF.Round(bboxPx.Width));
        var rawHeightPx = MathF.Max(1f, MathF.Round(bboxPx.Height));
        var safetyScale = MathF.Min(1f, destinationSizeCeiling / MathF.Max(rawWidthPx, rawHeightPx));
        subBitmapWidth = Math.Max(1, (int)MathF.Round(rawWidthPx * safetyScale));
        subBitmapHeight = Math.Max(1, (int)MathF.Round(rawHeightPx * safetyScale));
        // Corners relative to the sub-bitmap's own origin, at the sub-bitmap's own (possibly
        // safety-scaled-down) resolution -- NOT the full destination-image resolution.
        var scaleX = subBitmapWidth / MathF.Max(1f, rawWidthPx);
        var scaleY = subBitmapHeight / MathF.Max(1f, rawHeightPx);
        localCorners = new PerspectiveCorners(
            (destCorners.Corner0X - bboxPx.X) * scaleX, (destCorners.Corner0Y - bboxPx.Y) * scaleY,
            (destCorners.Corner1X - bboxPx.X) * scaleX, (destCorners.Corner1Y - bboxPx.Y) * scaleY,
            (destCorners.Corner2X - bboxPx.X) * scaleX, (destCorners.Corner2Y - bboxPx.Y) * scaleY,
            (destCorners.Corner3X - bboxPx.X) * scaleX, (destCorners.Corner3Y - bboxPx.Y) * scaleY);
        return true;
    }

    /// <summary>Render-layer degeneracy guard (defense-in-depth for a hand-edited/imported template
    /// that never went through the UI's own real-time convexity clamp -- <see cref="PerspectiveCorners.IsConvexAndWellFormed"/>
    /// already rejects most bad quads before a matrix is even solved, but a near-degenerate-not-quite-
    /// degenerate quad can still solve to an ill-conditioned matrix). Checks the projective
    /// denominator <c>w = g*x + h*y + 1</c> ONLY at the source rectangle's 4 corners -- <c>w</c> is
    /// AFFINE (linear) over a rectangle, so its extrema provably occur at the corners, no need to
    /// sample the whole extent. Requires BOTH: all 4 corner <c>w</c> values share the exact same sign
    /// (catches a horizon-crossing quad even when the ratio check below would pass -- e.g.
    /// w={+1,+1,-1,-1} has ratio 1 but genuinely crosses zero), AND a RELATIVE (not absolute -- an
    /// absolute threshold repeats the exact non-scale-invariant mistake <see cref="PerspectiveCorners.IsConvexAndWellFormed"/>
    /// already avoids) <c>min(|w|) &gt; k*max(|w|)</c> for a small constant k.</summary>
    private static bool IsWellConditioned(Matrix4x4 m, int sourceWidthPx, int sourceHeightPx)
    {
        const float minRelativeW = 0.05f;
        Span<float> ws =
        [
            (0 * m.M14) + (0 * m.M24) + m.M44,
            (sourceWidthPx * m.M14) + (0 * m.M24) + m.M44,
            (sourceWidthPx * m.M14) + (sourceHeightPx * m.M24) + m.M44,
            (0 * m.M14) + (sourceHeightPx * m.M24) + m.M44,
        ];
        var minAbs = float.MaxValue;
        var maxAbs = 0f;
        var sign = 0;
        foreach (var w in ws)
        {
            if (!float.IsFinite(w))
            {
                return false;
            }

            var thisSign = MathF.Sign(w);
            if (sign == 0)
            {
                sign = thisSign;
            }
            else if (sign != thisSign)
            {
                return false;
            }

            minAbs = MathF.Min(minAbs, MathF.Abs(w));
            maxAbs = MathF.Max(maxAbs, MathF.Abs(w));
        }

        return minAbs > minRelativeW * maxAbs;
    }

    private static PixelBounds ToPixelBounds(NormalizedRect bounds, int imageWidth, int imageHeight) => new(
        (float)(bounds.X * imageWidth), (float)(bounds.Y * imageHeight),
        (float)(bounds.Width * imageWidth), (float)(bounds.Height * imageHeight));

    private readonly record struct PixelBounds(float X, float Y, float Width, float Height);

    /// <summary>TX editor gap-items plan, item 3 (perspective transform, 2026-09-02) -- solves the
    /// standard "map a source rectangle to an arbitrary destination quadrilateral" projective
    /// (homography) matrix (Heckbert's classic closed-form derivation, not novel research -- see
    /// e.g. Paul Heckbert's "Fundamentals of Texture Mapping and Image Warping," 1989). Maps
    /// <c>(0,0)-(sourceWidthPx,0)-(sourceWidthPx,sourceHeightPx)-(0,sourceHeightPx)</c> (the source
    /// rectangle's own 4 corners, in that TopLeft/TopRight/BottomRight/BottomLeft winding order) onto
    /// <paramref name="corners"/>'s own 4 points, in the SAME winding order.
    /// <para><see cref="System.Numerics.Matrix4x4"/>/<see cref="Vector4.Transform(Vector4,Matrix4x4)"/>'s
    /// own field layout is a stable, documented .NET BCL convention (row-vector: <c>result = v * M</c>,
    /// i.e. <c>result.X = v.X*M11 + v.Y*M21 + v.Z*M31 + v.W*M41</c>, and so on for Y/Z/W) -- NOT an
    /// ImageSharp-specific ambiguity. Feeding a 2D point as <c>(x, y, 0, 1)</c>, the classical 3x3
    /// projective matrix <c>[[a,b,c],[d,e,f],[g,h,1]]</c> (destX = (a*x+b*y+c)/w, destY =
    /// (d*x+e*y+f)/w, w = g*x+h*y+1) maps onto <see cref="System.Numerics.Matrix4x4"/> fields
    /// M11=a/M21=b/M41=c (the X output row), M12=d/M22=e/M42=f (the Y output row), M14=g/M24=h/M44=1
    /// (the W/perspective row -- the FOURTH COLUMN in row-vector form, not the third row, since W is
    /// computed from M14/M24/M34/M44). The Z row/column (M13/M23/M33/M43, M31/M32/M34) is left as an
    /// unused identity placeholder (M33=1, everything else 0) -- this is a pure 2D transform, Z is
    /// never read. VERIFIED against real rendered pixels through the actual
    /// <c>ctx.Transform(Rectangle, Matrix4x4, Size, IResampler)</c> call this method feeds, not
    /// trusted from documentation alone -- see the dedicated pixel-level tests for this method and
    /// for the render call site that consumes it.</para></summary>
    private static Matrix4x4 SolveHomography(PerspectiveCorners corners, float sourceWidthPx, float sourceHeightPx)
    {
        // Heckbert's own derivation is for the UNIT square (0,0)-(1,0)-(1,1)-(0,1) -- generalized
        // here to an arbitrary source rectangle by substituting sourceWidthPx/sourceHeightPx for the
        // unit square's own "1" at each of the 4 corner positions (TopRight/BottomRight's own X is
        // sourceWidthPx, not 1; BottomRight/BottomLeft's own Y is sourceHeightPx, not 1), rather than
        // solving for the unit square and composing a separate scale matrix afterward -- one fewer
        // matrix multiplication, same result.
        double x0 = corners.Corner0X, y0 = corners.Corner0Y;
        double x1 = corners.Corner1X, y1 = corners.Corner1Y;
        double x2 = corners.Corner2X, y2 = corners.Corner2Y;
        double x3 = corners.Corner3X, y3 = corners.Corner3Y;
        double sw = sourceWidthPx, sh = sourceHeightPx;

        var dx1 = x1 - x2;
        var dx2 = x3 - x2;
        var dx3 = x0 - x1 + x2 - x3;
        var dy1 = y1 - y2;
        var dy2 = y3 - y2;
        var dy3 = y0 - y1 + y2 - y3;

        double a, b, c, d, e, f, g, h;
        if (Math.Abs(dx3) < 1e-9 && Math.Abs(dy3) < 1e-9)
        {
            // Affine case (a parallelogram -- source rect maps to a quad with no true perspective
            // skew): g=h=0, matching Heckbert's own documented degenerate case exactly.
            g = 0;
            h = 0;
            a = (x1 - x0) / sw;
            b = (x3 - x0) / sh;
            c = x0;
            d = (y1 - y0) / sw;
            e = (y3 - y0) / sh;
            f = y0;
        }
        else
        {
            var denom = (dx1 * dy2) - (dx2 * dy1);
            g = ((dx3 * dy2) - (dx2 * dy3)) / denom;
            h = ((dx1 * dy3) - (dx3 * dy1)) / denom;
            a = (x1 - x0 + (g * x1)) / sw;
            b = (x3 - x0 + (h * x3)) / sh;
            c = x0;
            d = (y1 - y0 + (g * y1)) / sw;
            e = (y3 - y0 + (h * y3)) / sh;
            f = y0;
            g /= sw;
            h /= sh;
        }

        return new Matrix4x4(
            (float)a, (float)d, 0, (float)g,
            (float)b, (float)e, 0, (float)h,
            0, 0, 1, 0,
            (float)c, (float)f, 0, 1);
    }
}
