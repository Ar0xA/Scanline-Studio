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
    /// <summary>Phase 2 (spec/15-template-designer.md) "set as backdrop" -- moves an existing
    /// element to full-frame (X=0.5,Y=0.5,Width=1,Height=1, covering the whole canvas under the
    /// CENTER-anchored convention) and to the very BOTTOM of the z-order
    /// (<c>Min(Z) - 1</c>, mirrors <see cref="NextZ"/>'s own max+1-for-top pattern, just for the
    /// bottom -- no Z==0 special case, consistent with Phase 0's own "no Z==0 special case"
    /// decision). Not restricted to image elements at the VM layer, but Phase 2's own AXAML only
    /// exposes this action on image-element rows.
    /// <para>[Plan-review blocker, fixed here] Setting Z alone is NOT enough -- the interactive
    /// canvas draws in <see cref="OverlayElements"/>' own COLLECTION order (see
    /// <see cref="MoveElementUp"/>'s own doc comment for why: <c>ZIndex</c> bound on a DataTemplate
    /// root has no effect, confirmed via Avalonia DevTools in Phase 1), so this ALSO moves the
    /// element to collection index 0, or the mini-preview (real <c>ApplyTemplate</c>, Z-based) and
    /// the interactive canvas would desync immediately -- exactly the bug class real-window review
    /// already caught once for <see cref="MoveElementUp"/>/<see cref="MoveElementDown"/>.</para>
    /// <para>Wrapped in <see cref="_suspendPreview"/> (same pattern as <see cref="Rotate"/>'s own
    /// multi-element geometry loop) so the 4 geometry-property assignments below don't ALSO each
    /// trigger their own <see cref="ITemplateElementViewModel.PushUndoSnapshotForGeometryChange"/>
    /// coalesced push on top of this method's own explicit <see cref="PushUndoSnapshot"/> -- without
    /// it, one click would push 2 undo steps instead of 1.</para>
    /// <para>Background/backdrop naming work (2026-09-15): clears <c>IsBackground</c> on any OTHER
    /// image element first -- a real pre-existing gap (this method never did so) that let two
    /// backdrops coexist, silently confusing <see cref="SendToBack"/>'s own "floor above the nearest
    /// backdrop" logic (picks the wrong one via <c>FirstOrDefault</c>). Fixed here rather than
    /// separately because <see cref="PromoteBackgroundToBackdrop"/>, added in the same change, is a
    /// second path that creates a backdrop and would otherwise double the odds of hitting it.</para></summary>
    [RelayCommand]
    private void SetAsBackdrop(ITemplateElementViewModel? element)
    {
        // Code-review finding: element could be a stale reference no longer in OverlayElements
        // (e.g. a queued click racing an Undo, which replaces every element wholesale -- see
        // ApplyState's own doc comment) -- IndexOf would then return -1, and
        // OverlayElements.Move(-1, 0) throws ArgumentOutOfRangeException out of a command handler.
        // Same "index < 0 means not found, no-op" guard as MoveElementUp/MoveElementDown, checked
        // BEFORE PushUndoSnapshot so a stale click doesn't leave a bogus undo step behind either.
        var index = element is null ? -1 : OverlayElements.IndexOf(element);
        if (index < 0)
        {
            return;
        }

        PushUndoSnapshot();
        _suspendPreview = true;
        try
        {
            // TX editor gap-items plan, item 3 (perspective transform) -- turn perspective off
            // FIRST, so the geometry writes below land on Natural fields, not the corners. Placed
            // inside this SAME _suspendPreview window (not before PushUndoSnapshot/before the try)
            // so its own coalesced undo push correctly no-ops here -- one undo step for the whole
            // command, not two (round-3 plan-review finding).
            if (element is ImageElementViewModel { PerspectiveEnabled: true } warpedImage)
            {
                warpedImage.DisablePerspectiveKeepingBounds();
            }

            // Single-backdrop invariant (see this method's own doc comment): at most one image
            // element is ever the backdrop at a time.
            foreach (var other in OverlayElements.OfType<ImageElementViewModel>())
            {
                if (!ReferenceEquals(other, element) && other.IsBackground)
                {
                    other.IsBackground = false;
                }
            }

            element!.X = 0.5;
            element.Y = 0.5;
            element.Width = 1;
            element.Height = 1;
            // At least one element (this one) is always in OverlayElements here, so Min(Z) is safe
            // without the empty-collection special case NextZ() needs for its own max+1 case.
            element.Z = OverlayElements.Min(e => e.Z) - 1;
            OverlayElements.Move(index, 0);
            element.Locked = true;
            // Phase 6 (spec/15-template-designer.md): IsBackground + the auto-lock above together
            // let the crop rect underneath become reachable again (see
            // ImageElementViewModel.BlocksHitTesting) -- SetAsBackdropCommand is only ever bound
            // from the image element's own DataTemplate (see this method's own doc comment), so
            // `element` is always really an ImageElementViewModel in practice; IsBackground simply
            // isn't part of the shared ITemplateElementViewModel interface (text/box elements have
            // no such concept), same reasoning as SetAsBackdropCommand itself living only on
            // ImageElementViewModel.
            if (element is ImageElementViewModel image)
            {
                image.IsBackground = true;
            }
        }
        finally
        {
            _suspendPreview = false;
        }

        RecomputePreview();
    }

    /// <summary>Background/backdrop naming work (2026-09-15): true once the editor has a REAL
    /// background photo loaded (Browse/Stock/Copy-to-TX), as opposed to the still-blank placeholder
    /// <see cref="OpenBlankEditorAsync"/> seeds every editor with. Read fresh by the View's own
    /// canvas-context-menu <c>Opened</c> handler (same
    /// "no live bound bool today, View computes a View-owned check right before the menu shows"
    /// pattern the Save Template item there already established), not an <c>[ObservableProperty]</c>
    /// -- its own PropertyChanged is instead raised manually from <see cref="NotifyWorkingCopyGeometryChanged"/>
    /// (2026-09-15: <c>TxControlsPaneViewModel.CanLoadBackground</c> now reads this LIVE, to
    /// re-disable Browse/Stock the instant <see cref="LoadBackground"/> installs a real photo).
    /// <see cref="HasNoBackgroundOrOverlayElements"/> below needs the identical treatment, same
    /// reasoning.</summary>
    public bool HasRealBackground => _sourceBaseline is not BlankImageSource;

    /// <summary>User-reported bug (2026-09-15): "once i removed the background however stock browse
    /// and open editor are still greyed out." <c>TxControlsPaneViewModel.CanChangeSourceOrMode</c>
    /// gates Browse/Stock/Open Editor on <c>_currentEditorIsBlank</c> (a one-shot snapshot from
    /// editor-OPEN time) plus <see cref="HasUnsavedEdits"/> being false -- <see cref="RemoveBackground"/>
    /// pushes a real undo step, so <see cref="HasUnsavedEdits"/> flips true and that gate stays
    /// locked even though the operator just deliberately cleared everything there was to protect.
    /// This is the live, CURRENT-state equivalent of that snapshot: true when there is genuinely
    /// nothing left that a Browse/Stock click would silently discard -- no real background AND no
    /// overlay elements (text/box/line/image; a backdrop counts, since it's an <see cref="OverlayElements"/>
    /// member like any other). <see cref="TxControlsPaneViewModel.IsCurrentEditorBlankAndUntouched"/>
    /// OR's this in alongside its existing snapshot check, so a genuinely blank-and-empty editor stays
    /// switchable-away-from regardless of how it got that way, while an editor with OTHER real work
    /// (added text, an inserted image) still correctly stays locked. Raised from
    /// <see cref="NotifyWorkingCopyGeometryChanged"/> (the shared choke point every
    /// <c>_sourceBaseline</c>-affecting command already funnels through) and from the
    /// <see cref="OverlayElements"/>.<c>CollectionChanged</c> subscription wired in the
    /// constructor.</summary>
    public bool HasNoBackgroundOrOverlayElements => !HasRealBackground && OverlayElements.Count == 0;

    /// <summary>User-requested (2026-09-15, background/backdrop naming work): the reverse of
    /// <see cref="SetAsBackdrop"/>. Bakes the CURRENT background photo -- crop and adjustments
    /// already applied, since a backdrop element gets no adjustment sliders of its own (adjustments
    /// stay scoped to the background being edited, same reasoning
    /// <see cref="ITransmitImagePreparer.ApplyTemplate"/>'s own doc comment gives for why an
    /// already-full-frame image element never gets adjustments reapplied either) -- into a new,
    /// full-frame, locked image element at the bottom of the stack, using the exact
    /// Crop -&gt; Resize -&gt; ApplyAdjustments pipeline order <see cref="ITransmitImagePreparer"/>'s own
    /// interface doc comments require. Then resets the background to blank, so the operator sees
    /// immediately that the photo moved, not duplicated. Reachable from the canvas's own empty-area
    /// right-click menu; refused with a status message (not a silent no-op) if invoked with nothing
    /// real loaded yet.
    /// <para><see cref="CropRect"/>/<see cref="PreserveAspect"/> are deliberately left UNTOUCHED --
    /// auditor-found blocker: <see cref="BuildTemplateElement"/>/<c>ProjectRectToCropRelative</c>
    /// projects every OTHER overlay element's bounds relative to <see cref="CropRect"/> and the
    /// letterbox padding <see cref="TryGetCropContentMetrics"/> derives from it, so resetting
    /// <see cref="CropRect"/> here silently resizes/repositions every text/box/line/other-image
    /// element in the transmitted frame -- not just the backdrop's own crop framing. Same reasoning
    /// <see cref="FlattenElementAsync"/>'s own doc comment states for why IT never resets
    /// <see cref="CropRect"/> either. The fresh blank background is shown through whatever crop
    /// window is currently set, exactly as Flatten leaves it -- the operator can re-crop afterward if
    /// they want a different framing on the new blank canvas.</para></summary>
    [RelayCommand]
    private void PromoteBackgroundToBackdrop()
    {
        if (_sourceBaseline is BlankImageSource)
        {
            StatusMessage = _localization.GetString("Panes.TxImageEditor.PromoteNoBackground");
            return;
        }

        PushUndoSnapshot();
        _suspendPreview = true;
        try
        {
            // Single-backdrop invariant (see SetAsBackdrop's own doc comment): at most one image
            // element is ever the backdrop at a time.
            foreach (var other in OverlayElements.OfType<ImageElementViewModel>())
            {
                if (other.IsBackground)
                {
                    other.IsBackground = false;
                }
            }

            var cropped = _preparer.Crop(_originalSource, CropRect);
            var resized = _preparer.Resize(cropped, _targetMode.ImageWidth, _targetMode.ImageHeight, PreserveAspect);
            var baked = _preparer.ApplyAdjustments(resized, Adjustments);

            var z = OverlayElements.Count == 0 ? 0 : OverlayElements.Min(e => e.Z) - 1;
            var element = CreateImageElement(NewImageElementDefaults(baked, new ImageSourceOrigin(ImageSourceKind.Promoted, null)) with
            {
                X = 0.5, Y = 0.5, Width = 1, Height = 1, Z = z, Locked = true,
                IsBackground = true, NaturalPixelWidth = _targetMode.ImageWidth, NaturalPixelHeight = _targetMode.ImageHeight,
            });
            OverlayElements.Insert(0, element);

            var placeholder = new BlankImageSource(_targetMode.ImageWidth, _targetMode.ImageHeight, BlankImageSource.DefaultColor);
            // New generation, same reasoning as BakeElementIntoBackgroundAsync's own identical assignment: this
            // source can never be reached from the old baseline by rotation, so ApplyState must
            // restore it by instance.
            _sourceBaseline = placeholder;
            _sourceBaselineRotation = _rotationCount;
            ReplaceSourceAndWorkingCopy(placeholder);
        }
        finally
        {
            _suspendPreview = false;
        }

        RecomputePreview();
    }

    /// <summary>User-requested (2026-09-15): a plain destructive clear, distinct from
    /// <see cref="PromoteBackgroundToBackdrop"/> -- that one PRESERVES the current photo (as a new
    /// backdrop element); this one discards it outright. Reachable from the canvas's own empty-area
    /// right-click menu, next to Promote.
    /// <para>User-reported feedback (2026-09-15): an earlier draft armed/required a second click to
    /// confirm before actually clearing -- removed. This command already pushes a real, working undo step (see
    /// <c>RemoveBackgroundCommand_UndoRestoresTheOriginalBackground</c>), so a second confirming click
    /// is redundant friction, not a safety net -- Undo already IS the safety net. One click, done;
    /// refused as a silent no-op only when the background is already blank (nothing real to discard,
    /// so nothing to undo either).</para>
    /// <para><see cref="CropRect"/> is deliberately left UNTOUCHED -- same auditor-found blocker
    /// <see cref="PromoteBackgroundToBackdrop"/>'s own doc comment explains: resetting it here would
    /// reproject every OTHER overlay element's bounds, not just clear the background.</para></summary>
    [RelayCommand]
    private void RemoveBackground()
    {
        if (_sourceBaseline is BlankImageSource)
        {
            return;
        }

        PushUndoSnapshot();
        _suspendPreview = true;
        try
        {
            var placeholder = new BlankImageSource(_targetMode.ImageWidth, _targetMode.ImageHeight, BlankImageSource.DefaultColor);
            _sourceBaseline = placeholder;
            _sourceBaselineRotation = _rotationCount;
            ReplaceSourceAndWorkingCopy(placeholder);
        }
        finally
        {
            _suspendPreview = false;
        }

        RecomputePreview();
    }

    /// <summary>User-requested (2026-09-15): "even if there are elements on the canvas, if no
    /// background has been picked before, i should be able to load one later also, not only as first
    /// canvas element." Before this, Browse/Stock ALWAYS discarded the whole editor and opened a
    /// brand-new one (<c>TxControlsPaneViewModel.OpenEditorWithLoadedSourceAsync</c>) -- correct when
    /// replacing an EXISTING real background (a genuinely disruptive "start over" action), but wrong
    /// when there was never a real background to begin with: <see cref="OverlayElements"/>/adjustments/
    /// crop are already completely independent of the background (RemoveBackground/
    /// PromoteBackgroundToBackdrop/DemoteBackdropToBackground above all prove this by leaving
    /// OverlayElements untouched on every background swap), so there was never anything for the
    /// wholesale editor-replace to actually protect in this specific case.
    /// <para>Public (not a <c>[RelayCommand]</c>) since this is called programmatically by
    /// <c>TxControlsPaneViewModel.OpenEditorForSourceAsync</c> on the ALREADY-open editor instance --
    /// no XAML control binds to it directly, same shape as <see cref="NotifyTransmitAvailabilityChanged"/>.
    /// Refuses (silent no-op) when a real background already exists -- the caller's own gate
    /// (<see cref="HasRealBackground"/>) is the intended guard, this is the same defensive body-level
    /// backstop every other geometry/background command in this class already documents.</para>
    /// <para><see cref="CropRect"/> is deliberately left UNTOUCHED, same reasoning
    /// <see cref="RemoveBackground"/>/<see cref="PromoteBackgroundToBackdrop"/>'s own doc comments
    /// give: resetting it here would reproject every OTHER overlay element's bounds too, not just
    /// install a background.</para></summary>
    public void LoadBackground(IImageSource source)
    {
        if (HasRealBackground)
        {
            return;
        }

        PushUndoSnapshot();
        _suspendPreview = true;
        try
        {
            _sourceBaseline = source;
            _sourceBaselineRotation = _rotationCount;
            ReplaceSourceAndWorkingCopy(source);
        }
        finally
        {
            _suspendPreview = false;
        }

        RecomputePreview();
    }

    /// <summary>TX editor gap-items plan, item 3 (perspective transform) -- toggles
    /// <see cref="ImageElementViewModel.PerspectiveEnabled"/>/<see cref="BoxElementViewModel.PerspectiveEnabled"/>.
    /// Uses this method's OWN non-coalesced <see cref="PushUndoSnapshot"/> (not the coalesced
    /// per-property push every ordinary geometry edit uses) -- a coalesced push from a toggle click
    /// landing inside an open coalescing window from a preceding drag would be silently swallowed,
    /// making the toggle un-undoable (round-3 plan-review nit). Wrapped in
    /// <see cref="_suspendPreview"/>, same shape as <see cref="SetAsBackdrop"/>/
    /// <see cref="Rotate"/>, so the corner-seed/writeback mutations below don't ALSO each push their
    /// own coalesced step on top of this one explicit push.
    /// <para>Enabling seeds the 4 corners from the CURRENT X/Y/Width/Height bbox; disabling writes
    /// the CURRENT bbox (of the corners as they are right now, NOT a preserved "pre-warp original")
    /// back into the Natural fields -- the only choice internally consistent with
    /// <see cref="ITemplateElementViewModel.ToSnapshot"/> already doing the identical "derived bbox becomes the base
    /// X/Y/Width/Height" thing for every Undo/Redo snapshot of a warped element (round-2 plan-review
    /// finding: this plan originally left that choice ambiguous across 3 different call sites).</para></summary>
    [RelayCommand]
    private void TogglePerspective(ITemplateElementViewModel? element)
    {
        var index = element is null ? -1 : OverlayElements.IndexOf(element);
        if (index < 0)
        {
            return;
        }

        PushUndoSnapshot();
        _suspendPreview = true;
        try
        {
            switch (element)
            {
                case PerspectiveElementViewModelBase { PerspectiveEnabled: false } flat:
                    // Element rotation (2026-09-20): Rotation/Perspective are mutually exclusive
                    // (TemplateImageElement.RotationDegrees's own doc comment) -- reset here, inside
                    // THIS suspend window, so it collapses into the same one undo step as the
                    // corner-seed below rather than pushing a second one.
                    flat.RotationDegrees = 0;
                    flat.SeedCornersFromBounds();
                    flat.PerspectiveEnabled = true;
                    break;
                case PerspectiveElementViewModelBase { PerspectiveEnabled: true } warped:
                    warped.DisablePerspectiveKeepingBounds();
                    break;
            }
        }
        finally
        {
            _suspendPreview = false;
        }

        RecomputePreview();
    }

    /// <summary>Element rotation (2026-09-20) -- shared Rotate 90°/180° commands for all 4 element
    /// types (<see cref="ITemplateElementViewModel.RotateClockwise90Command"/>'s own doc comment).
    /// Text/Box/Image add to their own persisted <c>RotationDegrees</c> (each element's own
    /// <c>OnRotationDegreesChanging</c> hook already pushes its own undo step -- no explicit
    /// <see cref="PushUndoSnapshot"/> needed here, same as every other single-property style/geometry
    /// edit in this class). Line has no persisted angle -- it rotates its own endpoints directly, in
    /// canvas/working-copy PIXEL space via <see cref="TemplateLineGeometry.RotateEndpoints"/> (see
    /// that helper's own doc comment for why normalized-space rotation would be aspect-wrong), wrapped
    /// in an explicit <see cref="PushUndoSnapshot"/>/<see cref="_suspendPreview"/> pair so 4 endpoint
    /// writes collapse into ONE undo step instead of 4 separately-coalesced ones. Same 3-gate Locked
    /// pattern as <see cref="CanFitSelectedImageToSafeArea"/>/<see cref="FitSelectedImageToSafeArea"/> --
    /// <see cref="CanRotateSelectedElement"/> here, the body backstop inside
    /// <see cref="RotateSelectedElement"/>, AXAML <c>IsEnabled</c> bound to each element's own
    /// <see cref="ITemplateElementViewModel.CanRotate"/>.</summary>
    private bool CanRotateSelectedElement() => SelectedOverlayElement?.CanRotate == true;

    [RelayCommand(CanExecute = nameof(CanRotateSelectedElement))]
    private void RotateClockwise90() => RotateSelectedElement(90);

    [RelayCommand(CanExecute = nameof(CanRotateSelectedElement))]
    private void RotateCounterclockwise90() => RotateSelectedElement(-90);

    [RelayCommand(CanExecute = nameof(CanRotateSelectedElement))]
    private void Rotate180() => RotateSelectedElement(180);

    /// <summary>Clockwise-positive, same convention as <see cref="TemplateTextElement.RotationDegrees"/>
    /// -- wraps into <c>[0, 360)</c> so repeated rotation never accumulates an unbounded value.</summary>
    private static double NormalizeDegrees(double degrees) => ((degrees % 360) + 360) % 360;

    private void RotateSelectedElement(double clockwiseDegrees)
    {
        if (SelectedOverlayElement is not { CanRotate: true } element)
        {
            return;
        }

        switch (element)
        {
            case OverlayElementViewModel text:
                text.RotationDegrees = NormalizeDegrees(text.RotationDegrees + clockwiseDegrees);
                break;
            case PerspectiveElementViewModelBase boxOrImage:
                boxOrImage.RotationDegrees = NormalizeDegrees(boxOrImage.RotationDegrees + clockwiseDegrees);
                break;
            case LineElementViewModel line:
                RotateLineEndpoints(line, clockwiseDegrees);
                break;
        }

        RecomputePreview();
    }

    private void RotateLineEndpoints(LineElementViewModel line, double clockwiseDegrees)
    {
        var (newX1, newY1, newX2, newY2) = TemplateLineGeometry.RotateEndpoints(
            line.X1, line.Y1, line.X2, line.Y2, clockwiseDegrees, line.ImageWidth, line.ImageHeight);

        PushUndoSnapshot();
        _suspendPreview = true;
        try
        {
            line.X1 = newX1;
            line.Y1 = newY1;
            line.X2 = newX2;
            line.Y2 = newY2;
        }
        finally
        {
            _suspendPreview = false;
        }
    }
}
