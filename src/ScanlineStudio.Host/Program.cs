using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Cw;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Audio.MiniAudio;
using ScanlineStudio.Core.Cw;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Localization;
using ScanlineStudio.Core.Logbook;
using ScanlineStudio.Core.Radio;
using ScanlineStudio.Core.Radio.Flrig;
using ScanlineStudio.Core.Radio.Hamlib;
using ScanlineStudio.Core.Radio.OmniRig;
using ScanlineStudio.Core.Radio.Rigctld;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;
using ScanlineStudio.UI;
using ScanlineStudio.UI.Services;
using ScanlineStudio.UI.Settings;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.Host;

// Composition root — see spec/01-architecture.md. This is the only place allowed to call `new`
// on concrete infrastructure types or register services with the DI container.
internal static partial class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // T1-18 (production_audit.md): CLAUDE.md §4 mandates this repo-wide -- no legacy text
        // (.ini/.dfm/.mtm/.MDT/inline C++ literals, Windows-31J/CP932 for Japanese-origin content)
        // may be decoded without it registered first. Must run before any code could attempt such a
        // decode; first statement in Main is the earliest point in this composition root.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Missing-feature sweep (2026-08-31): legacy YONIQ refuses a second launch outright
        // (Mmsstv.cpp's WinMain, silently exits if a TMmsstv window already exists) -- this port
        // never had an equivalent, letting unlimited concurrent instances fight over the same audio
        // devices, radio connection, and settings file. Checked as early as possible, before any of
        // the slow startup work below (host builder, file logging, DI) -- no point paying that cost
        // on a launch that's about to exit.
        if (!TryAcquireSingleInstanceLock(args, Console.Error, out var singleInstanceMutex))
        {
            return;
        }

        var hostBuilder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

        // File provider alongside the console provider CreateApplicationBuilder already registers
        // by default -- see FileLoggerProvider's own doc comment for why. Fixed, predictable path
        // (not per-run-timestamped) so it can always be read directly without hunting for the
        // latest file.
        var logPath = Path.Combine(AppLogPaths.LogDirectory, "app.log");

        // Debug is the default floor while this project is in active development/debugging (see
        // docs/logging-guidelines.md) -- deliberately verbose, not the intended shipped default.
        // Overridable via `--log-level <Level>` (any Microsoft.Extensions.Logging.LogLevel name,
        // e.g. Information/Warning/Error) so verbosity is a launch-argument change, not a rebuild,
        // once this flips to Information at the first real release tag.
        var minimumLevel = LogLevel.Debug;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "--log-level")
            {
                continue;
            }

            // Enum.TryParse alone accepts out-of-range numeric strings too (e.g. "99" -> (LogLevel)99,
            // which SetMinimumLevel then treats as "louder than Critical" -- silently disabling every
            // log). IsDefined guards that. A value that fails to parse at all must not be swallowed
            // silently either -- no logger exists yet at this point, so Console.Error is the only
            // way this is ever visible.
            if (Enum.TryParse<LogLevel>(args[i + 1], ignoreCase: true, out var parsedLevel) && Enum.IsDefined(parsedLevel))
            {
                minimumLevel = parsedLevel;
            }
            else
            {
                Console.Error.WriteLine($"Ignoring invalid --log-level value '{args[i + 1]}'; using {minimumLevel}. Valid values: {string.Join(", ", Enum.GetNames<LogLevel>())}.");
            }

            break;
        }

        hostBuilder.Logging.SetMinimumLevel(minimumLevel);

        // An unwritable log directory (e.g. a read-only profile, a permissions issue) must not
        // crash the app before a single log line exists -- fall back to console-only in that case,
        // via the console provider CreateApplicationBuilder already registered above.
        //
        // Named (not the previous inline `new FileLoggerProvider(logPath)`) so the SAME instance
        // can also be registered as ILogFileRelocator below and disposed from lifetime.Exit before
        // a restart spawns a new instance (see HandleLifetimeExit's own comment for why).
        FileLoggerProvider? fileLoggerProvider = null;
        try
        {
            fileLoggerProvider = new FileLoggerProvider(logPath);
            hostBuilder.Logging.AddProvider(fileLoggerProvider);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to open log file at '{logPath}': {ex}. Continuing with console logging only.");
        }

        // Physically applies any Config/Database directory move staged via Options > General,
        // before RegisterServices/hostBuilder.Build() -- the one point in the process guaranteed
        // to run before any SqliteConnection or ISettingsStore could ever have been opened. Moving
        // either file later would race a live connection (SQLite's own connection pooling keeps a
        // native handle open past SqliteConnection.Dispose() -- see AppLocationOverrides' own doc
        // comment). No logger exists yet -- failures go to Console.Error, same as every other
        // pre-host startup step in this file.
        ApplyPendingRelocations();

        // Tier C audit finding: extracted from Main into its own testable method (previously all
        // ~150 lines of pure `IServiceCollection` registration lived inline in Main, structurally
        // untestable -- SstvCompositionRootTests.cs could only exercise RegisterSstvServices below,
        // 3 of ~35 registrations. A missing/broken registration (or a missing startup step entirely
        // -- exactly how the culture-restore blocker above went unnoticed) surfaced only at first
        // real resolve in a real run. Same extraction shape RegisterSstvServices already established
        // for the SSTV DSP core registrations below.
        RegisterServices(hostBuilder.Services);

        // RegisterServices above already registered NoneLogFileRelocator as the ILogFileRelocator
        // default (so SstvCompositionRootTests, which calls RegisterServices alone, always has one
        // to resolve); this overwrites it with the real provider, last-registration-wins, same
        // substitution convention that test documents for this DI graph elsewhere. Must come AFTER
        // RegisterServices, not before -- registering it earlier would get shadowed instead.
        if (fileLoggerProvider is not null)
        {
            hostBuilder.Services.AddSingleton<ILogFileRelocator>(fileLoggerProvider);
        }

        // ValidateOnBuild/ValidateScopes default to on only in the Development environment, which a
        // GUI launch never is -- without this, a missing registration surfaces as an unguarded
        // crash from SetupWithLifetime -> MainViewModel resolution instead of the Console.Error +
        // rethrow immediately below. Zero AddScoped calls exist in this graph today, so
        // ValidateScopes is a pure future guard, not a behavior change. Note: ValidateOnBuild only
        // validates registered descriptors -- it can't see a missing registration reached solely
        // via a factory lambda's own GetRequiredService call, and MS.DI doesn't cache that kind of
        // failed construction, so it would still crash (and repeat) on first real resolve.
        hostBuilder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        }));

        // A DI-graph error (a missing registration, a bad factory lambda) here is otherwise an
        // unlogged crash before the window ever appears -- there is no logger to report through
        // yet at this exact point (the host that would provide one failed to build), so this one
        // site genuinely has to fall back to Console.Error rather than the file log.
        Microsoft.Extensions.Hosting.IHost host;
        try
        {
            host = hostBuilder.Build();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CRITICAL] Host failed to build: {ex}");
            throw;
        }

        App.Services = host.Services;

        var logger = host.Services.GetRequiredService<ILogger<App>>();
        Log.Starting(logger, logPath, minimumLevel);

        // Process-wide safety net: an exception that would otherwise crash the process with no
        // trace at all now at least gets one line in the log file first. These fire from arbitrary
        // threads, so they're wired as soon as a logger exists, before any further service is
        // resolved or the UI starts.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.UnhandledException(logger, e.ExceptionObject, e.IsTerminating);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.UnobservedTaskException(logger, e.Exception);
            e.SetObserved();
        };

        // Eagerly resolved so its constructor's ISstvDecoder event subscriptions actually happen --
        // see the registration comment above for why this can't just rely on being a constructor
        // dependency somewhere else the way ReceivedImageBuffer's own subscription does. Guarded
        // (previously wasn't) -- a failure here used to be an unlogged crash before the window ever
        // appeared; logged and continued, matching every other startup step's own defensive shape.
        try
        {
            host.Services.GetRequiredService<ReceiveHistoryRecorder>();
        }
        catch (Exception ex)
        {
            Log.ReceiveHistoryRecorderResolveFailed(logger, ex);
        }

        // ui_transition_plan.md step 12: same "eagerly resolved so its constructor's event
        // subscriptions actually happen" reasoning as ReceiveHistoryRecorder immediately above.
        try
        {
            host.Services.GetRequiredService<RxAudioAutoSaver>();
        }
        catch (Exception ex)
        {
            Log.RxAudioAutoSaverResolveFailed(logger, ex);
        }

        // fsk_cwid.md §5 A2: same "eagerly resolved so its constructor's event subscriptions
        // actually happen" reasoning as the two calls immediately above.
        try
        {
            host.Services.GetRequiredService<RxStationIdAttacher>();
        }
        catch (Exception ex)
        {
            Log.RxStationIdAttacherResolveFailed(logger, ex);
        }

        // Tier C audit finding (blocker): restoring a persisted non-English culture was never
        // actually implemented, despite two separate doc comments (LocalizationSettings.cs's own,
        // JsonLocalizationService.cs's own) explicitly assigning this exact step to the composition
        // root. A user's chosen language reverted to English on every relaunch, while the Options
        // dialog kept SHOWING the persisted (non-English) choice -- it reads the raw settings
        // section directly, not the live service's CurrentCulture -- so the dialog visibly
        // disagreed with what was actually on screen. Must run before SetupWithLifetime below: that
        // resolves MainViewModel and starts evaluating XAML `{loc:Translate}` bindings, so restoring
        // the culture any later would be a no-op for everything already on screen. Same defensive
        // shape as every other startup step here -- a bad/unrecognized persisted culture code (or
        // any other failure) must not prevent the UI from starting, just leave it in English.
        try
        {
            var localizationSettings = host.Services.GetRequiredService<ISettingsStore>().LoadAsync().GetAwaiter().GetResult()
                .GetSection(LocalizationSettings.SectionKey, LocalizationSettingsJsonContext.Default.LocalizationSettings);
            if (localizationSettings?.CultureCode is { } cultureCode)
            {
                host.Services.GetRequiredService<ILocalizationService>().SetCultureAsync(CultureInfo.GetCultureInfo(cultureCode)).GetAwaiter().GetResult();
                Log.CultureRestored(logger, cultureCode);
            }
        }
        catch (Exception ex)
        {
            Log.CultureRestoreFailed(logger, ex);
        }

        // Phase 1 dark mode / Phase 2 font-size presets: same "must run before SetupWithLifetime"
        // reasoning as the culture-restore block immediately above -- SetupWithLifetime constructs
        // the actual App instance and starts rendering MainWindow, so applying either later would
        // flash the wrong appearance first. Neither can be set directly on App here because App
        // doesn't exist yet at this point in startup -- both write to plain static fields instead
        // (App.StartupThemeVariant / App.StartupFontScale, see their own doc comments), which
        // OnFrameworkInitializationCompleted reads once the instance exists, just before MainWindow
        // is constructed. A missing/unset/corrupt setting leaves the corresponding static field
        // null, which keeps App.axaml's/App.Initialize()'s own hardcoded defaults (Light /
        // AppFontScale.Normal) -- same defensive shape as every other startup step here.
        //
        // Round-1-plan-review fix (Phase 2): ONE shared settings load for both Theme and FontScale,
        // not a second independent ISettingsStore.LoadAsync() call -- each field then gets its own
        // independent try/catch apply block below, matching the existing culture/theme separation,
        // without re-reading the whole settings document a second time.
        AppearanceSettings? appearanceSettings = null;
        try
        {
            appearanceSettings = host.Services.GetRequiredService<ISettingsStore>().LoadAsync().GetAwaiter().GetResult()
                .GetSection(AppearanceSettings.SectionKey, AppearanceSettingsJsonContext.Default.AppearanceSettings);
        }
        catch (Exception ex)
        {
            Log.AppearanceSettingsLoadFailed(logger, ex);
        }

        if (appearanceSettings?.Theme is { } theme)
        {
            try
            {
                App.StartupThemeVariant = theme switch
                {
                    AppTheme.Light => ThemeVariant.Light,
                    AppTheme.Dark => ThemeVariant.Dark,
                    AppTheme.System => ThemeVariant.Default,
                    _ => null,
                };
                Log.AppThemeRestored(logger, theme.ToString());
            }
            catch (Exception ex)
            {
                Log.AppThemeRestoreFailed(logger, ex);
            }
        }

        if (appearanceSettings?.FontScale is { } fontScale)
        {
            try
            {
                App.StartupFontScale = fontScale;
                Log.FontScaleRestored(logger, fontScale.ToString());
            }
            catch (Exception ex)
            {
                Log.FontScaleRestoreFailed(logger, ex);
            }
        }

        // Auto-connect from persisted settings at startup -- the radio status strip (step 9) is a
        // fixed, read-only label, not an interactive "Connect" button (Phase 3 plan decision), so
        // this is the only place the initial connection attempt happens. A missing/unset radio
        // section resolves to NoneConnectionSpec (a first-class, always-valid state,
        // spec/02-radio-layer.md) -- caught here defensively so a real connect failure can never
        // prevent the UI itself from starting; IRadioController's own reconnect/backoff machinery
        // takes over from here via its ConnectionEvents/StateChanges streams. Logged, not silently
        // swallowed, now that a logger exists.
        // T0-3: backgrounded, not blocking -- these used to run with .GetAwaiter().GetResult()
        // before SetupWithLifetime/lifetime.Start(args) below, so a powered-off rigctld host or a
        // stalled audio server hung the app with no window on screen at all. Task.Run here lets
        // lifetime.Start(args) run immediately; both calls keep their existing try/catch/log
        // semantics, just inside the background delegate.
        _ = Task.Run(async () =>
        {
            try
            {
                // Code-review correction: same observability-only reasoning as StartReceivingAsync's
                // own WaitAsync below -- HamlibRadioProtocol's own doc comment states ct is honored
                // only at the semaphore boundary, and rig_open, once started, is never cancelled or
                // abandoned. Without this, a wedged rig now hangs this backgrounded task silently
                // forever with zero log trace, instead of eventually logging via
                // Log.RadioAutoConnectFailed below.
                await host.Services.GetRequiredService<IRadioSessionService>().ConnectUsingSettingsAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.RadioAutoConnectFailed(logger, ex);
            }
        });

        // Same reasoning as the radio auto-connect above: Phase 3 has no "Start Receiving" button
        // anywhere in the UI (the walking skeleton's own demo target is a session that's simply
        // listening once the app is up), so this is the only place capture starts. A missing/unset
        // audio-device section throws InvalidOperationException from StartReceivingAsync -- caught
        // here the same way, so a machine with no configured capture device still gets a working UI
        // (waterfall/RX image just stay empty) instead of failing to start at all.
        _ = Task.Run(async () =>
        {
            try
            {
                // T0-3, auditor plan-review correction: StartReceivingAsync's own gate acquisition is
                // bounded (_rxTransitionGate.WaitAsync(_cleanupTimeout, ct)), but the native
                // capture-device open underneath it is NOT -- MiniAudioEngine's own doc comment states
                // "once the native open below is actually running, there is no way to cancel it
                // partway through." Backgrounding alone turns a visible pre-window hang into a SILENT
                // forever-hang with zero log trace (the catch below is never reached). This WaitAsync
                // is observability-only: it does NOT cancel the native open and does NOT release
                // _rxTransitionGate (whatever holds the gate open stays held regardless) -- it only
                // guarantees a log line fires so a wedged startup is diagnosable instead of invisible.
                // A genuinely wedged native open still leaves the app unable to Start/Stop RX later
                // (every such call times out against the still-held gate) until restart -- a real,
                // separate, pre-existing gap this fix does not close.
                await host.Services.GetRequiredService<ISstvSessionService>().StartReceivingAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.StartReceivingFailed(logger, ex);
            }
        });

        var lifetime = new ClassicDesktopStyleApplicationLifetime { Args = args };
        BuildAvaloniaApp().SetupWithLifetime(lifetime);
        Log.AvaloniaLifetimeStarted(logger);

        // IAudioEngine is IAsyncDisposable-only (no IDisposable) -- the built-in ServiceProvider's
        // synchronous Dispose() throws for a singleton shaped that way ("type only implements
        // IAsyncDisposable"), so this must go through DisposeAsync, not host.Dispose(). IHost
        // itself only declares IDisposable; the concrete Host type Microsoft.Extensions.Hosting
        // returns also implements IAsyncDisposable (confirmed by test-compiling the cast below,
        // not assumed), which disposes every singleton actually resolved during this run that
        // implements IAsyncDisposable/IDisposable -- nothing extra to do here if IAudioEngine was
        // never resolved at all.
        //
        // Round-1-engine-review fix: this used to call DisposeAsync unconditionally and
        // synchronously with no bound and no try/catch. MiniAudioEngine.DisposeAsync is not fully
        // bounded (MiniAudioCaptureSession.Dispose's own _drainThread.Join() has no timeout at
        // all if a SamplesCaptured subscriber never returns -- that class's own doc comment
        // documents this as a known hazard), so an unbounded wait here could hang the whole
        // shutdown sequence; and DisposeAsync can throw (e.g. MiniAudioContext.Release() on a
        // refcount imbalance). Bounded and logged (not silently swallowed) here -- a teardown
        // failure at process exit must not prevent the process from actually exiting, but should
        // still be visible in the log file for a later "why didn't X clean up" investigation.
        //
        // Round-2-engine-review fix: Task.WhenAny's own result never rethrows the winning task's
        // fault -- a faulted DisposeAsync used to be silently discarded by WhenAny itself, never
        // even reaching the try/catch below despite this handler's own comment implying otherwise.
        // If disposeTask is the one that completed (as opposed to the 10s Delay winning instead),
        // re-observing it via GetAwaiter().GetResult() is a no-op on success and rethrows -- into
        // the catch below -- on fault, actually giving the try/catch something to do.
        //
        // Extracted into HandleLifetimeExit (mirroring RegisterServices's own extraction) so the
        // dispose-then-ClearAllPools-then-conditional-restart-spawn ordering this feature adds is
        // directly unit-testable, not a documented manual step.
        var applicationRestarter = host.Services.GetRequiredService<IApplicationRestarter>();
        lifetime.Exit += (_, _) => HandleLifetimeExit(logger, (IAsyncDisposable)host, applicationRestarter, fileLoggerProvider, singleInstanceMutex: singleInstanceMutex);

        try
        {
            lifetime.Start(args);
        }
        catch (Exception ex)
        {
            Log.LifetimeStartThrew(logger, ex);
            throw;
        }

        // Defensive: keeps the single-instance mutex reachable for the JIT's own liveness analysis
        // through the entire blocking lifetime.Start(args) call above -- without a later use, an
        // aggressive optimizer could in principle treat the local as dead and let it finalize early,
        // releasing the lock while the app is still genuinely running.
        GC.KeepAlive(singleInstanceMutex);
    }

    /// <summary>Runs on the <c>ClassicDesktopStyleApplicationLifetime.Exit</c> event -- extracted
    /// from <c>Main</c> so the dispose-then-<c>ClearAllPools</c>-then-conditional-restart-spawn
    /// ordering is directly unit-testable, mirroring <see cref="RegisterServices"/>'s own
    /// extraction.
    ///
    /// <paramref name="logger"/> stays safe to log through even after <paramref name="host"/>'s
    /// own <c>DisposeAsync</c> completes: the file provider is registered as a pre-built instance
    /// (<c>hostBuilder.Logging.AddProvider(fileLoggerProvider)</c> in <c>Main</c>), which neither
    /// the DI container (it doesn't dispose instances it didn't create) nor
    /// <c>ILoggerFactory</c> (DI-supplied providers are registered <c>dispose: false</c>) ever
    /// disposes on the host's behalf -- the existing <see cref="Log.TeardownThrew"/>/
    /// <see cref="Log.TeardownTimedOut"/> calls below already relied on this before this feature
    /// existed. <paramref name="fileLoggerProvider"/> is disposed explicitly, deliberately, right
    /// before a restart spawn -- from that point on, logging through <paramref name="logger"/>
    /// still reaches the console provider (unaffected by disposing just this one provider
    /// instance) but no longer the file, an accepted narrow gap for this last shutdown step
    /// alone. <paramref name="singleInstanceMutex"/> (single-instance code-review finding) is
    /// disposed right before the restart spawn, for the same "this process is still alive at this
    /// point" reason -- see that disposal's own call-site comment for the full reasoning.</summary>
    internal static void HandleLifetimeExit(ILogger logger, IAsyncDisposable host, IApplicationRestarter restarter, FileLoggerProvider? fileLoggerProvider, TimeSpan? disposeTimeout = null, Mutex? singleInstanceMutex = null)
    {
        // disposeTimeout is a testability seam only (test-suite fixes phase 1, item 5) -- the real
        // call site never passes it, so this is a no-op default-preserving parameter, not a behavior
        // change. It exists because HandleLifetimeExitTests previously had no way to bound how long
        // a test waits on this method's own 10s timeout, and no way to reproduce the UI-thread-
        // capture condition that makes an unclean shutdown genuinely dangerous (the disposeTask's
        // continuation posting back to a blocked SynchronizationContext) -- see that test file's own
        // new test for the real reproduction.
        Log.ShuttingDown(logger);
        var disposedCleanly = true;
        try
        {
            // T0-5: Task.Run, not a direct host.DisposeAsync().AsTask() -- HandleLifetimeExit runs on
            // the UI thread (Avalonia raises lifetime.Exit there), and Task.Run does not flow the
            // ambient SynchronizationContext into its delegate, so every await inside the whole
            // DisposeAsync chain resolves on a thread-pool thread instead of posting a continuation
            // back to this (blocked) UI thread -- which is what turned "up to a 10s stall" into "a
            // guaranteed 10s stall" whenever any await anywhere in that chain lacked
            // ConfigureAwait(false).
            var disposeTask = Task.Run(() => host.DisposeAsync().AsTask());
            var completed = Task.WhenAny(disposeTask, Task.Delay(disposeTimeout ?? TimeSpan.FromSeconds(10))).GetAwaiter().GetResult();
            if (completed == disposeTask)
            {
                disposeTask.GetAwaiter().GetResult();
            }
            else
            {
                disposedCleanly = false;
                Log.TeardownTimedOut(logger);
            }
        }
        catch (Exception ex)
        {
            disposedCleanly = false;
            Log.TeardownThrew(logger, ex);
        }

        // Round-3 finding: DisposeAsync above does NOT release SQLite's pooled `history.db`
        // handle -- neither Sqlite store implements IDisposable, so the pool otherwise only
        // empties at real process exit, strictly after a restarted child process has already
        // tried (and possibly failed, on Windows) to move the file. Safe to call unconditionally,
        // restart or not -- a no-op if no pool exists.
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        catch (Exception ex)
        {
            Log.ClearPoolsThrew(logger, ex);
        }

        if (!restarter.RestartRequested)
        {
            return;
        }

        if (!disposedCleanly)
        {
            // Audio/CAT may still be held by this process -- the restarted instance may come up
            // with no radio/no audio this one time. Still restart: the user explicitly asked for
            // one, and there is no live UI left at this point to ask again.
            Log.RestartingAfterIncompleteTeardown(logger);
        }

        // Code-review round-1 finding: logged BEFORE disposing fileLoggerProvider below, so this
        // line is the last one guaranteed to reach app.log itself -- its absence from a prior
        // run's log (paired with no matching "Scanline Studio starting" from a new process) is
        // the only file-based way to diagnose a failed spawn, since a GUI launch has no attached
        // console for Log.RestartSpawnFailed/the Console.Error fallback below to actually reach.
        Log.RestartSpawning(logger);
        fileLoggerProvider?.Dispose();

        // Single-instance code-review finding: this process is still ALIVE here (lifetime.Exit
        // fired, but Main hasn't returned yet, so the mutex is still held) -- StartNewInstance
        // below re-launches without --allow-multiple-instances (ApplicationRestarter's own
        // Environment.GetCommandLineArgs() has no way to know this run's own bypass reasoning), so
        // the child races this process's own real exit for the same named lock. Released here,
        // deterministically, before the spawn -- this process is the ONLY holder, so disposing it
        // destroys the named object immediately, not just marks it for eventual release.
        singleInstanceMutex?.Dispose();

        if (!restarter.StartNewInstance())
        {
            Log.RestartSpawnFailed(logger);
            Console.Error.WriteLine("Failed to spawn a new Scanline Studio instance for restart; the application will need to be relaunched manually.");
        }
    }

    /// <summary>Missing-feature sweep (2026-08-31): legacy YONIQ refuses a second launch outright
    /// (<c>Mmsstv.cpp</c>'s <c>WinMain</c>, silently exits if a <c>TMmsstv</c> window already
    /// exists) -- this port never had an equivalent, letting unlimited concurrent instances fight
    /// over the same audio devices, radio connection, and settings file.
    ///
    /// Returns <see langword="true"/> (proceed) with <paramref name="mutex"/> set to the newly
    /// acquired, held-for-the-process's-whole-lifetime lock, or <see langword="false"/> (the
    /// caller must exit immediately, without starting the host) if another instance already holds
    /// it. <c>--allow-multiple-instances</c> bypasses the check entirely (this port's own
    /// equivalent of legacy's own <c>-Z</c> flag -- same escape hatch, useful for manual
    /// multi-instance testing, clearer name -- an exact-arg match, not legacy's own whole-command-
    /// line <c>strstr</c>, a strictly narrower/more predictable match) -- <paramref name="mutex"/>
    /// is <see langword="null"/> in that case, matching the "nothing to hold, nothing to release"
    /// reality. Legacy also skips its own refusal when a "TAppBuilder" (IDE-hosted design-time)
    /// window is present -- deliberately not ported, this port has no equivalent design-time host.
    ///
    /// Code-review finding: writes to <paramref name="errorWriter"/> on rejection, but on the
    /// dominant real launch path (a double-click, no attached console) that message reaches
    /// nowhere a user can see -- checked BEFORE the logger exists, so it doesn't reach
    /// <c>app.log</c> either. The real, narrower gain over legacy's own silent no-op: a caller who
    /// DOES have a console (a terminal launch, a test) sees a clear message instead of nothing;
    /// the GUI-double-click case is not meaningfully improved. Left as Console.Error anyway --
    /// moving the check later so a rejected launch could log to <c>app.log</c> would mean touching
    /// the shared, fixed-path log file from an instance that's about to exit, a worse tradeoff.
    ///
    /// Plain, unprefixed mutex name -- code-review correction: NOT because <c>Global\</c>/<c>Local\</c>
    /// prefixes are unsupported on Unix (.NET's own Unix named-mutex implementation does recognize
    /// them) but because unprefixed defaults to SESSION scope on both platforms, the correct
    /// default for a per-login-session desktop app (not machine-wide, which <c>Global\</c> would
    /// mean). <paramref name="mutex"/>'s own creation is guarded, not left to throw straight out of
    /// <c>Main</c> before any window or logger exists -- the same failure class this file's own
    /// `hostBuilder.Build()`/hosted-services startup are already hardened against (see those call
    /// sites' own comments); a single-instance nicety must fail OPEN, not crash the app entirely,
    /// if creating a named OS object is itself unavailable (a hostile/read-only temp directory, a
    /// non-mutex object squatting the name). <c>initiallyOwned: false</c> -- only <c>createdNew</c>
    /// is ever consulted, nothing ever <c>Wait</c>s on this handle, so claiming ownership would add
    /// abandoned-mutex/thread-affinity semantics for no benefit; this is purely a name-existence
    /// token. The real call site never explicitly releases the returned mutex on the normal exit
    /// path -- held for the process's entire lifetime (kept alive by
    /// <see cref="GC.KeepAlive(object?)"/> past the blocking <c>lifetime.Start(args)</c> call) and
    /// released automatically by the OS when the process exits -- EXCEPT the restart path, which
    /// disposes it explicitly before spawning a replacement process (see
    /// <see cref="HandleLifetimeExit"/>'s own comment for why that one path can't just wait for
    /// natural process exit). Testable via <paramref name="mutexName"/> -- the real call site
    /// always uses the default, a fixed process-wide name; tests pass their own unique name so they
    /// never collide with a genuinely running instance or with each other.</summary>
    internal static bool TryAcquireSingleInstanceLock(string[] args, TextWriter errorWriter, out Mutex? mutex, string mutexName = "ScanlineStudio.SingleInstance")
    {
        if (args.Contains("--allow-multiple-instances"))
        {
            mutex = null;
            return true;
        }

        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name: mutexName, out createdNew);
        }
        catch (Exception ex)
        {
            errorWriter.WriteLine($"Single-instance check unavailable ({ex.Message}); continuing without it.");
            mutex = null;
            return true;
        }

        if (createdNew)
        {
            return true;
        }

        errorWriter.WriteLine("Scanline Studio is already running. Pass --allow-multiple-instances to override.");
        mutex.Dispose();
        mutex = null;
        return false;
    }

    /// <summary>See the call site's own comment for why this must run before
    /// <see cref="RegisterServices"/>/<c>hostBuilder.Build()</c>. Testable via
    /// <paramref name="overridesFilePath"/> -- the real call site always passes <c>null</c> (the
    /// real fixed overrides path).</summary>
    internal static void ApplyPendingRelocations(string? overridesFilePath = null)
    {
        var overrides = AppLocationOverrides.LoadForBootstrap(overridesFilePath);
        var originalConfigDirectory = overrides.ConfigDirectory ?? AppConfigPaths.GetConfigDirectory(overridesFilePath);
        var originalDatabaseDirectory = overrides.DatabaseDirectory ?? AppDatabasePaths.GetDatabaseDirectory(overridesFilePath);

        var configResult = TryApplyPendingMove(
            currentDirectory: originalConfigDirectory,
            pendingDirectory: overrides.PendingConfigDirectory,
            fileName: "settings.json");

        var databaseResult = TryApplyPendingMove(
            currentDirectory: originalDatabaseDirectory,
            pendingDirectory: overrides.PendingDatabaseDirectory,
            fileName: "history.db",
            extraSidecarFileNames: ["history.db-journal"]);

        if (configResult is null && databaseResult is null)
        {
            return;
        }

        var updated = overrides;
        if (configResult is { } config)
        {
            updated = updated with { ConfigDirectory = config.AppliedDirectory, PendingConfigDirectory = config.StillPendingDirectory };
        }

        if (databaseResult is { } database)
        {
            updated = updated with { DatabaseDirectory = database.AppliedDirectory, PendingDatabaseDirectory = database.StillPendingDirectory };
        }

        try
        {
            AppLocationOverrides.SaveAsync(updated, overridesFilePath).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to persist location-overrides.json after applying a pending relocation: {ex}");

            // Code-review round-1 finding: the physical move(s) above may have already succeeded
            // even though the override record describing them didn't save -- roll each one back so
            // the file and the (unsaved, still-old) override record stay in agreement. Otherwise
            // settings.json/history.db end up stranded at the new location while every future
            // launch keeps looking for them at the old one (silent "my settings reset" / "my
            // logbook is empty" reports).
            if (configResult is { } configToRollBack)
            {
                RollBackAppliedMove(configToRollBack.AppliedDirectory, originalConfigDirectory, "settings.json", null);
            }

            if (databaseResult is { } databaseToRollBack)
            {
                RollBackAppliedMove(databaseToRollBack.AppliedDirectory, originalDatabaseDirectory, "history.db", ["history.db-journal"]);
            }
        }
    }

    /// <summary>Reverses a move <see cref="TryApplyPendingMove"/> already performed, when the
    /// override save describing it subsequently failed -- a no-op if nothing actually moved (the
    /// applied directory already equals the original one, e.g. the pending-equals-current case).
    /// Best-effort: a failed rollback is logged, not thrown, since the caller is already inside a
    /// failure-handling path with no further recovery available.</summary>
    private static void RollBackAppliedMove(string appliedDirectory, string originalDirectory, string fileName, string[]? extraSidecarFileNames)
    {
        // Code-review round-2 finding: this whole body used to run unguarded -- DirectoryPathComparer.AreEqual
        // (via Path.GetFullPath) throws ArgumentException for an empty/embedded-null directory
        // string, reachable from a hand-edited/corrupt location-overrides.json. That would
        // reproduce the exact round-1 blocker (an unhandled exception straight out of Main, before
        // any window or logger exists) from inside what's supposed to be this feature's own
        // failure-recovery path. This method's own doc comment already promises "logged, not
        // thrown" -- now actually true for every failure mode, not just a missing destination.
        try
        {
            if (DirectoryPathComparer.AreEqual(appliedDirectory, originalDirectory))
            {
                return;
            }

            var fileNames = extraSidecarFileNames is null ? [fileName] : new[] { fileName }.Concat(extraSidecarFileNames).ToArray();
            if (!TryMoveAllOrRollBack(appliedDirectory, originalDirectory, fileNames))
            {
                Console.Error.WriteLine($"Failed to roll back '{fileName}' from '{appliedDirectory}' to '{originalDirectory}' after the settings save failed. " +
                    $"The file may now be at '{appliedDirectory}' while location-overrides.json still points at '{originalDirectory}'.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to roll back '{fileName}' from '{appliedDirectory}' to '{originalDirectory}' after the settings save failed: {ex}. " +
                $"The file may now be at '{appliedDirectory}' while location-overrides.json still points at '{originalDirectory}'.");
        }
    }

    /// <summary>Returns <c>null</c> when there is no pending target to act on (no override update
    /// needed at all). Otherwise returns the directory the file actually ends up in
    /// (<c>AppliedDirectory</c>) and, if the move didn't fully succeed, the same pending target to
    /// retry next launch (<c>StillPendingDirectory</c>, <c>null</c> on success).</summary>
    private static (string AppliedDirectory, string? StillPendingDirectory)? TryApplyPendingMove(
        string currentDirectory, string? pendingDirectory, string fileName, string[]? extraSidecarFileNames = null)
    {
        if (pendingDirectory is null)
        {
            return null;
        }

        // Code-review round-1 finding (blocker): everything below used to run unguarded, so an
        // unusable pending target (a removable/network drive gone missing, a permission revoked,
        // a parent directory deleted, or simply a garbage path from a hand-edited
        // location-overrides.json) threw straight out of Main before any window or logger
        // existed -- this method's own contract ("must never brick startup") demands the opposite:
        // fall back to the old, still-valid directory and retry next launch, exactly like every
        // named failure path below already does.
        try
        {
            if (DirectoryPathComparer.AreEqual(currentDirectory, pendingDirectory))
            {
                // Already there (or the user picked the same directory back) -- clear the pending
                // field as a no-op rather than leaving a stale "will move on restart" showing forever.
                return (currentDirectory, null);
            }

            var fileNames = extraSidecarFileNames is null
                ? [fileName]
                : new[] { fileName }.Concat(extraSidecarFileNames).ToArray();

            Directory.CreateDirectory(pendingDirectory);

            // All-or-nothing conflict check up front -- refuse rather than overwrite/merge an
            // unrelated existing file at the destination.
            foreach (var name in fileNames)
            {
                if (File.Exists(Path.Combine(pendingDirectory, name)))
                {
                    Console.Error.WriteLine($"Cannot move '{name}' to '{pendingDirectory}': a file already exists there. Will retry on next launch.");
                    return (currentDirectory, pendingDirectory);
                }
            }

            // Short bounded retry -- even past the conflict check above, this is real cross-process/
            // antivirus/filesystem-indexer I/O; a transient lock shouldn't need a whole extra restart
            // to clear.
            const int maxAttempts = 5;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (TryMoveAllOrRollBack(currentDirectory, pendingDirectory, fileNames))
                {
                    return (pendingDirectory, null);
                }

                if (attempt < maxAttempts)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(1));
                }
            }

            Console.Error.WriteLine($"Failed to move '{fileName}' to '{pendingDirectory}' after {maxAttempts} attempts. Will retry on next launch.");
            return (currentDirectory, pendingDirectory);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to apply the pending move of '{fileName}' to '{pendingDirectory}': {ex}. Will retry on next launch.");
            return (currentDirectory, pendingDirectory);
        }
    }

    /// <summary>Moves every named file that still exists at <paramref name="sourceDirectory"/>
    /// into <paramref name="destinationDirectory"/>, rolling back whatever already moved this
    /// attempt if any individual move fails partway -- these are independent files (e.g.
    /// <c>history.db</c> + its <c>-journal</c> sidecar) with no atomic multi-file move primitive,
    /// so a failed attempt must leave them exactly where they started rather than half-migrated. A
    /// file already missing from the source (moved by a previous attempt, or never existed -- a
    /// fresh install) is skipped, not an error -- makes repeated attempts/launches idempotent.</summary>
    private static bool TryMoveAllOrRollBack(string sourceDirectory, string destinationDirectory, IReadOnlyList<string> fileNames)
    {
        var moved = new List<(string Source, string Destination)>();
        foreach (var name in fileNames)
        {
            var source = Path.Combine(sourceDirectory, name);
            if (!File.Exists(source))
            {
                continue;
            }

            var destination = Path.Combine(destinationDirectory, name);
            try
            {
                File.Move(source, destination);
                moved.Add((source, destination));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                foreach (var (rollbackSource, rollbackDestination) in moved)
                {
                    try
                    {
                        if (File.Exists(rollbackDestination))
                        {
                            File.Move(rollbackDestination, rollbackSource);
                        }
                    }
                    catch (Exception rollbackEx) when (rollbackEx is IOException or UnauthorizedAccessException)
                    {
                    }
                }

                return false;
            }
        }

        return true;
    }

    internal static void RegisterServices(IServiceCollection services)
    {
        // Restart-required-settings backlog item 3 (2026-08-27): registered as the concrete type,
        // not directly as ISettingsStore, then FORWARDED to both interfaces it implements
        // (ISettingsStore, ISettingsFileRelocator) -- round-3 plan-review finding. A naive
        // `AddSingleton<ISettingsStore>(...)` plus a separate `AddSingleton<ISettingsFileRelocator>(sp =>
        // (JsonSettingsStore)sp.GetRequiredService<ISettingsStore>())` cast would throw the moment any
        // test or alternate registration substitutes ISettingsStore with something else -- forwarding
        // through the concrete singleton instead guarantees both interfaces always resolve to the
        // SAME instance (one lock, one mutable path field) regardless of what else is registered.
        services.AddSingleton(sp => new JsonSettingsStore(sp.GetRequiredService<ILogger<JsonSettingsStore>>()));
        services.AddSingleton<ISettingsStore>(sp => sp.GetRequiredService<JsonSettingsStore>());
        services.AddSingleton<ISettingsFileRelocator>(sp => sp.GetRequiredService<JsonSettingsStore>());

        // Configurations-preset backlog, Phase 2 (2026-08-28): a genuinely separate store, own fixed
        // (non-relocating) location and own lock -- see ConfigurationPresetStore's own doc comment
        // for why this is not folded into JsonSettingsStore above.
        services.AddSingleton<IConfigurationPresetStore>(sp => new ConfigurationPresetStore(sp.GetRequiredService<ILogger<ConfigurationPresetStore>>()));

        // First IHttpClientFactory consumer in this codebase (QrzLogbookUploader) -- no prior
        // registration to match, this is the standard AddHttpClient() entry point.
        services.AddHttpClient();

        // Singleton, not transient: resolved exactly once at startup (App.axaml.cs) as the app's
        // one root view-model, but a second resolve would silently fork
        // TxControlsPaneViewModel.RadioStatus (assigned once in this constructor, from a singleton
        // TxControlsPaneViewModel) into a second, out-of-sync RadioStatusViewModel instance -- the
        // exact forked-persistence failure the RadioStatus wiring is designed to avoid. Only one
        // resolve site exists today, so this was a dormant risk, not an active bug; registering it
        // correctly here closes it off rather than leaving it to bite a future second call site.
        services.AddSingleton<MainViewModel>();

        // Options dialog -- transient so each open/close cycle gets a fresh OptionsSettingsService
        // (re-reads settings.json from disk each time, no stale in-memory copy carried over).
        services.AddTransient<OptionsSettingsService>();
        services.AddTransient<OptionsWindowViewModel>();

        // Macros reference/preview dialog (stub survey Tier 3) -- transient, same reasoning: a
        // fresh instance re-reads OperatorSettings (Name/Grid/Callsign) each open.
        services.AddTransient<MacrosReferenceWindowViewModel>();

        // Configurations-preset backlog, Phase 4b (cascading-menu redesign, 2026-08-28) -- transient,
        // same reasoning: a fresh instance re-lists presets from disk every time the Configurations
        // menu opens. TextPromptWindowViewModel/ConfirmActionDialogViewModel are deliberately NOT
        // registered here -- both take per-invocation constructor args (title/message/prefill), so
        // they're always `new`'d directly by MainWindow.axaml.cs's own Configurations-menu code,
        // matching OptionsWindowView.axaml.cs's own precedent for a parameterized child dialog VM.
        services.AddTransient<ConfigurationsManagerWindowViewModel>();

        // Locale files live alongside the built app -- see ScanlineStudio.Host.csproj's asset-copy item.
        // Always boots into English; restoring a persisted non-English culture happens later in Main
        // (Tier C audit finding -- this comment used to say "a separate, later step, once
        // ScanlineStudio.Settings has a culture section to restore from" and that step never actually
        // landed even after the section did; see the culture-restore block in Main).
        var localeDirectory = Path.Combine(AppContext.BaseDirectory, "assets", "locale");
        services.AddSingleton<ILocalizationService>(sp =>
            new JsonLocalizationService(localeDirectory, sp.GetRequiredService<ILogger<JsonLocalizationService>>()));

        services.AddSingleton<IFilePickerService, FilePickerService>();
        services.AddSingleton<IUrlLauncher, UrlLauncher>();
        services.AddSingleton<IClipboardImageService, ClipboardImageService>();

        // Options > General's Config/Database/Log storage-location rows (see AppLocationOverrides'
        // own doc comment). IApplicationRestarter backs the Config/Database "Restart Now" action --
        // Program.cs's own lifetime.Exit handler resolves it and checks RestartRequested only after
        // its existing teardown completes, see that handler's own comment for why.
        //
        // NoneLogFileRelocator is the default so this registration alone (as
        // SstvCompositionRootTests exercises) always has something to resolve; Main overwrites it
        // with the real FileLoggerProvider instance on success, AFTER this call, last-registration-
        // wins (see that call site's own comment -- registering it before RegisterServices instead
        // would get shadowed).
        services.AddSingleton<ILogFileRelocator, NoneLogFileRelocator>();
        services.AddTransient<IAppLocationsService, AppLocationsService>();
        services.AddSingleton<IApplicationRestarter, ApplicationRestarter>();

        // Fixed-shell pane view-models (spec/09-ui.md) -- singletons, one per app session, resolved
        // automatically by DI straight into MainViewModel's constructor (replaces the former
        // AppDockFactory, which built these same 4 instances by hand). TxImageEditorPaneViewModel is
        // NOT registered here -- it's constructed dynamically per edit session (with runtime-only
        // args: the picked image + target mode), same as before.
        services.AddSingleton<WaterfallPaneViewModel>();
        services.AddSingleton<RxImagePaneViewModel>();
        services.AddSingleton<RxHistoryPaneViewModel>();
        services.AddSingleton<TxControlsPaneViewModel>();
        services.AddSingleton<LogbookPaneViewModel>();
        services.AddSingleton<DecoderTracePaneViewModel>();

        // Piece Engine 6. Registered by type, not an eagerly-constructed instance (unlike
        // ISettingsStore above) -- MiniAudioEngine's constructor initializes the native miniaudio
        // context for real, which must not run at process start on a machine with no audio server.
        // Nothing resolves these from the UI yet (ScanlineStudio.Application has no real source files today,
        // confirmed via this project's own Opus plan-review pass) -- this registration exists so
        // the composition root is ready once something does, not because a consumer exists now.
        // Neither type may be referenced from ScanlineStudio.UI directly per spec/01-architecture.md's
        // layering rule (UI only talks to ScanlineStudio.Application service interfaces); resolving them
        // here, in ScanlineStudio.Host, does not violate that.
        services.AddSingleton<IAudioEngine, MiniAudioEngine>();
        services.AddSingleton<IAudioDeviceEnumerator, MiniAudioDeviceEnumerator>();
        services.AddSingleton<IAudioDeviceMuteQuery, MiniAudioDeviceMuteQuery>();

        // SSTV DSP core -- one decoder/encoder/waterfall per app session (Phase 3 scope: a single
        // concurrent session, matching the single IAudioEngine instance above).
        // Factories, not eagerly-constructed instances: the decoder reads both decoder settings and
        // the process-lifetime DSP sample rate; encoder and waterfall resolve that decoder-owned rate
        // so capture/RX/display/TX cannot silently disagree.
        // Ultracode audit finding #34: RestartableSstvDecoder (not AnalogFmSstvDecoder directly)
        // periodically discards/reconstructs the whole decoder object graph to avoid an int-overflow
        // before its int absolute-index space can overflow -- see that class's own doc comment. Every
        // consumer only ever holds ISstvDecoder, so this swap is fully transparent.
        RegisterSstvServices(services);

        // Image pipeline (step 5) -- ReceivedImageBuffer's constructor takes ISstvDecoder, resolved
        // automatically from the registration above (it subscribes to LineDecoded/DecodeRestarted
        // itself; see that class's own doc comment for why this is layering-legal).
        services.AddSingleton<IImageFileLoader, ImageFileLoader>();
        services.AddSingleton<IReceivedImageBuffer, ReceivedImageBuffer>();
        // Gallery pane's "Export frame" (2026-08-15) -- re-saves an already-received image file to a
        // user-chosen location, optionally re-encoded as JPEG.
        services.AddSingleton<IReceivedFrameExporter, ReceivedFrameExporter>();
        // TX template editor persistence (Phase 5, spec/15-template-designer.md) -- IImageSourceWriter
        // is the missing "write an IImageSource out as a PNG" counterpart to IImageFileLoader;
        // TemplateStore (Application layer) depends on it, IImageFileLoader, and
        // ITransmitImagePreparer, never ImageSharp directly.
        services.AddSingleton<IImageSourceWriter, ImageSourceWriter>();
        services.AddSingleton<ITemplateStore, TemplateStore>();

        // Phase 4 image-tooling UI -- spec/07-image-pipeline.md's "Stock image library"/"RX history"
        // sections. ReceiveHistoryRecorder is resolved once, explicitly, below (nothing else in the
        // DI graph depends on it as a constructor parameter the way ReceivedImageBuffer's ISstvDecoder
        // subscription gets triggered automatically -- its own event subscriptions only happen once
        // something actually asks the container to build one).
        services.AddSingleton<IStockImageLibrary, StockImageLibrary>();
        services.AddSingleton<IReceiveHistoryStore, SqliteReceiveHistoryStore>();
        services.AddSingleton<ReceiveHistoryRecorder>();

        // ui_transition_plan.md step 12 (Auto-save RX audio): eagerly resolved just below (same
        // "resolved once, explicitly" shape as ReceiveHistoryRecorder immediately above) so its
        // constructor's ISstvSessionService/IReceiveHistoryStore event subscriptions start even if
        // nothing else in the DI graph ever asks for it -- but RxHistoryPaneViewModel ALSO depends on
        // it (via IRxAudioAutoSaver, auditor-caught: without a live AudioAttached subscriber, a
        // just-received frame's in-memory entry never picked up its audio path). The interface
        // registration below forwards to this SAME singleton instance, not a second one.
        services.AddSingleton<RxAudioAutoSaver>();
        services.AddSingleton<IRxAudioAutoSaver>(sp => sp.GetRequiredService<RxAudioAutoSaver>());

        // fsk_cwid.md §5 A2: same "eagerly resolved, singleton forwarded through the interface"
        // shape as RxAudioAutoSaver immediately above, for the same reason -- its constructor's
        // ISstvSessionService/IReceiveHistoryStore event subscriptions must start even if nothing
        // else in the DI graph ever asks for it directly. fsk_cwid.md A-P3b: RxHistoryPaneViewModel
        // now ALSO depends on it (via IRxStationIdAttacher, auditor-caught: without a live
        // StationIdAttached subscriber, a just-received frame's decoded callsign/NR-RST never
        // reached the Gallery's in-memory entry) -- same "forwards to this SAME singleton instance"
        // shape RxAudioAutoSaver's own comment describes.
        services.AddSingleton<RxStationIdAttacher>();
        services.AddSingleton<IRxStationIdAttacher>(sp => sp.GetRequiredService<RxStationIdAttacher>());

        // QSO logbook backend (spec/08-logging.md + the accompanying plan file) -- SQLite storage
        // (same history.db file as RX history above), ADIF import/export, ADIF-over-UDP streaming
        // (generalized 2026-08-15 from a GridTracker-only streamer to fan the same WSJT-X
        // LoggedADIF datagram out to any configured destination -- GridTracker, N1MM Logger+,
        // Log4OM, or anything else that speaks the same protocol), and QRZ.com Logbook API upload.
        // No UI wired to ADIF UDP streaming yet this piece -- settings only reachable by
        // hand-editing settings.json until the Options-dialog piece lands.
        services.AddSingleton<ILogbookRepository, SqliteLogbookRepository>();
        services.AddSingleton<IAdifExporter, AdifExporter>();
        services.AddSingleton<IAdifImporter, AdifImporter>();
        services.AddSingleton<IAdifUdpStreamer, AdifUdpStreamer>();

        // QRZ.com Logbook API (upload/push) -- own named HttpClient with an explicit timeout, same
        // rationale as QrzXmlLookup below: a hung QRZ connection must not pin the Logbook pane's
        // upload path on the default 100s (functional-audit finding: QrzLogbookUploader requests
        // this exact name via IHttpClientFactory.CreateClient, so without this registration it got
        // default HttpClient options -- 100s -- instead). Credentials travel in a POST body here,
        // not a GET query string, so (unlike QrzXmlLookup below) the default request-logging
        // handler never logs them in plaintext -- RemoveAllLoggers() is not needed.
        services.AddHttpClient("QrzLogbookApi", c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<IQrzLogbookUploader, QrzLogbookUploader>();

        // QRZ.com XML Callbook lookup (spec/08-logging.md's "QRZ.com lookup" section) -- a
        // DIFFERENT QRZ product from the upload API above (username/password auth, pull/enrich
        // direction, not API-key/push). Its own named HttpClient with an explicit timeout, same
        // rationale as QrzLogbookApi above: a hung QRZ connection must not pin a Receive-tab
        // "Lookup QRZ" button disabled for the default 100s.
        //
        // RemoveAllLoggers() IS needed here, unlike QrzLogbookApi above (real-window-testing-caught
        // gap in an earlier draft): IHttpClientFactory's own built-in LoggingHttpMessageHandler logs
        // the full request URI at Information level by default -- and this API sends credentials as
        // GET query parameters (QRZ's own wire format, not this app's choice), so without this call,
        // a real QRZ password lands in ~/.local/share/ScanlineStudio/logs/app.log in plaintext every
        // time this client is used, regardless of anything QrzCallsignLookup's own Log class does or
        // doesn't log. Confirmed via a real manual test against the live QRZ server: the log line
        // read "GET https://xmldata.qrz.com/xml/current/?username=...&password=<plaintext>&agent=..."
        // before this fix.
        services.AddHttpClient("QrzXmlLookup", c => c.Timeout = TimeSpan.FromSeconds(15)).RemoveAllLoggers();
        services.AddSingleton<IQrzCallsignLookup, QrzCallsignLookup>();

        // TX image editor (spec/07-image-pipeline.md's "TX image editor" section) -- the
        // Crop/Resize/ApplyOverlay pipeline both TxImageEditorPaneViewModel's live preview and
        // TxControlsPaneViewModel's mode-change reflow run against.
        services.AddSingleton<ITransmitImagePreparer, TransmitImagePreparer>();
        services.AddSingleton<IMacroTextResolver, MacroTextResolver>();

        // Radio layer -- all three backends now registered (Settings/Options Piece 3): None,
        // rigctld, and linked Hamlib. RadioController's constructor takes
        // IEnumerable<IRadioProtocolFactory>, resolved automatically from every factory registered
        // here, and requires exactly one CanHandle match per connection attempt -- the real gap
        // this closes is that NEITHER NoneRadioProtocolFactory nor HamlibProtocolFactory was
        // registered before, so the default "none" BackendId (or a user picking Hamlib in Options)
        // would throw InvalidOperationException on connect, silently masked by the bare try/catch
        // below. HamlibProtocolFactory.Create() is safe to call unconditionally even when
        // libhamlib isn't installed on this machine -- HamlibRuntime's constructor catches
        // discovery failure internally (IsAvailable=false) rather than throwing; the throw only
        // happens later, if the user actually selects Hamlib and tries to connect.
        services.AddSingleton<IRadioProtocolFactory, NoneRadioProtocolFactory>();
        services.AddSingleton<IRadioProtocolFactory, RigctldProtocolFactory>();
        services.AddSingleton<IRadioProtocolFactory>(CreateHamlibProtocolFactory);
        services.AddSingleton<IRadioProtocolFactory, FlrigProtocolFactory>();
        // OmniRigProtocolFactory.Create() is safe to register unconditionally on any OS -- it only
        // throws PlatformNotSupportedException lazily, if a user actually selects OmniRig and tries
        // to connect on a non-Windows machine (spec/03-cat-layer.md's OmniRig section).
        services.AddSingleton<IRadioProtocolFactory, OmniRigProtocolFactory>();
        services.AddSingleton<IRadioController, RadioController>();
        // ui_transition_plan.md step 6 (T2-4): narrow adapter for ReceiveHistoryRecorder -- see
        // IRadioStateProvider's own doc comment for why this is a real adapter over IRadioController,
        // not a cast of IRadioSessionService.
        services.AddSingleton<IRadioStateProvider, RadioStateProvider>();

        // flrig's own named HttpClient -- explicit Timeout backstop (see FlrigClientProtocol's own
        // doc comment for why a per-call CancellationToken alone isn't enough: the un-key retry path
        // deliberately calls SetPttAsync with CancellationToken.None). RemoveAllLoggers() here is for
        // hot-path log volume (up to ~16 requests/sec at the default 250ms poll interval, each RPC
        // logged at Information by IHttpClientFactory's own handler) -- NOT credential leakage like
        // QrzXmlLookup above; flrig's URI carries no credentials.
        services.AddHttpClient("Flrig", c => c.Timeout = TimeSpan.FromSeconds(10)).RemoveAllLoggers();

        // Options-dialog-facing Hamlib path/rig-list probing (spec/03-cat-layer.md's "Discovery
        // order", tier 1) -- a separate service from IRadioProtocolFactory above since a probe here
        // must never disturb the real singleton's already-loaded runtime (see
        // HamlibDiscoveryService's own doc comment on why every native-registry call it makes is
        // serialized behind a process-wide gate).
        services.AddSingleton<IHamlibDiscoveryService>(sp => HamlibDiscoveryService.Create(sp.GetRequiredService<ILoggerFactory>()));

        // Options-dialog-facing serial port listing for the Hamlib CAT/PTT port pickers -- same
        // placement reasoning as IHamlibDiscoveryService directly above.
        services.AddSingleton<ISerialPortEnumerator, SerialPortEnumerator>();

        // ScanlineStudio.Application services -- the only things ScanlineStudio.UI is allowed to depend on
        // (spec/01-architecture.md's layering rule); everything above is UI-invisible plumbing.
        services.AddSingleton<IRadioSessionService, RadioSessionService>();
        services.AddSingleton<ISstvSessionService>(CreateSstvSessionService);
        services.AddSingleton<ILogbookSessionService, LogbookSessionService>();

        // Configurations-preset backlog, Phase 3 (2026-08-28) -- orchestrates across both session
        // services above plus the settings/preset stores, so it's registered last among these.
        services.AddSingleton<IConfigurationPresetService, ConfigurationPresetService>();
    }

    /// <summary>Reads the persisted tier-1 Hamlib library-path override (spec/03-cat-layer.md's
    /// "Discovery order") before constructing the real <see cref="HamlibProtocolFactory"/> -- must
    /// happen here, inside the factory delegate, not at <see cref="RegisterServices"/> call time:
    /// <see cref="IServiceCollection"/> has no built <see cref="IServiceProvider"/> yet to resolve
    /// <see cref="ISettingsStore"/> from at that point. Mirrors <see cref="CreateSstvDecoder"/>'s own
    /// "read settings inside the DI factory, log-and-fall-back on a corrupt section" pattern.</summary>
    internal static HamlibProtocolFactory CreateHamlibProtocolFactory(IServiceProvider services)
    {
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var appSettings = services.GetRequiredService<ISettingsStore>().LoadAsync().GetAwaiter().GetResult();

        RadioConnectionSettings radioSettings;
        try
        {
            radioSettings = appSettings.GetSection(RadioConnectionSettings.SectionKey, RadioSettingsJsonContext.Default.RadioConnectionSettings)
                ?? new RadioConnectionSettings();
        }
        catch (JsonException ex)
        {
            Log.SettingsSectionReadFailed(loggerFactory.CreateLogger(nameof(Program)), RadioConnectionSettings.SectionKey, ex);
            radioSettings = new RadioConnectionSettings();
        }

        return HamlibProtocolFactory.Create(radioSettings.HamlibLibraryPath, loggerFactory);
    }

    /// <summary>ui_transition_plan.md step 12 (Auto-save RX audio): constructs the normal DI-resolved
    /// <see cref="SstvSessionService"/>, then applies the PERSISTED auto-save-audio setting to it via
    /// <see cref="ISstvSessionService.SetAutoSaveAudioEnabled"/>/<see cref="ISstvSessionService.SetAudioDirectory"/>
    /// before returning it -- without this, the feature would stay off from app launch even if the
    /// user enabled it in a previous session, until they happened to re-open and re-save Options.
    /// Mirrors <see cref="CreateSstvDecoder"/>'s own "read settings synchronously inside the DI
    /// factory" pattern. Deliberately does NOT give <see cref="SstvSessionService"/> itself an
    /// <see cref="IReceiveHistoryStore"/> constructor dependency -- that would blur the layering the
    /// plan doc's Correlation design depends on (only the not-yet-built <c>RxAudioAutoSaver</c> needs
    /// that reference).</summary>
    internal static SstvSessionService CreateSstvSessionService(IServiceProvider services)
    {
        var service = ActivatorUtilities.CreateInstance<SstvSessionService>(services);

        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        try
        {
            var audioSettings = services.GetRequiredService<IReceiveHistoryStore>().GetAudioSettingsAsync().GetAwaiter().GetResult();
            service.SetAutoSaveAudioEnabled(audioSettings.Enabled);
            service.SetAudioDirectory(audioSettings.Directory);
        }
        catch (Exception ex)
        {
            // Same "log and fall back to defaults (off)" pattern as CreateSstvDecoder's own settings
            // reads -- a corrupt/unreadable settings section must not prevent the whole app from
            // starting, and "auto-save-audio stays off" is a safe, non-destructive fallback.
            Log.SettingsSectionReadFailed(loggerFactory.CreateLogger(nameof(Program)), ReceiveHistorySettings.SectionKey, ex);
        }

        return service;
    }

    internal static void RegisterSstvServices(IServiceCollection services)
    {
        services.AddSingleton<ISstvDecoder>(CreateSstvDecoder);
        services.AddSingleton<ISstvEncoder>(CreateSstvEncoder);
        services.AddSingleton<IWaterfallSource>(CreateWaterfallSource);
        // fsk_cwid.md §8.3: the v1 (and, per §7.1, the only planned) ICwIdDecoder backend -- own
        // code, no license question (deepcw-engine ruled out for bundling; see that section for why).
        services.AddSingleton<ICwIdDecoder, ClassicalCwDecoder>();
    }

    internal static RestartableSstvDecoder CreateSstvDecoder(IServiceProvider services)
    {
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var appSettings = services.GetRequiredService<ISettingsStore>().LoadAsync().GetAwaiter().GetResult();

        // Tier C audit finding (risk): GetSection's own Deserialize call throws JsonException for a
        // wrong-typed value in a hand-edited/version-skewed settings.json (e.g. "SampleRate": "auto")
        // -- unlike JsonSettingsStore.LoadAsync itself (already hardened against a corrupt FILE),
        // this is the third instance in this codebase of an unguarded settings-SECTION read reaching
        // a DI factory with no try/catch. Since this factory backs a singleton, a throw here is not
        // even one-shot: DI does not cache a failed construction, so the NEXT resolve (unguarded,
        // reached via SetupWithLifetime -> MainViewModel below) retries and throws again -- past the
        // one caller (StartReceivingAsync's own try/catch above) that happened to catch it the first
        // time. Same "log and fall back to defaults" pattern already established everywhere else in
        // this file for exactly this failure class.
        AudioDeviceSettings audioSettings;
        try
        {
            audioSettings = appSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings)
                ?? new AudioDeviceSettings();
        }
        catch (JsonException ex)
        {
            Log.SettingsSectionReadFailed(loggerFactory.CreateLogger(nameof(Program)), AudioDeviceSettings.SectionKey, ex);
            audioSettings = new AudioDeviceSettings();
        }

        var sampleRate = SstvSampleRate.NormalizePersisted(audioSettings.SampleRate);

        SstvDecoderSettings decoderSettings;
        try
        {
            decoderSettings = appSettings.GetSection(SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings)
                ?? new SstvDecoderSettings();
        }
        catch (JsonException ex)
        {
            Log.SettingsSectionReadFailed(loggerFactory.CreateLogger(nameof(Program)), SstvDecoderSettings.SectionKey, ex);
            decoderSettings = new SstvDecoderSettings();
        }

        // SenseLevel's distinct out-of-range fallback remains inside AnalogFmSstvDecoder's
        // constructor. Every other absent-or-out-of-range default lives in SstvDecoderSettings.Resolve
        // itself now -- single source of truth also used by the Loopback self-test's own decoder
        // construction, see that method's own doc comment for why a second independent copy here
        // would have been a real drift risk.
        var resolved = decoderSettings.Resolve();
        return new RestartableSstvDecoder(
            afcEnabled: resolved.AfcEnabled,
            syncRestartEnabled: resolved.SyncRestartEnabled,
            autoSyncEnabled: resolved.AutoSyncEnabled,
            autoStopEnabled: resolved.AutoStopEnabled,
            autoSlantEnabled: resolved.AutoSlantEnabled,
            senseLevel: resolved.SenseLevel,
            demodType: resolved.DemodType,
            rxBpfPreset: resolved.RxBpfPreset,
            rxBufferMode: resolved.RxBufferMode,
            pllVcoGain: resolved.PllVcoGain,
            pllLoopOrder: resolved.PllLoopOrder,
            pllLoopCutoffHz: resolved.PllLoopCutoffHz,
            pllOutputOrder: resolved.PllOutputOrder,
            pllOutputCutoffHz: resolved.PllOutputCutoffHz,
            zeroCrossingSmoothingMode: resolved.ZeroCrossingSmoothingMode,
            zeroCrossingOutputOrder: resolved.ZeroCrossingOutputOrder,
            zeroCrossingOutputCutoffHz: resolved.ZeroCrossingOutputCutoffHz,
            zeroCrossingSmoothingFrequencyHz: resolved.ZeroCrossingSmoothingFrequencyHz,
            sampleRate: sampleRate,
            loggerFactory: loggerFactory);
    }

    // Restart-required-settings backlog item 4 (2026-08-27): RestartableSstvEncoder, not the plain
    // AnalogFmSstvEncoder this used to construct directly -- see that class's own doc comment for why
    // it needs no periodic-maintenance machinery, only a live-swappable rate.
    internal static RestartableSstvEncoder CreateSstvEncoder(IServiceProvider services) =>
        new(services.GetRequiredService<ISstvDecoder>().SampleRate);

    internal static WaterfallSource CreateWaterfallSource(IServiceProvider services) =>
        new(services.GetRequiredService<ISstvDecoder>().SampleRate);

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() => App.BuildAvaloniaApp();

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Scanline Studio starting; logging to {LogPath} (minimum level {MinimumLevel})")]
        public static partial void Starting(ILogger logger, string logPath, LogLevel minimumLevel);

        [LoggerMessage(Level = LogLevel.Critical, Message = "Unhandled exception (IsTerminating={IsTerminating}): {ExceptionObject}")]
        public static partial void UnhandledException(ILogger logger, object exceptionObject, bool isTerminating);

        [LoggerMessage(Level = LogLevel.Error, Message = "Unobserved task exception")]
        public static partial void UnobservedTaskException(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to resolve ReceiveHistoryRecorder; RX images will not be auto-saved to history")]
        public static partial void ReceiveHistoryRecorderResolveFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to resolve RxAudioAutoSaver; RX audio will not be attached to history entries")]
        public static partial void RxAudioAutoSaverResolveFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to resolve RxStationIdAttacher; decoded FSK-ID station IDs will not be attached to history entries")]
        public static partial void RxStationIdAttacherResolveFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Avalonia lifetime started")]
        public static partial void AvaloniaLifetimeStarted(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Shutting down")]
        public static partial void ShuttingDown(ILogger logger);

        [LoggerMessage(Level = LogLevel.Critical, Message = "ApplicationLifetime.Start threw")]
        public static partial void LifetimeStartThrew(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Initial radio auto-connect failed; continuing without a radio connection")]
        public static partial void RadioAutoConnectFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Initial StartReceivingAsync failed; continuing without an active capture device")]
        public static partial void StartReceivingFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Host teardown (DisposeAsync) did not complete within 10s; exiting anyway")]
        public static partial void TeardownTimedOut(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Host teardown (DisposeAsync) threw; exiting anyway")]
        public static partial void TeardownThrew(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SqliteConnection.ClearAllPools threw during shutdown")]
        public static partial void ClearPoolsThrew(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Restarting after teardown did not complete cleanly; the new instance may start with no radio/audio connection")]
        public static partial void RestartingAfterIncompleteTeardown(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Spawning a new instance for restart")]
        public static partial void RestartSpawning(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to spawn a new instance for restart; Scanline Studio will need to be relaunched manually")]
        public static partial void RestartSpawnFailed(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Restored persisted UI culture '{CultureCode}'")]
        public static partial void CultureRestored(ILogger logger, string cultureCode);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to restore the persisted UI culture; continuing in English")]
        public static partial void CultureRestoreFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Restored persisted UI theme '{Theme}'")]
        public static partial void AppThemeRestored(ILogger logger, string theme);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to restore the persisted UI theme; continuing with the default")]
        public static partial void AppThemeRestoreFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load the Appearance settings section; theme and font scale both continue with their defaults")]
        public static partial void AppearanceSettingsLoadFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Restored persisted UI font scale '{FontScale}'")]
        public static partial void FontScaleRestored(ILogger logger, string fontScale);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to restore the persisted UI font scale; continuing with the default")]
        public static partial void FontScaleRestoreFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to read settings section '{SectionKey}'; using defaults")]
        public static partial void SettingsSectionReadFailed(ILogger logger, string sectionKey, Exception ex);
    }
}
