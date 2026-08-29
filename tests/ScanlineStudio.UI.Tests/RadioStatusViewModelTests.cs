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
    public void SelectedRadioModeChanged_UpdatesTheThreeSidebandBooleans()
    {
        // Stub survey Tier 4 (2026-08-26): the VFO card's sideband segment is now bound to these
        // three derived properties -- proves they track SelectedRadioMode both ways, including the
        // rig-driven direction (OnStateChanged sets SelectedRadioMode directly, not via one of these
        // setters), not just the user-click direction the setters cover.
        var vm = CreateViewModel();
        Dispatcher.UIThread.RunJobs();

        vm.SelectedRadioMode = RadioMode.Lsb;

        Assert.False(vm.IsSidebandUsb);
        Assert.True(vm.IsSidebandLsb);
        Assert.False(vm.IsSidebandFm);
    }

    [AvaloniaFact]
    public void SelectedRadioModeChanged_RaisesPropertyChangedForAllThreeSidebandBooleans()
    {
        // Code-review finding: a passing getter-value assertion alone (the test above) doesn't prove
        // the live RadioButtons actually re-render -- only a real PropertyChanged notification does,
        // and it's the one thing [NotifyPropertyChangedFor]'s own removal wouldn't otherwise be
        // caught by (the getters still compute correctly on next read either way). Mirrors
        // OptionsWindowViewModelTests' own established pattern for this exact class of gap.
        var vm = CreateViewModel();
        Dispatcher.UIThread.RunJobs();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SelectedRadioMode = RadioMode.Lsb;

        Assert.Contains(nameof(vm.IsSidebandUsb), raised);
        Assert.Contains(nameof(vm.IsSidebandLsb), raised);
        Assert.Contains(nameof(vm.IsSidebandFm), raised);
    }

    [AvaloniaFact]
    public void IsSidebandLsb_SetTrue_ChangesSelectedRadioModeAndCallsRadioSession()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        vm.IsSidebandLsb = true;

        Assert.Equal(RadioMode.Lsb, vm.SelectedRadioMode);
        Assert.Contains(RadioMode.Lsb, radioSession.SetModeCalls);
    }

    [AvaloniaFact]
    public void IsSidebandUsb_SetFalse_DoesNotChangeSelectedRadioMode()
    {
        // RadioButton also fires IsChecked=false for the option being DEselected as the group's
        // mutual exclusion switches away -- must be a no-op, not clear SelectedRadioMode to some
        // "none selected" state RadioMode has no member for. Starting mode is deliberately Lsb, NOT
        // the RadioMode field's own Usb default (code-review finding: starting from Usb left this
        // test green even with the `if (value)` guard removed entirely, since a guardless setter
        // would clobber SelectedRadioMode straight back to the value it already was) -- this is the
        // scenario the guard actually has to prevent: user clicks LSB, USB's own button unchecks as
        // the group's mutual exclusion switches away, and that deselection must not un-clobber LSB.
        var vm = CreateViewModel();
        Dispatcher.UIThread.RunJobs();
        vm.SelectedRadioMode = RadioMode.Lsb;

        vm.IsSidebandUsb = false;

        Assert.Equal(RadioMode.Lsb, vm.SelectedRadioMode);
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

    /// <summary>ui_transition_plan.md step 11 (T2-1): SetFrequencyCommand/FrequencyInputMhz were
    /// already fully built and tested (see the two tests above/below this block) -- this batch
    /// covers the NEW inline-edit wiring around them (BeginEditFrequencyCommand/IsEditingFrequency/
    /// CancelEditFrequencyCommand and the plausibility-range validation), not the pre-existing
    /// parse/apply behavior itself.</summary>
    [AvaloniaFact]
    public void BeginEditFrequencyCommand_PrefillsFromCurrentFrequency_AndEntersEditMode()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        vm.BeginEditFrequencyCommand.Execute(null);

        Assert.True(vm.IsEditingFrequency);
        Assert.Equal("14.230000", vm.FrequencyInputMhz);
    }

    [AvaloniaFact]
    public void BeginEditFrequencyCommand_NoRadioStateYet_PrefillsEmpty()
    {
        var vm = CreateViewModel();
        Dispatcher.UIThread.RunJobs();

        vm.BeginEditFrequencyCommand.Execute(null);

        Assert.True(vm.IsEditingFrequency);
        Assert.Equal(string.Empty, vm.FrequencyInputMhz);
    }

    [AvaloniaFact]
    public void CancelEditFrequencyCommand_RevertsWithoutApplying()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        vm.BeginEditFrequencyCommand.Execute(null);
        vm.FrequencyInputMhz = "14.230000";

        vm.CancelEditFrequencyCommand.Execute(null);

        Assert.False(vm.IsEditingFrequency);
        Assert.Empty(radioSession.SetFrequencyCalls);
    }

    [AvaloniaFact]
    public void SetFrequencyCommand_Success_ExitsEditMode()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        vm.BeginEditFrequencyCommand.Execute(null);
        vm.FrequencyInputMhz = "14.230000";

        vm.SetFrequencyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsEditingFrequency);
        Assert.Null(vm.ErrorMessage);
    }

    /// <summary>Code-review finding: a slow CAT backend's completion (flrig's own readback-verify
    /// poll loop can take ~2.65s) must not close a DIFFERENT, later edit session that the operator
    /// opened after cancelling the one that's still in flight -- reproduced here with a
    /// separately-controllable gate (SetFrequencyGate), not a shared one, per this project's own
    /// "deterministic gates, not shared race" convention. Uses ExecuteAsync + await, not
    /// Execute(null) + RunJobs: the fake's own gated await runs its continuation on the ThreadPool
    /// (ConfigureAwait(false) opts out of Dispatcher's SynchronizationContext), so only a real await
    /// -- not a Dispatcher pump -- can deterministically observe it complete.</summary>
    [AvaloniaFact]
    public async Task SetFrequencyCommand_StaleCompletionAfterCancelAndReopen_DoesNotCloseTheNewSession()
    {
        var gate = new TaskCompletionSource();
        var radioSession = new FakeRadioSessionService { SetFrequencyGate = gate };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        vm.BeginEditFrequencyCommand.Execute(null);
        vm.FrequencyInputMhz = "14.230000";
        var op = vm.SetFrequencyCommand.ExecuteAsync(null);

        // Cancel the in-flight edit and open a new one before the gated call completes.
        vm.CancelEditFrequencyCommand.Execute(null);
        vm.BeginEditFrequencyCommand.Execute(null);
        vm.FrequencyInputMhz = "7.171000";

        gate.SetResult();
        await op;
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsEditingFrequency);
        Assert.Equal("7.171000", vm.FrequencyInputMhz);
    }

    /// <summary>Auditor-caught gap: CommunityToolkit's [RelayCommand] defaults to
    /// AllowConcurrentExecutions=true, so pressing Enter twice on a slow backend (no busy indicator
    /// exists) starts a SECOND SetFrequencyAsync under the SAME edit session before the first
    /// returns -- confirmed by direct test against AsyncRelayCommand.Execute, not assumed. Without
    /// BeginEditFrequency's own session-id increment, the first call's completion closes the editor,
    /// the operator reopens it, and the SECOND call's stale completion (still carrying the OLD
    /// session id) then closes that new, unrelated edit anyway. Two independently-releasable gates
    /// (SetFrequencyGateQueue), each call's own ExecuteAsync awaited individually -- same
    /// "deterministic gates, not shared race" reasoning as the test above.</summary>
    [AvaloniaFact]
    public async Task SetFrequencyCommand_DoubleEnterThenReopenBeforeSecondCallCompletes_DoesNotCloseTheNewSession()
    {
        var gateA = new TaskCompletionSource();
        var gateB = new TaskCompletionSource();
        var radioSession = new FakeRadioSessionService { SetFrequencyGateQueue = new Queue<TaskCompletionSource>([gateA, gateB]) };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        vm.BeginEditFrequencyCommand.Execute(null);
        vm.FrequencyInputMhz = "14.230000";

        // Double Enter: both calls captured under the SAME edit session, no Cancel in between.
        var opA = vm.SetFrequencyCommand.ExecuteAsync(null);
        var opB = vm.SetFrequencyCommand.ExecuteAsync(null);

        gateA.SetResult();
        await opA;
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsEditingFrequency);

        vm.BeginEditFrequencyCommand.Execute(null);
        vm.FrequencyInputMhz = "7.171000";

        gateB.SetResult();
        await opB;
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsEditingFrequency);
        Assert.Equal("7.171000", vm.FrequencyInputMhz);
    }

    /// <summary>"Invalid entry rejected inline" (the plan's own wording) -- stays in edit mode so the
    /// operator can see the rejection and correct it, rather than silently reverting to the old
    /// display.</summary>
    [AvaloniaFact]
    public void SetFrequencyCommand_UnparsableInput_StaysInEditModeAndSetsErrorMessage()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        vm.BeginEditFrequencyCommand.Execute(null);
        vm.FrequencyInputMhz = "not a number";

        vm.SetFrequencyCommand.Execute(null);

        Assert.True(vm.IsEditingFrequency);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Empty(radioSession.SetFrequencyCalls);
    }

    [AvaloniaTheory]
    [InlineData("0.0001")] // below the plausible floor (0.001-30000 MHz, widened per code-review)
    [InlineData("50000")] // above the plausible ceiling
    [InlineData("-14.23")] // negative
    public void SetFrequencyCommand_ImplausibleRange_StaysInEditModeAndSetsErrorMessage(string mhzText)
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        vm.BeginEditFrequencyCommand.Execute(null);
        vm.FrequencyInputMhz = mhzText;

        vm.SetFrequencyCommand.Execute(null);

        Assert.True(vm.IsEditingFrequency);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Empty(radioSession.SetFrequencyCalls);
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
    public async Task SavePresetsCommand_RowWithInvalidFrequency_AbortsSaveAndLeavesEditorRowsUntouched()
    {
        // Plan-review finding, 2026-08-26: SavePresetsInternalAsync used to silently SKIP a row
        // whose FrequencyMhzText failed to parse, then rebuild EditorRows from the filtered list --
        // unreachable while EditorRows' only sources were persisted values and AddPresetRow's own
        // valid default, reachable the instant the Favourites Editor dialog lets a user type into
        // FrequencyMhzText directly. The row silently vanished on Save while reporting success.
        var radioSession = new FakeRadioSessionService
        {
            Presets = [new FrequencyPreset("Good", 14_230_000, RadioMode.Usb)],
        };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        var goodRow = Assert.Single(vm.EditorRows);

        vm.AddPresetRowCommand.Execute(null);
        var badRow = vm.EditorRows[1];
        badRow.FrequencyMhzText = "not-a-number";

        await vm.SavePresetsCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, vm.EditorRows.Count);
        Assert.Same(goodRow, vm.EditorRows[0]);
        Assert.Same(badRow, vm.EditorRows[1]);
        Assert.Equal("not-a-number", vm.EditorRows[1].FrequencyMhzText);
        Assert.Single(radioSession.Presets); // unchanged -- the save never reached SaveFrequencyPresetsAsync
        Assert.NotNull(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public void OpenFavouritesEditorCommand_RaisesFavouritesEditorRequestedAndReloadsPresets()
    {
        var radioSession = new FakeRadioSessionService
        {
            Presets = [new FrequencyPreset("40m SSTV", 7_171_000, RadioMode.Lsb)],
        };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        var fireCount = 0;
        vm.FavouritesEditorRequested += () => fireCount++;

        vm.OpenFavouritesEditorCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, fireCount);
        // Reload-on-open (plan-review finding): EditorRows is live shared state with no rollback --
        // without this, a prior session's un-Saved Add/Remove/edit would keep showing indefinitely
        // instead of reflecting what's actually persisted.
        Assert.Single(vm.EditorRows);
    }

    [AvaloniaFact]
    public void OpenToneGeneratorCommand_RaisesToneGeneratorRequested()
    {
        var vm = CreateViewModel();
        Dispatcher.UIThread.RunJobs();

        var fireCount = 0;
        vm.ToneGeneratorRequested += () => fireCount++;

        vm.OpenToneGeneratorCommand.Execute(null);

        Assert.Equal(1, fireCount);
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
    public void TuneCommand_ReturnsToTheStartLabelOnceTheToneFinishes()
    {
        // Plan-review finding, 2026-08-26: RadioStatusViewModel.TuneCommand lets the OPERATOR pick
        // the duration (up to ISstvSessionService's own 5-minute safety backstop), unlike the
        // fixed-30s Options tab Tune -- it needed the same start/stop toggle that dialog already
        // has. FakeSstvSessionService.TuneAsync completes synchronously (Task.CompletedTask), so
        // this proves the toggle correctly resets rather than getting stuck "Stop tune" -- it does
        // NOT exercise the mid-flight Stop-cancels-the-tone path (the fake has no way to hang).
        var sstvSession = new FakeSstvSessionService();
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        vm.TuneCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsTuning);
        Assert.Equal("RadioStatus.Tune", vm.TuneButtonLabel);
    }

    [AvaloniaFact]
    public void StopTuneIfActive_NothingCurrentlyTuning_IsASafeNoOp()
    {
        // ToneGeneratorWindowView.axaml.cs calls this unconditionally on every Closed, including the
        // overwhelmingly common case where the dialog is closed without ever clicking Tune.
        var vm = CreateViewModel();
        Dispatcher.UIThread.RunJobs();

        vm.StopTuneIfActive();
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
    public void GiveUpFlavoredDisconnected_RaisesConnectionGaveUpWithTheLocalizedMessage()
    {
        // Give-up-after-5 feature: a Disconnected event with a non-null Reason means
        // RadioController's own give-up branch fired, not a real Disconnect click (always
        // reason: null -- see the next test). Surfaced as the must-acknowledge popup
        // (ConnectionGaveUp, wired by MainWindow.axaml.cs -- RadioConnectionGaveUpWindowView); an
        // earlier persistent header text line covering the same signal was removed as redundant.
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        string? raised = null;
        vm.ConnectionGaveUp += message => raised = message;

        radioSession.PushConnectionEvent(new RadioConnectionEvent(
            RadioConnectionState.Disconnected, "Gave up after 5 attempts: simulated", null, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        // FakeLocalizationService.GetString echoes the raw key -- this proves the VM actually
        // localized the reason through Options.Radio.Connect.GaveUp, not just forwarded evt.Reason.
        Assert.Equal("Options.Radio.Connect.GaveUp", raised);
    }

    [AvaloniaFact]
    public void NormalDisconnected_DoesNotRaiseConnectionGaveUp()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        var raised = false;
        vm.ConnectionGaveUp += _ => raised = true;

        radioSession.PushConnectionEvent(new RadioConnectionEvent(RadioConnectionState.Disconnected, null, null, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.False(raised);
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

    [AvaloniaFact]
    public void VfoKickerDisplay_TracksIsKeyed_RxThenTx()
    {
        // Stub sweep, 2026-08-26: replaces the former static "VFO A · RX · M1" literal -- RX/TX now
        // reflects the rig's own real IsKeyed readback instead.
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("RadioStatus.VfoCaptionRx", vm.VfoKickerDisplay);

        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: true, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("RadioStatus.VfoCaptionTx", vm.VfoKickerDisplay);

        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("RadioStatus.VfoCaptionRx", vm.VfoKickerDisplay);
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
    public void CanReadCanSetBandwidth_SeededFromCapabilitiesAtConstruction()
    {
        var radioSession = new FakeRadioSessionService
        {
            Capabilities = RadioCapabilities.ReadBandwidth | RadioCapabilities.SetBandwidth,
        };

        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.CanReadBandwidth);
        Assert.True(vm.CanSetBandwidth);
        Assert.True(vm.SetBandwidthCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void CanReadCanSetBandwidth_RefreshedOnStateChanged_NotOnlyAtConstruction()
    {
        // Capabilities can go from None (before the first successful poll) to real flags mid-session
        // -- proves OnStateChanged re-reads IRadioSessionService.Capabilities live, not just the
        // constructor-time snapshot.
        var radioSession = new FakeRadioSessionService { Capabilities = RadioCapabilities.None };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.CanReadBandwidth);
        Assert.False(vm.CanSetBandwidth);

        radioSession.Capabilities = RadioCapabilities.ReadBandwidth | RadioCapabilities.SetBandwidth;
        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.CanReadBandwidth);
        Assert.True(vm.CanSetBandwidth);
    }

    [AvaloniaFact]
    public void BandwidthDisplay_PopulatedFromPolledState_PlaceholderWhenNull()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        radioSession.Push(new RadioState(
            14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow,
            BandwidthHz: 2400));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("BW 2400 Hz", vm.BandwidthDisplay);

        radioSession.Push(new RadioState(
            14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow,
            BandwidthHz: null));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("BW —", vm.BandwidthDisplay);
    }

    [AvaloniaFact]
    public void BandwidthCapabilitiesAndDisplay_ClearWhenCatLinkDropsMidSession()
    {
        // Same staleness gap this class already had to fix for RigMetersDisplay/IsKeyed -- without
        // this, a lost link would leave the BW pill showing its last live reading and an Apply
        // button a user could still click into a dead connection.
        var radioSession = new FakeRadioSessionService
        {
            Capabilities = RadioCapabilities.ReadBandwidth | RadioCapabilities.SetBandwidth,
        };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        radioSession.PushConnectionEvent(new RadioConnectionEvent(RadioConnectionState.Connected, null, null, DateTimeOffset.UtcNow));
        radioSession.Push(new RadioState(
            14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow,
            BandwidthHz: 2400));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.CanReadBandwidth);
        Assert.True(vm.CanSetBandwidth);
        Assert.Equal("BW 2400 Hz", vm.BandwidthDisplay);

        radioSession.PushConnectionEvent(new RadioConnectionEvent(RadioConnectionState.Disconnected, null, null, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.CanReadBandwidth);
        Assert.False(vm.CanSetBandwidth);
        Assert.Equal("BW —", vm.BandwidthDisplay);
        Assert.False(vm.SetBandwidthCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void SetBandwidthCommand_RoundsInputAndCallsRadioSession()
    {
        var radioSession = new FakeRadioSessionService { Capabilities = RadioCapabilities.SetBandwidth };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        vm.BandwidthInputHz = 2400.6;
        vm.SetBandwidthCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([2401], radioSession.SetBandwidthCalls);
        Assert.Null(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public void SetBandwidthCommand_BackendThrows_SetsErrorMessage()
    {
        var radioSession = new FakeRadioSessionService
        {
            Capabilities = RadioCapabilities.SetBandwidth,
            ThrowOnSetBandwidth = true,
        };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        vm.SetBandwidthCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.ErrorMessage);
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
