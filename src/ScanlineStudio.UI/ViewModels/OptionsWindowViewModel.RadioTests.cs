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
    private void InvalidateRadioTests()
    {
        _radioTestConfigurationGeneration++;
        RigctldTestSucceeded = false;
        HamlibCatTestSucceeded = false;
        HamlibPttTestSucceeded = false;
        FlrigTestSucceeded = false;
        FlrigPttTestSucceeded = false;
        OmniRigTestSucceeded = false;
        TestConnectionStatusMessage = null;
        TestPttErrorMessage = null;
    }

    private bool CanTestRigctldConnection() => !IsTestingConnection && !string.IsNullOrWhiteSpace(RigctldHost) && RigctldPort is > 0;

    [RelayCommand(CanExecute = nameof(CanTestRigctldConnection))]
    private async Task TestRigctldConnectionAsync()
    {
        // Empty/zero guarded by CanTestRigctldConnection above -- RigctldHost/Port are still
        // nullable properties (a TextBox/NumericUpDown can transiently clear to null while the
        // operator is editing), so the compiler-narrowed local copies below are what actually get
        // passed to the spec, not the possibly-changed-since-CanExecute-ran live properties.
        if (RigctldHost is not { } host || RigctldPort is not { } port)
        {
            return;
        }

        Log.TestRigctldConnectionInvoked(_logger, host, port);
        var generation = _radioTestConfigurationGeneration;
        IsTestingConnection = true;
        // Reset for the duration of this fresh attempt -- Connect must not stay authorized on a
        // stale PREVIOUS success while a new verification is genuinely in flight and could fail.
        RigctldTestSucceeded = false;
        TestConnectionStatusMessage = _localization.GetString("Options.Radio.TestConnection.Testing");
        try
        {
            using var cts = new CancellationTokenSource(MaxTestConnectionDuration);
            var result = await _radioSession.TestConnectionAsync(new RigctldConnectionSpec(host, port), cts.Token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                // Tier B audit finding: this posted lambda runs after the outer try/catch has already
                // exited, so its GetString calls were previously unguarded. A locale file with a
                // mismatched format placeholder throws FormatException out of GetString, which used to
                // both escape uncaught onto the dispatcher loop AND leave IsTestingConnection stuck
                // true (Test Connection permanently disabled for the life of the dialog). Caught (not
                // just finally'd) because the exception is real and reachable -- confirmed via a
                // dedicated regression test that failed with an unhandled FormatException from
                // Dispatcher.RunJobs before this catch was added. TestConnectionStatusMessage falls
                // back to null rather than another GetString call, since a broken locale key can't be
                // trusted to safely produce ANY string here.
                try
                {
                    if (generation != _radioTestConfigurationGeneration)
                    {
                        return;
                    }

                    RigctldTestSucceeded = result.Success;
                    TestConnectionStatusMessage = result.Success
                        ? _localization.GetString("Options.Radio.TestConnection.Success", result.RigId ?? string.Empty)
                        : _localization.GetString("Options.Radio.TestConnection.Failed", result.ErrorMessage ?? string.Empty);
                }
                catch (Exception ex)
                {
                    Log.TestRigctldConnectionStatusDisplayFailed(_logger, ex);
                    TestConnectionStatusMessage = null;
                }
                finally
                {
                    IsTestingConnection = false;
                }
            });
        }
        catch (Exception ex)
        {
            // TestConnectionAsync's own contract already catches connection/poll failures into
            // RadioConnectionTestResult.Success=false -- this only guards against something
            // unexpected escaping that contract (e.g. a DI/factory-resolution bug), so the dialog
            // never gets stuck showing "Testing..." forever.
            Log.TestRigctldConnectionFailed(_logger, host, port, ex);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (generation != _radioTestConfigurationGeneration)
                    {
                        return;
                    }

                    TestConnectionStatusMessage = _localization.GetString("Options.Radio.TestConnection.Failed", ex.Message);
                }
                catch (Exception formatEx)
                {
                    Log.TestRigctldConnectionStatusDisplayFailed(_logger, formatEx);
                    TestConnectionStatusMessage = null;
                }
                finally
                {
                    IsTestingConnection = false;
                }
            });
        }
    }

    /// <summary>Radio/CAT tab's flrig "Test connection" button -- same throwaway-protocol contract as
    /// <see cref="TestRigctldConnectionAsync"/> above (tests whatever is CURRENTLY TYPED into
    /// <see cref="FlrigHost"/>/<see cref="FlrigPort"/>, possibly not yet saved), reusing the same
    /// <see cref="TestConnectionStatusMessage"/>/<see cref="IsTestingConnection"/> fields -- safe
    /// since only one backend section is ever visible at a time. Never keys PTT -- reachability only,
    /// see <see cref="TestFlrigPttAsync"/> for the actual PTT-key test.</summary>
    private bool CanTestFlrigConnection() => !IsTestingConnection && !string.IsNullOrWhiteSpace(FlrigHost) && FlrigPort is > 0;

    [RelayCommand(CanExecute = nameof(CanTestFlrigConnection))]
    private async Task TestFlrigConnectionAsync()
    {
        if (FlrigHost is not { } host || FlrigPort is not { } port)
        {
            return;
        }

        if (_radioSession.RigId != "none")
        {
            TestConnectionStatusMessage = _localization.GetString("Options.Radio.Flrig.AlreadyConnected");
            return;
        }

        Log.TestFlrigConnectionInvoked(_logger, host, port);
        var generation = _radioTestConfigurationGeneration;
        IsTestingConnection = true;
        FlrigTestSucceeded = false;
        TestConnectionStatusMessage = _localization.GetString("Options.Radio.TestConnection.Testing");
        try
        {
            using var cts = new CancellationTokenSource(MaxTestConnectionDuration);
            var result = await _radioSession.TestConnectionAsync(new FlrigConnectionSpec(host, port), cts.Token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (generation != _radioTestConfigurationGeneration)
                    {
                        return;
                    }

                    FlrigTestSucceeded = result.Success;
                    TestConnectionStatusMessage = result.Success
                        ? _localization.GetString("Options.Radio.TestConnection.Success", result.RigId ?? string.Empty)
                        : _localization.GetString("Options.Radio.TestConnection.Failed", result.ErrorMessage ?? string.Empty);
                }
                catch (Exception ex)
                {
                    Log.TestFlrigConnectionStatusDisplayFailed(_logger, ex);
                    TestConnectionStatusMessage = null;
                }
                finally
                {
                    IsTestingConnection = false;
                }
            });
        }
        catch (Exception ex)
        {
            Log.TestFlrigConnectionFailed(_logger, host, port, ex);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (generation != _radioTestConfigurationGeneration)
                    {
                        return;
                    }

                    TestConnectionStatusMessage = _localization.GetString("Options.Radio.TestConnection.Failed", ex.Message);
                }
                catch (Exception formatEx)
                {
                    Log.TestFlrigConnectionStatusDisplayFailed(_logger, formatEx);
                    TestConnectionStatusMessage = null;
                }
                finally
                {
                    IsTestingConnection = false;
                }
            });
        }
    }

    /// <summary>Radio/CAT tab's OmniRig "Test connection" button -- same throwaway-protocol
    /// contract as <see cref="TestFlrigConnectionAsync"/> above, but no host/port to validate first
    /// (<see cref="OmniRigConnectionSpec"/> is parameterless -- OmniRig's own config dialog owns rig
    /// selection). No separate PTT test either, see <see cref="OmniRigTestSucceeded"/>'s own doc
    /// comment.</summary>
    private bool CanTestOmniRigConnection() => !IsTestingConnection;

    [RelayCommand(CanExecute = nameof(CanTestOmniRigConnection))]
    private async Task TestOmniRigConnectionAsync()
    {
        if (_radioSession.RigId != "none")
        {
            TestConnectionStatusMessage = _localization.GetString("Options.Radio.OmniRig.AlreadyConnected");
            return;
        }

        Log.TestOmniRigConnectionInvoked(_logger);
        var generation = _radioTestConfigurationGeneration;
        IsTestingConnection = true;
        OmniRigTestSucceeded = false;
        TestConnectionStatusMessage = _localization.GetString("Options.Radio.TestConnection.Testing");
        try
        {
            using var cts = new CancellationTokenSource(MaxTestConnectionDuration);
            var result = await _radioSession.TestConnectionAsync(new OmniRigConnectionSpec(), cts.Token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (generation != _radioTestConfigurationGeneration)
                    {
                        return;
                    }

                    OmniRigTestSucceeded = result.Success;
                    TestConnectionStatusMessage = result.Success
                        ? _localization.GetString("Options.Radio.TestConnection.Success", result.RigId ?? string.Empty)
                        : _localization.GetString("Options.Radio.TestConnection.Failed", result.ErrorMessage ?? string.Empty);
                }
                catch (Exception ex)
                {
                    Log.TestOmniRigConnectionStatusDisplayFailed(_logger, ex);
                    TestConnectionStatusMessage = null;
                }
                finally
                {
                    IsTestingConnection = false;
                }
            });
        }
        catch (Exception ex)
        {
            Log.TestOmniRigConnectionFailed(_logger, ex);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (generation != _radioTestConfigurationGeneration)
                    {
                        return;
                    }

                    TestConnectionStatusMessage = _localization.GetString("Options.Radio.TestConnection.Failed", ex.Message);
                }
                catch (Exception formatEx)
                {
                    Log.TestOmniRigConnectionStatusDisplayFailed(_logger, formatEx);
                    TestConnectionStatusMessage = null;
                }
                finally
                {
                    IsTestingConnection = false;
                }
            });
        }
    }

    partial void OnIsTestingConnectionChanged(bool value)
    {
        TestRigctldConnectionCommand.NotifyCanExecuteChanged();
        TestHamlibConnectionCommand.NotifyCanExecuteChanged();
        TestFlrigConnectionCommand.NotifyCanExecuteChanged();
        TestOmniRigConnectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnRigctldHostChanged(string? value)
    {
        InvalidateRadioTests();
        TestRigctldConnectionCommand.NotifyCanExecuteChanged();
        RigctldTestSucceeded = false;
    }

    partial void OnRigctldPortChanged(int? value)
    {
        InvalidateRadioTests();
        TestRigctldConnectionCommand.NotifyCanExecuteChanged();
        RigctldTestSucceeded = false;
    }

    partial void OnFlrigHostChanged(string? value)
    {
        InvalidateRadioTests();
        TestFlrigConnectionCommand.NotifyCanExecuteChanged();
        FlrigTestSucceeded = false;
        FlrigPttTestSucceeded = false;
    }

    partial void OnFlrigPortChanged(int? value)
    {
        InvalidateRadioTests();
        TestFlrigConnectionCommand.NotifyCanExecuteChanged();
        FlrigTestSucceeded = false;
        FlrigPttTestSucceeded = false;
    }

    partial void OnHamlibLibraryPathChanged(string? value) => InvalidateRadioTests();

    partial void OnHamlibModelChanged(uint? value)
    {
        InvalidateRadioTests();
        TestHamlibConnectionCommand.NotifyCanExecuteChanged();
        HamlibCatTestSucceeded = false;
        HamlibPttTestSucceeded = false;
    }

    // User-reported gap: Connect's own success gate (CanConnectRadio) must go stale the instant any
    // field the Test CAT/Test PTT spec is built from changes -- otherwise a success from BEFORE the
    // edit could wrongly authorize connecting under fields that were never actually tested.
    partial void OnHamlibSerialPortChanged(string? value)
    {
        InvalidateRadioTests();
        HamlibCatTestSucceeded = false;
        HamlibPttTestSucceeded = false;
    }

    partial void OnHamlibBaudRateChanged(int? value)
    {
        InvalidateRadioTests();
        HamlibCatTestSucceeded = false;
        HamlibPttTestSucceeded = false;
    }

    partial void OnHamlibPttTypeChanged(string? value)
    {
        InvalidateRadioTests();
        HamlibCatTestSucceeded = false;
        HamlibPttTestSucceeded = false;
    }

    partial void OnHamlibPttPortChanged(string? value)
    {
        InvalidateRadioTests();
        HamlibCatTestSucceeded = false;
        HamlibPttTestSucceeded = false;
    }

    /// <summary>Radio/CAT tab's "Test CAT" button -- same throwaway-protocol contract as
    /// <see cref="TestRigctldConnectionAsync"/> above (tests whatever is CURRENTLY TYPED into the
    /// Hamlib fields, possibly not yet saved), reusing the same <see cref="TestConnectionStatusMessage"/>/
    /// <see cref="IsTestingConnection"/> fields -- safe since only one backend section is ever visible
    /// at a time. Refuses to attempt a second, contending connection while the app's real session is
    /// already connected (<see cref="IRadioSessionService.RigId"/> != <c>"none"</c>).</summary>
    private bool CanTestHamlibConnection() => !IsTestingConnection && HamlibModel is > 0;

    [RelayCommand(CanExecute = nameof(CanTestHamlibConnection))]
    private async Task TestHamlibConnectionAsync()
    {
        if (HamlibModel is not { } model)
        {
            return;
        }

        if (_radioSession.RigId != "none")
        {
            TestConnectionStatusMessage = _localization.GetString("Options.Radio.Hamlib.AlreadyConnected");
            return;
        }

        Log.TestHamlibConnectionInvoked(_logger, model);
        var generation = _radioTestConfigurationGeneration;
        IsTestingConnection = true;
        // Reset for the duration of this fresh attempt -- same reasoning as
        // TestRigctldConnectionAsync's own RigctldTestSucceeded reset above.
        HamlibCatTestSucceeded = false;
        TestConnectionStatusMessage = _localization.GetString("Options.Radio.TestConnection.Testing");
        var spec = new HamlibConnectionSpec(model)
        {
            SerialPort = HamlibSerialPort,
            BaudRate = HamlibBaudRate,
            PttType = HamlibPttType,
            PttPort = HamlibPttPort,
        };
        try
        {
            using var cts = new CancellationTokenSource(MaxTestConnectionDuration);
            var result = await _radioSession.TestConnectionAsync(spec, cts.Token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (generation != _radioTestConfigurationGeneration)
                    {
                        return;
                    }

                    HamlibCatTestSucceeded = result.Success;
                    TestConnectionStatusMessage = result.Success
                        ? _localization.GetString("Options.Radio.TestConnection.Success", result.RigId ?? string.Empty)
                        : _localization.GetString("Options.Radio.TestConnection.Failed", result.ErrorMessage ?? string.Empty);
                }
                catch (Exception ex)
                {
                    Log.TestHamlibConnectionStatusDisplayFailed(_logger, ex);
                    TestConnectionStatusMessage = null;
                }
                finally
                {
                    IsTestingConnection = false;
                }
            });
        }
        catch (Exception ex)
        {
            Log.TestHamlibConnectionFailed(_logger, model, ex);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (generation != _radioTestConfigurationGeneration)
                    {
                        return;
                    }

                    TestConnectionStatusMessage = _localization.GetString("Options.Radio.TestConnection.Failed", ex.Message);
                }
                catch (Exception formatEx)
                {
                    Log.TestHamlibConnectionStatusDisplayFailed(_logger, formatEx);
                    TestConnectionStatusMessage = null;
                }
                finally
                {
                    IsTestingConnection = false;
                }
            });
        }
    }

    /// <summary>Radio/CAT tab's "Test PTT" button -- a real start/stop TOGGLE, same shape as
    /// <see cref="TuneCommand"/> (see that command's own doc comment), but scoped to Hamlib CAT/PTT
    /// (not <see cref="ISstvSessionService"/>) and capped at <see cref="MaxTestPttDuration"/>, not
    /// <see cref="MaxTuneDuration"/> -- this opens its own throwaway connection with no SWR-cutoff
    /// watchdog of its own, see that constant's own doc comment. Disabled in XAML under VOX
    /// (<see cref="CanTestPtt"/>) and refuses to run while the app's real session is already
    /// connected, same reasoning as <see cref="TestHamlibConnectionAsync"/> above.
    ///
    /// Code-review finding: <c>AllowConcurrentExecutions = true</c> is required, not decorative --
    /// CommunityToolkit.Mvvm's generated <c>AsyncRelayCommand</c> defaults to
    /// <see langword="false"/>, which ANDs into <c>CanExecute</c> for the whole duration a command is
    /// already running. Without this, the button greys out the instant the tone starts and the
    /// "Stop test" click (the <see cref="IsTestingPtt"/> branch below) becomes unreachable from the
    /// real UI -- only reachable from a test calling <c>ExecuteAsync</c> directly, which bypasses
    /// <c>CanExecute</c> entirely. <see cref="TuneCommand"/> had this same latent shape; left
    /// untouched here as out of that diff's scope, then fixed 2026-09-01 (see
    /// <see cref="RadioStatusViewModel.TuneCommand"/>'s own doc comment -- a DIFFERENT command from
    /// this dialog's own <see cref="TuneCommand"/> despite the shared name, though both had the
    /// identical bug).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task TestPttAsync()
    {
        if (IsTestingPtt)
        {
            _testPttCts?.Cancel();
            return;
        }

        if (HamlibModel is not { } model || IsPttMethodVoxSelected)
        {
            return;
        }

        if (_radioSession.RigId != "none")
        {
            TestPttErrorMessage = _localization.GetString("Options.Radio.Hamlib.AlreadyConnected");
            return;
        }

        Log.TestPttInvoked(_logger, model, MaxTestPttDuration.TotalSeconds);
        TestPttErrorMessage = null;
        var generation = _radioTestConfigurationGeneration;
        var request = ++_pttTestRequestGeneration;
        IsTestingPtt = true;
        // Reset for the duration of this fresh attempt -- same reasoning as
        // TestRigctldConnectionAsync's own RigctldTestSucceeded reset.
        HamlibPttTestSucceeded = false;
        var cts = new CancellationTokenSource();
        _testPttCts = cts;
        var spec = new HamlibConnectionSpec(model)
        {
            SerialPort = HamlibSerialPort,
            BaudRate = HamlibBaudRate,
            PttType = HamlibPttType,
            PttPort = HamlibPttPort,
        };
        try
        {
            // RadioSessionService.TestPttAsync's own contract: always returns a result, never
            // throws -- an early Stop click (cts.Cancel() above) is folded into Success=true there,
            // same "normal control flow" philosophy as TuneAsync's own OperationCanceledException
            // handling. A non-null ErrorMessage here means a REAL failure -- including the
            // un-key-failed-after-every-retry case, which must reach the operator, not be silently
            // dropped.
            var result = await _radioSession.TestPttAsync(spec, MaxTestPttDuration, cts.Token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                if (generation == _radioTestConfigurationGeneration && request == _pttTestRequestGeneration)
                {
                    HamlibPttTestSucceeded = result.Success;
                }
            });
            if (!result.Success)
            {
                // T1-7 (production_audit.md): used to assign result.ErrorMessage directly -- raw
                // English, unlike the sibling TestConnectionAsync path, which already wraps its own
                // result through this same localized-template convention. Nested try/catch, same
                // reasoning as TestRigctldConnectionAsync's own posted lambda above -- this runs
                // after the outer try/catch has already exited, so a broken locale key's GetString
                // FormatException would otherwise escape uncaught onto the dispatcher loop.
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (generation != _radioTestConfigurationGeneration || request != _pttTestRequestGeneration)
                        {
                            return;
                        }

                        TestPttErrorMessage = _localization.GetString("Options.Radio.TestPtt.Failed", result.ErrorMessage ?? string.Empty);
                    }
                    catch (Exception ex)
                    {
                        Log.TestHamlibPttStatusDisplayFailed(_logger, ex);
                        TestPttErrorMessage = null;
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Log.TestPttFailed(_logger, model, ex);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (generation != _radioTestConfigurationGeneration || request != _pttTestRequestGeneration)
                    {
                        return;
                    }

                    TestPttErrorMessage = _localization.GetString("Options.Radio.TestPtt.Failed", ex.Message);
                }
                catch (Exception displayEx)
                {
                    Log.TestHamlibPttStatusDisplayFailed(_logger, displayEx);
                    TestPttErrorMessage = null;
                }
            });
        }
        finally
        {
            // Code-review finding: IsTestingPtt goes false LAST, not first -- AllowConcurrentExecutions
            // means a click can re-enter this method the instant IsTestingPtt observably flips, and
            // the only thing gating that re-entry is this exact flag. Clearing it before _testPttCts
            // is nulled/disposed left a narrow window where a click's `_testPttCts?.Cancel()` could
            // race this finally's own cts.Dispose(), throwing ObjectDisposedException. With this
            // order, any click that observes IsTestingPtt == true finds _testPttCts still live.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _testPttCts = null;
                cts.Dispose();
                IsTestingPtt = false;
            });
        }
    }

    /// <summary>flrig's own PTT-key test -- same start/stop toggle shape as <see cref="TestPttAsync"/>
    /// above (shares <see cref="IsTestingPtt"/>/<see cref="TestPttErrorMessage"/>/<see cref="_testPttCts"/>,
    /// same "only one backend section visible at a time" safety as the shared test-status fields
    /// elsewhere in this class), capped at the same <see cref="MaxTestPttDuration"/>. No
    /// <see cref="CanTestPtt"/>-style VOX exemption check -- flrig has exactly one PTT mechanism.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task TestFlrigPttAsync()
    {
        if (IsTestingPtt)
        {
            _testPttCts?.Cancel();
            return;
        }

        if (FlrigHost is not { } host || FlrigPort is not { } port)
        {
            return;
        }

        if (_radioSession.RigId != "none")
        {
            TestPttErrorMessage = _localization.GetString("Options.Radio.Flrig.AlreadyConnected");
            return;
        }

        Log.TestFlrigPttInvoked(_logger, host, port, MaxTestPttDuration.TotalSeconds);
        TestPttErrorMessage = null;
        var generation = _radioTestConfigurationGeneration;
        var request = ++_pttTestRequestGeneration;
        IsTestingPtt = true;
        FlrigPttTestSucceeded = false;
        var cts = new CancellationTokenSource();
        _testPttCts = cts;
        var spec = new FlrigConnectionSpec(host, port);
        try
        {
            // RadioSessionService.TestPttAsync's own contract: always returns a result, never
            // throws -- same reasoning as TestPttAsync's own call above.
            var result = await _radioSession.TestPttAsync(spec, MaxTestPttDuration, cts.Token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                if (generation == _radioTestConfigurationGeneration && request == _pttTestRequestGeneration)
                {
                    FlrigPttTestSucceeded = result.Success;
                }
            });
            if (!result.Success)
            {
                // T1-7 (production_audit.md): same fix and reasoning as TestPttAsync's own equivalent
                // site above.
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (generation != _radioTestConfigurationGeneration || request != _pttTestRequestGeneration)
                        {
                            return;
                        }

                        TestPttErrorMessage = _localization.GetString("Options.Radio.TestPtt.Failed", result.ErrorMessage ?? string.Empty);
                    }
                    catch (Exception ex)
                    {
                        Log.TestFlrigPttStatusDisplayFailed(_logger, ex);
                        TestPttErrorMessage = null;
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Log.TestFlrigPttFailed(_logger, host, port, ex);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (generation != _radioTestConfigurationGeneration || request != _pttTestRequestGeneration)
                    {
                        return;
                    }

                    TestPttErrorMessage = _localization.GetString("Options.Radio.TestPtt.Failed", ex.Message);
                }
                catch (Exception displayEx)
                {
                    Log.TestFlrigPttStatusDisplayFailed(_logger, displayEx);
                    TestPttErrorMessage = null;
                }
            });
        }
        finally
        {
            // Same ordering reasoning as TestPttAsync's own finally block above.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _testPttCts = null;
                cts.Dispose();
                IsTestingPtt = false;
            });
        }
    }

    /// <summary>Called from the window's own Closing/Cancel path so a PTT test left running (rig
    /// possibly still keyed) can't outlive the dialog that started it -- see
    /// <see cref="StopTuneIfActive"/>'s own doc comment for the identical reasoning.</summary>
    public void StopTestPttIfActive() => _testPttCts?.Cancel();

    /// <summary>"Browse..." next to the Hamlib library-path field -- lets the user pick the shared
    /// library file directly instead of typing a path by hand. A successful pick both fills
    /// <see cref="HamlibLibraryPath"/> and immediately probes it (<see cref="RunHamlibProbeAsync"/>),
    /// matching the user-facing "find it, then list the rigs" flow in one action.</summary>
    private bool CanBrowseOrAutoDetectHamlib() => !IsProbingHamlib;

    [RelayCommand(CanExecute = nameof(CanBrowseOrAutoDetectHamlib))]
    private async Task BrowseHamlibLibraryAsync()
    {
        var path = await _filePickerService.PickHamlibLibraryFileAsync().ConfigureAwait(false);
        if (path is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => HamlibLibraryPath = path);
        await RunHamlibProbeAsync(path).ConfigureAwait(false);
    }

    /// <summary>"Auto-detect" -- runs spec/03-cat-layer.md's discovery tiers 2/3 (no override path)
    /// instead of tier 1. On success, overwrites <see cref="HamlibLibraryPath"/> with whatever was
    /// actually found, so Save persists a concrete tier-1 path from then on rather than leaving the
    /// field blank (which would silently re-run auto-detection every future launch instead of
    /// pinning down what was just confirmed to work).</summary>
    [RelayCommand(CanExecute = nameof(CanBrowseOrAutoDetectHamlib))]
    private Task AutoDetectHamlibAsync() => RunHamlibProbeAsync(overridePath: null, applyResolvedPathOnSuccess: true);

    /// <summary>"Test" next to the hand-typed path field -- re-probes whatever is CURRENTLY TYPED
    /// into <see cref="HamlibLibraryPath"/> (possibly not yet saved), same "test what's on screen,
    /// not what's persisted" contract as <see cref="TestRigctldConnectionAsync"/> above.</summary>
    [RelayCommand(CanExecute = nameof(CanBrowseOrAutoDetectHamlib))]
    private Task ProbeHamlibAsync() => RunHamlibProbeAsync(HamlibLibraryPath);

    private async Task RunHamlibProbeAsync(string? overridePath, bool applyResolvedPathOnSuccess = false)
    {
        Log.HamlibProbeInvoked(_logger, overridePath ?? "(auto-detect)");

        // User-caught crash: BrowseHamlibLibraryAsync reaches this method AFTER an off-thread
        // await (PickHamlibLibraryFileAsync().ConfigureAwait(false)), so these two entry writes
        // used to run on a threadpool thread, not the UI thread -- IsProbingHamlib's generated
        // OnIsProbingHamlibChanged calls NotifyCanExecuteChanged() on the three Hamlib commands,
        // which touches Button.Command and trips Avalonia's VerifyAccess(), an unhandled
        // InvalidOperationException that terminated the process. AutoDetectHamlibAsync/
        // ProbeHamlibAsync never hit this (they call in directly from command invocation, still on
        // the UI thread), which is exactly why only Browse crashed. Matches the exit path below,
        // which already does this correctly.
        Dispatcher.UIThread.Post(() =>
        {
            IsProbingHamlib = true;
            HamlibDiscoveryStatusMessage = _localization.GetString("Options.Radio.Hamlib.Probing");
        });
        try
        {
            var result = await _hamlibDiscovery.ProbeAsync(overridePath).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    ApplyHamlibProbeResult(result, applyResolvedPathOnSuccess);
                }
                catch (Exception ex)
                {
                    Log.HamlibProbeStatusDisplayFailed(_logger, ex);
                    HamlibDiscoveryStatusMessage = null;
                }
                finally
                {
                    IsProbingHamlib = false;
                }
            });
        }
        catch (Exception ex)
        {
            // ProbeAsync's own contract already catches discovery failures into
            // HamlibProbeResult.IsAvailable=false -- this only guards against something unexpected
            // escaping that contract, same reasoning as TestRigctldConnectionAsync's outer catch.
            Log.HamlibProbeFailed(_logger, ex);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    HamlibDiscoveryStatusMessage = _localization.GetString("Options.Radio.Hamlib.Probe.Failed", ex.Message);
                }
                catch (Exception formatEx)
                {
                    Log.HamlibProbeStatusDisplayFailed(_logger, formatEx);
                    HamlibDiscoveryStatusMessage = null;
                }
                finally
                {
                    IsProbingHamlib = false;
                }
            });
        }
    }

    private void ApplyHamlibProbeResult(HamlibProbeResult result, bool applyResolvedPathOnSuccess)
    {
        HamlibRigModels.Clear();
        SelectedHamlibRigModel = null;

        if (!result.IsAvailable)
        {
            HamlibDiscoveryStatusMessage = _localization.GetString(
                "Options.Radio.Hamlib.Probe.NotFound", string.Join("; ", result.Attempts));
            return;
        }

        if (applyResolvedPathOnSuccess && result.ResolvedPath is { } resolvedPath)
        {
            HamlibLibraryPath = resolvedPath;
        }

        foreach (var model in result.RigModels)
        {
            HamlibRigModels.Add(model);
        }

        HamlibDiscoveryStatusMessage = result.RigModels.Count > 0
            ? _localization.GetString("Options.Radio.Hamlib.Probe.Found", result.Version ?? string.Empty, result.ResolvedPath ?? string.Empty, result.RigModels.Count.ToString(CultureInfo.InvariantCulture))
            : _localization.GetString("Options.Radio.Hamlib.Probe.FoundNoModels", result.Version ?? string.Empty, result.ResolvedPath ?? string.Empty);
    }

    /// <summary>Restart-required-settings backlog item 5 (2026-08-28): treats any blank value
    /// (<see langword="null"/>, empty, or whitespace-only) as equal to any other blank value before
    /// comparing -- a plain <c>!=</c> would count clearing an already-blank field (a TextBox binding
    /// yields <c>""</c>, not <see langword="null"/>) as a real change, triggering a pointless native
    /// reload + permanent leak for a no-op. Matches <c>HamlibLibraryLocator</c>'s own
    /// <c>!string.IsNullOrWhiteSpace</c> blank test exactly (`:45`) -- NOT a stricter rule invented
    /// here, so a genuine "clear the field to fall back to auto-detection" case (non-blank -> blank)
    /// still correctly counts as a change and still reloads.
    ///
    /// Configurations-preset backlog, Phase 3 (2026-08-28) update: the preset-switch orchestrator
    /// needs this SAME rule and now has its own copy,
    /// <see cref="ScanlineStudio.Core.Radio.RadioConnectionSettingsExtensions.HamlibPathsAreEquivalent"/>
    /// -- NOT forwarded to from here. Round-2 code-review correction: <c>ScanlineStudio.UI</c> DOES
    /// see <c>ScanlineStudio.Core.Radio</c>'s types, transitively, via its own reference to
    /// <c>ScanlineStudio.Application</c> (which references Core.Radio directly, with no
    /// <c>PrivateAssets</c>) -- it is not that the type is literally unreachable. The real reason for
    /// two copies is architectural discipline, not a compile error: this dialog is meant to reach
    /// Radio settings ONLY through <c>IRadioSessionService</c>'s own Application-layer surface, never
    /// a Core project's types directly, and writing `RadioConnectionSettingsExtensions.HamlibPathsAreEquivalent(...)`
    /// here would be exactly that kind of direct reach-through, however harmless this one specific
    /// helper looks. Two copies, deliberately, kept identical -- see that method's own doc
    /// comment too.</summary>
    private static bool HamlibPathsAreEquivalent(string? a, string? b) =>
        string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b) || a == b;

    partial void OnIsProbingHamlibChanged(bool value)
    {
        BrowseHamlibLibraryCommand.NotifyCanExecuteChanged();
        AutoDetectHamlibCommand.NotifyCanExecuteChanged();
        ProbeHamlibCommand.NotifyCanExecuteChanged();
    }

    public string TestPttButtonLabel => _localization.GetString(IsTestingPtt ? "Options.Radio.Hamlib.TestPtt.Stop" : "Options.Radio.Hamlib.TestPtt");

    public string TestFlrigPttButtonLabel => _localization.GetString(IsTestingPtt ? "Options.Radio.Flrig.TestPtt.Stop" : "Options.Radio.Flrig.TestPtt");
}
