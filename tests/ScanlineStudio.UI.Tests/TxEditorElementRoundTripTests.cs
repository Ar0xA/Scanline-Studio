using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.UI.ViewModels;
using static ScanlineStudio.UI.ViewModels.TxImageEditorPaneViewModel;

namespace ScanlineStudio.UI.Tests;

/// <summary>Guard tests for the TX-editor element snapshot round-trip. Every assertion pins today's behaviour,
/// so a refactor of the element field mapping must keep all of these green.</summary>
public sealed partial class TxEditorElementRoundTripTests
{
    // 4x4 source, 4x4 mode, full crop: the crop projection is identity, so canvas offsets equal Document offsets.
    private static readonly SstvModeDefinition Mode = new(
        Id: "roundtrip",
        DisplayName: "Round Trip",
        VisCode: 0,
        ImageWidth: 4,
        ImageHeight: 4,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments: []);

    private const double CloneOffset = 0.02;

    private const double Tolerance = 1e-12;

    // ---------------------------------------------------------------------------------------------
    // Property inventory
    // ---------------------------------------------------------------------------------------------

    private static readonly Type[] ElementTypes =
    [
        typeof(OverlayElementViewModel), typeof(BoxElementViewModel), typeof(ImageElementViewModel), typeof(LineElementViewModel),
    ];

    /// <summary>Settable properties that are deliberately NOT element state. A null scope applies to every
    /// element type that has the property. Wiring (command/delegate-typed) is excluded by type, see
    /// <see cref="IsWiring"/>.</summary>
    private static readonly (Type? Scope, string Name, string Reason)[] AllowList =
    [
        (null, "IsSelected", "UI-transient: selection state, never snapshotted"),
        (typeof(OverlayElementViewModel), "IsEditingText", "UI-transient: in-place text edit mode"),
        (typeof(OverlayElementViewModel), "ShowRotateFlyoutContent", "UI-transient: flyout visibility"),
        (typeof(BoxElementViewModel), "ShowRotateFlyoutContent", "UI-transient: flyout visibility"),
        (typeof(LineElementViewModel), "PendingRotationDelta", "UI-transient: rotate-slider scratch value"),
        (null, "ImageWidth", "Canvas-derived: pane sets it from CanvasDisplayWidth"),
        (null, "ImageHeight", "Canvas-derived: pane sets it from CanvasDisplayHeight"),
        (typeof(OverlayElementViewModel), "CanvasFontSize", "Canvas-derived: recomputed by the pane"),
        (typeof(OverlayElementViewModel), "CanvasStrokeThicknessPixels", "Canvas-derived: recomputed by the pane"),
        (typeof(OverlayElementViewModel), "PreviewMetrics", "Canvas-derived: RefreshElementPreviewMetrics"),
        (typeof(BoxElementViewModel), "PreviewMetrics", "Canvas-derived: RefreshElementPreviewMetrics"),
        (typeof(LineElementViewModel), "PreviewMetrics", "Canvas-derived: RefreshElementPreviewMetrics"),
        (null, "TargetModeHeightPx", "Wiring: constant from the pane's target mode"),
        (typeof(OverlayElementViewModel), "FontSizePx", "Alias: px proxy of FontSizeRelative"),
        (typeof(LineElementViewModel), "StrokeThicknessPx", "Alias: px proxy of StrokeThickness"),
        (typeof(BoxElementViewModel), "BorderThicknessPx", "Alias: px proxy of BorderThickness"),
        (typeof(BoxElementViewModel), "CornerRadiusPx", "Alias: px proxy of CornerRadius"),
        (typeof(OverlayElementViewModel), "HasStroke", "Alias: StrokeColor is not null"),
        (typeof(OverlayElementViewModel), "HasShadow", "Alias: ShadowColor is not null"),
        (typeof(OverlayElementViewModel), "HasStack", "Alias: StackColor is not null"),
        (typeof(BoxElementViewModel), "HasBorder", "Alias: BorderColor is not null"),
        (typeof(OverlayElementViewModel), "StrokeColorForPicker", "Alias: picker view of StrokeColor"),
        (typeof(OverlayElementViewModel), "ShadowColorForPicker", "Alias: picker view of ShadowColor"),
        (typeof(OverlayElementViewModel), "StackColorForPicker", "Alias: picker view of StackColor"),
        (typeof(BoxElementViewModel), "BorderColorForPicker", "Alias: picker view of BorderColor"),
        (typeof(BoxElementViewModel), "NaturalX", "Alias: backing field of X while perspective is off"),
        (typeof(BoxElementViewModel), "NaturalY", "Alias: backing field of Y while perspective is off"),
        (typeof(BoxElementViewModel), "NaturalWidth", "Alias: backing field of Width while perspective is off"),
        (typeof(BoxElementViewModel), "NaturalHeight", "Alias: backing field of Height while perspective is off"),
        (typeof(ImageElementViewModel), "NaturalX", "Alias: backing field of X while perspective is off"),
        (typeof(ImageElementViewModel), "NaturalY", "Alias: backing field of Y while perspective is off"),
        (typeof(ImageElementViewModel), "NaturalWidth", "Alias: backing field of Width while perspective is off"),
        (typeof(ImageElementViewModel), "NaturalHeight", "Alias: backing field of Height while perspective is off"),
        (typeof(LineElementViewModel), "X", "Alias: derived from X1/X2, endpoints are the truth"),
        (typeof(LineElementViewModel), "Y", "Alias: derived from Y1/Y2, endpoints are the truth"),
        (typeof(LineElementViewModel), "Width", "Alias: derived from X1/X2, endpoints are the truth"),
        (typeof(LineElementViewModel), "Height", "Alias: derived from Y1/Y2, endpoints are the truth"),
    ];

    /// <summary>Per-type non-default value table: every inventoried property maps to the snapshot field
    /// the fixtures seed it from. The fixtures below give each entry a non-default value in at least one
    /// variant.</summary>
    private static readonly Dictionary<Type, Dictionary<string, Func<RawElementSnapshot, object?>>> ValueTable = new()
    {
        [typeof(OverlayElementViewModel)] = new(StringComparer.Ordinal)
        {
            ["X"] = s => s.X,
            ["Y"] = s => s.Y,
            ["Width"] = s => s.Width,
            ["Height"] = s => s.Height,
            ["Z"] = s => s.Z,
            ["Locked"] = s => s.Locked,
            ["Text"] = s => ((RawTextElementSnapshot)s).Text,
            ["FontSizeRelative"] = s => ((RawTextElementSnapshot)s).FontSizeRelative,
            ["Color"] = s => ((RawTextElementSnapshot)s).Color,
            ["FontFamily"] = s => ((RawTextElementSnapshot)s).FontFamily,
            ["StrokeColor"] = s => ((RawTextElementSnapshot)s).StrokeColor,
            ["StrokeThickness"] = s => ((RawTextElementSnapshot)s).StrokeThickness,
            ["ShadowColor"] = s => ((RawTextElementSnapshot)s).ShadowColor,
            ["ShadowOffsetX"] = s => ((RawTextElementSnapshot)s).ShadowOffsetX,
            ["ShadowOffsetY"] = s => ((RawTextElementSnapshot)s).ShadowOffsetY,
            ["RotationDegrees"] = s => ((RawTextElementSnapshot)s).RotationDegrees,
            ["GradientEnabled"] = s => ((RawTextElementSnapshot)s).GradientEnabled,
            ["GradientKind"] = s => ((RawTextElementSnapshot)s).GradientKind,
            ["GradientStartColor"] = s => ((RawTextElementSnapshot)s).GradientStartColor,
            ["GradientEndColor"] = s => ((RawTextElementSnapshot)s).GradientEndColor,
            ["Bold"] = s => ((RawTextElementSnapshot)s).Bold,
            ["Italic"] = s => ((RawTextElementSnapshot)s).Italic,
            ["StackColor"] = s => ((RawTextElementSnapshot)s).StackColor,
            ["StackStepX"] = s => ((RawTextElementSnapshot)s).StackStepX,
            ["StackStepY"] = s => ((RawTextElementSnapshot)s).StackStepY,
            ["BitmapFillEnabled"] = s => ((RawTextElementSnapshot)s).BitmapFillEnabled,
            ["BitmapFillSource"] = s => ((RawTextElementSnapshot)s).BitmapFillSource,
            ["GrowToFillEnabled"] = s => ((RawTextElementSnapshot)s).GrowToFillEnabled,
        },
        [typeof(BoxElementViewModel)] = new(StringComparer.Ordinal)
        {
            ["X"] = s => s.X,
            ["Y"] = s => s.Y,
            ["Width"] = s => s.Width,
            ["Height"] = s => s.Height,
            ["Z"] = s => s.Z,
            ["Locked"] = s => s.Locked,
            ["FillColor"] = s => ((RawBoxElementSnapshot)s).FillColor,
            ["BorderColor"] = s => ((RawBoxElementSnapshot)s).BorderColor,
            ["BorderThickness"] = s => ((RawBoxElementSnapshot)s).BorderThickness,
            ["Opacity"] = s => ((RawBoxElementSnapshot)s).Opacity,
            ["CornerRadius"] = s => ((RawBoxElementSnapshot)s).CornerRadius,
            ["GradientEnabled"] = s => ((RawBoxElementSnapshot)s).GradientEnabled,
            ["GradientKind"] = s => ((RawBoxElementSnapshot)s).GradientKind,
            ["GradientStartColor"] = s => ((RawBoxElementSnapshot)s).GradientStartColor,
            ["GradientEndColor"] = s => ((RawBoxElementSnapshot)s).GradientEndColor,
            ["PerspectiveEnabled"] = s => ((RawBoxElementSnapshot)s).PerspectiveEnabled,
            ["Corner0X"] = s => ((RawBoxElementSnapshot)s).Corner0X,
            ["Corner0Y"] = s => ((RawBoxElementSnapshot)s).Corner0Y,
            ["Corner1X"] = s => ((RawBoxElementSnapshot)s).Corner1X,
            ["Corner1Y"] = s => ((RawBoxElementSnapshot)s).Corner1Y,
            ["Corner2X"] = s => ((RawBoxElementSnapshot)s).Corner2X,
            ["Corner2Y"] = s => ((RawBoxElementSnapshot)s).Corner2Y,
            ["Corner3X"] = s => ((RawBoxElementSnapshot)s).Corner3X,
            ["Corner3Y"] = s => ((RawBoxElementSnapshot)s).Corner3Y,
            ["FillEnabled"] = s => ((RawBoxElementSnapshot)s).FillEnabled,
            ["RotationDegrees"] = s => ((RawBoxElementSnapshot)s).RotationDegrees,
        },
        [typeof(ImageElementViewModel)] = new(StringComparer.Ordinal)
        {
            ["X"] = s => s.X,
            ["Y"] = s => s.Y,
            ["Width"] = s => s.Width,
            ["Height"] = s => s.Height,
            ["Z"] = s => s.Z,
            ["Locked"] = s => s.Locked,
            ["Source"] = s => ((RawImageElementSnapshot)s).Source,
            ["Fit"] = s => ((RawImageElementSnapshot)s).Fit,
            ["Origin"] = s => ((RawImageElementSnapshot)s).Origin,
            ["IsBackground"] = s => ((RawImageElementSnapshot)s).IsBackground,
            ["NaturalPixelWidth"] = s => ((RawImageElementSnapshot)s).NaturalPixelWidth,
            ["NaturalPixelHeight"] = s => ((RawImageElementSnapshot)s).NaturalPixelHeight,
            ["PerspectiveEnabled"] = s => ((RawImageElementSnapshot)s).PerspectiveEnabled,
            ["Corner0X"] = s => ((RawImageElementSnapshot)s).Corner0X,
            ["Corner0Y"] = s => ((RawImageElementSnapshot)s).Corner0Y,
            ["Corner1X"] = s => ((RawImageElementSnapshot)s).Corner1X,
            ["Corner1Y"] = s => ((RawImageElementSnapshot)s).Corner1Y,
            ["Corner2X"] = s => ((RawImageElementSnapshot)s).Corner2X,
            ["Corner2Y"] = s => ((RawImageElementSnapshot)s).Corner2Y,
            ["Corner3X"] = s => ((RawImageElementSnapshot)s).Corner3X,
            ["Corner3Y"] = s => ((RawImageElementSnapshot)s).Corner3Y,
            ["RotationDegrees"] = s => ((RawImageElementSnapshot)s).RotationDegrees,
        },
        [typeof(LineElementViewModel)] = new(StringComparer.Ordinal)
        {
            ["Z"] = s => s.Z,
            ["Locked"] = s => s.Locked,
            ["X1"] = s => ((RawLineElementSnapshot)s).X1,
            ["Y1"] = s => ((RawLineElementSnapshot)s).Y1,
            ["X2"] = s => ((RawLineElementSnapshot)s).X2,
            ["Y2"] = s => ((RawLineElementSnapshot)s).Y2,
            ["StrokeColor"] = s => ((RawLineElementSnapshot)s).StrokeColor,
            ["StrokeThickness"] = s => ((RawLineElementSnapshot)s).StrokeThickness,
            ["Opacity"] = s => ((RawLineElementSnapshot)s).Opacity,
        },
    };

    /// <summary>Paste-style classification: these are copied by Paste Style; every other inventoried
    /// property of the type must stay unchanged.</summary>
    private static readonly Dictionary<Type, HashSet<string>> StyleProperties = new()
    {
        [typeof(OverlayElementViewModel)] = new(StringComparer.Ordinal)
        {
            "FontFamily", "FontSizeRelative", "Color", "StrokeColor", "StrokeThickness", "ShadowColor", "ShadowOffsetX",
            "ShadowOffsetY", "RotationDegrees", "GradientEnabled", "GradientKind", "GradientStartColor", "GradientEndColor",
            "Bold", "Italic", "StackColor", "StackStepX", "StackStepY", "BitmapFillSource", "BitmapFillEnabled",
        },
        [typeof(BoxElementViewModel)] = new(StringComparer.Ordinal)
        {
            "FillColor", "FillEnabled", "BorderColor", "BorderThickness", "Opacity", "CornerRadius", "GradientEnabled",
            "GradientKind", "GradientStartColor", "GradientEndColor", "RotationDegrees",
        },
        [typeof(LineElementViewModel)] = new(StringComparer.Ordinal) { "StrokeColor", "StrokeThickness", "Opacity" },
    };

    private static readonly Dictionary<Type, HashSet<string>> NonStyleProperties = new()
    {
        [typeof(OverlayElementViewModel)] = new(StringComparer.Ordinal)
        {
            "X", "Y", "Width", "Height", "Z", "Locked", "Text", "GrowToFillEnabled",
        },
        [typeof(BoxElementViewModel)] = new(StringComparer.Ordinal)
        {
            "X", "Y", "Width", "Height", "Z", "Locked", "PerspectiveEnabled",
            "Corner0X", "Corner0Y", "Corner1X", "Corner1Y", "Corner2X", "Corner2Y", "Corner3X", "Corner3Y",
        },
        [typeof(LineElementViewModel)] = new(StringComparer.Ordinal) { "Z", "Locked", "X1", "Y1", "X2", "Y2" },
    };

    private static bool IsWiring(PropertyInfo property) =>
        typeof(ICommand).IsAssignableFrom(property.PropertyType) || typeof(Delegate).IsAssignableFrom(property.PropertyType);

    private static bool IsAllowListed(Type type, string name) =>
        AllowList.Any(entry => (entry.Scope is null || entry.Scope == type) && entry.Name == name);

    private static IEnumerable<PropertyInfo> SettableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.SetMethod is { IsPublic: true });

    private static IReadOnlyList<PropertyInfo> InventoriedProperties(Type type) =>
        SettableProperties(type)
            .Where(p => !IsWiring(p) && !IsAllowListed(type, p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

    private static Dictionary<string, object?> ReadInventoried(ITemplateElementViewModel element) =>
        InventoriedProperties(element.GetType()).ToDictionary(p => p.Name, p => p.GetValue(element), StringComparer.Ordinal);

    [Fact]
    public void Inventory_EverySettableElementProperty_IsInTheValueTableOrTheAllowList()
    {
        var failures = new List<string>();
        foreach (var type in ElementTypes)
        {
            var table = ValueTable[type];
            foreach (var property in SettableProperties(type))
            {
                if (IsWiring(property) || IsAllowListed(type, property.Name))
                {
                    continue;
                }

                if (!table.ContainsKey(property.Name))
                {
                    failures.Add($"{type.Name}.{property.Name}: settable, but in neither the value table nor the allow-list");
                }
            }

            foreach (var name in table.Keys)
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property?.SetMethod is not { IsPublic: true } || IsWiring(property) || IsAllowListed(type, name))
                {
                    failures.Add($"{type.Name}.{name}: value-table entry is not an inventoried property");
                }
            }
        }

        foreach (var (scope, name, reason) in AllowList)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
            var scopes = scope is null ? ElementTypes : [scope];
            if (!scopes.Any(t => t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.SetMethod is { IsPublic: true }))
            {
                failures.Add($"allow-list entry {scope?.Name ?? "*"}.{name}: no such settable property");
            }
        }

        AssertNoFailures(failures);
    }

    [Fact]
    public void Inventory_PasteStyleClassification_CoversEveryInventoriedPropertyExactlyOnce()
    {
        var failures = new List<string>();
        foreach (var type in new[] { typeof(OverlayElementViewModel), typeof(BoxElementViewModel), typeof(LineElementViewModel) })
        {
            var inventory = InventoriedProperties(type).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            var style = StyleProperties[type];
            var nonStyle = NonStyleProperties[type];
            foreach (var name in inventory)
            {
                if (style.Contains(name) == nonStyle.Contains(name))
                {
                    failures.Add($"{type.Name}.{name}: must be classified as exactly one of style / non-style");
                }
            }

            foreach (var name in style.Concat(nonStyle).Where(n => !inventory.Contains(n)))
            {
                failures.Add($"{type.Name}.{name}: classified but not inventoried");
            }
        }

        AssertNoFailures(failures);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    private sealed record Fixtures(
        IReadOnlyList<RawElementSnapshot> Elements,
        IImageSource TextFillPicture,
        IImageSource RotatedImageSource,
        IImageSource WarpedImageSource);

    private static (double X, double Y, double Width, double Height) QuadBounds(
        double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3)
    {
        var minX = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3));
        var maxX = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
        var minY = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
        var maxY = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));
        return (minX + ((maxX - minX) / 2), minY + ((maxY - minY) / 2), maxX - minX, maxY - minY);
    }

    /// <summary>Eight elements, Z 0..7, ordered by Z. Two variants per exclusive pair: text picture-fill vs
    /// gradient, box/image rotation vs perspective. Two lines so Paste Style has a distinct line target.</summary>
    private static Fixtures BuildFixtures()
    {
        var fillPicture = new ArrayImageSource(3, 2, new Rgb24[6]);
        var rotatedImage = new ArrayImageSource(5, 3, new Rgb24[15]);
        var warpedImage = new ArrayImageSource(3, 5, new Rgb24[15]);
        var boxQuad = QuadBounds(0.30, 0.30, 0.60, 0.32, 0.62, 0.60, 0.28, 0.58);
        var imageQuad = QuadBounds(0.50, 0.50, 0.80, 0.52, 0.82, 0.80, 0.48, 0.78);

        RawElementSnapshot[] elements =
        [
            new RawTextElementSnapshot(
                X: 0.31, Y: 0.42, Width: 0.33, Height: 0.21, Z: 0, Locked: true,
                Text: "CQ DE %m", FontSizeRelative: 0.12, Color: new Rgb24(10, 20, 30),
                FontFamily: "Barlow", StrokeColor: new Rgb24(40, 50, 60), StrokeThickness: 0.031,
                ShadowColor: new Rgb24(70, 80, 90), ShadowOffsetX: 0.041, ShadowOffsetY: 0.051, RotationDegrees: 15,
                GradientEnabled: false, GradientKind: TextGradientKind.Radial,
                GradientStartColor: new Rgb24(1, 2, 3), GradientEndColor: new Rgb24(4, 5, 6),
                Bold: true, Italic: true,
                StackColor: new Rgb24(7, 8, 9), StackStepX: 0.061, StackStepY: 0.071,
                BitmapFillEnabled: true, BitmapFillSource: fillPicture,
                GrowToFillEnabled: true),
            new RawTextElementSnapshot(
                X: 0.55, Y: 0.6, Width: 0.27, Height: 0.16, Z: 1, Locked: false,
                Text: "599 TNX", FontSizeRelative: 0.09, Color: new Rgb24(200, 210, 220),
                FontFamily: "DejaVu Sans Mono", StrokeColor: new Rgb24(11, 12, 13), StrokeThickness: 0.025,
                ShadowColor: new Rgb24(14, 15, 16), ShadowOffsetX: 0.035, ShadowOffsetY: 0.045, RotationDegrees: -20,
                GradientEnabled: true, GradientKind: TextGradientKind.Vertical,
                GradientStartColor: new Rgb24(21, 22, 23), GradientEndColor: new Rgb24(24, 25, 26),
                Bold: false, Italic: false,
                StackColor: new Rgb24(27, 28, 29), StackStepX: 0.055, StackStepY: 0.065,
                BitmapFillEnabled: false, BitmapFillSource: null,
                GrowToFillEnabled: false),
            new RawBoxElementSnapshot(
                X: 0.4, Y: 0.45, Width: 0.25, Height: 0.2, Z: 2, Locked: true,
                FillColor: new Rgb24(100, 110, 120), BorderColor: new Rgb24(130, 140, 150), BorderThickness: 0.011, Opacity: 0.7,
                CornerRadius: 0.033,
                GradientEnabled: true, GradientKind: TextGradientKind.Radial,
                GradientStartColor: new Rgb24(31, 32, 33), GradientEndColor: new Rgb24(34, 35, 36),
                PerspectiveEnabled: false,
                Corner0X: 0.11, Corner0Y: 0.12, Corner1X: 0.21, Corner1Y: 0.13,
                Corner2X: 0.22, Corner2Y: 0.23, Corner3X: 0.12, Corner3Y: 0.24,
                FillEnabled: false,
                RotationDegrees: 30),
            new RawBoxElementSnapshot(
                X: boxQuad.X, Y: boxQuad.Y, Width: boxQuad.Width, Height: boxQuad.Height, Z: 3, Locked: false,
                FillColor: new Rgb24(160, 170, 180), BorderColor: new Rgb24(190, 200, 210), BorderThickness: 0.022, Opacity: 0.4,
                CornerRadius: 0.044,
                GradientEnabled: false, GradientKind: TextGradientKind.Vertical,
                GradientStartColor: new Rgb24(41, 42, 43), GradientEndColor: new Rgb24(44, 45, 46),
                PerspectiveEnabled: true,
                Corner0X: 0.30, Corner0Y: 0.30, Corner1X: 0.60, Corner1Y: 0.32,
                Corner2X: 0.62, Corner2Y: 0.60, Corner3X: 0.28, Corner3Y: 0.58,
                FillEnabled: true,
                RotationDegrees: 0),
            new RawImageElementSnapshot(
                X: 0.35, Y: 0.4, Width: 0.3, Height: 0.25, Z: 4, Locked: true,
                Source: rotatedImage, Fit: ImageFitMode.Cover, Origin: new ImageSourceOrigin(ImageSourceKind.File, "/photos/rotated.png"),
                IsBackground: true,
                NaturalPixelWidth: 640, NaturalPixelHeight: 480,
                PerspectiveEnabled: false,
                Corner0X: 0.13, Corner0Y: 0.14, Corner1X: 0.23, Corner1Y: 0.15,
                Corner2X: 0.24, Corner2Y: 0.25, Corner3X: 0.14, Corner3Y: 0.26,
                RotationDegrees: 45),
            new RawImageElementSnapshot(
                X: imageQuad.X, Y: imageQuad.Y, Width: imageQuad.Width, Height: imageQuad.Height, Z: 5, Locked: false,
                Source: warpedImage, Fit: ImageFitMode.Stretch, Origin: new ImageSourceOrigin(ImageSourceKind.RxHistory, "rx-entry-42"),
                IsBackground: false,
                NaturalPixelWidth: 320, NaturalPixelHeight: 240,
                PerspectiveEnabled: true,
                Corner0X: 0.50, Corner0Y: 0.50, Corner1X: 0.80, Corner1Y: 0.52,
                Corner2X: 0.82, Corner2Y: 0.80, Corner3X: 0.48, Corner3Y: 0.78,
                RotationDegrees: 0),
            new RawLineElementSnapshot(
                X: 0.4, Y: 0.5, Width: 0.6, Height: 0.6, Z: 6, Locked: true,
                X1: 0.1, Y1: 0.2, X2: 0.7, Y2: 0.8, StrokeColor: new Rgb24(51, 52, 53), StrokeThickness: 0.015, Opacity: 0.5),
            new RawLineElementSnapshot(
                X: 0.45, Y: 0.25, Width: 0.4, Height: 0.2, Z: 7, Locked: false,
                X1: 0.65, Y1: 0.15, X2: 0.25, Y2: 0.35, StrokeColor: new Rgb24(61, 62, 63), StrokeThickness: 0.025, Opacity: 0.8),
        ];

        return new Fixtures(elements, fillPicture, rotatedImage, warpedImage);
    }

    /// <summary>Writes every asset by path and returns the exact instance written for that path.</summary>
    private sealed class PathKeyedImageAssets : IImageSourceWriter, IImageFileLoader
    {
        public Dictionary<string, IImageSource> ByPath { get; } = new(StringComparer.Ordinal);

        public Task WritePngAsync(IImageSource source, string path, CancellationToken ct = default)
        {
            ByPath[path] = source;
            return Task.CompletedTask;
        }

        public Task<IImageSource> LoadOriginalAsync(string path, CancellationToken ct = default) =>
            ByPath.TryGetValue(path, out var source)
                ? Task.FromResult(source)
                : Task.FromException<IImageSource>(new FileNotFoundException(path));

        public Task<IImageSource> LoadAsync(string path, int targetWidth, int targetHeight, CancellationToken ct = default) =>
            LoadOriginalAsync(path, ct);
    }

    private sealed record Harness(
        TxImageEditorPaneViewModel Vm, FakeTemplateStore Store, PathKeyedImageAssets Assets, ReadyRackViewModel Rack,
        FakeTransmitImagePreparer Preparer);

    private static Harness CreateHarness(IReadOnlyList<RawElementSnapshot>? elements)
    {
        var store = new FakeTemplateStore();
        var assets = new PathKeyedImageAssets();
        var rack = new ReadyRackViewModel(store, new FakeSettingsStore(), new FakeLocalizationService(), new FakeFilePickerService(), NullLogger<ReadyRackViewModel>.Instance);
        var preparer = new FakeTransmitImagePreparer();
        var initialState = elements is null
            ? null
            : new EditorInitialState(new NormalizedRect(0, 0, 1, 1), PreserveAspect: false, new ImageAdjustments(), elements);
        var vm = new TxImageEditorPaneViewModel(
            new ArrayImageSource(4, 4, new Rgb24[16]), Mode, preparer, new MacroTextResolver(), new OperatorSettings(),
            new FakeRadioSessionService(), new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), assets, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            store, assets, rack, initialState);
        vm.ConfirmRequested = _ => Task.FromResult(true);
        return new Harness(vm, store, assets, rack, preparer);
    }

    private static string Label(ITemplateElementViewModel element) => $"{element.GetType().Name}[Z={element.Z}]";

    private static string Format(object? value) => value switch
    {
        null => "null",
        IImageSource source => $"{source.GetType().Name}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(source)}",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "?",
    };

    private static void CollectDiffs(
        string label, IReadOnlyDictionary<string, object?> expected, IReadOnlyDictionary<string, object?> actual,
        ICollection<string> failures, IReadOnlySet<string>? tolerant = null)
    {
        foreach (var (name, expectedValue) in expected)
        {
            var actualValue = actual[name];
            var ok = tolerant is not null && tolerant.Contains(name) && expectedValue is double e && actualValue is double a
                ? Math.Abs(e - a) <= Tolerance
                : Equals(expectedValue, actualValue);
            if (!ok)
            {
                failures.Add($"{label}.{name}: expected {Format(expectedValue)}, actual {Format(actualValue)}");
            }
        }

        foreach (var name in actual.Keys.Where(k => !expected.ContainsKey(k)))
        {
            failures.Add($"{label}.{name}: no expectation for this inventoried property");
        }
    }

    private static void AssertNoFailures(List<string> failures)
    {
        if (failures.Count > 0)
        {
            Assert.Fail(string.Join(Environment.NewLine, failures));
        }
    }

    private static Dictionary<string, object?> ExpectedFromSnapshot(Type type, RawElementSnapshot snapshot) =>
        ValueTable[type].ToDictionary(kv => kv.Key, kv => kv.Value(snapshot), StringComparer.Ordinal);

    private static List<Dictionary<string, object?>> CaptureAll(TxImageEditorPaneViewModel vm) =>
        vm.OverlayElements.Select(ReadInventoried).ToList();

    private static readonly HashSet<string> GeometryKeys = new(StringComparer.Ordinal) { "X", "Y", "Width", "Height" };

    [AvaloniaFact]
    public void Fixtures_SeededThroughInitialState_EveryValueSticks_AndVariantsCoverEveryPropertyNonDefault()
    {
        var fixtures = BuildFixtures();
        var vm = CreateHarness(fixtures.Elements).Vm;
        var failures = new List<string>();

        Assert.Equal(fixtures.Elements.Count, vm.OverlayElements.Count);
        for (var i = 0; i < fixtures.Elements.Count; i++)
        {
            var element = vm.OverlayElements[i];
            CollectDiffs(Label(element), ExpectedFromSnapshot(element.GetType(), fixtures.Elements[i]), ReadInventoried(element), failures, GeometryKeys);
        }

        var defaults = new Dictionary<Type, Dictionary<string, object?>>
        {
            [typeof(OverlayElementViewModel)] = ReadInventoried(new OverlayElementViewModel()),
            [typeof(BoxElementViewModel)] = ReadInventoried(new BoxElementViewModel()),
            [typeof(LineElementViewModel)] = ReadInventoried(new LineElementViewModel()),
            [typeof(ImageElementViewModel)] = ReadInventoried(new ImageElementViewModel(new ArrayImageSource(1, 1, new Rgb24[1]))
            {
                Origin = new ImageSourceOrigin(ImageSourceKind.LastRx, null),
            }),
        };

        foreach (var type in ElementTypes)
        {
            var ofType = vm.OverlayElements.Where(e => e.GetType() == type).Select(ReadInventoried).ToList();
            foreach (var name in InventoriedProperties(type).Select(p => p.Name))
            {
                if (!ofType.Any(values => !Equals(values[name], defaults[type][name])))
                {
                    failures.Add($"{type.Name}.{name}: no fixture variant sets it to a non-default value");
                }
            }
        }

        AssertNoFailures(failures);
    }

    // ---------------------------------------------------------------------------------------------
    // (a) Undo -> Redo
    // ---------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void UndoThenRedo_OfAnUnrelatedEdit_RecreatesEveryElementWithEveryPropertyAndTheSameDocument()
    {
        var fixtures = BuildFixtures();
        var vm = CreateHarness(fixtures.Elements).Vm;
        var before = CaptureAll(vm);
        var documentBefore = vm.Document.Elements.ToList();
        var instancesBefore = vm.OverlayElements.ToList();

        vm.Brightness = 17;
        vm.UndoCommand.Execute(null);

        Assert.Equal(0, vm.Brightness);
        AssertAllElementsMatch(vm, fixtures, before, "after Undo");
        Assert.All(vm.OverlayElements, e => Assert.DoesNotContain(e, instancesBefore));
        Assert.Equal(documentBefore, vm.Document.Elements);

        var instancesAfterUndo = vm.OverlayElements.ToList();
        vm.RedoCommand.Execute(null);

        Assert.Equal(17, vm.Brightness);
        AssertAllElementsMatch(vm, fixtures, before, "after Redo");
        Assert.All(vm.OverlayElements, e => Assert.DoesNotContain(e, instancesAfterUndo));
        Assert.Equal(documentBefore, vm.Document.Elements);
    }

    /// <summary>Checks against the fixture values as well as the captured pre-edit state: the seed goes
    /// through the same factories, so the captured state alone would hide a field a factory drops.</summary>
    private static void AssertAllElementsMatch(
        TxImageEditorPaneViewModel vm, Fixtures fixtures, List<Dictionary<string, object?>> before, string when)
    {
        var failures = new List<string>();
        Assert.Equal(before.Count, vm.OverlayElements.Count);
        for (var i = 0; i < before.Count; i++)
        {
            var element = vm.OverlayElements[i];
            var actual = ReadInventoried(element);
            CollectDiffs($"{when} vs fixture: {Label(element)}", ExpectedFromSnapshot(element.GetType(), fixtures.Elements[i]), actual, failures, GeometryKeys);
            CollectDiffs($"{when} vs before: {Label(element)}", before[i], actual, failures);
        }

        AssertNoFailures(failures);
    }

    // ---------------------------------------------------------------------------------------------
    // (b) Save -> Load
    // ---------------------------------------------------------------------------------------------

    /// <summary>Mirrors FakeTransmitImagePreparer.MeasureFittedFontSize divided back by the target height,
    /// which is what SaveTemplateAsync persists as FontSizeRelative. Under the fake this is an identity, so
    /// the fitted-size transform itself is guarded by
    /// <c>SaveThenLoadTemplate_GrowToFillEnabled_PersistsTheGrownFontSizeNotTheOriginal</c> (real preparer).</summary>
    private static double FakeFittedFontSizeRelative(double fontSizeRelative) =>
        fontSizeRelative * Mode.ImageHeight / Mode.ImageHeight;

    private static async Task SaveAsync(Harness harness, string name)
    {
        harness.Vm.NewTemplateName = name;
        await harness.Vm.SaveTemplateCommand.ExecuteAsync(null);
        Assert.Null(harness.Vm.StatusMessage);
    }

    private static void LoadThroughReadyRack(Harness harness, string templateId)
    {
        harness.Rack.LoadCommand.Execute(Assert.Single(harness.Rack.AllTemplates, t => t.Id == templateId));
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task SaveThenLoad_ThroughTheTemplateStore_RestoresEveryPropertyExceptTheKnownTransforms()
    {
        var fixtures = BuildFixtures();
        var harness = CreateHarness(fixtures.Elements);
        var vm = harness.Vm;
        var before = CaptureAll(vm);
        var documentBefore = vm.Document.Elements.ToList();
        var instancesBefore = vm.OverlayElements.ToList();

        await SaveAsync(harness, "Round trip");
        var (templateId, saved) = Assert.Single(harness.Store.Templates);
        var persisted = saved.Document.Elements;
        LoadThroughReadyRack(harness, templateId);

        Assert.Equal(before.Count, vm.OverlayElements.Count);
        Assert.All(vm.OverlayElements, e => Assert.DoesNotContain(e, instancesBefore));

        var failures = new List<string>();
        var expectedDocument = new List<TemplateElement>();
        for (var i = 0; i < before.Count; i++)
        {
            var expected = new Dictionary<string, object?>(before[i], StringComparer.Ordinal);
            var expectedEntry = documentBefore[i];
            switch (persisted[i])
            {
                case PersistedTextElement text:
                    expected["GrowToFillEnabled"] = false;
                    expected["FontSizeRelative"] = FakeFittedFontSizeRelative((double)before[i]["FontSizeRelative"]!);
                    if ((bool)before[i]["BitmapFillEnabled"]!)
                    {
                        var fillPath = harness.Store.GetAssetPath(templateId, Assert.IsType<string>(text.BitmapFillAssetFileName));
                        Assert.Same(before[i]["BitmapFillSource"], harness.Assets.ByPath[fillPath]);
                        expected["BitmapFillSource"] = harness.Assets.ByPath[fillPath];
                    }
                    else
                    {
                        Assert.Null(text.BitmapFillAssetFileName);
                        expected["BitmapFillSource"] = null;
                    }

                    var textEntry = (TemplateTextElement)expectedEntry;
                    expectedEntry = textEntry with
                    {
                        GrowToFillEnabled = false,
                        Font = textEntry.Font with { Size = FakeFittedFontSizeRelative(textEntry.Font.Size) },
                    };
                    break;
                case PersistedImageElement image:
                    var assetPath = harness.Store.GetAssetPath(templateId, image.AssetFileName);
                    Assert.Same(before[i]["Source"], harness.Assets.ByPath[assetPath]);
                    expected["Source"] = harness.Assets.ByPath[assetPath];
                    expected["Origin"] = new ImageSourceOrigin(ImageSourceKind.File, assetPath);
                    break;
            }

            CollectDiffs($"after load: {Label(vm.OverlayElements[i])}", expected, ReadInventoried(vm.OverlayElements[i]), failures);
            expectedDocument.Add(expectedEntry);
        }

        AssertNoFailures(failures);
        Assert.Equal(expectedDocument, vm.Document.Elements);
    }

    // ---------------------------------------------------------------------------------------------
    // (c) Duplicate and Copy/Paste
    // ---------------------------------------------------------------------------------------------

    /// <summary>Same rule as the pane's own ClampSharedOffsetDelta: one shift that keeps every value in [0,1].</summary>
    private static double SharedDelta(double offset, params double[] values)
    {
        var min = values.Max(v => -v);
        var max = values.Min(v => 1 - v);
        return min <= max ? Math.Clamp(offset, min, max) : 0;
    }

    /// <summary>Today's per-type, per-variant expected changes for a clone (Duplicate or Paste), applied to
    /// the fixture values the original was seeded from.</summary>
    private static (Dictionary<string, object?> Expected, HashSet<string> Shifted, double Dx, double Dy) ExpectedClone(
        Type type, IReadOnlyDictionary<string, object?> seeded, int newZ)
    {
        var values = new Dictionary<string, object?>(seeded, StringComparer.Ordinal);
        double Get(string name) => (double)values[name]!;
        var shifted = new HashSet<string>(StringComparer.Ordinal);
        values["Z"] = newZ;
        double dx;
        double dy;
        if (type == typeof(LineElementViewModel))
        {
            dx = SharedDelta(CloneOffset, Get("X1"), Get("X2"));
            dy = SharedDelta(CloneOffset, Get("Y1"), Get("Y2"));
            values["X1"] = Get("X1") + dx;
            values["X2"] = Get("X2") + dx;
            values["Y1"] = Get("Y1") + dy;
            values["Y2"] = Get("Y2") + dy;
            shifted.UnionWith(["X1", "X2", "Y1", "Y2"]);
        }
        else if (values.TryGetValue("PerspectiveEnabled", out var perspective) && perspective is true)
        {
            dx = SharedDelta(CloneOffset, Get("Corner0X"), Get("Corner1X"), Get("Corner2X"), Get("Corner3X"));
            dy = SharedDelta(CloneOffset, Get("Corner0Y"), Get("Corner1Y"), Get("Corner2Y"), Get("Corner3Y"));
            for (var c = 0; c < 4; c++)
            {
                values[$"Corner{c}X"] = Get($"Corner{c}X") + dx;
                values[$"Corner{c}Y"] = Get($"Corner{c}Y") + dy;
                shifted.Add($"Corner{c}X");
                shifted.Add($"Corner{c}Y");
            }

            values["X"] = Get("X") + dx;
            values["Y"] = Get("Y") + dy;
            shifted.UnionWith(["X", "Y", "Width", "Height"]);
        }
        else
        {
            var x = Math.Clamp(Get("X") + CloneOffset, 0, 1);
            var y = Math.Clamp(Get("Y") + CloneOffset, 0, 1);
            dx = x - Get("X");
            dy = y - Get("Y");
            values["X"] = x;
            values["Y"] = y;
            shifted.UnionWith(["X", "Y"]);
        }

        if (type == typeof(ImageElementViewModel))
        {
            values["IsBackground"] = false;
            values["Locked"] = false;
        }

        return (values, shifted, dx, dy);
    }

    private static bool Close(double expected, double actual) => Math.Abs(expected - actual) <= Tolerance;

    private static bool BoundsShifted(NormalizedRect original, NormalizedRect actual, double dx, double dy) =>
        Close(original.X + dx, actual.X) && Close(original.Y + dy, actual.Y)
        && Close(original.Width, actual.Width) && Close(original.Height, actual.Height);

    private static bool CornersShifted(PerspectiveCorners? original, PerspectiveCorners? actual, double dx, double dy) =>
        (original, actual) switch
        {
            (null, null) => true,
            ({ } o, { } a) => Close(o.Corner0X + dx, a.Corner0X) && Close(o.Corner0Y + dy, a.Corner0Y)
                && Close(o.Corner1X + dx, a.Corner1X) && Close(o.Corner1Y + dy, a.Corner1Y)
                && Close(o.Corner2X + dx, a.Corner2X) && Close(o.Corner2Y + dy, a.Corner2Y)
                && Close(o.Corner3X + dx, a.Corner3X) && Close(o.Corner3Y + dy, a.Corner3Y),
            _ => false,
        };

    /// <summary>The clone's Document entry must equal the original's apart from the expected-changes table.</summary>
    private static void CollectCloneDocumentDiffs(
        string label, TemplateElement original, TemplateElement clone, int newZ, double dx, double dy, ICollection<string> failures)
    {
        var (unchangedOk, shiftOk) = (original, clone) switch
        {
            (TemplateTextElement o, TemplateTextElement c) =>
                (o with { Bounds = c.Bounds, Z = c.Z } == c, BoundsShifted(o.Bounds, c.Bounds, dx, dy)),
            (TemplateBoxElement o, TemplateBoxElement c) =>
                (o with { Bounds = c.Bounds, Z = c.Z, Perspective = c.Perspective } == c,
                    BoundsShifted(o.Bounds, c.Bounds, dx, dy) && CornersShifted(o.Perspective, c.Perspective, dx, dy)),
            (TemplateImageElement o, TemplateImageElement c) =>
                (o with { Bounds = c.Bounds, Z = c.Z, Perspective = c.Perspective } == c,
                    BoundsShifted(o.Bounds, c.Bounds, dx, dy) && CornersShifted(o.Perspective, c.Perspective, dx, dy)),
            (TemplateLineElement o, TemplateLineElement c) =>
                (o with { Bounds = c.Bounds, Z = c.Z, X1 = c.X1, Y1 = c.Y1, X2 = c.X2, Y2 = c.Y2 } == c,
                    BoundsShifted(o.Bounds, c.Bounds, dx, dy) && Close(o.X1 + dx, c.X1) && Close(o.Y1 + dy, c.Y1)
                    && Close(o.X2 + dx, c.X2) && Close(o.Y2 + dy, c.Y2)),
            _ => (false, false),
        };

        if (!unchangedOk)
        {
            failures.Add($"{label} Document: clone entry differs outside the expected changes: original {original}, clone {clone}");
        }

        if (!shiftOk)
        {
            failures.Add($"{label} Document: clone entry not shifted by ({dx}, {dy}): original {original}, clone {clone}");
        }

        if (clone.Z != newZ)
        {
            failures.Add($"{label} Document: clone Z {clone.Z}, expected {newZ}");
        }
    }

    private static void RunCloneCase(Action<TxImageEditorPaneViewModel> cloneSelected, string operation)
    {
        const int count = 8;
        var failures = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var fixtures = BuildFixtures();
            var vm = CreateHarness(fixtures.Elements).Vm;
            var original = vm.OverlayElements[index];
            var label = $"{operation} {Label(original)}";
            var documentBefore = vm.Document.Elements.ToList();
            var beforeAll = CaptureAll(vm);
            var newZ = vm.OverlayElements.Max(e => e.Z) + 1;
            var (expected, shifted, dx, dy) = ExpectedClone(original.GetType(), ExpectedFromSnapshot(original.GetType(), fixtures.Elements[index]), newZ);

            vm.SelectedOverlayElement = original;
            cloneSelected(vm);

            Assert.Equal(count + 1, vm.OverlayElements.Count);
            var clone = vm.OverlayElements[^1];
            Assert.Same(clone, vm.SelectedOverlayElement);
            Assert.NotSame(original, clone);
            Assert.IsType(original.GetType(), clone);

            CollectDiffs(label, expected, ReadInventoried(clone), failures, shifted);
            for (var i = 0; i < count; i++)
            {
                CollectDiffs($"{label} existing {Label(vm.OverlayElements[i])}", beforeAll[i], ReadInventoried(vm.OverlayElements[i]), failures);
            }

            var documentAfter = vm.Document.Elements;
            Assert.Equal(count + 1, documentAfter.Count);
            for (var i = 0; i < count; i++)
            {
                if (!Equals(documentBefore[i], documentAfter[i]))
                {
                    failures.Add($"{label} Document: existing entry {i} changed: {documentBefore[i]} -> {documentAfter[i]}");
                }
            }

            CollectCloneDocumentDiffs(label, documentBefore[index], documentAfter[count], newZ, dx, dy, failures);
        }

        AssertNoFailures(failures);
    }

    [AvaloniaFact]
    public void Duplicate_EveryTypeAndVariant_ClonesEveryPropertyApartFromTheExpectedChanges() =>
        RunCloneCase(vm => vm.DuplicateCommand.Execute(null), "Duplicate");

    [AvaloniaFact]
    public void CopyThenPaste_EveryTypeAndVariant_ClonesEveryPropertyApartFromTheExpectedChanges() =>
        RunCloneCase(
            vm =>
            {
                vm.CopySelectedElementCommand.Execute(null);
                vm.PasteElementCommand.Execute(null);
            },
            "Copy/Paste");

    // ---------------------------------------------------------------------------------------------
    // (d) Paste style
    // ---------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void PasteStyle_EveryStylePropertyIsCopied_AndEveryNonStylePropertyIsUnchanged()
    {
        // Fixture index pairs of the same type, pasted both ways.
        // (2, 3) pins that a rotated box pasted onto a perspective box leaves both on: a suspected bug, pinned as today's behaviour.
        (int Source, int Target)[] pairs = [(0, 1), (1, 0), (2, 3), (3, 2), (6, 7), (7, 6)];
        var failures = new List<string>();
        foreach (var (sourceIndex, targetIndex) in pairs)
        {
            var vm = CreateHarness(BuildFixtures().Elements).Vm;
            var source = vm.OverlayElements[sourceIndex];
            var target = vm.OverlayElements[targetIndex];
            var label = $"PasteStyle {Label(source)} -> {Label(target)}";
            var sourceValues = ReadInventoried(source);
            var targetBefore = ReadInventoried(target);
            var type = target.GetType();

            foreach (var name in StyleProperties[type].Where(n => Equals(sourceValues[n], targetBefore[n])))
            {
                failures.Add($"{label}.{name}: source and target already equal, the paste would prove nothing");
            }

            vm.SelectedOverlayElement = source;
            vm.CopySelectedElementStyleCommand.Execute(null);
            vm.SelectedOverlayElement = target;
            Assert.True(vm.PasteSelectedElementStyleCommand.CanExecute(null));
            vm.PasteSelectedElementStyleCommand.Execute(null);

            var expected = targetBefore.ToDictionary(
                kv => kv.Key,
                kv => StyleProperties[type].Contains(kv.Key) ? sourceValues[kv.Key] : kv.Value,
                StringComparer.Ordinal);
            CollectDiffs(label, expected, ReadInventoried(target), failures);
        }

        AssertNoFailures(failures);
    }

    [AvaloniaFact]
    public void PasteStyle_ImageElements_HaveNoCopyOrPasteStyle()
    {
        var vm = CreateHarness(BuildFixtures().Elements).Vm;
        foreach (var image in vm.OverlayElements.OfType<ImageElementViewModel>())
        {
            vm.SelectedOverlayElement = image;
            Assert.False(vm.CopySelectedElementStyleCommand.CanExecute(null));
        }

        vm.SelectedOverlayElement = vm.OverlayElements[0];
        vm.CopySelectedElementStyleCommand.Execute(null);
        foreach (var image in vm.OverlayElements.OfType<ImageElementViewModel>())
        {
            vm.SelectedOverlayElement = image;
            Assert.False(vm.PasteSelectedElementStyleCommand.CanExecute(null));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // (f) New-element defaults
    // ---------------------------------------------------------------------------------------------

    private static Harness AddTextAt()
    {
        var harness = CreateHarness(null);
        harness.Vm.AddOverlayElementAt(0.5, 0.45, 0.3, 0.18);
        return harness;
    }

    private static Harness AddBoxAt()
    {
        var harness = CreateHarness(null);
        harness.Vm.AddBoxElementAt(0.4, 0.55, 0.3, 0.2);
        return harness;
    }

    private static Harness AddLineAt()
    {
        var harness = CreateHarness(null);
        harness.Vm.AddLineElementAt(0.2, 0.3, 0.7, 0.6);
        return harness;
    }

    private const string DroppedPath = "/drop/photo.png";

    private static async Task<(Harness Harness, IImageSource Dropped)> DropImageAsync()
    {
        var harness = CreateHarness(null);
        var dropped = new ArrayImageSource(3, 3, new Rgb24[9]);
        harness.Assets.ByPath[DroppedPath] = dropped;
        await harness.Vm.AddImagesFromDroppedFilesAsync([DroppedPath]);
        return (harness, dropped);
    }

    private static (Harness Harness, IImageSource Baked) PromoteBackground()
    {
        var harness = CreateHarness(null);
        var index = harness.Preparer.AdjustmentsResults.Count;
        harness.Vm.PromoteBackgroundToBackdropCommand.Execute(null);
        return (harness, harness.Preparer.AdjustmentsResults[index]);
    }

    private static Harness AddPlate()
    {
        var harness = AddTextAt();
        harness.Vm.AddPlateBehindTextCommand.Execute(null);
        return harness;
    }

    private static void AssertNewElement(
        Harness harness, ITemplateElementViewModel element, Dictionary<string, object?> expected, TemplateElement expectedDocumentEntry)
    {
        var failures = new List<string>();
        CollectDiffs($"new {Label(element)}", expected, ReadInventoried(element), failures);
        AssertNoFailures(failures);
        Assert.Equal(expectedDocumentEntry, harness.Vm.Document.Elements[harness.Vm.OverlayElements.IndexOf(element)]);
    }

    [AvaloniaFact]
    public void NewElementDefaults_AddOverlayElementAt()
    {
        var harness = AddTextAt();
        var element = Assert.IsType<OverlayElementViewModel>(harness.Vm.SelectedOverlayElement);
        AssertNewElement(harness, element, new(StringComparer.Ordinal)
        {
            ["X"] = 0.5,
            ["Y"] = 0.45,
            ["Width"] = 0.3,
            ["Height"] = 0.18,
            ["Z"] = 0,
            ["Locked"] = false,
            ["Text"] = "Text",
            ["FontSizeRelative"] = 0.1,
            ["Color"] = new Rgb24(255, 255, 255),
            ["FontFamily"] = "DejaVu Sans Mono",
            ["StrokeColor"] = null,
            ["StrokeThickness"] = 0.02,
            ["ShadowColor"] = null,
            ["ShadowOffsetX"] = 0.02,
            ["ShadowOffsetY"] = 0.02,
            ["RotationDegrees"] = 0.0,
            ["GradientEnabled"] = false,
            ["GradientKind"] = TextGradientKind.Horizontal,
            ["GradientStartColor"] = new Rgb24(255, 0, 0),
            ["GradientEndColor"] = new Rgb24(0, 0, 255),
            ["Bold"] = false,
            ["Italic"] = false,
            ["StackColor"] = null,
            ["StackStepX"] = 0.02,
            ["StackStepY"] = 0.02,
            ["BitmapFillEnabled"] = false,
            ["BitmapFillSource"] = null,
            ["GrowToFillEnabled"] = false,
        }, new TemplateTextElement(
            new NormalizedRect(0.35, 0.36, 0.3, 0.18), Z: 0, Content: "Text", Font: new FontSpec("DejaVu Sans Mono", 0.1, Bold: false, Italic: false),
            Color: new Rgb24(255, 255, 255), StrokeColor: null, StrokeThickness: 0.02, ShadowColor: null, ShadowOffsetX: 0.02, ShadowOffsetY: 0.02,
            RotationDegrees: 0, Gradient: null, StackColor: null, StackStepX: 0.02, StackStepY: 0.02, BitmapFill: null, GrowToFillEnabled: false));
    }

    private static Dictionary<string, object?> ExpectedPlainBox(
        double x, double y, double width, double height, Rgb24 fillColor, double opacity) => new(StringComparer.Ordinal)
    {
        ["X"] = x,
        ["Y"] = y,
        ["Width"] = width,
        ["Height"] = height,
        ["Z"] = 0,
        ["Locked"] = false,
        ["FillColor"] = fillColor,
        ["BorderColor"] = null,
        ["BorderThickness"] = 0.0,
        ["Opacity"] = opacity,
        ["CornerRadius"] = 0.0,
        ["GradientEnabled"] = false,
        ["GradientKind"] = TextGradientKind.Horizontal,
        ["GradientStartColor"] = new Rgb24(255, 0, 0),
        ["GradientEndColor"] = new Rgb24(0, 0, 255),
        ["PerspectiveEnabled"] = false,
        ["Corner0X"] = 0.0,
        ["Corner0Y"] = 0.0,
        ["Corner1X"] = 0.0,
        ["Corner1Y"] = 0.0,
        ["Corner2X"] = 0.0,
        ["Corner2Y"] = 0.0,
        ["Corner3X"] = 0.0,
        ["Corner3Y"] = 0.0,
        ["FillEnabled"] = true,
        ["RotationDegrees"] = 0.0,
    };

    [AvaloniaFact]
    public void NewElementDefaults_AddBoxElementAt()
    {
        var harness = AddBoxAt();
        var element = Assert.IsType<BoxElementViewModel>(harness.Vm.SelectedOverlayElement);
        AssertNewElement(
            harness, element, ExpectedPlainBox(0.4, 0.55, 0.3, 0.2, new Rgb24(64, 64, 64), opacity: 1.0),
            new TemplateBoxElement(
                new NormalizedRect(0.25, 0.45000000000000007, 0.3, 0.2), Z: 0, FillColor: new Rgb24(64, 64, 64), BorderColor: null,
                BorderThickness: 0, Opacity: 1, CornerRadius: 0, Gradient: null, Perspective: null, FillEnabled: true, RotationDegrees: 0));
    }

    [AvaloniaFact]
    public void NewElementDefaults_AddLineElementAt()
    {
        var harness = AddLineAt();
        var element = Assert.IsType<LineElementViewModel>(harness.Vm.SelectedOverlayElement);
        AssertNewElement(harness, element, new(StringComparer.Ordinal)
        {
            ["Z"] = 0,
            ["Locked"] = false,
            ["X1"] = 0.2,
            ["Y1"] = 0.3,
            ["X2"] = 0.7,
            ["Y2"] = 0.6,
            ["StrokeColor"] = new Rgb24(255, 255, 255),
            ["StrokeThickness"] = 0.01,
            ["Opacity"] = 1.0,
        }, new TemplateLineElement(
            new NormalizedRect(0.195, 0.295, 0.51, 0.31), Z: 0, X1: 0.2, Y1: 0.3, X2: 0.7, Y2: 0.6,
            StrokeColor: new Rgb24(255, 255, 255), Thickness: 0.01, Opacity: 1));
    }

    private static Dictionary<string, object?> ExpectedPlainImage(
        double x, double y, double width, double height, bool locked, IImageSource source, ImageSourceOrigin origin,
        bool isBackground, int naturalPixelWidth, int naturalPixelHeight) => new(StringComparer.Ordinal)
    {
        ["X"] = x,
        ["Y"] = y,
        ["Width"] = width,
        ["Height"] = height,
        ["Z"] = 0,
        ["Locked"] = locked,
        ["Source"] = source,
        ["Fit"] = ImageFitMode.Contain,
        ["Origin"] = origin,
        ["IsBackground"] = isBackground,
        ["NaturalPixelWidth"] = naturalPixelWidth,
        ["NaturalPixelHeight"] = naturalPixelHeight,
        ["PerspectiveEnabled"] = false,
        ["Corner0X"] = 0.0,
        ["Corner0Y"] = 0.0,
        ["Corner1X"] = 0.0,
        ["Corner1Y"] = 0.0,
        ["Corner2X"] = 0.0,
        ["Corner2Y"] = 0.0,
        ["Corner3X"] = 0.0,
        ["Corner3Y"] = 0.0,
        ["RotationDegrees"] = 0.0,
    };

    [AvaloniaFact]
    public async Task NewElementDefaults_InsertImageElementCore_ViaDroppedFile()
    {
        var (harness, dropped) = await DropImageAsync();
        var element = Assert.IsType<ImageElementViewModel>(harness.Vm.SelectedOverlayElement);
        AssertNewElement(
            harness, element,
            ExpectedPlainImage(0.5, 0.5, 0.3, 0.3, locked: false, dropped, new ImageSourceOrigin(ImageSourceKind.File, DroppedPath),
                isBackground: false, naturalPixelWidth: 3, naturalPixelHeight: 3),
            new TemplateImageElement(new NormalizedRect(0.35, 0.35, 0.3, 0.3), Z: 0, dropped, ImageFitMode.Contain, Perspective: null, RotationDegrees: 0));
    }

    [AvaloniaFact]
    public void NewElementDefaults_PromoteBackgroundToBackdrop()
    {
        var (harness, baked) = PromoteBackground();
        var element = Assert.IsType<ImageElementViewModel>(Assert.Single(harness.Vm.OverlayElements));
        AssertNewElement(
            harness, element,
            ExpectedPlainImage(0.5, 0.5, 1, 1, locked: true, baked, new ImageSourceOrigin(ImageSourceKind.Promoted, null),
                isBackground: true, naturalPixelWidth: 4, naturalPixelHeight: 4),
            new TemplateImageElement(new NormalizedRect(0, 0, 1, 1), Z: 0, baked, ImageFitMode.Contain, Perspective: null, RotationDegrees: 0));
    }

    [AvaloniaFact]
    public void NewElementDefaults_AddPlateBehindText()
    {
        var harness = AddPlate();
        var element = Assert.IsType<BoxElementViewModel>(harness.Vm.SelectedOverlayElement);
        AssertNewElement(
            harness, element, ExpectedPlainBox(0.5, 0.45, 0.33999999999999997, 0.22, new Rgb24(0, 0, 0), opacity: 0.6),
            new TemplateBoxElement(
                new NormalizedRect(0.33, 0.34, 0.33999999999999997, 0.22), Z: 0, FillColor: new Rgb24(0, 0, 0), BorderColor: null,
                BorderThickness: 0, Opacity: 0.6, CornerRadius: 0, Gradient: null, Perspective: null, FillEnabled: true, RotationDegrees: 0));
    }

    // ---------------------------------------------------------------------------------------------
    // (e) Saved-format golden
    // ---------------------------------------------------------------------------------------------

    // Captured from code; pins ImageSourceKind -> PersistedImageSourceKind (File = 0, RxHistory = 1); never edit to pass.
    private const string SavedFormatGolden = """
        {"$type":"text","Text":"CQ DE %m","FontSizeRelative":0.12,"Color":{"R":10,"G":20,"B":30},"FontFamily":"Barlow","StrokeColor":{"R":40,"G":50,"B":60},"StrokeThickness":0.031,"ShadowColor":{"R":70,"G":80,"B":90},"ShadowOffsetX":0.041,"ShadowOffsetY":0.051,"RotationDegrees":15,"GradientEnabled":false,"GradientKind":2,"GradientStartColor":{"R":1,"G":2,"B":3},"GradientEndColor":{"R":4,"G":5,"B":6},"Bold":true,"Italic":true,"StackColor":{"R":7,"G":8,"B":9},"StackStepX":0.061,"StackStepY":0.071,"BitmapFillEnabled":true,"BitmapFillAssetFileName":"asset0.png","X":0.31,"Y":0.42,"Width":0.33,"Height":0.21,"Z":0,"Locked":true}
        {"$type":"text","Text":"599 TNX","FontSizeRelative":0.09,"Color":{"R":200,"G":210,"B":220},"FontFamily":"DejaVu Sans Mono","StrokeColor":{"R":11,"G":12,"B":13},"StrokeThickness":0.025,"ShadowColor":{"R":14,"G":15,"B":16},"ShadowOffsetX":0.035,"ShadowOffsetY":0.045,"RotationDegrees":-20,"GradientEnabled":true,"GradientKind":1,"GradientStartColor":{"R":21,"G":22,"B":23},"GradientEndColor":{"R":24,"G":25,"B":26},"Bold":false,"Italic":false,"StackColor":{"R":27,"G":28,"B":29},"StackStepX":0.055,"StackStepY":0.065,"BitmapFillEnabled":false,"BitmapFillAssetFileName":null,"X":0.55,"Y":0.6,"Width":0.27,"Height":0.16,"Z":1,"Locked":false}
        {"$type":"box","FillColor":{"R":100,"G":110,"B":120},"BorderColor":{"R":130,"G":140,"B":150},"BorderThickness":0.011,"Opacity":0.7,"CornerRadius":0.033,"GradientEnabled":true,"GradientKind":2,"GradientStartColor":{"R":31,"G":32,"B":33},"GradientEndColor":{"R":34,"G":35,"B":36},"PerspectiveEnabled":false,"Corner0X":0.11,"Corner0Y":0.12,"Corner1X":0.21,"Corner1Y":0.13,"Corner2X":0.22,"Corner2Y":0.23,"Corner3X":0.12,"Corner3Y":0.24,"FillEnabled":false,"RotationDegrees":30,"X":0.4,"Y":0.45,"Width":0.25,"Height":0.2,"Z":2,"Locked":true}
        {"$type":"box","FillColor":{"R":160,"G":170,"B":180},"BorderColor":{"R":190,"G":200,"B":210},"BorderThickness":0.022,"Opacity":0.4,"CornerRadius":0.044,"GradientEnabled":false,"GradientKind":1,"GradientStartColor":{"R":41,"G":42,"B":43},"GradientEndColor":{"R":44,"G":45,"B":46},"PerspectiveEnabled":true,"Corner0X":0.3,"Corner0Y":0.3,"Corner1X":0.6,"Corner1Y":0.32,"Corner2X":0.62,"Corner2Y":0.6,"Corner3X":0.28,"Corner3Y":0.58,"FillEnabled":true,"RotationDegrees":0,"X":0.45,"Y":0.44999999999999996,"Width":0.33999999999999997,"Height":0.3,"Z":3,"Locked":false}
        {"$type":"image","AssetFileName":"asset1.png","Fit":2,"OriginKind":0,"OriginPayload":"/photos/rotated.png","IsBackground":true,"NaturalPixelWidth":640,"NaturalPixelHeight":480,"PerspectiveEnabled":false,"Corner0X":0.13,"Corner0Y":0.14,"Corner1X":0.23,"Corner1Y":0.15,"Corner2X":0.24,"Corner2Y":0.25,"Corner3X":0.14,"Corner3Y":0.26,"RotationDegrees":45,"X":0.35,"Y":0.4,"Width":0.3,"Height":0.25,"Z":4,"Locked":true}
        {"$type":"image","AssetFileName":"asset2.png","Fit":0,"OriginKind":1,"OriginPayload":"rx-entry-42","IsBackground":false,"NaturalPixelWidth":320,"NaturalPixelHeight":240,"PerspectiveEnabled":true,"Corner0X":0.5,"Corner0Y":0.5,"Corner1X":0.8,"Corner1Y":0.52,"Corner2X":0.82,"Corner2Y":0.8,"Corner3X":0.48,"Corner3Y":0.78,"RotationDegrees":0,"X":0.6499999999999999,"Y":0.65,"Width":0.33999999999999997,"Height":0.30000000000000004,"Z":5,"Locked":false}
        {"$type":"line","X1":0.1,"Y1":0.2,"X2":0.7,"Y2":0.8,"StrokeColor":{"R":51,"G":52,"B":53},"StrokeThickness":0.015,"Opacity":0.5,"X":0.39999999999999997,"Y":0.5,"Width":0.6,"Height":0.6000000000000001,"Z":6,"Locked":true}
        {"$type":"line","X1":0.65,"Y1":0.15,"X2":0.25,"Y2":0.35,"StrokeColor":{"R":61,"G":62,"B":63},"StrokeThickness":0.025,"Opacity":0.8,"X":0.45,"Y":0.25,"Width":0.4,"Height":0.19999999999999998,"Z":7,"Locked":false}
        """;

    [GeneratedRegex("[0-9a-f]{32}\\.png")]
    private static partial Regex GuidAssetName();

    [GeneratedRegex("asset[0-9]+\\.png")]
    private static partial Regex NormalizedAssetName();

    [GeneratedRegex("\"OriginKind\":[0-9]+,\"OriginPayload\":(?:null|\"(?:[^\"\\\\]|\\\\.)*\")")]
    private static partial Regex OriginFields();

    private static string SerializeElements(PersistedTemplateDocument document)
    {
        var assetNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var element in document.Elements)
        {
            var json = JsonSerializer.Serialize(element, PersistedTemplateJsonContext.Default.PersistedTemplateElement);
            lines.Add(GuidAssetName().Replace(json, match =>
            {
                if (!assetNames.TryGetValue(match.Value, out var normalized))
                {
                    normalized = $"asset{assetNames.Count}.png";
                    assetNames[match.Value] = normalized;
                }

                return normalized;
            }));
        }

        return string.Join("\n", lines);
    }

    private static string NormalizeOriginAndTemplateId(string json, string templateId) =>
        OriginFields().Replace(json, "\"OriginKind\":\"<normalized>\",\"OriginPayload\":\"<normalized>\"")
            .Replace(templateId, "<templateId>", StringComparison.Ordinal);

    [AvaloniaFact]
    public async Task SavedFormat_MatchesTheGolden_AndLoadingThenResavingTheGoldenIsByteIdentical()
    {
        var golden = SavedFormatGolden.ReplaceLineEndings("\n");

        var source = CreateHarness(BuildFixtures().Elements);
        await SaveAsync(source, "Golden source");
        var saved = Assert.Single(source.Store.Templates).Value.Document;
        Assert.Equal(golden, SerializeElements(saved));

        var reload = CreateHarness([]);
        var goldenElements = golden.Split('\n')
            .Select(line => JsonSerializer.Deserialize(line, PersistedTemplateJsonContext.Default.PersistedTemplateElement)!)
            .ToList();
        var templateId = reload.Store.CreateTemplateId("Golden");
        await reload.Store.SaveAsync(templateId, "Golden", new PersistedTemplateDocument(goldenElements));
        foreach (var assetName in NormalizedAssetName().Matches(golden).Select(m => m.Value).Distinct(StringComparer.Ordinal))
        {
            reload.Assets.ByPath[reload.Store.GetAssetPath(templateId, assetName)] = new ArrayImageSource(2, 2, new Rgb24[4]);
        }

        await reload.Rack.RefreshAsync();
        LoadThroughReadyRack(reload, templateId);
        Assert.Equal(goldenElements.Count, reload.Vm.OverlayElements.Count);

        await SaveAsync(reload, "Golden resaved");
        var resaved = reload.Store.Templates.Values.Single(t => t.Name == "Golden resaved").Document;
        Assert.Equal(NormalizeOriginAndTemplateId(golden, templateId), NormalizeOriginAndTemplateId(SerializeElements(resaved), templateId));
    }
}
