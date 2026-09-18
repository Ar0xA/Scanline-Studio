using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

public sealed class RadioStatusViewModelTests
{
    private static RadioStatusViewModel CreateViewModel(FakeRadioSessionService? radioSession = null, FakeSstvSessionService? sstvSession = null, FakeAppearanceSettingsService? appearanceSettings = null)
        => new(radioSession ?? new FakeRadioSessionService(), sstvSession ?? new FakeSstvSessionService(), new FakeLocalizationService(), appearanceSettings ?? new FakeAppearanceSettingsService(), NullLogger<RadioStatusViewModel>.Instance);

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
    public void SsbAsPktChecked_WhileOnUsb_ConvertsToDataAndCallsRadioSession()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedRadioMode = RadioMode.Usb;

        vm.SsbAsPkt = true;

        Assert.Equal(RadioMode.Data, vm.SelectedRadioMode);
        Assert.Contains(RadioMode.Data, radioSession.SetModeCalls);
    }

    [AvaloniaFact]
    public void SsbAsPktChecked_WhileOnLsb_ConvertsToDataR()
    {
        var vm = CreateViewModel();
        Dispatcher.UIThread.RunJobs();
        vm.SelectedRadioMode = RadioMode.Lsb;

        vm.SsbAsPkt = true;

        Assert.Equal(RadioMode.DataR, vm.SelectedRadioMode);
    }

    [AvaloniaFact]
    public void SsbAsPktUnchecked_WhileOnData_ConvertsBackToUsb()
    {
        var vm = CreateViewModel();
        Dispatcher.UIThread.RunJobs();
        vm.SsbAsPkt = true;
        vm.SelectedRadioMode = RadioMode.Data;

        vm.SsbAsPkt = false;

        Assert.Equal(RadioMode.Usb, vm.SelectedRadioMode);
    }

    [AvaloniaFact]
    public void SsbAsPktUnchecked_WhileOnDataR_ConvertsBackToLsb()
    {
        var vm = CreateViewModel();
        Dispatcher.UIThread.RunJobs();
        vm.SsbAsPkt = true;
        vm.SelectedRadioMode = RadioMode.DataR;

        vm.SsbAsPkt = false;

        Assert.Equal(RadioMode.Lsb, vm.SelectedRadioMode);
    }

    [AvaloniaFact]
    public void SsbAsPktToggled_WhileOnUnrelatedMode_IsANoOp()
    {
        // FM (and any other mode outside the Usb/Lsb/Data/DataR set) must not be touched --
        // matches how the segment buttons themselves never affect an unrelated mode. Checks the CAT
        // call count too (auditor finding): SelectedRadioMode's own self-assignment in the fallback
        // arm is equality-guarded by the generated setter, so it must NOT fire a redundant
        // SetModeAsync -- a bare SelectedRadioMode read-back alone wouldn't have caught that.
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedRadioMode = RadioMode.Fm;
        var callCountBeforeToggle = radioSession.SetModeCalls.Count;

        vm.SsbAsPkt = true;

        Assert.Equal(RadioMode.Fm, vm.SelectedRadioMode);
        Assert.Equal(callCountBeforeToggle, radioSession.SetModeCalls.Count);
    }

    [AvaloniaFact]
    public void SsbAsPktUnchecked_AfterASuppressedReadbackToAnUnrelatedMode_IsANoOp()
    {
        // Auditor finding: the one place the new switch composes with the existing rig-state-readback
        // suppression mechanism (_suppressModeCommand) -- SsbAsPkt=true, then a live rig readback
        // (Push, not a user click) reports a mode outside the Usb/Lsb/Data/DataR set, then the box is
        // unchecked. _suppressModeCommand is back to false by the time OnSsbAsPktChanged runs (reset
        // synchronously in OnStateChanged's own finally, same UI thread as this test), so this must
        // stay the fallback arm's plain no-op, not an accidental CAT command. Baseline captured BEFORE
        // Push (2nd-round auditor finding): capturing after would hide a broken suppression guard,
        // since the readback's own echoed SetModeAsync(Cw) would already be in the count either way.
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        vm.SsbAsPkt = true;
        var callCountBeforeReadback = radioSession.SetModeCalls.Count;

        radioSession.Push(new RadioState(14_230_000, RadioMode.Cw, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(callCountBeforeReadback, radioSession.SetModeCalls.Count);

        vm.SsbAsPkt = false;

        Assert.Equal(RadioMode.Cw, vm.SelectedRadioMode);
        Assert.Equal(callCountBeforeReadback, radioSession.SetModeCalls.Count);
    }

    [AvaloniaFact]
    public void SsbAsPktChanged_RaisesPropertyChangedForBothSidebandBooleans_EvenWhenSelectedRadioModeArmIsANoOp()
    {
        // 2nd-round auditor finding: mirrors SelectedRadioModeChanged_RaisesPropertyChangedForAllThree
        // SidebandBooleans's own established precedent for this exact gap class -- a getter-value
        // assertion alone doesn't prove the segment RadioButtons actually re-render. Starts already on
        // Data (not Usb) so OnSsbAsPktChanged's own switch hits the fallback (already-in-target) arm
        // and performs zero SelectedRadioMode side effect -- isolates SsbAsPkt's own
        // NotifyPropertyChangedFor attribute as the ONLY thing that can re-light the segment display.
        var vm = CreateViewModel();
        Dispatcher.UIThread.RunJobs();
        vm.SelectedRadioMode = RadioMode.Data;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SsbAsPkt = true;

        Assert.Contains(nameof(vm.IsSidebandUsb), raised);
        Assert.Contains(nameof(vm.IsSidebandLsb), raised);
    }

    [AvaloniaFact]
    public void SsbAsPktEnabled_IsSidebandUsbSetTrue_SetsDataInstead()
    {
        // Proves a FUTURE click through IsSidebandUsb's own setter also lands on the PKT variant
        // while the checkbox stays checked -- not just the one-time OnSsbAsPktChanged conversion.
        var vm = CreateViewModel();
        Dispatcher.UIThread.RunJobs();
        vm.SsbAsPkt = true;
        vm.SelectedRadioMode = RadioMode.Fm;

        vm.IsSidebandUsb = true;

        Assert.Equal(RadioMode.Data, vm.SelectedRadioMode);
        Assert.True(vm.IsSidebandUsb);
    }

    [AvaloniaFact]
    public void SsbAsPktEnabled_IsSidebandLsbSetTrue_SetsDataRInstead()
    {
        var vm = CreateViewModel();
        Dispatcher.UIThread.RunJobs();
        vm.SsbAsPkt = true;
        vm.SelectedRadioMode = RadioMode.Fm;

        vm.IsSidebandLsb = true;

        Assert.Equal(RadioMode.DataR, vm.SelectedRadioMode);
        Assert.True(vm.IsSidebandLsb);
    }

    [AvaloniaFact]
    public void SsbAsPktEnabled_SelectedModeIsData_IsSidebandUsbReadsTrue()
    {
        // The segment display side of the mapping: with SsbAsPkt on, Data/DataR are what "USB"/
        // "LSB" mean now, so the RadioButtons must show the correct one as active. Starts from Fm,
        // not the default Usb (2nd-round auditor nit): SsbAsPkt=true from a Usb start already
        // converts to Data via OnSsbAsPktChanged, making the later "act" assignment a no-op.
        var vm = CreateViewModel();
        Dispatcher.UIThread.RunJobs();
        vm.SelectedRadioMode = RadioMode.Fm;
        vm.SsbAsPkt = true;

        vm.SelectedRadioMode = RadioMode.Data;

        Assert.True(vm.IsSidebandUsb);
        Assert.False(vm.IsSidebandLsb);
    }

    // User-reported gap (2026-09-15): SsbAsPkt used to reset to false on every restart.

    [AvaloniaFact]
    public void SettingSsbAsPkt_PersistsToRadioSession()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        vm.SsbAsPkt = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(radioSession.SsbAsPktPreference);
    }

    [AvaloniaFact]
    public void Constructor_RestoresSsbAsPktFromSettings()
    {
        var radioSession = new FakeRadioSessionService { SsbAsPktPreference = true };

        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.SsbAsPkt);
    }

    [AvaloniaFact]
    public void Constructor_RestoringSsbAsPkt_DoesNotIssueAModeChangeOrResave()
    {
        // The restore-time set must be suppressed both ways: it must not silently key a real CAT
        // mode-set on the rig (the mode-conversion switch in OnSsbAsPktChanged) and must not re-save
        // the exact value it just read back.
        var radioSession = new FakeRadioSessionService { SsbAsPktPreference = true };

        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.SsbAsPkt);
        Assert.Equal(RadioMode.Usb, vm.SelectedRadioMode); // unchanged from the VM's own default
        Assert.DoesNotContain(RadioMode.Data, radioSession.SetModeCalls);
    }

    // User-reported gap (2026-09-18): a CAT-linked rig reporting PKTUSB/PKTLSB at connect time
    // correctly moved SelectedRadioMode/ModeDisplay, but left SsbAsPkt at its stale manual/persisted
    // value, so neither the USB nor the LSB segment button showed selected.

    [AvaloniaFact]
    public void RigReportsData_SsbAsPktBecomesTrue_WithoutIssuingACatModeSetOrResave()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        var callCountBeforePoll = radioSession.SetModeCalls.Count;

        radioSession.Push(new RadioState(14_230_000, RadioMode.Data, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.SsbAsPkt);
        Assert.True(vm.IsSidebandUsb);
        Assert.Equal(RadioMode.Data, vm.SelectedRadioMode); // untouched by OnSsbAsPktChanged's own conversion switch
        Assert.Equal(callCountBeforePoll, radioSession.SetModeCalls.Count);
        Assert.Empty(radioSession.SaveSsbAsPktPreferenceCalls);
    }

    [AvaloniaFact]
    public void RigReportsDataR_SsbAsPktBecomesTrue_AndIsSidebandLsbReadsTrue()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        radioSession.Push(new RadioState(14_230_000, RadioMode.DataR, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.SsbAsPkt);
        Assert.True(vm.IsSidebandLsb);
    }

    [AvaloniaFact]
    public void RigReportsUsb_AfterSsbAsPktWasTrue_SsbAsPktBecomesFalse()
    {
        // The true->false direction is the one where an escaped _suppressSsbAsPktPersist guard would
        // produce a real disk write (OnSsbAsPktChanged's own save runs unconditionally when
        // unsuppressed) -- asserted explicitly here, not just the resulting checkbox/segment state.
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        radioSession.Push(new RadioState(14_230_000, RadioMode.Data, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.SsbAsPkt);
        var callCountBeforePoll = radioSession.SetModeCalls.Count;

        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.SsbAsPkt);
        Assert.True(vm.IsSidebandUsb);
        Assert.Equal(callCountBeforePoll, radioSession.SetModeCalls.Count);
        Assert.Empty(radioSession.SaveSsbAsPktPreferenceCalls);
    }

    [AvaloniaFact]
    public void ConstructorReplaysLastKnownStateAsData_SsbAsPktIsTrue_NotTheStalePersistedFalse()
    {
        // The literal reported scenario: "at connect/startup". The constructor replays
        // LastKnownState synchronously (OnStateChanged), but GetSsbAsPktPreferenceAsync is an async
        // settings-store round trip that could otherwise complete AFTER that replay and clobber the
        // rig-derived value with the stale persisted one -- _ssbAsPktCameFromRig exists specifically
        // to stop that. SsbAsPktPreference is deliberately false here, the opposite of the rig-derived
        // true, so a race loss would be visible as a false result below.
        var radioSession = new FakeRadioSessionService
        {
            LastKnownState = new RadioState(14_230_000, RadioMode.Data, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow),
            SsbAsPktPreference = false,
        };

        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.SsbAsPkt);
        Assert.True(vm.IsSidebandUsb);
    }

    [AvaloniaFact]
    public void RigReportsUnrelatedMode_SsbAsPktIsUnaffected()
    {
        // FM/CW/RTTY/etc. must leave SsbAsPkt exactly where it was, matching how IsSidebandUsb/
        // IsSidebandLsb only ever cover the USB/LSB slots (never FM).
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        vm.SsbAsPkt = true;

        radioSession.Push(new RadioState(14_230_000, RadioMode.Fm, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.SsbAsPkt);
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

    /// <summary>User-reported 2026-09-18: a manually-typed frequency that crosses into a new band can
    /// make the rig itself recall a per-band filter default (e.g. a narrow CW width), entirely on the
    /// rig's own side -- SetFrequencyCommand never calls SetModeAsync, so nothing else would ever
    /// correct that. Re-applying the BW pill's own staged BandwidthInputHz after every successful
    /// frequency set overrides it, same reapply ApplyPresetCommand already does after its own
    /// SetModeAsync (see ApplyPresetCommand's own tests for that sibling coverage).</summary>
    [AvaloniaFact]
    public void SetFrequencyCommand_AlsoReappliesTheStagedBandwidth_WhenTheBackendCanSetIt()
    {
        var radioSession = new FakeRadioSessionService { Capabilities = RadioCapabilities.SetBandwidth };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        vm.BandwidthInputHz = 2800;

        vm.FrequencyInputMhz = "14.230000";
        vm.SetFrequencyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([14_230_000], radioSession.SetFrequencyCalls);
        Assert.Equal([2800], radioSession.SetBandwidthCalls);
        // The whole premise of the fix: the reapply must land AFTER the retune, not before.
        Assert.Equal(["frequency", "bandwidth"], radioSession.CallOrder);
    }

    [AvaloniaFact]
    public void SetFrequencyCommand_DoesNotTouchBandwidth_WhenTheBackendCannotSetIt()
    {
        var radioSession = new FakeRadioSessionService(); // Capabilities defaults to None.
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        vm.FrequencyInputMhz = "14.230000";
        vm.SetFrequencyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([14_230_000], radioSession.SetFrequencyCalls);
        Assert.Empty(radioSession.SetBandwidthCalls);
    }

    /// <summary>yoniq-auditor finding, 2026-09-18: the frequency set already succeeded once
    /// SetBandwidthAsync is reached, so a bandwidth-only failure must not leave the operator staring
    /// at a still-open editor and a misleading "No radio connected" for a retune that actually
    /// worked -- see the Dispatcher.UIThread.Post reorder this test pins.</summary>
    [AvaloniaFact]
    public void SetFrequencyCommand_BandwidthReapplyFails_StillExitsEditModeForTheSuccessfulRetune()
    {
        var radioSession = new FakeRadioSessionService
        {
            Capabilities = RadioCapabilities.SetBandwidth,
            ThrowOnSetBandwidth = true,
        };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        vm.BeginEditFrequencyCommand.Execute(null);
        vm.FrequencyInputMhz = "14.230000";

        vm.SetFrequencyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([14_230_000], radioSession.SetFrequencyCalls);
        Assert.False(vm.IsEditingFrequency);
        Assert.NotNull(vm.ErrorMessage);
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
    public void RxLevelDisplay_ReflectsSignalPeakLevel_AsPlainNumber()
    {
        var sstvSession = new FakeSstvSessionService { SignalPeakLevel = 0.5 };
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("50", vm.RxLevelDisplay);
        Assert.Equal(50.0, vm.RxLevelFillPercent);
    }

    /// <summary>2026-09-03 redesign: the meter switched from RawInputPeakLevel (raw pre-filter) to
    /// SignalPeakLevel (post-bandpass) as its source, and RxLevelInGoodRange's too-low floor dropped
    /// from a fixed 10% to 1% -- a real user report showed a 3-4% raw reading decoding a clean
    /// image, so the old 10% floor was flagging working signals as red. The too-hot side no longer
    /// has its own independently-calibrated raw-percentage threshold at all -- it now reuses
    /// IsLevelOverdriven (legacy's own real, already-ported clipping threshold) directly, so this
    /// theory drives that fake bool instead of a second peak-level percentage.</summary>
    [AvaloniaTheory]
    [InlineData(0.0, false, false)]  // near-silent -- red
    [InlineData(0.005, false, false)] // near-silent -- red
    [InlineData(0.01, false, true)]  // exactly at the floor -- >=, not >, so this is green
    [InlineData(0.03, false, true)]  // quiet but real (the user-reported case) -- green
    [InlineData(0.5, false, true)]   // good -- green
    [InlineData(0.95, true, false)]  // legacy overdrive threshold tripped -- red
    [InlineData(0.5, true, false)]   // overdrive can trip regardless of the peak-level reading -- red
    public void RxLevelInGoodRange_ReflectsSignalPeakLevelFloorAndLegacyOverdriveThreshold(double signalPeakLevel, bool isLevelOverdriven, bool expectedInGoodRange)
    {
        var sstvSession = new FakeSstvSessionService { SignalPeakLevel = signalPeakLevel, IsLevelOverdriven = isLevelOverdriven };
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(expectedInGoodRange, vm.RxLevelInGoodRange);
    }

    // User-requested (2026-09-18): a real-time "Decoding" indicator, distinct from the steady
    // "Receiving" state, obvious even from a non-Receive tab. Reads IReceivedImageBuffer.Progress
    // via the same tick the constructor now runs synchronously once up front (see
    // RadioStatusViewModel.OnRxAudioLevelTick's own doc comment), so these are all
    // construction-time assertions -- no real 250ms timer wait needed, same testability shape
    // RxAudioPeakLevel's own tests above already rely on.

    [AvaloniaTheory]
    [InlineData(null, false)] // idle -- no active decode
    [InlineData(0.0, true)]   // just started (ModeDetected)
    [InlineData(0.5, true)]   // mid-decode
    [InlineData(1.0, false)]  // exactly complete -- not an ongoing decode anymore
    public void IsDecodingImage_ReflectsReceivedImageProgress(double? progress, bool expectedIsDecoding)
    {
        var sstvSession = new FakeSstvSessionService();
        ((FakeReceivedImageBuffer)sstvSession.ReceivedImage).Progress = progress;

        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(expectedIsDecoding, vm.IsDecodingImage);
    }

    [AvaloniaFact]
    public void WhileDecoding_ReceivingButtonLabelAndStatusBarText_BothSayDecoding()
    {
        var sstvSession = new FakeSstvSessionService { IsReceiving = true };
        ((FakeReceivedImageBuffer)sstvSession.ReceivedImage).Progress = 0.3;

        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        // FakeLocalizationService.GetString echoes the raw key (see its own doc comment) --
        // asserting the key itself is what proves the correct string was requested, same
        // convention TuneButtonLabel/ReceivingButtonLabel's own existing tests never needed until
        // now because they only ever asserted the underlying bool, not the label text itself.
        Assert.Equal("RadioStatus.Decoding", vm.ReceivingButtonLabel);
        Assert.Equal("MainWindow.StatusBar.Decoding", vm.RxStatusBarText);
    }

    [AvaloniaFact]
    public void WhileDecoding_IsReceivingIdle_IsFalse_EvenThoughIsReceivingIsTrue()
    {
        // The green "Receiving" look and the amber "Decoding" look must be mutually exclusive by
        // construction (both AXAML call sites bind their green classes to this, not to IsReceiving
        // directly) -- not left to style declaration order to arbitrate a simultaneous true/true.
        var sstvSession = new FakeSstvSessionService { IsReceiving = true };
        ((FakeReceivedImageBuffer)sstvSession.ReceivedImage).Progress = 0.3;

        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsReceiving);
        Assert.False(vm.IsReceivingIdle);
    }

    [AvaloniaFact]
    public void WhileJustListening_NotDecoding_ReceivingButtonLabelAndStatusBarText_SayReceiving()
    {
        var sstvSession = new FakeSstvSessionService { IsReceiving = true };
        ((FakeReceivedImageBuffer)sstvSession.ReceivedImage).Progress = null;

        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsDecodingImage);
        Assert.True(vm.IsReceivingIdle);
        Assert.Equal("RadioStatus.Receiving", vm.ReceivingButtonLabel);
        Assert.Equal("MainWindow.StatusBar.Rx", vm.RxStatusBarText);
    }

    [AvaloniaFact]
    public void WhileIdle_NotDecoding_IsDecodingBlinkOnIsFalse()
    {
        var vm = CreateViewModel();
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsDecodingBlinkOn);
    }

    // User-requested (2026-09-18): the blink can be turned off in Options > Appearance, default on.
    // Disabled means steady amber for the whole decode, not "never light up" -- IsDecodingImage
    // itself is unaffected, only IsDecodingBlinkOn's own on/off cycling is.

    [AvaloniaFact]
    public void WhileDecoding_BlinkDisabledInAppearanceSettings_IsDecodingBlinkOnIsSteadyTrue()
    {
        var appearanceSettings = new FakeAppearanceSettingsService { DecodingIndicatorBlinks = false };
        var sstvSession = new FakeSstvSessionService();
        ((FakeReceivedImageBuffer)sstvSession.ReceivedImage).Progress = 0.3;

        var vm = CreateViewModel(sstvSession: sstvSession, appearanceSettings: appearanceSettings);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsDecodingImage);
        Assert.True(vm.IsDecodingBlinkOn);
    }

    [AvaloniaFact]
    public void WhileDecoding_BlinkEnabledByDefault_MatchesAppearanceSettingsDefault()
    {
        // No FakeAppearanceSettingsService override -- proves the wiring reads the SAME default
        // AppearanceSettings.DefaultDecodingIndicatorBlinks itself declares, not a second
        // independently-hardcoded "true" this ViewModel could silently drift from.
        var appearanceSettings = new FakeAppearanceSettingsService();
        Assert.Equal(ScanlineStudio.UI.Settings.AppearanceSettings.DefaultDecodingIndicatorBlinks, appearanceSettings.DecodingIndicatorBlinks);
        var sstvSession = new FakeSstvSessionService();
        ((FakeReceivedImageBuffer)sstvSession.ReceivedImage).Progress = 0.3;

        var vm = CreateViewModel(sstvSession: sstvSession, appearanceSettings: appearanceSettings);
        Dispatcher.UIThread.RunJobs();

        // Two OnRxAudioLevelTick() calls land before RunJobs() returns: the constructor's own
        // synchronous initial tick (counter -> 1, "off" phase), then
        // LoadDecodingIndicatorBlinksPreferenceSafeAsync's post-load re-tick (counter -> 2, "on"
        // phase) -- see that method's own doc comment for why the re-tick exists. 2 % 2 == 0 is
        // true, so this settles on-phase deterministically once RunJobs() drains both.
        Assert.True(vm.IsDecodingBlinkOn);
    }

    [AvaloniaFact]
    public void LiveAppearanceChange_WhileAlreadyDecoding_TakesEffectImmediately_NotOnTheNextPoll()
    {
        // Auditor-class startup/live-update-ordering finding, applied proactively: proves
        // DecodingIndicatorBlinksChanged's own handler re-evaluates the blink state SYNCHRONOUSLY
        // (via a direct OnRxAudioLevelTick() re-run) rather than waiting for the next real 250ms
        // timer tick, which this test has no way to force forward.
        var appearanceSettings = new FakeAppearanceSettingsService();
        var sstvSession = new FakeSstvSessionService();
        ((FakeReceivedImageBuffer)sstvSession.ReceivedImage).Progress = 0.3;
        var vm = CreateViewModel(sstvSession: sstvSession, appearanceSettings: appearanceSettings);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsDecodingImage);

        appearanceSettings.NotifyDecodingIndicatorBlinksChanged(false);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsDecodingBlinkOn);
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

    /// <summary>TX-pane Tune button plan (2026-09-01), auditor code-review finding: proves the Stop
    /// path is reachable through a REAL UI click (`CanExecute` + `Execute`), not just via a direct
    /// `ExecuteAsync` call that bypasses `CanExecute` entirely -- the shape every other Tune test in
    /// this file uses, which would stay green even if `AllowConcurrentExecutions` regressed back to
    /// its CommunityToolkit default (`false`), the exact bug this test exists to catch (already found
    /// once on the sibling `OptionsWindowViewModel.TestPttCommand`, see that command's own doc
    /// comment). Without `AllowConcurrentExecutions = true`, `CanExecute` would be `false` while
    /// `IsTuning`, so the second (Stop) click below would never reach the `IsTuning` branch at all.</summary>
    [AvaloniaFact]
    public async Task TuneCommand_ClickedWhileTuning_CanExecuteStaysTrue_AndStopsTheTone()
    {
        var sstvSession = new FakeSstvSessionService { TuneGate = new TaskCompletionSource() };
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        vm.TuneCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsTuning);

        // The real bug this guards against: without AllowConcurrentExecutions = true, this would be
        // false, and the button a real click drives through would already be disabled/unreachable.
        Assert.True(vm.TuneCommand.CanExecute(null));

        vm.TuneCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(100);
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
    public void ReceivingButtonLabel_TracksIsReceiving()
    {
        // User-reported gap (2026-09-15): the Receiving toggle's Content used to be a static loc
        // string regardless of checked state -- proves the label actually flips both ways, same
        // shape as the TuneButtonLabel tests above.
        var sstvSession = new FakeSstvSessionService { IsReceiving = true };
        var vm = CreateViewModel(sstvSession: sstvSession);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("RadioStatus.Receiving", vm.ReceivingButtonLabel);

        vm.IsReceiving = false;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("RadioStatus.ReceivingMuted", vm.ReceivingButtonLabel);
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

        // T1-13 (production_audit.md): SWR/ALC/PWR now route through FakeLocalizationService, which
        // echoes the raw key -- not a formatted string. Same convention as
        // TestRigctldConnectionCommand_Failure_SetsStatusMessageToFailedKey elsewhere in this suite.
        Assert.Equal("RadioStatus.Meters.SwrFormat · RadioStatus.Meters.AlcFormat · RadioStatus.Meters.PwrFormat", vm.RigMetersDisplay);
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

        // T1-13 (production_audit.md): see the AllThreeMetersPresent test's own comment above.
        Assert.Equal("RadioStatus.Meters.SwrFormat · RadioStatus.Meters.PwrFormat", vm.RigMetersDisplay);
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
        // T1-13 (production_audit.md): SWR/ALC/PWR now route through FakeLocalizationService, which
        // echoes the raw key -- not a formatted string. Same convention as
        // TestRigctldConnectionCommand_Failure_SetsStatusMessageToFailedKey elsewhere in this suite.
        Assert.Equal("RadioStatus.Meters.SwrFormat · RadioStatus.Meters.AlcFormat · RadioStatus.Meters.PwrFormat", vm.RigMetersDisplay);

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
        // T1-13 (production_audit.md): now routes through FakeLocalizationService, which echoes the
        // raw key -- not a formatted string.
        Assert.Equal("RadioStatus.BandwidthDisplayFormat", vm.BandwidthDisplay);

        radioSession.Push(new RadioState(
            14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow,
            BandwidthHz: null));
        Dispatcher.UIThread.RunJobs();
        // T1-13 (production_audit.md): now routes through FakeLocalizationService's raw-key echo.
        Assert.Equal("RadioStatus.BandwidthUnavailable", vm.BandwidthDisplay);
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
        // T1-13 (production_audit.md): now routes through FakeLocalizationService, which echoes the
        // raw key -- not a formatted string.
        Assert.Equal("RadioStatus.BandwidthDisplayFormat", vm.BandwidthDisplay);

        radioSession.PushConnectionEvent(new RadioConnectionEvent(RadioConnectionState.Disconnected, null, null, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.CanReadBandwidth);
        Assert.False(vm.CanSetBandwidth);
        // T1-13 (production_audit.md): now routes through FakeLocalizationService's raw-key echo.
        Assert.Equal("RadioStatus.BandwidthUnavailable", vm.BandwidthDisplay);
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

    /// <summary>2026-09-03: the bandwidth field became an editable ComboBox with quick-pick presets
    /// (same "ItemsSource + IsEditable" pattern as OptionsWindowView's Sample rate field) -- pins the
    /// preset list itself, that BandwidthInputHz still defaults to 2400 (an auditor domain-judgment
    /// review confirmed this is the right default given the CAT layer can only ever set width, never
    /// passband center/shift), and that a value NOT in the preset list still round-trips through
    /// SetBandwidthCommand exactly as a NumericUpDown's free entry did -- the list is a convenience,
    /// not a validation constraint.</summary>
    [AvaloniaFact]
    public void AvailableBandwidthPresetsHz_HasThe1800To2800Range_AndAnOutOfListValueStillApplies()
    {
        var radioSession = new FakeRadioSessionService { Capabilities = RadioCapabilities.SetBandwidth };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([1800.0, 2400.0, 2800.0], vm.AvailableBandwidthPresetsHz);
        Assert.Equal(2400.0, vm.BandwidthInputHz);

        vm.BandwidthInputHz = 2200; // not in the preset list
        vm.SetBandwidthCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([2200], radioSession.SetBandwidthCalls);
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

    private static FakeRadioSessionService BandwidthCapableSession(params FrequencyPreset[] presets)
        => new()
        {
            Presets = presets,
            Capabilities = RadioCapabilities.SetBandwidth,
        };

    [AvaloniaFact]
    public async Task ApplyPreset_SendsBandwidthAfterMode()
    {
        // Order is the whole point: setting the mode is what makes the rig fall back to its own
        // default passband, so a bandwidth sent before the mode would simply be overwritten. Call
        // counts alone cannot catch that, which is why FakeRadioSessionService records CallOrder.
        var radioSession = BandwidthCapableSession(new FrequencyPreset("SSTV", 14_230_000, RadioMode.Usb, 2400));
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        await vm.ApplyPresetCommand.ExecuteAsync(radioSession.Presets[0]);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["frequency", "mode", "bandwidth"], radioSession.CallOrder);
        Assert.Equal(2400, Assert.Single(radioSession.SetBandwidthCalls));
    }

    [AvaloniaFact]
    public async Task ApplyPreset_NoStoredBandwidthOnSsbMode_Sends2400()
    {
        var radioSession = BandwidthCapableSession(new FrequencyPreset("SSTV", 14_230_000, RadioMode.Usb));
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        await vm.ApplyPresetCommand.ExecuteAsync(radioSession.Presets[0]);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2400, Assert.Single(radioSession.SetBandwidthCalls));
    }

    [AvaloniaFact]
    public async Task ApplyPreset_NoStoredBandwidthOnFmMode_Sends15000()
    {
        // A single app-wide 2400 default would be the 500Hz bug mirrored: FM SSTV needs roughly
        // 12-15kHz, so 2400 would clip it exactly the way 500 clips SSB SSTV.
        var radioSession = BandwidthCapableSession(new FrequencyPreset("10m FM", 29_600_000, RadioMode.Fm));
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        await vm.ApplyPresetCommand.ExecuteAsync(radioSession.Presets[0]);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(15_000, Assert.Single(radioSession.SetBandwidthCalls));
    }

    [AvaloniaFact]
    public async Task ApplyPreset_BackendCannotSetBandwidth_SendsFrequencyAndModeOnly()
    {
        // flrig and OmniRig THROW from SetBandwidthAsync rather than no-op'ing, so an ungated call
        // would fail the click AFTER the frequency and mode had already changed.
        var radioSession = new FakeRadioSessionService
        {
            Presets = [new FrequencyPreset("SSTV", 14_230_000, RadioMode.Usb, 2400)],
            Capabilities = RadioCapabilities.None,
            ThrowOnSetBandwidth = true,
        };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        await vm.ApplyPresetCommand.ExecuteAsync(radioSession.Presets[0]);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["frequency", "mode"], radioSession.CallOrder);
        Assert.Empty(radioSession.SetBandwidthCalls);
        Assert.Null(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task ApplyPreset_StoredBandwidthIsZero_SubstitutesFamilyFallbackAndNeverSendsZero()
    {
        // 0 IS Hamlib's RIG_PASSBAND_NORMAL, so sending it would silently reinstate the very
        // rig-picks-its-own-width behaviour this feature exists to replace.
        var radioSession = BandwidthCapableSession(new FrequencyPreset("Hand edited", 14_230_000, RadioMode.Usb, 0));
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        await vm.ApplyPresetCommand.ExecuteAsync(radioSession.Presets[0]);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2400, Assert.Single(radioSession.SetBandwidthCalls));
    }

    [AvaloniaFact]
    public async Task ApplyPreset_WideBandwidthTypedOnSsbMode_SendsItUnchanged()
    {
        // The mode family drives the editor's snap-on-mode-change only. It must never gate the apply
        // path, or a width the operator typed, saved and can still see would be silently corrected.
        var radioSession = BandwidthCapableSession(new FrequencyPreset("Wide", 14_230_000, RadioMode.Usb, 12_000));
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        await vm.ApplyPresetCommand.ExecuteAsync(radioSession.Presets[0]);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(12_000, Assert.Single(radioSession.SetBandwidthCalls));
    }

    [AvaloniaFact]
    public async Task ApplyPreset_UnknownMode_SendsNothingAtAllAndReportsTheModeError()
    {
        // Without the up-front mode guard, SetFrequencyAsync retunes the rig first and SetModeAsync
        // then throws ArgumentOutOfRangeException, which the catch maps to "no radio connected" --
        // a wrong message after a real, already-applied frequency change.
        var radioSession = BandwidthCapableSession(new FrequencyPreset("Bad", 14_230_000, RadioMode.Unknown, 2400));
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        await vm.ApplyPresetCommand.ExecuteAsync(radioSession.Presets[0]);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(radioSession.CallOrder);
        Assert.Equal("RadioStatus.Error.InvalidPresetMode", vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task ApplyPreset_NoFamilyMode_SendsFrequencyAndModeButNoBandwidth()
    {
        // AM is mappable, so it reaches the rig -- but this app has no opinion on an AM filter width,
        // and inventing one would be worse than leaving the rig's own.
        var radioSession = BandwidthCapableSession(new FrequencyPreset("AM", 3_885_000, RadioMode.Am));
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        await vm.ApplyPresetCommand.ExecuteAsync(radioSession.Presets[0]);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["frequency", "mode"], radioSession.CallOrder);
        Assert.Empty(radioSession.SetBandwidthCalls);
    }

    [AvaloniaFact]
    public async Task SavePresets_BandwidthOutOfRange_AbortsAndLeavesEditorRowsUntouched()
    {
        var radioSession = new FakeRadioSessionService
        {
            Presets = [new FrequencyPreset("Good", 14_230_000, RadioMode.Usb, 2400)],
        };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        var row = Assert.Single(vm.EditorRows);
        row.BandwidthHz = 0;

        await vm.SavePresetsCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(vm.EditorRows);
        Assert.Equal(2400, Assert.Single(radioSession.Presets).BandwidthHz); // never reached the store
        Assert.NotNull(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public void EditorRow_ModeChangedAcrossFamilies_MovesQuickPicksAndSnapsTheValue()
    {
        var radioSession = new FakeRadioSessionService
        {
            Presets = [new FrequencyPreset("SSTV", 14_230_000, RadioMode.Usb, 2400)],
        };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        var row = Assert.Single(vm.EditorRows);

        row.SelectedMode = RadioMode.Fm;
        Assert.Equal([9000d, 12_000d, 15_000d], row.AvailableBandwidthPresetsHz);
        Assert.Equal(15_000, row.BandwidthHz);

        row.SelectedMode = RadioMode.Usb;
        Assert.Equal([1800d, 2400d, 2800d], row.AvailableBandwidthPresetsHz);
        Assert.Equal(2400, row.BandwidthHz);
    }

    [AvaloniaFact]
    public void EditorRow_ModeChangedWithinFamily_KeepsATypedWidth()
    {
        var radioSession = new FakeRadioSessionService
        {
            Presets = [new FrequencyPreset("SSTV", 14_230_000, RadioMode.Usb, 2800)],
        };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        var row = Assert.Single(vm.EditorRows);

        row.SelectedMode = RadioMode.Data;

        Assert.Equal(2800, row.BandwidthHz);
    }

    [AvaloniaFact]
    public void EditorRow_NoFamilyModeWithNoStoredBandwidth_SeedsASaveableValue()
    {
        // Without the no-family seed this row would hold 0, SavePresets rejects 0, and one
        // hand-edited row would make the whole editor unsaveable for every other row too.
        var radioSession = new FakeRadioSessionService
        {
            Presets = [new FrequencyPreset("Hand edited", 3_885_000, RadioMode.Am)],
        };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        var row = Assert.Single(vm.EditorRows);
        Assert.Equal(2400, row.BandwidthHz);
    }

    [AvaloniaFact]
    public void PresetModeChoices_HoldsExactlyTheSixSstvModes()
    {
        var vm = CreateViewModel();

        Assert.Equal(
            [RadioMode.Usb, RadioMode.Lsb, RadioMode.Fm, RadioMode.Data, RadioMode.DataR, RadioMode.Pkt],
            vm.PresetModeChoices.Select(choice => choice.Mode));
    }

    [AvaloniaTheory]
    [InlineData(RadioMode.Cw)]
    [InlineData(RadioMode.Am)]
    [InlineData(RadioMode.Unknown)]
    public void CanStoreCurrentPreset_RigOnAModeTheEditorCannotShow_IsFalse(RadioMode mode)
    {
        // "Store current" copies whatever the rig reports, so without this gate it can create a
        // Favourite the six-mode picker cannot display.
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        radioSession.Push(new RadioState(14_230_000, mode, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.StoreCurrentPresetCommand.CanExecute(null));

        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.StoreCurrentPresetCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task StoreCurrentPreset_RigBandwidthOutOfRange_CapturesTheFallbackInsteadOfFailingTheSave()
    {
        // A live rig reading must never reach the validator raw: the save would abort, the row would
        // be removed, and the frequency/mode capture would be lost too -- over a value the operator
        // never typed.
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow, BandwidthHz: 45_000));
        Dispatcher.UIThread.RunJobs();

        await vm.StoreCurrentPresetCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2400, Assert.Single(radioSession.Presets).BandwidthHz);
        Assert.Null(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task StoreCurrentPreset_RigBandwidthInRange_CapturesTheLiveReading()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow, BandwidthHz: 2800));
        Dispatcher.UIThread.RunJobs();

        await vm.StoreCurrentPresetCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2800, Assert.Single(radioSession.Presets).BandwidthHz);
    }

    [AvaloniaFact]
    public async Task ApplyPreset_NoFamilyModeWithAStoredBandwidth_StillSendsNoBandwidthCommand()
    {
        // Code-review finding: the stored value used to win before the family was ever consulted, so
        // a hand-edited AM Favourite (whose editor row seeds a saveable 2400, which any Save then
        // persists) would narrow the rig to an SSB width on an AM channel.
        var radioSession = BandwidthCapableSession(new FrequencyPreset("AM", 3_885_000, RadioMode.Am, 2400));
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        await vm.ApplyPresetCommand.ExecuteAsync(radioSession.Presets[0]);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["frequency", "mode"], radioSession.CallOrder);
        Assert.Empty(radioSession.SetBandwidthCalls);
    }

    [AvaloniaFact]
    public async Task EditorRow_StoredBandwidthOutOfRange_SeedsASaveableValueInsteadOfBlockingTheSave()
    {
        // Code-review finding: the seed used to pass a stored value through verbatim, so one
        // hand-edited row with "BandwidthHz": 0 made the whole editor unsaveable -- discarding every
        // OTHER row's unsaved edits along with it.
        var radioSession = new FakeRadioSessionService
        {
            Presets =
            [
                new FrequencyPreset("Hand edited", 14_230_000, RadioMode.Usb, 0),
                new FrequencyPreset("Fine", 7_171_000, RadioMode.Lsb, 2400),
            ],
        };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2400, vm.EditorRows[0].BandwidthHz);
        vm.EditorRows[1].Label = "Edited";

        await vm.SavePresetsCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.ErrorMessage);
        Assert.Equal("Edited", radioSession.Presets[1].Label);
    }

    [AvaloniaFact]
    public void EditorRow_ModeChangedWithinFamily_KeepsTheSameQuickPickListInstance()
    {
        // Code-review finding: a freshly allocated array per mode change swaps the bound ComboBox's
        // ItemsSource IDENTITY even when the contents are identical, which resets the control's own
        // selection for no reason.
        var radioSession = new FakeRadioSessionService
        {
            Presets = [new FrequencyPreset("SSTV", 14_230_000, RadioMode.Usb, 2400)],
        };
        var vm = CreateViewModel(radioSession);
        Dispatcher.UIThread.RunJobs();
        var row = Assert.Single(vm.EditorRows);
        var before = row.AvailableBandwidthPresetsHz;

        row.SelectedMode = RadioMode.Lsb;

        Assert.Same(before, row.AvailableBandwidthPresetsHz);
    }
}
