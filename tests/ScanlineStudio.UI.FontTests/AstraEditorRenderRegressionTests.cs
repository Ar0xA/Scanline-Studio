using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.UI.Controls;
using ScanlineStudio.UI.Imaging;
using ScanlineStudio.UI.ViewModels;
using ScanlineStudio.UI.Views;
using SixLabors.ImageSharp.PixelFormats;
using static ScanlineStudio.UI.FontTests.RealWindowTestSupport;
using Rgb24 = ScanlineStudio.Abstractions.Imaging.Rgb24;

namespace ScanlineStudio.UI.FontTests;

public sealed class AstraEditorRenderRegressionTests
{
    [AvaloniaTheory]
    [InlineData(200, 100)]
    [InlineData(100, 200)]
    public void RadialGradient_PreviewMatchesCircularFinalGeometry(int width, int height)
    {
        var box = new BoxElementViewModel
        {
            ImageWidth = width, ImageHeight = height, Width = 1, Height = 1,
            GradientEnabled = true, GradientKind = TextGradientKind.Radial,
            GradientStartColor = new Rgb24(255, 0, 0), GradientEndColor = new Rgb24(0, 0, 255),
        };
        using var preview = Render(new Border { Background = box.FillBrush }, width, height);
        var element = new TemplateBoxElement(new NormalizedRect(0, 0, 1, 1), 0, default, null, 0, 1, 0,
            new TextGradient(TextGradientKind.Radial, [new GradientColorStop(0, new Rgb24(255, 0, 0)), new GradientColorStop(1, new Rgb24(0, 0, 255))]));
        var final = new TransmitImagePreparer(FontPath).ApplyTemplate(CreateSource(width, height), new TemplateDocument(null, [element]));
        var x = width > height ? width / 2 : 1;
        var y = width > height ? 1 : height / 2;
        AssertColorClose(PixelAt(final, x, y), preview[x, y], 8);
    }

    [AvaloniaTheory]
    [InlineData(1.0, 0.0)]
    [InlineData(2.0, 3.0)]
    [InlineData(0.5, 1.0)]
    public void BitmapPattern_PreviewKeepsOutputPixelScaleAndPhase(double pixelScale, double outputOrigin)
    {
        const int outputSize = 24;
        var displaySize = (int)(outputSize * pixelScale);
        var box = new BoxElementViewModel
        {
            ImageWidth = displaySize, ImageHeight = displaySize, Width = 1, Height = 1,
            GradientEnabled = true, GradientKind = TextGradientKind.BitmapPattern,
            GradientStartColor = new Rgb24(255, 0, 0), GradientEndColor = new Rgb24(0, 0, 255),
            PreviewMetrics = new ElementPreviewMetrics(displaySize, new Size(pixelScale, pixelScale), new Point(outputOrigin, outputOrigin)),
        };
        using var preview = Render(new Border { Background = box.FillBrush }, displaySize, displaySize);
        var final = new TransmitImagePreparer(FontPath).ApplyTemplate(CreateSource(32, 32), new TemplateDocument(null,
            [new TemplateBoxElement(new NormalizedRect(outputOrigin / 32, outputOrigin / 32, outputSize / 32.0, outputSize / 32.0), 0, default, null, 0, 1, 0,
                new TextGradient(TextGradientKind.BitmapPattern, [new GradientColorStop(0, new Rgb24(255, 0, 0)), new GradientColorStop(1, new Rgb24(0, 0, 255))]))]));
        // At subpixel scale antialiasing averages neighboring cells; compare the period instead.
        if (pixelScale < 1)
        {
            for (var y = 2; y < displaySize - 2; y++)
                for (var x = 2; x < displaySize - 2; x++)
                    Assert.Equal(preview[x, y], preview[x + 2, y]);
        }
        else
        {
            for (var y = 2; y < outputSize - 2; y++)
                for (var x = 2; x < outputSize - 2; x++)
                    AssertColorClose(PixelAt(final, (int)outputOrigin + x, (int)outputOrigin + y),
                        preview[(int)((x + .5) * pixelScale), (int)((y + .5) * pixelScale)], 8);
        }
    }

    [AvaloniaFact]
    public void ShortTextGradient_UsesUnusedElementBoundsInsteadOfGlyphBounds()
    {
        const int width = 240, height = 100;
        var text = new OverlayElementViewModel
        {
            ImageWidth = width, ImageHeight = height, Width = 1, Height = 1,
            GradientEnabled = true, GradientKind = TextGradientKind.Horizontal,
            GradientStartColor = new Rgb24(255, 0, 0), GradientEndColor = new Rgb24(0, 0, 255),
        };
        var control = new StrokedTextBlock
        {
            Text = "J", FontSize = 40, FontFamily = new FontFamily("avares://ScanlineStudio.UI/Assets/Fonts/DejaVuSansMono#DejaVu Sans Mono"),
            Fill = text.ForegroundBrush, FillWidth = width, FillHeight = height,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        using var preview = Render(new Grid { Background = Brushes.Black, Children = { control } }, width, height);
        var final = new TransmitImagePreparer(FontPath).ApplyTemplate(CreateSource(width, height), new TemplateDocument(null,
            [new TemplateTextElement(new NormalizedRect(0, 0, 1, 1), 0, "J", new FontSpec("DejaVu Sans Mono", .4), default,
                Gradient: new TextGradient(TextGradientKind.Horizontal,
                    [new GradientColorStop(0, new Rgb24(255, 0, 0)), new GradientColorStop(1, new Rgb24(0, 0, 255))]))]));
        var previewRed = new List<byte>();
        var finalRed = new List<byte>();
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var p = preview[x, y];
                var f = PixelAt(final, x, y);
                if (p.R + p.B > 245) previewRed.Add(p.R);
                if (f.R + f.B > 245) finalRed.Add(f.R);
            }
        Assert.NotEmpty(previewRed);
        Assert.NotEmpty(finalRed);
        Assert.InRange(previewRed.Max() - previewRed.Min(), 1, 40);
        Assert.InRange(Math.Abs(previewRed.Average(v => v) - finalRed.Average(v => v)), 0, 12);
    }

    [AvaloniaFact]
    public void OutlinedText_AcuteGlyphVertex_NoMiterSpikeAboveTheGlyph()
    {
        // User-reported 2026-09-19: StrokedTextBlock (the TX editor's live canvas outline renderer)
        // used a plain Avalonia Pen, whose default MITER join (limit 10) shoots the outer corner out
        // to up to 10x the stroke width at any glyph vertex sharper than ~11.5 degrees -- exactly
        // "A"'s apex. The real render pipeline this control mirrors (TransmitImagePreparer.DrawGlyphs)
        // never showed this, because SixLabors.ImageSharp.Drawing's Pens.Solid defaults to
        // JointStyle.Square, a bounded corner style -- fixed here by giving this control's own Pen an
        // explicit Bevel join (Avalonia's closest bounded equivalent). "A" at a large size with a
        // thick relative outline (20% of font size, matching the reported ratio) into a canvas with
        // real headroom above the glyph's own cap height -- a spike would land squarely in that
        // headroom band; a correctly-joined outline never reaches it.
        const int width = 200, height = 200;
        var control = new StrokedTextBlock
        {
            Text = "A", FontSize = 100,
            FontFamily = new FontFamily("avares://ScanlineStudio.UI/Assets/Fonts/DejaVuSansMono#DejaVu Sans Mono"),
            Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 20,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        using var image = Render(new Grid { Background = Brushes.Gray, Children = { control } }, width, height);

        for (var y = 0; y < height / 5; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var p = image[x, y];
                Assert.True(p.R > 100 || p.G > 100 || p.B > 100, $"Unexpected dark pixel (miter spike?) at ({x},{y}): ({p.R},{p.G},{p.B}).");
            }
        }
    }

    [AvaloniaFact]
    public void RotatedTextPattern_UsesLocalPhaseWhenElementMoves()
    {
        using var vm = CreateEditor(CreateSource(320, 240), TestMode);
        vm.AddOverlayElementCommand.Execute(null);
        var text = Assert.IsType<OverlayElementViewModel>(vm.SelectedOverlayElement);
        text.Width = 120.0 / 320;
        text.Height = 80.0 / 240;
        text.X = .5;
        text.Y = .5;
        text.RotationDegrees = 90;
        text.GradientEnabled = true;
        text.GradientKind = TextGradientKind.BitmapPattern;
        PumpDispatcher();

        static Grid TextPreview(OverlayElementViewModel element) => new()
        {
            Background = Brushes.Black,
            Children =
            {
                new StrokedTextBlock
                {
                    Text = "J", FontSize = 40, FontFamily = new FontFamily("avares://ScanlineStudio.UI/Assets/Fonts/DejaVuSansMono#DejaVu Sans Mono"),
                    Fill = element.ForegroundBrush, FillWidth = 120, FillHeight = 80,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        using var before = Render(TextPreview(text), 120, 80);
        text.X += 3.0 / 320;
        text.Y += 5.0 / 240;
        PumpDispatcher();
        using var after = Render(TextPreview(text), 120, 80);
        for (var y = 0; y < 80; y++)
            for (var x = 0; x < 120; x++)
                Assert.Equal(before[x, y], after[x, y]);

        // The final renderer likewise preserves its local pattern when translating rotated text.
        var element = new TemplateTextElement(new NormalizedRect(100.0 / 320, 80.0 / 240, 120.0 / 320, 80.0 / 240), 0,
            "J", new FontSpec("DejaVu Sans Mono", .16), default, RotationDegrees: 90,
            Gradient: new TextGradient(TextGradientKind.BitmapPattern,
                [new GradientColorStop(0, new Rgb24(255, 0, 0)), new GradientColorStop(1, new Rgb24(0, 0, 255))]));
        var preparer = new TransmitImagePreparer(FontPath);
        var first = preparer.ApplyTemplate(CreateSource(320, 240), new TemplateDocument(null, [element]));
        var moved = preparer.ApplyTemplate(CreateSource(320, 240), new TemplateDocument(null,
            [element with { Bounds = element.Bounds with { X = 103.0 / 320, Y = 85.0 / 240 } }]));
        for (var y = 50; y < 190; y++)
            for (var x = 110; x < 220; x++)
                Assert.Equal(PixelAt(first, x, y), PixelAt(moved, x + 3, y + 5));
    }

    [AvaloniaFact]
    public void RealBoxBinding_CropAndZoomRefreshBorderRadiusAndTextLineEffects()
    {
        var (window, vm, _) = BuildRealWindow(CreateSource(320, 240));
        try
        {
            vm.AddBoxElementCommand.Execute(null);
            var box = Assert.IsType<BoxElementViewModel>(vm.SelectedOverlayElement);
            box.BorderColor = new Rgb24(255, 0, 0);
            box.BorderThickness = .025;
            box.CornerRadius = .05;
            vm.AddOverlayElementCommand.Execute(null);
            var text = Assert.IsType<OverlayElementViewModel>(vm.SelectedOverlayElement);
            text.ShadowColor = new Rgb24(255, 0, 0);
            text.StackColor = new Rgb24(255, 0, 0);
            text.ShadowOffsetX = text.StackStepX = .025;
            vm.AddLineElementCommand.Execute(null);
            var line = Assert.IsType<LineElementViewModel>(vm.SelectedOverlayElement);
            line.StrokeThickness = .025;
            PumpDispatcher();
            var border = window.GetVisualDescendants().OfType<Border>().Single(b => ReferenceEquals(b.DataContext, box) && b.CornerRadius.TopLeft > 0);
            Assert.Equal(6, border.BorderThickness.Top, 6);
            vm.CropRect = new NormalizedRect(.25, .25, .5, .5);
            PumpDispatcher();
            Assert.Equal(3, border.BorderThickness.Top, 6);
            Assert.Equal(6, border.CornerRadius.TopLeft, 6);
            Assert.Equal(3, line.CanvasStrokeThicknessPixels, 6);
            Assert.Equal(3, text.CanvasStackStepXPixels, 6);
            Assert.Equal(3, Assert.IsType<TranslateTransform>(text.ShadowRenderTransform).X, 6);
            vm.ZoomFactor = 2;
            PumpDispatcher();
            Assert.Equal(6, border.BorderThickness.Top, 6);
            Assert.Equal(12, border.CornerRadius.TopLeft, 6);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(true, 3.75)]
    [InlineData(false, 3.0)]
    public void RealBoxBinding_NonmatchingCropAspect_UsesContentScale(bool preserveAspect, double expectedThickness)
    {
        var (window, vm, _) = BuildRealWindow(CreateSource(400, 240));
        try
        {
            vm.LockAspectToMode = false;
            vm.PreserveAspect = preserveAspect;
            vm.CropRect = new NormalizedRect(.25, .25, .5, .5);
            vm.AddBoxElementCommand.Execute(null);
            var box = Assert.IsType<BoxElementViewModel>(vm.SelectedOverlayElement);
            box.BorderColor = new Rgb24(255, 0, 0);
            box.BorderThickness = .025;
            box.CornerRadius = .05;
            PumpDispatcher();

            var border = window.GetVisualDescendants().OfType<Border>().Single(b => ReferenceEquals(b.DataContext, box) && b.CornerRadius.TopLeft > 0);
            Assert.Equal(expectedThickness, border.BorderThickness.Top, 6);
            Assert.Equal(2 * expectedThickness, border.CornerRadius.TopLeft, 6);
            vm.AddOverlayElementCommand.Execute(null);
            var text = Assert.IsType<OverlayElementViewModel>(vm.SelectedOverlayElement);
            text.ShadowColor = text.StackColor = new Rgb24(255, 0, 0);
            text.ShadowOffsetX = text.StackStepX = .025;
            // The 200px-wide crop maps into 320 output pixels in both modes; horizontal
            // offsets therefore occupy 6 * 200/320 = 3.75 canvas pixels even when Y differs.
            Assert.Equal(3.75, Assert.IsType<TranslateTransform>(text.ShadowRenderTransform).X, 6);
            Assert.Equal(3.75, text.CanvasStackStepXPixels, 6);
            using var rendered = Render(new Border { Background = box.FillBrush, BorderBrush = Brushes.Red,
                BorderThickness = border.BorderThickness }, 80, 40);
            Assert.Equal((byte)255, rendered[40, 1].R);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void CancelPlacement_LeavesEarlierAddOrCompletedDragIntact(bool earlierDrag, bool movePlacement)
    {
        var (window, vm, canvas) = BuildRealWindow(CreateSource(320, 240));
        try
        {
            vm.AddOverlayElementCommand.Execute(null);
            var text = Assert.IsType<OverlayElementViewModel>(vm.SelectedOverlayElement);
            PumpDispatcher();
            if (earlierDrag)
            {
                var originalX = text.X;
                var start = canvas.TranslatePoint(new Point(text.X * vm.CanvasDisplayWidth, text.Y * vm.CanvasDisplayHeight), window)!.Value;
                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(start + new Vector(20, 10));
                window.MouseUp(start + new Vector(20, 10), MouseButton.Left);
                PumpDispatcher();
                Assert.NotEqual(originalX, text.X);
            }
            var priorX = text.X;
            var priorY = text.Y;
            var view = Assert.IsType<TxImageEditorPaneView>(window.Content);
            var button = view.FindControl<Button>("AddBoxButton")!;
            var buttonPoint = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(buttonPoint, MouseButton.Left);
            window.MouseUp(buttonPoint, MouseButton.Left);
            var press = canvas.TranslatePoint(new Point(25, 25), window)!.Value;
            window.MouseDown(press, MouseButton.Left);
            if (movePlacement) window.MouseMove(press + new Vector(40, 30));
            if (movePlacement) Assert.NotNull(vm.PlacementPreviewRect);
            var dragMode = typeof(TxImageEditorPaneView).GetField("_dragMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view);
            Assert.Equal("Placing", dragMode!.ToString());
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.MouseUp(press, MouseButton.Left);
            PumpDispatcher();

            var retained = Assert.IsType<OverlayElementViewModel>(Assert.Single(vm.OverlayElements));
            Assert.Equal(priorX, retained.X);
            Assert.Equal(priorY, retained.Y);
            Assert.Null(vm.PlacementPreviewRect);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PerspectivePreview_OneAxisResolutionCeilingPreservesAspect()
    {
        using var box = new BoxElementViewModel
        {
            ImageWidth = 4096, ImageHeight = 512, Width = 1, Height = 1,
            Corner0X = 0, Corner0Y = 0, Corner1X = 1, Corner1Y = 0, Corner2X = 1, Corner2Y = 1, Corner3X = 0, Corner3Y = 1,
            PerspectiveEnabled = true,
        };
        (int Width, int Height) actual = default;
        box.RenderWarpedPreview = (w, h) =>
        {
            actual = (w, h);
            return BgraPixelBuffer.FromSolidColor(default, w, h);
        };
        PumpDispatcher();
        Assert.Equal((2048, 256), actual);
    }

    private static SixLabors.ImageSharp.Image<Rgba32> Render(Control control, int width, int height)
    {
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(control);
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        stream.Position = 0;
        return SixLabors.ImageSharp.Image.Load<Rgba32>(stream);
    }

    private static Rgb24 PixelAt(IImageSource image, int x, int y) => image.GetScanline(y)[x];

    private static void AssertColorClose(Rgb24 expected, Rgba32 actual, int tolerance)
    {
        Assert.InRange(Math.Abs(expected.R - actual.R), 0, tolerance);
        Assert.InRange(Math.Abs(expected.G - actual.G), 0, tolerance);
        Assert.InRange(Math.Abs(expected.B - actual.B), 0, tolerance);
    }
}
