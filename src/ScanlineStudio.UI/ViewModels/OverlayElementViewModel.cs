using AvaloniaColor = Avalonia.Media.Color;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.UI.Imaging;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Mutable, drag/edit-friendly wrapper around <see cref="Abstractions.Imaging.TemplateTextElement"/>
/// (Phase 1, spec/15-template-designer.md — pre-Phase-1 this wrapped the older, size-less
/// <c>ImageOverlayElement</c>) — the immutable record gets replaced wholesale on every drag-frame
/// otherwise; this stays one instance per element so two-way X/Y/Width/Height/Text bindings from the
/// editor canvas work directly. X/Y are CENTER-anchored (kept from the pre-Phase-1 convention
/// deliberately, not switched to top-left — see <see cref="ITemplateElementViewModel"/>'s own doc
/// comment). <see cref="FontSizeRelative"/> is relative to the image's height, same as before Phase
/// 1, but its MEANING changed: it's now the STARTING/MAXIMUM size for
/// <see cref="ITransmitImagePreparer.ApplyTemplate"/>'s shrink-to-fit search against this element's
/// own <see cref="Width"/> x <see cref="Height"/> box, not a fixed rendered size.</summary>
public sealed partial class OverlayElementViewModel : ObservableObject, ITemplateElementViewModel, IDisposable
{
    [ObservableProperty]
    private string _text = "Text";

    [ObservableProperty]
    private double _x = 0.5;

    [ObservableProperty]
    private double _y = 0.5;

    /// <summary>Phase 1 default: 0.3 x 0.18, deliberately with headroom over the default
    /// <see cref="FontSizeRelative"/> (0.1) rather than a tight ~0.1-tall box — plan-review finding:
    /// <c>TextMeasurer</c>'s line height (ascender+descender+gap) exceeds a bare em size, so a box
    /// sized flush to the font fraction would shrink-to-fit immediately on creation, making "Add
    /// text" a visible regression from day one. 0.18 is comfortably above the ~1.5-2x headroom the
    /// review asked for.</summary>
    [ObservableProperty]
    private double _width = 0.3;

    [ObservableProperty]
    private double _height = 0.18;

    [ObservableProperty]
    private int _z;

    [ObservableProperty]
    private bool _locked;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Backlog item (auditor usability review, 2026-08-17) -- gates the canvas's own inline
    /// edit TextBox (double-click/F2 on a selected, unlocked text element), so <see cref="Text"/>
    /// becomes directly editable on the canvas itself instead of only via the ELEMENTS panel's own
    /// row TextBox. No separate commit/cancel plumbing needed -- the inline TextBox binds
    /// <see cref="Text"/> two-way, the exact same property the row TextBox already commits through
    /// directly, so toggling this back off is the only state this needs to own.</summary>
    [ObservableProperty]
    private bool _isEditingText;

    /// <summary>Per-element, session-only override of the shrink-to-fit search
    /// (<see cref="ITransmitImagePreparer.MeasureFittedFontSize"/>/<c>ApplyTemplate</c>'s font-fit
    /// search): when set, the fit search may also grow PAST <see cref="FontSizeRelative"/> if the box
    /// has room, not just shrink below it. User-requested (2026-09-15): growing a text box previously
    /// never grew the font past its last explicitly-set size, matching legacy YONIQ's port-first
    /// default in every OTHER respect except this one -- legacy's own <c>CDrawText::Move</c>
    /// (<c>Draw.cpp:2965-2988</c>) scales the font proportionally with box-drag in BOTH directions, no
    /// asymmetric cap, so growing past nominal is the more legacy-faithful behavior; this stays an
    /// opt-in per the user's own explicit choice (right-click menu), not a default-on behavior change.
    /// Deliberately NOT in <see cref="ScanlineStudio.Application.PersistedTextElement"/> -- resets to
    /// <see langword="false"/> on a template SAVE/LOAD round-trip (create a fresh element, reload a
    /// template, import), same "transient, no persisted-storage tier" as <see cref="IsSelected"/>/
    /// <see cref="IsEditingText"/> above. Unlike those two, this DOES feed the pipeline (see
    /// <c>OnOverlayElementPropertyChanged</c>'s own filter list -- deliberately NOT early-returned
    /// there, since it changes rendered output) and IS carried by
    /// <c>TxImageEditorPaneViewModel.RawTextElementSnapshot</c> (yoniq-auditor-flagged: without that,
    /// Undo/Redo would silently reset it on every text element, not just the one edit being undone) --
    /// so it survives Undo/Redo/Duplicate/Copy-Paste within one editor session, just not a
    /// save/reload.
    /// User-reported gap, fixed 2026-09-15: the flag resetting to false used to also mean the
    /// visual RESULT reset -- a template saved with a grown font reloaded back at its small, pre-grow
    /// <see cref="FontSizeRelative"/>, since that field only ever held the "set" size, never the
    /// grown one. <see cref="TxImageEditorPaneViewModel.BuildPersistedElementAsync"/> now bakes the
    /// live fitted/grown size (via <c>ComputeFittedFontSizeRelative</c>) into
    /// <see cref="FontSizeRelative"/> itself at save time -- this flag still doesn't need to survive
    /// the round trip for the SIZE it produced to.</summary>
    [ObservableProperty]
    private bool _growToFillEnabled;

    [ObservableProperty]
    private double _fontSizeRelative = 0.1;

    /// <summary>TX workflow modernization plan, Phase 1 (Quick Style Flyout) -- the target MODE's
    /// own pixel height, pushed once by <see cref="TxImageEditorPaneViewModel.CreateOverlayElement"/>
    /// from its own <c>_targetMode.ImageHeight</c> (ctor-assigned, never reassigned for the VM's
    /// lifetime -- see <see cref="TxImageEditorPaneViewModel.SelectedTextElementFontSizePx"/>'s own
    /// doc comment for why that's safe). Lets <see cref="FontSizePx"/> below do the exact same
    /// px-conversion that property already does, reachable from THIS element directly rather than
    /// through the parent VM's <c>SelectedTextElement</c>-keyed property -- a right-click's own
    /// context menu binds against the element itself, not the parent VM (see
    /// <see cref="RemoveCommand"/>'s own doc comment for why that binding path is a real, previously
    /// hit crash, not a style preference).</summary>
    public double TargetModeHeightPx { get; init; }

    /// <summary>Two-way px view of <see cref="FontSizeRelative"/>, reusing
    /// <see cref="TxImageEditorPaneViewModel.SelectedTextElementFontSizePx"/>'s own formula (not a
    /// second re-derivation) against <see cref="TargetModeHeightPx"/> instead of a live
    /// <c>_targetMode</c> field read. Setting <see cref="FontSizeRelative"/> already routes through
    /// <see cref="OnFontSizeRelativeChanging"/>'s existing undo hook below, so this needs no undo
    /// plumbing of its own.</summary>
    public double FontSizePx
    {
        get => FontSizeRelative * TargetModeHeightPx;
        set
        {
            if (TargetModeHeightPx <= 0)
            {
                return;
            }

            FontSizeRelative = value / TargetModeHeightPx;
        }
    }

    [ObservableProperty]
    private Rgb24 _color = new(255, 255, 255);

    /// <summary>Empty by default (matches <see cref="ITransmitImagePreparer.MeasureFittedFontSize"/>'s
    /// own "empty family falls back to the pipeline's default" contract) -- the REAL default (one of
    /// <see cref="ITransmitImagePreparer.AvailableFontFamilies"/>) is set explicitly by
    /// <see cref="TxImageEditorPaneViewModel.CreateOverlayElement"/>'s object initializer at
    /// construction time, queried fresh from the preparer rather than hardcoded here a second time
    /// (Phase 4, spec/15-template-designer.md).</summary>
    [ObservableProperty]
    private string _fontFamily = string.Empty;

    /// <summary>Auditor usability review follow-up (2026-08-18) -- from the user's original
    /// 2026-08-16 "yoniq text features" checklist (color/shadow/gradient/rotation shipped in Phase
    /// 4/8, bold/italic never started). Requires a real, separately-bundled Bold font FILE for
    /// whichever family is selected (<see cref="ITransmitImagePreparer"/>'s own constructor
    /// registers one per bundled family) -- an unavailable combination throws at Apply/preview time
    /// rather than silently faking a bold look, same "no invented rendering technique, real font
    /// variant or nothing" choice this project already made for stroke/shadow (a plain
    /// offset-duplicate-glyph draw, not a synthesized effect).</summary>
    [ObservableProperty]
    private bool _bold;

    [ObservableProperty]
    private bool _italic;

    /// <summary>Null means no outline (matches <see cref="TemplateBoxElement.BorderColor"/>'s own
    /// null-means-none convention) -- Phase 4, the user-requested legibility mechanism for text
    /// against varying backgrounds (this session's own real-window testing hit white-on-white text
    /// twice against near-white stock photos).</summary>
    [ObservableProperty]
    private Rgb24? _strokeColor;

    /// <summary>Relative to the image's HEIGHT, same convention as <see cref="FontSizeRelative"/>/
    /// <see cref="BoxElementViewModel.BorderThickness"/>. Meaningless while <see cref="StrokeColor"/>
    /// is null (mirrors <see cref="BoxElementViewModel.BorderThickness"/>'s own "thickness without a
    /// color is a no-op" contract).</summary>
    [ObservableProperty]
    private double _strokeThickness = 0.02;

    /// <summary>Phase 8 (spec/15-template-designer.md, YONIQ-style text-effects follow-up). Null
    /// means no shadow (same null-means-none convention as <see cref="StrokeColor"/>) -- a plain
    /// offset-duplicate-glyph draw (legacy YONIQ's own real mechanism), not a soft blur.</summary>
    [ObservableProperty]
    private Rgb24? _shadowColor;

    /// <summary>Relative to the image's HEIGHT, same convention as <see cref="FontSizeRelative"/>/
    /// <see cref="StrokeThickness"/> (both axes, so a template saved at one SSTV mode renders
    /// correctly at another). Meaningless while <see cref="ShadowColor"/> is null.</summary>
    [ObservableProperty]
    private double _shadowOffsetX = 0.02;

    [ObservableProperty]
    private double _shadowOffsetY = 0.02;

    /// <summary>Auditor usability review follow-up (2026-08-18) -- legacy YONIQ's "3D" text option
    /// (confirmed via <c>TextIn.cpp</c>/<c>Draw.cpp</c>'s <c>CBStack</c>/<c>m_Stack</c>: a stepped
    /// stack of offset solid-color copies, NOT a real 3D transform). Null means no stack effect, same
    /// null-means-none convention as <see cref="ShadowColor"/>.</summary>
    [ObservableProperty]
    private Rgb24? _stackColor;

    /// <summary>Relative to the image's HEIGHT, same convention as <see cref="ShadowOffsetX"/>/Y.
    /// Meaningless while <see cref="StackColor"/> is null.</summary>
    [ObservableProperty]
    private double _stackStepX = 0.02;

    [ObservableProperty]
    private double _stackStepY = 0.02;

    /// <summary>Phase 8: in-plane (2D) rotation only, clockwise-positive degrees -- true 3D/
    /// perspective is a separate, deferred future phase (Tier 3).</summary>
    [ObservableProperty]
    private double _rotationDegrees;

    /// <summary>Phase 8: simplified 2-stop gradient (start/end color + axis) rather than exposing
    /// <see cref="Abstractions.Imaging.TextGradient"/>'s own full arbitrary-stop-list shape directly
    /// on this VM -- a real, deliberate scope cut for the style panel's own UI (a 2-color picker
    /// pair + an axis ComboBox is the whole surface; an N-stop editor is real, unbudgeted UI work
    /// for a feature this project's own plan explicitly scoped as "feature parity, not literal
    /// legacy replication"). <see cref="BuildTemplateElement"/> in the owning
    /// <see cref="TxImageEditorPaneViewModel"/> composes these three fields into a real
    /// <see cref="Abstractions.Imaging.TextGradient"/> only when <see cref="GradientEnabled"/> is
    /// true.</summary>
    [ObservableProperty]
    private bool _gradientEnabled;

    [ObservableProperty]
    private TextGradientKind _gradientKind = TextGradientKind.Horizontal;

    [ObservableProperty]
    private Rgb24 _gradientStartColor = new(255, 0, 0);

    [ObservableProperty]
    private Rgb24 _gradientEndColor = new(0, 0, 255);

    /// <summary>TX editor gap-items plan, item 4b (picture fill, 2026-09-01): a THIRD fill mode,
    /// independent of <see cref="GradientEnabled"/> above (a sibling scalar, not a shared 3-way
    /// discriminator -- refactoring the already-shipped Gradient shape into an enum was rejected as
    /// unnecessary churn on tested code; see <see cref="Abstractions.Imaging.TemplateTextElement.BitmapFill"/>'s
    /// own doc comment for the full precedence-invariant reasoning). Enforced mutually exclusive with
    /// <see cref="GradientEnabled"/> at the setter level below (turning one on turns the other off) --
    /// a UI-input-time convenience only, NOT the sole enforcement: the real precedence rule (BitmapFill
    /// wins if both are somehow true, e.g. a hand-edited template file bypassing these setters
    /// entirely) is enforced again at every COMPOSITION site downstream (this class's own
    /// <see cref="ForegroundBrush"/>, <c>TxImageEditorPaneViewModel.BuildTemplateElement</c>, and
    /// <c>TemplateStore.ToTemplateElementAsync</c>). Deliberately NO <c>On...Changing</c> undo-push
    /// hook, matching <see cref="GradientEnabled"/>'s own shape exactly (same symmetry reasoning: an
    /// asymmetric undo hook on only one of a mutually-exclusive pair would push an undo step for one
    /// fill-mode toggle but not the other).</summary>
    [ObservableProperty]
    private bool _bitmapFillEnabled;

    /// <summary>Resolved bitmap, same "already-loaded, never re-decoded here" convention as
    /// <see cref="ImageElementViewModel.Source"/> -- MUST stay a stable cached instance across a
    /// template's own edit session (see <see cref="Abstractions.Imaging.TemplateTextElement.BitmapFill"/>'s
    /// own doc comment for why: record equality on that interface-typed member falls back to
    /// reference equality, the same trap <see cref="Abstractions.Imaging.TextGradient"/>'s own
    /// <c>Stops</c> list already documents).</summary>
    [ObservableProperty]
    private IImageSource? _bitmapFillSource;

    /// <summary>Cached conversion of <see cref="BitmapFillSource"/> for canvas display -- same
    /// "rebuilt only when the source itself changes, not on every property-changed pass" reasoning
    /// as <see cref="ImageElementViewModel.CanvasBitmap"/>. Unlike that property, legitimately null
    /// most of the time -- picture fill is opt-in, not every text element's core content.</summary>
    public WriteableBitmap? CanvasBitmapFill { get; private set; }

    /// <summary>Set by the owning <see cref="TxImageEditorPaneViewModel"/> at creation time -- lets
    /// this element compute its own on-screen position without the View needing a
    /// multi-binding/converter to combine X/Y with the canvas size itself. Settable (not
    /// <c>init</c>), not because it changes often, but because
    /// <see cref="TxImageEditorPaneViewModel.RotateCommand"/> updates every existing element's
    /// dimensions in place after a 90° rotation swaps width/height.</summary>
    [ObservableProperty]
    private double _imageWidth;

    [ObservableProperty]
    private double _imageHeight;

    /// <summary>Font size in CANVAS DISPLAY pixels (the same pixel space as <see cref="ImageWidth"/>/
    /// <see cref="ImageHeight"/>), pushed by <see cref="TxImageEditorPaneViewModel"/> whenever the
    /// crop rect, PreserveAspect, or this element's own <see cref="FontSizeRelative"/>/
    /// <see cref="Width"/>/<see cref="Height"/>/<see cref="Text"/> changes (Phase 1 widened the
    /// dependency set from FontSizeRelative-only, since the rendered size is now a real shrink-to-fit
    /// result of the box and content, not a closed-form function of FontSizeRelative alone) -- same
    /// parent-pushed pattern as <see cref="ImageWidth"/>/<see cref="ImageHeight"/>, for the same
    /// reason (no live ambient binding back to the parent VM from inside this DataTemplate; see
    /// <see cref="RemoveCommand"/>'s own doc comment for the concrete crash that pattern hit).
    /// Purely canvas-chrome display state -- never feeds
    /// <see cref="TxImageEditorPaneViewModel.BuildTemplateElement"/> or the real pipeline, so
    /// <c>TxImageEditorPaneViewModel.OnOverlayElementPropertyChanged</c> excludes it from triggering
    /// a preview recompute.</summary>
    [ObservableProperty]
    private double _canvasFontSize;

    /// <summary>Backlog item (user request, 2026-08-17) -- canvas-preview outline fix
    /// (Avalonia's plain <c>TextBlock</c> has no native stroke API; the real fix is a custom
    /// <c>StrokedTextBlock</c> control, see the canvas DataTemplate's own comment). Same
    /// parent-pushed, pure-canvas-chrome pattern as <see cref="CanvasFontSize"/> (0 when
    /// <see cref="StrokeColor"/> is null), computed in the identical pixel space so a 0-thickness
    /// stroke never accidentally shows.</summary>
    [ObservableProperty]
    private double _canvasStrokeThicknessPixels;

    /// <summary>Set once by <see cref="TxImageEditorPaneViewModel.CreateOverlayElement"/> at creation
    /// time (its own <c>RemoveOverlayElementCommand</c>), not bound in XAML via
    /// `$parent[ItemsControl].((vm:TxImageEditorPaneViewModel)DataContext)...` -- that pattern
    /// throws `ArgumentException: Unable to resolve type` the first time this element's
    /// DataTemplate is actually realized (this project uses classic, non-compiled bindings; an
    /// inline type cast in a binding path forces a runtime type-resolution step that doesn't
    /// reliably find sibling view-model types). This list starts empty and is only ever populated
    /// by <c>AddOverlayElement</c>/<c>AddBoxElement</c>, so the old binding had never actually been
    /// exercised by any hands-on session so far -- same latent-crash shape as TxControlsPaneViewModel's
    /// own Drive slider, found and fixed the same way.</summary>
    public IRelayCommand? RemoveCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.MoveUpCommand"/>
    public IRelayCommand? MoveUpCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.MoveDownCommand"/>
    public IRelayCommand? MoveDownCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.BringToFrontCommand"/>
    public IRelayCommand? BringToFrontCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.SendToBackCommand"/>
    public IRelayCommand? SendToBackCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.DuplicateCommand"/>
    public IRelayCommand? DuplicateCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.AlignSelectedElementToCropCommand"/>
    public IRelayCommand? AlignSelectedElementToCropCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.CopyCommand"/>
    public IRelayCommand? CopyCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.CutCommand"/>
    public IRelayCommand? CutCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.PasteCommand"/>
    public IRelayCommand? PasteCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.FlattenCommand"/>
    public IRelayCommand? FlattenCommand { get; init; }

    /// <summary>TX workflow modernization plan, Phase 1 -- text-only (box has its own separate
    /// fill/border style, not this one; image has no copyable "style"), same parent-pushed,
    /// no-CommandParameter shape as <see cref="DuplicateCommand"/>.</summary>
    public IRelayCommand? CopyStyleCommand { get; init; }

    public IRelayCommand? PasteStyleCommand { get; init; }

    /// <summary>Task #24 (right-click context menu addendum) -- text-only (matches
    /// <see cref="ImageElementViewModel.SetAsBackgroundCommand"/>'s own image-only precedent for
    /// exactly the same reason: not part of the shared <see cref="ITemplateElementViewModel"/>
    /// interface since box/image elements have no text to plate). Same parent-pushed pattern as
    /// <see cref="DuplicateCommand"/> -- parent-pushed the SAME parameterless
    /// <c>TxImageEditorPaneViewModel.AddPlateBehindTextCommand</c> instance the toolbar already uses,
    /// bound with no <c>CommandParameter</c>.</summary>
    public IRelayCommand? AddPlateCommand { get; init; }

    /// <summary>EditWindow redesign Phase 6 (mockups/Editwindow) -- text-only (matches
    /// <see cref="AddPlateCommand"/>'s own image/box-excluded precedent: only text elements have a
    /// <see cref="Text"/> to insert a macro token into). Parent-pushed the SAME
    /// <c>TxImageEditorPaneViewModel.InsertFieldCommand</c> instance the TEXT STYLE tab's own chips
    /// already use, bound in the context menu with the token string as <c>CommandParameter</c> --
    /// correctness depends on right-click having already set <c>SelectedOverlayElement</c> to THIS
    /// element (same reasoning as <see cref="DuplicateCommand"/>'s own doc comment).</summary>
    public IRelayCommand? InsertFieldCommand { get; init; }

    /// <summary>Backlog item (user request, 2026-08-17) -- text-only, same precedent as
    /// <see cref="InsertFieldCommand"/>. Parent-pushed the SAME
    /// <c>TxImageEditorPaneViewModel.SetFontSizePresetCommand</c> instance, bound in the context
    /// menu with a size-key string as <c>CommandParameter</c> (matches
    /// <see cref="AlignSelectedElementToCropCommand"/>'s own discrete-choice-via-CommandParameter
    /// shape, not a slider -- a native <c>ContextMenu</c> is not a reliable host for an embedded
    /// drag control). Full continuous control stays the TEXT STYLE tab's own <c>FontSizeRelative</c>
    /// TextBox; this is a quick-pick shortcut, not a replacement.</summary>
    public IRelayCommand? SetFontSizePresetCommand { get; init; }

    /// <summary>Same reasoning as <see cref="SetFontSizePresetCommand"/>, for
    /// <see cref="Color"/> -- a curated swatch list, not a full spectrum (the TEXT STYLE tab's own
    /// <c>ColorPicker</c> stays the full-control path).</summary>
    public IRelayCommand? SetTextColorPresetCommand { get; init; }

    /// <summary>Set once by <see cref="TxImageEditorPaneViewModel"/> at creation time, same pattern
    /// as <see cref="RemoveCommand"/> -- resolves this element's raw <see cref="Text"/> (which may
    /// contain macro tokens like <c>%m</c>/<c>{name}</c>) against the current operator settings.
    /// Null only in tests/design-time contexts that don't care about macro resolution.</summary>
    public Func<string, string>? ResolveMacros { get; init; }

    /// <summary>Set once by <see cref="TxImageEditorPaneViewModel"/> at creation time, same
    /// parent-pushed pattern as <see cref="RemoveCommand"/>/<see cref="ResolveMacros"/> -- pushes an
    /// undo/redo snapshot BEFORE <see cref="X"/>/<see cref="Y"/>/<see cref="Width"/>/<see cref="Height"/>
    /// actually change, coalesced under one SHARED key covering all four (Phase 1 plan-review
    /// finding: a diagonal drag-resize gesture must collapse to ONE undo step, same reasoning that
    /// already applied pre-Phase-1 to X/Y alone). Covers BOTH the canvas pointer-drag path and the
    /// sidebar X/Y TextBox edits. Null only in tests/design-time contexts that don't care about undo.</summary>
    public Action? PushUndoSnapshotForGeometryChange { get; init; }

    /// <summary>TX workflow modernization plan, Phase 1 -- closes a pre-existing gap:
    /// <see cref="FontSizeRelative"/>/<see cref="Color"/> had NO undo hook at all before the Quick
    /// Style Flyout made them a primary editing surface (see this class's own history at
    /// <c>SetFontSizePreset</c>'s doc comment in the VM, which explicitly preserved the gap rather
    /// than fix it as an unrelated side effect at the time). Coalesced under its OWN key, distinct
    /// from <see cref="PushUndoSnapshotForGeometryChange"/>'s "OverlayGeometry" -- an unrelated style
    /// tweak right after a drag must not fold invisibly into the drag's own undo step.</summary>
    public Action? PushUndoSnapshotForStyleChange { get; init; }

    public double LeftPixels => (X - (Width / 2)) * ImageWidth;

    public double TopPixels => (Y - (Height / 2)) * ImageHeight;

    [ObservableProperty]
    private ElementPreviewMetrics? _previewMetrics;

    private double StyleImageHeight => PreviewMetrics?.ImageHeight ?? ImageHeight;

    private double HorizontalStyleImageHeight => PreviewMetrics is { } metrics
        ? metrics.ImageHeight * metrics.PixelSize.Width / metrics.PixelSize.Height
        : ImageHeight;

    public double CanvasWidthPixels => Width * ImageWidth;

    public double CanvasHeightPixels => Height * ImageHeight;

    /// <summary>Phase 4 -- the TEXT STYLE panel's "Outline" checkbox binds here rather than directly
    /// to <see cref="StrokeColor"/> (a <c>Rgb24?</c>, not directly checkbox-bindable). Turning it ON
    /// seeds a real default color (black -- the highest-contrast choice against this app's own
    /// default WHITE text fill, which is exactly the "white text on a light photo" legibility
    /// problem this feature exists to fix); turning it OFF clears <see cref="StrokeColor"/> back to
    /// null (matches <see cref="Abstractions.Imaging.TemplateBoxElement.BorderColor"/>'s own
    /// null-means-none contract) rather than leaving a stale color the pipeline would just ignore
    /// anyway, so re-enabling later doesn't silently resurrect an old value with no visible
    /// indication it was still there.</summary>
    public bool HasStroke
    {
        get => StrokeColor is not null;
        set => StrokeColor = value ? (StrokeColor ?? new Rgb24(0, 0, 0)) : null;
    }

    /// <summary>Real-window finding (Phase 8 verification, but the bug is pre-existing since Phase 4
    /// -- caught here because <see cref="ShadowColor"/> inherited the identical pattern): binding a
    /// <c>ColorPicker.Color</c> two-way, through <c>Rgb24ToColorConverter</c>, DIRECTLY to a nullable
    /// <see cref="StrokeColor"/>/<see cref="ShadowColor"/> silently un-nulls it. That converter's own
    /// <c>Convert</c> maps <c>null -&gt; Colors.Black</c> for display (so the DISABLED picker doesn't
    /// spam a binding error while unchecked) -- but the picker's own two-way binding writes that
    /// synthetic fallback color straight back through <c>ConvertBack</c>, turning <c>null</c> into a
    /// real <c>Rgb24(0,0,0)</c> with no user action at all. Confirmed live: a freshly-created text
    /// element (never touched by the operator) showed BOTH "Outline" and "Shadow" checked with a
    /// black swatch, meaning every new element silently got a black outline AND a black drop-shadow
    /// baked into the transmitted image by default. Fixed by giving the picker its own NON-nullable
    /// view (<see cref="StrokeColorForPicker"/>/<see cref="ShadowColorForPicker"/> below) instead of
    /// binding the nullable source directly -- <see cref="GradientStartColor"/>/<see cref="GradientEndColor"/>
    /// never had this bug for exactly this reason (already non-nullable <c>Rgb24</c>, no null branch
    /// in the converter ever gets hit for them).</summary>
    public bool HasShadow
    {
        get => ShadowColor is not null;
        set => ShadowColor = value ? (ShadowColor ?? new Rgb24(0, 0, 0)) : null;
    }

    /// <summary>Non-nullable ColorPicker-facing view of <see cref="StrokeColor"/> -- see
    /// <see cref="HasShadow"/>'s own doc comment for the exact bug this sidesteps. Setter only
    /// commits while <see cref="HasStroke"/> is already true (the picker is disabled/decorative
    /// otherwise -- an incidental write while disabled is dropped, not silently un-nulling
    /// <see cref="StrokeColor"/>).</summary>
    public Rgb24 StrokeColorForPicker
    {
        get => StrokeColor ?? new Rgb24(0, 0, 0);
        set
        {
            if (HasStroke)
            {
                StrokeColor = value;
            }
        }
    }

    /// <inheritdoc cref="StrokeColorForPicker"/>
    public Rgb24 ShadowColorForPicker
    {
        get => ShadowColor ?? new Rgb24(0, 0, 0);
        set
        {
            if (HasShadow)
            {
                ShadowColor = value;
            }
        }
    }

    /// <inheritdoc cref="HasShadow"/>
    public bool HasStack
    {
        get => StackColor is not null;
        set => StackColor = value ? (StackColor ?? new Rgb24(0, 0, 0)) : null;
    }

    /// <inheritdoc cref="StrokeColorForPicker"/>
    public Rgb24 StackColorForPicker
    {
        get => StackColor ?? new Rgb24(0, 0, 0);
        set
        {
            if (HasStack)
            {
                StackColor = value;
            }
        }
    }

    /// <summary>Canvas-preview amendment (user explicitly asked for real WYSIWYG here, overriding
    /// this phase's own original "canvas can't show rotation/shadow/gradient, only the mini-preview
    /// can" scope decision -- Phase 4 made the identical call for stroke and it's STILL true that a
    /// plain Avalonia <c>TextBlock</c> has no native outline capability, so stroke stays
    /// mini-preview-only; rotation and gradient, unlike stroke, DO have real Avalonia primitives to
    /// use here). Bound to the outer element <c>Border</c>'s own <c>RenderTransform</c> (not the
    /// <c>TextBlock</c> alone) so it rotates the text AND its shadow copy together, matching the real
    /// pipeline's own behavior of rotating the whole offscreen sub-bitmap as one unit. Null at 0°
    /// (no transform needed for the common case) -- Avalonia's own <c>RenderTransform</c> accepts a
    /// null value as "identity," so this is not a special case, just an optimization.</summary>
    public Transform? RotationTransform => RotationDegrees != 0 ? new RotateTransform(RotationDegrees) : null;

    /// <summary>Auditor usability review follow-up (2026-08-18) -- Bold/Italic canvas WYSIWYG, bound
    /// by <see cref="Controls.StrokedTextBlock.FontWeight"/>/<c>FontStyle</c> and the shadow
    /// TextBlock's own matching properties. Same "expose the Avalonia type directly from this VM"
    /// precedent as <see cref="RotationTransform"/> just above.</summary>
    public FontWeight CanvasFontWeight => Bold ? FontWeight.Bold : FontWeight.Normal;

    public FontStyle CanvasFontStyle => Italic ? FontStyle.Italic : FontStyle.Normal;

    /// <summary>Canvas-preview amendment, shadow half -- Avalonia's <c>TextBlock</c> has no native
    /// drop-shadow primitive (confirmed, same as the stroke case), so this fakes it the same way the
    /// real pipeline does: a second, offset TextBlock copy underneath the real one (see the
    /// DataTemplate's own XAML for the actual two-TextBlock structure). Offset is in the SAME
    /// canvas-display pixel space <see cref="CanvasFontSize"/> already uses (both scale off
    /// <see cref="ImageHeight"/>, matching <see cref="Abstractions.Imaging.TemplateTextElement.ShadowOffsetX"/>/
    /// <see cref="ShadowOffsetY"/>'s own image-height-relative convention, so a template saved at one
    /// SSTV mode previews correctly at another here too). Null while <see cref="HasShadow"/> is false
    /// -- the shadow TextBlock is ALSO <c>IsVisible</c>-gated on <see cref="HasShadow"/>, so this only
    /// matters while it's actually shown.</summary>
    public Transform? ShadowRenderTransform => HasShadow
        ? new TranslateTransform(ShadowOffsetX * HorizontalStyleImageHeight, ShadowOffsetY * StyleImageHeight)
        : null;

    /// <summary>Canvas-preview amendment, stack half -- auditor usability review follow-up
    /// (2026-08-18): upgraded from an earlier single-offset "directional hint" to the REAL N-copy
    /// preview (user-reported, comparing the canvas against the mode-exact mini-preview side by
    /// side: "stack text looks off in the edit window vs the preview... noticable difference" -- a
    /// real gap, not a false alarm). Rendered by <see cref="Controls.StrokedTextBlock"/> itself (its
    /// own new <c>StackFill</c>/<c>StackStepXPixels</c>/<c>StackStepYPixels</c> properties), NOT an
    /// <c>ItemsControl</c> of generated copies -- this codebase has a documented, real crash from
    /// binding a generated DataTemplate's item back up to its parent VM (see
    /// <see cref="RemoveCommand"/>'s own doc comment), so plain direct StyledProperty bindings on a
    /// single control (the same pattern <c>StrokedTextBlock</c> already uses for Stroke) sidesteps
    /// that landmine entirely. These two are the PIXEL-space step (canvas-display pixels, same space
    /// <see cref="ImageHeight"/> itself is in), not the raw relative <see cref="StackStepX"/>/Y --
    /// Avalonia bindings have no arithmetic syntax, so the multiply-by-ImageHeight has to happen
    /// here, not in the binding expression.</summary>
    public double CanvasStackStepXPixels => HasStack ? StackStepX * HorizontalStyleImageHeight : 0;

    public double CanvasStackStepYPixels => HasStack ? StackStepY * StyleImageHeight : 0;

    /// <summary>Canvas-preview amendment, gradient half -- the main TextBlock's own
    /// <c>Foreground</c>: a plain solid brush from <see cref="Color"/> (identical to the pre-Phase-8
    /// binding), or a real Avalonia <see cref="LinearGradientBrush"/>/<see cref="RadialGradientBrush"/>
    /// when <see cref="GradientEnabled"/> is set. Coordinates use Avalonia's own
    /// <see cref="RelativeUnit.Relative"/> (0,0)-(1,1) across the TextBlock's own layout box --
    /// genuinely simpler than the real pipeline's own gradient-brush construction
    /// (<c>TransmitImagePreparer.BuildGradientBrush</c>), which has to compute real destination-image
    /// pixel coordinates by hand because ImageSharp's own gradient brushes have no relative-coordinate
    /// mode at all.</summary>
    public IBrush ForegroundBrush => BitmapFillEnabled && CanvasBitmapFill is { } bitmapFill
        ? new Avalonia.Media.ImageBrush(bitmapFill) { Stretch = Stretch.Fill }
        : GradientEnabled
            ? GradientBrushFactory.Build(GradientKind, GradientStartColor, GradientEndColor, CanvasWidthPixels, CanvasHeightPixels, PreviewMetrics)
            : new SolidColorBrush(ToAvaloniaColor(Color));

    private static AvaloniaColor ToAvaloniaColor(Rgb24 color) => AvaloniaColor.FromRgb(color.R, color.G, color.B);

    /// <summary>What actually gets drawn -- the canvas preview binds here, not <see cref="Text"/>,
    /// so the user sees "DE W1AW" rather than the literal "DE %m" template while editing.</summary>
    public string ResolvedText => ResolveMacros?.Invoke(Text) ?? Text;

    /// <summary>Phase 3 (spec/15-template-designer.md, named template variables + fill bar) --
    /// forces a <see cref="ResolvedText"/> property-changed raise from OUTSIDE this class. Needed
    /// because <see cref="ResolvedText"/> otherwise only ever
    /// re-raises as a SIDE EFFECT of <see cref="Text"/> changing (<see cref="OnTextChanged"/>
    /// below) -- that held by coincidence pre-Phase-3, since nothing else could change what
    /// <see cref="ResolveMacros"/> resolves to. A fill-bar edit changes the RESOLUTION CONTEXT
    /// (<see cref="TxImageEditorPaneViewModel"/>'s own template-variable dictionary), not this
    /// element's own <see cref="Text"/>, so it needs its own explicit trigger -- see
    /// <c>TxImageEditorPaneViewModel</c>'s own template-variable-changed handler for the full
    /// picture (this call covers only the canvas <c>TextBlock</c> binding refresh; the pipeline
    /// recompute and font-size refresh are separate, explicit calls that handler also makes, since
    /// <see cref="ResolvedText"/> itself is filtered out of
    /// <c>TxImageEditorPaneViewModel.OnOverlayElementPropertyChanged</c>'s own recompute trigger).</summary>
    public void NotifyResolvedTextChanged() => OnPropertyChanged(nameof(ResolvedText));

    partial void OnTextChanging(string value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnFontFamilyChanging(string value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnBoldChanging(bool value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnItalicChanging(bool value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnStrokeColorChanging(Rgb24? value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnStrokeThicknessChanging(double value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnShadowColorChanging(Rgb24? value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnShadowOffsetXChanging(double value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnShadowOffsetYChanging(double value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnStackColorChanging(Rgb24? value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnStackStepXChanging(double value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnStackStepYChanging(double value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnRotationDegreesChanging(double value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnGradientEnabledChanging(bool value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnGradientKindChanging(TextGradientKind value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnGradientStartColorChanging(Rgb24 value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnGradientEndColorChanging(Rgb24 value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnBitmapFillEnabledChanging(bool value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnBitmapFillSourceChanging(IImageSource? value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnFontSizeRelativeChanging(double value) => PushUndoSnapshotForStyleChange?.Invoke();

    // Quick Style Flyout's own FontSizePx TextBox needs to stay in sync when FontSizeRelative
    // changes via any OTHER path (Paste Style, the sidebar TEXT STYLE tab, a preset menu item).
    partial void OnFontSizeRelativeChanged(double value) => OnPropertyChanged(nameof(FontSizePx));

    partial void OnColorChanging(Rgb24 value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnXChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnYChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnWidthChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnHeightChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnXChanged(double value) => OnPropertyChanged(nameof(LeftPixels));

    partial void OnYChanged(double value) => OnPropertyChanged(nameof(TopPixels));

    partial void OnWidthChanged(double value)
    {
        OnPropertyChanged(nameof(LeftPixels));
        OnPropertyChanged(nameof(CanvasWidthPixels));
        OnPropertyChanged(nameof(ForegroundBrush));
    }

    partial void OnHeightChanged(double value)
    {
        OnPropertyChanged(nameof(TopPixels));
        OnPropertyChanged(nameof(CanvasHeightPixels));
        OnPropertyChanged(nameof(ForegroundBrush));
    }

    partial void OnPreviewMetricsChanged(ElementPreviewMetrics? value)
    {
        OnPropertyChanged(nameof(ShadowRenderTransform));
        OnPropertyChanged(nameof(CanvasStackStepXPixels));
        OnPropertyChanged(nameof(CanvasStackStepYPixels));
        OnPropertyChanged(nameof(ForegroundBrush));
    }

    partial void OnImageWidthChanged(double value)
    {
        OnPropertyChanged(nameof(LeftPixels));
        OnPropertyChanged(nameof(CanvasWidthPixels));
        OnPropertyChanged(nameof(ForegroundBrush));
    }

    partial void OnImageHeightChanged(double value)
    {
        OnPropertyChanged(nameof(TopPixels));
        OnPropertyChanged(nameof(CanvasHeightPixels));
        OnPropertyChanged(nameof(ForegroundBrush));
        // Canvas-preview amendment: ShadowRenderTransform's own offset scales off ImageHeight too.
        OnPropertyChanged(nameof(ShadowRenderTransform));
        OnPropertyChanged(nameof(CanvasStackStepXPixels));
        OnPropertyChanged(nameof(CanvasStackStepYPixels));
    }

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(ResolvedText));

    partial void OnStrokeColorChanged(Rgb24? value)
    {
        OnPropertyChanged(nameof(HasStroke));
        OnPropertyChanged(nameof(StrokeColorForPicker));
    }

    partial void OnShadowColorChanged(Rgb24? value)
    {
        OnPropertyChanged(nameof(HasShadow));
        OnPropertyChanged(nameof(ShadowColorForPicker));
        // Canvas-preview amendment: HasShadow gates ShadowRenderTransform (null while off).
        OnPropertyChanged(nameof(ShadowRenderTransform));
    }

    // Canvas-preview amendment (see RotationTransform/ShadowRenderTransform/ForegroundBrush's own
    // doc comments) -- every dependency of those three computed properties needs its own re-raise
    // hook, same "parent-pushed cascade, not left to accidentally work" discipline this file already
    // established for LeftPixels/TopPixels/CanvasWidthPixels/CanvasHeightPixels above.
    partial void OnRotationDegreesChanged(double value) => OnPropertyChanged(nameof(RotationTransform));

    partial void OnBoldChanged(bool value) => OnPropertyChanged(nameof(CanvasFontWeight));

    partial void OnItalicChanged(bool value) => OnPropertyChanged(nameof(CanvasFontStyle));

    partial void OnShadowOffsetXChanged(double value) => OnPropertyChanged(nameof(ShadowRenderTransform));

    partial void OnShadowOffsetYChanged(double value) => OnPropertyChanged(nameof(ShadowRenderTransform));

    partial void OnStackColorChanged(Rgb24? value)
    {
        OnPropertyChanged(nameof(HasStack));
        OnPropertyChanged(nameof(StackColorForPicker));
        OnPropertyChanged(nameof(CanvasStackStepXPixels));
        OnPropertyChanged(nameof(CanvasStackStepYPixels));
    }

    partial void OnStackStepXChanged(double value) => OnPropertyChanged(nameof(CanvasStackStepXPixels));

    partial void OnStackStepYChanged(double value) => OnPropertyChanged(nameof(CanvasStackStepYPixels));

    partial void OnColorChanged(Rgb24 value) => OnPropertyChanged(nameof(ForegroundBrush));

    partial void OnGradientEnabledChanged(bool value)
    {
        // TX editor gap-items plan, item 4b -- mutual-exclusion half; see BitmapFillEnabled's own
        // doc comment for why this is setter-level convenience, not the real precedence rule.
        if (value)
        {
            BitmapFillEnabled = false;
        }

        OnPropertyChanged(nameof(ForegroundBrush));
    }

    partial void OnGradientKindChanged(TextGradientKind value) => OnPropertyChanged(nameof(ForegroundBrush));

    partial void OnGradientStartColorChanged(Rgb24 value) => OnPropertyChanged(nameof(ForegroundBrush));

    partial void OnGradientEndColorChanged(Rgb24 value) => OnPropertyChanged(nameof(ForegroundBrush));

    partial void OnBitmapFillEnabledChanged(bool value)
    {
        if (value)
        {
            GradientEnabled = false;
        }

        OnPropertyChanged(nameof(ForegroundBrush));
    }

    partial void OnBitmapFillSourceChanged(IImageSource? value)
    {
        // Same deferred-dispose-of-the-OLD-bitmap pattern as ImageElementViewModel.OnSourceChanged
        // -- a WriteableBitmap holds a native/unmanaged resource nothing else disposes. Never fires
        // at construction (the generated setter isn't invoked by the field initializer), so `old`
        // here is always either null (first real assignment) or a previously-displayed bitmap.
        var old = CanvasBitmapFill;
        CanvasBitmapFill = value is not null ? ImageSourceBitmapConverter.ToBitmap(value) : null;
        OnPropertyChanged(nameof(CanvasBitmapFill));
        OnPropertyChanged(nameof(ForegroundBrush));
        if (old is not null)
        {
            Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
        }
    }

    private bool _disposed;

    /// <summary>Disposes <see cref="CanvasBitmapFill"/> when this element is discarded wholesale
    /// (template reload, undo/redo ApplyState, single-element Remove/Flatten -- see
    /// <c>TxImageEditorPaneViewModel</c>'s own <c>is IDisposable</c> discard-loop call sites, widened
    /// from an <c>ImageElementViewModel</c>-only check to cover this class too) rather than reassigned
    /// in place (<see cref="OnBitmapFillSourceChanged"/> above already handles that case). Deferred,
    /// same reasoning as that method -- the corresponding canvas control's own detach from the visual
    /// tree is not guaranteed synchronous with this call. Guarded against a second call (IDisposable's
    /// own contract).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (CanvasBitmapFill is { } bitmap)
        {
            Dispatcher.UIThread.Post(bitmap.Dispose, DispatcherPriority.Background);
        }
    }
}
