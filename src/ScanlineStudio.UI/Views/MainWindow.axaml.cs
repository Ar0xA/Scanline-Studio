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
                Position = new PixelPoint((int)left, (int)top);
                Width = width;
                Height = height;
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
        };

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.OptionsRequested += optionsViewModel =>
                {
                    if (logger is not null)
                    {
                        Log.ConstructingOptionsWindow(logger);
                    }

                    var window = new OptionsWindowView { DataContext = optionsViewModel };
                    window.Opened += (_, _) =>
                    {
                        if (logger is not null)
                        {
                            Log.OptionsWindowOpened(logger, window.Position.ToString(), Screens.ScreenFromWindow(window)?.Bounds.ToString() ?? "(none)");
                        }
                    };
                    window.ShowDialog(this);
                    if (logger is not null)
                    {
                        Log.ShowDialogReturned(logger);
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

        [LoggerMessage(Level = LogLevel.Debug, Message = "Exit requested; closing main window")]
        public static partial void ExitRequested(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Constructing and showing QsoLinkWindowView")]
        public static partial void ConstructingQsoLinkWindow(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "QsoLinkWindowView.ShowDialog returned")]
        public static partial void QsoLinkShowDialogReturned(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QsoLinkWindowView failed to open or show")]
        public static partial void QsoLinkWindowFailed(ILogger logger, Exception ex);
    }
}
