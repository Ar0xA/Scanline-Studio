using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>Standalone unit tests for <see cref="LineElementViewModel"/>'s own derived
/// X/Y/Width/Height contract (the "endpoints are truth, box is derived" architecture -- see
/// project_line_element_plan memory for the full plan-review history) -- warrants its own test file,
/// unlike <see cref="ScanlineStudio.UI.ViewModels.BoxElementViewModel"/>/<c>OverlayElementViewModel</c>
/// (thin property wrappers with no interesting standalone logic, tested only through
/// <c>TxImageEditorPaneViewModelTests</c>), because the degenerate-extent setter rule and the
/// notification cascade are real, independently-testable logic.</summary>
public sealed class LineElementViewModelTests
{
    [Fact]
    public void X_Get_ReturnsEndpointMidpoint()
    {
        var line = new LineElementViewModel { X1 = 0.2, X2 = 0.6 };

        Assert.Equal(0.4, line.X);
    }

    [Fact]
    public void X_Set_TranslatesBothEndpointsByTheDelta()
    {
        var line = new LineElementViewModel { X1 = 0.2, X2 = 0.6 };

        line.X = 0.5; // was 0.4, delta = +0.1

        Assert.Equal(0.3, line.X1);
        Assert.Equal(0.7, line.X2);
    }

    [Fact]
    public void Y_Set_TranslatesBothEndpointsByTheDelta()
    {
        var line = new LineElementViewModel { Y1 = 0.3, Y2 = 0.3 };

        line.Y = 0.5; // was 0.3, delta = +0.2

        Assert.Equal(0.5, line.Y1);
        Assert.Equal(0.5, line.Y2);
    }

    [Fact]
    public void Width_Get_IsAbsoluteExtent_NotSignedDifference()
    {
        // A right-to-left line (X1 > X2) must still read a POSITIVE Width -- a signed getter is the
        // exact bug plan-review round 3 caught (an identity write flips the line end-for-end).
        var line = new LineElementViewModel { X1 = 0.7, X2 = 0.3 };

        Assert.Equal(0.4, line.Width, precision: 10);
    }

    [Fact]
    public void Width_SetToTheSameValueItAlreadyReads_IsAnIdentityWrite_DoesNotFlipDirection()
    {
        var line = new LineElementViewModel { X1 = 0.7, X2 = 0.3 }; // right-to-left, dx = -0.4

        line.Width = line.Width; // read 0.4, write 0.4 back

        Assert.Equal(0.7, line.X1, precision: 10);
        Assert.Equal(0.3, line.X2, precision: 10);
    }

    [Fact]
    public void Width_Set_ScalesAboutCenter_PreservingOrientation()
    {
        var line = new LineElementViewModel { X1 = 0.4, X2 = 0.6 }; // center 0.5, left-to-right

        line.Width = 0.4;

        Assert.Equal(0.3, line.X1);
        Assert.Equal(0.7, line.X2);
    }

    [Fact]
    public void Width_Set_OnARightToLeftLine_PreservesRightToLeftOrientation()
    {
        var line = new LineElementViewModel { X1 = 0.6, X2 = 0.4 }; // center 0.5, right-to-left

        line.Width = 0.4;

        Assert.Equal(0.7, line.X1);
        Assert.Equal(0.3, line.X2);
    }

    [Fact]
    public void Width_SetOnAVerticalLine_DefaultsToPositiveLeftToRightOrientation()
    {
        // X1 == X2 (a vertical line) has no existing orientation to preserve -- the plan's own
        // stated decision: default to positive (left-to-right).
        var line = new LineElementViewModel { X1 = 0.5, X2 = 0.5 };

        line.Width = 0.4;

        Assert.Equal(0.3, line.X1);
        Assert.Equal(0.7, line.X2);
    }

    [Fact]
    public void Width_SetToZero_CollapsesBothEndpointsToTheCenter()
    {
        var line = new LineElementViewModel { X1 = 0.3, X2 = 0.7 };

        line.Width = 0;

        Assert.Equal(0.5, line.X1);
        Assert.Equal(0.5, line.X2);
    }

    [Fact]
    public void Width_SetToNegative_ClampsToItsAbsoluteValue()
    {
        var line = new LineElementViewModel { X1 = 0.4, X2 = 0.6 };

        line.Width = -0.4;

        Assert.Equal(0.3, line.X1);
        Assert.Equal(0.7, line.X2);
    }

    [Theory]
    [MemberData(nameof(NonFiniteValues))]
    public void Width_SetToNonFiniteValue_IsIgnored(double nonFinite)
    {
        var line = new LineElementViewModel { X1 = 0.4, X2 = 0.6 };

        line.Width = nonFinite;

        Assert.Equal(0.4, line.X1);
        Assert.Equal(0.6, line.X2);
    }

    [Theory]
    [MemberData(nameof(NonFiniteValues))]
    public void X_SetToNonFiniteValue_IsIgnored(double nonFinite)
    {
        var line = new LineElementViewModel { X1 = 0.4, X2 = 0.6 };

        line.X = nonFinite;

        Assert.Equal(0.4, line.X1);
        Assert.Equal(0.6, line.X2);
    }

    public static TheoryData<double> NonFiniteValues() => new()
    {
        double.NaN,
        double.PositiveInfinity,
        double.NegativeInfinity,
    };

    [Fact]
    public void Height_MirrorsWidth_OnTheYAxis()
    {
        var line = new LineElementViewModel { Y1 = 0.6, Y2 = 0.4 }; // right-to-left (top-to-bottom reversed)

        Assert.Equal(0.2, line.Height, precision: 10);

        line.Height = 0.2; // identity write

        Assert.Equal(0.6, line.Y1, precision: 10);
        Assert.Equal(0.4, line.Y2, precision: 10);
    }

    [Fact]
    public void LeftTopCanvasWidthHeightPixels_MatchTheDerivedBoundingBox()
    {
        var line = new LineElementViewModel { X1 = 0.3, Y1 = 0.4, X2 = 0.7, Y2 = 0.6, ImageWidth = 200, ImageHeight = 100 };

        // X=0.5, Y=0.5, Width=0.4, Height=0.2
        Assert.Equal((0.5 - 0.2) * 200, line.LeftPixels, precision: 10);
        Assert.Equal((0.5 - 0.1) * 100, line.TopPixels, precision: 10);
        Assert.Equal(0.4 * 200, line.CanvasWidthPixels, precision: 10);
        Assert.Equal(0.2 * 100, line.CanvasHeightPixels, precision: 10);
    }

    [Fact]
    public void CanvasStartEndPoint_AreContainerRelative_NotAbsoluteCanvasPixels()
    {
        var line = new LineElementViewModel { X1 = 0.3, Y1 = 0.5, X2 = 0.7, Y2 = 0.5, ImageWidth = 200, ImageHeight = 100 };

        // Absolute: X1=60px, LeftPixels = (0.5-0.2)*200 = 60 -- so container-relative start = 0.
        Assert.Equal(0, line.CanvasStartPoint.X, precision: 6);
        Assert.Equal((0.7 * 200) - line.LeftPixels, line.CanvasEndPoint.X, precision: 6);
    }

    [Fact]
    public void SettingX1_RaisesPropertyChangedForX_Width_AndPixelDerivedProperties()
    {
        var line = new LineElementViewModel { X1 = 0.3, X2 = 0.7, ImageWidth = 100, ImageHeight = 100 };
        var raised = new List<string?>();
        line.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        line.X1 = 0.2;

        Assert.Contains(nameof(LineElementViewModel.X), raised);
        Assert.Contains(nameof(LineElementViewModel.Width), raised);
        Assert.Contains(nameof(LineElementViewModel.LeftPixels), raised);
        Assert.Contains(nameof(LineElementViewModel.CanvasWidthPixels), raised);
        Assert.Contains(nameof(LineElementViewModel.CanvasStartPoint), raised);
    }

    [Fact]
    public void SettingAnEndpoint_PushesGeometryUndoSnapshot()
    {
        var pushed = 0;
        var line = new LineElementViewModel { PushUndoSnapshotForGeometryChange = () => pushed++ };

        line.X1 = 0.5;

        Assert.Equal(1, pushed);
    }

    [Fact]
    public void SettingStrokeColor_PushesStyleUndoSnapshot_NotGeometry()
    {
        var geometryPushes = 0;
        var stylePushes = 0;
        var line = new LineElementViewModel
        {
            PushUndoSnapshotForGeometryChange = () => geometryPushes++,
            PushUndoSnapshotForStyleChange = () => stylePushes++,
        };

        line.StrokeColor = new Rgb24(1, 2, 3);

        Assert.Equal(0, geometryPushes);
        Assert.Equal(1, stylePushes);
    }

    [Fact]
    public void StrokeThicknessPx_RoundTripsThroughTargetModeHeightPx()
    {
        var line = new LineElementViewModel { TargetModeHeightPx = 200, StrokeThickness = 0.01 };

        Assert.Equal(2, line.StrokeThicknessPx);

        line.StrokeThicknessPx = 4;

        Assert.Equal(0.02, line.StrokeThickness);
    }
}
