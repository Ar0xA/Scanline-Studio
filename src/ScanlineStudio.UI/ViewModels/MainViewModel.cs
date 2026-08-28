using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Services;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Root view-model for the fixed shell (Menu / header cards / Receive-Transmit-Gallery
/// TabControl, spec/09-ui.md) -- replaces the former Dock.Avalonia-based layout entirely; the 5 pane
/// view-models are now plain DI singletons (see ScanlineStudio.Host's Program.cs) rather than dockables
/// built by a factory.</summary>
public partial class MainViewModel : ViewModelBase
{
    /// <summary>Index of the Logbook tab in `MainWindow.axaml`'s main `TabControl` (source order:
    /// Receive=0, Transmit=1, Gallery=2, Logbook=3) -- named so a future tab reorder is at least
    /// grep-able, and guarded by a test asserting the 4th `TabItem`'s header key is
    /// `MainWindow.Tabs.Logbook` (a silent reorder would otherwise switch to the wrong tab with no
    /// compile-time or obvious runtime signal).</summary>
    public const int LogbookTabIndex = 3;

    /// <summary>Same reasoning/guard-test convention as <see cref="LogbookTabIndex"/> (source order
    /// above) -- drives <see cref="OnSelectedTabIndexChanged"/>'s disk/DB reconcile trigger.</summary>
    public const int GalleryTabIndex = 2;

    private readonly IServiceProvider _services;
    private readonly OptionsSettingsService _optionsSettingsService;
    private readonly ISettingsStore _settingsStore;
    private readonly ILocalizationService _localization;
    private readonly IUrlLauncher _urlLauncher;
    private readonly ILogger<MainViewModel> _logger;

    /// <summary>User-requested (2026-08-28): the OS window title now shows the app name, version, and
    /// currently active configuration ("Scanline Studio v0.9-&lt;sha&gt; - Cfg: Default"). Name/version
    /// are read ONCE, at construction -- same <see cref="Assembly.GetEntryAssembly"/> +
    /// <see cref="AssemblyProductAttribute"/>/<see cref="AssemblyInformationalVersionAttribute"/>
    /// fallback chain <see cref="AboutWindowViewModel"/> already uses (small enough, and used in only
    /// these 2 places, that duplicating the 3 lines beats a shared helper for 2 callers).</summary>
    private readonly string _applicationName;

    private readonly string _versionDisplay;

    /// <summary>Repo's own GitHub URL (README.md's own citation, `git remote -v`) -- the Help
    /// menu's "Open on GitHub" target.</summary>
    private const string RepositoryUrl = "https://github.com/Ar0xA/Scanline-Studio";

    [ObservableProperty]
    private TxImageEditorPaneViewModel? _activeEditor;

    /// <summary>Backs the main `TabControl`'s `SelectedIndex` (`Mode=TwoWay` -- both directions are
    /// load-bearing: the user's own manual tab clicks must flow back here, and "Log QSO" on the RX
    /// pane needs to programmatically switch to <see cref="LogbookTabIndex"/>). The app's first
    /// VM-driven tab switch -- previously this TabControl had no binding at all, pure click-driven
    /// default behavior.</summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>User-reported gap, 2026-08-26: disk/DB divergence (real report was ~85 images on
    /// disk against 1 DB row) needs a reconcile pass -- triggered here, on Gallery tab SELECTION,
    /// matching the user's own stated trigger point exactly (not app startup, which would run the
    /// scan for a session that never even visits Gallery). <see cref="RxHistoryPaneViewModel.ReconcileDiskThenRefreshAsync"/>
    /// itself guards against repeat runs within one session -- this handler doesn't need its own
    /// such guard.</summary>
    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == GalleryTabIndex)
        {
            _ = RxHistory.ReconcileDiskThenRefreshAsync();
        }
    }

    /// <summary>Operator's own callsign (spec/09-ui.md menu-row chip, mock2's own top-right
    /// "DL2QSK" green pill) -- real, loaded from <see cref="OptionsSettingsService"/> the same
    /// way <see cref="OptionsWindowViewModel"/> does; null/empty until the user sets it in
    /// Options, in which case the chip stays hidden (see <see cref="HasCallsign"/>).</summary>
    [ObservableProperty]
    private string? _callsign;

    /// <summary>Null until <see cref="LoadActiveConfigurationNameAsync"/> first resolves it (fresh
    /// install, before anything has seeded/marked a configuration active) -- <see cref="WindowTitleDisplay"/>
    /// falls back to just name+version in that case, rather than showing a stale or guessed name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitleDisplay))]
    private string? _activeConfigurationName;

    /// <summary>The OS window title -- composed, not a static locale string, since it now carries the
    /// currently active configuration name.</summary>
    public string WindowTitleDisplay => ActiveConfigurationName is { } configName
        ? _localization.GetString("MainWindow.TitleWithConfig", _applicationName, _versionDisplay, configName)
        : _localization.GetString("MainWindow.TitleWithVersion", _applicationName, _versionDisplay);

    public MainViewModel(
        WaterfallPaneViewModel waterfall,
        RxImagePaneViewModel rxImage,
        RxHistoryPaneViewModel rxHistory,
        TxControlsPaneViewModel txControls,
        LogbookPaneViewModel logbook,
        DecoderTracePaneViewModel decoderTrace,
        IRadioSessionService radioSession,
        ISstvSessionService sstvSession,
        ILocalizationService localization,
        OptionsSettingsService optionsSettingsService,
        ISettingsStore settingsStore,
        IUrlLauncher urlLauncher,
        IServiceProvider services,
        ILogger<MainViewModel> logger,
        ILogger<RadioStatusViewModel> radioStatusLogger)
    {
        _services = services;
        _optionsSettingsService = optionsSettingsService;
        _settingsStore = settingsStore;
        _localization = localization;
        _urlLauncher = urlLauncher;
        _logger = logger;

        // Same Assembly.GetEntryAssembly() + attribute fallback chain AboutWindowViewModel already
        // uses -- see that class's own doc comment for why GetEntryAssembly (the running
        // ScanlineStudio.Host executable), not this assembly.
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        _applicationName = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "Scanline Studio";
        _versionDisplay = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        Waterfall = waterfall;
        RxImage = rxImage;
        RxHistory = rxHistory;
        TxControls = txControls;
        Logbook = logbook;
        DecoderTrace = decoderTrace;

        // Replaces AppDockFactory.OpenTxImageEditor/CloseTxImageEditor's AddDockable/CloseDockable
        // pair -- a plain nullable property swapped via a ContentControl in the Transmit tab.
        txControls.EditorOpened += editor => ActiveEditor = editor;
        txControls.EditorClosed += () => ActiveEditor = null;

        RadioStatus = new RadioStatusViewModel(radioSession, sstvSession, localization, radioStatusLogger);

        // Plain reference hand-off, not an XAML ancestor-lookup binding -- see
        // TxControlsPaneViewModel.RadioStatus's own doc comment for why.
        txControls.RadioStatus = RadioStatus;

        // Backlog item (user request, 2026-08-17): "it should ALWAYS open the editor by default, no
        // need for a button" -- auto-open the blank-placeholder editor immediately so the Transmit
        // tab's center column is never empty on first landing. Triggered HERE, not from
        // TxControlsPaneViewModel's own constructor: EditorOpened needs a subscriber attached
        // before OpenBlankEditorCommand's own async chain reaches its EditorOpened?.Invoke(editor)
        // call, and TxControlsPaneViewModel's constructor runs (as a DI-resolved parameter) BEFORE
        // this constructor -- and therefore before the subscription two lines above -- even exists.
        // No persisted "last loaded image" restoration happens anywhere at startup (confirmed via
        // LoadTxPaneUiSettingsAsync, which only restores AutoFollowRxMode/favorite-mode picks), so
        // this can never clobber a remembered real image -- there isn't one.
        //
        // Tier B audit finding: moved to run AFTER RadioStatus is assigned above (was before) --
        // on a fresh install with no settings.json yet, JsonSettingsStore.LoadAsync returns an
        // already-completed task, so this whole async chain (including the EditorOpened callback
        // above, synchronously) used to run to completion INLINE at this point in the constructor,
        // before RadioStatus existed. Nothing on that path reads RadioStatus today, so this was
        // harmless in practice, but it's a real first-run-only ordering hazard removed for free by
        // reordering rather than something to rely on staying harmless as this constructor grows.
        _ = OpenBlankEditorSafelyAsync(txControls);

        _ = LoadCallsignAsync();
        _ = LoadActiveConfigurationNameAsync();
    }

    // Tier B audit finding: OpenBlankEditorCommand.ExecuteAsync's own try/catch (inside
    // TxControlsPaneViewModel.OpenEditorWithLoadedSourceAsync) does not cover every statement this
    // command chain can reach (e.g. CloseBlankEditorForReplacement's own EditorClosed invocation) --
    // an exception escaping there from this fire-and-forget call would surface only as a
    // nondeterministic, context-free "unobserved task exception" log at GC time, same failure class
    // LoadCallsignAsync's own try/catch already guards against just below. Isolated the same way.
    private async Task OpenBlankEditorSafelyAsync(TxControlsPaneViewModel txControls)
    {
        try
        {
            await txControls.OpenBlankEditorCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            Log.OpenBlankEditorFailed(_logger, ex);
        }
    }

    public WaterfallPaneViewModel Waterfall { get; }

    public RxImagePaneViewModel RxImage { get; }

    public RxHistoryPaneViewModel RxHistory { get; }

    public TxControlsPaneViewModel TxControls { get; }

    public LogbookPaneViewModel Logbook { get; }

    public DecoderTracePaneViewModel DecoderTrace { get; }

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

    /// <summary>Help &gt; About (spec/18-path-to-1.0.md High item 5) -- same
    /// view-model-never-touches-a-Window reasoning as <see cref="OptionsRequested"/>. Not
    /// DI-resolved, unlike <see cref="OptionsWindowViewModel"/> -- <see cref="MainViewModel"/>
    /// already holds the one real dependency <see cref="AboutWindowViewModel"/> needs
    /// (<see cref="IUrlLauncher"/>, see its own doc comment), so a plain <c>new</c> here is
    /// simpler than registering it in the container for a single caller.</summary>
    public event Action<AboutWindowViewModel>? AboutRequested;

    [RelayCommand]
    private void OpenOptions()
    {
        Log.OpenOptionsInvoked(_logger);
        OptionsRequested?.Invoke(_services.GetRequiredService<OptionsWindowViewModel>());
    }

    /// <summary>Configurations &gt; Macros (stub survey Tier 3) -- same
    /// view-model-never-touches-a-Window/DI-resolved-via-IServiceProvider shape as
    /// <see cref="OpenOptions"/> above. Storage's own former entry here (stub survey Tier 2) was
    /// removed once its one setting (the RX images folder) moved into Options &gt; General
    /// alongside the new Config/Database/Log rows.</summary>
    public event Action<MacrosReferenceWindowViewModel>? MacrosReferenceRequested;

    [RelayCommand]
    private void OpenMacrosReference()
    {
        Log.OpenMacrosReferenceInvoked(_logger);
        MacrosReferenceRequested?.Invoke(_services.GetRequiredService<MacrosReferenceWindowViewModel>());
    }

    /// <summary>Configurations-preset backlog, Phase 4b (2026-08-28, cascading-menu redesign) -- the
    /// whole feature now lives inside the "Configurations" menu itself (per-configuration cascading
    /// submenus, `MainWindow.axaml.cs`'s own `SubmenuOpened` handler), so there is no standalone
    /// window/command to open anymore -- only this plain resolver, called fresh every time that menu
    /// opens.</summary>
    public ConfigurationsManagerWindowViewModel ResolveConfigurationsManagerViewModel() =>
        _services.GetRequiredService<ConfigurationsManagerWindowViewModel>();

    /// <summary>The header-row callsign chip's click target -- previously a static, non-interactive
    /// chip with nothing wired to it at all. Callsign/OperatorName/OperatorGrid live on the TX tab
    /// (<see cref="OptionsWindowViewModel.TxTabIndex"/>), so this jumps straight there instead of
    /// opening on whatever tab happened to be selected last time.</summary>
    [RelayCommand]
    private void OpenOptionsToTxTab()
    {
        Log.OpenOptionsInvoked(_logger);
        var optionsViewModel = _services.GetRequiredService<OptionsWindowViewModel>();
        optionsViewModel.SelectedTabIndex = OptionsWindowViewModel.TxTabIndex;
        OptionsRequested?.Invoke(optionsViewModel);
    }

    /// <summary>The radio-connection give-up popup's own "Config" button jump target (user request)
    /// -- rig/CAT connection settings live on the Radio tab, so this jumps straight there instead
    /// of opening on whatever tab happened to be selected last time. Same shape as
    /// <see cref="OpenOptionsToTxTab"/> above; invoked from <c>MainWindow.axaml.cs</c>'s own
    /// give-up-popup handler (that popup's view-model has no DI access of its own, see
    /// <see cref="RadioConnectionGaveUpWindowViewModel"/>'s own doc comment), not bound directly
    /// to a XAML Command the way <see cref="OpenOptionsToTxTab"/> is.</summary>
    [RelayCommand]
    private void OpenOptionsToRadioTab()
    {
        Log.OpenOptionsInvoked(_logger);
        var optionsViewModel = _services.GetRequiredService<OptionsWindowViewModel>();
        optionsViewModel.SelectedTabIndex = OptionsWindowViewModel.RadioTabIndex;
        OptionsRequested?.Invoke(optionsViewModel);
    }

    [RelayCommand]
    private void OpenAbout()
    {
        Log.OpenAboutInvoked(_logger);
        AboutRequested?.Invoke(new AboutWindowViewModel(_urlLauncher));
    }

    [RelayCommand]
    private void OpenOnGitHub()
    {
        Log.OpenOnGitHubInvoked(_logger);
        _urlLauncher.Open(RepositoryUrl);
    }

    [RelayCommand]
    private void OpenApplicationLog()
    {
        Log.OpenApplicationLogInvoked(_logger);
        _urlLauncher.Open(AppLogPaths.LogDirectory);
    }

    [RelayCommand]
    private void Exit()
    {
        Log.ExitInvoked(_logger);
        ExitRequested?.Invoke();
    }

    /// <summary>Not private: also called from <c>MainWindow.axaml.cs</c>'s <see cref="OptionsRequested"/>
    /// handler once the Options window closes, so the header-row callsign chip
    /// (<see cref="CallsignDisplay"/>) picks up a callsign the user just typed and saved. Before this,
    /// this method only ever ran once, from the constructor -- the chip kept showing whatever
    /// callsign (or "N0CALL") was on disk at app startup until the next full restart, even after a
    /// successful Save (user-reported bug, 2026-08-23). Public, not internal -- this project has no
    /// <c>InternalsVisibleTo</c> wired up anywhere (see e.g. <c>AboutWindowViewModel</c>'s own doc
    /// comment), so a test needing to call this directly couldn't otherwise.</summary>
    public async Task LoadCallsignAsync()
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

    /// <summary>User-requested (2026-08-28): re-read after every Configurations-menu switch too
    /// (<c>MainWindow.axaml.cs</c>'s own <c>RefreshAfterConfigurationChange</c>), same reasoning as
    /// every other refresh-on-switch call there -- a switch changes which configuration is active.
    /// Public for the same reason <see cref="LoadCallsignAsync"/> is.</summary>
    public async Task LoadActiveConfigurationNameAsync()
    {
        try
        {
            var settings = await _settingsStore.LoadAsync();
            ActiveConfigurationName = settings.GetSection(
                ConfigurationPresetSettings.SectionKey, ConfigurationPresetSettingsJsonContext.Default.ConfigurationPresetSettings)?.ActivePresetName;
        }
        catch (Exception ex)
        {
            Log.LoadActiveConfigurationNameFailed(_logger, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "OpenOptions command invoked")]
        public static partial void OpenOptionsInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "OpenMacrosReference command invoked")]
        public static partial void OpenMacrosReferenceInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "OpenAbout command invoked")]
        public static partial void OpenAboutInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "OpenOnGitHub command invoked")]
        public static partial void OpenOnGitHubInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "OpenApplicationLog command invoked")]
        public static partial void OpenApplicationLogInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Exit command invoked")]
        public static partial void ExitInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error, Message = "Loading operator callsign failed; menu-row chip stays hidden")]
        public static partial void LoadCallsignFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Loading the active configuration name failed; window title omits it")]
        public static partial void LoadActiveConfigurationNameFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Auto-opening the blank TX editor at startup failed; Transmit tab may stay empty")]
        public static partial void OpenBlankEditorFailed(ILogger logger, Exception ex);
    }
}
