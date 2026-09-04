using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using ScanlineStudio.UI.ViewModels;
using ScanlineStudio.UI.Views;

namespace ScanlineStudio.UI;

// Fully qualified: bare "Application" became ambiguous with the ScanlineStudio.Application project's own
// namespace once it gained real content (both are reachable as "Application" from within ScanlineStudio.UI,
// nested under the same root ScanlineStudio namespace) -- this is Avalonia's Application, not a typo.
public partial class App : Avalonia.Application
{
    // Set by ScanlineStudio.Host before BuildAvaloniaApp().Start*() runs. Per spec/01-architecture.md,
    // ScanlineStudio.Host is the only composition root; this is just the hand-off point for the
    // already-built container. Null in the XAML previewer, which never calls into Host.
    //
    // Exactly two call sites are sanctioned to read this: this bootstrap resolve below, and
    // ScanlineStudio.UI.Localization.TranslateExtension (the XAML loader instantiates markup extensions
    // itself, with no constructor-injection route -- see that class's own doc comment). Every
    // other view-model must use real constructor injection; this is not a general service locator.
    public static IServiceProvider? Services { get; set; }

    // Set by ScanlineStudio.Host's Program.cs, beside its existing culture-restore block, before
    // BuildAvaloniaApp().Start*() runs -- same hand-off shape as Services above, but a plain static
    // field rather than an instance property, because Program.cs's read happens BEFORE
    // SetupWithLifetime constructs this App instance at all; there is no instance to set
    // RequestedThemeVariant on yet at that point. Null (unset, or the persisted setting failed to
    // load) means "keep App.axaml's own hardcoded Light default" -- OnFrameworkInitializationCompleted
    // below only overrides it when this is non-null.
    public static ThemeVariant? StartupThemeVariant { get; set; }

    // Single source of truth for the app's default font family URI: BuildAvaloniaApp below is the
    // actual load-bearing site (a typo here silently falls back to a system font, no exception, no
    // failing build -- ScanlineStudio.UI.FontTests.IndustryFontResolutionTests references THIS
    // constant rather than its own copy, specifically so a regression here is caught by that test
    // instead of only by eyeballing a running window).
    public const string DefaultFontFamilyUri = "avares://ScanlineStudio.UI/Assets/Fonts/Barlow#Barlow";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (StartupThemeVariant is { } themeVariant)
        {
            RequestedThemeVariant = themeVariant;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = (Services ?? throw new InvalidOperationException(
                        "App.Services was never set -- ScanlineStudio.Host must assign it before starting the lifetime."))
                    .GetRequiredService<MainViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Avalonia configuration, don't remove; also used by visual designer and by ScanlineStudio.Host's Program.cs.
    //
    // Default font family is embedded Barlow, not Avalonia's bundled Inter (mockups/guidance/
    // LAYOUT-SPEC.md's Industry design system): confirmed via a real (non-headless-drawing) Skia
    // probe that FontManagerOptions.DefaultFamilyName correctly drives FontFamily.Default
    // resolution, so every un-styled TextBlock (and anything a Style selector's FontFamily setter
    // can't reach -- ComboBox/ContextMenu/Flyout popups are separate top-levels, not children of
    // the Window they visually appear under) still renders in Barlow rather than silently falling
    // back to a system font.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions
            {
                DefaultFamilyName = DefaultFontFamilyUri,
            })
            .LogToTrace();
}
