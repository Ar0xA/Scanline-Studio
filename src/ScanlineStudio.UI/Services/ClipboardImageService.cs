using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace ScanlineStudio.UI.Services;

public sealed partial class ClipboardImageService : IClipboardImageService
{
    private readonly ILogger<ClipboardImageService> _logger;

    public ClipboardImageService(ILogger<ClipboardImageService> logger)
    {
        _logger = logger;
    }

    public async Task<bool> CopyImageAsync(Bitmap bitmap)
    {
        // Fully qualified: bare "Application" resolves to the sibling ScanlineStudio.Application
        // namespace from anywhere under the shared ScanlineStudio root -- same gotcha
        // FilePickerService.PickClipboardImageAsync already documents for itself.
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            Log.NoMainWindow(_logger);
            return false;
        }

        // TopLevel.Clipboard is nullable on some backends (documented "may be unavailable" case,
        // not an error) -- same outcome FilePickerService.PickClipboardImageAsync treats as a
        // silent no-op.
        if (mainWindow.Clipboard is not { } clipboard)
        {
            return false;
        }

        try
        {
            // ClipboardExtensions.SetBitmapAsync -- confirmed via reflection against the pinned
            // Avalonia 11.3.12 package before use (same verification discipline
            // FilePickerService.PickClipboardImageAsync's own TryGetBitmapAsync comment documents):
            // a real, built-in, cross-platform helper, not a hand-rolled per-platform format sniff.
            await clipboard.SetBitmapAsync(bitmap);
            return true;
        }
        catch (Exception ex)
        {
            Log.CopyImageFailed(_logger, ex);
            return false;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "No main window available to copy an image to the clipboard")]
        public static partial void NoMainWindow(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Copying an image to the clipboard failed")]
        public static partial void CopyImageFailed(ILogger logger, Exception exception);
    }
}
