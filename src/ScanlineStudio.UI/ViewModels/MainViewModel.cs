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
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty]
    private TxImageEditorPaneViewModel? _activeEditor;

    public MainViewModel(
        WaterfallPaneViewModel waterfall,
        RxImagePaneViewModel rxImage,
        RxHistoryPaneViewModel rxHistory,
        TxControlsPaneViewModel txControls,
        IRadioSessionService radioSession,
        ISstvSessionService sstvSession,
        ILocalizationService localization,
        IServiceProvider services,
        ILogger<MainViewModel> logger,
        ILogger<RadioStatusViewModel> radioStatusLogger)
    {
        _services = services;
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
    }

    public WaterfallPaneViewModel Waterfall { get; }

    public RxImagePaneViewModel RxImage { get; }

    public RxHistoryPaneViewModel RxHistory { get; }

    public TxControlsPaneViewModel TxControls { get; }

    public RadioStatusViewModel RadioStatus { get; }

    /// <summary>Carries the freshly-DI-resolved <see cref="OptionsWindowViewModel"/> so
    /// `MainWindow`'s code-behind can construct/show the actual `Window` -- a view-model must never
    /// construct a View itself, and there is no `IDialogService` abstraction yet (first
    /// dialog/Window in the app; not worth building one for a single caller).</summary>
    public event Action<OptionsWindowViewModel>? OptionsRequested;

    [RelayCommand]
    private void OpenOptions()
    {
        Log.OpenOptionsInvoked(_logger);
        OptionsRequested?.Invoke(_services.GetRequiredService<OptionsWindowViewModel>());
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "OpenOptions command invoked")]
        public static partial void OpenOptionsInvoked(ILogger logger);
    }
}
