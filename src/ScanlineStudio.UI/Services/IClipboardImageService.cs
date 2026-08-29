using Avalonia.Media.Imaging;

namespace ScanlineStudio.UI.Services;

/// <summary>ui_transition_plan.md step 3 (T1-5): copies a rendered image to the OS clipboard, for
/// the full-size image viewer's Copy action. Kept as its own tiny service interface (mirroring
/// <see cref="IFilePickerService"/>'s own established shape for a view-model-reachable platform
/// operation) so <c>ImageViewerWindowViewModel</c> stays unit-testable without a real
/// <c>TopLevel</c>/clipboard.</summary>
public interface IClipboardImageService
{
    /// <summary>Returns <see langword="false"/> on any failure (no main window, no clipboard on
    /// this backend, or the copy itself throwing) rather than throwing -- a failed clipboard copy
    /// is a normal, recoverable outcome for the caller to surface, not an exceptional one.</summary>
    Task<bool> CopyImageAsync(Bitmap bitmap);
}
