using Avalonia;
using Avalonia.Headless;
using Yoniq.UI.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Yoniq.UI.Tests;

public static class TestAppBuilder
{
    // Fully qualified: bare "Application" resolves to the sibling Yoniq.Application namespace from
    // anywhere under the shared Yoniq root (same gotcha App.axaml.cs and FilePickerService hit first).
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Avalonia.Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
