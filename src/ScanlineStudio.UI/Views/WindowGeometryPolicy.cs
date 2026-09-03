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

    /// <summary>User-reported bug (2026-09-03): a restored window's saved Width/Height was never
    /// re-validated against the CURRENT work area at all -- only the top-left corner point was
    /// checked (<see cref="ShouldRestorePosition"/> above), so a size that fit a PREVIOUS session's
    /// taskbar/monitor/DPI restored verbatim even once it no longer fits (e.g. a taller taskbar than
    /// when it was saved). Deliberately NOT a fixed-number fix for today's specific bad value -- the
    /// actual usable work area can change for many reasons (different monitor, different taskbar
    /// size, a DPI/resolution change), so this re-derives the fit against whatever
    /// <paramref name="workingArea"/> the CALLER passes in fresh at restore time, every launch, not
    /// a one-time correction.
    ///
    /// <paramref name="requested"/>/<paramref name="workingArea"/> must both be in the SAME unit
    /// (physical pixels -- the caller is responsible for converting a DIP-valued Width/Height by the
    /// target screen's own <c>Scaling</c> factor before calling this, and converting the clamped
    /// result back to DIPs after; this function does no DPI math itself, kept a pure geometric clamp
    /// so it stays trivially unit-testable with plain integer rectangles).
    /// <paramref name="workingArea"/> should be the screen's actual usable area (Avalonia's
    /// <c>Screen.WorkingArea</c>, which EXCLUDES the taskbar) -- not <c>Screen.Bounds</c> (the full
    /// monitor, which <see cref="ShouldRestorePosition"/> itself intentionally still checks against,
    /// since that check only needs "is this point on some real monitor at all," not "does the whole
    /// window fit around the taskbar").
    ///
    /// Size is clamped FIRST (never larger than the working area itself), THEN position is clamped
    /// so the already-size-clamped rectangle's edges stay within the working area -- this order
    /// means an oversized requested size shrinks to fit rather than a too-far-right/-down position
    /// pushing the rectangle further off-screen the other way.</summary>
    public static PixelRect ClampToWorkArea(PixelRect requested, PixelRect workingArea)
    {
        var width = Math.Min(requested.Width, workingArea.Width);
        var height = Math.Min(requested.Height, workingArea.Height);
        var x = Math.Clamp(requested.X, workingArea.X, workingArea.X + workingArea.Width - width);
        var y = Math.Clamp(requested.Y, workingArea.Y, workingArea.Y + workingArea.Height - height);
        return new PixelRect(x, y, width, height);
    }

    /// <summary>User-reported bug (2026-09-03), root cause #3: <see cref="MainWindow"/>'s own
    /// fallback default size (no usable persisted geometry -- see that class's own doc comment on
    /// its <c>DefaultWidth</c>/<c>DefaultHeight</c> constants for why this is reachable on an
    /// entirely ordinary install, not an edge case) needs a starting POSITION before
    /// <see cref="ClampToWorkArea"/> can run at all. Centers <paramref name="widthDip"/>/
    /// <paramref name="heightDip"/> (DIPs, e.g. <c>Window.Width</c>/<c>Height</c>'s own unit) within
    /// <paramref name="workingArea"/> (physical pixels, same <c>Screen.WorkingArea</c> contract as
    /// <see cref="ClampToWorkArea"/>'s own <paramref name="workingArea"/>) via <paramref name="scaling"/>
    /// (the target screen's own <c>Scaling</c> factor) -- same physical-pixels-throughout convention
    /// as the rest of this class, DIP arguments converted internally rather than left to the
    /// caller. Deliberately allowed to return a NEGATIVE X/Y (when the requested size is larger
    /// than the working area itself -- centering an oversized rectangle mathematically requires it)
    /// -- callers must NOT reject that as "off-screen" the way <see cref="ShouldRestorePosition"/>
    /// would (that check exists to reject a STALE persisted position that might belong to a
    /// since-removed monitor, not to gate a position this function just computed live against the
    /// CURRENT screen's own bounds -- it is by construction on-screen, just centered around an
    /// oversized rectangle). Pass the result straight to <see cref="ClampToWorkArea"/> instead,
    /// which correctly pulls a negative position back onto the working area as part of its own
    /// clamp.</summary>
    public static PixelPoint CenterInWorkArea(PixelRect workingArea, double widthDip, double heightDip, double scaling)
    {
        var x = workingArea.X + ((workingArea.Width - (widthDip * scaling)) / 2);
        var y = workingArea.Y + ((workingArea.Height - (heightDip * scaling)) / 2);
        return new PixelPoint((int)x, (int)y);
    }
}
