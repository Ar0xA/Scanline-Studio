using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Settings;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Services;
using ScanlineStudio.UI.Settings;

namespace ScanlineStudio.UI.ViewModels;

public sealed partial class OptionsWindowViewModel
{
    /// <summary>Deliberately NOT the same idiom as <see cref="RadioStatusViewModel.OnConnectionEvent"/>'s
    /// own <c>CatLinked</c> -- that property is a "genuinely reachable RIGHT NOW" status light,
    /// intentionally flapping false during a backoff/reconnect retry. This button instead answers
    /// "would <see cref="IRadioSessionService.DisconnectAsync"/> actually do something" -- exactly
    /// <see cref="IRadioSessionService.RigId"/>'s own contract (<c>RadioController</c>'s own doc
    /// comment: reset to "none" ONLY on an explicit Disconnect, never by a transient
    /// reconnect-backoff cycle). Live-reproduced bug this fixes: mirroring CatLinked's flapping
    /// semantics here made the button's own label disagree with what <c>RigId</c> (the actual gate
    /// Test CAT/Test PTT check) said during a poll retry -- a click landed on the wrong action
    /// entirely. <see cref="RadioConnectionState.Connecting"/>/<see cref="RadioConnectionState.Reconnecting"/>/
    /// <see cref="RadioConnectionState.Failed"/>/<see cref="RadioConnectionState.CommandFailed"/> are
    /// ALL excluded -- none of them change <c>RigId</c>, only <see cref="RadioConnectionState.Connected"/>/
    /// <see cref="RadioConnectionState.Disconnected"/> do. Code-review finding: this holds because
    /// every backend's <c>RigId</c> is a fixed compile-time constant per protocol TYPE (e.g.
    /// <c>"hamlib-native"</c>, <c>"rigctld-client"</c>), so <c>RadioController</c>'s own internal
    /// reconnect-after-backoff path (which reassigns <c>_rigId</c> with no published event) always
    /// reassigns the SAME value -- if a future backend ever derives <c>RigId</c> at connect time
    /// instead, that path could change it silently and this property would go stale.</summary>
    private void OnConnectionEvent(RadioConnectionEvent evt)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // IsRadioConnected itself stays exactly as documented above -- only Connected/Disconnected
            // touch it, deliberately, so the button's action/enablement never flaps during a normal
            // backoff retry. RadioLinkStatusMessage is a SEPARATE, purely presentational signal built
            // on IsGenuinelyConnected, which DOES reset on Reconnecting/Failed -- so it must be
            // re-evaluated on every event, not just these two, or it would freeze (stop updating, not
            // show wrong text) for the whole duration of a later backoff episode. Order matters:
            // IsRadioConnected is assigned FIRST so RadioLinkStatusMessage's own getter (which reads
            // IsRadioConnected) sees the settled value when the explicit notify below runs, not the
            // stale one from before this event.
            if (evt.State is RadioConnectionState.Connected or RadioConnectionState.Disconnected)
            {
                IsRadioConnected = evt.State == RadioConnectionState.Connected;
            }

            // Give-up-after-5 feature: a Disconnected event carrying a non-null Reason means the poll
            // loop gave up automatically (RadioController's own give-up branch), not a real Disconnect
            // click (which always publishes reason: null). Surfaced only here, not woven into
            // IsRadioConnected/ConnectRadioTooltip's own logic above -- this dialog may not even be
            // open when a give-up happens; see RadioStatusViewModel.ConnectionGaveUp for the
            // always-reachable must-acknowledge popup that also fires for this same signal.
            if (evt.State == RadioConnectionState.Disconnected && evt.Reason is not null)
            {
                ConnectRadioErrorMessage = _localization.GetString("Options.Radio.Connect.GaveUp", evt.Reason);
            }

            OnPropertyChanged(nameof(RadioLinkStatusMessage));
            OnPropertyChanged(nameof(ShowSwrCutoffCapabilityHint));
        });
    }

    /// <summary>Purely presentational -- see <see cref="OnConnectionEvent"/>'s own comment for why this
    /// is deliberately separate from <see cref="IsRadioConnected"/> rather than folded into it. User-
    /// reported gap: the button correctly says "Disconnect" the instant a backend resolves (see
    /// <see cref="IsRadioConnected"/>'s own doc comment for why that's correct), but nothing told the
    /// operator whether that session had ever actually been verified reachable -- this fills that gap
    /// without touching the button's own action/enablement contract at all. `RadioBackendId != "none"`
    /// guards the "None" backend specifically: its own poll never returns (there is nothing to
    /// confirm), so without this a stray Connected event reaching an open dialog for "no radio
    /// configured" could show "not yet confirmed reachable" for a config with nothing to reach.</summary>
    public string? RadioLinkStatusMessage =>
        IsRadioConnected && !_radioSession.IsGenuinelyConnected && RadioBackendId != "none"
            ? _localization.GetString("Options.Radio.Connect.NotYetConfirmed")
            : null;

    /// <summary>The capability-awareness the old TX-pane control's <c>IsEnabled="{Binding
    /// ShowSwrMeter}"</c> greying used to provide, in non-disabling form (see
    /// <see cref="SwrCutoffEnabled"/>'s own doc comment for why this control stays editable instead)
    /// -- without this, an flrig user (confirmed zero SWR support) could tick the checkbox above and
    /// get no indication anywhere that it will never fire. Gated on <see cref="IsRadioConnected"/>
    /// (a real session actually exists, matching that property's own "would Disconnect do something"
    /// contract), deliberately NOT <see cref="RadioBackendId"/> (the dropdown SELECTION, which can be
    /// edited to a different, not-yet-connected backend while a genuinely different one stays live --
    /// gating on the dropdown pick would show a stale/wrong capability read for whatever backend is
    /// ACTUALLY connected right now). <see langword="true"/> only once a session exists AND the
    /// currently connected/negotiated backend does not report <see cref="RadioCapabilities.SwrMeter"/>.
    /// Re-evaluated on every <see cref="OnConnectionEvent"/>, not just at load -- <see cref="OnConnectionEvent"/>'s
    /// own doc comment.</summary>
    public bool ShowSwrCutoffCapabilityHint =>
        IsRadioConnected && !_radioSession.Capabilities.HasFlag(RadioCapabilities.SwrMeter);

    [RelayCommand]
    private void RefreshSerialPorts()
    {
        AvailableSerialPorts.Clear();
        foreach (var port in _serialPortEnumerator.GetPortNames())
        {
            AvailableSerialPorts.Add(port);
        }
    }

    public string ConnectRadioButtonLabel => _localization.GetString(IsRadioConnected ? "Options.Radio.Disconnect" : "Options.Radio.Connect");

    public bool CanConnectRadio =>
        IsRigctldBackendSelected ? RigctldTestSucceeded :
        IsHamlibBackendSelected ? HamlibCatTestSucceeded && (HamlibPttTestSucceeded || IsPttMethodVoxSelected) :
        IsFlrigBackendSelected ? FlrigTestSucceeded && FlrigPttTestSucceeded :
        IsOmniRigBackendSelected ? OmniRigTestSucceeded :
        false; // None (nothing to connect to) or no backend selected.

    /// <summary>Bound to the Connect/Disconnect button's own <c>IsEnabled</c> -- Connect is gated by
    /// <see cref="CanConnectRadio"/>, but Disconnect never is: you must always be able to
    /// disconnect regardless of test state. <see cref="IsConnectingRadio"/> disables the button for
    /// the duration of Connect's own implicit save (ui_transition_plan.md step 7) -- that save can
    /// take several seconds (a Hamlib native library reload, a sample-rate device reopen), and this
    /// is the only path through this dialog where a button click now kicks off I/O that slow.</summary>
    public bool CanToggleRadioConnection => !IsConnectingRadio && (IsRadioConnected || CanConnectRadio);

    /// <summary>Shown via <c>ToolTip.ShowOnDisabled</c> (Avalonia doesn't show tooltips on a
    /// disabled control by default) so a disabled Connect button actually explains why, instead of
    /// just sitting there greyed out with no explanation.</summary>
    public string ConnectRadioTooltip => _localization.GetString(IsRadioConnected switch
    {
        true => "Options.Radio.Disconnect.Help",
        false when IsNoneBackendSelected => "Options.Radio.Connect.Help.None",
        false when IsRigctldBackendSelected && !RigctldTestSucceeded => "Options.Radio.Connect.Help.NeedsTestConnection",
        false when IsHamlibBackendSelected && !HamlibCatTestSucceeded => "Options.Radio.Connect.Help.NeedsTestCat",
        false when IsHamlibBackendSelected && !HamlibPttTestSucceeded && !IsPttMethodVoxSelected => "Options.Radio.Connect.Help.NeedsTestPtt",
        false when IsFlrigBackendSelected && !FlrigTestSucceeded => "Options.Radio.Connect.Help.NeedsTestConnection",
        false when IsFlrigBackendSelected && !FlrigPttTestSucceeded => "Options.Radio.Connect.Help.NeedsTestPtt",
        false when IsOmniRigBackendSelected && !OmniRigTestSucceeded => "Options.Radio.Connect.Help.NeedsTestConnection",
        false => "Options.Radio.Connect.Help",
    });

    /// <summary>Toggle, same shape as <see cref="TuneCommand"/>/<see cref="TestPttCommand"/> above --
    /// connects using PERSISTED settings (<see cref="IRadioSessionService.ConnectUsingSettingsAsync"/>'s
    /// own contract, same as app startup), not whatever is currently typed into this dialog. Prior
    /// to ui_transition_plan.md step 7, that meant Test could pass against a freshly-typed value
    /// while Connect silently used the last SAVED one -- Connect now saves first (via
    /// <see cref="SaveCoreAsync"/>, same call <see cref="ApplyAsync"/> uses) so it always connects
    /// with whatever Test just validated. Only when <see cref="_loadSucceeded"/>: mirrors
    /// <see cref="RestartNowAsync"/>'s own guard -- a dialog that failed to load has no real
    /// snapshot to save, and must not persist hardcoded constructor defaults over real settings. The
    /// <see cref="CanConnectRadio"/> check below is a defensive no-op mirroring the button's own
    /// <c>IsEnabled</c> binding (<see cref="CanToggleRadioConnection"/> in XAML) -- same "internal
    /// guard alongside an IsEnabled binding, not a formal CanExecute" idiom <see cref="TestPttAsync"/>
    /// already uses, so a direct <c>ExecuteAsync</c> call (e.g. from a test) can't bypass it
    /// either.</summary>
    [RelayCommand]
    private async Task ToggleRadioConnectionAsync()
    {
        if (IsRadioConnected)
        {
            Log.DisconnectRadioInvoked(_logger);
            try
            {
                // T1-8 (production_audit.md): RadioController.DisconnectAsync now throws
                // TimeoutException if another lifecycle call holds the internal lock past its own
                // bound (a new possibility this method never had to handle before -- every prior
                // internal failure was swallowed and logged inside RadioController itself). Uncaught,
                // this would surface as an unhandled exception on the UI thread via
                // AsyncRelayCommand's own rethrow-on-fault behavior.
                await _radioSession.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.DisconnectRadioFailed(_logger, ex);
                Dispatcher.UIThread.Post(() => ConnectRadioErrorMessage = _localization.GetString("Options.Radio.Disconnect.Failed", ex.Message));
                return;
            }

            // Stale test-status messages ("already connected") no longer apply once actually
            // disconnected -- same reasoning as OnRadioBackendIdChanged's own clearing below.
            Dispatcher.UIThread.Post(() =>
            {
                TestConnectionStatusMessage = null;
                TestPttErrorMessage = null;
                ConnectRadioErrorMessage = null;
            });
            return;
        }

        if (!CanConnectRadio)
        {
            return;
        }

        Log.ConnectRadioInvoked(_logger);
        ConnectRadioErrorMessage = null;
        IsConnectingRadio = true;
        try
        {
            if (_loadSucceeded && !await SaveCoreAsync())
            {
                ConnectRadioErrorMessage = _localization.GetString("Options.Radio.Connect.SaveFailed", SaveErrorMessage ?? string.Empty);
                return;
            }

            // Hamlib's own library-path reload can fail INSIDE a save that otherwise succeeds
            // (SaveCoreUnguardedAsync sets _hamlibLibraryReloadFailed but still returns true for the
            // rest of the snapshot) -- connecting anyway would silently CAT against the OLD loaded
            // library with no indication anything was wrong. No modal here (unlike
            // ShowPostSaveWarningsAsync, which Save/Apply use) -- this reuses the same inline error
            // slot Connect already shows failures in.
            if (_hamlibLibraryReloadFailed && HamlibLibraryReloadFailedMessage is { } hamlibMessage)
            {
                ConnectRadioErrorMessage = hamlibMessage;
                return;
            }

            await _radioSession.ConnectUsingSettingsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // ConnectUsingSettingsAsync's own contract only throws for a genuine factory-resolution
            // failure (e.g. an ambiguous/unregistered backend) -- a real connection/poll problem
            // instead surfaces later via ConnectionEvents (Failed/Reconnecting), same as it always
            // has at app startup, not as an exception here.
            Log.ConnectRadioFailed(_logger, ex);
            Dispatcher.UIThread.Post(() => ConnectRadioErrorMessage = ex.Message);
        }
        finally
        {
            // Code-review finding (step 7): the success path's ConnectUsingSettingsAsync runs
            // ConfigureAwait(false) I/O, so this continuation can land off the UI thread -- every
            // other post-ConfigureAwait(false) property write in this file already goes through
            // Dispatcher.UIThread.Post (see e.g. the ConnectRadioErrorMessage assignment just above)
            // specifically because IsConnectingRadio now also drives NotifyCanExecuteChangedFor on
            // SaveCommand/ApplyCommand, which touches the bound Button.Command directly -- a plain
            // property-changed push (this field's OLD only effect, via CanToggleRadioConnection) has
            // shipped fine off-thread elsewhere in this file, but a direct CanExecute re-check does not.
            Dispatcher.UIThread.Post(() => IsConnectingRadio = false);
        }
    }

    public bool IsRigctldSelected => RadioBackendId == "rigctld";

    /// <summary>Plain computed bool pair backing the Radio/CAT tab's two <c>RadioButton</c>s --
    /// Avalonia's own <c>StringConverters</c> has no "equals this parameter" converter, so this is
    /// simpler than writing a one-off converter class, and matches this ViewModel's existing
    /// <see cref="IsRigctldSelected"/> computed-property idiom.</summary>
    public bool IsNoneBackendSelected
    {
        get => RadioBackendId == "none";
        set
        {
            if (value)
            {
                RadioBackendId = "none";
            }
        }
    }

    public bool IsRigctldBackendSelected
    {
        get => RadioBackendId == "rigctld";
        set
        {
            if (value)
            {
                RadioBackendId = "rigctld";
            }
        }
    }

    public bool IsHamlibBackendSelected
    {
        get => RadioBackendId == "hamlib";
        set
        {
            if (value)
            {
                RadioBackendId = "hamlib";
            }
        }
    }

    public bool IsHamlibSelected => RadioBackendId == "hamlib";

    public bool IsFlrigBackendSelected
    {
        get => RadioBackendId == "flrig";
        set
        {
            if (value)
            {
                RadioBackendId = "flrig";
            }
        }
    }

    public bool IsFlrigSelected => RadioBackendId == "flrig";

    public bool IsOmniRigBackendSelected
    {
        get => RadioBackendId == "omnirig";
        set
        {
            if (value)
            {
                RadioBackendId = "omnirig";
            }
        }
    }

    public bool IsOmniRigSelected => RadioBackendId == "omnirig";

    /// <summary>Gates the OmniRig RadioButton's own <c>IsEnabled</c> -- OmniRig is a Windows-only COM
    /// automation server (<c>OmniRigProtocolFactory.Create</c> throws
    /// <see cref="PlatformNotSupportedException"/> off Windows), so a non-Windows user should never be
    /// able to pick it in the first place rather than discovering the failure only at Connect/Test
    /// time. The OS a process runs on never changes mid-session, so this is a plain computed property
    /// with no change notification needed.</summary>
    public static bool IsOmniRigBackendAvailable => OperatingSystem.IsWindows();

    /// <summary>Shown via <c>ToolTip.ShowOnDisabled</c> (same pattern as
    /// <see cref="ConnectRadioTooltip"/>) so a disabled OmniRig option actually explains why, instead
    /// of just sitting there greyed out.</summary>
    public string OmniRigBackendTooltip => _localization.GetString(
        IsOmniRigBackendAvailable ? "Options.Radio.Backend.OmniRig.Help" : "Options.Radio.Backend.OmniRig.Help.NotWindows");

    /// <summary>Backs the Radio/CAT tab's PTT-method 4-way radio group -- same computed-property
    /// idiom as <see cref="IsNoneBackendSelected"/>/etc., over the existing <see cref="HamlibPttType"/>
    /// string field (no new persisted field for the method itself). Mapping verified against the
    /// real Hamlib source (<c>hamlib/src/conf.c</c>'s <c>ptt_type</c> combo list): CAT sends PTT over
    /// the CAT link itself (<c>"RIG"</c>); VOX sends no PTT command at all -- the rig's own hardware
    /// VOX keys on audio presence (<c>"None"</c>).</summary>
    public bool IsPttMethodVoxSelected
    {
        get => HamlibPttType == "None";
        set
        {
            if (value)
            {
                HamlibPttType = "None";
            }
        }
    }

    public bool IsPttMethodCatSelected
    {
        get => HamlibPttType == "RIG";
        set
        {
            if (value)
            {
                HamlibPttType = "RIG";
            }
        }
    }

    public bool IsPttMethodRtsSelected
    {
        get => HamlibPttType == "RTS";
        set
        {
            if (value)
            {
                HamlibPttType = "RTS";
            }
        }
    }

    public bool IsPttMethodDtrSelected
    {
        get => HamlibPttType == "DTR";
        set
        {
            if (value)
            {
                HamlibPttType = "DTR";
            }
        }
    }

    /// <summary>Gates the PTT-port field -- only RTS/DTR key over a dedicated serial line
    /// (<c>ptt_pathname</c>); CAT keys over the CAT link itself, VOX over none at all.</summary>
    public bool IsPttPortEnabled => IsPttMethodRtsSelected || IsPttMethodDtrSelected;

    /// <summary>Gates the "Test PTT" button -- disabled under VOX (Hamlib sends no PTT command for
    /// it at all, so a test would report false success with nothing actually keyed -- verified
    /// against <c>hamlib/src/rig.c</c>'s <c>RIG_PTT_NONE</c> case) and until a rig model is chosen.</summary>
    public bool CanTestPtt => !IsPttMethodVoxSelected && HamlibModel is > 0;

    partial void OnSelectedHamlibRigModelChanged(HamlibRigModelInfo? value)
    {
        if (value is not null)
        {
            HamlibModel = value.ModelId;
        }
    }

    partial void OnRadioBackendIdChanged(string value)
    {
        InvalidateRadioTests();
        OnPropertyChanged(nameof(IsRigctldSelected));
        OnPropertyChanged(nameof(IsHamlibSelected));
        OnPropertyChanged(nameof(IsFlrigSelected));
        OnPropertyChanged(nameof(IsOmniRigSelected));
        OnPropertyChanged(nameof(IsNoneBackendSelected));
        OnPropertyChanged(nameof(IsRigctldBackendSelected));
        OnPropertyChanged(nameof(IsHamlibBackendSelected));
        OnPropertyChanged(nameof(IsFlrigBackendSelected));
        OnPropertyChanged(nameof(IsOmniRigBackendSelected));

        // Plan-review finding: without this, a stale rigctld test result stays visible after
        // switching to the Hamlib panel (they share TestConnectionStatusMessage), and vice versa --
        // both panels are never shown at once, but the message field is.
        TestConnectionStatusMessage = null;
        TestPttErrorMessage = null;

        // Code-review finding: RadioLinkStatusMessage reads RadioBackendId (the "None" guard) but
        // nothing re-raised it on a backend switch -- since ApplyFromSnapshot sets the real backend
        // AFTER the dialog's first binding evaluation (it runs from LoadSafeAsync, a fire-and-forget
        // task started in the constructor), a dialog opened on an already-broken session could show
        // nothing until the next Reconnecting tick, up to 30s away at a saturated backoff cap.
        OnPropertyChanged(nameof(RadioLinkStatusMessage));
    }
}
