using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.UI.Imaging;
using ScanlineStudio.UI.Services;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>ui_transition_plan.md step 3 (T1-5 + T2-6): a full-size RX/history viewer -- the live
/// received image and Gallery thumbnails were too small for reading a weak callsign/grid/report,
/// and nothing opened a resizable inspection view. Opened with a snapshot of the entries the
/// operator was browsing (Previous-Frames strip or the Gallery grid) plus a start index; the list
/// is NOT live-updated while open (a delete/refresh happening underneath is handled defensively --
/// see <see cref="LoadCurrentAsync"/>'s own doc comment -- but the plan's own "delete-safety after
/// step 4" verification note means the list itself doesn't need to react live yet).</summary>
public sealed partial class ImageViewerWindowViewModel : ViewModelBase
{
    /// <summary>Comfortably above every real SSTV mode's native resolution (the largest legacy
    /// modes top out well under 1000px on a side) -- <see cref="IReceiveHistoryStore.LoadThumbnailAsync"/>
    /// never upscales past the source's own size (its own <c>FitWithinLongestSide</c> is a no-op
    /// when the image already fits), so this reliably loads at true native resolution without a
    /// dedicated "load full-size" method existing on the store.</summary>
    private const int FullSizeMaxDimension = 4096;

    private const double MinZoom = 0.25;
    private const double MaxZoom = 8.0;
    private const double ZoomStepFactor = 1.25;

    private readonly IReadOnlyList<RxHistoryEntryViewModel> _entries;
    private readonly IReceiveHistoryStore _historyStore;
    private readonly IUrlLauncher _urlLauncher;
    private readonly IClipboardImageService _clipboardImageService;
    private readonly ILocalizationService _localization;
    private readonly ILogger<ImageViewerWindowViewModel> _logger;

    /// <summary>Guards against a stale async load from a superseded navigation overwriting a newer
    /// one -- Previous/Next can fire again before the prior <see cref="LoadCurrentAsync"/> call's
    /// await resolves (fast arrow-key repeat), and there's no cancellation token plumbed through
    /// <see cref="IReceiveHistoryStore.LoadThumbnailAsync"/> to cancel the in-flight one instead.
    /// </summary>
    private int _loadGeneration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current))]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private int _currentIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayWidth))]
    [NotifyPropertyChangedFor(nameof(DisplayHeight))]
    private Bitmap? _fullImage;

    [ObservableProperty]
    private bool _isFitToWindow = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayWidth))]
    [NotifyPropertyChangedFor(nameof(DisplayHeight))]
    [NotifyPropertyChangedFor(nameof(ZoomPercentDisplay))]
    private double _zoomFactor = 1.0;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    public event Action? CloseRequested;

    public ImageViewerWindowViewModel(
        IReadOnlyList<RxHistoryEntryViewModel> entries,
        int startIndex,
        IReceiveHistoryStore historyStore,
        IUrlLauncher urlLauncher,
        IClipboardImageService clipboardImageService,
        ILocalizationService localization,
        ILogger<ImageViewerWindowViewModel> logger)
    {
        _entries = entries;
        _historyStore = historyStore;
        _urlLauncher = urlLauncher;
        _clipboardImageService = clipboardImageService;
        _localization = localization;
        _logger = logger;
        _currentIndex = entries.Count == 0 ? 0 : Math.Clamp(startIndex, 0, entries.Count - 1);
        _ = LoadCurrentAsync();
    }

    public RxHistoryEntryViewModel? Current => CurrentIndex >= 0 && CurrentIndex < _entries.Count ? _entries[CurrentIndex] : null;

    public string PositionText => _entries.Count == 0
        ? string.Empty
        : _localization.GetString("ImageViewer.PositionFormat", CurrentIndex + 1, _entries.Count);

    public double DisplayWidth => FullImage is { } bitmap ? bitmap.PixelSize.Width * ZoomFactor : 0;

    public double DisplayHeight => FullImage is { } bitmap ? bitmap.PixelSize.Height * ZoomFactor : 0;

    public string ZoomPercentDisplay => _localization.GetString("ImageViewer.ZoomPercentFormat", (int)Math.Round(ZoomFactor * 100));

    private bool CanPrevious() => CurrentIndex > 0;

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private void Previous()
    {
        CurrentIndex--;
        _ = LoadCurrentAsync();
    }

    private bool CanNext() => CurrentIndex < _entries.Count - 1;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next()
    {
        CurrentIndex++;
        _ = LoadCurrentAsync();
    }

    [RelayCommand]
    private void FitToWindow()
    {
        IsFitToWindow = true;
        ZoomFactor = 1.0;
    }

    [RelayCommand]
    private void ActualSize()
    {
        IsFitToWindow = false;
        ZoomFactor = 1.0;
    }

    [RelayCommand]
    private void ZoomIn() => ZoomBy(ZoomStepFactor);

    [RelayCommand]
    private void ZoomOut() => ZoomBy(1 / ZoomStepFactor);

    /// <summary>Public (not a bare command body) -- the view's own mouse-wheel handler calls this
    /// directly with a per-notch factor, same shape as <see cref="ZoomIn"/>/<see cref="ZoomOut"/>
    /// reuse for the toolbar buttons rather than duplicating the clamp/fit-mode-exit logic in
    /// code-behind.</summary>
    public void ZoomBy(double factor)
    {
        IsFitToWindow = false;
        ZoomFactor = Math.Clamp(ZoomFactor * factor, MinZoom, MaxZoom);
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (FullImage is not { } bitmap)
        {
            return;
        }

        var copied = await _clipboardImageService.CopyImageAsync(bitmap);
        ErrorMessage = copied ? null : _localization.GetString("ImageViewer.Error.CopyFailed");
    }

    [RelayCommand]
    private void OpenFileLocation()
    {
        if (Current?.Entry.FilePath is not { } path)
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        _urlLauncher.Open(string.IsNullOrEmpty(directory) ? path : directory);
    }

    /// <summary>ui_transition_plan.md step 16 (T1-5 follow-up, Tier 3 accepted 2026-08-29) --
    /// distinct from <see cref="OpenFileLocation"/> above, which opens the containing FOLDER: this
    /// opens the image FILE itself in the OS's own default handler for it (an external image viewer/
    /// editor). <see cref="IUrlLauncher.Open"/> already does this correctly for a file path with no
    /// change needed there -- <c>UseShellExecute = true</c> (its own doc comment) resolves a file
    /// path to its default application the same way it resolves a folder path to the default file
    /// manager, so this is a thin, obvious call, not new plumbing.</summary>
    [RelayCommand]
    private void OpenExternally()
    {
        if (Current?.Entry.FilePath is not { } path)
        {
            return;
        }

        _urlLauncher.Open(path);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    /// <summary>A missing/moved file underneath the viewer (deleted by another session, or --
    /// once step 4's delete lands -- by this same one) surfaces as <see cref="ErrorMessage"/>
    /// rather than a crash or a stale image; the entry itself stays navigable either way.</summary>
    private async Task LoadCurrentAsync()
    {
        var generation = ++_loadGeneration;
        ErrorMessage = null;

        if (Current is not { } entry)
        {
            FullImage = null;
            return;
        }

        IsLoading = true;
        try
        {
            var source = await _historyStore.LoadThumbnailAsync(entry.Entry, FullSizeMaxDimension);
            if (generation != _loadGeneration)
            {
                return;
            }

            FullImage = ImageSourceBitmapConverter.ToBitmap(source);
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration)
            {
                return;
            }

            Log.LoadFullImageFailed(_logger, entry.Entry.FilePath, ex);
            FullImage = null;
            ErrorMessage = _localization.GetString("ImageViewer.Error.LoadFailed");
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                IsLoading = false;
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading full-size image failed: {FilePath}")]
        public static partial void LoadFullImageFailed(ILogger logger, string filePath, Exception exception);
    }
}
