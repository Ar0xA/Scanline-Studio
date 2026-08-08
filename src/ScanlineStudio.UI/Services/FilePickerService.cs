using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;

namespace ScanlineStudio.UI.Services;

public sealed partial class FilePickerService : IFilePickerService
{
    private readonly ILogger<FilePickerService> _logger;

    public FilePickerService(ILogger<FilePickerService> logger)
    {
        _logger = logger;
    }

    public async Task<string?> PickImageFileAsync()
    {
        // Fully qualified: bare "Application" resolves to the sibling ScanlineStudio.Application namespace
        // from anywhere under the shared ScanlineStudio root (same gotcha App.axaml.cs hit first).
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            // Indistinguishable from "user cancelled" to the caller today (both return null) --
            // this at least makes the real cause visible in the log.
            Log.NoMainWindow(_logger);
            return null;
        }

        var files = await mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private static readonly FilePickerFileType AdifFileType = new("ADIF log files")
    {
        Patterns = ["*.adi", "*.adif"],
    };

    public async Task<string?> PickAdifFileAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            Log.NoMainWindow(_logger);
            return null;
        }

        var files = await mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [AdifFileType],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveAdifFileAsync(string suggestedFileName)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            Log.NoMainWindow(_logger);
            return null;
        }

        var file = await mainWindow.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "adi",
            FileTypeChoices = [AdifFileType],
        });

        return file?.TryGetLocalPath();
    }

    private static partial class Log
    {
        // [CallerMemberName] resolves to whichever Pick*Async method called this, NOT the
        // suppressed positional-argument name -- shared by all 3 pickers, so the log line no longer
        // hardcodes "PickImageFileAsync" for an ADIF-picker failure.
        [LoggerMessage(Level = LogLevel.Warning, Message = "{CallerMemberName}: no MainWindow available; returning null (looks like a cancel to the caller)")]
        public static partial void NoMainWindow(ILogger logger, [CallerMemberName] string callerMemberName = "");
    }
}
