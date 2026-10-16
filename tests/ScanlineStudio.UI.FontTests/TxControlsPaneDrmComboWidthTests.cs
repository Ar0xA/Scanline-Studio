using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Localization;
using ScanlineStudio.UI.Tests;
using ScanlineStudio.UI.ViewModels;
using ScanlineStudio.UI.Views;
using Xunit;
using static ScanlineStudio.UI.FontTests.RealWindowTestSupport;

namespace ScanlineStudio.UI.FontTests;

/// <summary>The DRM ComboBoxes in the TX pane's fixed 236 px column (MainWindow.axaml) show every option unclipped,
/// with the real English and German strings.</summary>
public sealed class TxControlsPaneDrmComboWidthTests
{
    private const double TxColumnWidth = 236;

    [AvaloniaFact]
    public async Task DrmComboBoxes_ShowEveryOptionUnclipped_AtTheTxColumnWidth_InEnglishAndGerman()
    {
        EnsureAppServices();
        var originalServices = App.Services;
        try
        {
            var localeDir = Path.Combine(FindRepoRoot(), "assets", "locale");
            var localization = new JsonLocalizationService(localeDir, NullLogger<JsonLocalizationService>.Instance);
            App.Services = new ServiceCollection().AddSingleton<ILocalizationService>(localization).BuildServiceProvider();

            foreach (var culture in new[] { "en", "de" })
            {
                await localization.SetCultureAsync(CultureInfo.GetCultureInfo(culture));
                AssertDrmCombosFit(localization, culture);
            }
        }
        finally
        {
            App.Services = originalServices;
        }
    }

    private static void AssertDrmCombosFit(ILocalizationService localization, string culture)
    {
        var vm = new TxControlsPaneViewModel(
            new FakeSstvSessionService { AvailableModes = [TestMode] }, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), localization, new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(),
            NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(),
            new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var window = new Window { Content = new TxControlsPaneView { DataContext = vm }, Width = TxColumnWidth, Height = 1600 };
        window.Show();
        vm.SelectedTarget = vm.AvailableTargets.OfType<DrmTarget>().Single();
        PumpDispatcher();
        window.GetVisualDescendants().OfType<Expander>().Single().IsExpanded = true;
        PumpDispatcher();

        var combos = window.GetVisualDescendants().OfType<ComboBox>()
            .Where(c => c.IsEffectivelyVisible && c.ItemsSource is IReadOnlyList<string>)
            .ToList();
        Assert.Equal(8, combos.Count);

        foreach (var combo in combos)
        {
            var items = (IReadOnlyList<string>)combo.ItemsSource!;
            for (var i = 0; i < items.Count; i++)
            {
                combo.SelectedIndex = i;
                PumpDispatcher();
                var shown = combo.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == items[i] && t.IsEffectivelyVisible);
                var natural = new TextBlock
                {
                    Text = shown.Text, FontFamily = shown.FontFamily, FontSize = shown.FontSize,
                    FontWeight = shown.FontWeight, FontStyle = shown.FontStyle, LetterSpacing = shown.LetterSpacing,
                };
                natural.Measure(Size.Infinity);
                Assert.True(
                    natural.DesiredSize.Width <= shown.Bounds.Width + 0.5,
                    $"[{culture}] '{items[i]}' needs {natural.DesiredSize.Width:0.#} px, has {shown.Bounds.Width:0.#} px");
            }
        }

        window.Close();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScanlineStudio.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
