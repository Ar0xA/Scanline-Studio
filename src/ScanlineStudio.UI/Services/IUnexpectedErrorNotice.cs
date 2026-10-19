namespace ScanlineStudio.UI.Services;

/// <summary>Tells the user that an unexpected error was caught and the app kept running. Called on the UI
/// thread; the returned task completes when the notice is closed (or at once if it could not be shown).</summary>
public interface IUnexpectedErrorNotice
{
    Task ShowAsync();
}
