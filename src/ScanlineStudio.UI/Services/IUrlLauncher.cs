namespace ScanlineStudio.UI.Services;

/// <summary>Wraps opening a URL in the OS's default browser behind a plain interface, same
/// "view-model depends on an interface, not a static platform call" reasoning as
/// <see cref="IFilePickerService"/> -- lets <c>MainViewModel</c>'s "Open on GitHub" command stay
/// unit-testable without actually launching a browser.</summary>
public interface IUrlLauncher
{
    void Open(string url);
}
