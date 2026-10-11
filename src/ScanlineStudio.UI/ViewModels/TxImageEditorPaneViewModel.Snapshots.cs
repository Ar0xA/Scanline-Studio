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

    /// <summary>No parameter has a default, so adding a field fails the build at every construction
    /// site instead of silently dropping the value there.
    /// <paramref name="GradientEnabled"/>/<paramref name="GradientKind"/>/
    /// <paramref name="GradientStartColor"/>/<paramref name="GradientEndColor"/>
    /// mirror <see cref="OverlayElementViewModel"/>'s own simplified 2-stop-gradient VM shape exactly (see
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
        string FontFamily, Rgb24? StrokeColor, double StrokeThickness,
        Rgb24? ShadowColor, double ShadowOffsetX, double ShadowOffsetY, double RotationDegrees,
        bool GradientEnabled, TextGradientKind GradientKind,
        Rgb24? GradientStartColor, Rgb24? GradientEndColor,
        bool Bold, bool Italic,
        Rgb24? StackColor, double StackStepX, double StackStepY,
        bool BitmapFillEnabled, IImageSource? BitmapFillSource,
        // User-requested (2026-09-15) grow-to-fill toggle -- yoniq-auditor-flagged risk: without this
        // field, Undo/Redo silently reset EVERY text element's own toggle to off (ApplyState's own
        // recreate-every-element-from-a-snapshot path has nowhere else to read it from), a much wider
        // and more visible reset than OverlayElementViewModel.GrowToFillEnabled's own doc comment
        // ("resets on every template reload") implied. Deliberately NOT read by OverlayElementViewModel.ApplyStyleFrom
        // (that method copies fields explicitly, one at a time -- this one is simply never among
        // them), so Copy Style/Paste Style still correctly does NOT carry it between elements, only
        // Undo/Redo/Duplicate/Copy-Paste (which reuse this same record) do.
        bool GrowToFillEnabled)
        : RawElementSnapshot(X, Y, Width, Height, Z, Locked);

    /// <summary><paramref name="GradientEnabled"/>/<paramref name="GradientKind"/>/
    /// <paramref name="GradientStartColor"/>/<paramref name="GradientEndColor"/> (TX editor gap-items
    /// plan, 2026-09-01) mirror <see cref="RawTextElementSnapshot"/>'s own identical fields exactly
    /// -- same simplified 2-stop VM shape, and likewise no parameter defaults.
    /// <para><paramref name="PerspectiveEnabled"/>/<paramref name="Corner0X"/>..<paramref name="Corner3Y"/>
    /// (TX editor gap-items plan, item 3, 2026-09-02) -- without these, the FIRST Undo after enabling
    /// perspective on a box would silently revert the warp, since <c>ApplyState</c>'s own recreate-
    /// every-element-from-a-snapshot path has nowhere else to read the corners from. Same 9-flat-
    /// scalar convention as <see cref="PersistedBoxElement"/>'s own identical fields -- see that
    /// record's own doc comment for why (consistency with every other field here being flat, not a
    /// nested <c>PerspectiveCorners?</c>).</para></summary>
    public sealed record RawBoxElementSnapshot(
        double X, double Y, double Width, double Height, int Z, bool Locked,
        Rgb24 FillColor, Rgb24? BorderColor, double BorderThickness, double Opacity, double CornerRadius,
        bool GradientEnabled, TextGradientKind GradientKind,
        Rgb24? GradientStartColor, Rgb24? GradientEndColor,
        bool PerspectiveEnabled,
        double Corner0X, double Corner0Y, double Corner1X, double Corner1Y,
        double Corner2X, double Corner2Y, double Corner3X, double Corner3Y,
        // Legacy `.mtm` import -- see TemplateBoxElement.FillEnabled's own doc comment.
        bool FillEnabled,
        // Without this, the first Undo after rotating a box would silently revert it, same
        // reasoning as this record's own Perspective fields above.
        double RotationDegrees)
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
        IImageSource Source, ImageFitMode Fit, ImageSourceOrigin Origin, bool IsBackground,
        // 0 = unknown; carried here so ApplyState's own recreate-every-element-from-a-snapshot
        // path doesn't lose this field on the first Undo.
        int NaturalPixelWidth, int NaturalPixelHeight,
        // Same 9-flat-scalar convention as RawBoxElementSnapshot's own Perspective fields; see that
        // record's own doc comment.
        bool PerspectiveEnabled,
        double Corner0X, double Corner0Y, double Corner1X, double Corner1Y,
        double Corner2X, double Corner2Y, double Corner3X, double Corner3Y,
        double RotationDegrees)
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

    /// <summary>Live-capture counterpart to <see cref="EditorInitialState"/> -- for a mid-edit mode
    /// switch (TxControlsPaneViewModel.ReplaceEditorForModeSwitch), which needs to seed a brand new
    /// instance from THIS one's current state rather than from a previously-Applied EditState.
    /// Deliberately mirrors the same field set as the constructor's own <c>initialState</c> seeding
    /// (see the constructor body), all of which is normalized 0..1 against <c>_targetMode</c> and
    /// therefore reflows correctly when handed to a differently-sized target mode.</summary>
    public EditorInitialState CaptureInitialState() =>
        new(CropRect, PreserveAspect, BuildAdjustments(), RawOverlayElements, TemplateVariables);

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
}
