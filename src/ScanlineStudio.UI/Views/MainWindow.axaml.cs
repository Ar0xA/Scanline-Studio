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
    }
}
