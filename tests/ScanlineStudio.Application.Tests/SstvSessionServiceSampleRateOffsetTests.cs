using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

/// <summary>
/// Stub survey Tier 3, "Clock calibration" piece 1: <see cref="SstvSessionService.TransmitAsync"/>'s
/// resolution of <see cref="AudioDeviceSettings.TxSampleRateOffsetHz"/> into the value passed to the
/// encoder -- settings-boundary validation (round-2 plan-review blocker: an out-of-range value
/// must fall back to 0.0, not reach the encoder; the read site's NaN-safety is real but not
/// independently testable here, see <c>TransmitAsync_OutOfRangeOffsetConfigured_FallsBackToZero</c>'s
/// own doc comment for why) and the requirement that
/// <see cref="ISstvEncoder.EstimateSampleCount"/>/<see cref="ISstvEncoder.EncodeAsync"/> both
/// receive the exact SAME resolved value, not two independent reads. Verified via
/// <see cref="FakeSstvEncoder.LastSampleRateOffsetHz"/>/<see cref="FakeSstvEncoder.LastEstimateSampleRateOffsetHz"/>
/// rather than decoding real audio -- the DSP-level math itself is covered by
/// <c>AnalogFmSstvEncoderSampleRateOffsetTests</c> in <c>ScanlineStudio.Core.Sstv.Tests</c>; this
/// only needs to prove the Application-layer resolution logic feeds the right value in.
/// </summary>
public sealed class SstvSessionServiceSampleRateOffsetTests
{
    private static readonly SstvModeDefinition TestMode = new(
        Id: "test",
        DisplayName: "Test",
        VisCode: 0,
        ImageWidth: 1,
        ImageHeight: 1,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments: []);

    private static readonly IImageSource TestImage = new ArrayImageSource(1, 1, new Rgb24[1]);

    private static (SstvSessionService Service, FakeSstvEncoder Encoder, FakeSettingsStore SettingsStore) CreateService()
    {
        var audioEngine = new FakeAudioEngine();
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [8000])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [11025])],
        };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = "capture-1", PlaybackDeviceId = "playback-1", SampleRate = 8000 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var encoder = new FakeSstvEncoder();

        var service = new SstvSessionService(
            audioEngine, deviceEnumerator, new FakeAudioDeviceMuteQuery(), settingsStore, new FakeSstvDecoder(), encoder, new MacroTextResolver(),
            new FakeWaterfallSource(), new FakeReceivedImageBuffer(), new FakeRadioSessionService(), NullLogger<SstvSessionService>.Instance);
        return (service, encoder, settingsStore);
    }

    private static void WithTxSampleRateOffsetHz(FakeSettingsStore store, double offsetHz)
    {
        var current = store.Settings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings)
            ?? new AudioDeviceSettings();
        store.Settings = store.Settings.WithSection(
            AudioDeviceSettings.SectionKey,
            current with { TxSampleRateOffsetHz = offsetHz },
            AudioSettingsJsonContext.Default.AudioDeviceSettings);
    }

    [Fact]
    public async Task TransmitAsync_NoOffsetConfigured_PassesZeroToTheEncoder()
    {
        var (service, encoder, _) = CreateService();

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(0.0, encoder.LastSampleRateOffsetHz);
        Assert.Equal(0.0, encoder.LastEstimateSampleRateOffsetHz);
    }

    [Theory]
    [InlineData(1500.0)]
    [InlineData(-1500.0)]
    [InlineData(37.5)]
    public async Task TransmitAsync_ValidOffsetConfigured_PassesItThroughUnchanged(double offsetHz)
    {
        var (service, encoder, settingsStore) = CreateService();
        WithTxSampleRateOffsetHz(settingsStore, offsetHz);

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(offsetHz, encoder.LastSampleRateOffsetHz);
        Assert.Equal(offsetHz, encoder.LastEstimateSampleRateOffsetHz);
    }

    [Theory]
    [InlineData(1500.01)]
    [InlineData(-1500.01)]
    [InlineData(50000.0)]
    public async Task TransmitAsync_OutOfRangeOffsetConfigured_FallsBackToZero(double corruptOffsetHz)
    {
        // Round-2 plan-review blocker: a corrupted/hand-edited settings.json outside legacy's own
        // accepted +/-1500Hz manual range (Option.cpp:1142-1146) must not reach the encoder.
        // NaN/+-Infinity are NOT separately covered here: this project's JSON serialization does
        // not enable JsonNumberHandling.AllowNamedFloatingPointLiterals anywhere (confirmed by
        // grep), so those values cannot round-trip through a real settings.json at all -- STJ
        // itself throws ArgumentException on write, before this ever reaches read-side validation.
        // The `is >= x and <= y` pattern at the read site is still NaN-safe by construction (same
        // free defensive property CwToneFrequencyHz's own precedent has), just not independently
        // testable through this real-settings-store harness.
        var (service, encoder, settingsStore) = CreateService();
        WithTxSampleRateOffsetHz(settingsStore, corruptOffsetHz);

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(0.0, encoder.LastSampleRateOffsetHz);
        Assert.Equal(0.0, encoder.LastEstimateSampleRateOffsetHz);
    }

    [Fact]
    public async Task TransmitAsync_EstimateAndRealEncodeReceiveTheExactSameResolvedOffset()
    {
        // Plan-review finding (round 2), same requirement as the pre-existing stationId reuse
        // requirement: a settings change mid-resolution must not let the estimate and the real
        // encode see different effective rates.
        var (service, encoder, settingsStore) = CreateService();
        WithTxSampleRateOffsetHz(settingsStore, 1500.0);

        await service.TransmitAsync(TestMode, TestImage);

        Assert.NotNull(encoder.LastEstimateSampleRateOffsetHz);
        Assert.Equal(encoder.LastEstimateSampleRateOffsetHz, encoder.LastSampleRateOffsetHz);
    }

    [Fact]
    public async Task GetStationIdTransmitOptionsAsync_DoesNotRequireTxSampleRateOffsetHzToBeValid()
    {
        // The read-only station-ID preview shares the same underlying settings resolution (one
        // load, not two) -- an out-of-range offset must not make this unrelated preview throw.
        var (service, _, settingsStore) = CreateService();
        WithTxSampleRateOffsetHz(settingsStore, 50000.0);

        var resolved = await service.GetStationIdTransmitOptionsAsync();

        Assert.NotNull(resolved);
    }
}
