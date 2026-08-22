using Avalonia;

namespace ScanlineStudio.UI.Views;

/// <summary>Tier C audit finding: extracted from <see cref="MainWindow"/>'s own constructor so the
/// restored-position validation is unit-testable without a live window -- same no-<c>InternalsVisibleTo</c>/
/// public-not-internal precedent as <see cref="TxImageEditorPaneView.ComputeElementResize"/> and its
/// siblings.</summary>
public static class WindowGeometryPolicy
{
    /// <summary>True if <paramref name="position"/> falls within any of <paramref name="screenBounds"/>,
    /// or if <paramref name="screenBounds"/> is empty (meaning "no screen info available yet" --
    /// trusts the caller's own persisted value rather than disabling the whole restore feature, since
    /// <c>Window.Screens.All</c> may not be reliably populated this early in every Avalonia
    /// configuration). Used to reject restoring a window position persisted while on a since-removed
    /// monitor (unplugged second display, a resolution change) -- Windows does not clamp an
    /// off-screen position, so the app would otherwise appear not to start, with no visible way to
    /// reach the Options toggle that would turn this feature off.</summary>
    public static bool ShouldRestorePosition(PixelPoint position, IReadOnlyList<PixelRect> screenBounds)
    {
        if (screenBounds.Count == 0)
        {
            return true;
        }

        foreach (var bounds in screenBounds)
        {
            if (position.X >= bounds.X && position.X < bounds.X + bounds.Width &&
                position.Y >= bounds.Y && position.Y < bounds.Y + bounds.Height)
            {
                return true;
            }
        }

        return false;
    }
}
