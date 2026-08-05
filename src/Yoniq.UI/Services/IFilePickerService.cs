namespace Yoniq.UI.Services;

/// <summary>Wraps Avalonia's <c>IStorageProvider</c> file-picker dialog behind a plain interface, so
/// pane view-models can depend on it via constructor injection instead of a view's code-behind
/// owning the click handler (spec/09-ui.md: views carry no logic beyond <c>InitializeComponent()</c>
/// and purely visual concerns). Lives entirely in <c>Yoniq.UI</c> — both this interface and its
/// implementation — since it's a UI-shell concern, not something `Yoniq.Application`/`Core.*` need to
/// know about.</summary>
public interface IFilePickerService
{
    /// <summary>Returns the picked file's local path, or <c>null</c> if the user cancelled.</summary>
    Task<string?> PickImageFileAsync();
}
