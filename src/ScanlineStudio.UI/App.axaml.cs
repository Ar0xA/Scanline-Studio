using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
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
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
