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
    public void Constructor_LoadsPersistedPresetsAndTxState()
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
    public void TxVolumeDisplay_ShowsMutedGlyphInsteadOfPercent_WhenDeviceIsMuted()
    {
        var sstvSession = new FakeSstvSessionService { TxVolumePercent = 42, TxIsMuted = true };

        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.TxIsMuted);
        Assert.Equal("\U0001F507", vm.TxVolumeDisplay);
        // Muting doesn't reset the underlying percent -- the slider's own Value stays real.
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
    public void StoreCurrentPresetCommand_DisabledUntilFirstRadioStateArrives()
    {
        // Regression test (auditor-caught, 2026-08-11): before any RadioState, _currentFrequencyHz
        // is 0 -- without this guard, invoking the command would silently persist an unremovable
        // "0.000000 USB" preset (no in-app UI exposes EditorRows/RemovePresetRowCommand to delete it).
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.StoreCurrentPresetCommand.CanExecute(null));

        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.StoreCurrentPresetCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task StoreCurrentPresetCommand_AppendsCurrentFrequencyAndModeAsNewPreset()
    {
        var radioSession = new FakeRadioSessionService
        {
            Presets = [new FrequencyPreset("40m SSTV", 7_171_000, RadioMode.Lsb)],
        };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        await vm.StoreCurrentPresetCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, radioSession.Presets.Count);
        var stored = radioSession.Presets[1];
        Assert.Equal(14_230_000, stored.FrequencyHz);
        Assert.Equal(RadioMode.Usb, stored.Mode);
        Assert.Equal(2, vm.Presets.Count);
    }

    [AvaloniaFact]
    public async Task StoreCurrentPresetCommand_SaveFails_SetsErrorMessageInsteadOfSilentlyDroppingIt()
    {
        var radioSession = new FakeRadioSessionService { ThrowOnSaveFrequencyPresets = true };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        await vm.StoreCurrentPresetCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.ErrorMessage);
        // Tier B audit finding: this used to leave the appended row in EditorRows on a failed save
        // -- since no shipped UI exposes EditorRows/RemovePresetRowCommand to delete it, a retry
        // after the failure appended a SECOND row on top of the first, persisting a duplicate,
        // permanently undeletable preset the moment the save eventually succeeded.
        Assert.Empty(vm.EditorRows);
    }

    [AvaloniaFact]
    public async Task StoreCurrentPresetCommand_RetryAfterAFailedSave_DoesNotPersistADuplicate()
    {
        var radioSession = new FakeRadioSessionService { ThrowOnSaveFrequencyPresets = true };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        await vm.StoreCurrentPresetCommand.ExecuteAsync(null); // fails
        Dispatcher.UIThread.RunJobs();

        radioSession.ThrowOnSaveFrequencyPresets = false;
        await vm.StoreCurrentPresetCommand.ExecuteAsync(null); // retry, succeeds
        Dispatcher.UIThread.RunJobs();

        Assert.Single(radioSession.Presets);
        Assert.Single(vm.Presets);
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
    public void RxLevelDisplay_ReflectsRawInputPeakLevel_AsPlainNumber()
    {
        var sstvSession = new FakeSstvSessionService { RawInputPeakLevel = 0.5 };
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("50", vm.RxLevelDisplay);
        Assert.Equal(50.0, vm.RxLevelFillPercent);
    }

    [AvaloniaTheory]
    [InlineData(0.0, false)]  // too quiet -- red
    [InlineData(0.05, false)] // too quiet -- red
    [InlineData(0.5, true)]   // good -- green
    [InlineData(0.95, false)] // too hot -- red
    [InlineData(1.0, false)]  // too hot -- red
    public void RxLevelInGoodRange_ReflectsWhetherLevelIsWithinTheGoodDecodingBand(double rawInputPeakLevel, bool expectedInGoodRange)
    {
        var sstvSession = new FakeSstvSessionService { RawInputPeakLevel = rawInputPeakLevel };
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(expectedInGoodRange, vm.RxLevelInGoodRange);
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
        // spec/18-path-to-1.0.md High item 8: the constructor now retries StartReceivingAsync
        // itself whenever IsReceiving starts false, which would otherwise make the explicit
        // toggle-on below a no-op (already-true -> true short-circuits, OnIsReceivingChanged never
        // fires) and this assertion pass for the wrong reason. Starting IsReceiving true dodges
        // that retry; the explicit off-then-on cycle below isolates the real toggle-on path this
        // test actually means to exercise.
        var sstvSession = new FakeSstvSessionService { IsReceiving = true };
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();
        vm.IsReceiving = false;
        Dispatcher.UIThread.RunJobs();

        vm.IsReceiving = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(sstvSession.IsReceiving);
    }

    [AvaloniaFact]
    public void Constructor_IsReceivingAlreadyTrue_NeverCallsStartReceivingAgain()
    {
        // spec/18-path-to-1.0.md High item 8: the retry is gated on !_isReceiving specifically so
        // the overwhelmingly common case (Program.cs's own startup attempt already succeeded) stays
        // a true no-op -- no redundant StartReceivingAsync call.
        var sstvSession = new FakeSstvSessionService { IsReceiving = true };
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, sstvSession.StartReceivingCallCount);
        Assert.True(vm.IsReceiving);
    }

    [AvaloniaFact]
    public void Constructor_IsReceivingFalse_RetrySucceeds_SelfHealsWithoutAnError()
    {
        // spec/18-path-to-1.0.md High item 8: a transient failure at Program.cs's own earlier
        // attempt (e.g. a timing issue) shouldn't leave the user staring at an unexplained error if
        // this later retry, from the ViewModel constructor, would have succeeded.
        var sstvSession = new FakeSstvSessionService();
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, sstvSession.StartReceivingCallCount);
        Assert.True(vm.IsReceiving);
        Assert.Null(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public void Constructor_IsReceivingFalse_RetryFailsAgain_SurfacesTheErrorWithoutAnyUserAction()
    {
        // spec/18-path-to-1.0.md High item 8's actual target scenario: no configured/default audio
        // device at all -- Program.cs's own startup attempt failed silently (a Warning-level log
        // entry only), and this constructor-time retry fails again for the same real reason. The
        // fix's whole point is that ErrorMessage becomes visible in the header without the user
        // having to discover and manually retry the toggle themselves.
        var sstvSession = new FakeSstvSessionService { ThrowOnStartReceiving = true };
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, sstvSession.StartReceivingCallCount);
        Assert.False(vm.IsReceiving);
        Assert.NotNull(vm.ErrorMessage);
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
        // spec/18-path-to-1.0.md High item 8's own constructor-time retry already exercises this
        // exact throw-and-revert path once (IsReceiving starts false here, so the retry fires and
        // fails, reverting to false before this test's own explicit toggle runs) -- the explicit
        // vm.IsReceiving = true below is a genuine SECOND, real state transition (false -> true),
        // deliberately re-testing the same real user-toggle path this test's own name describes,
        // not relying on the constructor alone.
        var sstvSession = new FakeSstvSessionService { ThrowOnStartReceiving = true };
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        vm.IsReceiving = true;
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsReceiving);
        Assert.NotNull(vm.ErrorMessage);
        // Code-review finding: without this, the assertions above pass identically whether or not
        // the explicit toggle-on two lines up actually ran anything -- the constructor's own retry
        // alone produces the same final (false, non-null) state. This is what proves a genuine
        // SECOND StartReceivingAsync call happened, not just the constructor's first one.
        Assert.Equal(2, sstvSession.StartReceivingCallCount);
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

        // CatLinked now reads IsGenuinelyConnected live (not evt.State directly -- see that
        // property's own doc comment for why), so the fake's own latch must be set alongside each
        // pushed event, matching what the real RadioController would have already done by the time
        // it published the matching event.
        radioSession.IsGenuinelyConnected = true;
        radioSession.PushConnectionEvent(new RadioConnectionEvent(RadioConnectionState.Connected, null, null, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.CatLinked);

        radioSession.IsGenuinelyConnected = false;
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

        radioSession.IsGenuinelyConnected = true;
        radioSession.PushConnectionEvent(new RadioConnectionEvent(RadioConnectionState.Connected, null, null, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.CatLinked);

        // CommandFailed doesn't touch the latch either way (a command-level failure means the
        // session stays healthy) -- IsGenuinelyConnected correctly stays true here, unchanged.
        radioSession.PushConnectionEvent(new RadioConnectionEvent(RadioConnectionState.CommandFailed, null, null, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.CatLinked);
    }

    [AvaloniaFact]
    public void GiveUpFlavoredDisconnected_SetsConnectionErrorMessage_AndRaisesConnectionGaveUp()
    {
        // Give-up-after-5 feature: a Disconnected event with a non-null Reason means
        // RadioController's own give-up branch fired, not a real Disconnect click (always
        // reason: null -- see the next test). This is the persistent, always-reachable half of the
        // signal (RadioHeaderView.axaml, visible even with Options closed) plus the dismissible
        // toast (ConnectionGaveUp, wired by MainWindow.axaml.cs).
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.ConnectionErrorMessage);

        string? raised = null;
        vm.ConnectionGaveUp += message => raised = message;

        radioSession.PushConnectionEvent(new RadioConnectionEvent(
            RadioConnectionState.Disconnected, "Gave up after 5 attempts: simulated", null, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.ConnectionErrorMessage);
        Assert.Equal(vm.ConnectionErrorMessage, raised);
    }

    [AvaloniaFact]
    public void NormalDisconnected_DoesNotSetConnectionErrorMessage_OrRaiseConnectionGaveUp()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        var raised = false;
        vm.ConnectionGaveUp += _ => raised = true;

        radioSession.PushConnectionEvent(new RadioConnectionEvent(RadioConnectionState.Disconnected, null, null, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.ConnectionErrorMessage);
        Assert.False(raised);
    }

    [AvaloniaFact]
    public void ConnectionErrorMessage_ClearedOnNextConnecting()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        radioSession.PushConnectionEvent(new RadioConnectionEvent(
            RadioConnectionState.Disconnected, "Gave up after 5 attempts: simulated", null, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(vm.ConnectionErrorMessage);

        radioSession.PushConnectionEvent(new RadioConnectionEvent(RadioConnectionState.Connecting, null, null, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.ConnectionErrorMessage);
    }

    [AvaloniaFact]
    public void IsKeyed_TracksTheRigsOwnPttReadback_TrueThenFalse()
    {
        // spec/18-path-to-1.0.md High item 10.
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsKeyed);

        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: true, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsKeyed);

        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsKeyed);
    }

    // Auditor usability review follow-up (2026-08-18): the VFO card's rig-meters pill was a literal
    // stub on the false premise that no rig-meters concept exists on IRadioSessionService today --
    // RadioState.SwrRatio/AlcLevel/PowerPercent are real, already-polled data, just never read out.

    [AvaloniaFact]
    public void RigMetersDisplay_AllThreeMetersPresent_JoinsAllThree()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        radioSession.Push(new RadioState(
            14_230_000, RadioMode.Usb, IsTransmitting: true, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow,
            SwrRatio: 1.2f, AlcLevel: 50f, PowerPercent: 75f));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("SWR 1.2 · ALC 50% · PWR 75%", vm.RigMetersDisplay);
    }

    [AvaloniaFact]
    public void RigMetersDisplay_NoMetersRead_ShowsPlaceholder()
    {
        // Not transmitting (or a rig with no meter capability at all) -- RadioState's own convention
        // is null-means-"not read this poll," never a meaningful zero.
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("—", vm.RigMetersDisplay);
    }

    [AvaloniaFact]
    public void RigMetersDisplay_OnlySomeMetersCapable_ShowsOnlyThoseNonNull()
    {
        // A rig missing one meter capability (e.g. no ALC readback) still shows the other two --
        // the pill doesn't go blank just because one of three is absent.
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        radioSession.Push(new RadioState(
            14_230_000, RadioMode.Usb, IsTransmitting: true, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow,
            SwrRatio: 1.5f, AlcLevel: null, PowerPercent: 100f));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("SWR 1.5 · PWR 100%", vm.RigMetersDisplay);
    }

    // User-reported gap (2026-08-18): "while TX lights up, receiving should not stay green" -- the
    // header's Receiving toggle stayed visually lit throughout a local transmission. IsCapturePausedForTx
    // is a SEPARATE, purely visual flag; IsReceiving itself must stay untouched by this event.

    [AvaloniaFact]
    public void IsCapturePausedForTx_TracksSstvSessionCapturePausedEvent()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsCapturePausedForTx);

        sstvSession.RaiseCapturePausedForTransmitChanged(true);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsCapturePausedForTx);

        sstvSession.RaiseCapturePausedForTransmitChanged(false);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsCapturePausedForTx);
    }

    [AvaloniaFact]
    public void IsCapturePausedForTx_DoesNotChangeIsReceivingItself()
    {
        // The toggle's own Checked/click-handling semantics ("capture is armed") are deliberately
        // untouched by this event -- only its visual Opacity binding reacts to it (RadioHeaderView.axaml).
        var sstvSession = new FakeSstvSessionService();
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();
        vm.IsReceiving = true;

        sstvSession.RaiseCapturePausedForTransmitChanged(true);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsReceiving);
        Assert.True(vm.IsCapturePausedForTx);
    }

    [AvaloniaFact]
    public void IsKeyed_NeverSetByAnyRadioState_StaysFalse()
    {
        // The safe default: no CAT link (or the "none" backend) means no RadioState ever arrives,
        // so IsKeyed must never claim "confirmed keyed" with nothing behind it.
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsKeyed);
    }

    [AvaloniaFact]
    public void IsKeyed_RevertsToFalse_WhenCatLinkDropsMidSession()
    {
        // spec/18-path-to-1.0.md High item 10, round-1 plan-review blocker: without this, a rig
        // that was keyed when the link dropped would stay showing "keyed" forever -- IsKeyed is
        // only ever refreshed by a successful poll, so losing the connection needs its own,
        // separate clear via OnConnectionEvent, not just relying on the next (never-arriving) poll.
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        radioSession.PushConnectionEvent(new RadioConnectionEvent(RadioConnectionState.Connected, null, null, DateTimeOffset.UtcNow));
        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: true, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsKeyed);

        radioSession.PushConnectionEvent(new RadioConnectionEvent(RadioConnectionState.Disconnected, null, null, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.CatLinked);
        Assert.False(vm.IsKeyed);
    }

    [AvaloniaFact]
    public void RigMetersDisplay_RevertsToPlaceholder_WhenCatLinkDropsMidSession()
    {
        // Auditor-caught (2026-08-18, RX signal-strength meter review): RigMetersDisplay is only
        // ever refreshed by a successful poll, so without this a rig that read "SWR 1.2 · PWR 75%"
        // when the link dropped would keep showing that live-looking reading indefinitely with
        // nothing behind it.
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        radioSession.PushConnectionEvent(new RadioConnectionEvent(RadioConnectionState.Connected, null, null, DateTimeOffset.UtcNow));
        radioSession.Push(new RadioState(
            14_230_000, RadioMode.Usb, IsTransmitting: true, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow,
            SwrRatio: 1.2f, AlcLevel: 50f, PowerPercent: 75f));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("SWR 1.2 · ALC 50% · PWR 75%", vm.RigMetersDisplay);

        radioSession.PushConnectionEvent(new RadioConnectionEvent(RadioConnectionState.Disconnected, null, null, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.CatLinked);
        Assert.Equal("—", vm.RigMetersDisplay);
    }

    [AvaloniaFact]
    public void IsKeyed_RevertsToFalse_WhenConnectionEntersReconnecting()
    {
        // Code-review finding: the sibling test above (explicit Disconnected) is the wrong
        // real-world shape for the scenario this fix actually targets -- a mid-transmission
        // transport failure publishes Reconnecting, not Disconnected (RadioController's own poll
        // loop never publishes Disconnected except from an explicit DisconnectAsync call). The
        // guard (`!= Connected`) is already correct for both, but only THIS state exercises the
        // real failure path.
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        radioSession.PushConnectionEvent(new RadioConnectionEvent(RadioConnectionState.Connected, null, null, DateTimeOffset.UtcNow));
        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: true, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsKeyed);

        radioSession.PushConnectionEvent(new RadioConnectionEvent(RadioConnectionState.Reconnecting, null, null, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.CatLinked);
        Assert.False(vm.IsKeyed);
    }

    [AvaloniaFact]
    public void IsKeyed_UnaffectedByCommandFailed_MatchingCatLinkedsOwnContract()
    {
        // A single failed command doesn't mean the connection itself dropped -- IsKeyed must not
        // be cleared by this event either, same reasoning CatLinked already establishes.
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        radioSession.PushConnectionEvent(new RadioConnectionEvent(RadioConnectionState.Connected, null, null, DateTimeOffset.UtcNow));
        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: true, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsKeyed);

        radioSession.PushConnectionEvent(new RadioConnectionEvent(RadioConnectionState.CommandFailed, null, null, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsKeyed);
    }
}
