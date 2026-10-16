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
    /// <summary>Legacy's real precision mechanism, verified directly against
    /// `yoniq-old/YONIQ-main/PicRect.cpp:925-1001` (not assumed): plain arrow = 1px move,
    /// Ctrl+arrow = 16px move. Pixel deltas are computed against the ORIGINAL source's resolution
    /// (not the downsampled working copy) so nudge precision matches legacy's real granularity
    /// regardless of how small the interactive working copy is.</summary>
    public void NudgeCropMove(NudgeDirection direction, bool ctrl)
    {
        PushUndoSnapshot(); // one keypress = one discrete undo step, no coalescing needed
        var delta = ctrl ? 16 : 1;
        var (dxPixels, dyPixels) = DirectionToPixelDelta(direction, delta);
        ApplyCropMove((double)dxPixels / _originalSource.Width, (double)dyPixels / _originalSource.Height);
    }

    /// <summary>Phase 6 (spec/15-template-designer.md) -- element counterpart to
    /// <see cref="NudgeCropMove"/> above, same 1px/Ctrl+16px precision. Pixel deltas are computed
    /// against the WORKING COPY's resolution (<c>WorkingCopyWidth</c>/<c>WorkingCopyHeight</c>, same
    /// as <see cref="ITemplateElementViewModel.ImageWidth"/>/<c>ImageHeight</c> on the element
    /// itself), NOT <see cref="_originalSource"/> like the crop rect uses -- element X/Y are
    /// normalized against the working copy (see <see cref="ITemplateElementViewModel"/>'s own
    /// `LeftPixels`-style formulas), a different coordinate space than the crop rect's pre-resize
    /// one. Deliberately does NOT call <see cref="PushUndoSnapshot"/> itself (unlike
    /// <see cref="NudgeCropMove"/>, which has to -- <c>CropRect</c> is a single record property with
    /// no per-field change hook) -- assigning <c>X</c>/<c>Y</c> below already pushes an undo step via
    /// each element's own <c>OnXChanging</c>/<c>OnYChanging</c> -&gt;
    /// <c>PushUndoSnapshotForGeometryChange</c> hook, the SAME mechanism the sidebar X/Y TextBoxes
    /// already use; pushing here too would double up. MOVE only, no resize counterpart (plan-review-
    /// scoped decision -- <c>Shift+arrow</c> stays bound to crop-resize regardless of selection; see
    /// <c>OnCanvasKeyDown</c>'s own comment for the full reasoning). No-op if nothing is selected --
    /// the caller (<c>OnCanvasKeyDown</c>) already gates on a non-null, unlocked selection, but this
    /// method stays self-contained rather than trusting that.</summary>
    public void NudgeElement(NudgeDirection direction, bool ctrl)
    {
        // Code-review nit: same zero-size guard the drag path already has
        // (TxImageEditorPaneView.axaml.cs's OnCanvasPointerMoved) -- without it, a zero-sized
        // working copy divides X/Y into +-Infinity/NaN, which then propagates into the preview and
        // any saved template.
        if (SelectedOverlayElement is not { } element || WorkingCopyWidth <= 0 || WorkingCopyHeight <= 0)
        {
            return;
        }

        var delta = ctrl ? 16 : 1;
        var (dxPixels, dyPixels) = DirectionToPixelDelta(direction, delta);
        element.X += dxPixels / WorkingCopyWidth;
        element.Y += dyPixels / WorkingCopyHeight;
    }

    /// <summary>TX editor gap-items plan, item 2 (group-ops-lite) -- auditor finished-code-review
    /// finding: arrow-key nudge moved only the primary while a 2+ group was selected, an oversight
    /// (group MOVE is explicitly in scope for this feature, unlike resize/style/align) rather than a
    /// documented v1 cut. Same 1px/Ctrl+16px precision and pixel-space math as
    /// <see cref="NudgeElement"/>; Locked members are skipped individually rather than blocking the
    /// whole gesture, matching the drag path's own <c>_draggedGroup</c> capture (excludes Locked
    /// members, never rejects the drag outright). Each member's own <c>OnXChanging</c>/
    /// <c>OnYChanging</c>-&gt;<c>PushUndoSnapshotForGeometryChange</c> hook pushes the undo step,
    /// under the shared "OverlayGeometry" coalescing key, so an N-member group nudge still costs
    /// exactly one undo step.</summary>
    public void NudgeSelectedElements(NudgeDirection direction, bool ctrl)
    {
        if (WorkingCopyWidth <= 0 || WorkingCopyHeight <= 0)
        {
            return;
        }

        var delta = ctrl ? 16 : 1;
        var (dxPixels, dyPixels) = DirectionToPixelDelta(direction, delta);
        foreach (var element in SelectedOverlayElements)
        {
            if (element.Locked)
            {
                continue;
            }

            element.X += dxPixels / WorkingCopyWidth;
            element.Y += dyPixels / WorkingCopyHeight;
        }
    }

    /// <summary>Backlog item (auditor usability review, 2026-08-17): "Shift+arrow is permanently
    /// bound to crop resize (never element resize) with no keyboard element-resize path at all."
    /// Ctrl+Shift+arrow (see <c>TxImageEditorPaneView.OnRootKeyDown</c>) resizes the selected,
    /// unlocked element instead -- same 1px/Ctrl+16px precision as <see cref="NudgeElement"/>, but
    /// note Ctrl here is already consumed by the existing 1px/16px distinction, so this path is
    /// always 1px (matches plain Shift+arrow's own crop-resize precision, which has no Ctrl-fast
    /// variant either). Growing on Right/Down and shrinking on Left/Up mirrors
    /// <see cref="ApplyCropResize"/>'s own bottom-right-corner-grow convention exactly (same
    /// <see cref="DirectionToPixelDelta"/> sign, applied to Width/Height instead of the crop rect).
    /// Deliberately scoped to size only, not a 4-corner/8-handle drag system or an aspect-lock toggle
    /// (auditor's own item 18 also asked for those) -- <see cref="OnOverlayElementPointerPressed"/>'s
    /// own doc comment documents the single-corner-handle drag convention as an intentional,
    /// plan-reviewed design decision (mirrors legacy PicRect.cpp), not a gap; reversing that needs its
    /// own dedicated design pass, not a same-batch addition alongside 17 smaller, independent fixes.</summary>
    public void NudgeElementResize(NudgeDirection direction)
    {
        // Deliberately does NOT guard on Locked -- same division of responsibility as NudgeElement's
        // own doc comment: the real gate is the call site's own SelectedOverlayElement is { Locked:
        // false } check (TxImageEditorPaneView.OnRootKeyDown), matching every other Locked check in
        // this editor living at the call site, not duplicated inside the mutation method itself.
        if (SelectedOverlayElement is not { } element || WorkingCopyWidth <= 0 || WorkingCopyHeight <= 0)
        {
            return;
        }

        // TX editor gap-items plan, line element -- plan-review round 1/3 finding: a line has no
        // 8-handle box-resize concept at all (2 endpoint handles only), so this keyboard shortcut has
        // nothing to redefine, rather than something to fix -- a no-op, not a special-cased
        // Width/Height MATH the way ApplySnappedElementBounds' own line branch needed. Same reasoning
        // as that method's own doc comment, applied to the simpler "nothing to do" case here.
        if (element is LineElementViewModel)
        {
            return;
        }

        var (dxPixels, dyPixels) = DirectionToPixelDelta(direction, 1);
        element.Width = Math.Max(element.Width + (dxPixels / WorkingCopyWidth), MinNormalizedElementResizeSize);
        element.Height = Math.Max(element.Height + (dyPixels / WorkingCopyHeight), MinNormalizedElementResizeSize);
    }

    /// <summary>Applies <see cref="TxImageEditorPaneView.SnapElementBoundsToGrid"/>'s own already-
    /// computed result to <paramref name="element"/> as ONE atomic undo step (code-review finding):
    /// each of the 4 property assignments below has its own <c>On*Changing</c> hook that pushes a
    /// COALESCED undo snapshot (same mechanism <see cref="NudgeElement"/> above relies on) -- but
    /// that coalescing window is cleared by a background-priority dispatcher continuation that has
    /// virtually always already run by the time a pointer-release (where the snap fires) reaches
    /// here, well after the drag gesture's own last pointer-move. Left as 4 separate coalesced
    /// pushes, a snapped drag would need TWO Undos to get back to the pre-drag state (one for the
    /// unsnapped drag, one for the snap) -- not the "one gesture, one undo step" convention every
    /// other structural mutation in this editor follows (see <see cref="SetAsBackdrop"/> for the
    /// same <c>_suspendPreview</c> + single explicit push pattern this mirrors).</summary>
    public void ApplySnappedElementBounds(ITemplateElementViewModel element, double x, double y, double width, double height)
    {
        if (element is LineElementViewModel line)
        {
            // TX editor gap-items plan, line element -- plan-review round 2/3 finding: the
            // caller's own x/y/width/height were computed generically off DERIVED Width/Height
            // (see SnapElementBoundsToGrid's own doc comment -- it floors both to
            // MinNormalizedElementSize, then folds that floor back into a recomputed CENTER),
            // which for a degenerate (axis-aligned) line silently produces a wrong center, not
            // just a discardable Width/Height. Ignored entirely here, not partially applied --
            // each ENDPOINT is snapped independently instead (matching every other element's own
            // "snap by edges," not by center), gated on SnapToGrid since this method is ALSO
            // reached by the separate alignment-guide-snap path (which runs regardless of
            // SnapToGrid -- an ungated branch would grid-snap a line even with grid-snap off).
            // Alignment-guide snapping itself is out of scope for a line in v1 (deliberate scope
            // cut, not designed) -- when SnapToGrid is off, this is correctly a no-op.
            if (!SnapToGrid)
            {
                return;
            }

            var (snappedX1, snappedY1, snappedX2, snappedY2) =
                (SnapValueToGrid(line.X1), SnapValueToGrid(line.Y1), SnapValueToGrid(line.X2), SnapValueToGrid(line.Y2));

            // Code-review finding: the CALLER's own "did anything change" guard (TxImageEditorPaneView.
            // OnCanvasPointerReleased, `x != element.X || ...`) compares against the derived box, which
            // this branch ignores entirely -- so it can spuriously fire for a line whose real endpoints
            // were ALREADY grid-aligned (the exact case the doc comment above describes: a degenerate
            // line's floored-then-refolded box never actually matches its own endpoints). Re-checked
            // here, against what this branch actually would write, so a genuinely-unchanged snap
            // doesn't push a dead undo step (the first Ctrl+Z after such a drop would otherwise
            // silently do nothing -- the exact failure mode this method's own doc comment, further
            // down, exists to prevent for every OTHER element kind). Tolerance-based, NOT exact `==`
            // (own test-caught finding): SnapValueToGrid's own divide-then-multiply round-trip can
            // land on a different bit pattern than the ORIGINAL value even when both represent the
            // same already-aligned grid line -- e.g. 0.35 round-trips to 0.35000000000000003 -- so an
            // exact-equality check would spuriously call this a "change" for exactly the already-
            // aligned case this guard exists to catch.
            const double epsilon = 1e-9;
            if (Math.Abs(snappedX1 - line.X1) < epsilon && Math.Abs(snappedY1 - line.Y1) < epsilon
                && Math.Abs(snappedX2 - line.X2) < epsilon && Math.Abs(snappedY2 - line.Y2) < epsilon)
            {
                return;
            }

            PushUndoSnapshot();
            _suspendPreview = true;
            try
            {
                line.X1 = snappedX1;
                line.Y1 = snappedY1;
                line.X2 = snappedX2;
                line.Y2 = snappedY2;
            }
            finally
            {
                _suspendPreview = false;
            }

            RecomputePreview();
            return;
        }

        // TX editor gap-items plan, item 3 (perspective transform) -- v1 scope cut: grid-snap and
        // alignment-guide-snap are both deliberately deferred for a warped element (same tier as the
        // line case's own "out of scope in v1" alignment-guide note above). Checked BEFORE
        // PushUndoSnapshot (round-3 plan-review nit) so a snap-drop on a warped element pushes no
        // dead undo step, not just skips the 4 writes.
        if (element is PerspectiveElementViewModelBase { PerspectiveEnabled: true })
        {
            return;
        }

        PushUndoSnapshot();
        _suspendPreview = true;
        try
        {
            element.X = x;
            element.Y = y;
            element.Width = width;
            element.Height = height;
        }
        finally
        {
            _suspendPreview = false;
        }

        RecomputePreview();
    }

    // Same rounding formula as TxImageEditorPaneView.SnapElementBoundsToGrid's own private `Round`
    // local (View-side, not referenceable from this VM) -- deliberately duplicated, not shared, same
    // "separate, deliberately identical constant" convention MinNormalizedElementResizeSize's own doc
    // comment already establishes for its own View-side counterpart (MinNormalizedElementSize).
    private static double SnapValueToGrid(double value, double gridSize = 0.05) =>
        Math.Round(value / gridSize, MidpointRounding.AwayFromZero) * gridSize;

    /// <summary>TX editor gap-items plan, line element -- snap-on-drop for ONE endpoint after a
    /// <see cref="DragMode.LineEndpoint"/>-equivalent drag (<c>TxImageEditorPaneView.OnCanvasPointerReleased</c>'s
    /// own new branch), NOT <see cref="ApplySnappedElementBounds"/> -- that method's own line branch
    /// snaps BOTH endpoints together (a whole-line move-drop); an endpoint drag only ever moves the
    /// ONE endpoint the operator is dragging, the other must stay exactly where it was. Same
    /// tolerance-based no-op guard as that method's own line branch (round-2 code-review finding
    /// there: exact equality spuriously fires for an already-grid-aligned coordinate, since
    /// <see cref="SnapValueToGrid"/>'s own divide-then-multiply round-trip can land on a different
    /// bit pattern than the original value) -- deliberately reusing the identical epsilon, not a
    /// re-derived one.</summary>
    public void ApplySnappedLineEndpoint(LineElementViewModel line, bool isFirstEndpoint)
    {
        var (currentX, currentY) = isFirstEndpoint ? (line.X1, line.Y1) : (line.X2, line.Y2);
        var (snappedX, snappedY) = (SnapValueToGrid(currentX), SnapValueToGrid(currentY));

        const double epsilon = 1e-9;
        if (Math.Abs(snappedX - currentX) < epsilon && Math.Abs(snappedY - currentY) < epsilon)
        {
            return;
        }

        PushUndoSnapshot();
        _suspendPreview = true;
        try
        {
            if (isFirstEndpoint)
            {
                line.X1 = snappedX;
                line.Y1 = snappedY;
            }
            else
            {
                line.X2 = snappedX;
                line.Y2 = snappedY;
            }
        }
        finally
        {
            _suspendPreview = false;
        }

        RecomputePreview();
    }

    /// <summary>Shift+arrow resizes the crop rect's bottom-right corner by 1px and auto-engages
    /// stretch mode (<see cref="PreserveAspect"/> = false) -- legacy's own real behavior
    /// (`SBStrach->Down = TRUE` in the same verified source). Also clears
    /// <see cref="LockAspectToMode"/> for the same reason and the same legacy line: `SBRatio`/
    /// `SBStrach`/`SBNStrach` are a mutually-exclusive group in legacy, so engaging stretch mode on
    /// Shift+arrow also disengages legacy's own keep-aspect radio -- keyboard nudge stays free-form
    /// on both axes independently of whatever the drag-resize lock toggle currently reads
    /// (spec/18-path-to-1.0.md High item 4, round-1 plan-review finding).</summary>
    public void NudgeCropResize(NudgeDirection direction)
    {
        // One keypress = one discrete undo step (undo/redo sub-piece) -- pushed once up front, then
        // _suspendPreview-guarded (same pattern as Rotate/ApplyState) so the PreserveAspect/
        // LockAspectToMode assignments below don't ALSO fire their own On*Changing-coalesced pushes
        // via the mutually-exclusive-group side effect and turn one keypress into up to 3 steps.
        PushUndoSnapshot();
        _suspendPreview = true;
        try
        {
            PreserveAspect = false;
            LockAspectToMode = false;
            var (dxPixels, dyPixels) = DirectionToPixelDelta(direction, 1);
            ApplyCropResize((double)dxPixels / _originalSource.Width, (double)dyPixels / _originalSource.Height);
        }
        finally
        {
            _suspendPreview = false;
        }

        NotifyCropRectDerivedPropertiesAndRecomputePreview();
    }

    /// <summary>Called by the View exactly once per CROP drag gesture (move or resize), on the
    /// FIRST real (nonzero-delta) <c>PointerMoved</c> after a <c>StartDrag</c> -- NOT on
    /// <c>PointerPressed</c> itself (a bare click that never moves would otherwise push a no-op
    /// undo step), and NOT from <see cref="ApplyCropMove"/>/<see cref="ApplyCropResize"/>
    /// themselves (those are the shared per-frame helpers hit on EVERY pointer-move during a drag;
    /// pushing there would flood the stack the same way the coalesced slider path avoids). Public
    /// because gesture start/end is only observable at the View layer (pointer events), unlike the
    /// scalar sliders' own VM-level <c>On*Changing</c> hooks. Overlay-element drags do NOT use this
    /// method (code-review finding, folded in) -- they push via
    /// <see cref="ITemplateElementViewModel.PushUndoSnapshotForGeometryChange"/> instead, a path
    /// that also covers typed X/Y/Width/Height TextBox edits, which this drag-only method never
    /// would.</summary>
    public void PushUndoSnapshotForDragGesture() => PushUndoSnapshot();

    public void DragCropMove(double dxNormalized, double dyNormalized) => ApplyCropMove(dxNormalized, dyNormalized);

    public void DragCropResize(double dxNormalized, double dyNormalized) => ApplyCropResize(dxNormalized, dyNormalized);

    private void ApplyCropMove(double dx, double dy)
    {
        var newX = Math.Clamp(CropRect.X + dx, 0, 1 - CropRect.Width);
        var newY = Math.Clamp(CropRect.Y + dy, 0, 1 - CropRect.Height);
        CropRect = CropRect with { X = newX, Y = newY };
    }

    private void ApplyCropResize(double dx, double dy)
    {
        if (LockAspectToMode)
        {
            ApplyCropResizeAspectLocked(dx, dy);
            return;
        }

        var newWidth = Math.Clamp(CropRect.Width + dx, MinNormalizedCropSize, 1 - CropRect.X);
        var newHeight = Math.Clamp(CropRect.Height + dy, MinNormalizedCropSize, 1 - CropRect.Y);
        CropRect = CropRect with { Width = newWidth, Height = newHeight };
    }

    /// <summary>Inner-fit to <see cref="_target"/>'s own aspect ratio -- the largest
    /// target-aspect box that fits within the raw (unconstrained) dragged box. Monotonic and stable
    /// regardless of how a drag gesture is split across pointer-move events, unlike a "which axis
    /// moved more this event" heuristic (frame-rate/event-granularity dependent for the same
    /// physical gesture -- considered and discarded, spec/18-path-to-1.0.md High item 4's own
    /// design doc). Deliberately NOT a port of legacy's own `AdjustRatio` branch rule (round-1
    /// plan-review finding: that rule compares ABSOLUTE lengths in two different coordinate spaces,
    /// not an aspect ratio, and can actually GROW the crop box on a shrink drag -- a real
    /// difference, not a simplification. The overflow-handling shape below (shrink the OTHER axis
    /// proportionally, never an independent per-axis clamp) does mirror legacy's own
    /// `PicRect.cpp:182-189` in effect (code-review correction: `:178-179` is inside an
    /// `#if 0`-disabled block, not live code -- the real overflow handling is `:182-189`, both the
    /// X- and Y-overflow halves).</summary>
    private void ApplyCropResizeAspectLocked(double dx, double dy)
    {
        var targetAspect = (double)_target.Width / _target.Height;
        var minWidthPixels = MinNormalizedCropSize * WorkingCopyWidth;
        var minHeightPixels = MinNormalizedCropSize * WorkingCopyHeight;
        var maxWidthPixels = (1 - CropRect.X) * WorkingCopyWidth;
        var maxHeightPixels = (1 - CropRect.Y) * WorkingCopyHeight;

        // 1) Raw, unconstrained delta, floored to the per-axis minimum -- NOT capped to the max
        // here, which would change which axis step 2 picks as oversized. Flooring guards against a
        // fast drag pushing a raw axis to zero/negative (e.g. dragging the handle above CropRect.Y
        // in one pointer-move event), which would otherwise corrupt the ratio test below.
        var rawWidthPixels = Math.Max(minWidthPixels, (CropRect.Width + dx) * WorkingCopyWidth);
        var rawHeightPixels = Math.Max(minHeightPixels, (CropRect.Height + dy) * WorkingCopyHeight);

        // 2) Shrink whichever axis is proportionally oversized relative to targetAspect.
        double widthPixels, heightPixels;
        if (rawWidthPixels / rawHeightPixels > targetAspect)
        {
            heightPixels = rawHeightPixels;
            widthPixels = heightPixels * targetAspect;
        }
        else
        {
            widthPixels = rawWidthPixels;
            heightPixels = widthPixels / targetAspect;
        }

        // 3) Boundary overflow: shrink the OTHER axis proportionally to pull back in -- never an
        // independent per-axis clamp, which would re-break the aspect this method enforces.
        if (widthPixels > maxWidthPixels)
        {
            widthPixels = maxWidthPixels;
            heightPixels = widthPixels / targetAspect;
        }

        if (heightPixels > maxHeightPixels)
        {
            heightPixels = maxHeightPixels;
            widthPixels = heightPixels * targetAspect;
        }

        // 4) If the aspect-correct box that fits within both the bounds AND the minimum floor is
        // EMPTY, there is no valid resize to make -- reject it and leave CropRect exactly as it
        // was, rather than emit a rect that violates the lock (round-1 plan-review blocker: a real,
        // deterministic case -- narrow available width + wide target aspect -- not pathological).
        if (widthPixels < minWidthPixels || heightPixels < minHeightPixels)
        {
            return;
        }

        CropRect = CropRect with { Width = widthPixels / WorkingCopyWidth, Height = heightPixels / WorkingCopyHeight };
    }

    private static (int Dx, int Dy) DirectionToPixelDelta(NudgeDirection direction, int magnitude) => direction switch
    {
        NudgeDirection.Up => (0, -magnitude),
        NudgeDirection.Down => (0, magnitude),
        NudgeDirection.Left => (-magnitude, 0),
        NudgeDirection.Right => (magnitude, 0),
        _ => (0, 0),
    };
}
