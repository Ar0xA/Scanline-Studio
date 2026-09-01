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

    /// <summary>TX history plan (2026-09-01) -- the "Save" action on a
    /// <c>TxControlsPaneViewModel.SentFrames</c> entry. PNG-only (no JPEG choice offered at all,
    /// unlike <see cref="PickSaveImageFileAsync"/>'s two-format dialog): this app has no JPEG
    /// encoder reachable from an in-memory <c>IImageSource</c> (only <c>IImageSourceWriter.WritePngAsync</c>),
    /// so silently forcing a JPEG pick's extension to <c>.png</c> would violate
    /// <see cref="PickSaveImageFileAsync"/>'s own documented "the format value is the source of
    /// truth" contract instead of just not offering the choice. Same single-format shape as
    /// <see cref="PickSaveWavFileAsync"/>/<see cref="PickSaveAdifFileAsync"/>. Returns the chosen
    /// local path, or <see langword="null"/> if the user cancelled.</summary>
    Task<string?> PickSavePngFileAsync(string suggestedFileName);

    /// <summary>Piece C2 (RX tab Re-decode port): returns the picked WAV file's local path, or
    /// <c>null</c> if the user cancelled.</summary>
    Task<string?> PickOpenWavFileAsync();

    /// <summary>Piece C1 (RX tab Re-decode port): prompts for a save location pre-filled with
    /// <paramref name="suggestedFileName"/>; returns the chosen local path, or <c>null</c> if the
    /// user cancelled.</summary>
    Task<string?> PickSaveWavFileAsync(string suggestedFileName);

    /// <summary>Options dialog's Radio/CAT tab, "linked Hamlib" section -- lets the user browse
    /// directly to a Hamlib shared library file (<c>.dll</c>/<c>.dylib</c>/<c>.so</c>/<c>.so.N</c>),
    /// rather than typing a path by hand. Returns the picked file's local path, or <c>null</c> if the
    /// user cancelled.</summary>
    Task<string?> PickHamlibLibraryFileAsync();

    /// <summary>Stub survey Tier 2 -- the Storage settings dialog's own "Browse..." button for the
    /// RX images folder. The only FOLDER (not file) picker on this interface. Returns the picked
    /// directory's local path, or <c>null</c> if the user cancelled. <paramref
    /// name="suggestedStartDirectory"/> pre-seeds the dialog at the currently-configured directory
    /// (or the resolved default) rather than always opening wherever the OS last left it.</summary>
    Task<string?> PickFolderAsync(string? suggestedStartDirectory);

    /// <summary>Options dialog's Identification tab, sound-file station ID row -- lets the user
    /// browse to a legacy <c>.MMV</c> sound-file (a custom raw-PCM format, no standard extension to
    /// verify against upstream YONIQ install data). Returns the picked file's local path, or
    /// <c>null</c> if the user cancelled.</summary>
    Task<string?> PickMmvFileAsync();

    /// <summary>ui_transition_plan.md step 13 -- Templates panel's Export action. Prompts for a save
    /// location pre-filled with <paramref name="suggestedFileName"/>, typed to <c>.sstemplate</c>
    /// (Scanline Studio's own native template bundle format -- a zipped template folder, see
    /// <c>ITemplateStore.ExportAsync</c>). Returns the chosen local path, or <c>null</c> if the user
    /// cancelled.</summary>
    Task<string?> PickSaveTemplateBundleAsync(string suggestedFileName);

    /// <summary>ui_transition_plan.md step 13 -- Templates panel's Import action. Typed to
    /// <c>.sstemplate</c> only (step 14's legacy <c>.mtm</c> importer, not yet built, will extend
    /// this or add its own picker once that format's scope is decided -- deliberately not
    /// anticipated here). Returns the picked file's local path, or <c>null</c> if the user
    /// cancelled.</summary>
    Task<string?> PickOpenTemplateBundleAsync();
}
