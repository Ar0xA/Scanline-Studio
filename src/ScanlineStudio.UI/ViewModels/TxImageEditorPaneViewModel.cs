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
public sealed partial class TxImageEditorPaneViewModel : ViewModelBase, IDisposable
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
    /// all trailing/optional so pre-Phase-4 call sites/tests keep compiling unchanged.
    /// <paramref name="ShadowColor"/>/<paramref name="ShadowOffsetX"/>/<paramref name="ShadowOffsetY"/>/
    /// <paramref name="RotationDegrees"/>/<paramref name="GradientEnabled"/>/<paramref name="GradientKind"/>/
    /// <paramref name="GradientStartColor"/>/<paramref name="GradientEndColor"/> are Phase 8
    /// (YONIQ-style text-effects follow-up) additions, same trailing/optional treatment -- mirror
    /// <see cref="OverlayElementViewModel"/>'s own simplified 2-stop-gradient VM shape exactly (see
    /// that class's own doc comment for why the gradient isn't stored as a raw
    /// <see cref="TextGradient"/> here).
    /// <para>TX editor gap-items plan, item 4b (picture fill, 2026-09-01):
    /// <paramref name="BitmapFillEnabled"/>/<paramref name="BitmapFillSource"/> mirror
    /// <see cref="RawImageElementSnapshot.Source"/>'s own already-resolved-handle convention --
    /// carries the actual loaded <see cref="IImageSource"/>, not a path, so Undo/Copy-Paste-Style
    /// never re-decode it.</para></summary>
    public sealed record RawTextElementSnapshot(
        double X, double Y, double Width, double Height, int Z, bool Locked,
        string Text, double FontSizeRelative, Rgb24 Color,
        string FontFamily = "", Rgb24? StrokeColor = null, double StrokeThickness = 0.02,
        Rgb24? ShadowColor = null, double ShadowOffsetX = 0.02, double ShadowOffsetY = 0.02, double RotationDegrees = 0,
        bool GradientEnabled = false, TextGradientKind GradientKind = TextGradientKind.Horizontal,
        Rgb24? GradientStartColor = null, Rgb24? GradientEndColor = null,
        bool Bold = false, bool Italic = false,
        Rgb24? StackColor = null, double StackStepX = 0.02, double StackStepY = 0.02,
        bool BitmapFillEnabled = false, IImageSource? BitmapFillSource = null,
        // User-requested (2026-09-15) grow-to-fill toggle -- yoniq-auditor-flagged risk: without this
        // field, Undo/Redo silently reset EVERY text element's own toggle to off (ApplyState's own
        // recreate-every-element-from-a-snapshot path has nowhere else to read it from), a much wider
        // and more visible reset than OverlayElementViewModel.GrowToFillEnabled's own doc comment
        // ("resets on every template reload") implied. Deliberately NOT read by PasteSelectedElementStyle
        // (that method copies fields explicitly, one at a time -- this one is simply never among
        // them), so Copy Style/Paste Style still correctly does NOT carry it between elements, only
        // Undo/Redo/Duplicate/Copy-Paste (which reuse this same record) do.
        bool GrowToFillEnabled = false)
        : RawElementSnapshot(X, Y, Width, Height, Z, Locked);

    /// <summary><paramref name="GradientEnabled"/>/<paramref name="GradientKind"/>/
    /// <paramref name="GradientStartColor"/>/<paramref name="GradientEndColor"/> (TX editor gap-items
    /// plan, 2026-09-01) mirror <see cref="RawTextElementSnapshot"/>'s own identical fields exactly
    /// -- same trailing/optional treatment, same simplified 2-stop VM shape.
    /// <para><paramref name="PerspectiveEnabled"/>/<paramref name="Corner0X"/>..<paramref name="Corner3Y"/>
    /// (TX editor gap-items plan, item 3, 2026-09-02) -- without these, the FIRST Undo after enabling
    /// perspective on a box would silently revert the warp, since <c>ApplyState</c>'s own recreate-
    /// every-element-from-a-snapshot path has nowhere else to read the corners from. Same 9-flat-
    /// scalar convention as <see cref="PersistedBoxElement"/>'s own identical fields -- see that
    /// record's own doc comment for why (consistency with every other field here being flat, not a
    /// nested <c>PerspectiveCorners?</c>).</para></summary>
    public sealed record RawBoxElementSnapshot(
        double X, double Y, double Width, double Height, int Z, bool Locked,
        Rgb24 FillColor, Rgb24? BorderColor, double BorderThickness, double Opacity, double CornerRadius = 0,
        bool GradientEnabled = false, TextGradientKind GradientKind = TextGradientKind.Horizontal,
        Rgb24? GradientStartColor = null, Rgb24? GradientEndColor = null,
        bool PerspectiveEnabled = false,
        double Corner0X = 0, double Corner0Y = 0, double Corner1X = 0, double Corner1Y = 0,
        double Corner2X = 0, double Corner2Y = 0, double Corner3X = 0, double Corner3Y = 0,
        // Legacy `.mtm` import -- see TemplateBoxElement.FillEnabled's own doc comment.
        bool FillEnabled = true)
        : RawElementSnapshot(X, Y, Width, Height, Z, Locked);

    /// <summary>Line element (TX editor gap-items plan, 2026-09-01) -- base <paramref name="X"/>/
    /// <paramref name="Y"/>/<paramref name="Width"/>/<paramref name="Height"/> are derived-from-
    /// endpoints values written for uniformity with every other snapshot's own shape (e.g. code that
    /// lists every snapshot's bounds without knowing about lines specifically); <paramref name="X1"/>/
    /// <paramref name="Y1"/>/<paramref name="X2"/>/<paramref name="Y2"/> are the SOLE truth when
    /// reconstructing a live <see cref="LineElementViewModel"/> from one of these (same "endpoints
    /// are truth" rule <see cref="PersistedLineElement"/> states for its own base fields).</summary>
    public sealed record RawLineElementSnapshot(
        double X, double Y, double Width, double Height, int Z, bool Locked,
        double X1, double Y1, double X2, double Y2, Rgb24 StrokeColor, double StrokeThickness, double Opacity)
        : RawElementSnapshot(X, Y, Width, Height, Z, Locked);

    /// <summary>Which source an image element was resolved from, plus enough to re-resolve it later
    /// (spec/15-template-designer.md, plan-review finding) -- a resolved <see cref="IImageSource"/>
    /// alone can't tell Phase 5's persisted-template format whether to serialize a file reference or
    /// embed the pixels, and would foreclose ever re-resolving the "last RX image" case against a NEW
    /// picture on a future render (not built in Phase 2, but the origin field keeps that door open
    /// instead of silently designing it out).
    /// <see cref="Payload"/> is the file path for <see cref="ImageSourceKind.File"/>, the
    /// <see cref="ReceiveHistoryEntry.Id"/> for <see cref="ImageSourceKind.RxHistory"/>, and unused
    /// (null) for <see cref="ImageSourceKind.LastRx"/>/<see cref="ImageSourceKind.Clipboard"/>/
    /// <see cref="ImageSourceKind.Promoted"/> -- all three are inherently ephemeral, one-time
    /// snapshots with nothing stable to re-fetch later.
    /// <see cref="ImageSourceKind.Clipboard"/> (auditor usability review follow-up, 2026-08-18,
    /// Phase 2's own logged scope cut, picked back up) is the 4th source;
    /// <see cref="ImageSourceKind.Promoted"/> (background/backdrop naming work, 2026-09-15 --
    /// <see cref="PromoteBackgroundToBackdrop"/>) is the 5th.</summary>
    public enum ImageSourceKind { File, RxHistory, LastRx, Clipboard, Promoted }

    public sealed record ImageSourceOrigin(ImageSourceKind Kind, string? Payload);

    public sealed record RawImageElementSnapshot(
        double X, double Y, double Width, double Height, int Z, bool Locked,
        IImageSource Source, ImageFitMode Fit, ImageSourceOrigin Origin, bool IsBackground = false,
        // TX workflow modernization plan, Phase 7 -- trailing, defaulted (0 = unknown, same
        // convention IsBackground itself established), so ApplyState's own recreate-every-element-
        // from-a-snapshot path doesn't lose this field on the first Undo.
        int NaturalPixelWidth = 0, int NaturalPixelHeight = 0,
        // TX editor gap-items plan, item 3 (perspective transform, 2026-09-02) -- same 9-flat-scalar
        // convention as RawBoxElementSnapshot's own Perspective fields; see that record's own doc
        // comment.
        bool PerspectiveEnabled = false,
        double Corner0X = 0, double Corner0Y = 0, double Corner1X = 0, double Corner1Y = 0,
        double Corner2X = 0, double Corner2Y = 0, double Corner3X = 0, double Corner3Y = 0)
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
        IReadOnlyDictionary<string, string> TemplateVariables,
        // TX workflow modernization plan, Phase 7 -- see _sourceBaseline's own doc comment for why
        // this is the baseline generation, not the live _originalSource.
        IImageSource SourceBaseline, int SourceBaselineRotation);

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
    private readonly SstvModeDefinition _targetMode;
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
    /// <summary>Macros help plan (2026-09-01), item B -- opens the same Macros reference window
    /// `Tools ▸ Macros` does, reached via <see cref="OpenMacrosReferenceCommand"/>. Set only from
    /// TxControlsPaneViewModel's real construction call sites; test call sites leave this null, which
    /// <see cref="OpenMacrosReference"/> silently no-ops on -- same "unwired = harmless no-op"
    /// convention as this codebase's other cross-VM request delegates.</summary>
    private readonly Action? _macrosReferenceRequested;
    /// <summary>Ready Rack direct-fire plan (2026-09-01) -- invoked FRESH at each direct-fire (unlike
    /// the constructor's own one-shot <c>currentContactVariables</c> snapshot), so
    /// <see cref="OnReadyRackDirectFireRequested"/> can re-seed <c>his_call</c>/<c>his_grid</c> from
    /// whichever station is CURRENTLY being worked, not whichever was being worked when this editor
    /// was first opened. Null on any route that doesn't already pass a real
    /// <c>currentContactVariables</c> value either (thread caller intent -- see the constructor's own
    /// parameter doc comment for why).</summary>
    private readonly Func<IReadOnlyDictionary<string, string>?>? _currentContactProvider;
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

    private readonly List<EditorSnapshot> _undoStack = [];

    /// <summary>Templates rack rework, yoniq-auditor finding -- <see cref="_undoStack"/>'s own count
    /// snapshot taken right after the most recent successful template load (or 0, for an editor that
    /// has never loaded one). <see cref="LoadTemplateIntoLiveEditor"/> pushes ITS OWN undo snapshot
    /// (so the load itself is undoable, a deliberate existing design decision -- see that method's
    /// own doc comment), which means <see cref="HasUnsavedEdits"/> goes permanently true the instant
    /// ANY template is first loaded, with zero operator edits. Reusing that raw flag for the
    /// Templates rack's own "Loaded • edited" badge and its discard-confirm gate made both wrong: the
    /// badge read "edited" from the moment of load, and the modal fired on every SUBSEQUENT load in
    /// the same session even with nothing actually typed since. <see cref="IsDirtySinceLastTemplateLoad"/>
    /// below measures relative to THIS baseline instead -- true only once the stack grows (or
    /// shrinks via Undo) past what it was right after that load.</summary>
    private int _undoStackDepthAtLastTemplateLoad;
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

    /// <summary>TX editor gap-items plan, item 2 -- the ONE place that atomically replaces the whole
    /// selection (single element, empty, or a real 2+ group). Every raw
    /// <c>SelectedOverlayElement = x</c> assignment elsewhere in this file (unchanged, still valid)
    /// relies on <see cref="OnSelectedOverlayElementChanged"/>'s own self-healing collapse instead --
    /// this method exists for the cases that self-healing CAN'T cover: building/shrinking an actual
    /// group, and re-selecting an element that's ALREADY the primary (a value-EQUAL assignment
    /// never fires the hook at all, so re-clicking the primary's own sidebar row while it's a
    /// group's primary needs this explicit path to force the collapse -- see
    /// <see cref="OnElementRowPointerPressed"/>).</summary>
    public void SetSelection(IEnumerable<ITemplateElementViewModel> elements)
    {
        // ToList() FIRST, unconditionally -- a caller (ToggleElementSelection's removal branch, the
        // RemoveOverlayElement/FlattenElementAsync shrink) may pass a deferred Where() over
        // SelectedOverlayElements itself, which this method then clears; evaluating lazily would
        // observe its own mutation mid-enumeration.
        var newSet = elements.ToList();
        var previous = _updatingSelectionSet;
        _updatingSelectionSet = true;
        try
        {
            SelectedOverlayElements.Clear();
            foreach (var element in newSet)
            {
                SelectedOverlayElements.Add(element);
            }

            SelectedOverlayElement = newSet.Count > 0 ? newSet[^1] : null;
        }
        finally
        {
            _updatingSelectionSet = previous;
        }

        // The assignment above fires OnSelectedOverlayElementChanged, which already re-syncs
        // IsSelected against SelectedOverlayElements (populated before that assignment ran) for
        // every REAL change -- but CommunityToolkit's generated setter skips the hook entirely on a
        // value-EQUAL assignment (e.g. newSet's last element already WAS the primary, the routine
        // case for a group shrink that doesn't touch the primary). Re-run unconditionally here so
        // IsSelected always ends up correct regardless of whether the hook fired.
        foreach (var element in OverlayElements)
        {
            element.IsSelected = SelectedOverlayElements.Contains(element);
        }
    }

    /// <summary>TX editor gap-items plan, item 2 -- shift-click's own selection-toggle gesture:
    /// adds <paramref name="element"/> to the multi-selection set if absent, removes it if present.</summary>
    public void ToggleElementSelection(ITemplateElementViewModel element)
    {
        var current = SelectedOverlayElements.ToList();
        SetSelection(current.Contains(element) ? current.Where(e => !ReferenceEquals(e, element)) : current.Append(element));
    }

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

    /// <summary>Ready Rack direct-fire plan (2026-09-01): a SEPARATE arm/confirm token from plain
    /// recall's own (now <see cref="ConfirmRequested"/>'s real dialog, formerly a status-bar arm) --
    /// code-review finding on an earlier draft of this feature: sharing one token would let a
    /// plain-recall's own "will discard your edits" arm double as an unintended "yes, transmit"
    /// confirmation for a LATER Ctrl+N on the same slot, since the operator would only ever have
    /// read a discard warning, never a transmit one. Cleared at every real-edit point
    /// (<see cref="PushUndoSnapshot"/>, <see cref="PushUndoSnapshotCoalesced"/>,
    /// <see cref="ApplyState"/>) and its own consume-on-fire point in
    /// <see cref="OnReadyRackDirectFireRequested"/> -- same "any real edit disarms a stale
    /// confirmation" rule plain recall's own dialog-gated flow follows.</summary>
    private string? _pendingDirectFireTemplateId;

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
        // Ready Rack direct-fire plan (2026-09-01): same trailing-optional/closure shape as
        // macrosReferenceRequested above, same reasoning -- only routes that ALREADY pass a real
        // (non-null) currentContactVariables above pass a real delegate here too (thread caller
        // intent: TxControlsPaneViewModel.OpenEditorForExternalFileAsync's own "unseeded means
        // unseeded" contract for a Gallery-sourced editor with no linked QSO must not be reversed by
        // this new parameter). Invoked fresh at each direct-fire, unlike currentContactVariables
        // above (a one-shot constructor snapshot) -- see OnReadyRackDirectFireRequested's own doc
        // comment for why a live re-read is required, not a snapshot.
        Func<IReadOnlyDictionary<string, string>?>? currentContactProvider = null)
    {
        _originalSource = originalSource;
        _sourceBaseline = originalSource;
        _sourceBaselineRotation = 0;
        _targetMode = targetMode;
        _preparer = preparer;
        _macroTextResolver = macroTextResolver;
        _canTransmitNow = canTransmitNow ?? (static () => true);
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
        _imageSourceWriter = imageSourceWriter;
        ReadyRack = readyRack;
        ReadyRack.TemplateSelected += OnReadyRackTemplateSelected;
        ReadyRack.TemplateDirectFireRequested += OnReadyRackDirectFireRequested;
        // User-reported bug (2026-09-15): after Remove Background, Browse/Stock/Open Editor in the
        // sidebar stayed greyed out -- see HasNoBackgroundOrOverlayElements's own doc comment. Every
        // element add/remove (AddOverlayElement, RemoveOverlayElement, FlattenElementAsync,
        // DemoteBackdropToBackgroundAsync, ApplyState's wholesale Undo/Redo restore, and every other
        // OverlayElements mutation) funnels through this ONE collection, so a single subscription
        // here covers all of them instead of patching each call site individually.
        OverlayElements.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoBackgroundOrOverlayElements));

        _workingCopy = BuildWorkingCopy(originalSource, targetMode, preparer);
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
    }

    // T0-11 follow-up (production_audit.md): releases _workingCopyPool/_previewPool and every
    // live OverlayElements entry's own bitmap resources. Wired into all 6 of
    // TxControlsPaneViewModel's own editor-discard sites (CloseBlankEditorForReplacement,
    // OnEditorCancelled, OnEditorApplied, and the 3 construction-failure catches) via a uniform
    // "_currentEditor?.Dispose(); _currentEditor = null;" -- see that class's own comments at each
    // site. Deliberately does NOT unsubscribe this instance's own Applied/Cancelled/
    // DirectFireRequested/PropertyChanged handlers -- unchanged, already-documented precedent
    // (CloseBlankEditorForReplacement's own doc comment): nothing will ever invoke them again once
    // the caller swaps to a different editor instance, disposed or not.
    //
    // MUST BE CALLED ON THE UI THREAD. Every wired call site in TxControlsPaneViewModel reaches this
    // on the UI thread today -- the 3 normal-close sites (CloseBlankEditorForReplacement,
    // OnEditorCancelled, OnEditorApplied) are synchronous CommunityToolkit [RelayCommand]/event-
    // handler methods, and the 3 construction-failure catches are reached via `await`s with no
    // ConfigureAwait(false) anywhere in their chains, so Avalonia's captured SynchronizationContext
    // resumes them on the UI thread too (NOT because those catches contain no background dispatch --
    // auditor code-review correction, Tier-0 audit follow-up).
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
    /// confirm dialog (<see cref="ConfirmRequested"/>) when <see cref="IsDirtySinceLastTemplateLoad"/>,
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
            if (IsDirtySinceLastTemplateLoad)
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
                    // baseline, since the load itself grew the undo stack -- update the baseline and
                    // re-push the NOW-correct (false) dirty state BEFORE SetLoadedTemplate, so its own
                    // ApplyLoadedState computes IsLoadedAndEdited from the right value instead of the
                    // stale "dirty" one Push left behind. Doing it in the opposite order (an earlier
                    // draft's bug, caught by LoadTemplate_CleanEditor_FirstLoad_DoesNotAskAndBadgeIsPlainLoaded)
                    // left a freshly-loaded, unedited slot showing "Loaded • edited".
                    _undoStackDepthAtLastTemplateLoad = _undoStack.Count;
                    ReadyRack.SetCanvasDirty(IsDirtySinceLastTemplateLoad);
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

    /// <summary>Ready Rack direct-fire plan (2026-09-01): Ctrl+number's own handler -- load + re-seed
    /// + bake + fire, all in one keystroke. Own arm/confirm token (<see cref="_pendingDirectFireTemplateId"/>),
    /// separate from plain recall's own dialog-gated flow in <see cref="OnReadyRackTemplateSelected"/> --
    /// code-review finding on an earlier draft: sharing one token would let a plain-recall's own
    /// discard-only warning double as an unintended transmit confirmation for a later Ctrl+N on the
    /// same slot.
    ///
    /// Order matters, each step closes a real reviewed-and-found gap:
    /// 1. The blank-photo refusal runs FIRST, before touching the arm token or starting a load --
    ///    <see cref="LoadTemplateIntoLiveEditor"/> clears <see cref="OverlayElements"/> unconditionally,
    ///    so checking AFTER the load would wipe the operator's current overlay before refusing.
    ///    Checks <see cref="_sourceBaseline"/>, NOT <see cref="_originalSource"/> (code-review
    ///    finding on an earlier draft: <see cref="_originalSource"/> is NOT rotate-invariant --
    ///    <see cref="RotateImageOnly"/> reassigns it to a real decoded image even when the underlying
    ///    photo is still the placeholder, so blank-editor -&gt; Rotate -&gt; Ctrl+N would have bypassed
    ///    this refusal and sent overlay text on a gray card). <see cref="_sourceBaseline"/> is exactly
    ///    load-invariant AND rotate-invariant: untouched by <see cref="RotateImageOnly"/>, updated
    ///    only on a real flatten, restored on undo/redo -- so this refuses through any number of
    ///    rotates, allows once the operator actually flattens a real photo in, and correctly refuses
    ///    again if that flatten is undone.
    /// 2. Arm/confirm on <see cref="_pendingDirectFireTemplateId"/>, its OWN loc key that names both
    ///    halves ("will discard your edits AND transmit") -- never plain recall's own discard-only
    ///    dialog text (<see cref="OnReadyRackTemplateSelected"/>'s <c>ConfirmDiscardBody</c>).
    /// 3. <see cref="LoadTemplateAsync"/> now returns <see langword="false"/> on a lost
    ///    <see cref="_templateLoadGeneration"/> race -- abandoned silently (no error shown; a
    ///    superseded fire during rapid slot-switching is expected pileup behavior, not a failure),
    ///    never transmitted against the losing slot's stale canvas.
    /// 4. The <see cref="_currentContactProvider"/> re-seed is invoked FRESH here (not the
    ///    constructor's one-shot snapshot) -- overwrites <c>his_call</c>/<c>his_grid</c> in
    ///    <see cref="_templateVariables"/> ONLY for keys the fresh result actually has a value for
    ///    (an empty/null RX contact leaves whatever was already typed alone, same "never silently
    ///    blank a field" rule the constructor seed follows). The RX contact bar
    ///    (<c>OverrideCallsign</c>/<c>LookupGrid</c>) is the source of truth for who's being worked;
    ///    this stamps the card with it. A mis-decoded callsign gets corrected THERE, not in this fill
    ///    bar.
    /// 5. <see cref="CanApplyAndTransmit"/> is checked EXPLICITLY here, before baking -- a raw
    ///    <c>.Execute(null)</c>-shaped bypass would otherwise bake and (via <see cref="OnEditorDirectFire"/>)
    ///    close the editor BEFORE the downstream <c>TransmitCommand.CanExecute</c> re-check silently
    ///    swallows a busy fire. Refused visibly (<see cref="StatusMessage"/>), never a silent drop.
    /// 6. Does NOT call the existing <see cref="ApplyAndTransmitCommand"/>/<see cref="ApplyAndTransmit"/>
    ///    -- bakes directly and raises <see cref="DirectFireRequested"/> instead, so
    ///    <see cref="TxControlsPaneViewModel"/> can tell a direct-fire-triggered fire apart from an
    ///    ordinary manual one and chain the post-fire reopen only for this path.</summary>
    private async void OnReadyRackDirectFireRequested(string templateId)
    {
        if (_disposed) return;
        if (_sourceBaseline is BlankImageSource)
        {
            StatusMessage = _localization.GetString("Panes.TxImageEditor.DirectFireNoPhoto");
            return;
        }

        if (HasUnsavedEdits && _pendingDirectFireTemplateId != templateId)
        {
            _pendingDirectFireTemplateId = templateId;
            StatusMessage = _localization.GetString("Panes.TxImageEditor.ConfirmDirectFireOverwrite");
            return;
        }

        _pendingDirectFireTemplateId = null;
        StatusMessage = null;
        var generation = ++_templateLoadGeneration;
        bool loaded;
        try
        {
            loaded = await LoadTemplateAsync(templateId, generation);
        }
        catch (Exception ex)
        {
            Log.LoadTemplateFailed(_logger, templateId, ex);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.LoadTemplateFailed");
            return;
        }

        if (!loaded || _disposed || generation != _templateLoadGeneration)
        {
            // Superseded by a newer selection while loading -- expected pileup behavior, not a
            // failure. That newer selection will apply/fire its own result; this one is abandoned.
            return;
        }

        if (_currentContactProvider?.Invoke() is { } freshContact)
        {
            foreach (var (key, value) in freshContact)
            {
                _templateVariables[key] = value;
            }
        }

        if (!CanApplyAndTransmit())
        {
            StatusMessage = _localization.GetString("Panes.TxImageEditor.DirectFireBusy");
            return;
        }

        Log.DirectFireInvoked(_logger, _targetMode.Id);
        if (_disposed || generation != _templateLoadGeneration) return;
        var final = BuildFinalOutput();
        if (_disposed || generation != _templateLoadGeneration) return;
        DirectFireRequested?.Invoke(final);
    }

    public ObservableCollection<ITemplateElementViewModel> OverlayElements { get; } = [];

    /// <summary>Phase 3 (spec/15-template-designer.md) fill bar -- one row per template variable
    /// name currently referenced by a live text element, kept in sync by
    /// <see cref="RescanTemplateVariables"/> (called from <see cref="RecomputePreview"/>, so it runs
    /// on every mutation that could add/remove a reference). ROW visibility only -- see
    /// <see cref="_templateVariables"/>'s own doc comment for why the underlying VALUE is tracked
    /// separately and survives a row's temporary disappearance.</summary>
    public ObservableCollection<TemplateVariableRowViewModel> TemplateVariableRows { get; } = [];

    /// <summary>EditWindow redesign Phase 4 (mockups/Editwindow) -- the bottom QSO FILL bar's own
    /// "no fields yet" placeholder. A PLAIN computed property (not <c>[ObservableProperty]</c>,
    /// since there's no backing field to observe) needs an explicit <see cref="OnPropertyChanged"/>
    /// raise wherever <see cref="TemplateVariableRows"/> actually changes -- <see
    /// cref="RescanTemplateVariables"/> is the one place that adds/removes rows, so it's the one
    /// place this needs raising. Not bound as a bare <c>!TemplateVariableRows.Count</c> path in AXAML
    /// -- Avalonia's binding NOT-operator is for booleans, not a safe implicit int-to-bool
    /// conversion, so a real bool property is the correct fix here, not a shortcut.</summary>
    public bool HasNoTemplateVariableRows => TemplateVariableRows.Count == 0;

    /// <summary>Design-fidelity Phase F (#9, mockups/Editwindow line 239) -- QSO FILL header's
    /// compact token-count note. Same re-raise-alongside-<see cref="HasNoTemplateVariableRows"/>
    /// discipline (both derive from <see cref="TemplateVariableRows"/>.Count, which has no
    /// property-changed notification of its own).</summary>
    public string TemplateVariableCountText => _localization.GetString(
        "Panes.TxImageEditor.TemplateVariableCountFormat", TemplateVariableRows.Count);

    /// <summary>Design-fidelity Phase F (#9, mockups/Editwindow line 239) -- SEND side's compact
    /// mode/duration meta line. The mock's own version also shows SWR, which this VM has no access
    /// to (a TX-hardware meter reading that lives on <c>TxControlsPaneViewModel</c>, cross-VM data
    /// this pass doesn't wire up) -- a real, deliberate scope cut, not silently dropped. Mode name
    /// uppercased for DISPLAY only (Phase I, final confirming pass nit) -- SSTV mode display names
    /// are fixed Latin protocol abbreviations, never user data, so <c>ToUpperInvariant</c> is a safe
    /// display-side transform, not a rename of <see cref="SstvModeDefinition.DisplayName"/> itself.</summary>
    public string SendMetaText => _localization.GetString(
        "Panes.TxImageEditor.SendMetaFormat", _targetMode.DisplayName.ToUpperInvariant(), TxControlsPaneViewModel.GetFrameSeconds(_targetMode));

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

    /// <summary>Design-fidelity Phase H (final confirming pass finding #6) -- the PREVIEW panel's
    /// own MaxHeight, bound to the real target mode height rather than a hardcoded 200 (which
    /// downscaled every 256-line mode's "mode-exact" preview to 78%, defeating its own point --
    /// the mock renders this panel at literal 1:1). Same frozen-for-the-editor's-lifetime reasoning
    /// as <see cref="HeaderText"/> -- a plain computed property, not observable.</summary>
    public double PreviewMaxHeight => _targetMode.ImageHeight;

    /// <summary>Same real-dimensions fix as <see cref="HeaderText"/>, for the separate dimensions
    /// chip lower in the tool strip (mock2's own layout keeps both -- the chip is a compact
    /// at-a-glance readout next to the aspect/text/apply controls, not a duplicate of the header).</summary>
    public string DimensionsChipText => _localization.GetString(
        "Panes.TxImageEditor.DimensionsChipFormat", _targetMode.ImageWidth, _targetMode.ImageHeight);

    /// <summary>EditWindow redesign, design-fidelity Phase B (mockups/Editwindow) -- the new context
    /// bar's mono frame readout ("OUTGOING FRAME 320×256 · MARTIN M1 · 114.3 s"). Duration reuses
    /// <see cref="TxControlsPaneViewModel.GetFrameSeconds"/>, not a new computation, just applied to
    /// THIS editor's own fixed <see cref="_targetMode"/> -- a naive <c>LineDurationMs * ImageHeight
    /// / 1000.0</c> here would double-count for <see cref="ColorEncoding.YCbCrLinePaired"/>/
    /// <see cref="ColorEncoding.MonoAveragedPaired"/> modes (the exact PD90-family bug that method's
    /// own doc comment documents fixing on the TX Controls card; this editor was left using the
    /// naive formula when that fix landed, a real bug caught and fixed later the same day).</summary>
    public string FrameReadoutText => _localization.GetString(
        "Panes.TxImageEditor.FrameReadoutFormat",
        _targetMode.ImageWidth, _targetMode.ImageHeight, _targetMode.DisplayName.ToUpperInvariant(),
        TxControlsPaneViewModel.GetFrameSeconds(_targetMode));

    /// <summary>EditWindow redesign, design-fidelity Phase B -- backs the context bar's "UNSAVED
    /// EDITS" chip. A free proxy over the EXISTING undo stack (no new dirty-tracking mechanism):
    /// true the instant any edit has been pushed, false once undone back to the editor's opened (or
    /// last-Applied) state. Raised at the same 4 call sites <see cref="UndoCommand"/>'s own
    /// <c>NotifyCanExecuteChanged</c> already fires from -- both conditions flip on exactly the same
    /// <c>_undoStack.Count &gt; 0</c> transition, so they're always in lockstep.</summary>
    public bool HasUnsavedEdits => _undoStack.Count > 0;

    /// <summary>Templates rack rework -- see <see cref="_undoStackDepthAtLastTemplateLoad"/>'s own
    /// doc comment for why this is a SEPARATE signal from <see cref="HasUnsavedEdits"/>, not a
    /// reuse. Used ONLY by the Templates rack (the discard-confirm gate in
    /// <see cref="OnReadyRackTemplateSelected"/> and the "Loaded • edited" badge via
    /// <see cref="ReadyRackViewModel.SetCanvasDirty"/>) -- Cancel/Revert/the "UNSAVED EDITS" chip
    /// keep using <see cref="HasUnsavedEdits"/> unchanged, since THEIR question really is "is there
    /// anything at all to lose," not "since the last template load specifically."</summary>
    private bool IsDirtySinceLastTemplateLoad => _undoStack.Count != _undoStackDepthAtLastTemplateLoad;

    /// <summary>Design-fidelity Phase C (mockups/Editwindow line 130) -- canvas footer, bottom-left:
    /// crop dims (always the target mode's own fixed render size, same numbers as
    /// <see cref="DimensionsChipText"/>). The "LOCKED TO MODE" segment is a separate,
    /// <see cref="LockAspectToMode"/>-gated chip in the View, not baked into this string, so its
    /// IsVisible binding stays a plain bool instead of parsing this text.</summary>
    public string CropFooterText => _localization.GetString(
        "Panes.TxImageEditor.CropFooterFormat", _targetMode.ImageWidth, _targetMode.ImageHeight);

    /// <summary>Design-fidelity Phase C (mockups/Editwindow line 131) -- canvas footer, bottom-right:
    /// the live working-copy pixel size (which changes with zoom-independent factors like rotation)
    /// alongside the fixed render target size.</summary>
    public string WorkingCopyFooterText => _localization.GetString(
        "Panes.TxImageEditor.WorkingCopyFooterFormat", WorkingCopyWidth, WorkingCopyHeight, _targetMode.ImageWidth, _targetMode.ImageHeight);

    /// <summary>Design-fidelity Phase E (#15, mockups/Editwindow line 108) -- a small mono readout
    /// above the selected element on the interactive canvas: type/z-order + pixel geometry, plus
    /// rotation for text elements only (the only kind that has any). All-existing properties
    /// (LeftPixels/TopPixels/CanvasWidthPixels/CanvasHeightPixels/Z/RotationDegrees), reused rather
    /// than duplicated. Raised on selection change (<see cref="OnSelectedOverlayElementChanged"/>)
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

            return element is OverlayElementViewModel text
                ? _localization.GetString(
                    "Panes.TxImageEditor.SelectionReadoutWithRotationFormat",
                    typeLabel, element.Z, (int)Math.Round(element.LeftPixels), (int)Math.Round(element.TopPixels),
                    (int)Math.Round(element.CanvasWidthPixels), (int)Math.Round(element.CanvasHeightPixels), (int)Math.Round(text.RotationDegrees))
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

    /// <summary>Ready Rack direct-fire plan (2026-09-01), code-review finding: lets
    /// <see cref="TxControlsPaneViewModel.OnEditorDirectFire"/> INHERIT this editor's own live-
    /// contact intent for the post-fire reopen, rather than hardcoding a live provider unconditionally
    /// on every reopen. Without this, a Gallery-sourced editor (constructed with a
    /// <see langword="null"/> provider specifically so the live RX contact never leaks onto an
    /// unrelated source) would start leaking it anyway from fire #2 onward, the moment the reopen ran
    /// -- the exact class of bug the constructor-time <c>null</c> exists to prevent, just delayed one
    /// fire.</summary>
    public Func<IReadOnlyDictionary<string, string>?>? CurrentContactProvider => _currentContactProvider;

    /// <summary>Pixel-space dimensions of the interactive canvas's background image -- the View
    /// binds crop-handle/overlay-element positions directly to these (rather than a converter doing
    /// normalized-to-pixel math in XAML), keeping the View a plain binding consumer.</summary>
    public double WorkingCopyWidth => _workingCopy.Width;

    public double WorkingCopyHeight => _workingCopy.Height;

    /// <summary>Rearchitected Phase 7 (see <see cref="ZoomFactor"/>'s own doc comment for why): the
    /// actual on-screen size of <c>EditorCanvas</c>/the overlay <c>ItemsControl</c>, bound directly in
    /// the View instead of <see cref="WorkingCopyWidth"/>/<see cref="WorkingCopyHeight"/> -- this IS
    /// zoom, expressed as real pixels, so ordinary Avalonia layout/arrange handles it exactly like any
    /// other bound size with no transform-subtree involved.</summary>
    public double CanvasDisplayWidth => WorkingCopyWidth * ZoomFactor;

    public double CanvasDisplayHeight => WorkingCopyHeight * ZoomFactor;

    /// <summary>The safe-area inset in WORKING-COPY (unzoomed) units -- shared by
    /// <see cref="SafeAreaInsetPixels"/> (display) and <see cref="ApplyFitSafeArea"/> (which needs
    /// the zoom-INDEPENDENT inset to avoid a circular "zoom depends on safe-area size which depends
    /// on zoom" dependency).</summary>
    private const double SafeAreaInsetWorkingCopyUnits = 14;

    /// <summary>Safe-area guide inset (<c>TxImageEditorPaneView.axaml</c>'s dashed <c>Rectangle</c>),
    /// zoom-scaled so it still marks the same IMAGE-relative region at any zoom -- 14 was previously a
    /// literal XAML <c>Margin="14"</c> (screen px, correct only at the zoom that didn't exist yet).</summary>
    public double SafeAreaInsetPixels => SafeAreaInsetWorkingCopyUnits * ZoomFactor;

    /// <summary>Backlog fix (user real-window finding, 2026-08-17): the safe-area guide's own
    /// Width/Height, for Canvas.Left/Top+Width/Height positioning DIRECTLY INSIDE EditorCanvas --
    /// the same pixel-space convention already proven reliable for the crop rect/overlay elements/
    /// selection badge elsewhere on this canvas, rather than the Margin-on-a-Stretch-child-of-a-
    /// bare-Panel approach the guide used before. That approach measured/arranged unreliably (empty
    /// ScrollViewer-viewport-driven Panel sizing produced a near-zero-size guide pinned near the
    /// Panel's own top-left corner instead of an inset box around the actual photo -- confirmed via
    /// two rounds of real-window screenshot comparison, not assumed). <see cref="Math.Max(double,
    /// double)"/> floors at 0 -- a real, reachable case, not just theoretical: the working-copy
    /// downsample budget is keyed off the target MODE's own dimensions
    /// (<c>mode.ImageWidth/Height * WorkingCopyScaleFactor</c>), so a small mode's canvas can be
    /// smaller than 2x the 14px-times-zoom inset regardless of source image size or zoom level
    /// (confirmed via <c>SafeAreaWidthAndHeightPixels_NeverGoNegativeWhenInsetExceedsCanvasSize</c>,
    /// using this codebase's own SmallMode test fixture). Without the floor this would be a real
    /// negative-size Avalonia layout exception, not just a cosmetic bug.</summary>
    public double SafeAreaWidthPixels => Math.Max(0, CanvasDisplayWidth - (2 * SafeAreaInsetPixels));

    public double SafeAreaHeightPixels => Math.Max(0, CanvasDisplayHeight - (2 * SafeAreaInsetPixels));

    /// <summary>Task #23 (zoom slider addendum) -- the pre-existing "Fit"/"100%" button labels are
    /// always shown as a percentage, never a raw factor; this is the same convention for the new
    /// slider's own numeric readout. A plain VM property (not a XAML <c>StringFormat</c>) since the
    /// display value needs a real multiply (<c>* 100</c>), not just number formatting.</summary>
    public string ZoomPercentText => $"{ZoomFactor * 100:0}%";

    partial void OnZoomFactorChanged(double value)
    {
        OnPropertyChanged(nameof(CanvasDisplayWidth));
        OnPropertyChanged(nameof(CanvasDisplayHeight));
        OnPropertyChanged(nameof(SafeAreaInsetPixels));
        OnPropertyChanged(nameof(SafeAreaWidthPixels));
        OnPropertyChanged(nameof(SafeAreaHeightPixels));
        OnPropertyChanged(nameof(ZoomPercentText));
        OnPropertyChanged(nameof(CropLeftPixels));
        OnPropertyChanged(nameof(CropTopPixels));
        OnPropertyChanged(nameof(CropWidthPixels));
        OnPropertyChanged(nameof(CropHeightPixels));
        OnPropertyChanged(nameof(CropRightPixels));
        OnPropertyChanged(nameof(CropBottomPixels));
        // Follow-up visual-polish pass: these 6 also derive from CanvasDisplayWidth/Height, same as
        // every other pixel-space property re-raised above -- in practice they're almost always null
        // here (a working-copy/zoom change mid-drag is an edge case, not the normal path), but
        // include them anyway rather than leave a one-frame stale window if it ever does happen.
        OnPropertyChanged(nameof(PlacementPreviewLeftPixels));
        OnPropertyChanged(nameof(PlacementPreviewTopPixels));
        OnPropertyChanged(nameof(PlacementPreviewWidthPixels));
        OnPropertyChanged(nameof(PlacementPreviewHeightPixels));
        // Line counterpart to the 4 PlacementPreview*Pixels raises above -- same "derives from
        // CanvasDisplayWidth/Height, no notification of its own" reasoning.
        OnPropertyChanged(nameof(PlacementPreviewLineStart));
        OnPropertyChanged(nameof(PlacementPreviewLineEnd));
        OnPropertyChanged(nameof(GuideLineXPixels));
        OnPropertyChanged(nameof(GuideLineYPixels));
        RefreshOverlayElementZoomedImageSize();
        RefreshOverlayElementCanvasStyles();
    }

    /// <summary>Pushes zoom-premultiplied <see cref="ImageWidth"/>/<see cref="ImageHeight"/> onto
    /// every element (parent-pushed, same pattern as <see cref="RefreshOverlayElementCanvasStyles"/>)
    /// -- reuses each element's OWN existing <c>OnImageWidthChanged</c>/<c>OnImageHeightChanged</c>
    /// hooks (already wired to re-raise <c>LeftPixels</c>/<c>TopPixels</c>/<c>CanvasWidthPixels</c>/
    /// <c>CanvasHeightPixels</c>) to get every element's on-screen position/size correctly zoomed with
    /// zero new notification plumbing -- see <see cref="ZoomFactor"/>'s own doc comment for why this
    /// replaced a render-transform approach.</summary>
    private void RefreshOverlayElementZoomedImageSize()
    {
        foreach (var element in OverlayElements)
        {
            element.ImageWidth = CanvasDisplayWidth;
            element.ImageHeight = CanvasDisplayHeight;
        }
    }

    /// <summary>One-shot "100%" action (Phase 7) -- not a live-tracking toggle, matches the plain
    /// Button shape the stub already had. Clamped (task #23 plan-review nit) for consistency with
    /// <see cref="ApplyFit"/>/<see cref="ZoomBy"/>'s own clamping even though 1.0 is always inside
    /// <see cref="MinZoomFactor"/>/<see cref="MaxZoomFactor"/> today -- a no-op clamp now, but keeps
    /// every <see cref="ZoomFactor"/> writer on the same discipline rather than three of four.</summary>
    [RelayCommand]
    private void ZoomActual() => ZoomFactor = Math.Clamp(1.0, MinZoomFactor, MaxZoomFactor);

    /// <summary>Task #23 (zoom slider addendum) -- multiplicative step, the third
    /// <see cref="ZoomFactor"/> writer alongside <see cref="ApplyFit"/>/<see cref="ZoomActual"/>, same
    /// clamp-in-one-place discipline. Multiplicative (not additive) since <see cref="MinZoomFactor"/>/
    /// <see cref="MaxZoomFactor"/> span a 40x range (0.1-4.0) -- a fixed additive step would feel
    /// enormous near the low end and imperceptible near the high end. Guards non-finite/non-positive
    /// <paramref name="factor"/> (a scroll-wheel handler computing <c>Math.Pow(1.1, delta)</c> can't
    /// itself produce one, but this is a public VM method, not a private implementation detail paired
    /// 1:1 with that one caller).</summary>
    public void ZoomBy(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            return;
        }

        ZoomFactor = Math.Clamp(ZoomFactor * factor, MinZoomFactor, MaxZoomFactor);
    }

    /// <summary>One-shot "Fit" action (Phase 7) -- called by the View's code-behind, which owns the
    /// actual viewport size (<c>ScrollViewer.Bounds</c>); this VM never reaches for control sizes
    /// directly. Computed once per call (on editor load and on each Fit button press), never
    /// live-tracked against a resize hook -- a <c>SizeChanged</c>-driven recompute would fight the
    /// ScrollViewer's own Auto scrollbars (zoom changes content size, which changes scrollbar
    /// visibility, which changes the viewport, which would change Fit's own computed value again).</summary>
    public void ApplyFit(double viewportWidth, double viewportHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 || WorkingCopyWidth <= 0 || WorkingCopyHeight <= 0)
        {
            return;
        }

        var fit = Math.Min(viewportWidth / WorkingCopyWidth, viewportHeight / WorkingCopyHeight);
        ZoomFactor = Math.Clamp(fit, MinZoomFactor, MaxZoomFactor);
    }

    /// <summary>Backlog item (user request, 2026-08-17) -- "Fit" variant that fits the SAFE-AREA box
    /// (not the whole frame) into the viewport, so the safe-area guide itself fills the available
    /// space. The safe area's own working-copy-space size (<see cref="SafeAreaInsetWorkingCopyUnits"/>,
    /// not <see cref="SafeAreaInsetPixels"/>) is zoom-INDEPENDENT by construction -- using the
    /// zoom-scaled inset here would make the target depend on the very zoom being solved for. Falls
    /// back to whole-frame <see cref="ApplyFit"/> when the safe-area box would be degenerate (a mode
    /// small enough that twice the inset exceeds its own dimensions -- the same real, reachable floor
    /// case <see cref="SafeAreaWidthPixels"/>/<see cref="SafeAreaHeightPixels"/> already guard).</summary>
    public void ApplyFitSafeArea(double viewportWidth, double viewportHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 || WorkingCopyWidth <= 0 || WorkingCopyHeight <= 0)
        {
            return;
        }

        var safeWidth = WorkingCopyWidth - (2 * SafeAreaInsetWorkingCopyUnits);
        var safeHeight = WorkingCopyHeight - (2 * SafeAreaInsetWorkingCopyUnits);
        if (safeWidth <= 0 || safeHeight <= 0)
        {
            ApplyFit(viewportWidth, viewportHeight);
            return;
        }

        var fit = Math.Min(viewportWidth / safeWidth, viewportHeight / safeHeight);
        ZoomFactor = Math.Clamp(fit, MinZoomFactor, MaxZoomFactor);
    }

    /// <summary>Backlog item (user request, 2026-08-17) -- fits ONLY the working copy's width into
    /// the viewport (height may overflow into the ScrollViewer's own native vertical scroll) -- same
    /// View-owns-viewport-size call convention as <see cref="ApplyFit"/>.</summary>
    public void ApplyFitWidth(double viewportWidth)
    {
        if (viewportWidth <= 0 || WorkingCopyWidth <= 0)
        {
            return;
        }

        ZoomFactor = Math.Clamp(viewportWidth / WorkingCopyWidth, MinZoomFactor, MaxZoomFactor);
    }

    /// <summary>Same reasoning as <see cref="ApplyFitWidth"/>, for height.</summary>
    public void ApplyFitHeight(double viewportHeight)
    {
        if (viewportHeight <= 0 || WorkingCopyHeight <= 0)
        {
            return;
        }

        ZoomFactor = Math.Clamp(viewportHeight / WorkingCopyHeight, MinZoomFactor, MaxZoomFactor);
    }

    public double CropLeftPixels => CropRect.X * CanvasDisplayWidth;

    public double CropTopPixels => CropRect.Y * CanvasDisplayHeight;

    public double CropWidthPixels => CropRect.Width * CanvasDisplayWidth;

    public double CropHeightPixels => CropRect.Height * CanvasDisplayHeight;

    /// <summary>Bottom-right corner in pixel space -- the resize-handle's anchor point.</summary>
    public double CropRightPixels => (CropRect.X + CropRect.Width) * CanvasDisplayWidth;

    public double CropBottomPixels => (CropRect.Y + CropRect.Height) * CanvasDisplayHeight;

    /// <summary>Follow-up visual-polish pass to the TX workflow modernization plan -- live rubber-
    /// band preview during draw-to-place (<c>TxImageEditorPaneView.OnCanvasPointerMoved</c>'s own
    /// <c>DragMode.Placing</c> branch sets this on every frame; <c>OnCanvasPointerReleased</c>/
    /// <c>DisarmPlacement</c>/<c>CancelActiveDrag</c>/<c>OnEditorCanvasPointerCaptureLost</c> all
    /// clear it, every path that can end a placement drag/arm). Top-level, not per-element, like
    /// <see cref="CropRect"/> above -- this has no element to belong to until the drag actually
    /// completes and creates one. Null at rest (nothing shown).</summary>
    [ObservableProperty]
    private NormalizedRect? _placementPreviewRect;

    public double PlacementPreviewLeftPixels => (PlacementPreviewRect?.X ?? 0) * CanvasDisplayWidth;

    public double PlacementPreviewTopPixels => (PlacementPreviewRect?.Y ?? 0) * CanvasDisplayHeight;

    public double PlacementPreviewWidthPixels => (PlacementPreviewRect?.Width ?? 0) * CanvasDisplayWidth;

    public double PlacementPreviewHeightPixels => (PlacementPreviewRect?.Height ?? 0) * CanvasDisplayHeight;

    public bool IsPlacementPreviewVisible => PlacementPreviewRect is not null;

    partial void OnPlacementPreviewRectChanged(NormalizedRect? value)
    {
        OnPropertyChanged(nameof(PlacementPreviewLeftPixels));
        OnPropertyChanged(nameof(PlacementPreviewTopPixels));
        OnPropertyChanged(nameof(PlacementPreviewWidthPixels));
        OnPropertyChanged(nameof(PlacementPreviewHeightPixels));
        OnPropertyChanged(nameof(IsPlacementPreviewVisible));
    }

    /// <summary>TX editor gap-items plan, line element (2026-09-01) -- the line counterpart to
    /// <see cref="PlacementPreviewRect"/> immediately above, same "live rubber-band preview during
    /// draw-to-place, cleared by every path that can end a placement drag/arm" contract. A rect
    /// can't represent a line's own direction (a line drawn top-right to bottom-left must render
    /// that way during the drag, not get normalized into a box), so this is a parallel field, not a
    /// reuse of <see cref="PlacementPreviewRect"/> -- endpoints in normalized full-working-copy
    /// space, same convention as <see cref="LineElementViewModel.X1"/>/etc.</summary>
    [ObservableProperty]
    private (double X1, double Y1, double X2, double Y2)? _placementPreviewLine;

    public Avalonia.Point PlacementPreviewLineStart => new(
        (PlacementPreviewLine?.X1 ?? 0) * CanvasDisplayWidth, (PlacementPreviewLine?.Y1 ?? 0) * CanvasDisplayHeight);

    public Avalonia.Point PlacementPreviewLineEnd => new(
        (PlacementPreviewLine?.X2 ?? 0) * CanvasDisplayWidth, (PlacementPreviewLine?.Y2 ?? 0) * CanvasDisplayHeight);

    public bool IsPlacementPreviewLineVisible => PlacementPreviewLine is not null;

    partial void OnPlacementPreviewLineChanged((double X1, double Y1, double X2, double Y2)? value)
    {
        OnPropertyChanged(nameof(PlacementPreviewLineStart));
        OnPropertyChanged(nameof(PlacementPreviewLineEnd));
        OnPropertyChanged(nameof(IsPlacementPreviewLineVisible));
    }

    /// <summary>Same follow-up pass, the alignment-guide-line half --
    /// <c>TxImageEditorPaneView.OnCanvasPointerMoved</c>'s own <c>DragMode.Overlay</c> branch sets
    /// these from <c>ComputeAlignmentGuideLines</c> (display-only: the drop-time snap itself stays
    /// exactly the existing <c>ComputeAlignmentSnap</c>-driven behavior, unchanged by this).
    /// Independent per axis, same as the underlying alignment match itself.</summary>
    [ObservableProperty]
    private double? _guideLineXNormalized;

    [ObservableProperty]
    private double? _guideLineYNormalized;

    public double GuideLineXPixels => (GuideLineXNormalized ?? 0) * CanvasDisplayWidth;

    public double GuideLineYPixels => (GuideLineYNormalized ?? 0) * CanvasDisplayHeight;

    public bool IsGuideLineXVisible => GuideLineXNormalized is not null;

    public bool IsGuideLineYVisible => GuideLineYNormalized is not null;

    partial void OnGuideLineXNormalizedChanged(double? value)
    {
        OnPropertyChanged(nameof(GuideLineXPixels));
        OnPropertyChanged(nameof(IsGuideLineXVisible));
    }

    partial void OnGuideLineYNormalizedChanged(double? value)
    {
        OnPropertyChanged(nameof(GuideLineYPixels));
        OnPropertyChanged(nameof(IsGuideLineYVisible));
    }

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
            text.FontFamily, text.StrokeColor, text.StrokeThickness,
            text.ShadowColor, text.ShadowOffsetX, text.ShadowOffsetY, text.RotationDegrees,
            text.GradientEnabled, text.GradientKind, text.GradientStartColor, text.GradientEndColor,
            text.Bold, text.Italic,
            text.StackColor, text.StackStepX, text.StackStepY,
            text.BitmapFillEnabled, text.BitmapFillSource, text.GrowToFillEnabled),
        BoxElementViewModel box => new RawBoxElementSnapshot(
            box.X, box.Y, box.Width, box.Height, box.Z, box.Locked, box.FillColor, box.BorderColor, box.BorderThickness, box.Opacity,
            box.CornerRadius,
            box.GradientEnabled, box.GradientKind, box.GradientStartColor, box.GradientEndColor,
            box.PerspectiveEnabled,
            box.Corner0X, box.Corner0Y, box.Corner1X, box.Corner1Y, box.Corner2X, box.Corner2Y, box.Corner3X, box.Corner3Y,
            box.FillEnabled),
        ImageElementViewModel image => new RawImageElementSnapshot(
            image.X, image.Y, image.Width, image.Height, image.Z, image.Locked, image.Source, image.Fit, image.Origin, image.IsBackground,
            image.NaturalPixelWidth, image.NaturalPixelHeight,
            image.PerspectiveEnabled,
            image.Corner0X, image.Corner0Y, image.Corner1X, image.Corner1Y, image.Corner2X, image.Corner2Y, image.Corner3X, image.Corner3Y),
        LineElementViewModel line => new RawLineElementSnapshot(
            line.X, line.Y, line.Width, line.Height, line.Z, line.Locked,
            line.X1, line.Y1, line.X2, line.Y2, line.StrokeColor, line.StrokeThickness, line.Opacity),
        _ => throw new NotSupportedException($"Unrecognized {nameof(ITemplateElementViewModel)}: {element.GetType()}."),
    };

    public event Action<IImageSource>? Applied;

    /// <summary>ui_transition_plan.md step 2 (T1-2): the SEND row's primary action -- applies the
    /// same output <see cref="Applied"/> would, but signals the parent to immediately transmit it
    /// too, one click instead of Apply-then-hunt-for-the-real-Transmit-button-in-the-sidebar. The
    /// parent (only owner of transmit state) still runs the actual TransmitCommand.</summary>
    public event Action<IImageSource>? AppliedAndTransmitRequested;

    /// <summary>Ready Rack direct-fire plan (2026-09-01): a SEPARATE event from
    /// <see cref="AppliedAndTransmitRequested"/>, raised by <see cref="OnReadyRackDirectFireRequested"/>
    /// -- lets <see cref="TxControlsPaneViewModel"/> tell a direct-fire-triggered Apply&amp;Transmit
    /// apart from an ordinary manual one (the button click), so it can chain the post-fire reopen
    /// (see <c>OnEditorDirectFire</c>'s own doc comment) ONLY for the former; ordinary Apply &amp;
    /// Transmit's own "close and leave empty" behavior stays completely unchanged.</summary>
    public event Action<IImageSource>? DirectFireRequested;

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

    /// <summary>Floor for <see cref="NudgeElementResize"/> -- same value as
    /// <c>TxImageEditorPaneView.MinNormalizedElementSize</c> (a SEPARATE, deliberately identical
    /// constant, not a shared reference, per that field's own doc comment: the drag-resize floor is
    /// View-local, this one guards the new keyboard path instead).</summary>
    private const double MinNormalizedElementResizeSize = 0.02;

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
        if (element is ImageElementViewModel { PerspectiveEnabled: true } or BoxElementViewModel { PerspectiveEnabled: true })
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

    [RelayCommand]
    private void AddOverlayElement() =>
        AddOverlayElementAt(CropRect.X + (CropRect.Width / 2), CropRect.Y + (CropRect.Height / 2), DefaultElementWidth, DefaultTextElementHeight);

    /// <summary>TX workflow modernization plan, Phase 3a -- <see cref="AddOverlayElement"/> (the
    /// toolbar button's parameterless command) now delegates here with its own original
    /// crop-centered seed as the default rect. The draw-to-place gesture
    /// (<c>TxImageEditorPaneView.axaml.cs</c>'s armed-placement tunnel handler) calls this directly
    /// with a caller-computed rect instead. <paramref name="centerX"/>/<paramref name="centerY"/>
    /// are CENTER-anchored, matching <see cref="ITemplateElementViewModel.X"/>/<c>Y</c>'s own
    /// convention (NOT <see cref="CropRect"/>'s top-left-anchored one) -- see
    /// <see cref="ProjectRectToCropRelative"/>'s own doc comment for why X/Y are stored relative to
    /// the full working copy, not the crop, despite this.</summary>
    public void AddOverlayElementAt(double centerX, double centerY, double width, double height)
    {
        PushUndoSnapshot();
        var element = CreateOverlayElement(
            text: "Text",
            x: centerX,
            y: centerY,
            width: width,
            height: height,
            fontSizeRelative: DefaultFontSizeRelative,
            color: new Rgb24(255, 255, 255),
            z: NextZ(),
            locked: false);
        OverlayElements.Add(element);
        SelectedOverlayElement = element;
        // Force-selected unconditionally (TX workflow modernization plan, Phase 2) -- unlike plain
        // selection change, a brand-new element overrides even an open Geometry tab, since there's
        // nothing to "compare positions" against yet.
        SelectTextStyleTab();
        RecomputePreview();
    }

    /// <summary>New in Phase 1 (spec/15-template-designer.md) -- mirrors
    /// <see cref="AddOverlayElement"/> exactly (crop-centered seed, same undo/select/recompute
    /// shape), just for the box element type instead of text.</summary>
    [RelayCommand]
    private void AddBoxElement() =>
        AddBoxElementAt(CropRect.X + (CropRect.Width / 2), CropRect.Y + (CropRect.Height / 2), DefaultElementWidth, DefaultBoxElementHeight);

    /// <summary>TX workflow modernization plan, Phase 3a -- same delegation shape as
    /// <see cref="AddOverlayElementAt"/> right above, see its own doc comment.</summary>
    public void AddBoxElementAt(double centerX, double centerY, double width, double height)
    {
        PushUndoSnapshot();
        var element = CreateBoxElement(
            x: centerX,
            y: centerY,
            width: width,
            height: height,
            fillColor: new Rgb24(64, 64, 64),
            borderColor: null,
            borderThickness: 0,
            opacity: 1.0,
            z: NextZ(),
            locked: false);
        OverlayElements.Add(element);
        SelectedOverlayElement = element;
        // Force-selected unconditionally (TX workflow modernization plan, Phase 2) -- see
        // AddOverlayElement's own identical comment. Box's applicable tab is Geometry (no dedicated
        // Box Style tab exists), so this is also the "already the fallback" case made explicit.
        SelectGeometryTab();
        RecomputePreview();
    }

    /// <summary>TX editor gap-items plan (2026-09-01) -- same delegation shape as
    /// <see cref="AddOverlayElement"/>/<see cref="AddBoxElement"/>, a fixed horizontal default seed
    /// (matching the plan's own "horizontal, DefaultElementWidth long, centered" default) via the
    /// endpoint-parameterized <see cref="AddLineElementAt"/> below.</summary>
    [RelayCommand]
    private void AddLineElement()
    {
        var centerX = CropRect.X + (CropRect.Width / 2);
        var centerY = CropRect.Y + (CropRect.Height / 2);
        var halfWidth = DefaultElementWidth / 2;
        AddLineElementAt(centerX - halfWidth, centerY, centerX + halfWidth, centerY);
    }

    /// <summary>TX workflow modernization plan, Phase 3a's own delegation shape (see
    /// <see cref="AddOverlayElementAt"/>'s doc comment), endpoint-parameterized instead of
    /// center/width/height -- the draw-to-place gesture's own anchor/release points ARE a line's
    /// endpoints directly, with no rect-normalization step in between (deliberately NOT run through
    /// <c>ComputeRectFromDrag</c>, which would lose direction/order).</summary>
    public void AddLineElementAt(double x1, double y1, double x2, double y2)
    {
        PushUndoSnapshot();
        var element = CreateLineElement(
            x1: x1,
            y1: y1,
            x2: x2,
            y2: y2,
            strokeColor: new Rgb24(255, 255, 255),
            strokeThickness: DefaultLineStrokeThickness,
            opacity: 1.0,
            z: NextZ(),
            locked: false);
        OverlayElements.Add(element);
        SelectedOverlayElement = element;
        // Force-selected unconditionally -- see AddOverlayElement's own identical comment. Line's
        // applicable tab is Geometry (no dedicated Line Style tab, same as box).
        SelectGeometryTab();
        RecomputePreview();
    }

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
            StatusMessage = _localization.GetString("Panes.TxImageEditor.AddImageFailed");
            return;
        }

        if (path is null)
        {
            return;
        }

        await AddImageFromPathAsync(path);
    }

    /// <summary>Missing-feature sweep (2026-08-31): factored out of <see cref="AddImageFromFileAsync"/>'s
    /// own body -- the load+insert half, unchanged behavior/contract from what that method already
    /// did inline before this split. NOT shared with <see cref="AddImagesFromDroppedFilesAsync"/>
    /// (code-review correction) -- that method deliberately re-implements load+downsample+insert on
    /// its own (Task.Run offload, batched push/recompute via <see cref="InsertImageElementCore"/>
    /// instead of one push/recompute per file), so there are genuinely two "load a path into an
    /// image element" bodies, not one shared one.</summary>
    private async Task AddImageFromPathAsync(string path)
    {
        IImageSource source;
        try
        {
            source = await _imageFileLoader.LoadOriginalAsync(path);
        }
        catch (Exception ex)
        {
            Log.AddImageFromFileFailed(_logger, ex);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.AddImageFailed");
            return;
        }

        StatusMessage = null;
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
        //
        // Code-review finding (T0-10 pass): read exactly ONCE into a local, not twice -- a second,
        // separate Current read here could race a concurrent decode-restart swapping in a fresh
        // 1x1 placeholder BETWEEN the guard check and the insert below (same reasoning already
        // applied to TxControlsPaneViewModel.CopyReceivedImageToTxAsync's own identical read).
        var current = _receivedImageBuffer.Current;
        if (current is { Width: <= 1, Height: <= 1 })
        {
            return;
        }

        // T0-10 (production_audit.md): _receivedImageBuffer.Current's getter now lazily allocates
        // a fresh, independently-owned array the first time it's read since the underlying decode
        // last changed (was: a fresh array on every single scanline-group decode, regardless of
        // whether anything ever read it -- real LOH pressure on the capture drain thread). Either
        // way, whatever Current returns here is a genuinely independent snapshot this class is
        // free to retain indefinitely -- IReceivedImageBuffer.Current's own doc comment states
        // this contract explicitly now; capturing the reference here still freezes it for this
        // element, unchanged.
        InsertImageElement(current, new ImageSourceOrigin(ImageSourceKind.LastRx, null));
    }

    /// <summary>Auditor usability review follow-up (2026-08-18) -- the "+ IMAGE" flyout's 4th source
    /// (clipboard paste, Phase 2's own logged scope cut, picked back up: "zero precedent for either
    /// [clipboard-paste/OS drag-drop] anywhere in this codebase" at the time; Avalonia's own
    /// <c>ClipboardExtensions.TryGetBitmapAsync</c> is real, cross-platform precedent now used here).
    /// Same shape as <see cref="AddImageFromFileAsync"/> (picker call -> loader call -> insert), just
    /// via <see cref="IFilePickerService.PickClipboardImageAsync"/> instead of
    /// <see cref="IFilePickerService.PickImageFileAsync"/> -- reuses the SAME
    /// <see cref="_imageFileLoader"/> call, one image-loading code path (EXIF orientation included),
    /// not a second one. The picker's own returned path is a throwaway temp PNG file (see that
    /// method's own doc comment) -- deleted here once loaded, success or failure, since nothing else
    /// ever needs it again (<see cref="ImageSourceKind.Clipboard"/>'s own Payload is always null, same
    /// "ephemeral, nothing to re-resolve" tier as <see cref="ImageSourceKind.LastRx"/>).</summary>
    [RelayCommand]
    private async Task AddImageFromClipboardAsync()
    {
        string? tempPath;
        try
        {
            tempPath = await _filePickerService.PickClipboardImageAsync();
        }
        catch (Exception ex)
        {
            Log.AddImageFromClipboardFailed(_logger, ex);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.AddImageFailed");
            return;
        }

        if (tempPath is null)
        {
            // No image on the clipboard right now -- a normal, silent no-op (same "nothing to add
            // yet" shape as AddLastRxImage's own empty-buffer case), not an error.
            return;
        }

        try
        {
            IImageSource source;
            try
            {
                source = await _imageFileLoader.LoadOriginalAsync(tempPath);
            }
            catch (Exception ex)
            {
                Log.AddImageFromClipboardFailed(_logger, ex);
                StatusMessage = _localization.GetString("Panes.TxImageEditor.AddImageFailed");
                return;
            }

            StatusMessage = null;
            InsertImageElement(source, new ImageSourceOrigin(ImageSourceKind.Clipboard, null));
        }
        finally
        {
            // Best-effort cleanup -- a failure here must not mask or replace whatever result the
            // load attempt above already produced (same reasoning as SaveTemplateCommand's own
            // cleanup-on-failure path).
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                Log.ClipboardTempFileCleanupFailed(_logger, tempPath, ex);
            }
        }
    }

    /// <summary>TX editor gap-items plan, item 4b (picture fill) -- picker call -> loader call ->
    /// assign onto the SELECTED text element's own BitmapFillSource/Enabled, same shape as
    /// <see cref="AddImageFromFileAsync"/> immediately above, but assigning onto an EXISTING
    /// element's property instead of inserting a new one (this fills the text's own glyphs, it
    /// doesn't add a separate image element). Gated on a text element actually being selected --
    /// same <see cref="CanInsertField"/>-style guard.</summary>
    private bool CanSetTextBitmapFill() => SelectedTextElement is not null;

    [RelayCommand(CanExecute = nameof(CanSetTextBitmapFill))]
    private async Task PickTextBitmapFillFromFileAsync()
    {
        if (SelectedTextElement is not { } text)
        {
            return;
        }

        string? path;
        try
        {
            path = await _filePickerService.PickImageFileAsync();
        }
        catch (Exception ex)
        {
            Log.AddImageFromFileFailed(_logger, ex);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.TextBitmapFillFailed");
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
            StatusMessage = _localization.GetString("Panes.TxImageEditor.TextBitmapFillFailed");
            return;
        }

        StatusMessage = null;
        // Source assigned before Enabled -- matches PasteSelectedElementStyle's own convention (see
        // that switch case's own doc comment).
        text.BitmapFillSource = source;
        text.BitmapFillEnabled = true;
    }

    /// <summary>Same shape as <see cref="AddImageFromClipboardAsync"/> immediately above --
    /// <see cref="PickTextBitmapFillFromFileAsync"/>'s own doc comment covers the "assigns onto the
    /// selected element" difference from the image-ELEMENT-adding commands this mirrors.</summary>
    [RelayCommand(CanExecute = nameof(CanSetTextBitmapFill))]
    private async Task PickTextBitmapFillFromClipboardAsync()
    {
        if (SelectedTextElement is not { } text)
        {
            return;
        }

        string? tempPath;
        try
        {
            tempPath = await _filePickerService.PickClipboardImageAsync();
        }
        catch (Exception ex)
        {
            Log.AddImageFromClipboardFailed(_logger, ex);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.TextBitmapFillFailed");
            return;
        }

        if (tempPath is null)
        {
            return;
        }

        try
        {
            IImageSource source;
            try
            {
                source = await _imageFileLoader.LoadOriginalAsync(tempPath);
            }
            catch (Exception ex)
            {
                Log.AddImageFromClipboardFailed(_logger, ex);
                StatusMessage = _localization.GetString("Panes.TxImageEditor.TextBitmapFillFailed");
                return;
            }

            StatusMessage = null;
            text.BitmapFillSource = source;
            text.BitmapFillEnabled = true;
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                Log.ClipboardTempFileCleanupFailed(_logger, tempPath, ex);
            }
        }
    }

    /// <summary>Turns the fill mode back to solid (matching the GRADIENT checkbox's own
    /// "unchecking just clears the flag" shape) -- deliberately does NOT clear
    /// <see cref="OverlayElementViewModel.BitmapFillSource"/> itself, so re-checking PICTURE without
    /// re-picking a file restores the last picture, same "flag gates whether the resolved value is
    /// USED, the value itself persists" convention <see cref="OverlayElementViewModel.StrokeColor"/>/
    /// <see cref="OverlayElementViewModel.ShadowColor"/> already establish (those go null<->set
    /// instead of a separate enabled flag, but the "toggling off doesn't destroy data" spirit is the
    /// same).</summary>
    [RelayCommand(CanExecute = nameof(CanSetTextBitmapFill))]
    private void ClearTextBitmapFill()
    {
        if (SelectedTextElement is { } text)
        {
            text.BitmapFillEnabled = false;
        }
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
    /// (see <see cref="ITemplateElementViewModel.RemoveCommand"/>'s own doc comment).
    /// <see cref="ReceivedAt"/> (UX friction fix, Fable operator-perspective review): the AXAML row
    /// used to display <see cref="Id"/> raw (a GUID-shaped string, meaningless to an operator picking
    /// between up to <see cref="RxHistoryPickerMaxEntries"/> recent receptions) -- already
    /// local-offset (<c>DateTimeOffset.Now</c> at write time, see
    /// <c>ReceiveHistoryRecorder.RecordCompletedImageAsync</c>), same as <c>MainWindow.axaml</c>'s
    /// own established <c>ReceivedAt</c> display convention, so no extra conversion needed here.</summary>
    public sealed record RxHistoryPickerEntry(string Id, DateTimeOffset ReceivedAt, string FilePath, Bitmap? Thumbnail, IRelayCommand<RxHistoryPickerEntry>? SelectCommand);

    [ObservableProperty]
    private ObservableCollection<RxHistoryPickerEntry> _rxHistoryPickerEntries = [];

    /// <summary>Refreshes <see cref="RxHistoryPickerEntries"/> -- called when the "From RX history"
    /// flyout opens (View-level), not kept live/subscribed (same "no lifecycle hook to unsubscribe"
    /// reasoning as <see cref="AddLastRxImage"/>). <see cref="IReceiveHistoryStore.QueryAsync"/>
    /// already returns newest-first (<c>SqliteReceiveHistoryStore</c>'s own <c>ORDER BY
    /// ReceivedAtUtc DESC</c>), capped client-side to <see cref="RxHistoryPickerMaxEntries"/> since
    /// the store has no server-side limit parameter.</summary>
    [RelayCommand]
    private async Task RefreshRxHistoryPickerAsync()
    {
        // Tier B audit finding: this was the one sibling among the 4 image-source add/refresh paths
        // (AddImageFromFileAsync, AddImageFromClipboardAsync, AddImageFromRxHistoryAsync) with no
        // StatusMessage on failure -- log-only, so a failure here (e.g. an unreadable RX history
        // SQLite file) left the "From RX history" flyout silently empty (or stale, showing the
        // PREVIOUS successful refresh's entries pointing at possibly-deleted files) with zero
        // explanation. StatusMessage now cleared on entry / set on failure, same as the siblings.
        StatusMessage = null;
        IReadOnlyList<ReceiveHistoryEntry> entries;
        try
        {
            entries = await _receiveHistoryStore.QueryAsync(new ReceiveHistoryFilter());
        }
        catch (Exception ex)
        {
            Log.RefreshRxHistoryPickerFailed(_logger, ex);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.RxHistoryPickerRefreshFailed");
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

            picked.Add(new RxHistoryPickerEntry(entry.Id, entry.ReceivedAt, entry.FilePath, thumbnail, AddImageFromRxHistoryCommand));
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
            StatusMessage = _localization.GetString("Panes.TxImageEditor.AddImageFailed");
            return;
        }

        StatusMessage = null;
        InsertImageElement(source, new ImageSourceOrigin(ImageSourceKind.RxHistory, entry.Id));
    }

    /// <summary>Shared by all 4 Phase 2/missing-feature-sweep image sources -- same crop-centered
    /// seed/undo/select/recompute shape as <see cref="AddOverlayElement"/>/<see cref="AddBoxElement"/>.
    /// Default Contain fit (not Stretch) -- an inserted photo keeping its own aspect ratio by default
    /// is the less-surprising choice; <see cref="ImageElementViewModel.Fit"/> isn't yet user-editable
    /// in Phase 2 (Phase 4's style panel territory), but the pipeline/VM plumbing already supports it.
    /// One-source-at-a-time push+recompute -- <see cref="AddImagesFromDroppedFilesAsync"/>'s own
    /// multi-file batch does NOT call this (would be N undo steps + N pipeline runs for one user
    /// gesture, violating this editor's own established "one gesture, one undo step" convention --
    /// see <see cref="ApplySnappedElementBounds"/>'s own doc comment); it calls
    /// <see cref="InsertImageElementCore"/> directly instead, batching the push/recompute itself.</summary>
    private void InsertImageElement(IImageSource source, ImageSourceOrigin origin)
    {
        // TX workflow modernization plan, Phase 7 -- captured BEFORE DownsampleToBudget: "original
        // size" (ResetImageElementToOriginalSize) means the source's own native resolution, not the
        // working-copy-budget-capped copy this editor actually composites with.
        var naturalPixelWidth = source.Width;
        var naturalPixelHeight = source.Height;

        // Downsampled to the SAME working-copy budget as the background image itself (code-review
        // finding -- see DownsampleToBudget's own doc comment) before ANY of it touches the UI
        // thread's WriteableBitmap conversion or the real per-frame pipeline. Resolved once here,
        // at insert time, same as the file/RX-history sources' own LoadOriginalAsync resolution --
        // consistent with RawImageElementSnapshot's own "resolve once, snapshot the resolved value"
        // contract, not a departure from it.
        var downsampled = DownsampleToBudget(
            source, (int)(WorkingCopyWidth * WorkingCopyScaleFactor), (int)(WorkingCopyHeight * WorkingCopyScaleFactor), _preparer);

        PushUndoSnapshot();
        InsertImageElementCore(downsampled, naturalPixelWidth, naturalPixelHeight, origin, cascadeIndex: 0);
        RecomputePreview();
    }

    /// <summary>Missing-feature sweep (2026-08-31): the shared core <see cref="InsertImageElement"/>
    /// factors down to -- create the element, add it, select it -- with NO push/no recompute, so a
    /// caller inserting several elements for one user gesture (<see cref="AddImagesFromDroppedFilesAsync"/>)
    /// can batch those into one undo step and one pipeline run instead of one each.
    /// <paramref name="cascadeIndex"/> offsets each successive element diagonally (wrapped at 8 steps
    /// -- <c>DefaultElementWidth</c> is 0.3 of the normalized canvas, so unwrapped growth would push
    /// element centers outside [0,1] well before a 20-file drop finishes, and this editor's own
    /// established convention is that out-of-bounds content is clipped at render time, not
    /// repositioned -- so those elements would be invisible on the canvas, reachable only via the
    /// elements list) so a multi-file drop doesn't stack every element exactly on top of the first
    /// one, without ever pushing later elements off-canvas.</summary>
    private void InsertImageElementCore(
        IImageSource downsampled, int naturalPixelWidth, int naturalPixelHeight, ImageSourceOrigin origin, int cascadeIndex)
    {
        const double CascadeOffset = 0.03;
        const int CascadeWrap = 8;
        var offset = CascadeOffset * (cascadeIndex % CascadeWrap);
        var element = CreateImageElement(
            x: CropRect.X + (CropRect.Width / 2) + offset,
            y: CropRect.Y + (CropRect.Height / 2) + offset,
            width: DefaultElementWidth,
            height: DefaultElementWidth,
            source: downsampled,
            fit: ImageFitMode.Contain,
            origin: origin,
            z: NextZ(),
            locked: false,
            naturalPixelWidth: naturalPixelWidth,
            naturalPixelHeight: naturalPixelHeight);
        OverlayElements.Add(element);
        SelectedOverlayElement = element;
        // Force-selected unconditionally (TX workflow modernization plan, Phase 2) -- covers both
        // call sites (toolbar/clipboard InsertImageElement AND the OS drag-drop path below), since
        // both insert genuinely new image content, not a copy of something already on the canvas.
        SelectImageTab();
    }

    private const int MaxDroppedImageFiles = 20;

    /// <summary>Missing-feature sweep (2026-08-31): OS file drag-and-drop onto the TX editor,
    /// wired from <c>TxImageEditorPaneView.axaml.cs</c>'s own <c>OnEditorDrop</c> code-behind
    /// handler. Legacy YONIQ's own equivalent (<c>Main.cpp</c>'s <c>DropFile</c>) replaces the
    /// WHOLE TX bitmap with the one dropped file (<c>DragQueryFile(hDrop, 0, ...)</c> -- index 0
    /// only, silently ignoring any additional files); this port's own architecture is
    /// template/compositing-based, not "one flat bitmap," so a dropped file is a new picture
    /// ELEMENT (the same shape as the existing "+ IMAGE" flyout's 3 sources), and -- a deliberate
    /// improvement over legacy, not an oversight -- a multi-file drop inserts one element PER file
    /// instead of silently discarding all but the first.
    ///
    /// One undo step, one pipeline recompute, for the WHOLE drop (not per file) -- matches this
    /// editor's own established "one gesture, one undo step" convention (see
    /// <see cref="ApplySnappedElementBounds"/>'s own doc comment); each file is loaded and
    /// downsampled INSIDE the loop, one at a time, with the full-resolution decode discarded before
    /// the next iteration starts, so a large multi-file drop doesn't hold every full-res decode in
    /// memory at once. Capped at <see cref="MaxDroppedImageFiles"/> -- nothing bounds how many files
    /// an OS drag-drop can hand this method otherwise, and each one is a real decode plus a
    /// synchronous downsample.</summary>
    public async Task AddImagesFromDroppedFilesAsync(IReadOnlyList<string> paths)
    {
        try
        {
            if (paths.Count == 0)
            {
                Log.DroppedFilesEmpty(_logger);
                StatusMessage = _localization.GetString("Panes.TxImageEditor.NoImageFilesDropped");
                return;
            }

            var truncated = paths.Count > MaxDroppedImageFiles;
            var capped = truncated ? paths.Take(MaxDroppedImageFiles).ToList() : paths;

            var loaded = new List<(string Path, IImageSource Downsampled, int NaturalWidth, int NaturalHeight)>();
            var failureCount = 0;
            foreach (var path in capped)
            {
                try
                {
                    var source = await _imageFileLoader.LoadOriginalAsync(path);
                    // TX workflow modernization plan, Phase 7 -- captured before the downsample below,
                    // same reasoning as InsertImageElement's own capture.
                    var naturalWidth = source.Width;
                    var naturalHeight = source.Height;
                    // Downsample-then-discard-the-full-res-copy INSIDE the loop, same reasoning as
                    // the class doc comment above -- `source` is never retained past this iteration.
                    // Offloaded via Task.Run: DownsampleToBudget/_preparer.Resize is real synchronous
                    // CPU work that would otherwise stall the UI thread once per dropped file (the
                    // single-image path via InsertImageElement pays this cost once; a multi-file drop
                    // would pay it up to MaxDroppedImageFiles times in a row with no progress feedback).
                    var downsampled = await Task.Run(() => DownsampleToBudget(
                        source, (int)(WorkingCopyWidth * WorkingCopyScaleFactor), (int)(WorkingCopyHeight * WorkingCopyScaleFactor), _preparer));
                    loaded.Add((path, downsampled, naturalWidth, naturalHeight));
                }
                catch (Exception ex)
                {
                    Log.AddImageFromDroppedFileFailed(_logger, path, ex);
                    failureCount++;
                }
            }

            if (loaded.Count == 0)
            {
                StatusMessage = _localization.GetString("Panes.TxImageEditor.AddImageFailed");
                return;
            }

            PushUndoSnapshot();
            _suspendPreview = true;
            try
            {
                for (var i = 0; i < loaded.Count; i++)
                {
                    InsertImageElementCore(
                        loaded[i].Downsampled, loaded[i].NaturalWidth, loaded[i].NaturalHeight,
                        new ImageSourceOrigin(ImageSourceKind.File, loaded[i].Path), cascadeIndex: i);
                }
            }
            finally
            {
                _suspendPreview = false;
            }

            RecomputePreview();

            // Priority: truncation > failures > success -- both are real, but they mean different
            // things and call for different operator actions (re-drop the remainder that got cut,
            // vs. those specific files are just unreadable), so collapsing both into one generic
            // "some failed" message would destroy the information the status line exists to convey.
            StatusMessage = truncated
                ? _localization.GetString("Panes.TxImageEditor.DroppedImagesTruncated", loaded.Count, MaxDroppedImageFiles, paths.Count)
                : failureCount > 0
                    ? _localization.GetString("Panes.TxImageEditor.SomeDroppedImagesFailed", loaded.Count, capped.Count, failureCount)
                    : null;
        }
        catch (Exception ex)
        {
            // Same outer-guard shape as OnReadyRackTemplateSelected's own try/catch -- this is an
            // async void event handler's real body (TxImageEditorPaneView.axaml.cs's own
            // OnEditorDrop has no caller to observe a fault), so any exception this loop's own
            // per-file try/catch didn't already handle (a bug in DownsampleToBudget/CreateImageElement/
            // RecomputePreview itself) must be caught HERE, not left to crash the process --
            // AppDomain.UnhandledException only logs, it does not prevent termination.
            Log.AddImageFromFileFailed(_logger, ex);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.AddImageFailed");
        }
    }

    /// <summary>Missing-feature sweep (2026-08-31): the code-behind's own <c>OnEditorDrop</c> extracts
    /// local file paths from the drop payload BEFORE calling <see cref="AddImagesFromDroppedFilesAsync"/>
    /// -- that extraction itself can throw (a malformed platform drag payload, a revoked storage
    /// handle) and the code-behind has no logger of its own to report through, so it calls this
    /// instead of swallowing the exception silently.</summary>
    public void ReportDroppedFilesUnreadable(Exception exception)
    {
        Log.DroppedFilesUnreadable(_logger, exception);
        StatusMessage = _localization.GetString("Panes.TxImageEditor.DroppedFilesUnreadable");
    }

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
                DisablePerspective(warpedImage);
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
    /// <see cref="OpenBlankEditorAsync"/> seeds every editor with -- same check
    /// <see cref="OnReadyRackDirectFireRequested"/> already uses for its own "no photo" refusal.
    /// Read fresh by the View's own canvas-context-menu <c>Opened</c> handler (same
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
    /// real loaded yet, mirroring <see cref="OnReadyRackDirectFireRequested"/>'s own identical
    /// refusal shape for the same underlying check.
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
            var element = CreateImageElement(
                x: 0.5, y: 0.5, width: 1, height: 1, source: baked, fit: ImageFitMode.Contain,
                origin: new ImageSourceOrigin(ImageSourceKind.Promoted, null), z: z, locked: true,
                isBackground: true, naturalPixelWidth: _targetMode.ImageWidth, naturalPixelHeight: _targetMode.ImageHeight);
            OverlayElements.Insert(0, element);

            var placeholder = new BlankImageSource(_targetMode.ImageWidth, _targetMode.ImageHeight, BlankImageSource.DefaultColor);
            // New generation, same reasoning as FlattenElementAsync's own identical assignment: this
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

    private bool _isDemoting;

    private bool CanDemoteBackdropToBackground(ITemplateElementViewModel? element) =>
        !_isDemoting && element is ImageElementViewModel { IsBackground: true } && OverlayElements.Contains(element);

    /// <summary>User-requested (2026-09-15, background/backdrop naming work): the reverse of
    /// <see cref="PromoteBackgroundToBackdrop"/>. Replaces the background WITHIN the current crop
    /// window with this backdrop element's own pixels, respecting its
    /// <see cref="ImageElementViewModel.Fit"/> and perspective warp (if any) -- same "only the crop
    /// region, not the whole underlying source" scope <see cref="BakeElementIntoSource"/> already has
    /// for Flatten (nit: an earlier doc comment here overstated this as replacing the background
    /// "outright" -- widening the crop afterward can reveal pixels from the photo that was there
    /// before, matching Flatten's own established, documented behavior for the same reason).
    /// <para>[Auditor-found blocker, fixed here] An earlier draft called
    /// <c>_preparer.Resize(backdrop.Source, ..., preserveAspect: false)</c> directly -- that ignores
    /// <see cref="ImageElementViewModel.Fit"/> entirely (a <c>Contain</c>-fit backdrop, the element's
    /// own default, got silently stretched instead of letterboxed) and drops any perspective warp
    /// (the PERSPECTIVE checkbox is gated only on element type, not <c>Locked</c>/<c>IsBackground</c>,
    /// so a locked backdrop CAN still be warped). Fixed by reusing <see cref="BakeElementIntoSource"/>
    /// -- the SAME async, <c>Task.Run</c>-offloaded bake <see cref="FlattenElementAsync"/> already
    /// uses, which renders through the real <c>ComposePreview</c>/<c>ApplyTemplate</c> pipeline (Fit
    /// and perspective both correctly resolved there) rather than a bare resize. A full-frame element
    /// (X=0.5,Y=0.5,W=1,H=1 -- always true for a backdrop, by construction) makes that bake's own
    /// write-back rect exactly the whole crop-content area, so "patch this element into the source"
    /// and "replace the background with it" are the same operation here -- no separate replace-path
    /// needed. Same "stale-result discarded, not silently applied" re-validation
    /// <see cref="FlattenElementAsync"/>'s own doc comment explains, for the same reason (an offloaded
    /// compute means the operator can edit while it runs).</para>
    /// <para><see cref="CropRect"/> is deliberately left UNTOUCHED -- same auditor-found blocker
    /// <see cref="PromoteBackgroundToBackdrop"/>'s own doc comment explains: resetting it would
    /// reproject every OTHER overlay element's bounds, not just this one's framing. Reusing the bake
    /// (which already renders AT the current crop) makes this automatic, not something to remember.</para>
    /// <para>User-reported feedback (2026-09-15): an earlier draft armed/required a second click to
    /// confirm before actually replacing the background -- removed. This command already pushes a
    /// real, working undo step (see
    /// <c>DemoteBackdropToBackgroundCommand_UndoRestoresTheBackdropElement</c>), so a second confirming
    /// click is redundant friction, not a safety net -- Undo already IS the safety net. One click,
    /// done.</para></summary>
    [RelayCommand(CanExecute = nameof(CanDemoteBackdropToBackground))]
    private async Task DemoteBackdropToBackgroundAsync(ITemplateElementViewModel? element)
    {
        if (element is not ImageElementViewModel { IsBackground: true } backdrop || !OverlayElements.Contains(backdrop))
        {
            return;
        }

        if (!TryGetCropContentMetrics(out var padX, out var padY, out var contentWidth, out var contentHeight))
        {
            StatusMessage = _localization.GetString("Panes.TxImageEditor.FlattenUnavailable");
            return;
        }

        var request = new FlattenBakeRequest(
            _originalSource, CropRect, PreserveAspect, BuildTemplateElement(backdrop),
            _targetMode.ImageWidth, _targetMode.ImageHeight, padX, padY, contentWidth, contentHeight);
        var preparer = _preparer;

        IImageSource? demoted;
        _isDemoting = true;
        DemoteBackdropToBackgroundCommand.NotifyCanExecuteChanged();
        try
        {
            demoted = await Task.Run(() => BakeElementIntoSource(preparer, request));
        }
        catch (Exception ex)
        {
            Log.DemoteBackdropToBackgroundFailed(_logger, ex);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.DemoteFailed");
            return;
        }
        finally
        {
            _isDemoting = false;
            DemoteBackdropToBackgroundCommand.NotifyCanExecuteChanged();
        }

        if (demoted is null)
        {
            // A full-frame backdrop's write-back rect is always the whole crop-content area, so this
            // should be unreachable in practice -- handled anyway, same defensive shape
            // FlattenElementAsync's own identical case uses.
            StatusMessage = _localization.GetString("Panes.TxImageEditor.DemoteOutsideFrame");
            return;
        }

        // Stale-result guard, same shape and reasoning as FlattenElementAsync's own.
        if (!ReferenceEquals(_originalSource, request.Source)
            || !CropRect.Equals(request.CropRect)
            || PreserveAspect != request.PreserveAspect
            || !OverlayElements.Contains(backdrop)
            || !BuildTemplateElement(backdrop).Equals(request.Element))
        {
            Log.DemoteBackdropToBackgroundDiscardedAsStale(_logger);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.DemoteDiscardedStale");
            return;
        }

        // Captured before Dispose() below -- reading it off a disposed element afterward is safe
        // today (Dispose only releases bitmaps, never touches Origin), but there's no reason to rely
        // on that once a plain local does the same job for free.
        var wasPromoted = backdrop.Origin.Kind == ImageSourceKind.Promoted;

        PushUndoSnapshot();
        _suspendPreview = true;
        try
        {
            backdrop.PropertyChanged -= OnOverlayElementPropertyChanged;
            OverlayElements.Remove(backdrop);
            if (SelectedOverlayElements.Contains(backdrop))
            {
                SetSelection(SelectedOverlayElements.Where(e => !ReferenceEquals(e, backdrop)));
            }

            backdrop.Dispose();

            _sourceBaseline = demoted;
            _sourceBaselineRotation = _rotationCount;
            ReplaceSourceAndWorkingCopy(demoted);
        }
        finally
        {
            _suspendPreview = false;
        }

        // Auditor-found blocker: a backdrop created by PromoteBackgroundToBackdrop already has the
        // CURRENT adjustment sliders baked into its own pixels (Promote's own doc comment). If those
        // sliders are still non-identity when demoted back, the live pipeline applies them a SECOND
        // time to the now-restored background on every subsequent render -- the image visibly changes
        // on this click with no notice. Narrower than Flatten's own identical-shaped warning
        // (BuildAdjustments().IsIdentity check, Panes.TxImageEditor.FlattenAdjustmentsNowApply): a
        // backdrop created via the OTHER path (+Image, then Set as backdrop directly, never promoted)
        // has raw, never-baked pixels, so continuing to apply the current sliders to it is the SAME
        // "sliders apply live to whatever's currently loaded" behavior Browse/Stock already have --
        // warning there would be a false alarm. ImageSourceKind.Promoted (added alongside Promote
        // itself) is exactly the signal that distinguishes the two origins.
        // Auditor nit: this reads the sliders' CURRENT value, not what was actually baked at promote
        // time -- a promoted backdrop with sliders moved back to identity since correctly stays quiet,
        // but sliders moved to a DIFFERENT non-identity value after promote also warn even though
        // nothing was technically "already baked in" at that exact value. Accepted as conservative
        // (an extra warning, never a missed one); the loc string below is worded to stay true either
        // way rather than assert a specific baked-in history. Also clears any stale prior message on
        // the no-warn path, matching Flatten's own IsIdentity ? null : key shape -- a leftover message
        // from an earlier action must not survive a successful, nothing-to-report demote.
        StatusMessage = wasPromoted && !BuildAdjustments().IsIdentity
            ? _localization.GetString("Panes.TxImageEditor.DemotePromotedAdjustmentsNowReapply")
            : null;

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
    /// <see cref="BuildRawSnapshot"/> already doing the identical "derived bbox becomes the base
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
                case ImageElementViewModel { PerspectiveEnabled: false } image:
                    SeedPerspectiveCorners(image);
                    image.PerspectiveEnabled = true;
                    break;
                case ImageElementViewModel { PerspectiveEnabled: true } image:
                    DisablePerspective(image);
                    break;
                case BoxElementViewModel { PerspectiveEnabled: false } box:
                    SeedPerspectiveCorners(box);
                    box.PerspectiveEnabled = true;
                    break;
                case BoxElementViewModel { PerspectiveEnabled: true } box:
                    DisablePerspective(box);
                    break;
            }
        }
        finally
        {
            _suspendPreview = false;
        }

        RecomputePreview();
    }

    private static void SeedPerspectiveCorners(ImageElementViewModel element)
    {
        var left = element.X - (element.Width / 2);
        var top = element.Y - (element.Height / 2);
        var right = element.X + (element.Width / 2);
        var bottom = element.Y + (element.Height / 2);
        element.Corner0X = left;
        element.Corner0Y = top;
        element.Corner1X = right;
        element.Corner1Y = top;
        element.Corner2X = right;
        element.Corner2Y = bottom;
        element.Corner3X = left;
        element.Corner3Y = bottom;
    }

    private static void SeedPerspectiveCorners(BoxElementViewModel element)
    {
        var left = element.X - (element.Width / 2);
        var top = element.Y - (element.Height / 2);
        var right = element.X + (element.Width / 2);
        var bottom = element.Y + (element.Height / 2);
        element.Corner0X = left;
        element.Corner0Y = top;
        element.Corner1X = right;
        element.Corner1Y = top;
        element.Corner2X = right;
        element.Corner2Y = bottom;
        element.Corner3X = left;
        element.Corner3Y = bottom;
    }

    private static void DisablePerspective(ImageElementViewModel element)
    {
        element.NaturalX = element.X;
        element.NaturalY = element.Y;
        element.NaturalWidth = element.Width;
        element.NaturalHeight = element.Height;
        element.PerspectiveEnabled = false;
    }

    private static void DisablePerspective(BoxElementViewModel element)
    {
        element.NaturalX = element.X;
        element.NaturalY = element.Y;
        element.NaturalWidth = element.Width;
        element.NaturalHeight = element.Height;
        element.PerspectiveEnabled = false;
    }

    /// <summary>TX editor gap-items plan, item 3 -- see <see cref="Rotate"/>'s own call-site comment
    /// for why this bypasses the derived X/Y/Width/Height setters entirely, applying the SAME
    /// (x,y) -&gt; (1-y,x) 90°-clockwise transform every other element kind's own generic path uses,
    /// directly to all 4 corner fields.</summary>
    private static void RotateCorners(ImageElementViewModel element)
    {
        var (x0, y0) = (element.Corner0X, element.Corner0Y);
        var (x1, y1) = (element.Corner1X, element.Corner1Y);
        var (x2, y2) = (element.Corner2X, element.Corner2Y);
        var (x3, y3) = (element.Corner3X, element.Corner3Y);
        element.Corner0X = 1 - y0;
        element.Corner0Y = x0;
        element.Corner1X = 1 - y1;
        element.Corner1Y = x1;
        element.Corner2X = 1 - y2;
        element.Corner2Y = x2;
        element.Corner3X = 1 - y3;
        element.Corner3Y = x3;
    }

    /// <inheritdoc cref="RotateCorners(ImageElementViewModel)"/>
    private static void RotateCorners(BoxElementViewModel element)
    {
        var (x0, y0) = (element.Corner0X, element.Corner0Y);
        var (x1, y1) = (element.Corner1X, element.Corner1Y);
        var (x2, y2) = (element.Corner2X, element.Corner2Y);
        var (x3, y3) = (element.Corner3X, element.Corner3Y);
        element.Corner0X = 1 - y0;
        element.Corner0Y = x0;
        element.Corner1X = 1 - y1;
        element.Corner1Y = x1;
        element.Corner2X = 1 - y2;
        element.Corner2Y = x2;
        element.Corner3X = 1 - y3;
        element.Corner3Y = x3;
    }

    /// <summary>Shared element-construction wiring for <see cref="AddOverlayElement"/> and
    /// restoration (<see cref="CreateElementFromSnapshot"/>) -- round-1 plan-review finding on
    /// spec/18-path-to-1.0.md's re-open/re-edit sub-piece, still the right call in Phase 1: keep
    /// this in exactly one place, not duplicated between "new blank element" and "restored element"
    /// call sites.</summary>
    private OverlayElementViewModel CreateOverlayElement(
        string text, double x, double y, double width, double height, double fontSizeRelative, Rgb24 color, int z, bool locked,
        string? fontFamily = null, Rgb24? strokeColor = null, double strokeThickness = 0.02,
        Rgb24? shadowColor = null, double shadowOffsetX = 0.02, double shadowOffsetY = 0.02, double rotationDegrees = 0,
        bool gradientEnabled = false, TextGradientKind gradientKind = TextGradientKind.Horizontal,
        Rgb24? gradientStartColor = null, Rgb24? gradientEndColor = null,
        bool bold = false, bool italic = false,
        Rgb24? stackColor = null, double stackStepX = 0.02, double stackStepY = 0.02,
        bool bitmapFillEnabled = false, IImageSource? bitmapFillSource = null,
        bool growToFillEnabled = false)
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
            ShadowColor = shadowColor,
            ShadowOffsetX = shadowOffsetX,
            ShadowOffsetY = shadowOffsetY,
            RotationDegrees = rotationDegrees,
            GradientEnabled = gradientEnabled,
            GradientKind = gradientKind,
            // Null-coalesced to the VM's own field-initializer defaults (255,0,0)/(0,0,255) -- a
            // record constructor parameter default can't call a struct constructor (not a compile-
            // time constant), which is why RawTextElementSnapshot's own Gradient*Color fields are
            // nullable even though the VM's own are not; this is where that gets reconciled back.
            GradientStartColor = gradientStartColor ?? new Rgb24(255, 0, 0),
            GradientEndColor = gradientEndColor ?? new Rgb24(0, 0, 255),
            Bold = bold,
            Italic = italic,
            StackColor = stackColor,
            StackStepX = stackStepX,
            StackStepY = stackStepY,
            // TX editor gap-items plan, item 4b -- Source before Enabled (round-1 code-review
            // finding, matches every other write-both-together assignment in this file). Both
            // assigned AFTER Gradient* above -- if a hand-edited/imported template somehow has both
            // enabled, BitmapFillEnabled's own mutual-exclusion setter clears the GradientEnabled
            // that was just set, so the VM's post-construction state self-heals to the documented
            // precedence winner (BitmapFill) rather than staying ambiguous; the Source/Enabled
            // ordering between THEMSELVES doesn't affect that, Source's own setter never touches
            // Gradient.
            BitmapFillSource = bitmapFillSource,
            BitmapFillEnabled = bitmapFillEnabled,
            GrowToFillEnabled = growToFillEnabled,
            Z = z,
            Locked = locked,
            ImageWidth = CanvasDisplayWidth,
            ImageHeight = CanvasDisplayHeight,
            // Quick Style Flyout (TX workflow modernization plan, Phase 1) -- see its own doc
            // comment; _targetMode is ctor-only/never reassigned, so this constant stays correct
            // for the element's whole lifetime.
            TargetModeHeightPx = _targetMode.ImageHeight,
            RemoveCommand = RemoveOverlayElementCommand,
            MoveUpCommand = MoveElementUpCommand,
            MoveDownCommand = MoveElementDownCommand,
            BringToFrontCommand = BringToFrontCommand,
            SendToBackCommand = SendToBackCommand,
            DuplicateCommand = DuplicateCommand,
            AlignSelectedElementToCropCommand = AlignSelectedElementToCropCommand,
            CopyCommand = CopySelectedElementCommand,
            CutCommand = CutSelectedElementCommand,
            PasteCommand = PasteElementCommand,
            FlattenCommand = FlattenElementCommand,
            CopyStyleCommand = CopySelectedElementStyleCommand,
            PasteStyleCommand = PasteSelectedElementStyleCommand,
            AddPlateCommand = AddPlateBehindTextCommand,
            ClearTextBitmapFillCommand = ClearTextBitmapFillCommand,
            InsertFieldCommand = InsertFieldCommand,
            SetFontSizePresetCommand = SetFontSizePresetCommand,
            SetTextColorPresetCommand = SetTextColorPresetCommand,
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
            // Same ordering reasoning, same reason it must be set AFTER FontSizeRelative/Color above
            // in this initializer -- TX workflow modernization plan, Phase 1.
            PushUndoSnapshotForStyleChange = () => PushUndoSnapshotCoalesced("OverlayStyle"),
        };
        element.CanvasFontSize = ComputeCanvasFontSize(element);
        element.CanvasStrokeThicknessPixels = ComputeCanvasStrokeThicknessPixels(element);
        element.PropertyChanged += OnOverlayElementPropertyChanged;
        RefreshElementPreviewMetrics(element);
        return element;
    }

    /// <summary>Box counterpart to <see cref="CreateOverlayElement"/> -- same wiring shape, no
    /// CanvasFontSize (boxes don't shrink-to-fit).</summary>
    private BoxElementViewModel CreateBoxElement(
        double x, double y, double width, double height, Rgb24 fillColor, Rgb24? borderColor, double borderThickness, double opacity, int z, bool locked,
        double cornerRadius = 0,
        bool gradientEnabled = false, TextGradientKind gradientKind = TextGradientKind.Horizontal,
        Rgb24? gradientStartColor = null, Rgb24? gradientEndColor = null,
        // TX editor gap-items plan, item 3 (perspective transform, 2026-09-02) -- trailing/defaulted,
        // same additive convention every other Phase-N addition to this factory already uses.
        bool perspectiveEnabled = false,
        double corner0X = 0, double corner0Y = 0, double corner1X = 0, double corner1Y = 0,
        double corner2X = 0, double corner2Y = 0, double corner3X = 0, double corner3Y = 0,
        // Legacy `.mtm` import -- see TemplateBoxElement.FillEnabled's own doc comment.
        bool fillEnabled = true)
    {
        var element = new BoxElementViewModel
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            FillColor = fillColor,
            FillEnabled = fillEnabled,
            BorderColor = borderColor,
            BorderThickness = borderThickness,
            Opacity = opacity,
            CornerRadius = cornerRadius,
            GradientEnabled = gradientEnabled,
            GradientKind = gradientKind,
            // Same null-means-"use the VM's own default" fallback CreateOverlayElement's identical
            // Gradient*Color params already use -- a caller who never touches gradients at all
            // (every non-Phase-8 site) never has to know or care what these defaults are.
            GradientStartColor = gradientStartColor ?? new Rgb24(255, 0, 0),
            GradientEndColor = gradientEndColor ?? new Rgb24(0, 0, 255),
            // Perspective transform, item 3 -- required construction ORDER (round-1/round-3 plan-
            // review blockers): the 8 corners MUST be assigned before PerspectiveEnabled, or every
            // intermediate corner assignment would read a partially-populated quad through the
            // mode-switched X/Width getters (harmless here since nothing re-reads them mid-
            // initializer, but the wrong general habit to establish); PerspectiveEnabled MUST be
            // assigned before the undo-delegate lines below, or a real, undone restore's own
            // construction-time write would push a spurious undo step.
            Corner0X = corner0X,
            Corner0Y = corner0Y,
            Corner1X = corner1X,
            Corner1Y = corner1Y,
            Corner2X = corner2X,
            Corner2Y = corner2Y,
            Corner3X = corner3X,
            Corner3Y = corner3Y,
            PerspectiveEnabled = perspectiveEnabled,
            Z = z,
            Locked = locked,
            ImageWidth = CanvasDisplayWidth,
            ImageHeight = CanvasDisplayHeight,
            TargetModeHeightPx = _targetMode.ImageHeight,
            RemoveCommand = RemoveOverlayElementCommand,
            MoveUpCommand = MoveElementUpCommand,
            MoveDownCommand = MoveElementDownCommand,
            BringToFrontCommand = BringToFrontCommand,
            SendToBackCommand = SendToBackCommand,
            DuplicateCommand = DuplicateCommand,
            AlignSelectedElementToCropCommand = AlignSelectedElementToCropCommand,
            CopyCommand = CopySelectedElementCommand,
            CutCommand = CutSelectedElementCommand,
            PasteCommand = PasteElementCommand,
            FlattenCommand = FlattenElementCommand,
            CopyStyleCommand = CopySelectedElementStyleCommand,
            PasteStyleCommand = PasteSelectedElementStyleCommand,
            TogglePerspectiveCommand = TogglePerspectiveCommand,
            PushUndoSnapshotForGeometryChange = () => PushUndoSnapshotCoalesced("OverlayGeometry"),
            // Set LAST, after FillColor/BorderColor/BorderThickness/Opacity/CornerRadius above --
            // an object initializer assigns in listed order, so their own construction-time
            // assignment fires On*Changing while this is still null, avoiding a spurious undo push
            // from element creation itself (same ordering reasoning CreateOverlayElement's own
            // PushUndoSnapshotForStyleChange comment gives).
            PushUndoSnapshotForStyleChange = () => PushUndoSnapshotCoalesced("BoxStyle"),
        };
        element.PropertyChanged += OnOverlayElementPropertyChanged;
        // RenderWarpedPreview can't be assigned inside the object initializer above (it must close
        // over the constructed element itself, not in scope inside its own initializer) -- assigning
        // it triggers its own one-shot rebuild if PerspectiveEnabled is already true at this point
        // (BoxElementViewModel.RenderWarpedPreview's own setter, not a plain auto-property -- see its
        // doc comment for the real, found bug this fixes: a restored/loaded warped element would
        // otherwise stay silently invisible).
        element.RenderWarpedPreview = (w, h) => RenderWarpedElementPreview(element, w, h);
        RefreshElementPreviewMetrics(element);
        return element;
    }

    /// <summary>TX editor gap-items plan, item 3 -- shared by <see cref="CreateBoxElement"/>'s and
    /// <see cref="CreateImageElement"/>'s own <c>RenderWarpedPreview</c> delegate assignment. Builds
    /// the element's own <see cref="TemplateBoxElement"/>/<see cref="TemplateImageElement"/> from its
    /// CURRENT live state (Bounds/Perspective in the SAME space as its own X/Y/Width/Height -- this
    /// call reads properties fresh every time it's invoked, not a value captured when the delegate
    /// was assigned) and renders it via <see cref="_preparer"/>. NOT crop-projected (unlike
    /// <see cref="BuildImageTemplateElement"/>/<see cref="BuildBoxTemplateElement"/>) --
    /// <c>RenderWarpedElementPreview</c>'s own contract only needs the corners mutually consistent
    /// with the element's own Bounds, in ANY space.</summary>
    private BgraPixelBuffer RenderWarpedElementPreview(ITemplateElementViewModel element, int targetWidthPx, int targetHeightPx)
    {
        TemplateElement templateElement = element switch
        {
            ImageElementViewModel image => new TemplateImageElement(
                new PerspectiveCorners(image.Corner0X, image.Corner0Y, image.Corner1X, image.Corner1Y, image.Corner2X, image.Corner2Y, image.Corner3X, image.Corner3Y).ToBoundingBox(),
                Z: 0, image.Source, image.Fit,
                new PerspectiveCorners(image.Corner0X, image.Corner0Y, image.Corner1X, image.Corner1Y, image.Corner2X, image.Corner2Y, image.Corner3X, image.Corner3Y)),
            BoxElementViewModel box => new TemplateBoxElement(
                new PerspectiveCorners(box.Corner0X, box.Corner0Y, box.Corner1X, box.Corner1Y, box.Corner2X, box.Corner2Y, box.Corner3X, box.Corner3Y).ToBoundingBox(),
                Z: 0, box.FillColor, box.BorderColor, box.BorderThickness, box.Opacity, box.CornerRadius,
                box.GradientEnabled
                    ? new TextGradient(box.GradientKind, [new GradientColorStop(0f, box.GradientStartColor), new GradientColorStop(1f, box.GradientEndColor)])
                    : null,
                new PerspectiveCorners(box.Corner0X, box.Corner0Y, box.Corner1X, box.Corner1Y, box.Corner2X, box.Corner2Y, box.Corner3X, box.Corner3Y),
                box.FillEnabled),
            _ => throw new NotSupportedException($"Unrecognized {nameof(ITemplateElementViewModel)}: {element.GetType()}."),
        };
        var scaleY = ComputeTargetToCanvasScaleY();
        var displayHeight = element.Height * CanvasDisplayHeight;
        var styleHeight = scaleY > 0 && displayHeight > 0
            ? (_targetMode.ImageHeight / scaleY) * (targetHeightPx / displayHeight)
            : targetHeightPx;
        return _preparer.RenderWarpedElementPreview(templateElement, targetWidthPx, targetHeightPx, styleHeight);
    }

    /// <summary>Line counterpart to <see cref="CreateOverlayElement"/>/<see cref="CreateBoxElement"/>
    /// -- same wiring shape, TX editor gap-items plan (2026-09-01). Endpoint-parameterized (not
    /// center/width/height, matching every other element's own creation-factory shape) since a
    /// line's real geometry is the two endpoints. <b>Object initializer sets X1/Y1/X2/Y2 directly,
    /// deliberately NOT X/Y/Width/Height</b> (plan-review risk finding) -- unlike
    /// <see cref="BoxElementViewModel"/>, where X/Y/Width/Height ARE the real stored fields,
    /// <see cref="LineElementViewModel"/>'s X/Y/Width/Height are DERIVED get/set properties; assigning
    /// them here would run the derived setters against still-default/zero endpoints in
    /// object-initializer-assignment order, an unnecessary and undefined-feeling detour when the
    /// real fields are directly assignable.</summary>
    private LineElementViewModel CreateLineElement(
        double x1, double y1, double x2, double y2, Rgb24 strokeColor, double strokeThickness, double opacity, int z, bool locked)
    {
        var element = new LineElementViewModel
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            StrokeColor = strokeColor,
            StrokeThickness = strokeThickness,
            Opacity = opacity,
            Z = z,
            Locked = locked,
            ImageWidth = CanvasDisplayWidth,
            ImageHeight = CanvasDisplayHeight,
            TargetModeHeightPx = _targetMode.ImageHeight,
            RemoveCommand = RemoveOverlayElementCommand,
            MoveUpCommand = MoveElementUpCommand,
            MoveDownCommand = MoveElementDownCommand,
            BringToFrontCommand = BringToFrontCommand,
            SendToBackCommand = SendToBackCommand,
            DuplicateCommand = DuplicateCommand,
            AlignSelectedElementToCropCommand = AlignSelectedElementToCropCommand,
            CopyCommand = CopySelectedElementCommand,
            CutCommand = CutSelectedElementCommand,
            PasteCommand = PasteElementCommand,
            FlattenCommand = FlattenElementCommand,
            CopyStyleCommand = CopySelectedElementStyleCommand,
            PasteStyleCommand = PasteSelectedElementStyleCommand,
            // Set LAST, after X1/Y1/X2/Y2/StrokeColor/StrokeThickness/Opacity above -- same
            // "avoid a spurious undo push from element creation itself" ordering trick
            // CreateBoxElement's own identical comment documents.
            PushUndoSnapshotForGeometryChange = () => PushUndoSnapshotCoalesced("OverlayGeometry"),
            PushUndoSnapshotForStyleChange = () => PushUndoSnapshotCoalesced("LineStyle"),
        };
        element.PropertyChanged += OnOverlayElementPropertyChanged;
        RefreshElementPreviewMetrics(element);
        return element;
    }

    /// <summary>Image counterpart to <see cref="CreateOverlayElement"/>/<see cref="CreateBoxElement"/>
    /// -- same wiring shape, Phase 2 (spec/15-template-designer.md).</summary>
    private ImageElementViewModel CreateImageElement(
        double x, double y, double width, double height, IImageSource source, ImageFitMode fit, ImageSourceOrigin origin, int z, bool locked,
        bool isBackground = false, int naturalPixelWidth = 0, int naturalPixelHeight = 0,
        // TX editor gap-items plan, item 3 -- same additive convention as CreateBoxElement's own
        // identical trailing parameters; see that method's own comment for the required construction
        // order (corners before PerspectiveEnabled, PerspectiveEnabled before the undo delegate).
        bool perspectiveEnabled = false,
        double corner0X = 0, double corner0Y = 0, double corner1X = 0, double corner1Y = 0,
        double corner2X = 0, double corner2Y = 0, double corner3X = 0, double corner3Y = 0)
    {
        var element = new ImageElementViewModel(source)
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Fit = fit,
            Origin = origin,
            Corner0X = corner0X,
            Corner0Y = corner0Y,
            Corner1X = corner1X,
            Corner1Y = corner1Y,
            Corner2X = corner2X,
            Corner2Y = corner2Y,
            Corner3X = corner3X,
            Corner3Y = corner3Y,
            PerspectiveEnabled = perspectiveEnabled,
            Z = z,
            Locked = locked,
            IsBackground = isBackground,
            NaturalPixelWidth = naturalPixelWidth,
            NaturalPixelHeight = naturalPixelHeight,
            ImageWidth = CanvasDisplayWidth,
            ImageHeight = CanvasDisplayHeight,
            RemoveCommand = RemoveOverlayElementCommand,
            MoveUpCommand = MoveElementUpCommand,
            MoveDownCommand = MoveElementDownCommand,
            BringToFrontCommand = BringToFrontCommand,
            SendToBackCommand = SendToBackCommand,
            DuplicateCommand = DuplicateCommand,
            AlignSelectedElementToCropCommand = AlignSelectedElementToCropCommand,
            SetAsBackdropCommand = SetAsBackdropCommand,
            DemoteToBackgroundCommand = DemoteBackdropToBackgroundCommand,
            CopyCommand = CopySelectedElementCommand,
            CutCommand = CutSelectedElementCommand,
            PasteCommand = PasteElementCommand,
            FlattenCommand = FlattenElementCommand,
            FitCommand = SetSelectedImageFitCommand,
            FitToSafeAreaCommand = FitSelectedImageToSafeAreaCommand,
            ResetToOriginalSizeCommand = ResetImageElementToOriginalSizeCommand,
            TogglePerspectiveCommand = TogglePerspectiveCommand,
            PushUndoSnapshotForGeometryChange = () => PushUndoSnapshotCoalesced("OverlayGeometry"),
        };
        element.PropertyChanged += OnOverlayElementPropertyChanged;
        // See CreateBoxElement's own identical comment -- must be assigned post-construction, and
        // its own setter fixes the "restored already-warped element stays invisible" bug.
        element.RenderWarpedPreview = (w, h) => RenderWarpedElementPreview(element, w, h);
        return element;
    }

    /// <summary>Shared by the constructor's <see cref="EditorInitialState"/>-seeding block and
    /// <see cref="ApplyState"/> -- one place that knows how to turn a <see cref="RawElementSnapshot"/>
    /// back into a live element, rather than duplicating the type switch at both call sites.</summary>
    private ITemplateElementViewModel CreateElementFromSnapshot(RawElementSnapshot snapshot) => snapshot switch
    {
        RawTextElementSnapshot text => CreateOverlayElement(
            text.Text, text.X, text.Y, text.Width, text.Height, text.FontSizeRelative, text.Color, text.Z, text.Locked,
            text.FontFamily, text.StrokeColor, text.StrokeThickness,
            text.ShadowColor, text.ShadowOffsetX, text.ShadowOffsetY, text.RotationDegrees,
            text.GradientEnabled, text.GradientKind, text.GradientStartColor, text.GradientEndColor,
            text.Bold, text.Italic,
            text.StackColor, text.StackStepX, text.StackStepY,
            text.BitmapFillEnabled, text.BitmapFillSource, text.GrowToFillEnabled),
        RawBoxElementSnapshot box => CreateBoxElement(
            box.X, box.Y, box.Width, box.Height, box.FillColor, box.BorderColor, box.BorderThickness, box.Opacity, box.Z, box.Locked,
            box.CornerRadius,
            box.GradientEnabled, box.GradientKind, box.GradientStartColor, box.GradientEndColor,
            box.PerspectiveEnabled,
            box.Corner0X, box.Corner0Y, box.Corner1X, box.Corner1Y, box.Corner2X, box.Corner2Y, box.Corner3X, box.Corner3Y,
            box.FillEnabled),
        RawImageElementSnapshot image => CreateImageElement(
            image.X, image.Y, image.Width, image.Height, image.Source, image.Fit, image.Origin, image.Z, image.Locked, image.IsBackground,
            image.NaturalPixelWidth, image.NaturalPixelHeight,
            image.PerspectiveEnabled,
            image.Corner0X, image.Corner0Y, image.Corner1X, image.Corner1Y, image.Corner2X, image.Corner2Y, image.Corner3X, image.Corner3Y),
        // Endpoints (X1/Y1/X2/Y2), NOT the base X/Y/Width/Height -- RawLineElementSnapshot's own
        // doc comment: the base fields are derived-for-uniformity-on-write only, endpoints are the
        // sole truth on read.
        RawLineElementSnapshot line => CreateLineElement(
            line.X1, line.Y1, line.X2, line.Y2, line.StrokeColor, line.StrokeThickness, line.Opacity, line.Z, line.Locked),
        _ => throw new NotSupportedException($"Unrecognized {nameof(RawElementSnapshot)}: {snapshot.GetType()}."),
    };

    private bool CanSaveTemplate() => !string.IsNullOrWhiteSpace(NewTemplateName) && !IsSavingTemplate;

    /// <summary>Canvas right-click "Save Template" entry point (called from
    /// <c>OnSaveTemplateContextMenuClick</c>) -- user-reported gap (2026-09-15): the old behavior
    /// just focused an empty name field instead of saving, requiring the operator to type something
    /// first before the shortcut did anything. Now auto-fills the next free "tmp{N}" name (N starting
    /// at 1, case-insensitive against every existing template's real <see cref="ITemplateStore.ListAsync"/>
    /// name -- same source of truth and same case-insensitive match <see cref="SaveTemplateAsync"/>
    /// itself already uses for its overwrite-by-matching-name check) and saves immediately with that
    /// name. If a name was already typed, this is identical to the ordinary Save button -- the typed
    /// name always wins, auto-naming only fills a genuinely empty field.</summary>
    public async Task SaveTemplateWithAutoNameAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTemplateName))
        {
            try
            {
                var existingNames = (await _templateStore.ListAsync()).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var n = 1;
                while (existingNames.Contains($"tmp{n}"))
                {
                    n++;
                }

                NewTemplateName = $"tmp{n}";
            }
            catch (Exception ex)
            {
                Log.SaveTemplateFailed(_logger, "tmp<auto>", ex);
                StatusMessage = _localization.GetString("Panes.TxImageEditor.SaveTemplateFailed");
                return;
            }
        }

        if (SaveTemplateCommand.CanExecute(null))
        {
            await SaveTemplateCommand.ExecuteAsync(null);
        }
    }

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
        string? existingId = null;
        string? templateId = null;
        try
        {
            // Backlog item (auditor usability review, 2026-08-17): "Saving a template under an
            // existing name creates a duplicate entry, not an update/rename." CreateTemplateId
            // always mints a fresh guid8-suffixed id (see its own doc comment) -- saving under a
            // name that already exists reuses THAT existing id instead, so the save overwrites in
            // place (also preserves any pin referencing it, since pins are keyed by id).
            //
            // Tier B audit finding: this used to resolve existingId from ReadyRack.AllTemplates, a
            // separate VM's own in-memory projection populated by a FIRE-AND-FORGET RefreshAsync
            // call at editor-open time (TxControlsPaneViewModel's own OpenEditorWithLoadedSourceAsync/
            // EditCurrentImageAsync) -- saving before that refresh completes (or after it silently
            // swallowed a transient failure) read a stale, possibly-EMPTY list, so an overwrite of a
            // real existing template could silently degrade into creating a duplicate instead --
            // exactly the bug this whole feature exists to fix. _templateStore.ListAsync() is the
            // actual source of truth (same store SaveAsync/DeleteAsync below both write through).
            existingId = (await _templateStore.ListAsync()).FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;
            templateId = existingId ?? _templateStore.CreateTemplateId(name);

            // Tier B audit finding (blocker): this used to DeleteAsync(idToReplace) BEFORE writing
            // anything, then unconditionally DeleteAsync(templateId) again in the catch below -- on
            // the overwrite path both target the SAME existing id, so a save that fails ANY time
            // after the up-front delete (SaveAsync alone does a thumbnail render + 2 file writes,
            // any of which can throw: disk full, a network-backed MyPictures, an AV lock, a
            // permissions change) permanently destroyed the operator's PRE-EXISTING template with no
            // way to recover it -- StatusMessage just said "Save template failed." SaveAsync already
            // overwrites template.json/thumbnail.png in place (Directory.CreateDirectory is
            // idempotent), so no up-front delete is needed for the manifest/thumbnail at all. The
            // one thing the up-front delete bought -- not leaving orphaned GUID-named asset PNGs
            // behind from a PRIOR save of the same template (BuildPersistedElementAsync always mints
            // a fresh GUID filename, by design, never reusing/overwriting a prior asset in place) --
            // is deliberately accepted as a lesser, non-destructive trade-off (a slow accumulation of
            // unreferenced files in that one template's own assets/ folder across repeated
            // overwrite-saves) rather than risk deleting real, still-referenced data on a failure
            // path. A proper orphan sweep would need ITemplateStore to expose per-asset deletion,
            // which doesn't exist today and is out of scope for this fix.
            var elements = new List<PersistedTemplateElement>(OverlayElements.Count);
            foreach (var raw in RawOverlayElements)
            {
                elements.Add(await BuildPersistedElementAsync(templateId, raw));
            }

            await _templateStore.SaveAsync(templateId, name, new PersistedTemplateDocument(elements));
            NewTemplateName = string.Empty;
            StatusMessage = null;
            await ReadyRack.RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.SaveTemplateFailed(_logger, name, ex);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.SaveTemplateFailed");

            // existingId is null check preserved (blocker fix): only clean up a folder THIS call
            // freshly minted -- never delete a folder that pre-existed this save attempt. A failure
            // partway through the foreach above (e.g. WritePngAsync throws on element 2 of 3)
            // already wrote element 1's asset PNG under a brand-new templateId's own folder, with no
            // template.json ever referencing it -- ListAsync skips manifest-less folders, so that
            // asset is permanently invisible garbage, one new orphaned folder per failed save, with
            // no way for the operator to ever reach it via this app's own UI. DeleteAsync is safe to
            // call unconditionally on a NEW id: it no-ops if nothing was ever written (directory
            // doesn't exist), and removes the whole folder -- assets included -- if something
            // partial was. Best-effort: a cleanup failure here must not mask or replace the original
            // save failure already logged above.
            if (existingId is null && templateId is { } freshlyMintedId)
            {
                try
                {
                    await _templateStore.DeleteAsync(freshlyMintedId);
                }
                catch (Exception cleanupEx)
                {
                    Log.SaveTemplateCleanupFailed(_logger, freshlyMintedId, cleanupEx);
                }
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
                // TX editor gap-items plan, item 4b -- same GUID-based, never-index-derived asset
                // naming as the image case below, into the SAME shared assets/ folder (no new
                // per-kind subfolder). Degrades BitmapFillEnabled to false (rather than persisting a
                // dangling Enabled=true with no asset) when Enabled is set but Source is somehow null
                // -- shouldn't be reachable from the UI (picking a picture always sets both together),
                // defensive only.
                string? bitmapFillAssetFileName = null;
                if (text.BitmapFillEnabled && text.BitmapFillSource is { } bitmapFillSource)
                {
                    bitmapFillAssetFileName = $"{Guid.NewGuid():N}.png";
                    var bitmapFillAssetPath = _templateStore.GetAssetPath(templateId, bitmapFillAssetFileName);
                    await _imageSourceWriter.WritePngAsync(bitmapFillSource, bitmapFillAssetPath);
                }

                return new PersistedTextElement(
                    text.X, text.Y, text.Width, text.Height, text.Z, text.Locked,
                    // User-reported gap (2026-09-15): FontSizeRelative here used to be the pre-grow
                    // "set" size verbatim -- see ComputeFittedFontSizeRelative's own doc comment for
                    // why GrowToFillEnabled's actual visual RESULT needs to be baked in here instead,
                    // since the flag itself is deliberately not persisted below.
                    text.Text, ComputeFittedFontSizeRelative(text), text.Color, text.FontFamily, text.StrokeColor, text.StrokeThickness,
                    text.ShadowColor, text.ShadowOffsetX, text.ShadowOffsetY, text.RotationDegrees,
                    text.GradientEnabled, text.GradientKind, text.GradientStartColor, text.GradientEndColor,
                    text.Bold, text.Italic,
                    text.StackColor, text.StackStepX, text.StackStepY,
                    bitmapFillAssetFileName is not null, bitmapFillAssetFileName);
            case RawBoxElementSnapshot box:
                return new PersistedBoxElement(
                    box.X, box.Y, box.Width, box.Height, box.Z, box.Locked,
                    box.FillColor, box.BorderColor, box.BorderThickness, box.Opacity, box.CornerRadius,
                    box.GradientEnabled, box.GradientKind, box.GradientStartColor, box.GradientEndColor,
                    box.PerspectiveEnabled,
                    box.Corner0X, box.Corner0Y, box.Corner1X, box.Corner1Y, box.Corner2X, box.Corner2Y, box.Corner3X, box.Corner3Y,
                    box.FillEnabled);
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
                    ImageSourceKind.Clipboard => (PersistedImageSourceKind.Clipboard, image.Origin.Payload),
                    ImageSourceKind.Promoted => (PersistedImageSourceKind.Promoted, image.Origin.Payload),
                    _ => throw new NotSupportedException($"Unrecognized {nameof(ImageSourceKind)}: {image.Origin.Kind}."),
                };
                return new PersistedImageElement(
                    image.X, image.Y, image.Width, image.Height, image.Z, image.Locked,
                    assetFileName, image.Fit, originKind, originPayload, image.IsBackground,
                    image.NaturalPixelWidth, image.NaturalPixelHeight,
                    image.PerspectiveEnabled,
                    image.Corner0X, image.Corner0Y, image.Corner1X, image.Corner1Y, image.Corner2X, image.Corner2Y, image.Corner3X, image.Corner3Y);
            case RawLineElementSnapshot line:
                return new PersistedLineElement(
                    line.X, line.Y, line.Width, line.Height, line.Z, line.Locked,
                    line.X1, line.Y1, line.X2, line.Y2, line.StrokeColor, line.StrokeThickness, line.Opacity);
            default:
                throw new NotSupportedException($"Unrecognized {nameof(RawElementSnapshot)}: {raw.GetType()}.");
        }
    }

    /// <summary>Phase 5 -- calls <see cref="ITemplateStore.LoadAsync"/>, reconstructs every image
    /// element's <see cref="IImageSource"/> via the EXISTING <see cref="IImageFileLoader.LoadOriginalAsync"/>
    /// (same loader Phase 2's file source already uses), maps back to <see cref="RawElementSnapshot"/>s,
    /// and feeds them into <see cref="LoadTemplateIntoLiveEditor"/>.</summary>
    /// <summary>Returns whether the template was actually applied -- <see langword="false"/> means a
    /// newer selection superseded this one while it was loading (see the stale-generation branch
    /// below). Ready Rack direct-fire plan (2026-09-01): the return value lets
    /// <see cref="OnReadyRackDirectFireRequested"/> tell "loaded" from "discarded as stale" and
    /// abandon a fire cleanly rather than transmitting the losing slot's stale canvas -- the ORIGINAL
    /// caller (<see cref="OnReadyRackTemplateSelected"/>) still just awaits and ignores this, same as
    /// before this change (source-compatible).</summary>
    private async Task<bool> LoadTemplateAsync(string templateId, int generation)
    {
        var document = await _templateStore.LoadAsync(templateId);
        if (_disposed || generation != _templateLoadGeneration) return false;
        var snapshots = new List<RawElementSnapshot>(document.Elements.Count);
        foreach (var element in document.Elements)
        {
            snapshots.Add(await ToRawElementSnapshotAsync(templateId, element));
            if (_disposed || generation != _templateLoadGeneration) return false;
        }

        if (_disposed || generation != _templateLoadGeneration)
        {
            // A newer template selection has already started (and will apply ITS OWN result) since
            // this one began -- discard this stale load rather than clobber the canvas with an
            // out-of-date template.
            Log.TemplateLoadDiscardedAsStale(_logger, templateId);
            return false;
        }

        LoadTemplateIntoLiveEditor(snapshots);

        // UX friction fix (Fable operator-perspective review): pre-fills the Save-template name field
        // with the just-loaded template's OWN name, so a "tweak then re-save" workflow doesn't need
        // the operator to retype it from scratch -- SaveTemplateAsync's own existing
        // overwrite-by-matching-name lookup (right above) then does the rest. _templateStore.ListAsync()
        // is deliberately used here, not ReadyRack.AllTemplates -- SaveTemplateAsync's own doc comment
        // right above already documents why AllTemplates can be a stale, possibly-empty in-memory
        // projection (Tier B audit finding), and this same class of staleness would apply here too.
        //
        // Auditor code-review findings, all fixed here: (1) the generation re-check right below is
        // NOT redundant with the one above -- this ListAsync() await is itself a suspension point, so
        // a newer load (which bumps _templateLoadGeneration and sets its OWN NewTemplateName) can
        // finish first; without re-checking, this call's OWN continuation would resume afterward and
        // clobber the newer template's name onto the newer canvas, corrupting the next Save. (2) a
        // miss (loadedMetadata null -- a mid-flight rename/delete, or a manifest ListAsync's own
        // per-template catch skipped) now clears the field instead of leaving it holding whatever
        // OTHER template's name was there before, which SaveTemplateAsync's overwrite-by-matching-name
        // logic would otherwise silently apply to the wrong template on the next save. (3) this lookup
        // is cosmetic, not load-critical -- a failure here must not surface as "Load template failed"
        // for a load that in fact succeeded (OnReadyRackTemplateSelected's catch) or abort an
        // already-fired Ctrl+N direct-fire (OnReadyRackDirectFireRequested's catch), so it gets its
        // own try/catch and its own, accurately-worded log message instead of reusing LoadTemplateFailed.
        try
        {
            var loadedMetadata = (await _templateStore.ListAsync()).FirstOrDefault(t => t.Id == templateId);
            if (!_disposed && generation == _templateLoadGeneration)
            {
                NewTemplateName = loadedMetadata?.Name ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            Log.TemplateNamePrefillFailed(_logger, templateId, ex);
        }

        return !_disposed && generation == _templateLoadGeneration;
    }

    private async Task<RawElementSnapshot> ToRawElementSnapshotAsync(string templateId, PersistedTemplateElement element)
    {
        switch (element)
        {
            case PersistedTextElement text:
                // TX editor gap-items plan, item 4b -- same degrade-to-null-and-log, never-throw
                // shape as TemplateStore.LoadTextBitmapFillAsync's own thumbnail-path counterpart
                // (see that method's own doc comment for why): a missing/corrupt picture-fill asset
                // must not abort the WHOLE template load over one decorative field.
                IImageSource? bitmapFillSource = null;
                if (text.BitmapFillEnabled && !string.IsNullOrEmpty(text.BitmapFillAssetFileName))
                {
                    try
                    {
                        var bitmapFillAssetPath = _templateStore.GetAssetPath(templateId, text.BitmapFillAssetFileName);
                        bitmapFillSource = await _imageFileLoader.LoadOriginalAsync(bitmapFillAssetPath);
                    }
                    // Round-1 code-review finding: must not swallow OperationCanceledException --
                    // matches TemplateStore.LoadTextBitmapFillAsync's own identical fix.
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.BitmapFillAssetLoadFailed(_logger, templateId, text.BitmapFillAssetFileName, ex);
                    }
                }

                return new RawTextElementSnapshot(
                    text.X, text.Y, text.Width, text.Height, text.Z, text.Locked,
                    text.Text, text.FontSizeRelative, text.Color, text.FontFamily, text.StrokeColor, text.StrokeThickness,
                    text.ShadowColor, text.ShadowOffsetX, text.ShadowOffsetY, text.RotationDegrees,
                    text.GradientEnabled, text.GradientKind, text.GradientStartColor, text.GradientEndColor,
                    text.Bold, text.Italic,
                    text.StackColor, text.StackStepX, text.StackStepY,
                    bitmapFillSource is not null, bitmapFillSource);
            case PersistedBoxElement box:
                return new RawBoxElementSnapshot(
                    box.X, box.Y, box.Width, box.Height, box.Z, box.Locked,
                    box.FillColor, box.BorderColor, box.BorderThickness, box.Opacity, box.CornerRadius,
                    box.GradientEnabled, box.GradientKind, box.GradientStartColor, box.GradientEndColor,
                    box.PerspectiveEnabled,
                    box.Corner0X, box.Corner0Y, box.Corner1X, box.Corner1Y, box.Corner2X, box.Corner2Y, box.Corner3X, box.Corner3Y,
                    box.FillEnabled);
            case PersistedImageElement image:
                var assetPath = _templateStore.GetAssetPath(templateId, image.AssetFileName);
                var source = await _imageFileLoader.LoadOriginalAsync(assetPath);
                // Plan-review decision: a LOADED image element's Origin always points at its own
                // copied asset file, never whatever Kind/Payload it originally had when first saved
                // (a File/RxHistory/LastRx/Clipboard origin recorded on the persisted DTO is
                // informational only -- see PersistedImageElement's own doc comment for why none of
                // the four are safe to re-resolve from later).
                var origin = new ImageSourceOrigin(ImageSourceKind.File, assetPath);
                return new RawImageElementSnapshot(
                    image.X, image.Y, image.Width, image.Height, image.Z, image.Locked, source, image.Fit, origin, image.IsBackground,
                    // Pre-Phase-7 templates carry no natural size. Falling back to the ASSET's own
                    // dimensions is the honest value (the asset IS the pixels this element has) and
                    // needs no migration -- same additive convention IsBackground's own doc comment
                    // describes.
                    image.NaturalPixelWidth > 0 ? image.NaturalPixelWidth : source.Width,
                    image.NaturalPixelHeight > 0 ? image.NaturalPixelHeight : source.Height,
                    image.PerspectiveEnabled,
                    image.Corner0X, image.Corner0Y, image.Corner1X, image.Corner1Y, image.Corner2X, image.Corner2Y, image.Corner3X, image.Corner3Y);
            case PersistedLineElement line:
                return new RawLineElementSnapshot(
                    line.X, line.Y, line.Width, line.Height, line.Z, line.Locked,
                    line.X1, line.Y1, line.X2, line.Y2, line.StrokeColor, line.StrokeThickness, line.Opacity);
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
                // T0-11 (production_audit.md): every discarded ImageElementViewModel owns a
                // WriteableBitmap nothing else disposes -- this whole-collection discard is
                // separate from ImageElementViewModel.OnSourceChanged's own dispose-on-reassign
                // (that only covers a SURVIVING element's Source changing, not the element itself
                // being dropped). Widened to a generic IDisposable check (TX editor gap-items plan,
                // item 4b) -- OverlayElementViewModel now owns the same kind of WriteableBitmap when
                // a text element's own picture fill is set, for the same reason.
                if (element is IDisposable disposableElement)
                {
                    disposableElement.Dispose();
                }
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

    /// <summary>Backlog item (user request, 2026-08-17) -- context-menu quick-pick for
    /// <see cref="OverlayElementViewModel.FontSizeRelative"/>, same gate/shape as
    /// <see cref="InsertField"/> (text-only, <see cref="CanInsertField"/>). Unrecognized/null keys
    /// are a no-op (matches <see cref="SetTextColorPreset"/>'s own default-arm behavior) rather than
    /// throwing, since <c>CommandParameter</c> is caller-supplied XAML, not internal state.
    /// No explicit <see cref="PushUndoSnapshotCoalesced"/> call needed here (TX workflow
    /// modernization plan, Phase 1): the assignment below now goes through
    /// <see cref="OverlayElementViewModel.OnFontSizeRelativeChanging"/>, which pushes on its own --
    /// this used to be a real gap (no hook existed at all), closed once the Quick Style Flyout made
    /// this property a primary editing surface, not just a preset shortcut.</summary>
    [RelayCommand(CanExecute = nameof(CanInsertField))]
    private void SetFontSizePreset(string? sizeKey)
    {
        if (SelectedOverlayElement is not OverlayElementViewModel element)
        {
            return;
        }

        element.FontSizeRelative = sizeKey switch
        {
            "Small" => 0.06,
            "Medium" => 0.10,
            "Large" => 0.16,
            "XLarge" => 0.24,
            _ => element.FontSizeRelative,
        };
    }

    /// <summary>Backlog item (user request, 2026-08-17) -- context-menu quick-pick for
    /// <see cref="OverlayElementViewModel.Color"/>, same gate/shape/undo-consistency reasoning as
    /// <see cref="SetFontSizePreset"/>. A curated 6-swatch set (white/black/yellow/red/green/blue),
    /// not the full spectrum -- the TEXT STYLE tab's own <c>ColorPicker</c> stays the full-control
    /// path.</summary>
    [RelayCommand(CanExecute = nameof(CanInsertField))]
    private void SetTextColorPreset(string? colorKey)
    {
        if (SelectedOverlayElement is not OverlayElementViewModel element)
        {
            return;
        }

        element.Color = colorKey switch
        {
            "White" => new Rgb24(255, 255, 255),
            "Black" => new Rgb24(0, 0, 0),
            "Yellow" => new Rgb24(234, 179, 8),
            "Red" => new Rgb24(220, 38, 38),
            "Green" => new Rgb24(34, 197, 94),
            "Blue" => new Rgb24(59, 130, 246),
            _ => element.Color,
        };
    }

    partial void OnSelectedOverlayElementChanged(ITemplateElementViewModel? value)
    {
        // TX editor gap-items plan, item 2 (group-ops-lite, 2026-09-02) -- this hook is the SOLE
        // sync point between SelectedOverlayElement (the single "primary," unchanged in shape) and
        // SelectedOverlayElements (the new multi-selection set): a raw assignment (every existing
        // call site in this file, and every existing test that writes this property directly)
        // always means "select exactly this one" intent, so it collapses the set to match --
        // UNLESS this write came from SetSelection itself (guarded by _updatingSelectionSet, so a
        // real multi-element SetSelection([a, b, c]) doesn't get collapsed by its own trailing
        // primary-assignment). This is a STRUCTURAL guarantee, not a convention every call site has
        // to remember -- but only for a VALUE-CHANGING assignment: CommunityToolkit's generated
        // setter skips this hook entirely when the new value reference-equals the current one (no
        // Equals override on any element VM, confirmed before relying on it), so re-selecting an
        // ALREADY-primary element (e.g. clicking its own sidebar row while it's a 2+ group's
        // primary) does NOT re-fire this collapse -- call sites that need to force a collapse even
        // onto the current primary must go through the public SetSelection/SelectOnly path instead
        // of a raw assignment (see OnElementRowPointerPressed).
        if (!_updatingSelectionSet)
        {
            SelectedOverlayElements.Clear();
            if (value is not null)
            {
                SelectedOverlayElements.Add(value);
            }
        }

        // Backlog item (auditor usability review, 2026-08-17): ELEMENTS panel row highlight -- a
        // plain loop over every live element, same "parent pushes shared state down" convention as
        // every command on ITemplateElementViewModel, just a settable property instead of a command
        // (see IsSelected's own doc comment). O(n) in element count, which this editor's whole
        // undo/redo snapshot mechanism already treats as small/cheap (CaptureSnapshot copies the
        // full element list on every edit). Generalized (TX editor gap-items plan, item 2) from
        // "is this the one SelectedOverlayElement" to "is this a member of SelectedOverlayElements"
        // -- identical result whenever the set has 0-1 members (every pre-existing scenario).
        foreach (var element in OverlayElements)
        {
            element.IsSelected = SelectedOverlayElements.Contains(element);
        }

        if (value is not null)
        {
            SwitchToApplicableTabIfNeeded(value);
        }

        InsertFieldCommand.NotifyCanExecuteChanged();
        SetFontSizePresetCommand.NotifyCanExecuteChanged();
        SetTextColorPresetCommand.NotifyCanExecuteChanged();
        AddPlateBehindTextCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        CopySelectedElementCommand.NotifyCanExecuteChanged();
        CutSelectedElementCommand.NotifyCanExecuteChanged();
        CopySelectedElementStyleCommand.NotifyCanExecuteChanged();
        PasteSelectedElementStyleCommand.NotifyCanExecuteChanged();
        // TX editor gap-items plan, item 4b -- same "gated on a text element being selected" shape
        // as InsertFieldCommand above, needs the same refresh.
        PickTextBitmapFillFromFileCommand.NotifyCanExecuteChanged();
        PickTextBitmapFillFromClipboardCommand.NotifyCanExecuteChanged();
        ClearTextBitmapFillCommand.NotifyCanExecuteChanged();
        // Code-review finding: FontFamilyPickerItems/IsFontUnavailable MUST raise BEFORE
        // SelectedTextElement -- the Font ComboBox's ItemsSource is bound to
        // FontFamilyPickerItems and its SelectedItem (two-way) to SelectedTextElement.FontFamily.
        // Avalonia re-evaluates SelectedItem the moment SelectedTextElement's own change
        // notification fires; if ItemsSource is still the OLD list at that instant (an unavailable
        // font not yet included), SelectedItem resolves to no match and the two-way binding can
        // write that back, silently clobbering the very font name IsFontUnavailable exists to warn
        // about -- exactly the scenario this feature was built for (selecting a text element whose
        // font isn't bundled). Raising the ItemsSource-affecting properties first guarantees the
        // superset list (which always includes the current font, see FontFamilyPickerItems' own doc
        // comment) is already in place before SelectedItem gets re-evaluated.
        OnPropertyChanged(nameof(IsFontUnavailable));
        OnPropertyChanged(nameof(FontFamilyPickerItems));
        OnPropertyChanged(nameof(SelectedTextElement));
        OnPropertyChanged(nameof(SelectedImageElement));
        OnPropertyChanged(nameof(SelectedBoxElement));
        // Code-review finding: the same "check every sibling on a notification set" bug class this
        // project has hit repeatedly -- SelectedLineElement was added alongside SelectedBoxElement's
        // own declaration but never wired into this raise list, leaving the GEOMETRY tab's future
        // line-style block permanently stale on selection change.
        OnPropertyChanged(nameof(SelectedLineElement));
        OnPropertyChanged(nameof(SelectionReadoutText));
        OnPropertyChanged(nameof(SelectedTextElementFontSizePx));
        OnPropertyChanged(nameof(SelectedTextElementStrokeThicknessPx));
        OnPropertyChanged(nameof(SelectedTextElementShadowOffsetXPx));
        OnPropertyChanged(nameof(SelectedTextElementShadowOffsetYPx));
        OnPropertyChanged(nameof(SelectedTextElementStackStepXPx));
        OnPropertyChanged(nameof(SelectedTextElementStackStepYPx));
        OnPropertyChanged(nameof(SelectedBoxElementBorderThicknessPx));
        OnPropertyChanged(nameof(SelectedBoxElementCornerRadiusPx));
        OnPropertyChanged(nameof(SelectedElementLeftPx));
        OnPropertyChanged(nameof(SelectedElementTopPx));
        OnPropertyChanged(nameof(SelectedElementWidthPx));
        OnPropertyChanged(nameof(SelectedElementHeightPx));
        OnPropertyChanged(nameof(IsPerspectiveEligibleElementSelected));
        OnPropertyChanged(nameof(SelectedElementPerspectiveEnabled));
    }

    /// <summary>Phase 4 (spec/15-template-designer.md) -- <see cref="SelectedOverlayElement"/>
    /// narrowed to the TEXT case, or null when a box/image element (or nothing) is selected. Lets
    /// the TEXT STYLE panel's AXAML bind its whole content against this as a nested DataContext
    /// (font family/size/fill/stroke are all TEXT-only concepts) instead of every individual control
    /// needing its own <c>SelectedOverlayElement as OverlayElementViewModel</c> cast -- same
    /// "narrow once, bind against the narrowed type" shape <see cref="CanInsertField"/> already
    /// established for the insert-field chips.</summary>
    public OverlayElementViewModel? SelectedTextElement => SelectedOverlayElement as OverlayElementViewModel;

    /// <summary>Backlog item (user request, 2026-08-17): "text size should be in px not 0.1 or 0.16
    /// etc" -- the TEXT STYLE tab's SIZE field now shows/edits a real pixel count instead of the raw
    /// relative-to-height fraction, while <see cref="OverlayElementViewModel.FontSizeRelative"/>
    /// itself stays the underlying storage (deliberately mode-portable -- see its own doc comment: a
    /// template saved at one SSTV mode must still render correctly at another, so a plain rename to
    /// px storage would break that guarantee). Converts against the TARGET mode's own height, the
    /// same real-pixel convention <c>TransmitImagePreparer.DrawTemplateText</c>'s own
    /// <c>strokeThicknessPx = strokeThicknessRelative * imageHeightPx</c> already uses -- this is the
    /// size the text renders at in the TRANSMITTED image, not canvas-display pixels at the current
    /// zoom (<see cref="OverlayElementViewModel.CanvasFontSize"/> already covers that separate
    /// concern). Get/set both no-op safely when nothing is selected -- a bound TextBox still
    /// round-trips through this property even while its row is hidden by
    /// <c>SelectedTextElement</c>'s own null-check.</summary>
    public double SelectedTextElementFontSizePx
    {
        get => SelectedTextElement is { } text ? text.FontSizeRelative * _targetMode.ImageHeight : 0;
        set
        {
            if (SelectedTextElement is not { } text || _targetMode.ImageHeight <= 0)
            {
                return;
            }

            text.FontSizeRelative = value / _targetMode.ImageHeight;
        }
    }

    /// <summary>Backlog item (auditor usability review, 2026-08-17): "Stroke thickness / shadow
    /// offsets in TEXT STYLE are still raw relative fractions ... outline width shows '0.004' with no
    /// unit" -- same target-mode-height px conversion as <see cref="SelectedTextElementFontSizePx"/>,
    /// same reasoning (the size this actually renders at in the TRANSMITTED image, zoom-independent).</summary>
    public double SelectedTextElementStrokeThicknessPx
    {
        get => SelectedTextElement is { } text ? text.StrokeThickness * _targetMode.ImageHeight : 0;
        set
        {
            if (SelectedTextElement is not { } text || _targetMode.ImageHeight <= 0)
            {
                return;
            }

            text.StrokeThickness = value / _targetMode.ImageHeight;
        }
    }

    /// <inheritdoc cref="SelectedTextElementStrokeThicknessPx"/>
    public double SelectedTextElementShadowOffsetXPx
    {
        get => SelectedTextElement is { } text ? text.ShadowOffsetX * _targetMode.ImageHeight : 0;
        set
        {
            if (SelectedTextElement is not { } text || _targetMode.ImageHeight <= 0)
            {
                return;
            }

            text.ShadowOffsetX = value / _targetMode.ImageHeight;
        }
    }

    /// <inheritdoc cref="SelectedTextElementStrokeThicknessPx"/>
    public double SelectedTextElementShadowOffsetYPx
    {
        get => SelectedTextElement is { } text ? text.ShadowOffsetY * _targetMode.ImageHeight : 0;
        set
        {
            if (SelectedTextElement is not { } text || _targetMode.ImageHeight <= 0)
            {
                return;
            }

            text.ShadowOffsetY = value / _targetMode.ImageHeight;
        }
    }

    /// <inheritdoc cref="SelectedTextElementStrokeThicknessPx"/>
    public double SelectedTextElementStackStepXPx
    {
        get => SelectedTextElement is { } text ? text.StackStepX * _targetMode.ImageHeight : 0;
        set
        {
            if (SelectedTextElement is not { } text || _targetMode.ImageHeight <= 0)
            {
                return;
            }

            text.StackStepX = value / _targetMode.ImageHeight;
        }
    }

    /// <inheritdoc cref="SelectedTextElementStrokeThicknessPx"/>
    public double SelectedTextElementStackStepYPx
    {
        get => SelectedTextElement is { } text ? text.StackStepY * _targetMode.ImageHeight : 0;
        set
        {
            if (SelectedTextElement is not { } text || _targetMode.ImageHeight <= 0)
            {
                return;
            }

            text.StackStepY = value / _targetMode.ImageHeight;
        }
    }

    /// <summary>Backlog item (auditor usability review, 2026-08-17): "GEOMETRY X/Y/W/H are raw full-
    /// precision doubles, no px option" -- same conversion shape as <see cref="SelectedTextElementFontSizePx"/>,
    /// generalized to <see cref="SelectedOverlayElement"/> (the shared <see cref="ITemplateElementViewModel"/>
    /// interface, so this works for text/box/image alike, unlike the text-only SIZE field). Converted
    /// against <see cref="WorkingCopyWidth"/>/<see cref="WorkingCopyHeight"/> (zoom-independent native
    /// pixels), NOT <see cref="ITemplateElementViewModel.ImageWidth"/>/<c>ImageHeight</c> (those are
    /// the zoom-premultiplied CANVAS DISPLAY size -- typing a px value would drift with the zoom
    /// slider if this used those instead, the same zoom-independence <see cref="ZoomFactor"/>'s own
    /// doc comment establishes for undo/redo/snap/the pipeline). Left/Top (not center X/Y) since a
    /// px-based GEOMETRY field is most useful matching <see cref="ITemplateElementViewModel.LeftPixels"/>/
    /// <c>TopPixels</c>'s own top-left convention -- <see cref="ITemplateElementViewModel.X"/>/<c>Y</c>
    /// themselves stay center-anchored underneath (unchanged, see that interface's own doc comment),
    /// this is a display/edit-side conversion only, same as every other *Px property here.</summary>
    public double SelectedElementLeftPx
    {
        get => SelectedOverlayElement is { } element ? (element.X - (element.Width / 2)) * WorkingCopyWidth : 0;
        set
        {
            if (SelectedOverlayElement is not { } element || WorkingCopyWidth <= 0)
            {
                return;
            }

            element.X = (value / WorkingCopyWidth) + (element.Width / 2);
        }
    }

    /// <inheritdoc cref="SelectedElementLeftPx"/>
    public double SelectedElementTopPx
    {
        get => SelectedOverlayElement is { } element ? (element.Y - (element.Height / 2)) * WorkingCopyHeight : 0;
        set
        {
            if (SelectedOverlayElement is not { } element || WorkingCopyHeight <= 0)
            {
                return;
            }

            element.Y = (value / WorkingCopyHeight) + (element.Height / 2);
        }
    }

    /// <inheritdoc cref="SelectedElementLeftPx"/>
    public double SelectedElementWidthPx
    {
        get => SelectedOverlayElement is { } element ? element.Width * WorkingCopyWidth : 0;
        set
        {
            if (SelectedOverlayElement is not { } element || WorkingCopyWidth <= 0)
            {
                return;
            }

            element.Width = value / WorkingCopyWidth;
        }
    }

    /// <inheritdoc cref="SelectedElementLeftPx"/>
    public double SelectedElementHeightPx
    {
        get => SelectedOverlayElement is { } element ? element.Height * WorkingCopyHeight : 0;
        set
        {
            if (SelectedOverlayElement is not { } element || WorkingCopyHeight <= 0)
            {
                return;
            }

            element.Height = value / WorkingCopyHeight;
        }
    }

    /// <summary>Backlog item (auditor usability review, 2026-08-17): "Image elements' Fit mode isn't
    /// editable" -- same narrowed-cast shape as <see cref="SelectedTextElement"/>, for the GEOMETRY
    /// tab's new image-only Fit row.</summary>
    public ImageElementViewModel? SelectedImageElement => SelectedOverlayElement as ImageElementViewModel;

    /// <summary>Backlog item (auditor usability review, 2026-08-17): "Box elements have no style UI
    /// at all" -- same narrowed-cast shape as <see cref="SelectedTextElement"/>/<see cref="SelectedImageElement"/>,
    /// for the GEOMETRY tab's new box-only style block.</summary>
    public BoxElementViewModel? SelectedBoxElement => SelectedOverlayElement as BoxElementViewModel;

    /// <summary>TX editor gap-items plan (2026-09-01) -- same narrowed-cast shape as
    /// <see cref="SelectedBoxElement"/>, for the GEOMETRY tab's new line-only style block.</summary>
    public LineElementViewModel? SelectedLineElement => SelectedOverlayElement as LineElementViewModel;

    /// <summary>TX editor gap-items plan, item 3 (perspective transform) -- true when the currently
    /// selected element is an image or box AND supports perspective (both do). Backs the GEOMETRY
    /// tab's Perspective checkbox's own <c>IsVisible</c> -- one shared row rather than duplicating it
    /// inside BOTH the box-style and image-style blocks above.</summary>
    public bool IsPerspectiveEligibleElementSelected => SelectedOverlayElement is ImageElementViewModel or BoxElementViewModel;

    /// <summary>TX editor gap-items plan, item 3 -- read-only reflection of the selected element's
    /// own <c>PerspectiveEnabled</c> (not part of the shared <see cref="ITemplateElementViewModel"/>
    /// interface, so this narrows by type same as <see cref="SelectedBoxElement"/> above). The
    /// checkbox's own AXAML binds this ONE-WAY for its checked state and separately binds
    /// <see cref="ImageElementViewModel.TogglePerspectiveCommand"/> for the actual click action --
    /// NOT a plain two-way <c>IsChecked</c> binding, because enabling has a real side effect (seeding
    /// the 4 corners from the current bbox) only the command performs correctly; a bare two-way
    /// binding would flip the flag with no seed step. Re-raised from
    /// <see cref="OnSelectedOverlayElementChanged"/> (selection change) and
    /// <see cref="OnOverlayElementPropertyChanged"/> (the selected element's own flag flips).</summary>
    public bool SelectedElementPerspectiveEnabled => SelectedOverlayElement switch
    {
        ImageElementViewModel image => image.PerspectiveEnabled,
        BoxElementViewModel box => box.PerspectiveEnabled,
        _ => false,
    };

    /// <summary>Same target-mode-height px conversion as <see cref="SelectedTextElementStrokeThicknessPx"/>,
    /// for <see cref="BoxElementViewModel.BorderThickness"/> (same relative-to-image-height convention,
    /// see that property's own doc comment).</summary>
    public double SelectedBoxElementBorderThicknessPx
    {
        get => SelectedBoxElement is { } box ? box.BorderThickness * _targetMode.ImageHeight : 0;
        set
        {
            if (SelectedBoxElement is not { } box || _targetMode.ImageHeight <= 0)
            {
                return;
            }

            box.BorderThickness = value / _targetMode.ImageHeight;
        }
    }

    /// <summary>Auditor usability review follow-up (2026-08-18): same target-mode-height px
    /// conversion as <see cref="SelectedBoxElementBorderThicknessPx"/>, for
    /// <see cref="BoxElementViewModel.CornerRadius"/>.</summary>
    public double SelectedBoxElementCornerRadiusPx
    {
        get => SelectedBoxElement is { } box ? box.CornerRadius * _targetMode.ImageHeight : 0;
        set
        {
            if (SelectedBoxElement is not { } box || _targetMode.ImageHeight <= 0)
            {
                return;
            }

            box.CornerRadius = value / _targetMode.ImageHeight;
        }
    }

    /// <summary>The Fit ComboBox's own ItemsSource -- plain <c>Enum.GetValues</c> property, same
    /// pattern as <see cref="AvailableGradientKinds"/>.</summary>
    public IReadOnlyList<ImageFitMode> AvailableImageFitModes { get; } = Enum.GetValues<ImageFitMode>();

    /// <summary>Phase 4 -- the TEXT STYLE panel's font-family picker ItemsSource. Forwards
    /// <see cref="ITransmitImagePreparer.AvailableFontFamilies"/> rather than the VM hardcoding its
    /// own copy of what's bundled.</summary>
    public IReadOnlyList<string> AvailableFontFamilies => _preparer.AvailableFontFamilies;

    /// <summary>Phase 8 -- the TEXT STYLE panel's gradient-direction picker ItemsSource, same
    /// plain-`Enum.GetValues`-property pattern already established for enum ComboBoxes elsewhere in
    /// this codebase (e.g. <c>RadioStatusViewModel.AvailableModes</c>).</summary>
    public IReadOnlyList<TextGradientKind> AvailableGradientKinds { get; } = Enum.GetValues<TextGradientKind>();

    /// <summary>Phase 6 (spec/15-template-designer.md) -- true when the selected text element's own
    /// <see cref="OverlayElementViewModel.FontFamily"/> isn't one of <see cref="AvailableFontFamilies"/>
    /// (a cross-platform-shared or hand-edited template referencing a font this build doesn't bundle
    /// -- <see cref="ITransmitImagePreparer.ResolveFontFamily"/>'s own real pipeline behavior is to
    /// silently substitute the default font, with nothing today telling the operator that happened).
    /// Lives on the PARENT VM, not per-element -- <see cref="AvailableFontFamilies"/> itself already
    /// lives here, and the whole TEXT STYLE panel already binds <see cref="SelectedTextElement"/>
    /// paths off the parent (plan-review finding: pushing this onto every text element VM would be a
    /// new pattern, not the existing one).</summary>
    public bool IsFontUnavailable => SelectedTextElement is { } text && !AvailableFontFamilies.Contains(text.FontFamily);

    /// <summary>Phase 6 -- the Font `ComboBox`'s own `ItemsSource` (NOT <see cref="AvailableFontFamilies"/>
    /// directly). <see cref="ComboBox.SelectedItem"/> is two-way bound to
    /// <see cref="SelectedTextElement"/>'s own <c>FontFamily</c> -- if the bound value isn't present
    /// in `ItemsSource` at all, Avalonia resolves `SelectedItem` to no match, and a two-way binding
    /// can then write that resolved (non-)value straight back through, silently overwriting the
    /// element's real (if unavailable) font name before the operator ever sees the warning glyph
    /// (plan-review risk). Fix: always include the CURRENTLY selected font name in the list, even if
    /// it's not one of the real bundled families -- guarantees `SelectedItem` always has a match, so
    /// this binding can never silently mutate the underlying value regardless of the exact
    /// no-match resolution behavior. Deliberately a SEPARATE list from <see cref="AvailableFontFamilies"/>:
    /// <see cref="IsFontUnavailable"/>'s own check needs the real bundled set, not this
    /// display-safe superset.</summary>
    public IReadOnlyList<string> FontFamilyPickerItems =>
        SelectedTextElement is { } text && !AvailableFontFamilies.Contains(text.FontFamily)
            ? [.. AvailableFontFamilies, text.FontFamily]
            : AvailableFontFamilies;

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

    [RelayCommand]
    private void SelectTextStyleTab()
    {
        IsTextStyleTabSelected = true;
        IsGeometryTabSelected = false;
        IsImageTabSelected = false;
    }

    [RelayCommand]
    private void SelectGeometryTab()
    {
        IsTextStyleTabSelected = false;
        IsGeometryTabSelected = true;
        IsImageTabSelected = false;
    }

    [RelayCommand]
    private void SelectImageTab()
    {
        IsTextStyleTabSelected = false;
        IsGeometryTabSelected = false;
        IsImageTabSelected = true;
    }

    /// <summary>TX workflow modernization plan, Phase 2 -- called on plain selection change (NOT on
    /// new-element creation, which force-selects unconditionally instead, see each creation
    /// command's own call site). Geometry applies to every element type, so it's never switched away
    /// from here -- preserves the original "don't fight a user comparing positions" rationale
    /// (<see cref="IsTextStyleTabSelected"/>'s own doc comment) while fixing the common case: an
    /// inapplicable tab (Image while a text element is newly selected, or vice versa) switches to
    /// the one that actually applies. Box elements have no dedicated tab of their own -- their
    /// style lives in Geometry alongside position/size -- so Geometry is also their applicable tab.</summary>
    private void SwitchToApplicableTabIfNeeded(ITemplateElementViewModel element)
    {
        if (IsGeometryTabSelected)
        {
            return;
        }

        switch (element)
        {
            case OverlayElementViewModel when !IsTextStyleTabSelected:
                SelectTextStyleTab();
                break;
            case ImageElementViewModel when !IsImageTabSelected:
                SelectImageTab();
                break;
            case BoxElementViewModel:
            case LineElementViewModel:
                SelectGeometryTab();
                break;
        }
    }

    /// <summary>EditWindow redesign Phase 3, GEOMETRY tab -- aligns the selected element to one edge/
    /// center of the crop rect. <see cref="CropRect"/> is TOP-LEFT-anchored (<c>NormalizedRect.X/Y</c>
    /// is the left/top edge, confirmed via <see cref="CropLeftPixels"/>'s own
    /// <c>CropRect.X * CanvasDisplayWidth</c> formula) but element X/Y are CENTER-anchored
    /// (<see cref="ITemplateElementViewModel"/>'s own doc comment) -- the two conventions differ, so
    /// "align left" is <c>CropRect.X + element.Width / 2</c>, not a bare <c>CropRect.X</c> copy.
    /// A single undo step via <see cref="PushUndoSnapshot"/> -- Tier B audit finding: an earlier
    /// version of this doc comment claimed assigning "exactly one property per call" was enough to
    /// avoid a second push, but each element type's own <c>OnXChanging</c>/<c>OnYChanging</c> hook
    /// calls <see cref="ITemplateElementViewModel.PushUndoSnapshotForGeometryChange"/> regardless of
    /// property count -- <c>_suspendPreview</c> is what actually blocks the second push, same
    /// pattern <see cref="SetAsBackdrop"/> already established.</summary>
    [RelayCommand]
    private void AlignSelectedElementToCrop(string alignment)
    {
        if (SelectedOverlayElement is not { } element)
        {
            return;
        }

        PushUndoSnapshot();
        // Tier B audit finding: the X/Y assignments below used to run unguarded -- every element
        // type's own OnXChanging/OnYChanging hook calls PushUndoSnapshotForGeometryChange, which
        // isn't suppressed or coalesced against the PushUndoSnapshot() just above (that call's own
        // job is clearing _pendingCoalesceProperty, not preventing a second push), so a single Align
        // click pushed TWO undo steps for what visibly is one action -- the first Undo silently did
        // nothing, only the second actually moved the element back. _suspendPreview (same pattern
        // SetAsBackdrop/ApplySnappedElementBounds already use) blocks the per-property hook from
        // pushing its own step.
        _suspendPreview = true;
        try
        {
            switch (alignment)
            {
                case "Left":
                    element.X = CropRect.X + (element.Width / 2);
                    break;
                case "Center":
                    element.X = CropRect.X + (CropRect.Width / 2);
                    break;
                case "Right":
                    element.X = CropRect.X + CropRect.Width - (element.Width / 2);
                    break;
                case "Top":
                    element.Y = CropRect.Y + (element.Height / 2);
                    break;
                case "Middle":
                    element.Y = CropRect.Y + (CropRect.Height / 2);
                    break;
                case "Bottom":
                    element.Y = CropRect.Y + CropRect.Height - (element.Height / 2);
                    break;
            }
        }
        finally
        {
            _suspendPreview = false;
        }

        RecomputePreview();
    }

    /// <summary>TX workflow modernization plan, Phase 1 "Fit ▸" image-menu submenu -- same
    /// selection-implicit/string-parameter shape as <see cref="AlignSelectedElementToCrop"/> right
    /// above. <see cref="ImageElementViewModel.Fit"/> is a plain <c>[ObservableProperty]</c> with no
    /// geometry-coalesce hook of its own (unlike X/Y/Width/Height), so no <c>_suspendPreview</c>
    /// wrapping is needed here -- a single assignment is already a single undo step.</summary>
    [RelayCommand]
    private void SetSelectedImageFit(string mode)
    {
        if (SelectedOverlayElement is not ImageElementViewModel element || !Enum.TryParse<ImageFitMode>(mode, out var fit))
        {
            return;
        }

        PushUndoSnapshot();
        element.Fit = fit;
        RecomputePreview();
    }

    /// <summary>User-requested (2026-09-15): the safe-area guide's own normalized size, in the SAME
    /// canvas-normalized coordinate space <see cref="CropRect"/> and every element's own X/Y/Width/
    /// Height already live in (confirmed via <see cref="NudgeSelectedElements"/>'s own
    /// <c>dxPixels / WorkingCopyWidth</c> math and <see cref="AlignSelectedElementToCrop"/>'s own
    /// <c>CropRect.X + ...</c> formulas -- both only make sense if CropRect and element geometry share
    /// one space). <see cref="SafeAreaInsetPixels"/>/<see cref="SafeAreaWidthPixels"/> are the SAME
    /// computation in on-screen pixels; dividing by <see cref="WorkingCopyWidth"/>/
    /// <see cref="WorkingCopyHeight"/> instead of <see cref="CanvasDisplayWidth"/>/
    /// <see cref="CanvasDisplayHeight"/> cancels <see cref="ZoomFactor"/> out algebraically, same
    /// zoom-invariance reasoning <see cref="ProjectRectToCropRelative"/>'s own doc comment gives.
    /// Returns <see langword="false"/> for a degenerate result (working copy narrower/shorter than
    /// twice the inset) rather than a negative/zero size -- same guard
    /// <see cref="SafeAreaWidthPixels"/>'s own <c>Math.Max(0, ...)</c> represents in pixel space.</summary>
    private bool TryGetSafeAreaNormalizedSize(out double width, out double height)
    {
        width = height = 0;
        if (WorkingCopyWidth <= 0 || WorkingCopyHeight <= 0)
        {
            return false;
        }

        width = 1 - (2 * SafeAreaInsetWorkingCopyUnits / WorkingCopyWidth);
        height = 1 - (2 * SafeAreaInsetWorkingCopyUnits / WorkingCopyHeight);
        return width > 0 && height > 0;
    }

    private bool CanFitSelectedImageToSafeArea(string target) =>
        SelectedOverlayElement is ImageElementViewModel { Locked: false };

    /// <summary>User-requested (2026-09-15): "right click menu 'fit'... should have an option of
    /// 'fit safe area', fit width, fit height. Those options should resize the image area and image
    /// to the safe area size (or width or height)." A DIFFERENT concept from
    /// <see cref="SetSelectedImageFit"/> right above -- that changes how the image's own PIXELS fill
    /// its EXISTING bounds (Stretch/Contain/Cover); this changes the bounds (Width/Height/X/Y)
    /// themselves to match the safe-area guide's own size, centered, same
    /// "X=0.5,Y=0.5 full-frame convention" <see cref="SetAsBackdrop"/> already uses for its own
    /// exact-size assignment (no aspect-preserving scale here either -- <see cref="ImageElementViewModel.Fit"/>,
    /// set independently via the sibling submenu items, already owns how the pixels handle an
    /// arbitrary box). <paramref name="target"/> mirrors exactly which axis/axes the operator asked
    /// for -- "SafeArea" sets both Width/Height (and both X/Y); "Width"/"Height" touch only that one
    /// axis (size AND its own centering), leaving the other axis' current position/size untouched, a
    /// literal reading of "the safe area size (or width or height)."
    /// <para>Same Locked gate as <see cref="ResetImageElementToOriginalSize"/> -- a locked element's
    /// bounds are frozen by definition (see that command's own doc comment for the exact bug this
    /// prevents), in CanExecute, the body backstop below, and the MenuItem's own AXAML IsEnabled
    /// bind.</para></summary>
    [RelayCommand(CanExecute = nameof(CanFitSelectedImageToSafeArea))]
    private void FitSelectedImageToSafeArea(string target)
    {
        if (SelectedOverlayElement is not ImageElementViewModel { Locked: false } element)
        {
            return;
        }

        if (!TryGetSafeAreaNormalizedSize(out var safeWidth, out var safeHeight))
        {
            StatusMessage = _localization.GetString("Panes.TxImageEditor.FitToSafeAreaUnavailable");
            return;
        }

        PushUndoSnapshot();
        _suspendPreview = true;
        try
        {
            // Same "turn perspective off first, inside this same suspend window" reasoning as
            // ResetImageElementToOriginalSize/SetAsBackdrop's own identical fix.
            if (element.PerspectiveEnabled)
            {
                DisablePerspective(element);
            }

            switch (target)
            {
                case "SafeArea":
                    element.X = 0.5;
                    element.Y = 0.5;
                    element.Width = safeWidth;
                    element.Height = safeHeight;
                    break;
                case "Width":
                    element.X = 0.5;
                    element.Width = safeWidth;
                    break;
                case "Height":
                    element.Y = 0.5;
                    element.Height = safeHeight;
                    break;
            }
        }
        finally
        {
            _suspendPreview = false;
        }

        RecomputePreview();
    }

    // User-reported bug (2026-09-15): "when i set an image as backdrop and i pick the option 'reset
    // to original size' it gives an error but does not return as a non-backdropped image of the
    // previous size." Root cause: this had no Locked check at all, unlike every other
    // geometry-changing operation in this class (NudgeSelectedElements' own "if (element.Locked)
    // continue" is the established convention) -- a locked backdrop is defined as covering the whole
    // frame (X=0.5,Y=0.5,W=1,H=1, see SetAsBackdrop's own doc comment), so shrinking it to its
    // natural size left it in a broken, self-contradictory state: still Locked and IsBackground=true
    // (so still unreachable by normal drag, still treated as the frame's backdrop) but no longer
    // actually covering the frame. "Gives an error" was the (accurate but confusingly-worded, given
    // what actually happened) ResetToOriginalSizeFitted/ResetToOriginalSizeUnavailable status message
    // -- the real problem is this command running at all on a locked element, not its message text.
    private bool CanResetImageElementToOriginalSize(ImageElementViewModel? element) =>
        element is not null && !element.Locked && element.NaturalPixelWidth > 0 && element.NaturalPixelHeight > 0 && OverlayElements.Contains(element);

    /// <summary>TX workflow modernization plan, Phase 7. "Original size" means the element renders
    /// at its own NATURAL pixel count IN THE TRANSMITTED FRAME, which is not a division by the
    /// mode's dimensions: an element's rendered pixel width is
    /// <c>(Width / CropRect.Width) * contentWidth</c> (<see cref="ProjectRectToCropRelative"/>'s own
    /// <c>finalWidth * targetWidth</c>), so inverting that gives
    /// <c>Width = NaturalPixelWidth * CropRect.Width / contentWidth</c>. <c>contentWidth</c> is the
    /// LETTERBOXED content extent (<see cref="TryGetCropContentMetrics"/>), not
    /// <see cref="_targetMode"/>.ImageWidth -- using the mode dimension instead is wrong by exactly
    /// the pad factor under the default aspect-preserving resize.
    /// <para><b>Observable behavior, stated up front so it doesn't read as broken:</b> inserted
    /// image elements are capped at <see cref="WorkingCopyScaleFactor"/>x the mode's dimensions by
    /// <see cref="DownsampleToBudget"/>, and a real photo's NATURAL size is far larger than that
    /// again -- so for any real photo this lands in the clamp-to-frame branch, and the observable
    /// result is "fit to the crop frame at the image's natural aspect ratio," not a literal 1:1
    /// pixel mapping. The clamp is UNIFORM (one factor on both axes) so the natural aspect always
    /// survives it; the operator is told when it engaged.</para></summary>
    [RelayCommand(CanExecute = nameof(CanResetImageElementToOriginalSize))]
    private void ResetImageElementToOriginalSize(ImageElementViewModel? element)
    {
        // Body-level backstop, same "CanExecute alone isn't a hard gate for a direct Execute() call"
        // reasoning this class's other commands already document (e.g. QuickSelectMode).
        if (element is null || element.Locked || !OverlayElements.Contains(element) || element.NaturalPixelWidth <= 0 || element.NaturalPixelHeight <= 0)
        {
            return;
        }

        if (!TryGetCropContentMetrics(out _, out _, out var contentWidth, out var contentHeight) || contentWidth <= 0 || contentHeight <= 0)
        {
            StatusMessage = _localization.GetString("Panes.TxImageEditor.ResetToOriginalSizeUnavailable");
            return;
        }

        var width = element.NaturalPixelWidth * CropRect.Width / contentWidth;
        var height = element.NaturalPixelHeight * CropRect.Height / contentHeight;

        // Uniform clamp (same factor both axes) if natural size would exceed the crop frame --
        // preserves aspect ratio regardless of how much it engages. Floor matches
        // TxImageEditorPaneView.MinNormalizedElementSize (0.02) -- a separate, deliberately
        // identical constant on that class, not shared, same reasoning as that constant's own doc
        // comment gives for not sharing MinNormalizedCropSize either.
        const double minNormalizedElementSize = 0.02;
        var fit = Math.Min(1.0, Math.Min(CropRect.Width / width, CropRect.Height / height));
        var clamped = fit < 1.0;
        width = Math.Max(width * fit, minNormalizedElementSize);
        height = Math.Max(height * fit, minNormalizedElementSize);

        // One click, one undo step: each assignment below would otherwise push its own coalesced
        // step via OnWidthChanging/OnHeightChanging -- same _suspendPreview pattern
        // AlignSelectedElementToCrop/SetAsBackdrop already use for exactly this.
        PushUndoSnapshot();
        _suspendPreview = true;
        try
        {
            // TX editor gap-items plan, item 3 -- same "turn perspective off first, inside this same
            // suspend window" reasoning as SetAsBackdrop's own identical fix.
            if (element.PerspectiveEnabled)
            {
                DisablePerspective(element);
            }

            element.Width = width;
            element.Height = height;
        }
        finally
        {
            _suspendPreview = false;
        }

        Log.ResetImageElementToOriginalSize(_logger, element.NaturalPixelWidth, element.NaturalPixelHeight, clamped);
        StatusMessage = clamped ? _localization.GetString("Panes.TxImageEditor.ResetToOriginalSizeFitted") : null;
        RecomputePreview();
    }

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

    /// <summary>Everything <see cref="BakeElementIntoSource"/> needs, captured on the UI thread
    /// BEFORE the <see cref="Task.Run"/> offload -- that method is static and takes only this, so it
    /// can never read mutable VM state off the UI thread.</summary>
    private sealed record FlattenBakeRequest(
        IImageSource Source, NormalizedRect CropRect, bool PreserveAspect, TemplateElement Element,
        int ModeWidth, int ModeHeight, double PadX, double PadY, double ContentWidth, double ContentHeight);

    /// <summary>Rasterises ONE element into a copy of <paramref name="request"/>.Source, so the
    /// pipeline's own Crop-&gt;Resize reproduces it in the transmitted frame at the position/size
    /// <c>ApplyTemplate</c> was already drawing it. Returns null when the element does not intersect
    /// the transmitted frame at all (nothing to bake -- the caller reports it rather than silently
    /// deleting the element).
    /// <para><b>Why a uniformly-scaled copy of the TARGET FRAME, and not source space.</b>
    /// <see cref="ProjectRectToCropRelative"/> produces bounds normalized against the target frame,
    /// and every size-like element property (font size, stroke thickness, shadow/stack offsets,
    /// corner radius, border thickness) is relative to that frame's HEIGHT. A UNIFORM scale of that
    /// frame is therefore the one transform under which the existing projection stays valid with
    /// zero rewriting -- which is precisely why this reuses <c>ComposePreview</c> (which already
    /// consumes a <see cref="TemplateElement"/> built by <see cref="ProjectRectToCropRelative"/>)
    /// rather than re-deriving or re-purposing that projection outside the space it was defined
    /// for.</para>
    /// <para><b>Adjustments are NOT baked</b> and the sliders are NOT reset (settled after a design
    /// consult: baking current adjustments then zeroing the sliders was the original plan, but
    /// <c>ApplyAdjustments</c>'s Gaussian sharpen/denoise use an ABSOLUTE pixel sigma and its gamma
    /// is pointwise-nonlinear, so neither survives a change of raster scale -- baking them at any
    /// scale other than exactly 1x is not equivalent, and baking at exactly 1x would force the
    /// ENTIRE crop through a downscale/upscale round trip instead of just the element. Consequence,
    /// stated rather than hidden: after a flatten the baked pixels are part of the photo and the
    /// adjustment sliders apply to them from then on -- surfaced to the operator via
    /// <see cref="StatusMessage"/>, not silently. Identical output when the sliders are at
    /// identity.</para></summary>
    private static IImageSource? BakeElementIntoSource(ITransmitImagePreparer preparer, FlattenBakeRequest request)
    {
        // The crop's real source-pixel rect -- the SAME rounding Crop itself applies, shared, not
        // reimplemented (CropGeometry's own doc comment).
        var crop = CropGeometry.Measure(request.Source.Width, request.Source.Height, request.CropRect);

        // 1) Bake scale. The natural choice is "one baked frame pixel per source pixel of the crop":
        // the background then passes through the bake essentially unresampled and the element is
        // rasterized at native source resolution, so the pipeline's own single downscale is the only
        // resample either of them sees -- exactly as if the element had been part of the photo.
        var scale = Math.Max(1.0, Math.Max(crop.Width / request.ContentWidth, crop.Height / request.ContentHeight));
        var budgetScale = Math.Sqrt(MaxFlattenBakePixels / ((double)request.ModeWidth * request.ModeHeight));
        scale = Math.Min(scale, Math.Max(1.0, budgetScale));
        var elementExtentPx = Math.Max(request.Element.Bounds.Width * request.ModeWidth, request.Element.Bounds.Height * request.ModeHeight);
        if (elementExtentPx > 0)
        {
            scale = Math.Min(scale, Math.Max(1.0, TransmitImageLimits.MaxElementResizeDimensionPx / elementExtentPx));
        }

        // Floored at the mode's own dimensions: never bake BELOW transmit resolution.
        var bakeWidth = Math.Max(request.ModeWidth, (int)Math.Round(scale * request.ModeWidth));
        var bakeHeight = Math.Max(request.ModeHeight, (int)Math.Round(scale * request.ModeHeight));
        // The ACTUAL per-axis scales after integer rounding -- used for the extraction geometry
        // below rather than the requested `scale`, so a sub-pixel rounding difference between the
        // axes can't shift the extracted rect.
        var bakeScaleX = (double)bakeWidth / request.ModeWidth;
        var bakeScaleY = (double)bakeHeight / request.ModeHeight;

        // 2) The frame itself: the real pipeline, at bake resolution, with ONLY this element and
        // adjustments explicitly at identity. ComposePreview is the existing fused
        // Crop->Resize->(skip identity adjustments)->ApplyTemplate chain -- no new pipeline.
        var baked = preparer.ComposePreview(
            request.Source, request.CropRect, bakeWidth, bakeHeight, request.PreserveAspect,
            new ImageAdjustments(), new TemplateDocument(Name: null, [request.Element]));

        // 3) Element bounds -> CONTENT-relative fractions (letterbox padding removed). Computed from
        // the 1x pad/content metrics the projection itself produced; identical at any uniform scale.
        var left = ((request.Element.Bounds.X * request.ModeWidth) - request.PadX) / request.ContentWidth;
        var top = ((request.Element.Bounds.Y * request.ModeHeight) - request.PadY) / request.ContentHeight;
        var right = (((request.Element.Bounds.X + request.Element.Bounds.Width) * request.ModeWidth) - request.PadX) / request.ContentWidth;
        var bottom = (((request.Element.Bounds.Y + request.Element.Bounds.Height) * request.ModeHeight) - request.PadY) / request.ContentHeight;

        // 4) -> integer SOURCE-crop-relative write-back rect, rounded OUTWARD, margined, clamped.
        // Localising the write-back is what keeps every background pixel outside the element's own
        // box bit-identical to the original source -- a capped bake scale then degrades only the
        // photo directly under the element, never the whole frame.
        var writeLeft = Math.Clamp((int)Math.Floor(left * crop.Width) - FlattenWriteBackMarginPx, 0, crop.Width);
        var writeTop = Math.Clamp((int)Math.Floor(top * crop.Height) - FlattenWriteBackMarginPx, 0, crop.Height);
        var writeRight = Math.Clamp((int)Math.Ceiling(right * crop.Width) + FlattenWriteBackMarginPx, 0, crop.Width);
        var writeBottom = Math.Clamp((int)Math.Ceiling(bottom * crop.Height) + FlattenWriteBackMarginPx, 0, crop.Height);
        var writeWidth = writeRight - writeLeft;
        var writeHeight = writeBottom - writeTop;
        if (writeWidth <= 0 || writeHeight <= 0)
        {
            return null;
        }

        // 5) The same rect, mapped back into the BAKED frame's own pixel space (content offset by
        // the letterbox pad, both scaled by the actual bake scale).
        var patchLeft = Math.Clamp((int)Math.Round((request.PadX * bakeScaleX) + (writeLeft / (double)crop.Width * request.ContentWidth * bakeScaleX)), 0, bakeWidth - 1);
        var patchTop = Math.Clamp((int)Math.Round((request.PadY * bakeScaleY) + (writeTop / (double)crop.Height * request.ContentHeight * bakeScaleY)), 0, bakeHeight - 1);
        var patchRight = Math.Clamp((int)Math.Round((request.PadX * bakeScaleX) + (writeRight / (double)crop.Width * request.ContentWidth * bakeScaleX)), patchLeft + 1, bakeWidth);
        var patchBottom = Math.Clamp((int)Math.Round((request.PadY * bakeScaleY) + (writeBottom / (double)crop.Height * request.ContentHeight * bakeScaleY)), patchTop + 1, bakeHeight);

        var patch = preparer.CropPixels(baked, patchLeft, patchTop, patchRight - patchLeft, patchBottom - patchTop);
        // preserveAspect: false -- the patch must land on the write-back rect EXACTLY. Under a
        // stretch crop the two aspects legitimately differ, and letterboxing here would black-bar
        // the photo underneath.
        var fitted = preparer.Resize(patch, writeWidth, writeHeight, preserveAspect: false);
        return preparer.Composite(request.Source, fitted, crop.X + writeLeft, crop.Y + writeTop);
    }

    private bool _isFlattening;

    private bool CanFlattenElement(ITemplateElementViewModel? element) =>
        !_isFlattening && element is not null && OverlayElements.Contains(element);

    /// <summary>TX workflow modernization plan, Phase 7 -- rasterise one element into the photo and
    /// drop it from the document. Async + <see cref="Task.Run"/>-offloaded because the bake is a
    /// genuine full-frame render at up to <see cref="MaxFlattenBakePixels"/> -- a multi-second
    /// synchronous stall on the UI thread would be a real regression on this one gesture.
    /// <para>Every input the bake depends on is captured up front and RE-VALIDATED after the await:
    /// an offloaded compute means the operator can rotate, undo, re-crop or drag the element while
    /// it runs, and applying a stale bake would corrupt the image with no visible cause. Discarding
    /// is the correct response, not silently applying it. <see cref="PushUndoSnapshot"/>
    /// deliberately runs AFTER the await, at the real mutation point, so an intervening edit isn't
    /// swallowed into this step.</para></summary>
    [RelayCommand(CanExecute = nameof(CanFlattenElement))]
    private async Task FlattenElementAsync(ITemplateElementViewModel? element)
    {
        if (element is null || !OverlayElements.Contains(element))
        {
            return;
        }

        if (!TryGetCropContentMetrics(out var padX, out var padY, out var contentWidth, out var contentHeight))
        {
            StatusMessage = _localization.GetString("Panes.TxImageEditor.FlattenUnavailable");
            return;
        }

        var request = new FlattenBakeRequest(
            _originalSource, CropRect, PreserveAspect, BuildTemplateElement(element),
            _targetMode.ImageWidth, _targetMode.ImageHeight, padX, padY, contentWidth, contentHeight);
        var preparer = _preparer;

        IImageSource? flattened;
        _isFlattening = true;
        FlattenElementCommand.NotifyCanExecuteChanged();
        try
        {
            flattened = await Task.Run(() => BakeElementIntoSource(preparer, request));
        }
        catch (Exception ex)
        {
            Log.FlattenElementFailed(_logger, ex);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.FlattenFailed");
            return;
        }
        finally
        {
            _isFlattening = false;
            FlattenElementCommand.NotifyCanExecuteChanged();
        }

        if (flattened is null)
        {
            // Nothing of this element lands inside the transmitted frame. Deliberately NOT treated
            // as "flatten == delete": silently removing an element the operator can still see on the
            // canvas (outside the crop) would be a data-loss surprise.
            StatusMessage = _localization.GetString("Panes.TxImageEditor.FlattenOutsideFrame");
            return;
        }

        // Stale-result guard. Every one of these is an input the bake actually consumed:
        // TemplateElement is a record, so structural equality covers the element's geometry, text,
        // resolved macros, colours and effects in one comparison.
        if (!ReferenceEquals(_originalSource, request.Source)
            || !CropRect.Equals(request.CropRect)
            || PreserveAspect != request.PreserveAspect
            || !OverlayElements.Contains(element)
            || !BuildTemplateElement(element).Equals(request.Element))
        {
            Log.FlattenElementDiscardedAsStale(_logger);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.FlattenDiscardedStale");
            return;
        }

        Log.FlattenElementInvoked(_logger, element.GetType().Name);
        PushUndoSnapshot();
        _suspendPreview = true;
        try
        {
            element.PropertyChanged -= OnOverlayElementPropertyChanged;
            OverlayElements.Remove(element);
            // Widened to a generic IDisposable check (TX editor gap-items plan, item 4b) -- see
            // LoadTemplateIntoLiveEditor's own identical comment.
            if (element is IDisposable disposableElement)
            {
                disposableElement.Dispose();
            }

            // TX editor gap-items plan, item 2 (group-ops-lite) -- SHRINKS the selection if
            // `element` was a member, rather than the OLD "if it was the primary, null the whole
            // selection" check: that would have wrongly cleared every OTHER group member too when
            // the flattened element happened to be the primary, and would have left a stale,
            // DISPOSED element as a group member when it was a NON-primary member (this exact case
            // is why FlattenElementAsync specifically needs this, not just RemoveOverlayElement).
            if (SelectedOverlayElements.Contains(element))
            {
                SetSelection(SelectedOverlayElements.Where(e => !ReferenceEquals(e, element)));
            }

            // New generation: this source can never be reached from the old baseline by rotation,
            // so ApplyState must restore it by instance (see its own branch and _sourceBaseline's
            // doc comment). Baseline rotation is the CURRENT rotation -- the flattened image is
            // baked in whatever orientation the editor is in right now.
            _sourceBaseline = flattened;
            _sourceBaselineRotation = _rotationCount;
            ReplaceSourceAndWorkingCopy(flattened);
        }
        finally
        {
            _suspendPreview = false;
        }

        // Adjustment sliders are NOT reset and the adjustments were NOT baked -- see
        // BakeElementIntoSource's own doc comment. Output is identical when they're at identity;
        // when they aren't, the baked pixels are now subject to them, which the operator is told
        // rather than left to discover.
        StatusMessage = BuildAdjustments().IsIdentity
            ? null
            : _localization.GetString("Panes.TxImageEditor.FlattenAdjustmentsNowApply");

        RecomputePreview();
    }

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
    /// <see cref="SetAsBackdrop"/>'s own <c>Min(Z) - 1</c>, which is collision-free BY
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

        // _suspendPreview-guarded (same pattern as SetAsBackdrop/Rotate's own multi-element
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

        InsertClonedSnapshot(BuildRawSnapshot(selected));
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
            var snapshot = OffsetSnapshotForClone(BuildRawSnapshot(element), offset);
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
        var clone = InsertClonedSnapshot(BuildRawSnapshot(element));
        _pendingCoalesceProperty = "OverlayGeometry";
        return clone;
    }

    /// <summary>Backlog item (user request, 2026-08-17): in-editor Copy/Cut/Paste for canvas
    /// elements -- NOT the OS clipboard (no cross-app paste target exists for a
    /// <see cref="RawElementSnapshot"/>), a plain in-memory field, same scope as every other
    /// element-manipulation command in this file. Copy/Cut/Paste are wired to Ctrl/Cmd+C/X/V in
    /// <c>TxImageEditorPaneView.axaml.cs</c>'s <c>OnCanvasKeyDown</c>, the same handler as the
    /// existing Delete/arrow-key/Escape bindings.</summary>
    private RawElementSnapshot? _clipboardSnapshot;

    private bool CanCopyOrCutSelectedElement() => SelectedOverlayElement is not null;

    [RelayCommand(CanExecute = nameof(CanCopyOrCutSelectedElement))]
    private void CopySelectedElement()
    {
        if (SelectedOverlayElement is not { } selected)
        {
            return;
        }

        _clipboardSnapshot = BuildRawSnapshot(selected);
        PasteElementCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCopyOrCutSelectedElement))]
    private void CutSelectedElement()
    {
        if (SelectedOverlayElement is not { } selected)
        {
            return;
        }

        _clipboardSnapshot = BuildRawSnapshot(selected);
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
            _clipboardSnapshot = BuildRawSnapshot(pasted);
        }
    }

    /// <summary>TX workflow modernization plan, Phase 1 -- Copy/Paste Style. A SEPARATE clipboard
    /// field from <see cref="_clipboardSnapshot"/> (element copy/paste): copying an element's style
    /// must not clobber a pending element paste, and vice versa. Reuses <see cref="BuildRawSnapshot"/>
    /// wholesale rather than a second, narrower snapshot type -- <see cref="PasteSelectedElementStyle"/>
    /// picks out only the style-relevant fields when applying, so nothing new needs to track "which
    /// fields count as style" in two places. Gated to same-element-kind-only paste (text style onto
    /// text, box style onto box) -- deliberately, to avoid an ambiguous partial application if the
    /// two kinds' style fields don't line up (text has font/shadow/stack that box doesn't, box has
    /// fill/border/corner-radius that text doesn't -- both also carry gradient, box gradient fill
    /// added 2026-09-01). Image elements have no copyable "style"
    /// distinct from Fit (which already has its own quick-access submenu), so neither command is
    /// reachable for them.</summary>
    private RawElementSnapshot? _styleClipboardSnapshot;

    private bool CanCopySelectedElementStyle() => SelectedOverlayElement is OverlayElementViewModel or BoxElementViewModel or LineElementViewModel;

    [RelayCommand(CanExecute = nameof(CanCopySelectedElementStyle))]
    private void CopySelectedElementStyle()
    {
        if (SelectedOverlayElement is not (OverlayElementViewModel or BoxElementViewModel or LineElementViewModel))
        {
            return;
        }

        _styleClipboardSnapshot = BuildRawSnapshot(SelectedOverlayElement);
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
    /// assignment below would push its own step via that property's own change hook (where one
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
                    text.FontFamily = style.FontFamily;
                    text.FontSizeRelative = style.FontSizeRelative;
                    text.Color = style.Color;
                    text.StrokeColor = style.StrokeColor;
                    text.StrokeThickness = style.StrokeThickness;
                    text.ShadowColor = style.ShadowColor;
                    text.ShadowOffsetX = style.ShadowOffsetX;
                    text.ShadowOffsetY = style.ShadowOffsetY;
                    text.RotationDegrees = style.RotationDegrees;
                    text.GradientEnabled = style.GradientEnabled;
                    text.GradientKind = style.GradientKind;
                    // Same fallback convention as CreateOverlayElement's own default-red/blue pair --
                    // GradientStartColor/EndColor are non-nullable on the VM (they always drive a
                    // real gradient stop), the raw snapshot's nullability is only about whether
                    // gradient colors were ever customized away from that default.
                    text.GradientStartColor = style.GradientStartColor ?? new Rgb24(255, 0, 0);
                    text.GradientEndColor = style.GradientEndColor ?? new Rgb24(0, 0, 255);
                    text.Bold = style.Bold;
                    text.Italic = style.Italic;
                    text.StackColor = style.StackColor;
                    text.StackStepX = style.StackStepX;
                    text.StackStepY = style.StackStepY;
                    // TX editor gap-items plan, item 4b -- same "add every current field to THIS
                    // switch case or it silently drops" bug class the box-gradient case above already
                    // documents finding once. Source assigned BEFORE Enabled (round-2 code-review
                    // convention, matches every other write-both-together assignment in this file) --
                    // copies the resolved IImageSource handle directly, no re-upload/re-encode. Both
                    // unconditional (not gated on style.BitmapFillEnabled) so pasting a solid/gradient
                    // style onto a picture-filled text element correctly clears it, same as the
                    // Gradient fields immediately above already do for a solid-onto-gradient paste.
                    text.BitmapFillSource = style.BitmapFillSource;
                    text.BitmapFillEnabled = style.BitmapFillEnabled;
                    break;
                case (RawBoxElementSnapshot style, BoxElementViewModel box):
                    box.FillColor = style.FillColor;
                    // Legacy `.mtm` import -- same "add every current field to THIS switch case or it
                    // silently drops" bug class this case's own Gradient comment already documents.
                    box.FillEnabled = style.FillEnabled;
                    box.BorderColor = style.BorderColor;
                    box.BorderThickness = style.BorderThickness;
                    box.Opacity = style.Opacity;
                    box.CornerRadius = style.CornerRadius;
                    // Code-review finding (2026-09-01, box gradient fill): this case was missing
                    // Gradient entirely -- Copy Style on a gradient box then Paste Style silently
                    // produced a solid box, and pasting a solid box's style onto a gradient box left
                    // the gradient on. Same fallback convention as text's own case above.
                    box.GradientEnabled = style.GradientEnabled;
                    box.GradientKind = style.GradientKind;
                    box.GradientStartColor = style.GradientStartColor ?? new Rgb24(255, 0, 0);
                    box.GradientEndColor = style.GradientEndColor ?? new Rgb24(0, 0, 255);
                    break;
                case (RawLineElementSnapshot style, LineElementViewModel line):
                    line.StrokeColor = style.StrokeColor;
                    line.StrokeThickness = style.StrokeThickness;
                    line.Opacity = style.Opacity;
                    break;
            }
        }
        finally
        {
            _suspendPreview = false;
        }

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
                else if (token is "dist" or "bearing")
                {
                    // Tier B audit follow-up: {dist}/{bearing} resolve FROM the "his_grid" variable
                    // (MacroTextResolver.TryResolveDistanceBearing), not from a literal {his_grid}
                    // token -- without this, a template referencing ONLY {dist}/{bearing} (the DIST/
                    // BEARING chips' own real output) never got a fill-bar row at all, so the
                    // operator had no way to type HIS grid in and both tokens resolved to "" forever.
                    referenced.Add("his_grid");
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

        OnPropertyChanged(nameof(HasNoTemplateVariableRows));
        OnPropertyChanged(nameof(TemplateVariableCountText));
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
        // Tier B audit finding: this used to store an empty string unconditionally -- MacroTextResolver
        // only renders a token VERBATIM (e.g. "{his_call}") when its key is ABSENT from
        // _templateVariables; a PRESENT-but-empty key resolves to "", so blanking a fill-bar field
        // used to render "DE " instead of "DE {his_call}" -- the exact "DE " state
        // ClearTemplateVariablesCommand's own doc comment already classifies as a code-review
        // blocker for the bulk-clear path (easy to transmit by mistake). Two UI paths to the same
        // intent (clear one field vs. clear all) must produce the same result.
        if (value.Length == 0)
        {
            _templateVariables.Remove(key);
        }
        else
        {
            _templateVariables[key] = value;
        }
        // Moved here from RescanTemplateVariables (real-window finding, see that method's own
        // comment) -- this is now the ONLY place _templateVariables gains a new key, so it's the
        // only place that can flip CanClearTemplateVariables from false to true.
        ClearTemplateVariablesCommand.NotifyCanExecuteChanged();
        var token = $"{{{key}}}";
        // Tier B audit finding: {dist}/{bearing} resolve FROM the "his_grid" variable
        // (MacroTextResolver.TryResolveDistanceBearing), not from a literal {his_grid} token in the
        // element's own Text -- an element reading e.g. "DIST {dist}" contains no "{his_grid}"
        // substring at all, so the plain Contains(token) check below never matched it, leaving the
        // canvas TextBlock showing a stale distance/bearing after a his_grid fill-bar edit even
        // though RefreshOverlayElementCanvasStyles()/RecomputePreview() below both read
        // ResolvedText fresh and updated correctly -- a canvas-vs-preview divergence.
        var alsoRefreshDistanceBearing = key == "his_grid";
        foreach (var element in OverlayElements.OfType<OverlayElementViewModel>())
        {
            if (element.Text.Contains(token, StringComparison.Ordinal)
                || (alsoRefreshDistanceBearing && (element.Text.Contains("{dist}", StringComparison.Ordinal) || element.Text.Contains("{bearing}", StringComparison.Ordinal))))
            {
                element.NotifyResolvedTextChanged();
            }
        }

        RefreshOverlayElementCanvasStyles();
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

        RefreshOverlayElementCanvasStyles();
        RecomputePreview();
    }

    /// <summary>Macros help plan (2026-09-01), item B -- opens the same Macros reference window
    /// `Tools ▸ Macros` does, from the QSO FILL bar header, so an operator mid-template-edit doesn't
    /// have to remember a completely different menu exists. See <see cref="_macrosReferenceRequested"/>'s
    /// own doc comment for the unwired-in-tests contract.</summary>
    [RelayCommand]
    private void OpenMacrosReference() => _macrosReferenceRequested?.Invoke();

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

        // TX editor gap-items plan, item 2 (group-ops-lite) -- SHRINKS the selection if `element`
        // was a member, same shape as FlattenElementAsync's own identical fix (see that method's
        // own doc comment for why "if primary, null everything" is wrong for both a non-primary
        // member and a primary member of a 3+ group).
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

    [RelayCommand]
    private void Apply()
    {
        Log.ApplyInvoked(_logger, _targetMode.Id);
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
        Log.ApplyAndTransmitInvoked(_logger, _targetMode.Id);
        AppliedAndTransmitRequested?.Invoke(BuildFinalOutput());
    }

    /// <summary>Called by the parent at each of its own IsTransmitting/IsRunningLoopbackSelfTest
    /// toggle points, same convention it already follows for TransmitCommand/StopTransmitCommand/
    /// RunLoopbackSelfTestCommand -- CommunityToolkit does not auto-requery a CanExecute predicate
    /// that closes over another object's property.</summary>
    public void NotifyTransmitAvailabilityChanged() => ApplyAndTransmitCommand.NotifyCanExecuteChanged();

    private IImageSource BuildFinalOutput()
    {
        var cropped = _preparer.Crop(_originalSource, CropRect);
        var resized = _preparer.Resize(cropped, _targetMode.ImageWidth, _targetMode.ImageHeight, PreserveAspect);
        var adjusted = _preparer.ApplyAdjustments(resized, BuildAdjustments());
        return _preparer.ApplyTemplate(adjusted, BuildTemplateDocument());
    }

    /// <summary>Backlog item (auditor usability review, 2026-08-17): confirm before discarding
    /// unsaved edits. User-reported feedback (2026-09-15): the original arm/confirm shape (a second
    /// click on the SAME Cancel button) went stale the moment <see cref="ConfirmRequested"/>/
    /// <see cref="RequestConfirmAsync"/> were added for template recall -- migrated to the same real
    /// dialog rather than left as the one remaining "click twice" holdout in this class. A Cancel with
    /// nothing unsaved still cancels immediately (no confirmation needed for a no-op discard).</summary>
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
            if (HasUnsavedEdits)
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
                if (element is ImageElementViewModel { PerspectiveEnabled: true } warpedImage)
                {
                    RotateCorners(warpedImage);
                    continue;
                }

                if (element is BoxElementViewModel { PerspectiveEnabled: true } warpedBox)
                {
                    RotateCorners(warpedBox);
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
        _workingCopy = BuildWorkingCopy(newSource, _targetMode, _preparer);
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
        // an await-crossing operation still in flight when Dispose() runs (e.g. AddImageFromPathAsync/
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
        if (sender is ImageElementViewModel { PerspectiveEnabled: true } or BoxElementViewModel { PerspectiveEnabled: true }
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
        // Auditor-found blocker (Tier-0 audit follow-up, production_audit.md): same guard, same
        // reason, as NotifyWorkingCopyGeometryChanged's own -- this method reaches
        // RecomputePreviewPipeline()'s own _previewPool.Blit() call, reachable from await-crossing
        // operations (AddImageFromPathAsync, FlattenElementAsync, and others) still in flight when
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
            _workingCopy, CropRect, _targetMode.ImageWidth, _targetMode.ImageHeight, PreserveAspect,
            BuildAdjustments(), BuildTemplateDocument());
        PreviewImage = _previewPool.Blit(composited);
    }

    private TemplateDocument BuildTemplateDocument() => new(Name: null, OverlayElements.Select(BuildTemplateElement).ToList());

    private ImageAdjustments BuildAdjustments() => new(Brightness, Contrast, Saturation, Gamma, Sharpen, Denoise);

    private EditorSnapshot CaptureSnapshot() =>
        new(_rotationCount, CropRect, PreserveAspect, LockAspectToMode, BuildAdjustments(), RawOverlayElements, TemplateVariables,
            _sourceBaseline, _sourceBaselineRotation);

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
        RevertCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnsavedEdits));
        ReadyRack.SetCanvasDirty(IsDirtySinceLastTemplateLoad);
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
        RevertCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnsavedEdits));
        ReadyRack.SetCanvasDirty(IsDirtySinceLastTemplateLoad);
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

        // Backlog item (auditor usability review, 2026-08-17): any real edit disarms a pending
        // direct-fire overwrite confirmation -- see _pendingDirectFireTemplateId's own doc comment
        // for why a stale arm from long before a later, unrelated direct-fire would otherwise
        // silently skip its warning. Cancel itself has no arm state to disarm anymore -- it confirms
        // via a real dialog now, resolved synchronously in the operator's own click, not tracked
        // across later edits.
        _pendingDirectFireTemplateId = null;

        _undoStack.Add(CaptureSnapshot());
        if (_undoStack.Count > MaxUndoDepth)
        {
            _undoStack.RemoveAt(0);
        }

        _redoStack.Clear();
        _pendingCoalesceProperty = null; // a real, non-coalesced push always resets coalescing state
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnsavedEdits));
        ReadyRack.SetCanvasDirty(IsDirtySinceLastTemplateLoad);
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

        // Same disarm reasoning as PushUndoSnapshot's own -- see that method's own comment.
        _pendingDirectFireTemplateId = null;

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
        RevertCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnsavedEdits));
        ReadyRack.SetCanvasDirty(IsDirtySinceLastTemplateLoad);
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
        // Tier B audit finding: Undo/Redo (both funnel through here) never reset these two, unlike
        // PushUndoSnapshot/PushUndoSnapshotCoalesced, which both do -- an Undo/Redo IS a real state
        // change, same as any other edit, so it must disarm a pending direct-fire overwrite
        // confirmation (_pendingDirectFireTemplateId's own doc comment: "a stale arm from long
        // before would silently skip the warning on a LATER, unrelated direct-fire") and reset any
        // in-progress property-coalescing window. Without the latter reset specifically: Undo
        // landing inside an open coalescing window (e.g. Undo right after a slider drag, before that
        // drag's own coalesce window would naturally close) left _pendingCoalesceProperty pointing
        // at the now-reverted property, so the VERY NEXT edit to that same property silently
        // coalesced into the Undo's own restored snapshot instead of pushing a fresh step -- the
        // edit became invisibly non-undoable, with HasUnsavedEdits reading false while the document
        // was actually dirty.
        _pendingDirectFireTemplateId = null;
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
                bounds, text.Z, text.ResolvedText, new FontSpec(text.FontFamily, text.FontSizeRelative, text.Bold, text.Italic), text.Color,
                text.StrokeColor, text.StrokeThickness,
                text.ShadowColor, text.ShadowOffsetX, text.ShadowOffsetY, text.RotationDegrees,
                // Phase 8: the VM's own simplified 2-stop shape (GradientEnabled/Kind/Start/End)
                // composed into a real TextGradient only when actually enabled -- see
                // OverlayElementViewModel.GradientEnabled's own doc comment for why the VM doesn't
                // store a raw TextGradient directly.
                text.GradientEnabled
                    ? new TextGradient(text.GradientKind, [new GradientColorStop(0f, text.GradientStartColor), new GradientColorStop(1f, text.GradientEndColor)])
                    : null,
                text.StackColor, text.StackStepX, text.StackStepY,
                // TX editor gap-items plan, item 4b -- composed only when actually enabled, same
                // shape as Gradient above. Precedence when both are somehow true (see
                // TemplateTextElement.BitmapFill's own doc comment): the render pipeline's own
                // DrawTemplateText checks BitmapFill first, so composing both fields here is safe
                // regardless -- this switch doesn't need its own precedence logic.
                text.BitmapFillEnabled ? text.BitmapFillSource : null, text.GrowToFillEnabled),
            BoxElementViewModel box => BuildBoxTemplateElement(box, bounds),
            ImageElementViewModel image => BuildImageTemplateElement(image, bounds),
            // Deliberately NOT `bounds` (computed above from element.X/Y/Width/Height, which for a
            // line are DERIVED cascades of the endpoints, not independent geometry) -- each endpoint
            // is projected individually via ProjectRectToCropRelative(x, y, 0, 0) (round-2 plan-review,
            // confirmed correct: with width=height=0 the center-to-top-left conversion is a no-op in
            // every branch, so .X/.Y of the result IS the projected point), then the pipeline's own
            // Bounds is the ink-inflated box around those two projected points -- see
            // TemplateLineElement's own doc comment for why an un-inflated axis-aligned line would be
            // silently dropped by ApplyTemplate's shared skip rule. StrokeThickness passed straight
            // through unscaled, matching TemplateBoxElement.BorderThickness's own established
            // precedent just above (style-relative sizes are height-relative to the FINAL render, not
            // reprojected through crop scaling the way geometry is).
            LineElementViewModel line => BuildTemplateLineElement(line),
            _ => throw new NotSupportedException($"Unrecognized {nameof(ITemplateElementViewModel)}: {element.GetType()}."),
        };
    }

    /// <summary>TX editor gap-items plan, item 3 (perspective transform) -- projects each of the 4
    /// corners individually via <see cref="ProjectRectToCropRelative"/>(cx, cy, 0, 0), the EXACT
    /// technique <see cref="BuildTemplateLineElement"/>'s own endpoints already use (with
    /// width=height=0, the center-to-top-left conversion is a no-op in every branch, so `.X`/`.Y` of
    /// the result IS the projected point). Returns null when the element isn't perspective-enabled,
    /// so the caller falls back to the plain, non-perspective bounds.</summary>
    private PerspectiveCorners? TryProjectPerspectiveCorners(ITemplateElementViewModel element)
    {
        var raw = element switch
        {
            ImageElementViewModel { PerspectiveEnabled: true } image => new PerspectiveCorners(
                image.Corner0X, image.Corner0Y, image.Corner1X, image.Corner1Y, image.Corner2X, image.Corner2Y, image.Corner3X, image.Corner3Y),
            BoxElementViewModel { PerspectiveEnabled: true } box => new PerspectiveCorners(
                box.Corner0X, box.Corner0Y, box.Corner1X, box.Corner1Y, box.Corner2X, box.Corner2Y, box.Corner3X, box.Corner3Y),
            _ => (PerspectiveCorners?)null,
        };

        if (raw is not { } corners)
        {
            return null;
        }

        var p0 = ProjectRectToCropRelative(corners.Corner0X, corners.Corner0Y, 0, 0);
        var p1 = ProjectRectToCropRelative(corners.Corner1X, corners.Corner1Y, 0, 0);
        var p2 = ProjectRectToCropRelative(corners.Corner2X, corners.Corner2Y, 0, 0);
        var p3 = ProjectRectToCropRelative(corners.Corner3X, corners.Corner3Y, 0, 0);
        return new PerspectiveCorners(p0.X, p0.Y, p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y);
    }

    private TemplateBoxElement BuildBoxTemplateElement(BoxElementViewModel box, NormalizedRect naturalBounds)
    {
        var corners = TryProjectPerspectiveCorners(box);
        var gradient = box.GradientEnabled
            ? new TextGradient(box.GradientKind, [new GradientColorStop(0f, box.GradientStartColor), new GradientColorStop(1f, box.GradientEndColor)])
            : null;
        return new TemplateBoxElement(
            corners?.ToBoundingBox() ?? naturalBounds, box.Z, box.FillColor, box.BorderColor, box.BorderThickness, box.Opacity, box.CornerRadius, gradient, corners,
            box.FillEnabled);
    }

    private TemplateImageElement BuildImageTemplateElement(ImageElementViewModel image, NormalizedRect naturalBounds)
    {
        var corners = TryProjectPerspectiveCorners(image);
        return new TemplateImageElement(corners?.ToBoundingBox() ?? naturalBounds, image.Z, image.Source, image.Fit, corners);
    }

    private TemplateLineElement BuildTemplateLineElement(LineElementViewModel line)
    {
        var p1 = ProjectRectToCropRelative(line.X1, line.Y1, 0, 0);
        var p2 = ProjectRectToCropRelative(line.X2, line.Y2, 0, 0);
        var lineBounds = TemplateLineGeometry.ComputeInflatedBounds(
            p1.X, p1.Y, p2.X, p2.Y, line.StrokeThickness,
            imageWidthPx: _targetMode.ImageWidth, imageHeightPx: _targetMode.ImageHeight);
        return new TemplateLineElement(lineBounds, line.Z, p1.X, p1.Y, p2.X, p2.Y, line.StrokeColor, line.StrokeThickness, line.Opacity);
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
    /// CANVAS-DISPLAY (zoomed) pixel space as of the Phase 7 rearchitecture (previously working-copy
    /// space, before <c>ZoomFactor</c> existed) -- but the result stays zoom-INVARIANT regardless,
    /// since <c>Z</c> cancels algebraically: <c>contentWidth</c>/<c>contentHeight</c> (line ~2520/2521)
    /// scale by <c>Z</c> through <c>cropWidthPixels</c>/<c>cropHeightPixels</c> and by <c>1/Z</c>
    /// through <c>scaleX</c>/<c>scaleY</c>, so the returned NORMALIZED rect never carries a factor of
    /// <c>Z</c> -- correctly so, since this feeds <see cref="BuildTemplateElement"/> and the
    /// transmitted image must never depend on the operator's current canvas zoom. Also
    /// pixel-space-invariant up to <see cref="BuildWorkingCopy"/>'s own integer rounding (code-review
    /// nit: it depends only
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

        if (!TryGetCropContentMetrics(out var padX, out var padY, out var contentWidth, out var contentHeight))
        {
            return new NormalizedRect(relX - (relWidth / 2), relY - (relHeight / 2), relWidth, relHeight);
        }

        var targetWidth = (double)_targetMode.ImageWidth;
        var targetHeight = (double)_targetMode.ImageHeight;

        var finalCenterX = (padX + (relX * contentWidth)) / targetWidth;
        var finalCenterY = (padY + (relY * contentHeight)) / targetHeight;
        var finalWidth = relWidth * contentWidth / targetWidth;
        var finalHeight = relHeight * contentHeight / targetHeight;

        return new NormalizedRect(finalCenterX - (finalWidth / 2), finalCenterY - (finalHeight / 2), finalWidth, finalHeight);
    }

    /// <summary>TX workflow modernization plan, Phase 7 -- the letterbox/stretch metrics
    /// <see cref="ProjectRectToCropRelative"/> derives, in TARGET-MODE pixel space, extracted so the
    /// flatten command reads the SAME numbers the projection itself uses rather than recomputing
    /// them (a formula re-derived at a second call site is exactly the defect class that already hit
    /// this method once -- a proven function's math silently drifting from a second copy). Returns
    /// false in the two cases <see cref="ProjectRectToCropRelative"/> itself early-returns for
    /// (degenerate <see cref="CropRect"/>, degenerate crop pixel size) -- callers must treat false as
    /// "no valid projection exists right now," not substitute a fallback. Zoom-invariant, for the
    /// same reason <see cref="ProjectRectToCropRelative"/>'s own doc comment gives:
    /// <see cref="CropWidthPixels"/>/<see cref="CropHeightPixels"/> carry a factor of
    /// <see cref="ZoomFactor"/> and <c>scaleX</c>/<c>scaleY</c> carry <c>1/ZoomFactor</c>, so the
    /// products below carry none.</summary>
    private bool TryGetCropContentMetrics(out double padX, out double padY, out double contentWidth, out double contentHeight)
    {
        padX = padY = contentWidth = contentHeight = 0;

        if (CropRect.Width <= 0 || CropRect.Height <= 0)
        {
            return false;
        }

        var cropWidthPixels = CropWidthPixels;
        var cropHeightPixels = CropHeightPixels;
        if (cropWidthPixels <= 0 || cropHeightPixels <= 0)
        {
            return false;
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

        contentWidth = cropWidthPixels * scaleX;
        contentHeight = cropHeightPixels * scaleY;
        padX = (targetWidth - contentWidth) / 2;
        padY = (targetHeight - contentHeight) / 2;
        return true;
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
    /// <summary>Backlog item (user request, 2026-08-17) -- factored out of
    /// <see cref="ComputeCanvasFontSize"/> so <see cref="ComputeCanvasStrokeThicknessPixels"/> (the
    /// canvas-preview outline fix) can convert a target-mode-pixel quantity to canvas-display pixels
    /// the identical way, instead of a second, driftable copy of this same formula. Returns 0 (an
    /// already-guarded "no valid scale" sentinel both callers already check for) when the crop has
    /// zero area.</summary>
    private double ComputeTargetToCanvasScaleY()
    {
        var cropWidthPixels = CropWidthPixels;
        var cropHeightPixels = CropHeightPixels;
        if (cropWidthPixels <= 0 || cropHeightPixels <= 0)
        {
            return 0;
        }

        var targetWidth = (double)_targetMode.ImageWidth;
        var targetHeight = (double)_targetMode.ImageHeight;
        return PreserveAspect
            ? Math.Min(targetWidth / cropWidthPixels, targetHeight / cropHeightPixels)
            : targetHeight / cropHeightPixels;
    }

    private void RefreshElementPreviewMetrics(ITemplateElementViewModel element)
    {
        var scaleY = ComputeTargetToCanvasScaleY();
        if (scaleY <= 0 || !double.IsFinite(scaleY))
        {
            return;
        }

        var scaleX = PreserveAspect ? scaleY : _targetMode.ImageWidth / CropWidthPixels;
        var bounds = ProjectRectToCropRelative(element.X, element.Y, element.Width, element.Height);
        // Rotated text is painted into an element-local bitmap before rotation in the final
        // renderer; its pattern phase starts there, rather than at the full image origin.
        var localPattern = element is OverlayElementViewModel textElement
            && double.IsFinite(textElement.RotationDegrees) && textElement.RotationDegrees % 360 != 0;
        var metrics = new ElementPreviewMetrics(_targetMode.ImageHeight / scaleY,
            new Avalonia.Size(1 / scaleX, 1 / scaleY),
            localPattern ? default : new Avalonia.Point(bounds.X * _targetMode.ImageWidth, bounds.Y * _targetMode.ImageHeight));
        switch (element)
        {
            case OverlayElementViewModel text: text.PreviewMetrics = metrics; break;
            case BoxElementViewModel box: box.PreviewMetrics = metrics; break;
            case LineElementViewModel line: line.PreviewMetrics = metrics; break;
        }
    }

    private double ComputeCanvasFontSize(OverlayElementViewModel element)
    {
        var scaleY = ComputeTargetToCanvasScaleY();
        if (scaleY <= 0)
        {
            return 0;
        }

        var targetWidth = (double)_targetMode.ImageWidth;
        var targetHeight = (double)_targetMode.ImageHeight;
        var bounds = ProjectRectToCropRelative(element.X, element.Y, element.Width, element.Height);
        var boundsWidthPx = Math.Max(1, (int)Math.Round(bounds.Width * targetWidth));
        var boundsHeightPx = Math.Max(1, (int)Math.Round(bounds.Height * targetHeight));
        // FontSpec.Family + the trailing stroke-thickness argument fixed (Phase 4 plan-review
        // blocker) -- an empty family/omitted stroke here would measure against the WRONG font/
        // without the stroke fit-box allowance while the real pipeline (BuildTemplateElement) uses
        // the actually-selected ones, silently desyncing the canvas from the transmitted image.
        var strokeThicknessRelative = element.StrokeColor is { } ? element.StrokeThickness : 0;
        // Phase 8: shadow offset/rotation are the THIRD occurrence of this same fit-box-shrink
        // requirement (see MeasureFittedFontSize's own doc comment) -- omitting them here would
        // desync the canvas-side CanvasFontSize from what DrawTemplateText actually fits/renders,
        // the exact bug class this whole method exists to prevent.
        var shadowOffsetXRelative = element.ShadowColor is { } ? element.ShadowOffsetX : 0;
        var shadowOffsetYRelative = element.ShadowColor is { } ? element.ShadowOffsetY : 0;
        // Auditor usability review follow-up (2026-08-18): Bold/Italic are the FOURTH occurrence of
        // this same fit-box-measurement desync class -- Bold glyphs measure wider than Regular at
        // the same point size, so omitting them here would fit a size against the WRONG (narrower)
        // measurement while DrawTemplateText actually draws bold, the identical bug this method's
        // own doc comment already describes for family/stroke/shadow/rotation. StackStepX/Y are the
        // FIFTH occurrence, same reasoning, gated on StackColor the same way shadow is gated above.
        var stackStepXRelative = element.StackColor is { } ? element.StackStepX : 0;
        var stackStepYRelative = element.StackColor is { } ? element.StackStepY : 0;
        var fittedFinalSizePx = _preparer.MeasureFittedFontSize(
            element.ResolvedText, new FontSpec(element.FontFamily, element.FontSizeRelative, element.Bold, element.Italic), (int)Math.Round(targetHeight),
            boundsWidthPx, boundsHeightPx, strokeThicknessRelative,
            shadowOffsetXRelative, shadowOffsetYRelative, element.RotationDegrees,
            stackStepXRelative, stackStepYRelative, element.GrowToFillEnabled);

        // [Code-review blocker, fixed here] NO extra * ZoomFactor -- zoom already arrives here
        // implicitly, through scaleY: cropHeightPixels (both PreserveAspect and stretch branches
        // above) is CropHeightPixels, which is now CanvasDisplayHeight-based (= WorkingCopyHeight *
        // ZoomFactor) per the Phase 7 rearchitecture, so scaleY is proportional to 1/ZoomFactor
        // already. fittedFinalSizePx itself is zoom-invariant (ProjectRectToCropRelative's own bounds
        // and _targetMode's dimensions carry no Z, per that method's own doc comment). An earlier
        // version of this line multiplied by ZoomFactor AGAIN on top of that -- a real Z^2 bug, caught
        // by code-review, not by the one manual test that happened to run at exactly 1.0 zoom (where
        // Z^2 == Z == 1 hides the error completely).
        return fittedFinalSizePx / scaleY;
    }

    /// <summary>Save-time counterpart to <see cref="ComputeCanvasFontSize"/> above -- user-reported
    /// gap (2026-09-15): <see cref="OverlayElementViewModel.GrowToFillEnabled"/> is deliberately NOT
    /// persisted (see that property's own doc comment), so a template saved with a grown font
    /// reloaded back at its original, small, pre-grow <see cref="OverlayElementViewModel.FontSizeRelative"/>
    /// -- the grow was always recomputed live (here and in <c>TransmitImagePreparer.ApplyTemplate</c>),
    /// never baked into the one field that actually survives a save. Runs the identical fit search
    /// <see cref="ComputeCanvasFontSize"/> does, but against the TARGET mode's real image height (the
    /// same unit <see cref="OverlayElementViewModel.FontSizeRelative"/> is itself relative to), not
    /// canvas-display pixels -- so the persisted value already reflects wherever GrowToFillEnabled
    /// left it, whether that call site itself survives the round trip or not. Also correct when
    /// GrowToFillEnabled is false: the search is then shrink-only, so the result is never larger than
    /// <see cref="RawTextElementSnapshot.FontSizeRelative"/> -- calling this unconditionally for
    /// every text element at save time is safe, not just for grown ones. Takes the RAW (unresolved,
    /// e.g. still containing "%m") snapshot text, matching what <see cref="RawTextElementSnapshot.Text"/>/
    /// <see cref="ScanlineStudio.Application.PersistedTextElement.Text"/> both already store and what
    /// a future load will re-measure against -- using today's live <c>ResolvedText</c> (e.g. today's
    /// specific callsign) would bake in a size fitted to a string length a future QSO's callsign
    /// won't match.</summary>
    private double ComputeFittedFontSizeRelative(RawTextElementSnapshot text)
    {
        var targetHeight = (double)_targetMode.ImageHeight;
        if (targetHeight <= 0)
        {
            return text.FontSizeRelative;
        }

        var bounds = ProjectRectToCropRelative(text.X, text.Y, text.Width, text.Height);
        var boundsWidthPx = Math.Max(1, (int)Math.Round(bounds.Width * _targetMode.ImageWidth));
        var boundsHeightPx = Math.Max(1, (int)Math.Round(bounds.Height * targetHeight));
        var strokeThicknessRelative = text.StrokeColor is { } ? text.StrokeThickness : 0;
        var shadowOffsetXRelative = text.ShadowColor is { } ? text.ShadowOffsetX : 0;
        var shadowOffsetYRelative = text.ShadowColor is { } ? text.ShadowOffsetY : 0;
        var stackStepXRelative = text.StackColor is { } ? text.StackStepX : 0;
        var stackStepYRelative = text.StackColor is { } ? text.StackStepY : 0;
        var fittedFinalSizePx = _preparer.MeasureFittedFontSize(
            text.Text, new FontSpec(text.FontFamily, text.FontSizeRelative, text.Bold, text.Italic), (int)Math.Round(targetHeight),
            boundsWidthPx, boundsHeightPx, strokeThicknessRelative,
            shadowOffsetXRelative, shadowOffsetYRelative, text.RotationDegrees,
            stackStepXRelative, stackStepYRelative, text.GrowToFillEnabled);

        return fittedFinalSizePx / targetHeight;
    }

    /// <summary>Backlog item (user request, 2026-08-17) -- canvas-preview outline fix. Real-pixel
    /// formula matches <c>TransmitImagePreparer.DrawTemplateText</c>'s own
    /// <c>strokeThicknessPx = strokeThicknessRelative * imageHeightPx</c> exactly (against the
    /// TARGET mode's height, not the crop's), then converted to canvas-display pixels via the same
    /// <see cref="ComputeTargetToCanvasScaleY"/> <see cref="ComputeCanvasFontSize"/> already uses --
    /// keeps the canvas stroke width in the same pixel space as <see cref="OverlayElementViewModel.CanvasFontSize"/>.
    /// 0 when <see cref="OverlayElementViewModel.StrokeColor"/> is null (no outline) or the scale is
    /// invalid, matching <see cref="ComputeCanvasFontSize"/>'s own zero-sentinel convention.</summary>
    private double ComputeCanvasStrokeThicknessPixels(OverlayElementViewModel element)
    {
        if (element.StrokeColor is not { })
        {
            return 0;
        }

        var scaleY = ComputeTargetToCanvasScaleY();
        if (scaleY <= 0)
        {
            return 0;
        }

        return (element.StrokeThickness * _targetMode.ImageHeight) / scaleY;
    }

    /// <summary>Refresh crop/zoom-dependent style lengths and text fitting on the canvas.</summary>
    private void RefreshOverlayElementCanvasStyles()
    {
        foreach (var element in OverlayElements)
        {
            RefreshElementPreviewMetrics(element);
            if (element is OverlayElementViewModel text)
            {
                text.CanvasFontSize = ComputeCanvasFontSize(text);
                text.CanvasStrokeThicknessPixels = ComputeCanvasStrokeThicknessPixels(text);
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

        [LoggerMessage(Level = LogLevel.Warning, Message = "Template '{TemplateId}' text picture-fill asset '{AssetFileName}' could not be loaded; loading that element without its picture fill")]
        public static partial void BitmapFillAssetLoadFailed(ILogger logger, string templateId, string assetFileName, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Apply & Transmit invoked: targetMode={TargetMode}")]
        public static partial void ApplyAndTransmitInvoked(ILogger logger, string targetMode);

        [LoggerMessage(Level = LogLevel.Information, Message = "Ready Rack direct-fire invoked: targetMode={TargetMode}")]
        public static partial void DirectFireInvoked(ILogger logger, string targetMode);

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
