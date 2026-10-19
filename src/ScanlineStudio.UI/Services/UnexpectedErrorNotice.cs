using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.Logging;
using ScanlineStudio.UI.ViewModels;
using ScanlineStudio.UI.Views;

namespace ScanlineStudio.UI.Services;

/// <summary>Non-modal on purpose: a modal notice for an error that repeats would disable the main window,
/// including header Stop TX, until the process is killed.</summary>
public sealed partial class UnexpectedErrorNotice : IUnexpectedErrorNotice
{
    private readonly ILogger<UnexpectedErrorNotice> _logger;

    public UnexpectedErrorNotice(ILogger<UnexpectedErrorNotice> logger)
    {
        _logger = logger;
    }

    public Task ShowAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            Log.NoOwnerWindow(_logger);
            return Task.CompletedTask;
        }

        var owner = desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
        if (owner is not { IsVisible: true })
        {
            Log.NoOwnerWindow(_logger);
            return Task.CompletedTask;
        }

        var viewModel = new UnexpectedErrorDialogViewModel();
        var view = new UnexpectedErrorDialogView { DataContext = viewModel };
        var closed = new TaskCompletionSource();
        view.Closed += (_, _) => closed.TrySetResult();
        view.Show(owner);
        return closed.Task;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Unexpected-error notice not shown: no visible window to own it")]
        public static partial void NoOwnerWindow(ILogger logger);
    }
}
