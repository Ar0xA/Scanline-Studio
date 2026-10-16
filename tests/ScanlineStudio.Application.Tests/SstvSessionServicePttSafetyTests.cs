using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Cw;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

/// <summary>Tier A Batch 3 chunk 3a. Every test here guards the SAME failure class -- a physically
/// keyed transmitter that the code believes (or never checks) is un-keyed. This is a real-world
/// legality/safety property (an operator transmitting indefinitely on frequency), not a decode-quality
/// property, so these are deliberately end-to-end through <see cref="SstvSessionService"/> rather than
/// unit tests of the extracted helpers: the bugs were all in the ORDERING and BUDGETING between steps,
/// which a per-helper test cannot observe.</summary>
public sealed class SstvSessionServicePttSafetyTests
{
    private static readonly bool[] PttOnThenOff = [true, false];

    private static readonly SstvModeDefinition TestMode = new(
        Id: "test", DisplayName: "Test", VisCode: 0, ImageWidth: 1, ImageHeight: 1,
        ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);

    private static readonly IImageSource TestImage = new ArrayImageSource(1, 1, new Rgb24[1]);

    private static (SstvSessionService Service, IAudioEngine Engine, FakeRadioSessionService Radio, RecordingLogger<SstvSessionService> Logger)
        CreateService(
            Func<FakeAudioEngine, IAudioEngine>? wrapEngine = null,
            TimeSpan? cleanupTimeout = null,
            TimeSpan? playbackStopWaitBudget = null,
            TimeSpan? inFlightKeyedTransmitWait = null,
            FakeAudioDeviceEnumerator? deviceEnumerator = null,
            TimeSpan? playbackStallTimeout = null,
            FakeSettingsStore? settingsStore = null,
            FakeAudioDeviceMuteQuery? deviceMuteQuery = null)
    {
        var inner = new FakeAudioEngine();
        var engine = wrapEngine?.Invoke(inner) ?? inner;
        deviceEnumerator ??= new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [8000])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [11025])],
        };
        settingsStore ??= new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = "capture-1", PlaybackDeviceId = "playback-1", SampleRate = 8000 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        deviceMuteQuery ??= new FakeAudioDeviceMuteQuery();
        var radio = new FakeRadioSessionService();
        var logger = new RecordingLogger<SstvSessionService>();

        // The internal test constructor (see its own doc comment): these budgets must actually be
        // allowed to EXPIRE for these tests to distinguish fixed behavior from the bug, and the
        // production values (5s/5s/3s) would make this file take far too long to run.
        var service = new SstvSessionService(
            engine, deviceEnumerator, deviceMuteQuery, settingsStore, new FakeSstvDecoder(), new FakeSstvEncoder(),
            new MacroTextResolver(), new FakeWaterfallSource(), new FakeReceivedImageBuffer(), radio, new FakeCwIdDecoder(), logger,
            cleanupTimeoutForTests: cleanupTimeout ?? TimeSpan.FromMilliseconds(300),
            playbackStopWaitBudgetForTests: playbackStopWaitBudget ?? TimeSpan.FromMilliseconds(200),
            inFlightKeyedTransmitWaitForTests: inFlightKeyedTransmitWait ?? TimeSpan.FromMilliseconds(500),
            playbackStallTimeoutForTests: playbackStallTimeout ?? TimeSpan.FromMilliseconds(200));

        return (service, engine, radio, logger);
    }

    // ------------------------------------------------------------------ DI constructor selection

    [Fact]
    public async Task RealDependencyInjection_ResolvesTheProductionConstructor_NotTheInternalTestOnlyOne()
    {
        // The blocker-1/blocker-3 test-only budgets (Edit 4 of this fix's own design) only work
        // because Microsoft.Extensions.DependencyInjection considers PUBLIC constructors only when
        // choosing which one to invoke -- the internal 13-parameter test overload must never be a
        // candidate the container could accidentally pick for the real app. Unverified by the fix's
        // own author (their own stated assumption); confirmed here directly against the real
        // container, the same registration shape Program.cs uses
        // (services.AddSingleton<ISstvSessionService, SstvSessionService>()), not a hand-built
        // instance.
        var services = new ServiceCollection();
        services.AddSingleton<IAudioEngine>(new FakeAudioEngine());
        services.AddSingleton<IAudioDeviceEnumerator>(new FakeAudioDeviceEnumerator());
        services.AddSingleton<IAudioDeviceMuteQuery>(new FakeAudioDeviceMuteQuery());
        services.AddSingleton<ISettingsStore>(new FakeSettingsStore { Settings = new AppSettings() });
        services.AddSingleton<ISstvDecoder>(new FakeSstvDecoder());
        services.AddSingleton<ISstvEncoder>(new FakeSstvEncoder());
        services.AddSingleton<IMacroTextResolver, MacroTextResolver>();
        services.AddSingleton<IWaterfallSource>(new FakeWaterfallSource());
        services.AddSingleton<IReceivedImageBuffer>(new FakeReceivedImageBuffer());
        services.AddSingleton<IRadioSessionService>(new FakeRadioSessionService());
        services.AddSingleton<ICwIdDecoder>(new FakeCwIdDecoder());
        services.AddLogging();
        services.AddSingleton<ISstvSessionService, SstvSessionService>();

        await using var provider = services.BuildServiceProvider();

        // Throws AmbiguousMatchException/InvalidOperationException if the container can't uniquely
        // pick a constructor -- resolving successfully at all is most of what this test proves.
        var resolved = provider.GetRequiredService<ISstvSessionService>();

        Assert.IsType<SstvSessionService>(resolved);
    }

    // ------------------------------------------------------------------ blocker 1

    [Fact]
    public async Task Blocker1_StopPlaybackOutlastsItsBudget_PttIsStillUnkeyed()
    {
        // THE bug: one shared CancellationTokenSource(CleanupTimeout) covered StopPlayback AND the
        // un-key, and StopPlayback ran first. IAudioEngine.StopPlaybackAsync takes no token, so it
        // could not be bounded by that source -- it just consumed the wall clock. The real
        // MiniAudioEngine can take ~10.2s on a wedged output device (DrainTimeout 5s +
        // MiniAudioPlaybackSession.CloseTimeout 5s + DrainTailMargin 200ms) against a 5s budget, so
        // by the time TryUnkeyPttAsync ran, its token was already cancelled -- and BOTH real protocol
        // backends throw straight out of their lock-acquire gate on an already-cancelled token
        // (HamlibRadioProtocol.cs, RigctldClientProtocol.cs). PTT-off was never even ATTEMPTED: it
        // was caught, logged Warning, and dropped, leaving the rig keyed on the air.
        var gate = new TaskCompletionSource();
        var (service, _, radio, _) = CreateService(
            wrapEngine: inner => new GatedStopPlaybackAudioEngine(inner, gate.Task),
            cleanupTimeout: TimeSpan.FromMilliseconds(300),
            playbackStopWaitBudget: TimeSpan.FromMilliseconds(200));

        await service.TransmitAsync(TestMode, TestImage);

        // The stop is STILL hanging at this point -- and PTT is nevertheless confirmed un-keyed.
        Assert.False(gate.Task.IsCompleted);
        Assert.Equal(PttOnThenOff, radio.PttCalls);

        gate.SetResult();
    }

    [Fact]
    public async Task Blocker1_AbnormalTermination_UnkeysBeforeWaitingOnStopPlaybackAtAll()
    {
        // The safety path (manual Stop TX / SWR auto-cutoff) must not wait on the drain at all --
        // there is no audio tail worth preserving on an aborted transmission, and "off now" is the
        // entire point of a cutoff. Proven by asserting PTT is already off while StopPlayback itself
        // is still gated (never even reached yet -- the urgent un-key runs before it).
        //
        // Deterministic StartPlayback failure, NOT a real-time cancellation race: an earlier version
        // of this test raced a 10ms CancellationToken against GenerateTone's own tone-generation
        // speed, on the theory that 5 real seconds of tone gives ample time to still be mid-generation
        // when the cancellation fires (the same technique
        // SstvSessionServiceTests.TuneAsync_TokenCancelledMidTone_... uses). That is NOT reliable in
        // isolation -- GenerateTone's loop is CPU-bound sine synthesis with no real per-sample delay,
        // and on a fast/idle machine 240,000 samples can finish well under 10ms, so cancellation never
        // fires before the tone completes NORMALLY -- discovered when this exact test hung for its
        // full 5s WaitForAsync timeout when run standalone (fast machine, no other test load) despite
        // reliably passing as part of a larger, more heavily-loaded batch run. A deterministic throw
        // has no such timing dependency.
        var stopGate = new TaskCompletionSource();
        var (service, _, radio, _) = CreateService(
            wrapEngine: inner => new ThrowOnStartPlaybackAudioEngine(new GatedStopPlaybackAudioEngine(inner, stopGate.Task)),
            playbackStopWaitBudget: TimeSpan.FromSeconds(30));

        // Duration is irrelevant -- StartPlaybackAsync throws before any tone is ever generated.
        var tune = service.TuneAsync(TxLevelFamily.Sstv, 1750, TimeSpan.FromMilliseconds(1));
        await WaitForAsync(() => radio.PttCalls.Count == 2, TimeSpan.FromSeconds(5));

        Assert.Equal(PttOnThenOff, radio.PttCalls);
        Assert.False(stopGate.Task.IsCompleted, "the un-key must not have waited on the playback drain");

        stopGate.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => tune);
    }

    [Fact]
    public async Task Blocker1_NormalCompletion_StillDrainsBeforeUnkeying_NoTailTruncation()
    {
        // The counterweight to the two tests above, and the reason the un-key is NOT simply moved
        // first unconditionally: on a normal completion, dropping PTT while the miniaudio ring /
        // PulseAudio server queue still hold audio truncates the tail of EVERY successful
        // transmission -- exactly what MiniAudioEngine.DrainTailMargin exists to prevent. Order must
        // stay drain-then-unkey whenever the drain is healthy.
        List<string> order = [];
        var (service, _, radio, _) = CreateService(
            wrapEngine: inner => new RecordingOrderAudioEngine(inner, () => order.Add("stop-playback")));
        radio.BeforeSetPtt = tx => order.Add(tx ? "ptt-on" : "ptt-off");

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(["ptt-on", "stop-playback", "ptt-off"], order);
    }

    // ------------------------------------------------------------------ blocker 2

    [Fact]
    public async Task Blocker2_RigIdFlipsToNoneDuringCleanupUnkey_IsReportedAsARealStuckKeyedRig()
    {
        // THE bug: TryUnkeyPttAsync re-checked RigId AT CATCH TIME. RadioController.DisconnectAsync/
        // DisposeAsync both reset _rigId to "none" WITHOUT un-keying PTT, so: rig genuinely keyed by
        // this transmit -> controller disconnects mid-cleanup -> SetPttAsync(false) throws "No radio
        // connected" -> RigId now reads "none" -> logged as the BENIGN "PTT unkey skipped, no radio"
        // line and swallowed. A physically keyed transmitter reported as "nothing to unkey."
        var (service, _, radio, logger) = CreateService();
        radio.BeforeSetPtt = tx =>
        {
            if (!tx)
            {
                radio.RigId = "none"; // the controller disconnected/disposed underneath us
            }
        };

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("MAY STILL BE KEYED", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("'PTT off'", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("un-key skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Blocker2_GenuinelyNoRadioConfigured_StaysBenign_NoCriticalOnEveryTransmit()
    {
        // The property the catch-time re-read was originally added for, which must survive the fix:
        // a fresh install (RigId=="none") must not log a Critical/Warning on every single transmit --
        // otherwise the new Critical is noise and gets ignored exactly when it matters.
        var (service, _, radio, logger) = CreateService();
        radio.RigId = "none";

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Empty(radio.PttCalls);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    // ------------------------------------------------------------------ blocker 3

    [Fact]
    public async Task Blocker3_DisposeAsync_WaitsForAnInFlightKeyedTransmitsOwnUnkey()
    {
        // THE bug: nothing tracked "a PlayWithPttAsync call has PTT keyed RIGHT NOW" for DisposeAsync's
        // shutdown backstop. On window-close during TX, the UI's
        // TxControlsPaneViewModel.Dispose() cancels the transmit's token WITHOUT awaiting the
        // transmit task, DI teardown proceeds, RadioController disposes (triggering blocker 2's
        // silent swallow) while the transmit's own finally/un-key is still in flight, and the process
        // can exit with the transmitter keyed and nothing logged as wrong.
        var gate = new TaskCompletionSource();
        var (service, _, radio, _) = CreateService(
            wrapEngine: inner => new GatedStopPlaybackAudioEngine(inner, gate.Task),
            playbackStopWaitBudget: TimeSpan.FromSeconds(30),
            inFlightKeyedTransmitWait: TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource();
        var tune = service.TuneAsync(TxLevelFamily.Sstv, 1750, TimeSpan.FromSeconds(30), ct: cts.Token);
        await WaitForAsync(() => radio.PttCalls.Count == 1, TimeSpan.FromSeconds(5));

        cts.Cancel(); // exactly what TxControlsPaneViewModel.Dispose() does -- no await
        var dispose = service.DisposeAsync().AsTask();

        // The transmit's finally is parked in the gated StopPlayback, so its cleanup is NOT done --
        // and DisposeAsync must not have returned while that is true.
        await Task.Delay(150);
        Assert.False(dispose.IsCompleted, "DisposeAsync returned while an in-flight transmit still had PTT keyed");

        gate.SetResult();
        await dispose;
        await Assert.ThrowsAsync<OperationCanceledException>(() => tune);

        // Exactly one un-key: the transmit's own. DisposeAsync must not double-key/double-unkey.
        Assert.Equal(PttOnThenOff, radio.PttCalls);
    }

    [Fact]
    public async Task Blocker3_DisposeAsync_InFlightTransmitNeverFinishes_ForceUnkeysItselfWithinTheBound()
    {
        // The other half: if the in-flight transmit's own cleanup never completes (wedged device,
        // wedged rig), DisposeAsync must stop waiting and force the un-key ITSELF rather than exit
        // with the rig keyed. Bounded so it fits inside the host's own teardown budget.
        var gate = new TaskCompletionSource();
        var (service, _, radio, logger) = CreateService(
            wrapEngine: inner => new GatedStopPlaybackAudioEngine(inner, gate.Task),
            playbackStopWaitBudget: TimeSpan.FromSeconds(30),
            inFlightKeyedTransmitWait: TimeSpan.FromMilliseconds(300));

        using var cts = new CancellationTokenSource();
        var tune = service.TuneAsync(TxLevelFamily.Sstv, 1750, TimeSpan.FromSeconds(30), ct: cts.Token);
        await WaitForAsync(() => radio.PttCalls.Count == 1, TimeSpan.FromSeconds(5));

        var stopwatch = Stopwatch.StartNew();
        await service.DisposeAsync();
        stopwatch.Stop();

        Assert.Contains(false, radio.PttCalls);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("did not finish its own PTT un-key", StringComparison.Ordinal));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"DisposeAsync must stay bounded; took {stopwatch.Elapsed}");

        // Release the gate so the original tune's own (still in-flight, now moot) cleanup can finish
        // and this test doesn't leak a background task. Tone generation against the fake engine has
        // no real-time back-pressure, so the tune's own try block already completed successfully
        // BEFORE DisposeAsync's bounded wait even expired -- it was only ever parked inside its own
        // gated StopPlayback call, not still generating/pumping samples -- so cancelling `cts` at
        // this point has nothing left to cancel; the call completes normally, it does not throw.
        gate.SetResult();
        await tune;

        // The backstop's confirmed un-key landed after the tune's own key and nobody re-keyed since,
        // so the tune's own cleanup skips its now-redundant un-key: one key, one un-key in total.
        Assert.Equal(PttOnThenOff, radio.PttCalls);
        Assert.Contains(logger.Entries, e => e.Message.Contains("Skipping cleanup PTT un-key attempt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Blocker3_DisposeAsync_PttOffPrecedesStopCapture()
    {
        // Found during this fix (same failure class, not in the original report): DisposeAsync ran
        // StopReceivingAsync FIRST, and that ends in MiniAudioCaptureSession.Dispose's own drain
        // thread join, which has NO timeout at all if a SamplesCaptured subscriber never returns --
        // so a wedged drain thread hung shutdown before the PTT backstop was ever reached. Un-key
        // must come first. A transmit whose own cleanup un-key fails leaves the backstop something to
        // un-key while RX (resumed by that transmit's cleanup) is still running.
        List<string> order = [];
        var (service, _, radio, _) = CreateService(
            wrapEngine: inner => new RecordingOrderAudioEngine(inner, () => { }, () => order.Add("stop-capture")));
        var failNextUnkey = true;
        radio.BeforeSetPtt = tx =>
        {
            order.Add(tx ? "ptt-on" : "ptt-off");
            if (!tx && failNextUnkey)
            {
                failNextUnkey = false;
                throw new TimeoutException("simulated slow backend -- the transmit's own cleanup un-key times out");
            }
        };

        await service.StartReceivingAsync();
        await service.TransmitAsync(TestMode, TestImage);
        Assert.Equal([true], radio.PttCalls);
        Assert.True(service.IsReceiving, "the transmit's cleanup must have resumed RX before the dispose under test");
        order.Clear();

        await service.DisposeAsync();

        Assert.Equal(["ptt-off", "stop-capture"], order);
    }

    // ------------------------------------------------------------------ round 2 blocker

    [Fact]
    public async Task Round2_DisposeAsync_RacesATuneStillInItsPreKeyWindow_NeverKeysAfterDisposeReturns()
    {
        // Round-2 finding (independent re-audit): DisposeAsync's backstop only guarded
        // _keyedTransmitCompletion state published BEFORE it ran. A PlayWithPttAsync call still
        // resolving its playback device (RefreshAsync -- well before this call has published anything
        // or keyed PTT at all) let DisposeAsync run, find nothing to wait on/un-key, and return -- and
        // ONLY THEN did the call go on to key PTT, with no shutdown backstop left to catch it.
        // Reachable for real: RadioStatusViewModel.TuneAsync passes CancellationToken.None, so nothing
        // can ever cancel this window away.
        var deviceGate = new TaskCompletionSource();
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [8000])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [11025])],
            Gate = deviceGate.Task,
        };
        var (service, _, radio, _) = CreateService(deviceEnumerator: deviceEnumerator);

        var tune = service.TuneAsync(TxLevelFamily.Sstv, 1750, TimeSpan.FromMilliseconds(1));

        // The tune is parked inside ResolveDeviceAsync's RefreshAsync call -- before it has published
        // _keyedTransmitCompletion or keyed PTT at all.
        await Task.Delay(50);
        Assert.Empty(radio.PttCalls);

        await service.DisposeAsync();

        // Only now does the parked tune get to continue.
        deviceGate.SetResult();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => tune);

        // THE property: PTT must never be commanded ON by a call whose device resolution only
        // finished AFTER DisposeAsync had already run its backstop and returned -- a defensive un-key
        // attempt in cleanup (harmless, idempotent) is fine, but the rig must never have been keyed in
        // the first place.
        Assert.DoesNotContain(true, radio.PttCalls);
    }

    // ------------------------------------------------------------------ round 3 findings

    [Fact]
    public async Task Round3_DisposeAsync_RetriesAnUnkeyThatFailedDuringItsOwnCleanup()
    {
        // Round-3 finding: a "keyed at shutdown" state DisposeAsync's check missed -- a transmit whose
        // OWN cleanup un-key was attempted and FAILED recorded nothing before this fix
        // (_keyedTransmitCompletion is unconditionally cleared by the transmit's own finally
        // regardless of whether the un-key succeeded) -- so DisposeAsync concluded "nothing to
        // do" on a rig it had ALREADY logged Critical about ("MAY STILL BE KEYED"). Simulates the
        // plausible real case: CleanupTimeout expires against a slow-but-healthy backend, and a fresh
        // attempt at shutdown succeeds.
        var failNextUnkey = true;
        var (service, _, radio, logger) = CreateService();
        radio.BeforeSetPtt = tx =>
        {
            if (!tx && failNextUnkey)
            {
                failNextUnkey = false;
                throw new TimeoutException("simulated slow backend -- the transmit's own cleanup un-key times out");
            }
        };

        await service.TransmitAsync(TestMode, TestImage);

        // The transmit's OWN cleanup un-key failed and was escalated to Critical -- no successful
        // un-key has landed yet.
        Assert.Equal([true], radio.PttCalls);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("MAY STILL BE KEYED", StringComparison.Ordinal));

        // DisposeAsync must retry rather than conclude there is nothing left to do.
        await service.DisposeAsync();

        Assert.Equal(PttOnThenOff, radio.PttCalls);
    }

    // ------------------------------------------------------------------ round 4 findings

    [Fact]
    public async Task Round4_StartReceivingAsync_PostDispose_Throws()
    {
        var (service, _, _, _) = CreateService();
        await service.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.StartReceivingAsync());
    }

    // ------------------------------------------------------------------ round 6 findings

    [Fact]
    public async Task Round6_DisposeAsync_OverlappingTransmits_StillBackstopsWhenNewerCallClearsOlderCallsRegistration()
    {
        // Round-6 finding: two overlapping PlayWithPttAsync calls (a real production interleaving --
        // RadioStatusViewModel's Tune command has no TX-in-progress CanExecute gate, so clicking Tune
        // during a TransmitAsync produces two live calls) let the NEWER call's Interlocked.Exchange
        // publish silently drop the OLDER call's _keyedTransmitCompletion registration. If the newer
        // call finishes first, the field goes null while the OLDER call is STILL genuinely keyed --
        // blinding DisposeAsync's old nullness-based "is anything still in flight" check.
        // _keyedTransmitCount (this fix) survives the overwrite.
        //
        // Round-12 update: this exact overlap (two live PlayWithPttAsync calls) is now IMPOSSIBLE --
        // _transmitInFlight (round-12's own single-flight guard, see its own comment) rejects a
        // second concurrent call outright, before it touches anything. This test now verifies THAT
        // property directly: the "newer" call throws immediately from the guard, never even reaching
        // a key command, and the reference-count mechanism this test originally targeted is simply
        // never exercised for THIS pair of call sites anymore -- a stronger guarantee than surviving
        // the overwrite. The count stays as the shutdown backstop's "is anything still in flight"
        // signal regardless.
        // Windows-run finding (windows_tests.md section 5): this failed on ARM64 Windows with
        // TimeoutException instead of InvalidOperationException, after 4 s. NOT reproduced on Linux --
        // five consecutive runs pass in under a millisecond each.
        //
        // Suspected cause is thread-pool starvation induced by this test itself, not a production
        // race: BeforeSetPtt is a SYNCHRONOUS hook, and the body below blocks on two async operations
        // inside it with GetAwaiter().GetResult() while the outer call is still on the pool. If the
        // pool has not yet grown, those continuations cannot get a thread, the 300 ms cleanup waits
        // expire, and the outer call surfaces a timeout rather than the guard's rejection. Production
        // never blocks a PTT callback this way -- BeforeSetPtt exists only on the fake radio.
        //
        // Guaranteeing spare threads removes that failure mode without weakening any assertion. If
        // Windows still fails after this, the cause is NOT starvation and the race is real -- which is
        // why it is fixed this way rather than by widening the timeout.
        ThreadPool.GetMinThreads(out var minWorker, out var minIo);
        ThreadPool.SetMinThreads(Math.Max(minWorker, 16), minIo);

        var stage = 0;
        var (service, _, radio, _) = CreateService(wrapEngine: inner => new ThrowOnStartPlaybackAudioEngine(inner));
        radio.BeforeSetPtt = tx =>
        {
            if (!tx && stage == 1)
            {
                stage = 2;
#pragma warning disable xUnit1031
                // The NEWER call: overlaps the OLDER one (which is still executing this very un-key
                // command) -- now rejected outright by the single-flight guard.
                var ex = Assert.ThrowsAsync<InvalidOperationException>(() => service.TuneAsync(TxLevelFamily.Sstv, 1750, TimeSpan.FromMilliseconds(1))).GetAwaiter().GetResult();
                Assert.Equal("A transmit or tune is already in progress.", ex.Message);

                service.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore xUnit1031
            }
        };

        stage = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TuneAsync(TxLevelFamily.Sstv, 1750, TimeSpan.FromMilliseconds(1)));

        // THE property: the newer call never keyed or un-keyed anything (rejected before touching PTT
        // at all) -- 1 key (older) + 2 un-key attempts (DisposeAsync's backstop, fired while reentrant
        // and unable to observe the older call's own in-flight completion, then the older call's own
        // recovery) = 3 total PTT commands, not 5.
        Assert.Equal(3, radio.PttCalls.Count);
        Assert.Equal(1, radio.PttCalls.Count(c => c));
        Assert.Equal(2, radio.PttCalls.Count(c => !c));
    }

    // ------------------------------------------------------------------ round 12 findings

    [Fact]
    public async Task Round12_PlayWithPttAsync_RejectsOverlappingCall_BeforeTouchingPttOrPlayback()
    {
        // Round-12 finding: a second overlapping PlayWithPttAsync call used to silently re-key and
        // tear down the FIRST call's own live playback session (its own StartPlaybackAsync throws
        // "already started", caught generically, and the resulting cleanup un-keys + disposes the
        // FIRST call's playback mid-frame). The single-flight guard (_transmitInFlight) now rejects
        // the second call outright, before it touches PTT or playback at all.
        var (service, _, radio, _) = CreateService();
        radio.BeforeSetPtt = tx =>
        {
            if (tx)
            {
                // A second call attempted while the first is already past the guard and mid-key --
                // must be rejected immediately.
#pragma warning disable xUnit1031
                var ex = Assert.ThrowsAsync<InvalidOperationException>(() => service.TransmitAsync(TestMode, TestImage)).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                Assert.Equal("A transmit or tune is already in progress.", ex.Message);
            }
        };

        await service.TuneAsync(TxLevelFamily.Sstv, 1750, TimeSpan.FromMilliseconds(1));

        // THE property: only ONE key + ONE un-key -- the rejected second call never touched PTT.
        Assert.Equal([true, false], radio.PttCalls);
    }

    // ------------------------------------------------------------------ round 13 findings

    [Fact]
    public async Task Round13_EnqueueAllAsync_WedgedPlaybackDevice_TimesOutRatherThanStallingForever()
    {
        // Round-13 finding: EnqueueAllAsync's "buffer full, wait 10ms, retry" loop had no bound -- a
        // wedged playback device (EnqueuePlaybackSamples always returning 0) stalled TransmitAsync
        // forever WHILE PTT stayed keyed, and (amplified by round 12's own single-flight guard) that
        // stall permanently locked out every future transmit/tune for the process, since nothing could
        // ever clear _transmitInFlight. The stall timeout now throws TimeoutException, which routes
        // through PlayWithPttAsync's generic catch -> abnormalTermination -> urgent un-key BEFORE
        // StopPlayback, so a wedged device can no longer delay getting the transmitter off the air.
        var (service, _, radio, logger) = CreateService(
            wrapEngine: inner => new WedgedPlaybackAudioEngine(inner),
            playbackStallTimeout: TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => service.TransmitAsync(TestMode, TestImage));
        Assert.Contains("wedged", ex.Message, StringComparison.OrdinalIgnoreCase);

        // THE property: PTT went on, then urgently back off -- never left keyed by the stall.
        Assert.Equal(PttOnThenOff, radio.PttCalls);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Playback failed", StringComparison.OrdinalIgnoreCase));

        // Single-flight guard must have been released too -- a second transmit after the wedge is
        // cleared (fresh service call would still hit the same wedged fake, but this proves
        // _transmitInFlight itself was reset rather than permanently stuck from the aborted call).
        await Assert.ThrowsAsync<TimeoutException>(() => service.TransmitAsync(TestMode, TestImage));
        Assert.Equal([true, false, true, false], radio.PttCalls);
    }

    // ------------------------------------------------------------------ round 14 findings

    [Fact]
    public async Task Round14_PlayWithPttAsync_KeyCommandHangs_TimesOutAndStillUnkeysRatherThanLeavingPttKeyedForever()
    {
        // Round-14 finding 1: SetPttAsync(true, ct) had no bound of its own -- RigctldClientProtocol
        // bounds only its initial connect, not the per-command reply read, so a half-open CAT
        // connection can block this forever WHILE THE COMMAND HAS ALREADY REACHED THE WIRE AND
        // PHYSICALLY KEYED THE RIG. HangOnCallNumber:1 hangs only the key command (call #1) --
        // the cleanup un-key (call #2) must still get through and succeed, proving the fix's whole
        // point: a hung key command no longer prevents the un-key from ever being attempted.
        var (service, _, radio, logger) = CreateService(cleanupTimeout: TimeSpan.FromMilliseconds(50));
        radio.HangOnCallNumber = 1;

        await Assert.ThrowsAsync<TimeoutException>(() => service.TransmitAsync(TestMode, TestImage));

        // THE property: the cleanup un-key (call #2) got through and succeeded -- never blocked by
        // the still-pending, abandoned key command. (Call #1 itself never reaches PttCalls.Add --
        // it is still parked on _neverCompletes when the test ends, same accepted trade-off as
        // EnqueueAllAsync's own round-13 stall fix.)
        Assert.Equal([false], radio.PttCalls);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Playback failed", StringComparison.OrdinalIgnoreCase));

        // _transmitInFlight must have been released too -- a subsequent, non-hanging transmit must
        // succeed cleanly rather than being rejected as "already in progress."
        radio.HangOnCallNumber = null;
        await service.TransmitAsync(TestMode, TestImage);
        Assert.Equal([false, true, false], radio.PttCalls);
    }

    [Fact]
    public async Task Round14_StartPlaybackAsync_Hangs_TimesOutAndStillUnkeysRatherThanLeavingPttKeyedForever()
    {
        // Round-14 finding 2: same unbounded-await shape as finding 1, one step later -- PTT is
        // already keyed by the time StartPlaybackAsync runs, so an indefinite native device-open
        // hang here is exactly as much a leaked-keyed-transmitter exposure as the key command
        // itself, not a smaller-window variant of it (this was round 13's own mistaken severity
        // call, corrected here).
        var neverCompletes = new TaskCompletionSource();
        var (service, _, radio, logger) = CreateService(
            wrapEngine: inner => new GatedStartPlaybackAudioEngine(inner, neverCompletes.Task),
            cleanupTimeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() => service.TransmitAsync(TestMode, TestImage));

        // THE property: PTT went on, then urgently back off -- never left keyed by the hang.
        Assert.Equal(PttOnThenOff, radio.PttCalls);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Playback failed", StringComparison.OrdinalIgnoreCase));

        // _transmitInFlight must have been released too -- a subsequent, non-hanging transmit (the
        // gate only ever affects the FIRST StartPlaybackAsync call, so the abandoned first call stays
        // parked on `neverCompletes` forever rather than resolving and colliding with this one) must
        // succeed cleanly rather than being rejected as "already in progress."
        await service.TransmitAsync(TestMode, TestImage);
        Assert.Equal([true, false, true, false], radio.PttCalls);
    }

    // ------------------------------------------------------------------ round 15 findings

    [Fact]
    public async Task Round15_PlayWithPttAsync_ResolveDeviceAsync_Hangs_TimesOutRatherThanStrandingTransmitInFlightForever()
    {
        // Round-15 finding 3: three pre-key awaits (ResolveDeviceAsync, GetTxVolumePercentAsync,
        // LoadAudioSettingsAsync) had no bound of their own either -- PTT is NOT yet keyed at this
        // point (not the leaked-keyed-transmitter class), but a hang in any of them still permanently
        // strands _transmitInFlight (round 12's single-flight guard), killing TX for the rest of the
        // process's life. ResolveDeviceAsync's own happy path calls LoadAudioSettingsAsync internally
        // (TryResolveDeviceAsync), so gating the settings store's own LoadAsync hangs the FIRST of the
        // three -- sufficient to prove the general WaitAsync mechanism works; the other two share the
        // identical fix shape. Uses TuneAsync, not TransmitAsync: TransmitAsync's OWN preamble
        // (GetStationIdTransmitOptionsAsync) ALSO reads settings, unbounded, BEFORE PlayWithPttAsync is
        // even entered -- a separate bug this same round found and fixed by accident while writing
        // this test (see that method's own comment) -- and gating the shared settings store would hit
        // THAT bound first, never reaching the one this test targets. TuneAsync has no such preamble.
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = "capture-1", PlaybackDeviceId = "playback-1", SampleRate = 8000 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
            Gate = new TaskCompletionSource().Task,
        };
        var (service, _, radio, logger) = CreateService(cleanupTimeout: TimeSpan.FromMilliseconds(50), settingsStore: settingsStore);

        await Assert.ThrowsAsync<TimeoutException>(() => service.TuneAsync(TxLevelFamily.Sstv, 1750, TimeSpan.FromMilliseconds(1)));

        // THE property: PTT was never KEYED (the hang is entirely pre-key, so no `true` call) -- the
        // single `false` is the cleanup's own deliberately-unconditional un-key attempt (this class's
        // established safety-net behavior, harmless here), and the failure is still logged.
        Assert.Equal([false], radio.PttCalls);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Playback failed", StringComparison.OrdinalIgnoreCase));

        // _transmitInFlight must have been released too -- a subsequent, non-hanging tune must
        // succeed cleanly rather than being rejected as "already in progress."
        settingsStore.Gate = null;
        await service.TuneAsync(TxLevelFamily.Sstv, 1750, TimeSpan.FromMilliseconds(1));
        Assert.Equal([false, true, false], radio.PttCalls);
    }

    [Fact]
    public async Task Round15_GetStationIdTransmitOptionsAsync_SettingsReadHangs_TimesOutRatherThanHangingForever()
    {
        // Round-15 (discovered while testing finding 3 above, not itself in the auditor's report):
        // TransmitAsync's own preamble reads settings via GetStationIdTransmitOptionsAsync BEFORE
        // PlayWithPttAsync is ever entered -- unbounded, the same external-settings-read shape as
        // finding 3's three awaits, but reached earlier and NOT covered by _transmitInFlight (never
        // acquired at this point, since PlayWithPttAsync hasn't been called yet), so a hang here
        // doesn't strand any OTHER call the way finding 3's does -- still a real, unbounded wait on a
        // settings read worth closing, with the same fix shape.
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = "capture-1", PlaybackDeviceId = "playback-1", SampleRate = 8000 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
            Gate = new TaskCompletionSource().Task,
        };
        var (service, _, radio, _) = CreateService(cleanupTimeout: TimeSpan.FromMilliseconds(50), settingsStore: settingsStore);

        await Assert.ThrowsAsync<TimeoutException>(() => service.TransmitAsync(TestMode, TestImage));

        // THE property: PlayWithPttAsync was never entered -- no PTT calls at all, since both the
        // hang and its timeout happen entirely before the key command's own guarded region.
        Assert.Empty(radio.PttCalls);

        // A subsequent, non-hanging transmit must succeed cleanly.
        settingsStore.Gate = null;
        await service.TransmitAsync(TestMode, TestImage);
        Assert.Equal([true, false], radio.PttCalls);
    }

    // ------------------------------------------------------------------ round 16 findings

    [Fact]
    public async Task Round16_PlayWithPttAsync_RxResumeHangsIgnoringToken_TimesOutRatherThanStrandingTransmitInFlightForever()
    {
        // Round-16 finding 1: StartReceivingAsync(rxResumeCts.Token) alone never actually bounded
        // PlayWithPttAsync's own RX-resume steps -- MiniAudioDeviceEnumerator.RefreshAsync/
        // MiniAudioEngine.StartCaptureAsync only check `ct` at their own start/lock-acquire boundary,
        // never during the blocking native call itself, so a token cancelling mid-call does not
        // unblock a genuinely wedged device. GatedStartCaptureAudioEngine ignores its own `ct`
        // entirely (unlike FakeAudioDeviceEnumerator.Gate, which respects it and so could never have
        // caught this gap) -- hangOnCallNumber: 2 targets the RESUME call specifically, not the
        // initial StartReceivingAsync below.
        var gate = new TaskCompletionSource().Task;
        var (service, _, radio, logger) = CreateService(
            wrapEngine: inner => new GatedStartCaptureAudioEngine(inner, gate, hangOnCallNumber: 2),
            cleanupTimeout: TimeSpan.FromMilliseconds(50));

        await service.StartReceivingAsync();

        // Round-11 precedent: wrapped in WaitAsync(TimeSpan), not just awaited directly -- reverting
        // the fix parks the resume forever, so an un-guarded await would hang the whole test run
        // instead of failing this one test loudly and immediately.
        await service.TransmitAsync(TestMode, TestImage).WaitAsync(TimeSpan.FromSeconds(5));

        // THE property: the hung resume was swallowed (TryCleanupAsync's own best-effort contract,
        // unchanged by this fix) and logged, NOT left to hang the whole transmit -- and RX is
        // correctly NOT marked receiving (the resume never got far enough to set _isReceiving = true).
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Resume RX", StringComparison.Ordinal));
        Assert.False(service.IsReceiving);

        // _transmitInFlight must have been released too -- a subsequent transmit (RX not running this
        // time, so it never touches the still-hung gate at all) must succeed cleanly.
        await service.TransmitAsync(TestMode, TestImage);
        Assert.Equal([true, false, true, false], radio.PttCalls);
    }

    [Fact]
    public async Task Round16_StopReceivingAsync_StopCaptureHangs_TimesOutRatherThanHangingForever()
    {
        // Round-16 finding 2: IAudioEngine.StopCaptureAsync takes no CancellationToken at all, and
        // its drain-thread join is unbounded by design -- StopReceivingAsync now bounds the wait with
        // WaitAsync and treats RX as stopped (_isReceiving = false) regardless, mirroring
        // StopPlaybackWithWatchdogAsync's own established shape for the playback side.
        var gate = new TaskCompletionSource().Task;
        var (service, _, _, logger) = CreateService(
            wrapEngine: inner => new GatedStopCaptureAudioEngine(inner, gate),
            cleanupTimeout: TimeSpan.FromMilliseconds(50));

        await service.StartReceivingAsync();

        await service.StopReceivingAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // THE property: the call returned (did not hang) despite the underlying stop never
        // completing, IsReceiving correctly reflects "stopped" (the handlers are already detached
        // regardless of the stop's own outcome), and the timeout was logged, not silently swallowed.
        Assert.False(service.IsReceiving);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("StopCapture did not finish within", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Round16_TxVolumePercent_OutOfRangeSettingsValue_ClampedOnReadAndWrite()
    {
        // Round-16 finding 3: TxVolumePercent flowed straight into PlayWithPttAsync's `gain`
        // multiplier with no range check at all -- a corrupted/hand-edited settings.json (the same
        // threat model GetStationIdTransmitOptionsAsync's own WPM/tone-frequency validation already
        // codes against) could put an arbitrary multiplier on the transmitted audio.
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { TxVolumePercent = 99999 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var (service, _, _, _) = CreateService(settingsStore: settingsStore);

        // Read-side clamp.
        Assert.Equal(100, await service.GetTxVolumePercentAsync(TxLevelFamily.Sstv));

        // Write-side clamp -- an out-of-range value passed to the setter must not reach disk
        // unclamped either, otherwise every future read would keep needing the read-side clamp to
        // mask what's actually stored. Inspects the STORED value directly via settingsStore, not
        // through GetTxVolumePercentAsync -- reading it back through the getter would pass even with
        // the write-side clamp mutated away, since the read-side clamp masks an unclamped write too.
        await service.SetTxVolumePercentAsync(TxLevelFamily.Sstv, -20);
        var stored = settingsStore.Settings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings);
        Assert.Equal(0, stored?.TxVolumePercent);
    }

    // ------------------------------------------------------------------ round 17 findings

    [Fact]
    public async Task Round17_TryUnkeyPttAsync_UnkeyCommandHangs_TimesOutRatherThanHangingForever()
    {
        // Round-17 finding 2 (the single most safety-critical await in the file): TryUnkeyPttAsync's
        // own un-key command had no REAL bound -- UnkeyForCleanupAsync's fresh unkeyCts.Token was
        // passed as the callee's own `ct` parameter, which round 16 already proved does not bound
        // either shipped backend's native/protocol call. This is the exact same mistake, one call
        // away from the whole reason this file exists: PlayWithPttAsync's finally and (worst of all)
        // DisposeAsync's own backstop both funnel through this one method.
        // ThrowOnStartPlaybackAudioEngine forces an abnormal termination deterministically (throws
        // from StartPlaybackAsync, AFTER PTT is keyed but before any sample is pumped) so the cleanup
        // un-key is the SECOND SetPttAsync call, which HangOnCallNumber targets specifically -- the
        // FIRST call (the key itself) must succeed normally.
        var (service, _, radio, logger) = CreateService(
            wrapEngine: inner => new ThrowOnStartPlaybackAudioEngine(inner),
            cleanupTimeout: TimeSpan.FromMilliseconds(50));
        radio.HangOnCallNumber = 2;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransmitAsync(TestMode, TestImage));

        // THE property: the hung un-key timed out rather than hanging PlayWithPttAsync's own finally
        // forever, and the operator is told loudly (Critical) rather than the app just stalling.
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("MAY STILL BE KEYED", StringComparison.Ordinal));

        // _transmitInFlight must have been released too -- a subsequent transmit must reach the SAME
        // (still-throwing, by design) engine again rather than being rejected as "already in
        // progress." Round-11 precedent: wrapped in WaitAsync(TimeSpan) so a bug here fails this one
        // test loudly instead of hanging the whole run.
        radio.HangOnCallNumber = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransmitAsync(TestMode, TestImage).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // ------------------------------------------------------------------ round 18 findings

    [Fact]
    public async Task Round18_TryUnkeyPttAsync_UnkeyCommandTimesOutButStillReachesTheRig_NotCancelledAtTheGate()
    {
        // Round-18 finding 1: round 17's own WaitAsync(ct) fix bounded the WAIT but ALSO passed `ct`
        // to the command itself -- once the budget expired, the un-key was CANCELLED AT THE
        // BACKEND'S REQUEST GATE and never actually reached the rig, rather than staying queued
        // behind whatever wedged it and reaching the rig once that clears. GateOnCallNumber:2 parks
        // only the cleanup un-key (call #2) on a gate this test CAN complete later -- unlike
        // HangOnCallNumber's permanent hang -- specifically to prove the command is still alive and
        // completable after this method has already given up waiting on it.
        var gateTcs = new TaskCompletionSource();
        var (service, _, radio, logger) = CreateService(
            wrapEngine: inner => new ThrowOnStartPlaybackAudioEngine(inner),
            cleanupTimeout: TimeSpan.FromMilliseconds(50));
        radio.Gate = gateTcs.Task;
        radio.GateOnCallNumber = 2;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransmitAsync(TestMode, TestImage).WaitAsync(TimeSpan.FromSeconds(5)));

        // The urgent un-key (call #2) timed out and Critical fired -- but finding 2's own retry (see
        // its own test) then made a SECOND attempt (call #3, not gated), which succeeded and is the
        // one `false` recorded here. Call #2 itself is still parked on the gate at this point --
        // still alive, not recorded, not cancelled.
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("MAY STILL BE KEYED", StringComparison.Ordinal));
        Assert.Equal([true, false], radio.PttCalls);

        // THE property: releasing the gate now lets the originally-abandoned call #2 actually
        // complete and reach the rig too (a second, redundant-but-harmless `false`) -- round 17's own
        // ct-passthrough would have cancelled it before this point instead.
        gateTcs.SetResult();
        await WaitForAsync(() => radio.PttCalls.Count == 3, TimeSpan.FromSeconds(5));
        Assert.Equal([true, false, false], radio.PttCalls);
    }

    [Fact]
    public async Task Round18_PlayWithPttAsync_UrgentUnkeyFails_RetriesRatherThanGivingUpAfterOneAttempt()
    {
        // Round-18 finding 2: UnkeyForCleanupAsync's own bool return (whether the un-key was actually
        // CONFIRMED) used to be discarded at both PlayWithPttAsync cleanup call sites -- an urgent
        // un-key that failed against a momentarily-busy backend got exactly one attempt, then
        // deferred all the way to DisposeAsync. The first un-key attempt fails deterministically here
        // (BeforeSetPtt throws on the first tx=false call only); the fix's own retry, gated on
        // "abnormal termination AND not yet confirmed," must fire a second attempt, which succeeds.
        var attemptCount = 0;
        var (service, _, radio, _) = CreateService(wrapEngine: inner => new ThrowOnStartPlaybackAudioEngine(inner));
        radio.BeforeSetPtt = tx =>
        {
            if (!tx)
            {
                attemptCount++;
                if (attemptCount == 1)
                {
                    throw new TimeoutException("simulated: momentarily busy backend");
                }
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransmitAsync(TestMode, TestImage));

        // THE property: two attempts were made, not one -- the second (successful) attempt is the
        // only one recorded in PttCalls (the first threw before reaching PttCalls.Add).
        Assert.Equal(2, attemptCount);
        Assert.Equal([true, false], radio.PttCalls);
    }

    [Fact]
    public async Task Round18_TuneAsync_InvalidFrequencyOrDuration_ThrowsBeforeTouchingPtt()
    {
        // Round-18 finding 3 (round-17's own deferral (b), now fixed): neither parameter was
        // validated at all -- an out-of-range value used to reach GenerateTone/PumpToPlaybackAsync
        // directly, WITH PTT KEYED. Every rejected call here must never touch PTT at all.
        var (service, _, radio, _) = CreateService();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.TuneAsync(TxLevelFamily.Sstv, double.NaN, TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.TuneAsync(TxLevelFamily.Sstv, -100, TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.TuneAsync(TxLevelFamily.Sstv, 30_000, TimeSpan.FromSeconds(1))); // >= Nyquist at the hardcoded 48kHz
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.TuneAsync(TxLevelFamily.Sstv, 1750, TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.TuneAsync(TxLevelFamily.Sstv, 1750, TimeSpan.FromMinutes(10)));

        Assert.Empty(radio.PttCalls);

        // A valid call still works normally.
        await service.TuneAsync(TxLevelFamily.Sstv, 1750, TimeSpan.FromMilliseconds(1));
        Assert.Equal([true, false], radio.PttCalls);
    }

    // ------------------------------------------------------------------ round 19 findings

    [Fact]
    public async Task Round19_PlayWithPttAsync_UnkeyThrowsFromLoggingFailure_StillCompletesAndReleasesGuard()
    {
        // Round-19 finding 1 (blocker): PlayWithPttAsync's cleanup region had a finally but NO catch
        // -- UnkeyForCleanupAsync/TryUnkeyPttAsync are documented as never throwing, but that was
        // never actually enforced; a throwing logging provider (simulated via RecordingLogger's own
        // ThrowOnMessageContaining hook) is enough to violate it. Without this round's fix, the throw
        // would skip StopPlayback/the retry/RX-resume entirely, with the un-key failure never
        // recorded anywhere and _transmitInFlight potentially stuck (the OUTER finally, further down,
        // still releases it regardless -- but every step BETWEEN this one and that outer finally would
        // be skipped).
        var (service, _, radio, logger) = CreateService(wrapEngine: inner => new ThrowOnStartPlaybackAudioEngine(inner));
        radio.BeforeSetPtt = tx =>
        {
            if (!tx)
            {
                throw new TimeoutException("simulated: unkey command failed");
            }
        };
        // Precise match: "'PTT off' failed" (with the closing quote right after "off") matches ONLY
        // TryUnkeyPttAsync's own inner Log.CleanupStepFailed(_logger, "PTT off", ex) call -- NOT this
        // round's own new wrapper logs ("PTT off (urgent)"/"PTT off (retry)"), which must still
        // succeed and are what proves the fix actually catches the propagated failure.
        logger.ThrowOnMessageContaining = "'PTT off' failed";

        // THE property: the ORIGINAL exception (from the forced abnormal termination) still surfaces
        // -- not replaced or masked by the logging failure -- and the call completes rather than
        // hanging or crashing unexpectedly.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransmitAsync(TestMode, TestImage).WaitAsync(TimeSpan.FromSeconds(5)));

        // _transmitInFlight must have been released too -- a subsequent transmit must reach the SAME
        // (still-throwing, by design) engine again rather than being rejected as "already in
        // progress."
        logger.ThrowOnMessageContaining = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransmitAsync(TestMode, TestImage).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TierBAuditFinding_TryResolveDeviceAsync_UsingDefaultDeviceLogThrows_DoesNotMaskTheAlreadyResolvedDevice()
    {
        // Tier B audit finding (Area 1 of the SstvSessionService.cs concurrency-cadence sweep):
        // Log.UsingDefaultDevice used to sit unwrapped on the SUCCESS path -- a throwing logging
        // provider there would throw out of TryResolveDeviceAsync AFTER a device was already
        // successfully resolved, taking down every caller (StartReceivingAsync, TransmitAsync/
        // TuneAsync, both GetConfigured*DeviceNameAsync readouts). Same shape an earlier round
        // already fixed for Log.RxStarted/Log.RxStopped (the test right above this one) -- SafeLog
        // now guards it, so a logging fault must not mask an otherwise-successful device
        // resolution.
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [8000])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [11025], IsDefault: true)],
        };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = "capture-1", PlaybackDeviceId = null, SampleRate = 8000 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var (service, engine, _, logger) = CreateService(deviceEnumerator: deviceEnumerator, settingsStore: settingsStore);
        logger.ThrowOnMessageContaining = "using backend-reported default";

        await service.TransmitAsync(TestMode, TestImage).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEmpty(((FakeAudioEngine)engine).PlaybackSamples);
    }

    // ------------------------------------------------------------------ round 20 findings

    [Fact]
    public async Task Round20_UnkeyForCleanupAsync_LoggingFailsThroughoutTheWholeUnkeyPath_StateStillLatchedCorrectly()
    {
        // Round-20 finding 1 (blocker): TryUnkeyPttAsync's own "never throws" contract was false --
        // when its OWN log calls failed (a broken logging provider), the throw propagated BEFORE
        // UnkeyForCleanupAsync ever reached its own state-latching write, leaving
        // _pttUnkeyFailedOnRealRig false despite a genuinely failed un-key on a keyed rig --
        // DisposeAsync's own backstop would find nothing to do and the process could exit silently
        // on-air. Fixed via SafeLog (making the logging itself non-throwing) AND an assume-failed-
        // until-confirmed reorder in UnkeyForCleanupAsync (the state write now happens BEFORE the
        // attempt, not after). This test fails the LOGGING for every "PTT off"-related message this
        // whole path can reach (both round-18's own un-key attempts AND round-19's own wrapper
        // catches), proving the state record no longer depends on ANY of them succeeding.
        var (service, _, radio, logger) = CreateService(wrapEngine: inner => new ThrowOnStartPlaybackAudioEngine(inner));
        radio.BeforeSetPtt = tx =>
        {
            if (!tx)
            {
                throw new TimeoutException("simulated: unkey command failed");
            }
        };
        logger.ThrowOnMessageContaining = "PTT off";

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransmitAsync(TestMode, TestImage).WaitAsync(TimeSpan.FromSeconds(5)));

        // Clear the logging failure so DisposeAsync's own backstop attempt (and its own logging) can
        // proceed normally.
        logger.ThrowOnMessageContaining = null;

        // THE property: DisposeAsync's backstop must still fire -- proving _pttUnkeyFailedOnRealRig
        // was correctly latched despite every log call along the way failing.
        await service.DisposeAsync();
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("MAY STILL BE KEYED", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ round 21 findings

    [Fact]
    public async Task Round21_StartReceivingAsync_DisposedMidFlightAfterCaptureStarts_ClosesTheOrphanedCaptureSession()
    {
        // Round-21 re-raised finding: StartReceivingAsync's own second ObjectDisposedException check
        // (round 19's own fix, just before publishing) used to throw with no cleanup of its own -- by
        // the time this check runs, _audioEngine.StartCaptureAsync has already genuinely opened a
        // native capture session, but _isReceiving is still false (it only flips true just below this
        // check), so a subsequent StopReceivingAsync call unconditionally early-returns via its own
        // `if (!_isReceiving) return;` guard and this session is never closed. Rounds 18/19 made an
        // abandoned/timed-out RX-resume (via Task.Run) the NORMAL way to reach this branch, not an
        // exotic race, so this was a real session/device/thread leak at every shutdown that raced a
        // resume this way, not just a theoretical one.
        var gate = new TaskCompletionSource();
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = "capture-1", PlaybackDeviceId = "playback-1", SampleRate = 8000 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
            Gate = gate.Task,
        };
        var stopCaptureCount = 0;
        var (service, _, _, _) = CreateService(
            wrapEngine: inner => new RecordingOrderAudioEngine(inner, onStopPlayback: () => { }, onStopCapture: () => stopCaptureCount++),
            settingsStore: settingsStore);

        // Parked inside a _settingsStore.LoadAsync call that StartReceivingAsync makes BEFORE ever
        // calling StartCaptureAsync -- so the concurrent DisposeAsync below can genuinely race ahead
        // of it, exactly as an abandoned/timed-out Task.Run-backed resume would.
        var startReceiving = service.StartReceivingAsync();
        Assert.False(startReceiving.IsCompleted, "StartReceivingAsync should still be parked on the settings-store gate");

        // DisposeAsync sets `_disposed = true` as its own very first line (the same established
        // property round 8's own test relies on), then completes -- StopReceivingAsync no-ops here.
        // Tier B audit finding (Area 3): since _rxTransitionGate's own fix, this is no longer via the
        // `_isReceiving` guard (the gate is held by the parked Start above) -- it now no-ops via that
        // gate wait's own bounded timeout (_cleanupTimeout, 300ms here), logging and returning without
        // touching the audio engine. This test is exactly why that wait had to be BOUNDED: an
        // unbounded/uncancellable wait here would deadlock this line against the gate release below,
        // which only runs AFTER this await returns.
        await service.DisposeAsync();

        // Release the gate: StartReceivingAsync now proceeds through StartCaptureAsync (genuinely
        // opening the session) and reaches its own second ObjectDisposedException check with
        // _disposed already true.
        gate.SetResult();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => startReceiving.WaitAsync(TimeSpan.FromSeconds(5)));

        // THE property: the session StartCaptureAsync just opened must be closed before this method
        // throws, not left orphaned for nothing to ever close.
        Assert.Equal(1, stopCaptureCount);
    }

    // ------------------------------------------------------------------ round 22 findings

    [Fact]
    public async Task Round22_PlayWithPttAsync_CancellationLoggingFails_OperationCanceledExceptionStillPropagates()
    {
        // Round-22 finding (risk): a throwing log call in the OperationCanceledException catch arm
        // used to REPLACE that OCE as what actually propagates out of PlayWithPttAsync --
        // TxControlsPaneViewModel's own catch (OperationCanceledException) branches on this exact
        // exception identity to distinguish an SWR-cutoff abort from a generic failure (same reasoning
        // round 20 already applied to RaiseCapturePausedForTransmitChanged's own log call). Exercised
        // end-to-end via TuneAsync/cts.Cancel(), the same shape TxControlsPaneViewModel.Dispose() uses
        // (round-8's own established pattern, see Blocker3_DisposeAsync_WaitsForAnInFlightKeyedTransmitsOwnUnkey).
        var (service, _, radio, logger) = CreateService(inFlightKeyedTransmitWait: TimeSpan.FromSeconds(5));
        logger.ThrowOnMessageContaining = "Playback cancelled";

        using var cts = new CancellationTokenSource();
        var tune = service.TuneAsync(TxLevelFamily.Sstv, 1750, TimeSpan.FromSeconds(30), ct: cts.Token);
        await WaitForAsync(() => radio.PttCalls.Count == 1, TimeSpan.FromSeconds(5));

        cts.Cancel();

        // THE property: the ORIGINAL OperationCanceledException must reach the caller -- not whatever
        // SimulatedLoggingProviderFailureException the broken logging provider throws instead.
        await Assert.ThrowsAsync<OperationCanceledException>(() => tune);

        // PTT itself must still be correctly un-keyed regardless of the logging failure.
        logger.ThrowOnMessageContaining = null;
        await service.DisposeAsync();
        Assert.Equal(PttOnThenOff, radio.PttCalls);
    }

    // ------------------------------------------------------------------ round 23 findings

    [Fact]
    public async Task Round23_PlayWithPttAsync_PttKeyedLoggingFails_TransmitStillCompletesNormally()
    {
        // Round-23 finding (risk): Log.PttKeyed is the ONE log call in the whole file that executes
        // while a real transmitter is physically keyed -- was still unwrapped. State (_pttKeyEpoch,
        // pttKeyedOnRealRig) is always latched correctly BEFORE this call, so a throw here was never
        // a leaked-transmitter (failure class 1) risk -- but it WAS a transmission-destruction risk
        // (failure class 2): a transient logging-provider failure landing in this exact window used
        // to abort an otherwise-healthy transmit with the raw logging exception, immediately after
        // key-up and before any audio was ever sent -- a bare carrier key-up/key-down burst on air.
        var (service, _, radio, logger) = CreateService();
        logger.ThrowOnMessageContaining = "PTT keyed";

        // THE property: the fix (SafeLog) swallows the transient logging failure -- the transmit
        // must complete normally, not abort.
        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(PttOnThenOff, radio.PttCalls);
    }

    // ------------------------------------------------------------------ round 26 findings

    [Fact]
    public async Task Round26_TryUnkeyPttAsync_AbandonedCommandLaterFails_ObservedNotSilentlyLost()
    {
        // Round-26 finding (risk): TryUnkeyPttAsync's own abandoned unkeyTask (once WaitAsync gives up
        // on the cleanup budget) had no fault-observer, unlike every OTHER abandoned task in this class
        // (StopReceivingAsync, StopPlaybackWithWatchdogAsync, ResumeReceivingBoundedAsync) -- a late
        // failure behind a wedged un-key command, on the single most safety-critical await in the
        // file, surfaced only as an unobserved task exception, never logged.
        var gate = new TaskCompletionSource();
        var (service, _, radio, logger) = CreateService(cleanupTimeout: TimeSpan.FromMilliseconds(50));
        radio.Gate = gate.Task;
        radio.GateOnCallNumber = 2; // the cleanup un-key, after the key command (call #1)
        radio.BeforeSetPtt = tx =>
        {
            if (!tx)
            {
                throw new TimeoutException("simulated: un-key command eventually failed after the watchdog gave up");
            }
        };

        // Transmit keys (call #1), then its own cleanup un-key (call #2) parks on the gate --
        // TryUnkeyPttAsync's own WaitAsync gives up after cleanupTimeout (50ms), well before the gate
        // is ever released, leaving the command abandoned but still running.
        await service.TransmitAsync(TestMode, TestImage);

        // Release the gate -- the abandoned command now runs to completion, and (via BeforeSetPtt)
        // fails.
        gate.SetResult();

        // THE property: that late failure must be observed and logged, not silently lost as an
        // unobserved task exception.
        await WaitForAsync(
            () => logger.Entries.Any(e => e.Message.Contains("PTT off (finished after watchdog)", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));
    }

    // ------------------------------------------------------------------ round 27 findings

    [Fact]
    public async Task Round27_PlayWithPttAsync_EarlierUnkeyFailedThenDeviceResolutionFails_AbnormalTerminationStillLatchesCritical()
    {
        // Round-27 finding (risk): pttKeyedOnRealRig's entry baseline must be assigned BEFORE the three
        // bounded device/settings awaits. A failure in any of them (device not found is the realistic
        // one here) used to reach the generic catch with pttKeyedOnRealRig still false even when an
        // EARLIER call's failed unkey had left the rig possibly keyed, downgrading the cleanup failure
        // to a benign no-radio Debug line.
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [8000])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [11025])],
        };
        var (service, _, radio, logger) = CreateService(deviceEnumerator: deviceEnumerator);

        // Every un-key fails -- the first transmit's own cleanup un-key latches "may still be keyed",
        // and the second transmit's cleanup un-key failing too is what exposes whether it was
        // classified Critical (genuinely keyed) or Debug (benign no-radio skip): a SUCCEEDING un-key
        // looks identical either way.
        radio.BeforeSetPtt = tx =>
        {
            if (!tx)
            {
                throw new TimeoutException("simulated: cleanup un-key failed");
            }
        };

        await service.TransmitAsync(TestMode, TestImage);
        Assert.Equal([true], radio.PttCalls);
        var criticalsAfterFirstTransmit = CountStillKeyedCriticals(logger);
        Assert.True(criticalsAfterFirstTransmit > 0);

        // THE property: the second transmit aborts on device resolution (before PTT is ever touched by
        // this call), but the possibly-still-keyed rig must still be reported at Critical again.
        deviceEnumerator.OutputDevices = [];
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransmitAsync(TestMode, TestImage));
        Assert.True(
            CountStillKeyedCriticals(logger) > criticalsAfterFirstTransmit,
            "the second transmit's failed cleanup un-key must be classified Critical, not a no-radio Debug line");
        Assert.Equal([true], radio.PttCalls);
    }

    // ------------------------------------------------------------------ round 28 findings

    [Fact]
    public async Task Round28_PlayWithPttAsync_ConfirmedUnkeyBeforeOwnKeyCommand_CleanupStillAttemptsUnkey()
    {
        // Round-28 finding (blocker): the unkey-epoch snapshot used to be taken at METHOD ENTRY --
        // before this call's own key command -- so a confirmed un-key that happened BEFORE this call
        // keyed the rig was misread, at cleanup time, as "confirmed AFTER I keyed", and the cleanup
        // un-key was wrongly skipped. The two-epoch check only proves "nobody re-keyed since MY key
        // phase" -- it says nothing about whether the confirmed un-key it paired with actually
        // postdates that key, unless both epochs are read at the SAME point.
        //
        // The confirmed un-key here is DisposeAsync's backstop: its bounded wait (50ms) for the
        // in-flight transmit expires while the transmit's key command is still parked, before it ever
        // reaches the wire. cleanupTimeout is large so the parked key command does not time out and
        // take the abnormal path -- the transmit must complete normally and reach the retry gate.
        var keyGate = new TaskCompletionSource();
        var reachedKeyGate = new TaskCompletionSource();
        var (service, _, radio, _) = CreateService(
            cleanupTimeout: TimeSpan.FromSeconds(30),
            inFlightKeyedTransmitWait: TimeSpan.FromMilliseconds(50));
        radio.Gate = keyGate.Task;
        radio.GateOnCallNumber = 1; // the transmit's own key command
        radio.OnCallStarted = callNumber =>
        {
            if (callNumber == 1)
            {
                reachedKeyGate.TrySetResult();
            }
        };

        var transmit = service.TransmitAsync(TestMode, TestImage);

        // Deterministic happens-before point: the transmit's own key command has genuinely started and
        // is now parked at the gate, BEFORE it ever reaches the wire.
        await reachedKeyGate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // DisposeAsync's backstop un-key is confirmed BEFORE the transmit's own key command ever
        // reaches the wire -- says nothing about whether THIS call's own upcoming key stays on.
        await service.DisposeAsync();
        Assert.Equal([false], radio.PttCalls);

        // Release the transmit's own key command -- it now reaches the wire AFTER the confirmed
        // un-key.
        keyGate.SetResult();
        await transmit;

        // THE property: the transmit's own cleanup must still attempt its own un-key.
        Assert.Equal([false, true, false], radio.PttCalls);
    }

    [Fact]
    public async Task PlayWithPttAsync_KeyCommandFailsTwice_SecondUrgentUnkeyFails_RetryIsNotSkipped()
    {
        // Pins the key-epoch bump in PlayWithPttAsync's key-command catch. Transmit #1: key throws,
        // urgent un-key confirms off (unkey epoch advances). Transmit #2: key throws again, so its
        // epoch snapshots stay at their 0 placeholders, and its urgent un-key fails. The retry gate
        // must still retry: only the key epoch having advanced (>= 1) keeps
        // ConfirmedUnkeyAlreadyHappenedSinceOwnKey(0, 0) false -- without the bump it reads the
        // earlier confirmed un-key as covering transmit #2's possibly-keyed rig and skips the retry.
        var (service, _, radio, logger) = CreateService();
        var attempts = 0;
        radio.OnCallStarted = _ => Interlocked.Increment(ref attempts);
        var unkeyCount = 0;
        radio.BeforeSetPtt = tx =>
        {
            if (tx)
            {
                throw new TimeoutException("simulated: PTT key command written, reply read timed out");
            }

            if (++unkeyCount >= 2)
            {
                throw new TimeoutException("simulated: un-key failed");
            }
        };

        await Assert.ThrowsAsync<TimeoutException>(() => service.TransmitAsync(TestMode, TestImage));
        Assert.Equal([false], radio.PttCalls);
        Assert.Equal(2, attempts);

        await Assert.ThrowsAsync<TimeoutException>(() => service.TransmitAsync(TestMode, TestImage));

        // Key #1, urgent un-key #1, key #2, urgent un-key #2, retry un-key #2.
        Assert.Equal(5, attempts);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("Skipping cleanup PTT un-key attempt", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("MAY STILL BE KEYED", StringComparison.Ordinal));
    }

    private static int CountStillKeyedCriticals(RecordingLogger<SstvSessionService> logger) =>
        logger.Entries.Count(e => e.Level == LogLevel.Critical && e.Message.Contains("MAY STILL BE KEYED", StringComparison.Ordinal));

    // ------------------------------------------------------------------ round 31 findings

    [Fact]
    public async Task Round31_PlayWithPttAsync_AbandonedKeyCommandLaterFails_ObservedNotSilentlyLost()
    {
        // Round-31 finding (risk): PlayWithPttAsync's own PTT key command had no fault-observer --
        // the most safety-relevant abandoned command in the whole class: by this method's own
        // reasoning, it may have physically keyed the rig.
        var gate = new TaskCompletionSource();
        var (service, _, radio, logger) = CreateService(cleanupTimeout: TimeSpan.FromMilliseconds(50));
        radio.Gate = gate.Task;
        radio.GateOnCallNumber = 1; // the key command
        radio.BeforeSetPtt = tx =>
        {
            if (tx)
            {
                throw new TimeoutException("simulated: key command eventually failed after the watchdog gave up");
            }
        };

        // The key command parks on the gate; WaitAsync gives up after cleanupTimeout (50ms), well
        // before the gate is ever released -- abnormalTermination fires, and the finally's own urgent
        // un-key (tx=false, call #2, unaffected by BeforeSetPtt and not restricted by
        // GateOnCallNumber) completes normally.
        await Assert.ThrowsAsync<TimeoutException>(() => service.TransmitAsync(TestMode, TestImage));

        // Release the gate -- the abandoned key command now runs to completion, and fails.
        gate.SetResult();

        // THE property: the key command's own late failure must be observed and logged, not
        // silently lost as an unobserved task exception.
        await WaitForAsync(
            () => logger.Entries.Any(e => e.Message.Contains("PTT key command (finished after watchdog)", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));
    }

    // ------------------------------------------------------------------ Tier B Area 3 findings

    [Fact]
    public async Task TierBAuditFinding_ConcurrentStartAndStopReceiving_StopNeverSilentlyNoOpsWhileStartIsMidFlight()
    {
        // Tier B audit finding (Area 3): StartReceivingAsync/StopReceivingAsync's own `_isReceiving`
        // check-then-act had no gate between them. A real UI repro: RadioStatusViewModel.SetReceivingSafeAsync is fire-and-
        // forget with no busy guard, and the same class's own startup maintenance-retry path is a
        // SEPARATE caller hitting StopReceivingAsync/StartReceivingAsync concurrently -- a Start parked mid-flight (e.g. in a slow settings/device
        // read) let a concurrent Stop read `_isReceiving == false` and silently no-op, leaving capture
        // live with the UI reporting "not receiving" and no error surfaced. _rxTransitionGate closes
        // this: Stop now genuinely waits for the racing Start to finish, then acts on the real result.
        var gate = new TaskCompletionSource();
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = "capture-1", PlaybackDeviceId = "playback-1", SampleRate = 8000 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
            Gate = gate.Task,
        };
        // Tier B audit finding (round-2 confirmation nit): StopReceivingAsync's own gate wait is
        // bounded by _cleanupTimeout (see that field's doc comment) -- a generous 30s here so this
        // test's own assertion below is never at the mercy of the default 300ms test budget racing
        // this method's Task.Delay(50) under parallel test-host load. Only this test's PROPERTY
        // (Stop must wait, not no-op) depends on real wall-clock timing; a too-short budget would make
        // the test flake, not silently pass wrong.
        var (service, engine, _, _) = CreateService(settingsStore: settingsStore, cleanupTimeout: TimeSpan.FromSeconds(30));

        // Parked inside _settingsStore.LoadAsync, well before _isReceiving is ever published.
        var startTask = service.StartReceivingAsync();
        Assert.False(startTask.IsCompleted, "StartReceivingAsync should still be parked on the settings-store gate");

        // Before the fix, this returned immediately (no-op, `_isReceiving` still false at this
        // instant). After the fix, it blocks on the same _rxTransitionGate the parked Start holds.
        var stopTask = service.StopReceivingAsync();
        await Task.Delay(50);
        Assert.False(stopTask.IsCompleted, "StopReceivingAsync must wait for the racing Start, not silently no-op past it");

        gate.SetResult();

        await startTask.WaitAsync(TimeSpan.FromSeconds(5));
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));

        // THE property: Stop's command actually landed AFTER Start finished, instead of being dropped
        // -- capture is genuinely stopped and IsReceiving reports the same thing.
        Assert.False(service.IsReceiving);
        Assert.False(((FakeAudioEngine)engine).IsCapturing);
    }

    // ------------------------------------------------------------------ Tier B Area 4 findings

    [Fact]
    public async Task TierBAuditFinding_StartReceivingAsync_TimesOutRatherThanHangingForever_WhenGateIsStrandedByAnAbandonedResume()
    {
        // Tier B audit finding (Area 4): StartReceivingAsync's own _rxTransitionGate wait used to be
        // bounded only by the caller's own `ct` (StopReceivingAsync's identical wait was already
        // bounded by _cleanupTimeout, an asymmetry between two near-identical siblings). An ABANDONED
        // RX-resume that is still genuinely stuck inside StartCaptureAsync (round-16's own established
        // "a native call does not respect `ct` mid-flight" fact) holds this gate forever -- every
        // LATER caller (including the direct UI Start-RX path, CancellationToken.None) then hung
        // indefinitely with no log and no throw. Simulated here directly: park a first Start inside
        // the gate (holding _rxTransitionGate), then prove a second Start with no token of its own
        // times out instead of hanging.
        var startCaptureGate = new TaskCompletionSource();
        var (service, _, _, logger) = CreateService(
            wrapEngine: inner => new GatedStartCaptureAudioEngine(inner, startCaptureGate.Task, hangOnCallNumber: 1),
            cleanupTimeout: TimeSpan.FromMilliseconds(50));

        var firstStart = service.StartReceivingAsync();
        Assert.False(firstStart.IsCompleted, "first StartReceivingAsync should still be parked inside the gated StartCaptureAsync call, still holding _rxTransitionGate");

        // THE property: a second Start, with no token of its own to ever cancel it, still gives up
        // rather than hanging forever behind the first call's own permanently-held gate.
        await Assert.ThrowsAsync<TimeoutException>(() => service.StartReceivingAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains(logger.Entries, e => e.Message.Contains("rxTransitionGate wait timed out", StringComparison.Ordinal));

        // Cleanup: release the gate so the first (still-parked) call can finish and not leak a hung
        // background continuation into a later test.
        startCaptureGate.SetResult();
        await firstStart.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TierBAuditFinding_StartReceivingAsync_CallerGaveUpWhileStartCaptureStillRunning_DoesNotPublishIsReceiving()
    {
        // Tier B audit finding (Area 4): the direct twin of the finding above, one step later --
        // even once the gate itself is acquired, an ABANDONED RX-resume (ResumeReceivingBoundedAsync's
        // own rxResumeCts already expired) can still be genuinely running StartCaptureAsync well past
        // the point its own caller gave up waiting. Reaching the publish point afterward would turn
        // capture ON regardless of what happened meanwhile -- including a NEWER PlayWithPttAsync call
        // that is by then actively transmitting (capture going live mid-keyed-TX), even though that
        // method's own entry-time `wasReceiving` read correctly saw `false` and skipped pausing RX for
        // it (RaiseCapturePausedForTransmitChanged never even fires). `ct.IsCancellationRequested` is
        // the same reliable "this IS the abandoned resume" signal ResumeReceivingBoundedAsync's own
        // token threading already relies on elsewhere in this file.
        var startCaptureGate = new TaskCompletionSource();
        var stopCaptureCount = 0;
        var (service, _, _, _) = CreateService(
            wrapEngine: inner => new GatedStartCaptureAudioEngine(
                new RecordingOrderAudioEngine(inner, onStopPlayback: () => { }, onStopCapture: () => stopCaptureCount++),
                startCaptureGate.Task, hangOnCallNumber: 1));

        using var cts = new CancellationTokenSource();
        var startTask = service.StartReceivingAsync(cts.Token);
        Assert.False(startTask.IsCompleted, "StartReceivingAsync should still be parked inside the gated StartCaptureAsync call");

        // Simulates ResumeReceivingBoundedAsync's own caller giving up (rxResumeCts firing) WHILE the
        // native call is still genuinely in flight -- the exact abandoned-resume shape.
        cts.Cancel();

        // The native call itself does not respect `ct` (matches MiniAudioEngine's own real contract,
        // and round-16's own established fact for this specific call) -- it still completes normally.
        startCaptureGate.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startTask.WaitAsync(TimeSpan.FromSeconds(5)));

        // THE property: the session StartCaptureAsync just opened must be closed, and _isReceiving
        // must never have been published true -- not left for a later, unrelated PlayWithPttAsync
        // call to see as "already receiving" against a session it doesn't actually still own.
        Assert.False(service.IsReceiving);
        Assert.Equal(1, stopCaptureCount);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < timeout, "condition was never met");
            await Task.Delay(10);
        }
    }

    /// <summary>Decorates <see cref="FakeAudioEngine"/> with a StopPlaybackAsync that parks on a
    /// caller-controlled gate -- the deterministic stand-in for MiniAudioEngine's ~10.2s worst-case
    /// stop on a wedged output device (DrainTimeout + CloseTimeout + DrainTailMargin). A decorator,
    /// not a from-scratch IAudioEngine, so every OTHER member keeps FakeAudioEngine's real,
    /// contract-enforcing behavior.</summary>
    private sealed class GatedStopPlaybackAudioEngine(IAudioEngine inner, Task gate) : IAudioEngine
    {
        public int CaptureOverrunCount => inner.CaptureOverrunCount;

        public event Action<ReadOnlyMemory<float>>? SamplesCaptured
        {
            add => inner.SamplesCaptured += value;
            remove => inner.SamplesCaptured -= value;
        }

        public Task StartCaptureAsync(
            AudioDeviceInfo device, int sampleRate, ThreadPriority? drainThreadPriority = null,
            int periodSizeInFrames = 0, int periods = 0, AudioChannelSource channelSource = AudioChannelSource.Mono,
            CancellationToken ct = default) =>
            inner.StartCaptureAsync(device, sampleRate, drainThreadPriority, periodSizeInFrames, periods, channelSource, ct);

        public Task StopCaptureAsync() => inner.StopCaptureAsync();

        public Task StartPlaybackAsync(
            AudioDeviceInfo device, int sampleRate, int periodSizeInFrames = 0, int periods = 0,
            bool stereoTx = false, CancellationToken ct = default) =>
            inner.StartPlaybackAsync(device, sampleRate, periodSizeInFrames, periods, stereoTx, ct);

        public async Task StopPlaybackAsync()
        {
            await gate.ConfigureAwait(false);
            await inner.StopPlaybackAsync().ConfigureAwait(false);
        }

        public int EnqueuePlaybackSamples(ReadOnlyMemory<float> samples) => inner.EnqueuePlaybackSamples(samples);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>Round-16 finding 2: the capture-side mirror of <see cref="GatedStopPlaybackAudioEngine"/>
    /// -- parks <see cref="StopCaptureAsync"/> on a caller-controlled gate. Stands in for
    /// <c>MiniAudioCaptureSession</c>'s own unbounded-by-design drain-thread join.</summary>
    private sealed class GatedStopCaptureAudioEngine(IAudioEngine inner, Task gate) : IAudioEngine
    {
        public int CaptureOverrunCount => inner.CaptureOverrunCount;

        public event Action<ReadOnlyMemory<float>>? SamplesCaptured
        {
            add => inner.SamplesCaptured += value;
            remove => inner.SamplesCaptured -= value;
        }

        public Task StartCaptureAsync(
            AudioDeviceInfo device, int sampleRate, ThreadPriority? drainThreadPriority = null,
            int periodSizeInFrames = 0, int periods = 0, AudioChannelSource channelSource = AudioChannelSource.Mono,
            CancellationToken ct = default) =>
            inner.StartCaptureAsync(device, sampleRate, drainThreadPriority, periodSizeInFrames, periods, channelSource, ct);

        public async Task StopCaptureAsync()
        {
            await gate.ConfigureAwait(false);
            await inner.StopCaptureAsync().ConfigureAwait(false);
        }

        public Task StartPlaybackAsync(
            AudioDeviceInfo device, int sampleRate, int periodSizeInFrames = 0, int periods = 0,
            bool stereoTx = false, CancellationToken ct = default) =>
            inner.StartPlaybackAsync(device, sampleRate, periodSizeInFrames, periods, stereoTx, ct);

        public Task StopPlaybackAsync() => inner.StopPlaybackAsync();

        public int EnqueuePlaybackSamples(ReadOnlyMemory<float> samples) => inner.EnqueuePlaybackSamples(samples);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>Round-14 finding 2: the mirror-image of <see cref="GatedStopPlaybackAudioEngine"/> --
    /// parks <see cref="StartPlaybackAsync"/> on a caller-controlled gate instead of
    /// <see cref="StopPlaybackAsync"/>. Stands in for MiniAudioPlaybackSession's own synchronous
    /// native device open blocking indefinitely (round 14's own comment on the fix's call site), with
    /// PTT already keyed by the time this runs.</summary>
    private sealed class GatedStartPlaybackAudioEngine(IAudioEngine inner, Task gate) : IAudioEngine
    {
        private int _callCount;

        public int CaptureOverrunCount => inner.CaptureOverrunCount;

        public event Action<ReadOnlyMemory<float>>? SamplesCaptured
        {
            add => inner.SamplesCaptured += value;
            remove => inner.SamplesCaptured -= value;
        }

        public Task StartCaptureAsync(
            AudioDeviceInfo device, int sampleRate, ThreadPriority? drainThreadPriority = null,
            int periodSizeInFrames = 0, int periods = 0, AudioChannelSource channelSource = AudioChannelSource.Mono,
            CancellationToken ct = default) =>
            inner.StartCaptureAsync(device, sampleRate, drainThreadPriority, periodSizeInFrames, periods, channelSource, ct);

        public Task StopCaptureAsync() => inner.StopCaptureAsync();

        public async Task StartPlaybackAsync(
            AudioDeviceInfo device, int sampleRate, int periodSizeInFrames = 0, int periods = 0,
            bool stereoTx = false, CancellationToken ct = default)
        {
            // Only the FIRST call is gated -- if `gate` never completes, that call's own continuation
            // stays permanently abandoned/parked here rather than eventually resuming and colliding
            // (FakeAudioEngine's "already started" guard) with a LATER, deliberately un-gated call a
            // test needs to observe succeeding (e.g. proving _transmitInFlight was released).
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                await gate.ConfigureAwait(false);
            }

            await inner.StartPlaybackAsync(device, sampleRate, periodSizeInFrames, periods, stereoTx, ct).ConfigureAwait(false);
        }

        public Task StopPlaybackAsync() => inner.StopPlaybackAsync();

        public int EnqueuePlaybackSamples(ReadOnlyMemory<float> samples) => inner.EnqueuePlaybackSamples(samples);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>Round-16 finding 1: hangs the N-th call (1-based) to <see cref="StartCaptureAsync"/>
    /// on a caller-controlled gate that IGNORES `ct` entirely -- unlike
    /// <see cref="FakeAudioDeviceEnumerator.Gate"/> (which deliberately DOES respect `ct`, per its own
    /// round-10 comment), this matches the REAL <c>MiniAudioEngine.StartCaptureAsync</c>/
    /// <c>MiniAudioDeviceEnumerator.RefreshAsync</c> contract: `ct` is only checked at the
    /// start/lock-acquire boundary, never during the blocking native call itself, so a token
    /// cancelling mid-call does not unblock it. A ct-respecting fake could never have caught this
    /// gap -- see the round-16 playbook entry for why round 10's own equivalent test passed even
    /// before this fix existed.</summary>
    private sealed class GatedStartCaptureAudioEngine(IAudioEngine inner, Task gate, int hangOnCallNumber) : IAudioEngine
    {
        private int _callCount;

        public int CaptureOverrunCount => inner.CaptureOverrunCount;

        public event Action<ReadOnlyMemory<float>>? SamplesCaptured
        {
            add => inner.SamplesCaptured += value;
            remove => inner.SamplesCaptured -= value;
        }

        public async Task StartCaptureAsync(
            AudioDeviceInfo device, int sampleRate, ThreadPriority? drainThreadPriority = null,
            int periodSizeInFrames = 0, int periods = 0, AudioChannelSource channelSource = AudioChannelSource.Mono,
            CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _callCount) == hangOnCallNumber)
            {
                await gate.ConfigureAwait(false);
            }

            await inner.StartCaptureAsync(device, sampleRate, drainThreadPriority, periodSizeInFrames, periods, channelSource, ct).ConfigureAwait(false);
        }

        public Task StopCaptureAsync() => inner.StopCaptureAsync();

        public Task StartPlaybackAsync(
            AudioDeviceInfo device, int sampleRate, int periodSizeInFrames = 0, int periods = 0,
            bool stereoTx = false, CancellationToken ct = default) =>
            inner.StartPlaybackAsync(device, sampleRate, periodSizeInFrames, periods, stereoTx, ct);

        public Task StopPlaybackAsync() => inner.StopPlaybackAsync();

        public int EnqueuePlaybackSamples(ReadOnlyMemory<float> samples) => inner.EnqueuePlaybackSamples(samples);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>Composable decorator: forces an abnormal termination DETERMINISTICALLY, by throwing
    /// from <see cref="StartPlaybackAsync"/> (which <see cref="PlayWithPttAsync"/> awaits AFTER
    /// keying PTT but BEFORE any tone/image sample is ever generated) -- no real-time race against a
    /// cancellation token and a CPU-bound sample-generation loop's own speed, which is genuinely
    /// machine-dependent (see <see cref="Blocker1_AbnormalTermination_UnkeysBeforeWaitingOnStopPlaybackAtAll"/>'s
    /// own doc comment for the exact failure this replaced).</summary>
    private sealed class ThrowOnStartPlaybackAudioEngine(IAudioEngine inner) : IAudioEngine
    {
        public int CaptureOverrunCount => inner.CaptureOverrunCount;

        public event Action<ReadOnlyMemory<float>>? SamplesCaptured
        {
            add => inner.SamplesCaptured += value;
            remove => inner.SamplesCaptured -= value;
        }

        public Task StartCaptureAsync(
            AudioDeviceInfo device, int sampleRate, ThreadPriority? drainThreadPriority = null,
            int periodSizeInFrames = 0, int periods = 0, AudioChannelSource channelSource = AudioChannelSource.Mono,
            CancellationToken ct = default) =>
            inner.StartCaptureAsync(device, sampleRate, drainThreadPriority, periodSizeInFrames, periods, channelSource, ct);

        public Task StopCaptureAsync() => inner.StopCaptureAsync();

        public Task StartPlaybackAsync(
            AudioDeviceInfo device, int sampleRate, int periodSizeInFrames = 0, int periods = 0,
            bool stereoTx = false, CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated playback-start failure (test double).");

        public Task StopPlaybackAsync() => inner.StopPlaybackAsync();

        public int EnqueuePlaybackSamples(ReadOnlyMemory<float> samples) => inner.EnqueuePlaybackSamples(samples);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>Simulates a wedged playback device -- EnqueuePlaybackSamples always reports 0 samples
    /// accepted, forever. Used to prove EnqueueAllAsync's stall timeout actually bounds the wait
    /// instead of spinning on Task.Delay indefinitely.</summary>
    private sealed class WedgedPlaybackAudioEngine(IAudioEngine inner) : IAudioEngine
    {
        public int CaptureOverrunCount => inner.CaptureOverrunCount;

        public event Action<ReadOnlyMemory<float>>? SamplesCaptured
        {
            add => inner.SamplesCaptured += value;
            remove => inner.SamplesCaptured -= value;
        }

        public Task StartCaptureAsync(
            AudioDeviceInfo device, int sampleRate, ThreadPriority? drainThreadPriority = null,
            int periodSizeInFrames = 0, int periods = 0, AudioChannelSource channelSource = AudioChannelSource.Mono,
            CancellationToken ct = default) =>
            inner.StartCaptureAsync(device, sampleRate, drainThreadPriority, periodSizeInFrames, periods, channelSource, ct);

        public Task StopCaptureAsync() => inner.StopCaptureAsync();

        public Task StartPlaybackAsync(
            AudioDeviceInfo device, int sampleRate, int periodSizeInFrames = 0, int periods = 0,
            bool stereoTx = false, CancellationToken ct = default) =>
            inner.StartPlaybackAsync(device, sampleRate, periodSizeInFrames, periods, stereoTx, ct);

        public Task StopPlaybackAsync() => inner.StopPlaybackAsync();

        public int EnqueuePlaybackSamples(ReadOnlyMemory<float> samples) => 0;

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>Same decorator shape, but records WHEN StopPlayback/StopCapture happen so a test can
    /// assert cleanup ORDER against PTT -- the property blocker 1 and the DisposeAsync reorder are
    /// both really about.</summary>
    private sealed class RecordingOrderAudioEngine(FakeAudioEngine inner, Action onStopPlayback, Action? onStopCapture = null) : IAudioEngine
    {
        public int CaptureOverrunCount => inner.CaptureOverrunCount;

        public event Action<ReadOnlyMemory<float>>? SamplesCaptured
        {
            add => inner.SamplesCaptured += value;
            remove => inner.SamplesCaptured -= value;
        }

        public Task StartCaptureAsync(
            AudioDeviceInfo device, int sampleRate, ThreadPriority? drainThreadPriority = null,
            int periodSizeInFrames = 0, int periods = 0, AudioChannelSource channelSource = AudioChannelSource.Mono,
            CancellationToken ct = default) =>
            inner.StartCaptureAsync(device, sampleRate, drainThreadPriority, periodSizeInFrames, periods, channelSource, ct);

        public Task StopCaptureAsync()
        {
            onStopCapture?.Invoke();
            return inner.StopCaptureAsync();
        }

        public Task StartPlaybackAsync(
            AudioDeviceInfo device, int sampleRate, int periodSizeInFrames = 0, int periods = 0,
            bool stereoTx = false, CancellationToken ct = default) =>
            inner.StartPlaybackAsync(device, sampleRate, periodSizeInFrames, periods, stereoTx, ct);

        public Task StopPlaybackAsync()
        {
            onStopPlayback();
            return inner.StopPlaybackAsync();
        }

        public int EnqueuePlaybackSamples(ReadOnlyMemory<float> samples) => inner.EnqueuePlaybackSamples(samples);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>Same shape as <c>ScanlineStudio.Core.Sstv.Tests</c>' own <c>RecordingLogger&lt;T&gt;</c>
    /// -- reused rather than re-invented. Needed here because blocker 2's fix is observable ONLY
    /// through log severity/identity: the PTT command outcome is identical either way, what changed
    /// is whether a keyed rig is reported as benign or critical.</summary>
    /// <summary>Thrown by <see cref="RecordingLogger{T}.ThrowOnMessageContaining"/> -- a dedicated
    /// type so a mutation-verification test can distinguish "the original exception properly
    /// survived" from "a NEW exception thrown from inside a `finally` REPLACED it" (C# semantics: an
    /// exception thrown inside a `finally` block replaces whatever was already propagating) purely by
    /// type, without needing an exact message match.</summary>
    internal sealed class SimulatedLoggingProviderFailureException(string message) : Exception(message);

    internal sealed class RecordingLogger<T> : ILogger<T>
    {
        // Round-29 finding: was a plain List<T> -- this class's own round-26/28/29 fault-observer
        // fixes all log from a ThreadPool continuation (TaskContinuationOptions.ExecuteSynchronously
        // just means "run inline on whichever thread completes the task," not "run on the test
        // thread"), concurrently with a test's own WaitForAsync polling loop enumerating this same
        // collection on the test thread -- a genuine, if rare, InvalidOperationException
        // ("Collection was modified") race, caught by this exact new test intermittently failing on a
        // full-suite run. ConcurrentBag is thread-safe for concurrent Add/enumerate/Clear.
        public ConcurrentBag<(LogLevel Level, string Message)> Entries { get; } = [];

        /// <summary>Test-only hook (round 19): when a formatted message contains this substring, `Log`
        /// throws instead of recording -- simulates a broken logging provider (e.g. a file logger on a
        /// full disk), the realistic source rounds 17-19 identified for "a Log.* call throwing" as an
        /// in-scope failure mode for this class's own cleanup/dispose guards.</summary>
        public string? ThrowOnMessageContaining { get; set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (ThrowOnMessageContaining is not null && message.Contains(ThrowOnMessageContaining, StringComparison.Ordinal))
            {
                // A dedicated, deliberately unusual exception TYPE (not just a distinct message) --
                // a test asserting on the type alone must not accidentally pass because production
                // code happens to throw the same common BCL exception type for an unrelated reason.
                throw new SimulatedLoggingProviderFailureException($"Simulated logging provider failure (test double) for message: {message}");
            }

            Entries.Add((logLevel, message));
        }
    }
}
