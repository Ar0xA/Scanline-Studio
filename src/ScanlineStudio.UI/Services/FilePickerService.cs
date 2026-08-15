using System.IO;
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

    // Public (not private): code-review finding -- ResolveDestination below needs to reference the
    // EXACT SAME instances a real picker would echo back via SelectedFileType, and tests need to
    // construct that same realistic scenario without needing Application.Current/MainWindow (which
    // ResolveDestination itself deliberately doesn't touch).
    public static readonly FilePickerFileType PngFileType = new("PNG image")
    {
        Patterns = ["*.png"],
    };

    public static readonly FilePickerFileType JpegFileType = new("JPEG image")
    {
        Patterns = ["*.jpg", "*.jpeg"],
    };

    public async Task<(string Path, ImageExportFormat Format)?> PickSaveImageFileAsync(string suggestedFileName)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            Log.NoMainWindow(_logger);
            return null;
        }

        var result = await mainWindow.StorageProvider.SaveFilePickerWithResultAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedFileName,
            FileTypeChoices = [PngFileType, JpegFileType],
        });

        if (result.File?.TryGetLocalPath() is not { } path)
        {
            return null;
        }

        return ResolveDestination(path, result.SelectedFileType);
    }

    /// <summary>Code-review finding: extracted from <see cref="PickSaveImageFileAsync"/> so this
    /// branching logic (the only real logic in that method) is independently unit-testable -- the
    /// rest of that method needs a live <c>Application.Current</c>/<c>MainWindow</c> and can't be.
    /// <paramref name="selectedType"/> is the source of truth for which encoder to use --
    /// confirmed via real-window testing on this app's Linux/GTK backend (see
    /// `export-frame-jpeg-quality.md`'s own verification log) that a plain
    /// <c>SaveFilePickerAsync</c>'s returned path does NOT get its extension rewritten when the
    /// user switches the type dropdown -- <paramref name="path"/> alone is not trustworthy.
    /// <b>Reference-equality comparison</b> (<paramref name="selectedType"/> against
    /// <see cref="PngFileType"/>/<see cref="JpegFileType"/>, not a value/pattern comparison --
    /// <see cref="FilePickerFileType"/> declares no <c>Equals</c>/<c>==</c> override) relies on
    /// each platform backend echoing back the SAME instance passed into <c>FileTypeChoices</c>;
    /// confirmed true on Linux/GTK, NOT independently verified on Windows/macOS. Falls back to
    /// sniffing <paramref name="path"/>'s own extension only if <paramref name="selectedType"/> is
    /// null (documented case, "or null if not supported") OR — on an unverified platform where the
    /// echoed-back instance turns out not to be reference-equal — always, silently taking the
    /// sniff path instead of throwing; bounded blast radius either way, since Win32's own
    /// documented behavior is to rewrite the path's extension itself, which the sniff path already
    /// handles correctly.</summary>
    public static (string Path, ImageExportFormat Format) ResolveDestination(string path, FilePickerFileType? selectedType)
    {
        ImageExportFormat format;
        if (selectedType == JpegFileType)
        {
            format = ImageExportFormat.Jpeg;
        }
        else if (selectedType == PngFileType)
        {
            format = ImageExportFormat.Png;
        }
        else
        {
            var sniffedExtension = Path.GetExtension(path).ToLowerInvariant();
            format = sniffedExtension is ".jpg" or ".jpeg" ? ImageExportFormat.Jpeg : ImageExportFormat.Png;
        }

        var desiredExtension = format == ImageExportFormat.Jpeg ? ".jpg" : ".png";
        var currentExtension = Path.GetExtension(path);
        var extensionAlreadyMatches = currentExtension.Equals(desiredExtension, StringComparison.OrdinalIgnoreCase)
            || (format == ImageExportFormat.Jpeg && currentExtension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase));
        var normalizedPath = extensionAlreadyMatches ? path : Path.ChangeExtension(path, desiredExtension);

        return (normalizedPath, format);
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
