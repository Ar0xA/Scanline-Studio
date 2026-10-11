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
}
