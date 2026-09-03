using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Settings;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class MainWindow : Window
{
    private readonly ISettingsStore? _settingsStore;

    // Give-up-after-5 feature: dedup guard for the modal popup below -- ConnectionGaveUp is a
    // system-triggered event that can in principle fire again (e.g. the user retries via Options and
    // that retry also exhausts its 5 attempts) before the user has dismissed an already-open popup
    // from the previous give-up. Null once the window is closed (or before the first give-up).
    private RadioConnectionGaveUpWindowView? _connectionGaveUpWindow;

    // Configurations-preset backlog, Phase 4b: re-entrancy guard for ConfigurationsMenuItem's own
    // SubmenuOpened handler -- see that handler's own doc comment for why this is needed (the event
    // bubbles from each row's own nested submenu). Class-level, not local to the DataContextChanged
    // lambda below, since DataContextChanged can in principle fire more than once over this window's
    // lifetime and each firing re-subscribes a fresh closure -- a field survives that; a local
    // wouldn't reliably.
    private bool _isPopulatingConfigurationsMenu;

    // T1-12 (production_audit.md): re-entry guard for DataContextChanged below -- that lambda does
    // ~14 `vm.XXX += ...` subscriptions with no guard, so a second firing would double-subscribe
    // every one of them (2 Options windows, 2 viewer windows, etc.). Latent today (DataContext is
    // only ever assigned once), same "field survives a second firing, a local wouldn't" reasoning
    // as _isPopulatingConfigurationsMenu above -- just never back-applied to this handler itself.
    private bool _wired;

    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
        // Views aren't DI-constructed (Avalonia builds them via `new`, not the container) -- resolved
        // from App.Services directly, same pattern this project already uses for other code-behind
        // needs (e.g. FilePickerService). Null-tolerant: a headless/design-time construction with no
        // App.Services set must not throw here.
        var logger = App.Services?.GetService<ILogger<MainWindow>>();
        _settingsStore = App.Services?.GetService<ISettingsStore>();

        // Restore window geometry BEFORE the window is ever shown (App.axaml.cs constructs this
        // window, then hands it to the desktop lifetime -- no visible "jump" this way).
        //
        // Deadlock fix (found via real-window testing, not assumed safe): a bare
        // `_settingsStore.LoadAsync().GetAwaiter().GetResult()` here hangs the app on startup.
        // ScanlineStudio.Host.Program's ISstvDecoder factory does the same synchronous-block shape
        // and is safe -- but it runs during generic-host/DI-container construction, BEFORE
        // BuildAvaloniaApp().Start*() ever installs Avalonia's UI-thread SynchronizationContext.
        // This constructor runs AFTER that (App.axaml.cs's OnFrameworkInitializationCompleted calls
        // `new MainWindow()` on the UI thread). `_settingsStore` is typed as the abstract
        // `ISettingsStore`, not the concrete `JsonSettingsStore` -- even though that concrete
        // implementation now uses `ConfigureAwait(false)` throughout (restart-required-settings
        // backlog item 3, 2026-08-27), a bare `.LoadAsync().GetAwaiter().GetResult()` here would
        // still be one implementation swap away from resuming a continuation back on the captured
        // UI-thread context, which is exactly the thread blocked waiting on GetResult(). Wrapping in
        // Task.Run moves the whole awaited chain onto a thread-pool thread with no captured context,
        // so the continuation never needs the blocked UI thread back regardless of which
        // ISettingsStore is actually registered. Port of legacy's own
        // Main.cpp:1686-1692 load gate (sys.m_MemWindow) -- see WindowGeometrySettings' own doc
        // comment for the full citation.
        if (_settingsStore is not null)
        {
            var geometry = Task.Run(() => _settingsStore.LoadAsync()).GetAwaiter().GetResult()
                .GetSection(WindowGeometrySettings.SectionKey, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings);
            if (geometry is { RememberWindowPosition: true, Left: { } left, Top: { } top, Width: { } width, Height: { } height })
            {
                // Tier C audit finding (risk): applied with zero bounds validation before this fix --
                // a position persisted while on a since-removed monitor (unplugged second display, a
                // resolution change) restored to coordinates with no display behind them. Windows
                // does not clamp this, so the app appeared not to start, and the only recovery was
                // hand-deleting settings.json (the Options toggle to turn this off lives inside the
                // invisible window). `Screens.All` may not be reliably populated this early in every
                // Avalonia configuration -- if the list comes back empty, fall back to trusting the
                // persisted value rather than disabling the whole feature; only reject when a screen
                // list IS available and genuinely none of them contain this position.
                var restoredPosition = new PixelPoint((int)left, (int)top);
                var screenBounds = Screens.All.Select(s => s.Bounds).ToList();
                if (WindowGeometryPolicy.ShouldRestorePosition(restoredPosition, screenBounds))
                {
                    Position = restoredPosition;
                    Width = width;
                    Height = height;
                }
                else if (logger is not null)
                {
                    Log.RestoredWindowPositionOffScreen(logger, restoredPosition.X, restoredPosition.Y);
                }
            }
        }

        // Port of legacy's own Main.cpp:2214-2224 save gate (sys.m_MemWindow AND WindowState ==
        // wsNormal -- geometry is never captured while maximized/minimized). Re-reads
        // RememberWindowPosition fresh rather than caching the constructor's value, since the user
        // may have toggled it via Options mid-session. Same Task.Run deadlock fix as above --
        // real-window-testing-caught bug, round 2: Avalonia's Position/Width/Height (and every other
        // Layoutable property) can only be read on the UI thread, so they must be captured BEFORE
        // entering Task.Run, not read from inside its pool-thread delegate (that threw
        // "Call from invalid thread" and crashed the app on close, caught only by actually closing a
        // real running window, not by the build or test suite).
        Closing += (_, _) =>
        {
            if (_settingsStore is null || WindowState != WindowState.Normal)
            {
                return;
            }

            var left = Position.X;
            var top = Position.Y;
            var width = Width;
            var height = Height;

            // Tier C audit finding (risk): unguarded before this fix -- JsonSettingsStore.SaveAsync's
            // own Directory.CreateDirectory/File.Create/File.Move calls have no exception handling of
            // their own (unlike its sibling LoadAsync), so a disk-full/read-only-profile/settings.json-
            // locked-by-a-sync-client failure rethrew on the UI thread inside Closing, escaping past
            // Program.cs's own try/catch around lifetime.Start -- which means lifetime.Exit never
            // fires, and the 10s-bounded host DisposeAsync (audio capture device / radio connection
            // teardown) is skipped entirely, not just this save. Best-effort like every other
            // settings-write path in this app: log and let the window close anyway.
            try
            {
                Task.Run(() => _settingsStore.UpdateAsync(settings =>
                {
                    var current = settings.GetSection(WindowGeometrySettings.SectionKey, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings) ?? new WindowGeometrySettings();
                    if (current.RememberWindowPosition != true)
                    {
                        // No-op: UpdateAsync skips the save/notify/log entirely when mutate returns
                        // its own input unchanged -- preserves this guard's original "don't touch
                        // disk at all" behavior, not just "don't touch this field."
                        return settings;
                    }

                    var updated = current with { Left = left, Top = top, Width = width, Height = height };
                    return settings.WithSection(WindowGeometrySettings.SectionKey, updated, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings);
                })).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                if (logger is not null)
                {
                    Log.WindowGeometrySaveFailed(logger, ex);
                }
            }
        };

        DataContextChanged += (_, _) =>
        {
            if (_wired)
            {
                return;
            }

            if (DataContext is MainViewModel vm)
            {
                _wired = true;

                // Give-up-after-5 feature: the must-acknowledge popup (direct feedback: "should be a
                // popup window, not a tiny text under the VFO" -- replaces this feature's earlier
                // dismissible-toast, and later a persistent header text line that was tried alongside
                // this popup then removed as redundant, per user decision).
                //
                // Dedup-guarded by _connectionGaveUpWindow: ConnectionGaveUp is system-triggered and
                // can in principle fire again (e.g. a user-initiated retry via Options also exhausts
                // its own 5 attempts) before an already-open popup from a previous give-up has been
                // dismissed -- skip opening a second one rather than stacking dialogs.
                //
                // Code-review finding (carried over from the toast this replaces): wrapped in
                // try/catch, same reasoning as OptionsRequested/AboutRequested/QsoLinkRequested's own
                // handlers below -- an unhandled exception here would otherwise crash the process,
                // AND (this handler's own extra hazard, RadioStatusViewModel.OnConnectionEvent's own
                // doc comment) abort the CALLER's Dispatcher.Post callback mid-way, since that raises
                // this event synchronously.
                vm.RadioStatus.ConnectionGaveUp += message =>
                {
                    if (_connectionGaveUpWindow is not null)
                    {
                        return;
                    }

                    if (logger is not null)
                    {
                        Log.ConstructingConnectionGaveUpWindow(logger);
                    }

                    try
                    {
                        var giveUpViewModel = new RadioConnectionGaveUpWindowViewModel(message);
                        // User request: the popup's own "Config" button has no DI access of its
                        // own (see that view-model's own doc comment) -- this handler already owns
                        // the popup's lifecycle, so it's also where the jump-to-Options-Radio-tab
                        // request gets fulfilled, same view-model-never-touches-a-Window reasoning
                        // as every other cross-window request in this file.
                        giveUpViewModel.ConfigRequested += () => vm.OpenOptionsToRadioTabCommand.Execute(null);
                        var window = new RadioConnectionGaveUpWindowView
                        {
                            DataContext = giveUpViewModel,
                        };
                        _connectionGaveUpWindow = window;
                        window.Closed += (_, _) => _connectionGaveUpWindow = null;
                        window.ShowDialog(this);
                        if (logger is not null)
                        {
                            Log.ConnectionGaveUpShowDialogReturned(logger);
                        }
                    }
                    catch (Exception ex)
                    {
                        _connectionGaveUpWindow = null;
                        if (logger is not null)
                        {
                            Log.ConnectionGaveUpWindowFailed(logger, ex);
                        }
                    }
                };

                vm.OptionsRequested += optionsViewModel =>
                {
                    if (logger is not null)
                    {
                        Log.ConstructingOptionsWindow(logger);
                    }

                    // Tier C audit finding (risk): unguarded before this fix -- the comment this
                    // replaced claimed "no exception surface to guard," but InitializeComponent()
                    // (run inside the OptionsWindowView constructor) throws on a bad binding/missing
                    // resource, and ShowDialog can throw synchronously too; either one escaped through
                    // RelayCommand.Execute into input dispatch and crashed the process with only the
                    // generic AppDomain net for a trace. Same shape as QsoLinkRequested's own handler
                    // below.
                    try
                    {
                        var window = new OptionsWindowView { DataContext = optionsViewModel };
                        window.Opened += (_, _) =>
                        {
                            if (logger is not null)
                            {
                                Log.OptionsWindowOpened(logger, window.Position.ToString(), Screens.ScreenFromWindow(window)?.Bounds.ToString() ?? "(none)");
                            }
                        };
                        // User-reported bug (2026-08-23): the header-row callsign chip only ever
                        // loaded its callsign once, from MainViewModel's own constructor -- typing a
                        // new callsign in Options and hitting Save persisted it correctly, but the
                        // chip kept showing the old value (or "N0CALL") until the next full app
                        // restart. Re-running LoadOperatorSettingsAsync unconditionally on close (Save AND
                        // Cancel) is safe -- Cancel never touched disk, so this is a no-op reload of
                        // the same value in that case, same as re-running it costs nothing extra.
                        // Same bug, same fix, for the Transmit tab's Output-device and Identification
                        // fields (TxControlsPaneViewModel.LoadOutputDeviceNameAsync/LoadIdentificationSummaryAsync).
                        window.Closed += (_, _) =>
                        {
                            _ = vm.LoadOperatorSettingsAsync();
                            _ = vm.TxControls.LoadOutputDeviceNameAsync();
                            _ = vm.TxControls.LoadIdentificationSummaryAsync();
                            // Storage section moved in from the former standalone "Configurations >
                            // Storage" dialog (2026-08-27) -- carries over that dialog's own
                            // refresh-the-Gallery-Storage-card-on-close behavior, now for General
                            // tab's Images row instead.
                            _ = vm.RxHistory.LoadImagesDirectoryAsync();
                            // ui_transition_plan.md step 12 (Auto-save RX audio), Step 4: same
                            // refresh-on-close reasoning as the Images row just above, now for the
                            // new Audio row.
                            _ = vm.RxHistory.LoadAudioStorageInfoAsync();
                            // Receive tab's "Lookup QRZ" button gate -- re-checks whether QRZ
                            // lookup is configured, so enabling/disabling it in Options takes
                            // effect immediately without an app restart, same reasoning as every
                            // other refresh-on-close call in this block.
                            _ = vm.RxImage.LoadQrzLookupConfiguredAsync();
                            // Receive tab's own live "Squelch level" dropdown -- Options' own
                            // Squelch level control now ALSO applies live (OptionsWindowViewModel's
                            // save flow), so re-sync this pane's dropdown here too, same reasoning
                            // as every other refresh-on-close call in this block. A no-op if
                            // unchanged (RxImagePaneViewModel.RefreshSenseLevelFromSession's own doc
                            // comment).
                            vm.RxImage.RefreshSenseLevelFromSession();
                            // Receive tab's own "Auto-correct" status text -- Options' AutoSlantEnabled
                            // is now ALSO live (2026-08-27, restart-required-settings backlog item 1),
                            // same reasoning as RefreshSenseLevelFromSession immediately above.
                            vm.RxImage.RefreshAutoSlantEnabledFromSession();
                        };
                        // Options > General's Config/Database "Restart Now" -- closes THIS dialog
                        // first, then (from that window's own Closed event, not inline right after
                        // Close() -- Avalonia's Close() raising Closed synchronously isn't something
                        // this codebase can verify against its own vendored sources) closes
                        // MainWindow itself, reaching the exact same shutdown path
                        // MainViewModel.ExitRequested's own handler below uses for File > Exit. No
                        // process is spawned from here -- OptionsWindowViewModel.RestartNowAsync
                        // already set IApplicationRestarter.RestartRequested; the actual spawn only
                        // happens from Program.cs, after this process's own teardown completes.
                        optionsViewModel.RestartRequested += () =>
                        {
                            if (logger is not null)
                            {
                                Log.RestartRequested(logger);
                            }

                            window.Closed += (_, _) => Close();
                            window.Close();
                        };
                        window.ShowDialog(this);
                        if (logger is not null)
                        {
                            Log.ShowDialogReturned(logger);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (logger is not null)
                        {
                            Log.OptionsWindowFailed(logger, ex);
                        }
                    }
                };

                // Same synchronous, unawaited shape as OptionsRequested above (spec/18-path-to-1.0.md
                // High item 5) -- a static-content dialog with no async work.
                vm.AboutRequested += aboutViewModel =>
                {
                    if (logger is not null)
                    {
                        Log.ConstructingAboutWindow(logger);
                    }

                    // Tier C audit finding (risk): same reasoning as OptionsRequested's own fix above.
                    try
                    {
                        var window = new AboutWindowView { DataContext = aboutViewModel };
                        window.ShowDialog(this);
                        if (logger is not null)
                        {
                            Log.AboutShowDialogReturned(logger);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (logger is not null)
                        {
                            Log.AboutWindowFailed(logger, ex);
                        }
                    }
                };

                // Code-review fix: async void event handler (the delegate type is Action<...>, not
                // Func<...,Task>) -- an unhandled exception from ShowDialog would otherwise crash the
                // process instead of logging. Wrapped explicitly rather than left to propagate, unlike
                // OptionsRequested's own synchronous (and unawaited) handler above, which has no
                // await to fail past construction.
                vm.RxHistory.QsoLinkRequested += async qsoLinkViewModel =>
                {
                    if (logger is not null)
                    {
                        Log.ConstructingQsoLinkWindow(logger);
                    }

                    try
                    {
                        var window = new QsoLinkWindowView { DataContext = qsoLinkViewModel };
                        await window.ShowDialog(this);
                        if (logger is not null)
                        {
                            Log.QsoLinkShowDialogReturned(logger);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (logger is not null)
                        {
                            Log.QsoLinkWindowFailed(logger, ex);
                        }
                    }
                };

                // Deliberately synchronous, unlike QsoLinkRequested's handler above -- no dialog,
                // no ShowDialog to await. Safe to read vm.RxImage's properties directly here: every
                // writer of OverrideCallsign/DetectedMode/StartedAt already routes through
                // Dispatcher.UIThread.Post, and this handler itself only ever runs on the UI
                // thread (LogQsoCommand is a button click), so there is no interleaving window
                // between "user clicked Log QSO" and this read.
                vm.RxImage.LogQsoRequested += () =>
                {
                    if (logger is not null)
                    {
                        Log.LogQsoRequested(logger);
                    }

                    // ui_transition_plan.md step 5 (T1-6): frequency/mode now real -- prefer the RX
                    // frame's own LATCHED metadata (step 6, T2-4), falling back to live radio state
                    // only when the frame has none (see LogbookPaneViewModel.PrefillForNewEntry's own
                    // doc comment for why -- both stay null, never a fabricated value, if neither
                    // source has one).
                    // Fable UX-review finding, 2026-08-30: vm.RxImage.CurrentEntryId links the
                    // resulting QSO back to this frame's own ReceiveHistory entry -- previously
                    // omitted, so this path never linked (unlike Gallery's "Open in log").
                    // RST default plan (2026-09-01): vm.DefaultRst is MainViewModel's own cache
                    // (LoadOperatorSettingsAsync), refreshed at startup/Options-close/Configurations-
                    // switch like Callsign already is -- "?? OperatorSettings.DefaultRstFallback" only
                    // covers the narrow window before that first load completes.
                    // fsk_cwid.md A-P3b: vm.RxImage.DecodedNrRst (the other station's own decoded
                    // FSK NR/RST) is passed separately -- PrefillForNewEntry's own doc comment for
                    // why this seeds FormRstReceived specifically, not FormRstSent.
                    vm.Logbook.PrefillForNewEntry(
                        vm.RxImage.OverrideCallsign,
                        vm.RxImage.DetectedMode?.Id,
                        vm.RxImage.StartedAt ?? DateTimeOffset.UtcNow,
                        vm.RxImage.LookupName,
                        vm.RxImage.LookupQth,
                        vm.RxImage.LookupGrid,
                        vm.RxImage.LatchedFrequencyHz ?? vm.RadioStatus.CurrentFrequencyHz,
                        vm.RxImage.LatchedRigMode ?? vm.RadioStatus.CurrentRadioModeOrNull,
                        vm.RxImage.CurrentEntryId,
                        vm.DefaultRst ?? OperatorSettings.DefaultRstFallback,
                        vm.RxImage.DecodedNrRst);
                    vm.SelectedTabIndex = MainViewModel.LogbookTabIndex;
                };

                // ui_transition_plan.md step 3 (T1-5 + T2-6): non-modal (Show, not ShowDialog) --
                // an operator inspecting a weak-signal frame closely shouldn't be locked out of the
                // rest of the app while a new reception is still coming in. Both Receive
                // (PreviousFrames) and Gallery (FilteredEntries) route through the same event/View
                // shape; each source VM already builds the ImageViewerWindowViewModel itself (same
                // "source VM constructs, this handler just wraps it in a View" convention
                // MacrosReferenceRequested below uses), so this handler needs no knowledge of which
                // list it came from.
                vm.RxImage.ImageViewerRequested += viewerViewModel =>
                {
                    if (logger is not null)
                    {
                        Log.ConstructingImageViewerWindow(logger);
                    }

                    var window = new ImageViewerWindowView { DataContext = viewerViewModel };
                    window.Show(this);
                };
                vm.RxHistory.ImageViewerRequested += viewerViewModel =>
                {
                    if (logger is not null)
                    {
                        Log.ConstructingImageViewerWindow(logger);
                    }

                    var window = new ImageViewerWindowView { DataContext = viewerViewModel };
                    window.Show(this);
                };

                // ui_transition_plan.md step 12 (Auto-save RX audio), Step 4: RxHistoryPaneViewModel
                // deliberately does not depend on ISstvSessionService (see RedecodeRequested's own
                // doc comment) -- RxImagePaneViewModel already owns this exact responsibility for the
                // Receive tab's own "Decode WAV…" button, so this just reaches across to it.
                vm.RxHistory.RedecodeRequested += path =>
                {
                    // Auditor-caught (round 1 code-review): ICommand.Execute does not consult
                    // CanExecute the way a bound Button would -- explicit check so a Gallery click
                    // while the Receive tab's own file-picker decode is already in flight is a no-op
                    // here too, not just inside RedecodeFromPathAsync's own belt-and-suspenders guard.
                    if (vm.RxImage.RedecodeFromPathCommand.CanExecute(path))
                    {
                        vm.RxImage.RedecodeFromPathCommand.Execute(path);
                    }
                };

                // ui_transition_plan.md step 4 (T1-4, reframed): same delegate-property shape as
                // ConfigurationsManagerWindowViewModel.ConfirmRequested's own wiring below -- a
                // genuine request/response the Delete command awaits before continuing.
                vm.RxHistory.ConfirmRequested = async confirmVm =>
                {
                    var confirmView = new ConfirmActionDialogView { DataContext = confirmVm };
                    return await confirmView.ShowDialog<bool>(this);
                };

                // ui_transition_plan.md step 15 -- same shape as vm.RxHistory.ConfirmRequested above.
                vm.Logbook.ConfirmRequested = async confirmVm =>
                {
                    var confirmView = new ConfirmActionDialogView { DataContext = confirmVm };
                    return await confirmView.ShowDialog<bool>(this);
                };

                // ui_transition_plan.md step 5 (T1-6): same two values LogQsoRequested's own handler
                // above already trusts as "the received station's callsign/grid" -- see
                // TxControlsPaneViewModel.CurrentContactRequested's own doc comment.
                vm.TxControls.CurrentContactRequested = () => (vm.RxImage.OverrideCallsign, vm.RxImage.LookupGrid);

                // RX/TX pipeline fix plan (2026-09-01), item 1 -- same shape as CurrentContactRequested above.
                vm.TxControls.RequestTransmitTabFocus = () => vm.SelectedTabIndex = MainViewModel.TransmitTabIndex;

                // Macros help plan (2026-09-01), item B -- reuses OpenMacrosReferenceCommand's existing
                // resolve-and-show logic as-is (see the MacrosReferenceRequested wiring further below);
                // no extraction/duplication needed.
                vm.TxControls.RequestMacrosReference = () => vm.OpenMacrosReferenceCommand.Execute(null);

                // RX/TX pipeline fix plan (2026-09-01), item 3 -- a plain settable delegate PROPERTY
                // assignment (not `+=`), matching CurrentContactRequested's own convention above; the
                // Gallery is the INITIATOR here (unlike CurrentContactRequested, where TxControls
                // pulls from RX), so the delegate lives on RxHistory instead.
                vm.RxHistory.SendToTxRequested = request => vm.TxControls.OpenEditorForExternalFileAsync(request.FilePath, request.ContactVariables);

                // Worked-before plan (2026-09-01) -- Logbook is the INITIATOR here (a QSO was just
                // logged), same "plain assignment, not `+=`" convention as SendToTxRequested above.
                vm.Logbook.QsoLogged = vm.RxImage.NotifyQsoLogged;

                // Stub survey Tier 3 (2026-08-26). Same shape as OptionsRequested above --
                // DI-resolved view-model. Storage's own former entry here (stub survey Tier 2) was
                // removed once its one setting (the RX images folder) moved into Options > General
                // alongside the new Config/Database/Log rows.
                vm.MacrosReferenceRequested += macrosViewModel =>
                {
                    if (logger is not null)
                    {
                        Log.ConstructingMacrosReferenceWindow(logger);
                    }

                    try
                    {
                        var window = new MacrosReferenceWindowView { DataContext = macrosViewModel };
                        window.ShowDialog(this);
                        if (logger is not null)
                        {
                            Log.MacrosReferenceShowDialogReturned(logger);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (logger is not null)
                        {
                            Log.MacrosReferenceWindowFailed(logger, ex);
                        }
                    }
                };

                // Configurations-preset backlog, Phase 4 (2026-08-28). Same shape as
                // MacrosReferenceRequested above. window.Closed re-runs the SAME refresh block
                // OptionsRequested's own Closed handler below uses, PLUS a frequency-presets reload --
                // a preset switch can change every settings section Options' own dialog owns, so it
                // needs the identical readout refresh, and it can ALSO change FrequencyPresetsSettings
                // (the header's M1-M7 favourite buttons), which nothing else on this window refreshes.
                // Safe to run unconditionally even if the dialog was only browsed/closed with no
                // switch -- same "no-op reload of the same value" reasoning as Options' own comment.
                // Factored out (2026-08-28, user-requested quick-switch addition) -- the SAME refresh
                // block now runs from two places: this dialog's own Closed handler below, and the new
                // quick-switch menu's post-switch handler further down.
                void RefreshAfterConfigurationChange()
                {
                    _ = vm.LoadOperatorSettingsAsync();
                    _ = vm.TxControls.LoadOutputDeviceNameAsync();
                    _ = vm.TxControls.LoadIdentificationSummaryAsync();
                    _ = vm.RxHistory.LoadImagesDirectoryAsync();
                    _ = vm.RxHistory.LoadAudioStorageInfoAsync();
                    _ = vm.RxImage.LoadQrzLookupConfiguredAsync();
                    vm.RxImage.RefreshSenseLevelFromSession();
                    vm.RxImage.RefreshAutoSlantEnabledFromSession();
                    _ = vm.RadioStatus.LoadPresetsSafeAsync();
                    // User-requested (2026-08-28): the window title now shows the active configuration
                    // name -- a switch is exactly the moment it can change.
                    _ = vm.LoadActiveConfigurationNameAsync();
                }

                // Configurations-preset backlog, Phase 4b (2026-08-28, cascading-menu redesign --
                // direct user correction, see docs/plans/configuration-presets-phase4b-cascading-menu-plan.md).
                // No more standalone "Manage Configurations" dialog -- every saved configuration gets
                // its own top-level MenuItem with a nested per-action submenu, built fresh every time
                // this menu opens (never kept live for the whole app lifetime).
                //
                // ConfigurationsMenuItem.Items always keeps ConfigurationsNoneSavedPlaceholder
                // (declared in XAML) as its LAST entry -- an Avalonia MenuItem with zero Items never
                // opens a submenu popup at all, so leaving Items empty here would mean SubmenuOpened
                // itself could never fire again. This handler clears everything BEFORE that
                // placeholder and re-inserts the current configuration list at the front, every time,
                // then toggles the placeholder's own visibility based on whether the list is empty
                // (it should never really be, since Default always auto-seeds -- defensive only).
                //
                // Guarded on e.Source: SubmenuOpened bubbles (RoutingStrategies.Bubble), and every
                // per-configuration item built below has its own nested submenu -- hovering one raises
                // SubmenuOpened on THAT item first, which then bubbles up to this handler too. Without
                // the guard, that would re-run the whole rebuild and tear down the very item the user
                // is hovering. _isPopulatingConfigurationsMenu additionally blocks two overlapping
                // opens of the top-level item itself (this handler awaits before mutating Items).
                ConfigurationsMenuItem.SubmenuOpened += async (_, e) =>
                {
                    if (!ReferenceEquals(e.Source, ConfigurationsMenuItem) || _isPopulatingConfigurationsMenu)
                    {
                        return;
                    }

                    _isPopulatingConfigurationsMenu = true;
                    try
                    {
                        var configVm = vm.ResolveConfigurationsManagerViewModel();
                        configVm.TextPromptRequested = async promptVm =>
                        {
                            var promptView = new TextPromptWindowView { DataContext = promptVm };
                            return await promptView.ShowDialog<string?>(this);
                        };
                        configVm.ConfirmRequested = async confirmVm =>
                        {
                            var confirmView = new ConfirmActionDialogView { DataContext = confirmVm };
                            return await confirmView.ShowDialog<bool>(this);
                        };
                        await configVm.RefreshAsync();

                        while (ConfigurationsMenuItem.Items.Count > 1)
                        {
                            ConfigurationsMenuItem.Items.RemoveAt(0);
                        }

                        ConfigurationsNoneSavedPlaceholder.IsVisible = configVm.Presets.Count == 0;

                        var localization = App.Services?.GetService<ILocalizationService>();

                        // A menu Click fires before Avalonia closes the popup -- opening a dialog
                        // synchronously inside a Click handler is new exposure this redesign
                        // introduces on every action (Clone/Rename/Delete/Reset all show a dialog
                        // now, not just the rare quick-switch-failure case the old handler had).
                        // Task.Yield() lets the menu finish closing first.
                        //
                        // Every action shares the same failure-surfacing shape the original round-1
                        // code-review fix added for Switch alone: CloneAsync/RenameAsync/DeleteAsync/
                        // SwitchToRowAsync/ResetToDefaultAsync all set ErrorMessage on failure, and
                        // none of them are ever shown in a window of their own -- read it back after
                        // every action, not just Switch, or Clone/Rename/Delete failures go silent the
                        // exact same way Switch's used to.
                        async Task InvokeConfigurationActionAsync(Func<Task> action)
                        {
                            await Task.Yield();
                            await action();
                            if (configVm.ErrorMessage is { } errorMessage)
                            {
                                try
                                {
                                    var dialogVm = new QuickSwitchFailedDialogViewModel(errorMessage);
                                    var dialog = new QuickSwitchFailedDialogView { DataContext = dialogVm };
                                    await dialog.ShowDialog(this);
                                }
                                catch (Exception ex)
                                {
                                    if (logger is not null)
                                    {
                                        Log.QuickSwitchFailedDialogFailed(logger, ex);
                                    }
                                }
                            }

                            RefreshAfterConfigurationChange();
                        }

                        MenuItem BuildActionItem(string localeKey, Func<Task> action)
                        {
                            var header = localization?.GetString(localeKey) ?? localeKey;
                            var actionItem = new MenuItem { Header = header };
                            actionItem.Click += async (_, _) => await InvokeConfigurationActionAsync(action);
                            return actionItem;
                        }

                        var insertAt = 0;
                        foreach (var row in configVm.Presets)
                        {
                            var rowItem = new MenuItem
                            {
                                Header = row.Name,
                                ToggleType = MenuItemToggleType.CheckBox,
                                IsChecked = row.IsActive,
                            };

                            if (row.IsProtected)
                            {
                                // User-caught gap (2026-08-28): Default had no way back once you
                                // switched away from it -- "Reset" alone isn't discoverable as "get
                                // back to Default." Default now gets "Switch To" too, same as any
                                // other inactive row, alongside "Reset" (the confirmed version of the
                                // exact same underlying switch, kept for the "prevent accidental
                                // resets" ask).
                                if (!row.IsActive)
                                {
                                    rowItem.Items.Add(BuildActionItem("Configurations.SwitchToMenuItem", () => configVm.SwitchToRowCommand.ExecuteAsync(row)));
                                    rowItem.Items.Add(new Separator());
                                }

                                // No Rename here (user-requested, 2026-08-28): protection is keyed on
                                // the literal string "Default" (ConfigurationPresetRowViewModel.IsProtected),
                                // not any persistent identity -- renaming it doesn't customize Default,
                                // it detaches the content under a new (fully deletable, unprotected)
                                // name while RefreshAsync's own seed-if-missing check spawns a BRAND
                                // NEW "Default" from current live settings the next time the menu
                                // opens. Two near-identical presets where the user expected one renamed
                                // one -- a real trap, not a feature -- so Rename is deliberately left
                                // off Default's own submenu.
                                rowItem.Items.Add(BuildActionItem("Configurations.CloneIntoMenuItem", () => configVm.CloneCommand.ExecuteAsync(row)));
                                rowItem.Items.Add(BuildActionItem("Configurations.ResetMenuItem", () => configVm.ResetToDefaultCommand.ExecuteAsync(row)));
                            }
                            else if (row.IsActive)
                            {
                                rowItem.Items.Add(BuildActionItem("Configurations.CloneIntoMenuItem", () => configVm.CloneCommand.ExecuteAsync(row)));
                                rowItem.Items.Add(BuildActionItem("Configurations.RenameMenuItem", () => configVm.RenameCommand.ExecuteAsync(row)));
                            }
                            else
                            {
                                rowItem.Items.Add(BuildActionItem("Configurations.SwitchToMenuItem", () => configVm.SwitchToRowCommand.ExecuteAsync(row)));
                                rowItem.Items.Add(new Separator());
                                rowItem.Items.Add(BuildActionItem("Configurations.CloneIntoMenuItem", () => configVm.CloneCommand.ExecuteAsync(row)));
                                rowItem.Items.Add(BuildActionItem("Configurations.RenameMenuItem", () => configVm.RenameCommand.ExecuteAsync(row)));
                                if (row.CanDelete)
                                {
                                    rowItem.Items.Add(BuildActionItem("Configurations.DeleteMenuItem", () => configVm.DeleteCommand.ExecuteAsync(row)));
                                }
                            }

                            ConfigurationsMenuItem.Items.Insert(insertAt++, rowItem);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (logger is not null)
                        {
                            Log.ConfigurationsQuickSwitchPopulateFailed(logger, ex);
                        }
                    }
                    finally
                    {
                        _isPopulatingConfigurationsMenu = false;
                    }
                };

                // Stub survey Tier 2 (2026-08-26). Same synchronous, unawaited shape as
                // OptionsRequested/AboutRequested above -- ShowDialog itself is synchronous (blocks
                // until the dialog closes), no async construction work precedes it.
                vm.RadioStatus.FavouritesEditorRequested += () =>
                {
                    if (logger is not null)
                    {
                        Log.ConstructingFavouritesEditorWindow(logger);
                    }

                    try
                    {
                        var window = new FavouritesEditorWindowView { DataContext = vm.RadioStatus };
                        window.ShowDialog(this);
                        if (logger is not null)
                        {
                            Log.FavouritesEditorShowDialogReturned(logger);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (logger is not null)
                        {
                            Log.FavouritesEditorWindowFailed(logger, ex);
                        }
                    }
                };

                vm.RadioStatus.ToneGeneratorRequested += () =>
                {
                    if (logger is not null)
                    {
                        Log.ConstructingToneGeneratorWindow(logger);
                    }

                    try
                    {
                        var window = new ToneGeneratorWindowView { DataContext = vm.RadioStatus };
                        window.ShowDialog(this);
                        if (logger is not null)
                        {
                            Log.ToneGeneratorShowDialogReturned(logger);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (logger is not null)
                        {
                            Log.ToneGeneratorWindowFailed(logger, ex);
                        }
                    }
                };

                // Stub survey Tier 3 "Loopback self-test" (2026-08-26). Same shape as
                // FavouritesEditorRequested/ToneGeneratorRequested above -- pane-level event, not
                // forwarded through MainViewModel, since the result window needs a fresh, single-use
                // view-model (the result of ONE self-test run), not TxControls itself as its
                // DataContext.
                vm.TxControls.LoopbackSelfTestCompleted += result =>
                {
                    if (logger is not null)
                    {
                        Log.ConstructingLoopbackSelfTestResultWindow(logger);
                    }

                    try
                    {
                        var window = new LoopbackSelfTestResultWindowView { DataContext = result };
                        window.ShowDialog(this);
                        if (logger is not null)
                        {
                            Log.LoopbackSelfTestResultShowDialogReturned(logger);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (logger is not null)
                        {
                            Log.LoopbackSelfTestResultWindowFailed(logger, ex);
                        }
                    }
                };

                vm.ExitRequested += () =>
                {
                    if (logger is not null)
                    {
                        Log.ExitRequested(logger);
                    }

                    Close();
                };
            }
        };
    }

    // ui_transition_plan.md step 3 (T1-5): three DoubleTapped entry points into the full-size
    // viewer, each reading its own per-item DataContext directly off the tapped control (same
    // "code-behind pointer handler inside a DataTemplate" convention TxImageEditorPaneView.axaml.cs's
    // own crop-handle handlers already use) rather than trying to bind a routed event to a command
    // in AXAML.

    private void OnIncomingFrameDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.RxImage.OpenImageViewerCommand.Execute(null);
        }
    }

    private void OnPreviousFrameThumbnailDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is Control { DataContext: RxHistoryEntryViewModel entry } && DataContext is MainViewModel vm)
        {
            vm.RxImage.OpenImageViewerCommand.Execute(entry);
        }
    }

    private void OnGalleryThumbnailDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is Control { DataContext: RxHistoryEntryViewModel entry } && DataContext is MainViewModel vm)
        {
            vm.RxHistory.OpenImageViewerCommand.Execute(entry);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Constructing and showing ImageViewerWindowView")]
        public static partial void ConstructingImageViewerWindow(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Constructing and showing OptionsWindowView")]
        public static partial void ConstructingOptionsWindow(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "OptionsWindowView opened: Position={Position}, Screen={Screen}")]
        public static partial void OptionsWindowOpened(ILogger logger, string position, string screen);

        [LoggerMessage(Level = LogLevel.Debug, Message = "OptionsWindowView.ShowDialog returned")]
        public static partial void ShowDialogReturned(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Restart requested from Options > General (Config/Database Restart Now) -- closing Options, then the main window")]
        public static partial void RestartRequested(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Constructing and showing MacrosReferenceWindowView")]
        public static partial void ConstructingMacrosReferenceWindow(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "MacrosReferenceWindowView.ShowDialog returned")]
        public static partial void MacrosReferenceShowDialogReturned(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "MacrosReferenceWindowView failed to open or show")]
        public static partial void MacrosReferenceWindowFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Populating the Configurations quick-switch menu failed")]
        public static partial void ConfigurationsQuickSwitchPopulateFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QuickSwitchFailedDialogView failed to open or show")]
        public static partial void QuickSwitchFailedDialogFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Exit requested; closing main window")]
        public static partial void ExitRequested(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Constructing and showing AboutWindowView")]
        public static partial void ConstructingAboutWindow(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "AboutWindowView.ShowDialog returned")]
        public static partial void AboutShowDialogReturned(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Constructing and showing QsoLinkWindowView")]
        public static partial void ConstructingQsoLinkWindow(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "QsoLinkWindowView.ShowDialog returned")]
        public static partial void QsoLinkShowDialogReturned(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QsoLinkWindowView failed to open or show")]
        public static partial void QsoLinkWindowFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Constructing and showing FavouritesEditorWindowView")]
        public static partial void ConstructingFavouritesEditorWindow(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "FavouritesEditorWindowView.ShowDialog returned")]
        public static partial void FavouritesEditorShowDialogReturned(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "FavouritesEditorWindowView failed to open or show")]
        public static partial void FavouritesEditorWindowFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Constructing and showing ToneGeneratorWindowView")]
        public static partial void ConstructingToneGeneratorWindow(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "ToneGeneratorWindowView.ShowDialog returned")]
        public static partial void ToneGeneratorShowDialogReturned(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "ToneGeneratorWindowView failed to open or show")]
        public static partial void ToneGeneratorWindowFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Constructing and showing LoopbackSelfTestResultWindowView")]
        public static partial void ConstructingLoopbackSelfTestResultWindow(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "LoopbackSelfTestResultWindowView.ShowDialog returned")]
        public static partial void LoopbackSelfTestResultShowDialogReturned(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "LoopbackSelfTestResultWindowView failed to open or show")]
        public static partial void LoopbackSelfTestResultWindowFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Log QSO requested from the RX pane; switching to the Logbook tab")]
        public static partial void LogQsoRequested(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Restored window position ({X}, {Y}) is not on any known screen; using the default position instead")]
        public static partial void RestoredWindowPositionOffScreen(ILogger logger, int x, int y);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to save window geometry on close")]
        public static partial void WindowGeometrySaveFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "OptionsWindowView failed to open or show")]
        public static partial void OptionsWindowFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "AboutWindowView failed to open or show")]
        public static partial void AboutWindowFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Constructing and showing RadioConnectionGaveUpWindowView")]
        public static partial void ConstructingConnectionGaveUpWindow(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "RadioConnectionGaveUpWindowView.ShowDialog returned")]
        public static partial void ConnectionGaveUpShowDialogReturned(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "RadioConnectionGaveUpWindowView failed to open or show")]
        public static partial void ConnectionGaveUpWindowFailed(ILogger logger, Exception ex);
    }
}
