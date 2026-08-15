using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;
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
    private const double MinNormalizedCropSize = 0.02;

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
    private readonly ILogger<TxImageEditorPaneViewModel> _logger;

    // Suppresses RecomputePreview() while RotateCommand is mid-update (rotated working copy but
    // not-yet-transformed CropRect/overlay positions) -- without this, each overlay element's own
    // ImageWidth/ImageHeight PropertyChanged (now real notifications, see OverlayElementViewModel)
    // would each trigger a full Crop+Resize+ApplyOverlay against transiently inconsistent state.
    private bool _suspendPreview;

    [ObservableProperty]
    private NormalizedRect _cropRect = new(0, 0, 1, 1);

    [ObservableProperty]
    private bool _preserveAspect = true;

    [ObservableProperty]
    private Bitmap? _workingCopyBitmap;

    [ObservableProperty]
    private Bitmap? _previewImage;

    [ObservableProperty]
    private OverlayElementViewModel? _selectedOverlayElement;

    public TxImageEditorPaneViewModel(
        IImageSource originalSource,
        SstvModeDefinition targetMode,
        ITransmitImagePreparer preparer,
        IMacroTextResolver macroTextResolver,
        OperatorSettings operatorSettings,
        ILogger<TxImageEditorPaneViewModel> logger)
    {
        _originalSource = originalSource;
        _targetMode = targetMode;
        _preparer = preparer;
        _macroTextResolver = macroTextResolver;
        _operatorSettings = operatorSettings;
        _logger = logger;

        _workingCopy = BuildWorkingCopy(originalSource, targetMode, preparer);
        WorkingCopyBitmap = ImageSourceBitmapConverter.ToBitmap(_workingCopy);
        RecomputePreview();
    }

    public ObservableCollection<OverlayElementViewModel> OverlayElements { get; } = [];

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

    public event Action<IImageSource>? Applied;

    public event Action? Cancelled;

    /// <summary>Legacy's real precision mechanism, verified directly against
    /// `yoniq-old/YONIQ-main/PicRect.cpp:925-1001` (not assumed): plain arrow = 1px move,
    /// Ctrl+arrow = 16px move. Pixel deltas are computed against the ORIGINAL source's resolution
    /// (not the downsampled working copy) so nudge precision matches legacy's real granularity
    /// regardless of how small the interactive working copy is.</summary>
    public void NudgeCropMove(NudgeDirection direction, bool ctrl)
    {
        var delta = ctrl ? 16 : 1;
        var (dxPixels, dyPixels) = DirectionToPixelDelta(direction, delta);
        ApplyCropMove((double)dxPixels / _originalSource.Width, (double)dyPixels / _originalSource.Height);
    }

    /// <summary>Shift+arrow resizes the crop rect's bottom-right corner by 1px and auto-engages
    /// stretch mode (<see cref="PreserveAspect"/> = false) -- legacy's own real behavior
    /// (`SBStrach->Down = TRUE` in the same verified source).</summary>
    public void NudgeCropResize(NudgeDirection direction)
    {
        PreserveAspect = false;
        var (dxPixels, dyPixels) = DirectionToPixelDelta(direction, 1);
        ApplyCropResize((double)dxPixels / _originalSource.Width, (double)dyPixels / _originalSource.Height);
    }

    public void DragCropMove(double dxNormalized, double dyNormalized) => ApplyCropMove(dxNormalized, dyNormalized);

    public void DragCropResize(double dxNormalized, double dyNormalized) => ApplyCropResize(dxNormalized, dyNormalized);

    [RelayCommand]
    private void AddOverlayElement()
    {
        var element = new OverlayElementViewModel
        {
            ImageWidth = WorkingCopyWidth,
            ImageHeight = WorkingCopyHeight,
            RemoveCommand = RemoveOverlayElementCommand,
            ResolveMacros = text => _macroTextResolver.Resolve(text, _operatorSettings),
        };
        element.PropertyChanged += OnOverlayElementPropertyChanged;
        OverlayElements.Add(element);
        SelectedOverlayElement = element;
        RecomputePreview();
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
        var final = _preparer.ApplyOverlay(resized, BuildOverlay());
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

        // BuildWorkingCopy's own small-image fast path can return the source instance itself
        // (already covered by Constructor_OriginalWithinWorkingCopyBudget_UsesOriginalDirectlyAsWorkingCopy)
        // -- captured BEFORE reassigning _originalSource below, so a shared instance stays shared
        // (one Rotate call, not two independent copies where one used to be the same object).
        var wasShared = ReferenceEquals(_workingCopy, _originalSource);
        _originalSource = _preparer.Rotate(_originalSource);
        _workingCopy = wasShared ? _originalSource : _preparer.Rotate(_workingCopy);

        _suspendPreview = true;
        try
        {
            WorkingCopyBitmap = ImageSourceBitmapConverter.ToBitmap(_workingCopy);
            OnPropertyChanged(nameof(WorkingCopyWidth));
            OnPropertyChanged(nameof(WorkingCopyHeight));

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
                element.ImageWidth = WorkingCopyWidth;
                element.ImageHeight = WorkingCopyHeight;
            }

            CropRect = TransformCropRectClockwise(CropRect);
        }
        finally
        {
            _suspendPreview = false;
        }

        NotifyCropRectDerivedPropertiesAndRecomputePreview();
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
    }

    partial void OnPreserveAspectChanged(bool value) => RecomputePreview();

    private void OnOverlayElementPropertyChanged(object? sender, PropertyChangedEventArgs e) => RecomputePreview();

    private void ApplyCropMove(double dx, double dy)
    {
        var newX = Math.Clamp(CropRect.X + dx, 0, 1 - CropRect.Width);
        var newY = Math.Clamp(CropRect.Y + dy, 0, 1 - CropRect.Height);
        CropRect = CropRect with { X = newX, Y = newY };
    }

    private void ApplyCropResize(double dx, double dy)
    {
        var newWidth = Math.Clamp(CropRect.Width + dx, MinNormalizedCropSize, 1 - CropRect.X);
        var newHeight = Math.Clamp(CropRect.Height + dy, MinNormalizedCropSize, 1 - CropRect.Y);
        CropRect = CropRect with { Width = newWidth, Height = newHeight };
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
        var overlaid = _preparer.ApplyOverlay(resized, BuildOverlay());
        PreviewImage = ImageSourceBitmapConverter.ToBitmap(overlaid);
    }

    private ImageOverlay BuildOverlay() => new(OverlayElements.Select(e => e.ToImageOverlayElement()).ToList());

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
