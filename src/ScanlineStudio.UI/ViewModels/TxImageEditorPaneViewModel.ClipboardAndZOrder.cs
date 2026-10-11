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
    private bool CanDuplicate() => SelectedOverlayElement is not null;

    /// <summary>Phase 6 (spec/15-template-designer.md) -- clones the selected element (any type,
    /// unlike <see cref="AddPlateBehindText"/> which is text-only) with a small position offset,
    /// inserted at <see cref="NextZ"/> (top of stack, same convention as <see cref="AddOverlayElement"/>/
    /// <see cref="AddBoxElement"/> -- NOT <see cref="AddPlateBehindText"/>'s own insert-behind
    /// pattern). If the duplicated element is a background image element, the CLONE has
    /// <see cref="ImageElementViewModel.IsBackground"/>/<see cref="ITemplateElementViewModel.Locked"/>
    /// cleared before insert (plan-review risk) -- otherwise Duplicate would produce a second
    /// full-frame, top-Z, locked, hit-test-passthrough copy that covers the whole canvas and is
    /// itself unreachable by canvas click. A plain, independent, unlocked copy is what "duplicate"
    /// means regardless of what was duplicated.</summary>
    [RelayCommand(CanExecute = nameof(CanDuplicate))]
    private void Duplicate()
    {
        if (SelectedOverlayElements.Count > 1)
        {
            DuplicateGroup(SelectedOverlayElements.ToList());
            return;
        }

        if (SelectedOverlayElement is not { } selected)
        {
            return;
        }

        InsertClonedSnapshot(selected.ToSnapshot());
    }

    /// <summary>Group-ops-lite (TX editor gap-items plan, item 2) -- clones every member of a
    /// multi-selection in ONE undo step. Clones are built in Z order (not selection/click order,
    /// which round-1 plan-review flagged as the wrong sort key -- click order would silently
    /// re-stack overlapping elements), each via the same per-kind offset logic
    /// <see cref="InsertClonedSnapshot"/> uses for a single element, so a duplicated group keeps its
    /// original relative layout and simply lands as a new, independently-selected group on top.</summary>
    private void DuplicateGroup(IReadOnlyList<ITemplateElementViewModel> elements)
    {
        PushUndoSnapshot();
        var clones = new List<ITemplateElementViewModel>();
        foreach (var element in elements.OrderBy(e => e.Z))
        {
            const double offset = 0.02;
            var snapshot = OffsetSnapshotForClone(element.ToSnapshot(), offset);
            var copy = CreateElementFromSnapshot(snapshot);
            OverlayElements.Add(copy);
            clones.Add(copy);
        }

        SetSelection(clones);
        RecomputePreview();
    }

    /// <summary>Backlog item (user request, 2026-08-17): factored out of <see cref="Duplicate"/> so
    /// <see cref="PasteElement"/> (Copy/Cut/Paste addendum) can reuse the identical
    /// offset/Z/background-clearing logic instead of a second, driftable copy -- both commands mean
    /// the exact same thing ("insert an independent clone of this snapshot"), just sourced
    /// differently (the currently-selected element vs. <see cref="_clipboardSnapshot"/>). Returns the
    /// newly-created element (auditor usability review, 2026-08-17 -- <see cref="PasteElement"/> uses
    /// this to cascade repeated pastes, see its own doc comment).</summary>
    private ITemplateElementViewModel InsertClonedSnapshot(RawElementSnapshot snapshot)
    {
        var offsetSnapshot = OffsetSnapshotForClone(snapshot, 0.02);

        PushUndoSnapshot();
        var copy = CreateElementFromSnapshot(offsetSnapshot);
        OverlayElements.Add(copy);
        SelectedOverlayElement = copy;
        RecomputePreview();
        return copy;
    }

    /// <summary>Extracted (group-ops-lite, TX editor gap-items plan item 2) from
    /// <see cref="InsertClonedSnapshot"/>'s own per-kind offset switch so <see cref="DuplicateGroup"/>
    /// can apply the identical offset logic to each group member without a second, driftable copy.</summary>
    private RawElementSnapshot OffsetSnapshotForClone(RawElementSnapshot snapshot, double offset) => snapshot switch
    {
        RawImageElementSnapshot image => OffsetImageSnapshot(image, offset),
        RawTextElementSnapshot text => text with
        {
            X = Math.Clamp(text.X + offset, 0, 1),
            Y = Math.Clamp(text.Y + offset, 0, 1),
            Z = NextZ(),
        },
        RawBoxElementSnapshot box => OffsetBoxSnapshot(box, offset),
        // Plan-review-flagged real trap: offsetting the base X/Y here (like every case above)
        // would do NOTHING for a line -- CreateElementFromSnapshot's own line case reads ONLY
        // X1/Y1/X2/Y2 (the sole truth, see RawLineElementSnapshot's own doc comment), never the
        // base fields, so a clone/duplicate/paste would land EXACTLY on top of the original at
        // the same position (though a new Z, so at least not perfectly indistinguishable) -- the
        // `var other => other` fallthrough below would have hit this silently.
        // Code-review finding: a bare `Math.Clamp` PER ENDPOINT (like every case above) is wrong
        // here specifically -- an off-canvas line is legal (Rotate's own doc comment: elements
        // may sit "free overflow" past [0,1], clipped only at render time), and clamping each
        // endpoint independently can move one endpoint closer to the other than the other,
        // distorting the line's own length/angle on duplicate. OffsetLineSnapshot computes ONE
        // delta valid for BOTH endpoints on each axis, so the line's shape survives exactly (or
        // the clone doesn't move on that axis at all, if the line already spans past what ANY
        // single shared shift could keep in-bounds).
        RawLineElementSnapshot line => OffsetLineSnapshot(line, offset),
        var other => other,
    };

    /// <summary>Extracted (2nd-round code-review nit) so the shared-per-axis delta is computed ONCE
    /// each, not twice via duplicated call sites in a switch-expression arm -- a future edit to one
    /// of two copy-pasted calls and not its twin would silently reintroduce the exact distortion bug
    /// <see cref="ClampSharedOffsetDelta"/> exists to prevent, with no compiler warning.</summary>
    private RawLineElementSnapshot OffsetLineSnapshot(RawLineElementSnapshot line, double offset)
    {
        var dx = ClampSharedOffsetDelta(offset, line.X1, line.X2);
        var dy = ClampSharedOffsetDelta(offset, line.Y1, line.Y2);
        return line with { X1 = line.X1 + dx, X2 = line.X2 + dx, Y1 = line.Y1 + dy, Y2 = line.Y2 + dy, Z = NextZ() };
    }

    /// <summary>TX editor gap-items plan, item 3 (perspective transform) -- a perspective-enabled
    /// element's corners are the SAME "must shift by ONE shared delta, not clamp each independently"
    /// case <see cref="OffsetLineSnapshot"/>'s own doc comment already documents for a line's two
    /// endpoints, generalized to 4 points: an independent per-corner clamp would shear the quad.
    /// <paramref name="image"/>.<c>X</c>/<c>Y</c> (the Natural-field fallback, used only if perspective
    /// is later toggled off) are shifted by the SAME delta too, so they stay a reasonable value
    /// rather than going stale.</summary>
    private RawImageElementSnapshot OffsetImageSnapshot(RawImageElementSnapshot image, double offset)
    {
        if (!image.PerspectiveEnabled)
        {
            return image with
            {
                X = Math.Clamp(image.X + offset, 0, 1),
                Y = Math.Clamp(image.Y + offset, 0, 1),
                Z = NextZ(),
                IsBackground = false,
                Locked = false,
            };
        }

        var dx = ClampSharedOffsetDelta(offset, image.Corner0X, image.Corner1X, image.Corner2X, image.Corner3X);
        var dy = ClampSharedOffsetDelta(offset, image.Corner0Y, image.Corner1Y, image.Corner2Y, image.Corner3Y);
        return image with
        {
            X = image.X + dx,
            Y = image.Y + dy,
            Corner0X = image.Corner0X + dx,
            Corner0Y = image.Corner0Y + dy,
            Corner1X = image.Corner1X + dx,
            Corner1Y = image.Corner1Y + dy,
            Corner2X = image.Corner2X + dx,
            Corner2Y = image.Corner2Y + dy,
            Corner3X = image.Corner3X + dx,
            Corner3Y = image.Corner3Y + dy,
            Z = NextZ(),
            IsBackground = false,
            Locked = false,
        };
    }

    /// <inheritdoc cref="OffsetImageSnapshot"/>
    private RawBoxElementSnapshot OffsetBoxSnapshot(RawBoxElementSnapshot box, double offset)
    {
        if (!box.PerspectiveEnabled)
        {
            return box with
            {
                X = Math.Clamp(box.X + offset, 0, 1),
                Y = Math.Clamp(box.Y + offset, 0, 1),
                Z = NextZ(),
            };
        }

        var dx = ClampSharedOffsetDelta(offset, box.Corner0X, box.Corner1X, box.Corner2X, box.Corner3X);
        var dy = ClampSharedOffsetDelta(offset, box.Corner0Y, box.Corner1Y, box.Corner2Y, box.Corner3Y);
        return box with
        {
            X = box.X + dx,
            Y = box.Y + dy,
            Corner0X = box.Corner0X + dx,
            Corner0Y = box.Corner0Y + dy,
            Corner1X = box.Corner1X + dx,
            Corner1Y = box.Corner1Y + dy,
            Corner2X = box.Corner2X + dx,
            Corner2Y = box.Corner2Y + dy,
            Corner3X = box.Corner3X + dx,
            Corner3Y = box.Corner3Y + dy,
            Z = NextZ(),
        };
    }

    /// <summary>Clamps a single shift amount so EVERY value in <paramref name="values"/> (2 for a
    /// line's endpoint pair, 4 for a perspective element's corners on one axis) stays inside
    /// <c>[0,1]</c> when shifted by the SAME delta -- see <see cref="InsertClonedSnapshot"/>'s own
    /// line-case doc comment for why a bare per-point <c>Math.Clamp</c> is wrong here (it would
    /// distort a line's length/angle, or shear a perspective quad). Returns 0 (no shift) rather than
    /// throwing if no single shared delta could keep every value in range (only reachable when the
    /// shape already spans more than the whole canvas on this axis) -- a total function. Can return a
    /// delta that INVERTS SIGN and/or EXCEEDS <paramref name="offset"/> in magnitude (2nd-round
    /// code-review nit on this doc comment's own prior wording, which read as if the result were
    /// always a mild trim) -- e.g. a shape already past the canvas edge can need to shift the
    /// OPPOSITE direction from the nominal +offset seed to land every value back in range at all.</summary>
    private static double ClampSharedOffsetDelta(double offset, params double[] values)
    {
        var min = double.NegativeInfinity;
        var max = double.PositiveInfinity;
        foreach (var v in values)
        {
            min = Math.Max(min, -v);
            max = Math.Min(max, 1 - v);
        }

        return min <= max ? Math.Clamp(offset, min, max) : 0;
    }

    /// <summary>TX workflow modernization plan, Phase 3b -- Ctrl-drag-to-duplicate, called from
    /// <c>TxImageEditorPaneView.axaml.cs</c>'s <c>OnOverlayElementPointerPressed</c> when Ctrl is
    /// held (NOT Alt -- Alt+drag moves windows on many Linux desktop environments, Ctrl+drag matches
    /// Illustrator/Inkscape/Sketch's own convention instead). Reuses <see cref="InsertClonedSnapshot"/>
    /// wholesale (its harmless +0.02 seed offset is immediately overridden by the drag that follows).
    /// <see cref="InsertClonedSnapshot"/>'s own <see cref="PushUndoSnapshot"/> call resets
    /// <see cref="_pendingCoalesceProperty"/> to null -- re-arming it here to the SAME
    /// <c>"OverlayGeometry"</c> key the immediately-following drag's first move will target means
    /// <see cref="PushUndoSnapshotCoalesced"/> sees it already pending and no-ops, so one Ctrl-drag
    /// gesture produces exactly one undo step (the clone-insert), not two.</summary>
    public ITemplateElementViewModel DuplicateElementForDrag(ITemplateElementViewModel element)
    {
        var clone = InsertClonedSnapshot(element.ToSnapshot());
        _pendingCoalesceProperty = "OverlayGeometry";
        return clone;
    }

    private bool CanCopyOrCutSelectedElement() => SelectedOverlayElement is not null;

    [RelayCommand(CanExecute = nameof(CanCopyOrCutSelectedElement))]
    private void CopySelectedElement()
    {
        if (SelectedOverlayElement is not { } selected)
        {
            return;
        }

        _clipboardSnapshot = selected.ToSnapshot();
        PasteElementCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCopyOrCutSelectedElement))]
    private void CutSelectedElement()
    {
        if (SelectedOverlayElement is not { } selected)
        {
            return;
        }

        _clipboardSnapshot = selected.ToSnapshot();
        RemoveOverlayElement(selected);
        PasteElementCommand.NotifyCanExecuteChanged();
    }

    private bool CanPasteElement() => _clipboardSnapshot is not null;

    /// <summary>Backlog item (auditor usability review, 2026-08-17): "Repeated Paste stacks copies at
    /// the identical offset from the clipboard snapshot (not incrementing per paste), so 3x Ctrl+V
    /// looks like paste only worked once." Root cause: <see cref="_clipboardSnapshot"/> never changed
    /// across repeated pastes, so every paste re-applied <see cref="InsertClonedSnapshot"/>'s own
    /// fixed offset from the SAME original position. Fixed by re-snapshotting the just-pasted element
    /// back into <see cref="_clipboardSnapshot"/> after each paste -- the NEXT paste then offsets from
    /// the PREVIOUS paste, cascading diagonally (the common paste-in-place-then-offset convention),
    /// while a fresh Copy/Cut still resets the clipboard to the live source position exactly as
    /// before (both set <see cref="_clipboardSnapshot"/> directly, not through this method).</summary>
    [RelayCommand(CanExecute = nameof(CanPasteElement))]
    private void PasteElement()
    {
        if (_clipboardSnapshot is { } snapshot)
        {
            var pasted = InsertClonedSnapshot(snapshot);
            _clipboardSnapshot = pasted.ToSnapshot();
        }
    }

    private bool CanCopySelectedElementStyle() => SelectedOverlayElement is OverlayElementViewModel or BoxElementViewModel or LineElementViewModel;

    [RelayCommand(CanExecute = nameof(CanCopySelectedElementStyle))]
    private void CopySelectedElementStyle()
    {
        if (SelectedOverlayElement is not (OverlayElementViewModel or BoxElementViewModel or LineElementViewModel))
        {
            return;
        }

        _styleClipboardSnapshot = SelectedOverlayElement.ToSnapshot();
        PasteSelectedElementStyleCommand.NotifyCanExecuteChanged();
    }

    private bool CanPasteSelectedElementStyle() => _styleClipboardSnapshot switch
    {
        RawTextElementSnapshot => SelectedOverlayElement is OverlayElementViewModel,
        RawBoxElementSnapshot => SelectedOverlayElement is BoxElementViewModel,
        RawLineElementSnapshot => SelectedOverlayElement is LineElementViewModel,
        _ => false,
    };

    /// <summary>One coalesced undo step for the whole style application, same
    /// <see cref="_suspendPreview"/>-wrapped-explicit-<see cref="PushUndoSnapshot"/> pattern
    /// <see cref="AlignSelectedElementToCrop"/> already established -- without it, each property
    /// assignment in the element's ApplyStyleFrom would push its own step via that property's own change hook (where one
    /// exists), turning one "paste style" click into several undo steps.</summary>
    [RelayCommand(CanExecute = nameof(CanPasteSelectedElementStyle))]
    private void PasteSelectedElementStyle()
    {
        PushUndoSnapshot();
        _suspendPreview = true;
        try
        {
            switch (_styleClipboardSnapshot, SelectedOverlayElement)
            {
                case (RawTextElementSnapshot style, OverlayElementViewModel text):
                    text.ApplyStyleFrom(style);
                    break;
                case (RawBoxElementSnapshot style, BoxElementViewModel box):
                    box.ApplyStyleFrom(style);
                    break;
                case (RawLineElementSnapshot style, LineElementViewModel line):
                    line.ApplyStyleFrom(style);
                    break;
            }
        }
        finally
        {
            _suspendPreview = false;
        }

        RecomputePreview();
    }

    [RelayCommand]
    private void RemoveOverlayElement(ITemplateElementViewModel? element)
    {
        // Tier B audit finding: null-checked, but not checked for being a stale reference no longer
        // in OverlayElements (e.g. a queued click racing an Undo, which replaces every element
        // wholesale -- see ApplyState's own doc comment) -- same guard SetAsBackdrop/
        // MoveElementUp/MoveElementDown/BringToFront/SendToBack all already have, checked BEFORE
        // PushUndoSnapshot so a stale click doesn't leave a bogus undo step behind either.
        // Collection.Remove itself already no-ops silently on an absent element, so without this
        // guard the ONLY visible effect of a stale click used to be an extra undo step.
        if (element is null || !OverlayElements.Contains(element))
        {
            return;
        }

        PushUndoSnapshot();
        element.PropertyChanged -= OnOverlayElementPropertyChanged;
        OverlayElements.Remove(element);
        // Widened to a generic IDisposable check (TX editor gap-items plan, item 4b) -- see
        // LoadTemplateIntoLiveEditor's own identical comment.
        if (element is IDisposable disposableElement)
        {
            disposableElement.Dispose();
        }

        // Shrink, not clear: other group members stay selected and no disposed element stays in the set.
        if (SelectedOverlayElements.Contains(element))
        {
            SetSelection(SelectedOverlayElements.Where(e => !ReferenceEquals(e, element)));
        }

        RecomputePreview();
    }

    /// <summary>TX editor gap-items plan, item 2 (group-ops-lite) -- deletes every member of
    /// <see cref="SelectedOverlayElements"/> as ONE undo step, mirroring <see cref="RemoveOverlayElement"/>'s
    /// own stale-reference guard (filtered BEFORE <see cref="PushUndoSnapshot"/>, same "a queued
    /// click racing an Undo" reasoning that method's own doc comment gives).</summary>
    [RelayCommand]
    private void RemoveSelectedElements()
    {
        var toRemove = SelectedOverlayElements.Where(OverlayElements.Contains).ToList();
        if (toRemove.Count == 0)
        {
            return;
        }

        PushUndoSnapshot();
        foreach (var element in toRemove)
        {
            element.PropertyChanged -= OnOverlayElementPropertyChanged;
            OverlayElements.Remove(element);
            if (element is IDisposable disposableElement)
            {
                disposableElement.Dispose();
            }
        }

        SetSelection([]);
        RecomputePreview();
    }

    /// <summary>Layer-order reorder (Phase 1) -- discrete actions (element-list up/down buttons),
    /// pushed as a single non-coalesced undo step each, same shape as Rotate/Add/Remove.
    /// <para>Swaps Z with the adjacent element in actual DRAW order (<c>OrderBy(Z)</c>, the same
    /// stable sort <see cref="BuildTemplateDocument"/>/<c>ApplyTemplate</c> use, which breaks Z ties
    /// by <see cref="OverlayElements"/>'s own list index) -- code-review finding: a bare
    /// <c>element.Z += 1</c> is a no-op whenever the neighbour already holds that Z (the default
    /// state for every freshly-added element, since <see cref="NextZ"/> hands out consecutive
    /// integers with no gaps), because the stable sort's tie-break then still draws them in the
    /// same relative order.</para>
    /// <para>ALSO moves the element within <see cref="OverlayElements"/> itself, not just its Z value
    /// (real-window code-review finding, via Avalonia DevTools): the editor canvas's
    /// <c>ItemsControl</c> binds <c>ZIndex="{Binding Z}"</c> on each DataTemplate root, but Avalonia
    /// wraps each item in its own container and does not forward that attached property to reorder
    /// containers within the panel -- confirmed empirically (the mini preview, driven by the real
    /// <c>ApplyTemplate</c> pipeline, showed the correct new stacking after a swap; the interactive
    /// canvas did not). A plain <c>Canvas</c> draws children in CHILD order with no other z-ordering
    /// signal available, so keeping <see cref="OverlayElements"/> itself always sorted by Z (an
    /// invariant every other mutation site already upholds -- <see cref="NextZ"/> always appends the
    /// new max at the collection's own end) makes the canvas's natural draw order agree with the
    /// pipeline's Z-based order, without depending on any attached-property forwarding.</para></summary>
    [RelayCommand]
    private void MoveElementUp(ITemplateElementViewModel? element)
    {
        if (element is null)
        {
            return;
        }

        var ordered = OverlayElements.OrderBy(e => e.Z).ToList();
        var index = ordered.IndexOf(element);
        if (index < 0 || index == ordered.Count - 1)
        {
            return;
        }

        PushUndoSnapshot();
        var neighbor = ordered[index + 1];
        (element.Z, neighbor.Z) = (neighbor.Z, element.Z);
        OverlayElements.Move(OverlayElements.IndexOf(element), OverlayElements.IndexOf(neighbor));
        RecomputePreview();
    }

    [RelayCommand]
    private void MoveElementDown(ITemplateElementViewModel? element)
    {
        if (element is null)
        {
            return;
        }

        var ordered = OverlayElements.OrderBy(e => e.Z).ToList();
        var index = ordered.IndexOf(element);
        if (index <= 0)
        {
            return;
        }

        PushUndoSnapshot();
        var neighbor = ordered[index - 1];
        (element.Z, neighbor.Z) = (neighbor.Z, element.Z);
        OverlayElements.Move(OverlayElements.IndexOf(element), OverlayElements.IndexOf(neighbor));
        RecomputePreview();
    }

    /// <summary>Z-order jump commands (Addendum, spec/15-template-designer.md, explicit user
    /// request) -- a fast path around <see cref="MoveElementUp"/>/<see cref="MoveElementDown"/>'s own
    /// one-click-per-neighbour stepping for a large stack. <c>BringToFront</c> reuses <see
    /// cref="NextZ"/>'s own max+1 top-insert convention exactly (same mechanism <see
    /// cref="AddOverlayElement"/>/<see cref="AddBoxElement"/>/<see cref="Duplicate"/> already use);
    /// <c>Max(e => e.Z)</c> safely includes <c>element</c> itself here (unlike those call sites, where
    /// the new element isn't in <see cref="OverlayElements"/> yet) since Max+1 is still strictly
    /// greater than every other element's Z regardless of whether it also happens to be the current
    /// max. Early-returns on an already-topmost element (matches <see cref="MoveElementUp"/>'s own
    /// no-op-at-boundary behavior -- code-review finding: the first draft pushed an undo step and
    /// bumped Z even when nothing visibly moved).</summary>
    [RelayCommand]
    private void BringToFront(ITemplateElementViewModel? element)
    {
        var index = element is null ? -1 : OverlayElements.IndexOf(element);
        if (index < 0 || index == OverlayElements.Count - 1)
        {
            return;
        }

        PushUndoSnapshot();
        element!.Z = NextZ();
        OverlayElements.Move(index, OverlayElements.Count - 1);
        RecomputePreview();
    }

    /// <summary>See <see cref="BringToFront"/>'s own doc comment for the general shape.
    /// <c>SendToBack</c> reuses <see cref="SetAsBackdrop"/>'s own bottom-insert convention
    /// (<c>Min(Z) - 1</c> / move to collection index 0) -- EXCEPT it floors above the nearest <see
    /// cref="ImageElementViewModel.IsBackground"/> element BELOW <c>element</c> in the collection, if
    /// any (explicit user constraint: "obviously can't hide behind the actual background picture" --
    /// a background element is full-frame and opaque, so anything placed behind it would simply
    /// become invisible, defeating what "send to back" is supposed to mean here).
    /// <para>[Code-review correction -- a first draft of this method assumed "background is always
    /// the collection-wide strict Z minimum, therefore always at index 0" and used
    /// <c>OverlayElements.OfType&lt;ImageElementViewModel&gt;().FirstOrDefault(e =&gt; e.IsBackground)</c> +
    /// <c>Move(index, IndexOf(background) + 1)</c>. Auditor review found that premise false in three
    /// reachable ways, one a real crash/data-loss bug: (1) <see cref="MoveElementUp"/>/<see
    /// cref="MoveElementDown"/> are not gated on <c>Locked</c>/<see
    /// cref="ImageElementViewModel.IsBackground"/>, so a background element can end up off index 0,
    /// tied in Z with another element, or even ABOVE the element being sent to back -- at which point
    /// <c>IndexOf(background) + 1</c> can equal <c>Count</c>, and <c>ObservableCollection&lt;T&gt;.Move</c>
    /// (Remove-then-Insert) throws AFTER the remove already succeeded, silently dropping the element
    /// from <see cref="OverlayElements"/> with no <c>CollectionChanged</c> notification -- reachable in
    /// two clicks with THIS feature alone (<see cref="BringToFront"/> on the background row, itself
    /// ungated, then <see cref="SendToBack"/> on anything else). (2) The same stale-position case also
    /// INVERTS the floor: a background sitting above the element raises it instead of sending it back.
    /// (3) Nothing clears a previous element's <c>IsBackground</c> flag on a second
    /// <see cref="SetAsBackdrop"/> call, so multiple backgrounds are reachable, and
    /// <c>FirstOrDefault</c> picks the lowest one rather than the one actually adjacent to
    /// <c>element</c>.</para>
    /// <para>Fix: scan BACKWARDS from <c>element</c>'s own position for the nearest background at a
    /// LOWER index (never higher -- an above-element background is the pre-existing
    /// MoveUp/Down-gating gap noted above, out of this addendum's own scope, not chased here). The
    /// target insert index (<c>backgroundIndex + 1</c>) is then always <c>&lt;= index</c>, which is
    /// always a legal <c>Move</c> target regardless of where <c>element</c> currently sits -- this is
    /// what actually closes the crash, not a stronger invariant claim. The new Z is clamped to
    /// <c>Math.Min(background.Z + 1, next.Z)</c> (where <c>next</c> is whatever currently sits
    /// immediately after the background) rather than assigned outright, so it can never exceed the Z
    /// of the element it's about to be inserted before -- keeping <see cref="OverlayElements"/>' own
    /// collection-order-matches-Z invariant intact for this one insertion even when a tie is
    /// unavoidable, without depending on the disproven strict-minimum premise.</para>
    /// <para>Sending the background element itself to back is guarded as an explicit no-op (not left
    /// to fall through to the unconditional branch, which would have quietly decremented its Z by 1
    /// on every click forever -- a real, if cosmetically invisible, drift the first draft's own doc
    /// comment incorrectly claimed couldn't happen).</para></summary>
    [RelayCommand]
    private void SendToBack(ITemplateElementViewModel? element)
    {
        var index = element is null ? -1 : OverlayElements.IndexOf(element);
        if (index < 0 || element is ImageElementViewModel { IsBackground: true })
        {
            return;
        }

        var backgroundIndex = -1;
        for (var i = index - 1; i >= 0; i--)
        {
            if (OverlayElements[i] is ImageElementViewModel { IsBackground: true })
            {
                backgroundIndex = i;
                break;
            }
        }

        // Tier B audit finding: already-at-the-back is an explicit no-op, same as BringToFront's own
        // `index == OverlayElements.Count - 1` guard above -- without it, an element already at
        // collection index 0 (no background present) fell through to the unconditional branch and
        // had its own Z quietly decremented by 1 on every click forever (visually invisible, but a
        // real undo-stack/HasUnsavedEdits pollution -- every click pushed a bogus step). Same for an
        // element already sitting immediately after the background (target == index): Move(index,
        // index) is a no-op, but the undo step and RecomputePreview pass weren't.
        if (backgroundIndex < 0 ? index == 0 : backgroundIndex + 1 == index)
        {
            return;
        }

        PushUndoSnapshot();
        if (backgroundIndex >= 0)
        {
            var target = backgroundIndex + 1;
            var background = OverlayElements[backgroundIndex];
            var next = OverlayElements[target];
            element!.Z = Math.Min(background.Z + 1, next.Z);
            OverlayElements.Move(index, target);
        }
        else
        {
            element!.Z = OverlayElements.Min(e => e.Z) - 1;
            OverlayElements.Move(index, 0);
        }

        RecomputePreview();
    }
}
