using Avalonia;
using Avalonia.Headless;
using ScanlineStudio.UI.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace ScanlineStudio.UI.Tests;

public static class TestAppBuilder
{
    // Fully qualified: bare "Application" resolves to the sibling ScanlineStudio.Application namespace from
    // anywhere under the shared ScanlineStudio root (same gotcha App.axaml.cs and FilePickerService hit first).
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Avalonia.Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
