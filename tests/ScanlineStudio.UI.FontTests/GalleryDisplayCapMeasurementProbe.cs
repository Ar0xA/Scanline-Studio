using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.UI.Tests;
using ScanlineStudio.UI.ViewModels;
using Xunit.Sdk;

namespace ScanlineStudio.UI.FontTests;

/// <summary>Opt-in gate for <see cref="GalleryDisplayCapMeasurementProbe"/>: a timing probe, not a
/// pass/fail test -- it reports its table through the failure message, so it always "fails" when run.
/// Same discoverer as <c>[AvaloniaFact]</c> (that attribute is sealed), so the body still runs on the
/// headless UI thread.</summary>
[XunitTestCaseDiscoverer("Avalonia.Headless.XUnit.AvaloniaUIFactDiscoverer", "Avalonia.Headless.XUnit")]
public sealed class GalleryMeasurementProbeFactAttribute : FactAttribute
{
    public const string OptInVariable = "SCANLINE_RUN_GALLERY_PROBE";

    public GalleryMeasurementProbeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
        {
            Skip = $"Set {OptInVariable}=1 to run the Gallery timing probe. It reports through the "
                + "failure message by design, so it always 'fails' when run.";
        }
    }
}
/// <summary>Real-Skia window timings for the Gallery's display-capped grid at 1 500 / 5 000 /
/// 20 000 history rows, 300 and 1 500 displayed: a no-change refresh, the per-received-frame refresh
/// (one new row, cache warm), and three publishes (same set, to empty, refill), each with layout. The
/// ListBox mirrors <c>MainWindow.axaml</c>'s Gallery grid (outer ScrollViewer, UniformGrid Columns=6,
/// Image bound to Thumbnail + mode badge, IndustryListBoxItemTheme) -- hosting the full MainWindow
/// needs the whole MainViewModel graph. Decision threshold: 50 ms per measurement.</summary>
public sealed class GalleryDisplayCapMeasurementProbe
{
    private const int Repeats = 5;

    [GalleryMeasurementProbeFact]
    public async Task Measure()
    {
        RealWindowTestSupport.EnsureAppServices();
        var report = new StringBuilder();
        report.AppendLine("median ms of " + Repeats + "; refresh = UI-thread RefreshAsync, layout = dispatcher + layout + render tick");
        report.AppendLine("  rows shown | steady refresh + layout | 1-new-row refresh + layout | same set | to empty | refill");

        foreach (var rows in new[] { 1_500, 5_000, 20_000 })
        {
            foreach (var shown in new[] { 300, 1_500 })
            {
                report.AppendLine(await MeasureOneAsync(rows, shown));
            }
        }

        Assert.Fail(report.ToString());
    }

    private static async Task<string> MeasureOneAsync(int rows, int shown)
    {
        var noon = new DateTimeOffset(DateTime.Today.AddHours(12));
        var entries = Enumerable.Range(0, rows)
            .Select(i => new ReceiveHistoryEntry($"e{i:D6}", noon.AddSeconds(-i), i % 3 == 0 ? "robot36" : "scottie-s1", $"/tmp/e{i:D6}.png", null, ReceiveDecodeState.Completed))
            .ToList();
        var store = new FakeReceiveHistoryStore
        {
            EntriesToReturn = entries,
            // A real-size 240x192 thumbnail (320x256 source scaled to ThumbnailMaxDimension).
            ThumbnailToReturn = new ArrayImageSource(240, 192, new Rgb24[240 * 192]),
        };
        var vm = CreateVm(store);
        vm.ShowTodayOnly = false;
        for (var limit = 300; limit < shown; limit += 300)
        {
            vm.ShowOlderCommand.Execute(null);
        }

        var window = BuildWindow(vm);
        window.Show();
        Pump(window);

        var steadyRefresh = new List<double>();
        var steadyLayout = new List<double>();
        var frameRefresh = new List<double>();
        var frameLayout = new List<double>();
        var samePublish = new List<double>();
        var emptyPublish = new List<double>();
        var refillPublish = new List<double>();
        for (var r = 0; r < Repeats; r++)
        {
            // Steady state: nothing changed since the last refresh.
            var sw = Stopwatch.StartNew();
            await vm.RefreshCommand.ExecuteAsync(null);
            steadyRefresh.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart();
            Pump(window);
            steadyLayout.Add(sw.Elapsed.TotalMilliseconds);

            // The per-received-frame case: one new row on top, cache warm otherwise.
            store.EntriesToReturn.Add(new ReceiveHistoryEntry($"new{r}", noon.AddSeconds(r + 1), "robot36", $"/tmp/new{r}.png", null, ReceiveDecodeState.Completed));
            sw.Restart();
            await vm.RefreshCommand.ExecuteAsync(null);
            frameRefresh.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart();
            Pump(window);
            frameLayout.Add(sw.Elapsed.TotalMilliseconds);

            // Publishes, each with its layout. Search keystrokes publish the same way once their
            // debounce fires; the filter chips are used because they publish synchronously.
            // Every row is unlogged: identical set (e.g. a keystroke that still matches everything).
            sw.Restart();
            vm.FilterUnloggedOnly = !vm.FilterUnloggedOnly;
            Pump(window);
            samePublish.Add(sw.Elapsed.TotalMilliseconds);

            // No row is flagged: the grid empties (a no-match keystroke).
            sw.Restart();
            vm.FilterFlaggedOnly = true;
            Pump(window);
            emptyPublish.Add(sw.Elapsed.TotalMilliseconds);

            // Back to the full displayed set (clearing a no-match search).
            sw.Restart();
            vm.FilterFlaggedOnly = false;
            Pump(window);
            refillPublish.Add(sw.Elapsed.TotalMilliseconds);
        }

        if (vm.FilteredEntries.Count != shown)
        {
            throw new InvalidOperationException($"expected {shown} displayed, got {vm.FilteredEntries.Count}");
        }

        window.Close();
        return $"{rows,6} {shown,5} | {Median(steadyRefresh),7:F1} + {Median(steadyLayout),6:F1} | {Median(frameRefresh),7:F1} + {Median(frameLayout),6:F1} | {Median(samePublish),9:F1} | {Median(emptyPublish),9:F1} | {Median(refillPublish),9:F1}";
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    private static RxHistoryPaneViewModel CreateVm(FakeReceiveHistoryStore store) =>
        new(
            store,
            new FakeLocalizationService(),
            NullLogger<RxHistoryPaneViewModel>.Instance,
            new FakeLogbookSessionService(),
            NullLogger<QsoLinkWindowViewModel>.Instance,
            new FakeReceivedFrameExporter(),
            new FakeFilePickerService(),
            new FakeSettingsStore(),
            new FakeUrlLauncher(),
            new FakeClipboardImageService(),
            NullLogger<ImageViewerWindowViewModel>.Instance,
            new FakeRxAudioAutoSaver(),
            new FakeRxStationIdAttacher());

    private static Window BuildWindow(RxHistoryPaneViewModel vm)
    {
        var listBox = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            ItemsPanel = new FuncTemplate<Panel?>(() => new UniformGrid { Columns = 6 }),
            ItemTemplate = new FuncDataTemplate<RxHistoryEntryViewModel>((_, _) =>
            {
                var image = new Image { Stretch = Stretch.Uniform };
                image.Bind(Image.SourceProperty, new Binding(nameof(RxHistoryEntryViewModel.Thumbnail)));
                RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
                var badgeText = new TextBlock();
                badgeText.Bind(TextBlock.TextProperty, new Binding("Entry.ModeId"));
                var badge = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(4),
                    IsHitTestVisible = false,
                    Child = badgeText,
                };
                return new Border { Margin = new Thickness(0, 0, 7, 7), Child = new Panel { Children = { image, badge } } };
            }),
        };
        if (Avalonia.Application.Current!.TryFindResource("IndustryListBoxItemTheme", out var theme) && theme is ControlTheme controlTheme)
        {
            listBox.ItemContainerTheme = controlTheme;
        }

        listBox.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(RxHistoryPaneViewModel.FilteredEntries)));
        listBox.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(RxHistoryPaneViewModel.SelectedEntry)) { Mode = BindingMode.TwoWay });

        return new Window
        {
            Width = 1400,
            Height = 900,
            DataContext = vm,
            Content = new ScrollViewer { Content = listBox },
        };
    }
}
