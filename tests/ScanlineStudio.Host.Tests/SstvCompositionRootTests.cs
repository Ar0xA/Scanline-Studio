using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Host.Tests;

public sealed class SstvCompositionRootTests
{
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
        // Reads RestartableSstvDecoder's internal Inner*ForTests accessors (round-10 correction:
        // InnerDemodTypeForTests/InnerRxBpfPresetForTests predate round 8, unlike the other 3 read
        // here; InternalsVisibleTo was widened to this assembly this same round regardless) except
        // AutoSlantEnabled, which already has a public wrapper-level getter.
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
