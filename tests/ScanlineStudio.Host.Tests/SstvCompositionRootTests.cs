using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Logbook;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.Host.Tests;

public sealed class SstvCompositionRootTests
{
    [Fact]
    public async Task RegisterServices_ResolvesEveryServiceMainActuallyRequiresAtStartup()
    {
        // Tier C audit finding: Program.cs's own ~150-line DI registration block used to live
        // inline in Main -- structurally untestable, so a missing registration (or a missing
        // STARTUP STEP entirely, exactly how the culture-restore blocker fixed alongside this test
        // went unnoticed) surfaced only at first real resolve in a real run. Extracted into
        // RegisterServices (mirroring RegisterSstvServices's own existing shape) specifically so
        // this test can exist. Resolves the same things Main() itself resolves at startup
        // (MainViewModel and OptionsWindowViewModel transitively pull in almost every other
        // registration -- panes, the Application-layer services, the radio/logbook/image backends).
        // IAudioEngine/IAudioDeviceEnumerator are substituted with fakes -- MiniAudioEngine's own
        // constructor genuinely initializes a native audio context (Program.cs's own registration
        // comment: "must not run at process start on a machine with no audio server"), which is not
        // safe to do from a CI test host; every OTHER registration below is exactly what
        // RegisterServices itself defines, unmodified.
        //
        // Round-2 confirmation finding: all three substitute registrations MUST come AFTER
        // RegisterServices, not before -- DI is last-registration-wins, so registering the fake
        // ISettingsStore first (as an earlier version of this test did) got silently SHADOWED by
        // RegisterServices's own real JsonSettingsStore registration, resolving against (and
        // creating SQLite files under) the real developer machine's actual settings.json/profile
        // directory instead of this test's own isolated fake.
        var services = new ServiceCollection();
        services.AddLogging();
        Program.RegisterServices(services);
        services.AddSingleton<ISettingsStore>(new StaticSettingsStore(new AppSettings()));
        services.AddSingleton<IAudioEngine>(new FakeAudioEngine());
        services.AddSingleton<IAudioDeviceEnumerator>(new NullAudioDeviceEnumerator());
        // Not `using` -- ISstvSessionService's real implementation is IAsyncDisposable-only, same
        // reason Program.cs's own teardown handler goes through DisposeAsync explicitly rather than
        // a synchronous Dispose()/`using` (see that handler's own doc comment).
        await using var provider = services.BuildServiceProvider();

        var mainViewModel = provider.GetRequiredService<MainViewModel>();
        var optionsViewModel = provider.GetRequiredService<OptionsWindowViewModel>();
        _ = provider.GetRequiredService<ReceiveHistoryRecorder>();
        _ = provider.GetRequiredService<ISettingsStore>();

        Assert.NotNull(mainViewModel);
        Assert.NotNull(optionsViewModel);
    }

    [Fact]
    public void CreateSstvDecoder_CorruptAudioDeviceSettingsSection_FallsBackToDefaultsInsteadOfThrowing()
    {
        // Tier C audit finding (risk): GetSection's own Deserialize call throws JsonException for a
        // wrong-typed value in a hand-edited/version-skewed settings.json (e.g. "SampleRate": "auto")
        // -- unlike JsonSettingsStore.LoadAsync itself (already hardened against a corrupt FILE), this
        // was the third instance of an unguarded settings-SECTION read reaching a DI factory with no
        // try/catch. Since this factory backs a singleton, a throw here was not even one-shot: DI does
        // not cache a failed construction, so the NEXT resolve would retry and throw again, past the
        // one caller that happened to catch it the first time.
        var settings = new AppSettings();
        settings.Sections[AudioDeviceSettings.SectionKey] = JsonDocument.Parse("\"not an AudioDeviceSettings object\"").RootElement;
        var services = new ServiceCollection();
        services.AddSingleton<ISettingsStore>(new StaticSettingsStore(settings));
        services.AddLogging();
        Program.RegisterSstvServices(services);
        using var provider = services.BuildServiceProvider();

        var decoder = Assert.IsType<RestartableSstvDecoder>(provider.GetRequiredService<ISstvDecoder>());

        Assert.Equal(SstvSampleRate.Default, decoder.SampleRate);
    }

    [Fact]
    public void CreateSstvDecoder_CorruptDecoderSettingsSection_FallsBackToDefaultsInsteadOfThrowing()
    {
        // Same finding as above, the SstvDecoderSettings section's own independent read/catch.
        var settings = new AppSettings();
        settings.Sections[SstvDecoderSettings.SectionKey] = JsonDocument.Parse("42").RootElement;
        var services = new ServiceCollection();
        services.AddSingleton<ISettingsStore>(new StaticSettingsStore(settings));
        services.AddLogging();
        Program.RegisterSstvServices(services);
        using var provider = services.BuildServiceProvider();

        var decoder = Assert.IsType<RestartableSstvDecoder>(provider.GetRequiredService<ISstvDecoder>());

        Assert.True(decoder.AutoSlantEnabled);
    }

    private sealed class NullAudioDeviceEnumerator : IAudioDeviceEnumerator
    {
        public IReadOnlyList<AudioDeviceInfo> InputDevices { get; } = [];

        public IReadOnlyList<AudioDeviceInfo> OutputDevices { get; } = [];

        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public void SstvServices_UseThePersistedConfiguredSampleRateThroughActualRegistrations()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISettingsStore>(new StaticSettingsStore(
            new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { SampleRate = 22050 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings)));
        services.AddLogging();
        Program.RegisterSstvServices(services);
        using var provider = services.BuildServiceProvider();

        var resolvedDecoder = Assert.IsType<RestartableSstvDecoder>(provider.GetRequiredService<ISstvDecoder>());
        var encoder = Assert.IsType<AnalogFmSstvEncoder>(provider.GetRequiredService<ISstvEncoder>());
        var waterfall = Assert.IsType<WaterfallSource>(provider.GetRequiredService<IWaterfallSource>());

        Assert.Equal(22050, resolvedDecoder.SampleRate);
        Assert.Equal(resolvedDecoder.SampleRate, encoder.SampleRate);
        Assert.Equal(resolvedDecoder.SampleRate, waterfall.SampleRate);
    }

    [Theory]
    [InlineData(4999)]
    [InlineData(48501)]
    public void SstvServices_InvalidPersistedSampleRate_FallsBackThroughActualRegistrations(int persistedRate)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISettingsStore>(new StaticSettingsStore(
            new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { SampleRate = persistedRate },
                AudioSettingsJsonContext.Default.AudioDeviceSettings)));
        services.AddLogging();
        Program.RegisterSstvServices(services);
        using var provider = services.BuildServiceProvider();

        var decoder = Assert.IsType<RestartableSstvDecoder>(provider.GetRequiredService<ISstvDecoder>());
        var encoder = Assert.IsType<AnalogFmSstvEncoder>(provider.GetRequiredService<ISstvEncoder>());
        var waterfall = Assert.IsType<WaterfallSource>(provider.GetRequiredService<IWaterfallSource>());

        Assert.Equal(SstvSampleRate.Default, decoder.SampleRate);
        Assert.Equal(SstvSampleRate.Default, encoder.SampleRate);
        Assert.Equal(SstvSampleRate.Default, waterfall.SampleRate);
    }

    [Fact]
    public void SstvServices_UsesEveryPersistedDecoderSetting_ThroughActualRegistration()
    {
        // Round-9 D2-audit finding: Program.CreateSstvDecoder forwards 8 SstvDecoderSettings fields
        // (afcEnabled/syncRestartEnabled/autoSyncEnabled/autoStopEnabled/autoSlantEnabled/senseLevel/
        // demodType/rxBpfPreset) into RestartableSstvDecoder's constructor, but until this test only
        // sampleRate and rxBufferMode were ever pinned here -- deleting any of the other 8 (all
        // optional constructor parameters, so deletion compiles) left this whole suite green while a
        // persisted Options > Decode setting would silently never reach the decoder AT ALL, not just
        // after a periodic restart (the narrower risk round 8 fixed inside CreateInner itself). Every
        // value below is non-default so no assertion can pass vacuously against a parameter default.
        // Reads RestartableSstvDecoder's internal Inner*ForTests accessors (round-11 correction of a
        // round-10 miscount: InnerDemodTypeForTests/InnerRxBpfPresetForTests predate round 8, unlike
        // the other 5 read here; InternalsVisibleTo was widened to this assembly this same round
        // regardless) except AutoSlantEnabled, which already has a public wrapper-level getter.
        var settings = new AppSettings()
            .WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { SampleRate = 11025 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings)
            .WithSection(
                SstvDecoderSettings.SectionKey,
                new SstvDecoderSettings
                {
                    AfcEnabled = false,
                    SyncRestartEnabled = false,
                    AutoSyncEnabled = false,
                    AutoStopEnabled = true,
                    AutoSlantEnabled = false,
                    SenseLevel = 3,
                    DemodType = DemodType.Pll,
                    RxBpfPreset = RxBpfPreset.Narrow,
                },
                SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        var services = new ServiceCollection();
        services.AddSingleton<ISettingsStore>(new StaticSettingsStore(settings));
        services.AddLogging();
        Program.RegisterSstvServices(services);
        using var provider = services.BuildServiceProvider();

        var decoder = Assert.IsType<RestartableSstvDecoder>(provider.GetRequiredService<ISstvDecoder>());

        Assert.False(decoder.AutoSlantEnabled);
        Assert.False(decoder.InnerAfcEnabledForTests);
        Assert.False(decoder.InnerSyncRestartEnabledForTests);
        Assert.False(decoder.InnerAutoSyncEnabledForTests);
        Assert.True(decoder.InnerAutoStopEnabledForTests);
        Assert.Equal(3, decoder.InnerSenseLevelForTests);
        Assert.Equal(DemodType.Pll, decoder.InnerDemodTypeForTests);
        Assert.Equal(RxBpfPreset.Narrow, decoder.InnerRxBpfPresetForTests);
    }

    [Fact]
    public void SstvServices_ExtendedBuffer_ThreadsProductionLoggerFactoryThroughActualRegistration()
    {
        var settings = new AppSettings()
            .WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { SampleRate = 11025 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings)
            .WithSection(
                SstvDecoderSettings.SectionKey,
                new SstvDecoderSettings { RxBufferMode = RxBufferMode.Extended },
                SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        var loggerFactory = new RecordingLoggerFactory();
        var services = new ServiceCollection();
        services.AddSingleton<ISettingsStore>(new StaticSettingsStore(settings));
        services.AddLogging();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        Program.RegisterSstvServices(services);
        using var provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<ISstvDecoder>();

        Assert.Equal(1, loggerFactory.RxDiskLoggerCreateCount);
    }

    private sealed class StaticSettingsStore(AppSettings settings) : ISettingsStore
    {
        public IObservable<AppSettings> Changes { get; } = new NeverObservable();

        public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(settings);

        public Task SaveAsync(AppSettings updatedSettings, CancellationToken ct = default) =>
            throw new NotSupportedException();

        private sealed class NeverObservable : IObservable<AppSettings>
        {
            public IDisposable Subscribe(IObserver<AppSettings> observer) => EmptyDisposable.Instance;
        }

        private sealed class EmptyDisposable : IDisposable
        {
            public static EmptyDisposable Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public int RxDiskLoggerCreateCount { get; private set; }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
        {
            if (categoryName == "ScanlineStudio.Core.Sstv.RxDiskLineStagingBuffer")
            {
                RxDiskLoggerCreateCount++;
            }

            return PassiveLogger.Instance;
        }

        public void Dispose()
        {
        }
    }

    private sealed class PassiveLogger : ILogger
    {
        public static PassiveLogger Instance { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
