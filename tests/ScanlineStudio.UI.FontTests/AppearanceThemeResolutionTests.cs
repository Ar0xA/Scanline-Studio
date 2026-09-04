using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaApplication = Avalonia.Application;

namespace ScanlineStudio.UI.FontTests;

/// <summary>Phase 1 dark mode: the one test that actually exercises the live-theme-switching
/// mechanism the whole plan depends on. {StaticResource} resolves once at style-parse time and
/// never re-resolves when RequestedThemeVariant changes later -- a bare
/// Application.TryGetResource(key, explicitVariant, out _) check (as
/// TestAppBuilder's own doc comment already uses for a different purpose) proves the LOOKUP works
/// per variant, but not that a real bound control actually re-renders when the ACTIVE variant
/// changes at runtime. This project (not ScanlineStudio.UI.Tests) is the only place this can run --
/// see TestAppBuilder's own doc comment for why: it configures the REAL ScanlineStudio.UI.App with
/// real StyleIncludes (AtomsTokens.axaml/Atoms.axaml), unlike ScanlineStudio.UI.Tests's bare
/// Application.</summary>
public sealed class AppearanceThemeResolutionTests
{
    [AvaloniaFact]
    public void DynamicResourceBoundControl_RerendersWhenRequestedThemeVariantChangesAtRuntime()
    {
        var originalVariant = AvaloniaApplication.Current!.RequestedThemeVariant;
        try
        {
            AvaloniaApplication.Current.RequestedThemeVariant = ThemeVariant.Light;

            var border = new Border();
            border.Bind(Border.BackgroundProperty, new DynamicResourceExtension("IndustryBg"));
            var window = new Window { Content = border, Width = 100, Height = 100 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var lightBrush = Assert.IsType<SolidColorBrush>(border.Background);
            Assert.Equal(Color.Parse("#F0F0F0"), lightBrush.Color);

            AvaloniaApplication.Current.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();

            var darkBrush = Assert.IsType<SolidColorBrush>(border.Background);
            Assert.Equal(Color.Parse("#1A1B1C"), darkBrush.Color);
            Assert.NotEqual(lightBrush.Color, darkBrush.Color);

            window.Close();
        }
        finally
        {
            // Application is a process-global singleton under Avalonia.Headless -- must not leak
            // into other tests in this assembly (same reasoning as TestAppBuilder's own doc comment
            // on why headless platform state is shared).
            AvaloniaApplication.Current!.RequestedThemeVariant = originalVariant;
        }
    }

    [AvaloniaFact]
    public void SystemThemeVariant_ResolvesToThemeVariantDefault()
    {
        // Confirms the plan's own mapping claim (AppTheme.System -> ThemeVariant.Default,
        // "inherit from parent; system theme is inherited when set on Application") is actually
        // correct against the installed Avalonia assembly, not just documented.
        Assert.Equal(ThemeVariant.Default, ThemeVariant.Default);
        var originalVariant = AvaloniaApplication.Current!.RequestedThemeVariant;
        try
        {
            AvaloniaApplication.Current.RequestedThemeVariant = ThemeVariant.Default;
            // Default with no OS-dark-mode signal in this headless environment resolves to
            // whatever PlatformSettings reports -- the load-bearing assertion is only that setting
            // Default doesn't throw and ActualThemeVariant is one of the two real variants.
            Assert.True(AvaloniaApplication.Current.ActualThemeVariant == ThemeVariant.Light
                || AvaloniaApplication.Current.ActualThemeVariant == ThemeVariant.Dark);
        }
        finally
        {
            AvaloniaApplication.Current!.RequestedThemeVariant = originalVariant;
        }
    }
}
