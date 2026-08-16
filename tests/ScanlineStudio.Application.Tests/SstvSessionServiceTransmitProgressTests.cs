using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

/// <summary>
/// spec/18-path-to-1.0.md Medium item: "No TX send-progress feedback during transmit." Covers
/// <see cref="ISstvSessionService.TransmitProgressChanged"/>'s raise-site behavior specifically --
/// <see cref="AnalogFmSstvEncoderEstimateSampleCountTests"/>-equivalent exactness coverage for the
/// underlying sample-count math lives in <c>ScanlineStudio.Core.Sstv.Tests</c> instead.
/// </summary>
public sealed class SstvSessionServiceTransmitProgressTests
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

    private static (SstvSessionService Service, FakeAudioEngine AudioEngine, FakeSstvEncoder Encoder)
        CreateService(float[]? samplesToYield = null, long? estimateOverride = null)
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
        var decoder = new FakeSstvDecoder();
        var encoder = new FakeSstvEncoder();
        if (samplesToYield is not null)
        {
            encoder = new FakeSstvEncoder { SamplesToYield = samplesToYield };
        }

        if (estimateOverride is not null)
        {
            encoder = new FakeSstvEncoder { SamplesToYield = samplesToYield ?? encoder.SamplesToYield, EstimateSampleCountOverride = estimateOverride };
        }

        var waterfall = new FakeWaterfallSource();
        var receivedImage = new FakeReceivedImageBuffer();
        var radioSession = new FakeRadioSessionService();

        var service = new SstvSessionService(audioEngine, deviceEnumerator, settingsStore, decoder, encoder, new MacroTextResolver(), waterfall, receivedImage, radioSession, NullLogger<SstvSessionService>.Instance);
        return (service, audioEngine, encoder);
    }

    [Fact]
    public async Task TransmitAsync_RaisesTransmitProgressChanged_ReachingFractionOne()
    {
        var (service, _, _) = CreateService(); // default 3-sample fake output, default estimate = 3

        var reports = new List<TransmitProgressInfo>();
        service.TransmitProgressChanged += reports.Add;

        await service.TransmitAsync(TestMode, TestImage);

        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1].Fraction);
    }

    [Fact]
    public async Task TransmitAsync_ProgressFractionIsMonotonicallyIncreasing_AcrossMultipleChunks()
    {
        // 10000 samples > the pump loop's own 4096-sample chunk size -- forces at least 2 progress
        // reports (one mid-chunk, one for the trailing partial chunk), proving this isn't just a
        // single "0 then 1" report that would pass even with a broken running counter.
        var samples = new float[10_000];
        var (service, _, _) = CreateService(samplesToYield: samples);

        var reports = new List<TransmitProgressInfo>();
        service.TransmitProgressChanged += reports.Add;

        await service.TransmitAsync(TestMode, TestImage);

        Assert.True(reports.Count >= 2, $"expected at least 2 progress reports for {samples.Length} samples, got {reports.Count}");
        for (var i = 1; i < reports.Count; i++)
        {
            Assert.True(reports[i].Fraction >= reports[i - 1].Fraction, "fraction must never go backward");
        }

        Assert.Equal(1.0, reports[^1].Fraction);
        Assert.Equal(samples.Length / 11025.0, reports[^1].Elapsed.TotalSeconds, precision: 6);
        Assert.Equal(reports[^1].Elapsed, reports[^1].EstimatedTotal);
    }

    [Fact]
    public async Task TransmitAsync_EstimateOfZero_NeverRaisesTransmitProgressChanged()
    {
        // A degenerate/zero estimate must not divide by zero into a NaN fraction reaching a real
        // subscriber -- the guard skips reporting entirely rather than emitting garbage.
        var (service, _, _) = CreateService(estimateOverride: 0);

        var reports = new List<TransmitProgressInfo>();
        service.TransmitProgressChanged += reports.Add;

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Empty(reports);
    }

    [Fact]
    public async Task TransmitAsync_ThrowingProgressSubscriber_DoesNotAbortTheTransmission()
    {
        // Code-review finding: the default 3-sample fixture only ever raises ONE report, fired
        // AFTER every sample is already enqueued -- that proves non-propagation but not that pumping
        // actually CONTINUES past a mid-stream throwing report. 10,000 samples forces multiple
        // reports across the pump loop's own chunk boundary, so a throw on an EARLY report has a
        // real chance to abort the rest of the pump if the guard were broken.
        var samples = new float[10_000];
        var (service, audioEngine, _) = CreateService(samplesToYield: samples);
        service.TransmitProgressChanged += _ => throw new InvalidOperationException("subscriber bug");

        await service.TransmitAsync(TestMode, TestImage);

        Assert.False(audioEngine.IsPlaying);
        Assert.Equal(samples.Length, audioEngine.PlaybackSamples.Count);
    }

    [Fact]
    public async Task TransmitAsync_PassesTheSameResolvedStationIdToBothEstimateAndEncode()
    {
        // Pins the plan-review requirement (SstvSessionService.cs's own comment at the
        // EstimateSampleCount call site): TransmitAsync must resolve station-ID settings ONCE and
        // reuse that exact instance for both calls, never re-resolve independently -- a regression
        // here wouldn't be caught by any assertion on the reported fraction alone, since
        // FakeSstvEncoder's EstimateSampleCount ignores its stationId parameter for the size
        // computation itself.
        var (service, _, encoder) = CreateService();

        await service.TransmitAsync(TestMode, TestImage);

        Assert.NotNull(encoder.LastEstimateStationIdOptions);
        Assert.Same(encoder.LastEstimateStationIdOptions, encoder.LastStationIdOptions);
    }

    [Fact]
    public async Task TuneAsync_NeverRaisesTransmitProgressChanged()
    {
        var (service, _, _) = CreateService();
        var reports = new List<TransmitProgressInfo>();
        service.TransmitProgressChanged += reports.Add;

        await service.TuneAsync(1000, TimeSpan.FromMilliseconds(50));

        Assert.Empty(reports);
    }
}
