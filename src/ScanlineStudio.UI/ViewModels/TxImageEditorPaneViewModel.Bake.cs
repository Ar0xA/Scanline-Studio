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
    private bool CanDemoteBackdropToBackground(ITemplateElementViewModel? element) =>
        !_isDemoting && element is ImageElementViewModel { IsBackground: true } && OverlayElements.Contains(element);

    /// <summary>User-requested (2026-09-15, background/backdrop naming work): the reverse of
    /// <see cref="PromoteBackgroundToBackdrop"/>. Replaces the background WITHIN the current crop
    /// window with this backdrop element's own pixels, respecting its
    /// <see cref="ImageElementViewModel.Fit"/> and perspective warp (if any) -- same "only the crop
    /// region, not the whole underlying source" scope <see cref="BakeElementIntoSource"/> already has
    /// for Flatten (nit: an earlier doc comment here overstated this as replacing the background
    /// "outright" -- widening the crop afterward can reveal pixels from the photo that was there
    /// before, matching Flatten's own established, documented behavior for the same reason).
    /// <para>[Auditor-found blocker, fixed here] An earlier draft called
    /// <c>_preparer.Resize(backdrop.Source, ..., preserveAspect: false)</c> directly -- that ignores
    /// <see cref="ImageElementViewModel.Fit"/> entirely (a <c>Contain</c>-fit backdrop, the element's
    /// own default, got silently stretched instead of letterboxed) and drops any perspective warp
    /// (the PERSPECTIVE checkbox is gated only on element type, not <c>Locked</c>/<c>IsBackground</c>,
    /// so a locked backdrop CAN still be warped). Fixed by reusing <see cref="BakeElementIntoSource"/>
    /// -- the SAME async, <c>Task.Run</c>-offloaded bake <see cref="FlattenElementAsync"/> already
    /// uses, which renders through the real <c>ComposePreview</c>/<c>ApplyTemplate</c> pipeline (Fit
    /// and perspective both correctly resolved there) rather than a bare resize. A full-frame element
    /// (X=0.5,Y=0.5,W=1,H=1 -- always true for a backdrop, by construction) makes that bake's own
    /// write-back rect exactly the whole crop-content area, so "patch this element into the source"
    /// and "replace the background with it" are the same operation here -- no separate replace-path
    /// needed. Same "stale-result discarded, not silently applied" re-validation
    /// <see cref="FlattenElementAsync"/>'s own doc comment explains, for the same reason (an offloaded
    /// compute means the operator can edit while it runs).</para>
    /// <para><see cref="CropRect"/> is deliberately left UNTOUCHED -- same auditor-found blocker
    /// <see cref="PromoteBackgroundToBackdrop"/>'s own doc comment explains: resetting it would
    /// reproject every OTHER overlay element's bounds, not just this one's framing. Reusing the bake
    /// (which already renders AT the current crop) makes this automatic, not something to remember.</para>
    /// <para>User-reported feedback (2026-09-15): an earlier draft armed/required a second click to
    /// confirm before actually replacing the background -- removed. This command already pushes a
    /// real, working undo step (see
    /// <c>DemoteBackdropToBackgroundCommand_UndoRestoresTheBackdropElement</c>), so a second confirming
    /// click is redundant friction, not a safety net -- Undo already IS the safety net. One click,
    /// done.</para></summary>
    [RelayCommand(CanExecute = nameof(CanDemoteBackdropToBackground))]
    private async Task DemoteBackdropToBackgroundAsync(ITemplateElementViewModel? element)
    {
        if (element is not ImageElementViewModel { IsBackground: true } backdrop || !OverlayElements.Contains(backdrop))
        {
            return;
        }

        // Read before the bake helper disposes the element; Origin is init-only, so the value is the same.
        var wasPromoted = backdrop.Origin.Kind == ImageSourceKind.Promoted;

        var committed = await BakeElementIntoBackgroundAsync(backdrop, new BakeOperation(
            SetBusy: busy =>
            {
                _isDemoting = busy;
                DemoteBackdropToBackgroundCommand.NotifyCanExecuteChanged();
            },
            FailedKey: "Panes.TxImageEditor.DemoteFailed",
            OutsideFrameKey: "Panes.TxImageEditor.DemoteOutsideFrame",
            StaleKey: "Panes.TxImageEditor.DemoteDiscardedStale",
            LogFailed: ex => Log.DemoteBackdropToBackgroundFailed(_logger, ex),
            LogStale: () => Log.DemoteBackdropToBackgroundDiscardedAsStale(_logger)));
        if (!committed)
        {
            return;
        }

        // Auditor-found blocker: a backdrop created by PromoteBackgroundToBackdrop already has the
        // CURRENT adjustment sliders baked into its own pixels (Promote's own doc comment). If those
        // sliders are still non-identity when demoted back, the live pipeline applies them a SECOND
        // time to the now-restored background on every subsequent render -- the image visibly changes
        // on this click with no notice. Narrower than Flatten's own identical-shaped warning
        // (BuildAdjustments().IsIdentity check, Panes.TxImageEditor.FlattenAdjustmentsNowApply): a
        // backdrop created via the OTHER path (+Image, then Set as backdrop directly, never promoted)
        // has raw, never-baked pixels, so continuing to apply the current sliders to it is the SAME
        // "sliders apply live to whatever's currently loaded" behavior Browse/Stock already have --
        // warning there would be a false alarm. ImageSourceKind.Promoted (added alongside Promote
        // itself) is exactly the signal that distinguishes the two origins.
        // Auditor nit: this reads the sliders' CURRENT value, not what was actually baked at promote
        // time -- a promoted backdrop with sliders moved back to identity since correctly stays quiet,
        // but sliders moved to a DIFFERENT non-identity value after promote also warn even though
        // nothing was technically "already baked in" at that exact value. Accepted as conservative
        // (an extra warning, never a missed one); the loc string below is worded to stay true either
        // way rather than assert a specific baked-in history. Also clears any stale prior message on
        // the no-warn path, matching Flatten's own IsIdentity ? null : key shape -- a leftover message
        // from an earlier action must not survive a successful, nothing-to-report demote.
        StatusMessage = wasPromoted && !BuildAdjustments().IsIdentity
            ? _localization.GetString("Panes.TxImageEditor.DemotePromotedAdjustmentsNowReapply")
            : null;

        RecomputePreview();
    }

    /// <summary>Everything <see cref="BakeElementIntoSource"/> needs, captured on the UI thread
    /// BEFORE the <see cref="Task.Run"/> offload -- that method is static and takes only this, so it
    /// can never read mutable VM state off the UI thread.</summary>
    private sealed record FlattenBakeRequest(
        IImageSource Source, NormalizedRect CropRect, bool PreserveAspect, TemplateElement Element,
        int ModeWidth, int ModeHeight, double PadX, double PadY, double ContentWidth, double ContentHeight);

    /// <summary>Rasterises ONE element into a copy of <paramref name="request"/>.Source, so the
    /// pipeline's own Crop-&gt;Resize reproduces it in the transmitted frame at the position/size
    /// <c>ApplyTemplate</c> was already drawing it. Returns null when the element does not intersect
    /// the transmitted frame at all (nothing to bake -- the caller reports it rather than silently
    /// deleting the element).
    /// <para><b>Why a uniformly-scaled copy of the TARGET FRAME, and not source space.</b>
    /// <see cref="ProjectRectToCropRelative"/> produces bounds normalized against the target frame,
    /// and every size-like element property (font size, stroke thickness, shadow/stack offsets,
    /// corner radius, border thickness) is relative to that frame's HEIGHT. A UNIFORM scale of that
    /// frame is therefore the one transform under which the existing projection stays valid with
    /// zero rewriting -- which is precisely why this reuses <c>ComposePreview</c> (which already
    /// consumes a <see cref="TemplateElement"/> built by <see cref="ProjectRectToCropRelative"/>)
    /// rather than re-deriving or re-purposing that projection outside the space it was defined
    /// for.</para>
    /// <para><b>Adjustments are NOT baked</b> and the sliders are NOT reset (settled after a design
    /// consult: baking current adjustments then zeroing the sliders was the original plan, but
    /// <c>ApplyAdjustments</c>'s Gaussian sharpen/denoise use an ABSOLUTE pixel sigma and its gamma
    /// is pointwise-nonlinear, so neither survives a change of raster scale -- baking them at any
    /// scale other than exactly 1x is not equivalent, and baking at exactly 1x would force the
    /// ENTIRE crop through a downscale/upscale round trip instead of just the element. Consequence,
    /// stated rather than hidden: after a flatten the baked pixels are part of the photo and the
    /// adjustment sliders apply to them from then on -- surfaced to the operator via
    /// <see cref="StatusMessage"/>, not silently. Identical output when the sliders are at
    /// identity.</para></summary>
    private static IImageSource? BakeElementIntoSource(ITransmitImagePreparer preparer, FlattenBakeRequest request)
    {
        // The crop's real source-pixel rect -- the SAME rounding Crop itself applies, shared, not
        // reimplemented (CropGeometry's own doc comment).
        var crop = CropGeometry.Measure(request.Source.Width, request.Source.Height, request.CropRect);

        // 1) Bake scale. The natural choice is "one baked frame pixel per source pixel of the crop":
        // the background then passes through the bake essentially unresampled and the element is
        // rasterized at native source resolution, so the pipeline's own single downscale is the only
        // resample either of them sees -- exactly as if the element had been part of the photo.
        var scale = Math.Max(1.0, Math.Max(crop.Width / request.ContentWidth, crop.Height / request.ContentHeight));
        var budgetScale = Math.Sqrt(MaxFlattenBakePixels / ((double)request.ModeWidth * request.ModeHeight));
        scale = Math.Min(scale, Math.Max(1.0, budgetScale));
        var elementExtentPx = Math.Max(request.Element.Bounds.Width * request.ModeWidth, request.Element.Bounds.Height * request.ModeHeight);
        if (elementExtentPx > 0)
        {
            scale = Math.Min(scale, Math.Max(1.0, TransmitImageLimits.MaxElementResizeDimensionPx / elementExtentPx));
        }

        // Floored at the mode's own dimensions: never bake BELOW transmit resolution.
        var bakeWidth = Math.Max(request.ModeWidth, (int)Math.Round(scale * request.ModeWidth));
        var bakeHeight = Math.Max(request.ModeHeight, (int)Math.Round(scale * request.ModeHeight));
        // The ACTUAL per-axis scales after integer rounding -- used for the extraction geometry
        // below rather than the requested `scale`, so a sub-pixel rounding difference between the
        // axes can't shift the extracted rect.
        var bakeScaleX = (double)bakeWidth / request.ModeWidth;
        var bakeScaleY = (double)bakeHeight / request.ModeHeight;

        // 2) The frame itself: the real pipeline, at bake resolution, with ONLY this element and
        // adjustments explicitly at identity. ComposePreview is the existing fused
        // Crop->Resize->(skip identity adjustments)->ApplyTemplate chain -- no new pipeline.
        var baked = preparer.ComposePreview(
            request.Source, request.CropRect, bakeWidth, bakeHeight, request.PreserveAspect,
            new ImageAdjustments(), new TemplateDocument(Name: null, [request.Element]));

        // 3) Element bounds -> CONTENT-relative fractions (letterbox padding removed). Computed from
        // the 1x pad/content metrics the projection itself produced; identical at any uniform scale.
        var left = ((request.Element.Bounds.X * request.ModeWidth) - request.PadX) / request.ContentWidth;
        var top = ((request.Element.Bounds.Y * request.ModeHeight) - request.PadY) / request.ContentHeight;
        var right = (((request.Element.Bounds.X + request.Element.Bounds.Width) * request.ModeWidth) - request.PadX) / request.ContentWidth;
        var bottom = (((request.Element.Bounds.Y + request.Element.Bounds.Height) * request.ModeHeight) - request.PadY) / request.ContentHeight;

        // 4) -> integer SOURCE-crop-relative write-back rect, rounded OUTWARD, margined, clamped.
        // Localising the write-back is what keeps every background pixel outside the element's own
        // box bit-identical to the original source -- a capped bake scale then degrades only the
        // photo directly under the element, never the whole frame.
        var writeLeft = Math.Clamp((int)Math.Floor(left * crop.Width) - FlattenWriteBackMarginPx, 0, crop.Width);
        var writeTop = Math.Clamp((int)Math.Floor(top * crop.Height) - FlattenWriteBackMarginPx, 0, crop.Height);
        var writeRight = Math.Clamp((int)Math.Ceiling(right * crop.Width) + FlattenWriteBackMarginPx, 0, crop.Width);
        var writeBottom = Math.Clamp((int)Math.Ceiling(bottom * crop.Height) + FlattenWriteBackMarginPx, 0, crop.Height);
        var writeWidth = writeRight - writeLeft;
        var writeHeight = writeBottom - writeTop;
        if (writeWidth <= 0 || writeHeight <= 0)
        {
            return null;
        }

        // 5) The same rect, mapped back into the BAKED frame's own pixel space (content offset by
        // the letterbox pad, both scaled by the actual bake scale).
        var patchLeft = Math.Clamp((int)Math.Round((request.PadX * bakeScaleX) + (writeLeft / (double)crop.Width * request.ContentWidth * bakeScaleX)), 0, bakeWidth - 1);
        var patchTop = Math.Clamp((int)Math.Round((request.PadY * bakeScaleY) + (writeTop / (double)crop.Height * request.ContentHeight * bakeScaleY)), 0, bakeHeight - 1);
        var patchRight = Math.Clamp((int)Math.Round((request.PadX * bakeScaleX) + (writeRight / (double)crop.Width * request.ContentWidth * bakeScaleX)), patchLeft + 1, bakeWidth);
        var patchBottom = Math.Clamp((int)Math.Round((request.PadY * bakeScaleY) + (writeBottom / (double)crop.Height * request.ContentHeight * bakeScaleY)), patchTop + 1, bakeHeight);

        var patch = preparer.CropPixels(baked, patchLeft, patchTop, patchRight - patchLeft, patchBottom - patchTop);
        // preserveAspect: false -- the patch must land on the write-back rect EXACTLY. Under a
        // stretch crop the two aspects legitimately differ, and letterboxing here would black-bar
        // the photo underneath.
        var fitted = preparer.Resize(patch, writeWidth, writeHeight, preserveAspect: false);
        return preparer.Composite(request.Source, fitted, crop.X + writeLeft, crop.Y + writeTop);
    }

    private bool CanFlattenElement(ITemplateElementViewModel? element) =>
        !_isFlattening && element is not null && OverlayElements.Contains(element);

    /// <summary>TX workflow modernization plan, Phase 7 -- rasterise one element into the photo and
    /// drop it from the document. Async + <see cref="Task.Run"/>-offloaded because the bake is a
    /// genuine full-frame render at up to <see cref="MaxFlattenBakePixels"/> -- a multi-second
    /// synchronous stall on the UI thread would be a real regression on this one gesture.
    /// <para>Every input the bake depends on is captured up front and RE-VALIDATED after the await:
    /// an offloaded compute means the operator can rotate, undo, re-crop or drag the element while
    /// it runs, and applying a stale bake would corrupt the image with no visible cause. Discarding
    /// is the correct response, not silently applying it. <see cref="PushUndoSnapshot"/>
    /// deliberately runs AFTER the await, at the real mutation point, so an intervening edit isn't
    /// swallowed into this step.</para></summary>
    [RelayCommand(CanExecute = nameof(CanFlattenElement))]
    private async Task FlattenElementAsync(ITemplateElementViewModel? element)
    {
        if (element is null || !OverlayElements.Contains(element))
        {
            return;
        }

        var committed = await BakeElementIntoBackgroundAsync(element, new BakeOperation(
            SetBusy: busy =>
            {
                _isFlattening = busy;
                FlattenElementCommand.NotifyCanExecuteChanged();
            },
            FailedKey: "Panes.TxImageEditor.FlattenFailed",
            OutsideFrameKey: "Panes.TxImageEditor.FlattenOutsideFrame",
            StaleKey: "Panes.TxImageEditor.FlattenDiscardedStale",
            LogFailed: ex => Log.FlattenElementFailed(_logger, ex),
            LogStale: () => Log.FlattenElementDiscardedAsStale(_logger),
            BeforeCommit: () => Log.FlattenElementInvoked(_logger, element.GetType().Name)));
        if (!committed)
        {
            return;
        }

        // Adjustment sliders are NOT reset and the adjustments were NOT baked -- see
        // BakeElementIntoSource's own doc comment. Output is identical when they're at identity;
        // when they aren't, the baked pixels are now subject to them, which the operator is told
        // rather than left to discover.
        StatusMessage = BuildAdjustments().IsIdentity
            ? null
            : _localization.GetString("Panes.TxImageEditor.FlattenAdjustmentsNowApply");

        RecomputePreview();
    }

    /// <summary>What differs between Flatten and Demote inside <see cref="BakeElementIntoBackgroundAsync"/>.
    /// <paramref name="SetBusy"/> sets the command's own busy flag and refreshes its CanExecute;
    /// <paramref name="BeforeCommit"/> runs after the stale check, before the undo push.</summary>
    private sealed record BakeOperation(
        Action<bool> SetBusy,
        string FailedKey,
        string OutsideFrameKey,
        string StaleKey,
        Action<Exception> LogFailed,
        Action LogStale,
        Action? BeforeCommit = null);

    /// <summary>Shared body of <see cref="FlattenElementAsync"/> and
    /// <see cref="DemoteBackdropToBackgroundAsync"/>: bakes <paramref name="element"/> into the source
    /// off the UI thread, re-validates every bake input after the await, then removes the element and
    /// installs the baked source as a new baseline in one undo step. Returns <see langword="true"/>
    /// only when it committed; on every other path it has already set <see cref="StatusMessage"/>.
    /// <para>Every input the bake depends on is captured up front and RE-VALIDATED after the await:
    /// an offloaded compute means the operator can rotate, undo, re-crop or drag the element while
    /// it runs, and applying a stale bake would corrupt the image with no visible cause.
    /// <see cref="PushUndoSnapshot"/> deliberately runs AFTER the await, at the real mutation point,
    /// so an intervening edit isn't swallowed into this step.</para></summary>
    private async Task<bool> BakeElementIntoBackgroundAsync(ITemplateElementViewModel element, BakeOperation op)
    {
        if (!TryGetCropContentMetrics(out var padX, out var padY, out var contentWidth, out var contentHeight))
        {
            StatusMessage = _localization.GetString("Panes.TxImageEditor.FlattenUnavailable");
            return false;
        }

        var request = new FlattenBakeRequest(
            _originalSource, CropRect, PreserveAspect, BuildTemplateElement(element),
            _targetMode.ImageWidth, _targetMode.ImageHeight, padX, padY, contentWidth, contentHeight);
        var preparer = _preparer;

        IImageSource? baked;
        op.SetBusy(true);
        try
        {
            baked = await Task.Run(() => BakeElementIntoSource(preparer, request));
        }
        catch (Exception ex)
        {
            op.LogFailed(ex);
            StatusMessage = _localization.GetString(op.FailedKey);
            return false;
        }
        finally
        {
            op.SetBusy(false);
        }

        if (baked is null)
        {
            // Outside the frame: keep the element rather than silently delete something still visible on the canvas.
            StatusMessage = _localization.GetString(op.OutsideFrameKey);
            return false;
        }

        // Stale-result guard. Every one of these is an input the bake actually consumed:
        // TemplateElement is a record, so structural equality covers the element's geometry, text,
        // resolved macros, colours and effects in one comparison.
        if (!ReferenceEquals(_originalSource, request.Source)
            || !CropRect.Equals(request.CropRect)
            || PreserveAspect != request.PreserveAspect
            || !OverlayElements.Contains(element)
            || !BuildTemplateElement(element).Equals(request.Element))
        {
            op.LogStale();
            StatusMessage = _localization.GetString(op.StaleKey);
            return false;
        }

        op.BeforeCommit?.Invoke();
        PushUndoSnapshot();
        _suspendPreview = true;
        try
        {
            element.PropertyChanged -= OnOverlayElementPropertyChanged;
            OverlayElements.Remove(element);
            // Shrink, not clear: other group members stay selected and no disposed element stays in the set.
            if (SelectedOverlayElements.Contains(element))
            {
                SetSelection(SelectedOverlayElements.Where(e => !ReferenceEquals(e, element)));
            }

            // Generic IDisposable check -- see LoadTemplateIntoLiveEditor's own identical comment.
            if (element is IDisposable disposableElement)
            {
                disposableElement.Dispose();
            }

            // New generation: this source can never be reached from the old baseline by rotation,
            // so ApplyState must restore it by instance (see its own branch and _sourceBaseline's
            // doc comment). Baseline rotation is the CURRENT rotation -- the baked image is in
            // whatever orientation the editor is in right now.
            _sourceBaseline = baked;
            _sourceBaselineRotation = _rotationCount;
            ReplaceSourceAndWorkingCopy(baked);
        }
        finally
        {
            _suspendPreview = false;
        }

        return true;
    }
}
