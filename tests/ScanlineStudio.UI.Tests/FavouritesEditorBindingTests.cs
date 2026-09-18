using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.UI;
using ScanlineStudio.UI.ViewModels;
using ScanlineStudio.UI.Views;

namespace ScanlineStudio.UI.Tests;

/// <summary>Settles empirically what reading source could not: whether the Favourites editor's mode
/// ComboBox actually writes a selection back into the row view-model.
///
/// <para>The binding moved from <c>SelectedItem</c> (TwoWay by default) to
/// <c>SelectedValue</c>/<c>SelectedValueBinding</c>, whose own default binding mode is not stated in
/// the shipped Avalonia XML docs and whose source is not available locally. If it were not TwoWay,
/// every mode edit in that dialog would be silently discarded on Save. No view-model test can see
/// that: the view-model side behaves identically either way.</para>
///
/// <para>Builds the row <c>DataTemplate</c> directly rather than showing the window, matching
/// <c>TxImageEditorPaneViewModelTests</c>'s own real-template precedent. A shown window needs the
/// Industry styles, which pull in a font resource this test project's headless renderer cannot
/// realize -- that is what the separate UI.FontTests project exists for.</para></summary>
public sealed class FavouritesEditorBindingTests
{
    [AvaloniaFact]
    public void ModeComboBox_SelectionWritesBackToTheRowViewModel()
    {
        var (row, rowRoot) = BuildFirstRow(new FrequencyPreset("SSTV", 14_230_000, RadioMode.Usb, 2400));

        var modeCombo = rowRoot.GetLogicalDescendants().OfType<ComboBox>().First(c => !c.IsEditable);
        Assert.Equal(RadioMode.Usb, modeCombo.SelectedValue);

        modeCombo.SelectedValue = RadioMode.Fm;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(RadioMode.Fm, row.SelectedMode);
        // Proves the snap ran through the real binding too, not just through a direct property set.
        Assert.Equal(15_000, row.BandwidthHz);
    }

    [AvaloniaFact]
    public void ModeComboBox_OffersTheSixModesByTheirHamRadioNames()
    {
        var (_, rowRoot) = BuildFirstRow(new FrequencyPreset("SSTV", 14_230_000, RadioMode.Usb, 2400));

        var modeCombo = rowRoot.GetLogicalDescendants().OfType<ComboBox>().First(c => !c.IsEditable);
        var choices = Assert.IsAssignableFrom<IReadOnlyList<RadioModeChoice>>(modeCombo.ItemsSource);

        Assert.Equal(
            [RadioMode.Usb, RadioMode.Lsb, RadioMode.Fm, RadioMode.Data, RadioMode.DataR, RadioMode.Pkt],
            choices.Select(c => c.Mode));
    }

    [AvaloniaFact]
    public void BandwidthComboBox_BindsTheRowsOwnQuickPicksNotAFixedList()
    {
        var (_, rowRoot) = BuildFirstRow(new FrequencyPreset("10m FM", 29_600_000, RadioMode.Fm, 12_000));

        var bwCombo = rowRoot.GetLogicalDescendants().OfType<ComboBox>().First(c => c.IsEditable);

        Assert.Equal([9000d, 12_000d, 15_000d], Assert.IsAssignableFrom<IReadOnlyList<double>>(bwCombo.ItemsSource));
    }

    private static (FrequencyPresetEditorRowViewModel Row, Control RowRoot) BuildFirstRow(FrequencyPreset preset)
    {
        var radioSession = new FakeRadioSessionService { Presets = [preset] };
        App.Services = new ServiceCollection()
            .AddSingleton<ILocalizationService>(new FakeLocalizationService())
            .BuildServiceProvider();

        var vm = new RadioStatusViewModel(
            radioSession,
            new FakeSstvSessionService(),
            new FakeLocalizationService(),
            new FakeAppearanceSettingsService(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RadioStatusViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        var row = Assert.Single(vm.EditorRows);

        var view = new FavouritesEditorWindowView { DataContext = vm };
        var itemsHost = view.GetLogicalDescendants().OfType<ItemsControl>().First(c => c.ItemTemplate is not null);
        var rowRoot = itemsHost.ItemTemplate!.Build(row) ?? throw new InvalidOperationException("DataTemplate.Build returned null.");
        rowRoot.DataContext = row;
        Dispatcher.UIThread.RunJobs();

        return (row, rowRoot);
    }

    [AvaloniaFact]
    public void ColumnCaptions_LineUpWithTheInputsTheyName()
    {
        // Code-review finding: the caption Grid and the row Grid are siblings, and Avalonia has no
        // shared-size scope. An empty trailing Auto column in the caption Grid collapses to zero and
        // widens its star column instead, sliding every caption from "MHz" rightward almost a full
        // column off the input it names -- worse than no captions at all. Build-clean, runtime-only,
        // so only a real layout pass catches it.
        App.Services = new ServiceCollection()
            .AddSingleton<ILocalizationService>(new FakeLocalizationService())
            .BuildServiceProvider();
        var radioSession = new FakeRadioSessionService
        {
            Presets = [new FrequencyPreset("SSTV", 14_230_000, RadioMode.Usb, 2400)],
        };
        var vm = new RadioStatusViewModel(
            radioSession,
            new FakeSstvSessionService(),
            new FakeLocalizationService(),
            new FakeAppearanceSettingsService(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RadioStatusViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        var row = Assert.Single(vm.EditorRows);

        var view = new FavouritesEditorWindowView { DataContext = vm };
        var captionGrid = view.GetLogicalDescendants().OfType<Grid>()
            .First(g => g.Children.OfType<TextBlock>().Any());
        var itemsHost = view.GetLogicalDescendants().OfType<ItemsControl>().First(c => c.ItemTemplate is not null);
        var rowGrid = Assert.IsType<Grid>(itemsHost.ItemTemplate!.Build(row));
        rowGrid.DataContext = row;

        // Compared structurally, not by arranged pixels: ColumnDefinition.ActualWidth stays 0 for
        // the caption Grid, which is arranged inside the window's own tree rather than standalone.
        // The defect this guards is structural anyway -- a column spec that differs, or a trailing
        // Auto column left empty on one side and filled on the other.
        Assert.Equal(
            rowGrid.ColumnDefinitions.Select(c => c.Width.ToString()),
            captionGrid.ColumnDefinitions.Select(c => c.Width.ToString()));

        var rowOccupied = rowGrid.Children.Select(Grid.GetColumn).ToHashSet();
        var captionOccupied = captionGrid.Children.Select(Grid.GetColumn).ToHashSet();
        Assert.Equal(rowOccupied.OrderBy(i => i), captionOccupied.OrderBy(i => i));
    }
}
