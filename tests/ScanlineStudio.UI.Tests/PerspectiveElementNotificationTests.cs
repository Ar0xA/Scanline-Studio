using System.ComponentModel;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>Pins the exact PropertyChanged multiset (names and counts) each Box/Image geometry write raises,
/// so moving the shared perspective code into a base class cannot add or drop a notification.</summary>
public sealed class PerspectiveElementNotificationTests
{
    private static readonly string[] GeometryCascade =
    [
        "X", "Y", "Width", "Height", "LeftPixels", "TopPixels", "CanvasWidthPixels", "CanvasHeightPixels",
        "CanvasCorner0Point", "CanvasCorner1Point", "CanvasCorner2Point", "CanvasCorner3Point",
    ];

    private static readonly string[] BoxFillCascade = ["FillBrush", "EffectiveBackground"];

    private static readonly string[] CornerPoints = ["CanvasCorner0Point", "CanvasCorner1Point", "CanvasCorner2Point", "CanvasCorner3Point"];

    private static BoxElementViewModel MakeBox() => new() { ImageWidth = 100, ImageHeight = 100 };

    private static ImageElementViewModel MakeImage() =>
        new(new ArrayImageSource(2, 2, new Rgb24[4]))
        {
            Origin = new TxImageEditorPaneViewModel.ImageSourceOrigin(TxImageEditorPaneViewModel.ImageSourceKind.File, null),
            ImageWidth = 100,
            ImageHeight = 100,
        };

    private static List<string> Record(INotifyPropertyChanged element, Action act)
    {
        var raised = new List<string>();
        void Handler(object? sender, PropertyChangedEventArgs e)
        {
            Assert.Same(element, sender);
            raised.Add(e.PropertyName ?? "<null>");
        }

        element.PropertyChanged += Handler;
        try
        {
            act();
        }
        finally
        {
            element.PropertyChanged -= Handler;
        }

        Dispatcher.UIThread.RunJobs();
        return raised;
    }

    private static void AssertMultiset(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        static string Describe(IEnumerable<string> names) =>
            string.Join(", ", names.GroupBy(n => n, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}x{g.Count()}"));

        Assert.Equal(Describe(expected), Describe(actual));
    }

    [AvaloniaFact]
    public void Box_NaturalX_RaisesTheGeometryAndFillCascadeOnce()
    {
        var box = MakeBox();
        AssertMultiset([.. GeometryCascade, .. BoxFillCascade, "NaturalX"], Record(box, () => box.NaturalX = 0.7));
    }

    [AvaloniaFact]
    public void Image_NaturalX_RaisesTheGeometryCascadeOnce()
    {
        var image = MakeImage();
        AssertMultiset([.. GeometryCascade, "NaturalX"], Record(image, () => image.NaturalX = 0.7));
    }

    [AvaloniaFact]
    public void Box_CornerWrite_RaisesTheGeometryAndFillCascadeOnce()
    {
        var box = MakeBox();
        AssertMultiset([.. GeometryCascade, .. BoxFillCascade, "Corner2Y"], Record(box, () => box.Corner2Y = 0.15));
    }

    [AvaloniaFact]
    public void Image_CornerWrite_RaisesTheGeometryCascadeOnce()
    {
        var image = MakeImage();
        AssertMultiset([.. GeometryCascade, "Corner2Y"], Record(image, () => image.Corner2Y = 0.15));
    }

    [AvaloniaFact]
    public void Box_PerspectiveOnThenOff_RaisesTheExactMultiset()
    {
        var box = MakeBox();
        string[] on =
        [
            .. GeometryCascade, .. BoxFillCascade, "ShowResizeHandles", "ShowPerspectiveCornerHandles",
            "EffectiveBackground", "EffectiveBorderThicknessPixels", "EffectiveOpacity", "CanRotate", "PerspectiveEnabled",
        ];

        AssertMultiset(on, Record(box, () => box.PerspectiveEnabled = true));
        AssertMultiset([.. on, "WarpedCanvasBitmap"], Record(box, () => box.PerspectiveEnabled = false));
    }

    [AvaloniaFact]
    public void Image_PerspectiveOnThenOff_RaisesTheExactMultiset()
    {
        var image = MakeImage();
        string[] on = [.. GeometryCascade, "ShowResizeHandles", "ShowPerspectiveCornerHandles", "CanRotate", "PerspectiveEnabled"];

        AssertMultiset(on, Record(image, () => image.PerspectiveEnabled = true));
        AssertMultiset([.. on, "WarpedCanvasBitmap"], Record(image, () => image.PerspectiveEnabled = false));
    }

    [AvaloniaFact]
    public void Box_ImageWidthAndHeight_RaiseTheExactMultiset()
    {
        var box = MakeBox();
        AssertMultiset(
            ["LeftPixels", "CanvasWidthPixels", .. BoxFillCascade, .. CornerPoints, "ImageWidth"],
            Record(box, () => box.ImageWidth = 200));
        AssertMultiset(
            [
                "TopPixels", "CanvasHeightPixels", .. BoxFillCascade, "CanvasBorderThicknessPixels", "EffectiveBorderThicknessPixels",
                "CanvasCornerRadiusPixels", .. CornerPoints, "ImageHeight",
            ],
            Record(box, () => box.ImageHeight = 200));
    }

    [AvaloniaFact]
    public void Image_ImageWidthAndHeight_RaiseTheExactMultiset()
    {
        var image = MakeImage();
        AssertMultiset(["LeftPixels", "CanvasWidthPixels", .. CornerPoints, "ImageWidth"], Record(image, () => image.ImageWidth = 200));
        AssertMultiset(["TopPixels", "CanvasHeightPixels", .. CornerPoints, "ImageHeight"], Record(image, () => image.ImageHeight = 200));
    }

    [AvaloniaFact]
    public void Box_Locked_RaisesTheExactMultiset()
    {
        var box = MakeBox();
        AssertMultiset(["ShowResizeHandles", "ShowPerspectiveCornerHandles", "CanRotate", "Locked"], Record(box, () => box.Locked = true));
    }

    [AvaloniaFact]
    public void Image_Locked_RaisesTheExactMultiset()
    {
        var image = MakeImage();
        AssertMultiset(
            ["BlocksHitTesting", "ShowResizeHandles", "ShowPerspectiveCornerHandles", "CanRotate", "Locked"],
            Record(image, () => image.Locked = true));
    }

    [AvaloniaFact]
    public void Rotate_OnAWarpedImageElement_RotatesTheCornersDirectlyNotTheDerivedBbox()
    {
        var vm = new TxImageEditorPaneViewModel(
            new ArrayImageSource(4, 4, new Rgb24[16]),
            new SstvModeDefinition("small", "Small", 0, 4, 4, ColorEncoding.RgbSequential, []),
            new FakeTransmitImagePreparer(), new MacroTextResolver(), new OperatorSettings(), new FakeRadioSessionService(),
            new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(),
            new FakeReceivedImageBuffer { Current = new ArrayImageSource(2, 2, new Rgb24[4]) }, new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(),
            new ReadyRackViewModel(new FakeTemplateStore(), new FakeSettingsStore(), new FakeLocalizationService(), new FakeFilePickerService(), NullLogger<ReadyRackViewModel>.Instance));
        vm.AddLastRxImageCommand.Execute(null);
        var image = Assert.IsType<ImageElementViewModel>(Assert.Single(vm.OverlayElements));
        image.Corner0X = 0.1;
        image.Corner0Y = 0.2;
        image.Corner1X = 0.6;
        image.Corner1Y = 0.25;
        image.Corner2X = 0.55;
        image.Corner2Y = 0.7;
        image.Corner3X = 0.05;
        image.Corner3Y = 0.65;
        image.PerspectiveEnabled = true;

        vm.RotateCommand.Execute(null);

        Assert.True(image.PerspectiveEnabled);
        Assert.Equal(1 - 0.2, image.Corner0X, 1e-9);
        Assert.Equal(0.1, image.Corner0Y, 1e-9);
        Assert.Equal(1 - 0.25, image.Corner1X, 1e-9);
        Assert.Equal(0.6, image.Corner1Y, 1e-9);
        Assert.Equal(1 - 0.7, image.Corner2X, 1e-9);
        Assert.Equal(0.55, image.Corner2Y, 1e-9);
        Assert.Equal(1 - 0.65, image.Corner3X, 1e-9);
        Assert.Equal(0.05, image.Corner3Y, 1e-9);
    }
}
