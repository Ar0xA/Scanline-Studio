namespace ScanlineStudio.UI.Services;

/// <summary>Which encoder a picked image-export destination resolved to — see
/// <see cref="IFilePickerService.PickSaveImageFileAsync"/>'s own doc comment for why this is
/// returned explicitly rather than left for the caller to sniff from the file extension.</summary>
public enum ImageExportFormat
{
    Png,
    Jpeg,
}

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

    /// <summary>Auditor usability review follow-up (2026-08-18) -- TX Image Editor "+ IMAGE" flyout's
    /// 4th source (clipboard paste, Phase 2's own logged scope cut, now picked back up). Saves
    /// whatever bitmap is on the system clipboard to a fresh temp PNG file and returns ITS path, so
    /// callers can feed it through the SAME <c>IImageFileLoader.LoadOriginalAsync(path)</c> call
    /// <see cref="PickImageFileAsync"/>'s own callers already use -- one image-loading code path
    /// (EXIF orientation included), not a second hand-rolled Bitmap-to-<c>IImageSource</c> pixel
    /// converter. Returns <c>null</c> if there's no image on the clipboard right now (a normal,
    /// silent no-op state, not an error -- same "nothing to add yet" shape as
    /// <c>TxImageEditorPaneViewModel.AddLastRxImage</c>'s own empty-buffer case). Callers own deleting
    /// the returned temp file once they're done reading it.</summary>
    Task<string?> PickClipboardImageAsync();

    /// <summary>Returns the picked ADIF file's local path, or <c>null</c> if the user cancelled.</summary>
    Task<string?> PickAdifFileAsync();

    /// <summary>Prompts for a save location pre-filled with <paramref name="suggestedFileName"/>;
    /// returns the chosen local path, or <c>null</c> if the user cancelled.</summary>
    Task<string?> PickSaveAdifFileAsync(string suggestedFileName);

    /// <summary>Prompts for a save location pre-filled with <paramref name="suggestedFileName"/>,
    /// offering both PNG and JPEG as file-type choices. Returns the chosen local path AND which
    /// format the user actually picked, or <c>null</c> if the user cancelled. The returned path's
    /// extension is normalized to match the returned format on every platform where the
    /// implementation can determine which type the user picked (real-window-confirmed necessary
    /// and working on this app's Linux/GTK backend -- a plain <c>SaveFilePickerAsync</c>'s own
    /// extension-rewrite behavior is platform-split, and GTK's own dialog does NOT rewrite it);
    /// code-review finding, not independently re-verified on Windows/macOS: the resolution is a
    /// reference-equality check against this implementation's own <c>FilePickerFileType</c>
    /// instances, which depends on each platform backend echoing back the same instance it was
    /// given -- see <c>FilePickerService.ResolveDestination</c>'s own doc comment for the bounded,
    /// non-throwing fallback if that assumption ever turns out false on an unverified platform.
    /// Callers must never sniff the returned path's extension independently either way -- the
    /// format value here is the source of truth.</summary>
    Task<(string Path, ImageExportFormat Format)?> PickSaveImageFileAsync(string suggestedFileName);
}
