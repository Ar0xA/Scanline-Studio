using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
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
            TimeSpan? playbackStallTimeout = null)
    {
        var inner = new FakeAudioEngine();
        var engine = wrapEngine?.Invoke(inner) ?? inner;
        deviceEnumerator ??= new FakeAudioDeviceEnumerator
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
        var radio = new FakeRadioSessionService();
        var logger = new RecordingLogger<SstvSessionService>();

        // The internal test constructor (see its own doc comment): these budgets must actually be
        // allowed to EXPIRE for these tests to distinguish fixed behavior from the bug, and the
        // production values (5s/5s/3s) would make this file take far too long to run.
        var service = new SstvSessionService(
            engine, deviceEnumerator, settingsStore, new FakeSstvDecoder(), new FakeSstvEncoder(),
            new MacroTextResolver(), new FakeWaterfallSource(), new FakeReceivedImageBuffer(), radio, logger,
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
        services.AddSingleton<ISettingsStore>(new FakeSettingsStore { Settings = new AppSettings() });
        services.AddSingleton<ISstvDecoder>(new FakeSstvDecoder());
        services.AddSingleton<ISstvEncoder>(new FakeSstvEncoder());
        services.AddSingleton<IMacroTextResolver, MacroTextResolver>();
        services.AddSingleton<IWaterfallSource>(new FakeWaterfallSource());
        services.AddSingleton<IReceivedImageBuffer>(new FakeReceivedImageBuffer());
        services.AddSingleton<IRadioSessionService>(new FakeRadioSessionService());
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
        Assert.False(service.IsPttLocked);

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
        var tune = service.TuneAsync(1750, TimeSpan.FromMilliseconds(1));
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
        // THE bug: DisposeAsync's shutdown backstop was gated on _pttLocked ONLY -- nothing tracked
        // "a PlayWithPttAsync call has PTT keyed RIGHT NOW." On window-close during TX, the UI's
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
        var tune = service.TuneAsync(1750, TimeSpan.FromSeconds(30), ct: cts.Token);
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
        var tune = service.TuneAsync(1750, TimeSpan.FromSeconds(30), ct: cts.Token);
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
    }

    [Fact]
    public async Task Blocker3_DisposeAsync_AfterLeaveKeyedTune_StillUnkeys()
    {
        // The third keyed-at-shutdown state _pttLocked never covered: TuneAsync(leaveKeyedAfterTune:
        // true) leaves PTT physically keyed WITHOUT setting _pttLocked -- so shutdown used to walk
        // away from a keyed rig.
        var (service, _, radio, _) = CreateService();

        await service.TuneAsync(1750, TimeSpan.FromMilliseconds(1), leaveKeyedAfterTune: true);
        Assert.Equal([true], radio.PttCalls);
        Assert.False(service.IsPttLocked);

        await service.DisposeAsync();

        Assert.Equal(PttOnThenOff, radio.PttCalls);
    }

    [Fact]
    public async Task Blocker3_DisposeAsync_PttOffPrecedesStopCapture()
    {
        // Found during this fix (same failure class, not in the original report): DisposeAsync ran
        // StopReceivingAsync FIRST, and that ends in MiniAudioCaptureSession.Dispose's own drain
        // thread join, which has NO timeout at all if a SamplesCaptured subscriber never returns --
        // so a wedged drain thread hung shutdown before the PTT backstop was ever reached. Un-key
        // must come first.
        List<string> order = [];
        var (service, _, radio, _) = CreateService(
            wrapEngine: inner => new RecordingOrderAudioEngine(inner, () => { }, () => order.Add("stop-capture")));
        radio.BeforeSetPtt = tx => order.Add(tx ? "ptt-on" : "ptt-off");

        await service.StartReceivingAsync();
        await service.SetPttLockAsync(true);
        order.Clear();

        await service.DisposeAsync();

        Assert.Equal(["ptt-off", "stop-capture"], order);
    }

    // ------------------------------------------------------------------ risk B

    [Fact]
    public async Task RiskB_UnlockRacingCleanup_DoesNotStrandRxStopped()
    {
        // Risk B, exercised deterministically at the ONE interleaving that matters: SetPttLockAsync(false)
        // completes AFTER PlayWithPttAsync's cleanup snapshotted _pttLocked==true but BEFORE the
        // cleanup published _rxPendingResumeAfterUnlock. The unlock's own deferred-resume check then
        // ran against a still-false flag, so nothing would ever consume the flag the cleanup was about
        // to set -- RX stopped forever. Driven from BeforeSetPtt on the un-key call so the unlock
        // lands inside the cleanup window.
        var (service, engine, radio, _) = CreateService();
        await service.StartReceivingAsync();
        await service.SetPttLockAsync(true);

        var unlocked = false;
        radio.BeforeSetPtt = tx =>
        {
            if (tx || unlocked)
            {
                return;
            }

            unlocked = true;
        };

        await service.TransmitAsync(TestMode, TestImage);

        // The unlock lands here, mimicking a user releasing the lock as the transmit's cleanup runs.
        await service.SetPttLockAsync(false);

        Assert.True(((FakeAudioEngine)engine).IsCapturing, "RX must never be stranded stopped by an unlock racing cleanup");
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

        var tune = service.TuneAsync(1750, TimeSpan.FromMilliseconds(1));

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
        // Round-3 finding: the fourth "keyed at shutdown" state DisposeAsync's pre-round-3 three-state
        // check missed -- a transmit whose OWN cleanup un-key was attempted and FAILED recorded nothing
        // before this fix (_pttLocked was already false by then, _pttLeftKeyedByCall is never set for
        // this path, and _keyedTransmitCompletion is unconditionally cleared by the transmit's own
        // finally regardless of whether the un-key succeeded) -- so DisposeAsync concluded "nothing to
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

    [Fact]
    public async Task Round3_SetPttLockAsync_Engage_PostDispose_ThrowsAndNeverKeys()
    {
        var (service, _, radio, _) = CreateService();
        await service.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.SetPttLockAsync(true));

        Assert.Empty(radio.PttCalls);
        Assert.False(service.IsPttLocked);
    }

    [Fact]
    public async Task Round3_SetPttLockAsync_Unlock_PostDispose_StillWorks()
    {
        // The deliberate escape hatch (see SetPttLockAsync's own doc comment): disposal must never
        // remove the one remaining way to un-key a rig this class already left keyed. Only the ENGAGE
        // direction is rejected post-disposal.
        var (service, _, radio, _) = CreateService();
        await service.DisposeAsync();

        await service.SetPttLockAsync(false);

        Assert.Equal([false], radio.PttCalls);
    }

    // ------------------------------------------------------------------ round 4 findings

    [Fact]
    public async Task Round4_SetPttLockAsync_DisposeRacesTheSetPttAsyncAwait_ThrowsAndUnkeysRatherThanLeavingLockedTrue()
    {
        // Round-4 finding: the round-3 test above (Engage_PostDispose_ThrowsAndNeverKeys) disposes
        // FIRST, so it only exercises the CHEAP pre-await guard -- it stays green even if the
        // post-await recheck this test targets is deleted entirely (round 4 caught this as a
        // mutation-INSENSITIVE test). This drives DisposeAsync from INSIDE the SetPttAsync(true)
        // round-trip itself -- the actual race the post-await recheck exists to close.
        //
        // Round-8 update: SetPttLockAsync now publishes _keyedTransmitCompletion/_keyedTransmitCount
        // BEFORE this key command too (round-8's own fix, mirroring PlayWithPttAsync's blocker-3
        // mechanism) -- so the nested DisposeAsync call below now genuinely sees "something in flight"
        // and bounded-waits (_inFlightKeyedTransmitWait, 500ms here) before giving up and running its
        // own backstop un-key regardless, EXACTLY the documented "whether the wait succeeds or times
        // out, DisposeAsync's own backstop un-key runs next either way, and a redundant un-key is
        // documented-harmless" contract AwaitInFlightKeyedTransmitAsync's own doc comment already
        // states for PlayWithPttAsync -- this reentrant nesting can never let the wait succeed (nothing
        // will complete until this synchronous callback itself returns), so it always times out. That
        // extra backstop attempt is why PttCalls now has THREE entries, not two: DisposeAsync's own
        // premature backstop un-key (issued before this call's OWN key command has even returned),
        // then the key succeeding, then this call's own post-await recovery un-key.
        var (service, _, radio, _) = CreateService();
        radio.BeforeSetPtt = tx =>
        {
            if (tx)
            {
                // BeforeSetPtt is a synchronous Action<bool> (it fires from inside SetPttAsync, before
                // any await completes), so there is no async alternative here. DisposeAsync never takes
                // _pttLockGate, so this doesn't deadlock -- it bounded-waits on the in-flight
                // registration (see above) and then proceeds regardless.
#pragma warning disable xUnit1031
                service.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore xUnit1031
            }
        };

        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.SetPttLockAsync(true));

        // DisposeAsync's own premature backstop un-key, then this call's own key succeeding, then this
        // call's own post-await recovery un-key -- three PTT commands, ending un-keyed either way.
        Assert.Equal([false, true, false], radio.PttCalls);
        Assert.False(service.IsPttLocked);
    }

    [Fact]
    public async Task Round4_StartReceivingAsync_PostDispose_Throws()
    {
        var (service, _, _, _) = CreateService();
        await service.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.StartReceivingAsync());
    }

    [Fact]
    public async Task Round4_SetPttLockAsync_SuccessfulUnlock_ClearsStaleUnkeyFailedFlag_NoSpuriousBackstop()
    {
        // Round-4 finding: a prior transmit's FAILED cleanup un-key sets _pttUnkeyFailedOnRealRig and
        // logs Critical -- but SetPttLockAsync's successful-unlock branch didn't clear it, so a rig
        // the operator just genuinely un-keyed via the lock escape hatch still triggered a spurious
        // backstop un-key attempt at DisposeAsync -- and would emit a false Critical if THAT attempt
        // happened to fail too, undermining the one signal this whole chunk exists to keep trustworthy.
        var failNextUnkey = true;
        var (service, _, radio, logger) = CreateService();
        radio.BeforeSetPtt = tx =>
        {
            if (!tx && failNextUnkey)
            {
                failNextUnkey = false;
                throw new TimeoutException("simulated slow backend");
            }
        };

        await service.TransmitAsync(TestMode, TestImage);
        Assert.Equal([true], radio.PttCalls);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Critical);

        // The operator genuinely un-keys via the lock escape hatch -- this must invalidate the stale
        // flag, not just _pttLocked/_pttLeftKeyedByCall.
        await service.SetPttLockAsync(true);
        await service.SetPttLockAsync(false);
        Assert.Equal([true, true, false], radio.PttCalls);

        logger.Entries.Clear();
        await service.DisposeAsync();

        // No spurious backstop un-key attempt, no further Warning/Critical.
        Assert.Equal([true, true, false], radio.PttCalls);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    // ------------------------------------------------------------------ round 5 findings

    [Fact]
    public async Task Round5_UnkeyForCleanupAsync_LostUpdateRace_DoesNotWipeAConcurrentNewerKey()
    {
        // Round-5 finding (full-file sweep, not just re-verifying round 4): UnkeyForCleanupAsync's
        // post-success clears (_pttLocked/_pttLeftKeyedByCall/_pttUnkeyFailedOnRealRig = false) ran
        // unconditionally on the continuation AFTER the un-key succeeded -- but a CONCURRENT, NEWER key
        // command (SetPttLockAsync(true) here) can complete in that same window and record its own
        // "still keyed" state, which the stale continuation would then wipe out from under it:
        // transmitter genuinely re-keyed, every shutdown-backstop flag reading false, DisposeAsync's
        // four-state check finding nothing to do. Deterministic trigger, same shape as
        // Blocker1_AbnormalTermination_...: ThrowOnStartPlaybackAudioEngine forces the tune's
        // abnormal-termination cleanup (and its urgent un-key) without any real-time race.
        var relock = false;
        var (service, _, radio, _) = CreateService(wrapEngine: inner => new ThrowOnStartPlaybackAudioEngine(inner));
        radio.BeforeSetPtt = tx =>
        {
            if (!tx && !relock)
            {
                relock = true;
                // BeforeSetPtt is a synchronous Action<bool> firing from inside the tune's own un-key
                // command -- there is no async alternative here. SetPttLockAsync never blocks on
                // anything this un-key call holds, so this completes immediately rather than genuinely
                // blocking, landing squarely inside the epoch-race window this test targets.
#pragma warning disable xUnit1031
                service.SetPttLockAsync(true).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TuneAsync(1750, TimeSpan.FromMilliseconds(1)));

        // THE property: the concurrent SetPttLockAsync(true) call's own "still keyed" state must
        // survive the stale un-key continuation from the tune's own abnormal-termination cleanup.
        Assert.True(service.IsPttLocked, "a concurrent newer key must not be wiped by a stale un-key continuation");
    }

    // ------------------------------------------------------------------ round 6 findings

    [Fact]
    public async Task Round6_SetPttLockAsync_Unlock_LostUpdateRace_DoesNotWipeAConcurrentNewerKey()
    {
        // Round-6 finding: SetPttLockAsync's OWN unlock-path clears (_pttLeftKeyedByCall/
        // _pttUnkeyFailedOnRealRig = false) had the identical lost-update shape round 5 fixed in
        // UnkeyForCleanupAsync -- round 5 fixed only that twin location and missed this one.
        // _pttLocked starts false here (never locked) -- SetPttLockAsync's own documented behavior is
        // to ALWAYS issue the underlying command regardless of current belief, so this unlock call is
        // meaningful even though nothing local believes the rig is locked yet.
        var relocked = false;
        var (service, _, radio, _) = CreateService();
        radio.BeforeSetPtt = tx =>
        {
            if (!tx && !relocked)
            {
                relocked = true;
                // A concurrent, totally independent TuneAsync(leaveKeyedAfterTune: true) lands
                // entirely within this unlock's own SetPttAsync(false) round-trip -- reads _pttLocked
                // as still false (this call hasn't written to it yet), keys the rig, and leaves it
                // keyed. Safe to run synchronously: TuneAsync never touches _pttLockGate.
#pragma warning disable xUnit1031
                service.TuneAsync(1750, TimeSpan.FromMilliseconds(1), leaveKeyedAfterTune: true).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
            }
        };

        await service.SetPttLockAsync(false);

        // THE property: the concurrent tune's own "left keyed" state must survive the stale unlock
        // continuation. _pttLeftKeyedByCall isn't public, so observed via DisposeAsync's own backstop
        // -- if the flag had been wiped, DisposeAsync would find nothing to do and issue no further
        // un-key at all.
        await service.DisposeAsync();
        Assert.Equal([true, false, false], radio.PttCalls);
    }

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
        // the overwrite. (The mechanism itself remains relevant for SetPttLockAsync, which is not
        // covered by the single-flight guard -- see the round-8/round-12 SetPttLockAsync tests.)
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
                var ex = Assert.ThrowsAsync<InvalidOperationException>(() => service.TuneAsync(1750, TimeSpan.FromMilliseconds(1))).GetAwaiter().GetResult();
                Assert.Equal("A transmit or tune is already in progress.", ex.Message);

                service.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore xUnit1031
            }
        };

        stage = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TuneAsync(1750, TimeSpan.FromMilliseconds(1)));

        // THE property: the newer call never keyed or un-keyed anything (rejected before touching PTT
        // at all) -- 1 key (older) + 2 un-key attempts (DisposeAsync's backstop, fired while reentrant
        // and unable to observe the older call's own in-flight completion, then the older call's own
        // recovery) = 3 total PTT commands, not 5.
        Assert.Equal(3, radio.PttCalls.Count);
        Assert.Equal(1, radio.PttCalls.Count(c => c));
        Assert.Equal(2, radio.PttCalls.Count(c => !c));
    }

    // ------------------------------------------------------------------ round 7 findings

    [Fact]
    public async Task Round7_SetPttLockAsync_KeyCommandThrows_StillRecordsPossiblyKeyedForBackstop()
    {
        // Round-7 finding: SetPttAsync(true) throwing does not mean the rig wasn't physically keyed --
        // e.g. RigctldClientProtocol writes the PTT command, then the READ of its reply times out or
        // the connection drops, with the rig already keyed. Before this fix, SetPttLockAsync recorded
        // NOTHING on that failure -- _pttLocked never gets set (the throw happens before that write),
        // so DisposeAsync's four-state check found nothing to do and the process could exit with the
        // transmitter genuinely keyed, silently.
        //
        // Round-12 update: the catch now ALSO attempts an immediate GUARDED recovery un-key (see its
        // own comment) whenever _keyedTransmitCount == 1 -- true here, since this is the only call
        // with a registration -- so the un-key now happens immediately, not just eventually via
        // DisposeAsync's backstop.
        var (service, _, radio, logger) = CreateService();
        radio.BeforeSetPtt = tx =>
        {
            if (tx)
            {
                throw new TimeoutException("simulated: PTT command written, reply read timed out");
            }
        };

        await Assert.ThrowsAsync<TimeoutException>(() => service.SetPttLockAsync(true));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("MAY HAVE BEEN KEYED", StringComparison.Ordinal));

        // THE property: the guarded recovery already un-keyed the rig, immediately, inside the catch.
        Assert.Equal([false], radio.PttCalls);

        // DisposeAsync's backstop must find nothing left to do -- the recovery already handled it, so
        // no second un-key attempt.
        logger.Entries.Clear();
        radio.BeforeSetPtt = null;
        await service.DisposeAsync();

        Assert.Equal([false], radio.PttCalls);
    }

    [Fact]
    public async Task Round7_PlayWithPttAsync_LeaveKeyedWrite_NeverWritesFalseWhenThisCallDidNotKey()
    {
        // Round-7 finding: the _pttLeftKeyedByCall write in PlayWithPttAsync's leaveKeyedAfterCall
        // branch used to write pttKeyedOnRealRig UNCONDITIONALLY, including `false` when THIS call's
        // own key was skipped (no radio configured at its own key time) -- the same lost-update shape
        // rounds 5/6 already closed at two other sites, at a third location. A prior call may have
        // genuinely left the rig keyed; a later call with no radio configured must not silently
        // un-declare that.
        var (service, _, radio, _) = CreateService();

        // Establish a genuinely-keyed "left keyed" state from a real prior call.
        await service.TuneAsync(1750, TimeSpan.FromMilliseconds(1), leaveKeyedAfterTune: true);
        Assert.Equal([true], radio.PttCalls);

        // A SEPARATE call runs with no radio configured -- its own key is skipped, so it has nothing
        // of its own to report, and must not clear the flag the prior call set.
        radio.RigId = "none";
        await service.TuneAsync(1750, TimeSpan.FromMilliseconds(1), leaveKeyedAfterTune: true);

        // Restore before dispose so the backstop's own un-key can land cleanly (isolating THIS
        // assertion from blocker 2's own already-covered RigId-flip scenario).
        radio.RigId = "fake-radio";
        await service.DisposeAsync();

        // THE property: DisposeAsync must still see the earlier call's genuinely-keyed state and
        // issue a real backstop un-key.
        Assert.Equal([true, false], radio.PttCalls);
    }

    // ------------------------------------------------------------------ round 8 findings

    [Fact]
    public async Task Round8_SetPttLockAsync_KeyCommandThrows_EpochBump_SurvivesConcurrentUnkeyersStaleClear()
    {
        // Round-8 finding: round 7's own catch-block write (_pttLeftKeyedByCall = true, on a key
        // command that threw after possibly keying the rig) was ITSELF the lost-update shape rounds
        // 5-7 already closed at three other sites -- it never bumped _pttKeyEpoch, so a CONCURRENT
        // UnkeyForCleanupAsync call whose own epoch snapshot predates this write could still wipe it
        // moments later, believing nothing new happened.
        var stage = 0;
        var (service, _, radio, _) = CreateService(wrapEngine: inner => new ThrowOnStartPlaybackAudioEngine(inner));
        radio.BeforeSetPtt = tx =>
        {
            if (!tx && stage == 1)
            {
                stage = 2;
                // A concurrent SetPttLockAsync(true) call, whose OWN key command throws after possibly
                // keying the rig, lands entirely within THIS un-key's own SetPttAsync(false)
                // round-trip -- reassigning the hook first so the nested call's own key attempt throws
                // deterministically, without recursing back into this branch.
                radio.BeforeSetPtt = innerTx =>
                {
                    if (innerTx)
                    {
                        throw new TimeoutException("simulated: PTT command written, reply read timed out");
                    }
                };
#pragma warning disable xUnit1031
                Assert.ThrowsAsync<TimeoutException>(() => service.SetPttLockAsync(true)).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
            }
        };

        stage = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TuneAsync(1750, TimeSpan.FromMilliseconds(1)));

        // THE property: DisposeAsync must still see the concurrent call's "may have keyed" state and
        // issue a real backstop un-key.
        await service.DisposeAsync();
        Assert.Equal([true, false, false], radio.PttCalls);
    }

    [Fact]
    public async Task Round8_DisposeAsync_WaitsForAnInFlightSetPttLockAsyncsOwnCompletion()
    {
        // Round-8 finding: SetPttLockAsync's own key command was invisible to DisposeAsync's bounded
        // shutdown WAIT -- unlike PlayWithPttAsync (blocker 3), it never published
        // _keyedTransmitCompletion/_keyedTransmitCount before the key command, so DisposeAsync could
        // run straight through, disposing IRadioSessionService, before this call's own post-await
        // disposal-race recovery ever got a chance to run -- against a radio session that no longer
        // exists. Uses FakeRadioSessionService's new Gate (a genuine async park, unlike the
        // synchronous BeforeSetPtt hook other tests use) so the in-flight window is real, not nested.
        var gate = new TaskCompletionSource();
        var (service, _, radio, _) = CreateService(inFlightKeyedTransmitWait: TimeSpan.FromSeconds(30));
        radio.Gate = gate.Task;

        var setLock = service.SetPttLockAsync(true);
        Assert.False(setLock.IsCompleted, "the key command should still be parked at the gate");

        var dispose = service.DisposeAsync().AsTask();

        // Round-9 nit: no Task.Delay here -- a wall-clock wait was a race that could false-pass on a
        // loaded runner (the earlier version of this test only distinguished fixed-vs-broken by
        // timing). This check needs none: in THIS exact scenario, nothing DisposeAsync does after
        // AwaitInFlightKeyedTransmitAsync ever awaits an incomplete Task (StopReceivingAsync
        // early-returns, not receiving; the fake waterfall/decoder dispose synchronously) -- so
        // WITHOUT the fix, AwaitInFlightKeyedTransmitAsync itself sees nothing published, returns
        // synchronously, and the entire DisposeAsync call completes synchronously, making `dispose`
        // already IsCompleted==true the instant this line runs -- deterministically, not a race. WITH
        // the fix, Task.WhenAny(pending.Task, Task.Delay(...)) genuinely has nothing complete yet, so
        // the async state machine must actually suspend and `dispose` reads IsCompleted==false here,
        // just as deterministically.
        Assert.False(dispose.IsCompleted, "DisposeAsync returned while an in-flight SetPttLockAsync call still had a key command outstanding");

        gate.SetResult();

        // The key command, released from the gate, now genuinely completes -- but _disposed is
        // already true by this point (DisposeAsync sets it as its own very first line), so
        // SetPttLockAsync's own post-await disposal-race recovery (round-3/4's fix) fires: it un-keys
        // what it just keyed, then throws ObjectDisposedException. This is round 8's whole point --
        // DisposeAsync's wait let this call's own recovery run BEFORE DisposeAsync itself tore
        // anything down, rather than racing past it blind.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => setLock);
        await dispose;

        // Key, then this call's own recovery un-key -- DisposeAsync's own backstop must find nothing
        // left to do (no third entry) since the recovery already handled it.
        Assert.Equal([true, false], radio.PttCalls);
        Assert.False(service.IsPttLocked);
    }

    // ------------------------------------------------------------------ round 10 findings

    [Fact]
    public async Task Round10_SetPttLockAsync_Unlock_RxResumeBounded_DoesNotStrandTheLockGate()
    {
        // Round-10 finding: the RX resume after an unlock was bounded on the CALLER's ct, not a
        // fresh CTS -- unlike PlayWithPttAsync's own equivalent, which always uses a fresh
        // rxResumeCts. A wedged capture device (device enumeration, settings I/O, or
        // _audioEngine.StartCaptureAsync itself hanging) could park this call inside _pttLockGate
        // indefinitely -- stranding the PTT lock/unlock escape hatch itself: every other
        // SetPttLockAsync call, including a future emergency unlock, blocks on the SAME gate. Not
        // the leaked-keyed-transmitter class (the rig is already confirmed un-keyed by the time this
        // runs), but a real availability bug in the one API this whole method exists to keep working.
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [8000])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [11025])],
        };
        var (service, _, radio, logger) = CreateService(cleanupTimeout: TimeSpan.FromMilliseconds(200), deviceEnumerator: deviceEnumerator);

        await service.StartReceivingAsync();
        await service.SetPttLockAsync(true);
        await service.TransmitAsync(TestMode, TestImage);
        // The lock was engaged during the transmit, so its cleanup deferred the RX resume instead of
        // running it -- _rxPendingResumeAfterUnlock is now true, and the unlock below triggers it.

        // Wedge device enumeration permanently (never released) for the deferred resume the unlock
        // is about to trigger.
        deviceEnumerator.Gate = new TaskCompletionSource().Task;

        // Round-11 nit: wrapped in WaitAsync(TimeSpan), not just timed with a Stopwatch -- reverting
        // the fix parks this call forever (the caller's own ct is CancellationToken.None), so an
        // un-guarded await would hang the whole test run until the runner's own global timeout
        // instead of failing this one test loudly and immediately.
        var stopwatch = Stopwatch.StartNew();
        await service.SetPttLockAsync(false).WaitAsync(TimeSpan.FromSeconds(5));
        stopwatch.Stop();

        // Round-12 nit: tightened to 1s (the configured budget is 200ms) -- the WaitAsync(5s) above
        // already guarantees this can't exceed 5s, so a 5s bound here would be dead weight with no
        // independent diagnostic value of its own.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"unlock must not hang on a wedged RX resume; took {stopwatch.Elapsed}");
        Assert.Contains(logger.Entries, e => e.Message.Contains("Resume RX (after unlock)", StringComparison.Ordinal));

        // THE property: _pttLockGate must have been released promptly -- a SUBSEQUENT lock call must
        // not be stuck behind the stranded resume. Same WaitAsync guard, same reasoning.
        var lockStopwatch = Stopwatch.StartNew();
        await service.SetPttLockAsync(true).WaitAsync(TimeSpan.FromSeconds(5));
        lockStopwatch.Stop();
        Assert.True(lockStopwatch.Elapsed < TimeSpan.FromSeconds(1), $"the lock gate must not still be held by the earlier stranded resume; took {lockStopwatch.Elapsed}");
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

        await service.TuneAsync(1750, TimeSpan.FromMilliseconds(1));

        // THE property: only ONE key + ONE un-key -- the rejected second call never touched PTT.
        Assert.Equal([true, false], radio.PttCalls);
    }

    [Fact]
    public async Task Round12_SetPttLockAsync_KeyCommandThrows_RecoverySkippedWhenAnotherTransmitIsRegistered()
    {
        // Round-12 finding: the guarded recovery un-key (added this round) only fires when
        // _keyedTransmitCount == 1 -- if a genuine, concurrent PlayWithPttAsync transmission is ALSO
        // registered at that exact moment, the recovery must be skipped rather than un-keying
        // underneath that other call. This is the residual the guard is specifically designed to
        // preserve -- see the catch block's own comment.
        var stage = 0;
        var (service, _, radio, logger) = CreateService();
        radio.BeforeSetPtt = tx =>
        {
            if (tx && stage == 1)
            {
                stage = 2;
                // A concurrent SetPttLockAsync(true) call, whose OWN key command throws, lands while
                // the outer TuneAsync call (below) is still mid-key -- its own registration still
                // counted, since PttCalls.Add for it hasn't even run yet at this point.
                radio.BeforeSetPtt = innerTx =>
                {
                    if (innerTx)
                    {
                        throw new TimeoutException("simulated key failure");
                    }
                };
#pragma warning disable xUnit1031
                Assert.ThrowsAsync<TimeoutException>(() => service.SetPttLockAsync(true)).GetAwaiter().GetResult();
#pragma warning restore xUnit1031

                // THE property, checked HERE while both registrations still coexist: the concurrent
                // key failure must NOT have attempted a recovery un-key.
                Assert.Empty(radio.PttCalls);
                Assert.Contains(logger.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("MAY HAVE BEEN KEYED", StringComparison.Ordinal));
            }
        };

        stage = 1;
        await service.TuneAsync(1750, TimeSpan.FromMilliseconds(1));
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
    internal sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
