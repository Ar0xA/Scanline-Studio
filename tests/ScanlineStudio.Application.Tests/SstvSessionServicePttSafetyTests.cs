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
    public async Task SetPttLockAsync_UnlockAfterLockedTransmitCompletes_ResumesRx()
    {
        // T1-5 plan-review (round 2/3) correction: this test's own name/comment used to claim it
        // exercised Risk B's actual race window (SetPttLockAsync(false) landing DURING PlayWithPttAsync's
        // cleanup, between its pttLockedAtCleanup snapshot and its own publish of the deferred-resume
        // flag). It does not -- the BeforeSetPtt hook below only fires during an actual PTT command
        // send, and Risk B's own race window has no command send inside it at all (pure, synchronous
        // state-flag logic -- confirmed by direct re-read of PlayWithPttAsync's own cleanup body). The
        // two calls below run fully SEQUENTIALLY (TransmitAsync's own await completes in full before
        // SetPttLockAsync(false) is ever invoked), not concurrently. This is still a real, valid,
        // worth-keeping test of a DIFFERENT thing: a locked transmit correctly defers its own RX-resume,
        // and a LATER unlock correctly consumes and resumes it. See
        // PttSafetyCoordinatorTests.cs's own DecideWasReceivingResume_ConcurrentUnlockAlreadyLanded_*
        // and its RacingTryConsumeRxResumePending_* stress test for Risk B's actual coverage now that
        // the decision logic is directly, deterministically testable.
        var (service, engine, _, _) = CreateService();
        await service.StartReceivingAsync();
        await service.SetPttLockAsync(true);

        await service.TransmitAsync(TestMode, TestImage);
        await service.SetPttLockAsync(false);

        Assert.True(((FakeAudioEngine)engine).IsCapturing, "RX must resume once the operator unlocks after a locked transmit completes");
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
    public async Task TierBAuditFinding_SetPttLockAsync_NoRadioConfigured_NeverRecordsPossiblyKeyed()
    {
        // Tier B audit finding (companion to Round7's own test above, opposite conclusion): with
        // RigId == "none", the production chain (RadioSessionService -> RadioController ->
        // NoneRadioProtocol) throws SYNCHRONOUSLY, before pttCommand is ever assigned -- nothing
        // was ever dispatched to a real backend, so nothing could have been physically keyed. The
        // catch's own state-latching write used to be unconditional on `locked` alone, so this
        // benign no-radio case used to ALSO record "possibly keyed," permanently violating
        // _pttLeftKeyedByCall's own documented "never fires for RigId=='none'" invariant --
        // DisposeAsync's backstop would then fire a false Critical on a machine with no radio at
        // all. ThrowSynchronouslyOnNoneRig reproduces the real production shape (a plain async
        // fault, the fake's own default, does not distinguish "dispatched then failed" from
        // "never dispatched" the way the real chain does).
        var (service, _, radio, logger) = CreateService();
        radio.RigId = "none";
        radio.ThrowSynchronouslyOnNoneRig = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetPttLockAsync(true));

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Critical);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("MAY HAVE BEEN KEYED", StringComparison.Ordinal));

        // DisposeAsync's backstop must find nothing to do -- no un-key attempt, no Critical.
        logger.Entries.Clear();
        await service.DisposeAsync();

        Assert.Empty(radio.PttCalls);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Critical);
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

    // ------------------------------------------------------------------ round 15 findings

    [Fact]
    public async Task Round15_PlayWithPttAsync_KeyCommandThrows_EpochBump_SurvivesConcurrentUnkeyersStaleClear()
    {
        // Round-15 finding 1: PlayWithPttAsync's OWN key-command catch never bumped _pttKeyEpoch --
        // the fifth site of the lost-update shape rounds 5-8 already closed everywhere else (see
        // SetPttLockAsync's own twin, round-8 finding, whose comment explains the reasoning in full).
        // Round 14's WaitAsync fix made this newly reachable in practice (a hung key command now
        // reliably throws instead of hanging forever).
        var relocked = false;
        var (service, _, radio, _) = CreateService();
        radio.BeforeSetPtt = tx =>
        {
            if (!tx && !relocked)
            {
                relocked = true;
                // A concurrent TuneAsync lands entirely within this unlock's own SetPttAsync(false)
                // round-trip: its OWN key command throws (simulating a wedged/failing key attempt),
                // and its own cleanup un-key ALSO throws -- reassigning the hook to throw
                // unconditionally covers both, deterministically, without recursing back into this
                // branch (the reassignment only affects calls AFTER this point; this call's own
                // BeforeSetPtt invocation has already fired and won't run again).
                radio.BeforeSetPtt = _ => throw new TimeoutException("simulated: PTT command written, reply read timed out");
#pragma warning disable xUnit1031
                Assert.ThrowsAsync<TimeoutException>(() => service.TuneAsync(1750, TimeSpan.FromMilliseconds(1))).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
            }
        };

        // This call's own key command succeeds (RigId check passes, no throw for tx=false here --
        // the reassigned hook only applies to LATER calls) despite the concurrent chaos inside it.
        await service.SetPttLockAsync(false);

        // THE property: the concurrent tune's own "may still be keyed" state (_pttUnkeyFailedOnRealRig,
        // set when ITS OWN cleanup un-key also failed) must survive this unlock's stale-epoch clear --
        // DisposeAsync's backstop must still see it and issue a real un-key. Without the epoch bump,
        // this unlock's own epoch check would wrongly match (nothing bumped it) and wipe that state,
        // silently reporting a rig that may genuinely still be keyed as confirmed off.
        radio.BeforeSetPtt = null;
        await service.DisposeAsync();
        Assert.Equal([false, false], radio.PttCalls);
    }

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

        await Assert.ThrowsAsync<TimeoutException>(() => service.TuneAsync(1750, TimeSpan.FromMilliseconds(1)));

        // THE property: PTT was never KEYED (the hang is entirely pre-key, so no `true` call) -- the
        // single `false` is the cleanup's own deliberately-unconditional un-key attempt (this class's
        // established safety-net behavior, harmless here), and the failure is still logged.
        Assert.Equal([false], radio.PttCalls);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Playback failed", StringComparison.OrdinalIgnoreCase));

        // _transmitInFlight must have been released too -- a subsequent, non-hanging tune must
        // succeed cleanly rather than being rejected as "already in progress."
        settingsStore.Gate = null;
        await service.TuneAsync(1750, TimeSpan.FromMilliseconds(1));
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
        Assert.Equal(100, await service.GetTxVolumePercentAsync());

        // Write-side clamp -- an out-of-range value passed to the setter must not reach disk
        // unclamped either, otherwise every future read would keep needing the read-side clamp to
        // mask what's actually stored. Inspects the STORED value directly via settingsStore, not
        // through GetTxVolumePercentAsync -- reading it back through the getter would pass even with
        // the write-side clamp mutated away, since the read-side clamp masks an unclamped write too.
        await service.SetTxVolumePercentAsync(-20);
        var stored = settingsStore.Settings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings);
        Assert.Equal(0, stored?.TxVolumePercent);
    }

    // ------------------------------------------------------------------ round 17 findings

    [Fact]
    public async Task Round17_SetPttLockAsync_KeyCommandHangs_TimesOutRatherThanStrandingTheLockGateForever()
    {
        // Round-17 finding 1: SetPttLockAsync's own PTT command had no bound of its own -- the
        // identical gap PlayWithPttAsync's own key command had before round 14's fix, never given the
        // same treatment here. Verified against both shipped backends: HamlibRadioProtocol.SetPttAsync's
        // CallAsync wrapper takes no CancellationToken at all; RigctldClientProtocol's reply read has
        // no timeout of its own either. A wedged rig here previously hung this call forever WHILE
        // HOLDING _pttLockGate -- stranding every future SetPttLockAsync call, including a future
        // emergency unlock.
        var (service, _, radio, logger) = CreateService(cleanupTimeout: TimeSpan.FromMilliseconds(50));
        radio.HangOnCallNumber = 1;

        await Assert.ThrowsAsync<TimeoutException>(() => service.SetPttLockAsync(true));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Critical);

        // THE property: _pttLockGate must have been released -- a subsequent call must not be stuck
        // behind the hung one. Round-11 precedent: wrapped in WaitAsync(TimeSpan) so a bug here fails
        // this one test loudly instead of hanging the whole run.
        radio.HangOnCallNumber = null;
        await service.SetPttLockAsync(true).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Round17_TryUnkeyPttAsync_UnkeyCommandHangs_TimesOutRatherThanHangingForever()
    {
        // Round-17 finding 2 (the single most safety-critical await in the file): TryUnkeyPttAsync's
        // own un-key command had no REAL bound -- UnkeyForCleanupAsync's fresh unkeyCts.Token was
        // passed as the callee's own `ct` parameter, which round 16 already proved does not bound
        // either shipped backend's native/protocol call. This is the exact same mistake, one call
        // away from the whole reason this file exists: PlayWithPttAsync's finally, SetPttLockAsync's
        // recovery, and (worst of all) DisposeAsync's own backstop all funnel through this one method.
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

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.TuneAsync(double.NaN, TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.TuneAsync(-100, TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.TuneAsync(30_000, TimeSpan.FromSeconds(1))); // >= Nyquist at the hardcoded 48kHz
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.TuneAsync(1750, TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.TuneAsync(1750, TimeSpan.FromMinutes(10)));

        Assert.Empty(radio.PttCalls);

        // A valid call still works normally.
        await service.TuneAsync(1750, TimeSpan.FromMilliseconds(1));
        Assert.Equal([true, false], radio.PttCalls);
    }

    [Fact]
    public async Task Round18_SetPttLockAsync_UnlockCommandFails_LogsCriticalRatherThanSilently()
    {
        // Round-18 finding 5: the unlock direction's own SetPttAsync failure had no catch arm at all
        // -- it propagated with no log whatsoever, not even a Warning, for a failed EMERGENCY unlock
        // on a genuinely keyed rig.
        var (service, _, radio, logger) = CreateService();
        await service.SetPttLockAsync(true);

        radio.BeforeSetPtt = tx =>
        {
            if (!tx)
            {
                throw new TimeoutException("simulated: unlock command failed");
            }
        };

        await Assert.ThrowsAsync<TimeoutException>(() => service.SetPttLockAsync(false));

        // THE property: the operator is told immediately, not just whenever DisposeAsync eventually
        // runs.
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("MAY STILL BE KEYED", StringComparison.Ordinal));
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

    [Fact]
    public async Task Round19_SetPttLockAsync_UnlockCommand_CallerCancellationDoesNotReachTheCommandItself()
    {
        // Round-19 finding 2: the identical gap round 18 closed in TryUnkeyPttAsync survived here --
        // `ct` reached the UNLOCK command itself, not just the wait, so a caller cancelling `ct`
        // while this call was queued behind a wedged prior command would abort the un-key AT THE
        // BACKEND'S REQUEST GATE, never reaching the rig -- the emergency-unlock escape hatch this
        // method's own doc comment says exists precisely for "when something already went wrong."
        //
        // GateOnCallNumber:2 parks only the unlock command (call #2 -- call #1 is the initial engage)
        // -- note this happens AFTER _pttLockGate.WaitAsync(ct) at this method's very own entry, which
        // an ALREADY-cancelled token would reject before ever reaching this round's own fix at all,
        // so `cts` must start un-cancelled and only cancel WHILE genuinely parked, not before.
        // CancelAfter (not an immediate Cancel) guarantees genuine mid-flight cancellation without
        // needing to detect "now parked" directly -- the gate never resolves on its own, so the call
        // cannot possibly finish before the timer fires.
        var gateTcs = new TaskCompletionSource();
        var (service, _, radio, _) = CreateService();
        await service.SetPttLockAsync(true);
        radio.Gate = gateTcs.Task;
        radio.GateOnCallNumber = 2;

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        // The outer call itself throws EITHER WAY here -- the WAIT (WaitAsync(_cleanupTimeout, ct))
        // correctly stays cancellable by the caller's own `ct` in both directions, that part is
        // unchanged by this round's fix. The property under test is whether the underlying COMMAND,
        // left running in the background once the wait itself gives up, still reaches the rig --
        // observable only via PttCalls, not via the outer call's own (identical either way) exception.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SetPttLockAsync(false, cts.Token));

        // The command is still parked on the gate at this point -- not recorded, not cancelled (the
        // fake's OWN inner Gate.WaitAsync uses the command's own ct parameter, which this round's fix
        // set to CancellationToken.None for the unlock direction -- cts cancelling has no effect on it).
        Assert.Equal([true], radio.PttCalls);

        // THE property: releasing the gate now lets the abandoned command actually complete and
        // reach the rig. Without this round's fix, `ct` (cts.Token) would have reached the fake's own
        // inner Gate.WaitAsync too, so the command would already have been cancelled the moment
        // CancelAfter fired -- gateTcs.SetResult() here would then complete a task nothing is
        // meaningfully waiting on anymore, and PttCalls would never gain a second entry.
        gateTcs.SetResult();
        await WaitForAsync(() => radio.PttCalls.Count == 2, TimeSpan.FromSeconds(5));
        Assert.Equal([true, false], radio.PttCalls);
    }

    [Fact]
    public async Task Round19_SetPttLockAsync_UnlockFailsOnNeverKeyedRig_DoesNotLatchAFalseCriticalAlarm()
    {
        // Round-19 finding 5: the round-18 catch arm for the unlock direction latched
        // _pttUnkeyFailedOnRealRig (and logged Critical) unconditionally on failure -- but
        // SetPttLockAsync is documented as always issuing the command regardless of current belief,
        // so an operator can legitimately call SetPttLockAsync(false) on a rig this class never
        // believed was keyed at all. Without this round's gate, that produces a FALSE Critical alarm
        // and a permanently-latched flag for a rig that was demonstrably never keyed.
        var (service, _, radio, logger) = CreateService();
        // Deliberately no prior SetPttLockAsync(true) -- _pttLocked/_pttLeftKeyedByCall/
        // _pttUnkeyFailedOnRealRig are all still at their default false.
        radio.BeforeSetPtt = tx =>
        {
            if (!tx)
            {
                throw new TimeoutException("simulated: unlock command failed on a never-keyed rig");
            }
        };

        await Assert.ThrowsAsync<TimeoutException>(() => service.SetPttLockAsync(false));

        // THE property: no false alarm.
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Critical);

        // And DisposeAsync's own backstop must find nothing to do either -- no spurious un-key
        // attempt against a rig that was never believed keyed.
        radio.BeforeSetPtt = null;
        await service.DisposeAsync();
        Assert.Empty(radio.PttCalls);
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

    // Round-20 finding 2 (SetPttLockAsync's two now-guarded UnkeyForCleanupAsync call sites): NOT
    // given a dedicated test. Its own reachability mechanism was "UnkeyForCleanupAsync throws" -- the
    // SAME mechanism finding 1's fix (this round) closes by making every log call inside
    // TryUnkeyPttAsync/UnkeyForCleanupAsync non-throwing via SafeLog. After that fix, no currently-
    // known way exists to make UnkeyForCleanupAsync actually throw, so a test built on the same
    // trigger (an already-tried `ThrowOnMessageContaining` targeting the wrapper's own log message)
    // would be vacuous -- the wrapper's own catch is simply never entered. The fix itself remains
    // genuinely valuable as defense-in-depth (protects this method's exception classification if a
    // FUTURE change reintroduces a throwing path inside UnkeyForCleanupAsync), just not independently
    // exercisable with current test infrastructure. Noted here rather than shipped with a test that
    // would silently prove nothing.

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
        var tune = service.TuneAsync(1750, TimeSpan.FromSeconds(30), ct: cts.Token);
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

    // ------------------------------------------------------------------ round 24 findings

    [Fact]
    public async Task Round24_SetPttLockAsync_UnlockFailsAfterCatLinkDrops_StillLatchesCriticalImmediately()
    {
        // Round-24 finding (risk): the unlock-direction catch filter used to re-read RigId at failure
        // time (rigIsRealAtUnlockTime) -- the exact blocker-2 anti-pattern this file's own established
        // rule forbids elsewhere (never re-read RigId at catch/failure time). If the CAT link drops
        // between key and unlock (RadioController.DisconnectAsync sets RigId to "none" WITHOUT
        // un-keying, per its own doc comment), the rig is still genuinely keyed but the old filter
        // read "none" and never matched AT ALL -- not even the inner belief-based gate ever ran, so
        // the emergency unlock's own failure produced NO log whatsoever until DisposeAsync's shutdown
        // backstop, possibly hours later.
        var (service, _, radio, logger) = CreateService();

        // Lock successfully while the rig is real.
        await service.SetPttLockAsync(true);
        Assert.True(service.IsPttLocked);

        // Simulate a CAT link drop -- RigId flips to "none" WITHOUT un-keying -- then the emergency
        // unlock itself fails.
        radio.RigId = "none";
        radio.BeforeSetPtt = tx =>
        {
            if (!tx)
            {
                throw new TimeoutException("simulated: emergency unlock failed after CAT link drop");
            }
        };

        await Assert.ThrowsAsync<TimeoutException>(() => service.SetPttLockAsync(false));

        // THE property: the failure must be loudly logged IMMEDIATELY, not silently swallowed until
        // shutdown.
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("MAY STILL BE KEYED", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ round 25 findings

    [Fact]
    public async Task Round25_PlayWithPttAsync_LockEngagedThenCatLinkDrops_AbnormalTerminationStillLatchesCritical()
    {
        // Round-25 finding (risk): pttKeyedOnRealRig stayed false whenever RigId == "none" AT THIS
        // CALL'S OWN key time -- even when a PTT lock was already engaged on a real rig before the CAT
        // link dropped, meaning the rig is genuinely keyed independent of this call's own observation.
        // Silently disabled the cleanup Critical log AND the round-18 retry for a call that entered on
        // a rig kept keyed by an EARLIER SetPttLockAsync call.
        var (service, _, radio, logger) = CreateService(wrapEngine: inner => new ThrowOnStartPlaybackAudioEngine(inner));

        // Lock successfully on a real rig.
        await service.SetPttLockAsync(true);

        // CAT link drops -- RigId flips to "none" WITHOUT un-keying -- then the cleanup un-key itself
        // fails.
        radio.RigId = "none";
        radio.BeforeSetPtt = tx =>
        {
            if (!tx)
            {
                throw new TimeoutException("simulated: cleanup un-key failed after CAT link drop");
            }
        };

        // A transmit attempt aborts abnormally (StartPlaybackAsync throws).
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransmitAsync(TestMode, TestImage));

        // THE property: the failure must be loudly logged as a genuinely keyed rig, not silently
        // swallowed as the benign no-radio case.
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("MAY STILL BE KEYED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Round25_PlayWithPttAsync_LockEngagedDuringStopPlaybackDrain_CleanupDoesNotUnkeyIt()
    {
        // Round-25 finding (risk): pttLockedAtCleanup used to be snapshotted at the very TOP of
        // PlayWithPttAsync's finally, before StopPlaybackWithWatchdogAsync's own drain wait -- a
        // SetPttLockAsync(true) completing DURING that drain (engaging a genuine operator lock seconds
        // after this transmit's own body finished) was invisible to the stale snapshot, so this
        // transmit's own normal-completion cleanup silently un-keyed the just-engaged lock.
        // UnkeyForCleanupAsync's own epoch guard cannot catch this either -- it protects against a
        // newer key completing WHILE the un-key call itself is in flight, not one that already
        // completed before the un-key call was even entered.
        var stopPlaybackGate = new TaskCompletionSource();
        var reachedStopPlayback = new TaskCompletionSource();
        var (service, _, radio, _) = CreateService(
            wrapEngine: inner => new SignalingGatedStopPlaybackAudioEngine(inner, reachedStopPlayback, stopPlaybackGate.Task),
            playbackStopWaitBudget: TimeSpan.FromSeconds(30));

        var transmit = service.TransmitAsync(TestMode, TestImage);

        // Deterministic happens-before point: the transmit's own cleanup has genuinely entered
        // StopPlaybackWithWatchdogAsync's own StopPlaybackAsync call (past the un-fixed snapshot's own
        // read point in program order, since that one runs before StopPlaybackWithWatchdogAsync is even
        // called) and is now blocked on the gate below, BEFORE the fixed snapshot's own read point
        // (which runs only once StopPlaybackWithWatchdogAsync itself returns).
        await reachedStopPlayback.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Engage a lock WHILE the transmit's own cleanup is still draining playback.
        var setLock = service.SetPttLockAsync(true);
        await WaitForAsync(() => service.IsPttLocked, TimeSpan.FromSeconds(5));

        // Release the drain -- the transmit's own cleanup now proceeds to its un-key decision.
        stopPlaybackGate.SetResult();
        await transmit;
        await setLock;

        // THE property: the just-engaged lock must survive this transmit's own normal-completion
        // cleanup -- no un-key call for it, and IsPttLocked must still read true.
        Assert.True(service.IsPttLocked, "a PTT lock engaged during StopPlayback's drain was silently defeated by the transmit's own cleanup");
        Assert.Equal([true, true], radio.PttCalls);

        await service.DisposeAsync();
    }

    // ------------------------------------------------------------------ round 26 findings

    [Fact]
    public async Task Round26_TuneAsync_LeaveKeyedAfterTune_ConfirmedUnkeyDuringDrain_DisposeDoesNotDoubleUnkey()
    {
        // Round-26 finding (risk): _pttLeftKeyedByCall = true used to be written unconditionally once
        // a leaveKeyedAfterTune call reaches this point, even if a CONFIRMED un-key (e.g. the
        // operator's own SetPttLockAsync(false)) happened while this call's own StopPlayback drain was
        // still in flight -- a permanent false "still keyed" belief on a rig that is demonstrably off,
        // making DisposeAsync's own backstop always fire a redundant un-key.
        var stopPlaybackGate = new TaskCompletionSource();
        var (service, _, radio, _) = CreateService(
            wrapEngine: inner => new GatedStopPlaybackAudioEngine(inner, stopPlaybackGate.Task),
            playbackStopWaitBudget: TimeSpan.FromSeconds(30));

        var tune = service.TuneAsync(1750, TimeSpan.FromMilliseconds(1), leaveKeyedAfterTune: true);
        await WaitForAsync(() => radio.PttCalls.Count == 1, TimeSpan.FromSeconds(5));

        // While the tune's own cleanup drain is still in flight, a genuinely independent un-key
        // confirms the rig off (SetPttLockAsync always issues its own command regardless of current
        // belief -- this is the exact shape TxControlsPaneViewModel's own emergency-stop path uses).
        await service.SetPttLockAsync(false);
        Assert.Equal([true, false], radio.PttCalls);

        stopPlaybackGate.SetResult();
        await tune;

        // THE property: no redundant backstop un-key -- the rig is already confirmed off, so
        // DisposeAsync must find nothing left to do.
        await service.DisposeAsync();
        Assert.Equal([true, false], radio.PttCalls);
    }

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
    public async Task Round27_PlayWithPttAsync_ReKeyFailsAfterConfirmedUnkeyDuringDrain_CleanupStillAttemptsUnkey()
    {
        // Round-27 finding (risk): _pttUnkeyEpoch alone answered "did a confirmed un-key happen since
        // I started", not "is the rig off NOW" -- a key landing AFTER that confirmed un-key but BEFORE
        // this call's own cleanup was invisible to it, so cleanup wrongly skipped its own un-key
        // attempt on a rig that had since been re-keyed. Uses a FAILED re-key (fails after physically
        // keying, the round-7 scenario) specifically because a SUCCESSFUL re-key would already be
        // caught by pttLockedAtCleanup's own round-25 mechanism (_pttLocked would read true) -- this
        // scenario needs _pttLocked to stay false (the command never confirmed success) so ONLY the
        // _pttUnkeyEpoch/_pttKeyEpoch cross-check can catch it.
        var stopPlaybackGate = new TaskCompletionSource();
        var (service, _, radio, _) = CreateService(
            wrapEngine: inner => new GatedStopPlaybackAudioEngine(inner, stopPlaybackGate.Task),
            playbackStopWaitBudget: TimeSpan.FromSeconds(30));

        var transmit = service.TransmitAsync(TestMode, TestImage);
        await WaitForAsync(() => radio.PttCalls.Count == 1, TimeSpan.FromSeconds(5));

        // Confirmed un-key (the operator's own emergency unlock) during the drain.
        await service.SetPttLockAsync(false);
        Assert.Equal([true, false], radio.PttCalls);

        // A re-key that fails AFTER physically keying -- _pttLeftKeyedByCall latches true and
        // _pttKeyEpoch bumps, but _pttLocked stays false (the command never confirmed success).
        radio.BeforeSetPtt = tx =>
        {
            if (tx)
            {
                throw new TimeoutException("simulated: re-key command failed after physically keying");
            }
        };
        await Assert.ThrowsAsync<TimeoutException>(() => service.SetPttLockAsync(true));
        Assert.False(service.IsPttLocked);

        // Clear the hook so the transmit's own cleanup un-key (tx=false) can succeed normally.
        radio.BeforeSetPtt = null;

        stopPlaybackGate.SetResult();
        await transmit;

        // THE property: the transmit's own cleanup must still attempt its own un-key -- the rig was
        // re-keyed after the confirmed un-key, so the "already known to be off" signal must not be
        // trusted.
        Assert.Equal([true, false, false], radio.PttCalls);
    }

    [Fact]
    public async Task Round27_PlayWithPttAsync_LockEngagedThenDeviceResolutionFails_AbnormalTerminationStillLatchesCritical()
    {
        // Round-27 finding (risk): pttKeyedOnRealRig's own round-25 baseline read _pttLocked via
        // pttLockedBeforeKeyDecision, which is deliberately read LATE -- AFTER the three bounded device/settings
        // awaits. A failure in any of those three awaits (device not found is the realistic one here)
        // reached the generic catch with pttKeyedOnRealRig still false even when a lock was ALREADY
        // engaged at this call's own true entry, reproducing verbatim the harm round 25 exists to
        // prevent.
        var emptyOutputDevices = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [8000])],
            OutputDevices = [],
        };
        var (service, _, radio, logger) = CreateService(deviceEnumerator: emptyOutputDevices);

        // Lock successfully on a real rig BEFORE the transmit even starts.
        await service.SetPttLockAsync(true);
        Assert.Equal([true], radio.PttCalls);

        // The transmit's own cleanup un-key attempt (tx=false) then ALSO fails -- needed to observe
        // the classification difference at all: UnkeyForCleanupAsync always attempts the real command
        // regardless of pttKeyedOnRealRig's own value, so a SUCCEEDING un-key looks identical either
        // way. Only a FAILING one exposes whether it was classified Critical (genuinely keyed) or
        // Debug (benign no-radio skip).
        radio.BeforeSetPtt = tx =>
        {
            if (!tx)
            {
                throw new TimeoutException("simulated: cleanup un-key also failed");
            }
        };

        // THE property: the transmit aborts on device resolution (before PTT is ever touched by this
        // call), but the ALREADY-keyed rig must still be reported at Critical, not silently downgraded
        // to a benign no-radio Debug line.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransmitAsync(TestMode, TestImage));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("MAY STILL BE KEYED", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ round 28 findings

    [Fact]
    public async Task Round28_PlayWithPttAsync_ConfirmedUnkeyBeforeOwnKeyCommand_CleanupStillAttemptsUnkey()
    {
        // Round-28 finding (blocker): unkeyEpochAtEntry used to be snapshotted at METHOD ENTRY --
        // before this call's own key command -- so a confirmed un-key that happened BEFORE this call
        // keyed the rig was misread, at cleanup time, as "confirmed AFTER I keyed", and the cleanup
        // un-key was wrongly skipped. The two-epoch check (round 27) only proves "nobody re-keyed since
        // MY key phase" -- it says nothing about whether the confirmed un-key it paired with actually
        // postdates that key, unless both epochs are read at the SAME point.
        var keyGate = new TaskCompletionSource();
        var reachedKeyGate = new TaskCompletionSource();
        var (service, _, radio, _) = CreateService();
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

        // A confirmed un-key completes BEFORE the transmit's own key command ever reaches the wire --
        // says nothing about whether THIS call's own upcoming key stays on.
        await service.SetPttLockAsync(false);
        Assert.Equal([false], radio.PttCalls);

        // Release the transmit's own key command -- it now reaches the wire AFTER the confirmed
        // un-key.
        keyGate.SetResult();
        await transmit;

        // THE property: the transmit's own cleanup must still attempt its own un-key.
        Assert.Equal([false, true, false], radio.PttCalls);
    }

    // ------------------------------------------------------------------ round 29 findings

    [Fact]
    public async Task Round29_SetPttLockAsync_AbandonedCommandLaterFails_ObservedNotSilentlyLost()
    {
        // Round-29 finding (risk): SetPttLockAsync's own pttCommand (its key/un-key command) had no
        // fault-observer when WaitAsync gave up on it -- unlike every OTHER abandoned task in this
        // class (StopReceivingAsync, StopPlaybackWithWatchdogAsync, ResumeReceivingBoundedAsync,
        // TryUnkeyPttAsync's own round-26 fix). Round 18's own design deliberately gives the UNLOCK
        // direction's command CancellationToken.None so it "stays queued and eventually reaches the
        // rig" -- but a late failure behind that wedged command, on the one escape-hatch path this
        // chunk exists to keep observable, was silently lost.
        var gate = new TaskCompletionSource();
        var (service, _, radio, logger) = CreateService(cleanupTimeout: TimeSpan.FromMilliseconds(50));
        radio.Gate = gate.Task;
        radio.GateOnCallNumber = 1;
        radio.BeforeSetPtt = tx =>
        {
            if (tx)
            {
                throw new TimeoutException("simulated: PTT command eventually failed after the watchdog gave up");
            }
        };

        // SetPttLockAsync's own command parks on the gate -- its own WaitAsync gives up after
        // cleanupTimeout (50ms), well before the gate is ever released, leaving the command abandoned
        // but still running.
        await Assert.ThrowsAsync<TimeoutException>(() => service.SetPttLockAsync(true));

        // Release the gate -- the abandoned command now runs to completion, and (via BeforeSetPtt)
        // fails.
        gate.SetResult();

        // THE property: that late failure must be observed and logged, not silently lost as an
        // unobserved task exception.
        await WaitForAsync(
            () => logger.Entries.Any(e => e.Message.Contains("PTT command (finished after watchdog)", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));
    }

    // ------------------------------------------------------------------ round 30 findings

    [Fact]
    public async Task Round30_SetPttLockAsync_KeyCommandFailsDuringRecoveryUnkey_ObservedNotSilentlyLost()
    {
        // Round-30 finding (risk): the fault-observer round 29 added for SetPttLockAsync's own
        // pttCommand used to be attached AFTER the recovery un-key's own await (up to
        // _cleanupTimeout) -- but both shipped backends serialize on a single approximately-FIFO
        // request gate, so the recovery un-key cannot make progress until the abandoned key command
        // clears that SAME gate. That makes "the key command completes during the recovery await"
        // the EXPECTED ordering whenever the recovery does anything at all, not an unlucky race -- so
        // the old placement's own IsCompleted check almost always found the command already
        // completed and never attached. Uses two INDEPENDENT gates (round-30 test hook
        // FakeRadioSessionService.Gate2/GateOnCallNumber2) rather than relying on real scheduling
        // order between the two calls -- an earlier version of this test shared one gate between both
        // calls and passed even against the un-fixed code across multiple runs (the two calls'
        // completion order after a simultaneous release is not actually guaranteed), caught by
        // mutation-testing itself before this version was written.
        var keyGate = new TaskCompletionSource();
        var recoveryGate = new TaskCompletionSource();
        var reachedRecoveryGate = new TaskCompletionSource();
        var (service, _, radio, logger) = CreateService(cleanupTimeout: TimeSpan.FromMilliseconds(50));

        radio.Gate = keyGate.Task;
        radio.GateOnCallNumber = 1; // the key command
        radio.Gate2 = recoveryGate.Task;
        radio.GateOnCallNumber2 = 2; // the recovery un-key it triggers
        radio.OnCallStarted = callNumber =>
        {
            if (callNumber == 2)
            {
                reachedRecoveryGate.TrySetResult();
            }
        };
        radio.BeforeSetPtt = tx =>
        {
            if (tx)
            {
                throw new TimeoutException("simulated: key command eventually failed after the watchdog gave up");
            }
        };

        // The key command parks on its own gate; WaitAsync gives up after cleanupTimeout (50ms),
        // well before that gate is ever released, entering the catch's own recovery path (the first
        // engage-direction call ever made on this service, so _keyedTransmitCount reads exactly 1
        // and the recovery un-key fires).
        var lockCall = service.SetPttLockAsync(true);

        // Deterministic happens-before point: the recovery's own un-key call has genuinely started
        // and is now parked on its own SEPARATE gate -- confirms we are past the catch's own initial
        // synchronous section (state latch, Critical log, and -- if fixed -- the fault-observer
        // attachment), with the key command's own gate still fully unresolved.
        await reachedRecoveryGate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Release the key command's own gate -- it now fails (via BeforeSetPtt). Give its trivial,
        // no-further-await continuation time to genuinely complete BEFORE the recovery is allowed to
        // proceed, so the recovery's own await -- and any check made only after it -- sees the key
        // command already completed.
        keyGate.SetResult();
        await Task.Delay(200);

        // Release the recovery's own gate -- it now succeeds.
        recoveryGate.SetResult();

        // THE property: the key command's own late failure must be observed and logged, not
        // silently lost as an unobserved task exception.
        await WaitForAsync(
            () => logger.Entries.Any(e => e.Message.Contains("PTT command (finished after watchdog)", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<TimeoutException>(() => lockCall);
    }

    // ------------------------------------------------------------------ round 31 findings

    [Fact]
    public async Task Round31_PlayWithPttAsync_AbandonedKeyCommandLaterFails_ObservedNotSilentlyLost()
    {
        // Round-31 finding (risk): PlayWithPttAsync's own PTT key command had no fault-observer --
        // the direct twin of SetPttLockAsync's own pttCommand (rounds 29/30), but strictly more
        // reachable (this method has live production callers -- TransmitAsync/TuneAsync --
        // SetPttLockAsync currently has none) and the most safety-relevant instance in the whole
        // class: this is the one abandoned command that, by this method's own reasoning, may have
        // physically keyed the rig.
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

    // Round-31 finding (nit, not given a dedicated test): SetPttLockAsync's own _keyedTransmitCount
    // decrement was reordered to run BEFORE _pttLockGate.Release() rather than after (closing an
    // instruction-scale window where a new SetPttLockAsync(true) call could acquire the gate and
    // increment the count before the previous call's own decrement, making round-12's own recovery
    // guard read a spurious >1 and skip its recovery un-key). Same accepted-residual CLASS
    // _pttKeyEpoch's own field doc already documents -- deterministically forcing a second
    // SetPttLockAsync call to observe the OLD ordering's exact instruction-scale window would need
    // test-only hooks well beyond what a pure reordering fix like this one justifies (the fix cannot
    // introduce a NEW single-threaded bug -- it only changes when two independent Interlocked writes
    // become visible relative to the gate release -- so it is verified by inspection plus the
    // existing suite continuing to pass, not by a new race-dependent test).

    // ------------------------------------------------------------------ Tier B Area 3 findings

    [Fact]
    public async Task TierBAuditFinding_ConcurrentStartAndStopReceiving_StopNeverSilentlyNoOpsWhileStartIsMidFlight()
    {
        // Tier B audit finding (Area 3): StartReceivingAsync/StopReceivingAsync's own `_isReceiving`
        // check-then-act had no gate between them, unlike SetPttLockAsync's own identical shape
        // (_pttLockGate). A real UI repro: RadioStatusViewModel.SetReceivingSafeAsync is fire-and-
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

    /// <summary>Round-25 test double: like <see cref="GatedStopPlaybackAudioEngine"/>, but also signals
    /// <paramref name="reached"/> the INSTANT <see cref="StopPlaybackAsync"/> is entered, before parking
    /// on <paramref name="gate"/> -- needed because the fake engine has no real-time pacing (unlike a
    /// real device), so a test cannot reliably tell "the transmit's own cleanup has genuinely reached
    /// StopPlaybackWithWatchdogAsync" from external polling alone (e.g. watching the key command land) --
    /// the whole encode/pump/finally-entry sequence can complete near-instantly. This gives a test a
    /// deterministic happens-before point to race a concurrent SetPttLockAsync call against.</summary>
    private sealed class SignalingGatedStopPlaybackAudioEngine(IAudioEngine inner, TaskCompletionSource reached, Task gate) : IAudioEngine
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
            reached.TrySetResult();
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
