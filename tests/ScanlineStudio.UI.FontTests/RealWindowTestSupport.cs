using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.UI.Tests;
using ScanlineStudio.UI.ViewModels;
using ScanlineStudio.UI.Views;

namespace ScanlineStudio.UI.FontTests;

/// <summary>Shared setup for real-Skia, real-<see cref="TxImageEditorPaneView"/> tests in this
/// project -- extracted from <c>TxImageEditorPerspectiveCornerDragRealRenderTests</c> (the first test
/// to need this) when <c>TxImageEditorRealUiSmokeTests</c> needed the identical setup a second time.
/// This is the only place in <c>ScanlineStudio.UI.Tests</c> or this project that can host the real
/// production View -- see <c>TxImageEditorQuickStyleFlyoutRealClickTests</c>'s own doc comment (in
/// <c>ScanlineStudio.UI.Tests</c>) for why the stub-based project can't.</summary>
internal static class RealWindowTestSupport
{
    public const int DefaultSourceWidth = 320;
    public const int DefaultSourceHeight = 240;

    public static readonly SstvModeDefinition TestMode = new(
        Id: "test-mode",
        DisplayName: "Test",
        VisCode: 0,
        ImageWidth: DefaultSourceWidth,
        ImageHeight: DefaultSourceHeight,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments: []);

    private static bool _servicesInitialized;

    /// <summary><see cref="ScanlineStudio.UI.Localization.TranslateExtension"/> (backing every
    /// <c>{loc:Translate ...}</c> binding <see cref="TxImageEditorPaneView"/>'s AXAML uses throughout)
    /// throws <see cref="InvalidOperationException"/> against a null <c>App.Services</c>. Set once,
    /// process-wide -- <see cref="TestAppBuilder"/> deliberately leaves this to individual test
    /// classes (see its own doc comment); this is the shared one-time init every real-View test class
    /// in this project calls before constructing a View. Unguarded/one-way (no lock) -- safe today
    /// because <c>Avalonia.Headless.XUnit</c> serializes every <c>[AvaloniaFact]</c> body onto one
    /// per-assembly session dispatcher thread; a second concurrently-hosting test class would need to
    /// revisit this.</summary>
    public static void EnsureAppServices()
    {
        if (_servicesInitialized)
        {
            return;
        }

        var services = new ServiceCollection();
        services.AddSingleton<ILocalizationService>(new FakeLocalizationService());
        App.Services = services.BuildServiceProvider();
        _servicesInitialized = true;
    }

    public static string FontPath { get; } = Path.Combine(FindRepoRoot(), "assets", "fonts", "DejaVuSansMono.ttf");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScanlineStudio.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root (ScanlineStudio.sln) from test output directory.");
        }

        return dir.FullName;
    }

    public static ArrayImageSource CreateSource(int width, int height) => new(width, height, new Rgb24[width * height]);

    // User-reported bug (2026-09-15): "if i set as backdrop an image...the preview window no longer
    // displays the background image information" -- verifying it needs a background and an inserted
    // image the test can tell apart by ACTUAL pixel color, which the plain black CreateSource above
    // can't do (background and every AddImageFromFileCommand-inserted image are otherwise both
    // black).
    public static ArrayImageSource CreateSolidSource(int width, int height, Rgb24 color) =>
        new(width, height, Enumerable.Repeat(color, width * height).ToArray());

    public static TxImageEditorPaneViewModel CreateEditor(IImageSource original, SstvModeDefinition mode) =>
        CreateEditor(original, mode, new FakeImageFileLoader { ResultToReturn = CreateSource(mode.ImageWidth, mode.ImageHeight) });

    /// <summary>Overload exposing <paramref name="imageFileLoader"/> so a test can hand
    /// <see cref="AddImageFromFileCommand"/> a specific, distinguishable-by-color image instead of
    /// the default overload's plain black one -- see <see cref="CreateSolidSource"/>'s own doc
    /// comment for why that matters.</summary>
    public static TxImageEditorPaneViewModel CreateEditor(IImageSource original, SstvModeDefinition mode, IImageFileLoader imageFileLoader)
    {
        // PathToReturn: AddImageFromFileCommand needs a real, non-null path/image to hand to
        // InsertImageElement -- FakeFilePickerService's own default is already non-null, but this
        // pins it explicitly rather than relying on that default silently staying non-null.
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/real-window-test-source.png" };
        return new(original, mode, new TransmitImagePreparer(FontPath), new MacroTextResolver(), new OperatorSettings(),
            new FakeRadioSessionService(), new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance,
            filePicker, imageFileLoader, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(),
            new ReadyRackViewModel(new FakeTemplateStore(), new FakeSettingsStore(), new FakeLocalizationService(), new FakeFilePickerService(), NullLogger<ReadyRackViewModel>.Instance));
    }

    /// <summary>Real Window + real production View. <see cref="TxImageEditorPaneView"/>'s own
    /// <c>Loaded</c> handler calls <c>ApplyFitFromViewport</c>, which recomputes <c>ZoomFactor</c>
    /// from the ScrollViewer's real (window-size-dependent) bounds -- pinned back to 1.0 explicitly
    /// right after, so a caller's own coordinate math (which assumes a known, stable
    /// <c>CanvasDisplayWidth/Height</c>) isn't at the mercy of window-size-dependent Fit math (auditor
    /// plan-review finding, perspective-corner-drag round).</summary>
    public static (Window Window, TxImageEditorPaneViewModel Vm, Canvas EditorCanvas) BuildRealWindow(
        IImageSource original, SstvModeDefinition? mode = null, int windowWidth = 1920, int windowHeight = 1200) =>
        BuildRealWindowForVm(CreateEditor(original, mode ?? TestMode), windowWidth, windowHeight);

    /// <summary>Same real Window + real View wiring as <see cref="BuildRealWindow"/>, for a caller
    /// that needs a non-default <see cref="CreateEditor(IImageSource,SstvModeDefinition,IImageFileLoader)"/>
    /// overload (e.g. a custom <c>IImageFileLoader</c>) instead of that method's own plain-black
    /// default.</summary>
    public static (Window Window, TxImageEditorPaneViewModel Vm, Canvas EditorCanvas) BuildRealWindowForVm(
        TxImageEditorPaneViewModel vm, int windowWidth = 1920, int windowHeight = 1200)
    {
        EnsureAppServices();

        var view = new TxImageEditorPaneView { DataContext = vm };
        var window = new Window { Content = view, Width = windowWidth, Height = windowHeight };
        window.Show();
        PumpDispatcher();

        vm.ZoomFactor = 1.0;
        PumpDispatcher();

        var editorCanvas = view.FindControl<Canvas>("EditorCanvas")
            ?? throw new InvalidOperationException("EditorCanvas not found in the real View's visual tree.");

        return (window, vm, editorCanvas);
    }

    public static void PumpDispatcher()
    {
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }

        // Real (non-headless-drawing) rendering: input hit-testing in Avalonia 11.3.12 resolves
        // against the COMPOSITOR's own committed scene, a separate pass from the logical dispatcher
        // queue RunJobs() above flushes. Without forcing a render-timer tick, a just-laid-out control
        // is logically positioned correctly but not yet visible to InputHitTest -- confirmed
        // empirically during the perspective-corner-drag work; Avalonia's own CaptureRenderedFrame
        // helper does this same RunJobs()-then-ForceRenderTimerTick() sequence internally, confirming
        // it's an established requirement, not an invented workaround.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
}
