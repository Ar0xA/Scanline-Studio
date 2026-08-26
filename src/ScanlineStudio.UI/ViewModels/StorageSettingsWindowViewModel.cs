using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.UI.Services;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Stub survey Tier 2's "Configurations &gt; Storage" dialog -- exposes
/// <see cref="IReceiveHistoryStore.GetImagesDirectoryAsync"/>/<see cref="IReceiveHistoryStore.SetImagesDirectoryAsync"/>
/// (the RX images folder the Gallery tab's own Storage card already reads). DI-resolved via
/// <c>MainViewModel</c>'s own <c>IServiceProvider</c>, same precedent as
/// <see cref="OptionsWindowViewModel"/> -- unlike <see cref="QsoLinkWindowViewModel"/>, no existing
/// pane already holds the one dependency this needs (<see cref="IReceiveHistoryStore"/> lives on
/// <see cref="RxHistoryPaneViewModel"/>, a different pane than whatever menu opens this dialog).
///
/// <b>Never references <c>Core.Logbook.ReceiveHistorySettings</c> directly</b> (plan-review finding,
/// 2026-08-26, corrected an earlier plan that would have injected <c>ISettingsStore</c> straight into
/// this view-model): <see cref="IReceiveHistoryStore"/>'s own getter/setter pair is the UI-safe
/// surface `UiLayeringArchitectureTests` requires. "Naming" was dropped from the menu item's own
/// label (now just "Storage") -- no filename-pattern setting exists anywhere in the codebase to back
/// it; the pattern is hardcoded in <c>ReceiveHistoryRecorder</c>.
///
/// <b>Thread affinity</b>: same as <see cref="QsoLinkWindowViewModel"/>'s own doc comment -- no
/// <c>ConfigureAwait(false)</c> anywhere, every command runs on and resumes on the UI thread.</summary>
public sealed partial class StorageSettingsWindowViewModel : ViewModelBase
{
    private readonly IReceiveHistoryStore _historyStore;
    private readonly ILocalizationService _localization;
    private readonly IFilePickerService _filePicker;
    private readonly ILogger<StorageSettingsWindowViewModel> _logger;

    public StorageSettingsWindowViewModel(IReceiveHistoryStore historyStore, ILocalizationService localization, IFilePickerService filePicker, ILogger<StorageSettingsWindowViewModel> logger)
    {
        _historyStore = historyStore;
        _localization = localization;
        _filePicker = filePicker;
        _logger = logger;
        _ = LoadSafeAsync();
    }

    /// <summary>Pre-filled with the REAL effective path (plan-review finding) -- resolved through
    /// <see cref="IReceiveHistoryStore.GetImagesDirectoryAsync"/>'s own default-fallback, not left
    /// blank just because the underlying setting happens to be unset today.</summary>
    [ObservableProperty]
    private string _imagesDirectory = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Same view-model-never-touches-a-Window pattern as <see cref="OptionsWindowViewModel.RequestClose"/> --
    /// the View's code-behind subscribes <c>vm.RequestClose += Close;</c>.</summary>
    public event Action? RequestClose;

    private async Task LoadSafeAsync()
    {
        try
        {
            ImagesDirectory = await _historyStore.GetImagesDirectoryAsync();
        }
        catch (Exception ex)
        {
            Log.LoadFailed(_logger, ex);
        }
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var picked = await _filePicker.PickFolderAsync(ImagesDirectory);
        if (picked is not null)
        {
            ImagesDirectory = picked;
        }
    }

    /// <summary>Validates by actually creating the directory before persisting -- see
    /// <see cref="IReceiveHistoryStore.SetImagesDirectoryAsync"/>'s own doc comment for why. An
    /// empty/whitespace field resets to the default, matching that method's own null-means-default
    /// convention.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        Log.SaveInvoked(_logger, ImagesDirectory);
        try
        {
            ErrorMessage = null;
            await _historyStore.SetImagesDirectoryAsync(ImagesDirectory);
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            Log.SaveFailed(_logger, ex);
            ErrorMessage = _localization.GetString("StorageSettings.Error.SaveFailed", ex.Message);
        }
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading the configured RX images directory failed")]
        public static partial void LoadFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Storage settings save invoked: {Directory}")]
        public static partial void SaveInvoked(ILogger logger, string directory);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Storage settings save failed")]
        public static partial void SaveFailed(ILogger logger, Exception ex);
    }
}
