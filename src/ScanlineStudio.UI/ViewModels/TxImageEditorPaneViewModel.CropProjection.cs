using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.UI.Converters;
using ScanlineStudio.UI.Imaging;
using ScanlineStudio.UI.Services;

namespace ScanlineStudio.UI.ViewModels;

public sealed partial class TxImageEditorPaneViewModel
{
    /// <summary>Re-projects an element's CENTER-anchored X/Y/Width/Height from
    /// full-working-copy-normalized space into the CROPPED+RESIZED image's own top-left-anchored
    /// <see cref="NormalizedRect"/>, matching the Crop-&gt;Resize letterbox/stretch math
    /// <c>TransmitImagePreparer</c> actually applies. Design choice (round-1 auditor plan-review,
    /// tx-editor-overlay-wysiwyg.md, still the right call in Phase 1): elements stay anchored to
    /// source-PHOTO content, not to the crop frame -- dragging an element onto a subject's face
    /// keeps it there as the crop is adjusted later (clipped/hidden if the crop no longer includes
    /// that point), matching ordinary photo-editor behavior and requiring no new UI. The
    /// letterbox-pad term below is not optional: <c>Resize(preserveAspect: true)</c> (the default)
    /// uses <c>ResizeMode.Pad</c> (centered black bars on the constrained axis), so a naive
    /// `(X-CropRect.X)/CropRect.Width` re-projection (correct ONLY for stretch mode or a crop whose
    /// aspect exactly matches the target mode's) silently mis-places content under the default
    /// configuration -- round-1's own highest-severity finding on the pre-Phase-1 version of this
    /// fix. WIDTH/HEIGHT project the same way position does (Phase 1 addition, verified during
    /// Phase-1 plan-review): for two points <c>x</c> and <c>x+w</c>, the <c>padX</c>/<c>CropRect.X</c>
    /// terms cancel identically, leaving <c>finalW = (w/CropRect.Width) * contentWidth/targetWidth</c>
    /// -- the SAME <c>scaleX</c>/<c>contentWidth</c> machinery this method already computes for
    /// position, just applied to a size delta instead of an absolute coordinate. The final
    /// center-to-top-left conversion subtracts half the PROJECTED width/height, not half the raw
    /// one (Phase 1 plan-review blocker: these differ substantially under a mismatched-aspect
    /// letterboxed crop, and using the wrong one is exactly the kind of bug that looks plausible
    /// while being wrong). Both early-return branches below mirror each other's assumptions
    /// (degenerate CropRect vs. degenerate crop-pixel-size) so position and size are never projected
    /// under different assumptions from one another -- this replaces the pre-Phase-1
    /// position-only <c>ProjectToCropRelative</c> entirely (its only caller was this method's own
    /// predecessor) rather than keeping two methods that could drift apart.
    /// Pixel-space note: <see cref="CropWidthPixels"/>/<see cref="CropHeightPixels"/> are in
    /// CANVAS-DISPLAY (zoomed) pixel space as of the Phase 7 rearchitecture (previously working-copy
    /// space, before <c>ZoomFactor</c> existed) -- but the result stays zoom-INVARIANT regardless,
    /// since <c>Z</c> cancels algebraically: <c>contentWidth</c>/<c>contentHeight</c> (line ~2520/2521)
    /// scale by <c>Z</c> through <c>cropWidthPixels</c>/<c>cropHeightPixels</c> and by <c>1/Z</c>
    /// through <c>scaleX</c>/<c>scaleY</c>, so the returned NORMALIZED rect never carries a factor of
    /// <c>Z</c> -- correctly so, since this feeds <see cref="BuildTemplateElement"/> and the
    /// transmitted image must never depend on the operator's current canvas zoom. Also
    /// pixel-space-invariant up to <see cref="BuildWorkingCopy"/>'s own integer rounding (code-review
    /// nit: it depends only
    /// on the crop's aspect ratio, which is preserved from the original only up to that rounding, not
    /// bit-exactly) -- close enough for both <see cref="RecomputePreview"/> (working copy) and
    /// <see cref="Apply"/> (original source) to agree in practice, but not a hard guarantee for
    /// source dimensions that don't scale to round pixel counts.</summary>
    private NormalizedRect ProjectRectToCropRelative(double x, double y, double width, double height)
    {
        if (CropRect.Width <= 0 || CropRect.Height <= 0)
        {
            return new NormalizedRect(x - (width / 2), y - (height / 2), width, height);
        }

        var relX = (x - CropRect.X) / CropRect.Width;
        var relY = (y - CropRect.Y) / CropRect.Height;
        var relWidth = width / CropRect.Width;
        var relHeight = height / CropRect.Height;

        if (!TryGetCropContentMetrics(out var padX, out var padY, out var contentWidth, out var contentHeight))
        {
            return new NormalizedRect(relX - (relWidth / 2), relY - (relHeight / 2), relWidth, relHeight);
        }

        var targetWidth = (double)_targetMode.ImageWidth;
        var targetHeight = (double)_targetMode.ImageHeight;

        var finalCenterX = (padX + (relX * contentWidth)) / targetWidth;
        var finalCenterY = (padY + (relY * contentHeight)) / targetHeight;
        var finalWidth = relWidth * contentWidth / targetWidth;
        var finalHeight = relHeight * contentHeight / targetHeight;

        return new NormalizedRect(finalCenterX - (finalWidth / 2), finalCenterY - (finalHeight / 2), finalWidth, finalHeight);
    }

    /// <summary>TX workflow modernization plan, Phase 7 -- the letterbox/stretch metrics
    /// <see cref="ProjectRectToCropRelative"/> derives, in TARGET-MODE pixel space, extracted so the
    /// flatten command reads the SAME numbers the projection itself uses rather than recomputing
    /// them (a formula re-derived at a second call site is exactly the defect class that already hit
    /// this method once -- a proven function's math silently drifting from a second copy). Returns
    /// false in the two cases <see cref="ProjectRectToCropRelative"/> itself early-returns for
    /// (degenerate <see cref="CropRect"/>, degenerate crop pixel size) -- callers must treat false as
    /// "no valid projection exists right now," not substitute a fallback. Zoom-invariant, for the
    /// same reason <see cref="ProjectRectToCropRelative"/>'s own doc comment gives:
    /// <see cref="CropWidthPixels"/>/<see cref="CropHeightPixels"/> carry a factor of
    /// <see cref="ZoomFactor"/> and <c>scaleX</c>/<c>scaleY</c> carry <c>1/ZoomFactor</c>, so the
    /// products below carry none.</summary>
    private bool TryGetCropContentMetrics(out double padX, out double padY, out double contentWidth, out double contentHeight)
    {
        padX = padY = contentWidth = contentHeight = 0;

        if (CropRect.Width <= 0 || CropRect.Height <= 0)
        {
            return false;
        }

        var cropWidthPixels = CropWidthPixels;
        var cropHeightPixels = CropHeightPixels;
        if (cropWidthPixels <= 0 || cropHeightPixels <= 0)
        {
            return false;
        }

        var targetWidth = (double)_targetMode.ImageWidth;
        var targetHeight = (double)_targetMode.ImageHeight;

        double scaleX, scaleY;
        if (PreserveAspect)
        {
            scaleX = scaleY = Math.Min(targetWidth / cropWidthPixels, targetHeight / cropHeightPixels);
        }
        else
        {
            scaleX = targetWidth / cropWidthPixels;
            scaleY = targetHeight / cropHeightPixels;
        }

        contentWidth = cropWidthPixels * scaleX;
        contentHeight = cropHeightPixels * scaleY;
        padX = (targetWidth - contentWidth) / 2;
        padY = (targetHeight - contentHeight) / 2;
        return true;
    }

    /// <summary>Font size in CANVAS DISPLAY pixels for the given TEXT element -- the counterpart to
    /// <see cref="ProjectRectToCropRelative"/> for size rather than position. Phase 1: no longer a
    /// closed-form function of <c>FontSizeRelative</c> alone -- calls the real
    /// <see cref="ITransmitImagePreparer.MeasureFittedFontSize"/> (added in Phase 0 specifically so
    /// this method could mirror the pipeline's own shrink-to-fit search instead of reimplementing
    /// it) against the element's own crop-projected pixel bounds, matching what <c>ApplyTemplate</c>
    /// will actually render. The final division by <c>scaleY</c> is unchanged from the pre-Phase-1
    /// version: one final-image pixel of vertical extent corresponds to <c>1/scaleY</c>
    /// working-copy-canvas pixels, so <c>canvasFontSize = fittedFinalSizePx / scaleY</c> -- <c>scaleY</c>
    /// itself still depends on <c>PreserveAspect</c>, same reasoning as before. Bounds-to-pixel
    /// rounding uses <c>Math.Max(1, (int)Math.Round(...))</c> (double, not <c>MathF.Round</c>) on
    /// the same quantity <c>DrawTemplateText</c> rounds in <c>float</c> -- stated rule (Phase 1
    /// plan-review nit), not guaranteed byte-identical near a .5 boundary, but the transmitted
    /// preview image itself always comes from the real pipeline regardless, so this is a
    /// canvas-display-only approximation.</summary>
    /// <summary>Backlog item (user request, 2026-08-17) -- factored out of
    /// <see cref="ComputeCanvasFontSize"/> so <see cref="ComputeCanvasStrokeThicknessPixels"/> (the
    /// canvas-preview outline fix) can convert a target-mode-pixel quantity to canvas-display pixels
    /// the identical way, instead of a second, driftable copy of this same formula. Returns 0 (an
    /// already-guarded "no valid scale" sentinel both callers already check for) when the crop has
    /// zero area.</summary>
    private double ComputeTargetToCanvasScaleY()
    {
        var cropWidthPixels = CropWidthPixels;
        var cropHeightPixels = CropHeightPixels;
        if (cropWidthPixels <= 0 || cropHeightPixels <= 0)
        {
            return 0;
        }

        var targetWidth = (double)_targetMode.ImageWidth;
        var targetHeight = (double)_targetMode.ImageHeight;
        return PreserveAspect
            ? Math.Min(targetWidth / cropWidthPixels, targetHeight / cropHeightPixels)
            : targetHeight / cropHeightPixels;
    }

    private void RefreshElementPreviewMetrics(ITemplateElementViewModel element)
    {
        var scaleY = ComputeTargetToCanvasScaleY();
        if (scaleY <= 0 || !double.IsFinite(scaleY))
        {
            return;
        }

        var scaleX = PreserveAspect ? scaleY : _targetMode.ImageWidth / CropWidthPixels;
        var bounds = ProjectRectToCropRelative(element.X, element.Y, element.Width, element.Height);
        // Rotated text is painted into an element-local bitmap before rotation in the final
        // renderer; its pattern phase starts there, rather than at the full image origin.
        var localPattern = element is OverlayElementViewModel textElement
            && double.IsFinite(textElement.RotationDegrees) && textElement.RotationDegrees % 360 != 0;
        var metrics = new ElementPreviewMetrics(_targetMode.ImageHeight / scaleY,
            new Avalonia.Size(1 / scaleX, 1 / scaleY),
            localPattern ? default : new Avalonia.Point(bounds.X * _targetMode.ImageWidth, bounds.Y * _targetMode.ImageHeight));
        switch (element)
        {
            case OverlayElementViewModel text: text.PreviewMetrics = metrics; break;
            case BoxElementViewModel box: box.PreviewMetrics = metrics; break;
            case LineElementViewModel line: line.PreviewMetrics = metrics; break;
        }
    }

    private double ComputeCanvasFontSize(OverlayElementViewModel element)
    {
        var scaleY = ComputeTargetToCanvasScaleY();
        if (scaleY <= 0)
        {
            return 0;
        }

        var targetWidth = (double)_targetMode.ImageWidth;
        var targetHeight = (double)_targetMode.ImageHeight;
        var bounds = ProjectRectToCropRelative(element.X, element.Y, element.Width, element.Height);
        var boundsWidthPx = Math.Max(1, (int)Math.Round(bounds.Width * targetWidth));
        var boundsHeightPx = Math.Max(1, (int)Math.Round(bounds.Height * targetHeight));
        // FontSpec.Family + the trailing stroke-thickness argument fixed (Phase 4 plan-review
        // blocker) -- an empty family/omitted stroke here would measure against the WRONG font/
        // without the stroke fit-box allowance while the real pipeline (BuildTemplateElement) uses
        // the actually-selected ones, silently desyncing the canvas from the transmitted image.
        var strokeThicknessRelative = element.StrokeColor is { } ? element.StrokeThickness : 0;
        // Phase 8: shadow offset/rotation are the THIRD occurrence of this same fit-box-shrink
        // requirement (see MeasureFittedFontSize's own doc comment) -- omitting them here would
        // desync the canvas-side CanvasFontSize from what DrawTemplateText actually fits/renders,
        // the exact bug class this whole method exists to prevent.
        var shadowOffsetXRelative = element.ShadowColor is { } ? element.ShadowOffsetX : 0;
        var shadowOffsetYRelative = element.ShadowColor is { } ? element.ShadowOffsetY : 0;
        // Auditor usability review follow-up (2026-08-18): Bold/Italic are the FOURTH occurrence of
        // this same fit-box-measurement desync class -- Bold glyphs measure wider than Regular at
        // the same point size, so omitting them here would fit a size against the WRONG (narrower)
        // measurement while DrawTemplateText actually draws bold, the identical bug this method's
        // own doc comment already describes for family/stroke/shadow/rotation. StackStepX/Y are the
        // FIFTH occurrence, same reasoning, gated on StackColor the same way shadow is gated above.
        var stackStepXRelative = element.StackColor is { } ? element.StackStepX : 0;
        var stackStepYRelative = element.StackColor is { } ? element.StackStepY : 0;
        var fittedFinalSizePx = _preparer.MeasureFittedFontSize(
            element.ResolvedText, new FontSpec(element.FontFamily, element.FontSizeRelative, element.Bold, element.Italic), (int)Math.Round(targetHeight),
            boundsWidthPx, boundsHeightPx, strokeThicknessRelative,
            shadowOffsetXRelative, shadowOffsetYRelative, element.RotationDegrees,
            stackStepXRelative, stackStepYRelative, element.GrowToFillEnabled);

        // [Code-review blocker, fixed here] NO extra * ZoomFactor -- zoom already arrives here
        // implicitly, through scaleY: cropHeightPixels (both PreserveAspect and stretch branches
        // above) is CropHeightPixels, which is now CanvasDisplayHeight-based (= WorkingCopyHeight *
        // ZoomFactor) per the Phase 7 rearchitecture, so scaleY is proportional to 1/ZoomFactor
        // already. fittedFinalSizePx itself is zoom-invariant (ProjectRectToCropRelative's own bounds
        // and _targetMode's dimensions carry no Z, per that method's own doc comment). An earlier
        // version of this line multiplied by ZoomFactor AGAIN on top of that -- a real Z^2 bug, caught
        // by code-review, not by the one manual test that happened to run at exactly 1.0 zoom (where
        // Z^2 == Z == 1 hides the error completely).
        var canvasFontSize = fittedFinalSizePx / scaleY;

        // User-reported (2026-09-20): the SixLabors.Fonts-based search above is SHARED with the real
        // transmitted-image pipeline (MeasureFittedFontSize/DrawTemplateText both measure AND draw
        // via SixLabors, so it stays self-consistent there) -- but the CANVAS actually renders text
        // through StrokedTextBlock, which uses Avalonia's OWN FormattedText/text-layout engine, a
        // completely different font-metrics implementation. Horizontal advance-widths are
        // standardized and agree across engines (confirmed empirically: width-driven canvas resize
        // tracks correctly with no gap), but VERTICAL metrics (ascent/descent/line-height) are
        // engine-specific -- SixLabors can report "fits" for a size that, once rendered through
        // Avalonia's own text layout, is visibly taller than the box (confirmed: the user could
        // shrink a box's HEIGHT well past where the font should have started shrinking before
        // Avalonia's own overflow became visible, while the width-driven case never showed this
        // gap). This does one more shrink pass, purely in CANVAS pixel space, using the EXACT
        // FormattedText construction StrokedTextBlock.BuildFormattedText itself uses (same Typeface
        // resolution via PipelineFontFamilyNameConverter, the same converter the AXAML binding
        // applies), so CanvasFontSize never asks Avalonia to render something taller/wider than its
        // own box. Deliberately does NOT touch MeasureFittedFontSize/ComputeFittedFontSizePx/
        // DrawTemplateText -- the real transmitted image and the mode-exact PREVIEW panel (both fed
        // by that SAME untouched pipeline) are unaffected by this fix, on purpose.
        return ClampToAvaloniaRenderedSize(
            element.ResolvedText, element.FontFamily, element.Bold, element.Italic, canvasFontSize, element.CanvasWidthPixels, element.CanvasHeightPixels);
    }

    /// <summary>See <see cref="ComputeCanvasFontSize"/>'s own call-site comment for why this exists.
    /// A second, defensive shrink pass -- <paramref name="candidateSizePx"/> already reflects the
    /// real pipeline's own SixLabors-based fit, so this only ever shrinks FURTHER, never grows past
    /// it, and returns it UNCHANGED when Avalonia's own measurement already agrees (the common case
    /// for anything that isn't right at the fit boundary). Deliberately checks against the RAW
    /// <paramref name="boxWidthPx"/>/<paramref name="boxHeightPx"/> (CanvasWidthPixels/
    /// CanvasHeightPixels), not a stroke/shadow/stack-shrunk box -- this is a safety-net refinement
    /// for the Avalonia-vs-SixLabors vertical-metric gap specifically, not a full re-implementation
    /// of ShrinkFitBoxForEffects's own allowance math on the canvas side.
    /// <para>Code-review finding (2026-09-20): a real <see cref="FormattedText"/> measurement needs a
    /// real, resolvable glyph typeface -- <c>ScanlineStudio.UI.Tests</c>' own fast headless font
    /// manager stub (see <c>TxImageEditorQuickStyleFlyoutRealClickTests</c>' own doc comment for why
    /// that split exists) throws <see cref="InvalidOperationException"/> ("Could not create
    /// glyphTypeface") the instant any test creates a text element, which this method now runs on
    /// every single time via <see cref="ComputeCanvasFontSize"/>. Caught and treated as "can't verify,
    /// trust the SixLabors-based candidate as-is" -- the exact same fallback as every other
    /// decorative/measurement-only path in this codebase, not a special case invented for tests; a
    /// production environment with a genuinely broken font manager would hit the identical fallback,
    /// same as it already silently falls back elsewhere (<see cref="ResolveFontFamily"/>'s own
    /// unknown-family fallback, for one).</para></summary>
    private static double ClampToAvaloniaRenderedSize(
        string text, string fontFamilyName, bool bold, bool italic, double candidateSizePx, double boxWidthPx, double boxHeightPx)
    {
        if (string.IsNullOrEmpty(text) || candidateSizePx <= 0 || boxWidthPx <= 0 || boxHeightPx <= 0)
        {
            return candidateSizePx;
        }

        Typeface typeface;
        bool FitsAt(double sizePx)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, sizePx, null);
            return formatted.Width <= boxWidthPx && formatted.Height <= boxHeightPx;
        }

        try
        {
            typeface = new Typeface(
                (FontFamily)PipelineFontFamilyNameConverter.Instance.Convert(fontFamilyName, typeof(FontFamily), null, CultureInfo.InvariantCulture)!,
                italic ? FontStyle.Italic : FontStyle.Normal,
                bold ? FontWeight.Bold : FontWeight.Normal);

            if (FitsAt(candidateSizePx))
            {
                return candidateSizePx;
            }
        }
        catch (InvalidOperationException)
        {
            return candidateSizePx;
        }

        var low = 1.0;
        var high = candidateSizePx;
        for (var i = 0; i < 12 && high - low > 0.5; i++)
        {
            var mid = (low + high) / 2;
            if (FitsAt(mid))
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    /// <summary>Save-time counterpart to <see cref="ComputeCanvasFontSize"/> above -- user-reported
    /// gap (2026-09-15): <see cref="OverlayElementViewModel.GrowToFillEnabled"/> is deliberately NOT
    /// persisted (see that property's own doc comment), so a template saved with a grown font
    /// reloaded back at its original, small, pre-grow <see cref="OverlayElementViewModel.FontSizeRelative"/>
    /// -- the grow was always recomputed live (here and in <c>TransmitImagePreparer.ApplyTemplate</c>),
    /// never baked into the one field that actually survives a save. Runs the identical fit search
    /// <see cref="ComputeCanvasFontSize"/> does, but against the TARGET mode's real image height (the
    /// same unit <see cref="OverlayElementViewModel.FontSizeRelative"/> is itself relative to), not
    /// canvas-display pixels -- so the persisted value already reflects wherever GrowToFillEnabled
    /// left it, whether that call site itself survives the round trip or not. Also correct when
    /// GrowToFillEnabled is false: the search is then shrink-only, so the result is never larger than
    /// <see cref="RawTextElementSnapshot.FontSizeRelative"/> -- calling this unconditionally for
    /// every text element at save time is safe, not just for grown ones. Takes the RAW (unresolved,
    /// e.g. still containing "%m") snapshot text, matching what <see cref="RawTextElementSnapshot.Text"/>/
    /// <see cref="ScanlineStudio.Application.PersistedTextElement.Text"/> both already store and what
    /// a future load will re-measure against -- using today's live <c>ResolvedText</c> (e.g. today's
    /// specific callsign) would bake in a size fitted to a string length a future QSO's callsign
    /// won't match.</summary>
    private double ComputeFittedFontSizeRelative(RawTextElementSnapshot text)
    {
        var targetHeight = (double)_targetMode.ImageHeight;
        if (targetHeight <= 0)
        {
            return text.FontSizeRelative;
        }

        var bounds = ProjectRectToCropRelative(text.X, text.Y, text.Width, text.Height);
        var boundsWidthPx = Math.Max(1, (int)Math.Round(bounds.Width * _targetMode.ImageWidth));
        var boundsHeightPx = Math.Max(1, (int)Math.Round(bounds.Height * targetHeight));
        var strokeThicknessRelative = text.StrokeColor is { } ? text.StrokeThickness : 0;
        var shadowOffsetXRelative = text.ShadowColor is { } ? text.ShadowOffsetX : 0;
        var shadowOffsetYRelative = text.ShadowColor is { } ? text.ShadowOffsetY : 0;
        var stackStepXRelative = text.StackColor is { } ? text.StackStepX : 0;
        var stackStepYRelative = text.StackColor is { } ? text.StackStepY : 0;
        var fittedFinalSizePx = _preparer.MeasureFittedFontSize(
            text.Text, new FontSpec(text.FontFamily, text.FontSizeRelative, text.Bold, text.Italic), (int)Math.Round(targetHeight),
            boundsWidthPx, boundsHeightPx, strokeThicknessRelative,
            shadowOffsetXRelative, shadowOffsetYRelative, text.RotationDegrees,
            stackStepXRelative, stackStepYRelative, text.GrowToFillEnabled);

        return fittedFinalSizePx / targetHeight;
    }

    /// <summary>Backlog item (user request, 2026-08-17) -- canvas-preview outline fix. Real-pixel
    /// formula matches <c>TransmitImagePreparer.DrawTemplateText</c>'s own
    /// <c>strokeThicknessPx = strokeThicknessRelative * imageHeightPx</c> exactly (against the
    /// TARGET mode's height, not the crop's), then converted to canvas-display pixels via the same
    /// <see cref="ComputeTargetToCanvasScaleY"/> <see cref="ComputeCanvasFontSize"/> already uses --
    /// keeps the canvas stroke width in the same pixel space as <see cref="OverlayElementViewModel.CanvasFontSize"/>.
    /// 0 when <see cref="OverlayElementViewModel.StrokeColor"/> is null (no outline) or the scale is
    /// invalid, matching <see cref="ComputeCanvasFontSize"/>'s own zero-sentinel convention.</summary>
    private double ComputeCanvasStrokeThicknessPixels(OverlayElementViewModel element)
    {
        if (element.StrokeColor is not { })
        {
            return 0;
        }

        var scaleY = ComputeTargetToCanvasScaleY();
        if (scaleY <= 0)
        {
            return 0;
        }

        return (element.StrokeThickness * _targetMode.ImageHeight) / scaleY;
    }

    /// <summary>Refresh crop/zoom-dependent style lengths and text fitting on the canvas.</summary>
    private void RefreshOverlayElementCanvasStyles()
    {
        foreach (var element in OverlayElements)
        {
            RefreshElementPreviewMetrics(element);
            if (element is OverlayElementViewModel text)
            {
                text.CanvasFontSize = ComputeCanvasFontSize(text);
                text.CanvasStrokeThicknessPixels = ComputeCanvasStrokeThicknessPixels(text);
            }
        }
    }
}
