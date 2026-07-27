using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Yoniq.Settings;
using Yoniq.UI;
using Yoniq.UI.ViewModels;

namespace Yoniq.Host;

// Composition root — see spec/01-architecture.md. This is the only place allowed to call `new`
// on concrete infrastructure types or register services with the DI container.
internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        var hostBuilder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        hostBuilder.Services.AddSingleton<ISettingsStore>(new JsonSettingsStore());
        hostBuilder.Services.AddTransient<MainViewModel>();

        var host = hostBuilder.Build();
        App.Services = host.Services;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() => App.BuildAvaloniaApp();
}
