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
/// flow.</summary>
public sealed partial class TxControlsPaneViewModel : Tool
{
    private readonly ISstvSessionService _sstvSession;
    private readonly IImageFileLoader _imageFileLoader;
    private readonly IStockImageLibrary _stockLibrary;
    private readonly IFilePickerService _filePickerService;
    private readonly ILocalizationService _localization;

    private IImageSource? _loadedImage;

    /// <summary>Either a <see cref="StockImageEntry"/> (picked from the strip) or a <see cref="string"/>
    /// file path (browsed) -- retained across a mode change (audited finding: the pre-Phase-4 behavior
    /// of nulling the loaded image on every mode change reads as broken once a stock strip exists,
    /// since clicking a thumbnail then changing mode would silently deselect it with no visible cause).</summary>
    private object? _selectedSource;

    private CancellationTokenSource? _reloadCts;

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
        IFilePickerService filePickerService,
        ILocalizationService localization)
    {
        _sstvSession = sstvSession;
        _imageFileLoader = imageFileLoader;
        _stockLibrary = stockLibrary;
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

            withThumbnails.Add(new StockEntryViewModel(entry, thumbnail));
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

        SelectedFileName = Path.GetFileName(path);
        await LoadSourceAsync(path, CancellationToken.None);
    }

    [RelayCommand]
    private async Task SelectStockImageAsync(StockImageEntry entry)
    {
        ErrorMessage = null;
        SelectedFileName = entry.FileName;
        await LoadSourceAsync(entry, CancellationToken.None);
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

    partial void OnSelectedModeChanged(SstvModeDefinition? value)
    {
        _reloadCts?.Cancel();

        if (_selectedSource is null || value is null)
        {
            _loadedImage = null;
            PreviewImage = null;
            TransmitCommand.NotifyCanExecuteChanged();
            return;
        }

        // Disabled for the WHOLE in-flight reload window, not just at the start (audited finding:
        // OnSelectedModeChanged is synchronous, so the reload below is necessarily fire-and-forget --
        // leaving _loadedImage pointing at the OLD mode's image during that window would let
        // CanTransmit() pass and ISstvEncoder.EncodeAsync throw on the dimension mismatch).
        _loadedImage = null;
        TransmitCommand.NotifyCanExecuteChanged();
        _ = LoadSourceAsync(_selectedSource, CancellationToken.None);
    }

    /// <summary><paramref name="source"/> is either a <see cref="StockImageEntry"/> or a
    /// <see cref="string"/> file path. Cancels and replaces any in-flight reload for a previous
    /// source/mode combination (last-write-wins) -- a superseded attempt's result must never
    /// overwrite a newer one's.</summary>
    private async Task LoadSourceAsync(object source, CancellationToken externalCt)
    {
        if (SelectedMode is not { } mode)
        {
            return;
        }

        _selectedSource = source;

        _reloadCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _reloadCts = cts;

        try
        {
            var image = source switch
            {
                StockImageEntry stockEntry => await _stockLibrary.LoadFullAsync(stockEntry, mode.ImageWidth, mode.ImageHeight, cts.Token),
                string path => await _imageFileLoader.LoadAsync(path, mode.ImageWidth, mode.ImageHeight, cts.Token),
                _ => throw new InvalidOperationException($"Unrecognized TX source type: {source.GetType()}"),
            };

            if (cts.IsCancellationRequested)
            {
                return; // superseded -- the newer reload owns the final state, not this one
            }

            _loadedImage = image;
            PreviewImage = ImageSourceBitmapConverter.ToBitmap(image);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer mode change or source pick -- do nothing, matching the
            // cts.IsCancellationRequested check above for the non-throwing cancellation path.
        }
        catch (Exception)
        {
            if (cts.IsCancellationRequested)
            {
                return; // a stale/canceled attempt's failure must not clobber newer state either
            }

            _loadedImage = null;
            PreviewImage = null;
            ErrorMessage = _localization.GetString("Panes.TxControls.Error.LoadFailed");
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                TransmitCommand.NotifyCanExecuteChanged();
            }
        }
    }
}

public sealed record StockEntryViewModel(StockImageEntry Entry, Bitmap? Thumbnail);
