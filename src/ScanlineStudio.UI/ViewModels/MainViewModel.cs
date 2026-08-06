using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Application;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Root view-model for the fixed shell (Menu / header cards / Receive-Transmit-Gallery
/// TabControl, spec/09-ui.md) -- replaces the former Dock.Avalonia-based layout entirely; the 5 pane
/// view-models are now plain DI singletons (see ScanlineStudio.Host's Program.cs) rather than dockables
/// built by a factory.</summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly OptionsSettingsService _optionsSettingsService;
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty]
    private TxImageEditorPaneViewModel? _activeEditor;

    /// <summary>Operator's own callsign (spec/09-ui.md menu-row chip, mock2's own top-right
    /// "DL2QSK" green pill) -- real, loaded from <see cref="OptionsSettingsService"/> the same
    /// way <see cref="OptionsWindowViewModel"/> does; null/empty until the user sets it in
    /// Options, in which case the chip stays hidden (see <see cref="HasCallsign"/>).</summary>
    [ObservableProperty]
    private string? _callsign;

    public MainViewModel(
        WaterfallPaneViewModel waterfall,
        RxImagePaneViewModel rxImage,
        RxHistoryPaneViewModel rxHistory,
        TxControlsPaneViewModel txControls,
        IRadioSessionService radioSession,
        ISstvSessionService sstvSession,
        ILocalizationService localization,
        OptionsSettingsService optionsSettingsService,
        IServiceProvider services,
        ILogger<MainViewModel> logger,
        ILogger<RadioStatusViewModel> radioStatusLogger)
    {
        _services = services;
        _optionsSettingsService = optionsSettingsService;
        _logger = logger;

        Waterfall = waterfall;
        RxImage = rxImage;
        RxHistory = rxHistory;
        TxControls = txControls;

        // Replaces AppDockFactory.OpenTxImageEditor/CloseTxImageEditor's AddDockable/CloseDockable
        // pair -- a plain nullable property swapped via a ContentControl in the Transmit tab.
        txControls.EditorOpened += editor => ActiveEditor = editor;
        txControls.EditorClosed += () => ActiveEditor = null;

        RadioStatus = new RadioStatusViewModel(radioSession, sstvSession, localization, radioStatusLogger);

        // Plain reference hand-off, not an XAML ancestor-lookup binding -- see
        // TxControlsPaneViewModel.RadioStatus's own doc comment for why.
        txControls.RadioStatus = RadioStatus;

        _ = LoadCallsignAsync();
    }

    public WaterfallPaneViewModel Waterfall { get; }

    public RxImagePaneViewModel RxImage { get; }

    public RxHistoryPaneViewModel RxHistory { get; }

    public TxControlsPaneViewModel TxControls { get; }

    public RadioStatusViewModel RadioStatus { get; }

    /// <summary>What the menu-row chip actually displays -- "N0CALL" (the standard ham-radio
    /// placeholder callsign) until the user sets a real one in Options, matching mock2's own
    /// chip always being present rather than appearing/disappearing.</summary>
    public string CallsignDisplay => string.IsNullOrWhiteSpace(Callsign) ? "N0CALL" : Callsign;

    partial void OnCallsignChanged(string? value) => OnPropertyChanged(nameof(CallsignDisplay));

    /// <summary>Carries the freshly-DI-resolved <see cref="OptionsWindowViewModel"/> so
    /// `MainWindow`'s code-behind can construct/show the actual `Window` -- a view-model must never
    /// construct a View itself, and there is no `IDialogService` abstraction yet (first
    /// dialog/Window in the app; not worth building one for a single caller).</summary>
    public event Action<OptionsWindowViewModel>? OptionsRequested;

    /// <summary>File &gt; Exit -- same view-model-never-touches-a-Window reasoning as
    /// <see cref="OptionsRequested"/>; the code-behind owns the actual <c>Close()</c> call.</summary>
    public event Action? ExitRequested;

    [RelayCommand]
    private void OpenOptions()
    {
        Log.OpenOptionsInvoked(_logger);
        OptionsRequested?.Invoke(_services.GetRequiredService<OptionsWindowViewModel>());
    }

    [RelayCommand]
    private void Exit()
    {
        Log.ExitInvoked(_logger);
        ExitRequested?.Invoke();
    }

    private async Task LoadCallsignAsync()
    {
        try
        {
            var snapshot = await _optionsSettingsService.LoadAsync();
            Callsign = snapshot.Callsign;
        }
        catch (Exception ex)
        {
            Log.LoadCallsignFailed(_logger, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "OpenOptions command invoked")]
        public static partial void OpenOptionsInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Exit command invoked")]
        public static partial void ExitInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error, Message = "Loading operator callsign failed; menu-row chip stays hidden")]
        public static partial void LoadCallsignFailed(ILogger logger, Exception ex);
    }
}
