using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

public sealed class RadioStatusViewModelTests
{
    private static RadioStatusViewModel CreateViewModel(FakeRadioSessionService? radioSession = null, FakeSstvSessionService? sstvSession = null)
        => new(radioSession ?? new FakeRadioSessionService(), sstvSession ?? new FakeSstvSessionService(), new FakeLocalizationService(), NullLogger<RadioStatusViewModel>.Instance);

    [AvaloniaFact]
    public void Constructor_LoadsPersistedPresetsAndTxVolume()
    {
        var radioSession = new FakeRadioSessionService
        {
            Presets = [new FrequencyPreset("40m SSTV", 7_171_000, RadioMode.Lsb)],
        };
        var sstvSession = new FakeSstvSessionService { TxVolumePercent = 42 };

        var vm = CreateViewModel(radioSession, sstvSession);
        Dispatcher.UIThread.RunJobs();

        var preset = Assert.Single(vm.Presets);
        // Uppercased for display only (mock2's label line is CSS text-transform:uppercase).
        Assert.Equal("40M SSTV", preset.Label);
        Assert.Equal(42, vm.TxVolumePercent);
    }

    [AvaloniaFact]
    public void ApplyPresetCommand_SetsFrequencyAndModeOnTheRadioSession()
    {
        var radioSession = new FakeRadioSessionService
        {
            Presets = [new FrequencyPreset("20m SSTV", 14_230_000, RadioMode.Usb)],
        };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        var preset = Assert.Single(vm.Presets);

        preset.SelectCommand.Execute(preset.Preset);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([14_230_000], radioSession.SetFrequencyCalls);
        Assert.Equal([RadioMode.Usb], radioSession.SetModeCalls);
    }

    [AvaloniaFact]
    public void SetFrequencyCommand_ParsesMhzInputIntoHz()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        vm.FrequencyInputMhz = "14.230000";
        vm.SetFrequencyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([14_230_000], radioSession.SetFrequencyCalls);
    }

    [AvaloniaFact]
    public void SavePresetsCommand_PersistsEditedRowsAndRebuildsPresetButtons()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        vm.AddPresetRowCommand.Execute(null);
        var row = Assert.Single(vm.EditorRows);
        row.Label = "80m SSTV";
        row.FrequencyMhzText = "3.845000";
        row.SelectedMode = RadioMode.Lsb;

        vm.SavePresetsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var saved = Assert.Single(radioSession.Presets);
        Assert.Equal("80m SSTV", saved.Label);
        Assert.Equal(3_845_000, saved.FrequencyHz);
        Assert.Equal(RadioMode.Lsb, saved.Mode);
        var button = Assert.Single(vm.Presets);
        // Uppercased for display only (mock2's label line is CSS text-transform:uppercase) --
        // the underlying stored Label above stays exactly as typed.
        Assert.Equal("80M SSTV", button.Label);
    }

    [AvaloniaFact]
    public async Task TxVolumePercentChange_PersistsAfterDebounceDelay()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        vm.TxVolumePercent = 55;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(100, sstvSession.TxVolumePercent); // not persisted yet -- still debouncing

        await Task.Delay(600);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(55, sstvSession.TxVolumePercent);
    }

    [AvaloniaFact]
    public void ApplyPresetCommand_NoRadioConnected_SetsErrorMessageInsteadOfCrashing()
    {
        // Regression test: a real hands-on run crashed the whole app here -- IRadioSessionService
        // throws InvalidOperationException when no radio is connected (a routine, common state, not
        // an edge case), and that exception was propagating uncaught out of an AsyncRelayCommand.
        var radioSession = new FakeRadioSessionService
        {
            Presets = [new FrequencyPreset("20m SSTV", 14_230_000, RadioMode.Usb)],
            ThrowOnSetFrequencyOrMode = true,
        };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        var preset = Assert.Single(vm.Presets);

        preset.SelectCommand.Execute(preset.Preset);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public void SetFrequencyCommand_NoRadioConnected_SetsErrorMessageInsteadOfCrashing()
    {
        var radioSession = new FakeRadioSessionService { ThrowOnSetFrequencyOrMode = true };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        vm.FrequencyInputMhz = "14.230000";
        vm.SetFrequencyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public void TuneCommand_KeysTheToneThroughSstvSession()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();
        vm.TuneFrequencyHz = 1750;
        vm.TuneDurationSeconds = 3;

        vm.TuneCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var call = Assert.Single(sstvSession.TuneCalls);
        Assert.Equal(1750, call.FrequencyHz);
        Assert.Equal(TimeSpan.FromSeconds(3), call.Duration);
    }

    [AvaloniaFact]
    public void Constructor_InitializesIsReceivingFromTheSessionsRealCaptureState()
    {
        var sstvSession = new FakeSstvSessionService { IsReceiving = true };
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsReceiving);
    }

    [AvaloniaFact]
    public void CheckingIsReceiving_CallsStartReceivingOnTheSstvSession()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        vm.IsReceiving = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(sstvSession.IsReceiving);
    }

    [AvaloniaFact]
    public void UncheckingIsReceiving_CallsStopReceivingOnTheSstvSession()
    {
        var sstvSession = new FakeSstvSessionService { IsReceiving = true };
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        vm.IsReceiving = false;
        Dispatcher.UIThread.RunJobs();

        Assert.False(sstvSession.IsReceiving);
    }

    [AvaloniaFact]
    public void CheckingIsReceiving_SessionThrows_RevertsToggleAndSetsErrorMessageInsteadOfCrashing()
    {
        var sstvSession = new FakeSstvSessionService { ThrowOnStartReceiving = true };
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        vm.IsReceiving = true;
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsReceiving);
        Assert.NotNull(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public void HaltReceivingCommand_StopsReceivingAndUnchecksTheToggle()
    {
        var sstvSession = new FakeSstvSessionService { IsReceiving = true };
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();
        vm.IsReceiving = true;
        Dispatcher.UIThread.RunJobs();

        vm.HaltReceivingCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsReceiving);
        Assert.False(sstvSession.IsReceiving);
    }

    [AvaloniaFact]
    public void SessionMaintenanceWarningRaised_SetsMaintenanceMessage()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        sstvSession.RaiseMaintenanceWarningRaised();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.MaintenanceMessage);
    }

    [AvaloniaFact]
    public void SessionMaintenanceWarningCleared_ClearsMaintenanceMessage()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();
        sstvSession.RaiseMaintenanceWarningRaised();
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(vm.MaintenanceMessage);

        sstvSession.RaiseMaintenanceWarningCleared();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.MaintenanceMessage);
    }

    [AvaloniaFact]
    public void SessionMaintenanceCriticalStopRaised_SetsMaintenanceMessage_AndUnchecksReceivingWithoutReenteringStop()
    {
        var sstvSession = new FakeSstvSessionService { IsReceiving = true };
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();
        vm.IsReceiving = true;
        Dispatcher.UIThread.RunJobs();

        // The real SstvSessionService has already called StopReceivingAsync by the time this event
        // fires -- the fake's IsReceiving is flipped here to mirror that, and this handler must set
        // the toggle off via the _suppressReceivingCommand guard, not by calling StopReceivingAsync a
        // second time (which ThrowOnStopReceiving would catch if it were re-entered).
        sstvSession.IsReceiving = false;
        sstvSession.ThrowOnStopReceiving = true;
        sstvSession.RaiseMaintenanceCriticalStopRaised();
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsReceiving);
        Assert.NotNull(vm.MaintenanceMessage);
        Assert.Null(vm.ErrorMessage); // must not go through the ordinary SetReceivingSafeAsync failure path
    }

    [AvaloniaFact]
    public void CatLinked_TracksConnectionEvents_ConnectedTrueDisconnectedFalse()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.CatLinked);

        radioSession.PushConnectionEvent(new RadioConnectionEvent(RadioConnectionState.Connected, null, null, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.CatLinked);

        radioSession.PushConnectionEvent(new RadioConnectionEvent(RadioConnectionState.Disconnected, null, null, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.CatLinked);
    }

    [AvaloniaFact]
    public void CatLinked_IgnoresCommandFailed_ConnectionStaysHealthyPerThatStatesOwnContract()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        radioSession.PushConnectionEvent(new RadioConnectionEvent(RadioConnectionState.Connected, null, null, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.CatLinked);

        radioSession.PushConnectionEvent(new RadioConnectionEvent(RadioConnectionState.CommandFailed, null, null, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.CatLinked);
    }
}
