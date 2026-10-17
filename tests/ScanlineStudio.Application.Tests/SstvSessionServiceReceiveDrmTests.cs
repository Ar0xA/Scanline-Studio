using System.Reflection;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Drm;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

/// <summary>DRM RX session wiring (P7a-2 step 6/8): worker lifecycle across Start/Stop, the toggle, the dispose
/// backstop and its race with Start, and the signal-state generations. Fake receivers, deterministic gates.</summary>
public sealed class SstvSessionServiceReceiveDrmTests
{
    private const int Rate = 8000;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Cleanup = TimeSpan.FromMilliseconds(300);

    private static readonly SstvModeDefinition TestMode = new(
        Id: "test", DisplayName: "Test", VisCode: 0, ImageWidth: 1, ImageHeight: 1, ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);

    internal sealed record Harness(
        SstvSessionService Service, FakeAudioEngine Engine, FakeSstvDecoder Decoder, FakeDrmReceiverFactory Factory,
        FakeSettingsStore Settings, SstvSessionServicePttSafetyTests.RecordingLogger<SstvSessionService> Logger,
        List<DrmSignalState> States, List<DrmReceivedFile> Files, FakeWaterfallSource Waterfall);

    internal static Harness Create(bool? drmEnabled = null, int rate = Rate, FakeDrmReceiverFactory? factory = null, int[]? deviceRates = null,
        IDrmReceiverFactory? realFactory = null)
    {
        var engine = new FakeAudioEngine();
        var devices = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, deviceRates ?? [rate])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, deviceRates ?? [rate])],
        };
        var settings = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = "capture-1", PlaybackDeviceId = "playback-1", SampleRate = rate },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        if (drmEnabled is { } on)
        {
            settings.Settings = settings.Settings.WithSection(
                DrmRxSettings.SectionKey, new DrmRxSettings { RxEnabled = on }, DrmRxSettingsJsonContext.Default.DrmRxSettings);
        }

        var decoder = new FakeSstvDecoder { SampleRate = rate };
        factory ??= new FakeDrmReceiverFactory();
        var logger = new SstvSessionServicePttSafetyTests.RecordingLogger<SstvSessionService>();
        var waterfall = new FakeWaterfallSource { SampleRate = rate };
        var service = new SstvSessionService(
            engine, devices, new FakeAudioDeviceMuteQuery(), settings, decoder, new FakeSstvEncoder { SampleRate = rate }, new MacroTextResolver(),
            waterfall, new FakeReceivedImageBuffer(), new FakeRadioSessionService(), new FakeCwIdDecoder(), logger,
            cleanupTimeoutForTests: Cleanup, playbackStopWaitBudgetForTests: TimeSpan.FromMilliseconds(200),
            inFlightKeyedTransmitWaitForTests: TimeSpan.FromMilliseconds(500), playbackStallTimeoutForTests: TimeSpan.FromMilliseconds(200),
            drmReceiverFactory: realFactory ?? factory);
        var states = new List<DrmSignalState>();
        var files = new List<DrmReceivedFile>();
        service.DrmSignalStateChanged += s => { lock (states) states.Add(s); };
        service.DrmFileReceived += f => { lock (files) files.Add(f); };
        return new Harness(service, engine, decoder, factory, settings, logger, states, files, waterfall);
    }

    private static void Until(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, Wait), "condition not reached");

    private static DrmReceiveWorker? LiveWorker(SstvSessionService s) =>
        (DrmReceiveWorker?)typeof(SstvSessionService).GetField("_drmWorker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s);

    private static List<DrmReceiveWorker> Retiring(SstvSessionService s) =>
        (List<DrmReceiveWorker>)typeof(SstvSessionService).GetField("_drmWorkersRetiring", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s)!;

    private static SemaphoreSlim RxGate(SstvSessionService s) =>
        (SemaphoreSlim)typeof(SstvSessionService).GetField("_rxTransitionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s)!;

    // ---- start / stop -------------------------------------------------------------------------------------------

    [Fact]
    public async Task MissingSetting_ReadsAsOn_AndStartBuildsAWorkerAtTheDecoderRate()
    {
        var h = Create();
        await h.Service.StartReceivingAsync();

        Assert.True(h.Service.IsDrmReceiveActive);
        FakeDrmReceiver r = Assert.Single(h.Factory.Created);
        Assert.Equal(Rate, r.CaptureRate);
        h.Engine.PushCapturedSamples(new float[64]);
        Until(() => r.PushedSamples == 64);
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task Disabled_BuildsNoWorker()
    {
        var h = Create(drmEnabled: false);
        await h.Service.StartReceivingAsync();

        Assert.False(h.Service.IsDrmReceiveActive);
        Assert.Empty(h.Factory.Created);
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task AnUnsupportedRate_GivesNoWorker()
    {
        var factory = new FakeDrmReceiverFactory { Supported = _ => false };
        var h = Create(factory: factory);
        await h.Service.StartReceivingAsync();

        Assert.False(h.Service.IsDrmReceiveActive);
        Assert.Empty(factory.Created);
        Assert.Contains(h.Logger.Entries, e => e.Message.Contains("DRM RX not started", StringComparison.Ordinal));
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task Stop_ReturnsWithoutWaitingOnAGatedWorker_ThenRaisesANewerTerminalState()
    {
        var h = Create();
        using var gate = new SemaphoreSlim(0);
        h.Factory.Configure = r => r.PushGate = gate;
        await h.Service.StartReceivingAsync();
        FakeDrmReceiver r = h.Factory.Last!;
        DrmReceiveWorker worker = LiveWorker(h.Service)!;
        h.Engine.PushCapturedSamples(new float[64]);
        Assert.True(r.PushEntered.Wait(Wait));

        // The worker is stuck inside Push; Stop (the same code the drain-thread maintenance stop runs) must not wait on it.
        Task stop = h.Service.StopReceivingAsync();
        Assert.True(stop.IsCompleted, "Stop completed synchronously");
        await stop;

        Assert.False(h.Service.IsDrmReceiveActive);
        DrmSignalState last = h.States[^1];
        Assert.False(last.IsActive);
        Assert.True(last.Generation > worker.Generation);

        // A state the retired worker raises later is suppressed.
        r.OnPush = x => x.RaiseState(FakeDrmReceiver.State());
        int before = h.States.Count;
        gate.Release(10);
        await worker.Completion.WaitAsync(Wait);
        Assert.Equal(before, h.States.Count);
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task ARetiredWorker_StillDeliversItsFile()
    {
        var h = Create();
        using var gate = new SemaphoreSlim(0);
        h.Factory.Configure = r => r.PushGate = gate;
        await h.Service.StartReceivingAsync();
        FakeDrmReceiver r = h.Factory.Last!;
        DrmReceiveWorker worker = LiveWorker(h.Service)!;
        r.OnPush = x => x.RaiseFile(FakeDrmReceiver.File("pic.jp2", [1, 2]));
        h.Engine.PushCapturedSamples(new float[64]);
        Assert.True(r.PushEntered.Wait(Wait));
        await h.Service.StopReceivingAsync();

        gate.Release();
        await worker.Completion.WaitAsync(Wait);
        Assert.Equal("pic.jp2", Assert.Single(h.Files).FileName);
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task StopThenStart_BuildsAFreshReceiver_AndALateOldHandlerCallNeverReachesIt()
    {
        var h = Create();
        await h.Service.StartReceivingAsync();
        DrmReceiveWorker first = LiveWorker(h.Service)!;
        FakeDrmReceiver r1 = h.Factory.Last!;
        await h.Service.StopReceivingAsync();
        await h.Service.StartReceivingAsync();
        FakeDrmReceiver r2 = h.Factory.Last!;
        Assert.NotSame(r1, r2);

        // A straggler call on the old handler (e.g. after a capture-stop watchdog timeout) reaches only the old, completed queue.
        first.Handler(new float[32]);
        h.Engine.PushCapturedSamples(new float[16]);
        Until(() => r2.PushedSamples == 16);
        Assert.Equal(0, r1.PushedSamples);
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task ARateChange_RebuildsTheReceiverAtTheNewRate()
    {
        var h = Create(deviceRates: [8000, 11025]);
        await h.Service.StartReceivingAsync();
        await h.Service.RequestSampleRateAsync(11025);

        Assert.Equal([8000, 11025], h.Factory.Created.Select(r => r.CaptureRate));
        Assert.True(h.Service.IsDrmReceiveActive);
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task TransmitPauseResume_GivesAFreshReceiver()
    {
        var h = Create();
        await h.Service.StartReceivingAsync();
        await h.Service.TransmitAsync(TestMode, new ArrayImageSource(1, 1, new Rgb24[1]));

        Assert.Equal(2, h.Factory.Created.Count);
        Assert.True(h.Service.IsDrmReceiveActive);
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task EveryStartThrowPath_LeavesNoWorker()
    {
        var h = Create();
        h.Engine.StartCaptureExceptionToThrowOnce = new AudioDeviceUnavailableException("busy");
        await Assert.ThrowsAnyAsync<Exception>(() => h.Service.StartReceivingAsync());
        Assert.False(h.Service.IsDrmReceiveActive);

        // Cancelled mid-start (the abandoned RX-resume path): cancel while the settings load is parked.
        using var cts = new CancellationTokenSource();
        var load = new TaskCompletionSource();
        h.Settings.Gate = load.Task;
        Task start = h.Service.StartReceivingAsync(cts.Token);
        cts.Cancel();
        load.SetResult();
        h.Settings.Gate = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.False(h.Service.IsDrmReceiveActive);

        await h.Service.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => h.Service.StartReceivingAsync());
        Assert.Empty(h.Factory.Created);
    }

    // ---- retiring workers and dispose ---------------------------------------------------------------------------

    [Fact]
    public async Task StopStartStop_WithAStuckWorker_KeepsBothAndTheNextStartAwaitsBoth()
    {
        var h = Create();
        using var gate = new SemaphoreSlim(0);
        h.Factory.Configure = r => r.PushGate = gate;

        await h.Service.StartReceivingAsync();
        DrmReceiveWorker w1 = LiveWorker(h.Service)!;
        h.Engine.PushCapturedSamples(new float[8]);
        Assert.True(h.Factory.Last!.PushEntered.Wait(Wait));
        await h.Service.StopReceivingAsync();

        // Start's bounded await of w1 times out (logged), then a second stuck worker.
        await h.Service.StartReceivingAsync();
        Assert.Contains(h.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("retiring DRM RX worker", StringComparison.Ordinal));
        DrmReceiveWorker w2 = LiveWorker(h.Service)!;
        h.Engine.PushCapturedSamples(new float[8]);
        Assert.True(SpinWait.SpinUntil(() => h.Factory.Last!.PushEntered.IsSet, Wait));
        await h.Service.StopReceivingAsync();
        Assert.Equal([w1, w2], Retiring(h.Service));

        // Release both, then the next Start awaits and prunes both.
        gate.Release(100);
        await h.Service.StartReceivingAsync();
        Assert.True(w1.Completion.IsCompleted && w2.Completion.IsCompleted);
        Assert.Empty(Retiring(h.Service));
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_CancelsTheLiveAndEveryRetiringWorker_UnderOneBound()
    {
        var h = Create();
        using var gate = new SemaphoreSlim(0);
        h.Factory.Configure = r => r.PushGate = gate;
        await h.Service.StartReceivingAsync();
        DrmReceiveWorker w1 = LiveWorker(h.Service)!;
        h.Engine.PushCapturedSamples(new float[8]);
        h.Engine.PushCapturedSamples(new float[8]);
        Assert.True(h.Factory.Last!.PushEntered.Wait(Wait));
        await h.Service.StopReceivingAsync();
        await h.Service.StartReceivingAsync();
        DrmReceiveWorker w2 = LiveWorker(h.Service)!;
        h.Engine.PushCapturedSamples(new float[8]);
        h.Engine.PushCapturedSamples(new float[8]);
        Assert.True(SpinWait.SpinUntil(() => h.Factory.Last!.PushEntered.IsSet, Wait));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await h.Service.DisposeAsync().AsTask().WaitAsync(Wait);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"dispose took {sw.Elapsed}");

        // Cancelled, not drained: after release each worker exits without pushing its second queued block.
        gate.Release(100);
        await Task.WhenAll(w1.Completion, w2.Completion).WaitAsync(Wait);
        Assert.All(h.Factory.Created, r => Assert.Equal(8, r.PushedSamples));
    }

    [Fact]
    public async Task DisposeBackstop_StopsTheWorker_WhenStopsGateWaitTimesOut()
    {
        var h = Create();
        await h.Service.StartReceivingAsync();
        DrmReceiveWorker worker = LiveWorker(h.Service)!;

        // Hold the RX gate so DisposeAsync's StopReceivingAsync times out and returns without stopping.
        SemaphoreSlim rxGate = RxGate(h.Service);
        Assert.True(await rxGate.WaitAsync(0));
        try
        {
            await h.Service.DisposeAsync().AsTask().WaitAsync(Wait);
        }
        finally
        {
            rxGate.Release();
        }

        Assert.Null(LiveWorker(h.Service));
        await worker.Completion.WaitAsync(Wait);
        h.Engine.PushCapturedSamples(new float[8]);
        Assert.Equal(0, h.Factory.Last!.PushedSamples);
    }

    [Fact]
    public async Task StartRacingDispose_AtThePublish_ExactlyOneSideStopsTheWorker()
    {
        var h = Create();
        using var inCreate = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        h.Factory.BeforeCreate = _ =>
        {
            inCreate.Set();
            release.Wait(Wait);
        };

        // Start parks inside the factory, after its disposed/cancelled re-checks, holding the RX gate.
        Task start = Task.Run(() => h.Service.StartReceivingAsync());
        Assert.True(inCreate.Wait(Wait));

        // Dispose: its Stop times out on the gate, its backstop finds nothing published yet.
        await h.Service.DisposeAsync().AsTask().WaitAsync(Wait);
        Assert.Null(LiveWorker(h.Service));

        // Start resumes, publishes, sees _disposed and takes the worker back out itself.
        release.Set();
        await start.WaitAsync(Wait);
        Assert.Null(LiveWorker(h.Service));
        DrmReceiveWorker retired = Assert.Single(Retiring(h.Service));
        await retired.Completion.WaitAsync(Wait);
        h.Engine.PushCapturedSamples(new float[8]);
        Assert.Equal(0, h.Factory.Last!.PushedSamples);
    }

    // ---- toggle ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetDrmReceiveEnabled_IsTheDefaultUntilSaved_ThenTheSavedValue()
    {
        var h = Create();
        Assert.Equal(DrmReceiveDefaults.Enabled, await h.Service.GetDrmReceiveEnabledAsync());

        await h.Service.SetDrmReceiveEnabledAsync(false);
        Assert.False(await h.Service.GetDrmReceiveEnabledAsync());

        await h.Service.SetDrmReceiveEnabledAsync(true);
        Assert.True(await h.Service.GetDrmReceiveEnabledAsync());
    }

    [Fact]
    public async Task SetDrmReceiveEnabled_LiveAppliesWhenTheGateIsFree_AndPersists()
    {
        var h = Create();
        await h.Service.StartReceivingAsync();
        Assert.True(h.Service.IsDrmReceiveActive);

        await h.Service.SetDrmReceiveEnabledAsync(false);
        Assert.False(h.Service.IsDrmReceiveActive);
        Assert.False(h.Settings.Settings.GetSection(DrmRxSettings.SectionKey, DrmRxSettingsJsonContext.Default.DrmRxSettings)!.RxEnabled);
        Assert.False(h.States[^1].IsActive);

        await h.Service.SetDrmReceiveEnabledAsync(true);
        Assert.True(h.Service.IsDrmReceiveActive);
        Assert.Equal(2, h.Factory.Created.Count);
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task ASetWhileStartHoldsTheGate_AfterItReadTheToggle_IsReconciledOnRelease()
    {
        var h = Create();
        Task? set = null;
        bool gateHeldDuringSet = false;

        // Inside Start's publish window: the toggle was already read as on, and the gate is held, so Set cannot apply.
        h.Factory.BeforeCreate = _ =>
        {
            if (set is not null)
                return;
            set = h.Service.SetDrmReceiveEnabledAsync(false);
            gateHeldDuringSet = RxGate(h.Service).CurrentCount == 0;
        };
        await h.Service.StartReceivingAsync();
        await set!.WaitAsync(Wait);
        Assert.True(gateHeldDuringSet);

        Until(() => !h.Service.IsDrmReceiveActive && RxGate(h.Service).CurrentCount == 1);
        Assert.Single(h.Factory.Created);
        Assert.False(h.States[^1].IsActive);
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task ASetDuringARateChange_IsAppliedOnceTheChangeReleasesTheGate()
    {
        var h = Create(deviceRates: [8000, 11025]);
        await h.Service.StartReceivingAsync();
        SemaphoreSlim rxGate = RxGate(h.Service);

        var stopGate = new TaskCompletionSource();
        h.Engine.OnStopCaptureAsync = () => stopGate.Task;
        Task change = h.Service.RequestSampleRateAsync(11025);
        Assert.True(SpinWait.SpinUntil(() => rxGate.CurrentCount == 0, Wait));

        await h.Service.SetDrmReceiveEnabledAsync(false);
        h.Engine.OnStopCaptureAsync = null;
        stopGate.SetResult();
        await change.WaitAsync(Wait);

        Until(() => !h.Service.IsDrmReceiveActive && rxGate.CurrentCount == 1);
        Assert.Single(h.Factory.Created);
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task ASetDuringAFileDecode_IsApplied_AndNoFreshLiveWorkerStarts()
    {
        var h = Create();
        await h.Service.StartReceivingAsync();
        bool set = false;
        h.Decoder.OnPushSamples = _ =>
        {
            if (set)
                return;
            set = true;
            Assert.True(h.Service.SetDrmReceiveEnabledAsync(false).IsCompletedSuccessfully);
        };
        string path = Path.Combine(Path.GetTempPath(), $"drm-set-during-decode-{Guid.NewGuid():N}.wav");
        WavFile.Write(path, new float[8000], Rate);
        try
        {
            await h.Service.DecodeFromFileAsync(path).WaitAsync(Wait);
        }
        finally
        {
            File.Delete(path);
        }

        Until(() => RxGate(h.Service).CurrentCount == 1);
        Assert.True(set);
        Assert.False(h.Service.IsDrmReceiveActive);
        Assert.Equal(2, h.Factory.Created.Count);
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task ACaptureDeviceChange_RebuildsTheReceiver()
    {
        var h = Create();
        var devices = (FakeAudioDeviceEnumerator)typeof(SstvSessionService)
            .GetField("_deviceEnumerator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(h.Service)!;
        devices.InputDevices = [.. devices.InputDevices, new AudioDeviceInfo("capture-2", "Capture 2", 1, 0, [Rate])];
        await h.Service.StartReceivingAsync();
        await h.Service.RequestCaptureDeviceAsync("capture-2", "Capture 2");

        Assert.Equal(2, h.Factory.Created.Count);
        Assert.Equal("capture-2", h.Engine.LastRequestedCaptureDevice!.Id);
        Assert.True(h.Service.IsDrmReceiveActive);
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task ASetDuringStartsSettingsLoad_IsNotOverwrittenByTheStaleSeed()
    {
        var h = Create(drmEnabled: true);
        var load = new TaskCompletionSource();
        h.Settings.Gate = load.Task;
        Task start = h.Service.StartReceivingAsync();

        // Start is parked in a settings load that will return RxEnabled = true; the user turns DRM off meanwhile.
        Task set = h.Service.SetDrmReceiveEnabledAsync(false);
        h.Settings.Gate = null;
        load.SetResult();
        await Task.WhenAll(start, set).WaitAsync(Wait);

        Until(() => !h.Service.IsDrmReceiveActive && RxGate(h.Service).CurrentCount == 1);
        Assert.False(h.Service.IsDrmReceiveActive);
        await h.Service.DisposeAsync();
    }

    // ---- outputs --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASlowSubscriber_DoesNotBlockCapture()
    {
        var h = Create();
        using var block = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        h.Service.DrmSignalStateChanged += _ =>
        {
            entered.Set();
            block.Wait(Wait);
        };
        h.Factory.Configure = r => r.OnPush = x => x.RaiseState(FakeDrmReceiver.State());
        await h.Service.StartReceivingAsync();
        h.Engine.PushCapturedSamples(new float[8]);
        Assert.True(entered.Wait(Wait));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
            h.Engine.PushCapturedSamples(new float[8]);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"capture blocked for {sw.Elapsed}");
        block.Set();
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task ABsrRequest_ProducesNoFile()
    {
        var h = Create();
        h.Factory.Configure = r => r.OnPush = x => x.RaiseBsr([1, 2, 3]);
        await h.Service.StartReceivingAsync();
        DrmReceiveWorker w = LiveWorker(h.Service)!;
        h.Engine.PushCapturedSamples(new float[8]);
        await h.Service.StopReceivingAsync();
        await w.Completion.WaitAsync(Wait);

        Assert.Empty(h.Files);
        await h.Service.DisposeAsync();
    }
}
