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
    /// <c>SetAsBackground</c>'s own bottom-insert convention, with a floor at
    /// <c>background.Z + 1</c> when a locked background image element exists (see
    /// <c>TxImageEditorPaneViewModel.SendToBack</c>'s own doc comment) so an element can never be
    /// sent behind -- and made invisible under -- an opaque full-frame background.</summary>
    IRelayCommand? BringToFrontCommand { get; init; }

    IRelayCommand? SendToBackCommand { get; init; }

    /// <summary>Pushes ONE coalesced undo/redo step covering X/Y/Width/Height together (Phase 1
    /// plan-review finding: a diagonal drag-resize must collapse to one undo step, same reasoning as
    /// the pre-Phase-1 X/Y-only version covering a diagonal drag). Fires on <c>On*Changing</c> (before
    /// assignment) for all four geometry properties. Null only in tests/design-time contexts that
    /// don't care about undo.</summary>
    Action? PushUndoSnapshotForGeometryChange { get; init; }
}
