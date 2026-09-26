using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using ScanlineStudio.UI.FontTests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace ScanlineStudio.UI.FontTests;

public static class TestAppBuilder
{
    // Perspective-transform corner-drag verification (PROJECT_BRIEF.md's own tracked gap):
    // Configure<ScanlineStudio.UI.App>() instead of the bare Avalonia.Application this project used
    // before -- real production views (TxImageEditorPaneView) need App.axaml's own styles (FluentTheme,
    // ColorPicker StyleInclude) and resources (IndustryAccent and the other Industry design-system
    // brushes the corner handles/atoms reference), which a bare Application never loads. Confirmed
    // empirically (a throwaway probe test, not assumed) that App itself loads cleanly here:
    // Application.Current.ApplicationLifetime is null under Avalonia.Headless.XUnit (not
    // IClassicDesktopStyleApplicationLifetime), so App.OnFrameworkInitializationCompleted's own
    // MainWindow/App.Services branch never runs and never throws -- this project still constructs its
    // own test Windows directly, same as before. App.axaml's styles/resources DO load (verified:
    // Application.Current.Styles.Count == 4, TryFindResource("IndustryAccent", ...) resolves to
    // #ff0f62a8) -- Initialize()'s AvaloniaXamlLoader.Load(this) call is independent of that branch.
    // FontManagerOptions.DefaultFamilyName is set explicitly here (not implicitly via App's own
    // BuildAvaloniaApp(), which this project doesn't call) because App.BuildAvaloniaApp() also chains
    // .UsePlatformDetect(), which is wrong for a headless test process.
    //
    // A real production view under real Skia rendering still needs App.Services set (Localization.
    // TranslateExtension's own {loc:Translate} markup extension throws InvalidOperationException on a
    // null App.Services, and TxImageEditorPaneView.axaml uses it throughout) -- each test that
    // constructs one is responsible for that, not this shared builder (a per-test fake VS a
    // process-global one is a real per-test-suite tradeoff; see the perspective-drag tests' own doc
    // comment for the choice made there).
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<ScanlineStudio.UI.App>()
            .UseSkia()
            .UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .With(new FontManagerOptions { DefaultFamilyName = ScanlineStudio.UI.App.DefaultFontFamilyUri });
}
