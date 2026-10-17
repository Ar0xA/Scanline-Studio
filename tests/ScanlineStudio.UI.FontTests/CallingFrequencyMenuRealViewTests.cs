using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Localization;
using ScanlineStudio.UI.Settings;
using ScanlineStudio.UI.Tests;
using ScanlineStudio.UI.ViewModels;
using ScanlineStudio.UI.Views;
using Xunit;
using static ScanlineStudio.UI.FontTests.RealWindowTestSupport;

namespace ScanlineStudio.UI.FontTests;

/// <summary>The header's calling-frequency menu in the real <see cref="RadioHeaderView"/>: a real click opens it, the
/// band and entry container themes bind, and an entry's click reaches CAT; plus the width it costs the Favourites card.</summary>
public sealed class CallingFrequencyMenuRealViewTests(ITestOutputHelper output)
{
    private static (Window Window, RadioStatusViewModel Vm, FakeRadioSessionService Session) Show(double width, ILocalizationService? localization = null)
    {
        EnsureAppServices();
        var session = new FakeRadioSessionService
        {
            Capabilities = RadioCapabilities.SetBandwidth,
            Presets =
            [
                new FrequencyPreset("20m SSTV", 14_230_000, RadioMode.Usb), new FrequencyPreset("40m SSTV", 7_171_000, RadioMode.Lsb),
                new FrequencyPreset("80m EU", 3_730_000, RadioMode.Lsb), new FrequencyPreset("2m FM", 145_500_000, RadioMode.Fm),
                new FrequencyPreset("10m", 28_680_000, RadioMode.Usb), new FrequencyPreset("15m", 21_340_000, RadioMode.Usb),
            ],
        };
        var vm = new RadioStatusViewModel(session, new FakeSstvSessionService(), localization ?? new FakeLocalizationService(),
            new FakeAppearanceSettingsService(), NullLogger<RadioStatusViewModel>.Instance);
        var window = new Window { Content = new RadioHeaderView { DataContext = vm }, Width = width, Height = 240 };
        window.Show();
        PumpDispatcher();
        return (window, vm, session);
    }

    private static Button MenuButton(Window window) => ((RadioHeaderView)window.Content!).FindControl<Button>("CallingFrequenciesButton")!;

    [AvaloniaFact]
    public async Task ARealClick_OpensTheBands_AndAnEntryClick_TunesFrequencyModeAndWidth()
    {
        var (window, vm, session) = Show(1920);
        Button button = MenuButton(window);
        Point center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;

        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        PumpDispatcher();

        var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
        Assert.True(flyout.IsOpen);
        var presenter = Assert.IsType<MenuFlyoutPresenter>(GetPopup(flyout).Child);
        MenuItem[] bands = [.. presenter.GetLogicalChildren().OfType<MenuItem>()];
        Assert.Equal(12, bands.Length);
        Assert.Equal("20 m", bands[4].Header);

        bands[4].IsSubMenuOpen = true;
        PumpDispatcher();
        MenuItem[] entries = [.. bands[4].GetLogicalChildren().OfType<MenuItem>()];
        Assert.Equal(5, entries.Length);
        MenuItem drm = entries.Single(e => ((CallingFrequencyItemViewModel)e.DataContext!).Entry.FrequencyHz == 14_233_000);
        Assert.Equal("RadioStatus.CallingFrequencies.ItemFormat", drm.Header);
        Assert.Same(vm.ApplyCallingFrequencyCommand, drm.Command);

        drm.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitUntilAsync(() => session.SetBandwidthCalls.Count == 1);

        Assert.Equal([14_233_000L], session.SetFrequencyCalls);
        Assert.Equal([RadioMode.Usb], session.SetModeCalls);
        Assert.Equal([(int?)2800], session.SetBandwidthCalls);
        window.Close();
    }

    /// <summary>Real English strings (the fake localizer renders key names, which would size every control wrongly);
    /// App.Services is process-wide, so it is restored afterwards.</summary>
    [AvaloniaTheory]
    [InlineData(1920, false)]
    [InlineData(1280, false)]
    [InlineData(1920, true)]
    [InlineData(1280, true)]
    public async Task TheButton_CostsTheFavouritesCardOnlyItsOwnWidth(double windowWidth, bool large)
    {
        EnsureAppServices();
        var originalServices = App.Services;
        if (large)
            App.ApplyFontScale(AppFontScale.Large);
        try
        {
            var localization = new JsonLocalizationService(Path.Combine(RepoRoot(), "assets", "locale"), NullLogger<JsonLocalizationService>.Instance);
            await localization.SetCultureAsync(CultureInfo.GetCultureInfo("en"));
            App.Services = new ServiceCollection().AddSingleton<ILocalizationService>(localization).BuildServiceProvider();
            var (window, _, session) = Show(windowWidth, localization);
            session.Push(new RadioState(145_800_000, RadioMode.Fm, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
            PumpDispatcher();
            Button button = MenuButton(window);
            double with = FavouritesWidth(window);
            int rowWith = FirstRowPresets(window);
            button.IsVisible = false;
            PumpDispatcher();
            double without = FavouritesWidth(window);
            int rowWithout = FirstRowPresets(window);
            output.WriteLine($"{windowWidth}px {(large ? "Large" : "Normal")}: Favourites {without:F0} -> {with:F0} px (button {button.Bounds.Width:F0} px), first-row presets {rowWithout} -> {rowWith}");

            Assert.True(with > 0, "Favourites card collapsed");
            Assert.InRange(without - with, 0, button.Bounds.Width + 14 + 0.5);
            if (windowWidth == 1920)
                Assert.Equal(rowWithout, rowWith);
            window.Close();
        }
        finally
        {
            App.Services = originalServices;
            if (large)
                App.ApplyFontScale(AppFontScale.Normal);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScanlineStudio.sln")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    private static double FavouritesWidth(Window window)
    {
        Grid header = window.GetVisualDescendants().OfType<Grid>().First(g => g.ColumnDefinitions.Count == 3 && g.ColumnDefinitions[1].Width.IsStar);
        return header.Children.Cast<Control>().Single(c => Grid.GetColumn(c) == 1).Bounds.Width;
    }

    private static int FirstRowPresets(Window window)
    {
        WrapPanel panel = window.GetVisualDescendants().OfType<WrapPanel>().First(p => p.Children.Count == 6);
        double top = panel.Children.Min(c => c.Bounds.Y);
        return panel.Children.Count(c => c.Bounds.Y == top);
    }

    private static Popup GetPopup(MenuFlyout flyout) =>
        (Popup)typeof(PopupFlyoutBase).GetProperty("Popup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!.GetValue(flyout)!;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
            PumpDispatcher();
        }

        Assert.True(condition(), "condition not reached");
    }
}
