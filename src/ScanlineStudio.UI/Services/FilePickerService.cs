using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace ScanlineStudio.UI.Services;

public sealed class FilePickerService : IFilePickerService
{
    public async Task<string?> PickImageFileAsync()
    {
        // Fully qualified: bare "Application" resolves to the sibling ScanlineStudio.Application namespace
        // from anywhere under the shared ScanlineStudio root (same gotcha App.axaml.cs hit first).
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            return null;
        }

        var files = await mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
