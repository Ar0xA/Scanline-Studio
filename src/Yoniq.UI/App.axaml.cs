using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Yoniq.UI.ViewModels;
using Yoniq.UI.Views;

namespace Yoniq.UI;

public partial class App : Application
{
    // Set by Yoniq.Host before BuildAvaloniaApp().Start*() runs. Per spec/01-architecture.md,
    // Yoniq.Host is the only composition root; this is just the hand-off point for the
    // already-built container. Null in the XAML previewer, which never calls into Host.
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
                DataContext = Services?.GetRequiredService<MainViewModel>() ?? new MainViewModel(),
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
