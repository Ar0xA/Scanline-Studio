using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Shared shape for every element type the TX image editor's canvas can host (Phase 1,
/// spec/15-template-designer.md) — implemented by <see cref="OverlayElementViewModel"/> (text) and
/// <see cref="BoxElementViewModel"/>. <see cref="X"/>/<see cref="Y"/> stay CENTER-anchored (matches
/// <see cref="OverlayElementViewModel"/>'s pre-Phase-1 convention, kept deliberately rather than
/// switching to a top-left origin — minimizes the blast radius against the existing rotate-transform/
/// undo/canvas-drag machinery, all written in terms of a center point) — <see cref="Width"/>/
/// <see cref="Height"/> are new in Phase 1, same full-working-copy-normalized space as X/Y.
/// <see cref="Z"/> is layer order (ascending, matches
/// <see cref="Abstractions.Imaging.TemplateElement"/>'s own convention at the pipeline layer).
/// <see cref="Locked"/> blocks canvas drag/resize (View-level check) — Phase 1 plan-review finding:
/// does NOT block sidebar text-field edits by itself; each concrete VM/View decides which of its own
/// controls respect it.</summary>
public interface ITemplateElementViewModel : INotifyPropertyChanged
{
    double X { get; set; }

    double Y { get; set; }

    double Width { get; set; }

    double Height { get; set; }

    int Z { get; set; }

    bool Locked { get; set; }

    /// <summary>Backlog item (auditor usability review, 2026-08-17): true for exactly the element
    /// currently equal to <see cref="TxImageEditorPaneViewModel.SelectedOverlayElement"/> -- set by
    /// the parent VM's own <c>OnSelectedOverlayElementChanged</c> (a loop over every live element,
    /// same "parent pushes shared state down" convention as <see cref="RemoveCommand"/>'s own
    /// init-property wiring, just a plain settable property instead of a command since there's no
    /// per-element action here). Lets the ELEMENTS panel's own row highlight the currently-selected
    /// element without a <c>$parent</c>/ancestor-cast binding path (this codebase's own documented
    /// crash class, see <see cref="RemoveCommand"/>'s own doc comment).</summary>
    bool IsSelected { get; set; }

    /// <summary>Pixel-space canvas dimensions, parent-pushed at creation and on every rotate/zoom
    /// change — same pattern as <see cref="OverlayElementViewModel"/>'s pre-Phase-1 fields,
    /// generalized. Since Phase 7's rearchitecture (see <see
    /// cref="TxImageEditorPaneViewModel.ZoomFactor"/>'s own doc comment) the parent pushes
    /// <c>CanvasDisplayWidth</c>/<c>CanvasDisplayHeight</c> here (zoom-premultiplied), not raw
    /// <c>WorkingCopyWidth</c>/<c>WorkingCopyHeight</c> — this is what makes every derived pixel
    /// property below (<see cref="LeftPixels"/>/<see cref="TopPixels"/>/<see cref="CanvasWidthPixels"/>/
    /// <see cref="CanvasHeightPixels"/>) zoom-aware for free, via each element's own existing
    /// <c>OnImageWidthChanged</c>/<c>OnImageHeightChanged</c> notification hooks.</summary>
    double ImageWidth { get; set; }

    double ImageHeight { get; set; }

    /// <summary>Box top-left corner in canvas-display pixels — <c>(X - Width/2) * ImageWidth</c>.</summary>
    double LeftPixels { get; }

    double TopPixels { get; }

    /// <summary>Box size in canvas-display pixels — <c>Width * ImageWidth</c> /
    /// <c>Height * ImageHeight</c>.</summary>
    double CanvasWidthPixels { get; }

    double CanvasHeightPixels { get; }

    /// <summary>Set once at creation by <see cref="TxImageEditorPaneViewModel"/>, same
    /// parent-pushed pattern <see cref="OverlayElementViewModel"/> already used pre-Phase-1 for its
    /// text-only <c>RemoveCommand</c> — generalized so <see cref="BoxElementViewModel"/> gets the
    /// same wiring without a parallel <c>RemoveBoxElementCommand</c> (Phase 1 plan-review finding:
    /// one shared remove path, not two, or a second path risks forgetting the
    /// <c>PropertyChanged -=</c> detach the first one already got right).</summary>
    IRelayCommand? RemoveCommand { get; init; }

    /// <summary>Same parent-pushed pattern and rationale as <see cref="RemoveCommand"/> -- bound
    /// directly in XAML (<c>Command="{Binding MoveUpCommand}" CommandParameter="{Binding}"</c>),
    /// never via a <c>$parent[ItemsControl]</c>/inline-type-cast binding path (that exact pattern is
    /// the one <see cref="RemoveCommand"/>'s own doc comment documents as a real, previously-hit
    /// <c>ArgumentException</c> in this codebase).</summary>
    IRelayCommand? MoveUpCommand { get; init; }

    IRelayCommand? MoveDownCommand { get; init; }

    /// <summary>Z-order jump commands (Addendum, spec/15-template-designer.md) -- same parent-pushed
    /// pattern as <see cref="MoveUpCommand"/>/<see cref="MoveDownCommand"/>, generalized the same way
    /// rather than adding element-type-specific variants. <c>BringToFrontCommand</c> reuses
    /// <c>NextZ()</c>'s own max+1 top-insert convention; <c>SendToBackCommand</c> reuses
    /// <c>SetAsBackdrop</c>'s own bottom-insert convention, with a floor at
    /// <c>backdrop.Z + 1</c> when a locked backdrop image element exists (see
    /// <c>TxImageEditorPaneViewModel.SendToBack</c>'s own doc comment) so an element can never be
    /// sent behind -- and made invisible under -- an opaque full-frame backdrop.</summary>
    IRelayCommand? BringToFrontCommand { get; init; }

    IRelayCommand? SendToBackCommand { get; init; }

    /// <summary>Task #24 (right-click context menu addendum, plan-reviewed) -- parent-pushed the
    /// SAME instance as the toolbar's own parameterless <c>TxImageEditorPaneViewModel.DuplicateCommand</c>
    /// (which reads <see cref="ITemplateElementViewModel"/> via <c>SelectedOverlayElement</c>, not a
    /// parameter). Bound in the context menu with NO <c>CommandParameter</c> -- plan-review finding:
    /// <c>RelayCommand.Execute(object?)</c> ignores its argument regardless, so this is type-correct
    /// without reshaping the command's own signature (rejected alternative: making it
    /// element-parameterized like <see cref="RemoveCommand"/>/<see cref="BringToFrontCommand"/> would
    /// also require touching the EXISTING toolbar binding to keep working, a bigger blast radius for
    /// no real benefit). Correctness depends on right-click already having set
    /// <c>SelectedOverlayElement</c> to THIS element by the time the menu opens -- true today, since
    /// <c>OnOverlayElementPointerPressed</c> sets it on <c>PointerPressed</c> (ungated by mouse
    /// button), which fires before the native <c>ContextMenu</c> opens on <c>PointerReleased</c>.</summary>
    IRelayCommand? DuplicateCommand { get; init; }

    /// <summary>EditWindow redesign Phase 6 (mockups/Editwindow) -- parent-pushed the SAME
    /// parameterless <c>TxImageEditorPaneViewModel.AlignSelectedElementToCropCommand</c> instance the
    /// GEOMETRY tab's own align-to-crop buttons already use (which reads this element via
    /// <c>SelectedOverlayElement</c>, not a bound parameter -- same reasoning as
    /// <see cref="DuplicateCommand"/>'s own doc comment). Bound in the context menu with the
    /// alignment string as <c>CommandParameter</c> (the only real argument; the element itself is
    /// implicit via selection). On the shared interface, not text-only like <c>AddPlateCommand</c>,
    /// because all three element types' context menus offer "Align to Crop."</summary>
    IRelayCommand? AlignSelectedElementToCropCommand { get; init; }

    /// <summary>TX workflow modernization plan, Phase 1 -- element clipboard, parent-pushed the SAME
    /// parameterless <c>TxImageEditorPaneViewModel.CopySelectedElementCommand</c>/
    /// <c>CutSelectedElementCommand</c>/<c>PasteElementCommand</c> instances every element gets,
    /// same "reads via SelectedOverlayElement, no CommandParameter" shape as
    /// <see cref="DuplicateCommand"/>'s own doc comment. On the shared interface since all three
    /// element types' context menus offer Cut/Copy/Paste, same reasoning as
    /// <see cref="AlignSelectedElementToCropCommand"/>.</summary>
    IRelayCommand? CopyCommand { get; init; }

    IRelayCommand? CutCommand { get; init; }

    IRelayCommand? PasteCommand { get; init; }

    /// <summary>TX workflow modernization plan, Phase 7 -- rasterise this element into the photo and
    /// drop it from the document. On the shared interface (unlike, say,
    /// <see cref="OverlayElementViewModel.CopyStyleCommand"/>) because flatten is type-agnostic --
    /// it goes through <c>TxImageEditorPaneViewModel.BuildTemplateElement</c>, which already handles
    /// every element type. Parameterised like <see cref="RemoveCommand"/>, not selection-implicit
    /// like <see cref="DuplicateCommand"/>: this one is async and has a real per-element
    /// <c>CanExecute</c>.</summary>
    IRelayCommand? FlattenCommand { get; init; }

    /// <summary>Pushes ONE coalesced undo/redo step covering X/Y/Width/Height together (Phase 1
    /// plan-review finding: a diagonal drag-resize must collapse to one undo step, same reasoning as
    /// the pre-Phase-1 X/Y-only version covering a diagonal drag). Fires on <c>On*Changing</c> (before
    /// assignment) for all four geometry properties. Null only in tests/design-time contexts that
    /// don't care about undo.</summary>
    Action? PushUndoSnapshotForGeometryChange { get; init; }
}
