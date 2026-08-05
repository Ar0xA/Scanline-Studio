using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.UI.Imaging;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>New Phase-4 pane — see spec/07-image-pipeline.md's "Navigation: dockable panes, not
/// legacy's paged main window" section. Deliberately does NOT depend on <c>ISstvSessionService</c>
/// or touch <see cref="ScanlineStudio.Abstractions.Imaging.IReceivedImageBuffer"/> at all — selecting
/// a history entry loads a separate, read-only <see cref="PreviewImage"/>; browsing history must
/// never appear to interrupt or corrupt a live RX decode in progress
/// (<c>RxImagePaneViewModel</c> owns that live binding exclusively).</summary>
public sealed partial class RxHistoryPaneViewModel : Tool
{
    private const int ThumbnailMaxDimension = 96;
    private const int PreviewMaxDimension = 512;

    private readonly IReceiveHistoryStore _historyStore;
    private readonly ILocalizationService _localization;

    [ObservableProperty]
    private RxHistoryEntryViewModel? _selectedEntry;

    [ObservableProperty]
    private Bitmap? _previewImage;

    public RxHistoryPaneViewModel(IReceiveHistoryStore historyStore, ILocalizationService localization)
    {
        _historyStore = historyStore;
        _localization = localization;

        Id = "RxHistory";
        Title = localization.GetString("Panes.RxHistory.Title");
        localization.CultureChanged += () => Title = localization.GetString("Panes.RxHistory.Title");

        // Best-effort initial load -- a failure here (e.g. history store not reachable yet) leaves
        // the pane empty rather than blocking construction; RefreshCommand lets the user retry.
        _ = RefreshAsync();
    }

    public ObservableCollection<RxHistoryEntryViewModel> Entries { get; } = [];

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IReadOnlyList<ReceiveHistoryEntry> entries;
        try
        {
            entries = await _historyStore.QueryAsync(new ReceiveHistoryFilter());
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
