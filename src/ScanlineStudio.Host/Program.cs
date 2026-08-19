using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Audio.MiniAudio;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Localization;
using ScanlineStudio.Core.Logbook;
using ScanlineStudio.Core.Radio;
using ScanlineStudio.Core.Radio.Hamlib;
using ScanlineStudio.Core.Radio.Rigctld;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;
using ScanlineStudio.UI;
using ScanlineStudio.UI.Services;
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
        var hostBuilder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

        // File provider alongside the console provider CreateApplicationBuilder already registers
        // by default -- see FileLoggerProvider's own doc comment for why. Fixed, predictable path
        // (not per-run-timestamped) so it can always be read directly without hunting for the
        // latest file.
        var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanlineStudio", "logs", "app.log");

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
        try
        {
            hostBuilder.Logging.AddProvider(new FileLoggerProvider(logPath));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to open log file at '{logPath}': {ex}. Continuing with console logging only.");
        }

        hostBuilder.Services.AddSingleton<ISettingsStore>(sp => new JsonSettingsStore(sp.GetRequiredService<ILogger<JsonSettingsStore>>()));

        // First IHttpClientFactory consumer in this codebase (QrzLogbookUploader) -- no prior
        // registration to match, this is the standard AddHttpClient() entry point.
        hostBuilder.Services.AddHttpClient();

        // Singleton, not transient: resolved exactly once at startup (App.axaml.cs) as the app's
        // one root view-model, but a second resolve would silently fork
        // TxControlsPaneViewModel.RadioStatus (assigned once in this constructor, from a singleton
        // TxControlsPaneViewModel) into a second, out-of-sync RadioStatusViewModel instance -- the
        // exact forked-persistence failure the RadioStatus wiring is designed to avoid. Only one
        // resolve site exists today, so this was a dormant risk, not an active bug; registering it
        // correctly here closes it off rather than leaving it to bite a future second call site.
        hostBuilder.Services.AddSingleton<MainViewModel>();

        // Options dialog -- transient so each open/close cycle gets a fresh OptionsSettingsService
        // (re-reads settings.json from disk each time, no stale in-memory copy carried over).
        hostBuilder.Services.AddTransient<OptionsSettingsService>();
        hostBuilder.Services.AddTransient<OptionsWindowViewModel>();

        // Locale files live alongside the built app -- see ScanlineStudio.Host.csproj's asset-copy item.
        // Always boots into English; restoring a persisted non-English culture is a separate,
        // later step (once ScanlineStudio.Settings has a culture section to restore from).
        var localeDirectory = Path.Combine(AppContext.BaseDirectory, "assets", "locale");
        hostBuilder.Services.AddSingleton<ILocalizationService>(sp =>
            new JsonLocalizationService(localeDirectory, sp.GetRequiredService<ILogger<JsonLocalizationService>>()));

        hostBuilder.Services.AddSingleton<IFilePickerService, FilePickerService>();

        // Fixed-shell pane view-models (spec/09-ui.md) -- singletons, one per app session, resolved
        // automatically by DI straight into MainViewModel's constructor (replaces the former
        // AppDockFactory, which built these same 4 instances by hand). TxImageEditorPaneViewModel is
        // NOT registered here -- it's constructed dynamically per edit session (with runtime-only
        // args: the picked image + target mode), same as before.
        hostBuilder.Services.AddSingleton<WaterfallPaneViewModel>();
        hostBuilder.Services.AddSingleton<RxImagePaneViewModel>();
        hostBuilder.Services.AddSingleton<RxHistoryPaneViewModel>();
        hostBuilder.Services.AddSingleton<TxControlsPaneViewModel>();
        hostBuilder.Services.AddSingleton<LogbookPaneViewModel>();

        // Piece Engine 6. Registered by type, not an eagerly-constructed instance (unlike
        // ISettingsStore above) -- MiniAudioEngine's constructor initializes the native miniaudio
        // context for real, which must not run at process start on a machine with no audio server.
        // Nothing resolves these from the UI yet (ScanlineStudio.Application has no real source files today,
        // confirmed via this project's own Opus plan-review pass) -- this registration exists so
        // the composition root is ready once something does, not because a consumer exists now.
        // Neither type may be referenced from ScanlineStudio.UI directly per spec/01-architecture.md's
        // layering rule (UI only talks to ScanlineStudio.Application service interfaces); resolving them
        // here, in ScanlineStudio.Host, does not violate that.
        hostBuilder.Services.AddSingleton<IAudioEngine, MiniAudioEngine>();
        hostBuilder.Services.AddSingleton<IAudioDeviceEnumerator, MiniAudioDeviceEnumerator>();

        // SSTV DSP core -- one decoder/encoder/waterfall per app session (Phase 3 scope: a single
        // concurrent session, matching the single IAudioEngine instance above).
        // Factories, not eagerly-constructed instances: the decoder reads both decoder settings and
        // the process-lifetime DSP sample rate; encoder and waterfall resolve that decoder-owned rate
        // so capture/RX/display/TX cannot silently disagree.
        // Ultracode audit finding #34: RestartableSstvDecoder (not AnalogFmSstvDecoder directly)
        // periodically discards/reconstructs the whole decoder object graph to avoid an int-overflow
        // before its int absolute-index space can overflow -- see that class's own doc comment. Every
        // consumer only ever holds ISstvDecoder, so this swap is fully transparent.
        RegisterSstvServices(hostBuilder.Services);

        // Image pipeline (step 5) -- ReceivedImageBuffer's constructor takes ISstvDecoder, resolved
        // automatically from the registration above (it subscribes to LineDecoded/DecodeRestarted
        // itself; see that class's own doc comment for why this is layering-legal).
        hostBuilder.Services.AddSingleton<IImageFileLoader, ImageFileLoader>();
        hostBuilder.Services.AddSingleton<IReceivedImageBuffer, ReceivedImageBuffer>();
        // Gallery pane's "Export frame" (2026-08-15) -- re-saves an already-received image file to a
        // user-chosen location, optionally re-encoded as JPEG.
        hostBuilder.Services.AddSingleton<IReceivedFrameExporter, ReceivedFrameExporter>();
        // TX template editor persistence (Phase 5, spec/15-template-designer.md) -- IImageSourceWriter
        // is the missing "write an IImageSource out as a PNG" counterpart to IImageFileLoader;
        // TemplateStore (Application layer) depends on it, IImageFileLoader, and
        // ITransmitImagePreparer, never ImageSharp directly.
        hostBuilder.Services.AddSingleton<IImageSourceWriter, ImageSourceWriter>();
        hostBuilder.Services.AddSingleton<ITemplateStore, TemplateStore>();

        // Phase 4 image-tooling UI -- spec/07-image-pipeline.md's "Stock image library"/"RX history"
        // sections. ReceiveHistoryRecorder is resolved once, explicitly, below (nothing else in the
        // DI graph depends on it as a constructor parameter the way ReceivedImageBuffer's ISstvDecoder
        // subscription gets triggered automatically -- its own event subscriptions only happen once
        // something actually asks the container to build one).
        hostBuilder.Services.AddSingleton<IStockImageLibrary, StockImageLibrary>();
        hostBuilder.Services.AddSingleton<IReceiveHistoryStore, SqliteReceiveHistoryStore>();
        hostBuilder.Services.AddSingleton<ReceiveHistoryRecorder>();

        // QSO logbook backend (spec/08-logging.md + the accompanying plan file) -- SQLite storage
        // (same history.db file as RX history above), ADIF import/export, ADIF-over-UDP streaming
        // (generalized 2026-08-15 from a GridTracker-only streamer to fan the same WSJT-X
        // LoggedADIF datagram out to any configured destination -- GridTracker, N1MM Logger+,
        // Log4OM, or anything else that speaks the same protocol), and QRZ.com Logbook API upload.
        // No UI wired to ADIF UDP streaming yet this piece -- settings only reachable by
        // hand-editing settings.json until the Options-dialog piece lands.
        hostBuilder.Services.AddSingleton<ILogbookRepository, SqliteLogbookRepository>();
        hostBuilder.Services.AddSingleton<IAdifExporter, AdifExporter>();
        hostBuilder.Services.AddSingleton<IAdifImporter, AdifImporter>();
        hostBuilder.Services.AddSingleton<IAdifUdpStreamer, AdifUdpStreamer>();
        hostBuilder.Services.AddSingleton<IQrzLogbookUploader, QrzLogbookUploader>();

        // QRZ.com XML Callbook lookup (spec/08-logging.md's "QRZ.com lookup" section) -- a
        // DIFFERENT QRZ product from the upload API above (username/password auth, pull/enrich
        // direction, not API-key/push). Its own named HttpClient with an explicit timeout: the
        // bare AddHttpClient() above only covers the DEFAULT client QrzLogbookUploader uses, and a
        // hung QRZ connection must not pin a Receive-tab "Lookup QRZ" button disabled for the
        // default 100s.
        //
        // RemoveAllLoggers() is NOT optional here (real-window-testing-caught gap in an earlier
        // draft): IHttpClientFactory's own built-in LoggingHttpMessageHandler logs the full request
        // URI at Information level by default -- and this API sends credentials as GET query
        // parameters (QRZ's own wire format, not this app's choice), so without this call, a real
        // QRZ password lands in ~/.local/share/ScanlineStudio/logs/app.log in plaintext every time
        // this client is used, regardless of anything QrzCallsignLookup's own Log class does or
        // doesn't log. Confirmed via a real manual test against the live QRZ server: the log line
        // read "GET https://xmldata.qrz.com/xml/current/?username=...&password=<plaintext>&agent=..."
        // before this fix. QrzLogbookUploader (immediately above) doesn't need this: its credential
        // travels in a POST body, which the same default handler does not log.
        hostBuilder.Services.AddHttpClient("QrzXmlLookup", c => c.Timeout = TimeSpan.FromSeconds(15)).RemoveAllLoggers();
        hostBuilder.Services.AddSingleton<IQrzCallsignLookup, QrzCallsignLookup>();

        // TX image editor (spec/07-image-pipeline.md's "TX image editor" section) -- the
        // Crop/Resize/ApplyOverlay pipeline both TxImageEditorPaneViewModel's live preview and
        // TxControlsPaneViewModel's mode-change reflow run against.
        hostBuilder.Services.AddSingleton<ITransmitImagePreparer, TransmitImagePreparer>();
        hostBuilder.Services.AddSingleton<IMacroTextResolver, MacroTextResolver>();

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
        hostBuilder.Services.AddSingleton<IRadioProtocolFactory, NoneRadioProtocolFactory>();
        hostBuilder.Services.AddSingleton<IRadioProtocolFactory, RigctldProtocolFactory>();
        hostBuilder.Services.AddSingleton<IRadioProtocolFactory>(sp => HamlibProtocolFactory.Create(loggerFactory: sp.GetRequiredService<ILoggerFactory>()));
        hostBuilder.Services.AddSingleton<IRadioController, RadioController>();

        // ScanlineStudio.Application services -- the only things ScanlineStudio.UI is allowed to depend on
        // (spec/01-architecture.md's layering rule); everything above is UI-invisible plumbing.
        hostBuilder.Services.AddSingleton<IRadioSessionService, RadioSessionService>();
        hostBuilder.Services.AddSingleton<ISstvSessionService, SstvSessionService>();
        hostBuilder.Services.AddSingleton<ILogbookSessionService, LogbookSessionService>();

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

        // Apply a settings-driven process priority, if configured -- a QoL knob, not a startup
        // requirement, so a failure here (e.g. Win32Exception from insufficient permission on some
        // platforms) must never prevent the app from starting. Null means "leave the OS default
        // alone" (see AppPerformanceSettings.ProcessPriority's own doc comment).
        try
        {
            var appPerformanceSettings = host.Services.GetRequiredService<ISettingsStore>().LoadAsync().GetAwaiter().GetResult()
                .GetSection(AppPerformanceSettings.SectionKey, AppPerformanceSettingsJsonContext.Default.AppPerformanceSettings);
            if (appPerformanceSettings?.ProcessPriority is { } priority)
            {
                System.Diagnostics.Process.GetCurrentProcess().PriorityClass = priority;
                Log.ProcessPrioritySet(logger, priority);
            }
        }
        catch (Exception ex)
        {
            Log.ProcessPrioritySetFailed(logger, ex);
        }

        // Auto-connect from persisted settings at startup -- the radio status strip (step 9) is a
        // fixed, read-only label, not an interactive "Connect" button (Phase 3 plan decision), so
        // this is the only place the initial connection attempt happens. A missing/unset radio
        // section resolves to NoneConnectionSpec (a first-class, always-valid state,
        // spec/02-radio-layer.md) -- caught here defensively so a real connect failure can never
        // prevent the UI itself from starting; IRadioController's own reconnect/backoff machinery
        // takes over from here via its ConnectionEvents/StateChanges streams. Logged, not silently
        // swallowed, now that a logger exists.
        try
        {
            host.Services.GetRequiredService<IRadioSessionService>().ConnectUsingSettingsAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.RadioAutoConnectFailed(logger, ex);
        }

        // Same reasoning as the radio auto-connect above: Phase 3 has no "Start Receiving" button
        // anywhere in the UI (the walking skeleton's own demo target is a session that's simply
        // listening once the app is up), so this is the only place capture starts. A missing/unset
        // audio-device section throws InvalidOperationException from StartReceivingAsync -- caught
        // here the same way, so a machine with no configured capture device still gets a working UI
        // (waterfall/RX image just stay empty) instead of failing to start at all.
        try
        {
            host.Services.GetRequiredService<ISstvSessionService>().StartReceivingAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.StartReceivingFailed(logger, ex);
        }

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
        lifetime.Exit += (_, _) =>
        {
            Log.ShuttingDown(logger);
            try
            {
                var disposeTask = ((IAsyncDisposable)host).DisposeAsync().AsTask();
                var completed = Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(10))).GetAwaiter().GetResult();
                if (completed == disposeTask)
                {
                    disposeTask.GetAwaiter().GetResult();
                }
                else
                {
                    Log.TeardownTimedOut(logger);
                }
            }
            catch (Exception ex)
            {
                Log.TeardownThrew(logger, ex);
            }
        };

        try
        {
            lifetime.Start(args);
        }
        catch (Exception ex)
        {
            Log.LifetimeStartThrew(logger, ex);
            throw;
        }
    }

    internal static void RegisterSstvServices(IServiceCollection services)
    {
        services.AddSingleton<ISstvDecoder>(CreateSstvDecoder);
        services.AddSingleton<ISstvEncoder>(CreateSstvEncoder);
        services.AddSingleton<IWaterfallSource>(CreateWaterfallSource);
    }

    internal static RestartableSstvDecoder CreateSstvDecoder(IServiceProvider services)
    {
        var appSettings = services.GetRequiredService<ISettingsStore>().LoadAsync().GetAwaiter().GetResult();
        var audioSettings = appSettings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings)
            ?? new AudioDeviceSettings();
        var sampleRate = SstvSampleRate.NormalizePersisted(audioSettings.SampleRate);
        var decoderSettings = appSettings.GetSection(SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings)
            ?? new SstvDecoderSettings();

        // Absent or out-of-range values use the documented legacy-derived defaults. SenseLevel's
        // distinct out-of-range fallback remains inside AnalogFmSstvDecoder's constructor.
        var demodType = decoderSettings.DemodType is { } dt && Enum.IsDefined(dt) ? dt : DemodType.Hilbert;
        var rxBpfPreset = decoderSettings.RxBpfPreset is { } bpf && Enum.IsDefined(bpf) ? bpf : RxBpfPreset.Wide;
        var rxBufferMode = decoderSettings.RxBufferMode is { } rxb && Enum.IsDefined(rxb) ? rxb : RxBufferMode.On;
        return new RestartableSstvDecoder(
            afcEnabled: decoderSettings.AfcEnabled ?? true,
            syncRestartEnabled: decoderSettings.SyncRestartEnabled ?? true,
            autoSyncEnabled: decoderSettings.AutoSyncEnabled ?? true,
            autoStopEnabled: decoderSettings.AutoStopEnabled ?? false,
            autoSlantEnabled: decoderSettings.AutoSlantEnabled ?? true,
            senseLevel: decoderSettings.SenseLevel ?? 1,
            demodType: demodType,
            rxBpfPreset: rxBpfPreset,
            rxBufferMode: rxBufferMode,
            sampleRate: sampleRate);
    }

    internal static AnalogFmSstvEncoder CreateSstvEncoder(IServiceProvider services) =>
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

        [LoggerMessage(Level = LogLevel.Debug, Message = "Avalonia lifetime started")]
        public static partial void AvaloniaLifetimeStarted(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Shutting down")]
        public static partial void ShuttingDown(ILogger logger);

        [LoggerMessage(Level = LogLevel.Critical, Message = "ApplicationLifetime.Start threw")]
        public static partial void LifetimeStartThrew(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Process priority set to {Priority}")]
        public static partial void ProcessPrioritySet(ILogger logger, System.Diagnostics.ProcessPriorityClass priority);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to apply configured process priority; continuing at the OS default")]
        public static partial void ProcessPrioritySetFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Initial radio auto-connect failed; continuing without a radio connection")]
        public static partial void RadioAutoConnectFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Initial StartReceivingAsync failed; continuing without an active capture device")]
        public static partial void StartReceivingFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Host teardown (DisposeAsync) did not complete within 10s; exiting anyway")]
        public static partial void TeardownTimedOut(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Host teardown (DisposeAsync) threw; exiting anyway")]
        public static partial void TeardownThrew(ILogger logger, Exception ex);
    }
}
