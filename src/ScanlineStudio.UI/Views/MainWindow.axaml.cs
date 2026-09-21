using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
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

    // User-reported bug (2026-09-03), Windows only: re-entrancy guard for the maximize-bounds
    // recovery handler below -- see that handler's own doc comment.
    private bool _isRecoveringMaximizedBounds;

    // Code-review finding (2026-09-03): set whenever the constructor took manual control of
    // Position/Width/Height -- both the real-restore branch AND the computed-default branch (root
    // cause #3) -- see the Opened handler below for why this needs to be re-checked once the
    // window is shown.
    private bool _restoredWindowGeometryThisLaunch;

    // User-reported bug (2026-09-03), root cause #3: kept in sync BY HAND with
    // MainWindow.axaml's own Width="1920" Height="1032" -- no shared resource exists for this
    // (same "no single shared resource, both are literals" situation RadioHeaderView.axaml's own
    // band-height comment already documents for a different pair of duplicated literals). Used as
    // the fallback requested size whenever there's no usable persisted geometry to restore.
    private const double DefaultWidth = 1920;
    private const double DefaultHeight = 1032;

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
        //
        // User-directed 2026-09-03 ("how hard is it to set the window to maximized if that's what
        // we closed with"): declared out here, not inside the block below, so it survives to the
        // very end of this constructor -- WindowState = Maximized must be assigned AFTER the
        // Windows-only maximize-bounds-recovery PropertyChanged subscription further down (it reacts
        // to exactly this transition), not from inside this block, which runs before that
        // subscription exists.
        var shouldStartMaximized = false;
        if (_settingsStore is not null)
        {
            var geometry = Task.Run(() => _settingsStore.LoadAsync()).GetAwaiter().GetResult()
                .GetSection(WindowGeometrySettings.SectionKey, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings);

            // User-reported bug (2026-09-03), root cause #3, found only after the first two fixes
            // (the restore-time clamp below, and the maximize-toggle fix elsewhere in this file)
            // both failed to help a real user: an operator who ALWAYS closes from Maximized --
            // "what else is the maximize button there for," an entirely ordinary habit, not an
            // edge case -- never triggers so much as ONE Normal-state Closing save (that handler's
            // own doc comment: intentionally matches legacy's wsNormal-gated save,
            // Main.cpp:2214-2224). On such an install `geometry` has Left/Top/Width/Height ALL
            // null FOREVER, even with RememberWindowPosition=true, so the pattern match below
            // never matches and NOTHING downstream of it -- including the clamp this same comment
            // block used to describe -- ever runs. Every launch fell straight through to this
            // file's own AXAML-declared Width="1920" Height="1032" with WindowStartupLocation=
            // "CenterScreen" and ZERO clamping: Avalonia's CenterScreen only centers, it never
            // resizes, so a Height taller than the ACTUAL current work area (this default was
            // sized assuming roughly a 48px taskbar, per its own AXAML comment) overflows the work
            // area SYMMETRICALLY once centered -- title bar pushed above the screen, status bar
            // pushed below the taskbar by roughly equal amounts -- reproducing the exact reported
            // symptom on EVERY SINGLE LAUNCH for this usage pattern, regardless of anything the
            // first two fixes changed. Fixed by no longer requiring restored geometry to reach the
            // clamp at all: a genuinely-unset Left/Top/Width/Height now falls back to
            // DefaultWidth/DefaultHeight, manually centered on the CURRENT primary screen's own
            // WorkingArea (not Avalonia's own CenterScreen, which this constructor overrides to
            // Manual below regardless of which branch is taken) -- still run through the exact
            // SAME live-work-area clamp as a genuine restore, rather than left unclamped.
            // RememberWindowPosition=false still means "don't persist a resize" (unchanged, see
            // the Closing handler below) but no longer means "skip this baseline fits-on-screen
            // guarantee too" -- those are two different promises.
            double left;
            double top;
            double width;
            double height;
            bool isRestoredGeometry;
            if (geometry is { RememberWindowPosition: true, Left: { } restoredLeft, Top: { } restoredTop, Width: { } restoredWidth, Height: { } restoredHeight })
            {
                left = restoredLeft;
                top = restoredTop;
                width = restoredWidth;
                height = restoredHeight;
                isRestoredGeometry = true;
            }
            else
            {
                width = DefaultWidth;
                height = DefaultHeight;
                isRestoredGeometry = false;
                var primaryScreen = Screens.Primary;
                if (primaryScreen is not null)
                {
                    var centered = WindowGeometryPolicy.CenterInWorkArea(primaryScreen.WorkingArea, width, height, primaryScreen.Scaling);
                    left = centered.X;
                    top = centered.Y;
                }
                else
                {
                    // No screen info at all this early (same "empty screen list" edge case
                    // ShouldRestorePosition's own doc comment below already covers) -- (0,0) is a
                    // harmless placeholder; the clamp below is skipped entirely in that same case
                    // (targetScreen also resolves to null), same as it would have been before this
                    // fix existed.
                    left = 0;
                    top = 0;
                }
            }

            // Tier C audit finding (risk): applied with zero bounds validation before this fix --
            // a position persisted while on a since-removed monitor (unplugged second display, a
            // resolution change) restored to coordinates with no display behind them. Windows
            // does not clamp this, so the app appeared not to start, and the only recovery was
            // hand-deleting settings.json (the Options toggle to turn this off lives inside the
            // invisible window). `Screens.All` may not be reliably populated this early in every
            // Avalonia configuration -- if the list comes back empty, fall back to trusting the
            // persisted value rather than disabling the whole feature; only reject when a screen
            // list IS available and genuinely none of them contain this position.
            //
            // Code-review blocker (2026-09-03): this check must ONLY gate a REAL persisted
            // position (isRestoredGeometry) -- it exists to reject a STALE position that might
            // belong to a since-removed monitor, and rejects any negative X/Y as "off-screen" by
            // construction. The COMPUTED default position (the else branch above,
            // WindowGeometryPolicy.CenterInWorkArea's own doc comment) can be legitimately
            // negative whenever the default size is larger than the actual current work area --
            // exactly the condition that motivated this whole fix (a taller-than-default taskbar,
            // or >100% DPI scaling) -- so gating that computed position through this same check
            // would reject it in precisely the case it exists to fix, silently falling all the way
            // back through to Avalonia's own unclamped CenterScreen and reproducing the original
            // bug. The computed position is trustworthy by construction (derived from the CURRENT
            // screen's own live bounds, not a stale persisted value) and goes straight to the
            // clamp below regardless of this check's result.
            var startPosition = new PixelPoint((int)left, (int)top);
            var screenBounds = Screens.All.Select(s => s.Bounds).ToList();
            if (!isRestoredGeometry || WindowGeometryPolicy.ShouldRestorePosition(startPosition, screenBounds))
            {
                // User-reported bug (2026-09-03): ShouldRestorePosition above only ever validated
                // the top-left POINT, never whether the saved/default Width/Height actually fit
                // the CURRENT work area (screen minus taskbar) -- a size that fit a previous
                // session's taskbar/monitor/DPI (or this file's own default, sized for a roughly
                // 48px taskbar) restored/applied verbatim even once it no longer fit (worse with a
                // taller-than-default taskbar). Clamped here instead, unconditionally, on every
                // launch -- see WindowGeometryPolicy.ClampToWorkArea's own doc comment for why
                // this re-derives against the LIVE work area every time rather than trusting a
                // fixed value (the work area can change for many reasons: a different monitor, a
                // different taskbar size, a DPI/resolution change). ClampToWorkArea operates in
                // PHYSICAL pixels (Screen.WorkingArea's own unit); Width/Height are DIP-valued
                // Window properties, so this converts through the TARGET screen's own Scaling
                // factor both ways -- using the target screen found via ScreenFromPoint, not
                // this.RenderScaling, since the window hasn't been shown/assigned to a real screen
                // yet at this point in the constructor.
                //
                // Code-review blocker (2026-09-03): MainWindow.axaml's own
                // WindowStartupLocation="CenterScreen" runs AFTER the constructor, inside
                // Avalonia's own Show() sequence -- it RE-CENTERS on the working area and
                // discards whatever Position was just set here, making the clamp above dead
                // code for POSITION specifically (Width/Height still stick, since centering
                // only touches Position). Switching to Manual here (on this whole branch, covering
                // both a real restore AND the computed default above) preserves CenterScreen only
                // for the narrow leftover case that never reaches this branch at all (a
                // since-removed monitor rejected by ShouldRestorePosition above).
                WindowStartupLocation = WindowStartupLocation.Manual;
                _restoredWindowGeometryThisLaunch = true;
                var targetScreen = Screens.ScreenFromPoint(startPosition) ?? Screens.Primary;
                if (targetScreen is not null)
                {
                    var scaling = targetScreen.Scaling;
                    var requested = new PixelRect(startPosition, new PixelSize((int)Math.Round(width * scaling), (int)Math.Round(height * scaling)));
                    var clamped = WindowGeometryPolicy.ClampToWorkArea(requested, targetScreen.WorkingArea);
                    Position = clamped.Position;
                    Width = clamped.Width / scaling;
                    Height = clamped.Height / scaling;
                }
                else
                {
                    // No screen info available at all (same "trust the persisted value"
                    // fallback ShouldRestorePosition's own doc comment already established
                    // for this early-startup case) -- restore verbatim, unclamped.
                    Position = startPosition;
                    Width = width;
                    Height = height;
                }
            }
            else if (logger is not null)
            {
                Log.RestoredWindowPositionOffScreen(logger, startPosition.X, startPosition.Y);
            }

            // User-directed 2026-09-03 ("how hard is it to set the window to maximized if that's
            // what we closed with"): only the FLAG is set here -- Position/Width/Height are already
            // established above (from a real restore, a computed default, or -- the rejected-
            // position fallback -- Avalonia's own CenterScreen) and still matter as the "restore to"
            // bounds for whenever the operator later un-maximizes, regardless of whether this ends
            // up applying. The actual WindowState assignment happens at the very end of this
            // constructor (see shouldStartMaximized's own doc comment above for why: it has to run
            // AFTER the maximize-bounds-recovery PropertyChanged subscription exists, so a
            // restored-maximized window gets the exact same live bounds recompute a manual maximize
            // click would, not a second, separate code path to keep in sync). Gated on
            // RememberWindowPosition, same preference that gates everything else in this block --
            // "don't remember my window" should mean don't remember this either.
            shouldStartMaximized = geometry is { RememberWindowPosition: true, WasMaximized: true };
        }

        // Code-review finding (2026-09-03): the constructor's own clamp above compares the
        // DIP-valued CLIENT Width/Height against the working area -- it has no way to know the
        // window's actual FRAME size (title bar + borders) before the window is shown, since
        // Avalonia's own FrameSize is null pre-show. A window whose CLIENT size fits exactly can
        // still overflow the working area once its frame chrome (title bar, typically ~30-40
        // physical px on Windows) is added on top -- a smaller residual version of the exact
        // symptom this whole fix targets. Re-clamps once, right after the window is actually shown
        // and FrameSize becomes real, using the true frame-vs-client delta this time. Scoped to
        // _restoredWindowGeometryThisLaunch -- set by BOTH the real-restore branch and the
        // computed-default branch above (root cause #3), since this file now takes manual control
        // of Position/Width/Height on both paths; only left unset in the one remaining case that
        // still reaches Avalonia's own unclamped CenterScreen (a genuinely stale/off-screen
        // persisted position, rejected by ShouldRestorePosition).
        Opened += (_, _) =>
        {
            if (!_restoredWindowGeometryThisLaunch || WindowState != WindowState.Normal || FrameSize is not { } frameSize)
            {
                return;
            }

            var targetScreen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (targetScreen is null)
            {
                return;
            }

            var scaling = targetScreen.Scaling;
            var clientWidthPx = Width * scaling;
            var clientHeightPx = Height * scaling;
            var frameWidthPx = frameSize.Width * scaling;
            var frameHeightPx = frameSize.Height * scaling;
            // The chrome this client size doesn't already account for -- title bar + borders, in
            // physical pixels. Clamping the FRAME rect (not the client rect) against the working
            // area, then subtracting this same delta back off the clamped frame size, is what
            // actually keeps the OUTER window (what the operator sees, including the title bar)
            // inside the taskbar-excluded area -- clamping client size alone (the constructor's
            // own first pass) systematically under-corrects by exactly this amount.
            var decorationWidthPx = frameWidthPx - clientWidthPx;
            var decorationHeightPx = frameHeightPx - clientHeightPx;

            var requestedFrame = new PixelRect(Position, new PixelSize((int)Math.Round(frameWidthPx), (int)Math.Round(frameHeightPx)));
            var clampedFrame = WindowGeometryPolicy.ClampToWorkArea(requestedFrame, targetScreen.WorkingArea);
            if (clampedFrame == requestedFrame)
            {
                // Already fit once the real frame size was known -- nothing to correct, and
                // skipping the reassignment avoids a redundant resize/reflow on the common case.
                return;
            }

            Position = clampedFrame.Position;
            Width = Math.Max(0, (clampedFrame.Width - decorationWidthPx) / scaling);
            Height = Math.Max(0, (clampedFrame.Height - decorationHeightPx) / scaling);
        };

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
            // User-directed 2026-09-03 ("how hard is it to set the window to maximized if that's
            // what we closed with"): the gate now also accepts Maximized, not just Normal --
            // WindowGeometrySettings.WasMaximized's own doc comment for the full design. Minimized
            // is still excluded (same as before this change): no meaningful "restore to" state
            // exists for it, and legacy's own wsNormal-only save gate never covered it either.
            var closingState = WindowState;
            if (_settingsStore is null || (closingState != WindowState.Normal && closingState != WindowState.Maximized))
            {
                return;
            }

            // Only captured/used when closingState == Normal below -- while Maximized, Position/
            // Width/Height reflect the MAXIMIZED bounds, not a Normal-state size worth persisting
            // as the "restore to" fallback (see WasMaximized's own doc comment for why those four
            // fields are left untouched entirely in that case, not overwritten with maximized
            // values).
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

                    // WasMaximized always reflects THIS close's real state. Left/Top/Width/Height
                    // only update when closing from Normal -- when closing from Maximized, `current`'s
                    // own already-persisted values (the last real Normal-state size, or still null if
                    // this window has never once closed Normal) pass through unchanged, exactly the
                    // "restore to" fallback WasMaximized's own doc comment describes.
                    var updated = closingState == WindowState.Normal
                        ? current with { Left = left, Top = top, Width = width, Height = height, WasMaximized = false }
                        : current with { WasMaximized = true };
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

        // User-reported bug (2026-09-03), Windows only: a freshly-maximized window can render
        // slightly wrong -- the top sits a bit too high and the bottom extends below the taskbar
        // (worse with a taller-than-default taskbar) -- requiring the operator to manually drag
        // the window and re-maximize to correct it. This is a known Avalonia-on-Windows class of
        // issue, not something this app's own code was previously getting wrong (no custom window
        // chrome/decorations exist anywhere in this codebase, and maximize was purely native
        // Avalonia WindowState handling before this fix) -- see AvaloniaUI/Avalonia#19434, whose
        // own maintainer comment states outright that "things inside Screens.Primary doesn't work
        // well" on Windows; the native WM_GETMINMAXINFO-derived maximize bounds there can be
        // computed against a stale/wrong work area. The operator's own manual drag-then-re-maximize
        // already forces Windows to recompute against the CURRENT monitor correctly -- this
        // automates exactly that recompute instead of requiring it by hand: on transitioning INTO
        // Maximized, toggle back to Normal and immediately re-request Maximized on a posted
        // continuation (so the Normal transition's own native resize has already been processed
        // before Maximized is re-requested) -- same "toggle to force a recompute" shape as the
        // dirty fix documented in that same GitHub issue, applied to a different trigger (every
        // maximize, not just a live DPI/resolution change). _isRecoveringMaximizedBounds guards
        // against this handler re-entering itself on the SECOND (post-toggle) transition back into
        // Maximized -- without it, this would toggle forever. Not reproducible/testable from this
        // Linux dev environment -- needs real hands-on Windows confirmation; genuinely uncertain
        // this even fixes the underlying issue (the operator's own working fix included a DRAG, not
        // just a state toggle -- if Avalonia derives the wrong bounds from Screens.Primary rather
        // than the window's actual current monitor, a same-position toggle recomputes the same
        // wrong answer). Code-review hardening (2026-09-03): the posted continuation re-checks
        // WindowState is still Normal before re-maximizing (a user who minimizes/restores in the
        // gap before this runs must not have that overridden), and both statements are wrapped in
        // try/finally so a thrown exception can't leave the guard latched true forever (recovery
        // silently dead for the window's life) NOR escape unhandled on the UI thread -- every other
        // handler in this file is try/caught; this one was the one outlier before this fix.
        if (OperatingSystem.IsWindows())
        {
            PropertyChanged += (_, e) =>
            {
                if (e.Property != WindowStateProperty || WindowState != WindowState.Maximized || _isRecoveringMaximizedBounds)
                {
                    return;
                }

                _isRecoveringMaximizedBounds = true;
                WindowState = WindowState.Normal;
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (WindowState == WindowState.Normal)
                        {
                            WindowState = WindowState.Maximized;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (logger is not null)
                        {
                            Log.MaximizedBoundsRecoveryFailed(logger, ex);
                        }
                    }
                    finally
                    {
                        _isRecoveringMaximizedBounds = false;
                    }
                }, DispatcherPriority.Background);
            };
        }

        // User-directed 2026-09-03: the actual WindowState assignment shouldStartMaximized's own
        // doc comment (above, near this constructor's geometry block) promised -- deliberately
        // placed HERE, after the maximize-bounds-recovery subscription immediately above (Windows
        // only) already exists, so this transition gets caught and corrected by that same
        // mechanism exactly like a live user click would, not left to whatever Avalonia's own
        // maximize-from-code sizing does unassisted.
        if (shouldStartMaximized)
        {
            WindowState = WindowState.Maximized;
        }

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
                            if (logger is not null && logger.IsEnabled(LogLevel.Debug))
                            {
                                // CA1873 doesn't recognize a guard around a custom [LoggerMessage]
                                // partial method -- the IsEnabled check above already skips both
                                // ToString() calls when logging is disabled.
#pragma warning disable CA1873
                                Log.OptionsWindowOpened(logger, window.Position.ToString(), Screens.ScreenFromWindow(window)?.Bounds.ToString() ?? "(none)");
#pragma warning restore CA1873
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
                        //
                        // User-reported (2026-09-20): Closed alone left the chip stale while Apply
                        // was in use -- ApplyAsync deliberately never closes this window (see its own
                        // doc comment), so nothing above ever ran until the operator ALSO closed the
                        // dialog. OperatorSettingsSaved fires on every successful Save AND Apply, so
                        // subscribe it too for an immediate refresh; the Closed handler stays as-is
                        // (still needed for Cancel, and re-running this on Save's own Closed is a
                        // harmless no-op repeat of what OperatorSettingsSaved just did).
                        optionsViewModel.OperatorSettingsSaved += () => _ = vm.LoadOperatorSettingsAsync();
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

                // Gallery right-click "Add note..." (2026-09-19) -- same delegate-property shape as
                // vm.RxHistory.ConfirmRequested above, and the same construction shape
                // ConfigurationsManagerWindowViewModel.TextPromptRequested's own wiring uses further
                // below.
                vm.RxHistory.TextPromptRequested = async promptVm =>
                {
                    var promptView = new TextPromptWindowView { DataContext = promptVm };
                    return await promptView.ShowDialog<string?>(this);
                };

                // ui_transition_plan.md step 15 -- same shape as vm.RxHistory.ConfirmRequested above.
                vm.Logbook.ConfirmRequested = async confirmVm =>
                {
                    var confirmView = new ConfirmActionDialogView { DataContext = confirmVm };
                    return await confirmView.ShowDialog<bool>(this);
                };

                // Templates rack rework: unlike RxHistory/Logbook (constructed once), a
                // TxImageEditorPaneViewModel/ReadyRackViewModel pair is constructed FRESH every time
                // the operator opens the editor (TxControlsPaneViewModel.OpenEditor*Async), so the
                // same ConfirmRequested wiring has to happen once PER instance, via EditorOpened
                // rather than once at startup.
                vm.TxControls.EditorOpened += editor =>
                {
                    editor.ConfirmRequested = async confirmVm =>
                    {
                        var confirmView = new ConfirmActionDialogView { DataContext = confirmVm };
                        return await confirmView.ShowDialog<bool>(this);
                    };
                    editor.ReadyRack.ConfirmRequested = async confirmVm =>
                    {
                        var confirmView = new ConfirmActionDialogView { DataContext = confirmVm };
                        return await confirmView.ShowDialog<bool>(this);
                    };
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
                    vm.RxImage.RefreshAfcEnabledFromSession();
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

    /// <summary>User-requested (2026-09-19) Gallery right-click menu -- fires BEFORE the ContextMenu
    /// popup opens (not a Click), so <see cref="RxHistoryPaneViewModel.SelectedEntry"/> is already
    /// the right-clicked photo by the time any of its menu items run, matching the same
    /// SelectedEntry-only contract every other per-entry command in that class already has.</summary>
    private void OnGalleryThumbnailContextRequested(object? sender, Avalonia.Controls.ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: RxHistoryEntryViewModel entry } && DataContext is MainViewModel vm)
        {
            vm.RxHistory.SelectedEntry = entry;
        }
    }

    private void OnGalleryContextMenuOpenInLogClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.RxHistory.OpenInLogCommand.Execute(null);
        }
    }

    private void OnGalleryContextMenuExportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.RxHistory.ExportFrameCommand.Execute(null);
        }
    }

    private void OnGalleryContextMenuSendToTxClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.RxHistory.SendSelectedEntryToTxCommand.Execute(null);
        }
    }

    private void OnGalleryContextMenuToggleFlagClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.RxHistory.ToggleSelectedEntryFlagCommand.Execute(null);
        }
    }

    private void OnGalleryContextMenuAddNoteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.RxHistory.AddNoteToSelectedEntryCommand.Execute(null);
        }
    }

    private void OnGalleryContextMenuDeleteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.RxHistory.DeleteSelectedEntryCommand.Execute(null);
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

        [LoggerMessage(Level = LogLevel.Warning, Message = "Windows maximize-bounds recovery toggle failed")]
        public static partial void MaximizedBoundsRecoveryFailed(ILogger logger, Exception ex);

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
