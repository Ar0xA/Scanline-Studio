using Avalonia;
using ScanlineStudio.UI.Views;

namespace ScanlineStudio.UI.Tests;

/// <summary>Tier C audit finding: pins the bounds-validation fix extracted from MainWindow's own
/// constructor -- a restored window position/size persisted while on a since-removed monitor used to
/// be applied with zero validation, restoring the app to invisible off-screen coordinates with no
/// recoverable way to reach the Options toggle that would turn the feature off.</summary>
public sealed class WindowGeometryPolicyTests
{
    private static readonly PixelRect PrimaryScreen = new(0, 0, 1920, 1080);
    private static readonly PixelRect SecondScreen = new(1920, 0, 1920, 1080);

    [Fact]
    public void PositionInsidePrimaryScreen_ReturnsTrue()
    {
        Assert.True(WindowGeometryPolicy.ShouldRestorePosition(new PixelPoint(100, 100), [PrimaryScreen]));
    }

    [Fact]
    public void PositionInsideSecondScreen_ReturnsTrue()
    {
        Assert.True(WindowGeometryPolicy.ShouldRestorePosition(new PixelPoint(2500, 300), [PrimaryScreen, SecondScreen]));
    }

    [Fact]
    public void PositionOnNoKnownScreen_ReturnsFalse()
    {
        // The exact bug scenario: the second monitor from when this was saved is gone, and this
        // coordinate falls in the now-empty space it used to occupy.
        Assert.False(WindowGeometryPolicy.ShouldRestorePosition(new PixelPoint(2500, 300), [PrimaryScreen]));
    }

    [Fact]
    public void NegativePositionOnNoKnownScreen_ReturnsFalse()
    {
        Assert.False(WindowGeometryPolicy.ShouldRestorePosition(new PixelPoint(-3000, -3000), [PrimaryScreen]));
    }

    [Fact]
    public void EmptyScreenList_ReturnsTrue_TrustsThePersistedValueRatherThanDisablingTheFeature()
    {
        // Window.Screens.All may not be reliably populated this early in every Avalonia
        // configuration -- an empty list must not be treated as "no screen has this position" (which
        // would silently disable restoring for everyone, every launch).
        Assert.True(WindowGeometryPolicy.ShouldRestorePosition(new PixelPoint(2500, 300), []));
    }

    [Fact]
    public void PositionExactlyAtScreenOrigin_ReturnsTrue()
    {
        Assert.True(WindowGeometryPolicy.ShouldRestorePosition(new PixelPoint(0, 0), [PrimaryScreen]));
    }

    [Fact]
    public void PositionExactlyAtScreenFarEdge_ReturnsFalse()
    {
        // Bounds are exclusive on the far edge (X + Width is one pixel past the last valid column).
        Assert.False(WindowGeometryPolicy.ShouldRestorePosition(new PixelPoint(1920, 100), [PrimaryScreen]));
    }

    /// <summary>User-reported bug (2026-09-03): the exact scenario -- a size saved while the
    /// taskbar was its DEFAULT ~40px height no longer fits once the operator resizes their real
    /// Windows taskbar taller (here, a working area 120px shorter than the full 1080px monitor,
    /// simulating a much taller taskbar). The saved Top-left point (0,0) still validates fine via
    /// ShouldRestorePosition (that check never looked at Height at all), but the saved Height
    /// (1080, the FULL old monitor height) now overshoots the new working area's bottom by 120px --
    /// exactly reproducing "the bottom of the window is below the taskbar."</summary>
    [Fact]
    public void ClampToWorkArea_HeightTallerThanCurrentWorkingArea_ShrinksHeightToFit()
    {
        var workingArea = new PixelRect(0, 0, 1920, 960); // 120px shorter than PrimaryScreen's 1080 full height
        var requested = new PixelRect(0, 0, 1920, 1080);

        var clamped = WindowGeometryPolicy.ClampToWorkArea(requested, workingArea);

        Assert.Equal(new PixelRect(0, 0, 1920, 960), clamped);
    }

    [Fact]
    public void ClampToWorkArea_PositionAboveWorkingArea_MovesDownToFit()
    {
        // The other half of the user-reported symptom: a saved Top slightly above the working
        // area's own top edge (e.g. left over from an earlier bad maximize) -- "the top of the
        // window is outside of the screen."
        var workingArea = new PixelRect(0, 40, 1920, 1000); // taskbar reserves the top 40px in this scenario
        var requested = new PixelRect(0, 0, 800, 600);

        var clamped = WindowGeometryPolicy.ClampToWorkArea(requested, workingArea);

        Assert.Equal(new PixelRect(0, 40, 800, 600), clamped);
    }

    [Fact]
    public void ClampToWorkArea_PositionBelowAndRightOfWorkingArea_MovesUpAndLeftToFit()
    {
        var workingArea = new PixelRect(0, 0, 1920, 1040);
        var requested = new PixelRect(1800, 1000, 400, 300);

        var clamped = WindowGeometryPolicy.ClampToWorkArea(requested, workingArea);

        // Size unchanged (400x300 already fits within 1920x1040); position pulled back so the
        // clamped rectangle's far edges land exactly on the working area's own far edges.
        Assert.Equal(new PixelRect(1520, 740, 400, 300), clamped);
    }

    [Fact]
    public void ClampToWorkArea_AlreadyFitsEntirely_ReturnsUnchanged()
    {
        var workingArea = new PixelRect(0, 0, 1920, 1040);
        var requested = new PixelRect(100, 100, 800, 600);

        var clamped = WindowGeometryPolicy.ClampToWorkArea(requested, workingArea);

        Assert.Equal(requested, clamped);
    }

    [Fact]
    public void ClampToWorkArea_LargerThanWorkingAreaOnBothAxes_ShrinksToFillWorkingAreaExactly()
    {
        var workingArea = new PixelRect(0, 0, 1920, 960);
        var requested = new PixelRect(-50, -30, 2200, 1200);

        var clamped = WindowGeometryPolicy.ClampToWorkArea(requested, workingArea);

        Assert.Equal(workingArea, clamped);
    }

    [Fact]
    public void ClampToWorkArea_SecondScreenWorkingArea_ClampsRelativeToItsOwnOffset()
    {
        // Working areas aren't always anchored at (0,0) -- a second monitor to the right of the
        // primary, per this file's own SecondScreen fixture. The clamp must respect the working
        // area's own X/Y offset, not assume it starts at the origin.
        var workingArea = new PixelRect(1920, 0, 1920, 1040);
        var requested = new PixelRect(1920, 0, 1920, 1080);

        var clamped = WindowGeometryPolicy.ClampToWorkArea(requested, workingArea);

        Assert.Equal(new PixelRect(1920, 0, 1920, 1040), clamped);
    }
}
