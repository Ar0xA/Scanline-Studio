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
    /// <summary>Every field of a brand-new image element. A factory, not a static instance, because
    /// <paramref name="source"/> and <paramref name="origin"/> are non-nullable and have no default.</summary>
    private static RawImageElementSnapshot NewImageElementDefaults(IImageSource source, ImageSourceOrigin origin) => new(
        X: 0, Y: 0, Width: 0, Height: 0, Z: 0, Locked: false,
        Source: source, Fit: ImageFitMode.Contain, Origin: origin, IsBackground: false,
        NaturalPixelWidth: 0, NaturalPixelHeight: 0,
        PerspectiveEnabled: false,
        Corner0X: 0, Corner0Y: 0, Corner1X: 0, Corner1Y: 0,
        Corner2X: 0, Corner2Y: 0, Corner3X: 0, Corner3Y: 0,
        RotationDegrees: 0);

    /// <summary>Shared element-construction wiring for <see cref="AddOverlayElement"/> and
    /// restoration (<see cref="CreateElementFromSnapshot"/>) -- round-1 plan-review finding on
    /// spec/18-path-to-1.0.md's re-open/re-edit sub-piece, still the right call in Phase 1: keep
    /// this in exactly one place, not duplicated between "new blank element" and "restored element"
    /// call sites. Every field is read from <paramref name="s"/>; a brand-new element starts from
    /// <see cref="NewTextElementDefaults"/>.</summary>
    private OverlayElementViewModel CreateOverlayElement(RawTextElementSnapshot s)
    {
        var element = new OverlayElementViewModel
        {
            Text = s.Text,
            X = s.X,
            Y = s.Y,
            Width = s.Width,
            Height = s.Height,
            FontSizeRelative = s.FontSizeRelative,
            Color = s.Color,
            // "" stays ""; null (only from a hand-edited template.json) falls back to the preparer default.
            FontFamily = s.FontFamily ?? _preparer.AvailableFontFamilies.FirstOrDefault(string.Empty),
            StrokeColor = s.StrokeColor,
            StrokeThickness = s.StrokeThickness,
            ShadowColor = s.ShadowColor,
            ShadowOffsetX = s.ShadowOffsetX,
            ShadowOffsetY = s.ShadowOffsetY,
            RotationDegrees = s.RotationDegrees,
            GradientEnabled = s.GradientEnabled,
            GradientKind = s.GradientKind,
            // Null-coalesced to the VM's own field-initializer defaults (255,0,0)/(0,0,255) -- a
            // record constructor parameter default can't call a struct constructor (not a compile-
            // time constant), which is why RawTextElementSnapshot's own Gradient*Color fields are
            // nullable even though the VM's own are not; this is where that gets reconciled back.
            GradientStartColor = s.GradientStartColor ?? new Rgb24(255, 0, 0),
            GradientEndColor = s.GradientEndColor ?? new Rgb24(0, 0, 255),
            Bold = s.Bold,
            Italic = s.Italic,
            StackColor = s.StackColor,
            StackStepX = s.StackStepX,
            StackStepY = s.StackStepY,
            // TX editor gap-items plan, item 4b -- Source before Enabled (round-1 code-review
            // finding, matches every other write-both-together assignment in this file). Both
            // assigned AFTER Gradient* above -- if a hand-edited/imported template somehow has both
            // enabled, BitmapFillEnabled's own mutual-exclusion setter clears the GradientEnabled
            // that was just set, so the VM's post-construction state self-heals to the documented
            // precedence winner (BitmapFill) rather than staying ambiguous; the Source/Enabled
            // ordering between THEMSELVES doesn't affect that, Source's own setter never touches
            // Gradient.
            BitmapFillSource = s.BitmapFillSource,
            BitmapFillEnabled = s.BitmapFillEnabled,
            GrowToFillEnabled = s.GrowToFillEnabled,
            Z = s.Z,
            Locked = s.Locked,
            ImageWidth = CanvasDisplayWidth,
            ImageHeight = CanvasDisplayHeight,
            // Quick Style Flyout (TX workflow modernization plan, Phase 1) -- see its own doc
            // comment; _target is ctor-only/never reassigned, so this constant stays correct
            // for the element's whole lifetime.
            TargetModeHeightPx = _target.Height,
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
            RotateClockwise90Command = RotateClockwise90Command,
            RotateCounterclockwise90Command = RotateCounterclockwise90Command,
            Rotate180Command = Rotate180Command,
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
    /// CanvasFontSize (boxes don't shrink-to-fit). A brand-new box starts from
    /// <see cref="NewBoxElementDefaults"/>.</summary>
    private BoxElementViewModel CreateBoxElement(RawBoxElementSnapshot s)
    {
        var element = new BoxElementViewModel
        {
            X = s.X,
            Y = s.Y,
            Width = s.Width,
            Height = s.Height,
            FillColor = s.FillColor,
            FillEnabled = s.FillEnabled,
            BorderColor = s.BorderColor,
            BorderThickness = s.BorderThickness,
            Opacity = s.Opacity,
            CornerRadius = s.CornerRadius,
            GradientEnabled = s.GradientEnabled,
            GradientKind = s.GradientKind,
            // Same null-means-"use the VM's own default" fallback CreateOverlayElement's identical
            // Gradient*Color fields already use -- a caller who never touches gradients at all
            // (every non-Phase-8 site) never has to know or care what these defaults are.
            GradientStartColor = s.GradientStartColor ?? new Rgb24(255, 0, 0),
            GradientEndColor = s.GradientEndColor ?? new Rgb24(0, 0, 255),
            // Perspective transform, item 3 -- required construction ORDER (round-1/round-3 plan-
            // review blockers): the 8 corners MUST be assigned before PerspectiveEnabled, or every
            // intermediate corner assignment would read a partially-populated quad through the
            // mode-switched X/Width getters (harmless here since nothing re-reads them mid-
            // initializer, but the wrong general habit to establish); PerspectiveEnabled MUST be
            // assigned before the undo-delegate lines below, or a real, undone restore's own
            // construction-time write would push a spurious undo step.
            Corner0X = s.Corner0X,
            Corner0Y = s.Corner0Y,
            Corner1X = s.Corner1X,
            Corner1Y = s.Corner1Y,
            Corner2X = s.Corner2X,
            Corner2Y = s.Corner2Y,
            Corner3X = s.Corner3X,
            Corner3Y = s.Corner3Y,
            PerspectiveEnabled = s.PerspectiveEnabled,
            RotationDegrees = s.RotationDegrees,
            Z = s.Z,
            Locked = s.Locked,
            ImageWidth = CanvasDisplayWidth,
            ImageHeight = CanvasDisplayHeight,
            TargetModeHeightPx = _target.Height,
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
            RotateClockwise90Command = RotateClockwise90Command,
            RotateCounterclockwise90Command = RotateCounterclockwise90Command,
            Rotate180Command = Rotate180Command,
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
            // Element rotation (2026-09-20): RotationDegrees is threaded through here for a
            // faithful round-trip (Flatten's stale-result guard, plan §6/round-2 fix) even though
            // this composer only ever runs while PerspectiveEnabled is true (see its only caller,
            // PerspectiveElementViewModelBase.RebuildWarpedPreview), which under the
            // Rotation-vs-Perspective mutual-exclusivity rule means it's always 0
            // here in practice. Deliberately NOT consumed by a rotation branch below -- the canvas
            // already gets its rotation from the AXAML RenderTransform, and a rotation branch here
            // would double-rotate if this composer's PerspectiveEnabled-only invariant ever changed.
            ImageElementViewModel image => new TemplateImageElement(
                image.Corners.ToBoundingBox(),
                Z: 0, image.Source, image.Fit,
                image.Corners,
                image.RotationDegrees),
            BoxElementViewModel box => new TemplateBoxElement(
                box.Corners.ToBoundingBox(),
                Z: 0, box.FillColor, box.BorderColor, box.BorderThickness, box.Opacity, box.CornerRadius,
                box.GradientEnabled
                    ? new TextGradient(box.GradientKind, [new GradientColorStop(0f, box.GradientStartColor), new GradientColorStop(1f, box.GradientEndColor)])
                    : null,
                box.Corners,
                box.FillEnabled, box.RotationDegrees),
            _ => throw new NotSupportedException($"Unrecognized {nameof(ITemplateElementViewModel)}: {element.GetType()}."),
        };
        var scaleY = ComputeTargetToCanvasScaleY();
        var displayHeight = element.Height * CanvasDisplayHeight;
        var styleHeight = scaleY > 0 && displayHeight > 0
            ? (_target.Height / scaleY) * (targetHeightPx / displayHeight)
            : targetHeightPx;
        return _preparer.RenderWarpedElementPreview(templateElement, targetWidthPx, targetHeightPx, styleHeight);
    }

    /// <summary>Line counterpart to <see cref="CreateOverlayElement"/>/<see cref="CreateBoxElement"/>
    /// -- same wiring shape, TX editor gap-items plan (2026-09-01). Reads the endpoints, not
    /// center/width/height, since a line's real geometry is the two endpoints; a brand-new line
    /// starts from <see cref="NewLineElementDefaults"/>. <b>Object initializer sets X1/Y1/X2/Y2 directly,
    /// deliberately NOT X/Y/Width/Height</b> (plan-review risk finding) -- unlike
    /// <see cref="BoxElementViewModel"/>, where X/Y/Width/Height ARE the real stored fields,
    /// <see cref="LineElementViewModel"/>'s X/Y/Width/Height are DERIVED get/set properties; assigning
    /// them here would run the derived setters against still-default/zero endpoints in
    /// object-initializer-assignment order, an unnecessary and undefined-feeling detour when the
    /// real fields are directly assignable.</summary>
    private LineElementViewModel CreateLineElement(RawLineElementSnapshot s)
    {
        var element = new LineElementViewModel
        {
            // Endpoints (X1/Y1/X2/Y2), NOT the base X/Y/Width/Height -- RawLineElementSnapshot's own
            // doc comment: the base fields are derived-for-uniformity-on-write only, endpoints are the
            // sole truth on read.
            X1 = s.X1,
            Y1 = s.Y1,
            X2 = s.X2,
            Y2 = s.Y2,
            StrokeColor = s.StrokeColor,
            StrokeThickness = s.StrokeThickness,
            Opacity = s.Opacity,
            Z = s.Z,
            Locked = s.Locked,
            ImageWidth = CanvasDisplayWidth,
            ImageHeight = CanvasDisplayHeight,
            TargetModeHeightPx = _target.Height,
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
            RotateClockwise90Command = RotateClockwise90Command,
            RotateCounterclockwise90Command = RotateCounterclockwise90Command,
            Rotate180Command = Rotate180Command,
            // Set LAST, after X1/Y1/X2/Y2/StrokeColor/StrokeThickness/Opacity above -- same
            // "avoid a spurious undo push from element creation itself" ordering trick
            // CreateBoxElement's own identical comment documents.
            PushUndoSnapshotForGeometryChange = () => PushUndoSnapshotCoalesced("OverlayGeometry"),
            PushUndoSnapshotForStyleChange = () => PushUndoSnapshotCoalesced("LineStyle"),
        };
        element.PropertyChanged += OnOverlayElementPropertyChanged;
        // Assigned post-construction, same reasoning as ImageElementViewModel.RenderWarpedPreview's
        // own identical pattern -- must close over the constructed element itself, not in scope
        // inside its own object initializer.
        element.ApplyRotationDelta = delta =>
        {
            RotateLineEndpoints(element, delta);
            RecomputePreview();
        };
        RefreshElementPreviewMetrics(element);
        return element;
    }

    /// <summary>Image counterpart to <see cref="CreateOverlayElement"/>/<see cref="CreateBoxElement"/>
    /// -- same wiring shape, Phase 2 (spec/15-template-designer.md). A brand-new image starts from
    /// <see cref="NewImageElementDefaults"/>.</summary>
    private ImageElementViewModel CreateImageElement(RawImageElementSnapshot s)
    {
        var element = new ImageElementViewModel(s.Source)
        {
            X = s.X,
            Y = s.Y,
            Width = s.Width,
            Height = s.Height,
            Fit = s.Fit,
            Origin = s.Origin,
            // See CreateBoxElement's own comment for the required construction order (corners before
            // PerspectiveEnabled, PerspectiveEnabled before the undo delegate).
            Corner0X = s.Corner0X,
            Corner0Y = s.Corner0Y,
            Corner1X = s.Corner1X,
            Corner1Y = s.Corner1Y,
            Corner2X = s.Corner2X,
            Corner2Y = s.Corner2Y,
            Corner3X = s.Corner3X,
            Corner3Y = s.Corner3Y,
            PerspectiveEnabled = s.PerspectiveEnabled,
            RotationDegrees = s.RotationDegrees,
            Z = s.Z,
            Locked = s.Locked,
            IsBackground = s.IsBackground,
            NaturalPixelWidth = s.NaturalPixelWidth,
            NaturalPixelHeight = s.NaturalPixelHeight,
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
            RotateClockwise90Command = RotateClockwise90Command,
            RotateCounterclockwise90Command = RotateCounterclockwise90Command,
            Rotate180Command = Rotate180Command,
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
        RawTextElementSnapshot text => CreateOverlayElement(text),
        RawBoxElementSnapshot box => CreateBoxElement(box),
        RawImageElementSnapshot image => CreateImageElement(image),
        RawLineElementSnapshot line => CreateLineElement(line),
        _ => throw new NotSupportedException($"Unrecognized {nameof(RawElementSnapshot)}: {snapshot.GetType()}."),
    };

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
            box.FillEnabled, box.RotationDegrees);
    }

    private TemplateImageElement BuildImageTemplateElement(ImageElementViewModel image, NormalizedRect naturalBounds)
    {
        var corners = TryProjectPerspectiveCorners(image);
        return new TemplateImageElement(corners?.ToBoundingBox() ?? naturalBounds, image.Z, image.Source, image.Fit, corners, image.RotationDegrees);
    }

    private TemplateLineElement BuildTemplateLineElement(LineElementViewModel line)
    {
        var p1 = ProjectRectToCropRelative(line.X1, line.Y1, 0, 0);
        var p2 = ProjectRectToCropRelative(line.X2, line.Y2, 0, 0);
        var lineBounds = TemplateLineGeometry.ComputeInflatedBounds(
            p1.X, p1.Y, p2.X, p2.Y, line.StrokeThickness,
            imageWidthPx: _target.Width, imageHeightPx: _target.Height);
        return new TemplateLineElement(lineBounds, line.Z, p1.X, p1.Y, p2.X, p2.Y, line.StrokeColor, line.StrokeThickness, line.Opacity);
    }
}
