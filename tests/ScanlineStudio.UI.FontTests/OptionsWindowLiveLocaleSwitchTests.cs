using System.IO;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Localization;
using ScanlineStudio.UI.Localization;
using ScanlineStudio.UI.Tests;
using ScanlineStudio.UI.ViewModels;
using ScanlineStudio.UI.Views;
using Xunit;

namespace ScanlineStudio.UI.FontTests;

/// <summary>Regression coverage for a real, user-reported bug -- clicking Apply after changing
/// Options > General > Language left the Options dialog's own visible text (tab headers, field
/// labels) frozen in the old language, even though <see cref="ILocalizationService.CurrentCulture"/>
/// and <see cref="ILocalizationService.GetString"/> both correctly reflected the switch. Root cause:
/// <c>TranslateExtension.ProvideValue</c> handed a freshly-constructed <c>TranslateBindingSource</c>
/// straight to <c>Binding.Source</c> with nothing else keeping it alive, so almost every bound
/// control's translation source was garbage-collected before it ever got used, freezing that control
/// in whichever language was active at construction time -- see <see cref="TranslateExtension"/>'s
/// own doc comment for the fix. This test uses the REAL production <see cref="OptionsWindowView"/>
/// and a REAL <see cref="JsonLocalizationService"/> reading the repo's actual <c>assets/locale/</c>
/// files -- a fake/hand-rolled localization service would not have exercised the actual
/// <c>{loc:Translate}</c> markup-extension machinery this bug lived in.</summary>
public sealed class OptionsWindowLiveLocaleSwitchTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScanlineStudio.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    private static void PumpDispatcher()
    {
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    [AvaloniaFact]
    public async Task ClickingApply_UpdatesTheOptionsWindowsOwnTabHeaderAndFieldLabels()
    {
        // App.Services is process-wide, shared static state (RealWindowTestSupport.EnsureAppServices's
        // own doc comment) -- every other test in this assembly that opens a real production View
        // expects it to keep pointing at a FakeLocalizationService fixed to English. Save/restore it
        // around this test's real culture switch so that switch can't leak into unrelated tests that
        // happen to run afterward in the same process (this genuinely happened once: 7 unrelated
        // TxImageEditorRealUiSmokeTests failures, all German menu text, before this restore existed).
        var originalServices = App.Services;
        try
        {
            var localeDir = Path.Combine(FindRepoRoot(), "assets", "locale");
            var localization = new JsonLocalizationService(localeDir, NullLogger<JsonLocalizationService>.Instance);
            App.Services = new ServiceCollection().AddSingleton<ILocalizationService>(localization).BuildServiceProvider();

            var settingsStore = new FakeSettingsStore();
            var vm = new OptionsWindowViewModel(
                new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance),
                localization, new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore,
                new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(),
                new FakeSstvSessionService(), new FakeSerialPortEnumerator(), new FakeReceiveHistoryStore(),
                new FakeAppLocationsService(), new FakeApplicationRestarter(), new FakeAppearanceSettingsService(),
                NullLogger<OptionsWindowViewModel>.Instance);

            var window = new OptionsWindowView { DataContext = vm };
            window.Show();
            PumpDispatcher();

            var generalTab = window.GetVisualDescendants().OfType<TabControl>().First().Items.OfType<TabItem>().First();
            var languageLabel = window.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Classes.Contains("IndustryRowLabel") && t.Text == "Language");

            vm.SelectedCulture = System.Globalization.CultureInfo.GetCultureInfo("de");
            await vm.ApplyCommand.ExecuteAsync(null);
            PumpDispatcher();

            Assert.Equal("de", localization.CurrentCulture.TwoLetterISOLanguageName);
            Assert.Equal("Allgemein", generalTab.Header as string ?? generalTab.Header?.ToString());
            Assert.Equal("Sprache", languageLabel.Text);

            // yoniq-principal review: a real OptionsWindowView proves the ROOTED path works (the
            // assertions above), but can't by itself rule out every source having silently landed in
            // the permanent UNROOTED fallback instead (which would also pass those assertions, while
            // leaking -- the fallback only exists for a use-site Avalonia's XAML loader doesn't hand
            // an IProvideValueTarget for, which this codebase has none of today). Confirms the fast
            // path, not just the outcome.
            var fallback = typeof(TranslateExtension)
                .GetField("UnrootedFallbackSources", BindingFlags.Static | BindingFlags.NonPublic)?
                .GetValue(null) as System.Collections.ICollection;
            Assert.Equal(0, fallback?.Count ?? -1);

            window.Close();
        }
        finally
        {
            App.Services = originalServices;
        }
    }
}
