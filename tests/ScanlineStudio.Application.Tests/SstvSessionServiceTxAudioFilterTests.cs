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
/// Options stub backlog item 3 (docs/plans/options-stub-item3-tx-bpf-lpf-plan.md):
/// <see cref="SstvSessionService.TransmitAsync"/>'s resolution of
/// <see cref="AudioDeviceSettings.TxBpfEnabled"/>/<see cref="AudioDeviceSettings.TxBpfTapCount"/>/
/// <see cref="AudioDeviceSettings.TxLpfEnabled"/>/<see cref="AudioDeviceSettings.TxLpfFrequencyHz"/>
/// into the values passed to the encoder -- mirrors
/// <see cref="SstvSessionServiceSampleRateOffsetTests"/>'s own exact shape for the sibling
/// <c>TxSampleRateOffsetHz</c> field. Code-review round 1 finding: without this file, the new
/// settings-resolution clamp/fallback block in <c>ResolveTransmitSettingsAsync</c> had zero
/// assertions anywhere -- <see cref="FakeSstvEncoder"/>'s own new tracking fields were written but
/// never read by any test, the same shape as item 2's own shipped finding one file over
/// (<c>SstvSessionServiceSampleRateOffsetTests</c> already gets this right for the sibling field).
/// </summary>
public sealed class SstvSessionServiceTxAudioFilterTests
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
            new FakeWaterfallSource(), new FakeReceivedImageBuffer(), new FakeRadioSessionService(), new FakeCwIdDecoder(), NullLogger<SstvSessionService>.Instance);
        return (service, encoder, settingsStore);
    }

    private static void WithTxAudioFilterSettings(FakeSettingsStore store, AudioDeviceSettings updated)
    {
        var current = store.Settings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings)
            ?? new AudioDeviceSettings();
        store.Settings = store.Settings.WithSection(
            AudioDeviceSettings.SectionKey,
            current with
            {
                TxBpfEnabled = updated.TxBpfEnabled,
                TxBpfTapCount = updated.TxBpfTapCount,
                TxLpfEnabled = updated.TxLpfEnabled,
                TxLpfFrequencyHz = updated.TxLpfFrequencyHz,
            },
            AudioSettingsJsonContext.Default.AudioDeviceSettings);
    }

    [Fact]
    public async Task TransmitAsync_NoTxAudioFilterSettingsConfigured_PassesLegacyDefaultsToTheEncoder()
    {
        var (service, encoder, _) = CreateService();

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(true, encoder.LastTxBpfEnabled);
        Assert.Equal(24, encoder.LastTxBpfTapCount);
        Assert.Equal(false, encoder.LastTxLpfEnabled);
        Assert.Equal(2000.0, encoder.LastTxLpfFrequencyHz);
    }

    [Fact]
    public async Task TransmitAsync_ValidTxAudioFilterSettingsConfigured_PassesThemThroughUnchanged()
    {
        var (service, encoder, settingsStore) = CreateService();
        WithTxAudioFilterSettings(settingsStore, new AudioDeviceSettings { TxBpfEnabled = false, TxBpfTapCount = 48, TxLpfEnabled = true, TxLpfFrequencyHz = 2500.0 });

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(false, encoder.LastTxBpfEnabled);
        Assert.Equal(48, encoder.LastTxBpfTapCount);
        Assert.Equal(true, encoder.LastTxLpfEnabled);
        Assert.Equal(2500.0, encoder.LastTxLpfFrequencyHz);
    }

    [Theory]
    [InlineData(1, 2)] // below TAPMAX-range floor -> clamped up to 2
    [InlineData(513, 512)] // above TAPMAX -> clamped down to 512
    [InlineData(25, 24)] // odd, in range -> rounded down to the nearest even value
    public async Task TransmitAsync_OutOfRangeOrOddTapCountConfigured_ClampsAndRoundsToEven(int rawTapCount, int expectedTapCount)
    {
        // Real legacy Save-handler range (Option.cpp:456-459/TAPMAX) plus the round-to-even clamp
        // (see TxOutputBandpassFilter's own doc comment for why odd is legacy UB).
        var (service, encoder, settingsStore) = CreateService();
        WithTxAudioFilterSettings(settingsStore, new AudioDeviceSettings { TxBpfTapCount = rawTapCount });

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(expectedTapCount, encoder.LastTxBpfTapCount);
    }

    [Theory]
    [InlineData(99.9)] // below the real legacy floor (Option.cpp:452-459)
    [InlineData(3000.1)] // above the real legacy ceiling
    public async Task TransmitAsync_OutOfRangeLpfFrequencyConfigured_FallsBackToLegacyDefault(double corruptFrequencyHz)
    {
        var (service, encoder, settingsStore) = CreateService();
        WithTxAudioFilterSettings(settingsStore, new AudioDeviceSettings { TxLpfFrequencyHz = corruptFrequencyHz });

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(2000.0, encoder.LastTxLpfFrequencyHz);
    }
}
