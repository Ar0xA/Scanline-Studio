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
    /// <summary>EditWindow redesign, design-fidelity Phase B -- the raw "is there anything at all on
    /// the undo stack" signal (plus <see cref="_carriedOverUnsavedEdits"/> for a mode-switched
    /// editor, see below). True the instant any edit has been pushed, false once undone back to the
    /// editor's opened (or last-Applied) state. Raised at the same 4 call sites <see cref="UndoCommand"/>'s
    /// own <c>NotifyCanExecuteChanged</c> already fires from.
    /// <para>Mode-switch-mid-edit feature: <b>deliberate exception</b> to the "always in lockstep
    /// with <c>UndoCommand.CanExecute</c>" claim this comment used to make. A freshly constructed
    /// editor (<see cref="ReplaceEditorForModeSwitch"/>'s replacement instance) always has an empty
    /// <see cref="_undoStack"/>, so without <see cref="_carriedOverUnsavedEdits"/> this would read
    /// <see langword="false"/> right after a mode switch and <c>CancelAsync</c>'s confirm dialog
    /// would silently skip, discarding a populated canvas with no warning. The flag is seeded from
    /// the OLD editor's own <see cref="HasUnsavedEdits"/> at construction and never pushed as a fake
    /// undo entry, so it can read <see langword="true"/> while <c>UndoCommand.CanExecute</c> reads
    /// <see langword="false"/> (nothing to actually undo) -- do not "fix" that back.</para>
    /// <para>Deliberately NOT what the header chip/"New Template" confirm-gate use anymore (2026-09-19
    /// user request) -- see <see cref="IsDirtySinceLastCheckpoint"/> below for that. This property is
    /// kept, unchanged in definition, because it still answers a genuinely different, still-useful
    /// question ("is the undo stack non-empty at all") that some future feature may want -- it is not
    /// dead code, just no longer wired to those two specific user-facing surfaces.</para></summary>
    public bool HasUnsavedEdits => _undoStack.Count > 0 || _carriedOverUnsavedEdits;

    /// <summary>Templates rack rework -- see <see cref="_stateIdAtLastCheckpoint"/>'s own doc
    /// comment for why this is a SEPARATE signal from <see cref="HasUnsavedEdits"/>, not a reuse.
    /// <para>User-requested (2026-09-19): "when you save a template into the template library
    /// 'unsaved edits' should be removed and only added again if actual edits are made. Same for when
    /// it's loaded." Previously used ONLY by the Templates rack (the discard-confirm gate in
    /// <see cref="OnReadyRackTemplateSelected"/> and the "Loaded • edited" badge via
    /// <see cref="ReadyRackViewModel.SetCanvasDirty"/>) while Cancel/Revert/the "UNSAVED EDITS" chip
    /// kept using <see cref="HasUnsavedEdits"/> -- promoted to <see langword="public"/> and now backs
    /// ALL THREE surfaces uniformly, by direct user request superseding that original design choice:
    /// the chip and the confirm-gate exist to answer the exact same question the Ready Rack badge
    /// already did ("would leaving/discarding now lose something real"), and a checkpoint-relative
    /// answer is the more useful one for all three, not just the one the badge happened to get first.
    /// ORs in <see cref="_carriedOverDirtySinceCheckpoint"/> (a separate, clearable field from
    /// <see cref="_carriedOverUnsavedEdits"/> -- see its own doc comment, yoniq-auditor Blocker 1) for
    /// the same mode-switch-preservation reason <see cref="HasUnsavedEdits"/> itself does -- a
    /// mode-switched editor has no checkpoint baseline of its own yet, so the state-id comparison
    /// alone can't see work carried over from before the switch. Compares <see cref="_stateId"/>,
    /// not <see cref="_undoStack"/>`.Count` (yoniq-auditor Blocker 2) -- see <see cref="_stateId"/>'s
    /// own doc comment for why the raw stack count is unsafe to compare once <see cref="MaxUndoDepth"/>
    /// pins it.</para></summary>
    public bool IsDirtySinceLastCheckpoint => _stateId != _stateIdAtLastCheckpoint || _carriedOverDirtySinceCheckpoint;

    private EditorSnapshot CaptureSnapshot() =>
        new(_rotationCount, CropRect, PreserveAspect, LockAspectToMode, BuildAdjustments(), RawOverlayElements, TemplateVariables,
            _sourceBaseline, _sourceBaselineRotation, _backgroundIdentity);

    private bool CanUndo() => _undoStack.Count > 0;

    private bool CanRedo() => _redoStack.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        var (previous, previousStateId) = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _redoStack.Add((CaptureSnapshot(), _stateId));
        _stateId = previousStateId;
        ApplyState(previous);
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        RedoAllCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnsavedEdits));
        OnPropertyChanged(nameof(IsDirtySinceLastCheckpoint));
        ReadyRack.SetCanvasDirty(IsDirtySinceLastCheckpoint);
    }

    /// <summary>EditWindow redesign, design-fidelity Phase B -- the context bar's REVERT action.
    /// Pops the undo stack down to empty via the existing <see cref="Undo"/> path (one call per
    /// step, each already-correct: detach/reapply/redo-stack-push), rather than a separate
    /// "snapshot the state at editor-open" mechanism -- reuses machinery this editor already has.
    /// Two bounded, INTENTIONAL consequences of that reuse, not bugs: (1) <see cref="MaxUndoDepth"/>
    /// (50) means a session with more than 50 pushed edits reverts to the OLDEST RETAINED snapshot,
    /// not the true pre-open state -- the same bound every other Undo call already has. (2) each
    /// <see cref="Undo"/> call pushes its own discarded state onto <see cref="_redoStack"/>, so the
    /// entire reverted history stays Redo-able afterward -- deliberately not cleared, since Revert is
    /// just "Undo, repeatedly," not a distinct semantic that should behave differently.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Revert()
    {
        while (CanUndo())
        {
            Undo();
        }
    }

    /// <summary>The context bar's REDO ALL action: the inverse of <see cref="Revert"/>. Calls
    /// <see cref="Redo"/> until the redo stack is empty, so every step is applied through the same
    /// already-correct path.</summary>
    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void RedoAll()
    {
        while (CanRedo())
        {
            Redo();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_redoStack.Count == 0)
        {
            return;
        }

        var (next, nextStateId) = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _undoStack.Add((CaptureSnapshot(), _stateId));
        _stateId = nextStateId;
        ApplyState(next);
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        RedoAllCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnsavedEdits));
        OnPropertyChanged(nameof(IsDirtySinceLastCheckpoint));
        ReadyRack.SetCanvasDirty(IsDirtySinceLastCheckpoint);
    }

    /// <summary>Pushes the CURRENT state (before the caller's own change) as one undo step and
    /// clears the redo stack (standard undo/redo semantics -- redo history is only valid until the
    /// next new action). Used directly by naturally-discrete actions (Rotate, Add/RemoveOverlayElement,
    /// the View-level crop/overlay-drag-start hook) -- see <see cref="PushUndoSnapshotCoalesced"/>
    /// for the burst-of-rapid-changes variant (sliders/TextBoxes).</summary>
    private void PushUndoSnapshot()
    {
        // _suspendPreview doubles as "a restore/multi-element-geometry-change is in progress" --
        // without this guard, ApplyState setting PreserveAspect/LockAspectToMode/the 6 sliders
        // during an Undo/Redo would themselves push MORE undo snapshots via the On*Changing hooks
        // below, corrupting the stacks on every single Undo/Redo call. Safe to reuse: every
        // _suspendPreview=true site (Rotate, ApplyState, the constructor's own EditorInitialState
        // seeding, PreserveAspect's own crop-relock block, SetAsBackdrop, AddPlateBehindText) is
        // exactly a case where pushing would be wrong, and every REAL push site calls this BEFORE
        // entering its own _suspendPreview block, never from inside one.
        if (_suspendPreview)
        {
            return;
        }

        _undoStack.Add((CaptureSnapshot(), _stateId));
        _stateId = ++_nextStateId;
        if (_undoStack.Count > MaxUndoDepth)
        {
            _undoStack.RemoveAt(0);
        }

        _redoStack.Clear();
        _pendingCoalesceProperty = null; // a real, non-coalesced push always resets coalescing state
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        RedoAllCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnsavedEdits));
        OnPropertyChanged(nameof(IsDirtySinceLastCheckpoint));
        ReadyRack.SetCanvasDirty(IsDirtySinceLastCheckpoint);
    }

    /// <summary>Marks <paramref name="propertyName"/> as mid-burst and posts the Background-priority clear
    /// that ends the burst once the UI thread goes idle. Every arming site must use this: a marker armed
    /// without its clear stays set, and every later change to that property is silently not undoable.</summary>
    private void ArmCoalescing(string propertyName)
    {
        _pendingCoalesceProperty = propertyName;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_pendingCoalesceProperty == propertyName)
                {
                    _pendingCoalesceProperty = null;
                }
            },
            DispatcherPriority.Background);
    }

    /// <summary>Same contract as <see cref="PushUndoSnapshot"/>, but coalesces a rapid BURST of
    /// changes to the SAME property into one undo step -- round-1 plan-review finding: sliders
    /// fire <c>Value</c> changes continuously while dragging (many times per second), and TextBoxes
    /// fire <c>Text</c> changes per keystroke; pushing on every one of those would flood the undo
    /// stack and make one Undo click barely move anything. Unlike the crop/overlay-drag case (which
    /// has a real View-level drag-start/end signal to hook), neither has a cheap one here, and this
    /// codebase has no existing testable-clock abstraction to build a wall-clock debounce on
    /// (checked -- none exists; adding one purely for this would be new complexity beyond what's
    /// needed). Instead: the FIRST change for a given <paramref name="propertyName"/> pushes
    /// normally and records it as "pending"; a Background-priority <see cref="Dispatcher"/>
    /// continuation (matching this codebase's own established <c>Dispatcher.UIThread.Post</c>
    /// pattern, e.g. <c>TxControlsPaneViewModel</c>'s several call sites) clears that "pending"
    /// marker once the UI thread actually goes idle; further changes to the SAME property BEFORE
    /// that continuation runs are skipped (still mid-burst). A change to a DIFFERENT property
    /// always pushes fresh, even mid-burst for the first one.</summary>
    private void PushUndoSnapshotCoalesced(string propertyName)
    {
        // Same _suspendPreview-in-progress guard as PushUndoSnapshot's own -- see its doc comment.
        if (_suspendPreview)
        {
            return;
        }

        if (_pendingCoalesceProperty == propertyName)
        {
            return;
        }

        _undoStack.Add((CaptureSnapshot(), _stateId));
        _stateId = ++_nextStateId;
        if (_undoStack.Count > MaxUndoDepth)
        {
            _undoStack.RemoveAt(0);
        }

        _redoStack.Clear();
        ArmCoalescing(propertyName);
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        RedoAllCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnsavedEdits));
        OnPropertyChanged(nameof(IsDirtySinceLastCheckpoint));
        ReadyRack.SetCanvasDirty(IsDirtySinceLastCheckpoint);
    }

    /// <summary>Restores the editor to a previously-captured <see cref="EditorSnapshot"/> -- shared
    /// by <see cref="Undo"/> and <see cref="Redo"/>. NOT a straight reuse of the constructor's own
    /// <see cref="EditorInitialState"/>-seeding block (round-1 plan-review blocker B2): that block
    /// only ever ADDS into an empty <see cref="OverlayElements"/>, but a restore must first detach
    /// <see cref="OnOverlayElementPropertyChanged"/> from every CURRENTLY-live element (mirroring
    /// <see cref="RemoveOverlayElement"/>'s own detach) and clear the collection, or orphaned
    /// handlers leak and keep firing <see cref="RecomputePreview"/> forever; <see cref="SelectedOverlayElement"/>
    /// must also be nulled (it would otherwise dangle at a removed instance). Detach/clear runs
    /// BEFORE the rotation-reconciliation loop below (code-review finding on an earlier draft that
    /// had it after: <see cref="RotateImageOnly"/> updates every then-live element's own
    /// <c>ImageWidth</c>/<c>ImageHeight</c>, which is wasted work when those elements are about to
    /// be discarded and replaced wholesale anyway). Orientation is reconciled via
    /// <see cref="RotateImageOnly"/> called DELTA times (never the <see cref="Rotate"/> COMMAND --
    /// that command's own nested <c>_suspendPreview</c> block would clear THIS method's outer
    /// suspension partway through, round-1 blocker B3, and would apply a coordinate transform on
    /// top of coordinates this snapshot already stores pre-transformed for the target orientation).
    /// <see cref="LockAspectToMode"/> is assigned BEFORE <see cref="CropRect"/> (code-review
    /// correction of an earlier draft's reversed order and its own backwards rationale) -- a
    /// false-to-true transition fires <c>OnLockAspectToModeChanged</c> -&gt;
    /// <c>ApplyCropResizeAspectLocked(0, 0)</c>, which mutates <see cref="CropRect"/>; assigning
    /// <see cref="LockAspectToMode"/> FIRST means that side effect (a no-op re-fit in practice,
    /// since a snapshot captured while locked already has an aspect-correct rect) gets
    /// unconditionally overwritten by the real restored value on the very next line, rather than
    /// relying on the re-fit itself happening to be a no-op to avoid corrupting the restored
    /// rect.</summary>
    private void ApplyState(EditorSnapshot snapshot)
    {
        // Tier B audit finding: Undo/Redo (both funnel through here) never reset this, unlike
        // PushUndoSnapshot/PushUndoSnapshotCoalesced, which both do -- an Undo/Redo IS a real state
        // change, same as any other edit, so it must reset any in-progress property-coalescing
        // window. Without this: Undo landing inside an open coalescing window (e.g. Undo right after
        // a slider drag, before that drag's own coalesce window would naturally close) left
        // _pendingCoalesceProperty pointing at the now-reverted property, so the VERY NEXT edit to
        // that same property silently coalesced into the Undo's own restored snapshot instead of
        // pushing a fresh step -- the edit became invisibly non-undoable, with HasUnsavedEdits
        // reading false while the document was actually dirty.
        _pendingCoalesceProperty = null;

        _suspendPreview = true;
        try
        {
            foreach (var element in OverlayElements)
            {
                element.PropertyChanged -= OnOverlayElementPropertyChanged;
                // T0-11 (production_audit.md): see LoadTemplateIntoLiveEditor's own identical
                // comment -- every discarded ImageElementViewModel owns a WriteableBitmap nothing
                // else disposes. Widened to a generic IDisposable check (TX editor gap-items plan,
                // item 4b) -- OverlayElementViewModel owns the same kind when a text picture fill is set.
                if (element is IDisposable disposableElement)
                {
                    disposableElement.Dispose();
                }
            }

            OverlayElements.Clear();
            SelectedOverlayElement = null;

            // Phase 3: restored BEFORE the element-recreation loop below, same ordering reasoning as
            // the constructor's own EditorInitialState seeding -- the trailing RecomputePreview()'s
            // RescanTemplateVariables() rebuilds TemplateVariableRows from whatever's in
            // _templateVariables at that point, so the restored VALUES must already be in place.
            // TemplateVariableRows itself is cleared here too (not just left stale) since it holds
            // references to rows built against the elements just cleared above.
            _templateVariables.Clear();
            foreach (var (key, value) in snapshot.TemplateVariables)
            {
                _templateVariables[key] = value;
            }

            // Code-review nit, fixed here: Undo/Redo swaps _templateVariables wholesale without
            // going through ClearTemplateVariables/OnTemplateVariableValueChanged, so
            // ClearTemplateVariablesCommand's own CanExecute needs an explicit refresh here too --
            // otherwise a Redo that restores a non-empty dictionary after an Undo emptied it (or the
            // fixed Clear command itself) would leave the button's enabled state one step stale.
            ClearTemplateVariablesCommand.NotifyCanExecuteChanged();

            TemplateVariableRows.Clear();

            _backgroundIdentity = snapshot.BackgroundIdentity;

            // TX workflow modernization plan, Phase 7: the delta-rotation reconciliation below is
            // only valid while the CURRENT source and the snapshot's source come from the same
            // baseline -- i.e. while the only thing that changed between them is orientation. A
            // flatten replaces the source wholesale, and no number of 90-degree rotations gets from
            // a flattened image back to an unflattened one. Reference-compare the BASELINE (not
            // _originalSource, which a rotate legitimately reassigns): distinct flattens always mint
            // distinct instances, so identity is a sound generation test here.
            if (!ReferenceEquals(snapshot.SourceBaseline, _sourceBaseline))
            {
                _sourceBaseline = snapshot.SourceBaseline;
                _sourceBaselineRotation = snapshot.SourceBaselineRotation;
                ReplaceSourceAndWorkingCopy(snapshot.SourceBaseline);
                // The baseline is stored at ITS OWN orientation, which is not necessarily the
                // snapshot's -- rotate forward from the baseline's rotation to the snapshot's,
                // reusing the identical RotateImageOnly path (never the Rotate COMMAND, for the
                // reason this method's own doc comment already gives).
                _rotationCount = snapshot.SourceBaselineRotation;
                var baselineDelta = ((snapshot.RotationCount - snapshot.SourceBaselineRotation) % 4 + 4) % 4;
                for (var i = 0; i < baselineDelta; i++)
                {
                    RotateImageOnly();
                }
            }
            else
            {
                var delta = ((snapshot.RotationCount - _rotationCount) % 4 + 4) % 4;
                for (var i = 0; i < delta; i++)
                {
                    RotateImageOnly();
                }
            }

            LockAspectToMode = snapshot.LockAspectToMode;
            CropRect = snapshot.CropRect;
            PreserveAspect = snapshot.PreserveAspect;
            Brightness = snapshot.Adjustments.Brightness;
            Contrast = snapshot.Adjustments.Contrast;
            Saturation = snapshot.Adjustments.Saturation;
            Gamma = snapshot.Adjustments.Gamma;
            Sharpen = snapshot.Adjustments.Sharpen;
            Denoise = snapshot.Adjustments.Denoise;
            foreach (var raw in snapshot.OverlayElements)
            {
                OverlayElements.Add(CreateElementFromSnapshot(raw));
            }
        }
        finally
        {
            _suspendPreview = false;
        }

        NotifyCropRectDerivedPropertiesAndRecomputePreview();
    }
}
