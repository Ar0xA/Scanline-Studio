using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.UI.Imaging;

namespace ScanlineStudio.UI.ViewModels;

public enum NudgeDirection
{
    Up,
    Down,
    Left,
    Right,
}

/// <summary>Combined crop/resize/stretch + text overlay editor with a realtime TX-accurate
/// preview — see spec/07-image-pipeline.md's "TX image editor" section (two auditor rounds,
/// verdict "ready to build"). Placed in the RX/History dock region, not a separate window — direct
/// user decision, honoring the "unified working area" principle
/// ([[feedback_ui_effort_allocation]] memory).
///
/// Pure, UI-technology-agnostic API: drag operations take already-normalized (0..1) deltas (the
/// View converts real pointer/canvas pixels before calling in), so this class is fully testable
/// headlessly without simulating real pointer events.</summary>
public sealed partial class TxImageEditorPaneViewModel : ViewModelBase
{
    /// <summary>Raw (photo-anchored, un-macro-resolved) snapshot of one overlay element -- the
    /// counterpart to <see cref="ImageOverlayElement"/>, which <see cref="Overlay"/> exposes
    /// already crop-projected AND with <see cref="OverlayElementViewModel.ResolvedText"/> baked in.
    /// Deliberately a SEPARATE type, not a reuse of <see cref="ImageOverlayElement"/> with different
    /// semantics depending on which property produced it -- <see cref="TxControlsPaneViewModel"/>'s
    /// own EditState needs this exact, unprojected, unresolved form to faithfully re-seed a
    /// re-opened editor (round-1 plan-review finding on spec/18-path-to-1.0.md's re-open/re-edit
    /// sub-piece: restoring from the crop-projected <see cref="Overlay"/> instead would silently
    /// misplace existing overlay text AND permanently bake macro templates like <c>"DE %m"</c> into
    /// their currently-resolved value).</summary>
    public sealed record RawOverlayElementSnapshot(string Text, double X, double Y, double FontSizeRelative, Rgb24 Color);

    /// <summary>Prior edit state to seed a re-opened editor with (spec/18-path-to-1.0.md Medium
    /// item: re-open/re-edit after Apply) -- everything genuinely mode/crop-independent.
    /// Deliberately does NOT include <see cref="LockAspectToMode"/> (affects future drags only, not
    /// worth carrying across a re-open) or <see cref="Rotate"/>'s own orientation (the retained
    /// <c>Original</c> the caller passes to the constructor already reflects every prior rotate --
    /// see <see cref="CurrentSource"/>'s own doc comment).</summary>
    public sealed record EditorInitialState(
        NormalizedRect CropRect, bool PreserveAspect, ImageAdjustments Adjustments,
        IReadOnlyList<RawOverlayElementSnapshot> OverlayElements);

    /// <summary>One undo/redo step -- the editor's FULL editable state, captured wholesale rather
    /// than as a per-operation command/inverse (spec/18-path-to-1.0.md Medium item, undo/redo
    /// sub-piece; round-1 plan-review confirmed snapshot-over-command as the right call: the
    /// mutation surface here is ~10 scalars plus a small element list, cheap to snapshot, and a
    /// command pattern would need 6+ inverse operations while still special-casing Rotate).
    /// <see cref="RotationCount"/> instead of a copy of the rotated image itself -- the ORIGINAL
    /// image at construction time isn't retained (<see cref="RotateImageOnly"/> reassigns
    /// <see cref="_originalSource"/> in place), so reconciliation rotates a DELTA of
    /// <c>(target - current) mod 4</c> steps from whatever the CURRENT orientation is, not from a
    /// fixed baseline -- deterministic and exact regardless, since
    /// <c>TransmitImagePreparer.Rotate</c> is an exact <c>RotateMode.Rotate90</c> pixel permutation
    /// with no resampling.</summary>
    private sealed record EditorSnapshot(
        int RotationCount, NormalizedRect CropRect, bool PreserveAspect, bool LockAspectToMode,
        ImageAdjustments Adjustments, IReadOnlyList<RawOverlayElementSnapshot> OverlayElements);

    private const double MinNormalizedCropSize = 0.02;

    /// <summary>Undo/redo stack depth cap (round-1 plan-review risk: unbounded keyboard-nudge
    /// auto-repeat would otherwise grow <see cref="_undoStack"/> forever). Arbitrary but generous
    /// for an interactive editing session -- not a tuning knob expected to matter in practice.</summary>
    private const int MaxUndoDepth = 50;

    // ~2x the target mode's dimensions (capped at the original's own size) -- a data-structure
    // decision made now, not a tuning knob to retrofit later (spec's own perf section): every
    // interactive drag-frame recompute runs against this small copy, never the original.
    private const int WorkingCopyScaleFactor = 2;

    // Mutable (not readonly) since spec/18-path-to-1.0.md High item 3's Rotate command reassigns
    // both in place -- see RotateCommand's own doc comment for why (and why it's still safe: this
    // VM is UI-thread-only, same as every other mutable field here).
    private IImageSource _originalSource;
    private IImageSource _workingCopy;
    private readonly SstvModeDefinition _targetMode;
    private readonly ITransmitImagePreparer _preparer;
    private readonly IMacroTextResolver _macroTextResolver;
    private readonly OperatorSettings _operatorSettings;
    private readonly ILocalizationService _localization;
    private readonly ILogger<TxImageEditorPaneViewModel> _logger;

    // Suppresses RecomputePreview() while RotateCommand is mid-update (rotated working copy but
    // not-yet-transformed CropRect/overlay positions) -- without this, each overlay element's own
    // ImageWidth/ImageHeight PropertyChanged (now real notifications, see OverlayElementViewModel)
    // would each trigger a full Crop+Resize+ApplyOverlay against transiently inconsistent state.
    private bool _suspendPreview;

    // Also set/read by ApplyState (undo/redo) and RotateImageOnly -- see EditorSnapshot's own doc
    // comment for why this tracks orientation instead of retaining a pristine original image.
    private int _rotationCount;

    private readonly List<EditorSnapshot> _undoStack = [];
    private readonly List<EditorSnapshot> _redoStack = [];

    // Dispatcher-idle coalescing for PushUndoSnapshotCoalesced (round-1 plan-review: sliders/
    // TextBoxes fire many rapid Value/Text changes per user gesture with no cheap drag-start/end
    // event to hook, unlike crop/overlay-element dragging -- see PushUndoSnapshotCoalesced's own
    // doc comment for the full mechanism).
    private string? _pendingCoalesceProperty;

    [ObservableProperty]
    private NormalizedRect _cropRect = new(0, 0, 1, 1);

    [ObservableProperty]
    private bool _preserveAspect = true;

    /// <summary>Constrains the crop-rectangle drag-resize handle (NOT the keyboard Shift+arrow
    /// nudge, which stays free-form -- see <see cref="NudgeCropResize"/>'s own doc comment) to
    /// always maintain <see cref="_targetMode"/>'s own aspect ratio (spec/18-path-to-1.0.md High
    /// item 4). Orthogonal to <see cref="PreserveAspect"/>, which governs the separate Resize step
    /// (letterbox vs. stretch) -- this one constrains crop SHAPE. Default OFF so existing free-form
    /// behavior is unchanged unless a user opts in.</summary>
    [ObservableProperty]
    private bool _lockAspectToMode;

    [ObservableProperty]
    private Bitmap? _workingCopyBitmap;

    [ObservableProperty]
    private Bitmap? _previewImage;

    [ObservableProperty]
    private OverlayElementViewModel? _selectedOverlayElement;

    // Adjustment sliders (spec/18-path-to-1.0.md Medium item) -- 0 is each one's own no-op
    // default (see ImageAdjustments' own doc comment for the exact per-field mapping). Applied
    // between Resize and ApplyOverlay (never touching already-burned-in overlay text pixels) --
    // see ITransmitImagePreparer.ApplyAdjustments' own doc comment for why that pipeline position,
    // not before Crop/Resize.
    [ObservableProperty]
    private double _brightness;

    [ObservableProperty]
    private double _contrast;

    [ObservableProperty]
    private double _saturation;

    [ObservableProperty]
    private double _gamma;

    [ObservableProperty]
    private double _sharpen;

    [ObservableProperty]
    private double _denoise;

    public TxImageEditorPaneViewModel(
        IImageSource originalSource,
        SstvModeDefinition targetMode,
        ITransmitImagePreparer preparer,
        IMacroTextResolver macroTextResolver,
        OperatorSettings operatorSettings,
        ILocalizationService localization,
        ILogger<TxImageEditorPaneViewModel> logger,
        EditorInitialState? initialState = null)
    {
        _originalSource = originalSource;
        _targetMode = targetMode;
        _preparer = preparer;
        _macroTextResolver = macroTextResolver;
        _operatorSettings = operatorSettings;
        _localization = localization;
        _logger = logger;

        _workingCopy = BuildWorkingCopy(originalSource, targetMode, preparer);
        WorkingCopyBitmap = ImageSourceBitmapConverter.ToBitmap(_workingCopy);

        if (initialState is { } initial)
        {
            // Seeded BEFORE the trailing RecomputePreview() below, wrapped in _suspendPreview (same
            // pattern as Rotate()) so setting CropRect/PreserveAspect/6 slider properties/N overlay
            // elements here doesn't each independently trigger their own RecomputePreview() pass --
            // one full pipeline run at the end, not initialState.OverlayElements.Count + 8.
            _suspendPreview = true;
            try
            {
                CropRect = initial.CropRect;
                PreserveAspect = initial.PreserveAspect;
                Brightness = initial.Adjustments.Brightness;
                Contrast = initial.Adjustments.Contrast;
                Saturation = initial.Adjustments.Saturation;
                Gamma = initial.Adjustments.Gamma;
                Sharpen = initial.Adjustments.Sharpen;
                Denoise = initial.Adjustments.Denoise;
                // SelectedOverlayElement deliberately stays null (code-review nit) -- unlike
                // AddOverlayElement, which always selects the ONE element it just created, there is
                // no obviously-correct choice among N restored elements to auto-select.
                foreach (var snapshot in initial.OverlayElements)
                {
                    OverlayElements.Add(CreateOverlayElement(snapshot.Text, snapshot.X, snapshot.Y, snapshot.FontSizeRelative, snapshot.Color));
                }
            }
            finally
            {
                _suspendPreview = false;
            }
        }

        RecomputePreview();
    }

    public ObservableCollection<OverlayElementViewModel> OverlayElements { get; } = [];

    /// <summary>spec/18-path-to-1.0.md Medium item: the card header used to be a static locale
    /// string ("EDITOR — OUTGOING FRAME · 640×496 · PD120") regardless of the actual mode/image
    /// being edited. <see cref="_targetMode"/> is fixed for this editor instance's whole lifetime
    /// (frozen at construction, spec/18-path-to-1.0.md High item 2's own stale-mode-transmit-crash
    /// fix), so this never needs to react to a mode change mid-edit -- a plain computed property,
    /// not an <see cref="ObservableProperty"/>.</summary>
    public string HeaderText => _localization.GetString(
        "Panes.TxImageEditor.CardHeaderFormat", _targetMode.ImageWidth, _targetMode.ImageHeight, _targetMode.DisplayName);

    /// <summary>Same real-dimensions fix as <see cref="HeaderText"/>, for the separate dimensions
    /// chip lower in the tool strip (mock2's own layout keeps both -- the chip is a compact
    /// at-a-glance readout next to the aspect/text/apply controls, not a duplicate of the header).</summary>
    public string DimensionsChipText => _localization.GetString(
        "Panes.TxImageEditor.DimensionsChipFormat", _targetMode.ImageWidth, _targetMode.ImageHeight);

    /// <summary>The live, current-orientation source -- reflects any <see cref="RotateCommand"/>
    /// calls so far. Round-1 plan-review finding on spec/18-path-to-1.0.md High item 3: a host
    /// (<see cref="TxControlsPaneViewModel"/>) that captured the ORIGINAL constructor argument
    /// instead of reading this property would silently revert a rotate the next time it re-derives
    /// from that stale reference (e.g. on a later mode change) -- see
    /// <c>TxControlsPaneViewModel.OpenEditorForSourceAsync</c>'s own use of this property.</summary>
    public IImageSource CurrentSource => _originalSource;

    /// <summary>Pixel-space dimensions of the interactive canvas's background image -- the View
    /// binds crop-handle/overlay-element positions directly to these (rather than a converter doing
    /// normalized-to-pixel math in XAML), keeping the View a plain binding consumer.</summary>
    public double WorkingCopyWidth => _workingCopy.Width;

    public double WorkingCopyHeight => _workingCopy.Height;

    public double CropLeftPixels => CropRect.X * WorkingCopyWidth;

    public double CropTopPixels => CropRect.Y * WorkingCopyHeight;

    public double CropWidthPixels => CropRect.Width * WorkingCopyWidth;

    public double CropHeightPixels => CropRect.Height * WorkingCopyHeight;

    /// <summary>Bottom-right corner in pixel space -- the resize-handle's anchor point.</summary>
    public double CropRightPixels => (CropRect.X + CropRect.Width) * WorkingCopyWidth;

    public double CropBottomPixels => (CropRect.Y + CropRect.Height) * WorkingCopyHeight;

    /// <summary>Snapshot of the current overlay elements as the immutable
    /// <see cref="ImageOverlay"/> the pipeline actually consumes — lets a host (e.g.
    /// <see cref="TxControlsPaneViewModel"/>) capture the edit-state it needs to reflow on a later
    /// mode change, without re-deriving it from <see cref="OverlayElements"/> itself.</summary>
    public ImageOverlay Overlay => BuildOverlay();

    /// <summary>Same reasoning as <see cref="Overlay"/>, for the 6 adjustment sliders — without
    /// this, a host capturing edit-state for mode-change reflow would silently drop
    /// Brightness/Contrast/etc. on the next mode change (a real gap found and fixed in
    /// <see cref="TxControlsPaneViewModel"/>'s own <c>EditState</c>/<c>OnSelectedModeChanged</c>,
    /// spec/18-path-to-1.0.md Medium item).</summary>
    public ImageAdjustments Adjustments => BuildAdjustments();

    /// <summary>Raw (photo-anchored, un-macro-resolved) snapshot of every current overlay element
    /// -- the counterpart to <see cref="Overlay"/> for callers that need to faithfully RE-SEED an
    /// editor later rather than feed the real transmit pipeline. See
    /// <see cref="RawOverlayElementSnapshot"/>'s own doc comment for why this can't just reuse
    /// <see cref="Overlay"/>'s already-projected, already-macro-resolved elements.</summary>
    public IReadOnlyList<RawOverlayElementSnapshot> RawOverlayElements =>
        OverlayElements.Select(e => new RawOverlayElementSnapshot(e.Text, e.X, e.Y, e.FontSizeRelative, e.Color)).ToList();

    public event Action<IImageSource>? Applied;

    public event Action? Cancelled;

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
    /// <see cref="OverlayElementViewModel.PushUndoSnapshotForPositionChange"/> instead, a path that
    /// also covers typed X/Y TextBox edits, which this drag-only method never would.</summary>
    public void PushUndoSnapshotForDragGesture() => PushUndoSnapshot();

    public void DragCropMove(double dxNormalized, double dyNormalized) => ApplyCropMove(dxNormalized, dyNormalized);

    public void DragCropResize(double dxNormalized, double dyNormalized) => ApplyCropResize(dxNormalized, dyNormalized);

    [RelayCommand]
    private void AddOverlayElement()
    {
        // Seeded at the CROP's center (not the raw photo-center 0.5/0.5 that
        // OverlayElementViewModel's own X/Y field defaults would otherwise leave in place) -- a
        // tight, off-center crop would otherwise place brand-new text outside the visible/
        // transmitted frame immediately. See ProjectToCropRelative's own doc comment for why X/Y
        // are stored relative to the full working copy, not the crop, despite this.
        PushUndoSnapshot();
        var element = CreateOverlayElement(
            text: "Text",
            x: CropRect.X + (CropRect.Width / 2),
            y: CropRect.Y + (CropRect.Height / 2),
            fontSizeRelative: 0.1,
            color: new Rgb24(255, 255, 255));
        OverlayElements.Add(element);
        SelectedOverlayElement = element;
        RecomputePreview();
    }

    /// <summary>Shared element-construction wiring for <see cref="AddOverlayElement"/> and
    /// <see cref="EditorInitialState"/>-based restoration (round-1 plan-review finding on
    /// spec/18-path-to-1.0.md's re-open/re-edit sub-piece: keep this in exactly one place, not
    /// duplicated between "new blank element" and "restored element" call sites).</summary>
    private OverlayElementViewModel CreateOverlayElement(string text, double x, double y, double fontSizeRelative, Rgb24 color)
    {
        var element = new OverlayElementViewModel
        {
            Text = text,
            X = x,
            Y = y,
            FontSizeRelative = fontSizeRelative,
            Color = color,
            ImageWidth = WorkingCopyWidth,
            ImageHeight = WorkingCopyHeight,
            RemoveCommand = RemoveOverlayElementCommand,
            ResolveMacros = macroText => _macroTextResolver.Resolve(macroText, _operatorSettings),
            // Set AFTER X/Y above -- an object initializer assigns in listed order, so X/Y's own
            // construction-time assignment fires OnXChanging/OnYChanging while this is still null,
            // avoiding a spurious push from element creation itself (AddOverlayElement already
            // pushes explicitly before calling this; ApplyState's own restore is separately guarded
            // by _suspendPreview inside PushUndoSnapshotCoalesced regardless of ordering here).
            PushUndoSnapshotForPositionChange = () => PushUndoSnapshotCoalesced("OverlayPosition"),
        };
        element.CanvasFontSize = ComputeCanvasFontSize(element.FontSizeRelative);
        element.PropertyChanged += OnOverlayElementPropertyChanged;
        return element;
    }

    /// <summary>Backs the overlay editor's "Insert field" chips -- appends a macro token (e.g.
    /// <c>%m</c>, <c>{grid}</c>) to the currently selected overlay element's raw <see cref="OverlayElementViewModel.Text"/>.
    /// A no-op with nothing selected, matching every other selection-dependent action in this
    /// editor (no error, no auto-select).</summary>
    [RelayCommand]
    private void InsertField(string? token)
    {
        if (string.IsNullOrEmpty(token) || SelectedOverlayElement is not { } element)
        {
            return;
        }

        element.Text += token;
    }

    [RelayCommand]
    private void RemoveOverlayElement(OverlayElementViewModel? element)
    {
        if (element is null)
        {
            return;
        }

        PushUndoSnapshot();
        element.PropertyChanged -= OnOverlayElementPropertyChanged;
        OverlayElements.Remove(element);
        if (ReferenceEquals(SelectedOverlayElement, element))
        {
            SelectedOverlayElement = null;
        }

        RecomputePreview();
    }

    [RelayCommand]
    private void Apply()
    {
        Log.ApplyInvoked(_logger, _targetMode.Id);
        var cropped = _preparer.Crop(_originalSource, CropRect);
        var resized = _preparer.Resize(cropped, _targetMode.ImageWidth, _targetMode.ImageHeight, PreserveAspect);
        var adjusted = _preparer.ApplyAdjustments(resized, BuildAdjustments());
        var final = _preparer.ApplyOverlay(adjusted, BuildOverlay());
        Applied?.Invoke(final);
    }

    [RelayCommand]
    private void Cancel()
    {
        Log.CancelInvoked(_logger);
        Cancelled?.Invoke();
    }

    /// <summary>Rotates the source 90° clockwise (always -- no direction parameter, matching the
    /// View's single Rotate button; 4 clicks returns to the original orientation), transforming
    /// (not resetting) the current crop rect and every overlay element's position along with it --
    /// round-1 plan-review's own explicit design call: resetting would silently destroy deliberate
    /// framing/text work on the common "rotate after already cropping" case, and would break the
    /// 4-clicks-returns-to-start property the single-button UX depends on. <see cref="FontSizeRelative"/>
    /// is deliberately left untouched (see <see cref="OverlayElementViewModel.FontSizeRelative"/>'s
    /// own reasoning) -- <c>TransmitImagePreparer.ApplyOverlay</c> computes rendered size against
    /// the FINAL mode-sized output's height, never the source's own orientation, so rotation has no
    /// effect on what it means; "fixing" it here would be a real regression, not an improvement.</summary>
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
                // Y-grows-downward convention -- TransmitImagePreparer.Crop/ApplyOverlay's pixel
                // math -- not assumed from a generic formula). Deliberately NOT clamped to [0,1]
                // (unlike the crop rect below) -- code-review finding: TxImageEditorPaneView.axaml.cs's
                // own drag handler explicitly allows free overflow past the image bounds ("clipped
                // at render time only", spec/07-image-pipeline.md), so clamping here would silently
                // relocate an element the user deliberately dragged off-canvas and break the
                // 4-clicks-returns-to-start property for it.
                var (x, y) = (element.X, element.Y);
                element.X = 1 - y;
                element.Y = x;
            }

            CropRect = TransformCropRectClockwise(CropRect);

            // Code-review finding on spec/18-path-to-1.0.md High item 4: rotating the crop rect
            // via the 90°-clockwise transform above swaps its width/height along with the working
            // copy's own dimension swap -- for an already-aspect-locked rect, that leaves the
            // PIXEL aspect at the RECIPROCAL of _targetMode's own (unchanged) aspect, while
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

        WorkingCopyBitmap = ImageSourceBitmapConverter.ToBitmap(_workingCopy);
        OnPropertyChanged(nameof(WorkingCopyWidth));
        OnPropertyChanged(nameof(WorkingCopyHeight));

        foreach (var element in OverlayElements)
        {
            element.ImageWidth = WorkingCopyWidth;
            element.ImageHeight = WorkingCopyHeight;
        }
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

    partial void OnCropRectChanged(NormalizedRect value) => NotifyCropRectDerivedPropertiesAndRecomputePreview();

    /// <summary>Split out from <see cref="OnCropRectChanged"/> so <see cref="Rotate"/> can call it
    /// unconditionally -- CommunityToolkit's generated <see cref="CropRect"/> setter skips this
    /// partial hook entirely when the new value structurally equals the old one (record struct
    /// equality), which a rotate performed before any crop edit hits every time (the initial
    /// <c>(0,0,1,1)</c> transforms to itself). Relying on the hook alone would leave the preview and
    /// pixel-derived properties stale after such a rotate.</summary>
    private void NotifyCropRectDerivedPropertiesAndRecomputePreview()
    {
        RecomputePreview();
        OnPropertyChanged(nameof(CropLeftPixels));
        OnPropertyChanged(nameof(CropTopPixels));
        OnPropertyChanged(nameof(CropWidthPixels));
        OnPropertyChanged(nameof(CropHeightPixels));
        OnPropertyChanged(nameof(CropRightPixels));
        OnPropertyChanged(nameof(CropBottomPixels));

        // Pushed AFTER RecomputePreview() above, not before: CanvasFontSize is filtered out of
        // OnOverlayElementPropertyChanged's own RecomputePreview trigger (it's canvas-chrome-only,
        // never feeds the real pipeline), so ordering here doesn't cause a redundant second
        // recompute -- see CanvasFontSize's own doc comment.
        RefreshOverlayElementCanvasFontSizes();
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
        RefreshOverlayElementCanvasFontSizes();
    }

    partial void OnBrightnessChanged(double value) => RecomputePreview();

    partial void OnContrastChanged(double value) => RecomputePreview();

    partial void OnSaturationChanged(double value) => RecomputePreview();

    partial void OnGammaChanged(double value) => RecomputePreview();

    partial void OnSharpenChanged(double value) => RecomputePreview();

    partial void OnDenoiseChanged(double value) => RecomputePreview();

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

    /// <summary>Every element property EXCEPT the five below feeds
    /// <see cref="BuildImageOverlayElement"/>/the real pipeline, so those five (purely canvas-
    /// chrome display state -- see each one's own doc comment) are excluded here to avoid firing a
    /// full Crop-&gt;Resize-&gt;ApplyOverlay recompute for a change that can't affect its output; that
    /// matters concretely during a crop drag, where <see cref="RefreshOverlayElementCanvasFontSizes"/>
    /// pushes a new <see cref="OverlayElementViewModel.CanvasFontSize"/> to every element on every
    /// mouse-move frame (<see cref="NotifyCropRectDerivedPropertiesAndRecomputePreview"/>) -- without
    /// this filter, that would fire N additional redundant recomputes per frame instead of the one
    /// already performed. Code-review finding: <c>LeftPixels</c>/<c>TopPixels</c> must be filtered
    /// too, not just their own drivers (<c>ImageWidth</c>/<c>ImageHeight</c>) -- both
    /// <c>OnImageWidthChanged</c>/<c>OnImageHeightChanged</c> AND <c>OnXChanged</c>/<c>OnYChanged</c>
    /// re-raise them as a cascade (<see cref="OverlayElementViewModel"/>'s own partial hooks), so
    /// filtering only the drivers left a real gap: any future direct <c>ImageWidth</c>/<c>ImageHeight</c>
    /// write outside <see cref="Rotate"/>'s <see cref="_suspendPreview"/> guard would still trigger a
    /// recompute via the unfiltered <c>LeftPixels</c> cascade, silently defeating the filter above it.
    /// Filtering the cascade itself closes that gap without weakening X/Y's own trigger (X/Y are NOT
    /// filtered -- they feed the pipeline directly and still recompute on their own PropertyChanged,
    /// the LeftPixels/TopPixels re-raise that follows is now just a redundant second signal for the
    /// same edit, correctly suppressed). <see cref="OverlayElementViewModel.FontSizeRelative"/> DOES
    /// feed the pipeline, so its change still triggers a full recompute below; it also needs this one
    /// element's own <c>CanvasFontSize</c> refreshed inline (cheap, single-element) since it's the
    /// property that determines the canvas display size to begin with.</summary>
    private void OnOverlayElementPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OverlayElementViewModel.ImageWidth)
            or nameof(OverlayElementViewModel.ImageHeight)
            or nameof(OverlayElementViewModel.CanvasFontSize)
            or nameof(OverlayElementViewModel.LeftPixels)
            or nameof(OverlayElementViewModel.TopPixels))
        {
            return;
        }

        if (e.PropertyName == nameof(OverlayElementViewModel.FontSizeRelative) && sender is OverlayElementViewModel element)
        {
            element.CanvasFontSize = ComputeCanvasFontSize(element.FontSizeRelative);
        }

        RecomputePreview();
    }

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

    /// <summary>Inner-fit to <see cref="_targetMode"/>'s own aspect ratio -- the largest
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
        var targetAspect = (double)_targetMode.ImageWidth / _targetMode.ImageHeight;
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

    /// <summary>Realtime preview: the REAL <see cref="ITransmitImagePreparer"/> pipeline output
    /// against the small working copy (not a separately-drawn approximation) -- see
    /// spec/07-image-pipeline.md's "what 'TX-accurate' actually means this pass" note. Crop -&gt;
    /// Resize -&gt; ApplyOverlay, that exact order (overlay text must be rasterized at the FINAL
    /// mode dimensions, or a non-aspect-preserving stretch would smear already-drawn glyphs).</summary>
    private void RecomputePreview()
    {
        // See _suspendPreview's own doc comment -- RotateCommand sets this while multiple overlay
        // elements' cascading PropertyChanged events (via OnOverlayElementPropertyChanged) and the
        // CropRect reassignment would otherwise each trigger this full pipeline against transiently
        // half-updated (rotated working copy, not-yet-transformed crop/overlay) state.
        if (_suspendPreview)
        {
            return;
        }

        var cropped = _preparer.Crop(_workingCopy, CropRect);
        var resized = _preparer.Resize(cropped, _targetMode.ImageWidth, _targetMode.ImageHeight, PreserveAspect);
        var adjusted = _preparer.ApplyAdjustments(resized, BuildAdjustments());
        var overlaid = _preparer.ApplyOverlay(adjusted, BuildOverlay());
        PreviewImage = ImageSourceBitmapConverter.ToBitmap(overlaid);
    }

    private ImageOverlay BuildOverlay() => new(OverlayElements.Select(BuildImageOverlayElement).ToList());

    private ImageAdjustments BuildAdjustments() => new(Brightness, Contrast, Saturation, Gamma, Sharpen, Denoise);

    private EditorSnapshot CaptureSnapshot() =>
        new(_rotationCount, CropRect, PreserveAspect, LockAspectToMode, BuildAdjustments(), RawOverlayElements);

    private bool CanUndo() => _undoStack.Count > 0;

    private bool CanRedo() => _redoStack.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        var previous = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _redoStack.Add(CaptureSnapshot());
        ApplyState(previous);
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_redoStack.Count == 0)
        {
            return;
        }

        var next = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _undoStack.Add(CaptureSnapshot());
        ApplyState(next);
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Pushes the CURRENT state (before the caller's own change) as one undo step and
    /// clears the redo stack (standard undo/redo semantics -- redo history is only valid until the
    /// next new action). Used directly by naturally-discrete actions (Rotate, Add/RemoveOverlayElement,
    /// the View-level crop/overlay-drag-start hook) -- see <see cref="PushUndoSnapshotCoalesced"/>
    /// for the burst-of-rapid-changes variant (sliders/TextBoxes).</summary>
    private void PushUndoSnapshot()
    {
        // _suspendPreview doubles as "a restore (ApplyState, or the constructor's own
        // EditorInitialState seeding) is in progress" -- without this guard, ApplyState setting
        // PreserveAspect/LockAspectToMode/the 6 sliders during an Undo/Redo would themselves push
        // MORE undo snapshots via the On*Changing hooks below, corrupting the stacks on every
        // single Undo/Redo call. Safe to reuse: both callers of _suspendPreview=true (Rotate,
        // ApplyState) are exactly the cases where pushing would be wrong, and every REAL push site
        // (Rotate itself, Add/RemoveOverlayElement, the View-level drag-start hook) calls this
        // BEFORE entering its own _suspendPreview block, never from inside one.
        if (_suspendPreview)
        {
            return;
        }

        _undoStack.Add(CaptureSnapshot());
        if (_undoStack.Count > MaxUndoDepth)
        {
            _undoStack.RemoveAt(0);
        }

        _redoStack.Clear();
        _pendingCoalesceProperty = null; // a real, non-coalesced push always resets coalescing state
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
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

        _undoStack.Add(CaptureSnapshot());
        if (_undoStack.Count > MaxUndoDepth)
        {
            _undoStack.RemoveAt(0);
        }

        _redoStack.Clear();
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
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
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
        _suspendPreview = true;
        try
        {
            foreach (var element in OverlayElements)
            {
                element.PropertyChanged -= OnOverlayElementPropertyChanged;
            }

            OverlayElements.Clear();
            SelectedOverlayElement = null;

            var delta = ((snapshot.RotationCount - _rotationCount) % 4 + 4) % 4;
            for (var i = 0; i < delta; i++)
            {
                RotateImageOnly();
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
                OverlayElements.Add(CreateOverlayElement(raw.Text, raw.X, raw.Y, raw.FontSizeRelative, raw.Color));
            }
        }
        finally
        {
            _suspendPreview = false;
        }

        NotifyCropRectDerivedPropertiesAndRecomputePreview();
    }

    /// <summary>Builds the real, pipeline-bound overlay element from an on-canvas
    /// <see cref="OverlayElementViewModel"/>. Cannot just forward its raw X/Y as-is -- those are
    /// normalized against the FULL working copy (what the canvas drags against, so text stays
    /// anchored to the same photo content across crop changes -- see
    /// <see cref="ProjectToCropRelative"/>'s own doc comment), but <c>ApplyOverlay</c> interprets its
    /// input X/Y as normalized against the CROPPED+RESIZED image
    /// (<c>TransmitImagePreparer.cs</c>'s own <c>origin = (X * source.Width, Y * source.Height)</c>,
    /// where <c>source</c> is already post-Crop-and-Resize) -- re-project here, at the one call site
    /// both <see cref="RecomputePreview"/> and <see cref="Apply"/> share, so the side preview panel
    /// and the actually-transmitted image always agree (spec/18-path-to-1.0.md Medium item: TX image
    /// editor overlay text WYSIWYG).</summary>
    private ImageOverlayElement BuildImageOverlayElement(OverlayElementViewModel element)
    {
        var (x, y) = ProjectToCropRelative(element.X, element.Y);
        return new ImageOverlayElement(element.ResolvedText, x, y, element.FontSizeRelative, element.Color);
    }

    /// <summary>Re-projects an overlay element's X/Y from full-working-copy-normalized space into
    /// the CROPPED+RESIZED image's own normalized space, matching the Crop-&gt;Resize letterbox/
    /// stretch math <c>TransmitImagePreparer</c> actually applies. Design choice (round-1 auditor
    /// plan-review, tx-editor-overlay-wysiwyg.md): overlay elements stay anchored to source-PHOTO
    /// content, not to the crop frame -- dragging text onto a subject's face keeps it there as the
    /// crop is adjusted later (clipped/hidden if the crop no longer includes that point), matching
    /// ordinary photo-editor behavior and requiring no new UI. The letterbox-pad term below is not
    /// optional: <c>Resize(preserveAspect: true)</c> (the default) uses <c>ResizeMode.Pad</c>
    /// (centered black bars on the constrained axis), so a naive `(X-CropRect.X)/CropRect.Width`
    /// re-projection (correct ONLY for stretch mode or a crop whose aspect exactly matches the
    /// target mode's) silently mis-places text under the default configuration -- round-1's own
    /// highest-severity finding on this fix.
    /// Pixel-space note: <see cref="CropWidthPixels"/>/<see cref="CropHeightPixels"/> are in
    /// WORKING-COPY pixel space (not the original source's), but the result is pixel-space-invariant
    /// -- it depends only on the crop's aspect ratio, which <see cref="BuildWorkingCopy"/> preserves
    /// exactly from the original, so this is safe to use from both <see cref="RecomputePreview"/>
    /// (working copy) and <see cref="Apply"/> (original source) without drift.</summary>
    private (double X, double Y) ProjectToCropRelative(double x, double y)
    {
        if (CropRect.Width <= 0 || CropRect.Height <= 0)
        {
            return (x, y);
        }

        var relX = (x - CropRect.X) / CropRect.Width;
        var relY = (y - CropRect.Y) / CropRect.Height;

        var cropWidthPixels = CropWidthPixels;
        var cropHeightPixels = CropHeightPixels;
        if (cropWidthPixels <= 0 || cropHeightPixels <= 0)
        {
            return (relX, relY);
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

        var contentWidth = cropWidthPixels * scaleX;
        var contentHeight = cropHeightPixels * scaleY;
        var padX = (targetWidth - contentWidth) / 2;
        var padY = (targetHeight - contentHeight) / 2;

        return ((padX + (relX * contentWidth)) / targetWidth, (padY + (relY * contentHeight)) / targetHeight);
    }

    /// <summary>Font size in CANVAS DISPLAY pixels for the given <c>FontSizeRelative</c> -- the
    /// counterpart to <see cref="ProjectToCropRelative"/> for size rather than position. The real
    /// pipeline's <c>fontSize = FontSizeRelative * targetHeight</c> (fixed, regardless of letterbox
    /// vs. stretch -- <c>ApplyOverlay</c> always runs against the already-Resize()'d, exactly-
    /// target-dimensioned image). One final-image pixel of vertical extent corresponds to
    /// <c>1/scaleY</c> working-copy-canvas pixels, so <c>canvasFontSize = FontSizeRelative *
    /// targetHeight / scaleY</c> -- the two <c>targetHeight</c> factors do NOT cancel here (unlike
    /// the stretch-mode special case) because <c>scaleY</c> itself depends on <c>PreserveAspect</c>.</summary>
    private double ComputeCanvasFontSize(double fontSizeRelative)
    {
        var cropWidthPixels = CropWidthPixels;
        var cropHeightPixels = CropHeightPixels;
        if (cropWidthPixels <= 0 || cropHeightPixels <= 0)
        {
            return 0;
        }

        var targetWidth = (double)_targetMode.ImageWidth;
        var targetHeight = (double)_targetMode.ImageHeight;
        var scaleY = PreserveAspect
            ? Math.Min(targetWidth / cropWidthPixels, targetHeight / cropHeightPixels)
            : targetHeight / cropHeightPixels;

        return scaleY <= 0 ? 0 : fontSizeRelative * targetHeight / scaleY;
    }

    private void RefreshOverlayElementCanvasFontSizes()
    {
        foreach (var element in OverlayElements)
        {
            element.CanvasFontSize = ComputeCanvasFontSize(element.FontSizeRelative);
        }
    }

    private static IImageSource BuildWorkingCopy(IImageSource source, SstvModeDefinition mode, ITransmitImagePreparer preparer)
    {
        var targetWidth = Math.Min(source.Width, mode.ImageWidth * WorkingCopyScaleFactor);
        var targetHeight = Math.Min(source.Height, mode.ImageHeight * WorkingCopyScaleFactor);
        if (targetWidth >= source.Width && targetHeight >= source.Height)
        {
            return source;
        }

        // Preserve source aspect while capping to the working-copy budget above, rather than a
        // flat stretch -- this is a display/perf aid, not user-visible cropping/distortion.
        var scale = Math.Min((double)targetWidth / source.Width, (double)targetHeight / source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        return preparer.Resize(source, width, height, preserveAspect: false);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Apply invoked: targetMode={TargetMode}")]
        public static partial void ApplyInvoked(ILogger logger, string targetMode);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Cancel invoked")]
        public static partial void CancelInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Rotate invoked")]
        public static partial void RotateInvoked(ILogger logger);
    }
}
