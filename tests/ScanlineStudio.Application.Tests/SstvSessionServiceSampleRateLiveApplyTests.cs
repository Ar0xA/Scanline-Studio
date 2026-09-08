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
/// Restart-required-settings backlog item 4 (2026-08-27): <see cref="SstvSessionService.RequestSampleRateAsync"/>
/// orchestrates a live sample-rate change across the decoder, encoder, and waterfall -- the highest-
/// value tests per the implementation plan, since this is where the stop-capture -> commit ->
/// reopen-capture sequencing (and the Busy/Rejected/DeferredRecordingInProgress edge cases 5 rounds
/// of plan-review found) actually gets exercised end-to-end, not just at the decoder-unit level
/// <c>DecoderSampleRateLiveApplyTests</c> (Core.Sstv.Tests) already covers.
/// </summary>
public sealed class SstvSessionServiceSampleRateLiveApplyTests
{
    private const int InitialSampleRate = 8000;
    private const int NewSampleRate = 11025;

    private static (SstvSessionService Service, FakeAudioEngine AudioEngine, FakeSstvDecoder Decoder, FakeSstvEncoder Encoder, FakeWaterfallSource Waterfall) CreateService()
    {
        var audioEngine = new FakeAudioEngine();
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [8000, 11025])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [8000, 11025])],
        };
        var deviceMuteQuery = new FakeAudioDeviceMuteQuery();
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = "capture-1", PlaybackDeviceId = "playback-1" },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var decoder = new FakeSstvDecoder { SampleRate = InitialSampleRate };
        var encoder = new FakeSstvEncoder { SampleRate = InitialSampleRate };
        // Code-review round-1 finding: must NOT be left at the constructor default
        // (SstvSampleRate.Default == 11025 == this file's own NewSampleRate) -- that coincidence
        // made the commit-ordering assertion in RequestSampleRateAsync_WhileReceiving_... pass
        // vacuously, since "not yet the new rate" and "still the default" were indistinguishable.
        var waterfall = new FakeWaterfallSource { SampleRate = InitialSampleRate };
        var receivedImage = new FakeReceivedImageBuffer();
        var radioSession = new FakeRadioSessionService();

        var service = new SstvSessionService(audioEngine, deviceEnumerator, deviceMuteQuery, settingsStore, decoder, encoder, new MacroTextResolver(), waterfall, receivedImage, radioSession, new FakeCwIdDecoder(), NullLogger<SstvSessionService>.Instance);
        return (service, audioEngine, decoder, encoder, waterfall);
    }

    [Fact]
    public async Task RequestSampleRateAsync_SameRateAlreadyLive_ReturnsNoChange_TouchesNothing()
    {
        var (service, _, decoder, encoder, waterfall) = CreateService();

        var result = await service.RequestSampleRateAsync(InitialSampleRate);

        Assert.Equal(SampleRateApplyResult.NoChange, result);
        Assert.Equal(0, decoder.RequestSampleRateCallCount);
        Assert.Equal(0, encoder.RequestSampleRateCallCount);
        Assert.Equal(0, waterfall.RequestSampleRateCallCount);
    }

    [Fact]
    public async Task RequestSampleRateAsync_NotReceiving_CommitsImmediately_NoCaptureCalls()
    {
        var (service, audioEngine, decoder, encoder, waterfall) = CreateService();

        var result = await service.RequestSampleRateAsync(NewSampleRate);

        Assert.Equal(SampleRateApplyResult.Applied, result);
        Assert.Equal(NewSampleRate, decoder.SampleRate);
        Assert.Equal(NewSampleRate, encoder.SampleRate);
        Assert.Equal(NewSampleRate, waterfall.SampleRate);
        Assert.False(audioEngine.IsCapturing); // never opened -- nothing to coordinate
    }

    [Fact]
    public async Task RequestSampleRateAsync_WhileReceiving_StopsCommitsThenRestarts_InThatOrder()
    {
        var (service, audioEngine, decoder, encoder, waterfall) = CreateService();
        await service.StartReceivingAsync();
        Assert.True(audioEngine.IsCapturing);
        var waterfallRateBeforeChange = waterfall.SampleRate;

        var captureStateAtCommit = (IsCapturing: true, WaterfallRate: -1);
        decoder.OnApplyPendingReconfigurationNow = () =>
        {
            // Proves the ORDER, not just the final state: capture must already be stopped, and the
            // waterfall must NOT have been updated yet, at the exact moment the decoder commits.
            captureStateAtCommit = (audioEngine.IsCapturing, waterfall.SampleRate);
        };

        var result = await service.RequestSampleRateAsync(NewSampleRate);

        Assert.Equal(SampleRateApplyResult.Applied, result);
        Assert.False(captureStateAtCommit.IsCapturing); // capture was stopped BEFORE the decoder committed
        Assert.Equal(waterfallRateBeforeChange, captureStateAtCommit.WaterfallRate); // not yet updated at commit time
        Assert.NotEqual(NewSampleRate, captureStateAtCommit.WaterfallRate); // explicit, not just "unchanged"

        Assert.True(audioEngine.IsCapturing); // reopened afterward
        Assert.Equal(NewSampleRate, audioEngine.LastRequestedCaptureSampleRate);
        Assert.Equal(NewSampleRate, decoder.SampleRate);
        Assert.Equal(NewSampleRate, waterfall.SampleRate);
        Assert.Equal(1, waterfall.RequestSampleRateCallCount); // proves the call actually happened, not just the final value
        Assert.Equal(NewSampleRate, encoder.SampleRate);
    }

    [Fact]
    public async Task RequestSampleRateAsync_DecoderRejects_RestoresCaptureAtOldRate_WaterfallUntouched()
    {
        var (service, audioEngine, decoder, _, waterfall) = CreateService();
        await service.StartReceivingAsync();
        decoder.ApplyPendingReconfigurationNowResultToReturn = SwapResult.Rejected;

        var result = await service.RequestSampleRateAsync(NewSampleRate);

        Assert.Equal(SampleRateApplyResult.Rejected, result);
        Assert.Equal(InitialSampleRate, decoder.SampleRate); // never applied on Rejected in the first place, matching the real decoder's own rollback contract
        Assert.Equal(0, waterfall.RequestSampleRateCallCount); // never touched -- the swap didn't commit
        Assert.True(audioEngine.IsCapturing); // capture restored, at the OLD (unchanged) rate
        Assert.Equal(InitialSampleRate, audioEngine.LastRequestedCaptureSampleRate);
    }

    [Fact]
    public async Task RequestSampleRateAsync_DecoderBusy_ThrowsAndRestoresCapture()
    {
        var (service, audioEngine, decoder, _, _) = CreateService();
        await service.StartReceivingAsync();
        decoder.ApplyPendingReconfigurationNowResultToReturn = SwapResult.Busy;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestSampleRateAsync(NewSampleRate));

        Assert.Contains("busy", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(audioEngine.IsCapturing); // best-effort restart still happened despite the failure
        Assert.Equal(InitialSampleRate, audioEngine.LastRequestedCaptureSampleRate); // at the OLD rate -- the swap never committed
        Assert.Null(decoder.PendingSampleRate); // the stuck pending rate was cleared, not left armed
    }

    [Fact]
    public async Task RequestSampleRateAsync_RecordingInProgress_ReturnsDeferred_ChangesNothing()
    {
        var (service, audioEngine, decoder, _, waterfall) = CreateService();
        await service.StartReceivingAsync();
        await service.StartRecordingAsync("/tmp/does-not-matter.wav");

        var result = await service.RequestSampleRateAsync(NewSampleRate);

        Assert.Equal(SampleRateApplyResult.DeferredRecordingInProgress, result);
        Assert.Equal(0, decoder.RequestSampleRateCallCount);
        Assert.Equal(0, waterfall.RequestSampleRateCallCount);
        Assert.Equal(InitialSampleRate, audioEngine.LastRequestedCaptureSampleRate);
        Assert.True(audioEngine.IsCapturing); // untouched -- the recording (and reception) is still live
    }

    [Fact]
    public async Task RequestSampleRateAsync_WhileRecordingInProgress_StartRecordingRefusesDuringTheChange()
    {
        // Round-4 finding R2's TOCTOU close, from the OTHER direction: this pins that
        // StartRecordingAsync itself refuses while a change is actively being applied, not just that
        // RequestSampleRateAsync defers when a recording already exists.
        var (service, _, decoder, _, _) = CreateService();
        await service.StartReceivingAsync();

        decoder.OnApplyPendingReconfigurationNow = () =>
        {
            // StartRecordingAsync throws synchronously (before returning any Task) when it refuses --
            // no await needed here, and none is safe from inside this synchronous callback anyway.
            // Plain try/catch, not Assert.Throws/Record.Exception -- both bind to their Task-returning
            // overload here (the method's return TYPE is Task, regardless of it throwing before ever
            // producing one), and xUnit's analyzer forbids that shape for a reason that doesn't apply
            // to this specific synchronous-throw case.
            var threw = false;
            try
            {
                service.StartRecordingAsync("/tmp/racing.wav");
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Assert.True(threw, "expected StartRecordingAsync to refuse synchronously while a sample rate change is in progress");
        };

        var result = await service.RequestSampleRateAsync(NewSampleRate);

        Assert.Equal(SampleRateApplyResult.Applied, result);
    }

    private static readonly SstvModeDefinition TestMode = new(
        Id: "test",
        DisplayName: "Test",
        VisCode: 0,
        ImageWidth: 1,
        ImageHeight: 1,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments: []);

    private static readonly IImageSource TestImage = new ArrayImageSource(1, 1, new Rgb24[1]);

    [Theory]
    [InlineData("success")]
    [InlineData("cancel")]
    [InlineData("timeout")]
    [InlineData("throw")]
    public async Task TransmitAsync_FooterPreparationHoldsRateLeaseAndAlwaysReleasesIt(string outcome)
    {
        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var footer = Enumerable.Repeat(0.25f, 480).ToArray();
        var (service, encoder, audio, _) = CreateFooterResolutionService((_, rate) =>
        {
            entered.SetResult(rate);
            try
            {
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Test did not release footer preparation");
                }

                if (outcome != "success")
                {
                    throw new IOException("Injected resolver failure, possibly after abandonment");
                }

                return footer;
            }
            finally
            {
                workerFinished.SetResult();
            }
        }, outcome == "timeout" ? TimeSpan.FromMilliseconds(400) : TimeSpan.FromSeconds(5));
        await using var lifetime = service;
        using var cancellation = new CancellationTokenSource();
        TransmitProgressInfo? lastProgress = null;
        service.TransmitProgressChanged += progress => lastProgress = progress;
        var transmission = service.TransmitAsync(TestMode, TestImage, cancellation.Token);
        try
        {
            Assert.Equal(48000, await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await service.RequestSampleRateAsync(44100);
            Assert.Equal(48000, encoder.SampleRate);

            if (outcome == "cancel")
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transmission);
            }
            else if (outcome == "timeout")
            {
                await Assert.ThrowsAsync<TimeoutException>(() => transmission);
            }
            else
            {
                release.Set();
                if (outcome == "throw")
                {
                    await Assert.ThrowsAsync<IOException>(() => transmission);
                }
                else
                {
                    await transmission.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal(footer, audio.PlaybackSamples.TakeLast(footer.Length));
                    Assert.NotNull(lastProgress);
                    Assert.Equal(TimeSpan.FromSeconds(audio.PlaybackSamples.Count / 48000.0), lastProgress.Value.Elapsed);
                }
            }

            Assert.Equal(44100, encoder.SampleRate);
            if (outcome != "success")
            {
                Assert.Empty(audio.PlaybackSamples);
            }
        }
        finally
        {
            release.Set();
            await workerFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransmitAsync_FailureBeforeFooterWorkerReleasesLease(bool preCancelled)
    {
        var resolverCalled = false;
        var (service, encoder, audio, settings) = CreateFooterResolutionService((_, _) =>
        {
            resolverCalled = true;
            return [];
        }, TimeSpan.FromSeconds(5));
        await using var lifetime = service;
        using var cancellation = new CancellationTokenSource();
        if (preCancelled)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TransmitAsync(TestMode, TestImage, cancellation.Token));
        }
        else
        {
            settings.LoadAsyncException = new IOException("Injected settings load failure");
            await Assert.ThrowsAsync<IOException>(() => service.TransmitAsync(TestMode, TestImage));
            settings.LoadAsyncException = null;
        }

        encoder.RequestSampleRate(44100);
        Assert.Equal(44100, encoder.SampleRate);
        Assert.False(resolverCalled);
        Assert.Empty(audio.PlaybackSamples);
    }

    private static (SstvSessionService Service, RestartableSstvEncoder Encoder, FakeAudioEngine Audio, FakeSettingsStore Settings)
        CreateFooterResolutionService(Func<string, int, float[]?> resolver, TimeSpan cleanupTimeout)
    {
        var audio = new FakeAudioEngine();
        var encoder = new RestartableSstvEncoder(48000);
        var settings = new FakeSettingsStore
        {
            Settings = new AppSettings()
                .WithSection(AudioDeviceSettings.SectionKey,
                    new AudioDeviceSettings { PlaybackDeviceId = "playback-1", TxBpfEnabled = false, TxVolumePercent = 100 },
                    AudioSettingsJsonContext.Default.AudioDeviceSettings)
                .WithSection(StationIdSettings.SectionKey,
                    new StationIdSettings { CwIdMode = CwIdMode.SoundFile, SoundFileMmvPath = "gated.mmv" },
                    StationIdSettingsJsonContext.Default.StationIdSettings),
        };
        var service = new SstvSessionService(audio,
            new FakeAudioDeviceEnumerator { OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [48000, 44100])] },
            new FakeAudioDeviceMuteQuery(), settings, new FakeSstvDecoder { SampleRate = 48000 }, encoder,
            new MacroTextResolver(), new FakeWaterfallSource(), new FakeReceivedImageBuffer(), new FakeRadioSessionService(),
            new FakeCwIdDecoder(), NullLogger<SstvSessionService>.Instance,
            cleanupTimeout, null, null, null)
        {
            SoundFileSamplesResolverForTests = resolver,
        };
        return (service, encoder, audio, settings);
    }

    [Fact]
    public async Task TransmitAsync_ActuallyOpensTheEncoderBracket_ClosesItAfterwards()
    {
        // Code-review round-1 finding: the plan's single highest-value regression guard for round-2
        // blocker B1 ("the self-test TX path was wrongly declared already-safe") was missing --
        // nothing asserted that TransmitAsync's own bracket is actually open while the encoder is in
        // use, only that the final SUCCEEDED. This would ship green even if BeginTransmission moved
        // to the wrong place.
        var (service, _, _, encoder, _) = CreateService();
        var refCountDuringEncode = 0;
        encoder.BeforeFirstYield = () =>
        {
            refCountDuringEncode = encoder.TransmissionRefCount;
            return Task.CompletedTask;
        };

        await service.TransmitAsync(TestMode, TestImage);

        Assert.True(refCountDuringEncode > 0, "expected the encoder bracket to be open while EncodeAsync was enumerating");
        Assert.Equal(0, encoder.TransmissionRefCount); // closed again afterward
    }

    [Fact]
    public async Task RunLoopbackSelfTestAsync_ActuallyOpensTheEncoderBracket_ClosesItAfterwards()
    {
        // Code-review round-1 finding, same reasoning as the TransmitAsync test above -- this is the
        // SECOND, easy-to-miss encoder call site (round-4 finding B1), and needs its own independent
        // proof that its bracket actually opens, not just that the TX path's does.
        var (service, _, _, encoder, _) = CreateService();
        var refCountDuringEncode = 0;
        encoder.BeforeFirstYield = () =>
        {
            refCountDuringEncode = encoder.TransmissionRefCount;
            return Task.CompletedTask;
        };

        await service.RunLoopbackSelfTestAsync(SstvModeRegistry.MartinM1, TestImage);

        Assert.True(refCountDuringEncode > 0, "expected the encoder bracket to be open while EncodeAsync was enumerating");
        Assert.Equal(0, encoder.TransmissionRefCount); // closed again afterward
    }
}
