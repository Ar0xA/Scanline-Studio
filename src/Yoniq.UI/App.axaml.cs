using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Yoniq.UI.ViewModels;
using Yoniq.UI.Views;

namespace Yoniq.UI;

// Fully qualified: bare "Application" became ambiguous with the Yoniq.Application project's own
// namespace once it gained real content (both are reachable as "Application" from within Yoniq.UI,
// nested under the same root Yoniq namespace) -- this is Avalonia's Application, not a typo.
public partial class App : Avalonia.Application
{
    // Set by Yoniq.Host before BuildAvaloniaApp().Start*() runs. Per spec/01-architecture.md,
    // Yoniq.Host is the only composition root; this is just the hand-off point for the
    // already-built container. Null in the XAML previewer, which never calls into Host.
    //
    // Exactly two call sites are sanctioned to read this: this bootstrap resolve below, and
    // Yoniq.UI.Localization.TranslateExtension (the XAML loader instantiates markup extensions
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
                        "App.Services was never set -- Yoniq.Host must assign it before starting the lifetime."))
                    .GetRequiredService<MainViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Avalonia configuration, don't remove; also used by visual designer and by Yoniq.Host's Program.cs.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
