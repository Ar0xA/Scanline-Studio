using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>Live-locale-switch review (2026-09-20): yoniq-auditor code-review flagged F4 -- an
/// unverified-from-source risk that replacing <see cref="LogbookPaneViewModel.AvailableModes"/>'s
/// backing array while <c>FormMode</c> is TwoWay-bound via <c>SelectedValueBinding</c> could push a
/// transient selection-clear back into <c>FormMode</c>, silently wiping a half-filled QSO form's Mode
/// field. The shipped fix captures <c>FormMode</c> before rebuilding <c>AvailableModes</c> and
/// restores it after, deliberately NOT depending on whichever way Avalonia's
/// <c>SelectingItemsControl</c> actually behaves on an <c>ItemsSource</c> replacement. This test
/// proves the restore works with a REAL rebuilt array (new <c>RadioModeOption</c> instances, not the
/// same objects), not just that the capture/restore code compiles.</summary>
public sealed class LogbookPaneViewModelCultureSwitchTests
{
    [AvaloniaFact]
    public async Task CultureChanged_RebuildsAvailableModes_ButPreservesFormMode()
    {
        var localization = new FakeLocalizationService();
        var vm = new LogbookPaneViewModel(new FakeLogbookSessionService(), new FakeFilePickerService(),
            new FakeSstvSessionService(), localization, new FakeReceiveHistoryStore(),
            NullLogger<LogbookPaneViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.FormMode = ScanlineStudio.Abstractions.Radio.RadioMode.Usb;
        var originalOptions = vm.AvailableModes;

        await localization.SetCultureAsync(System.Globalization.CultureInfo.GetCultureInfo("de"));
        Dispatcher.UIThread.RunJobs();

        Assert.NotSame(originalOptions, vm.AvailableModes);
        Assert.Equal(ScanlineStudio.Abstractions.Radio.RadioMode.Usb, vm.FormMode);
    }

    [AvaloniaFact]
    public async Task CultureChanged_WithNoModeSelected_LeavesFormModeNull()
    {
        var localization = new FakeLocalizationService();
        var vm = new LogbookPaneViewModel(new FakeLogbookSessionService(), new FakeFilePickerService(),
            new FakeSstvSessionService(), localization, new FakeReceiveHistoryStore(),
            NullLogger<LogbookPaneViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.FormMode);

        await localization.SetCultureAsync(System.Globalization.CultureInfo.GetCultureInfo("de"));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.FormMode);
    }
}
