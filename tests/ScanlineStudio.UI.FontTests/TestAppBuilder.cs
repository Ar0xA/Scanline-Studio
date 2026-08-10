using Avalonia;
using Avalonia.Headless;
using ScanlineStudio.UI.FontTests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace ScanlineStudio.UI.FontTests;

public static class TestAppBuilder
{
    // Real Skia + UseHeadlessDrawing=false: see the csproj comment for why this project needs a
    // real font manager instead of ScanlineStudio.UI.Tests's fast HeadlessFontManagerStub.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Avalonia.Application>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
