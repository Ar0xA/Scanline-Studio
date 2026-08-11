using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class MainWindow : Window
{
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
