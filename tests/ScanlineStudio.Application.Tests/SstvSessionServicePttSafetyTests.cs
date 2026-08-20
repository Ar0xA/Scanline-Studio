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
            FakeAudioDeviceEnumerator? deviceEnumerator = null)
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
            inFlightKeyedTransmitWaitForTests: inFlightKeyedTransmitWait ?? TimeSpan.FromMilliseconds(500));

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
        var (service, _, radio, _) = CreateService();
        radio.BeforeSetPtt = tx =>
        {
            if (tx)
            {
                // BeforeSetPtt is a synchronous Action<bool> (it fires from inside SetPttAsync, before
                // any await completes), so there is no async alternative here. DisposeAsync never takes
                // _pttLockGate and has nothing gated to wait on in this test (no in-flight transmit, not
                // receiving), so this completes immediately rather than genuinely blocking.
#pragma warning disable xUnit1031
                service.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore xUnit1031
            }
        };

        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.SetPttLockAsync(true));

        // The engage succeeded against the backend (PttCalls got the "true"), DisposeAsync's backstop
        // ran and saw nothing yet (this call hadn't set _pttLocked yet), and the post-await recheck
        // must have un-keyed this call's own engage rather than leaving it physically keyed with
        // _pttLocked reporting true.
        Assert.Equal(PttOnThenOff, radio.PttCalls);
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
