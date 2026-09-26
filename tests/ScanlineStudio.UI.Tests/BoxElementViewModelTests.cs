using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>TX editor gap-items plan, item 3 (perspective transform, 2026-09-02) --
/// <see cref="BoxElementViewModel"/>'s own mode-switched X/Y/Width/Height mechanism is IDENTICAL logic
/// to <see cref="ImageElementViewModel"/>'s own (already covered in full by
/// <c>ImageElementViewModelTests</c>, including a mutation-verified check of the construction-time
/// "assign the delegate after PerspectiveEnabled" fix) -- this file covers only what's genuinely
/// DIFFERENT for a box: one representative mode-switch check (guards against a copy-paste divergence
/// between the two nearly-identical implementations), the box-only Effective* chrome-neutralization
/// properties, and <see cref="BoxElementViewModel.Dispose"/> (a NEW capability this feature adds --
/// the class didn't implement <see cref="IDisposable"/> before).</summary>
public sealed class BoxElementViewModelTests
{
    private static BoxElementViewModel MakeElement() => new() { ImageWidth = 100, ImageHeight = 100 };

    // Avalonia's WriteableBitmap has no public IsDisposed -- same technique
    // ImageElementViewModelTests uses: a disposed instance throws ObjectDisposedException from any
    // real operation, here .Lock().
    private static bool IsDisposed(WriteableBitmap bitmap)
    {
        try
        {
            using (bitmap.Lock())
            {
            }

            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    [Fact]
    public void Width_WhenPerspectiveEnabled_ScalesCornerXOffsetsAboutTheBboxCenter()
    {
        var element = MakeElement();
        element.Corner0X = 0.2; element.Corner0Y = 0.2;
        element.Corner1X = 0.4; element.Corner1Y = 0.2;
        element.Corner2X = 0.4; element.Corner2Y = 0.4;
        element.Corner3X = 0.2; element.Corner3Y = 0.4;
        element.PerspectiveEnabled = true;

        element.Width = 0.4;

        Assert.Equal(0.1, element.Corner0X, precision: 10);
        Assert.Equal(0.5, element.Corner1X, precision: 10);
        Assert.Equal(0.5, element.Corner2X, precision: 10);
        Assert.Equal(0.1, element.Corner3X, precision: 10);
    }

    [AvaloniaFact]
    public void EffectiveProperties_WhenPerspectiveEnabled_NeutralizeTheBorderChrome()
    {
        var element = MakeElement();
        element.FillColor = new Rgb24(10, 20, 30);
        element.BorderThickness = 0.05;
        element.Opacity = 0.5;

        // SolidColorBrush doesn't override Equals (reference equality) -- FillBrush/EffectiveBackground
        // are each freshly computed, so compare the underlying Color, not the brush instances.
        var effectiveBackgroundColor = Assert.IsType<SolidColorBrush>(element.EffectiveBackground).Color;
        var fillBrushColor = Assert.IsType<SolidColorBrush>(element.FillBrush).Color;
        Assert.Equal(fillBrushColor, effectiveBackgroundColor);
        Assert.Equal(element.CanvasBorderThicknessPixels, element.EffectiveBorderThicknessPixels);
        Assert.Equal(0.5, element.EffectiveOpacity);

        element.PerspectiveEnabled = true;

        Assert.Equal(Brushes.Transparent, element.EffectiveBackground);
        Assert.Equal(0, element.EffectiveBorderThicknessPixels);
        Assert.Equal(1, element.EffectiveOpacity);
    }

    [AvaloniaFact]
    public void Dispose_DisposesTheWarpedBitmap_DeferredViaDispatcherPost()
    {
        var element = MakeElement();
        element.PerspectiveEnabled = true;
        element.RenderWarpedPreview = (w, h) => BgraPixelBuffer.FromSolidColor(new Rgb24(1, 2, 3), w, h);
        Dispatcher.UIThread.RunJobs();
        var warped = element.WarpedCanvasBitmap;
        Assert.NotNull(warped);

        element.Dispose();

        Assert.False(IsDisposed(warped!), "must not be disposed before the deferred post runs");
        Dispatcher.UIThread.RunJobs();
        Assert.True(IsDisposed(warped!));
    }
}
