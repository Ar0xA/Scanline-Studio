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
    /// <summary>Rotates the source 90° clockwise (always -- no direction parameter, matching the
    /// View's single Rotate button; 4 clicks returns to the original orientation), transforming
    /// (not resetting) the current crop rect and every canvas element's position/size along with it
    /// -- round-1 plan-review's own explicit design call: resetting would silently destroy
    /// deliberate framing/text work on the common "rotate after already cropping" case, and would
    /// break the 4-clicks-returns-to-start property the single-button UX depends on.
    /// <see cref="OverlayElementViewModel.FontSizeRelative"/> is deliberately left untouched (same
    /// reasoning as before Phase 1 -- <c>TransmitImagePreparer</c> computes the shrink-to-fit
    /// STARTING size against the FINAL mode-sized output's height, never the source's own
    /// orientation). **Phase 1 correction to this comment's own pre-Phase-1 claim**: rotation is no
    /// longer guaranteed to have zero effect on the RENDERED font size -- swapping a non-square
    /// element's Width/Height (below) changes what shrink-to-fit's search actually has to fit into,
    /// so a wide-short text box that rotates into a tall-narrow one can end up smaller. The
    /// 4-clicks-returns-to-start property still holds exactly regardless (W/H swap back after 4
    /// rotations, same as X/Y).</summary>
    [RelayCommand]
    private void Rotate()
    {
        Log.RotateInvoked(_logger);
        PushUndoSnapshot();

        _suspendPreview = true;
        try
        {
            RotateImageOnly();

            foreach (var element in OverlayElements)
            {
                // Same underlying point transform as the crop rect below: (x,y) -> (1-y, x) for a
                // 90° clockwise rotation (verified against this codebase's own top-left-origin,
                // Y-grows-downward convention -- TransmitImagePreparer.Crop/ApplyTemplate's pixel
                // math -- not assumed from a generic formula). Deliberately NOT clamped to [0,1]
                // (unlike the crop rect below) -- code-review finding: TxImageEditorPaneView.axaml.cs's
                // own drag handler explicitly allows free overflow past the image bounds ("clipped
                // at render time only", spec/07-image-pipeline.md), so clamping here would silently
                // relocate an element the user deliberately dragged off-canvas and break the
                // 4-clicks-returns-to-start property for it. Width/Height swap alongside the point
                // transform (Phase 1 addition) -- exactly the same swap TransformCropRectClockwise
                // already applies to the crop rect's own Width/Height below, for the same reason (a
                // 90° rotation of a box swaps which axis is "wide").
                if (element is LineElementViewModel line)
                {
                    // TX editor gap-items plan, line element -- plan-review round 1/3 finding: the
                    // generic X/Y/Width/Height transform below is a valid rotation for a BOX (whose
                    // X/Y/Width/Height are independent, real fields), but for a line -- whose
                    // Width/Height are DERIVED, unsigned magnitudes of the endpoints -- running the
                    // SAME transform through those derived properties turns a 90° rotation into a
                    // MIRROR (and collapses a horizontal line's Height to 0, then asks the Width
                    // setter to grow a zero extent under an ambiguous rule). Rotating BOTH endpoints
                    // directly with the identical per-point transform used below sidesteps the
                    // derived-property ambiguity entirely -- verified (round 2 plan-review): 4
                    // applications compose to the identity, same as the generic path's own
                    // 4-clicks-returns-to-start property, for every starting orientation including a
                    // degenerate point.
                    var (x1, y1) = (line.X1, line.Y1);
                    var (x2, y2) = (line.X2, line.Y2);
                    line.X1 = 1 - y1;
                    line.Y1 = x1;
                    line.X2 = 1 - y2;
                    line.Y2 = x2;
                    continue;
                }

                // TX editor gap-items plan, item 3 (perspective transform) -- a DELIBERATE
                // DIFFERENCE from the generic path just below, not "matches existing behavior": the
                // SAME per-point transform is applied DIRECTLY to all 4 corner fields (bypassing the
                // derived X/Y/Width/Height setters, which would incorrectly translate+scale a quad
                // instead of rotate it), without re-indexing which corner is "TopLeft" -- this DOES
                // rotate the warped visual content along with the canvas position (the map is
                // orientation-preserving, so winding/convexity survive, and 4 applications compose
                // to the identity, same as every other element kind's own 4-clicks-returns-to-start
                // property). Toggling perspective off afterward always adopts the CURRENT bbox (see
                // TogglePerspective's own doc comment) -- no special-case discontinuity to handle.
                if (element is PerspectiveElementViewModelBase { PerspectiveEnabled: true } warped)
                {
                    warped.RotateCornersInFrameClockwise();
                    continue;
                }

                var (x, y) = (element.X, element.Y);
                element.X = 1 - y;
                element.Y = x;
                (element.Width, element.Height) = (element.Height, element.Width);
            }

            CropRect = TransformCropRectClockwise(CropRect);

            // Code-review finding on spec/18-path-to-1.0.md High item 4: rotating the crop rect
            // via the 90°-clockwise transform above swaps its width/height along with the working
            // copy's own dimension swap -- for an already-aspect-locked rect, that leaves the
            // PIXEL aspect at the RECIPROCAL of _target's own (unchanged) aspect, while
            // LockAspectToMode still reads true. Re-fitting immediately closes the gap the same
            // way OnLockAspectToModeChanged does when the toggle is first engaged -- safe here
            // too: X/Y are untouched by the rect transform above, so the max-bounds this re-fit
            // computes from them stay valid, and the fit can only shrink, never grow past bounds.
            if (LockAspectToMode)
            {
                ApplyCropResizeAspectLocked(0, 0);
            }
        }
        finally
        {
            _suspendPreview = false;
        }

        NotifyCropRectDerivedPropertiesAndRecomputePreview();
    }

    /// <summary>The IMAGE-only half of a 90°-clockwise rotation -- <c>_originalSource</c>/
    /// <c>_workingCopy</c>/<c>WorkingCopyBitmap</c>/dimensions/per-element <c>ImageWidth</c>/
    /// <c>ImageHeight</c>, with NO coordinate transform (no <see cref="CropRect"/>/overlay X-Y
    /// change). Extracted from <see cref="Rotate"/> (spec/18-path-to-1.0.md Medium item, undo/redo
    /// sub-piece, round-1 plan-review blocker B3) so <see cref="ApplyState"/> can reconcile
    /// orientation by calling this directly, delta-rotation-count times, without going through the
    /// <see cref="Rotate"/> COMMAND -- that command's own <c>_suspendPreview = true; try { ... }
    /// finally { _suspendPreview = false; }</c> block would clear an OUTER suspension `ApplyState`
    /// already has in progress partway through the restore (`_suspendPreview` is a bare bool, not a
    /// re-entrant counter), and would also apply a coordinate transform on TOP of coordinates the
    /// snapshot already stores pre-transformed for the target orientation -- a double-transform
    /// bug. Callers own their own <c>_suspendPreview</c>/undo-push/recompute -- this method does
    /// none of that itself.</summary>
    private void RotateImageOnly()
    {
        // BuildWorkingCopy's own small-image fast path can return the source instance itself
        // (already covered by Constructor_OriginalWithinWorkingCopyBudget_UsesOriginalDirectlyAsWorkingCopy)
        // -- captured BEFORE reassigning _originalSource below, so a shared instance stays shared
        // (one Rotate call, not two independent copies where one used to be the same object).
        var wasShared = ReferenceEquals(_workingCopy, _originalSource);
        _originalSource = _preparer.Rotate(_originalSource);
        _workingCopy = wasShared ? _originalSource : _preparer.Rotate(_workingCopy);
        _rotationCount = (_rotationCount + 1) % 4;

        NotifyWorkingCopyGeometryChanged();
    }

    /// <summary>The WHOLE-SOURCE-REPLACEMENT counterpart to <see cref="RotateImageOnly"/> (TX
    /// workflow modernization plan, Phase 7 -- flatten): swaps <see cref="_originalSource"/> outright
    /// and rebuilds <c>_workingCopy</c> from scratch via <see cref="BuildWorkingCopy"/> rather than
    /// transforming the existing one (there is no rotate/transform relationship between a pre- and
    /// post-flatten image -- the whole photo changed, not just its orientation). Does NOT touch
    /// <see cref="_rotationCount"/>, <see cref="_sourceBaseline"/> or <see cref="_sourceBaselineRotation"/>
    /// -- callers own those, exactly as <see cref="RotateImageOnly"/>'s own doc comment says callers
    /// own <c>_suspendPreview</c>/undo/recompute. Shares <see cref="NotifyWorkingCopyGeometryChanged"/>
    /// with the rotate path so the two can't notify a different set of properties.</summary>
    private void ReplaceSourceAndWorkingCopy(IImageSource newSource)
    {
        _originalSource = newSource;
        _workingCopy = BuildWorkingCopy(newSource, _target, _preparer);
        NotifyWorkingCopyGeometryChanged();
    }

    /// <summary>Extracted verbatim from <see cref="RotateImageOnly"/> (TX workflow modernization
    /// plan, Phase 7) so <see cref="ReplaceSourceAndWorkingCopy"/> raises the exact same property
    /// changes -- see that method's own doc comment for why each of these is raised
    /// (Phase 7 rearchitecture: CanvasDisplay* and the SafeArea* pixel properties derive from
    /// WorkingCopyWidth/Height and have no notification of their own).</summary>
    private void NotifyWorkingCopyGeometryChanged()
    {
        // Auditor-found blocker (Tier-0 audit follow-up, production_audit.md): without this guard,
        // an await-crossing operation still in flight when Dispose() runs (e.g. ImportImageAsync/
        // FlattenElementAsync resuming after Cancel/Apply was clicked, neither gated busy) reaches
        // _workingCopyPool.Blit() against an already-disposed pool -- wasted work against a discarded
        // instance at best, an unhandled WriteableBitmap crash on a real (non-headless) render target
        // at worst. Same shape as RecomputePreviewCoalesced's own existing _disposed guard (see this
        // file's Dispose() doc comment).
        if (_disposed)
        {
            return;
        }

        WorkingCopyBitmap = _workingCopyPool.Blit(_workingCopy);
        OnPropertyChanged(nameof(WorkingCopyWidth));
        OnPropertyChanged(nameof(WorkingCopyHeight));
        OnPropertyChanged(nameof(WorkingCopyFooterText));
        // HasRealBackground/HasNoBackgroundOrOverlayElements both read _sourceBaseline, which every
        // caller of this method (RemoveBackground/PromoteBackgroundToBackdrop/
        // DemoteBackdropToBackground/FlattenElementAsync/ApplyState/LoadBackground) always reassigns
        // right before calling ReplaceSourceAndWorkingCopy -- see HasNoBackgroundOrOverlayElements's
        // own doc comment. HasRealBackground's own doc comment used to claim nothing needed a live
        // notification for it -- true until TxControlsPaneViewModel.CanLoadBackground started reading
        // it live (2026-09-15) to re-enable Browse/Stock the instant LoadBackground installs a real
        // photo.
        OnPropertyChanged(nameof(HasRealBackground));
        OnPropertyChanged(nameof(HasNoBackgroundOrOverlayElements));
        // Phase 7 rearchitecture: WorkingCopyWidth/Height changing also changes CanvasDisplayWidth/
        // Height even though ZoomFactor itself didn't move, and nothing else notifies it on this path.
        // User-reported bug (2026-09-15): "remove background... the 'safe area'-blue lines dont match
        // up" -- this comment used to claim Crop*Pixels didn't need re-raising here because they "get
        // their own re-notify from the CropRect reassignment in the Rotate() caller." True for Rotate,
        // but wrong for RemoveBackground/PromoteBackgroundToBackdrop/DemoteBackdropToBackground, which
        // deliberately leave CropRect untouched (see each one's own doc comment) while still swapping
        // in a working copy of a DIFFERENT pixel size -- CanvasDisplayWidth/Height changed, but
        // CropRect.X/Y/Width/Height (the actual VALUES) didn't, so CommunityToolkit's generated
        // OnCropRectChanged hook never fired and the crop-rect Border in the View kept whatever pixel
        // rect it last computed against the OLD CanvasDisplayWidth/Height. SafeAreaInsetPixels is
        // zoom-only, unaffected by a working-copy dimension change -- SafeAreaWidthPixels/HeightPixels
        // DO depend on CanvasDisplayWidth/Height though, same as any other CanvasDisplay-derived pixel
        // property, so they need the same re-raise here. This is now the SAME full list
        // OnZoomFactorChanged re-raises for the identical "CanvasDisplayWidth/Height moved" reason,
        // minus SafeAreaInsetPixels/ZoomPercentText (those two are ZoomFactor-only, not
        // WorkingCopyWidth/Height-derived, so a working-copy swap alone never invalidates them).
        OnPropertyChanged(nameof(CanvasDisplayWidth));
        OnPropertyChanged(nameof(CanvasDisplayHeight));
        OnPropertyChanged(nameof(SafeAreaWidthPixels));
        OnPropertyChanged(nameof(SafeAreaHeightPixels));
        OnPropertyChanged(nameof(CropLeftPixels));
        OnPropertyChanged(nameof(CropTopPixels));
        OnPropertyChanged(nameof(CropWidthPixels));
        OnPropertyChanged(nameof(CropHeightPixels));
        OnPropertyChanged(nameof(CropRightPixels));
        OnPropertyChanged(nameof(CropBottomPixels));
        // Same reasoning as OnZoomFactorChanged's own identical addition -- see that method's comment.
        OnPropertyChanged(nameof(PlacementPreviewLeftPixels));
        OnPropertyChanged(nameof(PlacementPreviewTopPixels));
        OnPropertyChanged(nameof(PlacementPreviewWidthPixels));
        OnPropertyChanged(nameof(PlacementPreviewHeightPixels));
        OnPropertyChanged(nameof(PlacementPreviewLineStart));
        OnPropertyChanged(nameof(PlacementPreviewLineEnd));
        OnPropertyChanged(nameof(GuideLineXPixels));
        OnPropertyChanged(nameof(GuideLineYPixels));

        foreach (var element in OverlayElements)
        {
            element.ImageWidth = CanvasDisplayWidth;
            element.ImageHeight = CanvasDisplayHeight;
        }

        // Same gap as the Crop*Pixels one above: font size/stroke thickness for text elements are
        // ALSO CanvasDisplayWidth/Height-derived (ComputeCanvasFontSize et al.) and OnZoomFactorChanged
        // already refreshes them on every zoom change -- a working-copy dimension swap needs the same
        // refresh, not just the element X/Y/W/H push the loop above already does.
        RefreshOverlayElementCanvasStyles();
    }

    /// <summary>(x,y,w,h) -&gt; (1-y-h, x, h, w) -- exact for 90°-multiple rotations (bounding box
    /// of the four corner points under the same (x,y) -&gt; (1-y,x) transform <see cref="Rotate"/>
    /// applies to overlay positions). Clamped to [0,1]: the subtraction can land a hair outside due
    /// to floating-point rounding (e.g. -1e-17), which would violate ApplyCropMove/ApplyCropResize's
    /// own x ∈ [0, 1-w] invariant.</summary>
    private static NormalizedRect TransformCropRectClockwise(NormalizedRect rect) => new(
        X: Math.Clamp(1 - rect.Y - rect.Height, 0, 1),
        Y: Math.Clamp(rect.X, 0, 1),
        Width: rect.Height,
        Height: rect.Width);

    // T0-12: the only one of this method's 4 callers (Rotate, NudgeCropResize, ApplyState are the
    // other 3, all one-shot discrete actions left synchronous) that fires on every PointerMoved
    // during a crop-handle drag -- coalesced accordingly.
    partial void OnCropRectChanged(NormalizedRect value) =>
        NotifyCropRectDerivedPropertiesAndRecomputePreview(coalesceRecompute: true);

    /// <summary>Split out from <see cref="OnCropRectChanged"/> so <see cref="Rotate"/> can call it
    /// unconditionally -- CommunityToolkit's generated <see cref="CropRect"/> setter skips this
    /// partial hook entirely when the new value structurally equals the old one (record struct
    /// equality), which a rotate performed before any crop edit hits every time (the initial
    /// <c>(0,0,1,1)</c> transforms to itself). Relying on the hook alone would leave the preview and
    /// pixel-derived properties stale after such a rotate.</summary>
    private void NotifyCropRectDerivedPropertiesAndRecomputePreview(bool coalesceRecompute = false)
    {
        if (coalesceRecompute)
        {
            RecomputePreviewCoalesced();
        }
        else
        {
            RecomputePreview();
        }

        OnPropertyChanged(nameof(CropLeftPixels));
        OnPropertyChanged(nameof(CropTopPixels));
        OnPropertyChanged(nameof(CropWidthPixels));
        OnPropertyChanged(nameof(CropHeightPixels));
        OnPropertyChanged(nameof(CropRightPixels));
        OnPropertyChanged(nameof(CropBottomPixels));

        // T0-12: when coalesceRecompute is true, the pipeline pass above runs on a LATER tick, not
        // synchronously after RefreshOverlayElementCanvasStyles() below -- this no longer
        // guarantees a same-tick ordering. Still correct either way: CanvasFontSize is filtered out
        // of OnOverlayElementPropertyChanged's own RecomputePreview trigger (it's canvas-chrome-only,
        // never feeds the real pipeline, see CanvasFontSize's own doc comment), and the deferred
        // pipeline reads live state regardless of when RefreshOverlayElementCanvasStyles ran.
        RefreshOverlayElementCanvasStyles();
    }

    // spec/18-path-to-1.0.md Medium item, undo/redo sub-piece -- On*Changing (fires BEFORE the
    // assignment, unlike On*Changed above which fires after) is the correct push point: it captures
    // the OLD value as the undo target. Coalesced (see PushUndoSnapshotCoalesced's own doc comment)
    // since these are all continuously-updating bound controls (sliders drag, PreserveAspect/
    // LockAspectToMode are toggles so coalescing is a no-op for them in practice, but using the
    // same helper uniformly is simpler than special-casing). CropRect deliberately has NO
    // On*Changing push here -- crop dragging is covered by the View-level drag-start mechanism
    // instead (avoids double-pushing the same logical gesture from two different mechanisms).
    partial void OnPreserveAspectChanging(bool value) => PushUndoSnapshotCoalesced(nameof(PreserveAspect));

    partial void OnLockAspectToModeChanging(bool value) => PushUndoSnapshotCoalesced(nameof(LockAspectToMode));

    partial void OnBrightnessChanging(double value) => PushUndoSnapshotCoalesced(nameof(Brightness));

    partial void OnContrastChanging(double value) => PushUndoSnapshotCoalesced(nameof(Contrast));

    partial void OnSaturationChanging(double value) => PushUndoSnapshotCoalesced(nameof(Saturation));

    partial void OnGammaChanging(double value) => PushUndoSnapshotCoalesced(nameof(Gamma));

    partial void OnSharpenChanging(double value) => PushUndoSnapshotCoalesced(nameof(Sharpen));

    partial void OnDenoiseChanging(double value) => PushUndoSnapshotCoalesced(nameof(Denoise));

    partial void OnPreserveAspectChanged(bool value)
    {
        RecomputePreview();
        RefreshOverlayElementCanvasStyles();
    }

    // T0-12: coalesced -- each fires on every drag-delta tick of the bound Slider (plain TwoWay
    // binding, no throttling at the View layer).
    partial void OnBrightnessChanged(double value) => RecomputePreviewCoalesced();

    partial void OnContrastChanged(double value) => RecomputePreviewCoalesced();

    partial void OnSaturationChanged(double value) => RecomputePreviewCoalesced();

    partial void OnGammaChanged(double value) => RecomputePreviewCoalesced();

    partial void OnSharpenChanged(double value) => RecomputePreviewCoalesced();

    partial void OnDenoiseChanged(double value) => RecomputePreviewCoalesced();

    /// <summary>Re-fits the CURRENT crop rect the instant the lock engages, rather than waiting for
    /// the next drag -- legacy's own `SBRatioClick` (`PicRect.cpp:653-662`) does the same on click.
    /// The `(0,0)` delta means <see cref="ApplyCropResizeAspectLocked"/> only performs its
    /// inner-fit/overflow/reject logic against the rect as it already stands.</summary>
    partial void OnLockAspectToModeChanged(bool value)
    {
        if (value)
        {
            ApplyCropResizeAspectLocked(0, 0);
        }
    }

    /// <summary>Every element property EXCEPT the ones filtered below feeds
    /// <see cref="BuildTemplateElement"/>/the real pipeline (Phase 1 widened this from the
    /// pre-Phase-1 text-only version, see each filtered name's own reasoning), so those are
    /// excluded here to avoid firing a full Crop-&gt;Resize-&gt;ApplyTemplate recompute for a change
    /// that can't affect its output; that matters concretely during a crop drag, where
    /// <see cref="RefreshOverlayElementCanvasStyles"/> pushes a new
    /// <see cref="OverlayElementViewModel.CanvasFontSize"/> to every TEXT element on every
    /// mouse-move frame (<see cref="NotifyCropRectDerivedPropertiesAndRecomputePreview"/>) -- without
    /// this filter, that would fire N additional redundant recomputes per frame instead of the one
    /// already performed. <c>LeftPixels</c>/<c>TopPixels</c>/<c>CanvasWidthPixels</c>/
    /// <c>CanvasHeightPixels</c> are filtered for the same "cascade, not a driver" reason the
    /// pre-Phase-1 code-review already established for LeftPixels/TopPixels: both
    /// <c>ImageWidth</c>/<c>ImageHeight</c> AND <c>X</c>/<c>Y</c>/<c>Width</c>/<c>Height</c> re-raise
    /// them (each concrete VM's own partial hooks), so filtering only the drivers would leave a real
    /// gap. X/Y/Width/Height themselves are NOT filtered -- they feed the pipeline directly (both
    /// element types) and still recompute on their own PropertyChanged; the pixel-cascade that
    /// follows is now just a redundant second signal for the same edit, correctly suppressed.
    /// <see cref="ITemplateElementViewModel.Z"/> is likewise NOT filtered (Phase 1 plan-review
    /// finding: layer order genuinely changes the rendered output, unlike <c>Locked</c> below).
    /// <see cref="ITemplateElementViewModel.Locked"/> IS filtered -- pure interaction state, can
    /// never affect the pipeline. TEXT-only properties that need this one element's own
    /// <c>CanvasFontSize</c> refreshed inline (Phase 1 widened this from FontSizeRelative-only: the
    /// rendered size is now a real shrink-to-fit result of content AND box, not a closed-form
    /// function of FontSizeRelative alone) are handled after the filter, before the shared
    /// recompute.</summary>
    private void OnOverlayElementPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ITemplateElementViewModel.ImageWidth)
            or nameof(ITemplateElementViewModel.ImageHeight)
            or nameof(OverlayElementViewModel.PreviewMetrics)
            or nameof(OverlayElementViewModel.CanvasFontSize)
            // Backlog item (user request, 2026-08-17): canvas-preview outline fix -- pure
            // canvas-chrome derived from StrokeThickness/StrokeColor (both already independently
            // drive a recompute via their own unfiltered PropertyChanged), same "cascade, not a
            // driver" reasoning as CanvasFontSize just above.
            or nameof(OverlayElementViewModel.CanvasStrokeThicknessPixels)
            or nameof(OverlayElementViewModel.ResolvedText)
            or nameof(ITemplateElementViewModel.LeftPixels)
            or nameof(ITemplateElementViewModel.TopPixels)
            or nameof(ITemplateElementViewModel.CanvasWidthPixels)
            or nameof(ITemplateElementViewModel.CanvasHeightPixels)
            or nameof(BoxElementViewModel.CanvasBorderThicknessPixels)
            or nameof(BoxElementViewModel.CanvasCornerRadiusPixels)
            or nameof(ImageElementViewModel.CanvasBitmap)
            // Round-1 code-review finding (TX editor gap-items plan, item 4b): CanvasBitmapFill is
            // the SAME cascade-of-an-already-unfiltered-driver shape as CanvasBitmap immediately
            // above (driven by BitmapFillSource, itself unfiltered) -- omitting it fired an extra
            // full Crop->Resize->ApplyTemplate pass on every picture pick.
            or nameof(OverlayElementViewModel.CanvasBitmapFill)
            or nameof(ITemplateElementViewModel.Locked)
            // Phase 6: pure interaction state (which/whether an element blocks canvas hit-testing),
            // never affects pipeline output -- same tier as Locked itself just above.
            or nameof(ImageElementViewModel.IsBackground)
            or nameof(ImageElementViewModel.BlocksHitTesting)
            // Tier B audit finding: IsSelected/IsEditingText are the SAME pure-interaction-state
            // shape as Locked/IsBackground/BlocksHitTesting just above -- neither can ever affect
            // pipeline output, but wasn't filtered, so clicking a different element on the canvas
            // (which flips IsSelected false on the old element and true on the new) fired TWO extra
            // full Crop->Resize->ApplyAdjustments->ApplyTemplate passes, two extra
            // RescanTemplateVariables sweeps, and two extra PreviewImage bitmap allocations for a
            // pure selection change -- output stayed correct, this is a perf-only fix.
            or nameof(ITemplateElementViewModel.IsSelected)
            or nameof(OverlayElementViewModel.IsEditingText)
            // Code-review nit, fixed here: HasStroke is a derived bool of StrokeColor (raised by
            // OnStrokeColorChanged), not new information -- without this, every stroke-color edit
            // fired two full Crop->Resize->ApplyTemplate passes (one from StrokeColor's own
            // notification below, one from this redundant follow-up).
            or nameof(OverlayElementViewModel.HasStroke)
            // Phase 8: HasShadow is the identical derived-bool-of-ShadowColor case as HasStroke
            // just above, same reasoning, same fix.
            or nameof(OverlayElementViewModel.HasShadow)
            // Phase 8: StrokeColorForPicker/ShadowColorForPicker are pure ColorPicker-binding view
            // state (see their own doc comments) -- they re-raise as a side effect of StrokeColor/
            // ShadowColor's own OnChanged hooks, which ALREADY drive a recompute; without this filter
            // every stroke/shadow color edit fired a redundant second Crop->Resize->ApplyTemplate
            // pass, the identical bug class HasStroke's own filter entry above already fixed once.
            or nameof(OverlayElementViewModel.StrokeColorForPicker)
            or nameof(OverlayElementViewModel.ShadowColorForPicker)
            // Phase 8 canvas-preview amendment: RotationTransform/ShadowRenderTransform/
            // ForegroundBrush are pure canvas-chrome derived from RotationDegrees/ShadowOffsetX/
            // ShadowOffsetY/ShadowColor/Color/GradientEnabled/GradientKind/GradientStartColor/
            // GradientEndColor -- all of which already independently drive a recompute via their own
            // (unfiltered) PropertyChanged, same "cascade, not a driver" reasoning as CanvasFontSize.
            or nameof(OverlayElementViewModel.RotationTransform)
            or nameof(OverlayElementViewModel.ShadowRenderTransform)
            or nameof(OverlayElementViewModel.ForegroundBrush)
            // Code-review finding (2026-09-01, box gradient fill): FillBrush is the SAME pure-canvas-
            // chrome-derived-from-FillColor/GradientEnabled/GradientKind/GradientStartColor/
            // GradientEndColor shape as ForegroundBrush above, same fix -- without this, every fill-
            // color/gradient edit fired two full recompute passes instead of one.
            or nameof(BoxElementViewModel.FillBrush)
            // Auditor usability review follow-up (2026-08-18): CanvasFontWeight/CanvasFontStyle are
            // the SAME pure-canvas-chrome-derived-from-a-real-property shape as RotationTransform
            // above (derived from Bold/Italic, which already independently drive a recompute).
            or nameof(OverlayElementViewModel.CanvasFontWeight)
            or nameof(OverlayElementViewModel.CanvasFontStyle)
            // Auditor usability review follow-up (2026-08-18): HasStack/StackColorForPicker/
            // CanvasStackStepXPixels/YPixels are the SAME derived-bool/picker-view/canvas-chrome
            // shapes as HasShadow/ShadowColorForPicker/ShadowRenderTransform above, same reasoning,
            // same fix.
            or nameof(OverlayElementViewModel.HasStack)
            or nameof(OverlayElementViewModel.StackColorForPicker)
            or nameof(OverlayElementViewModel.CanvasStackStepXPixels)
            or nameof(OverlayElementViewModel.CanvasStackStepYPixels)
            // Quick Style Flyout / Fill & Border flyout (TX workflow modernization plan, Phase 1):
            // *Px are pure px-unit views of FontSizeRelative/BorderThickness/CornerRadius, which
            // already independently drive a recompute via their own (unfiltered) PropertyChanged --
            // same "cascade, not a driver" reasoning as CanvasFontSize above.
            or nameof(OverlayElementViewModel.FontSizePx)
            or nameof(BoxElementViewModel.BorderThicknessPx)
            or nameof(BoxElementViewModel.CornerRadiusPx)
            // TX editor gap-items plan (2026-09-01, line element) -- same "pure px-unit/canvas-chrome
            // cascade of an already-unfiltered driver (StrokeThickness)" reasoning as
            // BorderThicknessPx/CornerRadiusPx/CanvasBorderThicknessPixels above. Both names are
            // unique to LineElementViewModel, so no sender-type check is needed here (unlike
            // X/Y/Width/Height just below, whose SAME names are real, unfiltered drivers for every
            // other element kind).
            or nameof(LineElementViewModel.CanvasStrokeThicknessPixels)
            or nameof(LineElementViewModel.StrokeThicknessPx)
            or nameof(LineElementViewModel.CanvasStartPoint)
            or nameof(LineElementViewModel.CanvasEndPoint)
            // TX editor gap-items plan, item 3 (perspective transform) -- pure canvas chrome, the
            // SAME "cascade of an already-unfiltered driver" tier as CanvasBitmap/FillBrush above.
            // WarpedCanvasBitmap/CanvasCorner0Point..3Point are identically-named on BOTH
            // ImageElementViewModel and BoxElementViewModel (one filter entry catches both, no
            // sender-type check needed, same as CanvasWidthPixels above).
            or nameof(ImageElementViewModel.WarpedCanvasBitmap)
            or nameof(ImageElementViewModel.CanvasCorner0Point)
            or nameof(ImageElementViewModel.CanvasCorner1Point)
            or nameof(ImageElementViewModel.CanvasCorner2Point)
            or nameof(ImageElementViewModel.CanvasCorner3Point)
            or nameof(ImageElementViewModel.ShowResizeHandles)
            or nameof(ImageElementViewModel.ShowPerspectiveCornerHandles)
            // Round-2 plan-review finding: NaturalX/Y/Width/Height are the REAL backing fields once
            // X/Y/Width/Height became mode-switched hand-written properties -- their own OnChanged
            // hooks re-raise the PUBLIC X/Y/Width/Height names (preserving the external notification
            // contract every existing AXAML binding relies on), so leaving Natural* itself unfiltered
            // would double-recompute every ORDINARY (non-perspective) geometry edit: once from the
            // real NaturalX notification, once more from the X cascade it triggers -- the exact
            // FillBrush/CanvasBitmapFill double-recompute bug class already fixed twice above.
            or nameof(ImageElementViewModel.NaturalX)
            or nameof(ImageElementViewModel.NaturalY)
            or nameof(ImageElementViewModel.NaturalWidth)
            or nameof(ImageElementViewModel.NaturalHeight)
            // Box-only chrome-neutralization cascades (round-2 plan-review fix) -- derived from
            // PerspectiveEnabled/FillColor/Gradient*/BorderThickness/Opacity, all of which already
            // independently drive a recompute.
            or nameof(BoxElementViewModel.EffectiveBackground)
            or nameof(BoxElementViewModel.EffectiveBorderThicknessPixels)
            or nameof(BoxElementViewModel.EffectiveOpacity))
        {
            return;
        }

        // TX editor gap-items plan (2026-09-01, line element), plan-review round 1 finding: for
        // EVERY other element kind, X/Y/Width/Height ARE the real backing fields, so they're
        // deliberately left OUT of the global filter above (they feed the pipeline directly). For a
        // LineElementViewModel specifically, X1/Y1/X2/Y2 are the real backing fields instead --
        // X/Y/Width/Height are DERIVED cascades of those (see LineElementViewModel.RaiseGeometryChanged's
        // own doc comment), so leaving them unfiltered for a line sender would double-recompute every
        // endpoint edit: once from the raw X1/Y1/X2/Y2 notification (still unfiltered, the real
        // driver), once more from this cascade -- the exact "FillBrush" double-recompute bug class
        // already fixed once for box gradient fill. Sender-typed, not name-only, since these same
        // names must stay unfiltered for every OTHER element kind.
        if (sender is LineElementViewModel
            && e.PropertyName is nameof(ITemplateElementViewModel.X)
                or nameof(ITemplateElementViewModel.Y)
                or nameof(ITemplateElementViewModel.Width)
                or nameof(ITemplateElementViewModel.Height))
        {
            return;
        }

        // TX editor gap-items plan, item 3 (perspective transform) -- the SAME "derived cascade of a
        // real driver" shape as the LineElementViewModel check just above, but MODE-GATED: for an
        // ImageElementViewModel/BoxElementViewModel, X/Y/Width/Height ARE the real drivers when
        // PerspectiveEnabled is false (every other case in this file relies on that), and become a
        // cascade of the 8 corner fields ONLY once perspective is on -- so this filter entry, unlike
        // the line one above, must check the flag too, not just the sender's type.
        if (sender is PerspectiveElementViewModelBase { PerspectiveEnabled: true }
            && e.PropertyName is nameof(ITemplateElementViewModel.X)
                or nameof(ITemplateElementViewModel.Y)
                or nameof(ITemplateElementViewModel.Width)
                or nameof(ITemplateElementViewModel.Height))
        {
            return;
        }

        if (sender is ITemplateElementViewModel previewElement)
        {
            RefreshElementPreviewMetrics(previewElement);
        }

        if (sender is OverlayElementViewModel textElement
            && e.PropertyName is nameof(OverlayElementViewModel.FontSizeRelative)
                or nameof(ITemplateElementViewModel.Width)
                or nameof(ITemplateElementViewModel.Height)
                or nameof(OverlayElementViewModel.Text)
                // Phase 4: FontFamily/StrokeThickness both feed ComputeCanvasFontSize's own
                // MeasureFittedFontSize call (family changes what's measured; stroke thickness
                // changes the fit-box-shrink allowance) -- StrokeColor going null<->set also
                // changes whether the stroke allowance applies at all (see ComputeCanvasFontSize's
                // own strokeThicknessRelative computation), so it needs the same refresh.
                or nameof(OverlayElementViewModel.FontFamily)
                or nameof(OverlayElementViewModel.StrokeThickness)
                or nameof(OverlayElementViewModel.StrokeColor)
                // Phase 8: shadow offset/rotation both feed the SAME MeasureFittedFontSize
                // fit-box-shrink allowance stroke does (see ComputeCanvasFontSize's own updated
                // doc comment) -- omitting any of these would desync CanvasFontSize from what the
                // real pipeline renders, the third occurrence of exactly this bug class.
                or nameof(OverlayElementViewModel.ShadowColor)
                or nameof(OverlayElementViewModel.ShadowOffsetX)
                or nameof(OverlayElementViewModel.ShadowOffsetY)
                or nameof(OverlayElementViewModel.RotationDegrees)
                // Auditor usability review follow-up (2026-08-18): Bold/Italic are the FOURTH
                // occurrence of this same bug class -- a bold (and, in a real italic font FILE
                // rather than a synthesized skew, potentially an italic) glyph can measure wider
                // than Regular at the same point size -- see MeasureFittedFontSize's own call site
                // comment. Both included rather than verifying Italic's own advance-width delta is
                // exactly zero for the two bundled families and risking that becoming a false claim
                // if a font file ever changes -- the recompute cost here is cheap (one shrink-to-fit
                // search), not worth a fragile "these two behave differently" special case.
                or nameof(OverlayElementViewModel.Bold)
                or nameof(OverlayElementViewModel.Italic)
                // Auditor usability review follow-up (2026-08-18): StackColor/StackStepX/StackStepY
                // are the FIFTH occurrence of this same bug class -- see ComputeCanvasFontSize's own
                // updated doc comment.
                or nameof(OverlayElementViewModel.StackColor)
                or nameof(OverlayElementViewModel.StackStepX)
                or nameof(OverlayElementViewModel.StackStepY)
                // User-requested (2026-09-15) grow-to-fill toggle -- feeds MeasureFittedFontSize's
                // own growToFill argument directly (see ComputeCanvasFontSize's own updated call),
                // so toggling it must recompute CanvasFontSize the same as every other fit-search
                // input above. Deliberately NOT added to the early-return filter list at the top of
                // this method -- unlike Locked/IsSelected/IsEditingText, this one changes rendered
                // output, so it must also fall through to this method's own trailing
                // RecomputePreviewCoalesced() call.
                or nameof(OverlayElementViewModel.GrowToFillEnabled))
        {
            textElement.CanvasFontSize = ComputeCanvasFontSize(textElement);
            textElement.CanvasStrokeThicknessPixels = ComputeCanvasStrokeThicknessPixels(textElement);
        }

        // Phase 6: FontFamily can change on the selected text element without SelectedOverlayElement
        // itself changing (the operator picks a different font from the ComboBox) -- IsFontUnavailable
        // needs to be re-evaluated for that case too, not just on selection change.
        if (sender is OverlayElementViewModel && e.PropertyName == nameof(OverlayElementViewModel.FontFamily)
            && ReferenceEquals(sender, SelectedOverlayElement))
        {
            OnPropertyChanged(nameof(IsFontUnavailable));
            OnPropertyChanged(nameof(FontFamilyPickerItems));
        }

        // design-fidelity Phase E (#15): keep the canvas selection readout live across every
        // geometry-driving change on the CURRENTLY selected element (drag/resize/rotate) -- cheap
        // (a string format, not a Crop->Resize->ApplyTemplate pass), so raised unconditionally here
        // rather than added to the early-return filter list above, sidestepping that filter's own
        // documented "a new derived property must be remembered by hand" failure mode.
        if (ReferenceEquals(sender, SelectedOverlayElement))
        {
            OnPropertyChanged(nameof(SelectionReadoutText));
            OnPropertyChanged(nameof(SelectedTextElementFontSizePx));
            OnPropertyChanged(nameof(SelectedTextElementStrokeThicknessPx));
            OnPropertyChanged(nameof(SelectedTextElementShadowOffsetXPx));
            OnPropertyChanged(nameof(SelectedTextElementShadowOffsetYPx));
            OnPropertyChanged(nameof(SelectedTextElementStackStepXPx));
            OnPropertyChanged(nameof(SelectedTextElementStackStepYPx));
            OnPropertyChanged(nameof(SelectedElementLeftPx));
            OnPropertyChanged(nameof(SelectedElementTopPx));
            OnPropertyChanged(nameof(SelectedElementWidthPx));
            OnPropertyChanged(nameof(SelectedElementHeightPx));
            OnPropertyChanged(nameof(SelectedBoxElementBorderThicknessPx));
            OnPropertyChanged(nameof(SelectedBoxElementCornerRadiusPx));
            // TX editor gap-items plan, item 3 -- same "raised unconditionally on every change to the
            // selected element" shape as everything else in this block; the GEOMETRY tab's Perspective
            // checkbox needs to reflect a toggle regardless of which property changed to cause it.
            OnPropertyChanged(nameof(SelectedElementPerspectiveEnabled));
        }

        // T0-12: coalesced -- fires on every PointerMoved while dragging/resizing a selected canvas
        // element (X/Y/Width/Height feed the pipeline directly, so aren't in the early-return filter
        // list above), same per-pointer-move hot path as the crop drag and adjustment sliders.
        RecomputePreviewCoalesced();
    }

    /// <summary>Realtime preview: the REAL <see cref="ITransmitImagePreparer"/> pipeline output
    /// against the small working copy (not a separately-drawn approximation) -- see
    /// spec/07-image-pipeline.md's "what 'TX-accurate' actually means this pass" note. Crop -&gt;
    /// Resize -&gt; ApplyAdjustments -&gt; ApplyTemplate, that exact order (element content must be
    /// rasterized at the FINAL mode dimensions, or a non-aspect-preserving stretch would smear
    /// already-drawn content). Phase 1: migrated from <c>ApplyOverlay</c>/<c>ImageOverlay</c> (text
    /// only) onto <c>ApplyTemplate</c>/<c>TemplateDocument</c> (polymorphic) -- <c>ApplyOverlay</c>
    /// itself is NOT deleted (Phase 0 had marked it for Phase-1 deletion, but it's still the
    /// regression-test anchor for the shared <c>DrawGlyphs</c> no-clip/wrapping-length=width path
    /// <c>ApplyTemplate</c>'s own text rendering reuses -- deletion deferred to a later cleanup,
    /// not part of Phase 1's real goal).</summary>
    private void RecomputePreview()
    {
        // Auditor-found blocker (Tier-0 audit follow-up, production_audit.md): same guard, same
        // reason, as NotifyWorkingCopyGeometryChanged's own -- this method reaches
        // RecomputePreviewPipeline()'s own _previewPool.Blit() call, reachable from await-crossing
        // operations (ImportImageAsync, FlattenElementAsync, and others) still in flight when
        // Dispose() runs.
        if (_disposed)
        {
            return;
        }

        // See _suspendPreview's own doc comment -- RotateCommand sets this while multiple overlay
        // elements' cascading PropertyChanged events (via OnOverlayElementPropertyChanged) and the
        // CropRect reassignment would otherwise each trigger this full pipeline against transiently
        // half-updated (rotated working copy, not-yet-transformed crop/overlay) state.
        if (_suspendPreview)
        {
            return;
        }

        // Phase 3: rescans for template-variable references on every real recompute -- piggybacks on
        // this method (already called on every mutation that could add/remove a {word} reference:
        // element add/remove/Text edit, Rotate, Undo/Redo) rather than a separate trigger mechanism.
        RescanTemplateVariables();
        RecomputePreviewPipeline();
    }

    private void RecomputePreviewCoalesced()
    {
        if (_suspendPreview)
        {
            return;
        }

        RescanTemplateVariables();

        if (_recomputePreviewScheduled)
        {
            return;
        }

        _recomputePreviewScheduled = true;
        // T0-12 real-window finding: real-window testing across several drag mechanisms (rapid
        // discrete drag gestures, and a dense sustained single-gesture drag) consistently confirmed
        // this coalesces correctly and stays responsive at DispatcherPriority.Input -- kept here
        // rather than the plan's initial DispatcherPriority.Background choice, since one dense
        // sustained-drag scenario showed Background go a long time without running (Input did not
        // reproduce that under the same scenario). Input still sits below Render/Normal, so a burst
        // of triggers still coalesces to one pass; it's simply less prone to falling behind a
        // continuous stream of Input-priority pointer events than Background is.
        Dispatcher.UIThread.Post(() =>
        {
            _recomputePreviewScheduled = false;
            if (!_disposed && !_suspendPreview)
            {
                RecomputePreviewPipeline();
            }
        }, DispatcherPriority.Input);
    }

    // T1-14 (production_audit.md): one call instead of 4 -- ComposePreview does the whole
    // Crop->Resize->ApplyAdjustments->ApplyTemplate chain against ONE underlying ImageSharp
    // representation instead of converting in/out once per stage, a real per-frame cost since this
    // fires on every coalesced pointer-move frame (RecomputePreviewCoalesced). Deliberately does NOT
    // touch BuildFinalOutput below, which still uses the old per-stage chain (a cold path, not the
    // hot preview one T1-14 is about) -- ComposePreview's own doc comment states it must stay
    // pixel-identical to that chain; this project's pixel-exact equivalence tests are the guard.
    private void RecomputePreviewPipeline()
    {
        var composited = _preparer.ComposePreview(
            _workingCopy, CropRect, _target.Width, _target.Height, PreserveAspect,
            BuildAdjustments(), BuildTemplateDocument());
        PreviewImage = _previewPool.Blit(composited);
    }

    private TemplateDocument BuildTemplateDocument() => new(Name: null, OverlayElements.Select(BuildTemplateElement).ToList());

    private ImageAdjustments BuildAdjustments() => new(Brightness, Contrast, Saturation, Gamma, Sharpen, Denoise);

    private static IImageSource BuildWorkingCopy(IImageSource source, TargetGeometry target, ITransmitImagePreparer preparer) =>
        DownsampleToBudget(source, target.Width * WorkingCopyScaleFactor, target.Height * WorkingCopyScaleFactor, preparer);

    /// <summary>Shared aspect-preserving downsample-to-budget, factored out of
    /// <see cref="BuildWorkingCopy"/> (code-review finding, Phase 2, spec/15-template-designer.md):
    /// an inserted image element previously bypassed this entirely, going straight from
    /// <see cref="IImageFileLoader.LoadOriginalAsync"/>'s full native resolution into
    /// <see cref="ImageSourceBitmapConverter.ToBitmap"/> (a synchronous, UI-thread, per-pixel
    /// <c>Marshal.Copy</c> loop -- ~96 MB/~24M-iteration for a 6000x4000 phone photo) AND into
    /// <see cref="RecomputePreview"/>'s own real pipeline on every drag frame. This is exactly what
    /// <see cref="WorkingCopyScaleFactor"/> already exists to prevent for the background image
    /// itself ("every interactive drag-frame recompute runs against this small copy, never the
    /// original") -- <see cref="InsertImageElement"/> now applies the same budget.</summary>
    private static IImageSource DownsampleToBudget(IImageSource source, int maxWidth, int maxHeight, ITransmitImagePreparer preparer)
    {
        var targetWidth = Math.Min(source.Width, maxWidth);
        var targetHeight = Math.Min(source.Height, maxHeight);
        if (targetWidth >= source.Width && targetHeight >= source.Height)
        {
            return source;
        }

        // Preserve source aspect while capping to the budget above, rather than a flat stretch --
        // this is a display/perf aid, not user-visible cropping/distortion.
        var scale = Math.Min((double)targetWidth / source.Width, (double)targetHeight / source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        return preparer.Resize(source, width, height, preserveAspect: false);
    }
}
