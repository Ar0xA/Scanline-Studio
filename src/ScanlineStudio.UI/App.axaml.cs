using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using ScanlineStudio.UI.Settings;
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

    // Same hand-off shape as StartupThemeVariant above, for Phase 2's font-scale axis.
    public static AppFontScale? StartupFontScale { get; set; }

    // Phase 2 font-scale mechanism: ThemeVariant/ThemeDictionaries is a single axis already spent on
    // Light/Dark (an Application has one RequestedThemeVariant), so font-scale needs its own
    // independent live-switchable mechanism -- a manual swap of this owned container dictionary's
    // OWN MergedDictionaries membership between the Normal/Large ResourceIncludes below. Deliberately
    // NOT declared in App.axaml: declaring it there would let the XAML loader construct its own
    // ResourceInclude instance, distinct from any instance held here in C# -- Remove/Clear calls
    // against the wrong instance silently no-op (confirmed via a throwaway reflection probe against
    // the installed Avalonia 11.3.12 package: MergedDictionaries.Clear() then re-Add() of the SAME
    // instance works safely across repeated cycles, but only because this class owns both the
    // container and the two ResourceInclude instances end to end -- no competing XAML-constructed
    // copy ever exists).
    //
    // INSTANCE fields, not static (a real bug found while writing this phase's own FontTests
    // coverage): a ResourceDictionary/ResourceInclude can only ever have ONE owning IResourceHost --
    // Avalonia.Headless.XUnit constructs a genuinely FRESH Application instance per [AvaloniaFact]
    // test (confirmed: HeadlessUnitTestSession.EnsureIsolatedApplication() calls Initialize() again
    // for every test in the same process), so a `static` field here tried to re-add the FIRST test's
    // already-owned instances to the SECOND test's Application, throwing "The ResourceDictionary
    // already has a parent." A real desktop run only ever constructs one App instance per process,
    // so this specific failure never reaches production -- but the underlying design was still wrong
    // (a static field tied to instance lifetime), caught here rather than shipped. Each Initialize()
    // call now creates its own fresh container/includes, tied to that instance's own lifetime.
    private readonly ResourceDictionary _fontScaleContainer = new();
    private readonly ResourceInclude _normalFontScaleInclude = new(new Uri("avares://ScanlineStudio.UI/"))
    {
        Source = new Uri("avares://ScanlineStudio.UI/Styles/AtomsFontScaleNormal.axaml"),
    };
    private readonly ResourceInclude _largeFontScaleInclude = new(new Uri("avares://ScanlineStudio.UI/"))
    {
        Source = new Uri("avares://ScanlineStudio.UI/Styles/AtomsFontScaleLarge.axaml"),
    };

    /// <summary>The single owned swap point for the font-scale axis -- called from both
    /// <see cref="OnFrameworkInitializationCompleted"/> (startup) and
    /// <c>OptionsWindowViewModel.SaveCoreUnguardedAsync</c>'s live-apply block (on the UI thread in
    /// both cases), and by this phase's own FontTests coverage directly. Static (matching
    /// <see cref="StartupThemeVariant"/>'s own static-hand-off shape, and callable from
    /// <c>OptionsWindowViewModel</c> without threading an App-instance reference through it) but
    /// delegates to the CURRENT Application instance's own container/includes -- see those fields'
    /// own doc comment for why those can't be static themselves. One owned instance method, not
    /// duplicated remove/add bookkeeping at each call site, because a MergedDictionaries swap is
    /// stateful (unlike RequestedThemeVariant's stateless enum assignment) -- two independent copies
    /// of the same remove/add logic risk drifting (e.g. one path forgetting to remove the old
    /// dictionary first).
    ///
    /// Uses `as`, not a hard cast, and no-ops when `Current` isn't this subclass -- unlike Phase 1's
    /// theme axis (RequestedThemeVariant/ActualThemeVariant are base Avalonia.Application properties,
    /// so it works against ANY Application instance, real or a test harness's bare one),
    /// font-scale's state genuinely only exists on THIS subclass (the container/include fields
    /// above), so a bare-Application test host (ScanlineStudio.UI.Tests's own TestAppBuilder, unlike
    /// ScanlineStudio.UI.FontTests's real one) has nothing to apply to. A hard cast here would throw
    /// on every OptionsWindowViewModel Save in that test project's suite -- silently caught by
    /// SaveCoreUnguardedAsync's own try/catch (no test failure), but spamming a real logged error on
    /// every single save-triggering test. No-op is the honest behavior: there is no real font-scale
    /// state to mutate without a real App instance.</summary>
    public static void ApplyFontScale(AppFontScale scale) => (Current as App)?.ApplyFontScaleCore(scale);

    private void ApplyFontScaleCore(AppFontScale scale)
    {
        _fontScaleContainer.MergedDictionaries.Clear();
        _fontScaleContainer.MergedDictionaries.Add(scale == AppFontScale.Large ? _largeFontScaleInclude : _normalFontScaleInclude);
    }

    // Single source of truth for the app's default font family URI: BuildAvaloniaApp below is the
    // actual load-bearing site (a typo here silently falls back to a system font, no exception, no
    // failing build -- ScanlineStudio.UI.FontTests.IndustryFontResolutionTests references THIS
    // constant rather than its own copy, specifically so a regression here is caught by that test
    // instead of only by eyeballing a running window).
    public const string DefaultFontFamilyUri = "avares://ScanlineStudio.UI/Assets/Fonts/Barlow#Barlow";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Font-scale container must exist and resolve to a real value (Normal) before any control
        // parses -- seeded here, not in OnFrameworkInitializationCompleted below, so the XAML
        // previewer/design-time (which never reaches OnFrameworkInitializationCompleted) still
        // resolves every {DynamicResource Industry*FontSize/Height/...} reference correctly. Calls
        // the instance method directly (not the static ApplyFontScale(...) -> Current! wrapper) --
        // this runs from within the constructing instance itself, before relying on
        // Application.Current being set to it is necessary.
        Resources.MergedDictionaries.Add(_fontScaleContainer);
        ApplyFontScaleCore(AppFontScale.Normal);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (StartupThemeVariant is { } themeVariant)
        {
            RequestedThemeVariant = themeVariant;
        }

        if (StartupFontScale is { } fontScale)
        {
            // Instance method directly, not the static ApplyFontScale(...) -> (Current as App)?
            // wrapper -- this runs from within the constructing instance itself (same reasoning as
            // Initialize()'s own seed call), no reason to route through a soft cast of Current here.
            ApplyFontScaleCore(fontScale);
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
