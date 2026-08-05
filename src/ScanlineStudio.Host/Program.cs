using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Audio.MiniAudio;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Localization;
using ScanlineStudio.Core.Logbook;
using ScanlineStudio.Core.Radio;
using ScanlineStudio.Core.Radio.Rigctld;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;
using ScanlineStudio.UI;
using ScanlineStudio.UI.Docking;
using ScanlineStudio.UI.Services;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.Host;

// Composition root — see spec/01-architecture.md. This is the only place allowed to call `new`
// on concrete infrastructure types or register services with the DI container.
internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        var hostBuilder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        hostBuilder.Services.AddSingleton<ISettingsStore>(new JsonSettingsStore());
        hostBuilder.Services.AddTransient<MainViewModel>();

        // Locale files live alongside the built app -- see ScanlineStudio.Host.csproj's asset-copy item.
        // Always boots into English; restoring a persisted non-English culture is a separate,
        // later step (once ScanlineStudio.Settings has a culture section to restore from).
        var localeDirectory = Path.Combine(AppContext.BaseDirectory, "assets", "locale");
        hostBuilder.Services.AddSingleton<ILocalizationService>(sp =>
            new JsonLocalizationService(localeDirectory, sp.GetRequiredService<ILogger<JsonLocalizationService>>()));

        // AppDockFactory's constructor takes the real pane dependencies below, resolved
        // automatically by DI (decision #7 -- this, not a static locator, is how a Dock-constructed
        // pane gets real services).
        hostBuilder.Services.AddSingleton<AppDockFactory>();
        hostBuilder.Services.AddSingleton<IFilePickerService, FilePickerService>();

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
        hostBuilder.Services.AddSingleton<ISstvDecoder>(new AnalogFmSstvDecoder());
        hostBuilder.Services.AddSingleton<ISstvEncoder>(new AnalogFmSstvEncoder());
        hostBuilder.Services.AddSingleton<IWaterfallSource>(new WaterfallSource(sampleRate: 11025));

        // Image pipeline (step 5) -- ReceivedImageBuffer's constructor takes ISstvDecoder, resolved
        // automatically from the registration above (it subscribes to LineDecoded/DecodeRestarted
        // itself; see that class's own doc comment for why this is layering-legal).
        hostBuilder.Services.AddSingleton<IImageFileLoader, ImageFileLoader>();
        hostBuilder.Services.AddSingleton<IReceivedImageBuffer, ReceivedImageBuffer>();

        // Phase 4 image-tooling UI -- spec/07-image-pipeline.md's "Stock image library"/"RX history"
        // sections. ReceiveHistoryRecorder is resolved once, explicitly, below (nothing else in the
        // DI graph depends on it as a constructor parameter the way ReceivedImageBuffer's ISstvDecoder
        // subscription gets triggered automatically -- its own event subscriptions only happen once
        // something actually asks the container to build one).
        hostBuilder.Services.AddSingleton<IStockImageLibrary, StockImageLibrary>();
        hostBuilder.Services.AddSingleton<IReceiveHistoryStore, SqliteReceiveHistoryStore>();
        hostBuilder.Services.AddSingleton<ReceiveHistoryRecorder>();

        // TX image editor (spec/07-image-pipeline.md's "TX image editor" section) -- the
        // Crop/Resize/ApplyOverlay pipeline both TxImageEditorPaneViewModel's live preview and
        // TxControlsPaneViewModel's mode-change reflow run against.
        hostBuilder.Services.AddSingleton<ITransmitImagePreparer, TransmitImagePreparer>();

        // Radio layer -- RigctldProtocolFactory only (decision #12: the one backend wired for the
        // Phase 3 demo; linked Hamlib is a one-line addition later, not blocking this phase).
        // RadioController's constructor takes IEnumerable<IRadioProtocolFactory>, resolved
        // automatically from every factory registered here -- exactly one today, by design.
        hostBuilder.Services.AddSingleton<IRadioProtocolFactory, RigctldProtocolFactory>();
        hostBuilder.Services.AddSingleton<IRadioController, RadioController>();

        // ScanlineStudio.Application services -- the only things ScanlineStudio.UI is allowed to depend on
        // (spec/01-architecture.md's layering rule); everything above is UI-invisible plumbing.
        hostBuilder.Services.AddSingleton<IRadioSessionService, RadioSessionService>();
        hostBuilder.Services.AddSingleton<ISstvSessionService, SstvSessionService>();

        var host = hostBuilder.Build();
        App.Services = host.Services;

        // Eagerly resolved so its constructor's ISstvDecoder event subscriptions actually happen --
        // see the registration comment above for why this can't just rely on being a constructor
        // dependency somewhere else the way ReceivedImageBuffer's own subscription does.
        host.Services.GetRequiredService<ReceiveHistoryRecorder>();

        // Auto-connect from persisted settings at startup -- the radio status strip (step 9) is a
        // fixed, read-only label, not an interactive "Connect" button (Phase 3 plan decision), so
        // this is the only place the initial connection attempt happens. A missing/unset radio
        // section resolves to NoneConnectionSpec (a first-class, always-valid state,
        // spec/02-radio-layer.md) -- swallowed here defensively so a real connect failure can never
        // prevent the UI itself from starting; IRadioController's own reconnect/backoff machinery
        // takes over from here via its ConnectionEvents/StateChanges streams.
        try
        {
            host.Services.GetRequiredService<IRadioSessionService>().ConnectUsingSettingsAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        // Same reasoning as the radio auto-connect above: Phase 3 has no "Start Receiving" button
        // anywhere in the UI (the walking skeleton's own demo target is a session that's simply
        // listening once the app is up), so this is the only place capture starts. A missing/unset
        // audio-device section throws InvalidOperationException from StartReceivingAsync -- swallowed
        // here the same way, so a machine with no configured capture device still gets a working UI
        // (waterfall/RX image just stay empty) instead of failing to start at all.
        try
        {
            host.Services.GetRequiredService<ISstvSessionService>().StartReceivingAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        var lifetime = new ClassicDesktopStyleApplicationLifetime { Args = args };
        BuildAvaloniaApp().SetupWithLifetime(lifetime);

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
        // refcount imbalance). Bounded and swallowed here -- there is no logger in this project
        // yet to report through, and a teardown failure at process exit must not prevent the
        // process from actually exiting.
        //
        // Round-2-engine-review fix: Task.WhenAny's own result never rethrows the winning task's
        // fault -- a faulted DisposeAsync used to be silently discarded by WhenAny itself, never
        // even reaching the try/catch below despite this handler's own comment implying otherwise.
        // If disposeTask is the one that completed (as opposed to the 10s Delay winning instead),
        // re-observing it via GetAwaiter().GetResult() is a no-op on success and rethrows -- into
        // the catch below -- on fault, actually giving the try/catch something to do.
        lifetime.Exit += (_, _) =>
        {
            try
            {
                var disposeTask = ((IAsyncDisposable)host).DisposeAsync().AsTask();
                var completed = Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(10))).GetAwaiter().GetResult();
                if (completed == disposeTask)
                {
                    disposeTask.GetAwaiter().GetResult();
                }
            }
            catch
            {
            }
        };
        lifetime.Start(args);
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() => App.BuildAvaloniaApp();
}
