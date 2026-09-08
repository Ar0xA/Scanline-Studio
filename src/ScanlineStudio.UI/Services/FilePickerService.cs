using System.IO;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.UI.ViewModels;
using ScanlineStudio.UI.Views;

namespace ScanlineStudio.UI.Services;

public sealed partial class FilePickerService : IFilePickerService
{
    private readonly ILogger<FilePickerService> _logger;
    private readonly ILocalizationService _localization;

    public FilePickerService(ILogger<FilePickerService> logger, ILocalizationService localization)
    {
        _logger = logger;
        _localization = localization;
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

    public async Task<string?> PickClipboardImageAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            Log.NoMainWindow(_logger);
            return null;
        }

        // TopLevel.Clipboard is nullable on some backends (documented "may be unavailable" case,
        // not an error) -- same "no clipboard here" outcome as "nothing copied yet" below.
        if (mainWindow.Clipboard is not { } clipboard)
        {
            return null;
        }

        Avalonia.Media.Imaging.Bitmap? bitmap;
        try
        {
            // ClipboardExtensions.TryGetBitmapAsync -- confirmed via reflection against the pinned
            // Avalonia 11.3.12 package before use: a real, built-in, cross-platform helper, not a
            // hand-rolled per-platform MIME-type sniff over the lower-level GetDataAsync(string).
            bitmap = await clipboard.TryGetBitmapAsync();
        }
        catch (Exception ex)
        {
            Log.ClipboardImageReadFailed(_logger, ex);
            return null;
        }

        if (bitmap is null)
        {
            // Nothing image-shaped on the clipboard right now -- a normal, silent no-op state (the
            // system clipboard usually holds text, not a picture), not an error.
            return null;
        }

        using (bitmap)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
            try
            {
                // Bitmap.Save always encodes PNG regardless of the given path's extension (confirmed
                // via reflection: the overload takes no format parameter) -- matches the ".png"
                // extension chosen here, not a mismatch.
                bitmap.Save(tempPath);
            }
            catch (Exception ex)
            {
                Log.ClipboardImageReadFailed(_logger, ex);
                return null;
            }

            return tempPath;
        }
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

    private static readonly FilePickerFileType WavFileType = new("WAV audio files")
    {
        Patterns = ["*.wav"],
    };

    public async Task<string?> PickOpenWavFileAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            Log.NoMainWindow(_logger);
            return null;
        }

        var files = await mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [WavFileType],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveWavFileAsync(string suggestedFileName)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            Log.NoMainWindow(_logger);
            return null;
        }

        var file = await mainWindow.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "wav",
            FileTypeChoices = [WavFileType],
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

        return await ResolveConfirmedDestinationAsync(path, result.SelectedFileType, async destination =>
        {
            var dialog = new ConfirmActionDialogView
            {
                DataContext = new ConfirmActionDialogViewModel(
                    _localization.GetString("FilePicker.OverwriteTitle"),
                    _localization.GetString("FilePicker.OverwriteMessage", destination)),
            };
            return await dialog.ShowDialog<bool>(mainWindow);
        });
    }

    public async Task<(string Path, ImageExportFormat Format)?> ResolveConfirmedDestinationAsync(
        string pickedPath, FilePickerFileType? selectedType, Func<string, Task<bool>> confirmOverwrite)
    {
        var destination = ResolveDestination(pickedPath, selectedType);
        if (!string.Equals(pickedPath, destination.Path, StringComparison.Ordinal) && File.Exists(destination.Path))
        {
            Log.ConfirmNormalizedOverwrite(_logger, destination.Path);
            if (!await confirmOverwrite(destination.Path)) return null;
        }
        return destination;
    }

    public async Task<string?> PickSavePngFileAsync(string suggestedFileName)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            Log.NoMainWindow(_logger);
            return null;
        }

        var file = await mainWindow.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "png",
            FileTypeChoices = [PngFileType],
        });

        return file?.TryGetLocalPath();
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

    // "*.so.*" is its own pattern, not covered by "*.so" -- Linux Hamlib sonames are versioned
    // (libhamlib.so.4), a compound suffix a bare "*.so" glob would silently exclude. The built-in
    // FilePickerFileTypes.All escape hatch is always offered alongside this -- an unusual install
    // layout (a renamed file, a non-standard extension) must never leave the user stuck unable to
    // pick anything (auditor plan-review finding).
    private static readonly FilePickerFileType HamlibLibraryFileType = new("Hamlib library files")
    {
        Patterns = ["*.dll", "*.dylib", "*.so", "*.so.*"],
    };

    public async Task<string?> PickHamlibLibraryFileAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            Log.NoMainWindow(_logger);
            return null;
        }

        var files = await mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [HamlibLibraryFileType, FilePickerFileTypes.All],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    // No standard extension to verify against upstream YONIQ install data (yoniq-old/ has no sample
    // .mmv/.MDT file). ui_transition_plan.md step 8 (T2-2): unlike HamlibLibraryFileType above,
    // deliberately does NOT also offer FilePickerFileTypes.All -- this format has no ambiguous
    // real-world extension variance to guard against (a Hamlib .so can legitimately be named almost
    // anything on a given install; a station-ID sound file has exactly one convention, ".mmv"), and
    // the picker choosing an arbitrary non-.mmv file was the single easiest way to reach the
    // unplayable-header failure this step adds real validation for. Typed-path entry (the field
    // itself accepts free text) remains the escape hatch for a genuinely unusual file name.
    // Code-review finding: BOTH cases explicitly listed, not relying on FilePickerFileTypes.All's
    // removal being covered by case-insensitivity -- GTK/portal glob filters on Linux are
    // case-sensitive, and legacy's own convention (a Windows-produced file) is uppercase ".MMV".
    private static readonly FilePickerFileType MmvSoundFileType = new("MMV sound files")
    {
        Patterns = ["*.mmv", "*.MMV"],
    };

    public async Task<string?> PickMmvFileAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            Log.NoMainWindow(_logger);
            return null;
        }

        var files = await mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [MmvSoundFileType],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private static readonly FilePickerFileType TemplateBundleFileType = new("Scanline Studio template bundles")
    {
        Patterns = ["*.sstemplate"],
    };

    public async Task<string?> PickSaveTemplateBundleAsync(string suggestedFileName)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            Log.NoMainWindow(_logger);
            return null;
        }

        var file = await mainWindow.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "sstemplate",
            FileTypeChoices = [TemplateBundleFileType],
        });

        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickOpenTemplateBundleAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            Log.NoMainWindow(_logger);
            return null;
        }

        var files = await mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [TemplateBundleFileType],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFolderAsync(string? suggestedStartDirectory)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            Log.NoMainWindow(_logger);
            return null;
        }

        // Best-effort only -- a missing/inaccessible suggested directory (e.g. never-yet-created)
        // just means the OS picker opens at its own default location instead, same "degrade, don't
        // fail" shape as every other picker here. Auditor-caught (2026-08-26): guard AND try/catch,
        // not just the null check -- an empty/whitespace string (a cleared TextBox, or this method's
        // own caller's pre-load-complete default) reaching TryGetFolderFromPathAsync has
        // undocumented behavior for that input (Avalonia's own XML docs only cover the Uri
        // overload's null-if-missing contract, not the string overload's empty/relative-path
        // behavior), and an uncaught throw here would escape an AsyncRelayCommand and crash the
        // process -- a failure class this app has already been careful to guard against elsewhere.
        IStorageFolder? suggestedStartLocation = null;
        if (!string.IsNullOrWhiteSpace(suggestedStartDirectory))
        {
            try
            {
                suggestedStartLocation = await mainWindow.StorageProvider.TryGetFolderFromPathAsync(suggestedStartDirectory);
            }
            catch (Exception ex)
            {
                Log.SuggestedFolderResolveFailed(_logger, suggestedStartDirectory, ex);
            }
        }

        var folders = await mainWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            SuggestedStartLocation = suggestedStartLocation,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Confirming image overwrite at normalized destination {Path}")]
        public static partial void ConfirmNormalizedOverwrite(ILogger logger, string path);
        // [CallerMemberName] resolves to whichever Pick*Async method called this, NOT the
        // suppressed positional-argument name -- shared by all 3 pickers, so the log line no longer
        // hardcodes "PickImageFileAsync" for an ADIF-picker failure.
        [LoggerMessage(Level = LogLevel.Warning, Message = "{CallerMemberName}: no MainWindow available; returning null (looks like a cancel to the caller)")]
        public static partial void NoMainWindow(ILogger logger, [CallerMemberName] string callerMemberName = "");

        [LoggerMessage(Level = LogLevel.Warning, Message = "PickClipboardImageAsync: failed to read/save the clipboard image")]
        public static partial void ClipboardImageReadFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "PickFolderAsync: resolving the suggested start directory {Directory} failed; opening at the OS default instead")]
        public static partial void SuggestedFolderResolveFailed(ILogger logger, string directory, Exception exception);
    }
}
