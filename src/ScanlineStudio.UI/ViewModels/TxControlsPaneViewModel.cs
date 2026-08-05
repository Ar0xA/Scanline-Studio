using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.UI.Imaging;
using ScanlineStudio.UI.Services;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Real pane #3 of 3 (Phase-3 plan decision #9). Phase 4 added the inline stock/template
/// picker (spec/07-image-pipeline.md's "Stock image library" section) alongside the original browse
/// flow. The TX image editor pass (spec/07-image-pipeline.md's "TX image editor" section) changed
/// picking a source from "auto-resize straight to mode dimensions" to "load at native resolution,
/// then open the editor for crop/resize/stretch/overlay" -- this VM owns that hand-off (constructing
/// the editor, reacting to Applied/Cancelled) but never touches Dock/window placement itself; see
/// <see cref="EditorOpened"/>/<see cref="EditorClosed"/>.</summary>
public sealed partial class TxControlsPaneViewModel : Tool
{
    private readonly ISstvSessionService _sstvSession;
    private readonly IImageFileLoader _imageFileLoader;
    private readonly IStockImageLibrary _stockLibrary;
    private readonly ITransmitImagePreparer _preparer;
    private readonly IFilePickerService _filePickerService;
    private readonly ILocalizationService _localization;

    private IImageSource? _loadedImage;

    /// <summary>The native-resolution original plus the crop/stretch/overlay choices applied to it --
    /// retained (not just the final mode-sized image) so a later mode change can re-run
    /// Crop→Resize→ApplyOverlay against the *new* mode's dimensions instead of stretching/cropping an
    /// already-cropped image a second time. Normalized (0..1) coordinates make this composition valid
    /// across modes with different pixel dimensions.</summary>
    private sealed record EditState(IImageSource Original, NormalizedRect CropRect, bool PreserveAspect, ImageOverlay Overlay);

    private EditState? _editState;

    /// <summary>Guards against a second pick starting while an editor is already open/loading --
    /// simpler than a cancel-and-replace token for what's a single, short-lived, user-driven
    /// sequence (the view is expected to disable picking while an editor is open; this is the
    /// view-model-level backstop).</summary>
    private bool _isEditorOpen;

    [ObservableProperty]
    private SstvModeDefinition? _selectedMode;

    [ObservableProperty]
    private Bitmap? _previewImage;

    [ObservableProperty]
    private string? _selectedFileName;

    [ObservableProperty]
    private bool _isTransmitting;

    [ObservableProperty]
    private string? _errorMessage;

    public TxControlsPaneViewModel(
        ISstvSessionService sstvSession,
        IImageFileLoader imageFileLoader,
        IStockImageLibrary stockLibrary,
        ITransmitImagePreparer preparer,
        IFilePickerService filePickerService,
        ILocalizationService localization)
    {
        _sstvSession = sstvSession;
        _imageFileLoader = imageFileLoader;
        _stockLibrary = stockLibrary;
        _preparer = preparer;
        _filePickerService = filePickerService;
        _localization = localization;

        Id = "TxControls";
        Title = localization.GetString("Panes.TxControls.Title");
        localization.CultureChanged += () => Title = localization.GetString("Panes.TxControls.Title");

        AvailableModes = sstvSession.AvailableModes;
        _selectedMode = AvailableModes.Count > 0 ? AvailableModes[0] : null;

        // Best-effort initial load, same reasoning as RxHistoryPaneViewModel's constructor -- a
        // failure here leaves the strip empty rather than blocking construction.
        _ = RefreshStockLibraryAsync();
    }

    /// <summary>Fired when a picked source's original image has loaded and a
    /// <see cref="TxImageEditorPaneViewModel"/> is ready to be shown -- the host (<c>AppDockFactory</c>)
    /// owns turning this into an actual dockable pane; this VM only knows about the editor's own
    /// Applied/Cancelled events, never about Dock/window placement.</summary>
    public event Action<TxImageEditorPaneViewModel>? EditorOpened;

    /// <summary>Fired once the editor resolves (Applied or Cancelled either one) -- the host should
    /// remove the editor pane at this point.</summary>
    public event Action? EditorClosed;

    private const int StockThumbnailMaxDimension = 64;

    public IReadOnlyList<SstvModeDefinition> AvailableModes { get; }

    public ObservableCollection<StockEntryViewModel> StockEntries { get; } = [];

    [RelayCommand]
    private async Task RefreshStockLibraryAsync()
    {
        IReadOnlyList<StockImageEntry> entries;
        try
        {
            entries = await _stockLibrary.ListAsync();
        }
        catch
        {
            return; // Best-effort -- see constructor's own comment.
        }

        var withThumbnails = new List<StockEntryViewModel>(entries.Count);
        foreach (var entry in entries)
        {
            Bitmap? thumbnail = null;
            try
            {
                var image = await _stockLibrary.LoadThumbnailAsync(entry, StockThumbnailMaxDimension);
                thumbnail = ImageSourceBitmapConverter.ToBitmap(image);
            }
            catch
            {
                // A missing/corrupt file for one entry must not blank the whole strip -- see
                // RxHistoryPaneViewModel.RefreshAsync's identical reasoning.
            }

            withThumbnails.Add(new StockEntryViewModel(entry, thumbnail, SelectStockImageCommand));
        }

        StockEntries.Clear();
        foreach (var item in withThumbnails)
        {
            StockEntries.Add(item);
        }
    }

    [RelayCommand]
    private async Task SelectImageAsync()
    {
        ErrorMessage = null;
        var path = await _filePickerService.PickImageFileAsync();
        if (path is null)
        {
            return;
        }

        await OpenEditorForSourceAsync(path, Path.GetFileName(path));
    }

    [RelayCommand]
    private async Task SelectStockImageAsync(StockImageEntry entry)
    {
        ErrorMessage = null;
        await OpenEditorForSourceAsync(entry, entry.FileName);
    }

    /// <summary><paramref name="source"/> is either a <see cref="StockImageEntry"/> or a
    /// <see cref="string"/> file path. Loads it at native resolution and hands the resulting
    /// <see cref="TxImageEditorPaneViewModel"/> to whoever is listening on <see cref="EditorOpened"/> --
    /// this VM does not touch Dock/window placement itself.</summary>
    private async Task OpenEditorForSourceAsync(object source, string fileName)
    {
        if (_isEditorOpen || SelectedMode is not { })
        {
            return;
        }

        _isEditorOpen = true;
        IImageSource original;
        try
        {
            original = source switch
            {
                StockImageEntry stockEntry => await _stockLibrary.LoadOriginalAsync(stockEntry),
                string path => await _imageFileLoader.LoadOriginalAsync(path),
                _ => throw new InvalidOperationException($"Unrecognized TX source type: {source.GetType()}"),
            };
        }
        catch (Exception)
        {
            _isEditorOpen = false;
            ErrorMessage = _localization.GetString("Panes.TxControls.Error.LoadFailed");
            return;
        }

        // SelectedMode may have changed while the original was loading -- always target whatever
        // mode is current NOW, not the one in effect when the pick started.
        if (SelectedMode is not { } mode)
        {
            _isEditorOpen = false;
            return;
        }

        var editor = new TxImageEditorPaneViewModel(original, mode, _preparer, _localization);
        editor.Applied += final => OnEditorApplied(fileName, original, editor, final);
        editor.Cancelled += OnEditorCancelled;
        EditorOpened?.Invoke(editor);
    }

    private void OnEditorApplied(string fileName, IImageSource original, TxImageEditorPaneViewModel editor, IImageSource final)
    {
        _editState = new EditState(original, editor.CropRect, editor.PreserveAspect, editor.Overlay);
        _loadedImage = final;
        PreviewImage = ImageSourceBitmapConverter.ToBitmap(final);
        SelectedFileName = fileName;
        ErrorMessage = null;
        _isEditorOpen = false;
        TransmitCommand.NotifyCanExecuteChanged();
        EditorClosed?.Invoke();
    }

    private void OnEditorCancelled()
    {
        _isEditorOpen = false;
        EditorClosed?.Invoke();
    }

    private bool CanTransmit() => _loadedImage is not null && !IsTransmitting;

    [RelayCommand(CanExecute = nameof(CanTransmit))]
    private async Task TransmitAsync()
    {
        if (_loadedImage is not { } image || SelectedMode is not { } mode)
        {
            return;
        }

        ErrorMessage = null;
        IsTransmitting = true;
        TransmitCommand.NotifyCanExecuteChanged();
        try
        {
            await _sstvSession.TransmitAsync(mode, image);
        }
        catch (Exception)
        {
            ErrorMessage = _localization.GetString("Panes.TxControls.Error.TransmitFailed");
        }
        finally
        {
            IsTransmitting = false;
            TransmitCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Re-runs Crop→Resize→ApplyOverlay against the retained <see cref="EditState"/>'s
    /// original at the new mode's dimensions -- normalized (0..1) crop/overlay coordinates make this
    /// valid across modes. This is synchronous CPU work against an already-in-memory original (no
    /// file/network I/O like the old flat resize-reload did), so there is no async race to guard
    /// against here -- the old cancel-and-replace <c>CancellationTokenSource</c> machinery existed
    /// specifically for that I/O race and would be unused complexity now that the source is cached.</summary>
    partial void OnSelectedModeChanged(SstvModeDefinition? value)
    {
        if (value is null || _editState is not { } edit)
        {
            _loadedImage = null;
            PreviewImage = null;
            TransmitCommand.NotifyCanExecuteChanged();
            return;
        }

        var cropped = _preparer.Crop(edit.Original, edit.CropRect);
        var resized = _preparer.Resize(cropped, value.ImageWidth, value.ImageHeight, edit.PreserveAspect);
        var final = _preparer.ApplyOverlay(resized, edit.Overlay);

        _loadedImage = final;
        PreviewImage = ImageSourceBitmapConverter.ToBitmap(final);
        TransmitCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>Carries its own <see cref="SelectCommand"/> (the parent's
/// <see cref="TxControlsPaneViewModel.SelectStockImageCommand"/>, set once at construction) rather
/// than making the View reach back into an ancestor's DataContext with a cross-DataTemplate
/// type-cast binding -- that pattern (<c>$parent[ListBox].((vm:TxControlsPaneViewModel)DataContext)</c>)
/// compiled fine but crashed at runtime the first time this app actually ran with a non-empty stock
/// library ("Unable to resolve type ... from any of the following locations" -- Avalonia falls back
/// to a reflection-mode binding for this shape of path inside a deferred `ItemsControl` template,
/// and that resolver couldn't find the type). Found and fixed during Piece 5c's own hands-on
/// verification, not part of that piece's original scope.</summary>
public sealed record StockEntryViewModel(StockImageEntry Entry, Bitmap? Thumbnail, System.Windows.Input.ICommand SelectCommand);
