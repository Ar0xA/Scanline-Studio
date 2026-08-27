using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Settings;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class MainWindow : Window
{
    private readonly ISettingsStore? _settingsStore;

    // Give-up-after-5 feature: dedup guard for the modal popup below -- ConnectionGaveUp is a
    // system-triggered event that can in principle fire again (e.g. the user retries via Options and
    // that retry also exhausts its 5 attempts) before the user has dismissed an already-open popup
    // from the previous give-up. Null once the window is closed (or before the first give-up).
    private RadioConnectionGaveUpWindowView? _connectionGaveUpWindow;

    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
        // Views aren't DI-constructed (Avalonia builds them via `new`, not the container) -- resolved
        // from App.Services directly, same pattern this project already uses for other code-behind
        // needs (e.g. FilePickerService). Null-tolerant: a headless/design-time construction with no
        // App.Services set must not throw here.
        var logger = App.Services?.GetService<ILogger<MainWindow>>();
        _settingsStore = App.Services?.GetService<ISettingsStore>();

        // Restore window geometry BEFORE the window is ever shown (App.axaml.cs constructs this
        // window, then hands it to the desktop lifetime -- no visible "jump" this way).
        //
        // Deadlock fix (found via real-window testing, not assumed safe): a bare
        // `_settingsStore.LoadAsync().GetAwaiter().GetResult()` here hangs the app on startup.
        // ScanlineStudio.Host.Program's ISstvDecoder factory does the same synchronous-block shape
        // and is safe -- but it runs during generic-host/DI-container construction, BEFORE
        // BuildAvaloniaApp().Start*() ever installs Avalonia's UI-thread SynchronizationContext.
        // This constructor runs AFTER that (App.axaml.cs's OnFrameworkInitializationCompleted calls
        // `new MainWindow()` on the UI thread), and JsonSettingsStore.LoadAsync's own `await`s have
        // no ConfigureAwait(false) -- their continuation tries to resume back on the captured
        // UI-thread context, which is exactly the thread blocked waiting on GetResult(). Wrapping in
        // Task.Run moves the whole awaited chain onto a thread-pool thread with no captured context,
        // so the continuation never needs the blocked UI thread back. Port of legacy's own
        // Main.cpp:1686-1692 load gate (sys.m_MemWindow) -- see WindowGeometrySettings' own doc
        // comment for the full citation.
        if (_settingsStore is not null)
        {
            var geometry = Task.Run(() => _settingsStore.LoadAsync()).GetAwaiter().GetResult()
                .GetSection(WindowGeometrySettings.SectionKey, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings);
            if (geometry is { RememberWindowPosition: true, Left: { } left, Top: { } top, Width: { } width, Height: { } height })
            {
                // Tier C audit finding (risk): applied with zero bounds validation before this fix --
                // a position persisted while on a since-removed monitor (unplugged second display, a
                // resolution change) restored to coordinates with no display behind them. Windows
                // does not clamp this, so the app appeared not to start, and the only recovery was
                // hand-deleting settings.json (the Options toggle to turn this off lives inside the
                // invisible window). `Screens.All` may not be reliably populated this early in every
                // Avalonia configuration -- if the list comes back empty, fall back to trusting the
                // persisted value rather than disabling the whole feature; only reject when a screen
                // list IS available and genuinely none of them contain this position.
                var restoredPosition = new PixelPoint((int)left, (int)top);
                var screenBounds = Screens.All.Select(s => s.Bounds).ToList();
                if (WindowGeometryPolicy.ShouldRestorePosition(restoredPosition, screenBounds))
                {
                    Position = restoredPosition;
                    Width = width;
                    Height = height;
                }
                else if (logger is not null)
                {
                    Log.RestoredWindowPositionOffScreen(logger, restoredPosition.X, restoredPosition.Y);
                }
            }
        }

        // Port of legacy's own Main.cpp:2214-2224 save gate (sys.m_MemWindow AND WindowState ==
        // wsNormal -- geometry is never captured while maximized/minimized). Re-reads
        // RememberWindowPosition fresh rather than caching the constructor's value, since the user
        // may have toggled it via Options mid-session. Same Task.Run deadlock fix as above --
        // real-window-testing-caught bug, round 2: Avalonia's Position/Width/Height (and every other
        // Layoutable property) can only be read on the UI thread, so they must be captured BEFORE
        // entering Task.Run, not read from inside its pool-thread delegate (that threw
        // "Call from invalid thread" and crashed the app on close, caught only by actually closing a
        // real running window, not by the build or test suite).
        Closing += (_, _) =>
        {
            if (_settingsStore is null || WindowState != WindowState.Normal)
            {
                return;
            }

            var left = Position.X;
            var top = Position.Y;
            var width = Width;
            var height = Height;

            // Tier C audit finding (risk): unguarded before this fix -- JsonSettingsStore.SaveAsync's
            // own Directory.CreateDirectory/File.Create/File.Move calls have no exception handling of
            // their own (unlike its sibling LoadAsync), so a disk-full/read-only-profile/settings.json-
            // locked-by-a-sync-client failure rethrew on the UI thread inside Closing, escaping past
            // Program.cs's own try/catch around lifetime.Start -- which means lifetime.Exit never
            // fires, and the 10s-bounded host DisposeAsync (audio capture device / radio connection
            // teardown) is skipped entirely, not just this save. Best-effort like every other
            // settings-write path in this app: log and let the window close anyway.
            try
            {
                Task.Run(async () =>
                {
                    var settings = await _settingsStore.LoadAsync();
                    var current = settings.GetSection(WindowGeometrySettings.SectionKey, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings) ?? new WindowGeometrySettings();
                    if (current.RememberWindowPosition != true)
                    {
                        return;
                    }

                    var updated = current with { Left = left, Top = top, Width = width, Height = height };
                    await _settingsStore.SaveAsync(settings.WithSection(WindowGeometrySettings.SectionKey, updated, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings));
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                if (logger is not null)
                {
                    Log.WindowGeometrySaveFailed(logger, ex);
                }
            }
        };

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                // Give-up-after-5 feature: the must-acknowledge popup (direct feedback: "should be a
                // popup window, not a tiny text under the VFO" -- replaces this feature's earlier
                // dismissible-toast, and later a persistent header text line that was tried alongside
                // this popup then removed as redundant, per user decision).
                //
                // Dedup-guarded by _connectionGaveUpWindow: ConnectionGaveUp is system-triggered and
                // can in principle fire again (e.g. a user-initiated retry via Options also exhausts
                // its own 5 attempts) before an already-open popup from a previous give-up has been
                // dismissed -- skip opening a second one rather than stacking dialogs.
                //
                // Code-review finding (carried over from the toast this replaces): wrapped in
                // try/catch, same reasoning as OptionsRequested/AboutRequested/QsoLinkRequested's own
                // handlers below -- an unhandled exception here would otherwise crash the process,
                // AND (this handler's own extra hazard, RadioStatusViewModel.OnConnectionEvent's own
                // doc comment) abort the CALLER's Dispatcher.Post callback mid-way, since that raises
                // this event synchronously.
                vm.RadioStatus.ConnectionGaveUp += message =>
                {
                    if (_connectionGaveUpWindow is not null)
                    {
                        return;
                    }

                    if (logger is not null)
                    {
                        Log.ConstructingConnectionGaveUpWindow(logger);
                    }

                    try
                    {
                        var giveUpViewModel = new RadioConnectionGaveUpWindowViewModel(message);
                        // User request: the popup's own "Config" button has no DI access of its
                        // own (see that view-model's own doc comment) -- this handler already owns
                        // the popup's lifecycle, so it's also where the jump-to-Options-Radio-tab
                        // request gets fulfilled, same view-model-never-touches-a-Window reasoning
                        // as every other cross-window request in this file.
                        giveUpViewModel.ConfigRequested += () => vm.OpenOptionsToRadioTabCommand.Execute(null);
                        var window = new RadioConnectionGaveUpWindowView
                        {
                            DataContext = giveUpViewModel,
                        };
                        _connectionGaveUpWindow = window;
                        window.Closed += (_, _) => _connectionGaveUpWindow = null;
                        window.ShowDialog(this);
                        if (logger is not null)
                        {
                            Log.ConnectionGaveUpShowDialogReturned(logger);
                        }
                    }
                    catch (Exception ex)
                    {
                        _connectionGaveUpWindow = null;
                        if (logger is not null)
                        {
                            Log.ConnectionGaveUpWindowFailed(logger, ex);
                        }
                    }
                };

                vm.OptionsRequested += optionsViewModel =>
                {
                    if (logger is not null)
                    {
                        Log.ConstructingOptionsWindow(logger);
                    }

                    // Tier C audit finding (risk): unguarded before this fix -- the comment this
                    // replaced claimed "no exception surface to guard," but InitializeComponent()
                    // (run inside the OptionsWindowView constructor) throws on a bad binding/missing
                    // resource, and ShowDialog can throw synchronously too; either one escaped through
                    // RelayCommand.Execute into input dispatch and crashed the process with only the
                    // generic AppDomain net for a trace. Same shape as QsoLinkRequested's own handler
                    // below.
                    try
                    {
                        var window = new OptionsWindowView { DataContext = optionsViewModel };
                        window.Opened += (_, _) =>
                        {
                            if (logger is not null)
                            {
                                Log.OptionsWindowOpened(logger, window.Position.ToString(), Screens.ScreenFromWindow(window)?.Bounds.ToString() ?? "(none)");
                            }
                        };
                        // User-reported bug (2026-08-23): the header-row callsign chip only ever
                        // loaded its callsign once, from MainViewModel's own constructor -- typing a
                        // new callsign in Options and hitting Save persisted it correctly, but the
                        // chip kept showing the old value (or "N0CALL") until the next full app
                        // restart. Re-running LoadCallsignAsync unconditionally on close (Save AND
                        // Cancel) is safe -- Cancel never touched disk, so this is a no-op reload of
                        // the same value in that case, same as re-running it costs nothing extra.
                        // Same bug, same fix, for the Transmit tab's Output-device and Identification
                        // fields (TxControlsPaneViewModel.LoadOutputDeviceNameAsync/LoadIdentificationSummaryAsync).
                        window.Closed += (_, _) =>
                        {
                            _ = vm.LoadCallsignAsync();
                            _ = vm.TxControls.LoadOutputDeviceNameAsync();
                            _ = vm.TxControls.LoadIdentificationSummaryAsync();
                            // Storage section moved in from the former standalone "Configurations >
                            // Storage" dialog (2026-08-27) -- carries over that dialog's own
                            // refresh-the-Gallery-Storage-card-on-close behavior, now for General
                            // tab's Images row instead.
                            _ = vm.RxHistory.LoadImagesDirectoryAsync();
                        };
                        // Options > General's Config/Database "Restart Now" -- closes THIS dialog
                        // first, then (from that window's own Closed event, not inline right after
                        // Close() -- Avalonia's Close() raising Closed synchronously isn't something
                        // this codebase can verify against its own vendored sources) closes
                        // MainWindow itself, reaching the exact same shutdown path
                        // MainViewModel.ExitRequested's own handler below uses for File > Exit. No
                        // process is spawned from here -- OptionsWindowViewModel.RestartNowAsync
                        // already set IApplicationRestarter.RestartRequested; the actual spawn only
                        // happens from Program.cs, after this process's own teardown completes.
                        optionsViewModel.RestartRequested += () =>
                        {
                            if (logger is not null)
                            {
                                Log.RestartRequested(logger);
                            }

                            window.Closed += (_, _) => Close();
                            window.Close();
                        };
                        window.ShowDialog(this);
                        if (logger is not null)
                        {
                            Log.ShowDialogReturned(logger);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (logger is not null)
                        {
                            Log.OptionsWindowFailed(logger, ex);
                        }
                    }
                };

                // Same synchronous, unawaited shape as OptionsRequested above (spec/18-path-to-1.0.md
                // High item 5) -- a static-content dialog with no async work.
                vm.AboutRequested += aboutViewModel =>
                {
                    if (logger is not null)
                    {
                        Log.ConstructingAboutWindow(logger);
                    }

                    // Tier C audit finding (risk): same reasoning as OptionsRequested's own fix above.
                    try
                    {
                        var window = new AboutWindowView { DataContext = aboutViewModel };
                        window.ShowDialog(this);
                        if (logger is not null)
                        {
                            Log.AboutShowDialogReturned(logger);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (logger is not null)
                        {
                            Log.AboutWindowFailed(logger, ex);
                        }
                    }
                };

                // Code-review fix: async void event handler (the delegate type is Action<...>, not
                // Func<...,Task>) -- an unhandled exception from ShowDialog would otherwise crash the
                // process instead of logging. Wrapped explicitly rather than left to propagate, unlike
                // OptionsRequested's own synchronous (and unawaited) handler above, which has no
                // await to fail past construction.
                vm.RxHistory.QsoLinkRequested += async qsoLinkViewModel =>
                {
                    if (logger is not null)
                    {
                        Log.ConstructingQsoLinkWindow(logger);
                    }

                    try
                    {
                        var window = new QsoLinkWindowView { DataContext = qsoLinkViewModel };
                        await window.ShowDialog(this);
                        if (logger is not null)
                        {
                            Log.QsoLinkShowDialogReturned(logger);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (logger is not null)
                        {
                            Log.QsoLinkWindowFailed(logger, ex);
                        }
                    }
                };

                // Deliberately synchronous, unlike QsoLinkRequested's handler above -- no dialog,
                // no ShowDialog to await. Safe to read vm.RxImage's properties directly here: every
                // writer of OverrideCallsign/DetectedMode/StartedAt already routes through
                // Dispatcher.UIThread.Post, and this handler itself only ever runs on the UI
                // thread (LogQsoCommand is a button click), so there is no interleaving window
                // between "user clicked Log QSO" and this read.
                vm.RxImage.LogQsoRequested += () =>
                {
                    if (logger is not null)
                    {
                        Log.LogQsoRequested(logger);
                    }

                    vm.Logbook.PrefillForNewEntry(
                        vm.RxImage.OverrideCallsign,
                        vm.RxImage.DetectedMode?.Id,
                        vm.RxImage.StartedAt ?? DateTimeOffset.UtcNow,
                        vm.RxImage.LookupName,
                        vm.RxImage.LookupQth,
                        vm.RxImage.LookupGrid);
                    vm.SelectedTabIndex = MainViewModel.LogbookTabIndex;
                };

                // Stub survey Tier 3 (2026-08-26). Same shape as OptionsRequested above --
                // DI-resolved view-model. Storage's own former entry here (stub survey Tier 2) was
                // removed once its one setting (the RX images folder) moved into Options > General
                // alongside the new Config/Database/Log rows.
                vm.MacrosReferenceRequested += macrosViewModel =>
                {
                    if (logger is not null)
                    {
                        Log.ConstructingMacrosReferenceWindow(logger);
                    }

                    try
                    {
                        var window = new MacrosReferenceWindowView { DataContext = macrosViewModel };
                        window.ShowDialog(this);
                        if (logger is not null)
                        {
                            Log.MacrosReferenceShowDialogReturned(logger);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (logger is not null)
                        {
                            Log.MacrosReferenceWindowFailed(logger, ex);
                        }
                    }
                };

                // Stub survey Tier 2 (2026-08-26). Same synchronous, unawaited shape as
                // OptionsRequested/AboutRequested above -- ShowDialog itself is synchronous (blocks
                // until the dialog closes), no async construction work precedes it.
                vm.RadioStatus.FavouritesEditorRequested += () =>
                {
                    if (logger is not null)
                    {
                        Log.ConstructingFavouritesEditorWindow(logger);
                    }

                    try
                    {
                        var window = new FavouritesEditorWindowView { DataContext = vm.RadioStatus };
                        window.ShowDialog(this);
                        if (logger is not null)
                        {
                            Log.FavouritesEditorShowDialogReturned(logger);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (logger is not null)
                        {
                            Log.FavouritesEditorWindowFailed(logger, ex);
                        }
                    }
                };

                vm.RadioStatus.ToneGeneratorRequested += () =>
                {
                    if (logger is not null)
                    {
                        Log.ConstructingToneGeneratorWindow(logger);
                    }

                    try
                    {
                        var window = new ToneGeneratorWindowView { DataContext = vm.RadioStatus };
                        window.ShowDialog(this);
                        if (logger is not null)
                        {
                            Log.ToneGeneratorShowDialogReturned(logger);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (logger is not null)
                        {
                            Log.ToneGeneratorWindowFailed(logger, ex);
                        }
                    }
                };

                // Stub survey Tier 3 "Loopback self-test" (2026-08-26). Same shape as
                // FavouritesEditorRequested/ToneGeneratorRequested above -- pane-level event, not
                // forwarded through MainViewModel, since the result window needs a fresh, single-use
                // view-model (the result of ONE self-test run), not TxControls itself as its
                // DataContext.
                vm.TxControls.LoopbackSelfTestCompleted += result =>
                {
                    if (logger is not null)
                    {
                        Log.ConstructingLoopbackSelfTestResultWindow(logger);
                    }

                    try
                    {
                        var window = new LoopbackSelfTestResultWindowView { DataContext = result };
                        window.ShowDialog(this);
                        if (logger is not null)
                        {
                            Log.LoopbackSelfTestResultShowDialogReturned(logger);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (logger is not null)
                        {
                            Log.LoopbackSelfTestResultWindowFailed(logger, ex);
                        }
                    }
                };

                vm.ExitRequested += () =>
                {
                    if (logger is not null)
                    {
                        Log.ExitRequested(logger);
                    }

                    Close();
                };
            }
        };
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Constructing and showing OptionsWindowView")]
        public static partial void ConstructingOptionsWindow(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "OptionsWindowView opened: Position={Position}, Screen={Screen}")]
        public static partial void OptionsWindowOpened(ILogger logger, string position, string screen);

        [LoggerMessage(Level = LogLevel.Debug, Message = "OptionsWindowView.ShowDialog returned")]
        public static partial void ShowDialogReturned(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Restart requested from Options > General (Config/Database Restart Now) -- closing Options, then the main window")]
        public static partial void RestartRequested(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Constructing and showing MacrosReferenceWindowView")]
        public static partial void ConstructingMacrosReferenceWindow(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "MacrosReferenceWindowView.ShowDialog returned")]
        public static partial void MacrosReferenceShowDialogReturned(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "MacrosReferenceWindowView failed to open or show")]
        public static partial void MacrosReferenceWindowFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Exit requested; closing main window")]
        public static partial void ExitRequested(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Constructing and showing AboutWindowView")]
        public static partial void ConstructingAboutWindow(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "AboutWindowView.ShowDialog returned")]
        public static partial void AboutShowDialogReturned(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Constructing and showing QsoLinkWindowView")]
        public static partial void ConstructingQsoLinkWindow(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "QsoLinkWindowView.ShowDialog returned")]
        public static partial void QsoLinkShowDialogReturned(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QsoLinkWindowView failed to open or show")]
        public static partial void QsoLinkWindowFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Constructing and showing FavouritesEditorWindowView")]
        public static partial void ConstructingFavouritesEditorWindow(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "FavouritesEditorWindowView.ShowDialog returned")]
        public static partial void FavouritesEditorShowDialogReturned(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "FavouritesEditorWindowView failed to open or show")]
        public static partial void FavouritesEditorWindowFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Constructing and showing ToneGeneratorWindowView")]
        public static partial void ConstructingToneGeneratorWindow(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "ToneGeneratorWindowView.ShowDialog returned")]
        public static partial void ToneGeneratorShowDialogReturned(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "ToneGeneratorWindowView failed to open or show")]
        public static partial void ToneGeneratorWindowFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Constructing and showing LoopbackSelfTestResultWindowView")]
        public static partial void ConstructingLoopbackSelfTestResultWindow(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "LoopbackSelfTestResultWindowView.ShowDialog returned")]
        public static partial void LoopbackSelfTestResultShowDialogReturned(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "LoopbackSelfTestResultWindowView failed to open or show")]
        public static partial void LoopbackSelfTestResultWindowFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Log QSO requested from the RX pane; switching to the Logbook tab")]
        public static partial void LogQsoRequested(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Restored window position ({X}, {Y}) is not on any known screen; using the default position instead")]
        public static partial void RestoredWindowPositionOffScreen(ILogger logger, int x, int y);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to save window geometry on close")]
        public static partial void WindowGeometrySaveFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "OptionsWindowView failed to open or show")]
        public static partial void OptionsWindowFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "AboutWindowView failed to open or show")]
        public static partial void AboutWindowFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Constructing and showing RadioConnectionGaveUpWindowView")]
        public static partial void ConstructingConnectionGaveUpWindow(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "RadioConnectionGaveUpWindowView.ShowDialog returned")]
        public static partial void ConnectionGaveUpShowDialogReturned(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "RadioConnectionGaveUpWindowView failed to open or show")]
        public static partial void ConnectionGaveUpWindowFailed(ILogger logger, Exception ex);
    }
}
