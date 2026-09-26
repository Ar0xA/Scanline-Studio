using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>ui_transition_plan.md step 3 (T1-5 + T2-6). Covers navigation/zoom/fit-mode VM state
/// and the Copy/OpenFileLocation/Close commands -- per project memory, pixel-level rendering
/// (whether the Image control actually shows the right bytes) is a live-window concern, not a
/// headless-test one; these tests stop at "the right VM state and the right service calls."
/// </summary>
public sealed class ImageViewerWindowViewModelTests
{
    private static ReceiveHistoryEntry Entry(string id, string filePath = "/tmp/frame.png") =>
        new(id, DateTimeOffset.UtcNow, "sc1", filePath, null, ReceiveDecodeState.Completed);

    private static RxHistoryEntryViewModel EntryVm(string id, string filePath = "/tmp/frame.png") =>
        new(Entry(id, filePath), thumbnail: null);

    private static ImageViewerWindowViewModel Create(
        IReadOnlyList<RxHistoryEntryViewModel> entries,
        int startIndex,
        FakeReceiveHistoryStore historyStore,
        FakeUrlLauncher? urlLauncher = null,
        FakeClipboardImageService? clipboardImageService = null) =>
        new(
            entries,
            startIndex,
            historyStore,
            urlLauncher ?? new FakeUrlLauncher(),
            clipboardImageService ?? new FakeClipboardImageService(),
            new FakeLocalizationService(),
            NullLogger<ImageViewerWindowViewModel>.Instance);

    private static FakeReceiveHistoryStore StoreWithThumbnail() =>
        new() { ThumbnailToReturn = new ArrayImageSource(2, 2, [new Rgb24(1, 2, 3), new Rgb24(1, 2, 3), new Rgb24(1, 2, 3), new Rgb24(1, 2, 3)]) };

    [AvaloniaFact]
    public async Task Constructor_LoadsTheEntryAtStartIndex()
    {
        var historyStore = StoreWithThumbnail();
        var entries = new[] { EntryVm("a"), EntryVm("b"), EntryVm("c") };
        var vm = Create(entries, startIndex: 1, historyStore);

        await Task.Yield();

        Assert.Equal(entries[1], vm.Current);
        Assert.Equal("b", Assert.Single(historyStore.ThumbnailLoadCalls).EntryId);
    }

    [AvaloniaFact]
    public void Constructor_ClampsAnOutOfRangeStartIndex()
    {
        var historyStore = StoreWithThumbnail();
        var entries = new[] { EntryVm("a"), EntryVm("b") };

        var tooHigh = Create(entries, startIndex: 99, historyStore);
        Assert.Equal(1, tooHigh.CurrentIndex);

        var negative = Create(entries, startIndex: -5, historyStore);
        Assert.Equal(0, negative.CurrentIndex);
    }

    [AvaloniaFact]
    public void Constructor_EmptyEntries_DoesNotCrash_AndCurrentIsNull()
    {
        var vm = Create([], startIndex: 0, StoreWithThumbnail());

        Assert.Null(vm.Current);
        Assert.Equal(string.Empty, vm.PositionText);
        Assert.False(vm.PreviousCommand.CanExecute(null));
        Assert.False(vm.NextCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task PreviousNext_NavigateAndReloadTheImage_GatedAtBothEnds()
    {
        var historyStore = StoreWithThumbnail();
        var entries = new[] { EntryVm("a"), EntryVm("b"), EntryVm("c") };
        var vm = Create(entries, startIndex: 0, historyStore);
        await Task.Yield();

        Assert.False(vm.PreviousCommand.CanExecute(null));
        Assert.True(vm.NextCommand.CanExecute(null));

        vm.NextCommand.Execute(null);
        await Task.Yield();
        Assert.Equal(1, vm.CurrentIndex);
        Assert.Equal(entries[1], vm.Current);
        Assert.True(vm.PreviousCommand.CanExecute(null));
        Assert.True(vm.NextCommand.CanExecute(null));

        vm.NextCommand.Execute(null);
        await Task.Yield();
        Assert.Equal(2, vm.CurrentIndex);
        Assert.False(vm.NextCommand.CanExecute(null));

        vm.PreviousCommand.Execute(null);
        await Task.Yield();
        Assert.Equal(1, vm.CurrentIndex);

        Assert.Equal(["a", "b", "c", "b"], historyStore.ThumbnailLoadCalls.Select(c => c.EntryId));
    }

    /// <summary>A fast Next/Next before the first load resolves must not let the FIRST (now
    /// stale) load overwrite the state a later navigation already produced -- see
    /// ImageViewerWindowViewModel.LoadCurrentAsync's own doc comment on _loadGeneration.</summary>
    [AvaloniaFact]
    public async Task RapidNavigation_StaleLoadDoesNotOverwriteTheNewerOne()
    {
        var historyStore = new FakeReceiveHistoryStore();
        var entries = new[] { EntryVm("a"), EntryVm("b"), EntryVm("c") };
        var gateA = new TaskCompletionSource<IImageSource>();
        var imageB = new ArrayImageSource(1, 1, [new Rgb24(9, 9, 9)]);
        historyStore.ThumbnailLoadGates["a"] = gateA;
        historyStore.ThumbnailToReturn = imageB;

        var vm = Create(entries, startIndex: 0, historyStore);
        // The constructor's own load for "a" is now blocked on gateA.

        vm.NextCommand.Execute(null);
        await Task.Yield(); // "b" loads and resolves immediately (not gated).

        Assert.NotNull(vm.FullImage);
        var afterB = vm.FullImage;

        gateA.SetResult(new ArrayImageSource(1, 1, [new Rgb24(1, 1, 1)]));
        await Task.Yield();
        await Task.Yield();

        Assert.Same(afterB, vm.FullImage);
        Assert.Equal(1, vm.CurrentIndex);
    }

    [AvaloniaFact]
    public async Task LoadFailure_SetsErrorMessage_AndClearsFullImage_WithoutThrowing()
    {
        var historyStore = new FakeReceiveHistoryStore();
        var entries = new[] { EntryVm("a") };
        historyStore.ThumbnailLoadGates["a"] = new TaskCompletionSource<IImageSource>();
        historyStore.ThumbnailLoadGates["a"].SetException(new IOException("file moved"));

        var vm = Create(entries, startIndex: 0, historyStore);
        await Task.Yield();
        await Task.Yield();

        Assert.Null(vm.FullImage);
        Assert.NotNull(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public void FitAndActualSize_ToggleModeAndResetZoom()
    {
        var vm = Create([EntryVm("a")], 0, StoreWithThumbnail());

        vm.ZoomInCommand.Execute(null);
        Assert.False(vm.IsFitToWindow);
        Assert.True(vm.ZoomFactor > 1.0);

        vm.FitToWindowCommand.Execute(null);
        Assert.True(vm.IsFitToWindow);
        Assert.Equal(1.0, vm.ZoomFactor);

        vm.ZoomBy(2.0);
        Assert.False(vm.IsFitToWindow);

        vm.ActualSizeCommand.Execute(null);
        Assert.False(vm.IsFitToWindow);
        Assert.Equal(1.0, vm.ZoomFactor);
    }

    [AvaloniaFact]
    public void ZoomBy_ClampsToMinAndMax()
    {
        var vm = Create([EntryVm("a")], 0, StoreWithThumbnail());

        for (var i = 0; i < 40; i++)
        {
            vm.ZoomBy(0.5);
        }

        Assert.True(vm.ZoomFactor is > 0 and <= 1.0);

        for (var i = 0; i < 40; i++)
        {
            vm.ZoomBy(2.0);
        }

        Assert.True(vm.ZoomFactor <= 8.0);
    }

    [AvaloniaFact]
    public async Task CopyCommand_PassesTheLoadedBitmapToTheClipboardService()
    {
        var historyStore = StoreWithThumbnail();
        var clipboard = new FakeClipboardImageService();
        var vm = Create([EntryVm("a")], 0, historyStore, clipboardImageService: clipboard);
        await Task.Yield();

        await vm.CopyCommand.ExecuteAsync(null);

        var copied = Assert.Single(clipboard.CopiedImages);
        Assert.Same(vm.FullImage, copied);
        Assert.Null(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task CopyCommand_SetsErrorMessage_WhenTheClipboardServiceFails()
    {
        var historyStore = StoreWithThumbnail();
        var clipboard = new FakeClipboardImageService { ResultToReturn = false };
        var vm = Create([EntryVm("a")], 0, historyStore, clipboardImageService: clipboard);
        await Task.Yield();

        await vm.CopyCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public void CopyCommand_WithNoImageLoadedYet_DoesNotCallTheClipboardService()
    {
        var historyStore = new FakeReceiveHistoryStore();
        historyStore.ThumbnailLoadGates["a"] = new TaskCompletionSource<IImageSource>(); // never resolves
        var clipboard = new FakeClipboardImageService();
        var vm = Create([EntryVm("a")], 0, historyStore, clipboardImageService: clipboard);

        vm.CopyCommand.Execute(null);

        Assert.Empty(clipboard.CopiedImages);
    }

    [AvaloniaFact]
    public void OpenFileLocationCommand_OpensTheContainingDirectory_NotTheFileItself()
    {
        var urlLauncher = new FakeUrlLauncher();
        var vm = Create([EntryVm("a", "/tmp/history/frame.png")], 0, StoreWithThumbnail(), urlLauncher: urlLauncher);

        vm.OpenFileLocationCommand.Execute(null);

        // The command opens the file's CONTAINING DIRECTORY, which Path.GetDirectoryName builds
        // with platform separators -- on Windows that is \tmp\history, not /tmp/history.
        Assert.Equal(
            Path.GetDirectoryName("/tmp/history/frame.png"),
            Assert.Single(urlLauncher.OpenedUrls));
    }

    [AvaloniaFact]
    public void OpenExternallyCommand_OpensTheFileItself_NotTheContainingDirectory()
    {
        // ui_transition_plan.md step 16 -- the whole point of this command, distinct from
        // OpenFileLocation above: the FULL file path goes to IUrlLauncher.Open, not the directory.
        var urlLauncher = new FakeUrlLauncher();
        var vm = Create([EntryVm("a", "/tmp/history/frame.png")], 0, StoreWithThumbnail(), urlLauncher: urlLauncher);

        vm.OpenExternallyCommand.Execute(null);

        Assert.Equal("/tmp/history/frame.png", Assert.Single(urlLauncher.OpenedUrls));
    }

    [AvaloniaFact]
    public void OpenExternallyCommand_NoEntries_DoesNotThrow_DoesNotOpenAnything()
    {
        var urlLauncher = new FakeUrlLauncher();
        var vm = Create([], 0, StoreWithThumbnail(), urlLauncher: urlLauncher);

        vm.OpenExternallyCommand.Execute(null);

        Assert.Empty(urlLauncher.OpenedUrls);
    }

    [AvaloniaFact]
    public void CloseCommand_RaisesCloseRequested()
    {
        var vm = Create([EntryVm("a")], 0, StoreWithThumbnail());
        var raised = false;
        vm.CloseRequested += () => raised = true;

        vm.CloseCommand.Execute(null);

        Assert.True(raised);
    }

    [AvaloniaFact]
    public async Task Next_DisposesThePreviousFullImage_DeferredViaDispatcherPost()
    {
        // T0-11 (production_audit.md): OnFullImageChanged disposes the OLD bitmap, but only once
        // posted to the dispatcher at Background priority -- RunJobs() is required to observe it.
        var historyStore = StoreWithThumbnail();
        var entries = new[] { EntryVm("a"), EntryVm("b") };
        var vm = Create(entries, startIndex: 0, historyStore);
        await Task.Yield();
        var first = vm.FullImage;
        Assert.NotNull(first);

        vm.NextCommand.Execute(null);
        await Task.Yield();

        Assert.False(IsDisposed(first!), "must not be disposed before the deferred post runs");
        Dispatcher.UIThread.RunJobs();
        Assert.True(IsDisposed(first!));
    }

    [AvaloniaFact]
    public async Task CopyAsync_ThenNext_DoesNotDisposeTheBitmapStillBeingCopied()
    {
        // T0-11: the _copyInFlight guard -- a Next navigation while CopyAsync's own await is still
        // pending must not dispose the bitmap CopyAsync is actively reading.
        var historyStore = StoreWithThumbnail();
        var clipboard = new FakeClipboardImageService();
        var gate = new TaskCompletionSource();
        clipboard.Gate = gate.Task;
        var entries = new[] { EntryVm("a"), EntryVm("b") };
        var vm = Create(entries, startIndex: 0, historyStore, clipboardImageService: clipboard);
        await Task.Yield();
        var beingCopied = vm.FullImage;
        Assert.NotNull(beingCopied);

        var copyTask = vm.CopyCommand.ExecuteAsync(null);

        vm.NextCommand.Execute(null);
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        Assert.False(IsDisposed(beingCopied!), "must not dispose a bitmap CopyAsync is still awaiting on");

        gate.SetResult();
        await copyTask;
        Dispatcher.UIThread.RunJobs();

        // A LATER reassignment, with no copy in flight, must still dispose normally.
        var beforePrevious = vm.FullImage;
        Assert.NotNull(beforePrevious);
        vm.PreviousCommand.Execute(null);
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        Assert.True(IsDisposed(beforePrevious!), "a later reassignment with no copy in flight must dispose the superseded bitmap");
    }

    // Avalonia's WriteableBitmap has no public IsDisposed -- same technique
    // WriteableBitmapPoolTests uses: a disposed instance throws ObjectDisposedException
    // from any real operation, here .Lock().
    private static bool IsDisposed(Avalonia.Media.Imaging.Bitmap bitmap)
    {
        if (bitmap is not Avalonia.Media.Imaging.WriteableBitmap writeable)
        {
            return false;
        }

        try
        {
            using (writeable.Lock())
            {
            }

            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }
}
