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
public sealed partial class TxImageEditorPaneViewModel : ViewModelBase, IDisposable
{

    private const double MinNormalizedCropSize = 0.02;

    /// <summary>Phase 7 zoom bounds -- arbitrary but generous (0.1x lets a very large working copy
    /// still shrink to fit a small pane; 4x is well past the point of any real editing value at this
    /// canvas's typical size). Not exposed as a user setting, per the plan's own explicit scope cut.
    /// <c>public</c> (task #23, zoom slider addendum plan-review finding) so the AXAML slider's own
    /// <c>Minimum</c>/<c>Maximum</c> can bind via <c>x:Static</c> to these same two constants instead
    /// of a second, driftable pair of hardcoded XAML literals -- unlike the adjustment sliders (whose
    /// bounds exist nowhere else), these are ALSO enforced in <see cref="ZoomBy"/>'s own clamp, and
    /// <see cref="ZoomFactor"/>'s property setter itself has no clamp, so a drifted slider Maximum
    /// would write an out-of-range value straight through with nothing else to catch it.</summary>
    public const double MinZoomFactor = 0.1;

    public const double MaxZoomFactor = 4.0;

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
    private readonly TargetGeometry _target;
    /// <summary>See <see cref="HasUnsavedEdits"/>'s own doc comment.</summary>
    private readonly bool _carriedOverUnsavedEdits;
    /// <summary>yoniq-auditor finding (2026-09-19, Blocker 1): a SEPARATE, mutable field from
    /// <see cref="_carriedOverUnsavedEdits"/> -- seeded from the same constructor parameter, but
    /// deliberately NOT the same field, because <see cref="_carriedOverUnsavedEdits"/> is
    /// <see langword="readonly"/> and backs <see cref="HasUnsavedEdits"/>, which has its own 3
    /// legitimate production consumers that must keep reading "this editor started from a mode switch
    /// with real prior work" for its whole lifetime. <see cref="IsDirtySinceLastCheckpoint"/> needs the
    /// opposite: a mode-switched editor's carried-over dirtiness must be clearable the moment a save or
    /// load establishes a fresh checkpoint, or the chip/discard-confirm would read permanently dirty
    /// for that editor's entire remaining lifetime, never mind what the operator does afterward.
    /// Cleared at both checkpoint sites (<see cref="LoadTemplateIntoLiveEditor"/>'s own
    /// load-success tail, <see cref="SaveTemplateAsync"/>'s save-success branch).</summary>
    private bool _carriedOverDirtySinceCheckpoint;
    private readonly ITransmitImagePreparer _preparer;
    private readonly IMacroTextResolver _macroTextResolver;
    /// <summary>Reports the PARENT TxControlsPaneViewModel's own live !IsTransmitting &amp;&amp;
    /// !IsRunningLoopbackSelfTest state -- this editor has no session/transmit state of its own
    /// (see <see cref="ApplyAndTransmitCommand"/>'s own doc comment for why a delegate, not a
    /// duplicated flag). The parent calls <see cref="NotifyTransmitAvailabilityChanged"/> at every
    /// one of its own toggle points, mirroring how it already re-notifies TransmitCommand/
    /// StopTransmitCommand/RunLoopbackSelfTestCommand at those same sites. Defaults to "always
    /// allowed" (see the constructor's own optional-parameter comment) for the many test call
    /// sites that construct this class directly and don't exercise Apply &amp; Transmit.</summary>
    private readonly Func<bool> _canTransmitNow;
    /// <summary>Header transmit-progress bar (2026-09-19 user request: "TX should be more
    /// obvious") -- same "no duplicated flag, parent pushes on every toggle" pattern as
    /// <see cref="_canTransmitNow"/> above, extended to progress/percent/remaining-time display
    /// rather than just the boolean CanExecute gate. One snapshot delegate, not three separate
    /// ones, since all three values change together at the same points on the parent.</summary>
    private readonly Func<(bool IsTransmitting, double? Progress, string ProgressText)> _transmitStatusNow;
    /// <summary>Macros help plan (2026-09-01), item B -- opens the same Macros reference window
    /// `Tools ▸ Macros` does, reached via <see cref="OpenMacrosReferenceCommand"/>. Set only from
    /// TxControlsPaneViewModel's real construction call sites; test call sites leave this null, which
    /// <see cref="OpenMacrosReference"/> silently no-ops on -- same "unwired = harmless no-op"
    /// convention as this codebase's other cross-VM request delegates.</summary>
    private readonly Action? _macrosReferenceRequested;
    /// <summary>Was invoked FRESH at each Ready Rack direct-fire (unlike the constructor's own
    /// one-shot <c>currentContactVariables</c> snapshot) to re-seed <c>his_call</c>/<c>his_grid</c>
    /// from whichever station is CURRENTLY being worked. That consumer (the Ready Rack
    /// "pileup"/direct-fire feature) was removed 2026-09-19 by direct user request ("we do not queue
    /// pictures"), leaving this field and its public <see cref="CurrentContactProvider"/> exposure
    /// with no remaining reader -- left in place as accepted-vestigial (same call as
    /// <c>LoadTemplateAsync</c>'s own now-unread <c>bool</c> return from the same removal) rather than
    /// widening this change into the constructor-signature edit removing it fully would require.</summary>
    private readonly Func<IReadOnlyDictionary<string, string>?>? _currentContactProvider;
    // User-reported (2026-09-20): was `readonly`, assigned once at construction -- an
    // already-open editor kept resolving %m/{name}/{grid}/{dist}/{bearing} against whatever
    // OperatorSettings snapshot was current when it opened, so a Callsign/Grid change made via
    // Options while this editor stayed open never showed up. Mutated only by
    // RefreshOperatorSettings, called from MainWindow.axaml.cs's OptionsWindowViewModel.OperatorSettingsSaved
    // subscription for whichever editor is ActiveEditor.
    private OperatorSettings _operatorSettings;
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
    // directly by this VM's persistence mapper (not just by ITemplateStore) since only THIS VM holds
    // the live, resolved IImageSource pixels an image element's asset copy is written from --
    // ITemplateStore's own SaveAsync only ever writes template.json + thumbnail.png, never a
    // per-element asset (see its own doc comment).
    private readonly ITemplateStore _templateStore;
    private readonly TemplateElementPersistenceMapper _persistenceMapper;

    // Phase 3 (spec/15-template-designer.md) -- named template variables. PERSISTENT value map, only
    // ever added to by user input via OnTemplateVariableValueChanged/ClearTemplateVariables -- never
    // pruned by RescanTemplateVariables, which only ever computes which keys currently have a VISIBLE
    // row (see that method's own doc comment for why: editing an existing {his_call} token
    // character-by-character passes through syntactically-valid intermediate tokens on every
    // keystroke, and a naive "remove keys no longer referenced" rescan would silently discard the
    // operator's already-typed value on the very first backspace).
    private readonly Dictionary<string, string> _templateVariables = [];

    // Fixed known-macro token names (Phase 3: name/grid pre-existing, freq/mode new) -- a {word}
    // token matching one of these is a MACRO reference, not a variable, and must never grow a
    // fill-bar row of its own.
    //
    // Tier B audit finding: this set was never updated when MacroTextResolver.ResolveBraceTokens
    // added "dist"/"bearing" (2026-08-18) -- {dist}/{bearing} were silently treated as USER
    // variables instead, growing phantom fill-bar rows the operator could type into, whose value
    // MacroTextResolver's own switch intercepts before ever reaching the variables dictionary --
    // so anything typed into those rows was accepted, displayed, persisted, and permanently
    // ignored. Reachable directly from the UI: the TEXT STYLE tab's own chips insert both tokens.
    private static readonly HashSet<string> KnownMacroTokenNames = new(StringComparer.Ordinal) { "name", "grid", "freq", "mode", "dist", "bearing" };

    // Suppresses RecomputePreview() while RotateCommand is mid-update (rotated working copy but
    // not-yet-transformed CropRect/overlay positions) -- without this, each overlay element's own
    // ImageWidth/ImageHeight PropertyChanged (now real notifications, see OverlayElementViewModel)
    // would each trigger a full Crop+Resize+ApplyOverlay against transiently inconsistent state.
    private bool _suspendPreview;

    // T0-12: guards RecomputePreviewCoalesced's deferred Dispatcher.UIThread.Post continuation --
    // see Dispose()'s own comment.
    private bool _disposed;

    // Also set/read by ApplyState (undo/redo) and RotateImageOnly -- see EditorSnapshot's own doc
    // comment for why this tracks orientation instead of retaining a pristine original image.
    private int _rotationCount;

    /// <summary>TX workflow modernization plan, Phase 7 -- the <see cref="_originalSource"/>
    /// instance as of the last WHOLE-SOURCE REPLACEMENT (editor open, or a flatten), plus the
    /// <see cref="_rotationCount"/> at that moment. This is what <see cref="EditorSnapshot"/>
    /// retains instead of the live <see cref="_originalSource"/>, and the distinction is deliberate:
    /// <see cref="RotateImageOnly"/> reassigns <see cref="_originalSource"/> to a NEW instance on
    /// every rotate, so snapshotting the live reference would retain one full-resolution copy per
    /// rotate (a 6000x4000 source is ~72 MB as Rgb24; 50 undo steps of rotation would retain ~3.6
    /// GB). Every snapshot within one "generation" shares this ONE instance;
    /// <see cref="ApplyState"/> re-derives orientation from it by rotating a delta, exactly as it
    /// already does for the rotation-only case. Only a flatten mints a new baseline, and retaining
    /// the pre-flatten image is inherent to flatten being undoable at all.</summary>
    private IImageSource _sourceBaseline;

    private int _sourceBaselineRotation;

    private readonly List<(EditorSnapshot Snapshot, long StateId)> _undoStack = [];

    /// <summary>Identity of the CURRENT canvas state, for <see cref="IsDirtySinceLastCheckpoint"/>. Every
    /// undo push mints a fresh id from <see cref="_nextStateId"/>; each undo/redo stack entry stores the id
    /// of the state it restores, and <see cref="Undo"/>/<see cref="Redo"/> set this back to that stored id.
    /// So returning to a checkpointed state by Undo reads clean, while load → Undo → a new edit mints an
    /// id the checkpoint has never seen and reads dirty (a plain counter that Undo decremented landed back
    /// on the checkpoint number there). Invariant kept from the earlier counter (yoniq-auditor Blocker 2):
    /// <see cref="MaxUndoDepth"/>'s own <c>RemoveAt(0)</c> eviction never touches ids -- it is a memory
    /// trim, not an operator action, so an edit made while the stack is pinned at 50 still reads dirty.</summary>
    private long _stateId;

    /// <summary>Monotonic source for <see cref="_stateId"/>; never decremented or reused.</summary>
    private long _nextStateId;

    /// <summary>Templates rack rework, yoniq-auditor finding -- a snapshot of <see cref="_stateId"/>
    /// taken right after the most recent successful template load, or, for a save, the id of the state
    /// that was serialized (read with the element snapshot, before the save's own awaits); 0 for an
    /// editor that has done neither. <see cref="LoadTemplateIntoLiveEditor"/> pushes ITS OWN undo snapshot (so the load
    /// itself is undoable, a deliberate existing design decision -- see that method's own doc comment),
    /// which means <see cref="HasUnsavedEdits"/> goes permanently true the instant ANY template is first
    /// loaded, with zero operator edits. Reusing that raw flag for the Templates rack's own
    /// "Loaded • edited" badge and its discard-confirm gate made both wrong: the badge read "edited"
    /// from the moment of load, and the modal fired on every SUBSEQUENT load in the same session even
    /// with nothing actually typed since. <see cref="IsDirtySinceLastCheckpoint"/> below measures
    /// relative to THIS baseline instead -- true only once <see cref="_stateId"/> moves away from
    /// what it was right after that checkpoint.
    /// <para>User-requested (2026-09-19): "when you save a template into the template library
    /// 'unsaved edits' should be removed and only added again if actual edits are made. Same for when
    /// it's loaded." Renamed from <c>_undoStackDepthAtLastTemplateLoad</c> and extended to also reset
    /// on a successful <see cref="SaveTemplateAsync"/> -- a checkpoint is now "the canvas matches what
    /// the template library has," not just "what was most recently loaded." Also promoted from backing
    /// only the Ready Rack badge to backing the header's own "UNSAVED EDITS" chip and the "New
    /// Template" discard-confirm gate too -- see <see cref="IsDirtySinceLastCheckpoint"/>'s own doc
    /// comment for why <see cref="HasUnsavedEdits"/> itself is deliberately left unchanged rather than
    /// redefined.</para>
    /// <para>Retyped from <c>int</c>/<see cref="_undoStack"/>`.Count`-based to <see langword="long"/>/
    /// <see cref="_stateId"/>-based (yoniq-auditor finding, Blocker 2) -- see that field's own doc
    /// comment.</para></summary>
    private long _stateIdAtLastCheckpoint;
    private readonly List<(EditorSnapshot Snapshot, long StateId)> _redoStack = [];

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
    /// always maintain <see cref="_target"/>'s own aspect ratio (spec/18-path-to-1.0.md High
    /// item 4). Orthogonal to <see cref="PreserveAspect"/>, which governs the separate Resize step
    /// (letterbox vs. stretch) -- this one constrains crop SHAPE. Default OFF so existing free-form
    /// behavior is unchanged unless a user opts in.</summary>
    [ObservableProperty]
    private bool _lockAspectToMode;

    /// <summary>Phase 6 (spec/15-template-designer.md) -- gates the always-been-there dashed
    /// safe-area guide `Rectangle`'s own `IsVisible`. Default true matches the toolbar toggle's own
    /// pre-Phase-6 `IsChecked="True"` stub value, so the guide's default-visible behavior is
    /// unchanged unless the operator turns it off.</summary>
    [ObservableProperty]
    private bool _safeAreaVisible = true;

    /// <summary>Phase 6 -- when on, an element drag/resize gesture snaps its final (pointer-release)
    /// position to a fixed 5%-of-working-copy grid, computed in EDGE space (not center/size
    /// independently -- see <see cref="SnapElementBoundsToGrid"/>'s own doc comment for why).
    /// Snap-ON-DROP only, never during the drag itself (plan-review blocker: continuous per-frame
    /// snapping against this editor's own incremental drag-delta model discards residual motion and
    /// can defeat <see cref="MinNormalizedElementSize"/>'s own vanishing-element floor). Does not
    /// affect the crop rect.</summary>
    [ObservableProperty]
    private bool _snapToGrid;

    /// <summary>Phase 7 (spec/15-template-designer.md) -- purely a view-layer convenience. **Real-
    /// window finding, post-implementation**: the original design wrapped the editor canvas in
    /// Avalonia's <c>LayoutTransformControl</c> and left every pixel-conversion property (LeftPixels/
    /// CropLeftPixels/etc.) untouched, on the theory that a render-transform ancestor would keep zoom
    /// entirely out of the VM's pixel math. That theory was wrong in a way static plan-review couldn't
    /// catch: <c>LayoutTransformControl</c>'s subtree does not reliably re-composite on a
    /// descendant-only bounds change (confirmed via pixel-diffed before/after screenshots -- dragging
    /// an element updated its bound X/Y correctly, and the separate mini-preview picked it up
    /// correctly, but the interactive canvas itself stayed visually frozen until an unrelated
    /// LayoutTransform-property change forced a fresh pass; reproduced identically at exactly 1.0
    /// zoom, ruling out "only happens when actually scaled"; three different Avalonia invalidation
    /// attempts -- InvalidateArrange, InvalidateMeasure+InvalidateArrange, reassigning a fresh
    /// ScaleTransform instance every drag frame -- all failed). Diagnosed (auditor) as a
    /// render/composition-invalidation gap specific to that control, not a layout-correctness bug --
    /// the underlying Bounds were already right every time. Rearchitected to bake <see
    /// cref="ZoomFactor"/> directly into the existing pixel-conversion properties instead (<see
    /// cref="CanvasDisplayWidth"/>/<see cref="CanvasDisplayHeight"/>, <c>Crop*Pixels</c>, each
    /// element's own <c>ImageWidth</c>/<c>ImageHeight</c> pushed pre-multiplied by zoom) -- ordinary
    /// Avalonia data-binding invalidation, the same mechanism every other property on this VM already
    /// relies on with zero issues, so this sidesteps the whole bug class rather than working around
    /// it. No <c>LayoutTransformControl</c>/<c>RenderTransform</c> anywhere in this feature anymore.
    /// Still never touches undo/redo, snap-to-grid math, the pipeline, or saved-template data -- those
    /// all stay in native working-copy pixel space regardless of this value; only the CANVAS DISPLAY
    /// properties (and the pointer-delta math that reads them back) are zoom-aware. Both current
    /// writers (<see cref="ApplyFit"/>, <see cref="ZoomActual"/>) already clamp to <see
    /// cref="MinZoomFactor"/>/<see cref="MaxZoomFactor"/> before assigning.</summary>
    [ObservableProperty]
    private double _zoomFactor = 1.0;

    [ObservableProperty]
    private Bitmap? _workingCopyBitmap;

    [ObservableProperty]
    private Bitmap? _previewImage;

    // T0-11 (production_audit.md): 2 recycled buffers each instead of a fresh WriteableBitmap on
    // every Recompute/rotate -- see WriteableBitmapPool's own doc comment. Kept SEPARATE (not one
    // shared pool): WorkingCopyBitmap (canvas scale) and PreviewImage (preview-panel scale) can
    // genuinely differ in size at the same time, and resizing one must not invalidate the other's
    // buffers. Follow-up to the Tier-0 audit (production_audit.md): Dispose() is now wired into
    // all 6 of TxControlsPaneViewModel's own editor-discard sites -- see Dispose()'s own doc
    // comment below for what it releases and its UI-thread requirement.
    private readonly WriteableBitmapPool _workingCopyPool = new();
    private readonly WriteableBitmapPool _previewPool = new();

    [ObservableProperty]
    private ITemplateElementViewModel? _selectedOverlayElement;

    /// <summary>TX editor gap-items plan, item 2 (group-ops-lite, 2026-09-02) -- the FULL multi-
    /// selection set (always includes <see cref="SelectedOverlayElement"/> as a member when
    /// non-empty; empty exactly when it's null). Kept in sync by
    /// <see cref="OnSelectedOverlayElementChanged"/>'s own self-healing hook for the common
    /// (0-1 selected) case, and explicitly by <see cref="SetSelection"/> for a real 2+ group -- see
    /// that hook's own doc comment for the full mechanism. GEOMETRY/STYLE sidebar tabs deliberately
    /// keep reading only <see cref="SelectedOverlayElement"/> (the primary) -- group style/resize/
    /// align is out of scope for this "lite" feature.</summary>
    public ObservableCollection<ITemplateElementViewModel> SelectedOverlayElements { get; } = [];

    /// <summary>Guards <see cref="OnSelectedOverlayElementChanged"/>'s own self-healing collapse
    /// while <see cref="SetSelection"/> is assigning <see cref="SelectedOverlayElement"/> as part of
    /// building a REAL multi-element selection -- without this, the hook would immediately collapse
    /// the very set <see cref="SetSelection"/> just populated. Save/restore (not a hardcoded
    /// true/false pair) so a hypothetical future nested call can't clear an outer call's own guard
    /// early.</summary>
    private bool _updatingSelectionSet;

    /// <summary>Phase 5 (spec/15-template-designer.md) -- bound to the Templates panel's name-entry
    /// TextBox, backing <see cref="SaveTemplateAsync"/>'s <see cref="CanSaveTemplate"/> gate.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveTemplateCommand))]
    private string _newTemplateName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveTemplateCommand))]
    private bool _isSavingTemplate;

    /// <summary>Backlog item (auditor usability review, 2026-08-17): "No error surface anywhere in
    /// the editor -- a failed Save/Load/add-image is ILogger-only, invisible to the operator." A
    /// plain nullable status string, same established shape as <c>RadioStatusViewModel.ErrorMessage</c>/
    /// <c>LogbookPaneViewModel.StatusMessage</c> elsewhere in this codebase -- no new subsystem.
    /// Doubles as the surface for the arm/confirm prompts below (Cancel/Recall-overwrite) -- both are
    /// "the editor needs to tell the operator something transient," the same real UI need.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Templates rack rework -- replaces the old arm/confirm status-bar warning
    /// (<c>_pendingRecallTemplateId</c>) with a real confirm dialog, same delegate-property shape as
    /// <see cref="ReadyRackViewModel.ConfirmRequested"/>/<c>RxHistoryPaneViewModel.ConfirmRequested</c>.
    /// Set once per editor instance by <c>MainWindow.axaml.cs</c>'s <c>EditorOpened</c> subscription
    /// (this VM is constructed fresh per editor, not a DI singleton). Returns <see langword="false"/>
    /// (decline -- stay on the current canvas) when unwired, the safe default.</summary>
    public Func<ConfirmActionDialogViewModel, Task<bool>>? ConfirmRequested { get; set; }

    private async Task<bool> RequestConfirmAsync(string title, string message, string confirmLabel) =>
        ConfirmRequested is null
            ? false
            : await ConfirmRequested(new ConfirmActionDialogViewModel(title, message, confirmLabel, _localization.GetString("Panes.TxImageEditor.DialogCancel"))).ConfigureAwait(true);

    /// <summary>Tier B audit finding: <see cref="ReadyRackViewModel"/>'s Load/RecallSlot commands are
    /// plain synchronous <c>[RelayCommand]</c>s that just raise <see cref="ReadyRackViewModel.TemplateSelected"/>
    /// into <see cref="OnReadyRackTemplateSelected"/> (an <c>async void</c>) -- CommunityToolkit's
    /// default no-concurrent-execution gate never applies here, so nothing serializes two overlapping
    /// loads. Without this, clicking template A (slow asset load) then quickly clicking template B
    /// (fast) let B populate the canvas first, then A's slower continuation overwrite it right back
    /// with A -- the operator ends up looking at the template they did NOT just ask for. Bumped
    /// before <see cref="LoadTemplateAsync"/>'s own first await, checked again right before
    /// <see cref="LoadTemplateIntoLiveEditor"/> actually mutates the canvas; a stale load is silently
    /// dropped rather than clobbering a newer one.</summary>
    private int _templateLoadGeneration;

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

    /// <summary>SSTV convenience overload: the editor composes for <paramref name="targetMode"/>'s own frame.</summary>
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
        EditorInitialState? initialState = null,
        Func<bool>? canTransmitNow = null,
        IReadOnlyDictionary<string, string>? currentContactVariables = null,
        Action? macrosReferenceRequested = null,
        Func<IReadOnlyDictionary<string, string>?>? currentContactProvider = null,
        bool carriedOverUnsavedEdits = false,
        Func<(bool IsTransmitting, double? Progress, string ProgressText)>? transmitStatusNow = null)
        : this(
            originalSource, targetMode.ToGeometry(), preparer, macroTextResolver, operatorSettings, radioSessionService,
            localization, logger, filePickerService, imageFileLoader, receivedImageBuffer, receiveHistoryStore,
            templateStore, imageSourceWriter, readyRack, initialState, canTransmitNow, currentContactVariables,
            macrosReferenceRequested, currentContactProvider, carriedOverUnsavedEdits, transmitStatusNow)
    {
    }

    public TxImageEditorPaneViewModel(
        IImageSource originalSource,
        TargetGeometry target,
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
        EditorInitialState? initialState = null,
        // Optional, defaulting to "always allowed": adding this as a REQUIRED parameter would have
        // forced every one of this class's ~20 existing test call sites to change for a feature
        // most of them don't exercise. Trailing-optional keeps it opt-in -- only the 2 real
        // production call sites (TxControlsPaneViewModel) pass a real delegate.
        Func<bool>? canTransmitNow = null,
        // ui_transition_plan.md step 5 (T1-6): "Copy to TX"'s own HIS CALL/HIS GRID seed -- see this
        // constructor's own application of it, below, for why it's independent of initialState
        // (Copy-to-TX opens a brand-new editor with no prior edit session to restore).
        IReadOnlyDictionary<string, string>? currentContactVariables = null,
        // Macros help plan (2026-09-01), item B: same trailing-optional shape as canTransmitNow above,
        // same reasoning -- only the 2 real production call sites (TxControlsPaneViewModel) pass a
        // real delegate. Must be a CLOSURE at those call sites, not a captured property value (plan-
        // review finding) -- TxControlsPaneViewModel.RequestMacrosReference is a settable property
        // assigned later by MainWindow.axaml.cs, so passing its value directly would snapshot null
        // permanently if this editor is constructed before that assignment runs.
        Action? macrosReferenceRequested = null,
        // Same trailing-optional/closure shape as macrosReferenceRequested above, same reasoning --
        // only routes that ALREADY pass a real (non-null) currentContactVariables above pass a real
        // delegate here too (thread caller intent: TxControlsPaneViewModel.OpenEditorForExternalFileAsync's
        // own "unseeded means unseeded" contract for a Gallery-sourced editor with no linked QSO must
        // not be reversed by this new parameter). Accepted-vestigial since the Ready Rack pileup/
        // direct-fire feature this was built for was removed 2026-09-19 -- see
        // _currentContactProvider's own doc comment.
        Func<IReadOnlyDictionary<string, string>?>? currentContactProvider = null,
        // Mode-switch-mid-edit feature: trailing-optional, same "existing test call sites don't
        // change" reasoning as canTransmitNow/macrosReferenceRequested above. Only
        // TxControlsPaneViewModel.ReplaceEditorForModeSwitch passes a real (non-default) value --
        // see HasUnsavedEdits's own doc comment for why this exists.
        bool carriedOverUnsavedEdits = false,
        // Header transmit-progress bar (2026-09-19 user request: "TX should be more obvious") --
        // same trailing-optional/closure/"no duplicated flag" shape as canTransmitNow above, only
        // the 2 real production call sites pass a real delegate. One snapshot delegate rather than
        // 3 separate ones since IsTransmitting/TransmitProgress/TransmitProgressText always change
        // together, at the same toggle points, on the parent.
        Func<(bool IsTransmitting, double? Progress, string ProgressText)>? transmitStatusNow = null)
    {
        _originalSource = originalSource;
        _sourceBaseline = originalSource;
        _sourceBaselineRotation = 0;
        _target = target;
        _carriedOverUnsavedEdits = carriedOverUnsavedEdits;
        _carriedOverDirtySinceCheckpoint = carriedOverUnsavedEdits;
        _preparer = preparer;
        _macroTextResolver = macroTextResolver;
        _canTransmitNow = canTransmitNow ?? (static () => true);
        _transmitStatusNow = transmitStatusNow ?? (static () => (false, null, string.Empty));
        _macrosReferenceRequested = macrosReferenceRequested;
        _currentContactProvider = currentContactProvider;
        _operatorSettings = operatorSettings;
        _radioSessionService = radioSessionService;
        _localization = localization;
        _logger = logger;
        _filePickerService = filePickerService;
        _imageFileLoader = imageFileLoader;
        _receivedImageBuffer = receivedImageBuffer;
        _receiveHistoryStore = receiveHistoryStore;
        _templateStore = templateStore;
        _persistenceMapper = new TemplateElementPersistenceMapper(templateStore, imageSourceWriter, imageFileLoader, logger);
        ReadyRack = readyRack;
        ReadyRack.TemplateSelected += OnReadyRackTemplateSelected;
        // User-reported bug (2026-09-15): after Remove Background, Browse/Stock/Open Editor in the
        // sidebar stayed greyed out -- see HasNoBackgroundOrOverlayElements's own doc comment. Every
        // element add/remove (AddOverlayElement, RemoveOverlayElement, FlattenElementAsync,
        // DemoteBackdropToBackgroundAsync, ApplyState's wholesale Undo/Redo restore, and every other
        // OverlayElements mutation) funnels through this ONE collection, so a single subscription
        // here covers all of them instead of patching each call site individually.
        OverlayElements.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoBackgroundOrOverlayElements));

        _workingCopy = BuildWorkingCopy(originalSource, target, preparer);
        WorkingCopyBitmap = _workingCopyPool.Blit(_workingCopy);

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

        // ui_transition_plan.md step 5 (T1-6): applied AFTER initialState's own seed (above) and via
        // TryAdd, not direct assignment -- a re-edit's own previously-typed values must win over a
        // stale currentContactVariables snapshot passed alongside them (no call site passes both
        // today, but this ordering is the safe default if that ever changes). Written directly into
        // _templateVariables, same "read-only lookup, never eagerly written" contract
        // RescanTemplateVariables's own doc comment describes -- a template loaded/created LATER in
        // this same editor session that references {his_call}/{his_grid} picks this up the moment
        // RescanTemplateVariables next runs; one with no such reference never surfaces it at all.
        if (currentContactVariables is not null)
        {
            foreach (var (key, value) in currentContactVariables)
            {
                _templateVariables.TryAdd(key, value);
            }
        }

        RecomputePreview();

        // Live-locale-switch review (2026-09-20): every stored GetString-backed member here is the
        // transient StatusMessage (self-heals on the next failure or accepted stale in the
        // meantime, same convention as every other VM in this pass); every other GetString-backed
        // member is a computed getter, so the blanket refresh alone is enough. ReadyRack
        // deliberately does NOT get its own subscription -- it has no computed GetString members at
        // all (only its own transient StatusMessage), so a refresh call would be a no-op, and
        // subscribing it directly to this long-lived singleton would reintroduce exactly the leak
        // this fix removes elsewhere (see its "both die together" doc comment just below -- only
        // THIS class may subscribe to anything outside its own and ReadyRack's lifetime).
        //
        // Absolute last statement in the constructor, deliberately: this constructor CAN throw
        // (BuildWorkingCopy above, an oversized source image) -- TxControlsPaneViewModel's
        // construction call sites catch that and dispose the PREVIOUS editor, not this
        // never-fully-constructed one, so a subscription added before a throw would orphan itself
        // in ILocalizationService's invocation list forever. Subscribing only after every
        // throwing statement has already succeeded means Dispose() below is always reachable for
        // any instance that got this far.
        localization.CultureChanged += OnCultureChanged;
    }

    private void OnCultureChanged() => OnPropertyChanged(string.Empty);

    // T0-11 follow-up (production_audit.md): releases _workingCopyPool/_previewPool and every
    // live OverlayElements entry's own bitmap resources. Wired into TxControlsPaneViewModel's own
    // editor-discard sites (CloseBlankEditorForReplacement, OnEditorCancelled, and construction-
    // failure catches -- OnEditorApplied no longer disposes/discards the editor at all since
    // 2026-09-19, see its own doc comment) via a uniform "_currentEditor?.Dispose(); _currentEditor
    // = null;" -- see that class's own comments at each site. Deliberately does NOT unsubscribe this
    // instance's own Applied/Cancelled/PropertyChanged handlers -- unchanged, already-documented
    // precedent (CloseBlankEditorForReplacement's own doc comment): nothing will ever invoke them
    // again once the caller swaps to a different editor instance, disposed or not.
    //
    // MUST BE CALLED ON THE UI THREAD. Every wired call site in TxControlsPaneViewModel reaches this
    // on the UI thread today -- the normal-close sites (CloseBlankEditorForReplacement,
    // OnEditorCancelled) are synchronous CommunityToolkit [RelayCommand]/event-handler methods;
    // ReplaceEditorForModeSwitch's own construction-failure catch is fully synchronous, trivially on
    // the UI thread; and OpenEditorWithLoadedSourceAsync's construction-failure catch is reached via
    // `await`s with no ConfigureAwait(false) anywhere in its chain, so Avalonia's captured
    // SynchronizationContext resumes it on the UI thread too (NOT because that catch contains no
    // background dispatch -- auditor code-review correction, Tier-0 audit follow-up).
    public void Dispose()
    {
        // Idempotency guard, matching ImageElementViewModel.Dispose()'s own "guarded against a second
        // call" convention -- auditor code-review finding: WriteableBitmapPool.Dispose() doesn't null
        // its own slots, so a second call here would double-dispose the same WriteableBitmap. Not
        // reachable today (every call site nulls _currentEditor atomically right after calling this),
        // but IDisposable's own contract expects it regardless.
        if (_disposed)
        {
            return;
        }

        // Live-locale-switch review (2026-09-20): pairs with the CultureChanged subscription at the
        // end of the constructor -- this instance is genuinely recreated (mode switch/New Template),
        // unlike this codebase's other DI-singleton panes, so unsubscribing here is load-bearing,
        // not defensive hygiene: every discarded editor that skipped this would leak forever in the
        // long-lived ILocalizationService's own invocation list.
        _localization.CultureChanged -= OnCultureChanged;

        // T0-12: _disposed guards RecomputePreviewCoalesced's deferred Dispatcher.UIThread.Post
        // continuation, and (auditor code-review finding, Tier-0 audit follow-up) RecomputePreview's/
        // NotifyWorkingCopyGeometryChanged's own new guards above -- without those, an await-crossing
        // operation still in flight when Dispose() runs (Cancel/Apply have no busy gate) would Lock()
        // an already-disposed pooled WriteableBitmap and NRE on the UI thread with no observer.
        _disposed = true;
        ++_templateLoadGeneration;

        foreach (var element in OverlayElements)
        {
            if (element is IDisposable disposableElement)
            {
                disposableElement.Dispose();
            }
        }

        // SYNCHRONOUS, not deferred -- unlike each element VM's own Dispose() (which defers via
        // Dispatcher.UIThread.Post specifically because ITS bitmap is still reachable through a live
        // OverlayElements-bound DataTemplate until the View's own next layout pass), the pool's 2
        // bitmaps back WorkingCopyBitmap/PreviewImage, which nothing renders once EditorClosed fires
        // right after this returns -- an established, already-reviewed T0-11 choice, unchanged here.
        // A prior draft of this fix deferred this too, "for symmetry" -- reverted: it doesn't actually
        // prevent the crash the _disposed guards above exist for (verified empirically: with those
        // guards removed, a deferred dispose delays the underlying bitmap's own disposal past the
        // point an await-crossing operation's Blit() call would hit it, so the crash silently stops
        // reproducing in a test without being fixed), and it defeats the whole point of wiring
        // Dispose() into a real discard path -- reclaiming native bitmap memory PROMPTLY, not whenever
        // a Background-priority job happens to run.
        _workingCopyPool.Dispose();
        _previewPool.Dispose();
    }

    /// <summary>Phase 5 (spec/15-template-designer.md) -- the Templates panel's saved-template list
    /// + 9-slot pinned rack. Constructed fresh per editor instance by the host
    /// (<see cref="TxControlsPaneViewModel"/>), same "no DI singleton, no cross-editor-instance
    /// leak" reasoning as the editor itself -- see <see cref="TemplateSelected"/>'s own subscription
    /// in this constructor for why no unsubscribe/IDisposable is needed either (both die together).</summary>
    public ReadyRackViewModel ReadyRack { get; }

    /// <summary>Templates rack rework -- replaces the old arm/confirm status-bar warning with a real
    /// confirm dialog (<see cref="ConfirmRequested"/>) when <see cref="IsDirtySinceLastCheckpoint"/>,
    /// applied uniformly to BOTH the rack's numbered-slot recall/double-click and the Template
    /// Library's own Load button (both funnel through <see cref="ReadyRackViewModel.TemplateSelected"/>
    /// into this one handler). Declining leaves the canvas untouched. Also the error-surface fix for
    /// a failed load (item 11) -- previously ILogger-only.
    /// <para>yoniq-auditor finding: the confirm-dialog await now lives INSIDE the try -- it used to
    /// sit before it, so a throw from <c>ShowDialog</c> itself (e.g. the owner window closing mid-
    /// await) was an unhandled exception out of an <c>async void</c> method, a process crash rather
    /// than a logged, surfaced failure.</para></summary>
    private async void OnReadyRackTemplateSelected(string templateId)
    {
        if (_disposed) return;
        try
        {
            if (IsDirtySinceLastCheckpoint)
            {
                var confirmed = await RequestConfirmAsync(
                    _localization.GetString("Panes.TxImageEditor.ConfirmDiscardTitle"),
                    _localization.GetString("Panes.TxImageEditor.ConfirmDiscardBody", ReadyRack.GetTemplateName(templateId)),
                    _localization.GetString("Panes.TxImageEditor.ConfirmDiscardButton"));
                if (_disposed || !confirmed)
                {
                    return;
                }
            }

            StatusMessage = null;
            var generation = ++_templateLoadGeneration;
            ReadyRack.SetLoadInFlight(true);
            try
            {
                if (await LoadTemplateAsync(templateId, generation))
                {
                    // Order matters here: LoadTemplateIntoLiveEditor's own PushUndoSnapshot (inside
                    // LoadTemplateAsync, above) already fired SetCanvasDirty(true) against the OLD
                    // baseline, since the load itself grew the undo stack. The checkpoint reset itself
                    // now happens INSIDE LoadTemplateIntoLiveEditor, right after the new elements are in
                    // place (yoniq-auditor risk finding: capturing it here instead, after this method's
                    // own later ListAsync() await, left a real window where an edit made during that
                    // await got silently absorbed into the "clean" checkpoint, and made the chip visibly
                    // flash dirty-then-clean on every load) -- this call just re-notifies the ALREADY-
                    // correct (false) dirty state BEFORE SetLoadedTemplate, so its own ApplyLoadedState
                    // computes IsLoadedAndEdited from the right value instead of the stale "dirty" one
                    // Push left behind. Doing it in the opposite order (an earlier draft's bug, caught by
                    // LoadTemplate_CleanEditor_FirstLoad_DoesNotAskAndBadgeIsPlainLoaded) left a
                    // freshly-loaded, unedited slot showing "Loaded • edited".
                    OnPropertyChanged(nameof(IsDirtySinceLastCheckpoint));
                    ReadyRack.SetCanvasDirty(IsDirtySinceLastCheckpoint);
                    ReadyRack.SetLoadedTemplate(templateId);
                }
            }
            finally
            {
                if (!_disposed)
                {
                    ReadyRack.SetLoadInFlight(false);
                }
            }
        }
        catch (Exception ex)
        {
            Log.LoadTemplateFailed(_logger, templateId, ex);
            if (!_disposed)
            {
                StatusMessage = _localization.GetString("Panes.TxImageEditor.LoadTemplateFailed");
            }
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

    /// <summary>spec/18-path-to-1.0.md Medium item: the card header used to be a static locale
    /// string ("EDITOR — OUTGOING FRAME · 640×496 · PD120") regardless of the actual mode/image
    /// being edited. <see cref="_target"/> is fixed for this editor instance's whole lifetime
    /// (frozen at construction, spec/18-path-to-1.0.md High item 2's own stale-mode-transmit-crash
    /// fix), so this never needs to react to a mode change mid-edit -- a plain computed property,
    /// not an <see cref="ObservableProperty"/>.</summary>
    public string HeaderText => _localization.GetString(
        "Panes.TxImageEditor.CardHeaderFormat", _target.Width, _target.Height, _target.DisplayName);

    /// <summary>Design-fidelity Phase H (final confirming pass finding #6) -- the PREVIEW panel's
    /// own MaxHeight, bound to the real target mode height rather than a hardcoded 200 (which
    /// downscaled every 256-line mode's "mode-exact" preview to 78%, defeating its own point --
    /// the mock renders this panel at literal 1:1). Same frozen-for-the-editor's-lifetime reasoning
    /// as <see cref="HeaderText"/> -- a plain computed property, not observable.</summary>
    public double PreviewMaxHeight => _target.Height;

    /// <summary>Same real-dimensions fix as <see cref="HeaderText"/>, for the separate dimensions
    /// chip lower in the tool strip (mock2's own layout keeps both -- the chip is a compact
    /// at-a-glance readout next to the aspect/text/apply controls, not a duplicate of the header).</summary>
    public string DimensionsChipText => _localization.GetString(
        "Panes.TxImageEditor.DimensionsChipFormat", _target.Width, _target.Height);

    /// <summary>Mode-switch-mid-edit feature -- lets a host (<see cref="TxControlsPaneViewModel"/>)
    /// log which mode a replaced editor instance was targeting, without needing its own copy of
    /// <see cref="_target"/>. No other reader existed before this feature; every other
    /// <see cref="_target"/> use stays internal.</summary>
    public string TargetModeId => _target.Id;

    /// <summary>Mode-switch-mid-edit feature -- lets <see cref="TxControlsPaneViewModel.ReplaceEditorForModeSwitch"/>
    /// reuse the OLD editor instance's already-loaded <see cref="OperatorSettings"/> when
    /// constructing the NEW one, instead of an async <c>_settingsStore.LoadAsync()</c> reload. Kept
    /// fully synchronous on purpose -- a reload would reopen the exact re-entrancy window
    /// (two fast mode changes racing to construct two editors) that this editor's other
    /// re-open-with-seeded-state paths don't have to worry about, since those all run from an
    /// `async Task` already. <see cref="_operatorSettings"/> is kept fresh by
    /// <see cref="RefreshOperatorSettings"/> now, so reusing it here is at least as fresh as the
    /// editor session already was, and possibly fresher.</summary>
    public OperatorSettings OperatorSettings => _operatorSettings;

    /// <summary>User-reported (2026-09-20): pushes a newly-saved Callsign/Name/Grid into an
    /// ALREADY-OPEN editor so its live preview stops resolving %m/{name}/{grid}/{dist}/{bearing}
    /// against a stale snapshot (see <see cref="_operatorSettings"/>'s own doc comment). Same
    /// "refresh every element, then canvas styles, then the real pipeline preview" shape as
    /// <see cref="OnTemplateVariableValueChanged"/> -- unlike that handler, every element's
    /// <see cref="OverlayElementViewModel.ResolvedText"/> could be affected here (any of them may
    /// reference any operator-settings-sourced token), so there's no per-key filter to narrow the
    /// loop.</summary>
    public void RefreshOperatorSettings(OperatorSettings updated)
    {
        _operatorSettings = updated;

        foreach (var element in OverlayElements.OfType<OverlayElementViewModel>())
        {
            element.NotifyResolvedTextChanged();
        }

        RefreshOverlayElementCanvasStyles();
        RecomputePreview();
    }

    /// <summary>EditWindow redesign, design-fidelity Phase B (mockups/Editwindow) -- the new context
    /// bar's mono frame readout ("OUTGOING FRAME 320×256 · MARTIN M1 · 114.3 s"). Duration reuses
    /// <see cref="TxControlsPaneViewModel.GetFrameSeconds"/>, not a new computation, just applied to
    /// THIS editor's own fixed <see cref="_target"/> -- a naive <c>LineDurationMs * ImageHeight
    /// / 1000.0</c> here would double-count for <see cref="ColorEncoding.YCbCrLinePaired"/>/
    /// <see cref="ColorEncoding.MonoAveragedPaired"/> modes (the exact PD90-family bug that method's
    /// own doc comment documents fixing on the TX Controls card; this editor was left using the
    /// naive formula when that fix landed, a real bug caught and fixed later the same day).</summary>
    public string FrameReadoutText => (_target.FrameSeconds ?? _estimatedFrameSeconds) is { } seconds
        ? _localization.GetString(
            "Panes.TxImageEditor.FrameReadoutFormat",
            _target.Width, _target.Height, _target.DisplayName.ToUpperInvariant(), seconds)
        : _localization.GetString(
            "Panes.TxImageEditor.FrameReadoutFormatNoDuration",
            _target.Width, _target.Height, _target.DisplayName.ToUpperInvariant());

    /// <summary>Design-fidelity Phase C (mockups/Editwindow line 130) -- canvas footer, bottom-left:
    /// crop dims (always the target mode's own fixed render size, same numbers as
    /// <see cref="DimensionsChipText"/>). The "LOCKED TO MODE" segment is a separate,
    /// <see cref="LockAspectToMode"/>-gated chip in the View, not baked into this string, so its
    /// IsVisible binding stays a plain bool instead of parsing this text.</summary>
    public string CropFooterText => _localization.GetString(
        "Panes.TxImageEditor.CropFooterFormat", _target.Width, _target.Height);

    /// <summary>Design-fidelity Phase C (mockups/Editwindow line 131) -- canvas footer, bottom-right:
    /// the live working-copy pixel size (which changes with zoom-independent factors like rotation)
    /// alongside the fixed render target size.</summary>
    public string WorkingCopyFooterText => _localization.GetString(
        "Panes.TxImageEditor.WorkingCopyFooterFormat", WorkingCopyWidth, WorkingCopyHeight, _target.Width, _target.Height);

    /// <summary>Design-fidelity Phase E (#15, mockups/Editwindow line 108) -- a small mono readout
    /// above the selected element on the interactive canvas: type/z-order + pixel geometry, plus
    /// rotation for whichever element kinds have a persisted <c>RotationDegrees</c> (Text/Box/Image
    /// -- element rotation, 2026-09-20, generalized from the original text-only readout; Line has no
    /// persisted angle to show, see <c>TemplateLineElement</c>'s own doc comment for why). All-existing
    /// properties (LeftPixels/TopPixels/CanvasWidthPixels/CanvasHeightPixels/Z/RotationDegrees), reused
    /// rather than duplicated. Raised on selection change (<see cref="OnSelectedOverlayElementChanged"/>)
    /// and on every geometry-driving PropertyChanged of the CURRENTLY selected element (see
    /// <see cref="OnOverlayElementPropertyChanged"/>'s own end-of-method raise).</summary>
    public string SelectionReadoutText
    {
        get
        {
            if (SelectedOverlayElement is not { } element)
            {
                return string.Empty;
            }

            var typeLabel = element switch
            {
                OverlayElementViewModel => _localization.GetString("Panes.TxImageEditor.TypeBadgeText"),
                BoxElementViewModel => _localization.GetString("Panes.TxImageEditor.TypeBadgeBox"),
                ImageElementViewModel => _localization.GetString("Panes.TxImageEditor.TypeBadgeImage"),
                LineElementViewModel => _localization.GetString("Panes.TxImageEditor.TypeBadgeLine"),
                _ => throw new NotSupportedException($"Unrecognized {nameof(ITemplateElementViewModel)}: {element.GetType()}."),
            };

            double? rotationDegrees = element switch
            {
                OverlayElementViewModel text => text.RotationDegrees,
                BoxElementViewModel box => box.RotationDegrees,
                ImageElementViewModel image => image.RotationDegrees,
                _ => null,
            };

            return rotationDegrees is { } rotation
                ? _localization.GetString(
                    "Panes.TxImageEditor.SelectionReadoutWithRotationFormat",
                    typeLabel, element.Z, (int)Math.Round(element.LeftPixels), (int)Math.Round(element.TopPixels),
                    (int)Math.Round(element.CanvasWidthPixels), (int)Math.Round(element.CanvasHeightPixels), (int)Math.Round(rotation))
                : _localization.GetString(
                    "Panes.TxImageEditor.SelectionReadoutFormat",
                    typeLabel, element.Z, (int)Math.Round(element.LeftPixels), (int)Math.Round(element.TopPixels),
                    (int)Math.Round(element.CanvasWidthPixels), (int)Math.Round(element.CanvasHeightPixels));
        }
    }

    /// <summary>The live, current-orientation source -- reflects any <see cref="RotateCommand"/>
    /// calls so far. Round-1 plan-review finding on spec/18-path-to-1.0.md High item 3: a host
    /// (<see cref="TxControlsPaneViewModel"/>) that captured the ORIGINAL constructor argument
    /// instead of reading this property would silently revert a rotate the next time it re-derives
    /// from that stale reference (e.g. on a later mode change) -- see
    /// <c>TxControlsPaneViewModel.OpenEditorForSourceAsync</c>'s own use of this property.</summary>
    public IImageSource CurrentSource => _originalSource;

    /// <summary>Accepted-vestigial: see <see cref="_currentContactProvider"/>'s own doc comment.
    /// This property has no remaining reader since the Ready Rack pileup/direct-fire feature it was
    /// built for was removed 2026-09-19.</summary>
    public Func<IReadOnlyDictionary<string, string>?>? CurrentContactProvider => _currentContactProvider;

    /// <summary>The safe-area inset in WORKING-COPY (unzoomed) units -- shared by
    /// <see cref="SafeAreaInsetPixels"/> (display) and <see cref="ApplyFitSafeArea"/> (which needs
    /// the zoom-INDEPENDENT inset to avoid a circular "zoom depends on safe-area size which depends
    /// on zoom" dependency).</summary>
    private const double SafeAreaInsetWorkingCopyUnits = 14;

    /// <summary>Follow-up visual-polish pass to the TX workflow modernization plan -- live rubber-
    /// band preview during draw-to-place (<c>TxImageEditorPaneView.OnCanvasPointerMoved</c>'s own
    /// <c>DragMode.Placing</c> branch sets this on every frame; <c>OnCanvasPointerReleased</c>/
    /// <c>DisarmPlacement</c>/<c>CancelActiveDrag</c>/<c>OnEditorCanvasPointerCaptureLost</c> all
    /// clear it, every path that can end a placement drag/arm). Top-level, not per-element, like
    /// <see cref="CropRect"/> above -- this has no element to belong to until the drag actually
    /// completes and creates one. Null at rest (nothing shown).</summary>
    [ObservableProperty]
    private NormalizedRect? _placementPreviewRect;

    /// <summary>TX editor gap-items plan, line element (2026-09-01) -- the line counterpart to
    /// <see cref="PlacementPreviewRect"/> immediately above, same "live rubber-band preview during
    /// draw-to-place, cleared by every path that can end a placement drag/arm" contract. A rect
    /// can't represent a line's own direction (a line drawn top-right to bottom-left must render
    /// that way during the drag, not get normalized into a box), so this is a parallel field, not a
    /// reuse of <see cref="PlacementPreviewRect"/> -- endpoints in normalized full-working-copy
    /// space, same convention as <see cref="LineElementViewModel.X1"/>/etc.</summary>
    [ObservableProperty]
    private (double X1, double Y1, double X2, double Y2)? _placementPreviewLine;

    /// <summary>Same follow-up pass, the alignment-guide-line half --
    /// <c>TxImageEditorPaneView.OnCanvasPointerMoved</c>'s own <c>DragMode.Overlay</c> branch sets
    /// these from <c>ComputeAlignmentGuideLines</c> (display-only: the drop-time snap itself stays
    /// exactly the existing <c>ComputeAlignmentSnap</c>-driven behavior, unchanged by this).
    /// Independent per axis, same as the underlying alignment match itself.</summary>
    [ObservableProperty]
    private double? _guideLineXNormalized;

    [ObservableProperty]
    private double? _guideLineYNormalized;

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
    public IReadOnlyList<RawElementSnapshot> RawOverlayElements => OverlayElements.Select(e => e.ToSnapshot()).ToList();

    public event Action<IImageSource>? Applied;

    /// <summary>ui_transition_plan.md step 2 (T1-2): the SEND row's primary action -- applies the
    /// same output <see cref="Applied"/> would, but signals the parent to immediately transmit it
    /// too, one click instead of Apply-then-hunt-for-the-real-Transmit-button-in-the-sidebar. The
    /// parent (only owner of transmit state) still runs the actual TransmitCommand.</summary>
    public event Action<IImageSource>? AppliedAndTransmitRequested;

    public event Action? Cancelled;

    /// <summary>Floor for <see cref="NudgeElementResize"/> -- same value as
    /// <c>TxImageEditorPaneView.MinNormalizedElementSize</c> (a SEPARATE, deliberately identical
    /// constant, not a shared reference, per that field's own doc comment: the drag-resize floor is
    /// View-local, this one guards the new keyboard path instead).</summary>
    private const double MinNormalizedElementResizeSize = 0.02;

    // Phase 1 defaults. DefaultTextElementHeight is deliberately well above DefaultFontSizeRelative
    // (~1.8x), not flush to it -- plan-review finding: TextMeasurer's own line height (ascender +
    // descender + gap) exceeds a bare em size, so a box sized tight to the font fraction would
    // shrink-to-fit immediately on creation, making "Add text" a visible regression from day one.
    // First three are `internal` (not private), not because anything outside this class writes them,
    // but so TxImageEditorPaneView.axaml.cs's draw-to-place click-vs-drag branch (TX workflow
    // modernization plan, Phase 3a) reads the exact same default-size values AddOverlayElement/
    // AddBoxElement themselves use, rather than a second, driftable copy of the same three numbers.
    internal const double DefaultElementWidth = 0.3;
    internal const double DefaultTextElementHeight = 0.18;
    internal const double DefaultBoxElementHeight = 0.2;
    private const double DefaultFontSizeRelative = 0.1;
    // TX editor gap-items plan (2026-09-01) -- image-height-relative, same convention as
    // BoxElementViewModel.BorderThickness; ~2-3px at a typical SSTV mode's own render height.
    internal const double DefaultLineStrokeThickness = 0.01;

    private const int RxHistoryPickerMaxEntries = 20;
    private const int RxHistoryPickerThumbnailMaxDimension = 96;

    [ObservableProperty]
    private ObservableCollection<RxHistoryPickerEntry> _rxHistoryPickerEntries = [];

    private const int MaxDroppedImageFiles = 20;

    private bool _isDemoting;

    /// <summary>Every field of a brand-new text element; callers set position/size/Z with
    /// <c>with { ... }</c>. <see cref="RawTextElementSnapshot.FontFamily"/> is empty here because the
    /// preparer's default family is instance state -- <see cref="AddOverlayElementAt"/> supplies it.</summary>
    private static readonly RawTextElementSnapshot NewTextElementDefaults = new(
        X: 0, Y: 0, Width: 0, Height: 0, Z: 0, Locked: false,
        Text: "Text", FontSizeRelative: DefaultFontSizeRelative, Color: new Rgb24(255, 255, 255),
        FontFamily: string.Empty, StrokeColor: null, StrokeThickness: 0.02,
        ShadowColor: null, ShadowOffsetX: 0.02, ShadowOffsetY: 0.02, RotationDegrees: 0,
        GradientEnabled: false, GradientKind: TextGradientKind.Horizontal,
        GradientStartColor: null, GradientEndColor: null,
        Bold: false, Italic: false,
        StackColor: null, StackStepX: 0.02, StackStepY: 0.02,
        BitmapFillEnabled: false, BitmapFillSource: null,
        GrowToFillEnabled: false);

    /// <summary>Every field of a brand-new box element; callers set position/size/Z with
    /// <c>with { ... }</c>.</summary>
    private static readonly RawBoxElementSnapshot NewBoxElementDefaults = new(
        X: 0, Y: 0, Width: 0, Height: 0, Z: 0, Locked: false,
        FillColor: new Rgb24(64, 64, 64), BorderColor: null, BorderThickness: 0, Opacity: 1.0, CornerRadius: 0,
        GradientEnabled: false, GradientKind: TextGradientKind.Horizontal,
        GradientStartColor: null, GradientEndColor: null,
        PerspectiveEnabled: false,
        Corner0X: 0, Corner0Y: 0, Corner1X: 0, Corner1Y: 0,
        Corner2X: 0, Corner2Y: 0, Corner3X: 0, Corner3Y: 0,
        FillEnabled: true,
        RotationDegrees: 0);

    /// <summary>Every field of a brand-new line element; callers set the endpoints and Z with
    /// <c>with { ... }</c>. The base X/Y/Width/Height stay 0: <see cref="CreateLineElement"/> reads
    /// only the endpoints.</summary>
    private static readonly RawLineElementSnapshot NewLineElementDefaults = new(
        X: 0, Y: 0, Width: 0, Height: 0, Z: 0, Locked: false,
        X1: 0, Y1: 0, X2: 0, Y2: 0, StrokeColor: new Rgb24(255, 255, 255), StrokeThickness: DefaultLineStrokeThickness, Opacity: 1.0);

    /// <summary>The Fit ComboBox's own ItemsSource -- plain <c>Enum.GetValues</c> property, same
    /// pattern as <see cref="AvailableGradientKinds"/>.</summary>
    public IReadOnlyList<ImageFitMode> AvailableImageFitModes { get; } = Enum.GetValues<ImageFitMode>();

    /// <summary>Phase 8 -- the TEXT STYLE panel's gradient-direction picker ItemsSource, same
    /// plain-`Enum.GetValues`-property pattern already established for enum ComboBoxes elsewhere in
    /// this codebase (e.g. <c>RadioStatusViewModel.AvailableModes</c>).</summary>
    public IReadOnlyList<TextGradientKind> AvailableGradientKinds { get; } = Enum.GetValues<TextGradientKind>();

    /// <summary>EditWindow redesign Phase 3 (mockups/Editwindow) -- three mutually-exclusive flags
    /// rather than an enum + converter (simpler to bind directly as three IsVisible targets in
    /// AXAML, matches this codebase's own existing preference for plain bool ObservableProperty
    /// state over enum-plus-converter machinery elsewhere in this file). Defaults to Text Style,
    /// matching the mock's own default.
    /// <para>TX workflow modernization plan, Phase 2 -- revises the original "never auto-switch"
    /// policy, not abandons it: the original concern (auto-switching would fight a user who
    /// deliberately left GEOMETRY open while clicking between several elements to compare positions)
    /// is preserved exactly, since Geometry applies to every element type and is never
    /// force-switched away from (see <see cref="SwitchToApplicableTabIfNeeded"/>). What changed is
    /// the common case: selecting a text/image element while on an INAPPLICABLE tab (e.g. Image tab
    /// selected, then a text element clicked) now switches to the applicable one, and a genuinely
    /// NEW element (Add Text/Add Box toolbar, not Paste/Duplicate/Ctrl-drag-clone -- see each of
    /// those commands' own call sites) force-selects its tab unconditionally, even overriding an
    /// open Geometry tab, since there's nothing to "compare positions" against yet.</para></summary>
    [ObservableProperty]
    private bool _isTextStyleTabSelected = true;

    [ObservableProperty]
    private bool _isGeometryTabSelected;

    [ObservableProperty]
    private bool _isImageTabSelected;

    /// <summary>TX workflow modernization plan, Phase 7 -- bake-frame pixel ceiling. At the natural
    /// bake scale (source-crop pixels per target-frame pixel) a large photo into a small mode wants
    /// a proportionally large bake frame (a 6000x4000 photo into a 320x256 mode wants roughly
    /// 6000x4800, ~29 Mpx). Capped here, with the write-back deliberately LOCALISED to the element's
    /// own bounding box (see <see cref="BakeElementIntoSource"/>) so hitting this cap softens only
    /// the region under the element rather than resampling the entire photo through a round trip.</summary>
    private const long MaxFlattenBakePixels = 24_000_000;

    /// <summary>Outward margin, in SOURCE-crop pixels, added around the element's own bounding box
    /// before writing the baked patch back. Covers bicubic ringing at the patch edges (~2px) plus
    /// the sub-pixel slack in the two rect round-trips in <see cref="BakeElementIntoSource"/>.</summary>
    private const int FlattenWriteBackMarginPx = 3;

    private bool _isFlattening;

    /// <summary>Backlog item (user request, 2026-08-17): in-editor Copy/Cut/Paste for canvas
    /// elements -- NOT the OS clipboard (no cross-app paste target exists for a
    /// <see cref="RawElementSnapshot"/>), a plain in-memory field, same scope as every other
    /// element-manipulation command in this file. Copy/Cut/Paste are wired to Ctrl/Cmd+C/X/V in
    /// <c>TxImageEditorPaneView.axaml.cs</c>'s <c>OnCanvasKeyDown</c>, the same handler as the
    /// existing Delete/arrow-key/Escape bindings.</summary>
    private RawElementSnapshot? _clipboardSnapshot;

    /// <summary>TX workflow modernization plan, Phase 1 -- Copy/Paste Style. A SEPARATE clipboard
    /// field from <see cref="_clipboardSnapshot"/> (element copy/paste): copying an element's style
    /// must not clobber a pending element paste, and vice versa. Reuses <see cref="ITemplateElementViewModel.ToSnapshot"/>
    /// wholesale rather than a second, narrower snapshot type -- each element's <c>ApplyStyleFrom</c>
    /// picks out only the style-relevant fields when applying, so nothing new needs to track "which
    /// fields count as style" in two places. Gated to same-element-kind-only paste (text style onto
    /// text, box style onto box) -- deliberately, to avoid an ambiguous partial application if the
    /// two kinds' style fields don't line up (text has font/shadow/stack that box doesn't, box has
    /// fill/border/corner-radius that text doesn't -- both also carry gradient, box gradient fill
    /// added 2026-09-01). Image elements have no copyable "style"
    /// distinct from Fit (which already has its own quick-access submenu), so neither command is
    /// reachable for them.</summary>
    private RawElementSnapshot? _styleClipboardSnapshot;

    [RelayCommand]
    private void Apply()
    {
        Log.ApplyInvoked(_logger, _target.Id);
        Applied?.Invoke(BuildFinalOutput());
    }

    /// <summary>Gates the SEND row's "Apply &amp; Transmit" button on the PARENT's live transmit
    /// readiness (see <see cref="_canTransmitNow"/>'s own doc comment) -- this editor's own output
    /// is always producible, so unlike <see cref="TxControlsPaneViewModel.CanTransmit"/> there is no
    /// "_loadedImage is not null" half to this check, only the transmitting/self-test half.</summary>
    private bool CanApplyAndTransmit() => _canTransmitNow();

    [RelayCommand(CanExecute = nameof(CanApplyAndTransmit))]
    private void ApplyAndTransmit()
    {
        Log.ApplyAndTransmitInvoked(_logger, _target.Id);
        AppliedAndTransmitRequested?.Invoke(BuildFinalOutput());
    }

    /// <summary>Called by the parent at each of its own IsTransmitting/IsRunningLoopbackSelfTest
    /// toggle points, same convention it already follows for TransmitCommand/StopTransmitCommand/
    /// RunLoopbackSelfTestCommand -- CommunityToolkit does not auto-requery a CanExecute predicate
    /// that closes over another object's property.</summary>
    public void NotifyTransmitAvailabilityChanged() => ApplyAndTransmitCommand.NotifyCanExecuteChanged();

    /// <summary>Header transmit-progress display (2026-09-19 user request: "TX should be more
    /// obvious, [add it] as a bigger bar on the same line" as the context bar's own frame readout)
    /// -- bound directly by the context bar's AXAML in <c>TxImageEditorPaneView.axaml</c>.</summary>
    public bool IsTransmitting => _transmitStatusNow().IsTransmitting;

    /// <inheritdoc cref="IsTransmitting"/>
    public double? TransmitProgress => _transmitStatusNow().Progress;

    /// <inheritdoc cref="IsTransmitting"/>
    public string TransmitProgressText => _transmitStatusNow().ProgressText;

    /// <summary>Called by the parent at every one of its own IsTransmitting/TransmitProgress toggle
    /// points (<c>TransmitCoreAsync</c>'s start/finally, <c>OnTransmitProgressChanged</c>'s own
    /// per-tick update) -- same reasoning as <see cref="NotifyTransmitAvailabilityChanged"/>, but
    /// raising PropertyChanged for AXAML bindings instead of re-evaluating a command's CanExecute.</summary>
    public void NotifyTransmitProgressChanged()
    {
        OnPropertyChanged(nameof(IsTransmitting));
        OnPropertyChanged(nameof(TransmitProgress));
        OnPropertyChanged(nameof(TransmitProgressText));
    }

    /// <summary>Captures the inputs of <see cref="BuildFinalOutput"/> on the UI thread and returns a composer that
    /// renders the same frame through the fused <see cref="ITransmitImagePreparer.ComposePreview"/>, safe to run off
    /// the UI thread. Used by the DRM size estimate.</summary>
    public Func<IImageSource> CaptureFinalOutputComposer()
    {
        var preparer = _preparer;
        var source = _originalSource;
        var crop = CropRect;
        var (width, height) = (_target.Width, _target.Height);
        var preserveAspect = PreserveAspect;
        var adjustments = BuildAdjustments();
        var template = BuildTemplateDocument();
        return () => preparer.ComposePreview(source, crop, width, height, preserveAspect, adjustments, template);
    }

    /// <summary>Airtime for targets with no fixed frame time (DRM), shown in the header and SEND readouts.</summary>
    public void SetEstimatedFrameSeconds(double? seconds)
    {
        if (_estimatedFrameSeconds == seconds)
        {
            return;
        }

        _estimatedFrameSeconds = seconds;
        OnPropertyChanged(nameof(FrameReadoutText));
        OnPropertyChanged(nameof(SendMetaText));
    }

    private double? _estimatedFrameSeconds;

    private IImageSource BuildFinalOutput()
    {
        var cropped = _preparer.Crop(_originalSource, CropRect);
        var resized = _preparer.Resize(cropped, _target.Width, _target.Height, PreserveAspect);
        var adjusted = _preparer.ApplyAdjustments(resized, BuildAdjustments());
        return _preparer.ApplyTemplate(adjusted, BuildTemplateDocument());
    }

    /// <summary>Backlog item (auditor usability review, 2026-08-17): confirm before discarding
    /// unsaved edits. User-reported feedback (2026-09-15): the original arm/confirm shape (a second
    /// click on the SAME Cancel button) went stale the moment <see cref="ConfirmRequested"/>/
    /// <see cref="RequestConfirmAsync"/> were added for template recall -- migrated to the same real
    /// dialog rather than left as the one remaining "click twice" holdout in this class. A Cancel with
    /// nothing unsaved still cancels immediately (no confirmation needed for a no-op discard).
    /// <para>User-requested (2026-09-19): gated on <see cref="IsDirtySinceLastCheckpoint"/>, not
    /// <see cref="HasUnsavedEdits"/> -- discarding right after a template save/load must not confirm
    /// (nothing new would actually be lost), only once a real edit has happened SINCE that
    /// checkpoint. See that property's own doc comment.</para></summary>
    [RelayCommand]
    private async Task CancelAsync()
    {
        // Auditor finding: the confirm-dialog await must live inside a try -- ShowDialog itself can
        // throw (e.g. the owner window closing mid-await), same reasoning
        // OnReadyRackTemplateSelected's own identical try/catch already documents. An earlier draft
        // of this migration left it unguarded, the one call site among the three real-dialog
        // migrations this session shipped that didn't match the other two.
        try
        {
            if (IsDirtySinceLastCheckpoint)
            {
                var confirmed = await RequestConfirmAsync(
                    _localization.GetString("Panes.TxImageEditor.ConfirmCancelTitle"),
                    _localization.GetString("Panes.TxImageEditor.ConfirmCancelBody"),
                    _localization.GetString("Panes.TxImageEditor.ConfirmCancelButton"));
                if (_disposed || !confirmed)
                {
                    return;
                }
            }

            StatusMessage = null;
            Log.CancelInvoked(_logger);
            Cancelled?.Invoke();
        }
        catch (Exception ex)
        {
            Log.CancelConfirmFailed(_logger, ex);
            if (!_disposed)
            {
                StatusMessage = _localization.GetString("Panes.TxImageEditor.CancelFailed");
            }
        }
    }

    // T0-12 (production_audit.md): coalesces the 3 genuinely hot, per-pointer-move triggers (crop
    // drag via OnCropRectChanged, the 6 adjustment sliders' On*Changed hooks, and overlay-element
    // drag/resize via OnOverlayElementPropertyChanged's trailing call) into a single deferred
    // pipeline pass per UI-thread idle tick, instead of running the full synchronous
    // Crop->Resize->ApplyAdjustments->ApplyTemplate pipeline on every raw PointerMoved/slider-drag-
    // delta event. All 3 triggers are themselves only ever raised on the UI thread (Avalonia's own
    // routed pointer events / TwoWay Slider bindings), so unlike WaterfallPaneViewModel.OnFrame's
    // own coalescing (fed from a non-UI thread) this needs no lock -- a plain bool flag is safe.
    // RescanTemplateVariables stays eager/synchronous on every call (cheap -- a regex scan, not the
    // actual perf problem) so the fill-bar variable rows don't go stale mid-drag; only the expensive
    // pipeline pass is deferred and coalesced. RecomputePreview() itself is left untouched/synchronous
    // for the ~20 discrete one-shot call sites (button clicks, undo/redo, template load, Rotate) --
    // those already fire once per user action and deferring them would just add a frame of visible
    // lag for no benefit.
    private bool _recomputePreviewScheduled;

    internal static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Apply invoked: targetMode={TargetMode}")]
        public static partial void ApplyInvoked(ILogger logger, string targetMode);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Template '{TemplateId}' text picture-fill asset '{AssetFileName}' could not be loaded; loading that element without its picture fill")]
        public static partial void BitmapFillAssetLoadFailed(ILogger logger, string templateId, string assetFileName, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Apply & Transmit invoked: targetMode={TargetMode}")]
        public static partial void ApplyAndTransmitInvoked(ILogger logger, string targetMode);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Cancel invoked")]
        public static partial void CancelInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Cancel's own confirm dialog failed")]
        public static partial void CancelConfirmFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Rotate invoked")]
        public static partial void RotateInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "AddImageFromFile failed")]
        public static partial void AddImageFromFileFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "AddImagesFromDroppedFiles: failed to load {Path}")]
        public static partial void AddImageFromDroppedFileFailed(ILogger logger, string path, Exception exception);

        [LoggerMessage(Level = LogLevel.Debug, Message = "AddImagesFromDroppedFiles: drop contained no resolvable image files")]
        public static partial void DroppedFilesEmpty(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "AddImagesFromDroppedFiles: failed to read the drop payload")]
        public static partial void DroppedFilesUnreadable(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "AddImageFromClipboard failed")]
        public static partial void AddImageFromClipboardFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "AddImageFromClipboard: temp file cleanup failed: path={TempPath}")]
        public static partial void ClipboardTempFileCleanupFailed(ILogger logger, string tempPath, Exception exception);

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

        [LoggerMessage(Level = LogLevel.Debug, Message = "LoadTemplate({TemplateId}) discarded as stale -- a newer template selection superseded it")]
        public static partial void TemplateLoadDiscardedAsStale(ILogger logger, string templateId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "LoadTemplate({TemplateId}) succeeded, but the Save-name prefill lookup failed -- the canvas is loaded, only NewTemplateName is stale/unset")]
        public static partial void TemplateNamePrefillFailed(ILogger logger, string templateId, Exception exception);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Flatten element invoked: type={ElementType}")]
        public static partial void FlattenElementInvoked(ILogger logger, string elementType);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Flatten element failed")]
        public static partial void FlattenElementFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Flatten element result discarded as stale (editor state changed while baking)")]
        public static partial void FlattenElementDiscardedAsStale(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Demote backdrop to background failed")]
        public static partial void DemoteBackdropToBackgroundFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Demote backdrop to background result discarded as stale (editor state changed while baking)")]
        public static partial void DemoteBackdropToBackgroundDiscardedAsStale(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Reset image element to original size: natural={NaturalWidth}x{NaturalHeight} clampedToFrame={Clamped}")]
        public static partial void ResetImageElementToOriginalSize(ILogger logger, int naturalWidth, int naturalHeight, bool clamped);
    }
}
