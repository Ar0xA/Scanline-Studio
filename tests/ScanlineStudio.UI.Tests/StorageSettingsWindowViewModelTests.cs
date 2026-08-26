using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

public sealed class StorageSettingsWindowViewModelTests
{
    private static StorageSettingsWindowViewModel CreateViewModel(FakeReceiveHistoryStore? historyStore = null, FakeFilePickerService? filePicker = null)
        => new(historyStore ?? new FakeReceiveHistoryStore(), new FakeLocalizationService(), filePicker ?? new FakeFilePickerService(), NullLogger<StorageSettingsWindowViewModel>.Instance);

    [AvaloniaFact]
    public void Constructor_LoadsTheCurrentImagesDirectory()
    {
        var historyStore = new FakeReceiveHistoryStore { ImagesDirectory = "/configured/rx/history" };

        var vm = CreateViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("/configured/rx/history", vm.ImagesDirectory);
    }

    [AvaloniaFact]
    public async Task SaveCommand_Success_PersistsAndRaisesRequestClose()
    {
        var historyStore = new FakeReceiveHistoryStore();
        var vm = CreateViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();
        vm.ImagesDirectory = "/new/rx/history";

        var closed = false;
        vm.RequestClose += () => closed = true;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["/new/rx/history"], historyStore.SetImagesDirectoryCalls);
        Assert.True(closed);
        Assert.Null(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task SaveCommand_StoreThrows_SetsErrorMessageAndDoesNotClose()
    {
        // Plan-review finding: SetImagesDirectoryAsync validates by actually creating the directory
        // and can throw (a typo/permission problem) -- this must surface to the user, not close the
        // dialog as if it succeeded.
        var historyStore = new FakeReceiveHistoryStore { ThrowOnSetImagesDirectory = true };
        var vm = CreateViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();
        vm.ImagesDirectory = "/bad/path";

        var closed = false;
        vm.RequestClose += () => closed = true;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Empty(historyStore.SetImagesDirectoryCalls);
    }

    [AvaloniaFact]
    public async Task BrowseCommand_UserPicksAFolder_UpdatesImagesDirectory()
    {
        var filePicker = new FakeFilePickerService { FolderPathToReturn = "/picked/folder" };
        var vm = CreateViewModel(filePicker: filePicker);
        Dispatcher.UIThread.RunJobs();
        vm.ImagesDirectory = "/original";

        await vm.BrowseCommand.ExecuteAsync(null);

        Assert.Equal("/picked/folder", vm.ImagesDirectory);
        // The current value is passed as the suggested start location, not left null/default.
        Assert.Equal("/original", filePicker.LastSuggestedStartDirectory);
    }

    [AvaloniaFact]
    public async Task BrowseCommand_UserCancels_LeavesImagesDirectoryUnchanged()
    {
        var filePicker = new FakeFilePickerService { FolderPathToReturn = null };
        var vm = CreateViewModel(filePicker: filePicker);
        Dispatcher.UIThread.RunJobs();
        vm.ImagesDirectory = "/original";

        await vm.BrowseCommand.ExecuteAsync(null);

        Assert.Equal("/original", vm.ImagesDirectory);
    }

    [AvaloniaFact]
    public void CloseCommand_RaisesRequestCloseWithoutSaving()
    {
        var historyStore = new FakeReceiveHistoryStore();
        var vm = CreateViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();
        vm.ImagesDirectory = "/edited-but-not-saved";

        var closed = false;
        vm.RequestClose += () => closed = true;

        vm.CloseCommand.Execute(null);

        Assert.True(closed);
        Assert.Empty(historyStore.SetImagesDirectoryCalls);
    }
}
