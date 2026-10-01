using System.Collections.Specialized;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>Gallery display cap, lazy thumbnail loading, and the thumbnail cache's
/// reuse/conflict/eviction rules in <see cref="RxHistoryPaneViewModel"/>.</summary>
public sealed class RxHistoryThumbnailCacheTests
{
    private const int GalleryThumbnailDimension = 240;

    private static readonly IImageSource OnePixel = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]);

    // Anchored at noon today (not "now") so every generated row stays inside ShowTodayOnly's window
    // regardless of what time the test runs.
    private static readonly DateTimeOffset Noon = new(DateTime.Today.AddHours(12));

    private static List<ReceiveHistoryEntry> MakeEntries(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new ReceiveHistoryEntry($"e{i:D4}", Noon.AddSeconds(-i), "robot36", $"/tmp/e{i:D4}.png", null, ReceiveDecodeState.Completed))
            .ToList();

    private static int GalleryLoads(FakeReceiveHistoryStore store, string id) =>
        store.ThumbnailLoadCalls.Count(c => c.EntryId == id && c.MaxDimension == GalleryThumbnailDimension);

    private static bool IsDisposed(Bitmap bitmap)
    {
        try
        {
            using (((WriteableBitmap)bitmap).Lock())
            {
            }

            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    [AvaloniaFact]
    public async Task TwoRefreshes_UnchangedRows_LoadEachDisplayedThumbnailOnce_AndNeverLoadUndisplayedRows()
    {
        var store = new FakeReceiveHistoryStore { EntriesToReturn = MakeEntries(350), ThumbnailToReturn = OnePixel };
        var vm = PaneViewModelTests.CreateRxHistoryPaneViewModel(store);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var firstBitmap = vm.FilteredEntries[0].Thumbnail;

        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(300, vm.FilteredEntries.Count);
        for (var i = 0; i < 300; i++)
        {
            Assert.Equal(1, GalleryLoads(store, $"e{i:D4}"));
        }

        for (var i = 300; i < 350; i++)
        {
            Assert.Equal(0, GalleryLoads(store, $"e{i:D4}"));
        }

        Assert.NotNull(firstBitmap);
        Assert.Same(firstBitmap, vm.FilteredEntries[0].Thumbnail);
        Assert.All(vm.FilteredEntries, e => Assert.NotNull(e.Thumbnail));
    }

    [AvaloniaFact]
    public async Task NewFrameRecorded_LoadsOnlyTheNewEntrysThumbnail()
    {
        var entries = MakeEntries(5);
        var store = new FakeReceiveHistoryStore { EntriesToReturn = entries, ThumbnailToReturn = OnePixel };
        var vm = PaneViewModelTests.CreateRxHistoryPaneViewModel(store);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var loadsBefore = store.ThumbnailLoadCalls.Count(c => c.MaxDimension == GalleryThumbnailDimension);

        var newEntry = new ReceiveHistoryEntry("new", Noon.AddSeconds(1), "robot36", "/tmp/new.png", null, ReceiveDecodeState.Completed);
        store.EntriesToReturn = [newEntry, .. entries];
        store.RaiseRecorded(newEntry);
        Dispatcher.UIThread.RunJobs();

        var newLoads = store.ThumbnailLoadCalls.Where(c => c.MaxDimension == GalleryThumbnailDimension).Skip(loadsBefore).ToList();
        Assert.Equal([("new", GalleryThumbnailDimension)], newLoads);
        Assert.Equal(6, vm.FilteredEntries.Count);
        Assert.All(vm.FilteredEntries, e => Assert.NotNull(e.Thumbnail));
    }

    [AvaloniaFact]
    public async Task SearchAndFlaggedFilter_FindAnEntryOlderThanTheDisplayCap()
    {
        var entries = MakeEntries(350);
        entries[340] = entries[340] with { IsFlagged = true, Note = "rare needle" };
        var store = new FakeReceiveHistoryStore { EntriesToReturn = entries, ThumbnailToReturn = OnePixel };
        var vm = PaneViewModelTests.CreateRxHistoryPaneViewModel(store);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(vm.FilteredEntries, e => e.Entry.Id == "e0340");

        vm.FilterFlaggedOnly = true;
        Assert.Equal("e0340", Assert.Single(vm.FilteredEntries).Entry.Id);

        vm.FilterFlaggedOnly = false;
        vm.SearchText = "needle";
        await PaneViewModelTests.SettleGallerySearchAsync(vm);
        Assert.Equal("e0340", Assert.Single(vm.FilteredEntries).Entry.Id);
        Assert.False(vm.CanShowOlder);
    }

    [AvaloniaFact]
    public async Task HeaderCount_CountsEveryRow_NotTheDisplayCap()
    {
        var localization = new FakeLocalizationService();
        var store = new FakeReceiveHistoryStore { EntriesToReturn = MakeEntries(350), ThumbnailToReturn = OnePixel };
        var vm = PaneViewModelTests.CreateRxHistoryPaneViewModel(store, localization: localization);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(300, vm.FilteredEntries.Count);
        Assert.Equal(350, vm.Entries.Count);
        var lastCount = localization.Calls.Last(c => c.Key == "Panes.RxHistory.EntryCountFormat");
        Assert.Equal(350, lastCount.Args[0]);
    }

    [AvaloniaFact]
    public async Task ShowOlder_DisplaysMore_AndShowTodayOnlyChangeResetsTheLimit()
    {
        var store = new FakeReceiveHistoryStore { EntriesToReturn = MakeEntries(350), ThumbnailToReturn = OnePixel };
        var vm = PaneViewModelTests.CreateRxHistoryPaneViewModel(store);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.CanShowOlder);

        vm.ShowOlderCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(350, vm.FilteredEntries.Count);
        Assert.False(vm.CanShowOlder);
        Assert.Equal(1, GalleryLoads(store, "e0349"));

        vm.ShowTodayOnly = !vm.ShowTodayOnly;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(300, vm.FilteredEntries.Count);
        Assert.True(vm.CanShowOlder);
    }

    [AvaloniaFact]
    public async Task Eviction_OverBudget_NullsEvictedItems_DisposesAfterDrain_AndNeverTouchesDisplayedItems()
    {
        var store = new FakeReceiveHistoryStore { EntriesToReturn = MakeEntries(700), ThumbnailToReturn = OnePixel };
        var vm = PaneViewModelTests.CreateRxHistoryPaneViewModel(store);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.ShowOlderCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(600, vm.FilteredEntries.Count);
        var olderBitmaps = vm.Entries.Skip(300).Take(300).Select(e => e.Thumbnail!).ToList();
        Assert.All(olderBitmaps, Assert.NotNull);

        // Limit resets to 300 -> budget 400 -> 200 of the 300 no-longer-displayed thumbnails must go.
        vm.ShowTodayOnly = !vm.ShowTodayOnly;

        var older = vm.Entries.Skip(300).Take(300).ToList();
        var nulledIndexes = Enumerable.Range(0, 300).Where(i => older[i].Thumbnail is null).ToList();
        Assert.Equal(200, nulledIndexes.Count);
        Assert.All(nulledIndexes, i => Assert.False(IsDisposed(olderBitmaps[i]), "dispose must wait for the Background-priority post"));

        Dispatcher.UIThread.RunJobs();

        Assert.All(nulledIndexes, i => Assert.True(IsDisposed(olderBitmaps[i])));
        Assert.Equal(300, vm.FilteredEntries.Count);
        Assert.All(vm.FilteredEntries, e => Assert.False(IsDisposed(e.Thumbnail!)));
        Assert.All(vm.Entries.Where(e => e.Thumbnail is not null), e => Assert.False(IsDisposed(e.Thumbnail!)));
    }

    [AvaloniaFact]
    public async Task OverlappingRefreshes_SameKeyLoadedTwice_AdoptsTheCachedBitmap_NoSecondInstanceReachesTheItem()
    {
        var gate = new TaskCompletionSource<IImageSource>();
        var store = new FakeReceiveHistoryStore { EntriesToReturn = MakeEntries(1), ThumbnailToReturn = OnePixel };
        store.ThumbnailLoadGates["e0000"] = gate;
        // The constructor's own refresh is the first overlapping one.
        var vm = PaneViewModelTests.CreateRxHistoryPaneViewModel(store);
        Dispatcher.UIThread.RunJobs();
        var firstInstance = vm.Entries[0];

        // A changed row gets a new instance, so the first load's target is gone by the time it lands.
        store.EntriesToReturn = [store.EntriesToReturn[0] with { Note = "edited" }];
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var current = vm.Entries[0];
        Assert.NotSame(firstInstance, current);
        Assert.Equal(2, GalleryLoads(store, "e0000"));

        var assigned = new List<Bitmap?>();
        current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RxHistoryEntryViewModel.Thumbnail))
            {
                assigned.Add(current.Thumbnail);
            }
        };
        gate.SetResult(OnePixel);
        Dispatcher.UIThread.RunJobs();

        var bitmap = Assert.Single(assigned);
        Assert.NotNull(bitmap);
        Assert.Same(bitmap, current.Thumbnail);
        Assert.False(IsDisposed(bitmap));

        // The cache holds that same instance: a further refresh reuses it without reloading.
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(bitmap, vm.Entries[0].Thumbnail);
        Assert.Equal(2, GalleryLoads(store, "e0000"));
    }

    [AvaloniaFact]
    public async Task PerFrameRefresh_OneNewRow_RaisesOnlyOChangedFilteredEntriesEvents()
    {
        var entries = MakeEntries(1000);
        var store = new FakeReceiveHistoryStore { EntriesToReturn = entries, ThumbnailToReturn = OnePixel };
        var vm = PaneViewModelTests.CreateRxHistoryPaneViewModel(store);
        Dispatcher.UIThread.RunJobs();
        var before = vm.FilteredEntries.ToList();

        var events = new List<NotifyCollectionChangedAction>();
        NotifyCollectionChangedEventHandler handler = (_, e) => events.Add(e.Action);
        vm.FilteredEntries.CollectionChanged += handler;
        var newEntry = new ReceiveHistoryEntry("new", Noon.AddSeconds(1), "robot36", "/tmp/new.png", null, ReceiveDecodeState.Completed);
        store.EntriesToReturn = [newEntry, .. entries];
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.FilteredEntries.CollectionChanged -= handler;

        // One insert at the top, one removal at the 300 cap boundary.
        Assert.True(events.Count <= 3, $"expected <= 3 events, got {events.Count}");
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, events);
        Assert.Equal("new", vm.FilteredEntries[0].Entry.Id);
        Assert.Equal(300, vm.FilteredEntries.Count);
        Assert.Equal(before.Take(299), vm.FilteredEntries.Skip(1), ReferenceEqualityComparer.Instance);
    }

    [AvaloniaFact]
    public async Task Refresh_UnchangedRowKeepsItsInstance_ChangedRowGetsANewOneWithTheCachedBitmap()
    {
        var entries = MakeEntries(3);
        var store = new FakeReceiveHistoryStore { EntriesToReturn = entries, ThumbnailToReturn = OnePixel };
        var vm = PaneViewModelTests.CreateRxHistoryPaneViewModel(store);
        Dispatcher.UIThread.RunJobs();
        var unchanged = vm.Entries[0];
        var changed = vm.Entries[1];
        var changedBitmap = changed.Thumbnail;
        Assert.NotNull(changedBitmap);

        store.EntriesToReturn = [entries[0], entries[1] with { IsFlagged = true }, entries[2]];
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(unchanged, vm.Entries[0]);
        Assert.Same(unchanged, vm.FilteredEntries[0]);
        Assert.NotSame(changed, vm.Entries[1]);
        Assert.True(vm.Entries[1].Entry.IsFlagged);
        Assert.Same(vm.Entries[1], vm.FilteredEntries[1]);
        Assert.Same(changedBitmap, vm.Entries[1].Thumbnail);
        Assert.Equal(1, GalleryLoads(store, "e0001"));
    }

    [AvaloniaFact]
    public async Task FlaggedFilter_UnflaggingViaUpdateEntryInPlace_DropsTheEntryFromFilteredEntries()
    {
        var entries = MakeEntries(3);
        entries[1] = entries[1] with { IsFlagged = true };
        var store = new FakeReceiveHistoryStore { EntriesToReturn = entries, ThumbnailToReturn = OnePixel };
        var vm = PaneViewModelTests.CreateRxHistoryPaneViewModel(store);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.FilterFlaggedOnly = true;
        vm.SelectedEntry = Assert.Single(vm.FilteredEntries);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntryIsFlagged = false;
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.Entries.Single(e => e.Entry.Id == "e0001").Entry.IsFlagged);
        Assert.Empty(vm.FilteredEntries);
    }

    [AvaloniaFact]
    public async Task LateLazyLoad_AfterUpdateEntryInPlaceReplacedTheItem_LandsOnTheCurrentInstanceByKey()
    {
        var entries = MakeEntries(2);
        entries[1] = entries[1] with { ModeId = "scottie-s1" };
        var gate = new TaskCompletionSource<IImageSource>();
        var stationIdAttacher = new FakeRxStationIdAttacher();
        var store = new FakeReceiveHistoryStore { EntriesToReturn = entries, ThumbnailToReturn = OnePixel };
        store.ThumbnailLoadGates["e0000"] = gate;
        var vm = PaneViewModelTests.CreateRxHistoryPaneViewModel(store, stationIdAttacher: stationIdAttacher);
        Dispatcher.UIThread.RunJobs();
        var original = vm.Entries[0];

        // Newer publishes that no longer display e0000, so only the first loader ever loads it.
        vm.SearchText = "scottie";
        await PaneViewModelTests.SettleGallerySearchAsync(vm);
        stationIdAttacher.RaiseStationIdAttached("e0000", "N0CALL");
        Dispatcher.UIThread.RunJobs();
        var replacement = vm.Entries[0];
        Assert.NotSame(original, replacement);
        Assert.Equal("N0CALL", replacement.Entry.DecodedCallsign);
        Assert.Equal(1, GalleryLoads(store, "e0000"));

        gate.SetResult(OnePixel);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(replacement.Thumbnail);
        Assert.False(IsDisposed(replacement.Thumbnail!));

        // Re-displaying it reuses that cached bitmap rather than decoding again.
        vm.SearchText = null;
        await PaneViewModelTests.SettleGallerySearchAsync(vm);
        Assert.Same(replacement.Thumbnail, vm.FilteredEntries.Single(e => e.Entry.Id == "e0000").Thumbnail);
        Assert.Equal(1, GalleryLoads(store, "e0000"));
    }

    [AvaloniaFact]
    public async Task SearchTyping_IsDebounced_OnePublishPerPause()
    {
        var entries = MakeEntries(10);
        entries[7] = entries[7] with { Note = "needle" };
        var store = new FakeReceiveHistoryStore { EntriesToReturn = entries, ThumbnailToReturn = OnePixel };
        var vm = PaneViewModelTests.CreateRxHistoryPaneViewModel(store);
        Dispatcher.UIThread.RunJobs();

        var events = 0;
        vm.FilteredEntries.CollectionChanged += (_, _) => events++;
        vm.SearchText = "n";
        vm.SearchText = "ne";
        vm.SearchText = "needle";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(10, vm.FilteredEntries.Count);
        Assert.Equal(0, events);

        await PaneViewModelTests.SettleGallerySearchAsync(vm);

        // One publish: the nine non-matching rows removed, nothing else.
        Assert.Equal(9, events);
        Assert.Equal("e0007", Assert.Single(vm.FilteredEntries).Entry.Id);
    }
}
