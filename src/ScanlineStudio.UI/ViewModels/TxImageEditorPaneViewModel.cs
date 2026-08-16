using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
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
using ScanlineStudio.UI.Imaging;
using ScanlineStudio.UI.Services;

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
    /// <summary>Raw (photo-anchored, un-macro-resolved) snapshot of one canvas element -- the
    /// counterpart to <see cref="TemplateElement"/>, which <see cref="Document"/> exposes already
    /// crop-projected AND (for text) with <see cref="OverlayElementViewModel.ResolvedText"/> baked
    /// in. Deliberately a SEPARATE hierarchy, not a reuse of <see cref="TemplateElement"/> with
    /// different semantics depending on which property produced it -- <see cref="TxControlsPaneViewModel"/>'s
    /// own EditState needs this exact, unprojected, unresolved form to faithfully re-seed a
    /// re-opened editor (round-1 plan-review finding on spec/18-path-to-1.0.md's re-open/re-edit
    /// sub-piece: restoring from the crop-projected <see cref="Document"/> instead would silently
    /// misplace existing content AND permanently bake macro templates like <c>"DE %m"</c> into
    /// their currently-resolved value). Polymorphic as of Phase 1 (spec/15-template-designer.md) --
    /// one subtype per <see cref="ITemplateElementViewModel"/> concrete type.</summary>
    public abstract record RawElementSnapshot(double X, double Y, double Width, double Height, int Z, bool Locked);

    /// <summary><paramref name="FontFamily"/>/<paramref name="StrokeColor"/>/
    /// <paramref name="StrokeThickness"/> are Phase 4 (spec/15-template-designer.md) additions,
    /// all trailing/optional so pre-Phase-4 call sites/tests keep compiling unchanged.</summary>
    public sealed record RawTextElementSnapshot(
        double X, double Y, double Width, double Height, int Z, bool Locked,
        string Text, double FontSizeRelative, Rgb24 Color,
        string FontFamily = "", Rgb24? StrokeColor = null, double StrokeThickness = 0.02)
        : RawElementSnapshot(X, Y, Width, Height, Z, Locked);

    public sealed record RawBoxElementSnapshot(
        double X, double Y, double Width, double Height, int Z, bool Locked,
        Rgb24 FillColor, Rgb24? BorderColor, double BorderThickness, double Opacity)
        : RawElementSnapshot(X, Y, Width, Height, Z, Locked);

    /// <summary>Which of Phase 2's 3 sources an image element was resolved from, plus enough to
    /// re-resolve it later (spec/15-template-designer.md, plan-review finding) -- a resolved
    /// <see cref="IImageSource"/> alone can't tell Phase 5's persisted-template format whether to
    /// serialize a file reference or embed the pixels, and would foreclose ever re-resolving the
    /// "last RX image" case against a NEW picture on a future render (not built in Phase 2, but the
    /// origin field keeps that door open instead of silently designing it out).
    /// <see cref="Payload"/> is the file path for <see cref="ImageSourceKind.File"/>, the
    /// <see cref="ReceiveHistoryEntry.Id"/> for <see cref="ImageSourceKind.RxHistory"/>, and unused
    /// (null) for <see cref="ImageSourceKind.LastRx"/>.</summary>
    public enum ImageSourceKind { File, RxHistory, LastRx }

    public sealed record ImageSourceOrigin(ImageSourceKind Kind, string? Payload);

    public sealed record RawImageElementSnapshot(
        double X, double Y, double Width, double Height, int Z, bool Locked,
        IImageSource Source, ImageFitMode Fit, ImageSourceOrigin Origin)
        : RawElementSnapshot(X, Y, Width, Height, Z, Locked);

    /// <summary>Prior edit state to seed a re-opened editor with (spec/18-path-to-1.0.md Medium
    /// item: re-open/re-edit after Apply) -- everything genuinely mode/crop-independent.
    /// Deliberately does NOT include <see cref="LockAspectToMode"/> (affects future drags only, not
    /// worth carrying across a re-open) or <see cref="Rotate"/>'s own orientation (the retained
    /// <c>Original</c> the caller passes to the constructor already reflects every prior rotate --
    /// see <see cref="CurrentSource"/>'s own doc comment).</summary>
    /// <summary><paramref name="TemplateVariables"/> is optional (default <see langword="null"/>,
    /// treated as empty) -- Phase 3 (spec/15-template-designer.md) addition, so pre-Phase-3 call
    /// sites/tests that don't care about fill-bar values keep compiling unchanged. Real callers
    /// (<see cref="TxControlsPaneViewModel"/>'s re-open/re-edit path) pass the prior editor's own
    /// <see cref="TemplateVariables"/> snapshot -- without this, re-opening a just-applied image
    /// would restore <c>{his_call}</c> tokens correctly (via <see cref="OverlayElements"/> above)
    /// while silently losing everything the operator already typed into the fill bar.</summary>
    public sealed record EditorInitialState(
        NormalizedRect CropRect, bool PreserveAspect, ImageAdjustments Adjustments,
        IReadOnlyList<RawElementSnapshot> OverlayElements, IReadOnlyDictionary<string, string>? TemplateVariables = null);

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
        ImageAdjustments Adjustments, IReadOnlyList<RawElementSnapshot> OverlayElements,
        IReadOnlyDictionary<string, string> TemplateVariables);

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
    private readonly IRadioSessionService _radioSessionService;
    private readonly ILocalizationService _localization;
    private readonly ILogger<TxImageEditorPaneViewModel> _logger;

    // Phase 2 (spec/15-template-designer.md) -- image element sources. See the plan's own scope cut:
    // file/last-RX/RX-history have real precedent to reuse; clipboard/drag-drop are deferred (no
    // precedent anywhere in this codebase).
    private readonly IFilePickerService _filePickerService;
    private readonly IImageFileLoader _imageFileLoader;
    private readonly IReceivedImageBuffer _receivedImageBuffer;
    private readonly IReceiveHistoryStore _receiveHistoryStore;

    // Phase 5 (spec/15-template-designer.md) -- template persistence. IImageSourceWriter is used
    // directly here (not just by ITemplateStore) since only THIS VM holds the live, resolved
    // IImageSource pixels an image element's asset copy is written from -- ITemplateStore's own
    // SaveAsync only ever writes template.json + thumbnail.png, never a per-element asset (see its
    // own doc comment).
    private readonly ITemplateStore _templateStore;
    private readonly IImageSourceWriter _imageSourceWriter;

    // Phase 3 (spec/15-template-designer.md) -- named template variables. PERSISTENT value map, only
    // ever added to by user input via OnTemplateVariableValueChanged/ClearTemplateVariables -- never
    // pruned by RescanTemplateVariables, which only ever computes which keys currently have a VISIBLE
    // row (see that method's own doc comment for why: editing an existing {his_call} token
    // character-by-character passes through syntactically-valid intermediate tokens on every
    // keystroke, and a naive "remove keys no longer referenced" rescan would silently discard the
    // operator's already-typed value on the very first backspace).
    private readonly Dictionary<string, string> _templateVariables = [];

    // [A-Za-z0-9_]+, case-sensitive, no spaces -- same grammar as MacroTextResolver's own
    // BraceTokenPattern (Phase 3 plan-review's decided token grammar), duplicated here (not shared)
    // since this scan serves a different purpose (discovering which KEYS to show a fill-bar row
    // for, not resolving VALUES) and lives in a different project/layer than the resolver.
    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}")]
    private static partial Regex TemplateVariableTokenPattern();

    // Fixed known-macro token names (Phase 3: name/grid pre-existing, freq/mode new) -- a {word}
    // token matching one of these is a MACRO reference, not a variable, and must never grow a
    // fill-bar row of its own.
    private static readonly HashSet<string> KnownMacroTokenNames = new(StringComparer.Ordinal) { "name", "grid", "freq", "mode" };

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
    private ITemplateElementViewModel? _selectedOverlayElement;

    /// <summary>Phase 5 (spec/15-template-designer.md) -- bound to the Templates panel's name-entry
    /// TextBox, backing <see cref="SaveTemplateAsync"/>'s <see cref="CanSaveTemplate"/> gate.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveTemplateCommand))]
    private string _newTemplateName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveTemplateCommand))]
    private bool _isSavingTemplate;

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
        IRadioSessionService radioSessionService,
        ILocalizationService localization,
        ILogger<TxImageEditorPaneViewModel> logger,
        IFilePickerService filePickerService,
        IImageFileLoader imageFileLoader,
        IReceivedImageBuffer receivedImageBuffer,
        IReceiveHistoryStore receiveHistoryStore,
        ITemplateStore templateStore,
        IImageSourceWriter imageSourceWriter,
        ReadyRackViewModel readyRack,
        EditorInitialState? initialState = null)
    {
        _originalSource = originalSource;
        _targetMode = targetMode;
        _preparer = preparer;
        _macroTextResolver = macroTextResolver;
        _operatorSettings = operatorSettings;
        _radioSessionService = radioSessionService;
        _localization = localization;
        _logger = logger;
        _filePickerService = filePickerService;
        _imageFileLoader = imageFileLoader;
        _receivedImageBuffer = receivedImageBuffer;
        _receiveHistoryStore = receiveHistoryStore;
        _templateStore = templateStore;
        _imageSourceWriter = imageSourceWriter;
        ReadyRack = readyRack;
        ReadyRack.TemplateSelected += OnReadyRackTemplateSelected;

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
                // Seeded BEFORE the overlay elements below -- RescanTemplateVariables (run inside the
                // trailing RecomputePreview()) discovers variable references in the elements just
                // added and seeds each new row from _templateVariables, so the restored VALUES must
                // already be in place first, not filled in afterward.
                if (initial.TemplateVariables is { } variables)
                {
                    foreach (var (key, value) in variables)
                    {
                        _templateVariables[key] = value;
                    }
                }

                // SelectedOverlayElement deliberately stays null (code-review nit) -- unlike
                // AddOverlayElement, which always selects the ONE element it just created, there is
                // no obviously-correct choice among N restored elements to auto-select.
                //
                // Sorted by Z before seeding (round-3 code-review finding): MoveElementUp/Down assume
                // OverlayElements' own collection order always matches Z order (that's what lets a
                // plain Canvas.Move()-based reorder keep the canvas's draw order in sync with Z --
                // see that method's own doc comment). Every other mutation site upholds this
                // invariant already; this is the one entry point (an externally-supplied
                // EditorInitialState, e.g. a future template-load path) that could hand in elements
                // out of Z order and silently desync it -- sorting here closes that gap at the root
                // instead of every reorder call needing to defend against it.
                foreach (var snapshot in initial.OverlayElements.OrderBy(s => s.Z))
                {
                    OverlayElements.Add(CreateElementFromSnapshot(snapshot));
                }
            }
            finally
            {
                _suspendPreview = false;
            }
        }

        RecomputePreview();
    }

    /// <summary>Phase 5 (spec/15-template-designer.md) -- the Templates panel's saved-template list
    /// + 9-slot pinned rack. Constructed fresh per editor instance by the host
    /// (<see cref="TxControlsPaneViewModel"/>), same "no DI singleton, no cross-editor-instance
    /// leak" reasoning as the editor itself -- see <see cref="TemplateSelected"/>'s own subscription
    /// in this constructor for why no unsubscribe/IDisposable is needed either (both die together).</summary>
    public ReadyRackViewModel ReadyRack { get; }

    private async void OnReadyRackTemplateSelected(string templateId)
    {
        try
        {
            await LoadTemplateAsync(templateId);
        }
        catch (Exception ex)
        {
            Log.LoadTemplateFailed(_logger, templateId, ex);
        }
    }

    public ObservableCollection<ITemplateElementViewModel> OverlayElements { get; } = [];

    /// <summary>Phase 3 (spec/15-template-designer.md) fill bar -- one row per template variable
    /// name currently referenced by a live text element, kept in sync by
    /// <see cref="RescanTemplateVariables"/> (called from <see cref="RecomputePreview"/>, so it runs
    /// on every mutation that could add/remove a reference). ROW visibility only -- see
    /// <see cref="_templateVariables"/>'s own doc comment for why the underlying VALUE is tracked
    /// separately and survives a row's temporary disappearance.</summary>
    public ObservableCollection<TemplateVariableRowViewModel> TemplateVariableRows { get; } = [];

    /// <summary>Snapshot of the current template-variable value map, for a host
    /// (<see cref="TxControlsPaneViewModel"/>) to capture alongside <see cref="RawOverlayElements"/>
    /// when building its own re-open <c>EditState</c> -- same "read-only snapshot for callers that
    /// shouldn't hold a live reference into this VM's own mutable state" reasoning as
    /// <see cref="RawOverlayElements"/> itself. A defensive copy (not the live
    /// <see cref="_templateVariables"/> dictionary itself) so a caller that squirrels this away
    /// (e.g. into an undo/redo <c>EditorSnapshot</c>) isn't silently mutated by later edits.</summary>
    public IReadOnlyDictionary<string, string> TemplateVariables => new Dictionary<string, string>(_templateVariables);

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

    /// <summary>Snapshot of the current canvas elements as the immutable <see cref="TemplateDocument"/>
    /// the pipeline actually consumes (Phase 1 -- supersedes the pre-Phase-1 <c>Overlay</c>/
    /// <c>ImageOverlay</c> property, which only ever covered text) — lets a host (e.g.
    /// <see cref="TxControlsPaneViewModel"/>) capture the edit-state it needs to reflow on a later
    /// mode change, without re-deriving it from <see cref="OverlayElements"/> itself.</summary>
    public TemplateDocument Document => BuildTemplateDocument();

    /// <summary>Same reasoning as <see cref="Document"/>, for the 6 adjustment sliders — without
    /// this, a host capturing edit-state for mode-change reflow would silently drop
    /// Brightness/Contrast/etc. on the next mode change (a real gap found and fixed in
    /// <see cref="TxControlsPaneViewModel"/>'s own <c>EditState</c>/<c>OnSelectedModeChanged</c>,
    /// spec/18-path-to-1.0.md Medium item).</summary>
    public ImageAdjustments Adjustments => BuildAdjustments();

    /// <summary>Raw (photo-anchored, un-macro-resolved) snapshot of every current canvas element
    /// -- the counterpart to <see cref="Document"/> for callers that need to faithfully RE-SEED an
    /// editor later rather than feed the real transmit pipeline. See
    /// <see cref="RawElementSnapshot"/>'s own doc comment for why this can't just reuse
    /// <see cref="Document"/>'s already-projected, already-macro-resolved elements.</summary>
    public IReadOnlyList<RawElementSnapshot> RawOverlayElements => OverlayElements.Select(BuildRawSnapshot).ToList();

    private static RawElementSnapshot BuildRawSnapshot(ITemplateElementViewModel element) => element switch
    {
        OverlayElementViewModel text => new RawTextElementSnapshot(
            text.X, text.Y, text.Width, text.Height, text.Z, text.Locked, text.Text, text.FontSizeRelative, text.Color,
            text.FontFamily, text.StrokeColor, text.StrokeThickness),
        BoxElementViewModel box => new RawBoxElementSnapshot(
            box.X, box.Y, box.Width, box.Height, box.Z, box.Locked, box.FillColor, box.BorderColor, box.BorderThickness, box.Opacity),
        ImageElementViewModel image => new RawImageElementSnapshot(
            image.X, image.Y, image.Width, image.Height, image.Z, image.Locked, image.Source, image.Fit, image.Origin),
        _ => throw new NotSupportedException($"Unrecognized {nameof(ITemplateElementViewModel)}: {element.GetType()}."),
    };

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
    /// <see cref="ITemplateElementViewModel.PushUndoSnapshotForGeometryChange"/> instead, a path
    /// that also covers typed X/Y/Width/Height TextBox edits, which this drag-only method never
    /// would.</summary>
    public void PushUndoSnapshotForDragGesture() => PushUndoSnapshot();

    public void DragCropMove(double dxNormalized, double dyNormalized) => ApplyCropMove(dxNormalized, dyNormalized);

    public void DragCropResize(double dxNormalized, double dyNormalized) => ApplyCropResize(dxNormalized, dyNormalized);

    [RelayCommand]
    private void AddOverlayElement()
    {
        // Seeded at the CROP's center (not the raw photo-center 0.5/0.5 that
        // OverlayElementViewModel's own X/Y field defaults would otherwise leave in place) -- a
        // tight, off-center crop would otherwise place brand-new text outside the visible/
        // transmitted frame immediately. See ProjectRectToCropRelative's own doc comment for why
        // X/Y are stored relative to the full working copy, not the crop, despite this.
        PushUndoSnapshot();
        var element = CreateOverlayElement(
            text: "Text",
            x: CropRect.X + (CropRect.Width / 2),
            y: CropRect.Y + (CropRect.Height / 2),
            width: DefaultElementWidth,
            height: DefaultTextElementHeight,
            fontSizeRelative: DefaultFontSizeRelative,
            color: new Rgb24(255, 255, 255),
            z: NextZ(),
            locked: false);
        OverlayElements.Add(element);
        SelectedOverlayElement = element;
        RecomputePreview();
    }

    /// <summary>New in Phase 1 (spec/15-template-designer.md) -- mirrors
    /// <see cref="AddOverlayElement"/> exactly (crop-centered seed, same undo/select/recompute
    /// shape), just for the box element type instead of text.</summary>
    [RelayCommand]
    private void AddBoxElement()
    {
        PushUndoSnapshot();
        var element = CreateBoxElement(
            x: CropRect.X + (CropRect.Width / 2),
            y: CropRect.Y + (CropRect.Height / 2),
            width: DefaultElementWidth,
            height: DefaultBoxElementHeight,
            fillColor: new Rgb24(64, 64, 64),
            borderColor: null,
            borderThickness: 0,
            opacity: 1.0,
            z: NextZ(),
            locked: false);
        OverlayElements.Add(element);
        SelectedOverlayElement = element;
        RecomputePreview();
    }

    // Phase 1 defaults. DefaultTextElementHeight is deliberately well above DefaultFontSizeRelative
    // (~1.8x), not flush to it -- plan-review finding: TextMeasurer's own line height (ascender +
    // descender + gap) exceeds a bare em size, so a box sized tight to the font fraction would
    // shrink-to-fit immediately on creation, making "Add text" a visible regression from day one.
    private const double DefaultElementWidth = 0.3;
    private const double DefaultTextElementHeight = 0.18;
    private const double DefaultBoxElementHeight = 0.2;
    private const double DefaultFontSizeRelative = 0.1;

    /// <summary>New elements default to drawing on top of everything already on the canvas --
    /// existing max Z + 1, or 0 for the first element.</summary>
    private int NextZ() => OverlayElements.Count == 0 ? 0 : OverlayElements.Max(e => e.Z) + 1;

    /// <summary>Phase 2 (spec/15-template-designer.md) source 1/3 -- direct reuse of the same
    /// picker/loader the TX editor's own "Browse..." stock-image flow already uses
    /// (<see cref="TxControlsPaneViewModel.SelectImageAsync"/>), not new I/O. Mirrors that method's
    /// own error-handling shape: log + return on picker/load failure, silent return on cancel (a
    /// null path from the picker is a normal "user hit Cancel", not an error).</summary>
    [RelayCommand]
    private async Task AddImageFromFileAsync()
    {
        string? path;
        try
        {
            path = await _filePickerService.PickImageFileAsync();
        }
        catch (Exception ex)
        {
            Log.AddImageFromFileFailed(_logger, ex);
            return;
        }

        if (path is null)
        {
            return;
        }

        IImageSource source;
        try
        {
            source = await _imageFileLoader.LoadOriginalAsync(path);
        }
        catch (Exception ex)
        {
            Log.AddImageFromFileFailed(_logger, ex);
            return;
        }

        InsertImageElement(source, new ImageSourceOrigin(ImageSourceKind.File, path));
    }

    /// <summary>Phase 2 source 2/3 -- snapshot at insert time, NOT a live binding (plan-review
    /// decided open question: a saved template must not carry a reference to whatever RX buffer
    /// state happens to exist when it's reused later -- <see cref="IReceivedImageBuffer.Current"/>'s
    /// own instance is never mutated in place once read, so this assignment genuinely freezes it,
    /// though that's copy-on-WRITE inside the buffer, not copy-on-read here -- see this method body's
    /// own comment for why that distinction matters).
    /// <para>Mid-decode insert is intentionally ALLOWED (not gated on
    /// <see cref="IReceivedImageBuffer.Progress"/> being null/complete) -- a live CanExecute gate
    /// would need this VM to subscribe to <see cref="IReceivedImageBuffer.Updated"/>, a long-lived DI
    /// singleton event this VM has no <c>IDisposable</c>/lifecycle hook to ever unsubscribe from
    /// (every other event this VM raises, it owns and disposes with itself). A user who inserts a
    /// still-decoding (partially black) frame can simply Undo/Remove it -- an acceptable, low-severity
    /// v1 tradeoff documented here rather than left unspecified.</para></summary>
    [RelayCommand]
    private void AddLastRxImage()
    {
        // Code-review finding: the idle/never-received case is distinct from the mid-decode case
        // this method's own class doc comment already covers -- IReceivedImageBuffer.Current
        // defaults to (and resets to, on decode restart) a 1x1 black placeholder, never null. A
        // click here with nothing ever received would otherwise silently insert that black square
        // with no visible feedback. No live subscription needed to guard this (checked at click
        // time, not reactively) -- same "no lifecycle hook to unsubscribe" reasoning as this
        // method's own class-level doc comment, just applied as a body-level no-op instead of a
        // CanExecute gate that would go stale anyway without a subscription.
        if (_receivedImageBuffer.Current is { Width: <= 1, Height: <= 1 })
        {
            return;
        }

        // _receivedImageBuffer.Current's getter returns the stored reference directly (not a
        // fresh copy per read) -- the copy happens on WRITE, when a new scanline group decodes and
        // a fresh ArrayImageSource is swapped in. Never mutated in place afterward, so capturing the
        // reference here still freezes it for this element -- but a future perf change that mutated
        // the buffer in place instead of reallocating per scanline group would silently break this.
        InsertImageElement(_receivedImageBuffer.Current, new ImageSourceOrigin(ImageSourceKind.LastRx, null));
    }

    private const int RxHistoryPickerMaxEntries = 20;
    private const int RxHistoryPickerThumbnailMaxDimension = 96;

    /// <summary>One row in the Phase 2 "From RX history" picker flyout -- deliberately NOT a reuse of
    /// <c>RxHistoryPaneViewModel</c>'s own row type (that VM has its own, unrelated concerns/
    /// dependencies; the plan's own scope calls for injecting <see cref="IReceiveHistoryStore"/>
    /// directly here instead).</summary>
    /// <summary><see cref="SelectCommand"/> is parent-pushed (same pattern as
    /// <see cref="ITemplateElementViewModel.RemoveCommand"/>/<c>MoveUpCommand</c>) so the AXAML
    /// picker list can bind <c>Command="{Binding SelectCommand}" CommandParameter="{Binding}"</c>
    /// directly on each row -- deliberately NOT a
    /// <c>$parent[ItemsControl].((vm:TxImageEditorPaneViewModel)DataContext).AddImageFromRxHistoryCommand</c>
    /// binding path, which this codebase has already hit as a real
    /// <c>ArgumentException: Unable to resolve type</c> at first DataTemplate realization elsewhere
    /// (see <see cref="ITemplateElementViewModel.RemoveCommand"/>'s own doc comment).</summary>
    public sealed record RxHistoryPickerEntry(string Id, string FilePath, Bitmap? Thumbnail, IRelayCommand<RxHistoryPickerEntry>? SelectCommand);

    [ObservableProperty]
    private ObservableCollection<RxHistoryPickerEntry> _rxHistoryPickerEntries = [];

    /// <summary>Refreshes <see cref="RxHistoryPickerEntries"/> -- called when the "From RX history"
    /// flyout opens (View-level), not kept live/subscribed (same "no lifecycle hook to unsubscribe"
    /// reasoning as <see cref="AddLastRxImage"/>). <see cref="IReceiveHistoryStore.QueryAsync"/>
    /// already returns newest-first (<c>SqliteReceiveHistoryStore</c>'s own <c>ORDER BY ReceivedAt
    /// DESC</c>), capped client-side to <see cref="RxHistoryPickerMaxEntries"/> since the store has
    /// no server-side limit parameter.</summary>
    [RelayCommand]
    private async Task RefreshRxHistoryPickerAsync()
    {
        IReadOnlyList<ReceiveHistoryEntry> entries;
        try
        {
            entries = await _receiveHistoryStore.QueryAsync(new ReceiveHistoryFilter());
        }
        catch (Exception ex)
        {
            Log.RefreshRxHistoryPickerFailed(_logger, ex);
            return;
        }

        var picked = new List<RxHistoryPickerEntry>();
        foreach (var entry in entries.Take(RxHistoryPickerMaxEntries))
        {
            Bitmap? thumbnail = null;
            try
            {
                var thumbnailSource = await _receiveHistoryStore.LoadThumbnailAsync(entry, RxHistoryPickerThumbnailMaxDimension);
                thumbnail = ImageSourceBitmapConverter.ToBitmap(thumbnailSource);
            }
            catch (Exception ex)
            {
                // One bad thumbnail (e.g. a history row whose backing file was deleted out-of-band)
                // shouldn't blank the whole picker list -- degrade to a null-thumbnail row instead.
                Log.RxHistoryThumbnailLoadFailed(_logger, entry.Id, ex);
            }

            picked.Add(new RxHistoryPickerEntry(entry.Id, entry.FilePath, thumbnail, AddImageFromRxHistoryCommand));
        }

        RxHistoryPickerEntries = new ObservableCollection<RxHistoryPickerEntry>(picked);
    }

    /// <summary>Phase 2 source 3/3 -- <see cref="IReceiveHistoryStore"/> has no full-resolution
    /// loader (only <see cref="IReceiveHistoryStore.LoadThumbnailAsync"/>), so the full-res load
    /// reuses the same <see cref="IImageFileLoader.LoadOriginalAsync"/> the file-picker source
    /// already uses, against the entry's own real on-disk <see cref="ReceiveHistoryEntry.FilePath"/>
    /// -- confirmed the only available move, not a gap glossed over.</summary>
    [RelayCommand]
    private async Task AddImageFromRxHistoryAsync(RxHistoryPickerEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        IImageSource source;
        try
        {
            source = await _imageFileLoader.LoadOriginalAsync(entry.FilePath);
        }
        catch (Exception ex)
        {
            Log.AddImageFromRxHistoryFailed(_logger, entry.Id, ex);
            return;
        }

        InsertImageElement(source, new ImageSourceOrigin(ImageSourceKind.RxHistory, entry.Id));
    }

    /// <summary>Shared by all 3 Phase 2 image sources -- same crop-centered seed/undo/select/
    /// recompute shape as <see cref="AddOverlayElement"/>/<see cref="AddBoxElement"/>. Default
    /// Contain fit (not Stretch) -- an inserted photo keeping its own aspect ratio by default is the
    /// less-surprising choice; <see cref="ImageElementViewModel.Fit"/> isn't yet user-editable in
    /// Phase 2 (Phase 4's style panel territory), but the pipeline/VM plumbing already supports it.</summary>
    private void InsertImageElement(IImageSource source, ImageSourceOrigin origin)
    {
        // Downsampled to the SAME working-copy budget as the background image itself (code-review
        // finding -- see DownsampleToBudget's own doc comment) before ANY of it touches the UI
        // thread's WriteableBitmap conversion or the real per-frame pipeline. Resolved once here,
        // at insert time, same as the file/RX-history sources' own LoadOriginalAsync resolution --
        // consistent with RawImageElementSnapshot's own "resolve once, snapshot the resolved value"
        // contract, not a departure from it.
        var downsampled = DownsampleToBudget(
            source, (int)(WorkingCopyWidth * WorkingCopyScaleFactor), (int)(WorkingCopyHeight * WorkingCopyScaleFactor), _preparer);

        PushUndoSnapshot();
        var element = CreateImageElement(
            x: CropRect.X + (CropRect.Width / 2),
            y: CropRect.Y + (CropRect.Height / 2),
            width: DefaultElementWidth,
            height: DefaultElementWidth,
            source: downsampled,
            fit: ImageFitMode.Contain,
            origin: origin,
            z: NextZ(),
            locked: false);
        OverlayElements.Add(element);
        SelectedOverlayElement = element;
        RecomputePreview();
    }

    /// <summary>Phase 2 (spec/15-template-designer.md) "set as background" -- moves an existing
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
    /// it, one click would push 2 undo steps instead of 1.</para></summary>
    [RelayCommand]
    private void SetAsBackground(ITemplateElementViewModel? element)
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
            element!.X = 0.5;
            element.Y = 0.5;
            element.Width = 1;
            element.Height = 1;
            // At least one element (this one) is always in OverlayElements here, so Min(Z) is safe
            // without the empty-collection special case NextZ() needs for its own max+1 case.
            element.Z = OverlayElements.Min(e => e.Z) - 1;
            OverlayElements.Move(index, 0);
        }
        finally
        {
            _suspendPreview = false;
        }

        RecomputePreview();
    }

    /// <summary>Shared element-construction wiring for <see cref="AddOverlayElement"/> and
    /// restoration (<see cref="CreateElementFromSnapshot"/>) -- round-1 plan-review finding on
    /// spec/18-path-to-1.0.md's re-open/re-edit sub-piece, still the right call in Phase 1: keep
    /// this in exactly one place, not duplicated between "new blank element" and "restored element"
    /// call sites.</summary>
    private OverlayElementViewModel CreateOverlayElement(
        string text, double x, double y, double width, double height, double fontSizeRelative, Rgb24 color, int z, bool locked,
        string? fontFamily = null, Rgb24? strokeColor = null, double strokeThickness = 0.02)
    {
        var element = new OverlayElementViewModel
        {
            Text = text,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            FontSizeRelative = fontSizeRelative,
            Color = color,
            // Phase 4: defaults to the preparer's own default family (queried fresh, not a second
            // hardcoded literal) when the caller doesn't specify one -- AddOverlayElement's own
            // brand-new-element path.
            FontFamily = fontFamily ?? _preparer.AvailableFontFamilies.FirstOrDefault(string.Empty),
            StrokeColor = strokeColor,
            StrokeThickness = strokeThickness,
            Z = z,
            Locked = locked,
            ImageWidth = WorkingCopyWidth,
            ImageHeight = WorkingCopyHeight,
            RemoveCommand = RemoveOverlayElementCommand,
            MoveUpCommand = MoveElementUpCommand,
            MoveDownCommand = MoveElementDownCommand,
            // Phase 3: reads _radioSessionService.LastKnownState/_templateVariables FRESH on every
            // ResolvedText access (this delegate re-invokes on every call, not once) -- FREQ/MODE
            // reflect the radio state as of the last element mutation, not a live tick (no
            // subscription is wired here; see MacroTextResolver's own doc comment on why that's an
            // accepted, pre-existing-pattern tradeoff, same as %T's own staleness).
            ResolveMacros = macroText => _macroTextResolver.Resolve(macroText, _operatorSettings, _radioSessionService.LastKnownState, _templateVariables),
            // Set AFTER X/Y/Width/Height above -- an object initializer assigns in listed order, so
            // their own construction-time assignment fires On*Changing while this is still null,
            // avoiding a spurious push from element creation itself (AddOverlayElement already
            // pushes explicitly before calling this; ApplyState's own restore is separately guarded
            // by _suspendPreview inside PushUndoSnapshotCoalesced regardless of ordering here).
            PushUndoSnapshotForGeometryChange = () => PushUndoSnapshotCoalesced("OverlayGeometry"),
        };
        element.CanvasFontSize = ComputeCanvasFontSize(element);
        element.PropertyChanged += OnOverlayElementPropertyChanged;
        return element;
    }

    /// <summary>Box counterpart to <see cref="CreateOverlayElement"/> -- same wiring shape, no
    /// CanvasFontSize (boxes don't shrink-to-fit).</summary>
    private BoxElementViewModel CreateBoxElement(
        double x, double y, double width, double height, Rgb24 fillColor, Rgb24? borderColor, double borderThickness, double opacity, int z, bool locked)
    {
        var element = new BoxElementViewModel
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            FillColor = fillColor,
            BorderColor = borderColor,
            BorderThickness = borderThickness,
            Opacity = opacity,
            Z = z,
            Locked = locked,
            ImageWidth = WorkingCopyWidth,
            ImageHeight = WorkingCopyHeight,
            RemoveCommand = RemoveOverlayElementCommand,
            MoveUpCommand = MoveElementUpCommand,
            MoveDownCommand = MoveElementDownCommand,
            PushUndoSnapshotForGeometryChange = () => PushUndoSnapshotCoalesced("OverlayGeometry"),
        };
        element.PropertyChanged += OnOverlayElementPropertyChanged;
        return element;
    }

    /// <summary>Image counterpart to <see cref="CreateOverlayElement"/>/<see cref="CreateBoxElement"/>
    /// -- same wiring shape, Phase 2 (spec/15-template-designer.md).</summary>
    private ImageElementViewModel CreateImageElement(
        double x, double y, double width, double height, IImageSource source, ImageFitMode fit, ImageSourceOrigin origin, int z, bool locked)
    {
        var element = new ImageElementViewModel(source)
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Fit = fit,
            Origin = origin,
            Z = z,
            Locked = locked,
            ImageWidth = WorkingCopyWidth,
            ImageHeight = WorkingCopyHeight,
            RemoveCommand = RemoveOverlayElementCommand,
            MoveUpCommand = MoveElementUpCommand,
            MoveDownCommand = MoveElementDownCommand,
            SetAsBackgroundCommand = SetAsBackgroundCommand,
            PushUndoSnapshotForGeometryChange = () => PushUndoSnapshotCoalesced("OverlayGeometry"),
        };
        element.PropertyChanged += OnOverlayElementPropertyChanged;
        return element;
    }

    /// <summary>Shared by the constructor's <see cref="EditorInitialState"/>-seeding block and
    /// <see cref="ApplyState"/> -- one place that knows how to turn a <see cref="RawElementSnapshot"/>
    /// back into a live element, rather than duplicating the type switch at both call sites.</summary>
    private ITemplateElementViewModel CreateElementFromSnapshot(RawElementSnapshot snapshot) => snapshot switch
    {
        RawTextElementSnapshot text => CreateOverlayElement(
            text.Text, text.X, text.Y, text.Width, text.Height, text.FontSizeRelative, text.Color, text.Z, text.Locked,
            text.FontFamily, text.StrokeColor, text.StrokeThickness),
        RawBoxElementSnapshot box => CreateBoxElement(
            box.X, box.Y, box.Width, box.Height, box.FillColor, box.BorderColor, box.BorderThickness, box.Opacity, box.Z, box.Locked),
        RawImageElementSnapshot image => CreateImageElement(
            image.X, image.Y, image.Width, image.Height, image.Source, image.Fit, image.Origin, image.Z, image.Locked),
        _ => throw new NotSupportedException($"Unrecognized {nameof(RawElementSnapshot)}: {snapshot.GetType()}."),
    };

    private bool CanSaveTemplate() => !string.IsNullOrWhiteSpace(NewTemplateName) && !IsSavingTemplate;

    /// <summary>Phase 5 (spec/15-template-designer.md) -- builds <see cref="PersistedTemplateElement"/>s
    /// from the CURRENT live <see cref="OverlayElements"/> (via <see cref="RawOverlayElements"/>,
    /// already exists), writing any image element's live <see cref="IImageSource"/> pixels to a real
    /// file under the template's own <c>assets/</c> folder via <see cref="_imageSourceWriter"/>
    /// BEFORE calling <see cref="ITemplateStore.SaveAsync"/> -- that store's own <c>SaveAsync</c>
    /// only ever writes the manifest + thumbnail, never a per-element asset (see its own doc
    /// comment), since only this VM holds the resolved pixels to write.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveTemplate))]
    private async Task SaveTemplateAsync()
    {
        var name = NewTemplateName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        IsSavingTemplate = true;
        var templateId = _templateStore.CreateTemplateId(name);
        try
        {
            var elements = new List<PersistedTemplateElement>(OverlayElements.Count);
            foreach (var raw in RawOverlayElements)
            {
                elements.Add(await BuildPersistedElementAsync(templateId, raw));
            }

            await _templateStore.SaveAsync(templateId, name, new PersistedTemplateDocument(elements));
            NewTemplateName = string.Empty;
            await ReadyRack.RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.SaveTemplateFailed(_logger, name, ex);

            // Code-review finding: a failure partway through the foreach above (e.g. WritePngAsync
            // throws on element 2 of 3) already wrote element 1's asset PNG under this freshly-minted
            // templateId's own folder, with no template.json ever referencing it -- ListAsync skips
            // manifest-less folders, so that asset is permanently invisible garbage, one new orphaned
            // folder per failed save, with no way for the operator to ever reach it via this app's
            // own UI. DeleteAsync is safe to call unconditionally here: it no-ops if nothing was ever
            // written (directory doesn't exist), and removes the whole folder -- assets included --
            // if something partial was. Best-effort: a cleanup failure here must not mask or replace
            // the original save failure already logged above.
            try
            {
                await _templateStore.DeleteAsync(templateId);
            }
            catch (Exception cleanupEx)
            {
                Log.SaveTemplateCleanupFailed(_logger, templateId, cleanupEx);
            }
        }
        finally
        {
            IsSavingTemplate = false;
        }
    }

    private async Task<PersistedTemplateElement> BuildPersistedElementAsync(string templateId, RawElementSnapshot raw)
    {
        switch (raw)
        {
            case RawTextElementSnapshot text:
                return new PersistedTextElement(
                    text.X, text.Y, text.Width, text.Height, text.Z, text.Locked,
                    text.Text, text.FontSizeRelative, text.Color, text.FontFamily, text.StrokeColor, text.StrokeThickness);
            case RawBoxElementSnapshot box:
                return new PersistedBoxElement(
                    box.X, box.Y, box.Width, box.Height, box.Z, box.Locked,
                    box.FillColor, box.BorderColor, box.BorderThickness, box.Opacity);
            case RawImageElementSnapshot image:
                // GUID-based, never index-derived (plan-review finding -- see PersistedImageElement's
                // own doc comment): safe against any reordering/filtering between here and the manifest
                // write, and against a partial/failed save leaving a stale name behind.
                var assetFileName = $"{Guid.NewGuid():N}.png";
                var assetPath = _templateStore.GetAssetPath(templateId, assetFileName);
                // GetAssetPath is documented pure/no-I/O (its own doc comment: "safe to call before
                // the template's directory exists") -- SaveAsync's own Directory.CreateDirectory only
                // creates the template's ROOT folder, and only runs AFTER this write (code-review
                // finding: assets are written before SaveAsync is ever called), so the "assets/"
                // subdirectory itself is never created by anything else in time. Fixed inside the
                // REAL ImageSourceWriter itself (Core.Imaging), not here -- this VM works against the
                // ITemplateStore/IImageSourceWriter ABSTRACTIONS, which make no promise their own
                // paths are real, creatable filesystem locations (FakeTemplateStore's own
                // GetAssetPath deliberately returns a non-real "/fake/templates/..." path for exactly
                // this reason); a blind Directory.CreateDirectory call here against ANY path either
                // implementation hands back doesn't belong at this layer.
                await _imageSourceWriter.WritePngAsync(image.Source, assetPath);
                var (originKind, originPayload) = image.Origin.Kind switch
                {
                    ImageSourceKind.File => (PersistedImageSourceKind.File, image.Origin.Payload),
                    ImageSourceKind.RxHistory => (PersistedImageSourceKind.RxHistory, image.Origin.Payload),
                    ImageSourceKind.LastRx => (PersistedImageSourceKind.LastRx, image.Origin.Payload),
                    _ => throw new NotSupportedException($"Unrecognized {nameof(ImageSourceKind)}: {image.Origin.Kind}."),
                };
                return new PersistedImageElement(
                    image.X, image.Y, image.Width, image.Height, image.Z, image.Locked,
                    assetFileName, image.Fit, originKind, originPayload);
            default:
                throw new NotSupportedException($"Unrecognized {nameof(RawElementSnapshot)}: {raw.GetType()}.");
        }
    }

    /// <summary>Phase 5 -- calls <see cref="ITemplateStore.LoadAsync"/>, reconstructs every image
    /// element's <see cref="IImageSource"/> via the EXISTING <see cref="IImageFileLoader.LoadOriginalAsync"/>
    /// (same loader Phase 2's file source already uses), maps back to <see cref="RawElementSnapshot"/>s,
    /// and feeds them into <see cref="LoadTemplateIntoLiveEditor"/>.</summary>
    private async Task LoadTemplateAsync(string templateId)
    {
        var document = await _templateStore.LoadAsync(templateId);
        var snapshots = new List<RawElementSnapshot>(document.Elements.Count);
        foreach (var element in document.Elements)
        {
            snapshots.Add(await ToRawElementSnapshotAsync(templateId, element));
        }

        LoadTemplateIntoLiveEditor(snapshots);
    }

    private async Task<RawElementSnapshot> ToRawElementSnapshotAsync(string templateId, PersistedTemplateElement element)
    {
        switch (element)
        {
            case PersistedTextElement text:
                return new RawTextElementSnapshot(
                    text.X, text.Y, text.Width, text.Height, text.Z, text.Locked,
                    text.Text, text.FontSizeRelative, text.Color, text.FontFamily, text.StrokeColor, text.StrokeThickness);
            case PersistedBoxElement box:
                return new RawBoxElementSnapshot(
                    box.X, box.Y, box.Width, box.Height, box.Z, box.Locked,
                    box.FillColor, box.BorderColor, box.BorderThickness, box.Opacity);
            case PersistedImageElement image:
                var assetPath = _templateStore.GetAssetPath(templateId, image.AssetFileName);
                var source = await _imageFileLoader.LoadOriginalAsync(assetPath);
                // Plan-review decision: a LOADED image element's Origin always points at its own
                // copied asset file, never whatever Kind/Payload it originally had when first saved
                // (a File/RxHistory/LastRx origin recorded on the persisted DTO is informational only
                // -- see PersistedImageElement's own doc comment for why none of the three are safe
                // to re-resolve from later).
                var origin = new ImageSourceOrigin(ImageSourceKind.File, assetPath);
                return new RawImageElementSnapshot(image.X, image.Y, image.Width, image.Height, image.Z, image.Locked, source, image.Fit, origin);
            default:
                throw new NotSupportedException($"Unrecognized {nameof(PersistedTemplateElement)}: {element.GetType()}.");
        }
    }

    /// <summary>Phase 5 -- loads a template into an ALREADY-LIVE editor. NOT a reuse of the
    /// constructor's <see cref="EditorInitialState"/>-seeding block (which only ever ADDS into an
    /// EMPTY <see cref="OverlayElements"/>) -- mirrors <see cref="ApplyState"/>'s own detach/clear/
    /// re-add discipline instead (plan-review blocker 3): every existing element's
    /// <see cref="OnOverlayElementPropertyChanged"/> handler is detached first, the collection
    /// cleared, <see cref="SelectedOverlayElement"/> nulled, before the new elements are added.
    /// Unlike <see cref="ApplyState"/>, deliberately does NOT touch <see cref="CropRect"/>/
    /// adjustments/rotation/<see cref="_templateVariables"/> -- a template load replaces the
    /// OVERLAY LAYOUT, not the photo being edited or the operator's already-typed fill-bar values
    /// (Phase 5 plan: <c>TemplateVariables</c> are deliberately not persisted, and
    /// <see cref="RescanTemplateVariables"/> -- run inside <see cref="RecomputePreview"/> below --
    /// already re-derives which KEYS are referenced from the newly loaded elements' own text,
    /// preserving whatever VALUES are already in <see cref="_templateVariables"/>). Preceded by a
    /// real <see cref="PushUndoSnapshot"/> -- a template load must be undoable, like every other
    /// structural change in this editor. DECIDED: load REPLACES the current document, never
    /// merges.</summary>
    private void LoadTemplateIntoLiveEditor(IReadOnlyList<RawElementSnapshot> snapshots)
    {
        PushUndoSnapshot();
        _suspendPreview = true;
        try
        {
            foreach (var element in OverlayElements)
            {
                element.PropertyChanged -= OnOverlayElementPropertyChanged;
            }

            OverlayElements.Clear();
            SelectedOverlayElement = null;

            foreach (var snapshot in snapshots.OrderBy(s => s.Z))
            {
                OverlayElements.Add(CreateElementFromSnapshot(snapshot));
            }
        }
        finally
        {
            _suspendPreview = false;
        }

        RecomputePreview();
    }

    private bool CanInsertField() => SelectedOverlayElement is OverlayElementViewModel;

    /// <summary>Backs the overlay editor's "Insert field" chips -- appends a macro token (e.g.
    /// <c>%m</c>, <c>{grid}</c>) to the currently selected TEXT element's raw
    /// <see cref="OverlayElementViewModel.Text"/>. Gated by <see cref="CanInsertField"/> (Phase 1
    /// plan-review finding: with a box element selected, appending to nothing was a silent no-op --
    /// disabling the chips is better UX than a click that visibly does nothing) rather than a no-op
    /// body -- see <see cref="OnSelectedOverlayElementChanged"/> for the CanExecute re-evaluation
    /// hook.</summary>
    [RelayCommand(CanExecute = nameof(CanInsertField))]
    private void InsertField(string? token)
    {
        if (string.IsNullOrEmpty(token) || SelectedOverlayElement is not OverlayElementViewModel element)
        {
            return;
        }

        element.Text += token;
    }

    partial void OnSelectedOverlayElementChanged(ITemplateElementViewModel? value)
    {
        InsertFieldCommand.NotifyCanExecuteChanged();
        AddPlateBehindTextCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedTextElement));
    }

    /// <summary>Phase 4 (spec/15-template-designer.md) -- <see cref="SelectedOverlayElement"/>
    /// narrowed to the TEXT case, or null when a box/image element (or nothing) is selected. Lets
    /// the TEXT STYLE panel's AXAML bind its whole content against this as a nested DataContext
    /// (font family/size/fill/stroke are all TEXT-only concepts) instead of every individual control
    /// needing its own <c>SelectedOverlayElement as OverlayElementViewModel</c> cast -- same
    /// "narrow once, bind against the narrowed type" shape <see cref="CanInsertField"/> already
    /// established for the insert-field chips.</summary>
    public OverlayElementViewModel? SelectedTextElement => SelectedOverlayElement as OverlayElementViewModel;

    /// <summary>Phase 4 -- the TEXT STYLE panel's font-family picker ItemsSource. Forwards
    /// <see cref="ITransmitImagePreparer.AvailableFontFamilies"/> rather than the VM hardcoding its
    /// own copy of what's bundled.</summary>
    public IReadOnlyList<string> AvailableFontFamilies => _preparer.AvailableFontFamilies;

    private bool CanAddPlateBehindText() => SelectedOverlayElement is OverlayElementViewModel;

    /// <summary>Phase 4 "plate" (background box behind text) -- DECIDED as a one-shot BUTTON action,
    /// not a stateful toggle (plan-review: a persistent toggle would need Z-collision handling, a
    /// new persisted "is plated" flag with no home on <see cref="RawTextElementSnapshot"/> today,
    /// LIVE bounds tracking on every text drag/resize, and a real parent/child relationship this
    /// element model is deliberately flat today -- none of that is "pure UI convenience"). Inserts
    /// one ordinary, INDEPENDENT <see cref="BoxElementViewModel"/> sized to the text's CURRENT
    /// bounds plus a small padding margin, directly behind it in draw order -- after insertion the
    /// two are just two separate elements the user can move/resize/remove independently, same as if
    /// they'd used the existing "Box" button and positioned it by hand.
    /// <para>Inserted at the text element's own COLLECTION index (i.e. immediately behind it in
    /// draw order), then EVERY element's <c>Z</c> is renumbered to match the collection's own order
    /// exactly (<c>0, 1, 2, ...</c>) -- avoids any Z-collision with an existing element (a plain
    /// <c>text.Z - 1</c> could collide with whatever's already there, unlike
    /// <see cref="SetAsBackground"/>'s own <c>Min(Z) - 1</c>, which is collision-free BY
    /// CONSTRUCTION only because it always targets the absolute bottom). A full renumber keeps
    /// everyone else's RELATIVE order untouched, is simple, and Undo already restores the pre-
    /// renumber Z values for free via the existing whole-state snapshot mechanism.</para></summary>
    [RelayCommand(CanExecute = nameof(CanAddPlateBehindText))]
    private void AddPlateBehindText()
    {
        if (SelectedOverlayElement is not OverlayElementViewModel text)
        {
            return;
        }

        var index = OverlayElements.IndexOf(text);
        if (index < 0)
        {
            return;
        }

        PushUndoSnapshot();
        const double padding = 0.02;
        var plate = CreateBoxElement(
            x: text.X, y: text.Y,
            width: text.Width + (padding * 2), height: text.Height + (padding * 2),
            fillColor: new Rgb24(0, 0, 0), borderColor: null, borderThickness: 0, opacity: 0.6,
            z: text.Z, locked: false);

        // _suspendPreview-guarded (same pattern as SetAsBackground/Rotate's own multi-element
        // loops) so the up-to-N Z reassignments below don't each independently trigger their own
        // RecomputePreview() pass -- one full pipeline run at the end instead.
        _suspendPreview = true;
        try
        {
            OverlayElements.Insert(index, plate);
            for (var i = 0; i < OverlayElements.Count; i++)
            {
                OverlayElements[i].Z = i;
            }
        }
        finally
        {
            _suspendPreview = false;
        }

        SelectedOverlayElement = plate;
        RecomputePreview();
    }

    /// <summary>Phase 3 (spec/15-template-designer.md) -- discovers which template-variable KEYS are
    /// currently referenced by scanning every live text element's RAW <c>Text</c> (plan-review fix:
    /// NOT <see cref="Document"/>/<c>ResolvedText</c>, which are already-resolved by construction and
    /// would find nothing or only unfilled leftovers). Called from <see cref="RecomputePreview"/>, so
    /// it runs on every mutation that could add/remove a reference (element add/remove/Text edit,
    /// Undo/Redo). Only adjusts <see cref="TemplateVariableRows"/> (row VISIBILITY) -- NEVER writes
    /// into <see cref="_templateVariables"/> itself (real-window finding: an eager empty-string seed
    /// on first discovery made an unfilled variable resolve to "" instead of verbatim, since
    /// `MacroTextResolver`'s own unfilled-resolves-verbatim branch only triggers when the key is
    /// ABSENT from the dictionary -- see this method's own inline comment). An already-typed value
    /// survives a key's temporary de-reference for the same reason it was never written speculatively
    /// in the first place (see <see cref="_templateVariables"/>'s own doc comment for the exact
    /// character-by-character-editing scenario this protects against).</summary>
    private void RescanTemplateVariables()
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in OverlayElements.OfType<OverlayElementViewModel>())
        {
            foreach (Match match in TemplateVariableTokenPattern().Matches(element.Text))
            {
                var token = match.Groups[1].Value;
                if (!KnownMacroTokenNames.Contains(token))
                {
                    referenced.Add(token);
                }
            }
        }

        for (var i = TemplateVariableRows.Count - 1; i >= 0; i--)
        {
            if (!referenced.Contains(TemplateVariableRows[i].Key))
            {
                TemplateVariableRows.RemoveAt(i);
            }
        }

        var existingKeys = new HashSet<string>(TemplateVariableRows.Select(r => r.Key), StringComparer.Ordinal);
        foreach (var key in referenced)
        {
            if (existingKeys.Contains(key))
            {
                continue;
            }

            // Real-window finding (not caught by MacroTextResolverTests' own isolated unit test,
            // which never exercises this seeding path): do NOT write into _templateVariables here.
            // An eager `_templateVariables[key] = string.Empty` on first discovery would make the
            // dictionary key ALWAYS exist the instant a token is typed -- MacroTextResolver's own
            // "unfilled resolves verbatim" branch only triggers when the key is ABSENT, so an eager
            // seed makes that branch practically unreachable (every token resolves to "" the moment
            // it's typed, before the operator ever gets a chance to fill it in). Read-only lookup:
            // display whatever's already there (a value typed before this key's LAST
            // de-reference), or empty for a genuinely untouched key -- the dictionary itself is
            // written to ONLY by an actual edit (OnTemplateVariableValueChanged) or Clear.
            _templateVariables.TryGetValue(key, out var value);
            TemplateVariableRows.Add(new TemplateVariableRowViewModel(key, value ?? string.Empty)
            {
                ValueChangedCallback = OnTemplateVariableValueChanged,
            });
        }
    }

    /// <summary>Fired by a <see cref="TemplateVariableRowViewModel"/>'s own
    /// <see cref="TemplateVariableRowViewModel.ValueChangedCallback"/> whenever the operator types
    /// into a fill-bar row. Plan-review blocker: does NOT rely on
    /// <see cref="OverlayElementViewModel.ResolvedText"/>'s own property-changed raise alone --
    /// <see cref="OnOverlayElementPropertyChanged"/> explicitly filters <c>ResolvedText</c> out of
    /// its own recompute trigger (treated as a derived/computed property, same tier as
    /// <c>CanvasFontSize</c>), and that filtering held safe pre-Phase-3 only because
    /// <c>ResolvedText</c> never changed independently of its owning element's own <c>Text</c> --
    /// this handler is the first place that breaks that coincidence (a fill-bar edit changes what
    /// MANY elements resolve to, without any of their own <c>Text</c> changing), so it explicitly
    /// drives every step <see cref="OnOverlayElementPropertyChanged"/> would otherwise have chained
    /// together: the canvas <c>TextBlock</c> binding (via
    /// <see cref="OverlayElementViewModel.NotifyResolvedTextChanged"/>, scoped to only the elements
    /// that actually reference this key), the font-shrink-to-fit recompute, and the real pipeline
    /// preview -- mirroring how <see cref="Rotate"/>'s own multi-element loop already drives both
    /// explicitly rather than trusting a property-changed cascade to add up to the same effect.</summary>
    private void OnTemplateVariableValueChanged(string key, string value)
    {
        _templateVariables[key] = value;
        // Moved here from RescanTemplateVariables (real-window finding, see that method's own
        // comment) -- this is now the ONLY place _templateVariables gains a new key, so it's the
        // only place that can flip CanClearTemplateVariables from false to true.
        ClearTemplateVariablesCommand.NotifyCanExecuteChanged();
        var token = $"{{{key}}}";
        foreach (var element in OverlayElements.OfType<OverlayElementViewModel>())
        {
            if (element.Text.Contains(token, StringComparison.Ordinal))
            {
                element.NotifyResolvedTextChanged();
            }
        }

        RefreshOverlayElementCanvasFontSizes();
        RecomputePreview();
    }

    private bool CanClearTemplateVariables() => _templateVariables.Count > 0;

    /// <summary>The one real, spec-backed fill-bar action (spec/15-template-designer.md: "one
    /// 'clear fields' action after the QSO") -- removes every persisted variable KEY entirely
    /// (visible row or not; a key that's currently hidden because nothing references it right now
    /// still belongs to "that QSO's data" and must not survive to the next one), not just blank its
    /// value. <para>[Code-review blocker, fixed here] The original draft set each value to
    /// <c>string.Empty</c> instead of removing the key -- <see cref="MacroTextResolver"/>'s own
    /// unfilled-resolves-VERBATIM branch only fires when a key is ABSENT from the dictionary (see
    /// <see cref="_templateVariables"/>'s own doc comment), so a present-but-empty value made every
    /// `{token}` silently resolve to nothing instead of showing the token again -- exactly the
    /// "content vanishes instead of showing an obvious placeholder" failure this phase's own
    /// verbatim-resolution decision exists to prevent (a `DE {his_call}` reading `DE ` with no
    /// callsign, easy to transmit by mistake). A real `.Clear()`, not a per-key blank, also fixes
    /// <see cref="CanClearTemplateVariables"/> being permanently stuck true (it checks
    /// `Count > 0`, which a per-key blank never changes).</para>
    /// <para>Row display values are reset via <see cref="TemplateVariableRowViewModel
    /// .ResetDisplayValueWithoutNotifying"/>, NOT the ordinary <c>Value</c> setter -- that setter
    /// invokes <see cref="TemplateVariableRowViewModel.ValueChangedCallback"/> (wired to
    /// <see cref="OnTemplateVariableValueChanged"/>), which would immediately re-write an empty
    /// string right back into the dictionary this method just cleared, silently undoing the fix
    /// above.</para>
    /// <para>Iterates a defensive <c>.ToList()</c> snapshot of <see cref="TemplateVariableRows"/>
    /// (code-review risk, fixed here) -- <see cref="RecomputePreview"/> below re-enters
    /// <see cref="RescanTemplateVariables"/>, which can Add/Remove on that same collection; today
    /// nothing in this method changes which keys are referenced, so it's not yet reachable, but
    /// iterating the live collection directly is one future behavior change away from
    /// <c>InvalidOperationException</c> (the sibling dictionary-key loop above already defends the
    /// same way).</para> "Fill all" is deliberately NOT implemented (Phase 3 plan-review decision)
    /// -- no real default-value source exists yet (<see cref="OperatorSettings"/> is MY-side only;
    /// spec/15 frames any logbook-based prefill as future/additive, not this phase).</summary>
    [RelayCommand(CanExecute = nameof(CanClearTemplateVariables))]
    private void ClearTemplateVariables()
    {
        if (_templateVariables.Count == 0)
        {
            return;
        }

        PushUndoSnapshot();
        _templateVariables.Clear();
        ClearTemplateVariablesCommand.NotifyCanExecuteChanged();

        foreach (var row in TemplateVariableRows.ToList())
        {
            row.ResetDisplayValueWithoutNotifying(string.Empty);
        }

        foreach (var element in OverlayElements.OfType<OverlayElementViewModel>())
        {
            element.NotifyResolvedTextChanged();
        }

        RefreshOverlayElementCanvasFontSizes();
        RecomputePreview();
    }

    [RelayCommand]
    private void RemoveOverlayElement(ITemplateElementViewModel? element)
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

    [RelayCommand]
    private void Apply()
    {
        Log.ApplyInvoked(_logger, _targetMode.Id);
        var cropped = _preparer.Crop(_originalSource, CropRect);
        var resized = _preparer.Resize(cropped, _targetMode.ImageWidth, _targetMode.ImageHeight, PreserveAspect);
        var adjusted = _preparer.ApplyAdjustments(resized, BuildAdjustments());
        var final = _preparer.ApplyTemplate(adjusted, BuildTemplateDocument());
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
                var (x, y) = (element.X, element.Y);
                element.X = 1 - y;
                element.Y = x;
                (element.Width, element.Height) = (element.Height, element.Width);
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

    /// <summary>Every element property EXCEPT the ones filtered below feeds
    /// <see cref="BuildTemplateElement"/>/the real pipeline (Phase 1 widened this from the
    /// pre-Phase-1 text-only version, see each filtered name's own reasoning), so those are
    /// excluded here to avoid firing a full Crop-&gt;Resize-&gt;ApplyTemplate recompute for a change
    /// that can't affect its output; that matters concretely during a crop drag, where
    /// <see cref="RefreshOverlayElementCanvasFontSizes"/> pushes a new
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
            or nameof(OverlayElementViewModel.CanvasFontSize)
            or nameof(OverlayElementViewModel.ResolvedText)
            or nameof(ITemplateElementViewModel.LeftPixels)
            or nameof(ITemplateElementViewModel.TopPixels)
            or nameof(ITemplateElementViewModel.CanvasWidthPixels)
            or nameof(ITemplateElementViewModel.CanvasHeightPixels)
            or nameof(BoxElementViewModel.CanvasBorderThicknessPixels)
            or nameof(ImageElementViewModel.CanvasBitmap)
            or nameof(ITemplateElementViewModel.Locked)
            // Code-review nit, fixed here: HasStroke is a derived bool of StrokeColor (raised by
            // OnStrokeColorChanged), not new information -- without this, every stroke-color edit
            // fired two full Crop->Resize->ApplyTemplate passes (one from StrokeColor's own
            // notification below, one from this redundant follow-up).
            or nameof(OverlayElementViewModel.HasStroke))
        {
            return;
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
                or nameof(OverlayElementViewModel.StrokeColor))
        {
            textElement.CanvasFontSize = ComputeCanvasFontSize(textElement);
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

        var cropped = _preparer.Crop(_workingCopy, CropRect);
        var resized = _preparer.Resize(cropped, _targetMode.ImageWidth, _targetMode.ImageHeight, PreserveAspect);
        var adjusted = _preparer.ApplyAdjustments(resized, BuildAdjustments());
        var composited = _preparer.ApplyTemplate(adjusted, BuildTemplateDocument());
        PreviewImage = ImageSourceBitmapConverter.ToBitmap(composited);
    }

    private TemplateDocument BuildTemplateDocument() => new(Name: null, OverlayElements.Select(BuildTemplateElement).ToList());

    private ImageAdjustments BuildAdjustments() => new(Brightness, Contrast, Saturation, Gamma, Sharpen, Denoise);

    private EditorSnapshot CaptureSnapshot() =>
        new(_rotationCount, CropRect, PreserveAspect, LockAspectToMode, BuildAdjustments(), RawOverlayElements, TemplateVariables);

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
        // _suspendPreview doubles as "a restore/multi-element-geometry-change is in progress" --
        // without this guard, ApplyState setting PreserveAspect/LockAspectToMode/the 6 sliders
        // during an Undo/Redo would themselves push MORE undo snapshots via the On*Changing hooks
        // below, corrupting the stacks on every single Undo/Redo call. Safe to reuse: every
        // _suspendPreview=true site (Rotate, ApplyState, the constructor's own EditorInitialState
        // seeding, PreserveAspect's own crop-relock block, SetAsBackground, AddPlateBehindText) is
        // exactly a case where pushing would be wrong, and every REAL push site calls this BEFORE
        // entering its own _suspendPreview block, never from inside one.
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
                OverlayElements.Add(CreateElementFromSnapshot(raw));
            }
        }
        finally
        {
            _suspendPreview = false;
        }

        NotifyCropRectDerivedPropertiesAndRecomputePreview();
    }

    /// <summary>Builds the real, pipeline-bound element from an on-canvas
    /// <see cref="ITemplateElementViewModel"/>. Cannot just forward its raw X/Y/Width/Height as-is --
    /// those are normalized against the FULL working copy (what the canvas drags against, so content
    /// stays anchored to the same photo content across crop changes -- see
    /// <see cref="ProjectRectToCropRelative"/>'s own doc comment), but <c>ApplyTemplate</c>
    /// interprets its input <see cref="TemplateElement.Bounds"/> as normalized against the
    /// CROPPED+RESIZED image -- re-project here, at the one call site both
    /// <see cref="RecomputePreview"/> and <see cref="Apply"/> share, so the side preview panel and
    /// the actually-transmitted image always agree (spec/18-path-to-1.0.md Medium item: TX image
    /// editor overlay text WYSIWYG; Phase 1 generalized this from text-only to every element type).</summary>
    private TemplateElement BuildTemplateElement(ITemplateElementViewModel element)
    {
        var bounds = ProjectRectToCropRelative(element.X, element.Y, element.Width, element.Height);
        return element switch
        {
            // FontSpec.Family fixed to text.FontFamily (Phase 4 plan-review blocker) -- this is the
            // REAL pipeline call site (ApplyTemplate consumes this directly), not just the canvas
            // preview one; an empty string here would have meant every text element always rendered
            // in the default font regardless of what the style panel's picker actually selected.
            OverlayElementViewModel text => new TemplateTextElement(
                bounds, text.Z, text.ResolvedText, new FontSpec(text.FontFamily, text.FontSizeRelative), text.Color,
                text.StrokeColor, text.StrokeThickness),
            BoxElementViewModel box => new TemplateBoxElement(
                bounds, box.Z, box.FillColor, box.BorderColor, box.BorderThickness, box.Opacity),
            ImageElementViewModel image => new TemplateImageElement(bounds, image.Z, image.Source, image.Fit),
            _ => throw new NotSupportedException($"Unrecognized {nameof(ITemplateElementViewModel)}: {element.GetType()}."),
        };
    }

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
    /// WORKING-COPY pixel space (not the original source's), but the result is pixel-space-invariant
    /// up to <see cref="BuildWorkingCopy"/>'s own integer rounding (code-review nit: it depends only
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

        var cropWidthPixels = CropWidthPixels;
        var cropHeightPixels = CropHeightPixels;
        if (cropWidthPixels <= 0 || cropHeightPixels <= 0)
        {
            return new NormalizedRect(relX - (relWidth / 2), relY - (relHeight / 2), relWidth, relHeight);
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

        var finalCenterX = (padX + (relX * contentWidth)) / targetWidth;
        var finalCenterY = (padY + (relY * contentHeight)) / targetHeight;
        var finalWidth = relWidth * contentWidth / targetWidth;
        var finalHeight = relHeight * contentHeight / targetHeight;

        return new NormalizedRect(finalCenterX - (finalWidth / 2), finalCenterY - (finalHeight / 2), finalWidth, finalHeight);
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
    private double ComputeCanvasFontSize(OverlayElementViewModel element)
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

        if (scaleY <= 0)
        {
            return 0;
        }

        var bounds = ProjectRectToCropRelative(element.X, element.Y, element.Width, element.Height);
        var boundsWidthPx = Math.Max(1, (int)Math.Round(bounds.Width * targetWidth));
        var boundsHeightPx = Math.Max(1, (int)Math.Round(bounds.Height * targetHeight));
        // FontSpec.Family + the trailing stroke-thickness argument fixed (Phase 4 plan-review
        // blocker) -- an empty family/omitted stroke here would measure against the WRONG font/
        // without the stroke fit-box allowance while the real pipeline (BuildTemplateElement) uses
        // the actually-selected ones, silently desyncing the canvas from the transmitted image.
        var strokeThicknessRelative = element.StrokeColor is { } ? element.StrokeThickness : 0;
        var fittedFinalSizePx = _preparer.MeasureFittedFontSize(
            element.ResolvedText, new FontSpec(element.FontFamily, element.FontSizeRelative), (int)Math.Round(targetHeight),
            boundsWidthPx, boundsHeightPx, strokeThicknessRelative);

        return fittedFinalSizePx / scaleY;
    }

    /// <summary>TEXT elements only -- boxes have no font/shrink-to-fit concept.</summary>
    private void RefreshOverlayElementCanvasFontSizes()
    {
        foreach (var element in OverlayElements)
        {
            if (element is OverlayElementViewModel text)
            {
                text.CanvasFontSize = ComputeCanvasFontSize(text);
            }
        }
    }

    private static IImageSource BuildWorkingCopy(IImageSource source, SstvModeDefinition mode, ITransmitImagePreparer preparer) =>
        DownsampleToBudget(source, mode.ImageWidth * WorkingCopyScaleFactor, mode.ImageHeight * WorkingCopyScaleFactor, preparer);

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

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Apply invoked: targetMode={TargetMode}")]
        public static partial void ApplyInvoked(ILogger logger, string targetMode);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Cancel invoked")]
        public static partial void CancelInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Rotate invoked")]
        public static partial void RotateInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "AddImageFromFile failed")]
        public static partial void AddImageFromFileFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "RefreshRxHistoryPicker failed")]
        public static partial void RefreshRxHistoryPickerFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "RX history thumbnail load failed: entryId={EntryId}")]
        public static partial void RxHistoryThumbnailLoadFailed(ILogger logger, string entryId, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "AddImageFromRxHistory failed: entryId={EntryId}")]
        public static partial void AddImageFromRxHistoryFailed(ILogger logger, string entryId, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SaveTemplate failed: name={Name}")]
        public static partial void SaveTemplateFailed(ILogger logger, string name, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "LoadTemplate failed: templateId={TemplateId}")]
        public static partial void LoadTemplateFailed(ILogger logger, string templateId, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SaveTemplate cleanup of partially-written templateId={TemplateId} failed")]
        public static partial void SaveTemplateCleanupFailed(ILogger logger, string templateId, Exception exception);
    }
}
