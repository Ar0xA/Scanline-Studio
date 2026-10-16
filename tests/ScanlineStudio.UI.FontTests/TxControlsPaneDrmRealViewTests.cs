using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Application;
using ScanlineStudio.UI.Tests;
using ScanlineStudio.UI.ViewModels;
using ScanlineStudio.UI.Views;
using Xunit;
using static ScanlineStudio.UI.FontTests.RealWindowTestSupport;

namespace ScanlineStudio.UI.FontTests;

/// <summary>P4b: the real TX pane view with DRM selected — the per-type mode templates, the DRM block, and the
/// SSTV-only rows hidden.</summary>
public sealed class TxControlsPaneDrmRealViewTests
{
    [AvaloniaFact]
    public void SelectingDrm_ShowsTheDrmBlock_AndHidesTheSstvOnlyRows()
    {
        EnsureAppServices();
        var vm = new TxControlsPaneViewModel(
            new FakeSstvSessionService { AvailableModes = [TestMode] }, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(),
            NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(),
            new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var window = new Window { Content = new TxControlsPaneView { DataContext = vm }, Width = 320, Height = 1600 };
        window.Show();
        PumpDispatcher();

        var expander = window.GetVisualDescendants().OfType<Expander>().Single();
        Assert.False(expander.IsEffectivelyVisible);

        vm.SelectedTarget = vm.AvailableTargets.OfType<DrmTarget>().Single();
        PumpDispatcher();

        Assert.True(expander.IsEffectivelyVisible);
        var modeCombo = window.GetVisualDescendants().OfType<ComboBox>().First(c => ReferenceEquals(c.ItemsSource, vm.AvailableTargets));
        Assert.IsType<DrmTarget>(modeCombo.SelectedItem);
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Panes.TxControls.Drm.TargetName", texts);
        Assert.Contains("Panes.TxControls.Drm.Profile", texts);
        Assert.DoesNotContain("Panes.TxControls.VisHeaderLabel", texts);
        Assert.DoesNotContain("Panes.TxControls.Telemetry.ToneMap", texts);
        window.Close();
    }
}
