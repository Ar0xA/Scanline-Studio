namespace ScanlineStudio.UI.Services;

/// <summary>Wraps handing a URL or file-system path to the OS shell (default browser for a URL,
/// default file manager for a folder) behind a plain interface, same "view-model depends on an
/// interface, not a static platform call" reasoning as <see cref="IFilePickerService"/> -- lets
/// <c>MainViewModel</c>'s "Open on GitHub"/"Open application log" commands stay unit-testable
/// without actually launching anything.</summary>
public interface IUrlLauncher
{
    void Open(string urlOrPath);
}
