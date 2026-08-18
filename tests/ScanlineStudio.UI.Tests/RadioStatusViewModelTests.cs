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

    // Tier 2/3 follow-up (2026-08-18): the Transceiver card's RX level meter was a literal fixed
    // 63%/78% stub with no real gain parameter behind it -- RadioState.SignalStrengthDb is now real
    // (l STRENGTH / RIG_LEVEL_STRENGTH), RX-time-gated (opposite of the TX-only meters above).

    [AvaloniaFact]
    public void RxLevelDisplay_ValuePresent_FormatsSignedDbWithUnicodeMinus()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: -14, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("−14 dB", vm.RxLevelDisplay); // U+2212 MINUS SIGN, matching the source mockup's own glyph
    }

    [AvaloniaFact]
    public void RxLevelDisplay_PositiveValue_ShowsExplicitPlusSign()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: 14, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("+14 dB", vm.RxLevelDisplay);
    }

    [AvaloniaFact]
    public void RxLevelDisplay_ExactlyS9_ShowsZero()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: 0, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("0 dB", vm.RxLevelDisplay);
    }

    [AvaloniaFact]
    public void RxLevelDisplay_NoReadingThisPoll_ShowsPlaceholder()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: true, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("—", vm.RxLevelDisplay);
    }

    [AvaloniaTheory]
    [InlineData(-54, 0.0)]   // S0 floor -- clamped to the empty end of the bar
    [InlineData(60, 100.0)]  // S9+60 ceiling -- clamped to the full end of the bar
    [InlineData(0, 47.368421052631575)] // exactly S9 -- (0-(-54))/(60-(-54))*100
    [InlineData(-100, 0.0)]  // below floor -- still clamps to 0, not a negative fill
    [InlineData(200, 100.0)] // above ceiling -- still clamps to 100, not an overflowing fill
    public void RxLevelFillPercent_ClampsToTheS0ToS9Plus60Range(int signalStrengthDb, double expectedPercent)
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: signalStrengthDb, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(expectedPercent, vm.RxLevelFillPercent, precision: 10);
    }

    [AvaloniaFact]
    public void RxLevelFillPercent_NoReadingThisPoll_IsZero_NotAMissingDataIndicatorOfItsOwn()
    {
        // RxLevelDisplay's own "—" text carries the actual missing-data signal -- the fill bar just
        // reads as an empty bar, same as a genuinely weak signal at the S0 floor.
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: true, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0.0, vm.RxLevelFillPercent);
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
    public void RigMetersDisplayAndRxLevel_RevertToPlaceholder_WhenCatLinkDropsMidSession()
    {
        // Auditor-caught (2026-08-18, RX signal-strength meter review): same staleness gap as
        // IsKeyed above -- RigMetersDisplay/RxLevelDb are only ever refreshed by a successful poll,
        // so without this a rig that read "SWR 1.2 · PWR 75%"/"−14 dB" when the link dropped would
        // keep showing that live-looking reading indefinitely with nothing behind it.
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
        Assert.Equal("—", vm.RxLevelDisplay);
        Assert.Equal(0.0, vm.RxLevelFillPercent);
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
