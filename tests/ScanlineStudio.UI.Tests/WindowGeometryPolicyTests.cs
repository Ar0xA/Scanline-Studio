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
}
