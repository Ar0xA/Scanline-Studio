using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
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
        // IAudioEngine/IAudioDeviceEnumerator/IAudioDeviceMuteQuery are substituted with fakes --
        // MiniAudioEngine/MiniAudioDeviceMuteQuery's own constructors genuinely initialize a
        // native audio context (Program.cs's own registration comment: "must not run at process
        // start on a machine with no audio server"), which is not safe to do from a CI test host;
        // every OTHER registration below is exactly what RegisterServices itself defines, unmodified.
        //
        // Round-2 confirmation finding: all substitute registrations MUST come AFTER
        // RegisterServices, not before -- DI is last-registration-wins, so registering the fake
        // ISettingsStore first (as an earlier version of this test did) got silently SHADOWED by
        // RegisterServices's own real JsonSettingsStore registration, resolving against (and
        // creating SQLite files under) the real developer machine's actual settings.json/profile
        // directory instead of this test's own isolated fake.
        var services = new ServiceCollection();
        services.AddLogging();
        Program.RegisterServices(services);
        // Both interfaces substituted from the SAME instance -- see StaticSettingsStore's own doc
        // comment for why (round-3 plan-review risk-A).
        var staticSettingsStore = new StaticSettingsStore(new AppSettings());
        services.AddSingleton<ISettingsStore>(staticSettingsStore);
        services.AddSingleton<ISettingsFileRelocator>(staticSettingsStore);
        services.AddSingleton<IAudioEngine>(new FakeAudioEngine());
        services.AddSingleton<IAudioDeviceEnumerator>(new NullAudioDeviceEnumerator());
        services.AddSingleton<IAudioDeviceMuteQuery>(new FakeAudioDeviceMuteQuery());
        // Substituted (plan-review, test-suite fixes phase 1, item 4) -- CreateSstvSessionService's
        // real factory (Program.cs:880) eagerly calls IReceiveHistoryStore.GetAudioSettingsAsync at
        // DI resolve time; without this, resolving MainViewModel/ISstvSessionService in any test here
        // constructs the REAL SqliteReceiveHistoryStore against this developer/CI machine's actual
        // history.db, same shadowing hazard the ISettingsStore substitution above already guards
        // against for settings.json.
        services.AddSingleton<IReceiveHistoryStore>(new NullReceiveHistoryStore());
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
    public async Task RegisterServices_AllDescriptors_ResolveWithoutThrowing()
    {
        // Test-suite fixes phase 1, item 4: RegisterServices_ResolvesEveryServiceMainActuallyRequiresAtStartup
        // above only resolves 4 specific roots -- a registration nothing transitively reaches from
        // those 4 (e.g. MacrosReferenceWindowViewModel/ConfigurationsManagerWindowViewModel/
        // IApplicationRestarter, each only resolved lazily via App.Services at menu-click time)
        // surfaces as an unguarded crash in the real app, not a test failure. ValidateOnBuild here
        // catches a TYPE-registered descriptor missing a dependency; it cannot see into a
        // factory-lambda registration (JsonSettingsStore/ConfigurationPresetStore/ILocalizationService/
        // the Hamlib factory/ISstvSessionService/etc. -- see the sibling test below for those).
        var services = BuildServicesWithFakes();

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public async Task RegisterServices_EveryRegisteredService_CanBeResolved()
    {
        // The complement to the ValidateOnBuild test above: enumerates every descriptor
        // RegisterServices actually adds and resolves each by its service type, so a broken
        // factory-lambda registration (which ValidateOnBuild cannot see into at all) fails here
        // instead of at first real use in the shipped app.
        var services = BuildServicesWithFakes();
        var descriptors = services.ToList();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        foreach (var descriptor in descriptors)
        {
            // Open generic type definitions (e.g. ILogger<>/IOptions<>, added by services.AddLogging())
            // cannot be resolved directly -- GetService(typeof(ILogger<>)) throws by design; only a
            // closed generic constructed at a real call site (ILogger<MainViewModel>, etc.) is
            // resolvable, and those closed forms aren't in this descriptor list at all.
            if (descriptor.ServiceType.IsGenericTypeDefinition)
            {
                continue;
            }

            var resolved = scope.ServiceProvider.GetService(descriptor.ServiceType);

            Assert.True(resolved is not null, $"{descriptor.ServiceType} failed to resolve.");
        }
    }

    /// <summary>Same fake-substitution setup every full-graph test in this file uses (native audio
    /// context, settings.json, and history.db all substituted so resolving the graph never touches
    /// real hardware/disk state on this developer/CI machine) -- extracted here since the two tests
    /// above need it identically and don't otherwise care about a specific settings VALUE the way
    /// e.g. <see cref="MainViewModel_LoadOperatorSettingsAsync_ReflectsLaterSettingsChange"/> does.</summary>
    private static ServiceCollection BuildServicesWithFakes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        Program.RegisterServices(services);
        var staticSettingsStore = new StaticSettingsStore(new AppSettings());
        services.AddSingleton<ISettingsStore>(staticSettingsStore);
        services.AddSingleton<ISettingsFileRelocator>(staticSettingsStore);
        services.AddSingleton<IAudioEngine>(new FakeAudioEngine());
        services.AddSingleton<IAudioDeviceEnumerator>(new NullAudioDeviceEnumerator());
        services.AddSingleton<IAudioDeviceMuteQuery>(new FakeAudioDeviceMuteQuery());
        services.AddSingleton<IReceiveHistoryStore>(new NullReceiveHistoryStore());
        return services;
    }

    [Fact]
    public async Task MainViewModel_LoadOperatorSettingsAsync_ReflectsLaterSettingsChange()
    {
        // User-reported bug (2026-08-23): MainViewModel.Callsign (backs the header-row callsign
        // chip) only ever loaded once, from the constructor's own fire-and-forget call -- typing a
        // new callsign in Options and hitting Save persisted it correctly, but the chip kept
        // showing the old value until the next full app restart. LoadOperatorSettingsAsync (renamed
        // from LoadCallsignAsync, RST default plan 2026-09-01, when it was extended to also cache
        // DefaultRst) is public and re-callable (MainWindow.axaml.cs calls it again once the Options
        // window closes) -- this proves the re-call actually reflects a settings change, not just
        // that it doesn't throw. Same DI-substitution setup as RegisterServices_ResolvesEveryServiceMainActuallyRequiresAtStartup
        // above, since MainViewModel needs the full composition root to construct.
        var settingsStore = new StaticSettingsStore(new AppSettings());
        var services = new ServiceCollection();
        services.AddLogging();
        Program.RegisterServices(services);
        services.AddSingleton<ISettingsStore>(settingsStore);
        services.AddSingleton<ISettingsFileRelocator>(settingsStore);
        services.AddSingleton<IAudioEngine>(new FakeAudioEngine());
        services.AddSingleton<IAudioDeviceEnumerator>(new NullAudioDeviceEnumerator());
        services.AddSingleton<IAudioDeviceMuteQuery>(new FakeAudioDeviceMuteQuery());
        // Substituted (plan-review, test-suite fixes phase 1, item 4) -- CreateSstvSessionService's
        // real factory (Program.cs:880) eagerly calls IReceiveHistoryStore.GetAudioSettingsAsync at
        // DI resolve time; without this, resolving MainViewModel/ISstvSessionService in any test here
        // constructs the REAL SqliteReceiveHistoryStore against this developer/CI machine's actual
        // history.db, same shadowing hazard the ISettingsStore substitution above already guards
        // against for settings.json.
        services.AddSingleton<IReceiveHistoryStore>(new NullReceiveHistoryStore());
        await using var provider = services.BuildServiceProvider();
        var mainViewModel = provider.GetRequiredService<MainViewModel>();

        // Awaiting this directly (not the constructor's own separate fire-and-forget call) gives a
        // deterministic completion point regardless of that background task's own timing.
        await mainViewModel.LoadOperatorSettingsAsync();
        Assert.Null(mainViewModel.Callsign);
        // RST default plan (2026-09-01): "595" here, not null -- OptionsSettingsService.LoadAsync's
        // own "?? DefaultRstFallback" applies even with no OperatorSettings section on disk at all.
        Assert.Equal(OperatorSettings.DefaultRstFallback, mainViewModel.DefaultRst);

        settingsStore.Settings = new AppSettings().WithSection(
            OperatorSettings.SectionKey,
            new OperatorSettings { Callsign = "PD3AN", DefaultRst = "579" },
            OperatorSettingsJsonContext.Default.OperatorSettings);
        await mainViewModel.LoadOperatorSettingsAsync();

        Assert.Equal("PD3AN", mainViewModel.Callsign);
        // Auditor code-review finding: DefaultRst's own re-read was untested here -- if the
        // assignment inside LoadOperatorSettingsAsync ever regressed, vm.DefaultRst would stay null
        // and MainWindow.axaml.cs's own "?? DefaultRstFallback" fallback would silently mask it for
        // every operator, not just ones on a fresh install.
        Assert.Equal("579", mainViewModel.DefaultRst);
    }

    [Fact]
    public async Task MainViewModel_OpenOnGitHubCommand_OpensTheRepositoryUrl()
    {
        // Pins the literal URL, not just "it calls Open at all" -- a typo'd repo URL would
        // otherwise ship silently, same reasoning as this codebase's other "assert the exact
        // value, not just non-null" tests (e.g. the WAV/image file-picker suggested-name tests).
        var urlLauncher = new FakeUrlLauncher();
        var services = new ServiceCollection();
        services.AddLogging();
        Program.RegisterServices(services);
        var staticSettingsStore = new StaticSettingsStore(new AppSettings());
        services.AddSingleton<ISettingsStore>(staticSettingsStore);
        services.AddSingleton<ISettingsFileRelocator>(staticSettingsStore);
        services.AddSingleton<IAudioEngine>(new FakeAudioEngine());
        services.AddSingleton<IAudioDeviceEnumerator>(new NullAudioDeviceEnumerator());
        services.AddSingleton<IAudioDeviceMuteQuery>(new FakeAudioDeviceMuteQuery());
        // Substituted (plan-review, test-suite fixes phase 1, item 4) -- CreateSstvSessionService's
        // real factory (Program.cs:880) eagerly calls IReceiveHistoryStore.GetAudioSettingsAsync at
        // DI resolve time; without this, resolving MainViewModel/ISstvSessionService in any test here
        // constructs the REAL SqliteReceiveHistoryStore against this developer/CI machine's actual
        // history.db, same shadowing hazard the ISettingsStore substitution above already guards
        // against for settings.json.
        services.AddSingleton<IReceiveHistoryStore>(new NullReceiveHistoryStore());
        services.AddSingleton<ScanlineStudio.UI.Services.IUrlLauncher>(urlLauncher);
        await using var provider = services.BuildServiceProvider();
        var mainViewModel = provider.GetRequiredService<MainViewModel>();

        mainViewModel.OpenOnGitHubCommand.Execute(null);

        Assert.Equal(["https://github.com/Ar0xA/Scanline-Studio"], urlLauncher.OpenedUrls);
    }

    [Fact]
    public async Task MainViewModel_OpenApplicationLogCommand_OpensTheSameDirectoryTheLoggerWritesTo()
    {
        // Pins that this opens AppLogPaths.LogDirectory specifically -- the single source of truth
        // Program.cs's own logger configuration also reads from, not a second, independently
        // hand-typed path that could silently drift from where the log file actually lands.
        var urlLauncher = new FakeUrlLauncher();
        var services = new ServiceCollection();
        services.AddLogging();
        Program.RegisterServices(services);
        var staticSettingsStore = new StaticSettingsStore(new AppSettings());
        services.AddSingleton<ISettingsStore>(staticSettingsStore);
        services.AddSingleton<ISettingsFileRelocator>(staticSettingsStore);
        services.AddSingleton<IAudioEngine>(new FakeAudioEngine());
        services.AddSingleton<IAudioDeviceEnumerator>(new NullAudioDeviceEnumerator());
        services.AddSingleton<IAudioDeviceMuteQuery>(new FakeAudioDeviceMuteQuery());
        // Substituted (plan-review, test-suite fixes phase 1, item 4) -- CreateSstvSessionService's
        // real factory (Program.cs:880) eagerly calls IReceiveHistoryStore.GetAudioSettingsAsync at
        // DI resolve time; without this, resolving MainViewModel/ISstvSessionService in any test here
        // constructs the REAL SqliteReceiveHistoryStore against this developer/CI machine's actual
        // history.db, same shadowing hazard the ISettingsStore substitution above already guards
        // against for settings.json.
        services.AddSingleton<IReceiveHistoryStore>(new NullReceiveHistoryStore());
        services.AddSingleton<ScanlineStudio.UI.Services.IUrlLauncher>(urlLauncher);
        await using var provider = services.BuildServiceProvider();
        var mainViewModel = provider.GetRequiredService<MainViewModel>();

        mainViewModel.OpenApplicationLogCommand.Execute(null);

        Assert.Equal([AppLogPaths.LogDirectory], urlLauncher.OpenedUrls);
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

    /// <summary>Substituted so resolving the full composition root never touches this developer/CI
    /// machine's real <c>history.db</c> (see the substitution's own call-site comment). Every member
    /// no-ops or returns an empty/default result -- no test in this file drives RX history behavior,
    /// only construction.</summary>
    private sealed class NullReceiveHistoryStore : IReceiveHistoryStore
    {
        public event Action<ReceiveHistoryEntry>? Recorded;

        public event Action<ReceiveHistoryEntry>? Deleted;

        // Satisfies the interface without leaving either event entirely dead (CS0067) -- neither is
        // ever raised here, same established convention as this codebase's other minimal fakes
        // (e.g. FakeReceiveHistoryStoreForLogbook).
        public void RaiseRecorded(ReceiveHistoryEntry entry) => Recorded?.Invoke(entry);

        public void RaiseDeleted(ReceiveHistoryEntry entry) => Deleted?.Invoke(entry);

        public Task<IReadOnlyList<ReceiveHistoryEntry>> QueryAsync(ReceiveHistoryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ReceiveHistoryEntry>>([]);

        public Task<IImageSource> LoadThumbnailAsync(ReceiveHistoryEntry entry, int maxDimension, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RecordAsync(ReceiveHistoryEntry entry, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetImagesDirectoryAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);

        public Task SetImagesDirectoryAsync(string? directory, CancellationToken ct = default) => Task.CompletedTask;

        public Task<AudioAutoSaveSettings> GetAudioSettingsAsync(CancellationToken ct = default) =>
            Task.FromResult(new AudioAutoSaveSettings(false, string.Empty));

        public Task SetAudioSettingsAsync(bool enabled, string? directory, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> SetAudioFilePathAsync(string entryId, string path, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> SetNoteAsync(string entryId, string? note, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> SetFlaggedAsync(string entryId, bool isFlagged, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> SetLinkedQsoIdAsync(string entryId, string qsoId, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<int> ClearLinkedQsoIdAsync(string qsoId, CancellationToken ct = default) => Task.FromResult(0);

        public Task<bool> DeleteAsync(ReceiveHistoryEntry entry, CancellationToken ct = default) => Task.FromResult(false);

        public Task<int> ReconcileWithDiskAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class FakeUrlLauncher : ScanlineStudio.UI.Services.IUrlLauncher
    {
        public List<string> OpenedUrls { get; } = [];

        public void Open(string url) => OpenedUrls.Add(url);
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
                    // Options stub backlog item 1 -- non-default values, same "no assertion can pass
                    // vacuously against a parameter default" discipline as every other field here.
                    PllVcoGain = 2.5,
                    PllLoopOrder = 6,
                    PllLoopCutoffHz = 1300,
                    PllOutputOrder = 8,
                    PllOutputCutoffHz = 850,
                    // Options stub backlog item 2 -- same non-default discipline.
                    ZeroCrossingSmoothingMode = ZeroCrossingSmoothingMode.Fir,
                    ZeroCrossingOutputOrder = 9,
                    ZeroCrossingOutputCutoffHz = 700,
                    ZeroCrossingSmoothingFrequencyHz = 3000,
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
        var pllTuning = decoder.InnerPllTuningForTests;
        Assert.Equal(2.5, pllTuning.VcoGain);
        Assert.Equal(6, pllTuning.LoopOrder);
        Assert.Equal(1300, pllTuning.LoopCutoffHz);
        Assert.Equal(8, pllTuning.OutputOrder);
        Assert.Equal(850, pllTuning.OutputCutoffHz);
        var zeroCrossingTuning = decoder.InnerZeroCrossingTuningForTests;
        Assert.Equal(ZeroCrossingSmoothingMode.Fir, zeroCrossingTuning.SmoothingMode);
        Assert.Equal(9, zeroCrossingTuning.OutputOrder);
        Assert.Equal(700, zeroCrossingTuning.OutputCutoffHz);
        Assert.Equal(3000, zeroCrossingTuning.SmoothingFrequencyHz);
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

    // Round-3 plan-review risk-A: also implements ISettingsFileRelocator (a trivial no-op -- none of
    // these tests ever drives a Config-directory Apply) so BOTH interfaces substitute together at
    // every call site that registers this fake. Registering only ISettingsStore would leave
    // ISettingsFileRelocator resolving to the REAL JsonSettingsStore singleton -- a SEPARATE instance
    // constructed against this machine's actual location-overrides.json/config directory, exactly
    // the shadowing hazard the round-2 comment above documents one level deeper (harmless today since
    // no test here drives Apply, but a real hazard for any future test that does).
    private sealed class StaticSettingsStore(AppSettings settings) : ISettingsStore, ISettingsFileRelocator
    {
        // Settable (not the ctor param directly) so a test can simulate a settings change between
        // two LoadAsync calls -- e.g. MainViewModel_LoadOperatorSettingsAsync_ReflectsLaterSettingsChange
        // below, which mutates this between two calls to simulate what a real Options-dialog Save
        // does to the on-disk settings.
        public AppSettings Settings { get; set; } = settings;

        public IObservable<AppSettings> Changes { get; } = new NeverObservable();

        public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(Settings);

        public Task SaveAsync(AppSettings updatedSettings, CancellationToken ct = default) =>
            throw new NotSupportedException();

        // T0-2: naive forwarding still funnels through this class's own SaveAsync, which always
        // throws -- preserves the "this store never actually saves" contract automatically.
        public async Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default)
        {
            var current = await LoadAsync(ct).ConfigureAwait(false);
            var updated = mutate(current);
            await SaveAsync(updated, ct).ConfigureAwait(false);
            return updated;
        }

        public Task<(bool Moved, string PreviousDirectory)> RelocateAsync(string newDirectory, CancellationToken ct = default) =>
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
