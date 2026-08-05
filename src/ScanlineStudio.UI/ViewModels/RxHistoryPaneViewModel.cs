using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.UI.Imaging;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Browsable list of past received images, sourced by the fixed Gallery tab (and the
/// Receive tab's "Previous frames" strip) — see spec/07-image-pipeline.md's "RX history" section.
/// Deliberately does NOT depend on <c>ISstvSessionService</c> or touch
/// <see cref="ScanlineStudio.Abstractions.Imaging.IReceivedImageBuffer"/> at all — selecting a
/// history entry loads a separate, read-only <see cref="PreviewImage"/>; browsing history must
/// never appear to interrupt or corrupt a live RX decode in progress
/// (<c>RxImagePaneViewModel</c> owns that live binding exclusively).</summary>
public sealed partial class RxHistoryPaneViewModel : ViewModelBase
{
    private const int ThumbnailMaxDimension = 96;
    private const int PreviewMaxDimension = 512;

    private readonly IReceiveHistoryStore _historyStore;

    [ObservableProperty]
    private RxHistoryEntryViewModel? _selectedEntry;

    [ObservableProperty]
    private Bitmap? _previewImage;

    /// <summary>Gallery tab's All/Today filter (spec/09-ui.md) -- real, backed by
    /// <see cref="IReceiveHistoryStore.QueryAsync"/>'s own <c>From</c>/<c>To</c> filter fields.
    /// Defaults to <see langword="true"/>, matching the mock2 draft's own default selection.
    /// Band/Unlogged/Flagged/free-text filters from that same draft are omitted -- there is no
    /// frequency/callsign/grid field on <see cref="ReceiveHistoryEntry"/> to filter by, and
    /// <c>LinkedQsoId</c> is never set to anything but <see langword="null"/> by any code path
    /// today (spec/14-roadmap.md backlog).</summary>
    [ObservableProperty]
    private bool _showTodayOnly = true;

    /// <summary>Resolved saved-image folder for the Gallery tab's Storage card -- real, loaded
    /// once via <see cref="IReceiveHistoryStore.GetImagesDirectoryAsync"/>.</summary>
    [ObservableProperty]
    private string? _imagesDirectory;

    public RxHistoryPaneViewModel(IReceiveHistoryStore historyStore)
    {
        _historyStore = historyStore;

        // Best-effort initial load -- a failure here (e.g. history store not reachable yet) leaves
        // the pane empty rather than blocking construction; RefreshCommand lets the user retry.
        _ = RefreshAsync();
        _ = LoadImagesDirectoryAsync();
    }

    public ObservableCollection<RxHistoryEntryViewModel> Entries { get; } = [];

    partial void OnShowTodayOnlyChanged(bool value) => _ = RefreshAsync();

    private async Task LoadImagesDirectoryAsync()
    {
        try
        {
            ImagesDirectory = await _historyStore.GetImagesDirectoryAsync();
        }
        catch
        {
            // Best-effort, same reasoning as RefreshAsync -- the Storage card just shows nothing.
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var filter = ShowTodayOnly
            ? new ReceiveHistoryFilter(From: new DateTimeOffset(DateTime.Today))
            : new ReceiveHistoryFilter();

        IReadOnlyList<ReceiveHistoryEntry> entries;
        try
        {
            entries = await _historyStore.QueryAsync(filter);
        }
        catch
        {
            return;
        }

        var thumbnails = new List<RxHistoryEntryViewModel>(entries.Count);
        foreach (var entry in entries)
        {
            Bitmap? thumbnail = null;
            try
            {
                var image = await _historyStore.LoadThumbnailAsync(entry, ThumbnailMaxDimension);
                thumbnail = ImageSourceBitmapConverter.ToBitmap(image);
            }
            catch
            {
                // A missing/corrupt file for one entry must not blank the whole list -- that entry
                // just renders without a thumbnail.
            }

            thumbnails.Add(new RxHistoryEntryViewModel(entry, thumbnail));
        }

        Entries.Clear();
        foreach (var item in thumbnails)
        {
            Entries.Add(item);
        }
    }

    partial void OnSelectedEntryChanged(RxHistoryEntryViewModel? value)
    {
        PreviewImage = null;
        if (value is null)
        {
            return;
        }

        _ = LoadPreviewAsync(value.Entry);
    }

    private async Task LoadPreviewAsync(ReceiveHistoryEntry entry)
    {
        try
        {
            var image = await _historyStore.LoadThumbnailAsync(entry, PreviewMaxDimension);
            PreviewImage = ImageSourceBitmapConverter.ToBitmap(image);
        }
        catch
        {
            PreviewImage = null;
        }
    }
}

public sealed record RxHistoryEntryViewModel(ReceiveHistoryEntry Entry, Bitmap? Thumbnail);
