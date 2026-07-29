using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Yoniq.Abstractions.Audio;
using Yoniq.Core.Audio.MiniAudio;
using Yoniq.Settings;
using Yoniq.UI;
using Yoniq.UI.ViewModels;

namespace Yoniq.Host;

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

        // Piece Engine 6. Registered by type, not an eagerly-constructed instance (unlike
        // ISettingsStore above) -- MiniAudioEngine's constructor initializes the native miniaudio
        // context for real, which must not run at process start on a machine with no audio server.
        // Nothing resolves these from the UI yet (Yoniq.Application has no real source files today,
        // confirmed via this project's own Opus plan-review pass) -- this registration exists so
        // the composition root is ready once something does, not because a consumer exists now.
        // Neither type may be referenced from Yoniq.UI directly per spec/01-architecture.md's
        // layering rule (UI only talks to Yoniq.Application service interfaces); resolving them
        // here, in Yoniq.Host, does not violate that.
        hostBuilder.Services.AddSingleton<IAudioEngine, MiniAudioEngine>();
        hostBuilder.Services.AddSingleton<IAudioDeviceEnumerator, MiniAudioDeviceEnumerator>();

        var host = hostBuilder.Build();
        App.Services = host.Services;

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
        lifetime.Exit += (_, _) =>
        {
            try
            {
                var disposeTask = ((IAsyncDisposable)host).DisposeAsync().AsTask();
                Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(10))).GetAwaiter().GetResult();
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
