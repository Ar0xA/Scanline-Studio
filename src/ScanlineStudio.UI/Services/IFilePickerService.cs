namespace ScanlineStudio.UI.Services;

/// <summary>Wraps Avalonia's <c>IStorageProvider</c> file-picker dialog behind a plain interface, so
/// pane view-models can depend on it via constructor injection instead of a view's code-behind
/// owning the click handler (spec/09-ui.md: views carry no logic beyond <c>InitializeComponent()</c>
/// and purely visual concerns). Lives entirely in <c>ScanlineStudio.UI</c> — both this interface and its
/// implementation — since it's a UI-shell concern, not something `ScanlineStudio.Application`/`Core.*` need to
/// know about.</summary>
public interface IFilePickerService
{
    /// <summary>Returns the picked file's local path, or <c>null</c> if the user cancelled.</summary>
    Task<string?> PickImageFileAsync();

    /// <summary>Returns the picked ADIF file's local path, or <c>null</c> if the user cancelled.</summary>
    Task<string?> PickAdifFileAsync();

    /// <summary>Prompts for a save location pre-filled with <paramref name="suggestedFileName"/>;
    /// returns the chosen local path, or <c>null</c> if the user cancelled.</summary>
    Task<string?> PickSaveAdifFileAsync(string suggestedFileName);
}
