using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Cw;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Cw;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>fsk_cwid.md §10/B-P3's own planned deliverable: sibling of
/// <see cref="StationIdEndToEndTests"/>, but exercising the REAL <see cref="SstvSessionService"/> --
/// unlike that suite (which raises decoder events directly into <see cref="RxImagePaneViewModel"/>
/// via <see cref="FakeSstvSessionService"/>, bypassing the session layer entirely), the CW-ID
/// capture-window arm/close state machine lives in <c>SstvSessionService.CwId.cs</c>, not the
/// decoder, so proving the whole chain needs the real session in the loop: real
/// <see cref="AnalogFmSstvEncoder"/> (FSK-ID + CW-ID) -&gt; real <see cref="AnalogFmSstvDecoder"/> ->
/// real <see cref="SstvSessionService"/> arm/capture -&gt; real <see cref="ClassicalCwDecoder"/> ->
/// real <see cref="RxImagePaneViewModel"/>. "Proves the whole chain once; keep it to one test"
/// (fsk_cwid.md §10's own instruction) -- one test, not a suite.</summary>
public sealed class CwIdEndToEndTests
{
    private const int SampleRate = 11025;

    [AvaloniaFact]
    public async Task RealEncodeThenRealSessionArmThenRealCwDecode_FillsBothTheFskAndCwIdRows()
    {
        var mode = SstvModeRegistry.Mn73;
        var image = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, Enumerable.Repeat(new Rgb24(128, 64, 200), mode.ImageWidth * mode.ImageHeight).ToArray());
        var stationId = new StationIdTransmitOptions
        {
            FskIdEnabled = true,
            Callsign = "W1AW",
            CwEnabled = true,
            CwResolvedText = "DE W1AW",
            CwWpm = 28, // fast -- keeps the CW-ID audio (and this test) short
            CwToneFrequencyHz = 800,
        };
        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var encoded = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, image, stationId))
        {
            encoded.Add(sample);
        }

        // Trailing padding: the CW-ID capture window is SAMPLE-COUNT-driven from the real,
        // lag-corrected image-end origin (fsk_cwid.md §8.2), not a wall-clock timer -- so the window
        // can close within one bulk push as long as enough trailing samples follow the real CW-ID
        // audio the encoder already emitted. Uses StationIdSettings.MinCwIdRxWindowSeconds (the
        // window this test configures below) plus a margin for the FSK-ID/CW-ID audio itself, which
        // also falls inside the post-image window the arm measures from.
        var windowSamples = StationIdSettings.MinCwIdRxWindowSeconds * SampleRate;
        var padded = new List<float>(encoded);
        padded.AddRange(new float[windowSamples + SampleRate]);

        var audioEngine = new FakeAudioEngine();
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [SampleRate])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [SampleRate])],
        };
        var deviceMuteQuery = new FakeAudioDeviceMuteQuery();
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings()
                .WithSection(AudioDeviceSettings.SectionKey, new AudioDeviceSettings { CaptureDeviceId = "capture-1", PlaybackDeviceId = "playback-1", SampleRate = SampleRate }, AudioSettingsJsonContext.Default.AudioDeviceSettings)
                // Auditor code-review finding: StartReceivingAsync unconditionally overwrites
                // ISstvDecoder.StationIdDecodeEnabled from FskIdRxEnabled (defaulting to false),
                // clobbering the decoder-level flag this test sets directly below -- without this,
                // StationIdDecoded never fires and OverrideCallsign is silently satisfied by the CW
                // path instead of FSK, defeating this test's whole "assert both rows" point.
                .WithSection(StationIdSettings.SectionKey, new StationIdSettings { FskIdRxEnabled = true, CwIdRxEnabled = true, CwIdRxWindowSeconds = StationIdSettings.MinCwIdRxWindowSeconds }, StationIdSettingsJsonContext.Default.StationIdSettings),
        };
        var decoder = new AnalogFmSstvDecoder(SampleRate) { StationIdDecodeEnabled = true };
        var receivedImage = new FakeReceivedImageBuffer();

        var session = new SstvSessionService(
            audioEngine, deviceEnumerator, deviceMuteQuery, settingsStore, decoder, new NoOpSstvEncoder(),
            new MacroTextResolver(), new FakeWaterfallSource(), receivedImage, new NoOpRadioSessionService(),
            new ClassicalCwDecoder(), NullLogger<SstvSessionService>.Instance);

        var vm = new RxImagePaneViewModel(session, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        var cwIdDecodedTask = new TaskCompletionSource<CwIdDecodedInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.CwIdDecoded += info => cwIdDecodedTask.TrySetResult(info);

        await session.StartReceivingAsync();
        audioEngine.PushCapturedSamples(padded.ToArray());

        var cwInfo = await cwIdDecodedTask.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("W1AW", cwInfo.Callsign);
        Assert.Equal("W1AW", vm.OverrideCallsign);
        Assert.Contains("W1AW", vm.CwIdDisplay);
    }

    /// <summary>Minimal <see cref="IRadioSessionService"/> stand-in -- this test never transmits or
    /// touches CAT, so every member is either a safe default or throws (never expected to be
    /// called). Not <see cref="ScanlineStudio.Application.Tests"/>'s own <c>FakeRadioSessionService</c>
    /// (internal to that assembly, and carries a large test-hook surface this RX-only test doesn't
    /// need).</summary>
    private sealed class NoOpRadioSessionService : IRadioSessionService
    {
        public RadioState? LastKnownState => null;

        public RadioCapabilities Capabilities => RadioCapabilities.None;

        public string RigId => "none";

        public bool IsGenuinelyConnected => false;

        public IObservable<RadioState> StateChanges { get; } = System.Reactive.Linq.Observable.Never<RadioState>();

        public IObservable<RadioConnectionEvent> ConnectionEvents { get; } = System.Reactive.Linq.Observable.Never<RadioConnectionEvent>();

        public Task ConnectUsingSettingsAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<RadioConnectionTestResult> TestConnectionAsync(RadioConnectionSpec spec, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RadioConnectionTestResult> TestPttAsync(RadioConnectionSpec spec, TimeSpan duration, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<HamlibLibraryReloadResult?> RequestHamlibLibraryPathAsync(string? overridePath, CancellationToken ct = default) => Task.FromResult<HamlibLibraryReloadResult?>(null);

        public Task DisconnectAsync() => Task.CompletedTask;

        public Task SetFrequencyAsync(long hz, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetModeAsync(RadioMode mode, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetPttAsync(bool tx, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetBandwidthAsync(int? bandwidthHz, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<FrequencyPreset>> GetFrequencyPresetsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FrequencyPreset>>([]);

        public Task SaveFrequencyPresetsAsync(IReadOnlyList<FrequencyPreset> presets, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RadioSafetySpec> GetSafetySettingsAsync(CancellationToken ct = default) => Task.FromResult(new RadioSafetySpec(false, RadioSafetySpec.DefaultSwrCutoffThreshold));

        public Task SaveSafetySettingsAsync(RadioSafetySpec spec, CancellationToken ct = default) => Task.CompletedTask;

        public event Action<RadioSafetySpec>? SafetySettingsChanged { add { } remove { } }
    }

    /// <summary>Minimal <see cref="ISstvEncoder"/> stand-in for the session's own TX slot -- this
    /// test never transmits through the session (the test's TX audio is produced by a SEPARATE,
    /// real <see cref="AnalogFmSstvEncoder"/> instance, fed directly into the session's capture
    /// path), so every encode member is unreachable and throws.</summary>
    private sealed class NoOpSstvEncoder : ISstvEncoder
    {
        public int SampleRate => CwIdEndToEndTests.SampleRate;

        public IAsyncEnumerable<float> EncodeAsync(SstvModeDefinition mode, IImageSource image, StationIdTransmitOptions? stationId = null, double sampleRateOffsetHz = 0.0, bool txBpfEnabled = true, int txBpfTapCount = 24, bool txLpfEnabled = false, double txLpfFrequencyHz = 2000.0, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ReadOnlyMemory<float>> EncodeBatchedAsync(SstvModeDefinition mode, IImageSource image, StationIdTransmitOptions? stationId = null, double sampleRateOffsetHz = 0.0, bool txBpfEnabled = true, int txBpfTapCount = 24, bool txLpfEnabled = false, double txLpfFrequencyHz = 2000.0, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public long EstimateSampleCount(SstvModeDefinition mode, IImageSource image, StationIdTransmitOptions? stationId = null, double sampleRateOffsetHz = 0.0) =>
            throw new NotSupportedException();
    }
}
